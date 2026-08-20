# TIA Portal MCP bridge — version 1.1 read extension

**Status:** Discussion draft

**Target:** TIA Portal V20, TIA Portal Openness V20, .NET Framework 4.8, x64

**Last updated:** 2026-08-16

**Implementation status:** Not implemented

## 1. Purpose

Version 1.1 completes the read context needed before planning controlled project-changing operations for version two.

It is an additive read-only extension to the approved version-one contract. It does not reopen the version-one representation design, replace any canonical version-one tool, or authorize writes.

Version 1.1 adds exactly two proposed MCP tools:

- `get_project_used_products`
- `get_project_manifest`

SCL linting, block analysis, behavioral interpretation, and other derived analysis are outside version 1.1.

## 2. Relationship to version one

The [version-one read specification](version-one-read-specification.md) remains authoritative for:

- Runtime and active-project lifecycle.
- Loopback Streamable HTTP transport.
- PLC discovery, object identity, and selector rules.
- Native representations and checksums.
- Protection handling and structured errors.
- Provenance and native-message handling.
- Read-only safety and detach-only shutdown.

Version 1.1 must preserve all version-one invariants:

- TIA Portal remains the source of truth.
- Every Siemens engineering-object access runs through `StaTaskScheduler`.
- The MCP does not create or persist a project mirror, semantic model, source index, or call graph.
- Reads do not compile, save, close, import, create, rename, delete, clone, upgrade, download, or go online.
- `TIA_MCP_ACCESS=full` does not change MCP discovery, dispatch, or authorization.
- MCP `get_status.writeToolsAvailable` remains `false`.
- Unknown native facts remain `null` or explicitly unknown; they are not inferred as false.

Version 1.1 does not add hardware internals, HMI software, drives, online data, or project-changing operations to the MCP scope.

## 3. Proposed version 1.1 MCP surface

When version 1.1 is implemented, the complete MCP surface will contain the eight version-one tools plus the two version 1.1 tools:

| Tool | Version | Responsibility |
|---|---:|---|
| `connect_to_tia_portal` | 1.0 | Select and attach to one active compatible project |
| `get_status` | 1.0 | Report connection, project provenance, installed TIA products/options, and invariant MCP write unavailability |
| `list_devices` | 1.0 | Discover project devices and PLC software targets |
| `list_plc_objects` | 1.0 | Return one PLC's included software hierarchy |
| `find_plc_objects` | 1.0 | Search live PLC object and tag/constant metadata |
| `read_plc_object` | 1.0 | Return one complete native representation |
| `get_tag_table_entries` | 1.0 | Return direct tag-table, tag, and constant views |
| `get_cross_references` | 1.0 | Return native on-demand TIA cross-references |
| `get_project_used_products` | 1.1 | Return the native products and versions used by the active project |
| `get_project_manifest` | 1.1 | Return a deterministic on-demand inventory manifest for the active project's included PLC software |

No compatibility readers, derived-analysis tools, or project-changing tools are added.

## 4. `get_project_used_products`

### 4.1 Question answered

`get_project_used_products` answers:

> Which products and product versions does TIA Portal report as used by the active project?

The authoritative native source is `Project.UsedProducts`. The tool name must describe that source accurately; it must not retain the broader and potentially misleading legacy name `get_option_packages`.

### 4.2 Input

The tool has no arguments.

Its input schema must be an object with `additionalProperties: false`.

### 4.3 Response

The response must contain:

- Ordinary version-one provenance, including the active project identity.
- `authority: "native-project-used-products"`.
- An explicit completeness classification.
- `usedProducts`, preserving one entry per native `UsedProduct` returned by TIA.

Each entry contains only native facts available from the current V20 API contract:

- `name: string`
- `version: string | null`

Blank native versions are normalized to JSON `null`. Names and versions must not be rewritten into marketing names or interpreted as another product identifier.

Conceptual response shape:

```json
{
  "provenance": {},
  "authority": "native-project-used-products",
  "completeness": "complete",
  "usedProducts": [
    {
      "name": "<native product name>",
      "version": "<native version or null>"
    }
  ]
}
```

### 4.4 Evidence boundary

The response reports what the project uses. It does not by itself establish:

- Whether a corresponding product or option is installed.
- Whether it is licensed or currently usable.
- Which project objects require that product.
- Update, patch, service-pack, or build information.
- Whether similarly named used and installed products are equivalent.

Installed-software facts remain available through `get_status`. A calling agent may present the two native views together, but the MCP must not manufacture a compatibility conclusion from name similarity.

### 4.5 Failure behavior

If there is no active project, the tool returns the canonical `no-active-project` error.

Failure to enumerate `Project.UsedProducts` is a structured TIA-operation failure. The server must not return an empty list as success when native enumeration failed.

## 5. `get_project_manifest`

### 5.1 Question answered

`get_project_manifest` answers:

> What included PLC software objects exist in the active project at this read point, and what deterministic inventory fingerprint represents that observed structure?

The manifest is intended for:

- Human review before a controlled version-two change.
- Detecting inventory-level changes between two explicit reads.
- Recording which project and PLC objects were observed during a workflow.
- Selecting objects for targeted `read_plc_object` calls.

