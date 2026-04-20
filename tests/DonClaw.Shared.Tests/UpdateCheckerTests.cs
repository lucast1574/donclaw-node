using DonClaw.Shared;
using Xunit;

namespace DonClaw.Shared.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("8.3.2", "8.3.1", true)]   // patch bump
    [InlineData("8.4.0", "8.3.9", true)]   // minor bump
    [InlineData("9.0.0", "8.99.99", true)] // major bump
    [InlineData("8.3.1", "8.3.1", false)]  // identical
    [InlineData("8.3.0", "8.3.1", false)]  // older candidate
    [InlineData("8.3.1", "8.4.0", false)]  // older (candidate lags minor)
    [InlineData("v8.4.0", "8.3.1", true)]  // leading v tolerated
    [InlineData("8.4.0", "v8.3.1", true)]  // v in current too
    [InlineData("8.4.0-rc1", "8.3.9", true)]  // pre-release suffix ignored
    [InlineData("8.3.1-rc1", "8.3.1", false)] // pre-release of same version NOT newer
    [InlineData("", "8.3.1", false)]       // empty candidate
    [InlineData("8.3.1", "", false)]       // empty current
    public void IsNewer_SemverComparisons(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(candidate, current));
    }

    [Fact]
    public void IsNewer_HandlesUnparseableAsStringCompare()
    {
        // Neither side parses as semver -> falls back to string compare.
        // "z" > "a" ordinally, so this should be "newer".
        Assert.True(UpdateChecker.IsNewer("zulu", "alpha"));
        Assert.False(UpdateChecker.IsNewer("alpha", "zulu"));
    }
}
