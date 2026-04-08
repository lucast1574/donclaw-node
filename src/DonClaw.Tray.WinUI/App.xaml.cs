using DonClaw.Shared;
using DonClaw.Shared.Capabilities;
using DonClawTray.Services;
using DonClawTray.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.IO;
using System.Threading;
using WinUIEx;

namespace DonClawTray;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private SettingsManager? _settings;
    private NodeService? _nodeService;
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private bool _isExiting;

    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;

    private SettingsWindow? _settingsWindow;
    private Window? _keepAliveWindow;

    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DonClawNode");

    // Icon paths — relative to Assets/
    private const string IconConnected = "Assets\\donclaw-connected.ico";
    private const string IconConnecting = "Assets\\donclaw-connecting.ico";
    private const string IconDisconnected = "Assets\\donclaw-disconnected.ico";

    public App()
    {
        InitializeComponent();
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Logger.Error($"UnhandledException: {e.Exception}");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Logger.Error($"DomainUnhandledException: {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error($"UnobservedTaskException: {e.Exception}");
        e.SetObserved();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Single instance check
        _mutex = new Mutex(true, "DonClawNode", out bool createdNew);
        if (!createdNew)
        {
            Logger.Info("Another instance is already running. Exiting.");
            Exit();
            return;
        }

        // Register toast activation handler
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        _settings = new SettingsManager();

        // Initialize tray icon
        InitializeTrayIcon();

        // If we have settings, auto-connect
        if (!string.IsNullOrWhiteSpace(_settings.GatewayHost) && !string.IsNullOrWhiteSpace(_settings.Token))
        {
            InitializeNodeService();
        }
        else
        {
            // First run: show settings
            _dispatcherQueue.TryEnqueue(() => ShowSettings());
        }

        Logger.Info("DonClaw Node started");
    }

    private void InitializeKeepAliveWindow()
    {
        _keepAliveWindow = new Window();
        _keepAliveWindow.Content = new Microsoft.UI.Xaml.Controls.Grid();
        _keepAliveWindow.AppWindow.IsShownInSwitchers = false;
        _keepAliveWindow.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
    }

    private void InitializeTrayIcon()
    {
        InitializeKeepAliveWindow();

        var iconPath = GetStatusIconPath(ConnectionStatus.Disconnected);
        _trayIcon = new TrayIcon(1, iconPath, "DonClaw Node — Disconnected");
        _trayIcon.IsVisible = true;
        _trayIcon.Selected += OnTrayIconClicked;
        _trayIcon.ContextMenu += OnTrayContextMenu;
    }

    private void OnTrayIconClicked(TrayIcon sender, TrayIconEventArgs e)
    {
        ShowTrayMenu();
    }

    private void OnTrayContextMenu(TrayIcon sender, TrayIconEventArgs e)
    {
        ShowTrayMenu();
    }

    private void ShowTrayMenu()
    {
        if (_dispatcherQueue == null) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            // Build a simple popup menu
            var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();

            // Status header
            var statusText = _currentStatus switch
            {
                ConnectionStatus.Connected when _nodeService?.IsPaired == true => "● Connected & Paired",
                ConnectionStatus.Connected => "● Connected (unpaired)",
                ConnectionStatus.Connecting => "◌ Connecting...",
                ConnectionStatus.Error => "✕ Connection Error",
                _ => "○ Disconnected"
            };

            var statusItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
            {
                Text = statusText,
                IsEnabled = false
            };
            menu.Items.Add(statusItem);

            // Pairing status
            if (_nodeService?.IsPendingApproval == true)
            {
                menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutItem
                {
                    Text = $"⏳ Awaiting pairing — ID: {_nodeService.ShortDeviceId}...",
                    IsEnabled = false
                });
            }

            menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

            // Settings
            var settingsItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "⚙ Settings" };
            settingsItem.Click += (s, e) => ShowSettings();
            menu.Items.Add(settingsItem);

            // Reconnect
            var reconnectItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "↺ Reconnect" };
            reconnectItem.Click += (s, e) => Reconnect();
            menu.Items.Add(reconnectItem);

            // View Logs
            var logsItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "📄 View Logs" };
            logsItem.Click += (s, e) => OpenLogFile();
            menu.Items.Add(logsItem);

            menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());

            // Exit
            var exitItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "✕ Exit" };
            exitItem.Click += (s, e) => ExitApplication();
            menu.Items.Add(exitItem);

            // Show the menu at the cursor position by creating a tiny helper window
            // WinUIEx's TrayIcon doesn't expose a flyout API directly, so we show it
            // relative to a zero-size hidden element positioned at cursor
            try
            {
                // Attempt to get cursor position and show near it
                // For simplicity, show at current cursor position
                var element = _keepAliveWindow?.Content as FrameworkElement;
                if (element != null)
                {
                    menu.ShowAt(element, new Windows.Foundation.Point(0, 0));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to show tray menu: {ex.Message}");
            }
        });
    }

    private void InitializeNodeService()
    {
        if (_settings == null || _dispatcherQueue == null) return;
        if (string.IsNullOrWhiteSpace(_settings.GatewayHost) || string.IsNullOrWhiteSpace(_settings.Token))
        {
            Logger.Warn("Cannot connect: gateway host or token not configured");
            return;
        }

        try
        {
            Logger.Info("Initializing DonClaw Node service...");

            Directory.CreateDirectory(DataPath);

            _nodeService = new NodeService(new AppLogger(), _dispatcherQueue, DataPath);
            _nodeService.StatusChanged += OnNodeStatusChanged;
            _nodeService.NotificationRequested += OnNodeNotificationRequested;
            _nodeService.PairingStatusChanged += OnPairingStatusChanged;

            _ = _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize node service: {ex.Message}");
        }
    }

    private void Reconnect()
    {
        if (_nodeService != null)
        {
            try { _nodeService.Dispose(); } catch { }
            _nodeService = null;
        }

        _currentStatus = ConnectionStatus.Disconnected;
        UpdateTrayIcon();
        InitializeNodeService();
    }

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _currentStatus = status;
        _dispatcherQueue?.TryEnqueue(() => UpdateTrayIcon());

        if (status == ConnectionStatus.Connected && _nodeService?.IsPaired == true)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText("DonClaw Node")
                    .AddText("Connected and ready. Your PC is now controllable.")
                    .Show();
            }
            catch { }
        }
    }

    private void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
    {
        Logger.Info($"Pairing status: {args.Status}");

        try
        {
            if (args.Status == PairingStatus.Pending)
            {
                new ToastContentBuilder()
                    .AddText("DonClaw Node — Pairing Required")
                    .AddText($"Run: openclaw devices approve {args.DeviceId.Substring(0, 16)}...")
                    .Show();
            }
            else if (args.Status == PairingStatus.Paired)
            {
                new ToastContentBuilder()
                    .AddText("DonClaw Node — Paired!")
                    .AddText("Your PC is now paired and ready to receive commands.")
                    .Show();
            }
        }
        catch { }
    }

    private void OnNodeNotificationRequested(object? sender, SystemNotifyArgs args)
    {
        if (_settings?.ShowNotifications != true) return;

        try
        {
            new ToastContentBuilder()
                .AddText(args.Title)
                .AddText(args.Body)
                .Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show notification: {ex.Message}");
        }
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null) return;

        var iconPath = GetStatusIconPath(_currentStatus);
        var tooltip = _currentStatus switch
        {
            ConnectionStatus.Connected when _nodeService?.IsPaired == true => "DonClaw Node — Connected",
            ConnectionStatus.Connected => "DonClaw Node — Connected (awaiting pairing)",
            ConnectionStatus.Connecting => "DonClaw Node — Connecting...",
            ConnectionStatus.Error => "DonClaw Node — Error",
            _ => "DonClaw Node — Disconnected"
        };

        try
        {
            _trayIcon.SetIcon(iconPath);
            _trayIcon.Tooltip = tooltip;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update tray icon: {ex.Message}");
        }
    }

    private static string GetStatusIconPath(ConnectionStatus status)
    {
        var appDir = AppContext.BaseDirectory;
        var name = status switch
        {
            ConnectionStatus.Connected => "donclaw-connected.ico",
            ConnectionStatus.Connecting => "donclaw-connecting.ico",
            ConnectionStatus.Error => "donclaw-disconnected.ico",
            _ => "donclaw-disconnected.ico"
        };
        var path = Path.Combine(appDir, "Assets", name);
        // Fallback to generic icon if specific one doesn't exist
        if (!File.Exists(path))
            path = Path.Combine(appDir, "Assets", "donclaw.ico");
        return path;
    }

    private void ShowSettings()
    {
        if (_settingsWindow == null || _settingsWindow.IsClosed)
        {
            _settingsWindow = new SettingsWindow(_settings!);
            _settingsWindow.SettingsSaved += OnSettingsSaved;
            _settingsWindow.Closed += (s, e) =>
            {
                _settingsWindow!.SettingsSaved -= OnSettingsSaved;
                _settingsWindow = null;
            };

            // Show current device info if we have it
            if (_nodeService?.FullDeviceId != null)
            {
                var pairingMsg = _nodeService.IsPendingApproval
                    ? "⏳ Awaiting pairing approval"
                    : _nodeService.IsPaired
                        ? "✅ Paired"
                        : "○ Not paired";
                _settingsWindow.SetDeviceInfo(_nodeService.FullDeviceId, pairingMsg);
            }
        }
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        Logger.Info("Settings saved — reconnecting...");
        Reconnect();
    }

    private void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open log file: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        Logger.Info("DonClaw Node exiting...");

        try { _nodeService?.Dispose(); } catch { }
        try { _trayIcon?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        try { _settingsWindow?.Close(); } catch { }
        try { _keepAliveWindow?.Close(); } catch { }

        Logger.Info("Shutdown complete");
        Exit();
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        // Handle toast action buttons if needed
        Logger.Debug($"Toast activated: {args.Argument}");
    }
}

/// <summary>
/// Bridges the static Logger to the IOpenClawLogger interface.
/// </summary>
internal class AppLogger : IOpenClawLogger
{
    public void Info(string message) => Logger.Info(message);
    public void Debug(string message) => Logger.Debug(message);
    public void Warn(string message) => Logger.Warn(message);
    public void Error(string message, Exception? ex = null) =>
        Logger.Error(ex != null ? $"{message}: {ex.Message}" : message);
}
