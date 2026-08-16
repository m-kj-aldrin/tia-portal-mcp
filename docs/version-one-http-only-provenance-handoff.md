# Version one HTTP-only and provenance update handoff

Status: implementation handoff prepared on 2026-08-16. This document records
the user's final decisions and the work required in a new implementation task.
It is not itself an implementation or a replacement for the normative version
one read specification.

## 1. Read this first

The implementation task must first read:

1. `AGENTS.md`.
2. `docs/version-one-read-specification.md`.
3. This handoff.
4. `git status --short` and the complete existing diff.

The latest user decisions in this handoff supersede the current stdio-specific
instructions in `AGENTS.md`. Update those instructions as part of the same
change so that future tasks receive the new HTTP-only contract.

The worktree already contains intentional, uncommitted protected-object and V1
contract work. Preserve it. Do not reset, revert, overwrite, or silently omit
it. At handoff creation time the existing changes were:

- Modified: `docs/version-one-read-specification.md`.
- Modified: `src/TiaOpennessMcpServer/Models/V1Contracts.cs`.
- Modified: `src/TiaOpennessMcpServer/Program.cs`.
- Modified: `src/TiaOpennessMcpServer/Services/V1BridgeService.cs`.
- Modified: `src/TiaOpennessMcpServer/Services/V1TiaHelpers.cs`.
- Modified: `src/TiaOpennessMcpServer/dashboard.html`.
- Modified: `tests/TiaOpennessMcpServer.OfflineTests/Program.cs`.
- Modified:
  `tests/TiaOpennessMcpServer.OfflineTests/TiaOpennessMcpServer.OfflineTests.csproj`.
- Untracked and required by the current source:
  `src/TiaOpennessMcpServer/Utilities/McpArgumentPolicy.cs`.
- Untracked and required by the current source:
  `src/TiaOpennessMcpServer/Utilities/V1NativeFailurePolicy.cs`.
- Untracked and required by the current source:
  `src/TiaOpennessMcpServer/Utilities/V1ProtectionPolicy.cs`.

Recheck this list rather than assuming it is still exhaustive when the new task
starts.

## 2. Locked product decisions

### 2.1 One manually started local server

Version one is one user-started, long-running Windows dashboard/MCP process.
The user starts the executable before an AI client connects. Once the process
is available, the AI client initiates MCP communication and tool calls.

Version one does not include automatic startup by the client, a launcher, a
Windows service, a background service, or process supervision.

The same process serves:

- the dashboard;
- the Streamable HTTP MCP endpoint; and
- the existing REST/debugging surface.

The dashboard remains the user's setup, status, troubleshooting, and
client-to-MCP-to-TIA interaction view. Do not turn it into a separate semantic
project model.

### 2.2 Streamable HTTP is the only MCP transport

Remove stdio completely from the supported product, implementation,
documentation, dashboard, repository instructions, and tests.

The only V1 MCP endpoint is Streamable HTTP at the configured loopback URL,
normally `http://127.0.0.1:5000/mcp`. It must remain loopback-only. Do not add a
remote binding, network deployment mode, or transport authentication as part
of this change.

Remove rather than deprecate:

- the `--mcp-stdio` mode and argument handling;
- the stdin/stdout JSON-RPC loop and writer;
- stdio-specific logging behavior;
- stdio configuration examples;
- HTTP/stdio parity requirements; and
- dashboard claims that stdio is available.

Do not replace stdio with another client-launched transport. The removed flag
has no V1 compatibility contract and must not activate a hidden stdio path.

Multiple MCP clients may connect to the same HTTP server process. They share
that server's active-project state. V1 still permits zero or one active TIA
project per MCP process; it does not introduce per-client projects or sessions.
All TIA Openness access remains serialized through `StaTaskScheduler`.

### 2.3 Stable project-version field

Whenever a response contains a non-null V1 project identity,
`project.version` must be present in JSON. Its type is `string | null`.

- Read the value only from TIA Openness V20 `Project.Version`.
- Preserve a nonblank native value without inference.
- Normalize null, empty, or whitespace-only native values to JSON `null`.
- Do not omit the key merely because the project declares no value or the API
  read produces no value.
- Do not infer the project version from the `.ap20` extension, installed TIA
  product, assembly file version, registry, or filesystem metadata.

`project.version` is the TIA project metadata value. It is not the installed
TIA Portal version.

`tia.portalVersion` remains a separate nullable field derived from the native
installed-software diagnostics. In the current V20 environment it is expected
to identify V20 when TIA reports that value.

The application uses `JsonIgnoreCondition.WhenWritingNull` globally. Do not
change all response null behavior merely to retain `project.version`. Implement
and test a narrow contract-level serialization rule for this property, or an
equivalent narrowly scoped solution supported by the project's
`System.Text.Json` version.

### 2.4 Remove installed-product update completely

