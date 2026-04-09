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
/// HYBRID APPROACH: commands for navigation, vision for in-app interaction.
///
/// ── Fast command endpoints (no screenshots, no tokens) ──
///   POST /open     {"url": "..."}        → open URL in browser
///   POST /launch   {"path": "..."}       → launch app
///   POST /focus    {"name": "chrome"}    → focus app window
///   POST /notify   {"title","body"}      → show notification
///
/// ── Vision endpoints (for in-app interaction) ──
///   GET  /screenshot?q=60&amp;w=960&amp;fmt=webp  → compressed screenshot
///   GET  /region?x=0&amp;y=0&amp;w=500&amp;h=300&amp;q=50 → capture region only
///   POST /click    {"x","y"}             → instant click
///   POST /smooth   {"x","y"}             → smooth visible click
///   POST /move     {"x","y"}             → move cursor
///   POST /key      {"key": "enter"}      → press key
///   POST /type     {"text": "..."}        → type text
///   POST /macro    {"steps": [...]}       → multi-step macro
///
/// ── General ──
///   GET  /ping, /caps, /cursor
///   POST /invoke   {"command","args"}     → invoke any capability
/// </summary>
public class LocalApiServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly IReadOnlyList<INodeCapability> _capabilities;
    private readonly IOpenClawLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private Task? _listenTask;

    /// <summary>Full-screen screenshot: (quality, maxWidth) → JPEG/WebP bytes</summary>
    public Func<int, int, string, Task<byte[]>>? ScreenshotProvider { get; set; }

    /// <summary>Region screenshot: (x, y, w, h, quality, format) → bytes</summary>
    public Func<int, int, int, int, int, string, Task<byte[]>>? RegionScreenshotProvider { get; set; }

    /// <summary>Screenshot cache for instant screenshots.</summary>
    public ScreenshotCache? Cache { get; set; }

    /// <summary>Text finder for local OCR.</summary>
    public TextFinder? Finder { get; set; }

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

            // ── Vision endpoints ──
            case "/screenshot":
                await HandleScreenshot(stream, query);
                break;

            case "/region":
                await HandleRegion(stream, query);
                break;

            case "/invoke":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleInvoke(stream, body);
                break;

            // ── Fast command endpoints (no vision needed) ──
            case "/open":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "browser.open");
                break;

            case "/launch":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "app.launch");
                break;

            case "/focus":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "app.focus");
                break;

            case "/notify":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "system.notify");
                break;

            // ── Input shortcuts ──
            case "/click":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.mouseClick");
                break;

            case "/smooth":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleShortcut(stream, body, "input.smoothClick");
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

            // ── Hybrid Smart endpoints (local OCR, no AI vision needed) ──
            case "/find":
                await HandleFind(stream, method, body, query);
                break;

            case "/act":
                if (method != "POST") { await SendError(stream, 405, "POST required"); break; }
                await HandleAct(stream, body);
                break;

            case "/ocr":
                await HandleOcr(stream);
                break;

            case "/cached":
                await HandleCachedScreenshot(stream);
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

    /// <summary>
    /// Screenshot endpoint with compression options.
    /// Params: q (quality 1-100), w (max width), fmt (jpeg/webp/png)
    /// Lower quality + smaller width = fewer tokens when sent to AI.
    /// </summary>
    private async Task HandleScreenshot(NetworkStream stream, Dictionary<string, string> query)
    {
        if (ScreenshotProvider == null)
        {
            await SendError(stream, 503, "Screenshot provider not configured");
            return;
        }

        var quality = query.TryGetValue("q", out var qStr) && int.TryParse(qStr, out var q) ? q
                    : query.TryGetValue("quality", out var q2Str) && int.TryParse(q2Str, out var q2) ? q2 : 60;
        var maxWidth = query.TryGetValue("w", out var wStr) && int.TryParse(wStr, out var w) ? w
                     : query.TryGetValue("width", out var w2Str) && int.TryParse(w2Str, out var w2) ? w2 : 960;
        var fmt = query.TryGetValue("fmt", out var fStr) ? fStr : "jpeg";

        try
        {
            var bytes = await ScreenshotProvider(quality, maxWidth, fmt);
            var contentType = fmt switch { "webp" => "image/webp", "png" => "image/png", _ => "image/jpeg" };
            await SendResponse(stream, 200, contentType, bytes);
        }
        catch (Exception ex)
        {
            await SendError(stream, 500, $"Screenshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Region capture — only capture a specific area of the screen.
    /// Much faster and smaller than full screen. Use this for reading specific UI elements.
    /// Params: x, y, w, h, q (quality), fmt (jpeg/webp)
    /// </summary>
    private async Task HandleRegion(NetworkStream stream, Dictionary<string, string> query)
    {
        if (RegionScreenshotProvider == null)
        {
            await SendError(stream, 503, "Region screenshot not configured");
            return;
        }

        var x = query.TryGetValue("x", out var xStr) && int.TryParse(xStr, out var xv) ? xv : 0;
        var y = query.TryGetValue("y", out var yStr) && int.TryParse(yStr, out var yv) ? yv : 0;
        var w = query.TryGetValue("w", out var wStr) && int.TryParse(wStr, out var wv) ? wv : 400;
        var h = query.TryGetValue("h", out var hStr) && int.TryParse(hStr, out var hv) ? hv : 300;
        var quality = query.TryGetValue("q", out var qStr) && int.TryParse(qStr, out var q) ? q : 70;
        var fmt = query.TryGetValue("fmt", out var fStr) ? fStr : "jpeg";

        try
        {
            var bytes = await RegionScreenshotProvider(x, y, w, h, quality, fmt);
            var contentType = fmt switch { "webp" => "image/webp", "png" => "image/png", _ => "image/jpeg" };
            await SendResponse(stream, 200, contentType, bytes);
        }
        catch (Exception ex)
        {
            await SendError(stream, 500, $"Region capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Find text on screen using local OCR. No screenshot sent to AI.
    /// GET /find?q=Play  or  POST /find {"text": "Play"}
    /// Returns coordinates ready for clicking.
    /// </summary>
    private async Task HandleFind(NetworkStream stream, string method, string body, Dictionary<string, string> query)
    {
        if (Finder == null)
        {
            await SendError(stream, 503, "OCR not available");
            return;
        }

        string searchText;
        bool exact = false;

        if (method == "POST" && !string.IsNullOrWhiteSpace(body))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            searchText = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            exact = root.TryGetProperty("exact", out var e) && e.GetBoolean();
        }
        else
        {
            searchText = query.TryGetValue("q", out var q) ? q : "";
            exact = query.TryGetValue("exact", out var e) && e == "true";
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            await SendError(stream, 400, "Missing search text (q= or text field)");
            return;
        }

        var matches = await Finder.FindText(searchText, exact);
        await SendJson(stream, new { ok = true, query = searchText, count = matches.Count, matches });
    }

    /// <summary>
    /// Act: find text on screen + click it in one command.
    /// POST /act {"text": "Play", "smooth": true}
    /// This is the power endpoint — one call does: OCR find → smooth click.
    /// </summary>
    private async Task HandleAct(NetworkStream stream, string body)
    {
        if (Finder == null)
        {
            await SendError(stream, 503, "OCR not available");
            return;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var searchText = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var smooth = root.TryGetProperty("smooth", out var s) ? s.GetBoolean() : true;
        var index = root.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
        var button = root.TryGetProperty("button", out var btn) ? btn.GetString() ?? "left" : "left";

        if (string.IsNullOrWhiteSpace(searchText))
        {
            await SendError(stream, 400, "Missing text field");
            return;
        }

        // Find
        var matches = await Finder.FindText(searchText);
        if (matches.Count == 0)
        {
            await SendJson(stream, new { ok = false, error = $"Text '{searchText}' not found on screen", matches = 0 });
            return;
        }

        var target = index < matches.Count ? matches[index] : matches[0];

        // Click
        var clickCommand = smooth ? "input.smoothClick" : "input.mouseClick";
        var cap = _capabilities.FirstOrDefault(c => c.CanHandle(clickCommand));
        if (cap == null)
        {
            await SendError(stream, 500, "Input capability not found");
            return;
        }

        var argsJson = $"{{\"x\":{target.X},\"y\":{target.Y},\"button\":\"{button}\",\"moveDurationMs\":300}}";
        using var argsDoc = System.Text.Json.JsonDocument.Parse(argsJson);

        var req = new DonClaw.Shared.NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = clickCommand,
            Args = argsDoc.RootElement.Clone()
        };
        var res = await cap.ExecuteAsync(req);

        await SendJson(stream, new
        {
            ok = res.Ok,
            action = "click",
            target = new { text = target.Text, x = target.X, y = target.Y },
            smooth,
            totalMatches = matches.Count
        });
    }

    /// <summary>Full OCR dump of everything visible on screen.</summary>
    private async Task HandleOcr(NetworkStream stream)
    {
        if (Finder == null)
        {
            await SendError(stream, 503, "OCR not available");
            return;
        }

        var dump = await Finder.DumpScreenText();
        await SendJson(stream, new
        {
            ok = true,
            screenWidth = dump.ScreenWidth,
            screenHeight = dump.ScreenHeight,
            lines = dump.Lines.Select(l => new { text = l.Text, words = l.Words.Select(w => new { w.Text, w.X, w.Y }) })
        });
    }

    /// <summary>Return the cached screenshot instantly (no capture delay).</summary>
    private async Task HandleCachedScreenshot(NetworkStream stream)
    {
        if (Cache == null)
        {
            await SendError(stream, 503, "Screenshot cache not running");
            return;
        }

        var jpeg = Cache.GetLatestJpeg();
        if (jpeg == null || jpeg.Length == 0)
        {
            await SendError(stream, 503, "No cached screenshot yet");
            return;
        }

        await SendResponse(stream, 200, "image/jpeg", jpeg);
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
