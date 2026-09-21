using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TiaOpennessMcpServer.Models;

public static class V1RepresentationFormats
{
    public const string Best = "best";
    public const string SimaticSd = "simatic-sd";
    public const string SclSource = "scl-source";
    public const string SimaticMl = "simaticml";
}

public static class V1AttemptResults
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Partial = "partial";
    public const string Unsupported = "unsupported";
    public const string NotApplicable = "not_applicable";
}

public static class V1Authority
{
    public const string NativeExport = "native-export";
    public const string DirectOpennessView = "direct-openness-view";
    public const string NativeMetadata = "native-metadata";
    public const string NativeCrossReference = "native-cross-reference";
}

public static class V1Completeness
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

public static class V1ContentScopes
{
    public const string FullNativeRepresentation = "full-native-representation";
    public const string TiaExposedProtectedView = "tia-exposed-protected-view";
    public const string ProtectionUnknown = "native-representation-protection-unknown";
}

public static class V1ProtectionStates
{
    public const string Protected = "protected";
    public const string Unprotected = "unprotected";
    public const string Unknown = "unknown";
}

public static class V1ProtectionTypes
{
    public const string KnowHow = "know-how";
}

public static class V1ProtectionAccess
{
    public const string NativeFull = "native-full";
    public const string NativeLimited = "native-limited";
    public const string Unknown = "unknown";
}

public static class V1ObjectTypes
{
    public const string OrganizationBlock = "OB";
    public const string FunctionBlock = "FB";
    public const string Function = "FC";
    public const string DataBlock = "DB";
    public const string GlobalDataBlock = "global-db";
    public const string InstanceDataBlock = "instance-db";
    public const string ArrayDataBlock = "array-db";
    public const string PlcDataType = "UDT";
    public const string PlcTagTable = "tag-table";
    public const string Group = "group";
    public const string Tag = "tag";
    public const string UserConstant = "user-constant";
    public const string SystemConstant = "system-constant";
}

public sealed record V1InstalledProduct
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? ProductCode { get; init; }
    public IReadOnlyList<V1InstalledProduct> Options { get; init; } = Array.Empty<V1InstalledProduct>();
}

public sealed record V1TiaIdentity
{
    public string? PortalVersion { get; init; }
    public required IReadOnlyList<V1InstalledProduct> InstalledProducts { get; init; }
}

