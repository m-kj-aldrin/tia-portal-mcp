# UDT discovery and details

`list_udts` and `get_udt` are published MCP tools backed by the shared guarded service; the dashboard calls them through `/mcp`. See [read contracts](project-rehaul.md). Engineering reads are dispatched through `/mcp`; separate dashboard UDT routes are not exposed. Scoped verification is tracked in the [evidence index](evidence.md).

## Implemented behavior

`list_udts({processId, plcObjectId})` resolves the CPU DeviceItem owning PlcSoftware through the retained project's ObjectIdentifierProvider. It traverses only native type compositions: PLC TypeGroup, nested user groups, SystemTypeGroups, software units and safety units. V20 PlcSystemTypeGroup has Types but no nested Groups composition. The unnamed unit container introduces no invented path segment. Native composition order, names and identifiers are retained. Independent readable branches survive enumeration errors with complete:false and exact errors.

The shared inventory walker returns scope/typeGroup nodes and lightweight udt leaves with objectId, name, path, isSystem and isSafety. A system-owned root type container does not classify its user types as system types; actual system-group types do carry isSystem:true. Safety classification follows native safety-unit ownership, not names. No block fields, type members or exports are read by the inventory.

`get_udt` accepts processId, objectId, includeSource (default true), includePath (default true), sourceFormat (default best) and includeDependencies (default false). It resolves one PlcType directly, performs one bulk GetAttributes(ReadOnly | ReadWrite), and maps native attributes into name, namespace, state, timestamps and remaining typeSpecific fields. It imposes no block header. Missing values remain null; no duplicate typed-property fetch repairs missing bulk fields. The UDT's own path segment comes from the bulk Name. Disabling includePath skips optional reconstruction; external-source export independently locates the owning PLC/unit source group when source is requested.

The current implementation shares BlockReadRequest, BlockRead, BlockSource and BlockInventory with the block readers. UdtMetadata and the native UDT adapters own type-specific behavior. These internal names and file locations are not constraints on restructuring; the UDT JSON does not acquire block-only fields through this reuse.

Source policy:

- Best: native GenerateSource to `.udt`, then PlcType.ExportAsDocuments (SIMATIC SD), then PlcType.Export (SimaticML).
- Explicit format: one attempt, no fallback. Dependencies require source enabled and explicit external-source.
- Metadata-only: source:null and no export. Native protection does not prefilter attempts; no unlocking is performed.
- Both readers use OpennessSourceExporter for short owned temporary directories, absolute DirectoryInfo construction, native SD success checking, exact returned content/checksums and cleanup. Existing block file names and routing are preserved. SD PartialSuccess is rejected. All failures retain metadata and return source:null/errors; successful fallback keeps earlier failures in dashboard events only.
- The shared guard captures the existing attachment ticket before queueing and validates context before/after reads, at traversal boundaries and after failures. Context loss aborts fallback and discards the result. No implicit reconnect, import, save, compile or project mutation occurs.

The dashboard offers List UDTs, a named UDT selector and separate UDT source/path/dependency controls. CPU/device changes and reconnection clear earlier UDT selections. Switching between block and UDT inventories preserves the other valid selection for the same CPU.

## Evidence

The original discovery/best-source/metadata-only workflow was user-confirmed. Later native acceptance verified UDT external source, explicit SIMATIC SD and SimaticML export/import/readback for the recorded fixtures. Dependency export, unusual unit/protection cases and native failure cleanup remain outside that evidence.

<a id="historical-verification--2026-09-21"></a>
<a id="historical-comparison-procedure"></a>
<a id="user-confirmation--2026-09-21"></a>

The original dated record is preserved in [udt reader verification](../reference/history/udt-reader-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
