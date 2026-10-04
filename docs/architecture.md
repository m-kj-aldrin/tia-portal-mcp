# Architecture and API

The bridge exposes native TIA Portal V20 engineering operations through MCP. The dashboard extends the same interface with connection management and inspection. Engineering capabilities follow Openness; dashboard controls consume their contracts.

## Architecture and ownership

One user-started .NET Framework 4.8 x64 WinForms executable, `TiaPortalDashboard.exe`, owns one loopback HTTP listener, one connection registry and one engineering STA worker. A separate UI thread runs a tray-only `ApplicationContext`; it creates no visible form or taskbar entry. The tray opens the browser only when requested. All Openness calls and native objects stay on the engineering worker; only managed DTOs cross threads.

Paths in this table are relative to `src/TiaOpennessMcpServer/`.

| Location | Responsibility |
|---|---|
| `Program.cs`, `Host/ServerApplication.cs` | Assembly setup and composition of the single host, worker, service and endpoints. |
| `Host/` | HTTP listener, tray message loop, response handling, browser-origin policy and authenticated graceful lifecycle. |
| `Mcp/McpBoundary.cs` | Authoritative tool definitions, input schemas, dispatch and MCP result/error mapping. |
| `Mcp/McpRpcProcessor.cs`, `Mcp/McpEndpoint.cs` | JSON-RPC validation and the external `/mcp` endpoint. |
| `Operations/` | Managed requests/results, argument validation, inventory/read algorithms, metadata conversion and owned source-document staging. No Siemens dependency. |
| `Services/EngineeringService.cs` | Access enforcement, bounded scheduling, admission tickets, monitoring and managed observations. |
| `Services/ConnectionRegistry.cs`, `ConnectionContracts.cs`, `ConnectionSnapshot.cs` | Retained attachments, native-context validation and connection lifecycle contracts. |
| `Openness/OpennessConnectionBackend.cs` | Native UI-process discovery, attachment and retained project ownership. |
| `Openness/` reader/exporter adapters | Typed native inventories, direct object reads, source export and cross-references. |
| `Openness/OpennessWrites.cs`, `OpennessCompiler.cs` | Native modifications and explicit compilation. |
| `Diagnostics/` | Neutral request attribution and operation notes shared across boundaries. |
| `Dashboard/` | Connection actions, tool forms/selectors, readback workflows, bounded memory-only captures, managed state, HTML fragments and SSE. |
| `Dashboard/wwwroot/` | Declarative HTML, CSS with shared design properties and the pinned local Datastar client. The served page, stylesheet and client are embedded in the executable so they match its fragment renderers; the Datastar license is distributed beside it. |
| `Utilities/StaTaskScheduler.cs` | Serialized engineering STA execution. |

Outside the application, `tools/tia-mcp-server.ps1` owns managed lifecycle commands; `data/technology-object-catalogue.json` and its maintenance tool own catalogue data/provenance. Native checks live in `tests/mcp-live.cjs` (workflow), `mcp-client.cjs` (transport/report) and `mcp-fixture.cjs` (fixtures/assertions).

Operations and services do not depend on MCP or dashboard types. Openness implements the backend contracts; the host supplies those implementations. MCP depends on the managed engineering interface. Dashboard forms consume the MCP definitions and tool actions call the same composed `McpBoundary` in-process.

An external tool call goes through `McpEndpoint → McpRpcProcessor → McpBoundary → EngineeringService → ConnectionRegistry → Openness`. A dashboard tool action enters at the same boundary after checking its displayed context. The service captures the attachment ticket before queueing; the registry validates the retained process/project before and after work, at native collection boundaries and after failures.

## Public interfaces

- `/mcp`: external MCP over one JSON-RPC message per HTTP request. The production schemas describe exact arguments; operation DTOs describe returned fields. Documentation does not maintain a second schema.
- `/api/dashboard/*`: connection actions, tool execution, run inspection and events. Browser actions use `X-Tia-Dashboard: 1`. `POST /api/dashboard/tools/run` converts bound JSON field values using the published MCP schema, invokes the composed boundary and streams SSE updates.
- `/` and `/dashboard/*`: dashboard page/assets. Datastar signals hold local choices and input; the server owns connection state, forms and captures. Workspace/context views, forms, logs, history and run IDs/revisions reach the browser over SSE without polling.
- `/api/lifecycle/health` and `/api/lifecycle/stop`: token-protected host identity and graceful shutdown. Readiness requires the listener and initialized tray message loop. Tray Exit, authenticated stop and cancellation enter the same idempotent shutdown path. Passive `/api/status` is a compatibility status view, not identity proof.

