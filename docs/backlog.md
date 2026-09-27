# Backlog

Work the user has agreed to but that is not done yet, with the decisions already made. The contract documents stay authoritative until an item is implemented and changes them. Remove an item when it is done; do not keep completed entries here.

Recorded 2026-09-27, after commit `b777e9d`.

## Order

The dashboard rewrite is the largest item and comes last. The others don't depend on it.

1. Small code cleanups.
2. Unify the native acceptance scripts.
3. Turn `project-rehaul.md` into a current tool reference without the rehaul framing.
4. Items that need a decision or live TIA evidence.
5. Dashboard rewrite with Datastar and server-sent events (SSE), in stages, using the `Hypermedia.Datastar` 0.1.0 package in `packages/`.

## 1. Small code cleanups

- `OpennessConnectionBackend.OpenProject`: `startedInstance` is always true whenever `portal != null`, so the `else portal.Dispose()` branch can never run. Remove the flag and the branch. Keep `CloseOwnedInstance`; `tests/architecture.test.cjs` pins its `GetCurrentProcess().Dispose()`.

## 2. Unify the native acceptance scripts

Agreed: one shared client and option handling, with no hard-coded process IDs or user-specific project paths.

- `tests/native-acceptance/technology-objects.cjs` defaults to `--process-id 77188` and a user-specific `FillTank.ap20` path. It also has its own `fetch` MCP client.
- `tests/native-acceptance/technology-object-catalogue-create.cjs` has its own `fetch` MCP client.
- In `runner.cjs`, the `WRITES` list is missing `create_technology_object`, `set_technology_object_parameters`, `create_group`, `delete_group` and `rename`. Uncertain-write tracking therefore misses them, which affects `group-scenarios.cjs`.

Target: every suite uses `runner.cjs` (`optionsFrom`, `createContext`, preflight, CPU discovery, report writing). `--process-id` and `--project-path` become required with no defaults, and every modifying tool counts as a write. Update `docs/native-acceptance.md`, the related suite docs and `tests/native-acceptance-runner.test.cjs`.

## 3. Documentation

The project is in a refinement stage (cleaning up, filling gaps, focusing), not a rehaul. Documentation of each tool (its signature, purpose, inputs, response, native behavior and limits) is useful and stays. What goes is the wording that describes a rehaul or an initial stage.

- Rename `project-rehaul.md` to a neutral name such as `read-tools.md`, and `rehaul-dashboard.md` to `dashboard.md`. Only rename the dashboard document here; its content is rewritten with the dashboard. Update the links in `AGENTS.md` (two places), `README.md` (two places), `docs/README.md`, `architecture.md`, `cross-references.md`, `get-block.md` (uses the `#get_block` anchor), `udt-discovery-read.md`, `tag-table-discovery-read.md`, `user-manual.md` and `what-is-tia-openness.md`.
- Replace stage wording with plain statements of current behavior. For example, "Filtering is outside the initial rehaul scope" becomes "`list_*` tools return the complete hierarchy, with no filtering or pagination". Other phrases to replace include "initial surface", "this rehaul" and "baseline".
- Fill the gap: there are no sections yet for `list_technology_objects`, `list_available_technology_objects` and `get_technology_object`.
- Check each tool section against the current code (the `McpBoundary` descriptions and schemas and the readers). Report conflicts to the user instead of silently choosing which side is right.
- Do the same pass over `write-operations.md` and `compile-delete-export.md`.
- `tools/tia-mcp-server.ps1` (line 267) still has an error message saying the server "starts the rehaul transition".

## 4. Needs a decision or live TIA evidence

- **Extra native reads.** Each property read is a call into the TIA process. The block inventory reads `Name`, `Number` and `ProgrammingLanguage` as separate typed reads per block, plus the identifier. Device items read `Name` twice and re-read typed values already returned by the bulk `GetAttributes` call. The candidate fix is one bulk read per object. It needs a read-only live V20 check first, because bulk values can come back as a different type than the typed property (enums arrive as a Siemens wrapper struct).
- **Duplicate code.**
  - The block, UDT and tag-table readers each walk the software units and group trees in their own copy.
  - Four separate `PathOf` walkers build paths.
  - `OpennessWrites` has both `Destination` and `TechnologyDestination`.
  - Eight request parsers in `WriteContracts.cs` repeat the `processId`, unknown-field and flag checks with slightly different messages.

  Merging the parsers changes error text that MCP callers see, so the user must decide whether that is acceptable. Merging the traversal needs a live check.

