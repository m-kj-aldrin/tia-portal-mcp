namespace TiaOpennessMcpServer.Utilities;

public static class V1ProjectVersionPolicy
{
    public static string? Normalize(string? nativeVersion) =>
        string.IsNullOrWhiteSpace(nativeVersion) ? null : nativeVersion;
}
