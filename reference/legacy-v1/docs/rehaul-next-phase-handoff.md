# Handoff: rehaul transition and PLC block discovery

Prepared on 2026-09-21 for a fresh task. Continue from the completed connection/discovery prototype and its initial live checks. The user agreed to prepare the actual rehaul next, followed by PLC block discovery. This handoff does not implement that next phase.

## Start here

Repository: `C:\Users\m\Documents\Robot och Automationsprogramerare\OpennessDev\tia-portal-mcp`.

Branch at handoff: `codex/rehaul`. The implementation and its recorded test evidence are committed in `9c01618` — `Document connection prototype discovery increment`. That commit follows `f6a99ca`, the first connection prototype checkpoint. This handoff is a subsequent documentation-only commit. Verify the branch, latest commits and working tree before editing; do not assume nothing changed after this handoff.

Read:

1. [Repository instructions](../AGENTS.md).
2. [Settled rehaul specification](project-rehaul.md), especially the initial surface/legacy boundary, shared connection guard, inventory behavior and `list_blocks`.
3. [Prototype implementation and verification](connection-prototype.md), including both the initial lifecycle evidence and the user-executed discovery results.
4. [Server-management skill](../.agents/skills/manage-tia-mcp-server/SKILL.md) before checking or changing server lifecycle.

## Agreed next phase

The assistant handles repository preparation, implementation, builds and server loading. The user performs short, clearly explained live checks in TIA when a new build is actually running. Do not leave the user guessing whether the executable was built, whether the running server was updated, or which action belongs to whom.

Proceed in this order:

1. **Prepare the code transition.** Review the current source/test dependencies, separate legacy V1 into the agreed `reference/legacy-v1/` area, preserve the new connection/discovery services as active code, and reconcile the build, offline harness, repository instructions and documentation. The specification still says the repository owner performs the legacy move; the latest conversation agreed that the assistant prepares the transition. Reconcile that wording with the actual migration rather than assuming the owner already moved anything.
2. **Implement the bounded `list_blocks` increment.** Target a user-enabled connection by `processId` and its PLC-owning CPU DeviceItem by `plcObjectId`. Reuse the existing connection guard and retain native hierarchy, order, identity and partial-read behavior.
3. **Build and run meaningful offline checks.** Then load the successful build into the same managed server, with explicit reporting of runtime state and reconnection requirements.
4. **Ask the user for a short live comparison.** Compare the returned block tree with TIA for both test PLCs. Do not ask for another round of already-passed basic discovery tests without a regression concern.

`get_block` follows block discovery as a later increment: direct native block lookup, metadata and authoritative source. UDTs, tag tables and cross-references remain later parts of the settled rehaul.

### Keep migration and MCP publication explicit

The current active rules still lock the eight V1 MCP tools. Prototype mode advertises those definitions but rejects their execution with `prototype-mode`. The new discovery reads are prototype dashboard routes, not the final MCP tool surface.

The settled final rehaul surface contains exactly eleven tools: `list_tia_processes`, `get_status`, `list_devices`, `get_device`, `list_blocks`, `get_block`, `list_udts`, `get_udt`, `list_tag_tables`, `get_tag_table`, and `get_cross_references`. Connection actions are dashboard-only.

The next task must make the intermediate implementation/publication boundary concrete and reconcile `AGENTS.md`, definitions, dispatch, tests and documentation together. Do not silently publish a smaller replacement surface, advertise unimplemented tools as working, leave hidden legacy dispatch, or make the reference copy a runtime fallback. Preparing the source migration does not by itself complete the MCP cutover.

## What is implemented

The executable remains .NET Framework 4.8, x64, using the installed TIA V20 Public API and one shared STA worker. `TIA_MCP_CONNECTION_PROTOTYPE=1` selects the experiment within the existing executable and HTTP listener.

The prototype supports:

- Native process discovery without attachment, multiple user-managed UI-process attachments and explicit connect/disconnect.
- Passive status, background monitoring, recent connection events and a controlled-test monitoring pause. Operation guards remain enabled while monitoring is paused.
- A minimal timed project read, selected-process status, the Device/group inventory and direct selected-Device hardware/PLC-scope reads.
- Strict selectors, optional path construction, partial-read results and managed JSON projections of native values. Complex native objects are represented by type and `valueSerialized: false`, never serialized as proxies.
- Scoped same-path reopen evidence instead of the obsolete unconditional `samePathReopenVerified: false` warning.

