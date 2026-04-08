using System.Diagnostics;
using System.Text;

namespace DonClaw.Shared;

/// <summary>
/// Executes commands locally via Process.Start (pwsh.exe / cmd.exe).
/// </summary>
public class LocalCommandRunner : ICommandRunner
{
    private readonly IOpenClawLogger _logger;
    private const int OutputDrainTimeoutMs = 500;

    public string Name => "local";

    public LocalCommandRunner(IOpenClawLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var (fileName, arguments) = BuildProcessArgs(request);

        _logger.Info($"[EXEC] {fileName} {arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrEmpty(request.Cwd))
            psi.WorkingDirectory = request.Cwd;

        if (request.Env != null)
        {
            foreach (var (key, value) in request.Env)
                psi.Environment[key] = value;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var outputLock = new object();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (outputLock) { stdoutBuilder.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (outputLock) { stderrBuilder.AppendLine(e.Data); } } };

        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitTcs.TrySetResult(true);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC] Failed to start process: {ex.Message}");
            return new CommandResult
            {
                Stderr = $"Failed to start: {ex.Message}",
                ExitCode = -1,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (process.HasExited)
            exitTcs.TrySetResult(true);

        var timedOut = false;

        try
        {
            if (request.TimeoutMs > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(request.TimeoutMs);

                try
                {
                    await exitTcs.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    timedOut = true;
                    _logger.Warn($"[EXEC] Process timed out after {request.TimeoutMs}ms");
                    KillProcess(process);
                }
            }
            else
            {
                await exitTcs.Task.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        var drainTask = Task.Run(() =>
        {
            try { process.WaitForExit(); }
            catch { }
        });
        if (await Task.WhenAny(drainTask, Task.Delay(OutputDrainTimeoutMs, CancellationToken.None)) != drainTask)
            _logger.Warn("[EXEC] Output drain timed out");

        sw.Stop();

        string stdout, stderr;
        lock (outputLock)
        {
            stdout = stdoutBuilder.ToString().TrimEnd();
            stderr = stderrBuilder.ToString().TrimEnd();
        }

        var result = new CommandResult
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            DurationMs = sw.ElapsedMilliseconds
        };

        _logger.Info($"[EXEC] Exit={result.ExitCode} Duration={result.DurationMs}ms TimedOut={timedOut} Stdout={result.Stdout.Length}chars Stderr={result.Stderr.Length}chars");
        return result;
    }

    private static (string fileName, string arguments) BuildProcessArgs(CommandRequest request)
    {
        var shell = (request.Shell ?? "powershell").ToLowerInvariant();
        var command = request.Command;
        var isCmd = shell == "cmd";

        if (request.Args is { Length: > 0 })
            command = command + " " + string.Join(" ", request.Args.Select(a => ShellQuoting.QuoteForShell(a, isCmd)));

        return shell switch
        {
            "cmd" => ("cmd.exe", $"/C {command}"),
            "pwsh" => ("pwsh.exe", $"-NoProfile -NonInteractive -Command {command}"),
            _ => ("powershell.exe", $"-NoProfile -NonInteractive -Command {command}")
        };
    }

    private void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC] Failed to kill process: {ex.Message}");
        }
    }
}
