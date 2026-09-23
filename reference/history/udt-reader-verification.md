# UDT reader verification

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

## Historical verification — 2026-09-21

- [x] Staged net48/x64 Release build against installed V20: zero warnings/errors.
- [x] Offline harness: 49/49 groups. New UDT groups cover partial hierarchy, lightweight leaves, metadata preservation, all three fallback positions, strict failure, metadata-only and context loss. Existing stale-ticket and post-read transition tests now exercise both UDT routes.
- [x] Dashboard script tests: 6/6 with mocked fetch/minimal DOM, including UDT request flags, independent block state, CPU-change clearing and reconnection clearing.
- [x] Source-boundary tests: 5/5, including UDT direct lookup, one bulk attribute call, shared export and held MCP publication.
- [x] Normal Release build: zero warnings/errors. Previous managed PID 59320 exited gracefully. The first sandboxed launch failed at HttpListener construction and subsequently exited; the lifecycle helper confirmed stopped before a successful launch outside the sandbox. The single managed server started as PID 87980 on port 5000, read-only, implementationPhase:rehaul-udt-read. Passive status showed zero pending operations and no attachments. PIDs are snapshots.
- [x] HTTP smoke: 1/1 passed against that running server. Both UDT routes reject invalid/disconnected/cross-origin requests; the served HTML contains List UDTs and Read UDT; the held MCP descriptors remain unchanged and unpublished UDT names return unknownTool. Attachments were unchanged.
- [x] User reported the requested UDT check works; see the scoped confirmation below. No native UDT read/export was performed by the offline or HTTP checks.

Build and offline checks establish implementation behavior, not native UDT field coverage, source fidelity, protected/system/safety-unit coverage or live export-failure behavior.

## Historical comparison procedure

The user subsequently reported that this workflow works, as recorded below. This is a reference procedure for relevant regressions or unverified cases, not a new request to repeat passed checks.

1. Refresh the dashboard, reconnect the existing test process, then List devices → Read device → List UDTs. Compare the type names/groups with TIA.
2. Choose an existing UDT (for example T_Scale_Config if present) and Read UDT with best. Expect external-source and a `.udt` document if native generation permits it; compare its declaration with TIA. Check complete/errors and metadata.
3. Clear Include UDT source and Include UDT path, then read again. Expect source:null and metadata.path:null, with metadata retained.

Return the result or any mismatch. No project changes or repeat of earlier block-format checks is required. Native explicit SD/ML, dependencies and unusual unit/protection cases remain separately unverified until exercised.

## User confirmation — 2026-09-21

After the loaded-build test instructions for UDT discovery, best-source reading and metadata-only/path-disabled reading, the user replied: "yes it works". Record this as user-reported success of the requested workflow. No response payload or separate per-step details were supplied; this does not independently verify exact format/content, every metadata field, checksums or all native export branches. No routine repeat of the same check is required.

Tag tables, cross-references and MCP publication are implemented; see [tag-table evidence](../../docs/tag-table-discovery-read.md), [cross-reference evidence](../../docs/cross-references.md) and [current documentation](../../docs/README.md). Their native evidence remains scoped separately.
