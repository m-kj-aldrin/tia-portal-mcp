namespace TiaOpennessMcpServer.Operations;

internal static class OwnedTemp
{
    public static string Root { get; } =
        Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public static bool IsOwned(string folder, string prefix)
    {
        var full = Path.GetFullPath(folder);
        var name = Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(prefix.Length), "N", out _);
    }

    public static string Create(string prefix, Func<Exception> invalid)
    {
        var folder = Path.GetFullPath(Path.Combine(Root, prefix + Guid.NewGuid().ToString("N")));
        if (!IsOwned(folder, prefix) || folder.Length > 200) throw invalid();
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void DeleteRecursive(string folder, string prefix, string refusal)
    {
        var full = Path.GetFullPath(folder);
        if (!IsOwned(full, prefix) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)
                .Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
            throw new IOException(refusal);
        Directory.Delete(full, recursive: true);
    }

    public static void DeleteFlat(string folder, string prefix, string pathRefusal, string entryRefusal)
    {
        if (!IsOwned(folder, prefix) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(pathRefusal);
        var files = Directory.GetFileSystemEntries(folder);
        if (files.Any(file => (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0))
            throw new IOException(entryRefusal);
        foreach (var file in files) File.Delete(file);
        Directory.Delete(folder);
    }
}
