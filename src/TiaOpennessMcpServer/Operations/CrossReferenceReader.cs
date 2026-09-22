namespace TiaOpennessMcpServer.Operations;

// Lazy native adapters stay inside the guarded STA read. Fields are native values, not derived links.
internal class CrossObjectNode
{
    public Dictionary<string, Func<object?>> Fields { get; } = new();
}

internal sealed class CrossSourceNode : CrossObjectNode
{
    public Func<IEnumerable<CrossSourceNode>> Children { get; set; } = () => Array.Empty<CrossSourceNode>();
    public Func<IEnumerable<CrossReferenceNode>> References { get; set; } = () => Array.Empty<CrossReferenceNode>();
}

internal sealed class CrossReferenceNode : CrossObjectNode
{
    public Func<IEnumerable<CrossObjectNode>> Locations { get; set; } = () => Array.Empty<CrossObjectNode>();
}

internal sealed class CrossReferenceReader
{
    private readonly DiscoveryReadContext _read;
    public CrossReferenceReader(CrossReferenceRead result, Action validate) => _read = new DiscoveryReadContext(result.Errors, validate);

    public void Read(CrossReferenceRead result, Func<IEnumerable<CrossSourceNode>> sources) =>
        result.Sources = _read.Collect(sources, source => Source(source, "sources"), "sources");

    private Dictionary<string, object?> Fields(CrossObjectNode source, string location)
    {
        var result = new Dictionary<string, object?>();
        foreach (var field in source.Fields)
            result[field.Key] = _read.Read(() => DiscoveryValues.Convert(field.Value()), field.Key, location);
        return result;
    }

    private Dictionary<string, object?> Source(CrossSourceNode source, string location)
    {
        var result = Fields(source, location);
        var path = result.TryGetValue("path", out var value) && value is string text ? text : location;
        result["children"] = _read.Collect(source.Children, child => Source(child, path), path + "/children");
        result["references"] = _read.Collect(source.References, reference => Reference(reference, path), path + "/references");
        return result;
    }

    private Dictionary<string, object?> Reference(CrossReferenceNode source, string location)
    {
        var result = Fields(source, location);
        var path = result.TryGetValue("path", out var value) && value is string text ? text : location;
        result["locations"] = _read.Collect(source.Locations, item => Fields(item, path), path + "/locations");
        return result;
    }
}
