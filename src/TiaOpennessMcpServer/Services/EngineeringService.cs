using System.Diagnostics;
using TiaOpennessMcpServer.Utilities;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Operations;

namespace TiaOpennessMcpServer.Services;

internal sealed class EngineeringService : IDisposable, IEngineeringOperations
{
    private readonly StaTaskScheduler _sta;
    private readonly ConnectionRegistry _registry;
    public bool WriteToolsAvailable { get; }
    public string AccessProfile => WriteToolsAvailable ? "full" : "read-only";
    public int PendingOperations => Volatile.Read(ref _pending);
    public string? MonitorError => _monitorError;
    public int MonitoringSubscribers => Volatile.Read(ref _monitoringSubscribers);
    public bool BackgroundMonitoringActive => !_stopping && MonitoringSubscribers > 0;
    public long MonitoringObservationVersion => Interlocked.Read(ref _monitoringObservationVersion);
    public event Action<ConnectionSnapshot>? SnapshotPublished;
    public event Action<ConnectionEvent>? DiagnosticPublished;
    public event Action<string>? ObservationError;
    public event Action<int>? PendingOperationsChanged;
    private readonly Queue<ConnectionEvent> _diagnostics = new();
    private readonly object _diagnosticGate = new();
    private readonly object _monitoringGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;
    private volatile bool _stopping;
    private string? _monitorError;
    private string? _lastObserveError;
    private int _pending;
    private int _monitoringSubscribers;
    private long _monitoringObservationVersion;
    private const int MaxPending = 32;

    public EngineeringService(StaTaskScheduler sta, IConnectionBackend backend, bool writesEnabled = true)
    {
        _sta = sta;
        WriteToolsAvailable = writesEnabled;
        _registry = new ConnectionRegistry(backend, sta.VerifyAccess);
        _registry.Listen(OnDiagnostic);
        _monitor = MonitorAsync();
    }

    public ConnectionSnapshot CurrentSnapshot() => new(_registry.Observations(), _registry.Views());
    public IReadOnlyList<ConnectionEvent> ConnectionEvents() => _registry.Events();

    public Task<ConnectionView> OpenProjectAsync(string storedProjectPath) =>
        EnqueueAndPublish(() => _registry.OpenProject(storedProjectPath));

    public object BridgeStatus() => new
    {
        readAtUtc = DateTimeOffset.UtcNow, accessProfile = AccessProfile, writeToolsAvailable = WriteToolsAvailable,
        errors = Array.Empty<DiscoveryError>()
    };

    public Task<ProcessDiscovery> DiscoverAsync() => EnqueueAndPublish(_registry.Discover);

    public Task<ConnectionView> ConnectAsync(int processId, long expectedRuntimeStartUtcTicks,
        string? expectedProjectPath) =>
        EnqueueAndPublish(() => _registry.Connect(processId, expectedRuntimeStartUtcTicks, expectedProjectPath), processId);

    public Task<ConnectionView?> DisconnectAsync(int processId, long expectedRuntimeStartUtcTicks,
        string? expectedProjectPath, Guid expectedConnectionId) =>
        EnqueueAndPublish(() => _registry.Disconnect(processId, expectedRuntimeStartUtcTicks,
            expectedProjectPath, expectedConnectionId), processId);

    // Subscriptions belong to the service; consumers do not own the native monitor or its timer.
    public IDisposable AcquireMonitoringSubscription()
    {
        lock (_monitoringGate)
        {
            if (_stopping) throw new ObjectDisposedException(nameof(EngineeringService));
            _monitoringSubscribers++;
        }
        Publish();
        return new MonitoringSubscription(this);
    }

    private void ReleaseMonitoringSubscription()
    {
        lock (_monitoringGate)
        {
            // Disposal invalidates all outstanding subscriptions.
            if (_stopping) return;
            _monitoringSubscribers--;
        }
        Publish();
    }

    private sealed class MonitoringSubscription(EngineeringService owner) : IDisposable
    {
        private EngineeringService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseMonitoringSubscription();
    }

    public Task<ProcessStatus> ReadStatusAsync(int processId) =>
        RunAsync(processId, ticket =>
        {
            var status = _registry.ReadStatus(ticket);
            status.WriteToolsAvailable = WriteToolsAvailable;
            return status;
        }, allowDisconnected: true);

    public Task<DeviceInventory> ListDevicesAsync(int processId) =>
        RunAsync(processId, ticket => _registry.ListDevices(ticket));

    public Task<HardwareCatalogRead> SearchHardwareCatalogAsync(HardwareCatalogRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.SearchHardwareCatalog(ticket, request));

    public Task<DeviceRead> ReadDeviceAsync(int processId, string objectId, bool includePath) =>
        RunAsync(processId, ticket => _registry.ReadDevice(ticket, objectId, includePath));

    public Task<BlockInventory> ListBlocksAsync(int processId, string plcObjectId) =>
        RunAsync(processId, ticket => _registry.ListBlocks(ticket, plcObjectId));

    public Task<BlockRead> ReadBlockAsync(BlockReadRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.ReadBlock(ticket, request));

    public Task<BlockInventory> ListUdtsAsync(int processId, string plcObjectId) =>
        RunAsync(processId, ticket => _registry.ListUdts(ticket, plcObjectId));

    public Task<BlockRead> ReadUdtAsync(BlockReadRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.ReadUdt(ticket, request));

    public Task<BlockInventory> ListTagTablesAsync(int processId, string plcObjectId) =>
        RunAsync(processId, ticket => _registry.ListTagTables(ticket, plcObjectId));

