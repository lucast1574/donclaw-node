# 🐾 DonClaw Node

<p align="center">
  <img src="src/DonClaw.Tray.WinUI/Assets/logo.png" alt="DonClaw" width="200"/>
</p>

**DonClaw Node** es una aplicación de system tray para Windows que convierte tu PC en un nodo controlable desde una instancia de [OpenClaw](https://openclaw.ai) en la nube. Una vez conectado, tu asistente AI puede interactuar con tu PC **de forma visible** — abriendo navegadores, lanzando apps, leyendo archivos, ejecutando comandos y más. Todo sucede en tu pantalla, frente a ti.

## 🚀 Quick Start

### Requisitos
- **Windows 10** (20H2+) o **Windows 11**
- **.NET 10.0 SDK** — https://dotnet.microsoft.com/download/dotnet/10.0
- **WebView2 Runtime** — pre-instalado en Windows moderno

### Compilar y ejecutar

```powershell
# Clonar el repositorio
git clone https://github.com/lucast1574/donclaw-node.git
cd donclaw-node

# Compilar (elegir tu arquitectura)
dotnet build src/DonClaw.Tray.WinUI -r win-x64       # Para x64
dotnet build src/DonClaw.Tray.WinUI -r win-arm64      # Para ARM64

# Ejecutar
.\src\DonClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\DonClaw.Tray.WinUI.exe
```

### Configurar
1. El icono 🐾 aparece en el system tray
2. **Click derecho → Settings** (o doble click)
3. Ingresa tu **Gateway Host** (`ws://tu-instancia:18789`) y **Token**
4. **Save & Connect** → ¡tu PC es un nodo!

---

## 🎯 ¿Qué es DonClaw Node?

DonClaw Node es parte del ecosistema **DonClaw**, una plataforma SaaS que genera instancias de OpenClaw en la nube con IP pública. El problema: sin acceso al PC del usuario, la IA no puede ayudar realmente con tareas locales. DonClaw Node resuelve esto.

### Arquitectura

```
┌─────────────────────┐     ┌──────────────────────────┐     ┌─────────────────────┐
│  DonClaw Dashboard  │────▶│  OpenClaw Instance (nube) │◀───▶│  DonClaw Node (PC)  │
│     (Next.js)       │     │     Gateway + AI Agent    │ WS  │    System Tray App   │
└─────────────────────┘     └──────────────────────────┘     └─────────────────────┘
        Web UI                   Tu instancia cloud              Tu computadora
```

1. El usuario crea una instancia de OpenClaw en DonClaw (cloud)
2. Instala **DonClaw Node** en su PC Windows
3. Configura el host + token de su instancia
4. El AI assistant ahora puede controlar su PC vía WebSocket

### Principio clave: **Todo es visible**

DonClaw Node NO es un control remoto oculto. Todo lo que el AI hace, **el usuario lo ve en su pantalla**:
- ¿Abre Chrome? → El usuario ve Chrome abrirse
- ¿Lanza VS Code? → La ventana aparece en su escritorio
- ¿Abre un PDF? → Se abre en el visor predeterminado
- ¿Muestra una notificación? → Aparece como toast de Windows

Es un **asistente que trabaja delante del usuario**, no detrás.

---

## 🛠️ Capabilities (8 módulos, 40+ comandos)

### 🌐 Browser — Control del navegador

| Comando | Descripción | Visible |
|---|---|---|
| `browser.open` | Abre URL en navegador predeterminado | ✅ Se abre Chrome/Edge/Firefox |
| `browser.openTab` | Abre nueva pestaña (soporta Chrome, Edge, Firefox) | ✅ Aparece la pestaña |
| `browser.list` | Lista procesos de navegadores con títulos de ventana | — |

**Ejemplo:** El AI puede decir "abre la documentación de React" → Chrome se abre con la URL.

### 📱 App — Gestión de aplicaciones

| Comando | Descripción | Visible |
|---|---|---|
| `app.launch` | Lanza una app por nombre o ruta | ✅ La app aparece en pantalla |
| `app.list` | Lista apps con ventana (PID, nombre, título, RAM) | — |
| `app.close` | Cierra app por PID o nombre (graceful o force) | ✅ La ventana se cierra |
| `app.focus` | Trae ventana al frente (Win32 `SetForegroundWindow`) | ✅ La ventana aparece |

**Ejemplo:** `app.launch({path: "code", arguments: "C:\\proyecto"})` → VS Code se abre con el proyecto.

### 📁 File — Sistema de archivos completo

**El módulo más potente.** Diseñado para que el AI pueda leer, entender, y modificar código del usuario.

| Comando | Descripción |
|---|---|
| `file.list` | Lista contenido de directorio (archivos + carpetas) |
| `file.read` | Lee un archivo (con offset/limit para archivos grandes, máx 2MB) |
| `file.readMultiple` | **Lee hasta 20 archivos de golpe** (máx 10MB total) — ideal para contexto de proyecto |
| `file.write` | Escribe/crea archivos (crea carpetas automáticamente, máx 5MB) |
| `file.edit` | **Edita con search & replace** (oldText → newText, soporta múltiples edits) |
| `file.tree` | **Árbol del proyecto** — salta `node_modules`, `.git`, `bin`, `obj`, etc. automáticamente |
| `file.grep` | **Busca texto dentro de archivos** — grep recursivo, ignora binarios y carpetas conocidas |
| `file.info` | Metadata de archivo/directorio (tamaño, fechas, permisos) |
| `file.search` | Busca archivos por patrón de nombre |
| `file.open` | Abre archivo con app predeterminada (✅ visible) |
| `file.mkdir` | Crea directorios (con padres) |
| `file.delete` | Elimina archivo/directorio (requiere `confirm: true` por seguridad) |

**Flujo típico para ayudar con código:**
```
1. file.tree({path: "C:\\proyecto"})          → El AI ve la estructura
2. file.readMultiple({paths: [...]})          → Lee los archivos clave
3. file.grep({path: "C:\\proyecto", query: "TODO"}) → Encuentra TODOs
4. file.edit({path: "...", edits: [...]})     → Aplica cambios
5. file.write({path: "...", content: "..."})  → Crea archivos nuevos
```

### ⚡ Power — Control de energía

| Comando | Descripción |
|---|---|
| `power.shutdown` | Apaga el PC (delay configurable, default 60s) |
| `power.restart` | Reinicia el PC |
| `power.sleep` | Suspende/hiberna el PC |
| `power.lock` | Bloquea la sesión (Win32 `LockWorkStation`) |
| `power.status` | Estado: batería %, fuente de energía, uptime, info del sistema |

### 💻 System — Ejecución y notificaciones

| Comando | Descripción |
|---|---|
| `system.run` | Ejecuta comandos (PowerShell, cmd, pwsh) con timeout y captura de output |
| `system.run.prepare` | Pre-flight: muestra qué se ejecutaría sin ejecutar |
| `system.notify` | Muestra notificación toast de Windows |
| `system.which` | Busca ejecutables en PATH |
| `system.execApprovals.get` | Ver política de ejecución actual |
| `system.execApprovals.set` | Configurar reglas de qué comandos están permitidos |

**Exec Approval Policy:** `system.run` está protegido por reglas configurables. Por defecto:
- ✅ Permitido: `echo`, `Get-*`, `dir`, `hostname`, `whoami`, `systeminfo`
- ❌ Bloqueado: `rm`, `del`, `Remove-Item`, `Format-*`, `shutdown`, `net`
- Las reglas se guardan en `%LOCALAPPDATA%\DonClawNode\exec-policy.json`

### 📸 Screen — Capturas de pantalla

| Comando | Descripción |
|---|---|
| `screen.capture` | Captura screenshot (formato, calidad, monitor) |
| `screen.list` | Lista monitores conectados |

### 📷 Camera — Cámara

| Comando | Descripción |
|---|---|
| `camera.list` | Lista cámaras disponibles |
| `camera.snap` | Toma foto con una cámara específica |

### 🎨 Canvas — Ventana WebView2

| Comando | Descripción |
|---|---|
| `canvas.present` | Muestra ventana con URL o HTML (✅ visible) |
| `canvas.hide` | Oculta la ventana canvas |
| `canvas.navigate` | Navega a otra URL |
| `canvas.eval` | Ejecuta JavaScript en la ventana |
| `canvas.snapshot` | Captura screenshot del canvas |

---

## 📁 Estructura del proyecto

```
donclaw-node/
├── DonClaw.Node.slnx                    # Solución
├── README.md
└── src/
    ├── DonClaw.Shared/                   # Librería core (portable)
    │   ├── DonClaw.Shared.csproj
    │   ├── DonClawNodeClient.cs          # Cliente WebSocket + protocolo de nodo
    │   ├── WebSocketClientBase.cs        # Base WebSocket con auto-reconnect
    │   ├── DeviceIdentity.cs             # Identidad Ed25519 para pairing
    │   ├── NodeCapabilities.cs           # Interfaces INodeCapability
    │   ├── Models.cs                     # ConnectionStatus, PairingStatus
    │   ├── GatewayUrlHelper.cs           # Normalización de URLs
    │   ├── ShellQuoting.cs               # Quoting seguro de argumentos
    │   ├── ICommandRunner.cs             # Interfaz de ejecución de comandos
    │   ├── LocalCommandRunner.cs         # Implementación local (Process.Start)
    │   ├── ExecApprovalPolicy.cs         # Política de aprobación de ejecución
    │   ├── IOpenClawLogger.cs            # Interfaz de logging
    │   └── Capabilities/
    │       ├── BrowserCapability.cs      # 🌐 3 comandos
    │       ├── AppCapability.cs          # 📱 4 comandos
    │       ├── FileCapability.cs         # 📁 12 comandos (el más completo)
    │       ├── PowerCapability.cs        # ⚡ 5 comandos
    │       ├── SystemCapability.cs       # 💻 6 comandos
    │       ├── ScreenCapability.cs       # 📸 2 comandos
    │       ├── CameraCapability.cs       # 📷 2 comandos
    │       └── CanvasCapability.cs       # 🎨 5 comandos
    └── DonClaw.Tray.WinUI/              # App de system tray (WinUI 3)
        ├── DonClaw.Tray.WinUI.csproj
        ├── App.xaml / App.xaml.cs        # Lifecycle, tray icon, menú
        ├── Package.appxmanifest
        ├── app.manifest
        ├── Assets/
        │   ├── icon.png                  # Icono DonClaw
        │   └── logo.png                  # Logo DonClaw
        ├── Services/
        │   ├── NodeService.cs            # Orquesta cliente + capabilities
        │   ├── SettingsManager.cs        # Carga/guarda settings.json
        │   ├── AutoStartManager.cs       # Registro de auto-inicio Windows
        │   └── Logger.cs                 # File logger con rotación
        └── Windows/
            └── SettingsWindow.xaml/.cs    # UI: Host + Token + Connect

```

## 🔐 Seguridad

- **Ed25519 Device Identity** — Cada nodo tiene un par de claves criptográficas único. El gateway verifica la firma del dispositivo en cada conexión.
- **Exec Approval Policy** — Reglas configurables (glob pattern matching) que determinan qué comandos puede ejecutar `system.run`. Por defecto, solo comandos de lectura están permitidos.
- **Pairing Flow** — El primer connect genera una solicitud de pairing que debe aprobarse en el gateway.
- **Token Auth** — Requiere token del gateway para conectarse.
- **File Delete Safety** — `file.delete` requiere `confirm: true` explícito.
- **Todo visible** — El usuario siempre ve lo que sucede en su PC.

## ⚙️ Archivos de configuración

| Archivo | Ubicación | Descripción |
|---|---|---|
| Settings | `%APPDATA%\DonClawNode\settings.json` | Host, token, auto-start |
| Device Key | `%LOCALAPPDATA%\DonClawNode\device-key-ed25519.json` | Identidad criptográfica |
| Exec Policy | `%LOCALAPPDATA%\DonClawNode\exec-policy.json` | Reglas de ejecución de comandos |
| Logs | `%LOCALAPPDATA%\DonClawNode\donclaw-node.log` | Logs con rotación (máx 5MB) |

## 🧬 Origen

Basado en [OpenClaw Windows Hub](https://github.com/openclaw/openclaw-windows-node) (Molty), simplificado y extendido:
- ✂️ **Removido:** UI de operador (chat, sessions, activity stream, channels), CLI, CommandPalette
- ➕ **Agregado:** BrowserCapability, AppCapability, FileCapability (12 comandos), PowerCapability
- 🎯 **Enfoque:** Solo nodo. Solo lo esencial. Host + Token → Conectar.

## 📊 Stats

- **~4,800 líneas** de C#
- **34 archivos** en 2 proyectos
- **8 capabilities** con **40+ comandos**
- **0 dependencias** externas más allá de .NET y WinUI

## 📄 License

MIT

---

*Hecho con 🐾 por [lucast1574](https://github.com/lucast1574)*
