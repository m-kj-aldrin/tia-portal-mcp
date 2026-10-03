# Repository instructions

## Build and native checks

- Target Windows, .NET Framework 4.8, x64 WinForms and the installed TIA Portal V20 Public API.
- Build with the .NET 8 SDK: `dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release`.
- Tests use the loaded managed server and only the fixed disposable `tia/Demo/Demo.ap20`, opened in visible TIA and connected by the user. It must start with zero devices. Run `node tests/mcp-live.cjs --process-id <PID>`.
- Keep one native suite covering every published MCP tool with meaningful write/readback expectations. Do not reintroduce simulated harnesses, tests of test infrastructure, source-text assertions or a second test server.
- Preserve the three-file suite: workflow, transport/report and fixture/assertion responsibilities. A pass requires all 34 tools and cleanup to zero devices; do not claim an unchanged modified flag. Never retry an uncertain write.

## Architecture and contracts

- Start with [README](README.md), [architecture/API/source ownership](docs/architecture.md) and [critical limitations](docs/limitations.md). Documentation describes current architecture, high-level API, limits and unfinished goals. Do not add handoffs, run logs, duplicate schemas or completed designs.
- MCP is primary. Dashboard connection/testing/inspection follows its contracts. Keep Operations, Openness, Services, Diagnostics, Mcp, Dashboard and Host boundaries separate; operations/services must not reference MCP/dashboard types.
- Retain one user-started executable, one HTTP listener, one shared STA and one server-wide connection registry. All Openness calls/native objects stay on the STA; only managed DTOs cross threads.
- Authoritative definitions/schemas/dispatch live in the MCP boundary; Program composes/starts the app. Dashboard tool actions invoke that same composed boundary in-process at `/api/dashboard/tools/run`; never add separate dispatch or localhost MCP calls.
- Dashboard routes use `/api/dashboard/*`, browser actions use `X-Tia-Dashboard: 1`, and assets live in `Dashboard/wwwroot/`. Datastar/SSE carries status, logs and history without browser polling.
- Full access publishes 15 reads and 19 modifying tools. Explicit read-only access publishes only reads and rejects writes/compilation in the service. Keep definitions, dispatch, managed/native adapters and the native suite aligned.
- Preserve existing tool contracts. Device creation uses root `Project.Devices.CreateWithItem(typeIdentifier, deviceItemName, deviceName)` once. Device deletion resolves only an exact native Device, retains identity, then calls Delete once.
- Saving and PLC upload/download are permanently outside MCP. Do not introduce online actions, implicit compilation, automatic reconnect/retry, patch/update modes for source documents or source-name substitution.
- Keep runtime technology-catalogue data and its provenance intact.

## Native behavior and connection safety

- The user explicitly connects/disconnects visible TIA processes through the dashboard. MCP cannot start TIA/open projects. Dashboard Open project may start one visible instance for a stored closed-project tab.
- Project calls require explicit processId and the retained user-enabled primary-project attachment. Capture its ticket before queueing; validate runtime identity, path and retained native Project before/after work, at collection boundaries and after failures.
- Context loss discards results and releases only that attachment; require explicit reconnect. Ordinary native object/type/permission failures retain a valid context.
- Detach only with retained `TiaPortal.Dispose()`. `TiaPortalProcess.Dispose()` closes TIA and is forbidden for detach. A failed Open project may close only its own incomplete instance.
- Use native opaque IDs directly. plcObjectId is the CPU DeviceItem owning PlcSoftware; paths are navigation aids. Never invent IDs, serialize proxies or infer native versions.
- Inventories use typed native compositions/scopes/order. Detail reads resolve one object directly; block/UDT/table metadata uses one bulk attribute read without duplicate typed-property fetching.
- Metadata-only reads do not export. Source export/staging uses owned temporary files, exact text/checksums and explicit native formats. Typed table reads remain separate from native XML export.
- Source writes accept document contents and native generation/import determines replacement/affected objects. Whole-object deletes retain identity and call native Delete once without reading deleted proxies.
- Compilation is explicit and returns current recursive diagnostics; complete retrieval does not imply compilation success. Native errors retain exact text/origin, partial effects and cleanup failures. Do not claim rollback.
- During read-only validation do not write/import/create/compile/save/clone/close projects or perform online actions. Pause for the user if TIA requests external-access approval.

## Managed lifecycle

- Read the [manage-tia-mcp-server skill](.agents/skills/manage-tia-mcp-server/SKILL.md) before checking/managing the server. Use only `tools/tia-mcp-server.ps1`, exact executable identity checks and token-protected graceful shutdown.
- Never kill by image name, force-stop TIA/the server or delete state to bypass ownership checks. Build successfully before loading; use a staging output if the running executable is locked, then load only the normal Release executable.
- Native checks run against the loaded single server after explicit dashboard attachment. Report whether the new executable is actually running; restarting releases attachments and the user must reconnect.
