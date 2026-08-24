# Project rehaul

## Purpose and status

This document records the initial constraints and settled decisions for the project rehaul. Nothing described here is implemented yet, and this is not a complete implementation plan.

The document is organized by tool so that each tool has one clear responsibility. Shared behavior is defined once and referenced by the tools that use it.

The source-format decisions currently assume TIA Portal V20 Update 0.

The rehaul follows a native-fidelity principle: MCP extensions may structure information that Openness does not expose as one ready-made response, but they preserve TIA identity, names, hierarchy and ordering whenever possible.

## Tool map

| Tool | Responsibility | Contract status |
|---|---|---|
| `list_tia_processes` | List running TIA Portal processes, their optional primary project paths and this MCP server's connection state without attaching | Initial contract settled |
| `connect_to_tia_portal` | Establish, reuse or switch the bridge's shared attachment to one exact running TIA Portal process | Initial contract settled |
| `open_tia_project` | Start a visible or headless TIA Portal instance, open one exact project and make it the shared attachment | Initial contract settled; headless lifecycle requires live validation |
| `disconnect_from_tia_portal` | Release the bridge's shared TIA Portal attachment without saving or closing an externally owned project | Initial contract settled; headless lifecycle requires live validation |
| `get_status` | Report bridge connection state, active-project provenance and installed TIA products | Initial contract settled |
| `list_devices` | Inventory the native device-group tree and its Device objects | Initial contract settled |
| `get_device` | Read one Device's metadata and nested DeviceItem hardware tree and expose PLC software scopes | Initial contract settled |
| `list_blocks` | Inventory the block-group tree and its blocks | Initial contract settled |
| `get_block` | Read one block's native metadata and optional authoritative source | Initial contract settled |
| `list_udts` | Inventory the type-group tree and its UDTs | Initial contract settled |
| `get_udt` | Read one UDT's native metadata and optional authoritative source | Initial contract settled |
| `list_tag_tables` | Inventory the tag-table group tree and its tag tables | Initial contract settled |
| `get_tag_table` | Read one tag table's native metadata and optional authoritative source | Initial contract settled |
| `get_cross_references` | Query native TIA cross-references for one supported engineering object | Initial contract settled |

Persistent PLC External Source objects are outside the initial inventory scope.

`find_plc_objects` is intentionally omitted. Blocks, UDTs, tag tables and devices are discovered through their type-specific inventory tools. A broad custom search over all engineering-object types has no identified workflow and would overlap those inventories.

### Initial surface and legacy boundary

The initial rehaul exposes exactly the tools in the table above. It has no aliases, compatibility tools or hidden legacy dispatch paths.

Before implementation begins, the current source project and its coupled offline tests will be moved by the repository owner into the root-level `reference/legacy-v1/` area. That copy is inert comparison material only:

- New source code must not compile, reference or dispatch into it.
- The new project must not depend on its contracts, helpers or response models.
- A legacy behavior is adopted only when it is deliberately implemented under the rehaul contract.
- The reference copy is not a runtime fallback and is never part of the MCP tool surface.

## Shared response behavior

`get_status` is the sole owner of full TIA Portal, installed-product and active-project context. Other tools do not repeat that provenance packet. They return their own requested scope and identifiers.

Every tool response includes:

```json
{
  "readAtUtc": "UTC timestamp for this live operation",
  "errors": []
}
```

`readAtUtc` belongs to the individual live result. Separate calls are not an atomic project snapshot.

The error rules are:

- A complete successful operation returns `errors: []`.
- If `best` succeeds through a later fallback, the response identifies the format actually returned and still returns `errors: []`. Failed intermediate attempts remain available in the dashboard log.
- A partial inventory returns every readable branch, sets `complete: false` and records its unreadable branches in `errors`.
- If every source attempt fails, metadata is preserved, `source` is `null` and every failed native attempt is recorded in `errors`.
- An error originating from TIA Openness preserves the native Siemens message without reinterpretation and uses `origin: "tia-openness"`.
- Input validation and other bridge-owned failures use `origin: "bridge"` and a minimal bridge message. They are never presented as Siemens errors.

The MCP transport may mark a tool call as failed when the requested operation cannot return its primary payload, but the structured error information still follows this common shape. There is no second `sourceUnavailable` error packet.

## Shared core behavior for `get_*` tools

This section applies to the detailed object readers `get_device`, `get_block`, `get_udt` and `get_tag_table`. Operational queries such as `get_status` and `get_cross_references` define their own response behavior.

Every detailed reader uses one Siemens `objectId` as its only selector. The object is resolved directly through:

```text
ObjectIdentifierProvider.Find(objectId)
```

Name and MCP-constructed path resolution are not part of the initial rehaul.

The detailed object readers use the same optional path behavior:

```text
includePath: true   -> resolve and return the current constructed path
includePath: false  -> skip parent traversal and return path: null
```

`includePath` is optional and defaults to `true`. When enabled, the tool walks from the resolved object through its parent objects and constructs the path using the same convention as the corresponding `list_*` tool. It does not enumerate sibling objects or rebuild the complete inventory.

Every detailed metadata packet has the stable field:

```json
{
  "path": "PLC_1/Program blocks/Folder/ObjectName"
}
```

The value is `string | null`. It is `null` when `includePath` is `false` or when the path cannot be resolved. Failure to resolve the path does not fail the object read and does not add a separate verbose path-error packet.

