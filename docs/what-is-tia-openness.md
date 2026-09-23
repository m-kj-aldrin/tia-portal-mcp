# TIA Portal Openness in this project

TIA Portal Openness is Siemens' engineering API used by this bridge to access TIA Portal processes and their projects. MCP exposes the bridge's engineering operations to clients; the dashboard provides connection management, testing and inspection.

This page describes the current repository, not the full set of capabilities available in every Siemens version. Start with [current documentation](README.md) for scope and contracts.

## Runtime and ownership

The executable targets .NET Framework 4.8, x64 and the installed TIA Portal V20 Public API. All native calls run through the shared `StaTaskScheduler`, with native objects retained on that worker. Managed requests and results cross the service boundary.

The user enables connections to visible TIA processes through the dashboard. MCP operations select an existing connection using `processId`; they do not attach or open a project implicitly. Reads and writes use the retained native project and its context guards.

## Implemented operations

| Area | Native behavior used by the bridge |
|---|---|
| Discovery and details | Native process diagnostics, typed compositions, object identifiers and readable attributes |
| Block and UDT source reads | `GenerateSource`, `ExportAsDocuments` or `Export`, depending on the selected supported format |
| Block and UDT source writes | Temporary external-source generation or native document/XML import |
| Tag tables and entries | Native table/tag/user-constant creation, writable entry attributes, entry deletion and table XML import/export |
| Whole-object deletion | Typed native `Delete()` for blocks, UDTs and tag tables |
| Explicit PLC compilation | `PlcSoftware.GetService<ICompilable>().Compile()` and the returned diagnostic tree |
| Cross-references | Native `CrossReferenceService.GetCrossReferences` |

Full access publishes twelve reads and twelve modifying operations. Explicit read-only access publishes the twelve reads and rejects mutation and compilation in the service. Saving projects and PLC upload/download are permanently outside MCP, and other online actions are not exposed. This is the user's bridge boundary, not a claim that the broader Siemens API lacks those capabilities. Compilation is an explicit tool, with current-invocation diagnostics and no implicit compile added to other writes; see [compile, deletion and export](compile-delete-export.md).

## Source formats and updating

- `external-source`: native textual source such as `.scl`, `.awl`, `.db` or `.udt`.
- `simatic-sd`: a `.s7dcl` document with an optional matching `.s7res`.
- `simatic-ml`: native XML.

To update a block or UDT, read its source, edit the complete document and write it back with the intended native name and scope. Use documents matching the chosen supported format. The bridge does not invent a separate update operation, rename declarations or convert formats merely because a different label was chosen. Native generation/import determines replacement and affected objects.

The client supplies document contents. The bridge stages owned temporary files for Siemens' file-based APIs and attempts cleanup. Writes can partially modify the project and are not saved automatically. See [read contracts](project-rehaul.md) and [write contracts](write-operations.md) for exact behavior and evidence limits.
