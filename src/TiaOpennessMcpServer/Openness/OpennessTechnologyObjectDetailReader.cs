using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessTechnologyObjectDetailReader
{
    public static TechnologyObjectRead Read(Project project, TechnologyObjectReadRequest request, Action validate)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null) throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected technology object was not found.");
        if (!(target is TechnologicalInstanceDB item))
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a technology object.");
        var result = new TechnologyObjectRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        var name = read.Read(() => item.Name, "name", null);
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => PathOf(item, name, validate), validate);
        result.Metadata = new Dictionary<string, object?>
        {
            ["objectId"] = request.ObjectId,
            ["path"] = path,
            ["name"] = name,
            ["number"] = read.Read(() => item.Number, "number", path),
            ["ofSystemLibElement"] = read.Read(() => item.OfSystemLibElement, "ofSystemLibElement", path),
            ["ofSystemLibVersion"] = read.Read(() => item.OfSystemLibVersion?.ToString(), "ofSystemLibVersion", path)
        };
        if (!request.IncludeParameters)
        {
            result.Parameters = null;
            return result;
        }
        result.Parameters = TechnologyObjectReader.ReadParameters(result, validate, () => item.Parameters.Select(parameter => new TechnologyParameterNode
        {
            Name = () => parameter.Name,
            Value = () => parameter.Value,
            ObjectId = () => Identifier(identifiers, parameter)
        }));
        return result;
    }

    private static string? Identifier(ObjectIdentifierProvider identifiers, TechnologicalParameter parameter)
    {
        try { return identifiers.GetIdentifier(parameter); }
        catch (ConnectionFault) { throw; }
        catch { return null; }
    }

    private static string? PathOf(TechnologicalInstanceDB item, string? name, Action validate)
    {
        if (name == null) return null;
        var names = new Stack<string>();
        names.Push(name);
        IEngineeringObject? current = item.Parent;
        while (current != null)
        {
            validate();
            if (current is PlcSoftware software) { names.Push(software.Name); return string.Join("/", names); }
            if (current is TechnologicalInstanceDBGroup group) names.Push(group.Name);
            else return null;
            current = current.Parent;
        }
        return null;
    }
}