The path is MCP-constructed navigation information, not a native identifier. It must match the relevant `list_*` path exactly and is never an object selector; the Siemens `objectId` remains authoritative.

For source-owning readers, path-resolution cost should be benchmarked with source retrieval disabled so that export time does not hide the cost of parent traversal:

```text
includeSource: false, includePath: true
includeSource: false, includePath: false
```

### `typeSpecific` value conversion

Native `GetAttributes(...)` returns attribute names paired with .NET values. Known attributes are mapped into the stable metadata fields. Remaining readable attributes retain their Siemens names under `typeSpecific` and use one deterministic JSON conversion policy:

- `null`, strings, booleans and numbers remain their corresponding JSON values.
- Date and time values become ISO-8601 UTC strings.
- Siemens and .NET enums become their native enum-name strings.
- Simple collections of supported values become JSON arrays.
- An unknown complex value is not recursively serialized as a Siemens proxy. Its attribute name is retained together with its native .NET type and an explicit indication that its value was not serialized.

The actual attributes and value types exposed by the supported Device, DeviceItem, block, UDT and tag-table variants remain an implementation-time test surface. Observed fields are documented before the final `typeSpecific` contents are locked.

### Shared source behavior

`get_block`, `get_udt` and `get_tag_table` always return metadata and include source by default:

```text
includeSource: true   -> metadata and source; this is the default
includeSource: false  -> metadata only; do not perform an export
```

`sourceFormat` defaults to `best`. `best` follows the format order defined by the selected tool and returns the first usable native representation. An explicitly requested format is strict: it is attempted once and never falls back.

External-source generation for blocks and UDTs also accepts:

```text
includeDependencies: false  -> native GenerateOptions.None; this is the default
includeDependencies: true   -> native GenerateOptions.WithDependencies
```

`includeDependencies: true` is valid only together with an explicit `sourceFormat: "external-source"`. This prevents `best` from falling back to a representation that cannot honor the dependency request. TIA Portal owns dependency discovery and source generation; the MCP does not traverse, parse or independently list the generated dependencies. The generated external-source document may therefore contain the selected object and multiple dependent source objects.

SIMATIC SD and SimaticML export do not expose this dependency option. `get_tag_table` has no external-source representation and does not accept `includeDependencies`.

Every returned textual source document contains a reproducible SHA-256 checksum over its exact returned `content` string encoded as UTF-8 without a BOM. Line endings are preserved and no other normalization is performed. The checksum describes the returned MCP content, not the original encoding or byte layout of the temporary file written by TIA Portal:

```json
{
  "algorithm": "sha-256",
  "scope": "returned-content",
  "encoding": "utf-8-no-bom",
  "value": "hexadecimal checksum"
}
```

If source retrieval fails, the shared error behavior preserves metadata and returns `source: null` together with the failed native attempts in `errors`.

## Shared TIA Portal connection state

The dashboard, MCP clients and any other compatible bridge clients share one process-level TIA Portal attachment. The bridge never creates a separate dashboard attachment or a per-client attachment.

```text
Dashboard clients --+
                    +--> shared connection service --> zero or one TIA Portal process
MCP clients --------+
```

An attachment change made through one client is immediately the bridge state seen by every other client. Connecting, switching and disconnecting are therefore explicit operational actions; project-reading tools never change the active attachment implicitly.

All TIA Portal calls continue through the shared STA scheduler. Connection transitions are serialized so a project read cannot run against an attachment while that attachment is being replaced. The dashboard log records calls through the shared service together with their client origin and applicable TIA process and project context.

The bridge distinguishes:

- The running TIA Portal process, identified by native `processId`.
- The process's optional primary project, identified without attachment by native `TiaPortalProcess.ProjectPath`.
- The bridge's retained Openness attachment to one process.

There is no global native "active TIA window" or "active project across all processes" flag. Each process has zero or one primary project.

### Dashboard TIA tabs

The dashboard presents one general TIA tab model with separate runtime, project and log sections. A tab is bridge-owned UI state, not an Openness object and never an MCP selector.

```text
TIA tab
    -> Runtime
        -> current processId or null
        -> mode
        -> running or closed
        -> connected or disconnected
        -> previous runtime and connection identifiers
    -> Project
        -> primaryProjectPath or null
        -> open or historical
    -> Logs
        -> chronological bridge-owned entries
```

The same tab model represents both a running TIA process without a project and a process with an open primary project:

| Runtime | Project | MCP connection | Dashboard action |
|---|---|---|---|
| Running | None | Disconnected | Connect |
| Running | None | Connected | Disconnect; project-scoped tools remain unavailable |
| Running | Open | Disconnected | Connect |
| Running | Open | Connected | Disconnect or use project-scoped tools |
| Closed | Previously had a project | Disconnected | **Open project in TIA** |
| Closed | Never had a project | Disconnected | View logs or dismiss the tab |

Selecting a tab changes only the dashboard view. It never attaches, disconnects or switches the shared Openness connection. At most one tab can be connected by this MCP server at a time.

If a projectless process opens a primary project, its existing tab gains the project section and retains its logs. If one process changes from project A to project B, project A becomes historical and project B receives or reactivates its own tab; the current runtime is associated with project B from that point onward.

