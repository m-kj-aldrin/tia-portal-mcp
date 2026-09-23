# Current dashboard workflow

Open the [managed dashboard](http://127.0.0.1:5000/). The Server tab stays available. Each running TIA process, and each retained project, has its own tab. Selecting a tab only changes this view. Connections stay independent. The dashboard starts TIA only from Open project in TIA on a closed project tab.

1. On a running TIA tab that is not connected, choose Connect and approve access in TIA if prompted. Disconnect detaches this bridge only.
2. On the Server tab, use List TIA processes. Get status with no project returns bridge facts. On a TIA tab, Get status uses that tab's process.
3. Project tools are available when that tab is live, connected and has an open primary project. Use List devices, then Read device. A single station or CPU is selected automatically.
4. Use List blocks, List UDTs or List tag tables, then choose the item by its path or name.
5. Use Read block, Read UDT, Read tag table or Read cross-references. Source stays on unless you clear it. Include dependencies is available only with source enabled and explicit external-source.

For tag tables, clear Include entries for metadata only (`entries: null`). Clear Include tag table path to skip path construction. Entries keep their own native objectId, or null when TIA has none. This typed detail read has no source format or checksum. Use the separate `export_tag_table` read tool for a native SimaticML XML document and returned-content checksum.

For blocks, source format best follows the native language and type. For UDTs, best tries external-source (`.udt`), then SIMATIC SD, then SimaticML. An explicit external-source, simatic-sd or simatic-ml request never falls back.

The result stays on the tab that started the call, including success, failure, elapsed time and formatted JSON. Copy result copies that JSON. If the browser refuses the clipboard, the page says it could not copy. Partial results, explicit nulls and native error text stay visible. Switching tabs does not move an in-flight result. Reconnection or a project change clears object selectors, and an older response does not refill the new connection.

The call log on each tab records the operation, time, duration, outcome and process when they are known. Dashboard actions, MCP calls and server events stay distinct. A call is attributed to the connection captured for that request. If that connection is already gone, the call returns `notConnected`. If the connection is lost while the call is waiting or running, it returns `reconnectRequired`. Reconnecting does not run that call again.

History remains after disconnect, invalidation, a project change or process close, and it survives a browser refresh. It is discarded when this server stops. An exact project path can bring a historical tab back when that project appears again; the path match does not connect it. Dismiss history removes only that dashboard record. On a closed tab that still has a project path, Open project in TIA starts a new TIA window for that path and connects it. A closed process that never had a project does not offer that action. If the project is already open, the closed tab is gone and the action is not offered.

The server keeps 400 log entries and 24 historical TIA tabs. A banner appears when older history was discarded. While TIA work is queued or running, Connect and Disconnect are disabled. Log and status polling stay available. Hiding the browser tab pauses that polling; server monitoring and each read's own checks continue.

The same twelve read tools and twelve modifying tools are available to MCP clients at `/mcp` in full access. Explicit read-only access exposes twelve reads; compilation requires full access. The dashboard submits those `tools/call` requests and supplies the selected tab's `processId`. Tab ids and connection ids are not MCP selectors. See [dashboard behavior and evidence](rehaul-dashboard.md) and [MCP usage and evidence](../reference/history/rehaul-mcp-cutover.md).

## Arguments and read examples

Each dashboard input displays the description published by MCP, its JSON type, whether it is required, and any default/examples. For exact current schemas, MCP clients use `tools/list`. Parameter names are case-sensitive; unsupported and duplicate fields are rejected. Use real JSON booleans (`true`/`false`) and integers, not quoted versions. Omit optional fields to use defaults; `null` is not omission.

| Argument | Meaning |
|---|---|
| `processId` | Positive integer from `list_tia_processes`; project work requires the user's dashboard connection. Omit only for passive `get_status`. |
| `plcObjectId` | CPU DeviceItem ID returned by `get_device`, whose SoftwareContainer owns PlcSoftware. |
| `objectId` | Native ID of the object that the particular tool accepts. Device, block, UDT, table and entry IDs are different selectors. Preserve IDs exactly. |
| `includePath` | Optional boolean, default `true`; `false` skips parent traversal and returns null paths. |
| `includeSource` | Block/UDT reads: optional boolean, default `true`; `false` returns metadata and `source:null` without export. |
| `sourceFormat` | Block/UDT reads: optional `best` (default), `external-source`, `simatic-sd` or `simatic-ml`. Explicit formats do not fall back. Source writes require an explicit format; `best` is not a write format. |
| `includeDependencies` | Block/UDT reads: optional boolean, default `false`; `true` requires source enabled and explicitly chosen `external-source`. Dependencies may add declarations to a later write. |
| `includeEntries` | Tag-table reads: optional boolean, default `true`; `false` skips tags/constants and returns `entries:null`. |

These are example `tools/call` parameter objects. The array lists separate calls; it is not an MCP batch request. Replace placeholders with IDs returned by the preceding discovery/detail calls and replace `20` with the connected process ID. Readback `complete`, `errors` and null fields distinguish complete, partial and unavailable data.

```json
[
  {"name":"list_tia_processes","arguments":{}},
  {"name":"get_status","arguments":{}},
  {"name":"get_status","arguments":{"processId":20}},
  {"name":"list_devices","arguments":{"processId":20}},
  {"name":"get_device","arguments":{"processId":20,"objectId":"<Device native ID>"}},
  {"name":"list_blocks","arguments":{"processId":20,"plcObjectId":"<CPU native ID>"}},
  {"name":"get_block","arguments":{"processId":20,"objectId":"<block native ID>","includeSource":true}},
  {"name":"list_udts","arguments":{"processId":20,"plcObjectId":"<CPU native ID>"}},
  {"name":"get_udt","arguments":{"processId":20,"objectId":"<UDT native ID>","includeSource":true}},
  {"name":"list_tag_tables","arguments":{"processId":20,"plcObjectId":"<CPU native ID>"}},
  {"name":"get_tag_table","arguments":{"processId":20,"objectId":"<table native ID>","includeEntries":true}},
  {"name":"export_tag_table","arguments":{"processId":20,"objectId":"<table native ID>"}},
  {"name":"get_cross_references","arguments":{"processId":20,"objectId":"<tag native ID>"}}
]
```

For a tag's cross-references, use its own non-null `objectId` from the table detail, not the containing table ID. For other engineering objects, use their own IDs; the native cross-reference service determines support. `get_tag_table` has no `includeSource`, `sourceFormat` or `includeDependencies` arguments. `export_tag_table` accepts only `processId` and the table's `objectId`; its format is always `simatic-ml`.

## Write operations

Choose **Write operations** on a connected project workspace, then select a tool. Load the relevant inventory or enter native IDs directly. For attribute editing/deletion, load tag tables and select a table: its existing tags and user constants populate the entry list.

Enter the parameters and run the tool. Attribute values use JSON: `"Int"` is a string, `false` is a boolean, and `10` is a number. The native attribute must accept that type. In contrast, **Constant value** for creation is a text field: enter a literal such as `100` or `T#1s`; the request sends it as a JSON string.

| Tool | Inputs after `processId` | Intended use |
|---|---|---|
| `write_blocks` | CPU, explicit format, documents, optional destination group | Create or replace complete block source. |
| `write_udts` | CPU, explicit format, documents, optional destination group | Create or replace complete UDT source. |
| `create_tag_table` | CPU, name, optional destination group | Create an empty table. |
| `create_tag` | Table ID, name, data type, logical address | Add a tag. Example: `Bool` and `%M0.0`. |
| `create_user_constant` | Table ID, name, data type, literal text | Add a constant. Example: `Int` and `100`. |
| `set_tag_entry_attribute` | Entry ID, native attribute name, typed value | Change one tag/constant attribute. Example: `LogicalAddress` and `"%M0.1"`. |
| `delete_tag_entry` | Entry ID | Delete one tag or user constant. |
| `import_tag_tables` | CPU, one XML document, optional destination group | Import native SimaticML using Override. |
| `delete_block` | Block ID | Delete the entire native block. |
| `delete_udt` | UDT ID | Delete the entire native PLC type. |
| `delete_tag_table` | Table ID | Delete the entire native table. |
| `compile_plc` | CPU ID | Explicitly compile PLC software and return this invocation's native diagnostics. |

Destination fields are `groupObjectId` or `groupPath`, never both. Choose an existing matching group in the intended CPU/unit scope, or omit both for the CPU root. The complete [write argument reference](write-operations.md#arguments-and-selectors) lists exact field names. [Data type/address examples](write-operations.md#data-types-addresses-and-constant-literals) and [writable attributes](write-operations.md#editing-an-existing-tag-or-user-constant) distinguish native values from bridge validation. TIA checks native compatibility; the example lists are not exhaustive enums.

### What belongs in the source fields?

**Document file name** is a plain staging filename such as `MotorStatus.udt`, not a path to a file you must save first. **Source content** is the complete native source, with real line breaks, including the declaration around the members or block body. The field help comes from the document properties in the MCP schema. The server stages temporary files for Siemens' file-based API and attempts cleanup afterward.

Use `.udt` for a complete external UDT declaration, `.scl` for a complete SCL block, `.s7dcl` with optional matching `.s7res` for SIMATIC SD, or `.xml` for SimaticML, according to the selected format. See [complete UDT and block examples](write-operations.md#complete-source-examples).

### Updating an existing object

For a block or UDT, choose the intended destination scope and use **Load selected source**. This reads the selected object, copies its document names/content and actual returned format into the form, and removes read-result checksums. Edit the complete source while keeping its declaration name for an update. Then write, inspect the result and read back the affected object. The selected source object supplies editable content; it does not bind the write to that object's ID or automatically set its destination group.

The declaration and native generation/import determine which objects TIA creates or replaces in the selected scope. A different filename alone does not rename an object. This read-edit-write workflow is the intended update operation; separate create/update tools and member patches are not planned. A source may affect multiple objects and can also be authored without reading first. There is no stale-source check. Any chosen write format must match the submitted native documents; changing a format label does not convert the content.

For a table's contents, use entry creation, attribute editing and deletion. For XML import, take the complete document from `export_tag_table` and submit its `name` and `content` to `import_tag_tables` with the intended CPU/scope. The typed `get_tag_table` JSON is not importable XML, and omitted XML entries must not be assumed deleted.

Use `delete_block`, `delete_udt` or `delete_tag_table` with the selected object's own native ID to delete an entire object. Native restrictions still apply; these tools have no force or cascade option. Inspect the result, then refresh its inventory and verify absence. Direct table metadata editing/renaming remains unexposed. See the [coverage matrix](write-operations.md#create-update-delete-and-read).

Use `compile_plc` as an explicit operation after editing when compiler diagnostics are needed. The result preserves native nested messages, paths, timestamps, states and error/warning counts for that invocation. `complete:true` describes diagnostic retrieval; check `compilationSucceeded` to determine compiler success. A compiler error can therefore return `complete:true`, `compilationSucceeded:false` and MCP `isError:true`. The tool does not read old compiler history or offer a force-rebuild-all flag. Other writes do not compile automatically.

The inspector retains the exact write request and response. Follow-up inventory/readback calls appear separately in history. Inspect errors before another write; operations can partially change TIA even when they fail. The dashboard never retries a write or saves the project. Saving and PLC upload/download are permanently outside MCP; save explicitly in TIA when ready.

There is no probe endpoint, disposable arming step or session-created-object restriction. See [write operations](write-operations.md) for formats and parameters.
