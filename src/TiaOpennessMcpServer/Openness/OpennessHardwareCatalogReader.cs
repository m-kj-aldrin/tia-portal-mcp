using Siemens.Engineering;
using TiaOpennessMcpServer.Operations;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessHardwareCatalogReader
{
    public static HardwareCatalogRead Read(TiaPortal portal, HardwareCatalogRequest request, Action validate) =>
        HardwareCatalogReader.Read(request, validate, () => portal.HardwareCatalog.Find(string.Empty)
            .Select(entry => new HardwareCatalogNode
            {
                ReadField = field => field switch
                {
                    "typeIdentifier" => entry.TypeIdentifier,
                    "typeIdentifierNormalized" => entry.TypeIdentifierNormalized,
                    "articleNumber" => entry.ArticleNumber,
                    "typeName" => entry.TypeName,
                    "version" => entry.Version,
                    "catalogPath" => entry.CatalogPath,
                    "description" => entry.Description,
                    _ => throw new ArgumentOutOfRangeException(nameof(field))
                }
            }));
}
