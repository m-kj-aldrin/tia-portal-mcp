using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Prototype;

// Constructed and consumed only on the shared STA, with the retained project and original ticket.
internal sealed class OpennessTagTableReader
{
    private readonly ObjectIdentifierProvider _identifiers;

    private OpennessTagTableReader(ObjectIdentifierProvider identifiers) => _identifiers = identifiers;

    public static BlockInventory Read(Project project, int processId, string plcObjectId, Action validate)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null)
            throw new ConnectionFault("unsupportedObject", processId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(plcObjectId);
        if (target == null)
            throw new ConnectionFault("objectNotFound", processId, "The selected PLC object was not found.");
        if (!(target is DeviceItem cpu) || !(cpu.GetService<SoftwareContainer>()?.Software is PlcSoftware plc))
            throw new ConnectionFault("unsupportedObject", processId, "plcObjectId must identify the CPU DeviceItem owning PlcSoftware.");

        var result = new BlockInventory { PlcObjectId = plcObjectId };
        var adapter = new OpennessTagTableReader(identifiers);
        var root = new BlockInventoryNode { Kind = "scope", ScopeType = "plcSoftware", Name = () => plc.Name };
        root.Compositions.Add(() => One(adapter.Group(plc.TagTableGroup, false)));
        // No provider means this CPU exposes no unit hierarchy. Exceptions remain partial-read errors.
        root.Compositions.Add(() => adapter.Units(plc, false));
        root.Compositions.Add(() => adapter.Units(plc, true));
        result.Roots.Add(new BlockInventoryReader(result, validate).Read(root));
        return result;
    }

    private IEnumerable<BlockInventoryNode> Units(PlcSoftware plc, bool safety)
    {
        var provider = plc.GetService<PlcUnitProvider>();
        if (provider == null) yield break;
        var group = provider.UnitGroup;
        // PlcUnitSystemGroup has no native Name in V20. Keep named unit scopes directly under PLC.
        if (safety)
        {
            foreach (var unit in group.SafetyUnits) yield return Unit(unit, true);
        }
        else
        {
            foreach (var unit in group.Units) yield return Unit(unit, false);
        }
    }

    private BlockInventoryNode Unit(PlcUnitBase unit, bool safety)
    {
        var node = new BlockInventoryNode { Kind = "scope", ScopeType = safety ? "safetyUnit" : "softwareUnit",
            Name = () => unit.Name, InSafetyUnit = safety };
        node.Compositions.Add(() => One(Group(unit.TagTableGroup, safety)));
        return node;
    }

    private BlockInventoryNode Group(PlcTagTableGroup group, bool safety)
    {
        var node = new BlockInventoryNode { Kind = "tagTableGroup", Name = () => group.Name,
            ObjectId = () => DiscoveryValues.Nonblank(_identifiers.GetIdentifier(group)),
            IsSystem = group is PlcTagTableSystemGroup, InSafetyUnit = safety };
        // The root PlcTagTableSystemGroup is a system-owned container, not a system-table classification.
        node.Compositions.Add(() => group.TagTables.Select(table => Table(table, safety)));
        node.Compositions.Add(() => group.Groups.Select(child => Group(child, safety)));
        return node;
    }

    private BlockInventoryNode Table(PlcTagTable table, bool safety) => new()
    {
        Kind = "tagTable", Name = () => table.Name,
        ObjectId = () => DiscoveryValues.Nonblank(_identifiers.GetIdentifier(table)),
        IsSystem = false, InSafetyUnit = safety
    };

    private static IEnumerable<BlockInventoryNode> One(BlockInventoryNode node) { yield return node; }
}
