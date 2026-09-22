using System.Text.Json;
using TiaOpennessMcpServer.Prototype;

internal static class WriteProbeTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("write probe: arming refuses a missing confirmation or a different file name", Arming);
        yield return ("write probe: name substitution requires one quoted occurrence", Names);
        yield return ("write probe: replace and tag import stay inside session-created objects", Guards);
        yield return ("write probe: cleanup failure is visible and a write result is never saved", Cleanup);
    }

    private static WriteProbeRequest Request(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WriteProbeRequest.Parse(doc.RootElement);
    }

    private static ConnectionFault Fault(string code, Action action)
    {
        try { action(); }
        catch (ConnectionFault ex) { Check(ex.Code == code, "Expected " + code + ", got " + ex.Code); return ex; }
        throw new Exception("Expected failure: " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Arming()
    {
        Fault("notArmed", () => Request("{\"processId\":10,\"action\":\"arm\",\"projectFileName\":\"Disposable.ap20\",\"confirmDisposable\":false}"));
        Fault("invalidRequest", () => Request("{\"processId\":10,\"action\":\"arm\",\"projectFileName\":\"C:\\\\work\\\\Disposable.ap20\",\"confirmDisposable\":true}"));
        Fault("invalidRequest", () => Request("{\"processId\":10,\"action\":\"createCopy\",\"objectId\":\"block\"}"));
        var grouped = Request("{\"processId\":10,\"action\":\"createCopy\",\"objectId\":\"block\",\"newName\":\"Copy\",\"groupPath\":\"PLC_100/Program blocks/Probe\"}");
        Check(grouped.GroupPath == "PLC_100/Program blocks/Probe" && grouped.GroupObjectId == null, "Group path was not kept.");
        var session = new WriteProbeSession();
        var ticket = new RequestTicket(10, Guid.NewGuid());
        Fault("notArmed", () => session.Arm(ticket, @"C:\work\Disposable.ap20", "Other.ap20", true));
        session.Arm(ticket, @"C:\work\Disposable.ap20", "Disposable.ap20", true);
        session.Remember("block-1", "block", null);
        session.Disarm(10);
        var again = new RequestTicket(10, Guid.NewGuid());
        session.Arm(again, @"C:\work\Disposable.ap20", "Disposable.ap20", true);
        session.Guard(Request("{\"processId\":10,\"action\":\"replace\",\"objectId\":\"block-1\"}"));
        Fault("notArmed", () => session.Require(again, @"C:\work\Other.ap20"));
        Fault("notProbeObject", () => session.Guard(Request("{\"processId\":10,\"action\":\"replace\",\"objectId\":\"block-1\"}")));
        session.Disarm(10);
        Fault("notArmed", () => session.Require(again, @"C:\work\Disposable.ap20"));
    }

    private static void Names()
    {
        var renamed = WriteProbeNames.Substitute(new[] { "DATA_BLOCK \"GLOBAL\"\r\nBEGIN\r\nEND_DATA_BLOCK\r\n" }, "GLOBAL", "Probe_GLOBAL", 10);
        Check(renamed[0].Contains("\"Probe_GLOBAL\"", StringComparison.Ordinal) && !renamed[0].Contains("\"GLOBAL\"", StringComparison.Ordinal), "Single declaration was not renamed.");
        Fault("ambiguousName", () => WriteProbeNames.Substitute(new[] { "FUNCTION \"A\" : Void" }, "Missing", "Copy", 10));
        Fault("ambiguousName", () => WriteProbeNames.Substitute(new[] { "FUNCTION \"A\" : Void\r\n\"A\";" }, "A", "Copy", 10));
        Fault("ambiguousName", () => WriteProbeNames.Substitute(new[] { "FUNCTION \"A\" : Void", "TITLE \"A\"" }, "A", "Copy", 10));
        Fault("ambiguousName", () => WriteProbeNames.Substitute(new[] { "FUNCTION \"A\" : Void" }, "A", "Bad\"Name", 10));
    }

    private static void Guards()
    {
        var session = new WriteProbeSession();
        var ticket = new RequestTicket(4, Guid.NewGuid());
        session.Arm(ticket, @"D:\plc\Disposable.ap20", "Disposable.ap20", true);
        session.Remember("table-1", "tagTable", null);
        session.Remember("tag-1", "tag", "table-1");
        Fault("notProbeObject", () => session.Guard(Request("{\"processId\":4,\"action\":\"deleteTag\",\"objectId\":\"foreign\"}")));
        Fault("typedImportUnavailable", () => session.Guard(Request("{\"processId\":4,\"action\":\"importTagTable\",\"objectId\":\"table-1\"}")));
        session.NoteTypedFailure("table-1");
        session.Guard(Request("{\"processId\":4,\"action\":\"importTagTable\",\"objectId\":\"table-1\"}"));
        session.Guard(Request("{\"processId\":4,\"action\":\"addTag\",\"objectId\":\"table-1\",\"entryKind\":\"tag\",\"newName\":\"Start\",\"dataType\":\"Bool\",\"logicalAddress\":\"%M0.0\"}"));
        Fault("notProbeObject", () => session.Guard(Request("{\"processId\":4,\"action\":\"setTagAttribute\",\"objectId\":\"table-1\",\"attributeName\":\"Name\",\"attributeValue\":\"X\"}")));
        Check(session.ParentOf("tag-1") == "table-1", "Probe tag lost its table.");
        var result = new WriteProbeResult();
        Check(!result.Saved && !result.CleanupFailed, "A new probe result claims a save or cleanup failure.");
    }

    private static void Cleanup()
    {
        var folder = WriteProbeFiles.CreateDirectory();
        File.WriteAllText(Path.Combine(folder, "block.scl"), "FUNCTION \"A\" : Void");
        WriteProbeFiles.DeleteOwned(folder);
        Check(!Directory.Exists(folder), "Owned staging directory remained.");
        var refused = new WriteProbeResult();
        WriteProbeFiles.Finish(refused, Path.Combine(Path.GetTempPath(), "not-owned"));
        Check(refused.CleanupFailed && !refused.Complete && !refused.Saved &&
            refused.Errors.Any(error => error.Origin == "bridge" && error.Operation == "temporaryCleanup"),
            "Unexpected cleanup was not reported.");
    }
}
