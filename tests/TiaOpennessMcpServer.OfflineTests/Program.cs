using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;
using System.Text.Json;
using System.Text.Json.Serialization;

var tests = new (string Name, Action Run)[]
{
    ("SHA-256 known vectors", CheckKnownHashes),
    ("UTF-8 no-BOM preserves an explicit BOM code point", CheckExplicitBomCodePoint),
    ("line endings are exact content", CheckLineEndingDistinction),
    ("Unicode uses UTF-8 byte lengths", CheckUnicodeByteLengths),
    ("SIMATIC SD bundle is ordinal and deterministic", CheckBundleHash),
    ("SIMATIC SD bundle framing and validation", CheckBundleFramingAndValidation),
    ("representation negotiation matrix", CheckRepresentationMatrix),
    ("strict representation requests do not fall back", CheckStrictFallbackPolicy),
    ("project version is stable and explicit in provenance JSON", CheckProjectVersionContract),
    ("portal version comes only from the native installed product", CheckPortalVersionPolicy),
    ("installed-product provenance has no update field", CheckInstalledProductContract),
    ("protected attempt contract and fallback policy", CheckProtectedFallbackEvidence),
    ("protected-object metadata and native-message classification", CheckProtectionPolicy),
    ("native failure classification preserves cause boundaries", CheckNativeFailurePolicy),
    ("MCP argument policy rejects undeclared properties", CheckMcpArgumentPolicy),
    ("locked V1 MCP surface contains exactly eight canonical tools", CheckV1McpToolPolicy),
    ("protected response contract serializes explicit scope", CheckProtectedContractSerialization),
    ("object selectors enforce uniqueness and PLC ownership", CheckObjectSelectors),
    ("selector identifiers are opaque and selectors must be meaningful", CheckSelectorEdgeCases),
    ("PLC selectors enforce uniqueness", CheckPlcSelectors),
    ("project selection lifecycle matrix", CheckProjectSelectionPolicy),
    ("project canonicalization and conflict precedence", CheckProjectSelectionEdgeCases),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} offline test groups passed.");
return failed == 0 ? 0 : 1;

static void CheckKnownHashes()
{
    var empty = V1Checksums.ForContent(string.Empty);
    Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", empty.Value);
    Equal(0L, empty.ByteLength);
    Equal(V1Checksums.Algorithm, empty.Algorithm);
    Equal(V1Checksums.ContentEncoding, empty.Encoding);

    var abc = V1Checksums.ForContent("abc");
    Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", abc.Value);
    Equal(3L, abc.ByteLength);
}

static void CheckExplicitBomCodePoint()
{
    var checksum = V1Checksums.ForContent("\uFEFFabc");

    Equal("1c28dc3f1f804a1ad9c9b4b4cf5e2658d16ad4ed08e3020d04a8d2865018947c", checksum.Value);
    Equal(6L, checksum.ByteLength);
    NotEqual(V1Checksums.ForContent("abc").Value, checksum.Value);
}

static void CheckLineEndingDistinction()
{
    var lf = V1Checksums.ForContent("line 1\nline 2\n");
    var crlf = V1Checksums.ForContent("line 1\r\nline 2\r\n");

    Equal("9060554863a62b9db5f726216876654e561896071d2e6480f2048b70e0fdadb9", lf.Value);
    Equal("821af7eca18c2b5de4663bfd464e20fac360065baabcfb48082cefab599ac5d1", crlf.Value);
    NotEqual(lf.Value, crlf.Value);
    Equal(14L, lf.ByteLength);
    Equal(16L, crlf.ByteLength);
}

static void CheckUnicodeByteLengths()
{
    const string content = "Å😀\r\n";
    var document = V1Checksums.CreateDocument("unicode.s7dcl", content, "source", "text/plain");

    Equal(8L, document.ByteLength);
    Equal(8L, document.Checksum.ByteLength);
    Equal(8L, V1Checksums.GetUtf8ByteLength(content));
    Equal("269a55562d595b97e46a2772baeee3dc5b49687f018c6d07de6df00b6cd9ae7c", document.Checksum.Value);
}

