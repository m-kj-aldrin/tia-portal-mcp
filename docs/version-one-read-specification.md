# TIA Portal MCP bridge — version one read specification

**Status:** Approved product scope and implementation target

**Target:** TIA Portal V20, TIA Portal Openness V20, .NET Framework 4.8, x64

**Last updated:** 2026-08-16

**Final live acceptance:** Pending a separate representative demo-project task

## 1. Authority and precedence

This document is the normative specification for version one of the TIA Portal MCP bridge. Existing guides describe the server as it is currently implemented. When an existing guide or tool differs from this document, this document defines the intended version-one behavior.

The words **must**, **must not**, **should**, and **may** are normative. A capability is not complete merely because a related legacy tool already exists.

## 2. Product goal

Version one is an AI/MCP bridge for inspecting and understanding the offline PLC software in a TIA Portal project.

The bridge exposes the live TIA project structure, native Siemens representations, and native TIA relationships. A calling agent can use that evidence to explain and document existing program behavior.

The MCP server itself does not explain program behavior or own a semantic model of the project. TIA Portal remains the source of truth.

The dashboard is an auxiliary interface for setup, connection status, diagnostics, and manually testing read calls. It is not a second analysis or documentation product.

The long-term direction is to expose the whole TIA project and later support controlled writes so a human and an AI agent can stay synchronized with TIA. Those capabilities do not broaden version one's PLC-software read scope.

## 3. Architectural boundary

### 3.1 MCP responsibilities

The MCP server owns:

- TIA Portal connection and active-project lifecycle.
- Live discovery of PLCs and PLC software objects.
- Native TIA Openness reads and exports.
- Representation negotiation and explicit fallback reporting.
- Temporary export-file handling and cleanup.
- Native cross-reference retrieval.
- Response provenance, checksums, and structured errors.

### 3.2 Calling-agent responsibilities

The calling agent owns:

- Behavioral interpretation and explanation.
- Documentation generation.
- Call-graph assembly across multiple queries.
- Semantic models, diagrams, summaries, and comparisons.
- Any persistence of derived knowledge outside the MCP server.

### 3.3 Prohibited MCP-owned derivations

Version one must not:

- Construct or persist a semantic model of the TIA project.
- Maintain a source index, call graph, or cached project mirror.
- Convert SimaticML into SIMATIC SD, SCL, LAD, or another language.
- Recreate the compact XML format exported by the TIA tag-table UI.
- Treat a parser-generated explanation or translation as project source.

Flattening a native TIA result into transport-friendly JSON is allowed when the response remains traceable to the native objects and does not add semantic conclusions.

## 4. Version-one scope

### 4.1 Included

Version one covers every PLC in the single active project and the following offline PLC software:

- Program blocks: OB, FB, FC, and DB, including applicable global, instance, and array DB variants.
- PLC data types and UDTs.
- PLC tag tables and their tags.
- PLC user constants and system constants.
- TIA block/type/tag group and folder hierarchy.
- Native TIA cross-references for the included objects.
- Live metadata search over included PLC objects.

Safety, protected, system-generated, inconsistent, or otherwise non-exportable objects must still appear in inventory results. Their content limitations must be explicit.

### 4.2 Excluded

Version one excludes:

- Online PLC connections, live values, monitoring, forcing, downloads, and runtime diagnostics.
- Compilation or any automatic attempt to refresh project data by compiling.
- Hardware configuration internals, HMI software, drives, and the rest of the TIA project outside PLC software.
- Watch and force tables, trace configurations, external source artifacts, technology objects, and similar engineering artifacts.
- Full-text search inside exported source.
- Writes, imports, creates, deletes, renames, saves, and project upgrades as supported version-one behavior.

Temporary use of a TIA export API, including temporary source files required by Siemens, does not make the corresponding editor artifact part of the public scope.

## 5. Runtime and active-project lifecycle

### 5.1 Local-only operation

- The HTTP MCP endpoint and dashboard must bind only to loopback interfaces.
- Stdio remains a supported local transport.
- HTTP and stdio must expose the same tool definitions, validation, dispatch behavior, and response contracts.
- Documentation and responses must remain client-neutral.

### 5.2 At most one active project

One MCP server instance has zero or one active project at a time. A TIA project may contain multiple PLCs; this does not violate the one-project rule.

When connecting:

