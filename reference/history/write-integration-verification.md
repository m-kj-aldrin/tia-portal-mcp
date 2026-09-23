# Write integration verification

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

## Evidence

The five added tools passed their scoped [native lifecycle acceptance scenarios on 2026-09-23](../../docs/compile-delete-export.md#native-evidence--2026-09-23): populated table export/import with typed readback, compiler success/error/repair diagnostics, all three object deletions with absence checks, and final successful compilation. An initial local `EPERM` report-file failure stopped before transmission of the repair compile; a separately reviewed completion checked the same target/fixtures and finished the remaining operations without replaying successful writes. Both reports are preserved and SHA-linked. The project remained unsaved and connected, with the run's fixtures deleted. Dated evidence below applies to the original nineteen-tool publication.

The user reported on 2026-09-22 that the earlier writes worked. This is accepted as user-reported native evidence; it does not turn every historical probe checklist item into an independently replayed test.

The original integration's local checks covered its eight write schemas and dispatch paths, explicit read-only rejection, existing entry IDs, typed values, source document validation and exact staging, stale tickets/project transitions, partial/cleanup errors, dashboard payloads, source loading and post-write refresh. Those integration checks did not claim newly executed native writes against the user's projects.

Verification on 2026-09-22:

- The normal Release build passed. NU1900 reported unavailable NuGet vulnerability data; compilation succeeded.
- The Siemens-free harness passed 90/90 checks. Dashboard and architecture checks passed 34/34 tests.
- The managed helper gracefully stopped the earlier server and loaded the successful Release executable: PID 33128, port 5000, full access profile. Its status reported `rehaul-mcp-writes` and nineteen published tools.
- The live HTTP smoke test passed 1/1 against that server, including all tool dispatch paths using an unattached process ID, the retired endpoint returning 404, cross-origin MCP requests returning 403, and unsupported request content types returning 415. It did not execute native writes.
- The in-app browser displayed both operation modes, all eight write choices, existing table/entry selection controls, typed attribute input and editable source document fields. No browser console warnings or errors were observed. Native inventory population and mutation readback were covered by simulated dashboard tests, not a newly attached live project.

Attachments require explicit reconnection after this restart, and MCP clients must refresh tool discovery. No native writes, saves, explicit compilation or online operations were performed during this integration validation. The user's earlier successful writes remain separate user-reported evidence.

### Contract clarification verification — 2026-09-22

- Expanded descriptions for all nineteen tools, representative native values and nested document guidance are published by `tools/list` and displayed from those definitions in the dashboard. Names, accepted payloads, parser rules and native write behavior are unchanged.
- The offline harness passed 92/92 groups, including documented request parsing through the production MCP boundary and comparison of displayed source examples against their JSON contents. Dashboard/architecture tests passed 35/35, including help transport, literal text/HTML escaping and source-editor rebuilds without payload changes.
- Both the staged Release build and the normal Release build passed. NU1900 reported unavailable NuGet vulnerability data; there were no compilation errors.
- The helper gracefully stopped PID 33128. The first sandboxed launch failed at HttpListener initialization; after a helper status check reported stopped, the normal Release executable was started outside the sandbox as PID 64268 on port 5000 with full access. Passive status reported `rehaul-mcp-writes`, nineteen tools and no attachments. PIDs are snapshots, not persistent identifiers.
- The running `/mcp` `tools/list` and `/api/prototype/tool-forms` responses contained the new descriptions, address examples and document help. The in-app browser displayed tag argument guidance and UDT filename/content guidance on a disconnected project tab; no browser warnings/errors were observed.
- This clarification performed no native writes, saves, explicit compilation, online actions or new attachments. Request-parser checks do not verify native source generation, non-ASCII source acceptance, every type/address combination, or a complete native round trip. Reconnect attachments in the dashboard and refresh MCP tool discovery to use the loaded build.
