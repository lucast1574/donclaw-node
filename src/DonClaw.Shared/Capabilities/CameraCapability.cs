using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Camera capability — photos and video clips from the user's camera.
/// </summary>
public class CameraCapability : NodeCapabilityBase
{
    public override string Category => "camera";
    private static readonly string[] _commands = { "camera.list", "camera.snap", "camera.clip" };
    public override IReadOnlyList<string> Commands => _commands;

    public event Func<Task<CameraInfo[]>>? ListRequested;
    public event Func<CameraSnapArgs, Task<CameraSnapResult>>? SnapRequested;
    public event Func<CameraClipArgs, Task<CameraClipResult>>? ClipRequested;

    public CameraCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "camera.list" => await HandleListAsync(),
            "camera.snap" => await HandleSnapAsync(request),
            "camera.clip" => await HandleClipAsync(request),
            _ => Error($"Unknown: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleListAsync()
    {
        if (ListRequested == null) return Error("Camera list not available");
        try { return Success(new { cameras = await ListRequested() }); }
        catch (Exception ex) { return Error($"List failed: {ex.Message}"); }
    }

    private async Task<NodeInvokeResponse> HandleSnapAsync(NodeInvokeRequest request)
    {
        if (SnapRequested == null) return Error("Camera snap not available");
        try
        {
            var result = await SnapRequested(new CameraSnapArgs
            {
                DeviceId = GetStringArg(request.Args, "deviceId"),
                Format = GetStringArg(request.Args, "format", "jpeg")!,
                MaxWidth = GetIntArg(request.Args, "maxWidth", 1280),
                Quality = GetIntArg(request.Args, "quality", 80)
            });
            return Success(new { format = result.Format, width = result.Width, height = result.Height, base64 = result.Base64 });
        }
        catch (Exception ex) { return Error($"Snap failed: {ex.Message}"); }
    }

    /// <summary>
    /// Record a short video clip from the camera.
    /// </summary>
    private async Task<NodeInvokeResponse> HandleClipAsync(NodeInvokeRequest request)
    {
        if (ClipRequested == null) return Error("Camera clip not available");

        var durationMs = GetIntArg(request.Args, "durationMs", 5000);
        if (durationMs > 30000) return Error("Max clip duration: 30 seconds");

        try
        {
            var result = await ClipRequested(new CameraClipArgs
            {
                DeviceId = GetStringArg(request.Args, "deviceId"),
                DurationMs = durationMs,
                Format = GetStringArg(request.Args, "format", "mp4")!,
                Fps = GetIntArg(request.Args, "fps", 30)
            });
            return Success(new { format = result.Format, durationMs = result.DurationMs, sizeBytes = result.SizeBytes, base64 = result.Base64 });
        }
        catch (Exception ex) { return Error($"Clip failed: {ex.Message}"); }
    }
}

public class CameraInfo { public string DeviceId { get; set; } = ""; public string Name { get; set; } = ""; public bool IsDefault { get; set; } }
public class CameraSnapArgs { public string? DeviceId { get; set; } public string Format { get; set; } = "jpeg"; public int MaxWidth { get; set; } = 1280; public int Quality { get; set; } = 80; }
public class CameraSnapResult { public string Format { get; set; } = "jpeg"; public int Width { get; set; } public int Height { get; set; } public string Base64 { get; set; } = ""; }
public class CameraClipArgs { public string? DeviceId { get; set; } public int DurationMs { get; set; } = 5000; public string Format { get; set; } = "mp4"; public int Fps { get; set; } = 30; }
public class CameraClipResult { public string Format { get; set; } = "mp4"; public int DurationMs { get; set; } public long SizeBytes { get; set; } public string Base64 { get; set; } = ""; }
