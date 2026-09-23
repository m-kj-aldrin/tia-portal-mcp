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

    public DashboardService(EngineeringService engineering)
    {
        _engineering = engineering;
        _engineering.SnapshotPublished += Apply;
        _engineering.DiagnosticPublished += _history.ImportDiagnostic;
        _engineering.ObservationError += RecordObservationError;
        Apply(_engineering.CurrentSnapshot());
    }

    public object Status() => new
    {
        mode = "connection-prototype", writeToolsAvailable = _engineering.WriteToolsAvailable,
        implementationPhase = "native-compile-delete-export",
        mcpPublication = _engineering.WriteToolsAvailable ? "twenty-four-read-write-tools" : "twelve-read-only-tools",
        pendingOperations = _engineering.PendingOperations, monitorError = _engineering.MonitorError,
        backgroundMonitoringPaused = _engineering.BackgroundMonitoringPaused,
        connections = _engineering.CurrentSnapshot().Connections, events = _engineering.ConnectionEvents(),
        samePathReopenEvidence = ConnectionEvidence.SamePathReopen
    };

    public object Dashboard() => new
    {
        pendingOperations = _engineering.PendingOperations, monitorError = _engineering.MonitorError,
        backgroundMonitoringPaused = _engineering.BackgroundMonitoringPaused, history = _history.Snapshot()
    };

    public DashboardLogPage Logs(long after, int generation) => _history.ReadLogs(after, generation);

    public Task<ConnectionView> OpenProjectAsync(string? tabId)
    {
        var path = _history.ClosedProjectPath(tabId);
        if (path == null)
            throw new ConnectionFault("invalidRequest", 0, "Open project is available only on a closed tab that has a project path.");
        return ObserveAsync("openProject", null, path, () => _engineering.OpenProjectAsync(path), view => view);
    }

    public Task<ConnectionView> ConnectAsync(int processId) =>
        ObserveAsync("connect", processId, null, () => _engineering.ConnectAsync(processId), view => view);

    public Task<ConnectionView?> DisconnectAsync(int processId) =>
        ObserveAsync("disconnect", processId, null, () => _engineering.DisconnectAsync(processId), view => view);

    public Task<bool> SetMonitoringPausedAsync(bool paused) =>
        ObserveAsync(paused ? "pauseMonitoring" : "resumeMonitoring", null, null,
            () => _engineering.SetMonitoringPausedAsync(paused));

    public object Dismiss(string? tabId)
    {
        if (!_history.Dismiss(tabId))
            throw new ConnectionFault("invalidRequest", 0, "Only a historical TIA tab can be dismissed.");
        return new { dismissed = true, tabId };
    }

    public void RecordCall(OperationCallNote note) => _history.Record(new DashboardLogDraft
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
            ConnectionId = OperationCallContext.Current?.ConnectionId, ProjectPath = OperationCallContext.Current?.ProjectPath,
            DurationMs = durationMs, Outcome = outcome, Error = error
        });

    private void Apply(ConnectionSnapshot snapshot) => _history.Apply(snapshot.Observations, snapshot.Connections);

    private void RecordObservationError(string message) => _history.Record(new DashboardLogDraft
    {
        Origin = "server", Operation = "observeProcesses", Outcome = "partial", Error = message
    });

    private async Task<T> ObserveAsync<T>(string operation, int? processId, string? path, Func<Task<T>> action,
        Func<T, ConnectionView?>? connection = null)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var result = await action();
            var view = connection?.Invoke(result);
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = operation, ProcessId = view?.ProcessId ?? processId,
                ConnectionId = view?.ConnectionId, ProjectPath = view?.ApprovedProjectPath ?? path,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "success"
            });
            return result;
        }
        catch (Exception ex)
        {
            _history.Record(new DashboardLogDraft
            {
                Origin = "dashboard", Operation = operation, ProcessId = processId, ProjectPath = path,
                DurationMs = started.Elapsed.TotalMilliseconds, Outcome = "error", Error = ex.Message
            });
            throw;
        }
    }

    public void Dispose()
    {
        _engineering.SnapshotPublished -= Apply;
        _engineering.DiagnosticPublished -= _history.ImportDiagnostic;
        _engineering.ObservationError -= RecordObservationError;
    }
}
