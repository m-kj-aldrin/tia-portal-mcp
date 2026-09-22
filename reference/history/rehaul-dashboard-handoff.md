> Historical handoff only. The dashboard phase and later Open project action are complete. The read-only publication and continuation instructions below do not govern current work. Use [current documentation](../../docs/README.md).

# Handoff: dashboard tabs, history and call logs

Prepared 2026-09-21 after the eleven-tool MCP cutover was reviewed and merged into `codex/rehaul`.

## Start here

Continue in the existing checkout on `codex/rehaul`:

```text
C:\Users\m\Documents\Robot och Automationsprogramerare\OpennessDev\tia-portal-mcp
```

The user will create the new task manually with this handoff. Work directly in this folder. Do not create another task, worktree or branch for this phase unless the user requests it. Verify the current branch, HEAD and local changes before editing, and preserve any concurrent work.

Read `AGENTS.md`, this handoff, the **Browser dashboard**, **Shared TIA Portal connection state** and **Dashboard connection actions** sections of [project-rehaul.md](../../docs/project-rehaul.md), and [rehaul-mcp-cutover.md](rehaul-mcp-cutover.md). The older [MCP handoff](rehaul-mcp-cutover-handoff.md) describes an already-completed phase, not a publication hold to restore.

The next implementation task is the dashboard: project tabs, retained connection history and clearer call logs. Implement and verify that phase, rather than repeating the MCP cutover or stopping at a plan. Keep progress visible and record what was implemented, tested locally, loaded and observed live separately.

## Completed baseline

- `fa01378edfe11617efd5879f162be7928fbaee56` (`Publish eleven read-only MCP tools and update cutover documentation`) is merged into `codex/rehaul` by fast-forward from `1f1cda8`. This handoff is committed on top of that baseline.
- The completed worktree task was **Publish eleven-tool MCP surface**, task ID `01a0c4c2-b9fa-7360-a56b-bc681a703954`. Its last recommendation was dashboard project tabs, retained connection history and clearer call logs. Its remaining commit step has since been completed; do not repeat that closeout.
- The former worktree/branch still exist at `C:\Users\m\.codex\worktrees\8600\tia-portal-mcp` / `codex/publish-eleventool-mcp-surface`. They are not the next task's workspace. No cleanup, push or merge to `master` is requested.
- Before integration, all 20 copied files in the runtime-owning checkout matched the worktree commit. Those duplicate local edits were preserved in a stash named `backup: verified cutover copies before merging fa01378 into codex/rehaul` (stash commit `c6f2c985ff07b8026e6da4244e80ac93b2e54431`). They are already represented by the merge; do not apply that backup as new work or discard it as part of dashboard implementation.
- The original checkout was clean after the merge and before writing this handoff.

The active server publishes exactly eleven read-only MCP tools: `list_tia_processes`, `get_status`, `list_devices`, `get_device`, `list_blocks`, `get_block`, `list_udts`, `get_udt`, `list_tag_tables`, `get_tag_table`, `get_cross_references`. Definitions, schemas, validation and dispatch live in `Program.cs` and call the guarded readers. The offline MCP harness compiles that production boundary with only its executable bootstrap excluded.

Merge review passed a Release build with zero warnings/errors, 69/69 offline test groups, 15/15 dashboard/architecture checks, and 1/1 HTTP smoke test against the existing server. No merge-blocking issue was found.

The runtime was last checked healthy at PID **34156**, port **5000**, read-only. It already runs the cutover; the merge did not restart it. Its documented phase is `rehaul-mcp-read-only`, publication `eleven-read-only-tools`. These are snapshots: use the lifecycle skill to check current ownership/state before managing the server.

## Evidence already established

The registered MCP client exposed all eleven tools. Agent-executed native calls succeeded for process discovery, bridge status, both test projects' status, Level meter cross-references and Main's metadata-only block read. The user confirmed **Level meter %ID50 -> Main %OB1 -> NW1 / UsedBy / Read** against the TIA view. See [cutover evidence](rehaul-mcp-cutover.md) for exact calls and limits; do not repeat this basic acceptance check without a regression concern.

