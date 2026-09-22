using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Dashboard;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

internal static class ServiceIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("service integration: queued reads retain their original attachment ticket", () => QueuedTicket().GetAwaiter().GetResult());
        yield return ("service integration: presentation observer failures preserve engineering outcomes", () => ObserverFailures().GetAwaiter().GetResult());
        yield return ("service integration: dashboard opens only the stored path of a closed tab", () => ClosedProjectTab().GetAwaiter().GetResult());
        yield return ("service integration: dashboard and MCP actions each journal once", () => SingleJournalEntry().GetAwaiter().GetResult());
        yield return ("service integration: real async MCP calls keep isolated connection attribution", () => ConcurrentAttribution().GetAwaiter().GetResult());
    }

    private static async Task QueuedTicket()
    {
        using var scope = new Scope();
        var original = await scope.Engineering.ConnectAsync(10);
        using var queue = new QueueHold(scope.Sta);
        var disconnect = scope.Engineering.DisconnectAsync(10);
        var reconnect = scope.Engineering.ConnectAsync(10);
        var stale = scope.Engineering.ListDevicesAsync(10);
        queue.Release();
        await disconnect;
        var replacement = await reconnect;
        await Fault("reconnectRequired", async () => await stale);
        Check(replacement.ConnectionId != original.ConnectionId, "Reconnection retained the original ticket.");
        Check(scope.Backend.Processes[10].Reads == 0, "Queued read reached the replacement native attachment.");
        Check((await scope.Engineering.ListDevicesAsync(10)).Complete, "Stale request invalidated the replacement attachment.");
    }

    private static async Task ObserverFailures()
    {
        using var scope = new Scope();
        var snapshotCalls = 0;
        var diagnosticCalls = 0;
        scope.Engineering.SnapshotPublished += _ => throw new InvalidOperationException("Broken presentation observer");
        scope.Engineering.SnapshotPublished += _ => Interlocked.Increment(ref snapshotCalls);
        scope.Engineering.DiagnosticPublished += _ => throw new InvalidOperationException("Broken diagnostic observer");
        scope.Engineering.DiagnosticPublished += _ => Interlocked.Increment(ref diagnosticCalls);
        await scope.Engineering.ConnectAsync(10);
        var result = await scope.Engineering.ListDevicesAsync(10);
        Check(result.Complete && snapshotCalls >= 2, "Observer failure changed the read or stopped the next observer.");
        await scope.Sta.RunAsync(() => scope.Backend.Processes[10].Project = new FakeProject(@"C:\Projects\Changed.ap20"));
        await Fault("reconnectRequired", async () => await scope.Engineering.ListDevicesAsync(10));
        Check(diagnosticCalls == 1, "Diagnostic delivery stopped after an observer failed.");
        Check(scope.Engineering.CurrentSnapshot().Connections.Single().State == "invalidated", "Observer changed context-loss handling.");
    }

    private static async Task ClosedProjectTab()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Dashboard.ConnectAsync(10);
        var tab = Tabs(scope).Single(item => item.GetProperty("processId").GetInt32() == 10);
        var tabId = tab.GetProperty("id").GetString()!;
        foreach (var invalid in new[] { "server", "unknown", tabId, @"C:\Projects\Untrusted.ap20" })
            await Fault("invalidRequest", async () => await scope.Dashboard.OpenProjectAsync(invalid));
        Check(scope.Backend.Opens == 0, "A live tab or supplied path reached the native open adapter.");
        await scope.Sta.RunAsync(() => scope.Backend.Processes[10].Exited = true);
        await scope.Engineering.DiscoverAsync();
        var opened = await scope.Dashboard.OpenProjectAsync(tabId);
        Check(scope.Backend.Opens == 1 && opened.State == "connected" &&
            opened.ApprovedProjectPath == @"C:\Projects\A.ap20", "Closed tab did not open and attach its stored project path once.");
    }

    private static async Task SingleJournalEntry()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        var connection = await scope.Dashboard.ConnectAsync(10);
        var boundary = new McpBoundary(scope.Engineering, Json, _ => false, scope.Dashboard.RecordCall);
        await Call(boundary, 10, "dashboard");
        await scope.Dashboard.DisconnectAsync(10);
        var entries = scope.Dashboard.Logs(0, 0).Entries;
        foreach (var operation in new[] { "connect", "list_devices", "disconnect" })
            Check(entries.Count(entry => entry.Operation == operation) == 1, operation + " was missing or duplicated in dashboard history.");
        var read = entries.Single(entry => entry.Operation == "list_devices");
        Check(read.Origin == "dashboard" && read.ConnectionId == connection.ConnectionId &&
            read.ProjectPath == @"C:\Projects\A.ap20", "Async service attribution did not reach dashboard history.");
    }

    private static async Task ConcurrentAttribution()
    {
        using var scope = new Scope();
        var first = await scope.Engineering.ConnectAsync(10);
        var second = await scope.Engineering.ConnectAsync(20);
        var notes = new ConcurrentQueue<OperationCallNote>();
        var boundary = new McpBoundary(scope.Engineering, Json, _ => false, notes.Enqueue);
        using (var queue = new QueueHold(scope.Sta))
        {
            var readFirst = Call(boundary, 10, "dashboard");
            var readSecond = Call(boundary, 20, "mcp");
            queue.Release();
            await Task.WhenAll(readFirst, readSecond);
        }
        var calls = notes.ToArray();
        Check(calls.Length == 2, "Concurrent calls were missing or journaled twice.");
        var one = calls.Single(note => note.ProcessId == 10);
        var two = calls.Single(note => note.ProcessId == 20);
        Check(one.ConnectionId == first.ConnectionId && one.ProjectPath == @"C:\Projects\A.ap20" && one.Origin == "dashboard",
            "First async call lost or inherited another request's attribution.");
        Check(two.ConnectionId == second.ConnectionId && two.ProjectPath == @"C:\Projects\B.ap20" && two.Origin == "mcp",
            "Second async call lost or inherited another request's attribution.");
        await Call(boundary, null, "mcp");
        var passive = notes.Last();
        Check(passive.ConnectionId == null && passive.ProjectPath == null && passive.ProcessId == null,
            "Passive status inherited attribution from a completed project request.");
        Check(OperationCallContext.Current == null, "Completed request leaked its diagnostic scope.");
    }

    private static async Task Call(McpBoundary boundary, int? processId, string origin)
    {
        using var context = OperationCallContext.Begin(origin);
        using var parameters = JsonDocument.Parse(processId is int id
            ? "{\"name\":\"list_devices\",\"arguments\":{\"processId\":" + id + "}}"
            : "{\"name\":\"get_status\",\"arguments\":{}}");
        var response = await boundary.HandleAsync(new McpRpcRequest { Method = "tools/call", Params = parameters.RootElement });
        var result = JsonSerializer.SerializeToElement(response.result, Json);
        Check(response.rpcErr == null && !result.GetProperty("isError").GetBoolean(), "MCP engineering call failed.");
    }

    private static JsonElement[] Tabs(Scope scope) =>
        JsonSerializer.SerializeToElement(scope.Dashboard.Dashboard(), Json).GetProperty("history").GetProperty("tabs")
            .EnumerateArray().Where(tab => tab.GetProperty("kind").GetString() == "tia").ToArray();

    private static async Task Fault(string code, Func<Task> action)
    {
        try { await action(); }
        catch (ConnectionFault ex) { Check(ex.Code == code, "Expected " + code + ", got " + ex.Code); return; }
        throw new Exception("Expected " + code + " failure.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Scope : IDisposable
    {
        public StaTaskScheduler Sta { get; } = new();
        public FakeConnectionBackend Backend { get; } = new();
        public EngineeringService Engineering { get; }
        public DashboardService Dashboard { get; }

        public Scope()
        {
            Backend.Processes[10] = new FakeProcess(@"C:\Projects\A.ap20");
            Backend.Processes[20] = new FakeProcess(@"C:\Projects\B.ap20");
            Engineering = new EngineeringService(Sta, Backend);
            Engineering.SetMonitoringPausedAsync(true).GetAwaiter().GetResult();
            Dashboard = new DashboardService(Engineering);
        }

        public void Dispose()
        {
            Dashboard.Dispose();
            Engineering.Dispose();
            Sta.Dispose();
        }
    }

    private sealed class QueueHold : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _queued;

        public QueueHold(StaTaskScheduler scheduler)
        {
            _queued = scheduler.RunAsync(() =>
            {
                _entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release the STA queue.");
            });
            if (!_entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("STA queue did not start the test gate.");
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            Release();
            _queued.GetAwaiter().GetResult();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
