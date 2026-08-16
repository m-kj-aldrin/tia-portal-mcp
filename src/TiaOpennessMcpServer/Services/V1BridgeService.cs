using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Canonical version-one read facade. Every public call creates a fresh live
/// context on the STA thread; no inventory, source, search, or cross-reference
/// data survives the call.
/// </summary>
public sealed class V1BridgeService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly TiaOpennessOptions _options;
    private readonly ILogger<V1BridgeService> _log;

    public V1BridgeService(
        TiaPortalService tia,
        StaTaskScheduler sta,
        IOptions<TiaOpennessOptions> options,
        ILogger<V1BridgeService> log)
    {
        _tia = tia;
        _sta = sta;
        _options = options.Value;
        _log = log;
    }

    public async Task<V1ListDevicesResponse> ListDevicesAsync()
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var context = CreateContext();
            var devices = V1TiaHelpers.EnumerateDevices(context.Project)
                .Select(device =>
                {
                    var plcs = device.Plcs
                        .Select(plc => V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers))
                        .ToList();
                    return new V1Device
                    {
                        Name = V1TiaHelpers.Try(() => device.Device.Name) ?? "<unknown device>",
                        ObjectId = V1TiaHelpers.TryGetIdentifier(context.Identifiers, device.Device),
                        Path = device.Path,
                        PlcCapable = plcs.Count > 0,
                        Plc = plcs.Count == 1 ? plcs[0] : null,
                        Plcs = plcs,
                        TypeIdentifier = V1TiaHelpers.Try(() => device.Device.TypeIdentifier),
                    };
                })
                .ToList();

            return new V1ListDevicesResponse
            {
                Provenance = V1ProvenanceFactory.Create(_tia),
                Authority = V1Authority.NativeMetadata,
                Completeness = V1Completeness.Complete,
                Devices = devices,
            };
        });
    }

    public async Task<V1ListPlcObjectsResponse> ListPlcObjectsAsync(string plcSelector)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var context = CreateContext();
            var plc = ResolvePlc(context, plcSelector);
            var plcIdentity = V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers);
            var inventory = BuildInventory(context, plc, plcIdentity);

            return new V1ListPlcObjectsResponse
            {
                Provenance = V1ProvenanceFactory.Create(_tia, plcIdentity),
                Plc = plcIdentity,
                Authority = V1Authority.NativeMetadata,
                Completeness = V1Completeness.Complete,
                Objects = inventory.Roots
                    .Select(node => MapHierarchy(node, inventory.PlcObjectId))
                    .ToList(),
            };
        });
    }

    public async Task<V1FindPlcObjectsResponse> FindPlcObjectsAsync(
        string plcSelector,
        string? query,
        string? type = null,
        string? language = null,
        string? group = null)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var context = CreateContext();
            var plc = ResolvePlc(context, plcSelector);
            var plcIdentity = V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers);
            var inventory = BuildInventory(context, plc, plcIdentity);
            var normalizedQuery = query?.Trim() ?? "";
            var matches = new List<V1PlcObjectSearchMatch>();

            foreach (var handle in inventory.Objects)
            {
                var identity = ObjectIdentity(handle, inventory.PlcObjectId);
                if (!MatchesSearch(identity, normalizedQuery, type, language, group))
                    continue;
                matches.Add(new V1PlcObjectSearchMatch { Identity = identity });
            }

            IReadOnlyList<EntryHandle> entries;
            try
            {
                entries = EnumerateEntryHandles(inventory, context.Identifiers);
            }
            catch (Exception ex)
            {
                throw NativeOperationError(
                    ex,
                    "Live tag and constant metadata traversal failed.",
                    plcIdentity);
            }

            foreach (var entry in entries)
            {
                var identity = ObjectIdentity(entry.Handle, inventory.PlcObjectId);
                if (!MatchesSearch(identity, normalizedQuery, type, language, group))
                    continue;
                matches.Add(new V1PlcObjectSearchMatch
                {
                    Identity = identity,
                    ParentTagTable = ObjectIdentity(entry.ParentTable, inventory.PlcObjectId),
                    EntryKind = identity.Type,
                    DataType = entry.DataType,
                    AddressOrValue = entry.AddressOrValue,
                });
            }

            return new V1FindPlcObjectsResponse
            {
                Provenance = V1ProvenanceFactory.Create(_tia, plcIdentity),
                Query = normalizedQuery,
                Authority = V1Authority.NativeMetadata,
                Completeness = V1Completeness.Complete,
                Matches = matches
                    .OrderBy(match => match.Identity.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        });
    }

    public async Task<V1ReadPlcObjectResponse> ReadPlcObjectAsync(
        string plcSelector,
        V1ObjectSelector selector,
        string? requestedFormat)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var context = CreateContext();
            var plc = ResolvePlc(context, plcSelector);
            var plcIdentity = V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers);
            var inventory = BuildInventory(context, plc, plcIdentity);
            var resolved = ResolveObject(context, inventory, plcIdentity, selector);
            var objectIdentity = ObjectIdentity(resolved, inventory.PlcObjectId);

            string normalizedFormat;
            IReadOnlyList<V1RepresentationPlanStep> plan;
            try
            {
                normalizedFormat = V1RepresentationPolicy.NormalizeFormat(requestedFormat);
                plan = V1RepresentationPolicy.CreatePlan(
                    resolved.Type,
                    resolved.Language,
                    normalizedFormat);
            }
            catch (V1BridgeException ex)
            {
                throw ReadError(
                    ex.Error.Code,
                    ex.Error.Message,
                    plcIdentity,
                    objectIdentity,
                    ex.Error.Attempts,
                    ex.Error.NativeMessages,
                    ex);
            }

            var attempts = new List<V1Attempt>();
            RepresentationAttempt? lastAttempt = null;
            foreach (var step in plan)
            {
                if (!step.IsApplicable)
                {
                    var result = string.Equals(
                        step.Applicability,
                        V1PlanApplicability.NotApplicable,
                        StringComparison.Ordinal)
                        ? V1AttemptResults.NotApplicable
                        : V1AttemptResults.Unsupported;
                    var attempt = new V1Attempt
                    {
                        Format = step.Format,
                        Result = result,
                        Reason = step.Reason ?? "The requested representation does not apply to this object.",
                    };
                    attempts.Add(attempt);
                    lastAttempt = new RepresentationAttempt
                    {
                        Attempt = attempt,
                        ErrorCode = V1ErrorCodes.UnsupportedFormat,
                    };

                    if (!string.Equals(
                            normalizedFormat,
                            V1RepresentationFormats.Best,
                            StringComparison.Ordinal))
                    {
                        throw ReadError(
                            V1ErrorCodes.UnsupportedFormat,
                            attempt.Reason,
                            plcIdentity,
                            objectIdentity,
                            attempts);
                    }
                    continue;
                }

                RepresentationAttempt outcome;
                try
                {
                    outcome = ReadRepresentation(resolved, step.Format);
                }
                catch (Exception ex)
                {
                    outcome = FailedAttempt(
                        step.Format,
                        V1AttemptResults.Failed,
                        "The native representation attempt could not be completed or validated.",
                        V1ErrorCodes.ExportFailed,
                        exception: ex);
                }
                attempts.Add(outcome.Attempt);
                lastAttempt = outcome;
                if (outcome.Representation is not null)
                {
                    return new V1ReadPlcObjectResponse
                    {
                        Provenance = V1ProvenanceFactory.Create(
                            _tia,
                            plcIdentity,
                            objectIdentity),
                        Request = new V1RepresentationRequest { Format = normalizedFormat },
                        Representation = outcome.Representation,
                        Attempts = attempts,
                    };
                }

                if (!string.Equals(
                        normalizedFormat,
                        V1RepresentationFormats.Best,
                        StringComparison.Ordinal))
                {
                    throw ReadError(
                        outcome.ErrorCode,
                        outcome.Attempt.Reason,
                        plcIdentity,
                        objectIdentity,
                        attempts,
                        outcome.Attempt.NativeMessages,
                        outcome.Exception);
                }
            }

            var errorCode = lastAttempt?.ErrorCode ?? V1ErrorCodes.ExportFailed;
            var nativeMessages = attempts
                .SelectMany(attempt => attempt.NativeMessages)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            throw ReadError(
                errorCode,
                "No complete native representation could be read for the selected PLC object.",
                plcIdentity,
                objectIdentity,
                attempts,
                nativeMessages,
                lastAttempt?.Exception);
        });
    }

    public async Task<V1TagTableEntriesResponse> GetTagTableEntriesAsync(
        string plcSelector,
        V1ObjectSelector selector)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var context = CreateContext();
            var plc = ResolvePlc(context, plcSelector);
            var plcIdentity = V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers);
            var inventory = BuildInventory(context, plc, plcIdentity);
            var resolved = ResolveObject(context, inventory, plcIdentity, selector);
            if (resolved.EngineeringObject is not PlcTagTable table)
            {
                throw ReadError(
                    V1ErrorCodes.UnsupportedObject,
                    "get_tag_table_entries requires a PLC tag-table selector.",
                    plcIdentity,
                    ObjectIdentity(resolved, inventory.PlcObjectId));
            }

            var tableIdentity = ObjectIdentity(resolved, inventory.PlcObjectId);
            var tags = new List<V1TagEntry>();
            var userConstants = new List<V1ConstantEntry>();
            var systemConstants = new List<V1ConstantEntry>();
            try
            {
                foreach (PlcTag tag in table.Tags)
                {
                    tags.Add(new V1TagEntry
                    {
                        ObjectId = V1TiaHelpers.TryGetIdentifier(context.Identifiers, tag),
                        Name = V1TiaHelpers.Try(() => tag.Name) ?? "<unreadable tag>",
                        DataType = V1TiaHelpers.Try(() => tag.DataTypeName),
                        Address = V1TiaHelpers.Try(() => tag.LogicalAddress),
                        Accessible = V1TiaHelpers.Try<bool?>(() => tag.ExternalAccessible),
                        Visible = V1TiaHelpers.Try<bool?>(() => tag.ExternalVisible),
                        Writable = V1TiaHelpers.Try<bool?>(() => tag.ExternalWritable),
                        IsSafety = V1TiaHelpers.Try<bool?>(() => tag.IsSafety),
                        Comment = V1TiaHelpers.ReadSelectedText(context.Project, () => tag.Comment),
                    });
                }

                foreach (PlcUserConstant constant in table.UserConstants)
                {
                    userConstants.Add(new V1ConstantEntry
                    {
                        ObjectId = V1TiaHelpers.TryGetIdentifier(context.Identifiers, constant),
                        Name = V1TiaHelpers.Try(() => constant.Name) ?? "<unreadable user constant>",
                        DataType = V1TiaHelpers.Try(() => constant.DataTypeName),
                        Value = V1TiaHelpers.Try(() => constant.Value),
                        Comment = V1TiaHelpers.ReadSelectedText(context.Project, () => constant.Comment),
                    });
                }

                foreach (PlcSystemConstant constant in table.SystemConstants)
                {
                    systemConstants.Add(new V1ConstantEntry
                    {
                        ObjectId = V1TiaHelpers.TryGetIdentifier(context.Identifiers, constant),
                        Name = V1TiaHelpers.Try(() => constant.Name) ?? "<unreadable system constant>",
                        DataType = V1TiaHelpers.Try(() => constant.DataTypeName),
                        Value = V1TiaHelpers.Try(() => constant.Value),
                    });
                }
            }
            catch (Exception ex)
            {
                throw NativeOperationError(
                    ex,
                    "Direct tag-table metadata traversal failed.",
                    plcIdentity,
                    tableIdentity);
            }

            return new V1TagTableEntriesResponse
            {
                Provenance = V1ProvenanceFactory.Create(_tia, plcIdentity, tableIdentity),
                Table = tableIdentity,
                Authority = V1Authority.DirectOpennessView,
                Completeness = "selected-fields",
                SelectedLanguage = V1TiaHelpers.EditingLanguage(context.Project),
                Tags = tags,
                UserConstants = userConstants,
                SystemConstants = systemConstants,
            };
        });
    }

    public async Task<V1CrossReferencesResponse> GetCrossReferencesAsync(
        string plcSelector,
        V1ObjectSelector selector)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var context = CreateContext();
            var plc = ResolvePlc(context, plcSelector);
            var plcIdentity = V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers);
            var inventory = BuildInventory(context, plc, plcIdentity);
            V1ObjectHandle resolved;
            try
            {
                resolved = ResolveObject(
                    context,
                    inventory,
                    plcIdentity,
                    selector,
                    includeEntries: true);
            }
            catch (V1BridgeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw NativeOperationError(
                    ex,
                    "Live cross-reference target resolution failed.",
                    plcIdentity);
            }
            var targetIdentity = ObjectIdentity(resolved, inventory.PlcObjectId);

            if (resolved.EngineeringObject is not IEngineeringServiceProvider serviceProvider)
            {
                throw ReadError(
                    V1ErrorCodes.UnsupportedObject,
                    "The selected object does not expose TIA Portal's native CrossReferenceService.",
                    plcIdentity,
                    targetIdentity);
            }

            CrossReferenceService? service;
            try
            {
                service = serviceProvider.GetService<CrossReferenceService>();
            }
            catch (Exception ex)
            {
                throw CrossReferenceError(ex, plcIdentity, targetIdentity);
            }
            if (service is null)
            {
                throw ReadError(
                    V1ErrorCodes.UnsupportedObject,
                    "TIA Portal did not provide CrossReferenceService for the selected object.",
                    plcIdentity,
                    targetIdentity);
            }

            CrossReferenceResult result;
            try
            {
                result = service.GetCrossReferences(CrossReferenceFilter.AllObjects);
            }
            catch (Exception ex)
            {
                throw CrossReferenceError(ex, plcIdentity, targetIdentity);
            }

            List<V1ObjectHandle> knownObjects;
            try
            {
                knownObjects = inventory.Objects
                    .Concat(EnumerateEntryHandles(inventory, context.Identifiers).Select(entry => entry.Handle))
                    .ToList();
            }
            catch (Exception ex)
            {
                throw NativeOperationError(
                    ex,
                    "Live reference-target metadata traversal failed.",
                    plcIdentity,
                    targetIdentity);
            }
            var uses = new List<V1CrossReference>();
            var usedBy = new List<V1CrossReference>();
            try
            {
                foreach (SourceObject source in result.Sources)
                {
                    AddCrossReferenceSource(
                        source,
                        context.Identifiers,
                        inventory.PlcObjectId,
                        knownObjects,
                        uses,
                        usedBy);
                }
            }
            catch (Exception ex)
            {
                throw CrossReferenceError(ex, plcIdentity, targetIdentity);
            }

            return new V1CrossReferencesResponse
            {
                Provenance = V1ProvenanceFactory.Create(
                    _tia,
                    plcIdentity,
                    targetIdentity),
                Target = targetIdentity,
                Authority = V1Authority.NativeCrossReference,
                Completeness = V1Completeness.Complete,
                Uses = uses,
                UsedBy = usedBy,
            };
        });
    }

    private RepresentationAttempt ReadRepresentation(
        V1ObjectHandle handle,
        string format) => format switch
    {
        V1RepresentationFormats.SimaticSd => ReadSimaticSd(handle),
        V1RepresentationFormats.SclSource => ReadSclSource(handle),
        V1RepresentationFormats.SimaticMl => ReadSimaticMl(handle),
        _ => UnsupportedAttempt(
            format,
            $"Representation format '{format}' is not supported."),
    };

    private RepresentationAttempt ReadSimaticSd(V1ObjectHandle handle)
    {
        if (handle.EngineeringObject is not PlcBlock &&
            handle.EngineeringObject is not PlcType)
        {
            return UnsupportedAttempt(
                V1RepresentationFormats.SimaticSd,
                "The selected object does not expose native SIMATIC SD export in version one.");
        }

        string? temporaryDirectory = null;
        try
        {
            try
            {
                temporaryDirectory = CreateTemporaryExportDirectory("simatic-sd");
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    "A temporary directory for native SIMATIC SD export could not be created.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            DocumentExportResult exportResult;
            try
            {
                var directory = new DirectoryInfo(temporaryDirectory);
                exportResult = handle.EngineeringObject switch
                {
                    PlcBlock block => block.ExportAsDocuments(directory, "object"),
                    PlcType type => type.ExportAsDocuments(directory, "object"),
                    _ => throw new InvalidOperationException(
                        "The resolved object does not expose SIMATIC SD export."),
                };
            }
            catch (Exception ex)
            {
                var nativeMessages = ExceptionMessages(ex, temporaryDirectory);
                var errorCode = MapExportFailureCode(ex, nativeMessages);
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    FailureReason(
                        errorCode,
                        "TIA Portal's native SIMATIC SD export operation failed.",
                        handle.IsProtected),
                    errorCode,
                    nativeMessages,
                    ex);
            }

            IReadOnlyList<string> messages;
            try
            {
                messages = exportResult.Messages
                    .Cast<DocumentResultMessage>()
                    .Select(message => V1TiaHelpers.SingleLine(
                        message.Message,
                        temporaryDirectory))
                    .Where(message => message.Length > 0)
                    .ToList();
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    "TIA Portal returned a SIMATIC SD result whose native messages could not be read.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            if (exportResult.State == DocumentResultState.PartialSuccess)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Partial,
                    "TIA Portal returned PartialSuccess; incomplete SIMATIC SD content was rejected.",
                    V1ErrorCodes.PartialExport,
                    messages);
            }
            if (exportResult.State != DocumentResultState.Success)
            {
                var errorCode = MapExportFailureCode(exception: null, messages);
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    FailureReason(
                        errorCode,
                        $"TIA Portal returned {exportResult.State} for native SIMATIC SD export.",
                        handle.IsProtected),
                    errorCode,
                    messages);
            }

            List<FileInfo> exportedFiles;
            try
            {
                exportedFiles = exportResult.ExportedDocuments
                    .Where(file => file is not null)
                    .Select(file => new FileInfo(file.FullName))
                    .OrderBy(file => file.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    "TIA Portal's list of exported SIMATIC SD documents could not be read.",
                    V1ErrorCodes.ExportFailed,
                    messages,
                    ex);
            }

            var sourceFiles = exportedFiles
                .Where(file => file.Extension.Equals(
                    ".s7dcl",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sourceFiles.Count != 1)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    "A complete SIMATIC SD representation requires exactly one .s7dcl source document.",
                    V1ErrorCodes.ExportFailed,
                    messages);
            }

            var duplicateFileName = exportedFiles
                .GroupBy(file => file.Name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateFileName is not null)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    $"TIA Portal returned duplicate SIMATIC SD filename '{duplicateFileName.Key}'.",
                    V1ErrorCodes.ExportFailed,
                    messages);
            }

            var unsupportedDocument = exportedFiles.FirstOrDefault(file =>
                !file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) &&
                !file.Extension.Equals(".s7res", StringComparison.OrdinalIgnoreCase));
            if (unsupportedDocument is not null)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticSd,
                    V1AttemptResults.Failed,
                    $"TIA Portal returned unsupported SIMATIC SD document type '{unsupportedDocument.Extension}'.",
                    V1ErrorCodes.ExportFailed,
                    messages);
            }

            var documents = new List<V1Document>();
            foreach (var file in exportedFiles)
            {
                string content;
                try
                {
                    EnsureTemporaryFile(temporaryDirectory, file.FullName);
                    if (!File.Exists(file.FullName))
                    {
                        return FailedAttempt(
                            V1RepresentationFormats.SimaticSd,
                            V1AttemptResults.Failed,
                            $"TIA Portal reported SIMATIC SD document '{file.Name}', but the file was not created.",
                            V1ErrorCodes.ExportFailed,
                            messages);
                    }
                    content = File.ReadAllText(file.FullName);
                }
                catch (Exception ex)
                {
                    return FailedAttempt(
                        V1RepresentationFormats.SimaticSd,
                        V1AttemptResults.Failed,
                        $"SIMATIC SD document '{file.Name}' could not be read completely.",
                        V1ErrorCodes.ExportFailed,
                        messages,
                        ex);
                }

                if (file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(content))
                {
                    return FailedAttempt(
                        V1RepresentationFormats.SimaticSd,
                        V1AttemptResults.Failed,
                        "TIA Portal produced an empty .s7dcl source document.",
                        V1ErrorCodes.ExportFailed,
                        messages);
                }

                var role = file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase)
                    ? "source"
                    : "resource";
                documents.Add(V1Checksums.CreateDocument(
                    file.Name,
                    content,
                    role,
                    "text/plain"));
            }

            documents = documents
                .OrderBy(document => document.Role == "source" ? 0 : 1)
                .ThenBy(document => document.FileName, StringComparer.Ordinal)
                .ToList();
            var representation = new V1Representation
            {
                Format = V1RepresentationFormats.SimaticSd,
                Authority = V1Authority.NativeExport,
                Completeness = V1Completeness.Complete,
                Complete = true,
                Documents = documents,
                BundleChecksum = V1Checksums.ForSimaticSdBundle(documents),
                NativeMessages = messages,
            };
            return SucceededAttempt(
                representation,
                "TIA Portal returned a complete native SIMATIC SD document export.",
                messages);
        }
        finally
        {
            CleanupTemporaryExportDirectory(temporaryDirectory);
        }
    }

    private RepresentationAttempt ReadSclSource(V1ObjectHandle handle)
    {
        if (handle.EngineeringObject is not PlcBlock block)
        {
            return UnsupportedAttempt(
                V1RepresentationFormats.SclSource,
                "Raw SCL source generation applies only to an OB, FB, or FC block.");
        }

        string? temporaryDirectory = null;
        try
        {
            try
            {
                temporaryDirectory = CreateTemporaryExportDirectory("scl-source");
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SclSource,
                    V1AttemptResults.Failed,
                    "A temporary directory for native SCL source generation could not be created.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            var sourceFile = new FileInfo(Path.Combine(temporaryDirectory, "object.scl"));
            try
            {
                handle.Scope.ExternalSourceGroup.GenerateSource(
                    new IGenerateSource[] { block },
                    sourceFile,
                    GenerateOptions.None);
            }
            catch (Exception ex)
            {
                var nativeMessages = ExceptionMessages(ex, temporaryDirectory);
                var errorCode = MapExportFailureCode(ex, nativeMessages);
                return FailedAttempt(
                    V1RepresentationFormats.SclSource,
                    V1AttemptResults.Failed,
                    FailureReason(
                        errorCode,
                        "TIA Portal's native raw SCL source generation failed.",
                        handle.IsProtected),
                    errorCode,
                    nativeMessages,
                    ex);
            }

            string content;
            try
            {
                EnsureTemporaryFile(temporaryDirectory, sourceFile.FullName);
                if (!File.Exists(sourceFile.FullName))
                {
                    return FailedAttempt(
                        V1RepresentationFormats.SclSource,
                        V1AttemptResults.Failed,
                        "TIA Portal completed raw SCL generation without creating the requested source file.",
                        V1ErrorCodes.ExportFailed);
                }
                content = File.ReadAllText(sourceFile.FullName);
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SclSource,
                    V1AttemptResults.Failed,
                    "The generated raw SCL source file could not be read completely.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return FailedAttempt(
                    V1RepresentationFormats.SclSource,
                    V1AttemptResults.Failed,
                    "TIA Portal generated an empty raw SCL source file.",
                    V1ErrorCodes.ExportFailed);
            }

            var representation = new V1Representation
            {
                Format = V1RepresentationFormats.SclSource,
                Authority = V1Authority.NativeExport,
                Completeness = V1Completeness.Complete,
                Complete = true,
                Documents = new[]
                {
                    V1Checksums.CreateDocument(
                        sourceFile.Name,
                        content,
                        "source",
                        "text/plain"),
                },
            };
            return SucceededAttempt(
                representation,
                "TIA Portal generated one complete native raw SCL source file.");
        }
        finally
        {
            CleanupTemporaryExportDirectory(temporaryDirectory);
        }
    }

    private RepresentationAttempt ReadSimaticMl(V1ObjectHandle handle)
    {
        if (handle.EngineeringObject is not PlcBlock &&
            handle.EngineeringObject is not PlcType &&
            handle.EngineeringObject is not PlcTagTable)
        {
            return UnsupportedAttempt(
                V1RepresentationFormats.SimaticMl,
                "The selected object does not expose native SimaticML export in version one.");
        }

        string? temporaryDirectory = null;
        try
        {
            try
            {
                temporaryDirectory = CreateTemporaryExportDirectory("simaticml");
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticMl,
                    V1AttemptResults.Failed,
                    "A temporary directory for native SimaticML export could not be created.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            var exportFile = new FileInfo(Path.Combine(temporaryDirectory, "object.xml"));
            try
            {
                switch (handle.EngineeringObject)
                {
                    case PlcBlock block:
                        block.Export(
                            exportFile,
                            ExportOptions.WithDefaults,
                            DocumentInfoOptions.None);
                        break;
                    case PlcType type:
                        type.Export(
                            exportFile,
                            ExportOptions.WithDefaults,
                            DocumentInfoOptions.None);
                        break;
                    case PlcTagTable table:
                        table.Export(
                            exportFile,
                            ExportOptions.WithDefaults,
                            DocumentInfoOptions.None);
                        break;
                }
            }
            catch (Exception ex)
            {
                var nativeMessages = ExceptionMessages(ex, temporaryDirectory);
                var errorCode = MapExportFailureCode(ex, nativeMessages);
                return FailedAttempt(
                    V1RepresentationFormats.SimaticMl,
                    V1AttemptResults.Failed,
                    FailureReason(
                        errorCode,
                        "TIA Portal's native SimaticML export operation failed.",
                        handle.IsProtected),
                    errorCode,
                    nativeMessages,
                    ex);
            }

            string content;
            try
            {
                EnsureTemporaryFile(temporaryDirectory, exportFile.FullName);
                if (!File.Exists(exportFile.FullName))
                {
                    return FailedAttempt(
                        V1RepresentationFormats.SimaticMl,
                        V1AttemptResults.Failed,
                        "TIA Portal completed SimaticML export without creating the requested XML document.",
                        V1ErrorCodes.ExportFailed);
                }
                content = File.ReadAllText(exportFile.FullName);
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticMl,
                    V1AttemptResults.Failed,
                    "The exported SimaticML document could not be read completely.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticMl,
                    V1AttemptResults.Failed,
                    "TIA Portal produced an empty SimaticML document.",
                    V1ErrorCodes.ExportFailed);
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticMl,
                    V1AttemptResults.Failed,
                    "TIA Portal produced a SimaticML document that was not well-formed XML.",
                    V1ErrorCodes.ExportFailed,
                    exception: ex);
            }

            if (!ContainsRequestedSimaticMlObject(document, handle))
            {
                return FailedAttempt(
                    V1RepresentationFormats.SimaticMl,
                    V1AttemptResults.Failed,
                    "The native SimaticML document did not contain the requested engineering object.",
                    V1ErrorCodes.ExportFailed);
            }

            var representation = new V1Representation
            {
                Format = V1RepresentationFormats.SimaticMl,
                Authority = V1Authority.NativeExport,
                Completeness = V1Completeness.Complete,
                Complete = true,
                Documents = new[]
                {
                    V1Checksums.CreateDocument(
                        exportFile.Name,
                        content,
                        "simaticml",
                        "application/xml"),
                },
            };
            return SucceededAttempt(
                representation,
                "TIA Portal returned one complete native SimaticML document.");
        }
        finally
        {
            CleanupTemporaryExportDirectory(temporaryDirectory);
        }
    }

    private static bool ContainsRequestedSimaticMlObject(
        XDocument document,
        V1ObjectHandle handle)
    {
        if (document.Root is null)
            return false;

        foreach (var candidate in document.Root.DescendantsAndSelf())
        {
            if (!IsExpectedSimaticMlElement(candidate.Name.LocalName, handle.EngineeringObject))
                continue;

            var exportedName = candidate.Elements()
                .Where(element => string.Equals(
                    element.Name.LocalName,
                    "AttributeList",
                    StringComparison.Ordinal))
                .SelectMany(element => element.Elements())
                .FirstOrDefault(element => string.Equals(
                    element.Name.LocalName,
                    "Name",
                    StringComparison.Ordinal))
                ?.Value;
            if (string.Equals(exportedName, handle.Name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsExpectedSimaticMlElement(
        string localName,
        IEngineeringObject engineeringObject) => engineeringObject switch
    {
        OB => string.Equals(localName, "SW.Blocks.OB", StringComparison.Ordinal),
        FB => string.Equals(localName, "SW.Blocks.FB", StringComparison.Ordinal),
        FC => string.Equals(localName, "SW.Blocks.FC", StringComparison.Ordinal),
        GlobalDB => string.Equals(localName, "SW.Blocks.GlobalDB", StringComparison.Ordinal),
        InstanceDB => string.Equals(localName, "SW.Blocks.InstanceDB", StringComparison.Ordinal),
        ArrayDB => string.Equals(localName, "SW.Blocks.ArrayDB", StringComparison.Ordinal),
        PlcBlock => localName.StartsWith("SW.Blocks.", StringComparison.Ordinal),
        PlcType => localName.StartsWith("SW.Types.", StringComparison.Ordinal),
        PlcTagTable => string.Equals(
            localName,
            "SW.Tags.PlcTagTable",
            StringComparison.Ordinal),
        _ => false,
    };

    private string CreateTemporaryExportDirectory(string representation)
    {
        var root = Path.GetFullPath(_options.ExportDirectory);
        var directory = Path.GetFullPath(Path.Combine(
            root,
            "v1-read",
            representation,
            Guid.NewGuid().ToString("N")));
        EnsureContainedPath(root, directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void CleanupTemporaryExportDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        try
        {
            var root = Path.GetFullPath(_options.ExportDirectory);
            var fullDirectory = Path.GetFullPath(directory);
            EnsureContainedPath(root, fullDirectory);
            Directory.Delete(fullDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Could not remove a temporary version-one export directory: {Message}",
                V1TiaHelpers.SingleLine(ex.Message, directory));
        }
    }

    private static void EnsureTemporaryFile(string directory, string fileName)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullFileName = Path.GetFullPath(fileName);
        EnsureContainedPath(fullDirectory, fullFileName);
    }

    private static void EnsureContainedPath(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child);
        if (!fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The native export returned a path outside its temporary directory.");
        }
    }

    private static RepresentationAttempt SucceededAttempt(
        V1Representation representation,
        string reason,
        IReadOnlyList<string>? nativeMessages = null) => new()
    {
        Attempt = new V1Attempt
        {
            Format = representation.Format,
            Result = V1AttemptResults.Succeeded,
            Reason = reason,
            NativeMessages = nativeMessages ?? Array.Empty<string>(),
        },
        Representation = representation,
        ErrorCode = V1ErrorCodes.ExportFailed,
    };

    private static RepresentationAttempt UnsupportedAttempt(
        string format,
        string reason) => new()
    {
        Attempt = new V1Attempt
        {
            Format = format,
            Result = V1AttemptResults.Unsupported,
            Reason = reason,
        },
        ErrorCode = V1ErrorCodes.UnsupportedFormat,
    };

    private static RepresentationAttempt FailedAttempt(
        string format,
        string result,
        string reason,
        string errorCode,
        IReadOnlyList<string>? nativeMessages = null,
        Exception? exception = null) => new()
    {
        Attempt = new V1Attempt
        {
            Format = format,
            Result = result,
            Reason = reason,
            NativeMessages = nativeMessages ?? Array.Empty<string>(),
        },
        ErrorCode = errorCode,
        Exception = exception,
    };

    private static IReadOnlyList<string> ExceptionMessages(
        Exception exception,
        string? temporaryDirectory)
    {
        var message = V1TiaHelpers.SingleLine(exception.Message, temporaryDirectory);
        return message.Length == 0
            ? Array.Empty<string>()
            : new[] { message };
    }

    private static string MapExportFailureCode(
        Exception? exception,
        IEnumerable<string> nativeMessages)
    {
        var text = string.Join(" ", nativeMessages);
        if (exception is MissingProductsException)
            return V1ErrorCodes.MissingProductOrOption;
        if (exception is EngineeringSecurityException)
            return V1ErrorCodes.UiAuthenticationRequired;
        if (ContainsAny(text, "know-how protected", "know how protected", "protected content"))
            return V1ErrorCodes.ProtectedContent;
        if (ContainsAny(text, "missing product", "not installed", "support package"))
            return V1ErrorCodes.MissingProductOrOption;
        if (ContainsAny(text, "authentication", "authenticate", "external access", "login"))
            return V1ErrorCodes.UiAuthenticationRequired;
        return V1ErrorCodes.ExportFailed;
    }

    private static string FailureReason(string errorCode, string fallback) => errorCode switch
    {
        V1ErrorCodes.ProtectedContent =>
            "TIA Portal reported protected or inaccessible native content.",
        V1ErrorCodes.MissingProductOrOption =>
            "TIA Portal reported a missing product, option, or support package.",
        V1ErrorCodes.UiAuthenticationRequired =>
            "TIA Portal requires external-access approval or interactive authentication in the visible UI.",
        _ => fallback,
    };

    private static string FailureReason(
        string errorCode,
        string fallback,
        bool? isKnowHowProtected)
    {
        var reason = FailureReason(errorCode, fallback);
        if (isKnowHowProtected == true &&
            (errorCode == V1ErrorCodes.ExportFailed ||
             errorCode == V1ErrorCodes.TiaOperationFailed))
        {
            return reason +
                   " Object metadata reports know-how protection, but TIA did not identify protection as the failure cause.";
        }
        return reason;
    }

    private V1BridgeException CrossReferenceError(
        Exception exception,
        V1PlcIdentity plcIdentity,
        V1ObjectIdentity targetIdentity)
    {
        var nativeMessages = ExceptionMessages(exception, temporaryDirectory: null);
        var text = string.Join(" ", nativeMessages);
        string code;
        if (exception is EngineeringNotSupportedException)
            code = V1ErrorCodes.UnsupportedObject;
        else if (exception is MissingProductsException)
            code = V1ErrorCodes.MissingProductOrOption;
        else if (exception is EngineeringSecurityException)
            code = V1ErrorCodes.UiAuthenticationRequired;
        else if (ContainsAny(text, "know-how protected", "know how protected", "protected content"))
            code = V1ErrorCodes.ProtectedContent;
        else if (ContainsAny(text, "missing product", "not installed", "support package"))
            code = V1ErrorCodes.MissingProductOrOption;
        else if (ContainsAny(text, "authentication", "authenticate", "external access", "login"))
            code = V1ErrorCodes.UiAuthenticationRequired;
        else
            code = V1ErrorCodes.TiaOperationFailed;
        return ReadError(
            code,
            FailureReason(
                code,
                "TIA Portal's native cross-reference query failed."),
            plcIdentity,
            targetIdentity,
            nativeMessages: nativeMessages,
            inner: exception);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.IndexOf(
            needle,
            StringComparison.OrdinalIgnoreCase) >= 0);

    private static void AddCrossReferenceSource(
        SourceObject source,
        ObjectIdentifierProvider identifiers,
        string? plcObjectId,
        IReadOnlyList<V1ObjectHandle> knownObjects,
        ICollection<V1CrossReference> uses,
        ICollection<V1CrossReference> usedBy)
    {
        var sourceIdentity = CrossReferenceIdentity(
            V1TiaHelpers.Try(() => source.UnderlyingObject),
            V1TiaHelpers.Try(() => source.Name),
            V1TiaHelpers.Try(() => source.Path),
            V1TiaHelpers.Try(() => source.TypeName),
            identifiers,
            plcObjectId,
            knownObjects);
        var sourceDevice = V1TiaHelpers.Try(() => source.Device);
        var sourceAddress = V1TiaHelpers.Try(() => source.Address);
        foreach (ReferenceObject reference in source.References)
        {
            var referencedIdentity = CrossReferenceIdentity(
                V1TiaHelpers.Try(() => reference.UnderlyingObject),
                V1TiaHelpers.Try(() => reference.Name),
                V1TiaHelpers.Try(() => reference.Path),
                V1TiaHelpers.Try(() => reference.TypeName),
                identifiers,
                plcObjectId,
                knownObjects);
            var referencedDevice = V1TiaHelpers.Try(() => reference.Device);
            var referencePath = V1TiaHelpers.Try(() => reference.Path);
            var referenceAddress = V1TiaHelpers.Try(() => reference.Address);
            var locations = reference.Locations.Cast<Location>().ToList();

            if (locations.Count == 0)
            {
                uses.Add(new V1CrossReference
                {
                    Direction = "uses",
                    SourceObject = sourceIdentity,
                    SourceDevice = sourceDevice,
                    SourceAddress = sourceAddress,
                    ReferencedObject = referencedIdentity,
                    ReferencedDevice = referencedDevice,
                    ReferencedAddress = referenceAddress,
                    Path = referencePath,
                    Address = referenceAddress,
                });
                continue;
            }

            foreach (var location in locations)
            {
                var referenceType = V1TiaHelpers.Try<ReferenceType?>(
                    () => location.ReferenceType);
                var access = V1TiaHelpers.Try<Access?>(() => location.Access);
                var referenceLocation = V1TiaHelpers.Try(
                    () => location.ReferenceLocation);
                var locationName = V1TiaHelpers.Try(() => location.Name);
                var locationTypeName = V1TiaHelpers.Try(() => location.TypeName);
                var locationAddress = V1TiaHelpers.Try(() => location.Address);
                var referencedAsName = V1TiaHelpers.Try(
                    () => location.ReferencedAsName);
                var referencedAsObject = V1TiaHelpers.Try(
                    () => location.ReferencedAs);
                var referencedAsObjectId = referencedAsObject is null
                    ? null
                    : V1TiaHelpers.TryGetIdentifier(identifiers, referencedAsObject);
                var isUsedBy = referenceType == ReferenceType.UsedBy;
                var item = new V1CrossReference
                {
                    Direction = isUsedBy ? "used-by" : "uses",
                    SourceObject = sourceIdentity,
                    SourceDevice = sourceDevice,
                    SourceAddress = sourceAddress,
                    ReferencedObject = referencedIdentity,
                    ReferencedDevice = referencedDevice,
                    ReferencedAddress = referenceAddress,
                    Location = FirstNonBlank(referenceLocation, locationName),
                    ReferenceLocation = referenceLocation,
                    LocationName = locationName,
                    LocationTypeName = locationTypeName,
                    LocationAddress = locationAddress,
                    Path = referencePath,
                    Address = FirstNonBlank(locationAddress, referenceAddress),
                    ReferenceType = referenceType?.ToString(),
                    AccessType = access?.ToString(),
                    ReferencedAs = FirstNonBlank(referencedAsName, referencedAsObjectId),
                    ReferencedAsName = referencedAsName,
                    ReferencedAsObjectId = referencedAsObjectId,
                };
                if (isUsedBy)
                    usedBy.Add(item);
                else
                    uses.Add(item);
            }
        }

        foreach (SourceObject child in source.Children)
        {
            AddCrossReferenceSource(
                child,
                identifiers,
                plcObjectId,
                knownObjects,
                uses,
                usedBy);
        }
    }

    private static V1ObjectIdentity? CrossReferenceIdentity(
        IEngineeringObject? underlyingObject,
        string? nativeName,
        string? nativePath,
        string? nativeType,
        ObjectIdentifierProvider identifiers,
        string? plcObjectId,
        IReadOnlyList<V1ObjectHandle> knownObjects)
    {
        string? objectId = null;
        if (underlyingObject is not null)
        {
            objectId = V1TiaHelpers.TryGetIdentifier(identifiers, underlyingObject);
            var known = objectId is null
                ? knownObjects.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.EngineeringObject, underlyingObject))
                : knownObjects.FirstOrDefault(candidate => string.Equals(
                    candidate.ObjectId,
                    objectId,
                    StringComparison.Ordinal));
            if (known is not null)
            {
                var knownIdentity = ObjectIdentity(known, plcObjectId);
                return new V1ObjectIdentity
                {
                    ObjectId = knownIdentity.ObjectId,
                    PlcObjectId = knownIdentity.PlcObjectId,
                    ParentObjectId = knownIdentity.ParentObjectId,
                    Path = FirstNonBlank(nativePath, knownIdentity.Path)!,
                    Name = FirstNonBlank(nativeName, knownIdentity.Name)!,
                    Type = FirstNonBlank(nativeType, knownIdentity.Type)!,
                    Language = knownIdentity.Language,
                    Number = knownIdentity.Number,
                };
            }
        }

        var underlyingName = underlyingObject is null
            ? null
            : V1TiaHelpers.Try(() =>
                underlyingObject.GetAttribute("Name")?.ToString());
        var name = FirstNonBlank(nativeName, underlyingName);
        var path = FirstNonBlank(nativePath, name);
        var type = FirstNonBlank(
            nativeType,
            underlyingObject is null ? null : EngineeringObjectType(underlyingObject));
        if (name is null && path is null && type is null && objectId is null)
            return null;

        name ??= path ?? objectId ?? "<unknown cross-reference object>";
        path ??= name;
        type ??= underlyingObject?.GetType().Name ?? "unknown";
        return new V1ObjectIdentity
        {
            ObjectId = objectId,
            Path = path,
            Name = name,
            Type = type,
            Language = underlyingObject is PlcBlock block
                ? V1TiaHelpers.Try(() => block.ProgrammingLanguage.ToString())
                : null,
            Number = underlyingObject is PlcBlock numberedBlock
                ? V1TiaHelpers.Try<int?>(() => numberedBlock.Number)
                : null,
        };
    }

    private static string EngineeringObjectType(IEngineeringObject engineeringObject) =>
        engineeringObject switch
        {
            OB => V1ObjectTypes.OrganizationBlock,
            FB => V1ObjectTypes.FunctionBlock,
            FC => V1ObjectTypes.Function,
            GlobalDB => V1ObjectTypes.GlobalDataBlock,
            InstanceDB => V1ObjectTypes.InstanceDataBlock,
            ArrayDB => V1ObjectTypes.ArrayDataBlock,
            PlcBlock => V1ObjectTypes.DataBlock,
            PlcType => V1ObjectTypes.PlcDataType,
            PlcTagTable => V1ObjectTypes.PlcTagTable,
            PlcTag => V1ObjectTypes.Tag,
            PlcUserConstant => V1ObjectTypes.UserConstant,
            PlcSystemConstant => V1ObjectTypes.SystemConstant,
            _ => engineeringObject.GetType().Name,
        };

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private V1InventoryHandle BuildInventory(
        LiveContext context,
        V1PlcHandle plc,
        V1PlcIdentity plcIdentity)
    {
        try
        {
            return V1TiaHelpers.BuildInventory(context.Identifiers, plc);
        }
        catch (V1BridgeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw NativeOperationError(
                ex,
                "Live PLC hierarchy traversal failed.",
                plcIdentity);
        }
    }

    private V1BridgeException NativeOperationError(
        Exception exception,
        string message,
        V1PlcIdentity? plcIdentity = null,
        V1ObjectIdentity? objectIdentity = null)
    {
        var nativeMessages = ExceptionMessages(exception, temporaryDirectory: null);
        var mapped = MapExportFailureCode(exception, nativeMessages);
        var code = mapped == V1ErrorCodes.ExportFailed
            ? V1ErrorCodes.TiaOperationFailed
            : mapped;
        return ReadError(
            code,
            FailureReason(code, message),
            plcIdentity,
            objectIdentity,
            nativeMessages: nativeMessages,
            inner: exception);
    }

    private LiveContext CreateContext()
    {
        var project = _tia.Project ?? throw new V1BridgeException(new V1Error
        {
            Code = V1ErrorCodes.NoActiveProject,
            Message = "No TIA Portal project is active.",
        }, V1ProvenanceFactory.Create(_tia));
        var identifiers = project.GetService<ObjectIdentifierProvider>();
        if (identifiers is null)
        {
            throw new V1BridgeException(new V1Error
            {
                Code = V1ErrorCodes.MissingProductOrOption,
                Message = "TIA Portal did not expose ObjectIdentifierProvider for the active project.",
            }, V1ProvenanceFactory.Create(_tia));
        }

        return new LiveContext
        {
            Project = project,
            Identifiers = identifiers,
            Plcs = V1TiaHelpers.EnumeratePlcs(project),
        };
    }

    private V1PlcHandle ResolvePlc(LiveContext context, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new V1BridgeException(new V1Error
            {
                Code = V1ErrorCodes.InvalidSelector,
                Message = "A PLC selector is required.",
            }, V1ProvenanceFactory.Create(_tia));
        }

        var identified = context.Plcs
            .Select(plc => new
            {
                Plc = plc,
                Identity = V1ProvenanceFactory.PlcIdentity(plc, context.Identifiers),
            })
            .ToList();
        var value = selector.Trim();
        var matches = identified.Where(item =>
            string.Equals(item.Identity.ObjectId, value, StringComparison.Ordinal) ||
            string.Equals(item.Identity.Name, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Identity.DeviceName, value, StringComparison.OrdinalIgnoreCase) ||
            HierarchyPathEquals(item.Identity.Path, value)).ToList();

        if (matches.Count == 1)
            return matches[0].Plc;

        var error = new V1Error
        {
            Code = matches.Count == 0
                ? V1ErrorCodes.ObjectNotFound
                : V1ErrorCodes.AmbiguousSelector,
            Message = matches.Count == 0
                ? $"No PLC matched selector '{value}'."
                : $"PLC selector '{value}' matched {matches.Count} PLCs.",
            Candidates = matches.Select(item => new V1SelectionCandidate
            {
                ObjectId = item.Identity.ObjectId,
                Name = item.Identity.Name,
                Path = item.Identity.Path,
                Type = "PLC",
            }).ToList(),
        };
        throw new V1BridgeException(error, V1ProvenanceFactory.Create(_tia));
    }

    private V1ObjectHandle ResolveObject(
        LiveContext context,
        V1InventoryHandle inventory,
        V1PlcIdentity plcIdentity,
        V1ObjectSelector selector,
        bool includeEntries = false)
    {
        if (selector is null ||
            (string.IsNullOrWhiteSpace(selector.ObjectId) &&
             string.IsNullOrWhiteSpace(selector.Path) &&
             string.IsNullOrWhiteSpace(selector.Name)))
        {
            throw ReadError(
                V1ErrorCodes.InvalidSelector,
                "An object selector requires objectId, path, or name.",
                plcIdentity);
        }

        var candidates = inventory.Objects.ToList();
        if (includeEntries)
            candidates.AddRange(EnumerateEntryHandles(inventory, context.Identifiers).Select(e => e.Handle));

        var selectorObjectId = selector.ObjectId;
        var selectorPath = selector.Path;
        var selectorName = selector.Name;
        var selectorType = selector.Type;
        IEnumerable<V1ObjectHandle> matches;
        if (!string.IsNullOrWhiteSpace(selectorObjectId))
        {
            matches = candidates.Where(candidate =>
                string.Equals(candidate.ObjectId, selectorObjectId, StringComparison.Ordinal));
        }
        else
        {
            matches = candidates.Where(candidate =>
                (string.IsNullOrWhiteSpace(selectorPath) ||
                 HierarchyPathEquals(candidate.Path, selectorPath)) &&
                (string.IsNullOrWhiteSpace(selectorName) ||
                 string.Equals(candidate.Name, selectorName!.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(selectorType) ||
                 ObjectTypeMatches(candidate.Type, selectorType!)));
        }

        var materialized = matches.ToList();
        if (materialized.Count == 1)
            return materialized[0];

        var error = new V1Error
        {
            Code = materialized.Count == 0
                ? V1ErrorCodes.ObjectNotFound
                : V1ErrorCodes.AmbiguousSelector,
            Message = materialized.Count == 0
                ? "No object in the requested PLC matched the selector."
                : $"The object selector matched {materialized.Count} objects in the requested PLC.",
            Plc = plcIdentity,
            Candidates = materialized.Select(candidate => new V1SelectionCandidate
            {
                ObjectId = candidate.ObjectId,
                PlcObjectId = inventory.PlcObjectId,
                Path = candidate.Path,
                Name = candidate.Name,
                Type = candidate.Type,
            }).ToList(),
        };
        throw new V1BridgeException(error, V1ProvenanceFactory.Create(_tia, plcIdentity));
    }

    private V1BridgeException ReadError(
        string code,
        string message,
        V1PlcIdentity? plc = null,
        V1ObjectIdentity? engineeringObject = null,
        IReadOnlyList<V1Attempt>? attempts = null,
        IReadOnlyList<string>? nativeMessages = null,
        Exception? inner = null)
    {
        var error = new V1Error
        {
            Code = code,
            Message = message,
            Plc = plc,
            Object = engineeringObject,
            Attempts = attempts ?? Array.Empty<V1Attempt>(),
            NativeMessages = nativeMessages ?? Array.Empty<string>(),
        };
        var provenance = V1ProvenanceFactory.Create(_tia, plc, engineeringObject);
        return inner is null
            ? new V1BridgeException(error, provenance)
            : new V1BridgeException(error, provenance, inner);
    }

    private static V1ObjectIdentity ObjectIdentity(
        V1ObjectHandle handle,
        string? plcObjectId) => new()
    {
        ObjectId = handle.ObjectId,
        PlcObjectId = plcObjectId,
        ParentObjectId = handle.ParentObjectId,
        Path = handle.Path,
        Name = handle.Name,
        Type = handle.Type,
        Language = handle.Language,
        Number = handle.Number,
    };

    private static V1PlcObjectNode MapHierarchy(
        V1HierarchyHandle node,
        string? plcObjectId)
    {
        var identity = node.Object is null
            ? new V1ObjectIdentity
            {
                ObjectId = node.ObjectId,
                PlcObjectId = plcObjectId,
                ParentObjectId = node.ParentObjectId,
                Path = node.Path,
                Name = node.Name,
                Type = node.Type,
            }
            : ObjectIdentity(node.Object, plcObjectId);

        return new V1PlcObjectNode
        {
            Identity = identity,
            IsGroup = node.IsGroup,
            IsSystemGroup = node.IsSystemGroup,
            IsProtected = node.Object?.IsProtected,
            IsSafety = node.Object?.IsSafety ?? node.IsSafety,
            IsSystemGenerated = node.Object?.IsSystemGenerated ?? node.IsSystemGenerated,
            IsConsistent = node.Object?.IsConsistent,
            ContentAvailable = node.Object?.ContentAvailable,
            ContentLimitation = node.Object?.ContentLimitation,
            Children = node.Children.Select(child => MapHierarchy(child, plcObjectId)).ToList(),
        };
    }

    private static IReadOnlyList<EntryHandle> EnumerateEntryHandles(
        V1InventoryHandle inventory,
        ObjectIdentifierProvider identifiers)
    {
        var result = new List<EntryHandle>();
        foreach (var parent in inventory.Objects.Where(o => o.EngineeringObject is PlcTagTable))
        {
            var table = (PlcTagTable)parent.EngineeringObject;
            foreach (PlcTag tag in table.Tags)
            {
                result.Add(NewEntry(
                    tag,
                    parent,
                    V1TiaHelpers.Try(() => tag.Name) ?? "<unreadable tag>",
                    V1ObjectTypes.Tag,
                    V1TiaHelpers.Try(() => tag.DataTypeName),
                    V1TiaHelpers.Try(() => tag.LogicalAddress),
                    identifiers));
            }
            foreach (PlcUserConstant constant in table.UserConstants)
            {
                result.Add(NewEntry(
                    constant,
                    parent,
                    V1TiaHelpers.Try(() => constant.Name) ?? "<unreadable user constant>",
                    V1ObjectTypes.UserConstant,
                    V1TiaHelpers.Try(() => constant.DataTypeName),
                    V1TiaHelpers.Try(() => constant.Value),
                    identifiers));
            }
            foreach (PlcSystemConstant constant in table.SystemConstants)
            {
                result.Add(NewEntry(
                    constant,
                    parent,
                    V1TiaHelpers.Try(() => constant.Name) ?? "<unreadable system constant>",
                    V1ObjectTypes.SystemConstant,
                    V1TiaHelpers.Try(() => constant.DataTypeName),
                    V1TiaHelpers.Try(() => constant.Value),
                    identifiers));
            }
        }
        return result;
    }

    private static EntryHandle NewEntry(
        IEngineeringObject entry,
        V1ObjectHandle parent,
        string name,
        string type,
        string? dataType,
        string? addressOrValue,
        ObjectIdentifierProvider identifiers) => new()
    {
        ParentTable = parent,
        DataType = dataType,
        AddressOrValue = addressOrValue,
        Handle = new V1ObjectHandle
        {
            EngineeringObject = entry,
            Scope = parent.Scope,
            ObjectId = V1TiaHelpers.TryGetIdentifier(identifiers, entry),
            ParentObjectId = parent.ObjectId,
            Name = name,
            Path = V1TiaHelpers.CombinePath(parent.Path, name),
            Type = type,
            IsSafety = entry is PlcTag tag
                ? V1TiaHelpers.Try<bool?>(() => tag.IsSafety)
                : parent.IsSafety,
            IsSystemGenerated = parent.IsSystemGenerated,
        },
    };

    private static bool MatchesSearch(
        V1ObjectIdentity identity,
        string query,
        string? type,
        string? language,
        string? group)
    {
        if (!string.IsNullOrWhiteSpace(type) && !ObjectTypeMatches(identity.Type, type!))
            return false;
        if (!string.IsNullOrWhiteSpace(language) &&
            !string.Equals(identity.Language, language!.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(group) &&
            identity.Path.IndexOf(group!.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (string.IsNullOrWhiteSpace(query) || query == "*")
            return true;

        return identity.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
               identity.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
               identity.Type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
               (identity.ObjectId?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
    }

    private static bool ObjectTypeMatches(string actual, string requested)
    {
        var left = NormalizeType(actual);
        var right = NormalizeType(requested);
        if (left == right)
            return true;
        return right == "DB" &&
               (left == "DB" || left == "GLOBALDB" || left == "INSTANCEDB" || left == "ARRAYDB");
    }

    private static string NormalizeType(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool HierarchyPathEquals(string? left, string? right) =>
        string.Equals(
            left?.Trim().Replace('\\', '/').Trim('/'),
            right?.Trim().Replace('\\', '/').Trim('/'),
            StringComparison.OrdinalIgnoreCase);

    private sealed class RepresentationAttempt
    {
        public required V1Attempt Attempt { get; init; }
        public required string ErrorCode { get; init; }
        public V1Representation? Representation { get; init; }
        public Exception? Exception { get; init; }
    }

    private sealed class LiveContext
    {
        public required Project Project { get; init; }
        public required ObjectIdentifierProvider Identifiers { get; init; }
        public required IReadOnlyList<V1PlcHandle> Plcs { get; init; }
    }

    private sealed class EntryHandle
    {
        public required V1ObjectHandle Handle { get; init; }
        public required V1ObjectHandle ParentTable { get; init; }
        public string? DataType { get; init; }
        public string? AddressOrValue { get; init; }
    }
}
