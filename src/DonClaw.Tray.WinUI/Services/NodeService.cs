using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DonClaw.Shared;
using DonClaw.Shared.Capabilities;

namespace DonClaw.Tray.Services;

public class NodeService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly List<INodeCapability> _capabilities = new();
    private DonClawNodeClient? _client;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<SystemNotifyArgs>? NotifyRequested;
    /// <summary>Raised when the gateway has issued a permanent device token and the one-shot bootstrap token must be cleared from settings.</summary>
    public event EventHandler? BootstrapConsumed;

    public ConnectionStatus CurrentStatus { get; private set; } = ConnectionStatus.Disconnected;
    public bool IsConnected => _client?.IsConnected ?? false;
    public string? ShortDeviceId => _client?.ShortDeviceId;
    public string? FullDeviceId => _client?.FullDeviceId;
    public bool IsPendingApproval => _client?.IsPendingApproval ?? false;
    public bool IsPaired => _client?.IsPaired ?? false;

    public NodeService(IOpenClawLogger logger)
    {
        _logger = logger;
        InitializeCapabilities();
    }

    /// <summary>Get all registered capabilities (for local API server).</summary>
    public IReadOnlyList<INodeCapability> GetCapabilities() => _capabilities.AsReadOnly();

    public async Task ConnectAsync(string host, string token, string? bootstrapToken = null)
    {
        Disconnect();

        // Normalize URL
        if (!host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            host = host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? host.Replace("https://", "wss://") : "ws://" + host.TrimStart("http://".ToCharArray());
        }

        _logger.Info($"Connecting to {host}" + (string.IsNullOrEmpty(bootstrapToken) ? "" : " (with bootstrap token)"));
        _client = new DonClawNodeClient(host, token, SettingsManager.DataPath, _logger, bootstrapToken);
        _client.StatusChanged += (_, s) => { CurrentStatus = s; StatusChanged?.Invoke(this, s); };
        _client.PairingStatusChanged += (_, a) => PairingStatusChanged?.Invoke(this, a);
        _client.BootstrapConsumed += (_, _) => BootstrapConsumed?.Invoke(this, EventArgs.Empty);
        _client.InvokeReceived += (_, r) => _logger.Info($"Invoke: {r.Command}");

        RegisterCapabilitiesWithClient();
        await _client.ConnectAsync();
    }

    public void Disconnect()
    {
        if (_client != null) { _client.Dispose(); _client = null; }
        CurrentStatus = ConnectionStatus.Disconnected;
        StatusChanged?.Invoke(this, ConnectionStatus.Disconnected);
    }

    /// <summary>Create all capabilities upfront (available to local API immediately).</summary>
    private void InitializeCapabilities()
    {
        // 1. System
        var sys = new SystemCapability(_logger);
        sys.SetCommandRunner(new LocalCommandRunner(_logger));
        sys.SetApprovalPolicy(new ExecApprovalPolicy(SettingsManager.DataPath, _logger));
        sys.NotifyRequested += (_, a) => NotifyRequested?.Invoke(this, a);
        _capabilities.Add(sys);

        // 2. Screen
        _capabilities.Add(new ScreenCapability(_logger));

        // 3. Camera
        _capabilities.Add(new CameraCapability(_logger));

        // 4. Canvas
        _capabilities.Add(new CanvasCapability(_logger));

        // 5. Browser
        _capabilities.Add(new BrowserCapability(_logger));

        // 6. App
        _capabilities.Add(new AppCapability(_logger));

        // 7. File
        _capabilities.Add(new FileCapability(_logger));

        // 8. Power
        _capabilities.Add(new PowerCapability(_logger));

        // 9. Input
        _capabilities.Add(new InputCapability(_logger));

        // 10. Clipboard
        _capabilities.Add(new ClipboardCapability(_logger));

        // 11. Microphone
        _capabilities.Add(new MicrophoneCapability(_logger));

        _logger.Info($"Initialized {_capabilities.Count} capabilities");
    }

    /// <summary>Register already-created capabilities with the WS client.</summary>
    private void RegisterCapabilitiesWithClient()
    {
        if (_client == null) return;
        foreach (var cap in _capabilities)
            _client.RegisterCapability(cap);
        _logger.Info($"Registered {_capabilities.Count} capabilities with client");
    }

    public void Dispose() { _client?.Dispose(); }
}
