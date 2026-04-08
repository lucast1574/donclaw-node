using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// App capability — launch, list, close, and focus applications visibly on the user's screen.
/// Uses UseShellExecute = true and CreateNoWindow = false so apps appear normally.
/// </summary>
public class AppCapability : NodeCapabilityBase
{
    public override string Category => "app";

    private static readonly string[] _commands = new[]
    {
        "app.launch",
        "app.list",
        "app.close",
        "app.focus"
    };

    public override IReadOnlyList<string> Commands => _commands;

    public AppCapability(IOpenClawLogger logger) : base(logger) { }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "app.launch" => Task.FromResult(HandleLaunch(request)),
            "app.list" => Task.FromResult(HandleList(request)),
            "app.close" => Task.FromResult(HandleClose(request)),
            "app.focus" => Task.FromResult(HandleFocus(request)),
            _ => Task.FromResult(Error($"Unknown command: {request.Command}"))
        };
    }

    private NodeInvokeResponse HandleLaunch(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        var args = GetStringArg(request.Args, "args", "");

        if (string.IsNullOrWhiteSpace(path))
            return Error("Missing path parameter");

        Logger.Info($"app.launch: {path} {args}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args ?? "",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            var proc = Process.Start(psi);

            return Success(new
            {
                launched = true,
                pid = proc?.Id ?? -1,
                name = proc?.ProcessName ?? Path.GetFileNameWithoutExtension(path)
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"app.launch failed: {ex.Message}", ex);
            return Error($"Failed to launch app: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleList(NodeInvokeRequest request)
    {
        Logger.Info("app.list");

        try
        {
            var filter = GetStringArg(request.Args, "filter");
            var processes = Process.GetProcesses();
            var apps = new List<object>();

            foreach (var proc in processes)
            {
                try
                {
                    // Only show processes with windows (user-visible apps)
                    var includeAll = GetBoolArg(request.Args, "includeAll", false);
                    if (!includeAll && proc.MainWindowHandle == IntPtr.Zero)
                        continue;

                    var name = proc.ProcessName;
                    if (!string.IsNullOrWhiteSpace(filter) &&
                        !name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                        !(proc.MainWindowTitle?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                        continue;

                    apps.Add(new
                    {
                        pid = proc.Id,
                        name = proc.ProcessName,
                        title = proc.MainWindowTitle ?? "",
                        hasWindow = proc.MainWindowHandle != IntPtr.Zero
                    });
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            return Success(new { apps, count = apps.Count });
        }
        catch (Exception ex)
        {
            Logger.Error("app.list failed", ex);
            return Error($"List failed: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleClose(NodeInvokeRequest request)
    {
        var pid = GetIntArg(request.Args, "pid", -1);
        var name = GetStringArg(request.Args, "name");

        if (pid <= 0 && string.IsNullOrWhiteSpace(name))
            return Error("Missing pid or name parameter");

        Logger.Info($"app.close: pid={pid}, name={name}");

        try
        {
            Process[] targets;
            if (pid > 0)
            {
                try { targets = new[] { Process.GetProcessById(pid) }; }
                catch { return Error($"No process found with PID {pid}"); }
            }
            else
            {
                targets = Process.GetProcessesByName(name!);
                if (targets.Length == 0)
                    return Error($"No process found with name '{name}'");
            }

            int closed = 0;
            foreach (var proc in targets)
            {
                try
                {
                    // Try graceful close first via CloseMainWindow
                    if (proc.MainWindowHandle != IntPtr.Zero)
                        proc.CloseMainWindow();
                    else
                        proc.Kill();
                    closed++;
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            return Success(new { closed, requested = targets.Length });
        }
        catch (Exception ex)
        {
            Logger.Error("app.close failed", ex);
            return Error($"Close failed: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleFocus(NodeInvokeRequest request)
    {
        var pid = GetIntArg(request.Args, "pid", -1);
        var name = GetStringArg(request.Args, "name");

        if (pid <= 0 && string.IsNullOrWhiteSpace(name))
            return Error("Missing pid or name parameter");

        Logger.Info($"app.focus: pid={pid}, name={name}");

        try
        {
            Process? target = null;
            if (pid > 0)
            {
                try { target = Process.GetProcessById(pid); }
                catch { return Error($"No process found with PID {pid}"); }
            }
            else
            {
                var procs = Process.GetProcessesByName(name!);
                target = procs.FirstOrDefault();
                foreach (var p in procs.Skip(1)) p.Dispose();
            }

            if (target == null)
                return Error($"No process found");

            try
            {
                var hwnd = target.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    return Error("Process has no main window to focus");

                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

                return Success(new { focused = true, pid = target.Id, title = target.MainWindowTitle });
            }
            finally
            {
                target.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("app.focus failed", ex);
            return Error($"Focus failed: {ex.Message}");
        }
    }
}

internal static class NativeMethods
{
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
