# TIA Portal Dashboard

A Windows desktop app and MCP server for inspecting a running TIA Portal V20 project. It provides a built-in manual dashboard plus Streamable HTTP and stdio MCP transports.

> **MCP clients:** Any compatible client can use the server, including the ChatGPT app over Streamable HTTP and clients that launch local stdio MCP processes. Client-specific setup notes are optional; the server is not tied to one AI product.

> **New to TIA Portal Openness?** See [What is TIA Portal Openness?](docs/what-is-tia-openness.md) for a plain-English explanation of the API this project is built on.

> **Ready to use?** See the [User Manual](docs/user-manual.md) for a full walkthrough of both operating modes.

---

## Two modes

| Mode | Tab | How to use it |
|------|-----|---------------|
| **User Control** | Left tab | Fill in fields and click Run — fully manual, no AI |
| **Agent Control** | Right tab | Connect an MCP-compatible client and monitor its calls |

Both modes use the same TIA Portal connection and are read-only by default. Project-changing MCP tools, dashboard controls, and REST routes require the explicit full-access profile.

---

## What it can do

| Capability | State | Access |
|---|---|---|
| Browse devices, blocks, tags, option packages, and project signature | Implemented | Default read-only MCP |
| Read SCL and raw SimaticML with `read_block` | Implemented | Default read-only MCP |
| Read pure LAD as authoritative SIMATIC SD with `read_lad_source` | Implemented | Default read-only MCP |
| Analyse SCL without compiling | Implemented | Default read-only MCP |
| Write/import SCL or XML, create blocks, compile, rename/import tags, and save | Implemented | `TIA_MCP_ACCESS=full` |
| Inspect block attributes | Implemented REST/dashboard feature | Default read-only profile |
| Patch block and network text | Implemented REST/dashboard feature | `TIA_MCP_ACCESS=full` |
| Clone an attached project | Quarantined | Unsupported; not an approved workflow |
| Parse LAD into networks and rungs | Planned; no LAD-to-SCL converter exists | See the LAD architecture roadmap |

---

## Requirements

Before you start, make sure you have:

