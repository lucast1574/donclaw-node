using System.IO;
using DonClaw.Shared;
using Xunit;

namespace DonClaw.Shared.Tests;

/// <summary>
/// Pattern-matcher tests for ExecApprovalPolicy. We construct a fresh policy
/// against a tmp dir so rules don't leak between tests, and exercise the
/// glob-style patterns the default rule set relies on.
/// </summary>
public class ExecApprovalPolicyTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly ExecApprovalPolicy _policy;

    public ExecApprovalPolicyTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"donclaw-exec-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _policy = new ExecApprovalPolicy(_tmpDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("echo hello",     "echo *",     true)]
    [InlineData("ECHO hello",     "echo *",     true)]  // case-insensitive
    [InlineData("echo",           "echo *",     false)] // trailing-space glob expects args
    [InlineData("Get-Process",    "Get-*",      true)]
    [InlineData("Remove-Item foo","Get-*",      false)]
    public void MatchesPattern_BasicGlobs(string command, string pattern, bool expected)
    {
        Assert.Equal(expected, _policy.MatchesPattern(command, pattern));
    }

    [Fact]
    public void WildcardPattern_MatchesEverything()
    {
        Assert.True(_policy.MatchesPattern("anything at all", "*"));
        Assert.True(_policy.MatchesPattern("", "*"));
    }

    [Fact]
    public void Evaluate_EmptyCommand_Denied()
    {
        var result = _policy.Evaluate("");
        Assert.False(result.Allowed);
        Assert.Equal(ExecApprovalAction.Deny, result.Action);
    }

    [Fact]
    public void Evaluate_UsesDefaultActionWhenNoRuleMatches()
    {
        _policy.SetRules(new List<ExecApprovalRule>(), ExecApprovalAction.Allow);
        var result = _policy.Evaluate("some unseen command");
        Assert.True(result.Allowed);
        Assert.Equal(ExecApprovalAction.Allow, result.Action);
    }

    [Fact]
    public void Evaluate_FirstMatchWins()
    {
        // Allow rule comes first so a later Deny rule with a broader pattern
        // must NOT clobber it. This matches the real runtime behavior we rely
        // on in CreateDefaultRules (e.g. "Start-Process *" Allow before any
        // broader "* dangerous *" Deny).
        _policy.SetRules(new List<ExecApprovalRule>
        {
            new() { Pattern = "Start-Process notepad*", Action = ExecApprovalAction.Allow },
            new() { Pattern = "Start-Process *", Action = ExecApprovalAction.Deny },
        }, ExecApprovalAction.Deny);

        Assert.True(_policy.Evaluate("Start-Process notepad").Allowed);
        Assert.False(_policy.Evaluate("Start-Process calc").Allowed);
    }

    [Fact]
    public void Evaluate_ShellSpecificRule_OnlyMatchesMatchingShell()
    {
        _policy.SetRules(new List<ExecApprovalRule>
        {
            new() { Pattern = "start *", Shells = new[] { "cmd" }, Action = ExecApprovalAction.Allow },
        }, ExecApprovalAction.Deny);

        Assert.True(_policy.Evaluate("start notepad", shell: "cmd").Allowed);
        Assert.False(_policy.Evaluate("start notepad", shell: "powershell").Allowed);
    }
}