When a process closes, its tab remains in memory as historical. If it had an exact primary project path, **Open project in TIA** invokes `open_tia_project` with that path. A successful open associates the new TIA process and connection with the existing tab and appends new logs to the existing timeline; it does not create a duplicate tab. The old process and Openness session are historical identifiers and cannot actually be reattached.

An exact canonical `ProjectPath` is the only basis for matching a newly observed or reopened project to an existing historical tab. If a project was moved or renamed while closed, reopening uses the stored path and reports the resulting file-not-found error. The bridge does not search for the project.

If `ProjectPath` changes while the same TIA process remains running, the dashboard can observe the change during process refresh but cannot reliably distinguish Save As from opening a different project. The initial behavior treats every path change as a project transition: the old project tab becomes historical and the new exact path receives its own tab.

Runtime properties such as process ID, UI/headless mode and connection ID remain separate from project information even though they are presented together. A historical tab can accumulate several runtime process IDs and Openness connection IDs across exact-path reopenings.

### Dashboard logs

The MCP server records its own MCP tool calls, dashboard operations and applicable debug events. It does not enumerate external Openness sessions and does not attempt to obtain logs from other applications.

Every process-associated log entry retains at least:

- TIA `processId` when one applies.
- A bridge-created `connectionId` when one applies.
- Client origin such as `mcp` or `dashboard`.
- Operation or tool name.
- Timestamp, duration and result or error.

Logs from successive runtimes and connections are appended to the same matched tab without erasing their original identifiers. Events without a TIA process, such as server startup and process-enumeration failures, belong to one permanent **Server** tab.

Logs and historical tabs are bounded in-memory dashboard state. They are not persisted and disappear when the MCP server process restarts. Dismissing a historical tab removes only its dashboard history; it never closes a TIA process or project. Detailed logs remain a dashboard/debug API concern and are not exposed as a separate MCP tool.

## `list_tia_processes`

### Purpose

`list_tia_processes` is the read-only discovery operation for running TIA Portal instances. It uses the native non-blocking process diagnostic interface and never calls `Attach()` merely to enrich the list.

Each process entry contains:

```json
{
  "processId": 1234,
  "mode": "with-ui | headless",
  "primaryProjectPath": "C:\\Projects\\Line1\\Line1.ap20 or null",
  "connectedByMcp": true
}
```

`primaryProjectPath` is the native `TiaPortalProcess.ProjectPath`. It is `null` when the process has no primary project. The bridge does not attach to retrieve `Project.Name` and does not present a filename-derived value as native project metadata.

`connectedByMcp` is bridge-owned state. It is `true` only for the process that owns the MCP server's retained Openness connection; at most one returned process can have this value at a time. External Openness sessions and their counts are not part of the response.

The returned diagnostic values are a point-in-time native snapshot. A listed process can exit or change its primary project before a later connection attempt; the later operation reports the resulting native or bridge-owned selection error.

### Project scope

The initial tool reports only the optional primary project represented by `ProjectPath`. It does not attach and enumerate `TiaPortal.Projects`.

TIA Portal GUI Reference Projects are not part of the supported Openness project-access model and are outside scope.

Openness has a separate `ProjectOpenMode.Secondary` mechanism that can open additional read-only projects inside a TIA Portal instance. These secondary projects are not displayed in the TIA GUI, have `Project.IsPrimary == false`, and are accessible through the instance's `TiaPortal.Projects` composition. Secondary-project discovery and use may support a future cross-project read or comparison workflow, but are deliberately outside the initial surface.

## `connect_to_tia_portal`

### Purpose

`connect_to_tia_portal` attaches the shared bridge to one already-running TIA Portal process selected by native `processId`. Process IDs are discovered through `list_tia_processes`.

### Connection behavior

| Current state | Result |
|---|---|
| The bridge is detached and `processId` identifies a running TIA process | Attach to that exact process |
| The bridge is already attached to the requested `processId` | Reuse the retained attachment |
| The bridge is attached to process A and process B is requested | Attach to B first; after success, release A and make B the shared attachment |
| Attaching to B fails | Preserve the existing attachment to A and return the native or bridge-owned error |
| The listed process has exited or the ID is otherwise unavailable | Return an exact selection error and preserve the current attachment |

`processId` is the only initial selector. Project name, project path and enumeration order are not selectors. Specialized selection surfaces may be introduced later only when a concrete workflow requires them.

Attaching selects the TIA process. Its primary project may be absent; project-scoped tools require an attached process with an available primary project and otherwise return `noActiveProject`.

External-access approval remains in the TIA Portal UI when TIA requests it. Releasing an attachment to a user-started process must only detach this bridge: it must not save or close the user's project or TIA Portal instance.

## `open_tia_project`

### Purpose

`open_tia_project` starts a new TIA Portal instance, opens one exact compatible project as its primary project and makes the resulting Openness connection the bridge's shared attachment.

```text
open_tia_project({
  projectPath,
  mode: "with-ui" | "headless"
})
```

`projectPath` is a required exact filesystem path. `mode` maps directly to native `TiaPortalMode.WithUserInterface` or `TiaPortalMode.WithoutUserInterface`; it is optional and defaults to `with-ui`.

Creating the TIA Portal instance already establishes the Openness connection. The caller does not invoke `connect_to_tia_portal` afterward.

If the bridge is already attached, the new TIA instance is started and the requested project is opened before the previous attachment is released. A successful open switches the shared attachment to the new process. A failed start or open preserves the previous attachment and cleans up any incomplete bridge-owned instance.

