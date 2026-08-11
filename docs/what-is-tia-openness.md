# What is TIA Portal Openness?

TIA Portal Openness is Siemens' official programming interface (API) for automating engineering tasks in TIA Portal. Instead of clicking through menus by hand, you write code that controls TIA Portal for you.

> "TIA Portal Openness is the one common interface for digital engineering in TIA Portal — an API for automating TIA Portal engineering tasks in the value chain." — Siemens

---

## The short version

Normally, when you work with a TIA Portal project, you:
- Open TIA Portal
- Click through the project tree
- Manually configure hardware, write code, compile, download

TIA Portal Openness lets a program do those same steps automatically. Your code connects to TIA Portal while it's running and drives it through an object-oriented .NET API.

---

## What it can automate

| Area | Examples |
|---|---|
| **Projects** | Open, create, save, close projects |
| **Hardware** | Add devices, configure modules, set IP addresses |
| **PLC software** | Create, read, write, compile, and export blocks (OB, FB, FC, DB) |
| **Tag tables** | Read and write PLC tags and tag tables |
| **HMI** | Configure HMI screens and connections |
| **Data exchange** | Export/import blocks as SimaticML XML or SIMATIC SD documents |
| **CI/CD pipelines** | Automated build, test, and verification workflows |
| **Add-Ins** | Small tools embedded directly in the TIA Portal UI |

---

## What it cannot do

- It **cannot connect to a live PLC** (for that, use the S7 communication libraries or TIA Portal's own download/online functions)
- It **cannot remove StartDrive or Safety option packages** programmatically — those are read-only via the `UsedProducts` property
- It **does not work without TIA Portal running** — it attaches to a running TIA Portal process, it is not a standalone engine
- SCL has human-readable source through the API. In TIA Portal V20, pure LAD blocks can also be exported as readable SIMATIC SD documents; raw SimaticML remains the compatibility representation for other graphical or mixed-language cases.

---

## Key facts

- **Free** — included in every TIA Portal installation, no extra licence needed
- **Based on .NET Framework 4.8** — works with C#, VB.NET, F# (not .NET 5+)
- **COM-based** — all calls must run on an STA (Single-Threaded Apartment) thread
- **Versioned with TIA Portal** — V18, V19, V20, V21 each have their own API version; the DLL lives at:
  ```
  C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Siemens.Engineering.dll
  ```
- **Security group required** — your Windows account must be in the `Siemens TIA Openness` local group before any Openness application can connect

---

## Data formats you'll encounter

### SimaticML (XML)
The format TIA Portal Openness uses when you export or import blocks, tag tables, and HMI screens. Every `.xml` file produced by `block.Export()` is SimaticML. The schema definitions are at:
```
C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\Schemas\
```

### SIMATIC SD documents — V20+
`ExportAsDocuments()` can export a pure LAD block as readable `.s7dcl` program text with available `.s7res` resource and comment data. This is the authoritative MCP-facing LAD representation in this project; SimaticML remains available for traceability and unsupported cases.

### AutomationML (XML)
An open standard for exchanging hardware (CAx) data. Used when importing hardware configurations from tools like TIA Selection Tool or EPLAN Electric P8.

---

## How this project uses Openness

This dashboard uses the V20 API (`Siemens.Engineering.dll`) to:

1. **Attach** to a running TIA Portal process with `TiaPortal.GetProcesses()[0].Attach()`
2. **Read the project tree** — devices, blocks, tag tables
3. **Export blocks** as SimaticML XML for viewing and editing in the browser
4. **Export pure LAD** read-only as SIMATIC SD `.s7dcl` and `.s7res` documents
5. **Analyse SCL** without compiling or changing the project
6. **Expose explicit mutations** such as import, create, compile, rename, and save only through the opt-in full-access profile
7. **Read used products** from `project.UsedProducts`

The server profile is read-only by default; `TIA_MCP_ACCESS=full` exposes implemented mutating MCP tools, REST routes, and dashboard controls after an intentional restart. The clone route/tool is quarantined at the server boundary, and its legacy implementation is unsupported for a project attached from the user's running TIA Portal instance.

All of this runs through a dedicated STA thread (`StaTaskScheduler.cs`) because TIA Openness is COM-based and COM requires a single, stable thread for all calls.

---

## Official resources

| Resource | Link |
|---|---|
| Siemens overview page | [support.industry.siemens.com/cs/document/109792902](https://support.industry.siemens.com/cs/document/109792902) |
| V20 System Manual | [docs.tia.siemens.cloud — V20](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows) |
| V21 System Manual | [docs.tia.siemens.cloud — V21](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows) |
| GitHub examples | [github.com/tia-portal-applications](https://github.com/tia-portal-applications) |
| Siemens community forum | [Industry Online Support — Openness](https://support.industry.siemens.com/forum/ww/en/search/conf/243/?text=openness) |
| SITRAIN training | [TIA Openness 1 & 2 courses](https://www.sitrain-learning.siemens.com) |
| TIA Portal Add-Ins guide | [support.industry.siemens.com/cs/document/109773999](https://support.industry.siemens.com/cs/document/109773999) |
| SIMATIC SD format | [support.industry.siemens.com/cs/document/109994073](https://support.industry.siemens.com/cs/document/109994073) |

---

## Ready-to-use Openness tools from Siemens

Siemens and the community have built several tools on top of Openness worth knowing about:

- **TIA Portal Openness Explorer** — browse a project's API object tree interactively (good for learning)
- **Excel Code Generator** — generate PLC blocks from Excel spreadsheets
- **SIMATIC Modular Application Creator** — modular project generation
- **TIA Scripting (Python)** — Python wrapper for the Openness API
- **TIA Openness Migration Assistant** — help migrating projects between TIA Portal versions
- **Project Check** — validate projects against programming style guides
- **TIA Openness Library Compare** — diff two libraries or projects
