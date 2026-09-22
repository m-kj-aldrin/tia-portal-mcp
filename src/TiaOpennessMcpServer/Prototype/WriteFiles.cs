namespace TiaOpennessMcpServer.Prototype;

internal static class WriteFiles
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
        var name = Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar), TemporaryRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith("tia-write-", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(10), "N", out _);
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
