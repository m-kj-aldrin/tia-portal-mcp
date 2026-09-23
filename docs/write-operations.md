# MCP write operations

The normal server publishes twenty-four tools: twelve reads and twelve modifying operations, including explicit PLC compilation. MCP is the primary interface to the shared engineering operations. The dashboard tests those same tools through `/mcp`; its controls do not define their behavior. MCP definitions and dispatch have one authoritative implementation, with no required source-file location. The retired write-probe endpoint and its arming/session-created restrictions are not part of this contract.

`TIA_MCP_ACCESS` defaults to `full`; explicit `read-only` publishes twelve reads and rejects writes and compilation in the service. The lifecycle helper defaults a new start to full, preserves the stored profile on restart, and accepts an explicit override. Initialize reports `native-compile-delete-export-1`; status reports phase `native-compile-delete-export`, publication `twenty-four-read-write-tools` (or `twelve-read-only-tools`) and the actual `writeToolsAvailable` value. Refresh MCP tool discovery after upgrading.

## Native operation boundary

`write_blocks` and `write_udts` follow native source generation/import. An update is a consumer workflow: read the source, edit the complete document, then write it to the selected process and CPU/scope using a supported explicit format. Keep the intended native declaration name and scope when replacing an existing object. The source declaration and native operation determine what is affected; the staging filename is not an update selector.

Do not add separate create/update block or UDT tools, create-only/update-only modes or member-patch semantics to simulate an API that these native operations do not provide. This is the intended write contract, not an incomplete CRUD abstraction.

| Tool / format | Native operation used by this implementation |
|---|---|
| `write_blocks` / `write_udts`, `external-source` | `ExternalSources.CreateFromFile`, then `GenerateBlocksFromSource(GenerateBlockOption.None)` in the selected scope |
| `write_blocks` / `write_udts`, `simatic-sd` | Target `Blocks` / `Types` composition's `ImportFromDocuments(..., ImportDocumentOptions.Override)` |
| `write_blocks` / `write_udts`, `simatic-ml` | Target `Blocks` / `Types` composition's `Import(..., ImportOptions.Override)` |
| `create_tag_table` | `TagTables.Create` |
| `create_tag` / `create_user_constant` | `Tags.Create` / `UserConstants.Create` |
| `set_tag_entry_attribute` | The tag or user constant's native `SetAttribute` |
| `delete_tag_entry` | The tag or user constant's native `Delete` |
| `import_tag_tables` | `TagTables.Import(..., ImportOptions.Override)` |
| `delete_block` / `delete_udt` / `delete_tag_table` | The resolved `PlcBlock` / `PlcType` / `PlcTagTable` object's native `Delete()` |
| `compile_plc` | The selected CPU's `PlcSoftware.GetService<ICompilable>().Compile()` |

Bridge validation, guarded connection selection, temporary file ownership and error reporting remain necessary around these calls. They do not promise transactional replacement, rollback, stable IDs or one affected object. Native failures and partial results remain visible.

Compilation is explicit and returns diagnostics from that invocation; other writes do not invoke it automatically. Saving projects and PLC upload/download are permanently outside the MCP surface. There are no online, force-delete or automatic retry options. See [compile, deletion and tag-table export](compile-delete-export.md) for the five-tool extension and its evidence limits.

## Parameters

Tool descriptions and parameter help are published by MCP `tools/list` and displayed beside dashboard inputs from the same definitions. Help states JSON types, requirements, defaults and representative examples. Examples of native types/addresses are guidance, not an exhaustive allowlist; TIA validates the selected CPU and native object.

### Create, update, delete and read

