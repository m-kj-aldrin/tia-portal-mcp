using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Prototype;

internal static class OpennessBlockDetailReader
{
    public static BlockRead Read(Project project, BlockReadRequest request, Action validate)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null) throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected block was not found.");
        if (!(target is PlcBlock block)) throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC block.");
        var result = new BlockRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        var attributes = new Dictionary<string, object?>();
        // One native bulk attribute read. Never re-fetch a mapped attribute via its typed property.
        var native = read.Read(() => block.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite), "attributes", null);
        if (native != null)
            foreach (var pair in native)
                attributes[pair.Key] = read.Read(() => DiscoveryValues.Convert(pair.Value), "attribute:" + pair.Key, null);
        var name = attributes.TryGetValue("Name", out var value) ? value as string : null;
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => PathOf(block, name, validate), validate);
        result.Metadata = BlockMetadata.Map(attributes, request.ObjectId, path, OpennessBlockReader.BlockType(block));
        BlockSourceReader.Read(result, request, result.Metadata["programmingLanguage"] as string, block is DataBlock,
            format => OpennessSourceExporter.Export(block, block is DataBlock ? ".db" :
                result.Metadata["programmingLanguage"] as string == "STL" ? ".awl" : ".scl", format, request, result, validate), validate,
            ex => ex is EngineeringException ? "tia-openness" : "bridge");
        return result;
    }

    private static string? PathOf(PlcBlock block, string? name, Action validate)
    {
        if (name == null) return null;
        var names = new Stack<string>(); names.Push(name);
        IEngineeringObject? current = block.Parent;
        while (current != null)
        {
            validate();
            if (current is PlcSoftware software) { names.Push(software.Name); return string.Join("/", names); }
            if (current is PlcBlockGroup group) names.Push(group.Name);
            else if (current is PlcSystemBlockGroup system) names.Push(system.Name);
            else if (current is PlcUnitBase unit) names.Push(unit.Name);
            else if (!(current is PlcUnitSystemGroup) && !(current is PlcUnitProvider)) return null;
            current = current.Parent;
        }
        return null;
    }

}
