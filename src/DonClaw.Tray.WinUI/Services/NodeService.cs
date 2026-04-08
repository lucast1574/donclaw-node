using DonClaw.Shared;
using DonClaw.Shared.Capabilities;
using Microsoft.UI.Dispatching;
using System.IO;

namespace DonClawTray.Services;

/// <summary>
/// Node service — manages DonClawNodeClient + all 11 capabilities.
/// This is the core of DonClaw Node: it registers all capabilities and connects.
/// </summary>
public class NodeService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private DonClawNodeClient? _nodeClient;
    private readonly string _dataPath;

    // Capabilities (11 total)
    private SystemCapability? _systemCapability;
    private CanvasCapability? _canvasCapability;
    private ScreenCapability? _screenCapability;
    private CameraCapability? _cameraCapability;
    private BrowserCapability? _browserCapability;
    private AppCapability? _appCapability;
    private FileCapability? _fileCapability;
    private PowerCapability? _powerCapability;
    private InputCapability? _inputCapability;
    private ClipboardCapability? _clipboardCapability;
    private MicrophoneCapability? _microphoneCapability;

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
        _nodeClient.SetPermission("microphone.record", true);
        _nodeClient.SetPermission("input.control", true);

        await _nodeClient.ConnectAsync();
    }

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

        // 1. System: notifications + command execution
        _systemCapability = new SystemCapability(_logger);
        _systemCapability.NotifyRequested += OnSystemNotify;
        _systemCapability.SetCommandRunner(new LocalCommandRunner(_logger));
        _systemCapability.SetApprovalPolicy(new ExecApprovalPolicy(_dataPath, _logger));
        _nodeClient.RegisterCapability(_systemCapability);

        // 2. Canvas: WebView2-based canvas window
        _canvasCapability = new CanvasCapability(_logger);
        CanvasCapabilityReady?.Invoke(this, _canvasCapability);
        _nodeClient.RegisterCapability(_canvasCapability);

        // 3. Screen: capture + list monitors
        _screenCapability = new ScreenCapability(_logger);
        ScreenCapabilityReady?.Invoke(this, _screenCapability);
        _nodeClient.RegisterCapability(_screenCapability);

        // 4. Camera: list + snap + video clip
        _cameraCapability = new CameraCapability(_logger);
        CameraCapabilityReady?.Invoke(this, _cameraCapability);
        _nodeClient.RegisterCapability(_cameraCapability);

        // 5. Browser: open URLs/tabs in default browser (VISIBLE to user)
        _browserCapability = new BrowserCapability(_logger);
        _nodeClient.RegisterCapability(_browserCapability);

        // 6. App: launch/list/close/focus apps (VISIBLE to user)
        _appCapability = new AppCapability(_logger);
        _nodeClient.RegisterCapability(_appCapability);

        // 7. File: browse/read/write/edit/tree/grep — full filesystem access for AI code assistance
        _fileCapability = new FileCapability(_logger);
        _nodeClient.RegisterCapability(_fileCapability);

        // 8. Power: shutdown/restart/sleep/lock/status
        _powerCapability = new PowerCapability(_logger);
        _nodeClient.RegisterCapability(_powerCapability);

        // 9. Input: keyboard/mouse/macro — enables AI to operate ANY app (games, forms, etc.)
        _inputCapability = new InputCapability(_logger);
        _nodeClient.RegisterCapability(_inputCapability);

        // 10. Clipboard: read/write/clear the Windows clipboard
        _clipboardCapability = new ClipboardCapability(_logger);
        _nodeClient.RegisterCapability(_clipboardCapability);

        // 11. Microphone: list devices + record audio
        _microphoneCapability = new MicrophoneCapability(_logger);
        MicrophoneCapabilityReady?.Invoke(this, _microphoneCapability);
        _nodeClient.RegisterCapability(_microphoneCapability);

        _logger.Info("All 11 capabilities registered: system, canvas, screen, camera, browser, app, file, power, input, clipboard, microphone");
    }

    // Events for the App to hook into platform-specific capability setup
    public event EventHandler<CanvasCapability>? CanvasCapabilityReady;
    public event EventHandler<ScreenCapability>? ScreenCapabilityReady;
    public event EventHandler<CameraCapability>? CameraCapabilityReady;
    public event EventHandler<MicrophoneCapability>? MicrophoneCapabilityReady;

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _logger.Info($"Node status changed: {status}");
        StatusChanged?.Invoke(this, status);
    }

    private void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
    {
        _logger.Info($"Pairing status changed: {args.Status}");
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
