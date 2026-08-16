# TIA Portal MCP Server — Developer Guide

Repository-specific architecture, behavior, and safety notes for TIA Portal V20.

Reviewed against the source on 2026-08-11. Where the code and this document disagree, the code wins —
see **Known contradictions** at the end for the few points still unresolved.

---

## Architecture

```
Local-process MCP clients             HTTP MCP clients, including ChatGPT
  (stdio transport)                   (Streamable HTTP)
        │                                    │
        │  newline-delimited JSON-RPC        │  HTTP POST /mcp
        ▼                                    ▼
TiaPortalDashboard.exe  ──  HandleMcpRequest()  ──  REST endpoints
  (--mcp-stdio flag)                 (shared)        (/api/*)
        │
        │  TIA Openness API  (COM · STA thread required)
        ▼
Siemens.Engineering.dll  (TIA Portal V20 PublicAPI)
        │
        ▼
TIA Portal V20  (must be open with a project loaded)
```

**Source:**
```
src/TiaOpennessMcpServer/
├── Program.cs                     # HTTP listener, routing, stdio loop, MCP tool defs + dispatch
├── MainForm.cs                    # WinForms tray window (HTTP mode only)
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

## Transport modes

### stdio

Run with `--mcp-stdio`. The process communicates entirely over stdin/stdout; no window, no HTTP server.

Generic client configuration shape:

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

Client-specific configuration locations are not part of this repository. Add `"env": {"TIA_MCP_ACCESS": "full"}` only for an explicitly intended full-access session.

### Streamable HTTP

```
http://127.0.0.1:5000
```

Start the exe with no arguments. `GET /` serves the dashboard and MCP clients connect to
`http://127.0.0.1:5000/mcp`. The ChatGPT app and other Streamable HTTP clients can use this endpoint;
exact connector UI labels are client-specific.

Port `5000` is the default. Set `TIA_MCP_PORT` to an integer from `1` through `65535` before launch when a different local port is required, and update the dashboard/MCP URL accordingly.

`GET /mcp` deliberately returns 405 with a JSON-RPC error body so clients detect the modern
Streamable HTTP transport instead of falling back to HTTP+SSE discovery. Protocol version is
`2025-03-26`, or `2024-11-05` echoed back if the client asks for it.

---

## TIA Portal approval dialog

`connect_to_tia_portal` / `POST /api/connect` calls `AttachToRunningAsync()`, which blocks the STA
thread while TIA Portal shows its access approval dialog. Consequences:

1. **The call does not return until the user clicks "Yes to all".** Set client timeouts to ≥90 seconds.
2. The dialog is shown per connecting process, so each app restart or new stdio spawn can require a
   fresh approval. It sometimes appears behind other windows — check the TIA Portal taskbar button.
3. While the STA thread is blocked, every other STA-bound operation queues behind it. `get_status`
   only reads a null check and still returns immediately.

---

## Access profiles

- **Default read-only:** when `TIA_MCP_ACCESS` is unset, only non-mutating inspection and analysis tools are advertised and callable.
- **Full:** set `TIA_MCP_ACCESS=full` in the server process environment and restart to expose implemented write, import, create, compile, rename, and save tools.
- **Quarantined:** `clone_project` is never advertised and direct MCP/REST calls are rejected. Its legacy implementation is also unsupported for an externally attached project.

Full access changes availability only. An agent still needs explicit user authorization for project-changing work. The profile gates MCP discovery and dispatch, rejects mutating REST requests, and hides or disables corresponding dashboard controls.

## MCP tool catalog

Definitions and dispatch are shared by HTTP and stdio in `Program.cs`. Availability below describes actual server tools, not higher-level workflows or planned features.

| Tool | Required args | Optional | Availability | Returns |
|---|---|---|---|---|
| `connect_to_tia_portal` | — | `projectPath` ⚠ | Read-only default | Project info: `name, path, author, comment, modifiedDate, deviceCount, isModified` |
| `get_status` | — | — | Read-only default | Connection plus `accessProfile`, `writeEnabled`, and optional project details |
| `save_project` | — | — | Full | `{success:true}` |
| `list_devices` | — | — | Read-only default | Array of `{name, typeIdentifier, deviceType, cpuModel, ipAddress, subnetMask, gateway, slotCount, modules[]}` |
| `list_blocks` | `device` | — | Read-only default | Array of `{name, type, number, language, author, comment, modified, isKnowHow, sizeBytes}` — recurses into block folders |
| `read_block` | `device`, `block` | — | Read-only default | Block info plus `sourceCode` (SCL only) and `xmlContent` |
| `read_lad_source` | `device`, `block` | — | Read-only default | Authoritative pure LAD as SIMATIC SD metadata, complete `.s7dcl`, available `.s7res`, file names, and warnings |
| `write_block_scl` | `device`, `block`, `source` | — | Full | `{success:true}` |
| `import_block_xml` | `device`, `block`, `content` | — | Full | `{success:true}` |
| `compile_block` | `device`, `block` | — | Full | `{result:"…"}` — state, error/warning counts, and messages |
| `analyze_block` | `device`, `block` | — | Read-only default | SCL analysis result (see below) |
| `create_block` | `device`, `name`, `type`, `sourceCode` | `number` | Full | Created block info |
| `create_instance_db` | `device`, `name`, `instanceOfName` | `number` | Full | Created block info |
| `list_tag_tables` | `device` | — | Read-only default | Array of `{name, tagCount, comment}` |
| `get_tags` | `device`, `table` | — | Read-only default | Array of `{name, dataType, address, accessible, writable, comment}` |
| `import_tag_table` | `device`, `content` | — | Full | `{success:true}` |
| `batch_rename_tags` | `device`, `table`, `renames` | — | Full | `{renamed:N}` |
| `analyze_scl` | `source` | `blockName`, `blockType` | Read-only default | SCL analysis result |
| `get_option_packages` | — | — | Read-only default | Array of option packages / used products |
| `get_project_signature` | — | — | Read-only default | Full index of every device → blocks + tag tables |

