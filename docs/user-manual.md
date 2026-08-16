# TIA Portal Dashboard — User Manual

## Overview

The TIA Portal Dashboard has two tabs at the top of the window:

| Tab | What it does |
|---|---|
| **Operations** | Shows status, project provenance, setup/safety guidance, and the manual read-tool runner |
| **MCP diagnostics & logs** | Shows the Streamable HTTP endpoint, live advertised tool definitions, and recent MCP calls |

Both tabs use the same server process and TIA Portal Openness connection. The Operations runner is intentionally read-only. Project-changing MCP and REST operations require the full-access profile and separate explicit user authorization.

---

## Getting started

1. Open your project in **TIA Portal V20**.
2. Launch `src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe`.
3. The dashboard opens in a native Windows window.
4. For manual use, connect from the header or **Status** view. For an MCP client, follow the **MCP diagnostics & logs** startup sequence below.

> TIA Portal can show an Openness access approval dialog for each new connecting process. Approve it when you intentionally start a connection.

---

## Operations tab

The Operations tab is the human-facing operational console. It does not perform behavioral interpretation or maintain a separate project model.

### Sidebar

- **Status** — shows the active project, modified state, access profile, project provenance, and native installed-product versions/options. It also links to refresh and manual tools.
- **Manual read tools** — presents exactly the eight canonical V1 connection, discovery, and read forms. Select a tool, fill in the declared arguments, and run it; the JSON result and fallback evidence appear below.
- **Setup & safety** — explains exact project selection, visible-UI authentication, read-evidence boundaries, and the dashboard and loopback MCP URLs.

The header accepts an optional exact project path. With no active project, that path attaches to an exact open match or visibly opens the compatible project. Without a path, exactly one suitable open project is required. Multiple candidates return an ambiguity error, and a different active project is never switched implicitly.

There is no LAD-to-SCL converter. `read_plc_object` returns the selected complete native representation and its fallback evidence; interpretation remains the calling agent's responsibility.

---

## MCP diagnostics & logs tab

This tab shows the MCP (Model Context Protocol) surface used by connected clients. Switch to it to copy the endpoint, inspect the live tool contract, and monitor calls.

### What you see

- **Streamable HTTP endpoint** — the local loopback `/mcp` URL and active profile
- **Live tool definitions** — tools and descriptions advertised for the active access profile
- **MCP call log** — recent tool calls with timestamp, outcome, duration, fallback summary, and error text

### Access profiles

The default server profile advertises and accepts exactly the eight canonical V1 MCP tools and rejects mutating REST/dashboard actions. It is the recommended choice for the ChatGPT app and other agents.

To expose experimental project-changing MCP tools, dashboard controls, and REST routes outside V1, start the server with `TIA_MCP_ACCESS=full` and reconnect the client. Full access is an availability setting, not permission for an agent to make an unrequested change.

`clone_project` is never advertised and direct calls are rejected. Its legacy implementation is also unsupported for a project attached from the user's running TIA Portal instance.

### Connecting a client

1. Open the intended project in TIA Portal V20.
2. Start the dashboard app yourself and leave it running.
3. Add the displayed loopback `/mcp` URL, normally `http://127.0.0.1:5000/mcp`, to a compatible Streamable HTTP client. The ChatGPT app is one example; exact setup labels vary between clients.
4. From the client, call `connect_to_tia_portal` and approve Siemens external access in the visible TIA UI if prompted.
5. Use the read tools while **MCP diagnostics & logs** displays definitions and call activity.

Port `5000` is the default. If it is already occupied, set `TIA_MCP_PORT` to an available port before starting the app and use that same port in the MCP URL.

The user-started app is the single shared server process. Multiple compatible clients can connect to it, but they share its zero-or-one active TIA project. A client connection or disconnect does not create or dispose a separate TIA attachment. Automatic client launch, service operation, and process supervision are outside version one.

### Example read-only requests

- *"List the PLC-object hierarchy on PLC_1."*
- *"Find the LAD blocks in the Controls group."*
- *"Read a pure LAD block using its native SIMATIC SD representation."*
- *"Show the native uses and usedBy references for Conveyor_Control."*

An agent can call the corresponding read-only tools automatically. Project-changing work requires the full profile and separate explicit user authorization.

---

## Tool reference