| Object | Create | Update | Delete | Read |
|---|---|---|---|---|
| Block | `write_blocks` | Complete source generation/import through `write_blocks` | `delete_block` | `get_block` |
| UDT | `write_udts` | Complete source generation/import through `write_udts` | `delete_udt` | `get_udt` |
| Tag table | `create_tag_table` or `import_tag_tables` | XML import with native Override; entries can be edited separately | `delete_tag_table` | `get_tag_table`; native XML through `export_tag_table` |
| Tag | `create_tag` | `set_tag_entry_attribute` with the tag's ID | `delete_tag_entry` | `get_tag_table` |
| User constant | `create_user_constant` | `set_tag_entry_attribute` with the constant's ID | `delete_tag_entry` | `get_tag_table` |
| System constant | Not exposed | Read-only | Not exposed | `get_tag_table` |

This is implementation coverage, not proof of every native scenario. Direct table metadata editing remains unexposed. Source-based updating is already the intended workflow. Update-only modes and member patches are not missing features to add. No stale-source/checksum precondition is currently provided or implied as future work.

### Arguments and selectors

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
| `delete_block`, `delete_udt`, `delete_tag_table` | `objectId` of the matching block, UDT or table | None |
| `compile_plc` | `plcObjectId` of the CPU DeviceItem | None |

`plcObjectId` identifies the CPU DeviceItem from `get_device`. An omitted destination means that CPU's root composition. A supplied group must belong to that CPU and have the matching native composition type. Use the group's native ID or its exact `PLC[/unit]/group` inventory path, never both. Paths are the fallback for groups with no identifier; ambiguous or absent paths are rejected.

Existing tags and user constants can be targeted directly. System constants cannot be edited or deleted. `attributeName` is the native writable property name; TIA decides which properties and values are accepted. `attributeValue` supports JSON strings, booleans and finite numbers. Integers use Int32 where representable, otherwise Int64; remaining numbers use Double. No value is silently converted to a string.

Names, IDs, types, addresses and literal strings are not trimmed or rewritten. Required text must be nonblank. Omit unused optional destination fields rather than sending null or empty strings. `processId` is an integer from 1 to 2147483647, not a quoted number. `create_tag_table` creates an empty table; a same-name table is not an instruction to update it. TIA checks naming/uniqueness.

### Data types, addresses and constant literals

`dataType` is a native type name. Common tag examples include `Bool`, `Byte`, `Word`, `Int`, `DInt` and `Real`. Supported project-defined PLC types can also be used where TIA permits them. This is not a fixed list of all valid types: the CPU, memory area and context matter, and addressed tags and user constants have different type constraints.

The following are representative English-mnemonic address/type combinations, not a declaration that these addresses are configured or free in a project:

| `dataType` | `logicalAddress` example | Meaning |
|---|---|---|
| `Bool` | `%M0.0` | Memory byte 0, bit 0 |
| `Byte` | `%IB0` | Input byte starting at byte 0 (8 bits) |
| `Int` | `%IW64` | Input word starting at byte 64 (16 bits) |
| `Word` | `%QW64` | Output word starting at byte 64 (16 bits) |
| `Real` | `%MD100` | Memory double word starting at byte 100 (32 bits) |

`I`, `Q` and `M` denote input, output and memory areas. Bit addresses use `byte.bit`, with bit numbers 0 through 7; `B`, `W` and `D` denote byte, word and double-word widths. CPU address limits, supported types and area/width compatibility are checked by TIA, not by a bridge address parser. A block's DB member syntax should not be confused with a PLC tag-table address. See [Siemens V20 addressing](https://docs.tia.siemens.cloud/r/en-us/v20/programming-basics/using-and-addressing-operands/addressing-operands/addressing-plc-tags/addressing-plc-tags).

**Bridge restriction:** `create_tag` currently requires a nonblank `logicalAddress`. Siemens documents native creation with an empty address, but this tool rejects it before dispatch. Documenting that difference does not change validation.

A user constant has `value` instead of `logicalAddress`. Supply a native literal as a JSON string: `"100"` for `Int`, `"1.5"` for `Real`, or `"T#1s"` for `Time`. JSON `100` and `true` are not accepted by `create_user_constant.value`. TIA validates the literal against the requested type.

### Editing an existing tag or user constant

