using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

internal static class Program
{
    private static object? _keepAlive;

    [STAThread]
    private static int Main(string[] args)
    {
        RegisterSiemensResolver();
        try
        {
            if (args.Length == 1 && args[0] == "--list") { ListProcesses(); return 0; }
            if (args.Length == 1 && args[0] == "--help") { Usage(); return 0; }
            Run(Options.Parse(args));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Probe stopped: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static void RegisterSiemensResolver()
    {
        var portal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Siemens", "Automation", "Portal V20");
        var folders = new[]
        {
            Path.Combine(portal, "PublicAPI", "V20"), Path.Combine(portal, "Bin", "PublicAPI"),
            Path.Combine(portal, "Bin", "PublicAPI", "Client"), Path.Combine(portal, "Bin")
        };
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var name = new AssemblyName(request.Name).Name + ".dll";
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
    }

    private static void Usage()
    {
        Console.WriteLine("DeviceReferenceLookup --list");
        Console.WriteLine("DeviceReferenceLookup --process-id PID --expected-start-ticks UTC_TICKS --project-path ABSOLUTE_AP20 [--device-id ID] [--passes 10]");
        Console.WriteLine("The target process and project must be specified exactly. No project is saved or changed.");
    }

    private static void ListProcesses()
    {
        var found = false;
        foreach (var process in TiaPortal.GetProcesses().OrderBy(item => item.Id))
        {
            found = true;
            try
            {
                Console.WriteLine("PID " + process.Id + "; mode " + process.Mode +
                    "; start UTC ticks " + StartTicks(process.Id) +
                    "; project " + (process.ProjectPath?.FullName ?? "<none>"));
            }
            catch (Exception ex) { Console.WriteLine("PID " + process.Id + "; discovery failed: " + ex.Message); }
            // Disposing a TiaPortalProcess descriptor closes TIA; never dispose it here.
        }
        if (!found) Console.WriteLine("No running TIA Portal processes were discovered.");
    }

    private static void Run(Options options)
    {
        var descriptor = TiaPortal.GetProcesses().SingleOrDefault(item => item.Id == options.ProcessId)
            ?? throw new InvalidOperationException("The selected TIA process is no longer available.");
        CheckDescriptor(descriptor, options);
        TiaPortal? portal = null;
        try
        {
            portal = descriptor.Attach();
            var project = CheckAttachment(portal, options, null);
            void Guard() => CheckAttachment(portal, options, project);
            var identifiers = project.GetService<ObjectIdentifierProvider>()
                ?? throw new InvalidOperationException("This project has no ObjectIdentifierProvider.");
            var devices = AllDevices(project).ToList();
            if (devices.Count == 0) throw new InvalidOperationException("The project has no Device objects to measure.");
            var chosen = options.DeviceId == null
                ? devices.Select(device => (device, count: CountItems(device))).OrderByDescending(pair => pair.count).First().device
                : FindDevice(identifiers, options.DeviceId);
            var objectId = identifiers.GetIdentifier(chosen);
            if (string.IsNullOrWhiteSpace(objectId))
                throw new InvalidOperationException("The selected Device has no native object ID.");
            Guard();
            Console.WriteLine("Project: " + project.Path.FullName);
            Console.WriteLine("Device: " + chosen.Name + "; objectId " + objectId +
                "; nested DeviceItems " + CountItems(chosen) + "; project Device count " + devices.Count);
            Console.WriteLine("Comparison: fresh provider + Find + read | retained provider + Find + read | retained Device + read");
            Console.WriteLine("The read samples native device/item metadata and the complete nested DeviceItem tree; it does not run MCP or serialize an MCP response.");

            Device Fresh() => FindDevice(project.GetService<ObjectIdentifierProvider>()
                ?? throw new InvalidOperationException("ObjectIdentifierProvider disappeared."), objectId);
            Device Resolved() => FindDevice(identifiers, objectId);
            string FreshRead() => ReadDevice(Fresh(), identifiers);
            string ResolvedRead() => ReadDevice(Resolved(), identifiers);
            string RetainedRead() => ReadDevice(chosen, identifiers);

            var expected = RetainedRead();
            if (FreshRead() != expected || ResolvedRead() != expected)
                throw new InvalidOperationException("The lookup approaches returned different device values before timing.");

            var freshLookup = new List<double>();
            var retainedProviderLookup = new List<double>();
            var guardChecks = new List<double>();
            var freshTotal = new List<double>();
            var resolvedTotal = new List<double>();
            var retainedTotal = new List<double>();
            const int lookupBatch = 100;
            for (var pass = 0; pass < options.Passes; pass++)
            {
                Guard();
                guardChecks.Add(TimeGuards(100, Guard) / 100);
                if (pass % 2 == 0)
                {
                    freshLookup.Add(TimeBatch(lookupBatch, Fresh) / lookupBatch);
                    retainedProviderLookup.Add(TimeBatch(lookupBatch, Resolved) / lookupBatch);
                    freshTotal.Add(TimeRead(FreshRead, expected));
                    resolvedTotal.Add(TimeRead(ResolvedRead, expected));
                    retainedTotal.Add(TimeRead(RetainedRead, expected));
                }
                else
                {
                    retainedTotal.Add(TimeRead(RetainedRead, expected));
                    resolvedTotal.Add(TimeRead(ResolvedRead, expected));
                    freshTotal.Add(TimeRead(FreshRead, expected));
                    retainedProviderLookup.Add(TimeBatch(lookupBatch, Resolved) / lookupBatch);
                    freshLookup.Add(TimeBatch(lookupBatch, Fresh) / lookupBatch);
                }
                Guard();
            }
            Print("Lookup only: fresh provider + Find", freshLookup, lookupBatch, options.Passes);
            Print("Lookup only: retained provider + Find", retainedProviderLookup, lookupBatch, options.Passes);
            Print("Context check only (probe approximation)", guardChecks, 100, options.Passes);
            Print("Complete read: fresh provider + Find", freshTotal, 1, options.Passes);
            Print("Complete read: retained provider + Find", resolvedTotal, 1, options.Passes);
            Print("Complete read: retained Device reference", retainedTotal, 1, options.Passes);
            Console.WriteLine("All read results matched. Times are warm, repeated reads of one Device; differences may include run-to-run TIA variation.");
        }
        finally
        {
            // Releases only this probe's attachment. The project and TIA window remain open.
            portal?.Dispose();
        }
    }

    private static double TimeBatch(int count, Func<Device> action)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++) _keepAlive = action();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double TimeGuards(int count, Action action)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++) action();
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double TimeRead(Func<string> action, string expected)
    {
        var watch = Stopwatch.StartNew();
        var actual = action();
        watch.Stop();
        if (actual != expected) throw new InvalidOperationException("Device values changed or read approaches differ during timing.");
        _keepAlive = actual;
        return watch.Elapsed.TotalMilliseconds;
    }

