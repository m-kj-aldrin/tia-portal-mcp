using System.Diagnostics;
using System.Net;
using Hypermedia.Datastar;
using TiaOpennessMcpServer.Host;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

// Admission and change notifications are bounded and synchronous. Each response
// task captures managed HTML and owns its sequential SDK writer, never the STA.
internal sealed class DashboardEventStreams : IDisposable
{
    internal const int MaxStreams = 8;
    private readonly object _gate = new();
    private readonly HashSet<DashboardEventSubscription> _streams = new();
    private readonly EngineeringService _engineering;
    private readonly DashboardService _dashboard;
    private readonly DashboardRunStore _runs;
    private readonly LoopbackOriginPolicy _origins;
    private readonly string _formsHtml;
    private long _requiredObservationVersion;
    private bool _stopping;

    public DashboardEventStreams(EngineeringService engineering, DashboardService dashboard,
        LoopbackOriginPolicy origins, DashboardRunStore runs)
    {
        _engineering = engineering;
        _dashboard = dashboard;
        _runs = runs;
        _origins = origins;
        _formsHtml = DashboardSnapshotFragments.RenderToolForms(engineering.WriteToolsAvailable);
        _dashboard.Changed += Signal;
        _runs.Transition += QueueTransition;
    }

    internal int ActiveStreams { get { lock (_gate) return _streams.Count; } }

    internal DashboardEventSubscription? TrySubscribe(string? marker, string? origin, out int status)
    {
        if (marker != "1" || !_origins.Allows(origin)) { status = 403; return null; }
        lock (_gate)
        {
            if (_stopping) { status = 503; return null; }
            if (_streams.Count >= MaxStreams) { status = 429; return null; }
            // A new group of streams must not present an old native observation
            // as fresh. The service monitor reconciles after this lease opens.
            if (_streams.Count == 0)
                _requiredObservationVersion = _engineering.MonitoringObservationVersion + 1;
            var monitoring = _engineering.AcquireMonitoringSubscription();
            var requiredVersion = _requiredObservationVersion;
            var stream = new DashboardEventSubscription(() => Capture(requiredVersion), monitoring, Remove);
            _streams.Add(stream);
            status = 200;
            return stream;
        }
    }

    private DashboardStreamFrame Capture(long requiredObservationVersion)
    {
        var snapshot = _dashboard.CurrentDashboard();
        var runs = _runs.Snapshot();
        var checking = _engineering.MonitoringObservationVersion < requiredObservationVersion;
        var tabFragments = new Dictionary<string, DashboardRunTabFragments>(StringComparer.Ordinal);
        foreach (var tab in snapshot.Tabs)
        {
            var latest = runs.FirstOrDefault(run => run.TabId == tab.Id);
            tabFragments[tab.Id] = new DashboardRunTabFragments(
                () => DashboardRunFragments.RenderTabViews(tab, _runs),
                DashboardRunFragments.RenderStatus(latest, tab.Id),
                DashboardRunFragments.RenderHistory(tab.Id, runs));
        }
        return new DashboardStreamFrame(
            DashboardSnapshotFragments.RenderShared(snapshot, _dashboard.Logs(0, 0).Entries, _engineering, checking),
            _formsHtml,
            () => DashboardRunFragments.RenderViews(snapshot, _runs), tabFragments);
    }

    private void Signal()
    {
        lock (_gate)
            foreach (var stream in _streams) stream.Signal();
    }

    private void QueueTransition(DashboardRunTransition transition)
    {
        DashboardEventSubscription[] streams;
        lock (_gate) streams = _streams.ToArray();
        var note = new DashboardRunEvent(transition.Capture.TabId, transition.Capture.Outcome);
        foreach (var stream in streams) stream.QueueTransition(note);
    }

    private void Remove(DashboardEventSubscription stream)
    {
        lock (_gate) _streams.Remove(stream);
        Signal(); // Remaining streams observe the subscriber count change.
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        var subscription = TrySubscribe(context.Request.Headers["X-Tia-Dashboard"], context.Request.Headers["Origin"], out var status);
        if (subscription == null)
        {
            context.Response.StatusCode = status;
            context.Response.Close();
            return;
        }
        // This path owns the response from this point on. Never let a stream
        // error fall through to the host's JSON error response after SSE starts.
        try { await subscription.RunAsync(new ServerSentEventGenerator(context)).ConfigureAwait(false); }
        catch (ClientDisconnectedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.TraceError("Dashboard event stream stopped: " + ex.Message); }
        finally { subscription.Dispose(); }
    }

