using System.Diagnostics;
using System.Net;
using System.Windows.Forms;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using TiaOpennessMcpServer;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Services;
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

bool stdioMode = Array.IndexOf(args, "--mcp-stdio") >= 0;
int httpPort = 5000;
var configuredPort = Environment.GetEnvironmentVariable("TIA_MCP_PORT")?.Trim();
if (!string.IsNullOrEmpty(configuredPort) &&
    (!int.TryParse(configuredPort, out httpPort) || httpPort < 1 || httpPort > 65535))
{
    throw new InvalidOperationException("TIA_MCP_PORT must be an integer between 1 and 65535.");
}
bool writeEnabled = string.Equals(
    Environment.GetEnvironmentVariable("TIA_MCP_ACCESS")?.Trim(),
    "full",
    StringComparison.OrdinalIgnoreCase);
string accessProfile = writeEnabled ? "full" : "read-only";

var readOnlyMcpTools = new HashSet<string>(StringComparer.Ordinal)
{
    "connect_to_tia_portal",
    "get_status",
    "list_devices",
    "list_plc_objects",
    "find_plc_objects",
    "read_plc_object",
    "get_tag_table_entries",
    "get_cross_references",
    "list_blocks",
    "read_block",
    "read_scl_source",
    "read_lad_source",
    "list_tag_tables",
    "get_tags",
    "analyze_scl",
    "analyze_block",
    "get_option_packages",
    "get_project_signature",
};

var canonicalV1McpTools = new HashSet<string>(StringComparer.Ordinal)
{
    "connect_to_tia_portal",
    "get_status",
    "list_devices",
    "list_plc_objects",
    "find_plc_objects",
    "read_plc_object",
    "get_tag_table_entries",
    "get_cross_references",
};

// ── DI setup ──────────────────────────────────────────────────────────────────
var services = new ServiceCollection();
services.AddLogging(b => { if (!stdioMode) b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
services.Configure<TiaOpennessOptions>(_ => { });
services.AddSingleton<StaTaskScheduler>();
services.AddSingleton<TiaPortalService>();
services.AddSingleton<V1BridgeService>();
services.AddSingleton<HardwareService>();
services.AddSingleton<SoftwareService>();
services.AddSingleton<SclAnalyzerService>();
services.AddSingleton<TagService>();
services.AddSingleton<HmiTagService>();
services.AddSingleton<HmiScreenService>();

var sp      = services.BuildServiceProvider();
var tia     = sp.GetRequiredService<TiaPortalService>();
var v1      = sp.GetRequiredService<V1BridgeService>();
var hw      = sp.GetRequiredService<HardwareService>();
var sw      = sp.GetRequiredService<SoftwareService>();
var scl     = sp.GetRequiredService<SclAnalyzerService>();
var tagSvc  = sp.GetRequiredService<TagService>();
var hmiSvc      = sp.GetRequiredService<HmiTagService>();
var hmiScreenSvc = sp.GetRequiredService<HmiScreenService>();

var mcpLog  = new List<McpLogEntry>();
var mcpLock = new object();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented               = false,
};
jsonOpts.Converters.Add(new JsonStringEnumConverter());

// ── Stdio MCP mode ────────────────────────────────────────────────────────────
if (stdioMode)
{
    await RunStdioAsync();
    sp.Dispose();
    return;
}

// ── HTTP listener ─────────────────────────────────────────────────────────────
var listener = new HttpListener();
var dashboardUri = new Uri($"http://127.0.0.1:{httpPort}/");
if (!dashboardUri.IsLoopback)
    throw new InvalidOperationException("The dashboard and HTTP MCP listener must bind to a loopback address.");
listener.Prefixes.Add(dashboardUri.AbsoluteUri);
listener.Start();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.Stop(); };

// Launch the WinForms window on a dedicated STA thread (required by WinForms/COM)
var uiThread = new System.Threading.Thread(() =>
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm(dashboardUri));
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

