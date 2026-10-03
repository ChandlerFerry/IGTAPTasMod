using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace IGTAPTasMod
{
    internal static class FieldState
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> Fields =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]>();
        static readonly HashSet<string> Skipped = new HashSet<string> { "currentPlayerPath", "currentPlayerSprites", "currentPlayerScales", "HumanClass" };

        internal static FieldInfo[] For(Type type) { return Fields.GetOrAdd(type, Declared); }

        static FieldInfo[] Declared(Type type)
        {
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType) && !Skipped.Contains(f.Name) && !IsEngineSteps(f)) list.Add(f);
            return list.ToArray();
        }

        // MacroEngine's step list is the whole route being played: every resume hands the engine the steps to play
        // (MacroEngine.ResumeOwned), so a capture copying it every time would be thrown away.
        static bool IsEngineSteps(FieldInfo f)
        {
            return f.DeclaringType == typeof(MacroEngine) && f.Name == "_steps";
        }

        public static object[] Capture(object target)
        {
            Accessor a = AccessorFor(target.GetType());
            var values = new object[a.Count];
            a.Read(target, values);
            foreach (int i in a.Copied) values[i] = Copy(values[i]);
            return values;
        }

        public static void Restore(object target, object[] values)
        {
            AccessorFor(target.GetType()).Write(target, values);
        }

        public static bool Matches(object target, object[] values)
        {
            return AccessorFor(target.GetType()).Matches(target, values);
        }

        /// <summary>The target's field values (in <see cref="For"/> order, boxed, not copied) into <paramref name="into"/>, which holds at least as many.</summary>
        public static void Read(object target, object[] into)
        {
            AccessorFor(target.GetType()).Read(target, into);
        }

        static bool Same(object live, object captured)
        {
            if (live == null || captured == null) return live == captured;
            if (live.GetType() != captured.GetType()) return false;
            if (live is Array) return SameItems(live, captured);
            Collection kind = CollectionOf(live.GetType());
            if (kind.Copy == null) return Equals(live, captured);
            return !kind.HashSet && SameItems(live, captured);
        }

        static bool SameItems(object live, object captured)
        {
            System.Collections.IEnumerator a = ((System.Collections.IEnumerable)live).GetEnumerator(), b = ((System.Collections.IEnumerable)captured).GetEnumerator();
            while (true)
            {
                bool more = a.MoveNext();
                if (more != b.MoveNext()) return false;
                if (!more) return true;
                if (!Equals(a.Current, b.Current)) return false;
            }
        }

        internal static object Copy(object value)
        {
            if (value is Array array) return array.Clone();
            Func<object, object> copy = value == null ? null : CollectionOf(value.GetType()).Copy;
            return copy == null ? value : copy(value);
        }

        /// <summary>A List, HashSet, Queue or Stack type's copy (a new one of its items); no copy for any other type.</summary>
        struct Collection
        {
            public Func<object, object> Copy;
            public bool HashSet;
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Collection> Collections =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, Collection>();

        static Collection CollectionOf(Type type) { return Collections.GetOrAdd(type, NewCollection); }

        static Collection NewCollection(Type type)
        {
            var kind = new Collection();
            string method = null;
            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition();
                method = definition == typeof(List<>) ? nameof(CopyList) : definition == typeof(HashSet<>) ? nameof(CopyHashSet)
                    : definition == typeof(Queue<>) ? nameof(CopyQueue) : definition == typeof(Stack<>) ? nameof(CopyStack) : null;
            }
            if (method != null)
            {
                kind.Copy = (Func<object, object>)typeof(FieldState).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(type.GetGenericArguments()).CreateDelegate(typeof(Func<object, object>));
                kind.HashSet = method == nameof(CopyHashSet);
            }
            return kind;
        }

        static object CopyList<T>(object value) { return new List<T>((List<T>)value); }
        static object CopyHashSet<T>(object value) { return new HashSet<T>((HashSet<T>)value); }
        static object CopyQueue<T>(object value) { return new Queue<T>((Queue<T>)value); }

        static object CopyStack<T>(object value)
        {
            T[] items = ((Stack<T>)value).ToArray();
            Array.Reverse(items);
            return new Stack<T>(items);
        }

        /// <summary>
        /// A type's field reads, writes and comparison, compiled once (FieldInfo.GetValue/SetValue are reflection per field):
        /// the same values, copies and results as they gave.
        /// </summary>
        sealed class Accessor
        {
            public int Count;
            /// <summary>The fields whose value <see cref="Copy"/> can replace: a slot that can hold an array or a copied collection.</summary>
            public int[] Copied;
            public Action<object, object[]> Read, Write;
            public Func<object, object[], bool> Matches;
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Accessor> Accessors =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, Accessor>();
        static readonly MethodInfo CopyMethod = typeof(FieldState).GetMethod(nameof(Copy), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo SameMethod = typeof(FieldState).GetMethod(nameof(Same), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo GetTypeMethod = typeof(object).GetMethod("GetType", Type.EmptyTypes);
        static readonly MethodInfo EqualsMethod = typeof(object).GetMethod("Equals", new[] { typeof(object) });
        static readonly MethodInfo TypeOfMethod = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), new[] { typeof(RuntimeTypeHandle) });
        static readonly MethodInfo TypeEqualsMethod = typeof(Type).GetMethod("op_Equality", new[] { typeof(Type), typeof(Type) });

        static Accessor AccessorFor(Type type) { return Accessors.GetOrAdd(type, Build); }

        static bool CopyCanReplace(Type slot)
        {
            if (slot.IsValueType) return false;
            if (slot == typeof(object) || slot == typeof(Array) || slot.IsArray || slot.IsInterface) return true;
            if (!slot.IsGenericType) return false;
            Type definition = slot.GetGenericTypeDefinition();
            return definition == typeof(List<>) || definition == typeof(HashSet<>) || definition == typeof(Queue<>) || definition == typeof(Stack<>);
        }

        static Accessor Build(Type type)
        {
            FieldInfo[] fields = For(type);
            var copied = new List<int>();
            for (int i = 0; i < fields.Length; i++) if (CopyCanReplace(fields[i].FieldType)) copied.Add(i);
            var parameters = new[] { typeof(object), typeof(object[]) };

            var read = new DynamicMethod("FieldState.Read." + type.Name, null, parameters, typeof(FieldState).Module, true);
            ILGenerator il = read.GetILGenerator();
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                Target(il, type);
                il.Emit(OpCodes.Ldfld, f);
                if (f.FieldType.IsValueType) il.Emit(OpCodes.Box, f.FieldType);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Ret);

            var write = new DynamicMethod("FieldState.Write." + type.Name, null, parameters, typeof(FieldState).Module, true);
            il = write.GetILGenerator();
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                Target(il, type);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                if (copied.Contains(i)) il.Emit(OpCodes.Call, CopyMethod);
                if (f.FieldType.IsValueType) il.Emit(OpCodes.Unbox_Any, f.FieldType);
                else if (f.FieldType != typeof(object)) il.Emit(OpCodes.Castclass, f.FieldType);
                il.Emit(OpCodes.Stfld, f);
            }
            il.Emit(OpCodes.Ret);

            // Same(field value, captured) per field, stopping at the first difference. A plain struct field compares in place:
            // Same's checks for a boxed struct come down to captured being that struct and Equals(object) saying so.
            var matches = new DynamicMethod("FieldState.Matches." + type.Name, typeof(bool), parameters, typeof(FieldState).Module, true);
            il = matches.GetILGenerator();
            Label differs = il.DefineLabel();
            LocalBuilder captured = il.DeclareLocal(typeof(object));
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                Type slot = f.FieldType;
                if (slot.IsValueType && Nullable.GetUnderlyingType(slot) == null)
                {
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Stloc, captured);
                    il.Emit(OpCodes.Ldloc, captured);
                    il.Emit(OpCodes.Brfalse, differs);
                    il.Emit(OpCodes.Ldloc, captured);
                    il.Emit(OpCodes.Callvirt, GetTypeMethod);
                    il.Emit(OpCodes.Ldtoken, slot);
                    il.Emit(OpCodes.Call, TypeOfMethod);
                    il.Emit(OpCodes.Call, TypeEqualsMethod);
                    il.Emit(OpCodes.Brfalse, differs);
                    Target(il, type);
                    il.Emit(OpCodes.Ldflda, f);
                    il.Emit(OpCodes.Ldloc, captured);
                    il.Emit(OpCodes.Constrained, slot);
                    il.Emit(OpCodes.Callvirt, EqualsMethod);
                    il.Emit(OpCodes.Brfalse, differs);
                }
                else
                {
                    Target(il, type);
                    il.Emit(OpCodes.Ldfld, f);
                    if (slot.IsValueType) il.Emit(OpCodes.Box, slot);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Call, SameMethod);
                    il.Emit(OpCodes.Brfalse, differs);
                }
            }
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(differs);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);

            return new Accessor
            {
                Count = fields.Length,
                Copied = copied.ToArray(),
                Read = (Action<object, object[]>)read.CreateDelegate(typeof(Action<object, object[]>)),
                Write = (Action<object, object[]>)write.CreateDelegate(typeof(Action<object, object[]>)),
                Matches = (Func<object, object[], bool>)matches.CreateDelegate(typeof(Func<object, object[], bool>)),
            };
        }

        /// <summary>Loads the target as its own type: a class reference, or the address inside a boxed struct.</summary>
        static void Target(ILGenerator il, Type type)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(type.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, type);
        }
    }
}
