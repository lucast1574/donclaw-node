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
    /// <summary>
    /// One-shot bootstrap token (from "openclaw qr") used to auto-approve pairing
    /// on the very first connect. Cleared automatically once the gateway issues a
    /// permanent device token.
    /// </summary>
    public string BootstrapToken { get; set; } = "";
    public bool AutoStart { get; set; } = true;

    // ── DonClaw account ──────────────────────────────────────────────────
    /// <summary>JWT token from DonClaw backend login.</summary>
    public string DonClawToken { get; set; } = "";
    /// <summary>User email (for display in UI).</summary>
    public string DonClawEmail { get; set; } = "";
    /// <summary>User ID from DonClaw backend.</summary>
    public string DonClawUserId { get; set; } = "";
    /// <summary>Currently connected instance ID (from DonClaw).</summary>
    public string ConnectedInstanceId { get; set; } = "";

    public bool IsDonClawLoggedIn => !string.IsNullOrWhiteSpace(DonClawToken);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GatewayHost) &&
        (!string.IsNullOrWhiteSpace(Token) || !string.IsNullOrWhiteSpace(BootstrapToken));

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
                    BootstrapToken = data.BootstrapToken ?? "";
                    AutoStart = data.AutoStart;
                    DonClawToken = data.DonClawToken ?? "";
                    DonClawEmail = data.DonClawEmail ?? "";
                    DonClawUserId = data.DonClawUserId ?? "";
                    ConnectedInstanceId = data.ConnectedInstanceId ?? "";
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
                new SettingsData
                {
                    GatewayHost = GatewayHost,
                    Token = Token,
                    BootstrapToken = BootstrapToken,
                    AutoStart = AutoStart,
                    DonClawToken = DonClawToken,
                    DonClawEmail = DonClawEmail,
                    DonClawUserId = DonClawUserId,
                    ConnectedInstanceId = ConnectedInstanceId,
                }, Opts));
        }
        catch { }
    }

    /// <summary>
    /// Clears the one-shot bootstrap token once pairing completed.
    /// </summary>
    public void ClearBootstrapToken()
    {
        if (string.IsNullOrEmpty(BootstrapToken)) return;
        BootstrapToken = "";
        Save();
    }

    /// <summary>Clear DonClaw session (logout).</summary>
    public void ClearDonClawSession()
    {
        DonClawToken = "";
        DonClawEmail = "";
        DonClawUserId = "";
        ConnectedInstanceId = "";
        Save();
    }

    private class SettingsData
    {
        public string? GatewayHost { get; set; }
        public string? Token { get; set; }
        public string? BootstrapToken { get; set; }
        public bool AutoStart { get; set; } = true;
        // DonClaw account
        public string? DonClawToken { get; set; }
        public string? DonClawEmail { get; set; }
        public string? DonClawUserId { get; set; }
        public string? ConnectedInstanceId { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Setup code helpers
    //  Setup code = base64url(JSON { "url": "...", "bootstrapToken": "..." })
    //  Generated on the gateway side via: openclaw qr --setup-code-only
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode a setup code (base64url JSON) into host + bootstrap token.
    /// Returns null if the code is invalid.
    /// </summary>
    public static SetupCodePayload? TryDecodeSetupCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        // Accept: plain base64url, with optional "setup:" prefix, or full URL with ?setup=...
        if (trimmed.StartsWith("setup:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("setup:".Length);

        // Convert base64url → base64
        var b64 = trimmed.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1: return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(b64);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            var doc = JsonSerializer.Deserialize<SetupCodePayload>(json, Opts);

            if (doc == null) return null;
            if (string.IsNullOrWhiteSpace(doc.Url)) return null;
            if (string.IsNullOrWhiteSpace(doc.BootstrapToken)) return null;
            return doc;
        }
        catch
        {
            return null;
        }
    }

    public class SetupCodePayload
    {
        public string? Url { get; set; }
        public string? BootstrapToken { get; set; }
    }
}
