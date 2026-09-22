using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

// Shared block/UDT source-read options and response envelope.
internal sealed class BlockReadRequest
{
    public int ProcessId { get; private set; }
    public string ObjectId { get; private set; } = "";
    public bool IncludePath { get; private set; } = true;
    public bool IncludeSource { get; private set; } = true;
    public string SourceFormat { get; private set; } = "best";
    public bool IncludeDependencies { get; private set; }

    public static BlockReadRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var request = new BlockReadRequest { ProcessId = processId };
        var allowed = new HashSet<string>(new[] { "processId", "objectId", "includePath", "includeSource", "sourceFormat", "includeDependencies" });
        foreach (var field in root.EnumerateObject())
            if (!allowed.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        if (!root.TryGetProperty("objectId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank objectId.");
        request.ObjectId = id.GetString()!;
        bool Flag(string name, bool fallback)
        {
            if (!root.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new ConnectionFault("invalidRequest", processId, name + " must be a boolean.");
            return value.GetBoolean();
        }
        request.IncludePath = Flag("includePath", true);
        request.IncludeSource = Flag("includeSource", true);
        request.IncludeDependencies = Flag("includeDependencies", false);
        if (root.TryGetProperty("sourceFormat", out var format))
        {
            if (format.ValueKind != JsonValueKind.String || !(format.GetString() is "best" or "external-source" or "simatic-sd" or "simatic-ml"))
                throw new ConnectionFault("invalidRequest", processId, "Unknown sourceFormat.");
            request.SourceFormat = format.GetString()!;
        }
        if (request.IncludeDependencies && (!request.IncludeSource || request.SourceFormat != "external-source"))
            throw new ConnectionFault("invalidRequest", processId, "includeDependencies requires source and explicit external-source format.");
        return request;
    }
}

internal sealed class BlockRead : DiscoveryResult
{
    public Dictionary<string, object?> Metadata { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BlockSource? Source { get; set; }
    [JsonIgnore]
    public List<SourceAttempt> Attempts { get; } = new();
}

internal sealed class BlockSource
{
    public string Format { get; set; } = "";
    public bool DependenciesIncluded { get; set; }
    public string ContentScope => "native-permitted-content";
    public string? NativeState { get; set; }
    public List<string> NativeMessages { get; set; } = new();
    public List<SourceDocument> Documents { get; set; } = new();
}

internal sealed class SourceDocument
{
    public string Name { get; }
    public string Content { get; }
    public object Checksum { get; }
    public SourceDocument(string name, string content)
    {
        Name = name; Content = content;
        using var sha = SHA256.Create();
        Checksum = new { algorithm = "sha-256", scope = "returned-content", encoding = "utf-8-no-bom",
            value = BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false, true).GetBytes(content))).Replace("-", "").ToLowerInvariant() };
    }
}

internal sealed class SourceAttempt
{
    public string Format { get; set; } = "";
    public string State { get; set; } = "";
    public List<DiscoveryError> Errors { get; set; } = new();
}

internal sealed class SourceExportFault : Exception
{
    public List<DiscoveryError> Errors { get; }
    public SourceExportFault(List<DiscoveryError> errors) : base("Native source export did not return a complete representation.") => Errors = errors;
}
