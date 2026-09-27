# Dashboard tabs, history and call logs

The browser dashboard at `http://127.0.0.1:5000/` extends the MCP interface with connection management, testing and inspection for the published tools (thirty-two in full access, fifteen in explicit read-only access). Dashboard endpoints and history live in `Dashboard/`, with separate HTML, CSS and JavaScript in `Dashboard/wwwroot/`. Its connection and inspection routes use `/api/dashboard/*`. Headless attach is unavailable. Both read and modifying tools use the published `/mcp` interface. Dashboard controls consume those contracts and must not define engineering semantics. Explicit compilation and deletion require full access; tag-table XML export is a read. See [write operations](write-operations.md), [compile/delete/export](compile-delete-export.md) and the [architecture map](architecture.md). Verification is tracked in the [evidence index](evidence.md).

The working tree implements the [approved first dashboard stage](dashboard-stage-1-design.md): one event stream and monitoring subscriptions. The current page still uses polling, MCP tool calls and browser run captures. Server-owned tool runs, a Datastar-rendered page, the remaining schema-form changes and retirement of the polling page are the later stages in [backlog.md](backlog.md#stages). The user reports starting the new build and seeing the event request; native TIA behavior has not been verified in this work.

## Workbench interface

The Server workspace contains process discovery and passive bridge status. Each TIA workspace has separate **Read operations** and **Write operations** modes. Selecting a workspace or operation does not run a tool or connect a project. The target project, process and connection state remain above the work area; native connection details are expandable.

The tool picker uses the existing schema-generated forms. Empty selectors explain their prerequisites and link to the appropriate discovery operation. These links navigate only; the user executes each read. Device/PLC selection fills native IDs, and exact IDs can also be entered before loading an inventory. Dependency options retain the published schema constraints.

The inspector shows the decoded result, exact request body and endpoint, or full server response (including the MCP envelope). Each completed dashboard operation captures its original target, connection stamp, timestamp and outcome. Selecting an earlier browser run only reopens that capture; it never repeats a request or refills live selectors. An earlier connection is labelled explicitly. Partial completion is distinct from success; returned results alone do not establish a manual TIA comparison.

**Browser runs** retain up to 40 captures across workspaces in this browser tab. `sessionStorage` restores captures after refresh in the same server epoch. Storage is best-effort: above roughly two million serialized characters, older whole runs are omitted from persisted storage; the visible note reports that some captures may not survive refresh. Storage failure does not fail an operation. This is transient browser evidence, not a persistent project model or the server's journal. A server restart clears it. **Server log** shows the authoritative journal for the selected workspace with expandable entry details, including calls from external MCP clients. Full request/response payloads from other clients are not captured by this browser.

Write operations use the same generated forms, inspector and history as reads. Select any discovered table, tag or user constant, or supply an exact native ID. Selecting a table for editing invokes get_tag_table with entries enabled; empty/loading/error states have distinct explanations. System constants are excluded from writable choices. Attribute values accept JSON strings, booleans and numbers.

Block/UDT tools have editable document names/content and a Load selected source action. Source declarations determine output names; source is loaded without checksums or automatic renaming. Writes trigger relevant MCP inventory/detail refreshes, keep the write result selected, and retain follow-up reads in history. No arming or session-created object list remains.

Open project in TIA appears on a closed tab that still has a project path. It posts that tab id. The server opens the stored path in a new visible TIA window and connects that instance. The Server tab does not take a path. A closed process with no project does not offer the action. If a running TIA already has the path, the action is refused before another TIA starts. A failed open closes only the instance it created. Disconnect afterwards leaves that visible window open.

## Tabs and history

- The Server tab is permanent. It shows bridge status, process discovery and server-wide log entries.
- Each TIA tab distinguishes runtime, project and connection: running or closed, open project, no project or historical, and connected, disconnected or invalidated.
- Selecting a tab changes only the view. Live connections stay independent, including two processes with the same project path.
- A projectless runtime that gains a project with no archived tab keeps that tab and its log. Closing a project while the process stays open archives the project and shows a separate tab for the projectless process. Opening that same project again rejoins the archived tab and removes the temporary process tab after transferring its logs to the project tab. If the process exits during that gap, its logs are transferred to the archived project tab and only that tab remains. A process that never had a project keeps its own tab after it exits. Any other path change archives the previous project and associates the runtime with the new path. The new path is not connected by that transition.
- A closed or invalidated project remains as history. The same canonical path can reuse that tab when the project appears again. The path match never connects or adopts another runtime. PID reuse does not inherit the earlier connection.
- History stays in server memory across browser refresh and is dropped on server restart. Dismiss history removes a non-live TIA tab and its log entries. It does not close or change TIA. The server tab and a live runtime cannot be dismissed.
- Retention is 400 log entries and 24 historical TIA tabs. Dropped entries set a truncation flag. Dropped tabs also move the log generation so a client replaces its window instead of leaving a gap.

## Tool runner and logs

Tool forms are rendered from the same definitions as MCP `tools/list`. The page submits `tools/call` to `/mcp` and sends the selected TIA tab's `processId`. Dashboard calls send `X-Tia-Dashboard: 1`, so their log origin is `dashboard`. Other MCP clients omit that header and are recorded as `mcp`. This does not add a client identity or change the published tool schemas.

`get_status` without `processId` is bridge-only and is available on the Server tab. Project tools require a live connected tab with an open primary project. Results, including failures and partial reads, stay on the originating tab. A changed runtime, connection or project clears that tab's object selectors and ignores a late response for selector refill.

Each MCP call, dashboard connect/disconnect/open/dismiss action and applicable server diagnostic is recorded once. The log stores operation, timestamp, duration, outcome and the captured process, connection and project when available. Failed admission and partial reads remain inspectable. Export fallback diagnostics (`sourceExport`), invalidation and cleanup failure are imported once; ordinary successful registry reads are not copied into this log. Server events, including startup and monitoring failures, belong to the Server tab.

The page polls `GET /api/dashboard/dashboard` and `GET /api/dashboard/logs` every 1.5 seconds. Those reads use the in-memory snapshot and do not call MCP or discover processes. Browser polling pauses while the tab is hidden. Each project operation still validates its retained context.

Connect and Disconnect are disabled while native work is queued or running, or while a dashboard action is in flight. Tool buttons follow project readiness, not the busy flag. Passive dashboard and log reads stay available.

## Event stream and background monitoring

`GET /api/dashboard/events` requires `X-Tia-Dashboard: 1` and the shared loopback-origin check. At most eight streams are admitted; an additional request receives HTTP 429 before an event stream opens. The endpoint stays in the existing HTTP host and does not create another listener or TIA attachment.

`Hypermedia.Datastar` 0.1.0 supplies the SSE headers and framing. Each admitted stream sends an initial managed `{ dashboard, status }` snapshot as Datastar signals, then changed snapshots, with heartbeat comments every 15 seconds. Null fields are included so a later client can clear values that disappeared. A stream has one sequential writer; pending changes are coalesced into the latest managed snapshot so a slow browser does not block the engineering STA.

The current page opens the stream with `fetch` and the required custom header while visible. It drains the response and continues rendering from its existing polling results; no Datastar-rendered page is introduced in this stage. Hiding the tab or a `pagehide` event aborts that stream and cancels pending transport retries. Visibility or `pageshow` opens a fresh stream. Unexpected transport failure retries with delays from one second up to 30 seconds. A retry creates a new stream writer; it never reconnects a TIA process or repeats a tool call.

Each admitted stream holds one generic monitoring subscription owned by `EngineeringService`. The shared monitor discovers processes and validates connections every two seconds while at least one subscription exists. It checks the subscription state again on the STA worker before queued monitoring runs. Stream disposal releases its subscription; background monitoring stops when no subscribers remain. Closing one stream leaves other subscriptions active.

An idle HTTP disconnect is detected by the next send, including the 15-second heartbeat. The server releases that subscription when it observes the transport failure, so aborting the browser stream does not guarantee immediate server-side detection.

Dashboard status exposes `backgroundMonitoringActive` and `monitoringSubscribers`. There is no manual pause control or monitoring POST action. Explicit `get_status(processId)` validates the selected retained context on demand even when no dashboard stream exists. Project operations and process discovery also retain their own guards and on-demand behavior; no stream is required to use MCP.

## Evidence

Recorded local/browser checks and the user-confirmed Open project action are indexed in [evidence](evidence.md). Simulated tab transitions do not establish native TIA lifecycle behavior.

<a id="historical-workbench-validation--2026-09-22-before-mcp-writes"></a>
<a id="historical-local-checks--dashboard-increments"></a>
<a id="historical-loaded-server-snapshots--2026-09-21-and-2026-09-22"></a>

The original snapshots are preserved in [dashboard verification](../reference/history/dashboard-verification.md).
