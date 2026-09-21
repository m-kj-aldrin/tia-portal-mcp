using Siemens.Engineering;
using Siemens.Engineering.CrossReference;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class OpennessCrossReferenceReader
{
    private readonly ObjectIdentifierProvider _identifiers;
    private OpennessCrossReferenceReader(ObjectIdentifierProvider identifiers) => _identifiers = identifiers;

    public static CrossReferenceRead Read(Project project, CrossReferenceRequest request, Action validate)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null) throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected engineering object was not found.");
        var service = (target as IEngineeringServiceProvider)?.GetService<CrossReferenceService>();
        if (service == null) throw new ConnectionFault("unsupportedObject", request.ProcessId, "The selected object does not expose CrossReferenceService.");
        var result = new CrossReferenceRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        validate();
        var native = read.Read(() => service.GetCrossReferences(CrossReferenceFilter.AllObjects), "getCrossReferences", null);
        validate();
        if (native == null)
        {
            if (result.Errors.Count == 0)
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "getCrossReferences", Message = "The native query returned no result object." });
            return result;
        }
        var adapter = new OpennessCrossReferenceReader(identifiers);
        new CrossReferenceReader(result, validate).Read(result, () => native.Sources.Select(adapter.Source));
        return result;
    }

    private string? Identifier(object? underlying) => underlying is IEngineeringObject engineering
        ? DiscoveryValues.Nonblank(_identifiers.GetIdentifier(engineering)) : null;

    private CrossSourceNode Source(SourceObject source) => new()
    {
        Fields =
        {
            ["name"] = () => source.Name, ["path"] = () => source.Path, ["typeName"] = () => source.TypeName,
            ["device"] = () => source.Device, ["address"] = () => source.Address,
            ["objectId"] = () => Identifier(source.UnderlyingObject)
        },
        Children = () => source.Children.Select(Source),
        References = () => source.References.Select(Reference)
    };

    private CrossReferenceNode Reference(ReferenceObject reference) => new()
    {
        Fields =
        {
            ["name"] = () => reference.Name, ["path"] = () => reference.Path, ["typeName"] = () => reference.TypeName,
            ["device"] = () => reference.Device, ["address"] = () => reference.Address,
            ["objectId"] = () => Identifier(reference.UnderlyingObject)
        },
        Locations = () => reference.Locations.Select(Location)
    };

    private CrossObjectNode Location(Location location) => new()
    {
        Fields =
        {
            ["referenceType"] = () => location.ReferenceType, ["access"] = () => location.Access,
            ["referenceLocation"] = () => location.ReferenceLocation, ["name"] = () => location.Name,
            ["typeName"] = () => location.TypeName, ["address"] = () => location.Address,
            ["referencedAsName"] = () => location.ReferencedAsName,
            ["referencedAsObjectId"] = () => Identifier(location.ReferencedAs)
        }
    };
}
