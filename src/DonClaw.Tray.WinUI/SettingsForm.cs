using System;
using System.Drawing;
using System.Windows.Forms;
using DonClaw.Shared;
using DonClaw.Tray.Services;

namespace DonClaw.Tray;

/// <summary>
/// Settings window — shown when user clicks the tray icon.
/// Clean, modern-looking form with Host, Token, status, and Test button.
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
    private Button _testBtn = null!;
    private Label _footerLabel = null!;

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
        Text = "DonClaw Node";
        Size = new Size(420, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);

        // --- Title ---
        var title = new Label
        {
            Text = "🐾 DonClaw Node",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(24, 20),
            AutoSize = true
        };
        Controls.Add(title);

        // --- Status bar ---
        var statusPanel = new Panel
        {
            Location = new Point(24, 60),
            Size = new Size(360, 36),
            BackColor = Color.FromArgb(34, 34, 40)
        };

        _statusDot = new Panel
        {
            Size = new Size(12, 12),
            Location = new Point(12, 12),
            BackColor = Color.Gray
        };
        _statusDot.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_statusDot.BackColor);
            e.Graphics.FillEllipse(brush, 0, 0, 11, 11);
        };

        _statusLabel = new Label
        {
            Text = "Disconnected",
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(30, 8),
            AutoSize = true
        };

        statusPanel.Controls.Add(_statusDot);
        statusPanel.Controls.Add(_statusLabel);
        Controls.Add(statusPanel);

        // --- Gateway Host ---
        var hostLabel = new Label
        {
            Text = "Gateway Host",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 200),
            Location = new Point(24, 115),
            AutoSize = true
        };
        Controls.Add(hostLabel);

        _hostInput = new TextBox
        {
            Location = new Point(24, 138),
            Size = new Size(360, 32),
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10.5f)
        };
        Controls.Add(_hostInput);

        var hostHint = new Label
        {
            Text = "e.g. ws://your-instance.donclaw.com:18789",
            ForeColor = Color.FromArgb(100, 100, 110),
            Font = new Font("Segoe UI", 8),
            Location = new Point(24, 166),
            AutoSize = true
        };
        Controls.Add(hostHint);

        // --- Token ---
        var tokenLabel = new Label
        {
            Text = "Token",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(200, 200, 200),
            Location = new Point(24, 198),
            AutoSize = true
        };
        Controls.Add(tokenLabel);

        _tokenInput = new TextBox
        {
            Location = new Point(24, 221),
            Size = new Size(360, 32),
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10.5f),
            UseSystemPasswordChar = true
        };
        Controls.Add(_tokenInput);

        var tokenHint = new Label
        {
            Text = "Found in your DonClaw dashboard → Instance Settings",
            ForeColor = Color.FromArgb(100, 100, 110),
            Font = new Font("Segoe UI", 8),
            Location = new Point(24, 249),
            AutoSize = true
        };
        Controls.Add(tokenHint);

        // --- Buttons ---
        _connectBtn = new Button
        {
            Text = "💾 Save & Connect",
            Location = new Point(24, 295),
            Size = new Size(175, 40),
            BackColor = Color.FromArgb(55, 120, 250),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _connectBtn.FlatAppearance.BorderSize = 0;
        _connectBtn.Click += OnConnectClick;
        Controls.Add(_connectBtn);

        _testBtn = new Button
        {
            Text = "🔌 Test",
            Location = new Point(209, 295),
            Size = new Size(175, 40),
            BackColor = Color.FromArgb(50, 50, 58),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10),
            Cursor = Cursors.Hand
        };
        _testBtn.FlatAppearance.BorderSize = 1;
        _testBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 80);
        _testBtn.Click += OnTestClick;
        Controls.Add(_testBtn);

        // --- Device ID (if available) ---
        var deviceLabel = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(80, 80, 90),
            Font = new Font("Segoe UI", 8),
            Location = new Point(24, 350),
            AutoSize = true,
            Name = "deviceLabel"
        };
        Controls.Add(deviceLabel);

        // --- Footer ---
        _footerLabel = new Label
        {
            Text = $"DonClaw © {DateTime.Now.Year}",
            ForeColor = Color.FromArgb(60, 60, 70),
            Font = new Font("Segoe UI", 8),
            Dock = DockStyle.Bottom,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 30
        };
        Controls.Add(_footerLabel);
    }

    private void LoadValues()
    {
        _hostInput.Text = _settings.GatewayHost;
        _tokenInput.Text = _settings.Token;
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
    }

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        var host = _hostInput.Text.Trim();
        var token = _tokenInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
        {
            _statusLabel.Text = "❌ Host and Token are required";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            return;
        }

        _settings.GatewayHost = host;
        _settings.Token = token;
        _settings.Save();

        _connectBtn.Enabled = false;
        _connectBtn.Text = "⏳ Connecting...";

        try
        {
            _nodeService.Disconnect();
            await _nodeService.ConnectAsync(host, token);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ {ex.Message}";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
        }
        finally
        {
            _connectBtn.Enabled = true;
            _connectBtn.Text = "💾 Save & Connect";
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
            // Quick connect test — create a temporary client, try to connect
            var testHost = host;
            if (!testHost.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !testHost.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                testHost = "ws://" + testHost;

            using var client = new System.Net.WebSockets.ClientWebSocket();
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(new Uri(testHost), cts.Token);
            await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", cts.Token);

            _statusLabel.Text = "✅ Connection successful!";
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Don't exit app, just hide
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
