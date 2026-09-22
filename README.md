# TIA Portal MCP workbench

The active Windows bridge provides guarded read and write operations. It provides user-controlled connections to TIA V20 UI processes and guarded status, device, PLC block, UDT and tag-table discovery, plus block/UDT metadata and source reads and typed tag/constant entries and native cross-references through its browser dashboard and MCP endpoint. The read tools do not modify a TIA project. Open project in TIA, on a closed project tab, starts one visible TIA window for the stored path. Eight MCP write tools support native block/UDT source, tag tables, tags and user constants. Writes remain unsaved until the user saves in TIA. Headless startup, a typed project path on the Server tab, and MCP connect, disconnect or open tools are not part of this baseline.

The loopback `/mcp` endpoint normally publishes eleven reads plus eight [write operations](docs/write-operations.md). Explicit read-only access retains the eleven read tools in [project-rehaul.md](docs/project-rehaul.md). Connections remain user-controlled in the dashboard. Retired V1 names remain rejected. V1 code and coupled tests are preserved as inert material in [reference/legacy-v1](reference/README.md).

See [MCP cutover and verification](docs/rehaul-mcp-cutover.md) and [dashboard tabs, history and call logs](docs/rehaul-dashboard.md). The [dashboard handoff](docs/rehaul-dashboard-handoff.md) records that phase. Open project in TIA is a dashboard action on a closed project tab. It starts a new visible TIA window for that stored path.

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

See [cross-reference implementation and test](docs/cross-references.md), [tag-table implementation and test](docs/tag-table-discovery-read.md), [UDT implementation and test](docs/udt-discovery-read.md), [get_block implementation and test](docs/get-block.md), [current implementation and evidence](docs/rehaul-transition.md), [short user workflow](docs/user-manual.md) and [historical prototype evidence](docs/connection-prototype.md). Offline checks validate code behavior, not live TIA semantics.