Get the entry's own non-null `objectId` from `get_tag_table`. Supply one native attribute name and one correctly typed JSON value:

| Entry | `attributeName` | JSON value type / example |
|---|---|---|
| Tag | `DataTypeName` | string, e.g. `"Int"` |
| Tag | `LogicalAddress` | string, e.g. `"%IW64"` |
| Tag | `Name` | string; writable in V20 |
| Tag | `ExternalAccessible`, `ExternalVisible`, `ExternalWritable`, `IsSafety` | boolean, e.g. `false`; native applicability still governs |
| User constant | `DataTypeName` | string, e.g. `"Int"` |
| User constant | `Value` | native literal string, e.g. `"100"` |

Creation/readback uses the field `dataType`; editing uses the native attribute `DataTypeName`. User-constant `Name` is documented read-only, unlike a V20 tag's `Name`. These documented attributes are guidance, not a bridge allowlist or a guarantee that every property change is accepted for every object. Numeric `attributeValue` is permitted by the bridge only for native attributes expecting a number. Null, objects and arrays are rejected. See [Siemens V20 tags](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/tags-and-tag-tables/accessing-plc-tags) and [user/system constants](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/tags-and-tag-tables/accessing-plc-constants).

## Native source writes

`documents` is an array of `{ "name": "file.scl", "content": "exact source text" }`. The client supplies contents, not server file paths. Names must be unique plain Windows file names. No source declaration is renamed or rewritten.

**Document file name** is the temporary staging filename, for example `MotorStatus.udt`. It is not a filename that the client must first save, a server path, or the selector/name of the engineering object. Names must be unique ignoring case and at most 128 characters, with no path separators, reserved Windows filenames, control characters or trailing dot/space.

**Source content** contains the complete native declaration/document, including its surrounding syntax. A `.udt` includes `TYPE`, `STRUCT`, `END_STRUCT` and `END_TYPE`; a `.scl` includes its complete block declaration and body. SimaticML and SIMATIC SD retain their respective native structures. A member list, change instruction or `get_tag_table` JSON is not a native source document. Content must be nonblank valid Unicode. Enter real line breaks in the dashboard; JSON clients encode line breaks as `\n` or `\r\n`.

- `external-source`: one `.scl`, `.awl`, `.db` or `.udt`. Create a temporary native external source, call native `GenerateBlocksFromSource(GenerateBlockOption.None)` in the destination scope, then remove the external source and owned files.
- `simatic-sd`: one `.s7dcl` and optional `.s7res` with the same stem. Import directly into the block/type composition with `ImportDocumentOptions.Override`.
- `simatic-ml`: one `.xml`. Import directly into the block/type/tag-table composition with `ImportOptions.Override`.

Source format is explicit; there is no write-time fallback. Source declarations and native import/generation semantics determine affected names, including same-name replacement and multiple outputs. The block/UDT tool chooses the destination composition, not a promise that native source affects exactly one object. External generation failure may have already changed generated objects; writes are not transactions and no rollback is claimed.

Staging preserves the supplied text as UTF-8 without a BOM. The server owns uniquely named temporary directories. Cleanup is attempted on success and failure; a lost project context prevents further native access, and cleanup failures cannot be reported as complete success.

### Read, edit and write an existing block or UDT

1. Use `get_block` or `get_udt` with `includeSource:true`. Leave `includeDependencies:false` for the normal single-object workflow. Inspect errors and the returned documents before editing.
2. Edit the complete returned source, preserving the declaration name, namespace and intended CPU/unit scope for an update. A source may contain additional declarations; review all of them.
3. Call `write_blocks` or `write_udts` with the intended `plcObjectId` and group, the chosen supported `sourceFormat`, and each document's `name` and edited `content`. The submitted documents must match that format; normally reuse the format returned by the read. Choosing a different format requires documents in that native format, not merely changing the format label. Strip `checksum` and other read-result fields: document objects accept only `name` and `content`. Preserve the matching resource document when using SD.
4. Inspect `complete`, `errors`, `cleanupFailed` and `affectedObjects`. Reacquire IDs from the result/inventory and read back the affected objects; do not assume replacement preserves their previous IDs.

