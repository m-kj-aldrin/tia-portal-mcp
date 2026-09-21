using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Short-lived handles used only inside a StaTaskScheduler callback. These
/// objects must never be cached or returned through a transport response.
/// </summary>
internal sealed class V1DeviceHandle
{
    public required Device Device { get; init; }
    public required string Path { get; init; }
    public List<V1PlcHandle> Plcs { get; } = new();
}

internal sealed class V1PlcHandle
{
    public required Device Device { get; init; }
    public required DeviceItem DeviceItem { get; init; }
    public required PlcSoftware Software { get; init; }
    public required string DevicePath { get; init; }
    public required string Path { get; init; }
}

internal sealed class V1SoftwareScopeHandle
{
    public required IEngineeringObject EngineeringObject { get; init; }
    public required PlcBlockSystemGroup BlockGroup { get; init; }
    public required PlcTypeSystemGroup TypeGroup { get; init; }
    public required PlcTagTableSystemGroup TagTableGroup { get; init; }
    public required PlcExternalSourceSystemGroup ExternalSourceGroup { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsSafety { get; init; }
    public required bool IsSystemGenerated { get; init; }
}

internal sealed class V1ObjectHandle
{
    public required IEngineeringObject EngineeringObject { get; init; }
    public required V1SoftwareScopeHandle Scope { get; init; }
    public string? ObjectId { get; init; }
    public string? ParentObjectId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public string? Language { get; init; }
    public int? Number { get; init; }
    public bool? IsProtected { get; init; }
    public bool? IsSafety { get; init; }
    public bool? IsSystemGenerated { get; init; }
    public bool? IsConsistent { get; init; }
    public bool? ContentAvailable { get; init; }
    public string? ContentLimitation { get; init; }
}

internal sealed class V1HierarchyHandle
{
    public string? ObjectId { get; init; }
    public string? ParentObjectId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public required bool IsGroup { get; init; }
    public bool? IsSystemGroup { get; init; }
    public bool? IsSafety { get; init; }
    public bool? IsSystemGenerated { get; init; }
    public V1ObjectHandle? Object { get; init; }
    public List<V1HierarchyHandle> Children { get; } = new();
}

internal sealed class V1InventoryHandle
{
    public required V1PlcHandle Plc { get; init; }
    public required string? PlcObjectId { get; init; }
    public List<V1HierarchyHandle> Roots { get; } = new();
    public List<V1ObjectHandle> Objects { get; } = new();
}

internal static class V1TiaHelpers
{
    public static IReadOnlyList<V1DeviceHandle> EnumerateDevices(Project project)
    {
        var result = new List<V1DeviceHandle>();
        var seen = new HashSet<Device>();

        foreach (Device device in project.Devices)
            AddDevice(device, device.Name, result, seen);

        try
        {
            foreach (Device device in project.UngroupedDevicesGroup.Devices)
                AddDevice(device, device.Name, result, seen);
        }
        catch
        {
            // Some project types do not expose an ungrouped device group.
        }

        foreach (DeviceUserGroup group in project.DeviceGroups)
            AddDeviceGroup(group, group.Name, result, seen);

        return result;
    }

    public static IReadOnlyList<V1PlcHandle> EnumeratePlcs(Project project) =>
        EnumerateDevices(project).SelectMany(d => d.Plcs).ToList();

