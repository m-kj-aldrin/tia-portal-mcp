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
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var request = new TechnologyObjectReadRequest { ProcessId = processId };
        var allowed = new HashSet<string>(new[] { "processId", "objectId", "includePath", "includeParameters" });
        foreach (var field in root.EnumerateObject())
            if (!allowed.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        if (!root.TryGetProperty("objectId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank technology-object objectId.");
        request.ObjectId = id.GetString()!;
        bool Flag(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new ConnectionFault("invalidRequest", processId, name + " must be a boolean.");
            return value.GetBoolean();
        }
        request.IncludePath = Flag("includePath");
        request.IncludeParameters = Flag("includeParameters");
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
