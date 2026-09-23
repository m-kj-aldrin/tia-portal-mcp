# PLC compilation, native deletion and tag-table export

These five tools extend the existing engineering operations without changing the connection or host model. Full access publishes twenty-four tools: twelve reads and twelve modifying operations. Explicit read-only access includes `export_tag_table` and rejects all deletion and compilation requests. Initialization reports version `native-compile-delete-export-1`; passive status reports phase `native-compile-delete-export` and publication `twenty-four-read-write-tools` or `twelve-read-only-tools`.

Every request targets the user's existing dashboard attachment and retained project, captures its attachment ticket before queueing, and runs on the shared STA. The normal before/after/failure context checks apply. There is no automatic reconnect, retry or save. Saving projects and PLC upload/download are permanently outside the MCP surface. Other online actions are not exposed.

| Tool | Required arguments | Native operation | Access |
|---|---|---|---|
| `compile_plc` | `processId`, `plcObjectId` | `PlcSoftware.GetService<ICompilable>().Compile()` | Full |
| `delete_block` | `processId`, `objectId` | `PlcBlock.Delete()` | Full |
| `delete_udt` | `processId`, `objectId` | `PlcType.Delete()` | Full |
| `delete_tag_table` | `processId`, `objectId` | `PlcTagTable.Delete()` | Full |
| `export_tag_table` | `processId`, `objectId` | `PlcTagTable.Export(FileInfo, ExportOptions.WithReadOnly)` | Read-only or full |

Use the CPU DeviceItem ID for compilation and the selected object's own native ID for deletion/export. Unknown or duplicate arguments, wrong selector types and blank IDs are rejected. There are no optional force, cascade, rebuild, format, path or save parameters. Complete request examples are in [write operations](write-operations.md#explicit-compilation-object-deletion-and-table-export).

## Compilation and diagnostics

`compile_plc` resolves the CPU DeviceItem directly through the retained project's `ObjectIdentifierProvider`, obtains its `SoftwareContainer` and `PlcSoftware`, then invokes `ICompilable.Compile()` once. It compiles that PLC software scope, not a project-wide hardware/software rebuild. Siemens requires devices to be offline; this tool does not change their online state. See the [Siemens V20 compilation contract](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/compiling-a-project).

The result contains `operation`, `plcObjectId`, `saved:false`, nullable native `projectModified`, native `state`, `errorCount`, `warningCount`, and recursive `messages`. Each message preserves `path`, UTC `dateTime`, `state`, `description`, `errorCount`, `warningCount` and nested `messages`. Native order and hierarchy are retained. These diagnostics belong to this invocation; the implementation does not read historical compiler messages from TIA or scrape its UI.

`complete` describes successful retrieval of the API result and diagnostics. `compilationSucceeded` separately describes compiler success:

| Result | Meaning | MCP `isError` |
|---|---|---|
| `complete:true`, `compilationSucceeded:true` | Native compilation completed without compiler errors; warnings can remain | `false` |
| `complete:true`, `compilationSucceeded:false` | Native compiler error result was fully read; inspect recursive messages | `true` |
| `complete:false`, `compilationSucceeded:null` | A native/API result field or diagnostic collection could not be fully read; inspect `errors` and retained fields | `true` |

Native compiler error messages remain in `messages`, rather than being converted into invented bridge exceptions. Ordinary native call/diagnostic failures preserve their original text and origin. Context loss discards the result and requires explicit reconnection; it does not establish that compilation had no effect.

Installed V20 API inspection found the parameterless `Compile()` operation, with no force-rebuild-all overload. No equivalent of the TIA UI's **Rebuild all** is claimed. Writing/importing a source still performs its native intrinsic processing, but the bridge does not add an implicit compile step. Source readback, native consistency metadata and compiler diagnostics remain distinct evidence.

## Whole-object deletion

Each deletion resolves one native target directly and requires the corresponding `PlcBlock`, `PlcType` or `PlcTagTable`. The bridge captures its native name, kind, requested identifier and parent identifier before calling `Delete()` once. After successful return and context validation, `affectedObjects` contains that pre-deletion identity; it never reads properties from the deleted proxy.

