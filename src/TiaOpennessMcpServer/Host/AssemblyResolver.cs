using System.Reflection;

namespace TiaOpennessMcpServer.Host;

internal static class AssemblyResolver
{
    public static void Register()
    {
        string[] TiaSearchPaths = new[]
        {
            @"C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20",
            @"C:\Program Files\Siemens\Automation\Portal V20\Bin\PublicAPI",
            @"C:\Program Files\Siemens\Automation\Portal V20\Bin\PublicAPI\Client",
            @"C:\Program Files\Siemens\Automation\Portal V20\Bin",
        };
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var n = new AssemblyName(e.Name).Name!;
            foreach (var dir in TiaSearchPaths)
            {
                var p = Path.Combine(dir, n + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
    }
}
