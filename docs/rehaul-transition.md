> The subsequent [get_block increment](get-block.md) adds individual metadata/source reads. Build/runtime evidence below remains the block-discovery checkpoint.

# Rehaul source transition and block discovery

Implemented on 2026-09-21, continuing [the incoming handoff](rehaul-next-phase-handoff.md). This phase prepares the source transition and implements bounded block discovery; it does not complete the final MCP cutover.

## Transition and publication

- [x] Preserve the pre-transition source, coupled tests and documentation from commit `8ebc151` under `reference/legacy-v1/`. All 72 selected files were compared with the committed baseline, normalizing only checkout line endings.
- [x] Retire V1 services, models, export/parsing/write helpers and engineering dispatch from active source. Keep the shared STA host and guarded connection/discovery foundation active in `Prototype/`.
- [x] Reconcile active build/test links, AGENTS.md, README, lifecycle skill/helper, HTTP examples and editor tasks. Retired tests and historical API documents remain in the inert reference area.
- [x] Make the intermediate MCP boundary explicit. The same `/mcp` endpoint supports handshake/discovery and exactly eight disabled V1 descriptors. Their descriptions explicitly say DISABLED; calls return `prototype-mode`. Unpublished names, including `list_blocks`, are rejected. There is no V1 runtime fallback or hidden engineering dispatch.
- [ ] Deliberate complete eleven-tool MCP cutover. `get_block`, UDTs, typed tag tables and cross-references remain later increments.

Every startup now uses the guarded read-only implementation. The former environment flags cannot restore V1 or enable writes. The lifecycle helper accepts `-ConnectionPrototype` for existing commands, defaults to the same mode, and rejects explicitly disabling it or requesting full access before changing the server. The older status `mode:connection-prototype` remains for the intermediate route contract; `implementationPhase:rehaul-block-discovery` identifies this build's feature phase.

## Block inventory

The dashboard's List blocks button sends `POST /api/prototype/blocks` with exactly `{processId, plcObjectId}`. The selector is the CPU DeviceItem identified by Read device, not the Device, rack or PlcSoftware object. The native adapter resolves it directly through the retained project's ObjectIdentifierProvider and verifies SoftwareContainer.Software is PlcSoftware.

The service captures the original attachment ticket before queueing and uses the shared guard before/after traversal, at composition boundaries and after failures. The generic tree walker is exercised offline; PLC_100 and PLC_101 have the scoped user-executed comparisons recorded below. Broader native coverage remains pending.

The result contains readAtUtc, processId, plcObjectId, complete, errors and roots. Each native composition retains its own enumeration order. Cross-composition presentation is block root, software units, then safety units; each block group presents blocks, user groups, then native system block groups. These distinct compositions have no shared native global order.

Named software/safety unit scopes remain nested under the PLC software scope. V20 PlcUnitSystemGroup has no native Name, so it contributes no invented folder/path segment. Group paths use native names. Every readable block includes only identity, name/path, type, number, language and system/safety classification. No source, timestamps, checksums, detailed attributes or broad PLC inventory is read.

OB/FB/FC use their native block class; GlobalDB/InstanceDB/ArrayDB use DB, with detailed DB metadata deferred to get_block. Unknown native block classes retain their class name. A block in PlcSystemBlockGroup has isSystem:true; the top-level PlcBlockSystemGroup is a system-owned group but does not make its user blocks system blocks. Safety-unit ownership or a native V20 F language establishes leaf isSafety. Failed language reads outside a safety unit produce null safety classification rather than guessing. Ordinary groups inherit safety-unit ownership; they do not claim to summarize all descendant safety languages.

Unreadable fields become null with native errors. An interrupted composition retains already-read items and does not prevent later independent compositions from being read. complete:false identifies partial inventory. Lost project context discards the payload and requires explicit user reconnection.

## Verification

- Staged net48/x64 Release build against installed V20 API: passed, zero warnings/errors.
- Active offline harness: **34/34** groups passed. The earlier 51 included 23 retired V1 groups; the active harness retains all 28 connection/discovery groups and adds six block groups. Existing projectless/stale-ticket/transition cases now also exercise ListBlocks.
- Dashboard mocked-fetch/minimal-DOM checks: **4/4** passed, including CPU selector fidelity and reset on reconnection.
- Source boundary checks: **3/3** passed, covering the held MCP surface, reference isolation, absence of legacy routing and the direct CPU adapter/attachment-disposal boundary.
- Native ownership inspection: retained TiaPortal.Dispose remains the detach path; no project save/close and no TiaPortalProcess.Dispose.
- Normal Release build: passed, zero warnings/errors. After the interrupted Codex session, the lifecycle helper confirmed the earlier server was stopped. The single new managed server was started on port 5000 with PID 83620. Passive status confirmed implementationPhase:rehaul-block-discovery, read-only, zero pending operations and no connections. This PID is a snapshot, not a durable selector.
- Opt-in HTTP smoke on that existing server: **1/1** passed, covering handshake, all eight disabled MCP calls, rejection of unpublished block tools, invalid/disconnected block requests, cross-origin rejection, unavailable legacy device route and the served List blocks UI. No TIA attachment was made; the connection list remained empty.
- User comparison: **PLC_100 and PLC_101 passed in the supplied scenarios**, recorded below. The requested two-PLC block comparison is complete; broader native coverage remains pending. Earlier user-reported evidence remains in [connection-prototype.md](connection-prototype.md).