- **Windows 10 or 11** (TIA Portal only runs on Windows)
- **Siemens TIA Portal V20** installed and licensed
- **Your account added to the `Siemens TIA Openness` Windows group** (see setup step 3)
- **Microsoft Edge** installed (comes with Windows 10/11 — needed for the built-in browser window)
- **.NET Framework 4.8** — already included in Windows 10/11, nothing to install
- **.NET SDK 8 or later** — needed to build the project ([download here](https://dotnet.microsoft.com/download))

---

## Setup

### 1. Clone the repository

Open PowerShell and run:

```bash
git clone https://github.com/hadefuwa/tia-portal-mcp.git
cd tia-portal-mcp
```

### 2. Build the project

```bash
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
```

The output goes to:
```
src/TiaOpennessMcpServer/bin/Release/net48/TiaPortalDashboard.exe
```

### 3. Add yourself to the TIA Openness security group

TIA Portal requires your Windows account to be in a specific security group before any external tool can connect to it. You only need to do this once.

1. Open **Computer Management** (right-click Start → Computer Management)
2. Go to **Local Users and Groups → Groups**
3. Double-click **Siemens TIA Openness**
4. Click **Add** and enter your Windows username
5. Click OK, then **sign out and back in** for it to take effect

> Without this step the dashboard will show a security error when you try to connect.

### 4. Run the dashboard

1. Open your project in **TIA Portal V20**
2. Double-click `TiaPortalDashboard.exe`
3. A native desktop window opens with the dashboard inside it
4. Click **Connect to TIA Portal** — the dashboard reads your open project

TIA Portal will show an access approval dialog — click **Yes to all** to approve. This dialog appears **every time a new instance of the exe connects**, not just the first time. Each app restart will require one approval click.

> The window minimises to the **system tray** rather than closing. Right-click the tray icon to exit completely.

---

## How to use it

See the [User Manual](docs/user-manual.md) for a full walkthrough of both the User Control and Agent Control tabs, with a reference for every tool.

### Quick start — User Control (manual)

Once connected, your PLC devices appear in the left sidebar. Click a device to expand it. Click **Blocks** to list all blocks; click any block name to read it. Use **Run Tool** in the sidebar for the available manual tools.

### Quick start — MCP client (read-only default)

1. Start the dashboard and open the **Agent Control** tab to see the MCP endpoint and call log.
2. Connect a compatible client to `http://localhost:5000/mcp`, or configure it to launch the executable with `--mcp-stdio`.
3. Call `connect_to_tia_portal`, approve Siemens external access if prompted, then use list/read tools such as `list_devices`, `list_blocks`, `read_block`, and `read_lad_source`.

## Access profiles

With no access setting, the server exposes only non-mutating inspection and analysis operations. This is the recommended profile for the ChatGPT app and other agents.

To make implemented write, import, create, compile, rename, and save operations available, start the server with:

```powershell
$env:TIA_MCP_ACCESS = "full"
./src/TiaOpennessMcpServer/bin/Release/net48/TiaPortalDashboard.exe
```

For stdio, set `TIA_MCP_ACCESS=full` in the environment passed to the child process. Restart the server after changing the profile. Full access only makes tools available; it does not authorize an agent to change a project without the user's explicit instruction.

`get_status` reports `accessProfile` and `writeEnabled`, so clients can verify which profile is active.

`clone_project` is quarantined: it is not advertised in either profile and direct calls are rejected. Its legacy implementation is also unsupported for projects attached from a running TIA Portal instance.

> The profile also blocks project-changing REST requests and hides or disables the corresponding dashboard controls.

## Connecting an MCP client

### Streamable HTTP

Start the executable without arguments and configure the client with:

```text
http://localhost:5000/mcp
```

The ChatGPT app supports this Streamable HTTP endpoint. Other MCP clients may call it a custom connector, remote server, or HTTP MCP server; exact menu names vary by client.

Port `5000` is the default. Set `TIA_MCP_PORT` to an integer from `1` through `65535` before launch when another local server already uses that port, then use the matching URL in the client.

### stdio

Clients that can launch a local MCP process can use this generic configuration shape:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\path\\to\\TiaPortalDashboard.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

Add `"env": {"TIA_MCP_ACCESS": "full"}` only for an explicitly approved full-access session. Client-specific configuration file locations are outside this repository.

### Server protocol

- **URL**: `http://localhost:5000/mcp`
- **Protocol version**: MCP `2025-03-26` (with automatic fallback to `2024-11-05` for older clients)
- **Transports**: Streamable HTTP (POST to `/mcp`) and newline-delimited stdio (`--mcp-stdio`)

---

## Troubleshooting

### App / TIA Portal

**"Security error — not a member of Siemens TIA Openness group"**
Complete setup step 3. Make sure you signed out and back in after being added to the group.

**"No running TIA Portal process found"**
TIA Portal must be open with a project loaded before you click Connect.

**TIA Portal approval dialog keeps appearing on every restart**
This is expected behaviour — TIA Portal Openness shows the "Allow external access" dialog on every new process connection, not just the first time. Each time you restart the dashboard or the stdio exe a new connection is made and a new approval is required. Keep an eye on the TIA Portal taskbar button; the dialog sometimes appears behind other windows.

**"Inconsistent blocks and PLC data types (UDT) cannot be exported"**
Your project has UDT changes that haven't been compiled. In TIA Portal, press **Ctrl+B** to compile everything, then retry.

**`clone_project` is unavailable for the attached project**
This is intentional. Clone is quarantined at the server boundary because its legacy implementation saves and closes projects internally; it is unsupported for a project attached from the user's running TIA Portal instance.

**Build fails with "Siemens.Engineering.dll not found"**
The project expects TIA Portal V20 at the default path (`C:\Program Files\Siemens\Automation\Portal V20`). If yours is installed elsewhere, update the `HintPath` entries in the `.csproj` file.

**The window doesn't open / WebView2 error**
Make sure Microsoft Edge is installed and up to date. The built-in browser window uses the Edge WebView2 runtime, which ships with Edge on Windows 10/11.

### MCP clients

**Tools do not appear after adding the server**
- Confirm the dashboard app is running (the MCP server only runs while the app is open).
- Confirm the client supports Streamable HTTP or stdio MCP and that it is using the matching configuration.
- Restart or reconnect the client after changing server settings; many clients cache the tool list.
- Open a browser and navigate to `http://localhost:5000/mcp`. You should get a `405 Method Not Allowed` JSON response. If you get a connection error, the app is not running.

**The connector shows "connected" but no tools appear**
Check the Live call log in the Agent Control tab for `initialize` and `tools/list`. The server implements MCP `2025-03-26` and negotiates `2024-11-05` for older clients.

**Write or compile tools are missing**
That is the safe default. Set `TIA_MCP_ACCESS=full` in the server process environment and restart it only when a full-access session is intended.

**"Missing 'Namespace' identifier attribute" when creating a GlobalDB**
This error appears when the SimaticML XML has `Namespace` as an XML attribute on the element (`<SW.Blocks.GlobalDB Namespace="">`) instead of as a child element inside `<AttributeList>`. The correct form is `<Namespace />` inside `<AttributeList>`. This is handled correctly by the built-in `create_block` tool; you would only see this if crafting XML manually.

**"Cannot import multilingual text with culture 'en-US'" when creating a block**
The `<MultilingualText>` comment blocks in SimaticML XML include a `<Culture>` tag that must match the project's language. A project created in British English (`en-GB`) will reject `en-US`. The built-in block templates omit the comment section entirely to avoid this — if you are writing custom XML, use `<ObjectList />` for the ObjectList instead of including a MultilingualText entry.

**My code changes aren't reflected after restarting the app**
Rebuild the Release executable and confirm that you launch the output from this checkout:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
```

---

## How it works (technical summary)

The dashboard is a **.NET Framework 4.8** WinForms application that:

1. Resolves `Siemens.Engineering.dll` from your TIA Portal installation at startup
2. Attaches to the running TIA Portal process using the **TIA Portal Openness API**
3. Runs all API calls through a dedicated **STA (Single-Threaded Apartment) thread** — required because TIA Openness is a COM-based library
4. Serves the dashboard UI over `System.Net.HttpListener` on port 5000 by default, or `TIA_MCP_PORT` when configured
5. Displays the UI in a native **WinForms window** with an embedded **WebView2** (Edge-based) browser control
6. Serves the same MCP definitions and dispatch over HTTP and stdio
7. Filters the MCP surface to read-only by default unless `TIA_MCP_ACCESS=full` is set

> **.NET Framework 4.8 is required** — not .NET 5/6/7/8. The TIA Openness library depends on `System.Runtime.Remoting`, which was removed from modern .NET.

---

## Project structure

```
src/TiaOpennessMcpServer/
├── Program.cs                  # HTTP server, routing, app entry point
├── MainForm.cs                 # WinForms window + WebView2 + system tray
├── dashboard.html              # Single-page frontend
├── Services/
│   ├── TiaPortalService.cs     # Connection lifecycle, project metadata, used products
│   ├── SoftwareService.cs      # Blocks — list, read, write SCL, write XML, compile, patch texts
│   ├── HardwareService.cs      # Device enumeration
│   ├── TagService.cs           # PLC tag tables
│   ├── HmiTagService.cs        # WinCC Unified HMI tag tables and tags
│   └── SclAnalyzerService.cs   # SCL static analysis
├── Models/                     # Data transfer objects
└── Utilities/
    ├── StaTaskScheduler.cs     # STA thread wrapper for COM calls
    ├── XmlHelper.cs            # Block XML parse/patch helpers
    └── NetFxPolyfills.cs       # C# 9/11 types missing from net48
```

### REST API — HMI tag endpoints

> Project-changing REST and dashboard operations are rejected by the default read-only profile. Start the server with `TIA_MCP_ACCESS=full` only for an explicitly intended manual/full-access session.

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/devices/{device}/hmi/tags` | List all WinCC Unified tag tables with tag counts |
| `GET` | `/api/devices/{device}/hmi/tags/all` | Export all HMI tags flat (name, table, dataType, plcTag) |
| `GET` | `/api/devices/{device}/hmi/tags/{table}` | Get tags from a specific table |
| `POST` | `/api/devices/{device}/hmi/tags/{table}/create` | Create Bool/Int/etc tags in a table (see note) |

`{device}` is the HMI device name as it appears in TIA Portal (typically `HMI`).

```bash
# List tag tables
curl http://localhost:5000/api/devices/HMI/hmi/tags

# Export every tag with its PLC connection
curl http://localhost:5000/api/devices/HMI/hmi/tags/all

# Tags in a specific table
curl http://localhost:5000/api/devices/HMI/hmi/tags/Default%20tag%20table

# Create tags (body is a JSON array)
curl -X POST http://localhost:5000/api/devices/HMI/hmi/tags/Default%20tag%20table/create \
  -H "Content-Type: application/json" \
  -d '[{"name":"DI_A_0","dataType":"Bool"},{"name":"DQ_A_0","dataType":"Bool"}]'
```

> **WinCC Unified limitation:** The create endpoint creates tags as *Internal tags* (no PLC connection). The `SetAttribute("PlcTag", ...)` call in the Openness API throws for newly created tags regardless of format. After creating tags via the API, open TIA Portal → HMI tags and manually set the Connection and PLC tag for each one.

### REST API — Block text patching

| Method | Path | Description |
|--------|------|-------------|
| `PATCH` | `/api/devices/{device}/blocks/{block}/texts` | Update block/network titles and comments |
| `GET` | `/api/devices/{device}/blocks/{block}/attributes` | List all attributes and compositions on a block |

```json
// PATCH body — all fields optional
{
  "blockTitle": "My FB",
  "blockComment": "Main conveyor control",
  "networks": [
    { "title": "Enable check", "comment": "Gate on safety OK" },
    { "title": null, "comment": "Speed ramp" }
  ]
}
```

---

## Further reading

- [User Manual](docs/user-manual.md) — both tabs, access profiles, tool reference, setup, and troubleshooting
- [What is TIA Portal Openness?](docs/what-is-tia-openness.md) — plain-English guide to the API
- [LAD agent architecture](docs/lad-agent-architecture.md) — SIMATIC SD reading and the lossless, progressive LAD roadmap
- [Siemens TIA Portal Openness documentation](https://support.industry.siemens.com/cs/document/109792902) — official Siemens overview and links
- [TIA Portal Openness system manual (PDF)](https://support.industry.siemens.com/cs/ww/en/view/109748523) — full API reference

---

## Licence

MIT
