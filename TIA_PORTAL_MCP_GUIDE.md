# TIA Portal MCP Server — Developer Guide

Repository-specific architecture, behavior, and safety notes for TIA Portal V20.

Updated for the version-one HTTP runtime and provenance contract on 2026-08-16. The normative target is
[`docs/version-one-read-specification.md`](docs/version-one-read-specification.md); see **Known
contradictions** at the end for older implementation details that still require live confirmation.

---

## Architecture

```
Compatible Streamable HTTP MCP clients, including ChatGPT
                         │
                         │  HTTP POST /mcp (loopback only)
                         ▼
              TiaPortalDashboard.exe
       dashboard + MCP + REST/debugging surface
                         │
                         │  shared HandleMcpRequest()
                         │  TIA Openness API (COM · STA required)
                         ▼
              Siemens.Engineering.dll
              TIA Portal V20 PublicAPI
                         │
                         ▼
 TIA Portal V20 (visible UI; zero or one active project per server process)
```

The user starts this long-running local process before an MCP client connects. Multiple compatible
clients can share it, but they share the same active-project state and TIA attachment. Version one has
no per-client TIA project, client-launched server, background service, or process supervisor.

**Source:**
```
src/TiaOpennessMcpServer/
├── Program.cs                     # HTTP listener, routing, MCP tool definitions + dispatch
├── MainForm.cs                    # WinForms tray window for the shared local server
├── Services/TiaPortalService.cs   # Connection lifecycle, project info/save, signature, option packages
├── Services/HardwareService.cs    # Device and module enumeration
├── Services/SoftwareService.cs    # Blocks — list, read, create, write, compile, instance DB
├── Services/SclAnalyzerService.cs # Static SCL analysis (no compile required)
├── Services/TagService.cs         # Tag tables — list, read, import, batch rename
├── Utilities/XmlHelper.cs         # SimaticML XML builders and SCL extract/inject
├── Utilities/StaTaskScheduler.cs  # The single STA thread every Openness call runs on
└── Models/                        # DTOs — BlockInfo, DeviceInfo, TagInfo, ProjectSignature, …
```

**Export/temp files:** `C:\Temp\TiaExports\` (all XML written before import is left on disk — useful for debugging).

> `appsettings.json` is **not loaded**. `Program.cs` calls `services.Configure<TiaOpennessOptions>(_ => { })`
> with no configuration binding, so every option comes from the C# defaults in `TiaOpennessOptions.cs`.
> The file's values happen to match those defaults — editing it changes nothing. To change the export
> directory you must edit `TiaOpennessOptions.cs` and rebuild, or wire up configuration binding.

---

## Runtime and transport

### Streamable HTTP only

```
http://127.0.0.1:5000
```

The user starts the exe with no arguments. `GET /` serves the dashboard and compatible MCP clients
connect to `http://127.0.0.1:5000/mcp`. The ChatGPT app is one example; exact connector UI labels are
client-specific. The endpoint is loopback-only and is not a remote deployment surface.

Port `5000` is the default. Set `TIA_MCP_PORT` to an integer from `1` through `65535` before launch when a different local port is required, and update the dashboard/MCP URL accordingly.

`GET /mcp` deliberately returns 405 with a JSON-RPC error body so clients detect the modern
Streamable HTTP transport instead of falling back to HTTP+SSE discovery. Protocol version is
`2025-03-26`, or `2024-11-05` echoed back if the client asks for it.

Recommended startup sequence:

1. The user opens the intended TIA Portal project.
2. The user starts `TiaPortalDashboard.exe`.
3. The user points a compatible client at the displayed loopback `/mcp` URL.
4. The client calls `connect_to_tia_portal`; the user approves Siemens external access in the visible
   TIA UI if prompted.
5. The client uses the read tools while the dashboard displays connection status and call activity.

A client connection does not start another server or TIA attachment. Client disconnects do not detach
the shared process from TIA. Automatic startup, alternate MCP transports, service installation, and
process supervision are outside version one.

---

## TIA Portal approval dialog

`connect_to_tia_portal` / `POST /api/connect` calls `AttachToRunningAsync()`, which blocks the STA
thread while TIA Portal shows its access approval dialog. Consequences:

1. **The call does not return until the user clicks "Yes to all".** Set client timeouts to ≥90 seconds.
2. The dialog is shown per connecting server process, so each app restart can require fresh approval.
   Connecting another client to the already-running server does not create another TIA attachment. The
   dialog sometimes appears behind other windows — check the TIA Portal taskbar button.
