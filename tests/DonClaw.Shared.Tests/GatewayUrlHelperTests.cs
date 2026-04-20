using DonClaw.Shared;
using Xunit;

namespace DonClaw.Shared.Tests;

/// <summary>
/// Protect the URL-normalization code from silent regressions: small but every
/// downstream behaviour (per-gateway token lookup, stored settings, reconnect
/// loop) assumes these shapes stay stable.
///
/// <para>The expected values here come from running against actual known-good
/// inputs we've seen in prod — they're not a spec of what the normalizer
/// <em>should</em> do, they're a frozen snapshot of what it does. If any of
/// these start failing, investigate the downstream impact before "fixing" the
/// test.</para>
/// </summary>
public class GatewayUrlHelperTests
{
    [Theory]
    [InlineData("ws://host:18789")]
    [InlineData("wss://host:443")]
    [InlineData("wss://3270056e.app.donclaw.com/__openclaw__/gateway/ws")]
    public void NormalizeForWebSocket_AlreadyValid_ReturnsEquivalent(string input)
    {
        var result = GatewayUrlHelper.NormalizeForWebSocket(input);
        // Must keep the scheme + host (trailing slashes can vary per input).
        Assert.StartsWith(input.Split('?')[0].TrimEnd('/'), result.TrimEnd('/'));
    }

    [Theory]
    [InlineData("http://host:8080")]
    [InlineData("https://host")]
    public void NormalizeForWebSocket_HttpSchemes_ConvertToWs(string input)
    {
        var result = GatewayUrlHelper.NormalizeForWebSocket(input);
        Assert.True(
            result.StartsWith("ws://") || result.StartsWith("wss://"),
            $"Expected ws:// or wss:// after normalize, got '{result}'");
    }

    [Fact]
    public void NormalizeForWebSocket_EmptyInput_DoesNotThrow()
    {
        Assert.Equal(string.Empty, GatewayUrlHelper.NormalizeForWebSocket(""));
        Assert.Equal(string.Empty, GatewayUrlHelper.NormalizeForWebSocket(null));
    }

    [Fact]
    public void SanitizeForDisplay_StripsCredentials()
    {
        var result = GatewayUrlHelper.SanitizeForDisplay("wss://user:pass@host/path");
        Assert.DoesNotContain("pass", result);
    }
}