Deletion uses the existing mutation response, including `saved:false`, native `projectModified`, `complete`, `errors` and `affectedObjects`. Native object restrictions, permissions and dependency behavior remain authoritative. The bridge does not promise forced deletion, custom cascading, transactional rollback or automatic repair of references. Read the corresponding inventory afterward and verify that the target is absent; an old-ID detail read should also fail with `objectNotFound` when TIA no longer resolves it.

The installed V20 Public API documents `Delete()` on all three types. Siemens' examples confirm native [block deletion](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/deleting-block) and [UDT deletion](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/deleting-user-data-type); its V20 [tag-table API chapter](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/tags-and-tag-tables) lists tag-table deletion. Offline prerequisites and any native refusal are preserved, without changing online state.

## Tag-table export

`export_tag_table` resolves exactly one `PlcTagTable`, calls its native SimaticML export, and returns `objectId` and nullable `source` alongside the standard discovery envelope. `source.format` is always `simatic-ml`. The document is named `tag-table.xml`, and its returned text is not reformatted, parsed into entries or rebuilt from `get_tag_table` JSON.

The installed V20 API exposes `Export(FileInfo, ExportOptions)` and the overload adding `DocumentInfoOptions`. Its XML documentation explicitly permits read-only values. This implementation follows the existing source exporter's `WithReadOnly` convention. Inspection found no `ExportAsDocuments` method or `IGenerateSource` interface on `PlcTagTable`, so there is no SIMATIC SD or external-source option and no fallback. Siemens documents [one XML file per exported PLC tag table](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/tag-tables/exporting-plc-tag-tables).

The bridge generates a unique owned directory under the operating-system temporary folder, decodes the native file without changing line breaks or text, and computes SHA-256 over the returned content encoded as UTF-8 without BOM. This is an exact returned-content checksum, not a claim that the original file's BOM or byte encoding is preserved. The client cannot supply a server path. Cleanup runs after success and failure, refuses unexpected links/directories, and reports any cleanup failure as `temporaryCleanup` with `complete:false`.

`get_tag_table` keeps its typed metadata/entry contract unchanged and never exports. To import exported XML, supply each returned document's `name` and `content` to `import_tag_tables` in the intended CPU/scope; omit the read-result checksum. Native Override determines replacement behavior. Successful export alone does not prove successful import or semantic equivalence.

## Native acceptance

The five added tools passed the scoped native scenarios described below on 2026-09-23, across an initial run and a separately reviewed completion using the same fixtures. Installed API inspection, local tests and native results remain separate evidence. Earlier nineteen-tool results and user-reported successful writes remain accepted within their recorded scope.

The original [native acceptance runner](native-acceptance.md) retains its nineteen-tool scenario set against the twenty-four-tool publication. The new suite targets the five added operations:

```powershell
node tests/native-lifecycle-acceptance.cjs --process-id 12345 --project-path 'C:\TestProjects\Acceptance\Acceptance.ap20'
```

Run only against the already user-connected disposable project with the PLC offline. Replace the sample process/path with that authorized target. The runner uses the existing `/mcp` endpoint and creates uniquely named fixtures. It checks successful compilation, deliberately breaks a fixture's own tag reference to obtain native compiler errors, repairs it and compiles again. It also checks a populated table's XML export/checksum and import/readback, deletes its block/UDT/table fixtures, verifies inventory absence and old-ID read failures, then compiles after cleanup.

Optional arguments include `--plc-object-id`, `--address`, `--endpoint`, `--output` and `--timeout-ms`; use `--help` for the current command summary. The runner records requests, responses and assertions, never attaches, changes online state, saves, uploads, downloads or retries an uncertain write. A failed run can leave its recorded fixtures for inspection; it does not claim rollback. Native errors elsewhere in the selected PLC may affect compilation, so its diagnostics must remain visible rather than being attributed automatically to the fixture.

The intended checks establish the tested engineering API behavior, not PLC runtime behavior, all CPU families, protected/safety objects, every native restriction or historical compiler-log retrieval.

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
