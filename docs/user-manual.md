# Current dashboard workflow

Open the [managed dashboard](http://127.0.0.1:5000/). The Server tab stays available. Each running TIA process, and each retained project, has its own tab. Selecting a tab only changes this view. Connections stay independent, and the dashboard never starts TIA or opens a project.

1. On a running TIA tab that is not connected, choose Connect and approve access in TIA if prompted. Disconnect detaches this bridge only.
2. On the Server tab, use List TIA processes. Get status with no project returns bridge facts. On a TIA tab, Get status uses that tab's process.
3. Project tools are available when that tab is live, connected and has an open primary project. Use List devices, then Read device. A single station or CPU is selected automatically.
4. Use List blocks, List UDTs or List tag tables, then choose the item by its path or name.
5. Use Read block, Read UDT, Read tag table or Read cross-references. Source stays on unless you clear it. Include dependencies is available only with source enabled and explicit external-source.

For tag tables, clear Include entries for metadata only (`entries: null`). Clear Include tag table path to skip path construction. Entries keep their own native objectId, or null when TIA has none. Tag-table reads have no source format or checksum.

For blocks, source format best follows the native language and type. For UDTs, best tries external-source (`.udt`), then SIMATIC SD, then SimaticML. An explicit external-source, simatic-sd or simatic-ml request never falls back.

The result stays on the tab that started the call, including success, failure, elapsed time and formatted JSON. Copy result copies that JSON. If the browser refuses the clipboard, the page says it could not copy. Partial results, explicit nulls and native error text stay visible. Switching tabs does not move an in-flight result. Reconnection or a project change clears object selectors, and an older response does not refill the new connection.

The call log on each tab records the operation, time, duration, outcome and process when they are known. Dashboard actions, MCP calls and server events stay distinct. A call is attributed to the connection captured for that request.

History remains after disconnect, invalidation, a project change or process close, and it survives a browser refresh. It is discarded when this server stops. An exact project path can bring a historical tab back when that project appears again; the path match does not connect it. Dismiss history removes only that dashboard record. On a closed tab that still has a project path, Open project in TIA starts a new TIA window for that path and connects it. A closed process that never had a project does not offer that action. If the project is already open, the closed tab is gone and the action is not offered.

The server keeps 400 log entries and 24 historical TIA tabs. A banner appears when older history was discarded. While TIA work is queued or running, Connect and Disconnect are disabled. Log and status polling stay available. Hiding the browser tab pauses that polling; server monitoring and each read's own checks continue.

The same eleven tools are available to MCP clients at `/mcp`. The dashboard submits those `tools/call` requests and supplies the selected tab's `processId`. Tab ids and connection ids are not MCP selectors. See [dashboard behavior and evidence](rehaul-dashboard.md) and [MCP usage and evidence](rehaul-mcp-cutover.md).