static void CheckBundleHash()
{
    var documents = new[]
    {
        V1Checksums.CreateDocument("z.s7res", "β\r\n"),
        V1Checksums.CreateDocument("å.s7res", "Ω"),
        V1Checksums.CreateDocument("a.s7res", "😀"),
        V1Checksums.CreateDocument("A.s7dcl", "abc"),
    };

    var forward = V1Checksums.ForSimaticSdBundle(documents);
    var reverse = V1Checksums.ForSimaticSdBundle(documents.Reverse());

    Equal("a6d00fef9e0e227bd350ee2a1b0eb9447fa8a15ddf32327f95c37a4c8b9b2c33", forward.Value);
    Equal(forward.Value, reverse.Value);
    Equal(90L, forward.ByteLength);
    Equal(V1Checksums.ContentEncoding, forward.Encoding);
    Equal(V1Checksums.SimaticSdBundleScheme, forward.Scheme);
}

static void CheckBundleFramingAndValidation()
{
    var fakeMetadataDocument = new V1Document
    {
        FileName = "x",
        Content = "y",
        ByteLength = 999,
        Checksum = new V1Checksum
        {
            Algorithm = "not-used",
            Encoding = "not-used",
            Value = "not-used",
            ByteLength = 999,
        },
    };

    var checksum = V1Checksums.ForSimaticSdBundle(new[] { fakeMetadataDocument });
    Equal("9ce26c9c2d3b1d4aa1788e929041456d436ebba37700ab07c9052499468ba4b2", checksum.Value);
    Equal(14L, checksum.ByteLength);

    Throws<ArgumentException>(() => V1Checksums.ForSimaticSdBundle(Array.Empty<V1Document>()));
    Throws<ArgumentException>(() => V1Checksums.ForSimaticSdBundle(new[]
    {
        V1Checksums.CreateDocument("duplicate.s7res", "first"),
        V1Checksums.CreateDocument("duplicate.s7res", "second"),
    }));
}

static void CheckRepresentationMatrix()
{
    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.FunctionBlock, "SCL", "best"),
        (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Applicable),
        (V1RepresentationFormats.SclSource, V1PlanApplicability.Applicable),
        (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.FunctionBlock, "LAD", null),
        (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Applicable),
        (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));

    foreach (var codeBlockType in new[]
             {
                 V1ObjectTypes.OrganizationBlock,
                 V1ObjectTypes.FunctionBlock,
                 V1ObjectTypes.Function,
             })
    {
        PlanEquals(
            V1RepresentationPolicy.CreatePlan(codeBlockType, "GRAPH", "best"),
            (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Applicable),
            (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));
    }

    foreach (var dataBlockType in new[]
             {
                 V1ObjectTypes.DataBlock,
                 V1ObjectTypes.GlobalDataBlock,
                 V1ObjectTypes.InstanceDataBlock,
                 V1ObjectTypes.ArrayDataBlock,
             })
    {
        PlanEquals(
            V1RepresentationPolicy.CreatePlan(dataBlockType, null, "best"),
            (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Applicable),
            (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));
    }

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.PlcDataType, null, "best"),
        (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Applicable),
        (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.PlcTagTable, null, "best"),
        (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.PlcTagTable, null, "simatic-sd"),
        (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Unsupported));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.PlcTagTable, null, "scl-source"),
        (V1RepresentationFormats.SclSource, V1PlanApplicability.Unsupported));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.DataBlock, null, "scl-source"),
        (V1RepresentationFormats.SclSource, V1PlanApplicability.Unsupported));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.Function, "LAD", "scl-source"),
        (V1RepresentationFormats.SclSource, V1PlanApplicability.NotApplicable));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.Function, "SCL", "simatic-sd"),
        (V1RepresentationFormats.SimaticSd, V1PlanApplicability.Applicable));

    PlanEquals(
        V1RepresentationPolicy.CreatePlan(V1ObjectTypes.Function, "SCL", "simaticml"),
        (V1RepresentationFormats.SimaticMl, V1PlanApplicability.Applicable));

    Equal(V1RepresentationFormats.Best, V1RepresentationPolicy.NormalizeFormat("  "));
    Equal(V1RepresentationFormats.SimaticSd, V1RepresentationPolicy.NormalizeFormat(" SIMATIC-SD "));
    True(V1RepresentationPolicy.IsPureSclBlock(V1ObjectTypes.OrganizationBlock, "scl"));
    False(V1RepresentationPolicy.IsPureSclBlock(V1ObjectTypes.DataBlock, "SCL"));
    False(V1RepresentationPolicy.IsPureSclBlock(V1ObjectTypes.FunctionBlock, "Mixed"));

    ThrowsBridgeCode(
        () => V1RepresentationPolicy.CreatePlan("technology-object", null, "best"),
        V1ErrorCodes.UnsupportedObject);
    ThrowsBridgeCode(
        () => V1RepresentationPolicy.CreatePlan(V1ObjectTypes.Function, "SCL", "made-up"),
        V1ErrorCodes.UnsupportedFormat);
}

