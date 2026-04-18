# 🐾 DonClaw Node

<p align="center">
  <img src="src/DonClaw.Tray.WinUI/Assets/logo.png" alt="DonClaw" width="200"/>
</p>

**DonClaw Node** es una aplicación de system tray para Windows que convierte tu PC en un nodo controlable desde una instancia de [OpenClaw](https://openclaw.ai) en la nube. Una vez conectado, tu asistente AI puede interactuar con tu PC **de forma visible** — abriendo navegadores, lanzando apps, leyendo archivos, escribiendo, haciendo clic, ejecutando comandos y más. Todo sucede en tu pantalla, frente a ti.

---

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

A partir de la primera ejecución, DonClaw Node:
- Crea un **acceso directo en el menú Inicio** ("DonClaw Node") para que puedas buscarlo y pinearlo a la barra de tareas.
- Se registra en **auto-inicio** de Windows (puedes desactivarlo desde Settings).

### Configurar
1. El icono 🐾 aparece en el system tray
2. **Click derecho → Settings** (o doble click)
3. Ingresa tu **Gateway Host** — formatos válidos:
   - `ws://192.168.1.50:18789` (red local)
   - `wss://your-instance.tailscale.ts.net` (Tailscale)
   - `wss://tu-instancia.donclaw.com` (cloud)
4. Ingresa tu **Token** del gateway
5. Marca/desmarca **"Start with Windows"** si quieres
6. **🔗 Connect** → ¡tu PC es un nodo!
7. **⛔ Stop** detiene la conexión sin cerrar la app
8. **🔌 Test** verifica que el host responde

---

## 🎯 ¿Qué es DonClaw Node?

DonClaw Node es parte del ecosistema **DonClaw**, una plataforma SaaS que genera instancias de OpenClaw en la nube con IP pública. El problema: sin acceso al PC del usuario, la IA no puede ayudar realmente con tareas locales. DonClaw Node lo resuelve.

### Arquitectura

```
┌─────────────────────┐     ┌──────────────────────────┐     ┌─────────────────────┐
│  DonClaw Dashboard  │────▶│  OpenClaw Instance (nube) │◀───▶│  DonClaw Node (PC)  │
│     (Next.js)       │     │     Gateway + AI Agent    │ WS  │    System Tray App   │
└─────────────────────┘     └──────────────────────────┘     └─────────────────────┘
        Web UI                   Tu instancia cloud              Tu computadora
                                          │
                                          │ HTTP local
                                          ▼
                                  ┌─────────────────┐
                                  │  Local API :9800 │   ← Otros agentes locales
                                  └─────────────────┘     pueden hablar directo
```

1. El usuario crea una instancia de OpenClaw en DonClaw (cloud)
2. Instala **DonClaw Node** en su PC Windows
3. Configura host + token de su instancia
4. El AI assistant ahora puede controlar su PC vía WebSocket
5. (Opcional) Otros agentes locales pueden hablar con el nodo vía HTTP en `localhost:9800`

### Principio clave: **Todo es visible**

DonClaw Node NO es un control remoto oculto. Todo lo que el AI hace, **el usuario lo ve en su pantalla**:
- ¿Abre Chrome? → El usuario ve Chrome abrirse
- ¿Lanza VS Code? → La ventana aparece en su escritorio
- ¿Hace clic? → El cursor se mueve suavemente y hace clic
- ¿Escribe? → Las teclas se escriben de verdad
- ¿Muestra una notificación? → Aparece como toast de Windows

Es un **asistente que trabaja delante del usuario**, no detrás.

---

## 🛠️ Capabilities (11 módulos, 61 comandos)

### 🌐 Browser — Control del navegador (3)

| Comando | Descripción | Visible |
|---|---|---|
| `browser.open` | Abre URL en navegador predeterminado | ✅ |
| `browser.openTab` | Abre nueva pestaña (Chrome, Edge, Firefox) | ✅ |
| `browser.list` | Lista navegadores con títulos de ventana | — |

### 📱 App — Gestión de aplicaciones (4)

| Comando | Descripción | Visible |
|---|---|---|
| `app.launch` | Lanza una app por nombre o ruta | ✅ |
| `app.list` | Lista apps con ventana (PID, nombre, título, RAM) | — |
| `app.close` | Cierra app por PID o nombre (graceful o force) | ✅ |
| `app.focus` | Trae ventana al frente (Win32 `SetForegroundWindow`) | ✅ |

### 📁 File — Sistema de archivos completo (12)

**El módulo más potente.** Diseñado para que el AI pueda leer, entender y modificar código del usuario.

| Comando | Descripción |
|---|---|
| `file.list` | Lista contenido de directorio |
| `file.read` | Lee un archivo (offset/limit, máx 2MB) |
| `file.readMultiple` | **Hasta 20 archivos de golpe** (máx 10MB total) |
| `file.write` | Escribe/crea archivos (crea carpetas, máx 5MB) |
| `file.edit` | **Search & replace** (`oldText` → `newText`, múltiples edits) |
| `file.tree` | **Árbol del proyecto** — salta `node_modules`, `.git`, `bin`, `obj`, etc. |
| `file.grep` | **Grep recursivo** — ignora binarios y carpetas conocidas |
| `file.info` | Metadata (tamaño, fechas, permisos) |
| `file.search` | Busca archivos por patrón de nombre |
| `file.open` | Abre archivo con app predeterminada (✅ visible) |
| `file.mkdir` | Crea directorios (con padres) |
| `file.delete` | Elimina (requiere `confirm: true` por seguridad) |

### ⚡ Power — Control de energía (5)

| Comando | Descripción |
|---|---|
| `power.shutdown` | Apaga el PC (delay configurable, default 60s) |
| `power.restart` | Reinicia el PC |
| `power.sleep` | Suspende/hiberna el PC |
| `power.lock` | Bloquea la sesión (Win32 `LockWorkStation`) |
| `power.status` | Batería, fuente de energía, uptime, info del sistema |

### 💻 System — Ejecución y notificaciones (6)

| Comando | Descripción |
|---|---|
| `system.run` | Ejecuta comandos (PowerShell, cmd, pwsh) con timeout y captura de output |
| `system.run.prepare` | Valida un comando contra la exec policy sin ejecutarlo |
| `system.notify` | Muestra notificación toast de Windows |
| `system.which` | Busca ejecutables en PATH |
| `system.execApprovals.get` | Lee la exec approval policy actual |
| `system.execApprovals.set` | Actualiza la exec approval policy (glob patterns) |

**Exec Approval Policy:** `system.run` está protegido por reglas configurables (glob patterns). Por defecto:
- ✅ Permitido: `echo`, `Get-*`, `dir`, `hostname`, `whoami`, `systeminfo`
- ❌ Bloqueado: `rm`, `del`, `Remove-Item`, `Format-*`, `shutdown`, `net`
- Reglas en `%LOCALAPPDATA%\DonClawNode\exec-policy.json`

### 📸 Screen — Capturas de pantalla (2)

| Comando | Descripción |
|---|---|
| `screen.capture` | Captura screenshot (formato, calidad, monitor, región) |
| `screen.list` | Lista monitores conectados |

> **Nota:** A partir de v7.x el flujo recomendado es **OCR local** (`Input` + `find`/`act` vía Local API) en vez de mandar screenshots a la IA. Las screen capabilities siguen disponibles para casos donde realmente necesitas la imagen.

### 📷 Camera — Cámara (3)

| Comando | Descripción |
|---|---|
| `camera.list` | Lista cámaras disponibles |
| `camera.snap` | Toma foto con una cámara específica |
| `camera.clip` | Graba un clip de video |

### 🎨 Canvas — Ventana WebView2 (5)

| Comando | Descripción |
|---|---|
| `canvas.present` | Muestra ventana con URL o HTML (✅ visible) |
| `canvas.hide` | Oculta la ventana canvas |
| `canvas.navigate` | Navega a otra URL |
| `canvas.eval` | Ejecuta JavaScript en la ventana |
| `canvas.snapshot` | Captura screenshot del canvas |

### 📋 Clipboard — Portapapeles (3)

| Comando | Descripción |
|---|---|
| `clipboard.get` | Lee el contenido del portapapeles |
| `clipboard.set` | Escribe texto en el portapapeles |
| `clipboard.clear` | Vacía el portapapeles |

### 🖱️ Input — Mouse y teclado nativos (15)

Control del cursor y teclado a nivel Win32. Soporta **movimiento suave** del cursor (ease-in-out), no jumps de 1 pixel.

| Comando | Descripción |
|---|---|
| `input.mouseClick` | Click en posición (left/right/middle, single/double) |
| `input.mouseDown` / `input.mouseUp` | Botón presionado/soltado (para drag manual) |
| `input.mouseMove` | Mueve cursor instantáneo |
| `input.smoothMove` | **Mueve cursor con animación suave** |
| `input.smoothClick` | **Mueve suavemente y hace clic** |
| `input.mouseDrag` | Drag completo (down → move → up) |
| `input.mouseScroll` | Scroll del mouse wheel |
| `input.getCursorPos` | Posición actual del cursor |
| `input.type` | Escribe texto |
| `input.keyPress` | Presiona y suelta una tecla |
| `input.keyDown` / `input.keyUp` | Para combinaciones manuales |
| `input.combo` | Atajos de teclado (e.g. `ctrl+c`) |
| `input.macro` | Secuencia de inputs en orden |

### 🎤 Microphone — Audio (3)

| Comando | Descripción |
|---|---|
| `microphone.list` | Lista micrófonos disponibles |
| `microphone.record` | Inicia grabación |
| `microphone.stop` | Detiene grabación y devuelve audio |

---

## ⚡ Smart Layer (Tray-side services)

Sobre los capabilities, DonClaw Node corre servicios locales que reducen las round-trips entre la IA y el nodo.

### 🔍 TextFinder — OCR local con Windows.Media.Ocr

La IA no necesita ver tu pantalla. Le dice al nodo "encuentra el botón **Play**" y el nodo:
1. Captura la pantalla
2. La pasa por el OCR built-in de Windows
3. Devuelve `{ x, y, w, h, confidence }` listo para hacer clic

⇒ **Privacidad**: ningún screenshot viaja a la nube.
⇒ **Latencia**: respuesta en ~200ms.

### 🎬 Sequence Engine — encadenar acciones

La IA envía **una sola** orden con N pasos y el nodo los ejecuta localmente sin volver a consultar:

```json
{
  "steps": [
    { "action": "open", "url": "https://music.com" },
    { "action": "wait_for", "text": "Search" },
    { "action": "find_click", "text": "Search" },
    { "action": "type", "text": "Bohemian Rhapsody" },
    { "action": "key", "key": "enter" }
  ]
}
```

Acciones soportadas: `open`, `launch`, `focus`, `find_click`, `click`, `smooth`, `key`, `type`, `wait`, `wait_for`, `screenshot`, `notify`, `scroll`, `combo`.

### 📦 Screenshot Cache

Cache local de la última captura para que múltiples lookups OCR/find no re-capturen la pantalla cada vez.

---

## 🌐 Local API Server — `http://localhost:9800`

DonClaw Node expone un servidor HTTP local. Cualquier proceso en tu PC (un script, otro agente, una extensión) puede invocar capabilities sin pasar por el gateway cloud.

### Endpoints

| Endpoint | Descripción |
|---|---|
| `GET /ping` | Health check |
| `GET /caps` | Lista de capabilities disponibles |
| `POST /invoke` | Invocación genérica (`{cmd, args}`) |
| `POST /open` | Atajo a `browser.open` |
| `POST /launch` | Atajo a `app.launch` |
| `POST /focus` | Atajo a `app.focus` |
| `POST /notify` | Atajo a `system.notify` |
| `POST /click` | Click rápido en coordenadas |
| `POST /smooth` | Smooth click en coordenadas |
| `POST /move` | Mueve cursor |
| `POST /key` | Presiona tecla |
| `POST /type` | Escribe texto |
| `POST /cursor` | Posición del cursor |
| `POST /macro` | Secuencia de inputs |
| `POST /find` | OCR + buscar texto en pantalla |
| `POST /act` | Find + click (buscar texto y clickearlo) |
| `POST /ocr` | OCR completo de la pantalla |
| `POST /sequence` | Ejecuta una secuencia (Sequence Engine) |

Ejemplo:
```bash
curl -X POST http://localhost:9800/act \
  -H "Content-Type: application/json" \
  -d '{"text": "Save"}'
```

---

## 🔐 Seguridad

- **Token Auth** — Token del gateway requerido. Se envía como **HTTP Basic Auth** en el upgrade del WebSocket.
- **Ed25519 Device Identity** — Cada nodo tiene un par de claves criptográficas único en `%LOCALAPPDATA%\DonClawNode\device-key-ed25519.json`.
- **Pairing Flow** — El primer connect genera una solicitud de pairing que debe aprobarse en el gateway.
- **Exec Approval Policy** — Glob patterns que limitan qué comandos puede ejecutar `system.run`. Por defecto, solo lectura.
- **File Delete Safety** — `file.delete` requiere `confirm: true` explícito.
- **Local API LAN-accesible** — el servidor en `:9800` hace bind a `IPAddress.Any`, accesible desde 127.0.0.1 y toda la LAN. No tiene auth: quien rutee al host puede invocar capabilities. En redes compartidas, considerar firewall o cambiar el bind a `IPAddress.Loopback`.
- **Todo visible** — el usuario siempre ve lo que sucede en su PC.

---

## ⚙️ Archivos de configuración

| Archivo | Ubicación | Descripción |
|---|---|---|
| Settings | `%APPDATA%\DonClawNode\settings.json` | Host, token, auto-start |
| Device Key | `%LOCALAPPDATA%\DonClawNode\device-key-ed25519.json` | Identidad criptográfica |
| Exec Policy | `%LOCALAPPDATA%\DonClawNode\exec-policy.json` | Reglas de ejecución de comandos |
| Logs | `%LOCALAPPDATA%\DonClawNode\donclaw-node.log` | Logs con rotación (máx 5MB) |

---

## 📁 Estructura del proyecto

```
donclaw-node/
├── DonClaw.Node.slnx                       # Solución
├── README.md
└── src/
    ├── DonClaw.Shared/                      # Librería core (portable)
    │   ├── DonClaw.Shared.csproj
    │   ├── DonClawNodeClient.cs             # Cliente WebSocket + protocolo de nodo
    │   ├── WebSocketClientBase.cs           # Base WebSocket con auto-reconnect
    │   ├── DeviceIdentity.cs                # Identidad Ed25519 para pairing
    │   ├── NodeCapabilities.cs              # Interfaces INodeCapability
    │   ├── Models.cs                        # ConnectionStatus, PairingStatus
    │   ├── GatewayUrlHelper.cs              # Normalización de URLs
    │   ├── ShellQuoting.cs                  # Quoting seguro de argumentos
    │   ├── ICommandRunner.cs / LocalCommandRunner.cs
    │   ├── ExecApprovalPolicy.cs            # Política de aprobación de ejecución
    │   ├── IOpenClawLogger.cs               # Interfaz de logging
    │   └── Capabilities/
    │       ├── BrowserCapability.cs         # 🌐 3 comandos
    │       ├── AppCapability.cs             # 📱 4 comandos
    │       ├── FileCapability.cs            # 📁 12 comandos
    │       ├── PowerCapability.cs           # ⚡ 5 comandos
    │       ├── SystemCapability.cs          # 💻 3 comandos
    │       ├── ScreenCapability.cs          # 📸 2 comandos
    │       ├── CameraCapability.cs          # 📷 3 comandos
    │       ├── CanvasCapability.cs          # 🎨 5 comandos
    │       ├── ClipboardCapability.cs       # 📋 3 comandos
    │       ├── InputCapability.cs           # 🖱️ 13 comandos
    │       └── MicrophoneCapability.cs      # 🎤 3 comandos
    └── DonClaw.Tray.WinUI/                  # App de system tray (Win32 + WinForms)
        ├── DonClaw.Tray.WinUI.csproj
        ├── Program.cs                       # Entry point, tray icon, lifecycle
        ├── SettingsForm.cs                  # UI: Host + Token + Connect/Stop/Test
        └── Services/
            ├── NodeService.cs               # Orquesta cliente + capabilities
            ├── SettingsManager.cs           # Carga/guarda settings.json
            ├── AutoStartManager.cs          # Auto-inicio Windows
            ├── Logger.cs                    # File logger con rotación
            ├── LocalApiServer.cs            # 🌐 HTTP server en :9800
            ├── TextFinder.cs                # 🔍 OCR local (Windows.Media.Ocr)
            ├── ScreenshotCache.cs           # 📦 Cache de capturas
            └── SequenceEngine.cs            # 🎬 Encadenar acciones
```

---

## 🧬 Origen

Basado en [OpenClaw Windows Hub](https://github.com/openclaw/openclaw-windows-node) (Molty), simplificado y extendido:
- ✂️ **Removido:** UI de operador (chat, sessions, activity stream, channels), CLI, CommandPalette, dependencia de WinUI/XAML.
- ➕ **Agregado:** 11 capabilities, Local API HTTP, OCR local, Sequence Engine, Smart Layer.
- 🎯 **Enfoque:** Solo nodo. Solo lo esencial. Host + Token → Conectar.

---

## 📊 Stats

- **~7.500 líneas** de C#
- **32 archivos** en 2 proyectos
- **11 capabilities** con **61 comandos** (App 4 + Browser 3 + Camera 3 + Canvas 5 + Clipboard 3 + File 12 + Input 15 + Microphone 3 + Power 5 + Screen 2 + System 6)
- **0 dependencias** externas más allá de .NET y Windows runtime APIs

---

## 📄 License

MIT

---

*Hecho con 🐾 por [lucast1574](https://github.com/lucast1574)*
