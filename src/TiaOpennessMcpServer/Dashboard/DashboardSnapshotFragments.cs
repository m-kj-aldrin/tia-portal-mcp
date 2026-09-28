using System.Net;
using System.Text;
using System.Text.Json;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

// The dashboard stream contains escaped, managed HTML. The selected workspace
// remains a browser choice; every tab's current server state is sent together.
internal static class DashboardSnapshotFragments
{
    internal static string RenderShared(DashboardService dashboard, EngineeringService engineering, bool checking = false)
    {
        return RenderShared(dashboard.CurrentDashboard(), dashboard.Logs(0, 0).Entries, engineering, checking);
    }

    internal static string RenderShared(DashboardSnapshot snapshot, IReadOnlyList<DashboardLogEntry> logs,
        EngineeringService engineering, bool checking = false)
    {
        var html = new StringBuilder();
        RenderState(html, snapshot, engineering, checking);
        RenderTabs(html, snapshot);
        RenderActivity(html, engineering, checking);
        RenderLogs(html, logs);
        return html.ToString();
    }

    internal static string RenderToolForms(bool writeToolsAvailable) =>
        "<div id=\"tool-form-definitions\" hidden>" +
        DashboardToolForms.Render(McpBoundary.ToolDefs(writeToolsAvailable)) + "</div>";

    private static void RenderState(StringBuilder html, DashboardSnapshot snapshot,
        EngineeringService engineering, bool checking)
    {
        html.Append("<div id=\"dashboard-state\" hidden")
            .Append(Attr("data-epoch", snapshot.Epoch))
            .Append(Attr("data-generation", snapshot.Generation.ToString()))
            .Append(Attr("data-pending-operations", engineering.PendingOperations.ToString()))
            .Append(Attr("data-monitor-error", checking ? null : engineering.MonitorError))
            .Append(Attr("data-monitor-checking", Bool(checking)))
            .Append(Attr("data-background-monitoring-active", Bool(engineering.BackgroundMonitoringActive)))
            .Append(Attr("data-monitoring-subscribers", engineering.MonitoringSubscribers.ToString()))
            .Append(Attr("data-write-tools-available", Bool(engineering.WriteToolsAvailable)))
            .Append(Attr("data-logs-truncated", Bool(snapshot.LogsTruncated)))
            .Append(Attr("data-tabs-truncated", Bool(snapshot.TabsTruncated)))
            .Append(Attr("data-max-log-entries", snapshot.MaxLogEntries.ToString()))
            .Append(Attr("data-max-historical-tabs", snapshot.MaxHistoricalTabs.ToString()))
            .Append('>');
        foreach (var tab in snapshot.Tabs)
        {
            html.Append("<div class=\"dashboard-tab-state\"")
                .Append(Attr("data-tab-id", tab.Id))
                .Append(Attr("data-kind", tab.Kind))
                .Append(Attr("data-title", tab.Title))
                .Append(Attr("data-process-id", tab.ProcessId?.ToString()))
                .Append(Attr("data-mode", tab.Mode))
                .Append(Attr("data-runtime-state", tab.RuntimeState))
                .Append(Attr("data-runtime-identity", tab.RuntimeIdentity))
                .Append(Attr("data-connection-state", tab.ConnectionState))
                .Append(Attr("data-connection-id", tab.ConnectionId?.ToString()))
                .Append(Attr("data-project-path", tab.ProjectPath))
                .Append(Attr("data-project-state", tab.ProjectState))
                .Append(Attr("data-can-attach", Bool(tab.CanAttach)))
                .Append(Attr("data-unavailable-reason", tab.UnavailableReason))
                .Append(Attr("data-reason", tab.Reason))
                .Append(Attr("data-cleanup-error", tab.CleanupError))
                .Append(Attr("data-live", Bool(tab.Live)))
                .Append('>');
            foreach (var previous in tab.Previous)
            {
                html.Append("<span class=\"dashboard-prior-runtime\"")
                    .Append(Attr("data-process-id", previous.ProcessId.ToString()))
                    .Append(Attr("data-runtime-identity", previous.RuntimeStartUtcTicks.ToString()))
                    .Append(Attr("data-connection-id", previous.ConnectionId?.ToString()))
                    .Append(Attr("data-mode", previous.Mode))
                    .Append("></span>");
            }
            html.Append("</div>");
        }
        html.Append("</div>");
    }

    private static void RenderTabs(StringBuilder html, DashboardSnapshot snapshot)
    {
        html.Append("<nav id=\"tabs\" aria-label=\"Dashboard tabs\">");
        foreach (var tab in snapshot.Tabs)
        {
            var state = tab.Kind == "server" ? "Bridge & process discovery" :
                (tab.Live ? "running" : "closed") + " · " + tab.ConnectionState;
            html.Append("<button type=\"button\"")
                .Append(Attr("data-tab", tab.Id))
                .Append(Attr("aria-label", tab.Title + " — " + state))
                .Append(">").Append(H(tab.Title))
                .Append("<span class=\"tab-state\">").Append(H(state)).Append("</span></button>");
        }
        html.Append("</nav>");
    }

    private static void RenderActivity(StringBuilder html, EngineeringService engineering, bool checking)
    {
        var label = checking ? "Checking TIA…" : engineering.MonitorError != null ?
            "TIA check failed" : engineering.PendingOperations > 0 ? "TIA busy" : "Server online";
        var css = checking || engineering.MonitorError != null || engineering.PendingOperations > 0 ? "badge warn" : "badge good";
        html.Append("<span id=\"activity\" role=\"status\" class=\"")
            .Append(css).Append("\">").Append(H(label)).Append("</span>");
    }

    private static void RenderLogs(StringBuilder html, IReadOnlyList<DashboardLogEntry> logs)
    {
        html.Append("<div id=\"logs\">");
        if (logs.Count == 0)
            html.Append("<p class=\"log-row muted\">No server events yet.</p>");
        for (var i = logs.Count - 1; i >= 0; i--)
        {
            var entry = logs[i];
            html.Append("<details class=\"log-row\"")
                .Append(Attr("data-tab-id", entry.TabId))
                .Append(Attr("data-log-sequence", entry.Sequence.ToString()))
                .Append("><summary class=\"")
                .Append(entry.Outcome == "error" ? "error" : entry.Outcome == "partial" ? "partial" : "")
                .Append("\">")
                .Append(H(entry.AtUtc.ToLocalTime().ToString("HH:mm:ss")))
                .Append(" · ").Append(H(entry.Origin))
                .Append(" · ").Append(H(entry.Operation))
                .Append(" · ").Append(H(entry.Outcome));
            if (entry.DurationMs.HasValue)
                html.Append(" · ").Append(Math.Round(entry.DurationMs.Value)).Append(" ms");
            html.Append("</summary><pre>")
                .Append(H(JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true })))
                .Append("</pre></details>");
        }
        html.Append("</div>");
    }

    private static string Attr(string name, string? value) => " " + name + "=\"" + H(value) + "\"";
    private static string Bool(bool value) => value ? "true" : "false";
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
}
