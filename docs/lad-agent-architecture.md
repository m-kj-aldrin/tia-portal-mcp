# LAD agent architecture

## Purpose

Give an AI agent a compact, reliable view of Ladder logic while preserving the exported Siemens source as the authority. Reading comes first; editing is deferred until parsing and round-trip validation are dependable.

## Authoritative LAD source

For a pure LAD block in TIA Portal V20, the preferred representation is SIMATIC SD exported with `PlcBlock.ExportAsDocuments()`:

- `.s7dcl` contains the readable block and LAD program text.
- `.s7res` contains available resource and comment data.
- SimaticML XML remains available through `read_block` for compatibility and traceability.

`read_lad_source(device, block)` is the first read-only vertical slice. It accepts exactly `device` and `block`, verifies that the block is pure LAD and not know-how protected, exports into a unique temporary directory, reads the generated documents, returns their contents to the MCP client, and removes the temporary directory on a best-effort basis. It never imports, compiles, saves, or otherwise changes the TIA project.

The result contains block name, type, number, language, `sourceFormat: "simatic-sd"`, export state, generated file names, one `.s7dcl` source document, zero or more `.s7res` resource documents, and warnings. Missing source, failed or partial export, mixed or unsupported language, and know-how protection are explicit errors rather than incomplete source presented as authoritative.

The tool is part of the default read-only MCP profile. There is no implemented LAD-to-SCL converter; a client-generated explanation or translation is derived output and is never authoritative project logic.

## Lossless internal model

The next stage parses the returned `.s7dcl` without discarding its original text:

- `LadBlockDocument`: metadata, interface, ordered `networks`, original source, parser warnings, and eventually a source hash and parser version.
- `LadNetwork`: index, title, comment, ordered rungs, symbols used, called blocks, warnings, and original source span.
- `LadRung`: starting wire or power rail, ordered elements, ending wire, and original source.

Network, rung, element, branch, and wire order must be preserved. Known instructions can use semantic node types; an unknown instruction remains a generic node containing its name, arguments, source span, and original text. Branch topology must never be reduced to a flat instruction list.

Raw `.s7dcl` source and `.s7res` resources remain immutable authoritative inputs. Symbols, calls, explanations, cross-references, and search indexes are derived data that can be rebuilt; they must be stored separately and tied to the future source hash and parser version.

## Progressive MCP retrieval

After the lossless parser is validated, add stable, compact tools for:

1. Block outline and interface.
2. Ordered network list.
3. One network, including its rungs and topology.
4. Symbol, instruction, and called-block search.
5. Raw authoritative source retrieval.

Do not return the complete block when one network or a search result is sufficient. Derived explanations may cite source spans, but they are never stored as authoritative program logic.

## Editing gate

Editing remains out of scope until representative LAD blocks parse losslessly, export-to-model-to-source round trips are stable, unknown instructions survive unchanged, and re-import validation can be performed in an explicitly authorized disposable project.
