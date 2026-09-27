using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class TechnologyObjectReadRequest
{
    public int ProcessId { get; private set; }
    public string ObjectId { get; private set; } = "";
    public bool IncludePath { get; private set; } = true;
    public bool IncludeParameters { get; private set; } = true;

    public static TechnologyObjectReadRequest Parse(JsonElement root)
    {
        var processId = RequestValidation.PositiveProcessId(root);
        var request = new TechnologyObjectReadRequest { ProcessId = processId };
        RequestValidation.AllowedFields(root, processId, new[] { "processId", "objectId", "includePath", "includeParameters" });
        request.ObjectId = RequestValidation.RequiredString(root, processId, "objectId", "Supply a nonblank technology-object objectId.");
        request.IncludePath = RequestValidation.BooleanFlag(root, processId, "includePath");
        request.IncludeParameters = RequestValidation.BooleanFlag(root, processId, "includeParameters");
        return request;
    }
}

internal sealed class TechnologyObjectRead : DiscoveryResult
{
    public Dictionary<string, object?> Metadata { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Dictionary<string, object?>>? Parameters { get; set; }
}

internal sealed class TechnologyParameterNode
{
    public Func<string?> Name { get; set; } = () => null;
    public Func<object?> Value { get; set; } = () => null;
    public Func<string?> ObjectId { get; set; } = () => null;
}

internal static class TechnologyObjectReader
{
    public static List<Dictionary<string, object?>>? ReadParameters(TechnologyObjectRead result, Action validate,
        Func<IEnumerable<TechnologyParameterNode>> parameters)
    {
        var read = new DiscoveryReadContext(result.Errors, validate);
        var path = result.Metadata.TryGetValue("path", out var value) ? value as string : null;
        return read.Collect(parameters, parameter => Entry(read, parameter, path), path);
    }

    private static Dictionary<string, object?> Entry(DiscoveryReadContext read, TechnologyParameterNode parameter, string? path)
    {
        var name = read.Read(parameter.Name, "name", path);
        var location = name == null ? path : path == null ? name : path + "/" + name;
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["value"] = read.Read(() => DiscoveryValues.Convert(parameter.Value()), "value", location),
            ["objectId"] = read.Read(() => DiscoveryValues.Nonblank(parameter.ObjectId()), "identifier", location)
        };
    }
}
