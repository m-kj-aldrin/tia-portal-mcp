using TiaOpennessMcpServer.Operations;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Services;

// These interfaces contain no Siemens types so the actual guard can be exercised offline.
internal interface IConnectionBackend
{
    IReadOnlyList<ProcessObservation> Discover();
    IProjectAttachment Attach(int processId);
    IProjectAttachment OpenProject(string projectPath);
}

internal interface IProjectAttachment
{
    ProcessObservation ObserveProcess();
    object? GetPrimaryProject();
    string GetProjectPath(object project);
    bool SameProject(object retained, object current);
    ProcessStatus ReadStatus(object? retained, Action validate);
    DeviceInventory ListDevices(object retained, Action validate);
    DeviceRead ReadDevice(object retained, string objectId, bool includePath, Action validate);
    BlockInventory ListBlocks(object retained, string plcObjectId, Action validate);
    BlockRead ReadBlock(object retained, BlockReadRequest request, Action validate);
    BlockInventory ListUdts(object retained, string plcObjectId, Action validate);
    BlockRead ReadUdt(object retained, BlockReadRequest request, Action validate);
    BlockInventory ListTagTables(object retained, string plcObjectId, Action validate);
    TagTableRead ReadTagTable(object retained, TagTableReadRequest request, Action validate);
    CrossReferenceRead ReadCrossReferences(object retained, CrossReferenceRequest request, Action validate);
    TagTableExportResult ExportTagTable(object retained, ExportTagTableRequest request, Action validate);
    CompileResult Compile(object retained, CompileRequest request, Action validate);
    WriteResult Write(object retained, WriteRequest request, Action validate);
    void Detach();
    void CloseStartedInstance();
}

internal sealed class ProcessObservation
{
    public int ProcessId { get; set; }
    public long RuntimeStartUtcTicks { get; set; }
    public string? ProjectPath { get; set; }
    public string? Mode { get; set; }
    public bool CanAttach { get; set; }
    public string? UnavailableReason { get; set; }
}

internal sealed class ConnectionView
{
    public int ProcessId { get; set; }
    public Guid ConnectionId { get; set; }
    [JsonIgnore]
    public long RuntimeStartUtcTicks { get; set; }
    public string? ApprovedProjectPath { get; set; }
    public string State { get; set; } = "disconnected";
    public string? Reason { get; set; }
    public string? CleanupError { get; set; }
}

internal sealed class ConnectionEvent
{
    public long Sequence { get; set; }
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int ProcessId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Action { get; set; } = "";
    public string Message { get; set; } = "";
}

internal sealed class RequestTicket
{
    public int ProcessId { get; }
    public Guid ConnectionId { get; }
    public RequestTicket(int processId, Guid connectionId)
    {
        ProcessId = processId;
        ConnectionId = connectionId;
    }
}
