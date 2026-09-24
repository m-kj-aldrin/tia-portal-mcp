using TiaOpennessMcpServer.Operations;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

namespace TiaOpennessMcpServer.Openness;

// All native objects remain on the shared STA, under the original attachment ticket.
internal static class OpennessWrites
{
    public static WriteResult Run(Project project, WriteRequest request, Action validate)
    {
        var result = new WriteResult { Operation = request.Tool, Format = request.SourceFormat };
        try
        {
            switch (request.Tool)
            {
                case "write_blocks": case "write_udts": case "import_tag_tables":
                    Import(project, request, result, validate); break;
                case "create_tag_table":
                    var group = (PlcTagTableGroup)Destination(project, request, validate);
                    validate();
                    Remember(result, project, request, new[] { group.TagTables.Create(request.Name!) }, validate);
                    break;
                case "create_tag": case "create_user_constant":
                    var table = Table(project, request);
                    validate();
                    IEngineeringObject entry = request.Tool == "create_tag"
                        ? table.Tags.Create(request.Name!, request.DataType!, request.LogicalAddress!)
                        : table.UserConstants.Create(request.Name!, request.DataType!, request.Value!);
                    Remember(result, project, request, new[] { entry }, validate);
                    break;
                case "set_tag_entry_attribute": case "delete_tag_entry":
                    EditEntry(project, request, result, validate); break;
                case "delete_block": case "delete_udt": case "delete_tag_table":
                    DeleteObject(project, request, result, validate); break;
                default: throw new ConnectionFault("invalidRequest", request.ProcessId, "Unknown write tool.");
            }
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex) { validate(); result.Errors.Add(Error(ex, request.Tool)); }
        validate();
        try { result.ProjectModified = project.IsModified; }
        catch (Exception ex) { validate(); result.Errors.Add(Error(ex, "projectModified")); }
        return result;
    }

