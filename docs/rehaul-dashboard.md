> Current write integration and loaded-build verification: [write operations](write-operations.md). Older PID/test-count snapshots below are historical.

# Dashboard tabs, history and call logs

The browser dashboard at `http://127.0.0.1:5000/` is the connection surface and parameter runner for nineteen MCP tools (eleven in explicit read-only access). `Prototype/` and `/api/prototype/*` keep their names. Headless attach is unavailable. Both read and write operations use the published /mcp interface. See [write operations](write-operations.md).

## Workbench interface

The Server workspace contains process discovery and passive bridge status. Each TIA workspace has separate **Read operations** and **Write operations** modes. Selecting a workspace or operation does not run a tool or connect a project. The target project, process and connection state remain above the work area; native connection details are expandable.

The tool picker uses the existing schema-generated forms. Empty selectors explain their prerequisites and link to the appropriate discovery operation. These links navigate only; the user executes each read. Device/PLC selection fills native IDs, and exact IDs can also be entered before loading an inventory. Dependency options retain the published schema constraints.

The inspector shows the decoded result, exact request body and endpoint, or full server response (including the MCP envelope). Each completed dashboard operation captures its original target, connection stamp, timestamp and outcome. Selecting an earlier browser run only reopens that capture; it never repeats a request or refills live selectors. An earlier connection is labelled explicitly. Partial completion is distinct from success, and probe results do not establish manual TIA comparison.

**Browser runs** retain up to 40 captures across workspaces in this browser tab. `sessionStorage` restores captures after refresh in the same server epoch. Storage is best-effort: above roughly two million serialized characters, older whole runs are omitted from persisted storage; the visible note reports that some captures may not survive refresh. Storage failure does not fail an operation. This is transient browser evidence, not a persistent project model or the server's journal. A server restart clears it. **Server log** shows the authoritative journal for the selected workspace with expandable entry details, including calls from external MCP clients. Full request/response payloads from other clients are not captured by this browser.

Write operations use the same generated forms, inspector and history as reads. Select any discovered table, tag or user constant, or supply an exact native ID. Selecting a table for editing invokes get_tag_table with entries enabled; empty/loading/error states have distinct explanations. System constants are excluded from writable choices. Attribute values accept JSON strings, booleans and numbers.

Block/UDT tools have editable document names/content and a Load selected source action. Source declarations determine output names; source is loaded without checksums or automatic renaming. Writes trigger relevant MCP inventory/detail refreshes, keep the write result selected, and retain follow-up reads in history. No arming or session-created object list remains.

## Workbench validation — 2026-09-22

- Dashboard and architecture scripts: 31/31 checks. Added coverage exercises navigation without dispatch, explicit IDs before discovery, exact request/envelope inspection, opening earlier results without replay, refresh restoration without live-selector restoration, connection invalidation, precise arming, focused probe inputs, independent table destinations, and bounded history with storage failure.
- Siemens-free .NET harness: 89/89 groups. Release build succeeded with zero errors; NuGet reported `NU1900` because its vulnerability-data endpoint was unreachable from the restricted environment.
- The managed helper loaded the Release executable outside the sandbox as PID **44724**, port **5000**, read-only. The unsuccessful sandbox launch was closed by the user before this start. No staging executable was started.
- Live browser inspection confirmed the new interface, passive Server `get_status` through `/mcp`, exact request and full response views, and retained results after refresh. The response still reports `rehaul-mcp-read-only` and `eleven-read-only-tools`.
- Disconnected project and probe states show explicit connection guidance. Browser console had no warnings or errors. At 390 px the page width was 375 px (scrollbar excluded), and at 1440 px the page width was 1425 px with adjacent form/inspector panels; no horizontal page overflow. Temporary viewport overrides were reset.
- This UI check did not reconnect TIA, execute native inventories, arm probes or write to a project. Connected discovery/probe interactions above are simulated evidence. The restart released attachments; users must reconnect explicitly.

Open project in TIA appears on a closed tab that still has a project path. It posts that tab id. The server opens the stored path in a new visible TIA window and connects that instance. The Server tab does not take a path. A closed process with no project does not offer the action. If a running TIA already has the path, the action is refused before another TIA starts. A failed open closes only the instance it created. Disconnect afterwards leaves that visible window open.

## Tabs and history

- The Server tab is permanent. It shows bridge status, process discovery and server-wide log entries.
- Each TIA tab distinguishes runtime, project and connection: running or closed, open project, no project or historical, and connected, disconnected or invalidated.
- Selecting a tab changes only the view. Live connections stay independent, including two processes with the same project path.
- A projectless runtime that gains a project with no archived tab keeps that tab and its log. Closing a project while the process stays open archives the project and shows a separate tab for the projectless process. Opening that same project again rejoins the archived tab and removes the temporary process tab, including logs recorded on it. If the process exits during that gap, only the archived project tab remains. A process that never had a project keeps its own tab after it exits. Any other path change archives the previous project and associates the runtime with the new path. The new path is not connected by that transition.
- A closed or invalidated project remains as history. The same canonical path can reuse that tab when the project appears again. The path match never connects or adopts another runtime. PID reuse does not inherit the earlier connection.
- History stays in server memory across browser refresh and is dropped on server restart. Dismiss history removes a non-live TIA tab and its log entries. It does not close or change TIA. The server tab and a live runtime cannot be dismissed.
- Retention is 400 log entries and 24 historical TIA tabs. Dropped entries set a truncation flag. Dropped tabs also move the log generation so a client replaces its window instead of leaving a gap.

