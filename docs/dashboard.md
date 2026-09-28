# Dashboard tabs, history and call logs

The browser dashboard at `http://127.0.0.1:5000/` extends the MCP interface with connection management, testing and inspection for the published tools (thirty-two in full access, fifteen in explicit read-only access). Dashboard endpoints and history live in `Dashboard/`; the page uses declarative HTML, CSS and the pinned local Datastar client in `Dashboard/wwwroot/`. Its connection and inspection routes use `/api/dashboard/*`. Headless attach is unavailable. Dashboard tool actions call the same composed `McpBoundary` that serves external `/mcp` clients; dashboard controls consume those contracts and must not define engineering semantics. Explicit compilation and deletion require full access; tag-table XML export is a read. See [write operations](write-operations.md), [compile/delete/export](compile-delete-export.md) and the [architecture map](architecture.md). Verification is tracked in the [evidence index](evidence.md).

The [approved stage 1](dashboard-stage-1-design.md) established the event stream and monitoring subscriptions. [Stage 2](dashboard-stage-2-design.md) introduced escaped HTML SSE patches, in-process dashboard tool actions and bounded server-memory captures. The page now uses Datastar signals and attributes directly for local choices, form binding and visible actions. Server-rendered patches own contexts, forms, guarded selector options, inspector views, run history and logs. The browser has no dashboard application script or status, log and history polling. The user's earlier observation of an event request in the Network tab did not verify its contents or native TIA behavior.

## Workbench interface

The Server workspace contains process discovery and passive bridge status. Each TIA workspace has separate **Read operations** and **Write operations** modes. Selecting a workspace or operation does not run a tool or connect a project. The target project, process and connection state remain above the work area; native connection details are expandable.

The tool picker and fields are rendered directly from the published MCP input schemas. Datastar binds typed fields to local signals. The dashboard uses the selected operation to choose inventory suggestions: for example, `delete_block` suggests block IDs, while `delete_udt` suggests UDT IDs. For group operations that accept several kinds, the selected `kind` chooses the group suggestions. Server-owned options are scoped to the selected connected project; an exact native ID can also be entered. Empty selectors explain their prerequisites and link to a discovery operation. These links only choose a tool; the user executes each read. Dependency options retain the published schema constraints.

The inspector shows the decoded result with indentation when it is valid JSON; non-JSON text is shown as returned. Request and Response retain the exact captured text, including MCP error fields. Copying Result copies its displayed formatting. Each completed dashboard tool operation captures its original tab, target, timestamp and outcome. Selecting an earlier run only reopens its stored capture; it never repeats a request or refills live selectors. Partial completion is distinct from success; returned results alone do not establish a manual TIA comparison.

**Run history** retains at most 40 completed dashboard captures and 64 MiB of serialized capture data in server memory. Oldest completed captures are evicted whole. An oversized capture keeps metadata and an explicit `payloadRetained:false` marker. History survives browser refresh but clears on server restart. Dismissing or evicting a historical tab also removes its captures. **Server log** is the separate 400-entry metadata journal for the selected workspace, including calls from external MCP clients. Full request/response payloads from external clients are not copied into dashboard run history.

Write operations use the same generated forms, inspector and history as reads. Select a discovered table, tag or user constant, or supply an exact native ID. Run `get_tag_table` with entries enabled to populate tag and user-constant suggestions for editing; choosing a suggestion does not make a read call. System constants are excluded from writable choices. Attribute values accept JSON strings, booleans and numbers.

Block/UDT tools have editable document name/content fields and a Load selected source action. Source declarations determine output names; source is loaded without automatic renaming. After a write reports affected objects, the dashboard starts guarded follow-up inventory and detail reads when it has the required CPU or parent-table context. Each started read is a separate tool action with its own history entry; it does not repeat the write. If a read fails or lacks the context needed to start, the dashboard reports that uncertainty and directs the user to inspect history and TIA. An admitted write and its follow-up reads continue to completion even if the POST stream disconnects. No arming or session-created object list remains.

Open project in TIA appears on a closed tab that still has a project path. It posts that tab id. The server opens the stored path in a new visible TIA window and connects that instance. The Server tab does not take a path. A closed process with no project does not offer the action. If a running TIA already has the path, the action is refused before another TIA starts. A failed open closes only the instance it created. Disconnect afterwards leaves that visible window open.

Connect and Disconnect carry the runtime start identity as an exact decimal string and the project path shown on the selected tab; Disconnect also carries its attachment ID. Before acting, the server compares these with fresh discovery or the retained attachment and rejects a changed target. A stale browser view cannot connect a replacement runtime or detach a different connection.

## Tabs and history

- The Server tab is permanent. It shows bridge status, process discovery and server-wide log entries.
- Each TIA tab distinguishes runtime, project and connection: running or closed, open project, no project or historical, and connected, disconnected or invalidated.
- Selecting a tab changes only the view. Live connections stay independent, including two processes with the same project path.
- A projectless runtime that gains a project with no archived tab keeps that tab and its log. Closing a project while the process stays open archives the project and shows a separate tab for the projectless process. Opening that same project again rejoins the archived tab and removes the temporary process tab after transferring its logs to the project tab. If the process exits during that gap, its logs are transferred to the archived project tab and only that tab remains. A process that never had a project keeps its own tab after it exits. Any other path change archives the previous project and associates the runtime with the new path. The new path is not connected by that transition.
- A closed or invalidated project remains as history. The same canonical path can reuse that tab when the project appears again. The path match never connects or adopts another runtime. PID reuse does not inherit the earlier connection.
- History stays in server memory across browser refresh and is dropped on server restart. Dismiss history removes a non-live TIA tab and its log entries. It does not close or change TIA. The server tab and a live runtime cannot be dismissed.
- Retention is 400 log entries and 24 historical TIA tabs. Dropped entries set a truncation flag. Dropped tabs also move the log generation so a client replaces its window instead of leaving a gap.

