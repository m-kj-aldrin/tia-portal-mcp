# TIA Portal MCP workbench

The Windows bridge exposes TIA Portal V20 engineering operations through MCP. MCP is the primary interface; the browser dashboard provides user-controlled connections, tool testing and inspection. Engineering capabilities follow native Openness operations, independently of dashboard layout or controls. Start with the [current documentation](docs/README.md).

The bridge reads process status, devices, blocks, UDTs, tag tables, typed entries and cross-references. Eight write tools provide native source generation/import and tag-table/entry operations. Updating a block or UDT means reading its source, editing the complete document and writing it back with the intended native name and scope. Writes remain unsaved until the user saves in TIA.

The loopback `/mcp` endpoint normally publishes eleven reads plus eight [write operations](docs/write-operations.md). Explicit read-only access retains the eleven read tools in [project-rehaul.md](docs/project-rehaul.md). Connections remain user-controlled in the dashboard. Retired V1 names remain rejected. V1 code and coupled tests are preserved as inert material in [reference/legacy-v1](reference/README.md).

See [write contracts and evidence](docs/write-operations.md) and [dashboard behavior](docs/rehaul-dashboard.md). Open project in TIA is a dashboard action on a closed project tab. It starts a new visible TIA window for that stored path. MCP has no connection or project-opening tools.

## Build and run

Requires Windows, .NET 8 SDK, .NET Framework 4.8 and the installed Siemens TIA Portal V20 Public API.

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test tests/connection-prototype-dashboard.test.cjs tests/rehaul-boundary.test.cjs
./tools/tia-mcp-server.ps1 status -Json
./tools/tia-mcp-server.ps1 start -Json
```

Use one managed server only. If already running, use the helper's graceful stop/restart workflow when loading a new build; never force-kill it. Restart releases bridge attachments, so reconnect each process in the dashboard. It does not close or save the TIA projects.

The dashboard is [http://127.0.0.1:5000/](http://127.0.0.1:5000/). Connect each process explicitly, approve access in TIA if prompted, then inspect it. Omitting the former `-ConnectionPrototype` switch now starts the same rehaul transition; disabling it cannot restore V1. Normal startup uses full access. Use `-AccessProfile read-only` to explicitly restrict publication and execution. Restart preserves the stored profile unless overridden; use `restart -AccessProfile full` when upgrading an older read-only server.

See [cross-reference evidence](docs/cross-references.md), [tag-table evidence](docs/tag-table-discovery-read.md), [UDT evidence](docs/udt-discovery-read.md), [block evidence](docs/get-block.md) and the [user workflow](docs/user-manual.md). Completed migrations and handoffs are [historical records](reference/history/README.md). Offline checks validate code behavior, not live TIA semantics.
