using System.Text.Json;

namespace TiaOpennessMcpServer.Operations;

internal static class RequestValidation
{
    public static int PositiveProcessId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("processId", out var process) ||
            process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var processId) || processId <= 0)
            throw new ConnectionFault("invalidRequest", 0, "Supply a positive integer processId.");
        return processId;
    }

    public static void AllowedFields(JsonElement root, int processId, IEnumerable<string> allowed,
        string errorPrefix = "Unknown or duplicate field: ")
    {
        var fields = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!fields.Remove(field.Name)) throw new ConnectionFault("invalidRequest", processId, errorPrefix + field.Name);
    }

    public static string RequiredString(JsonElement root, int processId, string name, string errorMessage)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ConnectionFault("invalidRequest", processId, errorMessage);
        return value.GetString()!; // Native identifiers are opaque; never trim or reinterpret them.
    }

    public static bool BooleanFlag(JsonElement root, int processId, string name, bool fallback = true)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            throw new ConnectionFault("invalidRequest", processId, name + " must be a boolean.");
        return value.GetBoolean();
    }
}
