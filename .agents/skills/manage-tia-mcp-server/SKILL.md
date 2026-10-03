---
name: manage-tia-mcp-server
description: Safely manage the repository-local TIA Portal dashboard and MCP server lifecycle. Use when Codex needs to check, start, stop, or restart this checkout's server, or reload a successful Release build without affecting another checkout or the user's TIA Portal project.
---

# Manage the TIA MCP server

Run from the repository root with `tools/tia-mcp-server.ps1`. Start/stop/restart are external state changes; use them when the user requested lifecycle management or the implementation task explicitly requires loading a new build.

## Check state

```powershell
./tools/tia-mcp-server.ps1 status -Json
```

Read the structured status. Readiness requires token-protected `/api/lifecycle/health` to match the tracked PID and exact executable path. Passive `/api/status` is not identity proof. A stopped status is not a running build. If the helper reports unmanaged/path mismatch/invalid state, preserve its ownership checks; an unmanaged server must be exited by the user through its tray.

## Manage the process

```powershell
./tools/tia-mcp-server.ps1 start -Json
./tools/tia-mcp-server.ps1 stop -Json
./tools/tia-mcp-server.ps1 restart -Json
```

Start defaults to full access: 15 reads and 19 modifying tools. Explicit `-AccessProfile read-only` restricts publication and execution. Restart preserves the stored profile unless overridden. `-Port <number>` selects a non-default loopback port.

The compatibility `-ConnectionPrototype` spelling remains accepted; false is rejected and cannot select a retired runtime. See [architecture/API](../../../docs/architecture.md) for current behavior.

## Load a build

The helper never compiles. Build before changing the server:

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
./tools/tia-mcp-server.ps1 restart -Json
```

If the running executable is locked, first verify compilation using a separate staging output directory. After that succeeds, stop gracefully, build the normal Release output and start the one managed server. Never run a staging executable or create another host. Do not load a failed build.

After loading, verify authenticated identity and report the running PID, endpoint and access profile. A staged build alone does not update the server. Restart releases attachments; the user must reconnect each intended process and approve access in TIA if prompted.

The native suite uses the loaded server and a user-connected, initially empty `tia/Demo/Demo.ap20`:

```powershell
node tests/mcp-live.cjs --process-id <PID>
```

That suite's project modifications require its authorized disposable target; lifecycle management itself does not modify TIA projects. See [README](../../../README.md) for setup.

## Preserve safety

- Never stop by image name or use Stop-Process/taskkill/forced termination.
- Never delete runtime state to bypass unmanaged or mismatched ownership.
- Never save, compile, close or otherwise modify a TIA project as part of lifecycle management.
- Stop if graceful shutdown is rejected or times out; do not replace it with a forced stop.
