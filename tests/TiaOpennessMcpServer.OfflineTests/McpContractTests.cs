using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Prototype;

internal static class McpContractTests
{
    private static readonly string[] Names = { "list_tia_processes", "get_status", "list_devices", "get_device",
        "list_blocks", "get_block", "list_udts", "get_udt", "list_tag_tables", "get_tag_table", "get_cross_references" };
    private static readonly JsonSerializerOptions Options = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("MCP: exact eleven schemas and typed defaults", Schemas);
        yield return ("MCP: every tool dispatches once with native selectors and defaults", Dispatch);
        yield return ("MCP: options are forwarded without changing opaque identifiers", OptionsForwarded);
        yield return ("MCP: invalid and duplicate fields never reach readers", Invalid);
        yield return ("MCP: native errors and discarded-context failures keep process and provenance", Errors);
        yield return ("MCP: partial payloads and nulls survive the transport unchanged", Partial);
        yield return ("MCP: initialization, passive status and retired names", Protocol);
        yield return ("MCP: one call journal entry keeps the tool payload", Journal);
    }
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(value, Options);
    private static JsonElement Rpc(Fake reads, string method, string? parameters = null)
    {
        using var doc = JsonDocument.Parse(parameters ?? "null");
        var boundary = new McpBoundary(reads, Options, ex => ex is NativeFailure);
        var response = boundary.HandleAsync(new McpRpcRequest { Method = method,
            Params = parameters == null ? null : doc.RootElement }).GetAwaiter().GetResult();
        Check(response.rpcErr == null, "Unexpected RPC failure.");
        return Serialize(response.result);
    }
    private static JsonElement Call(Fake reads, string name, string args = "{}") =>
        Rpc(reads, "tools/call", "{\"name\":\"" + name + "\",\"arguments\":" + args + "}");
    private static JsonElement Payload(JsonElement result) => JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
    private static string Args(string name, string extra = "") => "{\"processId\":20" +
        (name is "list_blocks" or "list_udts" or "list_tag_tables" ? ",\"plcObjectId\":\" cpu /== \"" :
        name is "get_device" or "get_block" or "get_udt" or "get_tag_table" or "get_cross_references" ? ",\"objectId\":\" obj /== \"" : "") + extra + "}";
    private static void Schemas()
    {
        var listing = Rpc(new Fake(), "tools/list").GetProperty("tools");
        Check(listing.EnumerateArray().Select(x => x.GetProperty("name").GetString()).SequenceEqual(Names), "Tool surface changed.");
        foreach (var tool in listing.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            var schema = tool.GetProperty("inputSchema");
            Check(!schema.GetProperty("additionalProperties").GetBoolean(), "Extra properties allowed.");
            var expected = name == "list_tia_processes" ? Array.Empty<string>() : name == "get_status" ? new[] { "processId" } :
                name == "list_devices" ? new[] { "processId" } : name.StartsWith("list_") ? new[] { "processId", "plcObjectId" } :
                name == "get_cross_references" ? new[] { "processId", "objectId" } :
                name == "get_device" ? new[] { "processId", "objectId", "includePath" } :
                name == "get_tag_table" ? new[] { "processId", "objectId", "includePath", "includeEntries" } :
                new[] { "processId", "objectId", "includePath", "includeSource", "sourceFormat", "includeDependencies" };
            Check(schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).SequenceEqual(expected), "Wrong properties: " + name);
            var required = name is "list_tia_processes" or "get_status" ? Array.Empty<string>() : expected.Where(p => p.EndsWith("Id")).ToArray();
            Check(schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()).SequenceEqual(required), "Wrong required fields.");
            foreach (var p in schema.GetProperty("properties").EnumerateObject())
            {
                if (p.Name == "processId") Check(p.Value.GetProperty("type").GetString() == "integer" && p.Value.GetProperty("minimum").GetInt32() == 1, "Wrong process type.");
                if (p.Name.StartsWith("include")) Check(p.Value.GetProperty("type").GetString() == "boolean" &&
                    p.Value.GetProperty("default").GetBoolean() == (p.Name != "includeDependencies"), "Nonboolean default.");
                if (p.Name == "sourceFormat") Check(p.Value.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "best", "external-source", "simatic-sd", "simatic-ml" }), "Wrong formats.");
            }
            Check(schema.TryGetProperty("allOf", out _) == (name is "get_block" or "get_udt"), "Dependency condition missing or misplaced.");
        }
    }
    private static void Dispatch()
    {
        foreach (var name in Names)
        {
            var fake = new Fake();
            var response = Call(fake, name, name == "list_tia_processes" ? "{}" : Args(name));
            Check(!response.GetProperty("isError").GetBoolean() && fake.Calls == 1 && fake.Last == name, "Wrong dispatch: " + name);
            var payload = Payload(response);
            Check(payload.TryGetProperty("readAtUtc", out _) && payload.GetProperty("errors").GetArrayLength() == 0, "Missing envelope.");
            if (name != "list_tia_processes") Check(fake.ProcessId == 20 && payload.GetProperty("processId").GetInt32() == 20, "Process changed.");
            if (name.StartsWith("list_") && name != "list_tia_processes" && name != "list_devices") Check(fake.Id == " cpu /== ", "CPU ID changed.");
            if (name.StartsWith("get_") && name != "get_status") Check(fake.Id == " obj /== ", "Object ID changed.");
            if (fake.Block != null) Check(fake.Block.IncludeSource && fake.Block.IncludePath && !fake.Block.IncludeDependencies && fake.Block.SourceFormat == "best", "Source defaults changed.");
            if (fake.Table != null) Check(fake.Table.IncludeEntries && fake.Table.IncludePath, "Table defaults changed.");
            if (name == "get_device") Check(fake.IncludePath, "Device path default changed.");
        }
    }
    private static void OptionsForwarded()
    {
        foreach (var name in new[] { "get_block", "get_udt" })
        {
            var f = new Fake(); Call(f, name, Args(name, ",\"includePath\":false,\"includeDependencies\":true,\"sourceFormat\":\"external-source\""));
            Check(f.Block is { IncludeSource: true, IncludeDependencies: true, IncludePath: false, SourceFormat: "external-source" }, "Dependencies lost.");
            f = new Fake(); Call(f, name, Args(name, ",\"includeSource\":false")); Check(f.Block?.IncludeSource == false, "Metadata-only lost.");
        }
        var table = new Fake(); Call(table, "get_tag_table", Args("get_tag_table", ",\"includeEntries\":false,\"includePath\":false"));
        Check(table.Table is { IncludeEntries: false, IncludePath: false }, "Table options lost.");
        var device = new Fake(); Call(device, "get_device", Args("get_device", ",\"includePath\":false")); Check(!device.IncludePath, "Device option lost.");
    }
    private static void Rejected(string name, string args, string code = "invalidRequest")
    {
        var fake = new Fake(); var result = Call(fake, name, args); var payload = Payload(result);
        Check(result.GetProperty("isError").GetBoolean() && fake.Calls == 0 && payload.GetProperty("error").GetProperty("code").GetString() == code, "Accepted invalid " + name + ": " + args);
        using var supplied = JsonDocument.Parse(args);
        if (supplied.RootElement.ValueKind == JsonValueKind.Object && supplied.RootElement.TryGetProperty("processId", out var id))
            Check(payload.GetProperty("processId").GetRawText() == id.GetRawText(), "Failure lost requested selector.");
    }
    private static void Invalid()
    {
        foreach (var name in Names)
        {
            Rejected(name, "null"); Rejected(name, "[]");
            Rejected(name, Args(name, ",\"unknown\":false"));
            Rejected(name, Args(name, ",\"processId\":21"));
            if (name != "list_tia_processes")
                foreach (var value in new[] { "0", "-1", "1.5", "2147483648", "null", "\"20\"", "true" })
                    Rejected(name, Args(name).Replace("20", value));
            if (name is not ("list_tia_processes" or "get_status")) Rejected(name, "{}");
        }
        foreach (var name in new[] { "get_block", "get_udt" })
            foreach (var extra in new[] { ",\"includeSource\":\"false\"", ",\"includePath\":null", ",\"includeDependencies\":true",
                ",\"sourceFormat\":\"scl-source\"", ",\"sourceFormat\":\"best\",\"sourceFormat\":\"simatic-ml\"",
                ",\"sourceFormat\":\"external-source\",\"includeSource\":false,\"includeDependencies\":true" }) Rejected(name, Args(name, extra));
        foreach (var extra in new[] { ",\"includeEntries\":null", ",\"includeSource\":false", ",\"sourceFormat\":\"best\"", ",\"includeDependencies\":false" }) Rejected("get_tag_table", Args("get_tag_table", extra));
        Rejected("get_cross_references", Args("get_cross_references", ",\"includePath\":false"));
        foreach (var raw in new[] { "{}", "{\"name\":\"get_status\",\"name\":\"get_status\"}", "{\"name\":\"get_status\",\"extra\":1}", "{\"name\":\"get_status\",\"arguments\":{},\"arguments\":{}}" })
        { var f = new Fake(); Check(Rpc(f, "tools/call", raw).GetProperty("isError").GetBoolean() && f.Calls == 0, "Bad outer fields accepted."); }
    }
    private static void Errors()
    {
        foreach (var ex in new Exception[] { new NativeFailure("Exact Siemens text: Å / 42"),
            new ConnectionFault("nativeReadFailed", 20, "Exact Siemens text: Å / 42", new NativeFailure("Exact Siemens text: Å / 42")),
            new ConnectionFault("reconnectRequired", 20, "Context changed", new NativeFailure("Exact Siemens text: Å / 42")),
            new ConnectionFault("notConnected", 20, "Connect first") })
        {
            var f = new Fake { Failure = ex }; var result = Call(f, "get_block", Args("get_block")); var p = Payload(result);
            Check(result.GetProperty("isError").GetBoolean() && p.GetProperty("processId").GetInt32() == 20 && !p.TryGetProperty("metadata", out _), "Failure leaked payload or lost process.");
            if (ex is NativeFailure || ex.InnerException != null) Check(p.GetProperty("errors").EnumerateArray().Any(e => e.GetProperty("origin").GetString() == "tia-openness" && e.GetProperty("message").GetString() == "Exact Siemens text: Å / 42"), "Native provenance lost.");
            if (ex is ConnectionFault { ReconnectRequired: true }) Check(p.GetProperty("error").GetProperty("reconnectRequired").GetBoolean(), "Reconnect flag lost.");
        }
    }
    private static void Partial()
    {
        var partial = new BlockRead { ProcessId = 20, Metadata = new() { ["path"] = null, ["name"] = "Kept" } };
        partial.Errors.Add(new DiscoveryError { Origin = "tia-openness", Operation = "export", Message = "Native unchanged", Format = "simatic-ml" });
        var f = new Fake { BlockResult = partial }; var result = Call(f, "get_block", Args("get_block")); var payload = Payload(result);
        Check(!result.GetProperty("isError").GetBoolean() && payload.GetRawText() == Serialize(partial).GetRawText(), "Partial response was replaced or changed.");
        Check(payload.GetProperty("source").ValueKind == JsonValueKind.Null && !payload.GetProperty("complete").GetBoolean(), "Partial/null distinction lost.");
    }
    private static void Journal()
    {
        var notes = new List<McpCallNote>();
        using var call = JsonDocument.Parse("{\"name\":\"list_devices\",\"arguments\":{\"processId\":20}}");
        var boundary = new McpBoundary(new Fake(), Options, ex => ex is NativeFailure, notes.Add);
        var response = boundary.HandleAsync(new McpRpcRequest { Method = "tools/call", Params = call.RootElement }).GetAwaiter().GetResult();
        Check(response.rpcErr == null && notes.Count == 1 && notes[0].Operation == "list_devices" && notes[0].Outcome == "success" && notes[0].ProcessId == 20 && notes[0].Origin == "mcp", "Successful call was not journaled once.");
        var payload = Payload(Serialize(response.result));
        Check(payload.GetProperty("processId").GetInt32() == 20, "Journal changed the tool payload.");
        notes.Clear();
        using var listing = JsonDocument.Parse("null");
        boundary.HandleAsync(new McpRpcRequest { Method = "tools/list", Params = listing.RootElement }).GetAwaiter().GetResult();
        Check(notes.Count == 0, "tools/list was recorded as a tool call.");
        notes.Clear();
        using var unknown = JsonDocument.Parse("{\"name\":\"compile\",\"arguments\":{}}");
        var failed = boundary.HandleAsync(new McpRpcRequest { Method = "tools/call", Params = unknown.RootElement }).GetAwaiter().GetResult();
        Check(failed.rpcErr == null && notes.Count == 1 && notes[0].Operation == "compile" && notes[0].Outcome == "error", "Failed admission was not recorded once.");
        Check(Payload(Serialize(failed.result)).GetProperty("error").GetProperty("code").GetString() == "unknownTool", "Journal replaced the tool error.");
        notes.Clear();
        var captured = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var attributed = new Fake { OnRead = () => { DashboardCallContext.ConnectionId.Value = captured; DashboardCallContext.ProjectPath.Value = @"C:\Projects\A.ap20"; } };
        var attributedBoundary = new McpBoundary(attributed, Options, ex => ex is NativeFailure, notes.Add);
        DashboardCallContext.Origin.Value = "dashboard";
        using var status = JsonDocument.Parse("{\"name\":\"get_status\",\"arguments\":{\"processId\":20}}");
        attributedBoundary.HandleAsync(new McpRpcRequest { Method = "tools/call", Params = status.RootElement }).GetAwaiter().GetResult();
        Check(notes.Count == 1 && notes[0].Origin == "dashboard" && notes[0].ConnectionId == captured && notes[0].ProjectPath == @"C:\Projects\A.ap20", "Captured connection context was dropped.");
        DashboardCallContext.Origin.Value = null;
        DashboardCallContext.ConnectionId.Value = null;
        DashboardCallContext.ProjectPath.Value = null;
    }
    private static void Protocol()
    {
        foreach (var v in new[] { "2024-11-05", "2025-03-26" }) Check(Rpc(new Fake(), "initialize", "{\"protocolVersion\":\"" + v + "\"}").GetProperty("protocolVersion").GetString() == v, "Protocol changed.");
        var f = new Fake(); var p = Payload(Call(f, "get_status")); Check(f.Last == "bridge" && !p.TryGetProperty("processId", out _) && !p.TryGetProperty("connections", out _) && !p.GetProperty("writeToolsAvailable").GetBoolean(), "Passive status selected process.");
        Check(!Rpc(new Fake(), "tools/call", "{\"name\":\"get_status\"}").GetProperty("isError").GetBoolean(), "Omitted arguments rejected.");
        foreach (var name in new[] { "connect_to_tia_portal", "disconnect_from_tia_portal", "open_tia_project", "list_plc_objects", "find_plc_objects", "read_plc_object", "get_tag_table_entries", "compile", "save_project" }) Rejected(name, "{}", "unknownTool");
    }
    private sealed class NativeFailure : Exception { public NativeFailure(string message) : base(message) { } }
    private sealed class Fake : IMcpReads
    {
        public int Calls, ProcessId; public string? Last, Id; public bool IncludePath;
        public BlockReadRequest? Block; public TagTableReadRequest? Table; public Exception? Failure; public BlockRead? BlockResult;
        public Action? OnRead;
        private Task<T> Done<T>(string name, int process, string? id, T result)
        { Calls++; Last = name; ProcessId = process; Id = id; if (Failure != null) throw Failure; return Task.FromResult(result); }
        public object BridgeStatus() { Calls++; Last = "bridge"; return new { readAtUtc = DateTimeOffset.UtcNow, accessProfile = "read-only", writeToolsAvailable = false, errors = Array.Empty<DiscoveryError>() }; }
        public Task<ProcessDiscovery> DiscoverAsync() => Done("list_tia_processes", 0, null, new ProcessDiscovery());
        public Task<ProcessStatus> ReadStatusAsync(int p) { OnRead?.Invoke(); return Done("get_status", p, null, new ProcessStatus { ProcessId = p }); }
        public Task<DeviceInventory> ListDevicesAsync(int p) => Done("list_devices", p, null, new DeviceInventory { ProcessId = p });
        public Task<DeviceRead> ReadDeviceAsync(int p, string id, bool path) { IncludePath = path; return Done("get_device", p, id, new DeviceRead { ProcessId = p }); }
        public Task<BlockInventory> ListBlocksAsync(int p, string id) => Done("list_blocks", p, id, new BlockInventory { ProcessId = p });
        public Task<BlockInventory> ListUdtsAsync(int p, string id) => Done("list_udts", p, id, new BlockInventory { ProcessId = p });
        public Task<BlockInventory> ListTagTablesAsync(int p, string id) => Done("list_tag_tables", p, id, new BlockInventory { ProcessId = p });
        public Task<BlockRead> ReadBlockAsync(BlockReadRequest r) { Block = r; return Done("get_block", r.ProcessId, r.ObjectId, BlockResult ?? new BlockRead { ProcessId = r.ProcessId }); }
        public Task<BlockRead> ReadUdtAsync(BlockReadRequest r) { Block = r; return Done("get_udt", r.ProcessId, r.ObjectId, BlockResult ?? new BlockRead { ProcessId = r.ProcessId }); }
        public Task<TagTableRead> ReadTagTableAsync(TagTableReadRequest r) { Table = r; return Done("get_tag_table", r.ProcessId, r.ObjectId, new TagTableRead { ProcessId = r.ProcessId }); }
        public Task<CrossReferenceRead> ReadCrossReferencesAsync(CrossReferenceRequest r) => Done("get_cross_references", r.ProcessId, r.ObjectId, new CrossReferenceRead { ProcessId = r.ProcessId });
    }
}