1. If a project is active and no path is supplied, the MCP reuses it.
2. If a path is supplied and it matches the active project after canonical path normalization, the MCP reuses it.
3. If a different project is active, the MCP returns a project-conflict error. It must not detach, close, or switch projects implicitly.
4. With no active project, a supplied path is authoritative: the MCP first attaches to an exact open-project match; if none exists, it visibly opens that path. It must not attach to an unrelated open project.
5. With no active project and no supplied path, the MCP attaches only when exactly one suitable project is open.
6. If multiple open projects or TIA processes remain candidates, the MCP returns an ambiguity error with selectable candidates. It must not silently choose the first candidate.
7. If no project is open and no path is supplied, the MCP returns a structured error.
8. An ordinary read call must never switch projects implicitly.

### 5.3 Opening a project

When the MCP opens a project itself:

- TIA Portal must run with a visible user interface. Headless opening is not version-one behavior.
- The project must already be compatible with the installed TIA Portal V20 environment.
- A project requiring upgrade must be refused. `OpenWithUpgrade` or an equivalent upgrade workflow must not be used.
- Protected-project authentication occurs only in the visible TIA Portal UI.
- MCP tools must never accept or store TIA usernames, passwords, or other project credentials.

### 5.4 Detach and shutdown

Disconnecting or shutting down the MCP must detach the Openness client without saving or closing the active project and without closing TIA Portal, regardless of whether the project was initially attached or opened by the MCP.

Every Siemens engineering-object access must run through `StaTaskScheduler`.

## 6. Discovery, hierarchy, and identity

### 6.1 PLC discovery

The MCP must enumerate every PLC in the active project. Each downstream discovery or read call must identify its target PLC.

Non-PLC devices may be mentioned as top-level project metadata, but inspecting their internals is outside version one.

The canonical version-one tool surface is:

| Tool | Responsibility |
|---|---|
| `connect_to_tia_portal` | Attach to the selected open project or visibly open a supplied compatible path when none is active |
| `get_status` | Report connection, active project, installed TIA products/updates, and read-only profile status |
| `list_devices` | Discover PLC-capable devices; non-PLC entries are navigation metadata only |
| `list_plc_objects` | Return the included PLC software as a hierarchy |
| `find_plc_objects` | Search live object metadata without an index |
| `read_plc_object` | Return one native representation using strict or `best` negotiation |
| `get_tag_table_entries` | Return the direct convenience view of tags and constants |
| `get_cross_references` | Return native on-demand TIA cross-references |

Narrower legacy list/read tools may remain as described in section 15.

### 6.2 Hierarchical inventory

The canonical PLC inventory must preserve TIA's actual group and folder hierarchy. It must not reduce the project to an unexplained flat list.

Each returned object must include, when available:

- Siemens `object_id`.
- Parent identifier or hierarchy path.
- PLC identifier and name.
- Object name, type, and canonical path.
- Block number and programming language where applicable.
- Protection, safety, system-generated, consistency, and content-availability metadata where TIA exposes them.

Objects must not be omitted merely because a later content export may fail.

User and system groups must both be traversed where TIA exposes them. Failure to read one optional property or one protected object must be isolated to that inventory entry; it must not abort the remaining inventory.

Blocks, UDTs, and tag tables are content-bearing inventory objects. Tags and constants are entries within a tag table: they are discoverable through `get_tag_table_entries` and live metadata search, but version one does not require every entry to be expanded into the default project-tree response or accepted individually by `read_plc_object`.

### 6.3 Object selectors

The preferred selector for reads and cross-references is the Siemens cross-session `object_id` returned by discovery.

Resolving an `object_id` must also verify that the resolved object belongs to the requested PLC and is an allowed version-one object kind.

PLC, path, object type, and name remain convenience selectors. A convenience selector must resolve to exactly one object. If it resolves to zero or multiple objects, the MCP returns a not-found or ambiguity error and never selects the first recursive match.

An object identifier belongs to its project. A client must not assume that an identifier is meaningful in a different project.

### 6.4 Live metadata search

Version one includes a `find_plc_objects` capability that searches live TIA metadata:

- Object name.
- Group or object path.
- Object type.
- Siemens object identifier.
- Tag or constant entry name/type together with its parent tag table.

Search may support filters for PLC, type, language, and group. It must query the live project and must not depend on a persistent index. Searching inside source content is excluded.

## 7. Canonical object reader

### 7.1 Tool contract

`read_plc_object` is the canonical version-one content reader.

Its request identifies:

- The target PLC.
- The target object, preferably by `object_id`.
- An optional `format`.

Supported format values are:

- `best`
- `simatic-sd`
- `scl-source`
- `simaticml`

If `format` is omitted, it defaults to `best`.

### 7.2 `best` negotiation

`best` performs an ordered, object-level attempt:

