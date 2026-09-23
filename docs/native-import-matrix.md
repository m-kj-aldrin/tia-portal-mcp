# Native import matrix

This extends the [original nineteen-tool acceptance scenario set](native-acceptance.md) with distinct native import cases. The current server publishes twenty-four tools; compilation, whole-object deletion and table export are covered separately by the [native lifecycle suite](compile-delete-export.md#native-acceptance). Calling every tool does not establish every import format. The original unchanged-document XML/SD round trips demonstrated preservation; this matrix separately verifies creation and a meaningful change to each fixture.

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

## Fixture matrix

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

This covers the seven current tool/format routes: external generation for blocks and UDTs, XML Override imports for blocks/UDTs/tag tables, and SD Override imports for blocks and UDTs. It exercises all four accepted external-source extensions and both accepted SD document arrangements. Native support still depends on the target CPU and installation; the matrix defines the reusable suite independently of its [recorded results](evidence.md).

Source builders are bounded authored test inputs using native syntax observed in this project. They are not general source converters. Their native validity is established only by the actual import/readback results. The SD bundle uses the empty `<root />` resource content returned by the existing LAD exports; this does not establish preservation of nonempty translated resources.

Code fixtures are read back as external source for SCL/STL, and as SIMATIC SD for LAD. DB/UDT structure is checked through external source. Tag-table imports are checked through typed entries. Returned source checksums must match the exact UTF-8 content. XML block numbers are reserved separately by kind and rechecked against native inventory before import.

Existing LAD blocks outside the reserved `McpAT_`/`McpIM_` test prefixes are exported as both SimaticML and SIMATIC SD before writes. Their original SD documents and identities are retained and compared after the matrix. The suite creates its own named fixtures rather than replacing the existing LAD blocks.

## Reports and limits

Reports are written to ignored `test-results/native-import-acceptance/<run>/report.json` and `report.md`. Every planned cell remains visible, including unattempted and compile-pending cases. JSON evidence includes exact submitted documents and SHA-256 hashes, raw JSON-RPC requests/responses, write results and affected objects, fresh native IDs, readback contents, and the continuation chain. Exit zero requires all 28 creation/update cells to pass their semantic readback.

The matrix does not claim every instruction or IEC type, every CPU family/version, every object-kind/format combination, instance DBs, OB replacement, software-unit/user-group destination overloads, multi-declaration generation, protected/safety objects, rich resource localization, or forced partial-failure recovery. It checks engineering source and metadata, not PLC execution. Those are distinct additional scenarios, not failures hidden by a nineteen-tool count.

This import matrix retains its fixture blocks, UDTs and tag tables for inspection; it does not call the whole-object deletion tools. The separate [lifecycle suite](compile-delete-export.md#native-acceptance) verifies deletion of its own fixtures. No rollback or restored project is claimed. The user can inspect retained fixtures in the disposable project and discard changes when finished.

## Local checks

```powershell
node --test tests/native-import-fixtures.test.cjs tests/native-xml-import-fixtures.test.cjs tests/native-import-matrix.test.cjs tests/native-acceptance-runner.test.cjs tests/native-acceptance-scenarios.test.cjs
```

These tests catch wrong-target writes, unchanged semantic results, same-name identity replacement, stale source-plan changes, invalid declared contents, unexpected affected objects and source-export checkpoint classification. They are Siemens-free and do not establish native acceptance.

## Evidence

The final 2026-09-22 run passed all 14 creation and 14 semantic-update cells. The user later reported successful compilation; independent metadata reads then found all 13 retained block/UDT fixtures consistent. This is engineering evidence for the tested fixtures, not PLC runtime verification.

<a id="native-evidence--2026-09-22"></a>

The original dated record is preserved in [native import matrix runs](../reference/history/native-import-matrix-runs.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
