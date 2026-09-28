# Architecture

The project exposes native TIA Portal V20 engineering operations through MCP. The dashboard provides connection management, tool testing and inspection. The current publication contains fifteen read tools and seventeen modifying tools under the [read](read-tools.md) and [write](write-operations.md) contracts.

One user-started .NET Framework 4.8 x64 WinForms executable owns one loopback HTTP listener, one connection registry and one engineering `StaTaskScheduler`. The WinForms shell offers a link that opens the dashboard in the external browser. Its UI thread is separate from the engineering STA; no second server or attachment per client is introduced.

The executable remains `TiaPortalDashboard.exe` so the managed lifecycle helper retains its exact executable ownership checks.

## Source map

Paths below are relative to `src/TiaOpennessMcpServer/`.

| Location | Responsibility |
|---|---|
| `Program.cs` | Register Siemens assembly resolution, then start the application. |
| `Host/ServerApplication.cs` | Compose the single worker, engineering service, dashboard service and endpoints. |
| `Host/HttpHost.cs`, `HttpResponses.cs`, `MainForm.cs`, `AssemblyResolver.cs` | Listener and graceful lifecycle control, shared HTTP response handling, Windows shell and installed Siemens assembly resolution. |
| `Host/LoopbackOriginPolicy.cs` | Shared browser-origin check for MCP and dashboard requests; accepts the configured HTTP port at `127.0.0.1` and `localhost`. |
| `Mcp/McpBoundary.cs` | Authoritative tool definitions, schemas, dispatch and MCP result/error mapping. |
| `Mcp/McpRpcProcessor.cs` | Validate single JSON-RPC request/notification envelopes before tool dispatch; map parse and envelope errors. |
| `Mcp/McpEndpoint.cs`, `McpContracts.cs` | `/mcp` HTTP handling and MCP-specific transport types. |
| `Operations/` | Managed operation interface, requests, results, strict argument validation, source selection, read algorithms, metadata mapping, document staging and shared project-path normalization. No Siemens API dependency. |
| `Services/EngineeringService.cs` | Access-profile enforcement, bounded operation queue, admission tickets, monitoring and managed observations. |
| `Services/ConnectionRegistry.cs`, `ConnectionContracts.cs`, `ConnectionSnapshot.cs` | Retained attachments, context validation, connection lifecycle and backend interfaces. Native handles stay on the engineering worker. |
| `Openness/` | Native attachment implementation and typed Siemens readers, exports, writes and explicit compiler adapter. |
| `Diagnostics/` | Neutral call attribution and operation notes shared across boundaries. |
| `Dashboard/` | Dashboard routes, tab/history workflows, in-process tool actions, memory-only run captures, HTML fragment rendering, log presentation and forms derived from MCP definitions. |
| `Dashboard/DashboardEventStreams.cs` | Bounded SSE admission, managed HTML patches, serialized stream writes, heartbeats and monitoring-subscription lifetime. |
| `Dashboard/wwwroot/` | Separate `index.html`, `styles.css`, `dashboard.js` and pinned local Datastar client assets. |
| `Utilities/` | Shared STA scheduler. |

## Dependencies and request flow

`Operations/` defines the managed contract consumed by MCP, services and native adapters. `Services/` depends on these contracts, neutral diagnostics and the scheduler; it does not reference MCP or dashboard types. `Openness/` implements the backend interfaces using the installed Siemens API. The host supplies the backend when composing the service.

The MCP boundary depends on `IEngineeringOperations` and neutral diagnostics. It has no dashboard dependency. Dashboard forms consume the authoritative MCP definitions, while dashboard history subscribes to managed service observations and records operation notes. Dashboard tool actions call the same composed boundary instance in-process. Both endpoint modules use the shared HTTP response helper; neither creates a listener or its own attachment registry.

A project tool follows this path:

1. An external client submits a JSON-RPC envelope to `McpEndpoint` and `McpRpcProcessor`; a dashboard Datastar action submits to `/api/dashboard/tools/run`. Both reach the same `McpBoundary` instance, which validates and dispatches the selected tool.
2. `EngineeringService` captures the attachment ticket before queueing the operation on the shared STA.
3. `ConnectionRegistry` validates the retained runtime, project path and native project context, then invokes the native attachment.
4. `Openness/` performs the native read or write with the existing traversal checks. The registry validates the context again before returning the managed result.
5. The MCP boundary formats the result and emits a neutral call note. A dashboard invocation also stores its exact capture in bounded server memory and streams escaped result HTML to the initiating page. External MCP clients remain in the metadata journal without their full payloads copied into dashboard run history.

Passive bridge status and dashboard snapshot/log reads do not attach or execute a project read. Background discovery remains on the same engineering STA. Connection loss still discards a read result, releases only the affected attachment and requires explicit reconnection. Ordinary native object or permission failures retain a valid context.

`EngineeringService` owns generic monitoring subscriptions without depending on dashboard or SSE types. Its two-second monitor queues native discovery and connection checks only while at least one subscription exists, and checks that condition again on the STA worker before execution. Each admitted dashboard event stream holds one subscription until disposal. Explicit selected-process status validates its retained native context on demand when no stream is open.

