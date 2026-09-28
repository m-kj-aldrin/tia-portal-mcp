using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Dashboard;
using TiaOpennessMcpServer.Diagnostics;
using TiaOpennessMcpServer.Mcp;
using TiaOpennessMcpServer.Operations;
using TiaOpennessMcpServer.Services;
using TiaOpennessMcpServer.Utilities;

internal static class DashboardToolRunnerTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("dashboard tool action: one in-process MCP dispatch and one dashboard journal note", () => Dispatch().GetAwaiter().GetResult());
        yield return ("dashboard tool action: tab admission and duplicate IDs cannot dispatch", () => Admission().GetAwaiter().GetResult());
        yield return ("dashboard tool action: strict envelope, active limit and read-only publication", () => StrictAdmission().GetAwaiter().GetResult());
        yield return ("dashboard form action: schema values convert without inventing defaults or process IDs", () => FormFields().GetAwaiter().GetResult());
        yield return ("dashboard form action: malformed fields and unpublished tools never enter run history", () => StrictFormFields().GetAwaiter().GetResult());
        yield return ("dashboard form action: context stamps reject stale same-tab reconnects and project replacement", () => FormContextStamps().GetAwaiter().GetResult());
        yield return ("dashboard tool action: admission attachment fence survives reconnect and readback override", () => ConnectionFence().GetAwaiter().GetResult());
        yield return ("dashboard selectors: native choices exclude system entries and stale contexts", () => Selectors().GetAwaiter().GetResult());
        yield return ("dashboard source helper: exact documents patch only the retained context", () => SourceSignals().GetAwaiter().GetResult());
        yield return ("dashboard tool action: admitted call completes without a response writer", () => DetachedCompletion().GetAwaiter().GetResult());
        yield return ("dashboard run store: bounded whole captures and oversized metadata", Retention);
        yield return ("dashboard run fragments: escape source text and expose exact response", Escaping);
        yield return ("dashboard run fragments: indent JSON result without changing captured response", IndentedResult);
        yield return ("dashboard result classification: partial write and compiler errors are distinct", Classification);
    }

    private static async Task Dispatch()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "list_devices",
            arguments = new { processId = 10 }
        }));
        var started = scope.Runner.Begin(document.RootElement);
        Check(started.Outcome == "running" && scope.Runs.Snapshot().Single().Id == started.Id,
            "Run did not enter server history before dispatch.");
        var finished = await scope.Runner.RunAsync(started);
        Check(scope.Backend.Processes[10].Reads == 1, "Tool action did not dispatch exactly once through the service.");
        Check(scope.Notes.Count == 1 && scope.Notes[0].Origin == "dashboard" && scope.Notes[0].Operation == "list_devices",
            "MCP boundary did not journal one dashboard-origin call.");
        Check(finished.Capture.Outcome == "success" && finished.Capture.Completed &&
            finished.ResponseJson.Contains("\"isError\":false", StringComparison.Ordinal),
            "Completed MCP response was not captured intact.");
    }

    private static async Task Admission()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        var id = Guid.NewGuid().ToString("D");
        using var mismatched = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = id, name = "list_devices", arguments = new { processId = 20 } }));
        Check(Fault(() => scope.Runner.Begin(mismatched.RootElement)) && scope.Backend.Processes[10].Reads == 0,
            "Mismatched live tab reached engineering dispatch.");
        using var admitted = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = id, name = "get_status", arguments = new { processId = 10 } }));
        var started = scope.Runner.Begin(admitted.RootElement);
        Check(Fault(() => scope.Runner.Begin(admitted.RootElement)), "Duplicate requestId was admitted.");
        await scope.Runner.RunAsync(started);
        Check(scope.Backend.Processes[10].Reads == 1, "Duplicate action changed native read count.");
    }

    private static async Task FormFields()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var snapshot = scope.Dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.Single(item => item.Live && item.ProcessId == 10);

        var passive = BeginForm(scope, "server", "get_status", new { });
        using (var request = JsonDocument.Parse(passive.RequestJson!))
            Check(!request.RootElement.GetProperty("arguments").EnumerateObject().Any(),
                "Server status form invented a process selector.");
        scope.Runs.Finish(passive.Id, "success", "{}", null);

        var block = BeginForm(scope, tab.Id, "get_block", new
        {
            objectId = "native:block:1", includePath = "false", includeSource = "true", sourceFormat = ""
        });
        using (var request = JsonDocument.Parse(block.RequestJson!))
        {
            var args = request.RootElement.GetProperty("arguments");
            Check(args.GetProperty("processId").GetInt32() == 10 &&
                args.GetProperty("objectId").GetString() == "native:block:1" &&
                args.GetProperty("includePath").ValueKind == JsonValueKind.False &&
                args.GetProperty("includeSource").ValueKind == JsonValueKind.True &&
                !args.TryGetProperty("sourceFormat", out _) && !args.TryGetProperty("includeDependencies", out _),
                "Form action changed opaque fields, Boolean values or omitted MCP defaults.");
        }
        scope.Runs.Finish(block.Id, "success", "{}", null);

        var source = "<Document name=\"B\">\r\nDATA</Document>";
        var documents = JsonSerializer.Serialize(new[] { new { name = "B.xml", content = source } });
        var write = BeginForm(scope, tab.Id, "write_blocks", new
        {
            plcObjectId = "CPU:1", sourceFormat = "simatic-ml", groupPath = "", documents
        });
        using (var request = JsonDocument.Parse(write.RequestJson!))
        {
            var args = request.RootElement.GetProperty("arguments");
            Check(args.GetProperty("processId").GetInt32() == 10 &&
                args.GetProperty("documents")[0].GetProperty("content").GetString() == source &&
                !args.TryGetProperty("groupPath", out _),
                "Complete source document contents or omitted destination changed during form conversion.");
        }
        scope.Runs.Finish(write.Id, "success", "{}", null);

        var attribute = BeginForm(scope, tab.Id, "set_tag_entry_attribute", new
        {
            objectId = "entry:1", attributeName = "ExternalVisible", attributeValue = "false"
        });
        using (var request = JsonDocument.Parse(attribute.RequestJson!))
            Check(request.RootElement.GetProperty("arguments").GetProperty("attributeValue").ValueKind == JsonValueKind.False,
                "JSON-typed attribute value was coerced to text.");
        scope.Runs.Finish(attribute.Id, "success", "{}", null);

        var literal = BeginForm(scope, tab.Id, "set_tag_entry_attribute", new
        {
            objectId = "entry:1", attributeName = "Value", attributeValue = "\"100\""
        });
        using (var request = JsonDocument.Parse(literal.RequestJson!))
            Check(request.RootElement.GetProperty("arguments").GetProperty("attributeValue").GetString() == "100",
                "Quoted native literal lost its JSON string type.");
        scope.Runs.Finish(literal.Id, "success", "{}", null);
        Check(scope.Backend.Processes[10].Reads == 0 && scope.Backend.Processes[10].Compiles == 0,
            "Form transport conversion dispatched a native operation.");
    }

    private static async Task StrictFormFields()
    {
        using var scope = new Scope(writesEnabled: false);
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.Live && item.ProcessId == 10);
        Check(Fault(() => BeginForm(scope, tab.Id, "get_block", new { processId = 20, objectId = "B" }, false)),
            "A form overrode the workspace process selector.");
        Check(Fault(() => BeginForm(scope, tab.Id, "get_block", new { objectId = "B", includePath = "perhaps" }, false)),
            "Malformed Boolean text entered a tool request.");
        Check(Fault(() => BeginForm(scope, tab.Id, "get_block", new { objectId = "B", objectName = "foreign" }, false)),
            "Unknown form field entered a tool request.");
        Check(Fault(() => BeginForm(scope, tab.Id, "write_blocks", new { plcObjectId = "CPU" }, false)),
            "Read-only profile admitted a hidden write form.");
        Check(Fault(() => BeginForm(scope, tab.Id, "get_block", new { objectId = "B" }, false, "write_udts")),
            "Source helper could target the wrong write editor.");
        Check(Fault(() => BeginForm(scope, "missing-tab", "get_status", new { }, false)),
            "Form action accepted an absent workspace.");

        var raw = "{\"tabId\":" + JsonSerializer.Serialize(tab.Id) +
            ",\"contextStamp\":" + JsonSerializer.Serialize(DashboardSelectorStore.SignalPrefix(scope.Dashboard.CurrentDashboard(), tab)) +
            ",\"requestId\":" + JsonSerializer.Serialize(Guid.NewGuid().ToString("D")) +
            ",\"name\":\"get_block\",\"fields\":{\"objectId\":\"A\",\"objectId\":\"B\"}}";
        using var duplicated = JsonDocument.Parse(raw);
        Check(Fault(() => scope.Runner.BeginFields(duplicated.RootElement, false)),
            "Duplicate form field was silently accepted.");
        Check(scope.Runs.Snapshot().Count == 0 && scope.Backend.Processes[10].Reads == 0,
            "Rejected forms recorded a run or queried TIA.");
    }

    private static async Task FormContextStamps()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var initial = scope.Dashboard.CurrentDashboard();
        var tab = initial.Tabs.Single(item => item.Live && item.ProcessId == 10);
        var firstStamp = DashboardSelectorStore.SignalPrefix(initial, tab);
        var read = BeginForm(scope, tab.Id, "list_devices", new { }, contextStamp: firstStamp);
        Check(read.ExpectedConnectionId == tab.ConnectionId && read.ExpectedConnectionId != null,
            "Form admission did not retain the selected attachment ID.");
        scope.Runs.Finish(read.Id, "success", "{}", null);

        await scope.Engineering.DisconnectAsync(10);
        await scope.Engineering.ConnectAsync(10);
        var reconnected = scope.Dashboard.CurrentDashboard();
        var current = reconnected.Tabs.Single(item => item.Live && item.ProcessId == 10);
        Check(current.Id == tab.Id && current.ConnectionId != tab.ConnectionId,
            "Reconnect fixture did not retain the tab with a new attachment.");
        var count = scope.Runs.Snapshot().Count;
        Check(Fault(() => BeginForm(scope, tab.Id, "list_devices", new { }, contextStamp: firstStamp)) &&
            scope.Runs.Snapshot().Count == count && scope.Backend.Processes[10].Reads == 0,
            "A stale same-tab form entered history or reached TIA after reconnect.");
        var secondStamp = DashboardSelectorStore.SignalPrefix(reconnected, current);
        var accepted = BeginForm(scope, tab.Id, "list_devices", new { }, contextStamp: secondStamp);
        Check(accepted.ExpectedConnectionId == current.ConnectionId,
            "Current form did not capture the replacement attachment.");
        scope.Runs.Finish(accepted.Id, "success", "{}", null);

        var process = scope.Backend.Processes[10];
        process.Project = new FakeProject(process.Project!.Path);
        try { await scope.Engineering.ReadStatusAsync(10); }
        catch (ConnectionFault) { }
        var replaced = scope.Dashboard.CurrentDashboard();
        var replacementTab = replaced.Tabs.Single(item => item.Id == tab.Id);
        Check(DashboardSelectorStore.SignalPrefix(replaced, replacementTab) != secondStamp,
            "Same-path native project replacement did not change the form context stamp.");
        count = scope.Runs.Snapshot().Count;
        Check(Fault(() => BeginForm(scope, tab.Id, "list_devices", new { }, contextStamp: secondStamp)) &&
            scope.Runs.Snapshot().Count == count,
            "A stale form survived same-path project replacement.");

        var server = replaced.Tabs.Single(item => item.Id == "server");
        var serverStamp = DashboardSelectorStore.SignalPrefix(replaced, server);
        Check(Fault(() => BeginForm(scope, "server", "get_status", new { }, contextStamp: "old-server-context")),
            "Server workspace form did not verify its context stamp.");
        var passive = BeginForm(scope, "server", "get_status", new { }, contextStamp: serverStamp);
        Check(passive.ExpectedConnectionId == null, "Passive server status captured a TIA attachment.");
        scope.Runs.Finish(passive.Id, "success", "{}", null);
    }

    private static async Task ConnectionFence()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var first = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.Live && item.ProcessId == 10);
        var originalConnection = first.ConnectionId!.Value;
        var pending = BeginForm(scope, first.Id, "list_devices", new { });
        await scope.Engineering.DisconnectAsync(10);
        await scope.Engineering.ConnectAsync(10);
        var stale = await scope.Runner.RunAsync(pending);
        Check(stale.Capture.Outcome == "error" && scope.Backend.Processes[10].Reads == 0,
            "A run admitted before reconnect read the replacement attachment.");

        var current = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.Live && item.ProcessId == 10);
        var readback = BeginForm(scope, current.Id, "list_devices", new { });
        Check(readback.ExpectedConnectionId == current.ConnectionId && current.ConnectionId != originalConnection,
            "Readback race fixture did not capture a replacement attachment.");
        var blocked = await scope.Runner.RunAsync(readback, originalConnection);
        Check(blocked.Capture.Outcome == "error" && scope.Backend.Processes[10].Reads == 0,
            "Explicit original attachment override was ignored during readback.");

        await scope.Engineering.DisconnectAsync(10);
        var disconnected = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.Live && item.ProcessId == 10);
        var noAttachment = BeginForm(scope, disconnected.Id, "list_devices", new { });
        Check(noAttachment.ExpectedConnectionId == Guid.Empty,
            "A disconnected project form did not retain the no-attachment fence.");
        await scope.Engineering.ConnectAsync(10);
        var adopted = await scope.Runner.RunAsync(noAttachment);
        Check(adopted.Capture.Outcome == "error" && scope.Backend.Processes[10].Reads == 0,
            "A disconnected admission adopted a later attachment.");
    }

    private static async Task Selectors()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.Live && item.ProcessId == 10);
        var selectors = new DashboardSelectorStore(scope.Dashboard);

        Check(ObserveChoices(selectors, tab.Id, "list_devices", new { processId = 10 },
            new { roots = new[] { new { kind = "device", objectId = "device:1", name = "Station" } } }),
            "Device inventory did not provide a choice.");
        Check(selectors.ForTab(tab).Options["device"].Single().Id == "device:1",
            "Device choice lost the opaque native ID.");
        Check(ObserveChoices(selectors, tab.Id, "get_device", new { processId = 10, objectId = "device:1" },
            new { deviceItems = new[] { new { name = "CPU", plcObjectId = "cpu:1" } } }),
            "CPU detail did not provide a choice.");
        Check(selectors.ForTab(tab).Options["cpu"].Single().Id == "cpu:1",
            "CPU choice used the Device ID instead of its plcObjectId.");

        var blocks = new
        {
            roots = new object[]
            {
                new { kind = "blockGroup", objectId = "group:1", path = "PLC/Blocks/Named", isSystem = false,
                    children = new[] { new { kind = "block", objectId = "block:1", path = "PLC/Blocks/Named/B", isSystem = false } } },
                new { kind = "blockGroup", objectId = (string?)null, path = "PLC/Blocks/PathOnly", isSystem = false },
                new { kind = "blockGroup", objectId = "group:system", path = "PLC/System", isSystem = true }
            }
        };
        Check(ObserveChoices(selectors, tab.Id, "list_blocks", new { processId = 10, plcObjectId = "cpu:1" }, blocks),
            "Block inventory did not provide choices.");
        var (options, inventoryCpu) = selectors.ForTab(tab);
        Check(inventoryCpu == "cpu:1" && options["block"].Single().Id == "block:1" &&
            options["blockGroup"].Select(choice => choice.Id).SequenceEqual(new[] { "group:1" }) &&
            options["blockGroupPath"].Select(choice => choice.Id).SequenceEqual(new[]
                { "PLC/Blocks/Named", "PLC/Blocks/PathOnly" }),
            "Group ID/path choices included system groups or lost ID-less native paths.");

        var table = new { entries = new
        {
            tags = new[] { new { objectId = "tag:1", name = "Tag" } },
            userConstants = new[] { new { objectId = "constant:1", name = "Const" } },
            systemConstants = new[] { new { objectId = "system:1", name = "System" } }
        } };
        Check(ObserveChoices(selectors, tab.Id, "get_tag_table", new { processId = 10, objectId = "table:1" }, table),
            "Tag table detail did not provide entry choices.");
        Check(selectors.ForTab(tab).Options["entry"].Select(choice => choice.Id).SequenceEqual(new[]
            { "tag:1", "constant:1" }),
            "System constant became an editable entry choice.");

        var stale = new DashboardRunCapture
        {
            TabId = tab.Id, Operation = "list_blocks",
            RequestJson = JsonSerializer.Serialize(new { arguments = new { processId = 10, plcObjectId = "cpu:1" } })
        };
        var staleContext = selectors.Capture(stale);
        await scope.Engineering.DisconnectAsync(10);
        var completion = SelectorCompletion(stale, new { roots = new[]
            { new { kind = "block", objectId = "block:stale" } } });
        Check(!selectors.Observe(stale, completion, staleContext),
            "A read captured before disconnect repopulated stale native choices.");
        var current = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.Live && item.ProcessId == 10);
        var currentOptions = selectors.ForTab(current).Options;
        Check(!currentOptions.TryGetValue("block", out var currentBlocks) || currentBlocks.All(choice => choice.Id != "block:stale"),
            "A changed connection exposed stale native IDs in the current workspace.");
    }

    private static async Task SourceSignals()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var snapshot = scope.Dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.Single(item => item.Live && item.ProcessId == 10);
        var selectors = new DashboardSelectorStore(scope.Dashboard);
        var started = new DashboardRunCapture
        {
            TabId = tab.Id, Operation = "get_block",
            RequestJson = JsonSerializer.Serialize(new { arguments = new { processId = 10, objectId = "block:1" } })
        };
        var context = selectors.Capture(started);
        var exact = "DATA_BLOCK \"A\"\r\n  // < & >\r\nEND_DATA_BLOCK";
        var completion = SelectorCompletion(started, new
        {
            source = new { format = "external-source", documents = new[] { new { name = "A.db", content = exact } } }
        });
        var signals = DashboardRunFragments.SourceSignals(completion, snapshot, context, "write_blocks");
        Check(signals != null, "Matching source read did not provide an editor signal patch.");
        var prefix = DashboardSelectorStore.SignalPrefix(snapshot, tab);
        using (var patch = JsonDocument.Parse(signals!))
        {
            var nameKey = DashboardToolForms.DocumentSignalName(prefix, "write_blocks", 1, "name");
            var contentKey = DashboardToolForms.DocumentSignalName(prefix, "write_blocks", 1, "content");
            var secondNameKey = DashboardToolForms.DocumentSignalName(prefix, "write_blocks", 2, "name");
            var secondContentKey = DashboardToolForms.DocumentSignalName(prefix, "write_blocks", 2, "content");
            var formatKey = DashboardToolForms.FieldSignalName(prefix, "write_blocks", "sourceFormat");
            Check(patch.RootElement.EnumerateObject().Count() == 5 &&
                patch.RootElement.GetProperty(formatKey).GetString() == "external-source" &&
                patch.RootElement.GetProperty(nameKey).GetString() == "A.db" &&
                patch.RootElement.GetProperty(contentKey).GetString() == exact &&
                patch.RootElement.GetProperty(secondNameKey).GetString() == "" &&
                patch.RootElement.GetProperty(secondContentKey).GetString() == "",
                "Source helper patched unrelated fields or changed the native format.");
        }
        Check(DashboardRunFragments.SourceSignals(completion, snapshot, context, "write_udts") == null,
            "A block read could fill a UDT editor.");
        var resource = "RESOURCE\r\n  <keep this text>";
        var twoDocuments = SelectorCompletion(started, new
        {
            source = new { format = "simatic-sd", documents = new[]
            {
                new { name = "A.s7dcl", content = exact }, new { name = "A.s7res", content = resource }
            } }
        });
        using (var patch = JsonDocument.Parse(DashboardRunFragments.SourceSignals(twoDocuments, snapshot, context, "write_blocks")!))
            Check(patch.RootElement.GetProperty(DashboardToolForms.DocumentSignalName(prefix, "write_blocks", 2, "name")).GetString() == "A.s7res" &&
                patch.RootElement.GetProperty(DashboardToolForms.DocumentSignalName(prefix, "write_blocks", 2, "content")).GetString() == resource,
                "Second SD resource document was dropped or changed.");
        await scope.Engineering.DisconnectAsync(10);
        Check(DashboardRunFragments.SourceSignals(completion, scope.Dashboard.CurrentDashboard(), context, "write_blocks") == null,
            "A source read filled an editor after its attachment changed.");
    }

    private static DashboardRunCapture BeginForm(Scope scope, string tabId, string name, object fields,
        bool writeToolsAvailable = true, string? loadSourceFor = null, string? contextStamp = null)
    {
        var snapshot = scope.Dashboard.CurrentDashboard();
        var tab = snapshot.Tabs.FirstOrDefault(item => item.Id == tabId);
        var envelope = new Dictionary<string, object?>
        {
            ["tabId"] = tabId,
            ["contextStamp"] = contextStamp ?? (tab == null ? "missing" : DashboardSelectorStore.SignalPrefix(snapshot, tab)),
            ["requestId"] = Guid.NewGuid().ToString("D"),
            ["name"] = name, ["fields"] = fields
        };
        if (loadSourceFor != null) envelope["loadSourceFor"] = loadSourceFor;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        return scope.Runner.BeginFields(document.RootElement, writeToolsAvailable);
    }

    private static bool ObserveChoices(DashboardSelectorStore selectors, string tabId, string operation,
        object arguments, object payload)
    {
        var run = new DashboardRunCapture
        {
            TabId = tabId, Operation = operation,
            RequestJson = JsonSerializer.Serialize(new { arguments })
        };
        var context = selectors.Capture(run);
        return selectors.Observe(run, SelectorCompletion(run, payload), context);
    }

    private static DashboardRunCompletion SelectorCompletion(DashboardRunCapture started, object payload)
    {
        var finished = new DashboardRunCapture
        {
            TabId = started.TabId, Operation = started.Operation, Outcome = "success"
        };
        var response = JsonSerializer.Serialize(new { result = new
        {
            isError = false, content = new[] { new { text = JsonSerializer.Serialize(payload) } }
        } });
        return new DashboardRunCompletion(finished, response);
    }

    private static void Retention()
    {
        Check(DashboardRunStore.MaxCompleted == 40 && DashboardRunStore.MaxRetainedBytes == 64L * 1024 * 1024,
            "Production history limits changed.");
        var store = new DashboardRunStore(maxCompleted: 2, maxRetainedBytes: 200);
        var transitions = new List<(string Id, bool Started)>();
        store.Transition += item => transitions.Add((item.Capture.Id, item.Started));
        string Add(string payload)
        {
            var id = Guid.NewGuid().ToString("D");
            Check(store.TryStart(id, "server", "get_status", null, "{}", out _) != null, "Store rejected bounded start.");
            store.Finish(id, "success", payload, null);
            return id;
        }
        var first = Add("{\"first\":true}");
        var second = Add("{\"second\":true}");
        var third = Add("{\"third\":true}");
        Check(store.Get(first, "server") == null && store.Get(second, "server") != null && store.Get(third, "server") != null,
            "Retention did not evict the oldest whole completed run.");
        var oversized = Add(new string('x', 300));
        var retained = store.Get(oversized, "server")!;
        Check(!retained.PayloadRetained && retained.ResponseJson == null && retained.PayloadBytes > 200,
            "Oversized capture was truncated or retained rather than explicitly marked.");
        Check(transitions.Count == 8 && transitions[0].Started && !transitions[1].Started,
            "Ordered start/finish notifications were not emitted.");
        store.PruneTabs(new[] { "another-tab" });
        Check(store.Snapshot().Count == 0, "Evicted dashboard tabs retained full run captures.");
    }

    private static async Task StrictAdmission()
    {
        using var scope = new Scope(writesEnabled: false);
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        var id = Guid.NewGuid().ToString("D");
        foreach (var raw in new[]
        {
            "[]",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\"}",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\",\"arguments\":{},\"extra\":1}",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\",\"name\":\"get_status\",\"arguments\":{}}",
            "{\"tabId\":\"" + tab.Id + "\",\"requestId\":\"" + id + "\",\"name\":\"get_status\",\"arguments\":{\"processId\":0}}"
        })
        {
            using var document = JsonDocument.Parse(raw);
            Check(Fault(() => scope.Runner.Begin(document.RootElement)), "Malformed dashboard envelope was admitted: " + raw);
        }
        Check(scope.Runs.Snapshot().Count == 0 && scope.Backend.Processes[10].Reads == 0,
            "Admission failure recorded or dispatched a run.");
        var active = new List<DashboardRunCapture>();
        for (var index = 0; index < DashboardRunStore.MaxActive; index++)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "get_status", arguments = new { processId = 10 } }));
            active.Add(scope.Runner.Begin(document.RootElement));
        }
        using (var excess = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "get_status", arguments = new { processId = 10 } })))
        {
            try { scope.Runner.Begin(excess.RootElement); throw new InvalidOperationException("Fifth run was admitted."); }
            catch (ConnectionFault ex) { Check(ex.Code == "busy", "Fifth run returned the wrong admission fault."); }
        }
        foreach (var started in active) await scope.Runner.RunAsync(started);
        using var compile = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "compile_plc", arguments = new { processId = 10, plcObjectId = "CPU" } }));
        var rejected = await scope.Runner.RunAsync(scope.Runner.Begin(compile.RootElement));
        Check(rejected.Capture.Outcome == "error" && rejected.ResponseJson.Contains("unknownTool", StringComparison.Ordinal) &&
            scope.Backend.Processes[10].Compiles == 0, "Read-only profile dispatched an unpublished modifying tool.");
    }

    private static async Task DetachedCompletion()
    {
        using var scope = new Scope();
        await scope.Engineering.DiscoverAsync();
        await scope.Engineering.ConnectAsync(10);
        var tab = scope.Dashboard.CurrentDashboard().Tabs.Single(item => item.ProcessId == 10);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        { tabId = tab.Id, requestId = Guid.NewGuid().ToString("D"), name = "list_devices", arguments = new { processId = 10 } }));
        var started = scope.Runner.Begin(document.RootElement);
        var invocation = scope.Runner.RunAsync(started);
        // The HTTP action can lose its SSE writer after admission. The tool
        // task has no response-stream or request-cancellation dependency.
        await invocation;
        Check(scope.Runs.Get(started.Id, tab.Id)?.Completed == true && scope.Backend.Processes[10].Reads == 1,
            "No-writer completion did not retain exactly one result.");
    }

    private static void Escaping()
    {
        var run = new DashboardRunCapture
        {
            Id = Guid.NewGuid().ToString("D"), TabId = "server", Operation = "<img src=x>",
            StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            Outcome = "error", PayloadRetained = true, RequestJson = "{\"x\":\"<script>\"}",
            ResponseJson = "{\"result\":{\"isError\":true,\"content\":[{\"text\":\"<svg onload=x>\"}]}}"
        };
        var html = DashboardRunFragments.RenderInspector(run, "server");
        Check(!html.Contains("<img", StringComparison.Ordinal) && !html.Contains("<script", StringComparison.Ordinal) &&
            !html.Contains("<svg", StringComparison.Ordinal) && html.Contains("data-run-response", StringComparison.Ordinal) &&
            html.Contains("data-run-result", StringComparison.Ordinal) && html.Contains("&lt;svg", StringComparison.Ordinal),
            "Inspector did not HTML-escape exact request/response/result text.");
    }

    private static void IndentedResult()
    {
        var result = "{\"metadata\":{\"name\":\"Device\"},\"deviceItems\":[{\"name\":\"CPU\"}]}";
        var response = JsonSerializer.Serialize(new
        { result = new { content = new[] { new { text = result } } } });
        var run = new DashboardRunCapture
        {
            Id = Guid.NewGuid().ToString("D"), TabId = "server", Operation = "get_device",
            StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
            Outcome = "success", PayloadRetained = true, RequestJson = "{\"name\":\"get_device\"}",
            ResponseJson = response
        };
        var html = DashboardRunFragments.RenderInspector(run, "server");
        var resultStart = html.IndexOf("<pre data-run-result", StringComparison.Ordinal);
        var responseStart = html.IndexOf("<pre data-run-response", StringComparison.Ordinal);
        Check(resultStart >= 0 && responseStart > resultStart &&
            html.Substring(resultStart, responseStart - resultStart).Contains("\n  &quot;metadata&quot;: {", StringComparison.Ordinal) &&
            html.Contains(System.Net.WebUtility.HtmlEncode(response) + "</pre>", StringComparison.Ordinal) &&
            run.ResponseJson == response,
            "Result JSON was not indented or the exact MCP response was changed.");

        run.ResponseJson = JsonSerializer.Serialize(new
        { result = new { content = new[] { new { text = "plain text <native>" } } } });
        html = DashboardRunFragments.RenderInspector(run, "server");
        Check(html.Contains("plain text &lt;native&gt;</pre>", StringComparison.Ordinal),
            "Non-JSON result text was changed or rendered without escaping.");
    }

    private static void Classification()
    {
        string Wire(object payload, bool isError) => JsonSerializer.Serialize(new
        { result = new { isError, content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload) } } } });
        var partial = DashboardToolRunner.Classify(Wire(new
        { complete = false, affectedObjects = new[] { new { name = "A" } }, errors = new[] { new { message = "Native write failed" } } }, true));
        var compile = DashboardToolRunner.Classify(Wire(new
        { complete = true, compilationSucceeded = false, errors = new[] { new { message = "Compiler error" } } }, true));
        Check(partial.Outcome == "partial" && partial.Error == "Native write failed", "Partial native effects were hidden as a complete failure.");
        Check(compile.Outcome == "error" && compile.Error == "Compiler error", "Compiler diagnostics were mistaken for compilation success.");
    }

    private static bool Fault(Action action)
    {
        try { action(); return false; }
        catch (ConnectionFault ex) { return ex.Code == "invalidRequest"; }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Scope : IDisposable
    {
        public readonly StaTaskScheduler Sta = new();
        public readonly FakeConnectionBackend Backend = new();
        public readonly EngineeringService Engineering;
        public readonly DashboardService Dashboard;
        public readonly DashboardRunStore Runs = new();
        public readonly List<OperationCallNote> Notes = new();
        public readonly DashboardToolRunner Runner;

        public Scope(bool writesEnabled = true)
        {
            Backend.Processes[10] = new FakeProcess(@"C:\Projects\Demo.ap20");
            Engineering = new EngineeringService(Sta, Backend, writesEnabled);
            Dashboard = new DashboardService(Engineering);
            var mcp = new McpBoundary(Engineering, Json, _ => false, Notes.Add);
            Runner = new DashboardToolRunner(Dashboard, Runs, mcp, Json);
        }

        public void Dispose()
        { Dashboard.Dispose(); Engineering.Dispose(); Sta.Dispose(); }
    }
}
