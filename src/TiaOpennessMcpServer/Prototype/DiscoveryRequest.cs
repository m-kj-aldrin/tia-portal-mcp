using System.Text.Json;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class DiscoveryRequest
{
    public int ProcessId { get; private set; }
    public string? ObjectId { get; private set; }
    public string? PlcObjectId { get; private set; }
    public bool IncludePath { get; private set; } = true;

    public static DiscoveryRequest Parse(JsonElement root, bool device, bool blocks = false)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var result = new DiscoveryRequest { ProcessId = processId };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!names.Add(field.Name) || (field.Name != "processId" &&
                !(device && (field.Name == "objectId" || field.Name == "includePath")) &&
                !(blocks && field.Name == "plcObjectId")))
                throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate request field: " + field.Name);
        }
        if (blocks)
        {
            if (!root.TryGetProperty("plcObjectId", out var plcId) || plcId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(plcId.GetString()))
                throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank CPU DeviceItem plcObjectId.");
            result.PlcObjectId = plcId.GetString();
        }
        if (device)
        {
            if (!root.TryGetProperty("objectId", out var objectId) || objectId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(objectId.GetString()))
                throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank Device objectId.");
            result.ObjectId = objectId.GetString(); // Native identifiers are opaque; never trim or reinterpret them.
            if (root.TryGetProperty("includePath", out var path))
            {
                if (path.ValueKind != JsonValueKind.True && path.ValueKind != JsonValueKind.False)
                    throw new ConnectionFault("invalidRequest", processId, "includePath must be a boolean.");
                result.IncludePath = path.GetBoolean();
            }
        }
        return result;
    }
}