The tool uses the native current-version open operation and never upgrades a project. Authentication follows native TIA Portal behavior. The bridge does not accept project credentials through the MCP contract; a headless open that cannot complete under the available native authentication context returns the native failure.

The exact V20 process-lifecycle result when the bridge later disconnects from a bridge-created headless instance must be established through live validation. The expected native behavior is that a headless instance with no remaining client may terminate; this must not be claimed as locked until observed.

## `disconnect_from_tia_portal`

### Purpose and behavior

`disconnect_from_tia_portal` releases the bridge's retained Openness attachment. It is idempotent: calling it while already disconnected succeeds without side effects.

Disconnecting affects the shared bridge state used by the dashboard and every MCP client. It does not save or close an externally owned project and does not terminate a user-started TIA Portal process.

For a bridge-created headless instance, native V20 may terminate the TIA process when this bridge is the final attached client. The response must report the observed result after this lifecycle behavior has been verified; the bridge must not imply that such a headless process remains open.

## `get_status`

### Purpose

`get_status` reports the shared operational state. It is available without an attachment and when the attached TIA process has no primary project. It does not perform project inventory.

The response owns:

- Connected or disconnected state.
- Point-in-time read timestamp.
- Attached TIA process ID and mode when available.
- Primary-project name, path, native version and modified state when a primary project is available through the retained attachment.
- Attached TIA Portal version and native installed products and options when available.
- Current access profile and whether MCP write tools are available.

Installed products describe the attached TIA Portal process. They are not the products used by the active project. Devices, project-used products and PLC engineering objects do not belong in `get_status`.

## Shared rules for `list_*` inventory tools

This section applies to the project-scoped hierarchy tools `list_devices`, `list_blocks`, `list_udts` and `list_tag_tables`. `list_tia_processes` uses the separate native process diagnostic interface defined above.

### Native hierarchy ownership

Each inventory tool traverses only its corresponding native Openness compositions:

| Tool | Native hierarchy |
|---|---|
| `list_devices` | Device groups and devices; nested device items belong to `get_device` |
| `list_blocks` | Block groups and blocks |
| `list_udts` | Type groups and UDTs |
| `list_tag_tables` | Tag-table groups and tag tables |

The MCP reconstructs each tree from its corresponding native compositions. It must not perform one broad PLC-object inventory and then reuse that result as the three PLC software inventory responses.

Object leaves return Siemens `objectId` values. Group nodes reconstruct hierarchy and do not require an `objectId` when Openness does not provide one.

Paths and parent paths are MCP-constructed navigation values owned by the inventory tools. A `get_*` tool may reconstruct the same path through the shared optional path behavior, but paths are not selectors.

### Common PLC software inventory envelope

`list_blocks`, `list_udts` and `list_tag_tables` return identity, hierarchy and only enough classification to understand each item:

```json
{
  "plcObjectId": "Siemens PLC software object identifier",
  "complete": true,
  "errors": [],
  "roots": [
    {
      "kind": "scope",
      "scopeType": "plcSoftware | softwareUnit | safetyUnit",
      "name": "PLC_1",
      "path": "PLC_1",
      "isSafety": false,
      "children": []
    }
  ]
}
```

A tree can contain:

- Scope nodes for PLC software, software units and safety units.
- Group nodes for the tool's native hierarchy.
- Object leaves for blocks, UDTs or tag tables.

System and safety scopes, groups and objects are included whenever Openness permits them to be enumerated. `isSystem` and `isSafety` describe native classification; they are not filtering rules.

Detailed attributes such as header values, timestamps, protection, `typeSpecific`, checksums and source content do not belong in list responses. The corresponding `get_*` tool owns that information.

### Partial results

If one branch cannot be enumerated, the tool returns every readable branch with `complete: false`. One unreadable branch must not erase the rest of the inventory.

The error structure follows the shared response behavior and remains lean:

```json
{
  "origin": "tia-openness",
  "operation": "enumerate",
  "path": "PLC_1/System blocks",
  "message": "Native TIA Openness message"
}
```

The MCP identifies the failed branch with its constructed path and preserves the native TIA Openness message without reinterpretation.

### Native ordering

Every inventory tool preserves the enumeration order returned by the native TIA Openness compositions. The MCP does not alphabetically sort or otherwise rearrange scopes, groups, devices or object leaves. `get_device` separately preserves native DeviceItem order within the selected Device.

### Filtering

Filtering is outside the initial rehaul scope. Every `list_*` tool returns its complete available native hierarchy.

### Pagination

The initial rehaul returns the complete available tree without pagination. Pagination may be introduced later only if actual project size or measured performance justifies it.

## `list_devices`

### Purpose

`list_devices` inventories the native device-group hierarchy and its `Device` objects. It is a lightweight station-level inventory; it does not enumerate the nested hardware of every device.

### Native hierarchy

The tool follows the Openness compositions corresponding to the TIA project navigation:

```text
Project device groups and system groups
    -> Device
```

Root-level devices remain root device nodes. Devices inside user or system groups remain under those groups. The device-group tree and native enumeration order are preserved.

### Device classification boundary

All top-level hardware targets are native `Device` objects. The initial contract does not invent a closed enum such as `plc`, `hmi`, `drive` or `pcStation`.

