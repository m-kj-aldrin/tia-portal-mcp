# Connection prototype

This is the first implementation increment of [project-rehaul.md](project-rehaul.md). It uses the existing net48/x64 executable, loopback HTTP listener and shared STA worker. It is a development experiment, not the new MCP tool contract.

## Start and stop

Build and run the offline harness from the repository root:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
```

Check this checkout's server state, then start the prototype:

```powershell
./tools/tia-mcp-server.ps1 status
./tools/tia-mcp-server.ps1 start -ConnectionPrototype
```

Open the reported dashboard address in a browser. The application displays the address; it does not launch a browser or attach to TIA automatically. Use a different `-Port` only if necessary. If the helper reports another mode already running, stop or explicitly restart that managed instance when doing so is intended. Never stop an unrelated checkout.

```powershell
./tools/tia-mcp-server.ps1 stop
# To explicitly return a managed running prototype to normal V1:
./tools/tia-mcp-server.ps1 restart -ConnectionPrototype:$false
```

The helper records the mode and preserves it on an ordinary restart. `TIA_MCP_CONNECTION_PROTOTYPE=1` is the underlying mode switch. Prototype mode always uses the read-only profile. Its `/mcp` endpoint retains V1 discovery, but every tool call reports that prototype mode is active. Legacy REST engineering routes are unavailable, preventing a second attachment manager from operating alongside the experiment. Normal startup remains V1.

## Implemented scope

- Discover running processes; explicitly connect existing TIA UI instances by `processId`. Headless instances are listed as unavailable in this increment.
- Retain multiple attachments, shared by the prototype dashboard, and serialize all native access on the existing STA worker.
- Read the selected project's native name, path, nullable version and top-level device names. This is a small probe, not the planned complete `get_device` or inventory implementation.
- Validate fresh process information, runtime start time, primary-project path, native project equality and retained-project access before and after each read. Record time spent in the two checks separately from the read.
- Bind requests to an internal attachment ID before they wait in the worker queue. Old requests cannot run through a later attachment.
- Invalidate and detach on a context mismatch or failed context validation. Ordinary read errors with a still-valid project preserve the connection. Failed cleanup leaves it invalid and blocks a duplicate attachment until explicit cleanup retry succeeds.
- Monitor connected projects independently of dashboard polling. The controlled-test pause affects only this background monitoring; operation guards remain enabled. It is not a production rehaul feature.
- Retain the latest connection state per process and the last 100 events. This does not implement the planned persistent project tabs or full request history.
- Bound prototype admission to 32 pending operations. Native calls already executing are not force-cancelled; graceful shutdown waits for the worker before detaching.

Prototype routes are `/api/prototype/status`, `/processes`, `/connect`, `/disconnect`, `/read` and `/monitor` under the same prefix. Connection/read actions accept only `{ "processId": 1234 }`; the controlled monitoring toggle accepts only `{ "paused": true }`. POSTs require `X-Tia-Prototype: 1` and reject cross-origin browser requests. `/api/status` provides passive prototype health without native access.

## Live verification procedure

Use two disposable test projects in separate TIA V20 UI processes. The prototype does not create, open, close, save, compile or modify projects. Project lifecycle actions below are performed deliberately by the user in TIA. Approve native external-access prompts in TIA when connecting.

1. Refresh the process list, connect each test process, and read each project. Record the native paths and timing fields.
2. With background checks running, close or switch one test project in TIA. Its connection should become invalidated; the second process should remain readable. Reconnect explicitly to approve the new context.
3. For the **same-path reopening test**, first connect and read the project, then pause background checks using the checkbox. Close and reopen that project at the same path in TIA. Do not reconnect the prototype. Click **Read project** and inspect the error and event log. This pause ensures background monitoring does not detect the temporary projectless state before we can test native identity/proxy behavior. If the read succeeds, the proposed mechanism has not demonstrated same-path reopen detection; record that limitation rather than marking the case passed. Resume background checks afterwards.
4. Disconnect one attachment. Verify its TIA window and project remain open and the other connection still reads successfully.
5. Exit one test TIA process and verify invalidation. If an OS process ID is later reused, it must not inherit the old connection.

A change during a native read and realistic timing/latency still need a controlled live scenario; simulated post-read transitions are only offline evidence. Headless creation and disposal are deferred entirely. Full MCP tools, project opening, persistent project tabs and inventory/export behavior remain later increments.

## Verification evidence

- Release build: passed with zero warnings and errors on 2026-09-20 using installed V20 API references and cached packages.
- Offline harness: 38/38 groups passed, including 15 connection guard groups. These exercise the production guard against a simulated native backend; they do not prove Siemens identity or lifecycle semantics.
- Browser smoke check: dashboard renders, passive status works, background monitoring can be paused/resumed, and native process discovery lists a running V20 UI process without attaching.
- HTTP smoke check: disconnected reads fail, invalid arguments and cross-origin requests are rejected, legacy engineering routes are unavailable, and V1 discovery remains available while tool execution reports prototype mode.
- Live manual tests on 2026-09-21: the user reported successful results with two V20 UI processes, one project in each, and PLC names `PLC_100` and `PLC_101`. These are user-executed results, not independently replayed agent tests. See the recorded outcomes below.

### Manual test results — 2026-09-21

| Scenario | Reported outcome |
|---|---|
| Connect and read both processes, then read the first again | Both remained connected and returned their respective project information. |
| Disconnect `PLC_100` while `PLC_101` remains connected | The first TIA window and project stayed open; the second connection remained readable. Explicitly reconnecting and reading the first succeeded. |
| Close the first project with background checks enabled, keeping its TIA process running | Its connection became invalidated. The second connection remained readable. Reopening the first project did not authorize it automatically; explicit reconnect and read succeeded. |
| Pause background checks, close and reopen the same project at the same path in the same process, then read without reconnecting | The guard rejected the read with `reconnectRequired` and a retained-native-project mismatch. The user supplied the error response below. |
| Resume background checks after that rejection | The second connection still read successfully; explicitly reconnecting and reading the first succeeded. |

The supplied same-path reopen response identified process `34636`, error code `reconnectRequired`, `reconnectRequired: true`, and the message:

> The project context changed or could not be validated. Reconnect this process. The retained native project no longer matches the open project.

`nativeCause` was `InvalidOperationException`. In this case that exception and its message are generated by the bridge's native-project comparison guard; they are not a quoted Siemens diagnostic. The result supports same-path reopen detection in the tested V20 scenario. It is not a trace of each individual comparison or a guarantee for every lifecycle sequence or TIA version.

Still pending live verification: replacing a project with a different project, path changes, a projectless attachment gaining a project, transitions while the dashboard is hidden, a transition during a read, queued work across reconnection, process exit/PID reuse, guard overhead, and headless lifecycle behavior. Existing offline coverage remains separate evidence for the scenarios it simulates.
