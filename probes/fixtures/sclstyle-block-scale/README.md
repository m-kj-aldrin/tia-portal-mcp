# SclStyle block-scale fixture

This one native SCL source declares 48 uncalled, empty FCs named `ProbeScale_001` through `ProbeScale_048`. It is intended to increase the object count for block inventory and attribute-read experiments in the disposable `SclStyle.ap20` project. The functions do not reference inputs, outputs, tags or existing program blocks. They are synthetic measurement fixtures, not control logic.

The 2026-09-28 import targets a dedicated `Probe_BlockScale_20260928` program-block group through the existing MCP `write_blocks` external-source operation. The source file is kept here so the imported declarations are reviewable and reproducible. The TIA project is ignored by Git and must be saved manually by the user. No MCP save operation exists. Readback confirms creation; it does not establish compile or runtime behavior.