static void CheckStrictFallbackPolicy()
{
    True(V1RepresentationPolicy.CanFallback("best", V1AttemptResults.Failed));
    True(V1RepresentationPolicy.CanFallback("best", V1AttemptResults.Partial));
    True(V1RepresentationPolicy.CanFallback("best", V1AttemptResults.Unsupported));
    True(V1RepresentationPolicy.CanFallback("best", V1AttemptResults.NotApplicable));
    False(V1RepresentationPolicy.CanFallback("best", V1AttemptResults.Succeeded));
    False(V1RepresentationPolicy.CanFallback("simatic-sd", V1AttemptResults.Failed));
    False(V1RepresentationPolicy.CanFallback("scl-source", V1AttemptResults.Partial));
    False(V1RepresentationPolicy.CanFallback("simaticml", V1AttemptResults.Unsupported));
}

static void CheckProjectVersionContract()
{
    var options = ApplicationJsonOptions();
    var nullVersion = new V1Provenance
    {
        ReadAtUtc = DateTimeOffset.UnixEpoch,
        Project = new V1ProjectIdentity
        {
            Name = "Example",
            Version = V1ProjectVersionPolicy.Normalize(null),
        },
    };

    using (var document = JsonDocument.Parse(JsonSerializer.Serialize(nullVersion, options)))
    {
        var project = document.RootElement.GetProperty("project");
        True(project.TryGetProperty("version", out var version));
        Equal(JsonValueKind.Null, version.ValueKind);
    }

    const string nativeVersion = "  V20 project metadata  ";
    Equal(nativeVersion, V1ProjectVersionPolicy.Normalize(nativeVersion));
    var nativeIdentity = new V1ProjectIdentity
    {
        Name = "Example",
        Version = V1ProjectVersionPolicy.Normalize(nativeVersion),
    };
    using (var document = JsonDocument.Parse(JsonSerializer.Serialize(nativeIdentity, options)))
    {
        Equal(nativeVersion, document.RootElement.GetProperty("version").GetString());
    }

    Equal(null, V1ProjectVersionPolicy.Normalize(null));
    Equal(null, V1ProjectVersionPolicy.Normalize(""));
    Equal(null, V1ProjectVersionPolicy.Normalize(" \t \r\n "));
}

static void CheckInstalledProductContract()
{
    False(typeof(V1InstalledProduct).GetProperties().Any(property =>
        string.Equals(property.Name, "Update", StringComparison.Ordinal)));

    var product = new V1InstalledProduct
    {
        Name = "TIA Portal",
        Version = "V20",
        Options = new[]
        {
            new V1InstalledProduct
            {
                Name = "STEP 7",
                Version = "V20",
            },
        },
    };

    using var document = JsonDocument.Parse(
        JsonSerializer.Serialize(product, ApplicationJsonOptions()));
    False(document.RootElement.EnumerateObject().Any(property =>
        string.Equals(property.Name, "update", StringComparison.Ordinal)));
    var option = document.RootElement.GetProperty("options")[0];
    False(option.EnumerateObject().Any(property =>
        string.Equals(property.Name, "update", StringComparison.Ordinal)));
}

