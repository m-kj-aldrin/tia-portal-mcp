using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessTagTableDetailReader
{
    public static TagTableRead Read(Project project, TagTableReadRequest request, Action validate)
    {
        var identifiers = OpennessPlc.Identifiers(project, request.ProcessId);
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected tag table was not found.");
        if (!(target is PlcTagTable table)) throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC tag table.");
        var result = new TagTableRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        var attributes = TagTableReader.Attributes(read,
            () => table.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite), null);
        var name = attributes.TryGetValue("Name", out var value) ? value as string : null;
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => OpennessPlc.PathOf(table, name, validate), validate);
        result.Metadata = TagTableReader.Metadata(attributes, request.ObjectId, path);
        TagTableEntryNode Entry(IEngineeringObject entry) => new()
        {
            Identifier = () => identifiers.GetIdentifier(entry),
            Attributes = () => entry.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite)
        };
        // Lazy compositions: metadata-only never accesses or enumerates any entry collection.
        TagTableReader.ReadEntries(result, request.IncludeEntries, validate,
            () => table.Tags.Select(tag => Entry(tag)),
            () => table.UserConstants.Select(constant => Entry(constant)),
            () => table.SystemConstants.Select(constant => Entry(constant)));
        return result;
    }

}
