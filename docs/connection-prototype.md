# Connection prototype

This is the connection prototype and its subsequent discovery increment from [project-rehaul.md](project-rehaul.md). It uses the existing net48/x64 executable, loopback HTTP listener and shared STA worker. It is a development experiment, not the new MCP tool contract.

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
- Discover native process mode and optional primary-project path with this server's `connectedByMcp` state. Discovery never attaches. Missing processes, changed paths and changed runtime start identity invalidate an existing attachment; diagnostic path equality alone does not establish native project identity.
- Read status for one explicitly selected process. A disconnected process returns no attachment-dependent context; a connected projectless process returns TIA context with `project: null`. Connected status reads native installed products/options and project name/path/version/modified state without an inventory. The native V20 product type has no product-code property; no code or update value is manufactured.
- List root Devices, user device groups and the native ungrouped system group, preserving each composition's order. This does not visit DeviceItems. Read a selected Device by direct native identifier lookup to expand its DeviceItems and find PLC-owning CPU identifiers. Optional parent-only path reconstruction can be disabled; it does not rebuild an inventory.
- Preserve readable inventory branches and metadata on ordinary native failures. Partial reads set `complete: false` and retain native messages. Collection boundaries and failed reads recheck the connection; detected context loss stops traversal, invalidates that attachment and discards the payload. Object-not-found and wrong-type errors preserve a still-valid connection.
- Validate fresh process information, runtime start time, primary-project path, native project equality and retained-project access before and after each read. Record time spent in the two checks separately from the read.
- Bind requests to an internal attachment ID before they wait in the worker queue. Old requests cannot run through a later attachment.
- Invalidate and detach on a context mismatch or failed context validation. Ordinary read errors with a still-valid project preserve the connection. Failed cleanup leaves it invalid and blocks a duplicate attachment until explicit cleanup retry succeeds.
- Monitor connected projects independently of dashboard polling. The controlled-test pause affects only this background monitoring; operation guards remain enabled. It is not a production rehaul feature.
- Retain the latest connection state per process and the last 100 events. This does not implement the planned persistent project tabs or full request history.
- Bound prototype admission to 32 pending operations. Native calls already executing are not force-cancelled; graceful shutdown waits for the worker before detaching.

Prototype routes use the `/api/prototype` prefix:

| Method and suffix | Input / responsibility |
|---|---|
| `GET /status` | Passive prototype health, cached connection views, events and scoped verification evidence; no native access. Also available at `/api/status`. |
| `GET /processes` | Live diagnostic discovery; returns `{ readAtUtc, processes, errors }`. |
| `POST /connect`, `/disconnect`, `/read` | Only `{ "processId": 1234 }`; existing controls and minimal timed project probe. |
| `POST /process-status` | Only `{ "processId": 1234 }`; selected-process status through the retained attachment when connected. |
| `POST /devices` | Only `{ "processId": 1234 }`; native Device group tree, `complete`, `errors`. |
| `POST /device` | `{ "processId": 1234, "objectId": "native Device ID", "includePath": true }`; `includePath` is optional and defaults to true. |
| `POST /monitor` | Only `{ "paused": true }`; controlled-test monitoring toggle. |

POSTs require `X-Tia-Prototype: 1`, reject cross-origin browser requests and reject unknown/duplicate fields. The maximum request body is 16 KiB to accommodate native identifiers. Discovery payloads contain `readAtUtc` and `errors`; targeted reads and connection failures retain the requested `processId`. These prototype routes are temporary development entry points, not new MCP tools or the final dashboard tool runner.

The selected Device response has `metadata` and `deviceItems`; each item has `children` and nullable `plcObjectId`. That PLC selector is the CPU DeviceItem's native identifier, not the PlcSoftware or rack identifier. Dynamic attributes use the installed V20 API's positional name-list `GetAttributes` overload with individual reads after a bulk failure. Unknown complex values are represented by native type and `valueSerialized: false`; they are never serialized as engineering proxies. Exact native field coverage, group paths and identifier behavior still need live discovery tests.

The previous `samePathReopenVerified: false` is replaced by `samePathReopenEvidence`, which records the date, user-reported result, tested scenario and limitations. The dashboard notice carries the same scope. Prototype faults now use `causeType`, `causeMessage` and `causeOrigin` instead of labeling every inner exception as native; Siemens exceptions retain `origin: "tia-openness"`, while bridge guards remain `origin: "bridge"`.

## Live verification procedure

Use two disposable test projects in separate TIA V20 UI processes. The prototype does not create, open, close, save, compile or modify projects. Project lifecycle actions below are performed deliberately by the user in TIA. Approve native external-access prompts in TIA when connecting.

1. Refresh the process list, connect each test process, and read each project. Record the native paths and timing fields.
2. With background checks running, close or switch one test project in TIA. Its connection should become invalidated; the second process should remain readable. Reconnect explicitly to approve the new context.
3. For the **same-path reopening test**, first connect and read the project, then pause background checks using the checkbox. Close and reopen that project at the same path in TIA. Do not reconnect the prototype. Click **Read project** and inspect the error and event log. This pause ensures background monitoring does not detect the temporary projectless state before we can test native identity/proxy behavior. If the read succeeds, the proposed mechanism has not demonstrated same-path reopen detection; record that limitation rather than marking the case passed. Resume background checks afterwards.
4. Disconnect one attachment. Verify its TIA window and project remain open and the other connection still reads successfully.
5. Exit one test TIA process and verify invalidation. If an OS process ID is later reused, it must not inherit the old connection.

