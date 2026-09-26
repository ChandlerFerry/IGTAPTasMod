using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IGTAP.EngineSim
{
    public sealed class ShapeData
    {
        public string Type;
        public float Radius;
        public Vector2[] Vertices;
        public bool UseAdjacentStart, UseAdjacentEnd;
        public Vector2 AdjacentStart, AdjacentEnd;
    }

    public sealed class ColliderData
    {
        public Collider2D Collider;
        public ShapeData[] Shapes = Array.Empty<ShapeData>();
        public float Friction, Bounciness;
        public ColliderData Composite;
        public ShapeData[] SourceShapes;
    }

    public sealed class World
    {
        public readonly Dictionary<int, Object> Objects = new Dictionary<int, Object>();
        public readonly List<GameObject> GameObjects = new List<GameObject>();
        public readonly List<ColliderData> Colliders = new List<ColliderData>();
        public readonly List<Rigidbody2D> Bodies = new List<Rigidbody2D>();
        public readonly Dictionary<string, JsonElement> Physics2DSettings = new Dictionary<string, JsonElement>();
        public readonly int[] LayerCollisionMasks = new int[32];
        public readonly Dictionary<Transform, int> SiblingIndex = new Dictionary<Transform, int>();
        /// <summary>The root transforms of the scene FloatingOrigin moves (UpdateAllScenes off: the active scene; the export's `activeScene`).</summary>
        public readonly List<Transform> MovedRoots = new List<Transform>();
        public float FixedDeltaTime;
        public Assembly Game { get; } = typeof(Movement).Assembly;
        readonly Assembly _shim = typeof(Object).Assembly;
        readonly List<(Object target, JsonElement fields)> _pendingFields = new List<(Object, JsonElement)>();
        readonly List<(Rigidbody2D body, JsonElement props)> _pendingBodies = new List<(Rigidbody2D, JsonElement)>();
        readonly List<(ColliderData data, JsonElement props)> _pendingColliders = new List<(ColliderData, JsonElement)>();

        public static World Load(string path)
        {
            var world = new World();
            using (FileStream stream = File.OpenRead(path))
            using (JsonDocument doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 }))
                world.Build(doc.RootElement);
            return world;
        }

        void Build(JsonElement root)
        {
            FixedDeltaTime = root.GetProperty("time").GetProperty("fixedDeltaTime").GetSingle();
            Time.fixedDeltaTime = FixedDeltaTime;
            foreach (JsonProperty p in root.GetProperty("physics2d").EnumerateObject()) Physics2DSettings[p.Name] = p.Value.Clone();
            int i = 0;
            foreach (JsonElement m in root.GetProperty("layerCollisionMasks").EnumerateArray()) LayerCollisionMasks[i++] = m.GetInt32();
            i = 0;
            foreach (JsonElement n in root.GetProperty("layerNames").EnumerateArray()) Engine.LayerNames[i++] = n.GetString();

            var parents = new List<(GameObject go, int parent, int sibling)>();
            var scenes = new Dictionary<GameObject, string>();
            foreach (JsonElement o in root.GetProperty("objects").EnumerateArray())
            {
                var go = new GameObject(o.GetProperty("name").GetString());
                go.SetInstanceID(o.GetProperty("id").GetInt32());
                go.m_Layer = o.GetProperty("layer").GetInt32();
                go.tag = o.GetProperty("tag").GetString();
                go.m_ActiveSelf = o.GetProperty("activeSelf").GetBoolean();
                go.m_ActiveInHierarchy = o.GetProperty("activeInHierarchy").GetBoolean();
                Transform t = go.transform;
                t.SetInstanceID(o.GetProperty("transformId").GetInt32());
                t.name = go.name;
                t.InitPose(Vec3(o.GetProperty("position")), Quat(o.GetProperty("rotation")), Vec3(o.GetProperty("lossyScale")),
                    Vec3(o.GetProperty("localPosition")), Quat(o.GetProperty("localRotation")), Vec3(o.GetProperty("localScale")));
                Objects[go.GetInstanceID()] = go;
                Objects[t.GetInstanceID()] = t;
                GameObjects.Add(go);
                int sibling = o.GetProperty("siblingIndex").GetInt32();
                SiblingIndex[t] = sibling;
                parents.Add((go, o.GetProperty("parent").GetInt32(), sibling));
                scenes[go] = o.TryGetProperty("scene", out JsonElement scene) ? scene.GetString() : "";
                foreach (JsonElement c in o.GetProperty("components").EnumerateArray()) CreateComponent(go, c);
            }
            foreach (var (go, parent, sibling) in parents.OrderBy(p => p.sibling))
                if (parent != 0 && Objects.TryGetValue(parent, out Object p) && p is GameObject parentGo)
                    go.transform.SetParent(parentGo.transform);

            foreach (var (target, fields) in _pendingFields) FillFields(target, fields);
            foreach (var (body, props) in _pendingBodies) FillBody(body, props);
            foreach (var (data, props) in _pendingColliders) FillCollider(data, props);
            foreach (JsonElement s in root.GetProperty("statics").EnumerateArray()) SetStatic(s);
            FloatingOrigin floating = All<FloatingOrigin>().FirstOrDefault();
            string active = root.TryGetProperty("activeScene", out JsonElement activeScene) ? activeScene.GetString()
                : floating != null ? scenes[floating.gameObject] : null;
            foreach (GameObject go in GameObjects)
                if (go.transform.m_Parent == null && scenes[go] == active) MovedRoots.Add(go.transform);
            MovedRoots.Sort((a, b) => SiblingIndex[a].CompareTo(SiblingIndex[b]));
        }

        void CreateComponent(GameObject go, JsonElement c)
        {
            string typeName = c.GetProperty("type").GetString();
            bool isGame = c.GetProperty("assembly").GetString() == "Assembly-CSharp";
            Type type = isGame ? Game.GetType(typeName) : _shim.GetType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return;
            Component component;
            try { component = (Component)Activator.CreateInstance(type, true); }
            catch (Exception) { component = (Component)RuntimeHelpers.GetUninitializedObject(type); }
            component.SetInstanceID(c.GetProperty("id").GetInt32());
            component.name = go.name;
            go.AttachInternal(component);
            Objects[component.GetInstanceID()] = component;
            if (component is Behaviour behaviour && c.TryGetProperty("enabled", out JsonElement enabled)) behaviour.m_Enabled = enabled.GetBoolean();
            if (c.TryGetProperty("fields", out JsonElement fields)) _pendingFields.Add((component, fields.Clone()));
            if (component is Rigidbody2D body)
            {
                Bodies.Add(body);
                _pendingBodies.Add((body, c.GetProperty("props").Clone()));
            }
            if (component is Collider2D collider)
            {
                var data = new ColliderData { Collider = collider };
                collider.BackendData = data;
                if (c.TryGetProperty("shapes", out JsonElement shapes) && shapes.ValueKind == JsonValueKind.Object)
                {
                    data.Shapes = shapes.GetProperty("list").EnumerateArray().Select(ReadShape).ToArray();
                }
                if (c.TryGetProperty("sourceShapes", out JsonElement source) && source.ValueKind == JsonValueKind.Object)
                    data.SourceShapes = source.GetProperty("list").EnumerateArray().Select(ReadShape).ToArray();
                Colliders.Add(data);
                _pendingColliders.Add((data, c.GetProperty("props").Clone()));
            }
        }

        static ShapeData ReadShape(JsonElement s)
        {
            var verts = s.GetProperty("vertices").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var points = new Vector2[verts.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = new Vector2(verts[2 * i], verts[2 * i + 1]);
            return new ShapeData
            {
                Type = s.GetProperty("type").GetString(),
                Radius = s.GetProperty("radius").GetSingle(),
                Vertices = points,
                UseAdjacentStart = s.GetProperty("useAdjacentStart").GetBoolean(),
                UseAdjacentEnd = s.GetProperty("useAdjacentEnd").GetBoolean(),
                AdjacentStart = Vec2(s.GetProperty("adjacentStart")),
                AdjacentEnd = Vec2(s.GetProperty("adjacentEnd")),
            };
        }

        void FillCollider(ColliderData data, JsonElement props)
        {
            Collider2D c = data.Collider;
            c.InitCollider(Vec2(props.GetProperty("offset")), props.GetProperty("isTrigger").GetBoolean(),
                props.GetProperty("excludeLayers").GetInt32(), props.GetProperty("includeLayers").GetInt32(), props.GetProperty("enabled").GetBoolean());
            if (c is BoxCollider2D box)
            {
                box.InitSize(Vec2(props.GetProperty("size")));
                box.edgeRadius = props.GetProperty("edgeRadius").GetSingle();
            }
            if (c is CapsuleCollider2D capsule)
                capsule.InitShape(Vec2(props.GetProperty("size")), props.GetProperty("direction").GetInt32());
            c.attachedRigidbody = Ref(props, "attachedRigidbody") as Rigidbody2D;
            c.usedByEffector = props.GetProperty("usedByEffector").GetBoolean();
            data.Friction = props.GetProperty("friction").GetSingle();
            data.Bounciness = props.GetProperty("bounciness").GetSingle();
            c.friction = data.Friction;
            c.bounciness = data.Bounciness;
            if (props.TryGetProperty("compositeOperation", out JsonElement operation) && operation.ValueKind == JsonValueKind.Number && operation.GetInt32() != 0
                && Ref(props, "composite") is Collider2D composite)
                data.Composite = composite.BackendData as ColliderData;
        }

        void FillBody(Rigidbody2D body, JsonElement props)
        {
            body.bodyType = (RigidbodyType2D)props.GetProperty("bodyType").GetInt32();
            body.simulated = props.GetProperty("simulated").GetBoolean();
            body.mass = props.GetProperty("mass").GetSingle();
            body.gravityScale = props.GetProperty("gravityScale").GetSingle();
            body.constraints = (RigidbodyConstraints2D)props.GetProperty("constraints").GetInt32();
            body.freezeRotation = props.GetProperty("freezeRotation").GetBoolean();
            body.collisionDetectionMode = (CollisionDetectionMode2D)props.GetProperty("collisionDetectionMode").GetInt32();
            body.interpolation = (RigidbodyInterpolation2D)props.GetProperty("interpolation").GetInt32();
        }

        public Object Ref(JsonElement props, string name)
        {
            if (!props.TryGetProperty(name, out JsonElement r) || r.ValueKind != JsonValueKind.Object) return null;
            return Objects.TryGetValue(r.GetProperty("$ref").GetInt32(), out Object o) ? o : null;
        }

        void FillFields(Object target, JsonElement fields)
        {
            foreach (JsonElement entry in fields.EnumerateArray())
            {
                string declaring = entry[0].GetString(), name = entry[1].GetString();
                FieldInfo field = FindField(target.GetType(), declaring, name);
                if (field == null) continue;
                try
                {
                    object value = Read(entry[2], field.FieldType);
                    if (value == null && field.FieldType.Assembly == _shim && !typeof(Object).IsAssignableFrom(field.FieldType)
                        && field.FieldType.IsClass && field.FieldType.GetConstructor(Type.EmptyTypes) != null)
                        value = Activator.CreateInstance(field.FieldType);
                    field.SetValue(target, value);
                }
                catch (Exception) { }
            }
        }

        public static FieldInfo FindField(Type type, string declaring, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.FullName == declaring)
                    return t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            return null;
        }

        void SetStatic(JsonElement entry)
        {
            string typeName = entry[0].GetString(), name = entry[1].GetString();
            Type type;
            if (typeName.StartsWith("Singleton`1[", StringComparison.Ordinal))
            {
                Type arg = Game.GetType(typeName.Substring(12, typeName.Length - 13));
                Type open = Game.GetType("Singleton`1");
                if (arg == null || open == null) return;
                type = open.MakeGenericType(arg);
            }
            else type = Game.GetType(typeName);
            if (type == null) return;
            try
            {
                FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null || field.IsLiteral || field.IsInitOnly && field.FieldType.IsValueType) return;
                field.SetValue(null, Read(entry[2], field.FieldType));
            }
            catch (Exception) { }
        }

        public object Read(JsonElement e, Type type)
        {
            if (e.ValueKind == JsonValueKind.Null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(float)) return ReadFloat(e);
            if (type == typeof(double))
                return e.ValueKind == JsonValueKind.String ? double.Parse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture) : e.GetDouble();
            if (type == typeof(int)) return e.GetInt32();
            if (type == typeof(long)) return e.GetInt64();
            if (type == typeof(bool)) return e.GetBoolean();
            if (type == typeof(string)) return e.GetString();
            if (type.IsEnum) return Enum.ToObject(type, e.GetInt64());
            if (type == typeof(LayerMask)) return (LayerMask)e.GetInt32();
            if (type.IsPrimitive) return Convert.ChangeType(e.GetDouble(), type, System.Globalization.CultureInfo.InvariantCulture);
            if (typeof(Object).IsAssignableFrom(type))
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("$ref", out JsonElement id)) return null;
                return Objects.TryGetValue(id.GetInt32(), out Object o) && type.IsInstanceOfType(o) ? o : null;
            }
            if (type.IsArray)
            {
                Type element = type.GetElementType();
                var items = e.EnumerateArray().Select(x => Read(x, element)).ToArray();
                Array array = Array.CreateInstance(element, items.Length);
                for (int i = 0; i < items.Length; i++) array.SetValue(items[i], i);
                return array;
            }
            if (type.IsGenericType)
            {
                Type def = type.GetGenericTypeDefinition();
                Type[] args = type.GetGenericArguments();
                if (def == typeof(Dictionary<,>))
                {
                    var dict = (IDictionary)Activator.CreateInstance(type);
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("$dict", out JsonElement pairs))
                        foreach (JsonElement pair in pairs.EnumerateArray())
                        {
                            object key = Read(pair[0], args[0]);
                            if (key != null) dict[key] = Read(pair[1], args[1]);
                        }
                    return dict;
                }
                if (def == typeof(List<>) || def == typeof(HashSet<>) || def == typeof(Queue<>) || def == typeof(Stack<>))
                {
                    Array items = (Array)Read(e, args[0].MakeArrayType());
                    if (def == typeof(Stack<>)) Array.Reverse(items);
                    return Activator.CreateInstance(type, items);
                }
                if (def == typeof(Nullable<>)) return Read(e, args[0]);
            }
            if (e.ValueKind != JsonValueKind.Object) return type.IsValueType ? Activator.CreateInstance(type) : null;
            object instance;
            if (type.IsValueType) instance = Activator.CreateInstance(type);
            else
            {
                try { instance = Activator.CreateInstance(type, true); }
                catch (Exception) { instance = RuntimeHelpers.GetUninitializedObject(type); }
            }
            foreach (JsonProperty p in e.EnumerateObject())
            {
                string name = p.Name;
                Type owner = type;
                int sep = name.IndexOf("::", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    string ownerName = name.Substring(0, sep);
                    name = name.Substring(sep + 2);
                    while (owner != null && owner.Name != ownerName) owner = owner.BaseType;
                    if (owner == null) continue;
                }
                FieldInfo f = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f == null || f.IsInitOnly && type.IsValueType) continue;
                try { f.SetValue(instance, Read(p.Value, f.FieldType)); }
                catch (Exception) { }
            }
            return instance;
        }

        public static float ReadFloat(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String)
                return float.Parse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture);
            return e.GetSingle();
        }

        public static Vector2 Vec2(JsonElement e) { return new Vector2(ReadFloat(e.GetProperty("x")), ReadFloat(e.GetProperty("y"))); }
        public static Vector3 Vec3(JsonElement e) { return new Vector3(ReadFloat(e.GetProperty("x")), ReadFloat(e.GetProperty("y")), ReadFloat(e.GetProperty("z"))); }
        public static Quaternion Quat(JsonElement e)
        {
            return new Quaternion(ReadFloat(e.GetProperty("x")), ReadFloat(e.GetProperty("y")), ReadFloat(e.GetProperty("z")), ReadFloat(e.GetProperty("w")));
        }

        public T[] All<T>() where T : class
        {
            var result = new List<T>();
            foreach (GameObject go in GameObjects)
                foreach (Component c in go.m_Components)
                    if (c is T match) result.Add(match);
            return result.ToArray();
        }
    }
}