    public Task<TagTableRead> ReadTagTableAsync(TagTableReadRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.ReadTagTable(ticket, request));

    public Task<BlockInventory> ListTechnologyObjectsAsync(int processId, string plcObjectId) =>
        RunAsync(processId, ticket => _registry.ListTechnologyObjects(ticket, plcObjectId));

    public Task<AvailableTechnologyObjects> ListAvailableTechnologyObjectsAsync(int processId, string plcObjectId) =>
        RunAsync(processId, ticket => _registry.ListAvailableTechnologyObjects(ticket, plcObjectId));

    public Task<TechnologyObjectRead> ReadTechnologyObjectAsync(TechnologyObjectReadRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.ReadTechnologyObject(ticket, request));

    public Task<CrossReferenceRead> ReadCrossReferencesAsync(CrossReferenceRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.ReadCrossReferences(ticket, request));

    public Task<WriteResult> WriteAsync(WriteRequest request)
    {
        if (!WriteToolsAvailable) throw new ConnectionFault("readOnly", request.ProcessId, "This server was started with the read-only access profile.");
        return RunAsync(request.ProcessId, ticket => _registry.Write(ticket, request));
    }

    public Task<TagTableExportResult> ExportTagTableAsync(ExportTagTableRequest request) =>
        RunAsync(request.ProcessId, ticket => _registry.ExportTagTable(ticket, request));

    public Task<CompileResult> CompileAsync(CompileRequest request)
    {
        if (!WriteToolsAvailable) throw new ConnectionFault("readOnly", request.ProcessId, "This server was started with the read-only access profile.");
        return RunAsync(request.ProcessId, ticket => _registry.Compile(ticket, request));
    }

    private async Task<T> RunAsync<T>(int processId, Func<RequestTicket, T> work, bool allowDisconnected = false)
    {
        var ticket = _registry.Capture(processId, allowDisconnected,
            OperationCallContext.Current?.ExpectedConnectionId);
        Note(ticket);
        try { return await Enqueue(() => work(ticket), processId); }
        finally { Publish(); }
    }

    private void Note(RequestTicket ticket)
    {
        var context = OperationCallContext.Current;
        if (context == null) return;
        context.ConnectionId = ticket.ConnectionId == Guid.Empty ? null : ticket.ConnectionId;
        context.ProjectPath = _registry.ApprovedPath(ticket);
    }

    private void OnDiagnostic(ConnectionEvent ev)
    {
        if (ev.Action is not ("sourceExport" or "invalidated" or "cleanupFailed")) return;
        lock (_diagnosticGate) _diagnostics.Enqueue(ev);
    }

    private void Publish()
    {
        Notify(SnapshotPublished, CurrentSnapshot());
        ConnectionEvent[] pending;
        lock (_diagnosticGate)
        {
            pending = _diagnostics.ToArray();
            _diagnostics.Clear();
        }
        foreach (var ev in pending) Notify(DiagnosticPublished, ev);
    }

    private static void Notify<T>(Action<T>? observers, T value)
    {
        if (observers == null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            // Presentation observers must never change the result of an engineering operation.
            catch (Exception ex) { Trace.TraceError("Connection observer failed: " + ex.Message); }
        }
    }

    private async Task<T> EnqueueAndPublish<T>(Func<T> operation, int processId = 0)
    {
        try { return await Enqueue(operation, processId); }
        finally { Publish(); }
    }

    private async Task<T> Enqueue<T>(Func<T> operation, int processId = 0, bool notifyPending = true)
    {
        if (_stopping) throw new ConnectionFault("stopping", processId, "The server is shutting down.");
        var pending = Interlocked.Increment(ref _pending);
        if (pending > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            throw new ConnectionFault("busy", processId, "The server request queue is full. Try again after pending work finishes.");
        }
        if (notifyPending) Notify(PendingOperationsChanged, pending);
        try
        {
            return await _sta.RunAsync(() =>
            {
                if (_stopping) throw new ConnectionFault("stopping", processId, "The server is shutting down.");
                return operation();
            }).ConfigureAwait(false);
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref _pending);
            if (notifyPending) Notify(PendingOperationsChanged, remaining);
        }
    }

    private async Task MonitorOnceAsync()
    {
        // Avoid queueing any native work when the last consumer has gone away.
        if (!BackgroundMonitoringActive) return;
        var observed = await Enqueue<ProcessDiscovery?>(() =>
        {
            // A subscription can end while this task waits for the STA worker.
            if (!BackgroundMonitoringActive) return null;
            var found = _registry.Discover();
            _registry.Monitor();
            return found;
        }, notifyPending: false).ConfigureAwait(false);
        if (observed == null) return;
        _monitorError = null;
        Interlocked.Increment(ref _monitoringObservationVersion);
        Publish();
        var error = observed.Errors.Count == 0 ? null : DiscoveryError.Summary(observed.Errors);
        if (error != null && error != _lastObserveError) Notify(ObservationError, error);
        _lastObserveError = error;
    }

    private async Task MonitorAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
                await MonitorOnceAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _monitorError = ex.Message;
                Interlocked.Increment(ref _monitoringObservationVersion);
                Publish();
            }
        }
    }

    public void Dispose()
    {
        lock (_monitoringGate)
        {
            if (_stopping) return;
            _stopping = true;
            _monitoringSubscribers = 0;
        }
        _stop.Cancel();
        _monitor.GetAwaiter().GetResult();
        // Native calls cannot be force-cancelled. Cleanup waits on the same worker.
        _sta.RunAsync(_registry.DisconnectAll).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
