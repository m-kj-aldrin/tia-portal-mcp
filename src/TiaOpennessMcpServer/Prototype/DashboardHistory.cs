namespace TiaOpennessMcpServer.Prototype;

/// <summary>
/// In-memory dashboard tabs and call logs. Project history matches the canonical path only.
/// A path match never marks a runtime connected; that requires the registry view for the same runtime.
/// </summary>
internal sealed class DashboardHistory
{
    internal const int MaxLogEntries = 400;
    internal const int MaxHistoricalTabs = 24;
    internal const string ServerId = "server";

    private readonly object _gate = new();
    private readonly List<Tab> _tabs = new();
    private readonly List<DashboardLogEntry> _logs = new();
    private readonly string _epoch = Guid.NewGuid().ToString("n");
    private long _next = 1;
    private int _generation = 1;
    private long _change;
    private bool _logsTruncated;
    private bool _tabsTruncated;

    public DashboardHistory()
    {
        _tabs.Add(new Tab
        {
            Id = ServerId, Kind = "server", RuntimeState = "none",
            ConnectionState = "none", ProjectState = "none"
        });
        Record(new DashboardLogDraft
        {
            Origin = "server", Operation = "startup", Outcome = "success",
            Detail = "Dashboard history stays in memory until this server stops."
        });
    }

    public void Apply(IReadOnlyList<ProcessObservation> observed, IReadOnlyList<ConnectionView> views)
    {
        var live = new List<ProcessObservation>();
        var seen = new HashSet<int>();
        foreach (var process in observed)
        {
            if (process.ProcessId <= 0 || process.RuntimeStartUtcTicks <= 0 || !seen.Add(process.ProcessId)) continue;
            live.Add(process);
        }
        lock (_gate)
        {
            var bound = new HashSet<int>();
            var projectGap = new Dictionary<int, string>();
            foreach (var tab in _tabs.Where(item => item.Kind != "server" && item.ProcessId != null).ToList())
            {
                var match = live.FirstOrDefault(process => process.ProcessId == tab.ProcessId &&
                    process.RuntimeStartUtcTicks == tab.RuntimeStartUtcTicks);
                if (match == null)
                {
                    if (tab.CanonicalPath == null && tab.GapForTabId != null)
                        AbsorbGap(tab);
                    else
                        Retire(tab);
                    continue;
                }
                var path = Canonical(match.ProjectPath);
                if (!Same(tab.CanonicalPath, path))
                {
                    if (tab.CanonicalPath == null && path != null)
                    {
                        var historical = HistoricalTab(path);
                        if (historical != null)
                        {
                            AbsorbGap(tab, historical);
                            BindRuntime(historical, match, views);
                            bound.Add(match.ProcessId);
                            continue;
                        }
                        // A projectless runtime with no archived project keeps this tab and its logs.
                        Archive(tab);
                        var hadConnection = tab.Previous.Count > 0 && tab.Previous[tab.Previous.Count - 1].ConnectionId != null;
                        tab.ConnectionId = null;
                        tab.CanonicalPath = path;
                        BindRuntime(tab, match, views);
                        if (hadConnection && tab.ConnectionId == null) tab.ConnectionState = "invalidated";
                        bound.Add(match.ProcessId);
                        continue;
                    }
                    var archivedId = tab.Id;
                    Retire(tab);
                    if (path == null) projectGap[match.ProcessId] = archivedId;
                    continue;
                }
                BindRuntime(tab, match, views);
                bound.Add(match.ProcessId);
            }
            foreach (var process in live)
            {
                if (bound.Contains(process.ProcessId)) continue;
                var path = Canonical(process.ProjectPath);
                // A live tab already owns this path for a different runtime; do not merge those connections.
                var tab = path == null ? null : HistoricalTab(path);
                if (tab == null) tab = NewTab(path);
                else tab.CanonicalPath = path;
                if (path == null && projectGap.TryGetValue(process.ProcessId, out var origin))
                    tab.GapForTabId = origin;
                BindRuntime(tab, process, views);
            }
            EvictHistorical();
        }
    }

