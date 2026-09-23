using System.Net;
using System.Text;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Host;

namespace TiaOpennessMcpServer.Mcp;

internal sealed class McpEndpoint
{
    private readonly McpRpcProcessor _messages;
    private readonly HttpResponses _http;
    private readonly LoopbackOriginPolicy _origins;
    public McpEndpoint(McpBoundary mcp, HttpResponses http, LoopbackOriginPolicy origins)
    { _messages = new McpRpcProcessor(mcp); _http = http; _origins = origins; }

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
            await _http.Json(res, McpRpcResponse.Failure(-32601,
                "MCP endpoint requires POST. Server: tia-portal-openness, protocol: 2025-03-26"), 405);
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
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var message = await _messages.ProcessAsync(await reader.ReadToEndAsync());
                if (message.Response == null)
                { res.StatusCode = message.StatusCode; res.Close(); return; }
                await _http.Json(res, message.Response, message.StatusCode);
            }
            catch (Exception ex) { try { await _http.Json(res, McpRpcResponse.Failure(-32603, ex.Message), 500); } catch { } }
        }

        else await _http.Json(res, new { error = "Not found" }, 404);
    }
}
