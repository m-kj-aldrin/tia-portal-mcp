using System.Text.Json;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;

namespace TiaOpennessMcpServer.Dashboard;

// Admission is dashboard-specific. Tool publication, schema validation and
// engineering dispatch remain exclusively in the composed McpBoundary.
internal sealed class DashboardToolRunner
{
    private readonly DashboardService _dashboard;
    private readonly DashboardRunStore _runs;
    private readonly McpBoundary _mcp;
    private readonly JsonSerializerOptions _json;

    internal DashboardToolRunner(DashboardService dashboard, DashboardRunStore runs,
        McpBoundary mcp, JsonSerializerOptions json)
    { _dashboard = dashboard; _runs = runs; _mcp = mcp; _json = json; }

    internal DashboardRunCapture Begin(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid("Supply one JSON object for a dashboard tool action.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!fields.Add(field.Name) || field.Name is not ("tabId" or "requestId" or "name" or "arguments"))
                throw Invalid("Unknown or duplicate dashboard tool-action field: " + field.Name);
        if (fields.Count != 4 || !Text(root, "tabId", out var tabId) ||
            !Text(root, "requestId", out var requestId) || !Text(root, "name", out var name) ||
            !root.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw Invalid("Supply tabId, requestId, name and an arguments object.");
        if (tabId!.Length > 64 || name!.Length > 128 ||
            !Guid.TryParseExact(requestId, "D", out var requestGuid) || requestGuid == Guid.Empty)
            throw Invalid("The tabId or tool name is too long, or requestId is not a UUID.");
        var tab = _dashboard.CurrentDashboard().Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab == null) throw Invalid("The selected dashboard tab no longer exists.");
        int? processId = null;
        if (arguments.TryGetProperty("processId", out var process))
        {
            if (process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var parsed) || parsed <= 0)
                throw Invalid("arguments.processId must be a positive integer.");
            if (!tab.Live || tab.ProcessId != parsed)
                throw Invalid("arguments.processId must match the selected live dashboard tab.");
            processId = parsed;
        }
        var capture = _runs.TryStart(requestId!, tabId!, name!, processId, root.GetRawText(), out var rejection);
        if (capture == null)
            throw new ConnectionFault(rejection == "Four dashboard runs are already active." ? "busy" : "invalidRequest", 0, rejection!);
        return capture;
    }

    internal async Task<DashboardRunCompletion> RunAsync(DashboardRunCapture started)
    {
        string responseJson;
        try
        {
            using var request = JsonDocument.Parse(started.RequestJson!);
            var root = request.RootElement;
            var args = root.GetProperty("arguments").Clone();
            var parameters = JsonSerializer.SerializeToElement(new { name = started.Operation, arguments = args }, _json);
            using var call = OperationCallContext.Begin("dashboard");
            var (result, rpcErr) = await _mcp.HandleAsync(new McpRpcRequest
            {
                Id = started.Id,
                Method = "tools/call",
                Params = parameters
            }).ConfigureAwait(false);
            responseJson = JsonSerializer.Serialize(new McpRpcResponse
            {
                Id = started.Id, Result = result, Error = rpcErr
            }, _json);
        }
        catch (Exception ex)
        {
            // Even an unexpected bridge exception completes this admitted run
            // exactly once. A disconnected POST does not cancel or retry it.
            responseJson = JsonSerializer.Serialize(McpRpcResponse.Failure(-32603, ex.Message, started.Id), _json);
        }
        var (outcome, error) = Classify(responseJson);
        var finished = _runs.Finish(started.Id, outcome, responseJson, error);
        // The initiating action may display a large result once even when the
        // history store retains metadata only. Reconnect never replays it.
        if (!finished.PayloadRetained)
        {
            finished.RequestJson = started.RequestJson;
            finished.ResponseJson = responseJson;
        }
        return new DashboardRunCompletion(finished, responseJson);
    }

    internal static (string Outcome, string? Error) Classify(string responseJson)
    {
        try
        {
            using var response = JsonDocument.Parse(responseJson);
            var root = response.RootElement;
            if (root.TryGetProperty("error", out var protocol))
                return ("error", Message(protocol));
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return ("error", "The MCP boundary returned no result.");
            var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array ||
                content.GetArrayLength() == 0 || !content[0].TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
                return (isError ? "error" : "success", null);
            using var payload = JsonDocument.Parse(text.GetString() ?? "{}");
            var data = payload.RootElement;
            var incomplete = data.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.False;
            var errors = data.TryGetProperty("errors", out var errorList) && errorList.ValueKind == JsonValueKind.Array && errorList.GetArrayLength() > 0;
            var affected = data.TryGetProperty("affectedObjects", out var objects) && objects.ValueKind == JsonValueKind.Array && objects.GetArrayLength() > 0;
            var compilationFailed = data.TryGetProperty("compilationSucceeded", out var compiled) && compiled.ValueKind == JsonValueKind.False;
            var message = data.TryGetProperty("error", out var detail) ? Message(detail) :
                errors ? Message(errorList[0]) : null;
            if (compilationFailed) return ("error", message ?? "Compilation reported errors.");
            if (isError) return (affected ? "partial" : "error", message);
            return (incomplete || errors ? "partial" : "success", message);
        }
        catch (JsonException)
        { return ("error", "The MCP response was not valid JSON."); }
    }

    private static bool Text(JsonElement root, string name, out string? value)
    {
        value = root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? Message(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("message", out var message)
            ? message.ValueKind == JsonValueKind.String ? message.GetString() : null
            : element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is { Length: > 400 } ? value.Substring(0, 400) + "…" : value;
    }

    private static ConnectionFault Invalid(string message) => new("invalidRequest", 0, message);
}

internal sealed class DashboardRunCompletion
{
    public DashboardRunCapture Capture { get; }
    public string ResponseJson { get; }
    public DashboardRunCompletion(DashboardRunCapture capture, string responseJson)
    { Capture = capture; ResponseJson = responseJson; }
}
