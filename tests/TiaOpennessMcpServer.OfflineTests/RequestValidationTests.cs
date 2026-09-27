using System.Text.Json;
using TiaOpennessMcpServer.Operations;

internal static class RequestValidationTests
{
    private sealed record Parser(string Name, Func<JsonElement, object> Parse, string Selector,
        string MissingSelectorMessage, string FieldPrefix = "Unknown or duplicate field: ", bool Write = false);

    private static readonly Parser[] Parsers =
    {
        new("process discovery", root => DiscoveryRequest.Parse(root, false), "", "", "Unknown or duplicate request field: "),
        new("device discovery", root => DiscoveryRequest.Parse(root, true), "objectId", "Supply a nonblank Device objectId.", "Unknown or duplicate request field: "),
        new("CPU discovery", root => DiscoveryRequest.Parse(root, false, true), "plcObjectId", "Supply a nonblank CPU DeviceItem plcObjectId.", "Unknown or duplicate request field: "),
        new("block/UDT read", root => BlockReadRequest.Parse(root), "objectId", "Supply a nonblank objectId."),
        new("tag-table read", root => TagTableReadRequest.Parse(root), "objectId", "Supply a nonblank tag-table objectId."),
        new("tag-table export", root => ExportTagTableRequest.Parse(root), "objectId", "Supply a nonblank tag-table objectId."),
        new("cross-reference read", root => CrossReferenceRequest.Parse(root), "objectId", "Supply a nonblank engineering objectId."),
        new("compile", root => CompileRequest.Parse(root), "plcObjectId", "Supply a nonblank CPU DeviceItem plcObjectId."),
        new("technology-object read", root => TechnologyObjectReadRequest.Parse(root), "objectId", "Supply a nonblank technology-object objectId."),
        new("write", root => WriteRequest.Parse("delete_block", root), "objectId", "Supply objectId.", Write: true)
    };

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("request validation: process errors and duplicate-value precedence preserve exact faults", ProcessErrors);
        yield return ("request validation: field spelling, duplicates and encounter order preserve exact faults", Fields);
        yield return ("request validation: required selectors remain opaque and retain parser-specific faults", Selectors);
        yield return ("request validation: Boolean defaults, strict types and validation order remain compatible", Flags);
    }

    private static void Fault(Parser parser, string body, int processId, string message)
    {
        using var document = JsonDocument.Parse(body);
        try { parser.Parse(document.RootElement); }
        catch (ConnectionFault fault)
        {
            Check(fault.Code == "invalidRequest" && fault.ProcessId == processId && fault.Message == message,
                parser.Name + ": expected invalidRequest/" + processId + "/" + message +
                ", received " + fault.Code + "/" + fault.ProcessId + "/" + fault.Message);
            return;
        }
        throw new Exception(parser.Name + ": accepted invalid request " + body);
    }

    private static object Parse(Parser parser, string body)
    {
        using var document = JsonDocument.Parse(body);
        return parser.Parse(document.RootElement);
    }

    private static void ProcessErrors()
    {
        const string message = "Supply a positive integer processId.";
        foreach (var parser in Parsers)
        {
            foreach (var body in new[] { "null", "[]", "true", "20", "\"object\"" })
                Fault(parser, body, 0, parser.Write ? "Supply an arguments object." : message);
            Fault(parser, "{}", 0, message);
            foreach (var value in new[] { "null", "true", "\"20\"", "0", "-1", "1.5", "2147483648", "1e400" })
                Fault(parser, "{\"unknown\":true,\"processId\":" + value + "}", 0, message);
            Fault(parser, "{\"processId\":20,\"processId\":21}", 21, parser.FieldPrefix + "processId");
            Fault(parser, "{\"processId\":20,\"processId\":null}", 0, message);
        }
    }

    private static void Fields()
    {
        foreach (var parser in Parsers)
        {
            // Field checks precede missing selectors; the first unexpected field wins.
            Fault(parser, "{\"processId\":20,\"unknown\":null,\"other\":true}", 20, parser.FieldPrefix + "unknown");
            Fault(parser, "{\"processId\":20,\"other\":true,\"unknown\":null}", 20, parser.FieldPrefix + "other");
            Fault(parser, "{\"processId\":20,\"ProcessId\":20}", 20, parser.FieldPrefix + "ProcessId");
            if (parser.Selector.Length > 0)
                Fault(parser, "{\"processId\":20,\"" + parser.Selector + "\":\"x\",\"" + parser.Selector + "\":\"y\"}",
                    20, parser.FieldPrefix + parser.Selector);
        }
        var unknownWrite = new Parser("unknown write tool", root => WriteRequest.Parse("unknown", root), "", "", Write: true);
        Fault(unknownWrite, "{\"processId\":20,\"unknown\":true}", 20, "Unknown write tool.");
        Fault(unknownWrite, "null", 0, "Supply an arguments object.");
    }

    private static void Selectors()
    {
        const string opaque = " native /== ID ";
        foreach (var parser in Parsers.Where(parser => parser.Selector.Length > 0))
        {
            Fault(parser, "{\"processId\":20}", 20, parser.MissingSelectorMessage);
            foreach (var value in new[] { "null", "true", "20", "\"\"", JsonSerializer.Serialize(" ") })
                Fault(parser, "{\"processId\":20,\"" + parser.Selector + "\":" + value + "}", 20,
                    parser.Write ? parser.Selector + " must be a nonblank string." : parser.MissingSelectorMessage);
            var request = Parse(parser, "{\"processId\":2147483647,\"" + parser.Selector + "\":\"" + opaque + "\"}");
            var selector = request switch
            {
                DiscoveryRequest discovery => parser.Selector == "objectId" ? discovery.ObjectId : discovery.PlcObjectId,
                BlockReadRequest block => block.ObjectId,
                TagTableReadRequest table => table.ObjectId,
                ExportTagTableRequest export => export.ObjectId,
                CrossReferenceRequest references => references.ObjectId,
                CompileRequest compile => compile.PlcObjectId,
                TechnologyObjectReadRequest technology => technology.ObjectId,
                WriteRequest write => write.ObjectId,
                _ => throw new Exception("Missing selector assertion for " + parser.Name)
            };
            Check(selector == opaque, parser.Name + ": altered an opaque selector.");
        }
        var combined = new Parser("combined discovery", root => DiscoveryRequest.Parse(root, true, true), "", "",
            "Unknown or duplicate request field: ");
        Fault(combined, "{\"processId\":20,\"objectId\":null,\"plcObjectId\":null,\"includePath\":null}",
            20, "Supply a nonblank CPU DeviceItem plcObjectId.");
    }

    private static void Flags()
    {
        var flagParsers = new[]
        {
            (Parser: Parsers[1], Flags: new[] { "includePath" }),
            (Parser: Parsers[3], Flags: new[] { "includePath", "includeSource", "includeDependencies" }),
            (Parser: Parsers[4], Flags: new[] { "includePath", "includeEntries" }),
            (Parser: Parsers[8], Flags: new[] { "includePath", "includeParameters" })
        };
        foreach (var (parser, flags) in flagParsers)
        {
            var body = "{\"processId\":20,\"objectId\":\"id\"";
            foreach (var flag in flags)
            {
                foreach (var value in new[] { "null", "0", "\"false\"", "[]", "{}" })
                    Fault(parser, body + ",\"" + flag + "\":" + value + "}", 20, flag + " must be a boolean.");
                Fault(parser, body + ",\"" + flag + "\":true,\"" + flag + "\":false}", 20, parser.FieldPrefix + flag);
            }
            Fault(parser, "{\"processId\":20,\"includePath\":null}", 20, parser.MissingSelectorMessage);
            Fault(parser, body + string.Concat(flags.Reverse().Select(flag => ",\"" + flag + "\":null")) + "}",
                20, flags[0] + " must be a boolean.");
            var defaults = Parse(parser, body + "}");
            var disabled = Parse(parser, body + string.Concat(flags.Select(flag => ",\"" + flag + "\":false")) + "}");
            Check(FlagValues(defaults).SequenceEqual(flags.Select(flag => flag != "includeDependencies")), parser.Name + ": changed defaults.");
            Check(FlagValues(disabled).All(value => !value), parser.Name + ": lost explicit false flags.");
        }
        Fault(Parsers[3], "{\"processId\":20,\"objectId\":\"id\",\"sourceFormat\":\"wrong\",\"includeDependencies\":null}",
            20, "includeDependencies must be a boolean.");
        Fault(Parsers[3], "{\"processId\":20,\"objectId\":\"id\",\"sourceFormat\":\"wrong\",\"includeDependencies\":true}",
            20, "Unknown sourceFormat.");
    }

    private static bool[] FlagValues(object request) => request switch
    {
        DiscoveryRequest discovery => new[] { discovery.IncludePath },
        BlockReadRequest block => new[] { block.IncludePath, block.IncludeSource, block.IncludeDependencies },
        TagTableReadRequest table => new[] { table.IncludePath, table.IncludeEntries },
        TechnologyObjectReadRequest technology => new[] { technology.IncludePath, technology.IncludeParameters },
        _ => throw new Exception("Missing flag assertion.")
    };
}
