using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class TagTableReadRequest
{
    public int ProcessId { get; private set; }
    public string ObjectId { get; private set; } = "";
    public bool IncludePath { get; private set; } = true;
    public bool IncludeEntries { get; private set; } = true;

    public static TagTableReadRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var request = new TagTableReadRequest { ProcessId = processId };
        var allowed = new HashSet<string>(new[] { "processId", "objectId", "includePath", "includeEntries" });
        foreach (var field in root.EnumerateObject())
            if (!allowed.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        if (!root.TryGetProperty("objectId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank tag-table objectId.");
        request.ObjectId = id.GetString()!;
        bool Flag(string name)
        {
            if (!root.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new ConnectionFault("invalidRequest", processId, name + " must be a boolean.");
            return value.GetBoolean();
        }
        request.IncludePath = Flag("includePath");
        request.IncludeEntries = Flag("includeEntries");
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
