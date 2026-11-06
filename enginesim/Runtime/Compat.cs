using System;
using System.Reflection;
using System.Reflection.Emit;

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

        public delegate ref F FieldRef<in T, F>(T instance);

        public static FieldRef<T, F> FieldRefAccess<T, F>(string fieldName)
        {
            FieldInfo field = Field(typeof(T), fieldName);
            if (field == null || field.IsStatic || field.FieldType != typeof(F))
                throw new MissingFieldException(typeof(T).Name, fieldName);
            var method = new DynamicMethod("FieldRef." + typeof(T).Name + "." + fieldName, typeof(F).MakeByRefType(), new[] { typeof(T) },
                typeof(AccessTools).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Ret);
            return (FieldRef<T, F>)method.CreateDelegate(typeof(FieldRef<T, F>));
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
