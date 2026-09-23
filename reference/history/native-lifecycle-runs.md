# Native lifecycle acceptance runs

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

### Native evidence — 2026-09-23

The target was the user-connected `Prototype-A-1` project in TIA process **28572**, through managed MCP process **17872**. These process IDs identify the observed run only. The run used fixture prefix `McpLC_6c17d83c5803` and retained the exact requests, responses, source/checksums and compiler diagnostic trees in two ignored local reports:

- Initial report: `test-results/native-lifecycle-acceptance/2026-09-23T07-46-23-784Z_McpLC_6c17d83c5803/report.json`. Eleven scenarios passed. Its status remains `failed` because a Windows `EPERM` error prevented replacement of the local report file before the `compile.repaired` request was transmitted.
- Reviewed completion: `test-results/native-lifecycle-acceptance/2026-09-23T07-52-49-432Z_McpLC_6c17d83c5803_reviewed-completion/report.json`. All **8/8** steps passed, including two fresh target/fixture checks before further mutation. Its `basedOn` record links the original report with SHA-256 `653545bd4196317094428539131819e8f4140dab0c4448c3267aa0e83f7e5a6b`.

The local report failure was investigated before continuing. The runner persists a request record before sending it; trace 87 had no finished response or transport error, the failure was at that pre-send persistence step, and the server journal contained only the two earlier compile calls. Fresh reads then confirmed the target and restored fixture identities. The reviewed completion performed the previously unsent repair compile and remaining deletion/final checks; no successful write was replayed. The original failed report was preserved. The shared report writer subsequently gained bounded retries for transient local file replacement failures only; this does not retry an MCP request or enable automatic CLI resumption.

| Native scenario | Observed result |
|---|---|
| Populated table XML export/import | Native `WithReadOnly` SimaticML export returned exact content and a matching checksum; native import followed by typed readback preserved the Bool tag and Int constant value `37`. |
| Initial compile | Complete diagnostics, native `Warning`, **0 errors / 1 warning**, and `compilationSucceeded:true`. |
| Deliberately missing fixture tag | Deleting the run's referenced tag produced native `Error`, **1 error / 1 warning**, `complete:true`, `compilationSucceeded:false` and MCP `isError:true`; the recursive native diagnostic identified the exact missing fixture tag. |
| Repair compile | The tag was restored and its fresh native ID verified. Compilation then completed with **0 errors / 1 warning**. |
| Block, UDT and table deletion | All three deletion tools succeeded. Fresh matching inventories showed each fixture absent and detail reads using the former IDs returned `objectNotFound`. |
| Final compile and status | After fixture deletion, compilation completed with **0 errors / 1 warning** and the same project remained connected. The retained warning concerns references to I/O absent from the configured hardware and was already present before the deliberate fixture error. |

The run's block, UDT and tag-table fixtures were deleted, and the project remained unsaved. No save, PLC upload/download, online-state change or attachment change was performed by this suite. This verifies the tested native compile/error/repair, deletion and populated-table XML round-trip scenarios; it does not establish arbitrary XML edit semantics or PLC runtime behavior.

## Local verification and reload — 2026-09-23

- The Release build against the installed V20 Public API passed. `NU1900` reported an unreachable NuGet vulnerability advisory feed; there were no compiler errors.
- The Siemens-free .NET 8 harness passed **118/118** groups, including request/schema dispatch, access enforcement, current compiler diagnostics, deletion guards and exact tag-table export/cleanup behavior.
- JavaScript checks passed **126/126 tests**: 104 earlier checks, 14 lifecycle checks and 8 report-persistence checks. These use simulated responses and do not establish native acceptance.
- Both live HTTP smoke checks passed against the managed server. They verified publication and guarded rejection paths without performing native project mutations.
- The managed helper loaded the successful Release executable as PID **17872**, full access, with twenty-four published tools and phase `native-compile-delete-export`. This process ID is a snapshot, not a persistent selector.

The restart released bridge attachments. The user subsequently reconnected the intended project before the native run above; it remained connected at the final status check, so no further reconnection was required. The local checks and reload alone do not establish the native results; those are supported separately by the linked run reports.
