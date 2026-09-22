using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class TagTableReadRequest
{
    public int ProcessId { get; private set; }
    public string ObjectId { get; private set; } = "";
    public bool IncludePath { get; private set; } = true;
    public bool IncludeEntries { get; private set; } = true;

    public static TagTableReadRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var request = new TagTableReadRequest { ProcessId = processId };
        var allowed = new HashSet<string>(new[] { "processId", "objectId", "includePath", "includeEntries" });
        foreach (var field in root.EnumerateObject())
            if (!allowed.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        if (!root.TryGetProperty("objectId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank tag-table objectId.");
        request.ObjectId = id.GetString()!;
        bool Flag(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new ConnectionFault("invalidRequest", processId, name + " must be a boolean.");
            return value.GetBoolean();
        }
        request.IncludePath = Flag("includePath");
        request.IncludeEntries = Flag("includeEntries");
        return request;
    }
}

internal sealed class TagTableRead : DiscoveryResult
{
    public Dictionary<string, object?> Metadata { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TagTableEntries? Entries { get; set; }
}

internal sealed class TagTableEntries
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Dictionary<string, object?>>? Tags { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Dictionary<string, object?>>? UserConstants { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Dictionary<string, object?>>? SystemConstants { get; set; }
}
