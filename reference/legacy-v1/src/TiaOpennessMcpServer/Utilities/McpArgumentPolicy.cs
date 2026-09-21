using System;
using System.Collections.Generic;

namespace TiaOpennessMcpServer.Utilities;

public static class McpArgumentPolicy
{
    public static string? FirstUnknown(
        IEnumerable<string> allowedArguments,
        IEnumerable<string> suppliedArguments)
    {
        if (allowedArguments is null)
            throw new ArgumentNullException(nameof(allowedArguments));
        if (suppliedArguments is null)
            throw new ArgumentNullException(nameof(suppliedArguments));

        var allowed = new HashSet<string>(allowedArguments, StringComparer.Ordinal);
        foreach (var supplied in suppliedArguments)
        {
            if (!allowed.Contains(supplied))
                return supplied;
        }
        return null;
    }
}
