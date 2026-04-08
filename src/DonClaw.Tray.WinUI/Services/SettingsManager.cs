using System;
using System.IO;
using System.Text.Json;

namespace DonClaw.Tray.Services;

public class SettingsManager
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DonClawNode");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DonClawNode");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string GatewayHost { get; set; } = "";
    public string Token { get; set; } = "";
    public bool AutoStart { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GatewayHost) && !string.IsNullOrWhiteSpace(Token);

    public SettingsManager()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(DataPath);
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<SettingsData>(json, Opts);
                if (data != null)
                {
                    GatewayHost = data.GatewayHost ?? "";
                    Token = data.Token ?? "";
                    AutoStart = data.AutoStart;
                }
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new SettingsData { GatewayHost = GatewayHost, Token = Token, AutoStart = AutoStart }, Opts));
        }
        catch { }
    }

    private class SettingsData
    {
        public string? GatewayHost { get; set; }
        public string? Token { get; set; }
        public bool AutoStart { get; set; } = true;
    }
}