Remove the installed-product `update` field from the V1 public contract,
implementation, documentation, examples, dashboard, tool descriptions, and
tests.

TIA Openness V20 exposes the native installed product/version/options data used
here, but this implementation has no separate authoritative update value. V1
must not return a permanently empty field or infer update/service-pack/build
data from DLLs, paths, the registry, filenames, or free text.

Keep the native information that is actually available:

- installed product name;
- native product version when provided;
- product code if it is genuinely populated by an existing native source; and
- nested installed options.

Do not confuse unrelated code operations named "update" (for example an HMI
update route or the MSBuild `Update` item attribute) with the removed V1
installed-product field.

## 3. Required specification and documentation changes

Update the normative `docs/version-one-read-specification.md` so it states:

- V1 is a user-started, long-running local dashboard/MCP process.
- Streamable HTTP on loopback is the only MCP transport.
- An AI client initiates MCP requests after the user has started the process.
- Multiple clients may share the process, but the process has at most one
  active TIA project.
- Automatic client launch and process supervision are outside V1.
- `project.version` is always present when `project` is present, with a nullable
  native value and no inference.
- `tia.portalVersion` is a distinct installed-product fact.
- installed-product `update` does not exist in the V1 schema.

At minimum revise the runtime/lifecycle section, standard provenance envelope,
representative JSON, dashboard role, contract acceptance criteria, lifecycle
acceptance criteria, and final validation record. Remove every normative
HTTP/stdio parity or TIA update-level requirement.

Also align:

- `AGENTS.md`;
- `README.md`;
- `TIA_PORTAL_MCP_GUIDE.md`;
- `docs/user-manual.md`;
- `src/TiaOpennessMcpServer/dashboard.html`; and
- any other active documentation found by a repository-wide search.

Documentation must remain client-neutral. It may use the ChatGPT app as one
example of a Streamable HTTP client, but must not make the server dependent on
one client product.

Recommended user-facing startup sequence:

1. The user opens the intended TIA Portal project.
2. The user starts `TiaPortalDashboard.exe`.
3. The user points a compatible client at the displayed loopback `/mcp` URL.
4. The client calls `connect_to_tia_portal` and the user approves Siemens
   external access in the visible TIA UI if prompted.
5. The client uses the read tools while the dashboard displays status and call
   activity.

Do not imply that every client connection starts a new MCP or TIA attachment.
The attachment belongs to the shared, running server process.

## 4. Required implementation changes

### 4.1 `Program.cs`

- Remove `stdioMode` and `--mcp-stdio` detection.
- Restore normal logging without a stdio-specific console suppression branch.
- Remove the early stdio execution/return path.
- Remove `RunStdioAsync` and `WriteStdio` in full.
- Rename comments that describe the MCP handler as shared by HTTP and stdio.
- Keep one set of MCP definitions and one HTTP dispatch path.
- Preserve current MCP initialization, protocol negotiation, `tools/list`,
  runtime argument validation, access-profile filtering, error envelopes, and
  HTTP status behavior.
- Change the `get_status` description so it refers to native installed products
  and versions/options, not updates.

Do not change the V1 tool set or schemas merely because the transport is being
simplified.

### 4.2 V1 contracts and provenance

In `src/TiaOpennessMcpServer/Models/V1Contracts.cs`:

- Remove `V1InstalledProduct.Update`.
- Make the JSON contract retain `V1ProjectIdentity.Version` when null.
- Keep that property nullable; do not fabricate a sentinel such as `"unknown"`,
  `"V20"`, or an empty string.

In `src/TiaOpennessMcpServer/Services/V1ProvenanceFactory.cs`:

- Remove `Update = null` and its update-specific comment.
- Normalize the native project version to null when blank or whitespace.
- Keep installed product/version/options mapping native and recursive.
- Continue reading Siemens engineering objects only on the existing STA-bound
  call paths.

Review all response constructors and serializers that use these DTOs. The
project-version guarantee applies to status, success, and error provenance
whenever the `project` identity itself is present.

### 4.3 Dashboard

- Remove the stdio transport row and all stdio help/configuration text.
- Present one local Streamable HTTP MCP URL and the dashboard URL.
- Remove the installed-product Update column and fallback probes such as
  `updateVersion`, `updateLevel`, or `servicePack`.
- Continue to display the native product Version column and nested options.
- Display a null/blank project-version value as an honest unavailable marker,
  while preserving `version: null` in the API payload.
- Preserve call logs, timing, fallback/error visibility, project status, and
  read-only safety controls.

Do not expand this change into dashboard redesign or persistent client/session
tracking.

## 5. Test changes

Extend the Siemens-free offline tests where practical. At minimum cover:

1. A `V1ProjectIdentity` with `Version = null` serializes with
   `"version": null` under the same options used by the application.
