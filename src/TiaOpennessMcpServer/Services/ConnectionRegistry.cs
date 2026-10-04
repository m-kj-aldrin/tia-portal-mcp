using TiaOpennessMcpServer.Operations;

namespace TiaOpennessMcpServer.Services;

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
        public string? Mode;
        public string State = "connecting";
        public bool Active => State == "connected";
        public string? Reason;
        public string? CleanupError;
    }

    private readonly IConnectionBackend _backend;
    private readonly Action _assertWorker;
    private readonly object _gate = new();
    private readonly Dictionary<int, Slot> _slots = new();
    private readonly Queue<ConnectionEvent> _events = new();
    private ProcessObservation[] _seen = Array.Empty<ProcessObservation>();
    private Action<ConnectionEvent>? _listener;
    private long _eventSequence;

    public ConnectionRegistry(IConnectionBackend backend, Action assertWorker)
    {
        _backend = backend;
        _assertWorker = assertWorker;
    }

    public ProcessDiscovery Discover()
    {
        _assertWorker();
        var observations = _backend.Discover();
        lock (_gate) _seen = observations.Select(CloneObservation).ToArray();
        Slot[] active;
        lock (_gate) active = _slots.Values.Where(x => x.Active).ToArray();
        foreach (var slot in active)
        {
            var observed = observations.FirstOrDefault(x => x.ProcessId == slot.ProcessId);
            if (observed == null || observed.RuntimeStartUtcTicks != slot.RuntimeStart ||
                !ProjectPath.Same(observed.ProjectPath, slot.Path))
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

    public void Listen(Action<ConnectionEvent> listener) => _listener = listener;

    public ProcessObservation[] Observations()
    {
        lock (_gate) return _seen.Select(CloneObservation).ToArray();
    }

    public string? ApprovedPath(RequestTicket ticket)
    {
        lock (_gate)
        {
            if (ticket.ConnectionId == Guid.Empty) return null;
            if (_slots.TryGetValue(ticket.ProcessId, out var slot) && slot.Id == ticket.ConnectionId)
                return slot.Path;
            return null;
        }
    }

    // Called at request admission, before the request waits for the STA worker.
    public RequestTicket Capture(int processId, bool allowDisconnected = false, Guid? expectedConnectionId = null)
    {
        lock (_gate)
        {
            // Compare under the same lock as ticket creation. A same-PID reconnect
            // must not turn an admitted dashboard action into a new request.
            if (expectedConnectionId is Guid expected)
            {
                var connected = _slots.TryGetValue(processId, out var current) && current.Active;
                if (expected == Guid.Empty && allowDisconnected && !connected)
                    return new RequestTicket(processId, Guid.Empty);
                if (!connected || current!.Id != expected)
                    throw new ConnectionFault("reconnectRequired", processId,
                        "The connection changed before this dashboard request. Reconnect and inspect the current project before issuing a new request.");
            }
            if (!_slots.TryGetValue(processId, out var slot) || !slot.Active)
            {
                if (allowDisconnected) return new RequestTicket(processId, Guid.Empty);
                throw new ConnectionFault("notConnected", processId, "Connect this process in the dashboard first.");
            }
            return new RequestTicket(processId, slot.Id);
        }
    }

    public ConnectionView Connect(int processId, long expectedRuntimeStartUtcTicks, string? expectedProjectPath)
    {
        _assertWorker();
        if (processId <= 0) throw new ConnectionFault("invalidRequest", processId, "A positive processId is required.");
        if (expectedRuntimeStartUtcTicks <= 0) throw new ConnectionFault("invalidRequest", processId, "A positive runtime start is required.");
        // The dashboard card is only a snapshot. Refresh before touching a
        // retained slot, then verify again on the attachment itself below.
        var observed = _backend.Discover().FirstOrDefault(item => item.ProcessId == processId);
        if (observed == null || observed.RuntimeStartUtcTicks != expectedRuntimeStartUtcTicks ||
            !ProjectPath.Same(ProjectPath.Canonical(observed.ProjectPath), ProjectPath.Canonical(expectedProjectPath)))
            throw new ConnectionFault("reconnectRequired", processId,
                "The selected TIA process or project changed after this dashboard card was rendered. Refresh and select it again.");
        Slot? previous;
        lock (_gate) _slots.TryGetValue(processId, out previous);
        if (previous?.Active == true)
        {
            Validate(previous);
            Remember(previous);
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
            if (observation.RuntimeStartUtcTicks != expectedRuntimeStartUtcTicks ||
                !ProjectPath.Same(ProjectPath.Canonical(observation.ProjectPath), ProjectPath.Canonical(expectedProjectPath)))
                throw new ConnectionFault("reconnectRequired", processId,
                    "The selected TIA process or project changed while connecting. Refresh and select it again.");
            slot.RuntimeStart = observation.RuntimeStartUtcTicks;
            slot.Path = observation.ProjectPath;
            slot.Mode = observation.Mode;
            slot.Project = slot.Attachment.GetPrimaryProject();
            // Reject inconsistencies observed while establishing the baseline.
            Validate(slot);
            lock (_gate) slot.State = "connected";
            Record(slot, "connected", "Approved the current project context.");
            Remember(slot);
            return View(slot);
        }
        catch (Exception ex)
        {
            if (slot.State != "invalidated") Invalidate(slot, "Connection failed: " + ex.Message);
            if (ex is ConnectionFault) throw;
            throw new ConnectionFault("connectFailed", processId, ex.Message, ex);
        }
    }

    public ConnectionView OpenProject(string projectPath)
    {
        _assertWorker();
        var path = ProjectPath.Canonical(projectPath);
        if (path == null) throw new ConnectionFault("invalidRequest", 0, "A stored project path is required.");
        if (_backend.Discover().Any(item => item.RuntimeStartUtcTicks > 0 &&
            ProjectPath.Same(ProjectPath.Canonical(item.ProjectPath), path)))
            throw new ConnectionFault("invalidRequest", 0, "That project is already open in a running TIA process.");

        IProjectAttachment attachment;
        try { attachment = _backend.OpenProject(path); }
        catch (Exception ex)
        {
            if (ex is ConnectionFault) throw;
            throw new ConnectionFault("openFailed", 0, ex.Message, ex);
        }

        ProcessObservation observation;
        try { observation = attachment.ObserveProcess(); }
        catch (Exception ex)
        {
            CloseNewInstance(attachment);
            throw new ConnectionFault("openFailed", 0, ex.Message, ex);
        }
        var opened = ProjectPath.Canonical(observation.ProjectPath);
        if (observation.ProcessId <= 0 || observation.RuntimeStartUtcTicks <= 0 || observation.Mode != "with-ui" ||
            !ProjectPath.Same(opened, path))
        {
            CloseNewInstance(attachment);
            throw new ConnectionFault("openFailed", observation.ProcessId, "The new TIA window did not open the requested project.");
        }

        Slot? previous;
        lock (_gate) _slots.TryGetValue(observation.ProcessId, out previous);
        if (previous?.Active == true || previous?.Attachment != null)
        {
            CloseNewInstance(attachment);
            throw new ConnectionFault("openFailed", observation.ProcessId, "The new TIA process could not be registered.");
        }

        var slot = new Slot { ProcessId = observation.ProcessId, Attachment = attachment };
        lock (_gate) _slots[observation.ProcessId] = slot;
        try
        {
            slot.RuntimeStart = observation.RuntimeStartUtcTicks;
            slot.Path = opened;
            slot.Mode = observation.Mode;
            var project = attachment.GetPrimaryProject() ?? throw new InvalidOperationException("The new TIA window has no project.");
            if (!ProjectPath.Same(ProjectPath.Canonical(attachment.GetProjectPath(project)), path))
                throw new InvalidOperationException("The opened project path does not match the requested path.");
            var confirmed = attachment.ObserveProcess();
            if (confirmed.ProcessId != observation.ProcessId || confirmed.RuntimeStartUtcTicks != observation.RuntimeStartUtcTicks ||
                !ProjectPath.Same(ProjectPath.Canonical(confirmed.ProjectPath), path))
                throw new InvalidOperationException("The new TIA window changed during open.");
            slot.Project = project;
            lock (_gate) slot.State = "connected";
            Record(slot, "connected", "Opened the project in a new TIA window and approved its context.");
            Remember(slot);
            return View(slot);
        }
        catch (Exception ex)
        {
            CloseNewInstance(attachment);
            lock (_gate)
            {
                slot.Attachment = null;
                slot.Project = null;
                slot.State = "disconnected";
                _slots.Remove(slot.ProcessId);
            }
            if (ex is ConnectionFault) throw;
            throw new ConnectionFault("openFailed", slot.ProcessId, ex.Message, ex);
        }
    }

    private static void CloseNewInstance(IProjectAttachment attachment)
    {
        try { attachment.CloseStartedInstance(); }
        catch (Exception) { /* The failed open must not leave this attempt connected. */ }
    }

    private ConnectionView? Disconnect(int processId) => Disconnect(processId, null, null, null);

    public ConnectionView? Disconnect(int processId, long expectedRuntimeStartUtcTicks,
        string? expectedProjectPath, Guid expectedConnectionId) =>
        Disconnect(processId, expectedRuntimeStartUtcTicks, expectedProjectPath, (Guid?)expectedConnectionId);

    private ConnectionView? Disconnect(int processId, long? expectedRuntimeStartUtcTicks,
        string? expectedProjectPath, Guid? expectedConnectionId)
    {
        _assertWorker();
        Slot? slot;
        lock (_gate) _slots.TryGetValue(processId, out slot);
        if (expectedConnectionId is Guid expectedId &&
            (slot == null || slot.Id != expectedId || slot.RuntimeStart != expectedRuntimeStartUtcTicks ||
             !ProjectPath.Same(ProjectPath.Canonical(slot.Path), ProjectPath.Canonical(expectedProjectPath))))
            throw new ConnectionFault("reconnectRequired", processId,
                "The selected dashboard connection changed. Refresh before disconnecting it.");
        if (slot == null) return null;
        lock (_gate)
        {
            slot.State = "disconnected";
            slot.Reason = "Disconnected by the user.";
        }
        Release(slot);
        Record(slot, "disconnected", slot.Reason);
        return View(slot);
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
        return Execute(ticket, false, "processStatus", (attachment, project, validate) =>
            attachment.ReadStatus(project, validate));
    }

    public DeviceInventory ListDevices(RequestTicket ticket) => ReadDiscovery(ticket, "listDevices",
        (attachment, project, validate) => attachment.ListDevices(project, validate));

    public HardwareCatalogRead SearchHardwareCatalog(RequestTicket ticket, HardwareCatalogRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return ReadDiscovery(ticket, "searchHardwareCatalog", (attachment, project, validate) =>
            attachment.SearchHardwareCatalog(project, request, validate));
    }

    public BlockInventory ListBlocks(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, "listBlocks",
        (attachment, project, validate) => attachment.ListBlocks(project, plcObjectId, validate));

    public BlockRead ReadBlock(RequestTicket ticket, BlockReadRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        var result = ReadDiscovery(ticket, "getBlock", (attachment, project, validate) => attachment.ReadBlock(project, request, validate));
        RecordAttempts(ticket, result);
        return result;
    }

    public BlockInventory ListUdts(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, "listUdts",
        (attachment, project, validate) => attachment.ListUdts(project, plcObjectId, validate));

    public BlockRead ReadUdt(RequestTicket ticket, BlockReadRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        var result = ReadDiscovery(ticket, "getUdt", (attachment, project, validate) => attachment.ReadUdt(project, request, validate));
        RecordAttempts(ticket, result);
        return result;
    }

    public BlockInventory ListTagTables(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, "listTagTables",
        (attachment, project, validate) => attachment.ListTagTables(project, plcObjectId, validate));

    public TagTableRead ReadTagTable(RequestTicket ticket, TagTableReadRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return ReadDiscovery(ticket, "getTagTable", (attachment, project, validate) => attachment.ReadTagTable(project, request, validate));
    }

    public BlockInventory ListTechnologyObjects(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, "listTechnologyObjects",
        (attachment, project, validate) => attachment.ListTechnologyObjects(project, plcObjectId, validate));

    public AvailableTechnologyObjects ListAvailableTechnologyObjects(RequestTicket ticket, string plcObjectId) => ReadDiscovery(ticket, "listAvailableTechnologyObjects",
        (attachment, project, validate) => attachment.ListAvailableTechnologyObjects(project, plcObjectId, validate));

    public TechnologyObjectRead ReadTechnologyObject(RequestTicket ticket, TechnologyObjectReadRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return ReadDiscovery(ticket, "getTechnologyObject", (attachment, project, validate) => attachment.ReadTechnologyObject(project, request, validate));
    }

    public CrossReferenceRead ReadCrossReferences(RequestTicket ticket, CrossReferenceRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return ReadDiscovery(ticket, "getCrossReferences", (attachment, project, validate) => attachment.ReadCrossReferences(project, request, validate));
    }

    public WriteResult Write(RequestTicket ticket, WriteRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return Execute(ticket, true, request.Tool, (attachment, project, validate) =>
        {
            validate();
            return attachment.Write(project!, request, validate);
        }, "Write completed; both context checks passed.",
            "The project context became invalid during the write. Its outcome is uncertain; do not retry automatically. Reconnect this process.", failedCode: "nativeWriteFailed");
    }

    public TagTableExportResult ExportTagTable(RequestTicket ticket, ExportTagTableRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return ReadDiscovery(ticket, "export_tag_table", (attachment, project, validate) =>
            attachment.ExportTagTable(project, request, validate));
    }

    public CompileResult Compile(RequestTicket ticket, CompileRequest request)
    {
        RequireSameProcess(ticket, request.ProcessId);
        return Execute(ticket, true, "compile_plc", (attachment, project, validate) =>
        {
            validate();
            return attachment.Compile(project!, request, validate);
        }, "Compilation returned; both context checks passed.",
            "The project context became invalid during compilation. Its outcome is uncertain; do not retry automatically. Reconnect this process.", failedCode: "nativeCompileFailed");
    }

    public DeviceRead ReadDevice(RequestTicket ticket, string objectId, bool includePath) =>
        ReadDiscovery(ticket, "getDevice", (attachment, project, validate) =>
            attachment.ReadDevice(project, objectId, includePath, validate));

    private static void RequireSameProcess(RequestTicket ticket, int processId)
    {
        if (ticket.ProcessId != processId)
            throw new ConnectionFault("invalidRequest", processId, "Request and attachment process differ.");
    }

    private void RecordAttempts(RequestTicket ticket, BlockRead result)
    {
        foreach (var attempt in result.Attempts)
            Record(Resolve(ticket), "sourceExport", attempt.Format + ": " + attempt.State +
                (attempt.Errors.Count == 0 ? "" : " — " + DiscoveryError.Summary(attempt.Errors)));
    }

    private T ReadDiscovery<T>(RequestTicket ticket, string operation, Func<IProjectAttachment, object, Action, T> read)
        where T : DiscoveryResult =>
        Execute(ticket, true, operation, (attachment, project, validate) => read(attachment, project!, validate));

    private T Execute<T>(RequestTicket ticket, bool requiresProject, string operation,
        Func<IProjectAttachment, object?, Action, T> read,
        string completed = "Read completed; both context checks passed.",
        string lost = "The project context became invalid during the read. Reconnect this process.", string failedCode = "nativeReadFailed")
        where T : DiscoveryResult
    {
        _assertWorker();
        var slot = Resolve(ticket);
        Validate(slot);
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
                throw new ConnectionFault("reconnectRequired", slot.ProcessId, lost, ex);
            }
            Record(slot, "readFailed", ex.Message);
            if (ex is ConnectionFault) throw;
            throw new ConnectionFault(failedCode, slot.ProcessId, ex.Message, ex);
        }
        Validate(slot); // Do not expose a payload if a transition was detected after collecting it.
        Record(slot, operation, completed);
        payload.ProcessId = ticket.ProcessId;
        payload.ReadAtUtc = DateTimeOffset.UtcNow;
        return payload;
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
            if (!ProjectPath.Same(slot.Path, observation.ProjectPath))
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
                if (!ProjectPath.Same(slot.Path, attachment.GetProjectPath(slot.Project)))
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
        ConnectionEvent ev;
        lock (_gate)
        {
            ev = new ConnectionEvent
            {
                Sequence = ++_eventSequence, ProcessId = slot.ProcessId, ConnectionId = slot.Id,
                Action = action, Message = message
            };
            _events.Enqueue(ev);
            while (_events.Count > 100) _events.Dequeue();
        }
        _listener?.Invoke(ev);
    }

    private void Remember(Slot slot)
    {
        var observation = new ProcessObservation
        {
            ProcessId = slot.ProcessId, RuntimeStartUtcTicks = slot.RuntimeStart, ProjectPath = slot.Path,
            Mode = slot.Mode, CanAttach = true
        };
        lock (_gate)
        {
            var list = _seen.Where(item => item.ProcessId != slot.ProcessId).ToList();
            list.Add(CloneObservation(observation));
            _seen = list.ToArray();
        }
    }

    private static ProcessObservation CloneObservation(ProcessObservation source) => new()
    {
        ProcessId = source.ProcessId, RuntimeStartUtcTicks = source.RuntimeStartUtcTicks,
        ProjectPath = source.ProjectPath, Mode = source.Mode, CanAttach = source.CanAttach,
        UnavailableReason = source.UnavailableReason
    };

    private static ConnectionView View(Slot slot) => new()
    {
        ProcessId = slot.ProcessId, ConnectionId = slot.Id, RuntimeStartUtcTicks = slot.RuntimeStart,
        ApprovedProjectPath = slot.Path, State = slot.State, Reason = slot.Reason, CleanupError = slot.CleanupError
    };
}
