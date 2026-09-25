using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace IGTAPTasMod
{
    internal static class FieldState
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> Fields =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]>();
        static readonly HashSet<string> Skipped = new HashSet<string> { "currentPlayerPath", "currentPlayerSprites", "currentPlayerScales" };

        internal static FieldInfo[] For(Type type)
        {
            if (Fields.TryGetValue(type, out FieldInfo[] cached)) return cached;
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType) && !Skipped.Contains(f.Name)) list.Add(f);
            return Fields[type] = list.ToArray();
        }

        public static object[] Capture(object target)
        {
            FieldInfo[] fields = For(target.GetType());
            var values = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++) values[i] = Copy(fields[i].GetValue(target));
            return values;
        }

        public static void Restore(object target, object[] values)
        {
            FieldInfo[] fields = For(target.GetType());
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(target, Copy(values[i]));
        }

        internal static object Copy(object value)
        {
            if (value is Array array) return array.Clone();
            if (value != null && value.GetType().IsGenericType)
            {
                Type definition = value.GetType().GetGenericTypeDefinition();
                if (definition == typeof(List<>) || definition == typeof(HashSet<>) || definition == typeof(Queue<>))
                    return Activator.CreateInstance(value.GetType(), value);
                if (definition == typeof(Stack<>))
                {
                    Array items = (Array)value.GetType().GetMethod("ToArray").Invoke(value, null);
                    Array.Reverse(items);
                    return Activator.CreateInstance(value.GetType(), items);
                }
            }
            return value;
        }
    }
}
