using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

internal static class CrossReferenceTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("cross references: only process and opaque object selectors accepted", Requests);
        yield return ("cross references: native hierarchy, paths, order and full enum names preserved", Hierarchy);
        yield return ("cross references: partial fields and collections preserve independent branches", Partial);
        yield return ("cross references: empty and unavailable sources serialize distinctly", Empty);
        yield return ("cross references: context loss during traversal aborts remaining branches", ContextLoss);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static CrossReferenceRequest Request(string extra = "")
    {
        using var doc = JsonDocument.Parse("{\"processId\":20,\"objectId\":\" own tag ID \"" + extra + "}");
        return CrossReferenceRequest.Parse(doc.RootElement);
    }
    private static void Requests()
    {
        Check(Request().ObjectId == " own tag ID " && Request().ProcessId == 20, "Selector changed.");
        foreach (var extra in new[] { ",\"objectId\":\"duplicate\"", ",\"plcObjectId\":\"cpu\"", ",\"includePath\":false",
            ",\"filter\":\"AllObjects\"", ",\"includeSource\":false", ",\"includeEntries\":true" }) Invalid(() => Request(extra));
        foreach (var text in new[] { "null", "[]", "{}", "{\"processId\":20}", "{\"processId\":0,\"objectId\":\"x\"}",
            "{\"processId\":\"20\",\"objectId\":\"x\"}", "{\"processId\":20,\"objectId\":\" \"}" })
        {
            using var doc = JsonDocument.Parse(text); Invalid(() => CrossReferenceRequest.Parse(doc.RootElement));
        }
    }
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("Invalid request accepted."); }
        catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
    }
    private enum NativeReferenceType { TypeInstance }
    private enum NativeAccess { RW }
    private static CrossSourceNode Source(string name) => new()
    {
        Fields = { ["name"] = () => name, ["path"] = () => "Native\\" + name, ["typeName"] = () => "FB",
            ["device"] = () => "PLC", ["address"] = () => "FB2", ["objectId"] = () => " id:" + name + " " }
    };
    private static CrossReferenceNode Reference() => new()
    {
        Fields = { ["name"] = () => "Ref", ["path"] = () => "native-reference-path", ["objectId"] = () => null },
        Locations = () => new[] { new CrossObjectNode { Fields = { ["referenceType"] = () => NativeReferenceType.TypeInstance,
            ["access"] = () => NativeAccess.RW, ["referenceLocation"] = () => "Network 3", ["name"] = () => "Native name",
            ["typeName"] = () => "Native type", ["address"] = () => "%I5.0", ["referencedAsName"] = () => "Start Button",
            ["referencedAsObjectId"] = () => " tag-id " } } }
    };
    private static JsonElement Json(CrossReferenceRead result) => JsonSerializer.SerializeToElement(result,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    private static CrossReferenceRead Read(Func<IEnumerable<CrossSourceNode>> sources, Action? validate = null)
    {
        var result = new CrossReferenceRead(); new CrossReferenceReader(result, validate ?? (() => { })).Read(result, sources); return result;
    }
    private static void Hierarchy()
    {
        var source = Source("Z"); source.Children = () => new[] { Source("Child") }; source.References = () => new[] { Reference() };
        var result = Read(() => new[] { source, Source("A") }); var json = Json(result); var roots = json.GetProperty("sources");
        Check(result.Complete && roots[0].GetProperty("name").GetString() == "Z" && roots[1].GetProperty("name").GetString() == "A", "Order changed.");
        Check(roots[0].GetProperty("path").GetString() == "Native\\Z" && roots[0].GetProperty("objectId").GetString() == " id:Z ", "Native paths/IDs rewritten.");
        Check(roots[0].GetProperty("children")[0].GetProperty("name").GetString() == "Child", "Source hierarchy flattened.");
        var reference = roots[0].GetProperty("references")[0]; var location = reference.GetProperty("locations")[0];
        Check(reference.GetProperty("objectId").ValueKind == JsonValueKind.Null, "Missing native object replaced by fake ID.");
        Check(location.GetProperty("referenceType").GetString() == "TypeInstance" && location.GetProperty("access").GetString() == "RW", "Native enum simplified.");
        Check(location.GetProperty("referencedAsObjectId").GetString() == " tag-id " && location.GetProperty("referenceLocation").GetString() == "Network 3", "Location identity/detail lost.");
        Check(!roots[0].TryGetProperty("uses", out _) && !json.TryGetProperty("source", out _), "Derived/export schema leaked.");
    }
    private static IEnumerable<CrossSourceNode> InterruptedChildren()
    {
        yield return Source("retained");
        throw new Exception("Native enumeration interrupted");
    }
    private static void Partial()
    {
        var source = Source("partial"); source.Fields["objectId"] = () => throw new Exception("Native ID unavailable");
        source.Children = InterruptedChildren;
        var failed = Reference(); failed.Locations = () => throw new Exception("Locations denied");
        source.References = () => new[] { failed, Reference() };
        var result = Read(() => new[] { source, Source("later") }); var roots = Json(result).GetProperty("sources");
        Check(!result.Complete && result.Errors.Count == 3 && roots.GetArrayLength() == 2, "Failures discarded independent branches.");
        Check(roots[0].GetProperty("objectId").ValueKind == JsonValueKind.Null && roots[0].GetProperty("children").GetArrayLength() == 1, "Readable fields/children lost.");
        var references = roots[0].GetProperty("references");
        Check(references[0].GetProperty("locations").ValueKind == JsonValueKind.Null && references[1].GetProperty("locations").GetArrayLength() == 1, "Unavailable locations confused with empty or later reference lost.");
        Check(result.Errors[0].Message == "Native ID unavailable" && result.Errors.All(error => error.Origin == "tia-openness"), "Native failure text/origin changed.");
    }
    private static void Empty()
    {
        var empty = Read(() => Array.Empty<CrossSourceNode>());
        Check(empty.Complete && Json(empty).GetProperty("sources").GetArrayLength() == 0, "Empty query not successful.");
        var unavailable = Read(() => throw new Exception("Sources denied"));
        Check(!unavailable.Complete && Json(unavailable).GetProperty("sources").ValueKind == JsonValueKind.Null, "Unavailable query presented as empty.");
    }
    private static void ContextLoss()
    {
        var alive = true; var later = false; var source = Source("changed");
        source.Fields["name"] = () => { alive = false; throw new Exception("Native context changed"); };
        source.Children = () => { later = true; return Array.Empty<CrossSourceNode>(); };
        try
        {
            Read(() => new[] { source }, () => { if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Changed"); });
            throw new Exception("Context loss swallowed.");
        }
        catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
        Check(!later, "Traversal continued after invalidation.");
    }
}
