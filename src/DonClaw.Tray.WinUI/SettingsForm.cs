using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DonClaw.Shared;
using DonClaw.Tray.Services;

namespace DonClaw.Tray;

/// <summary>
/// Borderless popup settings window — appears above the system tray (like Molty/OpenClaw).
/// No title bar, no close/minimize buttons. Clicks outside dismiss it.
/// Rounded corners with a dark modern UI.
/// </summary>
public class SettingsForm : Form
{
    private readonly SettingsManager _settings;
    private readonly NodeService _nodeService;

    private TextBox _hostInput = null!;
    private TextBox _tokenInput = null!;
    private Label _statusLabel = null!;
    private Panel _statusDot = null!;
    private Button _connectBtn = null!;
    private Button _disconnectBtn = null!;
    private Button _testBtn = null!;
    private CheckBox _autoStartCheck = null!;
    private Label _machineLabel = null!;

    #region Win32 — Taskbar position detection
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    #endregion

    // Corner radius for the popup shape
    private const int CornerRadius = 12;

    public SettingsForm(SettingsManager settings, NodeService nodeService)
    {
        _settings = settings;
        _nodeService = nodeService;

        InitializeUI();
        LoadValues();

        _nodeService.StatusChanged += (_, s) =>
        {
            if (InvokeRequired) Invoke(() => UpdateStatus(s));
            else UpdateStatus(s);
        };

        UpdateStatus(_nodeService.CurrentStatus);
    }

    private void InitializeUI()
    {
        // --- Borderless popup window ---
        Text = "DonClaw Node";
        Size = new Size(380, 450);
        FormBorderStyle = FormBorderStyle.None;  // No borders, no title bar
        ShowInTaskbar = false;                    // Don't show in taskbar
        TopMost = true;                           // Always on top
        StartPosition = FormStartPosition.Manual; // We position it ourselves
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);

        // Rounded corners via region
        Region = CreateRoundedRegion(Size.Width, Size.Height, CornerRadius);

        // Double buffer for smooth rendering
        DoubleBuffered = true;

