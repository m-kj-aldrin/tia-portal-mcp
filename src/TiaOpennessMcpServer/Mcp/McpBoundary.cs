using System.Diagnostics;
using System.Text.Json;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Diagnostics;

namespace TiaOpennessMcpServer.Mcp;

internal sealed class McpBoundary
{
    private readonly IEngineeringOperations _operations;
    private readonly JsonSerializerOptions _json;
    private readonly Func<Exception, bool> _isNative;
    private readonly Action<OperationCallNote>? _journal;
    private static readonly List<McpToolDefinition> ReadDefinitions = CreateToolDefs(false);
    private static readonly List<McpToolDefinition> FullDefinitions = CreateToolDefs(true);
    private static readonly HashSet<string> ReadNames = new(ReadDefinitions.Select(tool => tool.Name), StringComparer.Ordinal);
    private static readonly HashSet<string> FullNames = new(FullDefinitions.Select(tool => tool.Name), StringComparer.Ordinal);
    public McpBoundary(IEngineeringOperations operations, JsonSerializerOptions json, Func<Exception, bool> isNative, Action<OperationCallNote>? journal = null)
    { _operations = operations; _json = json; _isNative = isNative; _journal = journal; }

    internal static List<McpToolDefinition> ToolDefs(bool writesEnabled = true) =>
        writesEnabled ? FullDefinitions : ReadDefinitions;

    private static bool Publishes(bool writesEnabled, string name) =>
        (writesEnabled ? FullNames : ReadNames).Contains(name);

