using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DonClaw.Shared;

namespace DonClaw.Tray.Services;

/// <summary>
/// Sequence Engine — executes a chain of actions in one shot.
/// The AI sends one /sequence command, the node does everything locally.
/// No round-trips to the AI between steps.
///
/// Supported actions:
///   open, launch, focus, find_click, click, smooth, key, type,
///   wait, wait_for, screenshot, notify, scroll, combo
/// </summary>
public class SequenceEngine
{
    private readonly IReadOnlyList<INodeCapability> _capabilities;
    private readonly TextFinder _finder;
    private readonly ScreenshotCache _cache;
    private readonly IOpenClawLogger _logger;

    public SequenceEngine(
        IReadOnlyList<INodeCapability> capabilities,
        TextFinder finder,
        ScreenshotCache cache,
        IOpenClawLogger logger)
    {
        _capabilities = capabilities;
        _finder = finder;
        _cache = cache;
        _logger = logger;
    }

    public async Task<SequenceResult> ExecuteAsync(JsonElement stepsArray)
    {
        var result = new SequenceResult();
        var sw = Stopwatch.StartNew();

        foreach (var step in stepsArray.EnumerateArray())
        {
            var action = step.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(action)) continue;

            _logger.Info($"[Sequence] Step: {action}");
            var stepResult = new StepResult { Action = action };
            var stepSw = Stopwatch.StartNew();

            try
            {
                switch (action)
                {
                    case "open":
                        stepResult = await ExecCapability("browser.open", step);
                        break;

                    case "launch":
                        stepResult = await ExecCapability("app.launch", step);
                        break;

                    case "focus":
                        stepResult = await ExecCapability("app.focus", step);
                        break;

                    case "click":
                        stepResult = await ExecCapability("input.mouseClick", step);
                        break;

                    case "smooth":
                        stepResult = await ExecCapability("input.smoothClick", step);
                        break;

                    case "key":
                        stepResult = await ExecCapability("input.keyPress", step);
                        break;

                    case "type":
                        stepResult = await ExecCapability("input.type", step);
                        break;

                    case "combo":
                        stepResult = await ExecCapability("input.combo", step);
                        break;

                    case "scroll":
                        stepResult = await ExecCapability("input.mouseScroll", step);
                        break;

                    case "notify":
                        stepResult = await ExecCapability("system.notify", step);
                        break;

                    case "wait":
                        var ms = step.TryGetProperty("ms", out var msP) ? msP.GetInt32() : 500;
                        ms = Math.Min(ms, 10000); // Max 10s
                        await Task.Delay(ms);
                        stepResult = new StepResult { Action = "wait", Ok = true, Data = new { waited = ms } };
                        break;

                    case "wait_for":
                        stepResult = await HandleWaitFor(step);
                        break;

                    case "find_click":
                        stepResult = await HandleFindClick(step);
                        break;

                    case "find":
                        stepResult = await HandleFind(step);
                        break;

                    case "screenshot":
                        var jpeg = _cache.GetFreshJpeg(200);
                        var b64 = Convert.ToBase64String(jpeg);
                        stepResult = new StepResult
                        {
                            Action = "screenshot",
                            Ok = true,
                            Data = new { format = "jpeg", size = jpeg.Length, image = $"data:image/jpeg;base64,{b64}" }
                        };
                        break;

                    default:
                        stepResult = new StepResult { Action = action, Ok = false, Error = $"Unknown action: {action}" };
                        break;
                }
            }
            catch (Exception ex)
            {
                stepResult.Ok = false;
                stepResult.Error = ex.Message;
            }

            stepResult.Action = action;
            stepResult.DurationMs = (int)stepSw.ElapsedMilliseconds;
            result.Steps.Add(stepResult);

            // If a step fails and it's critical, stop the sequence
            if (!stepResult.Ok && step.TryGetProperty("required", out var req) && req.GetBoolean())
            {
                _logger.Warn($"[Sequence] Required step '{action}' failed: {stepResult.Error}");
                break;
            }
        }

