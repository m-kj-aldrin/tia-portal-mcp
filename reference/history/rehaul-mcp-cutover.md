> Historical checkpoint only. Current publication and source responsibilities are defined in [current documentation](../../docs/README.md). The read-only startup, source locations and next-step statements below describe the completed phase. Registered-client and user-confirmed evidence retains its original scope.

# Read-tool MCP cutover (historical checkpoint)

At this checkpoint, the `/mcp` endpoint published exactly `list_tia_processes`, `get_status`, `list_devices`, `get_device`, `list_blocks`, `get_block`, `list_udts`, `get_udt`, `list_tag_tables`, `get_tag_table` and `get_cross_references`. Initialize reports `rehaul-read-only-1`; passive runtime status reports `implementationPhase: rehaul-mcp-read-only` and `mcpPublication: eleven-read-only-tools`.

Definitions, schemas, argument validation and dispatch remain in Program.cs. The service interface exposes only the existing reads. Each project read still enters the shared service, captures its attachment ticket before queueing, and runs against the retained project on the single STA worker. No second listener, native session, attachment action, retry or legacy alias was added.

## Contract

- `list_tia_processes` accepts no arguments and discovers without attaching. Its result includes native process modes, optional primary-project paths and bridge connection state.
- `get_status` without arguments returns passive bridge facts, readAtUtc and errors; no process, connection history or aggregate project metadata. With processId it uses the guarded native status reader, including disconnected/projectless behavior. A missing process is an error.
- Project reads require a positive Int32 processId. Inventories for blocks, UDTs and tag tables also require the CPU DeviceItem plcObjectId. Detailed reads and cross-references require the native objectId. IDs are passed unchanged.
- Unknown/duplicate fields, nonboolean options and invalid dependency combinations are rejected before a reader is called. Schemas expose integer bounds, native selector strings, boolean defaults and the conditional dependency constraint.
- Source and path default to true; sourceFormat defaults to best and includeDependencies to false. Dependencies require source enabled and explicit external-source. Tag-table includeEntries defaults to true and accepts no source options. Cross-references accept only processId/objectId.
- The MCP text content contains the existing managed reader envelope unchanged, including complete, errors and explicit nulls. A partial result is returned as a payload with isError:false; clients must inspect complete/errors. A thrown failure has isError:true, readAtUtc, errors and the supplied processId when present. Exact native exception text is marked tia-openness; bridge validation and guard errors remain bridge-owned. There is no replacement source-error packet.
- Connection actions, old V1-only names and write operations return unknownTool. Environment flags cannot restore V1 or enable writes.

Clients use Streamable HTTP at `http://127.0.0.1:5000/mcp`. Refresh tool discovery after upgrading. First call list_tia_processes; the user enables the desired process in the dashboard. Supply that processId for subsequent calls. Use list_devices/get_device to obtain the CPU selector, then the corresponding typed inventory and detail tool.

## Local verification

The assigned worktree started clean at `1f1cda8a325584d08c80db21240387f1b610eb6d` (the incoming handoff).

- Release build targets net48/x64 and the installed V20 API. Initial sandbox build succeeded with NU1900 warnings because NuGet vulnerability data could not be fetched; no compiler errors.
- Siemens-free harness: 69/69 groups. Seven new groups compile the actual Program.cs boundary with only the executable bootstrap excluded, and exercise all definitions/dispatches, selectors/defaults, option forwarding, duplicate/invalid fields, native error provenance, context-loss failures, partial payload fidelity, initialization and retired names. Existing native-reader simulations and guard regressions remain included.
- Dashboard/mock and source-boundary checks: 15/15. The attachment adapter still detaches only with retained TiaPortal.Dispose(); no project save/close or TiaPortalProcess.Dispose is introduced.
- The opt-in HTTP smoke checks the already-running managed server only: actual initialize/tools/list, all eleven dispatch paths, invalid/disconnected requests, duplicate arguments, strict source/entry options, retired names and existing REST protections. It never attaches to TIA or starts a listener.

## Loading and native evidence

Verified loading on 2026-09-21:

