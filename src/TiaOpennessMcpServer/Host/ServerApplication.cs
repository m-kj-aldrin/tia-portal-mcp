using System.Text.Json;
using System.Text.Json.Serialization;
using Siemens.Engineering;
using TiaOpennessMcpServer.Dashboard;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Openness;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Host;

// One composition root owns the worker, service, dashboard and HTTP listener.
internal static class ServerApplication
{
    public static async Task RunAsync()
    {
        var port = 5000;
        var configuredPort = Environment.GetEnvironmentVariable("TIA_MCP_PORT")?.Trim();
        if (!string.IsNullOrEmpty(configuredPort) &&
            (!int.TryParse(configuredPort, out port) || port < 1 || port > 65535))
            throw new InvalidOperationException("TIA_MCP_PORT must be an integer between 1 and 65535.");

        var token = Environment.GetEnvironmentVariable("TIA_MCP_CONTROL_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(token) && token!.Length < 32)
            throw new InvalidOperationException("TIA_MCP_CONTROL_TOKEN must contain at least 32 characters.");

        var access = Environment.GetEnvironmentVariable("TIA_MCP_ACCESS") ?? "full";
        if (access != "full" && access != "read-only")
            throw new InvalidOperationException("TIA_MCP_ACCESS must be full or read-only.");

        var baseUri = new Uri($"http://127.0.0.1:{port}/");
        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        json.Converters.Add(new JsonStringEnumConverter());

        using var sta = new StaTaskScheduler();
        using var engineering = new EngineeringService(sta, new OpennessConnectionBackend(), access == "full");
        using var dashboard = new DashboardService(engineering);
        var http = new HttpResponses(json);
        var origins = new LoopbackOriginPolicy(baseUri);
        var boundary = new McpBoundary(engineering, json, ex => ex is EngineeringException, dashboard.RecordCall);
        var mcp = new McpEndpoint(boundary, http, origins);
        var dashboardEndpoints = new DashboardEndpoints(engineering, dashboard, http, origins, ex => ex is EngineeringException);
        using var host = new HttpHost(baseUri, token, http, mcp, dashboardEndpoints);
        await host.RunAsync();
    }
}
