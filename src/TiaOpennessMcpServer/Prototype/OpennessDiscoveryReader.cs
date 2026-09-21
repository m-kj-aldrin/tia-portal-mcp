using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaOpennessMcpServer.Prototype;

// Called exclusively inside ConnectionRegistry's STA guard, using its retained primary project.
// All return values are managed DTOs; no engineering proxy escapes this reader.
internal sealed class OpennessDiscoveryReader
{
    private readonly Project _project;
    private readonly ObjectIdentifierProvider? _identifiers;
    private readonly DiscoveryReadContext _read;
    private readonly Action _validate;
    private readonly int _processId;

    public OpennessDiscoveryReader(Project project, DiscoveryResult result, int processId, Action validate)
    {
        _project = project;
        _processId = processId;
        _validate = validate;
        _read = new DiscoveryReadContext(result.Errors, validate);
        _identifiers = _read.Read(() => project.GetService<ObjectIdentifierProvider>(), "identifierProvider", null);
        if (_identifiers == null && result.Errors.Count == 0)
            result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "identifierProvider",
                Message = "The project does not expose ObjectIdentifierProvider." });
    }

    public void ListDevices(DeviceInventory result)
    {
        Add(result.Roots, _read.Collect(() => _project.Devices, device => DeviceLeaf(device, ""), ""));
        Add(result.Roots, _read.Collect(() => _project.DeviceGroups, group => Group(group, ""), ""));
        var system = _read.Read(() => _project.UngroupedDevicesGroup, "systemGroup", "");
        if (system != null) result.Roots.Add(Group(system, ""));
    }

    private Dictionary<string, object?> Group(DeviceGroup group, string? parent)
    {
        var name = _read.Read(() => group.Name, "name", parent);
        var path = Join(parent, name);
        var children = new List<Dictionary<string, object?>>();
        var node = new Dictionary<string, object?>
        {
            ["kind"] = "deviceGroup", ["name"] = name, ["path"] = path,
            ["isSystem"] = group is DeviceSystemGroup, ["children"] = children
        };
        Add(children, _read.Collect(() => group.Devices, device => DeviceLeaf(device, path), path));
        if (group is DeviceUserGroup user)
            Add(children, _read.Collect(() => user.Groups, child => Group(child, path), path));
        return node;
    }

    private Dictionary<string, object?> DeviceLeaf(Device device, string? parent)
    {
        var name = _read.Read(() => device.Name, "name", parent);
        var path = Join(parent, name);
        return new Dictionary<string, object?>
        {
            ["kind"] = "device", ["objectId"] = Identifier(device, path), ["name"] = name,
            ["path"] = path, ["typeIdentifier"] = _read.Read(() => device.TypeIdentifier, "typeIdentifier", path),
            ["isGsd"] = _read.Read<bool?>(() => device.IsGsd, "isGsd", path)
        };
    }

    public void ReadDevice(DeviceRead result, string objectId, bool includePath)
    {
        if (_identifiers == null)
            throw new ConnectionFault("unsupportedObject", _processId, "The project does not expose ObjectIdentifierProvider.");
        // Direct lookup only, without enumerating siblings, devices or PLC software inventories.
        var target = _identifiers.Find(objectId);
        if (target == null) throw new ConnectionFault("objectNotFound", _processId, "The selected object was not found.");
        if (!(target is Device device))
            throw new ConnectionFault("unsupportedObject", _processId, "The selected object is not a Device.");
        var path = includePath ? PathOf(device) : null;
        var metadata = HardwareMetadata(device, path);
        metadata["isGsd"] = _read.Read<bool?>(() => device.IsGsd, "isGsd", path);
        result.Metadata = metadata;
        result.DeviceItems = Items(device, path);
    }

    private List<Dictionary<string, object?>>? Items(HardwareObject owner, string? parent) =>
        _read.Collect(() => owner.DeviceItems, item =>
        {
            var name = _read.Read(() => item.Name, "name", parent);
            var path = Join(parent, name);
            var node = HardwareMetadata(item, path);
            node["classification"] = _read.Read(() => item.Classification.ToString(), "classification", path);
            node["positionNumber"] = _read.Read<int?>(() => item.PositionNumber, "positionNumber", path);
            node["isBuiltIn"] = _read.Read<bool?>(() => item.IsBuiltIn, "isBuiltIn", path);
            node["isPlugged"] = _read.Read<bool?>(() => item.IsPlugged, "isPlugged", path);
            // The CPU DeviceItem owns the selector. Never substitute the PlcSoftware/rack/Device ID.
            node["plcObjectId"] = _read.Read(() => item.GetService<SoftwareContainer>()?.Software is PlcSoftware
                ? node["objectId"] : null, "plcSoftware", path);
            node["children"] = Items(item, path);
            return node;
        }, parent);

    private Dictionary<string, object?> HardwareMetadata(HardwareObject item, string? path)
    {
        var attrs = Attributes((IEngineeringObject)item, path);
        var result = new Dictionary<string, object?>
        {
            ["objectId"] = Identifier(item, path), ["path"] = path,
            ["name"] = _read.Read(() => item.Name, "name", path),
            ["typeIdentifier"] = _read.Read(() => item.TypeIdentifier, "typeIdentifier", path)
        };
        foreach (var name in new[] { "TypeName", "Author", "Comment", "IsGsd", "OrderNumber", "FirmwareVersion" })
        {
            if (attrs.TryGetValue(name, out var value))
            {
                result[char.ToLowerInvariant(name[0]) + name.Substring(1)] = value;
                attrs.Remove(name);
            }
        }
        foreach (var name in new[] { "Name", "TypeIdentifier", "Classification", "PositionNumber", "IsBuiltIn", "IsPlugged" })
            attrs.Remove(name);
        result["typeSpecific"] = attrs;
        return result;
    }

    private Dictionary<string, object?> Attributes(IEngineeringObject target, string? path)
    {
        var values = new Dictionary<string, object?>();
        var infos = _read.Read(() => target.GetAttributeInfos(), "attributeInfos", path);
        if (infos == null) return values;
        var names = infos.Where(x => (x.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
            .Select(x => x.Name).ToArray();
        if (names.Length == 0) return values;
        var bulk = _read.Read(() => target.GetAttributes(names), "attributes", path);
        if (bulk != null)
        {
            // The name-list overload returns positional values, unlike the access-options overload's pairs.
            for (var index = 0; index < names.Length; index++)
                values[names[index]] = _read.Read(() => DiscoveryValues.Convert(bulk[index]), "attribute:" + names[index], path);
        }
        else
        {
            // A single protected/unavailable attribute must not discard all readable metadata.
            foreach (var name in names)
                values[name] = _read.Read(() => DiscoveryValues.Convert(target.GetAttribute(name)), "attribute:" + name, path);
        }
        return values;
    }

    private string? Identifier(IEngineeringObject item, string? path) => _identifiers == null ? null :
        _read.Read(() => DiscoveryValues.Nonblank(_identifiers.GetIdentifier(item)), "identifier", path);

    private string? PathOf(Device device)
    {
        try
        {
            var names = new Stack<string>();
            IEngineeringObject? current = device;
            while (current != null && !(current is Project))
            {
                if (current is Device hardware) names.Push(hardware.Name);
                else if (current is DeviceGroup group) names.Push(group.Name);
                else return null;
                current = current.Parent;
            }
            return current == null ? null : string.Join("/", names);
        }
        catch (ConnectionFault) { throw; }
        catch { _validate(); return null; } // Optional path resolution has no separate error packet.
    }

    private static string? Join(string? parent, string? name) => parent == null || name == null ? null :
        parent.Length == 0 ? name : parent + "/" + name;

    private static void Add<T>(List<T> target, List<T>? values)
    {
        if (values != null) target.AddRange(values);
    }

    public static ProcessStatus ReadStatus(TiaPortal portal, object? retained, Action validate)
    {
        var result = new ProcessStatus { State = "connected" };
        var read = new DiscoveryReadContext(result.Errors, validate);
        var process = portal.GetCurrentProcess();
        var products = read.Collect(() => process.InstalledSoftware, product => Product(product, read), "installedProducts");
        var portalProduct = products?.FirstOrDefault(product =>
            string.Equals(product["name"] as string, "Totally Integrated Automation Portal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(product["name"] as string, "TIA Portal", StringComparison.OrdinalIgnoreCase));
        result.Tia = new Dictionary<string, object?>
        {
            ["mode"] = process.Mode == TiaPortalMode.WithUserInterface ? "with-ui" : "headless",
            ["portalVersion"] = portalProduct == null ? null : portalProduct["version"],
            ["installedProducts"] = products
        };
        if (retained is Project project)
            result.Project = new Dictionary<string, object?>
            {
                ["name"] = read.Read(() => project.Name, "projectName", null),
                ["path"] = read.Read(() => project.Path.FullName, "projectPath", null),
                ["version"] = read.Read(() => DiscoveryValues.Nonblank(project.Version), "projectVersion", null),
                ["isModified"] = read.Read<bool?>(() => project.IsModified, "projectModified", null)
            };
        return result;
    }

    private static Dictionary<string, object?> Product(TiaPortalProduct product, DiscoveryReadContext read) => new()
    {
        ["name"] = read.Read(() => product.Name, "productName", "installedProducts"),
        ["version"] = read.Read(() => DiscoveryValues.Nonblank(product.Version), "productVersion", "installedProducts"),
        ["options"] = read.Collect(() => product.Options, option => Product(option, read), "installedProducts/options")
        // Public V20 TiaPortalProduct exposes no ProductCode; do not manufacture one or an update field.
    };
}
