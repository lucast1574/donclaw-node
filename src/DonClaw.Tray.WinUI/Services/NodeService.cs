using System;
using System.Threading.Tasks;
using DonClaw.Shared;
using DonClaw.Shared.Capabilities;

namespace DonClaw.Tray.Services;

public class NodeService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private DonClawNodeClient? _client;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<SystemNotifyArgs>? NotifyRequested;

    public ConnectionStatus CurrentStatus { get; private set; } = ConnectionStatus.Disconnected;
    public bool IsConnected => _client?.IsConnected ?? false;
    public string? ShortDeviceId => _client?.ShortDeviceId;
    public string? FullDeviceId => _client?.FullDeviceId;
    public bool IsPendingApproval => _client?.IsPendingApproval ?? false;
    public bool IsPaired => _client?.IsPaired ?? false;

    public NodeService(IOpenClawLogger logger) { _logger = logger; }

    public async Task ConnectAsync(string host, string token)
    {
        Disconnect();

        // Normalize URL
        if (!host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            host = host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? host.Replace("https://", "wss://") : "ws://" + host.TrimStart("http://".ToCharArray());
        }

        _logger.Info($"Connecting to {host}");
        _client = new DonClawNodeClient(host, token, SettingsManager.DataPath, _logger);
        _client.StatusChanged += (_, s) => { CurrentStatus = s; StatusChanged?.Invoke(this, s); };
        _client.PairingStatusChanged += (_, a) => PairingStatusChanged?.Invoke(this, a);
        _client.InvokeReceived += (_, r) => _logger.Info($"Invoke: {r.Command}");

        RegisterAllCapabilities();
        await _client.ConnectAsync();
    }

    public void Disconnect()
    {
        if (_client != null) { _client.Dispose(); _client = null; }
        CurrentStatus = ConnectionStatus.Disconnected;
    }

    private void RegisterAllCapabilities()
    {
        if (_client == null) return;

        // 1. System
        var sys = new SystemCapability(_logger);
        sys.SetCommandRunner(new LocalCommandRunner(_logger));
        sys.SetApprovalPolicy(new ExecApprovalPolicy(SettingsManager.DataPath, _logger));
        sys.NotifyRequested += (_, a) => NotifyRequested?.Invoke(this, a);
        _client.RegisterCapability(sys);

        // 2. Screen
        _client.RegisterCapability(new ScreenCapability(_logger));

        // 3. Camera
        _client.RegisterCapability(new CameraCapability(_logger));

        // 4. Canvas
        _client.RegisterCapability(new CanvasCapability(_logger));

        // 5. Browser
        _client.RegisterCapability(new BrowserCapability(_logger));

        // 6. App
        _client.RegisterCapability(new AppCapability(_logger));

        // 7. File
        _client.RegisterCapability(new FileCapability(_logger));

        // 8. Power
        _client.RegisterCapability(new PowerCapability(_logger));

        // 9. Input
        _client.RegisterCapability(new InputCapability(_logger));

        // 10. Clipboard
        _client.RegisterCapability(new ClipboardCapability(_logger));

        // 11. Microphone
        _client.RegisterCapability(new MicrophoneCapability(_logger));

        _logger.Info("Registered 11 capabilities");
    }

    public void Dispose() { _client?.Dispose(); }
}
