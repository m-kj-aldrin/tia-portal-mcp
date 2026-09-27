# Handoff — 2026-09-27

A snapshot of where the work stands, for the next session. It assigns no new work. Agreed but unfinished work and the user's decisions about it are in [backlog.md](backlog.md); the contract documents stay authoritative until an item is implemented and changes them. When this handoff is no longer current, move it to `reference/history/` instead of updating it in place.

## State

HEAD is `b777e9d` ("Remove duplicated validation, path comparison and identifier lookups so shared helpers keep those checks consistent."). Everything below is uncommitted in the working tree. The user has not asked for a commit.

The last full check passed: Release build with 0 warnings and 0 errors, the offline harness 128 of 128, and `node --test "tests/*.test.cjs"` with 139 pass, 0 fail and 4 skipped (those need a live server).

## What changed since `b777e9d`

- **Version and status labels.** The MCP handshake now reports the build's informational version (for example `1.0.0+<commit>`) as `serverInfo.version`, from `McpBoundary.ServerVersion`. The hard-coded `implementationPhase` and `mcpPublication` labels are removed from `EngineeringService`, `DashboardService` and the status responses. `accessProfile` and `writeToolsAvailable` remain. Updated: `McpContractTests`, `ServiceIntegrationTests`, `http-smoke.test.cjs`, `docs/architecture.md`, `docs/compile-delete-export.md`, `docs/write-operations.md`.
- **Documentation cleanup.** Deleted the redirect stubs `docs/connection-prototype.md`, `docs/future-features.md`, `docs/lad-agent-architecture.md`, the three `docs/version-one-*.md` files and the root `TIA_PORTAL_MCP_GUIDE.md`. The records they pointed to under `reference/` are untouched. Corrected stale tool counts (the server publishes 32 tools: 15 reads and 17 writes) in `README.md`, `AGENTS.md`, `docs/README.md`, `docs/architecture.md`, `docs/native-acceptance.md`, `docs/native-import-matrix.md`, `docs/project-rehaul.md` and `docs/what-is-tia-openness.md`.
- **Tool-count check.** `tests/architecture.test.cjs` now fails if a current document states a read, write or total tool count that differs from the `McpBoundary` publication. Sentences about the historical nineteen-tool acceptance scenario are exempt.
- **Backlog.** New `docs/backlog.md`, linked from `docs/README.md` and `AGENTS.md`.
- **Datastar package.** `packages/Hypermedia.Datastar.0.1.0.nupkg` is present but not referenced by any project yet. Wiring it in is the first step of the dashboard rewrite; see the backlog.

## Running server

The managed server was started before `b777e9d` and is running an older build than the working tree. Reload it with `tools/tia-mcp-server.ps1` only when the user asks. A reload drops the bridge attachments, so the user must reconnect each TIA process in the dashboard afterwards. Never stop it by image name or by deleting its state file.

## User decisions already made

- The project is in a refinement stage, not a rehaul. Tool documentation stays; wording that describes a rehaul or an initial stage goes. `project-rehaul.md` becomes a current tool reference under a neutral name.
- Work proceeds in the backlog's order. The dashboard rewrite is the largest item and comes last, in five stages. Each stage gets a short design check with the user before it is built, and each leaves a working dashboard.
- The new dashboard uses Datastar. Tool runs go through a dashboard route that calls the same `McpBoundary` dispatch in-process. Run history lives in server memory only. The background monitor runs only while a dashboard event stream is open.
- The acceptance scripts get one shared client, with required `--process-id` and `--project-path` and no defaults.

## Verification commands

```powershell
dotnet build src/TiaOpennessMcpServer/TiaOpennessMcpServer.csproj --configuration Release
dotnet run --project tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj --configuration Release
node --test "tests/*.test.cjs"
```

The running server locks the normal Release output, so a build may need a separate output folder (`-o`) until the server is reloaded.
