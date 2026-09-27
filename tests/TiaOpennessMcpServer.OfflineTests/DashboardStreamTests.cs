using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypermedia.Datastar;
using TiaOpennessMcpServer.Dashboard;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Host;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

internal static class DashboardStreamTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("dashboard streams: SDK emits exact signal, element and heartbeat bytes", () => Framing().GetAwaiter().GetResult());
        yield return ("dashboard streams: admission checks marker, origin and eight-stream capacity before monitoring", Admission);
        yield return ("dashboard streams: slow writer coalesces snapshots without blocking notifications or losing latest state", () => Coalescing().GetAwaiter().GetResult());
        yield return ("dashboard streams: continuous changes cannot postpone ordered heartbeats", () => Heartbeats().GetAwaiter().GetResult());
        yield return ("dashboard streams: cancellation, startup and write failures release monitoring once without retry", () => Failures().GetAwaiter().GetResult());
        yield return ("dashboard streams: initial managed snapshot includes explicit nulls and subscription status", () => ManagedSnapshot().GetAwaiter().GetResult());
        yield return ("dashboard streams: isolated observers receive native snapshots and journal changes", () => ChangeObservers().GetAwaiter().GetResult());
    }

    private static async Task Framing()
    {
        using var stream = new CaptureStream();
        var generator = new ServerSentEventGenerator(stream, leaveOpen: true);
        await generator.StartAsync();
        var firstFlush = stream.Flushes;
        await generator.StartAsync();
        Check(firstFlush == 1 && stream.Flushes == 1, "SDK startup must flush once and be idempotent.");
        await generator.PatchSignalsAsync("{\r\n  \"ready\": true\r\n}\r\n", onlyIfMissing: true, eventId: "7", retryDuration: 2000);
        await generator.PatchElementsAsync("<div id=\"status\">Connected</div>");
        await generator.SendCommentAsync("heartbeat\r\nnext");
        var expected = "event: datastar-patch-signals\nid: 7\nretry: 2000\ndata: onlyIfMissing true\n" +
            "data: signals {\ndata: signals   \"ready\": true\ndata: signals }\ndata: signals \n\n" +
            "event: datastar-patch-elements\ndata: elements <div id=\"status\">Connected</div>\n\n" +
            ": heartbeat\n: next\n\n";
        Check(stream.Content == expected, "SDK framing differs from the pinned Datastar protocol. Actual: " + stream.Content);
        Check(stream.Flushes == 4 && stream.ToArray().Take(3).SequenceEqual(Encoding.UTF8.GetBytes("eve")), "Each event must flush and UTF-8 must omit BOM.");
        await generator.CloseAsync();
        Check(stream.CanWrite, "leaveOpen stream constructor closed the caller-owned stream.");
    }

    private static void Admission()
    {
        using var scope = new Scope();
        foreach (var entry in new (string? Marker, string? Origin)[]
        {
            (null, null), ("0", null), ("1", "null"), ("1", "http://example.org:5000"), ("1", "http://localhost:5001")
        })
        {
            Check(scope.Streams.TrySubscribe(entry.Marker, entry.Origin, out var status) == null && status == 403,
                "Invalid stream admission was accepted.");
        }
        Check(scope.Engineering.MonitoringSubscribers == 0 && scope.Streams.ActiveStreams == 0, "Rejected admission acquired monitoring.");
        var admitted = Enumerable.Range(0, DashboardEventStreams.MaxStreams)
            .Select(index => scope.Streams.TrySubscribe("1", "http://localhost:5000", out _)!).ToArray();
        Check(admitted.All(item => item != null) && scope.Engineering.MonitoringSubscribers == 8 && scope.Streams.ActiveStreams == 8,
            "Eight accepted streams did not own exactly eight monitoring subscriptions.");
        Check(scope.Streams.TrySubscribe("1", null, out var fullStatus) == null && fullStatus == 429, "Ninth stream was not rejected before initialization.");
        admitted[0].Dispose();
        admitted[0].Dispose();
        using var replacement = scope.Streams.TrySubscribe("1", "http://127.0.0.1:5000", out var restoredStatus);
        Check(restoredStatus == 200 && replacement != null && scope.Engineering.MonitoringSubscribers == 8, "Released capacity could not be reused.");
        scope.Streams.Dispose();
        Check(scope.Streams.ActiveStreams == 0 && scope.Engineering.MonitoringSubscribers == 0, "Shutdown retained stream leases.");
        Check(scope.Streams.TrySubscribe("1", null, out var stoppedStatus) == null && stoppedStatus == 503, "Shutdown admitted another stream.");
        Check(scope.Backend.Attaches == 0 && scope.Backend.Discoveries == 0, "Stream admission attached or directly discovered TIA.");
    }

    private static async Task Coalescing()
    {
        var revision = 0;
        var captures = 0;
        var lease = new Lease();
        using var stream = new CaptureStream { BlockFirstWrite = true };
        using var subscription = new DashboardEventSubscription(() =>
        {
            Interlocked.Increment(ref captures);
            return JsonSerializer.Serialize(new { revision = Volatile.Read(ref revision) });
        }, lease, _ => { });
        var writer = subscription.RunAsync(new ServerSentEventGenerator(stream, leaveOpen: true));
        await Bounded(stream.FirstWrite.Task);
        var notifications = Task.Run(() =>
        {
            for (var value = 1; value <= 100; value++) { Volatile.Write(ref revision, value); subscription.Signal(); }
        });
        await Bounded(notifications);
        Check(captures == 1, "Notifications captured snapshots on the publisher or waited for its slow writer.");
        stream.ReleaseWrite.TrySetResult(true);
        await Until(() => stream.Content.Contains("\"revision\":100"));
        subscription.Dispose();
        await Cancelled(writer);
        Check(stream.Content.Split("event: datastar-patch-signals").Length - 1 == 2 && captures == 2,
            "Pending snapshots accumulated instead of coalescing to latest state.");
        Check(stream.MaxWriters == 1 && lease.Releases == 1, "Response sends overlapped or monitoring was released twice.");
    }

    private static async Task Heartbeats()
    {
        var revision = 0;
        using var stream = new CaptureStream();
        using var subscription = new DashboardEventSubscription(() => JsonSerializer.Serialize(new { revision = Volatile.Read(ref revision) }),
            new Lease(), _ => { }, TimeSpan.FromMilliseconds(30));
        var writer = subscription.RunAsync(new ServerSentEventGenerator(stream, leaveOpen: true));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMilliseconds(140))
        {
            Interlocked.Increment(ref revision);
            subscription.Signal();
            await Task.Delay(4);
        }
        subscription.Dispose();
        await Cancelled(writer);
        Check(stream.Content.Split(": heartbeat\n\n").Length - 1 >= 2, "Frequent changes postponed the heartbeat.");
        Check(stream.MaxWriters == 1, "Heartbeat and snapshot writes overlapped.");
    }

    private static async Task Failures()
    {
        foreach (var failure in new[] { "startup", "write", "cancelled-write", "cancelled-idle" })
        {
            var lease = new Lease();
            var removed = 0;
            using var stream = new CaptureStream
            {
                FailStart = failure == "startup", FailWrite = failure == "write", BlockFirstWrite = failure == "cancelled-write"
            };
            using var subscription = new DashboardEventSubscription(() => "{\"ready\":true}", lease, _ => Interlocked.Increment(ref removed));
            var generator = new ServerSentEventGenerator(stream, leaveOpen: true);
            var writer = subscription.RunAsync(generator);
            if (failure == "cancelled-write") await Bounded(stream.FirstWrite.Task);
            if (failure == "cancelled-idle") await Until(() => stream.Content.Contains("\"ready\":true"));
            if (failure.StartsWith("cancelled", StringComparison.Ordinal)) subscription.Dispose();
            try { await Bounded(writer); throw new Exception("Expected stream termination."); }
            catch (ClientDisconnectedException) { Check(failure is "startup" or "write", "Unexpected disconnect classification."); }
            catch (OperationCanceledException) { Check(failure.StartsWith("cancelled", StringComparison.Ordinal), "Unexpected cancellation classification."); }
            subscription.Dispose();
            Check(lease.Releases == 1 && removed == 1 && stream.WriteAttempts <= 1, "Stream failure retained ownership, released twice or retried a send.");
            Check(stream.CanWrite, "SDK ignored leaveOpen during failure cleanup.");
        }
    }

    private static async Task ManagedSnapshot()
    {
        using var scope = new Scope();
        using var subscription = scope.Streams.TrySubscribe("1", null, out _)!;
        using var output = new CaptureStream();
        var writer = subscription.RunAsync(new ServerSentEventGenerator(output, leaveOpen: true));
        await Until(() => output.Content.Contains("data: signals "));
        var json = output.Content.Split('\n').Single(line => line.StartsWith("data: signals ", StringComparison.Ordinal))[14..];
        using var parsed = JsonDocument.Parse(json);
        var status = parsed.RootElement.GetProperty("status");
        Check(status.GetProperty("monitoringSubscribers").GetInt32() == 1 && status.GetProperty("backgroundMonitoringActive").GetBoolean(),
            "Snapshot omitted current subscription status.");
        Check(status.GetProperty("monitorError").ValueKind == JsonValueKind.Null, "Null values were omitted from a complete signal snapshot.");
        Check(parsed.RootElement.GetProperty("dashboard").GetProperty("history").GetProperty("tabs").GetArrayLength() == 1,
            "Current managed dashboard history was omitted.");
        subscription.Dispose();
        await Cancelled(writer);
        Check(scope.Engineering.MonitoringSubscribers == 0 && scope.Streams.ActiveStreams == 0, "Stopped response retained monitoring or capacity.");
    }

    private static async Task ChangeObservers()
    {
        using var scope = new Scope();
        var changes = 0;
        scope.Dashboard.Changed += () => throw new InvalidOperationException("Broken stream observer");
        scope.Dashboard.Changed += () => Interlocked.Increment(ref changes);
        await scope.Engineering.DiscoverAsync();
        await scope.Dashboard.ConnectAsync(10);
        var prior = changes;
        scope.Dashboard.RecordCall(new OperationCallNote { Operation = "get_status", Outcome = "success", Origin = "mcp" });
        Check(changes == prior + 1, "Journal update did not notify the surviving observer.");
        await scope.Sta.RunAsync(() => scope.Backend.Processes[10].Project = null);
        try { await scope.Engineering.ReadStatusAsync(10); throw new Exception("Expected invalidation."); }
        catch (ConnectionFault ex) { Check(ex.Code == "reconnectRequired", "Unexpected context-loss failure."); }
        Check(changes > prior + 2 && scope.Dashboard.Logs(0, 0).Entries.Any(entry => entry.Operation == "invalidated"),
            "Native invalidation snapshot/diagnostic notifications were lost.");
        Check(scope.Backend.Processes[10].Detaches == 1 && !scope.Backend.Processes[10].ClosedByServer,
            "Presentation observers changed native cleanup behavior.");
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Expected stream observation did not arrive.");
            await Task.Delay(5);
        }
    }

    private static async Task Bounded(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new TimeoutException("Stream operation did not terminate.");
        await task;
    }

    private static async Task Cancelled(Task task)
    {
        try { await Bounded(task); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected canceled stream writer.");
    }

    private sealed class Lease : IDisposable
    {
        public int Releases;
        public void Dispose() => Interlocked.Increment(ref Releases);
    }

    private sealed class CaptureStream : MemoryStream
    {
        private readonly object _gate = new();
        private int _writers;
        public bool BlockFirstWrite, FailStart, FailWrite;
        public int Flushes, WriteAttempts, MaxWriters;
        public readonly TaskCompletionSource<bool> FirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Content { get { lock (_gate) return Encoding.UTF8.GetString(ToArray()); } }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Flushes);
            cancellationToken.ThrowIfCancellationRequested();
            if (FailStart && Flushes == 1) throw new IOException("Startup flush failed.");
            return Task.CompletedTask;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref WriteAttempts);
            var writers = Interlocked.Increment(ref _writers);
            MaxWriters = Math.Max(MaxWriters, writers);
            try
            {
                if (attempt == 1)
                {
                    FirstWrite.TrySetResult(true);
                    if (BlockFirstWrite)
                    {
                        using var cancelled = cancellationToken.Register(() => ReleaseWrite.TrySetCanceled(cancellationToken));
                        await ReleaseWrite.Task;
                    }
                }
                if (FailWrite) throw new IOException("Write disconnected.");
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) base.Write(buffer, offset, count);
            }
            finally { Interlocked.Decrement(ref _writers); }
        }
    }

    private sealed class Scope : IDisposable
    {
        public StaTaskScheduler Sta { get; } = new();
        public FakeConnectionBackend Backend { get; } = new();
        public EngineeringService Engineering { get; }
        public DashboardService Dashboard { get; }
        public DashboardEventStreams Streams { get; }

        public Scope()
        {
            Backend.Processes[10] = new FakeProcess(@"C:\Projects\A.ap20");
            Engineering = new EngineeringService(Sta, Backend);
            Dashboard = new DashboardService(Engineering);
            Streams = new DashboardEventStreams(Engineering, Dashboard,
                new LoopbackOriginPolicy(new Uri("http://127.0.0.1:5000/")), new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
        }

        public void Dispose() { Streams.Dispose(); Dashboard.Dispose(); Engineering.Dispose(); Sta.Dispose(); }
    }
}
