using System.Linq;
using System.Reflection;

namespace IGTAP.EngineSim.Cli.Runs
{
    // Every v2 output carries the build's commit, so what an older engine measured retires itself; "unknown" outside git
    // (the pre-commit hook builds a checkout of the staged tree).
    static class BuildInfo
    {
        public static readonly string Commit = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "EngineCommit")?.Value is string c && c.Trim().Length > 0 ? c.Trim() : "unknown";
    }
}