    private static void Print(string label, List<double> values, int batch, int passes)
    {
        values.Sort();
        var middle = passes / 2;
        var median = passes % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
        Console.WriteLine(label + ": median " + median.ToString("F3", CultureInfo.InvariantCulture) +
            " ms per operation; range " + values[0].ToString("F3", CultureInfo.InvariantCulture) +
            "–" + values[passes - 1].ToString("F3", CultureInfo.InvariantCulture) +
            " ms (" + passes + " passes, " + batch + " operation(s) per pass).");
    }

    private static string ReadDevice(Device device, ObjectIdentifierProvider identifiers)
    {
        var result = new StringBuilder();
        AppendHardware(device, identifiers, result);
        result.Append("|isGsd=").Append(device.IsGsd);
        AppendItems(device, identifiers, result);
        return result.ToString();
    }

    private static void AppendItems(HardwareObject owner, ObjectIdentifierProvider identifiers, StringBuilder result)
    {
        foreach (var item in owner.DeviceItems)
        {
            AppendHardware(item, identifiers, result);
            result.Append("|class=").Append(item.Classification)
                .Append("|position=").Append(item.PositionNumber)
                .Append("|builtIn=").Append(item.IsBuiltIn)
                .Append("|plugged=").Append(item.IsPlugged)
                .Append("|plc=").Append(item.GetService<SoftwareContainer>()?.Software is PlcSoftware);
            AppendItems(item, identifiers, result);
        }
    }

