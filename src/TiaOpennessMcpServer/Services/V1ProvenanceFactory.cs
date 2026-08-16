using Siemens.Engineering;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Creates a point-in-time provenance projection. Call only from the dedicated
/// STA scheduler; TiaPortalProcess diagnostics are refreshed for every call.
/// </summary>
internal static class V1ProvenanceFactory
{
    public static V1Provenance Create(
        TiaPortalService tia,
        V1PlcIdentity? plc = null,
        V1ObjectIdentity? engineeringObject = null)
    {
        var project = tia.Project;
        V1TiaIdentity? tiaIdentity = null;
        V1ProjectIdentity? projectIdentity = null;

        if (tia.Portal is not null)
        {
            try
            {
                var process = tia.Portal.GetCurrentProcess();
                var products = process.InstalledSoftware
                    .Select(MapProduct)
                    .ToList();
                tiaIdentity = new V1TiaIdentity
                {
                    PortalVersion = V1PortalVersionPolicy.FromInstalledProducts(products),
                    InstalledProducts = products,
                };
            }
            catch
            {
                // A disconnected/error response includes only provenance known now.
            }
        }

        if (project is not null)
        {
            projectIdentity = new V1ProjectIdentity
            {
                Name = V1TiaHelpers.Try(() => project.Name) ?? "<unknown>",
                Path = V1TiaHelpers.Try(() => project.Path.FullName),
                Version = V1ProjectVersionPolicy.Normalize(
                    V1TiaHelpers.Try(() => project.Version)),
                IsModified = V1TiaHelpers.Try<bool?>(() => project.IsModified),
            };
        }

        return new V1Provenance
        {
            ReadAtUtc = DateTimeOffset.UtcNow,
            Tia = tiaIdentity,
            Project = projectIdentity,
            Plc = plc,
            Object = engineeringObject,
        };
    }

    public static V1PlcIdentity PlcIdentity(
        V1PlcHandle plc,
        ObjectIdentifierProvider identifierProvider) => new()
    {
        ObjectId = V1TiaHelpers.TryGetIdentifier(identifierProvider, plc.DeviceItem),
        Name = V1TiaHelpers.Try(() => plc.DeviceItem.Name) ??
               V1TiaHelpers.Try(() => plc.Software.Name) ??
               V1TiaHelpers.Try(() => plc.Device.Name) ?? "<unknown PLC>",
        DeviceName = V1TiaHelpers.Try(() => plc.Device.Name),
        Path = plc.Path,
    };

    private static V1InstalledProduct MapProduct(TiaPortalProduct product) => new()
    {
        Name = product.Name ?? "",
        Version = product.Version,
        Options = product.Options.Select(MapProduct).ToList(),
    };
}