1. Native SIMATIC SD.
2. Native raw SCL source, only when the object is an applicable pure SCL block.
3. Native SimaticML.
4. Structured failure if none succeeds.

The actual result of the native export attempt is authoritative for capability detection. Installed TIA version, update level, project version, language, and object metadata are diagnostic context; they must not be used as a hard rule that suppresses an otherwise valid object-level attempt.

For an object category that has no native API for a representation, the attempt is recorded as `unsupported` rather than simulated through a converter.

### 7.3 Strict explicit formats

An explicit format request is strict:

- `simatic-sd` returns complete native SIMATIC SD or an explicit error.
- `scl-source` returns complete native raw SCL or an explicit error.
- `simaticml` returns complete native SimaticML or an explicit error.

An explicit request never silently falls back to another representation.

### 7.4 Object/format matrix

| Object | Native attempt order for `best` |
|---|---|
| OB, FB, or FC | SIMATIC SD → raw SCL when the block is pure SCL → SimaticML |
| DB | SIMATIC SD → SimaticML |
| UDT / PLC type | SIMATIC SD → SimaticML |
| PLC tag table | SimaticML |

Mixed-language, GRAPH, STL, FBD, protected, safety, system-generated, or inconsistent blocks follow the same policy: attempt only native representations that TIA exposes and report every limitation. The MCP must not infer that failure for one block makes the whole project unsupported.

### 7.5 Successful content

A representation is selected only when its native operation completes and its representation-specific completeness checks pass.

For SIMATIC SD:

- A complete `.s7dcl` source document is required.
- Available `.s7res` resource documents are returned alongside it.
- A partial or incomplete `DocumentExportResult` is failure, even if Siemens created one or more files.
- Siemens export messages are retained in the attempt record.

For raw SCL, `GenerateSource` must complete successfully and produce one nonempty source file for the requested pure SCL block. That complete file is returned unchanged.

For SimaticML, the native `Export` operation must complete successfully and produce one nonempty, well-formed XML document containing the requested engineering object. That complete native XML export is returned unchanged. The MCP must not transform it into another source format.

The canonical version-one SimaticML representation uses `DocumentInfoOptions.None` so volatile document timestamps or installed-product blocks do not change the content checksum when the PLC object itself is unchanged. This remains a native Siemens export: optional `DocumentInfo` is deliberately excluded from the representation contract, while installed-product provenance is returned separately in the standard response envelope.

`best` returns only the selected successful representation's content. It does not return every available representation in one response.

### 7.6 Fallback trail

Every `best` response includes an ordered `attempts` collection. Each entry contains:

- Requested format.
- Result: `succeeded`, `failed`, `partial`, `unsupported`, or `not_applicable`.
- Concise reason.
- Native Siemens messages when available.

The selected representation and the reason for every preceding fallback must therefore be visible to the calling agent.

## 8. Tag tables, tags, and constants

### 8.1 Native representation

The authoritative native export for a PLC tag table is SimaticML produced through TIA Openness.

For a tag table:

- `best` returns SimaticML.
- `simaticml` returns SimaticML.
- `simatic-sd` returns an unsupported-format error.
- `scl-source` returns an unsupported-format error.

### 8.2 Convenience view

`get_tag_table_entries` is the compact version-one convenience view. It reads `PlcTag`, user-constant, and system-constant objects directly through the TIA Openness object model; it does not read or parse SimaticML.

The convenience response must:

- Identify one target PLC and one tag table, preferably using the table's `object_id`.
- Group its result into separate `tags`, `userConstants`, and `systemConstants` collections.
- Include tags, PLC user constants, and system constants.
- Include each entry's native identifier when TIA supports one, name, datatype, value/address, access properties, and selected-language comment where applicable and available.
- Identify itself as a direct, selected-field Openness view rather than a complete export.
- Use the standard provenance envelope.
- Use the active-language rule for projected comments.

### 8.3 Formats that must remain distinct

The following are different formats and must never be mislabeled as each other:

- Openness SimaticML, whose PLC tag-table structure includes `Document` and `SW.Tags.PlcTagTable` elements.
- The compact TIA UI tag-table XML rooted at `Tagtable`.
- The TIA UI/Excel `.xlsx` tag-table exchange format.

Version one does not expose or recreate the compact UI XML format. A native Openness tag-table export uses XML/SimaticML semantics and an `.xml` filename.

## 9. Native cross-references

Version one includes an on-demand `get_cross_references` capability based on TIA Portal's native `CrossReferenceService`.

The tool must:

- Prefer a Siemens `object_id` selector.
- Return all native reference types available for the selected object, not only block calls.
- Include calls, conditional or unconditional calls, reads, writes, instance relationships, type usage, and other native access/reference categories when present.
- Return both `uses` and `used by` directions when TIA provides them.
- Preserve native source object, referenced object, location, path, address, reference type, access type, and referenced-as information when available.
- Query TIA on demand and avoid storing a call graph.

The calling agent may traverse multiple cross-reference responses to construct a larger call hierarchy. The MCP must not compile the PLC or parse exported source as a hidden fallback to refresh or manufacture cross-references.

## 10. Standard response envelope

Every successful live project-backed response must include a compact provenance envelope containing the fields applicable to that response:

- Read timestamp in UTC.
- Active project name, path, project version, and modified state.
- Installed TIA Portal/STEP 7 product version and update information reported by TIA.
- Target PLC name and identifier.
- Target object identifier, path, name, type, and language where applicable.
- Requested and actual representation.
- Completeness and authority classification.
- Fallback attempts where representation negotiation occurs.

Disconnected status responses and errors include only the provenance known at the time. PLC fields are required only for PLC-backed results, object fields only for object-backed results, representation fields only for content reads, and fallback attempts only when negotiation occurs.

Native project version and installed-product version are separate facts and must be reported separately.

- Project version must come from TIA's native project version property (`Project.Version` in V20), not a guessed dynamic attribute.
- Installed TIA Portal, STEP 7, option, and update information must come from the attached TIA process/session diagnostics (`TiaPortalProcess.InstalledSoftware` in V20), not solely from the referenced DLL file version.

A representative shape is:

```json
{
  "provenance": {
    "readAtUtc": "2026-08-16T12:00:00Z",
    "tia": {
      "portalVersion": "V20",
      "installedProducts": []
    },
    "project": {
      "name": "Example",
      "path": "C:\\Projects\\Example.ap20",
      "version": "V20",
      "isModified": true
    },
    "plc": {
      "objectId": "...",
      "name": "PLC_1"
    },
    "object": {
      "objectId": "...",
      "path": "Program blocks/Control/FB_Motor",
      "name": "FB_Motor",
      "type": "FB",
      "language": "LAD"
    }
  },
  "request": {
    "format": "best"
  },
  "representation": {
    "format": "simatic-sd",
    "authority": "native-export",
    "complete": true,
    "documents": []
  },
  "attempts": []
}
```

This is a structural example, not a requirement to use these exact C# type names.

## 11. Checksums, freshness, and language

### 11.1 Content checksums

Every returned source or document must include a SHA-256 checksum reproducible from the response. The checksum is calculated over the UTF-8, no-BOM encoding of the exact `content` string, preserving its line endings and without other normalization. The response reports this checksum encoding explicitly.

A multi-document SIMATIC SD bundle includes a checksum for each document and a deterministic aggregate checksum. The aggregate scheme is `simatic-sd-bundle-v1`: sort documents by ordinal filename, then append for each document a 4-byte unsigned big-endian filename length, the UTF-8 filename, an 8-byte unsigned big-endian content length, and the UTF-8 content before applying SHA-256.

The checksum is an equality/change signal only. It is not a semantic or behavioral hash, and the MCP does not retain previous values.

### 11.2 Live reads and no caching

- Every inventory, search, read, and cross-reference call queries the live in-memory TIA project.
- Unsaved edits visible in TIA Portal are part of the source state.
- PLC object content must not be cached between calls.
- Separate calls are not an atomic project snapshot; timestamps and checksums allow the client to detect intervening changes.
- Temporary export files may exist only for the duration needed to serve a native export and must be cleaned up on a best-effort basis.

### 11.3 Language selection

For projected metadata and convenience JSON, version one returns the active project editing language when available. If no text exists for that language, it falls back to the first available text.

Native SIMATIC SD, raw SCL, and SimaticML files are returned unchanged even when they contain additional multilingual content.

## 12. Errors and evidence boundaries

Errors must be structured and must preserve the distinction between:

- Object not found.
- Ambiguous selector.
- Unsupported object or format.
- Protected or inaccessible content.
- Failed native export.
- Partial native export.
- Missing installed product or option.
- Authentication awaiting user action in TIA Portal.
- Unsupported project version or required upgrade.
- No active project.

An error must identify the affected PLC/object and include native Siemens messages when safe and available. The MCP must not return empty or partial content as if it were complete authoritative source.

Inventory visibility, export success, semantic understanding, and runtime behavior are separate claims. A successful export proves only that the reported native representation was retrieved from the stated live project state.

## 13. Safety and access profiles

### 13.1 Supported version-one surface

