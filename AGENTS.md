# Repository instructions

## Build

- Build on Windows with the installed .NET 8 SDK: `dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release`.
- The application must remain a .NET Framework 4.8, x64 WinForms executable targeting the TIA Portal V20 Public API at `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20`.
- There is no test-framework project. The Siemens-free offline harness is a .NET 8 console program; run it with `dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release`, not `dotnet test`.
- Do not treat the mutating tasks in `.vscode/tasks.json` as tests.

## Architecture

- Run every TIA Openness call through `StaTaskScheduler`; never access Siemens engineering objects from a thread-pool thread.
- Version one is one user-started, long-running dashboard/MCP process. It serves the dashboard, the loopback Streamable HTTP `/mcp` endpoint, and the existing REST/debugging surface. Do not introduce another MCP transport, a client launcher, a background service, or process supervision.
- `tools/tia-mcp-server.ps1` is an explicit lifecycle helper for this checkout, not a supervisor. Use it only when lifecycle management is in scope; it must verify the exact executable path and use the token-protected graceful shutdown route rather than stopping processes by image name.
- Keep one set of MCP tool definitions, validation, and HTTP dispatch functions in `Program.cs`. Multiple compatible HTTP clients share the running process and its zero-or-one active TIA project; do not introduce per-client TIA attachments or project state.
- The V1 MCP surface always advertises and accepts exactly these eight tools: `connect_to_tia_portal`, `get_status`, `list_devices`, `list_plc_objects`, `find_plc_objects`, `read_plc_object`, `get_tag_table_entries`, and `get_cross_references`. This invariant also applies when `TIA_MCP_ACCESS=full`; do not register or dispatch compatibility, derived-analysis, option-package, project-signature, or project-changing operations as MCP tools. Reusable internal services may remain where a canonical tool depends on them.
- Keep authoritative exported source separate from derived parsing, explanations, and summaries.
- Keep documentation and MCP behavior client-neutral. Compatible clients, including the ChatGPT app, connect to the user-started loopback Streamable HTTP endpoint.
- Whenever a V1 response contains a non-null project identity, serialize `project.version` as `string | null`: preserve a nonblank native `Project.Version`, normalize blank values to JSON `null`, and never infer it. Keep nullable `tia.portalVersion` as a separate native installed-software fact.
- The V1 installed-product contract includes only native product name/version, a genuinely populated native product code, and nested options. It has no `update` field and must not infer update, patch, service-pack, or build values.
- MCP and the dashboard are read-only in every access profile. `TIA_MCP_ACCESS=full` may enable separately gated project-changing REST endpoints, but it never changes MCP discovery or dispatch and is not authorization to change a project. MCP `get_status.writeToolsAvailable` remains `false` in every profile.
- Keep the legacy project-clone REST route quarantined and unsupported for a project attached from the user's running TIA Portal instance; it is not an MCP tool.

## Connection prototype increment

- The user has authorized the first connection prototype from `docs/project-rehaul.md`. `TIA_MCP_CONNECTION_PROTOTYPE=1` (lifecycle helper `-ConnectionPrototype`) selects this experiment in the existing executable and HTTP listener. It uses the shared STA worker and server-wide multiple-process connection registry in `Prototype/`.
- Normal startup retains the V1 rules above. In prototype mode the existing eight MCP definitions remain discoverable, but tool execution returns an explicit prototype-mode error. Legacy REST engineering routes are unavailable; `/api/status` is passive prototype status. Only the prototype dashboard can discover, connect/disconnect existing UI processes and issue its minimal guarded read through `/api/prototype/*`. This is an intermediate debugging surface, not the rehaul MCP cutover or a second transport.
- The prototype never starts TIA, opens a project or attaches to headless instances. Lifecycle experiments involving project close/reopen require deliberate user actions on disposable test projects. Keep native equality behavior and detach behavior marked unverified until observed in live V20.
- Add Siemens-free connection guard tests to the existing offline harness. Offline simulations do not establish native lifecycle behavior.

## TIA test safety

- The live-TIA V1 MCP acceptance allowlist is exactly: `connect_to_tia_portal`, `get_status`, `list_devices`, `list_plc_objects`, `find_plc_objects`, `read_plc_object`, `get_tag_table_entries`, and `get_cross_references`. Do not use separately gated project-changing REST endpoints during V1 acceptance.
- Saving, writing, importing, creating, compiling, cloning, closing the user's project, and online operations are forbidden during read-only validation.
- If TIA Portal requests approval for external access, pause and ask the user to approve it.
- Do not stop dashboard processes by image name. Another checkout may be connected to the user's project.
- Before live testing, verify that disposal of an externally attached session only detaches the Openness client and does not save or close the user's project. If that ownership guard is absent or regresses, report live verification as blocked.
