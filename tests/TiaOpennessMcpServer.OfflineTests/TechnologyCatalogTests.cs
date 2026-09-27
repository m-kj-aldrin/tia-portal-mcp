using System.Text.Json;
using TiaOpennessMcpServer.Operations;

internal static class TechnologyCatalogTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("technology catalogue: a 1511T at firmware 2.6 keeps only the committed rows that CPU can create", TechnologyCpu);
        yield return ("technology catalogue: a standard 1500 omits technology-CPU rows from the file", StandardCpu);
        yield return ("technology catalogue: S7-1200, G2 and S7-300 stay on their own catalogue rows", Families);
        yield return ("technology catalogue: an unrecognized CPU and a missing firmware stay explicit", Gaps);
    }

    private static string CatalogueJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "data", "technology-object-catalogue.json");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new Exception("The committed technology-object catalogue was not found.");
    }

    private static AvailableTechnologyObjects Select(string? typeName, string? firmware, string? orderNumber = null) =>
        TechnologyObjectCatalogue.Select(CatalogueJson(), typeName, null, orderNumber, firmware);

    private static void TechnologyCpu()
    {
        using var document = JsonDocument.Parse(CatalogueJson());
        var objects = document.RootElement.GetProperty("objects").EnumerateArray().ToArray();
        var result = Select("CPU 1511T-1 PN", "V2.6", "6ES7 511-1TK01-0AB0");
        Check(result.Complete && result.CpuFamily == "S7-1500" && result.TechnologyCpu, "1511T was not classified from the CPU text.");
        Check(result.CatalogueDescription != null && result.CatalogueDescription.Contains("not a per-order-number"), "The catalogue limit was dropped.");
        Check(result.TechnologyObjects.Count > 0 && result.TechnologyObjects.Count < objects.Length, "The tool returned the whole catalogue or nothing.");
        var speed = objects.Single(item => item.GetProperty("cpu").GetString() == "S7-1500" && item.GetProperty("name").GetString() == "TO_SpeedAxis");
        var returned = result.TechnologyObjects.Single(item => item.Name == "TO_SpeedAxis");
        Check(returned.Version == speed.GetProperty("version").GetString() && returned.SystemLibElement == "TO_SpeedAxis", "The speed-axis row was not taken from the catalogue file.");
        var cam = result.TechnologyObjects.Single(item => item.Name == "TO_Cam (S7-1500T)");
        Check(cam.SystemLibElement == "TO_Cam" && cam.Footnotes.Any(text => text.Contains("Writing parameters")), "TO_Cam lost its Create name or footnote.");
        var names = result.TechnologyObjects.Select(item => item.Name).ToArray();
        Check(names.Contains("TO_Kinematics (S7-1500T)") && names.Contains("PID_3Step") && !names.Contains("TO_LeadingAxisProxy (S7-1500T)") && !names.Contains("TO_CommandTable"), "1511T V2.6 included a row its firmware or family does not allow.");
    }

    private static void StandardCpu()
    {
        var result = Select("CPU 1511-1 PN", "V2.6");
        Check(result.Complete && result.CpuFamily == "S7-1500" && !result.TechnologyCpu, "A standard 1511 was treated as a technology CPU.");
        Check(result.TechnologyObjects.Any(item => item.Name == "TO_SpeedAxis") && result.TechnologyObjects.All(item => !item.Name.Contains("(S7-1500T)")), "Technology-CPU catalogue rows leaked onto a standard CPU.");
    }

    private static void Families()
    {
        var classic = Select("CPU 1214C DC/DC/DC", "V4.2");
        Check(classic.CpuFamily == "S7-1200" && classic.TechnologyObjects.Any(item => item.Name == "TO_CommandTable") && classic.TechnologyObjects.All(item => item.Name != "TO_SpeedAxis"), "S7-1200 did not stay on its catalogue rows.");
        Check(Select("CPU 1214C", "V4.1").Complete && Select("CPU 1214C", "V4.1").TechnologyObjects.Count == 0, "Firmware below the catalogue minimum still returned objects.");
        var g2 = Select("CPU 1212C G2", "V1.0");
        Check(g2.CpuFamily == "S7-1200 G2" && g2.TechnologyObjects.Single(item => item.Name == "TO_SpeedAxis").Version.Contains("V8.0"), "S7-1200 G2 did not use its catalogue version.");
        var legacy = Select("CPU 314C-2 PN/DP", "V3.3");
        Check(legacy.CpuFamily == "S7-300/400" && legacy.TechnologyObjects.Any(item => item.Name == "AXIS_REF") && legacy.TechnologyObjects.All(item => item.Name != "PID_Compact"), "S7-300 did not stay on its catalogue rows.");
    }

    private static void Gaps()
    {
        var unknown = Select("HMI TP1200", null);
        Check(!unknown.Complete && unknown.TechnologyObjects.Count == 0 && unknown.Errors[0].Origin == "bridge", "An unrecognized device invented catalogue rows.");
        var unread = Select("CPU 1511T-1 PN", null);
        Check(!unread.Complete && unread.TechnologyObjects.Any(item => item.Name == "CONT_C") && unread.TechnologyObjects.All(item => item.Name != "TO_SpeedAxis"), "A missing firmware still applied a firmware gate.");
    }
}
