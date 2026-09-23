# Read contracts and shared engineering behavior

## Purpose and status

This document defines the twelve implemented read tools and shared engineering behavior. Full access also publishes twelve [modifying operations](write-operations.md), including explicit PLC compilation. Start at [current documentation](README.md) for the product boundary, source responsibilities and reading order. Reader details and scoped native evidence are in [cross-references](cross-references.md), [tag tables](tag-table-discovery-read.md), [UDTs](udt-discovery-read.md), [block reads](get-block.md) and [tag-table export](compile-delete-export.md).

MCP is the primary interface. Engineering operations follow native Openness behavior; the dashboard consumes their MCP contracts for testing and adds connection management and inspection. Its layout and implementation do not define tool requirements. Separate the operations, native calls, shared services, MCP endpoints, dashboard endpoints and dashboard assets as described in the current documentation.

Connect, Disconnect and Open project in TIA stay on the dashboard. Open project is only on a closed tab that has a stored project path. It starts one visible TIA window for that path and attaches it. The user reported on 2026-09-22 that this action works. A call that arrives after the connection is gone returns `notConnected`. A call accepted before the connection is lost returns `reconnectRequired` and is not run again after reconnect. These calls already wait on the single TIA worker; that line is not a retry queue.

Left out of this baseline, because they do not fit the project: headless startup and attachment, a project path typed on the Server tab, and any MCP tool that connects, disconnects or opens a project. Full access publishes twenty-four tools and explicit read-only access publishes twelve; [write operations](write-operations.md) defines the modifying contracts. Saving projects and PLC upload/download are permanently outside MCP. See [connection prototype usage and verification](../reference/history/connection-prototype.md) for earlier evidence.

The document is organized by tool so that each tool has one clear responsibility. Shared behavior is defined once and referenced by the tools that use it.

The implementation targets the installed TIA Portal V20 Public API. Source-format support and native verification remain scoped to the recorded versions and scenarios; do not infer an installed update or patch level from this document.

The rehaul follows a native-fidelity principle: MCP extensions may structure information that Openness does not expose as one ready-made response, but they preserve TIA identity, names, hierarchy and ordering whenever possible.

## Read tool map

| Tool | Responsibility | Contract status |
|---|---|---|
| `list_tia_processes` | List running TIA Portal processes, their optional primary project paths and this MCP server's connection state without attaching | Initial contract settled |
| `get_status` | Report bridge status or the connection state, primary-project provenance and installed TIA products for an explicitly selected process | Initial contract settled |
| `list_devices` | Inventory the native device-group tree and its Device objects | Initial contract settled |
| `get_device` | Read one Device's metadata and nested DeviceItem hardware tree and expose PLC software scopes | Initial contract settled |
| `list_blocks` | Inventory the block-group tree and its blocks | Initial contract settled |
| `get_block` | Read one block's native metadata and optional authoritative source | Initial contract settled |
| `list_udts` | Inventory the type-group tree and its UDTs | Initial contract settled |
| `get_udt` | Read one UDT's native metadata and optional authoritative source | Initial contract settled |
| `list_tag_tables` | Inventory the tag-table group tree and its tag tables | Initial contract settled |
| `get_tag_table` | Read one tag table's native metadata and optional typed tag and constant entries, including native identifiers | Initial contract settled |
| `get_cross_references` | Query native TIA cross-references for one supported engineering object | Initial contract settled |
| `export_tag_table` | Export one native PLC tag table as SimaticML XML with exact returned-content checksums | Implemented; populated-table round trip verified on 2026-09-23 |

Persistent PLC External Source objects are outside the initial inventory scope.

`find_plc_objects` is intentionally omitted. Blocks, UDTs, tag tables and devices are discovered through their type-specific inventory tools. A broad custom search over all engineering-object types has no identified workflow and would overlap those inventories.

### Current surface and legacy boundary

The table above is the read surface. Full access additionally exposes twelve modifying tools. Keep publication and dispatch aligned with the implemented contracts; the count is not a permanent prohibition on agreed new native operations. There are no aliases, compatibility tools or hidden legacy dispatch paths. `export_tag_table` takes only `processId` and the table's `objectId`; its fixed native SimaticML contract is separate from `get_tag_table` and is specified in [compile, deletion and export](compile-delete-export.md).

Connection management is exclusively user-controlled through the dashboard. **Connect**, **Disconnect** and **Open project in TIA** are dashboard actions backed by the shared connection service. The previously proposed `connect_to_tia_portal`, `disconnect_from_tia_portal` and `open_tia_project` are not advertised or accepted as MCP tools. Agents use the connections enabled by the user.

The completed source transition preserved the pre-transition source project, coupled offline tests and documentation from commit `8ebc151` in `reference/legacy-v1/`, then retired its engineering services and routes. That snapshot and the completed handoffs under `reference/history/` are inert comparison/evidence material only:

- New source code must not compile, reference or dispatch into it.
- The new project must not depend on its contracts, helpers or response models.
- A legacy behavior is adopted only when it is deliberately implemented under the rehaul contract.
- The reference copy is not a runtime fallback and is never part of the MCP tool surface.

## Shared response behavior

`get_status({ processId })` is the sole owner of full TIA Portal, installed-product and primary-project context for the selected connection. Other tools do not repeat that provenance packet. They return their own requested scope and identifiers.

Every tool response includes:

```json
{
  "readAtUtc": "UTC timestamp for this live operation",
  "errors": []
}
```

`readAtUtc` belongs to the individual live result. Separate calls are not an atomic project snapshot.

Responses to requests targeting a process also include that `processId` at the top level, including failures. It identifies the requested process and does not imply that the process is still connected. Object metadata and inventory leaves do not repeat it.

The error rules are:

- A complete successful operation returns `errors: []`.
- If `best` succeeds through a later fallback, the response identifies the format actually returned and still returns `errors: []`. Failed intermediate attempts remain available in the dashboard log.
- A partial inventory returns every readable branch, sets `complete: false` and records its unreadable branches in `errors`.
- If every source attempt fails, metadata is preserved, `source` is `null` and every failed native attempt is recorded in `errors`.
- An error originating from TIA Openness preserves the native Siemens message without reinterpretation and uses `origin: "tia-openness"`.
- Input validation and other bridge-owned failures use `origin: "bridge"` and a minimal bridge message. They are never presented as Siemens errors.

The MCP transport may mark a tool call as failed when the requested operation cannot return its primary payload, but the structured error information still follows this common shape. There is no second `sourceUnavailable` error packet.

## Shared core behavior for `get_*` tools

This section applies to the detailed object readers `get_device`, `get_block`, `get_udt` and `get_tag_table`. Operational queries such as `get_status` and `get_cross_references` define their own response behavior.

Every detailed reader requires `processId` to select an existing user-enabled connection and one Siemens `objectId` to select the object within that connection's primary project. The object is resolved directly through that project's provider:

```text
ObjectIdentifierProvider.Find(objectId)
```

Name and MCP-constructed path resolution are not part of the initial rehaul.

The detailed object readers use the same optional path behavior:

```text
includePath: true   -> resolve and return the current constructed path
includePath: false  -> skip parent traversal and return path: null
```

`includePath` is optional and defaults to `true`. When enabled, the tool walks from the resolved object through its parent objects and constructs the path using the same convention as the corresponding `list_*` tool. It does not enumerate sibling objects or rebuild the complete inventory.

Every detailed metadata packet has the stable field:

```json
{
  "path": "PLC_1/Program blocks/Folder/ObjectName"
}
```

The value is `string | null`. It is `null` when `includePath` is `false` or when the path cannot be resolved. Failure to resolve the path does not fail the object read and does not add a separate verbose path-error packet.

