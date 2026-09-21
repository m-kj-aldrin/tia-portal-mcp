using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaOpennessMcpServer.Prototype;

internal static class DiscoveryTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("discovery: strict arguments preserve native selectors", Requests);
        yield return ("discovery: partial collections preserve native order and readable siblings", PartialCollections);
        yield return ("discovery: collection acquisition differs from empty and interrupted enumeration", CollectionFailures);
        yield return ("discovery: traversal stops immediately on context loss", ContextLoss);
        yield return ("discovery: native values cannot leak proxy objects into JSON", Values);
        yield return ("discovery: required nulls and scoped evidence survive serialization", Serialization);
    }

    private static void Requests()
    {
        using var valid = JsonDocument.Parse("{\"processId\":20,\"objectId\":\" native ID \",\"includePath\":false}");
        var request = DiscoveryRequest.Parse(valid.RootElement, true);
        Check(request.ProcessId == 20 && request.ObjectId == " native ID " && !request.IncludePath, "Selector altered.");
        using var defaults = JsonDocument.Parse("{\"processId\":20,\"objectId\":\"id\"}");
        Check(DiscoveryRequest.Parse(defaults.RootElement, true).IncludePath, "Wrong path default.");
        foreach (var body in new[]
        {
            "{}", "[]", "null", "{\"processId\":0}", "{\"processId\":1.5}", "{\"processId\":\"20\"}",
            "{\"processId\":20,\"processId\":20}", "{\"processId\":20,\"expectedProject\":\"x\"}",
            "{\"processId\":20,\"objectId\":\"\"}", "{\"processId\":20,\"objectId\":\"id\",\"includePath\":null}",
            "{\"processId\":20,\"objectId\":\"id\",\"includeSource\":false}",
            "{\"processId\":20,\"objectId\":\"id\",\"objectId\":\"id2\"}"
        })
        {
            using var document = JsonDocument.Parse(body);
            try { DiscoveryRequest.Parse(document.RootElement, true); throw new Exception("Accepted invalid request: " + body); }
            catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
        }
        try { DiscoveryRequest.Parse(valid.RootElement, false); throw new Exception("Object selector accepted for process-only request."); }
        catch (ConnectionFault ex) when (ex.Code == "invalidRequest") { }
    }

    private static void PartialCollections()
    {
        var result = new DeviceInventory();
        var validations = 0;
        var read = new DiscoveryReadContext(result.Errors, () => validations++);
        var values = read.Collect(() => new[] { "Z", "protected", "A" }, item => item == "protected"
            ? throw new UnauthorizedAccessException("Native protected item") : item, "group");
        Check(values!.SequenceEqual(new[] { "Z", "A" }), "Reordered or discarded readable siblings.");
        Check(!result.Complete && result.Errors.Single().Message == "Native protected item" && validations == 2,
            "Missing native failure or context check.");
    }

    private static IEnumerable<int> Interrupted()
    {
        yield return 7;
        throw new InvalidOperationException("Native enumeration failed");
    }

    private static void CollectionFailures()
    {
        var errors = new List<DiscoveryError>();
        var read = new DiscoveryReadContext(errors, () => { });
        Check(read.Collect<int, int>(() => throw new Exception("Unavailable"), x => x, "missing") == null,
            "Failed collection represented as empty.");
        Check(read.Collect(() => Array.Empty<int>(), x => x, "empty")!.Count == 0, "Empty collection not preserved.");
        Check(read.Collect(Interrupted, x => x, "partial")!.SequenceEqual(new[] { 7 }), "Partial enumeration discarded.");
        Check(errors.Count == 2 && errors.All(error => error.Operation == "enumerate"), "Enumeration failures lost.");
    }

    private static void ContextLoss()
    {
        var errors = new List<DiscoveryError>();
        var alive = true;
        var touched = new List<int>();
        var read = new DiscoveryReadContext(errors, () =>
        {
            if (!alive) throw new ConnectionFault("reconnectRequired", 20, "Context changed");
        });
        try
        {
            read.Collect(() => new[] { 1, 2, 3 }, item =>
            {
                touched.Add(item);
                if (item == 2) { alive = false; throw new Exception("Native proxy lost"); }
                return item;
            }, "device");
            throw new Exception("Returned payload after invalidation.");
        }
        catch (ConnectionFault ex) when (ex.Code == "reconnectRequired") { }
        Check(touched.SequenceEqual(new[] { 1, 2 }) && errors.Count == 0, "Continued after lost context or swallowed guard error.");
    }

    private sealed class Proxy
    {
        public string Dangerous => throw new Exception("Proxy property reached by serializer");
    }

    private static void Values()
    {
        var cycle = new ArrayList(); cycle.Add(cycle);
        var native = new object?[] { null, true, 7, "name", DayOfWeek.Monday, new Proxy() };
        var converted = DiscoveryValues.Convert(native);
        var json = JsonSerializer.Serialize(converted);
        Check(json.Contains("Monday") && json.Contains("valueSerialized") && !json.Contains("Dangerous"), "Proxy leaked.");
        Check(JsonSerializer.Serialize(DiscoveryValues.Convert(cycle)).Contains("valueSerialized"), "Cyclic collection not bounded.");
        Check(JsonSerializer.Serialize(DiscoveryValues.Convert(double.NaN)).Contains("valueSerialized"), "NaN not handled.");
        Check((string?)DiscoveryValues.Convert(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(2))) ==
            "2026-09-21T10:00:00.0000000+00:00", "Timestamp not normalized to UTC.");
    }

    private static void Serialization()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(new ProcessStatus { ProcessId = 20 }, options));
        Check(status.RootElement.GetProperty("tia").ValueKind == JsonValueKind.Null &&
            status.RootElement.GetProperty("project").ValueKind == JsonValueKind.Null, "Status nulls omitted.");
        using var entry = JsonDocument.Parse(JsonSerializer.Serialize(new ProcessEntry { ProcessId = 20 }, options));
        Check(entry.RootElement.GetProperty("primaryProjectPath").ValueKind == JsonValueKind.Null, "Projectless path omitted.");
        var evidence = JsonSerializer.Serialize(PrototypeEvidence.SamePathReopen, options);
        Check(evidence.Contains("user-reported-pass") && evidence.Contains("2026-09-21") && evidence.Contains("limitation"),
            "Evidence scope not exposed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
