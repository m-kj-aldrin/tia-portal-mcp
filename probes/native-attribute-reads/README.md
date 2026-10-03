# Native attribute-read probe

## Question

Could block inventory use one native attribute call per block instead of separate `Name`, `Number`, and `ProgrammingLanguage` property reads? Can device-item metadata reuse values already returned by its bulk attribute call instead of fetching the same values again through typed properties?

This probe reports returned values and CLR types, missing attributes, errors, and elapsed times. It does not change the product implementation or prove that every V20 object behaves the same way.

## Select the disposable TIA process

Build from the repository root on Windows with the installed .NET 8 SDK:

```powershell
dotnet build probes/native-attribute-reads/NativeAttributeReads.csproj --configuration Release
```

The build **does not attach to TIA**. List visible processes without attaching:

```powershell
.\probes\native-attribute-reads\bin\Release\net48\NativeAttributeReads.exe --list
```

Choose the disposable process from the printed PID, UTC start ticks, and project path. Then supply all three values explicitly:

```powershell
.\probes\native-attribute-reads\bin\Release\net48\NativeAttributeReads.exe `
  --process-id 12345 `
  --expected-start-ticks 638000000000000000 `
  --project-path 'C:\path\to\Disposable.ap20'
```

The numbers and path above are placeholders. The probe checks the selected process's UI mode, start time, and project path before attachment and again after attachment. A missing or changed target stops the run. It never starts TIA or opens a project. If TIA requests external-access approval, pause and approve it in TIA yourself. Leave this process disconnected from the managed MCP server while the probe runs.

Optional `--limit N` caps block and device-item samples separately (default 100; maximum 500). `--passes N` sets timing repetitions (default 5; maximum 20). `--help` prints the syntax. Output goes to the console; redirect it to a file outside the repository if you want to retain raw results.

## Native operations and interpretation

The probe attaches once to the selected existing UI process, retains its one open primary project, and reads native device items and PLC blocks on its STA main thread. It compares three block approaches: typed properties, `GetAttributes` with three explicit names, and `GetAttributes` with read access flags. For device items it compares the current pattern (bulk readable attributes plus typed property reads) with reuse of the bulk values. It does no source export, write, compile, save, upload, download, project open, or automatic retry.

The probe prints up to five example differences or errors per comparison. Text equality and CLR type equality are reported separately; matching printed text alone is not proof of semantic equivalence. Timing covers repeated reads of objects where both compared approaches completed, and alternates their order. Discovery, attachment, project traversal, and identifier lookup are outside the timed section because those costs would remain in both implementations. A representative speed difference and acceptable values/types are both needed before considering a product change.

The probe calls only `TiaPortal.Dispose()` on its own attachment. It never disposes `TiaPortalProcess`, saves, or closes the TIA window. A build or `--list` is not native acceptance, and this probe is not part of the MCP test suite.

The first manual V20 run on `SclStyle.ap20`, and a second run after adding the [block-scale fixture](../fixtures/sclstyle-block-scale/README.md), are recorded with their sample sizes, values, timings and limits in [the evidence index](../../docs/evidence.md#manual-native-attribute-probe).