Earlier block inventories, best source routing for SCL/LAD/DB, UDT workflow and FIO tag-table reads have their own scoped evidence in the reader documents. Populated constants, broader software/safety-unit coverage, protected-source cases and broader native lifecycle behavior remain separate targeted work. Simulated lifecycle tests are not native verification. Old process/object IDs are observations, not permanent selectors.

## Dashboard scope for this phase

Follow the settled dashboard design in `project-rehaul.md`, keeping presentation minimal and raw results inspectable. Its project-opening/headless features remain future scope under the current `AGENTS.md`; implementing tabs and logs does not authorize starting TIA or opening projects.

1. **Server and TIA tabs.** Add one permanent Server tab and server-owned TIA tabs that distinguish runtime, project and connection state. Show running/projectless/connected/disconnected/historical states clearly. Selecting a tab only changes the view. Multiple user-enabled connections remain independent.
2. **Retained history.** Preserve a project's tab and logs after disconnect, invalidation, project transition or process closure. A projectless runtime gaining a project retains its timeline, while A -> B makes A historical and associates the runtime with B. Match historical projects only by exact canonical project path; keep runtime and connection identifiers separate. Matching history must never authorize or reconnect a process. Account for distinct runtime identities and simultaneous processes without conflating live connections merely because paths match.
3. **Tool runner through MCP.** Obtain tool names, descriptions and input schemas from the same definitions as `tools/list`; follow the documented server-rendered minimal-form approach instead of maintaining another handwritten argument contract. Submit actual `tools/call` requests through `/mcp`. Display and supply the originating tab's explicit `processId`; tab IDs and internal connection IDs are never MCP selectors. Keep project tools unavailable without a valid connection and primary project, while respecting `get_status`'s separate status semantics.
4. **Results stay with their request.** Display success/failure, elapsed duration, structured errors and formatted raw JSON with copy support. Preserve partial results, explicit nulls and native error text. Switching tabs during an outstanding call must not reroute its result. Reconnection or project changes clear stale object selections and prevent an old response from repopulating the current connection's selectors. Avoid making users manually copy opaque IDs where existing discovery results can supply them.
5. **Call logs and attribution.** Record MCP calls, dashboard actions and applicable debug events, with operation, timestamp, duration, outcome/error and originating process/connection/project context when available. Distinguish `mcp` and `dashboard` origin without modifying the eleven tool schemas or inventing client identity. Attribute a call using its captured request/validated connection context, not whichever tab or connection is current at completion. Include failed admission and partial-read outcomes, and preserve existing export-fallback diagnostics. Server-wide events belong to the Server tab.
6. **Passive monitoring and bounds.** Use small dashboard-only state and incremental-log endpoints, shared server state and a simple polling loop. Pause browser polling while hidden; server monitoring and per-read validation must remain effective. Monitoring must not manufacture MCP tool calls. Bound logs and historical tabs in memory, expose truncation/cursor reset coherently, and define/test retention limits. History survives browser refresh but not server restart. Dismissing historical history cannot close or alter TIA.
7. **Busy state.** Show queued/running work and disable connection-changing controls while a TIA operation runs as specified in the design. Passive status/log reads stay responsive. Keep every native operation on the one shared STA worker.

Do not implement **Open project in TIA**, headless attachment/startup, writes, compilation, persistent engineering models, extra MCP tools, a second server/transport, WebView, SSE/WebSockets or a UI framework migration in this phase. Do not present deferred open-project actions as working. These boundaries come from the active repository rules and the chosen dashboard increment; the larger design document includes later capabilities.

## Where to work