3. While the STA thread is blocked, every other STA-bound operation queues behind it. `get_status`
   only reads a null check and still returns immediately.

---

## Access profiles

- **Default read-only V1:** when `TIA_MCP_ACCESS` is unset, exactly the eight canonical V1 tools in the catalog below are advertised and callable.
- **Full:** set `TIA_MCP_ACCESS=full` in the server process environment and restart to expose experimental write, import, create, compile, rename, and save operations outside the V1 specification and acceptance.
- **Quarantined:** `clone_project` is never advertised and direct MCP/REST calls are rejected. Its legacy implementation is also unsupported for an externally attached project.

Full access changes availability only. An agent still needs explicit user authorization for project-changing work. The profile gates MCP discovery and dispatch, rejects mutating REST requests, and hides or disables corresponding dashboard controls.

## MCP tool catalog

Definitions, validation, and dispatch use one Streamable HTTP path in `Program.cs`. The default read-only V1 catalog is exactly the eight tools below. Each input schema sets `additionalProperties: false`, and runtime dispatch rejects undeclared arguments and every other tool name under the default profile.

| Tool | Required args | Optional | Availability | Returns |
|---|---|---|---|---|
| `connect_to_tia_portal` | — | `projectPath` | Read-only default | V1 response with `provenance`, `connected`, and connection `action` |
| `get_status` | — | — | Read-only default | V1 provenance plus `connected`, `accessProfile`, and `writeToolsAvailable` |
| `list_devices` | — | — | Read-only default | V1 provenance plus native-metadata `devices` and PLC identities |
| `list_plc_objects` | `plc` | — | Read-only default | V1 hierarchical PLC-software inventory with identity and availability metadata |
| `find_plc_objects` | `plc` | `query`, `type`, `language`, `group` | Read-only default | V1 live metadata `matches`; no source index or full-text search |
| `read_plc_object` | `plc` and at least one object selector | `objectId`, `path`, `name`, `type`, `format` | Read-only default | V1 provenance, protection, complete native representation, and ordered attempts |
| `get_tag_table_entries` | `plc` and at least one table selector | `objectId`, `path`, `table` | Read-only default | Direct Openness view grouped into tags, user constants, and system constants |
| `get_cross_references` | `plc` and at least one object selector | `objectId`, `path`, `name`, `type` | Read-only default | Native protected-aware `uses` and `usedBy` references |

There is no LAD-to-SCL converter. `read_plc_object` returns one complete native representation selected by a strict format request or by the documented `best` fallback order; interpretation remains outside the MCP.

Reusable discovery/export services and the existing REST/debugging surface may remain in the process where canonical tools depend on them. They do not add V1 MCP calls. Experimental full-access MCP operations are intentionally outside this catalog and outside V1 acceptance.

### V1 provenance identity

Whenever status, success, or error provenance contains a non-null V1 project identity, the serialized
`project` object contains a `version` key. Its value is the nonblank native TIA `Project.Version` string
or JSON `null`; blank values normalize to `null`, and the server does not infer a value from `.ap20`,
installed TIA, assemblies, registry, paths, or filesystem metadata.

`tia.portalVersion` is a separate nullable fact derived from native installed-software diagnostics. The
mapping recognizes Siemens' native product name `Totally Integrated Automation Portal` and preserves
that installed-product record's native version. The installed-product tree retains native product names,
native versions, genuinely populated native product codes, and nested options. It contains no `update`
field and does not infer update, patch, service-pack, build, or project-version values.

**Tool quirks worth knowing:**

- **`projectPath` on `connect_to_tia_portal` is authoritative when no project is active.** The server
  attaches to one exact open-project match or visibly opens that compatible path when none is open.
  Without a path it attaches only when exactly one suitable open project exists; multiple candidates
  return an ambiguity error. A different already-active project returns `project-conflict` and is never
  switched implicitly.
- **Canonical selectors have explicit precedence.** Object reads and cross-references require `plc`
  plus at least one of `objectId`, `path`, or `name`; tag-table reads require `plc` plus at least one of
  `objectId`, `path`, or `table`. `objectId` takes precedence when present. Otherwise convenience
  selectors must resolve uniquely, and `type` only qualifies path/name selection.
- **Strict input validation applies before TIA dispatch.** Unknown fields, including password,
  credential, username, or unlock fields, return a structured invalid-request result.