2. A nonblank native project version is preserved.
3. Empty and whitespace-only native project versions normalize to null.
4. `V1InstalledProduct` serialization contains no `update` property.
5. Existing protected-object response/error serialization and argument-policy
   tests continue to pass.

If normalization cannot be tested without Siemens assemblies, extract only the
pure string-normalization rule into a small Siemens-free utility and link that
file into the offline test project. Do not move TIA object access out of the
STA-bound provenance path.

Add a repository/static contract check or an explicit review step confirming
that active source and user documentation contain no stdio mode. Exclude this
handoff/history document from that search because it intentionally describes
the removal. Do not assert that the entire repository contains no word
`update`; only the V1 installed-product/update-level contract is being removed.

The offline harness is a .NET 8 console test program and is run with
`dotnet run`, not `dotnet test`.

## 6. Verification sequence for the implementation task

### 6.1 Static and offline verification

1. Review the final diff for accidental loss of the existing protected-object
   work.
2. Run `git diff --check`.
3. Run the offline console tests with the repository's documented command.
4. Search active source and documentation for `stdio` and `mcp-stdio`, excluding
   this handoff, and resolve all remaining product claims or code paths.
5. Search the V1 installed-product/provenance code, schemas, examples, and
   dashboard for `update`, `updateVersion`, `updateLevel`, and `servicePack`.
   Distinguish unrelated write APIs and MSBuild syntax.
6. Build the .NET Framework 4.8 x64 application.

The user may have the Release executable running. Do not stop it or overwrite a
locked live output without the user's direction. Use a separate verification
output directory for the first build if needed. Before live acceptance, the
user must start the newly built binary; do not assume an isolated verification
build replaced the running server.

### 6.2 HTTP acceptance

Reuse the user-started server. Do not start or stop another instance.

Verify over the exact loopback `/mcp` endpoint:

- `initialize` and the agreed protocol version;
- `tools/list`, including descriptions and input schemas;
- `get_status`;
- the existing canonical V1 read flow needed to detect regressions; and
- that the dashboard still records the interaction.

For a response containing an active project, assert from the actual JSON text:

- the `project` object contains a `version` key;
- a project with no declared native value returns `"version": null`;
- `tia.portalVersion` remains separate and reports the native TIA product value
  when available; and
- no installed product or option contains an `update` key.

Confirm the listener is loopback-only. Do not test stdio. Do not save, write,
import, create, compile, clone, close, switch projects, or perform online
operations. If Siemens requests external-access approval, pause and ask the
user to approve it.

Compare `project.isModified` and the available project filesystem baseline
before and after the read flow. Report limitations honestly; do not attribute
normal TIA/index churn to the MCP without causal evidence.

## 7. Acceptance criteria

The update is complete when all of the following hold:

- The application exposes MCP through loopback Streamable HTTP only.
- No executable stdio loop or `--mcp-stdio` mode remains.
- V1 documentation states that the user starts the server before the client
  connects.
- Multiple clients share one server and its zero-or-one active project; no
  per-client project model was introduced.
- The dashboard shows the HTTP endpoint and interaction status without claiming
  stdio support.
- Existing `tools/list` names, descriptions, strict schemas, and canonical V1
  read behavior have not regressed, apart from the intentional `get_status`
  wording correction.
- Every serialized non-null project identity includes `version` as either the
  nonblank native string or JSON null.
- Installed TIA version and project version remain separate facts.
- The installed-product `update` field and all promises to report an update
  level are absent from the V1 contract and output.
- No update value is inferred from non-Openness sources.
- Offline tests and the Release build pass.
- HTTP smoke/live tests pass against the new user-started binary.
- The read-only validation leaves the project unchanged to the limits of the
  recorded evidence.

## 8. Explicitly out of scope

- Automatic MCP startup by an AI client.
- Reintroducing stdio as an adapter or compatibility mode.
- Windows service, tray autostart, watchdog, installer, or process manager.
- Remote/network MCP access, TLS, or authentication.
- Multiple simultaneous active TIA projects in one process.
- Per-client project selection or persistent MCP sessions.
- Persistent project models, caches, indexes, or semantic analysis in the MCP.
- Inferring TIA update, patch, service-pack, build, or project-version values.
- Any TIA project mutation or online operation.
- A general dashboard redesign.

## 9. Expected implementation report

The next task should report:

- files changed;
- the exact transport/runtime behavior after the change;
- the exact provenance schema behavior, including a raw JSON example with a
  nullable project version;
- confirmation that installed-product update was removed rather than inferred;
- offline test and build results;
- HTTP calls made and their actual results;
- live-test failures or untested boundaries;
- evidence that the user-started server was the new binary; and
- evidence and limitations for the claim that the TIA project remained
  unchanged.

Do not claim all of V1 complete solely because this focused update passes. Keep
the broader representative-project and lifecycle acceptance items separate and
state which of them remain open.