The shared origin policy accepts the configured loopback addresses and port. JSON-RPC batches are unsupported. Notifications never dispatch engineering operations. Explicit read-only access restricts both publication and service execution; full access is the default.

### Dashboard state and rendering

Each workspace has one operation picker, grouped into Read and Modify from published MCP definitions, alongside one active inspector. Private browser state holds the workspace's selected operation, follow/pin choice and context-bound input drafts. Server metadata supplies the current workspaces and capture identities/revisions. Context changes replace the forms and discard obsolete draft branches; available selectors remain editable native IDs.

A complete hardware-catalogue search replaces the workspace's Create device type-identifier suggestions with its returned page. Labels show model, article number and version; values preserve exact native identifiers. Suggestions belong to the current attachment/project context and clear when it changes. Catalogue selection does not submit creation. Integer inputs use the published schema bounds, including zero-based catalogue offsets.

On wide screens, Operation and bounded Run history share the left column while the inspector occupies the right. Narrow screens show Operation, Inspector and Run history in that order. History scrolls within its own region. Empty, running, completed, failed and unavailable captures use the same inspector shell: its header, summary and Result/Request/Response controls retain their positions, while payloads, errors and notices scroll inside the output region. Selecting another capture does not resize the workspace.

One long-lived event stream per visible dashboard page morphs stable HTML regions. Form markup is cached by context, access profile and selector contents, so activity updates do not rebuild every form. The browser's declarative inspector effect fetches the selected capture from `GET /api/dashboard/runs/view` when its identity or revision changes. That route accepts a Datastar GET payload or the existing query parameters. Superseded reads are cancelled and responses are guarded against a newer selection. Reconnection reconciles the current selection without replaying an operation.

Latest follows the newest explicit operation in the selected workspace. Selecting history pins that run; Latest or another submission resumes following. Status, Result, Request and Response are rendered from the same capture. Automatic follow-up reads carry dashboard-only `ParentRunId` metadata, remain inspectable in history and do not displace the explicit operation in Latest. An evicted pinned run shows an unavailable notice until the user changes selection.

`DashboardToolWorkflow` owns follow-up read orchestration and selector observation; `DashboardEndpoints` owns HTTP admission and responses. Every tool invocation still uses `DashboardToolRunner` and the composed MCP boundary in-process. Operation and connection POSTs never retry or cancel admitted native work. Source loading requires the draft's selected explicit format and locks its matching draft until the response is consumed, then patches exact native document names, contents and formats only while the original context remains valid. Writing those documents remains a separate submission. Block/UDT write readback requests the write's explicit format; a native rejection remains visible without switching formats. Metadata-only inspection omits the format and requests `includeSource:false`.

Captures remain in server memory and clear on restart. The store retains at most 40 completed captures and 64 MiB of request/response payloads, evicting oldest completed runs whole. Oversized captures retain metadata only; their initiating browser may display the payload once, and a concurrent metadata read cannot overwrite that display. The browser keeps selection metadata and drafts rather than a second capture store.

### Read tools: 16

| Capability | Tools | Main implementation |
|---|---|---|
| Process discovery and status | `list_tia_processes`, `get_status` | Connection backend and engineering service. |
| Devices/CPUs | `list_devices`, `get_device` | Native discovery reader. |
| Installed hardware catalogue | `search_hardware_catalog` | Retained TiaPortal catalogue adapter; managed field filtering and pagination. |
| Blocks and source | `list_blocks`, `get_block` | Block inventory/detail readers and source exporter. |
| UDTs and source | `list_udts`, `get_udt` | UDT inventory/detail readers and source exporter. |
| Tag tables and entries | `list_tag_tables`, `get_tag_table`, `export_tag_table` | Tag-table readers and separate native XML exporter. |
| Cross-references | `get_cross_references` | Native cross-reference service adapter. |
| Technology objects | `list_technology_objects`, `list_available_technology_objects`, `get_technology_object` | Technology-object readers and catalogue adapter. |

### Modifying tools: 19

| Capability | Tools | Native behavior |
|---|---|---|
| Devices | `create_device`, `delete_device` | Root `Project.Devices.CreateWithItem(typeIdentifier, deviceItemName, deviceName)`; resolve an exact native Device by ID and call Delete once. |
| Block/UDT documents | `write_blocks`, `write_udts` | Complete supplied documents through native external-source generation or explicit SD/SimaticML import. |
| Tables and entries | `create_tag_table`, `create_tag`, `create_user_constant`, `set_tag_entry_attribute`, `delete_tag_entry`, `import_tag_tables` | Native typed compositions, attributes and import operations. System constants remain read-only. |
| Whole engineering objects | `delete_block`, `delete_udt`, `delete_tag_table` | Resolve the matching native type by ID, retain identity, then Delete once. |
| Technology objects | `create_technology_object`, `set_technology_object_parameters` | Native technology-object creation and parameter assignment. |
| Organization | `create_group`, `delete_group`, `rename` | Native group composition and supported object naming. |
| Compilation | `compile_plc` | Selected CPU's `PlcSoftware.ICompilable.Compile()`, with diagnostics from that invocation. |

