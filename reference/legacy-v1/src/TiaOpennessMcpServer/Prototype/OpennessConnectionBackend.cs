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
        return new Attachment(process.Attach(), processId, started);
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

        public Attachment(TiaPortal portal, int processId, long started)
        {
            _portal = portal;
            _processId = processId;
            _started = started;
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

        // Never Project.Close/Save or TiaPortalProcess.Dispose. Attach rejects headless instances.
        public void Detach() => _portal.Dispose();
    }
}
