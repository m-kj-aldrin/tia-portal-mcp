# Architecture

The project exposes native TIA Portal V20 engineering operations through MCP. The dashboard provides connection management, tool testing and inspection. The current publication contains fourteen read tools and fourteen modifying tools under the [read](project-rehaul.md) and [write](write-operations.md) contracts.

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
| `Dashboard/` | Dashboard routes, tab/history workflows, log presentation and forms derived from MCP definitions. |
| `Dashboard/wwwroot/` | Separate `index.html`, `styles.css` and `dashboard.js` assets. |
| `Utilities/` | Shared STA scheduler and .NET Framework compatibility support. |

## Dependencies and request flow

`Operations/` defines the managed contract consumed by MCP, services and native adapters. `Services/` depends on these contracts, neutral diagnostics and the scheduler; it does not reference MCP or dashboard types. `Openness/` implements the backend interfaces using the installed Siemens API. The host supplies the backend when composing the service.

The MCP boundary depends on `IEngineeringOperations` and neutral diagnostics. It has no dashboard dependency. Dashboard forms consume the authoritative MCP definitions, while dashboard history subscribes to managed service observations and records operation notes. Both endpoint modules use the shared HTTP response helper; neither creates a listener or its own attachment registry.

A project tool follows this path:

1. `McpEndpoint` accepts the HTTP request, `McpRpcProcessor` validates its JSON-RPC envelope, and `McpBoundary` validates and dispatches the selected tool.
2. `EngineeringService` captures the attachment ticket before queueing the operation on the shared STA.
3. `ConnectionRegistry` validates the retained runtime, project path and native project context, then invokes the native attachment.
4. `Openness/` performs the native read or write with the existing traversal checks. The registry validates the context again before returning the managed result.
5. The MCP boundary formats the result and emits a neutral call note. Dashboard history can display that note without controlling the engineering operation.

Passive bridge status and dashboard snapshot/log reads do not attach or execute a project read. Background discovery remains on the same engineering STA. Connection loss still discards a read result, releases only the affected attachment and requires explicit reconnection. Ordinary native object or permission failures retain a valid context.

Call attribution uses a request-owned context object that flows through asynchronous service calls. Each MCP call starts its own scope; concurrent calls and later passive status calls cannot inherit another call's attribution.

## HTTP and publication boundary

- `/mcp` publishes twenty-eight tools in full access and fourteen reads in explicit read-only access. Compilation and all mutation tools require full access. Definitions and dispatch have one production implementation, linked directly into the Siemens-free contract harness.
- `/api/dashboard/*` exposes process discovery, passive status, tool forms, dashboard history/logs and user connection actions (connect, disconnect, monitor, open project and dismiss history). Engineering tool calls use `/mcp`; there is no parallel dashboard read/probe API. The browser uses `X-Tia-Dashboard: 1` for its POST actions and MCP call attribution. External MCP clients do not need the header.
- `/api/status` remains a passive dashboard-status compatibility route. It does not establish managed-server identity.
- `/` serves the dashboard; `/dashboard/styles.css` and `/dashboard/dashboard.js` serve its separate assets.
- `GET /api/lifecycle/health` and `POST /api/lifecycle/stop` belong to the managed host lifecycle and require `X-Tia-Mcp-Control-Token`.

MCP and dashboard POST routes share an explicit origin allowlist for `http://127.0.0.1:<port>` and `http://localhost:<port>`. This follows the two local dashboard addresses; it does not trust an incoming Host header, resolve arbitrary hostnames, allow other ports or grant CORS access. Clients without an Origin header remain supported. The dashboard's custom request header and MCP content-type requirements still apply.

The MCP transport accepts one JSON-RPC message per request. Malformed JSON returns HTTP 400 with code `-32700`; an invalid envelope returns HTTP 400 with code `-32600`. Error responses preserve an explicit null ID when no valid request ID is available. Supported `notifications/*` messages do not dispatch engineering operations. Batch arrays are rejected; the transport does not implement batching. These checks leave the twenty-four tool schemas and engineering contracts unchanged.

The managed health response contains `status:"ready"`, `processId` and `executablePath` after the WinForms shell is ready. The helper requires HTTP 200 and verifies the status, tracked process ID and exact executable path before accepting readiness. It never uses unauthenticated `/api/status` as a fallback and never returns the control token. Older builds can still receive authenticated graceful stop; identity health is unavailable until they are reloaded.

The former `/api/prototype/*` routes are retired. The lifecycle helper's old prototype switch remains only a compatibility spelling; it cannot restore V1. Historical evidence stays under `reference/` and is not an active dependency.

Initialization identifies `native-compile-delete-export-1`; passive status identifies phase `native-compile-delete-export` and publication `twenty-eight-read-write-tools` or `fourteen-read-only-tools`. These implementation markers do not prove native acceptance or the identity of a running executable. Use authenticated lifecycle health for managed-server identity and the [evidence index](evidence.md) for recorded engineering verification.

## Evidence

The source boundaries are covered by the Siemens-free service/MCP harness and architecture checks. Recorded build, browser and HTTP checks establish only their stated local and transport behavior; native engineering evidence is tracked separately.

<a id="cleanup-checklist--2026-09-22"></a>
<a id="verification--2026-09-22"></a>
<a id="localhost-origin-correction--2026-09-22"></a>

The original dated record is preserved in [architecture verification](../reference/history/architecture-verification.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
