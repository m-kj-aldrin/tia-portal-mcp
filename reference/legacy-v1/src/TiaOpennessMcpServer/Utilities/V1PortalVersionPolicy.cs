using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// Selects the native TIA Portal version from the installed-product snapshot.
/// This policy does not infer a version from project or installation artifacts.
/// </summary>
public static class V1PortalVersionPolicy
{
    private const string SiemensProductName = "Totally Integrated Automation Portal";
    private const string ShortProductName = "TIA Portal";

    public static string? FromInstalledProducts(IEnumerable<V1InstalledProduct> products)
    {
        if (products is null)
        {
            throw new ArgumentNullException(nameof(products));
        }

        var nativeVersion = products.FirstOrDefault(product =>
            IsPortalProductName(product.Name))?.Version;

        return string.IsNullOrWhiteSpace(nativeVersion) ? null : nativeVersion;
    }

    private static bool IsPortalProductName(string name) =>
        string.Equals(name, SiemensProductName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, ShortProductName, StringComparison.OrdinalIgnoreCase);
}
