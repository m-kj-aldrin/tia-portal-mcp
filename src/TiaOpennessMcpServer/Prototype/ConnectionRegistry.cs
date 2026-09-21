using System.Diagnostics;

namespace TiaOpennessMcpServer.Prototype;

/// <summary>Owns native attachments on the STA worker. Only managed tickets/views cross threads.</summary>
internal sealed class ConnectionRegistry
{
    private sealed class Slot
    {
        public int ProcessId;
        public Guid Id = Guid.NewGuid();
        public long RuntimeStart;
        public string? Path;
        public object? Project;
        public IProjectAttachment? Attachment;
        public bool Active;
        public string State = "connecting";
        public string? Reason;
        public string? CleanupError;
    }

    private readonly IConnectionBackend _backend;
    private readonly Action _assertWorker;
    private readonly object _gate = new();
    private readonly Dictionary<int, Slot> _slots = new();
    private readonly Queue<ConnectionEvent> _events = new();

    public ConnectionRegistry(IConnectionBackend backend, Action assertWorker)
    {
        _backend = backend;
        _assertWorker = assertWorker;
    }

    public IReadOnlyList<ProcessObservation> Discover()
    {
        _assertWorker();
        return _backend.Discover();
    }

    public ConnectionView[] Views()
    {
        lock (_gate) return _slots.Values.Select(View).OrderBy(x => x.ProcessId).ToArray();
    }

    public ConnectionEvent[] Events()
    {
        lock (_gate) return _events.Reverse().ToArray();
    }

