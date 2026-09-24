using System.Net;
using System.Text;
using System.Text.Json;

using TiaOpennessMcpServer.Mcp;

namespace TiaOpennessMcpServer.Dashboard;

/// <summary>Minimal dashboard forms produced from the same tool definitions as MCP tools/list.</summary>
internal static class DashboardToolForms
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["list_tia_processes"] = "List TIA processes",
        ["get_status"] = "Get status",
        ["list_devices"] = "List devices",
        ["get_device"] = "Read device",
        ["list_blocks"] = "List blocks",
        ["get_block"] = "Read block",
        ["list_udts"] = "List UDTs",
        ["get_udt"] = "Read UDT",
        ["list_tag_tables"] = "List tag tables",
        ["get_tag_table"] = "Read tag table",
        ["get_cross_references"] = "Read cross-references",
        ["export_tag_table"] = "Export tag table as SimaticML",
        ["list_technology_objects"] = "List technology objects",
        ["get_technology_object"] = "Read technology object",
        ["write_blocks"] = "Write blocks", ["write_udts"] = "Write UDTs",
        ["create_tag_table"] = "Create tag table", ["create_tag"] = "Create tag",
        ["create_user_constant"] = "Create user constant", ["set_tag_entry_attribute"] = "Edit tag or constant attribute",
        ["delete_tag_entry"] = "Delete tag or constant", ["import_tag_tables"] = "Import tag tables",
        ["delete_block"] = "Delete block", ["delete_udt"] = "Delete UDT",
        ["delete_tag_table"] = "Delete tag table",
        ["create_technology_object"] = "Create technology object",
        ["set_technology_object_parameters"] = "Set technology object parameters",
        ["compile_plc"] = "Compile PLC software"
    };

    public static string Render(IReadOnlyList<McpToolDefinition> tools)
    {
        var html = new StringBuilder();
        foreach (var tool in tools)
        {
            var process = tool.InputSchema.Properties.ContainsKey("processId");
            var requiredProcess = Required(tool, "processId");
            html.Append("<form data-tool=\"").Append(Encode(tool.Name)).Append("\" data-process=\"")
                .Append(process ? (requiredProcess ? "required" : "optional") : "none")
                .Append("\" data-write=\"").Append(tool.Annotations.ReadOnlyHint ? "false" : "true")
                .Append("\" data-project=\"").Append(requiredProcess ? "true" : "false").Append("\">");
            html.Append("<p>").Append(Encode(tool.Description)).Append("</p>");
            foreach (var property in tool.InputSchema.Properties)
            {
                var schema = property.Value as Dictionary<string, object> ?? new Dictionary<string, object>();
                var type = schema.TryGetValue("type", out var kind) ? kind as string ?? "string" : "string";
                var required = Required(tool, property.Key);
                schema.TryGetValue("default", out var fallback);
                var enabledWhen = property.Key == "includeDependencies" && tool.InputSchema.AllOf != null
                    ? "includeSource=true,sourceFormat=external-source" : null;
                html.Append("<label>").Append(Encode(property.Key)).Append(' ');
                if (schema.TryGetValue("enum", out var values) && values is string[] choices)
                {
                    html.Append("<select name=\"").Append(Encode(property.Key)).Append("\" data-type=\"string\"");
                    if (required) html.Append(" data-required=\"true\"");
                    if (fallback is string selected) html.Append(" data-default=\"").Append(Encode(selected)).Append('"');
                    html.Append('>');
                    if (required && fallback == null) html.Append("<option value=\"\">Choose format...</option>");
                    foreach (var choice in choices)
                    {
                        html.Append("<option value=\"").Append(Encode(choice)).Append('"');
                        if (fallback is string current && current == choice) html.Append(" selected");
                        html.Append('>').Append(Encode(choice)).Append("</option>");
                    }
                    html.Append("</select>");
                }
                else if (type == "boolean")
                {
                    var on = fallback is true;
                    html.Append("<input name=\"").Append(Encode(property.Key))
                        .Append("\" type=\"checkbox\" data-type=\"boolean\" data-default=\"").Append(on ? "true" : "false").Append('"');
                    if (on) html.Append(" checked");
                    if (enabledWhen != null) html.Append(" data-enabled-when=\"").Append(Encode(enabledWhen)).Append('"');
                    html.Append('>');
                }
                else if (type == "array" || schema["type"] is string[])
                {
                    html.Append("<textarea name=\"").Append(Encode(property.Key)).Append("\" data-type=\"")
                        .Append(type == "array" ? "documents" : "json").Append("\" data-required=\"true\"></textarea>");
                }
                else
                {
                    var input = type == "integer" ? "number" : "text";
                    html.Append("<input name=\"").Append(Encode(property.Key)).Append("\" type=\"").Append(input)
                        .Append("\" data-type=\"").Append(Encode(type)).Append('"');
                    if (required) html.Append(" data-required=\"true\"");
                    if (property.Key == "processId") html.Append(" readonly");
                    html.Append('>');
                }
                var helpSchema = JsonSerializer.SerializeToElement(schema);
                html.Append("<small class=\"field-help\"");
                if (helpSchema.TryGetProperty("items", out var items) && items.TryGetProperty("properties", out var children))
                    foreach (var child in children.EnumerateObject())
                        html.Append(" data-document-").Append(Encode(child.Name)).Append("-help=\"")
                            .Append(Encode(Help(child.Value, true))).Append('"');
                html.Append('>').Append(Encode(Help(helpSchema, required))).Append("</small></label>");
            }
            var label = Labels.TryGetValue(tool.Name, out var friendly) ? friendly : tool.Name;
            html.Append("<button type=\"button\">").Append(Encode(label)).Append("</button></form>");
        }
        return html.ToString();
    }

    private static bool Required(McpToolDefinition tool, string name) =>
        tool.InputSchema.Required.Any(item => string.Equals(item, name, StringComparison.Ordinal));

    private static string Help(JsonElement schema, bool required)
    {
        var type = schema.GetProperty("type");
        var text = new StringBuilder(required ? "Required. " : "Optional. ");
        text.Append("Type: ").Append(type.ValueKind == JsonValueKind.Array
            ? string.Join(" or ", type.EnumerateArray().Select(item => item.GetString())) : type.GetString()).Append(". ");
        if (schema.TryGetProperty("default", out var fallback)) text.Append("Default: ").Append(fallback.GetRawText()).Append(". ");
        if (schema.TryGetProperty("description", out var description)) text.Append(description.GetString());
        if (schema.TryGetProperty("examples", out var examples))
            text.Append(" Examples: ").Append(string.Join(", ", examples.EnumerateArray().Select(item => item.GetRawText()))).Append('.');
        return text.ToString();
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
}
