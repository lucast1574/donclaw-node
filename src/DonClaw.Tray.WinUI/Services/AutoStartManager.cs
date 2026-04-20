using System;
using Microsoft.Win32;

namespace DonClaw.Tray.Services;

public static class AutoStartManager
{
    private const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DonClawNode";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, true);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe))
                {
                    // Pass --silent so startup via Windows login goes straight to tray
                    // without popping the Settings window. The user can still open it
                    // by clicking the tray icon whenever they want.
                    key.SetValue(AppName, $"\"{exe}\" --silent");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }
}
