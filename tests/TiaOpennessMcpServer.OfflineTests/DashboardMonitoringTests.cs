using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

internal static class DashboardMonitoringTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("dashboard monitoring: zero subscribers skip native work and queue admission", () => NoSubscribers().GetAwaiter().GetResult());
        yield return ("dashboard monitoring: one and multiple subscribers share one native tick", () => Subscribers().GetAwaiter().GetResult());
        yield return ("dashboard monitoring: last lease release skips a tick already queued on the STA", () => QueuedRelease().GetAwaiter().GetResult());
        yield return ("dashboard monitoring: concurrent lease disposal is idempotent", () => IdempotentLease().GetAwaiter().GetResult());
        yield return ("dashboard monitoring: disposed service rejects subscriptions and releases existing leases safely", DisposedService);
        yield return ("dashboard monitoring: subscribed tick validates a replaced project with the same path", () => RetainedContext().GetAwaiter().GetResult());
        yield return ("dashboard monitoring: status detects context loss without subscribers or automatic attachment", () => OnDemandStatus().GetAwaiter().GetResult());
    }

    private static async Task NoSubscribers()
    {
        using var scope = new Scope();
        using var queue = new QueueHold(scope.Sta);
        var tick = scope.Service.MonitorOnceAsync();
        Check(tick.IsCompleted && scope.Service.PendingOperations == 0, "An unsubscribed tick waited for or admitted STA work.");
        await tick;
        Check(scope.Backend.Discoveries == 0 && scope.Process.Observations == 0 && scope.Backend.Attaches == 0,
            "An unsubscribed tick discovered, validated or attached a native process.");
        Check(scope.Service.MonitoringSubscribers == 0 && !scope.Service.BackgroundMonitoringActive,
            "Monitoring started without a subscription.");
    }

    private static async Task Subscribers()
    {
        using var scope = new Scope();
        await scope.Service.ConnectAsync(10);
        var notifications = 0;
        scope.Service.SnapshotPublished += _ => Interlocked.Increment(ref notifications);
        using var first = scope.Service.AcquireMonitoringSubscription();
        Check(scope.Service.MonitoringSubscribers == 1 && scope.Service.BackgroundMonitoringActive,
            "First subscription did not activate monitoring.");
        var observations = scope.Process.Observations;
        await scope.Service.MonitorOnceAsync();
        Check(scope.Backend.Discoveries == 1 && scope.Process.Observations > observations,
            "Subscribed monitoring did not discover and validate the retained native context.");
        using var second = scope.Service.AcquireMonitoringSubscription();
        Check(scope.Service.MonitoringSubscribers == 2, "Multiple subscriptions were not counted.");
        await scope.Service.MonitorOnceAsync();
        Check(scope.Backend.Discoveries == 2 && scope.Backend.Attaches == 1,
            "Multiple subscriptions duplicated native discovery or created another attachment.");
        first.Dispose();
        Check(scope.Service.MonitoringSubscribers == 1 && scope.Service.BackgroundMonitoringActive,
            "Releasing one of multiple subscriptions stopped monitoring.");
        await scope.Service.MonitorOnceAsync();
        Check(scope.Backend.Discoveries == 3, "Remaining subscription stopped receiving native monitoring.");
        second.Dispose();
        observations = scope.Process.Observations;
        await scope.Service.MonitorOnceAsync();
        Check(scope.Backend.Discoveries == 3 && scope.Process.Observations == observations && scope.Process.Detaches == 0,
            "Last release ran more native monitoring or detached a user-enabled attachment.");
        Check(scope.Service.MonitoringSubscribers == 0 && !scope.Service.BackgroundMonitoringActive && notifications >= 4,
            "Monitoring transitions were not reflected in managed snapshots.");
    }

    private static async Task QueuedRelease()
    {
        using var scope = new Scope();
        await scope.Service.ConnectAsync(10);
        var observations = scope.Process.Observations;
        using var queue = new QueueHold(scope.Sta);
        using var subscription = scope.Service.AcquireMonitoringSubscription();
        var tick = scope.Service.MonitorOnceAsync();
        Check(!tick.IsCompleted && scope.Service.PendingOperations == 1, "Subscribed tick was not queued behind the held STA worker.");
        subscription.Dispose();
        queue.Release();
        await tick;
        Check(scope.Backend.Discoveries == 0 && scope.Process.Observations == observations && scope.Service.PendingOperations == 0,
            "Tick reached native discovery or validation after its last subscription ended.");
    }

    private static async Task IdempotentLease()
    {
        using var scope = new Scope();
        using var first = scope.Service.AcquireMonitoringSubscription();
        using var second = scope.Service.AcquireMonitoringSubscription();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(first.Dispose)));
        Check(scope.Service.MonitoringSubscribers == 1 && scope.Service.BackgroundMonitoringActive,
            "Concurrent repeated lease disposal decremented another subscriber.");
        second.Dispose();
        first.Dispose();
        Check(scope.Service.MonitoringSubscribers == 0 && !scope.Service.BackgroundMonitoringActive,
            "Repeated disposal left a subscriber or a negative count.");
    }

    private static void DisposedService()
    {
        using var scope = new Scope();
        using var subscription = scope.Service.AcquireMonitoringSubscription();
        scope.Service.Dispose();
        Check(scope.Service.MonitoringSubscribers == 0 && !scope.Service.BackgroundMonitoringActive,
            "Disposed service retained active monitoring.");
        try { scope.Service.AcquireMonitoringSubscription(); }
        catch (ObjectDisposedException)
        {
            subscription.Dispose();
            Check(scope.Service.MonitoringSubscribers == 0, "Lease disposal after service shutdown changed the subscriber count.");
            return;
        }
        throw new Exception("Disposed service accepted a new monitoring subscription.");
    }

    private static async Task RetainedContext()
    {
        using var scope = new Scope();
        await scope.Service.ConnectAsync(10);
        await scope.Sta.RunAsync(() => scope.Process.Project = new FakeProject(@"C:\Projects\A.ap20"));
        using var subscription = scope.Service.AcquireMonitoringSubscription();
        await scope.Service.MonitorOnceAsync();
        Check(scope.Backend.Discoveries == 1 && scope.Service.CurrentSnapshot().Connections.Single().State == "invalidated" &&
            scope.Process.Detaches == 1 && scope.Backend.Attaches == 1,
            "Monitoring accepted a replacement project with the same path or reattached automatically.");
    }

    private static async Task OnDemandStatus()
    {
        foreach (var transition in new[] { "exit", "close", "replacement", "stale-proxy", "during-status" })
        {
            using var scope = new Scope();
            await scope.Service.ConnectAsync(10);
            await scope.Sta.RunAsync(() =>
            {
                switch (transition)
                {
                    case "exit": scope.Process.Exited = true; break;
                    case "close": scope.Process.Project = null; break;
                    case "replacement": scope.Process.Project = new FakeProject(@"C:\Projects\A.ap20"); break;
                    case "stale-proxy": scope.Process.Project!.Alive = false; break;
                    case "during-status": scope.Process.DuringRead = () => scope.Process.Project = new FakeProject(@"C:\Projects\A.ap20"); break;
                }
            });
            Check(scope.Service.CurrentSnapshot().Connections.Single().State == "connected", "A zero-subscriber monitor ran before the status request.");
            try { await scope.Service.ReadStatusAsync(10); }
            catch (ConnectionFault ex)
            {
                Check(ex.Code == "reconnectRequired" && scope.Service.CurrentSnapshot().Connections.Single().State == "invalidated" &&
                    scope.Process.Detaches == 1 && scope.Backend.Attaches == 1 && scope.Backend.Discoveries == 0 &&
                    scope.Service.MonitoringSubscribers == 0 && !scope.Process.ClosedByServer,
                    "Status failed to guard " + transition + " without monitoring, or automatically attached/closed the process.");
                Check(scope.Process.Reads == (transition == "during-status" ? 1 : 0),
                    "Status returned or read from the invalid native context for " + transition + ".");
                continue;
            }
            throw new Exception("Status reported a connected native context after " + transition + ".");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Scope : IDisposable
    {
        public StaTaskScheduler Sta { get; } = new();
        public FakeConnectionBackend Backend { get; } = new();
        public FakeProcess Process { get; } = new(@"C:\Projects\A.ap20");
        public EngineeringService Service { get; }

        public Scope()
        {
            Backend.Processes[10] = Process;
            Service = new EngineeringService(Sta, Backend);
        }

        public void Dispose()
        {
            Service.Dispose();
            Sta.Dispose();
        }
    }

    private sealed class QueueHold : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _held;

        public QueueHold(StaTaskScheduler sta)
        {
            _held = sta.RunAsync(() =>
            {
                _entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Monitoring test did not release the STA worker.");
            });
            if (!_entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Monitoring test did not enter the STA worker.");
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            Release();
            _held.GetAwaiter().GetResult();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
