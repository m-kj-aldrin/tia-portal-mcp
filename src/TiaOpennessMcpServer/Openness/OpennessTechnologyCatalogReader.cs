using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.HW;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessTechnologyCatalogReader
{
    public static AvailableTechnologyObjects Read(Project project, int processId, string plcObjectId, Action validate)
    {
        validate();
        var cpu = OpennessPlc.Resolve(project, processId, plcObjectId).Cpu;
        validate();
        var typeIdentifier = cpu.TypeIdentifier;
        validate();
        var infos = cpu.GetAttributeInfos();
        var names = new[] { "TypeName", "OrderNumber", "FirmwareVersion" }
            .Where(name => infos.Any(info => info.Name == name && (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0))
            .ToArray();
        string? typeName = null, orderNumber = null, firmware = null;
        if (names.Length > 0)
        {
            validate();
            var values = cpu.GetAttributes(names);
            for (var index = 0; index < names.Length; index++)
            {
                if (values[index] is not string text || string.IsNullOrWhiteSpace(text)) continue;
                switch (names[index])
                {
                    case "TypeName": typeName = text; break;
                    case "OrderNumber": orderNumber = text; break;
                    case "FirmwareVersion": firmware = text; break;
                }
            }
        }
        var result = TechnologyObjectCatalogue.SelectFile(
            TechnologyObjectCatalogue.BesideExecutable(), typeName, typeIdentifier, orderNumber, firmware);
        result.ProcessId = processId;
        result.PlcObjectId = plcObjectId;
        return result;
    }
}
