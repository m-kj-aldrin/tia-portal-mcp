# TIA Portal Dashboard

A Windows desktop app and MCP server for inspecting a running TIA Portal V20 project. One user-started process provides the built-in manual dashboard, the existing REST/debugging surface, and a loopback Streamable HTTP MCP endpoint.

> **MCP clients:** Any compatible Streamable HTTP client can use the server, including the ChatGPT app. Client-specific setup notes are optional; the server is not tied to one AI product and does not rely on the client to launch it.

> **New to TIA Portal Openness?** See [What is TIA Portal Openness?](docs/what-is-tia-openness.md) for a plain-English explanation of the API this project is built on.

> **Ready to use?** See the [User Manual](docs/user-manual.md) for a walkthrough of both dashboard tabs.

> **Version-one product target:** See the [Version One Read Specification](docs/version-one-read-specification.md). It defines the intended MCP-first, read-focused behavior when the current implementation or older guides differ.

---

## Two dashboard tabs

| Tab | How to use it |
|---|---|
| **Operations** | View status, connect safely, and run the supported read tools manually |
| **MCP diagnostics & logs** | Copy the HTTP endpoint, inspect live tool definitions, and monitor client calls |

Both tabs use the same server process and TIA Portal connection. The Operations runner intentionally remains read-only; project-changing MCP and REST operations require the explicit full-access profile and separate user authorization.

---

## What it can do

| Capability | State | Access |
|---|---|---|
| Discover devices and canonical PLC identities with `list_devices` | Implemented | Default read-only V1 MCP |
| Inventory PLC blocks, UDTs, tag tables, and group hierarchy with `list_plc_objects` | Implemented | Default read-only V1 MCP |
| Search live PLC-object metadata with `find_plc_objects` | Implemented | Default read-only V1 MCP |
| Read one native PLC representation with strict or `best` negotiation through `read_plc_object` | Implemented | Default read-only V1 MCP |
| Read tags, user constants, and system constants with `get_tag_table_entries` | Implemented | Default read-only V1 MCP |
| Query native `uses` and `usedBy` evidence with `get_cross_references` | Implemented | Default read-only V1 MCP |
| Write/import SCL or XML, create blocks, compile, rename/import tags, and save | Experimental; outside V1 | `TIA_MCP_ACCESS=full` |
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

See the [User Manual](docs/user-manual.md) for a walkthrough of the Operations and MCP diagnostics & logs tabs, with a reference for every tool.

### Quick start — Operations (manual)

Use **Status** to connect or inspect provenance, **Manual read tools** to invoke the eight canonical V1 tools, and **Setup & safety** for the endpoint and evidence boundaries. Responses appear as JSON in the tool runner.

### Quick start — MCP client (read-only default)

1. Open the intended project in TIA Portal V20.
2. Start `TiaPortalDashboard.exe` and open **MCP diagnostics & logs** to see the MCP endpoint and call log.
3. Point a compatible client at the displayed loopback `/mcp` URL, normally `http://127.0.0.1:5000/mcp`.
4. From the client, call `connect_to_tia_portal` and approve Siemens external access in the visible TIA UI if prompted.
5. Call `get_status`, then `list_devices` to obtain the canonical PLC identities.
6. Use `list_plc_objects` and `find_plc_objects` for discovery, `read_plc_object` for native content, `get_tag_table_entries` for tags/constants, and `get_cross_references` for native relationship evidence while the dashboard displays call activity.

The running process owns the TIA attachment. Multiple MCP clients may share it and its zero-or-one active project; connecting or disconnecting one client does not create or dispose a separate TIA attachment.

## Access profiles

With no access setting, the V1 MCP server advertises and accepts exactly eight tools: `connect_to_tia_portal`, `get_status`, `list_devices`, `list_plc_objects`, `find_plc_objects`, `read_plc_object`, `get_tag_table_entries`, and `get_cross_references`. This is the recommended profile for the ChatGPT app and other agents.

To make implemented write, import, create, compile, rename, and save operations available, start the server with:

```powershell
$env:TIA_MCP_ACCESS = "full"
./src/TiaOpennessMcpServer/bin/Release/net48/TiaPortalDashboard.exe
```

