using System;
using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Utilities;

public static class V1NativeFailurePolicy
{
    public static string Classify(
        string? nativeMessage,
        bool isMissingProductException = false,
        bool isSecurityException = false)
    {
        if (isMissingProductException ||
            ContainsAny(nativeMessage, "missing product", "not installed", "support package"))
        {
            return V1ErrorCodes.MissingProductOrOption;
        }

        // Explicit project/external-access evidence is actionable and distinct
        // from object know-how protection, even if both are mentioned.
        if (ContainsAny(
                nativeMessage,
                "external access",
                "project authentication",
                "login required",
                "log in"))
        {
            return V1ErrorCodes.UiAuthenticationRequired;
        }

        if (V1ProtectionPolicy.IsProtectedFailureMessage(nativeMessage))
            return V1ErrorCodes.ProtectedContent;

        if (ContainsAny(
                nativeMessage,
                "authentication required",
                "authentication is required",
                "requires authentication",
                "authenticate"))
        {
            return V1ErrorCodes.UiAuthenticationRequired;
        }

        // EngineeringSecurityException alone does not identify whether the
        // cause is project authentication, object protection, or another
        // Siemens security condition. Preserve it as an unspecified export
        // failure unless native message data supplies the cause above.
        _ = isSecurityException;
        return V1ErrorCodes.ExportFailed;
    }

    private static bool ContainsAny(string? value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var needle in needles)
        {
            if (value!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
