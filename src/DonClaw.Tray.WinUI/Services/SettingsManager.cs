using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DonClawTray.Services;

/// <summary>
/// DonClaw Node settings — just gateway host and token.
/// Stored at %APPDATA%\DonClawNode\settings.json
/// </summary>
public class SettingsManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DonClawNode");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Gateway WebSocket URL (e.g., ws://my-instance.donclaw.com:18789)</summary>
    public string GatewayHost { get; set; } = "ws://localhost:18789";

    /// <summary>Authentication token for the DonClaw gateway</summary>
    public string Token { get; set; } = "";

    /// <summary>Auto-start with Windows</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>Show toast notifications when commands are received</summary>
    public bool ShowNotifications { get; set; } = true;

    public SettingsManager()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var data = JsonSerializer.Deserialize<SettingsData>(json, _jsonOptions);
                if (data != null)
                {
                    GatewayHost = data.GatewayHost ?? GatewayHost;
                    Token = data.Token ?? Token;
                    AutoStart = data.AutoStart;
                    ShowNotifications = data.ShowNotifications;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load settings: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var data = new SettingsData
            {
                GatewayHost = GatewayHost,
                Token = Token,
                AutoStart = AutoStart,
                ShowNotifications = ShowNotifications
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(SettingsFilePath, json);

            Logger.Info("Settings saved");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal class SettingsData
{
    [JsonPropertyName("gatewayHost")]
    public string? GatewayHost { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("showNotifications")]
    public bool ShowNotifications { get; set; } = true;
}
