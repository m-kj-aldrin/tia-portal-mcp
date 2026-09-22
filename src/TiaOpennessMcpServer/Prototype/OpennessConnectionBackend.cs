using System.Diagnostics;
using Siemens.Engineering;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class OpennessConnectionBackend : IConnectionBackend
{
    public IReadOnlyList<ProcessObservation> Discover()
    {
        var result = new List<ProcessObservation>();
        foreach (var process in TiaPortal.GetProcesses())
        {
            try { result.Add(Observe(process, StartTime(process.Id))); }
            catch (Exception ex)
            {
                result.Add(new ProcessObservation { ProcessId = process.Id, UnavailableReason = ex.Message });
            }
            // TiaPortalProcess.Dispose closes TIA; process descriptors are never disposed here.
        }
        return result;
    }

    public IProjectAttachment Attach(int processId)
    {
        var started = StartTime(processId);
        var process = TiaPortal.GetProcesses().SingleOrDefault(x => x.Id == processId)
            ?? throw new InvalidOperationException("The selected TIA process is no longer available.");
        var observed = Observe(process, started);
        if (!observed.CanAttach)
            throw new InvalidOperationException(observed.UnavailableReason);

        // Ownership is restricted to existing UI instances in this first prototype.
        // Return the handle immediately; the registry owns cleanup even if baseline validation fails.
        return new Attachment(process.Attach(), processId, started, startedByServer: false);
    }

    public IProjectAttachment OpenProject(string projectPath)
    {
        var full = DashboardHistory.Canonical(projectPath);
        if (full == null || !File.Exists(full))
            throw new InvalidOperationException("The project file was not found.");
        var info = new FileInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The project path is a link and was not opened.");
        TiaPortal? portal = null;
        var startedInstance = false;
        try
        {
            portal = new TiaPortal(TiaPortalMode.WithUserInterface);
            startedInstance = true;
            portal.Projects.Open(info);
            var process = portal.GetCurrentProcess();
            var started = StartTime(process.Id);
            var observed = Observe(process, started);
            if (!string.Equals(DashboardHistory.Canonical(observed.ProjectPath), full, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The opened project path does not match the requested path.");
            var attachment = new Attachment(portal, process.Id, started, startedByServer: true);
            portal = null;
            return attachment;
        }
        catch
        {
            if (portal != null)
            {
                if (startedInstance) CloseOwnedInstance(portal);
                else portal.Dispose();
            }
            throw;
        }
    }

    private static void CloseOwnedInstance(TiaPortal portal)
    {
        try { portal.GetCurrentProcess().Dispose(); }
        finally { try { portal.Dispose(); } catch (Exception) { } }
    }

    private static long StartTime(int processId)
    {
        using var process = Process.GetProcessById(processId); // System.Diagnostics, not Siemens.
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static ProcessObservation Observe(TiaPortalProcess process, long expectedStart)
    {
        if (StartTime(process.Id) != expectedStart)
            throw new InvalidOperationException("The selected process ID was reused.");
        var withUi = process.Mode == TiaPortalMode.WithUserInterface;
        return new ProcessObservation
        {
            ProcessId = process.Id,
            RuntimeStartUtcTicks = expectedStart,
            ProjectPath = process.ProjectPath?.FullName,
            Mode = withUi ? "with-ui" : "headless",
            CanAttach = withUi,
            UnavailableReason = withUi ? null : "Headless attachment is outside this first prototype."
        };
    }

    private sealed class Attachment : IProjectAttachment
    {
        private readonly TiaPortal _portal;
        private readonly int _processId;
        private readonly long _started;
        private readonly bool _startedByServer;

        public Attachment(TiaPortal portal, int processId, long started, bool startedByServer)
        {
            _portal = portal;
            _processId = processId;
            _started = started;
            _startedByServer = startedByServer;
        }

        public ProcessObservation ObserveProcess()
        {
            if (StartTime(_processId) != _started)
                throw new InvalidOperationException("The original process exited or was replaced.");
            return Observe(_portal.GetCurrentProcess(), _started);
        }

        public object? GetPrimaryProject()
        {
            if (_portal.Projects.Count > 1)
                throw new InvalidOperationException("Expected zero or one primary project.");
            return _portal.Projects.FirstOrDefault();
        }

        public string GetProjectPath(object project) => ((Project)project).Path.FullName;

        public bool SameProject(object retained, object current) => ((Project)retained).Equals((Project)current);

        public ProjectRead ReadProject(object retained)
        {
            var project = (Project)retained;
            var version = project.Version;
            return new ProjectRead
            {
                Name = project.Name,
                Path = project.Path.FullName,
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                TopLevelDeviceNames = project.Devices.Select(x => x.Name).ToArray()
            };
        }

        public ProcessStatus ReadStatus(object? retained, Action validate) =>
            OpennessDiscoveryReader.ReadStatus(_portal, retained, validate);

        public DeviceInventory ListDevices(object retained, Action validate)
        {
            var result = new DeviceInventory();
            new OpennessDiscoveryReader((Project)retained, result, _processId, validate).ListDevices(result);
            return result;
        }

        public DeviceRead ReadDevice(object retained, string objectId, bool includePath, Action validate)
        {
            var result = new DeviceRead();
            new OpennessDiscoveryReader((Project)retained, result, _processId, validate).ReadDevice(result, objectId, includePath);
            return result;
        }

        public BlockInventory ListBlocks(object retained, string plcObjectId, Action validate) =>
            OpennessBlockReader.Read((Project)retained, _processId, plcObjectId, validate);

        public BlockRead ReadBlock(object retained, BlockReadRequest request, Action validate) =>
            OpennessBlockDetailReader.Read((Project)retained, request, validate);

        public BlockInventory ListUdts(object retained, string plcObjectId, Action validate) =>
            OpennessUdtReader.Read((Project)retained, _processId, plcObjectId, validate);

        public BlockRead ReadUdt(object retained, BlockReadRequest request, Action validate) =>
            OpennessUdtDetailReader.Read((Project)retained, request, validate);

        public BlockInventory ListTagTables(object retained, string plcObjectId, Action validate) =>
            OpennessTagTableReader.Read((Project)retained, _processId, plcObjectId, validate);

        public TagTableRead ReadTagTable(object retained, TagTableReadRequest request, Action validate) =>
            OpennessTagTableDetailReader.Read((Project)retained, request, validate);

        public CrossReferenceRead ReadCrossReferences(object retained, CrossReferenceRequest request, Action validate) =>
            OpennessCrossReferenceReader.Read((Project)retained, request, validate);

        public bool? ProjectModified(object project) => ((Project)project).IsModified;

        public WriteProbeResult WriteProbe(object retained, WriteProbeRequest request, WriteProbeSession session, Action validate) =>
            OpennessWriteProbe.Run((Project)retained, request, session, validate);

        // Detach releases this bridge. It does not close a visible TIA window.
        public void Detach() => _portal.Dispose();

        // Closes only the TIA window this Open action started, and only when that open did not finish.
        public void CloseStartedInstance()
        {
            if (!_startedByServer)
                throw new InvalidOperationException("Refusing to close a TIA process this server did not start.");
            CloseOwnedInstance(_portal);
        }
    }
}
