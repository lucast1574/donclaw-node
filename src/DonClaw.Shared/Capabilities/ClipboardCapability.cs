using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DonClaw.Shared.Capabilities;

/// <summary>
/// Clipboard capability — read and write the Windows clipboard.
/// Useful for copy/paste automation, transferring data between AI and user's apps.
/// </summary>
public class ClipboardCapability : NodeCapabilityBase
{
    public override string Category => "clipboard";

    private static readonly string[] _commands = { "clipboard.get", "clipboard.set", "clipboard.clear" };
    public override IReadOnlyList<string> Commands => _commands;

    // Clipboard requires STA thread on Windows
    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public ClipboardCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "clipboard.get" => await RunOnStaThread(() => HandleGet()),
            "clipboard.set" => await RunOnStaThread(() => HandleSet(request)),
            "clipboard.clear" => await RunOnStaThread(() => HandleClear()),
            _ => Error($"Unknown: {request.Command}")
        };
    }

    private NodeInvokeResponse HandleGet()
    {
        Logger.Info("clipboard.get");

        try
        {
            if (!OpenClipboard(IntPtr.Zero))
                return Error("Failed to open clipboard");

            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                    return Success(new { text = (string?)null, hasText = false });

                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                    return Error("Failed to lock clipboard data");

                try
                {
                    var text = Marshal.PtrToStringUni(ptr) ?? "";
                    return Success(new { text, hasText = true, length = text.Length });
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex) { return Error($"Get failed: {ex.Message}"); }
    }

    private NodeInvokeResponse HandleSet(NodeInvokeRequest request)
    {
        var text = GetStringArg(request.Args, "text");
        if (text == null) return Error("Missing text parameter");

        Logger.Info($"clipboard.set: {text.Length} chars");

        try
        {
            if (!OpenClipboard(IntPtr.Zero))
                return Error("Failed to open clipboard");

            try
            {
                EmptyClipboard();

                var bytes = (text.Length + 1) * 2; // Unicode: 2 bytes per char + null terminator
                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return Error("Failed to allocate memory");

                var ptr = GlobalLock(hMem);
                if (ptr == IntPtr.Zero) return Error("Failed to lock memory");

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                    Marshal.WriteInt16(ptr, text.Length * 2, 0); // Null terminator
                }
                finally
                {
                    GlobalUnlock(hMem);
                }

                SetClipboardData(CF_UNICODETEXT, hMem);

                return Success(new { set = true, length = text.Length });
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex) { return Error($"Set failed: {ex.Message}"); }
    }

    private NodeInvokeResponse HandleClear()
    {
        Logger.Info("clipboard.clear");

        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return Error("Failed to open clipboard");
            try { EmptyClipboard(); }
            finally { CloseClipboard(); }
            return Success(new { cleared = true });
        }
        catch (Exception ex) { return Error($"Clear failed: {ex.Message}"); }
    }

    /// <summary>
    /// Clipboard operations require STA thread on Windows.
    /// </summary>
    private Task<NodeInvokeResponse> RunOnStaThread(Func<NodeInvokeResponse> action)
    {
        var tcs = new TaskCompletionSource<NodeInvokeResponse>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetResult(Error($"STA thread error: {ex.Message}")); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