    public void Dispose()
    {
        DashboardEventSubscription[] streams;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            streams = _streams.ToArray();
        }
        _dashboard.Changed -= Signal;
        _runs.Transition -= QueueTransition;
        // Release native-monitor leases immediately. HttpHost then closes its
        // listener to unblock any response write that ignores cancellation.
        foreach (var stream in streams) stream.Dispose();
    }
}

internal sealed class DashboardRunTabFragments
{
    private readonly Func<string> _fullHtml;
    public string FullHtml => _fullHtml();
    public string StatusHtml { get; }
    public string HistoryHtml { get; }
    public DashboardRunTabFragments(string fullHtml, string statusHtml, string historyHtml)
        : this(() => fullHtml, statusHtml, historyHtml) { }
    public DashboardRunTabFragments(Func<string> fullHtml, string statusHtml, string historyHtml)
    { _fullHtml = fullHtml; StatusHtml = statusHtml; HistoryHtml = historyHtml; }
}

internal sealed class DashboardStreamFrame
{
    public string SharedHtml { get; }
    public string FormsHtml { get; }
    private readonly Func<string> _initialRunsHtml;
    public string InitialRunsHtml => _initialRunsHtml();
    public IReadOnlyDictionary<string, DashboardRunTabFragments> RunTabs { get; }
    public DashboardStreamFrame(string sharedHtml, string formsHtml, string initialRunsHtml,
        IReadOnlyDictionary<string, DashboardRunTabFragments> runTabs)
        : this(sharedHtml, formsHtml, () => initialRunsHtml, runTabs) { }
    public DashboardStreamFrame(string sharedHtml, string formsHtml, Func<string> initialRunsHtml,
        IReadOnlyDictionary<string, DashboardRunTabFragments> runTabs)
    { SharedHtml = sharedHtml; FormsHtml = formsHtml; _initialRunsHtml = initialRunsHtml; RunTabs = runTabs; }
}

internal sealed class DashboardRunEvent
{
    public string TabId { get; }
    public string Outcome { get; }
    public DashboardRunEvent(string tabId, string outcome) { TabId = tabId; Outcome = outcome; }
}

internal sealed class DashboardEventSubscription : IDisposable
{
    private const int MaxQueuedTransitions = 256;
    private readonly object _gate = new();
    private readonly Func<DashboardStreamFrame> _snapshot;
    private readonly IDisposable _monitoring;
    private readonly Action<DashboardEventSubscription> _remove;
    private readonly TimeSpan _heartbeat;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<DashboardRunEvent> _transitions = new();
    private TaskCompletionSource<bool> _changed = NewSignal();
    private bool _pending = true;
    private bool _disposed;
    private int _running;

