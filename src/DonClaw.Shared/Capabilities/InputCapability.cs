using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Input capability — programmatic control of keyboard, mouse, and gamepad.
/// THIS is what enables the AI to play Fortnite, fill forms, automate any app.
/// 
/// Uses Win32 SendInput API to inject real keyboard/mouse events into the active
/// desktop session. The user sees everything happening on their screen.
/// 
/// Examples:
/// - AI plays a game: sends WASD keys, mouse movements, clicks
/// - AI fills a form: types text, tabs between fields, clicks buttons
/// - AI operates any app: keyboard shortcuts, drag & drop, scrolling
/// </summary>
public class InputCapability : NodeCapabilityBase
{
    public override string Category => "input";

    private static readonly string[] _commands =
    {
        "input.keyPress", "input.keyDown", "input.keyUp", "input.type",
        "input.mouseMove", "input.mouseClick", "input.mouseDown", "input.mouseUp",
        "input.mouseScroll", "input.mouseDrag",
        "input.smoothMove", "input.smoothClick",
        "input.combo", "input.macro", "input.getCursorPos"
    };

    public override IReadOnlyList<string> Commands => _commands;

    // Win32 SendInput structures
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // Input type constants
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    // Mouse event flags
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // Keyboard flags
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    public InputCapability(IOpenClawLogger logger) : base(logger) { }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "input.keyPress" => HandleKeyPress(request),
            "input.keyDown" => HandleKeyDown(request),
            "input.keyUp" => HandleKeyUp(request),
            "input.type" => await HandleTypeAsync(request),
            "input.mouseMove" => HandleMouseMove(request),
            "input.mouseClick" => HandleMouseClick(request),
            "input.mouseDown" => HandleMouseDown(request),
            "input.mouseUp" => HandleMouseUp(request),
            "input.mouseScroll" => HandleMouseScroll(request),
            "input.mouseDrag" => await HandleMouseDragAsync(request),
            "input.smoothMove" => await HandleSmoothMoveAsync(request),
            "input.smoothClick" => await HandleSmoothClickAsync(request),
            "input.combo" => HandleCombo(request),
            "input.macro" => await HandleMacroAsync(request),
            "input.getCursorPos" => HandleGetCursorPos(),
            _ => Error($"Unknown: {request.Command}")
        };
    }

    /// <summary>
    /// Press and release a key. Key can be a name ("enter", "space", "a", "f1") or virtual key code.
    /// </summary>
    private NodeInvokeResponse HandleKeyPress(NodeInvokeRequest request)
    {
        var key = GetStringArg(request.Args, "key");
        var holdMs = GetIntArg(request.Args, "holdMs", 50);
        if (string.IsNullOrEmpty(key)) return Error("Missing key");

        var vk = ResolveVirtualKey(key);
        if (vk == 0) return Error($"Unknown key: {key}");

        Logger.Info($"input.keyPress: {key} (vk=0x{vk:X2})");

        SendKeyDown(vk);
        if (holdMs > 0) Thread.Sleep(holdMs);
        SendKeyUp(vk);

        return Success(new { pressed = true, key, vk = (int)vk });
    }

    private NodeInvokeResponse HandleKeyDown(NodeInvokeRequest request)
    {
        var key = GetStringArg(request.Args, "key");
        if (string.IsNullOrEmpty(key)) return Error("Missing key");
        var vk = ResolveVirtualKey(key);
        if (vk == 0) return Error($"Unknown key: {key}");
        SendKeyDown(vk);
        return Success(new { down = true, key });
    }

    private NodeInvokeResponse HandleKeyUp(NodeInvokeRequest request)
    {
        var key = GetStringArg(request.Args, "key");
        if (string.IsNullOrEmpty(key)) return Error("Missing key");
        var vk = ResolveVirtualKey(key);
        if (vk == 0) return Error($"Unknown key: {key}");
        SendKeyUp(vk);
        return Success(new { up = true, key });
    }

    /// <summary>
    /// Type a string of text character by character. Simulates real typing.
    /// </summary>
    private async Task<NodeInvokeResponse> HandleTypeAsync(NodeInvokeRequest request)
    {
        var text = GetStringArg(request.Args, "text");
        var delayMs = GetIntArg(request.Args, "delayMs", 30); // Delay between keystrokes
        if (string.IsNullOrEmpty(text)) return Error("Missing text");

        Logger.Info($"input.type: \"{(text.Length > 40 ? text[..40] + "..." : text)}\" ({text.Length} chars)");

        foreach (var ch in text)
        {
            var vkResult = VkKeyScan(ch);
            if (vkResult == -1) continue;

            var vk = (ushort)(vkResult & 0xFF);
            var shift = (vkResult & 0x100) != 0;

            if (shift) SendKeyDown(0x10); // VK_SHIFT
            SendKeyDown(vk);
            SendKeyUp(vk);
            if (shift) SendKeyUp(0x10);

            if (delayMs > 0) await Task.Delay(delayMs);
        }

        return Success(new { typed = true, length = text.Length });
    }

    /// <summary>
    /// Move mouse to absolute screen coordinates.
    /// </summary>
    private NodeInvokeResponse HandleMouseMove(NodeInvokeRequest request)
    {
        var x = GetIntArg(request.Args, "x", -1);
        var y = GetIntArg(request.Args, "y", -1);
        var relative = GetBoolArg(request.Args, "relative", false);

        if (relative)
        {
            GetCursorPos(out var pos);
            x = pos.X + x;
            y = pos.Y + y;
        }

        if (x < 0 || y < 0) return Error("Missing x/y coordinates");

        SetCursorPos(x, y);
        return Success(new { moved = true, x, y });
    }

    /// <summary>
    /// Click at coordinates (or current position if no coords given).
    /// </summary>
    private NodeInvokeResponse HandleMouseClick(NodeInvokeRequest request)
    {
        var x = GetIntArg(request.Args, "x", -1);
        var y = GetIntArg(request.Args, "y", -1);
        var button = GetStringArg(request.Args, "button", "left");
        var doubleClick = GetBoolArg(request.Args, "double", false);

        if (x >= 0 && y >= 0) SetCursorPos(x, y);

        var (downFlag, upFlag) = button?.ToLowerInvariant() switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        SendMouseEvent(downFlag);
        SendMouseEvent(upFlag);

        if (doubleClick)
        {
            Thread.Sleep(50);
            SendMouseEvent(downFlag);
            SendMouseEvent(upFlag);
        }

        return Success(new { clicked = true, button, x, y, doubleClick });
    }

    private NodeInvokeResponse HandleMouseDown(NodeInvokeRequest request)
    {
        var button = GetStringArg(request.Args, "button", "left");
        var flag = button?.ToLowerInvariant() switch
        {
            "right" => MOUSEEVENTF_RIGHTDOWN,
            "middle" => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_LEFTDOWN
        };
        SendMouseEvent(flag);
        return Success(new { down = true, button });
    }

    private NodeInvokeResponse HandleMouseUp(NodeInvokeRequest request)
    {
        var button = GetStringArg(request.Args, "button", "left");
        var flag = button?.ToLowerInvariant() switch
        {
            "right" => MOUSEEVENTF_RIGHTUP,
            "middle" => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_LEFTUP
        };
        SendMouseEvent(flag);
        return Success(new { up = true, button });
    }

    /// <summary>
    /// Scroll the mouse wheel. Positive = up, negative = down.
    /// </summary>
    private NodeInvokeResponse HandleMouseScroll(NodeInvokeRequest request)
    {
        var amount = GetIntArg(request.Args, "amount", 3); // Number of "clicks"
        var x = GetIntArg(request.Args, "x", -1);
        var y = GetIntArg(request.Args, "y", -1);

        if (x >= 0 && y >= 0) SetCursorPos(x, y);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = MOUSEEVENTF_WHEEL,
                    mouseData = (uint)(amount * 120) // 120 = one wheel click
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

        return Success(new { scrolled = true, amount });
    }

    /// <summary>
    /// Drag from one point to another. Holds left button, moves, releases.
    /// </summary>
    private async Task<NodeInvokeResponse> HandleMouseDragAsync(NodeInvokeRequest request)
    {
        var fromX = GetIntArg(request.Args, "fromX", 0);
        var fromY = GetIntArg(request.Args, "fromY", 0);
        var toX = GetIntArg(request.Args, "toX", 0);
        var toY = GetIntArg(request.Args, "toY", 0);
        var steps = GetIntArg(request.Args, "steps", 20);
        var durationMs = GetIntArg(request.Args, "durationMs", 500);

        SetCursorPos(fromX, fromY);
        await Task.Delay(50);
        SendMouseEvent(MOUSEEVENTF_LEFTDOWN);

        var stepDelay = durationMs / Math.Max(steps, 1);
        for (int i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            var cx = (int)(fromX + (toX - fromX) * t);
            var cy = (int)(fromY + (toY - fromY) * t);
            SetCursorPos(cx, cy);
            await Task.Delay(stepDelay);
        }

        SendMouseEvent(MOUSEEVENTF_LEFTUP);
        return Success(new { dragged = true, from = new { x = fromX, y = fromY }, to = new { x = toX, y = toY } });
    }

    /// <summary>
    /// Press a key combination (e.g., Ctrl+C, Alt+Tab, Ctrl+Shift+S).
    /// </summary>
    private NodeInvokeResponse HandleCombo(NodeInvokeRequest request)
    {
        var keys = new List<string>();
        if (request.Args.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
            request.Args.TryGetProperty("keys", out var keysEl) &&
            keysEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in keysEl.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    keys.Add(item.GetString()!);
        }

        // Also support "combo" as string like "ctrl+c"
        var comboStr = GetStringArg(request.Args, "combo");
        if (!string.IsNullOrEmpty(comboStr))
            keys.AddRange(comboStr.Split('+', StringSplitOptions.TrimEntries));

        if (keys.Count == 0) return Error("Missing keys array or combo string");

        Logger.Info($"input.combo: {string.Join("+", keys)}");

        var vkeys = new List<ushort>();
        foreach (var key in keys)
        {
            var vk = ResolveVirtualKey(key);
            if (vk == 0) return Error($"Unknown key in combo: {key}");
            vkeys.Add(vk);
        }

        // Press all keys down in order
        foreach (var vk in vkeys) SendKeyDown(vk);
        Thread.Sleep(50);
        // Release in reverse order
        for (int i = vkeys.Count - 1; i >= 0; i--) SendKeyUp(vkeys[i]);

        return Success(new { combo = true, keys });
    }

    /// <summary>
    /// Execute a macro — a sequence of input actions with delays.
    /// This is the power feature: enables complex automation like playing games,
    /// filling multi-step forms, operating any application.
    /// 
    /// Each step: { action: "keyPress"|"click"|"type"|"move"|"wait", ...params }
    /// </summary>
    private async Task<NodeInvokeResponse> HandleMacroAsync(NodeInvokeRequest request)
    {
        if (!request.Args.TryGetProperty("steps", out var stepsEl) ||
            stepsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Error("Missing steps array");

        var maxSteps = 200;
        var stepCount = 0;

        Logger.Info("input.macro: starting");

        foreach (var step in stepsEl.EnumerateArray())
        {
            if (stepCount++ >= maxSteps) break;

            var action = step.TryGetProperty("action", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(action)) continue;

            switch (action)
            {
                case "keyPress":
                    var key = step.TryGetProperty("key", out var k) ? k.GetString() : null;
                    if (key != null)
                    {
                        var vk = ResolveVirtualKey(key);
                        if (vk != 0) { SendKeyDown(vk); Thread.Sleep(30); SendKeyUp(vk); }
                    }
                    break;

                case "keyDown":
                    var kd = step.TryGetProperty("key", out var kd2) ? kd2.GetString() : null;
                    if (kd != null) { var vk = ResolveVirtualKey(kd); if (vk != 0) SendKeyDown(vk); }
                    break;

                case "keyUp":
                    var ku = step.TryGetProperty("key", out var ku2) ? ku2.GetString() : null;
                    if (ku != null) { var vk = ResolveVirtualKey(ku); if (vk != 0) SendKeyUp(vk); }
                    break;

                case "type":
                    var text = step.TryGetProperty("text", out var t2) ? t2.GetString() : null;
                    if (text != null)
                        foreach (var ch in text)
                        {
                            var vkr = VkKeyScan(ch);
                            if (vkr == -1) continue;
                            var vk = (ushort)(vkr & 0xFF);
                            var shift = (vkr & 0x100) != 0;
                            if (shift) SendKeyDown(0x10);
                            SendKeyDown(vk); SendKeyUp(vk);
                            if (shift) SendKeyUp(0x10);
                            await Task.Delay(20);
                        }
                    break;

                case "click":
                    var cx = step.TryGetProperty("x", out var cx2) ? cx2.GetInt32() : -1;
                    var cy = step.TryGetProperty("y", out var cy2) ? cy2.GetInt32() : -1;
                    if (cx >= 0 && cy >= 0) SetCursorPos(cx, cy);
                    SendMouseEvent(MOUSEEVENTF_LEFTDOWN);
                    SendMouseEvent(MOUSEEVENTF_LEFTUP);
                    break;

                case "rightClick":
                    var rx = step.TryGetProperty("x", out var rx2) ? rx2.GetInt32() : -1;
                    var ry = step.TryGetProperty("y", out var ry2) ? ry2.GetInt32() : -1;
                    if (rx >= 0 && ry >= 0) SetCursorPos(rx, ry);
                    SendMouseEvent(MOUSEEVENTF_RIGHTDOWN);
                    SendMouseEvent(MOUSEEVENTF_RIGHTUP);
                    break;

                case "move":
                    var mx = step.TryGetProperty("x", out var mx2) ? mx2.GetInt32() : 0;
                    var my = step.TryGetProperty("y", out var my2) ? my2.GetInt32() : 0;
                    SetCursorPos(mx, my);
                    break;

                case "scroll":
                    var sa = step.TryGetProperty("amount", out var sa2) ? sa2.GetInt32() : 3;
                    var si = new INPUT { type = INPUT_MOUSE, union = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = (uint)(sa * 120) } } };
                    SendInput(1, new[] { si }, Marshal.SizeOf<INPUT>());
                    break;

                case "wait":
                    var ms = step.TryGetProperty("ms", out var ms2) ? ms2.GetInt32() : 100;
                    await Task.Delay(Math.Min(ms, 10000)); // Max 10s per wait
                    break;

                case "combo":
                    var comboKeys = new List<ushort>();
                    if (step.TryGetProperty("keys", out var ck) && ck.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var ki in ck.EnumerateArray())
                        {
                            var vk = ResolveVirtualKey(ki.GetString() ?? "");
                            if (vk != 0) comboKeys.Add(vk);
                        }
                    foreach (var vk in comboKeys) SendKeyDown(vk);
                    Thread.Sleep(50);
                    for (int i = comboKeys.Count - 1; i >= 0; i--) SendKeyUp(comboKeys[i]);
                    break;
            }

            // Small delay between steps unless it was a wait
            if (action != "wait") await Task.Delay(10);
        }

        Logger.Info($"input.macro: completed {stepCount} steps");
        return Success(new { completed = true, stepsExecuted = stepCount });
    }

    /// <summary>
    /// Smoothly move the cursor to a target position with visible animation.
    /// The user sees the cursor glide across the screen — feels natural and innovative.
    /// Uses an ease-out curve for a human-like deceleration effect.
    /// </summary>
    private async Task<NodeInvokeResponse> HandleSmoothMoveAsync(NodeInvokeRequest request)
    {
        var targetX = GetIntArg(request.Args, "x", -1);
        var targetY = GetIntArg(request.Args, "y", -1);
        var durationMs = GetIntArg(request.Args, "durationMs", 300);
        var steps = GetIntArg(request.Args, "steps", 30);

        if (targetX < 0 || targetY < 0) return Error("Missing x/y coordinates");

        GetCursorPos(out var startPos);
        Logger.Info($"input.smoothMove: ({startPos.X},{startPos.Y}) → ({targetX},{targetY}) in {durationMs}ms");

        var stepDelay = Math.Max(1, durationMs / steps);

        for (int i = 1; i <= steps; i++)
        {
            // Ease-out cubic: starts fast, decelerates smoothly
            float t = (float)i / steps;
            float eased = 1f - (1f - t) * (1f - t) * (1f - t);

            var cx = (int)(startPos.X + (targetX - startPos.X) * eased);
            var cy = (int)(startPos.Y + (targetY - startPos.Y) * eased);
            SetCursorPos(cx, cy);
            await Task.Delay(stepDelay);
        }

        // Ensure we land exactly on target
        SetCursorPos(targetX, targetY);

        return Success(new { moved = true, from = new { x = startPos.X, y = startPos.Y }, to = new { x = targetX, y = targetY }, durationMs, steps });
    }

    /// <summary>
    /// Smooth click: glide cursor to target, brief pause, then click.
    /// This is the showcase feature — the user sees the AI "thinking" and moving
    /// the cursor like a human would, then clicking. Very visual and impressive.
    /// </summary>
    private async Task<NodeInvokeResponse> HandleSmoothClickAsync(NodeInvokeRequest request)
    {
        var targetX = GetIntArg(request.Args, "x", -1);
        var targetY = GetIntArg(request.Args, "y", -1);
        var button = GetStringArg(request.Args, "button", "left");
        var moveDurationMs = GetIntArg(request.Args, "moveDurationMs", 300);
        var pauseMs = GetIntArg(request.Args, "pauseMs", 80);
        var doubleClick = GetBoolArg(request.Args, "double", false);

        if (targetX < 0 || targetY < 0) return Error("Missing x/y coordinates");

        GetCursorPos(out var startPos);
        Logger.Info($"input.smoothClick: ({startPos.X},{startPos.Y}) → ({targetX},{targetY}) then {button} click");

        // Phase 1: Smooth move
        int steps = 25;
        int stepDelay = Math.Max(1, moveDurationMs / steps);

        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            float eased = 1f - (1f - t) * (1f - t) * (1f - t);
            var cx = (int)(startPos.X + (targetX - startPos.X) * eased);
            var cy = (int)(startPos.Y + (targetY - startPos.Y) * eased);
            SetCursorPos(cx, cy);
            await Task.Delay(stepDelay);
        }
        SetCursorPos(targetX, targetY);

        // Phase 2: Brief pause (mimics human "aiming")
        await Task.Delay(pauseMs);

        // Phase 3: Click
        var (downFlag, upFlag) = button?.ToLowerInvariant() switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
        };

        SendMouseEvent(downFlag);
        await Task.Delay(30); // Human-like hold
        SendMouseEvent(upFlag);

        if (doubleClick)
        {
            await Task.Delay(80);
            SendMouseEvent(downFlag);
            await Task.Delay(30);
            SendMouseEvent(upFlag);
        }

        return Success(new { clicked = true, button, x = targetX, y = targetY, smooth = true, doubleClick });
    }

    private NodeInvokeResponse HandleGetCursorPos()
    {
        GetCursorPos(out var pos);
        return Success(new { x = pos.X, y = pos.Y });
    }

    // --- Low-level helpers ---

    private void SendKeyDown(ushort vk)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void SendKeyUp(ushort vk)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void SendMouseEvent(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE, union = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Resolve a key name to its Windows Virtual Key code.
    /// Supports named keys (enter, space, ctrl, etc.) and single characters.
    /// </summary>
    private static ushort ResolveVirtualKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;

        // Named keys
        var vk = key.ToLowerInvariant() switch
        {
            // Modifiers
            "ctrl" or "control" or "lctrl" => 0x11,
            "rctrl" => 0xA3,
            "shift" or "lshift" => 0x10,
            "rshift" => 0xA1,
            "alt" or "lalt" or "menu" => 0x12,
            "ralt" => 0xA5,
            "win" or "lwin" or "super" => 0x5B,
            "rwin" => 0x5C,

            // Navigation
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "space" => 0x20,
            "backspace" or "back" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "escape" or "esc" => 0x1B,

            // Arrow keys
            "up" or "arrowup" => 0x26,
            "down" or "arrowdown" => 0x28,
            "left" or "arrowleft" => 0x25,
            "right" or "arrowright" => 0x27,

            // Function keys
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,

            // Media keys
            "volumeup" => 0xAF,
            "volumedown" => 0xAE,
            "volumemute" or "mute" => 0xAD,
            "playpause" or "play" => 0xB3,
            "next" or "nexttrack" => 0xB0,
            "prev" or "prevtrack" => 0xB1,
            "stop" => 0xB2,

            // Other
            "capslock" or "caps" => 0x14,
            "numlock" => 0x90,
            "scrolllock" => 0x91,
            "printscreen" or "prtsc" => 0x2C,
            "pause" => 0x13,
            "apps" or "contextmenu" => 0x5D,

            _ => (ushort)0
        };

        if (vk != 0) return (ushort)vk;

        // Single character: use VkKeyScan
        if (key.Length == 1)
        {
            var result = VkKeyScan(key[0]);
            if (result != -1) return (ushort)(result & 0xFF);
        }

        // Try parsing as hex (0x41) or decimal virtual key code
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(key[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexVk))
            return hexVk;

        if (ushort.TryParse(key, out var decVk))
            return decVk;

        return 0;
    }
}
