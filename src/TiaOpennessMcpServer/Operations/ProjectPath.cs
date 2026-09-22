namespace TiaOpennessMcpServer.Operations;

// Shared path normalization used by attachment guards and project history.
internal static class ProjectPath
{
    public static string? Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path!.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
                return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (PathTooLongException) { }
        return trimmed;
    }
}
