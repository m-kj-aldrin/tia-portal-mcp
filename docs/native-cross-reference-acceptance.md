# Expanded native cross-reference acceptance on V20

TIA Portal V20 is the supported test target. This suite extends the earlier single-tag examples with a known fixture graph, then checks native relationships against that graph. It uses the existing MCP endpoint and manually connected disposable project. It never connects, saves or transfers anything to/from a PLC.

```powershell
node tests/native-cross-reference-acceptance.cjs --process-id 12345 --project-path 'C:\TestProjects\Acceptance\Acceptance.ap20'
```

Replace the sample process/path with the authorized disposable target. The common CPU, endpoint, memory address, timeout and output options are supported; `--help` describes them. The selected project and full access are checked before every write. There is no automatic retry or resume of a failed native operation.

The fixture contains two Bool tags, one Int constant, a UDT, a DB containing that UDT, and five SCL FCs. Only this run's uniquely named objects are created, changed or deleted. The native assertions are:

| Scenario | Expected evidence |
|---|---|
| Repeated reads/writes | Reader FC reads the global tag twice; Writer FC writes it twice and reads it once. Matching native target IDs, access kinds and occurrence counts are required. Both incoming UsedBy and outgoing Uses relationships are checked. |
| Local/global name distinction | LocalOnly FC has a local variable with the global tag's exact name. It must not be reported as a use of the global tag. |
| Unused tag | No native UsedBy locations for the separate unused tag. An empty native source collection is valid. |
| User constant | Native reference from the constant to the Reader FC. |
| Call chain | Top calls Caller, which calls Reader and Writer; native UsedBy/Call relationships identify the callers. Caller's outgoing Uses/Call relationships are also checked. |
| DB members | Native child sources retain Data with nested Flag and Count members. Flag has a Writer write; Count has a Reader read and Writer read/write. Outgoing block-to-DB relationships are also checked. Null member IDs are preserved. |
| UDT dependency | TypeInstance/Declaration identifies the DB from the UDT; InstanceType/Declaration identifies the UDT from the DB's Data member. |
| Freshness after edit | Writer is regenerated without global-tag uses, then explicitly compiled. Its tag references disappear while Reader's two locations remain. |
| Freshness after deletion | Top is deleted and the PLC explicitly compiled. Top disappears from Caller's references. |
| Cleanup | Caller, Reader, Writer, LocalOnly, DB, UDT and the populated table are deleted in dependency order; fresh inventories and old-ID reads confirm absence. Final compilation and project status are checked. |

Every call and native response is retained in `test-results/native-cross-reference-acceptance/<run>/report.json`; `report.md` summarizes steps. Responses preserve native Sources/Children/References/Locations rather than substituting a parsed source graph. The expected graph comes from the test fixture; the production MCP performs no parsing or inference.

Native SCL locations can be coarse: the two reads of the same tag returned two location entries with the identical `@<Reader> ▶ Program code` label. The test verifies that repeated entries survive; it does not require unique labels or invent source-line positions. The earlier LAD example returned an NW1 location.

Offline checks exercise the fixture declarations and the assertions themselves, including incorrect IDs, directions/access, duplicate locations, partial collections and stale references:

```powershell
node --test tests/native-cross-reference-acceptance.test.cjs
```

## Evidence

The scoped V20 fixture relationships passed across the original setup and a reviewed completion on 2026-09-23. The completion passed 27 steps, including fixture verification, freshness checks, cleanup and final compilation. Recorded responses preserve duplicate native SCL location labels; unique source-line positions are not inferred.

<a id="verification-status"></a>

The original dated record is preserved in [expanded native cross-reference runs](../reference/history/native-cross-reference-runs.md). See the [evidence index](evidence.md) for current coverage and remaining limits.
