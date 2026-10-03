using System.Net;
using System.Text;
using System.Text.Json;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

// The dashboard stream contains escaped, managed HTML. Datastar keeps only
// local view choices; native state and available actions come from the server.
internal static class DashboardSnapshotFragments
{
    internal static string RenderShared(DashboardService dashboard, EngineeringService engineering,
        bool checking = false, string streamId = "")
    {
        return RenderShared(dashboard.CurrentDashboard(), dashboard.Logs(0, 0).Entries, engineering, checking, streamId);
    }

    internal static string RenderShared(DashboardSnapshot snapshot, IReadOnlyList<DashboardLogEntry> logs,
        EngineeringService engineering, bool checking = false, string streamId = "")
    {
        var html = new StringBuilder();
        html.Append(DashboardClientState.Render(snapshot, engineering, checking, streamId));
        RenderTabs(html, snapshot);
        RenderContexts(html, snapshot, engineering);
        RenderActivity(html, engineering, checking);
        RenderLogs(html, snapshot, logs);
        return html.ToString();
    }

    private static void RenderTabs(StringBuilder html, DashboardSnapshot snapshot)
    {
        html.Append("<nav id=\"tabs\" aria-label=\"Dashboard tabs\">");
        foreach (var tab in snapshot.Tabs)
        {
            var state = tab.Kind == "server" ? "Bridge & process discovery" :
                (tab.Live ? "running" : "closed") + " · " + tab.ConnectionState;
            html.Append("<button type=\"button\"")
                .Append(Attr("id", "dashboard-tab-" + DashboardToolForms.TabKey(tab.Id)))
                .Append(Attr("data-tab", tab.Id))
                .Append(Attr("data-attr:aria-pressed", "String($_ui.selectedTabId === el.dataset.tab)"))
                .Append(Attr("data-on:click", "$_ui.selectedTabId = el.dataset.tab; $_ui.inspectorView = 'result'"))
                .Append(Attr("aria-label", tab.Title + " — " + state))
                .Append(">").Append(H(tab.Title))
                .Append("<span class=\"tab-state\">").Append(H(state)).Append("</span></button>");
        }
        html.Append("</nav>");
    }

    private static void RenderContexts(StringBuilder html, DashboardSnapshot snapshot, EngineeringService engineering)
    {
        html.Append("<div id=\"dashboard-contexts\">");
        if (snapshot.LogsTruncated || snapshot.TabsTruncated)
            html.Append("<p id=\"banner\" class=\"notice\">Older dashboard history was discarded. This server keeps ")
                .Append(snapshot.MaxLogEntries).Append(" log entries and ")
                .Append(snapshot.MaxHistoricalTabs).Append(" historical tabs.</p>");
        foreach (var tab in snapshot.Tabs)
            RenderContext(html, tab, engineering);
        html.Append("</div>");
    }

    private static void RenderContext(StringBuilder html, DashboardTabView tab, EngineeringService engineering)
    {
        var tia = tab.Kind == "tia";
        var projectReady = tia && tab.Live && tab.ConnectionState == "connected" &&
            !string.IsNullOrEmpty(tab.ProjectPath) && tab.ProjectState == "open";
        html.Append("<section class=\"dashboard-context\"")
            .Append(Attr("id", "dashboard-context-" + DashboardToolForms.TabKey(tab.Id)))
            .Append(Attr("data-tab-id", tab.Id))
            .Append(Attr("data-show", "$_ui.selectedTabId === el.dataset.tabId"))
            .Append(Attr("data-preserve-attr", "style"));
        if (tab.Id != "server") html.Append(" style=\"display:none\"");
        html.Append("><div class=\"context-heading\"><h2>").Append(H(tab.Title)).Append("</h2>")
            .Append("<div class=\"actions\">");
        if (tia) RenderConnectionActions(html, tab);
        html.Append("</div></div><div class=\"summary\" aria-label=\"Target status\">");
        if (tia)
        {
            Badge(html, tab.ConnectionState, projectReady ? "good" : "");
            Badge(html, "PID " + (tab.ProcessId?.ToString() ?? "—"), "");
            Badge(html, tab.ProjectState == "open" ? "Project open" :
                tab.ProjectState == "historical" ? "Historical project" : "No project open", "");
        }
        else
        {
            Badge(html, McpBoundary.ToolDefs(engineering.WriteToolsAvailable).Count + " MCP tools · " +
                (engineering.WriteToolsAvailable ? "read + write" : "read-only"), "good");
        }
        html.Append("</div><details class=\"context-details\"")
            .Append(Attr("id", "dashboard-context-details-" + DashboardToolForms.TabKey(tab.Id)))
            .Append(Attr("data-preserve-attr", "open"))
            .Append("><summary>Connection details</summary><p class=\"mono context-path\">");
        if (tia) html.Append(H(ContextDetails(tab)));
        else html.Append("Server events, process discovery and bridge status. TIA tabs keep their own project tools and logs.");
        html.Append("</p></details>");
        if (tia && !tab.Live && !string.IsNullOrEmpty(tab.ProjectPath))
            html.Append("<p class=\"history-note\">Stored path: ").Append(H(tab.ProjectPath))
                .Append(". Open project starts a new TIA window for this project.</p>");
        html.Append("</section>");
    }