A change during a native read and realistic timing/latency still need a controlled live scenario; simulated post-read transitions are only offline evidence. Headless creation and disposal are deferred entirely. Full MCP tools, project opening, persistent project tabs, PLC software inventories and export behavior remain later increments.

### Discovery increment checks

After deliberately loading the updated executable into the same managed server and reconnecting the test projects:

1. Refresh processes. Verify both connected states, UI mode and native project paths; discovery alone must not connect a process.
2. Read status for each process, including a disconnected and a projectless process when available. Check installed products/options and nullable native project version. A projectless connection permits status but rejects device reads.
3. List devices in each project. Compare root devices, nested user groups, the ungrouped system group and native order with TIA. DeviceItems must appear only in the selected Device read.
4. Copy a Device `objectId` from that process's result into its input, then **Read device**. Compare the hardware tree and CPU `plcObjectId`. Check HMI/non-PLC devices, multiple PLC scopes if available, unavailable identifiers and protected/unreadable attributes.
5. Compare `includePath: true` and `false`: matching list/read paths when enabled, `path: null` with parent traversal skipped when disabled. Check missing IDs and a CPU ID supplied as a Device ID; neither may trigger a broad inventory or reconnect.
6. Repeat the deliberate lifecycle checks with these new reads. Keep read-during-transition, queued-work/reconnect, process exit/PID reuse, hidden dashboard and overhead evidence separate from offline simulations.

### Discovery increment verification — 2026-09-21

- Release build passed with zero warnings/errors against the installed V20 API. To preserve the running server's two attachments, output was directed to `src/TiaOpennessMcpServer/bin/DiscoveryCheck/` using `-p:OutDir=bin/DiscoveryCheck/` and cached packages (`--no-restore`). This build does not load the new code into the running server.
- Offline harness: **51/51 groups passed**, including 13 additional discovery groups. Tests cover connection selection, disconnected/projectless status, old tickets after reconnection, detected transitions during every new read, partial enumeration, failure isolation, strict inputs, safe value conversion and explicit null serialization.
- Dashboard script smoke tests: **3/3 passed** with `node --test tests/connection-prototype-dashboard.test.cjs`. These use a minimal DOM and mocked fetch, not a real browser or HTTP listener; they check target/identifier preservation, clearing inputs on reconnection and no automatic retry/connect after guard errors.
- The running managed prototype was initially retained during implementation. At the user's request, the old server (PID `27784`) then exited gracefully, the normal Release output was rebuilt with zero warnings/errors, and one replacement prototype server (PID `85716`) started on port 5000. Passive HTTP checks confirmed the updated evidence field, new dashboard controls and an empty connection list before user reconnection. No concurrent second MCP server was started.
- V1 discovery/dispatch remains exactly eight tools; prototype mode still rejects MCP execution. The source migration and final MCP cutover remain a separate step governed by `project-rehaul.md`.

### User-executed discovery results — 2026-09-21

The user confirmed that the initial status/device-list checks looked correct, then supplied three Device-read JSON responses. The agent inspected those responses and compared the first process's path-enabled and path-disabled payloads; it did not independently replay these native reads or compare the hardware against the TIA UI.

| Scenario | Supplied evidence |
|---|---|
| Read Device in process `34636` with paths enabled | Device `hMbKvDx4QkSMnfG8ji74mg==`, eight DeviceItems, CPU `PLC_100` (`CPU 1511-1 PN`, firmware `V2.6`). CPU `objectId` and `plcObjectId` both equal `FbxBd++WREeJ3XSmOr1YXg==`; other items have null PLC selectors. `complete: true`, `errors: []`. |
| Repeat that Device read with paths disabled | Device metadata path and all eight DeviceItem paths are null. Device/item IDs, item names, parent-child relationships and CPU PLC selector match the preceding response. `complete: true`, `errors: []`. This verifies the returned path option behavior, not a runtime trace proving parent traversal was skipped. |
| Read Device in process `38568` | Distinct Device ID `h0mj4z3nBUW2iOmmgWScvA==`, eight DeviceItems, CPU `PLC_101`; CPU `objectId` and `plcObjectId` both equal `mF7QzMMCVkSpMne5mlqV4Q==`. `complete: true`, `errors: []`. This supports correct process targeting in the two supplied examples. |
| Supply the CPU DeviceItem ID to the Device reader, then retry with the valid Device ID without reconnecting | In process `38568`, CPU ID `mF7QzMMCVkSpMne5mlqV4Q==` returned bridge-owned `unsupportedObject`, message `The selected object is not a Device.`, and `reconnectRequired: false` at `2026-09-21T12:54:58.0213431+00:00`. The user then reported that reading Device `h0mj4z3nBUW2iOmmgWScvA==` worked without reconnecting. This supports wrong-type rejection and continued connection usability in this scenario; the successful retry was user-reported, with no new response payload supplied. |

Observed fields include station/rack/CPU metadata, CPU subitems, a PROFINET interface and two ports. Complex attributes such as `CommentML`, `Container` and `Items` appear as native-type markers with `valueSerialized: false`. These markers are intentional representation limits, not read errors. They do not establish coverage for other hardware variants or all available native information.

Remaining discovery checks include missing selectors, disconnected/projectless selected status, grouped/HMI/other hardware variants, partial native failures and lifecycle transitions during the new reads. The initial connection prototype's manual lifecycle evidence below remains separate from these discovery results.

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
