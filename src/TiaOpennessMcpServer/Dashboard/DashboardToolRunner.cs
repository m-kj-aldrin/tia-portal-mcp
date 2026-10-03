using System.Text.Json;
using System.Globalization;
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

    internal DashboardRunCapture Begin(JsonElement root, string? parentRunId = null)
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
        var capture = _runs.TryStart(requestId!, tabId!, name!, processId, root.GetRawText(), out var rejection, parentRunId);
        if (capture == null)
            throw new ConnectionFault(rejection == "Four dashboard runs are already active." ? "busy" : "invalidRequest", 0, rejection!);
        // Keep the admission-time attachment identity on the returned request
        // capture, before an async response writer or another dashboard action
        // can observe a replacement. Guid.Empty fences project calls admitted
        // while disconnected, so a later attachment cannot be adopted.
        if (McpBoundary.ToolDefs().FirstOrDefault(tool => tool.Name == name)?.InputSchema.Required
            .Contains("processId", StringComparer.Ordinal) == true)
            capture.ExpectedConnectionId = CapturedConnection(tab);
        return capture;
    }

    // Datastar forms submit bound field values. This transport adapter uses the
    // published MCP schema only for JSON type conversion; McpBoundary remains
    // responsible for tool publication, argument validation and dispatch.
    internal DashboardRunCapture BeginFields(JsonElement root, bool writeToolsAvailable)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Invalid("Supply one JSON object for a dashboard form action.");
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!supplied.Add(property.Name) || property.Name is not ("tabId" or "contextStamp" or "requestId" or "name" or "fields" or "loadSourceFor"))
                throw Invalid("Unknown or duplicate dashboard form field: " + property.Name);
        if (supplied.Count != (supplied.Contains("loadSourceFor") ? 6 : 5) || !Text(root, "tabId", out var tabId) ||
            !Text(root, "contextStamp", out var contextStamp) ||
            !Text(root, "requestId", out var requestId) || !Text(root, "name", out var name) ||
            !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            throw Invalid("Supply tabId, contextStamp, requestId, name and a fields object.");
        if (supplied.Contains("loadSourceFor") &&
            (!Text(root, "loadSourceFor", out var sourceTarget) ||
             sourceTarget != (name == "get_block" ? "write_blocks" : name == "get_udt" ? "write_udts" : null)))
            throw Invalid("The source helper target does not match its read tool.");

        var definition = McpBoundary.ToolDefs(writeToolsAvailable)
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
        if (definition == null)
            throw Invalid("The selected tool is not published by this server.");
        var snapshot = _dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab == null) throw Invalid("The selected dashboard tab no longer exists.");
        if (!string.Equals(contextStamp, DashboardSelectorStore.SignalPrefix(snapshot, tab), StringComparison.Ordinal))
            throw Invalid("This dashboard form belongs to an earlier workspace context. Refresh the dashboard view.");

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (definition.InputSchema.Properties.ContainsKey("processId") && tab.Kind != "server" &&
            tab.Live && tab.ProcessId is int processId)
            arguments["processId"] = processId;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw Invalid("Duplicate dashboard form field: " + field.Name);
            if (field.Name == "processId")
                throw Invalid("The dashboard supplies processId from the selected workspace.");
            if (!definition.InputSchema.Properties.TryGetValue(field.Name, out var schema))
                throw Invalid("Unknown dashboard form field: " + field.Name);
            var converted = ConvertField(field.Name, field.Value, schema);
            if (converted.Include) arguments[field.Name] = converted.Value;
        }
        var normalized = JsonSerializer.SerializeToElement(new
        {
            tabId, requestId, name, arguments
        }, _json);
        var capture = Begin(normalized);
        // Begin rereads dashboard state for its common admission checks. The
        // form's checked context may change between these two reads, so keep
        // the attachment from the snapshot that matched contextStamp.
        if (definition.InputSchema.Required.Contains("processId", StringComparer.Ordinal))
            capture.ExpectedConnectionId = CapturedConnection(tab);
        return capture;
    }

    private static Guid CapturedConnection(DashboardTabView tab) =>
        tab.ConnectionState == "connected" && tab.ConnectionId is Guid id ? id : Guid.Empty;

    private static (bool Include, object? Value) ConvertField(string name, JsonElement value,
        Dictionary<string, object> schema)
    {
        var kind = schema.TryGetValue("type", out var declared) ? declared : "string";
        var isJson = kind is string[] || kind is string textKind && textKind == "array";
        if (isJson)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return (false, null);
                try { using var document = JsonDocument.Parse(raw!); return (true, document.RootElement.Clone()); }
                catch (JsonException) { throw Invalid(name + " must contain valid JSON."); }
            }
            return value.ValueKind == JsonValueKind.Null ? (false, null) : (true, value.Clone());
        }
        if (kind is string booleanKind && booleanKind == "boolean")
        {
            if (value.ValueKind == JsonValueKind.True) return (true, true);
            if (value.ValueKind == JsonValueKind.False) return (true, false);
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return (true, parsed);
            throw Invalid(name + " must be true or false.");
        }
        if (kind is string integerKind && integerKind == "integer")
        {
            var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (string.IsNullOrWhiteSpace(raw)) return (false, null);
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw Invalid(name + " must be an integer.");
            return (true, parsed);
        }
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(name + " must be text.");
        var text = value.GetString() ?? "";
        return string.IsNullOrEmpty(text) ? (false, null) : (true, text);
    }

    internal async Task<DashboardRunCompletion> RunAsync(DashboardRunCapture started,
        Guid? expectedConnectionId = null)
    {
        string responseJson;
        try
        {
            using var request = JsonDocument.Parse(started.RequestJson!);
            var root = request.RootElement;
            var args = root.GetProperty("arguments").Clone();
            var parameters = JsonSerializer.SerializeToElement(new { name = started.Operation, arguments = args }, _json);
            using var call = OperationCallContext.Begin("dashboard");
            call.ExpectedConnectionId = expectedConnectionId ?? started.ExpectedConnectionId;
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
