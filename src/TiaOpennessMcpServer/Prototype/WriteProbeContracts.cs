using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Prototype;

// Dashboard-only disposable-project probes. These types contain no Siemens objects.
internal sealed class WriteProbeRequest
{
    public string Action { get; private set; } = "";
    public int ProcessId { get; private set; }
    public string? ObjectId { get; private set; }
    public string? PlcObjectId { get; private set; }
    public string? GroupObjectId { get; private set; }
    public string? GroupPath { get; private set; }
    public string? NewName { get; private set; }
    public string SourceFormat { get; private set; } = "best";
    public string? EntryKind { get; private set; }
    public string? DataType { get; private set; }
    public string? LogicalAddress { get; private set; }
    public string? Value { get; private set; }
    public string? AttributeName { get; private set; }
    public object? AttributeValue { get; private set; }
    public string? ProjectFileName { get; private set; }
    public bool ConfirmDisposable { get; private set; }

    public static WriteProbeRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ConnectionFault("invalidRequest", 0, "Supply a JSON object.");
        if (!root.TryGetProperty("processId", out var process) || process.ValueKind != JsonValueKind.Number ||
            !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        if (!root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String)
            throw new ConnectionFault("invalidRequest", processId, "Supply a write-probe action.");
        var request = new WriteProbeRequest { ProcessId = processId, Action = action.GetString() ?? "" };
        var allowed = request.Action switch
        {
            "arm" => new[] { "processId", "action", "projectFileName", "confirmDisposable" },
            "disarm" => new[] { "processId", "action" },
            "createCopy" => new[] { "processId", "action", "objectId", "newName", "sourceFormat", "groupObjectId", "groupPath" },
            "replace" => new[] { "processId", "action", "objectId", "sourceFormat" },
            "createTable" => new[] { "processId", "action", "plcObjectId", "newName", "groupObjectId", "groupPath" },
            "addTag" => new[] { "processId", "action", "objectId", "entryKind", "newName", "dataType", "logicalAddress", "value" },
            "setTagAttribute" => new[] { "processId", "action", "objectId", "attributeName", "attributeValue" },
            "deleteTag" => new[] { "processId", "action", "objectId" },
            "importTagTable" => new[] { "processId", "action", "objectId" },
            _ => throw new ConnectionFault("invalidRequest", processId, "Unknown write-probe action.")
        };
        var fields = new HashSet<string>(allowed);
        foreach (var field in root.EnumerateObject())
            if (!fields.Remove(field.Name))
                throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        request.ObjectId = Optional(root, "objectId", processId);
        request.PlcObjectId = Optional(root, "plcObjectId", processId);
        request.GroupObjectId = Optional(root, "groupObjectId", processId);
        request.GroupPath = Optional(root, "groupPath", processId);
        request.NewName = Optional(root, "newName", processId);
        request.DataType = Optional(root, "dataType", processId);
        request.LogicalAddress = Optional(root, "logicalAddress", processId);
        request.Value = Optional(root, "value", processId);
        request.AttributeName = Optional(root, "attributeName", processId);
        request.ProjectFileName = Optional(root, "projectFileName", processId);
        if (root.TryGetProperty("sourceFormat", out var format))
        {
            if (format.ValueKind != JsonValueKind.String || format.GetString() is not ("best" or "external-source" or "simatic-sd" or "simatic-ml"))
                throw new ConnectionFault("invalidRequest", processId, "Unknown sourceFormat.");
            request.SourceFormat = format.GetString()!;
        }
        if (root.TryGetProperty("entryKind", out var entry))
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not ("tag" or "userConstant"))
                throw new ConnectionFault("invalidRequest", processId, "entryKind must be tag or userConstant.");
            request.EntryKind = entry.GetString();
        }
        if (root.TryGetProperty("confirmDisposable", out var confirm))
        {
            if (confirm.ValueKind != JsonValueKind.True && confirm.ValueKind != JsonValueKind.False)
                throw new ConnectionFault("invalidRequest", processId, "confirmDisposable must be a boolean.");
            request.ConfirmDisposable = confirm.GetBoolean();
        }
        if (root.TryGetProperty("attributeValue", out var attribute))
        {
            request.AttributeValue = attribute.ValueKind switch
            {
                JsonValueKind.String => attribute.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when attribute.TryGetInt64(out var number) => number,
                _ => throw new ConnectionFault("invalidRequest", processId, "attributeValue must be a string, boolean, or integer.")
            };
        }
        switch (request.Action)
        {
            case "arm":
                if (string.IsNullOrWhiteSpace(request.ProjectFileName) || request.ProjectFileName!.IndexOfAny(new[] { '/', '\\' }) >= 0)
                    throw new ConnectionFault("invalidRequest", processId, "Type only the connected project file name.");
                if (!request.ConfirmDisposable)
                    throw new ConnectionFault("notArmed", processId, "Confirm that this connected project is disposable.");
                break;
            case "createCopy":
            case "replace":
            case "addTag":
            case "setTagAttribute":
            case "deleteTag":
            case "importTagTable":
                if (string.IsNullOrWhiteSpace(request.ObjectId))
                    throw new ConnectionFault("invalidRequest", processId, "Supply the objectId for this write probe.");
                break;
        }
        if (request.Action is "createCopy" or "createTable")
            CheckName(request.NewName, processId);
        if (request.Action == "createTable" && string.IsNullOrWhiteSpace(request.PlcObjectId))
            throw new ConnectionFault("invalidRequest", processId, "Supply the CPU plcObjectId for the new tag table.");
        if (request.Action == "addTag")
        {
            if (request.EntryKind == null)
                throw new ConnectionFault("invalidRequest", processId, "Supply entryKind tag or userConstant.");
            CheckName(request.NewName, processId);
            if (string.IsNullOrWhiteSpace(request.DataType))
                throw new ConnectionFault("invalidRequest", processId, "Supply a data type.");
            if (request.EntryKind == "tag" && string.IsNullOrWhiteSpace(request.LogicalAddress))
                throw new ConnectionFault("invalidRequest", processId, "Supply a logical address.");
            if (request.EntryKind == "userConstant" && string.IsNullOrWhiteSpace(request.Value))
                throw new ConnectionFault("invalidRequest", processId, "Supply a user-constant value.");
        }
        if (request.Action == "setTagAttribute" &&
            (string.IsNullOrWhiteSpace(request.AttributeName) || request.AttributeValue == null ||
             request.AttributeName!.IndexOfAny(new[] { '\r', '\n' }) >= 0))
            throw new ConnectionFault("invalidRequest", processId, "Supply an attribute name and value.");
        return request;
    }

    private static string? Optional(JsonElement root, string name, int processId)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ConnectionFault("invalidRequest", processId, name + " must be a nonblank string.");
        return value.GetString()!.Trim();
    }

    private static void CheckName(string? name, int processId)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length > 128 || name.IndexOfAny(new[] { '"', '/', '\\', '\r', '\n' }) >= 0)
            throw new ConnectionFault("invalidRequest", processId, "Supply a single-line name without quotes or slashes.");
    }
}

