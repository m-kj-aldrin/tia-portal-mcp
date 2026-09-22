namespace TiaOpennessMcpServer.Diagnostics;

internal sealed class OperationCallNote
{
    public string Origin { get; set; } = "mcp";
    public string Operation { get; set; } = "";
    public int? ProcessId { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? ProjectPath { get; set; }
    public double DurationMs { get; set; }
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
}