static void CheckPortalVersionPolicy()
{
    var actualSiemensProducts = new[]
    {
        new V1InstalledProduct
        {
            Name = "STEP 7 Professional",
            Version = "V20",
        },
        new V1InstalledProduct
        {
            Name = "Totally Integrated Automation Portal",
            Version = "V20",
        },
    };

    Equal("V20", V1PortalVersionPolicy.FromInstalledProducts(actualSiemensProducts));

    var shortNativeName = new[]
    {
        new V1InstalledProduct
        {
            Name = "TIA Portal",
            Version = "native-version",
        },
    };
    Equal("native-version", V1PortalVersionPolicy.FromInstalledProducts(shortNativeName));

    var blankNativeVersion = new[]
    {
        new V1InstalledProduct
        {
            Name = "Totally Integrated Automation Portal",
            Version = " \t ",
        },
    };
    Equal(null, V1PortalVersionPolicy.FromInstalledProducts(blankNativeVersion));

    var optionOnly = new[]
    {
        new V1InstalledProduct
        {
            Name = "TIA Portal Openness",
            Version = "V20",
        },
    };
    Equal(null, V1PortalVersionPolicy.FromInstalledProducts(optionOnly));
    Equal(null, V1PortalVersionPolicy.FromInstalledProducts(Array.Empty<V1InstalledProduct>()));
}

static void CheckProtectedFallbackEvidence()
{
    var attempt = new V1Attempt
    {
        Format = V1RepresentationFormats.SimaticSd,
        Result = V1AttemptResults.Failed,
        Reason = "TIA Portal reported protected native content.",
        ErrorCode = V1ErrorCodes.ProtectedContent,
    };

    Equal(V1ErrorCodes.ProtectedContent, attempt.ErrorCode);
    True(V1RepresentationPolicy.CanFallback("best", attempt.Result));
    False(V1RepresentationPolicy.CanFallback("simatic-sd", attempt.Result));
}

