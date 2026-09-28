using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using Hypermedia.Datastar;
using TiaOpennessMcpServer.Host;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

internal sealed class DashboardEndpoints : IDisposable
{
    private readonly EngineeringService _service;
    private readonly DashboardService _dashboard;
    private readonly HttpResponses _http;
    private readonly LoopbackOriginPolicy _origins;
    private readonly Func<Exception?, bool> _isNative;
    private readonly DashboardEventStreams _events;
    private readonly DashboardRunStore _runs;
    private readonly DashboardSelectorStore _selectors;
    private readonly DashboardToolRunner _tools;
    private readonly JsonSerializerOptions _json;
    private const int MaxToolBodyBytes = 16 * 1024 * 1024;
    private const int MaxActionBodyBytes = 16 * 1024;
    private static readonly object AssetGate = new();
    private static readonly Dictionary<string, byte[]> AssetBytes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string File, string ContentType)> Assets = new(StringComparer.Ordinal)
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/dashboard/styles.css"] = ("styles.css", "text/css; charset=utf-8"),
        ["/dashboard/datastar.js"] = ("datastar.js", "text/javascript; charset=utf-8")
    };

    public DashboardEndpoints(EngineeringService service, DashboardService dashboard, HttpResponses http,
        LoopbackOriginPolicy origins, Func<Exception?, bool> isNative, JsonSerializerOptions json, McpBoundary mcp)
    {
        _service = service; _dashboard = dashboard; _http = http; _origins = origins; _isNative = isNative; _json = json;
        _runs = new DashboardRunStore();
        _selectors = new DashboardSelectorStore(dashboard);
        _runs.Changed += _dashboard.NotifyRunChanged;
        _dashboard.Changed += PruneRunTabs;
        _tools = new DashboardToolRunner(dashboard, _runs, mcp, json);
        _events = new DashboardEventStreams(service, dashboard, origins, _runs, _selectors);
    }

    public async Task HandleAsync(HttpListenerContext ctx, string path)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";
        try
        {
            if (req.HttpMethod == "GET" && Assets.TryGetValue(path, out var asset))
            {
                await _http.WriteBytes(res, ReadAsset(asset.File), asset.ContentType);
            }
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/events")
                await _events.HandleAsync(ctx);
            else if (req.HttpMethod == "GET" && (path == "/api/status" || path == "/api/dashboard/status"))
                await _http.Json(res, _dashboard.Status());
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/dashboard")
                await _http.Json(res, _dashboard.Dashboard());
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/logs")
            {
                long after = 0;
                int generation = 0;
                long.TryParse(req.QueryString["after"], out after);
                int.TryParse(req.QueryString["generation"], out generation);
                await _http.Json(res, _dashboard.Logs(after < 0 ? 0 : after, generation));
            }
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/tool-forms")
                await _http.WriteBytes(res, Encoding.UTF8.GetBytes(DashboardToolForms.Render(McpBoundary.ToolDefs(_service.WriteToolsAvailable))), "text/html; charset=utf-8");
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/runs/view")
                await HandleRunViewAsync(ctx);
            else if (req.HttpMethod == "GET" && path == "/api/dashboard/processes")
                await _http.Json(res, await _service.DiscoverAsync());
            else if (req.HttpMethod == "POST" && path == "/api/dashboard/tools/run")
                await HandleToolActionAsync(ctx);
            else if (req.HttpMethod == "POST" &&
                     (path == "/api/dashboard/connect" || path == "/api/dashboard/disconnect" ||
                      path == "/api/dashboard/tabs/dismiss" ||
                      path == "/api/dashboard/projects/open"))
                await HandleConnectionActionAsync(ctx, path);
            else
                await _http.Json(res, new { error = "Unknown dashboard route." }, 404);
        }
        catch (ConnectionFault ex)
        {
            var causeOrigin = _isNative(ex.InnerException) ? "tia-openness" : "bridge";
            var errors = new List<DiscoveryError>
            {
                new() { Origin = "bridge", Operation = path, Message = ex.Message }
            };
            if (ex.InnerException != null)
                errors.Add(new DiscoveryError { Origin = causeOrigin, Operation = path, Message = ex.InnerException.Message });
            await _http.Json(res, new { readAtUtc = DateTimeOffset.UtcNow, ex.ProcessId,
                errors,
                error = new { ex.Code, ex.Message, ex.ProcessId, ex.ReconnectRequired,
                causeOrigin = ex.InnerException == null ? null : causeOrigin,
                causeType = ex.InnerException?.GetType().Name, causeMessage = ex.InnerException?.Message } },
                ex.Code == "invalidRequest" ? 400 : ex.Code == "busy" ? 429 : 409);
        }
        catch (JsonException ex) { await _http.Json(res, new { error = new { code = "invalidRequest", message = ex.Message } }, 400); }
    }

    private bool Authorized(HttpListenerRequest request) =>
        request.Headers["X-Tia-Dashboard"] == "1" && _origins.Allows(request.Headers["Origin"]);

    private static bool IsJson(HttpListenerRequest request) =>
        string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, int limit)
    {
        if (request.ContentLength64 == 0 || request.ContentLength64 > limit)
            throw new ConnectionFault("invalidRequest", 0, $"A JSON request body of at most {limit / 1024} KiB is required.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > limit)
                throw new ConnectionFault("invalidRequest", 0, $"The JSON request exceeds {limit / 1024} KiB.");
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length == 0) throw new ConnectionFault("invalidRequest", 0, "A JSON request body is required.");
        try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { throw new ConnectionFault("invalidRequest", 0, "The JSON request must be valid UTF-8."); }
    }

    private async Task HandleToolActionAsync(HttpListenerContext ctx)
    {
        if (!Authorized(ctx.Request))
        { await _http.Json(ctx.Response, new { error = "Use the dashboard on this server." }, 403); return; }
        DashboardRunCapture started;
        string? loadSourceFor = null;
        try
        {
            if (!IsJson(ctx.Request)) throw new ConnectionFault("invalidRequest", 0, "The tool action requires application/json.");
            using var body = JsonDocument.Parse(await ReadBodyAsync(ctx.Request, MaxToolBodyBytes));
            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("loadSourceFor", out var target) && target.ValueKind == JsonValueKind.String)
                loadSourceFor = target.GetString();
            started = body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("fields", out _)
                ? _tools.BeginFields(body.RootElement, _service.WriteToolsAvailable)
                : _tools.Begin(body.RootElement);
        }
        catch (Exception ex) when (ex is ConnectionFault or JsonException)
        { await SendActionErrorAsync(ctx, ex.Message); return; }

        // Start once before touching the response. If the browser goes away,
        // the admitted MCP call still runs to completion and enters history.
        var context = _selectors.Capture(started);
        var invocation = RunAndRefreshAsync(started, context);
        var writer = new ServerSentEventGenerator(ctx);
        try
        {
            await writer.StartAsync();
            await writer.PatchElementsAsync(DashboardRunFragments.RenderStatus(started, started.TabId));
            await writer.PatchElementsAsync(DashboardRunFragments.RenderInspector(started, started.TabId));
            var (finished, readbackFailed) = await invocation.ConfigureAwait(false);
            await writer.PatchElementsAsync(DashboardRunFragments.RenderStatus(finished.Capture, started.TabId));
            await writer.PatchElementsAsync(DashboardRunFragments.RenderInspector(finished.Capture, started.TabId));
            await writer.PatchElementsAsync(DashboardRunFragments.RenderHistory(started.TabId, _runs.Snapshot()));
            var sourceSignals = DashboardRunFragments.SourceSignals(finished,
                _dashboard.CurrentDashboard(), context, loadSourceFor);
            if (sourceSignals != null) await writer.PatchSignalsAsync(sourceSignals);
            if (readbackFailed)
                await writer.PatchElementsAsync(ActionMessage(
                    "A follow-up read failed or could not start. Inspect its run history and the TIA project before another write.", true));
        }
        catch (Exception ex)
        {
            await invocation.ConfigureAwait(false);
            Trace.TraceError("Dashboard tool response stream stopped: " + ex.Message);
        }
        finally
        {
            try { await writer.CloseAsync(); }
            catch (Exception ex) when (ex is ClientDisconnectedException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task<(DashboardRunCompletion Completion, bool ReadbackFailed)> RunAndRefreshAsync(
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
                var read = _tools.Begin(envelope);
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
            else
                await Read(detail, new { processId = tab.ProcessId, objectId = id,
                    includeSource = true, includePath = false,
                    sourceFormat = TextArg(args, "sourceFormat") ?? "best" }).ConfigureAwait(false);
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

    private async Task HandleRunViewAsync(HttpListenerContext ctx)
    {
        if (!Authorized(ctx.Request))
        { await _http.Json(ctx.Response, new { error = "Use the dashboard on this server." }, 403); return; }
        var tabId = ctx.Request.QueryString["tabId"];
        var runId = ctx.Request.QueryString["runId"];
        if (string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(runId) ||
            !_dashboard.CurrentDashboard().Tabs.Any(tab => tab.Id == tabId) ||
            _runs.Get(runId, tabId) is not DashboardRunCapture capture)
        { await SendActionErrorAsync(ctx, "This run is no longer in server history."); return; }
        var writer = new ServerSentEventGenerator(ctx);
        try
        {
            await writer.StartAsync();
            await writer.PatchElementsAsync(DashboardRunFragments.RenderStatus(capture, tabId));
            await writer.PatchElementsAsync(DashboardRunFragments.RenderInspector(capture, tabId));
        }
        catch (Exception ex) when (ex is ClientDisconnectedException or IOException or HttpListenerException or ObjectDisposedException) { }
        finally
        {
            try { await writer.CloseAsync(); }
            catch (Exception ex) when (ex is ClientDisconnectedException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task HandleConnectionActionAsync(HttpListenerContext ctx, string path)
    {
        if (!Authorized(ctx.Request))
        { await _http.Json(ctx.Response, new { error = "Use the dashboard on this server." }, 403); return; }
        DashboardRunCapture started;
        string? targetTab = null;
        int? processId = null;
        long? expectedRuntimeStartUtcTicks = null;
        string? expectedProjectPath = null;
        Guid? expectedConnectionId = null;
        string? dismissTab = null;
        JsonElement root;
        try
        {
            if (!IsJson(ctx.Request)) throw new ConnectionFault("invalidRequest", 0, "The dashboard action requires application/json.");
            using var body = JsonDocument.Parse(await ReadBodyAsync(ctx.Request, MaxActionBodyBytes));
            root = body.RootElement.Clone();
            if (path is "/api/dashboard/tabs/dismiss" or "/api/dashboard/projects/open")
            {
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("tabId", out var tab) || tab.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(tab.GetString()))
                    throw new ConnectionFault("invalidRequest", 0, "Supply only the dashboard tabId.");
                targetTab = tab.GetString();
                if (!_dashboard.CurrentDashboard().Tabs.Any(item => item.Id == targetTab))
                    throw new ConnectionFault("invalidRequest", 0, "The selected dashboard tab no longer exists.");
                if (path == "/api/dashboard/tabs/dismiss") { dismissTab = targetTab; targetTab = DashboardHistory.ServerId; }
            }
            else
            {
                var disconnect = path == "/api/dashboard/disconnect";
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != (disconnect ? 4 : 3) ||
                    !root.TryGetProperty("processId", out var process) ||
                    process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out var pid) || pid <= 0 ||
                    !root.TryGetProperty("expectedRuntimeStartUtcTicks", out var runtime) ||
                    runtime.ValueKind != JsonValueKind.String ||
                    !long.TryParse(runtime.GetString(), NumberStyles.None, CultureInfo.InvariantCulture,
                        out var ticks) || ticks <= 0 ||
                    !root.TryGetProperty("expectedProjectPath", out var projectPath) ||
                    projectPath.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new ConnectionFault("invalidRequest", 0,
                        "Supply the displayed process, runtime identity and project path.");
                processId = pid;
                expectedRuntimeStartUtcTicks = ticks;
                expectedProjectPath = projectPath.ValueKind == JsonValueKind.String ? projectPath.GetString() : null;
                if (disconnect)
                {
                    if (!root.TryGetProperty("expectedConnectionId", out var connection) ||
                        connection.ValueKind != JsonValueKind.String ||
                        !Guid.TryParseExact(connection.GetString(), "D", out var parsed) || parsed == Guid.Empty)
                        throw new ConnectionFault("invalidRequest", 0,
                            "Supply the displayed connection identity for disconnect.");
                    expectedConnectionId = parsed;
                }
                var matching = _dashboard.CurrentDashboard().Tabs.FirstOrDefault(tab =>
                    tab.Live && tab.ProcessId == pid && tab.RuntimeIdentity == runtime.GetString() &&
                    string.Equals(tab.ProjectPath, expectedProjectPath, StringComparison.Ordinal) &&
                    (!disconnect || tab.ConnectionId == expectedConnectionId));
                if (matching == null)
                    throw new ConnectionFault("reconnectRequired", pid,
                        "The displayed TIA workspace changed. Refresh the dashboard before using this action.");
                targetTab = matching.Id;
            }
            var operation = path.Substring(path.LastIndexOf('/') + 1);
            started = _runs.TryStart(Guid.NewGuid().ToString("D"), targetTab!, operation, processId,
                root.GetRawText(), out var rejection) ?? throw new ConnectionFault("busy", 0, rejection!);
        }
        catch (Exception ex) when (ex is ConnectionFault or JsonException)
        { await SendActionErrorAsync(ctx, ex.Message); return; }

        var action = ExecuteConnectionAsync(path, started, processId, dismissTab,
            expectedRuntimeStartUtcTicks, expectedProjectPath, expectedConnectionId);
        var writer = new ServerSentEventGenerator(ctx);
        try
        {
            await writer.StartAsync();
            await writer.PatchElementsAsync(DashboardRunFragments.RenderStatus(started, started.TabId));
            var finished = await action.ConfigureAwait(false);
            await writer.PatchElementsAsync(DashboardRunFragments.RenderStatus(finished, started.TabId));
            await writer.PatchElementsAsync(DashboardRunFragments.RenderInspector(finished, started.TabId));
            await writer.PatchElementsAsync(DashboardRunFragments.RenderHistory(started.TabId, _runs.Snapshot()));
            await writer.PatchElementsAsync(DashboardSnapshotFragments.RenderShared(_dashboard, _service));
            await writer.PatchElementsAsync(ActionMessage(finished.Outcome == "success" ? "Dashboard action completed." :
                finished.Error ?? "Dashboard action failed.", finished.Outcome == "error"));
        }
        catch (Exception ex)
        {
            await action.ConfigureAwait(false);
            Trace.TraceError("Dashboard action response stream stopped: " + ex.Message);
        }
        finally
        {
            try { await writer.CloseAsync(); }
            catch (Exception ex) when (ex is ClientDisconnectedException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task<DashboardRunCapture> ExecuteConnectionAsync(string path, DashboardRunCapture started,
        int? processId, string? dismissTab, long? expectedRuntimeStartUtcTicks,
        string? expectedProjectPath, Guid? expectedConnectionId)
    {
        string responseJson;
        string outcome;
        string? error = null;
        try
        {
            object? result = path switch
            {
                "/api/dashboard/connect" => await _dashboard.ConnectAsync(processId!.Value,
                    expectedRuntimeStartUtcTicks!.Value, expectedProjectPath),
                "/api/dashboard/disconnect" => await _dashboard.DisconnectAsync(processId!.Value,
                    expectedRuntimeStartUtcTicks!.Value, expectedProjectPath, expectedConnectionId!.Value),
                "/api/dashboard/projects/open" => await _dashboard.OpenProjectAsync(started.TabId),
                "/api/dashboard/tabs/dismiss" => _dashboard.Dismiss(dismissTab),
                _ => throw new InvalidOperationException("Unknown dashboard action.")
            };
            responseJson = JsonSerializer.Serialize(result, _json);
            outcome = "success";
            if (dismissTab != null) _runs.DismissTab(dismissTab);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            var fault = ex as ConnectionFault;
            responseJson = JsonSerializer.Serialize(new
            {
                error = new { code = fault?.Code ?? "bridgeFailure", message = ex.Message,
                    processId = fault?.ProcessId, reconnectRequired = fault?.ReconnectRequired ?? false,
                    causeOrigin = ex.InnerException == null ? null : _isNative(ex.InnerException) ? "tia-openness" : "bridge",
                    causeMessage = ex.InnerException?.Message }
            }, _json);
            outcome = "error";
        }
        var capture = _runs.Finish(started.Id, outcome, responseJson, error);
        if (!capture.PayloadRetained)
        { capture.RequestJson = started.RequestJson; capture.ResponseJson = responseJson; }
        return capture;
    }

    private async Task SendActionErrorAsync(HttpListenerContext ctx, string message)
    {
        var writer = new ServerSentEventGenerator(ctx);
        try
        {
            await writer.StartAsync();
            await writer.PatchElementsAsync(ActionMessage(message, true));
        }
        catch (Exception ex) when (ex is ClientDisconnectedException or IOException or HttpListenerException or ObjectDisposedException) { }
        finally
        {
            try { await writer.CloseAsync(); }
            catch (Exception ex) when (ex is ClientDisconnectedException or IOException or ObjectDisposedException) { }
        }
    }

    private static string ActionMessage(string message, bool error) =>
        "<p id=\"dashboard-action-message\" class=\"" + (error ? "error" : "hint") +
        "\" role=\"status\">" + WebUtility.HtmlEncode(message) + "</p>";

    private void PruneRunTabs()
    {
        var tabs = _dashboard.CurrentDashboard().Tabs.Select(tab => tab.Id).ToArray();
        _runs.PruneTabs(tabs);
        _selectors.Prune(tabs);
    }

    private static byte[] ReadAsset(string file)
    {
        lock (AssetGate)
        {
            if (AssetBytes.TryGetValue(file, out var cached)) return cached;
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dashboard", "wwwroot", file));
            AssetBytes[file] = bytes;
            return bytes;
        }
    }

    public void Dispose()
    {
        _runs.Changed -= _dashboard.NotifyRunChanged;
        _dashboard.Changed -= PruneRunTabs;
        _events.Dispose();
    }
}
