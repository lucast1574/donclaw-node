using System.IO;
using System.Text.Json;
using DonClaw.Shared;
using Xunit;

namespace DonClaw.Shared.Tests;

/// <summary>
/// Tests for per-gateway device token storage. The migration path (legacy
/// single-DeviceToken → TokensByGateway map) is important because a bad
/// upgrade would lose the user's pairing across gateways silently.
/// </summary>
public class DeviceIdentityTests : IDisposable
{
    private readonly string _tmpDir;

    public DeviceIdentityTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"donclaw-id-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void GenerateNew_CreatesKeyFileAndStableDeviceId()
    {
        var id = new DeviceIdentity(_tmpDir);
        id.Initialize();

        Assert.True(File.Exists(Path.Combine(_tmpDir, "device-key-ed25519.json")));
        var deviceId = id.DeviceId;
        Assert.False(string.IsNullOrWhiteSpace(deviceId));
        // SHA-256 hex -> 64 lowercase hex chars
        Assert.Equal(64, deviceId.Length);
    }

    [Fact]
    public void PerGatewayTokens_AreIsolated()
    {
        var id = new DeviceIdentity(_tmpDir);
        id.Initialize();

        // No tokens stored yet
        Assert.Null(id.GetDeviceTokenFor("ws://gateway-a/"));
        Assert.Null(id.GetDeviceTokenFor("ws://gateway-b/"));

        id.StoreDeviceTokenFor("ws://gateway-a/", "token-A");
        id.StoreDeviceTokenFor("ws://gateway-b/", "token-B");

        Assert.Equal("token-A", id.GetDeviceTokenFor("ws://gateway-a/"));
        Assert.Equal("token-B", id.GetDeviceTokenFor("ws://gateway-b/"));

        // Casing / trailing-slash normalization
        Assert.Equal("token-A", id.GetDeviceTokenFor("WS://GATEWAY-A"));
    }

    [Fact]
    public void ClearDeviceTokenFor_OnlyClearsTargetedGateway()
    {
        var id = new DeviceIdentity(_tmpDir);
        id.Initialize();
        id.StoreDeviceTokenFor("ws://a", "A");
        id.StoreDeviceTokenFor("ws://b", "B");

        id.ClearDeviceTokenFor("ws://a");

        Assert.Null(id.GetDeviceTokenFor("ws://a"));
        Assert.Equal("B", id.GetDeviceTokenFor("ws://b"));
    }

    [Fact]
    public void ReloadingIdentity_PreservesStoredTokens()
    {
        var first = new DeviceIdentity(_tmpDir);
        first.Initialize();
        first.StoreDeviceTokenFor("ws://gw", "persisted-token");
        var originalId = first.DeviceId;

        // Simulate a process restart: new instance reads the same file.
        var second = new DeviceIdentity(_tmpDir);
        second.Initialize();

        Assert.Equal(originalId, second.DeviceId);
        Assert.Equal("persisted-token", second.GetDeviceTokenFor("ws://gw"));
    }

    [Fact]
    public void LegacyDeviceTokenField_DoesNotBreakLoad()
    {
        // Simulate a v8.3.x file that only has the old scalar DeviceToken.
        // The loader should not crash; per-gateway map should just start empty.
        var keyPath = Path.Combine(_tmpDir, "device-key-ed25519.json");
        var legacy = new
        {
            privateKeyBase64 = "UG/oABsOj9fUAw+TmW6H8tDo3nLg0YzmnCvbE1vWMZI=", // fake but matching shape
            publicKeyBase64 = "not-real",
            deviceId = "legacydevice0000000000000000000000000000000000000000000000000000",
            deviceToken = "LEGACY-TOKEN",
            algorithm = "Ed25519",
            createdAt = 1_700_000_000_000L,
        };
        File.WriteAllText(keyPath, JsonSerializer.Serialize(legacy));

        // Initialize will regenerate because the privateKeyBase64 isn't a real
        // Ed25519 key, but that's fine: we just want to confirm parsing the
        // legacy shape doesn't throw. The regenerated identity starts fresh.
        var id = new DeviceIdentity(_tmpDir);
        id.Initialize();

        Assert.NotNull(id.DeviceId);
    }
}
