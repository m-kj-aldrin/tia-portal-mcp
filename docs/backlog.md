# Backlog

Work agreed with the user but not yet implemented. Remove an item when it is done; the current contract documents remain authoritative.

Updated 2026-09-28 after the approved [dashboard stage 2 design](dashboard-stage-2-design.md).

## Native evidence or design decision

- **Extra native reads.** Each property read is a call into the TIA process. The block inventory reads `Name`, `Number` and `ProgrammingLanguage` as separate typed reads per block, plus the identifier. Device items read `Name` twice and re-read typed values already returned by the bulk `GetAttributes` call. The candidate fix is one bulk read per object. It needs a read-only live V20 check first, because bulk values can come back as a different type than the typed property (enums arrive as a Siemens wrapper struct).
- **Duplicate native traversal.** The block, UDT and tag-table readers walk software units and groups separately. Device, block, UDT, tag-table and technology-object detail readers each have a `PathOf` walker. `OpennessWrites` has both `Destination` and `TechnologyDestination`. Merging native traversal needs a live check.

## Dashboard schema forms

The server-driven Datastar page, HTML SSE updates, in-process tool action and memory-only run history are implemented in stage 2. The next dashboard stage needs its own short design check with the user before implementation.

Build **every tool form and selector from the published MCP input schemas**, replacing hard-coded tool-name lists. This must fix:

- A CPU selector currently appears only on `list_blocks`, `list_udts` and `list_tag_tables`; every tool requiring `plcObjectId` needs one.
- Choosing a CPU currently copies it only to hard-coded tools. Forms such as `create_technology_object`, `create_group`, `delete_group` and `rename` must follow the selected CPU where their schemas require one.
- Technology-object and group forms currently fall back to tag-table groups; group selectors must use the matching inventory and kind.

Keep MCP tool definitions and dispatch authoritative. The dashboard must not invent engineering operations, tool arguments or an independent validation contract.

After the stage: build, run the Siemens-free offline harness and Node checks. Reload the managed server only when the user asks.

## Operational state

The user reported starting the stage 1 build and seeing `/api/dashboard/events` in the browser Network tab. That was the user's observation of a request, not independent stream validation or managed-server identity. After explicit user approvals, stage 2 was built into the normal Release output and restarted through `tools/tia-mcp-server.ps1` to load an observer-loop fix and later an indented Result view. Authenticated HTTP checks verified the local assets and initial HTML SSE events; a fresh Chrome tab rendered the dashboard and a read-only `list_tia_processes` result with indentation. Later stream updates and native engineering behavior remain unverified. Each TIA process must be reconnected in the dashboard after the latest restart.
