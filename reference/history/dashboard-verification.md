# Dashboard verification

These dated snapshots preserve the original observations and limitations. Old tool counts, routes, process IDs and test instructions are historical, not current runtime state or new work. See the [evidence index](../../docs/evidence.md).

## Historical workbench validation — 2026-09-22, before MCP writes

- Dashboard and architecture scripts: 31/31 checks. Added coverage exercises navigation without dispatch, explicit IDs before discovery, exact request/envelope inspection, opening earlier results without replay, refresh restoration without live-selector restoration, connection invalidation, precise arming, focused probe inputs, independent table destinations, and bounded history with storage failure.
- Siemens-free .NET harness: 89/89 groups. Release build succeeded with zero errors; NuGet reported `NU1900` because its vulnerability-data endpoint was unreachable from the restricted environment.
- The managed helper loaded the Release executable outside the sandbox as PID **44724**, port **5000**, read-only. The unsuccessful sandbox launch was closed by the user before this start. No staging executable was started.
- Live browser inspection confirmed the new interface, passive Server `get_status` through `/mcp`, exact request and full response views, and retained results after refresh. The response still reports `rehaul-mcp-read-only` and `eleven-read-only-tools`.
- Disconnected project and probe states show explicit connection guidance. Browser console had no warnings or errors. At 390 px the page width was 375 px (scrollbar excluded), and at 1440 px the page width was 1425 px with adjacent form/inspector panels; no horizontal page overflow. Temporary viewport overrides were reset.
- This UI check did not reconnect TIA, execute native inventories, arm probes or write to a project. Connected discovery/probe interactions above are simulated evidence. The restart released attachments; users must reconnect explicitly.

## Historical local checks — dashboard increments

- Staged Release build, while the previous executable was locked: 0 warnings, 0 errors. That staging executable was not started.
- Siemens-free harness: 80/80 groups, including projectless timeline retention, A-to-B archive without connecting B, exact-path reappearance without reconnection, two same-path processes, PID reuse, late call attribution, failed and partial outcomes, log and historical-tab bounds, dismiss, and tool forms generated from the published schemas.
- Later harness run: 83/83 groups. The added cases cover a project closed inside a running process, that project opening again on the same tab, a different project staying separate, a second live copy of the same path staying separate, the temporary process tab disappearing when that process exits, and a process that never had a project remaining after exit.
- Dashboard script and architecture checks: 22/22. They cover MCP `tools/call` submission, selector clearing, late results, busy Connect/Disconnect, hidden polling, copy, a second process's connect target, historical dismiss, and the unchanged detach boundary.

Simulated transitions are not native TIA lifecycle evidence. Earlier reader evidence in [block reads](../../docs/get-block.md), [UDTs](../../docs/udt-discovery-read.md), [tag tables](../../docs/tag-table-discovery-read.md), [cross-references](../../docs/cross-references.md) and [MCP cutover](rehaul-mcp-cutover.md) is unchanged. This dashboard does not add native coverage for the eleven tools.

## Historical loaded-server snapshots — 2026-09-21 and 2026-09-22

On 2026-09-21 the lifecycle helper stopped the previous managed process 34156 gracefully, then started the normal Release build. At that checkpoint the server was PID **33068**, port **5000**, `implementationPhase: rehaul-mcp-read-only`, `mcpPublication: eleven-read-only-tools`, `writeToolsAvailable: false`. The staging executable was not started. That restart released bridge attachments. This is not the current runtime state or a new reconnection instruction.

The opt-in HTTP smoke passed 1/1 against that process. It exercised dashboard, log and tool-form reads, rejected dismissal of the Server tab, and dispatched the eleven tools against a nonexistent process. Those failures are recorded on the Server tab. It did not attach to TIA. Attachment count was unchanged.

In the browser, the Server tab and three discovered, disconnected project tabs were visible. No Connect action was used. Bridge Get status returned read-only facts with no project. Get status on the disconnected Prototype-A-1 tab (process 34636) returned `state: disconnected`, `project: null` and `complete: true`; the tab stayed disconnected. Connect was disabled while that call was running and enabled again afterward. The Server tab kept its own result and log when selected again. Project tools stayed unavailable on the disconnected tab. At a 390px width the tabs wrapped and the page did not scroll sideways. The automation browser could not focus the page, so Copy result reported that it could not copy; the script test covers a successful clipboard write.

This does not add native inventory, source, tag-entry or cross-reference coverage. Detach remains the retained portal disposal only; no attachment was created, so detach was not exercised live.

On 2026-09-22 the lifecycle helper stopped PID 33068 gracefully and started the tab-history correction. That server was PID 75772, port 5000, with the same read-only phase and publication. Restart cleared dashboard history and released attachments. Immediately after start, FillTank and Prototype-A-1 were discovered running and disconnected, with no leftover closed-process tab. The close-project and process-exit cases above remain offline evidence; those TIA actions were not repeated.

The same helper later stopped PID 75772 and started the Open project action as PID **16788**, port **5000**, still read-only. Attachments were released again. The user then tried Open project in TIA and reported that it works.
