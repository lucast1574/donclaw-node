using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DonClaw.Shared;

namespace DonClaw.Tray.Services;

/// <summary>
/// Lightweight HTTP server that runs locally on the node.
/// Allows direct invocation of any registered capability without going through the gateway.
/// This enables fast, low-latency control for gaming, automation, etc.
/// 
/// Endpoints:
///   POST /invoke  { "command": "input.mouseClick", "args": { "x": 960, "y": 540 } }
///   GET  /ping
///   GET  /caps     (list all capabilities and commands)
/// </summary>
public class LocalApiServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly IReadOnlyList<INodeCapability> _capabilities;
    private readonly IOpenClawLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private readonly int _port;

    public int Port => _port;

    public LocalApiServer(int port, IReadOnlyList<INodeCapability> capabilities, IOpenClawLogger logger)
    {
        _port = port;
        _capabilities = capabilities;
        _logger = logger;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _listenTask = Task.Run(ListenLoop);
            _logger.Info($"Local API server started on port {_port}");
        }
        catch (HttpListenerException ex)
        {
            _logger.Error($"Failed to start local API server on port {_port}: {ex.Message}");
            _logger.Info("Try running: netsh http add urlacl url=http://+:9800/ user=Everyone");
            
            // Fallback: try localhost only
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
                _listenTask = Task.Run(ListenLoop);
                _logger.Info($"Local API server started on localhost:{_port} (localhost only)");
            }
            catch (Exception ex2)
            {
                _logger.Error($"Failed to start local API server even on localhost: {ex2.Message}");
            }
        }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx));
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.Warn($"API listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // CORS headers for local tools
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        try
        {
            var path = req.Url?.AbsolutePath ?? "/";

            switch (path)
            {
                case "/ping":
                    await WriteJson(res, new { ok = true, version = Program.Version, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    break;

                case "/caps":
                    var caps = _capabilities.Select(c => new
                    {
                        category = c.Category,
                        commands = c.Commands.ToList()
                    }).ToList();
                    await WriteJson(res, new { capabilities = caps, totalCommands = caps.Sum(c => c.commands.Count) });
                    break;

                case "/invoke":
                    if (req.HttpMethod != "POST")
                    {
                        await WriteError(res, 405, "POST required");
                        break;
                    }
                    await HandleInvoke(req, res);
                    break;

                case "/screenshot":
                    await HandleScreenshot(req, res);
                    break;

                default:
                    await WriteError(res, 404, "Not found. Endpoints: /ping, /caps, /invoke, /screenshot");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"API request error: {ex.Message}");
            try { await WriteError(res, 500, ex.Message); } catch { }
        }
    }

    private async Task HandleInvoke(HttpListenerRequest req, HttpListenerResponse res)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("command", out var cmdProp))
        {
            await WriteError(res, 400, "Missing 'command' field");
            return;
        }

        var command = cmdProp.GetString() ?? "";
        var args = root.TryGetProperty("args", out var argsEl) ? argsEl.Clone() : default;

        // Find capability
        var capability = _capabilities.FirstOrDefault(c => c.CanHandle(command));
        if (capability == null)
        {
            await WriteError(res, 404, $"No capability for command: {command}");
            return;
        }

        var request = new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = command,
            Args = args
        };

        var response = await capability.ExecuteAsync(request);

        await WriteJson(res, new { ok = response.Ok, payload = response.Payload, error = response.Error });
    }

    private async Task HandleScreenshot(HttpListenerRequest req, HttpListenerResponse res)
    {
        // Proxy to screen.capture command
        var capability = _capabilities.FirstOrDefault(c => c.CanHandle("screen.capture"));
        if (capability == null)
        {
            await WriteError(res, 404, "Screen capability not available");
            return;
        }

        var request = new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = "screen.capture",
            Args = default
        };

        var response = await capability.ExecuteAsync(request);
        await WriteJson(res, new { ok = response.Ok, payload = response.Payload, error = response.Error });
    }

    private static async Task WriteJson(HttpListenerResponse res, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task WriteError(HttpListenerResponse res, int code, string message)
    {
        var json = JsonSerializer.Serialize(new { error = message });
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = code;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
