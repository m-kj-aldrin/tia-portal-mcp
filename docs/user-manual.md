# TIA Portal Dashboard — User Manual

## Overview

The TIA Portal Dashboard has two operating modes, accessible as tabs at the top of the window:

| Tab | Who uses it | What it does |
|-----|-------------|--------------|
| **User Control** | You (human) | Run tools manually — fill in fields and click Run |
| **Agent Control** | Any MCP-compatible client, including the ChatGPT app | Shows the MCP endpoint, advertised tools, and call log |

Both tabs use the same TIA Portal Openness connection and are read-only by default. Project-changing MCP tools, dashboard controls, and REST routes require the full-access profile.

---

## Getting started

1. Open your project in **TIA Portal V20**.
2. Launch `src\TiaOpennessMcpServer\bin\Release\net48\TiaPortalDashboard.exe`.
3. The dashboard opens in a native Windows window.
4. Click **Connect to TIA Portal** (top of the left sidebar) — the status bar turns green and shows your project name.

> TIA Portal can show an Openness access approval dialog for each new connecting process. Approve it when you intentionally start a connection.

---

## User Control tab

The User Control tab is for manual, human-driven work without an external MCP client.

### Sidebar

The left sidebar has three sections:

**Quick Actions** — one-click buttons at the top:
- **Connect** — attach to the running TIA Portal process
- **Save** — save the TIA Portal project (full-access profile only)
- **Status** — poll the current connection and project info
- **Devices** — list all PLCs and HMIs in the project

**Tree** — shows devices and their contents once connected. Click a device to expand it; click **Blocks** or **Tag Tables** to load them into the main panel.

**Tools** — a categorised list of all available tools. Clicking a tool name opens it in the main panel (same as the Run Tool view).

### Run Tool

Click **Run Tool** in the sidebar (or any tool name in the Tools section) to open the tool runner. Select a tool from the chip grid, fill in any required parameters, and click **▶ Run Tool**. The JSON response appears below.

**Available manual dashboard tools by category:**

| Category | Tools |
|----------|-------|
| Connection | connect_to_tia_portal, get_status |
| Hardware | list_devices |
| Blocks | Read-only: list_blocks, read_block, analyze_block. Full: write_block_scl, import_block_xml, compile_block, create_block, create_instance_db |
| Tags | Read-only: list_tag_tables, get_tags. Full: import_tag_table, batch_rename_tags |
| Analysis | analyze_scl |
| Project | Read-only: get_option_packages, get_project_signature. Full: save_project. `clone_project` is quarantined |

`read_scl_source` and `read_lad_source` are MCP-only and do not add controls to the manual dashboard tool runner. Mutating manual tools are hidden or blocked in the read-only profile.

### Docs & API Reference

Click **Docs** in the sidebar to access built-in reference material:

- **Quick Start** — connection and first steps
- **Capabilities** — separates implemented read-only core, opt-in full-access operations, and roadmap items; the live MCP tool list remains authoritative
- **TIA Openness API** — PlcBlock class hierarchy, attribute reference, service table, namespace list
- **Troubleshooting** — common errors and fixes
- **MCP Setup** — endpoint details for connecting an MCP client

There is no dedicated LAD-to-SCL converter. For LAD, `read_lad_source` is authoritative; parsing and progressive network retrieval remain planned work.

---

## Agent Control tab

The Agent Control tab shows the MCP (Model Context Protocol) server used by connected clients. Switch to it to see live status and monitor MCP calls.

### What you see

- **Capabilities** — summary of implemented core, full-access, and roadmap capabilities
- **MCP Server** — connection URL and transport information
- **Available tools** — tools advertised for the active access profile
- **Live call log** — recent MCP calls with timestamp and success/failure

### Access profiles

The default server profile advertises only inspection and analysis tools and rejects mutating REST/dashboard actions. It is the recommended choice for the ChatGPT app and other agents.

