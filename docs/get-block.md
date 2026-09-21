# Individual block metadata and source

Implemented 2026-09-21 after the user-reported block-tree comparison passed for PLC_100 and PLC_101. User-supplied native evidence and its limits are recorded below. This page records the original dashboard increment; get_block is now also published through the [eleven-tool MCP cutover](rehaul-mcp-cutover.md).

## Scope and contract

POST /api/prototype/block accepts processId and objectId, plus includeSource (default true), includePath (default true), sourceFormat (default best) and includeDependencies (default false). Unknown/duplicate parameters, wrong types and dependency requests without source plus explicit external-source are rejected. Identifiers are opaque.

The service captures the existing connection ticket before queueing. The native reader resolves the block directly through the retained project's ObjectIdentifierProvider and checks PlcBlock type. The existing guard rejects stale tickets and project changes before/after reading. No inventory rebuild, implicit attachment or retry against replacement projects occurs.

One GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite) reads metadata. Known fields map to name, blockType, number, autoNumber, namespace, programmingLanguage, memoryLayout, header, state and timestamps; other values retain their native names in typeSpecific. HeaderName means header.userDefinedId, not title. Missing values remain null; a failed bulk operation produces an error without secondary typed-property reads. Runtime class supplies blockType; the selector supplies native identity. Unknown complex attribute values remain explicit type markers, never live proxies. Header Version values use their native Version string. Actual native field coverage remains exploratory pending the test below.

When includePath is true, ancestors reconstruct the same named path convention as List blocks, omitting the unnamed unit/provider containers. No siblings are enumerated. The block's own name comes from bulk metadata, without re-fetching block.Name. Optional path failure returns null after validating context. includePath:false skips this path reconstruction. External-source export still independently follows ancestors to locate its owning PLC or unit source group; metadata-only with paths disabled does neither traversal nor export.

## Source behavior

- SCL, DB and STL: external source first, then SimaticML. Extensions are .scl, .db and .awl respectively.
- LAD: SIMATIC SD first, then SimaticML. The native LAD attribute alone does not prove pure networks; native rejection/PartialSuccess triggers best fallback without parsing source to classify networks.
- FBD, GRAPH, mixed/unlisted/unknown language: SimaticML.
- Explicit format: exactly one attempt, no fallback. Protected blocks are not prefiltered and no passwords/unlocking are attempted.

Native calls are GenerateSource with GenerateOptions.None/WithDependencies, ExportAsDocuments and Export with WithReadOnly. These write only temporary export files; no engineering object is created/imported, compiled, saved or changed. A short, unique directory under the OS temporary folder belongs to each attempt. Returned files must be inside it. Cleanup checks the owned absolute path and link attributes; failures appear in the dashboard events rather than overwrite the source result.

SIMATIC SD must return Success; PartialSuccess is rejected with its exact native messages and a separately labeled bridge explanation. On success, native state/messages and all returned documents are preserved. Files are read using BOM-aware decoding with strict UTF-8 as the no-BOM default; text and line endings are not normalized. SHA-256 covers each exact returned content string encoded as UTF-8 without a BOM. No document checksum is produced for metadata-only reads.

The source packet identifies the actual format and dependency flag. contentScope:native-permitted-content means only what the native exporter allowed; it is not a claim that protected internals were exposed. Protection state remains native metadata. No XML/source parsing, derived summaries or source equality claims are introduced.

If all attempts fail, metadata survives, source is null and every failure is in errors with format/origin/native message. A successful later fallback has no earlier attempt errors in its response; those attempts remain in the selected connection's dashboard events. Context loss aborts export/fallback and discards the payload. Local I/O/decoding failures are bridge-origin, native exceptions are tia-openness.

## Verification and runtime

