namespace TiaOpennessMcpServer.Diagnostics;

// Each boundary starts a fresh scope. The request-owned object flows into async service
// methods so captured attribution remains visible to the caller after it awaits the result.
internal sealed class OperationCallContext : IDisposable
{
    private static readonly AsyncLocal<OperationCallContext?> _current = new();
    private readonly OperationCallContext? _previous;
    private bool _disposed;

    public static OperationCallContext? Current => _current.Value;
    public string? Origin { get; }
    public Guid? ConnectionId { get; set; }
    public string? ProjectPath { get; set; }

    private OperationCallContext(string? origin, OperationCallContext? previous)
    {
        Origin = origin;
        _previous = previous;
    }

    public static OperationCallContext Begin(string? origin = null)
    {
        var context = new OperationCallContext(origin, Current);
        _current.Value = context;
        return context;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Value = _previous;
    }
}
