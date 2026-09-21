# Repository instructions

## Build and checks

- Build on Windows with the installed .NET 8 SDK: `dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release`.
- Keep the application a .NET Framework 4.8, x64 WinForms executable targeting the installed TIA Portal V20 Public API.
- Run the Siemens-free .NET 8 harness with `dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release`, not `dotnet test`.
- Dashboard script checks: `node --test tests/connection-prototype-dashboard.test.cjs`. Architecture/migration checks: `node --test tests/rehaul-boundary.test.cjs`.
- No active build, test or runtime dependency may point into `reference/legacy-v1/`. This is an inert snapshot of commit `8ebc151`, including coupled tests and former instructions. Do not run its project or lifecycle helper as a fallback.

## Active architecture and publication boundary

- There is one user-started, long-running executable, one HTTP listener, one shared STA scheduler and a server-wide connection registry. No second server, transport, launcher, service, supervisor or per-client attachments.
- Run every TIA Openness call through `StaTaskScheduler`. Native objects remain on that worker; only managed DTOs cross threads.
- The authorized source transition is implemented. `Prototype/` is the active guarded connection/discovery foundation; its name and `/api/prototype/*` routes remain to avoid a gratuitous rename during incremental work.
- Every startup is read-only rehaul transition mode. The old prototype switch is accepted by the helper as a legacy spelling; it cannot restore V1. `TIA_MCP_ACCESS=full` never enables writes; the helper rejects full on start/restart.
- MCP publication is explicitly held: `/mcp` retains exactly the eight disabled V1 descriptors (connect_to_tia_portal, get_status, list_devices, list_plc_objects, find_plc_objects, read_plc_object, get_tag_table_entries, get_cross_references). Descriptions state they are disabled; calls return `prototype-mode`. Unknown/unpublished names are rejected. There is no engineering MCP dispatch or hidden legacy REST dispatch.
- The final rehaul cutover must reconcile definitions, dispatch, tests, instructions and documentation together and publish exactly the eleven tools in `docs/project-rehaul.md`. Do not silently publish a smaller surface or claim unimplemented tools work.
- Keep the sole MCP definitions, validation and HTTP dispatch in `Program.cs`. Preserve client-neutral documentation and keep authoritative source separate from derived content.

## Connection and discovery rules

- The user connects/disconnects existing UI processes through the dashboard. Never start TIA, open projects, attach headless instances, reconnect automatically or introduce an implicit active process.
- Project reads require `processId`, an existing user-enabled attachment and the retained native Project. Capture its internal ticket before queueing; validate runtime identity, path and retained native context before/after reads, at collection boundaries and after failures.
- Context loss discards the result, releases only that attachment and requires explicit reconnection. Never adopt a replacement project. Ordinary object/type/permission failures must not invalidate a still-valid context.
- Detach only with the retained `TiaPortal.Dispose()`; `TiaPortalProcess.Dispose()` closes TIA and is forbidden for detach.
- Device lookups use the retained project's ObjectIdentifierProvider directly. `plcObjectId` identifies the CPU DeviceItem whose SoftwareContainer owns PlcSoftware, never the rack, Device or software object.
- Block, UDT and tag-table discovery traverse only their native typed compositions and unit scopes, preserve names/order/identity and readable branches, and return no source or detailed metadata. See `docs/rehaul-transition.md` for classification and unnamed unit-container handling.
- Individual block and UDT reads resolve one object directly and read metadata using one native GetAttributes(ReadOnly | ReadWrite) call. Do not re-fetch returned attributes through typed properties. Metadata-only reads must not export. Source export is native, read-only and uses per-request temporary files, exact-content checksums, strict explicit formats and guarded best fallback. See docs/get-block.md and docs/udt-discovery-read.md.
- Tag-table detail reads use one bulk table attribute call and the native Tags/UserConstants/SystemConstants compositions. Entries have their own native identifiers or null; never invent IDs. includeEntries:false skips entry access and returns entries:null. No tag-table export/XML/source/checksum path is supported. See docs/tag-table-discovery-read.md.
- Cross-references resolve one object directly and query its native CrossReferenceService with AllObjects. Preserve Sources/Children/References/Locations and native enum/path fields; add IDs only for underlying engineering objects. No extra type allowlist, inventory rebuild, parsing, compilation or derived graph. See docs/cross-references.md.
- Native identifiers are opaque; paths are navigation aids. Device `includePath:false` skips reconstruction and returns null paths.
- Native nonblank project versions are preserved; blank becomes null. Installed portal version is separate. Do not infer update/patch/build/product codes.
- Native error messages retain their text and tia-openness origin; bridge errors remain distinguishable. Never serialize native proxies.
- Add Siemens-free checks to the existing harness. Simulated tests do not establish native lifecycle or inventory behavior.

## Lifecycle and live TIA safety

- Use the `manage-tia-mcp-server` skill before managing this checkout's server. Use only `tools/tia-mcp-server.ps1`, its exact executable checks and token-protected graceful shutdown. Never kill by image name or delete state to bypass ownership checks.
- Build/test first, then load that successful Release build into the single managed server. Report whether the new executable is actually running and that the user must reconnect attachments.
- Saving, writing, importing, creating, compiling, cloning, closing the user's project and online operations are forbidden during read-only validation.
- If TIA requests external-access approval, pause for the user to approve it.
- Before live validation, confirm attachment disposal only detaches and cannot save/close the user's project. If this guard regresses, live verification is blocked.
- Native block-list comparison passed for both disposable PLC projects as user-reported evidence. The user confirmed best-format routing for SCL/LAD/DB. The user reported that the requested UDT discovery/detail workflow works; see docs/udt-discovery-read.md for evidence limits. Tag-table inventory and typed entry reads are implemented. User-supplied FIO results cover 15 tags with distinct native IDs and metadata-only mode; populated constant reads remain unverified (docs/tag-table-discovery-read.md). The user supplied a successful Level meter → Main NW1 UsedBy/Read cross-reference result with matching source/reference IDs; independent TIA-view comparison remains unconfirmed (docs/cross-references.md); the eleven-tool MCP cutover is the next publication phase. Do not rerun already-passed basic discovery checks without a regression concern.
- Earlier same-path reopen, detach and device-discovery evidence in `docs/connection-prototype.md` is user-reported and scenario-specific. Do not generalize it or relabel it as agent-replayed verification.
