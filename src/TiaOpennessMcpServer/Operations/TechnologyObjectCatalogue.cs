using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaOpennessMcpServer.Operations;

// Filters the JSON written by tools/technology-object-catalogue.cjs. The rows stay in that file.
internal static class TechnologyObjectCatalogue
{
    public const string FileName = "technology-object-catalogue.json";

    public static string BesideExecutable() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

    public static AvailableTechnologyObjects SelectFile(string path, string? typeName, string? typeIdentifier, string? orderNumber, string? firmwareVersion)
    {
        if (!File.Exists(path))
            return Failed("The technology-object catalogue file was not found.");
        try { return Select(File.ReadAllText(path), typeName, typeIdentifier, orderNumber, firmwareVersion); }
        catch (JsonException)
        {
            return Failed("The technology-object catalogue file could not be read.");
        }
    }

    public static AvailableTechnologyObjects Select(string catalogueJson, string? typeName, string? typeIdentifier, string? orderNumber, string? firmwareVersion)
    {
        var catalogue = JsonSerializer.Deserialize<CatalogueFile>(catalogueJson, JsonOptions);
        if (catalogue?.Objects == null || catalogue.Objects.Count == 0)
            return Failed("The technology-object catalogue does not contain any objects.");
        var result = new AvailableTechnologyObjects
        {
            TypeName = Blank(typeName),
            FirmwareVersion = Blank(firmwareVersion),
            CatalogueDescription = Blank(catalogue.Source?.Description)
        };
        var family = FamilyOf(typeName, typeIdentifier, orderNumber);
        result.CpuFamily = family;
        result.TechnologyCpu = family == "S7-1500" && Regex.IsMatch(
            string.Join(" ", new[] { typeName, orderNumber }.Where(text => !string.IsNullOrWhiteSpace(text))),
            @"15\d{2}T", RegexOptions.IgnoreCase);
        if (family == null)
        {
            result.Errors.Add(Error("This CPU is outside the V20 technology-object catalogue."));
            return result;
        }
        var firmware = ParseVersion(result.FirmwareVersion);
        if (firmware == null && (result.FirmwareVersion != null || catalogue.Objects.Any(item => item.Cpu == family && !IsAny(item.Firmware))))
            result.Errors.Add(Error("Firmware version was not readable, so technology objects that require a firmware minimum were omitted."));
        var footnotes = catalogue.Footnotes.Where(item => !string.IsNullOrWhiteSpace(item.Marker))
            .GroupBy(item => item.Marker, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Text).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "", StringComparer.Ordinal);
        foreach (var item in catalogue.Objects)
        {
            if (!string.Equals(item.Cpu, family, StringComparison.Ordinal)) continue;
            if (TechnologyOnly(item.Name) && !result.TechnologyCpu) continue;
            if (!FirmwareAllows(item.Firmware, firmware)) continue;
            result.TechnologyObjects.Add(new AvailableTechnologyObject
            {
                Technology = item.Technology,
                Name = item.Name,
                SystemLibElement = CreateElement(item.Name),
                Version = item.Version,
                Firmware = item.Firmware,
                Notes = item.Notes,
                Footnotes = item.Notes.Where(footnotes.ContainsKey).Select(marker => footnotes[marker]).ToList()
            });
        }
        return result;
    }

    private static AvailableTechnologyObjects Failed(string message)
    {
        var result = new AvailableTechnologyObjects();
        result.Errors.Add(Error(message));
        return result;
    }

    private static DiscoveryError Error(string message) => new()
    {
        Origin = "bridge",
        Operation = "list_available_technology_objects",
        Message = message
    };

    private static string? FamilyOf(string? typeName, string? typeIdentifier, string? orderNumber)
    {
        var blob = string.Join(" ", new[] { typeName, typeIdentifier, orderNumber }.Where(text => !string.IsNullOrWhiteSpace(text)));
        if (blob.Length == 0) return null;
        if (Regex.IsMatch(blob, @"1200\s*G2|12\d{2}[A-Z0-9]*\s*G2|S71200G2", RegexOptions.IgnoreCase)) return "S7-1200 G2";
        if (Regex.IsMatch(blob, @"S7-?1200|S71200|CPU\s*12\d{2}|6ES7\s*2", RegexOptions.IgnoreCase)) return "S7-1200";
        if (Regex.IsMatch(blob, @"S7-?1500|S71500|CPU\s*15\d{2}|6ES7\s*5", RegexOptions.IgnoreCase)) return "S7-1500";
        if (Regex.IsMatch(blob, @"S7-?300|S7-?400|CPU\s*[34]\d{2}|6ES7\s*[34]", RegexOptions.IgnoreCase)) return "S7-300/400";
        return null;
    }

    private static bool TechnologyOnly(string name) =>
        name.IndexOf("(S7-1500T)", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string CreateElement(string name) =>
        Regex.Replace(name, @"\s*\(S7-1500T\)\s*$", "", RegexOptions.IgnoreCase);

    private static bool IsAny(string? firmware) =>
        string.Equals(firmware?.Trim(), "Any", StringComparison.OrdinalIgnoreCase);

    private static bool FirmwareAllows(string? cell, Version? cpuFirmware)
    {
        if (IsAny(cell)) return true;
        if (cpuFirmware == null) return false;
        var required = ParseVersion(cell);
        return required != null && cpuFirmware >= required;
    }

    private static string? Blank(string? text)
    {
        if (text == null) return null;
        var value = text.Trim();
        return value.Length == 0 ? null : value;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\d+(?:\.\d+){0,3}");
        if (!match.Success) return null;
        var raw = match.Value.Contains('.') ? match.Value : match.Value + ".0";
        return Version.TryParse(raw, out var version) ? version : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class CatalogueFile
    {
        public CatalogueSource? Source { get; set; }
        public List<CatalogueFootnote> Footnotes { get; set; } = new();
        public List<CatalogueObject> Objects { get; set; } = new();
    }

    private sealed class CatalogueSource
    {
        public string? Description { get; set; }
    }

    private sealed class CatalogueFootnote
    {
        public string Marker { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private sealed class CatalogueObject
    {
        public string Cpu { get; set; } = "";
        public string Technology { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Firmware { get; set; } = "";
        public List<string> Notes { get; set; } = new();
    }
}

internal sealed class AvailableTechnologyObjects : DiscoveryResult
{
    public string? PlcObjectId { get; set; }
    public string? CpuFamily { get; set; }
    public string? TypeName { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool TechnologyCpu { get; set; }
    public string? CatalogueDescription { get; set; }
    public List<AvailableTechnologyObject> TechnologyObjects { get; } = new();
}

internal sealed class AvailableTechnologyObject
{
    public string Technology { get; set; } = "";
    public string Name { get; set; } = "";
    public string SystemLibElement { get; set; } = "";
    public string Version { get; set; } = "";
    public string Firmware { get; set; } = "";
    public List<string> Notes { get; set; } = new();
    public List<string> Footnotes { get; set; } = new();
}
