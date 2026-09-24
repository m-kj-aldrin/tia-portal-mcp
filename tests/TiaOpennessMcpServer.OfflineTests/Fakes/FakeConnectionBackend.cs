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
    public int Reads, Detaches, Compiles, Exports;
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
    private void ObserveOperation()
    {
        process.Reads++;
        process.DuringRead?.Invoke();
        if (process.ReadError != null) throw process.ReadError;
    }
    public ProcessStatus ReadStatus(object? retained, Action validate)
    {
        ObserveOperation();
        return new ProcessStatus { State = "connected", Tia = new() { ["mode"] = "with-ui" },
            Project = retained == null ? null : new() { ["path"] = ((FakeProject)retained).Path } };
    }
    public DeviceInventory ListDevices(object retained, Action validate)
    {
        ObserveOperation();
        return new DeviceInventory();
    }
    public DeviceRead ReadDevice(object retained, string objectId, bool includePath, Action validate)
    {
        ObserveOperation();
        return new DeviceRead { Metadata = new() { ["objectId"] = objectId,
            ["path"] = includePath ? ((FakeProject)retained).Path : null } };
    }
    public BlockInventory ListBlocks(object retained, string plcObjectId, Action validate)
    {
        ObserveOperation();
        return new BlockInventory { PlcObjectId = plcObjectId };
    }
    public BlockRead ReadBlock(object retained, BlockReadRequest request, Action validate)
    {
        ObserveOperation();
        return new BlockRead { Metadata = new() { ["objectId"] = request.ObjectId } };
    }
    public BlockInventory ListUdts(object retained, string plcObjectId, Action validate) => ListBlocks(retained, plcObjectId, validate);
    public BlockRead ReadUdt(object retained, BlockReadRequest request, Action validate) => ReadBlock(retained, request, validate);
    public BlockInventory ListTagTables(object retained, string plcObjectId, Action validate) => ListBlocks(retained, plcObjectId, validate);
    public BlockInventory ListTechnologyObjects(object retained, string plcObjectId, Action validate) => ListBlocks(retained, plcObjectId, validate);
    public TechnologyObjectRead ReadTechnologyObject(object retained, TechnologyObjectReadRequest request, Action validate)
    {
        ObserveOperation();
        return new TechnologyObjectRead { Metadata = new() { ["objectId"] = request.ObjectId }, Parameters = request.IncludeParameters ? new() : null };
    }
    public TagTableRead ReadTagTable(object retained, TagTableReadRequest request, Action validate)
    {
        ObserveOperation();
        return new TagTableRead { Metadata = new() { ["objectId"] = request.ObjectId } };
    }
    public CrossReferenceRead ReadCrossReferences(object retained, CrossReferenceRequest request, Action validate)
    {
        ObserveOperation();
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
    public WriteResult Write(object retained, WriteRequest request, Action validate)
    {
        ObserveOperation();
        return new WriteResult { Operation = request.Tool };
    }
    public CompileResult Compile(object retained, CompileRequest request, Action validate)
    {
        process.Compiles++;
        ObserveOperation();
        return new CompileResult { PlcObjectId = request.PlcObjectId, State = "Success", ErrorCount = 0, WarningCount = 0, Messages = new() };
    }
    public TagTableExportResult ExportTagTable(object retained, ExportTagTableRequest request, Action validate)
    {
        process.Exports++;
        ObserveOperation();
        return new TagTableExportResult();
    }
}
