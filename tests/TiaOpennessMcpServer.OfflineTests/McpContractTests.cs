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
        yield return ("MCP: read-only profile retains eleven schemas and typed defaults", Schemas);
        yield return ("MCP help: schema descriptions and nested document guidance reach dashboard safely", ParameterHelp);
        yield return ("MCP examples: documented calls parse and displayed sources match request contents", DocumentationExamples);
        yield return ("MCP writes: all eight schemas, dispatch paths and read-only rejection", Writes);
        yield return ("MCP writes: validation never dispatches and partial/native errors never retry", WriteErrors);
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
    private static void ParameterHelp()
    {
        var definitions = McpBoundary.ToolDefs();
        var published = Rpc(new Fake { WriteToolsAvailable = true }, "tools/list").GetProperty("tools");
        foreach (var tool in published.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            var html = DashboardToolForms.Render(new[] { definitions.Single(item => item.Name == name) });
            foreach (var property in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
            {
                var description = property.Value.GetProperty("description").GetString()!;
                Check(!string.IsNullOrWhiteSpace(description) && html.Contains(System.Net.WebUtility.HtmlEncode(description)), "Missing field guidance: " + name + "." + property.Name);
                if (property.Name is "dataType" or "logicalAddress" or "attributeName")
                    Check(!property.Value.TryGetProperty("enum", out _) && property.Value.GetProperty("examples").GetArrayLength() > 0, "Native guidance became an allowlist or lost examples.");
                if (property.Name == "documents")
                    foreach (var child in property.Value.GetProperty("items").GetProperty("properties").EnumerateObject())
                        Check(html.Contains("data-document-" + child.Name + "-help=\"") && html.Contains(System.Net.WebUtility.HtmlEncode(child.Value.GetProperty("description").GetString()!)), "Nested source help missing from the rendered transport.");
            }
        }
        var form = definitions.Single(tool => tool.Name == "create_tag");
        var schema = (Dictionary<string, object>)form.InputSchema.Properties["name"];
        schema["description"] = "A <script> & \"quote\" &lt; literal";
        var encoded = DashboardToolForms.Render(new[] { form });
        Check(!encoded.Contains("<script>") && encoded.Contains("&lt;script&gt;") && encoded.Contains("&amp;lt;"), "Help content was interpreted as markup.");
        Check(encoded.Contains("Required. Type: string."), "Requirements/types missing from dashboard help.");
        Check(DashboardToolForms.Render(definitions).Contains("Optional. Type: boolean. Default: true."), "Defaults missing from dashboard help.");
    }

    private static void DocumentationExamples()
    {
        var covered = new HashSet<string>();
        var sources = new List<string>();
        foreach (var resource in new[] { "write-operations.md", "user-manual.md" })
        {
            using var stream = typeof(McpContractTests).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd().Replace("\r\n", "\n");
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(markdown, @"```json\n([\s\S]*?)```"))
            {
                using var json = JsonDocument.Parse(match.Groups[1].Value);
                var examples = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
                foreach (var example in examples)
                {
                    var tool = example.GetProperty("name").GetString()!;
                    var fake = new Fake { WriteToolsAvailable = true };
                    var response = Rpc(fake, "tools/call", example.GetRawText());
                    Check(!response.GetProperty("isError").GetBoolean() && fake.Calls == 1, "Documented call rejected: " + tool);
                    covered.Add(tool);
                    if (fake.Written?.SourceFormat == "external-source")
                        sources.AddRange(fake.Written.Documents.Select(document => document.Content));
                }
            }
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(markdown, @"```scl\n([\s\S]*?)```"))
                Check(sources.Contains(match.Groups[1].Value), "Displayed source differs from its documented JSON request.");
        }
        Check(Names.All(covered.Contains) && new[] { "write_blocks", "write_udts", "create_tag_table", "create_tag", "create_user_constant", "set_tag_entry_attribute", "delete_tag_entry" }.All(covered.Contains), "Documented workflow examples lost tool coverage.");
        Check(sources.Count == 2 && sources.Any(source => source.StartsWith("TYPE ")) && sources.Any(source => source.StartsWith("FUNCTION_BLOCK ")), "Complete source examples missing.");
    }

    private static readonly Dictionary<string, string> WriteArguments = new()
    {
        ["write_blocks"] = "{\"processId\":20,\"plcObjectId\":\" cpu /== \",\"sourceFormat\":\"external-source\",\"documents\":[{\"name\":\"A.scl\",\"content\":\"FUNCTION \\\"A\\\" : Void\\r\\nEND_FUNCTION\\r\\n\"}]}",
        ["write_udts"] = "{\"processId\":20,\"plcObjectId\":\" cpu /== \",\"sourceFormat\":\"simatic-sd\",\"documents\":[{\"name\":\"A.s7dcl\",\"content\":\"native declaration\"},{\"name\":\"A.s7res\",\"content\":\"native resource\"}]}",
        ["create_tag_table"] = "{\"processId\":20,\"plcObjectId\":\" cpu /== \",\"name\":\"Table\",\"groupPath\":\"PLC/PLC tags/Folder\"}",
        ["create_tag"] = "{\"processId\":20,\"objectId\":\" existing-table \",\"name\":\"Start\",\"dataType\":\"Bool\",\"logicalAddress\":\"%M0.0\"}",
        ["create_user_constant"] = "{\"processId\":20,\"objectId\":\" existing-table \",\"name\":\"Limit\",\"dataType\":\"Int\",\"value\":\"10\"}",
        ["set_tag_entry_attribute"] = "{\"processId\":20,\"objectId\":\" existing-tag \",\"attributeName\":\"ExternalAccessible\",\"attributeValue\":false}",
        ["delete_tag_entry"] = "{\"processId\":20,\"objectId\":\" existing-constant \"}",
        ["import_tag_tables"] = "{\"processId\":20,\"plcObjectId\":\" cpu /== \",\"documents\":[{\"name\":\"Tables.xml\",\"content\":\"<Document />\"}]}"
    };
    private static void Writes()
    {
        var tools = Rpc(new Fake { WriteToolsAvailable = true }, "tools/list").GetProperty("tools").EnumerateArray().ToArray();
        Check(tools.Select(t => t.GetProperty("name").GetString()).SequenceEqual(Names.Concat(WriteArguments.Keys)), "Write publication differs from the nineteen tools.");
        foreach (var pair in WriteArguments)
        {
            var tool = tools.Single(t => t.GetProperty("name").GetString() == pair.Key);
            Check(!tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "Write advertised read-only.");
            var schema = tool.GetProperty("inputSchema");
            Check(!schema.GetProperty("additionalProperties").GetBoolean(), "Unknown write fields allowed.");
            using var input = JsonDocument.Parse(pair.Value);
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                Check(input.RootElement.TryGetProperty(required.GetString()!, out _), "Sample omits schema requirement.");
            var fake = new Fake { WriteToolsAvailable = true };
            var result = Call(fake, pair.Key, pair.Value);
            Check(!result.GetProperty("isError").GetBoolean() && fake.Calls == 1 && fake.Last == pair.Key && fake.ProcessId == 20, "Wrong write dispatch.");
            Check(fake.Written != null && !Payload(result).GetProperty("saved").GetBoolean(), "Write contract dropped.");
            var selector = input.RootElement.TryGetProperty("objectId", out var id) ? id.GetString() : input.RootElement.GetProperty("plcObjectId").GetString();
            Check(fake.Id == selector, "Native identifier was trimmed.");
            var readOnly = new Fake();
            Check(Payload(Call(readOnly, pair.Key, pair.Value)).GetProperty("error").GetProperty("code").GetString() == "unknownTool" && readOnly.Calls == 0, "Read-only profile dispatched a write.");
        }
        var html = DashboardToolForms.Render(McpBoundary.ToolDefs());
        Check(html.Contains("data-write=\"true\"") && html.Contains("data-type=\"documents\"") && html.Contains("data-type=\"json\""), "Write parameter forms lost their types.");
        Check(!DashboardToolForms.Render(McpBoundary.ToolDefs(false)).Contains("data-write=\"true\""), "Read-only forms include writes.");
    }
    private static void WriteErrors()
    {
        foreach (var pair in WriteArguments)
        {
            var fake = new Fake { WriteToolsAvailable = true };
            var invalid = pair.Value.Substring(0, pair.Value.Length - 1) + ",\"action\":\"arm\"}";
            var rejected = Call(fake,pair.Key,invalid);
            Check(rejected.GetProperty("isError").GetBoolean() && fake.Calls == 0, "Legacy probe field reached native write.");
        }
        var partial = new WriteResult { ProcessId = 20, Operation = "write_blocks" };
        partial.AffectedObjects.Add(new WriteObject { ObjectId = "native", Kind = "block", Name = "A" });
        partial.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "temporaryCleanup", Message = "locked temporary file" });
        var f = new Fake { WriteToolsAvailable = true, WriteResponse = partial };
        var response = Call(f,"write_blocks",WriteArguments["write_blocks"]);
        Check(response.GetProperty("isError").GetBoolean() && f.Calls == 1 && Payload(response).GetProperty("cleanupFailed").GetBoolean() &&
            Payload(response).GetProperty("affectedObjects").GetArrayLength() == 1 && !Payload(response).GetProperty("complete").GetBoolean(), "Partial write claimed success or retried.");
        f = new Fake { WriteToolsAvailable = true, Failure = new NativeFailure("TIA exact write error") };
        var error = Payload(Call(f,"delete_tag_entry",WriteArguments["delete_tag_entry"]));
        Check(f.Calls == 1 && error.GetProperty("error").GetProperty("code").GetString() == "nativeWriteFailed" &&
            error.GetProperty("errors")[0].GetProperty("origin").GetString() == "tia-openness", "Native write provenance lost.");
    }
    private sealed class NativeFailure : Exception { public NativeFailure(string message) : base(message) { } }
    private sealed class Fake : IMcpOperations
    {
        public bool WriteToolsAvailable { get; set; }
        public WriteRequest? Written;
        public WriteResult? WriteResponse;
        public Task<WriteResult> WriteAsync(WriteRequest request)
        { Written = request; return Done(request.Tool, request.ProcessId, request.ObjectId ?? request.PlcObjectId, WriteResponse ?? new WriteResult { ProcessId = request.ProcessId, Operation = request.Tool }); }

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
