# UDT discovery and detail increment — 2026-09-21

The dashboard now implements the settled `list_udts` and `get_udt` reads via POST `/api/prototype/udts` and `/api/prototype/udt`. MCP publication remains held at eight disabled V1 descriptors; the eleven-tool cutover is still pending. Tag tables and cross-references follow this increment.

## Implemented behavior

`list_udts({processId, plcObjectId})` resolves the CPU DeviceItem owning PlcSoftware through the retained project's ObjectIdentifierProvider. It traverses only native type compositions: PLC TypeGroup, nested user groups, SystemTypeGroups, software units and safety units. V20 PlcSystemTypeGroup has Types but no nested Groups composition. The unnamed unit container introduces no invented path segment. Native composition order, names and identifiers are retained. Independent readable branches survive enumeration errors with complete:false and exact errors.

The shared inventory walker returns scope/typeGroup nodes and lightweight udt leaves with objectId, name, path, isSystem and isSafety. A system-owned root type container does not classify its user types as system types; actual system-group types do carry isSystem:true. Safety classification follows native safety-unit ownership, not names. No block fields, type members or exports are read by the inventory.

`get_udt` accepts processId, objectId, includeSource (default true), includePath (default true), sourceFormat (default best) and includeDependencies (default false). It resolves one PlcType directly, performs one bulk GetAttributes(ReadOnly | ReadWrite), and maps native attributes into name, namespace, state, timestamps and remaining typeSpecific fields. It imposes no block header. Missing values remain null; no duplicate typed-property fetch repairs missing bulk fields. The UDT's own path segment comes from the bulk Name. Disabling includePath skips optional reconstruction; external-source export independently locates the owning PLC/unit source group when source is requested.

BlockReadRequest, BlockRead, BlockSource and BlockInventory retain their existing internal names and serve as shared transport envelopes/policy components. UdtMetadata and the two native UDT adapters own type-specific behavior. This reuse does not add block-only fields to UDT JSON.

Source policy:

- Best: native GenerateSource to `.udt`, then PlcType.ExportAsDocuments (SIMATIC SD), then PlcType.Export (SimaticML).
- Explicit format: one attempt, no fallback. Dependencies require source enabled and explicit external-source.
- Metadata-only: source:null and no export. Native protection does not prefilter attempts; no unlocking is performed.
- Both readers use OpennessSourceExporter for short owned temporary directories, absolute DirectoryInfo construction, native SD success checking, exact returned content/checksums and cleanup. Existing block file names and routing are preserved. SD PartialSuccess is rejected. All failures retain metadata and return source:null/errors; successful fallback keeps earlier failures in dashboard events only.
- The shared guard captures the existing attachment ticket before queueing and validates context before/after reads, at traversal boundaries and after failures. Context loss aborts fallback and discards the result. No implicit reconnect, import, save, compile or project mutation occurs.

The dashboard offers List UDTs, a named UDT selector and separate UDT source/path/dependency controls. CPU/device changes and reconnection clear earlier UDT selections. Switching between block and UDT inventories preserves the other valid selection for the same CPU.

## Verification

- [x] Staged net48/x64 Release build against installed V20: zero warnings/errors.
- [x] Offline harness: 49/49 groups. New UDT groups cover partial hierarchy, lightweight leaves, metadata preservation, all three fallback positions, strict failure, metadata-only and context loss. Existing stale-ticket and post-read transition tests now exercise both UDT routes.
- [x] Dashboard script tests: 6/6 with mocked fetch/minimal DOM, including UDT request flags, independent block state, CPU-change clearing and reconnection clearing.
- [x] Source-boundary tests: 5/5, including UDT direct lookup, one bulk attribute call, shared export and held MCP publication.
- [x] Normal Release build: zero warnings/errors. Previous managed PID 59320 exited gracefully. The first sandboxed launch failed at HttpListener construction and subsequently exited; the lifecycle helper confirmed stopped before a successful launch outside the sandbox. The single managed server started as PID 87980 on port 5000, read-only, implementationPhase:rehaul-udt-read. Passive status showed zero pending operations and no attachments. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server. Both UDT routes reject invalid/disconnected/cross-origin requests; the served HTML contains List UDTs and Read UDT; the held MCP descriptors remain unchanged and unpublished UDT names return unknownTool. Attachments were unchanged.
- [x] User reported the requested UDT check works; see the scoped confirmation below. No native UDT read/export was performed by the offline or HTTP checks.

Build and offline checks establish implementation behavior, not native UDT field coverage, source fidelity, protected/system/safety-unit coverage or live export-failure behavior.

## Short user check after reload

1. Refresh the dashboard, reconnect the existing test process, then List devices → Read device → List UDTs. Compare the type names/groups with TIA.
2. Choose an existing UDT (for example T_Scale_Config if present) and Read UDT with best. Expect external-source and a `.udt` document if native generation permits it; compare its declaration with TIA. Check complete/errors and metadata.
3. Clear Include UDT source and Include UDT path, then read again. Expect source:null and metadata.path:null, with metadata retained.

Return the result or any mismatch. No project changes or repeat of earlier block-format checks is required. Native explicit SD/ML, dependencies and unusual unit/protection cases remain separately unverified until exercised.

## User confirmation — 2026-09-21

After the loaded-build test instructions for UDT discovery, best-source reading and metadata-only/path-disabled reading, the user replied: "yes it works". Record this as user-reported success of the requested workflow. No response payload or separate per-step details were supplied; this does not independently verify exact format/content, every metadata field, checksums or all native export branches. No routine repeat of the same check is required.

Next bounded increment: list_tag_tables and get_tag_table, using native typed tag/constant compositions and identifiers under the settled contract. Cross-references and the complete eleven-tool MCP cutover follow.
