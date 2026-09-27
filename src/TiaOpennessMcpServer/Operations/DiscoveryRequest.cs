using System.Text.Json;

namespace TiaOpennessMcpServer.Operations;

internal sealed class DiscoveryRequest
{
    public int ProcessId { get; private set; }
    public string? ObjectId { get; private set; }
    public string? PlcObjectId { get; private set; }
    public bool IncludePath { get; private set; } = true;

    public static DiscoveryRequest Parse(JsonElement root, bool device, bool blocks = false)
    {
        var processId = RequestValidation.PositiveProcessId(root);
        var result = new DiscoveryRequest { ProcessId = processId };
        var allowed = new List<string> { "processId" };
        if (device) allowed.AddRange(new[] { "objectId", "includePath" });
        if (blocks) allowed.Add("plcObjectId");
        RequestValidation.AllowedFields(root, processId, allowed, "Unknown or duplicate request field: ");
        if (blocks)
            result.PlcObjectId = RequestValidation.RequiredString(root, processId, "plcObjectId", "Supply a nonblank CPU DeviceItem plcObjectId.");
        if (device)
        {
            result.ObjectId = RequestValidation.RequiredString(root, processId, "objectId", "Supply a nonblank Device objectId.");
            result.IncludePath = RequestValidation.BooleanFlag(root, processId, "includePath");
        }
        return result;
    }
}
