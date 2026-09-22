using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

internal static class TagTableTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("tag tables: strict selectors and flags reject source options", Requests);
        yield return ("tag tables: inventory has table leaves without entries or block fields", Inventory);
        yield return ("tag tables: bulk metadata mapping preserves native timestamp and unknown fields", Metadata);
        yield return ("tag tables: entries retain order, own IDs and separate tag/constant fields", Entries);
        yield return ("tag tables: metadata-only skips all entry access and serializes explicit null", MetadataOnly);
        yield return ("tag tables: empty, unavailable and interrupted collections remain distinct", Partial);
        yield return ("tag tables: failed ID or attribute leaves readable entry fields intact", FieldFailures);
        yield return ("tag tables: context loss during a failed read aborts later collections", ContextLoss);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static TagTableReadRequest Request(string extra = "")
    {
        using var json = JsonDocument.Parse("{\"processId\":20,\"objectId\":\" table ID \"" + extra + "}");
        return TagTableReadRequest.Parse(json.RootElement);
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
    private static void Requests()
    {
        var request = Request();
        Check(request.ProcessId == 20 && request.ObjectId == " table ID " && request.IncludeEntries && request.IncludePath, "Defaults or opaque identity changed.");
        Check(!Request(",\"includeEntries\":false,\"includePath\":false").IncludeEntries, "Metadata-only rejected.");
        foreach (var extra in new[] { ",\"includeEntries\":null", ",\"includePath\":1", ",\"includeSource\":false",
            ",\"sourceFormat\":\"best\"", ",\"includeDependencies\":false", ",\"objectId\":\"other\"", ",\"plcObjectId\":\"cpu\"" })
            Invalid(() => Request(extra));
        foreach (var text in new[] { "null", "{}", "[]", "{\"processId\":20}", "{\"processId\":0,\"objectId\":\"x\"}",
            "{\"processId\":\"20\",\"objectId\":\"x\"}", "{\"processId\":20,\"objectId\":\" \"}" })
        {
            using var doc = JsonDocument.Parse(text);
            Invalid(() => TagTableReadRequest.Parse(doc.RootElement));
        }
    }
    private static void Invalid(Action read)
    {
        try { read(); throw new Exception("Invalid request accepted."); }
        catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
    }
    private static void Inventory()
    {
        var root = new BlockInventoryNode { Kind = "tagTableGroup", Name = () => "PLC tags", IsSystem = true };
        root.Compositions.Add(() => new[] { new BlockInventoryNode { Kind = "tagTable", Name = () => "Default tag table",
            ObjectId = () => " table ", ProgrammingLanguage = () => throw new Exception("Block field read") } });
        var result = new BlockInventory();
        var json = Json(new BlockInventoryReader(result, () => { }).Read(root));
        var table = json.GetProperty("children")[0];
        Check(table.EnumerateObject().Count() == 6 && table.GetProperty("kind").GetString() == "tagTable", "Inventory leaked detail fields.");
        Check(!table.GetProperty("isSystem").GetBoolean() && table.GetProperty("path").GetString() == "PLC tags/Default tag table", "Root ownership classified default table as system or path lost.");
    }
    private static void Metadata()
    {
        var json = Json(TagTableReader.Metadata(new() { ["Name"] = "Table", ["IsDefault"] = false,
            ["ModifiedTimeStamp"] = "native-timestamp", ["Extra"] = 17 }, " table ", null));
        Check(json.GetProperty("timestamps").GetProperty("modified").GetString() == "native-timestamp", "Native ModifiedTimeStamp lost.");
        Check(json.GetProperty("typeSpecific").EnumerateObject().Count() == 1 && json.GetProperty("typeSpecific").GetProperty("Extra").GetInt32() == 17, "Extra attributes lost/duplicated.");
        Check(!json.GetProperty("isDefault").GetBoolean() && json.GetProperty("path").ValueKind == JsonValueKind.Null, "Native false/null changed.");
    }
    private static TagTableEntryNode Entry(string name, bool tag = true) => new()
    {
        Identifier = () => " entry:" + name + " ",
        Attributes = () => new Dictionary<string, object?> { ["Name"] = name, ["DataTypeName"] = "Bool",
            [tag ? "LogicalAddress" : "Value"] = tag ? "%I0.0" : "FALSE", ["ExternalAccessible"] = true, ["Comment"] = new object() }
    };
    private static void Entries()
    {
        var result = new TagTableRead();
        TagTableReader.ReadEntries(result, true, () => { }, () => new[] { Entry("Z"), Entry("A") },
            () => new[] { Entry("User", false) }, () => new[] { Entry("System", false) });
        var json = Json(result); var entries = json.GetProperty("entries");
        var tag = entries.GetProperty("tags")[0];
        Check(result.Complete && tag.GetProperty("name").GetString() == "Z" && entries.GetProperty("tags")[1].GetProperty("name").GetString() == "A", "Native order changed.");
        Check(tag.GetProperty("objectId").GetString() == " entry:Z " && tag.GetProperty("logicalAddress").GetString() == "%I0.0", "Entry identity/address lost.");
        Check(!tag.TryGetProperty("value", out _) && tag.GetProperty("dataType").GetString() == "Bool", "Wrong tag shape.");
        Check(tag.GetProperty("typeSpecific").EnumerateObject().Count() == 1 && !tag.GetProperty("typeSpecific").TryGetProperty("Comment", out _), "Mapped/multilingual fields leaked.");
        foreach (var kind in new[] { "userConstants", "systemConstants" })
        {
            var constant = entries.GetProperty(kind)[0];
            Check(constant.GetProperty("value").GetString() == "FALSE" && !constant.TryGetProperty("logicalAddress", out _), "Constant native value lost or fabricated address.");
        }
        Check(!json.TryGetProperty("source", out _) && !json.TryGetProperty("checksum", out _), "Export contract leaked.");
    }
    private static void MetadataOnly()
    {
        var result = new TagTableRead();
        IEnumerable<TagTableEntryNode> Forbidden() => throw new Exception("Entries accessed");
        TagTableReader.ReadEntries(result, false, () => throw new Exception("Entry guard reached"), Forbidden, Forbidden, Forbidden);
        Check(result.Complete && Json(result).GetProperty("entries").ValueKind == JsonValueKind.Null, "Metadata-only not explicit null/complete.");
    }
    private static IEnumerable<TagTableEntryNode> Interrupted()
    {
        yield return Entry("retained");
        throw new Exception("Native enumeration stopped");
    }
    private static void Partial()
    {
        var result = new TagTableRead();
        TagTableReader.ReadEntries(result, true, () => { }, Interrupted,
            () => throw new Exception("User constants inaccessible"), () => Array.Empty<TagTableEntryNode>());
        var entries = Json(result).GetProperty("entries");
        Check(!result.Complete && result.Errors.Count == 2 && entries.GetProperty("tags").GetArrayLength() == 1, "Interrupted collection lost prior entries.");
        Check(entries.GetProperty("userConstants").ValueKind == JsonValueKind.Null && entries.GetProperty("systemConstants").GetArrayLength() == 0, "Unavailable confused with empty.");
        Check(result.Errors[0].Message == "Native enumeration stopped" && result.Errors[1].Path == "userConstants", "Error text/collection location lost.");
    }
    private static IEnumerable<KeyValuePair<string, object?>> InterruptedAttributes()
    {
        yield return new("Name", "partial");
        throw new Exception("Native attribute read stopped");
    }
    private static void FieldFailures()
    {
        var noId = Entry("readable"); noId.Identifier = () => throw new Exception("Identifier unsupported");
        var partial = Entry("partial"); partial.Attributes = InterruptedAttributes;
        var result = new TagTableRead();
        TagTableReader.ReadEntries(result, true, () => { }, () => new[] { noId, partial, Entry("later") },
            () => Array.Empty<TagTableEntryNode>(), () => Array.Empty<TagTableEntryNode>());
        var tags = Json(result).GetProperty("entries").GetProperty("tags");
        Check(tags.GetArrayLength() == 3 && tags[0].GetProperty("objectId").ValueKind == JsonValueKind.Null && tags[0].GetProperty("logicalAddress").GetString() == "%I0.0", "Failed ID discarded readable tag.");
        Check(tags[1].GetProperty("name").GetString() == "partial" && tags[1].GetProperty("dataType").ValueKind == JsonValueKind.Null, "Partial attributes lost or inferred.");
        Check(result.Errors.Count == 2 && result.Errors.All(e => e.Origin == "tia-openness"), "Native failures omitted.");
    }
    private static void ContextLoss()
    {
        var alive = true; var later = false; var entry = Entry("changed");
        entry.Identifier = () => { alive = false; throw new Exception("Native context gone"); };
        var result = new TagTableRead();
        try
        {
            TagTableReader.ReadEntries(result, true, () => { if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Changed"); },
                () => new[] { entry }, () => { later = true; return Array.Empty<TagTableEntryNode>(); }, () => Array.Empty<TagTableEntryNode>());
            throw new Exception("Context loss swallowed.");
        }
        catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
        Check(!later && result.Entries == null, "Read continued after invalidation.");
    }
}
