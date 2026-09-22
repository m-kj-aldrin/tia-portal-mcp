using TiaOpennessMcpServer.Operations;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Openness;

internal static class OpennessSourceExporter
{
    private static PlcExternalSourceSystemGroup ExternalSources(IEngineeringObject target, Action validate)
    {
        IEngineeringObject? current = target.Parent;
        while (current != null)
        {
            validate();
            if (current is PlcUnitBase unit) return unit.ExternalSourceGroup;
            if (current is PlcSoftware software) return software.ExternalSourceGroup;
            current = current.Parent;
        }
        throw new InvalidOperationException("The object's owning external-source group could not be resolved.");
    }

    public static BlockSource Export(IEngineeringObject target, string extension, string format, BlockReadRequest request, BlockRead result, Action validate)
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var folder = Path.GetFullPath(Path.Combine(temporaryRoot, "tia-read-" + Guid.NewGuid().ToString("N")));
        if (!folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || folder.Length > 200)
            throw new InvalidOperationException("A short, owned temporary export directory is required.");
        Directory.CreateDirectory(folder);
        try
        {
            var source = new BlockSource { Format = format, DependenciesIncluded = format == "external-source" && request.IncludeDependencies };
            var stem = target is PlcType ? "udt" : "block";
            IEnumerable<FileInfo> files;
            validate();
            switch (format)
            {
                case "external-source":
                    var output = new FileInfo(Path.Combine(folder, stem + extension));
                    ExternalSources(target, validate).GenerateSource(new[] { (IGenerateSource)target }, output,
                        request.IncludeDependencies ? GenerateOptions.WithDependencies : GenerateOptions.None);
                    files = new[] { output };
                    break;
                case "simatic-sd":
                    var directory = new DirectoryInfo(Path.GetFullPath(folder));
                    var exported = target is PlcBlock block ? block.ExportAsDocuments(directory, stem) :
                        ((PlcType)target).ExportAsDocuments(directory, stem);
                    validate();
                    var state = exported.State;
                    var messages = exported.Messages.Select(message => message.Message).ToList();
                    if (state != DocumentResultState.Success)
                    {
                        var errors = messages.Select(message => new DiscoveryError { Origin = "tia-openness", Operation = "sourceExport",
                            Format = format, Message = message }).ToList();
                        errors.Add(new DiscoveryError { Origin = "bridge", Operation = "sourceExport", Format = format,
                            Message = "SIMATIC SD returned " + state + "; a complete successful export is required." });
                        throw new SourceExportFault(errors); // In particular, never return PartialSuccess documents.
                    }
                    source.NativeState = state.ToString(); source.NativeMessages = messages;
                    files = exported.ExportedDocuments.ToArray();
                    break;
                case "simatic-ml":
                    var xml = new FileInfo(Path.Combine(folder, stem + ".xml"));
                    if (target is PlcBlock xmlBlock) xmlBlock.Export(xml, ExportOptions.WithReadOnly);
                    else ((PlcType)target).Export(xml, ExportOptions.WithReadOnly);
                    files = new[] { xml };
                    break;
                default: throw new InvalidOperationException("Unknown source format.");
            }
            validate();
            foreach (var file in files)
            {
                var fullPath = Path.GetFullPath(file.FullName);
                if (!fullPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Export returned a file outside the owned staging directory.");
                using var reader = new StreamReader(fullPath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                source.Documents.Add(new SourceDocument(fullPath.Substring(folder.Length + 1), reader.ReadToEnd()));
            }
            return source;
        }
        finally
        {
            try
            {
                // Only this request's generated directory is deleted. Never delete a caller/native-provided path.
                if (!folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                        .Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
                    throw new IOException("Temporary export cleanup refused an unexpected path or link.");
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                result.Attempts.Add(new SourceAttempt { Format = format, State = "cleanupFailed", Errors = new()
                { new DiscoveryError { Origin = "bridge", Operation = "temporaryCleanup", Format = format, Message = ex.Message } } });
            }
        }
    }
}
