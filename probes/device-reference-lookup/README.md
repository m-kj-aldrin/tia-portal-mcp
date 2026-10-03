# Device reference lookup probe

## Question

For repeated `get_device` style reads, how much time goes to obtaining the project's `ObjectIdentifierProvider` and resolving a Device ID, compared with reading the Device and its nested `DeviceItem` metadata? Would retaining a native Device reference across calls save meaningful time?

This is a standalone, read-only Openness probe. It does not call MCP or change product code. Its metadata walk approximates the native work in `get_device`; it does not construct or serialize the MCP response, and it does not include the product's request scheduling or context guards in the timed region.

## Run against an explicitly selected disposable project

From the repository root on Windows:

```powershell
dotnet build probes/device-reference-lookup/DeviceReferenceLookup.csproj --configuration Release
.\probes\device-reference-lookup\bin\Release\net48\DeviceReferenceLookup.exe --list
```

Supply the listed PID, UTC start ticks and full project path. The probe verifies all three before and after attaching, and between timing passes:

```powershell
.\probes\device-reference-lookup\bin\Release\net48\DeviceReferenceLookup.exe `
  --process-id 12345 `
  --expected-start-ticks 638000000000000000 `
  --project-path 'C:\path\to\Disposable.ap20' `
  --passes 10
```

If omitted, `--device-id` selects the Device with the most nested DeviceItems. Pass a specific Siemens Device `objectId` to target another Device. `--passes` accepts 1–30; the default is 10. If TIA requests external-access approval, approve it in TIA before continuing.

The probe measures three forms of a repeated read: fresh provider and `Find`, retained provider and `Find`, and retained Device reference. It also times lookup and a probe approximation of the process/project identity check in batches of 100 to expose smaller costs. The order alternates, and every approach must return the same sampled values. All timings are warm repeated calls on one Device. The console prints medians and ranges; these are exploratory numbers, not product benchmarks or proof of a distinct cross-process round trip per property. The context-check timing is not the product's exact `ConnectionRegistry.Validate` implementation.

The probe never writes, exports, compiles, saves, opens or closes a project. It detaches by disposing only its own `TiaPortal` attachment. It never disposes a `TiaPortalProcess` descriptor, which would close the TIA window. Keep the selected process disconnected from the managed MCP server while this separate probe runs.

The first SclStyle run and its limits are recorded in [the evidence index](../../docs/evidence.md#manual-native-device-reference-probe).
