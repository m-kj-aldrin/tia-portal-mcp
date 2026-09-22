using TiaOpennessMcpServer.Host;

internal static class OriginPolicyTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("origin policy: local browser names and clients without Origin remain supported", AcceptedOrigins);
        yield return ("origin policy: external and unconfigured origins remain rejected", RejectedOrigins);
        yield return ("origin policy: an origin must match the complete allowed authority", MalformedOrigins);
        yield return ("origin policy: allowed browser origins follow the configured listener port", ConfiguredPort);
    }

    private static void AcceptedOrigins()
    {
        var policy = new LoopbackOriginPolicy(new Uri("http://127.0.0.1:5000/"));
        foreach (var origin in new string?[] { null, "http://127.0.0.1:5000", "http://localhost:5000", "HTTP://LOCALHOST:5000" })
            Check(policy.Allows(origin), "Rejected the supported origin: " + (origin ?? "<absent>"));
    }

    private static void RejectedOrigins()
    {
        var policy = new LoopbackOriginPolicy(new Uri("http://127.0.0.1:5000/"));
        foreach (var origin in new[]
        {
            "", "null", "https://example.org", "http://example.org:5000",
            "https://localhost:5000", "http://localhost:5001", "http://127.0.0.1:5001",
            "http://localhost", "http://localhost.:5000", "http://localhost.example.org:5000",
            "http://example.localhost:5000", "http://127.0.0.1.example.org:5000",
            "http://127.0.0.2:5000", "http://127.1:5000", "http://2130706433:5000", "http://[::1]:5000"
        }) Check(!policy.Allows(origin), "Accepted an origin outside the explicit listener names: " + origin);
    }

    private static void MalformedOrigins()
    {
        var policy = new LoopbackOriginPolicy(new Uri("http://127.0.0.1:5000/"));
        foreach (var origin in new[]
        {
            "localhost:5000", "//localhost:5000", "http://localhost:5000/",
            "http://localhost:5000/path", "http://localhost:5000?query", "http://localhost:5000#fragment",
            "http://user@localhost:5000", "http://localhost:5000@example.org",
            " http://localhost:5000", "http://localhost:5000 ",
            "http://localhost:5000 http://127.0.0.1:5000", "http://localhost:5000,https://example.org"
        }) Check(!policy.Allows(origin), "Accepted a malformed or compound origin: " + origin);
    }

    private static void ConfiguredPort()
    {
        var policy = new LoopbackOriginPolicy(new Uri("http://127.0.0.1:5017/"));
        Check(policy.Allows("http://127.0.0.1:5017") && policy.Allows("http://localhost:5017"),
            "Configured listener port was not accepted for both supported names.");
        Check(!policy.Allows("http://127.0.0.1:5000") && !policy.Allows("http://localhost:5000"),
            "The default port remained allowed on a differently configured listener.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
