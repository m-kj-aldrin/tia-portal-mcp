using System.Net;
using System.Windows.Forms;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using TiaOpennessMcpServer;
using TiaOpennessMcpServer.Utilities;
using TiaOpennessMcpServer.Prototype;

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
services.AddSingleton<ConnectionPrototypeService>();
var sp = services.BuildServiceProvider();
var connectionPrototype = sp.GetRequiredService<ConnectionPrototypeService>();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented               = false,
};
jsonOpts.Converters.Add(new JsonStringEnumConverter());

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
                error   = new { code = -32601, message = "MCP endpoint requires POST. Server: tia-portal-openness rehaul transition, protocol: 2025-03-26" }
            }, 405);
        }

        // ── MCP JSON-RPC 2.0 (Streamable HTTP) ───────────────────────────────────
        else if (method == "POST" && path == "/mcp")
        {
            try
            {
                var body = await ReadJson<McpRpcRequest>(req);
                if (body is null)
                { await Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); return; }

                // Notifications have no id — acknowledge and return
                if (body.Id is null && (body.Method?.StartsWith("notifications/") ?? false))
                { res.StatusCode = 202; res.Close(); return; }

                var (result, rpcErr) = await HandleMcpRequest(body);
                if (rpcErr != null)
                    await Json(res, new { jsonrpc = "2.0", id = body.Id, error = rpcErr });
                else
                    await Json(res, new { jsonrpc = "2.0", id = body.Id, result });
            }
            catch (Exception ex) { try { await Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = ex.Message } }, 500); } catch { } }
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
        else if (req.HttpMethod == "GET" && path == "/api/prototype/processes")
            await Json(res, await connectionPrototype!.DiscoverAsync());
        else if (req.HttpMethod == "POST" &&
                 (path == "/api/prototype/connect" || path == "/api/prototype/disconnect" ||
                  path == "/api/prototype/read" || path == "/api/prototype/monitor" ||
                  path == "/api/prototype/process-status" || path == "/api/prototype/devices" ||
                  path == "/api/prototype/device" || path == "/api/prototype/blocks" || path == "/api/prototype/block"))
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
                await Json(res, await connectionPrototype.ReadBlockAsync(BlockReadRequest.Parse(root)));
                return;
            }
            var request = DiscoveryRequest.Parse(root, device: path == "/api/prototype/device", blocks: path == "/api/prototype/blocks");
            var processId = request.ProcessId;
            object? result = path == "/api/prototype/connect" ? await connectionPrototype!.ConnectAsync(processId)
                : path == "/api/prototype/disconnect" ? await connectionPrototype!.DisconnectAsync(processId)
                : path == "/api/prototype/process-status" ? await connectionPrototype!.ReadStatusAsync(processId)
                : path == "/api/prototype/devices" ? await connectionPrototype!.ListDevicesAsync(processId)
                : path == "/api/prototype/device" ? await connectionPrototype!.ReadDeviceAsync(processId, request.ObjectId!, request.IncludePath)
                : path == "/api/prototype/blocks" ? await connectionPrototype!.ListBlocksAsync(processId, request.PlcObjectId!)
                : await connectionPrototype!.ReadAsync(processId);
            await Json(res, result);
        }
        else
            await Json(res, new { error = "Use the prototype discovery and connection routes." }, 404);
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

List<McpToolDefinition> McpToolDefs()
{
    return new List<McpToolDefinition>
    {
        McpT("connect_to_tia_portal", "Reuses the active project, attaches to an exact open-project match, or visibly opens the supplied compatible project path when no project is active.",
            McpP("projectPath", "string", false, "Optional exact project path. Credentials remain exclusively in the visible TIA Portal UI.")),
        McpT("get_status",   "Returns connection state, active-project provenance, native installed TIA products with versions and options, and access-profile availability."),
        McpT("list_devices", "Discovers all project devices and every PLC software target. Non-PLC devices are navigation metadata only."),
        McpT("list_plc_objects", "Returns the live included PLC software hierarchy with Siemens identity and content-availability metadata.",
            McpP("plc", "string", true, "Target PLC name, path, or Siemens object_id returned by list_devices")),
        McpT("find_plc_objects", "Searches live PLC object and tag/constant metadata without building an index or searching source text.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("query",    "string", false, "Optional name, path, type, or object_id text; omit or use * to return all matching filters"),
            McpP("type",     "string", false, "Optional object-type filter"),
            McpP("language", "string", false, "Optional programming-language filter"),
            McpP("group",    "string", false, "Optional hierarchy/group path filter")),
        McpT("read_plc_object", "Reads one PLC object through ordinary native TIA representations. At least one of objectId, path, or name is required; objectId takes precedence, while type only qualifies path/name selection. With format best, the server tries applicable formats in order until one complete representation succeeds; explicit formats never fall back. When TIA returns a protected native view, the response preserves protection and content-scope metadata. The MCP rejects passwords and unlock arguments; unlocking remains exclusively in the visible TIA UI.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("objectId", "string", false, "Preferred Siemens object_id selector; when supplied, it takes precedence over path, name, and type"),
            McpP("path",     "string", false, "Optional canonical object path selector"),
            McpP("name",     "string", false, "Optional object-name selector; must resolve uniquely"),
            McpP("type",     "string", false, "Optional object-type qualifier for path/name selection; it is not a selector by itself"),
            McpP("format",   "string", false, "Representation: best (default), simatic-sd, scl-source, or simaticml",
                "best", "best", "simatic-sd", "scl-source", "simaticml")),
        McpT("get_tag_table_entries", "Returns direct selected-field Openness views of a tag table's tags, user constants, and system constants. At least one of objectId, path, or table is required; objectId takes precedence, while path/table selection must resolve uniquely.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("objectId", "string", false, "Preferred tag-table Siemens object_id selector; when supplied, it takes precedence over path and table"),
            McpP("path",     "string", false, "Optional canonical tag-table path selector"),
            McpP("table",    "string", false, "Optional tag-table name selector; must resolve uniquely")),
        McpT("get_cross_references", "Queries native TIA cross-references on demand for one included object without compilation, source parsing, or a persisted call graph. At least one of objectId, path, or name is required; objectId takes precedence, while type only qualifies path/name selection.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("objectId", "string", false, "Preferred Siemens object_id selector; when supplied, it takes precedence over path, name, and type"),
            McpP("path",     "string", false, "Optional canonical object path selector"),
            McpP("name",     "string", false, "Optional object-name selector; must resolve uniquely"),
            McpP("type",     "string", false, "Optional object-type qualifier for path/name selection; it is not a selector by itself")),
    };
}

