using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessBlockDetailReader
{
    public static BlockRead Read(Project project, BlockReadRequest request, Action validate)
    {
        var identifiers = OpennessPlc.Identifiers(project, request.ProcessId);
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected block was not found.");
        if (!(target is PlcBlock block)) throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC block.");
        var result = new BlockRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        // One native bulk attribute read. Never re-fetch a mapped attribute via its typed property.
        var attributes = OpennessPlc.ReadAttributes(block, read);
        var name = attributes.TryGetValue("Name", out var value) ? value as string : null;
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => OpennessPlc.PathOf(block, name, validate), validate);
        result.Metadata = BlockMetadata.Map(attributes, request.ObjectId, path, OpennessBlockReader.BlockType(block));
        BlockSourceReader.Read(result, request,
            format => OpennessSourceExporter.Export(block, block is DataBlock ? ".db" :
                result.Metadata["programmingLanguage"] as string == "STL" ? ".awl" : ".scl", format, request, result, validate), validate,
            ex => ex is EngineeringException ? "tia-openness" : "bridge");
        return result;
    }

}
