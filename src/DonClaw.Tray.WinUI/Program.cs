using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DonClaw.Shared;
using DonClaw.Tray.Services;

namespace DonClaw.Tray;

static class Program
{
    public const string Version = "8.3.1";
    public const int LocalApiPort = 9800;

    private static NotifyIcon? _trayIcon;
    private static NodeService? _nodeService;
    private static SettingsManager? _settings;
    private static SettingsForm? _settingsForm;
    private static LocalApiServer? _localApi;
    private static ScreenshotCache? _screenshotCache;
    private static TextFinder? _textFinder;

    // Cache loaded logo for tray icon
    private static Image? _logoImage;

    [STAThread]
    static void Main()
    {
        // Single instance
        using var mutex = new Mutex(true, "DonClawNodeMutex", out bool created);
        if (!created) { Logger.Info("Already running"); return; }

        Logger.Info($"=== DonClaw Node v{Version} starting ===");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        _settings = new SettingsManager();

        // Register auto-start (enabled by default on first run)
        if (_settings.AutoStart)
            AutoStartManager.Set(true);

        // Create Start Menu shortcut so users can find and pin the app
        CreateStartMenuShortcut();

        // Try to load the logo image for the tray icon
        _logoImage = LoadLogoImage();

        // Create node service
        _nodeService = new NodeService(new AppLogger());
        _nodeService.StatusChanged += OnStatusChanged;
        _nodeService.NotifyRequested += OnNotify;
        // Once the gateway issues a permanent device token, drop the one-shot bootstrap token from settings.
        _nodeService.BootstrapConsumed += (_, _) =>
        {
            if (_settings != null && !string.IsNullOrEmpty(_settings.BootstrapToken))
            {
                Logger.Info("Bootstrap token consumed — clearing from settings");
                _settings.ClearBootstrapToken();
            }
        };

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Text = "DonClaw Node",
            Icon = CreateTrayIcon(ConnectionStatus.Disconnected),
            Visible = true
        };

