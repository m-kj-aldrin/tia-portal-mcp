# Handoff: publish the eleven-tool read-only MCP surface

Prepared on 2026-09-21 for a fresh Codex task. The user requested this handoff, its commit, and immediate kickoff of the next phase in a new task. Continue implementation; do not stop after restating a plan or ask again whether to begin.

## Starting point

Saved project / runtime-owning checkout:

`C:\Users\m\Documents\Robot och Automationsprogramerare\OpennessDev\tia-portal-mcp`

The incoming branch is `codex/rehaul`. Before this documentation-only handoff, the working tree was clean at `a6ec66fed30150a3f26890ca813e7023f23d8bad` (`Add native cross-reference reads`). Earlier checkpoints are `22db07b` (tag tables), `184170d` (UDTs), `7583e00` (source transition and block reads), and `8ebc151` (the previous handoff). This handoff is committed immediately after those checkpoints. Verify the actual checkout, HEAD and working tree before editing; the task may be created in an isolated worktree based on this committed state.

Read first:

1. [AGENTS.md](../AGENTS.md).
2. [Settled specification](project-rehaul.md): tool map, shared response/connection rules, get_status, each tool's inputs, and dashboard/transport boundaries.
3. [Cross-reference implementation and evidence](cross-references.md), [tag-table evidence](tag-table-discovery-read.md), [UDT evidence](udt-discovery-read.md), and [block-source evidence](get-block.md).
4. [Lifecycle skill](../.agents/skills/manage-tia-mcp-server/SKILL.md) before checking or changing a server.

The old [next-phase handoff](rehaul-next-phase-handoff.md) is a historical incoming snapshot. Do not restart its already-completed source migration or block-discovery work. Current code and the evidence documents above supersede its old to-do list.

## Authorized next phase

Implement the deliberate MCP cutover in the existing executable and loopback `/mcp` endpoint. Publish exactly these eleven tools together:

| Tool | Existing service to reuse |
|---|---|
| `list_tia_processes` | `DiscoverAsync()` |
| `get_status` | Passive bridge status when no processId is supplied; `ReadStatusAsync(processId)` for a selected process. Reconcile the public envelope with the settled specification. |
| `list_devices` | `ListDevicesAsync(processId)` |
| `get_device` | `ReadDeviceAsync(processId, objectId, includePath)` |
| `list_blocks` | `ListBlocksAsync(processId, plcObjectId)` |
| `get_block` | `ReadBlockAsync(BlockReadRequest)` |
| `list_udts` | `ListUdtsAsync(processId, plcObjectId)` |
| `get_udt` | `ReadUdtAsync(BlockReadRequest)` |
| `list_tag_tables` | `ListTagTablesAsync(processId, plcObjectId)` |
| `get_tag_table` | `ReadTagTableAsync(TagTableReadRequest)` |
| `get_cross_references` | `ReadCrossReferencesAsync(CrossReferenceRequest)` |

Keep one definition/validation/HTTP dispatch boundary in Program.cs. Replace the disabled descriptors and publication-hold behavior with the full settled contract. Do not register aliases, compatibility tools, connection actions, write operations or a smaller replacement surface. Retired V1 names must be unknown except names intentionally retained with their new contracts, such as get_status and get_cross_references.

Concretely:

1. Review the implemented request parsers/DTOs against the specification and identify any transport-facing gaps. Reuse the existing guarded readers, preserving their selectors and partial/error behavior.
2. Implement accurate schemas and execution for all eleven tools. Use integer processId, native opaque objectId/plcObjectId and real boolean defaults. Reject unknown/duplicate fields and invalid option combinations. `list_tia_processes` and bridge-level `get_status` do not require a connection; project reads do. Native failures keep exact messages and provenance, including requested processId on failures. Preserve partial payloads rather than converting them into a second incompatible error packet.
3. Reconcile initialize instructions/version, runtime status, README, AGENTS.md, dashboard hold notices, lifecycle documentation and tests in the same change. The current eight-tool lock describes the intermediate state; this specifically authorized phase replaces it with the exact eleven-tool invariant.
4. Add meaningful MCP contract/dispatch checks to the existing harness/Node checks. Verify actual discovery and calls over the existing HTTP transport, all schemas, unknown legacy names, disconnected/invalid input paths, strict options, and preservation of guard semantics. Do not test only by source-text matching. Preserve the useful existing offline regressions.
5. Build/test, load the successful build into the single managed server, verify the new discovery/dispatch via HTTP, and report exactly what is running. Only then ask the user for a short targeted live MCP comparison or any unavoidable TIA access approval.

The current browser inspector already exercises all read families. Make it truthful and usable after publication. The larger final dashboard design (tabs/log refinements) is not completed by these increments; keep remaining UI work explicit. Dashboard-only project-open behavior in the broader specification is not implemented and is not authorization to open, close or otherwise modify a TIA project during this read-only cutover. Avoid an unrelated UI redesign or new transport in this phase.

