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
    public string? SourceFormat { get; private set; }
    public bool IncludeDependencies { get; private set; }

    public static BlockReadRequest Parse(JsonElement root)
    {
        var processId = RequestValidation.PositiveProcessId(root);
        var request = new BlockReadRequest { ProcessId = processId };
        RequestValidation.AllowedFields(root, processId, new[] { "processId", "objectId", "includePath", "includeSource", "sourceFormat", "includeDependencies" });
        request.ObjectId = RequestValidation.RequiredString(root, processId, "objectId", "Supply a nonblank objectId.");
        request.IncludePath = RequestValidation.BooleanFlag(root, processId, "includePath");
        request.IncludeSource = RequestValidation.BooleanFlag(root, processId, "includeSource");
        request.IncludeDependencies = RequestValidation.BooleanFlag(root, processId, "includeDependencies", false);
        if (root.TryGetProperty("sourceFormat", out var format))
        {
            if (format.ValueKind != JsonValueKind.String || !(format.GetString() is "simatic-ml" or "simatic-sd" or "external-source"))
                throw new ConnectionFault("invalidRequest", processId, "Choose an explicit sourceFormat: simatic-ml, simatic-sd or external-source.");
            request.SourceFormat = format.GetString()!;
        }
        if (request.IncludeSource && request.SourceFormat == null)
            throw new ConnectionFault("invalidRequest", processId, "sourceFormat is required when includeSource is true (the default).");
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
