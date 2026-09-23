# Tag-table discovery and typed entries

`list_tag_tables` and `get_tag_table` are published MCP tools backed by the shared guarded service; the dashboard calls them through `/mcp`. See [read contracts](project-rehaul.md). Engineering reads are dispatched through `/mcp`; separate dashboard tag-table read routes are not exposed. Scoped verification is tracked in the [evidence index](evidence.md).

## Implemented behavior

`list_tag_tables({processId, plcObjectId})` resolves the CPU DeviceItem owning PlcSoftware directly through the retained project's ObjectIdentifierProvider. It visits native TagTableGroup/Groups/TagTables and software/safety-unit scopes, preserving names, composition order and native table IDs. The root group is system-owned; that does not classify its tables as system tables or imply that a default table is a system table. Table safety classification follows its native unit scope. The V20 public tag-table group API has no separate SystemTagTableGroups composition.

The shared inventory walker returns scope/tagTableGroup nodes and lightweight tagTable leaves. It reads no entries, detailed attributes, block/UDT inventories or source documents. Partial hierarchy reads retain independent readable branches with errors.

`get_tag_table({processId, objectId, includeEntries:true, includePath:true})` resolves one PlcTagTable directly. Both booleans default to true. Unknown or duplicate fields, invalid selectors and source-format/dependency options are rejected. One bulk GetAttributes(ReadOnly | ReadWrite) supplies table metadata: name, isDefault, timestamps.modified from native ModifiedTimeStamp, and remaining typeSpecific attributes. No duplicate typed-property reads repair missing attributes. Missing values remain null. Optional path reconstruction uses the bulk Name and native ancestor names; includePath:false skips it.

Entries come from the native typed Tags, UserConstants and SystemConstants compositions, in separate arrays and their native order. Each native entry supplies its own ObjectIdentifierProvider ID and one bulk attribute read. Name and DataTypeName map to name/dataType; tags map LogicalAddress to logicalAddress; constants map Value to value. Other readable attributes remain in typeSpecific after shared JSON conversion. Comment is excluded with the existing multilingual-content boundary; native proxies are never serialized or traversed as JSON.

If native identifier access fails, objectId is null and its exact native error remains visible while readable fields survive. Empty/blank native IDs normalize to null without inventing a replacement. An ID does not establish cross-reference service support. Unavailable collections return null with errors; successfully read empty collections return []. Interrupted enumeration retains earlier entries. Failures in one composition do not prevent reading the other independent compositions while the context is valid.

With includeEntries:false, entries is explicitly null and no entry composition, entry attributes or identifier is accessed. Omitting entries intentionally does not make complete false. No export, XML parsing, source packet, checksum, import, compilation or project modification is involved.

Native XML export is now available through the separate `export_tag_table({processId, objectId})` read tool. It calls `PlcTagTable.Export`, returns exact native SimaticML text and returned-content checksums, and leaves this typed detail contract unchanged. See [export contract and evidence](compile-delete-export.md#tag-table-export).

The existing guard captures the attachment ticket before STA queueing and rechecks process/project context before and after the request, at collection boundaries and after failures. Context loss aborts traversal, discards the payload and requires explicit reconnection. A missing or wrong-type target does not invalidate a still-valid attachment.

The dashboard adds List tag tables, a named table selector, Read tag table, Include entries and Include tag table path. The normal workflow requires no manual ID entry. Changing Device/CPU or reconnecting clears table choices; block/UDT and table choices remain independent within the same CPU.

## Evidence

User-supplied FIO results cover 15 tags and metadata-only behavior. Later native acceptance verifies populated user-constant values, native IDs and editing; the lifecycle suite also verifies populated-table XML export/import with typed readback. Populated system constants remain unverified.

<a id="historical-verification--2026-09-21"></a>
<a id="historical-comparison-procedure"></a>
<a id="user-supplied-fio-results--2026-09-21"></a>

The original dated record is preserved in [tag-table reader verification](../reference/history/tag-table-reader-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