To expose implemented project-changing MCP tools, dashboard controls, and REST routes, start the server with `TIA_MCP_ACCESS=full` and reconnect the client. Full access is an availability setting, not permission for an agent to make an unrequested change.

`clone_project` is never advertised and direct calls are rejected. Its legacy implementation is also unsupported for a project attached from the user's running TIA Portal instance.

### Connecting a client

For Streamable HTTP, start the app normally and add `http://127.0.0.1:5000/mcp` to a compatible client. The ChatGPT app supports this endpoint. Exact setup labels vary between clients.

Port `5000` is the default. If it is already occupied, set `TIA_MCP_PORT` to an available port before starting the app and use that same port in the MCP URL.

For stdio, configure a compatible client to launch:

```text
C:\path\to\TiaPortalDashboard.exe --mcp-stdio
```

Pass `TIA_MCP_ACCESS=full` in that child process environment only for an explicitly intended full-access session.

### Example read-only requests

- *"List all FBs on PLC_1."*
- *"Read a pure LAD block using its authoritative SIMATIC SD source."*
- *"Read Conveyor_Control and explain what the exported source shows."*
- *"Show the project signature and identify which blocks call for closer inspection."*

An agent can call the corresponding read-only tools automatically. Project-changing work requires the full profile and separate explicit user authorization.

---

## Tool reference

Unmarked tools are available in the default read-only profile. Tools marked **full access** require `TIA_MCP_ACCESS=full` over MCP, REST, and the manual dashboard.

### Connection tools

**connect_to_tia_portal**
Attaches to the running TIA Portal V20 process. TIA Portal must be open with a project loaded. Returns the project name and path on success.

**get_status**
Returns the current connection state, active access profile, write-enabled flag, and, if connected, project details. Use this to confirm both the connection and safety profile.

---

### Hardware tools

**list_devices**
Returns all devices in the open project — PLCs, HMIs, drives. Each entry has a name, type, and type identifier. Use this to get the device name you need for block and tag operations.

---

### Block tools

**list_blocks** · `device`
Lists every block (OB, FB, FC, DB) on a device, including block number, language, and modification date. Searches recursively through all subgroups.

**read_block** · `device`, `block`
Uses the compatibility SimaticML export path and returns extracted SCL or raw XML when that export succeeds, plus language, type, number, author, and modification date. Its established response is unchanged; an inconsistent block may instead contain the existing export-error text.

**read_scl_source** · `device`, `block` · MCP only
Generates the complete authoritative raw source for one unprotected, pure SCL block by using TIA Portal's external-source API without dependencies. It returns block metadata, `sourceFormat: "scl"`, the generated filename, complete `sourceCode`, and cleanup warnings. Non-SCL and know-how-protected blocks are rejected explicitly. The temporary source is never imported, compiled, or saved into the project.

**read_lad_source** · `device`, `block` · MCP only
Exports a pure LAD block read-only in TIA Portal V20's SIMATIC SD format. It returns the complete `.s7dcl` program text, available `.s7res` resource/comment contents, block and export metadata, generated file names, and warnings. It reports a clear error for non-LAD, mixed or incomplete exports, know-how protection, or unavailable document export. Use this in preference to interpreting raw SimaticML when an agent needs to reason about LAD logic.

**write_block_scl** · `device`, `block`, `source` · **full access**
Overwrites the SCL source of a block. Exports the block XML, patches the source section, and reimports. Always call **compile_block** afterwards to check for errors.

**import_block_xml** · `device`, `block`, `content` · **full access**
Imports raw SimaticML XML into a block. Use this for LAD, FBD, STL, and GRAPH blocks, or when you have a complete XML to write. The block is replaced by the import (Override mode).

**compile_block** · `device`, `block` · **full access**
Compiles a block using the TIA Openness compiler service. Returns the compiler state, error count, warning count, and all compiler messages with line numbers.

**analyze_block** · `device`, `block`
Runs static SCL analysis on a block without compiling it. Checks for common issues: unbalanced `IF`/`END_IF`, nested control structures, variables declared but unused, and more. Returns a list of findings with severity and line numbers.