    internal DashboardEventSubscription(Func<DashboardStreamFrame> snapshot, IDisposable monitoring,
        Action<DashboardEventSubscription> remove, TimeSpan? heartbeat = null)
    {
        _snapshot = snapshot;
        _monitoring = monitoring;
        _remove = remove;
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(15);
        if (_heartbeat <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(heartbeat));
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Signal()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _changed.TrySetResult(true);
        }
    }

    internal void QueueTransition(DashboardRunEvent transition)
    {
        var overflow = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (_transitions.Count >= MaxQueuedTransitions) overflow = true;
            else
            {
                _transitions.Enqueue(transition);
                _pending = true;
                _changed.TrySetResult(true);
            }
        }
        // An overwhelmed stream reconnects to a complete current GET view.
        // It never silently drops an ordered transition and keeps no unbounded queue.
        if (overflow) Dispose();
    }

    private bool TakePending()
    {
        lock (_gate)
        {
            if (!_pending) return false;
            // Reset before capturing, so a change during capture or transmission
            // remains pending for the following iteration.
            _pending = false;
            _changed = NewSignal();
            return true;
        }
    }

    private DashboardRunEvent[] TakeTransitions()
    {
        lock (_gate)
        {
            var transitions = _transitions.ToArray();
            _transitions.Clear();
            return transitions;
        }
    }

    private Task ChangeTask() { lock (_gate) return _changed.Task; }

    internal async Task RunAsync(ServerSentEventGenerator generator, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A dashboard subscription has only one response writer.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        var token = stop.Token;
        string? previousShared = null;
        var previousTabs = new Dictionary<string, (string Status, string History)>(StringComparer.Ordinal);
        try
        {
            await generator.StartAsync(token).ConfigureAwait(false);
            var heartbeat = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Continuous updates cannot postpone heartbeats.
                if (heartbeat.Elapsed >= _heartbeat)
                {
                    await generator.SendCommentAsync("heartbeat", token).ConfigureAwait(false);
                    heartbeat.Restart();
                }
                if (TakePending())
                {
                    var frame = _snapshot();
                    var transitions = TakeTransitions();
                    if (previousShared == null)
                    {
                        // Datastar receives actual form HTML and a complete
                        // managed view on every safe GET stream connection.
                        await generator.PatchElementsAsync(frame.FormsHtml, cancellationToken: token).ConfigureAwait(false);
                        await generator.PatchElementsAsync(frame.InitialRunsHtml, cancellationToken: token).ConfigureAwait(false);
                        await generator.PatchElementsAsync(frame.SharedHtml, cancellationToken: token).ConfigureAwait(false);
                        previousShared = frame.SharedHtml;
                        foreach (var pair in frame.RunTabs)
                            previousTabs[pair.Key] = (pair.Value.StatusHtml, pair.Value.HistoryHtml);
                    }
                    else
                    {
                        // New tab wrappers are appended without replacing an
                        // inspector where this browser selected an older run.
                        foreach (var pair in frame.RunTabs)
                        {
                            if (previousTabs.ContainsKey(pair.Key)) continue;
                            await generator.PatchElementsAsync(pair.Value.FullHtml,
                                new PatchElementsOptions { Selector = "#dashboard-run-views", Mode = ElementPatchMode.Append },
                                token).ConfigureAwait(false);
                            previousTabs[pair.Key] = (pair.Value.StatusHtml, pair.Value.HistoryHtml);
                        }
                        // These compact events retain start/finish order even if
                        // a slow writer coalesced the surrounding full snapshots.
                        foreach (var transition in transitions)
                        {
                            if (!previousTabs.ContainsKey(transition.TabId)) continue;
                            var status = DashboardRunFragments.RenderStatus(new DashboardRunCapture
                            { TabId = transition.TabId, Outcome = transition.Outcome }, transition.TabId);
                            await generator.PatchElementsAsync(status, cancellationToken: token).ConfigureAwait(false);
                        }
                        if (!string.Equals(previousShared, frame.SharedHtml, StringComparison.Ordinal))
                        {
                            await generator.PatchElementsAsync(frame.SharedHtml, cancellationToken: token).ConfigureAwait(false);
                            previousShared = frame.SharedHtml;
                        }
                        foreach (var pair in frame.RunTabs)
                        {
                            var prior = previousTabs[pair.Key];
                            if (prior.Status != pair.Value.StatusHtml)
                                await generator.PatchElementsAsync(pair.Value.StatusHtml, cancellationToken: token).ConfigureAwait(false);
                            if (prior.History != pair.Value.HistoryHtml)
                                await generator.PatchElementsAsync(pair.Value.HistoryHtml, cancellationToken: token).ConfigureAwait(false);
                            previousTabs[pair.Key] = (pair.Value.StatusHtml, pair.Value.HistoryHtml);
                        }
                        foreach (var gone in previousTabs.Keys.Except(frame.RunTabs.Keys).ToArray())
                        {
                            await generator.RemoveElementsAsync("#run-view-" + gone, cancellationToken: token).ConfigureAwait(false);
                            previousTabs.Remove(gone);
                        }
                    }
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var remaining = _heartbeat - heartbeat.Elapsed;
                var delay = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, wait.Token);
                await Task.WhenAny(ChangeTask(), delay).ConfigureAwait(false);
                wait.Cancel(); // Do not accumulate abandoned heartbeat timers.
            }
        }
        finally
        {
            Dispose();
            try { await generator.CloseAsync().ConfigureAwait(false); }
            catch (ClientDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _changed.TrySetResult(true);
        }
        _stop.Cancel();
        _monitoring.Dispose();
        _remove(this);
        // The writer's linked token still observes _stop until it exits. This
        // source is left for GC instead of racing Dispose with linked callbacks.
    }
}
