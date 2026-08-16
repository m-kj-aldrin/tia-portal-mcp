using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaOpennessMcpServer.Utilities;

public static class V1ProjectSelectionActions
{
    public const string Reuse = "reuse";
    public const string Conflict = "conflict";
    public const string AttachExact = "attach-exact";
    public const string OpenVisible = "open-visible";
    public const string Ambiguity = "ambiguity";
    public const string NoActive = "no-active";
}

public sealed record V1ProjectSelectionDecision
{
    public required string Action { get; init; }
    public string? ActivePath { get; init; }
    public string? RequestedPath { get; init; }
    public string? SelectedPath { get; init; }
    public required IReadOnlyList<string> CandidatePaths { get; init; }
    public required string Reason { get; init; }
}

public static class V1ProjectSelectionPolicy
{
    public static V1ProjectSelectionDecision Decide(
        string? activeProjectPath,
        string? requestedProjectPath,
        IEnumerable<string?>? openCandidatePaths)
    {
        var active = CanonicalizeOptional(activeProjectPath);
        var requested = CanonicalizeOptional(requestedProjectPath);
        var candidates = (openCandidatePaths ?? Array.Empty<string?>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => CanonicalizePath(path!))
            .ToArray();

        if (active is not null)
        {
            if (requested is null || PathEquals(active, requested))
            {
                return Decision(
                    V1ProjectSelectionActions.Reuse,
                    active,
                    requested,
                    active,
                    candidates,
                    requested is null
                        ? "An active project is already attached and no different path was requested."
                        : "The requested path is the active project path after canonical normalization.");
            }

            return Decision(
                V1ProjectSelectionActions.Conflict,
                active,
                requested,
                null,
                candidates,
                "A different project is already active; version one does not switch projects implicitly.");
        }

        if (requested is not null)
        {
            var exactCandidates = candidates.Where(path => PathEquals(path, requested)).ToArray();
            if (exactCandidates.Length == 1)
            {
                return Decision(
                    V1ProjectSelectionActions.AttachExact,
                    null,
                    requested,
                    exactCandidates[0],
                    candidates,
                    "Exactly one open project matches the authoritative requested path.");
            }

            if (exactCandidates.Length > 1)
            {
                return Decision(
                    V1ProjectSelectionActions.Ambiguity,
                    null,
                    requested,
                    null,
                    exactCandidates,
                    "More than one open project candidate matches the requested path.");
            }

            return Decision(
                V1ProjectSelectionActions.OpenVisible,
                null,
                requested,
                requested,
                candidates,
                "No open project matches the requested path; open that path with a visible TIA UI.");
        }

        if (candidates.Length == 0)
        {
            return Decision(
                V1ProjectSelectionActions.NoActive,
                null,
                null,
                null,
                candidates,
                "No project is active and no open project candidate or requested path is available.");
        }

        if (candidates.Length == 1)
        {
            return Decision(
                V1ProjectSelectionActions.AttachExact,
                null,
                null,
                candidates[0],
                candidates,
                "Exactly one suitable open project candidate is available.");
        }

        return Decision(
            V1ProjectSelectionActions.Ambiguity,
            null,
            null,
            null,
            candidates,
            "Multiple open project candidates require explicit selection.");
    }

    public static string CanonicalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A project path is required.", nameof(path));

        var canonical = Path.GetFullPath(path.Trim())
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (canonical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            canonical = @"\\" + canonical.Substring(8);
        else if (canonical.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            canonical = canonical.Substring(4);

        var root = Path.GetPathRoot(canonical) ?? string.Empty;
        while (canonical.Length > root.Length &&
               (canonical.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                canonical.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
        {
            canonical = canonical.Substring(0, canonical.Length - 1);
        }

        return canonical;
    }

    private static string? CanonicalizeOptional(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : CanonicalizePath(path!);

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static V1ProjectSelectionDecision Decision(
        string action,
        string? active,
        string? requested,
        string? selected,
        IReadOnlyList<string> candidates,
        string reason) => new()
    {
        Action = action,
        ActivePath = active,
        RequestedPath = requested,
        SelectedPath = selected,
        CandidatePaths = candidates,
        Reason = reason,
    };
}
