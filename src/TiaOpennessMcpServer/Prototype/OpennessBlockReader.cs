using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Prototype;

// Constructed and consumed only on the shared STA, with the retained project and original ticket.
internal sealed class OpennessBlockReader
{
    private readonly ObjectIdentifierProvider _identifiers;

    private OpennessBlockReader(ObjectIdentifierProvider identifiers) => _identifiers = identifiers;

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
        var adapter = new OpennessBlockReader(identifiers);
        var root = new BlockInventoryNode { Kind = "scope", ScopeType = "plcSoftware", Name = () => plc.Name };
        root.Compositions.Add(() => One(adapter.Group(plc.BlockGroup, false)));
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
        node.Compositions.Add(() => One(Group(unit.BlockGroup, safety)));
        return node;
    }

    private BlockInventoryNode Group(PlcBlockGroup group, bool safety)
    {
        var node = new BlockInventoryNode { Name = () => group.Name,
            IsSystem = group is PlcBlockSystemGroup, InSafetyUnit = safety };
        // The root PlcBlockSystemGroup is a system-owned container, not a system-block classification.
        node.Compositions.Add(() => group.Blocks.Select(block => Block(block, false, safety)));
        node.Compositions.Add(() => group.Groups.Select(child => Group(child, safety)));
        if (group is PlcBlockSystemGroup root)
            node.Compositions.Add(() => root.SystemBlockGroups.Select(child => SystemGroup(child, safety)));
        return node;
    }

    private BlockInventoryNode SystemGroup(PlcSystemBlockGroup group, bool safety)
    {
        var node = new BlockInventoryNode { Name = () => group.Name, IsSystem = true, InSafetyUnit = safety };
        node.Compositions.Add(() => group.Blocks.Select(block => Block(block, true, safety)));
        node.Compositions.Add(() => group.Groups.Select(child => SystemGroup(child, safety)));
        return node;
    }

    private BlockInventoryNode Block(PlcBlock block, bool system, bool safety) => new()
    {
        Kind = "block", Name = () => block.Name,
        ObjectId = () => DiscoveryValues.Nonblank(_identifiers.GetIdentifier(block)),
        BlockType = () => BlockType(block),
        Number = () => block.Number, ProgrammingLanguage = () => block.ProgrammingLanguage.ToString(),
        IsSystem = system, InSafetyUnit = safety
    };

    internal static string BlockType(PlcBlock block) => block switch
    {
        OB => "OB", FB => "FB", FC => "FC", GlobalDB => "DB", InstanceDB => "DB", ArrayDB => "DB",
        _ => block.GetType().Name
    };

    private static IEnumerable<BlockInventoryNode> One(BlockInventoryNode node) { yield return node; }
}
