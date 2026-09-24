using System.Net;
using System.Text;
using System.Text.Json;
using TiaOpennessMcpServer.Host;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

internal sealed class DashboardEndpoints
{
    private readonly EngineeringService _service;
    private readonly DashboardService _dashboard;
    private readonly HttpResponses _http;
    private readonly LoopbackOriginPolicy _origins;
    private readonly Func<Exception?, bool> _isNative;
    private static readonly object AssetGate = new();
    private static readonly Dictionary<string, byte[]> AssetBytes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string File, string ContentType)> Assets = new(StringComparer.Ordinal)
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/dashboard/styles.css"] = ("styles.css", "text/css; charset=utf-8"),
        ["/dashboard/dashboard.js"] = ("dashboard.js", "text/javascript; charset=utf-8")
    };

    public DashboardEndpoints(EngineeringService service, DashboardService dashboard, HttpResponses http, LoopbackOriginPolicy origins, Func<Exception?, bool> isNative)
    { _service = service; _dashboard = dashboard; _http = http; _origins = origins; _isNative = isNative; }

    public async Task HandleAsync(HttpListenerContext ctx, string path)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";
        try
        {
            if (req.HttpMethod == "GET" && Assets.TryGetValue(path, out var asset))
            {
                await _http.WriteBytes(res, ReadAsset(asset.File), asset.ContentType);
            }
            else if (req.HttpMethod == "GET" && (path == "/api/status" || path == "/api/dashboard/status"))
                await _http.Json(res, _dashboard.Status());
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/dashboard")
                await _http.Json(res, _dashboard.Dashboard());
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/logs")
            {
                long after = 0;
                int generation = 0;
                long.TryParse(req.QueryString["after"], out after);
                int.TryParse(req.QueryString["generation"], out generation);
                await _http.Json(res, _dashboard.Logs(after < 0 ? 0 : after, generation));
            }
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/tool-forms")
                await _http.WriteBytes(res, Encoding.UTF8.GetBytes(DashboardToolForms.Render(McpBoundary.ToolDefs(_service.WriteToolsAvailable))), "text/html; charset=utf-8");
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/processes")
                await _http.Json(res, await _service.DiscoverAsync());
            else if (req.HttpMethod == "POST" &&
                     (path == "/api/dashboard/connect" || path == "/api/dashboard/disconnect" ||
                      path == "/api/dashboard/monitor" || path == "/api/dashboard/tabs/dismiss" ||
                      path == "/api/dashboard/projects/open"))
            {
                // Browser cross-origin forms cannot supply this header. No CORS permission is granted.
                var origin = req.Headers["Origin"];
                if (req.Headers["X-Tia-Dashboard"] != "1" ||
                    !_origins.Allows(origin))
                {
                    await _http.Json(res, new { error = "Use the dashboard on this server." }, 403);
                    return;
                }
                if (req.ContentLength64 <= 0 || req.ContentLength64 > 16384)
                    throw new ConnectionFault("invalidRequest", 0, "A JSON request body of at most 16 KiB is required.");
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                var root = body.RootElement;
                if (path == "/api/dashboard/tabs/dismiss")
                {
                    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                        !root.TryGetProperty("tabId", out var tabId) || tabId.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(tabId.GetString()))
                        throw new ConnectionFault("invalidRequest", 0, "Supply only the historical tabId to dismiss.");
                    await _http.Json(res, _dashboard.Dismiss(tabId.GetString()));
                    return;
                }
                if (path == "/api/dashboard/projects/open")
                {
                    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                        !root.TryGetProperty("tabId", out var openTab) || openTab.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(openTab.GetString()))
                        throw new ConnectionFault("invalidRequest", 0, "Supply only the closed project tabId to open.");
                    await _http.Json(res, await _dashboard.OpenProjectAsync(openTab.GetString()));
                    return;
                }
                if (path == "/api/dashboard/monitor")
                {
                    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                        !root.TryGetProperty("paused", out var paused) ||
                        (paused.ValueKind != JsonValueKind.True && paused.ValueKind != JsonValueKind.False))
                        throw new ConnectionFault("invalidRequest", 0, "Supply only a boolean paused field.");
                    await _http.Json(res, new { paused = await _dashboard.SetMonitoringPausedAsync(paused.GetBoolean()) });
                    return;
                }
                var request = DiscoveryRequest.Parse(root, device: false);
                var processId = request.ProcessId;
                if (path == "/api/dashboard/connect") await _http.Json(res, await _dashboard.ConnectAsync(processId));
                else await _http.Json(res, await _dashboard.DisconnectAsync(processId));
            }
            else
                await _http.Json(res, new { error = "Unknown dashboard route." }, 404);
        }
        catch (ConnectionFault ex)
        {
            var causeOrigin = _isNative(ex.InnerException) ? "tia-openness" : "bridge";
            var errors = new List<DiscoveryError>
            {
                new() { Origin = "bridge", Operation = path, Message = ex.Message }
            };
            if (ex.InnerException != null)
                errors.Add(new DiscoveryError { Origin = causeOrigin, Operation = path, Message = ex.InnerException.Message });
            await _http.Json(res, new { readAtUtc = DateTimeOffset.UtcNow, ex.ProcessId,
                errors,
                error = new { ex.Code, ex.Message, ex.ProcessId, ex.ReconnectRequired,
                causeOrigin = ex.InnerException == null ? null : causeOrigin,
                causeType = ex.InnerException?.GetType().Name, causeMessage = ex.InnerException?.Message } },
                ex.Code == "invalidRequest" ? 400 : ex.Code == "busy" ? 429 : 409);
        }
        catch (JsonException ex) { await _http.Json(res, new { error = new { code = "invalidRequest", message = ex.Message } }, 400); }
    }

    private static byte[] ReadAsset(string file)
    {
        lock (AssetGate)
        {
            if (AssetBytes.TryGetValue(file, out var cached)) return cached;
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dashboard", "wwwroot", file));
            AssetBytes[file] = bytes;
            return bytes;
        }
    }
}
