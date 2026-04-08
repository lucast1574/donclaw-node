# DonClaw Node — Build Verification Checklist

## File Count Verification

```bash
# C# source files
find . -name "*.cs" | wc -l
# Expected: 24

# Project files
find . -name "*.csproj" | wc -l
# Expected: 2

# XAML files
find . -name "*.xaml" | wc -l
# Expected: 2 (App.xaml, SettingsWindow.xaml)

# Total compilable units
find . \( -name "*.cs" -o -name "*.csproj" -o -name "*.xaml" \) | wc -l
# Expected: 28
```

## Structure Verification

```
✅ src/DonClaw.Shared/
   ✅ DonClaw.Shared.csproj
   ✅ 11 core files (WebSocketClientBase, DonClawNodeClient, DeviceIdentity, etc.)
   ✅ Capabilities/ (8 capabilities)

✅ src/DonClaw.Tray.WinUI/
   ✅ DonClaw.Tray.WinUI.csproj
   ✅ App.xaml + App.xaml.cs
   ✅ Services/ (4 services)
   ✅ Windows/ (1 window: SettingsWindow)
   ✅ Assets/ (icons + MSIX assets)
   ✅ app.manifest
   ✅ Package.appxmanifest

✅ Root/
   ✅ DonClaw.Node.slnx
   ✅ README.md
   ✅ PROJECT_SUMMARY.md
   ✅ VERIFICATION.md (this file)
```

## Capability Verification

All 8 capabilities present:

1. ✅ SystemCapability — system.notify, system.run, system.which, system.execApprovals.*
2. ✅ ScreenCapability — screen.capture, screen.list
3. ✅ CameraCapability — camera.list, camera.snap
4. ✅ CanvasCapability — canvas.present, canvas.hide, canvas.navigate, canvas.eval, canvas.snapshot, canvas.a2ui.*
5. ✅ BrowserCapability — browser.open, browser.openTab, browser.list (NEW)
6. ✅ AppCapability — app.launch, app.list, app.close, app.focus (NEW)
7. ✅ FileCapability — file.list, file.read, file.info, file.search, file.open (NEW)
8. ✅ PowerCapability — power.shutdown, power.restart, power.sleep, power.lock, power.status (NEW)

## Namespace Verification

All files use DonClaw namespaces:
- ✅ DonClaw.Shared
- ✅ DonClaw.Shared.Capabilities
- ✅ DonClawTray
- ✅ DonClawTray.Services
- ✅ DonClawTray.Windows

No references to "OpenClaw" in namespaces (only in comments/docs).

## Branding Verification

- ✅ ClientId = "donclaw-node"
- ✅ Display names: "DonClaw Node"
- ✅ Settings path: %APPDATA%\DonClawNode
- ✅ Log path: %LOCALAPPDATA%\DonClawNode
- ✅ Auto-start registry key: "DonClawNode"

## Build Readiness

✅ **All source files created**
✅ **Solution file present**
✅ **Project files have correct TargetFramework (net10.0)**
✅ **PackageReferences present** (NSec, WinUIEx, Toolkit.Uwp.Notifications, WindowsAppSDK)
⚠️  **Icon files are placeholders** (need real .ico files)
⚠️  **Screen/Camera/Canvas services not implemented** (event handlers wired, but no actual capture code)

## Next Steps for Build Testing

1. Copy this project to a Windows machine with:
   - .NET 10 SDK installed
   - Windows 10 SDK (10.0.19041.0+)
   - Visual Studio 2022 17.12+ or Rider

2. Replace placeholder icons:
   ```bash
   # Replace these with real .ico files:
   src/DonClaw.Tray.WinUI/Assets/donclaw.ico
   src/DonClaw.Tray.WinUI/Assets/donclaw-connected.ico
   src/DonClaw.Tray.WinUI/Assets/donclaw-connecting.ico
   src/DonClaw.Tray.WinUI/Assets/donclaw-disconnected.ico
   ```

3. Build:
   ```bash
   dotnet build DonClaw.Node.slnx
   ```

4. Run:
   ```bash
   dotnet run --project src/DonClaw.Tray.WinUI/DonClaw.Tray.WinUI.csproj
   ```

5. Test connection:
   - Open Settings
   - Enter gateway host (ws://localhost:18789 or your DonClaw instance)
   - Enter token
   - Click "Save & Connect"
   - Check tray icon changes to "connecting" then "connected"
   - On gateway, run: `openclaw devices list` (should show pending device)
   - Approve: `openclaw devices approve <device-id>`
   - Verify tray icon shows "connected & paired"

## Test Commands (after pairing)

From your DonClaw agent:

```javascript
// Browser
await node.invoke('browser.open', { url: 'https://google.com' })

// App
await node.invoke('app.launch', { path: 'notepad.exe' })
await node.invoke('app.list', {})

// File
await node.invoke('file.list', { path: 'C:\\Users' })
await node.invoke('file.read', { path: 'C:\\test.txt' })

// Power
await node.invoke('power.status', {})
await node.invoke('power.lock', {})

// System
await node.invoke('system.notify', { title: 'Hello', body: 'Test notification' })
await node.invoke('system.run', { command: ['echo', 'Hello World'] })
```

---

**Created:** 2026-04-08  
**Status:** ✅ Ready for build testing on Windows
