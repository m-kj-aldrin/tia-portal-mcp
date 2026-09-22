using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

internal static class BlockReadTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("block read: strict selectors, flags and dependency combination", Requests);
        yield return ("block read: metadata-only never exports or computes source", MetadataOnly);
        yield return ("block read: best routing covers DB SCL STL LAD and conservative fallback", Routing);
        yield return ("block read: fallback logs failed attempts without polluting success", Fallback);
        yield return ("block read: strict formats attempt once and retain metadata on failure", Strict);
        yield return ("block read: all failures preserve native messages and reject partial export", Failures);
        yield return ("block read: context loss prevents fallback and payload return", ContextLoss);
        yield return ("block read: exact content checksum and explicit null source", Checksums);
        yield return ("block read: metadata mapping preserves attributes and HeaderName meaning", Metadata);
        yield return ("block read: optional path skips traversal and rechecks swallowed failures", Paths);
        yield return ("block read: V20 wrapped enum survives bulk mapping and drives best export", WrappedEnum);
    }

    private static BlockReadRequest Request(string fields = "")
    {
        using var doc = JsonDocument.Parse("{\"processId\":20,\"objectId\":\" block ID \"" + fields + "}");
        return BlockReadRequest.Parse(doc.RootElement);
    }
    private static BlockSource Source(string format) => new() { Format = format, Documents = new() { new SourceDocument("block.scl", "abc\r\n") } };
    private static void Read(BlockRead result, BlockReadRequest request, Func<string, BlockSource> export, Action? validate = null) =>
        BlockSourceReader.Read(result, request, "SCL", false, export, validate ?? (() => { }), _ => "tia-openness");

    private static void Requests()
    {
        var defaults = Request();
        Check(defaults.ObjectId == " block ID " && defaults.IncludeSource && defaults.IncludePath && !defaults.IncludeDependencies && defaults.SourceFormat == "best", "Defaults/opaque ID changed.");
        Check(Request(",\"sourceFormat\":\"external-source\",\"includeDependencies\":true").IncludeDependencies, "Valid dependencies rejected.");
        foreach (var body in new[] { "null", "[]", "{}", "{\"processId\":\"20\",\"objectId\":\"x\"}",
            "{\"processId\":20,\"objectId\":\" \"}", "{\"processId\":20,\"objectId\":\"x\",\"objectId\":\"y\"}",
            "{\"processId\":20,\"objectId\":\"x\",\"plcObjectId\":\"y\"}" })
        {
            using var doc = JsonDocument.Parse(body);
            Invalid(() => BlockReadRequest.Parse(doc.RootElement));
        }
        foreach (var extra in new[] { ",\"includeSource\":null", ",\"includePath\":1", ",\"sourceFormat\":\"scl\"",
            ",\"includeDependencies\":true", ",\"sourceFormat\":\"simatic-sd\",\"includeDependencies\":true",
            ",\"sourceFormat\":\"external-source\",\"includeSource\":false,\"includeDependencies\":true", ",\"password\":\"x\"" })
            Invalid(() => Request(extra));
    }
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("Invalid request accepted."); }
        catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
    }
    private static void MetadataOnly()
    {
        var result = new BlockRead();
        Read(result, Request(",\"includeSource\":false"), _ => throw new Exception("Export called"), () => throw new Exception("Source guard called"));
        Check(result.Source == null && result.Attempts.Count == 0 && result.Errors.Count == 0, "Metadata-only exported.");
    }
    private static void Routing()
    {
        foreach (var language in new[] { "SCL", "STL" })
            Check(BlockSourceReader.Formats("best", language, false).SequenceEqual(new[] { "external-source", "simatic-ml" }), "Text routing failed.");
        Check(BlockSourceReader.Formats("best", "DB", true).SequenceEqual(new[] { "external-source", "simatic-ml" }), "DB routing failed.");
        Check(BlockSourceReader.Formats("best", "LAD", false).SequenceEqual(new[] { "simatic-sd", "simatic-ml" }), "LAD routing failed.");
        foreach (var language in new string?[] { "FBD", "GRAPH", "Mixed", "unknown", null })
            Check(BlockSourceReader.Formats("best", language, false).SequenceEqual(new[] { "simatic-ml" }), "Unknown/mixed language guessed.");
    }
    private static void Fallback()
    {
        var result = new BlockRead();
        Read(result, Request(), format => format == "external-source" ? throw new Exception("Native refusal\r\nexact") : Source(format));
        Check(result.Source?.Format == "simatic-ml" && result.Errors.Count == 0, "Successful fallback polluted.");
        Check(result.Attempts.Count == 2 && result.Attempts[0].Errors[0].Message == "Native refusal\r\nexact", "Attempt evidence lost.");
    }
    private static void Strict()
    {
        var result = new BlockRead { Metadata = new() { ["name"] = "protected" } };
        var count = 0;
        Read(result, Request(",\"sourceFormat\":\"external-source\""), _ => { count++; throw new Exception("Native denial"); });
        Check(count == 1 && result.Source == null && (string?)result.Metadata["name"] == "protected" && result.Errors.Single().Format == "external-source", "Strict export fell back/lost metadata.");
    }
    private static void Failures()
    {
        var result = new BlockRead();
        Read(result, Request(), format => throw new SourceExportFault(new() { new DiscoveryError { Format = format, Message = "Native PartialSuccess detail" } }));
        Check(result.Source == null && result.Errors.Count == 2 && result.Attempts.All(attempt => attempt.State == "failed"), "Partial accepted or failures lost.");
        var empty = new BlockRead();
        Read(empty, Request(",\"sourceFormat\":\"simatic-ml\""), format => new BlockSource { Format = format });
        Check(empty.Source == null && empty.Errors.Count == 1, "Empty success accepted.");
    }
    private static void ContextLoss()
    {
        foreach (var failExport in new[] { false, true })
        {
            var alive = true; var calls = 0; var result = new BlockRead();
            try
            {
                Read(result, Request(), format => { calls++; alive = false; if (failExport) throw new Exception("Lost proxy"); return Source(format); },
                    () => { if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Changed"); });
                throw new Exception("Context loss swallowed.");
            }
            catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
            Check(calls == 1 && result.Source == null, "Retried or exposed changed context.");
        }
    }
    private static void Checksums()
    {
        var abc = new SourceDocument("block.scl", "abc");
        var hash = JsonSerializer.SerializeToElement(abc.Checksum);
        Check(hash.GetProperty("value").GetString() == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Incorrect SHA-256.");
        Check(JsonSerializer.Serialize(new SourceDocument("x", "a\r\n").Checksum) != JsonSerializer.Serialize(new SourceDocument("x", "a\n").Checksum), "Line endings normalized.");
        var text = "Å😀\r\n";
        Check(new SourceDocument("x", text).Content == text, "Content changed.");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var result = JsonSerializer.SerializeToElement(new BlockRead(), options);
        Check(result.GetProperty("source").ValueKind == JsonValueKind.Null && !result.TryGetProperty("attempts", out _), "Source null missing or internal attempts leaked.");
    }
    private static void Metadata()
    {
        var attributes = new Dictionary<string, object?> { ["Name"] = "FC1", ["HeaderName"] = "User ID", ["HeaderAuthor"] = "Author",
            ["ProgrammingLanguage"] = "SCL", ["NativeExtra"] = 42 };
        var mapped = BlockMetadata.Map(attributes, "opaque", null, "FC");
        var json = JsonSerializer.SerializeToElement(mapped);
        Check(json.GetProperty("header").GetProperty("userDefinedId").GetString() == "User ID", "HeaderName misrepresented.");
        Check(json.GetProperty("typeSpecific").EnumerateObject().Count() == 1 && json.GetProperty("typeSpecific").GetProperty("NativeExtra").GetInt32() == 42, "Extra attributes lost/duplicated.");
        Check(json.GetProperty("number").ValueKind == JsonValueKind.Null && json.GetProperty("path").ValueKind == JsonValueKind.Null, "Unavailable metadata guessed.");
    }
    private static void Paths()
    {
        var reached = false; var validated = false;
        Check(BlockMetadata.OptionalPath(false, () => { reached = true; return "path"; }, () => { }) == null && !reached, "Disabled path traversed.");
        Check(BlockMetadata.OptionalPath(true, () => throw new Exception("Native path failed"), () => validated = true) == null && validated, "Path failure skipped guard.");
        try { BlockMetadata.OptionalPath(true, () => throw new Exception("Lost proxy"), () => throw new ConnectionFault("reconnectRequired", 20, "Changed")); throw new Exception("Guard swallowed."); }
        catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
    }
    private static void WrappedEnum()
    {
        foreach (var entry in new[] { (Language: "SCL", First: "external-source"), (Language: "LAD", First: "simatic-sd"),
            (Language: "STL", First: "external-source"), (Language: "FBD", First: "simatic-ml") })
        {
            var attributes = new Dictionary<string, object?>
            {
                ["ProgrammingLanguage"] = DiscoveryValues.Convert(new Siemens.Engineering.Contract.EnumToClientRepresentation("ProgrammingLanguage", entry.Language)),
                ["MemoryLayout"] = DiscoveryValues.Convert(new Siemens.Engineering.Contract.EnumToClientRepresentation("MemoryLayout", "Optimized"))
            };
            var result = new BlockRead { Metadata = BlockMetadata.Map(attributes, "native-id", null, "FC") };
            var attempts = new List<string>();
            BlockSourceReader.Read(result, Request(), result.Metadata["programmingLanguage"] as string, false,
                format => { attempts.Add(format); return Source(format); }, () => { }, _ => "bridge");
            Check((string?)result.Metadata["programmingLanguage"] == entry.Language && (string?)result.Metadata["memoryLayout"] == "Optimized", "Bulk enum was lost.");
            Check(attempts.SequenceEqual(new[] { entry.First }) && result.Source?.Format == entry.First, "Best ignored wrapped native language.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
