using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

// Alias our model enums to avoid ambiguity with Siemens.Engineering.SW.Blocks.*
using ModelBlockType = TiaOpennessMcpServer.Models.BlockType;
using ModelLanguage  = TiaOpennessMcpServer.Models.ProgrammingLanguage;

namespace TiaOpennessMcpServer.Services;

public sealed class SoftwareService
{
    private readonly TiaPortalService _tia;
    private readonly StaTaskScheduler _sta;
    private readonly TiaOpennessOptions _opts;
    private readonly ILogger<SoftwareService> _log;

    public SoftwareService(
        TiaPortalService tia,
        StaTaskScheduler sta,
        IOptions<TiaOpennessOptions> opts,
        ILogger<SoftwareService> log)
    {
        _tia  = tia;
        _sta  = sta;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Block enumeration ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Models.BlockInfo>> ListBlocksAsync(string deviceName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            var results = new List<Models.BlockInfo>();
            CollectBlocks(plc.BlockGroup, results);
            return (IReadOnlyList<Models.BlockInfo>)results;
        });
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<BlockContent> ReadBlockAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var lang       = MapLanguage(block);
            var xmlContent = "";
            var sclSource  = "";

            try
            {
                Directory.CreateDirectory(_opts.ExportDirectory);
                var exportFile = Path.Combine(
                    _opts.ExportDirectory,
                    $"{deviceName}_{blockName}_{DateTime.UtcNow:yyyyMMddHHmmss}.xml");

                block.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                xmlContent = File.ReadAllText(exportFile);
                sclSource  = XmlHelper.ExtractSclSource(xmlContent) ?? "";
                _log.LogDebug("Read block {Block} ({Bytes} bytes XML)", blockName, xmlContent.Length);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Export failed for {Block}: {Msg}", blockName, ex.Message);
                sclSource = lang == ModelLanguage.SCL
                    ? $"// Cannot export block source.\n// {ex.Message.Split('\n')[0]}\n//\n// Tip: compile the full project in TIA Portal (Ctrl+B), then refresh."
                    : $"// Language: {lang}\n// Only SCL blocks have readable source code in TIA Openness.";
            }

