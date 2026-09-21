using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Prototype;

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
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank block objectId.");
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

// Independent of Siemens so strict attempts, metadata-only reads and fallback can be exercised offline.
internal static class BlockSourceReader
{
    public static string[] Formats(string requested, string? language, bool dataBlock) => requested != "best" ? new[] { requested } :
        dataBlock || language is "SCL" or "STL" ? new[] { "external-source", "simatic-ml" } :
        language == "LAD" ? new[] { "simatic-sd", "simatic-ml" } : new[] { "simatic-ml" };

    public static void Read(BlockRead result, BlockReadRequest request, string? language, bool dataBlock,
        Func<string, BlockSource> export, Action validate, Func<Exception, string> origin)
    {
        if (!request.IncludeSource) return;
        var failures = new List<DiscoveryError>();
        foreach (var format in Formats(request.SourceFormat, language, dataBlock))
        {
            validate();
            try
            {
                var source = export(format);
                validate();
                if (source.Documents.Count == 0 || source.Documents.Any(document => string.IsNullOrWhiteSpace(document.Content)))
                    throw new InvalidOperationException("The export returned no usable source content.");
                result.Source = source;
                result.Attempts.Add(new SourceAttempt { Format = format, State = "success" });
                return; // Earlier attempts remain in the dashboard log, not successful result errors.
            }
            catch (ConnectionFault) { throw; }
            catch (Exception ex)
            {
                validate(); // Ordinary export failure must never conceal context loss.
                var errors = ex is SourceExportFault fault ? fault.Errors : new List<DiscoveryError>
                {
                    new() { Origin = origin(ex), Operation = "sourceExport", Format = format, Message = ex.Message }
                };
                failures.AddRange(errors);
                result.Attempts.Add(new SourceAttempt { Format = format, State = "failed", Errors = errors });
            }
        }
        result.Errors.AddRange(failures);
    }
}

internal static class BlockMetadata
{
    public static string? OptionalPath(bool include, Func<string?> read, Action validate)
    {
        if (!include) return null;
        try { return read(); }
        catch (ConnectionFault) { throw; }
        catch { validate(); return null; }
    }

    public static Dictionary<string, object?> Map(Dictionary<string, object?> attributes, string objectId, string? path, string blockType)
    {
        object? Take(string key)
        {
            if (!attributes.TryGetValue(key, out var value)) return null;
            attributes.Remove(key); return value;
        }
        return new Dictionary<string, object?>
        {
            ["objectId"] = objectId, ["path"] = path, ["blockType"] = blockType,
            ["name"] = Take("Name"), ["number"] = Take("Number"), ["autoNumber"] = Take("AutoNumber"),
            ["namespace"] = Take("Namespace"), ["programmingLanguage"] = Take("ProgrammingLanguage"), ["memoryLayout"] = Take("MemoryLayout"),
            ["header"] = new Dictionary<string, object?> { ["author"] = Take("HeaderAuthor"), ["family"] = Take("HeaderFamily"),
                ["userDefinedId"] = Take("HeaderName"), ["version"] = Take("HeaderVersion") },
            ["state"] = new Dictionary<string, object?> { ["isConsistent"] = Take("IsConsistent"), ["isKnowHowProtected"] = Take("IsKnowHowProtected") },
            ["timestamps"] = new Dictionary<string, object?> { ["created"] = Take("CreationDate"), ["modified"] = Take("ModifiedDate"),
                ["compiled"] = Take("CompileDate"), ["codeModified"] = Take("CodeModifiedDate"), ["interfaceModified"] = Take("InterfaceModifiedDate"),
                ["parameterModified"] = Take("ParameterModified"), ["structureModified"] = Take("StructureModified") },
            ["typeSpecific"] = attributes
        };
    }
}
