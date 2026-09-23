# Native import matrix runs

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

## Native evidence — 2026-09-22

The user authorized broader writes in the already connected disposable `Prototype-A-1` project (process 28572, PLC_100). The user also reported that SCL SIMATIC SD export was unavailable through the Siemens import/export add-in and identified `Main` and `WaterTank_Core` as LAD examples. The agent exported both as SIMATIC SD and SimaticML through MCP successfully. No installed update level was inferred.

| Run directory under `test-results/native-import-acceptance/` | Observed result |
|---|---|
| `2026-09-22T17-56-21-176Z_McpIM_a9e6f0d5825d` | External SCL FC, SCL FB and STL FC creation/readback passed. DB import succeeded, but its readback assertion expected the initializer beside the declaration. Native export instead placed `SampleCount := 17;` in `BEGIN`. The assertion was corrected with a regression test; source inputs were unchanged. |
| `2026-09-22T18-01-20-227Z_McpIM_a9e6f0d5825d` | Explicit continuation reread the existing DB without importing it again. All nine external/SD creation cases passed, including LAD declaration-only/bundle inputs. The first XML write was rejected because the authored block omitted `Namespace`. Fresh inventory in `post-failure-inventory.json` showed no block with that failed fixture name. |
| `2026-09-22T18-03-28-022Z_McpIM_7cb26c907481` | Corrected XML included `Namespace`, but the SCL literal's `ConstantType` was rejected as a writable field. The fixture now retains the native `Informative="true"` marker observed in the export. Post-failure inventory again showed no block with the failed fixture name. No rollback was inferred from the error response. |
| `2026-09-22T18-04-06-129Z_McpIM_1c27f6ce3f4e` | **All 14 creation and 14 semantic-update cells passed**, with 61 successful execution/check steps. This fresh run used the corrected XML inputs and verified every listed import route. Final original LAD SD documents matched exactly, and the same project remained connected. No uncertain write was recorded. |

The final run contains 28 successful write calls and independent readbacks. XML cases ran first to occupy their explicitly reserved block numbers before auto-numbered source generation. No rejected import was automatically retried: failed evidence was preserved, the named-object inventory was inspected, and corrected inputs were submitted in a fresh run with a new prefix.

At completion of the automated run, source readbacks succeeded without a compile checkpoint. Most generated/imported block and UDT metadata still reported `isConsistent:false`; those source assertions alone did **not** establish successful offline compilation. The runner made no explicit compile/save/download call, changed no attachment, restarted no server, and changed no production server source.

The user subsequently reported running **Compile → Rebuild all** for both blocks and data types, with everything compiling successfully. This is user-reported compiler evidence; the MCP publication at that time did not expose compiler diagnostics. The agent then performed metadata-only MCP reads against all **13 block/UDT fixtures** in the final matrix (ten blocks and three UDTs). Every retained name/ID matched and every object returned **`isConsistent:true`**, with complete responses and no errors. The same `Prototype-A-1` project remained connected. Exact requests and responses are preserved in `post-compile-verification.json` alongside the final report, linked by that report's SHA-256. The original run evidence is unchanged. This closes the offline-compilation follow-up for the tested fixtures; PLC runtime behavior remains unverified.

All **64** local acceptance/fixture checks passed after the fixture corrections. The remaining objects from both prefixes are recorded in the local reports. Earlier test prefixes and the original incomplete SCL-SD probe report remain separate historical evidence; this LAD-based matrix supplies the previously missing block SD import verification.