The response preserves native device identification through `objectId`, `Name`, `TypeIdentifier` and `IsGsd`. It does not inspect every `DeviceItem` merely to derive a device category or discover PLC software.

A derived device-category enum and category filtering may be introduced later if a concrete workflow justifies them. They are not part of the initial implementation.

### Response

```json
{
  "complete": true,
  "errors": [],
  "roots": [
    {
      "kind": "deviceGroup",
      "name": "Production line",
      "path": "Production line",
      "isSystem": false,
      "children": [
        {
          "kind": "device",
          "objectId": "Siemens identifier or null",
          "name": "PLC_Station",
          "path": "Production line/PLC_Station",
          "typeIdentifier": "native TIA type identifier",
          "isGsd": false
        }
      ]
    }
  ]
}
```

Completeness refers to the device-group and Device hierarchy only. Nested racks, CPUs, modules, submodules and interfaces are deliberately deferred to `get_device`.

### Workflow

```text
list_devices
    -> device objectId

get_device({ objectId })
    -> nested DeviceItem tree
    -> plcObjectId
```

## `get_device`

### Purpose

`get_device` reads one native `Device` and expands only that device's nested hardware. This keeps `list_devices` lightweight while preserving the complete native hardware hierarchy when it is actually needed.

### Selector and input

```text
get_device({ objectId })
get_device({ objectId, includePath: false })
```

The Siemens Device `objectId` is the only selector. The shared optional path behavior applies. Unlike block, UDT and tag-table readers, `get_device` has no source representation and no `includeSource` or `sourceFormat` argument.

### Native hierarchy

```text
Device
    -> DeviceItem
        -> nested DeviceItem
```

A `Device` is the container for a central or distributed station. `DeviceItem` objects are its racks, CPUs, head modules, modules, submodules and interfaces. The familiar GUI name `PLC_1` can appear on both the station and its CPU item; object type and identity must come from Openness, not from the displayed name.

The nested tree preserves native parent-child relationships and native enumeration order. Network nodes, addresses, channels, hardware identifiers and product-specific services are separate Openness information areas and are not automatically folded into this general device read.

### Response responsibility

The response contains:

- Device metadata including `objectId`, optional constructed `path`, `name`, `typeIdentifier`, `isGsd`, `typeName`, `author` and the non-multilingual comment view when readable.
- The complete nested DeviceItem hierarchy for that Device.
- Core DeviceItem attributes such as `objectId`, `name`, `typeIdentifier`, `typeName`, `classification`, `positionNumber`, `isBuiltIn`, `isPlugged`, `isGsd`, `orderNumber` and `firmwareVersion` when applicable.
- `plcObjectId` on each DeviceItem whose `SoftwareContainer.Software` is a `PlcSoftware`.

The `plcObjectId` is the Siemens identifier of the PLC-owning CPU DeviceItem, not the identifier of its rack or parent Device. The caller never supplies a rack identifier to access PLC software.

### PLC discovery flow

```text
list_tia_processes()
    -> select processId by primaryProjectPath
    -> connect_to_tia_portal({ processId })
    -> list_devices()
    -> Device objectId
    -> get_device({ objectId })
    -> CPU DeviceItem plcObjectId
    -> list_blocks({ plcObjectId })
    -> block objectId
    -> get_block({ objectId })
```

Internally, a PLC software inventory begins from the returned CPU identifier:

```text
ObjectIdentifierProvider.Find(plcObjectId)
    -> DeviceItem
    -> SoftwareContainer
    -> PlcSoftware
```

If a caller already retains a valid `plcObjectId` or block `objectId`, the earlier discovery calls are unnecessary. `get_block({ objectId })` remains a direct lookup and does not require Device, rack or PLC identifiers.

## `list_blocks`

### Purpose

`list_blocks` inventories the block-group hierarchy and all readable blocks under one PLC software scope. It owns block navigation and discovery; it does not export block source or return the complete block metadata packet.

### Input

```text
list_blocks({ plcObjectId })
```

### Block leaf

```json
{
  "kind": "block",
  "objectId": "Siemens block object identifier",
  "name": "MotorControl",
  "path": "PLC_1/Program blocks/MotorControl",
  "blockType": "FB",
  "number": 12,
  "programmingLanguage": "SCL",
  "isSystem": false,
  "isSafety": false
}
```

The response uses the shared inventory envelope and partial-result behavior.

### Relationship to `get_block`

```text
list_blocks({ plcObjectId })
    -> block objectIds and block-group tree

get_block({ objectId })
    -> direct native lookup, metadata and optional source
```

The caller retains returned identifiers. `get_block` does not rebuild or refresh block inventory when an identifier fails. It returns `objectNotFound`, after which the caller may explicitly call `list_blocks` again.

Block inventory should also be refreshed after changing the active project and, in a future write version, after operations that create, delete or move blocks.

## `get_block`

### Purpose

`get_block` reads one PLC block. It always returns native block metadata and returns the authoritative source by default.

### Selector

The tool resolves the block through the native Siemens operation:

```text
ObjectIdentifierProvider.Find(objectId)
```

The Siemens `objectId` is the only selector in the initial rehaul. Name and MCP-constructed path resolution are not part of `get_block`.

### Input behavior

```text
get_block({ objectId })
    -> metadata + best available source

get_block({ objectId, includeSource: false })
    -> metadata only

get_block({ objectId, sourceFormat: ... })
    -> metadata + exact requested source format

get_block({ objectId, sourceFormat: "external-source", includeDependencies: true })
    -> metadata + native external source generated with dependencies

get_block({ objectId, includePath: false })
    -> metadata with path: null, without parent traversal
```

