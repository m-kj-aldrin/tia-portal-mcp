using System.Diagnostics;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

namespace TiaOpennessMcpServer.Dashboard;

/// <summary>Dashboard history and workflows layered over the shared engineering service.</summary>
internal sealed class DashboardService : IDisposable
{
    private readonly EngineeringService _engineering;
    private readonly DashboardHistory _history = new();
    public event Action? Changed;

    public DashboardService(EngineeringService engineering)
    {
        _engineering = engineering;
        _engineering.SnapshotPublished += Apply;
        _engineering.DiagnosticPublished += ImportDiagnostic;
        _engineering.ObservationError += RecordObservationError;
        Apply(_engineering.CurrentSnapshot());
    }

    public object Status() => new
    {
        writeToolsAvailable = _engineering.WriteToolsAvailable,
        pendingOperations = _engineering.PendingOperations, monitorError = _engineering.MonitorError,
        backgroundMonitoringActive = _engineering.BackgroundMonitoringActive,
        monitoringSubscribers = _engineering.MonitoringSubscribers,
        connections = _engineering.CurrentSnapshot().Connections, events = _engineering.ConnectionEvents()
    };

    public object Dashboard() => new
    {
        pendingOperations = _engineering.PendingOperations, monitorError = _engineering.MonitorError,
        backgroundMonitoringActive = _engineering.BackgroundMonitoringActive,
        monitoringSubscribers = _engineering.MonitoringSubscribers, history = _history.Snapshot()
    };

    public DashboardLogPage Logs(long after, int generation) => _history.ReadLogs(after, generation);

    public Task<ConnectionView> OpenProjectAsync(string? tabId)
    {
        var path = _history.ClosedProjectPath(tabId);
        if (path == null)
            throw new ConnectionFault("invalidRequest", 0, "Open project is available only on a closed tab that has a project path.");
        return ObserveAsync("openProject", null, path, () => _engineering.OpenProjectAsync(path));
    }

    public Task<ConnectionView> ConnectAsync(int processId) =>
        ObserveAsync("connect", processId, null, () => _engineering.ConnectAsync(processId));

    public Task<ConnectionView?> DisconnectAsync(int processId) =>
        ObserveAsync("disconnect", processId, null, () => _engineering.DisconnectAsync(processId));

    public object Dismiss(string? tabId)
    {
        if (!_history.Dismiss(tabId))
            throw new ConnectionFault("invalidRequest", 0, "Only a historical TIA tab can be dismissed.");
        NotifyChanged();
        return new { dismissed = true, tabId };
    }

    public void RecordCall(OperationCallNote note)
    {
        _history.Record(new DashboardLogDraft
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
        NotifyChanged();
    }

    private void Apply(ConnectionSnapshot snapshot)
    {
        _history.Apply(snapshot.Observations, snapshot.Connections);
        NotifyChanged();
    }

    private void ImportDiagnostic(ConnectionEvent ev)
    {
        _history.ImportDiagnostic(ev);
        NotifyChanged();
    }

    private void RecordObservationError(string message)
    {
        _history.Record(new DashboardLogDraft
        {
            Origin = "server", Operation = "observeProcesses", Outcome = "partial", Error = message
        });
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var observers = Changed;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception ex) { Trace.TraceError("Dashboard observer failed: " + ex.Message); }
        }
    }

    private async Task<T> ObserveAsync<T>(string operation, int? processId, string? path, Func<Task<T>> action)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var result = await action();
            var view = result as ConnectionView;
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = operation, ProcessId = view?.ProcessId ?? processId,
                ConnectionId = view?.ConnectionId, ProjectPath = view?.ApprovedProjectPath ?? path,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "success"
            });
            NotifyChanged();
            return result;
        }
        catch (Exception ex)
        {
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = operation, ProcessId = processId, ProjectPath = path,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "error", Error = ex.Message
            });
            NotifyChanged();
            throw;
        }
    }

    public void Dispose()
    {
        _engineering.SnapshotPublished -= Apply;
        _engineering.DiagnosticPublished -= ImportDiagnostic;
        _engineering.ObservationError -= RecordObservationError;
    }
}
