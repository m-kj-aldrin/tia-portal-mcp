using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Operations;

internal static class TagTableExportTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("tag table export: strict native selector rejects paths and format options", Requests);
        yield return ("tag table export: exact XML and returned-content checksum survive owned staging", ExactContent);
        yield return ("tag table export: failures preserve origin and clean partial native files", ExportFailure);
        yield return ("tag table export: missing or empty native files never claim success", InvalidContent);
        yield return ("tag table export: context loss discards source and cleans files", ContextLoss);
        yield return ("tag table export: cleanup failure is explicit without traversing unexpected directories", CleanupFailure);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static ExportTagTableRequest Request(string text)
    { using var json = JsonDocument.Parse(text); return ExportTagTableRequest.Parse(json.RootElement); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    private static void Requests()
    {
        var request = Request("{\"processId\":20,\"objectId\":\" native/table == \"}");
        Check(request.ProcessId == 20 && request.ObjectId == " native/table == ", "Native identity changed.");
        foreach (var text in new[] { "null", "[]", "{}", "{\"processId\":0,\"objectId\":\"table\"}",
            "{\"processId\":\"20\",\"objectId\":\"table\"}", "{\"processId\":20,\"objectId\":\" \"}",
            "{\"processId\":20,\"processId\":21,\"objectId\":\"table\"}",
            "{\"processId\":20,\"objectId\":\"table\",\"objectId\":\"other\"}",
            "{\"processId\":20,\"objectId\":\"table\",\"path\":\"C:/output.xml\"}",
            "{\"processId\":20,\"objectId\":\"table\",\"sourceFormat\":\"simatic-ml\"}",
            "{\"processId\":20,\"objectId\":\"table\",\"includeEntries\":false}" })
        {
            try { Request(text); throw new Exception("Invalid export request accepted."); }
            catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
        }
    }

    private static void ExactContent()
    {
        const string content = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Document>\r\n  <Name>Åäö</Name>\r\n</Document>\r\n";
        var result = new TagTableExportResult { ObjectId = " table " };
        string? folder = null;
        var calls = 0;
        TagTableExportReader.Read(result, file =>
        {
            calls++;
            folder = file.DirectoryName;
            Check(!file.Exists, "Native export destination already existed.");
            File.WriteAllText(file.FullName, content, new UTF8Encoding(true, true));
        }, () => { }, _ => "bridge");
        var json = Json(result);
        var source = json.GetProperty("source");
        var document = source.GetProperty("documents")[0];
        var expected = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(content))).ToLowerInvariant();
        Check(result.Complete && calls == 1 && folder != null && !Directory.Exists(folder), "Export did not run exactly once or clean its folder.");
        Check(json.GetProperty("objectId").GetString() == " table " && source.GetProperty("format").GetString() == "simatic-ml", "Identity or native format changed.");
        Check(document.GetProperty("name").GetString() == "tag-table.xml" && document.GetProperty("content").GetString() == content, "Export text was normalized or rebuilt.");
        Check(document.GetProperty("checksum").GetProperty("value").GetString() == expected &&
            document.GetProperty("checksum").GetProperty("scope").GetString() == "returned-content", "Checksum no longer describes returned content.");
    }

    private static void ExportFailure()
    {
        var result = new TagTableExportResult();
        string? folder = null;
        TagTableExportReader.Read(result, file =>
        {
            folder = file.DirectoryName;
            File.WriteAllText(file.FullName, "partial native output");
            throw new InvalidOperationException("Native export denied");
        }, () => { }, _ => "tia-openness");
        Check(!result.Complete && result.Source == null && result.Errors.Single().Message == "Native export denied" &&
            result.Errors.Single().Origin == "tia-openness", "Failure invented source or lost native error text/origin.");
        Check(folder != null && !Directory.Exists(folder) && Json(result).GetProperty("source").ValueKind == JsonValueKind.Null,
            "Failed export leaked its partial file or omitted explicit source:null.");
    }

    private static void InvalidContent()
    {
        foreach (var content in new string?[] { null, " \r\n" })
        {
            var result = new TagTableExportResult();
            string? folder = null;
            TagTableExportReader.Read(result, file =>
            {
                folder = file.DirectoryName;
                if (content != null) File.WriteAllText(file.FullName, content);
            }, () => { }, _ => "bridge");
            Check(!result.Complete && result.Source == null && result.Errors.Single().Origin == "bridge", "Unusable native output claimed success.");
            Check(folder != null && !Directory.Exists(folder), "Unusable export left temporary files.");
        }
    }

    private static void ContextLoss()
    {
        foreach (var nativeFails in new[] { false, true })
        {
            var alive = true;
            string? folder = null;
            var result = new TagTableExportResult();
            try
            {
                TagTableExportReader.Read(result, file =>
                {
                    folder = file.DirectoryName;
                    File.WriteAllText(file.FullName, "<Document/>");
                    alive = false;
                    if (nativeFails) throw new IOException("Native context lost");
                }, () => { if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Changed"); }, _ => "bridge");
                throw new Exception("Context loss was swallowed.");
            }
            catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
            Check(result.Source == null && folder != null && !Directory.Exists(folder), "Invalid context returned source or leaked files.");
        }
    }

    private static void CleanupFailure()
    {
        string? folder = null;
        string? child = null;
        string? output = null;
        try
        {
            var result = new TagTableExportResult();
            TagTableExportReader.Read(result, file =>
            {
                folder = file.DirectoryName!;
                output = file.FullName;
                File.WriteAllText(output, "<Document/>");
                child = Path.Combine(folder, "unexpected");
                Directory.CreateDirectory(child);
            }, () => { }, _ => "bridge");
            Check(!result.Complete && result.Source != null && result.Errors.Single().Operation == "temporaryCleanup" &&
                result.Errors.Single().Origin == "bridge", "Cleanup failure claimed complete success or discarded valid source.");
            Check(child != null && Directory.Exists(child), "Cleanup traversed an unexpected child directory.");
        }
        finally
        {
            if (child != null) Directory.Delete(child);
            if (output != null) File.Delete(output);
            if (folder != null) Directory.Delete(folder);
        }
    }
}