        result.TotalMs = (int)sw.ElapsedMilliseconds;
        result.Ok = result.Steps.All(s => s.Ok);
        return result;
    }

    /// <summary>
    /// Wait until specific text appears on screen (OCR polling).
    /// Replaces the AI screenshot-analyze-decide loop.
    /// </summary>
    private async Task<StepResult> HandleWaitFor(JsonElement step)
    {
        var text = step.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var timeoutMs = step.TryGetProperty("timeoutMs", out var to) ? to.GetInt32() : 5000;

        if (string.IsNullOrEmpty(text))
            return new StepResult { Ok = false, Error = "Missing text" };

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var matches = await _finder.FindText(text);
            if (matches.Count > 0)
            {
                return new StepResult
                {
                    Ok = true,
                    Data = new
                    {
                        found = true,
                        text,
                        match = new { matches[0].Text, matches[0].X, matches[0].Y },
                        waitedMs = (int)sw.ElapsedMilliseconds
                    }
                };
            }
            await Task.Delay(300); // Poll every 300ms
        }

        return new StepResult { Ok = false, Error = $"Text '{text}' not found within {timeoutMs}ms" };
    }

    /// <summary>
    /// Find text on screen via OCR + smooth click it. All in one step.
    /// </summary>
    private async Task<StepResult> HandleFindClick(JsonElement step)
    {
        var text = step.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var smooth = !step.TryGetProperty("smooth", out var s) || s.GetBoolean(); // default true
        var index = step.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
        var timeoutMs = step.TryGetProperty("timeoutMs", out var to) ? to.GetInt32() : 3000;

        if (string.IsNullOrEmpty(text))
            return new StepResult { Ok = false, Error = "Missing text" };

        // Poll OCR until text found (or timeout)
        var sw = Stopwatch.StartNew();
        List<TextMatch> matches;
        do
        {
            matches = await _finder.FindText(text);
            if (matches.Count > 0) break;
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new StepResult { Ok = false, Error = $"Text '{text}' not found within {timeoutMs}ms" };
            await Task.Delay(250);
        } while (true);

        var target = index < matches.Count ? matches[index] : matches[0];

        // Click
        var cmd = smooth ? "input.smoothClick" : "input.mouseClick";
        var cap = _capabilities.FirstOrDefault(c => c.CanHandle(cmd));
        if (cap == null)
            return new StepResult { Ok = false, Error = "Input capability not found" };

        var argsJson = $"{{\"x\":{target.X},\"y\":{target.Y},\"moveDurationMs\":250}}";
        using var argsDoc = JsonDocument.Parse(argsJson);

        var req = new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = cmd,
            Args = argsDoc.RootElement.Clone()
        };
        var res = await cap.ExecuteAsync(req);

        return new StepResult
        {
            Ok = res.Ok,
            Data = new
            {
                action = "find_click",
                target = new { text = target.Text, x = target.X, y = target.Y },
                smooth,
                searchMs = (int)sw.ElapsedMilliseconds
            }
        };
    }

    /// <summary>Find text on screen (no click).</summary>
    private async Task<StepResult> HandleFind(JsonElement step)
    {
        var text = step.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(text))
            return new StepResult { Ok = false, Error = "Missing text" };

        var matches = await _finder.FindText(text);
        return new StepResult
        {
            Ok = true,
            Data = new { query = text, count = matches.Count, matches = matches.Select(m => new { m.Text, m.X, m.Y }) }
        };
    }

    /// <summary>Execute a capability command, mapping step JSON properties to args.</summary>
    private async Task<StepResult> ExecCapability(string command, JsonElement step)
    {
        var cap = _capabilities.FirstOrDefault(c => c.CanHandle(command));
        if (cap == null)
            return new StepResult { Ok = false, Error = $"No capability for {command}" };

        // Build args from step properties (excluding "action" and "required")
        var argsDict = new Dictionary<string, object>();
        foreach (var prop in step.EnumerateObject())
        {
            if (prop.Name == "action" || prop.Name == "required") continue;
            argsDict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.GetRawText()
            };
        }

        var argsJson = JsonSerializer.Serialize(argsDict);
        using var argsDoc = JsonDocument.Parse(argsJson);

        var req = new NodeInvokeRequest
        {
            Id = Guid.NewGuid().ToString(),
            Command = command,
            Args = argsDoc.RootElement.Clone()
        };

        var res = await cap.ExecuteAsync(req);
        return new StepResult { Ok = res.Ok, Data = res.Payload, Error = res.Error };
    }
}

public class SequenceResult
{
    public bool Ok { get; set; }
    public int TotalMs { get; set; }
    public List<StepResult> Steps { get; set; } = new();
}

public class StepResult
{
    public string Action { get; set; } = "";
    public bool Ok { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
}
