using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace DonClaw.Shared;

/// <summary>
/// Manages device identity (keypair) for node authentication using Ed25519.
///
/// <para>
/// Device tokens are stored <b>per gateway</b>. Each WebSocket gateway issues its
/// own opaque device token once the pairing is approved, and those tokens are
/// not interchangeable: presenting gateway A's token to gateway B produces a
/// "too many failed authentication attempts" PolicyViolation, which then
/// throttles the IP for a while. Before this became a per-gateway map the Node
/// would clobber the previous gateway's token on every new pairing, which
/// caused a bricked-feeling infinite reconnect loop when users toggled between
/// their LAN gateway and any DonClaw cloud instance.
/// </para>
///
/// <para>
/// The private key (and therefore the DeviceId) is still global — identity is
/// "this is the same machine", but each gateway gets its own authorization.
/// </para>
/// </summary>
public class DeviceIdentity
{
    private readonly string _keyPath;
    private readonly IOpenClawLogger _logger;
    private Key? _privateKey;
    private PublicKey? _publicKey;
    private string? _deviceId;

    /// <summary>Per-gateway device tokens: key = normalized gateway host, value = opaque token.</summary>
    private readonly Dictionary<string, string> _tokensByGateway = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy single-token field (pre per-gateway split). Kept only for backwards-compat reads.</summary>
    private string? _legacyDeviceToken;

    private static readonly SignatureAlgorithm Ed25519Algorithm = SignatureAlgorithm.Ed25519;

    public string DeviceId => _deviceId ?? throw new InvalidOperationException("Device not initialized");
    public string PublicKeyBase64Url => _publicKey != null
        ? Base64UrlEncode(_publicKey.Export(KeyBlobFormat.RawPublicKey))
        : throw new InvalidOperationException("Device not initialized");

