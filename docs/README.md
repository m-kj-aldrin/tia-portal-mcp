# Current documentation

This is the entry point for the active project. Follow the current user request and [repository instructions](../AGENTS.md). Historical handoffs do not assign new work or override the current contract.

## Product and operation boundary

MCP is the primary interface to the TIA engineering operations. The dashboard extends it with user-controlled connections, tool testing and inspection. Dashboard design must follow the operation contracts; it must not define them.

Engineering behavior stays close to the native Openness operations. `write_blocks` and `write_udts` submit complete source documents to native generation/import. To update an object, a consumer reads its source, edits the document and writes it back with the intended native name and scope in a supported explicit format. There is no separate update API or planned create-only/update-only/member-patch abstraction. Native APIs remain responsible for replacement, accepted values and affected objects.

Connection guards, access profiles, document staging and result serialization are bridge responsibilities. Keep those responsibilities distinct from native engineering semantics.

TIA Portal **V20** is the supported target for this MCP and its native acceptance tests. Testing other TIA versions is not an acceptance requirement. Coverage is measured through engineering workflows and representative object structures; exhaustive testing of every PLC instruction or data type is not required to accept those verified engineering workflows. Native restrictions for special objects still apply.

Explicit PLC compilation returns diagnostics from that invocation. Native deletion targets a block, UDT or tag table by ID; tag-table XML export is a separate read from typed entry inspection. Saving projects and PLC upload/download are permanently outside MCP. Other writes do not invoke compilation automatically.

## Active references

| Document | Responsibility |
|---|---|
| [Repository README](../README.md) | Build, access profiles and managed startup |
| [Architecture](architecture.md) | Source map, dependencies, protocol, request flow and lifecycle ownership |
| [Read contracts](read-tools.md) | Fifteen read tools, shared connection rules and native response contracts |
| [Write contracts](write-operations.md) | Seventeen modifying tools, native API mapping, arguments, source formats and results |
| [Compile, deletion and tag-table export](compile-delete-export.md) | Native compile diagnostics, whole-object deletion, XML export and lifecycle-runner usage |
| [Verification evidence](evidence.md) | Scoped native results, user reports, local checks and remaining gaps |
| [Automated native acceptance](native-acceptance.md) | Real MCP calls and write/read-back assertions against a user-connected disposable project |
| [Native import matrix](native-import-matrix.md) | Creation and semantic replacement by source format, extension and object kind, with per-import evidence |
| [Expanded cross-reference acceptance](native-cross-reference-acceptance.md) | V20 fixture graph, multiple locations/access kinds, member and call relationships, and freshness after changes |
| [Block details](get-block.md), [UDTs](udt-discovery-read.md), [tag tables](tag-table-discovery-read.md), [cross-references](cross-references.md) | Native adapter behavior and links to scoped evidence |
| [Dashboard](dashboard.md) | Console, selection, history/logs and the stage 1 event-stream implementation |
| [Dashboard stage 1 design](dashboard-stage-1-design.md) | Approved scope for the event stream, monitoring subscriptions and local checks |
| [User manual](user-manual.md) | Using the console and tool arguments |
| [Openness overview](what-is-tia-openness.md) | The API's role in this bridge |
| [Backlog](backlog.md) | Agreed work not yet done, with the decisions already made |
| [Handoff 2026-09-27](handoff.md) | Snapshot of the working tree for the next session |

Full access currently publishes thirty-two tools; explicit read-only access publishes fifteen. This describes the implemented inventory, not a permanent limit on future native operations. New tools require an agreed native operation and contract, not a dashboard feature request. The [evidence index](evidence.md) distinguishes each acceptance suite's scenarios from the published tool count.

## Source responsibilities

Keep engineering operations and managed contracts, native Openness implementations, shared services, MCP endpoints, dashboard endpoints and dashboard assets separate. Core operations and services must not reference MCP or dashboard types. Both endpoint modules use one host, one HTTP listener, one connection registry and one STA scheduler. The dashboard tests engineering tools through `/mcp`; its connection and inspection endpoints use shared services.

The source is organized into `Operations/`, `Openness/`, `Services/`, `Diagnostics/`, `Mcp/`, `Dashboard/` and `Host/`. `Program.cs` registers assembly resolution and starts the composition root. Dashboard HTML, CSS and JavaScript live separately in `Dashboard/wwwroot/`; dashboard routes use `/api/dashboard/*`. The [architecture map](architecture.md) describes the boundaries and verification status. Tests protect contracts and native boundaries without requiring MCP to remain in a particular startup file.

Dashboard stage 1 is implemented in the working tree with `Hypermedia.Datastar` 0.1.0, a bounded managed event stream and service-owned monitoring subscriptions. The current page still polls, executes tools through `/mcp` and keeps browser run captures; stages 2–5 remain pending. The user reports starting the new build and seeing the event request; native TIA evidence is unchanged. See the [dashboard reference](dashboard.md) and [backlog](backlog.md#stages).

## Historical material

[Reference material](../reference/README.md) contains the untouched V1 snapshot and [completed phase records](../reference/history/README.md). Old publication holds, opt-in prototype startup, write-probe arming, fixed source locations and continuation prompts in those records are retired. Consult them only for a specific historical result. Dated test counts, process IDs and user reports never establish the current runtime state or broader native behavior.
