# Automated native MCP acceptance

This opt-in runner calls the published MCP tools on the one already-running server. It checks the response envelope and uses independent read calls to verify engineering changes in a disposable TIA V20 project. The user connects the intended TIA UI process through the dashboard and accepts external access. The runner cannot attach, reconnect, open a project or manage the server.

For broader creation and semantic replacement coverage by file format and object kind, use the [native import matrix](native-import-matrix.md). Its LAD fixtures cover SIMATIC SD block imports without relying on SCL SD support. The original runner below retains its nineteen-tool scenario set while accepting the current twenty-four-tool publication; its unchanged XML/SD reimports are preservation checks, not evidence of semantic edits in those formats. The five added compile/delete/export tools have a separate [native lifecycle suite](compile-delete-export.md#native-acceptance).

## Run

Open a disposable project or copy containing a supported PLC CPU. Connect it in the dashboard yourself. Then run from this repository:

```powershell
node tests/native-acceptance.cjs --process-id 12345 --project-path 'C:\TestProjects\Acceptance\Acceptance.ap20'
```

Replace the example process ID and path with the connected disposable target. Running this command authorizes the described fixture writes to that target. The expected path is checked before any fixture creation and again before every write. The runner uses the published tools through `http://127.0.0.1:5000/mcp`; it does not create another listener or invoke native APIs directly.

A project with one discovered PLC CPU needs no additional selector. If it has multiple CPUs, the preflight report lists their native IDs and stops before writing; select one with `--plc-object-id '<native CPU DeviceItem ID>'`. The runner reads existing tag tables and chooses an unused memory byte in M0..M255 for its Bool tag. An explicit `--address '%M100.0'` is checked for overlap with existing memory tags. This is an engineering fixture address, not a statement about online memory use or CPU execution.

Optional arguments are `--endpoint http://localhost:5000/mcp`, `--output <report-directory>` and `--timeout-ms 120000`. The endpoint must remain local. Use `--help` for the command summary.

Native source generation can leave the new block/UDT inconsistent. After the core write/read-back scenarios, this runner checks native consistency and stops with `blocked` if offline compilation is required. It does not compile automatically. Compile the selected PLC software explicitly in TIA or through the separately authorized `compile_plc` operation, inspect its diagnostics, then continue using the same process/path arguments plus:

```powershell
--resume-report 'test-results\native-acceptance\<stopped-run>\report.json'
```

Continuation checks the recorded project, CPU, fixture names and retained IDs, and requires passed core scenarios. It creates a separate report linked to the earlier evidence; passed creation/edit scenarios are not repeated. A stopped scenario that attempted a write, or a report with an uncertain write, cannot be resumed this way. The user must explicitly request the continuation; there is no background retry.

## What the tests establish

Each run generates a fresh `McpAT_<random>` prefix. It checks for existing names before creation and updates only its own fixtures. Replacements reacquire native IDs from fresh inventories; they do not assume a replaced block or UDT keeps its ID.

| Scenario | Read-back evidence |
|---|---|
| Publication and target preflight | Twenty-four published tool names; manually connected process; expected project path; full access; discovered CPU |
| Tag-table creation | New table identity in inventory; empty tag/constant collections; metadata-only returns null entries/path |
| Tag creation and editing | Native tag ID, Bool type, memory address and changed boolean attribute |
| Populated user constant | Native ID, Int type and literal value changed from 100 to 200 |
| Tag and constant deletion | Separate disposable entries disappear by both name and ID; retained entries remain |
| Expected native attribute failure | MCP error and native error text; the fixture entry remains readable and unchanged |
| Tag-table SimaticML import and Override | A unique empty table is imported, reacquired and read; importing the same document again retains its expected name and empty contents |
| Block source creation/update | Exported FC assignment changes from 17 to 29; its reference to the fixture tag is retained |
| UDT source creation/update | Exported Bool field remains and an Int field is added |
| Native consistency checkpoint | Both retained source fixtures report consistent after any necessary explicitly requested offline compile |
| Explicit SD and SimaticML block/UDT imports | Export the owned object, import its complete native documents, reacquire identity and verify the source assertions again |
| Cross-references | The fixture tag's source has a UsedBy/Read reference to the generated FC |
| Final status | The same project remains connected |

Checksums verify the exact exported document content. Cross-references are checked while fixtures are consistent, before format imports. The runner captures all pending native format documents before replacing either object, since imports can make objects inconsistent again. Cross-format round trips compare the fixture's stated source assertions, not byte equality: native formatting, IDs and timestamps can change. A passing source assertion does not prove PLC runtime behavior or exhaustive equivalence of arbitrary programs.

The small tag-table XML fixture is authored test input, not an existing verified TIA export. Its first successful native import/readback establishes acceptance of that document on the tested installation. Provenance is recorded in the report. [Siemens documents SCL-to-SIMATIC-SD support from V20 Update 4](https://docs.tia.siemens.cloud/r/en-us/v20-updates/tia-portal-updates-readme/improvements-in-step-7/improvements-in-update-4); the runner does not infer the installed update from a major-version string.

This test suite is additional native evidence. Previously accepted manual checks remain accepted; this does not reclassify them as agent-executed results. A native format-export rejection on a still-consistent retained fixture is recorded as `unavailable`, with the exact native error and a coverage gap. Independent captured formats can still be tested; the rejected format is never silently replaced or counted as passed. Unexpected errors, transport/context failures and any write/read-back failure stop the run. There is no automatic write retry. Later scenarios and uncalled tools are not counted as passed.

## Evidence and remaining objects

The runner writes `test-results/native-acceptance/<run>/report.json` and `report.md`. These local files are ignored by Git because they contain project paths, native identities and complete requests/source/responses. The JSON file records each request before sending it and retains response text, HTTP status, timings, assertion failures and partial affected objects. Exit code zero requires a passed run with all nineteen tools in its original scenario set called and no declared coverage gap; it does not claim coverage of all twenty-four published tools.

An explicit output directory containing an earlier report is rejected so previous evidence remains intact.

This runner retains its uniquely named block, UDT and tag-table fixtures for inspection; it does not call the new whole-object deletion tools. It records their names/IDs and any partial write results. It never claims rollback or a restored project. The user can discard the disposable project's unsaved changes in TIA when finished. This runner never saves, explicitly compiles, uploads, downloads, closes TIA or performs an online action. Native source generation may perform the processing intrinsic to that native operation. The separate lifecycle suite creates and deletes its own fixtures.

A timeout or unreadable write response leaves the outcome uncertain. Inspect the report and project before starting another run; the runner does not retry. A connection/context error stops testing and requires user-controlled reconnection.

Some native cases remain outside this small fixture: populated system constants, protected objects, safety/unit scopes, every native format/object combination, forced mid-write failures, native temporary-source leak inspection and lifecycle/PID-reuse scenarios. `cleanupFailed:false` establishes the bridge's reported cleanup result, not an independent inspection of all native external sources.

## Test the runner without TIA

```powershell
node --test tests/native-acceptance-runner.test.cjs tests/native-acceptance-scenarios.test.cjs
```

These checks use in-memory fake responses and start no server. They verify target selection, no retry on uncertain writes, raw evidence retention and that read-back assertions catch writes reporting success without the intended effect. They do not establish native acceptance; only an explicitly executed real run can do that.

Initial implementation verification on 2026-09-22: all 27 runner/scenario checks and the existing 37 dashboard/architecture checks passed before native execution.

## Evidence

The original runner exercised all nineteen tools in its scenario set across linked runs. Its final recorded run remained incomplete because SCL FC SIMATIC SD export was unavailable. The later LAD-based import matrix supplies the missing block SD import verification without relabeling the original run as passed.

<a id="first-native-runs--2026-09-22"></a>

The original dated record is preserved in [original native acceptance runs](../reference/history/native-acceptance-runs.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