### Shared conventions

Project operations require a positive `processId`, a user-enabled attachment and its retained primary Project. Discovery/status have their schema-defined bridge-only modes. `plcObjectId` identifies the CPU DeviceItem whose SoftwareContainer owns PlcSoftware. Other IDs identify their own native object; IDs are opaque and paths are navigation aids.

Inventories traverse native typed compositions/scopes in native order and return lightweight identities. Detail reads resolve one object directly. Block/UDT metadata uses one native bulk attribute read. Optional path/source/entry flags skip the corresponding work; unavailable values remain null. Partial reads retain readable branches with `complete:false` and explicit errors.

Hardware-catalogue searches call the retained attachment's `TiaPortal.HardwareCatalog.Find(string.Empty)` and filter its entries using explicit managed matching rules. Supplied filters combine with AND. Text fields use ordinal case-insensitive contains; version uses ordinal case-insensitive equality; identifiers use exact ordinal equality. Paging preserves native order and reads remaining metadata only for returned rows. A fresh call re-reads the catalogue; paging limits response size rather than native query cost, and does not retain a snapshot or native proxy cache. Partial retrieval retains readable rows, errors and null totals. Catalogue entries expose native type descriptions and paths, not project object identities or proof of standalone creation support.

Block and UDT source reads require an explicit `sourceFormat` whenever `includeSource` is true (including its default). The three equally exposed formats are `simatic-ml`, `simatic-sd` and `external-source`; there is no format default, `best` value or automatic fallback. A metadata-only read uses `includeSource:false` and needs no format. `includeDependencies:true` remains specific to an explicit `external-source` read with source enabled.

Source export makes one attempt through the selected native API: SimaticML `Export`, SIMATIC SD `ExportAsDocuments`, or external-source `GenerateSource`. It uses owned temporary files and returns exact document text/checksums. SimaticML XML is returned unchanged: the bridge does not extract embedded SCL, convert it or replace the document with a simplified representation. Source failures retain available metadata and exact native errors. `get_tag_table` reads typed entries; `export_tag_table` separately returns native SimaticML.

Updates to blocks/UDTs mean read source in an explicit format, edit the complete document and write it back with that format and the intended native name/scope. Writes require an explicit format and accept document contents, never client-controlled server paths. Complete SimaticML XML, including SCL embedded as text, passes unchanged to native `Import`; SIMATIC SD uses `ImportFromDocuments`, and external source uses native generation. Native generation/import determines replacement and affected objects; the bridge does not invent patch/update modes or parse, convert or validate embedded SCL. Temporary files/sources are owned and cleaned up, with incomplete cleanup reported.

Native errors retain their text and `tia-openness` origin; bridge errors remain distinct. `complete` describes result retrieval, not PLC runtime correctness. Compilation can have `complete:true` and `compilationSucceeded:false`. Native writes can have partial effects on failure; no rollback or automatic retry is promised.

## Limits and unfinished goals

Connections are explicit dashboard actions on visible processes. Open project starts one visible TIA window for a stored closed-project tab; MCP cannot start TIA, open projects or attach headless instances. Context loss discards the result and detaches only that attachment; the user must reconnect. No replacement project is silently adopted.

Saving and PLC upload/download are permanently outside MCP. No other online operation, implicit compilation, force rebuild, compiler-history query or source-name substitution is exposed. Detach uses the retained `TiaPortal.Dispose()`; disposing a `TiaPortalProcess` would close TIA and is forbidden.

Native restrictions remain authoritative for source representations, object creation/deletion, writable attributes and technology-object versions. See [critical investigation findings and unverified cases](limitations.md).

Unfinished goals:
- Measure native-read costs and confirm value normalization on representative V20 objects before further read optimizations.
- Consolidate duplicated unit/group traversal, ancestor-path reconstruction and write-destination traversal after native validation.

A Release build checks installed-API compatibility. The single native MCP suite checks all 35 tools through real reads/writes/readbacks on the fixed empty Demo project. It uses the existing loaded server, verifies catalogue filters/paging and creates its fixed S7-1500 CPU from the returned exact identifier, plus controlled fixtures including PID_Compact V2.3. The suite explicitly calls `compile_plc` before native XML/SD exports and after imports to establish fixture consistency; source tools never compile implicitly. Success verifies those workflows and cleanup to zero devices; it does not establish runtime PLC execution or all native object variants.
