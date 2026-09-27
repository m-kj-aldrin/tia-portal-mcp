using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hypermedia.Datastar;
using TiaOpennessMcpServer.Host;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

// Admission and change notifications are bounded and synchronous. Each response
// task captures managed DTOs and owns its sequential SDK writer, never the STA.
internal sealed class DashboardEventStreams : IDisposable
{
    internal const int MaxStreams = 8;
    private readonly object _gate = new();
    private readonly HashSet<DashboardEventSubscription> _streams = new();
    private readonly EngineeringService _engineering;
    private readonly DashboardService _dashboard;
    private readonly LoopbackOriginPolicy _origins;
    private readonly JsonSerializerOptions _json;
    private bool _stopping;

    public DashboardEventStreams(EngineeringService engineering, DashboardService dashboard,
        LoopbackOriginPolicy origins, JsonSerializerOptions json)
    {
        _engineering = engineering;
        _dashboard = dashboard;
        _origins = origins;
        // Signals represent the whole snapshot: explicit nulls clear fields
        // whose values disappear, instead of retaining them in a future client.
        _json = new JsonSerializerOptions(json) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        _dashboard.Changed += Signal;
    }

    internal int ActiveStreams { get { lock (_gate) return _streams.Count; } }

    internal DashboardEventSubscription? TrySubscribe(string? marker, string? origin, out int status)
    {
        if (marker != "1" || !_origins.Allows(origin)) { status = 403; return null; }
        lock (_gate)
        {
            if (_stopping) { status = 503; return null; }
            if (_streams.Count >= MaxStreams) { status = 429; return null; }
            var monitoring = _engineering.AcquireMonitoringSubscription();
            var stream = new DashboardEventSubscription(SnapshotJson, monitoring, Remove);
            _streams.Add(stream);
            status = 200;
            return stream;
        }
    }

    private string SnapshotJson() => JsonSerializer.Serialize(new
    {
        dashboard = _dashboard.Dashboard(), status = _dashboard.Status()
    }, _json);

    private void Signal()
    {
        lock (_gate)
            foreach (var stream in _streams) stream.Signal();
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
        // Release native-monitor leases immediately. HttpHost then closes its
        // listener to unblock any response write that ignores cancellation.
        foreach (var stream in streams) stream.Dispose();
    }
}

internal sealed class DashboardEventSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<string> _snapshot;
    private readonly IDisposable _monitoring;
    private readonly Action<DashboardEventSubscription> _remove;
    private readonly TimeSpan _heartbeat;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<bool> _changed = NewSignal();
    private bool _pending = true;
    private bool _disposed;
    private int _running;

    internal DashboardEventSubscription(Func<string> snapshot, IDisposable monitoring,
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

    private bool TakePending()
    {
        lock (_gate)
        {
            if (!_pending) return false;
            // Reset before capturing the snapshot, so a change during capture
            // or transmission remains pending for the following iteration.
            _pending = false;
            _changed = NewSignal();
            return true;
        }
    }

    private Task ChangeTask() { lock (_gate) return _changed.Task; }

    internal async Task RunAsync(ServerSentEventGenerator generator, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A dashboard subscription has only one response writer.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        var token = stop.Token;
        string? previous = null;
        try
        {
            await generator.StartAsync(token).ConfigureAwait(false);
            var heartbeat = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Continuous snapshot updates cannot postpone heartbeats.
                if (heartbeat.Elapsed >= _heartbeat)
                {
                    await generator.SendCommentAsync("heartbeat", token).ConfigureAwait(false);
                    heartbeat.Restart();
                }
                if (TakePending())
                {
                    var snapshot = _snapshot();
                    if (!string.Equals(previous, snapshot, StringComparison.Ordinal))
                    {
                        await generator.PatchSignalsAsync(snapshot, cancellationToken: token).ConfigureAwait(false);
                        previous = snapshot;
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