**create_block** · `device`, `name`, `type`, `sourceCode`, `[number]` · **full access**
Creates a new block (FB, FC, OB, or GlobalDB) from SCL source. Generates a SimaticML XML skeleton, imports it, and returns the new block info. Block type must be one of: `FB`, `FC`, `OB`, `GlobalDB`.

**create_instance_db** · `device`, `name`, `instanceOfName`, `[number]` · **full access**
Creates a new Instance DB linked to an FB. Generates the SimaticML XML with the correct `InstanceOfName` attribute and imports it. If `number` is omitted, TIA Portal assigns one automatically.

---

### Tag tools

**list_tag_tables** · `device`
Lists all tag tables on a device with their names and tag counts. Searches recursively through all subgroups.

**get_tags** · `device`, `table`
Returns all tags in a tag table — name, data type, logical address, accessibility flags, and comment.

**import_tag_table** · `device`, `content` · **full access**
Imports a complete tag table from SimaticML XML content. Creates a new table or replaces an existing one (Override mode). The XML must follow the `SW.Tags.PlcTagTable` SimaticML format.

**batch_rename_tags** · `device`, `table`, `renames` · **full access**
Renames multiple tags in a single atomic operation. Reads the current table, applies the rename map, generates new XML, and reimports. The `renames` parameter is a JSON array of `{"from": "OldName", "to": "NewName"}` pairs.

---

### Analysis tools

**analyze_scl** · `source`, `[blockName]`, `[blockType]`
Runs static SCL analysis on any SCL text without needing an open block. Useful for checking code before writing it to TIA Portal.

---

### Project tools

**save_project** · **full access**
Saves the currently open TIA Portal project. Equivalent to pressing Ctrl+S in TIA Portal.

**clone_project** · `name`, `path` · **quarantined**
The server does not advertise this tool and rejects direct calls. Legacy clone code exports, saves, closes, creates, and imports projects; it is unsupported for a project attached from the user's running TIA Portal instance.

**get_option_packages**
Lists all option packages and used products referenced by the project — for example StartDrive, Safety, or TIA Portal Comfort Panels. Useful for auditing what licenses a project requires.

**get_project_signature**
Returns a complete index of the entire project: every device, every block (name, type, number, language, consistency state), and every tag table (name, tag count). Use this to understand the full project structure at a glance, or to compare before and after a change.

---

## Tips

- **Stay read-only by default**: Do inspection and source retrieval before considering a full-access session.
- **Refresh after authorized writes**: In a full-access workflow, compile the changed block and compare project state only after the user has authorized those operations.
- **Auto-number**: Leave the `number` field empty on create_block and create_instance_db to let TIA Portal assign the next available number.
- **Batch rename JSON format**: The `renames` field for batch_rename_tags must be valid JSON: `[{"from":"Old","to":"New"},{"from":"Old2","to":"New2"}]`
- **Tag table XML**: Use get_tags on an existing table as a read-only reference before an explicitly authorized import.
- **App must stay open**: The MCP server runs inside the app process. If you exit the dashboard from the system tray, HTTP clients lose the connection.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| "Security error — not a member of Siemens TIA Openness group" | Add your Windows account to the `Siemens TIA Openness` local group (Computer Management → Local Users and Groups → Groups) and sign out/in |
| "No running TIA Portal process found" | TIA Portal must be open with a project loaded before clicking Connect |
| "Inconsistent blocks cannot be exported" | Press Ctrl+B in TIA Portal to compile everything, then retry |
| My changes don't appear after restarting the app | Rebuild the project in Release and launch the executable from this checkout's `bin\Release\net48` folder |
| A project-changing tool is missing | The MCP server is read-only by default. Set `TIA_MCP_ACCESS=full` in the server environment and restart only for an intended full-access session |
| Live call log is empty | The log only fills when an MCP client calls the `/mcp` endpoint, not when you use User Control manually |
