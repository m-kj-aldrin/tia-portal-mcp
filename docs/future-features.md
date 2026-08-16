# Future features and research notes

This file is a backlog. Items here are planned or exploratory, not current server capabilities.

## 1. Methodology and skills layer

Add client-neutral, on-demand procedures for naming conventions, DB architecture, recipes, operating modes, alarms, and OEE. An MCP-capable agent should load only the guidance relevant to the current task.

The default workflow remains read-only. Procedures that use the full-access profile must name their project-changing operations and require explicit user authorization.

## 2. Lossless LAD parsing and progressive retrieval

Build the parser and retrieval tools described in [LAD agent architecture](lad-agent-architecture.md): block outline, ordered network list, individual network retrieval, and symbol/instruction/call search.

The existing internal LAD export service already supplies authoritative SIMATIC SD documents to canonical `read_plc_object`. It is not a separate V1 MCP tool. Parsing, semantic explanations, LAD editing, and LAD-to-SCL conversion are not implemented tools.

## 3. Two-process architecture

Keep a .NET Framework 4.8 worker for TIA Openness and move MCP protocol handling to a modern .NET process. The worker would own all Siemens objects on its STA thread and communicate with the MCP process over a narrow IPC contract.

This is high effort and becomes useful if modern MCP SDKs or stronger process isolation justify the split.

## 4. Explicit project selection

`connect_to_tia_portal` can already prefer a running instance by `projectPath`. A future startup option could make that selection before the first tool call or use a TIA process identifier. It must select an already open project without silently opening, saving, closing, or cloning it.

## 5. V21 support

TIA Portal V20 and V21 require different Siemens assembly bindings. If V21 support is needed, produce a separate V21 binary rather than pretending one executable is version-neutral.
