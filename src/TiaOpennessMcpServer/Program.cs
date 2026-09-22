using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Prototype;
#if !MCP_CONTRACT_TEST
using System.Net;
using System.Windows.Forms;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using TiaOpennessMcpServer;
using TiaOpennessMcpServer.Utilities;

// ── Assembly resolver — must run before any Siemens type is referenced ────────
string[] TiaSearchPaths = new[]
{
    @"C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20",
    @"C:\Program Files\Siemens\Automation\Portal V20\Bin\PublicAPI",
    @"C:\Program Files\Siemens\Automation\Portal V20\Bin\PublicAPI\Client",
    @"C:\Program Files\Siemens\Automation\Portal V20\Bin",
};
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var n = new AssemblyName(e.Name).Name!;
    foreach (var dir in TiaSearchPaths)
    {
        var p = Path.Combine(dir, n + ".dll");
        if (File.Exists(p)) return Assembly.LoadFrom(p);
    }
    return null;
};

int httpPort = 5000;
var configuredPort = Environment.GetEnvironmentVariable("TIA_MCP_PORT")?.Trim();
if (!string.IsNullOrEmpty(configuredPort) &&
    (!int.TryParse(configuredPort, out httpPort) || httpPort < 1 || httpPort > 65535))
{
    throw new InvalidOperationException("TIA_MCP_PORT must be an integer between 1 and 65535.");
}
var lifecycleControlToken = Environment.GetEnvironmentVariable("TIA_MCP_CONTROL_TOKEN")?.Trim();
if (lifecycleControlToken != null &&
    lifecycleControlToken.Length > 0 &&
    lifecycleControlToken.Length < 32)
{
    throw new InvalidOperationException("TIA_MCP_CONTROL_TOKEN must contain at least 32 characters.");
}

// ── DI setup ──────────────────────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
services.AddSingleton<StaTaskScheduler>();
var accessProfile = Environment.GetEnvironmentVariable("TIA_MCP_ACCESS") ?? "full";
if (accessProfile != "full" && accessProfile != "read-only")
    throw new InvalidOperationException("TIA_MCP_ACCESS must be full or read-only.");
services.AddSingleton(provider => new ConnectionPrototypeService(provider.GetRequiredService<StaTaskScheduler>(), accessProfile == "full"));
var sp = services.BuildServiceProvider();
var connectionPrototype = sp.GetRequiredService<ConnectionPrototypeService>();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented               = false,
};
jsonOpts.Converters.Add(new JsonStringEnumConverter());
var mcp = new McpBoundary(connectionPrototype, jsonOpts, ex => ex is EngineeringException, connectionPrototype.RecordCall);

// ── HTTP listener ─────────────────────────────────────────────────────────────
var listener = new HttpListener();
var dashboardUri = new Uri($"http://127.0.0.1:{httpPort}/");
if (!dashboardUri.IsLoopback)
    throw new InvalidOperationException("The dashboard and HTTP MCP listener must bind to a loopback address.");
listener.Prefixes.Add(dashboardUri.AbsoluteUri);
listener.Start();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.Stop(); };

// Launch the WinForms window on a dedicated STA thread (required by WinForms/COM)
var mainFormReady = new TaskCompletionSource<MainForm>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var uiThread = new System.Threading.Thread(() =>
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    var mainForm = new MainForm(dashboardUri, browserOnly: true);
    mainFormReady.TrySetResult(mainForm);
    Application.Run(mainForm);
    listener.Stop(); // stop the HTTP loop when the window is closed via tray "Exit"
});
uiThread.SetApartmentState(System.Threading.ApartmentState.STA);
uiThread.IsBackground = false;
uiThread.Start();

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try   { ctx = await listener.GetContextAsync(); }
    catch { break; }
    _ = Task.Run(() => HandleAsync(ctx));
}

sp.Dispose();

