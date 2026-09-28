# TIA Portal MCP workbench

The Windows bridge exposes TIA Portal V20 engineering operations through MCP. MCP is the primary interface; the browser dashboard provides user-controlled connections, tool testing and inspection. Engineering capabilities follow native Openness operations, independently of dashboard layout or controls. Start with the [current documentation](docs/README.md).

The bridge reads process status, devices, blocks, UDTs, tag tables, typed entries and cross-references, and exports native tag-table XML. It also lists and reads technology objects and the V20 catalogue rows a CPU can create. Seventeen modifying tools provide source generation/import, tag-table/entry operations, technology-object creation and parameters, group creation/deletion, renaming, deletion of blocks/UDTs/tables and explicit offline PLC compilation. Updating a block or UDT means reading its source, editing the complete document and writing it back with the intended native name and scope. Writes remain unsaved until the user saves in TIA. Saving and PLC upload/download are permanently outside the MCP surface.

The loopback `/mcp` endpoint normally publishes thirty-two tools: fifteen reads plus seventeen [modifying operations](docs/write-operations.md). Explicit read-only access retains the fifteen [read tools](docs/read-tools.md). Connections remain user-controlled in the dashboard. Retired V1 names remain rejected. V1 code and coupled tests are preserved as inert material in [reference/legacy-v1](reference/README.md).

See [write contracts](docs/write-operations.md), [compile, deletion and tag-table export](docs/compile-delete-export.md), [verification evidence](docs/evidence.md), and [dashboard behavior](docs/dashboard.md). Open project in TIA is a dashboard action on a closed project tab. It starts a new visible TIA window for that stored path. MCP has no connection or project-opening tools.

The [architecture](docs/architecture.md) maps source responsibilities, protocol, lifecycle ownership and request flow. One Windows executable hosts the MCP endpoint and dashboard, sharing one HTTP listener, connection registry and engineering STA worker.

The working tree includes [dashboard stage 2](docs/dashboard.md#event-stream-and-background-monitoring): the bundled Datastar page receives HTML over SSE, tool actions call the same MCP boundary in-process, and bounded run captures live in server memory. The browser does not poll status, logs or history. Background monitoring runs while at least one event stream is open. Schema-form improvements remain in [the backlog](docs/backlog.md). The user's earlier report of seeing an event request did not verify its contents or native TIA behavior.

## Build and run

Requires Windows, .NET 8 SDK, .NET Framework 4.8 and the installed Siemens TIA Portal V20 Public API.

Root `nuget.config` restores the repository's `Hypermedia.Datastar` 0.1.0 package from `packages/` and other dependencies from NuGet.org. Both production and offline-harness projects reference that package.

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test tests/dashboard.test.cjs tests/architecture.test.cjs
./tools/tia-mcp-server.ps1 status -Json
./tools/tia-mcp-server.ps1 start -Json
```

Use one managed server only. If already running, use the helper's graceful stop/restart workflow when loading a new build; never force-kill it. Restart releases bridge attachments, so reconnect each process in the dashboard. It does not close or save the TIA projects.

The dashboard is [http://127.0.0.1:5000/](http://127.0.0.1:5000/). Connect each process explicitly, approve access in TIA if prompted, then inspect it. Normal startup uses full access. Use `-AccessProfile read-only` to explicitly restrict publication and execution. Restart preserves the stored profile unless overridden; use `restart -AccessProfile full` when changing an older read-only server. The former `-ConnectionPrototype` switch is a compatibility spelling and cannot restore V1.

The helper verifies readiness through token-authenticated `/api/lifecycle/health`, checking the tracked process ID and exact executable path. Passive `/api/status` is not an identity check. An older running build can still be stopped gracefully, but authenticated identity health requires reloading the current build.

See the [user workflow](docs/user-manual.md) and [evidence index](docs/evidence.md). Completed migrations, run narratives and handoffs are [historical records](reference/history/README.md). Offline checks validate code behavior, not live TIA semantics.
