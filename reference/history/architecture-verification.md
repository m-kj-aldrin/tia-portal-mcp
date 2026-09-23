# Architecture verification

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

## Cleanup checklist — 2026-09-22

- [x] Separate managed operations, native adapters, connection/execution services and diagnostics.
- [x] Keep MCP definitions and dispatch together in the MCP boundary.
- [x] Reduce `Program.cs` to startup and put composition and listener ownership in `Host/`.
- [x] Separate dashboard history/workflows, endpoint handling and HTML/CSS/JavaScript assets.
- [x] Replace active dashboard prototype route/header names and update current documentation.
- [x] Remove unused embedded-browser, dependency-injection and logging package dependencies.
- [x] Complete the Release build and Siemens-free .NET harness after integration.
- [x] Complete dashboard and architecture script checks after integration.
- [x] Load the checked Release executable through the managed lifecycle helper and verify the running build.
- [x] Verify passive dashboard/MCP HTTP behavior after restart and report attachment reconnection requirements.

## Verification — 2026-09-22

- Release build succeeded with no compiler errors. NuGet reported `NU1900` because its vulnerability advisory feed was unreachable; advisory lookup was not verified.
- The Siemens-free .NET 8 harness passed 97 groups, including production service/STA tests for queued attachment tickets, observer failure isolation, closed-tab opening, single journal entries and concurrent asynchronous call attribution.
- Dashboard JavaScript checks passed 26 tests; architecture checks passed 11 tests.
- The managed helper gracefully stopped the old process and started the Release executable as PID `68940`, full access, at `http://127.0.0.1:5000/`. The first sandboxed launch failed during HTTP listener initialization; the same launcher and build started successfully outside the sandbox.
- The running server's complete `tools/list` definitions matched the pre-reload server's nineteen definitions. The opt-in HTTP smoke check passed, including strict request and disconnected-admission paths. Write requests used an impossible process ID and reached no native writes.
- All three served assets matched the source files. Browser inspection showed the styled dashboard, nineteen tools, disconnected TIA workspaces and a successful passive `get_status` call. No browser warnings/errors were recorded.

The restart released bridge attachments; the user must reconnect them in the dashboard. This verification did not attach to, save, compile or modify a TIA project and does not establish additional native read/write behavior. Existing user-reported and agent-observed results retain their stated limits in the [read contracts](../../docs/project-rehaul.md), [write operations](../../docs/write-operations.md) and linked evidence pages. Previously passed native discovery checks need not be repeated without a regression concern.

### Localhost origin correction — 2026-09-22

The user subsequently reported HTTP 403 from `list_tia_processes` at `http://localhost:5000/`. Reproduction showed the numeric loopback origin succeeded while localhost was rejected by the old literal origin check. Both endpoints now use the shared explicit loopback origin policy described above.

The Release build, 101 offline groups, 37 dashboard/architecture checks and both live HTTP smoke checks passed. Foreign, wrong-port, opaque and malformed origins remained rejected. After the managed reload (PID `57308`, full access, `rehaul-mcp-writes`), the user's existing localhost Chrome tab completed `list_tia_processes` with HTTP 200, two processes and no errors. Both processes remained disconnected; no attachment or native project write was performed.