    public void Record(DashboardLogDraft draft)
    {
        lock (_gate)
        {
            var tab = Resolve(draft.ProcessId, draft.ConnectionId, draft.ProjectPath);
            var entry = new DashboardLogEntry
            {
                Sequence = _next++,
                AtUtc = DateTimeOffset.UtcNow,
                DurationMs = draft.DurationMs,
                Origin = string.IsNullOrWhiteSpace(draft.Origin) ? "server" : draft.Origin,
                Operation = string.IsNullOrWhiteSpace(draft.Operation) ? "unknown" : draft.Operation,
                Outcome = draft.Outcome is "success" or "partial" or "error" ? draft.Outcome : "error",
                Error = string.IsNullOrWhiteSpace(draft.Error) ? null : draft.Error,
                Detail = string.IsNullOrWhiteSpace(draft.Detail) ? null : draft.Detail,
                ProcessId = draft.ProcessId is > 0 ? draft.ProcessId : null,
                ConnectionId = draft.ConnectionId is Guid id && id != Guid.Empty ? id : null,
                ProjectPath = draft.ProjectPath ?? (tab.Kind == "server" ? null : tab.CanonicalPath),
                TabId = tab.Id
            };
            _logs.Add(entry);
            while (_logs.Count > MaxLogEntries)
            {
                _logs.RemoveAt(0);
                _logsTruncated = true;
            }
        }
    }

    public void ImportDiagnostic(ConnectionEvent ev)
    {
        if (ev.Action is not ("sourceExport" or "invalidated" or "cleanupFailed")) return;
        var succeeded = ev.Action == "sourceExport" && ev.Message.IndexOf(": success", StringComparison.Ordinal) >= 0 &&
            ev.Message.IndexOf("—", StringComparison.Ordinal) < 0;
        Record(new DashboardLogDraft
        {
            Origin = "server",
            Operation = ev.Action,
            ProcessId = ev.ProcessId > 0 ? ev.ProcessId : null,
            ConnectionId = ev.ConnectionId == Guid.Empty ? null : ev.ConnectionId,
            Outcome = succeeded ? "success" : "error",
            Error = succeeded ? null : ev.Message,
            Detail = ev.Message
        });
    }

