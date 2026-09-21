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

Safety, protected, system-generated, inconsistent, or otherwise non-exportable objects must still appear in inventory results. A protected object remains part of the readable project model: every native representation and relationship that TIA ordinarily exposes may be returned, while content that TIA does not expose remains unknown. Limitations must be explicit.

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

### 5.1 User-started local HTTP runtime

- Version one is one user-started, long-running Windows dashboard/MCP process. The user starts the executable before an AI client initiates MCP communication or tool calls.
- The same process serves the dashboard, the existing REST/debugging surface, and the Streamable HTTP MCP endpoint at the configured loopback URL, normally `http://127.0.0.1:5000/mcp`.
- Streamable HTTP on loopback is the only version-one MCP transport. The MCP endpoint and dashboard must not bind to non-loopback interfaces.
- Multiple compatible MCP clients may connect to the process. They share its TIA attachment and active-project state; version one does not create per-client TIA attachments, per-client active projects, or persistent per-client project sessions.
- Automatic client launch, a launcher, Windows-service or background-service operation, process supervision, and replacement client-launched transports are outside version one.
- Documentation and responses must remain client-neutral. A specific client may be shown as an example but is not a server dependency.

### 5.2 At most one active project

One running MCP server process has zero or one active project at a time, shared by every connected client. A TIA project may contain multiple PLCs; this does not violate the one-project rule.

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
- Know-how passwords and object unlocking likewise remain exclusively in the visible TIA Portal UI. The MCP must not accept a password, unlock an object, decrypt content, or invoke a protection-bypass workflow.
- Tool calls containing arguments not declared by that tool's `additionalProperties: false` input schema must be rejected at runtime. Undeclared fields beside the standard `name`, `arguments`, and protocol `_meta` tool-call parameters are rejected as well. A password, credential, username, or unlock value therefore fails as an invalid request rather than being ignored.

### 5.4 Detach and shutdown

Connecting or disconnecting an individual MCP client must not create or dispose the shared TIA attachment. When the server explicitly disconnects from TIA or the process shuts down, it must detach the Openness client without saving or closing the active project and without closing TIA Portal, regardless of whether the project was initially attached or opened by the MCP.

Every Siemens engineering-object access must run through `StaTaskScheduler`.

## 6. Discovery, hierarchy, and identity

### 6.1 PLC discovery

The MCP must enumerate every PLC in the active project. Each downstream discovery or read call must identify its target PLC.

Non-PLC devices may be mentioned as top-level project metadata, but inspecting their internals is outside version one.

The canonical version-one tool surface is:

| Tool | Responsibility |
|---|---|
| `connect_to_tia_portal` | Attach to the selected open project or visibly open a supplied compatible path when none is active |
| `get_status` | Report connection, active project, native installed TIA product versions/options, REST access-profile status, and invariant MCP write unavailability |
| `list_devices` | Discover PLC-capable devices; non-PLC entries are navigation metadata only |
| `list_plc_objects` | Return the included PLC software as a hierarchy |
| `find_plc_objects` | Search live object metadata without an index |
| `read_plc_object` | Return one native representation using strict or `best` negotiation |
| `get_tag_table_entries` | Return the direct convenience view of tags and constants |
| `get_cross_references` | Return native on-demand TIA cross-references |

### 6.2 Hierarchical inventory

The canonical PLC inventory must preserve TIA's actual group and folder hierarchy. It must not reduce the project to an unexplained flat list.

Each returned object must include, when available:

- Siemens `object_id`.
- Parent identifier or hierarchy path.
- PLC identifier and name.
- Object name, type, and canonical path.
- Block number and programming language where applicable.
- Protection, safety, system-generated, consistency, and content-availability metadata where TIA exposes them.

Protection, consistency, and content-availability metadata are three-state facts: true, false, or unknown. A missing or `null` fact means TIA did not expose it or that no object-level read has established it; it must not be interpreted as false. `isProtected: true` does not by itself prove that every native representation is unavailable.

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

The actual result of the native export attempt is authoritative for capability detection. Native installed-product diagnostics, project version, language, and object metadata are context; they must not be used as a hard rule that suppresses an otherwise valid object-level attempt.

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