- Canonical V1 tool failures come back as `isError: true` with the text content containing a serialized
  structured V1 error envelope, including available provenance, native messages, protection, and fallback
  attempts. Tool failures are content results rather than JSON-RPC protocol errors. The last 200 calls
  are visible at `GET /api/mcp/log` and on the dashboard.

---

## HTTP API endpoints

All at `http://127.0.0.1:5000`. All JSON. **Keys are camelCase** (`content`, not `Content`).
Enums serialise as strings (`"GlobalDB"`, `"SCL"`). Access-profile rejections use HTTP 403 and the
quarantined clone route uses HTTP 410; older route errors may still return `{error:"…"}` with HTTP 200.

These legacy REST/debugging routes are operational and maintainer interfaces, not additional V1 MCP
tools. Only the eight names in the MCP catalog above belong to the default V1 MCP surface.

Mutating REST routes return HTTP 403 unless the server was started with `TIA_MCP_ACCESS=full`. `/api/connect`, the standalone `/api/analyze` POST, and the read-only per-block SCL analysis route remain available in the read-only profile.

### Project / connection

| Method | Path | Body | Notes |
|--------|------|------|-------|
| GET | `/` | — | Serves `dashboard.html` from next to the exe |
| GET | `/api/status` | — | `{connected, project?}` — never blocks |
| POST | `/api/connect` | — | Attaches to running TIA Portal; blocks until user approves |
| GET | `/api/project/signature` | — | Every block and tag table on every device, with consistency state |
| GET | `/api/project/options` | — | Option packages and used products |
| POST | `/api/project/clone` | `name, path` | Always quarantined; returns HTTP 410 |
| POST | `/api/project/save` | — | Saves the open project |

### Devices

| Method | Path | Notes |
|--------|------|-------|
| GET | `/api/devices` | Lists all devices with modules |

Use the **exact** name from this call in path segments — in the SF2 project the PLC is `S7-1200`,
not `S7-1200 station_1`. Names containing spaces must be URL-encoded (`%20`); the router
`Uri.UnescapeDataString`s each segment.

### Blocks

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| GET | `/api/devices/{device}/blocks` | — | List all blocks, recursing into folders |
| GET | `/api/devices/{device}/blocks/{block}` | — | Read block (XML + SCL source) |
| POST | `/api/devices/{device}/blocks` | `name, type, number?, language, sourceCode` | Create block. `language` is accepted but **ignored** — always SCL |
| POST | `/api/devices/{device}/blocks/{block}/xml` | `content` | Import raw SimaticML XML (creates or overwrites) |
| PUT | `/api/devices/{device}/blocks/{block}/scl` | `source` | Patch SCL in an existing block |
| POST | `/api/devices/{device}/blocks/{block}/compile` | — | Compile; returns `{result}` |
| POST | `/api/devices/{device}/blocks/{block}/analyze` | — | Static SCL analysis, no compile |
| POST | `/api/devices/{device}/blocks/instance-db` | `name, instanceOfName, number?` | Create instance DB |

### Tags

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| GET | `/api/devices/{device}/tags` | — | List tag tables |
| GET | `/api/devices/{device}/tags/{table}` | — | Get all tags in a table |
| POST | `/api/devices/{device}/tags/import` | `content` | Import tag table from SimaticML XML |
| POST | `/api/devices/{device}/tags/{table}/rename` | `renames: [{from, to}]` | Batch rename |

### Analysis and diagnostics

| Method | Path | Body fields | Notes |
|--------|------|-------------|-------|
| POST | `/api/analyze` | `source, blockName?, blockType?` | Analyse SCL without an open block |
| GET | `/api/mcp/log` | — | Last 50 MCP tool calls (tool, timestamp, success, error) |
| POST | `/mcp` | JSON-RPC 2.0 | MCP Streamable HTTP endpoint |

---

## Creating blocks (full/manual access)

### SCL blocks (FB, FC, OB)

Pass SCL source as `sourceCode`. It is base64-encoded into `<Source Name="BlockSource">` and imported
with `ImportOptions.Override`.