Restart the user-started server after changing the profile. Full-access operations are experimental and outside the V1 specification and acceptance. Full access only makes tools available; it does not authorize an agent to change a project without the user's explicit instruction.

MCP `get_status` reports `accessProfile` and `writeToolsAvailable`, so clients can verify which profile is active. The dashboard REST status keeps `writeEnabled` as a compatibility alias.

`clone_project` is quarantined: it is not advertised in either profile and direct calls are rejected. Its legacy implementation is also unsupported for projects attached from a running TIA Portal instance.

> The profile also blocks project-changing REST requests and hides or disables the corresponding dashboard controls.

## Connecting an MCP client

### Streamable HTTP only

The user starts the executable before connecting a client. Configure the client with:

```text
http://127.0.0.1:5000/mcp
```

The ChatGPT app supports this Streamable HTTP endpoint. Other compatible clients may call it a custom connector, local server, or HTTP MCP server; exact menu names vary by client. The listener remains loopback-only and is not a remote deployment endpoint.

Port `5000` is the default. Set `TIA_MCP_PORT` to an integer from `1` through `65535` before launch when another local server already uses that port, then use the matching URL in the client.

Automatic client launch, process supervision, Windows-service operation, and alternative MCP transports are outside version one.

### Server protocol

- **URL**: `http://127.0.0.1:5000/mcp`
- **Protocol version**: MCP `2025-03-26` (with automatic fallback to `2024-11-05` for older clients)
- **Transport**: loopback Streamable HTTP (POST to `/mcp`)

### V1 project and TIA versions

Whenever a V1 response contains a non-null project identity, its JSON contains `project.version`. The value is the nonblank native TIA `Project.Version` string or `null`; it is never inferred from the `.ap20` extension, installed TIA version, assemblies, registry, paths, or filesystem metadata.

`tia.portalVersion` is a separate nullable fact from native installed-software diagnostics. The native Siemens installed-product name `Totally Integrated Automation Portal` is recognized as the portal product and its native `version` supplies this value. Installed-product entries retain native product names, native versions, genuinely populated native product codes, and nested options. They do not expose an `update` field or infer update, patch, service-pack, build, or project-version values.

---

## Troubleshooting

### App / TIA Portal

**"Security error — not a member of Siemens TIA Openness group"**
Complete setup step 3. Make sure you signed out and back in after being added to the group.

**"No running TIA Portal process found"**
TIA Portal must be open with a project loaded before you click Connect.

**TIA Portal approval dialog keeps appearing on every restart**
This is expected behaviour — TIA Portal Openness can show the "Allow external access" dialog on every new process connection, not just the first time. Restarting the dashboard creates a new server process and may require a new approval. Connecting another MCP client to the already-running server does not create another TIA attachment. Keep an eye on the TIA Portal taskbar button; the dialog sometimes appears behind other windows.

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
- Confirm the client supports Streamable HTTP and is using the loopback `/mcp` URL displayed by this server.
- Restart or reconnect the client after changing server settings; many clients cache the tool list.
- Open a browser and navigate to `http://127.0.0.1:5000/mcp`. You should get a `405 Method Not Allowed` JSON response. If you get a connection error, the app is not running.

**The connector shows "connected" but no tools appear**
Check **Live tool definitions** in **MCP diagnostics & logs**; the default profile must show the eight canonical V1 tools. The call log begins with actual tool calls, so `initialize` and `tools/list` discovery do not need to create entries. The server implements MCP `2025-03-26` and negotiates `2024-11-05` for older clients.

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
6. Serves one set of MCP definitions and dispatch over the loopback Streamable HTTP endpoint
7. Exposes exactly the eight canonical V1 tools in the default read-only MCP profile; experimental full-access operations remain outside the V1 specification and acceptance

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
│   ├── SoftwareService.cs      # Blocks — list, read/export source, write SCL/XML, compile, patch texts
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
curl http://127.0.0.1:5000/api/devices/HMI/hmi/tags

# Export every tag with its PLC connection
curl http://127.0.0.1:5000/api/devices/HMI/hmi/tags/all

# Tags in a specific table
curl http://127.0.0.1:5000/api/devices/HMI/hmi/tags/Default%20tag%20table

# Create tags (body is a JSON array)
curl -X POST http://127.0.0.1:5000/api/devices/HMI/hmi/tags/Default%20tag%20table/create \
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
