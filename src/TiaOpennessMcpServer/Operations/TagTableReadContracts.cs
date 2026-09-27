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
        var processId = RequestValidation.PositiveProcessId(root);
        var request = new TagTableReadRequest { ProcessId = processId };
        RequestValidation.AllowedFields(root, processId, new[] { "processId", "objectId", "includePath", "includeEntries" });
        request.ObjectId = RequestValidation.RequiredString(root, processId, "objectId", "Supply a nonblank tag-table objectId.");
        request.IncludePath = RequestValidation.BooleanFlag(root, processId, "includePath");
        request.IncludeEntries = RequestValidation.BooleanFlag(root, processId, "includeEntries");
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