```powershell
$body = @{
    name       = "FB_SpeedControl"
    type       = "FB"
    number     = $null        # auto-number
    language   = "SCL"
    sourceCode = @'
FUNCTION_BLOCK "FB_SpeedControl"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1

VAR_INPUT
    Enable    : Bool;
    Setpoint  : Real;
END_VAR
VAR_OUTPUT
    Running   : Bool;
END_VAR
VAR
    _rampVal  : Real;
END_VAR

BEGIN
    IF Enable THEN
        _rampVal := Setpoint;
        Running  := TRUE;
    ELSE
        Running  := FALSE;
    END_IF;
END_FUNCTION_BLOCK
'@
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/devices/S7-1200/blocks" `
    -Method POST -Body $body -ContentType "application/json" -TimeoutSec 30
```

**SCL rules for TIA Portal V20:**
- Block name in the declaration must match the `name` field.
- `{ S7_Optimized_Access := 'TRUE' }` is optional but standard.
- FB sections: `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR` (static), `VAR_TEMP`.
- `R_TRIG`, `F_TRIG`, `TON`, `TOF` are valid IEC types — declare them in `VAR`.

### GlobalDB

Pass SCL `DATA_BLOCK…END_VAR` as `sourceCode`. The server regex-scans the source for member
declarations and generates SimaticML from them.

```powershell
$body = @{
    name       = "ProductionData_DB"
    type       = "GlobalDB"
    number     = $null
    language   = "SCL"
    sourceCode = @'
DATA_BLOCK "ProductionData_DB"
   VAR
      LineSpeed    : Real;
      Temperature  : Real;
      PartCount    : DInt;
      ShiftRunning : Bool;
      BatchID      : String[20];
      FaultCode    : Int;
   END_VAR
'@
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/devices/S7-1200/blocks" `
    -Method POST -Body $body -ContentType "application/json" -TimeoutSec 30
```

**⚠ Parser limits — `XmlHelper.ParseSclVarSection` is a single regex, `^\s+(\w+)\s*:\s*([\w\[\].\"]+)\s*;`:**

| Declaration | Result |
|---|---|
| `LineSpeed : Real;` | ✅ parsed |
| `BatchID : String[20];` | ✅ parsed |
| `"MyUdt_Type"` members | ✅ parsed (quotes allowed) |
| `Setpoint : Real := 25.0;` | ❌ **silently dropped** — initial values not supported |
| `Buffer : Array[0..9] of Int;` | ❌ **silently dropped** — spaces in the type |
| nested `STRUCT … END_STRUCT` | ❌ **silently dropped** |

Dropped members produce no error — you get a DB that imported cleanly with fields missing. The regex
also scans the whole source, not just between `VAR`/`END_VAR`, so anything shaped like a declaration
anywhere in the text becomes a member. In an explicitly authorized full-access workflow, perform a fresh
canonical `read_plc_object` read-back afterwards, or hand-write the XML and use `import_block_xml`.

### Instance DB

```powershell
$body = @{
    name           = "FB_SpeedControl_DB"
    instanceOfName = "FB_SpeedControl"
    number         = $null   # or a specific int
} | ConvertTo-Json

Invoke-RestMethod -Uri "http://127.0.0.1:5000/api/devices/S7-1200/blocks/instance-db" `
    -Method POST -Body $body -ContentType "application/json"
```

---

## SimaticML XML rules

These rules were discovered through live import attempts against TIA Portal V20.

### GlobalDB — minimal valid template

This is exactly what `XmlHelper.CreateGlobalDbXml` emits.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <SW.Blocks.GlobalDB ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Static">
            <Member Name="MyVar" Datatype="Real" />
          </Section>
        </Sections>
      </Interface>
      <Name>MyDB</Name>
      <Namespace />
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
    <ObjectList />
  </SW.Blocks.GlobalDB>
</Document>
```

`AutoNumber` is `true` when no number is given, `false` when one is — and the number goes on the
element as `Number="…"`, not as a child element.

### FB/FC/OB skeleton

This is exactly what `XmlHelper.CreateSclBlockXml` emits — all seven sections, for every block type.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <SW.Blocks.FB ID="0" Number="1">
    <AttributeList>
      <AutoNumber>false</AutoNumber>
      <Name>FB_MyBlock</Name>
      <ProgrammingLanguage>SCL</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4">
              <Parts /><Wires />
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList>
      </SW.Blocks.CompileUnit>
      <!-- Sections for SCL blocks go in ObjectList, not AttributeList -->
      <Section xmlns="…/SW/Interface/v5" Name="Input" />
      <Section xmlns="…/SW/Interface/v5" Name="Output" />
      <Section xmlns="…/SW/Interface/v5" Name="InOut" />
      <Section xmlns="…/SW/Interface/v5" Name="Static" />
      <Section xmlns="…/SW/Interface/v5" Name="Temp" />
      <Section xmlns="…/SW/Interface/v5" Name="Constant" />
      <Section xmlns="…/SW/Interface/v5" Name="Return" />
      <Source Name="BlockSource">BASE64_ENCODED_SCL_HERE</Source>
    </ObjectList>
  </SW.Blocks.FB>
</Document>
```

`xmlns` is shown abbreviated above; the real value is
`http://www.siemens.com/automation/Openness/SW/Interface/v5` on every `Section`.

Note the `Return` section is emitted for FBs too — see **Known contradictions**.

### Common import errors

| Error message | Cause | Fix |
|---|---|---|
| `Missing ''Namespace'' identifier attribute` | `<Namespace />` is missing from `<AttributeList>` — or was set as an XML attribute (`Namespace=""`) instead | Add `<Namespace />` as a **child element** of `<AttributeList>`. TIA calls these "identifier attributes" but means child elements |
| `Cannot import multilingual text with culture 'en-US'` | A `<MultilingualText>` block carries a `<Culture>` the project doesn't have (e.g. project is `en-GB`) | Use `<ObjectList />` — comment blocks are optional |
| `Class of the 'Siemens.Engineering.Section' type is not supported` | `<Section>` elements placed directly in `<ObjectList>` for a GlobalDB | For GlobalDB, `Interface/Sections` goes inside `<AttributeList>` |
| `Section 'Return' is not valid for this block` | `<Section Name="Return">` included in an FB | See **Known contradictions** — the generator emits it unconditionally |
| `Error when calling method 'Import'` (generic) | Catch-all for malformed XML | Export an existing block of the same type from TIA Portal and diff against your XML |

**Key distinction by block type:**

| Element | SCL block (FB/FC/OB) | GlobalDB |
|---|---|---|
| `Interface/Sections` | Inside `<ObjectList>` | Inside `<AttributeList>` |
| `<CompileUnit>` | Required in `<ObjectList>` | Not present |
| `<Namespace />` | Not emitted | Required in `<AttributeList>` |
| SCL source | Base64 in `<Source Name="BlockSource">` | Not applicable — members only |

---

## Tag table XML

`XmlHelper.CreateTagTableXml` builds this format, used by both `import_tag_table` and
`batch_rename_tags`.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V20" />
  <SW.Tags.PlcTagTable ID="0" CompositionName="TagTables">
    <AttributeList>
      <Name>Default tag table</Name>
    </AttributeList>
    <ObjectList>

      <SW.Tags.PlcTag ID="2" CompositionName="Tags">
        <AttributeList>
          <DataTypeName>Bool</DataTypeName>
          <ExternalAccessible>true</ExternalAccessible>
          <ExternalVisible>true</ExternalVisible>
          <ExternalWritable>false</ExternalWritable>
          <LogicalAddress>%I0.0</LogicalAddress>
          <Name>I_StartButton</Name>
        </AttributeList>
        <ObjectList />   <!-- safest: omit comments entirely -->
      </SW.Tags.PlcTag>

    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>
```

**Rules:**
- Every element in `ObjectList` needs a **unique integer `ID`** — start at 2 and increment for every
  element *and* sub-element.
- `ExternalWritable`: `false` for inputs (`%I`), `true` for outputs (`%Q`) and memory (`%M`).
- Addresses: `%I0.0`, `%Q0.0`, `%MW0`, `%MD0`, `%IW0`, etc.
- **Import replaces the table, it does not merge.** `TagTables.Import` uses `ImportOptions.Override`,
  so the imported XML becomes the table's full contents — any tag you leave out is gone. This is why
  `BatchRenameTagsAsync` re-emits every tag in the table, not just the renamed ones. Always read the
  table first, modify, and write the whole thing back.
- **Comment blocks are risky** — `CreateTagTableXml` emits `<Culture>en-US</Culture>` per tag. See
  **Known contradictions**.

---

## Locale / culture gotcha

TIA Portal projects store a language setting (e.g. `en-GB`, `en-US`, `de-DE`). Any
`<MultilingualText>` block with a `<Culture>` that doesn't match the project fails with:

```
Cannot import multilingual text with culture 'en-US': the specified culture does not exist within the current project.
```

**Rule for hand-written XML:** never include `<MultilingualText>` comment blocks — use `<ObjectList />`.
Block and tag comments are optional and the import succeeds without them.

To find a project's culture, export any existing block or tag table and read the `<Culture>` value.

---

## Known contradictions

Unresolved conflicts between this guide's rules and what the code actually emits. Test each on a
throwaway project before trusting either side.

**1. `CreateTagTableXml` and `CreateInstanceDbXml` hardcode `<Culture>en-US</Culture>.**
Both violate the locale rule above ([`XmlHelper.cs`](src/TiaOpennessMcpServer/Utilities/XmlHelper.cs),
the instance-DB template and the per-tag comment block). If the culture rule is right, then
`create_instance_db`, `import_tag_table` and `batch_rename_tags` all fail on a non-en-US project.
Either the generators need `<ObjectList />`, or the rule is narrower than stated. **Not yet tested
against an en-GB project.**

**2. `CreateSclBlockXml` emits `<Section Name="Return" />` for FBs.**
The error table above says `Return` is invalid for an FB, yet the generator emits it for FB, FC and OB
alike — and `create_block` is reported working. Either TIA V20 tolerates an empty `Return` section on
an FB, or FB creation is broken in a way nobody has hit. **Needs one live `create_block` with `type=FB`.**

**3. Approval-dialog frequency.** Documented as "every new process connection". A `connect` against an
already-running server process returned immediately with no dialog on 2026-08-03 — consistent with the
approval persisting for the life of the process, but that run may simply have been approved earlier.

---

## Rebuild workflow

The executable is locked while this checkout is running. Stop only the process whose executable path points to this checkout; do not kill every `TiaPortalDashboard.exe`, because another checkout may be attached to the user's project. Then build:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
.\src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe
```

After launching the rebuilt server, reconnect or restart clients so they refresh the advertised tools.

After restarting: connect and approve the TIA Portal dialog. **The project is not affected by
restarting the dashboard.**

---

## Default read-only workflow

```
1. Open TIA Portal V20 with the project
2. Start TiaPortalDashboard.exe
3. Point a compatible client at the displayed loopback /mcp URL
4. initialize + tools/list        →  verify the exact eight-tool V1 surface
5. connect_to_tia_portal          →  approve the visible TIA Portal dialog if prompted
6. get_status                     →  verify provenance and the read-only profile
7. list_devices                   →  capture canonical PLC identities
8. list_plc_objects               →  inspect the hierarchy for each PLC
9. find_plc_objects               →  narrow live metadata with meaningful filters
10. read_plc_object               →  retrieve strict or best-negotiated native evidence
11. get_tag_table_entries         →  inspect tags and constants
12. get_cross_references          →  query native uses and usedBy relationships
```

Do not infer authorization to write from an inspection request. A project-changing workflow requires both `TIA_MCP_ACCESS=full` and an explicit user instruction naming the intended change. `clone_project` is not part of that workflow.

---

## Server-side Openness API notes

- All TIA Openness calls must run on the STA thread via `StaTaskScheduler.RunAsync()`. Never call them
  from a thread pool thread.
- `PlcBlockComposition.Import(FileInfo, ImportOptions.Override)` — creates or overwrites a block.
- `PlcTagTableGroup.TagTables.Import(FileInfo, ImportOptions.Override)` — replaces a tag table.
- Block and tag-table lookup recurses through user groups (`PlcBlockUserGroup`, `PlcTagTableUserGroup`),
  so nested folders are found — but names must be unique across folders, since the first match wins.
- The canonical SimaticML representation branch exports with `PlcBlock.Export(...,
  ExportOptions.WithDefaults)` and returns the unmodified XML content when complete.
- The canonical SIMATIC SD representation branch calls
  `PlcBlock.ExportAsDocuments(DirectoryInfo, string)` and returns the generated `.s7dcl` plus required
  resource documents. It uses a unique temporary directory and does not import, compile, or save.
- The canonical raw-SCL representation branch uses TIA's external-source generation API without
  dependencies and returns the generated source unchanged; its temporary artifact is cleaned up.
- `PlcExternalSourceComposition.CreateFromFile(name, path)` + `GenerateBlocksFromSource(GenerateBlockOption.None)`
  — an alternative way to create SCL blocks from `.scl` files directly. Not currently used by the server.
- `block.Export(new FileInfo(path), ExportOptions.WithDefaults)` gives a reference XML to diff against
  when debugging an explicitly authorized import outside V1 acceptance.