McpToolDefinition McpT(
    string name,
    string desc,
    params (string n, string t, bool r, string d, string? v, string[]? e)[] ps) => new()
{
    Name = name,
    Description = "DISABLED during rehaul transition; calls return prototype-mode. Former V1 contract: " + desc,
    InputSchema = new McpInputSchema {
        Type       = "object",
        Properties = ps.ToDictionary(
            p => p.n,
            p => McpPropertySchema(p.t, p.d, p.v, p.e)),
        Required   = ps.Where(p => p.r).Select(p => p.n).ToArray(),
        AdditionalProperties = false,
    }
};
(string n, string t, bool r, string d, string? v, string[]? e) McpP(
    string n,
    string t,
    bool r,
    string d,
    string? defaultValue = null,
    params string[] enumValues) =>
    (n, t, r, d, defaultValue, enumValues.Length == 0 ? null : enumValues);

object McpPropertySchema(
    string type,
    string description,
    string? defaultValue,
    string[]? enumValues)
{
    var schema = new Dictionary<string, object>
    {
        ["type"] = type,
        ["description"] = description,
    };
    if (enumValues is { Length: > 0 })
        schema["enum"] = enumValues;
    if (defaultValue is not null)
        schema["default"] = defaultValue;
    return schema;
}

// Publication hold: these eight disabled descriptors are retained until the complete eleven-tool cutover.
// No engineering dispatch exists here. The inert reference tree is never loaded.
Task<(object? result, object? rpcErr)> HandleMcpRequest(McpRpcRequest body)
{
    object? result = null;
    object? rpcErr = null;
    switch (body.Method)
    {
        case "initialize":
            var clientVersion = body.Params is { ValueKind: JsonValueKind.Object } parameters &&
                parameters.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString() : null;
            result = new { protocolVersion = clientVersion == "2024-11-05" ? "2024-11-05" : "2025-03-26",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "tia-portal-openness", version = "rehaul-transition" },
                instructions = "MCP publication is on hold. The eight descriptors are disabled V1 contracts. Use the dashboard for the implemented read-only rehaul increments." };
            break;
        case "ping": result = new { }; break;
        case "tools/list": result = new { tools = McpToolDefs() }; break;
        case "tools/call":
            var code = "invalidRequest";
            var message = "Supply a tool name and an optional arguments object.";
            if (body.Params is { ValueKind: JsonValueKind.Object } call &&
                call.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(name.GetString()) &&
                call.EnumerateObject().All(field => field.Name == "name" || field.Name == "arguments" || field.Name == "_meta") &&
                call.EnumerateObject().Select(field => field.Name).Distinct().Count() == call.EnumerateObject().Count() &&
                (!call.TryGetProperty("arguments", out var args) || args.ValueKind == JsonValueKind.Object))
            {
                var known = McpToolDefs().Any(tool => tool.Name == name.GetString());
                code = known ? "prototype-mode" : "unknownTool";
                message = known
                    ? "Rehaul transition: MCP execution is disabled. Use the dashboard. Restarting cannot restore V1."
                    : "This tool is not published. The eleven-tool rehaul cutover is pending.";
            }
            var payload = new { readAtUtc = DateTimeOffset.UtcNow,
                errors = new[] { new { origin = "bridge", operation = "tools/call", message } },
                error = new { code, message } };
            result = new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload, jsonOpts) } },
                isError = true };
            break;
        default: rpcErr = new { code = -32601, message = "Method not found: " + body.Method }; break;
    }
    return Task.FromResult((result, rpcErr));
}

class McpRpcRequest {
    [JsonPropertyName("jsonrpc")] public string       JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public object?      Id      { get; set; }
    [JsonPropertyName("method")]  public string       Method  { get; set; } = "";
    [JsonPropertyName("params")]  public JsonElement? Params  { get; set; }
}
class McpToolDefinition {
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public McpInputSchema InputSchema { get; set; } = new();
}
class McpInputSchema {
    public string Type { get; set; } = "object";
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.Ordinal);
    public string[] Required { get; set; } = Array.Empty<string>();
    public bool AdditionalProperties { get; set; }
}
