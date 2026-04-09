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
    public const string Version = "6.0.0";
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

        // Register auto-start
        if (_settings.AutoStart)
            AutoStartManager.Set(true);

        // Try to load the logo image for the tray icon
        _logoImage = LoadLogoImage();

        // Create node service
        _nodeService = new NodeService(new AppLogger());
        _nodeService.StatusChanged += OnStatusChanged;
        _nodeService.NotifyRequested += OnNotify;

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Text = "DonClaw Node",
            Icon = CreateTrayIcon(ConnectionStatus.Disconnected),
            Visible = true
        };

        // Context menu (right-click)
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

        // Start screenshot cache (background capture loop)
        _screenshotCache = new ScreenshotCache();
        _screenshotCache.Start();

        // Start text finder (local OCR)
        _textFinder = new TextFinder();

        // Start local API server (direct HTTP access to capabilities)
        _localApi = new LocalApiServer(LocalApiPort, _nodeService.GetCapabilities(), new AppLogger());
        _localApi.ScreenshotProvider = TakeScreenshot;
        _localApi.RegionScreenshotProvider = TakeRegionScreenshot;
        _localApi.Cache = _screenshotCache;
        _localApi.Finder = _textFinder;
        _localApi.Start();

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
        try { await _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token); }
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
    /// Full-screen capture with format support (jpeg/png) and max-width scaling.
    /// Smaller maxWidth = smaller file = fewer AI tokens = faster processing.
    /// Default: 960px wide JPEG at q60 ≈ 30-60KB (vs 300KB+ at full res).
    /// </summary>
    private static Task<byte[]> TakeScreenshot(int quality, int maxWidth, string format)
    {
        return Task.Run(() =>
        {
            var screen = Screen.PrimaryScreen!;
            var w = screen.Bounds.Width;
            var h = screen.Bounds.Height;

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
            }

            return CompressImage(bmp, quality, maxWidth, format);
        });
    }

    /// <summary>
    /// Region capture — only grab a specific rectangle of the screen.
    /// Use this to read specific UI elements without capturing the whole screen.
    /// Much smaller and faster than full-screen capture.
    /// </summary>
    private static Task<byte[]> TakeRegionScreenshot(int x, int y, int w, int h, int quality, string format)
    {
        return Task.Run(() =>
        {
            // Clamp to screen bounds
            var screen = Screen.PrimaryScreen!;
            w = Math.Min(w, screen.Bounds.Width - x);
            h = Math.Min(h, screen.Bounds.Height - y);
            if (w <= 0 || h <= 0) throw new ArgumentException("Invalid region");

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
            }

            return CompressImage(bmp, quality, 0, format); // 0 = no resize for regions
        });
    }

    /// <summary>
    /// Compress a bitmap to the requested format with optional downscaling.
    /// Supports jpeg and png (WebP requires external codec on Windows).
    /// </summary>
    private static byte[] CompressImage(Bitmap bmp, int quality, int maxWidth, string format)
    {
        Bitmap output = bmp;
        bool needsDispose = false;

        // Downscale if maxWidth is set and image is wider
        if (maxWidth > 0 && bmp.Width > maxWidth)
        {
            float scale = (float)maxWidth / bmp.Width;
            int newH = (int)(bmp.Height * scale);
            output = new Bitmap(bmp, maxWidth, newH);
            needsDispose = true;
        }

        try
        {
            using var ms = new MemoryStream();

            if (format == "png")
            {
                output.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            }
            else // jpeg (default, best compression-to-quality for AI vision)
            {
                var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
                output.Save(ms, encoder, encoderParams);
            }

            return ms.ToArray();
        }
        finally
        {
            if (needsDispose) output.Dispose();
        }
    }
}