async Task HandleAsync(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var res = ctx.Response;

    if (req.HttpMethod == "OPTIONS")
    {
        res.StatusCode = 200;
        res.Close();
        return;
    }

    var rawPath = req.RawUrl ?? "/";
    var queryStart = rawPath.IndexOf('?');
    if (queryStart >= 0) rawPath = rawPath.Substring(0, queryStart);
    var path = rawPath.TrimEnd('/');
    if (path == "") path = "/";
    var method = req.HttpMethod;


    try
    {
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            path.Equals("/api/lifecycle/stop", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(lifecycleControlToken))
            {
                await Json(res, new { error = "Lifecycle control is not enabled for this process." }, 404);
                return;
            }

            var suppliedToken = req.Headers["X-Tia-Mcp-Control-Token"]?.Trim();
            if (!string.Equals(suppliedToken, lifecycleControlToken, StringComparison.Ordinal))
            {
                await Json(res, new { error = "Invalid lifecycle control token." }, 403);
                return;
            }

            if (mainFormReady.Task.Status != TaskStatus.RanToCompletion)
            {
                await Json(res, new { error = "The dashboard UI is not ready for shutdown." }, 503);
                return;
            }

            await Json(res, new { status = "stopping" }, 202);
            mainFormReady.Task.Result.RequestExit();
            return;
        }

        if (path != "/mcp") { await HandleConnectionPrototype(ctx, path); return; }
        // ── MCP endpoint info (GET) ───────────────────────────────────────────────
        if (method == "GET" && path == "/mcp")
        {
            // Return a recognisable MCP error so clients detect the modern Streamable HTTP
            // transport and don't fall back to the old HTTP+SSE discovery flow.
            res.StatusCode = 405;
            await Json(res, new {
                jsonrpc = "2.0", id = (object?)null,
                error   = new { code = -32601, message = "MCP endpoint requires POST. Server: tia-portal-openness rehaul, protocol: 2025-03-26" }
            }, 405);
        }

        // ── MCP JSON-RPC 2.0 (Streamable HTTP) ───────────────────────────────────
        else if (method == "POST" && path == "/mcp")
        {
            var origin = req.Headers["Origin"];
            if (origin != null && !string.Equals(origin, dashboardUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            { await Json(res, new { error = "Cross-origin MCP requests are not allowed." }, 403); return; }
            if (!string.Equals(req.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            { await Json(res, new { error = "MCP requires application/json." }, 415); return; }
            DashboardCallContext.Origin.Value = req.Headers["X-Tia-Prototype"] == "1" ? "dashboard" : "mcp";
            try
            {
                var body = await ReadJson<McpRpcRequest>(req);
                if (body is null)
                { await Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); return; }

                // Notifications have no id — acknowledge and return
                if (body.Id is null && (body.Method?.StartsWith("notifications/") ?? false))
                { res.StatusCode = 202; res.Close(); return; }

                var (result, rpcErr) = await mcp.HandleAsync(body);
                if (rpcErr != null)
                    await Json(res, new { jsonrpc = "2.0", id = body.Id, error = rpcErr });
                else
                    await Json(res, new { jsonrpc = "2.0", id = body.Id, result });
            }
            catch (Exception ex) { try { await Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = ex.Message } }, 500); } catch { } }
            finally
            {
                DashboardCallContext.Origin.Value = null;
                DashboardCallContext.ConnectionId.Value = null;
                DashboardCallContext.ProjectPath.Value = null;
            }
        }

        else await Json(res, new { error = "Not found" }, 404);
    }
    catch (Exception ex)
    {
        try { await Json(res, new { error = ex.Message }, 500); } catch { }
    }
}