- The runtime-owning checkout at `C:\Users\m\Documents\Robot och Automationsprogramerare\OpennessDev\tia-portal-mcp` was still clean on `codex/rehaul`, HEAD `1f1cda8`, before integration. The tracked patch passed git apply --check; the two new files had no existing destination. No concurrent work was overwritten. Changes remain uncommitted in both the assigned worktree and the owning checkout; no push or PR was made.
- The owning checkout's separate CutoverCheck Release build passed with zero warnings/errors, followed by 69/69 offline groups and 15/15 Node checks. The staging executable was never started.
- The owner's lifecycle helper verified PID 53216, stopped it gracefully, then started the normal Release executable after a successful build with zero warnings/errors. The new managed PID is **34156**, port **5000**. Runtime status reports `rehaul-mcp-read-only`, `eleven-read-only-tools`, `writeToolsAvailable:false`, no connections and zero pending operations. PIDs are snapshots.
- HTTP smoke passed **1/1** against that loaded server, with actual discovery and all eleven MCP dispatch paths exercised. Project reads used an intentionally disconnected process; no TIA attachment or native project read/export was performed. Native non-attaching process discovery succeeded. Attachments remained unchanged.

A successful local build, simulated reader response or disconnected HTTP test does not establish a successful attached native read through MCP. The subsequent attached-client checks below now provide that bounded evidence; the user subsequently confirmed the targeted cross-reference comparison with the TIA view, as recorded below.

Earlier native evidence in [block reads](../../docs/get-block.md), [UDTs](../../docs/udt-discovery-read.md), [tag tables](../../docs/tag-table-discovery-read.md), [cross-references](../../docs/cross-references.md) and [connection prototype](connection-prototype.md) remains user-supplied or user-reported and scenario-specific. This transport change does not establish new native source fidelity, populated constants, unit/safety/protection coverage or general lifecycle guarantees.

## Registered MCP client validation — 2026-09-21

After the user registered the MCP server, restarted the client and enabled the TIA connections, the agent executed these calls through the registered `tia_portal` MCP tools (not shell HTTP requests):

- The client exposed exactly all eleven tools. Before native validation, the attachment adapter was checked again: Detach uses only retained `_portal.Dispose()` with no project save/close or process disposal.
- At 16:39:29Z, list_tia_processes reported Prototype-A-1 (34636) and Prototype-A-2 (38568) connected, and FillTank (63600) disconnected. No connection was created or changed by the agent.
- Bridge get_status returned read-only, writeToolsAvailable:false and eleven-read-only-tools without selecting a project.
- At 16:39:44Z and 16:39:48Z, targeted get_status returned the correct separate project names/paths, connected state, V20 and installed products for both test processes. Native project version was null and isModified was true for each. These are observed native values, not inferred versions or evidence of agent changes. Both responses had complete:true/errors:[].
- At 16:40:05Z, get_cross_references for process 34636 and Level meter `X2WQjgcMm0GHsgwYSRIPEw==` returned `%ID50`, PLC_100/FIO, UsedBy Main `%OB1` (`k0Btmfp9aUmUomwUpAZNsA==`), access Read, location `@Main ▶ NW1`, complete:true/errors:[]. This matches the earlier supplied response. The user subsequently confirmed that this cross-reference is correct after being asked to compare it with the TIA view.
- At 16:40:30Z, get_block used the Main ID returned by that cross-reference, with includeSource:false/includePath:false. It returned Main, OB1, LAD, metadata.path:null, source:null, complete:true/errors:[]. No export was requested.

This verifies actual app → MCP → retained native project reads for these operations. It does not assert that all eleven tools have been executed against attached native projects, or expand prior source/lifecycle evidence. The user confirmed "Yes the crossreference is correct" in response to the requested TIA-view comparison of Level meter → Main NW1 / UsedBy / Read. This completes the targeted live comparison as agent-executed MCP evidence plus user-confirmed TIA-view evidence; the agent did not independently inspect the TIA UI. No save, compile, import, project close/open or online action was performed.

Dashboard tabs, retained history and call logs are implemented in [rehaul-dashboard.md](../../docs/rehaul-dashboard.md). Opening a project in TIA remains unimplemented. The evidence above is unchanged. The historical handoff and per-reader check snapshots retain their original publication-hold evidence.
