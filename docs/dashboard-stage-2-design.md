# Dashboard stage 2 — server-driven Datastar design for review

Status: **approved by the user on 2026-09-28** for implementation. This records the agreed design and its evidence limits; the current implementation and checks are described in [dashboard.md](dashboard.md) and [handoff.md](handoff.md).

## The corrected stage boundary

Stage 2 makes the **whole active dashboard event-driven**. It bundles the pinned Datastar v1.0.4 client, removes the browser's 1.5-second polling loop and makes server-rendered HTML over SSE the way dashboard state reaches the browser. Tool calls use Datastar actions. This brings the Datastar page and pending-operation notifications originally listed as stage 3 into stage 2; the backlog's stage list will be revised after approval. Form generation fixes remain a later stage.

The old page's connection controls and form helpers can be adapted, but they cannot continue polling or rewriting Datastar-owned regions. There is still one executable, HTTP listener, connection registry, STA worker and authoritative `McpBoundary`. The MCP tool contracts do not change.

## Two SSE flows, with no browser polling

### Ongoing dashboard state

The browser starts one Datastar `@get('/api/dashboard/events')` request with `X-Tia-Dashboard: 1`. The existing endpoint sends an **initial complete HTML view** for tabs, connection state, target summary, busy controls, server log and run-history rows. It then sends `datastar-patch-elements` events whenever the server publishes a change. Datastar morphs the ID-addressed fragments; the browser does not fetch status or logs on a timer.

The server publishes changes when a process/connection snapshot changes, a journal entry or run-history entry is added, a connection action completes, and a queued/running engineering operation starts or finishes. External `/mcp` calls also change pending state and the server journal, so open dashboards receive those updates. The pending-operation transition needs an explicit service notification added in this stage; the current page discovers it by polling. Each stream has one ordered writer. It may coalesce redundant snapshots, but it preserves ordered run-started/run-finished events. It sends heartbeat comments without making a browser status request. Reconnection after a broken **GET** stream sends a fresh complete view, so missed events cannot leave stale state. The server renders from managed DTOs and escapes HTML; no native proxy crosses to a stream writer.

The existing service monitor still checks TIA state internally every two seconds **only while at least one dashboard stream is open**. That is how it learns about an external TIA change. It sends an SSE patch only when the observed state changes. There is no periodic browser request. When the first stream opens after a gap, its initial view marks native status as checking until a fresh service-owned observation completes; the following SSE replaces it with the observed state. This avoids presenting an old connection snapshot as freshly verified. Hiding the page closes its stream; reopening it reconciles the view again. This monitor never attaches or reconnects a TIA process.

### A tool action and its result

A form triggers a Datastar `@post('/api/dashboard/tools/run')` action with the selected tab ID, tool name and arguments. Use the action's custom dashboard header, `retry: 'never'` and no automatic cancellation of an admitted request. The form button becomes busy immediately.

The dashboard route validates admission, records a running capture and calls the **same composed `McpBoundary.HandleAsync`** in-process with `Method = "tools/call"` and `OperationCallContext.Begin("dashboard")`. The boundary retains publication, argument parsing, connection tickets, STA execution, result shaping and its one journal note. No second tool implementation or localhost `/mcp` request is introduced.

The POST response itself is SSE. It first streams an escaped **Running…** HTML patch to that tab's run-status element. When the boundary returns, it streams the server-rendered inspector HTML: result, exact request/response, success/partial/error badge and any affected objects or diagnostics. It then completes the action. The ongoing `/events` stream publishes the new run-history row and any changed busy/status/connection fragments to **all** open dashboards. A different dashboard can explicitly select that run to receive its full HTML inspector; full source documents are not broadcast in every status event.

~~~text
Datastar @post → /api/dashboard/tools/run → shared McpBoundary
                                       ↘ HTML SSE → initiating tab's inspector
Server state change → /api/dashboard/events → HTML SSE → all open dashboards
~~~

This uses Datastar's documented [backend actions](https://data-star.dev/reference/actions) and [HTML patch SSE events](https://data-star.dev/reference/sse_events). The implementation will verify the options against the exact bundled v1.0.4 script and record its version, license and specified SHA-256.

## Page ownership and existing workflows