## Short user comparison

After the new server is confirmed running:

1. Refresh the dashboard and reconnect both existing test processes, approving access in TIA if prompted. A reload releases prior bridge attachments.
2. For each PLC, use List devices and Read device only to obtain its current CPU plcObjectId. Enter it in that process's CPU field and select List blocks.
3. Compare the result with TIA: group hierarchy, block names, numbers and languages for PLC_100 and PLC_101. Compare unit/system/safety branches only when those actually exist. Report complete/errors and any mismatch; a fixture without such branches cannot verify their coverage.

Do not create, compile, save, open/close projects or perform online actions for this comparison. get_block follows after block discovery; source retrieval is not part of this test.

## User-executed block comparison — 2026-09-21

The user reported "yes everyting looks correct" and supplied a block response for process 34636, PLC_100, CPU plcObjectId `FbxBd++WREeJ3XSmOr1YXg==`, readAtUtc `2026-09-21T14:19:06.167865+00:00`. The response had complete:true and errors:[]. This records the user's TIA comparison and supplied response, not an independently replayed agent comparison.

The tree contained seven blocks in the supplied order:

| Group under Program blocks | Block | Type / number | Language |
|---|---|---|---|
| Root | Main | OB 1 | LAD |
| Root | GLOBAL | DB 2 | DB |
| 01:Lib | Scale_To_Actual | FC 2 | SCL |
| 01:Lib | Scale_To_Formal | FC 1 | SCL |
| 01:Lib | Scale_Config_Flow | DB 5 | DB |
| 01:Lib | Scale_Config_Level | DB 4 | DB |
| 10:Devices/WaterTank | WaterTank_Core | FB 1 | LAD |

The payload preserves the nested groups and contains nonblank native identifiers for all seven blocks. Program blocks is the native system-owned container; each returned block has isSystem:false and isSafety:false. No system-block subgroup or software/safety unit was present, so this result does not verify those branches, partial native failures or lifecycle changes during a block read. The subsequent PLC_101 comparison is recorded below.

Before this success, supplying the station Device ID `hMbKvDx4QkSMnfG8ji74mg==` to List blocks returned unsupportedObject with reconnectRequired:false. A subsequent agent read of the existing connection identified that ID as the station and returned the correct CPU selector via Read device. The user then supplied the successful block result above. Manual Device-versus-CPU ID entry caused confusion; this is a dashboard usability finding, not evidence of a connection failure.

### PLC_101 comparison

The user confirmed that PLC_101 works and explained that its blocks were copied from PLC_100. The supplied response identifies process 38568, CPU plcObjectId `mF7QzMMCVkSpMne5mlqV4Q==`, and readAtUtc `2026-09-21T14:21:22.7746932+00:00`, with complete:true and errors:[]. The seven block names, types, numbers, languages, group hierarchy and within-composition order match the PLC_100 response above. Paths use PLC_101 and every corresponding block has a distinct native objectId:

| Block | PLC_101 objectId |
|---|---|
| Main | TlOfTgqtZkqT0QZkJwIH6Q== |
| GLOBAL | DSj4RCCqY0a37HJhGMVNJQ== |
| Scale_To_Actual | tmn2lKKpJU6mniM2D8RO3Q== |
| Scale_To_Formal | s6XJCudMZU6SWs4SDKl2Vw== |
| Scale_Config_Flow | 8UAYTqTzGEyziqLBNbNy7w== |
| Scale_Config_Level | M5hZ7m2DTkiXwHaSwsP5og== |
| WaterTank_Core | K+tbPChBf02ROeh9UPEikg== |

This supports correct process/PLC targeting and the user's successful comparison for both fixtures. Identical block inventories are expected after the user's copy; the inventory response does not read or verify source-code equality. No source export or agent write was performed to establish this result. System-block branches, software/safety units, partial native failures and broader lifecycle behavior remain unverified by these two fixtures.

- [x] User comparison for PLC_100.
- [x] User comparison for PLC_101.
- [x] Subsequent bounded increment: get_block metadata and authoritative source implemented; see [its verification status](get-block.md). Native comparison remains separate from implementation.

- [x] Subsequent UDT inventory/detail increment implemented; see [UDT checks and user confirmation](udt-discovery-read.md).

- [x] Tag-table inventory and typed entry reads implemented; see [checks and user-supplied FIO evidence](tag-table-discovery-read.md).
