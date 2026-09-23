using System.Text;
using System.Text.Json;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

internal static class WriteTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("writes: typed attribute values and opaque IDs survive parsing", Values);
        yield return ("writes: whole-object deletion requires only process and opaque object identity", DeletionRequests);
        yield return ("writes: source document shape, paths and formats are validated", Documents);
        yield return ("writes: exact UTF-8 source staging and owned cleanup", Staging);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static WriteRequest Request(string tool, string json)
    { using var doc = JsonDocument.Parse(json); return WriteRequest.Parse(tool, doc.RootElement); }
    private static void Invalid(string tool, string json)
    {
        try { Request(tool,json); }
        catch (ConnectionFault ex) { Check(ex.Code == "invalidRequest", "Wrong failure."); return; }
        throw new Exception("Accepted invalid write: " + json);
    }
    private static void Values()
    {
        foreach (var pair in new (string Json, object Expected)[] { ("\" value \"", " value "), ("false", false), ("true", true), ("0", 0), ("-10", -10), ("2147483648", 2147483648L), ("1.25", 1.25) })
        {
            var request = Request("set_tag_entry_attribute", "{\"processId\":5,\"objectId\":\" opaque /== \",\"attributeName\":\"Name\",\"attributeValue\":" + pair.Json + "}");
            Check(request.ObjectId == " opaque /== " && Equals(request.AttributeValue,pair.Expected), "Value/ID was coerced or trimmed.");
        }
        foreach (var invalid in new[] { "null", "{}", "[]", "1e400" })
            Invalid("set_tag_entry_attribute", "{\"processId\":5,\"objectId\":\"tag\",\"attributeName\":\"Name\",\"attributeValue\":" + invalid + "}");
        Invalid("delete_tag_entry", "{\"processId\":\"5\",\"objectId\":\"tag\"}");
        Invalid("delete_tag_entry", "{\"processId\":5,\"processId\":6,\"objectId\":\"tag\"}");
    }
    private static string Body(string format, params (string Name, string Content)[] documents) => JsonSerializer.Serialize(new
    { processId = 5, plcObjectId = "cpu", sourceFormat = format, documents = documents.Select(d => new { name = d.Name, content = d.Content }) });
    private static void DeletionRequests()
    {
        foreach (var tool in new[] { "delete_block", "delete_udt", "delete_tag_table" })
        {
            var request = Request(tool, "{\"processId\":5,\"objectId\":\" opaque /== \"}");
            Check(request.Tool == tool && request.ProcessId == 5 && request.ObjectId == " opaque /== ", "Deletion identity was changed.");
            foreach (var invalid in new[]
            {
                "{}", "null", "[]",
                "{\"processId\":5}",
                "{\"processId\":0,\"objectId\":\"id\"}",
                "{\"processId\":5.1,\"objectId\":\"id\"}",
                "{\"processId\":\"5\",\"objectId\":\"id\"}",
                "{\"processId\":5,\"objectId\":null}",
                "{\"processId\":5,\"objectId\":5}",
                "{\"processId\":5,\"objectId\":\" \"}",
                "{\"processId\":5,\"objectId\":\"id\",\"objectId\":\"other\"}",
                "{\"processId\":5,\"processId\":6,\"objectId\":\"id\"}",
                "{\"processId\":5,\"objectId\":\"id\",\"force\":true}",
                "{\"processId\":5,\"objectId\":\"id\",\"cascade\":true}",
                "{\"processId\":5,\"objectId\":\"id\",\"plcObjectId\":\"cpu\"}",
                "{\"processId\":5,\"objectId\":\"id\",\"name\":\"name\"}"
            }) Invalid(tool, invalid);
        }
    }
    private static void Documents()
    {
        Request("write_blocks", Body("external-source", ("A.scl","  code\r\n")));
        Request("write_udts", Body("simatic-sd", ("A.s7dcl","decl"),("A.s7res","resources")));
        Request("write_blocks", Body("simatic-ml", ("A.xml","<Document/>")));
        foreach (var name in new[] { "../A.scl", "a/b.scl", "a\\b.scl", "C:A.scl", "NUL.scl", "A.scl ", "A.scl.", "a:s.scl" })
            Invalid("write_blocks",Body("external-source",(name,"code")));
        Invalid("write_blocks",Body("best",("A.scl","code")));
        Invalid("write_blocks",Body("external-source",("A.xml","code")));
        Invalid("write_blocks",Body("simatic-ml",("A.xml","code"),("a.XML","code")));
        Invalid("write_udts",Body("simatic-sd",("A.s7dcl","decl"),("B.s7res","resources")));
        Invalid("write_blocks",Body("external-source",("A.scl","  ")));
        Invalid("write_blocks",Body("external-source",("A.scl","code")).TrimEnd('}') + ",\"groupObjectId\":\"id\",\"groupPath\":\"path\"}");
    }
    private static void Staging()
    {
        var content = "  FUNCTION \"Å\" : Void\r\n  // exact text\r\nEND_FUNCTION\r\n";
        var request = Request("write_blocks", Body("external-source",("A.scl",content)));
        var result = new WriteResult();
        var folder = WriteFiles.CreateDirectory();
        try
        {
            var files = WriteFiles.Stage(folder,request.Documents);
            Check(File.ReadAllBytes(files[0].FullName).SequenceEqual(new UTF8Encoding(false,true).GetBytes(content)), "Staging changed content or added a BOM.");
            try { WriteFiles.Stage(folder,request.Documents); throw new Exception("Overwrote existing staging file."); }
            catch (IOException) { }
        }
        finally { WriteFiles.Finish(result,folder); }
        Check(!Directory.Exists(folder) && result.Complete && !result.Saved, "Staging leaked or claims a save.");
        WriteFiles.Finish(result,Path.Combine(Path.GetTempPath(),"not-owned"));
        Check(result.CleanupFailed && !result.Complete && !result.Saved, "Cleanup failure claimed complete success.");
    }
}
