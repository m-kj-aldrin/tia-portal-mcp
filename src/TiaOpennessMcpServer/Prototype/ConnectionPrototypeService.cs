using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class ConnectionPrototypeService : IDisposable
{
    private readonly StaTaskScheduler _sta;
    private readonly ConnectionRegistry _registry;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;
    private volatile bool _stopping;
    private string? _monitorError;
    private int _pending;
    private volatile bool _monitorPaused;
    private const int MaxPending = 32;

    public ConnectionPrototypeService(StaTaskScheduler sta)
    {
        _sta = sta;
        _registry = new ConnectionRegistry(new OpennessConnectionBackend(), sta.VerifyAccess);
        _monitor = MonitorAsync();
    }

    // Passive status never waits for a native call and does not expose native objects.
    public object Status() => new
    {
        mode = "connection-prototype", writeToolsAvailable = false,
        pendingOperations = Volatile.Read(ref _pending), monitorError = _monitorError,
        backgroundMonitoringPaused = _monitorPaused,
        connections = _registry.Views(), events = _registry.Events(),
        samePathReopenVerified = false
    };

    public Task<IReadOnlyList<ProcessObservation>> DiscoverAsync() => Enqueue(_registry.Discover);
    public Task<ConnectionView> ConnectAsync(int processId) => Enqueue(() => _registry.Connect(processId));
    public Task<ConnectionView?> DisconnectAsync(int processId) => Enqueue(() => _registry.Disconnect(processId));
    public Task<bool> SetMonitoringPausedAsync(bool paused) => Enqueue(() => _monitorPaused = paused);

    public Task<GuardedRead> ReadAsync(int processId)
    {
        var ticket = _registry.Capture(processId);
        return Enqueue(() => _registry.Read(ticket));
    }

    private async Task<T> Enqueue<T>(Func<T> operation)
    {
        if (_stopping) throw new ConnectionFault("stopping", 0, "The prototype is shutting down.");
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            throw new ConnectionFault("busy", 0, "The prototype request queue is full. Try again after pending work finishes.");
        }
        try
        {
            return await _sta.RunAsync(() =>
            {
                if (_stopping) throw new ConnectionFault("stopping", 0, "The prototype is shutting down.");
                return operation();
            }).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref _pending); }
    }

    private async Task MonitorAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
                await Enqueue(() => { if (!_monitorPaused) _registry.Monitor(); return true; }).ConfigureAwait(false);
                _monitorError = null;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { _monitorError = ex.Message; }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _stop.Cancel();
        _monitor.GetAwaiter().GetResult();
        // Native calls cannot be force-cancelled. Cleanup waits on the same worker.
        _sta.RunAsync(_registry.DisconnectAll).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