- `src/TiaOpennessMcpServer/connection-prototype.html`: current handwritten dashboard, per-process discovery selections, raw responses and `/api/prototype/*` calls. Migrate its engineering tool tester to MCP while preserving useful selector behavior and inspectable failures.
- `src/TiaOpennessMcpServer/Program.cs`: sole HTTP listener, static dashboard hosting, dashboard REST controls and `McpBoundary`. Keep the exact eleven-tool surface and request protections intact. Dashboard-only state/log routes are not new engineering APIs.
- `src/TiaOpennessMcpServer/Prototype/ConnectionPrototypeService.cs`: shared scheduler, admission-ticket capture, reader dispatch, pending count, passive status and background monitor. Its current status includes registry views/events; it has no complete tab/history/call-log model yet.
- `src/TiaOpennessMcpServer/Prototype/ConnectionRegistry.cs` and `ConnectionContracts.cs`: connection identity, guard transitions and bounded connection events. Existing `ConnectionEvent` fields are timestamp, process ID, connection ID, action and message; they are not the full required call-log contract.
- `src/TiaOpennessMcpServer/Prototype/OpennessConnectionBackend.cs`: native attachment boundary. Detach currently uses retained `_portal.Dispose()`; preserve that behavior.
- `tests/TiaOpennessMcpServer.OfflineTests/`: Siemens-free guard, reader and production MCP-contract tests. Extend here for state/log behavior.
- `tests/connection-prototype-dashboard.test.cjs`, `tests/rehaul-boundary.test.cjs`, `tests/rehaul-http-smoke.test.cjs`: UI mock, architecture and opt-in existing-server checks. Update relevant checks alongside the dashboard.

Do not rename `Prototype/` or all prototype routes as incidental cleanup. Keep `reference/legacy-v1/` inert. Review existing log/event plumbing before adding new state so each operation is recorded once and server bounds remain explicit.

## Verification and loading

Use meaningful Siemens-free tests for tab transitions, history retention, PID reuse, two independent processes, exact-path reappearance without reconnection, late responses after tab changes/reconnection, call attribution, failures/partial outcomes, retention/cursors and passive-poll behavior. Preserve existing selector, guard and MCP-contract coverage. Verify UI behavior in the browser as well as mocks, including narrow layouts, busy state, copy support and visible error handling.

Run from this checkout:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test tests/connection-prototype-dashboard.test.cjs tests/rehaul-boundary.test.cjs
```

The application remains net48/x64 with installed TIA V20 Public API; the test harness is net8 and is run with `dotnet run`, not `dotnet test`. If sandbox profile access requires it, use a temporary `DOTNET_CLI_HOME` and the existing `C:\Users\m\.nuget\packages` cache rather than changing targets or adding a runtime.

Before server management, read `.agents/skills/manage-tia-mcp-server/SKILL.md`. This checkout owns the single managed server. If the executable is locked, first build to a separate output directory and complete offline checks, then use `tools/tia-mcp-server.ps1` to stop gracefully, build normal Release and start the successful build. Never launch the staging executable or a second server, kill by process name, or bypass ownership state. Report the actual loaded PID/phase and ask the user to reconnect attachments after a restart.

The opt-in HTTP smoke uses the already-running server:

```powershell
$env:REHAUL_HTTP_SMOKE='1'
node --test tests/rehaul-http-smoke.test.cjs
```

Keep native validation targeted and read-only. Confirm detach still cannot save/close the user's project before live checks. The user controls connection/reconnection and any disposable-project lifecycle actions; pause if TIA requests external-access approval. Never save, import, create, compile, close/open projects or perform online operations to test the dashboard. Use simulations for destructive transition coverage and distinguish them from user-executed native evidence.

Update the dashboard usage/evidence documentation and README to describe the result and deferred scope accurately. Do not overwrite historical evidence or claim all eleven tools have native coverage. The new task should finish with the implemented behavior, checks, loaded-build status and any specific remaining user check clearly stated.

## Suggested opening message for the new task

> Continue from `docs/rehaul-dashboard-handoff.md`. Work directly in this existing checkout on `codex/rehaul`; do not create a worktree or another task. Implement the dashboard tabs, retained history, MCP-backed tool runner and call logs described in the handoff, preserving the eleven read-only tools and connection guards. Test the result and load the successful build using the repository lifecycle skill. Keep TIA project operations read-only and report evidence and remaining limits separately.
