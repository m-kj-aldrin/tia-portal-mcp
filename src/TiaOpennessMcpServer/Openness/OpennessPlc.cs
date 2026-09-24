using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessPlc
{
    public readonly struct Resolved
    {
        public Resolved(ObjectIdentifierProvider identifiers, PlcSoftware software)
        {
            Identifiers = identifiers;
            Software = software;
        }

        public ObjectIdentifierProvider Identifiers { get; }
        public PlcSoftware Software { get; }
    }

    public static Resolved Resolve(Project project, int processId, string plcObjectId)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null)
            throw new ConnectionFault("unsupportedObject", processId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(plcObjectId);
        if (target == null)
            throw new ConnectionFault("objectNotFound", processId, "The selected PLC object was not found.");
        if (!(target is DeviceItem cpu) || !(cpu.GetService<SoftwareContainer>()?.Software is PlcSoftware plc))
            throw new ConnectionFault("unsupportedObject", processId, "plcObjectId must identify the CPU DeviceItem owning PlcSoftware.");
        return new Resolved(identifiers, plc);
    }
}
