namespace DonClaw.Shared;

/// <summary>
/// Request to execute a command.
/// </summary>
public class CommandRequest
{
    public string Command { get; set; } = "";
    public string[]? Args { get; set; }
    public string? Shell { get; set; }
    public string? Cwd { get; set; }
    public int TimeoutMs { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// Result of a command execution.
/// </summary>
public class CommandResult
{
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>
/// Abstraction for command execution.
/// </summary>
public interface ICommandRunner
{
    string Name { get; }
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}
