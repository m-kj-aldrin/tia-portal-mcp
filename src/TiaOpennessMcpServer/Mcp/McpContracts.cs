using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Mcp;

internal sealed class McpRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public object? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}
internal sealed class McpToolAnnotations
{
    public bool ReadOnlyHint { get; set; }
    public bool DestructiveHint => !ReadOnlyHint;
}

internal sealed class McpToolDefinition
{
    public McpToolAnnotations Annotations { get; set; } = new();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public McpInputSchema InputSchema { get; set; } = new();
}
internal sealed class McpInputSchema
{
    public string Type { get; set; } = "object";
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.Ordinal);
    public string[] Required { get; set; } = Array.Empty<string>();
    public bool AdditionalProperties => false;
    public object[]? AllOf { get; set; }
}
