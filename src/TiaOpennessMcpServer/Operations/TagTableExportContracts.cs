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
        var processId = RequestValidation.PositiveProcessId(root);
        RequestValidation.AllowedFields(root, processId, new[] { "processId", "objectId" });
        return new ExportTagTableRequest { ProcessId = processId,
            ObjectId = RequestValidation.RequiredString(root, processId, "objectId", "Supply a nonblank tag-table objectId.") };
    }
}

internal sealed class TagTableExportResult : DiscoveryResult
{
    public string ObjectId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public BlockSource? Source { get; set; }
}

// The native callback and its guards execute synchronously on the engineering STA.
// Owns temporary export files, exact content and cleanup.
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

    private static string CreateDirectory() =>
        OwnedTemp.Create(Prefix, () => new IOException("A short, owned temporary export directory is required."));

    // A table export creates one flat XML file. Never recurse through an unexpected directory or link.
    private static void DeleteDirectory(string folder) =>
        OwnedTemp.DeleteFlat(folder, Prefix,
            "Temporary export cleanup refused an unexpected path or link.",
            "Temporary export cleanup refused an unexpected directory or link.");
}