The path is MCP-constructed navigation information, not a native identifier. It must match the relevant `list_*` path exactly and is never an object selector; the Siemens `objectId` remains authoritative.

For source-owning readers, path-resolution cost should be benchmarked with source retrieval disabled so that export time does not hide the cost of parent traversal:

```text
includeSource: false, includePath: true
includeSource: false, includePath: false
```

### `typeSpecific` value conversion

Native `GetAttributes(AttributeAccessOptions)` returns attribute names paired with .NET values. Block, UDT and tag-table detail readers use one bulk call with `ReadOnly | ReadWrite`. Device discovery uses the name-list overload `GetAttributes(IEnumerable<string>)`, which returns positional values for the supplied names, preserving that association. Known attributes map into the stable metadata fields. Remaining readable attributes retain their Siemens names under `typeSpecific` and use one deterministic JSON conversion policy:

- `null`, strings, booleans and numbers remain their corresponding JSON values.
- Date and time values become ISO-8601 UTC strings.
- Siemens and .NET enums become their native enum-name strings.
- Simple collections of supported values become JSON arrays.
- An unknown complex value is not recursively serialized as a Siemens proxy. Its attribute name is retained together with its native .NET type and an explicit indication that its value was not serialized.

The actual attributes and value types exposed by the supported Device, DeviceItem, block, UDT, tag-table and tag/constant entry variants remain an implementation-time test surface. Observed fields are documented before the final `typeSpecific` contents are locked.

### Shared source behavior

`get_block` and `get_udt` always return metadata and include source by default. This source behavior does not apply to `get_tag_table`, which reads native typed entries instead:

```text
includeSource: true   -> metadata and source; this is the default
includeSource: false  -> metadata only; do not perform an export
```

`sourceFormat` defaults to `best`. `best` follows the format order defined by the selected tool and returns the first usable native representation. An explicitly requested format is strict: it is attempted once and never falls back.

External-source generation for blocks and UDTs also accepts:

```text
includeDependencies: false  -> native GenerateOptions.None; this is the default
includeDependencies: true   -> native GenerateOptions.WithDependencies
```

`includeDependencies: true` is valid only together with an explicit `sourceFormat: "external-source"`. This prevents `best` from falling back to a representation that cannot honor the dependency request. TIA Portal owns dependency discovery and source generation; the MCP does not traverse, parse or independently list the generated dependencies. The generated external-source document may therefore contain the selected object and multiple dependent source objects.

SIMATIC SD and SimaticML export do not expose this dependency option. The typed `get_tag_table` reader does not accept `includeDependencies`.

Every returned textual source document contains a reproducible SHA-256 checksum over its exact returned `content` string encoded as UTF-8 without a BOM. Line endings are preserved and no other normalization is performed. The checksum describes the returned MCP content, not the original encoding or byte layout of the temporary file written by TIA Portal:

```json
{
  "algorithm": "sha-256",
  "scope": "returned-content",
  "encoding": "utf-8-no-bom",
  "value": "hexadecimal checksum"
}
```

If source retrieval fails, the shared error behavior preserves metadata and returns `source: null` together with the failed native attempts in `errors`.

## Shared TIA Portal connection state

One running MCP server can retain Openness attachments to multiple TIA Portal processes simultaneously, with at most one retained attachment per process. The dashboard and all compatible MCP clients share this connection collection. There are no per-client attachments or implicit active-process selections.

```text
User in dashboard -> connect / disconnect / open project
    -> shared connection service
        -> attachment to TIA process A -> primary project A or none
        -> attachment to TIA process B -> primary project B or none

MCP clients -> operation with processId -> selected existing attachment
```

Only the user manages connections and opens projects through the dashboard. Connecting or disconnecting one process leaves other connections unaffected. An agent changes which project it uses by supplying another connected `processId`; it cannot attach, detach, open a project or replace another agent's connection through MCP. A user's disconnect affects every client using that particular connection.

All TIA Portal calls continue through the shared STA scheduler. Multiple connections can remain available while their engineering operations execute sequentially. Connection transitions and project operations are serialized. The dashboard log records calls through the shared service together with their client origin and the selected TIA process and project context.

The bridge distinguishes:

- The running TIA Portal process, identified by native `processId`.
- The process's optional primary-project path, observable without attachment through native `TiaPortalProcess.ProjectPath`. A path alone does not identify a particular opening of that project.
- The bridge's retained Openness attachment to that process and the approved primary-project context established when the user connected: the native `Project` object and its path, or an explicitly projectless context. Monitoring never silently replaces this baseline.
- A bridge-created `connectionId` used internally and in logs to distinguish attachment periods. It is not an MCP request selector.

There is no global native "active TIA window" or "active project across all processes" flag. Each process has zero or one primary project.

### Process targeting

Every project-scoped tool requires an integer `processId`: `list_devices`, `get_device`, `list_blocks`, `get_block`, `list_udts`, `get_udt`, `list_tag_tables`, `get_tag_table`, `get_cross_references`, `export_tag_table` and all twelve modifying tools. `list_tia_processes` is server-wide; `get_status` defines its server and selected-process forms separately.

The server resolves `processId` to an existing valid attachment before looking up `objectId` or `plcObjectId`. Identifiers and constructed paths are interpreted only within that connection's primary project. No project tool chooses the first connected process, falls back to another connection or attaches automatically. A disconnected or invalidated target returns a bridge-owned error; an attached process without a primary project returns `noActiveProject` for project operations.

An agent discovers running processes through `list_tia_processes`, selects one with `connectedByMcp: true` and supplies its `processId` on each operation. If the needed process is disconnected, the user must enable its connection in the dashboard.

Calls do not require an expected project, context revision or client-held connection token. After the user explicitly reconnects the same process, newly submitted calls target its newly approved context. The server cannot detect an agent's outdated assumptions about that process from `processId` alone.

### Connection invalidation

If a TIA process closes or its primary project changes, the server invalidates that process's connection and releases any remaining attachment. Other connections remain available. Project A's tab and logs are retained; project B's tab shows the same still-running process as disconnected until the user explicitly reconnects.

This is a bridge policy: the native Openness attachment is to the process, and changing its project does not itself require that attachment to be lost. Invalidation makes the connection unavailable immediately, then disposes the retained `TiaPortal` attachment on the shared STA worker. Cleanup failure must not restore its validity. Do not use `TiaPortalProcess.Dispose()` to detach: that method closes the associated TIA instance. The ownership rules under **Disconnect** also apply to automatic invalidation. This baseline does not start or attach to a headless TIA instance.

A primary-project change includes replacement, closing the project, a changed project path, or opening a project in a previously projectless connected process. Exact-path tab matching preserves history but never reauthorizes an invalidated connection. Reopening the same project also requires an explicit user connect or **Open project in TIA** action.

The server checks the retained runtime and primary-project context before executing a queued project operation, as well as during monitoring. Requests belonging to an invalidated attachment must fail instead of being redirected to a replacement project or a later attachment. A reused operating-system process ID does not inherit the earlier connection. This behavior must remain effective when no dashboard page is open or its polling is paused.

### Shared operation guard

Implement these checks once in the shared connection service; individual tools use the validated project supplied by that service:

1. When accepting a project request, bind it internally to the selected attachment's `connectionId`. Before execution on the STA worker, reject it if that attachment is no longer valid or has been replaced, even if the same `processId` is connected again.
2. Immediately before project access, obtain fresh process information and compare its project path, including `null`, with the approved baseline. `TiaPortalProcess` is a static snapshot; rereading the originally captured descriptor is not a fresh check. A missing process or changed path invalidates the connection and returns a reconnect-required error without executing the requested operation.
3. Also validate the retained native project against the currently open primary project. The V20 prototype uses native object equality through `.Equals()` together with detecting an unusable retained project object; path equality alone is insufficient. The user-executed same-path reopen test on 2026-09-21 rejected the retained context through the native-project mismatch guard; see [the recorded evidence and its limits](../reference/history/connection-prototype.md#manual-test-results--2026-09-21). If the project differs or the connection's project context cannot be validated, invalidate the connection and return a reconnect-required error without executing the operation or silently adopting another project.
4. Execute against the retained, validated project object. If the project context becomes invalid during execution, stop further project work, invalidate the connection and report the failure. Never reacquire a replacement project and automatically retry. Ordinary object-not-found, permission or export failures do not by themselves prove that the whole project connection changed.
5. For read operations, validate the context again before returning the result. If a change is detected or the project context can no longer be validated, discard the collected payload, invalidate the connection and return a reconnect-required error. This detects additional transitions but does not make the operation atomic or provide a snapshot of all project contents.

The user-facing error explains that the selected process's project context changed or could no longer be validated and that the user must reconnect. Preserve the underlying native failure when available. Other process connections remain usable.

Siemens documents the diagnostic interface as nonblocking, including while TIA is busy. Keep the fresh check on each project operation and measure its overhead in the prototype; do not replace it with potentially stale dashboard polling based on an assumed performance problem.

Native API references: [diagnostic snapshots and ProjectPath](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/diagnostic-interfaces-on-tia-portal), [native object equality](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/verifying-object-equality), and [attachment disposal](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/terminating-the-connection-to-the-tia-portal).

### First prototype and verification

The connection policy above is implemented. Completed live checks below are based on the user's manual V20 tests on 2026-09-21. Unchecked items are live evidence still open; the corresponding behavior is implemented and covered offline. See [prototype verification evidence](../reference/history/connection-prototype.md#verification-evidence).

- [x] Retain attachments to two user-selected TIA processes and verify that invalidating one leaves the other usable.
- [x] With background checks enabled, detect project closure, preserve the other connection, and require explicit reconnect after reopening.
- [ ] Verify fresh path checks for replacement, closing, path changes and a projectless process gaining a project, including with dashboard polling paused.
- [x] With background checks paused, close and reopen the same test project at the same path and reject a read through the old context. The supplied response reached the retained-native-project mismatch guard. Resume monitoring and verify that the other process remains readable and explicit reconnection restores the first.
- [ ] Verify a project change during a read returns an error without retrying against the replacement project or returning a payload from a detected invalid context.
- [ ] Queue a request, invalidate and reconnect its process, and verify the old request cannot execute through the new attachment. Check process exit and reused-PID handling as well.
- [x] Verify explicit attachment disposal leaves a user-started TIA UI instance and its project open, preserves the other process's connection, and permits explicit reconnection.
- [ ] Measure the guard overhead.
- Headless startup and attachment are omitted. This baseline creates no headless instance whose shutdown needs validation.

Lifecycle scenarios require isolated disposable test projects or deliberate user actions. They are not permission for the existing read-only tools to close, save or modify the user's project. Record observed V20 behavior separately from offline checks. The read-only cutover is done. The unchecked items above are remaining live evidence, not missing connection code.

## Browser dashboard

### Purpose and hosting

The dashboard exists to monitor the MCP server and exercise its published tools. It is served as a normal loopback web page by the same MCP server process and opened in the user's regular browser. The rehaul does not embed WebView or another browser renderer. Starting the server displays the dashboard URL but does not launch a browser automatically.

The current dashboard has one permanent **Server** tab plus the TIA tabs defined below. Presentation can evolve independently of the operation contracts; target selection and exact requests/results must remain clear.

The dashboard is not a second engineering API. It uses the shared internal connection service for dashboard connection operations, while tool testing uses the actual Streamable HTTP `/mcp` contract.

### Tool placement and execution

The **Server** tab owns server state, the `list_tia_processes` discovery view and server-wide logs. Each TIA tab owns user-only **Connect** and **Disconnect** controls, selected-process status and the project-scoped tool runner for its runtime and project history. A closed tab that still has a project path offers **Open project in TIA** for that stored path. The Server tab does not accept a project path.

Project-scoped tools are enabled in every tab whose process has a valid connection and a primary project. Selecting a tab changes only the view. Its tool tester automatically supplies and displays that tab's `processId` in the MCP request. Tab IDs and internal `connectionId` values are not tool arguments. The server records the requested process and its validated connection/project context so results and logs remain associated with the originating tab even if the user changes the selected tab while a call runs.

The dashboard consumes the published tool names, descriptions and `inputSchema` values. The current implementation renders HTML forms on the server from those definitions; that rendering location is an implementation choice, not a constraint on the tool contract or future dashboard structure. Submitting a tool form invokes MCP `tools/call` through `/mcp`.

### Passive monitoring

The browser needs to observe tool activity caused by AI agents, user connection actions and external TIA process or project changes without generating artificial MCP tool calls. Initial monitoring uses small dashboard-only state and incremental-log endpoints backed by shared server state and process diagnostic services. These endpoints do not duplicate engineering operations, and monitoring reads are not recorded as MCP tool executions.

A small generic browser polling loop refreshes tab state and requests only new log entries. Polling pauses while the page is hidden. Server-sent events, WebSockets and Datastar are outside the initial implementation.

The current dashboard uses polling. Any future presentation or monitoring change must preserve the shared host and operation boundaries; it does not define new engineering capabilities.

### Inputs, results and concurrency

**Open project in TIA** uses the closed tab's stored project path. There is no path field and no windowless choice. The action starts one visible TIA window.

Tool results show success or failure, elapsed duration, structured errors and inspectable raw JSON. Any additional presentation must preserve access to the authoritative request/response and remain separate from engineering semantics.

All Siemens engineering operations are queued through the shared `StaTaskScheduler`. STA means Single-Threaded Apartment: Siemens objects are created and accessed on one dedicated backend thread. The HTTP server itself is not single-threaded. While a TIA operation runs, the dashboard shows that operation and disables connection-changing controls; passive state and log reads remain available.

### TIA tabs

The dashboard presents one general TIA tab model with separate runtime, project and log sections. A tab is bridge-owned UI state, not an Openness object and never an MCP selector.

```text
TIA tab
    -> Runtime
        -> current processId or null
        -> mode
        -> running or closed
        -> connected or disconnected
        -> previous runtime and connection identifiers
    -> Project
        -> primaryProjectPath or null
        -> open or historical
    -> Logs
        -> chronological bridge-owned entries
```

The same tab model represents both a running TIA process without a project and a process with an open primary project:

| Runtime | Project | MCP connection | Dashboard action |
|---|---|---|---|
| Running | None | Disconnected | Connect |
| Running | None | Connected | Disconnect; project-scoped tools remain unavailable |
| Running | Open | Disconnected | Connect |
| Running | Open | Connected | Disconnect or use project-scoped tools |
| Closed | Previously had a project | Disconnected | **Open project in TIA** |
| Closed | Never had a project | Disconnected | View logs or dismiss the tab |

Selecting a tab changes only the dashboard view. It never attaches, disconnects or changes any Openness connection. Multiple TIA tabs can be connected simultaneously. Connecting tab B leaves tab A connected; disconnecting either tab preserves its project context and logs.

Closing a project while its TIA process keeps running archives that project tab and shows a separate tab for the projectless process. Opening a project there rejoins the archived tab when the path matches, and the temporary process tab is removed; logs from the gap stay on the project tab. A process that exits during that gap leaves only the archived project tab. A process that was projectless without an archived project keeps its own tab, including after it exits. If that process opens a project whose path has no archived tab, its existing tab gains the project and keeps its logs. Any existing connection is invalidated and project tools await explicit user reconnection. If one process changes from project A to project B, project A becomes historical and project B receives or reactivates its own tab. The current runtime is associated with B, but its connection is invalidated. The user must connect from B's tab before agents can use it.

When a process closes, its connection is invalidated and its tab remains in memory as historical. If it had an exact primary project path, the user can invoke **Open project in TIA** with that path. A successful open associates the new TIA process and connection with the existing historical tab, updates its current `processId` and appends new logs to the existing timeline. The old process and Openness session remain historical identifiers. Other process connections are unaffected.

An exact canonical `ProjectPath` is the only basis for matching a newly observed or reopened project to an existing historical tab. If a project was moved or renamed while closed, reopening uses the stored path and reports the resulting file-not-found error. The bridge does not search for the project.

If `ProjectPath` changes while the same TIA process remains running, the dashboard can observe the change during process refresh but cannot reliably distinguish Save As from opening a different project. Every path change is treated as a project transition: the old project tab becomes historical, the new exact path receives its own tab and the connection is invalidated until explicit user reconnection.

Runtime properties such as process ID, UI/headless mode and connection ID remain separate from project information even though they are presented together. A historical tab can accumulate several runtime process IDs and Openness connection IDs across exact-path reopenings.

Discovering a running process that matches a historical project's exact path can update the tab's runtime information, but never automatically connects it. Historical tabs and logs are dashboard history, not live engineering state or connection authority.

### Logs

The MCP server records its own MCP tool calls, dashboard operations and applicable debug events. It does not enumerate external Openness sessions and does not attempt to obtain logs from other applications.

Every process-associated log entry retains at least:

- TIA `processId` when one applies.
- A bridge-created `connectionId` when one applies.
- Client origin such as `mcp` or `dashboard`.
- Operation or tool name.
- Timestamp, duration and result or error.

Logs from successive runtimes and connections are appended to the same matched tab without erasing their original identifiers. Events without a TIA process, such as server startup and process-enumeration failures, belong to one permanent **Server** tab.

Logs and historical tabs are bounded in-memory dashboard state. They are not persisted and disappear when the MCP server process restarts. Dismissing a historical tab removes only its dashboard history; it never closes a TIA process or project. Detailed logs remain a dashboard/debug API concern and are not exposed as a separate MCP tool.

## `list_tia_processes`

### Purpose

`list_tia_processes` is the read-only discovery operation for running TIA Portal instances. It uses the native non-blocking process diagnostic interface and never calls `Attach()` merely to enrich the list.

It takes no target `processId` and reports both connected and disconnected running processes. The dashboard uses the same discovery service for user connection management. Agents can inspect the list but can operate on project content only through connections the user has enabled. Closed processes appear only in dashboard history, not in this live process list.

Each process entry contains:

```json
{
  "processId": 1234,
  "mode": "with-ui | headless",
  "primaryProjectPath": "C:\\Projects\\Line1\\Line1.ap20 or null",
  "connectedByMcp": true
}
```

`primaryProjectPath` is the native `TiaPortalProcess.ProjectPath`. It is `null` when the process has no primary project. The bridge does not attach to retrieve `Project.Name` and does not present a filename-derived value as native project metadata.

`connectedByMcp` is bridge-owned state. It is `true` when this server retains a valid user-enabled connection to the listed process. Multiple entries may be `true` simultaneously. It becomes `false` on disconnect or invalidation. External Openness sessions and their counts are not part of the response. No separate `list_connections` tool or public `connectionId` selector is needed for this initial model.

The returned diagnostic values are a point-in-time native snapshot. A listed process can exit, change its primary project or be disconnected by the user before a later operation. That operation validates the targeted connection and reports the resulting native or bridge-owned error without choosing another process or reconnecting.

### Project scope

The initial tool reports only the optional primary project represented by `ProjectPath`. It does not attach and enumerate `TiaPortal.Projects`.

TIA Portal GUI Reference Projects are not part of the supported Openness project-access model and are outside scope.

Openness has a separate `ProjectOpenMode.Secondary` mechanism that can open additional read-only projects inside a TIA Portal instance. These secondary projects are not displayed in the TIA GUI, have `Project.IsPrimary == false`, and are accessible through the instance's `TiaPortal.Projects` composition. Secondary-project discovery and use remain outside the initial surface. The V20 Demo-to-Assembler secondary-project probe reached 43 object rows through navigation/getters, but every tested `GenerateSource`/`Export` path was rejected in the read-only context. That evidence does not establish complete source access through secondary projects. Multi-project source workflows in this rehaul use normal primary projects in separate user-connected processes.

## Dashboard connection actions

These actions are available only to the user through the dashboard. They use the shared connection service and its logging and STA execution rules. They are excluded from MCP discovery and dispatch in every access profile; the MCP tool tester cannot invoke them as tools.

### Connect

**Connect** attaches the bridge to one already-running TIA Portal process selected by native `processId` from the dashboard's process list or TIA tab.

| Current state | Result |
|---|---|
| The selected process is running and disconnected | Attach to that exact process and add its connection |
| The selected process already has a valid connection | Reuse that attachment |
| Process A is connected and the user connects process B | Add B's connection and keep A connected |
| Attaching to the selected process fails | Return the native or bridge-owned error; preserve every other connection |
| The listed process has exited or the ID is otherwise unavailable | Return an exact selection error; preserve every other connection |
| The selected process's earlier connection was invalidated by a project change | Explicit user connection establishes a new attachment for the current project context |

`processId` is the only attachment selector. Project name, project path and enumeration order are not attachment selectors. The service retains the selected runtime identity and current primary-project context for subsequent validation.

Attaching to a projectless TIA process is allowed. Selected-process status remains available; project-scoped tools require a primary project and otherwise return `noActiveProject`. If a project is subsequently opened in that process, the connection-invalidation rule requires explicit user reconnection for the new context.

External-access approval remains in the TIA Portal UI when TIA requests it. Releasing an attachment to a user-started process must only detach this bridge: it must not save or close the user's project or TIA Portal instance.

### Open project in TIA

**Open project in TIA** is available on a closed tab that has a stored project path. The request supplies that tab id. The server uses the stored path and does not accept a path typed for this action. It starts a new visible TIA Portal instance, opens that exact project as its primary project, and adds the attachment to the server's connection collection. It does not open the project in a TIA process that is already running, and it does not replace the project in another connected process. If any running TIA already has that path, the action is refused before a new instance starts.

Creating the TIA Portal instance already establishes the Openness attachment. After the project opens successfully, its context is recorded and the tab becomes connected without a separate **Connect** action.

A successful open leaves all existing process connections intact. A failed start or open also preserves them and closes only the incomplete instance created by this action. Exact project-path matching reuses a historical tab and appends logs while updating its runtime information.

The action uses the native current-version open operation and never upgrades a project. Authentication follows native TIA Portal behavior. The bridge does not accept project credentials. A missing file is reported and no TIA is left running for that attempt. Headless startup is not offered. Disconnecting afterwards releases the attachment and leaves the visible TIA window open.

### Disconnect

**Disconnect** targets the TIA tab's `processId` and releases only that process's retained Openness attachment. It is idempotent: disconnecting an already-disconnected target succeeds without side effects. The tab and its logs remain available.

The user action affects every client using that process; agents cannot invoke it through MCP. Other process connections remain intact. Disconnecting does not save or close an externally owned project and does not terminate a user-started TIA Portal process.

This baseline does not create a headless TIA instance. Disconnect releases the attachment to a visible window and leaves that window open.

## `get_status`

### Purpose

`get_status` reports server or selected-process operational state. It does not perform project inventory or change connections.

### Input and scope

```text
get_status()
    -> bridge status only; no implicit TIA process selection

get_status({ processId })
    -> bridge status and status of that process's connection
    -> native TIA and primary-project context when the connection is valid
```

The no-argument form remains available when no processes are connected. It reports server facts such as the access profile and write-tool availability, without aggregating project metadata or duplicating `list_tia_processes`. TIA-dependent status always requires `processId`, even if only one process is connected.

For a disconnected target, the targeted form reports the disconnected state and leaves attachment-dependent context unavailable; it never attaches to enrich the result. If the target is connected without a primary project, it returns TIA context with no project context. A missing process or invalidated context follows the shared error rules.

### Response responsibility

For the selected process, the response owns:

- Connected or disconnected state.
- Point-in-time read timestamp.
- Requested `processId` and native TIA process mode when available.
- Primary-project name, path, native version and modified state when a primary project is available through the retained attachment.
- Attached TIA Portal version and native installed products and options when available.
- Current access profile and whether MCP write tools are available.

Installed products describe the selected attached TIA Portal process. They are not the products used by its primary project. Devices, project-used products and PLC engineering objects do not belong in `get_status`.

## Shared rules for `list_*` inventory tools

This section applies to the project-scoped hierarchy tools `list_devices`, `list_blocks`, `list_udts` and `list_tag_tables`. `list_tia_processes` uses the separate native process diagnostic interface defined above.

Every project inventory requires `processId`. PLC software inventories additionally require `plcObjectId`, resolved only within that connected process's primary project. The connection is validated before native traversal begins.

### Native hierarchy ownership

Each inventory tool traverses only its corresponding native Openness compositions:

| Tool | Native hierarchy |
|---|---|
| `list_devices` | Device groups and devices; nested device items belong to `get_device` |
| `list_blocks` | Block groups and blocks |
| `list_udts` | Type groups and UDTs |
| `list_tag_tables` | Tag-table groups and tag tables |

The MCP reconstructs each tree from its corresponding native compositions. It must not perform one broad PLC-object inventory and then reuse that result as the three PLC software inventory responses.

Object leaves return Siemens `objectId` values. Group nodes reconstruct hierarchy and do not require an `objectId` when Openness does not provide one.

Paths and parent paths are MCP-constructed navigation values owned by the inventory tools. A `get_*` tool may reconstruct the same path through the shared optional path behavior, but paths are not selectors.

### Common PLC software inventory envelope

`list_blocks`, `list_udts` and `list_tag_tables` return identity, hierarchy and only enough classification to understand each item:

```json
{
  "readAtUtc": "UTC timestamp",
  "processId": 1234,
  "plcObjectId": "Siemens CPU DeviceItem object identifier",
  "complete": true,
  "errors": [],
  "roots": [
    {
      "kind": "scope",
      "scopeType": "plcSoftware | softwareUnit | safetyUnit",
      "name": "PLC_1",
      "path": "PLC_1",
      "isSafety": false,
      "children": []
    }
  ]
}
```

A tree can contain:

- Scope nodes for PLC software, software units and safety units.
- Group nodes for the tool's native hierarchy.
- Object leaves for blocks, UDTs or tag tables.

System and safety scopes, groups and objects are included whenever Openness permits them to be enumerated. `isSystem` and `isSafety` describe native classification; they are not filtering rules.

Detailed attributes such as header values, timestamps, protection, `typeSpecific`, checksums and source content do not belong in list responses. The corresponding `get_*` tool owns that information.

### Partial results

If one branch cannot be enumerated, the tool returns every readable branch with `complete: false`. One unreadable branch must not erase the rest of the inventory.

The error structure follows the shared response behavior and remains lean:

```json
{
  "origin": "tia-openness",
  "operation": "enumerate",
  "path": "PLC_1/System blocks",
  "message": "Native TIA Openness message"
}
```

The MCP identifies the failed branch with its constructed path and preserves the native TIA Openness message without reinterpretation.

### Native ordering

Every inventory tool preserves the enumeration order returned by the native TIA Openness compositions. The MCP does not alphabetically sort or otherwise rearrange scopes, groups, devices or object leaves. `get_device` separately preserves native DeviceItem order within the selected Device.

### Filtering

Filtering is outside the initial rehaul scope. Every `list_*` tool returns its complete available native hierarchy.

### Pagination

The initial rehaul returns the complete available tree without pagination. Pagination may be introduced later only if actual project size or measured performance justifies it.

## `list_devices`

### Purpose

`list_devices` inventories the native device-group hierarchy and its `Device` objects. It is a lightweight station-level inventory; it does not enumerate the nested hardware of every device.

### Input

```text
list_devices({ processId })
```

The inventory begins at the primary project of that user-enabled connection.

### Native hierarchy

The tool follows the Openness compositions corresponding to the TIA project navigation:

```text
Project device groups and system groups
    -> Device
```

Root-level devices remain root device nodes. Devices inside user or system groups remain under those groups. The device-group tree and native enumeration order are preserved.

### Device classification boundary

All top-level hardware targets are native `Device` objects. The initial contract does not invent a closed enum such as `plc`, `hmi`, `drive` or `pcStation`.

The response preserves native device identification through `objectId`, `Name`, `TypeIdentifier` and `IsGsd`. It does not inspect every `DeviceItem` merely to derive a device category or discover PLC software.

A derived device-category enum and category filtering may be introduced later if a concrete workflow justifies them. They are not part of the initial implementation.

### Response

```json
{
  "readAtUtc": "UTC timestamp",
  "processId": 1234,
  "complete": true,
  "errors": [],
  "roots": [
    {
      "kind": "deviceGroup",
      "name": "Production line",
      "path": "Production line",
      "isSystem": false,
      "children": [
        {
          "kind": "device",
          "objectId": "Siemens identifier or null",
          "name": "PLC_Station",
          "path": "Production line/PLC_Station",
          "typeIdentifier": "native TIA type identifier",
          "isGsd": false
        }
      ]
    }
  ]
}
```

Completeness refers to the device-group and Device hierarchy only. Nested racks, CPUs, modules, submodules and interfaces are deliberately deferred to `get_device`.

### Workflow

```text
list_devices({ processId })
    -> device objectId

get_device({ processId, objectId })
    -> nested DeviceItem tree
    -> plcObjectId
```

## `get_device`

### Purpose

`get_device` reads one native `Device` and expands only that device's nested hardware. This keeps `list_devices` lightweight while preserving the complete native hardware hierarchy when it is actually needed.

### Selector and input

```text
get_device({ processId, objectId })
get_device({ processId, objectId, includePath: false })
```

`processId` selects the connection; the Siemens Device `objectId` selects the object within its primary project. The shared optional path behavior applies. Unlike block and UDT readers, `get_device` has no source representation and no `includeSource` or `sourceFormat` argument.

### Native hierarchy

```text
Device
    -> DeviceItem
        -> nested DeviceItem
```

A `Device` is the container for a central or distributed station. `DeviceItem` objects are its racks, CPUs, head modules, modules, submodules and interfaces. The familiar GUI name `PLC_1` can appear on both the station and its CPU item; object type and identity must come from Openness, not from the displayed name.

The nested tree preserves native parent-child relationships and native enumeration order. Network nodes, addresses, channels, hardware identifiers and product-specific services are separate Openness information areas and are not automatically folded into this general device read.

### Response responsibility

The response contains:

- Device metadata including `objectId`, optional constructed `path`, `name`, `typeIdentifier`, `isGsd`, `typeName`, `author` and the non-multilingual comment view when readable.
- The complete nested DeviceItem hierarchy for that Device.
- Core DeviceItem attributes such as `objectId`, `name`, `typeIdentifier`, `typeName`, `classification`, `positionNumber`, `isBuiltIn`, `isPlugged`, `isGsd`, `orderNumber` and `firmwareVersion` when applicable.
- `plcObjectId` on each DeviceItem whose `SoftwareContainer.Software` is a `PlcSoftware`.

The `plcObjectId` is the Siemens identifier of the PLC-owning CPU DeviceItem, not the identifier of its rack or parent Device. The caller never supplies a rack identifier to access PLC software.

### PLC discovery flow

```text
User connects the desired TIA process in the dashboard
    -> connection becomes available to all MCP clients

Agent:
list_tia_processes()
    -> select processId by primaryProjectPath and connectedByMcp: true
    -> list_devices({ processId })
    -> Device objectId
    -> get_device({ processId, objectId })
    -> CPU DeviceItem plcObjectId
    -> list_blocks({ processId, plcObjectId })
    -> block objectId
    -> get_block({ processId, objectId })
```

Internally, a PLC software inventory begins from the returned CPU identifier within the selected connection's primary project:

```text
ObjectIdentifierProvider.Find(plcObjectId)
    -> DeviceItem
    -> SoftwareContainer
    -> PlcSoftware
```

If a caller already retains a valid `processId` and a `plcObjectId` or block `objectId` for that connection's current project, the earlier discovery calls are unnecessary. `get_block({ processId, objectId })` remains a direct lookup and does not require Device, rack or PLC identifiers. Choosing another connected process changes only the target of subsequent calls and leaves the first connection available to other agents.

## `list_blocks`

### Purpose

`list_blocks` inventories the block-group hierarchy and all readable blocks under one PLC software scope. It owns block navigation and discovery; it does not export block source or return the complete block metadata packet.

### Input

```text
list_blocks({ processId, plcObjectId })
```

### Block leaf

```json
{
  "kind": "block",
  "objectId": "Siemens block object identifier",
  "name": "MotorControl",
  "path": "PLC_1/Program blocks/MotorControl",
  "blockType": "FB",
  "number": 12,
  "programmingLanguage": "SCL",
  "isSystem": false,
  "isSafety": false
}
```

The response uses the shared inventory envelope and partial-result behavior.

### Relationship to `get_block`

```text
list_blocks({ processId, plcObjectId })
    -> block objectIds and block-group tree

get_block({ processId, objectId })
    -> direct native lookup, metadata and optional source
```

The caller retains returned identifiers. `get_block` does not rebuild or refresh block inventory when an identifier fails. It returns `objectNotFound`, after which the caller may explicitly call `list_blocks` again.

Block inventory belongs to the selected connection's primary project. Refresh it after a project transition and explicit user reconnection, and after writes affecting that inventory. Identifiers from project A must not be reused as project B's inventory.

## `get_block`

### Purpose

`get_block` reads one PLC block. It always returns native block metadata and returns the authoritative source by default.

### Selector

After validating `processId`, the tool resolves the block through the selected primary project's native Siemens provider:

```text
ObjectIdentifierProvider.Find(objectId)
```

`processId` selects the connection, and the Siemens `objectId` is the only object selector. Name and MCP-constructed path resolution are not part of `get_block`.

### Input behavior

```text
get_block({ processId, objectId })
    -> metadata + best available source

get_block({ processId, objectId, includeSource: false })
    -> metadata only

get_block({ processId, objectId, sourceFormat: ... })
    -> metadata + exact requested source format

get_block({ processId, objectId, sourceFormat: "external-source", includeDependencies: true })
    -> metadata + native external source generated with dependencies

get_block({ processId, objectId, includePath: false })
    -> metadata with path: null, without parent traversal
```

Metadata cannot be omitted. `includePath` defaults to `true` according to the shared `get_*` behavior. When `includeSource` is `false`, the MCP must not export the block merely to obtain additional metadata.

### Response

```json
{
  "readAtUtc": "UTC timestamp",
  "processId": 1234,
  "metadata": {
    "objectId": "Siemens object identifier",
    "path": "string or null",
    "name": "Scl_Block",
    "blockType": "FB",
    "number": 2,
    "autoNumber": false,
    "namespace": "string or null",
    "programmingLanguage": "SCL",
    "memoryLayout": "Optimized",
    "header": {
      "author": "string",
      "family": "string",
      "userDefinedId": "string or null",
      "version": "0.1"
    },
    "state": {
      "isConsistent": true,
      "isKnowHowProtected": false
    },
    "timestamps": {
      "created": "UTC timestamp",
      "modified": "UTC timestamp",
      "compiled": "UTC timestamp",
      "codeModified": "UTC timestamp",
      "interfaceModified": "UTC timestamp",
      "parameterModified": "UTC timestamp",
      "structureModified": "UTC timestamp"
    },
    "typeSpecific": {
      "...": "remaining readable native Openness attributes"
    }
  },
  "source": {
    "format": "external-source | simatic-sd | simatic-ml",
    "dependenciesIncluded": false,
    "documents": [
      {
        "name": "source document name",
        "content": "authoritative exported content",
        "checksum": {
          "algorithm": "sha-256",
          "scope": "returned-content",
          "encoding": "utf-8-no-bom",
          "value": "hexadecimal checksum"
        }
      }
    ]
  },
  "errors": []
}
```

Source is a document list because some representations, particularly SIMATIC SD, contain more than one document. Every document includes a SHA-256 checksum over its exact returned content. No source checksum is returned when source is omitted.

### Metadata retrieval

Block metadata is read with one native bulk attribute operation:

```text
GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite)
```

The MCP maps known Siemens attribute names into the stable metadata structure. Every remaining readable native attribute is placed under `typeSpecific` with its Siemens name preserved. An attribute returned by the bulk operation must not also be fetched separately through its typed property.

`typeSpecific` remains exploratory. It must be tested across supported block types, languages, PLC families and protection states. The observed native attributes must be documented before that part of the schema is locked.

Bulk attributes do not replace the other native responsibilities:

- `ObjectIdentifierProvider` owns identity and lookup.
- Runtime block types distinguish OB, FB, FC and the DB variants.
- Openness compositions own hierarchy and group traversal.
- Native export operations own source content.

### Source formats

The preferred source representation depends on the block:

| Block | Preferred representation in TIA Portal V20 Update 0 |
|---|---|
| SCL block | External source `.scl` |
| Data block | External source `.db` |
| Pure LAD block | SIMATIC SD |
| FBD block | SimaticML |
| GRAPH block | SimaticML |
| Mixed-language block | SimaticML |

`sourceFormat` accepts `best`, `external-source`, `simatic-sd` or `simatic-ml`. It defaults to `best`.

| Block | `best` attempt order in TIA Portal V20 Update 0 |
|---|---|
| SCL block | External source, then SimaticML |
| Data block | External source, then SimaticML |
| Pure LAD block | SIMATIC SD, then SimaticML |
| FBD block | SimaticML |
| GRAPH block | SimaticML |
| Mixed-language block | SimaticML |

`best` continues through applicable formats until one complete native representation succeeds. An explicitly requested format is attempted exactly once and never falls back. The response identifies the representation actually returned.

The implemented routing extension uses external source (.awl), then SimaticML for STL. Unlisted or unknown native language values use SimaticML. A native LAD language value is eligible for a SIMATIC SD attempt; it is not independent proof that all networks are pure LAD. If the native exporter rejects mixed content or returns PartialSuccess, best falls back to SimaticML. No source parsing is used to preclassify networks.

The source is authoritative exported content. The MCP does not derive the metadata packet by parsing or normalizing that source.

### Source failure

If no source representation succeeds, metadata is still returned:

```json
{
  "readAtUtc": "UTC timestamp",
  "processId": 1234,
  "metadata": {
    "...": "native block metadata"
  },
  "source": null,
  "errors": [
    {
      "origin": "tia-openness",
      "operation": "sourceExport",
      "format": "external-source",
      "message": "Native TIA Openness message"
    }
  ]
}
```

Block protection is metadata, not an MCP filtering rule. The MCP attempts every applicable native export and returns everything TIA Portal permits it to export. A protected or limited representation returned by TIA is preserved unchanged together with its protection and content-scope information.

The MCP does not attempt passwords or unlocking. A native refusal is preserved in `errors`.

### Header text and comments

The TIA Portal GUI fields **Title**, **Comment**, and **User-defined ID** are separate values. In the V20 Openness API, `HeaderName` maps to **User-defined ID**, so the MCP exposes it as `header.userDefinedId`. It is not the block name or title.

V20 does not expose the GUI block Title and Comment as direct `PlcBlock` properties. They are not normalized into the metadata packet. If the selected representation contains them, they remain part of the authoritative source.

Interface comments and program comments also remain part of the source. This version does not return a separate multilingual-content structure.

## `list_udts`

### Purpose

`list_udts` inventories the type-group hierarchy and all readable UDTs under one PLC software scope. It owns UDT navigation and discovery, not detailed UDT metadata or source export.

### Input

```text
list_udts({ processId, plcObjectId })
```

### UDT leaf

```json
{
  "kind": "udt",
  "objectId": "Siemens UDT object identifier",
  "name": "t_Item",
  "path": "PLC_1/PLC data types/t_Item",
  "isSystem": false,
  "isSafety": false
}
```

The response uses the shared inventory envelope and partial-result behavior. The detailed `get_udt` selector and response schema are not inferred by `list_udts`.

## `get_udt`

### Purpose

`get_udt` is the dedicated owner of one UDT's detailed metadata and authoritative source. These details must not be duplicated in `list_udts`.

### Selector and input behavior

```text
get_udt({ processId, objectId })
    -> metadata + best available source

get_udt({ processId, objectId, includeSource: false })
    -> metadata only

get_udt({ processId, objectId, sourceFormat: ... })
    -> metadata + exact requested source format

get_udt({ processId, objectId, sourceFormat: "external-source", includeDependencies: true })
    -> metadata + native external source generated with dependencies
```

`processId` selects the connection; the Siemens UDT `objectId` selects the object within its primary project. Metadata cannot be omitted. The shared `includePath`, optional-source, strict-format and source-failure behavior applies.

### Metadata

The initial stable UDT metadata contains:

```json
{
  "objectId": "Siemens UDT object identifier",
  "path": "string or null",
  "name": "t_Item",
  "namespace": "string or null",
  "state": {
    "isConsistent": true,
    "isKnowHowProtected": false
  },
  "timestamps": {
    "created": "UTC timestamp",
    "modified": "UTC timestamp",
    "interfaceModified": "UTC timestamp"
  },
  "typeSpecific": {
    "...": "remaining readable native Openness attributes"
  }
}
```

Metadata is read through the native `PlcType` object and one bulk `GetAttributes` operation using the same known-field and exploratory-`typeSpecific` rule as `get_block`. A block metadata header is not imposed on a UDT.

### Source formats

The preferred UDT representation is an external source `.udt` document.

`sourceFormat` accepts `best`, `external-source`, `simatic-sd` or `simatic-ml`. It defaults to `best`.

```text
best:
external-source (.udt)
    -> SIMATIC SD
    -> SimaticML
```

The `.udt` source is generated through the UDT's containing `PlcExternalSourceSystemGroup`. SIMATIC SD uses native `PlcType.ExportAsDocuments`, and SimaticML uses native `PlcType.Export`. These are internal format-specific operations behind the one `get_udt` surface.

An explicit format is strict and never falls back. Source protection is metadata, not an MCP filtering rule; the tool attempts every applicable native operation and returns everything TIA Portal permits it to export.

## `list_tag_tables`

### Purpose

`list_tag_tables` inventories the tag-table group hierarchy and all readable tag tables under one PLC software scope.

### Input

```text
list_tag_tables({ processId, plcObjectId })
```

### Tag-table leaf

```json
{
  "kind": "tagTable",
  "objectId": "Siemens tag-table object identifier",
  "name": "Default tag table",
  "path": "PLC_1/PLC tags/Default tag table",
  "isSystem": false,
  "isSafety": false
}
```

The response uses the shared inventory envelope and partial-result behavior.

`list_tag_tables` returns tag-table groups and tag tables only. It does not return tag or constant entries or detailed tag-table metadata.

## `get_tag_table`

### Purpose

`get_tag_table` is the dedicated owner of one tag table's detailed metadata and native typed tag and constant entries. It exposes entry identifiers so callers can request cross-references for individual tags and other supported entries. These details must not be duplicated in `list_tag_tables` or a separate `list_tags` or `get_tag_table_entries` tool.

### Selector and input behavior

```text
get_tag_table({ processId, objectId })
    -> metadata + all tags, user constants and system constants

get_tag_table({ processId, objectId, includeEntries: false })
    -> metadata only

get_tag_table({ processId, objectId, includePath: false })
    -> metadata with path: null + all entries, without parent traversal
```

`processId` selects the connection; the Siemens tag-table `objectId` selects the object within its primary project. Lookup remains direct through `ObjectIdentifierProvider.Find(objectId)`, with no name or path selector. Metadata cannot be omitted, and the shared `includePath` behavior applies.

`includeEntries` defaults to `true`. When it is `false`, the tool returns `entries: null` and does not enumerate the table's entry compositions. It does not export the table to obtain metadata. For path benchmarking, use `includeEntries: false` and compare `includePath: true` with `includePath: false`.

This tool does not accept `includeSource`, `sourceFormat` or `includeDependencies`.

### Response and metadata

The table metadata retains its initial stable fields. Entries are grouped by their native compositions; the following empty-table example shows the response structure:

```json
{
  "readAtUtc": "UTC timestamp",
  "processId": 1234,
  "metadata": {
    "objectId": "Siemens tag-table object identifier",
    "path": "string or null",
    "name": "Default tag table",
    "isDefault": true,
    "timestamps": {
      "modified": "UTC timestamp"
    },
    "typeSpecific": {
      "...": "remaining readable native Openness attributes"
    }
  },
  "entries": {
    "tags": [],
    "userConstants": [],
    "systemConstants": []
  },
  "complete": true,
  "errors": []
}
```

Table metadata is read through the native `PlcTagTable` object and bulk attributes. `complete` describes the requested read; intentionally omitting entries does not make the result incomplete.

### Native entries and identifiers

The tool enumerates all entries in `PlcTagTable.Tags`, `PlcTagTable.UserConstants` and `PlcTagTable.SystemConstants`, preserving native order within each separate collection. It reads properties and attributes from those native objects; it does not parse XML.

Each entry carries:

- `objectId`: its own native identifier, obtained through the selected primary project's `ObjectIdentifierProvider.GetIdentifier(entry)`, not the table's identifier or an XML-local ID.
- `name` and `dataType`: the corresponding readable native values.
- `logicalAddress` for tags, or `value` for constants, where exposed by the native API.
- Optional `typeSpecific`: remaining readable native attributes, excluding fields already mapped above, using the shared JSON conversion policy.

Native identifier support and the exact readable attributes must be verified for each entry type during implementation. If Openness cannot provide an identifier, return `objectId: null`; never invent one. An entry identifier does not itself guarantee `CrossReferenceService` support, which remains checked by `get_cross_references`.

Unreadable entries or attributes do not discard readable table metadata or entries. Partial reads set `complete: false` and preserve failures in the shared `errors` field. An empty array means a successfully read empty collection; a collection that cannot be read is `null` with an error.

### Representation boundary

The JSON structure is MCP-constructed from native Openness objects and attributes. It is not a native exported document, a parsed SimaticML model or a claim of complete equivalence with every field in a SimaticML export.

`PlcTagTable.Export` is exposed by the separate `export_tag_table` tool. Export remains outside this typed detail reader: `get_tag_table` has no source-format path or XML fallback. The existing typed-reader scope excluding multilingual content remains unchanged; export returns the native XML text without rebuilding it from these entries.

No checksum is calculated or returned for tag-table metadata or entries in the initial rehaul. Source checksums for `get_block` and `get_udt` are unchanged.

## `get_cross_references`

### Purpose

`get_cross_references` queries TIA Portal's native `CrossReferenceService` for one engineering object. It owns reference information and does not duplicate object metadata or source readers.

### Selector and lookup

```text
get_cross_references({ processId, objectId })
```

`processId` selects the connection; the Siemens `objectId` selects the object within its primary project. The tool resolves it directly through that project's `ObjectIdentifierProvider.Find(objectId)` and requests `CrossReferenceService` from the resolved object. It does not rebuild the complete PLC inventory to resolve or enrich the target.

Applicability is service-driven. Blocks, DB variants, PLC tags, system constants and UDTs are among the Step 7 objects documented to expose the service. If TIA Portal returns no service for the selected object, the tool returns `unsupportedObject`; the MCP does not maintain a second manual support classification.

For a PLC tag, the discovery flow is `list_tag_tables` -> `get_tag_table` -> `get_cross_references`, retaining the same `processId`. The last call uses the selected tag's non-null `objectId` from `entries.tags`, not the containing table's identifier. The same route applies to constants when Openness provides both an identifier and the cross-reference service.

### Query and response boundary

The initial implementation uses the native `CrossReferenceFilter.AllObjects` query and does not expose additional filters without a concrete workflow.

The native result is preserved as the hierarchy returned by Openness:

```text
CrossReferenceResult
    -> Sources: SourceObject[]
        -> Children: SourceObject[]
        -> References: ReferenceObject[]
            -> Locations: Location[]
```

The initial response follows that hierarchy rather than flattening it into MCP-created `uses` and `usedBy` arrays:

```json
{
  "readAtUtc": "UTC timestamp",
  "processId": 1234,
  "sources": [
    {
      "name": "MotorControl",
      "path": "Program blocks/MotorControl",
      "typeName": "FB",
      "device": "PLC_1",
      "address": "FB2",
      "objectId": "Siemens identifier or null",
      "children": [],
      "references": [
        {
          "name": "MotorDB",
          "path": "Program blocks/MotorDB",
          "typeName": "DB",
          "device": "PLC_1",
          "address": "DB3",
          "objectId": "Siemens identifier or null",
          "locations": [
            {
              "referenceType": "Uses",
              "access": "RW",
              "referenceLocation": "Network 1",
              "name": "native location name or null",
              "typeName": "native location type or null",
              "address": "native location address or null",
              "referencedAsName": "MotorDB",
              "referencedAsObjectId": "Siemens identifier or null"
            }
          ]
        }
      ]
    }
  ],
  "errors": []
}
```

`SourceObject` and `ReferenceObject` preserve native `Name`, `Path`, `TypeName`, `Device`, `Address` and `UnderlyingObject` information. A Siemens `objectId` is added only when Openness supplies an underlying `IEngineeringObject` that `ObjectIdentifierProvider` can identify. `Location` preserves the native `ReferenceType`, `Access`, `ReferenceLocation`, `Name`, `TypeName`, `Address`, `ReferencedAs` and `ReferencedAsName` information. Native enum names are returned without reducing them to a smaller MCP enum.

The tool does not build a PLC inventory merely to enrich a cross-reference result. Textual members remain textual and are not fabricated as engineering objects.

The tool does not compile, parse source or maintain a persisted call graph. It reports the cross-reference state returned by TIA Portal at call time.

## Shared inventory refresh behavior

The MCP does not maintain a hidden persistent object index. The caller retains object identifiers together with their process and primary-project context. Changing which connected process the agent targets does not disconnect any other process.

A `get_*` tool does not automatically rebuild inventory when an identifier cannot be resolved. It returns `objectNotFound`; the caller decides whether to invoke the relevant `list_*` tool again.

For individual tags and constants, refresh their containing table through `get_tag_table` to rediscover current entry identifiers; `list_tag_tables` remains table-level inventory only.

Inventory should be refreshed:

- After a project transition and explicit user reconnection, or when an agent first targets another project without an inventory for it.
- After `objectNotFound` when the caller expects the object still to exist.
- After writes, refresh the relevant inventory and detail readback through the read MCP tools.

## Writes: transient source staging

The implemented write tools and their contracts are defined in [write operations](write-operations.md). Writes follow native generation/import. A consumer updates a block or UDT by reading its source, editing the complete document and writing it back with the intended native name and scope. Separate create/update wrappers, update-only modes and member patches are not part of the product direction. This section records the shared staging boundary.

Project write operations require `processId` and use only a valid user-enabled connection under the same targeting and invalidation rules as project reads. They cannot establish or replace connections as part of a write.

### External-source writes

When a write uses an external source such as `.scl`, `.db` or `.udt`, the bridge's engineering implementation owns the transient lifecycle:

```text
Receive source content
    -> write a uniquely named temporary source file
    -> create a temporary PlcExternalSource in the target TIA scope
    -> generate the requested blocks or UDTs
    -> delete the PlcExternalSource from the TIA project
    -> delete the temporary disk files
```

The temporary `PlcExternalSource` is an implementation artifact, not a persistent project object created for the user. Cleanup is attempted after both successful and failed generation.

If cleanup fails, the operation reports `cleanupFailed` and must not claim complete success.

### SIMATIC SD writes

For SIMATIC SD, the engineering implementation stages the `.s7dcl` and applicable `.s7res` documents in a temporary directory and calls native document import directly on the target block or type composition.

SIMATIC SD import does not create a `PlcExternalSource`.

### SimaticML writes

For SimaticML, the engineering implementation stages a temporary XML file and calls native import directly on the target composition.

SimaticML import does not create a `PlcExternalSource`.

### Temporary-file ownership

All temporary files and directories are owned internally by the bridge. Cleanup is attempted after the operation and failures are reported. The client supplies document content through the MCP request and does not manage server-local file paths.

The active SCL writer uses the transient native external-source lifecycle. Retired V1 XML-patching behavior is not part of the active implementation.

## Contracts intentionally not yet locked

The following areas remain open and are not silently decided by this document:

- The final observed contents of the exploratory `typeSpecific` sections across supported Device, DeviceItem, block, UDT, tag-table and tag/constant entry variants.
- Native identifier support and readable field coverage for each tag/constant entry type; the typed JSON reader is settled, but full equivalence with SimaticML is not assumed.
- Broader native coverage of published writes beyond the user-reported successful writes and recorded integration checks.
- Pagination unless measured project size or performance makes it necessary.
- Derived device-category enums and filtering are not implemented or an agreed extension; retain native classification unless a separate contract is explicitly agreed.
- Broader live coverage for the native project guard. Same-path reopening was rejected in the user-executed V20 test on 2026-09-21. The guard behavior is implemented; the remaining live scenarios in the prototype checklist are not yet run. Headless startup is omitted, not pending.