There is no dedicated LAD-to-SCL converter. `read_lad_source` returns the authoritative LAD representation; lossless parsing and progressive network retrieval are planned in `docs/lad-agent-architecture.md`.

**Tool quirks worth knowing:**

- **`projectPath` on `connect_to_tia_portal` is a selector, not an opener.** It prefers a matching
  running TIA process; when no path is supplied, the first process is used.
- **`number` is declared as a `string`** in every schema (`create_block`, `create_instance_db`) and
  parsed with `int.TryParse`. Pass `"5"`, not `5`. Anything unparseable silently becomes auto-number.
- **`create_block` always creates SCL.** `type` selects FB / FC / OB / GlobalDB, but the language is
  hardcoded — there is no way to create a LAD/FBD/STL block through it. Use `import_block_xml` for those.
- **`read_lad_source` accepts only pure LAD.** It rejects non-LAD, mixed or partial document exports,
  and know-how-protected blocks instead of returning incomplete source. `read_block` remains available
  for SCL and raw SimaticML XML.
- **`clone_project` is quarantined.** It is filtered out of both tool profiles and direct calls fail.
  Its legacy implementation saves and closes projects internally and rejects an externally attached source.
- Project-backed tools call `EnsureConnected()` and fail when no TIA project is attached. `analyze_scl`
  is standalone and does not need a TIA connection.
- Tool errors come back as `isError: true` with the first line of the exception as text, not as a
  JSON-RPC error. The last 200 calls are visible at `GET /api/mcp/log` and on the dashboard.

### SCL analysis result shape

Returned by `analyze_block` and `analyze_scl`:

```
{ blockName, blockType, isValid, summary,
  diagnostics: [{ severity: Error|Warning|Info|Hint, code, message, line, column, suggestion }],
  variables:   [{ name, dataType, section, initValue, comment, declLine }],
  metrics:     { linesOfCode, linesOfComments, cyclomaticComplexity, nestingDepthMax,
                 variableCount, timerCallCount, counterCallCount, functionCallCount } }
```

`analyze_block` is an SCL analyzer and returns `{error:"Block is not SCL or source could not be read."}` for LAD/FBD/STL. Pure LAD has an authoritative text export through `read_lad_source`; it is not input to this SCL analyzer.

---

## HTTP API endpoints

All at `http://127.0.0.1:5000`. All JSON. **Keys are camelCase** (`content`, not `Content`).
Enums serialise as strings (`"GlobalDB"`, `"SCL"`). Access-profile rejections use HTTP 403 and the
quarantined clone route uses HTTP 410; older route errors may still return `{error:"…"}` with HTTP 200.

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
anywhere in the text becomes a member. Always `read_block` afterwards to confirm what landed, or hand-write
the XML and use `import_block_xml`.

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
already-running stdio process returned immediately with no dialog on 2026-08-03 — consistent with the
approval persisting for the life of the process, but that run may simply have been approved earlier.

---

## Rebuild workflow

The executable is locked while this checkout is running. Stop only the process whose executable path points to this checkout; do not kill every `TiaPortalDashboard.exe`, because another checkout may be attached to the user's project. Then build:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj -c Release
.\src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe
```

For stdio mode, reconnect or restart the client after a rebuild so it launches the new executable.

After restarting: connect and approve the TIA Portal dialog. **The project is not affected by
restarting the dashboard.**

---

## Default read-only workflow

```
1. Open TIA Portal V20 with the project
2. Start TiaPortalDashboard.exe  (or let an MCP client spawn it via stdio)
3. connect_to_tia_portal          (approve the TIA Portal dialog)
4. list_devices                   →  note the exact device name
5. list_blocks / list_tag_tables  →  inspect structure
6. read_lad_source                →  authoritative source for pure LAD
7. read_block                     →  SCL source or raw SimaticML
8. get_project_signature          →  stable project inventory
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
- `read_block` works by exporting to `C:\Temp\TiaExports` and reading the file back. If the block has
  never been compiled the export can fail; the returned `sourceCode` then contains a `//` comment
  explaining why. Compile the project in TIA Portal (Ctrl+B) and retry.
- `read_lad_source` calls `PlcBlock.ExportAsDocuments(DirectoryInfo, string)` and returns the generated
  `.s7dcl` and available `.s7res` contents directly. It exports into a unique temporary directory and
  does not import, compile, or save the project.
- `PlcExternalSourceComposition.CreateFromFile(name, path)` + `GenerateBlocksFromSource(GenerateBlockOption.None)`
  — an alternative way to create SCL blocks from `.scl` files directly. Not currently used by the server.
- `block.Export(new FileInfo(path), ExportOptions.WithDefaults)` gives a reference XML to diff against
  when debugging import errors.