Relevant files:

| File | Responsibility |
|---|---|
| `src/TiaOpennessMcpServer/Prototype/ConnectionRegistry.cs` | Shared attachment ownership, admission tickets, context checks, guarded reads and invalidation. |
| `Prototype/ConnectionContracts.cs` | Siemens-free backend/attachment interfaces, tickets and connection DTOs. |
| `Prototype/ConnectionPrototypeService.cs` | Shared STA queue, bounded admission, monitoring and passive status. |
| `Prototype/OpennessConnectionBackend.cs` | Native process discovery, UI attachment and detach adapter. |
| `Prototype/OpennessDiscoveryReader.cs` | Native status, Device groups, direct Device lookup, hardware and CPU identifiers. |
| `Prototype/DiscoveryContracts.cs`, `DiscoveryReadContext.cs`, `DiscoveryRequest.cs` | Discovery DTOs, partial-read/value conversion behavior and input validation. |
| `src/TiaOpennessMcpServer/Program.cs` | Existing MCP definitions/dispatch and prototype HTTP routes. |
| `src/TiaOpennessMcpServer/connection-prototype.html` | Prototype dashboard. |
| `tests/TiaOpennessMcpServer.OfflineTests/` | Console harness, including connection and discovery tests linked to production Siemens-free code. |
| `tests/connection-prototype-dashboard.test.cjs` | Minimal-DOM/mocked-fetch dashboard script tests. |

Paths beginning with `Prototype/` in the table are relative to `src/TiaOpennessMcpServer/`.

The new readers are independent of V1 models/services. Preserve that separation when migrating. A legacy behavior is adopted only deliberately under the rehaul contract; new source must not compile against the inert reference copy.

## Settled connection and selector rules

- Connections are server-wide and shared by clients. There is no implicit active process or per-client TIA attachment.
- The user enables connections through the dashboard. Project operations select an existing connection using `processId`; they never attach or reconnect automatically.
- Retain the approved native Project and path at connection time. Do not introduce expected-project parameters or client-held context revisions.
- Capture an internal attachment ticket before queueing. Reject old tickets after disconnect/invalidation/reconnection, even if the PID is unchanged.
- On the STA worker, validate fresh process identity, project path and retained native project context before reading; read through that retained project; validate again before returning the payload. Discovery traversal also checks at collection boundaries and after read failures.
- Detected context loss invalidates/releases only that connection, discards the payload and requires explicit user reconnection. Never adopt or retry against a replacement project. Ordinary object/type/permission errors do not automatically invalidate a still-valid connection.
- Detach with the retained `TiaPortal.Dispose()`. `TiaPortalProcess.Dispose()` closes TIA and must not be used for detach.
- `plcObjectId` identifies the CPU **DeviceItem** whose `SoftwareContainer.Software` is `PlcSoftware`, not the rack, Device or software object. Resolve it directly within the retained project's `ObjectIdentifierProvider`.
- Native identifiers are opaque. Constructed paths are navigation aids, not selectors. `includePath: false` returns null paths and skips path reconstruction.
- The typed `get_tag_table` design is settled: native tag/constant compositions and identifiers where supported, without XML parsing or an initial checksum. Do not reopen the earlier design-level tag-ID discovery concern.

## Verification completed

Implementation validation recorded in this task:

- Release build: zero warnings/errors against the installed V20 API.
- Offline harness: **51/51 groups**, including guard, selection, stale-ticket, transition, partial-read, serialization and input-validation checks.
- Dashboard script checks: **3/3**, using mocked fetch/minimal DOM. These are not real-browser or native TIA tests.
- Initial build went to `bin/DiscoveryCheck/` to preserve the old running executable. Subsequently, at the user's request, the old server stopped gracefully, the normal Release output was built successfully, and a single replacement server was started. The new program is already loaded, not merely staged.

