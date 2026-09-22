using System.Net;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Host;

namespace TiaOpennessMcpServer.Mcp;

internal sealed class McpEndpoint
{
    private readonly McpBoundary _mcp;
    private readonly HttpResponses _http;
    private readonly LoopbackOriginPolicy _origins;
    public McpEndpoint(McpBoundary mcp, HttpResponses http, LoopbackOriginPolicy origins)
    { _mcp = mcp; _http = http; _origins = origins; }

    public async Task HandleAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;
        var method = req.HttpMethod;
        // ── MCP endpoint info (GET) ───────────────────────────────────────────────
        if (method == "GET")
        {
            // Return a recognisable MCP error so clients detect the modern Streamable HTTP
            // transport and don't fall back to the old HTTP+SSE discovery flow.
            res.StatusCode = 405;
            await _http.Json(res, new {
                jsonrpc = "2.0", id = (object?)null,
                error   = new { code = -32601, message = "MCP endpoint requires POST. Server: tia-portal-openness rehaul, protocol: 2025-03-26" }
            }, 405);
        }

        // ── MCP JSON-RPC 2.0 (Streamable HTTP) ───────────────────────────────────
        else if (method == "POST")
        {
            var origin = req.Headers["Origin"];
            if (!_origins.Allows(origin))
            { await _http.Json(res, new { error = "Cross-origin MCP requests are not allowed." }, 403); return; }
            if (!string.Equals(req.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            { await _http.Json(res, new { error = "MCP requires application/json." }, 415); return; }
            using var call = OperationCallContext.Begin(req.Headers["X-Tia-Dashboard"] == "1" ? "dashboard" : "mcp");
            try
            {
                var body = await _http.ReadJson<McpRpcRequest>(req);
                if (body is null)
                { await _http.Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } }); return; }

                // Notifications have no id — acknowledge and return
                if (body.Id is null && (body.Method?.StartsWith("notifications/") ?? false))
                { res.StatusCode = 202; res.Close(); return; }

                var (result, rpcErr) = await _mcp.HandleAsync(body);
                if (rpcErr != null)
                    await _http.Json(res, new { jsonrpc = "2.0", id = body.Id, error = rpcErr });
                else
                    await _http.Json(res, new { jsonrpc = "2.0", id = body.Id, result });
            }
            catch (Exception ex) { try { await _http.Json(res, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = ex.Message } }, 500); } catch { } }
        }

        else await _http.Json(res, new { error = "Not found" }, 404);
    }
}
