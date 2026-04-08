using System;
using System.IO;
using DonClaw.Shared;

namespace DonClaw.Tray.Services;

public static class Logger
{
    private static readonly object _lock = new();
    public static readonly string LogFilePath;

    static Logger()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DonClawNode");
        Directory.CreateDirectory(dir);
        LogFilePath = Path.Combine(dir, "donclaw-node.log");
    }

    public static void Info(string msg) => Write("INF", msg);
    public static void Debug(string msg) => Write("DBG", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg) => Write("ERR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}\n";
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogFilePath, line);
                var fi = new FileInfo(LogFilePath);
                if (fi.Length > 5 * 1024 * 1024)
                {
                    var text = File.ReadAllText(LogFilePath);
                    File.WriteAllText(LogFilePath, text[(text.Length / 2)..]);
                }
            }
            catch { }
        }
    }
}

public class AppLogger : IOpenClawLogger
{
    public void Info(string message) => Logger.Info(message);
    public void Debug(string message) => Logger.Debug(message);
    public void Warn(string message) => Logger.Warn(message);
    public void Error(string message, Exception? ex = null) =>
        Logger.Error(ex != null ? $"{message}: {ex.Message}" : message);
}
