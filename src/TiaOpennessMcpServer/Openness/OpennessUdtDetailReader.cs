using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.SW.Types;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessUdtDetailReader
{
    public static BlockRead Read(Project project, BlockReadRequest request, Action validate)
    {
        var identifiers = OpennessPlc.Identifiers(project, request.ProcessId);
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected UDT was not found.");
        if (!(target is PlcType type)) throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC data type.");
        var result = new BlockRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        var attributes = OpennessPlc.ReadAttributes(type, read);
        var name = attributes.TryGetValue("Name", out var value) ? value as string : null;
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => OpennessPlc.PathOf(type, name, validate), validate);
        result.Metadata = UdtMetadata.Map(attributes, request.ObjectId, path);
        BlockSourceReader.Read(result, request,
            format => OpennessSourceExporter.Export(type, ".udt", format, request, result, validate), validate,
            ex => ex is EngineeringException ? "tia-openness" : "bridge");
        return result;
    }

}
