---
name: manage-tia-mcp-server
description: Safely manage the repository-local TIA Portal dashboard and MCP server lifecycle. Use when Codex needs to check, start, stop, or restart this checkout's server, or reload a successful Release build without affecting another checkout or the user's TIA Portal project.
---

# Manage the TIA MCP server

Run commands from the repository root with `tools/tia-mcp-server.ps1`. Treat start, stop, and restart as external state changes; use them only when the user requested lifecycle management or the current implementation task explicitly requires loading a newly built executable.

## Check state

Run status before changing the process:

```powershell
./tools/tia-mcp-server.ps1 status -Json
```

Interpret exit code `0` as healthy, `3` as stopped, and `1` as unhealthy, unmanaged, or failed. If status reports `unmanaged`, ask the user to exit that dashboard through its tray icon. Never force-stop it.

## Manage the process

Use one of:

```powershell
./tools/tia-mcp-server.ps1 start
./tools/tia-mcp-server.ps1 stop
./tools/tia-mcp-server.ps1 restart
```

Use `-Port <number>` only when a non-default loopback port is required. Start in the default `read-only` profile. Use `-AccessProfile full` only when the user has explicitly authorized the separately gated REST profile; it never authorizes a TIA project change.

## Reload a build

Build separately; the lifecycle tool never compiles:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
./tools/tia-mcp-server.ps1 restart
```

Restart only after the build succeeds. Then report the MCP endpoint and remind the user that MCP clients may need to reconnect and TIA Portal may request external-access approval again.

## Preserve safety

- Never stop `TiaPortalDashboard.exe` by image name.
- Never replace graceful shutdown with `Stop-Process`, `taskkill`, or another forced termination.
- Never delete the runtime-state file merely to bypass an unmanaged or mismatched process.
- Never save, compile, close, or otherwise modify the user's TIA Portal project as part of lifecycle management.
- Stop if the helper reports a path mismatch, invalid state, rejected shutdown, or timeout.
