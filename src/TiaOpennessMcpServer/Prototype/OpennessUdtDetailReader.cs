using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Prototype;

internal static class OpennessUdtDetailReader
{
    public static BlockRead Read(Project project, BlockReadRequest request, Action validate)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null) throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected UDT was not found.");
        if (!(target is PlcType type)) throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC data type.");
        var result = new BlockRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        var attributes = new Dictionary<string, object?>();
        var native = read.Read(() => type.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite), "attributes", null);
        if (native != null)
            foreach (var pair in native)
                attributes[pair.Key] = read.Read(() => DiscoveryValues.Convert(pair.Value), "attribute:" + pair.Key, null);
        var name = attributes.TryGetValue("Name", out var value) ? value as string : null;
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => PathOf(type, name, validate), validate);
        result.Metadata = UdtMetadata.Map(attributes, request.ObjectId, path);
        BlockSourceReader.Read(result, request, null, false,
            format => OpennessSourceExporter.Export(type, ".udt", format, request, result, validate), validate,
            ex => ex is EngineeringException ? "tia-openness" : "bridge", udt: true);
        return result;
    }

    private static string? PathOf(PlcType type, string? name, Action validate)
    {
        if (name == null) return null;
        var names = new Stack<string>(); names.Push(name);
        IEngineeringObject? current = type.Parent;
        while (current != null)
        {
            validate();
            if (current is PlcSoftware software) { names.Push(software.Name); return string.Join("/", names); }
            if (current is PlcTypeGroup group) names.Push(group.Name);
            else if (current is PlcSystemTypeGroup system) names.Push(system.Name);
            else if (current is PlcUnitBase unit) names.Push(unit.Name);
            else if (!(current is PlcUnitSystemGroup) && !(current is PlcUnitProvider)) return null;
            current = current.Parent;
        }
        return null;
    }
}
