using System;
using System.Collections.Generic;
using System.Linq;
using TiaOpennessMcpServer.Models;

namespace TiaOpennessMcpServer.Utilities;

public static class V1ProtectionPolicy
{
    public const string ProtectedContentLimitation =
        "TIA may expose only a protected native view; hidden implementation content remains unknown.";

    public static V1Protection Describe(bool? isProtected, string? contentLimitation = null)
    {
        if (isProtected == true)
        {
            return new V1Protection
            {
                State = V1ProtectionStates.Protected,
                IsProtected = true,
                Type = V1ProtectionTypes.KnowHow,
                Access = V1ProtectionAccess.NativeLimited,
                ContentLimitation = string.IsNullOrWhiteSpace(contentLimitation)
                    ? ProtectedContentLimitation
                    : contentLimitation,
            };
        }

        if (isProtected == false)
        {
            return new V1Protection
            {
                State = V1ProtectionStates.Unprotected,
                IsProtected = false,
                Access = V1ProtectionAccess.NativeFull,
            };
        }

        return new V1Protection
        {
            State = V1ProtectionStates.Unknown,
            Access = V1ProtectionAccess.Unknown,
        };
    }

    public static string ContentScope(bool? isProtected) => isProtected switch
    {
        true => V1ContentScopes.TiaExposedProtectedView,
        false => V1ContentScopes.FullNativeRepresentation,
        null => V1ContentScopes.ProtectionUnknown,
    };

    public static bool IsProtectedFailureMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var tokens = new string(message!
                .Select(character => char.IsLetterOrDigit(character)
                    ? char.ToLowerInvariant(character)
                    : ' ')
                .ToArray())
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < tokens.Length; index++)
        {
            if (IsNegated(tokens, index))
                continue;

            if (Matches(tokens, index, "know", "how", "protected") ||
                Matches(tokens, index, "know", "how", "protection") ||
                Matches(tokens, index, "protected", "content") ||
                string.Equals(tokens[index], "knowhowprotected", StringComparison.Ordinal) ||
                string.Equals(tokens[index], "knowhowprotection", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNegated(IReadOnlyList<string> tokens, int index)
    {
        return EndsImmediatelyBefore(tokens, index, "not") ||
               EndsImmediatelyBefore(tokens, index, "no") ||
               EndsImmediatelyBefore(tokens, index, "without") ||
               EndsImmediatelyBefore(tokens, index, "not", "currently") ||
               EndsImmediatelyBefore(tokens, index, "no", "longer") ||
               EndsImmediatelyBefore(tokens, index, "without", "any") ||
               EndsImmediatelyBefore(tokens, index, "not", "marked") ||
               EndsImmediatelyBefore(tokens, index, "not", "marked", "as") ||
               EndsImmediatelyBefore(tokens, index, "not", "configured", "as") ||
               EndsImmediatelyBefore(tokens, index, "not", "set", "as");
    }

    private static bool EndsImmediatelyBefore(
        IReadOnlyList<string> tokens,
        int index,
        params string[] expected)
    {
        var start = index - expected.Length;
        return start >= 0 && Matches(tokens, start, expected);
    }

    private static bool Matches(
        IReadOnlyList<string> tokens,
        int start,
        params string[] expected)
    {
        if (start + expected.Length > tokens.Count)
            return false;
        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(tokens[start + index], expected[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
