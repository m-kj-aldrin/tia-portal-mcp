using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessPlc
{
    public readonly struct Resolved
    {
        public Resolved(ObjectIdentifierProvider identifiers, PlcSoftware software, DeviceItem cpu)
        {
            Identifiers = identifiers;
            Software = software;
            Cpu = cpu;
        }

        public ObjectIdentifierProvider Identifiers { get; }
        public PlcSoftware Software { get; }
        public DeviceItem Cpu { get; }
    }

    public static ObjectIdentifierProvider Identifiers(Project project, int processId) =>
        project.GetService<ObjectIdentifierProvider>() ??
        throw new ConnectionFault("unsupportedObject", processId, "The project does not expose ObjectIdentifierProvider.");

    public static Dictionary<string, object?> ReadAttributes(IEngineeringObject target, DiscoveryReadContext read)
    {
        var attributes = new Dictionary<string, object?>();
        var native = read.Read(() => target.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite), "attributes", null);
        if (native != null)
            foreach (var pair in native)
                attributes[pair.Key] = read.Read(() => DiscoveryValues.Convert(pair.Value), "attribute:" + pair.Key, null);
        return attributes;
    }

    // Keep each PLC object's native parent types; unnamed unit containers contribute no path segment.
    public static string? PathOf(IEngineeringObject item, string? name, Action validate, bool technology = false)
    {
        if (name == null) return null;
        var unitScope = !technology && (item is PlcBlock or PlcType or PlcTagTable);
        var names = new Stack<string>();
        names.Push(name);
        IEngineeringObject? current = item.Parent;
        while (current != null)
        {
            validate();
            switch (current)
            {
                case PlcSoftware software:
                    names.Push(software.Name);
                    return string.Join("/", names);
                case PlcBlockGroup group when !technology && item is PlcBlock: names.Push(group.Name); break;
                case PlcSystemBlockGroup group when !technology && item is PlcBlock: names.Push(group.Name); break;
                case PlcTypeGroup group when item is PlcType: names.Push(group.Name); break;
                case PlcSystemTypeGroup group when item is PlcType: names.Push(group.Name); break;
                case PlcTagTableGroup group when item is PlcTagTable: names.Push(group.Name); break;
                case TechnologicalInstanceDBGroup group when technology: names.Push(group.Name); break;
                case PlcUnitBase unit when unitScope: names.Push(unit.Name); break;
                case PlcUnitSystemGroup when unitScope: break;
                case PlcUnitProvider when unitScope: break;
                default: return null;
            }
            current = current.Parent;
        }
        return null;
    }

    // An object without a readable native identifier keeps a null ID; context loss still propagates.
    public static string? OptionalIdentifier(ObjectIdentifierProvider identifiers, Func<IEngineeringObject?> target)
    {
        try { return DiscoveryValues.Nonblank(identifiers.GetIdentifier(target())); }
        catch (Exception ex) when (ex is not ConnectionFault) { return null; }
    }

    public static Resolved Resolve(Project project, int processId, string plcObjectId)
    {
        var identifiers = Identifiers(project, processId);
        var target = identifiers.Find(plcObjectId);
        if (target == null)
            throw new ConnectionFault("objectNotFound", processId, "The selected PLC object was not found.");
        if (!(target is DeviceItem cpu) || !(cpu.GetService<SoftwareContainer>()?.Software is PlcSoftware plc))
            throw new ConnectionFault("unsupportedObject", processId, "plcObjectId must identify the CPU DeviceItem owning PlcSoftware.");
        return new Resolved(identifiers, plc, cpu);
    }
}
