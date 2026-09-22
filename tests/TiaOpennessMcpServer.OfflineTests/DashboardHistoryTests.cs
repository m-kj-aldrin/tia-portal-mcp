using TiaOpennessMcpServer.Prototype;

internal static class DashboardHistoryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("dashboard: projectless runtime keeps its tab when a project appears", ProjectlessKeepsTimeline);
        yield return ("dashboard: closing a project keeps a temporary process tab and rejoins that project", ProjectGapRejoins);
        yield return ("dashboard: a projectless gap disappears when the process exits", ProjectGapExit);
        yield return ("dashboard: a process that never had a project remains after it exits", ProjectlessExit);
        yield return ("dashboard: path change archives A and does not connect B", PathTransition);
        yield return ("dashboard: exact path reappearance updates runtime without connecting", Reappearance);
        yield return ("dashboard: simultaneous same-path processes stay independent", TwoProcesses);
        yield return ("dashboard: reused process ID does not inherit the old connection", ReusedPid);
        yield return ("dashboard: late call stays on the captured connection", LateAttribution);
        yield return ("dashboard: failed admission and partial results stay inspectable", Failures);
        yield return ("dashboard: log cursor resets when retained entries are dropped", Retention);
        yield return ("dashboard: dismissing history does not require a live runtime", Dismiss);
        yield return ("dashboard: tool forms come from the published schemas", Forms);
    }

    private static void ProjectlessKeepsTimeline()
    {
        var history = new DashboardHistory();
        var connection = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, null) }, new[] { View(10, 100, connection, null, "connected") });
        var tab = Tia(history).Single();
        history.Record(new DashboardLogDraft { Origin = "dashboard", Operation = "connect", ProcessId = 10, ConnectionId = connection, Outcome = "success" });
        history.Apply(new[] { Process(10, 100, @"C:\Projects\B.ap20") }, new[] { View(10, 100, connection, null, "invalidated") });
        var updated = Tia(history).Single();
        Check(updated.Id == tab.Id && updated.ProjectPath != null && updated.ProjectPath.EndsWith("B.ap20", StringComparison.OrdinalIgnoreCase), "Projectless timeline was split.");
        Check(updated.ConnectionState == "invalidated" && updated.ConnectionId == null && updated.Live, "The new project was connected automatically.");
        Check(updated.Previous.Any(item => item.ConnectionId == connection), "The earlier connection was discarded.");
        Check(history.ReadLogs(0, 0).Entries.Any(entry => entry.Operation == "connect" && entry.TabId == tab.Id), "The original log left the tab.");
        history.Apply(new[] { Process(10, 100, @"C:\Projects\B.ap20") }, new[] { View(10, 100, connection, null, "invalidated") });
        Check(Tia(history).Single().Previous.Count(item => item.ConnectionId == connection) == 1, "Repeating the snapshot duplicated history.");
    }

    private static void ProjectGapRejoins()
    {
        var history = new DashboardHistory();
        var connection = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, connection, @"C:\Projects\A.ap20", "connected") });
        var projectId = Tia(history).Single().Id;
        history.Record(new DashboardLogDraft { Origin = "mcp", Operation = "list_devices", ProcessId = 10, ConnectionId = connection, ProjectPath = @"C:\Projects\A.ap20", Outcome = "success" });
        history.Apply(new[] { Process(10, 100, null) }, new[] { View(10, 100, connection, @"C:\Projects\A.ap20", "invalidated") });
        var during = Tia(history);
        var archived = during.Single(tab => tab.Id == projectId);
        var process = during.Single(tab => tab.Id != projectId);
        Check(during.Count == 2 && archived.ProjectState == "historical" && archived.Live == false, "Closing the project removed its history.");
        Check(process.Live && process.ProjectPath == null && process.ProcessId == 10 && process.ConnectionState == "invalidated", "The still-open process did not keep its own tab.");
        history.Record(new DashboardLogDraft { Origin = "dashboard", Operation = "get_status", ProcessId = 10, Outcome = "success" });
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, connection, @"C:\Projects\A.ap20", "invalidated") });
        var tab = Tia(history).Single();
        Check(tab.Id == projectId && tab.Live && tab.ProjectPath!.EndsWith("A.ap20", StringComparison.OrdinalIgnoreCase), "Reopening the project created another tab.");
        Check(tab.ConnectionState == "invalidated", "Rejoining the project connected it.");
        var entries = history.ReadLogs(0, 0).Entries;
        Check(entries.Single(entry => entry.Operation == "list_devices").TabId == projectId && entries.Single(entry => entry.Operation == "get_status").TabId == projectId, "The project and gap logs were not merged.");

        var other = new DashboardHistory();
        other.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, Array.Empty<ConnectionView>());
        var archivedId = Tia(other).Single().Id;
        other.Apply(new[] { Process(10, 100, null) }, Array.Empty<ConnectionView>());
        other.Apply(new[] { Process(10, 100, @"C:\Projects\B.ap20") }, Array.Empty<ConnectionView>());
        var opened = Tia(other);
        Check(opened.Count == 2 && opened.Single(tab => tab.Id == archivedId).ProjectState == "historical" && opened.Single(tab => tab.Live).ProjectPath!.EndsWith("B.ap20", StringComparison.OrdinalIgnoreCase), "Opening a different project merged it into the archived one.");

        var both = new DashboardHistory();
        both.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20"), Process(20, 200, null) }, Array.Empty<ConnectionView>());
        both.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20"), Process(20, 200, @"C:\Projects\A.ap20") }, Array.Empty<ConnectionView>());
        var live = Tia(both);
        Check(live.Count == 2 && live.All(tab => tab.Live && tab.ProjectPath!.EndsWith("A.ap20", StringComparison.OrdinalIgnoreCase)), "A second open copy of the project was merged into the first.");
    }

    private static void ProjectGapExit()
    {
        var history = new DashboardHistory();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, Array.Empty<ConnectionView>());
        var projectId = Tia(history).Single().Id;
        history.Apply(new[] { Process(10, 100, null) }, Array.Empty<ConnectionView>());
        Check(Tia(history).Count == 2, "The shutdown gap did not keep the project and the process apart.");
        history.Record(new DashboardLogDraft { Origin = "dashboard", Operation = "get_status", ProcessId = 10, Outcome = "success" });
        history.Apply(Array.Empty<ProcessObservation>(), Array.Empty<ConnectionView>());
        var tab = Tia(history).Single();
        Check(tab.Id == projectId && tab.Live == false && tab.ProjectState == "historical" && tab.ProjectPath!.EndsWith("A.ap20", StringComparison.OrdinalIgnoreCase), "Process exit left a closed projectless tab.");
        Check(history.ReadLogs(0, 0).Entries.Single(entry => entry.Operation == "get_status").TabId == projectId, "The gap log was dropped with the process tab.");
        history.Apply(new[] { Process(40, 400, @"C:\Projects\A.ap20") }, Array.Empty<ConnectionView>());
        Check(Tia(history).Single().Id == projectId && Tia(history).Single().Live && Tia(history).Single().ConnectionState == "disconnected", "The reopened project did not reuse its history.");
    }

    private static void ProjectlessExit()
    {
        var history = new DashboardHistory();
        history.Apply(new[] { Process(10, 100, null) }, Array.Empty<ConnectionView>());
        var id = Tia(history).Single().Id;
        history.Record(new DashboardLogDraft { Origin = "dashboard", Operation = "get_status", ProcessId = 10, Outcome = "success" });
        history.Apply(Array.Empty<ProcessObservation>(), Array.Empty<ConnectionView>());
        var tab = Tia(history).Single();
        Check(tab.Id == id && tab.Live == false && tab.ProjectPath == null && tab.ProjectState == "none", "A process that never opened a project was removed.");
        Check(history.ReadLogs(0, 0).Entries.Single(entry => entry.Operation == "get_status").TabId == id, "The projectless log was discarded.");
    }

    private static void PathTransition()
    {
        var history = new DashboardHistory();
        var connection = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, connection, @"C:\Projects\A.ap20", "connected") });
        var original = Tia(history).Single().Id;
        history.Record(new DashboardLogDraft { Origin = "mcp", Operation = "list_devices", ProcessId = 10, ConnectionId = connection, ProjectPath = @"C:\Projects\A.ap20", Outcome = "success" });
        history.Apply(new[] { Process(10, 100, @"C:/Projects/B.ap20") }, new[] { View(10, 100, connection, @"C:\Projects\A.ap20", "invalidated") });
        var tabs = Tia(history);
        var archived = tabs.Single(tab => tab.Id == original);
        var current = tabs.Single(tab => tab.Id != original);
        Check(archived.ProjectState == "historical" && archived.Live == false && archived.ProjectPath!.EndsWith("A.ap20", StringComparison.OrdinalIgnoreCase), "Project A did not become historical.");
        Check(current.Live && current.ProjectPath!.EndsWith("B.ap20", StringComparison.OrdinalIgnoreCase) && current.ConnectionState != "connected" && current.ConnectionId == null, "Project B inherited A's connection.");
        Check(history.ReadLogs(0, 0).Entries.Single(entry => entry.Operation == "list_devices").TabId == original, "The in-flight project log moved to B.");
    }

    private static void Reappearance()
    {
        var history = new DashboardHistory();
        var oldConnection = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, oldConnection, @"C:\Projects\A.ap20", "connected") });
        var id = Tia(history).Single().Id;
        history.Apply(Array.Empty<ProcessObservation>(), Array.Empty<ConnectionView>());
        Check(Tia(history).Single().ProjectState == "historical" && Tia(history).Single().Live == false, "Closing the process dropped the tab.");
        history.Apply(new[] { Process(40, 400, @"c:\projects\a.ap20") }, Array.Empty<ConnectionView>());
        var tab = Tia(history).Single();
        Check(tab.Id == id && tab.Live && tab.ProcessId == 40 && tab.ConnectionState == "disconnected" && tab.ConnectionId == null, "Exact-path reappearance reconnected or created another tab.");
        Check(tab.Previous.Any(item => item.ConnectionId == oldConnection), "The earlier runtime was not retained.");
    }

    private static void TwoProcesses()
    {
        var history = new DashboardHistory();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\Same.ap20"), Process(20, 200, @"C:\Projects\Same.ap20") }, new[]
        {
            View(10, 100, a, @"C:\Projects\Same.ap20", "connected"),
            View(20, 200, b, @"C:\Projects\Same.ap20", "disconnected")
        });
        var tabs = Tia(history);
        Check(tabs.Count == 2, "Same paths were merged.");
        var first = tabs.Single(tab => tab.ProcessId == 10);
        var second = tabs.Single(tab => tab.ProcessId == 20);
        Check(first.ConnectionState == "connected" && first.ConnectionId == a, "The connected process lost its own connection.");
        Check(second.ConnectionState == "disconnected" && second.ConnectionId == b && second.Id != first.Id, "The other process became connected because the path matched.");
    }

    private static void ReusedPid()
    {
        var history = new DashboardHistory();
        var oldConnection = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, oldConnection, @"C:\Projects\A.ap20", "connected") });
        history.Apply(new[] { Process(10, 200, @"C:\Projects\A.ap20") }, new[] { View(10, 100, oldConnection, @"C:\Projects\A.ap20", "invalidated") });
        var tab = Tia(history).Single();
        Check(tab.Live && tab.ProcessId == 10 && tab.RuntimeIdentity == "200" && tab.ConnectionState == "disconnected" && tab.ConnectionId == null, "The reused PID kept the old connection.");
        Check(tab.Previous.Any(item => item.ConnectionId == oldConnection && item.RuntimeStartUtcTicks == 100), "The old runtime identity was dropped.");
    }

    private static void LateAttribution()
    {
        var history = new DashboardHistory();
        var oldConnection = Guid.NewGuid();
        var newer = Guid.NewGuid();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, oldConnection, @"C:\Projects\A.ap20", "connected") });
        var original = Tia(history).Single().Id;
        history.Apply(new[] { Process(10, 100, @"C:\Projects\B.ap20") }, new[] { View(10, 100, oldConnection, @"C:\Projects\A.ap20", "invalidated") });
        history.Apply(new[] { Process(10, 100, @"C:\Projects\B.ap20") }, new[] { View(10, 100, newer, @"C:\Projects\B.ap20", "connected") });
        history.Record(new DashboardLogDraft { Origin = "mcp", Operation = "get_block", ProcessId = 10, ConnectionId = oldConnection, ProjectPath = @"C:\Projects\A.ap20", Outcome = "error", Error = "reconnect" });
        var entry = history.ReadLogs(0, 0).Entries.Single(item => item.Operation == "get_block");
        Check(entry.TabId == original && entry.ConnectionId == oldConnection && entry.Outcome == "error", "The late response followed the tab that was current at completion.");
        Check(Tia(history).Single(tab => tab.Live).ConnectionId == newer && Tia(history).Single(tab => tab.Live).Id != original, "The new connection was not kept on B.");
    }

    private static void Failures()
    {
        var history = new DashboardHistory();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, Array.Empty<ConnectionView>());
        var tab = Tia(history).Single();
        history.Record(new DashboardLogDraft { Origin = "mcp", Operation = "list_devices", ProcessId = 10, Outcome = "error", Error = "Connect this process in the prototype dashboard first." });
        history.Record(new DashboardLogDraft { Origin = "mcp", Operation = "get_block", ProcessId = 10, Outcome = "partial", Error = "tia-openness: export failed" });
        history.ImportDiagnostic(new ConnectionEvent { Action = "read", ProcessId = 10, Message = "Read completed; both context checks passed." });
        history.ImportDiagnostic(new ConnectionEvent { Action = "sourceExport", ProcessId = 10, ConnectionId = Guid.NewGuid(), Message = "external-source: failed — bridge: disk" });
        var entries = history.ReadLogs(0, 0).Entries;
        Check(entries.Single(entry => entry.Operation == "list_devices").TabId == tab.Id && entries.Single(entry => entry.Operation == "list_devices").Error!.Contains("Connect"), "Failed admission missed the process tab.");
        Check(entries.Single(entry => entry.Operation == "get_block").Outcome == "partial" && entries.Single(entry => entry.Operation == "get_block").Error!.Contains("export failed"), "Partial outcome was collapsed.");
        Check(entries.Count(entry => entry.Operation == "sourceExport") == 1 && entries.All(entry => entry.Operation != "read"), "Diagnostics were duplicated or a completed read was imported.");
        Check(entries.Any(entry => entry.Operation == "startup" && entry.TabId == "server"), "Server startup was not retained.");
    }

    private static void Retention()
    {
        var history = new DashboardHistory();
        for (var i = 0; i < DashboardHistory.MaxLogEntries + 5; i++)
            history.Record(new DashboardLogDraft { Origin = "server", Operation = "tick", Outcome = "success" });
        var page = history.ReadLogs(0, 0);
        Check(page.Entries.Count == DashboardHistory.MaxLogEntries && page.Truncated && page.Oldest > 1, "The log bound was not applied.");
        var gap = history.ReadLogs(page.Oldest - 2, page.Generation);
        Check(gap.Reset && gap.Entries.Count == DashboardHistory.MaxLogEntries, "A dropped cursor did not reset.");
        var next = history.ReadLogs(page.Next, page.Generation);
        Check(!next.Reset && next.Entries.Count == 0, "A current cursor replayed old entries.");
        for (var i = 0; i < DashboardHistory.MaxHistoricalTabs + 1; i++)
        {
            history.Apply(new[] { Process(1000 + i, 1, @"C:\Projects\P" + i + ".ap20") }, Array.Empty<ConnectionView>());
            history.Apply(Array.Empty<ProcessObservation>(), Array.Empty<ConnectionView>());
        }
        var snapshot = history.Snapshot();
        Check(snapshot.Tabs.Count(tab => tab.Kind == "tia") == DashboardHistory.MaxHistoricalTabs && snapshot.TabsTruncated, "Historical tabs were not bounded.");
        Check(snapshot.Tabs.Any(tab => tab.Id == "server"), "The Server tab was evicted.");
        var reset = history.ReadLogs(page.Next, page.Generation);
        Check(reset.Reset, "Dropping a historical tab left a hole without a cursor reset.");
    }

    private static void Dismiss()
    {
        var history = new DashboardHistory();
        history.Apply(new[] { Process(10, 100, @"C:\Projects\A.ap20") }, new[] { View(10, 100, Guid.NewGuid(), @"C:\Projects\A.ap20", "connected") });
        var live = Tia(history).Single();
        history.Record(new DashboardLogDraft { Origin = "mcp", Operation = "get_status", ProcessId = 10, ConnectionId = live.ConnectionId, Outcome = "success" });
        Check(!history.Dismiss(live.Id) && !history.Dismiss("server") && !history.Dismiss("missing"), "A live or server tab was dismissed.");
        history.Apply(Array.Empty<ProcessObservation>(), Array.Empty<ConnectionView>());
        var historical = Tia(history).Single();
        Check(historical.Live == false && history.Dismiss(historical.Id), "Historical history could not be dismissed.");
        Check(Tia(history).Count == 0, "Dismiss left the project tab.");
        Check(history.ReadLogs(0, 0).Entries.All(entry => entry.TabId != historical.Id), "Dismiss kept the project log.");
        Check(history.ReadLogs(0, 0).Entries.Any(entry => entry.Operation == "dismiss" && entry.TabId == "server"), "Dismiss was not recorded on the Server tab.");
    }

    private static void Forms()
    {
        var html = DashboardToolForms.Render(McpBoundary.ToolDefs());
        var names = new[] { "list_tia_processes", "get_status", "list_devices", "get_device", "list_blocks", "get_block", "list_udts", "get_udt", "list_tag_tables", "get_tag_table", "get_cross_references" };
        Check(names.All(name => html.Contains("data-tool=\"" + name + "\"", StringComparison.Ordinal)) && !html.Contains("connect_to_tia_portal", StringComparison.Ordinal), "Forms did not follow the eleven-tool list.");
        foreach (var tool in McpBoundary.ToolDefs())
            foreach (var property in tool.InputSchema.Properties.Keys)
                Check(html.Contains("name=\"" + property + "\"", StringComparison.Ordinal), "Schema property was omitted: " + property);
        Check(html.Contains("data-enabled-when=\"includeSource=true,sourceFormat=external-source\"", StringComparison.Ordinal), "Dependency constraint was handwritten away from the schema.");
        Check(html.Contains(">Read block</button>", StringComparison.Ordinal) && html.Contains(">Read cross-references</button>", StringComparison.Ordinal), "Tool actions were not rendered.");
        Check(!html.Contains("<script", StringComparison.OrdinalIgnoreCase), "Tool form HTML contained a script.");
    }

    private static ProcessObservation Process(int processId, long runtime, string? path) => new()
    {
        ProcessId = processId, RuntimeStartUtcTicks = runtime, ProjectPath = path, Mode = "with-ui", CanAttach = true
    };

    private static ConnectionView View(int processId, long runtime, Guid connection, string? path, string state) => new()
    {
        ProcessId = processId, RuntimeStartUtcTicks = runtime, ConnectionId = connection, ApprovedProjectPath = path, State = state
    };

    private static List<DashboardTabView> Tia(DashboardHistory history) =>
        history.Snapshot().Tabs.Where(tab => tab.Kind == "tia").ToList();

    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
}