## 5. Dashboard rewrite (Datastar and SSE)

Goal: replace the polling browser dashboard with a server-driven HTML page. The server is the source of truth; the browser only renders what the server sends.

Decided by the user:

- Use [Datastar](https://data-star.dev/) on the client. The server sends HTML elements and signals over SSE; Datastar morphs them into the page, which preserves focus and typed input during updates. Bundle `datastar.js` in `Dashboard/wwwroot/` instead of loading it from a CDN, and record its version and license.
- Tool runs: the page posts to a dashboard route, and the server calls the same `McpBoundary` dispatch in-process and streams the result. There must be no second tool implementation. This replaces the current rule that the dashboard calls `/mcp` directly. Update `AGENTS.md`, `docs/README.md`, `architecture.md`, `dashboard.md`, `write-operations.md`, `user-manual.md`, and the dashboard-endpoint test in `tests/architecture.test.cjs` ("dashboard endpoints expose connection and inspection actions without parallel engineering reads").
- Run history lives in server memory only and is cleared on restart. This replaces the browser local-storage history (`persistRuns` in `dashboard.js`).
- The background monitor runs only while at least one dashboard event stream is open (see below).

### Server side: the Datastar SDK (`Hypermedia.Datastar`)

The official [.NET SDK](https://github.com/starfederation/datastar-dotnet) is not usable here: its core is F# on ASP.NET Core request/response types, and the C# layer adds ASP.NET dependency injection and model binding. A separate project implemented the language-neutral [Datastar SDK specification](https://github.com/starfederation/datastar/blob/e1fd0ef54310ee8b3819abccf36d3cfae5394bd1/sdk/ADR.md) (commit `e1fd0ef`) as `Hypermedia.Datastar` 0.1.0. The package is in `packages/Hypermedia.Datastar.0.1.0.nupkg`. It targets `netstandard2.0`, depends only on System.Text.Json 8.0.5, and was tested with datastar.js v1.0.4 (SHA-256 `727844adfc825ee651fb93c544a2a739986f9a21820a94524b35f0cac470cf91`; bundle that exact file into `Dashboard/wwwroot/`).

Its API, namespace `Hypermedia.Datastar`:

- `ServerSentEventGenerator` from an `HttpListenerContext` or a `Stream` (pass `leaveOpen: true` when the caller owns the stream).
- `StartAsync` sets the event-stream headers, `KeepAlive`, chunked sending and flushes. It is idempotent, and the first send also starts the response.
- `PatchElementsAsync(html, PatchElementsOptions)` with selector, `ElementPatchMode`, view transition, `ElementNamespace`, event id and retry; `RemoveElementsAsync(selector)`; `ExecuteScriptAsync`.
- `PatchSignalsAsync` for a JSON string or an object; `SendCommentAsync` for heartbeats.
- `DatastarRequest.ReadSignalsAsync` (a `JsonDocument` or `T`) and `IsDatastarRequest`. GET reads the `datastar` query parameter, other methods the body.
- A write to a disconnected client throws `ClientDisconnectedException`; invalid signals throw `InvalidSignalsException`. After any transport failure the generator is faulted: close it and create a new one. Only `CloseAsync`/`Dispose` closes the response.

Wiring it in, still to do: add a root `nuget.config` with `packages/` as a package source, and add a `PackageReference` to both `TiaOpennessMcpServer.csproj` and the offline harness project (the harness compiles the dashboard sources). Check that `Program.cs` assembly resolution, which exists for the Siemens assemblies, does not interfere with loading the library's DLL from the output folder. Update by adding a new `.nupkg` and bumping the version in both project files.

Behavior the dashboard code must respect:

- Start the stream with `Content-Type: text/event-stream`, `Cache-Control: no-cache`, keep-alive and chunked sending, then flush immediately.
- `Send` writes `event:`, optional `id:`, `retry:` only when not 1000 ms, one `data:` line per data line, and a blank line, then flushes. Serialize writes (for example with `SemaphoreSlim`) so events arrive in order.
- `PatchElements` (`datastar-patch-elements`): `selector`, `mode` only when not `outer`, `useViewTransition` only when true, `namespace` only when not `html`, then one `elements` line per HTML line. Removal uses mode `remove`.
- `PatchSignals` (`datastar-patch-signals`): `onlyIfMissing` only when true, then one `signals` line per JSON line.
- `ReadSignals`: for GET, the URL-encoded JSON in the `datastar` query parameter; for other methods, the JSON body. Invalid JSON is an error. Use the existing System.Text.Json.
- Skip `ExecuteScript` unless the dashboard needs it.
- Write through a `Stream`, so the SDK's own tests can check the output byte for byte against the specification's examples without `HttpListener`.
- Bundle the `datastar.js` release the SDK was tested against.

### Constraints to keep

- One process, one `HttpListener`, one STA scheduler. `HttpHost` already hands each request to `Task.Run`, so a long-lived stream does not block other requests. Send heartbeat comments, treat a failed write as a disconnect, and limit the number of open streams.
- Keep the `/api/dashboard/*` prefix and the loopback origin checks. Verify that Datastar requests can carry `X-Tia-Dashboard: 1` (its actions accept custom headers), or replace that check with an equivalent one.
- Native objects stay on the STA worker; rendering uses managed DTOs only. HTML is escaped on the server.
- Connect, disconnect and Open project stay user actions in the dashboard. No automatic reconnect.

### Current bugs the rewrite must fix

- A CPU dropdown appears only on `list_blocks`, `list_udts` and `list_tag_tables`. `list_technology_objects`, `list_available_technology_objects` and every other tool with `plcObjectId` should get one.
- Choosing a CPU is copied only to a hard-coded list of tools (`cpuTools()` / `setPlc` in `dashboard.js`). Forms such as `create_technology_object`, `create_group`, `delete_group` and `rename` don't follow it.
- Group pickers on technology-object and group forms show tag-table groups (`inventoryFor` falls back to `list_tag_tables`).
- Common cause: forms depend on hard-coded tool-name lists. Build every form from the published MCP input schemas instead.

### Stages

Each stage leaves a working dashboard and gets a short design check with the user before it is built.

1. **Server stream.** Wire in the `Hypermedia.Datastar` package (see above) and add one dashboard event stream, and run the monitor only while a stream is open. The current page keeps polling.
2. **Tool runs on the server.** Add the route that calls the `McpBoundary` dispatch, and keep run history in server memory.
3. **Datastar page.** Serve a new page that renders connections and tabs from the stream, next to the old page.
4. **Forms from schemas.** Build every form from the tool schemas; this fixes the bugs above.
5. **Remove the old dashboard.** Drop the old page, `dashboard.js` polling and local-storage history. Replace `tests/dashboard.test.cjs` (a fake-DOM test whose modifying-form test covers only the original write tools) with offline-harness tests for fragment rendering and SSE framing plus a small Node smoke test. Update the dashboard route list pinned in `tests/architecture.test.cjs`, rewrite `dashboard.md`, and review `user-manual.md`.

After each stage: build, run the offline harness and Node tests, and reload the managed server only when the user asks.

### Monitor only while a dashboard is connected

`EngineeringService.MonitorAsync` runs process discovery and `ConnectionRegistry.Monitor()` every 2 seconds, whether or not anyone is watching. MCP doesn't need this: `list_tia_processes` discovers on demand, and every project operation validates its attachment before and after it runs. The monitor only lets the dashboard notice early that TIA or a project closed. So run it only while at least one dashboard event stream is open. Check what `get_status` reports for a selected process whose TIA window has closed while no monitor is running; if it would still say connected, validate on demand in that status read. Remove the pause-monitoring control if it no longer has a purpose.

## Operational state

The managed server was started before commit `b777e9d` and is running an older build than the working tree. Reload it with `tools/tia-mcp-server.ps1` only when the user asks; afterwards each TIA process must be reconnected in the dashboard.