        // Context menu (right-click) — minimal and clean
        var menu = new ContextMenuStrip();
        menu.BackColor = Color.FromArgb(30, 30, 36);
        menu.ForeColor = Color.White;
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.Renderer = new DarkMenuRenderer();
        menu.Items.Add("View Logs", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true }); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
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

        // Start screenshot cache (background capture loop)
        _screenshotCache = new ScreenshotCache();
        _screenshotCache.Start();

        // Start text finder (local OCR)
        _textFinder = new TextFinder();

        // Start local API server (direct HTTP access to capabilities)
        _localApi = new LocalApiServer(LocalApiPort, _nodeService.GetCapabilities(), new AppLogger());
        _localApi.Cache = _screenshotCache;
        _localApi.Finder = _textFinder;
        _localApi.Sequence = new SequenceEngine(
            _nodeService.GetCapabilities(), _textFinder, _screenshotCache, new AppLogger());
        _localApi.Start();

        // Auto-connect if configured
        if (_settings.IsConfigured)
        {
            var bootstrap = string.IsNullOrWhiteSpace(_settings.BootstrapToken) ? null : _settings.BootstrapToken;
            _ = Task.Run(async () =>
            {
                try { await _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token, bootstrap); }
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
        _screenshotCache?.Dispose();
        _localApi?.Dispose();
        _trayIcon.Dispose();
        _nodeService?.Dispose();
        _logoImage?.Dispose();
        Logger.Info("=== DonClaw Node stopped ===");
    }

    private static void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings!, _nodeService!);
        }
        _settingsForm.ShowAtTray();
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
        var bootstrap = string.IsNullOrWhiteSpace(_settings.BootstrapToken) ? null : _settings.BootstrapToken;
        try { await _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token, bootstrap); }
        catch (Exception ex) { Logger.Error($"Reconnect failed: {ex.Message}"); }
    }

    private static void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (_trayIcon == null) return;

        _trayIcon.Icon = CreateTrayIcon(status);
        _trayIcon.Text = status switch
        {
            ConnectionStatus.Connected => "DonClaw Node — Connected ✓",
            ConnectionStatus.Connecting => "DonClaw Node — Connecting...",
            ConnectionStatus.Error => "DonClaw Node — Error",
            _ => "DonClaw Node — Disconnected"
        };

        if (status == ConnectionStatus.Connected)
            _trayIcon.ShowBalloonTip(2000, "DonClaw Node", "Connected and ready!", ToolTipIcon.Info);
    }

    private static void OnNotify(object? sender, DonClaw.Shared.Capabilities.SystemNotifyArgs args)
    {
        _trayIcon?.ShowBalloonTip(3000, args.Title, args.Body, ToolTipIcon.Info);
    }

    /// <summary>
    /// Try to load the DonClaw logo PNG from Assets (icon_white.png — white logo for dark tray backgrounds).
    /// Returns null if not found.
    /// </summary>
    private static Image? LoadLogoImage()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icon_white.png");
            if (File.Exists(path))
            {
                // Load into memory stream to avoid file lock
                var bytes = File.ReadAllBytes(path);
                var ms = new MemoryStream(bytes);
                return Image.FromStream(ms);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Create the tray icon: grey circle with the DonClaw logo inside.
    /// A small colored status dot (green/yellow/red) appears in the bottom-right corner.
    /// </summary>
    private static Icon CreateTrayIcon(ConnectionStatus status)
    {
        const int size = 32; // Use 32px for better quality on HiDPI
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        // Grey circle background
        using var bgBrush = new SolidBrush(Color.FromArgb(90, 90, 96));
        g.FillEllipse(bgBrush, 0, 0, size - 1, size - 1);

        // Draw DonClaw logo centered inside the circle
        if (_logoImage != null)
        {
            // Scale logo to fit inside circle with padding
            int logoPad = 7;
            int logoArea = size - logoPad * 2;

            // The icon_white.png is 232x154, so we need to maintain aspect ratio
            float aspect = (float)_logoImage.Width / _logoImage.Height;
            int drawW, drawH;
            if (aspect > 1)
            {
                drawW = logoArea;
                drawH = (int)(logoArea / aspect);
            }
            else
            {
                drawH = logoArea;
                drawW = (int)(logoArea * aspect);
            }
            int drawX = (size - drawW) / 2;
            int drawY = (size - drawH) / 2;
            g.DrawImage(_logoImage, drawX, drawY, drawW, drawH);
        }
        else
        {
            // Fallback: draw the DonClaw two-squares logo manually
            using var logoBrush = new SolidBrush(Color.White);
            // Two small squares side by side (the DonClaw icon motif)
            int sq = 6;
            int gap = 3;
            int totalW = sq * 2 + gap;
            int startX = (size - totalW) / 2;
            int startY = (size - sq) / 2;
            g.FillRectangle(logoBrush, startX, startY, sq, sq);
            g.FillRectangle(logoBrush, startX + sq + gap, startY, sq, sq);
        }

        // Small status indicator dot in bottom-right corner
        var statusColor = status switch
        {
            ConnectionStatus.Connected => Color.FromArgb(50, 205, 50),
            ConnectionStatus.Connecting => Color.FromArgb(255, 200, 50),
            ConnectionStatus.Error => Color.FromArgb(240, 60, 60),
            _ => Color.FromArgb(160, 160, 160) // subtle grey for disconnected
        };

        int dotSize = 10;
        int dotX = size - dotSize - 1;
        int dotY = size - dotSize - 1;

        // Dark outline around the status dot for visibility
        using var outlineBrush = new SolidBrush(Color.FromArgb(50, 50, 54));
        g.FillEllipse(outlineBrush, dotX - 1, dotY - 1, dotSize + 2, dotSize + 2);

        using var dotBrush = new SolidBrush(statusColor);
        g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);

        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        var result = (Icon)icon.Clone();
        DestroyIcon(hIcon);
        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Create a Start Menu shortcut so users can search "DonClaw" and pin it.
    /// Uses a .url file (no COM dependency on IWshShortcut).
    /// </summary>
    private static void CreateStartMenuShortcut()
    {
        try
        {
            var startMenuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            var shortcutPath = Path.Combine(startMenuPath, "DonClaw Node.url");
            var exePath = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exePath) || File.Exists(shortcutPath))
                return;

            File.WriteAllText(shortcutPath,
                $"[InternetShortcut]\nURL=file:///{exePath.Replace('\\', '/')}\nIconIndex=0\nIconFile={exePath.Replace('\\', '/')}\n");

            Logger.Info($"Start Menu shortcut created: {shortcutPath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to create Start Menu shortcut: {ex.Message}");
        }
    }

}

/// <summary>Dark-themed context menu renderer for the tray icon.</summary>
class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(200, 200, 210);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rc = new Rectangle(Point.Empty, e.Item.Size);
        using var brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(55, 120, 250) : Color.FromArgb(30, 30, 36));
        e.Graphics.FillRectangle(brush, rc);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Color.FromArgb(50, 50, 58));
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(55, 55, 62));
        var rc = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        e.Graphics.DrawRectangle(pen, rc);
    }
}

class DarkColorTable : ProfessionalColorTable
{
    public override Color MenuItemSelected => Color.FromArgb(55, 120, 250);
    public override Color MenuBorder => Color.FromArgb(55, 55, 62);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color ImageMarginGradientBegin => Color.FromArgb(30, 30, 36);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 30, 36);
    public override Color ImageMarginGradientEnd => Color.FromArgb(30, 30, 36);
    public override Color ToolStripDropDownBackground => Color.FromArgb(30, 30, 36);
    public override Color SeparatorDark => Color.FromArgb(50, 50, 58);
    public override Color SeparatorLight => Color.FromArgb(50, 50, 58);
}
