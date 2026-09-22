# Native cross-reference increment — 2026-09-21

`get_cross_references` is a published MCP tool backed by the shared guarded service; the dashboard calls it through `/mcp`. See [read contracts](project-rehaul.md). The older POST `/api/prototype/cross-references` route remains an implementation detail. Dated checks below preserve the original reader increment and subsequent MCP replay; they are not current runtime state or a new task.

## Implemented contract

The request accepts only `{processId, objectId}`. Selectors are required and opaque; filters, path/source flags, names and extra/duplicate fields are rejected. The service captures the existing attachment ticket before queueing on the shared STA worker. It resolves the target directly through the retained project's ObjectIdentifierProvider.Find and asks that object's IEngineeringServiceProvider for CrossReferenceService. A missing object returns objectNotFound; absent provider/service returns unsupportedObject. Applicability has no extra bridge type allowlist.

The native query is GetCrossReferences(CrossReferenceFilter.AllObjects). Its result preserves Sources → Children/References → Locations in native order. Source/reference fields retain Name, Path, TypeName, Device and Address. Their objectId comes only from a native UnderlyingObject that is an IEngineeringObject and can be identified. Location fields retain ReferenceType, Access, ReferenceLocation, Name, TypeName, Address and ReferencedAsName; referencedAsObjectId is obtained only from a native engineering ReferencedAs object. Enum names remain native; textual values are never fabricated into engineering objects. Native paths are not reconstructed or rewritten.

The adapter reads native values lazily on the STA and projects managed dictionaries. There is no inventory rebuild, source parsing/export, compilation, derived uses/usedBy graph, persistent index or project modification. Native errors preserve their messages and origin. Failed fields become null; failed collection acquisition becomes null with errors; successfully read empty collections are []. Interrupted enumeration retains readable earlier items and independent branches. A native query failure returns sources:null with errors. A null native result without an exception is reported as a bridge error, not a successful empty result.

The shared guard checks the retained context before/after the operation, around the native query, at collection boundaries and after failures. Context loss discards the payload and requires explicit reconnection. Ordinary unsupported-object/query errors do not themselves replace or invalidate a still-valid attachment.

## Dashboard workflow

The Cross-reference object selector uses already-read block and UDT inventories plus identified tags/user constants/system constants from the most recently read selected tag table. It always submits the selected object's own ID and processId. Entries with no ID are omitted from selectable candidates. A raw Object ID field allows other native objects without inventing a frontend support allowlist.

Changing the table clears its earlier entry choices; metadata-only table reads also clear those choices. Device/CPU changes and reconnection clear cross-reference selectors. Reading references does not automatically attach, retry, rebuild inventory or read block source.

## Historical verification — 2026-09-21

- [x] Staged net48/x64 Release build against installed V20 API: zero warnings/errors.
- [x] Offline harness: 62/62 groups. Five added groups cover strict requests, native hierarchy/path/order/enum retention, field and collection failures, empty/unavailable results and context loss. Existing stale-ticket and post-read-transition checks include cross-reference reads.
- [x] Dashboard script checks: 8/8, including own tag/constant IDs, block/UDT selection, ordinary unsupported-object response without reconnect, metadata-only entry clearing, CPU changes and reconnection clearing. These use mocked fetch/minimal DOM.
- [x] Architecture/source-boundary checks: 7/7, including direct service lookup, AllObjects, native underlying identities, pre-queue ticket capture, held publication and no export/inventory/compile path.
- [x] Normal Release build: zero warnings/errors. Managed PID 62172 exited gracefully; the helper loaded the single replacement PID 53216 on port 5000. Passive status reported implementationPhase:rehaul-cross-references, writeToolsAvailable:false, zero pending operations and no attachments. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server, including invalid/disconnected/cross-origin cross-reference requests, served Read cross-references control, unchanged attachments and the eight disabled MCP descriptors. No native query or TIA attachment was performed by the smoke check.
- [x] User supplied a successful native cross-reference response for Level meter; see the scoped evidence below.
- [x] The initial view-comparison gap was subsequently resolved for Level meter → Main NW1 by registered MCP replay and user confirmation, recorded below. The agent did not inspect the TIA UI independently. Offline/dashboard/HTTP checks alone provided no native query evidence.

The build validates installed API signatures, not actual service availability, ID resolvability, query freshness or fidelity for every object type. An entry having an ID does not establish cross-reference support. Populated tag-table constants and broader lifecycle/unit/protection scenarios retain their previously documented evidence limits.

## Historical comparison procedure

The targeted comparison was completed as recorded below. This procedure is retained for a relevant regression, not a request to repeat passed checks.

1. Refresh the dashboard, reconnect the test process, then List devices → Read device → List tag tables → choose FIO → Read tag table with Include entries enabled.
2. Under Cross-reference object, choose a tag that is used in the project (for example Start Button if it is used), then Read cross-references. Compare the referenced blocks, access and locations with TIA's cross-reference view. Return the JSON or any mismatch. An unused object may correctly return an empty result.
3. Optionally List blocks and select a used block in Cross-reference object to compare the block route too. No source export, project compile/save or online action is required.

The MCP cutover is complete. Remaining native evidence gaps stay explicit and are not resolved merely by publishing the tool.

## User-supplied Level meter result — 2026-09-21

The user supplied a response from process 34636 at 2026-09-21T16:08:08.4988477+00:00 with complete:true and errors:[]. Its source is Level meter, Real, %ID50, device PLC_100, path PLC_100\PLC tags\FIO, objectId X2WQjgcMm0GHsgwYSRIPEw==. That ID matches the Level meter entry in the earlier supplied FIO table response.

The source has no children and one reference: Main, LAD-Organization block, %OB1, device PLC_100, path PLC_100\Program blocks, objectId k0Btmfp9aUmUomwUpAZNsA==. That ID matches Main in the earlier PLC_100 block inventory. The reference has one location: referenceType:UsedBy, access:Read, referenceLocation:"@Main ▶ NW1". The response therefore reports that Main reads Level meter in network 1.

The location's name, typeName, address and referencedAsName are empty strings; referencedAsObjectId is null. These fields remain exactly as returned and do not, by themselves, indicate an incomplete result. No identity is inferred for the location.

This initial response provides native evidence for the individual-tag route, matching identifiers and one UsedBy/Read location. At that point the user had supplied JSON without a TIA-view comparison; the subsequent confirmation below resolves that specific gap. Universal service support, multiple-source/child coverage, other reference/access kinds and populated constants are not established. No routine replay of the successful tag query is needed.

## Registered MCP replay and user confirmation — 2026-09-21

Following the eleven-tool cutover, the agent queried the same Level meter tag through the registered MCP client at 16:40:05Z. It returned the same Main / UsedBy / Read / NW1 relationship and matching source/reference IDs, with complete:true/errors:[]. See [registered-client evidence](../reference/history/rehaul-mcp-cutover.md).

After being asked to confirm this relationship against TIA's cross-reference view, the user replied: "Yes the crossreference is correct". This completes the targeted comparison as agent-executed MCP evidence plus user-confirmed TIA-view evidence. It supersedes the earlier unconfirmed-view status for this specific relationship. The agent did not inspect the TIA UI independently; broader object types, child hierarchies, access kinds and populated constants remain outside this verification. No further routine replay of this check is needed.
