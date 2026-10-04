using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class HardwareCatalogRequest
{
    // Check exact identifiers first so combined searches avoid unnecessary native field reads.
    internal static readonly string[] Fields =
    {
        "typeIdentifier", "typeIdentifierNormalized", "articleNumber", "typeName",
        "version", "catalogPath", "description"
    };

    public int ProcessId { get; private set; }
    public int Offset { get; private set; }
    public int Limit { get; private set; } = 100;
    public IReadOnlyDictionary<string, string> Filters { get; private set; } = new Dictionary<string, string>();

    public static HardwareCatalogRequest Parse(JsonElement root)
    {
        var processId = RequestValidation.PositiveProcessId(root);
        RequestValidation.AllowedFields(root, processId, Fields.Concat(new[] { "processId", "offset", "limit" }));
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
            if (root.TryGetProperty(field, out _))
                filters.Add(field, RequestValidation.RequiredString(root, processId, field, field + " must be nonblank text; omit unused filters."));
        return new HardwareCatalogRequest
        {
            ProcessId = processId, Filters = filters,
            Offset = Integer(root, processId, "offset", 0, 0, int.MaxValue),
            Limit = Integer(root, processId, "limit", 100, 1, 500)
        };
    }

    private static int Integer(JsonElement root, int processId, string name, int fallback, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new ConnectionFault("invalidRequest", processId, name + " must be an integer from " + minimum + " to " + maximum + ".");
        return number;
    }

    internal static bool Matches(string field, string? value, string filter) => value != null && (field switch
    {
        "typeIdentifier" or "typeIdentifierNormalized" => string.Equals(value, filter, StringComparison.Ordinal),
        "version" => string.Equals(value, filter, StringComparison.OrdinalIgnoreCase),
        _ => value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
    });
}

internal sealed class HardwareCatalogRead : DiscoveryResult
{
    public List<Dictionary<string, string?>> Items { get; } = new();
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int ReturnedCount => Items.Count;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? TotalMatches { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? HasMore { get; set; }
}

// Read delegates are consumed synchronously on the STA; only their string values leave it.
internal sealed class HardwareCatalogNode
{
    public Func<string, string?> ReadField { get; set; } = _ => null;
}