The default read-only V1 MCP surface and the Operations manual runner advertise and accept exactly the eight tools below. Every input schema sets `additionalProperties: false`; unknown fields, including password or credential fields, are rejected as invalid requests. Experimental full-access operations are outside the V1 specification and acceptance and always require separate explicit user authorization.

### Connection tools

**connect_to_tia_portal** · `[projectPath]`
Reuses the active project; otherwise it attaches to one exact open-project match or visibly opens a supplied compatible path. Without a path, exactly one suitable open project is required. A different active project returns a conflict, and multiple candidates return ambiguity. On success it returns V1 `provenance`, `connected`, and the connection `action`.

**get_status** · no arguments
Returns the current connection state, active access profile, write-enabled flag, and, if connected, V1 provenance and project details. Whenever the project identity is present, its JSON contains `version` as the nonblank native `Project.Version` string or `null`; blank values are not guessed. `tia.portalVersion` is a separate nullable installed-software fact. The native installed-product name `Totally Integrated Automation Portal` supplies that value from its native version. Installed products retain native names, versions, genuinely populated product codes, and nested options, with no inferred update/patch/service-pack/build field.

---

### Canonical V1 discovery and read tools

**list_devices** · no arguments
Returns V1 provenance, authority/completeness, and the project devices with explicit PLC software identities. Non-PLC devices are navigation metadata only.

**list_plc_objects** · `plc`
Returns the selected PLC's live block, UDT/type, tag-table, and group hierarchy with Siemens identities and three-state protection/content-availability metadata.

**find_plc_objects** · `plc`, `[query]`, `[type]`, `[language]`, `[group]`
Searches live object, tag, and constant metadata without creating an index or searching inside source content.

**read_plc_object** · `plc`, at least one of `objectId` / `path` / `name`, `[type]`, `[format]`
Reads one resolved PLC object. `objectId` takes precedence when present; `type` only qualifies path/name selection, which must resolve uniquely. `format=best` records ordered native SIMATIC SD, applicable raw SCL, and SimaticML attempts; an explicit format is strict. Success returns provenance, protection, one complete native representation, exact-content checksums, content scope, and the attempt trail.

**get_tag_table_entries** · `plc`, at least one of `objectId` / `path` / `table`
Returns a selected-field direct Openness view grouped into `tags`, `userConstants`, and `systemConstants`, with standard provenance. `objectId` takes precedence; path/table selection must resolve uniquely.

**get_cross_references** · `plc`, at least one of `objectId` / `path` / `name`, `[type]`
Queries TIA's native cross-reference service on demand and returns protected-aware `uses` and `usedBy` relationships without compilation, source parsing, or a stored call graph. `objectId` takes precedence; `type` only qualifies path/name selection, which must resolve uniquely.

---

## Tips

- **Stay read-only by default**: Do inspection and source retrieval before considering a full-access session.
- **Prefer stable selectors**: Use the Siemens object identity returned by discovery; convenience names and paths must resolve uniquely.
- **Choose formats deliberately**: Use `format=best` for an evidence-preserving fallback trail or an explicit format when a strict representation is required.
- **App must stay open**: The MCP server runs inside the app process. If you exit the dashboard from the system tray, HTTP clients lose the connection.
- **Shared attachment**: Multiple clients see the same active project. Disconnecting one client does not detach the running server from TIA.
- **Unavailable project version**: The dashboard shows an honest unavailable marker when TIA reports no project version; the API still serializes `"version": null`.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| "Security error — not a member of Siemens TIA Openness group" | Add your Windows account to the `Siemens TIA Openness` local group (Computer Management → Local Users and Groups → Groups) and sign out/in |
| "No running TIA Portal process found" | TIA Portal must be open with a project loaded before clicking Connect |
| "Inconsistent blocks cannot be exported" | Press Ctrl+B in TIA Portal to compile everything, then retry |
| My changes don't appear after restarting the app | Rebuild the project in Release and launch the executable from this checkout's `bin\Release\net48` folder |
| A project-changing tool is missing | The MCP server is read-only by default. Set `TIA_MCP_ACCESS=full` in the server environment and restart only for an intended full-access session |
| Live call log is empty | Run an MCP tool call. Calls from both compatible clients and the Operations manual runner use `/mcp` and appear in the log; merely opening the tab does not create a tool-call entry |
