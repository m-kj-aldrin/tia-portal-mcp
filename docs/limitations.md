# Critical native limitations

This document records investigation findings that constrain development. It keeps observed native behavior, reported observations and unresolved interpretations separate. It is not a record of individual runs. The supported target is TIA Portal V20.

## Project identity and lifetime

A path is insufficient to identify an attached project: the same path can be closed and reopened with a different native Project. A reported same-path reopen check rejected the retained context. The bridge therefore validates runtime identity, path and retained native project identity, captures the attachment before queueing and requires explicit reconnection after context loss.

Keep native objects on the STA and dispose only the retained `TiaPortal` attachment when detaching. `TiaPortalProcess.Dispose()` closes TIA. Broader lifecycle transitions and PID-reuse cases remain unverified; the normal native tool suite does not establish those transitions.

Implementation: [connection registry](../src/TiaOpennessMcpServer/Services/ConnectionRegistry.cs) and [native backend](../src/TiaOpennessMcpServer/Openness/OpennessConnectionBackend.cs).

## Source formats and protected content

Native export support depends on object kind, language, protection and chosen format. Native investigation found SCL FC SIMATIC SD export unavailable while other SD/SimaticML/external-source fixture workflows worked. Do not infer universal format support from one fixture or rewrite native documents to simulate a missing representation.

SIMATIC SD PartialSuccess is insufficient for the bridge's complete-source result. Explicit formats make one attempt; best-format fallback is a separate policy. Protection metadata does not prove access to protected internals, and returned text means only native-permitted content.

Secondary projects are a separate Openness read-only mechanism, outside the MCP surface. A secondary-project investigation could navigate objects but all tested GenerateSource/Export paths were refused. Navigation does not establish complete source access. Multi-project source workflows use primary projects in separately connected visible processes.

Implementation: [source exporter](../src/TiaOpennessMcpServer/Openness/OpennessSourceExporter.cs) and [source selection](../src/TiaOpennessMcpServer/Operations/BlockSourceReader.cs).

## Attribute representation and performance

Named/bulk GetAttributes can return native enums or representation wrappers where typed properties return strings. Printable equality is insufficient to prove equivalent managed JSON. Preserve native values through deliberate conversion, without exposing proxies or re-fetching all attributes through typed properties.

Small native experiments suggested a possible benefit from named block reads. Device attribute reuse varied, and retaining Device references did not demonstrate a reliable end-to-end gain. An approximate context-check timing suggested guards could contribute materially to latency, but production stages were not independently measured. These are investigation leads, not grounds to weaken identity checks or introduce a proxy cache.

Implementation: [block metadata](../src/TiaOpennessMcpServer/Operations/BlockMetadata.cs), [UDT metadata](../src/TiaOpennessMcpServer/Operations/UdtMetadata.cs) and [native detail readers](../src/TiaOpennessMcpServer/Openness/).

## Native identities, entries and cross-references

Entries/parameters receive IDs only when the native identifier provider supports them; null is valid and must not be replaced with an invented identity. An ID does not guarantee that the object exposes CrossReferenceService. Typed tag-table inspection excludes multilingual content; native XML export is a separate representation.

Cross-reference results preserve native Sources, Children, References, Locations, enums and paths. Repeated SCL location labels were observed and must not be treated as distinct source-line numbers or expanded into a derived graph.

Implementation: [tag-table detail reader](../src/TiaOpennessMcpServer/Openness/OpennessTagTableDetailReader.cs) and [cross-reference reader](../src/TiaOpennessMcpServer/Openness/OpennessCrossReferenceReader.cs).

## Technology-object catalogue

Catalogue rows describe documented CPU-family/firmware guidance, not verified support for every order number. Version cells may contain labels or multiple versions; creation requires one native system-library element/version pair that TIA accepts. Catalogue equality does not prove successful native creation.

Implementation/provenance: [catalogue data](../data/technology-object-catalogue.json), [catalogue adapter](../src/TiaOpennessMcpServer/Openness/OpennessTechnologyCatalogReader.cs) and [native writes](../src/TiaOpennessMcpServer/Openness/OpennessWrites.cs).

## Remaining verification limits

A passing native MCP suite verifies its controlled S7-1500 fixture, nested groups and write/readback expectations. Earlier broader fixture observations do not establish every native variant.

- Populated system-constant fields/IDs, protected or safety objects and unusual software-unit scopes remain unverified.
- Every source-format/object combination, instance-DB/OB replacement and translated resources remain outside the core workflow's guarantee.
- Forced mid-write failures, arbitrary partial-failure recovery and independent inspection for leaked native temporary sources remain unverified.
- Compiler diagnostics concern the current explicit invocation. They provide neither compiler history nor proof of PLC runtime behavior.
- Returning to zero devices does not restore the project's modified flag. The suite leaves saving/discarding unsaved changes to the user.