Metadata cannot be omitted. `includePath` defaults to `true` according to the shared `get_*` behavior. When `includeSource` is `false`, the MCP must not export the block merely to obtain additional metadata.

### Response

```json
{
  "readAtUtc": "UTC timestamp",
  "metadata": {
    "objectId": "Siemens object identifier",
    "path": "string or null",
    "name": "Scl_Block",
    "blockType": "FB",
    "number": 2,
    "autoNumber": false,
    "namespace": "string or null",
    "programmingLanguage": "SCL",
    "memoryLayout": "Optimized",
    "header": {
      "author": "string",
      "family": "string",
      "userDefinedId": "string or null",
      "version": "0.1"
    },
    "state": {
      "isConsistent": true,
      "isKnowHowProtected": false
    },
    "timestamps": {
      "created": "UTC timestamp",
      "modified": "UTC timestamp",
      "compiled": "UTC timestamp",
      "codeModified": "UTC timestamp",
      "interfaceModified": "UTC timestamp",
      "parameterModified": "UTC timestamp",
      "structureModified": "UTC timestamp"
    },
    "typeSpecific": {
      "...": "remaining readable native Openness attributes"
    }
  },
  "source": {
    "format": "external-source | simatic-sd | simatic-ml",
    "dependenciesIncluded": false,
    "documents": [
      {
        "name": "source document name",
        "content": "authoritative exported content",
        "checksum": {
          "algorithm": "sha-256",
          "scope": "returned-content",
          "encoding": "utf-8-no-bom",
          "value": "hexadecimal checksum"
        }
      }
    ]
  },
  "errors": []
}
```

Source is a document list because some representations, particularly SIMATIC SD, contain more than one document. Every document includes a SHA-256 checksum over its exact returned content. No source checksum is returned when source is omitted.

### Metadata retrieval

Block metadata is read with one native bulk attribute operation:

```text
GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite)
```

The MCP maps known Siemens attribute names into the stable metadata structure. Every remaining readable native attribute is placed under `typeSpecific` with its Siemens name preserved. An attribute returned by the bulk operation must not also be fetched separately through its typed property.

`typeSpecific` remains exploratory. It must be tested across supported block types, languages, PLC families and protection states. The observed native attributes must be documented before that part of the schema is locked.

Bulk attributes do not replace the other native responsibilities:

- `ObjectIdentifierProvider` owns identity and lookup.
- Runtime block types distinguish OB, FB, FC and the DB variants.
- Openness compositions own hierarchy and group traversal.
- Native export operations own source content.

### Source formats

The preferred source representation depends on the block:

| Block | Preferred representation in TIA Portal V20 Update 0 |
|---|---|
| SCL block | External source `.scl` |
| Data block | External source `.db` |
| Pure LAD block | SIMATIC SD |
| FBD block | SimaticML |
| GRAPH block | SimaticML |
| Mixed-language block | SimaticML |

`sourceFormat` accepts `best`, `external-source`, `simatic-sd` or `simatic-ml`. It defaults to `best`.

| Block | `best` attempt order in TIA Portal V20 Update 0 |
|---|---|
| SCL block | External source, then SimaticML |
| Data block | External source, then SimaticML |
| Pure LAD block | SIMATIC SD, then SimaticML |
| FBD block | SimaticML |
| GRAPH block | SimaticML |
| Mixed-language block | SimaticML |

`best` continues through applicable formats until one complete native representation succeeds. An explicitly requested format is attempted exactly once and never falls back. The response identifies the representation actually returned.

The source is authoritative exported content. The MCP does not derive the metadata packet by parsing or normalizing that source.

### Source failure

If no source representation succeeds, metadata is still returned:

```json
{
  "readAtUtc": "UTC timestamp",
  "metadata": {
    "...": "native block metadata"
  },
  "source": null,
  "errors": [
    {
      "origin": "tia-openness",
      "operation": "sourceExport",
      "format": "external-source",
      "message": "Native TIA Openness message"
    }
  ]
}
```

Block protection is metadata, not an MCP filtering rule. The MCP attempts every applicable native export and returns everything TIA Portal permits it to export. A protected or limited representation returned by TIA is preserved unchanged together with its protection and content-scope information.

The MCP does not attempt passwords or unlocking. A native refusal is preserved in `errors`.

### Header text and comments

The TIA Portal GUI fields **Title**, **Comment**, and **User-defined ID** are separate values. In the V20 Openness API, `HeaderName` maps to **User-defined ID**, so the MCP exposes it as `header.userDefinedId`. It is not the block name or title.

V20 does not expose the GUI block Title and Comment as direct `PlcBlock` properties. They are not normalized into the metadata packet. If the selected representation contains them, they remain part of the authoritative source.

Interface comments and program comments also remain part of the source. This version does not return a separate multilingual-content structure.

## `list_udts`

### Purpose

`list_udts` inventories the type-group hierarchy and all readable UDTs under one PLC software scope. It owns UDT navigation and discovery, not detailed UDT metadata or source export.

### Input

```text
list_udts({ plcObjectId })
```

### UDT leaf

