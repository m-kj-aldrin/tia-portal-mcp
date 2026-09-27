using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class CrossReferenceRequest
{
    public int ProcessId { get; private set; }
    public string ObjectId { get; private set; } = "";

    public static CrossReferenceRequest Parse(JsonElement root)
    {
        var processId = RequestValidation.PositiveProcessId(root);
        RequestValidation.AllowedFields(root, processId, new[] { "processId", "objectId" });
        return new CrossReferenceRequest { ProcessId = processId,
            ObjectId = RequestValidation.RequiredString(root, processId, "objectId", "Supply a nonblank engineering objectId.") };
    }
}

internal sealed class CrossReferenceRead : DiscoveryResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Dictionary<string, object?>>? Sources { get; set; }
}