- [x] Staged net48/x64 Release build against installed V20 API: zero warnings/errors.
- [x] Offline harness: 44/44 groups, including ten new metadata/source policy groups and existing stale-ticket/transition scenarios extended to ReadBlock.
- [x] Dashboard mocked-fetch/minimal-DOM checks: 5/5; automatic station/CPU selection, named block choice, source options and reconnection clearing.
- [x] Source boundary checks: 4/4; direct lookup, one bulk metadata call, held MCP surface, reference isolation and no engineering writes.
- [x] Normal Release build: zero warnings/errors. The previous managed PID 83620 exited gracefully; the helper started the single replacement PID 45940 on port 5000. Passive status identified rehaul-block-read, read-only, zero pending operations and no connections. Process inspection found one TiaPortalDashboard executable, belonging to this checkout. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server, including invalid/disconnected get_block requests, held MCP publication and served Read block UI. No TIA attachment or native export was performed by these checks.
- [x] User reported the corrected best-format selection for tested SCL, LAD and DB blocks; see the confirmation below. Full exported-content/attribute fidelity, protected blocks, mixed networks, unit paths and cleanup under native failure remain unverified.

## Short live comparison

After the new build is confirmed running, refresh the dashboard and reconnect the existing test process. Use List devices → Read device → List blocks; single station and CPU choices fill automatically.

1. Choose Scale_To_Actual, leave Include source enabled and source format best, then Read block. Compare metadata with TIA; expected source format is external-source with .scl text and checksum.
2. Choose Main (LAD) and Read block. Expect SIMATIC SD if this fixture's networks are supported; otherwise best may use SimaticML. Check source.format, source.documents and errors.
3. Choose GLOBAL (DB) and Read block. Compare its metadata and .db content; SimaticML fallback remains permitted if the native source generator refuses it.
4. Clear Include source and Include block path and read once more: source and metadata.path should both be null; metadata remains present.

Report any error/mismatch and the returned format. No save, compile, import, project close/reopen or online action is needed. Do not repeat already-passed connection and block-tree tests beyond obtaining current choices after the server reload.

## Native enum conversion correction — 2026-09-21

The user supplied Scale_To_Actual (process 34636, objectId LXkYAT+gFUqWR2TtZ9OsKA==) responses at 14:42:40Z (metadata-only, path:null) and 14:46:12Z (source included, constructed path correct). Bulk metadata had scalar values and native attribute names, but ProgrammingLanguage and MemoryLayout were represented as Siemens.Engineering.Contract.EnumToClientRepresentation markers. The latter response returned SimaticML even though the block is SCL. This was a bridge conversion/routing defect: the wrapper was not a CLR Enum, so the language string was absent and best selected its unknown-language SimaticML route. This was not evidence that external-source export had been attempted and failed.

Inspection of the installed V20 contract found a public value struct with public string Type/Value properties. Conversion now narrowly recognizes this value struct and reads its public Value, without a private-member access, extra attribute fetch, XML parsing, generic proxy stringification or new Siemens assembly reference. Other complex objects still receive the explicit unserialized marker.

- Regression harness: 45/45 groups passed, including wrapped SCL/LAD/STL/FBD values through conversion, metadata mapping and best routing, plus MemoryLayout.
- Separate local probe constructed values using the actual installed Siemens.Engineering.Contract type and invoked production conversion/routing: SCL and Optimized survived; best chose external-source then simatic-ml. This was an in-memory contract check, not a TIA attachment or a live export.
- Staged and normal Release builds: zero warnings/errors. Managed PID 45940 stopped gracefully; the corrected single server started as PID 59320 on port 5000. HTTP smoke: 1/1 passed with no attachment.
- The user subsequently reported successful best-format results for SCL, LAD and DB, recorded below. Native export failure may still legitimately fall back, with earlier attempts retained in dashboard events.

## User confirmation after enum correction — 2026-09-21

The user reported: "best return scl for scl and lad returns simatic-sd, db returns external-source". This is user-executed confirmation of the expected format choice for the three tested block categories after the corrected server was loaded. No new response payloads were supplied with this confirmation, so it does not establish independent checksum validation, complete source-content comparison, specific returned attribute values or broader language/protection coverage.

The earlier supplied metadata-only response already showed source:null and metadata.path:null with metadata retained. The basic requested read modes and best-format checks are therefore covered at this scoped evidence level. No further routine replay of these same checks is required before the next increment.

Subsequent increment: list_udts and get_udt are now implemented using the settled PlcType contracts; see [UDT scope and verification](udt-discovery-read.md). Block and UDT exports share OpennessSourceExporter; block routing and output naming are preserved. Tag tables, cross-references and the [eleven-tool MCP cutover](rehaul-mcp-cutover.md) are also implemented; their evidence is documented separately.
