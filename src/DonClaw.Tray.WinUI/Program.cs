using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DonClaw.Shared;
using DonClaw.Tray.Services;

namespace DonClaw.Tray;

/// <summary>
/// DonClaw Node — headless tray app. No WinUI XAML needed.
/// Uses Win32 Shell_NotifyIcon for the system tray icon and a simple WinForms-style message loop.
/// </summary>
static class Program
{
    // Win32 tray icon interop
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInstance, string name, uint type, int cx, int cy, uint flags);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int pt_x, pt_y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName;
    }

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    private static IntPtr _hWnd;
    private static NOTIFYICONDATA _nid;
    private static NodeService? _nodeService;
    private static SettingsManager? _settings;
    private static WndProcDelegate? _wndProcDelegate; // prevent GC

    [STAThread]
    static void Main()
    {
        // Single instance
        using var mutex = new Mutex(true, "DonClawNodeMutex", out bool created);
        if (!created) { Logger.Info("Already running"); return; }

        Logger.Info("=== DonClaw Node starting ===");

        _settings = new SettingsManager();

        // Register auto-start
        if (_settings.AutoStart)
            AutoStartManager.Set(true);

        // Create hidden window for message pump
        var hInstance = GetModuleHandle(null);
        _wndProcDelegate = WndProc;
        var wc = new WNDCLASS
        {
            lpszClassName = "DonClawTrayClass",
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance
        };
        RegisterClass(ref wc);
        _hWnd = CreateWindowEx(0, "DonClawTrayClass", "DonClawNode", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        // Create tray icon
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "donclaw.ico");
        var hIcon = File.Exists(iconPath)
            ? LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE)
            : IntPtr.Zero;

        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = hIcon,
            szTip = "DonClaw Node — Starting..."
        };
        Shell_NotifyIcon(NIM_ADD, ref _nid);

        // Start node service
        _nodeService = new NodeService(new AppLogger());
        _nodeService.StatusChanged += OnStatusChanged;
        _nodeService.NotifyRequested += OnNotify;

        if (_settings.IsConfigured)
        {
            _ = Task.Run(async () =>
            {
                try { await _nodeService.ConnectAsync(_settings.GatewayHost, _settings.Token); }
                catch (Exception ex) { Logger.Error($"Connect failed: {ex.Message}"); }
            });
        }
        else
        {
            ShowBalloon("DonClaw Node", "Not configured. Edit settings.json in %APPDATA%\\DonClawNode");
            Logger.Info("Not configured — user needs to edit settings.json");
        }

        // Message loop
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Cleanup
        Shell_NotifyIcon(NIM_DELETE, ref _nid);
        _nodeService?.Dispose();
        Logger.Info("=== DonClaw Node stopped ===");
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                // Open settings file
                var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DonClawNode", "settings.json");
                try { Process.Start(new ProcessStartInfo(settingsPath) { UseShellExecute = true }); } catch { }
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                // Open log file
                try { Process.Start(new ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true }); } catch { }
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        var tip = status switch
        {
            ConnectionStatus.Connected => "DonClaw Node — Connected ✓",
            ConnectionStatus.Connecting => "DonClaw Node — Connecting...",
            ConnectionStatus.Error => "DonClaw Node — Error",
            _ => "DonClaw Node — Disconnected"
        };

        _nid.szTip = tip;
        _nid.uFlags = NIF_TIP;

        // Try to load status-specific icon
        var iconName = status switch
        {
            ConnectionStatus.Connected => "donclaw-connected.ico",
            ConnectionStatus.Connecting => "donclaw-connecting.ico",
            _ => "donclaw-disconnected.ico"
        };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", iconName);
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "donclaw.ico");

        if (File.Exists(iconPath))
        {
            var hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            if (hIcon != IntPtr.Zero)
            {
                _nid.hIcon = hIcon;
                _nid.uFlags |= NIF_ICON;
            }
        }

        Shell_NotifyIcon(NIM_MODIFY, ref _nid);

        if (status == ConnectionStatus.Connected)
            ShowBalloon("DonClaw Node", "Connected and ready. Your PC is now controllable.");
    }

    private static void OnNotify(object? sender, DonClaw.Shared.Capabilities.SystemNotifyArgs args)
    {
        ShowBalloon(args.Title, args.Body);
    }

    private static void ShowBalloon(string title, string text)
    {
        _nid.uFlags = NIF_INFO;
        _nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        _nid.szInfo = text.Length > 255 ? text[..255] : text;
        _nid.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }
}
