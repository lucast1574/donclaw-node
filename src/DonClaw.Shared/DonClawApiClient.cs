using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DonClaw.Shared;

/// <summary>
/// HTTP client for the DonClaw backend API (api.donclaw.com).
/// Handles authentication and instance discovery for the Node tray app.
/// </summary>
public class DonClawApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IOpenClawLogger _logger;
    private string? _jwtToken;

    public const string DefaultApiUrl = "https://api.donclaw.com";
    // Backend responds with camelCase keys (gatewayUrl, gatewayToken, workspaceName,
    // etc.). Previous SnakeCaseLower policy silently deserialized everything to
    // empty strings, which caused the "Connect" flow to fail with "no token".
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool IsAuthenticated => !string.IsNullOrEmpty(_jwtToken);
    public string? JwtToken => _jwtToken;

    public DonClawApiClient(IOpenClawLogger logger, string? apiUrl = null)
    {
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(apiUrl ?? DefaultApiUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Set JWT from a previous session (loaded from settings).</summary>
    public void SetToken(string token)
    {
        _jwtToken = token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    // ==================== AUTH ====================

    /// <summary>Login with email + password. Returns user info on success.</summary>
    public async Task<LoginResult> LoginAsync(string email, string password)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { email, password });
            var response = await _http.PostAsync("/auth/login",
                new StringContent(body, Encoding.UTF8, "application/json"));

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var err = JsonSerializer.Deserialize<ApiError>(json, s_jsonOpts);
                return new LoginResult { Success = false, Error = err?.Message ?? $"HTTP {(int)response.StatusCode}" };
            }

            var result = JsonSerializer.Deserialize<LoginResponse>(json, s_jsonOpts);
            if (result?.AccessToken == null)
                return new LoginResult { Success = false, Error = "No access token returned" };

            SetToken(result.AccessToken);
            return new LoginResult
            {
                Success = true,
                Token = result.AccessToken,
                UserId = result.User?.Id,
                Email = result.User?.Email,
                Role = result.User?.Role,
                FirstName = result.User?.FirstName,
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Login failed: {ex.Message}");
            return new LoginResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>Verify that the current JWT is still valid by hitting /auth/profile.</summary>
    public async Task<bool> VerifyTokenAsync()
    {
        if (!IsAuthenticated) return false;
        try
        {
            var response = await _http.GetAsync("/auth/profile");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ==================== NODE ====================

    /// <summary>Check if the user has an active plan that allows Node usage.</summary>
    public async Task<PlanCheckResult> VerifyNodePlanAsync()
    {
        try
        {
            var response = await _http.GetAsync("/instances/node/verify-plan");
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PlanCheckResult>(json, s_jsonOpts);
            return result ?? new PlanCheckResult { Allowed = false, Reason = "Unknown error" };
        }
        catch (Exception ex)
        {
            return new PlanCheckResult { Allowed = false, Reason = ex.Message };
        }
    }

    /// <summary>Get all instances the user can connect Node to.</summary>
    public async Task<ConnectableResult> GetConnectableInstancesAsync()
    {
        try
        {
            var response = await _http.GetAsync("/instances/node/connectable");
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var err = JsonSerializer.Deserialize<ApiError>(json, s_jsonOpts);
                return new ConnectableResult { Allowed = false, Reason = err?.Message ?? "Request failed" };
            }

            var result = JsonSerializer.Deserialize<ConnectableResult>(json, s_jsonOpts);
            return result ?? new ConnectableResult { Allowed = false, Reason = "Parse error" };
        }
        catch (Exception ex)
        {
            return new ConnectableResult { Allowed = false, Reason = ex.Message };
        }
    }

    /// <summary>Get setup code for a specific instance.</summary>
    public async Task<SetupCodeResult?> GetSetupCodeAsync(string instanceId)
    {
        try
        {
            var response = await _http.GetAsync($"/instances/{instanceId}/setup-code");
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<SetupCodeResult>(json, s_jsonOpts);
        }
        catch (Exception ex)
        {
            _logger.Error($"GetSetupCode failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    // ==================== DTOs ====================

    public class LoginResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? Token { get; set; }
        public string? UserId { get; set; }
        public string? Email { get; set; }
        public string? Role { get; set; }
        public string? FirstName { get; set; }
    }

    public class LoginResponse
    {
        public string? AccessToken { get; set; }
        public bool MfaRequired { get; set; }
        public UserInfo? User { get; set; }
    }

    public class UserInfo
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Role { get; set; }
        public bool EmailVerified { get; set; }
    }

    public class PlanCheckResult
    {
        public bool Allowed { get; set; }
        public string? Reason { get; set; }
    }

    public class ConnectableResult
    {
        public bool Allowed { get; set; }
        public string? Reason { get; set; }
        public List<ConnectableInstance>? Instances { get; set; }
    }

    public class ConnectableInstance
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string WorkspaceName { get; set; } = "";
        public string WorkspaceSlug { get; set; } = "";
        public string Status { get; set; } = "";
        public string Domain { get; set; } = "";
        public string GatewayUrl { get; set; } = "";
        public string GatewayToken { get; set; } = "";
        public string SetupCode { get; set; } = "";
    }

    public class SetupCodeResult
    {
        public string? Error { get; set; }
        public string? InstanceId { get; set; }
        public string? InstanceName { get; set; }
        public string? Domain { get; set; }
        public string? GatewayUrl { get; set; }
        public string? GatewayToken { get; set; }
        public string? SetupCode { get; set; }
    }

    public class ApiError
    {
        public string? Message { get; set; }
        public int StatusCode { get; set; }
    }
}