    public static IReadOnlyList<V1SoftwareScopeHandle> EnumerateSoftwareScopes(
        V1PlcHandle plc)
    {
        var result = new List<V1SoftwareScopeHandle>
        {
            new()
            {
                EngineeringObject = plc.Software,
                BlockGroup = plc.Software.BlockGroup,
                TypeGroup = plc.Software.TypeGroup,
                TagTableGroup = plc.Software.TagTableGroup,
                ExternalSourceGroup = plc.Software.ExternalSourceGroup,
                Name = plc.Software.Name,
                Path = plc.Path,
                IsSafety = false,
                IsSystemGenerated = false,
            }
        };

        var provider = plc.Software.GetService<PlcUnitProvider>();

        if (provider is null)
            return result;

        foreach (PlcUnit unit in provider.UnitGroup.Units)
        {
            result.Add(new V1SoftwareScopeHandle
            {
                EngineeringObject = unit,
                BlockGroup = unit.BlockGroup,
                TypeGroup = unit.TypeGroup,
                TagTableGroup = unit.TagTableGroup,
                ExternalSourceGroup = unit.ExternalSourceGroup,
                Name = unit.Name,
                Path = CombinePath(plc.Path, unit.Name),
                IsSafety = false,
                IsSystemGenerated = false,
            });
        }

        foreach (PlcSafetyUnit unit in provider.UnitGroup.SafetyUnits)
        {
            result.Add(new V1SoftwareScopeHandle
            {
                EngineeringObject = unit,
                BlockGroup = unit.BlockGroup,
                TypeGroup = unit.TypeGroup,
                TagTableGroup = unit.TagTableGroup,
                ExternalSourceGroup = unit.ExternalSourceGroup,
                Name = unit.Name,
                Path = CombinePath(plc.Path, unit.Name),
                IsSafety = true,
                IsSystemGenerated = true,
            });
        }

        return result;
    }

    public static V1InventoryHandle BuildInventory(
        ObjectIdentifierProvider identifierProvider,
        V1PlcHandle plc)
    {
        var plcObjectId = TryGetIdentifier(identifierProvider, plc.DeviceItem);
        var inventory = new V1InventoryHandle
        {
            Plc = plc,
            PlcObjectId = plcObjectId,
        };

        foreach (var scope in EnumerateSoftwareScopes(plc))
        {
            var scopeId = TryGetIdentifier(identifierProvider, scope.EngineeringObject);
            var scopeNode = NewGroup(
                scopeId,
                plcObjectId,
                scope.Name,
                scope.Path,
                scope.EngineeringObject is PlcSoftware
                    ? "plc-software"
                    : scope.IsSafety
                        ? "safety-software-unit"
                        : "software-unit",
                isSystemGroup: false,
                isSafety: scope.IsSafety,
                isSystemGenerated: scope.IsSystemGenerated);

            var blockRoot = NewGroup(
                TryGetIdentifier(identifierProvider, scope.BlockGroup),
                scopeId,
                scope.BlockGroup.Name,
                CombinePath(scope.Path, scope.BlockGroup.Name),
                "block-group",
                isSystemGroup: true,
                isSafety: scope.IsSafety,
                isSystemGenerated: true);
            AddBlockGroup(
                scope.BlockGroup,
                blockRoot,
                scope,
                identifierProvider,
                plcObjectId,
                inventory,
                isSystemGroup: false);
            scopeNode.Children.Add(blockRoot);

            var typeRoot = NewGroup(
                TryGetIdentifier(identifierProvider, scope.TypeGroup),
                scopeId,
                scope.TypeGroup.Name,
                CombinePath(scope.Path, scope.TypeGroup.Name),
                "type-group",
                isSystemGroup: true,
                isSafety: scope.IsSafety,
                isSystemGenerated: true);
            AddTypeGroup(
                scope.TypeGroup,
                typeRoot,
                scope,
                identifierProvider,
                plcObjectId,
                inventory,
                isSystemGroup: false);
            scopeNode.Children.Add(typeRoot);

            var tagRoot = NewGroup(
                TryGetIdentifier(identifierProvider, scope.TagTableGroup),
                scopeId,
                scope.TagTableGroup.Name,
                CombinePath(scope.Path, scope.TagTableGroup.Name),
                "tag-table-group",
                isSystemGroup: true,
                isSafety: scope.IsSafety,
                isSystemGenerated: true);
            AddTagTableGroup(
                scope.TagTableGroup,
                tagRoot,
                scope,
                identifierProvider,
                plcObjectId,
                inventory,
                isSystemGroup: false);
            scopeNode.Children.Add(tagRoot);

            inventory.Roots.Add(scopeNode);
        }

        return inventory;
    }

