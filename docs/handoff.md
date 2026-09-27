# Handoff — 2026-09-28, refinement and dashboard stage 1

This is the current working-tree snapshot. It assigns no new work. Agreed but unfinished work is in [backlog.md](backlog.md); tool contracts remain authoritative. Move this snapshot to `reference/history/` when replacing it in a later session.

## State

The refinement started from `6e616c5a`; the user requested a commit on 2026-09-28. The [previous handoff](../reference/history/handoff-2026-09-27-before-refinement.md) was archived unchanged. Its old HEAD and running-server observations describe its original checkpoint.

Documentation, acceptance-runner and parser work, and the approved [dashboard stage 1](dashboard-stage-1-design.md), are implemented and locally checked in this working tree. Remaining dashboard stages 2–5 still need their design checks before implementation.

## Changes

- Removed the unreachable failed-Open-project cleanup branch. Detach still calls the retained portal's `Dispose`; only an incomplete instance started by Open project may be closed on failure.
- Native acceptance scripts use the shared guarded client. An explicit process ID and absolute project path are required; publication checks cover all 32 tools. Transport uncertainty and lost context prevent later writes and cleanup, while partial effects and ownership remain recorded. No native acceptance run was executed here.
- Current read-tool and dashboard references are [read-tools.md](read-tools.md) and [dashboard.md](dashboard.md). Active links, technology-object contracts and source-format explanations were aligned with production code.
- `systemLibVersion` help documents the two-, three- and four-component versions accepted by the existing parser. The parser's acceptance and existing error message remain unchanged.
- Eight request parsers share `Operations/RequestValidation.cs`. Compatibility tests preserve exact error text, codes, process attribution, validation order, Boolean defaults and opaque selectors.
- Dashboard stage 1 adds the local Datastar SDK, one SSE route and subscriber-controlled background monitoring. The old page still polls, uses `/mcp` for tools and stores operation captures in the browser. Later stages remain in the backlog.

## User decisions

- Keep the version parser and document its accepted formats.
- Share argument validation while preserving existing client-visible errors.
- Build dashboard stage 1 according to its approved design. Later dashboard stages each need their own design check.
- Reload the managed server only when the user asks.

## Runtime and evidence

The user reports starting MCP from the new build and seeing `/api/dashboard/events` in the browser Network tab. This confirms the user's observation of a request, not the stream's content, complete monitoring behavior or the exact managed-server identity. The agent has not inspected, started, stopped or reloaded the server or made native TIA calls. Any later agent-managed reload uses the lifecycle skill and `tools/tia-mcp-server.ps1`; attachments must be reconnected afterwards.

Offline checks and browser mocks do not establish native lifecycle behavior. Existing native evidence in [evidence.md](evidence.md) remains unchanged. The extra bulk reads and native traversal cleanup still need scoped live evidence.

## Verification

- Release build: **0 warnings, 0 errors**, both in `test-results/refinement-build` and the normal `src/TiaOpennessMcpServer/bin/Release/net48` output.
- Siemens-free offline harness: **146/146 groups**, including four parser-compatibility and fourteen stream/monitoring groups added here.
- Node suite: **183 passed, 0 failed, 4 skipped** (the skipped checks require a live server).
- .NET Framework SDK smoke: the output DLL loads and writes exact expected signal bytes under Windows PowerShell 5.1 / CLR 4.0, with the existing Siemens assembly resolver registered. It loads the executable assembly without starting the application and makes no TIA calls. The local smoke script, `test-results/refinement-sdk-smoke.ps1`, is ignored and is not part of this commit.
- Independent stream review and whitespace/link checks passed. Idle HTTP disconnects are detected by the next send or heartbeat; subscription release follows that detection.

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release -o test-results/refinement-build
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test "tests/*.test.cjs"
```

The separate Release output avoids replacing executable files that may be in use by the managed server.
