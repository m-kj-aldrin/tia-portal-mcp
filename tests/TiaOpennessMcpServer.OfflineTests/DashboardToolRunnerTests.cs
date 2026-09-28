using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Dashboard;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

internal static class DashboardToolRunnerTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("dashboard tool action: one in-process MCP dispatch and one dashboard journal note", () => Dispatch().GetAwaiter().GetResult());
        yield return ("dashboard tool action: tab admission and duplicate IDs cannot dispatch", () => Admission().GetAwaiter().GetResult());
        yield return ("dashboard tool action: strict envelope, active limit and read-only publication", () => StrictAdmission().GetAwaiter().GetResult());
        yield return ("dashboard tool action: admitted call completes without a response writer", () => DetachedCompletion().GetAwaiter().GetResult());
        yield return ("dashboard run store: bounded whole captures and oversized metadata", Retention);
        yield return ("dashboard run fragments: escape source text and expose exact response", Escaping);
        yield return ("dashboard run fragments: indent JSON result without changing captured response", IndentedResult);
        yield return ("dashboard result classification: partial write and compiler errors are distinct", Classification);
    }

    private static async Task Dispatch()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "list_devices",
            arguments = new { processId = 10 }
        }));
        var started = scope.Runner.Begin(document.RootElement);
        Check(started.Outcome == "running" && scope.Runs.Snapshot().Single().Id == started.Id,
            "Run did not enter server history before dispatch.");
        var finished = await scope.Runner.RunAsync(started);
        Check(scope.Backend.Processes[10].Reads == 1, "Tool action did not dispatch exactly once through the service.");
        Check(scope.Notes.Count == 1 && scope.Notes[0].Origin == "dashboard" && scope.Notes[0].Operation == "list_devices",
            "MCP boundary did not journal one dashboard-origin call.");
        Check(finished.Capture.Outcome == "success" && finished.Capture.Completed &&
            finished.ResponseJson.Contains("\"isError\":false", StringComparison.Ordinal),
            "Completed MCP response was not captured intact.");
    }

    private static async Task Admission()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        var id = Guid.NewGuid().ToString("D");
        using var mismatched = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = id, name = "list_devices", arguments = new { processId = 20 } }));
        Check(Fault(() => scope.Runner.Begin(mismatched.RootElement)) && scope.Backend.Processes[10].Reads == 0,
            "Mismatched live tab reached engineering dispatch.");
        using var admitted = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = id, name = "get_status", arguments = new { processId = 10 } }));
        var started = scope.Runner.Begin(admitted.RootElement);
        Check(Fault(() => scope.Runner.Begin(admitted.RootElement)), "Duplicate requestId was admitted.");
        await scope.Runner.RunAsync(started);
        Check(scope.Backend.Processes[10].Reads == 1, "Duplicate action changed native read count.");
    }

    private static void Retention()
    {
        Check(DashboardRunStore.MaxCompleted == 40 && DashboardRunStore.MaxRetainedBytes == 64L * 1024 * 1024,
            "Production history limits changed.");
        var store = new DashboardRunStore(maxCompleted: 2, maxRetainedBytes: 200);
        var transitions = new List<(string Id, bool Started)>();
        store.Transition += item => transitions.Add((item.Capture.Id, item.Started));
        string Add(string payload)
        {
            var id = Guid.NewGuid().ToString("D");
            Check(store.TryStart(id, "server", "get_status", null, "{}", out _) != null, "Store rejected bounded start.");
            store.Finish(id, "success", payload, null);
            return id;
        }
        var first = Add("{\"first\":true}");
        var second = Add("{\"second\":true}");
        var third = Add("{\"third\":true}");
        Check(store.Get(first, "server") == null && store.Get(second, "server") != null && store.Get(third, "server") != null,
            "Retention did not evict the oldest whole completed run.");
        var oversized = Add(new string('x', 300));
        var retained = store.Get(oversized, "server")!;
        Check(!retained.PayloadRetained && retained.ResponseJson == null && retained.PayloadBytes > 200,
            "Oversized capture was truncated or retained rather than explicitly marked.");
        Check(transitions.Count == 8 && transitions[0].Started && !transitions[1].Started,
            "Ordered start/finish notifications were not emitted.");
        store.PruneTabs(new[] { "another-tab" });
        Check(store.Snapshot().Count == 0, "Evicted dashboard tabs retained full run captures.");
    }

    private static async Task StrictAdmission()
    {
        using var scope = new Scope(writesEnabled: false);
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        var id = Guid.NewGuid().ToString("D");
        foreach (var raw in new[]
        {
            "[]",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\"}",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\",\"arguments\":{},\"extra\":1}",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\",\"name\":\"get_status\",\"arguments\":{}}",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\",\"arguments\":{\"processId\":0}}"
        })
        {
            using var document = JsonDocument.Parse(raw);
            Check(Fault(() => scope.Runner.Begin(document.RootElement)), "Malformed dashboard envelope was admitted: " + raw);
        }
        Check(scope.Runs.Snapshot().Count == 0 && scope.Backend.Processes[10].Reads == 0,
            "Admission failure recorded or dispatched a run.");
        var active = new List<DashboardRunCapture>();
        for (var index = 0; index < DashboardRunStore.MaxActive; index++)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "get_status", arguments = new { processId = 10 } }));
            active.Add(scope.Runner.Begin(document.RootElement));
        }
        using (var excess = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "get_status", arguments = new { processId = 10 } })))
        {
            try { scope.Runner.Begin(excess.RootElement); throw new InvalidOperationException("Fifth run was admitted."); }
            catch (ConnectionFault ex) { Check(ex.Code == "busy", "Fifth run returned the wrong admission fault."); }
        }
        foreach (var started in active) await scope.Runner.RunAsync(started);
        using var compile = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "compile_plc", arguments = new { processId = 10, plcObjectId = "CPU" } }));
        var rejected = await scope.Runner.RunAsync(scope.Runner.Begin(compile.RootElement));
        Check(rejected.Capture.Outcome == "error" && rejected.ResponseJson.Contains("unknownTool", StringComparison.Ordinal) &&
            scope.Backend.Processes[10].Compiles == 0, "Read-only profile dispatched an unpublished modifying tool.");
    }

    private static async Task DetachedCompletion()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "list_devices", arguments = new { processId = 10 } }));
        var started = scope.Runner.Begin(document.RootElement);
        var invocation = scope.Runner.RunAsync(started);
        // The HTTP action can lose its SSE writer after admission. The tool
        // task has no response-stream or request-cancellation dependency.
        await invocation;
        Check(scope.Runs.Get(started.Id, tab.Id)?.Completed == true && scope.Backend.Processes[10].Reads == 1,
            "No-writer completion did not retain exactly one result.");
    }

    private static void Escaping()
    {
        var run = new DashboardRunCapture
        {
            Id = Guid.NewGuid().ToString("D"), TabId = "server", Operation = "<img src=x>",
            StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            Outcome = "error", PayloadRetained = true, RequestJson = "{\"x\":\"<script>\"}",
            ResponseJson = "{\"result\":{\"isError\":true,\"content\":[{\"text\":\"<svg onload=x>\"}]}}"
        };
        var html = DashboardRunFragments.RenderInspector(run, "server");
        Check(!html.Contains("<img", StringComparison.Ordinal) && !html.Contains("<script", StringComparison.Ordinal) &&
            !html.Contains("<svg", StringComparison.Ordinal) && html.Contains("data-run-response", StringComparison.Ordinal) &&
            html.Contains("data-run-result", StringComparison.Ordinal) && html.Contains("&lt;svg", StringComparison.Ordinal),
            "Inspector did not HTML-escape exact request/response/result text.");
    }

    private static void IndentedResult()
    {
        var result = "{\"metadata\":{\"name\":\"Device\"},\"deviceItems\":[{\"name\":\"CPU\"}]}";
        var response = JsonSerializer.Serialize(new
        { result = new { content = new[] { new { text = result } } } });
        var run = new DashboardRunCapture
        {
            Id = Guid.NewGuid().ToString("D"), TabId = "server", Operation = "get_device",
            StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            Outcome = "success", PayloadRetained = true, RequestJson = "{\"name\":\"get_device\"}",
            ResponseJson = response
        };
        var html = DashboardRunFragments.RenderInspector(run, "server");
        var resultStart = html.IndexOf("<pre data-run-result", StringComparison.Ordinal);
        var responseStart = html.IndexOf("<pre data-run-response", StringComparison.Ordinal);
        Check(resultStart >= 0 && responseStart > resultStart &&
            html.Substring(resultStart, responseStart - resultStart).Contains("\n  &quot;metadata&quot;: {", StringComparison.Ordinal) &&
            html.Contains(System.Net.WebUtility.HtmlEncode(response) + "</pre>", StringComparison.Ordinal) &&
            run.ResponseJson == response,
            "Result JSON was not indented or the exact MCP response was changed.");

        run.ResponseJson = JsonSerializer.Serialize(new
        { result = new { content = new[] { new { text = "plain text <native>" } } } });
        html = DashboardRunFragments.RenderInspector(run, "server");
        Check(html.Contains("plain text &lt;native&gt;</pre>", StringComparison.Ordinal),
            "Non-JSON result text was changed or rendered without escaping.");
    }

    private static void Classification()
    {
        string Wire(object payload, bool isError) => JsonSerializer.Serialize(new
        { result = new { isError, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } } });
        var partial = DashboardToolRunner.Classify(Wire(new
        { complete = false, affectedObjects = new[] { new { name = "A" } }, errors = new[] { new { message = "Native write failed" } } }, true));
        var compile = DashboardToolRunner.Classify(Wire(new
        { complete = true, compilationSucceeded = false, errors = new[] { new { message = "Compiler error" } } }, true));
        Check(partial.Outcome == "partial" && partial.Error == "Native write failed", "Partial native effects were hidden as a complete failure.");
        Check(compile.Outcome == "error" && compile.Error == "Compiler error", "Compiler diagnostics were mistaken for compilation success.");
    }

    private static bool Fault(Action action)
    {
        try { action(); return false; }
        catch (ConnectionFault ex) { return ex.Code == "invalidRequest"; }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Scope : IDisposable
    {
        public readonly StaTaskScheduler Sta = new();
        public readonly FakeConnectionBackend Backend = new();
        public readonly EngineeringService Engineering;
        public readonly DashboardService Dashboard;
        public readonly DashboardRunStore Runs = new();
        public readonly List<OperationCallNote> Notes = new();
        public readonly DashboardToolRunner Runner;

        public Scope(bool writesEnabled = true)
        {
            Backend.Processes[10] = new FakeProcess(@"C:\Projects\Demo.ap20");
            Engineering = new EngineeringService(Sta, Backend, writesEnabled);
            Dashboard = new DashboardService(Engineering);
            var mcp = new McpBoundary(Engineering, Json, _ => false, Notes.Add);
            Runner = new DashboardToolRunner(Dashboard, Runs, mcp, Json);
        }

        public void Dispose()
        { Dashboard.Dispose(); Engineering.Dispose(); Sta.Dispose(); }
    }
}