    private static void Import(Project project, WriteRequest request, WriteResult result, Action validate)
    {
        var destination = Destination(project, request, validate);
        var scope = ScopeOf(destination, validate, request.ProcessId);
        string? folder = null;
        PlcExternalSource? external = null;
        try
        {
            folder = WriteFiles.CreateDirectory();
            var files = WriteFiles.Stage(folder, request.Documents);
            validate();
            if (request.SourceFormat == "external-source")
            {
                external = Sources(scope).ExternalSources.CreateFromFile("mcp_" + Guid.NewGuid().ToString("N"), files[0].FullName);
                validate();
                var generated = Generate(external, destination, destination is PlcBlockSystemGroup or PlcTypeSystemGroup, request.ProcessId);
                Remember(result, project, request, generated, validate);
            }
            else if (request.SourceFormat == "simatic-sd")
            {
                var stem = Path.GetFileNameWithoutExtension(files.Single(file => file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase)).Name);
                if (destination is PlcTypeGroup types)
                {
                    var imported = types.Types.ImportFromDocuments(new DirectoryInfo(folder), stem, ImportDocumentOptions.Override);
                    Accepted(result, imported.State, imported.Messages.Select(item => item.Message));
                    Remember(result, project, request, imported.ImportedPlcTypes, validate);
                }
                else
                {
                    var imported = ((PlcBlockGroup)destination).Blocks.ImportFromDocuments(new DirectoryInfo(folder), stem, ImportDocumentOptions.Override);
                    Accepted(result, imported.State, imported.Messages.Select(item => item.Message));
                    Remember(result, project, request, imported.ImportedPlcBlocks, validate);
                }
            }
            else
            {
                IEnumerable<IEngineeringObject> imported = destination switch
                {
                    PlcTypeGroup types => types.Types.Import(files[0], ImportOptions.Override),
                    PlcBlockGroup blocks => blocks.Blocks.Import(files[0], ImportOptions.Override),
                    PlcTagTableGroup tables => tables.TagTables.Import(files[0], ImportOptions.Override),
                    _ => throw new InvalidOperationException("Unsupported import composition.")
                };
                Remember(result, project, request, imported, validate);
            }
            if (result.AffectedObjects.Count == 0 && result.Errors.Count == 0)
                result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = request.Tool, Message = "The native write returned no objects." });
        }
        finally { Cleanup(result, external, folder, validate); }
    }

    private static void EditEntry(Project project, WriteRequest request, WriteResult result, Action validate)
    {
        validate();
        var target = Identifiers(project, request.ProcessId).Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected tag or user constant was not found.");
        if (target is PlcSystemConstant || (target is not PlcTag && target is not PlcUserConstant))
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a tag or user constant. System constants are read-only.");
        var deleting = request.Tool == "delete_tag_entry";
        string? name = null;
        var parentId = DiscoveryValues.Nonblank(Identifiers(project, request.ProcessId).GetIdentifier(target.Parent));
        if (deleting) name = target is PlcTag tagName ? tagName.Name : ((PlcUserConstant)target).Name;
        validate();
        if (target is PlcTag tag)
        {
            if (deleting) tag.Delete();
            else tag.SetAttribute(request.AttributeName!, request.AttributeValue);
        }
        else
        {
            var constant = (PlcUserConstant)target;
            if (deleting) constant.Delete();
            else constant.SetAttribute(request.AttributeName!, request.AttributeValue);
        }
        validate();
        if (deleting) result.AffectedObjects.Add(new WriteObject { ObjectId = request.ObjectId, ParentObjectId = parentId, Kind = target is PlcTag ? "tag" : "userConstant", Name = name });
        else Remember(result, project, request, new[] { target }, validate);
    }

    private static void DeleteObject(Project project, WriteRequest request, WriteResult result, Action validate)
    {
        validate();
        var identifiers = Identifiers(project, request.ProcessId);
        var target = identifiers.Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected object was not found.");
        var item = new WriteObject { ObjectId = request.ObjectId };
        Action delete;
        switch (request.Tool)
        {
            case "delete_block" when target is PlcBlock block:
                item.Kind = "block";
                item.Name = block.Name;
                delete = block.Delete;
                break;
            case "delete_udt" when target is PlcType type:
                item.Kind = "udt";
                item.Name = type.Name;
                delete = type.Delete;
                break;
            case "delete_tag_table" when target is PlcTagTable table:
                item.Kind = "tagTable";
                item.Name = table.Name;
                delete = table.Delete;
                break;
            default:
                throw new ConnectionFault("unsupportedObject", request.ProcessId,
                    "objectId must identify the native object type required by " + request.Tool + ".");
        }
        item.ParentObjectId = DiscoveryValues.Nonblank(identifiers.GetIdentifier(target.Parent));
        validate();
        delete();
        validate();
        // A deleted native proxy is no longer readable. Return only the pre-delete snapshot.
        result.AffectedObjects.Add(item);
    }

    private static void Remember(WriteResult result, Project project, WriteRequest request,
        IEnumerable<IEngineeringObject> objects, Action validate)
    {
        var identifiers = Identifiers(project, request.ProcessId);
        foreach (var engineering in objects)
        {
            validate();
            var item = new WriteObject { Kind = engineering switch
                { PlcBlock => "block", PlcType => "udt", PlcTagTable => "tagTable", PlcTag => "tag", PlcUserConstant => "userConstant", _ => engineering.GetType().Name } };
            result.AffectedObjects.Add(item);
            try { item.ObjectId = DiscoveryValues.Nonblank(identifiers.GetIdentifier(engineering)); }
            catch (Exception ex) { validate(); result.Errors.Add(Error(ex, "identifier")); }
            if (engineering is PlcTag or PlcUserConstant)
                try { item.ParentObjectId = DiscoveryValues.Nonblank(identifiers.GetIdentifier(engineering.Parent)); }
                catch (Exception ex) { validate(); result.Errors.Add(Error(ex, "parentIdentifier")); }
            try { item.Name = engineering switch
                { PlcBlock b => b.Name, PlcType t => t.Name, PlcTagTable t => t.Name, PlcTag t => t.Name, PlcUserConstant c => c.Name, _ => null }; }
            catch (Exception ex) { validate(); result.Errors.Add(Error(ex, "name")); }
            validate();
        }
    }

    private static IEngineeringObject Destination(Project project, WriteRequest request, Action validate)
    {
        validate();
        var plc = Cpu(project, request.PlcObjectId!, request.ProcessId);
        var identifiers = Identifiers(project, request.ProcessId);
        IEngineeringObject Root(IEngineeringObject scope) => request.Tool switch
        {
            "write_blocks" => BlockRoot(scope), "write_udts" => TypeRoot(scope),
            _ => scope is PlcUnitBase unit ? unit.TagTableGroup : ((PlcSoftware)scope).TagTableGroup
        };
        var root = Root(plc);
        if (request.GroupObjectId == null && request.GroupPath == null) return root;
        IEngineeringObject chosen;
        if (request.GroupObjectId != null)
        {
            validate();
            chosen = identifiers.Find(request.GroupObjectId) ?? throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected destination group was not found.");
        }
        else
        {
            // Match the inventory's exact PLC[/unit]/group path, never a guessed suffix.
            var matches = new List<IEngineeringObject>();
            void Find(IEngineeringObject group, string parent)
            {
                validate();
                var name = GroupName(group);
                var path = name == null ? parent : parent + "/" + name;
                if (path == request.GroupPath) matches.Add(group);
                IEnumerable<IEngineeringObject> children = group switch
                {
                    PlcBlockGroup b => b.Groups, PlcTypeGroup t => t.Groups, PlcTagTableGroup t => t.Groups,
                    _ => Array.Empty<IEngineeringObject>()
                };
                foreach (var child in children) { validate(); Find(child, path); }
            }
            Find(root, plc.Name);
            validate();
            var units = plc.GetService<PlcUnitProvider>()?.UnitGroup;
            if (units != null)
            {
                foreach (var unit in units.Units) { validate(); Find(Root(unit), plc.Name + "/" + unit.Name); }
                foreach (var unit in units.SafetyUnits) { validate(); Find(Root(unit), plc.Name + "/" + unit.Name); }
            }
            if (matches.Count != 1) throw new ConnectionFault("objectNotFound", request.ProcessId, "The destination group path did not identify exactly one native group.");
            chosen = matches[0];
        }
        validate();
        if (!InScope(plc, chosen, identifiers, validate))
            throw new ConnectionFault("invalidRequest", request.ProcessId, "The destination group is outside the selected CPU.");
        if ((request.Tool == "write_blocks" && chosen is PlcBlockGroup) ||
            (request.Tool == "write_udts" && chosen is PlcTypeGroup) ||
            (request.Tool is "create_tag_table" or "import_tag_tables" && chosen is PlcTagTableGroup)) return chosen;
        throw new ConnectionFault("unsupportedObject", request.ProcessId, "Select a destination group of the matching native type.");
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

    private static string? GroupName(IEngineeringObject group) => group switch
    {
        PlcBlockGroup blocks => blocks.Name,
        PlcTypeGroup types => types.Name,
        PlcTagTableGroup tables => tables.Name,
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

    private static bool Accepted(WriteResult result, DocumentResultState state, IEnumerable<string> messages)
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

    private static void Cleanup(WriteResult result, PlcExternalSource? external, string? folder, Action validate)
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
                result.Errors.Add(Error(ex, "temporaryCleanup"));
        }
        WriteFiles.Finish(result, folder);
        if (lost != null) throw lost;
    }

    private static PlcTagTable Table(Project project, WriteRequest request)
    {
        var target = Identifiers(project, request.ProcessId).Find(request.ObjectId!) ??
            throw new ConnectionFault("objectNotFound", request.ProcessId, "The selected tag table was not found.");
        return target as PlcTagTable ??
            throw new ConnectionFault("unsupportedObject", request.ProcessId, "objectId must identify a tag table.");
    }

    private static PlcSoftware Cpu(Project project, string plcObjectId, int processId) =>
        OpennessPlc.Resolve(project, processId, plcObjectId).Software;

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
