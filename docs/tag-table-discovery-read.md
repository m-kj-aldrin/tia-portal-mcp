# Tag-table discovery and typed entries — 2026-09-21

The dashboard implements `list_tag_tables` and `get_tag_table` through POST `/api/prototype/tag-tables` and `/api/prototype/tag-table`. Both readers are now also published through MCP; see [current cutover](rehaul-mcp-cutover.md). The checks and runtime snapshots below record the earlier reader increment, before publication.

## Implemented behavior

`list_tag_tables({processId, plcObjectId})` resolves the CPU DeviceItem owning PlcSoftware directly through the retained project's ObjectIdentifierProvider. It visits native TagTableGroup/Groups/TagTables and software/safety-unit scopes, preserving names, composition order and native table IDs. The root group is system-owned; that does not classify its tables as system tables or imply that a default table is a system table. Table safety classification follows its native unit scope. The V20 public tag-table group API has no separate SystemTagTableGroups composition.

The shared inventory walker returns scope/tagTableGroup nodes and lightweight tagTable leaves. It reads no entries, detailed attributes, block/UDT inventories or source documents. Partial hierarchy reads retain independent readable branches with errors.

`get_tag_table({processId, objectId, includeEntries:true, includePath:true})` resolves one PlcTagTable directly. Both booleans default to true. Unknown or duplicate fields, invalid selectors and source-format/dependency options are rejected. One bulk GetAttributes(ReadOnly | ReadWrite) supplies table metadata: name, isDefault, timestamps.modified from native ModifiedTimeStamp, and remaining typeSpecific attributes. No duplicate typed-property reads repair missing attributes. Missing values remain null. Optional path reconstruction uses the bulk Name and native ancestor names; includePath:false skips it.

Entries come from the native typed Tags, UserConstants and SystemConstants compositions, in separate arrays and their native order. Each native entry supplies its own ObjectIdentifierProvider ID and one bulk attribute read. Name and DataTypeName map to name/dataType; tags map LogicalAddress to logicalAddress; constants map Value to value. Other readable attributes remain in typeSpecific after shared JSON conversion. Comment is excluded with the existing multilingual-content boundary; native proxies are never serialized or traversed as JSON.

If native identifier access fails, objectId is null and its exact native error remains visible while readable fields survive. Empty/blank native IDs normalize to null without inventing a replacement. An ID does not establish cross-reference service support. Unavailable collections return null with errors; successfully read empty collections return []. Interrupted enumeration retains earlier entries. Failures in one composition do not prevent reading the other independent compositions while the context is valid.

With includeEntries:false, entries is explicitly null and no entry composition, entry attributes or identifier is accessed. Omitting entries intentionally does not make complete false. No export, XML parsing, source packet, checksum, import, compilation or project modification is involved.

The existing guard captures the attachment ticket before STA queueing and rechecks process/project context before and after the request, at collection boundaries and after failures. Context loss aborts traversal, discards the payload and requires explicit reconnection. A missing or wrong-type target does not invalidate a still-valid attachment.

The dashboard adds List tag tables, a named table selector, Read tag table, Include entries and Include tag table path. The normal workflow requires no manual ID entry. Changing Device/CPU or reconnecting clears table choices; block/UDT and table choices remain independent within the same CPU.

## Verification

- [x] Staged net48/x64 Release build against installed V20 API: zero warnings/errors.
- [x] Offline harness: 57/57 groups. Eight added groups cover strict requests, lightweight inventory, metadata, entry mapping/IDs/order, metadata-only, partial/empty/unavailable collections, field failures and context loss. Existing stale-ticket and post-read transition cases now include both tag-table readers.
- [x] Dashboard script tests: 7/7 with mocked fetch/minimal DOM, including exact typed-read options, independent UDT choice, CPU-change clearing and reconnection clearing.
- [x] Architecture/source-boundary tests: 6/6, including native tag/constant compositions, direct lookup/entry identifiers, bulk metadata, no export/writes, held MCP publication and pre-queue tickets.
- [x] Normal Release build: zero warnings/errors. Managed PID 87980 exited gracefully; the helper loaded the single replacement PID 62172 on port 5000. Passive status reported implementationPhase:rehaul-tag-table-read, writeToolsAvailable:false, zero pending operations and no attachments. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server. Invalid/source-option requests, disconnected selections and cross-origin calls are rejected; both tag-table controls are served; MCP publication remains held. These checks did not attach to TIA or read native tables.
- [x] User reported the FIO table looks correct and supplied entry-enabled and metadata-only responses; see the scoped evidence below.
- [ ] Populated user/system constant values and their native identifier availability remain untested by this fixture.

Build/offline checks alone do not establish native fidelity. The supplied FIO response below provides scoped evidence for tag fields and identifiers. Populated constants, software/safety-unit cases, permission failures and cross-reference applicability remain pending live evidence.

## Short user comparison after reload

1. Refresh the dashboard and reconnect the existing TIA process. Use List devices → Read device → List tag tables and compare the hierarchy with TIA.
2. Choose an existing populated table and Read tag table. Compare tag names, data types and addresses. Inspect entries.tags, entries.userConstants and entries.systemConstants, their objectId fields and errors. Empty constant collections are valid if the fixture has no constants; that does not test populated constant reads.
3. Clear Include entries and Include tag table path, then read again. Expect entries:null and metadata.path:null, with metadata retained.

Return the result or any mismatch. No save, import, compile, download or fixture modification is needed. Do not repeat already-passed block and UDT checks merely to verify this increment.

## User-supplied FIO results — 2026-09-21

The user supplied two responses for process 34636, table FIO, objectId dzT+iCgGkUmAV/I+y2kv7g==, and reported "looks correct". This is user-executed native evidence, not an agent-replayed TIA comparison.

- At 15:50:51.5229007Z, the full response has path PLC_100/PLC tags/FIO and 15 tags: eight Bool, five Real and two DInt. Every tag has a nonblank distinct native objectId, and none equals the table ID. Fields include name, dataType, logicalAddress and native ExternalAccessible/ExternalVisible/ExternalWritable/IsSafety attributes in typeSpecific. For example, Start Button is Bool at %I5.0, Level meter is Real at %ID50 and Display Present Value (PV) is DInt at %QD62.
- Both constant collections are empty arrays, so this demonstrates readable empty collections, not populated constant field or identifier support.
- At 15:50:59.9130106Z, the metadata-only response has entries:null and metadata.path:null. Its objectId, name FIO, isDefault:false, modified timestamp 2026-09-21T11:40:59.5092764Z and empty typeSpecific match the full response.
- Both responses have complete:true and errors:[]. The supplied results confirm the requested output behavior; they do not independently trace skipped native calls or prove cross-reference service support for these tag IDs.

No separate inventory-tree payload was supplied. Record the user's overall successful workflow report without claiming exhaustive native group, field or ordering verification. No routine replay of the FIO read is needed. get_cross_references is now implemented; see [checks and pending native comparison](cross-references.md). The full eleven-tool MCP cutover follows.
