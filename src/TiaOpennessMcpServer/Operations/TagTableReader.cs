namespace TiaOpennessMcpServer.Operations;

// Native callbacks are created and consumed on the shared STA. No native entry escapes.
internal sealed class TagTableEntryNode
{
    public Func<string?> Identifier { get; set; } = () => null;
    public Func<IEnumerable<KeyValuePair<string, object?>>> Attributes { get; set; } = () => Array.Empty<KeyValuePair<string, object?>>();
}

internal static class TagTableReader
{
    public static Dictionary<string, object?> Attributes(DiscoveryReadContext read,
        Func<IEnumerable<KeyValuePair<string, object?>>> attributes, string? path)
    {
        var result = new Dictionary<string, object?>();
        read.Collect(attributes, pair =>
        {
            // Multilingual content is outside the initial contract; never traverse its proxy.
            if (pair.Key != "Comment")
                result[pair.Key] = read.Read(() => DiscoveryValues.Convert(pair.Value), "attribute:" + pair.Key, path);
            return true;
        }, path);
        return result;
    }

    public static Dictionary<string, object?> Metadata(Dictionary<string, object?> attributes, string objectId, string? path) => new()
    {
        ["objectId"] = objectId, ["path"] = path, ["name"] = Take(attributes, "Name"),
        ["isDefault"] = Take(attributes, "IsDefault"),
        ["timestamps"] = new Dictionary<string, object?> { ["modified"] = Take(attributes, "ModifiedTimeStamp") },
        ["typeSpecific"] = attributes
    };

    public static void ReadEntries(TagTableRead result, bool includeEntries, Action validate,
        Func<IEnumerable<TagTableEntryNode>> tags, Func<IEnumerable<TagTableEntryNode>> userConstants,
        Func<IEnumerable<TagTableEntryNode>> systemConstants)
    {
        if (!includeEntries) return;
        var read = new DiscoveryReadContext(result.Errors, validate);
        var path = result.Metadata.TryGetValue("path", out var value) ? value as string : null;
        result.Entries = new TagTableEntries
        {
            Tags = read.Collect(tags, entry => Entry(read, entry, true, path), Location(path, "tags")),
            UserConstants = read.Collect(userConstants, entry => Entry(read, entry, false, path), Location(path, "userConstants")),
            SystemConstants = read.Collect(systemConstants, entry => Entry(read, entry, false, path), Location(path, "systemConstants"))
        };
    }

    private static Dictionary<string, object?> Entry(DiscoveryReadContext read, TagTableEntryNode entry, bool tag, string? path)
    {
        var attributes = Attributes(read, entry.Attributes, path);
        var name = Take(attributes, "Name");
        var location = name is string text ? Location(path, text) : path;
        return new Dictionary<string, object?>
        {
            ["objectId"] = read.Read(() => DiscoveryValues.Nonblank(entry.Identifier()), "identifier", location),
            ["name"] = name, ["dataType"] = Take(attributes, "DataTypeName"),
            [tag ? "logicalAddress" : "value"] = Take(attributes, tag ? "LogicalAddress" : "Value"),
            ["typeSpecific"] = attributes
        };
    }

    private static string Location(string? path, string name) => path == null ? name : path + "/" + name;
    private static object? Take(Dictionary<string, object?> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value)) return null;
        attributes.Remove(key); return value;
    }
}
