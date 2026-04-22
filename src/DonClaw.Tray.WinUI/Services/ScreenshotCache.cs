using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DonClaw.Tray.Services;

/// <summary>
/// Continuously captures screenshots in the background and caches them.
/// When a screenshot is requested, returns the latest cached one instantly
/// instead of waiting for a new capture (~50ms vs ~300ms+).
/// 
/// This eliminates the capture latency from the AI control loop.
/// </summary>
public class ScreenshotCache : IDisposable
{
    private byte[]? _cachedFull;         // Full-res bitmap bytes (raw)
    private byte[]? _cachedJpeg;         // Pre-compressed JPEG
    private int _cachedWidth;
    private int _cachedHeight;
    private long _cachedAtMs;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _captureLoop;

    /// <summary>How often to capture (ms). Lower = more CPU, fresher screenshots.</summary>
    public int IntervalMs { get; set; } = 500;

    /// <summary>Pre-compressed JPEG quality for the cache.</summary>
    public int DefaultQuality { get; set; } = 55;

    /// <summary>Pre-compressed max width.</summary>
    public int DefaultMaxWidth { get; set; } = 960;

    /// <summary>Age of the current cached screenshot in ms.</summary>
    public long CacheAgeMs
    {
        get
        {
            lock (_lock)
                return _cachedAtMs == 0 ? -1 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _cachedAtMs;
        }
    }

    public void Start()
    {
        _captureLoop = Task.Run(CaptureLoop);
    }

