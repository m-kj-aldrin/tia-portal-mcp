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
        yield return ("service integration: queued compilation, export and deletion cannot adopt a replacement attachment", () => QueuedEngineeringOperations().GetAwaiter().GetResult());
        yield return ("service integration: full access guards compilation and deletion while export stays read-only", () => AccessProfiles().GetAwaiter().GetResult());
        yield return ("service integration: compilation and export discard results on context loss without retry", () => EngineeringContextLoss().GetAwaiter().GetResult());
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

    private static CompileRequest CompileRequest(int processId = 10) =>
        TiaOpennessMcpServer.Operations.CompileRequest.Parse(JsonSerializer.SerializeToElement(new { processId, plcObjectId = " cpu /== " }));
    private static ExportTagTableRequest ExportRequest(int processId = 10) =>
        ExportTagTableRequest.Parse(JsonSerializer.SerializeToElement(new { processId, objectId = " table /== " }));
    private static WriteRequest DeleteRequest(string operation = "delete_block") =>
        WriteRequest.Parse(operation, JsonSerializer.SerializeToElement(new { processId = 10, objectId = " native /== " }));

    private static async Task QueuedEngineeringOperations()
    {
        foreach (var operation in new[] { "compile_plc", "export_tag_table", "delete_block", "delete_udt", "delete_tag_table" })
        {
            using var scope = new Scope();
            var original = await scope.Engineering.ConnectAsync(10);
            using var queue = new QueueHold(scope.Sta);
            var disconnect = scope.Engineering.DisconnectAsync(10);
            var reconnect = scope.Engineering.ConnectAsync(10);
            Task stale = operation == "compile_plc" ? scope.Engineering.CompileAsync(CompileRequest()) :
                operation == "export_tag_table" ? scope.Engineering.ExportTagTableAsync(ExportRequest()) : scope.Engineering.WriteAsync(DeleteRequest(operation));
            queue.Release();
            await disconnect;
            var replacement = await reconnect;
            await Fault("reconnectRequired", async () => await stale);
            Check(replacement.ConnectionId != original.ConnectionId && scope.Backend.Processes[10].Reads == 0 &&
                scope.Backend.Processes[10].Compiles == 0 && scope.Backend.Processes[10].Exports == 0, operation + " reached a replacement attachment.");
            Check((await scope.Engineering.ListDevicesAsync(10)).Complete, "Stale operation invalidated the replacement attachment.");
        }
    }

    private static async Task AccessProfiles()
    {
        using var scope = new Scope(writesEnabled: false);
        var readOnlyStatus = JsonSerializer.SerializeToElement(scope.Engineering.BridgeStatus(), Json);
        Check(readOnlyStatus.GetProperty("mcpPublication").GetString() == "twelve-read-only-tools" &&
            readOnlyStatus.GetProperty("implementationPhase").GetString() == "native-compile-delete-export", "Read-only bridge status reports the wrong publication.");
        await Fault("readOnly", async () => await scope.Engineering.CompileAsync(CompileRequest()));
        foreach (var operation in new[] { "delete_block", "delete_udt", "delete_tag_table" })
            await Fault("readOnly", async () => await scope.Engineering.WriteAsync(DeleteRequest(operation)));
        Check(scope.Backend.Attaches == 0 && scope.Backend.Processes[10].Reads == 0 && scope.Engineering.PendingOperations == 0,
            "Read-only rejection queued work, attached or reached native code.");
        await scope.Engineering.ConnectAsync(10);
        await Fault("readOnly", async () => await scope.Engineering.CompileAsync(CompileRequest()));
        var exported = await scope.Engineering.ExportTagTableAsync(ExportRequest());
        Check(exported.Complete && exported.ProcessId == 10 && scope.Backend.Processes[10].Exports == 1 &&
            scope.Backend.Processes[10].Compiles == 0, "Read-only profile rejected export or admitted compilation.");
        using var full = new Scope();
        Check(JsonSerializer.SerializeToElement(full.Engineering.BridgeStatus(), Json).GetProperty("mcpPublication").GetString() == "twenty-four-read-write-tools", "Full-access bridge status reports the wrong publication.");
        await Fault("notConnected", async () => await full.Engineering.CompileAsync(CompileRequest()));
        await full.Engineering.ConnectAsync(10);
        var compiled = await full.Engineering.CompileAsync(CompileRequest());
        Check(compiled.Complete && compiled.CompilationSucceeded == true && !compiled.Saved && compiled.ProcessId == 10 &&
            compiled.PlcObjectId == " cpu /== " && full.Backend.Processes[10].Compiles == 1, "Full-access compile did not use the retained native target once.");
    }

    private static async Task EngineeringContextLoss()
    {
        foreach (var compile in new[] { true, false })
        {
            using var scope = new Scope();
            await scope.Engineering.ConnectAsync(10);
            var process = scope.Backend.Processes[10];
            process.DuringRead = () => process.Project = new FakeProject(@"C:\Projects\A.ap20");
            await Fault("reconnectRequired", async () =>
            {
                if (compile) await scope.Engineering.CompileAsync(CompileRequest());
                else await scope.Engineering.ExportTagTableAsync(ExportRequest());
            });
            Check(process.Reads == 1 && process.Detaches == 1 && (compile ? process.Compiles : process.Exports) == 1 &&
                scope.Engineering.CurrentSnapshot().Connections.Single().State == "invalidated", "Context loss kept or retried a native operation.");
        }
        using var ordinary = new Scope();
        await ordinary.Engineering.ConnectAsync(10);
        ordinary.Backend.Processes[10].ReadError = new UnauthorizedAccessException("Native compile access denied");
        await Fault("nativeCompileFailed", async () => await ordinary.Engineering.CompileAsync(CompileRequest()));
        Check(ordinary.Backend.Processes[10].Compiles == 1 && ordinary.Backend.Processes[10].Detaches == 0 &&
            ordinary.Engineering.CurrentSnapshot().Connections.Single().State == "connected", "Ordinary compile failure invalidated or retried a valid connection.");
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

        public Scope(bool writesEnabled = true)
        {
            Backend.Processes[10] = new FakeProcess(@"C:\Projects\A.ap20");
            Backend.Processes[20] = new FakeProcess(@"C:\Projects\B.ap20");
            Engineering = new EngineeringService(Sta, Backend, writesEnabled);
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
