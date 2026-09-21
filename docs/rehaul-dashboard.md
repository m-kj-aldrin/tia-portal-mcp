# Dashboard tabs, history and call logs

The browser dashboard at `http://127.0.0.1:5000/` is the user-controlled connection surface for the eleven read-only MCP tools. `Prototype/` and `/api/prototype/*` keep their names. Opening a project in TIA, headless attach and writes are not part of this dashboard.

## Tabs and history

- The Server tab is permanent. It shows bridge status, process discovery and server-wide log entries.
- Each TIA tab distinguishes runtime, project and connection: running or closed, open project, no project or historical, and connected, disconnected or invalidated.
- Selecting a tab changes only the view. Live connections stay independent, including two processes with the same project path.
- A projectless runtime that gains a project keeps that tab and its log. Any other path change archives the previous project and associates the runtime with the new path. The new path is not connected by that transition.
- A closed or invalidated project remains as history. The same canonical path can reuse that tab when the project appears again. The path match never connects or adopts another runtime. PID reuse does not inherit the earlier connection.
- History stays in server memory across browser refresh and is dropped on server restart. Dismiss history removes a non-live TIA tab and its log entries. It does not close or change TIA. The server tab and a live runtime cannot be dismissed.
- Retention is 400 log entries and 24 historical TIA tabs. Dropped entries set a truncation flag. Dropped tabs also move the log generation so a client replaces its window instead of leaving a gap.

## Tool runner and logs

Tool forms are rendered from the same definitions as MCP `tools/list`. The page submits `tools/call` to `/mcp` and sends the selected TIA tab's `processId`. Dashboard calls send `X-Tia-Prototype: 1`, so their log origin is `dashboard`. Other MCP clients omit that header and are recorded as `mcp`. This does not add a client identity or change the eleven tool schemas.

`get_status` without `processId` is bridge-only and is available on the Server tab. Project tools require a live connected tab with an open primary project. Results, including failures and partial reads, stay on the originating tab. A changed runtime, connection or project clears that tab's object selectors and ignores a late response for selector refill.

Each MCP call, dashboard connect/disconnect/monitor/dismiss action and applicable server diagnostic is recorded once. The log stores operation, timestamp, duration, outcome and the captured process, connection and project when available. Failed admission and partial reads remain inspectable. Export fallback diagnostics (`sourceExport`), invalidation and cleanup failure are imported once; ordinary successful registry reads are not copied into this log. Server events, including startup and monitoring failures, belong to the Server tab.

The page polls `GET /api/prototype/dashboard` and `GET /api/prototype/logs`. Those reads use the in-memory snapshot and do not call MCP or discover processes. Browser polling pauses while the tab is hidden. The existing server monitor still enumerates processes on the shared STA worker, and each project read still validates its retained context. The pause control is for a controlled reopen test; while paused, that background enumeration is skipped and per-read validation remains.

Connect and Disconnect are disabled while native work is queued or running, or while a dashboard action is in flight. Tool buttons follow project readiness, not the busy flag. Passive dashboard and log reads stay available.

## Local checks

- Staged Release build, while the previous executable was locked: 0 warnings, 0 errors. That staging executable was not started.
- Siemens-free harness: 80/80 groups, including projectless timeline retention, A-to-B archive without connecting B, exact-path reappearance without reconnection, two same-path processes, PID reuse, late call attribution, failed and partial outcomes, log and historical-tab bounds, dismiss, and tool forms generated from the published schemas.
- Dashboard script and architecture checks: 22/22. They cover MCP `tools/call` submission, selector clearing, late results, busy Connect/Disconnect, hidden polling, copy, a second process's connect target, historical dismiss, and the unchanged detach boundary.

Simulated transitions are not native TIA lifecycle evidence. Earlier reader evidence in [block reads](get-block.md), [UDTs](udt-discovery-read.md), [tag tables](tag-table-discovery-read.md), [cross-references](cross-references.md) and [MCP cutover](rehaul-mcp-cutover.md) is unchanged. This dashboard does not add native coverage for the eleven tools.

## Loaded server

On 2026-09-21 the lifecycle helper stopped the previous managed process 34156 gracefully, then started the normal Release build. The running server is PID **33068**, port **5000**, `implementationPhase: rehaul-mcp-read-only`, `mcpPublication: eleven-read-only-tools`, `writeToolsAvailable: false`. The staging executable was not started. Restart released bridge attachments, so each TIA process must be connected again in the dashboard.

The opt-in HTTP smoke passed 1/1 against that process. It exercised dashboard, log and tool-form reads, rejected dismissal of the Server tab, and dispatched the eleven tools against a nonexistent process. Those failures are recorded on the Server tab. It did not attach to TIA. Attachment count was unchanged.

In the browser, the Server tab and three discovered, disconnected project tabs were visible. No Connect action was used. Bridge Get status returned read-only facts with no project. Get status on the disconnected Prototype-A-1 tab (process 34636) returned `state: disconnected`, `project: null` and `complete: true`; the tab stayed disconnected. Connect was disabled while that call was running and enabled again afterward. The Server tab kept its own result and log when selected again. Project tools stayed unavailable on the disconnected tab. At a 390px width the tabs wrapped and the page did not scroll sideways. The automation browser could not focus the page, so Copy result reported that it could not copy; the script test covers a successful clipboard write.

This does not add native inventory, source, tag-entry or cross-reference coverage. Detach remains the retained portal disposal only; no attachment was created, so detach was not exercised live.
