# Project rehaul

This document records the initial constraints and decisions for the project rehaul. The design is still at an early stage; this is not an implementation plan.

## Decision 1: Preferred PLC document representations

The MCP should use the following representations when extracting PLC documents through TIA Portal Openness. These choices assume TIA Portal V20 Update 0.

| PLC object | Preferred representation |
|---|---|
| SCL block | External source `.scl` |
| Data block | External source `.db` |
| UDT | External source `.udt` |
| Pure LAD block | SIMATIC SD |
| FBD block | SimaticML in Update 0 |
| GRAPH block | SimaticML |
| Mixed-language block | SimaticML in Update 0 |

## Decision 2: `get_block` read contract

`get_block` resolves one PLC block through the native Siemens `ObjectIdentifierProvider.Find(objectId)` operation. The Siemens `objectId` is the only block selector in the initial rehaul; name and path resolution are not part of `get_block`.

The tool returns the native metadata of the block together with its source. Metadata is always returned. Source is returned by default but can be omitted when the caller only needs block information.

```text
get_block({ objectId })                       -> metadata + best available source
get_block({ objectId, includeSource: false }) -> metadata only
get_block({ objectId, sourceFormat: ... })    -> metadata + exact requested source format
```

There is no option to omit metadata. When `includeSource` is `false`, the MCP must not export the block merely to obtain additional metadata.

### Response shape

```json
{
  "metadata": {
    "objectId": "Siemens object identifier",
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
    "documents": [
      {
        "name": "source document name",
        "content": "authoritative exported content",
        "checksum": {
          "algorithm": "sha256",
          "value": "hexadecimal checksum"
        }
      }
    ]
  }
}
```

Source is represented as a document list because some representations, particularly SIMATIC SD, can contain more than one document. Every returned source document includes a checksum calculated over its exact returned content. When source is omitted, no source checksum is returned.

### Native attribute access

Block metadata should be read through one native bulk attribute operation:

```text
GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite)
```

The MCP maps known Siemens attribute names into the common metadata structure. It places every remaining readable native attribute under `typeSpecific`, preserving the Siemens attribute name. The same attribute must not also be fetched separately through its typed property.

`typeSpecific` is an exploratory surface at this stage. It will be tested across the supported block types, languages, PLC families and protection states. The observed native attributes will be documented before this part of the schema is locked.

Bulk attributes do not replace other Openness mechanisms:

- `ObjectIdentifierProvider` owns object identity and lookup.
- Runtime block types identify OB, FB, FC and the different DB variants.
- Openness compositions own hierarchy and group traversal.
- Native export operations own source content.

### Source selection and fallback

`sourceFormat` accepts `best`, `external-source`, `simatic-sd` or `simatic-ml`. It defaults to `best` when omitted.

| Block | `best` attempt order in TIA Portal V20 Update 0 |
|---|---|
| SCL block | External source, then SimaticML |
| Data block | External source, then SimaticML |
| Pure LAD block | SIMATIC SD, then SimaticML |
| FBD block | SimaticML |
| GRAPH block | SimaticML |
| Mixed-language block | SimaticML |

`best` continues through the applicable formats until one complete native representation succeeds. An explicitly requested format is attempted exactly once and never falls back. The response always identifies the representation that was actually returned.

If no source representation succeeds, the metadata is still returned:

```json
{
  "metadata": { "...": "native block metadata" },
  "source": null,
  "sourceUnavailable": {
    "requestedFormat": "best",
    "attempts": [
      {
        "format": "external-source",
        "result": "failed",
        "reason": "native TIA error"
      }
    ],
    "reason": "No complete native source representation succeeded."
  }
}
```

Block protection is metadata, not an MCP filtering rule. The MCP attempts every applicable native export and returns everything TIA Portal permits it to export. If TIA returns a protected or limited representation, it is returned unchanged with its protection and content-scope metadata. The MCP does not attempt passwords or unlocking. A native refusal is reported through `sourceUnavailable`.

The source format is selected according to Decision 1. The source remains authoritative exported content; metadata is not derived by parsing or normalizing that source.

### Block header text in TIA Portal V20

The TIA Portal GUI fields **Title**, **Comment**, and **User-defined ID** are separate values. In the V20 Openness API, `HeaderName` maps to **User-defined ID**, so the MCP exposes it as `header.userDefinedId`. It is not the block name or title.

