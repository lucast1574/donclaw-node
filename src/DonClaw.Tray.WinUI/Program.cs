using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DonClaw.Shared;
using DonClaw.Tray.Services;

namespace DonClaw.Tray;

static class Program
{
    private static NotifyIcon? _trayIcon;
    private static NodeService? _nodeService;
    private static SettingsManager? _settings;
    private static SettingsForm? _settingsForm;

    [STAThread]
    static void Main()
    {
        // Single instance
        using var mutex = new Mutex(true, "DonClawNodeMutex", out bool created);
        if (!created) { Logger.Info("Already running"); return; }

        Logger.Info("=== DonClaw Node starting ===");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        _settings = new SettingsManager();

        // Register auto-start
        if (_settings.AutoStart)
            AutoStartManager.Set(true);

        // Create node service
        _nodeService = new NodeService(new AppLogger());
        _nodeService.StatusChanged += OnStatusChanged;
        _nodeService.NotifyRequested += OnNotify;

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Text = "DonClaw Node",
            Icon = CreateStatusIcon(Color.Gray),
            Visible = true
        };

        // Context menu
        var menu = new ContextMenuStrip();
        menu.Items.Add("⚙️ Settings", null, (_, _) => ShowSettings());
        menu.Items.Add("🔄 Reconnect", null, (_, _) => Reconnect());
        menu.Items.Add("📄 View Logs", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add("-");
        menu.Items.Add("❌ Exit", null, (_, _) =>
        {
            _nodeService?.Dispose();
            _trayIcon!.Visible = false;
            Application.Exit();
        });

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.Click += (_, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
                ShowSettings();
        };

        // Auto-connect if configured
        if (_settings.IsConfigured)
        {
            _ = Task.Run(async () =>
            {
                try { await _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token); }
                catch (Exception ex) { Logger.Error($"Connect failed: {ex.Message}"); }
            });
        }
        else
        {
            _trayIcon.ShowBalloonTip(3000, "DonClaw Node", "Click to configure your connection.", ToolTipIcon.Info);
            ShowSettings();
        }

        Application.Run();

        // Cleanup
        _trayIcon.Dispose();
        _nodeService?.Dispose();
        Logger.Info("=== DonClaw Node stopped ===");
    }

    private static void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings!, _nodeService!);
        }
        _settingsForm.Show();
        _settingsForm.BringToFront();
        _settingsForm.Activate();
    }

    private static async void Reconnect()
    {
        if (_settings == null || _nodeService == null) return;
        if (!_settings.IsConfigured)
        {
            ShowSettings();
            return;
        }
        _nodeService.Disconnect();
        try { await _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token); }
        catch (Exception ex) { Logger.Error($"Reconnect failed: {ex.Message}"); }
    }

    private static void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (_trayIcon == null) return;

        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Color.FromArgb(50, 205, 50), "DonClaw Node — Connected ✓"),
            ConnectionStatus.Connecting => (Color.FromArgb(255, 200, 50), "DonClaw Node — Connecting..."),
            ConnectionStatus.Error => (Color.FromArgb(240, 60, 60), "DonClaw Node — Error"),
            _ => (Color.Gray, "DonClaw Node — Disconnected")
        };

        _trayIcon.Icon = CreateStatusIcon(color);
        _trayIcon.Text = text;

        if (status == ConnectionStatus.Connected)
            _trayIcon.ShowBalloonTip(2000, "DonClaw Node", "Connected and ready!", ToolTipIcon.Info);
    }

    private static void OnNotify(object? sender, DonClaw.Shared.Capabilities.SystemNotifyArgs args)
    {
        _trayIcon?.ShowBalloonTip(3000, args.Title, args.Body, ToolTipIcon.Info);
    }

    /// <summary>
    /// Create a simple colored circle icon for the tray. 
    /// Green = connected, yellow = connecting, red = error, gray = disconnected.
    /// </summary>
    private static Icon CreateStatusIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Dark circle background
        using var bgBrush = new SolidBrush(Color.FromArgb(30, 30, 36));
        g.FillEllipse(bgBrush, 0, 0, 15, 15);

        // Status color inner circle
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 3, 3, 9, 9);

        return Icon.FromHandle(bmp.GetHicon());
    }
}