    public string? ClosedProjectPath(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId)) return null;
        lock (_gate)
        {
            var tab = _tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab == null || tab.Kind == "server" || tab.RuntimeState == "running") return null;
            return tab.CanonicalPath;
        }
    }

    public bool Dismiss(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId)) return false;
        lock (_gate)
        {
            var tab = _tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab == null || tab.Kind == "server" || tab.RuntimeState == "running") return false;
            _tabs.Remove(tab);
            _logs.RemoveAll(entry => entry.TabId == tab.Id);
            _generation++;
        }
        Record(new DashboardLogDraft
        {
            Origin = "dashboard", Operation = "dismiss", Outcome = "success",
            Detail = "Removed dashboard history for tab " + tabId + "."
        });
        return true;
    }

    public DashboardSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DashboardSnapshot
            {
                Epoch = _epoch,
                Generation = _generation,
                LogsTruncated = _logsTruncated,
                TabsTruncated = _tabsTruncated,
                MaxLogEntries = MaxLogEntries,
                MaxHistoricalTabs = MaxHistoricalTabs,
                Tabs = _tabs.Select(View).ToList()
            };
        }
    }

    public DashboardLogPage ReadLogs(long after, int generation)
    {
        lock (_gate)
        {
            var oldest = _logs.Count == 0 ? 0 : _logs[0].Sequence;
            var reset = (after > 0 && oldest > after + 1) || (generation > 0 && generation != _generation);
            var entries = reset || after <= 0 ? _logs.ToList() : _logs.Where(entry => entry.Sequence > after).ToList();
            return new DashboardLogPage
            {
                Reset = reset, Generation = _generation, Oldest = oldest, Next = _next - 1,
                Truncated = _logsTruncated || _tabsTruncated, Entries = entries
            };
        }
    }

    private Tab Resolve(int? processId, Guid? connectionId, string? projectPath)
    {
        if (connectionId is Guid id && id != Guid.Empty)
        {
            var byConnection = _tabs.FirstOrDefault(tab => tab.ConnectionId == id ||
                tab.Previous.Any(item => item.ConnectionId == id));
            if (byConnection != null) return byConnection;
        }
        if (processId is int pid && pid > 0)
        {
            var live = _tabs.FirstOrDefault(tab => tab.ProcessId == pid && tab.RuntimeState == "running");
            if (live != null) return live;
        }
        var path = Canonical(projectPath);
        if (path != null)
        {
            var byPath = _tabs.FirstOrDefault(tab => tab.Kind != "server" && tab.RuntimeState == "running" && Same(tab.CanonicalPath, path))
                ?? _tabs.FirstOrDefault(tab => tab.Kind != "server" && Same(tab.CanonicalPath, path));
            if (byPath != null) return byPath;
        }
        return _tabs.First(tab => tab.Id == ServerId);
    }

    private void BindRuntime(Tab tab, ProcessObservation process, IReadOnlyList<ConnectionView> views)
    {
        tab.ProcessId = process.ProcessId;
        tab.RuntimeStartUtcTicks = process.RuntimeStartUtcTicks;
        tab.Mode = string.IsNullOrWhiteSpace(process.Mode) ? null : process.Mode;
        tab.RuntimeState = "running";
        tab.CanAttach = process.CanAttach;
        tab.UnavailableReason = process.UnavailableReason;
        tab.CanonicalPath = Canonical(process.ProjectPath);
        tab.ProjectState = tab.CanonicalPath == null ? "none" : "open";
        var view = views.FirstOrDefault(item => item.ProcessId == process.ProcessId);
        var sameRuntime = view != null && view.RuntimeStartUtcTicks == process.RuntimeStartUtcTicks;
        var samePath = sameRuntime && Same(Canonical(view!.ApprovedProjectPath), tab.CanonicalPath);
        if (view != null && samePath)
        {
            tab.ConnectionId = view.ConnectionId == Guid.Empty ? null : view.ConnectionId;
            tab.ConnectionState = string.IsNullOrWhiteSpace(view.State) ? "disconnected" : view.State;
            tab.Reason = view.Reason;
            tab.CleanupError = view.CleanupError;
        }
        else if (view != null && sameRuntime && view.State == "invalidated")
        {
            tab.ConnectionId = null;
            tab.ConnectionState = "invalidated";
            tab.Reason = view.Reason;
            tab.CleanupError = view.CleanupError;
        }
        else
        {
            tab.ConnectionId = null;
            tab.ConnectionState = "disconnected";
            tab.Reason = null;
            tab.CleanupError = null;
        }
        tab.UpdatedSequence = ++_change;
    }

    private Tab? HistoricalTab(string? path) =>
        path == null ? null : _tabs.Where(item => item.Kind != "server" && item.RuntimeState != "running" &&
            Same(item.CanonicalPath, path)).OrderByDescending(item => item.UpdatedSequence).FirstOrDefault();

    private void AbsorbGap(Tab gap, Tab? target = null)
    {
        target ??= gap.GapForTabId == null ? null : _tabs.FirstOrDefault(item => item.Id == gap.GapForTabId);
        if (target == null || target.Id == gap.Id)
        {
            Retire(gap);
            return;
        }
        foreach (var entry in _logs)
        {
            if (entry.TabId != gap.Id) continue;
            entry.TabId = target.Id;
            if (entry.ProjectPath == null) entry.ProjectPath = target.CanonicalPath;
        }
        foreach (var previous in gap.Previous)
        {
            if (target.Previous.Any(item => item.ProcessId == previous.ProcessId &&
                item.RuntimeStartUtcTicks == previous.RuntimeStartUtcTicks && item.ConnectionId == previous.ConnectionId))
                continue;
            target.Previous.Add(previous);
            if (target.Previous.Count > 50) target.Previous.RemoveAt(0);
        }
        _tabs.Remove(gap);
        _generation++;
    }

    private void Retire(Tab tab)
    {
        Archive(tab);
        var hadConnection = tab.Previous.Count > 0 && tab.Previous[tab.Previous.Count - 1].ConnectionId != null;
        tab.ProcessId = null;
        tab.RuntimeStartUtcTicks = 0;
        tab.RuntimeState = "closed";
        tab.ConnectionId = null;
        tab.ConnectionState = hadConnection ? "invalidated" : "disconnected";
        tab.CanAttach = false;
        tab.UnavailableReason = null;
        tab.CleanupError = null;
        tab.ProjectState = tab.CanonicalPath == null ? "none" : "historical";
        tab.UpdatedSequence = ++_change;
    }

    private static void Archive(Tab tab)
    {
        if (tab.ProcessId == null && tab.ConnectionId == null) return;
        tab.Previous.Add(new DashboardRuntimeRef
        {
            ProcessId = tab.ProcessId ?? 0,
            RuntimeStartUtcTicks = tab.RuntimeStartUtcTicks,
            ConnectionId = tab.ConnectionId,
            Mode = tab.Mode
        });
        if (tab.Previous.Count > 50) tab.Previous.RemoveAt(0);
    }

    private Tab NewTab(string? path) 
    {
        var tab = new Tab
        {
            Id = Guid.NewGuid().ToString("n"), Kind = "tia", CanonicalPath = path,
            ProjectState = path == null ? "none" : "open", RuntimeState = "closed", ConnectionState = "disconnected"
        };
        _tabs.Add(tab);
        return tab;
    }

    private void EvictHistorical()
    {
        var historical = _tabs.Where(tab => tab.Kind != "server" && tab.RuntimeState != "running")
            .OrderBy(tab => tab.UpdatedSequence).ToList();
        while (historical.Count > MaxHistoricalTabs)
        {
            var victim = historical[0];
            historical.RemoveAt(0);
            _tabs.Remove(victim);
            _logs.RemoveAll(entry => entry.TabId == victim.Id);
            _tabsTruncated = true;
            _generation++;
        }
    }

    private DashboardTabView View(Tab tab) => new()
    {
        Id = tab.Id,
        Kind = tab.Kind,
        Title = Title(tab),
        ProcessId = tab.RuntimeState == "running" ? tab.ProcessId : null,
        Mode = tab.Mode,
        RuntimeState = tab.RuntimeState,
        RuntimeIdentity = tab.RuntimeState == "running" ? tab.RuntimeStartUtcTicks.ToString() : null,
        ConnectionState = tab.ConnectionState,
        ConnectionId = tab.ConnectionId,
        ProjectPath = tab.CanonicalPath,
        ProjectState = tab.ProjectState,
        CanAttach = tab.CanAttach,
        UnavailableReason = tab.UnavailableReason,
        Reason = tab.Reason,
        CleanupError = tab.CleanupError,
        Live = tab.RuntimeState == "running",
        Previous = tab.Previous.Select(item => new DashboardRuntimeRef
        {
            ProcessId = item.ProcessId,
            RuntimeStartUtcTicks = item.RuntimeStartUtcTicks,
            ConnectionId = item.ConnectionId,
            Mode = item.Mode
        }).ToList()
    };

    private static string Title(Tab tab)
    {
        if (tab.Kind == "server") return "Server";
        if (!string.IsNullOrWhiteSpace(tab.CanonicalPath))
        {
            var name = Path.GetFileName(tab.CanonicalPath);
            return string.IsNullOrWhiteSpace(name) ? tab.CanonicalPath! : name;
        }
        return tab.ProcessId is int id ? "Process " + id : "Closed process";
    }

    internal static string? Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path!.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
                return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (PathTooLongException) { }
        return trimmed;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class Tab
    {
        public string Id = "";
        public string Kind = "tia";
        public int? ProcessId;
        public long RuntimeStartUtcTicks;
        public string? Mode;
        public string RuntimeState = "closed";
        public string ConnectionState = "disconnected";
        public Guid? ConnectionId;
        public string? CanonicalPath;
        public string ProjectState = "none";
        public bool CanAttach = true;
        public string? UnavailableReason;
        public string? Reason;
        public string? CleanupError;
        public long UpdatedSequence;
        public string? GapForTabId;
        public List<DashboardRuntimeRef> Previous = new();
    }
}

