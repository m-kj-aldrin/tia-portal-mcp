using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Operations;

internal sealed class ExportTagTableRequest
{
    public int ProcessId { get; private set; }
    public string ObjectId { get; private set; } = "";

    public static ExportTagTableRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        var allowed = new HashSet<string>(new[] { "processId", "objectId" });
        foreach (var field in root.EnumerateObject())
            if (!allowed.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, "Unknown or duplicate field: " + field.Name);
        if (!root.TryGetProperty("objectId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
            throw new ConnectionFault("invalidRequest", processId, "Supply a nonblank tag-table objectId.");
        return new ExportTagTableRequest { ProcessId = processId, ObjectId = id.GetString()! };
    }
}

internal sealed class TagTableExportResult : DiscoveryResult
{
    public string ObjectId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BlockSource? Source { get; set; }
}

// The native callback and its guards execute synchronously on the engineering STA.
// File handling stays Siemens-free so exact content and cleanup can be verified offline.
internal static class TagTableExportReader
{
    private const string Prefix = "tia-tag-read-";

    public static void Read(TagTableExportResult result, Action<FileInfo> export, Action validate, Func<Exception, string> origin)
    {
        string? folder = null;
        try
        {
            validate();
            folder = CreateDirectory();
            var file = new FileInfo(Path.Combine(folder, "tag-table.xml"));
            export(file);
            validate();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(file.FullName) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Export returned an unexpected path or link.");
            string content;
            using (var reader = new StreamReader(file.FullName, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
                content = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(content)) throw new IOException("The export returned no usable source content.");
            validate();
            result.Source = new BlockSource
            {
                Format = "simatic-ml", Documents = new List<SourceDocument> { new(file.Name, content) }
            };
        }
        catch (ConnectionFault) { throw; }
        catch (Exception ex)
        {
            validate(); // A native failure must never conceal loss of the retained project.
            result.Errors.Add(new DiscoveryError { Origin = origin(ex), Operation = "sourceExport", Format = "simatic-ml", Message = ex.Message });
        }
        finally
        {
            if (folder != null)
            {
                try { DeleteDirectory(folder); }
                catch (Exception ex)
                {
                    result.Errors.Add(new DiscoveryError { Origin = "bridge", Operation = "temporaryCleanup", Format = "simatic-ml", Message = ex.Message });
                }
            }
        }
    }

    private static string CreateDirectory()
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        if (!IsOwned(folder) || folder.Length > 200)
            throw new IOException("A short, owned temporary export directory is required.");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static bool IsOwned(string folder)
    {
        var full = Path.GetFullPath(folder);
        var name = Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(Prefix.Length), "N", out _);
    }

    private static void DeleteDirectory(string folder)
    {
        if (!IsOwned(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Temporary export cleanup refused an unexpected path or link.");
        var files = Directory.GetFileSystemEntries(folder);
        // A table export creates one flat XML file. Never recurse through an unexpected directory or link.
        if (files.Any(file => (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new IOException("Temporary export cleanup refused an unexpected directory or link.");
        foreach (var file in files) File.Delete(file);
        Directory.Delete(folder);
    }
}
