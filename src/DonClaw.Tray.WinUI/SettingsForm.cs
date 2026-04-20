using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using DonClaw.Shared;
using DonClaw.Tray.Services;

namespace DonClaw.Tray;

/// <summary>
/// Borderless popup settings window with two modes:
///  1. DonClaw Account: login → pick instance → auto-connect
///  2. Free Mode: manual gateway host + token (legacy, self-hosted)
/// </summary>
public class SettingsForm : Form
{
    private readonly SettingsManager _settings;
    private readonly NodeService _nodeService;
    private DonClawApiClient? _apiClient;

    // Shared controls
    private Label _statusLabel = null!;
    private Panel _statusDot = null!;
    private Label _machineLabel = null!;
    private CheckBox _autoStartCheck = null!;
    private LinkLabel _modeToggleBtn = null!;

    // DonClaw mode controls
    private Panel _donclawPanel = null!;
    private Panel _loginPanel = null!;
    private Panel _instancesPanel = null!;
    private Button _loginBtn = null!;
    private Label _loginErrorLabel = null!;
    private AuthCallbackServer? _authServer;
    private Label _userLabel = null!;
    private LinkLabel _logoutBtn = null!;
    private ListBox _instancesList = null!;
    private Button _connectInstanceBtn = null!;
    private Button _refreshBtn = null!;
    private List<DonClawApiClient.ConnectableInstance> _instances = new();

    // Free mode controls (legacy)
    private Panel _freePanel = null!;
    private TextBox _hostInput = null!;
    private TextBox _tokenInput = null!;
    private Button _connectBtn = null!;
    private Button _disconnectBtn = null!;
    private Button _testBtn = null!;

    private bool _isDonClawMode = true;
    private System.Windows.Forms.Timer? _planCheckTimer;

