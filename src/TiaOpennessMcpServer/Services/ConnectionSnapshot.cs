namespace TiaOpennessMcpServer.Services;

/// <summary>Managed connection state for observers; never contains native engineering objects.</summary>
internal sealed class ConnectionSnapshot
{
    public IReadOnlyList<ProcessObservation> Observations { get; }
    public IReadOnlyList<ConnectionView> Connections { get; }

    public ConnectionSnapshot(IReadOnlyList<ProcessObservation> observations, IReadOnlyList<ConnectionView> connections)
    {
        Observations = observations;
        Connections = connections;
    }
}