internal sealed class DashboardLogDraft
{
    public string Origin { get; set; } = "server";
    public string Operation { get; set; } = "";
    public int? ProcessId { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? ProjectPath { get; set; }
    public double? DurationMs { get; set; }
    public string Outcome { get; set; } = "success";
    public string? Error { get; set; }
    public string? Detail { get; set; }
}

internal sealed class DashboardLogEntry
{
    public long Sequence { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public double? DurationMs { get; set; }
    public string Origin { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
    public string? Detail { get; set; }
    public int? ProcessId { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? ProjectPath { get; set; }
    public string TabId { get; set; } = "";
}

internal sealed class DashboardSnapshot
{
    public string Epoch { get; set; } = "";
    public int Generation { get; set; }
    public bool LogsTruncated { get; set; }
    public bool TabsTruncated { get; set; }
    public int MaxLogEntries { get; set; }
    public int MaxHistoricalTabs { get; set; }
    public List<DashboardTabView> Tabs { get; set; } = new();
}

internal sealed class DashboardTabView
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int? ProcessId { get; set; }
    public string? Mode { get; set; }
    public string RuntimeState { get; set; } = "";
    public string? RuntimeIdentity { get; set; }
    public string ConnectionState { get; set; } = "";
    public Guid? ConnectionId { get; set; }
    public string? ProjectPath { get; set; }
    public string ProjectState { get; set; } = "";
    public bool CanAttach { get; set; }
    public string? UnavailableReason { get; set; }
    public string? Reason { get; set; }
    public string? CleanupError { get; set; }
    public bool Live { get; set; }
    public List<DashboardRuntimeRef> Previous { get; set; } = new();
}

internal sealed class DashboardRuntimeRef
{
    public int ProcessId { get; set; }
    public long RuntimeStartUtcTicks { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? Mode { get; set; }
}

internal sealed class DashboardLogPage
{
    public bool Reset { get; set; }
    public int Generation { get; set; }
    public long Oldest { get; set; }
    public long Next { get; set; }
    public bool Truncated { get; set; }
    public List<DashboardLogEntry> Entries { get; set; } = new();
}
