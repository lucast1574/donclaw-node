using DonClaw.Shared;
using DonClaw.Shared.Capabilities;
using Microsoft.UI.Dispatching;
using System.IO;

namespace DonClawTray.Services;

/// <summary>
/// Node service — manages DonClawNodeClient + all capabilities.
/// This is the core of DonClaw Node: it registers all capabilities and connects.
/// </summary>
public class NodeService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private DonClawNodeClient? _nodeClient;
    private readonly string _dataPath;

    // Capabilities
    private SystemCapability? _systemCapability;
    private CanvasCapability? _canvasCapability;
    private ScreenCapability? _screenCapability;
    private CameraCapability? _cameraCapability;
    private BrowserCapability? _browserCapability;
    private AppCapability? _appCapability;
    private FileCapability? _fileCapability;
    private PowerCapability? _powerCapability;

    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SystemNotifyArgs>? NotificationRequested;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;

    public bool IsConnected => _nodeClient?.IsConnected ?? false;
    public string? NodeId => _nodeClient?.NodeId;
    public bool IsPendingApproval => _nodeClient?.IsPendingApproval ?? false;
    public bool IsPaired => _nodeClient?.IsPaired ?? false;
    public string? ShortDeviceId => _nodeClient?.ShortDeviceId;
    public string? FullDeviceId => _nodeClient?.FullDeviceId;
    public string? GatewayUrl => _nodeClient?.GatewayUrl;

    public NodeService(IOpenClawLogger logger, DispatcherQueue dispatcherQueue, string dataPath)
    {
        _logger = logger;
        _dispatcherQueue = dispatcherQueue;
        _dataPath = dataPath;
    }

    /// <summary>
    /// Connect to the DonClaw gateway as a node.
    /// </summary>
    public async Task ConnectAsync(string gatewayUrl, string token)
    {
        if (_nodeClient != null)
            await DisconnectAsync();

        _logger.Info($"Connecting DonClaw Node to {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");

        _nodeClient = new DonClawNodeClient(gatewayUrl, token, _dataPath, _logger);
        _nodeClient.StatusChanged += OnNodeStatusChanged;
        _nodeClient.PairingStatusChanged += OnPairingStatusChanged;
        _nodeClient.InvokeReceived += OnInvokeReceived;

        RegisterCapabilities();

        _nodeClient.SetPermission("camera.capture", true);
        _nodeClient.SetPermission("screen.capture", true);

        await _nodeClient.ConnectAsync();
    }

    /// <summary>
    /// Disconnect from the gateway.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_nodeClient != null)
        {
            await _nodeClient.DisconnectAsync();
            _nodeClient.Dispose();
            _nodeClient = null;
        }
    }

    private void RegisterCapabilities()
    {
        if (_nodeClient == null) return;

        // System: notifications + command execution
        _systemCapability = new SystemCapability(_logger);
        _systemCapability.NotifyRequested += OnSystemNotify;
        _systemCapability.SetCommandRunner(new LocalCommandRunner(_logger));
        _systemCapability.SetApprovalPolicy(new ExecApprovalPolicy(_dataPath, _logger));
        _nodeClient.RegisterCapability(_systemCapability);

        // Canvas: WebView2-based canvas window
        _canvasCapability = new CanvasCapability(_logger);
        // Canvas events are handled in App.xaml.cs where the CanvasWindow lives
        CanvasCapabilityReady?.Invoke(this, _canvasCapability);
        _nodeClient.RegisterCapability(_canvasCapability);

        // Screen: capture + list
        _screenCapability = new ScreenCapability(_logger);
        ScreenCapabilityReady?.Invoke(this, _screenCapability);
        _nodeClient.RegisterCapability(_screenCapability);

        // Camera: list + snap
        _cameraCapability = new CameraCapability(_logger);
        CameraCapabilityReady?.Invoke(this, _cameraCapability);
        _nodeClient.RegisterCapability(_cameraCapability);

        // Browser: open URLs in default browser (visible to user)
        _browserCapability = new BrowserCapability(_logger);
        _nodeClient.RegisterCapability(_browserCapability);

        // App: launch/list/close/focus apps (visible to user)
        _appCapability = new AppCapability(_logger);
        _nodeClient.RegisterCapability(_appCapability);

        // File: browse/read/open files
        _fileCapability = new FileCapability(_logger);
        _nodeClient.RegisterCapability(_fileCapability);

        // Power: shutdown/restart/sleep/lock/status
        _powerCapability = new PowerCapability(_logger);
        _nodeClient.RegisterCapability(_powerCapability);

        _logger.Info("All 8 capabilities registered: system, canvas, screen, camera, browser, app, file, power");
    }

    // Events for the App to hook into capability setup
    public event EventHandler<CanvasCapability>? CanvasCapabilityReady;
    public event EventHandler<ScreenCapability>? ScreenCapabilityReady;
    public event EventHandler<CameraCapability>? CameraCapabilityReady;

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _logger.Info($"Node status changed: {status}");
        StatusChanged?.Invoke(this, status);
    }

    private void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
    {
        _logger.Info($"Pairing status changed: {args.Status} (device: {args.DeviceId.Substring(0, Math.Min(16, args.DeviceId.Length))}...)");
        PairingStatusChanged?.Invoke(this, args);
    }

    private void OnInvokeReceived(object? sender, NodeInvokeRequest request)
    {
        _logger.Info($"Command invoked: {request.Command}");
    }

    private void OnSystemNotify(object? sender, SystemNotifyArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            NotificationRequested?.Invoke(this, args);
        });
    }

    public void Dispose()
    {
        var client = _nodeClient;
        _nodeClient = null;
        try { client?.Dispose(); } catch { }
    }
}