static void CheckProtectionPolicy()
{
    var protectedObject = V1ProtectionPolicy.Describe(true);
    Equal(V1ProtectionStates.Protected, protectedObject.State);
    Equal(true, protectedObject.IsProtected);
    Equal(V1ProtectionTypes.KnowHow, protectedObject.Type);
    Equal(V1ProtectionAccess.NativeLimited, protectedObject.Access);
    True(!string.IsNullOrWhiteSpace(protectedObject.ContentLimitation));
    Equal(
        V1ContentScopes.TiaExposedProtectedView,
        V1ProtectionPolicy.ContentScope(true));

    var unprotectedObject = V1ProtectionPolicy.Describe(false);
    Equal(V1ProtectionStates.Unprotected, unprotectedObject.State);
    Equal(false, unprotectedObject.IsProtected);
    Equal(null, unprotectedObject.Type);
    Equal(V1ProtectionAccess.NativeFull, unprotectedObject.Access);
    Equal(
        V1ContentScopes.FullNativeRepresentation,
        V1ProtectionPolicy.ContentScope(false));

    var unknownObject = V1ProtectionPolicy.Describe(null);
    Equal(V1ProtectionStates.Unknown, unknownObject.State);
    Equal(null, unknownObject.IsProtected);
    Equal(V1ProtectionAccess.Unknown, unknownObject.Access);
    Equal(
        V1ContentScopes.ProtectionUnknown,
        V1ProtectionPolicy.ContentScope(null));

    foreach (var message in new[]
             {
                 "The block is know-how-protected and cannot be exported.",
                 "The block is know-how protected.",
                 "Know how protected content is unavailable.",
                 "Protected content cannot be read.",
                 "Not exportable because protected content.",
                 "The block is not readable: protected content.",
             })
    {
        True(V1ProtectionPolicy.IsProtectedFailureMessage(message));
    }

    False(V1ProtectionPolicy.IsProtectedFailureMessage("External-access approval is required."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("The block is inconsistent."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("The block is not know-how protected."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("The block is not currently know-how protected."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("The block is no longer know-how protected."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("This is not currently protected content."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("The block is not marked as know-how protected."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("The object is without any know-how protection."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage("Unprotected content was exported."));
    False(V1ProtectionPolicy.IsProtectedFailureMessage(null));
}

static void CheckNativeFailurePolicy()
{
    Equal(
        V1ErrorCodes.ProtectedContent,
        V1NativeFailurePolicy.Classify(
            "The block is know-how-protected and cannot be exported."));
    Equal(
        V1ErrorCodes.ProtectedContent,
        V1NativeFailurePolicy.Classify(
            "Authentication is required to access protected content."));
    Equal(
        V1ErrorCodes.UiAuthenticationRequired,
        V1NativeFailurePolicy.Classify(
            "External access is required for this know-how-protected block."));
    Equal(
        V1ErrorCodes.MissingProductOrOption,
        V1NativeFailurePolicy.Classify("Export failed.", isMissingProductException: true));
    Equal(
        V1ErrorCodes.ExportFailed,
        V1NativeFailurePolicy.Classify(
            "A Siemens security operation failed.",
            isSecurityException: true));
}

static void CheckMcpArgumentPolicy()
{
    var allowed = new[] { "plc", "objectId", "path", "name", "type", "format" };
    Equal(null, McpArgumentPolicy.FirstUnknown(allowed, new[] { "plc", "format" }));
    Equal("password", McpArgumentPolicy.FirstUnknown(allowed, new[] { "plc", "password" }));
    Equal("unlock", McpArgumentPolicy.FirstUnknown(allowed, new[] { "unlock" }));
    Equal("Password", McpArgumentPolicy.FirstUnknown(allowed, new[] { "Password" }));

    var toolCallParams = new[] { "name", "arguments", "_meta" };
    Equal(null, McpArgumentPolicy.FirstUnknown(toolCallParams, new[] { "name", "_meta" }));
    Equal("password", McpArgumentPolicy.FirstUnknown(toolCallParams, new[] { "name", "password" }));
}

static void CheckV1McpToolPolicy()
{
    var expected = new[]
    {
        "connect_to_tia_portal",
        "get_status",
        "list_devices",
        "list_plc_objects",
        "find_plc_objects",
        "read_plc_object",
        "get_tag_table_entries",
        "get_cross_references",
    };

    Equal(8, V1McpToolPolicy.CanonicalNames.Count);
    Equal(string.Join("\n", expected), string.Join("\n", V1McpToolPolicy.CanonicalNames));
    foreach (var toolName in expected)
        True(V1McpToolPolicy.IsCanonical(toolName));

    foreach (var removedToolName in new[]
             {
                 "list_blocks",
                 "read_block",
                 "read_scl_source",
                 "read_lad_source",
                 "list_tag_tables",
                 "get_tags",
                 "analyze_scl",
                 "analyze_block",
                 "get_option_packages",
                 "get_project_signature",
             })
    {
        False(V1McpToolPolicy.IsCanonical(removedToolName));
    }

    False(V1McpToolPolicy.IsCanonical(null));
    False(V1McpToolPolicy.IsCanonical("GET_STATUS"));
}

static void CheckProtectedContractSerialization()
{
    var response = new V1ReadPlcObjectResponse
    {
        Provenance = new V1Provenance { ReadAtUtc = DateTimeOffset.UnixEpoch },
        Request = new V1RepresentationRequest { Format = V1RepresentationFormats.Best },
        Protection = V1ProtectionPolicy.Describe(true),
        Representation = new V1Representation
        {
            Format = V1RepresentationFormats.SimaticMl,
            Authority = V1Authority.NativeExport,
            Completeness = V1Completeness.Complete,
            Complete = true,
            ContentScope = V1ContentScopes.TiaExposedProtectedView,
            Documents = new[]
            {
                V1Checksums.CreateDocument(
                    "object.xml",
                    "<Document><SW.Blocks.FB><AttributeList><Name>FB_Motor</Name></AttributeList></SW.Blocks.FB></Document>",
                    "simaticml",
                    "application/xml"),
            },
        },
        Attempts = new[]
        {
            new V1Attempt
            {
                Format = V1RepresentationFormats.SimaticSd,
                Result = V1AttemptResults.Failed,
                Reason = "Protected.",
                ErrorCode = V1ErrorCodes.ProtectedContent,
            },
        },
    };
    var json = JsonSerializer.Serialize(response, ApplicationJsonOptions());

    True(json.Contains("\"protection\"", StringComparison.Ordinal));
    True(json.Contains("\"state\":\"protected\"", StringComparison.Ordinal));
    True(json.Contains("\"contentScope\":\"tia-exposed-protected-view\"", StringComparison.Ordinal));
    True(json.Contains("\"errorCode\":\"protected-content\"", StringComparison.Ordinal));
}

static JsonSerializerOptions ApplicationJsonOptions()
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
    options.Converters.Add(new JsonStringEnumConverter());
    return options;
}

static void CheckObjectSelectors()
{
    var objects = new[]
    {
        Object("object-1", "plc-1", "Program blocks/A/Duplicate", "Duplicate", "FB"),
        Object("object-2", "plc-1", "Program blocks/B/Duplicate", "Duplicate", "FB"),
        Object("object-3", "plc-2", "Program blocks/Duplicate", "Duplicate", "FB"),
    };

    var byId = V1Selector.ResolveObject(
        objects,
        "plc-1",
        new V1ObjectSelector { ObjectId = "object-1" },
        identity => identity);
    Equal("object-1", byId.ObjectId);

    var byPath = V1Selector.ResolveObject(
        objects,
        "plc-1",
        new V1ObjectSelector { Path = @"program BLOCKS\b\duplicate", Type = "fb" },
        identity => identity);
    Equal("object-2", byPath.ObjectId);

    var ambiguity = ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            objects,
            "plc-1",
            new V1ObjectSelector { Name = "Duplicate" },
            identity => identity),
        V1ErrorCodes.AmbiguousSelector);
    Equal(2, ambiguity.Error.Candidates.Count);

    ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            objects,
            "plc-1",
            new V1ObjectSelector { ObjectId = "object-3" },
            identity => identity),
        V1ErrorCodes.ObjectNotFound);

    ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            objects,
            "plc-1",
            new V1ObjectSelector { Name = "Missing" },
            identity => identity),
        V1ErrorCodes.ObjectNotFound);
}

static void CheckSelectorEdgeCases()
{
    var objects = new[]
    {
        Object("Opaque-ID", "plc-1", "Program blocks/Shared", "Shared", "FB"),
        Object("other-id", "plc-1", "Data types/Shared", "Shared", "UDT"),
    };

    ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            objects,
            "plc-1",
            new V1ObjectSelector { ObjectId = "opaque-id" },
            identity => identity),
        V1ErrorCodes.ObjectNotFound);

    var byNameAndType = V1Selector.ResolveObject(
        objects,
        "plc-1",
        new V1ObjectSelector { Name = "shared", Type = "udt" },
        identity => identity);
    Equal("other-id", byNameAndType.ObjectId);

    var byTrailingPath = V1Selector.ResolveObject(
        objects,
        "plc-1",
        new V1ObjectSelector { Path = "/program blocks/shared/" },
        identity => identity);
    Equal("Opaque-ID", byTrailingPath.ObjectId);

    ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            objects,
            "plc-1",
            new V1ObjectSelector(),
            identity => identity),
        V1ErrorCodes.InvalidSelector);
    ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            objects,
            "plc-1",
            new V1ObjectSelector { Type = "FB" },
            identity => identity),
        V1ErrorCodes.InvalidSelector);

    var duplicateIdentifierObjects = new[]
    {
        Object("duplicate-id", "plc-1", "Program blocks/A", "A", "FB"),
        Object("duplicate-id", "plc-1", "Program blocks/B", "B", "FB"),
    };
    ThrowsBridgeCode(
        () => V1Selector.ResolveObject(
            duplicateIdentifierObjects,
            "plc-1",
            new V1ObjectSelector { ObjectId = "duplicate-id" },
            identity => identity),
        V1ErrorCodes.AmbiguousSelector);
}

