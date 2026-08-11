# Repository instructions

## Build

- Build on Windows with the installed .NET 8 SDK: `dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release`.
- The application must remain a .NET Framework 4.8, x64 WinForms executable targeting the TIA Portal V20 Public API at `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20`.
- There is currently no automated test project. Do not treat the mutating tasks in `.vscode/tasks.json` as tests.

## Architecture

- Run every TIA Openness call through `StaTaskScheduler`; never access Siemens engineering objects from a thread-pool thread.
- Keep HTTP and stdio MCP behavior aligned by defining and dispatching tools through the shared functions in `Program.cs`.
- Preserve `read_block` as the existing SCL/raw-SimaticML reader. Read pure LAD through the separate, read-only `read_lad_source` SIMATIC SD export path.
- Keep authoritative exported source separate from derived parsing, explanations, and summaries.

## TIA test safety

- The live-TIA test allowlist is: `connect_to_tia_portal`, `get_status`, `list_devices`, `list_blocks`, `read_block`, and `read_lad_source`. Do not call any other TIA-facing tool unless the user explicitly authorizes that project-changing operation.
- Saving, writing, importing, creating, compiling, cloning, closing the user's project, and online operations are forbidden during read-only validation.
- If TIA Portal requests approval for external access, pause and ask the user to approve it.
- Do not stop dashboard processes by image name. Another checkout may be connected to the user's project.
- Before live testing, verify that disposal of an externally attached session only detaches the Openness client and does not save or close the user's project. If that ownership guard is absent or regresses, report live verification as blocked.
