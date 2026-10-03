using System.Net;
using System.Text;
using System.Text.Json;

namespace TiaOpennessMcpServer.Dashboard;

// One escaped renderer serves action responses, history selection and the
// ongoing dashboard stream. HTML is derived only from managed captures.
internal static class DashboardRunFragments
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string RenderStatus(DashboardRunCapture? run, bool unavailable = false)
    {
        var label = unavailable ? "Unavailable" : run == null ? "No run selected" : run.Outcome switch
        {
            "running" => "Running…",
            "success" => "Success",
            "partial" => "Partial",
            _ => "Error"
        };
        var css = unavailable ? " warn" : run?.Outcome switch
        {
            "running" => " warn",
            "success" => " good",
            "partial" => " warn",
            "error" => " error",
            _ => ""
        };
        return "<div id=\"run-status\" class=\"badge" + css +
            "\" role=\"status\">" + H(label) + "</div>";
    }

    internal static string RenderInspector(DashboardRunCapture? run, string tabId, string? oneShotIdentity = null,
        long oneShotVersion = 0)
    {
        var html = new StringBuilder("<div id=\"run-inspector-content\" class=\"run-inspector run-inspector-shell\"");
        if (oneShotIdentity != null)
            html.Append(" data-one-shot-delivery=\"").Append(H(oneShotIdentity))
                .Append("\" data-one-shot-version=\"").Append(oneShotVersion)
                .Append("\" data-one-shot-tab=\"").Append(H(tabId))
                .Append("\" data-signals=\"").Append(H(DashboardClientState.NestSignals(
                    new Dictionary<string, object?> { ["_ui.inspector.oneShotIdentity"] = oneShotIdentity }))).Append('"');
        if (run != null)
            html.Append(" data-run-id=\"").Append(H(run.Id)).Append("\" data-request-id=\"").Append(H(run.Id))
                .Append("\" data-run-tool=\"").Append(H(run.Operation)).Append("\" data-run-tab-id=\"")
                .Append(H(run.TabId)).Append("\" data-run-failed=\"")
                .Append(run.Outcome == "error" ? "true" : "false").Append("\" data-run-partial=\"")
                .Append(run.Outcome == "partial" ? "true" : "false").Append('"');
        html.Append("><div class=\"inspector-summary\" tabindex=\"0\">").Append(RenderStatus(run));
        html.Append("<p class=\"hint\">");
        if (run == null)
            html.Append("Choose Latest or a saved run.");
        else
        {
            html.Append(H(run.Operation)).Append(" · ").Append(H(run.StartedAtUtc.ToString("u")));
            if (run.ProcessId is int pid) html.Append(" · PID ").Append(pid);
        }
        html.Append("</p></div>");
        AppendInspectorToolbar(html);
        html.Append("<div id=\"run-inspector-body\" class=\"inspector-body\" tabindex=\"0\" aria-label=\"Capture content\">");
        if (run == null)
        {
            AppendInspectorViews(html, "Run a tool or choose a saved run to inspect it.",
                "No request has been selected.", "No response has been selected.");
            return html.Append("</div></div>").ToString();
        }
        if (!string.IsNullOrEmpty(run.Error))
            html.Append("<p class=\"notice error\">").Append(H(run.Error)).Append("</p>");
        if (!run.PayloadRetained)
            html.Append("<p class=\"notice\">The capture exceeded the server's 64 MiB history budget. Its metadata remains, but its request and response payloads were not retained.</p>");
        var result = ResultText(run.ResponseJson);
        if (run.Outcome == "running") result = "The tool is running. The result will appear here when it completes.";
        if (!run.PayloadRetained && run.Completed && run.ResponseJson == null)
            result = "Payload not retained; inspect the current TIA project before repeating a modifying call.";
        AppendInspectorViews(html, result, run.RequestJson ?? "Payload not retained.",
            run.ResponseJson ?? "No response retained.");
        return html.Append("</div></div>").ToString();
    }

    internal static string RenderUnavailable()
    {
        var html = new StringBuilder("<div id=\"run-inspector-content\" class=\"run-inspector run-inspector-shell\"><div class=\"inspector-summary\" tabindex=\"0\">")
            .Append(RenderStatus(null, unavailable: true))
            .Append("<p class=\"hint\">The selected run is no longer retained.</p></div>");
        AppendInspectorToolbar(html);
        html.Append("<div id=\"run-inspector-body\" class=\"inspector-body\" tabindex=\"0\" aria-label=\"Capture content\"><p class=\"notice\">This run is no longer in server history. Choose another run or select Latest.</p>");
        AppendInspectorViews(html, "No result is available for this run.",
            "No request is available for this run.", "No response is available for this run.");
        return html.Append("</div></div>").ToString();
    }

    private static void AppendInspectorToolbar(StringBuilder html)
    {
        html.Append("<div class=\"segmented\" data-inspector-tabs>");
        InspectorButton(html, "result", "Result");
        InspectorButton(html, "request", "Request");
        InspectorButton(html, "response", "Response");
        html.Append("</div>");
    }

    private static void AppendInspectorViews(StringBuilder html, string result, string request, string response)
    {
        html.Append("<pre data-run-result data-show=\"$_ui.inspectorView === 'result'\" data-preserve-attr=\"style\">")
            .Append(H(result)).Append("</pre>")
            .Append("<pre data-run-request data-show=\"$_ui.inspectorView === 'request'\" data-preserve-attr=\"style\" style=\"display:none\">")
            .Append(H(request)).Append("</pre>")
            .Append("<pre data-run-response data-show=\"$_ui.inspectorView === 'response'\" data-preserve-attr=\"style\" style=\"display:none\">")
            .Append(H(response)).Append("</pre>");
    }

    private static void InspectorButton(StringBuilder html, string view, string label)
    {
        html.Append("<button type=\"button\" data-inspector-view=\"").Append(view)
            .Append("\" data-on:click=\"$_ui.inspectorView = el.dataset.inspectorView\"")
            .Append(" data-attr:aria-pressed=\"String($_ui.inspectorView === el.dataset.inspectorView)\"")
            .Append(">").Append(label).Append("</button>");
    }

    internal static string RenderHistory(string tabId, IReadOnlyList<DashboardRunCapture> runs)
    {
        var ui = "$_ui.tabs." + DashboardToolForms.TabKey(tabId);
        var html = new StringBuilder("<div id=\"run-history-").Append(H(tabId))
            .Append("\" class=\"run-history\" data-preserve-attr=\"style\" data-tab-id=\"").Append(H(tabId))
            .Append("\" data-show=\"$_ui.selectedTabId === el.dataset.tabId\"");
        if (tabId != "server") html.Append(" style=\"display:none\"");
        html.Append('>');
        var relevant = runs.Where(run => run.TabId == tabId).ToArray();
        if (relevant.Length == 0)
            html.Append("<p class=\"muted\">No runs captured here yet.</p>");
        foreach (var run in relevant)
        {
            html.Append("<button type=\"button\" class=\"run-row\" id=\"history-").Append(H(run.Id))
                .Append("\" data-on:click=\"").Append(H(ui + ".followLatest=false; " + ui +
                    ".pinnedRunId=el.dataset.historyRunId; $_ui.inspectorView='result'"))
                .Append("\" data-attr:aria-pressed=\"").Append(H("String(!" + ui + ".followLatest && " + ui + ".pinnedRunId === el.dataset.historyRunId)")).Append('"')
                .Append(" data-history-run-id=\"").Append(H(run.Id)).Append("\" data-history-tab-id=\"")
                .Append(H(tabId)).Append("\"><small>").Append(H(run.StartedAtUtc.ToLocalTime().ToString("g")))
                .Append("</small><span class=\"run-name\">").Append(H(run.Operation))
                .Append(run.ParentRunId == null ? "" : " · readback")
                .Append("</span><span class=\"").Append(run.Outcome == "error" ? "error" : run.Outcome == "partial" ? "partial" : "")
                .Append("\">").Append(H(run.Outcome)).Append("</span></button>");
        }
        return html.Append("</div>").ToString();
    }

    // A source read can fill the matching write editor only while its original
    // tab still names the same runtime, attachment and project. Exact source
    // text crosses to Datastar as a JSON string, without client-side parsing.
    internal static string? SourceSignals(DashboardRunCompletion completion, DashboardSnapshot snapshot,
        DashboardSelectorStore.RunContext context, string? loadSourceFor)
    {
        if (completion.Capture.Outcome != "success" ||
            (loadSourceFor != "write_blocks" && loadSourceFor != "write_udts") ||
            completion.Capture.TabId != context.TabId ||
            completion.Capture.Operation != (loadSourceFor == "write_blocks" ? "get_block" : "get_udt"))
            return null;
        var tab = snapshot.Tabs.FirstOrDefault(item => item.Id == context.TabId);
        if (tab == null || snapshot.Epoch != context.Epoch ||
            DashboardSelectorStore.Stamp(tab) != context.Stamp)
            return null;
        try
        {
            using var response = JsonDocument.Parse(completion.ResponseJson);
            var result = response.RootElement.GetProperty("result");
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                return null;
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            using var payload = JsonDocument.Parse(text ?? "{}");
            if (!payload.RootElement.TryGetProperty("source", out var source) ||
                source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty("documents", out var documents) ||
                documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() is < 1 or > 2 ||
                !source.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                format.GetString() is not ("external-source" or "simatic-sd" or "simatic-ml"))
                return null;
            var values = new List<(string Name, string Content)>();
            foreach (var document in documents.EnumerateArray())
            {
                var name = document.GetProperty("name").GetString();
                var content = document.GetProperty("content").GetString();
                if (name == null || string.IsNullOrWhiteSpace(name) || content == null) return null;
                values.Add((name, content));
            }
            var prefix = DashboardSelectorStore.SignalPrefix(snapshot, tab);
            var signals = new Dictionary<string, object?>
            {
                [DashboardToolForms.FieldSignalName(prefix, loadSourceFor, "sourceFormat")] = format.GetString()
            };
            for (var index = 1; index <= 2; index++)
            {
                var (name, content) = index <= values.Count ? values[index - 1] : ("", "");
                signals[DashboardToolForms.DocumentSignalName(prefix, loadSourceFor, index, "name")] = name;
                signals[DashboardToolForms.DocumentSignalName(prefix, loadSourceFor, index, "content")] = content;
            }
            return DashboardClientState.NestSignals(signals);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        { return null; }
    }

    // The response can arrive after the event stream removed its source form.
    // Target a context-owned slot and require the branch to still exist, so a
    // late source response cannot recreate a discarded draft namespace.
    internal static string RenderSourceDelivery(string signalsJson, string contextPrefix, string writeTool)
    {
        var key = DashboardToolForms.DraftKey(contextPrefix);
        using var signals = JsonDocument.Parse(signalsJson);
        var source = signals.RootElement.GetProperty("_drafts").GetProperty(key);
        var names = new List<string>
        {
            DashboardToolForms.FieldSignalName(contextPrefix, writeTool, "sourceFormat").Split('.').Last()
        };
        for (var index = 1; index <= 2; index++)
            foreach (var part in new[] { "name", "content" })
                names.Add(DashboardToolForms.DocumentSignalName(contextPrefix, writeTool, index, part).Split('.').Last());
        var fields = JsonSerializer.Serialize(names.ToDictionary(name => name, name => source.GetProperty(name)));
        // Keep the effect distinct for every response, including repeated loads
        // of identical source after the user edited the bound document.
        var deliveryId = Guid.NewGuid().ToString("N");
        var encodedKey = JsonSerializer.Serialize(key);
        var effect = "@peek(() => {if (el.dataset.deliveryId === " + JsonSerializer.Serialize(deliveryId) +
            " && $_server.draftKeys.includes(" + encodedKey + ") && Object.keys($_drafts).includes(" + encodedKey +
            ")) Object.assign($_drafts." + key + "," + fields + ");})";
        return "<div id=\"" + H(DashboardToolForms.SourceDeliveryId(contextPrefix, writeTool)) +
            "\" hidden data-delivery-id=\"" + H(deliveryId) + "\" data-effect=\"" + H(effect) + "\"></div>";
    }

    private static string ResultText(string? responseJson)
    {
        if (responseJson == null) return "No response has been captured.";
        try
        {
            using var response = JsonDocument.Parse(responseJson);
            var root = response.RootElement;
            if (root.TryGetProperty("result", out var result) && result.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0 &&
                content[0].TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return FormatJsonForDisplay(text.GetString() ?? "");
        }
        catch (JsonException) { }
        return FormatJsonForDisplay(responseJson);
    }

    private static string FormatJsonForDisplay(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException) { return value; }
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
}
