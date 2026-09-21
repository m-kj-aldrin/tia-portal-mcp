using System.Text.Json;
using TiaOpennessMcpServer.Prototype;

internal static class UdtTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("UDTs: partial type hierarchy retains order, identities and scope flags without block fields", Inventory);
        yield return ("UDTs: metadata preserves native extras without imposing a block header", Metadata);
        yield return ("UDTs: best tries external source then SD then ML, explicit formats stay strict", Sources);
        yield return ("UDTs: metadata-only exports nothing and context loss prevents fallback", Guard);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static BlockReadRequest Request(string extra = "")
    {
        using var json = JsonDocument.Parse("{\"processId\":20,\"objectId\":\" UDT ID \"" + extra + "}");
        return BlockReadRequest.Parse(json.RootElement);
    }
    private static BlockInventoryNode Leaf(string name, bool safety = false, bool system = false) => new()
    {
        Kind = "udt", Name = () => name, ObjectId = () => " id:" + name + " ",
        InSafetyUnit = safety, IsSystem = system,
        ProgrammingLanguage = () => throw new Exception("UDT must not read block language"),
        Number = () => throw new Exception("UDT must not read block number")
    };
    private static IEnumerable<BlockInventoryNode> Interrupted()
    {
        yield return Leaf("Z");
        throw new Exception("Native type enumeration stopped");
    }
    private static void Inventory()
    {
        var root = new BlockInventoryNode { Kind = "scope", ScopeType = "plcSoftware", Name = () => "PLC" };
        var group = new BlockInventoryNode { Kind = "typeGroup", IsSystem = true, Name = () => "PLC data types" };
        group.Compositions.Add(Interrupted);
        group.Compositions.Add(() => new[] { Leaf("A", true, true) });
        root.Compositions.Add(() => new[] { group });
        var result = new BlockInventory { PlcObjectId = " cpu " };
        result.Roots.Add(new BlockInventoryReader(result, () => { }).Read(root));
        var json = JsonSerializer.SerializeToElement(result.Roots[0]);
        var leaves = json.GetProperty("children")[0].GetProperty("children");
        Check(leaves.GetArrayLength() == 2 && leaves[0].GetProperty("name").GetString() == "Z", "Partial branch/order lost.");
        Check(leaves[0].GetProperty("path").GetString() == "PLC/PLC data types/Z" && leaves[0].GetProperty("objectId").GetString() == " id:Z ", "Native identity/path changed.");
        Check(leaves[0].EnumerateObject().Count() == 6 && !leaves[0].GetProperty("isSystem").GetBoolean(), "Block fields leaked or system root classified user UDT as system.");
        Check(leaves[1].GetProperty("isSafety").GetBoolean() && leaves[1].GetProperty("isSystem").GetBoolean(), "Scope flags lost.");
        Check(!result.Complete && result.Errors.Single().Message == "Native type enumeration stopped", "Partial error lost.");
    }
    private static void Metadata()
    {
        var metadata = UdtMetadata.Map(new() { ["Name"] = "T_Item", ["Namespace"] = "", ["IsKnowHowProtected"] = true,
            ["HeaderName"] = "Native extra", ["InterfaceModifiedDate"] = "timestamp" }, " opaque ", null);
        var json = JsonSerializer.SerializeToElement(metadata);
        Check(!json.TryGetProperty("header", out _) && !json.TryGetProperty("blockType", out _), "Block schema imposed on UDT.");
        Check(json.GetProperty("typeSpecific").GetProperty("HeaderName").GetString() == "Native extra" && json.GetProperty("typeSpecific").EnumerateObject().Count() == 1, "Native extras lost or duplicated.");
        Check(json.GetProperty("path").ValueKind == JsonValueKind.Null && json.GetProperty("state").GetProperty("isConsistent").ValueKind == JsonValueKind.Null, "Unavailable values inferred.");
        Check(json.GetProperty("timestamps").GetProperty("interfaceModified").GetString() == "timestamp", "Timestamp lost.");
    }
    private static BlockSource Source(string format) => new() { Format = format, Documents = new() { new SourceDocument("source.udt", "TYPE T_Item\r\nEND_TYPE\r\n") } };
    private static void Read(BlockRead result, BlockReadRequest request, Func<string, BlockSource> export, Action? validate = null) =>
        BlockSourceReader.Read(result, request, null, false, export, validate ?? (() => { }), _ => "tia-openness", udt: true);
    private static void Sources()
    {
        var formats = new[] { "external-source", "simatic-sd", "simatic-ml" };
        for (var successAt = 0; successAt < formats.Length; successAt++)
        {
            var attempts = new List<string>(); var result = new BlockRead();
            Read(result, Request(), format => { attempts.Add(format); return format == formats[successAt] ? Source(format) : throw new Exception("Exact native refusal"); });
            Check(attempts.SequenceEqual(formats.Take(successAt + 1)) && result.Source?.Format == formats[successAt] && result.Complete, "UDT fallback order or success errors incorrect.");
        }
        foreach (var format in formats)
        {
            var result = new BlockRead { Metadata = new() { ["name"] = "protected" } }; var calls = 0;
            Read(result, Request(",\"sourceFormat\":\"" + format + "\""), _ => { calls++; throw new Exception("Native protection refusal"); });
            Check(calls == 1 && result.Source == null && result.Metadata.ContainsKey("name") && result.Errors.Single().Message == "Native protection refusal", "Strict failure lost metadata or fell back.");
        }
        var failed = new BlockRead();
        Read(failed, Request(), _ => throw new Exception("Export denied"));
        Check(failed.Source == null && failed.Errors.Count == 3 && !failed.Complete, "All failed attempts not returned.");
    }
    private static void Guard()
    {
        var result = new BlockRead();
        Read(result, Request(",\"includeSource\":false,\"includePath\":false"), _ => throw new Exception("Export reached"));
        Check(result.Source == null && result.Attempts.Count == 0, "Metadata-only exported.");
        var alive = true; var calls = 0;
        try
        {
            Read(result, Request(), format => { calls++; alive = false; return Source(format); },
                () => { if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Changed"); });
            throw new Exception("Context loss swallowed.");
        }
        catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
        Check(calls == 1 && result.Source == null, "Context loss returned payload or retried.");
    }
}
