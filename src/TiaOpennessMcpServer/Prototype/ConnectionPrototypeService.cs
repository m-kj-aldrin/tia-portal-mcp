using System.Diagnostics;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class ConnectionPrototypeService : IDisposable, IMcpReads
{
    private readonly StaTaskScheduler _sta;
    private readonly ConnectionRegistry _registry;
    private readonly DashboardHistory _history = new();
    private readonly Queue<ConnectionEvent> _diagnostics = new();
    private readonly object _diagnosticGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;
    private volatile bool _stopping;
    private string? _monitorError;
    private string? _lastObserveError;
    private int _pending;
    private volatile bool _monitorPaused;
    private const int MaxPending = 32;

    public ConnectionPrototypeService(StaTaskScheduler sta)
    {
        _sta = sta;
        _registry = new ConnectionRegistry(new OpennessConnectionBackend(), sta.VerifyAccess);
        _registry.Listen(OnDiagnostic);
        _monitor = MonitorAsync();
    }

    // Passive status never waits for a native call and does not expose native objects.
    public object Status() => new
    {
        mode = "connection-prototype", writeToolsAvailable = false,
        implementationPhase = "rehaul-mcp-read-only", mcpPublication = "eleven-read-only-tools",
        pendingOperations = Volatile.Read(ref _pending), monitorError = _monitorError,
        backgroundMonitoringPaused = _monitorPaused,
        connections = _registry.Views(), events = _registry.Events(),
        samePathReopenEvidence = PrototypeEvidence.SamePathReopen
    };

    public object Dashboard() => new
    {
        pendingOperations = Volatile.Read(ref _pending), monitorError = _monitorError,
        backgroundMonitoringPaused = _monitorPaused, history = _history.Snapshot()
    };

    public DashboardLogPage Logs(long after, int generation) => _history.ReadLogs(after, generation);

    public object Dismiss(string? tabId)
    {
        if (!_history.Dismiss(tabId))
            throw new ConnectionFault("invalidRequest", 0, "Only a historical TIA tab can be dismissed.");
        return new { dismissed = true, tabId };
    }

    public void RecordCall(McpCallNote note) => _history.Record(new DashboardLogDraft
    {
        Origin = string.IsNullOrWhiteSpace(note.Origin) ? "mcp" : note.Origin,
        Operation = note.Operation,
        ProcessId = note.ProcessId is > 0 ? note.ProcessId : null,
        ConnectionId = note.ConnectionId,
        ProjectPath = note.ProjectPath,
        DurationMs = note.DurationMs,
        Outcome = note.Outcome,
        Error = note.Error
    });

    public void RecordExternal(string origin, string operation, int? processId, double durationMs, string outcome, string? error) =>
        _history.Record(new DashboardLogDraft
        {
            Origin = origin, Operation = operation, ProcessId = processId is > 0 ? processId : null,
            ConnectionId = DashboardCallContext.ConnectionId.Value, ProjectPath = DashboardCallContext.ProjectPath.Value,
            DurationMs = durationMs, Outcome = outcome, Error = error
        });

    public object BridgeStatus() => new
    {
        readAtUtc = DateTimeOffset.UtcNow, accessProfile = "read-only", writeToolsAvailable = false,
        implementationPhase = "rehaul-mcp-read-only", mcpPublication = "eleven-read-only-tools",
        errors = Array.Empty<DiscoveryError>()
    };

    public async Task<ProcessDiscovery> DiscoverAsync()
    {
        try { return await Enqueue(_registry.Discover); }
        finally { Publish(); }
    }

    public async Task<ConnectionView> ConnectAsync(int processId)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var view = await Enqueue(() => _registry.Connect(processId), processId);
            Publish();
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = "connect", ProcessId = processId, ConnectionId = view.ConnectionId,
                ProjectPath = view.ApprovedProjectPath, DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "success"
            });
            return view;
        }
        catch (Exception ex)
        {
            Publish();
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = "connect", ProcessId = processId,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "error", Error = ex.Message
            });
            throw;
        }
    }

    public async Task<ConnectionView?> DisconnectAsync(int processId)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var view = await Enqueue(() => _registry.Disconnect(processId), processId);
            Publish();
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = "disconnect", ProcessId = processId,
                ConnectionId = view?.ConnectionId, ProjectPath = view?.ApprovedProjectPath,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "success"
            });
            return view;
        }
        catch (Exception ex)
        {
            Publish();
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = "disconnect", ProcessId = processId,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "error", Error = ex.Message
            });
            throw;
        }
    }

    public async Task<bool> SetMonitoringPausedAsync(bool paused)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var value = await Enqueue(() => _monitorPaused = paused);
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = paused ? "pauseMonitoring" : "resumeMonitoring",
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "success"
            });
            return value;
        }
        catch (Exception ex)
        {
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = paused ? "pauseMonitoring" : "resumeMonitoring",
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "error", Error = ex.Message
            });
            throw;
        }
    }

    public async Task<GuardedRead> ReadAsync(int processId)
    {
        var ticket = _registry.Capture(processId);
        Note(ticket);
        try { return await Enqueue(() => _registry.Read(ticket), processId); }
        finally { Publish(); }
    }

    public async Task<ProcessStatus> ReadStatusAsync(int processId)
    {
        var ticket = _registry.Capture(processId, allowDisconnected: true);
        Note(ticket);
        try { return await Enqueue(() => _registry.ReadStatus(ticket), processId); }
        finally { Publish(); }
    }

    public async Task<DeviceInventory> ListDevicesAsync(int processId)
    {
        var ticket = _registry.Capture(processId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ListDevices(ticket), processId); }
        finally { Publish(); }
    }

    public async Task<DeviceRead> ReadDeviceAsync(int processId, string objectId, bool includePath)
    {
        var ticket = _registry.Capture(processId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ReadDevice(ticket, objectId, includePath), processId); }
        finally { Publish(); }
    }

    public async Task<BlockInventory> ListBlocksAsync(int processId, string plcObjectId)
    {
        var ticket = _registry.Capture(processId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ListBlocks(ticket, plcObjectId), processId); }
        finally { Publish(); }
    }

    public async Task<BlockRead> ReadBlockAsync(BlockReadRequest request)
    {
        var ticket = _registry.Capture(request.ProcessId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ReadBlock(ticket, request), request.ProcessId); }
        finally { Publish(); }
    }

    public async Task<BlockInventory> ListUdtsAsync(int processId, string plcObjectId)
    {
        var ticket = _registry.Capture(processId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ListUdts(ticket, plcObjectId), processId); }
        finally { Publish(); }
    }

    public async Task<BlockRead> ReadUdtAsync(BlockReadRequest request)
    {
        var ticket = _registry.Capture(request.ProcessId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ReadUdt(ticket, request), request.ProcessId); }
        finally { Publish(); }
    }

    public async Task<BlockInventory> ListTagTablesAsync(int processId, string plcObjectId)
    {
        var ticket = _registry.Capture(processId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ListTagTables(ticket, plcObjectId), processId); }
        finally { Publish(); }
    }

    public async Task<TagTableRead> ReadTagTableAsync(TagTableReadRequest request)
    {
        var ticket = _registry.Capture(request.ProcessId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ReadTagTable(ticket, request), request.ProcessId); }
        finally { Publish(); }
    }

    public async Task<CrossReferenceRead> ReadCrossReferencesAsync(CrossReferenceRequest request)
    {
        var ticket = _registry.Capture(request.ProcessId);
        Note(ticket);
        try { return await Enqueue(() => _registry.ReadCrossReferences(ticket, request), request.ProcessId); }
        finally { Publish(); }
    }

    private void Note(RequestTicket ticket)
    {
        DashboardCallContext.ConnectionId.Value = ticket.ConnectionId == Guid.Empty ? null : ticket.ConnectionId;
        DashboardCallContext.ProjectPath.Value = _registry.ApprovedPath(ticket);
    }

    private void OnDiagnostic(ConnectionEvent ev)
    {
        if (ev.Action is not ("sourceExport" or "invalidated" or "cleanupFailed")) return;
        lock (_diagnosticGate) _diagnostics.Enqueue(ev);
    }

    private void Publish()
    {
        _history.Apply(_registry.Observations(), _registry.Views());
        ConnectionEvent[] pending;
        lock (_diagnosticGate)
        {
            pending = _diagnostics.ToArray();
            _diagnostics.Clear();
        }
        foreach (var ev in pending) _history.ImportDiagnostic(ev);
    }

    private async Task<T> Enqueue<T>(Func<T> operation, int processId = 0)
    {
        if (_stopping) throw new ConnectionFault("stopping", processId, "The prototype is shutting down.");
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            throw new ConnectionFault("busy", processId, "The prototype request queue is full. Try again after pending work finishes.");
        }
        try
        {
            return await _sta.RunAsync(() =>
            {
                if (_stopping) throw new ConnectionFault("stopping", processId, "The prototype is shutting down.");
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
                var observed = await Enqueue(() =>
                {
                    if (_monitorPaused) return (string?)null;
                    var found = _registry.Discover();
                    _registry.Monitor();
                    return found.Errors.Count == 0 ? "" :
                        string.Join(" | ", found.Errors.Select(error => error.Origin + ": " + error.Message));
                }).ConfigureAwait(false);
                Publish();
                if (observed == "") _lastObserveError = null;
                else if (observed != null && observed != _lastObserveError)
                {
                    _lastObserveError = observed;
                    _history.Record(new DashboardLogDraft
                    {
                        Origin = "server", Operation = "observeProcesses", Outcome = "partial", Error = observed
                    });
                }
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
