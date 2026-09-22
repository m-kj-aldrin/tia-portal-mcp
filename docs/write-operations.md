# MCP write operations

The normal server publishes nineteen tools: the existing eleven reads and eight writes. Both dashboard modes execute MCP `tools/call` requests through `/mcp`, using the definitions and dispatch in `Program.cs`. The separate write-probe endpoint and all arming, session-created-object and typed-failure restrictions have been removed.

`TIA_MCP_ACCESS` defaults to `full`; explicit `read-only` publishes eleven reads and rejects writes in the service. The lifecycle helper defaults a new start to full, preserves the stored profile on restart, and accepts an explicit override. Initialize reports `rehaul-writes-1`; status reports `rehaul-mcp-writes`, `nineteen-read-write-tools` (or `eleven-read-only-tools`) and the actual `writeToolsAvailable` value. Refresh MCP tool discovery after upgrading.

## Parameters

Every write requires a positive `processId` and a user-connected primary project. Native identifiers remain opaque and are passed unchanged. Unknown and duplicate fields are rejected before dispatch.

| Tool | Required parameters after processId | Optional parameters |
|---|---|---|
| `write_blocks`, `write_udts` | `plcObjectId`, `sourceFormat`, `documents` | `groupObjectId` or `groupPath` |
| `create_tag_table` | `plcObjectId`, `name` | `groupObjectId` or `groupPath` |
| `create_tag` | `objectId` of a table, `name`, `dataType`, `logicalAddress` | None |
| `create_user_constant` | `objectId` of a table, `name`, `dataType`, `value` (native literal as a string) | None |
| `set_tag_entry_attribute` | `objectId` of an entry, `attributeName`, `attributeValue` | None |
| `delete_tag_entry` | `objectId` of an entry | None |
| `import_tag_tables` | `plcObjectId`, `documents` (one SimaticML XML document) | `groupObjectId` or `groupPath` |

`plcObjectId` identifies the CPU DeviceItem from `get_device`. An omitted destination means that CPU's root composition. A supplied group must belong to that CPU and have the matching native composition type. Use the group's native ID or its exact `PLC[/unit]/group` inventory path, never both. Paths are the fallback for groups with no identifier; ambiguous or absent paths are rejected.

Existing tags and user constants can be targeted directly. System constants cannot be edited or deleted. `attributeName` is the native writable property name; TIA decides which properties and values are accepted. `attributeValue` supports JSON strings, booleans and finite numbers. Integers use Int32 where representable, otherwise Int64; remaining numbers use Double. No value is silently converted to a string.

## Native source writes

`documents` is an array of `{ "name": "file.scl", "content": "exact source text" }`. The client supplies contents, not server file paths. Names must be unique plain Windows file names. No source declaration is renamed or rewritten.

- `external-source`: one `.scl`, `.awl`, `.db` or `.udt`. Create a temporary native external source, call native `GenerateBlocksFromSource(GenerateBlockOption.None)` in the destination scope, then remove the external source and owned files.
- `simatic-sd`: one `.s7dcl` and optional `.s7res` with the same stem. Import directly into the block/type composition with `ImportDocumentOptions.Override`.
- `simatic-ml`: one `.xml`. Import directly into the block/type/tag-table composition with `ImportOptions.Override`.

Source format is explicit; there is no write-time fallback. Source declarations and native import/generation semantics determine affected names, including same-name replacement and multiple outputs. The block/UDT tool chooses the destination composition, not a promise that native source affects exactly one object. External generation failure may have already changed generated objects; writes are not transactions and no rollback is claimed.

Staging preserves the supplied text as UTF-8 without a BOM. The server owns uniquely named temporary directories. Cleanup is attempted on success and failure; a lost project context prevents further native access, and cleanup failures cannot be reported as complete success.

## Results and dashboard

Writes return the shared `processId`, `readAtUtc` (result observation time), `complete` and `errors` envelope, plus `operation`, `saved:false`, nullable native `projectModified`, `cleanupFailed`, native import state/messages when supplied, and `affectedObjects`. Affected objects carry their actual kind, name and native ID or null. Entries also include their containing table's `parentObjectId` when available, for readback. A deletion returns its identity captured before deletion. Result retrieval failures retain already-observed affected objects and exact errors.

Incomplete writes set MCP `isError:true`; partial objects/errors remain in the payload. Native failures retain `tia-openness` provenance and exact text. Connection loss discards the payload and requires explicit reconnection; it does not establish that a write was rolled back. No write is retried automatically. The service captures its ticket before queueing and all native work stays on the shared STA.

The dashboard offers **Read operations** and **Write operations**, with forms generated from published schemas. Existing entry choices come from `get_tag_table(includeEntries:true)`. Inventory loading, empty tables and failed reads are distinguished. A native ID can also be supplied directly. Source documents have editable name/content fields, and **Load selected source** copies the read tool's source documents into the form without checksums or name substitution.

After writes, relevant inventory/detail calls run through MCP. The write result stays selected and those follow-up reads remain in history. Readback failure does not rerun the write or erase its response. Project/connection/PLC changes clear stale target selections. Saving, explicit compilation, downloads and online operations are not exposed by this increment; the user saves in TIA.

## Evidence

The user reported on 2026-09-22 that the earlier writes worked. This is accepted as user-reported native evidence; it does not turn every historical probe checklist item into an independently replayed test.

Local checks for this integration cover all eight published write schemas and dispatch paths, explicit read-only rejection, existing entry IDs, typed values, source document validation and exact staging, stale tickets/project transitions, partial/cleanup errors, dashboard payloads, source loading and post-write refresh. These integration checks do not claim newly executed native writes against the user's projects.

Verification on 2026-09-22:

- The normal Release build passed. NU1900 reported unavailable NuGet vulnerability data; compilation succeeded.
- The Siemens-free harness passed 90/90 checks. Dashboard and architecture checks passed 34/34 tests.
- The managed helper gracefully stopped the earlier server and loaded the successful Release executable: PID 33128, port 5000, full access profile. Its status reported `rehaul-mcp-writes` and nineteen published tools.
- The live HTTP smoke test passed 1/1 against that server, including all tool dispatch paths using an unattached process ID, the retired endpoint returning 404, cross-origin MCP requests returning 403, and unsupported request content types returning 415. It did not execute native writes.
- The in-app browser displayed both operation modes, all eight write choices, existing table/entry selection controls, typed attribute input and editable source document fields. No browser console warnings or errors were observed. Native inventory population and mutation readback were covered by simulated dashboard tests, not a newly attached live project.

Attachments require explicit reconnection after this restart, and MCP clients must refresh tool discovery. No native writes, saves, explicit compilation or online operations were performed during this integration validation. The user's earlier successful writes remain separate user-reported evidence.
