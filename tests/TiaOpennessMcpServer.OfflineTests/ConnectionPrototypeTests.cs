using TiaOpennessMcpServer.Prototype;

internal static class ConnectionPrototypeTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("prototype: independent connections and idempotent connect", IndependentConnections);
        yield return ("prototype: stale queued work cannot use a later attachment", QueuedRequest);
        yield return ("prototype: path change, close and projectless-to-open invalidate", PathTransitions);
        yield return ("prototype: same path with different native identity invalidates", SamePath);
        yield return ("prototype: unusable retained proxy invalidates despite equal identity", DeadProxy);
        yield return ("prototype: post-read transition discards payload", DuringRead);
        yield return ("prototype: native read failure with context loss requires reconnect", ReadFailureWithTransition);
        yield return ("prototype: ordinary read errors preserve valid connection", OrdinaryReadFailure);
        yield return ("prototype: PID reuse rejects work before native read", ReusedPid);
        yield return ("prototype: cleanup failure remains invalid and blocks duplicate attach", FailedCleanup);
        yield return ("prototype: monitor invalidates independently of dashboard", Monitor);
        yield return ("prototype: projectless connection returns noActiveProject", Projectless);
        yield return ("prototype: shutdown releases all attachments", Shutdown);
        yield return ("prototype: baseline transition during connection fails", DuringConnect);
        yield return ("prototype: worker affinity enforced before native access", WorkerAffinity);
        yield return ("discovery: process list shares state without attaching", ProcessDiscovery);
        yield return ("discovery: status without a connection never attaches", DisconnectedStatus);
        yield return ("discovery: projectless status available but inventories rejected", ProjectlessStatus);
        yield return ("discovery: every reader rejects earlier attachment tickets", QueuedDiscovery);
        yield return ("discovery: every reader discards a transitioned payload", DiscoveryTransition);
        yield return ("discovery: device selector and optional path reach retained context", DeviceSelector);
        yield return ("discovery: target errors preserve valid connections", TargetFailure);
    }

    private static void ProcessDiscovery()
    {
        var (r, b) = Setup();
        var initial = r.Discover();
        Check(initial.Processes.Count == 2 && initial.Processes.All(p => !p.ConnectedByMcp), "Initial state incorrect.");
        Check(b.Attaches == 0, "Discovery attached.");
        r.Connect(10); r.Connect(20);
        Check(r.Discover().Processes.All(p => p.ConnectedByMcp), "Multiple connections not reflected.");
        b.Processes[10].Start++;
        var changed = r.Discover();
        Check(!changed.Processes[0].ConnectedByMcp && changed.Processes[1].ConnectedByMcp, "Reused PID inherited state.");
        b.Processes.Remove(10);
        Check(r.Discover().Processes.Count == 1 && r.Views().Length == 2, "Closed process leaked into live list or history erased.");
        Check(b.Attaches == 2, "Discovery attached again.");
        b.Processes[20].Project!.Path = "Changed.ap20";
        Check(!r.Discover().Processes.Single().ConnectedByMcp, "Path transition retained connection authority.");
    }

    private static void DisconnectedStatus()
    {
        var (r, b) = Setup();
        var ticket = r.Capture(10, allowDisconnected: true);
        var result = r.ReadStatus(ticket);
        Check(result.State == "disconnected" && result.Project == null && result.Tia == null, "Disconnected context populated.");
        Check(b.Attaches == 0, "Status attached.");
        Fault("processNotFound", () => r.ReadStatus(r.Capture(99, allowDisconnected: true)));
        r.Connect(10);
        Fault("reconnectRequired", () => r.ReadStatus(ticket));
        Check(b.Processes[10].Reads == 0, "Disconnected request adopted new attachment.");
    }

    private static void ProjectlessStatus()
    {
        var (r, b) = Setup();
        b.Processes[10].Project = null;
        r.Connect(10);
        var ticket = r.Capture(10);
        var result = r.ReadStatus(ticket);
        Check(result.State == "connected" && result.Tia != null && result.Project == null, "Projectless status unavailable.");
        Fault("noActiveProject", () => r.ListDevices(ticket));
        Fault("noActiveProject", () => r.ReadDevice(ticket, "device", false));
        Fault("noActiveProject", () => r.ListBlocks(ticket, "cpu"));
        Check(b.Processes[10].Reads == 1, "Projectless inventory reached native adapter.");
    }

    private static IEnumerable<Action<ConnectionRegistry, RequestTicket>> DiscoveryOperations()
    {
        yield return (registry, ticket) => registry.ReadStatus(ticket);
        yield return (registry, ticket) => registry.ListDevices(ticket);
        yield return (registry, ticket) => registry.ReadDevice(ticket, "native-id", true);
        yield return (registry, ticket) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse("{\"processId\":" + ticket.ProcessId + ",\"objectId\":\"tag\"}");
            registry.ReadCrossReferences(ticket, CrossReferenceRequest.Parse(body.RootElement));
        };
        yield return (registry, ticket) => registry.ListTagTables(ticket, " native-cpu ");
        yield return (registry, ticket) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse("{\"processId\":" + ticket.ProcessId + ",\"objectId\":\"table\"}");
            registry.ReadTagTable(ticket, TagTableReadRequest.Parse(body.RootElement));
        };
        yield return (registry, ticket) => registry.ListUdts(ticket, " native-cpu ");
        yield return (registry, ticket) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse("{\"processId\":" + ticket.ProcessId + ",\"objectId\":\"udt\"}");
            registry.ReadUdt(ticket, BlockReadRequest.Parse(body.RootElement));
        };
        yield return (registry, ticket) => registry.ListBlocks(ticket, " native-cpu ");
        yield return (registry, ticket) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse("{\"processId\":" + ticket.ProcessId + ",\"objectId\":\"block\"}");
            registry.ReadBlock(ticket, BlockReadRequest.Parse(body.RootElement));
        };
    }

    private static void QueuedDiscovery()
    {
        foreach (var operation in DiscoveryOperations())
        {
            var (r, b) = Setup();
            r.Connect(10);
            var ticket = r.Capture(10);
            r.Disconnect(10); r.Connect(10);
            Fault("reconnectRequired", () => operation(r, ticket));
            Check(b.Processes[10].Reads == 0, "Discovery used a later attachment.");
            operation(r, r.Capture(10));
            Check(b.Processes[10].Reads == 1, "New discovery request failed.");
        }
    }

    private static void DiscoveryTransition()
    {
        foreach (var operation in DiscoveryOperations())
        {
            var (r, b) = Setup();
            r.Connect(10); r.Connect(20);
            var process = b.Processes[10];
            process.DuringRead = () => process.Project = new FakeProject("A.ap20");
            Fault("reconnectRequired", () => operation(r, r.Capture(10)));
            Check(process.Reads == 1 && process.Detaches == 1, "Invalid read retried or not detached.");
            r.ListDevices(r.Capture(20));
        }
    }

    private static void DeviceSelector()
    {
        var (r, b) = Setup();
        r.Connect(10); r.Connect(20);
        var result = r.ReadDevice(r.Capture(20), " native-id ", false);
        Check(result.ProcessId == 20 && (string?)result.Metadata["objectId"] == " native-id " &&
            result.Metadata["path"] == null, "Selector or path option changed.");
        Check(b.Processes[10].Reads == 0 && b.Processes[20].Reads == 1, "Wrong process read.");
    }

    private static void TargetFailure()
    {
        var (r, b) = Setup();
        r.Connect(10);
        b.Processes[10].ReadError = new ConnectionFault("objectNotFound", 10, "Missing object");
        Fault("objectNotFound", () => r.ReadDevice(r.Capture(10), "missing", true));
        Check(r.Views().Single().State == "connected", "Missing object invalidated the connection.");
    }

    private static (ConnectionRegistry Registry, Backend Backend) Setup()
    {
        var backend = new Backend();
        backend.Processes[10] = new FakeProcess("A.ap20");
        backend.Processes[20] = new FakeProcess("B.ap20");
        return (new ConnectionRegistry(backend, () => { }), backend);
    }

    private static void IndependentConnections()
    {
        var (r, b) = Setup();
        var first = r.Connect(10);
        r.Connect(20);
        Check(r.Connect(10).ConnectionId == first.ConnectionId, "Connect changed a valid attachment.");
        Check(b.Attaches == 2, "Duplicate native attach.");
        r.Disconnect(10);
        Check(r.Read(r.Capture(20)).Project.Name == "B.ap20", "Other process was affected.");
        r.Disconnect(10);
        Check(b.Processes[10].Detaches == 1, "Disconnect was not idempotent.");
    }

    private static void QueuedRequest()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var oldRequest = r.Capture(10);
        r.Disconnect(10);
        r.Connect(10);
        Fault("reconnectRequired", () => r.Read(oldRequest));
        Check(b.Processes[10].Reads == 0, "Queued work touched replacement attachment.");
        r.Read(r.Capture(10));
        Check(b.Processes[10].Reads == 1, "A newly submitted request should work.");
    }

    private static void PathTransitions()
    {
        foreach (var transition in new[] { "replace", "close", "open", "saveAs" })
        {
            var (r, b) = Setup();
            var p = b.Processes[10];
            if (transition == "open") p.Project = null;
            r.Connect(10);
            var ticket = r.Capture(10);
            if (transition == "close") p.Project = null;
            else if (transition == "saveAs") p.Project!.Path = "Changed.ap20";
            else p.Project = new FakeProject("New.ap20");
            Fault("reconnectRequired", () => r.Read(ticket));
            Check(p.Reads == 0 && p.Detaches == 1, "Transition did not fail before the read.");
        }
    }

    private static void SamePath()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var request = r.Capture(10);
        b.Processes[10].Project = new FakeProject("A.ap20");
        Fault("reconnectRequired", () => r.Read(request));
        Check(b.Processes[10].Reads == 0, "Different opening was accepted.");
    }

    private static void DeadProxy()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var request = r.Capture(10);
        b.Processes[10].Project!.Alive = false;
        Fault("reconnectRequired", () => r.Read(request));
        Check(b.Processes[10].Reads == 0, "Invalid proxy was used.");
    }

    private static void DuringRead()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var p = b.Processes[10];
        p.DuringRead = () => p.Project = new FakeProject("A.ap20");
        Fault("reconnectRequired", () => r.Read(r.Capture(10)));
        Check(p.Reads == 1 && p.Detaches == 1, "Read was retried or connection not invalidated.");
    }

    private static void ReadFailureWithTransition()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var p = b.Processes[10];
        p.DuringRead = () => p.Project = null;
        p.ReadError = new InvalidOperationException("Native project unavailable");
        var fault = Fault("reconnectRequired", () => r.Read(r.Capture(10)));
        Check(fault.InnerException == p.ReadError, "Lost the original native failure.");
        Check(p.Reads == 1 && p.Detaches == 1, "Retried failed work.");
    }

    private static void OrdinaryReadFailure()
    {
        var (r, b) = Setup();
        r.Connect(10);
        b.Processes[10].ReadError = new UnauthorizedAccessException("Object protected");
        Fault("nativeReadFailed", () => r.Read(r.Capture(10)));
        Check(r.Views().Single().State == "connected", "Ordinary failure disconnected the project.");
        Check(b.Processes[10].Detaches == 0, "Ordinary failure detached the client.");
    }

    private static void ReusedPid()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var ticket = r.Capture(10);
        b.Processes[10].Start++;
        Fault("reconnectRequired", () => r.Read(ticket));
        Check(b.Processes[10].Reads == 0, "PID reuse reached a native read.");
    }

    private static void FailedCleanup()
    {
        var (r, b) = Setup();
        r.Connect(10);
        var p = b.Processes[10];
        var ticket = r.Capture(10);
        p.FailDetach = true;
        p.Project = null;
        Fault("reconnectRequired", () => r.Read(ticket));
        Check(r.Views().Single().CleanupError != null, "Cleanup failure not exposed.");
        Fault("notConnected", () => r.Capture(10));
        Fault("cleanupFailed", () => r.Connect(10));
        Check(b.Attaches == 1, "Created another attachment over failed cleanup.");
        p.FailDetach = false;
        r.Connect(10);
        Check(b.Attaches == 2, "Failed cleanup could not be retried explicitly.");
        Fault("reconnectRequired", () => r.Read(ticket));
    }

    private static void Monitor()
    {
        var (r, b) = Setup();
        r.Connect(10); r.Connect(20);
        b.Processes[10].Project = null;
        r.Monitor();
        Check(r.Views().Single(x => x.ProcessId == 10).State == "invalidated", "Monitoring did not invalidate.");
        r.Read(r.Capture(20));
    }

    private static void Projectless()
    {
        var (r, b) = Setup();
        b.Processes[10].Project = null;
        r.Connect(10);
        Fault("noActiveProject", () => r.Read(r.Capture(10)));
        Check(r.Views().Single().State == "connected", "Stable projectless context was invalidated.");
    }

    private static void Shutdown()
    {
        var (r, b) = Setup();
        r.Connect(10); r.Connect(20); r.DisconnectAll();
        Check(b.Processes.Values.All(p => p.Detaches == 1), "Not all clients detached.");
        Check(r.Views().All(x => x.State == "disconnected"), "Shutdown retained a usable attachment.");
    }

    private static void DuringConnect()
    {
        var (r, b) = Setup();
        var p = b.Processes[10];
        p.OnGetProject = () => { p.Project = new FakeProject("B.ap20"); p.OnGetProject = null; };
        Fault("reconnectRequired", () => r.Connect(10));
        Check(p.Detaches == 1, "Failed connection leaked an attachment.");
    }

    private static void WorkerAffinity()
    {
        var b = new Backend();
        var r = new ConnectionRegistry(b, () => throw new InvalidOperationException("Wrong thread"));
        try { r.Connect(10); throw new Exception("Missing affinity assertion"); }
        catch (InvalidOperationException ex) when (ex.Message == "Wrong thread") { }
        Check(b.Attaches == 0, "Native adapter reached before affinity check.");
        Check(r.Views().Length == 0, "Passive status should be readable from any thread.");
    }

    private static ConnectionFault Fault(string code, Action action)
    {
        try { action(); }
        catch (ConnectionFault ex) { Check(ex.Code == code, $"Expected {code}, got {ex.Code}"); return ex; }
        throw new Exception("Expected failure: " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FakeProject
    {
        public string Path;
        public bool Alive = true;
        public FakeProject(string path) { Path = path; }
    }

    private sealed class FakeProcess
    {
        public long Start = 123;
        public FakeProject? Project;
        public int Reads, Detaches;
        public bool FailDetach;
        public Exception? ReadError;
        public Action? DuringRead, OnGetProject;
        public FakeProcess(string path) { Project = new FakeProject(path); }
    }

    private sealed class Backend : IConnectionBackend
    {
        public readonly Dictionary<int, FakeProcess> Processes = new();
        public int Attaches;
        public IReadOnlyList<ProcessObservation> Discover() => Processes.Select(pair => new ProcessObservation
        {
            ProcessId = pair.Key, RuntimeStartUtcTicks = pair.Value.Start, ProjectPath = pair.Value.Project?.Path,
            Mode = "with-ui", CanAttach = true
        }).ToArray();
        public IProjectAttachment Attach(int processId) { Attaches++; return new Attachment(processId, Processes[processId]); }
    }

    private sealed class Attachment(int processId, FakeProcess process) : IProjectAttachment
    {
        public ProcessObservation ObserveProcess() => new()
        {
            ProcessId = processId, RuntimeStartUtcTicks = process.Start, ProjectPath = process.Project?.Path, CanAttach = true
        };
        public object? GetPrimaryProject() { var project = process.Project; process.OnGetProject?.Invoke(); return project; }
        public string GetProjectPath(object project) => ((FakeProject)project).Alive
            ? ((FakeProject)project).Path : throw new InvalidOperationException("Stale native proxy");
        public bool SameProject(object retained, object current) => retained == current;
        public ProjectRead ReadProject(object retained)
        {
            process.Reads++;
            var result = new ProjectRead { Name = ((FakeProject)retained).Path, Path = ((FakeProject)retained).Path };
            process.DuringRead?.Invoke();
            if (process.ReadError != null) throw process.ReadError;
            return result;
        }
        public ProcessStatus ReadStatus(object? retained, Action validate)
        {
            process.Reads++;
            process.DuringRead?.Invoke();
            return new ProcessStatus { State = "connected", Tia = new() { ["mode"] = "with-ui" },
                Project = retained == null ? null : new() { ["path"] = ((FakeProject)retained).Path } };
        }
        public DeviceInventory ListDevices(object retained, Action validate)
        {
            ReadProject(retained);
            return new DeviceInventory();
        }
        public DeviceRead ReadDevice(object retained, string objectId, bool includePath, Action validate)
        {
            ReadProject(retained);
            return new DeviceRead { Metadata = new() { ["objectId"] = objectId,
                ["path"] = includePath ? ((FakeProject)retained).Path : null } };
        }
        public BlockInventory ListBlocks(object retained, string plcObjectId, Action validate)
        {
            ReadProject(retained);
            return new BlockInventory { PlcObjectId = plcObjectId };
        }
        public BlockRead ReadBlock(object retained, BlockReadRequest request, Action validate)
        {
            ReadProject(retained);
            return new BlockRead { Metadata = new() { ["objectId"] = request.ObjectId } };
        }
        public BlockInventory ListUdts(object retained, string plcObjectId, Action validate) => ListBlocks(retained, plcObjectId, validate);
        public BlockRead ReadUdt(object retained, BlockReadRequest request, Action validate) => ReadBlock(retained, request, validate);
        public BlockInventory ListTagTables(object retained, string plcObjectId, Action validate) => ListBlocks(retained, plcObjectId, validate);
        public TagTableRead ReadTagTable(object retained, TagTableReadRequest request, Action validate)
        {
            ReadProject(retained);
            return new TagTableRead { Metadata = new() { ["objectId"] = request.ObjectId } };
        }
        public CrossReferenceRead ReadCrossReferences(object retained, CrossReferenceRequest request, Action validate)
        {
            ReadProject(retained);
            return new CrossReferenceRead { Sources = new() };
        }
        public void Detach()
        {
            if (process.FailDetach) throw new InvalidOperationException("Cleanup failed");
            process.Detaches++;
        }
    }
}