public sealed record V1ProjectIdentity
{
    public required string Name { get; init; }
    public string? Path { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Version { get; init; }
    public bool? IsModified { get; init; }
}

public sealed record V1PlcIdentity
{
    public string? ObjectId { get; init; }
    public required string Name { get; init; }
    public string? DeviceName { get; init; }
    public string? Path { get; init; }
}

public sealed record V1ObjectIdentity
{
    public string? ObjectId { get; init; }
    public string? PlcObjectId { get; init; }
    public string? ParentObjectId { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Language { get; init; }
    public int? Number { get; init; }
}

public sealed record V1Provenance
{
    public required DateTimeOffset ReadAtUtc { get; init; }
    public V1TiaIdentity? Tia { get; init; }
    public V1ProjectIdentity? Project { get; init; }
    public V1PlcIdentity? Plc { get; init; }
    public V1ObjectIdentity? Object { get; init; }
}

public sealed record V1Checksum
{
    public required string Algorithm { get; init; }
    public required string Encoding { get; init; }
    public required string Value { get; init; }
    public required long ByteLength { get; init; }
    public string? Scheme { get; init; }
}

public sealed record V1Document
{
    public required string FileName { get; init; }
    public string? Role { get; init; }
    public string? MediaType { get; init; }
    public required string Content { get; init; }
    public required long ByteLength { get; init; }
    public required V1Checksum Checksum { get; init; }
}

public sealed record V1Representation
{
    public required string Format { get; init; }
    public required string Authority { get; init; }
    public required string Completeness { get; init; }
    public required bool Complete { get; init; }
    public required string ContentScope { get; init; }
    public required IReadOnlyList<V1Document> Documents { get; init; }
    public V1Checksum? BundleChecksum { get; init; }
    public IReadOnlyList<string> NativeMessages { get; init; } = Array.Empty<string>();
}

public sealed record V1Protection
{
    public required string State { get; init; }
    public bool? IsProtected { get; init; }
    public string? Type { get; init; }
    public required string Access { get; init; }
    public string? ContentLimitation { get; init; }
}

public sealed record V1Attempt
{
    public required string Format { get; init; }
    public required string Result { get; init; }
    public required string Reason { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<string> NativeMessages { get; init; } = Array.Empty<string>();
}

public sealed record V1RepresentationRequest
{
    public required string Format { get; init; }
}

public sealed record V1ObjectSelector
{
    public string? ObjectId { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
}

public sealed record V1PlcSelector
{
    public string? ObjectId { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
}

public sealed record V1SelectionCandidate
{
    public string? ObjectId { get; init; }
    public string? PlcObjectId { get; init; }
    public string? Path { get; init; }
    public required string Name { get; init; }
    public string? Type { get; init; }
}

public sealed record V1ConnectionResponse
{
    public required V1Provenance Provenance { get; init; }
    public required bool Connected { get; init; }
    public required string Action { get; init; }
}

public sealed record V1StatusResponse
{
    public required V1Provenance Provenance { get; init; }
    public required bool Connected { get; init; }
    public required string AccessProfile { get; init; }
    public required bool WriteToolsAvailable { get; init; }
}

public sealed record V1Device
{
    public required string Name { get; init; }
    public string? ObjectId { get; init; }
    public string? Path { get; init; }
    public required bool PlcCapable { get; init; }
    public required IReadOnlyList<V1PlcIdentity> Plcs { get; init; }
    public V1PlcIdentity? Plc { get; init; }
    public string? TypeIdentifier { get; init; }
}

public sealed record V1ListDevicesResponse
{
    public required V1Provenance Provenance { get; init; }
    public required string Authority { get; init; }
    public required string Completeness { get; init; }
    public required IReadOnlyList<V1Device> Devices { get; init; }
}

public sealed record V1PlcObjectNode
{
    public required V1ObjectIdentity Identity { get; init; }
    public required bool IsGroup { get; init; }
    public bool? IsSystemGroup { get; init; }
    public bool? IsProtected { get; init; }
    public bool? IsSafety { get; init; }
    public bool? IsSystemGenerated { get; init; }
    public bool? IsConsistent { get; init; }
    public bool? ContentAvailable { get; init; }
    public string? ContentLimitation { get; init; }
    public IReadOnlyList<V1PlcObjectNode> Children { get; init; } = Array.Empty<V1PlcObjectNode>();
}

public sealed record V1ListPlcObjectsResponse
{
    public required V1Provenance Provenance { get; init; }
    public required V1PlcIdentity Plc { get; init; }
    public required string Authority { get; init; }
    public required string Completeness { get; init; }
    public required IReadOnlyList<V1PlcObjectNode> Objects { get; init; }
}

public sealed record V1PlcObjectSearchMatch
{
    public required V1ObjectIdentity Identity { get; init; }
    public V1ObjectIdentity? ParentTagTable { get; init; }
    public string? EntryKind { get; init; }
    public string? DataType { get; init; }
    public string? AddressOrValue { get; init; }
}

public sealed record V1FindPlcObjectsResponse
{
    public required V1Provenance Provenance { get; init; }
    public required string Query { get; init; }
    public required string Authority { get; init; }
    public required string Completeness { get; init; }
    public required IReadOnlyList<V1PlcObjectSearchMatch> Matches { get; init; }
}

public sealed record V1ReadPlcObjectResponse
{
    public required V1Provenance Provenance { get; init; }
    public required V1RepresentationRequest Request { get; init; }
    public required V1Protection Protection { get; init; }
    public required V1Representation Representation { get; init; }
    public required IReadOnlyList<V1Attempt> Attempts { get; init; }
}

public sealed record V1TagEntry
{
    public string? ObjectId { get; init; }
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public string? Address { get; init; }
    public bool? Accessible { get; init; }
    public bool? Writable { get; init; }
    public bool? Visible { get; init; }
    public bool? IsSafety { get; init; }
    public string? Comment { get; init; }
}

public sealed record V1ConstantEntry
{
    public string? ObjectId { get; init; }
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public string? Value { get; init; }
    public string? Comment { get; init; }
}

public sealed record V1TagTableEntriesResponse
{
    public required V1Provenance Provenance { get; init; }
    public required V1ObjectIdentity Table { get; init; }
    public required string Authority { get; init; }
    public required string Completeness { get; init; }
    public string? SelectedLanguage { get; init; }
    public required IReadOnlyList<V1TagEntry> Tags { get; init; }
    public required IReadOnlyList<V1ConstantEntry> UserConstants { get; init; }
    public required IReadOnlyList<V1ConstantEntry> SystemConstants { get; init; }
}

public sealed record V1CrossReference
{
    public required string Direction { get; init; }
    public V1ObjectIdentity? SourceObject { get; init; }
    public string? SourceDevice { get; init; }
    public string? SourceAddress { get; init; }
    public V1ObjectIdentity? ReferencedObject { get; init; }
    public string? ReferencedDevice { get; init; }
    public string? ReferencedAddress { get; init; }
    public string? Location { get; init; }
    public string? ReferenceLocation { get; init; }
    public string? LocationName { get; init; }
    public string? LocationTypeName { get; init; }
    public string? LocationAddress { get; init; }
    public string? Path { get; init; }
    public string? Address { get; init; }
    public string? ReferenceType { get; init; }
    public string? AccessType { get; init; }
    public string? ReferencedAs { get; init; }
    public string? ReferencedAsName { get; init; }
    public string? ReferencedAsObjectId { get; init; }
}

public sealed record V1CrossReferencesResponse
{
    public required V1Provenance Provenance { get; init; }
    public required V1ObjectIdentity Target { get; init; }
    public required V1Protection Protection { get; init; }
    public required string Authority { get; init; }
    public required string Completeness { get; init; }
    public required IReadOnlyList<V1CrossReference> Uses { get; init; }
    public required IReadOnlyList<V1CrossReference> UsedBy { get; init; }
}

public static class V1ErrorCodes
{
    public const string InvalidRequest = "invalid-request";
    public const string InvalidSelector = "invalid-selector";
    public const string ObjectNotFound = "object-not-found";
    public const string AmbiguousSelector = "ambiguous-selector";
    public const string AmbiguousProject = "ambiguous-project";
    public const string UnsupportedObject = "unsupported-object";
    public const string UnsupportedFormat = "unsupported-format";
    public const string ProtectedContent = "protected-content";
    public const string ExportFailed = "native-export-failed";
    public const string PartialExport = "partial-native-export";
    public const string MissingProductOrOption = "missing-product-or-option";
    public const string UiAuthenticationRequired = "ui-authentication-required";
    public const string IncompatibleProject = "incompatible-project";
    public const string UpgradeRequired = "project-upgrade-required";
    public const string ProjectConflict = "project-conflict";
    public const string NoActiveProject = "no-active-project";
    public const string TiaOperationFailed = "tia-operation-failed";
}

public sealed record V1Error
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public V1PlcIdentity? Plc { get; init; }
    public V1ObjectIdentity? Object { get; init; }
    public V1Protection? Protection { get; init; }
    public IReadOnlyList<V1SelectionCandidate> Candidates { get; init; } = Array.Empty<V1SelectionCandidate>();
    public IReadOnlyList<string> NativeMessages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<V1Attempt> Attempts { get; init; } = Array.Empty<V1Attempt>();
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}

public sealed record V1ErrorEnvelope
{
    public V1Provenance? Provenance { get; init; }
    public required V1Error Error { get; init; }
}

public sealed class V1BridgeException : Exception
{
    public V1BridgeException(V1Error error, V1Provenance? provenance = null)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
        Provenance = provenance;
    }

    public V1BridgeException(V1Error error, V1Provenance? provenance, Exception innerException)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message, innerException)
    {
        Error = error;
        Provenance = provenance;
    }

    public V1Error Error { get; }
    public V1Provenance? Provenance { get; }

    public V1ErrorEnvelope ToEnvelope() => new()
    {
        Provenance = Provenance,
        Error = Error,
    };
}
