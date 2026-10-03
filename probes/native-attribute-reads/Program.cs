using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;

internal static class Program
{
    private static readonly string[] BlockFields = { "Name", "Number", "ProgrammingLanguage" };
    private static readonly string[] DeviceFields =
        { "Name", "TypeIdentifier", "Classification", "PositionNumber", "IsBuiltIn", "IsPlugged" };
    private static int _sink;

    [STAThread]
    private static int Main(string[] args)
    {
        RegisterSiemensResolver();
        try
        {
            if (args.Length == 1 && args[0] == "--help") { Usage(); return 0; }
            if (args.Length == 1 && args[0] == "--list") { ListProcesses(); return 0; }
            var options = Options.Parse(args);
            Run(options);
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
        Console.WriteLine("NativeAttributeReads --list");
        Console.WriteLine("NativeAttributeReads --process-id PID --expected-start-ticks UTC_TICKS --project-path ABSOLUTE_AP20 [--limit 100] [--passes 5]");
        Console.WriteLine("--list discovers processes without attaching. A run requires all three explicit target values.");
    }

    private static void ListProcesses()
    {
        var found = false;
        foreach (var process in TiaPortal.GetProcesses().OrderBy(item => item.Id))
        {
            found = true;
            try
            {
                Console.WriteLine("PID: " + process.Id);
                Console.WriteLine("  Mode: " + process.Mode);
                Console.WriteLine("  Start UTC ticks: " + StartTicks(process.Id));
                Console.WriteLine("  Project path: " + (process.ProjectPath?.FullName ?? "<none>"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("PID: " + process.Id + " (discovery failed: " + ex.Message + ")");
            }
            // TiaPortalProcess.Dispose closes TIA; discovery descriptors are never disposed.
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
            Console.WriteLine("Attached to PID " + options.ProcessId + " and project " + project.Path.FullName);
            var samples = Collect(project, options.Limit);
            Console.WriteLine("Samples: " + samples.Blocks.Count + " blocks; " + samples.DeviceItems.Count + " device items (limit " + options.Limit + " each).");
            void Guard() => CheckAttachment(portal, options, project);
            Guard();
            CompareAndTime("Blocks: typed properties vs named GetAttributes", samples.Blocks,
                BlockFields, ReadBlockTyped, ReadBlockNamed, options.Passes, Guard);
            CompareAndTime("Blocks: typed properties vs all readable GetAttributes", samples.Blocks,
                BlockFields, ReadBlockTyped, ReadBlockAll, options.Passes, Guard);
            CompareAndTime("Device items: current bulk plus typed reads vs bulk reuse", samples.DeviceItems,
                DeviceFields, ReadDeviceCurrent, ReadDeviceBulk, options.Passes, Guard);
            Guard();
            Console.WriteLine("Probe completed. Matching text alone does not establish equivalent native types or product behavior.");
        }
        finally
        {
            // This releases only the attachment created above. Never dispose the
            // TiaPortalProcess descriptor: that operation closes the TIA window.
            portal?.Dispose();
        }
    }

    private static void CheckDescriptor(TiaPortalProcess descriptor, Options options)
    {
        if (descriptor.Id != options.ProcessId || StartTicks(descriptor.Id) != options.ExpectedStartTicks ||
            descriptor.Mode != TiaPortalMode.WithUserInterface ||
            !SamePath(descriptor.ProjectPath?.FullName, options.ProjectPath))
            throw new InvalidOperationException("The selected UI process, start time, or open project differs from the supplied target. List processes again.");
    }

    private static Project CheckAttachment(TiaPortal portal, Options options, Project? retained)
    {
        var current = portal.GetCurrentProcess();
        CheckDescriptor(current, options);
        if (portal.Projects.Count != 1)
            throw new InvalidOperationException("The attachment does not have exactly one primary project.");
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

    private static Samples Collect(Project project, int limit)
    {
        var samples = new Samples();
        var seenItems = new HashSet<DeviceItem>();
        var seenSoftware = new HashSet<PlcSoftware>();
        var seenBlocks = new HashSet<PlcBlock>();
        foreach (var device in Devices(project))
            CollectItems(device, samples, seenItems, seenSoftware, seenBlocks, limit);
        return samples;
    }

    private static IEnumerable<Device> Devices(Project project)
    {
        foreach (var device in project.Devices) yield return device;
        foreach (var group in project.DeviceGroups)
            foreach (var device in Devices(group)) yield return device;
        foreach (var device in Devices(project.UngroupedDevicesGroup)) yield return device;
    }

    private static IEnumerable<Device> Devices(DeviceGroup group)
    {
        foreach (var device in group.Devices) yield return device;
        if (group is DeviceUserGroup user)
            foreach (var child in user.Groups)
                foreach (var device in Devices(child)) yield return device;
    }

    private static void CollectItems(HardwareObject owner, Samples samples,
        HashSet<DeviceItem> seenItems, HashSet<PlcSoftware> seenSoftware,
        HashSet<PlcBlock> seenBlocks, int limit)
    {
        foreach (var item in owner.DeviceItems)
        {
            if (!seenItems.Add(item)) continue;
            if (samples.DeviceItems.Count < limit) samples.DeviceItems.Add(item);
            if (samples.Blocks.Count < limit &&
                item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc && seenSoftware.Add(plc))
                CollectBlocks(plc, samples.Blocks, seenBlocks, limit);
            if (samples.DeviceItems.Count < limit || samples.Blocks.Count < limit)
                CollectItems(item, samples, seenItems, seenSoftware, seenBlocks, limit);
        }
    }

    private static void CollectBlocks(PlcSoftware plc, List<PlcBlock> blocks,
        HashSet<PlcBlock> seen, int limit)
    {
        CollectBlockGroup(plc.BlockGroup, blocks, seen, limit);
        if (blocks.Count >= limit) return;
        var units = plc.GetService<PlcUnitProvider>()?.UnitGroup;
        if (units == null) return;
        foreach (var unit in units.Units)
            CollectBlockGroup(unit.BlockGroup, blocks, seen, limit);
        foreach (var unit in units.SafetyUnits)
            CollectBlockGroup(unit.BlockGroup, blocks, seen, limit);
    }

    private static void CollectBlockGroup(PlcBlockGroup group, List<PlcBlock> blocks,
        HashSet<PlcBlock> seen, int limit)
    {
        if (blocks.Count >= limit) return;
        foreach (var block in group.Blocks)
        {
            if (blocks.Count >= limit) return;
            if (seen.Add(block)) blocks.Add(block);
        }
        foreach (var child in group.Groups) CollectBlockGroup(child, blocks, seen, limit);
        if (group is PlcBlockSystemGroup system)
            foreach (var child in system.SystemBlockGroups)
                CollectSystemBlockGroup(child, blocks, seen, limit);
    }

    private static void CollectSystemBlockGroup(PlcSystemBlockGroup group, List<PlcBlock> blocks,
        HashSet<PlcBlock> seen, int limit)
    {
        if (blocks.Count >= limit) return;
        foreach (var block in group.Blocks)
        {
            if (blocks.Count >= limit) return;
            if (seen.Add(block)) blocks.Add(block);
        }
        foreach (var child in group.Groups) CollectSystemBlockGroup(child, blocks, seen, limit);
    }

    private static Dictionary<string, NativeValue> ReadBlockTyped(PlcBlock block) => new()
    {
        ["Name"] = Value(block.Name), ["Number"] = Value(block.Number),
        ["ProgrammingLanguage"] = Value(block.ProgrammingLanguage.ToString())
    };

    private static Dictionary<string, NativeValue> ReadBlockNamed(PlcBlock block)
    {
        var values = block.GetAttributes(BlockFields);
        var result = new Dictionary<string, NativeValue>(StringComparer.Ordinal);
        for (var i = 0; i < BlockFields.Length; i++) result[BlockFields[i]] = Value(values[i]);
        return result;
    }

    private static Dictionary<string, NativeValue> ReadBlockAll(PlcBlock block)
    {
        var result = new Dictionary<string, NativeValue>(StringComparer.Ordinal);
        foreach (var pair in block.GetAttributes(AttributeAccessOptions.ReadOnly | AttributeAccessOptions.ReadWrite))
            if (BlockFields.Contains(pair.Key, StringComparer.Ordinal)) result[pair.Key] = Value(pair.Value);
        return result;
    }

    private static Dictionary<string, NativeValue> ReadDeviceCurrent(DeviceItem item)
    {
        var firstName = item.Name; // The current detail reader gets Name before HardwareMetadata.
        var bulk = ReadReadableAttributes(item);
        var result = new Dictionary<string, NativeValue>(StringComparer.Ordinal)
        {
            ["Name"] = Value(item.Name), ["TypeIdentifier"] = Value(item.TypeIdentifier),
            ["Classification"] = Value(item.Classification.ToString()),
            ["PositionNumber"] = Value(item.PositionNumber),
            ["IsBuiltIn"] = Value(item.IsBuiltIn), ["IsPlugged"] = Value(item.IsPlugged)
        };
        GC.KeepAlive(firstName);
        GC.KeepAlive(bulk);
        return result;
    }

    private static Dictionary<string, NativeValue> ReadDeviceBulk(DeviceItem item)
    {
        var bulk = ReadReadableAttributes(item);
        var result = new Dictionary<string, NativeValue>(StringComparer.Ordinal);
        foreach (var name in DeviceFields)
            if (bulk.TryGetValue(name, out var value)) result[name] = Value(value);
        return result;
    }

    private static Dictionary<string, object?> ReadReadableAttributes(IEngineeringObject target)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var names = target.GetAttributeInfos()
            .Where(info => (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
            .Select(info => info.Name).ToArray();
        if (names.Length == 0) return result;
        try
        {
            var values = target.GetAttributes(names);
            for (var i = 0; i < names.Length; i++) result[names[i]] = values[i];
        }
        catch (EngineeringException)
        {
            // Like the active reader, do not discard every readable attribute
            // because one bulk member is unavailable or protected.
            foreach (var name in names)
            {
                try { result[name] = target.GetAttribute(name); }
                catch (EngineeringException) { }
            }
        }
        return result;
    }

    private static NativeValue Value(object? value)
    {
        var text = value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString();
        return new NativeValue(value?.GetType().FullName ?? "<null>", text ?? "<null>");
    }

    private static void CompareAndTime<T>(string title, IReadOnlyList<T> samples, string[] names,
        Func<T, Dictionary<string, NativeValue>> first,
        Func<T, Dictionary<string, NativeValue>> second, int passes, Action guard)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        if (samples.Count == 0) { Console.WriteLine("  No samples in this project."); return; }
        var eligible = new List<T>();
        var differences = 0;
        var failures = 0;
        var examples = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            if (i % 20 == 0) guard();
            var left = Capture(() => first(samples[i]));
            var right = Capture(() => second(samples[i]));
            if (left.Error != null || right.Error != null)
            {
                failures++;
                if (examples++ < 5)
                    Console.WriteLine("  Sample " + (i + 1) + " failed: typed/current=" +
                        (left.Error ?? "ok") + "; bulk=" + (right.Error ?? "ok"));
                continue;
            }
            eligible.Add(samples[i]);
            foreach (var name in names)
            {
                var hasLeft = left.Fields!.TryGetValue(name, out var a);
                var hasRight = right.Fields!.TryGetValue(name, out var b);
                if (hasLeft && hasRight && a.Text == b.Text && a.Type == b.Type) continue;
                differences++;
                if (examples++ < 5)
                    Console.WriteLine("  Sample " + (i + 1) + " " + name + ": typed/current=" +
                        (hasLeft ? a.ToString() : "<missing>") + "; bulk=" +
                        (hasRight ? b.ToString() : "<missing>"));
            }
        }
        guard();
        Console.WriteLine("  Checked " + samples.Count + "; read failures " + failures +
            "; field value/type differences " + differences + "; both approaches completed on " + eligible.Count + ".");
        if (eligible.Count == 0) { Console.WriteLine("  No comparable reads to time."); return; }
        var firstTimes = new List<double>();
        var secondTimes = new List<double>();
        for (var pass = 0; pass < passes; pass++)
        {
            guard();
            if (pass % 2 == 0)
            {
                firstTimes.Add(Time(eligible, first));
                secondTimes.Add(Time(eligible, second));
            }
            else
            {
                secondTimes.Add(Time(eligible, second));
                firstTimes.Add(Time(eligible, first));
            }
            guard();
        }
        Console.WriteLine("  Median over " + passes + " passes of " + eligible.Count + " objects: typed/current " +
            Median(firstTimes).ToString("F2", CultureInfo.InvariantCulture) + " ms; bulk " +
            Median(secondTimes).ToString("F2", CultureInfo.InvariantCulture) + " ms.");
        if (differences > 0 || failures > 0)
            Console.WriteLine("  Timing alone cannot justify a replacement while differences or failures remain.");
    }

    private static ReadOutcome Capture(Func<Dictionary<string, NativeValue>> read)
    {
        try { return new ReadOutcome(read(), null); }
        catch (Exception ex) { return new ReadOutcome(null, ex.GetType().Name + ": " + ex.Message); }
    }

    private static double Time<T>(IReadOnlyList<T> samples, Func<T, Dictionary<string, NativeValue>> read)
    {
        var watch = Stopwatch.StartNew();
        foreach (var sample in samples) _sink += read(sample).Count;
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] :
            (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    private sealed class Samples
    {
        internal List<PlcBlock> Blocks { get; } = new();
        internal List<DeviceItem> DeviceItems { get; } = new();
    }

    private readonly struct NativeValue
    {
        internal string Type { get; }
        internal string Text { get; }
        internal NativeValue(string type, string text) { Type = type; Text = text; }
        public override string ToString() => Type + " [" + Text + "]";
    }

    private readonly struct ReadOutcome
    {
        internal Dictionary<string, NativeValue>? Fields { get; }
        internal string? Error { get; }
        internal ReadOutcome(Dictionary<string, NativeValue>? fields, string? error)
        { Fields = fields; Error = error; }
    }

    private sealed class Options
    {
        internal int ProcessId { get; private set; }
        internal long ExpectedStartTicks { get; private set; }
        internal string ProjectPath { get; private set; } = "";
        internal int Limit { get; private set; } = 100;
        internal int Passes { get; private set; } = 5;

        internal static Options Parse(string[] args)
        {
            if (args.Length == 0 || args.Length % 2 != 0) throw new ArgumentException("Supply explicit target options; use --help for syntax.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (!new[] { "--process-id", "--expected-start-ticks", "--project-path", "--limit", "--passes" }
                        .Contains(args[i], StringComparer.Ordinal) || values.ContainsKey(args[i]))
                    throw new ArgumentException("Unknown or duplicate option: " + args[i]);
                values.Add(args[i], args[i + 1]);
            }
            if (!values.TryGetValue("--process-id", out var pid) ||
                !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0 ||
                !values.TryGetValue("--expected-start-ticks", out var start) ||
                !long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 ||
                !values.TryGetValue("--project-path", out var path) || !Path.IsPathRooted(path) || !File.Exists(path))
                throw new ArgumentException("Supply a positive PID, the listed UTC start ticks, and an existing absolute project path.");
            var options = new Options { ProcessId = processId, ExpectedStartTicks = ticks,
                ProjectPath = Path.GetFullPath(path) };
            if (values.TryGetValue("--limit", out var limitText) &&
                (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 1 || limit > 500))
                throw new ArgumentException("--limit must be between 1 and 500.");
            if (values.TryGetValue("--passes", out var passesText) &&
                (!int.TryParse(passesText, NumberStyles.None, CultureInfo.InvariantCulture, out var passes) || passes < 1 || passes > 20))
                throw new ArgumentException("--passes must be between 1 and 20.");
            if (limitText != null) options.Limit = int.Parse(limitText, CultureInfo.InvariantCulture);
            if (passesText != null) options.Passes = int.Parse(passesText, CultureInfo.InvariantCulture);
            return options;
        }
    }
}
