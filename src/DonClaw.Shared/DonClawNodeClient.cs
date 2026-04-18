using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DonClaw.Shared;

/// <summary>
/// DonClaw Node client - connects to an OpenClaw gateway as a node.
/// Handles registration, handshake, and command dispatch to capabilities.
/// </summary>
public class DonClawNodeClient : WebSocketClientBase
{
    private readonly DeviceIdentity _deviceIdentity;
    private readonly List<INodeCapability> _capabilities = new();
    private readonly NodeRegistration _registration;

    private bool _isConnected;
    private string? _nodeId;
    private string? _pendingNonce;
    private bool _isPendingApproval;

    private static readonly JsonSerializerOptions s_ignoreNullOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions s_indentedOptions = new() { WriteIndented = true };
    private static readonly Regex s_commandValidator = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    public event EventHandler<NodeInvokeRequest>? InvokeReceived;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;

    public new bool IsConnected => _isConnected;
    public string? NodeId => _nodeId;
    public string GatewayUrl => GatewayUrlForDisplay;
    public IReadOnlyList<INodeCapability> Capabilities => _capabilities;

    public bool IsPendingApproval => _isPendingApproval;
    public bool IsPaired => !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);

    public string ShortDeviceId => _deviceIdentity.DeviceId.Length > 16
        ? _deviceIdentity.DeviceId.Substring(0, 16)
        : _deviceIdentity.DeviceId;

    public string FullDeviceId => _deviceIdentity.DeviceId;

    protected override int ReceiveBufferSize => 65536;
    protected override string ClientRole => "node-host";

    // ClientId must be "node-host" — required by the OpenClaw gateway protocol
    private const string ClientId = "node-host";

    /// <summary>
    /// One-shot bootstrap token (from "openclaw qr") used to auto-approve
    /// pairing on the very first connect. Null/empty for regular reconnects.
    /// </summary>
    private readonly string? _bootstrapToken;

    /// <summary>Fires when the gateway issues a permanent device token and the bootstrap token is no longer needed.</summary>
    public event EventHandler? BootstrapConsumed;

    public DonClawNodeClient(string gatewayUrl, string token, string dataPath, IOpenClawLogger? logger = null, string? bootstrapToken = null)
        : base(gatewayUrl, token, logger)
    {
        _deviceIdentity = new DeviceIdentity(dataPath, _logger);
        _deviceIdentity.Initialize();
        _bootstrapToken = string.IsNullOrWhiteSpace(bootstrapToken) ? null : bootstrapToken;

        _registration = new NodeRegistration
        {
            Id = _deviceIdentity.DeviceId,
            Version = "1.0.0",
            Platform = "windows",
            DisplayName = $"DonClaw Node ({Environment.MachineName})"
        };
    }

    public void RegisterCapability(INodeCapability capability)
    {
        _capabilities.Add(capability);

        if (!_registration.Capabilities.Contains(capability.Category))
            _registration.Capabilities.Add(capability.Category);

        foreach (var cmd in capability.Commands)
        {
            if (!_registration.Commands.Contains(cmd))
                _registration.Commands.Add(cmd);
        }

        _logger.Info($"Registered capability: {capability.Category} ({capability.Commands.Count} commands)");
    }

    public void SetPermission(string permission, bool value)
    {
        _registration.Permissions[permission] = value;
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        Dispose();
        RaiseStatusChanged(ConnectionStatus.Disconnected);
        _logger.Info("Node disconnected");
        return Task.CompletedTask;
    }

    protected override async Task ProcessMessageAsync(string json)
    {
        try
        {
            _logger.Debug($"[NODE RX] {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                _logger.Warn("[NODE] Message has no 'type' field");
                return;
            }

            var type = typeProp.GetString();
            _logger.Debug($"[NODE] Processing message type: {type}");

            switch (type)
            {
                case "event":
                    await HandleEventAsync(root);
                    break;
                case "res":
                    HandleResponse(root);
                    break;
                case "req":
                    await HandleRequestAsync(root);
                    break;
                default:
                    _logger.Warn($"[NODE] Unknown message type: {type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.Warn($"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Error("Message processing error", ex);
        }
    }

    private async Task HandleEventAsync(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventProp)) return;
        var eventType = eventProp.GetString();

        if (eventType != "health" && eventType != "tick" && eventType != "agent" && eventType != "chat")
            _logger.Info($"[NODE] Received event: {eventType}");

        switch (eventType)
        {
            case "connect.challenge":
                await HandleConnectChallengeAsync(root);
                break;
            case "node.invoke.request":
                await HandleNodeInvokeEventAsync(root);
                break;
        }
    }

    private async Task HandleNodeInvokeEventAsync(JsonElement root)
    {
        _logger.Info("[NODE] Received node.invoke.request event");

        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn("[NODE] node.invoke.request has no payload");
            return;
        }

        string? requestId = null;
        if (payload.TryGetProperty("requestId", out var reqIdProp))
            requestId = reqIdProp.GetString();
        else if (payload.TryGetProperty("id", out var idProp))
            requestId = idProp.GetString();

        if (string.IsNullOrEmpty(requestId))
        {
            _logger.Warn("[NODE] node.invoke.request has no requestId");
            return;
        }

        if (!payload.TryGetProperty("command", out var cmdProp))
        {
            _logger.Warn("[NODE] node.invoke.request has no command");
            await SendNodeInvokeResultAsync(requestId, false, null, "Missing command");
            return;
        }

        var command = cmdProp.GetString() ?? "";

        if (string.IsNullOrEmpty(command) || command.Length > 100 || !s_commandValidator.IsMatch(command))
        {
            _logger.Warn($"[NODE] Invalid command format: {command}");
            await SendNodeInvokeResultAsync(requestId, false, null, "Invalid command format");
            return;
        }

        JsonElement args = default;
        if (payload.TryGetProperty("args", out var argsEl))
        {
            args = argsEl;
        }
        else if (payload.TryGetProperty("paramsJSON", out var paramsJsonProp))
        {
            var paramsJsonStr = paramsJsonProp.GetString();
            if (!string.IsNullOrEmpty(paramsJsonStr))
            {
                try
                {
                    using var doc = JsonDocument.Parse(paramsJsonStr);
                    args = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    _logger.Warn($"[NODE] Failed to parse paramsJSON: {ex.Message}");
                }
            }
        }

        _logger.Info($"[NODE] Invoking command: {command}");

        var request = new NodeInvokeRequest
        {
            Id = requestId,
            Command = command,
            Args = args
        };

        var capability = _capabilities.FirstOrDefault(c => c.CanHandle(command));

        if (capability == null)
        {
            _logger.Warn($"[NODE] No capability registered for command: {command}");
            await SendNodeInvokeResultAsync(requestId, false, null, $"Command not supported: {command}");
            return;
        }

        try
        {
            InvokeReceived?.Invoke(this, request);
            var response = await capability.ExecuteAsync(request);
            response.Id = requestId;
            await SendNodeInvokeResultAsync(requestId, response.Ok, response.Payload, response.Error);
        }
        catch (Exception ex)
        {
            _logger.Error($"[NODE] Command execution failed: {command}", ex);
            await SendNodeInvokeResultAsync(requestId, false, null, $"Execution failed: {ex.Message}");
        }
    }

    private async Task SendNodeInvokeResultAsync(string requestId, bool success, object? payload, string? error)
    {
        var response = new
        {
            type = "req",
            id = Guid.NewGuid().ToString(),
            method = "node.invoke.result",
            @params = new
            {
                id = requestId,
                nodeId = _deviceIdentity.DeviceId,
                ok = success,
                payload = payload,
                error = error == null ? null : new { message = error }
            }
        };

        var json = JsonSerializer.Serialize(response, s_ignoreNullOptions);
        _logger.Info($"[NODE] Sending invoke result for {requestId}: ok={success}");
        await SendRawAsync(json);
    }

    private async Task HandleConnectChallengeAsync(JsonElement root)
    {
        _logger.Info("Received connect challenge, sending node registration...");

        string? nonce = null;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (root.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("nonce", out var nonceProp))
                nonce = nonceProp.GetString();
            if (payload.TryGetProperty("ts", out var tsProp))
                ts = tsProp.GetInt64();
        }

        _pendingNonce = nonce;
        await SendNodeConnectAsync(nonce, ts);
    }

    private async Task SendNodeConnectAsync(string? nonce, long ts)
    {
        string? signature = null;
        var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var authToken = _deviceIdentity.DeviceToken ?? _token;
        var isPaired = !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);
        // Only send the bootstrap token on the very first connect (before pairing).
        var useBootstrap = !isPaired && !string.IsNullOrEmpty(_bootstrapToken);

        if (!string.IsNullOrEmpty(nonce))
        {
            try
            {
                signature = _deviceIdentity.SignPayload(nonce, signedAt, ClientId, authToken);

                _logger.Debug("=== Debug Info ===");
                _logger.Debug($"Device ID: {_deviceIdentity.DeviceId}");
                _logger.Debug($"Public Key: {_deviceIdentity.PublicKeyBase64Url}");
                _logger.Debug($"Client ID: {ClientId}");
                _logger.Debug($"Nonce: {nonce}");
                _logger.Debug($"SignedAt: {signedAt}");
                _logger.Debug($"Signature: {signature}");
                _logger.Debug("==================");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to sign payload: {ex.Message}");
            }
        }

        _logger.Info($"Connecting with Ed25519 device identity (paired: {isPaired}, bootstrap: {useBootstrap})");

        object authPayload = useBootstrap
            ? new { token = authToken, bootstrapToken = _bootstrapToken }
            : new { token = authToken };

        var msg = new
        {
            type = "req",
            id = Guid.NewGuid().ToString(),
            method = "connect",
            @params = new
            {
                minProtocol = 3,
                maxProtocol = 3,
                client = new
                {
                    id = ClientId,
                    version = _registration.Version,
                    platform = _registration.Platform,
                    mode = "node",
                    displayName = _registration.DisplayName
                },
                role = "node",
                scopes = Array.Empty<string>(),
                caps = _registration.Capabilities,
                commands = _registration.Commands,
                permissions = _registration.Permissions,
                auth = authPayload,
                locale = "en-US",
                userAgent = $"donclaw-node/{_registration.Version}",
                device = new
                {
                    id = _deviceIdentity.DeviceId,
                    publicKey = _deviceIdentity.PublicKeyBase64Url,
                    signature = signature,
                    signedAt = signedAt,
                    nonce = nonce
                }
            }
        };

        await SendRawAsync(JsonSerializer.Serialize(msg));
        _logger.Info($"Sent node registration with device ID: {_deviceIdentity.DeviceId.Substring(0, 16)}..., paired: {isPaired}");
    }

    private void HandleResponse(JsonElement root)
    {
        _logger.Debug($"[NODE] HandleResponse - ok: {(root.TryGetProperty("ok", out var okVal) ? okVal.ToString() : "missing")}");

        if (!root.TryGetProperty("payload", out var payload))
        {
            _logger.Warn("[NODE] Response has no payload");
            return;
        }

        var payloadText = payload.ToString();
        _logger.Debug($"[NODE] Response payload: {payloadText.Substring(0, Math.Min(200, payloadText.Length))}...");

        if (payload.TryGetProperty("type", out var t) && t.GetString() == "hello-ok")
        {
            _isConnected = true;

            if (payload.TryGetProperty("nodeId", out var nodeIdProp))
                _nodeId = nodeIdProp.GetString();

            bool gotNewToken = false;
            if (payload.TryGetProperty("auth", out var authPayload) &&
                authPayload.TryGetProperty("deviceToken", out var deviceTokenProp))
            {
                var deviceToken = deviceTokenProp.GetString();
                if (!string.IsNullOrEmpty(deviceToken))
                {
                    gotNewToken = true;
                    var wasWaiting = _isPendingApproval;
                    _isPendingApproval = false;
                    _logger.Info("Received device token - we are now paired!");
                    _deviceIdentity.StoreDeviceToken(deviceToken);
                    // Bootstrap token (if any) has been consumed by the gateway.
                    BootstrapConsumed?.Invoke(this, EventArgs.Empty);
                    PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                        PairingStatus.Paired,
                        _deviceIdentity.DeviceId,
                        wasWaiting ? "Pairing approved!" : null));
                }
            }

            _logger.Info($"Node registered successfully! ID: {_nodeId ?? _deviceIdentity.DeviceId.Substring(0, 16)}");

            if (!gotNewToken)
            {
                if (string.IsNullOrEmpty(_deviceIdentity.DeviceToken))
                {
                    _isPendingApproval = true;
                    _logger.Info($"Not yet paired - run: openclaw devices approve {_deviceIdentity.DeviceId}");
                    PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                        PairingStatus.Pending,
                        _deviceIdentity.DeviceId,
                        $"Run: openclaw devices approve {ShortDeviceId}..."));
                }
                else
                {
                    _isPendingApproval = false;
                    _logger.Info("Already paired with stored device token");
                    PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(
                        PairingStatus.Paired,
                        _deviceIdentity.DeviceId));
                }
            }

            RaiseStatusChanged(ConnectionStatus.Connected);
        }

        if (root.TryGetProperty("ok", out var okProp) && !okProp.GetBoolean())
        {
            var error = "Unknown error";
            var errorCode = "none";
            if (root.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.TryGetProperty("message", out var msgProp))
                    error = msgProp.GetString() ?? error;
                if (errorProp.TryGetProperty("code", out var codeProp))
                    errorCode = codeProp.ToString();
            }
            _logger.Error($"Node registration failed: {error} (code: {errorCode})");
            RaiseStatusChanged(ConnectionStatus.Error);
        }
    }

    private async Task HandleRequestAsync(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var methodProp)) return;
        var method = methodProp.GetString();

        string? id = null;
        if (root.TryGetProperty("id", out var idProp))
            id = idProp.GetString();

        switch (method)
        {
            case "node.invoke":
                await HandleNodeInvokeAsync(root, id);
                break;
            case "ping":
                await SendPongAsync(id);
                break;
            default:
                _logger.Warn($"Unknown request method: {method}");
                if (id != null)
                    await SendErrorResponseAsync(id, $"Unknown method: {method}");
                break;
        }
    }

    private async Task HandleNodeInvokeAsync(JsonElement root, string? requestId)
    {
        if (requestId == null)
        {
            _logger.Warn("node.invoke without request ID");
            return;
        }

        if (!root.TryGetProperty("params", out var paramsEl))
        {
            await SendErrorResponseAsync(requestId, "Missing params");
            return;
        }

        if (!paramsEl.TryGetProperty("command", out var cmdProp))
        {
            await SendErrorResponseAsync(requestId, "Missing command");
            return;
        }

        var command = cmdProp.GetString() ?? "";

        if (string.IsNullOrEmpty(command) || command.Length > 100 || !s_commandValidator.IsMatch(command))
        {
            _logger.Warn($"Invalid command format: {(command.Length > 50 ? command.Substring(0, 50) + "..." : command)}");
            await SendErrorResponseAsync(requestId, "Invalid command format");
            return;
        }

        var args = paramsEl.TryGetProperty("args", out var argsEl) ? argsEl : default;

        _logger.Info($"Received node.invoke: {command}");

        var request = new NodeInvokeRequest
        {
            Id = requestId,
            Command = command,
            Args = args
        };

        var capability = _capabilities.FirstOrDefault(c => c.CanHandle(command));

        if (capability == null)
        {
            _logger.Warn($"No capability registered for command: {command}");
            await SendErrorResponseAsync(requestId, $"Command not supported: {command}");
            return;
        }

        try
        {
            InvokeReceived?.Invoke(this, request);
            var response = await capability.ExecuteAsync(request);
            response.Id = requestId;
            await SendInvokeResponseAsync(response);
        }
        catch (Exception ex)
        {
            _logger.Error($"Command execution failed: {command}", ex);
            await SendErrorResponseAsync(requestId, $"Execution failed: {ex.Message}");
        }
    }

    private async Task SendInvokeResponseAsync(NodeInvokeResponse response)
    {
        var msg = new
        {
            type = "res",
            id = response.Id,
            ok = response.Ok,
            payload = response.Payload,
            error = response.Ok ? null : new { message = response.Error }
        };

        await SendRawAsync(JsonSerializer.Serialize(msg, s_ignoreNullOptions));
        _logger.Info($"Sent invoke response: ok={response.Ok}");
    }

    private async Task SendErrorResponseAsync(string requestId, string error)
    {
        var msg = new
        {
            type = "res",
            id = requestId,
            ok = false,
            error = new { message = error }
        };
        await SendRawAsync(JsonSerializer.Serialize(msg));
    }

    private async Task SendPongAsync(string? requestId)
    {
        if (requestId == null) return;
        var msg = new
        {
            type = "res",
            id = requestId,
            ok = true,
            payload = new { pong = true }
        };
        await SendRawAsync(JsonSerializer.Serialize(msg));
    }

    protected override void OnDisconnected()
    {
        _isConnected = false;
        _isPendingApproval = false;
    }

    protected override void OnError(Exception ex)
    {
        _isConnected = false;
        _isPendingApproval = false;
    }
}
