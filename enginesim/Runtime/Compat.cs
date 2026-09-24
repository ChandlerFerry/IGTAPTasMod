using System;
using System.Reflection;

// The plugin files compiled into the runtime (MacroEngine, FieldState) use these plugin/Harmony members.
namespace HarmonyLib
{
    internal static class AccessTools
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static FieldInfo Field(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        public static MethodInfo Method(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod(name, All | BindingFlags.DeclaredOnly);
                if (m != null) return m;
            }
            return null;
        }
    }
}

namespace IGTAPTasMod
{
    internal static class TasPlugin
    {
        public static readonly HostLog Log = new HostLog();
    }

    internal sealed class HostLog
    {
        public void LogInfo(object message) { }
        public void LogWarning(object message) { }
        public void LogError(object message) { }
    }
}
