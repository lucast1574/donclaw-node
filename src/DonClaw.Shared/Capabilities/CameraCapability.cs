namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Camera capability using Windows.Media.Capture
/// </summary>
public class CameraCapability : NodeCapabilityBase
{
    public override string Category => "camera";

    private static readonly string[] _commands = new[]
    {
        "camera.list",
        "camera.snap"
    };

    public override IReadOnlyList<string> Commands => _commands;

    public event Func<Task<CameraInfo[]>>? ListRequested;
    public event Func<CameraSnapArgs, Task<CameraSnapResult>>? SnapRequested;

    public CameraCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "camera.list" => await HandleListAsync(request),
            "camera.snap" => await HandleSnapAsync(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleListAsync(NodeInvokeRequest request)
    {
        Logger.Info("camera.list");

        if (ListRequested == null)
            return Error("Camera list not available");

        try
        {
            var cameras = await ListRequested();
            return Success(new { cameras });
        }
        catch (Exception ex)
        {
            Logger.Error("Camera list failed", ex);
            return Error($"List failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleSnapAsync(NodeInvokeRequest request)
    {
        var deviceId = GetStringArg(request.Args, "deviceId");
        var format = GetStringArg(request.Args, "format", "jpeg");
        var maxWidth = GetIntArg(request.Args, "maxWidth", 1280);
        var quality = GetIntArg(request.Args, "quality", 80);

        Logger.Info($"camera.snap: deviceId={deviceId ?? "(default)"}, format={format}");

        if (SnapRequested == null)
            return Error("Camera snap not available");

        try
        {
            var result = await SnapRequested(new CameraSnapArgs
            {
                DeviceId = deviceId,
                Format = format ?? "jpeg",
                MaxWidth = maxWidth,
                Quality = quality
            });

            return Success(new
            {
                format = result.Format,
                width = result.Width,
                height = result.Height,
                base64 = result.Base64
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Camera snap failed", ex);
            return Error($"Snap failed: {ex.Message}");
        }
    }
}

public class CameraInfo
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}

public class CameraSnapArgs
{
    public string? DeviceId { get; set; }
    public string Format { get; set; } = "jpeg";
    public int MaxWidth { get; set; } = 1280;
    public int Quality { get; set; } = 80;
}

public class CameraSnapResult
{
    public string Format { get; set; } = "jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Base64 { get; set; } = "";
}