async Task HandleConnectionPrototype(HttpListenerContext ctx, string path)
{
    var req = ctx.Request;
    var res = ctx.Response;
    res.Headers["Cache-Control"] = "no-store";
    try
    {
        if (req.HttpMethod == "GET" && path == "/")
        {
            var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "connection-prototype.html"));
            await WriteBytes(res, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        }
        else if (req.HttpMethod == "GET" && (path == "/api/status" || path == "/api/prototype/status"))
            await Json(res, connectionPrototype!.Status());
        else if (req.HttpMethod == "GET" && path == "/api/prototype/dashboard")
            await Json(res, connectionPrototype!.Dashboard());
        else if (req.HttpMethod == "GET" && path == "/api/prototype/logs")
        {
            long after = 0;
            int generation = 0;
            long.TryParse(req.QueryString["after"], out after);
            int.TryParse(req.QueryString["generation"], out generation);
            await Json(res, connectionPrototype!.Logs(after < 0 ? 0 : after, generation));
        }
        else if (req.HttpMethod == "GET" && path == "/api/prototype/tool-forms")
            await WriteBytes(res, Encoding.UTF8.GetBytes(DashboardToolForms.Render(McpBoundary.ToolDefs(connectionPrototype.WriteToolsAvailable))), "text/html; charset=utf-8");
        else if (req.HttpMethod == "GET" && path == "/api/prototype/processes")
            await Json(res, await connectionPrototype!.DiscoverAsync());
        else if (req.HttpMethod == "POST" &&
                 (path == "/api/prototype/connect" || path == "/api/prototype/disconnect" ||
                  path == "/api/prototype/read" || path == "/api/prototype/monitor" ||
                  path == "/api/prototype/process-status" || path == "/api/prototype/devices" ||
                  path == "/api/prototype/device" || path == "/api/prototype/blocks" || path == "/api/prototype/block" ||
                  path == "/api/prototype/udts" || path == "/api/prototype/udt" ||
                  path == "/api/prototype/tag-tables" || path == "/api/prototype/tag-table" ||
                  path == "/api/prototype/cross-references" || path == "/api/prototype/tabs/dismiss" ||
                  path == "/api/prototype/projects/open"))
        {
            // Browser cross-origin forms cannot supply this header. No CORS permission is granted.
            var origin = req.Headers["Origin"];
            if (req.Headers["X-Tia-Prototype"] != "1" ||
                (origin != null && !string.Equals(origin, dashboardUri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)))
            {
                await Json(res, new { error = "Use the prototype dashboard on this server." }, 403);
                return;
            }
            if (req.ContentLength64 <= 0 || req.ContentLength64 > 16384)
                throw new ConnectionFault("invalidRequest", 0, "A JSON request body of at most 16 KiB is required.");
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var root = body.RootElement;
            if (path == "/api/prototype/tabs/dismiss")
            {
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("tabId", out var tabId) || tabId.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(tabId.GetString()))
                    throw new ConnectionFault("invalidRequest", 0, "Supply only the historical tabId to dismiss.");
                await Json(res, connectionPrototype!.Dismiss(tabId.GetString()));
                return;
            }
            if (path == "/api/prototype/projects/open")
            {
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("tabId", out var openTab) || openTab.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(openTab.GetString()))
                    throw new ConnectionFault("invalidRequest", 0, "Supply only the closed project tabId to open.");
                await Json(res, await connectionPrototype!.OpenProjectAsync(openTab.GetString()));
                return;
            }
            if (path == "/api/prototype/monitor")
            {
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("paused", out var paused) ||
                    (paused.ValueKind != JsonValueKind.True && paused.ValueKind != JsonValueKind.False))
                    throw new ConnectionFault("invalidRequest", 0, "Supply only a boolean paused field.");
                await Json(res, new { paused = await connectionPrototype!.SetMonitoringPausedAsync(paused.GetBoolean()) });
                return;
            }
            if (path == "/api/prototype/block")
            {
                var block = BlockReadRequest.Parse(root);
                await Respond("get_block", block.ProcessId, async () => (object?)await connectionPrototype.ReadBlockAsync(block));
                return;
            }
            if (path == "/api/prototype/udt")
            {
                var udt = BlockReadRequest.Parse(root);
                await Respond("get_udt", udt.ProcessId, async () => (object?)await connectionPrototype.ReadUdtAsync(udt));
                return;
            }
            if (path == "/api/prototype/tag-table")
            {
                var table = TagTableReadRequest.Parse(root);
                await Respond("get_tag_table", table.ProcessId, async () => (object?)await connectionPrototype.ReadTagTableAsync(table));
                return;
            }
            if (path == "/api/prototype/cross-references")
            {
                var references = CrossReferenceRequest.Parse(root);
                await Respond("get_cross_references", references.ProcessId, async () => (object?)await connectionPrototype.ReadCrossReferencesAsync(references));
                return;
            }
            var request = DiscoveryRequest.Parse(root, device: path == "/api/prototype/device", blocks: path == "/api/prototype/blocks" || path == "/api/prototype/udts" || path == "/api/prototype/tag-tables");
            var processId = request.ProcessId;
            if (path == "/api/prototype/connect") await Json(res, await connectionPrototype!.ConnectAsync(processId));
            else if (path == "/api/prototype/disconnect") await Json(res, await connectionPrototype!.DisconnectAsync(processId));
            else if (path == "/api/prototype/process-status") await Respond("get_status", processId, async () => (object?)await connectionPrototype!.ReadStatusAsync(processId));
            else if (path == "/api/prototype/devices") await Respond("list_devices", processId, async () => (object?)await connectionPrototype!.ListDevicesAsync(processId));
            else if (path == "/api/prototype/device") await Respond("get_device", processId, async () => (object?)await connectionPrototype!.ReadDeviceAsync(processId, request.ObjectId!, request.IncludePath));
            else if (path == "/api/prototype/blocks") await Respond("list_blocks", processId, async () => (object?)await connectionPrototype!.ListBlocksAsync(processId, request.PlcObjectId!));
            else if (path == "/api/prototype/udts") await Respond("list_udts", processId, async () => (object?)await connectionPrototype!.ListUdtsAsync(processId, request.PlcObjectId!));
            else if (path == "/api/prototype/tag-tables") await Respond("list_tag_tables", processId, async () => (object?)await connectionPrototype!.ListTagTablesAsync(processId, request.PlcObjectId!));
            else await Respond("readProject", processId, async () => (object?)await connectionPrototype!.ReadAsync(processId));
        }
        else
            await Json(res, new { error = "Use the prototype discovery and connection routes." }, 404);

        async Task Respond(string operation, int processId, Func<Task<object?>> work)
        {
            var started = Stopwatch.StartNew();
            try
            {
                var result = await work();
                var outcome = "success";
                string? error = null;
                if (result is DiscoveryResult discovery && discovery.Errors.Count > 0)
                {
                    outcome = "partial";
                    error = string.Join(" | ", discovery.Errors.Select(item => item.Origin + ": " + item.Message));
                }
                connectionPrototype!.RecordExternal("dashboard", operation, processId, started.Elapsed.TotalMilliseconds, outcome, error);
                await Json(res, result);
            }
            catch (Exception ex)
            {
                var id = ex is ConnectionFault fault && fault.ProcessId > 0 ? fault.ProcessId : processId;
                connectionPrototype!.RecordExternal("dashboard", operation, id, started.Elapsed.TotalMilliseconds, "error", ex.Message);
                throw;
            }
        }
    }
    catch (ConnectionFault ex)
    {
        var causeOrigin = ex.InnerException is EngineeringException ? "tia-openness" : "bridge";
        var errors = new List<DiscoveryError>
        {
            new() { Origin = "bridge", Operation = path, Message = ex.Message }
        };
        if (ex.InnerException != null)
            errors.Add(new DiscoveryError { Origin = causeOrigin, Operation = path, Message = ex.InnerException.Message });
        await Json(res, new { readAtUtc = DateTimeOffset.UtcNow, ex.ProcessId,
            errors,
            error = new { ex.Code, ex.Message, ex.ProcessId, ex.ReconnectRequired,
            causeOrigin = ex.InnerException == null ? null : causeOrigin,
            causeType = ex.InnerException?.GetType().Name, causeMessage = ex.InnerException?.Message } },
            ex.Code == "invalidRequest" ? 400 : ex.Code == "busy" ? 429 : 409);
    }
    catch (JsonException ex) { await Json(res, new { error = new { code = "invalidRequest", message = ex.Message } }, 400); }
}

