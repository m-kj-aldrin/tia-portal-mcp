# Current documentation

This is the entry point for the active project. Follow the current user request and [repository instructions](../AGENTS.md). Historical handoffs do not assign new work or override the current contract.

## Product and operation boundary

MCP is the primary interface to the TIA engineering operations. The dashboard extends it with user-controlled connections, tool testing and inspection. Dashboard design must follow the operation contracts; it must not define them.

Engineering behavior stays close to the native Openness operations. `write_blocks` and `write_udts` submit complete source documents to native generation/import. To update an object, a consumer reads its source, edits the document and writes it back with the intended native name and scope in a supported explicit format. There is no separate update API or planned create-only/update-only/member-patch abstraction. Native APIs remain responsible for replacement, accepted values and affected objects.

Connection guards, access profiles, document staging and result serialization are bridge responsibilities. Keep those responsibilities distinct from native engineering semantics.

Explicit PLC compilation returns diagnostics from that invocation. Native deletion targets a block, UDT or tag table by ID; tag-table XML export is a separate read from typed entry inspection. Saving projects and PLC upload/download are permanently outside MCP. Other writes do not invoke compilation automatically.

## Active references

| Document | Responsibility |
|---|---|
| [Repository README](../README.md) | Build, access profiles and managed startup |
| [Architecture](architecture.md) | Source map, dependencies, request flow and cleanup verification checklist |
| [Read contracts](project-rehaul.md) | Twelve read tools, shared connection rules and native response contracts |
| [Write contracts](write-operations.md) | Twelve modifying tools, native API mapping, arguments, source formats and write evidence |
| [Compile, deletion and tag-table export](compile-delete-export.md) | Five additional native tools, current compile diagnostics and scoped native readback/error/deletion evidence |
| [Automated native acceptance](native-acceptance.md) | Real MCP calls and write/read-back assertions against a user-connected disposable project |
| [Native import matrix](native-import-matrix.md) | Creation and semantic replacement by source format, extension and object kind, with per-import evidence |
| [Block details](get-block.md), [UDTs](udt-discovery-read.md), [tag tables](tag-table-discovery-read.md), [cross-references](cross-references.md) | Implemented reader behavior and dated, scoped evidence |
| [Dashboard](rehaul-dashboard.md) | Current console behavior and implementation evidence |
| [User manual](user-manual.md) | Using the console and tool arguments |
| [Openness overview](what-is-tia-openness.md) | The API's role in this bridge |

Full access currently publishes twenty-four tools; explicit read-only access publishes twelve. This describes the implemented inventory, not a permanent limit on future native operations. New tools require an agreed native operation and contract, not a dashboard feature request. The original native acceptance runner continues to exercise its nineteen-tool scenario set against the current publication; the new lifecycle suite covers the five added tools separately.

## Source responsibilities

Keep engineering operations and managed contracts, native Openness implementations, shared services, MCP endpoints, dashboard endpoints and dashboard assets separate. Core operations and services must not reference MCP or dashboard types. Both endpoint modules use one host, one HTTP listener, one connection registry and one STA scheduler. The dashboard tests engineering tools through `/mcp`; its connection and inspection endpoints use shared services.

The source is organized into `Operations/`, `Openness/`, `Services/`, `Diagnostics/`, `Mcp/`, `Dashboard/` and `Host/`. `Program.cs` registers assembly resolution and starts the composition root. Dashboard HTML, CSS and JavaScript live separately in `Dashboard/wwwroot/`; dashboard routes use `/api/dashboard/*`. The [architecture map](architecture.md) describes the boundaries and verification status. Tests protect contracts and native boundaries without requiring MCP to remain in a particular startup file.

## Historical material

[Reference material](../reference/README.md) contains the untouched V1 snapshot and [completed phase records](../reference/history/README.md). Old publication holds, opt-in prototype startup, write-probe arming, fixed source locations and continuation prompts in those records are retired. Consult them only for a specific historical result. Dated test counts, process IDs and user reports never establish the current runtime state or broader native behavior.