The active page includes the local Datastar script. Server-rendered fragments own tabs, connection/target status, busy state, inspector, server log and run-history list. Client signals may keep local view choices such as selected tab, selected tool and typed form values; they are not an alternative source for MCP or connection state. Stable per-tab fragment IDs keep a late tool result on its originating tab even if the user switches workspaces. DOM morphing preserves focus and typed input.

The current tool forms can still be derived from the published MCP definitions and may retain small JavaScript helpers for document editing and argument collection until the later schema-form stage. They submit actions, not polling requests. Result-dependent selectors and automatic post-write readbacks are updated by server-sent fragments or small Datastar signals from the **completed tool result**. Follow-up reads are separate calls through the same boundary and produce their own history/SSE updates. No JSON SSE parser or periodic JSON detail fetch is used to keep the old renderer alive. Existing connection, disconnection, Open project and Dismiss remain user actions and return SSE HTML updates; they never happen automatically.

On first load, the initial event supplies the complete current view. On a tab or history selection, a Datastar action may request the chosen view once; that is a user action, not polling. All later server-side changes arrive over the ongoing stream. The browser removes `poll()`, timed `refreshDashboard`/`refreshLogs`, `persistRuns`/`restoreRuns` and `sessionStorage` run history.

## Validation, results and loss of transport

`POST /api/dashboard/tools/run` accepts one scoped Datastar JSON payload, for example:

~~~json
{
  "tabId": "dashboard-tab-id",
  "requestId": "browser-generated-uuid",
  "name": "get_block",
  "arguments": { "processId": 123, "objectId": "opaque-native-id" }
}
~~~

The route requires `X-Tia-Dashboard: 1`, the existing loopback-origin check, JSON content type, valid outer fields and a bounded body checked while reading. A proposed **16 MiB** tool-body limit is separate from the 16 KiB connection-action limit; a proposed maximum of **four active dashboard runs** bounds in-flight captures. A valid positive `arguments.processId` must match the selected live tab for history attribution. `tabId` never becomes an engineering selector. Tool-specific arguments and full/read-only publication remain `McpBoundary` decisions. Rejected admission invokes no tool and displays an escaped error.

A successful SSE connection says nothing about tool success. The HTML result distinguishes success, partial completion and failure while retaining the exact MCP `isError`, `complete`, `errors`, `affectedObjects` and `compilationSucceeded` values in the inspector. Failed writes with reported effects stay visible as partial; compiler errors remain errors even when diagnostics were retrieved completely. No save, retry or rollback is implied.

Once admitted, the server finishes the one invocation and its history record even if the POST stream disconnects. The Datastar tool action never retries automatically. The ongoing dashboard stream announces the final run state; after reconnect its initial view includes that state. If the result is still uncertain, the page says **unknown** and directs the user to inspect history and TIA before another write. Safe GET-stream reconnection never replays a POST.

## Memory-only history

The server stores complete dashboard captures for tool calls, helper reads, readbacks and connection actions separately from the existing 400-entry metadata journal. Proposed retention is **40 completed captures** and **64 MiB** of serialized capture data; oldest completed captures are evicted whole. A single oversized capture keeps metadata with an explicit `payloadRetained:false` marker rather than silently truncating content. Historical-tab dismissal/eviction also removes its captures. Everything clears on server restart. External MCP clients continue to appear in the metadata journal, without their full payloads copied into dashboard history.

The ongoing stream patches history summaries as runs start and finish. A history-row Datastar `@get` action returns the selected capture as an HTML fragment without executing a tool. No browser storage or history polling remains.

## Implementation and checks

Implement the Datastar page/fragment renderer, expand the existing event stream to HTML patches, add pending-operation start/finish notifications, wire the tool action to the existing boundary and add the run store. Keep server connection safety and native operation behavior unchanged. Update the active architecture/dashboard/manual documentation and stage list when the code is implemented.

Verify initial and changed SSE HTML, escaping, ordered/coalesced updates, zero browser polling, pending transitions, one tool dispatch, read-only publication, partial write/compile rendering, context-safe tab switching, history retention, stream reconnect and no POST retry. Run the Release build, Siemens-free .NET 8 harness, `node --test tests/dashboard.test.cjs` and `node --test tests/architecture.test.cjs`. Do not reload the managed server or perform native TIA writes. The user's Network-tab observation of `/api/dashboard/events` remains a report of that request, not complete stream validation.
