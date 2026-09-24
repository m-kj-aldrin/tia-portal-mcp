using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaOpennessMcpServer.Openness;

// Constructed and consumed only on the shared STA, with the retained project and original ticket.
internal sealed class OpennessTechnologyObjectReader
{
    private readonly ObjectIdentifierProvider _identifiers;

    private OpennessTechnologyObjectReader(ObjectIdentifierProvider identifiers) => _identifiers = identifiers;

    public static BlockInventory Read(Project project, int processId, string plcObjectId, Action validate)
    {
        var resolved = OpennessPlc.Resolve(project, processId, plcObjectId);
        var plc = resolved.Software;
        var result = new BlockInventory { PlcObjectId = plcObjectId };
        var adapter = new OpennessTechnologyObjectReader(resolved.Identifiers);
        var root = new BlockInventoryNode { Kind = "scope", ScopeType = "plcSoftware", Name = () => plc.Name };
        root.Compositions.Add(() => One(adapter.Group(plc.TechnologicalObjectGroup)));
        result.Roots.Add(new BlockInventoryReader(result, validate).Read(root));
        return result;
    }

    private BlockInventoryNode Group(TechnologicalInstanceDBGroup group)
    {
        var node = new BlockInventoryNode
        {
            Kind = "technologyObjectGroup", Name = () => group.Name,
            ObjectId = () => DiscoveryValues.Nonblank(_identifiers.GetIdentifier(group)),
            IsSystem = group is TechnologicalInstanceDBSystemGroup
        };
        node.Compositions.Add(() => group.TechnologicalObjects.Select(Object));
        node.Compositions.Add(() => group.Groups.Select(Group));
        return node;
    }

    private BlockInventoryNode Object(TechnologicalInstanceDB item) => new()
    {
        Kind = "technologyObject", Name = () => item.Name,
        ObjectId = () => DiscoveryValues.Nonblank(_identifiers.GetIdentifier(item)),
        Number = () => item.Number,
        SystemLibElement = () => item.OfSystemLibElement,
        SystemLibVersion = () => item.OfSystemLibVersion?.ToString()
    };

    private static IEnumerable<BlockInventoryNode> One(BlockInventoryNode node) { yield return node; }
}
