# TIA Portal MCP workbench

A Windows bridge for native TIA Portal V20 engineering operations. MCP is the primary interface; the dashboard provides user-controlled connections, tool execution and inspection. Full access publishes 35 tools: 16 reads and 19 modifying operations.

See [architecture, API and source ownership](docs/architecture.md) and [critical native limitations](docs/limitations.md). Exact tool schemas and dispatch live in [McpBoundary](src/TiaOpennessMcpServer/Mcp/McpBoundary.cs).

## Build and start

Requires Windows, TIA Portal V20 with its installed Public API, the .NET 8 SDK, .NET Framework 4.8 and Node.js for native checks.

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
./tools/tia-mcp-server.ps1 status -Json
./tools/tia-mcp-server.ps1 start -Json
```

The server runs in the Windows notification area without a dashboard window or taskbar entry. Open the [dashboard](http://127.0.0.1:5000/) in your browser, or choose **Open dashboard** from the tray menu or double-click the tray icon. Startup leaves the browser unopened. **Exit** stops the server gracefully and releases its attachments; TIA stays open.

Select a workspace, connect the intended visible TIA process and approve external access in TIA if prompted. The operation picker groups the published tools into **Read** and **Modify**. Project tools use that explicit attachment. Saving and PLC upload/download remain outside MCP.

Run history sits beneath Operation beside the inspector on wide screens; narrow screens show Operation, Inspector and Run history in that order. History and inspector output scroll independently. The inspector keeps its layout when selecting captures, including running, failed and unavailable runs.

The inspector follows **Latest** for each workspace. Choosing a run in history pins it; selecting **Latest** or submitting another operation resumes following. Result, Request and Response show the same captured run. Source-loading helpers read native documents into a locked draft; submit the edited documents separately. Changing the connection or project clears that context's draft and object suggestions.

Search the installed hardware catalogue with `search_hardware_catalog` to find native type identifiers, models, article numbers, versions and catalogue paths. Field filters combine with AND and results are paged. A complete dashboard search supplies editable type-identifier suggestions for Create device from its returned page; catalogue presence does not guarantee standalone creation support.

Normal startup uses full access. Use `start -AccessProfile read-only` for the 16 read tools. Restart preserves the stored profile unless overridden. Load builds through the guarded lifecycle helper; a restart releases attachments, so reconnect them in the dashboard.

## Native MCP checks

Open the fixed disposable project `tia/Demo/Demo.ap20` in visible TIA Portal V20 and connect it through the dashboard. The project must start with zero devices and the server must use full access.

```powershell
node tests/mcp-live.cjs --process-id <PID>
```

The suite calls every published tool against that real project, verifies catalogue filters and pagination, discovers its fixed CPU's type identifier, creates that CPU and fixtures, verifies writes through reads, then deletes the test device. A passing run leaves zero devices; it does not imply `projectModified:false`. The runner never saves or closes TIA, performs online actions or retries uncertain writes.

The suite accepts endpoint and timeout options. Results go to `test-results/mcp-live/<run>/report.json`. A failed run preserves the available response and fixture information for inspection.
