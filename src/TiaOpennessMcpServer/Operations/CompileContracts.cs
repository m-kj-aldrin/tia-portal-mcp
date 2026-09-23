using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class CompileRequest
{
    public int ProcessId { get; private set; }
    public string PlcObjectId { get; private set; } = "";

    public static CompileRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var allowed = new HashSet<string>(new[] { "processId", "plcObjectId" }, StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!allowed.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        if (!root.TryGetProperty("plcObjectId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank CPU DeviceItem plcObjectId.");
        return new CompileRequest { ProcessId = processId, PlcObjectId = id.GetString()! };
    }
}

internal sealed class CompileResult : DiscoveryResult
{
    public string Operation => "compile_plc";
    public string PlcObjectId { get; set; } = "";
    public bool Saved => false;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool? ProjectModified { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? ErrorCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? WarningCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<CompileMessage>? Messages { get; set; }

    // Complete reports whether the API/diagnostic read completed. A normal compiler error
    // remains a fully read native result, but is never presented as successful compilation.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? CompilationSucceeded => !Complete || ErrorCount == null || WarningCount == null ||
        ErrorCount < 0 || WarningCount < 0 || State is not ("Success" or "Information" or "Warning" or "Error") || Messages == null
        ? null : State != "Error" && ErrorCount == 0;
}

internal sealed class CompileMessage
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Path { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? DateTime { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Description { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? ErrorCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? WarningCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public List<CompileMessage>? Messages { get; set; }
}
