# Backlog

Work agreed with the user but not yet implemented. Remove an item when it is done; the current contract documents remain authoritative.

Updated 2026-09-28 after the declarative Datastar dashboard migration.

## Native evidence or design decision

- **Extra native reads.** Each property read is a call into the TIA process. The block inventory reads `Name`, `Number` and `ProgrammingLanguage` as separate typed reads per block, plus the identifier. Device items read `Name` twice and re-read typed values already returned by the bulk `GetAttributes` call. The candidate fix is one bulk read per object. It needs a read-only live V20 check first, because bulk values can come back as a different type than the typed property (enums arrive as a Siemens wrapper struct).
- **Duplicate native traversal.** The block, UDT and tag-table readers walk software units and groups separately. Device, block, UDT, tag-table and technology-object detail readers each have a `PathOf` walker. `OpennessWrites` has both `Destination` and `TechnologyDestination`. Merging native traversal needs a live check.

## Dashboard selector semantics

Visible tool forms now come from the published MCP input schemas, with Datastar field binding and server-supplied inventory suggestions. Every published `plcObjectId` field shares the selected CPU signal. The MCP schemas still use generic names such as `objectId` and `groupObjectId`; they do not identify which inventory kind should supply suggestions. `DashboardToolForms.ListKind` and `InventoryTool` therefore retain an explicit dashboard mapping by tool name.

Decide whether to add selector-kind metadata to the published tool definitions or maintain that mapping as a tested dashboard hint when adding tools. Check group and technology-object coverage across create, delete and rename forms. Keep exact native ID entry available, and keep MCP definitions and dispatch authoritative; suggestions must not become a second engineering validation contract.
