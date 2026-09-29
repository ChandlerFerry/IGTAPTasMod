using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Box2D;
using IGTAPTasMod;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// Serializes a <see cref="SimSnapshot"/> and restores it into another simulation of the same trace and world export:
    /// a Full-TAS solve's goal-boundary states persist under the run's archive dir (docs/fulltas.md "Offline search"), so a
    /// new solve or window starts from the stored state instead of replaying the run's inputs up to it. Every load of a
    /// world export creates its objects in the same order, so references travel as the export's instance ids (world
    /// objects), collider ids and fixture indexes (physics), and every other value field by field, as
    /// <see cref="FieldState.Capture"/> sees it: a restored state continues exactly as the replayed one. Anything this
    /// can't write throws <see cref="NotSupportedException"/> and the caller keeps replaying instead. A snapshot to save
    /// must be its simulation's current state (the fixture indexes are read from the live world).
    /// </summary>
    public sealed class SnapshotStore
    {
        // 2: each type in the table carries its field layout (Layout), so a snapshot of a build whose types changed is dropped,
        // not misread (a field that became a tuple array read as a bool crashed the solve after a redeploy).
        // 3: the engine state leaves out MacroEngine._steps (FieldState.For), so its values sit one slot earlier after it.
        public const int Format = 3;

        /// <summary>A stable hash of the fields a snapshot writes for type (names and declared types, in order).</summary>
        static int Layout(Type type)
        {
            uint h = 2166136261;
            void Add(string s) { foreach (char c in s) h = (h ^ c) * 16777619; h = (h ^ '|') * 16777619; }
            foreach (FieldInfo f in FieldState.For(type)) { Add(f.Name); Add(f.FieldType.FullName ?? f.FieldType.Name); }
            return (int)h;
        }

        readonly Simulation _sim;
        readonly World _world;
        readonly PhysicsWorld _physics;

        public SnapshotStore(Simulation sim)
        {
            _sim = sim;
            _world = sim.World;
            _physics = sim.Physics;
        }

        /// <summary>Hashes the inputs a persisted state was reached with: <see cref="Feed"/> chains across a prefix's parts.</summary>
        public const ulong HashSeed = 14695981039346656037UL;

        public static ulong Feed(ulong hash, InputTick t)
        {
            hash = Feed(hash, t.x); hash = Feed(hash, t.y); hash = Feed(hash, t.held); hash = Feed(hash, t.press);
            hash = Feed(hash, t.release); hash = Feed(hash, t.dash); hash = Feed(hash, t.dashJump, 4);
            hash = Feed(hash, t.springDash, 4); hash = Feed(hash, t.lockX.HasValue);
            if (t.lockX.HasValue) hash = Feed(hash, t.lockX.Value);
            hash = Feed(hash, t.lockY.HasValue);
            if (t.lockY.HasValue) hash = Feed(hash, t.lockY.Value);
            hash = Feed(hash, t.turnX.HasValue);
            if (t.turnX.HasValue) hash = Feed(hash, t.turnX.Value);
            hash = Feed(hash, t.reset);
            if (t.pause > 0) hash = Feed(hash, ((long)t.pauseTick << 32) | (uint)t.pause, 8);
            if (t.pause > 0 && t.pauseFrame != 0) hash = Feed(hash, t.pauseFrame, 4);
            return hash;
        }

        public static ulong Feed(ulong hash, float value) { return Feed(hash, BitConverter.SingleToInt32Bits(value), 4); }

        public static ulong Feed(ulong hash, bool value) { return Feed(hash, value ? 1 : 0, 1); }

        public static ulong Feed(ulong hash, long value, int bytes)
        {
            for (int i = 0; i < bytes; i++) { hash ^= (byte)(value >> (8 * i)); hash *= 1099511628211UL; }
            return hash;
        }

        public byte[] Save(SimSnapshot s)
        {
            var o = new Out(this, new HashSet<int>(_physics.ChangedIdsOf(s.Physics)));
            foreach (string name in RootOrder) o.Field(s, RootField(name));
            var bytes = new MemoryStream();
            using (var w = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes("IGTSNAP1"));
                w.Write(Format);
                o.WriteTables(w);
                w.Write(o.Payload());
            }
            return bytes.ToArray();
        }

        public SimSnapshot Load(byte[] data)
        {
            var i = new In(this, data);
            var s = new SimSnapshot();
            foreach (string name in RootOrder)
            {
                FieldInfo f = RootField(name);
                f.SetValue(s, i.Value(f.FieldType));
                switch (name)
                {
                    // The pre-apply mirrors what Simulation.Restore does (it runs it again, unchanged): the physics
                    // restore's rebuilds below read the rebase state, the player's collider geometry and the toggles.
                    case "Rebases": _sim.PutRebases(s.Rebases); break;
                    case "Colliders": ApplyPose(s); break;
                    case "Layers": _sim.TrackToggles(s); ApplyToggles(s); break;
                }
            }
            i.Adopt(s.Physics);
            return s;
        }

        static readonly Dictionary<string, FieldInfo> RootFields = new Dictionary<string, FieldInfo>();

        static FieldInfo RootField(string name)
        {
            // Solver workers load snapshots on their own threads.
            lock (RootFields)
            {
                if (RootFields.TryGetValue(name, out FieldInfo cached)) return cached;
                FieldInfo f = typeof(SimSnapshot).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(nameof(SimSnapshot), name);
                return RootFields[name] = f;
            }
        }

        /// <summary>Written and read in this order: the fields <see cref="Load"/> pre-applies come before <c>Physics</c>.</summary>
        static readonly string[] RootOrder =
        {
            "Tick", "Rebases", "RebasePending", "RebaseDue", "Position", "LocalScale", "Rotation", "Colliders",
            "Active", "Enabled", "Layers", "Physics", "Movement", "Engine", "Courses", "Pending", "Touched",
            "Routines", "Economy", "Goal", "Random", "Waypoints", "VmanTimeLeft", "StepFrom", "StepTo", "Animation",
        };

        void ApplyPose(SimSnapshot s)
        {
            Transform t = _sim.Player.transform;
            t.m_Position = s.Position; t.m_LocalPosition = t.m_Parent == null ? s.Position : s.Position - t.m_Parent.m_Position;
            t.m_LocalScale = s.LocalScale; t.m_LossyScale = s.LocalScale;
            t.m_Rotation = s.Rotation; t.m_LocalRotation = s.Rotation;
            BoxCollider2D[] boxes = _sim.Player.GetComponents<BoxCollider2D>();
            for (int k = 0; k < boxes.Length && k < s.Colliders.Length; k++)
            {
                boxes[k].m_Offset = s.Colliders[k].offset;
                boxes[k].m_Size = s.Colliders[k].size;
                boxes[k].m_ExcludeLayers = s.Colliders[k].exclude;
                boxes[k].m_Enabled = s.Colliders[k].enabled;
            }
        }

        void ApplyToggles(SimSnapshot s)
        {
            _physics.Suspended = true;
            int k = 0;
            foreach (var (g, loaded) in _sim.ActiveAsLoaded)
            {
                bool active = Saved(s.Active, k++, g, loaded);
                if (g.activeSelf != active) g.SetActive(active);
            }
            k = 0;
            foreach (var (b, loaded) in _sim.EnabledAsLoaded)
            {
                bool enabled = Saved(s.Enabled, k++, b, loaded);
                if (b.enabled != enabled) b.enabled = enabled;
            }
            k = 0;
            foreach (var (g, loaded) in _sim.LayerAsLoaded) g.m_Layer = Saved(s.Layers, k++, g, loaded);
            _physics.Suspended = false;
        }

        /// <summary>The value a saved list holds for the index-th object tracked since load, or its loaded value (Simulation.Saved).</summary>
        static TValue Saved<TKey, TValue>((TKey target, TValue value)[] saved, int index, TKey key, TValue loaded) where TKey : class
        {
            if (index >= saved.Length) return loaded;
            if (ReferenceEquals(saved[index].target, key)) return saved[index].value;
            foreach (var (target, value) in saved) if (ReferenceEquals(target, key)) return value;
            return loaded;
        }

        #region value codec

        enum Tag : byte
        {
            Null = 0, Back = 1, Bool = 2, I32 = 3, I64 = 4, F32 = 5, F64 = 6, Char = 7, Str = 8, Enum = 9,
            Struct = 10, Class = 11, Array = 12, Multi = 13, List = 14, HashSet = 15, Queue = 16, Stack = 17,
            Dictionary = 18, Handle = 19, Decimal = 20, TypedScalar = 21,
        }

        sealed class Out
        {
            readonly SnapshotStore _owner;
            readonly MemoryStream _bytes = new MemoryStream();
            readonly BinaryWriter _w;
            readonly List<Type> _typeList = new List<Type>();
            readonly Dictionary<Type, int> _typeIds = new Dictionary<Type, int>();
            readonly Dictionary<object, int> _seen = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            readonly List<(int owner, int index, bool free, int fingerprint)> _fixtures = new List<(int, int, bool, int)>();
            readonly Dictionary<object, int> _fixtureIds = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            readonly HashSet<int> _changed;

            public Out(SnapshotStore owner, HashSet<int> changed)
            {
                _owner = owner;
                _w = new BinaryWriter(_bytes, Encoding.UTF8, true);
                _changed = changed;
            }

            public byte[] Payload() => _bytes.ToArray();

            public void WriteTables(BinaryWriter w)
            {
                w.Write(_typeList.Count);
                foreach (Type t in _typeList) { w.Write(t.AssemblyQualifiedName); w.Write(Layout(t)); }
                w.Write(_fixtures.Count);
                foreach (var (owner, index, free, fingerprint) in _fixtures)
                {
                    w.Write(owner); w.Write(index); w.Write(free); w.Write(fingerprint);
                }
            }

            int TypeId(Type type)
            {
                if (_typeIds.TryGetValue(type, out int id)) return id;
                _typeList.Add(type);
                return _typeIds[type] = _typeList.Count - 1;
            }

            public void Field(object target, FieldInfo f) { Value(f.GetValue(target), f.FieldType); }

            public void Value(object value, Type declared)
            {
                if (value == null) { _w.Write((byte)Tag.Null); return; }
                Type type = value.GetType();
                if (type.IsPrimitive || type == typeof(string) || type.IsEnum || type == typeof(decimal))
                {
                    // Scalars don't gain from identity tracking except strings (MacroStep labels repeat).
                    if (type == typeof(string))
                    {
                        if (Back(value)) return;
                        _w.Write((byte)Tag.Str); _w.Write((string)value);
                        return;
                    }
                    if (type != declared && !type.IsEnum)
                    {
                        // A boxed scalar in an object-typed slot keeps its exact type (Tag.Enum carries its own).
                        _w.Write((byte)Tag.TypedScalar);
                        _w.Write(TypeId(type));
                    }
                    Scalar(value, type);
                    return;
                }
                if (!type.IsValueType && Back(value)) return;
                if (typeof(Object).IsAssignableFrom(type))
                {
                    _w.Write((byte)Tag.Handle);
                    _w.Write((byte)HandleKind.WorldObject);
                    _w.Write(_owner.WorldId((Object)value));
                    return;
                }
                if (type == typeof(b2Body))
                {
                    _w.Write((byte)Tag.Handle);
                    _w.Write((byte)HandleKind.Body);
                    _w.Write(_owner._physics.RigidbodyIdOf((b2Body)value));
                    return;
                }
                if (type == typeof(b2Contact))
                {
                    // Created again by b2World.LoadState from the contact's fixtures; nothing else reads it.
                    _w.Write((byte)Tag.Handle);
                    _w.Write((byte)HandleKind.Contact);
                    return;
                }
                if (type == PhysicsWorld.ColliderStateType)
                {
                    _w.Write((byte)Tag.Handle);
                    _w.Write((byte)HandleKind.Collider);
                    _w.Write(_owner._physics.IdOfState(value));
                    return;
                }
                if (type == typeof(b2Fixture))
                {
                    _w.Write((byte)Tag.Handle);
                    _w.Write((byte)HandleKind.Fixture);
                    _w.Write(FixtureId((b2Fixture)value));
                    return;
                }
                if (type.IsArray) { ArrayValue((Array)value, type); return; }
                if (type.IsGenericType)
                {
                    Type definition = type.GetGenericTypeDefinition();
                    if (definition == typeof(List<>)) { Sequence(value, type, Tag.List); return; }
                    if (definition == typeof(HashSet<>)) { Sequence(value, type, Tag.HashSet); return; }
                    if (definition == typeof(Queue<>)) { Sequence(value, type, Tag.Queue); return; }
                    if (definition == typeof(Stack<>)) { Sequence(value, type, Tag.Stack); return; }
                    if (definition == typeof(Dictionary<,>)) { DictionaryValue(value, type); return; }
                }
                if (typeof(Delegate).IsAssignableFrom(type))
                    throw new NotSupportedException("a " + type.Name + " value can't be stored in a snapshot");
                Fields(value, type, type.IsValueType ? Tag.Struct : Tag.Class);
            }

            bool Back(object value)
            {
                if (_seen.TryGetValue(value, out int id))
                {
                    _w.Write((byte)Tag.Back);
                    _w.Write(id);
                    return true;
                }
                _seen[value] = _seen.Count;
                return false;
            }

            void Scalar(object value, Type type)
            {
                if (type == typeof(bool)) { _w.Write((byte)Tag.Bool); _w.Write((bool)value); }
                else if (type == typeof(int)) { _w.Write((byte)Tag.I32); _w.Write((int)value); }
                else if (type == typeof(uint)) { _w.Write((byte)Tag.I32); _w.Write(unchecked((int)(uint)value)); }
                else if (type == typeof(float)) { _w.Write((byte)Tag.F32); _w.Write((float)value); }
                else if (type == typeof(double)) { _w.Write((byte)Tag.F64); _w.Write((double)value); }
                else if (type == typeof(char)) { _w.Write((byte)Tag.Char); _w.Write((char)value); }
                else if (type == typeof(decimal)) { _w.Write((byte)Tag.Decimal); foreach (int part in decimal.GetBits((decimal)value)) _w.Write(part); }
                else if (type.IsEnum) { _w.Write((byte)Tag.Enum); _w.Write(TypeId(type)); _w.Write(Convert.ToInt64(value)); }
                else { _w.Write((byte)Tag.I64); _w.Write(Raw(value, type)); }
            }

            static long Raw(object value, Type type)
            {
                if (type == typeof(long)) return (long)value;
                if (type == typeof(ulong)) return unchecked((long)(ulong)value);
                return Convert.ToInt64(value);
            }

            void Fields(object value, Type type, Tag tag)
            {
                _w.Write((byte)tag);
                _w.Write(TypeId(type));
                foreach (FieldInfo f in FieldState.For(type)) Field(value, f);
            }

            void ArrayValue(Array array, Type type)
            {
                Type element = type.GetElementType();
                if (array.Rank == 1)
                {
                    _w.Write((byte)Tag.Array);
                    _w.Write(TypeId(type));
                    _w.Write(array.Length);
                    foreach (object item in array) Value(item, element);
                    return;
                }
                _w.Write((byte)Tag.Multi);
                _w.Write(TypeId(type));
                _w.Write(array.Rank);
                for (int d = 0; d < array.Rank; d++) _w.Write(array.GetLength(d));
                foreach (object item in array) Value(item, element);
            }

            void Sequence(object value, Type type, Tag tag)
            {
                _w.Write((byte)tag);
                _w.Write(TypeId(type));
                Type item = type.GetGenericArguments()[0];
                var list = new List<object>();
                foreach (object element in (IEnumerable)value) list.Add(element);
                _w.Write(list.Count);
                foreach (object element in list) Value(element, item);
            }

            void DictionaryValue(object value, Type type)
            {
                _w.Write((byte)Tag.Dictionary);
                _w.Write(TypeId(type));
                Type[] args = type.GetGenericArguments();
                var pairs = new List<object[]>();
                foreach (object pair in (IEnumerable)value)
                {
                    PropertyInfo key = pair.GetType().GetProperty("Key"), val = pair.GetType().GetProperty("Value");
                    pairs.Add(new[] { key.GetValue(pair), val.GetValue(pair) });
                }
                _w.Write(pairs.Count);
                foreach (object[] pair in pairs) { Value(pair[0], args[0]); Value(pair[1], args[1]); }
            }

            int FixtureId(b2Fixture fixture)
            {
                if (_fixtureIds.TryGetValue(fixture, out int id)) return id;
                (int owner, int index) = _owner._physics.LocateFixture(fixture);
                // A fixture a rebuild took out lives on only in the broadphase tree's shared base nodes, which every
                // snapshot's own delta overwrites before the tree is read: such a reference restores as null.
                bool free = index >= 0 && (_changed.Contains(owner) || _owner._physics.IsPlayerCollider(owner));
                _fixtures.Add((owner, index, free, index >= 0 ? PhysicsWorld.FingerprintOf(fixture) : 0));
                return _fixtureIds[fixture] = _fixtures.Count - 1;
            }
        }

        enum HandleKind : byte { WorldObject = 1, Body = 2, Collider = 3, Fixture = 4, Contact = 5 }

        sealed class In
        {
            readonly SnapshotStore _owner;
            readonly BinaryReader _r;
            readonly Type[] _types;
            readonly List<object> _objects = new List<object>();
            readonly (int owner, int index, bool free, int fingerprint)[] _fixtures;
            b2Fixture[] _bound;
            readonly List<(object state, b2Fixture[] fixtures)> _changed = new List<(object, b2Fixture[])>();

            public In(SnapshotStore owner, byte[] data)
            {
                _owner = owner;
                var stream = new MemoryStream(data, false);
                _r = new BinaryReader(stream, Encoding.UTF8, true);
                if (Encoding.ASCII.GetString(_r.ReadBytes(8)) != "IGTSNAP1" || _r.ReadInt32() != Format)
                    throw new NotSupportedException("a snapshot file of another engine build; dropping it");
                int types = _r.ReadInt32();
                _types = new Type[types];
                for (int i = 0; i < types; i++)
                {
                    string name = _r.ReadString();
                    _types[i] = Resolve(name);
                    if (_r.ReadInt32() != Layout(_types[i])) throw new NotSupportedException("a snapshot's type changed its fields since (" + name + "); dropping it");
                }
                int fixtures = _r.ReadInt32();
                _fixtures = new (int, int, bool, int)[fixtures];
                for (int i = 0; i < fixtures; i++) _fixtures[i] = (_r.ReadInt32(), _r.ReadInt32(), _r.ReadBoolean(), _r.ReadInt32());
            }

            static Type Resolve(string name)
            {
                Type type = Type.GetType(name, false);
                if (type != null) return type;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if ((type = a.GetType(name.Substring(0, name.IndexOf(',')), false)) != null) return type;
                throw new NotSupportedException("a snapshot's type is gone (" + name + "); dropping it");
            }

            /// <summary>Every fixture the snapshot references, as this world's object with that shape.</summary>
            void Bind()
            {
                if (_bound != null) return;
                _bound = new b2Fixture[_fixtures.Length];
                // Rebuilds first (a collider's rebuild replaces its composite's fixtures too), binds second. A fixture can
                // be either generation its collider holds: the loaded one, or the rebuilt one a snapshot lists it for.
                for (int i = 0; i < _fixtures.Length; i++)
                {
                    var (owner, index, free, fingerprint) = _fixtures[i];
                    if (index < 0) continue;
                    if (Found(owner, index, free, fingerprint) != null) continue;
                    if (!free) throw new NotSupportedException("a collider's loaded shapes differ from the snapshot's (collider " + owner + " #" + index + "); dropping it");
                    _owner._physics.RebuildCollider(owner);
                }
                for (int i = 0; i < _fixtures.Length; i++)
                {
                    var (owner, index, free, fingerprint) = _fixtures[i];
                    if (index < 0) continue;
                    b2Fixture fixture = Found(owner, index, free, fingerprint);
                    if (fixture == null)
                        throw new NotSupportedException("a collider's shapes can't be rebuilt to the snapshot's (collider " + owner + " #" + index + "); dropping it");
                    _bound[i] = fixture;
                }
            }

            b2Fixture Found(int owner, int index, bool free, int fingerprint)
            {
                (List<b2Fixture> fixtures, b2Fixture[] pristine) = _owner._physics.FixturesAt(owner);
                IList<b2Fixture> first = free ? fixtures : (IList<b2Fixture>)pristine ?? fixtures;
                IList<b2Fixture> second = free ? (IList<b2Fixture>)pristine ?? fixtures : fixtures;
                if (Matches(first, index, fingerprint)) return first[index];
                if (Matches(second, index, fingerprint)) return second[index];
                return null;
            }

            static bool Matches(IList<b2Fixture> list, int index, int fingerprint)
                => index >= 0 && index < list.Count && PhysicsWorld.FingerprintOf(list[index]) == fingerprint;

            public void Adopt(object physicsSnapshot) { _owner._physics.AdoptChanged(physicsSnapshot); }

            public object Value(Type declared)
            {
                Tag tag = (Tag)_r.ReadByte();
                switch (tag)
                {
                    case Tag.Null: return null;
                    case Tag.Back: return _objects[_r.ReadInt32()];
                    case Tag.Str:
                        string s = _r.ReadString();
                        _objects.Add(s);
                        return s;
                    case Tag.TypedScalar:
                        Type exact = _types[_r.ReadInt32()];
                        return Coerce(ScalarPayload((Tag)_r.ReadByte()), exact);
                    case Tag.Enum:
                        Type enumType = _types[_r.ReadInt32()];
                        long raw = _r.ReadInt64();
                        return Enum.ToObject(enumType, raw);
                    case Tag.Bool: case Tag.I32: case Tag.I64: case Tag.F32: case Tag.F64: case Tag.Char: case Tag.Decimal:
                        return Coerce(ScalarPayload(tag), declared);
                }
                return Composite(tag);
            }

            object ScalarPayload(Tag tag)
            {
                switch (tag)
                {
                    case Tag.Bool: return _r.ReadBoolean();
                    case Tag.I32: return _r.ReadInt32();
                    case Tag.I64: return _r.ReadInt64();
                    case Tag.F32: return _r.ReadSingle();
                    case Tag.F64: return _r.ReadDouble();
                    case Tag.Char: return (char)_r.ReadUInt16();
                    case Tag.Decimal:
                        var parts = new int[4];
                        for (int i = 0; i < 4; i++) parts[i] = _r.ReadInt32();
                        return new decimal(parts);
                    default: throw new NotSupportedException("a snapshot byte stream of another engine build; dropping it");
                }
            }

            /// <summary>Back to the slot's own type (a uint field's raw bits, a Nullable field's boxed value).</summary>
            static object Coerce(object value, Type declared)
            {
                if (declared == null || declared == typeof(object) || value == null || declared.IsInstanceOfType(value)) return value;
                Type want = Nullable.GetUnderlyingType(declared) ?? declared;
                if (want.IsEnum) return Enum.ToObject(want, Convert.ToInt64(value));
                long raw = Convert.ToInt64(value);
                switch (Type.GetTypeCode(want))
                {
                    case TypeCode.Int32: return want == typeof(uint) ? unchecked((uint)raw) : (int)raw;
                    case TypeCode.UInt32: return unchecked((uint)raw);
                    case TypeCode.Int64: return raw;
                    case TypeCode.UInt64: return unchecked((ulong)raw);
                    case TypeCode.Int16: return (short)raw;
                    case TypeCode.UInt16: return unchecked((ushort)raw);
                    case TypeCode.Byte: return (byte)raw;
                    case TypeCode.SByte: return (sbyte)raw;
                    case TypeCode.Single: return Convert.ToSingle(value);
                    case TypeCode.Double: return Convert.ToDouble(value);
                    case TypeCode.Char: return Convert.ToChar(value);
                    case TypeCode.Decimal: return Convert.ToDecimal(value);
                    case TypeCode.Boolean: return Convert.ToBoolean(value);
                    default: return value;
                }
            }

            object Composite(Tag tag)
            {
                switch (tag)
                {
                    case Tag.Struct:
                    case Tag.Class:
                    {
                        Type type = _types[_r.ReadInt32()];
                        object value = RuntimeHelpers.GetUninitializedObject(type);
                        if (tag == Tag.Class) _objects.Add(value);
                        foreach (FieldInfo f in FieldState.For(type)) Set(f, value, Value(f.FieldType));
                        return value;
                    }
                    case Tag.Array:
                    case Tag.Multi:
                    {
                        Type type = _types[_r.ReadInt32()];
                        Type element = type.GetElementType();
                        if (tag == Tag.Array)
                        {
                            int length = _r.ReadInt32();
                            Array array = Array.CreateInstance(element, length);
                            _objects.Add(array);
                            for (int i = 0; i < length; i++) array.SetValue(Value(element), i);
                            return array;
                        }
                        int rank = _r.ReadInt32();
                        var lengths = new int[rank];
                        for (int d = 0; d < rank; d++) lengths[d] = _r.ReadInt32();
                        Array multi = Array.CreateInstance(element, lengths);
                        _objects.Add(multi);
                        foreach (int[] index in Indices(lengths)) multi.SetValue(Value(element), index);
                        return multi;
                    }
                    case Tag.List:
                    case Tag.HashSet:
                    case Tag.Queue:
                    case Tag.Stack:
                    {
                        Type type = _types[_r.ReadInt32()];
                        Type item = type.GetGenericArguments()[0];
                        int count = _r.ReadInt32();
                        // The container goes in the identity list where the writer put it: before its items.
                        object container = Activator.CreateInstance(type);
                        _objects.Add(container);
                        var items = new object[count];
                        for (int i = 0; i < count; i++) items[i] = Value(item);
                        if (tag == Tag.List)
                        {
                            var list = (IList)container;
                            foreach (object o in items) list.Add(o);
                        }
                        else if (tag == Tag.Stack)
                        {
                            // A stack enumerates top first; pushing the saved order back in reverse, as FieldState.Copy does.
                            MethodInfo push = type.GetMethod("Push");
                            for (int i = count - 1; i >= 0; i--) push.Invoke(container, new[] { items[i] });
                        }
                        else
                        {
                            MethodInfo add = type.GetMethod(tag == Tag.HashSet ? "Add" : "Enqueue");
                            foreach (object o in items) add.Invoke(container, new[] { o });
                        }
                        return container;
                    }
                    case Tag.Dictionary:
                    {
                        Type type = _types[_r.ReadInt32()];
                        Type[] args = type.GetGenericArguments();
                        int count = _r.ReadInt32();
                        object dictionary = Activator.CreateInstance(type);
                        _objects.Add(dictionary);
                        var pairs = new object[count][];
                        for (int i = 0; i < count; i++) pairs[i] = new[] { Value(args[0]), Value(args[1]) };
                        MethodInfo add = type.GetMethod("Add");
                        foreach (object[] pair in pairs) add.Invoke(dictionary, pair);
                        return dictionary;
                    }
                    case Tag.Handle: return Handle();
                    default: throw new NotSupportedException("a snapshot byte stream of another engine build; dropping it");
                }
            }

            static IEnumerable<int[]> Indices(int[] lengths)
            {
                var index = new int[lengths.Length];
                while (true)
                {
                    yield return (int[])index.Clone();
                    int d = index.Length - 1;
                    for (; d >= 0 && ++index[d] == lengths[d]; d--) index[d] = 0;
                    if (d < 0) yield break;
                }
            }

            object Handle()
            {
                var kind = (HandleKind)_r.ReadByte();
                object value;
                switch (kind)
                {
                    case HandleKind.WorldObject: value = _owner.WorldObject(_r.ReadInt32()); break;
                    case HandleKind.Body: value = _owner._physics.BodyAt(_r.ReadInt32()); break;
                    case HandleKind.Collider: value = _owner._physics.StateAt(_r.ReadInt32()); break;
                    case HandleKind.Contact: value = null; break;
                    case HandleKind.Fixture:
                        Bind();
                        value = _bound[_r.ReadInt32()];
                        break;
                    default: throw new NotSupportedException("a snapshot byte stream of another engine build; dropping it");
                }
                // In the identity list where the writer put the handle's object.
                _objects.Add(value);
                return value;
            }

            static void Set(FieldInfo f, object target, object value) { Setter(f)(target, value); }
        }

        Object WorldObject(int id)
            => _world.Objects.TryGetValue(id, out Object o) && !o.m_Destroyed ? o
                : throw new NotSupportedException("a world object of the snapshot is gone; dropping it");

        int WorldId(Object o) => !o.m_Destroyed && _world.Objects.ContainsKey(o.GetInstanceID()) ? o.GetInstanceID()
            : throw new NotSupportedException("a snapshot holds a world object the export doesn't; not saved");

        static readonly Dictionary<FieldInfo, Action<object, object>> Setters = new Dictionary<FieldInfo, Action<object, object>>();

        /// <summary>A field setter that also works on initonly fields (WaitForSeconds.m_Seconds, Pair.A) and boxed structs.</summary>
        static Action<object, object> Setter(FieldInfo f)
        {
            lock (Setters)
            {
                if (Setters.TryGetValue(f, out Action<object, object> setter)) return setter;
                var dm = new DynamicMethod("snapset_" + f.Name, typeof(void), new[] { typeof(object), typeof(object) }, f.DeclaringType.Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(f.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, f.DeclaringType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Unbox_Any, f.FieldType);
                il.Emit(OpCodes.Stfld, f);
                il.Emit(OpCodes.Ret);
                return Setters[f] = (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
            }
        }

        #endregion

        #region resume

        static readonly int RunTicksIndex = FieldIndex(typeof(MacroEngine), "_runTicks");
        static readonly int CountEveryIndex = FieldIndex(typeof(MacroEngine), "_countEveryPhysicsTick");

        static int FieldIndex(Type type, string name)
        {
            FieldInfo[] fields = FieldState.For(type);
            for (int i = 0; i < fields.Length; i++) if (fields[i].Name == name) return i;
            throw new MissingFieldException(type.Name, name);
        }
        readonly Dictionary<SnapshotIndex.Entry, SimSnapshot> _decoded = new Dictionary<SnapshotIndex.Entry, SimSnapshot>();

        /// <summary>
        /// The stored state decoded for this world, made to sit at <paramref name="tick"/> of the list it resumes (the run's
        /// own state there has that many input ticks counted, as MacroEngine.RebaseInputs leaves a run's start's).
        /// </summary>
        public SimSnapshot StateOf(SnapshotIndex.Entry entry, int tick)
        {
            if (!_decoded.TryGetValue(entry, out SimSnapshot s)) _decoded[entry] = s = Load(entry.Data);
            s.Tick = tick;
            s.Engine[RunTicksIndex] = (long)tick;
            s.Engine[CountEveryIndex] = true;
            return s;
        }

        #endregion
    }

    /// <summary>
    /// Goal-boundary states of a run, persisted beside its archive and keyed by the hash of the inputs up to them: a
    /// Full-TAS solve's windows start from one instead of replaying the run up to there, and an archive cell whose own
    /// prefix passes one replays from it (docs/fulltas.md "Offline search"). A route edit hashes differently, so a stale
    /// state is never picked up.
    /// </summary>
    public sealed class SnapshotIndex
    {
        public sealed class Entry
        {
            public ulong Hash;
            public int Tick, Goal;
            public byte[] Data;
        }

        readonly Dictionary<ulong, Entry> _byHash = new Dictionary<ulong, Entry>();
        List<Entry> _byTick = new List<Entry>();
        /// <summary>Workers look states up while a worker's first prefix replay files them.</summary>
        readonly object _gate = new object();

        /// <summary>How many of the run's inputs come before the window these are used in, and their hash's chain.</summary>
        public int Base;
        public ulong BaseHash = SnapshotStore.HashSeed;

        public int Count { get { return _byHash.Count; } }

        public void Add(Entry entry)
        {
            lock (_gate)
            {
                if (_byHash.TryGetValue(entry.Hash, out Entry old)) { old.Data = entry.Data; old.Goal = entry.Goal; return; }
                _byHash[entry.Hash] = entry;
                _byTick.Add(entry);
                _byTick.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            }
        }

        public bool TryGet(ulong hash, out Entry entry) { lock (_gate) return _byHash.TryGetValue(hash, out entry); }

        /// <summary>Hash of the run's inputs[0..count].</summary>
        public static ulong Hash(List<InputTick> inputs, int count, ulong from = SnapshotStore.HashSeed)
        {
            ulong hash = from;
            for (int i = 0; i < count; i++) hash = SnapshotStore.Feed(hash, inputs[i]);
            return hash;
        }

        /// <summary>
        /// The latest stored state the source's own prefix reaches at or before <paramref name="through"/> and later than
        /// <paramref name="after"/>, its inputs' hash up to it matching this window's chain. Null: none of them.
        /// </summary>
        public Entry Find(List<InputTick> source, int through, int after)
        {
            Entry[] byTick;
            lock (_gate) byTick = _byTick.ToArray();
            Entry best = null;
            ulong hash = BaseHash;
            int next = 0;
            for (int i = 0; i <= through; i++)
            {
                hash = SnapshotStore.Feed(hash, source[i]);
                while (next < byTick.Length && byTick[next].Tick - Base <= i + 1)
                {
                    Entry e = byTick[next++];
                    int at = e.Tick - Base;
                    if (at == i + 1 && at > after && e.Hash == hash) best = e;
                }
                if (next == byTick.Length && best != null) break;
            }
            return best;
        }
    }
}