    private static void RenderConnectionActions(StringBuilder html, DashboardTabView tab)
    {
        if (tab.Live && tab.ConnectionState != "connected")
        {
            if (tab.ProcessId.HasValue && tab.RuntimeIdentity != null && tab.CanAttach &&
                string.IsNullOrEmpty(tab.CleanupError))
                ActionButton(html, "Connect", "connect",
                    "{processId:Number(el.dataset.processId),expectedRuntimeStartUtcTicks:el.dataset.runtimeStartUtcTicks," +
                    "expectedProjectPath:el.dataset.projectPathPresent === 'true' ? el.dataset.projectPath : null}",
                    null, true, tab);
            else
                html.Append("<button type=\"button\" disabled>Connect</button>");
        }
        if (tab.ProcessId.HasValue && tab.RuntimeIdentity != null && tab.ConnectionId.HasValue &&
            (tab.ConnectionState == "connected" || !string.IsNullOrEmpty(tab.CleanupError)))
            ActionButton(html, "Disconnect", "disconnect",
                "{processId:Number(el.dataset.processId),expectedRuntimeStartUtcTicks:el.dataset.runtimeStartUtcTicks," +
                "expectedProjectPath:el.dataset.projectPathPresent === 'true' ? el.dataset.projectPath : null," +
                "expectedConnectionId:el.dataset.connectionId}", null, false, tab);
        else if (tab.ProcessId.HasValue && !string.IsNullOrEmpty(tab.CleanupError))
            html.Append("<button type=\"button\" disabled>Disconnect</button>");
        if (!tab.Live && !string.IsNullOrEmpty(tab.ProjectPath))
            ActionButton(html, "Open project in TIA", "projects/open", "{tabId:el.dataset.tabId}", tab.Id);
        if (!tab.Live)
            ActionButton(html, "Dismiss history", "tabs/dismiss", "{tabId:el.dataset.tabId}", tab.Id);
    }

    private static void ActionButton(StringBuilder html, string label, string route, string payload,
        string? tabId, bool primary = false, DashboardTabView? connectionTab = null)
    {
        var expression = "@post('/api/dashboard/" + route + "', {payload:" + payload +
            ",headers:{'X-Tia-Dashboard':'1'},retry:'never',requestCancellation:'disabled'})";
        var targetTab = connectionTab?.Id ?? tabId;
        if (route != "tabs/dismiss" && targetTab != null)
        {
            var ui = "$_ui.tabs." + DashboardToolForms.TabKey(targetTab);
            expression = ui + ".followLatest=true, " + ui + ".pinnedRunId='', " + expression;
        }
        html.Append("<button type=\"button\"");
        if (primary) html.Append(" class=\"primary\"");
        if (tabId != null) html.Append(Attr("data-tab-id", tabId));
        if (connectionTab != null)
            html.Append(Attr("data-process-id", connectionTab.ProcessId?.ToString()))
                .Append(Attr("data-runtime-start-utc-ticks", connectionTab.RuntimeIdentity))
                .Append(Attr("data-project-path-present", Bool(connectionTab.ProjectPath != null)))
                .Append(Attr("data-project-path", connectionTab.ProjectPath))
                .Append(Attr("data-connection-id", connectionTab.ConnectionId?.ToString("D")));
        html.Append(Attr("data-indicator", "_ui.connectionBusy"))
            .Append(Attr("data-attr:disabled", "$_server.busy || $_ui.connectionBusy"))
            .Append(Attr("data-on:click", "!$_server.busy && !$_ui.connectionBusy && (" + expression + ")"))
            .Append('>').Append(H(label)).Append("</button>");
    }

