namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Screen capture capability using Windows.Graphics.Capture
/// </summary>
public class ScreenCapability : NodeCapabilityBase
{
    public override string Category => "screen";

    private static readonly string[] _commands = new[]
    {
        "screen.capture",
        "screen.list",
        "screen.perceive"
    };

    public override IReadOnlyList<string> Commands => _commands;

    public event Func<ScreenCaptureArgs, Task<ScreenCaptureResult>>? CaptureRequested;
    public event Func<Task<ScreenInfo[]>>? ListRequested;
    public event Func<ScreenPerceiveArgs, Task<ScreenPerceiveResult>>? PerceiveRequested;

    public ScreenCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "screen.capture" => await HandleCaptureAsync(request),
            "screen.list" => await HandleListAsync(request),
            "screen.perceive" => await HandlePerceiveAsync(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandlePerceiveAsync(NodeInvokeRequest request)
    {
        if (PerceiveRequested == null)
            return Error("Screen perceive not available");

        var thumbMaxWidth = GetIntArg(request.Args, "thumbMaxWidth", 640);
        var thumbQuality = GetIntArg(request.Args, "thumbQuality", 40);
        var includeThumb = GetBoolArg(request.Args, "includeThumbnail", true);
        var thumbFormat = GetStringArg(request.Args, "thumbFormat", "jpeg") ?? "jpeg";

        Logger.Info($"screen.perceive: thumbW={thumbMaxWidth} Q={thumbQuality} thumb={includeThumb} fmt={thumbFormat}");

        try
        {
            var r = await PerceiveRequested(new ScreenPerceiveArgs
            {
                ThumbMaxWidth = thumbMaxWidth,
                ThumbQuality = thumbQuality,
                IncludeThumbnail = includeThumb,
                ThumbFormat = thumbFormat,
            });

            object? thumbnail = null;
            if (includeThumb && !string.IsNullOrEmpty(r.ThumbnailBase64))
            {
                var fmt = string.IsNullOrEmpty(r.ThumbnailFormat) ? "jpeg" : r.ThumbnailFormat;
                thumbnail = new
                {
                    format = fmt,
                    width = r.ThumbnailWidth,
                    height = r.ThumbnailHeight,
                    base64 = r.ThumbnailBase64,
                    image = $"data:image/{fmt};base64,{r.ThumbnailBase64}",
                };
            }

            return Success(new
            {
                screenWidth = r.ScreenWidth,
                screenHeight = r.ScreenHeight,
                lines = r.Lines,
                thumbnail,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("screen.perceive failed", ex);
            return Error($"Perceive failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleCaptureAsync(NodeInvokeRequest request)
    {
        // Sane defaults for the AI control loop: 1280px @ Q60 is still very
        // readable but ~3x smaller payload than 1920px @ Q80. Callers that
        // need the full-res original can still pass explicit maxWidth/quality.
        var format = GetStringArg(request.Args, "format", "jpeg");
        var maxWidth = GetIntArg(request.Args, "maxWidth", 1280);
        var quality = GetIntArg(request.Args, "quality", 60);
        var monitor = GetIntArg(request.Args, "monitor", 0);
        var screenIndex = GetIntArg(request.Args, "screenIndex", monitor);
        var includePointer = GetBoolArg(request.Args, "includePointer", true);

        Logger.Info($"screen.capture: format={format}, maxWidth={maxWidth}, monitor={screenIndex}");

        if (CaptureRequested == null)
            return Error("Screen capture not available");

        try
        {
            var result = await CaptureRequested(new ScreenCaptureArgs
            {
                Format = format ?? "png",
                MaxWidth = maxWidth,
                Quality = quality,
                MonitorIndex = screenIndex,
                IncludePointer = includePointer
            });

            var image = $"data:image/{result.Format.ToLowerInvariant()};base64,{result.Base64}";
            return Success(new
            {
                format = result.Format,
                width = result.Width,
                height = result.Height,
                base64 = result.Base64,
                image
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Screen capture failed", ex);
            return Error($"Capture failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleListAsync(NodeInvokeRequest request)
    {
        Logger.Info("screen.list");

        if (ListRequested == null)
            return Error("Screen list not available");

        try
        {
            var screens = await ListRequested();
            var formatted = new List<object>();
            foreach (var screen in screens)
            {
                formatted.Add(new
                {
                    index = screen.Index,
                    name = screen.Name,
                    primary = screen.IsPrimary,
                    bounds = new { x = screen.X, y = screen.Y, width = screen.Width, height = screen.Height },
                    workingArea = new { x = screen.WorkingX, y = screen.WorkingY, width = screen.WorkingWidth, height = screen.WorkingHeight }
                });
            }
            return Success(new { screens = formatted });
        }
        catch (Exception ex)
        {
            Logger.Error("Screen list failed", ex);
            return Error($"List failed: {ex.Message}");
        }
    }
}

public class ScreenCaptureArgs
{
    public string Format { get; set; } = "jpeg";
    public int MaxWidth { get; set; } = 1280;
    public int Quality { get; set; } = 60;
    public int MonitorIndex { get; set; } = 0;
    public bool IncludePointer { get; set; } = true;
}

public class ScreenCaptureResult
{
    public string Format { get; set; } = "png";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Base64 { get; set; } = "";
}

public class ScreenPerceiveArgs
{
    public int ThumbMaxWidth { get; set; } = 640;
    public int ThumbQuality { get; set; } = 40;
    public bool IncludeThumbnail { get; set; } = true;
    /// <summary>"jpeg" (default) or "webp". WebP is ~25-35% smaller.</summary>
    public string ThumbFormat { get; set; } = "jpeg";
}

public class ScreenPerceiveResult
{
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    /// <summary>Shape: [{ text, words: [{Text, X, Y}] }] — serialized as-is.</summary>
    public object Lines { get; set; } = Array.Empty<object>();
    public string ThumbnailBase64 { get; set; } = "";
    public int ThumbnailWidth { get; set; }
    public int ThumbnailHeight { get; set; }
    public string ThumbnailFormat { get; set; } = "jpeg";
}

public class ScreenInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int WorkingX { get; set; }
    public int WorkingY { get; set; }
    public int WorkingWidth { get; set; }
    public int WorkingHeight { get; set; }
    public bool IsPrimary { get; set; }
}
