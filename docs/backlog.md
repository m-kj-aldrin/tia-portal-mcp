# Backlog

Work agreed with the user but not yet implemented. Remove an item when it is done; the current contract documents remain authoritative.

Updated 2026-09-28 after the declarative Datastar dashboard migration.

## Native refinements requiring live checks

- **Extra native reads.** Each property read is a call into the TIA process. The block inventory reads `Name`, `Number` and `ProgrammingLanguage` as separate typed reads per block, plus the identifier. Device items read `Name` twice and re-read typed values already returned by the bulk `GetAttributes` call. The candidate fix is one bulk read per object. It needs a read-only live V20 check first, because bulk values can come back as a different type than the typed property (enums arrive as a Siemens wrapper struct).
- **Duplicate native traversal.** The block, UDT and tag-table readers walk software units and groups separately. Device, block, UDT, tag-table and technology-object detail readers each have a `PathOf` walker. `OpennessWrites` has both `Destination` and `TechnologyDestination`. Merging native traversal needs a live check.