## Tool runner and logs

Tool forms are rendered from the same definitions as MCP `tools/list`. The page submits `tools/call` to `/mcp` and sends the selected TIA tab's `processId`. Dashboard calls send `X-Tia-Prototype: 1`, so their log origin is `dashboard`. Other MCP clients omit that header and are recorded as `mcp`. This does not add a client identity or change the published tool schemas.

`get_status` without `processId` is bridge-only and is available on the Server tab. Project tools require a live connected tab with an open primary project. Results, including failures and partial reads, stay on the originating tab. A changed runtime, connection or project clears that tab's object selectors and ignores a late response for selector refill.

Each MCP call, dashboard connect/disconnect/monitor/dismiss action and applicable server diagnostic is recorded once. The log stores operation, timestamp, duration, outcome and the captured process, connection and project when available. Failed admission and partial reads remain inspectable. Export fallback diagnostics (`sourceExport`), invalidation and cleanup failure are imported once; ordinary successful registry reads are not copied into this log. Server events, including startup and monitoring failures, belong to the Server tab.

The page polls `GET /api/prototype/dashboard` and `GET /api/prototype/logs`. Those reads use the in-memory snapshot and do not call MCP or discover processes. Browser polling pauses while the tab is hidden. The existing server monitor still enumerates processes on the shared STA worker, and each project read still validates its retained context. The pause control is for a controlled reopen test; while paused, that background enumeration is skipped and per-read validation remains.

Connect and Disconnect are disabled while native work is queued or running, or while a dashboard action is in flight. Tool buttons follow project readiness, not the busy flag. Passive dashboard and log reads stay available.

## Local checks

- Staged Release build, while the previous executable was locked: 0 warnings, 0 errors. That staging executable was not started.
- Siemens-free harness: 80/80 groups, including projectless timeline retention, A-to-B archive without connecting B, exact-path reappearance without reconnection, two same-path processes, PID reuse, late call attribution, failed and partial outcomes, log and historical-tab bounds, dismiss, and tool forms generated from the published schemas.
- Later harness run: 83/83 groups. The added cases cover a project closed inside a running process, that project opening again on the same tab, a different project staying separate, a second live copy of the same path staying separate, the temporary process tab disappearing when that process exits, and a process that never had a project remaining after exit.
- Dashboard script and architecture checks: 22/22. They cover MCP `tools/call` submission, selector clearing, late results, busy Connect/Disconnect, hidden polling, copy, a second process's connect target, historical dismiss, and the unchanged detach boundary.

Simulated transitions are not native TIA lifecycle evidence. Earlier reader evidence in [block reads](get-block.md), [UDTs](udt-discovery-read.md), [tag tables](tag-table-discovery-read.md), [cross-references](cross-references.md) and [MCP cutover](rehaul-mcp-cutover.md) is unchanged. This dashboard does not add native coverage for the eleven tools.

## Loaded server

On 2026-09-21 the lifecycle helper stopped the previous managed process 34156 gracefully, then started the normal Release build. The running server is PID **33068**, port **5000**, `implementationPhase: rehaul-mcp-read-only`, `mcpPublication: eleven-read-only-tools`, `writeToolsAvailable: false`. The staging executable was not started. Restart released bridge attachments, so each TIA process must be connected again in the dashboard.

The opt-in HTTP smoke passed 1/1 against that process. It exercised dashboard, log and tool-form reads, rejected dismissal of the Server tab, and dispatched the eleven tools against a nonexistent process. Those failures are recorded on the Server tab. It did not attach to TIA. Attachment count was unchanged.

In the browser, the Server tab and three discovered, disconnected project tabs were visible. No Connect action was used. Bridge Get status returned read-only facts with no project. Get status on the disconnected Prototype-A-1 tab (process 34636) returned `state: disconnected`, `project: null` and `complete: true`; the tab stayed disconnected. Connect was disabled while that call was running and enabled again afterward. The Server tab kept its own result and log when selected again. Project tools stayed unavailable on the disconnected tab. At a 390px width the tabs wrapped and the page did not scroll sideways. The automation browser could not focus the page, so Copy result reported that it could not copy; the script test covers a successful clipboard write.

This does not add native inventory, source, tag-entry or cross-reference coverage. Detach remains the retained portal disposal only; no attachment was created, so detach was not exercised live.

On 2026-09-22 the lifecycle helper stopped PID 33068 gracefully and started the tab-history correction. That server was PID 75772, port 5000, with the same read-only phase and publication. Restart cleared dashboard history and released attachments. Immediately after start, FillTank and Prototype-A-1 were discovered running and disconnected, with no leftover closed-process tab. The close-project and process-exit cases above remain offline evidence; those TIA actions were not repeated.

The same helper later stopped PID 75772 and started the Open project action as PID **16788**, port **5000**, still read-only. Attachments were released again. The user then tried Open project in TIA and reported that it works.
