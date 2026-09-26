using System;
using System.Reflection;

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
    }
}