Mixed-language, GRAPH, STL, FBD, safety, system-generated, inconsistent, and know-how-protected objects follow the same object-level native-attempt policy. Protection must not cause an object to disappear or be rejected before an ordinary native read is attempted. The MCP attempts only representations that TIA exposes, never decrypts, bypasses, or reconstructs protected content, and reports every demonstrated limitation. Failure for one object or representation must not be generalized to the project or another representation.

### 7.5 Successful content

A successful representation is complete only within that native representation's contract. `complete: true` together with `completeness: "complete"` means every required artifact for the selected format was returned and passed the checks below. It does not assert semantic understanding, behavioral correctness, compilation consistency, equivalence to another representation, availability of every format, or disclosure of protected implementation logic.

Every successful representation also includes `contentScope`:

- `full-native-representation` means TIA reported the object as unprotected and the selected native representation passed its completeness checks.
- `tia-exposed-protected-view` means TIA reported know-how protection and returned a valid native representation; hidden implementation content remains unknown.
- `native-representation-protection-unknown` means the representation passed its checks but TIA did not expose a reliable protection fact.

A partial, empty, or structurally invalid native result is never returned as a successful canonical representation.

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

- Attempted native representation format.
- Result: `succeeded`, `failed`, `partial`, `unsupported`, or `not_applicable`.
- Concise reason.
- A structured `errorCode` when the attempt did not succeed.
- Native Siemens messages when available.

The selected representation and the reason for every preceding fallback must therefore be visible to the calling agent. A protection-related failure is recorded as `protected-content`; under `best` it does not suppress a later ordinary native attempt. A strict explicit format never falls back.

### 7.7 Protected-object reads

Object know-how protection is distinct from protected-project authentication and Siemens external-access approval.

The canonical reader uses only the normal native TIA operations in the representation matrix. Continuing from a protection-related failure to the next planned representation under `best` is native fallback, not circumvention. If TIA returns a representation that passes the format-specific checks, the MCP returns it together with:

- `protection.state: "protected"` and `isProtected: true`.
- `protection.type: "know-how"`.
- `protection.access: "native-limited"` and a concise content limitation.
- `representation.contentScope: "tia-exposed-protected-view"`.

Canonical content and cross-reference responses always carry a `protection` object. `state` and nullable `isProtected` must agree. A known unprotected object uses `state: "unprotected"`, `isProtected: false`, and `access: "native-full"`; this means no protection-derived restriction is known, not that every representation must succeed. A known protected object uses `access: "native-limited"`; this means ordinary TIA reads may be limited, not that a readable view is guaranteed. When TIA does not expose the fact, the response uses `state: "unknown"` and `access: "unknown"`, and `isProtected` is `null` or omitted rather than guessed. `access` is a protection classification established independently of the requested representation's success or failure.

The calling agent may interpret the exposed interface, metadata, native document, and cross-references, but must treat implementation content that TIA did not expose as unknown. It must not infer, recreate, or present hidden logic as project evidence.

An applicable strict format that TIA identifies as blocked by protection returns `protected-content`. If every applicable `best` attempt is blocked by protection, the terminal error is also `protected-content` and retains the complete attempt trail. If protected-object metadata exists but TIA reports a different or unspecified native failure, the response preserves both the known protection metadata and the demonstrated failure cause rather than inventing a bypass or hidden source.

The MCP never accepts a know-how password or unlock directive. A user may unlock or authenticate only in the visible TIA Portal UI; a later canonical read then queries the live object again without caching.

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
- Repeat the target object's known protection state and limitation. Cross-references that TIA exposes for a protected object may be returned, but they do not prove visibility of hidden implementation logic.

The calling agent may traverse multiple cross-reference responses to construct a larger call hierarchy. The MCP must not compile the PLC or parse exported source as a hidden fallback to refresh or manufacture cross-references.

## 10. Standard response envelope

Every successful live project-backed response must include a compact provenance envelope containing the fields applicable to that response:

- Read timestamp in UTC.
- Active project name, path, project version, and modified state.
- Native installed TIA Portal/STEP 7 product names, versions, genuinely populated product codes, and nested options reported by TIA.
- Target PLC name and identifier.
- Target object identifier, path, name, type, and language where applicable.
- Requested and actual representation.
- Completeness and authority classification.
- Protection classification for canonical content and cross-reference reads, plus content-scope classification for returned representations.
- Fallback attempts where representation negotiation occurs.

