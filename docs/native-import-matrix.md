# Native import matrix

This extends the [nineteen-tool acceptance checks](native-acceptance.md) with distinct native import cases. Calling every tool does not establish every import format. The original unchanged-document XML/SD round trips demonstrated preservation; this matrix separately verifies creation and a meaningful change to each fixture.

## Run

The user connects a disposable project in the dashboard and accepts TIA external access. Run against the one existing MCP server:

```powershell
node tests/native-import-acceptance.cjs --process-id 12345 --project-path 'C:\TestProjects\Acceptance\Acceptance.ap20'
```

Replace the example process ID and path with the authorized disposable project. Optional arguments match the basic runner: `--plc-object-id`, `--address`, `--endpoint`, `--output`, and `--timeout-ms`. This command writes the fixtures listed below. It never attaches, reconnects, saves, explicitly compiles, downloads or closes TIA.

If a successful import cannot yet be read back because native metadata reports it inconsistent, the report records `compile-required`. Independent creation cases can continue, but replacements wait until all creation readbacks pass. Compile the selected PLC software offline in TIA, then explicitly continue:

```powershell
node tests/native-import-acceptance.cjs --process-id 12345 --project-path 'C:\TestProjects\Acceptance\Acceptance.ap20' --resume-report 'test-results\native-import-acceptance\<blocked-run>\report.json'
```

Continuation creates a new linked report, validates the retained target and fixture plan, and resumes reads without repeating successful imports. A diagnosed readback assertion failure may also be explicitly resumed when the native write result was already verified and the submitted documents remain exactly the same. Failed or uncertain writes cannot use this path. The runner never uses a successful native response alone as a semantic pass.

## Planned matrix

Each row has separate creation and semantic-update cells. Fixture names begin with a unique `McpIM_<random>` prefix. The supplied native declarations, not staging filenames, identify the objects. Writes use root PLC compositions; IDs are reacquired after each replacement.

| Case | Tool | Native input | Fixture and verified change |
|---|---|---|---|
| `external.scl.fc` | `write_blocks` | One `.scl` | SCL FC: Marker assignment 17 to 29 |
| `external.scl.fb` | `write_blocks` | One `.scl` | SCL FB: Marker assignment 17 to 29; static Count retained |
| `external.awl.fc` | `write_blocks` | One `.awl` | STL FC: load 17 to 29, transferred to Marker |
| `external.db` | `write_blocks` | One `.db` | Global DB: SampleCount initial value 17 to 29 |
| `external.udt` | `write_udts` | One `.udt` | UDT: Flag Bool retained; SampleCount Int added |
| `sd.lad.fb.dcl` | `write_blocks` | One `.s7dcl` | LAD FB: Move 17 to 29 into Marker |
| `sd.lad.fb.bundle` | `write_blocks` | `.s7dcl` + matching `.s7res` | LAD FB: same semantic change, paired resource input |
| `sd.db` | `write_blocks` | One `.s7dcl` | Global DB: Flag Bool retained; SampleCount Int added |
| `sd.udt` | `write_udts` | One `.s7dcl` | UDT: Flag Bool retained; SampleCount Int added |
| `xml-scl-fc` | `write_blocks` | One SimaticML `.xml` | SCL FC: Marker assignment 17 to 29 |
| `xml-lad-fb` | `write_blocks` | One SimaticML `.xml` | LAD FB: Move 17 to 29 into Marker |
| `xml-db` | `write_blocks` | One SimaticML `.xml` | Global DB: Flag Bool retained; SampleCount Int added |
| `xml-udt` | `write_udts` | One SimaticML `.xml` | UDT: Flag Bool retained; SampleCount Int added |
| `xml-tags` | `import_tag_tables` | One SimaticML `.xml` | Bool tag, address and native entry IDs; Int constant 17 to 29 |

This covers the seven current tool/format routes: external generation for blocks and UDTs, XML Override imports for blocks/UDTs/tag tables, and SD Override imports for blocks and UDTs. It exercises all four accepted external-source extensions and both accepted SD document arrangements. Native support still depends on the target CPU and installation; planned cells do not imply successful execution.

Source builders are bounded authored test inputs using native syntax observed in this project. They are not general source converters. Their native validity is established only by the actual import/readback results. The SD bundle uses the empty `<root />` resource content returned by the existing LAD exports; this does not establish preservation of nonempty translated resources.

Code fixtures are read back as external source for SCL/STL, and as SIMATIC SD for LAD. DB/UDT structure is checked through external source. Tag-table imports are checked through typed entries. Returned source checksums must match the exact UTF-8 content. XML block numbers are reserved separately by kind and rechecked against native inventory before import.

