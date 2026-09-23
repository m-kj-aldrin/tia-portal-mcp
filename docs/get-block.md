# Individual block metadata and source

`get_block` is a published MCP read tool backed by the shared guarded service. Its contract is defined in [read contracts](project-rehaul.md#get_block). The dashboard calls it through `/mcp`. Scoped verification is tracked in the [evidence index](evidence.md).

## Scope and contract

`get_block` accepts processId and objectId, plus includeSource (default true), includePath (default true), sourceFormat (default best) and includeDependencies (default false). Unknown/duplicate parameters, wrong types and dependency requests without source plus explicit external-source are rejected. Identifiers are opaque. Engineering reads are dispatched through `/mcp`; there is no separate dashboard block-read route.

The service captures the existing connection ticket before queueing. The native reader resolves the block directly through the retained project's ObjectIdentifierProvider and checks PlcBlock type. The existing guard rejects stale tickets and project changes before/after reading. No inventory rebuild, implicit attachment or retry against replacement projects occurs.

One GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite) reads metadata. Known fields map to name, blockType, number, autoNumber, namespace, programmingLanguage, memoryLayout, header, state and timestamps; other values retain their native names in typeSpecific. HeaderName means header.userDefinedId, not title. Missing values remain null; a failed bulk operation produces an error without secondary typed-property reads. Runtime class supplies blockType; the selector supplies native identity. Unknown complex attribute values remain explicit type markers, never live proxies. Header Version values use their native Version string. Native field coverage remains limited to the recorded fixtures.

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

## Evidence

Recorded checks cover the tested metadata/read modes and best-format selection. Later native import tests add exact returned-content checksum and semantic readback assertions for their authored fixtures.

<a id="historical-verification-and-runtime--2026-09-21"></a>
<a id="historical-comparison-procedure"></a>
<a id="native-enum-conversion-correction--2026-09-21"></a>
<a id="user-confirmation-after-enum-correction--2026-09-21"></a>

The original dated record is preserved in [block reader verification](../reference/history/block-reader-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
