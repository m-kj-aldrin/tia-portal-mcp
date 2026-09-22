namespace TiaOpennessMcpServer.Operations;

internal static class UdtMetadata
{
    public static Dictionary<string, object?> Map(Dictionary<string, object?> attributes, string objectId, string? path)
    {
        object? Take(string key)
        {
            if (!attributes.TryGetValue(key, out var value)) return null;
            attributes.Remove(key); return value;
        }
        return new Dictionary<string, object?>
        {
            ["objectId"] = objectId, ["path"] = path, ["name"] = Take("Name"), ["namespace"] = Take("Namespace"),
            ["state"] = new Dictionary<string, object?> { ["isConsistent"] = Take("IsConsistent"), ["isKnowHowProtected"] = Take("IsKnowHowProtected") },
            ["timestamps"] = new Dictionary<string, object?> { ["created"] = Take("CreationDate"), ["modified"] = Take("ModifiedDate"),
                ["interfaceModified"] = Take("InterfaceModifiedDate") },
            ["typeSpecific"] = attributes
        };
    }
}