    #region Win32
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint f);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO i);
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    #endregion

    private const int W = 400, H_LOGIN = 240, H_FULL = 460, R = 12;

    public SettingsForm(SettingsManager settings, NodeService nodeService)
    {
        _settings = settings;
        _nodeService = nodeService;

        InitializeUI();

        _nodeService.StatusChanged += (_, s) =>
        {
            if (InvokeRequired) Invoke(() => UpdateStatus(s));
            else UpdateStatus(s);
        };

        // Auto-recover from expired/consumed DonClaw bootstrap tokens. When
        // the gateway closes the socket with 'pairing required', ask the
        // backend for a freshly-minted setup code and reconnect with it.
        // Rate-limited in the client so this only fires every ~15s. In Free
        // mode (no _apiClient) we do nothing and the existing reconnect loop
        // just keeps retrying until the operator approves manually.
        _nodeService.AuthRejected += async (_, reason) =>
        {
            if (_apiClient == null || string.IsNullOrEmpty(_settings.ConnectedInstanceId)) return;
            try
            {
                var fresh = await _apiClient.GetSetupCodeAsync(_settings.ConnectedInstanceId);
                if (fresh == null
                    || !string.IsNullOrEmpty(fresh.Error)
                    || string.IsNullOrWhiteSpace(fresh.GatewayUrl)
                    || string.IsNullOrWhiteSpace(fresh.GatewayToken))
                {
                    return;
                }
                _settings.GatewayHost = fresh.GatewayUrl!;
                _settings.Token = fresh.GatewayToken!;
                _settings.BootstrapToken = fresh.GatewayToken!;
                _settings.Save();
                _nodeService.Disconnect();
                await _nodeService.ConnectAsync(fresh.GatewayUrl!, fresh.GatewayToken!, fresh.GatewayToken!);
            }
            catch { /* non-fatal: retry loop will keep trying */ }
        };

        // Restore previous session
        if (_settings.IsDonClawLoggedIn)
        {
            _apiClient = new DonClawApiClient(new TrayLogger());
            _apiClient.SetToken(_settings.DonClawToken);
            ShowLoggedInState();
            _ = RefreshInstances();
        }

        UpdateStatus(_nodeService.CurrentStatus);
    }

    private void InitializeUI()
    {
        Text = "DonClaw Node";
        Size = new Size(W, H_LOGIN); // Start compact, expand after login
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        Region = CreateRoundedRegion(W, H_LOGIN, R);

        BuildTitleBar();
        BuildStatusBar();
        BuildDonClawPanel();
        BuildFreePanel();
        BuildFooter();

        _freePanel.Visible = false;
        _donclawPanel.Visible = true;
        _modeToggleBtn.Visible = false; // Hide mode toggle until logged in
    }

    // ==================== TITLE ====================
    private void BuildTitleBar()
    {
        var p = new Panel { Location = new Point(0, 0), Size = new Size(W, 52), BackColor = BackColor };
        var logo = new PictureBox { Size = new Size(28, 28), Location = new Point(16, 12), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        try { var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icon_white.png"); if (File.Exists(path)) logo.Image = Image.FromFile(path); } catch { }
        p.Controls.Add(logo);
        p.Controls.Add(new Label { Text = "DonClaw Node", Font = new Font("Segoe UI", 14, FontStyle.Bold), Location = new Point(50, 14), AutoSize = true });
        p.Controls.Add(new Label { Text = $"v{Program.Version}", Font = new Font("Segoe UI", 8), ForeColor = Color.FromArgb(100, 100, 110), Location = new Point(225, 20), AutoSize = true });
        Controls.Add(p);
    }

    // ==================== STATUS BAR ====================
    private void BuildStatusBar()
    {
        var sp = new Panel { Location = new Point(20, 56), Size = new Size(W - 40, 34), BackColor = Color.FromArgb(34, 34, 40) };
        MakeRounded(sp, 6);
        _statusDot = new Panel { Size = new Size(10, 10), Location = new Point(12, 12), BackColor = Color.Gray };
        _statusDot.Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var b = new SolidBrush(_statusDot.BackColor); e.Graphics.FillEllipse(b, 0, 0, 9, 9); };
        _statusLabel = new Label { Text = "Disconnected", ForeColor = Color.FromArgb(160, 160, 160), Font = new Font("Segoe UI", 9f), Location = new Point(28, 8), AutoSize = true };
        sp.Controls.Add(_statusDot);
        sp.Controls.Add(_statusLabel);
        Controls.Add(sp);
    }

    // ==================== DONCLAW PANEL (Login + Instances) ====================
    private void BuildDonClawPanel()
    {
        _donclawPanel = new Panel { Location = new Point(0, 96), Size = new Size(W, 330), BackColor = BackColor };

        // --- Login subpanel (compact: just one button) ---
        _loginPanel = new Panel { Location = new Point(20, 10), Size = new Size(W - 40, 90), BackColor = BackColor };

        _loginPanel.Controls.Add(new Label
        {
            Text = "Conecta tu cuenta de DonClaw",
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(160, 160, 170),
            Location = new Point(0, 0),
            AutoSize = true
        });

        _loginBtn = MakeButton("🔐 Iniciar sesión en donclaw.com", new Point(0, 28), new Size(W - 40, 38), Color.FromArgb(55, 120, 250));
        _loginBtn.Click += OnLoginClick;
        _loginPanel.Controls.Add(_loginBtn);

        _loginErrorLabel = new Label { Text = "", ForeColor = Color.FromArgb(240, 100, 100), Font = new Font("Segoe UI", 8f), Location = new Point(0, 72), Size = new Size(W - 40, 18) };
        _loginPanel.Controls.Add(_loginErrorLabel);

        _donclawPanel.Controls.Add(_loginPanel);

        // --- Instances subpanel (hidden until logged in) ---
        _instancesPanel = new Panel { Location = new Point(20, 0), Size = new Size(W - 40, 280), BackColor = BackColor, Visible = false };

        _userLabel = new Label { Text = "", Font = new Font("Segoe UI", 9f), ForeColor = Color.FromArgb(160, 200, 255), Location = new Point(0, 0), AutoSize = true };
        _instancesPanel.Controls.Add(_userLabel);

        _logoutBtn = new LinkLabel { Text = "Cerrar sesión", Font = new Font("Segoe UI", 8f), ForeColor = Color.FromArgb(140, 140, 150), LinkColor = Color.FromArgb(140, 140, 150), Location = new Point(W - 140, 2), AutoSize = true };
        _logoutBtn.LinkClicked += OnLogoutClick;
        _instancesPanel.Controls.Add(_logoutBtn);

        _instancesPanel.Controls.Add(new Label { Text = "Tus instancias", Font = new Font("Segoe UI", 9f, FontStyle.Bold), Location = new Point(0, 24), AutoSize = true });

        // Scrollable panel for instance tiles
        _instancesList = new ListBox
        {
            Location = new Point(0, 48),
            Size = new Size(W - 40, 150),
            BackColor = Color.FromArgb(30, 30, 36),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 44
        };
        _instancesList.DrawItem += OnDrawInstanceItem;
        _instancesList.DoubleClick += OnConnectInstanceClick; // Double click to connect
        _instancesPanel.Controls.Add(_instancesList);

        _connectInstanceBtn = MakeButton("⚡ Conectar", new Point(0, 206), new Size((W - 48) / 2, 34), Color.FromArgb(55, 120, 250));
        _connectInstanceBtn.Click += OnConnectInstanceClick;
        _instancesPanel.Controls.Add(_connectInstanceBtn);

        _refreshBtn = MakeButton("🔄 Refrescar", new Point((W - 48) / 2 + 8, 206), new Size((W - 48) / 2, 34), Color.FromArgb(50, 50, 58));
        _refreshBtn.Click += async (_, _) => await RefreshInstances();
        _instancesPanel.Controls.Add(_refreshBtn);

        _machineLabel = new Label { Text = "", ForeColor = Color.FromArgb(50, 205, 50), Font = new Font("Segoe UI", 8.5f), Location = new Point(0, 248), AutoSize = true, Visible = false };
        _instancesPanel.Controls.Add(_machineLabel);

        _donclawPanel.Controls.Add(_instancesPanel);
        Controls.Add(_donclawPanel);
    }

    // ==================== FREE PANEL (Legacy: Host + Token) ====================
    private void BuildFreePanel()
    {
        _freePanel = new Panel { Location = new Point(0, 96), Size = new Size(W, 330), BackColor = BackColor };

        int y = 10;
        _freePanel.Controls.Add(new Label { Text = "Gateway Host", Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Color.FromArgb(200, 200, 200), Location = new Point(20, y), AutoSize = true });
        _hostInput = new TextBox
        {
            Location = new Point(20, y + 22), Size = new Size(W - 40, 28),
            BackColor = Color.FromArgb(40, 40, 46), ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f),
            // Prefill with previously used Free-mode host so users don't retype every time
            Text = !string.IsNullOrWhiteSpace(_settings.FreeGatewayHost)
                ? _settings.FreeGatewayHost
                : _settings.GatewayHost,
        };
        _freePanel.Controls.Add(_hostInput);
        _freePanel.Controls.Add(new Label { Text = "ws://local-ip:18789 or wss://your.tailscale.ts.net", ForeColor = Color.FromArgb(80, 80, 90), Font = new Font("Segoe UI", 7.5f), Location = new Point(20, y + 50), AutoSize = true });

        y += 75;
        _freePanel.Controls.Add(new Label { Text = "Token", Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Color.FromArgb(200, 200, 200), Location = new Point(20, y), AutoSize = true });
        _tokenInput = new TextBox
        {
            Location = new Point(20, y + 22), Size = new Size(W - 40, 28),
            BackColor = Color.FromArgb(40, 40, 46), ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f),
            UseSystemPasswordChar = true,
            Text = !string.IsNullOrWhiteSpace(_settings.FreeToken)
                ? _settings.FreeToken
                : _settings.Token,
        };
        _freePanel.Controls.Add(_tokenInput);
        _freePanel.Controls.Add(new Label { Text = "Gateway token from your OpenClaw instance", ForeColor = Color.FromArgb(80, 80, 90), Font = new Font("Segoe UI", 7.5f), Location = new Point(20, y + 50), AutoSize = true });

        y += 80;
        _connectBtn = MakeButton("🔗 Connect", new Point(20, y), new Size(108, 36), Color.FromArgb(55, 120, 250));
        _connectBtn.Click += OnFreeConnectClick;
        _freePanel.Controls.Add(_connectBtn);

        _disconnectBtn = MakeButton("⛔ Stop", new Point(136, y), new Size(108, 36), Color.FromArgb(180, 50, 50));
        _disconnectBtn.Enabled = false;
        _disconnectBtn.Click += (_, _) => _nodeService.Disconnect();
        _freePanel.Controls.Add(_disconnectBtn);

        _testBtn = MakeButton("🔌 Test", new Point(252, y), new Size(108, 36), Color.FromArgb(50, 50, 58));
        _testBtn.Click += OnTestClick;
        _freePanel.Controls.Add(_testBtn);

        Controls.Add(_freePanel);
    }

    // ==================== FOOTER ====================
    private void BuildFooter()
    {
        _autoStartCheck = new CheckBox
        {
            Text = "Start with Windows", ForeColor = Color.FromArgb(160, 160, 170), Font = new Font("Segoe UI", 8.5f),
            Location = new Point(20, 376), AutoSize = true, Checked = AutoStartManager.IsEnabled()
        };
        _autoStartCheck.CheckedChanged += (_, _) => { AutoStartManager.Set(_autoStartCheck.Checked); _settings.AutoStart = _autoStartCheck.Checked; _settings.Save(); };
        Controls.Add(_autoStartCheck);

        // Mode toggle
        _modeToggleBtn = new LinkLabel
        {
            Text = "Modo libre →", Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(120, 160, 255),
            LinkColor = Color.FromArgb(120, 160, 255), Location = new Point(W - 120, 378), AutoSize = true
        };
        _modeToggleBtn.LinkClicked += (_, _) => ToggleMode();
        Controls.Add(_modeToggleBtn);

        Controls.Add(new Panel { Location = new Point(20, 400), Size = new Size(W - 40, 1), BackColor = Color.FromArgb(50, 50, 58) });

        // Quick links
        var logsLink = new LinkLabel { Text = "📄 Logs", Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(140, 140, 150), LinkColor = Color.FromArgb(140, 140, 150), Location = new Point(20, 408), AutoSize = true };
        logsLink.LinkClicked += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true }); } catch { } };
        Controls.Add(logsLink);

        var exitLink = new LinkLabel { Text = "❌ Exit", Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(140, 140, 150), LinkColor = Color.FromArgb(140, 140, 150), Location = new Point(100, 408), AutoSize = true };
        exitLink.LinkClicked += (_, _) => { _nodeService?.Dispose(); Application.Exit(); };
        Controls.Add(exitLink);

        // Copy-LAN-token helper: lets the user grab the bearer token for
        // remote scripts/agents that want to hit http://host:9800 from the LAN.
        // Loopback calls don't need it, so this is optional.
        var tokenLink = new LinkLabel
        {
            Text = "🔑 Copy LAN token",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(140, 140, 150),
            LinkColor = Color.FromArgb(140, 140, 150),
            Location = new Point(170, 408),
            AutoSize = true,
        };
        tokenLink.LinkClicked += (_, _) =>
        {
            try
            {
                _settings.EnsureLocalApiToken();
                Clipboard.SetText(_settings.LocalApiToken);
                tokenLink.Text = "✅ Copied";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { tokenLink.Text = "🔑 Copy LAN token"; t.Stop(); t.Dispose(); };
                t.Start();
            }
            catch { }
        };
        Controls.Add(tokenLink);

        Controls.Add(new Label { Text = $"DonClaw Node v{Program.Version}  ·  © {DateTime.Now.Year}", ForeColor = Color.FromArgb(50, 50, 60), Font = new Font("Segoe UI", 7.5f), Location = new Point(20, 432), Size = new Size(W - 40, 20), TextAlign = ContentAlignment.MiddleCenter });
    }

    // ==================== MODE TOGGLE ====================
    private void ToggleMode()
    {
        _isDonClawMode = !_isDonClawMode;
        _donclawPanel.Visible = _isDonClawMode;
        _freePanel.Visible = !_isDonClawMode;
        _modeToggleBtn.Text = _isDonClawMode ? "Modo libre →" : "← DonClaw";
        BringFooterToFront();

        if (!_isDonClawMode)
        {
            // Always show the Free-mode credentials, not whatever is currently live
            // (which might be a DonClaw instance URL). Falls back to the live slot
            // if no Free-mode credentials have ever been saved.
            _hostInput.Text = !string.IsNullOrWhiteSpace(_settings.FreeGatewayHost)
                ? _settings.FreeGatewayHost
                : _settings.GatewayHost;
            _tokenInput.Text = !string.IsNullOrWhiteSpace(_settings.FreeToken)
                ? _settings.FreeToken
                : _settings.Token;
        }
    }

    // ==================== DONCLAW LOGIN ====================
    private async void OnLoginClick(object? sender, EventArgs e)
    {
        _loginBtn.Enabled = false;
        _loginBtn.Text = "⏳ Esperando login en el navegador...";
        _loginErrorLabel.Text = "";

        try
        {
            // Start ephemeral HTTP server for callback
            _authServer?.Dispose();
            _authServer = new AuthCallbackServer();

            // Open browser to DonClaw login with callback port
            var loginUrl = $"https://donclaw.com/auth/node-callback?port={_authServer.Port}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(loginUrl) { UseShellExecute = true });

            // Wait for callback (5 min timeout)
            var callbackTask = _authServer.WaitForCallbackAsync(TimeSpan.FromMinutes(5));

            // Show cancel option
            _loginErrorLabel.Text = "Se abrió el navegador. Inicia sesión ahí.";
            _loginErrorLabel.ForeColor = Color.FromArgb(160, 160, 170);

            var (token, email, userId) = await callbackTask;

            // Save session
            _settings.DonClawToken = token;
            _settings.DonClawEmail = email;
            _settings.DonClawUserId = userId;
            _settings.Save();

            // Init API client with the received token
            _apiClient?.Dispose();
            _apiClient = new DonClawApiClient(new TrayLogger());
            _apiClient.SetToken(token);

            // Verify plan before showing instances
            var planCheck = await _apiClient.VerifyNodePlanAsync();
            if (!planCheck.Allowed)
            {
                _loginErrorLabel.Text = planCheck.Reason ?? "Plan no válido para usar DonClaw Node.";
                _loginErrorLabel.ForeColor = Color.FromArgb(240, 100, 100);
                _settings.ClearDonClawSession();
                _apiClient?.Dispose();
                _apiClient = null;
                return;
            }

            ShowLoggedInState();
            StartPlanCheckTimer();
            await RefreshInstances();
        }
        catch (TimeoutException)
        {
            _loginErrorLabel.Text = "Tiempo agotado. Inténtalo de nuevo.";
            _loginErrorLabel.ForeColor = Color.FromArgb(240, 100, 100);
        }
        catch (Exception ex)
        {
            _loginErrorLabel.Text = ex.Message;
            _loginErrorLabel.ForeColor = Color.FromArgb(240, 100, 100);
        }
        finally
        {
            _loginBtn.Enabled = true;
            _loginBtn.Text = "🔐 Iniciar sesión en donclaw.com";
            _authServer?.Dispose();
            _authServer = null;
        }
    }

    private void ShowLoggedInState()
    {
        _loginPanel.Visible = false;
        _instancesPanel.Visible = true;
        _userLabel.Text = $"👤 {_settings.DonClawEmail}";
        _modeToggleBtn.Visible = true;

        // Expand window to full size
        Size = new Size(W, H_FULL);
        Region = CreateRoundedRegion(W, H_FULL, R);

        // Footer sits at absolute Y but _donclawPanel (opaque) overlaps it;
        // raise footer controls above the panel so they render on top.
        BringFooterToFront();
    }

    private void BringFooterToFront()
    {
        try
        {
            _autoStartCheck?.BringToFront();
            _modeToggleBtn?.BringToFront();
            foreach (Control c in Controls)
            {
                // Footer separator + Logs/Exit + version label are direct children
                // with small heights near the bottom — bring them all forward.
                if (c == _donclawPanel || c == _freePanel) continue;
                c.BringToFront();
            }
        }
        catch { /* best-effort visual fix */ }
    }

    private void OnLogoutClick(object? sender, EventArgs e)
    {
        _nodeService.Disconnect();
        StopPlanCheckTimer();
        _settings.ClearDonClawSession();
        _apiClient?.Dispose();
        _apiClient = null;
        _instancesList.Items.Clear();
        _instances.Clear();
        _loginPanel.Visible = true;
        _instancesPanel.Visible = false;
        _freePanel.Visible = false;
        _donclawPanel.Visible = true;
        _isDonClawMode = true;
        _modeToggleBtn.Visible = false;
        _modeToggleBtn.Text = "Modo libre →";

        // Shrink window back to compact
        Size = new Size(W, H_LOGIN);
        Region = CreateRoundedRegion(W, H_LOGIN, R);
    }

    // ==================== INSTANCES ====================
    private async Task RefreshInstances()
    {
        if (_apiClient == null || !_apiClient.IsAuthenticated) return;

        _refreshBtn.Enabled = false;
        _refreshBtn.Text = "⏳...";

        // Verify token still valid
        var valid = await _apiClient.VerifyTokenAsync();
        if (!valid)
        {
            _loginErrorLabel.Text = "Sesión expirada. Inicia sesión de nuevo.";
            OnLogoutClick(null, EventArgs.Empty);
            _refreshBtn.Enabled = true;
            _refreshBtn.Text = "🔄 Refrescar";
            return;
        }

        var result = await _apiClient.GetConnectableInstancesAsync();
        _refreshBtn.Enabled = true;
        _refreshBtn.Text = "🔄 Refrescar";

        if (!result.Allowed)
        {
            _instancesList.Items.Clear();
            _instancesList.Items.Add(result.Reason ?? "Plan no válido");
            _instances.Clear();
            return;
        }

        _instances = result.Instances ?? new();
        _instancesList.Items.Clear();
        foreach (var inst in _instances)
            _instancesList.Items.Add($"{inst.Name}  ·  {inst.WorkspaceName}");

        if (_instancesList.Items.Count == 0)
            _instancesList.Items.Add("(No hay instancias activas)");
    }

    private void OnDrawInstanceItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _instances.Count) return;
        var inst = _instances[e.Index];
        var isSelected = (e.State & DrawItemState.Selected) != 0;
        var rc = e.Bounds;

        // Tile background with rounded feel
        using var bg = new SolidBrush(isSelected ? Color.FromArgb(55, 120, 250) : Color.FromArgb(38, 38, 44));
        e.Graphics.FillRectangle(bg, rc.X + 2, rc.Y + 2, rc.Width - 4, rc.Height - 4);

        // Status dot
        var dotColor = inst.Status == "active" ? Color.FromArgb(50, 205, 50) : Color.Gray;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dotBrush = new SolidBrush(dotColor);
        e.Graphics.FillEllipse(dotBrush, rc.X + 12, rc.Y + 10, 8, 8);

        // Instance name (bold)
        using var nameFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var nameBrush = new SolidBrush(Color.White);
        e.Graphics.DrawString(inst.Name, nameFont, nameBrush, rc.X + 28, rc.Y + 4);

        // Workspace + domain (smaller, muted)
        using var subFont = new Font("Segoe UI", 8f);
        using var subBrush = new SolidBrush(isSelected ? Color.FromArgb(200, 220, 255) : Color.FromArgb(120, 120, 135));
        e.Graphics.DrawString($"{inst.WorkspaceName}  ·  {inst.Domain}", subFont, subBrush, rc.X + 28, rc.Y + 24);
    }

    private async void OnConnectInstanceClick(object? sender, EventArgs e)
    {
        var idx = _instancesList.SelectedIndex;
        if (idx < 0 || idx >= _instances.Count) return;

        var inst = _instances[idx];
        _connectInstanceBtn.Enabled = false;
        _connectInstanceBtn.Text = "⏳ Conectando...";

        try
        {
            // Fetch a FRESH setup code right before connecting. The backend's
            // bootstrap tokens are one-shot and expire after 10 minutes, so the
            // one we got when we first listed instances may already be stale.
            // GET /instances/{id}/setup-code triggers the server to mint a new
            // bootstrap if the stored one is close to expiring. Falls back to
            // the token from the list if the extra call fails.
            var gatewayUrl = inst.GatewayUrl;
            var bootstrap = inst.GatewayToken;
            if (_apiClient != null)
            {
                try
                {
                    var fresh = await _apiClient.GetSetupCodeAsync(inst.Id);
                    if (fresh != null
                        && string.IsNullOrEmpty(fresh.Error)
                        && !string.IsNullOrWhiteSpace(fresh.GatewayUrl)
                        && !string.IsNullOrWhiteSpace(fresh.GatewayToken))
                    {
                        gatewayUrl = fresh.GatewayUrl!;
                        bootstrap = fresh.GatewayToken!;
                    }
                }
                catch { /* non-fatal: use the list value */ }
            }

            // Live slot (GatewayHost/Token/BootstrapToken) reflects this DonClaw
            // instance. Free-mode slot (FreeGatewayHost/FreeToken/Free...) is
            // untouched so the user's LAN credentials survive when they toggle
            // back between modes.
            _settings.GatewayHost = gatewayUrl;
            _settings.Token = bootstrap;
            _settings.BootstrapToken = bootstrap;
            _settings.ConnectedInstanceId = inst.Id;
            _settings.Save();

            _nodeService.Disconnect();
            await _nodeService.ConnectAsync(gatewayUrl, bootstrap, bootstrap);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"❌ {ex.Message}";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
        }
        finally
        {
            _connectInstanceBtn.Enabled = true;
            _connectInstanceBtn.Text = "Conectar";
        }
    }

    // ==================== FREE MODE ====================
    private async void OnFreeConnectClick(object? sender, EventArgs e)
    {
        var host = _hostInput.Text.Trim();
        var token = _tokenInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
        {
            _statusLabel.Text = "❌ Host and Token required";
            _statusLabel.ForeColor = Color.FromArgb(240, 60, 60);
            return;
        }

        // Persist in BOTH the free-mode slot (so it survives a DonClaw detour)
        // and the live slot (so auto-connect works on next startup).
        // Bootstrap token defaults to the regular token — the gateway accepts the
        // same value for both in LAN pairing flows, so the user only needs to
        // provide one string.
        _settings.FreeGatewayHost = host;
        _settings.FreeToken = token;
        _settings.FreeBootstrapToken = token;
        _settings.GatewayHost = host;
        _settings.Token = token;
        _settings.BootstrapToken = token;
        _settings.Save();

        _connectBtn.Enabled = false;
        _connectBtn.Text = "⏳...";
        try
        {
            _nodeService.Disconnect();
            await _nodeService.ConnectAsync(host, token, token);
        }
        catch (Exception ex) { _statusLabel.Text = $"❌ {ex.Message}"; _statusLabel.ForeColor = Color.FromArgb(240, 60, 60); }
        finally { _connectBtn.Enabled = true; _connectBtn.Text = "🔗 Connect"; }
    }

    private async void OnTestClick(object? sender, EventArgs e)
    {
        var host = _hostInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) { _statusLabel.Text = "❌ Host required"; return; }

        _testBtn.Enabled = false; _testBtn.Text = "⏳...";
        try
        {
            var h = host.StartsWith("ws://") || host.StartsWith("wss://") ? host : "ws://" + host;
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            using var cts = new System.Threading.CancellationTokenSource(5000);
            await ws.ConnectAsync(new Uri(h), cts.Token);
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", cts.Token);
            _statusLabel.Text = "✅ Reachable!"; _statusLabel.ForeColor = Color.FromArgb(50, 205, 50);
        }
        catch (Exception ex) { _statusLabel.Text = $"❌ {ex.Message}"; _statusLabel.ForeColor = Color.FromArgb(240, 60, 60); }
        finally { _testBtn.Enabled = true; _testBtn.Text = "🔌 Test"; }
    }

    // ==================== STATUS ====================
    private void UpdateStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Color.FromArgb(50, 205, 50), "Connected ✓"),
            ConnectionStatus.Connecting => (Color.FromArgb(255, 200, 50), "Connecting..."),
            ConnectionStatus.Error => (Color.FromArgb(240, 60, 60), "Connection Error"),
            _ => (Color.Gray, "Disconnected")
        };
        _statusDot.BackColor = color; _statusDot.Invalidate();
        _statusLabel.Text = text; _statusLabel.ForeColor = color;
        if (_disconnectBtn != null) _disconnectBtn.Enabled = status == ConnectionStatus.Connected || status == ConnectionStatus.Connecting;

        if (status == ConnectionStatus.Connected)
        { _machineLabel.Text = $"💻 Connected to {Environment.MachineName}"; _machineLabel.ForeColor = Color.FromArgb(50, 205, 50); _machineLabel.Visible = true; }
        else if (status == ConnectionStatus.Connecting)
        { _machineLabel.Text = $"💻 Connecting..."; _machineLabel.ForeColor = Color.FromArgb(255, 200, 50); _machineLabel.Visible = true; }
        else { _machineLabel.Visible = false; }
    }

    // ==================== PLAN VERIFICATION ====================

    /// <summary>Check plan every 30 min. Disconnect if plan expired.</summary>
    private void StartPlanCheckTimer()
    {
        _planCheckTimer?.Stop();
        _planCheckTimer = new System.Windows.Forms.Timer { Interval = 24 * 60 * 60 * 1000 }; // 24h
        _planCheckTimer.Tick += async (_, _) => await CheckPlanStillActive();
        _planCheckTimer.Start();
    }

    private void StopPlanCheckTimer()
    {
        _planCheckTimer?.Stop();
        _planCheckTimer?.Dispose();
        _planCheckTimer = null;
    }

    private async Task CheckPlanStillActive()
    {
        if (_apiClient == null || !_apiClient.IsAuthenticated) return;

        try
        {
            var valid = await _apiClient.VerifyTokenAsync();
            if (!valid)
            {
                // Token expired
                if (InvokeRequired) Invoke(() => HandlePlanExpired("Sesión expirada. Inicia sesión de nuevo."));
                else HandlePlanExpired("Sesión expirada. Inicia sesión de nuevo.");
                return;
            }

            var planCheck = await _apiClient.VerifyNodePlanAsync();
            if (!planCheck.Allowed)
            {
                if (InvokeRequired) Invoke(() => HandlePlanExpired(planCheck.Reason ?? "Tu plan ya no permite usar DonClaw Node."));
                else HandlePlanExpired(planCheck.Reason ?? "Tu plan ya no permite usar DonClaw Node.");
            }
        }
        catch { /* Network error — retry on next tick */ }
    }

    private void HandlePlanExpired(string reason)
    {
        _nodeService.Disconnect();
        StopPlanCheckTimer();
        _statusLabel.Text = reason;
        _statusLabel.ForeColor = Color.FromArgb(240, 100, 100);
        _statusDot.BackColor = Color.FromArgb(240, 100, 100);
        _statusDot.Invalidate();

        // Show notification
        try
        {
            var ni = Application.OpenForms.Count > 0 ? null : new NotifyIcon { Icon = SystemIcons.Warning, Visible = true };
            ni?.ShowBalloonTip(5000, "DonClaw Node", reason, ToolTipIcon.Warning);
        }
        catch { }

        // Force logout after short delay
        OnLogoutClick(null, EventArgs.Empty);
    }

    private static void ResetDeviceIdentity()
    {
        try { var p = Path.Combine(SettingsManager.DataPath, "device-key-ed25519.json"); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // ==================== POSITIONING ====================
    public void ShowAtTray()
    {
        var (x, y) = CalcPos();
        Location = new Point(x, y);
        Region = CreateRoundedRegion(Width, Height, R);
        Show(); BringToFront(); Activate();

        // Quick plan check when user opens the tray (non-blocking)
        if (_apiClient?.IsAuthenticated == true)
            _ = CheckPlanStillActive();
    }

    private (int, int) CalcPos()
    {
        const int m = 12;
        try
        {
            GetCursorPos(out POINT pt);
            var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
                return (mi.rcWork.Right - Width - m, mi.rcWork.Bottom - Height - m);
        }
        catch { }
        var wa = Screen.PrimaryScreen!.WorkingArea;
        return (wa.Right - Width - m, wa.Bottom - Height - m);
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Color.FromArgb(55, 55, 62), 1); using var path = RPath(0, 0, Width - 1, Height - 1, R); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.DrawPath(pen, path); }
    private bool _drag; private Point _ds;
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && e.Y < 52) { _drag = true; _ds = e.Location; } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) Location = new Point(Location.X + e.X - _ds.X, Location.Y + e.Y - _ds.Y); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; }
    protected override bool ProcessCmdKey(ref Message msg, Keys k) { if (k == Keys.Escape) { Hide(); return true; } return base.ProcessCmdKey(ref msg, k); }

    #region Helpers
    private static Button MakeButton(string text, Point loc, Size sz, Color bg)
    {
        var b = new Button { Text = text, Location = loc, Size = sz, BackColor = bg, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }
    private static Region CreateRoundedRegion(int w, int h, int r) { using var p = RPath(0, 0, w, h, r); return new Region(p); }
    private static GraphicsPath RPath(int x, int y, int w, int h, int r) { var p = new GraphicsPath(); int d = r * 2; p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure(); return p; }
    private static void MakeRounded(Panel p, int r) { p.Paint += (_, e) => { using var path = RPath(0, 0, p.Width, p.Height, r); p.Region = new Region(path); }; }
    #endregion

    /// <summary>Simple logger bridge for DonClawApiClient.</summary>
    private class TrayLogger : IOpenClawLogger
    {
        public void Info(string msg) => Logger.Info($"[API] {msg}");
        public void Debug(string msg) { }
        public void Warn(string msg) => Logger.Warn($"[API] {msg}");
        public void Error(string msg, Exception? ex = null) => Logger.Error($"[API] {msg}{(ex != null ? $": {ex.Message}" : "")}");
    }
}
