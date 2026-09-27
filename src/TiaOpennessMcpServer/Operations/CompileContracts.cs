using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class CompileRequest
{
    public int ProcessId { get; private set; }
    public string PlcObjectId { get; private set; } = "";

    public static CompileRequest Parse(JsonElement root)
    {
        var processId = RequestValidation.PositiveProcessId(root);
        RequestValidation.AllowedFields(root, processId, new[] { "processId", "plcObjectId" });
        return new CompileRequest { ProcessId = processId,
            PlcObjectId = RequestValidation.RequiredString(root, processId, "plcObjectId", "Supply a nonblank CPU DeviceItem plcObjectId.") };
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
