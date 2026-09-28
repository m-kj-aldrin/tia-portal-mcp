using System.Net;
using System.Text;
using System.Text.Json;

namespace TiaOpennessMcpServer.Dashboard;

// One escaped renderer serves action responses, history selection and the
// ongoing dashboard stream. HTML is derived only from managed captures.
internal static class DashboardRunFragments
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    internal static string RenderViews(DashboardSnapshot tabs, DashboardRunStore store)
    {
        var html = new StringBuilder("<div id=\"dashboard-run-views\">");
        foreach (var tab in tabs.Tabs) html.Append(RenderTabViews(tab, store));
        return html.Append("</div>").ToString();
    }

    internal static string RenderTabViews(DashboardTabView tab, DashboardRunStore store)
    {
        var latest = store.LatestForTab(tab.Id);
        return new StringBuilder("<section class=\"dashboard-run-view\" data-run-view-tab=\"")
            .Append(H(tab.Id)).Append("\" id=\"run-view-").Append(H(tab.Id)).Append("\">")
            .Append(RenderStatus(latest, tab.Id))
            .Append(RenderInspector(latest, tab.Id))
            .Append(RenderHistory(tab.Id, store.Snapshot()))
            .Append("</section>").ToString();
    }

    internal static string RenderStatus(DashboardRunCapture? run, string tabId)
    {
        var label = run == null ? "No run selected" : run.Outcome switch
        {
            "running" => "Running…",
            "success" => "Success",
            "partial" => "Partial",
            _ => "Error"
        };
        var css = run?.Outcome switch
        {
            "running" => " warn",
            "success" => " good",
            "partial" => " warn",
            "error" => " error",
            _ => ""
        };
        return "<div id=\"run-status-" + H(tabId) + "\" class=\"badge" + css +
            "\" role=\"status\">" + H(label) + "</div>";
    }

    internal static string RenderInspector(DashboardRunCapture? run, string tabId)
    {
        var html = new StringBuilder("<div id=\"run-inspector-").Append(H(tabId)).Append("\" class=\"run-inspector\"");
        if (run == null)
            return html.Append("><p class=\"hint\">Run a tool or choose a saved run to inspect it.</p></div>").ToString();
        html.Append(" data-run-id=\"").Append(H(run.Id)).Append("\" data-request-id=\"").Append(H(run.Id))
            .Append("\" data-run-tool=\"").Append(H(run.Operation)).Append("\" data-run-tab-id=\"")
            .Append(H(run.TabId)).Append("\" data-run-failed=\"")
            .Append(run.Outcome == "error" ? "true" : "false").Append("\" data-run-partial=\"")
            .Append(run.Outcome == "partial" ? "true" : "false").Append("\">");
        html.Append("<p class=\"hint\">").Append(H(run.Operation)).Append(" · ")
            .Append(H(run.StartedAtUtc.ToString("u")));
        if (run.ProcessId is int pid) html.Append(" · PID ").Append(pid);
        html.Append("</p>");
        if (!string.IsNullOrEmpty(run.Error))
            html.Append("<p class=\"error\">").Append(H(run.Error)).Append("</p>");
        if (!run.PayloadRetained)
            html.Append("<p class=\"notice\">The capture exceeded the server's 64 MiB history budget. Its metadata remains, but its request and response payloads were not retained.</p>");
        var result = ResultText(run.ResponseJson);
        if (run.Outcome == "running") result = "The tool is running. The result will appear here when it completes.";
        if (!run.PayloadRetained && run.Completed && run.ResponseJson == null)
            result = "Payload not retained; inspect the current TIA project before repeating a modifying call.";
        html.Append("<div class=\"segmented\" data-inspector-tabs>")
            .Append("<button type=\"button\" data-inspector-view=\"result\" aria-pressed=\"true\">Result</button>")
            .Append("<button type=\"button\" data-inspector-view=\"request\" aria-pressed=\"false\">Request</button>")
            .Append("<button type=\"button\" data-inspector-view=\"response\" aria-pressed=\"false\">Response</button></div>");
        html.Append("<pre data-run-result tabindex=\"0\">").Append(H(result)).Append("</pre>")
            .Append("<pre data-run-request tabindex=\"0\" hidden>").Append(H(run.RequestJson ?? "Payload not retained.")).Append("</pre>")
            .Append("<pre data-run-response tabindex=\"0\" hidden>").Append(H(run.ResponseJson ?? "No response retained.")).Append("</pre>")
            .Append("</div>");
        return html.ToString();
    }

    internal static string RenderHistory(string tabId, IReadOnlyList<DashboardRunCapture> runs)
    {
        var html = new StringBuilder("<div id=\"run-history-").Append(H(tabId)).Append("\" class=\"run-history\">");
        var relevant = runs.Where(run => run.TabId == tabId).ToArray();
        if (relevant.Length == 0)
            html.Append("<p class=\"muted\">No runs captured here yet.</p>");
        foreach (var run in relevant)
        {
            var url = "/api/dashboard/runs/view?tabId=" + Uri.EscapeDataString(tabId) + "&runId=" + Uri.EscapeDataString(run.Id);
            html.Append("<button type=\"button\" class=\"run-row\" data-history-url=\"").Append(H(url))
                .Append("\" data-history-run-id=\"").Append(H(run.Id)).Append("\" data-history-tab-id=\"")
                .Append(H(tabId)).Append("\"><small>").Append(H(run.StartedAtUtc.ToLocalTime().ToString("g")))
                .Append("</small><span class=\"run-name\">").Append(H(run.Operation))
                .Append("</span><span class=\"").Append(run.Outcome == "error" ? "error" : run.Outcome == "partial" ? "partial" : "")
                .Append("\">").Append(H(run.Outcome)).Append("</span></button>");
        }
        return html.Append("</div>").ToString();
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