```json
{
  "kind": "udt",
  "objectId": "Siemens UDT object identifier",
  "name": "t_Item",
  "path": "PLC_1/PLC data types/t_Item",
  "isSystem": false,
  "isSafety": false
}
```

The response uses the shared inventory envelope and partial-result behavior. The detailed `get_udt` selector and response schema are not inferred by `list_udts`.

## `get_udt`

### Purpose

`get_udt` is the dedicated owner of one UDT's detailed metadata and authoritative source. These details must not be duplicated in `list_udts`.

### Selector and input behavior

```text
get_udt({ objectId })
    -> metadata + best available source

get_udt({ objectId, includeSource: false })
    -> metadata only

get_udt({ objectId, sourceFormat: ... })
    -> metadata + exact requested source format

get_udt({ objectId, sourceFormat: "external-source", includeDependencies: true })
    -> metadata + native external source generated with dependencies
```

The Siemens UDT `objectId` is the only selector. Metadata cannot be omitted. The shared `includePath`, optional-source, strict-format and source-failure behavior applies.

### Metadata

The initial stable UDT metadata contains:

```json
{
  "objectId": "Siemens UDT object identifier",
  "path": "string or null",
  "name": "t_Item",
  "namespace": "string or null",
  "state": {
    "isConsistent": true,
    "isKnowHowProtected": false
  },
  "timestamps": {
    "created": "UTC timestamp",
    "modified": "UTC timestamp",
    "interfaceModified": "UTC timestamp"
  },
  "typeSpecific": {
    "...": "remaining readable native Openness attributes"
  }
}
```

Metadata is read through the native `PlcType` object and one bulk `GetAttributes` operation using the same known-field and exploratory-`typeSpecific` rule as `get_block`. A block metadata header is not imposed on a UDT.

### Source formats

The preferred UDT representation is an external source `.udt` document.

`sourceFormat` accepts `best`, `external-source`, `simatic-sd` or `simatic-ml`. It defaults to `best`.

```text
best:
external-source (.udt)
    -> SIMATIC SD
    -> SimaticML
```

The `.udt` source is generated through the UDT's containing `PlcExternalSourceSystemGroup`. SIMATIC SD uses native `PlcType.ExportAsDocuments`, and SimaticML uses native `PlcType.Export`. These are internal format-specific operations behind the one `get_udt` surface.

An explicit format is strict and never falls back. Source protection is metadata, not an MCP filtering rule; the tool attempts every applicable native operation and returns everything TIA Portal permits it to export.

## `list_tag_tables`

### Purpose

`list_tag_tables` inventories the tag-table group hierarchy and all readable tag tables under one PLC software scope.

### Input

```text
list_tag_tables({ plcObjectId })
```

### Tag-table leaf

```json
{
  "kind": "tagTable",
  "objectId": "Siemens tag-table object identifier",
  "name": "Default tag table",
  "path": "PLC_1/PLC tags/Default tag table",
  "isSystem": false,
  "isSafety": false
}
```

The response uses the shared inventory envelope and partial-result behavior.

`list_tag_tables` returns tag-table groups and tag tables only. It does not return tag entries or detailed tag-table metadata.

## `get_tag_table`

### Purpose

`get_tag_table` is the dedicated owner of one tag table's detailed metadata and authoritative native source. These values must not be duplicated in `list_tag_tables`.

### Selector and input behavior

```text
get_tag_table({ objectId })
    -> metadata + SimaticML source

get_tag_table({ objectId, includeSource: false })
    -> metadata only

get_tag_table({ objectId, sourceFormat: "simatic-ml" })
    -> metadata + strict SimaticML source
```

The Siemens tag-table `objectId` is the only selector. Metadata cannot be omitted. The shared `includePath`, optional-source and source-failure behavior applies.

### Metadata

The initial stable tag-table metadata contains:

```json
{
  "objectId": "Siemens tag-table object identifier",
  "path": "string or null",
  "name": "Default tag table",
  "isDefault": true,
  "timestamps": {
    "modified": "UTC timestamp"
  },
  "typeSpecific": {
    "...": "remaining readable native Openness attributes"
  }
}
```

The metadata is read through the native `PlcTagTable` object and bulk attributes. The table's `Tags`, `UserConstants` and `SystemConstants` compositions remain native typed APIs, but the initial contract does not construct an additional JSON content model from them.

### Source format

The V20 Update 0 public Openness action `PlcTagTable.Export` exports SimaticML. `PlcTagTable` does not expose `ExportAsDocuments`, so SIMATIC SD is not an available tag-table representation.

The TIA Portal tag-table editor separately supports simpler exchange formats such as XLSX, XML and SDF. Those GUI formats are not exposed by the V20 Update 0 public `PlcTagTable` export API and are therefore not recreated in the initial MCP bridge.

`sourceFormat` initially accepts only `best` and `simatic-ml`:

```text
best -> SimaticML
```

There is no fallback in the initial contract because only one native Openness source representation is available. The MCP returns the exact native SimaticML document and its checksum and does not claim that the export contains values which the native operation omits.

A future derived `tag-table-xml` or JSON representation may be added inside `get_tag_table` after the core API is stable. It must be clearly identified as MCP-constructed rather than native export. A separate overlapping `get_tag_table_entries` convenience tool is not part of the rehaul.

## `get_cross_references`

### Purpose

`get_cross_references` queries TIA Portal's native `CrossReferenceService` for one engineering object. It owns reference information and does not duplicate object metadata or source readers.

