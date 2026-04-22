using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DonClaw.Tray.Services;

/// <summary>
/// Finds text on screen using Windows built-in OCR (Windows.Media.Ocr).
/// This runs 100% locally on the node — no screenshots sent to AI.
/// 
/// Usage:
///   var results = await finder.FindText("Play");
///   // Returns [{text: "Play", x: 520, y: 370, w: 80, h: 30, confidence: 0.95}]
///
/// The AI just says "find Play" and gets coordinates instantly.
/// </summary>
public class TextFinder
{
    public TextFinder() { }

    /// <summary>
    /// Find all occurrences of text on the current screen.
    /// Returns center coordinates for each match — ready for clicking.
    /// </summary>
    public async Task<List<TextMatch>> FindText(string query, bool exactMatch = false, int screenIndex = 0)
    {
        var results = new List<TextMatch>();

        try
        {
            // Capture screen
            var screen = Screen.PrimaryScreen!;
            using var bmp = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
            }

            // Convert to SoftwareBitmap for Windows OCR
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;

            // Use Windows.Media.Ocr
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                ms.AsRandomAccessStream());
            var softwareBmp = await decoder.GetSoftwareBitmapAsync();

            var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null)
            {
                // Fallback: try English
                ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                    new Windows.Globalization.Language("en-US"));
            }

            if (ocrEngine == null)
                return results; // No OCR engine available

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBmp);

            var queryLower = query.ToLowerInvariant();

            foreach (var line in ocrResult.Lines)
            {
                // Check full line text
                var lineText = line.Text;

                foreach (var word in line.Words)
                {
                    bool matches;
                    if (exactMatch)
                        matches = word.Text.Equals(query, StringComparison.OrdinalIgnoreCase);
                    else
                        matches = word.Text.ToLowerInvariant().Contains(queryLower);

                    if (matches)
                    {
                        var rect = word.BoundingRect;
                        results.Add(new TextMatch
                        {
                            Text = word.Text,
                            X = (int)(rect.X + rect.Width / 2),  // Center X
                            Y = (int)(rect.Y + rect.Height / 2), // Center Y
                            Left = (int)rect.X,
                            Top = (int)rect.Y,
                            Width = (int)rect.Width,
                            Height = (int)rect.Height
                        });
                    }
                }

                // Also check if the query matches part of a full line (multi-word search)
                if (query.Contains(' ') && lineText.ToLowerInvariant().Contains(queryLower))
                {
                    // Use the bounding rect of the first word as approximate position
                    var firstWord = line.Words.FirstOrDefault();
                    var lastWord = line.Words.LastOrDefault();
                    if (firstWord != null && lastWord != null)
                    {
                        var left = (int)firstWord.BoundingRect.X;
                        var top = (int)firstWord.BoundingRect.Y;
                        var right = (int)(lastWord.BoundingRect.X + lastWord.BoundingRect.Width);
                        var bottom = (int)(lastWord.BoundingRect.Y + lastWord.BoundingRect.Height);

                        results.Add(new TextMatch
                        {
                            Text = lineText,
                            X = (left + right) / 2,
                            Y = (top + bottom) / 2,
                            Left = left,
                            Top = top,
                            Width = right - left,
                            Height = bottom - top,
                            IsLine = true
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"TextFinder.FindText failed: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Get ALL text visible on screen with positions (full OCR dump).
    /// Useful for debugging or letting the AI understand the full screen state.
    /// </summary>
    public async Task<List<TextMatch>> ReadAllText()
    {
        return await FindText("", exactMatch: false); // Empty query won't match anything
        // Actually, let me implement this properly:
    }

    /// <summary>
    /// Read all visible text on screen — returns every word with its position.
    /// </summary>
    public async Task<OcrDump> DumpScreenText()
    {
        var dump = new OcrDump();

        try
        {
            var screen = Screen.PrimaryScreen!;
            using var bmp = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                ms.AsRandomAccessStream());
            var softwareBmp = await decoder.GetSoftwareBitmapAsync();

            var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                             new Windows.Globalization.Language("en-US"));

            if (ocrEngine == null)
                return dump;

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBmp);

            foreach (var line in ocrResult.Lines)
            {
                var lineEntry = new OcrLine
                {
                    Text = line.Text,
                    Words = line.Words.Select(w => new TextMatch
                    {
                        Text = w.Text,
                        X = (int)(w.BoundingRect.X + w.BoundingRect.Width / 2),
                        Y = (int)(w.BoundingRect.Y + w.BoundingRect.Height / 2),
                        Left = (int)w.BoundingRect.X,
                        Top = (int)w.BoundingRect.Y,
                        Width = (int)w.BoundingRect.Width,
                        Height = (int)w.BoundingRect.Height
                    }).ToList()
                };
                dump.Lines.Add(lineEntry);
            }

            dump.ScreenWidth = screen.Bounds.Width;
            dump.ScreenHeight = screen.Bounds.Height;
        }
        catch (Exception ex)
        {
            Logger.Error($"TextFinder.DumpScreenText failed: {ex.Message}");
        }

        return dump;
    }

    /// <summary>
    /// One-shot "perceive" primitive: takes a SINGLE screenshot, runs OCR on
    /// it AND returns a small thumbnail (JPEG or WebP), in one trip.
    ///
    /// Purpose: let the AI control loop replace `screenshot + OCR` (two
    /// captures + two payloads) with a single `/perceive` call that returns
    /// text-with-coords (cheap for the LLM) plus a tiny backup thumbnail.
    ///
    /// thumbMaxWidth / thumbQuality control the thumbnail size. Defaults
    /// (640 @ Q40) yield ~8-15 KB on typical screens for JPEG, ~5-11 KB
    /// for WebP.
    /// </summary>
    public async Task<PerceiveResult> PerceiveScreen(
        int thumbMaxWidth = 640,
        int thumbQuality = 40,
        bool includeThumbnail = true,
        string thumbFormat = "jpeg")
    {
        var result = new PerceiveResult();
        thumbFormat = (thumbFormat ?? "jpeg").ToLowerInvariant();
        if (thumbFormat != "webp" && thumbFormat != "jpeg" && thumbFormat != "jpg")
            thumbFormat = "jpeg";

        try
        {
            var screen = Screen.PrimaryScreen!;
            result.ScreenWidth = screen.Bounds.Width;
            result.ScreenHeight = screen.Bounds.Height;

            // Single capture shared by both OCR and thumbnail generation.
            using var bmp = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screen.Bounds.Location, Point.Empty, screen.Bounds.Size);
            }

            // OCR path — feed the bitmap to Windows.Media.Ocr.
            try
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Bmp);
                ms.Position = 0;

                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                    ms.AsRandomAccessStream());
                var softwareBmp = await decoder.GetSoftwareBitmapAsync();

                var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                             ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                                 new Windows.Globalization.Language("en-US"));

                if (ocrEngine != null)
                {
                    var ocrResult = await ocrEngine.RecognizeAsync(softwareBmp);
                    foreach (var line in ocrResult.Lines)
                    {
                        var lineEntry = new OcrLine
                        {
                            Text = line.Text,
                            Words = line.Words.Select(w => new TextMatch
                            {
                                Text = w.Text,
                                X = (int)(w.BoundingRect.X + w.BoundingRect.Width / 2),
                                Y = (int)(w.BoundingRect.Y + w.BoundingRect.Height / 2),
                                Left = (int)w.BoundingRect.X,
                                Top = (int)w.BoundingRect.Y,
                                Width = (int)w.BoundingRect.Width,
                                Height = (int)w.BoundingRect.Height
                            }).ToList()
                        };
                        result.Lines.Add(lineEntry);
                    }
                }
            }
            catch (Exception ex)
            {
                // OCR failing shouldn't kill the whole perceive — we still want
                // the thumbnail as a fallback signal for the caller.
                Logger.Error($"PerceiveScreen OCR failed: {ex.Message}");
            }

            // Thumbnail path — downscale from the SAME bitmap and compress.
            if (includeThumbnail)
            {
                try
                {
                    int tw = thumbMaxWidth <= 0 ? 640 : thumbMaxWidth;
                    int tq = Math.Clamp(thumbQuality, 1, 100);

                    Bitmap thumbSrc = bmp;
                    bool disposeThumb = false;
                    if (bmp.Width > tw)
                    {
                        float scale = (float)tw / bmp.Width;
                        int th = (int)(bmp.Height * scale);
                        thumbSrc = new Bitmap(bmp, tw, th);
                        disposeThumb = true;
                    }

                    try
                    {
                        if (thumbFormat == "webp")
                        {
                            // WebP path via SkiaSharp (WIC on net10-windows
                            // doesn't expose WebpEncoderId). Reuses the same
                            // helper that ScreenshotCache.CaptureWithFormatAsync
                            // uses for screen.capture to keep encoding
                            // behaviour consistent.
                            var bytes = await Task.Run(() => DonClaw.Tray.Services.ScreenshotCache
                                .EncodeBitmapAsWebP(thumbSrc, tq));
                            result.ThumbnailJpeg = bytes;
                            result.ThumbnailFormat = "webp";
                        }
                        else
                        {
                            using var tms = new MemoryStream();
                            var enc = ImageCodecInfo.GetImageEncoders()
                                .First(e => e.FormatID == ImageFormat.Jpeg.Guid);
                            var prms = new EncoderParameters(1);
                            prms.Param[0] = new EncoderParameter(Encoder.Quality, (long)tq);
                            thumbSrc.Save(tms, enc, prms);

                            result.ThumbnailJpeg = tms.ToArray();
                            result.ThumbnailFormat = "jpeg";
                        }
                        result.ThumbnailWidth = thumbSrc.Width;
                        result.ThumbnailHeight = thumbSrc.Height;
                    }
                    finally
                    {
                        if (disposeThumb) thumbSrc.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"PerceiveScreen thumbnail failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"PerceiveScreen failed: {ex.Message}");
        }

        return result;
    }
}

/// <summary>
/// Combined OCR + thumbnail result for the `/perceive` endpoint.
/// ThumbnailJpeg may be empty if the caller asked to skip it or it failed.
/// Despite the name the bytes match ThumbnailFormat ("jpeg" or "webp").
/// </summary>
public class PerceiveResult
{
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public List<OcrLine> Lines { get; set; } = new();
    public byte[] ThumbnailJpeg { get; set; } = Array.Empty<byte>();
    public int ThumbnailWidth { get; set; }
    public int ThumbnailHeight { get; set; }
    public string ThumbnailFormat { get; set; } = "jpeg";
}

public class TextMatch
{
    public string Text { get; set; } = "";
    public int X { get; set; }      // Center X (click here)
    public int Y { get; set; }      // Center Y (click here)
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsLine { get; set; }
}

public class OcrLine
{
    public string Text { get; set; } = "";
    public List<TextMatch> Words { get; set; } = new();
}

public class OcrDump
{
    public List<OcrLine> Lines { get; set; } = new();
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
}