V20 does not expose the GUI block Title and Comment as direct `PlcBlock` properties. They are therefore not normalized into the metadata packet. If the selected source representation contains them, they remain part of that authoritative source. Interface comments and program comments also remain part of the source. The MCP does not return a separate multilingual-content structure in this version.

## Decision 3: Inventory boundaries and object discovery

The rehaul uses separate inventory tools for the distinct Openness object hierarchies:

| Tool | Native hierarchy traversed |
|---|---|
| `list_blocks` | Block groups and blocks |
| `list_udts` | Type groups and UDTs |
| `list_tag_tables` | Tag-table groups and tag tables |

Each inventory tool reconstructs only its own group tree from the corresponding native Openness compositions. Object leaves return Siemens `objectId` values together with the navigation hierarchy. Group nodes reconstruct that hierarchy and do not require an `objectId` when Openness does not provide one. A broad inventory traversal must not run implicitly inside an object reader.

The intended block workflow is:

```text
list_devices
    -> plcObjectId

list_blocks({ plcObjectId })
    -> block objectIds and block-group tree

get_block({ objectId })
    -> direct native lookup, metadata and optional source
```

The agent retains returned identifiers for subsequent reads. `get_block` does not automatically rebuild or refresh the inventory when an identifier fails. It returns `objectNotFound`, after which the agent may explicitly call the relevant inventory tool again. Inventory should also be refreshed after changing the active project and, in a future write version, after operations that create, delete or move objects.

Paths and parent paths are MCP-constructed navigation values owned by the inventory tools. They are not selectors or metadata fields of the initial `get_block` contract.

### Inventory response boundary

The inventory tools return identity, hierarchy and only the classification needed to understand each item. They do not duplicate the detailed metadata or source owned by the corresponding `get_*` tool.

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
      "children": [
        {
          "kind": "group",
          "name": "Program blocks",
          "path": "PLC_1/Program blocks",
          "isSystem": false,
          "isSafety": false,
          "children": [
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
          ]
        }
      ]
    }
  ]
}
```

`list_udts` and `list_tag_tables` use the same envelope and group structure. Their object leaves use `kind: "udt"` and `kind: "tagTable"` respectively, with the common identity and classification fields applicable to that hierarchy. `list_tag_tables` inventories tag-table groups and tag tables; it does not return tag entries.

System and safety scopes, groups and objects are included whenever Openness permits them to be enumerated. `isSystem` and `isSafety` describe native classification; they are not filtering rules.

Detailed attributes such as header values, timestamps, protection, `typeSpecific`, checksums and source content are excluded from the list responses. They belong to `get_block`, `get_udt` or `get_tag_table`.

If one branch cannot be enumerated, the tool returns the readable branches with `complete: false` and records the affected path, an error code and the native error message in `errors`. One unreadable branch must not erase the rest of the inventory.

The initial rehaul returns the complete available tree without pagination. Pagination may be introduced later if actual project size or measured performance justifies it.

Persistent PLC External Source objects are not part of the initial inventory scope.

## Decision 4: Transient source staging for future writes

This decision defines the boundary for source-file handling without defining the complete future write API.

When a future write uses an external source such as `.scl`, `.db` or `.udt`, the MCP owns the entire transient lifecycle:

```text
Receive source content
    -> write a uniquely named temporary source file
    -> create a temporary PlcExternalSource in the target TIA scope
    -> generate the requested blocks or UDTs from that source
    -> delete the PlcExternalSource from the TIA project
    -> delete the temporary disk files
```

The temporary `PlcExternalSource` is an implementation artifact, not a persistent project object created for the user. Cleanup is attempted after both successful and failed generation. If cleanup fails, the operation reports `cleanupFailed` and must not claim complete success.

SIMATIC SD uses a different native path. The MCP writes the `.s7dcl` and applicable `.s7res` documents into a temporary directory and calls the native document import operation directly on the target block composition. It does not create a `PlcExternalSource`. SimaticML similarly uses direct composition import from a temporary XML file.

All temporary disk files and directories are internal to the MCP and are removed after the operation. The client supplies document content through the MCP request and does not manage server-local file paths.

The existing legacy SCL REST write is not the model for this rehaul. It exports an existing block as SimaticML, injects SCL content into that XML and reimports the patched XML. A future source-native SCL write instead follows the transient external-source lifecycle above.

