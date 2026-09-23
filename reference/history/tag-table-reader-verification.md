# Tag-table reader verification

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

## Historical verification — 2026-09-21

- [x] Staged net48/x64 Release build against installed V20 API: zero warnings/errors.
- [x] Offline harness: 57/57 groups. Eight added groups cover strict requests, lightweight inventory, metadata, entry mapping/IDs/order, metadata-only, partial/empty/unavailable collections, field failures and context loss. Existing stale-ticket and post-read transition cases now include both tag-table readers.
- [x] Dashboard script tests: 7/7 with mocked fetch/minimal DOM, including exact typed-read options, independent UDT choice, CPU-change clearing and reconnection clearing.
- [x] Architecture/source-boundary tests: 6/6, including native tag/constant compositions, direct lookup/entry identifiers, bulk metadata, no export/writes, held MCP publication and pre-queue tickets.
- [x] Normal Release build: zero warnings/errors. Managed PID 87980 exited gracefully; the helper loaded the single replacement PID 62172 on port 5000. Passive status reported implementationPhase:rehaul-tag-table-read, writeToolsAvailable:false, zero pending operations and no attachments. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server. Invalid/source-option requests, disconnected selections and cross-origin calls are rejected; both tag-table controls are served; MCP publication remains held. These checks did not attach to TIA or read native tables.
- [x] User reported the FIO table looks correct and supplied entry-enabled and metadata-only responses; see the scoped evidence below.
- [ ] Populated user/system constant values and their native identifier availability remain untested by this fixture.

Build/offline checks alone do not establish native fidelity. The supplied FIO response below provides scoped evidence for tag fields and identifiers. Populated constants, software/safety-unit cases, permission failures and cross-reference applicability remain pending live evidence.

## Historical comparison procedure

The user subsequently supplied successful FIO and metadata-only results, as recorded below. This is a reference procedure for relevant regressions or unverified cases. Populated constants remain unverified; that does not make the already-passed FIO check a new task.

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

No separate inventory-tree payload was supplied. Record the user's overall successful workflow report without claiming exhaustive native group, field or ordering verification. No routine replay of the FIO read is needed. Cross-references and MCP publication are implemented; see [cross-reference evidence](../../docs/cross-references.md) and [current documentation](../../docs/README.md).
