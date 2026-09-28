using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TiaOpennessMcpServer.Dashboard;

// Read results provide suggestions only. The native IDs remain editable and
// McpBoundary validates every submitted selector against the retained project.
internal sealed class DashboardSelectorStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TabChoices> _tabs = new(StringComparer.Ordinal);
    private readonly DashboardService _dashboard;

    internal DashboardSelectorStore(DashboardService dashboard) { _dashboard = dashboard; }

    internal readonly struct RunContext
    {
        internal string TabId { get; }
        internal string Stamp { get; }
        internal string Epoch { get; }
        internal string? Cpu { get; }
        internal Guid? ConnectionId { get; }
        internal RunContext(string tabId, string stamp, string epoch, string? cpu, Guid? connectionId)
        { TabId = tabId; Stamp = stamp; Epoch = epoch; Cpu = cpu; ConnectionId = connectionId; }
    }

    internal RunContext Capture(DashboardRunCapture run)
    {
        var snapshot = _dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.FirstOrDefault(item => item.Id == run.TabId);
        string? cpu = null;
        if (run.RequestJson != null)
        {
            using var request = JsonDocument.Parse(run.RequestJson);
            if (request.RootElement.TryGetProperty("arguments", out var args) &&
                args.TryGetProperty("plcObjectId", out var selected) && selected.ValueKind == JsonValueKind.String)
                cpu = selected.GetString();
        }
        return new RunContext(run.TabId, tab == null ? "" : Stamp(tab), snapshot.Epoch, cpu,
            tab?.ConnectionId);
    }

    internal static string Stamp(DashboardTabView tab) => string.Join("|", new[]
    {
        tab.Id, tab.RuntimeIdentity ?? "", tab.ConnectionId?.ToString("D") ?? "",
        tab.ProjectPath ?? "", tab.ConnectionState, tab.ProjectState, tab.Live ? "1" : "0"
    });

    internal static string SignalPrefix(DashboardSnapshot snapshot, DashboardTabView tab)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Stamp(tab)));
        return "f" + snapshot.Epoch + "_" + tab.Id.Replace("-", "") + "_" +
            BitConverter.ToString(digest, 0, 8).Replace("-", "").ToLowerInvariant();
    }

    internal (IReadOnlyDictionary<string, IReadOnlyList<(string Id, string Label)>> Options, string? InventoryCpu)
        ForTab(DashboardTabView tab)
    {
        lock (_gate)
        {
            var state = GetOrReset(tab);
            var options = new Dictionary<string, IReadOnlyList<(string Id, string Label)>>(StringComparer.Ordinal);
            foreach (var pair in state.Options)
                options[pair.Key] = pair.Value.ToArray();
            options["object"] = new[] { "block", "udt", "tagTable", "entry", "technologyObject" }
                .SelectMany(kind => state.Options.TryGetValue(kind, out var found) ? found : Enumerable.Empty<(string Id, string Label)>())
                .GroupBy(choice => choice.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            return (options, state.InventoryCpu);
        }
    }

    internal void Prune(IReadOnlyCollection<string> tabIds)
    {
        lock (_gate)
            foreach (var gone in _tabs.Keys.Except(tabIds).ToArray()) _tabs.Remove(gone);
    }

    internal bool Observe(DashboardRunCapture started, DashboardRunCompletion completion, RunContext context)
    {
        if (completion.Capture.Outcome != "success" || context.Stamp.Length == 0) return false;
        var snapshot = _dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.FirstOrDefault(item => item.Id == context.TabId);
        if (tab == null || context.Epoch != snapshot.Epoch || Stamp(tab) != context.Stamp) return false;
        JsonElement body;
        try
        {
            using var response = JsonDocument.Parse(completion.ResponseJson);
            var result = response.RootElement.GetProperty("result");
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                return false;
            var raw = result.GetProperty("content")[0].GetProperty("text").GetString();
            using var payload = JsonDocument.Parse(raw ?? "{}");
            body = payload.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        { return false; }

        var changed = false;
        lock (_gate)
        {
            // ForTab checks the current stamp before exposing any options. A
            // concurrent context change therefore cannot publish stale IDs.
            var state = GetOrReset(tab);
            changed = Apply(started.Operation, body, state, context.Cpu);
        }
        if (changed) _dashboard.NotifyRunChanged();
        return changed;
    }

    private TabChoices GetOrReset(DashboardTabView tab)
    {
        var stamp = Stamp(tab);
        if (!_tabs.TryGetValue(tab.Id, out var state) || state.Stamp != stamp)
        {
            state = new TabChoices(stamp);
            _tabs[tab.Id] = state;
        }
        return state;
    }

    private static bool Apply(string tool, JsonElement body, TabChoices state, string? cpu)
    {
        if (tool == "list_devices")
        {
            state.Options["device"] = Nodes(body, "roots", "device");
            state.Options.Remove("cpu");
            state.ClearCpu();
            return true;
        }
        if (tool == "get_device")
        {
            state.Options["cpu"] = Flatten(body, "deviceItems")
                .Select(item => (Id: String(item, "plcObjectId"), Label: String(item, "name")))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => (item.Id!, item.Label ?? item.Id!)).ToList();
            state.ClearCpu();
            return true;
        }
        if (tool is "list_blocks" or "list_udts" or "list_tag_tables" or "list_technology_objects")
        {
            if (string.IsNullOrWhiteSpace(cpu)) return false;
            if (state.InventoryCpu != cpu) state.ClearCpu();
            state.InventoryCpu = cpu;
            var (kind, group) = tool switch
            {
                "list_blocks" => ("block", "blockGroup"),
                "list_udts" => ("udt", "typeGroup"),
                "list_tag_tables" => ("tagTable", "tagTableGroup"),
                _ => ("technologyObject", "technologyObjectGroup")
            };
            state.Options[kind] = Nodes(body, "roots", kind);
            var groups = Flatten(body, "roots")
                .Where(item => String(item, "kind") == group && !Bool(item, "isSystem")).ToArray();
            state.Options[group] = groups
                .Select(item => (Id: String(item, "objectId"), Label: String(item, "path") ?? String(item, "name")))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => (item.Id!, item.Label ?? item.Id!)).ToList();
            state.Options[group + "Path"] = groups
                .Select(item => (Id: String(item, "path"), Label: String(item, "path") ?? String(item, "name")))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => (item.Id!, item.Label ?? item.Id!)).ToList();
            if (tool == "list_tag_tables") state.Options.Remove("entry");
            return true;
        }
        if (tool == "get_tag_table" && body.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Object)
        {
            var writable = new List<(string Id, string Label)>();
            foreach (var (property, label) in new[] { ("tags", "Tag"), ("userConstants", "User constant") })
                if (entries.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array)
                    foreach (var item in values.EnumerateArray())
                    {
                        var id = String(item, "objectId");
                        if (!string.IsNullOrWhiteSpace(id)) writable.Add((id!, label + ": " + (String(item, "name") ?? id)));
                    }
            state.Options["entry"] = writable;
            return true;
        }
        return false;
    }

    private static List<(string Id, string Label)> Nodes(JsonElement body, string property, string kind) =>
        Flatten(body, property).Where(item => String(item, "kind") == kind)
            .Select(item => (Id: String(item, "objectId"),
                Label: String(item, "path") ?? String(item, "name")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => (item.Id!, item.Label ?? item.Id!)).ToList();

    private static IEnumerable<JsonElement> Flatten(JsonElement body, string property)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(property, out var roots) ||
            roots.ValueKind != JsonValueKind.Array) yield break;
        var stack = new Stack<JsonElement>(roots.EnumerateArray().Reverse());
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item.ValueKind != JsonValueKind.Object) continue;
            yield return item;
            if (item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray().Reverse()) stack.Push(child);
        }
    }

    private static string? String(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private sealed class TabChoices
    {
        internal string Stamp { get; }
        internal string? InventoryCpu;
        internal Dictionary<string, List<(string Id, string Label)>> Options { get; } = new(StringComparer.Ordinal);
        internal TabChoices(string stamp) { Stamp = stamp; }
        internal void ClearCpu()
        {
            InventoryCpu = null;
            foreach (var kind in new[] { "block", "udt", "tagTable", "entry", "technologyObject",
                "blockGroup", "typeGroup", "tagTableGroup", "technologyObjectGroup",
                "blockGroupPath", "typeGroupPath", "tagTableGroupPath", "technologyObjectGroupPath" }) Options.Remove(kind);
        }
    }
}
