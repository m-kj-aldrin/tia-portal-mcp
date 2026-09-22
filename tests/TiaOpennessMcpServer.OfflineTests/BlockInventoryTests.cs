using System.Text.Json;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

internal static class BlockInventoryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("blocks: strict CPU selector request", Requests);
        yield return ("blocks: hierarchy, composition order and lightweight leaves", Hierarchy);
        yield return ("blocks: interrupted composition retains siblings and later branches", Partial);
        yield return ("blocks: metadata failure retains block and exact error", MetadataFailure);
        yield return ("blocks: native F languages and safety-unit classification", Safety);
        yield return ("blocks: collection guard failure discards traversal", ContextLoss);
    }

    private static void Requests()
    {
        using var valid = JsonDocument.Parse("{\"processId\":20,\"plcObjectId\":\" cpu ID \"}");
        var request = DiscoveryRequest.Parse(valid.RootElement, false, true);
        Check(request.ProcessId == 20 && request.PlcObjectId == " cpu ID ", "Opaque selector changed.");
        foreach (var input in new[] { "{}", "{\"processId\":20}", "{\"processId\":20,\"plcObjectId\":3}",
            "{\"processId\":20,\"plcObjectId\":\" \"}", "{\"processId\":20,\"plcObjectId\":\"x\",\"objectId\":\"y\"}",
            "{\"processId\":20,\"plcObjectId\":\"x\",\"plcObjectId\":\"y\"}",
            "{\"processId\":20,\"plcObjectId\":\"x\",\"includeSource\":false}",
            "{\"processId\":20,\"plcObjectId\":\"x\",\"includePath\":false}" })
        {
            using var doc = JsonDocument.Parse(input);
            try { DiscoveryRequest.Parse(doc.RootElement, false, true); throw new Exception("Invalid request accepted."); }
            catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
        }
    }

    private static BlockInventoryNode Leaf(string name, string language = "SCL") => new()
    {
        Kind = "block", Name = () => name, ObjectId = () => " id:" + name + " ",
        BlockType = () => "FB", Number = () => 12, ProgrammingLanguage = () => language
    };

    private static BlockInventoryNode Group(string name, params BlockInventoryNode[] children)
    {
        var node = new BlockInventoryNode { Name = () => name };
        node.Compositions.Add(() => children);
        return node;
    }

    private static JsonElement Serialize(BlockInventoryNode source, out BlockInventory result, Action? validate = null)
    {
        result = new BlockInventory { ProcessId = 20, PlcObjectId = " cpu " };
        result.Roots.Add(new BlockInventoryReader(result, validate ?? (() => { })).Read(source));
        return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static void Hierarchy()
    {
        var scope = Group("PLC", Group("Program blocks", Leaf("Z"), Group("nested", Leaf("A"))));
        scope.Kind = "scope"; scope.ScopeType = "plcSoftware";
        var unit = Group("Unit Z", Group("Program blocks", Leaf("U")));
        unit.Kind = "scope"; unit.ScopeType = "softwareUnit";
        var safety = Group("Safety", Group("Program blocks", Leaf("S", "F_LAD")));
        safety.Kind = "scope"; safety.ScopeType = "safetyUnit"; safety.InSafetyUnit = true;
        scope.Compositions.Add(() => new[] { unit });
        scope.Compositions.Add(() => new[] { safety });
        var json = Serialize(scope, out var result);
        var root = json.GetProperty("roots")[0];
        var blocks = root.GetProperty("children")[0].GetProperty("children");
        Check(result.Complete && blocks[0].GetProperty("name").GetString() == "Z", "Order or completeness lost.");
        Check(blocks[1].GetProperty("children")[0].GetProperty("path").GetString() == "PLC/Program blocks/nested/A", "Hierarchy flattened.");
        Check(root.GetProperty("children")[2].GetProperty("scopeType").GetString() == "safetyUnit", "Unit scope lost.");
        Check(blocks[0].GetProperty("objectId").GetString() == " id:Z ", "Identifier changed.");
        Check(blocks[0].EnumerateObject().Count() == 9 && !blocks[0].TryGetProperty("source", out _), "Leaf includes detailed metadata.");
        Check(json.GetProperty("plcObjectId").GetString() == " cpu " && json.GetProperty("processId").GetInt32() == 20, "Scope missing.");
    }

    private static IEnumerable<BlockInventoryNode> Interrupted()
    {
        yield return Leaf("first");
        throw new InvalidOperationException("Native composition stopped");
    }

    private static void Partial()
    {
        var root = Group("PLC"); root.Compositions.Clear();
        root.Compositions.Add(Interrupted);
        root.Compositions.Add(() => throw new UnauthorizedAccessException("Native unit access denied"));
        root.Compositions.Add(() => new[] { Group("readable", Leaf("last")) });
        var json = Serialize(root, out var result);
        var children = json.GetProperty("roots")[0].GetProperty("children");
        Check(children.GetArrayLength() == 2 && children[0].GetProperty("name").GetString() == "first" &&
            children[1].GetProperty("name").GetString() == "readable", "Readable branches lost.");
        Check(!result.Complete && result.Errors.Count == 2 && result.Errors.All(e => e.Origin == "tia-openness" && e.Path == "PLC"), "Partial failure missing.");
    }

    private static void MetadataFailure()
    {
        var leaf = Leaf("protected");
        leaf.Number = () => throw new Exception("Native number unavailable");
        var json = Serialize(Group("PLC", leaf, Leaf("next")), out var result);
        var children = json.GetProperty("roots")[0].GetProperty("children");
        Check(children[0].GetProperty("number").ValueKind == JsonValueKind.Null && children.GetArrayLength() == 2, "Metadata failure discarded node.");
        Check(result.Errors.Single().Message == "Native number unavailable" && result.Errors[0].Path == "PLC/protected", "Native error changed.");
    }

    private static void Safety()
    {
        foreach (var language in new[] { "F_STL", "F_LAD", "F_FBD", "F_DB", "F_LAD_LIB", "F_FBD_LIB", "F_CALL" })
            Check(BlockInventoryReader.IsSafetyLanguage(language) == true, "Native F language omitted.");
        Check(BlockInventoryReader.IsSafetyLanguage("FBD") == false && BlockInventoryReader.IsSafetyLanguage(null) == null, "Safety guessed.");
        var leaf = Leaf("safety", "DB"); leaf.InSafetyUnit = true; leaf.IsSystem = true;
        var json = Serialize(leaf, out _).GetProperty("roots")[0];
        Check(json.GetProperty("isSafety").GetBoolean() && json.GetProperty("isSystem").GetBoolean(), "Native ownership lost.");
    }

    private static void ContextLoss()
    {
        var reached = false;
        var root = Group("PLC");
        root.Compositions.Add(() => { reached = true; return new[] { Leaf("never") }; });
        try { Serialize(root, out _, () => throw new ConnectionFault("reconnectRequired", 20, "Changed")); throw new Exception("Guard swallowed."); }
        catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
        Check(!reached, "Read continued after context loss.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
