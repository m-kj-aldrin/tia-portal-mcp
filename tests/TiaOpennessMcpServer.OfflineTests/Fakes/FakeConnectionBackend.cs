using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;

internal sealed class FakeProject
{
    public string Path;
    public bool Alive = true;
    public FakeProject(string path) { Path = path; }
}

internal sealed class FakeProcess
{
    public long Start = 123;
    public FakeProject? Project;
    public int Reads, Detaches;
    public bool FailDetach, StartedByServer, ClosedByServer, Exited;
    public Exception? ReadError;
    public Action? DuringRead, OnGetProject;
    public FakeProcess(string path) { Project = new FakeProject(path); }
}

internal sealed class FakeConnectionBackend : IConnectionBackend
{
    public readonly Dictionary<int, FakeProcess> Processes = new();
    public int Attaches, Opens;
    public Exception? OpenError;
    public string? OpenedPath;
    public IReadOnlyList<ProcessObservation> Discover() => Processes.Where(pair => !pair.Value.Exited).Select(pair => new ProcessObservation
    {
        ProcessId = pair.Key, RuntimeStartUtcTicks = pair.Value.Start, ProjectPath = pair.Value.Project?.Path,
        Mode = "with-ui", CanAttach = true
    }).ToArray();
    public IProjectAttachment Attach(int processId) { Attaches++; return new FakeProjectAttachment(processId, Processes[processId]); }
    public IProjectAttachment OpenProject(string projectPath)
    {
        Opens++;
        if (OpenError != null) throw OpenError;
        var id = Processes.Count == 0 ? 70 : Processes.Keys.Max() + 1;
        var process = new FakeProcess(OpenedPath ?? projectPath) { Start = 900 + Opens, StartedByServer = true };
        Processes[id] = process;
        return new FakeProjectAttachment(id, process);
    }
}

internal sealed class FakeProjectAttachment(int processId, FakeProcess process) : IProjectAttachment
{
    public ProcessObservation ObserveProcess() => new()
    {
        ProcessId = processId, RuntimeStartUtcTicks = process.Start, ProjectPath = process.Project?.Path,
        Mode = "with-ui", CanAttach = true
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
    public void CloseStartedInstance()
    {
        if (!process.StartedByServer) throw new InvalidOperationException("Refusing to close a TIA process this server did not start.");
        process.ClosedByServer = true;
        process.Exited = true;
    }
    public bool? ProjectModified(object project) => null;
    public WriteResult Write(object retained, WriteRequest request, Action validate)
    {
        ReadProject(retained);
        return new WriteResult { Operation = request.Tool };
    }
}
