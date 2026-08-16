using System;
using System.Collections.Generic;

namespace TiaOpennessMcpServer.Utilities;

public static class V1McpToolPolicy
{
    private static readonly IReadOnlyList<string> CanonicalNamesValue = Array.AsReadOnly(new[]
    {
        "connect_to_tia_portal",
        "get_status",
        "list_devices",
        "list_plc_objects",
        "find_plc_objects",
        "read_plc_object",
        "get_tag_table_entries",
        "get_cross_references",
    });

    private static readonly HashSet<string> CanonicalNameSet =
        new(CanonicalNamesValue, StringComparer.Ordinal);

    public static IReadOnlyList<string> CanonicalNames => CanonicalNamesValue;

    public static bool IsCanonical(string? toolName) =>
        toolName is not null && CanonicalNameSet.Contains(toolName);
}