    private static List<McpToolDefinition> CreateToolDefs(bool writesEnabled = true)
    {
        var process = McpP("processId", "integer", true, "Positive process ID from list_tia_processes. The user must connect that UI process in the dashboard first. Every project call uses that retained project; never attaches or selects an implicit process.");
        var cpu = McpP("plcObjectId", "string", true, "Opaque native CPU DeviceItem ID from get_device, whose SoftwareContainer owns PlcSoftware. Not a rack or software ID.");
        var deviceId = McpP("objectId", "string", true, "Opaque Device objectId from list_devices in this process. Preserve exactly; a name, path or CPU DeviceItem ID is not a Device selector.");
        var blockId = McpP("objectId", "string", true, "Opaque block objectId from list_blocks in this process. Preserve exactly; names and paths are not selectors.");
        var udtId = McpP("objectId", "string", true, "Opaque UDT objectId from list_udts in this process. Preserve exactly; names and paths are not selectors.");
        var tableId = McpP("objectId", "string", true, "Opaque tag-table objectId from list_tag_tables in this process. Preserve exactly; an entry ID is not a table ID.");
        var referenceId = McpP("objectId", "string", true, "Opaque engineering objectId in this process, for example a block/UDT inventory ID or a tag's own objectId from get_tag_table. Use the entry ID for a tag's references, not its table ID. Null IDs cannot be used.");
        var path = McpP("includePath", "boolean", false, "Default true: construct navigation paths through parents. False skips that traversal and returns null paths; paths are navigation aids, not object selectors.", true);
        var source = McpP("includeSource", "boolean", false, "Default true: export native source documents with name, content and exact-content checksum. False reads metadata only and returns source:null without export.", true);
        var format = McpP("sourceFormat", "string", false, "Default best uses the tool's documented native fallback order. Explicit external-source, simatic-sd or simatic-ml never fall back. For a later write use the returned source.format, not best, and only each document's name/content.", "best", "best", "external-source", "simatic-sd", "simatic-ml");
        var dependencies = McpP("includeDependencies", "boolean", false, "Default false. True requires includeSource enabled and explicitly supplied sourceFormat:external-source. Native dependency generation may include additional declarations; consider every declaration before writing the source back.", false);
        var tools = new List<McpToolDefinition>
        {
            McpT("list_tia_processes", "Discover running TIA processes, optional primary-project paths and connectedByMcp state without attaching. Accepts no arguments; use a returned processId after the user connects it in the dashboard."),
            McpT("get_status", "Without processId, passive bridge facts only. With processId, connection state and available native TIA/products/primary-project context; never attaches.",
                McpP("processId", "integer", false, "Positive process ID from list_tia_processes for explicit connection/native status. Omit this field for passive bridge status only; null is not omission.")),
            McpT("list_devices", "Inventory native device groups and Devices. Preserves readable branches; inspect complete/errors. Use a returned Device objectId with get_device for CPU discovery. No detailed metadata or source.", process),
            McpT("get_device", "Read one Device's metadata and nested DeviceItem tree. Use the CPU's returned plcObjectId for block, UDT and tag-table inventories and source writes.", process, deviceId, path),
            McpT("list_blocks", "Inventory native block groups, blocks and unit scopes for one CPU. Use block IDs with get_block and group IDs/paths as write destinations. No source or detailed metadata; inspect complete/errors.", process, cpu),
            McpT("get_block", "Read block metadata and optional native source. best: SCL/STL/DB external-source then simatic-ml; LAD simatic-sd then simatic-ml; other languages simatic-ml. Source failures retain metadata and errors. To update, edit the complete returned documents and submit their name/content to write_blocks in the intended CPU/scope, then read back.", process, blockId, path, source, format, dependencies),
            McpT("list_udts", "Inventory native type groups, UDTs and unit scopes for one CPU. Use UDT IDs with get_udt and group IDs/paths as write destinations. No source or detailed metadata; inspect complete/errors.", process, cpu),
            McpT("get_udt", "Read UDT metadata and optional native source. best: external-source (.udt), simatic-sd, then simatic-ml. Source failures retain metadata and errors. To update, edit the complete returned documents and submit their name/content to write_udts in the intended CPU/scope, then read back.", process, udtId, path, source, format, dependencies),
            McpT("list_tag_tables", "Inventory native tag-table groups and tables for one CPU. Use table IDs with get_tag_table or entry creation; group IDs/paths select table creation/import destinations. No entries, source or detailed metadata.", process, cpu),
            McpT("get_tag_table", "Read table metadata and optional native Tags/UserConstants/SystemConstants with their own IDs or null. Use tag/user-constant IDs for attribute edits or deletion. No source export or checksum: this JSON is not an import_tag_tables XML document.", process, tableId, path,
                McpP("includeEntries", "boolean", false, "Default true: read tags, user constants and system constants with native values and their own IDs or null. False skips entry access and returns entries:null. System constants are read-only.", true)),
            McpT("get_cross_references", "Query the object's native CrossReferenceService with AllObjects. Preserve Sources/Children/References/Locations, native paths and enums. Native service determines support; no compile or derived graph. Inspect complete/errors before treating the result as complete usage information.", process, referenceId),
            McpT("export_tag_table", "Export one native PLC tag table as a complete SimaticML XML document with exact-content checksum. Returns document contents, never a server path. No external-source or SIMATIC SD representation. Use the returned document name/content with import_tag_tables; typed metadata/entries remain get_tag_table. Does not change or save the project.", process, tableId),
            McpT("list_technology_objects", "Inventory native technology-object groups and technology objects for one CPU. Use a returned objectId with get_technology_object, get_block or delete_block. No parameters or source; inspect complete/errors.", process, cpu),
            McpT("get_technology_object", "Read one technology object and its Parameters composition. Each parameter has a name, value and objectId when the native identifier exists. includeParameters:false skips that composition and returns parameters:null. The instance-DB document remains get_block. Does not change or save the project.", process,
                McpP("objectId", "string", true, "Opaque technology-object objectId from list_technology_objects or get_cross_references in this process. Preserve exactly; a name or path is not a selector."),
                path,
                McpP("includeParameters", "boolean", false, "Default true: enumerate TechnologicalInstanceDB.Parameters and return each name and value. False skips the composition and returns parameters:null.", true))
        };
        if (writesEnabled)
        {
            var group = McpP("groupObjectId", "string", false, "Existing native group ID from the matching block/type/tag-table inventory in this CPU, including the intended unit scope. Must have the matching composition type. Omit both destination fields for the CPU root; mutually exclusive with groupPath.");
            var groupPath = McpP("groupPath", "string", false, "Exact PLC[/unit]/group path from the matching inventory, used when a group has no native ID. Must resolve uniquely within this CPU. Mutually exclusive with groupObjectId; this does not create folders.");
            var writeFormat = McpP("sourceFormat", "string", true, "Required explicit format: external-source = one .scl/.awl/.db/.udt; simatic-sd = one .s7dcl plus optional same-stem .s7res; simatic-ml = one .xml. For read-edit-write use returned source.format. best is not accepted; no fallback.", null, "external-source", "simatic-sd", "simatic-ml");
            var documents = McpDocuments();
            var name = McpP("name", "string", true, "Nonblank native name of the new table, tag or user constant. TIA validates naming and uniqueness; this is creation, not an existing-object selector or rename operation.");
            var tagType = McpE(McpP("dataType", "string", true, "Native PLC tag type name, for example Bool, Byte, Word, Int, DInt or Real. Supported project-defined PLC types may also be used; CPU, memory area and address compatibility determine acceptance. This is not a fixed enum. Attribute edits use DataTypeName."), "Bool", "Int", "Real");
            var constantType = McpE(McpP("dataType", "string", true, "Native user-constant type name, for example Int, Real or Time, compatible with the supplied literal. Constants have a value instead of a logical address; their supported types differ from addressed tags. TIA determines acceptance. Attribute edits use DataTypeName."), "Int", "Real", "Time");
            var table = McpP("objectId", "string", true, "Native tag-table objectId from list_tag_tables or get_tag_table. Creates an entry inside this existing table; preserve the ID exactly.");
            var entry = McpP("objectId", "string", true, "The existing tag's or user constant's own objectId from get_tag_table(includeEntries:true), not the containing table ID. Null IDs cannot be used. System constants are read-only.");
            tools.AddRange(new[]
            {
                McpT("write_blocks", "Create or replace blocks from complete native source documents. To update, read with get_block, edit source, and write to the intended CPU/scope. Declarations determine affected names, not filenames or a target block ID. External source uses generation; SD/XML use Override. No update-only mode, member patch, stale-source check or delete. May affect multiple objects or fail partially; inspect complete/errors/affectedObjects and read back. Does not save or retry.", process, cpu, group, groupPath, writeFormat, documents),
                McpT("write_udts", "Create or replace PLC data types from complete native source documents. To update, read with get_udt, edit source, and write to the intended CPU/scope. Declarations determine affected names, not filenames or a target UDT ID. External source uses generation; SD/XML use Override. No update-only mode, member patch, stale-source check or delete. May affect multiple objects or fail partially; inspect complete/errors/affectedObjects and read back. Does not save or retry.", process, cpu, group, groupPath, writeFormat, documents),
                McpT("create_tag_table", "Create an empty tag table in the selected PLC root or existing group. Add entries with create_tag/create_user_constant. Does not update, rename or delete an existing table and does not save.", process, cpu, group, groupPath, name),
                McpT("create_tag", "Create a tag in an existing table. Supply a native type and compatible logical address. To edit an existing tag use set_tag_entry_attribute with its entry ID. Does not save.", process, table, name, tagType,
                    McpE(McpP("logicalAddress", "string", true, "Nonblank native logical address. I=input, Q=output, M=memory; bit addresses use byte.bit (bit 0..7), B/W/D select 8/16/32 bits. Examples: Bool at %M0.0, Int at %IW64, Real at %MD100. CPU range, type and area must be compatible. Unlike native unset-address creation, this tool rejects blank addresses."), "%M0.0", "%IW64", "%MD100")),
                McpT("create_user_constant", "Create a user constant in an existing table using a type and native literal string. To edit its writable attributes use set_tag_entry_attribute with the constant's ID. Does not save.", process, table, name, constantType,
                    McpE(McpP("value", "string", true, "Nonblank native constant literal as a JSON string, for example Int: \"100\", Real: \"1.5\", Time: \"T#1s\". The literal must match dataType; a JSON number or boolean is not accepted here."), "100", "1.5", "T#1s")),
                McpT("set_tag_entry_attribute", "Change one native attribute on an existing tag or user constant. TIA determines writability and value acceptance. No table metadata or system-constant edits. Does not save; read back the containing table.", process, entry,
                    McpE(McpP("attributeName", "string", true, "Native writable attribute name, with native casing. Tags: DataTypeName, LogicalAddress, Name (V20), ExternalAccessible, ExternalVisible, ExternalWritable, IsSafety. User constants: DataTypeName or Value; Name is documented read-only. This is guidance, not an allowlist; TIA decides."), "DataTypeName", "LogicalAddress", "ExternalAccessible", "Value"),
                    ("attributeValue", true, new Dictionary<string, object> { ["type"] = new[] { "string", "boolean", "number" }, ["description"] = "JSON string, boolean or finite number matching the native attribute. DataTypeName/LogicalAddress/Name and constant Value use strings; ExternalAccessible/ExternalVisible/ExternalWritable/IsSafety use booleans. Integers become Int32 then Int64 where representable, otherwise Double. No string coercion; null, arrays and objects are rejected.", ["examples"] = new object[] { "Int", "%IW64", false, "100" } })),
                McpT("delete_tag_entry", "Delete one existing tag or user constant by its own ID. Does not delete blocks, UDTs, whole tag tables or system constants. Returns the identity captured before deletion; read back its table. Does not save or retry.", process, entry),
                McpT("import_tag_tables", "Import tag tables from one complete SimaticML XML document using native Override. get_tag_table JSON cannot be used as XML. Native names/import semantics determine affected tables; no direct table-ID update or table rename. Do not assume omitted entries are deleted. Inspect complete/errors/affectedObjects and read back; no save or retry.", process, cpu, group, groupPath, McpDocuments(xmlOnly: true)),
                McpT("delete_block", "Delete one native PLC block by its own ID. TIA determines whether deletion is permitted and how existing references are affected. Returns the identity captured before deletion; verify absence with list_blocks. No force, cascade, save or automatic retry.", process, blockId),
                McpT("delete_udt", "Delete one native PLC data type by its own ID. TIA determines whether deletion is permitted and how existing references are affected. Returns the identity captured before deletion; verify absence with list_udts. No force, cascade, save or automatic retry.", process, udtId),
                McpT("delete_tag_table", "Delete one native PLC tag table, including its native contained entries, by the table's own ID. TIA determines permissions and restrictions. Returns the identity captured before deletion; verify absence with list_tag_tables. No force, save or automatic retry.", process, tableId),
                McpT("create_technology_object", "Create a technology object in the selected CPU technology-object root or an existing technology-object group. Supply the native system-library element and version; TIA validates the pair. Does not create folders, save or compile.", process, cpu,
                    McpP("groupObjectId", "string", false, "Existing technology-object group ID from list_technology_objects in this CPU. Omit both destination fields for the technology-object root; mutually exclusive with groupPath."),
                    McpP("groupPath", "string", false, "Exact PLC/group path from list_technology_objects, used when a group has no native ID. Must resolve uniquely within this CPU. Mutually exclusive with groupObjectId; this does not create folders."),
                    McpP("name", "string", true, "Nonblank native name of the new technology object. TIA validates naming and uniqueness. This is creation, not an existing-object selector."),
                    McpP("systemLibElement", "string", true, "Native system-library element associated with the new technology object, for example PID_Compact. This is not a fixed catalogue; TIA validates the name."),
                    McpP("systemLibVersion", "string", true, "System-library version parsed as major.minor, for example 2.4. TIA validates it against the element.")),
                McpT("set_technology_object_parameters", "Set one or more parameters on an existing technology object through its Parameters composition. Each entry is found by name and assigned Value. Duplicate names are rejected. A missing name or rejected value is an error; earlier assignments in the same call stay applied. Does not save, compile or retry.", process,
                    McpP("objectId", "string", true, "Opaque technology-object objectId from list_technology_objects or get_technology_object. Preserve exactly."),
                    ("parameters", true, new Dictionary<string, object>
                    {
                        ["type"] = "array", ["minItems"] = 1,
                        ["description"] = "One or more parameters to set. Names must be unique in this call. Values are JSON strings, booleans or finite numbers; TIA decides acceptance.",
                        ["items"] = new Dictionary<string, object>
                        {
                            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new[] { "name", "value" },
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["name"] = new Dictionary<string, object> { ["type"] = "string", ["minLength"] = 1, ["description"] = "Native TechnologicalParameter name. Preserve the name returned by get_technology_object." },
                                ["value"] = new Dictionary<string, object> { ["description"] = "JSON string, boolean or finite number assigned to TechnologicalParameter.Value. Null, arrays and objects are rejected.", ["type"] = new[] { "string", "boolean", "number" } }
                            }
                        }
                    })),
                McpT("compile_plc", "Compile the selected CPU's PLC software offline using native ICompilable.Compile(). Returns compilationSucceeded, native state/counts and recursive messages with paths, timestamps and descriptions. complete describes diagnostic retrieval, not compile success. Compiler errors set MCP isError while preserving diagnostics. This is the native compile operation, not a forced Rebuild all or historical UI-log reader. Requires full access and an offline target; never saves, uploads, downloads or retries.", process, cpu)
            });
        }
        return tools;
    }

    private static (string name, bool required, Dictionary<string, object> schema) McpE(
        (string name, bool required, Dictionary<string, object> schema) property, params object[] examples)
    {
        property.schema["examples"] = examples;
        return property;
    }

    private static (string name, bool required, Dictionary<string, object> schema) McpDocuments(bool xmlOnly = false) =>
        ("documents", true, new Dictionary<string, object>
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = xmlOnly ? 1 : 2,
            ["description"] = (xmlOnly ? "One complete SimaticML XML document." : "One external source or XML document, or one SIMATIC SD .s7dcl with optional same-stem .s7res.") +
                " Supply name/content only, without read-result checksums. The server stages exact UTF-8 text without BOM in owned temporary files, invokes native generation/import, then attempts cleanup. No client/server file paths; filenames do not select objects.",
            ["items"] = new { type = "object", additionalProperties = false, required = new[] { "name", "content" },
                properties = new
                {
                    name = new { type = "string", minLength = 1, maxLength = 128,
                        description = (xmlOnly ? "Plain .xml filename, for example Tags.xml." : "Plain filename with the selected format's extension, for example MotorStatus.udt or ScaleValue.scl; SD pairs share a stem.") +
                            " Used for temporary staging, not an object selector. Unique ignoring case, max 128 characters; no paths, reserved Windows names, control characters or trailing dot/space.",
                        examples = xmlOnly ? new[] { "Tags.xml" } : new[] { "MotorStatus.udt", "ScaleValue.scl" } },
                    content = new { type = "string", minLength = 1,
                        description = (xmlOnly ? "Complete native SimaticML XML text, including the document structure; not get_tag_table JSON." : "Complete native source text for this format, not a patch or member list. A .udt contains the full TYPE/STRUCT/END_STRUCT/END_TYPE declaration; .scl contains the complete block declaration and body; XML/SD retain their native structure.") +
                            " Must be nonblank valid Unicode. Use actual newlines in this editor; JSON clients encode them as \\n. Declarations determine the names TIA creates or replaces; changing the filename alone does not rename an object." }
                } }
        });

    internal static bool IsWrite(string name) => name is "write_blocks" or "write_udts" or "create_tag_table" or
        "create_tag" or "create_user_constant" or "set_tag_entry_attribute" or "delete_tag_entry" or "import_tag_tables" or
        "delete_block" or "delete_udt" or "delete_tag_table" or "create_technology_object" or "set_technology_object_parameters" or "compile_plc";

    private static McpToolDefinition McpT(string name, string description,
        params (string name, bool required, Dictionary<string, object> schema)[] properties) => new()
    {
        Name = name, Description = description, Annotations = new McpToolAnnotations { ReadOnlyHint = !IsWrite(name) },
        InputSchema = new McpInputSchema
        {
            Properties = properties.ToDictionary(p => p.name, p => (object)p.schema),
            Required = properties.Where(p => p.required).Select(p => p.name).ToArray(),
            AllOf = name is "get_block" or "get_udt" ? new object[] { new Dictionary<string, object>
            {
                ["if"] = new { required = new[] { "includeDependencies" }, properties = new { includeDependencies = new { @const = true } } },
                ["then"] = new { required = new[] { "sourceFormat" }, properties = new
                    { sourceFormat = new { @const = "external-source" }, includeSource = new { @const = true } } }
            } } : properties.Any(property => property.name == "groupObjectId")
                ? new object[] { new { @not = new { required = new[] { "groupObjectId", "groupPath" } } } } : null
        }
    };

    private static (string name, bool required, Dictionary<string, object> schema) McpP(
        string name, string type, bool required, string description, object? defaultValue = null, params string[] values)
    {
        var schema = new Dictionary<string, object> { ["type"] = type, ["description"] = description };
        if (type == "integer") { schema["minimum"] = 1; schema["maximum"] = int.MaxValue; }
        if (type == "string" && required) { schema["minLength"] = 1; schema["pattern"] = @"\S"; }
        if (defaultValue != null) schema["default"] = defaultValue;
        if (values.Length > 0) schema["enum"] = values;
        return (name, required, schema);
    }

    internal async Task<(object? result, object? rpcErr)> HandleAsync(McpRpcRequest body)
    {
        switch (body.Method)
        {
            case "initialize":
                var clientVersion = body.Params is { ValueKind: JsonValueKind.Object } parameters &&
                    parameters.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString() : null;
                return (new { protocolVersion = clientVersion == "2024-11-05" ? "2024-11-05" : "2025-03-26",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "tia-portal-openness", version = "native-compile-delete-export-1" },
                    instructions = (_operations.WriteToolsAvailable ? "Fourteen read tools and fourteen modifying operations, including offline PLC compilation. Changes are not saved automatically. " : "Fourteen read-only tools. ") +
                        "Discover with list_tia_processes. The user connects existing TIA UI processes in the dashboard; MCP never attaches or reconnects. Supply processId on every project operation and native selectors. Inspect complete, errors, affectedObjects and compilationSucceeded. Native writes can partially change the project on failure; never retry automatically. Saving and PLC upload/download remain human responsibilities and are not published operations." }, null);
            case "ping": return (new { }, null);
            case "tools/list": return (new { tools = ToolDefs(_operations.WriteToolsAvailable) }, null);
            case "tools/call": return (await CallAsync(body.Params), null);
            default: return (null, new { code = -32601, message = "Method not found: " + body.Method });
        }
    }

    private async Task<object> CallAsync(JsonElement? parameters)
    {
        using var context = OperationCallContext.Begin(OperationCallContext.Current?.Origin ?? "mcp");
        JsonElement? requestedProcess = null;
        string operation = "tools/call";
        var started = Stopwatch.StartNew();
        try
        {
            // Preserve the supplied process selector even when argument validation fails.
            if (parameters is { ValueKind: JsonValueKind.Object } candidate &&
                candidate.TryGetProperty("arguments", out var supplied) && supplied.ValueKind == JsonValueKind.Object &&
                supplied.TryGetProperty("processId", out var selected)) requestedProcess = selected;
            if (!(parameters is { ValueKind: JsonValueKind.Object } call))
                throw Invalid("Supply a tool name and an optional arguments object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in call.EnumerateObject())
                if (!names.Add(field.Name) || !(field.Name is "name" or "arguments" or "_meta"))
                    throw Invalid("Unknown or duplicate tool-call field: " + field.Name);
            if (!call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw Invalid("Supply a tool name.");
            operation = name.GetString()!;
            if (!Publishes(_operations.WriteToolsAvailable, operation))
                throw new ConnectionFault("unknownTool", 0, "This tool is not published.");
            using var empty = JsonDocument.Parse("{}");
            var args = call.TryGetProperty("arguments", out var arguments) ? arguments : empty.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw Invalid("arguments must be an object.");
            object payload;
            // Readers capture tickets before queueing. Do not schedule or attach here.
            switch (operation)
            {
                case "list_tia_processes":
                    if (args.EnumerateObject().Any()) throw Invalid("This tool accepts no arguments.");
                    payload = await _operations.DiscoverAsync(); break;
                case "get_status":
                    payload = !args.EnumerateObject().Any() ? _operations.BridgeStatus()
                        : await _operations.ReadStatusAsync(DiscoveryRequest.Parse(args, false).ProcessId); break;
                case "list_devices":
                    payload = await _operations.ListDevicesAsync(DiscoveryRequest.Parse(args, false).ProcessId); break;
                case "get_device":
                    var device = DiscoveryRequest.Parse(args, true);
                    payload = await _operations.ReadDeviceAsync(device.ProcessId, device.ObjectId!, device.IncludePath); break;
                case "list_blocks": case "list_udts": case "list_tag_tables": case "list_technology_objects":
                    var inventory = DiscoveryRequest.Parse(args, false, blocks: true);
                    payload = operation == "list_blocks" ? await _operations.ListBlocksAsync(inventory.ProcessId, inventory.PlcObjectId!)
                        : operation == "list_udts" ? await _operations.ListUdtsAsync(inventory.ProcessId, inventory.PlcObjectId!)
                        : operation == "list_tag_tables" ? await _operations.ListTagTablesAsync(inventory.ProcessId, inventory.PlcObjectId!)
                        : await _operations.ListTechnologyObjectsAsync(inventory.ProcessId, inventory.PlcObjectId!); break;
                case "get_block": payload = await _operations.ReadBlockAsync(BlockReadRequest.Parse(args)); break;
                case "get_udt": payload = await _operations.ReadUdtAsync(BlockReadRequest.Parse(args)); break;
                case "get_tag_table": payload = await _operations.ReadTagTableAsync(TagTableReadRequest.Parse(args)); break;
                case "get_technology_object": payload = await _operations.ReadTechnologyObjectAsync(TechnologyObjectReadRequest.Parse(args)); break;
                case "get_cross_references": payload = await _operations.ReadCrossReferencesAsync(CrossReferenceRequest.Parse(args)); break;
                case "export_tag_table": payload = await _operations.ExportTagTableAsync(ExportTagTableRequest.Parse(args)); break;
                case "compile_plc": payload = await _operations.CompileAsync(CompileRequest.Parse(args)); break;
                case "write_blocks": case "write_udts": case "create_tag_table": case "create_tag":
                case "create_user_constant": case "set_tag_entry_attribute": case "delete_tag_entry": case "import_tag_tables":
                case "delete_block": case "delete_udt": case "delete_tag_table":
                case "create_technology_object": case "set_technology_object_parameters":
                    payload = await _operations.WriteAsync(WriteRequest.Parse(operation, args)); break;
                default: throw new InvalidOperationException("Published tool has no dispatch.");
            }
            // Partial payloads and exact native errors remain in the reader's response envelope.
            var compilationFailed = payload is CompileResult compiled && compiled.CompilationSucceeded == false;
            NoteCall(operation, requestedProcess, payload, compilationFailed,
                compilationFailed ? "Compilation reported errors; see the returned compiler messages." : null, started);
            return ToolResult(payload, payload is WriteResult write && !write.Complete ||
                payload is CompileResult compile && (!compile.Complete || compile.CompilationSucceeded != true));
        }
        catch (Exception ex)
        {
            var errors = new List<DiscoveryError>();
            for (Exception? cause = ex; cause != null; cause = cause.InnerException)
            {
                if (cause is ConnectionFault && cause.InnerException?.Message == cause.Message) continue;
                errors.Add(new DiscoveryError { Origin = _isNative(cause) ? "tia-openness" : "bridge",
                    Operation = operation, Message = cause.Message });
            }
            var fault = ex as ConnectionFault;
            var payload = new Dictionary<string, object?>
            {
                ["readAtUtc"] = DateTimeOffset.UtcNow, ["errors"] = errors,
                ["error"] = new { code = fault?.Code ?? (_isNative(ex) ? (operation == "compile_plc" ? "nativeCompileFailed" : IsWrite(operation) ? "nativeWriteFailed" : "nativeReadFailed") : "bridgeFailure"),
                    message = ex.Message, reconnectRequired = fault?.ReconnectRequired ?? false }
            };
            if (requestedProcess.HasValue) payload["processId"] = requestedProcess.Value;
            NoteCall(operation, requestedProcess, null, true, ex.Message, started);
            return ToolResult(payload, true);
        }
    }

    private void NoteCall(string operation, JsonElement? requestedProcess, object? payload, bool failed, string? error, Stopwatch started)
    {
        try
        {
            int? process = requestedProcess is { ValueKind: JsonValueKind.Number } selected && selected.TryGetInt32(out var parsed) ? parsed : null;
            if (payload is DiscoveryResult discovery && discovery.ProcessId > 0) process = discovery.ProcessId;
            var partial = !failed && ((payload is DiscoveryResult result && result.Errors.Count > 0) ||
                (payload is ProcessDiscovery processes && processes.Errors.Count > 0));
            if (partial)
            {
                var errors = payload is DiscoveryResult discoveryErrors ? discoveryErrors.Errors :
                    ((ProcessDiscovery)payload!).Errors;
                error = string.Join(" | ", errors.Select(item => item.Origin + ": " + item.Message));
            }
            _journal?.Invoke(new OperationCallNote
            {
                Origin = OperationCallContext.Current?.Origin ?? "mcp",
                Operation = operation,
                ProcessId = process,
                ConnectionId = OperationCallContext.Current?.ConnectionId,
                ProjectPath = OperationCallContext.Current?.ProjectPath,
                DurationMs = started.Elapsed.TotalMilliseconds,
                Outcome = failed ? "error" : partial ? "partial" : "success",
                Error = error
            });
        }
        catch { /* A log failure must not replace the tool payload. */ }
    }

    private object ToolResult(object payload, bool failed) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload, _json) } }, isError = failed
    };
    private static ConnectionFault Invalid(string message) => new("invalidRequest", 0, message);
}
