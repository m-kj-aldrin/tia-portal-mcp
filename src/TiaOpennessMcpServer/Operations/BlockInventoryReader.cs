namespace TiaOpennessMcpServer.Operations;

// Lazy native adapters stay inside the guarded STA operation. Only the walker's dictionaries escape.
// Shared block/UDT/tag-table hierarchy walker. Non-block leaves never read block fields.
// Independent compositions let a failed block enumeration leave child groups/units readable.
internal sealed class BlockInventoryNode
{
    public string Kind { get; set; } = "blockGroup";
    public string? ScopeType { get; set; }
    public Func<string?> Name { get; set; } = () => null;
    public Func<string?> ObjectId { get; set; } = () => null;
    public Func<string?> BlockType { get; set; } = () => null;
    public Func<int?> Number { get; set; } = () => null;
    public Func<string?> ProgrammingLanguage { get; set; } = () => null;
    public bool IsSystem { get; set; }
    public bool InSafetyUnit { get; set; }
    public List<Func<IEnumerable<BlockInventoryNode>>> Compositions { get; } = new();
}

internal sealed class BlockInventory : DiscoveryResult
{
    public string PlcObjectId { get; set; } = "";
    public List<Dictionary<string, object?>> Roots { get; } = new();
}

internal sealed class BlockInventoryReader
{
    private readonly DiscoveryReadContext _read;

    public BlockInventoryReader(BlockInventory result, Action validate) =>
        _read = new DiscoveryReadContext(result.Errors, validate);

    public Dictionary<string, object?> Read(BlockInventoryNode source, string? parent = "")
    {
        var name = _read.Read(source.Name, "name", parent);
        var path = parent == null || name == null ? null : parent.Length == 0 ? name : parent + "/" + name;
        var node = new Dictionary<string, object?>
        {
            ["kind"] = source.Kind, ["name"] = name, ["path"] = path,
            ["isSystem"] = source.IsSystem, ["isSafety"] = source.InSafetyUnit
        };
        if (source.Kind == "scope") node["scopeType"] = source.ScopeType;
        if (source.Kind == "udt" || source.Kind == "tagTable")
        {
            node["objectId"] = _read.Read(source.ObjectId, "identifier", path);
            return node;
        }
        if (source.Kind == "block")
        {
            var language = _read.Read(source.ProgrammingLanguage, "programmingLanguage", path);
            node["objectId"] = _read.Read(source.ObjectId, "identifier", path);
            node["blockType"] = _read.Read(source.BlockType, "blockType", path);
            node["number"] = _read.Read(source.Number, "number", path);
            node["programmingLanguage"] = language;
            node["isSafety"] = source.InSafetyUnit ? true : IsSafetyLanguage(language);
            return node;
        }
        if (source.Kind is "blockGroup" or "typeGroup" or "tagTableGroup")
        {
            var groupId = _read.Read(source.ObjectId, "identifier", path);
            if (!string.IsNullOrWhiteSpace(groupId)) node["objectId"] = groupId;
        }
        var children = new List<Dictionary<string, object?>>();
        node["children"] = children;
        foreach (var composition in source.Compositions)
        {
            var branch = _read.Collect(composition, child => Read(child, path), path);
            if (branch != null) children.AddRange(branch);
        }
        return node;
    }

    // V20 native F languages, including generated failsafe call blocks outside safety units.
    internal static bool? IsSafetyLanguage(string? language) => language == null ? null :
        language is "F_STL" or "F_LAD" or "F_FBD" or "F_DB" or "F_LAD_LIB" or "F_FBD_LIB" or "F_CALL";
}
