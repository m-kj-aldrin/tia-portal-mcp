# Project rehaul

## Purpose and status

This document records the initial constraints and settled decisions for the project rehaul. Nothing described here is implemented yet, and this is not a complete implementation plan.

The document is organized by tool so that each tool has one clear responsibility. Shared behavior is defined once and referenced by the tools that use it.

The source-format decisions currently assume TIA Portal V20 Update 0.

The rehaul follows a native-fidelity principle: MCP extensions may structure information that Openness does not expose as one ready-made response, but they preserve TIA identity, names, hierarchy and ordering whenever possible.

## Tool map

| Tool | Responsibility | Contract status |
|---|---|---|
| `connect_to_tia_portal` | Establish or reuse the bridge's attachment to one exact TIA Portal project | Initial contract settled |
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

## `connect_to_tia_portal`

### Purpose

`connect_to_tia_portal` establishes the bridge's zero-or-one active project attachment. It does not assume that the bridge is already attached, although reuse of an existing attachment is the normal fast path.

### Connection behavior

| Current state | Result |
|---|---|
| The bridge is already attached and `projectPath` is omitted or matches it | Reuse the current project |
| The bridge is attached to project A and an exact different `projectPath` B is supplied | Detach from A without saving or closing it, then attach to or visibly open exact project B |
| The bridge is detached and exactly one project is open in TIA Portal | Attach to that project |
| `projectPath` exactly matches one open project | Attach to the exact project |
| No project is open and `projectPath` is supplied | Start visible TIA Portal and open the compatible project |
| No project is open and no path is supplied | Return `noActiveProject` |
| More than one project is open without an exact match | Return `ambiguousProject` |

`projectPath` is an optional exact filesystem path, not an engineering-object selector. Before a project is attached there is no project-scoped Siemens object identifier available to the caller.

The tool never upgrades a project. External-access approval and interactive authentication remain in the visible TIA Portal UI. Detaching the bridge must not save or close the user's project.

## `get_status`

### Purpose

`get_status` reports operational state. It is available without an active project and does not perform project inventory.

The response owns:

- Connected or disconnected state.
- Point-in-time read timestamp.
- Active project name, path, native version and modified state when available.
- Attached TIA Portal version and native installed products and options when available.
- Current access profile and whether MCP write tools are available.

Installed products describe the attached TIA Portal process. They are not the products used by the active project. Devices, project-used products and PLC engineering objects do not belong in `get_status`.

## Shared rules for `list_*` inventory tools

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
connect_to_tia_portal()
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