    private static void AppendHardware(HardwareObject target, ObjectIdentifierProvider identifiers, StringBuilder result)
    {
        result.Append("|id=").Append(identifiers.GetIdentifier(target))
            .Append("|name=").Append(target.Name)
            .Append("|type=").Append(target.TypeIdentifier);
        var names = target.GetAttributeInfos()
            .Where(info => (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
            .Select(info => info.Name).ToArray();
        if (names.Length == 0) return;
        try
        {
            var values = target.GetAttributes(names);
            for (var i = 0; i < names.Length; i++)
                result.Append('|').Append(names[i]).Append('=').Append(values[i]);
        }
        catch (EngineeringException)
        {
            // Match the product reader's bulk-failure fallback for one inaccessible attribute.
            foreach (var name in names)
            {
                try { result.Append('|').Append(name).Append('=').Append(target.GetAttribute(name)); }
                catch (EngineeringException) { result.Append('|').Append(name).Append("=<unreadable>"); }
            }
        }
    }

    private static Device FindDevice(ObjectIdentifierProvider identifiers, string objectId)
    {
        var target = identifiers.Find(objectId);
        return target as Device ?? throw new InvalidOperationException("The selected ID no longer resolves to a Device.");
    }

    private static int CountItems(HardwareObject owner)
    {
        var count = 0;
        foreach (var item in owner.DeviceItems) count += 1 + CountItems(item);
        return count;
    }

    private static IEnumerable<Device> AllDevices(Project project)
    {
        foreach (var device in project.Devices) yield return device;
        foreach (var group in project.DeviceGroups)
            foreach (var device in AllDevices(group)) yield return device;
        foreach (var device in AllDevices(project.UngroupedDevicesGroup)) yield return device;
    }

    private static IEnumerable<Device> AllDevices(DeviceGroup group)
    {
        foreach (var device in group.Devices) yield return device;
        if (group is DeviceUserGroup user)
            foreach (var child in user.Groups)
                foreach (var device in AllDevices(child)) yield return device;
    }

    private static void CheckDescriptor(TiaPortalProcess descriptor, Options options)
    {
        if (descriptor.Id != options.ProcessId || StartTicks(descriptor.Id) != options.ExpectedStartTicks ||
            descriptor.Mode != TiaPortalMode.WithUserInterface ||
            !SamePath(descriptor.ProjectPath?.FullName, options.ProjectPath))
            throw new InvalidOperationException("The selected UI process, start time, or open project differs from the supplied target. List again.");
    }

    private static Project CheckAttachment(TiaPortal portal, Options options, Project? retained)
    {
        CheckDescriptor(portal.GetCurrentProcess(), options);
        if (portal.Projects.Count != 1) throw new InvalidOperationException("Expected exactly one open primary project.");
        var project = portal.Projects.First();
        if (!SamePath(project.Path.FullName, options.ProjectPath) ||
            (retained != null && !retained.Equals(project)))
            throw new InvalidOperationException("The retained project changed during the probe.");
        return project;
    }

    private static long StartTicks(int processId)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static bool SamePath(string? actual, string expected) => actual != null &&
        string.Equals(Path.GetFullPath(actual).TrimEnd('\\', '/'),
            Path.GetFullPath(expected).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private sealed class Options
    {
        internal int ProcessId { get; private set; }
        internal long ExpectedStartTicks { get; private set; }
        internal string ProjectPath { get; private set; } = "";
        internal string? DeviceId { get; private set; }
        internal int Passes { get; private set; } = 10;

        internal static Options Parse(string[] args)
        {
            if (args.Length == 0 || args.Length % 2 != 0) throw new ArgumentException("Supply explicit target options; use --help.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new[] { "--process-id", "--expected-start-ticks", "--project-path", "--device-id", "--passes" };
            for (var i = 0; i < args.Length; i += 2)
            {
                if (!allowed.Contains(args[i], StringComparer.Ordinal) || values.ContainsKey(args[i]))
                    throw new ArgumentException("Unknown or duplicate option: " + args[i]);
                values.Add(args[i], args[i + 1]);
            }
            if (!values.TryGetValue("--process-id", out var pid) ||
                !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0 ||
                !values.TryGetValue("--expected-start-ticks", out var start) ||
                !long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 ||
                !values.TryGetValue("--project-path", out var path) || !Path.IsPathRooted(path) || !File.Exists(path))
                throw new ArgumentException("Supply a positive PID, its UTC start ticks, and an existing absolute project path.");
            var result = new Options { ProcessId = processId, ExpectedStartTicks = ticks, ProjectPath = Path.GetFullPath(path) };
            if (values.TryGetValue("--device-id", out var deviceId))
                result.DeviceId = string.IsNullOrWhiteSpace(deviceId) ? throw new ArgumentException("--device-id cannot be blank.") : deviceId;
            if (values.TryGetValue("--passes", out var passesText))
            {
                if (!int.TryParse(passesText, NumberStyles.None, CultureInfo.InvariantCulture, out var passes) || passes < 1 || passes > 30)
                    throw new ArgumentException("--passes must be between 1 and 30.");
                result.Passes = passes;
            }
            return result;
        }
    }
}