It is not a behavioral model, source backup, project clone, or proof that object contents are unchanged.

### 5.2 Input

The initial version 1.1 proposal has no arguments and covers every PLC in the single active project.

Its input schema must be an object with `additionalProperties: false`.

### 5.3 Manifest scope

The manifest contains:

- Active-project identity.
- Every PLC discovered by the canonical version-one traversal.
- Every included content-bearing PLC software object:
  - OB, FB, FC, and DB variants.
  - PLC data types and UDTs.
  - PLC tag tables.
- The native hierarchy and identity metadata already available to `list_plc_objects`.

Each manifest object contains, when natively available:

- `objectId`
- `plcObjectId`
- `parentObjectId`
- `path`
- `name`
- `type`
- `language`
- `number`
- `isProtected`
- `isSafety`
- `isSystemGenerated`
- `isConsistent`
- `contentAvailable`
- `contentLimitation`

Tags and constants are not expanded into the initial manifest. Their authoritative selected-field view remains available through `get_tag_table_entries`.

### 5.4 Deterministic inventory checksum

The response includes a SHA-256 `manifestChecksum` calculated over a documented canonical serialization of the manifest's project, PLC, and object identity/metadata entries.

The checksum contract must:

- Sort PLCs and objects deterministically using stable Siemens identifiers when available and canonical paths as the fallback.
- Exclude observation timestamps and response ordering.
- Preserve the distinction between `null`, `false`, `true`, empty string, and absent values.
- Identify its checksum scheme with a versioned name.
- Produce the same value for the same observed inventory facts regardless of enumeration order.

The checksum represents only the manifest fields. It must not be described as a source-code checksum, project-file checksum, compile fingerprint, or behavioral signature.

### 5.5 Response

The response must contain:

- Ordinary version-one provenance.
- `authority: "direct-selected-field-openness-view"` for the native manifest facts.
- An explicit completeness classification.
- The checksum scheme and `manifestChecksum`.
- The included PLC and object entries.
- Explicit limitations for native fields that could not be read.

The manifest may use a flat deterministically sorted object list for comparison, provided that every entry retains its parent identity and canonical path. `list_plc_objects` remains the canonical hierarchical navigation response.

### 5.6 No persistent project model

The manifest is assembled for one request and returned to the caller. The MCP must not:

- Store the manifest after the request.
- Compare it with a prior manifest.
- Maintain a background project index.
- Monitor the project for changes.
- Treat it as a replacement for a fresh TIA read.

The calling agent or user owns any storage and comparison of manifest responses.

### 5.7 Source-content preconditions remain targeted

The initial manifest does not export every object or calculate every object's source-content checksum. A future version-two write must use a targeted `read_plc_object` result and its native-representation checksum as the content precondition for the object being changed.

This keeps the manifest bounded and prevents one inventory request from triggering project-wide source exports.

### 5.8 Failure and partial metadata

Failure to enumerate a required project, PLC, or object collection is a structured TIA-operation failure. The server must not silently omit an unreadable required branch and label the result complete.

Failure to read an optional native property is isolated to that field and represented as unknown. It must not remove the object or convert the unknown fact to `false`.

## 6. Explicitly excluded legacy tools

Version 1.1 does not restore:

- `list_blocks`
- `list_tag_tables`
- `get_tags`
- `get_project_signature`
- `analyze_scl`
- `analyze_block`
- `read_block`
- `read_scl_source`
- `read_lad_source`

The first three are superseded by canonical version-one discovery and tag tools. `get_project_signature` is replaced by the better-defined manifest contract. The analysis tools produce derived heuristic results rather than additional native TIA evidence. The compatibility readers remain outside the canonical MCP surface.

## 7. Version 1.1 acceptance direction

Version 1.1 implementation will require, at minimum:

- `tools/list` advertises exactly the ten version 1.0 and 1.1 tools in every access profile.
- Direct calls to removed legacy, derived-analysis, and project-changing tool names remain rejected.
- Both new tools use the shared MCP definitions, argument validation, and HTTP dispatch path.
- Both new tools run all Siemens access through `StaTaskScheduler`.
- Both new tools return version-one provenance and structured errors.
- Used-product results preserve the native project-used-product boundary.
- Manifest enumeration covers every discovered PLC and included content-bearing object.
- Manifest checksums are deterministic under reordered input.
- Neither tool exports source, compiles, saves, closes, writes, imports, creates, clones, upgrades, or goes online.
- Live acceptance uses a disposable representative project and verifies that the project remains unchanged.

This document defines a proposed scope for discussion. It does not claim that version 1.1 is implemented or accepted.

## 8. Questions for the next discussion

The next discussion should confirm:

1. Whether the initial manifest should remain limited to content-bearing PLC objects or also fingerprint tag and constant entry metadata.
2. The exact canonical serialization and versioned checksum-scheme name for `manifestChecksum`.
3. Whether used products should remain a raw native list only, as proposed, or whether a separately classified exact-match comparison with `get_status` is needed.
4. Whether version 1.1 should have its own live-acceptance record after version-one acceptance or share one combined representative-project validation run.
