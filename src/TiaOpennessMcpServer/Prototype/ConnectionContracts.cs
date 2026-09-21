using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Prototype;

// These interfaces contain no Siemens types so the actual guard can be exercised offline.
internal interface IConnectionBackend
{
    IReadOnlyList<ProcessObservation> Discover();
    IProjectAttachment Attach(int processId);
}

internal interface IProjectAttachment
{
    ProcessObservation ObserveProcess();
    object? GetPrimaryProject();
    string GetProjectPath(object project);
    bool SameProject(object retained, object current);
    ProjectRead ReadProject(object retained);
    void Detach();
}

internal sealed class ProcessObservation
{
    public int ProcessId { get; set; }
    public long RuntimeStartUtcTicks { get; set; }
    public string? ProjectPath { get; set; }
    public bool CanAttach { get; set; }
    public string? UnavailableReason { get; set; }
}

internal sealed class ProjectRead
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Version { get; set; }
    public string[] TopLevelDeviceNames { get; set; } = Array.Empty<string>();
}

internal sealed class ConnectionView
{
    public int ProcessId { get; set; }
    public Guid ConnectionId { get; set; }
    public string? ApprovedProjectPath { get; set; }
    public string State { get; set; } = "disconnected";
    public string? Reason { get; set; }
    public string? CleanupError { get; set; }
}

internal sealed class ConnectionEvent
{
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

internal sealed class GuardedRead
{
    public int ProcessId { get; set; }
    public Guid ConnectionId { get; set; }
    public ProjectRead Project { get; set; } = new();
    public DateTimeOffset CheckedAtUtc { get; set; }
    public double BeforeCheckMs { get; set; }
    public double ReadMs { get; set; }
    public double AfterCheckMs { get; set; }
}

internal sealed class ConnectionFault : Exception
{
    public string Code { get; }
    public int ProcessId { get; }
    public bool ReconnectRequired => Code == "reconnectRequired";
    public ConnectionFault(string code, int processId, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        ProcessId = processId;
    }
}
