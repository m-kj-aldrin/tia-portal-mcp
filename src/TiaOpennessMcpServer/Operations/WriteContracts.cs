using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

// Managed arguments shared by MCP, the guarded service and the Siemens-free harness.
internal sealed class WriteRequest
{
    public string Tool { get; private set; } = "";
    public int ProcessId { get; private set; }
    public string? ObjectId { get; private set; }
    public string? PlcObjectId { get; private set; }
    public string? GroupObjectId { get; private set; }
    public string? GroupPath { get; private set; }
    public string? Name { get; private set; }
    public string? DataType { get; private set; }
    public string? LogicalAddress { get; private set; }
    public string? Value { get; private set; }
    public string? AttributeName { get; private set; }
    public object? AttributeValue { get; private set; }
    public string? SourceFormat { get; private set; }
    public List<WriteDocument> Documents { get; } = new();

    public static WriteRequest Parse(string tool, JsonElement root)
    {
        var request = new WriteRequest { Tool = tool };
        ConnectionFault Invalid(string message) => new("invalidRequest", request.ProcessId, message);
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("Supply an arguments object.");
        if (!root.TryGetProperty("processId", out var process) || !process.TryGetInt32Safe(out var processId) || processId <= 0)
            throw Invalid("Supply a positive integer processId.");
        request.ProcessId = processId;
        var allowed = tool switch
        {
            "write_blocks" or "write_udts" => new[] { "processId", "plcObjectId", "groupObjectId", "groupPath", "sourceFormat", "documents" },
            "import_tag_tables" => new[] { "processId", "plcObjectId", "groupObjectId", "groupPath", "documents" },
            "create_tag_table" => new[] { "processId", "plcObjectId", "groupObjectId", "groupPath", "name" },
            "create_tag" => new[] { "processId", "objectId", "name", "dataType", "logicalAddress" },
            "create_user_constant" => new[] { "processId", "objectId", "name", "dataType", "value" },
            "set_tag_entry_attribute" => new[] { "processId", "objectId", "attributeName", "attributeValue" },
            "delete_tag_entry" or "delete_block" or "delete_udt" or "delete_tag_table" => new[] { "processId", "objectId" },
            _ => throw Invalid("Unknown write tool.")
        };
        var fields = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!fields.Remove(field.Name)) throw Invalid("Unknown or duplicate field: " + field.Name);
        string? Text(string name, bool required = false)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                if (required) throw Invalid("Supply " + name + ".");
                return null;
            }
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw Invalid(name + " must be a nonblank string.");
            return value.GetString(); // Native IDs and supplied values are opaque; do not trim.
        }
        var destination = allowed.Contains("plcObjectId");
        request.PlcObjectId = Text("plcObjectId", destination);
        request.ObjectId = Text("objectId", !destination);
        request.GroupObjectId = Text("groupObjectId");
        request.GroupPath = Text("groupPath");
        if (request.GroupObjectId != null && request.GroupPath != null)
            throw Invalid("Supply groupObjectId or groupPath, not both.");
        request.Name = Text("name", allowed.Contains("name"));
        request.DataType = Text("dataType", allowed.Contains("dataType"));
        request.LogicalAddress = Text("logicalAddress", tool == "create_tag");
        request.Value = Text("value", tool == "create_user_constant");
        request.AttributeName = Text("attributeName", tool == "set_tag_entry_attribute");
        if (tool == "set_tag_entry_attribute")
        {
            if (!root.TryGetProperty("attributeValue", out var value)) throw Invalid("Supply attributeValue.");
            request.AttributeValue = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
                JsonValueKind.Number when value.TryGetInt64(out var large) => large,
                JsonValueKind.Number when value.TryGetDouble(out var number) && !double.IsInfinity(number) && !double.IsNaN(number) => number,
                _ => throw Invalid("attributeValue must be a string, boolean or finite number.")
            };
        }
        if (allowed.Contains("documents"))
        {
            request.SourceFormat = tool == "import_tag_tables" ? "simatic-ml" : Text("sourceFormat", true);
            if (request.SourceFormat is not ("external-source" or "simatic-sd" or "simatic-ml"))
                throw Invalid("Choose an explicit sourceFormat: external-source, simatic-sd or simatic-ml.");
            if (!root.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array ||
                documents.GetArrayLength() < 1 || documents.GetArrayLength() > 2)
                throw Invalid("Supply one source document, or one SIMATIC SD declaration with its optional resource document.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents.EnumerateArray())
            {
                if (document.ValueKind != JsonValueKind.Object) throw Invalid("Each document needs name and content.");
                var keys = new HashSet<string>(new[] { "name", "content" });
                foreach (var property in document.EnumerateObject())
                    if (!keys.Remove(property.Name)) throw Invalid("Unknown or duplicate document field: " + property.Name);
                if (keys.Count != 0 || document.GetProperty("name").ValueKind != JsonValueKind.String ||
                    document.GetProperty("content").ValueKind != JsonValueKind.String)
                    throw Invalid("Each document needs string name and content fields.");
                var name = document.GetProperty("name").GetString()!;
                var content = document.GetProperty("content").GetString()!;
                var stem = Path.GetFileNameWithoutExtension(name);
                var device = stem.Split('.')[0].ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.EndsWith(".") || name.EndsWith(" ") ||
                    name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl) ||
                    device is "CON" or "PRN" or "AUX" or "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && char.IsDigit(device[3])) ||
                    !names.Add(name)) throw Invalid("Document names must be unique, plain file names, without paths or reserved names.");
                if (string.IsNullOrWhiteSpace(content)) throw Invalid("Document content must not be blank.");
                try { new UTF8Encoding(false, true).GetByteCount(content); }
                catch (EncoderFallbackException) { throw Invalid("Document content must contain valid Unicode."); }
                request.Documents.Add(new WriteDocument { Name = name, Content = content });
            }
            var extensions = request.Documents.Select(d => Path.GetExtension(d.Name).ToLowerInvariant()).ToArray();
            if (request.SourceFormat == "external-source" && (extensions.Length != 1 || !new[] { ".scl", ".awl", ".db", ".udt" }.Contains(extensions[0])))
                throw Invalid("External source requires one .scl, .awl, .db or .udt document.");
            if (request.SourceFormat == "simatic-ml" && (extensions.Length != 1 || extensions[0] != ".xml"))
                throw Invalid("SimaticML requires one .xml document.");
            if (request.SourceFormat == "simatic-sd" && (extensions.Count(e => e == ".s7dcl") != 1 ||
                extensions.Any(e => e != ".s7dcl" && e != ".s7res") ||
                request.Documents.Select(d => Path.GetFileNameWithoutExtension(d.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
                throw Invalid("SIMATIC SD requires one .s7dcl and an optional .s7res with the same file stem.");
        }
        return request;
    }
}

internal static class JsonWriteNumbers
{
    public static bool TryGetInt32Safe(this JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }
}

internal sealed class WriteDocument
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
}

internal sealed class WriteObject
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ObjectId { get; set; }
    public string? ParentObjectId { get; set; }
    public string Kind { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Name { get; set; }
}

internal sealed class WriteResult : DiscoveryResult
{
    public string Operation { get; set; } = "";
    public bool Saved => false;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool? ProjectModified { get; set; }
    public bool CleanupFailed => Errors.Any(error => error.Operation == "temporaryCleanup");
    public string? Format { get; set; }
    public string? NativeState { get; set; }
    public List<string> NativeMessages { get; set; } = new();
    public List<WriteObject> AffectedObjects { get; set; } = new();
}