async Task Json(HttpListenerResponse res, object? data, int status = 200)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, jsonOpts));
    res.StatusCode = status;
    res.ContentType = "application/json; charset=utf-8";
    await WriteBytes(res, bytes, res.ContentType);
}

async Task WriteBytes(HttpListenerResponse res, byte[] bytes, string contentType)
{
    res.ContentType     = contentType;
    res.ContentLength64 = bytes.Length;
    try
    {
        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        res.Close();
    }
    catch { }
}

async Task<T?> ReadJson<T>(HttpListenerRequest req) where T : class
{
    using var reader = new System.IO.StreamReader(req.InputStream, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return null;
    return JsonSerializer.Deserialize<T>(body, jsonOpts);
}


#endif
// Definitions, validation and dispatch are shared with the Siemens-free contract harness.
internal interface IMcpOperations
{
    bool WriteToolsAvailable { get; }
    Task<WriteResult> WriteAsync(WriteRequest request);
    object BridgeStatus();
    Task<ProcessDiscovery> DiscoverAsync();
    Task<ProcessStatus> ReadStatusAsync(int processId);
    Task<DeviceInventory> ListDevicesAsync(int processId);
    Task<DeviceRead> ReadDeviceAsync(int processId, string objectId, bool includePath);
    Task<BlockInventory> ListBlocksAsync(int processId, string plcObjectId);
    Task<BlockRead> ReadBlockAsync(BlockReadRequest request);
    Task<BlockInventory> ListUdtsAsync(int processId, string plcObjectId);
    Task<BlockRead> ReadUdtAsync(BlockReadRequest request);
    Task<BlockInventory> ListTagTablesAsync(int processId, string plcObjectId);
    Task<TagTableRead> ReadTagTableAsync(TagTableReadRequest request);
    Task<CrossReferenceRead> ReadCrossReferencesAsync(CrossReferenceRequest request);
}

internal sealed class McpCallNote
{
    public string Origin { get; set; } = "mcp";
    public string Operation { get; set; } = "";
    public int? ProcessId { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? ProjectPath { get; set; }
    public double DurationMs { get; set; }
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
}

internal sealed class McpBoundary
{
    private readonly IMcpOperations _reads;
    private readonly JsonSerializerOptions _json;
    private readonly Func<Exception, bool> _isNative;
    private readonly Action<McpCallNote>? _journal;
    public McpBoundary(IMcpOperations reads, JsonSerializerOptions json, Func<Exception, bool> isNative, Action<McpCallNote>? journal = null)
    { _reads = reads; _json = json; _isNative = isNative; _journal = journal; }

    internal static List<McpToolDefinition> ToolDefs(bool writesEnabled = true)
    {
        var process = McpP("processId", "integer", true, "Positive process ID from list_tia_processes. The user must connect that UI process in the dashboard first. Every project call uses that retained project; never attaches or selects an implicit process.");
        var cpu = McpP("plcObjectId", "string", true, "Opaque native CPU DeviceItem ID from get_device, whose SoftwareContainer owns PlcSoftware. Not a rack or software ID.");
        var deviceId = McpP("objectId", "string", true, "Opaque Device objectId from list_devices in this process. Preserve exactly; a name, path or CPU DeviceItem ID is not a Device selector.");
        var blockId = McpP("objectId", "string", true, "Opaque block objectId from list_blocks in this process. Preserve exactly; names and paths are not selectors.");
        var udtId = McpP("objectId", "string", true, "Opaque UDT objectId from list_udts in this process. Preserve exactly; names and paths are not selectors.");
        var tableId = McpP("objectId", "string", true, "Opaque tag-table objectId from list_tag_tables in this process. Preserve exactly; an entry ID is not a table ID.");
        var referenceId = McpP("objectId", "string", true, "Opaque engineering objectId in this process, for example a block/UDT inventory ID or a tag's own objectId from get_tag_table. Use the entry ID for a tag's references, not its table ID. Null IDs cannot be used.");
        var path = McpP("includePath", "boolean", false, "Default true: construct navigation paths through parents. False skips that traversal and returns null paths; paths are navigation aids, not object selectors.", true);
        var source = McpP("includeSource", "boolean", false, "Default true: export native source documents with name, content and exact-content checksum. False reads metadata only and returns source:null without export.", true);
        var format = McpP("sourceFormat", "string", false, "Default best uses the tool's documented native fallback order. Explicit external-source, simatic-sd or simatic-ml never fall back. For a later write use the returned source.format, not best, and only each document's name/content.", "best", "best", "external-source", "simatic-sd", "simatic-ml");
        var dependencies = McpP("includeDependencies", "boolean", false, "Default false. True requires includeSource enabled and explicitly supplied sourceFormat:external-source. Native dependency generation may include additional declarations; consider every declaration before writing the source back.", false);
        var tools = new List<McpToolDefinition>
        {
            McpT("list_tia_processes", "Discover running TIA processes, optional primary-project paths and connectedByMcp state without attaching. Accepts no arguments; use a returned processId after the user connects it in the dashboard."),
            McpT("get_status", "Without processId, passive bridge facts only. With processId, connection state and available native TIA/products/primary-project context; never attaches.",
                McpP("processId", "integer", false, "Positive process ID from list_tia_processes for explicit connection/native status. Omit this field for passive bridge status only; null is not omission.")),
            McpT("list_devices", "Inventory native device groups and Devices. Preserves readable branches; inspect complete/errors. Use a returned Device objectId with get_device for CPU discovery. No detailed metadata or source.", process),
            McpT("get_device", "Read one Device's metadata and nested DeviceItem tree. Use the CPU's returned plcObjectId for block, UDT and tag-table inventories and source writes.", process, deviceId, path),
            McpT("list_blocks", "Inventory native block groups, blocks and unit scopes for one CPU. Use block IDs with get_block and group IDs/paths as write destinations. No source or detailed metadata; inspect complete/errors.", process, cpu),
            McpT("get_block", "Read block metadata and optional native source. best: SCL/STL/DB external-source then simatic-ml; LAD simatic-sd then simatic-ml; other languages simatic-ml. Source failures retain metadata and errors. To update, edit the complete returned documents and submit their name/content to write_blocks in the intended CPU/scope, then read back.", process, blockId, path, source, format, dependencies),
            McpT("list_udts", "Inventory native type groups, UDTs and unit scopes for one CPU. Use UDT IDs with get_udt and group IDs/paths as write destinations. No source or detailed metadata; inspect complete/errors.", process, cpu),
            McpT("get_udt", "Read UDT metadata and optional native source. best: external-source (.udt), simatic-sd, then simatic-ml. Source failures retain metadata and errors. To update, edit the complete returned documents and submit their name/content to write_udts in the intended CPU/scope, then read back.", process, udtId, path, source, format, dependencies),
            McpT("list_tag_tables", "Inventory native tag-table groups and tables for one CPU. Use table IDs with get_tag_table or entry creation; group IDs/paths select table creation/import destinations. No entries, source or detailed metadata.", process, cpu),
            McpT("get_tag_table", "Read table metadata and optional native Tags/UserConstants/SystemConstants with their own IDs or null. Use tag/user-constant IDs for attribute edits or deletion. No source export or checksum: this JSON is not an import_tag_tables XML document.", process, tableId, path,
                McpP("includeEntries", "boolean", false, "Default true: read tags, user constants and system constants with native values and their own IDs or null. False skips entry access and returns entries:null. System constants are read-only.", true)),
            McpT("get_cross_references", "Query the object's native CrossReferenceService with AllObjects. Preserve Sources/Children/References/Locations, native paths and enums. Native service determines support; no compile or derived graph. Inspect complete/errors before treating the result as complete usage information.", process, referenceId)
        };
        if (writesEnabled)
        {
            var group = McpP("groupObjectId", "string", false, "Existing native group ID from the matching block/type/tag-table inventory in this CPU, including the intended unit scope. Must have the matching composition type. Omit both destination fields for the CPU root; mutually exclusive with groupPath.");
            var groupPath = McpP("groupPath", "string", false, "Exact PLC[/unit]/group path from the matching inventory, used when a group has no native ID. Must resolve uniquely within this CPU. Mutually exclusive with groupObjectId; this does not create folders.");
            var writeFormat = McpP("sourceFormat", "string", true, "Required explicit format: external-source = one .scl/.awl/.db/.udt; simatic-sd = one .s7dcl plus optional same-stem .s7res; simatic-ml = one .xml. For read-edit-write use returned source.format. best is not accepted; no fallback.", null, "external-source", "simatic-sd", "simatic-ml");
            var documents = McpDocuments();
            var name = McpP("name", "string", true, "Nonblank native name of the new table, tag or user constant. TIA validates naming and uniqueness; this is creation, not an existing-object selector or rename operation.");
            var tagType = McpE(McpP("dataType", "string", true, "Native PLC tag type name, for example Bool, Byte, Word, Int, DInt or Real. Supported project-defined PLC types may also be used; CPU, memory area and address compatibility determine acceptance. This is not a fixed enum. Attribute edits use DataTypeName."), "Bool", "Int", "Real");
            var constantType = McpE(McpP("dataType", "string", true, "Native user-constant type name, for example Int, Real or Time, compatible with the supplied literal. Constants have a value instead of a logical address; their supported types differ from addressed tags. TIA determines acceptance. Attribute edits use DataTypeName."), "Int", "Real", "Time");
            var table = McpP("objectId", "string", true, "Native tag-table objectId from list_tag_tables or get_tag_table. Creates an entry inside this existing table; preserve the ID exactly.");
            var entry = McpP("objectId", "string", true, "The existing tag's or user constant's own objectId from get_tag_table(includeEntries:true), not the containing table ID. Null IDs cannot be used. System constants are read-only.");
            tools.AddRange(new[]
            {
                McpT("write_blocks", "Create or replace blocks from complete native source documents. To update, read with get_block, edit source, and write to the intended CPU/scope. Declarations determine affected names, not filenames or a target block ID. External source uses generation; SD/XML use Override. No update-only mode, member patch, stale-source check or delete. May affect multiple objects or fail partially; inspect complete/errors/affectedObjects and read back. Does not save or retry.", process, cpu, group, groupPath, writeFormat, documents),
                McpT("write_udts", "Create or replace PLC data types from complete native source documents. To update, read with get_udt, edit source, and write to the intended CPU/scope. Declarations determine affected names, not filenames or a target UDT ID. External source uses generation; SD/XML use Override. No update-only mode, member patch, stale-source check or delete. May affect multiple objects or fail partially; inspect complete/errors/affectedObjects and read back. Does not save or retry.", process, cpu, group, groupPath, writeFormat, documents),
                McpT("create_tag_table", "Create an empty tag table in the selected PLC root or existing group. Add entries with create_tag/create_user_constant. Does not update, rename or delete an existing table and does not save.", process, cpu, group, groupPath, name),
                McpT("create_tag", "Create a tag in an existing table. Supply a native type and compatible logical address. To edit an existing tag use set_tag_entry_attribute with its entry ID. Does not save.", process, table, name, tagType,
                    McpE(McpP("logicalAddress", "string", true, "Nonblank native logical address. I=input, Q=output, M=memory; bit addresses use byte.bit (bit 0..7), B/W/D select 8/16/32 bits. Examples: Bool at %M0.0, Int at %IW64, Real at %MD100. CPU range, type and area must be compatible. Unlike native unset-address creation, this tool rejects blank addresses."), "%M0.0", "%IW64", "%MD100")),
                McpT("create_user_constant", "Create a user constant in an existing table using a type and native literal string. To edit its writable attributes use set_tag_entry_attribute with the constant's ID. Does not save.", process, table, name, constantType,
                    McpE(McpP("value", "string", true, "Nonblank native constant literal as a JSON string, for example Int: \"100\", Real: \"1.5\", Time: \"T#1s\". The literal must match dataType; a JSON number or boolean is not accepted here."), "100", "1.5", "T#1s")),
                McpT("set_tag_entry_attribute", "Change one native attribute on an existing tag or user constant. TIA determines writability and value acceptance. No table metadata or system-constant edits. Does not save; read back the containing table.", process, entry,
                    McpE(McpP("attributeName", "string", true, "Native writable attribute name, with native casing. Tags: DataTypeName, LogicalAddress, Name (V20), ExternalAccessible, ExternalVisible, ExternalWritable, IsSafety. User constants: DataTypeName or Value; Name is documented read-only. This is guidance, not an allowlist; TIA decides."), "DataTypeName", "LogicalAddress", "ExternalAccessible", "Value"),
                    ("attributeValue", true, new Dictionary<string, object> { ["type"] = new[] { "string", "boolean", "number" }, ["description"] = "JSON string, boolean or finite number matching the native attribute. DataTypeName/LogicalAddress/Name and constant Value use strings; ExternalAccessible/ExternalVisible/ExternalWritable/IsSafety use booleans. Integers become Int32 then Int64 where representable, otherwise Double. No string coercion; null, arrays and objects are rejected.", ["examples"] = new object[] { "Int", "%IW64", false, "100" } })),
                McpT("delete_tag_entry", "Delete one existing tag or user constant by its own ID. Does not delete blocks, UDTs, whole tag tables or system constants. Returns the identity captured before deletion; read back its table. Does not save or retry.", process, entry),
                McpT("import_tag_tables", "Import tag tables from one complete SimaticML XML document using native Override. get_tag_table JSON cannot be used as XML. Native names/import semantics determine affected tables; no direct table-ID update, table rename or whole-table delete tool. Do not assume omitted entries are deleted. Inspect complete/errors/affectedObjects and read back; no save or retry.", process, cpu, group, groupPath, McpDocuments(xmlOnly: true))
            });
        }
        return tools;
    }

    private static (string name, bool required, Dictionary<string, object> schema) McpE(
        (string name, bool required, Dictionary<string, object> schema) property, params object[] examples)
    {
        property.schema["examples"] = examples;
        return property;
    }

    private static (string name, bool required, Dictionary<string, object> schema) McpDocuments(bool xmlOnly = false) =>
        ("documents", true, new Dictionary<string, object>
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = xmlOnly ? 1 : 2,
            ["description"] = (xmlOnly ? "One complete SimaticML XML document." : "One external source or XML document, or one SIMATIC SD .s7dcl with optional same-stem .s7res.") +
                " Supply name/content only, without read-result checksums. The server stages exact UTF-8 text without BOM in owned temporary files, invokes native generation/import, then attempts cleanup. No client/server file paths; filenames do not select objects.",
            ["items"] = new { type = "object", additionalProperties = false, required = new[] { "name", "content" },
                properties = new
                {
                    name = new { type = "string", minLength = 1, maxLength = 128,
                        description = (xmlOnly ? "Plain .xml filename, for example Tags.xml." : "Plain filename with the selected format's extension, for example MotorStatus.udt or ScaleValue.scl; SD pairs share a stem.") +
                            " Used for temporary staging, not an object selector. Unique ignoring case, max 128 characters; no paths, reserved Windows names, control characters or trailing dot/space.",
                        examples = xmlOnly ? new[] { "Tags.xml" } : new[] { "MotorStatus.udt", "ScaleValue.scl" } },
                    content = new { type = "string", minLength = 1,
                        description = (xmlOnly ? "Complete native SimaticML XML text, including the document structure; not get_tag_table JSON." : "Complete native source text for this format, not a patch or member list. A .udt contains the full TYPE/STRUCT/END_STRUCT/END_TYPE declaration; .scl contains the complete block declaration and body; XML/SD retain their native structure.") +
                            " Must be nonblank valid Unicode. Use actual newlines in this editor; JSON clients encode them as \\n. Declarations determine the names TIA creates or replaces; changing the filename alone does not rename an object." }
                } }
        });

    internal static bool IsWrite(string name) => name is "write_blocks" or "write_udts" or "create_tag_table" or
        "create_tag" or "create_user_constant" or "set_tag_entry_attribute" or "delete_tag_entry" or "import_tag_tables";

    private static McpToolDefinition McpT(string name, string description,
        params (string name, bool required, Dictionary<string, object> schema)[] properties) => new()
    {
        Name = name, Description = description, Annotations = new McpToolAnnotations { ReadOnlyHint = !IsWrite(name) },
        InputSchema = new McpInputSchema
        {
            Properties = properties.ToDictionary(p => p.name, p => (object)p.schema),
            Required = properties.Where(p => p.required).Select(p => p.name).ToArray(),
            AllOf = name is "get_block" or "get_udt" ? new object[] { new Dictionary<string, object>
            {
                ["if"] = new { required = new[] { "includeDependencies" }, properties = new { includeDependencies = new { @const = true } } },
                ["then"] = new { required = new[] { "sourceFormat" }, properties = new
                    { sourceFormat = new { @const = "external-source" }, includeSource = new { @const = true } } }
            } } : properties.Any(property => property.name == "groupObjectId")
                ? new object[] { new { @not = new { required = new[] { "groupObjectId", "groupPath" } } } } : null
        }
    };

    private static (string name, bool required, Dictionary<string, object> schema) McpP(
        string name, string type, bool required, string description, object? defaultValue = null, params string[] values)
    {
        var schema = new Dictionary<string, object> { ["type"] = type, ["description"] = description };
        if (type == "integer") { schema["minimum"] = 1; schema["maximum"] = int.MaxValue; }
        if (type == "string" && required) { schema["minLength"] = 1; schema["pattern"] = @"\S"; }
        if (defaultValue != null) schema["default"] = defaultValue;
        if (values.Length > 0) schema["enum"] = values;
        return (name, required, schema);
    }

    internal async Task<(object? result, object? rpcErr)> HandleAsync(McpRpcRequest body)
    {
        switch (body.Method)
        {
            case "initialize":
                var clientVersion = body.Params is { ValueKind: JsonValueKind.Object } parameters &&
                    parameters.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString() : null;
                return (new { protocolVersion = clientVersion == "2024-11-05" ? "2024-11-05" : "2025-03-26",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "tia-portal-openness", version = "rehaul-writes-1" },
                    instructions = (_reads.WriteToolsAvailable ? "Eleven read tools and eight write tools. Writes are not saved automatically. " : "Eleven read-only tools. ") +
                        "Discover with list_tia_processes. The user connects existing TIA UI processes in the dashboard; MCP never attaches or reconnects. Supply processId on every project operation and native selectors. Inspect complete, errors and affectedObjects. Native writes can partially change the project on failure; never retry automatically. No save, compile or online operations." }, null);
            case "ping": return (new { }, null);
            case "tools/list": return (new { tools = ToolDefs(_reads.WriteToolsAvailable) }, null);
            case "tools/call": return (await CallAsync(body.Params), null);
            default: return (null, new { code = -32601, message = "Method not found: " + body.Method });
        }
    }

    private async Task<object> CallAsync(JsonElement? parameters)
    {
        JsonElement? requestedProcess = null;
        string operation = "tools/call";
        DashboardCallContext.ConnectionId.Value = null;
        DashboardCallContext.ProjectPath.Value = null;
        var started = Stopwatch.StartNew();
        try
        {
            // Preserve the supplied process selector even when argument validation fails.
            if (parameters is { ValueKind: JsonValueKind.Object } candidate &&
                candidate.TryGetProperty("arguments", out var supplied) && supplied.ValueKind == JsonValueKind.Object &&
                supplied.TryGetProperty("processId", out var selected)) requestedProcess = selected;
            if (!(parameters is { ValueKind: JsonValueKind.Object } call))
                throw Invalid("Supply a tool name and an optional arguments object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in call.EnumerateObject())
                if (!names.Add(field.Name) || !(field.Name is "name" or "arguments" or "_meta"))
                    throw Invalid("Unknown or duplicate tool-call field: " + field.Name);
            if (!call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw Invalid("Supply a tool name.");
            operation = name.GetString()!;
            if (!ToolDefs(_reads.WriteToolsAvailable).Any(tool => tool.Name == operation))
                throw new ConnectionFault("unknownTool", 0, "This tool is not published.");
            using var empty = JsonDocument.Parse("{}");
            var args = call.TryGetProperty("arguments", out var arguments) ? arguments : empty.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw Invalid("arguments must be an object.");
            object payload;
            // Readers capture tickets before queueing. Do not schedule or attach here.
            switch (operation)
            {
                case "list_tia_processes":
                    if (args.EnumerateObject().Any()) throw Invalid("This tool accepts no arguments.");
                    payload = await _reads.DiscoverAsync(); break;
                case "get_status":
                    payload = !args.EnumerateObject().Any() ? _reads.BridgeStatus()
                        : await _reads.ReadStatusAsync(DiscoveryRequest.Parse(args, false).ProcessId); break;
                case "list_devices":
                    payload = await _reads.ListDevicesAsync(DiscoveryRequest.Parse(args, false).ProcessId); break;
                case "get_device":
                    var device = DiscoveryRequest.Parse(args, true);
                    payload = await _reads.ReadDeviceAsync(device.ProcessId, device.ObjectId!, device.IncludePath); break;
                case "list_blocks": case "list_udts": case "list_tag_tables":
                    var inventory = DiscoveryRequest.Parse(args, false, blocks: true);
                    payload = operation == "list_blocks" ? await _reads.ListBlocksAsync(inventory.ProcessId, inventory.PlcObjectId!)
                        : operation == "list_udts" ? await _reads.ListUdtsAsync(inventory.ProcessId, inventory.PlcObjectId!)
                        : await _reads.ListTagTablesAsync(inventory.ProcessId, inventory.PlcObjectId!); break;
                case "get_block": payload = await _reads.ReadBlockAsync(BlockReadRequest.Parse(args)); break;
                case "get_udt": payload = await _reads.ReadUdtAsync(BlockReadRequest.Parse(args)); break;
                case "get_tag_table": payload = await _reads.ReadTagTableAsync(TagTableReadRequest.Parse(args)); break;
                case "get_cross_references": payload = await _reads.ReadCrossReferencesAsync(CrossReferenceRequest.Parse(args)); break;
                case "write_blocks": case "write_udts": case "create_tag_table": case "create_tag":
                case "create_user_constant": case "set_tag_entry_attribute": case "delete_tag_entry": case "import_tag_tables":
                    payload = await _reads.WriteAsync(WriteRequest.Parse(operation, args)); break;
                default: throw new InvalidOperationException("Published tool has no dispatch.");
            }
            // Partial payloads and exact native errors remain in the reader's response envelope.
            NoteCall(operation, requestedProcess, payload, false, null, started);
            return ToolResult(payload, payload is WriteResult write && !write.Complete);
        }
        catch (Exception ex)
        {
            var errors = new List<DiscoveryError>();
            for (Exception? cause = ex; cause != null; cause = cause.InnerException)
            {
                if (cause is ConnectionFault && cause.InnerException?.Message == cause.Message) continue;
                errors.Add(new DiscoveryError { Origin = _isNative(cause) ? "tia-openness" : "bridge",
                    Operation = operation, Message = cause.Message });
            }
            var fault = ex as ConnectionFault;
            var payload = new Dictionary<string, object?>
            {
                ["readAtUtc"] = DateTimeOffset.UtcNow, ["errors"] = errors,
                ["error"] = new { code = fault?.Code ?? (_isNative(ex) ? (IsWrite(operation) ? "nativeWriteFailed" : "nativeReadFailed") : "bridgeFailure"),
                    message = ex.Message, reconnectRequired = fault?.ReconnectRequired ?? false }
            };
            if (requestedProcess.HasValue) payload["processId"] = requestedProcess.Value;
            NoteCall(operation, requestedProcess, null, true, ex.Message, started);
            return ToolResult(payload, true);
        }
    }

    private void NoteCall(string operation, JsonElement? requestedProcess, object? payload, bool failed, string? error, Stopwatch started)
    {
        try
        {
            int? process = requestedProcess is { ValueKind: JsonValueKind.Number } selected && selected.TryGetInt32(out var parsed) ? parsed : null;
            if (payload is DiscoveryResult discovery && discovery.ProcessId > 0) process = discovery.ProcessId;
            var partial = !failed && ((payload is DiscoveryResult result && result.Errors.Count > 0) ||
                (payload is ProcessDiscovery processes && processes.Errors.Count > 0));
            if (partial)
            {
                var errors = payload is DiscoveryResult discoveryErrors ? discoveryErrors.Errors :
                    ((ProcessDiscovery)payload!).Errors;
                error = string.Join(" | ", errors.Select(item => item.Origin + ": " + item.Message));
            }
            _journal?.Invoke(new McpCallNote
            {
                Origin = string.IsNullOrWhiteSpace(DashboardCallContext.Origin.Value) ? "mcp" : DashboardCallContext.Origin.Value!,
                Operation = operation,
                ProcessId = process,
                ConnectionId = DashboardCallContext.ConnectionId.Value,
                ProjectPath = DashboardCallContext.ProjectPath.Value,
                DurationMs = started.Elapsed.TotalMilliseconds,
                Outcome = failed ? "error" : partial ? "partial" : "success",
                Error = error
            });
        }
        catch { /* A log failure must not replace the tool payload. */ }
    }

    private object ToolResult(object payload, bool failed) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload, _json) } }, isError = failed
    };
    private static ConnectionFault Invalid(string message) => new("invalidRequest", 0, message);
}

internal sealed class McpRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public object? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}
internal sealed class McpToolAnnotations
{
    public bool ReadOnlyHint { get; set; }
    public bool DestructiveHint => !ReadOnlyHint;
}

internal sealed class McpToolDefinition
{
    public McpToolAnnotations Annotations { get; set; } = new();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public McpInputSchema InputSchema { get; set; } = new();
}
internal sealed class McpInputSchema
{
    public string Type { get; set; } = "object";
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.Ordinal);
    public string[] Required { get; set; } = Array.Empty<string>();
    public bool AdditionalProperties => false;
    public object[]? AllOf { get; set; }
}