User-executed live evidence, recorded separately in [the verification notes](connection-prototype.md#user-executed-discovery-results--2026-09-21):

- User reported correct initial status/device-list results.
- Supplied Device-read responses from process `34636` identified `PLC_100`; process `38568` identified `PLC_101`. Each had eight hardware items, `complete: true`, `errors: []`, and a CPU `plcObjectId` equal to that CPU's own native ID.
- Comparing PLC_100 reads with paths enabled/disabled showed all nine paths (Device plus eight items) changed to null while IDs, item names, hierarchy and CPU selector remained unchanged. This verifies the response behavior, not an independent runtime trace of parent traversal.
- Supplying PLC_101's CPU ID to the Device reader returned bridge-owned `unsupportedObject` and `reconnectRequired: false`. The user then reported a successful read using the correct Device ID without reconnecting. This retry has user confirmation, not a separately supplied success payload.

Earlier user-executed connection tests covered independent attachments, safe UI detach/reconnect, project closure detection and same-path reopening rejection with background monitoring paused. The same-path error came from our retained-project comparison guard; it was not a Siemens diagnostic or an individual `.Equals()` trace. Do not generalize these results to every lifecycle scenario.

## Pending evidence

Keep these explicit rather than blocking all incremental development or claiming universal verification:

- Missing/stale object identifiers; disconnected/projectless selected status against live TIA.
- Nested user/system device groups, HMI/other hardware variants, multiple PLC scopes and partial native failures.
- Replacement/path changes, projectless-to-open transitions, hidden-dashboard behavior, changes during reads, queued work across reconnection, process exit/PID reuse and guard overhead.
- Headless creation, attachment and disposal; these remain outside the implemented prototype.

For `list_blocks`, preserve each native block composition's order and its scope/group hierarchy, including software/safety units and system/safety groups where accessible. Return block identity and lightweight classification only: native ID, name/path, type, number, language and applicable system/safety classification. Do not export source or substitute a broad PLC-object inventory. Preserve readable branches with `complete: false` and native errors when a branch fails; reject context loss through the shared guard.

## Runtime snapshot at handoff

A passive check on 2026-09-21 reported:

- One healthy managed prototype server: PID `85716`, port `5000`, read-only, dashboard `http://127.0.0.1:5000/`.
- No pending operations; background monitoring enabled.
- Connected TIA process `34636`: `tia/Prototype-A-1/Prototype-A-1.ap20` (`PLC_100` in the supplied read).
- Connected TIA process `38568`: `tia/Prototype-A-2/Prototype-A-2.ap20` (`PLC_101` in the supplied read).

These PIDs and connections are a snapshot, not durable selectors. Recheck through the lifecycle helper and dashboard state. Rediscover native object identifiers as needed rather than hardcoding the test IDs from the evidence notes.

**The user explicitly does not want multiple MCP servers.** Do not start a second server for testing. Use the exact-checkout lifecycle helper and its graceful shutdown path. Do not restart blindly, force-kill by image name, or delete lifecycle state to bypass an ownership check. On a reload, explain that attachments are released and the user reconnects them in the dashboard; native access approval may be requested again.

## Build and test commands

From the repository root, before migration changes the applicable paths:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test tests/connection-prototype-dashboard.test.cjs
```

Use `dotnet run`, not `dotnet test`. The mutating VS Code tasks are not tests.

If sandbox restrictions prevent use of the default CLI home, the successful local workaround was:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'tia-discovery-dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = 'C:\Users\m\.nuget\packages'
```

`--no-restore` worked with the existing assets/package cache; it is not a substitute for a necessary restore after dependency/project changes. A build with `-p:OutDir=bin/DiscoveryCheck/` does not update the running server. Loading the Windows HTTP listener required execution outside the sandbox in this task. Use the supported approval mechanism if needed; do not work around lifecycle ownership checks.

All live checks are read-only. Do not save, compile, modify, open or close TIA projects or perform online operations. Lifecycle experiments requiring project close/reopen are deliberate user actions on disposable projects. If TIA requests external-access approval, pause for the user to approve it.

## Suggested opening prompt for the fresh task

> Continue from `docs/rehaul-next-phase-handoff.md`. Read the handoff, AGENTS.md and the rehaul specification, then verify the checkout. Prepare the agreed legacy/source/test transition while preserving the guarded connection and discovery services. Make the intermediate MCP publication boundary explicit before changing the active V1 surface. Next implement the bounded `list_blocks` increment through the shared connection service, build/test it, and use only the existing managed server when loading it. Keep native test evidence separate from offline simulations and tell me clearly when a short live check is ready. Do not start a second MCP server or modify my TIA projects.
