using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Hypermedia.Datastar;
using TiaOpennessMcpServer.Host;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

// Each response owns its SDK writer. Notifications coalesce into current
// managed views; the retained capture, rather than a badge transition, is truth.
internal sealed class DashboardEventStreams : IDisposable
{
    internal const int MaxStreams = 8;
    private readonly object _gate = new();
    private readonly HashSet<DashboardEventSubscription> _streams = new();
    private readonly Dictionary<string, (string Signature, string Html)> _forms = new(StringComparer.Ordinal);
    private readonly EngineeringService _engineering;
    private readonly DashboardService _dashboard;
    private readonly DashboardRunStore _runs;
    private readonly DashboardSelectorStore _selectors;
    private readonly LoopbackOriginPolicy _origins;
    private long _requiredObservationVersion;
    private bool _stopping;

    public DashboardEventStreams(EngineeringService engineering, DashboardService dashboard,
        LoopbackOriginPolicy origins, DashboardRunStore runs, DashboardSelectorStore selectors)
    {
        _engineering = engineering; _dashboard = dashboard; _origins = origins;
        _runs = runs; _selectors = selectors;
        _dashboard.Changed += Signal;
    }

    private DashboardEventSubscription? TrySubscribe(string? marker, string? origin, out int status)
    {
        if (marker != "1" || !_origins.Allows(origin)) { status = 403; return null; }
        lock (_gate)
        {
            if (_stopping) { status = 503; return null; }
            if (_streams.Count >= MaxStreams) { status = 429; return null; }
            if (_streams.Count == 0)
                _requiredObservationVersion = _engineering.MonitoringObservationVersion + 1;
            var requiredVersion = _requiredObservationVersion;
            var streamId = Guid.NewGuid().ToString("N");
            var stream = new DashboardEventSubscription(() => Capture(requiredVersion, streamId),
                _engineering.AcquireMonitoringSubscription(), Remove);
            _streams.Add(stream);
            status = 200;
            return stream;
        }
    }

    private DashboardStreamFrame Capture(long requiredObservationVersion, string streamId)
    {
        var snapshot = _dashboard.CurrentDashboard();
        var runs = _runs.SnapshotState();
        var checking = _engineering.MonitoringObservationVersion < requiredObservationVersion;
        var definitions = McpBoundary.ToolDefs(_engineering.WriteToolsAvailable);
        var forms = new StringBuilder("<div id=\"dashboard-forms\">");
        var histories = new StringBuilder("<div id=\"dashboard-run-history\">");
        foreach (var tab in snapshot.Tabs)
        {
            var prefix = DashboardSelectorStore.SignalPrefix(snapshot, tab);
            var (options, cpu) = _selectors.ForTab(tab);
            var ready = tab.Kind == "tia" && tab.Live && tab.ConnectionState == "connected" &&
                tab.ProjectState == "open" && !string.IsNullOrEmpty(tab.ProjectPath);
            var signature = JsonSerializer.Serialize(new
            {
                prefix, ready, _engineering.WriteToolsAvailable, tab.ProcessId, cpu,
                options = options.Select(pair => new
                {
                    kind = pair.Key, values = pair.Value.Select(value => new { id = value.Id, label = value.Label })
                })
            });
            lock (_forms)
            {
                if (!_forms.TryGetValue(tab.Id, out var cached) || cached.Signature != signature)
                {
                    cached = (signature, DashboardToolForms.Render(definitions, tab.Id, prefix,
                        tab.Kind == "server", tab.ProcessId, ready, options, cpu));
                    _forms[tab.Id] = cached;
                }
                forms.Append(cached.Html);
            }
            histories.Append(DashboardRunFragments.RenderHistory(tab.Id, runs.Captures));
        }
        lock (_forms)
            foreach (var gone in _forms.Keys.Except(snapshot.Tabs.Select(tab => tab.Id)).ToArray()) _forms.Remove(gone);
        return new DashboardStreamFrame(
            DashboardSnapshotFragments.RenderShared(snapshot, _dashboard.Logs(0, 0).Entries,
                _engineering, checking, streamId),
            forms.Append("</div>").ToString(), histories.Append("</div>").ToString(),
            DashboardClientState.RenderRuns(snapshot, runs), DashboardClientState.RenderPrune(snapshot));
    }

    private void Signal()
    {
        lock (_gate) foreach (var stream in _streams) stream.Signal();
    }

    private void Remove(DashboardEventSubscription stream)
    {
        lock (_gate) _streams.Remove(stream);
        Signal();
    }

    public async Task HandleAsync(HttpListenerContext context)
    {
        var subscription = TrySubscribe(context.Request.Headers["X-Tia-Dashboard"],
            context.Request.Headers["Origin"], out var status);
        if (subscription == null) { context.Response.StatusCode = status; context.Response.Close(); return; }
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
        foreach (var stream in streams) stream.Dispose();
    }
}

internal sealed class DashboardStreamFrame
{
    public string SharedHtml { get; }
    public string FormsHtml { get; }
    public string HistoryHtml { get; }
    public string MetadataHtml { get; }
    public string PruneHtml { get; }
    public DashboardStreamFrame(string shared, string forms, string history, string metadata, string prune)
    { SharedHtml = shared; FormsHtml = forms; HistoryHtml = history; MetadataHtml = metadata; PruneHtml = prune; }
}

internal sealed class DashboardEventSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<DashboardStreamFrame> _snapshot;
    private readonly IDisposable _monitoring;
    private readonly Action<DashboardEventSubscription> _remove;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<bool> _changed = NewSignal();
    private bool _pending = true;
    private bool _disposed;

    internal DashboardEventSubscription(Func<DashboardStreamFrame> snapshot, IDisposable monitoring,
        Action<DashboardEventSubscription> remove)
    { _snapshot = snapshot; _monitoring = monitoring; _remove = remove; }

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

    internal async Task RunAsync(ServerSentEventGenerator writer)
    {
        var token = _stop.Token;
        var previous = new string?[5];
        var heartbeat = Stopwatch.StartNew();
        try
        {
            await writer.StartAsync(token).ConfigureAwait(false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (heartbeat.Elapsed >= TimeSpan.FromSeconds(15))
                {
                    await writer.SendCommentAsync("heartbeat", token).ConfigureAwait(false);
                    heartbeat.Restart();
                }
                bool pending;
                Task changed;
                lock (_gate)
                {
                    pending = _pending;
                    if (pending) { _pending = false; _changed = NewSignal(); }
                    changed = _changed.Task;
                }
                if (pending)
                {
                    var frame = _snapshot();
                    var regions = new[] { frame.SharedHtml, frame.FormsHtml, frame.HistoryHtml, frame.MetadataHtml, frame.PruneHtml };
                    for (var i = 0; i < regions.Length; i++)
                        if (previous[i] != regions[i])
                        {
                            await writer.PatchElementsAsync(regions[i], cancellationToken: token).ConfigureAwait(false);
                            previous[i] = regions[i];
                        }
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var remaining = TimeSpan.FromSeconds(15) - heartbeat.Elapsed;
                await Task.WhenAny(changed, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, wait.Token)).ConfigureAwait(false);
                wait.Cancel();
            }
        }
        finally
        {
            Dispose();
            try { await writer.CloseAsync().ConfigureAwait(false); }
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
    }
}
