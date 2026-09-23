# Architecture

The project exposes native TIA Portal V20 engineering operations through MCP. The dashboard provides connection management, tool testing and inspection. The completed architecture cleanup preserved the original eleven read tools and eight write tools. The subsequent [native-operation increment](compile-delete-export.md) adds tag-table export, three deletion tools and explicit PLC compilation, bringing the current publication to twelve reads and twelve modifying tools without changing the host or connection model.

One user-started .NET Framework 4.8 x64 WinForms executable owns one loopback HTTP listener, one connection registry and one engineering `StaTaskScheduler`. The WinForms shell opens the dashboard in the external browser. Its UI thread is separate from the engineering STA; no second server or attachment per client is introduced.

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
| `Mcp/McpEndpoint.cs`, `McpContracts.cs` | `/mcp` HTTP/protocol handling and MCP-specific transport types. |
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

1. `McpEndpoint` accepts the request and `McpBoundary` validates and dispatches the selected tool.
2. `EngineeringService` captures the attachment ticket before queueing the operation on the shared STA.
3. `ConnectionRegistry` validates the retained runtime, project path and native project context, then invokes the native attachment.
4. `Openness/` performs the native read or write with the existing traversal checks. The registry validates the context again before returning the managed result.
5. The MCP boundary formats the result and emits a neutral call note. Dashboard history can display that note without controlling the engineering operation.

Passive bridge status and dashboard snapshot/log reads do not attach or execute a project read. Background discovery remains on the same engineering STA. Connection loss still discards a read result, releases only the affected attachment and requires explicit reconnection. Ordinary native object or permission failures retain a valid context.

Call attribution uses a request-owned context object that flows through asynchronous service calls. This corrects the former individual `AsyncLocal` assignments, which were restored on return to the caller and could lose the captured attachment/path. Each MCP call starts its own scope; concurrent calls and later passive status calls cannot inherit another call's attribution.

## HTTP and publication boundary

- `/mcp` publishes twenty-four tools in full access and twelve reads in explicit read-only access. Compilation and all mutation tools require full access. Definitions and dispatch have one production implementation, linked directly into the Siemens-free contract harness.
- `/api/dashboard/*` contains dashboard connection and inspection routes. The browser uses `X-Tia-Dashboard: 1` for its POST actions and MCP call attribution. External MCP clients do not need the header.
- `/` serves the dashboard; `/dashboard/styles.css` and `/dashboard/dashboard.js` serve its separate assets.
- `/api/lifecycle/stop` remains token-protected and belongs to the managed host lifecycle.

MCP and dashboard POST routes share an explicit origin allowlist for `http://127.0.0.1:<port>` and `http://localhost:<port>`. This follows the two local dashboard addresses; it does not trust an incoming Host header, resolve arbitrary hostnames, allow other ports or grant CORS access. Clients without an Origin header remain supported. The dashboard's custom request header and MCP content-type requirements still apply.

The old `/api/prototype/*` routes are replaced by `/api/dashboard/*`. This dashboard route change does not rename MCP tools or change their arguments. The lifecycle helper's old prototype switch remains only a documented compatibility spelling; it cannot restore V1. Historical evidence stays under `reference/` and is not an active dependency.

Current initialization identifies `native-compile-delete-export-1`; passive status identifies phase `native-compile-delete-export` and publication `twenty-four-read-write-tools` or `twelve-read-only-tools`. The dated cleanup and runtime evidence below describes the earlier nineteen-tool build. The added operations have separate, scoped 2026-09-23 native evidence in [compile, deletion and export](compile-delete-export.md).

## Cleanup checklist — 2026-09-22

- [x] Separate managed operations, native adapters, connection/execution services and diagnostics.
- [x] Keep MCP definitions and dispatch together in the MCP boundary.
- [x] Reduce `Program.cs` to startup and put composition and listener ownership in `Host/`.
- [x] Separate dashboard history/workflows, endpoint handling and HTML/CSS/JavaScript assets.
- [x] Replace active dashboard prototype route/header names and update current documentation.
- [x] Remove unused embedded-browser, dependency-injection and logging package dependencies.
- [x] Complete the Release build and Siemens-free .NET harness after integration.
- [x] Complete dashboard and architecture script checks after integration.
- [x] Load the checked Release executable through the managed lifecycle helper and verify the running build.
- [x] Verify passive dashboard/MCP HTTP behavior after restart and report attachment reconnection requirements.

## Verification — 2026-09-22

- Release build succeeded with no compiler errors. NuGet reported `NU1900` because its vulnerability advisory feed was unreachable; advisory lookup was not verified.
- The Siemens-free .NET 8 harness passed 97 groups, including production service/STA tests for queued attachment tickets, observer failure isolation, closed-tab opening, single journal entries and concurrent asynchronous call attribution.
- Dashboard JavaScript checks passed 26 tests; architecture checks passed 11 tests.
- The managed helper gracefully stopped the old process and started the Release executable as PID `68940`, full access, at `http://127.0.0.1:5000/`. The first sandboxed launch failed during HTTP listener initialization; the same launcher and build started successfully outside the sandbox.
- The running server's complete `tools/list` definitions matched the pre-reload server's nineteen definitions. The opt-in HTTP smoke check passed, including strict request and disconnected-admission paths. Write requests used an impossible process ID and reached no native writes.
- All three served assets matched the source files. Browser inspection showed the styled dashboard, nineteen tools, disconnected TIA workspaces and a successful passive `get_status` call. No browser warnings/errors were recorded.

The restart released bridge attachments; the user must reconnect them in the dashboard. This verification did not attach to, save, compile or modify a TIA project and does not establish additional native read/write behavior. Existing user-reported and agent-observed results retain their stated limits in the [read contracts](project-rehaul.md), [write operations](write-operations.md) and linked evidence pages. Previously passed native discovery checks need not be repeated without a regression concern.

### Localhost origin correction — 2026-09-22

The user subsequently reported HTTP 403 from `list_tia_processes` at `http://localhost:5000/`. Reproduction showed the numeric loopback origin succeeded while localhost was rejected by the old literal origin check. Both endpoints now use the shared explicit loopback origin policy described above.

The Release build, 101 offline groups, 37 dashboard/architecture checks and both live HTTP smoke checks passed. Foreign, wrong-port, opaque and malformed origins remained rejected. After the managed reload (PID `57308`, full access, `rehaul-mcp-writes`), the user's existing localhost Chrome tab completed `list_tia_processes` with HTTP 200, two processes and no errors. Both processes remained disconnected; no attachment or native project write was performed.
