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
    var mainForm = new MainForm(dashboardUri);
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
                var status = await tia.GetStatusV1Async(accessProfile);
                // Keep the dashboard's legacy aliases while making provenance the
                // authoritative v1 status shape. MCP get_status returns the typed
                // response directly.
                await Json(res, new
                {
                    status.Provenance,
                    status.Connected,
                    status.AccessProfile,
                    status.WriteToolsAvailable,
                    // REST/dashboard write availability remains distinct from
                    // the canonical MCP surface, which never has write tools.
                    writeEnabled,
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
    var unknownToolCallParameter = McpArgumentPolicy.FirstUnknown(
        new[] { "name", "arguments", "_meta" },
        p.EnumerateObject().Select(property => property.Name));
    if (unknownToolCallParameter is not null)
    {
        throw InvalidMcpRequest(
            $"Unknown tool-call parameter '{unknownToolCallParameter}'. " +
            "Only name, arguments, and protocol _meta are accepted.");
    }
    if (!p.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String ||
        string.IsNullOrWhiteSpace(n.GetString()))
        throw InvalidMcpRequest("A nonempty string tool name is required.");
    string name = n.GetString()!;
    JsonElement? args = p.TryGetProperty("arguments", out var a) ? a : (JsonElement?)null;
    if (args.HasValue &&
        args.Value.ValueKind is not JsonValueKind.Null and
        not JsonValueKind.Undefined and
        not JsonValueKind.Object)
    {
        throw InvalidMcpRequest("Tool arguments must be a JSON object.");
    }

    if (!V1McpToolPolicy.IsCanonical(name))
    {
        throw new InvalidOperationException(
            $"MCP tool '{name}' is unavailable in the locked canonical V1 surface. " +
            $"The available tools are: {string.Join(", ", V1McpToolPolicy.CanonicalNames)}.");
    }

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

    var toolDefinition = McpToolDefs().FirstOrDefault(tool => string.Equals(
        tool.Name,
        name,
        StringComparison.Ordinal));
    if (toolDefinition is not null && args?.ValueKind == JsonValueKind.Object)
    {
        var unknownArgument = McpArgumentPolicy.FirstUnknown(
            toolDefinition.InputSchema.Properties.Keys,
            args.Value.EnumerateObject().Select(property => property.Name));
        if (unknownArgument is not null)
        {
            throw InvalidMcpRequest(
                $"Unknown argument '{unknownArgument}' for MCP tool '{name}'. " +
                "Arguments not declared by the tool schema are rejected.");
        }
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
            return await tia.GetStatusV1Async(accessProfile);
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
        default: throw new InvalidOperationException($"Unknown tool: {name}");
    }
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
    Description = desc,
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

// ── Streamable HTTP MCP request handler ───────────────────────────────────────

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
                if (V1McpToolPolicy.IsCanonical(mcpTool))
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

V1ErrorEnvelope UnexpectedV1Envelope(Exception exception, string nativeMessage)
{
    var mapped = V1NativeFailurePolicy.Classify(
        nativeMessage,
        exception is MissingProductsException,
        exception is EngineeringSecurityException);
    var code = mapped == V1ErrorCodes.ExportFailed
        ? V1ErrorCodes.TiaOperationFailed
        : mapped;
    var message = code switch
    {
        V1ErrorCodes.MissingProductOrOption =>
            "The operation requires an installed TIA product, option, or support package that is unavailable.",
        V1ErrorCodes.UiAuthenticationRequired =>
            "TIA Portal requires external-access approval or interactive project authentication in the visible UI.",
        V1ErrorCodes.ProtectedContent =>
            "TIA Portal reported protected or inaccessible native content.",
        _ => "The canonical TIA Portal operation failed.",
    };
    return new V1ErrorEnvelope
    {
        Provenance = new V1Provenance { ReadAtUtc = DateTimeOffset.UtcNow },
        Error = new V1Error
        {
            Code = code,
            Message = message,
            NativeMessages = string.IsNullOrWhiteSpace(nativeMessage)
                ? Array.Empty<string>()
                : new[] { nativeMessage },
        },
    };
}

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
    public McpInputSchema InputSchema { get; set; } = new();
}
class McpInputSchema {
    public string Type { get; set; } = "object";
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.Ordinal);
    public string[] Required { get; set; } = Array.Empty<string>();
    public bool AdditionalProperties { get; set; }
}