Existing LAD blocks outside the reserved `McpAT_`/`McpIM_` test prefixes are exported as both SimaticML and SIMATIC SD before writes. Their original SD documents and identities are retained and compared after the matrix. The suite creates its own named fixtures rather than replacing the existing LAD blocks.

## Reports and limits

Reports are written to ignored `test-results/native-import-acceptance/<run>/report.json` and `report.md`. Every planned cell remains visible, including unattempted and compile-pending cases. JSON evidence includes exact submitted documents and SHA-256 hashes, raw JSON-RPC requests/responses, write results and affected objects, fresh native IDs, readback contents, and the continuation chain. Exit zero requires all 28 creation/update cells to pass their semantic readback.

The matrix does not claim every instruction or IEC type, every CPU family/version, every object-kind/format combination, instance DBs, OB replacement, software-unit/user-group destination overloads, multi-declaration generation, protected/safety objects, rich resource localization, or forced partial-failure recovery. It checks engineering source and metadata, not PLC execution. Those are distinct additional scenarios, not failures hidden by a nineteen-tool count.

Fixture blocks, UDTs and tag tables remain because whole-object deletion is not published. No rollback or restored project is claimed. The user can inspect them in the disposable project and discard changes when finished.

## Local checks

```powershell
node --test tests/native-import-fixtures.test.cjs tests/native-xml-import-fixtures.test.cjs tests/native-import-matrix.test.cjs tests/native-acceptance-runner.test.cjs tests/native-acceptance-scenarios.test.cjs
```

These tests catch wrong-target writes, unchanged semantic results, same-name identity replacement, stale source-plan changes, invalid declared contents, unexpected affected objects and source-export checkpoint classification. They are Siemens-free and do not establish native acceptance.

## Native evidence — 2026-09-22

The user authorized broader writes in the already connected disposable `Prototype-A-1` project (process 28572, PLC_100). The user also reported that SCL SIMATIC SD export was unavailable through the Siemens import/export add-in and identified `Main` and `WaterTank_Core` as LAD examples. The agent exported both as SIMATIC SD and SimaticML through MCP successfully. No installed update level was inferred.

| Run directory under `test-results/native-import-acceptance/` | Observed result |
|---|---|
| `2026-09-22T17-56-21-176Z_McpIM_a9e6f0d5825d` | External SCL FC, SCL FB and STL FC creation/readback passed. DB import succeeded, but its readback assertion expected the initializer beside the declaration. Native export instead placed `SampleCount := 17;` in `BEGIN`. The assertion was corrected with a regression test; source inputs were unchanged. |
| `2026-09-22T18-01-20-227Z_McpIM_a9e6f0d5825d` | Explicit continuation reread the existing DB without importing it again. All nine external/SD creation cases passed, including LAD declaration-only/bundle inputs. The first XML write was rejected because the authored block omitted `Namespace`. Fresh inventory in `post-failure-inventory.json` showed no block with that failed fixture name. |
| `2026-09-22T18-03-28-022Z_McpIM_7cb26c907481` | Corrected XML included `Namespace`, but the SCL literal's `ConstantType` was rejected as a writable field. The fixture now retains the native `Informative="true"` marker observed in the export. Post-failure inventory again showed no block with the failed fixture name. No rollback was inferred from the error response. |
| `2026-09-22T18-04-06-129Z_McpIM_1c27f6ce3f4e` | **All 14 creation and 14 semantic-update cells passed**, with 61 successful execution/check steps. This fresh run used the corrected XML inputs and verified every listed import route. Final original LAD SD documents matched exactly, and the same project remained connected. No uncertain write was recorded. |

The final run contains 28 successful write calls and independent readbacks. XML cases ran first to occupy their explicitly reserved block numbers before auto-numbered source generation. No rejected import was automatically retried: failed evidence was preserved, the named-object inventory was inspected, and corrected inputs were submitted in a fresh run with a new prefix.

The final source readbacks succeeded without a compile checkpoint. Most generated/imported block and UDT metadata still reported `isConsistent:false`; the native source assertions do **not** establish a successful offline compilation. Compilation and runtime behavior remain unverified for these new fixtures. The runner made no explicit compile/save/download call, changed no attachment, restarted no server, and changed no production server source.

All **64** local acceptance/fixture checks passed after the fixture corrections. The remaining objects from both prefixes are recorded in the local reports. Earlier test prefixes and the original incomplete SCL-SD probe report remain separate historical evidence; this LAD-based matrix supplies the previously missing block SD import verification.