static void CheckPlcSelectors()
{
    var plcs = new[]
    {
        new V1PlcIdentity { ObjectId = "plc-1", Name = "CPU", Path = "Station A/CPU" },
        new V1PlcIdentity { ObjectId = "plc-2", Name = "CPU", Path = "Station B/CPU" },
    };

    var byId = V1Selector.ResolvePlc(
        plcs,
        new V1PlcSelector { ObjectId = "plc-2" },
        identity => identity);
    Equal("plc-2", byId.ObjectId);

    ThrowsBridgeCode(
        () => V1Selector.ResolvePlc(
            plcs,
            new V1PlcSelector { Name = "CPU" },
            identity => identity),
        V1ErrorCodes.AmbiguousSelector);

    ThrowsBridgeCode(
        () => V1Selector.ResolvePlc(
            plcs,
            new V1PlcSelector { ObjectId = "plc-missing" },
            identity => identity),
        V1ErrorCodes.ObjectNotFound);

    ThrowsBridgeCode(
        () => V1Selector.ResolvePlc(
            plcs,
            new V1PlcSelector(),
            identity => identity),
        V1ErrorCodes.InvalidSelector);

    ThrowsBridgeCode(
        () => V1Selector.ResolvePlc(
            plcs,
            new V1PlcSelector { ObjectId = "PLC-2" },
            identity => identity),
        V1ErrorCodes.ObjectNotFound);

    var duplicateIdentifiers = new[]
    {
        new V1PlcIdentity { ObjectId = "same-id", Name = "CPU A" },
        new V1PlcIdentity { ObjectId = "same-id", Name = "CPU B" },
    };
    ThrowsBridgeCode(
        () => V1Selector.ResolvePlc(
            duplicateIdentifiers,
            new V1PlcSelector { ObjectId = "same-id" },
            identity => identity),
        V1ErrorCodes.AmbiguousSelector);
}

