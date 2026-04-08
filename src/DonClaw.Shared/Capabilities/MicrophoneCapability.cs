using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Microphone capability — record audio from the user's microphone.
/// Can be used for voice commands, audio notes, or streaming audio to the AI.
/// </summary>
public class MicrophoneCapability : NodeCapabilityBase
{
    public override string Category => "microphone";

    private static readonly string[] _commands = { "microphone.list", "microphone.record", "microphone.stop" };
    public override IReadOnlyList<string> Commands => _commands;

    // Events for platform-specific implementation (WinUI has access to MediaCapture)
    public event Func<Task<MicrophoneInfo[]>>? ListRequested;
    public event Func<MicrophoneRecordArgs, Task<MicrophoneRecordResult>>? RecordRequested;
    public event Func<Task<MicrophoneRecordResult>>? StopRequested;

    private bool _isRecording;

    public MicrophoneCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "microphone.list" => await HandleListAsync(),
            "microphone.record" => await HandleRecordAsync(request),
            "microphone.stop" => await HandleStopAsync(),
            _ => Error($"Unknown: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleListAsync()
    {
        if (ListRequested == null) return Error("Microphone list not available");
        try
        {
            var mics = await ListRequested();
            return Success(new { microphones = mics, count = mics.Length });
        }
        catch (Exception ex) { return Error($"List failed: {ex.Message}"); }
    }

    private async Task<NodeInvokeResponse> HandleRecordAsync(NodeInvokeRequest request)
    {
        if (RecordRequested == null) return Error("Microphone recording not available");
        if (_isRecording) return Error("Already recording. Call microphone.stop first.");

        var durationMs = GetIntArg(request.Args, "durationMs", 5000); // Default 5 seconds
        var format = GetStringArg(request.Args, "format", "wav");
        var deviceId = GetStringArg(request.Args, "deviceId");
        var maxDurationMs = 60000; // Max 60 seconds

        if (durationMs > maxDurationMs)
            return Error($"Max duration: {maxDurationMs}ms (60 seconds)");

        Logger.Info($"microphone.record: duration={durationMs}ms, format={format}");

        try
        {
            _isRecording = true;
            var result = await RecordRequested(new MicrophoneRecordArgs
            {
                DurationMs = durationMs,
                Format = format!,
                DeviceId = deviceId
            });
            _isRecording = false;

            return Success(new
            {
                recorded = true,
                format = result.Format,
                durationMs = result.DurationMs,
                sizeBytes = result.SizeBytes,
                base64 = result.Base64
            });
        }
        catch (Exception ex)
        {
            _isRecording = false;
            return Error($"Record failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleStopAsync()
    {
        if (!_isRecording) return Success(new { stopped = false, message = "Not recording" });
        if (StopRequested == null) return Error("Stop not available");

        try
        {
            var result = await StopRequested();
            _isRecording = false;
            return Success(new { stopped = true, format = result.Format, durationMs = result.DurationMs, base64 = result.Base64 });
        }
        catch (Exception ex)
        {
            _isRecording = false;
            return Error($"Stop failed: {ex.Message}");
        }
    }
}

public class MicrophoneInfo
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}

public class MicrophoneRecordArgs
{
    public int DurationMs { get; set; } = 5000;
    public string Format { get; set; } = "wav";
    public string? DeviceId { get; set; }
}

public class MicrophoneRecordResult
{
    public string Format { get; set; } = "wav";
    public int DurationMs { get; set; }
    public long SizeBytes { get; set; }
    public string Base64 { get; set; } = "";
}