## Current implementation and boundaries

- One net48/x64 WinForms executable, one listener, one shared StaTaskScheduler, server-wide multi-process ConnectionRegistry. All Openness calls and lazy native callbacks execute on that STA. No per-client native sessions.
- `Prototype/` is active rehaul code; its name and `/api/prototype/*` routes were intentionally retained during incremental development. Renaming is unnecessary for publication.
- MCP currently initializes and lists exactly eight **disabled V1 descriptors**. Every known tool call returns `prototype-mode`; unpublished names return `unknownTool`. Descriptions explicitly say DISABLED. All working engineering reads are presently dashboard routes.
- Startup is always the read-only rehaul implementation. Old environment flags cannot restore V1 or enable writes. `writeToolsAvailable` remains false.
- The pre-transition baseline is inert under `reference/legacy-v1/`. No active build/test/runtime dependency points there. Do not launch its server, helper or harness as a fallback.

Connection rules are not negotiable during cutover: the user connects existing TIA UI processes through the dashboard; MCP does not autoattach, reconnect, start TIA or select an implicit active process. Capture the internal attachment ticket before queueing. Read the retained project and validate process identity, native context and path before/after execution, at collection boundaries and after failures. Context loss discards the payload, releases only that attachment and requires explicit reconnect. Ordinary wrong-object/permission errors preserve a valid attachment. Detach only through retained TiaPortal.Dispose(), never TiaPortalProcess.Dispose().

Selectors: processId chooses the enabled attachment; plcObjectId is the CPU DeviceItem whose SoftwareContainer owns PlcSoftware, not the station Device/rack/software ID. Object lookups are direct through the retained project's ObjectIdentifierProvider. Paths are navigation aids, never selectors.

Reader behavior to preserve:

- Inventories preserve native typed hierarchy, per-composition order and partial readable branches. They do not return detailed metadata or source. Unnamed unit containers add no invented path component.
- Block/UDT metadata uses one bulk GetAttributes(ReadOnly | ReadWrite), with no duplicate typed-property repair. Unknown complex values become explicit type markers, not proxies. V20 EnumToClientRepresentation is narrowly converted using its public Value, fixing the earlier SCL-best routing bug.
- Block best: SCL/STL/DB → external-source then SimaticML; LAD → SIMATIC SD then SimaticML; unlisted language → SimaticML. UDT best: `.udt` external source → SIMATIC SD → SimaticML. Explicit format never falls back. Dependencies require source enabled and explicit external-source. Shared native exporter uses owned temporary staging, exact-content checksums and rejects SD PartialSuccess. Metadata-only exports nothing.
- Tag tables read native Tags/UserConstants/SystemConstants with their own IDs, native fields and partial-read handling; no XML/export/checksum. includeEntries:false returns entries:null and skips entry access. Empty arrays differ from unavailable null collections.
- Cross-references use native CrossReferenceService/AllObjects, without a type allowlist, inventory enrichment, source parsing or compile. Preserve Sources/Children/References/Locations, native path/enum values and IDs only for identifiable underlying engineering objects.

## Code and check entry points

Under `src/TiaOpennessMcpServer/`:

- `Program.cs`: sole HTTP/MCP boundary, currently the publication hold.
- `Prototype/ConnectionPrototypeService.cs`: pre-queue tickets, shared STA admission, monitoring and passive status.
- `Prototype/ConnectionRegistry.cs`, `ConnectionContracts.cs`, `OpennessConnectionBackend.cs`: guarded attachment ownership and reader dispatch.
- `DiscoveryRequest.cs`, `DiscoveryReadContext.cs`, `DiscoveryContracts.cs`: shared validation, managed conversion, partial collection semantics and envelopes.
- `BlockInventoryReader.cs`: reused tree projection for blocks/UDTs/tables; internal block-oriented names do not add block fields to other leaf types.
- `BlockReadContracts.cs`, `UdtMetadata.cs`, `TagTableReadContracts.cs`, `CrossReferenceContracts.cs`: Siemens-free reader contracts/policies.
- `Openness*Reader.cs`, `Openness*DetailReader.cs`, `OpennessSourceExporter.cs`: native adapters.
- `connection-prototype.html`: named selectors and JSON results; no hidden attachment actions.

Checks:

```powershell
$env:DOTNET_CLI_HOME=Join-Path $env:TEMP 'tia-discovery-dotnet'
$env:NUGET_PACKAGES='C:/Users/m/.nuget/packages'
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test tests/connection-prototype-dashboard.test.cjs tests/rehaul-boundary.test.cjs
git -c core.safecrlf=false diff --check
```

The offline harness is a net8 console program, not `dotnet test`. Its csproj links Siemens-free production code; link any new policy files deliberately. Installed V20 API XML/DLLs are at `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20`. The production executable must stay net48/x64.

