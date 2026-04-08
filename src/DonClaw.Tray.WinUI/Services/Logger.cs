using System.IO;

namespace DonClawTray.Services;

/// <summary>
/// Simple file logger for the DonClaw Node tray application.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory;
    private static readonly string _logFilePath;

    static Logger()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DonClawNode");

        Directory.CreateDirectory(_logDirectory);
        _logFilePath = Path.Combine(_logDirectory, "donclaw-node.log");

        // Rotate log if too large (> 5MB)
        try
        {
            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Exists && fileInfo.Length > 5 * 1024 * 1024)
            {
                var backupPath = Path.Combine(_logDirectory, "donclaw-node.log.old");
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(_logFilePath, backupPath);
            }
        }
        catch { }
    }

    public static string LogFilePath => _logFilePath;

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Debug(string message) => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { }
        }

#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }
}
