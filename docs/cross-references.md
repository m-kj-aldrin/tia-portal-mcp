# Native cross-reference increment — 2026-09-21

`get_cross_references` is implemented through dashboard POST `/api/prototype/cross-references`. MCP publication remains held: the existing eight V1 descriptors, including get_cross_references, are still disabled. This increment does not silently publish the eleven-tool MCP surface.

## Implemented contract

The request accepts only `{processId, objectId}`. Selectors are required and opaque; filters, path/source flags, names and extra/duplicate fields are rejected. The service captures the existing attachment ticket before queueing on the shared STA worker. It resolves the target directly through the retained project's ObjectIdentifierProvider.Find and asks that object's IEngineeringServiceProvider for CrossReferenceService. A missing object returns objectNotFound; absent provider/service returns unsupportedObject. Applicability has no extra bridge type allowlist.

The native query is GetCrossReferences(CrossReferenceFilter.AllObjects). Its result preserves Sources → Children/References → Locations in native order. Source/reference fields retain Name, Path, TypeName, Device and Address. Their objectId comes only from a native UnderlyingObject that is an IEngineeringObject and can be identified. Location fields retain ReferenceType, Access, ReferenceLocation, Name, TypeName, Address and ReferencedAsName; referencedAsObjectId is obtained only from a native engineering ReferencedAs object. Enum names remain native; textual values are never fabricated into engineering objects. Native paths are not reconstructed or rewritten.

The adapter reads native values lazily on the STA and projects managed dictionaries. There is no inventory rebuild, source parsing/export, compilation, derived uses/usedBy graph, persistent index or project modification. Native errors preserve their messages and origin. Failed fields become null; failed collection acquisition becomes null with errors; successfully read empty collections are []. Interrupted enumeration retains readable earlier items and independent branches. A native query failure returns sources:null with errors. A null native result without an exception is reported as a bridge error, not a successful empty result.

The shared guard checks the retained context before/after the operation, around the native query, at collection boundaries and after failures. Context loss discards the payload and requires explicit reconnection. Ordinary unsupported-object/query errors do not themselves replace or invalidate a still-valid attachment.

## Dashboard workflow

The Cross-reference object selector uses already-read block and UDT inventories plus identified tags/user constants/system constants from the most recently read selected tag table. It always submits the selected object's own ID and processId. Entries with no ID are omitted from selectable candidates. A raw Object ID field allows other native objects without inventing a frontend support allowlist.

Changing the table clears its earlier entry choices; metadata-only table reads also clear those choices. Device/CPU changes and reconnection clear cross-reference selectors. Reading references does not automatically attach, retry, rebuild inventory or read block source.

## Verification

- [x] Staged net48/x64 Release build against installed V20 API: zero warnings/errors.
- [x] Offline harness: 62/62 groups. Five added groups cover strict requests, native hierarchy/path/order/enum retention, field and collection failures, empty/unavailable results and context loss. Existing stale-ticket and post-read-transition checks include cross-reference reads.
- [x] Dashboard script checks: 8/8, including own tag/constant IDs, block/UDT selection, ordinary unsupported-object response without reconnect, metadata-only entry clearing, CPU changes and reconnection clearing. These use mocked fetch/minimal DOM.
- [x] Architecture/source-boundary checks: 7/7, including direct service lookup, AllObjects, native underlying identities, pre-queue ticket capture, held publication and no export/inventory/compile path.
- [x] Normal Release build: zero warnings/errors. Managed PID 62172 exited gracefully; the helper loaded the single replacement PID 53216 on port 5000. Passive status reported implementationPhase:rehaul-cross-references, writeToolsAvailable:false, zero pending operations and no attachments. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server, including invalid/disconnected/cross-origin cross-reference requests, served Read cross-references control, unchanged attachments and the eight disabled MCP descriptors. No native query or TIA attachment was performed by the smoke check.
- [x] User supplied a successful native cross-reference response for Level meter; see the scoped evidence below.
- [ ] Independent comparison with TIA's cross-reference view has not been explicitly confirmed. No native cross-reference query was performed by the offline/dashboard/HTTP checks.

The build validates installed API signatures, not actual service availability, ID resolvability, query freshness or fidelity for every object type. An entry having an ID does not establish cross-reference support. Populated tag-table constants and broader lifecycle/unit/protection scenarios retain their previously documented evidence limits.

## Short user comparison after reload

1. Refresh the dashboard, reconnect the test process, then List devices → Read device → List tag tables → choose FIO → Read tag table with Include entries enabled.
2. Under Cross-reference object, choose a tag that is used in the project (for example Start Button if it is used), then Read cross-references. Compare the referenced blocks, access and locations with TIA's cross-reference view. Return the JSON or any mismatch. An unused object may correctly return an empty result.
3. Optionally List blocks and select a used block in Cross-reference object to compare the block route too. No source export, project compile/save or online action is required.

The next bounded phase is the deliberate eleven-tool MCP cutover, reconciling tool definitions, validation, dispatch, tests and documentation together after this native check. Remaining evidence gaps must stay explicit rather than be presented as verified by publication.

## User-supplied Level meter result — 2026-09-21

The user supplied a response from process 34636 at 2026-09-21T16:08:08.4988477+00:00 with complete:true and errors:[]. Its source is Level meter, Real, %ID50, device PLC_100, path PLC_100\PLC tags\FIO, objectId X2WQjgcMm0GHsgwYSRIPEw==. That ID matches the Level meter entry in the earlier supplied FIO table response.

The source has no children and one reference: Main, LAD-Organization block, %OB1, device PLC_100, path PLC_100\Program blocks, objectId k0Btmfp9aUmUomwUpAZNsA==. That ID matches Main in the earlier PLC_100 block inventory. The reference has one location: referenceType:UsedBy, access:Read, referenceLocation:"@Main ▶ NW1". The response therefore reports that Main reads Level meter in network 1.

The location's name, typeName, address and referencedAsName are empty strings; referencedAsObjectId is null. These fields remain exactly as returned and do not, by themselves, indicate an incomplete result. No identity is inferred for the location.

This provides native response evidence for the individual-tag route, source/reference identifiers and one UsedBy/Read location. The user supplied JSON without explicitly confirming comparison with TIA's cross-reference view; do not claim independent source-code or view verification, universal service support, multiple-source/child coverage, other reference/access kinds or populated constants. No routine replay of this successful tag query is needed. The next implementation phase remains the eleven-tool MCP cutover, with these evidence limits retained.