## Tool runner and logs

Tool forms are rendered from the same definitions as MCP `tools/list`. A visible form submits its bound field values, selected tab id, context stamp, tool name and UUID through a Datastar action to `POST /api/dashboard/tools/run`. The route checks that the displayed runtime, project and connection context still matches, converts field types with the published input schema, and takes `processId` from the selected live workspace. Project calls retain the admission-time attachment identity so a replacement attachment cannot be adopted while the call is pending. The route then calls `McpBoundary.HandleAsync` in-process with `tools/call` and dashboard attribution. The MCP boundary still enforces publication, argument validation, connection tickets, access profile and result shaping. External clients call `/mcp` and are recorded as `mcp`. No tool schema or engineering operation is added by the dashboard.

The route requires the dashboard marker, allowed loopback origin, JSON content type and a bounded body of at most 16 MiB. Its form envelope contains `tabId`, `contextStamp`, unique UUID `requestId`, `name` and object `fields`. The adapter also accepts an explicit `arguments` envelope; any supplied positive `arguments.processId` must match the selected live tab. At most four dashboard actions are active. Invalid payload admission calls no MCP tool and returns an escaped SSE error; unauthorized requests receive HTTP 403. The boundary remains responsible for every tool-specific argument and read-only publication check. A disconnected POST is never retried automatically; an admitted operation still finishes and records its result in memory.

`get_status` without `processId` is bridge-only and is available on the Server tab. Project tools require a live connected tab with an open primary project. Results, including failures and partial reads, stay on the originating tab. A changed runtime, connection or project clears that tab's object selectors and ignores a late response for selector refill.

Each MCP call, dashboard connect/disconnect/open/dismiss action and applicable server diagnostic is recorded once. The log stores operation, timestamp, duration, outcome and the captured process, connection and project when available. Tool results, including partial effects or failures, remain inspectable in run history. Export fallback diagnostics (`sourceExport`), invalidation and cleanup failure are imported once; ordinary successful registry reads are not copied into this log. Server events, including startup and monitoring failures, belong to the Server tab.

The page does not poll dashboard or log routes. A persistent GET event stream supplies initial HTML and subsequent changes. Selecting a stored capture makes one safe GET for its inspector; it does not execute a tool. Each project operation still validates its retained context.

Connect and Disconnect are disabled while native work is queued or running, or while a dashboard action is in flight. Tool buttons follow project readiness. The server publishes pending-operation start and finish transitions, including work from external MCP clients, over the event stream.

## Event stream and background monitoring

`GET /api/dashboard/events` requires `X-Tia-Dashboard: 1` and the shared loopback-origin check. At most eight streams are admitted; an additional request receives HTTP 429 before an event stream opens. The endpoint stays in the existing HTTP host and does not create another listener or TIA attachment.

`Hypermedia.Datastar` 0.1.0 supplies the SSE headers and framing. Each admitted stream sends a complete initial escaped HTML view for tabs, activity, logs and run summaries, then `datastar-patch-elements` events for changed fragments. Its one sequential writer coalesces snapshots and sends heartbeat comments every 15 seconds. A broken GET stream can reconnect and receive a complete view without replaying any POST action. Network writes and HTML rendering use managed DTOs off the engineering STA.

The client is bundled as `Dashboard/wwwroot/datastar.js`, version 1.0.4 (SHA-256 `727844adfc825ee651fb93c544a2a739986f9a21820a94524b35f0cac470cf91`). Its local license text is `Dashboard/wwwroot/DATASTAR-LICENSE.txt`; no CDN request is needed.

The page loads only its pinned local Datastar script and opens the stream with `X-Tia-Dashboard: 1`. Event and history GET actions exclude all local form signals from their queries, so source documents are not sent when connecting the stream or opening a stored run. Datastar morphs ID-addressed HTML fragments while signals preserve local view choices and typed inputs. Visible tool and connection controls call Datastar actions directly; there are no hidden proxy buttons. A tool action uses Datastar POST with `retry:'never'`; its own SSE response first patches a running state and then the escaped result in the originating tab. The ongoing stream patches summaries for all open dashboards. A lost POST response is not replayed; inspect the server history and TIA state before attempting another write. Reconnecting the GET stream never reconnects a TIA process.

Each admitted stream holds one generic monitoring subscription owned by `EngineeringService`. The shared monitor discovers processes and validates connections every two seconds while at least one subscription exists. It checks the subscription state again on the STA worker before queued monitoring runs. Stream disposal releases its subscription; background monitoring stops when no subscribers remain. Closing one stream leaves other subscriptions active.

An idle HTTP disconnect is detected by the next send, including the 15-second heartbeat. The server releases that subscription when it observes the transport failure, so aborting the browser stream does not guarantee immediate server-side detection.

Dashboard status exposes `backgroundMonitoringActive` and `monitoringSubscribers`. There is no manual pause control or monitoring POST action. Explicit `get_status(processId)` validates the selected retained context on demand even when no dashboard stream exists. Project operations and process discovery also retain their own guards and on-demand behavior; no stream is required to use MCP.

## Evidence

Recorded local/browser checks and the user-confirmed Open project action are indexed in [evidence](evidence.md). Simulated tab transitions do not establish native TIA lifecycle behavior.

<a id="historical-workbench-validation--2026-09-22-before-mcp-writes"></a>
<a id="historical-local-checks--dashboard-increments"></a>
<a id="historical-loaded-server-snapshots--2026-09-21-and-2026-09-22"></a>

The original snapshots are preserved in [dashboard verification](../reference/history/dashboard-verification.md).
