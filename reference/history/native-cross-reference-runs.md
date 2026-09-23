# Expanded native cross-reference runs

This record preserves the original dated observations and their limits. Commands, pending checks, process IDs, tool counts and reconnection instructions describe that checkpoint only. They do not assign current work or describe the running server. See the [current evidence index](../../docs/evidence.md) and [current documentation](../../docs/README.md).

## Verification status

Verified on 2026-09-23 through the real MCP endpoint against manually connected `Prototype-A-1`, TIA V20, process 28572, CPU `FbxBd++WREeJ3XSmOr1YXg==`.

- Initial run `2026-09-23T08-56-43-507Z_McpXR_4652743e9b3f`: ten setup/compile steps passed, then an incorrect test expectation required repeated SCL location labels to be distinct. The native result already contained every expected occurrence. No native write failed at this stop.
- Reviewed completion `2026-09-23T09-02-22-722Z_McpXR_4652743e9b3f_reviewed`: **27/27 steps passed**, including an additional read-only preflight that verified every retained fixture ID and unchanged exported source/table entries. The report links the original report by path and SHA-256. Creation and baseline compilation were not replayed; the original failed assertion report remains intact.
- All relationships in the table above passed, including outgoing Uses, incoming UsedBy, nested DB members, UDT declaration relationships and freshness after source replacement/deletion.
- All eight created top-level objects (six blocks, one UDT and one populated table) were deleted with inventory and old-ID absence checks. The two tags and user constant belonged to that table. Final compilation succeeded with **zero errors and one warning**. The same project remained connected and modified; no save or PLC upload/download occurred.
- Six offline assertion tests passed. Release build and 118/118 managed offline checks passed; the build emitted NU1900 because vulnerability-feed access was unavailable. The 40 architecture/dashboard checks also passed. No production-code change was needed for these expanded tests.

The reusable suite runs the complete fixture lifecycle in a new run. It intentionally has no automatic resume. The one-off reviewed continuation is retained beside the local reports, not exposed as a general retry option. A failed run retains recorded fixtures for inspection and does not claim rollback or automatically delete objects after a failed write.

This establishes the stated engineering cross-reference behavior on V20. It does not assert PLC runtime behavior or coverage of special safety/protected objects and software-unit scopes. Those are separate object scenarios if needed; testing other TIA versions or every PLC instruction is not required for this V20 baseline.
