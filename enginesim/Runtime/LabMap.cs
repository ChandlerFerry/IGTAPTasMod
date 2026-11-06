using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class LabShape
    {
        public string kind = "box";
        public float[] at;
        public float[] size;
        public float[][] points;
        public string layer = "Ground";
        public string tag = "Ground";
        public bool trigger;
        public bool spike;
        public float rotation;
        public LabSpring spring;
        public string colour;

        public bool? Blue => colour == null ? null : colour == "blue";

        public Quaternion Rotation()
        {
            if (rotation == 0f) return Quaternion.identity;
            double half = rotation * Math.PI / 360.0;
            return new Quaternion(0f, 0f, (float)Math.Sin(half), (float)Math.Cos(half));
        }

        public (float x, float y, float w, float h) Box()
        {
            if (kind != "box" || at == null || size == null) throw new InvalidDataException("A box shape needs at [x, y] and size [w, h].");
            return (at[0], at[1], size[0], size[1]);
        }

        public (float x0, float y0, float x1, float y1) Bounds()
        {
            if (kind == "box")
            {
                var (x, y, w, h) = Box();
                if (rotation == 0f) return (x - w / 2f, y - h / 2f, x + w / 2f, y + h / 2f);
                Vector2[] corners = Corners(w, h);
                return (x + corners.Min(c => c.x), y + corners.Min(c => c.y), x + corners.Max(c => c.x), y + corners.Max(c => c.y));
            }
            if (points == null || points.Length == 0) throw new InvalidDataException("A " + kind + " shape needs points.");
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            foreach (float[] p in points)
            {
                x0 = MathF.Min(x0, p[0]); x1 = MathF.Max(x1, p[0]);
                y0 = MathF.Min(y0, p[1]); y1 = MathF.Max(y1, p[1]);
            }
            return (x0, y0, x1, y1);
        }

        public Vector2[] Corners(float w, float h)
        {
            Quaternion q = Rotation();
            Vector2 Turn(float x, float y) { Vector3 r = q * new Vector3(x, y, 0f); return new Vector2(r.x, r.y); }
            return new[] { Turn(w / 2f, -h / 2f), Turn(w / 2f, h / 2f), Turn(-w / 2f, h / 2f), Turn(-w / 2f, -h / 2f) };
        }
    }

    public sealed class LabSpring
    {
        public float upForce = 70f;
        public float strength;
        public float movementLock = 0.25f;
    }

    public sealed class LabMapDef
    {
        public int schema = 1;
        public string id;
        public string name;
        public string tech;
        public string techNote;
        public string category = "NONE";
        public bool CategoryGiven;
        public float[] anchor;
        public float[] region;
        public List<LabShape> shapes = new List<LabShape>();
        public LabStart start = new LabStart();
        public float[] finish;
        public int maxTicks = 90;

        public (float x, float y) Anchor
        {
            get
            {
                if (anchor == null || anchor.Length < 2) throw new InvalidDataException("Lab map " + id + " has no anchor [x, y].");
                return (anchor[0], anchor[1]);
            }
        }

        public (float x, float y, float w, float h) Finish
        {
            get
            {
                if (finish == null || finish.Length < 4) throw new InvalidDataException("Lab map " + id + " has no finish [x, y, w, h].");
                return (finish[0], finish[1], finish[2], finish[3]);
            }
        }

        public static LabMapDef Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 64 });
            return Parse(doc.RootElement, path);
        }

        public void UseDemoStart(JsonElement demo)
        {
            if (demo.TryGetProperty("start", out JsonElement s) && s.ValueKind == JsonValueKind.Object) start = ParseStart(s);
        }

        public static LabMapDef Parse(JsonElement root, string path = null)
        {
            var lab = new LabMapDef();
            foreach (JsonProperty p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "schema": lab.schema = p.Value.GetInt32(); break;
                    case "id": lab.id = p.Value.GetString(); break;
                    case "name": lab.name = p.Value.GetString(); break;
                    case "tech": lab.tech = p.Value.GetString(); break;
                    case "techNote": lab.techNote = p.Value.GetString(); break;
                    case "category": lab.category = p.Value.GetString(); lab.CategoryGiven = true; break;
                    case "anchor": lab.anchor = Floats(p.Value); break;
                    case "region": lab.region = Floats(p.Value); break;
                    case "maxTicks": lab.maxTicks = p.Value.GetInt32(); break;
                    case "finish": lab.finish = Floats(p.Value); break;
                    case "start": lab.start = ParseStart(p.Value); break;
                    case "shapes":
                        foreach (JsonElement s in p.Value.EnumerateArray()) lab.shapes.Add(ParseShape(s));
                        break;
                }
            }
            if (string.IsNullOrEmpty(lab.id)) lab.id = path == null ? "lab" : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(lab.name)) lab.name = lab.id;
            if (lab.shapes.Count == 0) throw new InvalidDataException("Lab map " + lab.id + " has no shapes.");
            _ = lab.Anchor;
            _ = lab.Finish;
            return lab;
        }

        static LabShape ParseShape(JsonElement s)
        {
            var shape = new LabShape();
            foreach (JsonProperty p in s.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "kind": shape.kind = p.Value.GetString(); break;
                    case "at": shape.at = Floats(p.Value); break;
                    case "size": shape.size = Floats(p.Value); break;
                    case "layer": shape.layer = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32().ToString(CultureInfo.InvariantCulture) : p.Value.GetString(); break;
                    case "tag": shape.tag = p.Value.GetString(); break;
                    case "trigger": shape.trigger = p.Value.GetBoolean(); break;
                    case "spike": shape.spike = p.Value.GetBoolean(); break;
                    case "rotation": shape.rotation = p.Value.GetSingle(); break;
                    case "colour": shape.colour = p.Value.GetString(); break;
                    case "spring":
                        shape.spring = new LabSpring();
                        foreach (JsonProperty f in p.Value.EnumerateObject())
                        {
                            switch (f.Name)
                            {
                                case "upForce": shape.spring.upForce = f.Value.GetSingle(); break;
                                case "strength": shape.spring.strength = f.Value.GetSingle(); break;
                                case "movementLock": shape.spring.movementLock = f.Value.GetSingle(); break;
                                default: throw new InvalidDataException("A lab spring has upForce, strength and movementLock, not " + f.Name + ".");
                            }
                        }
                        break;
                    case "points":
                        shape.points = p.Value.EnumerateArray().Select(Floats).ToArray();
                        break;
                }
            }
            if (shape.kind != "box" && shape.kind != "polygon" && shape.kind != "edge")
                throw new InvalidDataException("A lab shape's kind is box, polygon or edge, not " + shape.kind + ".");
            if (shape.rotation != 0f && shape.kind != "box") throw new InvalidDataException("Only a lab box has a rotation.");
            if (shape.spring != null && !shape.trigger) throw new InvalidDataException("A lab spring is a trigger (SpringScript acts on OnTriggerStay2D).");
            if (shape.colour != null && shape.colour != "blue" && shape.colour != "orange") throw new InvalidDataException("A lab shape's colour is blue or orange, not " + shape.colour + ".");
            shape.Bounds();
            return shape;
        }

        static LabStart ParseStart(JsonElement s)
        {
            var start = new LabStart();
            foreach (JsonProperty p in s.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "x": start.x = p.Value.GetSingle(); break;
                    case "y": start.y = p.Value.GetSingle(); break;
                    case "vx": start.vx = p.Value.GetSingle(); break;
                    case "vy": start.vy = p.Value.GetSingle(); break;
                    case "mx": start.mx = p.Value.GetSingle(); break;
                    case "my": start.my = p.Value.GetSingle(); break;
                    case "facing": start.facing = p.Value.GetSingle(); break;
                    case "blueActive": start.blueActive = p.Value.GetBoolean(); break;
                    case "fields":
                        start.fields = new Dictionary<string, object>();
                        foreach (JsonProperty f in p.Value.EnumerateObject())
                            start.fields[f.Name] = f.Value.ValueKind == JsonValueKind.String ? f.Value.GetString() : f.Value.GetRawText();
                        break;
                }
            }
            return start;
        }

        internal static float[] Floats(JsonElement e)
        {
            return e.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
    }

    public sealed class LabStart
    {
        public float? x, y;
        public float? vx, vy;
        public float? mx, my;
        public float? facing;
        public bool? blueActive;
        public Dictionary<string, object> fields;
    }

    public static class LabMap
    {
        // Call before the Simulation is created so its physics and FloatingOrigin roots pick the lab up.
        public static List<GameObject> Inject(World world, LabMapDef lab)
        {
            bool blueActive = world.All<colouredBlockSwapper>().FirstOrDefault()?.isBlueActive ?? true;
            Vector3 origin = world.All<FloatingOrigin>().FirstOrDefault()?.currentOrigin ?? Vector3.zero;
            (float ax, float ay) = lab.Anchor;
            var root = new GameObject("LabMap " + lab.id);
            root.tag = "Untagged";
            root.m_Layer = 0;
            root.m_ActiveSelf = root.m_ActiveInHierarchy = true;
            Vector3 at = new Vector3(ax + origin.x, ay + origin.y, 0f);
            root.transform.InitPose(at, Quaternion.identity, Vector3.one, at, Quaternion.identity, Vector3.one);
            world.Objects[root.GetInstanceID()] = root;
            world.Objects[root.transform.GetInstanceID()] = root.transform;
            world.GameObjects.Add(root);
            world.MovedRoots.Add(root.transform);
            var made = new List<GameObject> { root };
            for (int i = 0; i < lab.shapes.Count; i++) made.Add(Shape(world, root, lab.shapes[i], lab.id + " " + (i + 1) + " " + lab.shapes[i].kind, origin, blueActive));
            return made;
        }

        static GameObject Shape(World world, GameObject root, LabShape shape, string name, Vector3 origin, bool blueActive)
        {
            var go = new GameObject(name);
            go.tag = shape.tag;
            go.m_Layer = Layer(world, shape.layer);
            go.m_ActiveSelf = go.m_ActiveInHierarchy = true;
            Vector3 at = new Vector3(shape.at != null ? shape.at[0] : 0f, shape.at != null ? shape.at[1] : 0f, 0f);
            Vector3 world3 = root.transform.m_Position + at;
            Quaternion rotation = shape.Rotation();
            go.transform.InitPose(world3, rotation, Vector3.one, world3, rotation, Vector3.one);
            go.transform.SetParent(root.transform);
            world.Objects[go.GetInstanceID()] = go;
            world.Objects[go.transform.GetInstanceID()] = go.transform;
            world.GameObjects.Add(go);

            var data = new ColliderData { Friction = 0.4f, Bounciness = 0f };
            bool enabled = shape.Blue is not bool blue || blue == blueActive;
            Collider2D made;
            switch (shape.kind)
            {
                case "box":
                {
                    var (x, y, w, h) = shape.Box();
                    var box = new BoxCollider2D();
                    Attach(world, go, box, data);
                    box.InitCollider(Vector2.zero, shape.trigger, 0, 0, enabled);
                    made = box;
                    box.InitSize(new Vector2(w, h));
                    box.edgeRadius = 0f;
                    data.Shapes = new[] { Polygon(shape.rotation == 0f
                        ? Vertices(world3.x - w / 2f, world3.y - h / 2f, world3.x + w / 2f, world3.y + h / 2f)
                        : shape.Corners(w, h).Select(c => new Vector2(world3.x + c.x, world3.y + c.y)).ToArray()) };
                    break;
                }
                case "polygon":
                {
                    var poly = new PolygonCollider2D();
                    Attach(world, go, poly, data);
                    poly.InitCollider(Vector2.zero, shape.trigger, 0, 0, enabled);
                    made = poly;
                    poly.pathCount = 1;
                    Vector2[] points = shape.points.Select(p => new Vector2(world3.x + p[0], world3.y + p[1])).ToArray();
                    if (points.Length < 3) throw new InvalidDataException("A lab polygon needs at least 3 points.");
                    data.Shapes = new[] { Polygon(points) };
                    break;
                }
                default:
                {
                    var edge = new EdgeCollider2D();
                    Attach(world, go, edge, data);
                    edge.InitCollider(Vector2.zero, shape.trigger, 0, 0, enabled);
                    made = edge;
                    if (shape.points.Length < 2) throw new InvalidDataException("A lab edge needs at least 2 points.");
                    data.Shapes = new[] { new ShapeData
                    {
                        Type = "Edges", Vertices = shape.points.Select(p => new Vector2(world3.x + p[0], world3.y + p[1])).ToArray(),
                    } };
                    break;
                }
            }
            if (shape.Blue is bool colour) world.LabColours.Add((made, colour));
            if (shape.spike) AddScript(world, go, "spikeScript");
            if (shape.spring != null)
            {
                MonoBehaviour spring = AddScript(world, go, "SpringScript");
                const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                spring.GetType().GetField("upForce", Fields).SetValue(spring, shape.spring.upForce);
                spring.GetType().GetField("strength", Fields).SetValue(spring, shape.spring.strength);
                spring.GetType().GetField("movementLock", Fields).SetValue(spring, shape.spring.movementLock);
            }
            return go;
        }

        static MonoBehaviour AddScript(World world, GameObject go, string type)
        {
            Type found = world.Game.GetType(type) ?? throw new InvalidOperationException("This world has no " + type + ".");
            var script = (MonoBehaviour)Activator.CreateInstance(found, true);
            script.name = go.name;
            go.AttachInternal(script);
            world.Objects[script.GetInstanceID()] = script;
            return script;
        }

        static void Attach(World world, GameObject go, Collider2D collider, ColliderData data)
        {
            collider.name = go.name;
            go.AttachInternal(collider);
            data.Collider = collider;
            collider.BackendData = data;
            world.Objects[collider.GetInstanceID()] = collider;
            world.Colliders.Add(data);
        }

        static ShapeData Polygon(Vector2[] points)
        {
            return new ShapeData { Type = "Polygon", Vertices = points };
        }

        static Vector2[] Vertices(float x0, float y0, float x1, float y1)
        {
            // Export order of box polygons: (max,min), (max,max), (min,max), (min,min); must match for tick-exact physics.
            return new[] { new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1), new Vector2(x0, y0) };
        }

        static int Layer(World world, string layer)
        {
            if (int.TryParse(layer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) return number;
            for (int i = 0; i < Engine.LayerNames.Length; i++)
                if (string.Equals(Engine.LayerNames[i], layer, StringComparison.OrdinalIgnoreCase)) return i;
            throw new InvalidDataException("No Unity layer named " + layer + " in this world export.");
        }
    }
}
