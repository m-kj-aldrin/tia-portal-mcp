using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;
using TiaOpennessMcpServer.Operations;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessTagTableExporter
{
    public static TagTableExportResult Read(Project project, ExportTagTableRequest request, Action validate)
    {
        validate();
        var identifiers = project.GetService<ObjectIdentifierProvider>() ??
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(request.ObjectId) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected tag table was not found.");
        validate();
        if (target is not PlcTagTable table)
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC tag table.");
        var result = new TagTableExportResult { ObjectId = request.ObjectId };
        // V20 exposes SimaticML export for tag tables, with no ExportAsDocuments or IGenerateSource path.
        TagTableExportReader.Read(result, file => table.Export(file, ExportOptions.WithReadOnly), validate,
            ex => ex is EngineeringException ? "tia-openness" : "bridge");
        return result;
    }
}