    private static string ContextDetails(DashboardTabView tab)
    {
        var details = new List<string>
        {
            tab.Live ? "Runtime running" : "Runtime closed"
        };
        if (tab.ProcessId.HasValue) details.Add("process " + tab.ProcessId.Value);
        var mode = tab.Mode;
        if (mode != null && mode.Length != 0) details.Add(mode);
        details.Add(tab.ProjectState == "open" ? "project open" :
            tab.ProjectState == "historical" ? "historical project" : "no project");
        var projectPath = tab.ProjectPath;
        if (projectPath != null && projectPath.Length != 0) details.Add(projectPath);
        details.Add(tab.ConnectionState);
        if (tab.ConnectionId.HasValue) details.Add("connection " + tab.ConnectionId.Value);
        var reason = tab.Reason;
        if (reason != null && reason.Length != 0) details.Add(reason);
        var unavailableReason = tab.UnavailableReason;
        if (unavailableReason != null && unavailableReason.Length != 0) details.Add(unavailableReason);
        var cleanupError = tab.CleanupError;
        if (cleanupError != null && cleanupError.Length != 0) details.Add(cleanupError);
        foreach (var previous in tab.Previous)
            details.Add("earlier runtime " + previous.ProcessId +
                (previous.ConnectionId.HasValue ? " connection " + previous.ConnectionId.Value : ""));
        return string.Join("\n", details);
    }

    private static void Badge(StringBuilder html, string? label, string extraClass)
    {
        html.Append("<span class=\"badge");
        if (!string.IsNullOrEmpty(extraClass)) html.Append(' ').Append(extraClass);
        html.Append("\">").Append(H(label)).Append("</span>");
    }

    private static void RenderActivity(StringBuilder html, EngineeringService engineering, bool checking)
    {
        var label = checking ? "Checking TIA…" : engineering.MonitorError != null ?
            "TIA check failed" : engineering.PendingOperations > 0 ? "TIA busy" : "Server online";
        var css = checking || engineering.MonitorError != null || engineering.PendingOperations > 0 ? "badge warn" : "badge good";
        html.Append("<span id=\"activity\" role=\"status\" class=\"")
            .Append(css).Append("\">").Append(H(label)).Append("</span>");
    }

    private static void RenderLogs(StringBuilder html, DashboardSnapshot snapshot, IReadOnlyList<DashboardLogEntry> logs)
    {
        html.Append("<div id=\"logs\">");
        for (var i = logs.Count - 1; i >= 0; i--)
        {
            var entry = logs[i];
            html.Append("<details class=\"log-row\"")
                .Append(Attr("id", "dashboard-log-" + snapshot.Epoch + "-" + entry.Sequence))
                .Append(Attr("data-tab-id", entry.TabId))
                .Append(Attr("data-log-sequence", entry.Sequence.ToString()))
                .Append(Attr("data-show", "$_ui.selectedTabId === el.dataset.tabId"))
                .Append(Attr("data-preserve-attr", "style open"));
            if (entry.TabId != "server") html.Append(" style=\"display:none\"");
            html.Append("><summary class=\"")
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
        foreach (var tab in snapshot.Tabs.Where(tab => logs.All(entry => entry.TabId != tab.Id)))
        {
            html.Append("<p class=\"log-row muted\"")
                .Append(Attr("id", "dashboard-log-empty-" + DashboardToolForms.TabKey(tab.Id)))
                .Append(Attr("data-tab-id", tab.Id))
                .Append(Attr("data-show", "$_ui.selectedTabId === el.dataset.tabId"))
                .Append(Attr("data-preserve-attr", "style"));
            if (tab.Id != "server") html.Append(" style=\"display:none\"");
            html.Append(">No server events for this workspace yet.</p>");
        }
        html.Append("</div>");
    }

    private static string Attr(string name, string? value) => " " + name + "=\"" + H(value) + "\"";
    private static string Bool(bool value) => value ? "true" : "false";
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
}
