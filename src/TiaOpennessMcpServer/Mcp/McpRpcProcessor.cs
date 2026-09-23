using System.Globalization;
using System.Text.Json;

namespace TiaOpennessMcpServer.Mcp;

// The HTTP endpoint and the offline harness use this same wire-message admission path.
// This host accepts individual requests/notifications; arrays are rejected before dispatch.
internal sealed class McpRpcProcessor
{
    private readonly McpBoundary _boundary;
    public McpRpcProcessor(McpBoundary boundary) => _boundary = boundary;

    public async Task<McpMessageResult> ProcessAsync(string text)
    {
        McpRpcRequest? request;
        try
        {
            using var document = JsonDocument.Parse(text);
            request = Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return new McpMessageResult(McpRpcResponse.Failure(-32700, "Parse error"), 400);
        }
        if (request == null)
            return new McpMessageResult(McpRpcResponse.Failure(-32600, "Invalid Request"), 400);

        // Valid notifications are one-way messages. In particular, they must never
        // reach tools/call or cause an engineering operation to be retried/cancelled.
        if (request.Id == null) return new McpMessageResult(null, 202);
        try
        {
            var (result, error) = await _boundary.HandleAsync(request);
            return new McpMessageResult(new McpRpcResponse { Id = request.Id, Result = result, Error = error });
        }
        catch (Exception ex)
        {
            return new McpMessageResult(McpRpcResponse.Failure(-32603, ex.Message, request.Id), 500);
        }
    }

    private static McpRpcRequest? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!fields.Add(property.Name)) return null;
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != "2.0" ||
            !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
            root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)) return null;
        var hasParameters = root.TryGetProperty("params", out var parameters);
        if (hasParameters && parameters.ValueKind != JsonValueKind.Object) return null;
        var name = method.GetString()!;
        var notification = name.StartsWith("notifications/", StringComparison.Ordinal) && name.Length > "notifications/".Length;
        var hasId = root.TryGetProperty("id", out var id);
        if (notification ? hasId : !hasId || !IsRequestId(id)) return null;
        return new McpRpcRequest
        {
            JsonRpc = "2.0", Method = name,
            Id = hasId ? (object)id.Clone() : null,
            Params = hasParameters ? parameters.Clone() : (JsonElement?)null
        };
    }

    private static bool IsRequestId(JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.String) return true;
        if (id.ValueKind != JsonValueKind.Number) return false;
        // Check integrality without converting/rounding opaque numeric IDs. This
        // also preserves large integers and integral decimal/exponent spellings.
        var text = id.GetRawText();
        var exponentAt = text.IndexOfAny(new[] { 'e', 'E' });
        var significand = exponentAt < 0 ? text : text.Substring(0, exponentAt);
        var point = significand.IndexOf('.');
        var fractionalDigits = point < 0 ? 0 : significand.Length - point - 1;
        var digits = significand.Replace(".", "").TrimStart('-').TrimStart('0');
        if (digits.Length == 0) return true;
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        if (exponentAt < 0) return trailingZeros >= fractionalDigits;
        var exponentText = text.Substring(exponentAt + 1);
        if (!int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent))
            return exponentText[0] != '-';
        return (long)exponent + trailingZeros >= fractionalDigits;
    }
}

internal sealed class McpMessageResult
{
    public McpRpcResponse? Response { get; }
    public int StatusCode { get; }
    public McpMessageResult(McpRpcResponse? response, int statusCode = 200)
    { Response = response; StatusCode = statusCode; }
}
