using System.Diagnostics;
using System.Security.Principal;

namespace DonClaw.Tray.Services;

/// <summary>
/// UAC / "run as administrator" helpers for the tray app.
///
/// <para>Design choice: the Node is <b>not</b> forced to run elevated. Most of
/// what the user's assistant does with it (opening apps, moving the mouse,
/// reading user-scope files) works fine at Medium integrity. Running as admin
/// permanently would trigger a UAC prompt on every Windows login and would
/// give the paired assistant more authority than it needs by default.</para>
///
/// <para>Instead we expose <see cref="IsRunningAsAdmin"/> so the UI can show
/// the current integrity level, and <see cref="RestartElevated"/> so the
/// user can re-launch the app with admin rights on demand (e.g. because
/// they need to install something or modify a protected file).</para>
/// </summary>
public static class AdminElevation
{
    /// <summary>True if the current process has Administrator rights.</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restart the tray as elevated. Triggers the standard Windows UAC prompt;
    /// if the user accepts, a new elevated DonClawNode.exe is launched and the
    /// current (non-elevated) one exits. If the user cancels, nothing changes
    /// and we return false so the caller can show a soft error.
    /// </summary>
    public static bool RestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true, // required for Verb="runas"
                Verb = "runas",
                // Pass --silent so the restarted instance goes straight to tray
                // instead of popping the Settings window in the user's face.
                Arguments = "--silent",
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "",
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            // User declined the UAC prompt, or Windows blocked the elevation.
            return false;
        }
    }
}