internal sealed class WriteProbeObject
{
    public string? ObjectId { get; set; }
    public string Kind { get; set; } = "";
    public string? Name { get; set; }
}

internal sealed class WriteProbeResult : DiscoveryResult
{
    public string Action { get; set; } = "";
    public bool Armed { get; set; }
    public bool Saved => false;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? ProjectModified { get; set; }
    public bool CleanupFailed => Errors.Any(error => error.Operation == "temporaryCleanup");
    public string? Format { get; set; }
    public string? ProjectPath { get; set; }
    public string? NativeState { get; set; }
    public List<string> NativeMessages { get; set; } = new();
    public List<WriteProbeObject> Created { get; set; } = new();
}

internal sealed class WriteProbeSession
{
    private readonly object _gate = new();
    private Armed? _arm;
    private int _processId;
    private string? _projectPath;
    private readonly Dictionary<string, Entry> _created = new(StringComparer.Ordinal);
    private readonly HashSet<string> _typedFailures = new(StringComparer.Ordinal);

    public void Arm(RequestTicket ticket, string projectPath, string projectFileName, bool confirmDisposable)
    {
        var canonical = DashboardHistory.Canonical(projectPath);
        var fileName = canonical == null ? "" : Path.GetFileName(canonical);
        if (!confirmDisposable || string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(fileName, projectFileName.Trim(), StringComparison.Ordinal))
            throw new ConnectionFault("notArmed", ticket.ProcessId, "Type the connected project file name and confirm that the project is disposable.");
        lock (_gate)
        {
            if (_projectPath != null && (_processId != ticket.ProcessId ||
                !string.Equals(_projectPath, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                _created.Clear();
                _typedFailures.Clear();
            }
            _processId = ticket.ProcessId;
            _projectPath = canonical;
            _arm = new Armed(ticket.ProcessId, ticket.ConnectionId, canonical!);
        }
    }

    public void Disarm(int processId)
    {
        lock (_gate)
            if (_arm != null && _arm.ProcessId == processId) _arm = null;
    }

    public void Require(RequestTicket ticket, string projectPath)
    {
        var canonical = DashboardHistory.Canonical(projectPath);
        lock (_gate)
        {
            if (_arm != null && _arm.ProcessId == ticket.ProcessId &&
                !string.Equals(_arm.ProjectPath, canonical, StringComparison.OrdinalIgnoreCase))
            {
                _arm = null;
                _projectPath = null;
                _created.Clear();
                _typedFailures.Clear();
            }
            if (_arm == null || _arm.ProcessId != ticket.ProcessId || _arm.ConnectionId != ticket.ConnectionId ||
                !string.Equals(_arm.ProjectPath, canonical, StringComparison.OrdinalIgnoreCase))
                throw new ConnectionFault("notArmed", ticket.ProcessId, "Arm this disposable project again before a write probe.");
        }
    }

    public void Remember(string objectId, string kind, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return;
        lock (_gate) _created[objectId] = new Entry(kind, parentId);
    }

    public void Forget(string objectId)
    {
        lock (_gate) _created.Remove(objectId);
    }

    public string? ParentOf(string objectId)
    {
        lock (_gate) return _created.TryGetValue(objectId, out var entry) ? entry.ParentId : null;
    }

    public void NoteTypedFailure(string tableObjectId)
    {
        if (!string.IsNullOrWhiteSpace(tableObjectId))
            lock (_gate) _typedFailures.Add(tableObjectId);
    }

    public bool TypedFailed(string tableObjectId)
    {
        lock (_gate) return _typedFailures.Contains(tableObjectId);
    }

    public void Guard(WriteProbeRequest request)
    {
        switch (request.Action)
        {
            case "replace":
                RequireCreated(request, "block", "udt");
                break;
            case "deleteTag":
            case "setTagAttribute":
                RequireCreated(request, "tag", "userConstant");
                break;
            case "addTag":
                RequireCreated(request, "tagTable");
                break;
            case "importTagTable":
                RequireCreated(request, "tagTable");
                if (!TypedFailed(request.ObjectId!))
                    throw new ConnectionFault("typedImportUnavailable", request.ProcessId,
                        "Whole-table SimaticML import is available after a typed create or delete on this probe table returns a native error.");
                break;
        }
    }

    private void RequireCreated(WriteProbeRequest request, params string[] kinds)
    {
        lock (_gate)
        {
            if (request.ObjectId == null || !_created.TryGetValue(request.ObjectId, out var entry) ||
                !kinds.Contains(entry.Kind, StringComparer.Ordinal))
                throw new ConnectionFault("notProbeObject", request.ProcessId,
                    "This probe can change only an object it created in the armed project.");
        }
    }

    private sealed record Armed(int ProcessId, Guid ConnectionId, string ProjectPath);
    private sealed record Entry(string Kind, string? ParentId);
}

internal static class WriteProbeNames
{
    public static IReadOnlyList<string> Substitute(IReadOnlyList<string> documents, string? currentName, string newName, int processId)
    {
        if (string.IsNullOrWhiteSpace(currentName) || currentName!.IndexOf('"') >= 0 || newName.IndexOf('"') >= 0)
            throw new ConnectionFault("ambiguousName", processId, "The exported name must be a single quoted identifier.");
        var quoted = "\"" + currentName + "\"";
        var replacement = "\"" + newName + "\"";
        var changed = 0;
        var result = new List<string>(documents.Count);
        foreach (var document in documents)
        {
            var count = Count(document, quoted);
            if (count > 1)
                throw new ConnectionFault("ambiguousName", processId, "A source document contains the current quoted name more than once.");
            if (count == 1)
            {
                changed++;
                result.Add(document.Replace(quoted, replacement));
            }
            else result.Add(document);
        }
        if (changed != 1)
            throw new ConnectionFault("ambiguousName", processId, "The exported source must contain the current quoted name in exactly one document.");
        return result;
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}

internal static class WriteProbeFiles
{
    public static string TemporaryRoot { get; } =
        Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public static string CreateDirectory()
    {
        var folder = Path.GetFullPath(Path.Combine(TemporaryRoot, "tia-write-" + Guid.NewGuid().ToString("N")));
        if (!IsOwned(folder) || folder.Length > 200)
            throw new InvalidOperationException("A short, owned temporary directory is required.");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static bool IsOwned(string folder)
    {
        var full = Path.GetFullPath(folder);
        var marker = Path.DirectorySeparatorChar + "tia-write-";
        return full.StartsWith(TemporaryRoot, StringComparison.OrdinalIgnoreCase) &&
            full.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void DeleteOwned(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!IsOwned(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)
                .Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Temporary write cleanup refused an unexpected path or link.");
        Directory.Delete(full, recursive: true);
    }

    public static void Finish(WriteProbeResult result, string? folder)
    {
        if (folder == null) return;
        try { DeleteOwned(folder); }
        catch (Exception ex)
        {
            result.Errors.Add(new DiscoveryError
            {
                Origin = "bridge", Operation = "temporaryCleanup", Message = ex.Message
            });
        }
    }
}