    public DeviceIdentity(string dataPath, IOpenClawLogger? logger = null)
    {
        _keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Get the stored device token for a specific gateway host, or null if the
    /// Node has never successfully paired with that gateway.
    /// </summary>
    public string? GetDeviceTokenFor(string? gatewayHost)
    {
        var key = NormalizeGateway(gatewayHost);
        if (key == null) return null;
        return _tokensByGateway.TryGetValue(key, out var t) ? t : null;
    }

    /// <summary>
    /// Initialize the device identity - loads existing or generates new keypair.
    /// </summary>
    public void Initialize()
    {
        if (File.Exists(_keyPath))
            LoadExisting();
        else
            GenerateNew();
    }

    private void LoadExisting()
    {
        try
        {
            var json = File.ReadAllText(_keyPath);
            var data = JsonSerializer.Deserialize<DeviceKeyData>(json);

            if (data == null || string.IsNullOrEmpty(data.PrivateKeyBase64))
            {
                _logger.Warn("Invalid device key file, generating new");
                GenerateNew();
                return;
            }

            var privateKeyBytes = Convert.FromBase64String(data.PrivateKeyBase64);
            _privateKey = Key.Import(Ed25519Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
            _publicKey = _privateKey.PublicKey;
            _deviceId = data.DeviceId;

            // Legacy scalar (older builds stored a single token). Keep it around
            // so existing pairings aren't instantly lost after upgrade — but don't
            // actively use it: the gateway that issued it will re-issue a fresh
            // one under the new map the next time the user connects there.
            _legacyDeviceToken = data.DeviceToken;

            _tokensByGateway.Clear();
            if (data.TokensByGateway != null)
            {
                foreach (var kv in data.TokensByGateway)
                {
                    var k = NormalizeGateway(kv.Key);
                    if (k != null && !string.IsNullOrEmpty(kv.Value))
                        _tokensByGateway[k] = kv.Value;
                }
            }

            _logger.Info($"Loaded Ed25519 device identity: {_deviceId?.Substring(0, 16)}... " +
                         $"({_tokensByGateway.Count} gateway token(s) known)");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to load device key: {ex.Message}");
            GenerateNew();
        }
    }

    private void GenerateNew()
    {
        _logger.Info("Generating new Ed25519 device keypair...");

        _privateKey = Key.Create(Ed25519Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _publicKey = _privateKey.PublicKey;

        var publicKeyBytes = _publicKey.Export(KeyBlobFormat.RawPublicKey);

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(publicKeyBytes);
        _deviceId = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var privateKeyBytes = _privateKey.Export(KeyBlobFormat.RawPrivateKey);

        var data = new DeviceKeyData
        {
            PrivateKeyBase64 = Convert.ToBase64String(privateKeyBytes),
            PublicKeyBase64 = Convert.ToBase64String(publicKeyBytes),
            DeviceId = _deviceId,
            Algorithm = "Ed25519",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TokensByGateway = new(),
        };

        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        _logger.Info($"Generated new Ed25519 device identity: {_deviceId}");
    }

    /// <summary>
    /// Sign a payload for device authentication.
    /// Format: v2|{deviceId}|{client.id}|{client.mode}|{role}|{scopes}|{signedAtMs}|{token}|{nonce}
    /// </summary>
    public string SignPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_privateKey == null || _deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        var payload = BuildDebugPayload(nonce, signedAtMs, clientId, authToken);
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Ed25519Algorithm.Sign(_privateKey, dataBytes);
        return Base64UrlEncode(signature);
    }

    /// <summary>
    /// Build the payload string (for debugging).
    /// </summary>
    public string BuildDebugPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        return $"v2|{_deviceId}|{clientId}|node|node||{signedAtMs}|{authToken}|{nonce}";
    }

    /// <summary>
    /// Persist the device token received from a specific gateway after pairing.
    /// </summary>
    public void StoreDeviceTokenFor(string? gatewayHost, string token)
    {
        var key = NormalizeGateway(gatewayHost);
        if (key == null)
        {
            _logger.Warn("StoreDeviceTokenFor called without a gateway host; ignoring.");
            return;
        }

        _tokensByGateway[key] = token;
        PersistTokens();
        _logger.Info($"Device token stored for gateway {key}");
    }

    /// <summary>
    /// Forget the stored device token for a given gateway. Call this when the
    /// gateway rejects the stored token (e.g. the gateway was reset / re-paired
    /// elsewhere), so the next connect falls back to the bootstrap token and
    /// re-issues a fresh one.
    /// </summary>
    public void ClearDeviceTokenFor(string? gatewayHost)
    {
        var key = NormalizeGateway(gatewayHost);
        if (key == null) return;
        if (_tokensByGateway.Remove(key))
        {
            PersistTokens();
            _logger.Info($"Cleared stored device token for gateway {key}");
        }
    }

    private void PersistTokens()
    {
        try
        {
            if (!File.Exists(_keyPath))
            {
                _logger.Warn($"Key file {_keyPath} not found; cannot persist tokens");
                return;
            }

            var json = File.ReadAllText(_keyPath);
            var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
            if (data == null) return;

            data.TokensByGateway = new Dictionary<string, string>(_tokensByGateway, StringComparer.OrdinalIgnoreCase);
            // Clear legacy scalar once we've migrated — next load will use the map.
            data.DeviceToken = null;
            _legacyDeviceToken = null;

            File.WriteAllText(_keyPath,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to persist device tokens: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalize a WebSocket URL to a stable dictionary key. Drops trailing slashes,
    /// normalizes casing, keeps scheme+host+port+path so different instances under
    /// the same domain (e.g. /__openclaw__/gateway/ws) get separate buckets.
    /// </summary>
    private static string? NormalizeGateway(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var trimmed = host.Trim();
        // Strip trailing slashes
        while (trimmed.EndsWith('/')) trimmed = trimmed[..^1];
        return trimmed.ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private class DeviceKeyData
    {
        public string? PrivateKeyBase64 { get; set; }
        public string? PublicKeyBase64 { get; set; }
        public string? DeviceId { get; set; }

        /// <summary>
        /// Legacy field: pre-split (v8.3.x and earlier) stored a single device
        /// token here. Retained for backwards-compatible reads; write path now
        /// uses <see cref="TokensByGateway"/> and sets this to null.
        /// </summary>
        public string? DeviceToken { get; set; }

        /// <summary>Per-gateway tokens (key = normalized ws URL).</summary>
        public Dictionary<string, string>? TokensByGateway { get; set; }

        public string? Algorithm { get; set; }
        public long CreatedAt { get; set; }
    }
}