Call attribution uses a request-owned context object that flows through asynchronous service calls. Each MCP call starts its own scope; concurrent calls and later passive status calls cannot inherit another call's attribution.

## HTTP and publication boundary

- `/mcp` publishes thirty-two tools in full access and fifteen reads in explicit read-only access. Compilation and all mutation tools require full access. Definitions and dispatch have one production implementation, linked directly into the Siemens-free contract harness.
- `/api/dashboard/*` exposes process discovery, passive status, tool forms, bounded memory-only run history, the managed event stream and user connection actions (connect, disconnect, open project and dismiss history). `POST /api/dashboard/tools/run` calls the shared `McpBoundary` in-process and streams HTML result patches. It does not implement tools separately or call localhost `/mcp`. The browser uses `X-Tia-Dashboard: 1` for its actions and event stream. External MCP clients do not need that header.
- `GET /api/dashboard/events` requires that custom header and the shared loopback-origin check. It admits at most eight streams and rejects excess requests with HTTP 429 before opening SSE. Monitoring lifetime comes from those subscriptions; the former monitoring POST action is removed.
- `/api/status` remains a passive dashboard-status compatibility route. It does not establish managed-server identity.
- `/` serves the dashboard; `/dashboard/styles.css`, `/dashboard/dashboard.js` and `/dashboard/datastar.js` serve its separate assets.
- `GET /api/lifecycle/health` and `POST /api/lifecycle/stop` belong to the managed host lifecycle and require `X-Tia-Mcp-Control-Token`.

MCP and dashboard POST routes, and the dashboard event-stream GET, share an explicit origin allowlist for `http://127.0.0.1:<port>` and `http://localhost:<port>`. This follows the two local dashboard addresses; it does not trust an incoming Host header, resolve arbitrary hostnames, allow other ports or grant CORS access. Clients without an Origin header remain supported. The dashboard's custom request header and MCP content-type requirements still apply.

The MCP transport accepts one JSON-RPC message per request. Malformed JSON returns HTTP 400 with code `-32700`; an invalid envelope returns HTTP 400 with code `-32600`. Error responses preserve an explicit null ID when no valid request ID is available. Supported `notifications/*` messages do not dispatch engineering operations. Batch arrays are rejected; the transport does not implement batching. These checks leave the tool schemas and engineering contracts unchanged.

The managed health response contains `status:"ready"`, `processId` and `executablePath` after the WinForms shell is ready. The helper requires HTTP 200 and verifies the status, tracked process ID and exact executable path before accepting readiness. It never uses unauthenticated `/api/status` as a fallback and never returns the control token. Older builds can still receive authenticated graceful stop; identity health is unavailable until they are reloaded.

The former `/api/prototype/*` routes are retired. The lifecycle helper's old prototype switch remains only a compatibility spelling; it cannot restore V1. Historical evidence stays under `reference/` and is not an active dependency.

Initialization reports the build's informational version (for example `1.0.0+<commit>`) as `serverInfo.version`; passive status reports `accessProfile` and `writeToolsAvailable`. Neither proves native acceptance or the identity of a running executable. Use authenticated lifecycle health for managed-server identity and the [evidence index](evidence.md) for recorded engineering verification.

## Dashboard stream implementation

Both production and offline-harness projects reference the repository's `Hypermedia.Datastar` 0.1.0 package. Root `nuget.config` declares `packages/` and NuGet.org as package sources. The application retains its .NET Framework 4.8 x64 target and installed Siemens assembly-resolution boundary.

The SDK owns SSE response headers and Datastar HTML patch framing. `DashboardEventStreams` sends a complete initial managed HTML view for tabs, activity, logs and per-tab run summaries, then patches changed fragments. Its one writer coalesces pending snapshots and sends a heartbeat comment every 15 seconds. Service notifications also publish engineering queue start/finish transitions, including calls from external MCP clients. Only managed DTOs reach the HTML renderer; network writes stay off the engineering STA. Disposal releases its monitoring subscription. Server shutdown stops streams and releases subscriptions before closing the host listener.

The page uses the local Datastar client to open the event stream and morph server-rendered HTML, including forms from the published MCP definitions. A safe GET reconnect receives the complete current view. Tool buttons use Datastar POST actions with no automatic POST retry; their response is also HTML SSE for the originating tab. The browser does not poll status, logs or history or store run captures. Schema-derived selector improvements remain separate work.

This implements the approved [dashboard stage 2 design](dashboard-stage-2-design.md) on top of [stage 1](dashboard-stage-1-design.md). Schema-derived forms and selector fixes remain in the [backlog](backlog.md). The user's earlier report of seeing the event request does not establish complete stream or native TIA behavior.

## Evidence

The source boundaries are covered by the Siemens-free service/MCP harness and architecture checks. Recorded build, browser and HTTP checks establish only their stated local and transport behavior; native engineering evidence is tracked separately.

<a id="cleanup-checklist--2026-09-22"></a>
<a id="verification--2026-09-22"></a>
<a id="localhost-origin-correction--2026-09-22"></a>

The original dated record is preserved in [architecture verification](../reference/history/architecture-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