    // Called at request admission, before the request waits for the STA worker.
    public RequestTicket Capture(int processId)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(processId, out var slot) || !slot.Active)
                throw new ConnectionFault("notConnected", processId, "Connect this process in the prototype dashboard first.");
            return new RequestTicket(processId, slot.Id);
        }
    }

    public ConnectionView Connect(int processId)
    {
        _assertWorker();
        if (processId <= 0) throw new ConnectionFault("invalidRequest", processId, "A positive processId is required.");
        Slot? previous;
        lock (_gate) _slots.TryGetValue(processId, out previous);
        if (previous?.Active == true)
        {
            Validate(previous);
            return View(previous);
        }
        if (previous?.Attachment != null)
        {
            Release(previous);
            if (previous.Attachment != null)
                throw new ConnectionFault("cleanupFailed", processId, "The earlier attachment could not be released. No new attachment was made.");
        }

        var slot = new Slot { ProcessId = processId };
        lock (_gate) _slots[processId] = slot;
        try
        {
            slot.Attachment = _backend.Attach(processId);
            var observation = slot.Attachment.ObserveProcess();
            if (observation.ProcessId != processId || observation.RuntimeStartUtcTicks <= 0)
                throw new InvalidOperationException("The selected process identity could not be established.");
            slot.RuntimeStart = observation.RuntimeStartUtcTicks;
            slot.Path = observation.ProjectPath;
            slot.Project = slot.Attachment.GetPrimaryProject();
            // Reject inconsistencies observed while establishing the baseline.
            Validate(slot);
            lock (_gate)
            {
                slot.Active = true;
                slot.State = "connected";
            }
            Record(slot, "connected", "Approved the current project context.");
            return View(slot);
        }
        catch (Exception ex)
        {
            if (slot.State != "invalidated") Invalidate(slot, "Connection failed: " + ex.Message);
            if (ex is ConnectionFault) throw;
            throw new ConnectionFault("connectFailed", processId, ex.Message, ex);
        }
    }

    public ConnectionView? Disconnect(int processId)
    {
        _assertWorker();
        Slot? slot;
        lock (_gate) _slots.TryGetValue(processId, out slot);
        if (slot == null) return null;
        lock (_gate)
        {
            slot.Active = false;
            slot.State = "disconnected";
            slot.Reason = "Disconnected by the user.";
        }
        Release(slot);
        Record(slot, "disconnected", slot.Reason);
        return View(slot);
    }

    public GuardedRead Read(RequestTicket ticket)
    {
        _assertWorker();
        var slot = Resolve(ticket);
        var timer = Stopwatch.StartNew();
        Validate(slot);
        var before = timer.Elapsed.TotalMilliseconds;
        if (slot.Project == null)
            throw new ConnectionFault("noActiveProject", slot.ProcessId, "This connected process has no primary project.");

        ProjectRead project;
        try { project = slot.Attachment!.ReadProject(slot.Project); }
        catch (Exception ex)
        {
            // Recheck context to distinguish a project transition from a normal read failure.
            try { Validate(slot); }
            catch (ConnectionFault)
            {
                throw new ConnectionFault("reconnectRequired", slot.ProcessId,
                    "The project context became invalid during the read. Reconnect this process.", ex);
            }
            Record(slot, "readFailed", ex.Message);
            throw new ConnectionFault("nativeReadFailed", slot.ProcessId, ex.Message, ex);
        }
        var afterRead = timer.Elapsed.TotalMilliseconds;
        Validate(slot); // Do not expose a payload if a transition was detected after collecting it.
        var result = new GuardedRead
        {
            ProcessId = slot.ProcessId, ConnectionId = slot.Id, Project = project,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            BeforeCheckMs = before, ReadMs = afterRead - before,
            AfterCheckMs = timer.Elapsed.TotalMilliseconds - afterRead
        };
        Record(slot, "read", "Read project metadata and top-level device names; both context checks passed.");
        return result;
    }

    public void Monitor()
    {
        _assertWorker();
        Slot[] active;
        lock (_gate) active = _slots.Values.Where(x => x.Active).ToArray();
        foreach (var slot in active)
        {
            try { Validate(slot); }
            catch (ConnectionFault) { /* Invalidation and native cause are already recorded. */ }
        }
    }

    public void DisconnectAll()
    {
        _assertWorker();
        int[] ids;
        lock (_gate) ids = _slots.Keys.ToArray();
        foreach (var id in ids) Disconnect(id);
    }

    private Slot Resolve(RequestTicket ticket)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(ticket.ProcessId, out var slot) || !slot.Active || slot.Id != ticket.ConnectionId)
                throw new ConnectionFault("reconnectRequired", ticket.ProcessId,
                    "This request belongs to an earlier attachment and was rejected. Submit a new request after reconnection.");
            return slot;
        }
    }

    private void Validate(Slot slot)
    {
        try
        {
            var attachment = slot.Attachment ?? throw new InvalidOperationException("Attachment unavailable.");
            var observation = attachment.ObserveProcess();
            if (observation.ProcessId != slot.ProcessId || observation.RuntimeStartUtcTicks != slot.RuntimeStart)
                throw new InvalidOperationException("The TIA process exited or its process ID was reused.");
            if (!SamePath(slot.Path, observation.ProjectPath))
                throw new InvalidOperationException("The primary-project path changed.");
            var current = attachment.GetPrimaryProject();
            if (slot.Project == null)
            {
                if (current != null || slot.Path != null)
                    throw new InvalidOperationException("The projectless context changed.");
            }
            else
            {
                if (current == null || slot.Path == null || !attachment.SameProject(slot.Project, current))
                    throw new InvalidOperationException("The retained native project no longer matches the open project.");
                // Exercise the retained proxy, even when native Equals reports equality.
                if (!SamePath(slot.Path, attachment.GetProjectPath(slot.Project)))
                    throw new InvalidOperationException("The retained project path changed.");
            }
        }
        catch (Exception ex)
        {
            Invalidate(slot, ex.GetType().Name + ": " + ex.Message);
            throw new ConnectionFault("reconnectRequired", slot.ProcessId,
                "The project context changed or could not be validated. Reconnect this process. " + ex.Message, ex);
        }
    }

    private void Invalidate(Slot slot, string reason)
    {
        lock (_gate)
        {
            slot.Active = false;
            slot.State = "invalidated";
            slot.Reason = reason;
        }
        Record(slot, "invalidated", reason);
        Release(slot);
    }

    private void Release(Slot slot)
    {
        try
        {
            slot.Attachment?.Detach();
            lock (_gate)
            {
                slot.Attachment = null;
                slot.Project = null;
                slot.CleanupError = null;
            }
        }
        catch (Exception ex)
        {
            lock (_gate) slot.CleanupError = ex.GetType().Name + ": " + ex.Message;
            Record(slot, "cleanupFailed", slot.CleanupError);
            // Retain the failed cleanup handle, but never make it available to operations.
        }
    }

    private void Record(Slot slot, string action, string message)
    {
        lock (_gate)
        {
            _events.Enqueue(new ConnectionEvent
            {
                ProcessId = slot.ProcessId, ConnectionId = slot.Id, Action = action, Message = message
            });
            while (_events.Count > 100) _events.Dequeue();
        }
    }

    private static ConnectionView View(Slot slot) => new()
    {
        ProcessId = slot.ProcessId, ConnectionId = slot.Id, ApprovedProjectPath = slot.Path,
        State = slot.State, Reason = slot.Reason, CleanupError = slot.CleanupError
    };

    private static bool SamePath(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
