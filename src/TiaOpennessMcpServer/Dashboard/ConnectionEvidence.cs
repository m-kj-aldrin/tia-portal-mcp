namespace TiaOpennessMcpServer.Dashboard;

// Recorded evidence is deliberately scoped; it is neither a runtime test nor universal certification.
internal static class ConnectionEvidence
{
    public static object SamePathReopen => new
    {
        status = "user-reported-pass", testedOn = "2026-09-21", tiaVersion = "V20",
        scenario = "Same process and project path; background monitoring paused; old read rejected with reconnectRequired.",
        limitation = "One manual scenario. No individual Equals trace or general lifecycle guarantee.",
        reference = "reference/history/connection-prototype.md#manual-test-results--2026-09-21"
    };
}
