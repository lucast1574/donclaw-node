using System.Net.WebSockets;
using System.Text;

namespace DonClaw.Shared;

/// <summary>
/// Abstract base class for WebSocket-based gateway clients.
/// Extracts shared connection lifecycle: connect, listen, reconnect, send, dispose.
/// </summary>
public abstract class WebSocketClientBase : IDisposable
{
    private ClientWebSocket? _webSocket;
    private readonly string _gatewayUrl;
    private readonly string? _credentials;
    private CancellationTokenSource _cts;
    private bool _disposed;
    private int _reconnectAttempts;
    private int _reconnectLoopActive;
    private DateTime _firstConnectAttempt;
    private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };

    /// <summary>Max time to keep retrying before giving up. Default 60s.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    protected readonly string _token;
    protected readonly IOpenClawLogger _logger;

    protected string GatewayUrlForDisplay { get; }
    protected bool IsDisposed => _disposed;
    protected bool IsConnected => _webSocket?.State == WebSocketState.Open;
    protected CancellationToken CancellationToken => _cts.Token;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    protected abstract Task ProcessMessageAsync(string json);
    protected abstract int ReceiveBufferSize { get; }
    protected abstract string ClientRole { get; }

    protected virtual Task OnConnectedAsync() => Task.CompletedTask;
    protected virtual void OnDisconnected() { }
    protected virtual void OnError(Exception ex) { }
    protected virtual void OnDisposing() { }
    protected virtual bool ShouldAutoReconnect() => true;

    protected WebSocketClientBase(string gatewayUrl, string token, IOpenClawLogger? logger = null)
    {
        if (string.IsNullOrEmpty(gatewayUrl))
            throw new ArgumentException("Gateway URL is required.", nameof(gatewayUrl));
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("Token is required.", nameof(token));

        _gatewayUrl = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        GatewayUrlForDisplay = GatewayUrlHelper.SanitizeForDisplay(_gatewayUrl);
        _token = token;
        _credentials = GatewayUrlHelper.ExtractCredentials(gatewayUrl);
        _logger = logger ?? NullLogger.Instance;
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync()
    {
        if (_disposed)
        {
            _logger.Debug($"Skipping {ClientRole} connect: client already disposed");
            return;
        }

        try
        {
            RaiseStatusChanged(ConnectionStatus.Connecting);
            if (_reconnectAttempts == 0)
                _firstConnectAttempt = DateTime.UtcNow;
            _logger.Info($"Connecting to {ClientRole}: {GatewayUrlForDisplay}");

            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            var uri = new Uri(_gatewayUrl);
            var originScheme = uri.Scheme == "wss" ? "https" : "http";
            var origin = $"{originScheme}://{uri.Host}:{uri.Port}";
            _webSocket.Options.SetRequestHeader("Origin", origin);

            if (!string.IsNullOrEmpty(_credentials))
            {
                var credentialsToEncode = GatewayUrlHelper.DecodeCredentials(_credentials);
                _webSocket.Options.SetRequestHeader(
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentialsToEncode))}");
            }

            await _webSocket.ConnectAsync(uri, _cts.Token);

            _reconnectAttempts = 0;
            _logger.Info($"{ClientRole} connected, waiting for challenge...");

            await OnConnectedAsync();

            _ = Task.Run(() => ListenForMessagesAsync(), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug($"{ClientRole} connect canceled (likely shutdown)");
        }
        catch (ObjectDisposedException)
        {
            _logger.Debug($"{ClientRole} connect aborted after dispose");
        }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} connection failed", ex);
            RaiseStatusChanged(ConnectionStatus.Error);

            if (!_disposed && !_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
                _ = ReconnectWithBackoffAsync();
        }
    }

    private async Task ListenForMessagesAsync()
    {
        var buffer = new byte[ReceiveBufferSize];
        var sb = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage && sb.Length == 0)
                    {
                        await ProcessMessageAsync(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    else
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (result.EndOfMessage)
                        {
                            await ProcessMessageAsync(sb.ToString());
                            sb.Clear();
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeStatus = _webSocket.CloseStatus?.ToString() ?? "unknown";
                    var closeDesc = _webSocket.CloseStatusDescription ?? "no description";
                    _logger.Info($"Server closed connection: {closeStatus} - {closeDesc}");
                    OnDisconnected();
                    RaiseStatusChanged(ConnectionStatus.Disconnected);
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.Warn("Connection closed prematurely");
            OnDisconnected();
            RaiseStatusChanged(ConnectionStatus.Disconnected);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} listen error", ex);
            OnError(ex);
            RaiseStatusChanged(ConnectionStatus.Error);
        }

        if (!_disposed)
        {
            try
            {
                if (!_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
                    await ReconnectWithBackoffAsync();
            }
            catch (ObjectDisposedException) { }
        }
    }

    protected async Task ReconnectWithBackoffAsync()
    {
        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
            return;

        try
        {
            while (!_disposed && !_cts.Token.IsCancellationRequested && ShouldAutoReconnect())
            {
                // Check timeout
                if (DateTime.UtcNow - _firstConnectAttempt > ConnectionTimeout)
                {
                    _logger.Warn($"{ClientRole} connection timeout after {ConnectionTimeout.TotalSeconds}s — giving up");
                    RaiseStatusChanged(ConnectionStatus.Error);
                    break;
                }

                var delay = BackoffMs[Math.Min(_reconnectAttempts, BackoffMs.Length - 1)];
                _reconnectAttempts++;
                var elapsed = (int)(DateTime.UtcNow - _firstConnectAttempt).TotalSeconds;
                _logger.Warn($"{ClientRole} reconnecting in {delay}ms (attempt {_reconnectAttempts}, {elapsed}s elapsed)");
                RaiseStatusChanged(ConnectionStatus.Connecting);

                await Task.Delay(delay, _cts.Token);

                if (_cts.Token.IsCancellationRequested || _disposed || !ShouldAutoReconnect())
                    break;

                var oldSocket = _webSocket;
                _webSocket = null;
                try { oldSocket?.Dispose(); } catch { }

                await ConnectAsync();

                if (IsConnected)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.Error($"{ClientRole} reconnect failed", ex);
            RaiseStatusChanged(ConnectionStatus.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectLoopActive, 0);
        }
    }

    protected async Task SendRawAsync(string message)
    {
        var ws = _webSocket;
        if (ws?.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (ObjectDisposedException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
        {
            _logger.Warn($"WebSocket send failed (state changed): {ex.Message}");
        }
    }

    protected async Task CloseWebSocketAsync()
    {
        var ws = _webSocket;
        if (ws?.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", System.Threading.CancellationToken.None);
    }

    protected void RaiseStatusChanged(ConnectionStatus status)
        => StatusChanged?.Invoke(this, status);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        OnDisposing();

        try { _cts.Cancel(); } catch { }

        var ws = _webSocket;
        _webSocket = null;
        try { ws?.Dispose(); } catch { }
    }
}
