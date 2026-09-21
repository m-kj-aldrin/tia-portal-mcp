using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Prototype;

internal static class OpennessBlockDetailReader
{
    public static BlockRead Read(Project project, BlockReadRequest request, Action validate)
    {
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers == null) throw new ConnectionFault("unsupportedObject", request.ProcessId, "The project does not expose ObjectIdentifierProvider.");
        var target = identifiers.Find(request.ObjectId);
        if (target == null) throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected block was not found.");
        if (!(target is PlcBlock block)) throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC block.");
        var result = new BlockRead();
        var read = new DiscoveryReadContext(result.Errors, validate);
        var attributes = new Dictionary<string, object?>();
        // One native bulk attribute read. Never re-fetch a mapped attribute via its typed property.
        var native = read.Read(() => block.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite), "attributes", null);
        if (native != null)
            foreach (var pair in native)
                attributes[pair.Key] = read.Read(() => DiscoveryValues.Convert(pair.Value), "attribute:" + pair.Key, null);
        var name = attributes.TryGetValue("Name", out var value) ? value as string : null;
        var path = BlockMetadata.OptionalPath(request.IncludePath, () => PathOf(block, name, validate), validate);
        result.Metadata = BlockMetadata.Map(attributes, request.ObjectId, path, OpennessBlockReader.BlockType(block));
        BlockSourceReader.Read(result, request, result.Metadata["programmingLanguage"] as string, block is DataBlock,
            format => Export(block, format, request, result, validate), validate,
            ex => ex is EngineeringException ? "tia-openness" : "bridge");
        return result;
    }

    private static string? PathOf(PlcBlock block, string? name, Action validate)
    {
        if (name == null) return null;
        var names = new Stack<string>(); names.Push(name);
        IEngineeringObject? current = block.Parent;
        while (current != null)
        {
            validate();
            if (current is PlcSoftware software) { names.Push(software.Name); return string.Join("/", names); }
            if (current is PlcBlockGroup group) names.Push(group.Name);
            else if (current is PlcSystemBlockGroup system) names.Push(system.Name);
            else if (current is PlcUnitBase unit) names.Push(unit.Name);
            else if (!(current is PlcUnitSystemGroup) && !(current is PlcUnitProvider)) return null;
            current = current.Parent;
        }
        return null;
    }

    private static PlcExternalSourceSystemGroup ExternalSources(PlcBlock block, Action validate)
    {
        IEngineeringObject? current = block.Parent;
        while (current != null)
        {
            validate();
            if (current is PlcUnitBase unit) return unit.ExternalSourceGroup;
            if (current is PlcSoftware software) return software.ExternalSourceGroup;
            current = current.Parent;
        }
        throw new InvalidOperationException("The block's owning external-source group could not be resolved.");
    }

    private static BlockSource Export(PlcBlock block, string format, BlockReadRequest request, BlockRead result, Action validate)
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var folder = Path.GetFullPath(Path.Combine(temporaryRoot, "tia-read-" + Guid.NewGuid().ToString("N")));
        if (!folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || folder.Length > 200)
            throw new InvalidOperationException("A short, owned temporary export directory is required.");
        Directory.CreateDirectory(folder);
        try
        {
            var source = new BlockSource { Format = format, DependenciesIncluded = format == "external-source" && request.IncludeDependencies };
            IEnumerable<FileInfo> files;
            validate();
            switch (format)
            {
                case "external-source":
                    var language = result.Metadata["programmingLanguage"] as string;
                    var extension = block is DataBlock ? ".db" : language == "STL" ? ".awl" : ".scl";
                    var output = new FileInfo(Path.Combine(folder, "block" + extension));
                    ExternalSources(block, validate).GenerateSource(new IGenerateSource[] { block }, output,
                        request.IncludeDependencies ? GenerateOptions.WithDependencies : GenerateOptions.None);
                    files = new[] { output };
                    break;
                case "simatic-sd":
                    var exported = block.ExportAsDocuments(new DirectoryInfo(Path.GetFullPath(folder)), "block");
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
                    var xml = new FileInfo(Path.Combine(folder, "block.xml"));
                    block.Export(xml, ExportOptions.WithReadOnly);
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
