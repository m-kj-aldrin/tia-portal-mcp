using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaOpennessMcpServer.Models;
using TiaOpennessMcpServer.Utilities;

namespace TiaOpennessMcpServer.Services;

/// <summary>
/// Manages the lifecycle of a TIA Portal instance and open project.
/// All Portal API calls are marshaled through <see cref="StaTaskScheduler"/>
/// to ensure they originate from the required STA thread.
/// </summary>
public sealed class TiaPortalService : IDisposable
{
    private readonly StaTaskScheduler    _sta;
    private readonly TiaOpennessOptions  _opts;
    private readonly ILogger<TiaPortalService> _log;

    private TiaPortal? _portal;
    private Project?   _project;
    private bool       _ownsProject;
    private string     _lastConnectionAction = "none";

    // Expose raw objects to peer services (all access must go through STA).
    internal TiaPortal? Portal  => _portal;
    internal Project?   Project => _project;

    public bool IsConnected => _portal is not null && _project is not null;

    public TiaPortalService(
        StaTaskScheduler sta,
        IOptions<TiaOpennessOptions> opts,
        ILogger<TiaPortalService> log)
    {
        _sta  = sta;
        _opts = opts.Value;
        _log  = log;

        Directory.CreateDirectory(_opts.ExportDirectory);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reuses the active project, attaches to one exact open-project candidate, or
    /// visibly opens the supplied path. It never switches an active project.
    /// </summary>
    public async Task<ProjectInfo> AttachToRunningAsync(string? projectPath = null)
    {
        return await _sta.RunAsync(() =>
        {
            var requestedPath = string.IsNullOrWhiteSpace(projectPath)
                ? null
                : CanonicalizeProjectPath(projectPath!);

            if (_portal is not null && _project is not null)
            {
                var activePath = CanonicalizeProjectPath(_project.Path.FullName);
                if (requestedPath is not null &&
                    !PathEquals(activePath, requestedPath))
                {
                    throw new V1BridgeException(new V1Error
                    {
                        Code = V1ErrorCodes.ProjectConflict,
                        Message = $"This MCP instance is already attached to '{_project.Name}' " +
                                  $"at '{activePath}'. The requested project was '{requestedPath}'. " +
                                  "Version one never switches projects implicitly.",
                        Details = new Dictionary<string, string>
                        {
                            ["activeProjectPath"] = activePath,
                            ["requestedProjectPath"] = requestedPath,
                        },
                    }, V1ProvenanceFactory.Create(this));
                }

                _lastConnectionAction = V1ProjectSelectionActions.Reuse;
                _log.LogInformation(
                    "Already attached to TIA Portal — reusing project: {Name}", _project.Name);
                return BuildProjectInfo(_project);
            }

            if (_portal is not null)
            {
                _portal.Dispose();
                _portal = null;
                _ownsProject = false;
            }

            var processes = TiaPortal.GetProcesses();
            _log.LogInformation("{Count} running TIA Portal process(es) found.", processes.Count);

            // TiaPortalProcess.ProjectPath describes only the primary project.
            // Attach transiently to enumerate primary and secondary projects so an
            // already-open secondary project is never missed or replaced by a new TIA.
            var openCandidates = DiscoverOpenProjects(processes);

            if (requestedPath is not null)
            {
                var exactMatches = openCandidates
                    .Where(c => PathEquals(c.ProjectPath, requestedPath))
                    .ToList();

                if (exactMatches.Count > 1)
                {
                    var error = AmbiguousProjectError(exactMatches, requestedPath);
                    DisposeCandidatePortals(openCandidates, except: null);
                    throw error;
                }

                if (exactMatches.Count == 1)
                    return SelectAttachedCandidate(openCandidates, exactMatches[0]);

                DisposeCandidatePortals(openCandidates, except: null);
                return OpenVisibleProject(requestedPath!);
            }

            if (openCandidates.Count == 0)
            {
                throw new V1BridgeException(new V1Error
                {
                    Code = V1ErrorCodes.NoActiveProject,
                    Message = "No active TIA Portal project was found. Supply projectPath " +
                              "to visibly open a compatible V20 project, or open exactly one project in TIA Portal.",
                }, V1ProvenanceFactory.Create(this));
            }

            if (openCandidates.Count > 1)
            {
                var error = AmbiguousProjectError(openCandidates, requestedPath: null);
                DisposeCandidatePortals(openCandidates, except: null);
                throw error;
            }

            return SelectAttachedCandidate(openCandidates, openCandidates[0]);
        });
    }

    public async Task<ProjectInfo> OpenProjectAsync(string projectPath, bool headless = false)
    {
        if (headless)
            throw new NotSupportedException(
                "Version one opens TIA Portal projects only with a visible user interface.");

        return await AttachToRunningAsync(projectPath);
    }

    private static List<OpenProjectCandidate> DiscoverOpenProjects(
        IEnumerable<TiaPortalProcess> processes)
    {
        var result = new List<OpenProjectCandidate>();
        foreach (var process in processes)
        {
            TiaPortal? portal = null;
            try
            {
                portal = process.Attach();
                var projects = portal.Projects.Cast<Project>().ToList();
                if (projects.Count == 0)
                {
                    portal.Dispose();
                    continue;
                }

                foreach (var project in projects)
                {
                    result.Add(new OpenProjectCandidate(
                        process,
                        portal,
                        project,
                        CanonicalizeProjectPath(project.Path.FullName)));
                }
            }
            catch
            {
                portal?.Dispose();
                DisposeCandidatePortals(result, except: null);
                throw;
            }
        }

        return result;
    }

    private ProjectInfo SelectAttachedCandidate(
        IReadOnlyList<OpenProjectCandidate> allCandidates,
        OpenProjectCandidate selected)
    {
        _portal = selected.Portal;
        _project = selected.Project;
        _ownsProject = false;
        _lastConnectionAction = V1ProjectSelectionActions.AttachExact;
        DisposeCandidatePortals(allCandidates, selected.Portal);

        _log.LogInformation(
            "Attached to exact open TIA Portal project: {Name}", _project.Name);
        return BuildProjectInfo(_project);
    }

    private ProjectInfo OpenVisibleProject(string canonicalProjectPath)
    {
        var file = new FileInfo(canonicalProjectPath);
        if (!file.Exists)
            throw new V1BridgeException(new V1Error
            {
                Code = V1ErrorCodes.ObjectNotFound,
                Message = $"Project file not found: {canonicalProjectPath}",
                Details = new Dictionary<string, string>
                {
                    ["projectPath"] = canonicalProjectPath,
                },
            }, V1ProvenanceFactory.Create(this));

        if (TryReadProjectFileVersion(file.Extension, out var projectFileVersion) &&
            projectFileVersion != 20)
        {
            var isOlderProject = projectFileVersion < 20;
            throw new V1BridgeException(new V1Error
            {
                Code = isOlderProject
                    ? V1ErrorCodes.UpgradeRequired
                    : V1ErrorCodes.IncompatibleProject,
                Message = isOlderProject
                    ? $"Project '{canonicalProjectPath}' targets TIA Portal V{projectFileVersion} " +
                      "and would require an upgrade. Version one never invokes an upgrade workflow."
                    : $"Project '{canonicalProjectPath}' targets newer TIA Portal V{projectFileVersion} " +
                      "and is incompatible with this V20 bridge.",
                Details = new Dictionary<string, string>
                {
                    ["projectPath"] = canonicalProjectPath,
                    ["projectFileVersion"] = projectFileVersion.ToString(),
                    ["installedTargetVersion"] = "20",
                },
            }, V1ProvenanceFactory.Create(this));
        }

        TiaPortal? openedPortal = null;
        try
        {
            _log.LogInformation(
                "No exact open-project match was found; opening visibly: {Path}",
                canonicalProjectPath);

            openedPortal = new TiaPortal(TiaPortalMode.WithUserInterface);
            openedPortal.Authentication += (_, e) =>
                e.AuthenticationTypeProvider = AuthenticationTypeProvider.Interactive;
            // Deliberately use Open, never OpenWithUpgrade. An incompatible project
            // is surfaced to the caller and left unchanged.
            var openedProject = openedPortal.Projects.Open(file);

            _portal = openedPortal;
            _project = openedProject;
            _ownsProject = true;
            _lastConnectionAction = V1ProjectSelectionActions.OpenVisible;
            openedPortal = null;

            _log.LogInformation("Visibly opened project: {Name}", _project.Name);
            return BuildProjectInfo(_project);
        }
        finally
        {
            openedPortal?.Dispose();
        }
    }

    private static void DisposeCandidatePortals(
        IEnumerable<OpenProjectCandidate> candidates,
        TiaPortal? except)
    {
        var disposed = new HashSet<TiaPortal>();
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate.Portal, except) || !disposed.Add(candidate.Portal))
                continue;
            candidate.Portal.Dispose();
        }
    }

    private V1BridgeException AmbiguousProjectError(
        IEnumerable<OpenProjectCandidate> candidates,
        string? requestedPath)
    {
        var materialized = candidates.ToList();
        return new V1BridgeException(new V1Error
        {
            Code = V1ErrorCodes.AmbiguousProject,
            Message = requestedPath is null
                ? "Multiple TIA Portal projects are open. Supply projectPath to select one."
                : $"More than one running TIA Portal process exposes '{requestedPath}'.",
            Candidates = materialized.Select(candidate => new V1SelectionCandidate
            {
                Name = V1TiaHelpers.Try(() => candidate.Project.Name) ?? "<unknown project>",
                Path = candidate.ProjectPath,
                Type = "project",
            }).ToList(),
            Details = requestedPath is null
                ? null
                : new Dictionary<string, string> { ["requestedProjectPath"] = requestedPath },
        }, V1ProvenanceFactory.Create(this));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static string CanonicalizeProjectPath(string path)
        => V1ProjectSelectionPolicy.CanonicalizePath(path);

    private static bool TryReadProjectFileVersion(string extension, out int version)
    {
        version = 0;
        if (extension.Length <= 3 ||
            !extension.StartsWith(".ap", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(extension.Substring(3), out version);
    }

    private sealed record OpenProjectCandidate(
        TiaPortalProcess Process,
        TiaPortal Portal,
        Project Project,
        string ProjectPath);

    public async Task SaveAsync()
    {
        EnsureConnected();
        await _sta.RunAsync(() =>
        {
            if (!_ownsProject)
                throw new InvalidOperationException(
                    "Cannot save an externally attached project. Save it explicitly in TIA Portal instead.");

            _log.LogInformation("Saving project…");
            _project!.Save();
        });
    }

    public async Task<ProjectInfo> GetProjectInfoAsync()
    {
        EnsureConnected();
        return await _sta.RunAsync(() => BuildProjectInfo(_project!));
    }

    public async Task<V1ConnectionResponse> ConnectV1Async(string? projectPath = null)
    {
        try
        {
            await AttachToRunningAsync(projectPath);
            return await _sta.RunAsync(() => new V1ConnectionResponse
            {
                Provenance = V1ProvenanceFactory.Create(this),
                Connected = IsConnected,
                Action = _lastConnectionAction,
            });
        }
        catch (V1BridgeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var mapped = await _sta.RunAsync(() => MapConnectionException(ex, projectPath));
            throw mapped;
        }
    }

    public async Task<V1StatusResponse> GetStatusV1Async(
        string accessProfile,
        bool writeToolsAvailable)
    {
        return await _sta.RunAsync(() => new V1StatusResponse
        {
            Provenance = V1ProvenanceFactory.Create(this),
            Connected = IsConnected,
            AccessProfile = accessProfile,
            WriteToolsAvailable = writeToolsAvailable,
        });
    }

    private V1BridgeException MapConnectionException(Exception exception, string? requestedPath)
    {
        var nativeMessage = V1TiaHelpers.SingleLine(exception.Message);
        var code = V1ErrorCodes.TiaOperationFailed;
        var message = "TIA Portal connection or project opening failed.";

        if (exception is MissingProductsException)
        {
            code = V1ErrorCodes.MissingProductOrOption;
            message = "The project requires an installed TIA product, option, or support package that is unavailable.";
        }
        else if (exception is EngineeringSecurityException ||
                 ContainsAny(nativeMessage, "authentication", "authenticate", "password", "login", "access denied"))
        {
            code = V1ErrorCodes.UiAuthenticationRequired;
            message = "TIA Portal requires external-access approval or interactive authentication. Complete it in the visible TIA UI; credentials are never accepted by MCP tools.";
        }
        else if (ContainsAny(nativeMessage, "newer version", "later version", "not compatible", "incompatible"))
        {
            code = V1ErrorCodes.IncompatibleProject;
            message = "The project is incompatible with the installed TIA Portal V20 environment. Version one never invokes a conversion or upgrade workflow.";
        }
        else if (ContainsAny(nativeMessage, "upgrade", "older version", "project version"))
        {
            code = V1ErrorCodes.UpgradeRequired;
            message = "The project appears to require an upgrade. Version one never invokes an upgrade workflow.";
        }
        else if (exception is FileNotFoundException)
        {
            code = V1ErrorCodes.ObjectNotFound;
            message = "The requested project file was not found.";
        }

        var details = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(requestedPath))
            details["requestedProjectPath"] = CanonicalizeProjectPath(requestedPath!);

        return new V1BridgeException(new V1Error
        {
            Code = code,
            Message = message,
            NativeMessages = nativeMessage.Length == 0
                ? Array.Empty<string>()
                : new[] { nativeMessage },
            Details = details.Count == 0 ? null : details,
        }, V1ProvenanceFactory.Create(this), exception);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

    // ── Clone project ─────────────────────────────────────────────────────────

    public async Task<Models.CloneResult> CloneProjectAsync(string newName, string targetFolder)
    {
        EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            if (!_ownsProject)
                throw new InvalidOperationException(
                    "Cannot clone an externally attached project because cloning must save and close its source. " +
                    "Open the project through this application and retry.");

            var result = new Models.CloneResult();
            var exportDir = Path.Combine(_opts.ExportDirectory, "clone_export");
            Directory.CreateDirectory(exportDir);

            // ── 1. Snapshot device info before we touch anything ──────────────
            var srcProject = _project!;
            var sourceProjectWasOwned = _ownsProject;
            var deviceSnapshots = new List<(string Name, string TypeId, string Ip)>();
            var blockFiles   = new List<(string DeviceName, string FilePath)>();
            var tagFiles     = new List<(string DeviceName, string FilePath)>();

            foreach (Device device in srcProject.Devices)
            {
                string typeId = "", ip = "";
                try { typeId = device.TypeIdentifier ?? ""; } catch { }
                ip = ReadIpFromDevice(device);
                deviceSnapshots.Add((device.Name, typeId, ip));

                var plc = GetPlcFromDevice(device);
                if (plc is null) continue;

                // Export blocks
                foreach (PlcBlock block in plc.BlockGroup.Blocks.Cast<PlcBlock>())
                {
                    var f = Path.Combine(exportDir, $"{device.Name}__BLOCK__{block.Name}.xml");
                    try
                    {
                        block.Export(new FileInfo(f), ExportOptions.WithDefaults);
                        blockFiles.Add((device.Name, f));
                        result.BlocksExported++;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Export block {block.Name}: {ex.Message.Split('\n')[0]}");
                    }
                }

                // Export tag tables
                foreach (PlcTagTable table in plc.TagTableGroup.TagTables.Cast<PlcTagTable>())
                {
                    var f = Path.Combine(exportDir, $"{device.Name}__TAGS__{table.Name}.xml");
                    try
                    {
                        table.Export(new FileInfo(f), ExportOptions.WithDefaults);
                        tagFiles.Add((device.Name, f));
                        result.TagTablesExported++;
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Export tags {table.Name}: {ex.Message.Split('\n')[0]}");
                    }
                }
            }

            _log.LogInformation("Exported {B} blocks, {T} tag tables. Closing source project to create clone…",
                result.BlocksExported, result.TagTablesExported);

            // ── 2. Close source project (TIA only allows one project open at a time)
            var originalPath = srcProject.Path.FullName;
            srcProject.Save();
            srcProject.Close();
            _project = null;
            _ownsProject = false;

            // ── 3. Create new project ─────────────────────────────────────────
            var dir = new DirectoryInfo(targetFolder);
            dir.Create();
            var newProject = _portal!.Projects.Create(dir, newName);
            result.ProjectPath = Path.Combine(targetFolder, newName + ".ap20");

            // ── 4. Recreate hardware ──────────────────────────────────────────
            foreach (var (devName, typeId, ip) in deviceSnapshots)
            {
                if (string.IsNullOrWhiteSpace(typeId))
                {
                    result.Warnings.Add($"Skipped device '{devName}': no type identifier.");
                    continue;
                }
                try
                {
                    var newDev = newProject.Devices.CreateWithItem(typeId, devName, devName);
                    result.DevicesCreated++;
                    if (!string.IsNullOrWhiteSpace(ip))
                        SetIpOnDevice(newDev, ip);
                }
                catch (Exception)
                {
                    // Firmware version mismatch — retry without version suffix
                    var baseId = System.Text.RegularExpressions.Regex.Replace(typeId, @"/V\d+\.\d+.*$", "");
                    try
                    {
                        var newDev = newProject.Devices.CreateWithItem(baseId, devName, devName);
                        result.DevicesCreated++;
                        result.Warnings.Add($"Device '{devName}' created with base type (version stripped).");
                        if (!string.IsNullOrWhiteSpace(ip))
                            SetIpOnDevice(newDev, ip);
                    }
                    catch (Exception ex2)
                    {
                        result.Warnings.Add($"Could not create device '{devName}': {ex2.Message.Split('\n')[0]}");
                    }
                }
            }

            // ── 5. Import blocks ──────────────────────────────────────────────
            foreach (var (devName, filePath) in blockFiles)
            {
                var plc = GetPlcFromProject(newProject, devName);
                if (plc is null) { result.Warnings.Add($"No PLC found for device '{devName}' in new project."); continue; }
                try
                {
                    plc.BlockGroup.Blocks.Import(new FileInfo(filePath), ImportOptions.Override);
                    result.BlocksImported++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Import block {Path.GetFileName(filePath)}: {ex.Message.Split('\n')[0]}");
                }
            }

            // ── 6. Import tag tables ──────────────────────────────────────────
            foreach (var (devName, filePath) in tagFiles)
            {
                var plc = GetPlcFromProject(newProject, devName);
                if (plc is null) continue;
                try
                {
                    plc.TagTableGroup.TagTables.Import(new FileInfo(filePath), ImportOptions.Override);
                    result.TagTablesImported++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Import tags {Path.GetFileName(filePath)}: {ex.Message.Split('\n')[0]}");
                }
            }

            // ── 7. Save new project ───────────────────────────────────────────
            newProject.Save();
            result.Success = true;
            _log.LogInformation("Clone complete → {Path}", result.ProjectPath);

            // ── 8. Reopen the original project so the dashboard stays connected
            try
            {
                _project = _portal!.Projects.Open(new FileInfo(originalPath));
                _ownsProject = sourceProjectWasOwned;
                _log.LogInformation("Reopened original project: {Path}", originalPath);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Clone succeeded but could not reopen original project: {ex.Message.Split('\n')[0]}. Click Connect to reopen it manually.");
                _project = newProject;
                _ownsProject = true;
            }

            return result;
        });
    }

    private static PlcSoftware? GetPlcFromDevice(Device device)
    {
        foreach (DeviceItem di in device.DeviceItems)
        {
            var sw = di.GetService<SoftwareContainer>()?.Software as PlcSoftware;
            if (sw is not null) return sw;
        }
        return null;
    }

    private static PlcSoftware? GetPlcFromProject(Project project, string deviceName)
    {
        foreach (Device device in project.Devices)
        {
            if (!device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var plc = GetPlcFromDevice(device);
            if (plc is not null) return plc;
        }
        return null;
    }

    private static string ReadIpFromDevice(Device device)
    {
        try
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                var ni = di.GetService<NetworkInterface>();
                if (ni is null) continue;
                foreach (Node n in ni.Nodes)
                {
                    var addr = n.GetAttribute("Address") as string;
                    if (!string.IsNullOrWhiteSpace(addr)) return addr!;
                }
            }
        }
        catch { }
        return "";
    }

    private static void SetIpOnDevice(Device device, string ip)
    {
        try
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                var ni = di.GetService<NetworkInterface>();
                if (ni is null) continue;
                foreach (Node n in ni.Nodes)
                {
                    try { n.SetAttribute("Address", ip); } catch { }
                    try { n.CreateAndConnectToSubnet("PN/IE_1"); } catch { }
                }
            }
        }
        catch { }
    }

    // ── Project signature ─────────────────────────────────────────────────────

    public async Task<Models.ProjectSignature> GetProjectSignatureAsync()
    {
        EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var proj    = _project!;
            var devices = new List<Models.DeviceSignature>();

            foreach (Device device in proj.Devices)
            {
                PlcSoftware? plc = null;
                try
                {
                    foreach (DeviceItem di in device.DeviceItems)
                    {
                        plc = di.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                        if (plc is not null) break;
                    }
                }
                catch { }
                if (plc is null) continue;

                var blocks = new List<Models.BlockSignature>();
                CollectBlockSigs(plc.BlockGroup, blocks);

                var tables = plc.TagTableGroup.TagTables
                    .Cast<PlcTagTable>()
                    .Select(t => new Models.TagTableSig { Name = t.Name, TagCount = t.Tags.Count })
                    .ToList();

                devices.Add(new Models.DeviceSignature
                {
                    Name      = device.Name,
                    Blocks    = blocks,
                    TagTables = tables,
                });
            }

            return new Models.ProjectSignature
            {
                CapturedAt   = DateTime.UtcNow.ToString("O"),
                ProjectName  = proj.Name,
                ProjectPath  = proj.Path.FullName,
                Devices      = devices,
            };
        });
    }

    private static void CollectBlockSigs(PlcBlockGroup group, List<Models.BlockSignature> list)
    {
        foreach (PlcBlock b in group.Blocks.Cast<PlcBlock>())
        {
            bool consistent = false;
            try { consistent = b.IsConsistent; } catch { }
            list.Add(new Models.BlockSignature
            {
                Name         = b.Name,
                Type         = b.GetType().Name.Replace("PlcBlock", ""),
                Number       = b.Number,
                Language     = b.ProgrammingLanguage.ToString(),
                IsConsistent = consistent,
            });
        }
        foreach (PlcBlockUserGroup sub in group.Groups)
            CollectBlockSigs(sub, list);
    }

    private static void CollectBlockSigs(PlcBlockUserGroup group, List<Models.BlockSignature> list)
    {
        foreach (PlcBlock b in group.Blocks.Cast<PlcBlock>())
        {
            bool consistent = false;
            try { consistent = b.IsConsistent; } catch { }
            list.Add(new Models.BlockSignature
            {
                Name         = b.Name,
                Type         = b.GetType().Name.Replace("PlcBlock", ""),
                Number       = b.Number,
                Language     = b.ProgrammingLanguage.ToString(),
                IsConsistent = consistent,
            });
        }
        foreach (PlcBlockUserGroup sub in group.Groups)
            CollectBlockSigs(sub, list);
    }

    // ── Option packages ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Models.OptionPackageInfo>> GetOptionPackagesAsync()
    {
        EnsureConnected();
        return await _sta.RunAsync(() =>
        {
            var result = new List<Models.OptionPackageInfo>();
            foreach (UsedProduct pkg in _project!.UsedProducts)
            {
                result.Add(new Models.OptionPackageInfo
                {
                    DisplayName    = pkg.Name    ?? "",
                    DisplayVersion = pkg.Version ?? "",
                });
            }
            return (IReadOnlyList<Models.OptionPackageInfo>)result;
        });
    }

    public async Task CloseAsync()
    {
        if (_project is null && _portal is null) return;
        await _sta.RunAsync(() =>
        {
            var portal = _portal;
            _project = null;
            _ownsProject = false;
            _portal = null;
            _lastConnectionAction = "none";
            portal?.Dispose();
            _log.LogInformation(
                "Detached the Openness client without saving or closing the project or TIA Portal.");
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException(
                "No TIA Portal project is open. Call connect_to_tia_portal first.");
    }

    private static ProjectInfo BuildProjectInfo(Project project)
    {
        string author = "", comment = "", modified = "", version = "";
        bool   changed = false;

        try { author   = project.Author ?? ""; } catch { }
        try { comment  = project.Comment.Items.Cast<MultilingualTextItem>()
                             .FirstOrDefault()?.Text ?? ""; } catch { }
        try { modified = project.LastModified.ToString("O"); } catch { }
        try { version  = project.Version ?? ""; } catch { }
        try { changed  = project.IsModified; } catch { }

        return new ProjectInfo
        {
            Name          = project.Name,
            Path          = project.Path.FullName,
            Author        = author,
            Comment       = comment,
            CreatedDate   = "",
            ModifiedDate  = modified,
            Version       = version,
            PortalVersion = version,
            DeviceCount   = project.Devices.Count,
            IsModified    = changed,
        };
    }

    public void Dispose()
    {
        if (_project is null && _portal is null) return;

        try
        {
            var disposeTask = _sta.RunAsync(() =>
            {
                var portal      = _portal;

                _project = null;
                _ownsProject = false;
                _portal = null;
                _lastConnectionAction = "none";

                // Disposing the Openness client detaches this process. Never save,
                // close, or otherwise mutate the user's project during shutdown.
                portal?.Dispose();
            });

            if (!disposeTask.Wait(TimeSpan.FromSeconds(10)))
            {
                _log.LogWarning(
                    "Timed out waiting for TIA Portal cleanup on the STA thread; no cross-thread cleanup was attempted.");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error while disposing the TIA Portal connection.");
        }
    }
}
