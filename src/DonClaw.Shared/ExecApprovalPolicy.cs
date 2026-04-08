using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DonClaw.Shared;

/// <summary>
/// A single rule in the exec approval policy.
/// </summary>
public class ExecApprovalRule
{
    public string Pattern { get; set; } = "*";
    public ExecApprovalAction Action { get; set; } = ExecApprovalAction.Deny;
    public string[]? Shells { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}

public enum ExecApprovalAction
{
    Allow,
    Deny,
    Prompt
}

public class ExecApprovalResult
{
    public bool Allowed { get; set; }
    public ExecApprovalAction Action { get; set; }
    public string? MatchedPattern { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Manages execution approval rules for system.run commands.
/// </summary>
public class ExecApprovalPolicy
{
    private readonly IOpenClawLogger _logger;
    private readonly string _policyFilePath;
    private List<ExecApprovalRule> _rules = new();
    private ExecApprovalAction _defaultAction = ExecApprovalAction.Deny;

    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);

    public IReadOnlyList<ExecApprovalRule> Rules => _rules.AsReadOnly();

    public ExecApprovalAction DefaultAction
    {
        get => _defaultAction;
        set => _defaultAction = value;
    }

    public ExecApprovalPolicy(string dataPath, IOpenClawLogger logger)
    {
        _logger = logger;
        _policyFilePath = Path.Combine(dataPath, "exec-policy.json");
        Load();
    }

    public ExecApprovalResult Evaluate(string command, string? shell = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ExecApprovalResult
            {
                Allowed = false,
                Action = ExecApprovalAction.Deny,
                Reason = "Empty command"
            };
        }

        var normalizedShell = (shell ?? "powershell").ToLowerInvariant();

        foreach (var rule in _rules)
        {
            if (!rule.Enabled) continue;

            if (rule.Shells is { Length: > 0 })
            {
                if (!rule.Shells.Any(s => s.Equals(normalizedShell, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            if (MatchesPattern(command, rule.Pattern))
            {
                var allowed = rule.Action == ExecApprovalAction.Allow;
                _logger.Info($"[EXEC-POLICY] {(allowed ? "ALLOW" : "DENY")}: '{command}' matched rule '{rule.Pattern}'");
                return new ExecApprovalResult
                {
                    Allowed = allowed,
                    Action = rule.Action,
                    MatchedPattern = rule.Pattern,
                    Reason = rule.Description ?? $"Matched rule: {rule.Pattern}"
                };
            }
        }

        var defaultAllowed = _defaultAction == ExecApprovalAction.Allow;
        _logger.Info($"[EXEC-POLICY] DEFAULT {(_defaultAction)}: '{command}' (no rule matched)");
        return new ExecApprovalResult
        {
            Allowed = defaultAllowed,
            Action = _defaultAction,
            Reason = "No matching rule; default policy applied"
        };
    }

    public void AddRule(ExecApprovalRule rule)
    {
        _rules.Add(rule);
        Save();
    }

    public void SetRules(IEnumerable<ExecApprovalRule> rules, ExecApprovalAction? defaultAction = null)
    {
        _rules = new List<ExecApprovalRule>(rules);
        if (defaultAction.HasValue) _defaultAction = defaultAction.Value;
        Save();
    }

    public ExecPolicyData GetPolicyData()
    {
        return new ExecPolicyData
        {
            DefaultAction = _defaultAction,
            Rules = _rules.ToList()
        };
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_policyFilePath))
            {
                var json = File.ReadAllText(_policyFilePath);
                var data = JsonSerializer.Deserialize<ExecPolicyData>(json, _jsonOptions);
                if (data != null)
                {
                    _rules = data.Rules ?? new List<ExecApprovalRule>();
                    _defaultAction = data.DefaultAction;
                    _logger.Info($"[EXEC-POLICY] Loaded {_rules.Count} rules from {_policyFilePath}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-POLICY] Failed to load policy: {ex.Message}");
        }

        _rules = CreateDefaultRules();
        _defaultAction = ExecApprovalAction.Deny;
        _logger.Info("[EXEC-POLICY] Using default policy");
        Save();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_policyFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new ExecPolicyData
            {
                DefaultAction = _defaultAction,
                Rules = _rules
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_policyFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC-POLICY] Failed to save: {ex.Message}");
        }
    }

    private static List<ExecApprovalRule> CreateDefaultRules()
    {
        return new List<ExecApprovalRule>
        {
            new() { Pattern = "echo *", Action = ExecApprovalAction.Allow, Description = "Echo commands" },
            new() { Pattern = "Get-*", Action = ExecApprovalAction.Allow, Shells = new[] { "powershell", "pwsh" }, Description = "PowerShell Get- cmdlets (read-only)" },
            new() { Pattern = "dir *", Action = ExecApprovalAction.Allow, Description = "Directory listing" },
            new() { Pattern = "hostname", Action = ExecApprovalAction.Allow, Description = "Hostname query" },
            new() { Pattern = "whoami", Action = ExecApprovalAction.Allow, Description = "Current user" },
            new() { Pattern = "systeminfo", Action = ExecApprovalAction.Allow, Description = "System info" },
            new() { Pattern = "ipconfig *", Action = ExecApprovalAction.Allow, Description = "Network config" },
            new() { Pattern = "ping *", Action = ExecApprovalAction.Allow, Description = "Ping" },
            new() { Pattern = "type *", Action = ExecApprovalAction.Allow, Shells = new[] { "cmd" }, Description = "Read file (cmd)" },
            new() { Pattern = "cat *", Action = ExecApprovalAction.Allow, Description = "Read file" },
            new() { Pattern = "Remove-Item *", Action = ExecApprovalAction.Deny, Description = "Block file deletion" },
            new() { Pattern = "rm *", Action = ExecApprovalAction.Deny, Description = "Block rm" },
            new() { Pattern = "del *", Action = ExecApprovalAction.Deny, Description = "Block del" },
            new() { Pattern = "Format-*", Action = ExecApprovalAction.Deny, Description = "Block format commands" },
            new() { Pattern = "Stop-Computer*", Action = ExecApprovalAction.Deny, Description = "Block shutdown" },
            new() { Pattern = "Restart-Computer*", Action = ExecApprovalAction.Deny, Description = "Block restart" },
            new() { Pattern = "*Invoke-WebRequest*", Action = ExecApprovalAction.Deny, Description = "Block web downloads" },
            new() { Pattern = "*Start-Process*", Action = ExecApprovalAction.Deny, Description = "Block process launch" },
            new() { Pattern = "*reg *", Action = ExecApprovalAction.Deny, Description = "Block registry edits" },
            new() { Pattern = "shutdown*", Action = ExecApprovalAction.Deny, Description = "Block shutdown" },
            new() { Pattern = "net *", Action = ExecApprovalAction.Deny, Description = "Block net commands" },
        };
    }

    internal bool MatchesPattern(string command, string pattern)
    {
        if (pattern == "*") return true;

        var regex = _regexCache.GetOrAdd(pattern, static p =>
        {
            var regexPattern = "^" + Regex.Escape(p)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        });

        try { return regex.IsMatch(command); }
        catch (RegexMatchTimeoutException)
        {
            _logger.Warn($"[EXEC-POLICY] Pattern match timed out for '{pattern}'; denying as safe default");
            return false;
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public class ExecPolicyData
{
    public ExecApprovalAction DefaultAction { get; set; } = ExecApprovalAction.Deny;
    public List<ExecApprovalRule> Rules { get; set; } = new();
}