Disconnected status responses and errors include only the provenance known at the time. PLC fields are required only for PLC-backed results, object fields only for object-backed results, representation fields only for content reads, and fallback attempts only when negotiation occurs.

Whenever status, success, or error provenance contains a non-null V1 project identity, its serialized `project` object must contain a `version` key of type `string | null`:

- Read the value only from TIA Openness V20 `Project.Version`.
- Preserve a nonblank native value unchanged.
- Normalize a null, empty, or whitespace-only native value to JSON `null`; do not omit the key.
- Do not infer a value from the `.ap20` extension, installed TIA product, assembly version, registry, project path, or filesystem metadata.

Native project version and installed-product version are separate facts and must be reported separately. Nullable `tia.portalVersion` comes from native installed-software diagnostics and is not a synonym or fallback for `project.version`. The mapping must recognize Siemens' native installed-product name `Totally Integrated Automation Portal` as the portal product and preserve that product record's native version. It must not derive the value from DLLs, filenames, registry data, the project extension, or another product record.

The recursively mapped `tia.installedProducts` tree may contain native product names, native versions, genuinely populated native product codes, and nested installed options. It must not contain an `update` field or infer update, patch, service-pack, or build values from DLLs, paths, the registry, filenames, or free text.

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
      "version": null,
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
  "protection": {
    "state": "protected",
    "isProtected": true,
    "type": "know-how",
    "access": "native-limited",
    "contentLimitation": "TIA may expose only a protected native view; hidden implementation content remains unknown."
  },
  "representation": {
    "format": "simaticml",
    "authority": "native-export",
    "completeness": "complete",
    "complete": true,
    "contentScope": "tia-exposed-protected-view",
    "documents": [
      {
        "fileName": "object.xml",
        "role": "simaticml",
        "mediaType": "application/xml",
        "content": "<Document><SW.Blocks.FB><AttributeList><Name>FB_Motor</Name></AttributeList></SW.Blocks.FB></Document>",
        "byteLength": 102,
        "checksum": {
          "algorithm": "sha-256",
          "encoding": "utf-8-no-bom",
          "value": "3cf2001327cedc1293a79201858b8e8d7e8b1eb415997ef8ba6bb473639ccd6c",
          "byteLength": 102
        }
      }
    ]
  },
  "attempts": [
    {
      "format": "simatic-sd",
      "result": "failed",
      "reason": "TIA Portal reported protected or inaccessible native content.",
      "errorCode": "protected-content",
      "nativeMessages": []
    },
    {
      "format": "simaticml",
      "result": "succeeded",
      "reason": "TIA Portal returned one complete native SimaticML document.",
      "nativeMessages": []
    }
  ]
}
```

This is a structural example, not a requirement to use these exact C# type names. It deliberately shows that `project.version` remains present as JSON `null` while `tia.portalVersion` remains a separate installed-product fact.

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

`protected-content` means an object representation was identified as inaccessible because of content protection. `ui-authentication-required` means Siemens external-access approval or interactive project authentication is required in the visible TIA UI. They must not be substituted for one another. A generic Siemens security exception without cause-specific native message data proves neither condition and remains an unspecified native failure.

Canonical content-read and cross-reference errors repeat the known protection state and limitation after a target object has been resolved. A `best` error retains every attempt's structured error code and native message so a later generic failure cannot erase an earlier protection-specific result.

Inventory visibility, export success, semantic understanding, and runtime behavior are separate claims. A successful export proves only that the reported native representation was retrieved from the stated live project state.

## 13. Safety and access profiles

### 13.1 Supported version-one surface

The complete version-one MCP surface is read-only and consists of exactly these eight advertised and callable tools in every access profile:

- `connect_to_tia_portal`
- `get_status`
- `list_devices`
- `list_plc_objects`
- `find_plc_objects`
- `read_plc_object`
- `get_tag_table_entries`
- `get_cross_references`

`tools/list` must return exactly this set regardless of `TIA_MCP_ACCESS`. MCP dispatch must always reject every other tool name; removing a name from discovery while continuing to accept direct calls is not compliant. Each of the eight input schemas sets `additionalProperties: false`, and runtime validation must enforce the declared required fields and selector precedence/uniqueness rules.

The surface may attach to or visibly open a compatible project, but it must not:

- Save or close a project.
- Compile PLC software.
- Import, create, overwrite, delete, or rename project objects.
- Clone or reconstruct a project.
- Perform online operations.

### 13.2 Non-MCP project-changing implementations

Existing mutating implementations may remain only as internal services or separately gated REST endpoints behind the explicit `TIA_MCP_ACCESS=full` profile. They must not be registered, advertised, or accepted as MCP tools or dashboard controls. They are experimental, unsupported by this version-one specification, and outside version-one acceptance.

Reusable internal discovery and export services may remain where the eight canonical tools depend on them. Their presence does not create additional MCP calls.

Enabling `full` changes REST endpoint availability only. It never changes MCP discovery or dispatch, never adds dashboard write controls, and is never authorization for an agent to change a project. MCP `get_status.accessProfile` may report `full`, but `writeToolsAvailable` must remain `false`. The legacy project-clone REST route remains quarantined and unsupported.

No project-changing operation may be used during version-one validation.

## 14. Dashboard role

The dashboard must be limited to what supports operation of the MCP bridge:

- The one user-started local server, its dashboard URL, and its configured loopback Streamable HTTP `/mcp` URL.
- TIA connection and project-path selection.
- Active project, PLC, nullable native project version, native installed product versions/options, and access-profile status.
- Siemens external-access and authentication guidance.
- MCP call logs, timing, fallback attempts, and errors.
- Manual invocation of read tools for debugging.

The dashboard must render a null or blank native project version as an honest unavailable marker while the API preserves `project.version` as JSON `null`. It must not expose an installed-product Update column or infer update, patch, service-pack, or build values. Multiple clients observe the same running process and TIA attachment; the dashboard must not introduce persistent client/session tracking or per-client project state.

Behavioral analysis, semantic diagrams, documentation authoring, and a separate persistent project model belong to the calling agent, not the dashboard.

## 15. Acceptance criteria

Version one is complete only when all of the following are satisfied.

### 15.1 Contract and implementation

- The canonical discovery, search, object-read, tag/constant, and cross-reference capabilities satisfy this specification.
- The user starts one long-running local server before compatible clients connect; the process serves the dashboard, REST/debugging routes, and the sole loopback Streamable HTTP MCP endpoint.
- Multiple clients share the process and its zero-or-one active project; no per-client TIA attachment or active-project model is introduced.
- `tools/list` advertises exactly the eight canonical tools in section 13.1 in every access profile, with their approved descriptions, required fields, selector precedence/uniqueness rules, and `additionalProperties: false` schemas.
- Runtime dispatch accepts those eight tools and rejects every other tool name in every access profile, including direct calls to names absent from `tools/list`.
- `get_status.writeToolsAvailable` is `false` in every profile; `accessProfile: "full"` may describe separately gated REST availability but never additional MCP capability or dashboard write controls.
- The dashboard shows the HTTP endpoint and shared interaction status without claiming another MCP transport.
- Every serialized non-null V1 project identity contains `version` as either the preserved nonblank native string or JSON `null`.
- Nullable installed TIA portal version and nullable project version remain separate facts. The installed-product tree has no `update` field and no update, patch, service-pack, build, or project-version value is inferred.
- The application remains a .NET Framework 4.8, x64 WinForms executable targeting the TIA Portal V20 Public API.
- All TIA Openness work runs on `StaTaskScheduler`.
- The Siemens-free .NET 8 offline console harness passes with the repository's documented `dotnet run` command.
- The Release build succeeds with the repository's documented build command.

### 15.2 Read behavior

- Every PLC and included content-bearing object is discoverable with hierarchy and identity; tags and constants are discoverable through their tag-table convenience view and metadata search.
- Multiple PLCs in one project can be targeted explicitly.
- `best` and every strict format behave according to the representation matrix.
- Partial SIMATIC SD is rejected and recorded before fallback.
- Tag tables return native SimaticML through `read_plc_object`.
- The tag convenience view includes tags, user constants, and system constants.
- Native cross-references expose calls and other reference types on demand.
- Every applicable live response includes provenance, and every returned source/document includes an exact-content checksum.
- Every successful representation has `complete: true`, `completeness: "complete"`, and an explicit `contentScope`; incomplete output is never returned as success.
- Protected and unsupported objects remain visible. A protected-object read either returns a validated native representation with protection and scope explicit, or fails with a structured, cause-accurate error and the complete attempt trail.
- Unsupported objects and formats return structured `unsupported-object` or `unsupported-format` evidence; they are never simulated through another representation.
- Protected content is never silently omitted, decrypted, reconstructed, or presented as unrestricted implementation source.

### 15.3 Lifecycle and safety

- The user-started process is available before a client initiates MCP requests; automatic client launch and process supervision are not required or supported.
- The MCP attaches to an open project or visibly opens a supplied compatible path when none is active.
- Multiple candidates cause explicit selection rather than first-match attachment.
- Multiple client connections share the server's active project, and disconnecting one client does not detach the server from TIA.
- Projects requiring upgrade are refused.
- Protected-project credentials remain in the TIA UI.
- Reads reflect unsaved in-memory edits.
- No version-one read compiles, saves, closes, imports, writes, clones, or goes online.
- MCP shutdown leaves TIA Portal and the project open.
- HTTP is unreachable through non-loopback interfaces.

### 15.4 Final representative-project validation

Final live acceptance is a separate last step after implementation. It must reuse the newly built executable already started by the user and call its exact configured loopback `/mcp` endpoint. The acceptance runner must not start or stop another server instance. No alternative transport is tested. It requires a disposable TIA Portal V20 demo project containing at least:

- LAD and SCL blocks.
- DB variants needed to exercise the reader.
- A UDT/PLC type.
- A tag table with tags, a user constant, and an observable system constant where TIA permits it.
- Direct block calls and tag/DB usages suitable for cross-reference validation.
- At least one unsupported or incomplete-read case to validate explicit errors and fallback reporting.
- At least one protected object for which a blocked strict representation returns `protected-content` and `best` continues to another native representation when TIA exposes one.

The HTTP acceptance sequence is:

1. Call `initialize`.
2. Call `tools/list`; verify that exactly the eight tools in section 13.1 are present and that every description, required field, selector rule, and `additionalProperties: false` schema matches the contract.
3. Call `connect_to_tia_portal` with the exact demo-project path. When that project is already active, require the safe `reuse` action.
4. Call `get_status`; require the `project.version` key with JSON `null` when native `Project.Version` has no declared value, require `tia.portalVersion` to equal the native `V20` reported for `Totally Integrated Automation Portal`, and verify recursively that no installed product or option contains an `update` key.
5. Call `list_devices`; verify every expected PLC and its canonical PLC identity.
6. Call `list_plc_objects` for both PLCs; verify hierarchy, the mixed-language block, the protected DB, UDTs, tag tables, and all protection/content metadata that TIA exposes.
7. Call `find_plc_objects` with meaningful name/query, type, language, and group filters.
8. Call `read_plc_object` for pure SCL, pure LAD/SIMATIC SD, the compiled mixed-language block and its actual fallback trail, the protected DB with both `best` fallback and a blocked strict format, and applicable UDT/DB cases.
9. Call `get_tag_table_entries`; verify tags, user constants, and system constants wherever TIA exposes them.
10. Call `get_cross_references`; verify actual `uses` and `usedBy` evidence for block calls and DB/tag usage.
11. Verify structured failures for invalid, unknown, and password arguments; object not found; and unsupported strict format.
12. Call final `get_status` and compare the project files with the pre-read baseline.

For every response containing an active project, the validation record must retain the actual JSON evidence that `project.version` is present, including `"version": null` when TIA reports no nonblank native value. It must record nullable `tia.portalVersion` separately, native installed product names/versions/options, and verify that no installed product or nested option contains an `update` key. It must state the exact endpoint and binary used, tools called, representations returned, checksums, dashboard observations, and all observed limitations. The dashboard call log must record the canonical tool calls. The record must also confirm loopback-only reachability and verify through final status and filesystem comparison that the demo project was not compiled, saved, closed, or otherwise changed by the read workflow. Version one must not be marked complete until all eight canonical tools have been tested successfully or every remaining limitation is explicitly documented.

Creating this demo project is not part of implementing the MCP read features; it is a separate follow-up task.