    private async Task CaptureLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                CaptureNow();
            }
            catch { }

            try { await Task.Delay(IntervalMs, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Force an immediate capture (also called by the loop).</summary>
    public void CaptureNow()
    {
        var screen = Screen.PrimaryScreen!;
        var w = screen.Bounds.Width;
        var h = screen.Bounds.Height;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
        }

        // Compress to JPEG at default settings
        var jpeg = CompressToJpeg(bmp, DefaultQuality, DefaultMaxWidth);

        lock (_lock)
        {
            _cachedJpeg = jpeg;
            _cachedWidth = w;
            _cachedHeight = h;
            _cachedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>Get the latest cached JPEG. Returns instantly.</summary>
    public byte[]? GetLatestJpeg()
    {
        lock (_lock) return _cachedJpeg;
    }

    /// <summary>Get a fresh screenshot (captures now if cache is older than maxAgeMs).</summary>
    public byte[] GetFreshJpeg(int maxAgeMs = 300)
    {
        if (CacheAgeMs < 0 || CacheAgeMs > maxAgeMs)
            CaptureNow();
        lock (_lock) return _cachedJpeg ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Capture a screenshot on-demand with caller-specified quality and maxWidth.
    /// Falls back to the pre-compressed cache when the requested params match
    /// cache defaults (zero extra CPU); otherwise captures fresh and compresses
    /// with the requested params.
    ///
    /// Used by screen.capture so callers that pass small maxWidth/quality for
    /// bandwidth savings actually get small payloads (before this, params were
    /// ignored and everything was 960px@Q55 regardless).
    /// </summary>
    public (byte[] Jpeg, int Width, int Height) CaptureWithParams(int maxWidth, int quality, int maxCacheAgeMs = 200)
    {
        // Normalize bad inputs to safe defaults instead of throwing.
        if (maxWidth <= 0) maxWidth = DefaultMaxWidth;
        quality = Math.Clamp(quality, 1, 100);

        // Fast path: if caller wants exactly what the cache holds and it's fresh,
        // skip the recapture + recompress.
        if (maxWidth == DefaultMaxWidth && quality == DefaultQuality)
        {
            if (CacheAgeMs < 0 || CacheAgeMs > maxCacheAgeMs)
                CaptureNow();
            lock (_lock)
                return (_cachedJpeg ?? Array.Empty<byte>(), _cachedWidth, _cachedHeight);
        }

        // Slow path: capture fresh and compress with the requested params.
        var screen = Screen.PrimaryScreen!;
        var w = screen.Bounds.Width;
        var h = screen.Bounds.Height;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
        }

        var jpeg = CompressToJpeg(bmp, quality, maxWidth);
        return (jpeg, w, h);
    }

    /// <summary>
    /// Capture a screenshot on-demand and encode it with the requested format
    /// ("jpeg" or "webp"). WebP is ∼25-35% smaller than JPEG at visually
    /// equivalent quality — useful for AI control loops where every KB over
    /// the wire costs tokens/latency.
    ///
    /// WebP path is ASYNC because Windows.Graphics.Imaging.BitmapEncoder is
    /// async-only. JPEG path remains sync via System.Drawing (reused from
    /// CaptureWithParams).
    /// </summary>
    public async Task<(byte[] Bytes, int Width, int Height, string Format)> CaptureWithFormatAsync(
        int maxWidth, int quality, string format)
    {
        format = (format ?? "jpeg").ToLowerInvariant();
        if (format != "webp" && format != "jpeg" && format != "jpg")
            format = "jpeg";

        // JPEG path: delegate to the sync version so we don't diverge.
        if (format == "jpeg" || format == "jpg")
        {
            var (jpeg, w, h) = CaptureWithParams(maxWidth, quality);
            return (jpeg, w, h, "jpeg");
        }

        // WebP path. Windows.Graphics.Imaging wants a SoftwareBitmap, which is
        // awkward to build from raw GDI pixels, so we round-trip through a
        // BMP in-memory decoder. Cost is ~10-30 ms extra — still much cheaper
        // than the bandwidth savings on a typical screen.
        if (maxWidth <= 0) maxWidth = DefaultMaxWidth;
        quality = Math.Clamp(quality, 1, 100);

        var screen = Screen.PrimaryScreen!;
        var screenW = screen.Bounds.Width;
        var screenH = screen.Bounds.Height;

        using var bmp = new Bitmap(screenW, screenH);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
        }

        Bitmap src = bmp;
        bool disposeSrc = false;
        if (maxWidth > 0 && bmp.Width > maxWidth)
        {
            float scale = (float)maxWidth / bmp.Width;
            int newH = (int)(bmp.Height * scale);
            src = new Bitmap(bmp, maxWidth, newH);
            disposeSrc = true;
        }

        try
        {
            using var bmpMs = new MemoryStream();
            src.Save(bmpMs, ImageFormat.Bmp);
            bmpMs.Position = 0;

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                bmpMs.AsRandomAccessStream());
            var softwareBmp = await decoder.GetSoftwareBitmapAsync();

            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.WebpEncoderId, outStream);

            // WebP encoder accepts ImageQuality 0.0-1.0.
            var qualityOpt = new Windows.Graphics.Imaging.BitmapTypedValue(
                quality / 100.0, Windows.Foundation.PropertyType.Single);
            encoder.BitmapProperties.SetPropertiesAsync(
                new Dictionary<string, Windows.Graphics.Imaging.BitmapTypedValue>
                {
                    ["ImageQuality"] = qualityOpt,
                }).GetAwaiter().GetResult();

            encoder.SetSoftwareBitmap(softwareBmp);
            await encoder.FlushAsync();

            outStream.Seek(0);
            var bytes = new byte[outStream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)outStream.Size);
            reader.ReadBytes(bytes);

            return (bytes, src.Width, src.Height, "webp");
        }
        finally
        {
            if (disposeSrc) src.Dispose();
        }
    }

    /// <summary>Capture a specific region as JPEG.</summary>
    public byte[] CaptureRegion(int x, int y, int w, int h, int quality = 70)
    {
        var screen = Screen.PrimaryScreen!;
        w = Math.Min(w, screen.Bounds.Width - x);
        h = Math.Min(h, screen.Bounds.Height - y);
        if (w <= 0 || h <= 0) return Array.Empty<byte>();

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        }

        return CompressToJpeg(bmp, quality, 0);
    }

    /// <summary>Get screen dimensions.</summary>
    public (int width, int height) GetScreenSize()
    {
        var screen = Screen.PrimaryScreen!;
        return (screen.Bounds.Width, screen.Bounds.Height);
    }

    private static byte[] CompressToJpeg(Bitmap bmp, int quality, int maxWidth)
    {
        Bitmap output = bmp;
        bool needsDispose = false;

        if (maxWidth > 0 && bmp.Width > maxWidth)
        {
            float scale = (float)maxWidth / bmp.Width;
            int newH = (int)(bmp.Height * scale);
            output = new Bitmap(bmp, maxWidth, newH);
            needsDispose = true;
        }

        try
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            var prms = new EncoderParameters(1);
            prms.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            output.Save(ms, encoder, prms);
            return ms.ToArray();
        }
        finally
        {
            if (needsDispose) output.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