static void CheckProjectSelectionPolicy()
{
    var root = Path.Combine(Path.GetTempPath(), "v1-project-policy");
    var demo = Path.Combine(root, "Demo.ap20");
    var sameCanonical = Path.Combine(root, "nested", "..", "DEMO.ap20");
    var other = Path.Combine(root, "Other.ap20");

    Equal(
        V1ProjectSelectionActions.Reuse,
        V1ProjectSelectionPolicy.Decide(demo, null, Array.Empty<string>()).Action);
    Equal(
        V1ProjectSelectionActions.Reuse,
        V1ProjectSelectionPolicy.Decide(demo, sameCanonical, Array.Empty<string>()).Action);
    Equal(
        V1ProjectSelectionActions.Conflict,
        V1ProjectSelectionPolicy.Decide(demo, other, Array.Empty<string>()).Action);

    var exact = V1ProjectSelectionPolicy.Decide(null, demo, new[] { other, sameCanonical });
    Equal(V1ProjectSelectionActions.AttachExact, exact.Action);
    EqualIgnoringCase(V1ProjectSelectionPolicy.CanonicalizePath(demo), exact.SelectedPath);

    Equal(
        V1ProjectSelectionActions.OpenVisible,
        V1ProjectSelectionPolicy.Decide(null, demo, new[] { other }).Action);
    Equal(
        V1ProjectSelectionActions.NoActive,
        V1ProjectSelectionPolicy.Decide(null, null, Array.Empty<string>()).Action);
    Equal(
        V1ProjectSelectionActions.AttachExact,
        V1ProjectSelectionPolicy.Decide(null, null, new[] { demo }).Action);
    Equal(
        V1ProjectSelectionActions.Ambiguity,
        V1ProjectSelectionPolicy.Decide(null, null, new[] { demo, other }).Action);
    Equal(
        V1ProjectSelectionActions.Ambiguity,
        V1ProjectSelectionPolicy.Decide(null, demo, new[] { demo, sameCanonical }).Action);
}

