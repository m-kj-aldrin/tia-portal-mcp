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

    public ProcessDiscovery Discover()
    {
        _assertWorker();
        var observations = _backend.Discover();
        Slot[] active;
        lock (_gate) active = _slots.Values.Where(x => x.Active).ToArray();
        foreach (var slot in active)
        {
            var observed = observations.FirstOrDefault(x => x.ProcessId == slot.ProcessId);
            if (observed == null || observed.RuntimeStartUtcTicks != slot.RuntimeStart ||
                !SamePath(observed.ProjectPath, slot.Path))
                Invalidate(slot, "Fresh process discovery no longer matches the approved runtime and project path.");
        }
        var result = new ProcessDiscovery();
        foreach (var observed in observations)
        {
            bool connected;
            lock (_gate) connected = _slots.TryGetValue(observed.ProcessId, out var slot) && slot.Active;
            result.Processes.Add(new ProcessEntry
            {
                ProcessId = observed.ProcessId, Mode = observed.Mode, PrimaryProjectPath = observed.ProjectPath,
                ConnectedByMcp = connected, CanAttach = observed.CanAttach, UnavailableReason = observed.UnavailableReason
            });
            if (observed.RuntimeStartUtcTicks == 0 && observed.UnavailableReason != null)
                result.Errors.Add(new DiscoveryError { Operation = "observeProcess", Path = observed.ProcessId.ToString(),
                    Message = observed.UnavailableReason, Origin = "bridge" });
        }
        return result;
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
    public RequestTicket Capture(int processId, bool allowDisconnected = false)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(processId, out var slot) || !slot.Active)
            {
                if (allowDisconnected) return new RequestTicket(processId, Guid.Empty);
                throw new ConnectionFault("notConnected", processId, "Connect this process in the prototype dashboard first.");
            }
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
        var read = Execute(ticket, true, "read", (attachment, project, _) => attachment.ReadProject(project!));
        return new GuardedRead
        {
            ProcessId = ticket.ProcessId, ConnectionId = ticket.ConnectionId, Project = read.Value,
            CheckedAtUtc = DateTimeOffset.UtcNow, BeforeCheckMs = read.BeforeCheckMs,
            ReadMs = read.ReadMs, AfterCheckMs = read.AfterCheckMs
        };
    }

    public ProcessStatus ReadStatus(RequestTicket ticket)
    {
        _assertWorker();
        if (ticket.ConnectionId == Guid.Empty)
        {
            lock (_gate)
            {
                if (_slots.TryGetValue(ticket.ProcessId, out var slot))
                {
                    if (slot.Active || slot.State == "invalidated")
                        throw new ConnectionFault("reconnectRequired", ticket.ProcessId,
                            "The connection changed or was invalidated. Submit status again after explicit reconnection.");
                }
            }
            try
            {
                if (!_backend.Discover().Any(x => x.ProcessId == ticket.ProcessId))
                    throw new ConnectionFault("processNotFound", ticket.ProcessId, "The selected TIA process is no longer available.");
            }
            catch (ConnectionFault) { throw; }
            catch (Exception ex) { throw new ConnectionFault("nativeReadFailed", ticket.ProcessId, ex.Message, ex); }
            return new ProcessStatus { ProcessId = ticket.ProcessId };
        }
        return ReadDiscovery(ticket, false, "processStatus", (attachment, project, validate) =>
            attachment.ReadStatus(project, validate));
    }

    public DeviceInventory ListDevices(RequestTicket ticket) => ReadDiscovery(ticket, true, "listDevices",
        (attachment, project, validate) => attachment.ListDevices(project!, validate));

    public BlockInventory ListBlocks(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, true, "listBlocks",
        (attachment, project, validate) => attachment.ListBlocks(project!, plcObjectId, validate));

    public BlockRead ReadBlock(RequestTicket ticket, BlockReadRequest request)
    {
        if (ticket.ProcessId != request.ProcessId)
            throw new ConnectionFault("invalidRequest", request.ProcessId, "Request and attachment process differ.");
        var result = ReadDiscovery(ticket, true, "getBlock", (attachment, project, validate) => attachment.ReadBlock(project!, request, validate));
        foreach (var attempt in result.Attempts)
            Record(Resolve(ticket), "sourceExport", attempt.Format + ": " + attempt.State +
                (attempt.Errors.Count == 0 ? "" : " — " + string.Join(" | ", attempt.Errors.Select(error => error.Origin + ": " + error.Message))));
        return result;
    }

    public BlockInventory ListUdts(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, true, "listUdts",
        (attachment, project, validate) => attachment.ListUdts(project!, plcObjectId, validate));

    public BlockRead ReadUdt(RequestTicket ticket, BlockReadRequest request)
    {
        if (ticket.ProcessId != request.ProcessId)
            throw new ConnectionFault("invalidRequest", request.ProcessId, "Request and attachment process differ.");
        var result = ReadDiscovery(ticket, true, "getUdt", (attachment, project, validate) => attachment.ReadUdt(project!, request, validate));
        foreach (var attempt in result.Attempts)
            Record(Resolve(ticket), "sourceExport", attempt.Format + ": " + attempt.State +
                (attempt.Errors.Count == 0 ? "" : " — " + string.Join(" | ", attempt.Errors.Select(error => error.Origin + ": " + error.Message))));
        return result;
    }

    public BlockInventory ListTagTables(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, true, "listTagTables",
        (attachment, project, validate) => attachment.ListTagTables(project!, plcObjectId, validate));

    public TagTableRead ReadTagTable(RequestTicket ticket, TagTableReadRequest request)
    {
        if (ticket.ProcessId != request.ProcessId)
            throw new ConnectionFault("invalidRequest", request.ProcessId, "Request and attachment process differ.");
        return ReadDiscovery(ticket, true, "getTagTable", (attachment, project, validate) => attachment.ReadTagTable(project!, request, validate));
    }

    public DeviceRead ReadDevice(RequestTicket ticket, string objectId, bool includePath) =>
        ReadDiscovery(ticket, true, "getDevice", (attachment, project, validate) =>
            attachment.ReadDevice(project!, objectId, includePath, validate));

    private T ReadDiscovery<T>(RequestTicket ticket, bool requiresProject, string operation,
        Func<IProjectAttachment, object?, Action, T> read) where T : DiscoveryResult
    {
        var result = Execute(ticket, requiresProject, operation, read).Value;
        result.ProcessId = ticket.ProcessId;
        result.ReadAtUtc = DateTimeOffset.UtcNow;
        return result;
    }

    private sealed class TimedRead<T>
    {
        public T Value = default!;
        public double BeforeCheckMs, ReadMs, AfterCheckMs;
    }

    private TimedRead<T> Execute<T>(RequestTicket ticket, bool requiresProject, string operation,
        Func<IProjectAttachment, object?, Action, T> read)
    {
        _assertWorker();
        var slot = Resolve(ticket);
        var timer = Stopwatch.StartNew();
        Validate(slot);
        var before = timer.Elapsed.TotalMilliseconds;
        if (requiresProject && slot.Project == null)
            throw new ConnectionFault("noActiveProject", slot.ProcessId, "This connected process has no primary project.");

        T payload;
        try { payload = read(slot.Attachment!, slot.Project, () => Validate(slot)); }
        catch (Exception ex)
        {
            if (!slot.Active) throw; // A traversal checkpoint already invalidated and released this attachment.
            // Recheck context to distinguish a project transition from a normal read failure.
            try { Validate(slot); }
            catch (ConnectionFault)
            {
                throw new ConnectionFault("reconnectRequired", slot.ProcessId,
                    "The project context became invalid during the read. Reconnect this process.", ex);
            }
            Record(slot, "readFailed", ex.Message);
            if (ex is ConnectionFault) throw;
            throw new ConnectionFault("nativeReadFailed", slot.ProcessId, ex.Message, ex);
        }
        var afterRead = timer.Elapsed.TotalMilliseconds;
        Validate(slot); // Do not expose a payload if a transition was detected after collecting it.
        var result = new TimedRead<T>
        {
            Value = payload,
            BeforeCheckMs = before, ReadMs = afterRead - before,
            AfterCheckMs = timer.Elapsed.TotalMilliseconds - afterRead
        };
        Record(slot, operation, "Read completed; both context checks passed.");
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
