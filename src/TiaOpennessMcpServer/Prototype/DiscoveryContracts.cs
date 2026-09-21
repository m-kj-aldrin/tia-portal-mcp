using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Prototype;

internal sealed class DiscoveryError
{
    public string Origin { get; set; } = "tia-openness";
    public string Operation { get; set; } = "";
    public string? Path { get; set; }
    public string Message { get; set; } = "";
    public string? Format { get; set; }
}

internal class DiscoveryResult
{
    public DateTimeOffset ReadAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int ProcessId { get; set; }
    public bool Complete => Errors.Count == 0;
    public List<DiscoveryError> Errors { get; } = new();
}

internal sealed class ProcessDiscovery
{
    public DateTimeOffset ReadAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ProcessEntry> Processes { get; } = new();
    public List<DiscoveryError> Errors { get; } = new();
}

internal sealed class ProcessEntry
{
    public int ProcessId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Mode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PrimaryProjectPath { get; set; }
    public bool ConnectedByMcp { get; set; }
    // Prototype-only dashboard availability, not a derived device/process classification.
    public bool CanAttach { get; set; }
    public string? UnavailableReason { get; set; }
}

internal sealed class ProcessStatus : DiscoveryResult
{
    public string State { get; set; } = "disconnected";
    public string AccessProfile => "read-only";
    public bool WriteToolsAvailable => false;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Dictionary<string, object?>? Tia { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Dictionary<string, object?>? Project { get; set; }
}

internal sealed class DeviceInventory : DiscoveryResult
{
    public List<Dictionary<string, object?>> Roots { get; } = new();
}

internal sealed class DeviceRead : DiscoveryResult
{
    public Dictionary<string, object?> Metadata { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Dictionary<string, object?>>? DeviceItems { get; set; }
}

// Recorded evidence is deliberately scoped; it is neither a runtime test nor universal certification.
internal static class PrototypeEvidence
{
    public static object SamePathReopen => new
    {
        status = "user-reported-pass", testedOn = "2026-09-21", tiaVersion = "V20",
        scenario = "Same process and project path; background monitoring paused; old read rejected with reconnectRequired.",
        limitation = "One manual scenario. No individual Equals trace or general lifecycle guarantee.",
        reference = "docs/connection-prototype.md#manual-test-results--2026-09-21"
    };
}
