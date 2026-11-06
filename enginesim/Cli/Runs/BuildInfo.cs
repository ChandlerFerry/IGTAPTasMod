using System.Linq;
using System.Reflection;

namespace IGTAP.EngineSim.Cli.Runs
{
    static class BuildInfo
    {
        public static readonly string Commit = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "EngineCommit")?.Value is string c && c.Trim().Length > 0 ? c.Trim() : "unknown";
    }
}
