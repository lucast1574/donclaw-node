using System.Diagnostics;
using System.Text.Json;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Browser capability — opens URLs in the user's default browser visibly on their screen.
/// Uses Process.Start with UseShellExecute = true so the browser opens in the user's session.
/// </summary>
public class BrowserCapability : NodeCapabilityBase
{
    public override string Category => "browser";

    private static readonly string[] _commands = new[]
    {
        "browser.open",
        "browser.openTab",
        "browser.list"
    };

    public override IReadOnlyList<string> Commands => _commands;

    public BrowserCapability(IOpenClawLogger logger) : base(logger) { }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "browser.open" => Task.FromResult(HandleOpen(request)),
            "browser.openTab" => Task.FromResult(HandleOpenTab(request)),
            "browser.list" => Task.FromResult(HandleList(request)),
            _ => Task.FromResult(Error($"Unknown command: {request.Command}"))
        };
    }

    private NodeInvokeResponse HandleOpen(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Error("Missing url parameter");

        if (!IsValidUrl(url))
            return Error($"Invalid or unsafe URL: {url}");

        Logger.Info($"browser.open: {url}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            return Success(new { opened = true, url });
        }
        catch (Exception ex)
        {
            Logger.Error($"browser.open failed: {ex.Message}", ex);
            return Error($"Failed to open URL: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleOpenTab(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Error("Missing url parameter");

        if (!IsValidUrl(url))
            return Error($"Invalid or unsafe URL: {url}");

        Logger.Info($"browser.openTab: {url}");

        // Opening a URL with UseShellExecute = true typically opens a new tab in the
        // default browser if one is already running. This is the simplest approach.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            return Success(new { opened = true, url });
        }
        catch (Exception ex)
        {
            Logger.Error($"browser.openTab failed: {ex.Message}", ex);
            return Error($"Failed to open tab: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleList(NodeInvokeRequest request)
    {
        Logger.Info("browser.list");

        try
        {
            var browserProcessNames = new[] { "chrome", "firefox", "msedge", "opera", "brave", "vivaldi", "iexplore" };
            var browsers = new List<object>();

            foreach (var procName in browserProcessNames)
            {
                var processes = Process.GetProcessesByName(procName);
                foreach (var proc in processes)
                {
                    try
                    {
                        browsers.Add(new
                        {
                            name = proc.ProcessName,
                            pid = proc.Id,
                            title = proc.MainWindowTitle
                        });
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }

            return Success(new { browsers });
        }
        catch (Exception ex)
        {
            Logger.Error("browser.list failed", ex);
            return Error($"List failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Only allow http and https URLs to prevent command injection via other schemes.
    /// </summary>
    private static bool IsValidUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme == "http" || uri.Scheme == "https";
        }
        return false;
    }
}
