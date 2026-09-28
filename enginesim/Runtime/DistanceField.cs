using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// The guide: how far the run's target is through the static collision geometry, as a Dijkstra field over a coarse grid of
    /// capture-frame cells (cell size a parameter, <see cref="DefaultCell"/>). Free space is whatever no solid collider covers,
    /// and a spike cell costs six free steps (their grace jumps cross them, but rarely). So the number is a heuristic: a mover,
    /// a one-way or a colour swap is in it only as the world export left it. It weights the search's explore cell picks and
    /// reports how close the best cell got; a wrong number never cuts a run.
    /// Built lazily per world and target, over the region the target and the cells asked about cover (plus margin, so a run's
    /// wanderings grow it only every few hundred units of new ground), and cached per geometry version: a block swap
    /// (swapBlocks toggles the coloured tilemap colliders) or a zone load bumps it, and the next query rebuilds.
    /// </summary>
    public sealed class DistanceField
    {
        public const float DefaultCell = 24f;

        /// <summary>Guidance on (`solve --no-guide` turns it off): distances come from the field, not from straight lines.</summary>
        public static bool Enabled = true;

        /// <summary>The cell size fields build with.</summary>
        public static float CellSize = DefaultCell;

        const int Free = 0, Spike = 1, Solid = 2;
        const int SpikeLayer = 6;
        /// <summary>A step into a spike cell costs this many free steps.</summary>
        const float SpikeStep = 6f;
        /// <summary>Margin around the region the field covers, so wanderings grow it only every half of it in new ground.</summary>
        const float Margin = 512f;
        /// <summary>The most cells one field holds; a bigger region coarsens the cell instead.</summary>
        const int MaxCells = 1 << 18;
        const int CachedFields = 4;

        static readonly ConditionalWeakTable<Simulation, DistanceField> Fields = new ConditionalWeakTable<Simulation, DistanceField>();

        /// <summary>This simulation's field: one per worker, as each worker loads its own world.</summary>
        public static DistanceField For(Simulation sim) { return Fields.GetValue(sim, s => new DistanceField(s)); }

        readonly Simulation _sim;
        readonly World _world;
        readonly colouredBlockSwapper _swapper;
        readonly HashSet<GameObject> _spikes = new HashSet<GameObject>();
        readonly Dictionary<ColliderData, List<ColliderData>> _sources = new Dictionary<ColliderData, List<ColliderData>>();
        readonly List<List<Rect>> _goalBoxes = new List<List<Rect>>();
        readonly List<Field> _built = new List<Field>();
        readonly object _gate = new object();
        Vector2[] _points = new Vector2[0];
        List<Rect> _finish, _gates;
        bool _bounds;
        float _x0, _y0, _x1, _y1;
        int _region;

        static readonly FieldInfo EndOfCourse = typeof(endGate).GetField("isEndOfCourse", BindingFlags.Instance | BindingFlags.NonPublic);

        DistanceField(Simulation sim)
        {
            _sim = sim;
            _world = sim.World;
            _swapper = _world.All<colouredBlockSwapper>().FirstOrDefault();
            foreach (spikeScript spike in _world.All<spikeScript>()) _spikes.Add(spike.gameObject);
            foreach (ColliderData data in _world.Colliders)
                if (data.Composite != null)
                {
                    if (!_sources.TryGetValue(data.Composite, out List<ColliderData> sources))
                        _sources[data.Composite] = sources = new List<ColliderData>();
                    sources.Add(data);
                }
        }

        /// <summary>
        /// How far <paramref name="at"/> (capture frame) is from the target the run scores on now: the current at: goal in
        /// Full-TAS goal mode, else the finish box, the finish start's gate or the course's end gate. NaN when the run has no
        /// place to walk to yet (a goal that isn't an at: one, or a world with no gate for it).
        /// </summary>
        public float Distance(Vector2 at)
        {
            if (!Target(out int key, out List<Rect> boxes)) return float.NaN;
            int version = Version();
            lock (_gate)
            {
                Grow(at, boxes);
                Field field = null;
                for (int i = 0; i < _built.Count; i++)
                    if (_built[i].Version == version && _built[i].Target == key && _built[i].Region == _region) { field = _built[i]; _built.RemoveAt(i); break; }
                if (field == null)
                {
                    field = Build(version, key, boxes);
                    if (_built.Count >= CachedFields) _built.RemoveAt(_built.Count - 1);
                }
                _built.Insert(0, field);
                return At(field, at);
            }
        }

        /// <summary>Where the run ends now: its boxes, and a key naming them (-1: the finish box, -2: the finish start's gate,
        /// -3: the course's end gate, else the at: goal's index), false while the current goal is not an at: one.</summary>
        bool Target(out int key, out List<Rect> boxes)
        {
            FullTasTracker goals = _sim.Goals;
            if (goals != null)
            {
                FullTasGoal goal = goals.Current;
                if (goal == null || goal.kind != FullTasGoal.At) { key = 0; boxes = null; return false; }
                key = goals.Next;
                while (_goalBoxes.Count <= key) _goalBoxes.Add(null);
                boxes = _goalBoxes[key];
                if (boxes == null)
                    _goalBoxes[key] = boxes = new List<Rect> { new Rect(goal.x - goal.w / 2f, goal.y - goal.h / 2f, goal.w, goal.h) };
                return true;
            }
            if (_sim.TargetFinish.w > 0f)
            {
                (float x, float y, float w, float h) = _sim.TargetFinish;
                key = -1;
                boxes = _finish ?? (_finish = new List<Rect> { new Rect(x - w / 2f, y - h / 2f, w, h) });
                return true;
            }
            bool start = _sim.TargetStart != 0;
            key = start ? -2 : -3;
            boxes = _gates ?? (_gates = GateBoxes(start ? _sim.TargetStart : _sim.TargetCourse, start));
            return boxes.Count > 0;
        }

        /// <summary>
        /// The target course's real end gates (an end gate's isEndOfCourse: entering one finishes the run) or, on a transition,
        /// the finish start's course start gates (entering one starts that course and ends the run), in the capture frame.
        /// </summary>
        List<Rect> GateBoxes(int course, bool start)
        {
            var boxes = new List<Rect>();
            Vector3 by = _sim.CurrentOrigin - _sim.Origin;
            if (start)
                foreach (startGate gate in _world.All<startGate>()) GateBox(boxes, gate, gate.GetComponentInParent<courseScript>(), course, by, true);
            else
                foreach (endGate gate in _world.All<endGate>())
                    GateBox(boxes, gate, gate.GetComponentInParent<courseScript>(), course, by, EndOfCourse != null && (bool)EndOfCourse.GetValue(gate));
            return boxes;
        }

        void GateBox(List<Rect> boxes, MonoBehaviour gate, courseScript course, int want, Vector3 by, bool counts)
        {
            if (!counts || course == null || want != 0 && course.courseNumber != want) return;
            BoxCollider2D box = gate.GetComponent<BoxCollider2D>();
            if (box == null) return;
            Vector3 scale = gate.transform.lossyScale, at = gate.transform.position - by;
            float w = Math.Abs(box.size.x * scale.x), h = Math.Abs(box.size.y * scale.y);
            boxes.Add(new Rect(at.x + box.offset.x * scale.x - w / 2f, at.y + box.offset.y * scale.y - h / 2f, w, h));
        }

        /// <summary>What the geometry is now: every collider's live state (a zone load toggles them) and the swapper's colour.</summary>
        int Version()
        {
            int h = _swapper != null && _swapper.isBlueActive ? 17 : 0;
            List<ColliderData> all = _world.Colliders;
            for (int i = 0; i < all.Count; i++)
            {
                Collider2D c = all[i].Collider;
                h = h * 31 + (c.m_Enabled && c.gameObject.m_ActiveInHierarchy ? i + 1 : 0);
            }
            return h;
        }

        /// <summary>The region the field covers: the target and everything asked about, grown only past the margin's half.</summary>
        void Grow(Vector2 at, List<Rect> boxes)
        {
            float x0 = at.x, y0 = at.y, x1 = at.x, y1 = at.y;
            foreach (Rect r in boxes)
            {
                x0 = Math.Min(x0, r.xMin); y0 = Math.Min(y0, r.yMin);
                x1 = Math.Max(x1, r.xMax); y1 = Math.Max(y1, r.yMax);
            }
            if (_bounds && x0 >= _x0 - Margin / 2f && y0 >= _y0 - Margin / 2f && x1 <= _x1 + Margin / 2f && y1 <= _y1 + Margin / 2f) return;
            if (!_bounds) { _bounds = true; _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; }
            else
            {
                _x0 = Math.Min(_x0, x0); _y0 = Math.Min(_y0, y0);
                _x1 = Math.Max(_x1, x1); _y1 = Math.Max(_y1, y1);
            }
            _region++;
        }

        Field Build(int version, int key, List<Rect> boxes)
        {
            float x0 = _x0 - Margin, y0 = _y0 - Margin, x1 = _x1 + Margin, y1 = _y1 + Margin;
            float cell = CellSize;
            int w = Math.Max(1, Mathf.CeilToInt((x1 - x0) / cell)), h = Math.Max(1, Mathf.CeilToInt((y1 - y0) / cell));
            if ((long)w * h > MaxCells)
            {
                cell *= (float)Math.Sqrt((double)w * h / MaxCells);
                w = Math.Max(1, Mathf.CeilToInt((x1 - x0) / cell));
                h = Math.Max(1, Mathf.CeilToInt((y1 - y0) / cell));
            }
            var field = new Field
            {
                Version = version, Target = key, Region = _region, X0 = x0, Y0 = y0, Cell = cell, W = w, H = h,
                Distance = new float[w * h], Cost = new byte[w * h],
            };
            Array.Fill(field.Distance, float.PositiveInfinity);
            Raster(field);
            Flood(field, boxes);
            return field;
        }

        sealed class Field
        {
            public int Version, Target, Region, W, H;
            public float X0, Y0, Cell;
            public float[] Distance;
            public byte[] Cost;
        }

        /// <summary>Cells a solid collider covers (its centre within a quarter cell of the shape: thin tilemap walls too), then
        /// the spike shapes as cost, in the capture frame: a body's shapes sit at its capture pose, a body-less export's shapes
        /// are capture-frame already.</summary>
        void Raster(Field field)
        {
            Vector3 by = _sim.CurrentOrigin - _sim.Origin;
            GameObject player = _sim.Player.gameObject;
            float dilate = field.Cell / 4f;
            List<ColliderData> all = _world.Colliders;
            for (int i = 0; i < all.Count; i++)
            {
                ColliderData data = all[i];
                Collider2D c = data.Collider;
                if (ReferenceEquals(c.gameObject, player) || data.Composite != null) continue;
                bool on = c.m_Enabled && c.gameObject.m_ActiveInHierarchy;
                ShapeData[] shapes = data.Shapes;
                if (data.SourceShapes != null)
                {
                    // A coloured block or spike's composite: swapBlocks toggles its source tilemaps and its geometry follows
                    // them, so both colours are exported with their tilemaps on as SourceShapes.
                    bool live = on;
                    if (_sources.TryGetValue(data, out List<ColliderData> sources))
                    {
                        live = false;
                        foreach (ColliderData s in sources)
                            if (s.Collider.m_Enabled && s.Collider.gameObject.m_ActiveInHierarchy) { live = true; break; }
                    }
                    shapes = live ? data.SourceShapes : Array.Empty<ShapeData>();
                }
                else if (!on) shapes = Array.Empty<ShapeData>();
                if (shapes.Length == 0) continue;
                Rigidbody2D body = c.attachedRigidbody;
                // A mover is where the export left it, which is nowhere it stays: left out instead of wrong.
                if (body != null && body.bodyType != RigidbodyType2D.Static) continue;
                Transform pose = body == null ? null : body.transform;
                bool spike = c.gameObject.layer == SpikeLayer || _spikes.Contains(c.gameObject);
                // A trigger stops no one: camera zones, buy machines and gate boxes are not walls. A spike's trigger kills.
                if (c.m_IsTrigger && !spike) continue;
                for (int s = 0; s < shapes.Length; s++) Raster(field, shapes[s], pose, by, dilate, spike);
            }
        }

        void Raster(Field field, ShapeData shape, Transform pose, Vector3 by, float dilate, bool spike)
        {
            Vector2[] vertices = shape.Vertices;
            if (_points.Length < vertices.Length) Array.Resize(ref _points, vertices.Length);
            Vector2[] points = _points;
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector2 v = vertices[i];
                if (pose != null) v = Pose(pose, by, v);
                points[i] = v;
                x0 = Math.Min(x0, v.x); y0 = Math.Min(y0, v.y);
                x1 = Math.Max(x1, v.x); y1 = Math.Max(y1, v.y);
            }
            float reach = shape.Radius + dilate, reach2 = reach * reach;
            int cx0 = Math.Max(0, Mathf.FloorToInt((x0 - reach - field.X0) / field.Cell));
            int cy0 = Math.Max(0, Mathf.FloorToInt((y0 - reach - field.Y0) / field.Cell));
            int cx1 = Math.Min(field.W - 1, Mathf.FloorToInt((x1 + reach - field.X0) / field.Cell));
            int cy1 = Math.Min(field.H - 1, Mathf.FloorToInt((y1 + reach - field.Y0) / field.Cell));
            byte mark = spike ? (byte)Spike : (byte)Solid;
            for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    var at = new Vector2(field.X0 + (cx + .5f) * field.Cell, field.Y0 + (cy + .5f) * field.Cell);
                    if (DistanceSq(shape, points, vertices.Length, at) > reach2) continue;
                    int i = cy * field.W + cx;
                    if (field.Cost[i] == Free || mark == Solid) field.Cost[i] = mark;
                }
        }

        /// <summary>The capture-frame point of a body-local shape vertex at the body's pose now.</summary>
        static Vector2 Pose(Transform t, Vector3 by, Vector2 v)
        {
            Quaternion q = t.m_Rotation;
            float x2 = q.x * 2f, y2 = q.y * 2f, z2 = q.z * 2f;
            float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2, xy = q.x * y2, wz = q.w * z2;
            float m00 = 1f - (yy + zz), m01 = xy - wz, m10 = xy + wz, m11 = 1f - (xx + zz);
            Vector3 p = t.m_Position;
            return new Vector2(p.x - by.x + m00 * v.x + m01 * v.y, p.y - by.y + m10 * v.x + m11 * v.y);
        }

        /// <summary>The square of the distance from a point to a shape, its radius left out.</summary>
        static float DistanceSq(ShapeData shape, Vector2[] points, int count, Vector2 at)
        {
            switch (shape.Type)
            {
                case "Circle": return DistanceSq(points[0], at);
                case "Capsule": return SegmentSq(points[0], points[1], at);
                default:
                {
                    if (shape.Type == "Polygon" && Inside(points, count, at)) return 0f;
                    float best = float.PositiveInfinity;
                    for (int i = 0; i + 1 < count; i++) best = Math.Min(best, SegmentSq(points[i], points[i + 1], at));
                    if (shape.Type == "Polygon" && count > 2) best = Math.Min(best, SegmentSq(points[count - 1], points[0], at));
                    return best;
                }
            }
        }

        static float DistanceSq(Vector2 a, Vector2 b) { float x = a.x - b.x, y = a.y - b.y; return x * x + y * y; }

        static float SegmentSq(Vector2 a, Vector2 b, Vector2 at)
        {
            float x = b.x - a.x, y = b.y - a.y, length = x * x + y * y;
            float t = length > 0f ? Math.Clamp(((at.x - a.x) * x + (at.y - a.y) * y) / length, 0f, 1f) : 0f;
            return DistanceSq(new Vector2(a.x + t * x, a.y + t * y), at);
        }

        /// <summary>Whether a point is inside a convex polygon (every edge on one side of it).</summary>
        static bool Inside(Vector2[] points, int count, Vector2 at)
        {
            bool positive = false, negative = false;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = points[i], b = points[(i + 1) % count];
                float cross = (b.x - a.x) * (at.y - a.y) - (b.y - a.y) * (at.x - a.x);
                if (cross > 0f) positive = true;
                else if (cross < 0f) negative = true;
                if (positive && negative) return false;
            }
            return true;
        }

        /// <summary>The walkable distance from the target boxes out over the free cells: 8-way steps of a cell's side or
        /// diagonal, a spike cell six of them.</summary>
        void Flood(Field field, List<Rect> boxes)
        {
            var queue = new PriorityQueue<int, float>();
            foreach (Rect box in boxes)
            {
                int cx0 = Math.Max(0, Mathf.FloorToInt((box.xMin - field.X0) / field.Cell));
                int cy0 = Math.Max(0, Mathf.FloorToInt((box.yMin - field.Y0) / field.Cell));
                int cx1 = Math.Min(field.W - 1, Mathf.FloorToInt((box.xMax - field.X0) / field.Cell));
                int cy1 = Math.Min(field.H - 1, Mathf.FloorToInt((box.yMax - field.Y0) / field.Cell));
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int i = cy * field.W + cx;
                        if (!(field.Distance[i] > 0f)) continue;
                        field.Distance[i] = 0f;
                        queue.Enqueue(i, 0f);
                    }
            }
            while (queue.TryDequeue(out int at, out float distance))
            {
                if (distance > field.Distance[at]) continue;
                int cx = at % field.W, cy = at / field.W;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (dx == 0 && dy == 0 || x < 0 || y < 0 || x >= field.W || y >= field.H) continue;
                        int i = y * field.W + x;
                        if (field.Cost[i] == Solid) continue;
                        float step = field.Cell * (dx == 0 || dy == 0 ? 1f : MathF.Sqrt(2f)) * (field.Cost[i] == Spike ? SpikeStep : 1f);
                        float next = distance + step;
                        if (next < field.Distance[i]) { field.Distance[i] = next; queue.Enqueue(i, next); }
                    }
            }
        }

        /// <summary>The field at a point: the nearest cell that is not inside geometry (a clip's cell can be), its distance plus
        /// the step to it; infinite where the field never reached.</summary>
        static float At(Field field, Vector2 at)
        {
            int cx = Mathf.FloorToInt((at.x - field.X0) / field.Cell), cy = Mathf.FloorToInt((at.y - field.Y0) / field.Cell);
            for (int ring = 0; ring <= 4; ring++)
            {
                float near = float.PositiveInfinity;
                int found = -1;
                for (int y = cy - ring; y <= cy + ring; y++)
                    for (int x = cx - ring; x <= cx + ring; x++)
                    {
                        if (ring > 0 && Math.Abs(x - cx) < ring && Math.Abs(y - cy) < ring) continue;
                        if (x < 0 || y < 0 || x >= field.W || y >= field.H) continue;
                        int i = y * field.W + x;
                        if (field.Cost[i] == Solid) continue;
                        Vector2 centre = new Vector2(field.X0 + (x + .5f) * field.Cell, field.Y0 + (y + .5f) * field.Cell);
                        float off = MathF.Sqrt(DistanceSq(centre, at));
                        if (off < near) { near = off; found = i; }
                    }
                if (found >= 0) return field.Distance[found] + near;
            }
            return float.PositiveInfinity;
        }
    }
}
