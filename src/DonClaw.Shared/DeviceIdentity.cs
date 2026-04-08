using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace DonClaw.Shared;

/// <summary>
/// Manages device identity (keypair) for node authentication using Ed25519
/// </summary>
public class DeviceIdentity
{
    private readonly string _keyPath;
    private readonly IOpenClawLogger _logger;
    private Key? _privateKey;
    private PublicKey? _publicKey;
    private string? _deviceId;
    private string? _deviceToken;

    private static readonly SignatureAlgorithm Ed25519Algorithm = SignatureAlgorithm.Ed25519;

    public string DeviceId => _deviceId ?? throw new InvalidOperationException("Device not initialized");
    public string PublicKeyBase64Url => _publicKey != null ? Base64UrlEncode(_publicKey.Export(KeyBlobFormat.RawPublicKey)) : throw new InvalidOperationException("Device not initialized");
    public string? DeviceToken => _deviceToken;

    public DeviceIdentity(string dataPath, IOpenClawLogger? logger = null)
    {
        _keyPath = Path.Combine(dataPath, "device-key-ed25519.json");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Initialize the device identity - loads existing or generates new keypair
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
            _deviceToken = data.DeviceToken;

            _logger.Info($"Loaded Ed25519 device identity: {_deviceId?.Substring(0, 16)}...");
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
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        _logger.Info($"Generated new Ed25519 device identity: {_deviceId}");
    }

    /// <summary>
    /// Sign a payload for device authentication
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
    /// Build the payload string (for debugging)
    /// </summary>
    public string BuildDebugPayload(string nonce, long signedAtMs, string clientId, string authToken)
    {
        if (_deviceId == null)
            throw new InvalidOperationException("Device not initialized");

        return $"v2|{_deviceId}|{clientId}|node|node||{signedAtMs}|{authToken}|{nonce}";
    }

    /// <summary>
    /// Store the device token received after pairing approval
    /// </summary>
    public void StoreDeviceToken(string token)
    {
        _deviceToken = token;

        try
        {
            if (File.Exists(_keyPath))
            {
                var json = File.ReadAllText(_keyPath);
                var data = JsonSerializer.Deserialize<DeviceKeyData>(json);
                if (data != null)
                {
                    data.DeviceToken = token;
                    File.WriteAllText(_keyPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                    _logger.Info("Device token stored");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to store device token: {ex.Message}");
        }
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
        public string? DeviceToken { get; set; }
        public string? Algorithm { get; set; }
        public long CreatedAt { get; set; }
    }
}