    public static string? TryGetIdentifier(
        ObjectIdentifierProvider provider,
        IEngineeringObject engineeringObject)
    {
        try
        {
            var value = provider.GetIdentifier(engineeringObject);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static string ReadSelectedText(Project project, MultilingualText text)
    {
        try
        {
            var items = text.Items.Cast<MultilingualTextItem>().ToList();
            var editingCulture = project.LanguageSettings.EditingLanguage?.Culture?.Name;
            if (!string.IsNullOrWhiteSpace(editingCulture))
            {
                var selected = items.FirstOrDefault(item =>
                    string.Equals(
                        item.Language?.Culture?.Name,
                        editingCulture,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(item.Text));
                if (selected is not null)
                    return selected.Text;
            }

            return items.FirstOrDefault(item => !string.IsNullOrEmpty(item.Text))?.Text ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string ReadSelectedText(
        Project project,
        Func<MultilingualText> textReader)
    {
        try { return ReadSelectedText(project, textReader()); }
        catch { return ""; }
    }

    public static string? EditingLanguage(Project project)
    {
        try { return project.LanguageSettings.EditingLanguage?.Culture?.Name; }
        catch { return null; }
    }

    public static T? Try<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }

    public static string SingleLine(string? value, string? pathToRedact = null)
    {
        var result = (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (!string.IsNullOrEmpty(pathToRedact))
            result = result.Replace(pathToRedact, "<temporary directory>");

        return result;
    }

    private static V1HierarchyHandle NewGroup(
        string? objectId,
        string? parentObjectId,
        string name,
        string path,
        string type,
        bool isSystemGroup,
        bool? isSafety = null,
        bool? isSystemGenerated = null) => new()
    {
        ObjectId = objectId,
        ParentObjectId = parentObjectId,
        Name = name,
        Path = path,
        Type = type,
        IsGroup = true,
        IsSystemGroup = isSystemGroup,
        IsSafety = isSafety,
        IsSystemGenerated = isSystemGenerated,
    };

    private static void AddBlockGroup(
        PlcBlockGroup group,
        V1HierarchyHandle node,
        V1SoftwareScopeHandle scope,
        ObjectIdentifierProvider provider,
        string? plcObjectId,
        V1InventoryHandle inventory,
        bool isSystemGroup)
    {
        foreach (PlcBlock block in group.Blocks)
            AddBlock(block, node, scope, provider, plcObjectId, inventory, isSystemGroup);

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            var childNode = NewGroup(
                TryGetIdentifier(provider, child),
                node.ObjectId,
                child.Name,
                CombinePath(node.Path, child.Name),
                "block-group",
                isSystemGroup: false,
                isSafety: scope.IsSafety,
                isSystemGenerated: false);
            AddBlockGroup(child, childNode, scope, provider, plcObjectId, inventory, false);
            node.Children.Add(childNode);
        }

        if (group is PlcBlockSystemGroup systemRoot)
        {
            foreach (PlcSystemBlockGroup systemChild in systemRoot.SystemBlockGroups)
            {
                var childNode = NewGroup(
                    TryGetIdentifier(provider, systemChild),
                    node.ObjectId,
                    systemChild.Name,
                    CombinePath(node.Path, systemChild.Name),
                    "system-block-group",
                    isSystemGroup: true,
                    isSafety: scope.IsSafety,
                    isSystemGenerated: true);
                AddSystemBlockGroup(
                    systemChild,
                    childNode,
                    scope,
                    provider,
                    plcObjectId,
                    inventory);
                node.Children.Add(childNode);
            }
        }
    }

    private static void AddSystemBlockGroup(
        PlcSystemBlockGroup group,
        V1HierarchyHandle node,
        V1SoftwareScopeHandle scope,
        ObjectIdentifierProvider provider,
        string? plcObjectId,
        V1InventoryHandle inventory)
    {
        foreach (PlcBlock block in group.Blocks)
            AddBlock(block, node, scope, provider, plcObjectId, inventory, true);

        foreach (PlcSystemBlockGroup child in group.Groups)
        {
            var childNode = NewGroup(
                TryGetIdentifier(provider, child),
                node.ObjectId,
                child.Name,
                CombinePath(node.Path, child.Name),
                "system-block-group",
                isSystemGroup: true,
                isSafety: scope.IsSafety,
                isSystemGenerated: true);
            AddSystemBlockGroup(child, childNode, scope, provider, plcObjectId, inventory);
            node.Children.Add(childNode);
        }
    }

    private static void AddBlock(
        PlcBlock block,
        V1HierarchyHandle parent,
        V1SoftwareScopeHandle scope,
        ObjectIdentifierProvider provider,
        string? plcObjectId,
        V1InventoryHandle inventory,
        bool isSystemGroup)
    {
        var name = Try(() => block.Name) ?? $"<unreadable-{block.GetType().Name}>";
        var isProtected = Try<bool?>(() => block.IsKnowHowProtected);
        var handle = new V1ObjectHandle
        {
            EngineeringObject = block,
            Scope = scope,
            ObjectId = TryGetIdentifier(provider, block),
            ParentObjectId = parent.ObjectId,
            Name = name,
            Path = CombinePath(parent.Path, name),
            Type = BlockTypeName(block),
            Language = Try(() => block.ProgrammingLanguage.ToString()),
            Number = Try<int?>(() => block.Number),
            IsProtected = isProtected,
            IsSafety = scope.IsSafety,
            IsSystemGenerated = isSystemGroup,
            IsConsistent = Try<bool?>(() => block.IsConsistent),
            ContentAvailable = null,
            ContentLimitation = isProtected == true
                ? V1ProtectionPolicy.ProtectedContentLimitation
                : "Native content availability is determined only by an object-level read attempt.",
        };
        AddObjectNode(parent, handle, inventory);
    }

    private static void AddTypeGroup(
        PlcTypeGroup group,
        V1HierarchyHandle node,
        V1SoftwareScopeHandle scope,
        ObjectIdentifierProvider provider,
        string? plcObjectId,
        V1InventoryHandle inventory,
        bool isSystemGroup)
    {
        foreach (PlcType type in group.Types)
            AddType(type, node, scope, provider, plcObjectId, inventory, isSystemGroup);

        foreach (PlcTypeUserGroup child in group.Groups)
        {
            var childNode = NewGroup(
                TryGetIdentifier(provider, child),
                node.ObjectId,
                child.Name,
                CombinePath(node.Path, child.Name),
                "type-group",
                isSystemGroup: false,
                isSafety: scope.IsSafety,
                isSystemGenerated: false);
            AddTypeGroup(child, childNode, scope, provider, plcObjectId, inventory, false);
            node.Children.Add(childNode);
        }

        if (group is PlcTypeSystemGroup systemRoot)
        {
            foreach (PlcSystemTypeGroup systemChild in systemRoot.SystemTypeGroups)
            {
                var childNode = NewGroup(
                    TryGetIdentifier(provider, systemChild),
                    node.ObjectId,
                    systemChild.Name,
                    CombinePath(node.Path, systemChild.Name),
                    "system-type-group",
                    isSystemGroup: true,
                    isSafety: scope.IsSafety,
                    isSystemGenerated: true);
                foreach (PlcType type in systemChild.Types)
                    AddType(type, childNode, scope, provider, plcObjectId, inventory, true);
                node.Children.Add(childNode);
            }
        }
    }

    private static void AddType(
        PlcType type,
        V1HierarchyHandle parent,
        V1SoftwareScopeHandle scope,
        ObjectIdentifierProvider provider,
        string? plcObjectId,
        V1InventoryHandle inventory,
        bool isSystemGroup)
    {
        var name = Try(() => type.Name) ?? $"<unreadable-{type.GetType().Name}>";
        var isProtected = Try<bool?>(() => type.IsKnowHowProtected);
        var handle = new V1ObjectHandle
        {
            EngineeringObject = type,
            Scope = scope,
            ObjectId = TryGetIdentifier(provider, type),
            ParentObjectId = parent.ObjectId,
            Name = name,
            Path = CombinePath(parent.Path, name),
            Type = V1ObjectTypes.PlcDataType,
            IsProtected = isProtected,
            IsSafety = scope.IsSafety,
            IsSystemGenerated = isSystemGroup,
            IsConsistent = Try<bool?>(() => type.IsConsistent),
            ContentAvailable = null,
            ContentLimitation = isProtected == true
                ? V1ProtectionPolicy.ProtectedContentLimitation
                : "Native content availability is determined only by an object-level read attempt.",
        };
        AddObjectNode(parent, handle, inventory);
    }

    private static void AddTagTableGroup(
        PlcTagTableGroup group,
        V1HierarchyHandle node,
        V1SoftwareScopeHandle scope,
        ObjectIdentifierProvider provider,
        string? plcObjectId,
        V1InventoryHandle inventory,
        bool isSystemGroup)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            var name = Try(() => table.Name) ?? "<unreadable-tag-table>";
            var handle = new V1ObjectHandle
            {
                EngineeringObject = table,
                Scope = scope,
                ObjectId = TryGetIdentifier(provider, table),
                ParentObjectId = node.ObjectId,
                Name = name,
                Path = CombinePath(node.Path, name),
                Type = V1ObjectTypes.PlcTagTable,
                IsSafety = scope.IsSafety,
                IsSystemGenerated = isSystemGroup,
                ContentAvailable = null,
                ContentLimitation =
                    "Native SimaticML availability is determined only by an object-level read attempt.",
            };
            AddObjectNode(node, handle, inventory);
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            var childNode = NewGroup(
                TryGetIdentifier(provider, child),
                node.ObjectId,
                child.Name,
                CombinePath(node.Path, child.Name),
                "tag-table-group",
                isSystemGroup: false,
                isSafety: scope.IsSafety,
                isSystemGenerated: false);
            AddTagTableGroup(child, childNode, scope, provider, plcObjectId, inventory, false);
            node.Children.Add(childNode);
        }
    }

    private static void AddObjectNode(
        V1HierarchyHandle parent,
        V1ObjectHandle handle,
        V1InventoryHandle inventory)
    {
        inventory.Objects.Add(handle);
        parent.Children.Add(new V1HierarchyHandle
        {
            ObjectId = handle.ObjectId,
            ParentObjectId = handle.ParentObjectId,
            Name = handle.Name,
            Path = handle.Path,
            Type = handle.Type,
            IsGroup = false,
            Object = handle,
        });
    }

    private static string BlockTypeName(PlcBlock block) => block switch
    {
        OB => V1ObjectTypes.OrganizationBlock,
        FB => V1ObjectTypes.FunctionBlock,
        FC => V1ObjectTypes.Function,
        GlobalDB => "global-db",
        InstanceDB => "instance-db",
        ArrayDB => "array-db",
        _ => V1ObjectTypes.DataBlock,
    };

    private static void AddDeviceGroup(
        DeviceUserGroup group,
        string parentPath,
        List<V1DeviceHandle> result,
        HashSet<Device> seen)
    {
        foreach (Device device in group.Devices)
            AddDevice(device, CombinePath(parentPath, device.Name), result, seen);

        foreach (DeviceUserGroup child in group.Groups)
            AddDeviceGroup(child, CombinePath(parentPath, child.Name), result, seen);
    }

    private static void AddDevice(
        Device device,
        string path,
        List<V1DeviceHandle> result,
        HashSet<Device> seen)
    {
        if (!seen.Add(device))
            return;

        var handle = new V1DeviceHandle { Device = device, Path = path };
        foreach (DeviceItem item in device.DeviceItems)
            AddPlcsFromItem(
                device,
                item,
                path,
                CombinePath(path, item.Name),
                handle.Plcs);
        result.Add(handle);
    }

    private static void AddPlcsFromItem(
        Device device,
        DeviceItem item,
        string devicePath,
        string itemPath,
        List<V1PlcHandle> result)
    {
        var software = item.GetService<SoftwareContainer>()?.Software as PlcSoftware;

        if (software is not null)
        {
            result.Add(new V1PlcHandle
            {
                Device = device,
                DeviceItem = item,
                Software = software,
                DevicePath = devicePath,
                // The CPU DeviceItem is the PLC identity boundary. Keep its full
                // hardware path instead of deriving identity from PlcSoftware.
                Path = itemPath,
            });
        }

        foreach (DeviceItem child in item.DeviceItems)
            AddPlcsFromItem(
                device,
                child,
                devicePath,
                CombinePath(itemPath, child.Name),
                result);
    }

    public static string CombinePath(string parent, string child) =>
        string.IsNullOrWhiteSpace(parent) ? child : parent + "/" + child;
}
