namespace TiaOpennessMcpServer.Operations;

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
