# Native cross-references

`get_cross_references` is a published MCP tool backed by the shared guarded service; the dashboard calls it through `/mcp`. See [read contracts](project-rehaul.md). Engineering queries are dispatched through `/mcp`; there is no separate dashboard cross-reference route. Scoped verification is tracked in the [evidence index](evidence.md).

## Implemented contract

The [expanded V20 acceptance suite](native-cross-reference-acceptance.md) tests a controlled fixture graph beyond the original single-tag cases. Its run instructions are separate from the recorded native results.

The request accepts only `{processId, objectId}`. Selectors are required and opaque; filters, path/source flags, names and extra/duplicate fields are rejected. The service captures the existing attachment ticket before queueing on the shared STA worker. It resolves the target directly through the retained project's ObjectIdentifierProvider.Find and asks that object's IEngineeringServiceProvider for CrossReferenceService. A missing object returns objectNotFound; absent provider/service returns unsupportedObject. Applicability has no extra bridge type allowlist.

The native query is GetCrossReferences(CrossReferenceFilter.AllObjects). Its result preserves Sources → Children/References → Locations in native order. Source/reference fields retain Name, Path, TypeName, Device and Address. Their objectId comes only from a native UnderlyingObject that is an IEngineeringObject and can be identified. Location fields retain ReferenceType, Access, ReferenceLocation, Name, TypeName, Address and ReferencedAsName; referencedAsObjectId is obtained only from a native engineering ReferencedAs object. Enum names remain native; textual values are never fabricated into engineering objects. Native paths are not reconstructed or rewritten.

The adapter reads native values lazily on the STA and projects managed dictionaries. There is no inventory rebuild, source parsing/export, compilation, derived uses/usedBy graph, persistent index or project modification. Native errors preserve their messages and origin. Failed fields become null; failed collection acquisition becomes null with errors; successfully read empty collections are []. Interrupted enumeration retains readable earlier items and independent branches. A native query failure returns sources:null with errors. A null native result without an exception is reported as a bridge error, not a successful empty result.

The shared guard checks the retained context before/after the operation, around the native query, at collection boundaries and after failures. Context loss discards the payload and requires explicit reconnection. Ordinary unsupported-object/query errors do not themselves replace or invalidate a still-valid attachment.

## Dashboard workflow

The Cross-reference object selector uses already-read block and UDT inventories plus identified tags/user constants/system constants from the most recently read selected tag table. It always submits the selected object's own ID and processId. Entries with no ID are omitted from selectable candidates. A raw Object ID field allows other native objects without inventing a frontend support allowlist.

Changing the table clears its earlier entry choices; metadata-only table reads also clear those choices. Device/CPU changes and reconnection clear cross-reference selectors. Reading references does not automatically attach, retry, rebuild inventory or read block source.

## Evidence

The original Level meter relationship was replayed through MCP and confirmed by the user against TIA. The later V20 fixture suite verifies repeated accesses, a user constant, calls, nested DB members, declaration relationships and freshness after source edits/deletion. Special safety/protection and software-unit cases remain outside this evidence.

<a id="historical-verification--2026-09-21"></a>
<a id="historical-comparison-procedure"></a>
<a id="user-supplied-level-meter-result--2026-09-21"></a>
<a id="registered-mcp-replay-and-user-confirmation--2026-09-21"></a>
<a id="expanded-automated-native-verification--2026-09-23"></a>

The original dated record is preserved in [cross-reference verification](../reference/history/cross-reference-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