static void CheckProjectSelectionEdgeCases()
{
    var root = Path.Combine(Path.GetTempPath(), "v1-project-policy-edges");
    var active = Path.Combine(root, "Active.ap20");
    var requested = Path.Combine(root, "Requested.ap20");
    var unrelated = Path.Combine(root, "Unrelated.ap20");

    var conflict = V1ProjectSelectionPolicy.Decide(active, requested, new[] { requested });
    Equal(V1ProjectSelectionActions.Conflict, conflict.Action);
    Equal(null, conflict.SelectedPath);

    var reuse = V1ProjectSelectionPolicy.Decide(active, null, new[] { requested, unrelated });
    Equal(V1ProjectSelectionActions.Reuse, reuse.Action);
    EqualIgnoringCase(V1ProjectSelectionPolicy.CanonicalizePath(active), reuse.SelectedPath);

    var extendedPath = @"\\?\" + active;
    Equal(
        V1ProjectSelectionActions.Reuse,
        V1ProjectSelectionPolicy.Decide(active, extendedPath, Array.Empty<string>()).Action);

    Equal(
        V1ProjectSelectionActions.NoActive,
        V1ProjectSelectionPolicy.Decide(null, null, null).Action);
    Equal(
        V1ProjectSelectionActions.NoActive,
        V1ProjectSelectionPolicy.Decide(null, null, new string?[] { null, "  " }).Action);

    var exactAmbiguity = V1ProjectSelectionPolicy.Decide(
        null,
        requested,
        new[] { requested, unrelated, @"\\?\" + requested });
    Equal(V1ProjectSelectionActions.Ambiguity, exactAmbiguity.Action);
    Equal(2, exactAmbiguity.CandidatePaths.Count);

    var openVisible = V1ProjectSelectionPolicy.Decide(null, requested, new[] { unrelated });
    Equal(V1ProjectSelectionActions.OpenVisible, openVisible.Action);
    EqualIgnoringCase(V1ProjectSelectionPolicy.CanonicalizePath(requested), openVisible.SelectedPath);
}

static V1ObjectIdentity Object(string objectId, string plcObjectId, string path, string name, string type) => new()
{
    ObjectId = objectId,
    PlcObjectId = plcObjectId,
    Path = path,
    Name = name,
    Type = type,
};

static void PlanEquals(
    IReadOnlyList<V1RepresentationPlanStep> actual,
    params (string Format, string Applicability)[] expected)
{
    Equal(expected.Length, actual.Count);
    for (var index = 0; index < expected.Length; index++)
    {
        Equal(expected[index].Format, actual[index].Format);
        Equal(expected[index].Applicability, actual[index].Applicability);
    }
}

static V1BridgeException ThrowsBridgeCode(Action action, string expectedCode)
{
    try
    {
        action();
    }
    catch (V1BridgeException exception)
    {
        Equal(expectedCode, exception.Error.Code);
        return exception;
    }

    throw new InvalidOperationException($"Expected V1BridgeException with code '{expectedCode}'.");
}

static TException Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}

static void NotEqual<T>(T unexpected, T actual)
{
    if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        throw new InvalidOperationException($"Did not expect '{actual}'.");
}

static void EqualIgnoringCase(string expected, string? actual)
{
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Expected '{expected}' (case-insensitive), received '{actual}'.");
}

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value)
        throw new InvalidOperationException("Expected false.");
}
