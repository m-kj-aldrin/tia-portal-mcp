namespace TiaOpennessMcpServer.Operations;

internal static class WriteFiles
{
    private const string Prefix = "tia-write-";

    public static string CreateDirectory() =>
        OwnedTemp.Create(Prefix, () => new InvalidOperationException("A short, owned temporary directory is required."));

    public static bool IsOwned(string folder) => OwnedTemp.IsOwned(folder, Prefix);

    public static void DeleteOwned(string folder) =>
        OwnedTemp.DeleteRecursive(folder, Prefix, "Temporary write cleanup refused an unexpected path or link.");

    public static List<FileInfo> Stage(string folder, IReadOnlyList<WriteDocument> documents)
    {
        if (!IsOwned(folder)) throw new IOException("Unexpected staging directory.");
        var files = new List<FileInfo>();
        foreach (var document in documents)
        {
            var path = Path.GetFullPath(Path.Combine(folder, document.Name));
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Document escaped the staging directory.");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false, true))) writer.Write(document.Content);
            files.Add(new FileInfo(path));
        }
        return files;
    }

    public static void Finish(WriteResult result, string? folder)
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
