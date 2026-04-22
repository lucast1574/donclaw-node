using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
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

    /// <summary>Raised when the gateway rejects our auth (e.g. bootstrap expired). Tray can fetch a fresh setup code and reconnect.</summary>
    public event EventHandler<string>? AuthRejected;

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
        _client.AuthRejected += (_, reason) => AuthRejected?.Invoke(this, reason);
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

    /// <summary>
    /// Wire the ScreenCapability event handlers to the ScreenshotCache so that
    /// screen.capture / screen.list work both via gateway invoke AND local API.
    /// Must be called after the ScreenshotCache is created and started.
    /// </summary>
    public void WireScreenCapture(ScreenshotCache cache)
    {
        var screenCap = _capabilities.OfType<ScreenCapability>().FirstOrDefault();
        if (screenCap == null)
        {
            _logger.Warn("WireScreenCapture: ScreenCapability not found");
            return;
        }

        screenCap.CaptureRequested += (args) =>
        {
            // Honor caller-provided maxWidth/quality so requesters that want
            // smaller payloads for bandwidth/LLM-latency actually get them.
            // (Previously args were silently discarded and everything came back
            // as the cache's default 960px@Q55 — ~90 KB per frame.)
            var (jpeg, w, h) = cache.CaptureWithParams(args.MaxWidth, args.Quality);

            return Task.FromResult(new ScreenCaptureResult
            {
                Format = "jpeg",
                Width = w,
                Height = h,
                Base64 = Convert.ToBase64String(jpeg)
            });
        };

        // Note: PerceiveRequested is wired separately in WirePerceive() because
        // it needs the TextFinder (created outside NodeService in Program.cs).

        screenCap.ListRequested += async () =>
        {
            var screens = Screen.AllScreens;
            var result = new ScreenInfo[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                result[i] = new ScreenInfo
                {
                    Index = i,
                    Name = s.DeviceName,
                    IsPrimary = s.Primary,
                    Width = s.Bounds.Width,
                    Height = s.Bounds.Height,
                    X = s.Bounds.X,
                    Y = s.Bounds.Y,
                    WorkingX = s.WorkingArea.X,
                    WorkingY = s.WorkingArea.Y,
                    WorkingWidth = s.WorkingArea.Width,
                    WorkingHeight = s.WorkingArea.Height
                };
            }
            return result;
        };

        _logger.Info("ScreenCapability wired to ScreenshotCache");
    }

    /// <summary>
    /// Wire the ScreenCapability.PerceiveRequested event to the TextFinder
    /// so that `screen.perceive` (via gateway invoke OR LocalApi) returns
    /// OCR-with-coords + a tiny thumbnail in a single capture. Must be called
    /// after the TextFinder is created in Program.cs.
    /// </summary>
    public void WirePerceive(TextFinder finder)
    {
        var screenCap = _capabilities.OfType<ScreenCapability>().FirstOrDefault();
        if (screenCap == null)
        {
            _logger.Warn("WirePerceive: ScreenCapability not found");
            return;
        }

        screenCap.PerceiveRequested += async (args) =>
        {
            var r = await finder.PerceiveScreen(args.ThumbMaxWidth, args.ThumbQuality, args.IncludeThumbnail);

            // Shape the OCR lines to match the LocalApi /ocr/perceive response
            // (text + words with coords). Kept identical so callers can use
            // the same parsing code whether they hit gateway invoke or LocalApi.
            var lines = r.Lines.Select(l => new
            {
                text = l.Text,
                words = l.Words.Select(w => new { w.Text, w.X, w.Y }).ToArray(),
            }).ToArray();

            return new ScreenPerceiveResult
            {
                ScreenWidth = r.ScreenWidth,
                ScreenHeight = r.ScreenHeight,
                Lines = lines,
                ThumbnailBase64 = r.ThumbnailJpeg.Length > 0
                    ? Convert.ToBase64String(r.ThumbnailJpeg)
                    : "",
                ThumbnailWidth = r.ThumbnailWidth,
                ThumbnailHeight = r.ThumbnailHeight,
            };
        };

        _logger.Info("ScreenCapability wired to TextFinder (perceive)");
    }

    public void Dispose() { _client?.Dispose(); }
}
