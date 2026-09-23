using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;

internal static class RpcEnvelopeTests
{
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const string WriteCall = "\"method\":\"tools/call\",\"params\":{\"name\":\"delete_block\",\"arguments\":{\"processId\":20,\"objectId\":\"native-block\"}}";

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("MCP wire: malformed JSON produces a parse error with explicit null ID", MalformedJson);
        yield return ("MCP wire: invalid envelopes and batches never dispatch engineering operations", InvalidEnvelopes);
        yield return ("MCP wire: request IDs retain exact string and integral numeric values", RequestIds);
        yield return ("MCP wire: client notifications are acknowledged without tool dispatch", Notifications);
        yield return ("MCP wire: valid calls retain production argument validation and write results", ToolCalls);
        yield return ("MCP wire: unknown methods and internal errors preserve response IDs", MethodAndInternalErrors);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static string Request(string id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + "," + WriteCall + "}";

    private static McpMessageResult Process(Fake operations, string text) =>
        new McpRpcProcessor(new McpBoundary(operations, Options, _ => false)).ProcessAsync(text).GetAwaiter().GetResult();

    private static JsonElement Wire(McpMessageResult message)
    {
        Check(message.Response != null, "Expected a protocol response.");
        return JsonSerializer.SerializeToElement(message.Response, Options);
    }

    private static void Rejected(string text, int code)
    {
        var operations = new Fake();
        var result = Process(operations, text);
        var wire = Wire(result);
        Check(result.StatusCode == 400 && wire.GetProperty("error").GetProperty("code").GetInt32() == code,
            "Incorrect admission error for: " + text);
        Check(wire.GetProperty("jsonrpc").GetString() == "2.0" &&
            wire.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Null && !wire.TryGetProperty("result", out _),
            "Protocol error lost id:null or contains a success result.");
        Check(operations.Calls == 0 && operations.ProfileReads == 0, "Rejected envelope reached the engineering boundary.");
    }

    private static void MalformedJson()
    {
        foreach (var text in new[] { "", " ", "{", "{\"jsonrpc\":\"2.0\",", Request("1") + "{}" })
            Rejected(text, -32700);
    }

    private static void InvalidEnvelopes()
    {
        foreach (var text in new[]
        {
            "null", "true", "1", "\"request\"", "[]", "[" + Request("1") + "]", "{}",
            "{\"id\":1," + WriteCall + "}",
            Request("1").Replace("\"jsonrpc\":\"2.0\"", "\"jsonrpc\":\"1.0\""),
            Request("1").Replace("\"jsonrpc\":\"2.0\"", "\"jsonrpc\":2"),
            Request("1").Replace("\"jsonrpc\":\"2.0\"", "\"jsonrpc\":\"2.0\",\"jsonrpc\":\"2.0\""),
            "{\"jsonrpc\":\"2.0\"," + WriteCall + "}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2," + WriteCall + "}",
            "{\"jsonrpc\":\"2.0\",\"id\":1}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":4}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":[]}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":true}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{},\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"error\":{}}"
        }) Rejected(text, -32600);
        foreach (var id in new[] { "null", "true", "false", "{}", "[]", "0.1", "1e-1", "9007199254740992.1", "1e-9999999999" })
            Rejected(Request(id), -32600);
    }

    private static void RequestIds()
    {
        foreach (var id in new[] { "\"client-1\"", "\"\"", "0", "-1", "1.0", "10e-1", "1e2", "-0.00e-9999999999", "1234567890123456789012345678901234567890" })
        {
            var operations = new Fake();
            var message = Process(operations, Request(id));
            var wire = Wire(message);
            Check(message.StatusCode == 200 && wire.GetProperty("id").GetRawText() == id,
                "Request ID was rounded, rewritten or rejected: " + id);
            Check(!wire.TryGetProperty("error", out _) && operations.Calls == 1 && operations.LastWrite?.Tool == "delete_block",
                "Valid request did not dispatch exactly once.");
        }
    }

    private static void Notifications()
    {
        foreach (var text in new[]
        {
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":1,\"reason\":\"Client timeout\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/roots/list_changed\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":1,\"progress\":1}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/client-extension\",\"params\":{}}"
        })
        {
            var operations = new Fake();
            var result = Process(operations, text);
            Check(result.StatusCode == 202 && result.Response == null && operations.Calls == 0 && operations.ProfileReads == 0,
                "Client notification returned a JSON-RPC response or dispatched an operation.");
        }
        foreach (var text in new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"1.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":[]}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/\"}"
        }) Rejected(text, -32600);
    }

    private static void ToolCalls()
    {
        var operations = new Fake();
        var wire = Wire(Process(operations, Request("\"write\"")));
        var result = wire.GetProperty("result");
        using var payload = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Check(!result.GetProperty("isError").GetBoolean() && operations.Calls == 1 &&
            operations.LastWrite?.ObjectId == "native-block" && !payload.RootElement.GetProperty("saved").GetBoolean(),
            "Wire admission changed the existing write contract.");

        operations = new Fake();
        wire = Wire(Process(operations, Request("1").Replace("\"processId\":20", "\"processId\":20,\"unexpected\":true")));
        result = wire.GetProperty("result");
        using var rejected = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Check(!wire.TryGetProperty("error", out _) && result.GetProperty("isError").GetBoolean() && operations.Calls == 0 &&
            rejected.RootElement.GetProperty("error").GetProperty("code").GetString() == "invalidRequest",
            "Existing tool argument validation was bypassed or changed into a protocol error.");
    }

    private static void MethodAndInternalErrors()
    {
        var operations = new Fake();
        var message = Process(operations, "{\"jsonrpc\":\"2.0\",\"id\":\"unknown\",\"method\":\"unknown-method\"}");
        var wire = Wire(message);
        Check(message.StatusCode == 200 && wire.GetProperty("id").GetString() == "unknown" &&
            wire.GetProperty("error").GetProperty("code").GetInt32() == -32601 &&
            !wire.TryGetProperty("result", out _) && operations.Calls == 0, "Unknown method handling changed.");

        operations = new Fake { FailProfile = true };
        message = Process(operations, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\"}");
        wire = Wire(message);
        Check(message.StatusCode == 500 && wire.GetProperty("id").GetInt32() == 7 &&
            wire.GetProperty("error").GetProperty("code").GetInt32() == -32603 && !wire.TryGetProperty("result", out _),
            "Internal error lost its known request ID or included a success result.");
    }

    private sealed class Fake : IEngineeringOperations
    {
        public int Calls, ProfileReads;
        public bool FailProfile;
        public WriteRequest? LastWrite;
        public bool WriteToolsAvailable
        {
            get { ProfileReads++; if (FailProfile) throw new InvalidOperationException("Simulated boundary failure"); return true; }
        }
        public Task<WriteResult> WriteAsync(WriteRequest request)
        {
            Calls++; LastWrite = request;
            return Task.FromResult(new WriteResult { ProcessId = request.ProcessId, Operation = request.Tool });
        }
        private Task<T> Unexpected<T>() { Calls++; throw new InvalidOperationException("Unexpected engineering dispatch"); }
        public object BridgeStatus() { Calls++; throw new InvalidOperationException("Unexpected status dispatch"); }
        public Task<ProcessDiscovery> DiscoverAsync() => Unexpected<ProcessDiscovery>();
        public Task<ProcessStatus> ReadStatusAsync(int processId) => Unexpected<ProcessStatus>();
        public Task<DeviceInventory> ListDevicesAsync(int processId) => Unexpected<DeviceInventory>();
        public Task<DeviceRead> ReadDeviceAsync(int processId, string objectId, bool includePath) => Unexpected<DeviceRead>();
        public Task<BlockInventory> ListBlocksAsync(int processId, string plcObjectId) => Unexpected<BlockInventory>();
        public Task<BlockRead> ReadBlockAsync(BlockReadRequest request) => Unexpected<BlockRead>();
        public Task<BlockInventory> ListUdtsAsync(int processId, string plcObjectId) => Unexpected<BlockInventory>();
        public Task<BlockRead> ReadUdtAsync(BlockReadRequest request) => Unexpected<BlockRead>();
        public Task<BlockInventory> ListTagTablesAsync(int processId, string plcObjectId) => Unexpected<BlockInventory>();
        public Task<TagTableRead> ReadTagTableAsync(TagTableReadRequest request) => Unexpected<TagTableRead>();
        public Task<CrossReferenceRead> ReadCrossReferencesAsync(CrossReferenceRequest request) => Unexpected<CrossReferenceRead>();
        public Task<TagTableExportResult> ExportTagTableAsync(ExportTagTableRequest request) => Unexpected<TagTableExportResult>();
        public Task<CompileResult> CompileAsync(CompileRequest request) => Unexpected<CompileResult>();
    }
}