// ── Request dispatcher ────────────────────────────────────────────────────────

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

    Dictionary<string, string> m;

    try
    {
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            path.Equals("/api/project/clone", StringComparison.OrdinalIgnoreCase))
        {
            await Json(res, new
            {
                error = "Project cloning is quarantined because an externally attached project must never be saved or closed by this server."
            }, 410);
            return;
        }

        var isApiPath = path.Equals("/api", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        var isReadOnlyBlockAnalyze =
            method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            TryMatch(path, "/api/devices/{device}/blocks/{block}/analyze", out _);
        var isReadOnlyApiException = path.Equals("/api/connect", StringComparison.OrdinalIgnoreCase) ||
                                     path.Equals("/api/analyze", StringComparison.OrdinalIgnoreCase) ||
                                     isReadOnlyBlockAnalyze;
        if (!writeEnabled &&
            isApiPath &&
            !method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !isReadOnlyApiException)
        {
            await Json(res, new
            {
                error = "This REST operation is disabled by the read-only access profile. " +
                        "Set TIA_MCP_ACCESS=full before starting the server to enable writes.",
                accessProfile,
                writeEnabled,
            }, 403);
            return;
        }

        // ── Static file ───────────────────────────────────────────────────────
        if (method == "GET" && path == "/")
        {
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "dashboard.html");
            var html     = File.Exists(htmlPath)
                ? File.ReadAllText(htmlPath)
                : "<h1>dashboard.html not found next to the exe.</h1>";
            await WriteBytes(res, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");
        }

        // ── Status ────────────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/status")
        {
            try
            {
                var status = await tia.GetStatusV1Async(accessProfile, writeEnabled);
                // Keep the dashboard's legacy aliases while making provenance the
                // authoritative v1 status shape. MCP get_status returns the typed
                // response directly.
                await Json(res, new
                {
                    status.Provenance,
                    status.Connected,
                    status.AccessProfile,
                    status.WriteToolsAvailable,
                    writeEnabled = status.WriteToolsAvailable,
                    project = status.Provenance.Project,
                });
            }
            catch (V1BridgeException ex)
            {
                await WriteV1Error(res, ex);
            }
        }

        // ── Connect ───────────────────────────────────────────────────────────
        else if (method == "POST" && path == "/api/connect")
        {
            try
            {
                var body = await ReadJson<ConnectRequest>(req);
                await Json(res, await tia.ConnectV1Async(body?.ProjectPath));
            }
            catch (V1BridgeException ex) { await WriteV1Error(res, ex); }
        }

        // ── Devices ───────────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/devices")
        {
            try   { await Json(res, await hw.GetDevicesAsync()); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Blocks list ───────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/blocks", out m))
        {
            try   { await Json(res, await sw.ListBlocksAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block read ────────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/blocks/{block}", out m))
        {
            try   { await Json(res, await sw.ReadBlockAsync(m["device"], m["block"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block create ──────────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks", out m))
        {
            try
            {
                var body = await ReadJson<BlockCreateRequest>(req);
                if (body is null) { await Json(res, new { error = "Request body required." }, 400); return; }
                await Json(res, await sw.CreateBlockAsync(m["device"], body));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block SCL write ───────────────────────────────────────────────────
        else if (method == "PUT" && TryMatch(path, "/api/devices/{device}/blocks/{block}/scl", out m))
        {
            try
            {
                var body = await ReadJson<SclWriteRequest>(req);
                await sw.WriteBlockSclAsync(m["device"], m["block"], body?.Source ?? "");
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block compile ─────────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/{block}/xml", out m))
        {
            try
            {
                var body = await ReadJson<XmlWriteRequest>(req);
                await sw.WriteBlockXmlAsync(m["device"], m["block"], body?.Content ?? "");
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/{block}/compile", out m))
        {
            try   { await Json(res, new { result = await sw.CompileBlockAsync(m["device"], m["block"]) }); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block attribute diagnostics ───────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/blocks/{block}/attributes", out m))
        {
            try   { await Json(res, await sw.GetBlockAttributeInfosAsync(m["device"], m["block"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block texts (direct property patch — works on OBs) ────────────────
        else if (method == "PATCH" && TryMatch(path, "/api/devices/{device}/blocks/{block}/texts", out m))
        {
            try
            {
                var body = await ReadJson<BlockTextsRequest>(req);
                if (body is null) { await Json(res, new { error = "body required" }, 400); return; }
                await sw.PatchBlockTextsAsync(m["device"], m["block"], body);
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Block analyze ─────────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/{block}/analyze", out m))
        {
            try
            {
                var content = await sw.ReadBlockAsync(m["device"], m["block"]);
                if (string.IsNullOrWhiteSpace(content.SourceCode))
                { await Json(res, new { error = "Block is not SCL or source could not be read." }); return; }
                await Json(res, await scl.AnalyzeAsync(content.SourceCode, m["block"], content.Type.ToString()));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Create instance DB ────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/blocks/instance-db", out m))
        {
            try
            {
                var body = await ReadJson<InstanceDbCreateRequest>(req);
                if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.InstanceOfName))
                { await Json(res, new { error = "name and instanceOfName are required." }, 400); return; }
                await Json(res, await sw.CreateInstanceDbAsync(m["device"], body.Name, body.InstanceOfName, body.Number));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Tag tables ────────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/tags", out m))
        {
            try   { await Json(res, await tagSvc.GetTagTablesAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Tags in table ─────────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/tags/{table}", out m))
        {
            try   { await Json(res, await tagSvc.GetTagsAsync(m["device"], m["table"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Import tag table (XML content) ───────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/tags/import", out m))
        {
            try
            {
                var body = await ReadJson<TagImportRequest>(req);
                if (body is null || string.IsNullOrWhiteSpace(body.Content))
                { await Json(res, new { error = "content is required." }, 400); return; }
                await tagSvc.ImportTagTableFromContentAsync(m["device"], body.Content);
                await Json(res, new { success = true });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI tag tables (WinCC Unified) ───────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/tags", out m))
        {
            try   { await Json(res, await hmiSvc.ListTagTablesAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI all tags (flat) ───────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/tags/all", out m))
        {
            try   { await Json(res, await hmiSvc.GetAllTagsAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI tags in table ─────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/tags/{table}", out m))
        {
            try   { await Json(res, await hmiSvc.GetTagsAsync(m["device"], m["table"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI create tags in table ──────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/hmi/tags/{table}/create", out m))
        {
            try
            {
                var body = await ReadJson<List<HmiTagCreateRequest>>(req);
                if (body is null || body.Count == 0) { await Json(res, new { error = "body required: array of {name, dataType, plcTag}" }, 400); return; }
                await Json(res, await hmiSvc.CreateTagsAsync(m["device"], m["table"], body));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — list ────────────────────────────────────────────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/screens", out m))
        {
            try   { await Json(res, await hmiScreenSvc.ListScreensAsync(m["device"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — update faceplate interface parameters ───────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/hmi/screens/{screen}/update-faceplate-tags", out m))
        {
            try
            {
                var body = await ReadJson<List<FaceplateTagUpdate>>(req);
                if (body is null || body.Count == 0) { await Json(res, new { error = "body required: array of {containerName, parameterName, newValue}" }, 400); return; }
                await Json(res, await hmiScreenSvc.UpdateFaceplateTagsAsync(m["device"], m["screen"], body));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── HMI screens — tag dynamizations in a screen (read only) ─────────
        else if (method == "GET" && TryMatch(path, "/api/devices/{device}/hmi/screens/{screen}/tags", out m))
        {
            try   { await Json(res, await hmiScreenSvc.GetScreenTagRefsAsync(m["device"], m["screen"])); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Batch rename tags ─────────────────────────────────────────────────
        else if (method == "POST" && TryMatch(path, "/api/devices/{device}/tags/{table}/rename", out m))
        {
            try
            {
                var body = await ReadJson<TagBatchRenameRequest>(req);
                if (body is null || body.Renames.Count == 0)
                { await Json(res, new { error = "renames list is required." }, 400); return; }
                var count = await tagSvc.BatchRenameTagsAsync(m["device"], m["table"], body.Renames);
                await Json(res, new { renamed = count });
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Project signature ─────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/project/signature")
        {
            try   { await Json(res, await tia.GetProjectSignatureAsync()); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Save project ──────────────────────────────────────────────────────
        // ── Clone project ─────────────────────────────────────────────────────────
        else if (method == "POST" && path == "/api/project/clone")
        {
            try
            {
                var body = await ReadJson<CloneRequest>(req);
                if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Path))
                { await Json(res, new { error = "name and path are required." }); return; }
                await Json(res, await tia.CloneProjectAsync(body.Name, body.Path));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Option packages ───────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/project/options")
        {
            try   { await Json(res, await tia.GetOptionPackagesAsync()); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        else if (method == "POST" && path == "/api/project/save")
        {
            try   { await tia.SaveAsync(); await Json(res, new { success = true }); }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── Standalone SCL analysis ───────────────────────────────────────────
        else if (method == "POST" && path == "/api/analyze")
        {
            try
            {
                var body = await ReadJson<SclAnalyzeRequest>(req);
                await Json(res, await scl.AnalyzeAsync(
                    body?.Source ?? "", body?.BlockName ?? "Block", body?.BlockType ?? "FB"));
            }
            catch (Exception ex) { await Json(res, new { error = ex.Message }); }
        }

        // ── MCP endpoint info (GET) ───────────────────────────────────────────────
        else if (method == "GET" && path == "/mcp")
        {
            // Return a recognisable MCP error so clients detect the modern Streamable HTTP
            // transport and don't fall back to the old HTTP+SSE discovery flow.
            res.StatusCode = 405;
            await Json(res, new {
                jsonrpc = "2.0", id = (object?)null,
                error   = new { code = -32601, message = "MCP endpoint requires POST. Server: tia-portal-openness v1.0.0, protocol: 2025-03-26" }
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

        // ── MCP call log ──────────────────────────────────────────────────────────
        else if (method == "GET" && path == "/api/mcp/log")
        {
            List<McpLogEntry> snapshot;
            lock (mcpLock) { snapshot = mcpLog.Take(50).ToList(); }
            await Json(res, snapshot);
        }

        else
        {
            await Json(res, new { error = "Not found" }, 404);
        }
    }
    catch (V1BridgeException ex)
    {
        try { await WriteV1Error(res, ex); } catch { }
    }
    catch (Exception ex)
    {
        try { await Json(res, new { error = ex.Message }, 500); } catch { }
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

async Task Json(HttpListenerResponse res, object? data, int status = 200)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, jsonOpts));
    res.StatusCode = status;
    res.ContentType = "application/json; charset=utf-8";
    await WriteBytes(res, bytes, res.ContentType);
}

async Task WriteV1Error(HttpListenerResponse res, V1BridgeException exception)
    => await Json(res, exception.ToEnvelope(), V1HttpStatus(exception.Error.Code));

int V1HttpStatus(string code) => code switch
{
    V1ErrorCodes.InvalidRequest or V1ErrorCodes.InvalidSelector => 400,
    V1ErrorCodes.UiAuthenticationRequired => 401,
    V1ErrorCodes.ProtectedContent => 403,
    V1ErrorCodes.ObjectNotFound => 404,
    V1ErrorCodes.AmbiguousSelector or
    V1ErrorCodes.AmbiguousProject or
    V1ErrorCodes.ProjectConflict or
    V1ErrorCodes.NoActiveProject or
    V1ErrorCodes.IncompatibleProject or
    V1ErrorCodes.UpgradeRequired => 409,
    V1ErrorCodes.UnsupportedObject or
    V1ErrorCodes.UnsupportedFormat or
    V1ErrorCodes.ExportFailed or
    V1ErrorCodes.PartialExport => 422,
    V1ErrorCodes.MissingProductOrOption => 424,
    _ => 500,
};

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

bool TryMatch(string path, string pattern, out Dictionary<string, string> vars)
{
    vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var ps = path.Split('/');
    var pp = pattern.Split('/');
    if (ps.Length != pp.Length) return false;
    for (int i = 0; i < pp.Length; i++)
    {
        if (pp[i].StartsWith("{") && pp[i].EndsWith("}"))
            vars[pp[i].Substring(1, pp[i].Length - 2)] = Uri.UnescapeDataString(ps[i]);
        else if (!string.Equals(ps[i], pp[i], StringComparison.OrdinalIgnoreCase))
            return false;
    }
    return true;
}

V1BridgeException InvalidMcpRequest(string message) => new(new V1Error
{
    Code = V1ErrorCodes.InvalidRequest,
    Message = message,
}, new V1Provenance { ReadAtUtc = DateTimeOffset.UtcNow });

async Task<object?> McpDispatch(JsonElement p)
{
    if (p.ValueKind != JsonValueKind.Object)
        throw InvalidMcpRequest("Tool-call params must be a JSON object.");
    if (!p.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(n.GetString()))
        throw InvalidMcpRequest("A nonempty string tool name is required.");
    string name = n.GetString()!;
    JsonElement? args = p.TryGetProperty("arguments", out var a) ? a : (JsonElement?)null;
    string A(string key, string def = "")
    {
        if (!args.HasValue || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return def;
        if (args.Value.ValueKind != JsonValueKind.Object)
            throw InvalidMcpRequest("Tool arguments must be a JSON object.");
        if (!args.Value.TryGetProperty(key, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return def;
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidMcpRequest($"Argument '{key}' must be a string.");
        return value.GetString() ?? def;
    }
    string? O(string key)
    {
        var value = A(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    V1ObjectSelector ObjectSelector(string? nameAlias = null) => new()
    {
        ObjectId = O("objectId"),
        Path = O("path"),
        Name = O("name") ?? (nameAlias is null ? null : O(nameAlias)),
        Type = O("type"),
    };

    if (!writeEnabled && !readOnlyMcpTools.Contains(name))
    {
        throw new InvalidOperationException(
            $"MCP tool '{name}' is disabled by the read-only access profile. " +
            "Set TIA_MCP_ACCESS=full before starting the server to enable mutating tools.");
    }

    if (name == "clone_project")
    {
        throw new InvalidOperationException(
            "clone_project is quarantined because an externally attached project must never be saved or closed by this server.");
    }

    if (!tia.IsConnected && name is
        "list_devices" or
        "list_plc_objects" or
        "find_plc_objects" or
        "read_plc_object" or
        "get_tag_table_entries" or
        "get_cross_references")
    {
        throw new V1BridgeException(new V1Error
        {
            Code = V1ErrorCodes.NoActiveProject,
            Message = "No TIA Portal project is active. Call connect_to_tia_portal first.",
        }, new V1Provenance { ReadAtUtc = DateTimeOffset.UtcNow });
    }

    switch (name)
    {
        case "connect_to_tia_portal":
            return await tia.ConnectV1Async(O("projectPath"));
        case "get_status":
            return await tia.GetStatusV1Async(accessProfile, writeEnabled);
        case "save_project":           await tia.SaveAsync(); return new { success = true };
        case "list_devices":           return await v1.ListDevicesAsync();
        case "list_plc_objects":       return await v1.ListPlcObjectsAsync(A("plc"));
        case "find_plc_objects":       return await v1.FindPlcObjectsAsync(
            A("plc"), O("query"), O("type"), O("language"), O("group"));
        case "read_plc_object":        return await v1.ReadPlcObjectAsync(
            A("plc"), ObjectSelector(), O("format"));
        case "get_tag_table_entries":  return await v1.GetTagTableEntriesAsync(
            A("plc"), ObjectSelector("table"));
        case "get_cross_references":   return await v1.GetCrossReferencesAsync(
            A("plc"), ObjectSelector());
        case "list_blocks":            return await sw.ListBlocksAsync(A("device"));
        case "read_block":             return await sw.ReadBlockAsync(A("device"), A("block"));
        case "read_scl_source":        return await sw.ReadSclSourceAsync(A("device"), A("block"));
        case "read_lad_source":        return await sw.ReadLadSourceAsync(A("device"), A("block"));
        case "write_block_scl":        await sw.WriteBlockSclAsync(A("device"), A("block"), A("source")); return new { success = true };
        case "import_block_xml":       await sw.WriteBlockXmlAsync(A("device"), A("block"), A("content")); return new { success = true };
        case "compile_block":          return new { result = await sw.CompileBlockAsync(A("device"), A("block")) };
        case "analyze_block":
        {
            var blk = await sw.ReadBlockAsync(A("device"), A("block"));
            if (string.IsNullOrWhiteSpace(blk.SourceCode))
                return new { error = "Block is not SCL or source could not be read." };
            return await scl.AnalyzeAsync(blk.SourceCode, A("block"), blk.Type.ToString());
        }
        case "create_block":
            return await sw.CreateBlockAsync(A("device"), new BlockCreateRequest {
                Name       = A("name"),
                Type       = (BlockType)Enum.Parse(typeof(BlockType), A("type", "FB"), ignoreCase: true),
                Language   = ProgrammingLanguage.SCL,
                Number     = int.TryParse(A("number"), out var nb) ? (int?)nb : null,
                SourceCode = A("sourceCode")
            });
        case "list_tag_tables":        return await tagSvc.GetTagTablesAsync(A("device"));
        case "get_tags":               return await tagSvc.GetTagsAsync(A("device"), A("table"));
        case "analyze_scl":            return await scl.AnalyzeAsync(A("source"), A("blockName", "Block"), A("blockType", "FB"));
        case "clone_project":          return await tia.CloneProjectAsync(A("name"), A("path"));
        case "get_option_packages":    return await tia.GetOptionPackagesAsync();
        case "get_project_signature":  return await tia.GetProjectSignatureAsync();
        case "create_instance_db":
            return await sw.CreateInstanceDbAsync(
                A("device"), A("name"), A("instanceOfName"),
                int.TryParse(A("number"), out var idb) ? (int?)idb : null);
        case "import_tag_table":
            await tagSvc.ImportTagTableFromContentAsync(A("device"), A("content"));
            return new { success = true };
        case "batch_rename_tags":
        {
            var rawRenames = args.HasValue && args.Value.TryGetProperty("renames", out var rv)
                ? JsonSerializer.Deserialize<List<TagRenameItem>>(rv.GetRawText(), jsonOpts) ?? new()
                : new List<TagRenameItem>();
            var renamed = await tagSvc.BatchRenameTagsAsync(A("device"), A("table"), rawRenames);
            return new { renamed };
        }
        default: throw new InvalidOperationException($"Unknown tool: {name}");
    }
}

List<McpToolDefinition> McpToolDefs()
{
    var tools = new List<McpToolDefinition>
    {
        McpT("connect_to_tia_portal", "Reuses the active project, attaches to an exact open-project match, or visibly opens the supplied compatible project path when no project is active.",
            McpP("projectPath", "string", false, "Optional exact project path. Credentials remain exclusively in the visible TIA Portal UI.")),
        McpT("get_status",   "Returns connection state, active-project provenance, installed TIA products and updates, and access-profile availability."),
        McpT("save_project", "Saves the currently open TIA Portal project."),
        McpT("list_devices", "Discovers all project devices and every PLC software target. Non-PLC devices are navigation metadata only."),
        McpT("list_plc_objects", "Returns the live included PLC software hierarchy with Siemens identity and content-availability metadata.",
            McpP("plc", "string", true, "Target PLC name, path, or Siemens object_id returned by list_devices")),
        McpT("find_plc_objects", "Searches live PLC object and tag/constant metadata without building an index or searching source text.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("query",    "string", false, "Optional name, path, type, or object_id text; omit or use * to return all matching filters"),
            McpP("type",     "string", false, "Optional object-type filter"),
            McpP("language", "string", false, "Optional programming-language filter"),
            McpP("group",    "string", false, "Optional hierarchy/group path filter")),
        McpT("read_plc_object", "Reads one PLC object as a complete native representation. best records ordered SIMATIC SD, applicable raw SCL, and SimaticML attempts; explicit formats are strict.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("objectId", "string", false, "Preferred Siemens object_id selector"),
            McpP("path",     "string", false, "Optional canonical object path selector"),
            McpP("name",     "string", false, "Optional object-name selector; must resolve uniquely"),
            McpP("type",     "string", false, "Optional object-type selector qualifier"),
            McpP("format",   "string", false, "Representation: best (default), simatic-sd, scl-source, or simaticml",
                "best", "best", "simatic-sd", "scl-source", "simaticml")),
        McpT("get_tag_table_entries", "Returns direct selected-field Openness views of a tag table's tags, user constants, and system constants.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("objectId", "string", false, "Preferred tag-table Siemens object_id selector"),
            McpP("path",     "string", false, "Optional canonical tag-table path selector"),
            McpP("table",    "string", false, "Optional tag-table name selector; must resolve uniquely")),
        McpT("get_cross_references", "Queries native TIA cross-references on demand for one included object without compilation, source parsing, or a persisted call graph.",
            McpP("plc",      "string", true,  "Target PLC name, path, or Siemens object_id"),
            McpP("objectId", "string", false, "Preferred Siemens object_id selector"),
            McpP("path",     "string", false, "Optional canonical object path selector"),
            McpP("name",     "string", false, "Optional object-name selector; must resolve uniquely"),
            McpP("type",     "string", false, "Optional object-type selector qualifier")),
        McpT("list_blocks",  "Lists all blocks (OB, FB, FC, DB) on a device.",
            McpP("device", "string", true, "Device name as shown in TIA Portal")),
        McpT("read_block", "Reads a block's source code, XML, language, type, and number.",
            McpP("device", "string", true, "Device name"),
            McpP("block",  "string", true, "Block name")),
        McpT("read_scl_source", "Generates and returns the complete authoritative raw source for a pure SCL block without changing the project.",
            McpP("device", "string", true, "Device name"),
            McpP("block",  "string", true, "Block name")),
        McpT("read_lad_source", "Exports a pure LAD block read-only as SIMATIC SD and returns the .s7dcl source plus available .s7res resource/comment documents.",
            McpP("device", "string", true, "Device name"),
            McpP("block",  "string", true, "Block name")),
        McpT("write_block_scl", "Overwrites a block's SCL source. Call compile_block afterwards to apply.",
            McpP("device", "string", true, "Device name"),
            McpP("block",  "string", true, "Block name"),
            McpP("source", "string", true, "Full SCL source text")),
        McpT("import_block_xml", "Imports raw SimaticML XML into a block. Use for LAD/FBD/STL blocks.",
            McpP("device",  "string", true, "Device name"),
            McpP("block",   "string", true, "Block name"),
            McpP("content", "string", true, "Full SimaticML XML content")),
        McpT("compile_block", "Compiles a block and returns compiler output with error line numbers.",
            McpP("device", "string", true, "Device name"),
            McpP("block",  "string", true, "Block name")),
        McpT("analyze_block", "Derived non-authoritative utility: runs static SCL analysis on a block without compiling it.",
            McpP("device", "string", true, "Device name"),
            McpP("block",  "string", true, "Block name")),
        McpT("create_block", "Creates a new SCL block on a device.",
            McpP("device",      "string", true,  "Device name"),
            McpP("name",        "string", true,  "New block name"),
            McpP("type",        "string", true,  "Block type: FB, FC, OB, or GlobalDB"),
            McpP("sourceCode",  "string", true,  "Full SCL source"),
            McpP("number",      "string", false, "Block number (optional integer)")),
        McpT("list_tag_tables", "Lists all tag tables on a device with their names and tag counts.",
            McpP("device", "string", true, "Device name")),
        McpT("get_tags", "Returns all tags in a tag table with type, address, and comment.",
            McpP("device", "string", true, "Device name"),
            McpP("table",  "string", true, "Tag table name")),
        McpT("analyze_scl", "Derived non-authoritative utility: runs static analysis on supplied SCL text without needing an open block.",
            McpP("source",     "string", true,  "SCL source code"),
            McpP("blockName",  "string", false, "Block name for context"),
            McpP("blockType",  "string", false, "Block type: FB, FC, OB, or GlobalDB")),
        McpT("clone_project", "Experimental project reconstruction; quarantined for externally attached projects.",
            McpP("name", "string", true, "New project name"),
            McpP("path", "string", true, "Destination folder path")),
        McpT("get_option_packages", "Lists all option packages and used products referenced by the project."),
        McpT("get_project_signature", "Returns a full index of every block and tag table on every device — names, numbers, languages, and consistency state."),
        McpT("create_instance_db", "Creates a new Instance DB linked to an FB.",
            McpP("device",          "string", true,  "Device name"),
            McpP("name",            "string", true,  "Instance DB name"),
            McpP("instanceOfName",  "string", true,  "FB name this DB is an instance of"),
            McpP("number",          "string", false, "DB number (optional)")),
        McpT("import_tag_table", "Imports a complete tag table from SimaticML XML content (creates or replaces).",
            McpP("device",   "string", true, "Device name"),
            McpP("content",  "string", true, "SimaticML XML for the tag table")),
        McpT("batch_rename_tags", "Rebuilds and reimports a tag table with multiple tag names changed.",
            McpP("device",   "string", true, "Device name"),
            McpP("table",    "string", true, "Tag table name"),
            McpP("renames",  "array",  true, "Array of {from, to} rename pairs")),
    };

    return writeEnabled
        ? tools.Where(t => t.Name != "clone_project").ToList()
        : tools.Where(t => readOnlyMcpTools.Contains(t.Name)).ToList();
}

McpToolDefinition McpT(
    string name,
    string desc,
    params (string n, string t, bool r, string d, string? v, string[]? e)[] ps) => new()
{
    Name = name,
    Description = desc,
    InputSchema = new {
        type       = "object",
        properties = ps.ToDictionary(
            p => p.n,
            p => McpPropertySchema(p.t, p.d, p.v, p.e)),
        required   = ps.Where(p => p.r).Select(p => p.n).ToArray(),
        additionalProperties = false,
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

// ── Shared MCP request handler (used by both HTTP and stdio) ──────────────────

async Task<(object? result, object? rpcErr)> HandleMcpRequest(McpRpcRequest body)
{
    object? result = null;
    object? rpcErr = null;
    string  mcpTool = "";
    switch (body.Method)
    {
        case "initialize":
        {
            // Echo back the client's requested version if we support it.
            var clientPv = body.Params.HasValue &&
                           body.Params.Value.TryGetProperty("protocolVersion", out var pvEl)
                ? pvEl.GetString() ?? "2025-03-26" : "2025-03-26";
            var responsePv = clientPv == "2024-11-05" ? "2024-11-05" : "2025-03-26";
            result = new {
                protocolVersion = responsePv,
                capabilities    = new { tools = new { } },
                serverInfo      = new { name = "tia-portal-openness", version = "1.0.0" }
            };
            break;
        }
        case "ping":
            result = new { };
            break;
        case "tools/list":
            result = new { tools = McpToolDefs() };
            break;
        case "tools/call":
            if (!body.Params.HasValue)
            { rpcErr = new { code = -32602, message = "Missing params" }; break; }
            var timer = Stopwatch.StartNew();
            try
            {
                if (body.Params.Value.ValueKind == JsonValueKind.Object &&
                    body.Params.Value.TryGetProperty("name", out var tn) &&
                    tn.ValueKind == JsonValueKind.String)
                    mcpTool = tn.GetString() ?? "";

                var callResult = await McpDispatch(body.Params.Value);
                timer.Stop();
                var txt = JsonSerializer.Serialize(callResult, jsonOpts);
                var attemptsSummary = McpAttemptsSummary(callResult);
                result = new { content = new[] { new { type = "text", text = txt } }, isError = false };
                AddMcpLog(new McpLogEntry
                {
                    Tool = mcpTool,
                    At = DateTime.UtcNow,
                    Success = true,
                    DurationMs = timer.ElapsedMilliseconds,
                    AttemptsSummary = attemptsSummary,
                });
            }
            catch (V1BridgeException ex)
            {
                timer.Stop();
                var envelope = ex.ToEnvelope();
                var txt = JsonSerializer.Serialize(envelope, jsonOpts);
                result = new { content = new[] { new { type = "text", text = txt } }, isError = true };
                AddMcpLog(new McpLogEntry
                {
                    Tool = mcpTool,
                    At = DateTime.UtcNow,
                    Success = false,
                    Error = ex.Error.Message,
                    DurationMs = timer.ElapsedMilliseconds,
                    AttemptsSummary = FormatAttempts(ex.Error.Attempts),
                });
            }
            catch (Exception ex)
            {
                timer.Stop();
                var msg = SafeSingleLine(ex.Message);
                if (canonicalV1McpTools.Contains(mcpTool))
                {
                    var envelope = UnexpectedV1Envelope(ex, msg);
                    var txt = JsonSerializer.Serialize(envelope, jsonOpts);
                    result = new { content = new[] { new { type = "text", text = txt } }, isError = true };
                }
                else
                {
                    result = new { content = new[] { new { type = "text", text = msg } }, isError = true };
                }
                AddMcpLog(new McpLogEntry
                {
                    Tool = mcpTool,
                    At = DateTime.UtcNow,
                    Success = false,
                    Error = msg,
                    DurationMs = timer.ElapsedMilliseconds,
                });
            }
            break;
        default:
            rpcErr = new { code = -32601, message = $"Method not found: {body.Method}" };
            break;
    }
    return (result, rpcErr);
}

V1ErrorEnvelope UnexpectedV1Envelope(Exception exception, string nativeMessage) => new()
{
    Provenance = new V1Provenance { ReadAtUtc = DateTimeOffset.UtcNow },
    Error = new V1Error
    {
        Code = exception is MissingProductsException
            ? V1ErrorCodes.MissingProductOrOption
            : exception is EngineeringSecurityException
                ? V1ErrorCodes.UiAuthenticationRequired
                : V1ErrorCodes.TiaOperationFailed,
        Message = exception is MissingProductsException
            ? "The operation requires an installed TIA product, option, or support package that is unavailable."
            : exception is EngineeringSecurityException
                ? "TIA Portal requires external-access approval or interactive authentication in the visible UI."
                : "The canonical TIA Portal operation failed.",
        NativeMessages = string.IsNullOrWhiteSpace(nativeMessage)
            ? Array.Empty<string>()
            : new[] { nativeMessage },
    },
};

string SafeSingleLine(string? message) =>
    (message ?? "")
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();

void AddMcpLog(McpLogEntry entry)
{
    lock (mcpLock)
    {
        mcpLog.Insert(0, entry);
        if (mcpLog.Count > 200)
            mcpLog.RemoveAt(mcpLog.Count - 1);
    }
}

string? McpAttemptsSummary(object? response) => response switch
{
    V1ReadPlcObjectResponse read => FormatAttempts(read.Attempts),
    V1ErrorEnvelope error => FormatAttempts(error.Error.Attempts),
    _ => null,
};

string? FormatAttempts(IReadOnlyList<V1Attempt>? attempts)
{
    if (attempts is null || attempts.Count == 0)
        return null;
    return string.Join(" -> ", attempts.Select(attempt => $"{attempt.Format}:{attempt.Result}"));
}

// ── Stdio MCP loop ─────────────────────────────────────────────────────────────

async Task RunStdioAsync()
{
    var stdin  = new System.IO.StreamReader(Console.OpenStandardInput(),  new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

    string? line;
    while ((line = await stdin.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        McpRpcRequest? req;
        try   { req = JsonSerializer.Deserialize<McpRpcRequest>(line, jsonOpts); }
        catch { await WriteStdio(stdout, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); continue; }
        if (req is null) continue;

        // Notifications have no id — acknowledge silently
        if (req.Id is null && (req.Method?.StartsWith("notifications/") ?? false)) continue;

        var (result, rpcErr) = await HandleMcpRequest(req);
        if (rpcErr != null)
            await WriteStdio(stdout, new { jsonrpc = "2.0", id = req.Id, error = rpcErr });
        else
            await WriteStdio(stdout, new { jsonrpc = "2.0", id = req.Id, result });
    }
}

async Task WriteStdio(System.IO.StreamWriter w, object obj)
    => await w.WriteLineAsync(JsonSerializer.Serialize(obj, jsonOpts));

// ── Request body DTOs ─────────────────────────────────────────────────────────

class SclWriteRequest   { public string Source    { get; set; } = ""; }
class ConnectRequest    { public string? ProjectPath { get; set; } }
class SclAnalyzeRequest { public string Source    { get; set; } = "";
                          public string BlockName { get; set; } = "Block";
                          public string BlockType { get; set; } = "FB"; }
class XmlWriteRequest   { public string Content   { get; set; } = ""; }
class CloneRequest      { public string Name      { get; set; } = ""; public string Path { get; set; } = ""; }

class TagImportRequest        { public string Content      { get; set; } = ""; }
class TagBatchRenameRequest   { public List<TagRenameItem> Renames { get; set; } = new(); }
class InstanceDbCreateRequest { public string Name           { get; set; } = "";
                                public string InstanceOfName { get; set; } = "";
                                public int?   Number         { get; set; } }

class McpRpcRequest {
    [JsonPropertyName("jsonrpc")] public string       JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public object?      Id      { get; set; }
    [JsonPropertyName("method")]  public string       Method  { get; set; } = "";
    [JsonPropertyName("params")]  public JsonElement? Params  { get; set; }
}
class McpLogEntry {
    public string   Tool    { get; set; } = "";
    public DateTime At      { get; set; }
    public bool     Success { get; set; }
    public string?  Error   { get; set; }
    public long     DurationMs { get; set; }
    public string?  AttemptsSummary { get; set; }
}
class McpToolDefinition {
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public object InputSchema { get; set; } = new { };
}
