using System.Net;
using System.Text;
using System.Text.Json;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

// Only view choices and drafts belong to the browser. Complete assignments of
// managed metadata remove absent keys, including those from an earlier stream.
internal static class DashboardClientState
{
    internal static string Render(DashboardSnapshot snapshot, EngineeringService engineering,
        bool checking, string streamId)
    {
        var tabs = snapshot.Tabs.ToDictionary(tab => DashboardToolForms.TabKey(tab.Id), tab => new
        {
            tool = tab.Kind == "server" ? "list_tia_processes" : "list_devices",
            followLatest = true, pinnedRunId = ""
        });
        var ids = JsonSerializer.Serialize(snapshot.Tabs.Select(tab => tab.Id).ToArray());
        var drafts = JsonSerializer.Serialize(snapshot.Tabs.Select(tab =>
            DashboardToolForms.DraftKey(DashboardSelectorStore.SignalPrefix(snapshot, tab))).ToArray());
        var epoch = JsonSerializer.Serialize(snapshot.Epoch);
        var defaults = JsonSerializer.Serialize(new { _ui = new { tabs } });
        var effect = new StringBuilder("@peek(() => {")
            .Append("if ($_server.epoch !== ").Append(epoch).Append(") {")
            .Append("$_server.runState={version:-1,tabs:{},records:{}};")
            .Append("$_ui.inspector={requestedKey:'',viewKey:'',oneShotIdentity:''};}")
            .Append("$_server.epoch=").Append(epoch).Append(';')
            .Append("$_server.busy=").Append(engineering.PendingOperations > 0 ? "true" : "false").Append(';')
            .Append("$_server.writeToolsAvailable=").Append(engineering.WriteToolsAvailable ? "true" : "false").Append(';')
            .Append("$_server.tabIds=").Append(ids).Append(';')
            .Append("$_server.draftKeys=").Append(drafts).Append(';');
        if (streamId.Length > 0)
            effect.Append("$_server.streamId=").Append(JsonSerializer.Serialize(streamId)).Append(';');
        effect.Append("if (!").Append(ids).Append(".includes($_ui.selectedTabId)) $_ui.selectedTabId='server';");
        var definitions = McpBoundary.ToolDefs(engineering.WriteToolsAvailable);
        foreach (var tab in snapshot.Tabs)
        {
            var ui = "$_ui.tabs." + DashboardToolForms.TabKey(tab.Id);
            var names = definitions.Where(tool => tab.Kind == "server"
                ? tool.Name is "list_tia_processes" or "get_status"
                : tool.Name == "get_status" || tool.InputSchema.Properties.ContainsKey("processId"))
                .Select(tool => tool.Name).ToArray();
            effect.Append("if (!").Append(JsonSerializer.Serialize(names)).Append(".includes(")
                .Append(ui).Append(".tool)) ").Append(ui).Append(".tool=")
                .Append(JsonSerializer.Serialize(tab.Kind == "server" ? "list_tia_processes" : "list_devices")).Append(';');
        }
        effect.Append("})");
        return "<div id=\"dashboard-state\" hidden data-signals__ifmissing=\"" + H(defaults) +
            "\" data-effect=\"" + H(effect.ToString()) + "\" data-epoch=\"" + H(snapshot.Epoch) +
            "\" data-monitor-checking=\"" + (checking ? "true" : "false") + "\"></div>";
    }

    // Removed indicator/binding plugins can write their final values back into
    // the old branch. Prune only after the stream has removed those forms and
    // history rows, so the final cleanup cannot recreate an obsolete context.
    internal static string RenderPrune(DashboardSnapshot snapshot)
    {
        var keys = JsonSerializer.Serialize(snapshot.Tabs.Select(tab => DashboardToolForms.TabKey(tab.Id)).ToArray());
        var drafts = JsonSerializer.Serialize(snapshot.Tabs.Select(tab =>
            DashboardToolForms.DraftKey(DashboardSelectorStore.SignalPrefix(snapshot, tab))).ToArray());
        var effect = "@peek(() => {$_drafts=Object.fromEntries(Object.entries($_drafts).filter(([key])=>" +
            drafts + ".includes(key)));$_ui.tabs=Object.fromEntries(Object.entries($_ui.tabs).filter(([key])=>" +
            keys + ".includes(key)));})";
        return "<div id=\"dashboard-prune\" hidden data-effect=\"" + H(effect) + "\"></div>";
    }

    internal static string RenderRuns(DashboardSnapshot tabs, DashboardRunSnapshot snapshot)
    {
        var state = new
        {
            version = snapshot.Version,
            tabs = tabs.Tabs.ToDictionary(tab => tab.Id, tab => new
            {
                key = DashboardToolForms.TabKey(tab.Id),
                latestRunId = snapshot.Captures.FirstOrDefault(run => run.TabId == tab.Id && run.ParentRunId == null)?.Id ?? ""
            }),
            records = snapshot.Captures.ToDictionary(run => run.Id, run => new
            {
                revision = run.Revision, payloadRetained = run.PayloadRetained, parentRunId = run.ParentRunId
            })
        };
        // POST and event responses can interleave. A completed observation must
        // never be replaced by an older running observation from another writer.
        var effect = "@peek(() => {if ($_server.epoch === " + JsonSerializer.Serialize(tabs.Epoch) +
            " && " + snapshot.Version + " >= $_server.runState.version) $_server.runState=" +
            JsonSerializer.Serialize(state) + ";})";
        return "<div id=\"dashboard-run-state\" hidden data-effect=\"" + H(effect) + "\"></div>";
    }

    internal static string Identity(string epoch, string tabId, string runId) => epoch + "|" + tabId + "|" + runId;

    internal static string NestSignals(IReadOnlyDictionary<string, object?> values)
    {
        var root = new Dictionary<string, object?>();
        foreach (var pair in values)
        {
            var parts = pair.Key.Split('.');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!node.TryGetValue(parts[i], out var child))
                    node[parts[i]] = child = new Dictionary<string, object?>();
                node = (Dictionary<string, object?>)child!;
            }
            node[parts[parts.Length - 1]] = pair.Value;
        }
        return JsonSerializer.Serialize(root);
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
}