Reading first is recommended, not enforced. There is no existing-object ID, create-only/update-only mode, member-patch operation or expected-checksum parameter on either source writer. Changing only the filename does not rename the engineering object. Changing a declaration name may create a different object while leaving the old one in place; this is not a rename operation. Same-name external declarations overwrite according to native scope rules; SD/XML use native Override. See [Siemens V20 source-generation rules](https://docs.tia.siemens.cloud/r/en-us/v20/creating-and-managing-blocks/using-external-source-files-for-stl-and-scl/basics-of-using-external-source-files).

`get_tag_table` returns typed JSON entries, not XML, so its result cannot be sent directly to `import_tag_tables`. Use entry creation/attribute editing/deletion for ordinary table-content changes. XML import requires a complete native SimaticML document obtained or authored separately. Do not treat Override as an exact synchronization contract or assume entries omitted from XML will be deleted.

### Complete source examples

The following are MCP `tools/call` parameter objects (`name` plus `arguments`). Replace process and native-ID placeholders with discovered values; choose the intended existing destination group for a non-root write. These examples are checked against the production request parser, not native generation or a PLC compile. Do not use example addresses/names as evidence that they are available in your project.

This complete UDT source declares `MotorStatus`:

```scl
TYPE "MotorStatus"
VERSION : 0.1
   STRUCT
      Running : Bool;
      Faulted : Bool;
   END_STRUCT;
END_TYPE
```

```json
{
  "name": "write_udts",
  "arguments": {
    "processId": 20,
    "plcObjectId": "<CPU native ID>",
    "sourceFormat": "external-source",
    "documents": [{
      "name": "MotorStatus.udt",
      "content": "TYPE \"MotorStatus\"\nVERSION : 0.1\n   STRUCT\n      Running : Bool;\n      Faulted : Bool;\n   END_STRUCT;\nEND_TYPE\n"
    }]
  }
}
```

If the current `MotorStatus` already contains `Running`, adding `Faulted` means editing that full declaration and writing it back under the same declaration name and scope. The write request is the same kind of request as creation.

This complete SCL FB copies an input to an output:

```scl
FUNCTION_BLOCK "MotorControl"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Start : Bool;
   END_VAR
   VAR_OUTPUT
      Running : Bool;
   END_VAR
BEGIN
   #Running := #Start;
END_FUNCTION_BLOCK
```

```json
{
  "name": "write_blocks",
  "arguments": {
    "processId": 20,
    "plcObjectId": "<CPU native ID>",
    "sourceFormat": "external-source",
    "documents": [{
      "name": "MotorControl.scl",
      "content": "FUNCTION_BLOCK \"MotorControl\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\n   VAR_INPUT\n      Start : Bool;\n   END_VAR\n   VAR_OUTPUT\n      Running : Bool;\n   END_VAR\nBEGIN\n   #Running := #Start;\nEND_FUNCTION_BLOCK\n"
    }]
  }
}
```

### Table and entry examples

Create an empty table, then use its returned/discovered native ID for entry creation:

```json
{"name":"create_tag_table","arguments":{"processId":20,"plcObjectId":"<CPU native ID>","name":"Signals"}}
```

```json
{"name":"create_tag","arguments":{"processId":20,"objectId":"<table native ID>","name":"Start","dataType":"Bool","logicalAddress":"%M0.0"}}
```

```json
{"name":"create_user_constant","arguments":{"processId":20,"objectId":"<table native ID>","name":"Limit","dataType":"Int","value":"100"}}
```

Read existing entries and edit one native attribute, then read the table again:

```json
{"name":"get_tag_table","arguments":{"processId":20,"objectId":"<table native ID>","includeEntries":true}}
```

```json
{"name":"set_tag_entry_attribute","arguments":{"processId":20,"objectId":"<tag native ID>","attributeName":"LogicalAddress","attributeValue":"%M0.1"}}
```

```json
{"name":"set_tag_entry_attribute","arguments":{"processId":20,"objectId":"<constant native ID>","attributeName":"Value","attributeValue":"200"}}
```

Deleting an entry requires that entry's ID; this request cannot delete the containing table:

```json
{"name":"delete_tag_entry","arguments":{"processId":20,"objectId":"<tag native ID>"}}
```

### Explicit compilation, object deletion and table export

These are separate `tools/call` parameter objects. Supply a currently connected process and native IDs for the intended objects; the deletion calls remove the entire selected object.

```json
{"name":"compile_plc","arguments":{"processId":20,"plcObjectId":"<CPU native ID>"}}
```

```json
{"name":"delete_block","arguments":{"processId":20,"objectId":"<block native ID>"}}
```

```json
{"name":"delete_udt","arguments":{"processId":20,"objectId":"<UDT native ID>"}}
```

```json
{"name":"delete_tag_table","arguments":{"processId":20,"objectId":"<table native ID>"}}
```

`export_tag_table` is a read tool and is also available with read-only access. It returns native SimaticML XML in `source.documents`, including checksums, and accepts no format or path argument. To import that document later, pass only its `name` and `content` to `import_tag_tables` with the intended CPU and scope.

```json
{"name":"export_tag_table","arguments":{"processId":20,"objectId":"<table native ID>"}}
```

`compile_plc` returns the current invocation's recursive native `messages`, `state`, `errorCount` and `warningCount`. `complete:true` means the API result and diagnostics were read completely; it can coexist with `compilationSucceeded:false` when the compiler reports errors. Compiler failure sets MCP `isError:true`. A partial/unavailable diagnostic result has `complete:false` and `compilationSucceeded:null`. Warnings alone can still yield `compilationSucceeded:true`. The tool does not read past TIA compiler history and has no force-rebuild-all mode.

## Results and dashboard

Mutation tools return the shared `processId`, `readAtUtc` (result observation time), `complete` and `errors` envelope, plus `operation`, `saved:false`, nullable native `projectModified`, `cleanupFailed`, native import state/messages when supplied, and `affectedObjects`. Affected objects carry their actual kind, name and native ID or null. Entries also include their containing table's `parentObjectId` when available, for readback. A deletion returns its identity captured before deletion, without rereading the deleted proxy. Result retrieval failures retain already-observed affected objects and exact errors. The dedicated compilation result uses the diagnostic fields described above, plus `saved:false` and native `projectModified`.

Incomplete writes set MCP `isError:true`; partial objects/errors remain in the payload. Native failures retain `tia-openness` provenance and exact text. Connection loss discards the payload and requires explicit reconnection; it does not establish that a write was rolled back. No write is retried automatically. The service captures its ticket before queueing and all native work stays on the shared STA.

The dashboard offers **Read operations** and **Write operations**, with forms generated from published schemas. Existing entry choices come from `get_tag_table(includeEntries:true)`. Inventory loading, empty tables and failed reads are distinguished. A native ID can also be supplied directly. Source documents have editable name/content fields, and **Load selected source** copies the read tool's source documents into the form without checksums or name substitution.

After writes, relevant inventory/detail calls run through MCP. The write result stays selected and those follow-up reads remain in history. Readback failure does not rerun the write or erase its response. Project/connection/PLC changes clear stale target selections. Explicit compilation is available through `compile_plc`; saving and PLC upload/download remain permanently outside MCP, and other online operations are not exposed.

## Evidence

The native import matrix verifies creation and semantic replacement across its 28 cells. The lifecycle suite verifies compilation success/error/repair, whole-object deletion and populated-table XML export/import. The original user-reported writes and local integration checks remain separate evidence.

<a id="contract-clarification-verification--2026-09-22"></a>

The original dated record is preserved in [write integration verification](../reference/history/write-integration-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
