using System.Text;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Prototype;

// Disposable-project probes. Runs only on the shared STA with the retained project and original ticket.
internal static class OpennessWriteProbe
{
    public static WriteProbeResult Run(Project project, WriteProbeRequest request, WriteProbeSession session, Action validate)
    {
        var result = new WriteProbeResult { Action = request.Action, Armed = true };
        try
        {
            switch (request.Action)
            {
                case "createCopy":
                case "replace": Copy(project, request, session, result, validate); break;
                case "createTable": CreateTable(project, request, session, result, validate); break;
                case "addTag": AddTag(project, request, session, result, validate); break;
                case "setTagAttribute": SetAttribute(project, request, result, validate); break;
                case "deleteTag": DeleteEntry(project, request, session, result, validate); break;
                case "importTagTable": ImportTable(project, request, session, result, validate); break;
                default: throw new ConnectionFault("invalidRequest", request.ProcessId, "This write probe is not executable.");
            }
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate();
            result.Errors.Add(Error(ex, request.Action));
        }
        try { result.ProjectModified = project.IsModified; }
        catch (Exception ex)
        {
            validate();
            result.Errors.Add(Error(ex, "projectModified"));
        }
        return result;
    }

    private static void Copy(Project project, WriteProbeRequest request, WriteProbeSession session, WriteProbeResult result, Action validate)
    {
        var identifiers = Identifiers(project, request.ProcessId);
        validate();
        var target = identifiers.Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected object was not found.");
        var replacing = request.Action == "replace";
        var attributes = Attributes(target, validate);
        var currentName = attributes.TryGetValue("Name", out var name) ? name as string : null;
        var language = attributes.TryGetValue("ProgrammingLanguage", out var programming) ? programming as string : null;
        var dataBlock = target is DataBlock;
        var udt = target is PlcType;
        if (target is not PlcBlock && !udt)
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a PLC block or PLC data type.");
        var format = BlockSourceReader.Formats(request.SourceFormat, language, dataBlock, udt)[0];
        result.Format = format;
        var scope = ScopeOf(target, validate, request.ProcessId);
        var explicitGroup = !string.IsNullOrWhiteSpace(request.GroupObjectId) || !string.IsNullOrWhiteSpace(request.GroupPath);
        var destination = Destination(identifiers, scope, request, udt, validate);
        if (!replacing && NameTaken(destination, request.NewName!))
            throw new ConnectionFault("alreadyExists", request.ProcessId, "An object with this name already exists in the target group.");

        var scratch = new BlockRead();
        var extension = format == "simatic-ml" ? ".xml" : format == "simatic-sd" ? ".s7dcl" :
            udt ? ".udt" : dataBlock ? ".db" : language == "STL" ? ".awl" : ".scl";
        BlockSource exported;
        try
        {
            using var body = JsonDocument.Parse("{\"processId\":" + request.ProcessId + ",\"objectId\":" +
                JsonSerializer.Serialize(request.ObjectId) + ",\"includePath\":false,\"sourceFormat\":" + JsonSerializer.Serialize(format) + "}");
            exported = OpennessSourceExporter.Export(target, extension, format, BlockReadRequest.Parse(body.RootElement), scratch, validate);
        }
        catch (SourceExportFault fault)
        {
            result.Errors.AddRange(fault.Errors);
            return;
        }
        foreach (var attempt in scratch.Attempts.Where(item => item.State == "cleanupFailed"))
            result.Errors.AddRange(attempt.Errors);
        var contents = exported.Documents.Select(document => document.Content).ToList();
        var names = exported.Documents.Select(document => document.Name).ToList();
        if (!replacing)
        {
            try { contents = WriteProbeNames.Substitute(contents, currentName, request.NewName!, request.ProcessId).ToList(); }
            catch (ConnectionFault ex) when (ex.Code == "ambiguousName")
            {
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "ambiguousName", Message = ex.Message });
                return;
            }
        }

        string? folder = null;
        PlcExternalSource? external = null;
        try
        {
            folder = WriteProbeFiles.CreateDirectory();
            var files = WriteDocuments(folder, names, contents);
            validate();
            if (format == "external-source")
            {
                if (files.Count != 1)
                    throw new InvalidOperationException("External-source export must be one document.");
                external = Sources(scope).ExternalSources.CreateFromFile("probe" + Guid.NewGuid().ToString("N").Substring(0, 8), files[0].FullName);
                validate();
                var generated = Generate(external, destination, !explicitGroup, request.ProcessId);
                Remember(result, session, identifiers, generated, udt ? "udt" : "block", replacing ? currentName : request.NewName, null, validate);
            }
            else if (format == "simatic-sd")
            {
                var stem = files.Select(file => file.Name).FirstOrDefault(file => file.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase));
                if (stem == null) throw new InvalidOperationException("SIMATIC SD export did not include an .s7dcl document.");
                stem = Path.GetFileNameWithoutExtension(stem);
                var options = replacing ? ImportDocumentOptions.Override : ImportDocumentOptions.None;
                if (udt)
                {
                    var imported = ((PlcTypeGroup)destination).Types.ImportFromDocuments(new DirectoryInfo(folder), stem, options);
                    if (Accepted(result, imported.State, imported.Messages.Select(item => item.Message)))
                        Remember(result, session, identifiers, imported.ImportedPlcTypes, "udt", replacing ? currentName : request.NewName, null, validate);
                }
                else
                {
                    var imported = ((PlcBlockGroup)destination).Blocks.ImportFromDocuments(new DirectoryInfo(folder), stem, options);
                    if (Accepted(result, imported.State, imported.Messages.Select(item => item.Message)))
                        Remember(result, session, identifiers, imported.ImportedPlcBlocks, "block", replacing ? currentName : request.NewName, null, validate);
                }
            }
            else
            {
                var xml = files.First(file => file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase));
                var mode = replacing ? ImportOptions.Override : ImportOptions.None;
                if (udt)
                    Remember(result, session, identifiers, ((PlcTypeGroup)destination).Types.Import(xml, mode), "udt", replacing ? currentName : request.NewName, null, validate);
                else
                    Remember(result, session, identifiers, ((PlcBlockGroup)destination).Blocks.Import(xml, mode), "block", replacing ? currentName : request.NewName, null, validate);
            }
            if (result.Created.Count == 0 && result.Errors.Count == 0)
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = request.Action, Message = "The native write returned no objects." });
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate();
            result.Errors.Add(Error(ex, format));
        }
        finally { Cleanup(result, external, folder, validate); }
    }

    private static void CreateTable(Project project, WriteProbeRequest request, WriteProbeSession session, WriteProbeResult result, Action validate)
    {
        var plc = Cpu(project, request.PlcObjectId!, request.ProcessId);
        validate();
        var group = TagGroup(project, plc, request, validate);
        if (group.TagTables.Find(request.NewName!) != null)
            throw new ConnectionFault("alreadyExists", request.ProcessId, "A tag table with this name already exists in the target group.");
        validate();
        var table = group.TagTables.Create(request.NewName!);
        Remember(result, session, Identifiers(project, request.ProcessId), new[] { table }, "tagTable", request.NewName, null, validate);
    }

    private static void AddTag(Project project, WriteProbeRequest request, WriteProbeSession session, WriteProbeResult result, Action validate)
    {
        var table = Table(project, request);
        try
        {
            validate();
            IEngineeringObject created = request.EntryKind == "userConstant"
                ? table.UserConstants.Create(request.NewName!, request.DataType!, request.Value!)
                : table.Tags.Create(request.NewName!, request.DataType!, request.LogicalAddress!);
            Remember(result, session, Identifiers(project, request.ProcessId), new[] { created }, request.EntryKind!, request.NewName, request.ObjectId, validate);
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate();
            session.NoteTypedFailure(request.ObjectId!);
            result.Errors.Add(Error(ex, "addTag"));
        }
    }

    private static void SetAttribute(Project project, WriteProbeRequest request, WriteProbeResult result, Action validate)
    {
        var target = Identifiers(project, request.ProcessId).Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected entry was not found.");
        if (target is PlcSystemConstant)
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "System constants are left unchanged.");
        validate();
        switch (target)
        {
            case PlcTag tag: tag.SetAttribute(request.AttributeName!, request.AttributeValue); break;
            case PlcUserConstant constant: constant.SetAttribute(request.AttributeName!, request.AttributeValue); break;
            default: throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a probe-created tag or user constant.");
        }
        result.Created.Add(new WriteProbeObject { ObjectId = request.ObjectId, Kind = target is PlcTag ? "tag" : "userConstant", Name = request.AttributeName });
    }

    private static void DeleteEntry(Project project, WriteProbeRequest request, WriteProbeSession session, WriteProbeResult result, Action validate)
    {
        var target = Identifiers(project, request.ProcessId).Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected entry was not found.");
        if (target is PlcSystemConstant)
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "System constants are left unchanged.");
        try
        {
            validate();
            switch (target)
            {
                case PlcTag tag: tag.Delete(); break;
                case PlcUserConstant constant: constant.Delete(); break;
                default: throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a probe-created tag or user constant.");
            }
            session.Forget(request.ObjectId!);
            result.Created.Add(new WriteProbeObject { ObjectId = request.ObjectId, Kind = "deleted" });
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate();
            var parent = session.ParentOf(request.ObjectId!);
            if (parent != null) session.NoteTypedFailure(parent);
            result.Errors.Add(Error(ex, "deleteTag"));
        }
    }

    private static void ImportTable(Project project, WriteProbeRequest request, WriteProbeSession session, WriteProbeResult result, Action validate)
    {
        var table = Table(project, request);
        string? folder = null;
        try
        {
            folder = WriteProbeFiles.CreateDirectory();
            var file = new FileInfo(Path.Combine(folder, "table.xml"));
            validate();
            table.Export(file, ExportOptions.WithReadOnly);
            validate();
            if (table.Parent is not PlcTagTableGroup parent)
                throw new InvalidOperationException("The tag table has no parent group.");
            result.Format = "simatic-ml";
            var imported = parent.TagTables.Import(file, ImportOptions.Override);
            Remember(result, session, Identifiers(project, request.ProcessId), imported, "tagTable", request.NewName, null, validate);
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate();
            result.Errors.Add(Error(ex, "importTagTable"));
        }
        finally { WriteProbeFiles.Finish(result, folder); }
    }

    private static IList<IEngineeringObject> Generate(PlcExternalSource source, IEngineeringObject destination, bool root, int processId)
    {
        if (!root && destination is PlcBlockUserGroup blocks)
            return source.GenerateBlocksFromSource(blocks, GenerateBlockOption.None);
        if (!root && destination is PlcTypeUserGroup types)
            return source.GenerateBlocksFromSource(types, GenerateBlockOption.None);
        // Root is the parameterless overload. A second BlockGroup fetch is a different Openness wrapper, so identity comparison cannot detect it.
        if (root || destination is PlcBlockSystemGroup or PlcTypeSystemGroup)
            return source.GenerateBlocksFromSource(GenerateBlockOption.None);
        throw new ConnectionFault("unsupportedObject", processId, "External-source generation can target the root group or a user group.");
    }

    private static IEngineeringObject Destination(ObjectIdentifierProvider identifiers, IEngineeringObject scope, WriteProbeRequest request, bool udt, Action validate)
    {
        if (string.IsNullOrWhiteSpace(request.GroupObjectId) && string.IsNullOrWhiteSpace(request.GroupPath))
            return udt ? TypeRoot(scope) : BlockRoot(scope);
        validate();
        var root = udt ? (IEngineeringObject)TypeRoot(scope) : BlockRoot(scope);
        var chosen = !string.IsNullOrWhiteSpace(request.GroupObjectId)
            ? identifiers.Find(request.GroupObjectId) ?? throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected group was not found.")
            : GroupByPath(root, request.GroupPath!, request.ProcessId);
        if (!string.IsNullOrWhiteSpace(request.GroupObjectId) && !InScope(scope, chosen, identifiers, validate))
            throw new ConnectionFault("invalidRequest", request.ProcessId, "The group is outside the source object's PLC or unit scope.");
        if (udt && chosen is PlcTypeGroup) return chosen;
        if (!udt && chosen is PlcBlockGroup) return chosen;
        throw new ConnectionFault("unsupportedObject", request.ProcessId, udt
            ? "groupObjectId must identify a PLC data-type group."
            : "groupObjectId must identify a block group.");
    }

    private static IEngineeringObject GroupByPath(IEngineeringObject root, string path, int processId)
    {
        var names = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var rootName = GroupName(root);
        var start = 0;
        if (rootName != null)
        {
            var index = Array.FindIndex(names, name => string.Equals(name, rootName, StringComparison.Ordinal));
            if (index >= 0) start = index + 1;
        }
        var current = root;
        for (var i = start; i < names.Length; i++)
        {
            current = NextGroup(current, names[i]) ??
                throw new ConnectionFault("objectNotFound", processId, "The selected group was not found.");
        }
        return current;
    }

    private static string? GroupName(IEngineeringObject group) => group switch
    {
        PlcBlockGroup blocks => blocks.Name,
        PlcTypeGroup types => types.Name,
        PlcTagTableGroup tables => tables.Name,
        _ => null
    };

    private static IEngineeringObject? NextGroup(IEngineeringObject group, string name) => group switch
    {
        PlcBlockGroup blocks => blocks.Groups.Find(name),
        PlcTypeGroup types => types.Groups.Find(name),
        PlcTagTableGroup tables => tables.Groups.Find(name),
        _ => null
    };

    private static bool InScope(IEngineeringObject scope, IEngineeringObject candidate, ObjectIdentifierProvider identifiers, Action validate)
    {
        var scopeId = DiscoveryValues.Nonblank(identifiers.GetIdentifier(scope));
        IEngineeringObject? current = candidate;
        while (current != null)
        {
            validate();
            if (ReferenceEquals(current, scope)) return true;
            var candidateId = DiscoveryValues.Nonblank(identifiers.GetIdentifier(current));
            if (scopeId != null && candidateId != null && string.Equals(candidateId, scopeId, StringComparison.Ordinal))
                return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool NameTaken(IEngineeringObject destination, string name) => destination switch
    {
        PlcBlockGroup blocks => blocks.Blocks.Find(name) != null,
        PlcTypeGroup types => types.Types.Find(name) != null,
        _ => false
    };

    private static bool Accepted(WriteProbeResult result, DocumentResultState state, IEnumerable<string> messages)
    {
        var native = messages.Where(message => !string.IsNullOrWhiteSpace(message)).ToList();
        result.NativeState = state.ToString();
        result.NativeMessages = native;
        if (state == DocumentResultState.Success) return true;
        foreach (var message in native)
            result.Errors.Add(new DiscoveryError { Origin = "tia-openness", Operation = "importDocuments", Format = "simatic-sd", Message = message });
        result.Errors.Add(new DiscoveryError
        {
            Origin = "bridge", Operation = "importDocuments", Format = "simatic-sd",
            Message = "SIMATIC SD returned " + state + "; a complete successful import is required."
        });
        return false;
    }

    private static List<FileInfo> WriteDocuments(string folder, IReadOnlyList<string> names, IReadOnlyList<string> contents)
    {
        if (names.Count != contents.Count || names.Count == 0)
            throw new InvalidOperationException("The export returned no usable source documents.");
        var files = new List<FileInfo>();
        for (var index = 0; index < names.Count; index++)
        {
            var fileName = Path.GetFileName(names[index]);
            if (!string.Equals(fileName, names[index], StringComparison.Ordinal) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("Export returned an unexpected document name.");
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, contents[index], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            files.Add(new FileInfo(path));
        }
        return files;
    }

    private static void Remember(WriteProbeResult result, WriteProbeSession session, ObjectIdentifierProvider identifiers,
        IEnumerable<IEngineeringObject> objects, string kind, string? name, string? parentId, Action validate)
    {
        foreach (var engineering in objects)
        {
            validate();
            var id = DiscoveryValues.Nonblank(identifiers.GetIdentifier(engineering));
            if (id == null)
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "identifier", Message = "The written object has no native identifier." });
            else session.Remember(id, kind, parentId);
            result.Created.Add(new WriteProbeObject { ObjectId = id, Kind = kind, Name = name });
        }
    }

    private static void Cleanup(WriteProbeResult result, PlcExternalSource? external, string? folder, Action validate)
    {
        ConnectionFault? lost = null;
        try
        {
            if (external != null)
            {
                validate();
                external.Delete();
            }
        }
        catch (ConnectionFault ex) { lost = ex; }
        catch (Exception ex)
        {
            try { validate(); }
            catch (ConnectionFault context) { lost = context; }
            if (lost == null)
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "temporaryCleanup", Message = ex.Message });
        }
        WriteProbeFiles.Finish(result, folder);
        if (lost != null) throw lost;
    }

    private static PlcTagTable Table(Project project, WriteProbeRequest request)
    {
        var target = Identifiers(project, request.ProcessId).Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected tag table was not found.");
        return target as PlcTagTable ??
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a tag table.");
    }

    private static PlcTagTableGroup TagGroup(Project project, PlcSoftware plc, WriteProbeRequest request, Action validate)
    {
        if (string.IsNullOrWhiteSpace(request.GroupObjectId) && string.IsNullOrWhiteSpace(request.GroupPath))
            return plc.TagTableGroup;
        validate();
        var identifiers = Identifiers(project, request.ProcessId);
        var chosen = !string.IsNullOrWhiteSpace(request.GroupObjectId)
            ? identifiers.Find(request.GroupObjectId) ?? throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected group was not found.")
            : GroupByPath(plc.TagTableGroup, request.GroupPath!, request.ProcessId);
        if (chosen is not PlcTagTableGroup group)
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "The selected group is not a tag-table group.");
        if (!string.IsNullOrWhiteSpace(request.GroupObjectId) && !InScope(plc, group, identifiers, validate))
            throw new ConnectionFault("invalidRequest", request.ProcessId, "The group is outside the selected CPU.");
        return group;
    }

    private static PlcSoftware Cpu(Project project, string plcObjectId, int processId)
    {
        var target = Identifiers(project, processId).Find(plcObjectId) ??
            throw new ConnectionFault("objectNotFound", processId, "The selected PLC object was not found.");
        if (target is not DeviceItem cpu || cpu.GetService<SoftwareContainer>()?.Software is not PlcSoftware plc)
            throw new ConnectionFault("unsupportedObject", processId, "plcObjectId must identify the CPU DeviceItem owning PlcSoftware.");
        return plc;
    }

    private static IEngineeringObject ScopeOf(IEngineeringObject start, Action validate, int processId)
    {
        IEngineeringObject? current = start;
        while (current != null)
        {
            validate();
            if (current is PlcUnitBase or PlcSoftware) return current;
            current = current.Parent;
        }
        throw new ConnectionFault("unsupportedObject", processId, "The object is not inside PLC software or a unit.");
    }

    private static PlcBlockGroup BlockRoot(IEngineeringObject scope) => scope switch
    {
        PlcUnitBase unit => unit.BlockGroup,
        PlcSoftware software => software.BlockGroup,
        _ => throw new InvalidOperationException("The scope has no block group.")
    };

    private static PlcTypeGroup TypeRoot(IEngineeringObject scope) => scope switch
    {
        PlcUnitBase unit => unit.TypeGroup,
        PlcSoftware software => software.TypeGroup,
        _ => throw new InvalidOperationException("The scope has no type group.")
    };

    private static PlcExternalSourceSystemGroup Sources(IEngineeringObject scope) => scope switch
    {
        PlcUnitBase unit => unit.ExternalSourceGroup,
        PlcSoftware software => software.ExternalSourceGroup,
        _ => throw new InvalidOperationException("The scope has no external-source group.")
    };

    private static Dictionary<string, object?> Attributes(IEngineeringObject target, Action validate)
    {
        validate();
        var native = target switch
        {
            PlcBlock block => block.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite),
            PlcType type => type.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite),
            _ => throw new InvalidOperationException("The object has no bulk attributes.")
        };
        var attributes = new Dictionary<string, object?>();
        foreach (var pair in native) attributes[pair.Key] = DiscoveryValues.Convert(pair.Value);
        return attributes;
    }

    private static ObjectIdentifierProvider Identifiers(Project project, int processId) =>
        project.GetService<ObjectIdentifierProvider>() ??
        throw new ConnectionFault("unsupportedObject", processId, "The project does not expose ObjectIdentifierProvider.");

    private static DiscoveryError Error(Exception ex, string operation) => new()
    {
        Origin = ex is EngineeringException ? "tia-openness" : "bridge",
        Operation = operation,
        Message = ex.Message
    };
}
