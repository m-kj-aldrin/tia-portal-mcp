using System.Text;
using System.Diagnostics;

namespace TiaOpennessMcpServer.Dashboard;

// Full dashboard captures are deliberately separate from the metadata journal.
// Only managed strings cross this boundary; no native object is retained here.
internal sealed class DashboardRunStore
{
    internal const int MaxActive = 4;
    internal const int MaxCompleted = 40;
    internal const long MaxRetainedBytes = 64L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly List<DashboardRunCapture> _runs = new();
    private readonly HashSet<string> _seenRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _seenOrder = new();
    private readonly int _maxCompleted;
    private readonly long _maxRetainedBytes;
    private long _retainedBytes;
    public event Action? Changed;
    public event Action<DashboardRunTransition>? Transition;

    internal DashboardRunStore(int maxCompleted = MaxCompleted, long maxRetainedBytes = MaxRetainedBytes)
    {
        if (maxCompleted < 1 || maxCompleted > MaxCompleted || maxRetainedBytes < 1 || maxRetainedBytes > MaxRetainedBytes)
            throw new ArgumentOutOfRangeException(nameof(maxCompleted));
        _maxCompleted = maxCompleted;
        _maxRetainedBytes = maxRetainedBytes;
    }

    internal DashboardRunCapture? TryStart(string requestId, string tabId, string operation,
        int? processId, string requestJson, out string? rejection)
    {
        DashboardRunCapture? capture;
        lock (_gate)
        {
            if (_seenRequests.Contains(requestId))
            { rejection = "This requestId was already used; inspect history before starting another run."; return null; }
            if (_runs.Count(run => !run.Completed) >= MaxActive)
            { rejection = "Four dashboard runs are already active."; return null; }
            RememberRequest(requestId);
            capture = new DashboardRunCapture
            {
                Id = requestId, TabId = tabId, Operation = operation, ProcessId = processId,
                StartedAtUtc = DateTimeOffset.UtcNow, RequestJson = requestJson,
                Outcome = "running", PayloadRetained = true
            };
            _runs.Add(capture);
        }
        rejection = null;
        NotifyTransition(new DashboardRunTransition(capture.Clone(), true));
        NotifyChanged();
        return capture.Clone();
    }

    internal DashboardRunCapture Finish(string requestId, string outcome, string responseJson, string? error)
    {
        DashboardRunCapture complete;
        lock (_gate)
        {
            var capture = _runs.FirstOrDefault(run => run.Id == requestId && !run.Completed)
                ?? throw new InvalidOperationException("Dashboard run was not active.");
            capture.CompletedAtUtc = DateTimeOffset.UtcNow;
            capture.Outcome = outcome;
            capture.Error = error;
            var bytes = (long)Encoding.UTF8.GetByteCount(capture.RequestJson ?? "") +
                Encoding.UTF8.GetByteCount(responseJson);
            if (bytes > _maxRetainedBytes)
            {
                capture.RequestJson = null;
                capture.ResponseJson = null;
                capture.PayloadRetained = false;
                capture.PayloadBytes = bytes;
            }
            else
            {
                capture.ResponseJson = responseJson;
                capture.PayloadBytes = bytes;
                _retainedBytes += bytes;
            }
            TrimCompleted();
            complete = capture.Clone();
        }
        NotifyTransition(new DashboardRunTransition(complete.Clone(), false));
        NotifyChanged();
        return complete;
    }

    internal IReadOnlyList<DashboardRunCapture> Snapshot()
    {
        lock (_gate) return _runs.AsEnumerable().Reverse().Select(run => run.Clone()).ToArray();
    }

    internal DashboardRunCapture? Get(string runId, string tabId)
    {
        lock (_gate) return _runs.FirstOrDefault(run => run.Id == runId && run.TabId == tabId)?.Clone();
    }

    internal DashboardRunCapture? LatestForTab(string tabId)
    {
        lock (_gate) return _runs.LastOrDefault(run => run.TabId == tabId)?.Clone();
    }

    internal void DismissTab(string tabId)
    {
        var changed = false;
        lock (_gate)
        {
            for (var i = _runs.Count - 1; i >= 0; i--)
            {
                if (_runs[i].TabId != tabId || !_runs[i].Completed) continue;
                _retainedBytes -= _runs[i].PayloadRetained ? _runs[i].PayloadBytes : 0;
                _runs.RemoveAt(i);
                changed = true;
            }
        }
        if (changed) NotifyChanged();
    }

    internal void PruneTabs(IReadOnlyCollection<string> retainedTabIds)
    {
        var kept = new HashSet<string>(retainedTabIds, StringComparer.Ordinal);
        var changed = false;
        lock (_gate)
        {
            for (var i = _runs.Count - 1; i >= 0; i--)
            {
                if (!_runs[i].Completed || kept.Contains(_runs[i].TabId)) continue;
                _retainedBytes -= _runs[i].PayloadRetained ? _runs[i].PayloadBytes : 0;
                _runs.RemoveAt(i);
                changed = true;
            }
        }
        if (changed) NotifyChanged();
    }

    private void RememberRequest(string requestId)
    {
        _seenRequests.Add(requestId);
        _seenOrder.Enqueue(requestId);
        // Tombstones outlive the displayed captures, preventing accidental
        // immediate replays while keeping this memory bounded.
        while (_seenOrder.Count > 1024) _seenRequests.Remove(_seenOrder.Dequeue());
    }

    private void TrimCompleted()
    {
        while (_runs.Count(run => run.Completed) > _maxCompleted || _retainedBytes > _maxRetainedBytes)
        {
            var oldest = _runs.FindIndex(run => run.Completed);
            if (oldest < 0) break;
            var capture = _runs[oldest];
            _retainedBytes -= capture.PayloadRetained ? capture.PayloadBytes : 0;
            _runs.RemoveAt(oldest);
        }
    }

    private void NotifyChanged()
    {
        var observers = Changed;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); }
            catch (Exception ex) { Trace.TraceError("Dashboard run observer failed: " + ex.Message); }
    }

    private void NotifyTransition(DashboardRunTransition transition)
    {
        var observers = Transition;
        if (observers == null) return;
        foreach (Action<DashboardRunTransition> observer in observers.GetInvocationList())
            try { observer(transition); }
            catch (Exception ex) { Trace.TraceError("Dashboard run transition observer failed: " + ex.Message); }
    }
}

internal sealed class DashboardRunTransition
{
    public DashboardRunCapture Capture { get; }
    public bool Started { get; }
    public DashboardRunTransition(DashboardRunCapture capture, bool started)
    { Capture = capture; Started = started; }
}

internal sealed class DashboardRunCapture
{
    public string Id { get; set; } = "";
    public string TabId { get; set; } = "";
    public string Operation { get; set; } = "";
    public int? ProcessId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public bool Completed => CompletedAtUtc != null;
    public string Outcome { get; set; } = "running";
    public string? Error { get; set; }
    public bool PayloadRetained { get; set; }
    public long PayloadBytes { get; set; }
    public string? RequestJson { get; set; }
    public string? ResponseJson { get; set; }

    public DashboardRunCapture Clone() => (DashboardRunCapture)MemberwiseClone();
}
