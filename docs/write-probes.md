# Write probes — pending native results

The dashboard Write probes panel posts to `/api/prototype/write-probe`. It is not an MCP tool. Discovery and dispatch stay the eleven read-only tools, and `writeToolsAvailable` stays false. These probes exist so a disposable project can show what TIA Portal V20 actually does. The results below are not filled in from API summaries.

The server does not call `Project.Save`, compile, or go online. `saved` is always false. When Openness exposes `ProjectBase.IsModified`, the result includes that native flag. A cleanup failure is `temporaryCleanup` and the result is not complete.

## Arming

Arming is in memory for one process, project path, and connection ticket. Type the connected project file name exactly and confirm that the project is disposable. A different file name, a missing confirmation, a path change, or a new connection disarms the session. Re-arming the same project keeps the objects this session created. Disarming stops further writes and keeps that created set until the path or process changes.

Every probe uses the shared STA worker, the ticket captured before the call is queued, and the existing context checks. Context loss discards the result and does not adopt another project.

## Actions

- Create copy reads one existing block or UDT with the current exporter, then creates a new object in the PLC root or a chosen existing group. **Root** uses the parameterless generate or import call. A folder from the block, UDT, or tag-table list is sent as its native id, or as its inventory path when Openness does not identify the folder. The source object is only read.
- Replace runs again onto an object this same probe session created. It does not replace the fixture used as the source. `GenerateBlocksFromSource(GenerateBlockOption.None)` can delete blocks it generated when generation fails, so the replace target is only the session-created copy.
- Create table, add tag, set tag attribute, and delete probe tag use the typed tag-table API on a table this session created. System constants are left unchanged.
- Import probe table as SimaticML is a separate action. It runs only after typed create or delete on that probe table has returned a native error.

`best` uses only the first preferred format: external source for SCL, STL, DB, and UDT; SIMATIC SD for LAD; SimaticML for other block languages. It does not retry another format. Choose `simatic-ml` explicitly for the fallback probe. LAD create uses `ImportDocumentOptions.None` under the new name. Replace of a probe-created LAD or SimaticML object uses `Override`. External-source generation has no override flag; whether a second generate with the same name replaces the probe object is one of the native questions.

The server substitutes the new name only when the exported text contains the current quoted name once, in exactly one document. Zero matches, several matches in one document, or the same quoted name in more than one document return `ambiguousName` and do not generate. A LAD export that puts the block name in both `.s7dcl` and `.s7res` is that `ambiguousName` outcome. This substitution is probe scaffolding.

Temporary files stay under an owned `tia-write-` directory and are deleted after the call, including the external source created for generation.

## Checks already run without TIA

- Release build against the installed V20 API: zero warnings.
- Offline harness: 89/89. The write-probe groups cover arming refusal, same-path re-arm, path-change disarm, ambiguous-name refusal, session-created replace and tag-import guards, cleanup failure, and `saved` remaining false. A registry case also rejects an unarmed call, a foreign replace, a stale ticket, and context loss during the probe.
- Dashboard script: 15/15. The panel is separate from the eleven tool forms and stays disabled until the disposable project is armed.
- Boundary: 9/9. MCP dispatch still has no write tool, and the probe does not save, compile, or close.
- The managed server was reloaded from that Release build. Passive status reported `implementationPhase` `rehaul-mcp-read-only`, `mcpPublication` `eleven-read-only-tools`, and `writeToolsAvailable` false.

Those checks do not establish native create, replace, import, or tag behavior.

## Disposable-project checklist

Reconnect one disposable project in the dashboard, arm it with its file name, and leave it unsaved. Compare the new objects in TIA. Do not treat an API description as a result.

- [ ] SCL create, then same-name generate onto that probe-created block.
- [ ] DB create.
- [ ] UDT create.
- [ ] LAD document create, then `Override` on that probe-created block.
- [ ] One explicit SimaticML import of a probe-created block.
- [ ] Tag create, attribute change, and delete on a probe-created table.
- [ ] Whole-table SimaticML import only if typed create or delete fails.

Record the native ids, errors, cleanup, and modified flag from those runs here after they happen. Whether external-source generation replaces an existing name, and whether typed tag create and delete succeed, stay open until then.
