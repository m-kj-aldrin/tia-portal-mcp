# Manual TIA Openness probes

This folder holds small, manually invoked experiments about native TIA Portal V20 behavior. A probe answers one question about the Siemens API; it does not test the MCP implementation or establish native acceptance for a published tool.

- `src/` does not reference this folder. Probes are not included in the application build, offline harness, or managed-server lifecycle.
- Each probe has its own README, project, and explicit target selection. No probe chooses an implicit TIA process or opens a project.
- Run probes only against a disposable, visible V20 project. Check each probe's stated native actions before running it. If TIA asks for external-access approval, the user decides in TIA.
- Keep raw output outside the repository. Record a scoped finding in `docs/evidence.md` only after interpreting it; do not treat a successful API call as proof that a product workflow works.
- Do not add shared probe infrastructure until repeated experiments show a concrete need.

- [`native-attribute-reads`](native-attribute-reads/README.md) compares individual property reads with native attribute reads. Its first manual result is recorded in [the evidence index](../docs/evidence.md#manual-native-attribute-probe).
- [`device-reference-lookup`](device-reference-lookup/README.md) measures repeated Device lookup and native metadata reads with or without a retained Device reference. Its first manual result is recorded in [the evidence index](../docs/evidence.md#manual-native-device-reference-probe).

Building a probe does not run it against TIA.
