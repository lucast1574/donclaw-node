using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DonClaw.Shared;

namespace DonClaw.Tray.Services;

/// <summary>
/// Lightweight HTTP server using raw TcpListener (no admin privileges needed).
/// Exposes all DonClaw Node capabilities over a local HTTP API.
/// 
/// Endpoints:
///   GET  /ping                        → health check
///   GET  /caps                        → list capabilities and commands
///   GET  /screenshot?quality=70&amp;scale=0.5  → JPEG screenshot
///   POST /invoke                      → invoke any capability command
///   POST /click                       → shortcut: click at x,y
///   POST /move                        → shortcut: move cursor to x,y
///   POST /key                         → shortcut: press a key
///   POST /type                        → shortcut: type text
/// </summary>
public class LocalApiServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IReadOnlyList<INodeCapability> _capabilities;
    private readonly IOpenClawLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private Task? _listenTask;

    // Screenshot function injected from WinForms context (needs UI thread for some capture methods)
    public Func<int, int, Task<byte[]>>? ScreenshotProvider { get; set; }

    public int Port => _port;

    public LocalApiServer(int port, IReadOnlyList<INodeCapability> capabilities, IOpenClawLogger logger)
    {
        _port = port;
        _capabilities = capabilities;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _listenTask = Task.Run(ListenLoop);
            _logger.Info($"[LocalAPI] Server started on port {_port} (all interfaces)");
        }
        catch (Exception ex)
        {
            _logger.Error($"[LocalAPI] Failed to start on port {_port}: {ex.Message}");
        }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClient(client));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.Warn($"[LocalAPI] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using var _ = client;
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;

        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);

            // Read request line
            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;

            var method = parts[0];
            var rawPath = parts[1];

            // Read headers
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                    headers[line[..colonIdx].Trim()] = line[(colonIdx + 1)..].Trim();
            }

            // Read body if present
            string body = "";
            if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
            {
                var buf = new char[cl];
                var read = 0;
                while (read < cl)
                {
                    var n = await reader.ReadAsync(buf, read, cl - read);
                    if (n == 0) break;
                    read += n;
                }
                body = new string(buf, 0, read);
            }

            // Parse path and query
            var qIdx = rawPath.IndexOf('?');
            var path = qIdx >= 0 ? rawPath[..qIdx] : rawPath;
            var query = qIdx >= 0 ? ParseQuery(rawPath[(qIdx + 1)..]) : new Dictionary<string, string>();

            // Handle CORS preflight
            if (method == "OPTIONS")
            {
                await SendResponse(stream, 204, null, "");
                return;
            }

            // Route
            await Route(stream, method, path, query, body);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[LocalAPI] Request error: {ex.Message}");
        }
    }

    private async Task Route(NetworkStream stream, string method, string path, Dictionary<string, string> query, string body)
    {
        switch (path)
        {
            case "/ping":
                await SendJson(stream, new { ok = true, version = Program.Version, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                break;

            case "/caps":
                var caps = _capabilities.Select(c => new { category = c.Category, commands = c.Commands.ToList() }).ToList();
                await SendJson(stream, new { capabilities = caps, totalCommands = caps.Sum(c => c.commands.Count) });
                break;

            case "/screenshot":
                await HandleScreenshot(stream, query);
                break;

            case "/invoke":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleInvoke(stream, body);
                break;

            // ── Shortcuts for common actions ──
            case "/click":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.mouseClick");
                break;

            case "/move":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.mouseMove");
                break;

            case "/key":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.keyPress");
                break;

            case "/type":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.type");
                break;

            case "/cursor":
                await HandleShortcut(stream, "", "input.getCursorPos");
                break;

            case "/macro":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.macro");
                break;

            default:
                await SendError(stream, 404, $"Not found. Try: /ping, /caps, /screenshot, /invoke, /click, /move, /key, /type, /cursor, /macro");
                break;
        }
    }

    private async Task HandleInvoke(NetworkStream stream, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var command = root.TryGetProperty("command", out var cmdP) ? cmdP.GetString() : null;
            if (string.IsNullOrEmpty(command))
            {
                await SendError(stream, 400, "Missing 'command'");
                return;
            }

            var args = root.TryGetProperty("args", out var argsEl) ? argsEl.Clone() : default;

            var cap = _capabilities.FirstOrDefault(c => c.CanHandle(command));
            if (cap == null)
            {
                await SendError(stream, 404, $"No capability for: {command}");
                return;
            }

            var req = new NodeInvokeRequest { Id = Guid.NewGuid().ToString(), Command = command, Args = args };
            var res = await cap.ExecuteAsync(req);
            await SendJson(stream, new { ok = res.Ok, payload = res.Payload, error = res.Error });
        }
        catch (JsonException)
        {
            await SendError(stream, 400, "Invalid JSON");
        }
    }

    /// <summary>Shortcut handler — maps body JSON directly as args to a command.</summary>
    private async Task HandleShortcut(NetworkStream stream, string body, string command)
    {
        var args = default(JsonElement);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                args = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                await SendError(stream, 400, "Invalid JSON");
                return;
            }
        }

        var cap = _capabilities.FirstOrDefault(c => c.CanHandle(command));
        if (cap == null)
        {
            await SendError(stream, 404, $"No capability for: {command}");
            return;
        }

        var req = new NodeInvokeRequest { Id = Guid.NewGuid().ToString(), Command = command, Args = args };
        var res = await cap.ExecuteAsync(req);
        await SendJson(stream, new { ok = res.Ok, payload = res.Payload, error = res.Error });
    }

    private async Task HandleScreenshot(NetworkStream stream, Dictionary<string, string> query)
    {
        if (ScreenshotProvider == null)
        {
            await SendError(stream, 503, "Screenshot provider not configured");
            return;
        }

        var quality = query.TryGetValue("quality", out var qStr) && int.TryParse(qStr, out var q) ? q : 70;
        var scale = query.TryGetValue("scale", out var sStr) && int.TryParse(sStr, out var s) ? s : 100;

        try
        {
            var jpegBytes = await ScreenshotProvider(quality, scale);
            await SendResponse(stream, 200, "image/jpeg", jpegBytes);
        }
        catch (Exception ex)
        {
            await SendError(stream, 500, $"Screenshot failed: {ex.Message}");
        }
    }

    #region HTTP Response helpers

    private static async Task SendJson(NetworkStream stream, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await SendResponse(stream, 200, "application/json", Encoding.UTF8.GetBytes(json));
    }

    private static async Task SendError(NetworkStream stream, int code, string message)
    {
        var json = JsonSerializer.Serialize(new { error = message });
        await SendResponse(stream, code, "application/json", Encoding.UTF8.GetBytes(json));
    }

    private static async Task SendResponse(NetworkStream stream, int code, string? contentType, string body)
    {
        await SendResponse(stream, code, contentType, Encoding.UTF8.GetBytes(body));
    }

    private static async Task SendResponse(NetworkStream stream, int code, string? contentType, byte[] body)
    {
        var statusText = code switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", 500 => "Internal Server Error", 503 => "Service Unavailable", _ => "OK" };

        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {statusText}\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        sb.Append("Connection: close\r\n");
        if (contentType != null)
            sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);
        if (body.Length > 0)
            await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(string qs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
                d[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return d;
    }

    #endregion

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