Latest completed checkpoint: staged and normal Release builds had zero warnings/errors; offline harness **62/62**; dashboard mocked-fetch/minimal-DOM checks **8/8**; source-boundary checks **7/7**; HTTP smoke **1/1**. These checks passed in the preceding increment and were not rerun for this documentation-only handoff. Existing boundary/HTTP tests intentionally assert the publication hold; update their assertions for the cutover rather than deleting protection coverage.

## Runtime and worktree coordination

At handoff, a passive refresh confirmed the original checkout's managed server healthy: PID **53216**, port **5000**, `implementationPhase:rehaul-cross-references`, `mcpPublication:held-eight-disabled-v1-descriptors`, `writeToolsAvailable:false`. PID/status are snapshots. No restart or native project read was performed while preparing this handoff.

Only the runtime-owning checkout has its ignored lifecycle state/token. A new worktree does not inherit that process ownership. Develop and test in the assigned worktree, but do not start a second dashboard there, even on another port. Before loading code, deliberately integrate the verified changes into the original runtime-owning checkout after checking its current branch and uncommitted work; never overwrite concurrent user changes. Use its exact-path lifecycle helper for the existing server. Keep one runtime owner and one server, and report any real integration blocker rather than bypass ownership checks.

When the executable is locked, build to a separate output directory such as `-p:OutDir=bin/CutoverCheck/`, run checks, then gracefully stop, build normal Release output, and start the one server. Never run the staged executable. The lifecycle helper does not compile. On this machine, launching inside the execution sandbox previously failed at HttpListener construction; use the tool's reviewed elevated/outside-sandbox execution for the lifecycle start when required. Do not repeat the known sandbox failure or force-kill a failed process.

HTTP smoke runs only against the already-running server:

```powershell
$env:REHAUL_HTTP_SMOKE='1'
node --test tests/rehaul-http-smoke.test.cjs
```

It must not attach to TIA or start another listener. A server reload releases bridge attachments; tell the user to reconnect each process and approve TIA access if prompted. A staged build is not a loaded update.

## Native evidence and limits

All live evidence below was supplied or reported by the user; do not relabel it as agent-replayed verification.

- Device/CPU discovery and block inventories were successfully compared for PLC_100 and PLC_101; the user had copied the same seven blocks into both. Distinct IDs and paths support targeting, not source equality.
- Block metadata-only produced source:null/path:null. After correcting native enum conversion, the user confirmed best gives external SCL for SCL, SIMATIC SD for LAD and external-source for DB. Full content/checksum fidelity and broader languages/protection are not independently verified.
- The user reported the requested UDT workflow works, without separate payloads/per-step assertions. This is scoped workflow confirmation, not full UDT field/source coverage.
- FIO in PLC_100: supplied full response contained 15 tags (8 Bool, 5 Real, 2 DInt), with 15 distinct own IDs and readable fields; metadata-only preserved metadata and returned entries:null/path:null. Both responses complete:true/errors:[]. Both constant arrays were empty; populated constants and their identifiers remain unverified.
- Cross-reference response at 2026-09-21T16:08:08.4988477+00:00, process 34636: Level meter `%ID50`, objectId `X2WQjgcMm0GHsgwYSRIPEw==`, is UsedBy Main `%OB1`, objectId `k0Btmfp9aUmUomwUpAZNsA==`, with access Read at `@Main ▶ NW1`. IDs match prior table/block responses; complete:true/errors:[]. The user subsequently said “good” and requested this handoff. An independent comparison against TIA's view/source code was not explicitly stated.
- Earlier same-path reopen rejection and UI detach have scenario-specific user-reported evidence in connection-prototype.md. Broader native equality/lifecycle races, software/safety units, system objects, permission failures and mixed/protected source remain limited or unverified.

Historical fixture selectors (rediscover if stale): process 34636 / PLC_100 CPU `FbxBd++WREeJ3XSmOr1YXg==`; process 38568 / PLC_101 CPU `mF7QzMMCVkSpMne5mlqV4Q==`; FIO table `dzT+iCgGkUmAV/I+y2kv7g==`. Never autoattach or hardcode these as current identities. Dashboard named choices are preferred.

## Completion and communication

Finish the transport implementation and required checks, load it, then clearly distinguish implemented/tested/loaded from user-verified native behavior. Keep progress visible while working; the user previously had to ask whether anything was happening. Do not end a turn promising to implement without doing it. Ask the user only for a genuinely necessary decision, TIA access prompt or short native check after the build is running. No routine replay of all earlier discovery/source tests is needed without a regression concern.

No TIA save, write, import, compile, clone, project close/open or online operation is authorized by this phase. No memory-file update, push, PR or unrelated cleanup is requested. The next task should produce a reviewable eleven-tool MCP cutover, preserving the read-only boundary and explicitly documenting any still-unverified scenarios.