        // --- Title area with logo image ---
        var titlePanel = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(380, 52),
            BackColor = Color.FromArgb(24, 24, 28)
        };

        // Load the DonClaw icon (icon.png — black rounded rect with two white squares)
        var logoPicture = new PictureBox
        {
            Size = new Size(28, 28),
            Location = new Point(16, 12),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon_white.png");
            if (File.Exists(logoPath))
                logoPicture.Image = Image.FromFile(logoPath);
        }
        catch { }
        titlePanel.Controls.Add(logoPicture);

        var titleLabel = new Label
        {
            Text = "DonClaw Node",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(50, 14),
            AutoSize = true
        };
        titlePanel.Controls.Add(titleLabel);

        var versionLabel = new Label
        {
            Text = $"v{Program.Version}",
            Font = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(100, 100, 110),
            Location = new Point(210, 20),
            AutoSize = true
        };
        titlePanel.Controls.Add(versionLabel);

        Controls.Add(titlePanel);

        // --- Status bar ---
        var statusPanel = new Panel
        {
            Location = new Point(20, 56),
            Size = new Size(340, 34),
            BackColor = Color.FromArgb(34, 34, 40)
        };
        MakeRounded(statusPanel, 6);

        _statusDot = new Panel
        {
            Size = new Size(10, 10),
            Location = new Point(12, 12),
            BackColor = Color.Gray
        };
        _statusDot.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_statusDot.BackColor);
            e.Graphics.FillEllipse(brush, 0, 0, 9, 9);
        };

        _statusLabel = new Label
        {
            Text = "Disconnected",
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Segoe UI", 9f),
            Location = new Point(28, 8),
            AutoSize = true
        };

        statusPanel.Controls.Add(_statusDot);
        statusPanel.Controls.Add(_statusLabel);
        Controls.Add(statusPanel);

        // --- Gateway Host ---
        var hostLabel = new Label
        {
            Text = "Gateway Host",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 200),
            Location = new Point(20, 105),
            AutoSize = true
        };
        Controls.Add(hostLabel);

        _hostInput = new TextBox
        {
            Location = new Point(20, 128),
            Size = new Size(340, 30),
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f)
        };
        Controls.Add(_hostInput);

        var hostHint = new Label
        {
            Text = "ws://local-ip:18789 or wss://your.tailscale.ts.net",
            ForeColor = Color.FromArgb(80, 80, 90),
            Font = new Font("Segoe UI", 7.5f),
            Location = new Point(20, 155),
            AutoSize = true
        };
        Controls.Add(hostHint);

        // --- Token ---
        var tokenLabel = new Label
        {
            Text = "Token",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 200),
            Location = new Point(20, 182),
            AutoSize = true
        };
        Controls.Add(tokenLabel);

        _tokenInput = new TextBox
        {
            Location = new Point(20, 205),
            Size = new Size(340, 30),
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            UseSystemPasswordChar = true
        };
        Controls.Add(_tokenInput);

        var tokenHint = new Label
        {
            Text = "Ask your DonClaw admin for host + token",
            ForeColor = Color.FromArgb(80, 80, 90),
            Font = new Font("Segoe UI", 7.5f),
            Location = new Point(20, 232),
            AutoSize = true
        };
        Controls.Add(tokenHint);

        // --- Machine info (shown when connected) ---
        _machineLabel = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(50, 205, 50),
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(20, 252),
            AutoSize = true,
            Visible = false
        };
        Controls.Add(_machineLabel);

        // --- Buttons ---
        _connectBtn = new Button
        {
            Text = "🔗 Connect",
            Location = new Point(20, 280),
            Size = new Size(108, 38),
            BackColor = Color.FromArgb(55, 120, 250),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _connectBtn.FlatAppearance.BorderSize = 0;
        _connectBtn.Click += OnConnectClick;
        Controls.Add(_connectBtn);

        _disconnectBtn = new Button
        {
            Text = "⛔ Stop",
            Location = new Point(136, 280),
            Size = new Size(108, 38),
            BackColor = Color.FromArgb(180, 50, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _disconnectBtn.FlatAppearance.BorderSize = 0;
        _disconnectBtn.Click += (_, _) => { _nodeService.Disconnect(); };
        Controls.Add(_disconnectBtn);

        _testBtn = new Button
        {
            Text = "🔌 Test",
            Location = new Point(252, 280),
            Size = new Size(108, 38),
            BackColor = Color.FromArgb(50, 50, 58),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f),
            Cursor = Cursors.Hand
        };
        _testBtn.FlatAppearance.BorderSize = 1;
        _testBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 80);
        _testBtn.Click += OnTestClick;
        Controls.Add(_testBtn);

        // --- Auto-start checkbox ---
        _autoStartCheck = new CheckBox
        {
            Text = "Start with Windows",
            ForeColor = Color.FromArgb(160, 160, 170),
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(20, 326),
            AutoSize = true,
            Checked = AutoStartManager.IsEnabled()
        };
        _autoStartCheck.CheckedChanged += (_, _) => {
            AutoStartManager.Set(_autoStartCheck.Checked);
            _settings.AutoStart = _autoStartCheck.Checked;
            _settings.Save();
        };
        Controls.Add(_autoStartCheck);

        // --- Separator line ---
        var separator = new Panel
        {
            Location = new Point(20, 352),
            Size = new Size(340, 1),
            BackColor = Color.FromArgb(50, 50, 58)
        };
        Controls.Add(separator);

        // --- Quick actions row ---
        var reconnectLink = new LinkLabel
        {
            Text = "🔄 Reconnect",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(140, 140, 150),
            LinkColor = Color.FromArgb(140, 140, 150),
            ActiveLinkColor = Color.FromArgb(100, 160, 255),
            Location = new Point(20, 364),
            AutoSize = true
        };
        reconnectLink.LinkClicked += (_, _) =>
        {
            if (_settings.IsConfigured)
            {
                _nodeService.Disconnect();
                var boot = string.IsNullOrWhiteSpace(_settings.BootstrapToken) ? null : _settings.BootstrapToken;
                _ = _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token, boot);
            }
        };
        Controls.Add(reconnectLink);

        var logsLink = new LinkLabel
        {
            Text = "📄 Logs",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(140, 140, 150),
            LinkColor = Color.FromArgb(140, 140, 150),
            ActiveLinkColor = Color.FromArgb(100, 160, 255),
            Location = new Point(130, 364),
            AutoSize = true
        };
        logsLink.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true }); } catch { }
        };
        Controls.Add(logsLink);

        var exitLink = new LinkLabel
        {
            Text = "❌ Exit",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(140, 140, 150),
            LinkColor = Color.FromArgb(140, 140, 150),
            ActiveLinkColor = Color.FromArgb(255, 100, 100),
            Location = new Point(210, 364),
            AutoSize = true
        };
        exitLink.LinkClicked += (_, _) =>
        {
            _nodeService?.Dispose();
            Application.Exit();
        };
        Controls.Add(exitLink);

        // --- Device ID ---
        var deviceLabel = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(60, 60, 70),
            Font = new Font("Segoe UI", 7.5f),
            Location = new Point(20, 390),
            AutoSize = true,
            Name = "deviceLabel"
        };
        Controls.Add(deviceLabel);

        // --- Footer ---
        var footerLabel = new Label
        {
            Text = $"DonClaw Node v{Program.Version}  ·  © {DateTime.Now.Year}",
            ForeColor = Color.FromArgb(50, 50, 60),
            Font = new Font("Segoe UI", 7.5f),
            Location = new Point(20, 420),
            Size = new Size(340, 20),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(footerLabel);
    }

    /// <summary>
    /// Show the popup positioned above the system tray area (taskbar notification area),
    /// just like Molty does. Anchored to bottom-right of the work area.
    /// </summary>
    public void ShowAtTray()
    {
        var (x, y) = CalculateTrayPosition();
        Location = new Point(x, y);

        // Reapply rounded region in case of DPI changes
        Region = CreateRoundedRegion(Width, Height, CornerRadius);

        Show();
        BringToFront();
        Activate();
    }

    /// <summary>
    /// Calculate position above the system tray (notification area).
    /// Puts the popup in the bottom-right of the work area, just above the taskbar.
    /// </summary>
    private (int x, int y) CalculateTrayPosition()
    {
        const int margin = 12;

        try
        {
            // Get cursor position to identify which monitor we're on
            GetCursorPos(out POINT pt);
            var hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                var workArea = monitorInfo.rcWork;

                // Position: bottom-right corner of work area, with margin
                int x = workArea.Right - Width - margin;
                int y = workArea.Bottom - Height - margin;

                return (x, y);
            }
        }
        catch { }

        // Fallback: use Screen class
        var screen = Screen.PrimaryScreen!;
        var wa = screen.WorkingArea;
        return (wa.Right - Width - margin, wa.Bottom - Height - margin);
    }

    private void LoadValues()
    {
        _hostInput.Text = _settings.GatewayHost;
        _tokenInput.Text = _settings.Token;
    }

    /// <summary>
    /// Legacy dialog entry point — retained in case we want to re-expose a setup-code UI later.
    /// Not wired to any control in v8.3.1+.
    /// </summary>
    private void PromptForSetupCode()
    {
        using var dlg = new Form
        {
            Text = "Paste Setup Code",
            Size = new Size(440, 260),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 28),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f),
            ShowInTaskbar = false,
            TopMost = true
        };

        var info = new Label
        {
            Text = "Paste the setup code from your DonClaw admin.\r\nIt fills Host + bootstrap token automatically.",
            ForeColor = Color.FromArgb(170, 170, 180),
            Font = new Font("Segoe UI", 9f),
            Location = new Point(14, 12),
            Size = new Size(400, 36)
        };
        dlg.Controls.Add(info);

        var codeBox = new TextBox
        {
            Location = new Point(14, 54),
            Size = new Size(400, 100),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f)
        };
        dlg.Controls.Add(codeBox);

        var status = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(240, 120, 120),
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(14, 162),
            Size = new Size(400, 18),
            AutoSize = false
        };
        dlg.Controls.Add(status);

        var applyBtn = new Button
        {
            Text = "Apply",
            Location = new Point(318, 190),
            Size = new Size(96, 30),
            BackColor = Color.FromArgb(55, 120, 250),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        applyBtn.FlatAppearance.BorderSize = 0;
        dlg.Controls.Add(applyBtn);

        var cancelBtn = new Button
        {
            Text = "Cancel",
            Location = new Point(214, 190),
            Size = new Size(96, 30),
            BackColor = Color.FromArgb(50, 50, 58),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f),
            DialogResult = DialogResult.Cancel
        };
        cancelBtn.FlatAppearance.BorderSize = 0;
        dlg.Controls.Add(cancelBtn);
        dlg.CancelButton = cancelBtn;

        // Auto-paste from clipboard for convenience
        try
        {
            if (Clipboard.ContainsText())
            {
                var clip = Clipboard.GetText().Trim();
                if (clip.Length > 20 && clip.Length < 4096)
                    codeBox.Text = clip;
            }
        }
        catch { }

        applyBtn.Click += (_, _) =>
        {
            var raw = codeBox.Text.Trim();
            var decoded = SettingsManager.TryDecodeSetupCode(raw);
            if (decoded == null)
            {
                status.Text = "❌ Invalid setup code. Ask your admin for a new one.";
                status.ForeColor = Color.FromArgb(240, 120, 120);
                return;
            }

            _hostInput.Text = decoded.Url ?? "";
            _settings.GatewayHost = decoded.Url ?? "";
            _settings.BootstrapToken = decoded.BootstrapToken ?? "";
            // A fresh bootstrap token implies a fresh pairing: clear stored device token.
            try
            {
                var keyPath = Path.Combine(SettingsManager.DataPath, "device-key-ed25519.json");
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch { }
            _settings.Save();

            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _statusLabel.Text = "🎟️ Setup code applied — click Connect";
            _statusLabel.ForeColor = Color.FromArgb(120, 180, 255);
        }
    }

    private void UpdateStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Color.FromArgb(50, 205, 50), "Connected ✓"),
            ConnectionStatus.Connecting => (Color.FromArgb(255, 200, 50), "Connecting..."),
            ConnectionStatus.Error => (Color.FromArgb(240, 60, 60), "Connection Error"),
            _ => (Color.Gray, "Disconnected")
        };

        _statusDot.BackColor = color;
        _statusDot.Invalidate();
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;

        if (_nodeService.ShortDeviceId != null)
        {
            var dl = Controls["deviceLabel"] as Label;
            if (dl != null) dl.Text = $"Device: {_nodeService.ShortDeviceId}...";
        }

        // Enable/disable disconnect button
        _disconnectBtn.Enabled = (status == ConnectionStatus.Connected || status == ConnectionStatus.Connecting);

        // Show machine name when connected
        if (status == ConnectionStatus.Connected)
        {
            _machineLabel.Text = $"💻 Connected to {Environment.MachineName}";
            _machineLabel.ForeColor = Color.FromArgb(50, 205, 50);
            _machineLabel.Visible = true;
        }
        else if (status == ConnectionStatus.Connecting)
        {
            _machineLabel.Text = $"💻 Connecting {Environment.MachineName}...";
            _machineLabel.ForeColor = Color.FromArgb(255, 200, 50);
            _machineLabel.Visible = true;
        }
        else
        {
            _machineLabel.Visible = false;
        }
    }

    /// <summary>
    /// Delete the old device key so the node registers as a new device
    /// on the target gateway. This is needed when switching between gateways.
    /// </summary>
    private static void ResetDeviceIdentity()
    {
        try
        {
            var keyPath = Path.Combine(SettingsManager.DataPath, "device-key-ed25519.json");
            if (File.Exists(keyPath))
                File.Delete(keyPath);
        }
        catch { }
    }

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        var host = _hostInput.Text.Trim();
        var token = _tokenInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            _statusLabel.Text = "❌ Host is required";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            return;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            _statusLabel.Text = "❌ Token is required";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            return;
        }

        // If host or token changed, reset device identity for clean pairing
        if (host != _settings.GatewayHost || token != _settings.Token)
            ResetDeviceIdentity();

        _settings.GatewayHost = host;
        _settings.Token = token;
        _settings.Save();

        // Bootstrap strategy:
        //  - If the stored BootstrapToken is set (setup-code flow), use it.
        //  - Otherwise, attempt to pair by also sending the Token as a bootstrap candidate.
        //    The gateway will silently ignore it if it isn't a valid bootstrap.
        //    Once paired, the device token replaces both and Token becomes irrelevant.
        var bootstrap = !string.IsNullOrWhiteSpace(_settings.BootstrapToken) ? _settings.BootstrapToken : token;
        var hasBootstrap = !string.IsNullOrWhiteSpace(bootstrap);

        _connectBtn.Enabled = false;
        _connectBtn.Text = "⏳ Connecting...";

        try
        {
            _nodeService.Disconnect();
            await _nodeService.ConnectAsync(host, token, hasBootstrap ? bootstrap : null);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ {ex.Message}";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
        }
        finally
        {
            _connectBtn.Enabled = true;
            _connectBtn.Text = "🔗 Connect";
        }
    }

    private async void OnTestClick(object? sender, EventArgs e)
    {
        var host = _hostInput.Text.Trim();
        var token = _tokenInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
        {
            _statusLabel.Text = "❌ Fill in Host and Token first";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            return;
        }

        _testBtn.Enabled = false;
        _testBtn.Text = "⏳ Testing...";
        _statusLabel.Text = "Testing connection...";
        _statusLabel.ForeColor = Color.FromArgb(255, 200, 50);
        _statusDot.BackColor = Color.FromArgb(255, 200, 50);
        _statusDot.Invalidate();

        try
        {
            var testHost = host;
            if (!testHost.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !testHost.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                testHost = "ws://" + testHost;

            using var client = new System.Net.WebSockets.ClientWebSocket();
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(new Uri(testHost), cts.Token);
            await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", cts.Token);

            _statusLabel.Text = "✅ Reachable! Click Connect to pair.";
            _statusLabel.ForeColor = Color.FromArgb(50, 205, 50);
            _statusDot.BackColor = Color.FromArgb(50, 205, 50);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "❌ Connection timed out";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            _statusDot.BackColor = Color.FromArgb(240, 60, 60);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ {ex.Message}";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            _statusDot.BackColor = Color.FromArgb(240, 60, 60);
        }
        finally
        {
            _statusDot.Invalidate();
            _testBtn.Enabled = true;
            _testBtn.Text = "🔌 Test";
        }
    }

    /// <summary>
    /// Click outside → close (deactivate hides the popup, just like Molty).
    /// </summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    /// <summary>
    /// Don't exit app on close, just hide. Reused on next tray click.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// Paint a subtle border around the rounded popup.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(55, 55, 62), 1);
        using var path = CreateRoundedPath(0, 0, Width - 1, Height - 1, CornerRadius);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    /// <summary>
    /// Allow dragging the borderless window by clicking on the title area.
    /// </summary>
    private bool _dragging;
    private Point _dragStart;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 52) // Title area height
        {
            _dragging = true;
            _dragStart = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Location = new Point(
                Location.X + e.X - _dragStart.X,
                Location.Y + e.Y - _dragStart.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    /// <summary>Escape key also dismisses the popup.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    #region Helpers

    private static Region CreateRoundedRegion(int w, int h, int r)
    {
        using var path = CreateRoundedPath(0, 0, w, h, r);
        return new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void MakeRounded(Panel panel, int radius)
    {
        panel.Paint += (s, e) =>
        {
            using var path = CreateRoundedPath(0, 0, panel.Width, panel.Height, radius);
            panel.Region = new Region(path);
        };
    }

    #endregion
}
