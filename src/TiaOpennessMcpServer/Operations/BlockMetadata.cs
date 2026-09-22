namespace TiaOpennessMcpServer.Operations;

internal static class BlockMetadata
{
    public static string? OptionalPath(bool include, Func<string?> read, Action validate)
    {
        if (!include) return null;
        try { return read(); }
        catch (ConnectionFault) { throw; }
        catch { validate(); return null; }
    }

    public static Dictionary<string, object?> Map(Dictionary<string, object?> attributes, string objectId, string? path, string blockType)
    {
        object? Take(string key)
        {
            if (!attributes.TryGetValue(key, out var value)) return null;
            attributes.Remove(key); return value;
        }
        return new Dictionary<string, object?>
        {
            ["objectId"] = objectId, ["path"] = path, ["blockType"] = blockType,
            ["name"] = Take("Name"), ["number"] = Take("Number"), ["autoNumber"] = Take("AutoNumber"),
            ["namespace"] = Take("Namespace"), ["programmingLanguage"] = Take("ProgrammingLanguage"), ["memoryLayout"] = Take("MemoryLayout"),
            ["header"] = new Dictionary<string, object?> { ["author"] = Take("HeaderAuthor"), ["family"] = Take("HeaderFamily"),
                ["userDefinedId"] = Take("HeaderName"), ["version"] = Take("HeaderVersion") },
            ["state"] = new Dictionary<string, object?> { ["isConsistent"] = Take("IsConsistent"), ["isKnowHowProtected"] = Take("IsKnowHowProtected") },
            ["timestamps"] = new Dictionary<string, object?> { ["created"] = Take("CreationDate"), ["modified"] = Take("ModifiedDate"),
                ["compiled"] = Take("CompileDate"), ["codeModified"] = Take("CodeModifiedDate"), ["interfaceModified"] = Take("InterfaceModifiedDate"),
                ["parameterModified"] = Take("ParameterModified"), ["structureModified"] = Take("StructureModified") },
            ["typeSpecific"] = attributes
        };
    }
}
