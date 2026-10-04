using System.Diagnostics;
using System.Text.Json;
using TiaOpennessMcpServer.Mcp;

namespace TiaOpennessMcpServer.Dashboard;

// Dashboard readback policy belongs to the workflow. Each admitted operation
// still executes through the one composed MCP boundary in DashboardToolRunner.
internal sealed class DashboardToolWorkflow
{
    private readonly DashboardService _dashboard;
    private readonly DashboardToolRunner _tools;
    private readonly DashboardSelectorStore _selectors;
    private readonly JsonSerializerOptions _json;

    internal DashboardToolWorkflow(DashboardService dashboard, DashboardToolRunner tools,
        DashboardSelectorStore selectors, JsonSerializerOptions json)
    { _dashboard = dashboard; _tools = tools; _selectors = selectors; _json = json; }

    internal async Task<(DashboardRunCompletion Completion, bool ReadbackFailed)> RunAsync(
        DashboardRunCapture started, DashboardSelectorStore.RunContext context)
    {
        var completed = await RunAndObserveAsync(started, context).ConfigureAwait(false);
        if (!McpBoundary.IsWrite(started.Operation)) return (completed, false);
        try
        {
            var failed = await RefreshAfterWriteAsync(started, completed, context).ConfigureAwait(false);
            return (completed, failed);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Dashboard follow-up read failed: " + ex.Message);
            return (completed, true);
        }
    }

    private async Task<DashboardRunCompletion> RunAndObserveAsync(DashboardRunCapture started,
        DashboardSelectorStore.RunContext context, Guid? expectedConnectionId = null)
    {
        var completed = await _tools.RunAsync(started, expectedConnectionId)
            .ConfigureAwait(false);
        _selectors.Observe(started, completed, context);
        return completed;
    }

    private async Task<bool> RefreshAfterWriteAsync(DashboardRunCapture started,
        DashboardRunCompletion completion, DashboardSelectorStore.RunContext context)
    {
        if (completion.Capture.Outcome == "error" &&
            completion.ResponseJson.IndexOf("affectedObjects", StringComparison.Ordinal) < 0)
            return false;
        var body = ToolResultData(completion.ResponseJson);
        if (body == null) return false;
        if (!body.Value.TryGetProperty("affectedObjects", out var effects) || effects.ValueKind != JsonValueKind.Array)
            return false;
        var snapshot = _dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.FirstOrDefault(item => item.Id == context.TabId);
        if (tab == null || snapshot.Epoch != context.Epoch || DashboardSelectorStore.Stamp(tab) != context.Stamp)
            return true;
        if (started.RequestJson == null) return false;
        using var request = JsonDocument.Parse(started.RequestJson);
        var args = request.RootElement.GetProperty("arguments");
        var cpu = context.Cpu ?? _selectors.ForTab(tab).InventoryCpu;
        var tableId = started.Operation is "create_tag" or "create_user_constant" ? TextArg(args, "objectId") : null;
        if (tableId == null)
            tableId = effects.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                    (TextArg(item, "kind") is "tag" or "userConstant"))
                .Select(item => TextArg(item, "parentObjectId")).FirstOrDefault(value => value != null);
        var failed = false;
        async Task Read(string name, object parameters)
        {
            var current = _dashboard.CurrentDashboard();
            var target = current.Tabs.FirstOrDefault(item => item.Id == context.TabId);
            if (target == null || current.Epoch != context.Epoch || DashboardSelectorStore.Stamp(target) != context.Stamp)
            {
                failed = true;
                return;
            }
            try
            {
                var envelope = JsonSerializer.SerializeToElement(new
                {
                    tabId = context.TabId, requestId = Guid.NewGuid().ToString("D"), name,
                    arguments = parameters
                }, _json);
                var read = _tools.Begin(envelope, started.Id);
                var readContext = _selectors.Capture(read);
                var result = await RunAndObserveAsync(read, readContext, context.ConnectionId)
                    .ConfigureAwait(false);
                if (result.Capture.Outcome != "success") failed = true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Dashboard follow-up read failed: " + ex.Message);
                failed = true;
            }
        }
        if (started.Operation is "create_device" or "delete_device")
        {
            if (tab.ProcessId == null) return true;
            await Read("list_devices", new { processId = tab.ProcessId }).ConfigureAwait(false);
            if (started.Operation == "create_device")
                foreach (var item in effects.EnumerateArray())
                    if (TextArg(item, "kind") == "device" && TextArg(item, "objectId") is string id)
                        await Read("get_device", new { processId = tab.ProcessId, objectId = id,
                            includePath = false }).ConfigureAwait(false);
            return failed;
        }
        if (tableId != null)
        {
            await Read("get_tag_table", new { processId = tab.ProcessId, objectId = tableId,
                includeEntries = true, includePath = false }).ConfigureAwait(false);
            return failed;
        }
        if (cpu == null || tab.ProcessId == null) return true;
        var inventory = started.Operation switch
        {
            "write_blocks" or "delete_block" => "list_blocks",
            "write_udts" or "delete_udt" => "list_udts",
            "create_technology_object" or "set_technology_object_parameters" => "list_technology_objects",
            "create_group" or "delete_group" or "rename" => TextArg(args, "kind") switch
            {
                "block" => "list_blocks", "udt" => "list_udts", "technologyObject" => "list_technology_objects",
                _ => "list_tag_tables"
            },
            _ => "list_tag_tables"
        };
        await Read(inventory, new { processId = tab.ProcessId, plcObjectId = cpu }).ConfigureAwait(false);
        if (!body.Value.TryGetProperty("affectedObjects", out var affected) || affected.ValueKind != JsonValueKind.Array)
            return failed;
        if (started.Operation is "delete_block" or "delete_udt" or "delete_tag_table" or "delete_tag_entry" or "delete_group")
            return failed;
        foreach (var item in affected.EnumerateArray())
        {
            var id = TextArg(item, "objectId");
            var detail = TextArg(item, "kind") switch
            {
                "block" => "get_block", "udt" => "get_udt", "tagTable" => "get_tag_table",
                "technologyObject" => "get_technology_object", _ => null
            };
            if (id == null || detail == null) continue;
            if (detail == "get_tag_table")
                await Read(detail, new { processId = tab.ProcessId, objectId = id,
                    includeEntries = true, includePath = false }).ConfigureAwait(false);
            else if (detail == "get_technology_object")
                await Read(detail, new { processId = tab.ProcessId, objectId = id,
                    includePath = false }).ConfigureAwait(false);
            else if (TextArg(args, "sourceFormat") is string sourceFormat)
                await Read(detail, new { processId = tab.ProcessId, objectId = id,
                    includeSource = true, includePath = false,
                    sourceFormat }).ConfigureAwait(false);
            else
                await Read(detail, new { processId = tab.ProcessId, objectId = id,
                    includeSource = false, includePath = false }).ConfigureAwait(false);
        }
        return failed;
    }

    private static JsonElement? ToolResultData(string responseJson)
    {
        try
        {
            using var response = JsonDocument.Parse(responseJson);
            var content = response.RootElement.GetProperty("result").GetProperty("content");
            if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0) return null;
            using var payload = JsonDocument.Parse(content[0].GetProperty("text").GetString() ?? "{}");
            return payload.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        { return null; }
    }

    private static string? TextArg(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