            return new BlockContent
            {
                Name       = block.Name,
                Type       = MapBlockType(block),
                Number     = block.Number,
                Language   = lang,
                Author     = block.HeaderAuthor ?? "",
                Comment    = BlockComment(block),
                Modified   = block.ModifiedDate.ToString("O"),
                IsKnowHow  = block.IsKnowHowProtected,
                SizeBytes  = xmlContent.Length,
                SourceCode = sclSource,
                XmlContent = xmlContent,
            };
        });
    }

    /// <summary>
    /// Exports a pure LAD block as SIMATIC SD and returns the generated document
    /// contents. This is a read-only Openness operation; no document is imported
    /// back into the TIA Portal project.
    /// </summary>
    public async Task<LadSourceContent> ReadLadSourceAsync(string deviceName, string blockName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name is required.", nameof(deviceName));
        if (string.IsNullOrWhiteSpace(blockName))
            throw new ArgumentException("Block name is required.", nameof(blockName));

        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var rawLanguage = block.ProgrammingLanguage;
            if (rawLanguage != Siemens.Engineering.SW.Blocks.ProgrammingLanguage.LAD)
            {
                throw new NotSupportedException(
                    $"Block '{block.Name}' uses programming language '{rawLanguage}'; " +
                    "read_lad_source supports only pure LAD blocks. " +
                    "Use read_block for SCL source or raw SimaticML XML.");
            }

            if (block.IsKnowHowProtected)
            {
                throw new NotSupportedException(
                    $"LAD block '{block.Name}' is know-how protected and cannot be " +
                    "exported as readable SIMATIC SD source.");
            }

            var exportDirectory = Path.Combine(
                _opts.ExportDirectory,
                "lad-source",
                Guid.NewGuid().ToString("N"));
            var warnings = new List<string>();

            try
            {
                try
                {
                    Directory.CreateDirectory(exportDirectory);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not create a temporary directory for SIMATIC SD export " +
                        $"of LAD block '{block.Name}' ({ex.GetType().Name}).",
                        ex);
                }

                Siemens.Engineering.SW.DocumentExportResult exportResult;
                try
                {
                    exportResult = block.ExportAsDocuments(
                        new DirectoryInfo(exportDirectory), "block");
                }
                catch (Exception ex)
                {
                    var detail = SingleLine(ex.Message, exportDirectory);
                    throw new InvalidOperationException(
                        $"SIMATIC SD export failed for LAD block '{block.Name}'" +
                        (detail.Length == 0 ? "." : $": {detail}"),
                        ex);
                }

                var exportMessages = exportResult.Messages
                    .Cast<Siemens.Engineering.SW.DocumentResultMessage>()
                    .Select(m => SingleLine(m.Message, exportDirectory))
                    .Where(m => m.Length > 0)
                    .ToList();

                if (exportResult.State != Siemens.Engineering.SW.DocumentResultState.Success)
                {
                    var details = exportMessages.Count == 0
                        ? ""
                        : $" Siemens: {string.Join("; ", exportMessages)}";
                    var reason = exportResult.State == Siemens.Engineering.SW.DocumentResultState.PartialSuccess
                        ? "Incomplete or mixed-language exports are not returned."
                        : "The block may use mixed languages or document export may be unavailable.";

                    throw new NotSupportedException(
                        $"SIMATIC SD export for LAD block '{block.Name}' returned " +
                        $"{exportResult.State}. {reason}{details}");
                }

                warnings.AddRange(exportMessages);

                var exportedFiles = exportResult.ExportedDocuments
                    .Where(f => f is not null)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var generatedFileNames = exportedFiles
                    .Select(f => f.Name)
                    .ToList();

                var sourceFiles = exportedFiles
                    .Where(f => f.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (sourceFiles.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"SIMATIC SD export for LAD block '{block.Name}' produced no " +
                        ".s7dcl source document.");
                }
                if (sourceFiles.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"SIMATIC SD export for LAD block '{block.Name}' produced " +
                        $"{sourceFiles.Count} .s7dcl source documents; exactly one was expected.");
                }

                string sourceContent;
                try
                {
                    sourceContent = File.ReadAllText(sourceFiles[0].FullName);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not read SIMATIC SD source document " +
                        $"'{sourceFiles[0].Name}' for LAD block '{block.Name}' " +
                        $"({ex.GetType().Name}).",
                        ex);
                }

                if (string.IsNullOrWhiteSpace(sourceContent))
                {
                    throw new InvalidOperationException(
                        $"SIMATIC SD export for LAD block '{block.Name}' produced an " +
                        "empty .s7dcl source document.");
                }

                var resourceDocuments = new List<SimaticSdDocumentContent>();
                foreach (var resourceFile in exportedFiles.Where(f =>
                             f.Extension.Equals(".s7res", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        resourceDocuments.Add(new SimaticSdDocumentContent
                        {
                            FileName = resourceFile.Name,
                            Content  = File.ReadAllText(resourceFile.FullName),
                        });
                    }
                    catch (Exception ex)
                    {
                        warnings.Add(
                            $"Could not read SIMATIC SD resource document " +
                            $"'{resourceFile.Name}' ({ex.GetType().Name}).");
                    }
                }

                if (resourceDocuments.Count == 0)
                    warnings.Add("TIA Portal generated no readable .s7res resource document.");

                foreach (var unexpected in exportedFiles.Where(f =>
                             !f.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) &&
                             !f.Extension.Equals(".s7res", StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add(
                        $"Unexpected exported document '{unexpected.Name}' was ignored.");
                }

                return new LadSourceContent
                {
                    BlockName         = block.Name,
                    BlockType         = MapBlockType(block),
                    BlockNumber       = block.Number,
                    Language          = ModelLanguage.LAD,
                    SourceFormat      = "simatic-sd",
                    ExportState       = exportResult.State.ToString(),
                    GeneratedFileNames = generatedFileNames,
                    SourceDocument    = new SimaticSdDocumentContent
                    {
                        FileName = sourceFiles[0].Name,
                        Content  = sourceContent,
                    },
                    ResourceDocuments = resourceDocuments,
                    Warnings          = warnings,
                };
            }
            finally
            {
                if (Directory.Exists(exportDirectory))
                {
                    try
                    {
                        Directory.Delete(exportDirectory, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(
                            "Could not remove temporary LAD export directory: {Message}",
                            SingleLine(ex.Message, exportDirectory));
                        warnings.Add(
                            $"Temporary export cleanup failed ({ex.GetType().Name}).");
                    }
                }
            }
        });
    }

    // ── Write (SCL) ───────────────────────────────────────────────────────────

    public async Task WriteBlockSclAsync(string deviceName, string blockName, string sclSource)
    {
        _tia.EnsureConnected();
        await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            Directory.CreateDirectory(_opts.ExportDirectory);
            var exportFile = Path.Combine(_opts.ExportDirectory,
                $"{deviceName}_{blockName}_edit.xml");
            block.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);

            var xml     = File.ReadAllText(exportFile);
            var patched = XmlHelper.InjectSclSource(xml, sclSource);
            File.WriteAllText(exportFile, patched);

            plc.BlockGroup.Blocks.Import(new FileInfo(exportFile), ImportOptions.Override);
            _log.LogInformation("Written SCL to {Block} on {Device}.", blockName, deviceName);
        });
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Models.BlockInfo> CreateBlockAsync(string deviceName, BlockCreateRequest req)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);

            Directory.CreateDirectory(_opts.ExportDirectory);
            var xmlContent = req.Type == Models.BlockType.GlobalDB
                ? XmlHelper.CreateGlobalDbXml(req.Name, req.Number, req.SourceCode)
                : XmlHelper.CreateSclBlockXml(req.Name, req.Type.ToString(), req.Number, req.SourceCode);

            var importFile = Path.Combine(_opts.ExportDirectory,
                $"create_{deviceName}_{req.Name}.xml");
            File.WriteAllText(importFile, xmlContent);

            plc.BlockGroup.Blocks.Import(new FileInfo(importFile), ImportOptions.Override);
            var block = FindBlock(plc.BlockGroup, req.Name);

            _log.LogInformation("Created {Type} {Block} on {Device}.",
                req.Type, req.Name, deviceName);

            return new Models.BlockInfo
            {
                Name      = block.Name,
                Type      = MapBlockType(block),
                Number    = block.Number,
                Language  = MapLanguage(block),
                Author    = block.HeaderAuthor ?? "",
                Comment   = BlockComment(block),
                Modified  = block.ModifiedDate.ToString("O"),
                IsKnowHow = block.IsKnowHowProtected,
                SizeBytes = 0,
            };
        });
    }

    // ── Compile ───────────────────────────────────────────────────────────────

    public async Task<string> CompileBlockAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var compilable = block.GetService<ICompilable>()
                ?? throw new NotSupportedException(
                    $"Block '{blockName}' does not support compilation.");

            var result   = compilable.Compile();
            var messages = result.Messages
                .Cast<CompilerResultMessage>()
                .Select(m => $"[{m.State}] {m.Description}")
                .ToList();

            _log.LogInformation("Compiled {Block}: {State} ({W}W {E}E)",
                blockName, result.State, result.WarningCount, result.ErrorCount);

            return string.Join("\n",
                $"Result:   {result.State}",
                $"Errors:   {result.ErrorCount}",
                $"Warnings: {result.WarningCount}",
                "",
                string.Join("\n", messages));
        });
    }

    // ── Create Instance DB ────────────────────────────────────────────────────

    public async Task<Models.BlockInfo> CreateInstanceDbAsync(
        string deviceName, string name, string instanceOfName, int? number)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc     = GetPlcSoftware(deviceName);
            var xml     = XmlHelper.CreateInstanceDbXml(name, instanceOfName, number);
            var path    = Path.Combine(_opts.ExportDirectory, $"create_{deviceName}_{name}_idb.xml");
            Directory.CreateDirectory(_opts.ExportDirectory);
            File.WriteAllText(path, xml, System.Text.Encoding.UTF8);
            plc.BlockGroup.Blocks.Import(new FileInfo(path), ImportOptions.Override);
            var block = FindBlock(plc.BlockGroup, name);
            _log.LogInformation("Created InstanceDB {Name} (of {FB}) on {Device}.",
                name, instanceOfName, deviceName);
            return BlockToInfo(block);
        });
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    public async Task<string> ExportBlockAsync(
        string deviceName, string blockName, string? exportPath = null)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            Directory.CreateDirectory(_opts.ExportDirectory);
            var path = exportPath ?? Path.Combine(
                _opts.ExportDirectory, $"{deviceName}_{blockName}.xml");

            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            _log.LogInformation("Exported {Block} → {Path}", blockName, path);
            return path;
        });
    }

    public async Task PatchBlockTextsAsync(string deviceName, string blockName, Models.BlockTextsRequest req)
    {
        _tia.EnsureConnected();
        await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            if (!string.IsNullOrEmpty(req.BlockTitle))
                block.SetAttribute("Title", req.BlockTitle);
            if (!string.IsNullOrEmpty(req.BlockComment))
                block.SetAttribute("Comment", req.BlockComment);

            _log.LogInformation("Patched texts on block {Block}.", blockName);
        });
    }

    public async Task<object> GetBlockAttributeInfosAsync(string deviceName, string blockName)
    {
        _tia.EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var plc   = GetPlcSoftware(deviceName);
            var block = FindBlock(plc.BlockGroup, blockName);

            var attrs = block.GetAttributeInfos()
                .Cast<Siemens.Engineering.EngineeringAttributeInfo>()
                .Select(i => new { name = i.Name, access = i.AccessMode.ToString() })
                .ToList();

            var iEng = (Siemens.Engineering.IEngineeringObject)block;
            var compositions = iEng.GetCompositionInfos()
                .Cast<Siemens.Engineering.EngineeringCompositionInfo>()
                .Select(i => i.Name)
                .ToList();

            return (object)new { blockType = block.GetType().Name, attributes = attrs, compositions };
        });
    }

    public async Task WriteBlockXmlAsync(string deviceName, string blockName, string xmlContent)
    {
        _tia.EnsureConnected();
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content is empty.");

        await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            Directory.CreateDirectory(_opts.ExportDirectory);
            var importFile = Path.Combine(_opts.ExportDirectory,
                $"{deviceName}_{blockName}_xml_edit.xml");
            File.WriteAllText(importFile, xmlContent);

            // Import to the block's own parent group so Override resolves correctly
            // for blocks in user-defined subfolders.
            PlcBlockComposition targetBlocks;
            try
            {
                var existing = FindBlock(plc.BlockGroup, blockName);
                targetBlocks = existing.Parent switch
                {
                    PlcBlockUserGroup ug => ug.Blocks,
                    PlcBlockGroup     rg => rg.Blocks,
                    _                   => plc.BlockGroup.Blocks,
                };
            }
            catch
            {
                targetBlocks = plc.BlockGroup.Blocks;
            }

            targetBlocks.Import(new FileInfo(importFile), ImportOptions.Override);
            _log.LogInformation("Imported raw XML for {Block} on {Device}.", blockName, deviceName);
        });
    }

    public async Task ImportBlockAsync(string deviceName, string xmlFilePath)
    {
        _tia.EnsureConnected();
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"Import file not found: {xmlFilePath}");

        await _sta.RunAsync(() =>
        {
            var plc = GetPlcSoftware(deviceName);
            plc.BlockGroup.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override);
            _log.LogInformation("Imported block from {Path} into {Device}.", xmlFilePath, deviceName);
        });
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    internal PlcSoftware GetPlcSoftware(string deviceName)
    {
        foreach (Device device in _tia.Project!.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (DeviceItem di in device.DeviceItems)
            {
                var sw = di.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (sw is not null) return sw;
            }
        }
        throw new KeyNotFoundException(
            $"No PLC software found for device '{deviceName}'. " +
            "Ensure the device contains a CPU with software.");
    }

    private static PlcBlock FindBlock(PlcBlockGroup group, string name)
    {
        var block = group.Blocks
            .Cast<PlcBlock>()
            .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (block is not null) return block;

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            block = FindBlockInUserGroup(sub, name);
            if (block is not null) return block;
        }

        throw new KeyNotFoundException($"Block '{name}' not found.");
    }

    private static PlcBlock? FindBlockInUserGroup(PlcBlockUserGroup group, string name)
    {
        var block = group.Blocks
            .Cast<PlcBlock>()
            .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (block is not null) return block;

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            block = FindBlockInUserGroup(sub, name);
            if (block is not null) return block;
        }
        return null;
    }

    private static void CollectBlocks(PlcBlockGroup group, List<Models.BlockInfo> list)
    {
        foreach (PlcBlock block in group.Blocks.Cast<PlcBlock>())
            list.Add(BlockToInfo(block));

        foreach (PlcBlockUserGroup sub in group.Groups)
            CollectBlocksFromUserGroup(sub, list);
    }

    private static void CollectBlocksFromUserGroup(PlcBlockUserGroup group, List<Models.BlockInfo> list)
    {
        foreach (PlcBlock block in group.Blocks.Cast<PlcBlock>())
            list.Add(BlockToInfo(block));

        foreach (PlcBlockUserGroup sub in group.Groups)
            CollectBlocksFromUserGroup(sub, list);
    }

    private static Models.BlockInfo BlockToInfo(PlcBlock block) => new()
    {
        Name      = block.Name,
        Type      = MapBlockType(block),
        Number    = block.Number,
        Language  = MapLanguage(block),
        Author    = block.HeaderAuthor ?? "",
        Comment   = BlockComment(block),
        Modified  = block.ModifiedDate.ToString("O"),
        IsKnowHow = block.IsKnowHowProtected,
        SizeBytes = 0,
    };

    private static string BlockComment(PlcBlock block)
    {
        try { return block.GetAttribute("Comment") as string ?? ""; }
        catch { return ""; }
    }

    private static string SingleLine(string? value, string? pathToRedact = null)
    {
        var result = (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (!string.IsNullOrEmpty(pathToRedact))
            result = result.Replace(pathToRedact, "<temporary directory>");

        return result;
    }

    private static ModelBlockType MapBlockType(PlcBlock block) => block switch
    {
        OB       => ModelBlockType.OB,
        FB       => ModelBlockType.FB,
        FC       => ModelBlockType.FC,
        GlobalDB => ModelBlockType.GlobalDB,
        _        => ModelBlockType.DB,
    };

    private static ModelLanguage MapLanguage(PlcBlock block)
    {
        return block.ProgrammingLanguage switch
        {
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.SCL   => ModelLanguage.SCL,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.LAD   => ModelLanguage.LAD,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.FBD   => ModelLanguage.FBD,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.STL   => ModelLanguage.STL,
            Siemens.Engineering.SW.Blocks.ProgrammingLanguage.GRAPH => ModelLanguage.GRAPH,
            _                                                        => ModelLanguage.SCL,
        };
    }
}