The supported/default version-one surface is read-only. It may attach to or visibly open a compatible project, but it must not:

- Save or close a project.
- Compile PLC software.
- Import, create, overwrite, delete, or rename project objects.
- Clone or reconstruct a project.
- Perform online operations.

### 13.2 Existing write implementations

Existing mutating implementations may remain in the codebase behind the explicit `TIA_MCP_ACCESS=full` profile. They are experimental, quarantined, unsupported by this version-one specification, and outside version-one acceptance.

Enabling `full` changes availability only. It is never authorization for an agent to change a project. `clone_project` remains quarantined and unsupported.

No project-changing tool may be called during version-one validation.

## 14. Dashboard role

The dashboard must be limited to what supports operation of the MCP bridge:

- Local server and transport setup.
- TIA connection and project-path selection.
- Active project, PLC, version/update, and access-profile status.
- Siemens external-access and authentication guidance.
- MCP call logs, timing, fallback attempts, and errors.
- Manual invocation of read tools for debugging.

Behavioral analysis, semantic diagrams, documentation authoring, and a separate persistent project model belong to the calling agent, not the dashboard.

## 15. Compatibility tools

The following existing readers may remain for client compatibility:

- `read_block`: existing compatibility SCL/raw-SimaticML behavior.
- `read_scl_source`: authoritative raw SCL for applicable pure SCL blocks.
- `read_lad_source`: authoritative SIMATIC SD for applicable pure LAD blocks.
- `list_blocks`, `list_tag_tables`, and `get_tags`: narrower discovery/convenience views.

Their existing contracts must not be silently redefined. They may delegate to shared internals where their documented behavior remains unchanged. The required grouped tag/constant view therefore uses `get_tag_table_entries`; the existing tag-only `get_tags` response may remain intact. `read_plc_object` is the canonical new reader for representation negotiation.

Legacy analyzers such as `analyze_scl` and `analyze_block` are not normative version-one evidence interfaces. If retained, they must be labeled as derived utilities and must never be presented as authoritative project behavior.

## 16. Acceptance criteria

Version one is complete only when all of the following are satisfied.

### 16.1 Contract and implementation

- The canonical discovery, search, object-read, tag/constant, and cross-reference capabilities satisfy this specification.
- HTTP and stdio expose aligned schemas and behavior through shared definitions and dispatch.
- The application remains a .NET Framework 4.8, x64 WinForms executable targeting the TIA Portal V20 Public API.
- All TIA Openness work runs on `StaTaskScheduler`.
- Existing compatibility readers retain their documented behavior.
- The Release build succeeds with the repository's documented build command.

### 16.2 Read behavior

- Every PLC and included content-bearing object is discoverable with hierarchy and identity; tags and constants are discoverable through their tag-table convenience view and metadata search.
- Multiple PLCs in one project can be targeted explicitly.
- `best` and every strict format behave according to the representation matrix.
- Partial SIMATIC SD is rejected and recorded before fallback.
- Tag tables return native SimaticML through `read_plc_object`.
- The tag convenience view includes tags, user constants, and system constants.
- Native cross-references expose calls and other reference types on demand.
- Every applicable live response includes provenance, and every returned source/document includes an exact-content checksum.
- Protected or unsupported objects remain visible and fail explicitly when read.

### 16.3 Lifecycle and safety

- The MCP attaches to an open project or visibly opens a supplied compatible path when none is active.
- Multiple candidates cause explicit selection rather than first-match attachment.
- Projects requiring upgrade are refused.
- Protected-project credentials remain in the TIA UI.
- Reads reflect unsaved in-memory edits.
- No version-one read compiles, saves, closes, imports, writes, clones, or goes online.
- MCP shutdown leaves TIA Portal and the project open.
- HTTP is unreachable through non-loopback interfaces.

### 16.4 Final representative-project validation

Final live acceptance is a separate last step after implementation. It requires a disposable TIA Portal V20 demo project containing at least:

- LAD and SCL blocks.
- DB variants needed to exercise the reader.
- A UDT/PLC type.
- A tag table with tags, a user constant, and an observable system constant where TIA permits it.
- Direct block calls and tag/DB usages suitable for cross-reference validation.
- At least one unsupported, protected, or incomplete-read case to validate explicit errors and fallback reporting.

The validation record must state the exact project version, installed TIA/STEP 7 version and update, tools called, representations returned, checksums, and all observed limitations. It must also verify that the demo project was not compiled, saved, closed, or otherwise changed by the read workflow.

Creating this demo project is not part of implementing the MCP read features; it is a separate follow-up task.
