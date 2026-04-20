using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace DonClaw.Tray.Services;

/// <summary>
/// Ephemeral HTTP server on a random port that listens for the OAuth callback
/// from DonClaw's web login flow. The flow:
///   1. Node opens browser → https://donclaw.com/auth/node-callback?port={port}
///   2. User logs in on the web (email/pass or Google)
///   3. Web redirects to http://localhost:{port}/callback?token=JWT&amp;email=...
///   4. This server receives the callback and extracts the token
///   5. Server shuts down
/// </summary>
public class AuthCallbackServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    public int Port { get; private set; }

    public AuthCallbackServer()
    {
        // Find an available port at construction time so callers can read Port immediately
        var temp = new TcpListener(IPAddress.Loopback, 0);
        temp.Start();
        Port = ((IPEndPoint)temp.LocalEndpoint).Port;
        temp.Stop();
    }

    /// <summary>
    /// Start listening and wait for the callback.
    /// Returns (token, email, userId) on success, or throws on timeout/cancel.
    /// </summary>
    public async Task<(string token, string email, string userId)> WaitForCallbackAsync(
        TimeSpan timeout)
    {
        var prefix = $"http://localhost:{Port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        _cts = new CancellationTokenSource(timeout);

        try
        {
            var contextTask = _listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask,
                Task.Delay(timeout, _cts.Token));

            if (completed != contextTask)
                throw new TimeoutException("Login timed out. Please try again.");

            var ctx = await contextTask;
            var query = ctx.Request.Url?.Query ?? "";
            var parms = HttpUtility.ParseQueryString(query);

            var token = parms["token"] ?? "";
            var email = parms["email"] ?? "";
            var userId = parms["userId"] ?? "";

            // Send a nice response to the browser
            var html = @"<html><head><meta charset='utf-8'><style>
                body{font-family:system-ui;background:#111;color:#fff;display:flex;justify-content:center;align-items:center;height:100vh;margin:0}
                .card{text-align:center;padding:40px}
                h1{color:#34d399;margin-bottom:8px}
                p{color:#999}
                </style></head><body><div class='card'>
                <h1>✓ Conectado</h1>
                <p>Puedes cerrar esta ventana y volver a DonClaw Node.</p>
                </div></body></html>";
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();

            if (string.IsNullOrEmpty(token))
                throw new Exception("No token received from login.");

            return (token, email, userId);
        }
        finally
        {
            try { _listener.Stop(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
    }
}
