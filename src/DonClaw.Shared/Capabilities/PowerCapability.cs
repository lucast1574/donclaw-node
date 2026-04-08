using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Power capability — system power management.
/// Shutdown, restart, sleep, lock, and battery status.
/// </summary>
public class PowerCapability : NodeCapabilityBase
{
    public override string Category => "power";

    private static readonly string[] _commands = new[]
    {
        "power.shutdown",
        "power.restart",
        "power.sleep",
        "power.lock",
        "power.status"
    };

    public override IReadOnlyList<string> Commands => _commands;

    public PowerCapability(IOpenClawLogger logger) : base(logger) { }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "power.shutdown" => Task.FromResult(HandleShutdown(request)),
            "power.restart" => Task.FromResult(HandleRestart(request)),
            "power.sleep" => Task.FromResult(HandleSleep(request)),
            "power.lock" => Task.FromResult(HandleLock(request)),
            "power.status" => Task.FromResult(HandleStatus(request)),
            _ => Task.FromResult(Error($"Unknown command: {request.Command}"))
        };
    }

    private NodeInvokeResponse HandleShutdown(NodeInvokeRequest request)
    {
        var delaySeconds = GetIntArg(request.Args, "delay", 60);
        var force = GetBoolArg(request.Args, "force", false);

        if (delaySeconds < 0) delaySeconds = 0;
        if (delaySeconds > 3600) delaySeconds = 3600; // Max 1 hour delay

        Logger.Info($"power.shutdown: delay={delaySeconds}s, force={force}");

        try
        {
            var forceFlag = force ? "/f " : "";
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = $"/s {forceFlag}/t {delaySeconds}",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return Success(new { scheduled = true, action = "shutdown", delaySeconds });
        }
        catch (Exception ex)
        {
            Logger.Error("power.shutdown failed", ex);
            return Error($"Shutdown failed: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleRestart(NodeInvokeRequest request)
    {
        var delaySeconds = GetIntArg(request.Args, "delay", 60);
        var force = GetBoolArg(request.Args, "force", false);

        if (delaySeconds < 0) delaySeconds = 0;
        if (delaySeconds > 3600) delaySeconds = 3600;

        Logger.Info($"power.restart: delay={delaySeconds}s, force={force}");

        try
        {
            var forceFlag = force ? "/f " : "";
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = $"/r {forceFlag}/t {delaySeconds}",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return Success(new { scheduled = true, action = "restart", delaySeconds });
        }
        catch (Exception ex)
        {
            Logger.Error("power.restart failed", ex);
            return Error($"Restart failed: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleSleep(NodeInvokeRequest request)
    {
        Logger.Info("power.sleep");

        try
        {
            // Use rundll32 to suspend — works on most Windows systems
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "powrprof.dll,SetSuspendState 0,1,0",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return Success(new { scheduled = true, action = "sleep" });
        }
        catch (Exception ex)
        {
            Logger.Error("power.sleep failed", ex);
            return Error($"Sleep failed: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleLock(NodeInvokeRequest request)
    {
        Logger.Info("power.lock");

        try
        {
            NativePowerMethods.LockWorkStation();
            return Success(new { locked = true });
        }
        catch (Exception ex)
        {
            Logger.Error("power.lock failed", ex);
            return Error($"Lock failed: {ex.Message}");
        }
    }

    private NodeInvokeResponse HandleStatus(NodeInvokeRequest request)
    {
        Logger.Info("power.status");

        try
        {
            var status = new PowerStatusInfo();
            var uptimeMs = Environment.TickCount64;
            var uptimeSeconds = uptimeMs / 1000;

            int batteryPercent = -1;
            bool onAc = true;
            string batteryStatus = "unknown";

            try
            {
                var powerStatus = NativePowerMethods.GetSystemPowerStatus();
                onAc = powerStatus.ACLineStatus == 1;
                batteryPercent = powerStatus.BatteryLifePercent == 255
                    ? -1
                    : powerStatus.BatteryLifePercent;

                batteryStatus = powerStatus.BatteryFlag switch
                {
                    1 => "high",
                    2 => "low",
                    4 => "critical",
                    8 => "charging",
                    128 => "no_battery",
                    _ => "unknown"
                };
            }
            catch { }

            return Success(new
            {
                onAc,
                batteryPercent,
                batteryStatus,
                uptimeSeconds,
                uptimeFormatted = FormatUptime(uptimeSeconds)
            });
        }
        catch (Exception ex)
        {
            Logger.Error("power.status failed", ex);
            return Error($"Status failed: {ex.Message}");
        }
    }

    private static string FormatUptime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }
}

internal static class NativePowerMethods
{
    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    public static SystemPowerStatus GetSystemPowerStatus()
    {
        GetSystemPowerStatus(out var status);
        return status;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

internal class PowerStatusInfo
{
    public bool OnAc { get; set; }
    public int BatteryPercent { get; set; }
    public string BatteryStatus { get; set; } = "unknown";
    public long UptimeSeconds { get; set; }
}