### Selector and lookup

```text
get_cross_references({ objectId })
```

The Siemens `objectId` is the only selector. The tool resolves it directly through `ObjectIdentifierProvider.Find(objectId)` and requests `CrossReferenceService` from the resolved object. It does not rebuild the complete PLC inventory to resolve or enrich the target.

Applicability is service-driven. Blocks, DB variants, PLC tags, system constants and UDTs are among the Step 7 objects documented to expose the service. If TIA Portal returns no service for the selected object, the tool returns `unsupportedObject`; the MCP does not maintain a second manual support classification.

### Query and response boundary

The initial implementation uses the native `CrossReferenceFilter.AllObjects` query and does not expose additional filters without a concrete workflow.

The native result is preserved as the hierarchy returned by Openness:

```text
CrossReferenceResult
    -> Sources: SourceObject[]
        -> Children: SourceObject[]
        -> References: ReferenceObject[]
            -> Locations: Location[]
```

The initial response follows that hierarchy rather than flattening it into MCP-created `uses` and `usedBy` arrays:

```json
{
  "readAtUtc": "UTC timestamp",
  "sources": [
    {
      "name": "MotorControl",
      "path": "Program blocks/MotorControl",
      "typeName": "FB",
      "device": "PLC_1",
      "address": "FB2",
      "objectId": "Siemens identifier or null",
      "children": [],
      "references": [
        {
          "name": "MotorDB",
          "path": "Program blocks/MotorDB",
          "typeName": "DB",
          "device": "PLC_1",
          "address": "DB3",
          "objectId": "Siemens identifier or null",
          "locations": [
            {
              "referenceType": "Uses",
              "access": "RW",
              "referenceLocation": "Network 1",
              "name": "native location name or null",
              "typeName": "native location type or null",
              "address": "native location address or null",
              "referencedAsName": "MotorDB",
              "referencedAsObjectId": "Siemens identifier or null"
            }
          ]
        }
      ]
    }
  ],
  "errors": []
}
```

`SourceObject` and `ReferenceObject` preserve native `Name`, `Path`, `TypeName`, `Device`, `Address` and `UnderlyingObject` information. A Siemens `objectId` is added only when Openness supplies an underlying `IEngineeringObject` that `ObjectIdentifierProvider` can identify. `Location` preserves the native `ReferenceType`, `Access`, `ReferenceLocation`, `Name`, `TypeName`, `Address`, `ReferencedAs` and `ReferencedAsName` information. Native enum names are returned without reducing them to a smaller MCP enum.

The tool does not build a PLC inventory merely to enrich a cross-reference result. Textual members remain textual and are not fabricated as engineering objects.

The tool does not compile, parse source or maintain a persisted call graph. It reports the cross-reference state returned by TIA Portal at call time.

## Shared inventory refresh behavior

The MCP does not maintain a hidden persistent object index. The caller retains object identifiers returned by the inventory tools.

A `get_*` tool does not automatically rebuild inventory when an identifier cannot be resolved. It returns `objectNotFound`; the caller decides whether to invoke the relevant `list_*` tool again.

Inventory should be refreshed:

- After changing the active project.
- After `objectNotFound` when the caller expects the object still to exist.
- In a future write version, after creating, deleting or moving objects.

## Future writes: transient source staging

This section defines the settled file-handling boundary for future write tools. It does not define their names, inputs, write semantics or complete response contracts.

### External-source writes

When a future write uses an external source such as `.scl`, `.db` or `.udt`, the MCP owns the complete transient lifecycle:

```text
Receive source content
    -> write a uniquely named temporary source file
    -> create a temporary PlcExternalSource in the target TIA scope
    -> generate the requested blocks or UDTs
    -> delete the PlcExternalSource from the TIA project
    -> delete the temporary disk files
```

The temporary `PlcExternalSource` is an implementation artifact, not a persistent project object created for the user. Cleanup is attempted after both successful and failed generation.

If cleanup fails, the operation reports `cleanupFailed` and must not claim complete success.

### SIMATIC SD writes

For SIMATIC SD, the MCP writes the `.s7dcl` and applicable `.s7res` documents into a temporary directory and calls the native document import operation directly on the target block composition.

SIMATIC SD import does not create a `PlcExternalSource`.

### SimaticML writes

For SimaticML, the MCP writes a temporary XML file and calls the native import operation directly on the target composition.

SimaticML import does not create a `PlcExternalSource`.

### Temporary-file ownership

All temporary files and directories are internal to the MCP and are removed after the operation. The client supplies document content through the MCP request and does not manage server-local file paths.

The existing legacy SCL REST write is not the model for the rehaul. It exports an existing block as SimaticML, injects SCL into that XML and reimports the patched XML. A future source-native SCL write follows the transient external-source lifecycle instead.

## Contracts intentionally not yet locked

The following areas remain open and are not silently decided by this document:

- The final observed contents of the exploratory `typeSpecific` metadata sections across supported Device, DeviceItem, block, UDT and tag-table variants.
- A derived tag-table XML or JSON representation; the initial native source remains SimaticML only.
- Future write-tool names, request schemas and write semantics.
- Pagination unless measured project size or performance makes it necessary.
- A derived device-category enum and device-category filtering; both are explicitly future scope.
- The exact native V20 process-lifecycle result when disconnecting from a bridge-created headless TIA Portal instance.
