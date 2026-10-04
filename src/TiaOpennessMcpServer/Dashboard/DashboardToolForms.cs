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
        ["search_hardware_catalog"] = "Search hardware catalogue",
        ["get_device"] = "Read device",
        ["create_device"] = "Create device",
        ["delete_device"] = "Delete device",
        ["list_blocks"] = "List blocks",
        ["get_block"] = "Read block",
        ["list_udts"] = "List UDTs",
        ["get_udt"] = "Read UDT",
        ["list_tag_tables"] = "List tag tables",
        ["get_tag_table"] = "Read tag table",
        ["get_cross_references"] = "Read cross-references",
        ["export_tag_table"] = "Export tag table as SimaticML",
        ["list_technology_objects"] = "List technology objects",
        ["list_available_technology_objects"] = "List available technology objects",
        ["get_technology_object"] = "Read technology object",
        ["write_blocks"] = "Write blocks", ["write_udts"] = "Write UDTs",
        ["create_tag_table"] = "Create tag table", ["create_tag"] = "Create tag",
        ["create_user_constant"] = "Create user constant", ["set_tag_entry_attribute"] = "Edit tag or constant attribute",
        ["delete_tag_entry"] = "Delete tag or constant", ["import_tag_tables"] = "Import tag tables",
        ["delete_block"] = "Delete block", ["delete_udt"] = "Delete UDT",
        ["delete_tag_table"] = "Delete tag table",
        ["create_technology_object"] = "Create technology object",
        ["set_technology_object_parameters"] = "Set technology object parameters",
        ["create_group"] = "Create group", ["delete_group"] = "Delete group", ["rename"] = "Rename",
        ["compile_plc"] = "Compile PLC software"
    };

    // The dashboard is a view of the published MCP schema. Form fields carry raw
    // browser values; DashboardToolRunner converts them using that same schema
    // before passing the resulting arguments through the composed MCP boundary.
    public static string Render(IReadOnlyList<McpToolDefinition> tools, string tabId, string signalPrefix,
        bool isServerTab, int? processId, bool projectReady,
        IReadOnlyDictionary<string, IReadOnlyList<(string Id, string Label)>>? options = null,
        string? inventoryCpu = null)
    {
        var prefix = SafeSignalPrefix(signalPrefix);
        var ui = "_ui.tabs." + TabKey(tabId);
        var available = tools.Where(tool => ForTab(tool, isServerTab)).ToArray();
        var read = available.Where(tool => tool.Annotations.ReadOnlyHint).ToArray();
        var write = available.Where(tool => !tool.Annotations.ReadOnlyHint).ToArray();
        var html = new StringBuilder();
        html.Append("<div id=\"dashboard-form-").Append(prefix)
            .Append("\" class=\"dashboard-tab-forms\" data-tab-id=\"").Append(Encode(tabId))
            .Append("\" data-show=\"").Append(Encode("$_ui.selectedTabId === " + JsString(tabId)))
            .Append("\" data-preserve-attr=\"style\"");
        if (!isServerTab) html.Append(" style=\"display:none\"");
        html
            .Append(" data-signals=\"").Append(Encode("{_drafts:{" + prefix + ":{inventorycpu:" + JsString(inventoryCpu ?? "") + "}}}"))
            .Append("\">");
        foreach (var kind in DatalistKinds)
        {
            html.Append("<datalist id=\"").Append(DatalistId(signalPrefix, kind)).Append("\">");
            if (options != null && options.TryGetValue(kind, out var values))
                foreach (var value in values)
                    html.Append("<option value=\"").Append(Encode(value.Id))
                        .Append("\" label=\"").Append(Encode(value.Label)).Append("\"></option>");
            html.Append("</datalist>");
        }
        RenderPicker(html, read, write, ui + ".tool");
        if (!isServerTab && !projectReady)
            html.Append("<p class=\"tool-prerequisite\">Connect this TIA process and open its project before running project operations.</p>");
        foreach (var tool in available)
            RenderLiveForm(html, tool, ui, prefix, signalPrefix, isServerTab, projectReady);
        html.Append("</div>");
        return html.ToString();
    }

    internal static string DatalistId(string signalPrefix, string kind) =>
        "dashboard-options-" + SafeSignalPrefix(signalPrefix) + "-" + kind;

    internal static string FieldSignalName(string signalPrefix, string tool, string field) =>
        FieldSignal(SafeSignalPrefix(signalPrefix), tool, field);

    internal static string SourceSignalName(string signalPrefix, string tool) =>
        DraftSignal(SafeSignalPrefix(signalPrefix), tool + "_sourceid");

    internal static string SourceDeliveryId(string signalPrefix, string tool) =>
        "dashboard-source-delivery-" + SafeSignalPrefix(signalPrefix) + "-" + tool;

    internal static string DocumentSignalName(string signalPrefix, string tool, int index, string part) =>
        DocSignal(SafeSignalPrefix(signalPrefix), tool, index, part);

    internal static string TabKey(string tabId) =>
        tabId == "server" ? "server" : "t" + tabId.Replace("-", "");

    internal static string DraftKey(string signalPrefix) => SafeSignalPrefix(signalPrefix);

    private static readonly string[] DatalistKinds =
    {
        "device", "cpu", "hardwareType", "block", "udt", "tagTable", "entry", "technologyObject", "object",
        "blockGroup", "typeGroup", "tagTableGroup", "technologyObjectGroup",
        "blockGroupPath", "typeGroupPath", "tagTableGroupPath", "technologyObjectGroupPath"
    };

    private static bool ForTab(McpToolDefinition tool, bool isServerTab) =>
        isServerTab ? tool.Name is "list_tia_processes" or "get_status" :
        tool.Name == "get_status" || tool.InputSchema.Properties.ContainsKey("processId");

    private static void RenderPicker(StringBuilder html, IReadOnlyList<McpToolDefinition> reads,
        IReadOnlyList<McpToolDefinition> writes, string signal)
    {
        html.Append("<label class=\"tool-operation-picker\">Operation<select aria-label=\"MCP operation\" data-bind=\"")
            .Append(signal).Append("\">");
        RenderPickerGroup(html, reads, "Read");
        RenderPickerGroup(html, writes, "Modify");
        html.Append("</select></label>");
    }

    private static void RenderPickerGroup(StringBuilder html, IReadOnlyList<McpToolDefinition> tools, string label)
    {
        if (tools.Count == 0) return;
        html.Append("<optgroup label=\"").Append(label).Append("\">");
        foreach (var tool in tools)
        {
            var friendlyLabel = Labels.TryGetValue(tool.Name, out var friendly) ? friendly : tool.Name;
            html.Append("<option value=\"").Append(Encode(tool.Name)).Append("\">")
                .Append(Encode(friendlyLabel)).Append(" · ").Append(Encode(tool.Name)).Append("</option>");
        }
        html.Append("</optgroup>");
    }

    private static void RenderLiveForm(StringBuilder html, McpToolDefinition tool, string ui,
        string prefix, string originalPrefix, bool isServerTab, bool projectReady)
    {
        var indicator = DraftSignal(prefix, tool.Name + "_running");
        var sourceIndicator = tool.Name is "write_blocks" or "write_udts"
            ? DraftSignal(prefix, tool.Name + "_source_running") : null;
        var enabled = isServerTab || projectReady || tool.Name == "get_status";
        html.Append("<form class=\"tool-form\" data-tool=\"").Append(Encode(tool.Name))
            .Append("\" data-write=\"").Append(tool.Annotations.ReadOnlyHint ? "false" : "true")
            .Append("\" data-show=\"").Append(Encode("$" + ui + ".tool === " + JsString(tool.Name)))
            .Append("\" data-preserve-attr=\"style\"");
        if (tool.Name != (isServerTab ? "list_tia_processes" : "list_devices"))
            html.Append(" style=\"display:none\"");
        html.Append(" data-indicator=\"").Append(indicator).Append('"');
        var fields = new List<string>();
        foreach (var property in tool.InputSchema.Properties)
        {
            if (property.Key == "processId") continue;
            fields.Add(JsString(property.Key) + ":" +
                (property.Key == "documents" ? DocumentPayload(prefix, tool.Name, property.Value) :
                    "$" + FieldSignal(prefix, tool.Name, property.Key)));
        }
        var expression = "$" + ui + ".followLatest=true; $" + ui + ".pinnedRunId=''; " +
            "@post('/api/dashboard/tools/run', {payload:{tabId:$_ui.selectedTabId,contextStamp:" + JsString(originalPrefix) +
            ",requestId:crypto.randomUUID(),name:" +
            JsString(tool.Name) + ",fields:{" + string.Join(",", fields) +
            "}},headers:{'X-Tia-Dashboard':'1'},retry:'never',requestCancellation:'disabled'})";
        html.Append(" data-on:submit=\"").Append(Encode(expression)).Append("\">");
        html.Append("<details class=\"tool-description\"><summary>Tool description</summary><p>")
            .Append(Encode(tool.Description)).Append("</p></details>");
        // The source response patches bound editor signals. Freeze the entire
        // write draft until that response has been consumed by Datastar.
        if (sourceIndicator != null)
            html.Append("<fieldset class=\"source-load-lock\" data-attr:disabled=\"$")
                .Append(sourceIndicator).Append("\">");
        foreach (var property in tool.InputSchema.Properties)
        {
            if (property.Key == "processId") continue;
            if (property.Key == "documents") RenderDocumentField(html, tool, property.Value, prefix);
            else RenderLiveField(html, tool, property.Key, property.Value, prefix, originalPrefix, ui + ".tool");
        }
        if (tool.Name is "write_blocks" or "write_udts")
            RenderSourceHelper(html, tool.Name, prefix, originalPrefix, ui, enabled);
        var label = Labels.TryGetValue(tool.Name, out var friendly) ? friendly : tool.Name;
        html.Append("<button type=\"submit\" data-attr:disabled=\"")
            .Append(Encode("$_server.busy || $_ui.connectionBusy || $" + indicator +
                (sourceIndicator == null ? "" : " || $" + sourceIndicator) +
                (enabled ? "" : " || true")))
            .Append("\">").Append(Encode(label)).Append("</button>");
        if (sourceIndicator != null) html.Append("</fieldset>");
        html.Append("</form>");
    }

    private static string DocumentPayload(string prefix, string tool, Dictionary<string, object> schema)
    {
        var first = "{name:$" + DocSignal(prefix, tool, 1, "name") +
            ",content:$" + DocSignal(prefix, tool, 1, "content") + "}";
        if (MaxDocuments(schema) == 1) return "JSON.stringify([" + first + "])";
        var secondName = "$" + DocSignal(prefix, tool, 2, "name");
        var secondContent = "$" + DocSignal(prefix, tool, 2, "content");
        var second = "{name:" + secondName + ",content:" + secondContent + "}";
        return "JSON.stringify((" + secondName + " || " + secondContent + ") ? [" + first + "," + second + "] : [" + first + "])";
    }

    private static int MaxDocuments(Dictionary<string, object> schema) =>
        schema.TryGetValue("maxItems", out var value) && value is int count ? count : 1;

    private static string DocSignal(string prefix, string tool, int index, string part) =>
        DraftSignal(prefix, tool + "_doc" + index + "_" + part);

    private static void RenderDocumentField(StringBuilder html, McpToolDefinition tool,
        Dictionary<string, object> schema, string prefix)
    {
        var schemaJson = JsonSerializer.SerializeToElement(schema);
        var children = schemaJson.GetProperty("items").GetProperty("properties");
        var nameHelp = Help(children.GetProperty("name"), true);
        var contentHelp = Help(children.GetProperty("content"), true);
        html.Append("<fieldset name=\"documents\" data-type=\"documents\" data-required=\"true\"")
            .Append(" data-document-name-help=\"").Append(Encode(nameHelp))
            .Append("\" data-document-content-help=\"").Append(Encode(contentHelp))
            .Append("\"><legend>Source documents</legend>");
        RenderDocumentSlot(html, tool.Name, prefix, 1, nameHelp, contentHelp, required: true);
        if (MaxDocuments(schema) > 1)
        {
            var secondName = "$" + DocSignal(prefix, tool.Name, 2, "name");
            var secondContent = "$" + DocSignal(prefix, tool.Name, 2, "content");
            html.Append("<details class=\"source-editor\" data-attr:open=\"")
                .Append(Encode("!!(" + secondName + " || " + secondContent + ")"))
                .Append("\"><summary>Optional second resource document (.s7res)</summary>");
            RenderDocumentSlot(html, tool.Name, prefix, 2, nameHelp, contentHelp, required: false);
            html.Append("</details>");
        }
        html.Append("<small class=\"field-help\">").Append(Encode(Help(schemaJson, true)))
            .Append("</small></fieldset>");
    }

    private static void RenderDocumentSlot(StringBuilder html, string tool, string prefix, int index,
        string nameHelp, string contentHelp, bool required)
    {
        html.Append("<div class=\"source-editor\"><label>Document ").Append(index)
            .Append(" name<input type=\"text\" name=\"documents[").Append(index - 1)
            .Append("].name\" data-bind=\"").Append(DocSignal(prefix, tool, index, "name"))
            .Append('"');
        if (required) html.Append(" required");
        html.Append("><small class=\"field-help\">").Append(Encode(nameHelp))
            .Append("</small></label><label>Document ").Append(index)
            .Append(" content<textarea name=\"documents[").Append(index - 1)
            .Append("].content\" class=\"mono\" rows=\"16\" spellcheck=\"false\" data-bind=\"")
            .Append(DocSignal(prefix, tool, index, "content")).Append('"');
        if (required) html.Append(" required");
        html.Append("></textarea><small class=\"field-help\">").Append(Encode(contentHelp))
            .Append("</small></label></div>");
    }

    private static void RenderSourceHelper(StringBuilder html, string tool, string prefix,
        string originalPrefix, string ui, bool enabled)
    {
        var read = tool == "write_blocks" ? "get_block" : "get_udt";
        var inventory = tool == "write_blocks" ? "list_blocks" : "list_udts";
        var listKind = tool == "write_blocks" ? "block" : "udt";
        var sourceSignal = DraftSignal(prefix, tool + "_sourceid");
        var indicator = DraftSignal(prefix, tool + "_source_running");
        var listExpression = "$" + DraftSignal(prefix, "cpu") + " === $" + DraftSignal(prefix, "inventorycpu") + " ? " +
            JsString(DatalistId(originalPrefix, listKind)) + " : null";
        var expression = "$" + ui + ".followLatest=true; $" + ui + ".pinnedRunId=''; " +
            "@post('/api/dashboard/tools/run', {payload:{tabId:$_ui.selectedTabId,contextStamp:" + JsString(originalPrefix) +
            ",requestId:crypto.randomUUID(),name:" +
            JsString(read) + ",loadSourceFor:" + JsString(tool) + ",fields:{objectId:$" + sourceSignal +
            ",includeSource:true,includePath:false,sourceFormat:'best'}},headers:{'X-Tia-Dashboard':'1'},retry:'never',requestCancellation:'disabled'})";
        html.Append("<div class=\"tool-source-helper\"><label>Existing source object ID<input type=\"text\" data-bind=\"")
            .Append(sourceSignal).Append("\" data-attr:list=\"").Append(Encode(listExpression))
            .Append("\"></label><div class=\"tool-field-actions\"><button type=\"button\" data-on:click=\"")
            .Append(Encode("$" + ui + ".tool=" + JsString(inventory)))
            .Append("\">Go to ").Append(Encode(inventory)).Append("</button><button type=\"button\" data-indicator=\"")
            .Append(indicator).Append("\" data-attr:disabled=\"")
            .Append(Encode("$_server.busy || $_ui.connectionBusy || $" + indicator + " || !$" + sourceSignal +
                (enabled ? "" : " || true")))
            .Append("\" data-on:click=\"").Append(Encode(expression))
            .Append("\">Load selected source</button></div><small class=\"field-help\">Read the full native source into the documents editor before editing. Loading only reads; submit the write separately.</small>")
            .Append("<div id=\"").Append(SourceDeliveryId(originalPrefix, tool)).Append("\" hidden></div></div>");
    }

    private static void RenderLiveField(StringBuilder html, McpToolDefinition tool, string name,
        Dictionary<string, object> schema, string prefix, string originalPrefix, string toolSignal)
    {
        var schemaJson = JsonSerializer.SerializeToElement(schema);
        var type = schema.TryGetValue("type", out var kind) && kind is string value ? value : "json";
        var required = Required(tool, name);
        schema.TryGetValue("default", out var fallback);
        var signal = FieldSignal(prefix, tool.Name, name);
        var listKind = ListKind(tool.Name, name);
        html.Append("<label>").Append(Encode(name)).Append(' ');
        if (schema.TryGetValue("enum", out var choicesValue) && choicesValue is string[] choices)
        {
            html.Append("<select name=\"").Append(Encode(name)).Append("\" data-type=\"string\" data-bind=\"")
                .Append(signal).Append('"');
            if (name == "sourceFormat" && tool.InputSchema.AllOf != null)
                html.Append(" data-on:change=\"")
                    .Append(Encode("evt.target.value !== 'external-source' && ($" +
                        FieldSignal(prefix, tool.Name, "includeDependencies") + " = false)"))
                    .Append('"');
            if (required) html.Append(" required data-required=\"true\"");
            html.Append('>');
            if (required && fallback == null) html.Append("<option value=\"\">Choose…</option>");
            else if (!required && fallback == null) html.Append("<option value=\"\">Omit</option>");
            foreach (var choice in choices)
            {
                html.Append("<option value=\"").Append(Encode(choice)).Append('"');
                if (fallback is string selected && selected == choice) html.Append(" selected");
                html.Append('>').Append(Encode(choice)).Append("</option>");
            }
            html.Append("</select>");
        }
        else if (type == "boolean")
        {
            var on = fallback is true;
            html.Append("<input name=\"").Append(Encode(name))
                .Append("\" type=\"checkbox\" data-type=\"boolean\" data-bind=\"").Append(signal)
                .Append("\" data-default=\"").Append(on ? "true" : "false").Append('"');
            if (name == "includeSource" && tool.InputSchema.AllOf != null)
                html.Append(" data-on:change=\"")
                    .Append(Encode("!evt.target.checked && ($" +
                        FieldSignal(prefix, tool.Name, "includeDependencies") + " = false)"))
                    .Append('"');
            if (on) html.Append(" checked");
            if (name == "includeDependencies" && tool.InputSchema.AllOf != null)
                html.Append(" data-enabled-when=\"includeSource=true,sourceFormat=external-source\" data-attr:disabled=\"")
                    .Append(Encode("!($" + FieldSignal(prefix, tool.Name, "includeSource") +
                        " && $" + FieldSignal(prefix, tool.Name, "sourceFormat") + " === 'external-source')"))
                    .Append('"');
            html.Append('>');
        }
        else if (type == "array" || type == "json")
        {
            var fieldType = type == "array" && name == "documents" ? "documents" : "json";
            html.Append("<textarea name=\"").Append(Encode(name)).Append("\" data-type=\"")
                .Append(fieldType).Append("\" data-bind=\"").Append(signal).Append('"');
            if (required) html.Append(" required data-required=\"true\"");
            html.Append(" rows=\"").Append(name == "documents" ? "16" : "7")
                .Append("\" spellcheck=\"false\"");
            if (name == "documents") html.Append(" class=\"mono\"");
            html.Append(" placeholder=\"").Append(name == "documents" ? "[{\"name\":\"Example.scl\",\"content\":\"...\"}]" : "JSON value")
                .Append("\"></textarea>");
        }
        else
        {
            html.Append("<input name=\"").Append(Encode(name)).Append("\" type=\"")
                .Append(type == "integer" ? "number" : "text")
                .Append("\" data-type=\"").Append(Encode(type)).Append("\" data-bind=\"")
                .Append(signal).Append('"');
            if (required) html.Append(" required data-required=\"true\"");
            if (type == "integer")
            {
                html.Append(" step=\"1\"");
                if (fallback != null) html.Append(" value=\"").Append(Encode(fallback.ToString())).Append('"');
                if (schema.TryGetValue("minimum", out var minimum)) html.Append(" min=\"").Append(minimum).Append('"');
                if (schema.TryGetValue("maximum", out var maximum)) html.Append(" max=\"").Append(maximum).Append('"');
            }
            if (listKind != null)
            {
                var listId = DatalistId(originalPrefix, listKind);
                if (listKind is "cpu" or "device" or "hardwareType")
                    html.Append(" list=\"").Append(listId).Append('"');
                else
                {
                    var listExpression = "$" + DraftSignal(prefix, "cpu") + " === $" + DraftSignal(prefix, "inventorycpu") + " ? " + JsString(listId) + " : null";
                    html.Append(" data-attr:list=\"").Append(Encode(listExpression)).Append('"');
                }
            }
            else if (name is "groupObjectId" or "groupPath" && tool.InputSchema.Properties.ContainsKey("kind"))
            {
                var kindSignal = "$" + FieldSignal(prefix, tool.Name, "kind");
                var suffix = name == "groupPath" ? "Path" : "";
                var listExpression = "$" + DraftSignal(prefix, "cpu") + " === $" + DraftSignal(prefix, "inventorycpu") + " ? (" +
                    kindSignal + " === 'block' ? " + JsString(DatalistId(originalPrefix, "blockGroup" + suffix)) + " : " +
                    kindSignal + " === 'udt' ? " + JsString(DatalistId(originalPrefix, "typeGroup" + suffix)) + " : " +
                    kindSignal + " === 'tagTable' ? " + JsString(DatalistId(originalPrefix, "tagTableGroup" + suffix)) + " : " +
                    kindSignal + " === 'technologyObject' ? " + JsString(DatalistId(originalPrefix, "technologyObjectGroup" + suffix)) + " : null) : null";
                html.Append(" data-attr:list=\"").Append(Encode(listExpression)).Append('"');
            }
            html.Append('>');
        }
        html.Append("<small class=\"field-help\"");
        if (schemaJson.TryGetProperty("items", out var items) && items.TryGetProperty("properties", out var children))
            foreach (var child in children.EnumerateObject())
                html.Append(" data-document-").Append(Encode(child.Name)).Append("-help=\"")
                    .Append(Encode(Help(child.Value, true))).Append('"');
        html.Append('>').Append(Encode(Help(schemaJson, required))).Append("</small></label>");
        if (name is "groupObjectId" or "groupPath" && tool.InputSchema.Properties.ContainsKey("kind"))
        {
            var kindSignal = "$" + FieldSignal(prefix, tool.Name, "kind");
            var navigation = "$" + toolSignal + "=(" + kindSignal +
                "==='block'?'list_blocks':" + kindSignal + "==='udt'?'list_udts':" +
                kindSignal + "==='tagTable'?'list_tag_tables':'list_technology_objects')";
            html.Append("<div class=\"tool-field-actions\"><button type=\"button\" data-attr:disabled=\"")
                .Append(Encode("!" + kindSignal)).Append("\" data-on:click=\"")
                .Append(Encode(navigation))
                .Append("\">Go to matching group inventory</button></div>");
        }
        else if (InventoryTool(tool.Name, name) is string inventory)
            html.Append("<div class=\"tool-field-actions\"><button type=\"button\" data-on:click=\"")
                .Append(Encode("$" + toolSignal + "=" + JsString(inventory)))
                .Append("\">Go to ").Append(Encode(inventory)).Append("</button></div>");
    }

    private static string FieldSignal(string prefix, string tool, string field) =>
        DraftSignal(prefix, field == "plcObjectId" ? "cpu" : tool + "_" + field.ToLowerInvariant());

    private static string DraftSignal(string prefix, string field) => "_drafts." + prefix + "." + field;

    private static string? ListKind(string tool, string field)
    {
        if (tool == "create_device" && field == "typeIdentifier") return "hardwareType";
        if (field == "plcObjectId") return "cpu";
        if (field is "groupObjectId" or "groupPath")
        {
            var suffix = field == "groupPath" ? "Path" : "";
            return tool switch
        {
            "write_blocks" => "blockGroup" + suffix, "write_udts" => "typeGroup" + suffix,
            "create_tag_table" or "import_tag_tables" => "tagTableGroup" + suffix,
            "create_technology_object" => "technologyObjectGroup" + suffix, _ => null
        };
        }
        if (field != "objectId") return null;
        return tool switch
        {
            "get_device" or "delete_device" => "device",
            "get_block" or "delete_block" => "block",
            "get_udt" or "delete_udt" => "udt",
            "get_tag_table" or "export_tag_table" or "create_tag" or "create_user_constant" or "delete_tag_table" => "tagTable",
            "set_tag_entry_attribute" or "delete_tag_entry" => "entry",
            "get_technology_object" or "set_technology_object_parameters" => "technologyObject",
            "get_cross_references" or "rename" => "object",
            _ => null
        };
    }

    private static string? InventoryTool(string tool, string field)
    {
        if (tool == "create_device" && field == "typeIdentifier") return "search_hardware_catalog";
        if (field == "plcObjectId") return "get_device";
        if (field == "groupObjectId" || field == "groupPath") return tool switch
        {
            "write_blocks" => "list_blocks", "write_udts" => "list_udts",
            "create_technology_object" => "list_technology_objects", _ => "list_tag_tables"
        };
        if (field != "objectId") return null;
        return tool switch
        {
            "get_device" or "delete_device" => "list_devices",
            "get_block" or "delete_block" => "list_blocks",
            "get_udt" or "delete_udt" => "list_udts",
            "get_tag_table" or "export_tag_table" or "delete_tag_table" or "create_tag" or "create_user_constant" => "list_tag_tables",
            "set_tag_entry_attribute" or "delete_tag_entry" => "get_tag_table",
            "get_technology_object" or "set_technology_object_parameters" => "list_technology_objects",
            _ => null
        };
    }

    private static string SafeSignalPrefix(string value)
    {
        var html = new StringBuilder("f");
        foreach (var c in value)
        {
            var safe = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c :
                c is >= 'A' and <= 'Z' ? char.ToLowerInvariant(c) : '_';
            if (safe != '_' || html[html.Length - 1] != '_') html.Append(safe);
        }
        return html.ToString();
    }

    private static string JsString(string value) => JsonSerializer.Serialize(value);

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
