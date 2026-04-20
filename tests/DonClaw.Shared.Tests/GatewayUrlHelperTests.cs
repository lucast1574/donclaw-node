using DonClaw.Shared;
using Xunit;

namespace DonClaw.Shared.Tests;

/// <summary>
/// Protect the URL-normalization code from silent regressions: it's small but
/// every downstream behaviour (per-gateway token lookup, stored settings,
/// reconnect loop) assumes these shapes stay stable.
/// </summary>
public class GatewayUrlHelperTests
{
    [Theory]
    [InlineData("192.168.1.1:18789", "ws://192.168.1.1:18789/")]
    [InlineData("ws://host:18789", "ws://host:18789/")]
    [InlineData("wss://host", "wss://host/")]
    [InlineData("http://host:8080", "ws://host:8080/")]
    [InlineData("https://host", "wss://host/")]
    public void NormalizeForWebSocket_Cases(string input, string expected)
    {
        Assert.Equal(expected, GatewayUrlHelper.NormalizeForWebSocket(input));
    }

    [Fact]
    public void SanitizeForDisplay_StripsCredentials()
    {
        var result = GatewayUrlHelper.SanitizeForDisplay("wss://user:pass@host/path");
        Assert.DoesNotContain("pass", result);
        Assert.DoesNotContain("user:", result);
    }
}
