using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Operations;

internal static class CompileTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("compile: accepts only explicit process and opaque CPU identity", Requests);
        yield return ("compile: preserves recursive native diagnostics and UTC timestamps", Hierarchy);
        yield return ("compile: compiler errors and warnings are separate from API completion", Outcomes);
        yield return ("compile: partial diagnostics retain readable siblings without a success claim", Partial);
        yield return ("compile: unavailable messages are distinct from empty messages", Unavailable);
        yield return ("compile: context loss after a successful field aborts further native access", ContextLoss);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static CompileRequest Request(string text)
    {
        using var document = JsonDocument.Parse(text);
        return CompileRequest.Parse(document.RootElement);
    }

    private static void Requests()
    {
        var request = Request("{\"processId\":20,\"plcObjectId\":\" cpu ID \"}");
        Check(request.ProcessId == 20 && request.PlcObjectId == " cpu ID ", "Native selector changed.");
        foreach (var text in new[] { "null", "[]", "{}", "{\"processId\":20}",
            "{\"processId\":0,\"plcObjectId\":\"cpu\"}", "{\"processId\":20.5,\"plcObjectId\":\"cpu\"}",
            "{\"processId\":\"20\",\"plcObjectId\":\"cpu\"}", "{\"processId\":20,\"plcObjectId\":\" \"}",
            "{\"processId\":20,\"plcObjectId\":null}", "{\"processId\":20,\"plcObjectId\":\"cpu\",\"plcObjectId\":\"duplicate\"}",
            "{\"processId\":20,\"processId\":21,\"plcObjectId\":\"cpu\"}",
            "{\"processId\":20,\"plcObjectId\":\"cpu\",\"rebuildAll\":true}",
            "{\"processId\":20,\"plcObjectId\":\"cpu\",\"save\":true}",
            "{\"processId\":20,\"plcObjectId\":\"cpu\",\"download\":true}" })
        {
            try { Request(text); throw new Exception("Invalid compile request accepted."); }
            catch (ConnectionFault fault) when (fault.Code == "invalidRequest") { }
        }
    }

    private static CompileResultNode Root(string state = "Success", int errors = 0, int warnings = 0) => new()
    {
        State = () => state, ErrorCount = () => errors, WarningCount = () => warnings
    };
    private static CompileMessageNode Message(string path, string description, string state = "Information", int errors = 0, int warnings = 0) => new()
    {
        Path = () => path, Description = () => description, State = () => state,
        DateTime = () => new DateTime(2026, 9, 23, 5, 6, 7, DateTimeKind.Unspecified),
        ErrorCount = () => errors, WarningCount = () => warnings
    };
    private static CompileResult Read(CompileResultNode node, Action? validate = null)
    {
        var result = new CompileResult { ProcessId = 20, PlcObjectId = " cpu ID " };
        new CompileResultReader(result, validate ?? (() => { })).Read(result, node);
        return result;
    }
    private static JsonElement Json(CompileResult result) => JsonSerializer.SerializeToElement(result,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    private static void Hierarchy()
    {
        var native = Root("Error", 1, 1);
        var parent = Message("PLC/Program blocks", "Program blocks", "Error", 1, 0);
        parent.Messages = () => new[] { Message("PLC/Program blocks/FC1", "Tag #Missing not defined.\r\nLine 2", "Error", 1, 0) };
        native.Messages = () => new[] { parent, Message("PLC/Warnings", "Native warning", "Warning", 0, 1) };
        var result = Read(native); var json = Json(result); var messages = json.GetProperty("messages");
        Check(result.Complete && result.CompilationSucceeded == false && result.Errors.Count == 0, "Compiler error confused with API fault.");
        Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("path").GetString() == "PLC/Program blocks", "Native hierarchy/order changed.");
        Check(messages[0].GetProperty("messages")[0].GetProperty("description").GetString() == "Tag #Missing not defined.\r\nLine 2", "Native error text lost.");
        Check(messages[0].GetProperty("dateTime").GetString() == "2026-09-23T05:06:07.0000000Z", "Documented UTC shifted by local host timezone.");
        Check(messages[1].GetProperty("state").GetString() == "Warning" && messages[1].GetProperty("warningCount").GetInt32() == 1, "Native state/counts changed.");
        Check(json.GetProperty("saved").GetBoolean() == false && json.GetProperty("projectModified").ValueKind == JsonValueKind.Null, "Unobserved save/project state invented.");
    }

    private static void Outcomes()
    {
        foreach (var state in new[] { "Success", "Information", "Warning" })
        {
            var result = Read(Root(state, 0, state == "Warning" ? 3 : 0));
            Check(result.Complete && result.CompilationSucceeded == true, "Successful native state incorrectly failed: " + state);
        }
        Check(Read(Root("Error", 0)).CompilationSucceeded == false, "Native Error state ignored.");
        Check(Read(Root("Success", 1)).CompilationSucceeded == false, "Positive native error count ignored.");
        Check(Read(Root("FutureState")).CompilationSucceeded == null, "Unknown native state claimed success.");
        Check(Read(Root("Success", -1)).CompilationSucceeded == null, "Invalid native count claimed success.");
        var missingCount = Root(); missingCount.WarningCount = () => null;
        Check(Read(missingCount).CompilationSucceeded == null, "Unavailable native count claimed success.");
    }

    private static IEnumerable<CompileMessageNode> InterruptedMessages()
    {
        yield return Message("retained", "Readable child");
        throw new Exception("Native diagnostic enumeration interrupted");
    }
    private static void Partial()
    {
        var native = Root(); var broken = Message("broken", "Hidden");
        broken.Description = () => throw new Exception("Native description unavailable");
        broken.Messages = InterruptedMessages;
        native.Messages = () => new[] { broken, Message("later", "Still readable") };
        var result = Read(native); var json = Json(result); var messages = json.GetProperty("messages");
        Check(!result.Complete && result.CompilationSucceeded == null && result.Errors.Count == 2, "Partial read claimed a complete successful compile.");
        Check(messages.GetArrayLength() == 2 && messages[1].GetProperty("description").GetString() == "Still readable", "Independent diagnostics were discarded.");
        Check(messages[0].GetProperty("description").ValueKind == JsonValueKind.Null && messages[0].GetProperty("messages").GetArrayLength() == 1, "Readable partial diagnostics lost.");
        Check(result.Errors[0].Message == "Native description unavailable" && result.Errors.All(error => error.Origin == "tia-openness"), "Native API error text/origin changed.");
    }

    private static void Unavailable()
    {
        Check(Json(Read(Root())).GetProperty("messages").GetArrayLength() == 0, "An empty successful result was not preserved.");
        var native = Root(); native.Messages = () => throw new Exception("Messages unavailable");
        var result = Read(native);
        Check(!result.Complete && result.CompilationSucceeded == null && Json(result).GetProperty("messages").ValueKind == JsonValueKind.Null, "Unavailable messages misrepresented as empty.");
    }

    private static void ContextLoss()
    {
        var native = Root(); var alive = true; var reachedNextField = false;
        native.State = () => { alive = false; return "Success"; };
        native.ErrorCount = () => { reachedNextField = true; return 0; };
        try
        {
            Read(native, () => { if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Project changed"); });
            throw new Exception("Context loss swallowed.");
        }
        catch (ConnectionFault fault) when (fault.Code == "reconnectRequired") { }
        Check(!reachedNextField, "Native read continued after invalidation.");
    }
}
