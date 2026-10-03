using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Box2D;
using static Box2D.b2Collision;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class PhysicsWorld : IPhysicsBackend
    {
        sealed class ColliderState
        {
            public ColliderData Data;
            public Collider2D Collider;
            public BodyState Body;
            public readonly List<b2Fixture> Fixtures = new List<b2Fixture>(1);
            public int Layer, ContactMask, Id;
            public Vector3 BuiltScale;
            /// <summary>What the world loaded it as (with <see cref="Pristine"/>): a restore to a snapshot that doesn't list it.</summary>
            public Vector3 PristineScale;
            public int PristineLayer, PristineMask;
            /// <summary>Its transform as loaded: a body-less collider a script moved off it is built from its geometry (MovedShapes).</summary>
            public Vector3 LoadedPosition, LoadedScale;
            public Quaternion LoadedRotation;
            /// <summary>A body-less collider whose transform a script wrote since the last sync (in _movedByScript): rebuilt there, as Unity
            /// moves a static collider at its next transform sync.</summary>
            public bool Moved;
            public (MonoBehaviour script, Action<object> call)[][] Handlers;
            public bool Listens;
            public ColliderState Composite;
            public ColliderState[] Sources;
            public bool[] SourcesLiveAtLoad;
            public b2Fixture[] Pristine;
            /// <summary>What the snapshot being restored lists for it (Restore's scratch).</summary>
            public Built Restoring;
            /// <summary>An array holding exactly what Fixtures holds now (null when none is known): Restore skips comparing it.</summary>
            public b2Fixture[] FixturesAs;
            /// <summary>Changed with its fixtures not built yet (PhysicsWorld.RebuildLater; built by Settle).</summary>
            public bool Unbuilt;
            /// <summary>Rebuilt since load, so it joins _changed and snapshots record its fixtures.</summary>
            public bool Tracked;
            public bool Rescaled;
            /// <summary>Rebuilt since the last step (PhysicsWorld.FindLapsedTouches).</summary>
            public bool Reshaped;
            public bool Regrid;
            /// <summary>Whether the player touching it is a spike touch (<see cref="PlayerTouch"/>): -1 not looked up yet, 0 no,
            /// 1 a spikeScript's trigger.</summary>
            public sbyte TouchKind = -1;
        }

        sealed class BodyState
        {
            public Rigidbody2D Rigidbody;
            public b2Body Body;
            public bool TransformDirty, ScaleDirty;
            /// <summary>Teleported (SetTransform) since the last step (PhysicsWorld.FindLapsedTouches).</summary>
            public bool Shifted;
            /// <summary>In _movedStatic.</summary>
            public bool MovedStatic;
            /// <summary>Its place in _bodyList (a snapshot names bodies by it).</summary>
            public int Index;
            public readonly List<ColliderState> Colliders = new List<ColliderState>();
        }

        readonly struct Pair : IEquatable<Pair>
        {
            public readonly ColliderState A, B;
            public readonly bool Trigger;
            /// <summary>A trigger pair's shapes overlapped after the step that recorded it (not only at its start).</summary>
            public readonly bool Overlapped;
            public Pair(ColliderState a, ColliderState b, bool trigger, bool overlapped = false)
            {
                if (a.Id <= b.Id) { A = a; B = b; } else { A = b; B = a; }
                Trigger = trigger;
                Overlapped = overlapped;
            }
            public Pair Overlapping() => new Pair(A, B, Trigger, true);
            public bool Equals(Pair o) { return ReferenceEquals(A, o.A) && ReferenceEquals(B, o.B); }
            public override bool Equals(object o) { return o is Pair p && Equals(p); }
            public override int GetHashCode() { return A.Id * 100003 + B.Id; }
        }

        readonly World _world;
        readonly Simulation _sim;
        readonly b2World _b2;
        readonly b2Body _ground;
        readonly Dictionary<Collider2D, ColliderState> _colliders = new Dictionary<Collider2D, ColliderState>();
        readonly Dictionary<int, ColliderState> _byId = new Dictionary<int, ColliderState>();
        readonly Dictionary<Rigidbody2D, BodyState> _bodies = new Dictionary<Rigidbody2D, BodyState>();
        readonly Dictionary<GameObject, List<ColliderState>> _byObject = new Dictionary<GameObject, List<ColliderState>>();
        // _bodies/_colliders are filled only in the constructor and nothing is ever removed, so these copies of their
        // Values (taken at the constructor's end) hold exactly the order a foreach over .Values would walk forever.
        BodyState[] _bodyList = Array.Empty<BodyState>();
        ColliderState[] _colliderList = Array.Empty<ColliderState>();
        readonly int[] _layerMasks;
        readonly float _contactOffset;
        readonly int _velocityIterations, _positionIterations;
        readonly bool _queriesHitTriggers, _queriesStartInColliders;
        HashSet<Pair> _touching = new HashSet<Pair>(), _touchingNext = new HashSet<Pair>();
        // Trigger pairs touched only at this step's start (Box2D's Collide), not after its move: they enter and exit in the
        // step, and aren't touching after it (CollectTouching).
        readonly HashSet<Pair> _startOnly = new HashSet<Pair>();
        readonly BodyState _player;
        readonly List<ColliderState> _changed = new List<ColliderState>();
        readonly List<BodyState> _movedStatic = new List<BodyState>();
        bool _loaded;
        bool _suspended;

        /// <summary>
        /// Collider changes are ignored while set (a restore puts the toggles back). Setting it builds the fixtures still
        /// waiting (Settle) first, from the colliders as they are before the restore writes them, as an at-once rebuild did.
        /// </summary>
        internal bool Suspended
        {
            get { return _suspended; }
            set { if (value) Settle(); _suspended = value; }
        }

        // A FloatingOrigin rebase moved every transform; bodies and body-less colliders follow at the next sync, as Unity's do.
        bool _worldMoved;

        public PhysicsWorld(World world, Simulation sim)
        {
            _world = world;
            _sim = sim;
            _layerMasks = world.LayerCollisionMasks;
            _inside = new InsideQuery(this);
            _ray = new ClosestRay(this);
            _contactOffset = Setting("defaultContactOffset", 0.01f);
            _velocityIterations = (int)Setting("velocityIterations", 8);
            _positionIterations = (int)Setting("positionIterations", 3);
            _queriesHitTriggers = SettingBool("queriesHitTriggers", true);
            _queriesStartInColliders = SettingBool("queriesStartInColliders", true);
            b2Settings.maxLinearCorrection = Setting("maxLinearCorrection", 0.2f);
            b2Settings.maxAngularCorrection = Setting("maxAngularCorrection", 8f) * (MathF.PI / 180f);
            b2Settings.maxTranslation = Setting("maxTranslationSpeed", 100f);
            b2Settings.maxRotation = Setting("maxRotationSpeed", 360f) * (MathF.PI / 180f);
            b2Settings.baumgarte = Setting("baumgarteScale", 0.2f);
            b2Settings.toiBaumgarte = Setting("baumgarteTOIScale", 0.75f);
            b2Settings.timeToSleep = Setting("timeToSleep", 0.5f);
            b2Settings.linearSleepTolerance = Setting("linearSleepTolerance", 0.01f);
            b2Settings.angularSleepTolerance = Setting("angularSleepTolerance", 2f) * (MathF.PI / 180f);
            b2Settings.velocityThreshold = Setting("bounceThreshold", 1f);
            b2Settings.polygonRadius = _contactOffset;

            Vector2 gravity = world.Physics2DSettings.TryGetValue("gravity", out JsonElement g) ? World.Vec2(g) : Vector2.zero;
            _b2 = new b2World(new b2Vec2(gravity.x, gravity.y));
            _b2.SetContactFilter(new Filter());
            _b2.SetContinuousPhysics(true);
            _b2.SetSubStepping(SettingBool("useSubStepping", false));
            _ground = _b2.CreateBody(new b2BodyDef());

            // A static body off at the export is built too (a tree segment's ground, TreeSegmentActivationComponent.IE_Grow): it
            // never moves and has fixtures only while its colliders are on (Live). A dynamic or kinematic one off at the export
            // isn't (this Box2D can't disable a body): one turned on ends the run at the next step (Unbodied).
            foreach (Rigidbody2D rb in world.Bodies)
            {
                if (!rb.simulated || !rb.gameObject.activeInHierarchy && rb.bodyType != RigidbodyType2D.Static) continue;
                var state = new BodyState { Rigidbody = rb };
                rb.BackendData = state;
                _bodies[rb] = state;
                Transform t = rb.transform;
                var def = new b2BodyDef
                {
                    type = rb.bodyType == RigidbodyType2D.Dynamic ? b2BodyType.b2_dynamicBody
                        : rb.bodyType == RigidbodyType2D.Kinematic ? b2BodyType.b2_kinematicBody : b2BodyType.b2_staticBody,
                    position = new b2Vec2(t.m_Position.x, t.m_Position.y),
                    angle = t.m_Rotation.eulerAngles.z * (MathF.PI / 180f),
                    fixedRotation = (rb.constraints & RigidbodyConstraints2D.FreezeRotation) != 0 || rb.freezeRotation,
                    bullet = rb.collisionDetectionMode == CollisionDetectionMode2D.Continuous,
                    gravityScale = rb.gravityScale,
                    allowSleep = true,
                };
                state.Body = _b2.CreateBody(def);
                state.Body.UserData = state;
            }
            foreach (ColliderData data in world.Colliders)
            {
                Collider2D c = data.Collider;
                var state = new ColliderState { Data = data, Collider = c, Layer = c.gameObject.layer, Id = _colliders.Count,
                    LoadedPosition = c.transform.m_Position, LoadedScale = c.transform.m_LossyScale, LoadedRotation = c.transform.m_Rotation };
                Rigidbody2D rb = c.attachedRigidbody;
                state.Body = rb != null && _bodies.TryGetValue(rb, out BodyState b) ? b : null;
                state.Body?.Colliders.Add(state);
                _colliders[c] = state;
                _byId[state.Id] = state;
                if (!_byObject.TryGetValue(c.gameObject, out var list)) _byObject[c.gameObject] = list = new List<ColliderState>();
                list.Add(state);
                state.Handlers = Handlers(CallbackScripts(c));
                state.Listens = state.Handlers.Any(h => h.Length > 0);
                Rebuild(state);
                // What the world loaded it as: a snapshot that doesn't list it restores this, whatever its rebuilds made since.
                state.FixturesAs = state.Pristine = state.Fixtures.ToArray();
                state.PristineScale = state.BuiltScale;
                state.PristineLayer = state.Layer;
                state.PristineMask = state.ContactMask;
            }
            Movement player = sim.Player;
            _player = _bodies[player.GetComponent<Rigidbody2D>()];
            var sources = new Dictionary<ColliderState, List<ColliderState>>();
            foreach (ColliderState s in _colliders.Values)
                if (s.Data.Composite != null && _colliders.TryGetValue(s.Data.Composite.Collider, out ColliderState composite))
                {
                    s.Composite = composite;
                    if (!sources.TryGetValue(composite, out var list)) sources[composite] = list = new List<ColliderState>();
                    list.Add(s);
                }
            foreach (var (composite, list) in sources)
            {
                composite.Sources = list.ToArray();
                composite.SourcesLiveAtLoad = list.Select(Live).ToArray();
                if (composite.Data.SourceShapes == null && composite.Data.Shapes.Length == 0) composite.Data.SourceShapes = PathShapes(composite);
            }
            _bodyList = new BodyState[_bodies.Count];
            _bodies.Values.CopyTo(_bodyList, 0);
            for (int i = 0; i < _bodyList.Length; i++) _bodyList[i].Index = i;
            _colliderList = new ColliderState[_colliders.Count];
            _colliders.Values.CopyTo(_colliderList, 0);
            _loaded = true;
        }

        float Setting(string name, float fallback)
        {
            return _world.Physics2DSettings.TryGetValue(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : fallback;
        }

        bool SettingBool(string name, bool fallback)
        {
            return _world.Physics2DSettings.TryGetValue(name, out JsonElement e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) ? e.GetBoolean() : fallback;
        }

        static MonoBehaviour[] CallbackScripts(Collider2D c)
        {
            var scripts = c.gameObject.GetComponents<MonoBehaviour>().ToList();
            Rigidbody2D rb = c.attachedRigidbody;
            if (rb != null && rb.gameObject != c.gameObject) scripts.AddRange(rb.gameObject.GetComponents<MonoBehaviour>());
            return scripts.ToArray();
        }

        static int ContactMask(int[] matrix, Collider2D c)
        {
            return (matrix[c.gameObject.layer] | c.m_IncludeLayers.value) & ~c.m_ExcludeLayers.value;
        }

        sealed class Filter : b2ContactFilter
        {
            public override bool ShouldCollide(b2Fixture fixtureA, b2Fixture fixtureB)
            {
                var a = (ColliderState)fixtureA.UserData;
                var b = (ColliderState)fixtureB.UserData;
                return (a.ContactMask & (1 << b.Layer)) != 0 && (b.ContactMask & (1 << a.Layer)) != 0;
            }
        }

        bool Live(ColliderState s)
        {
            Collider2D c = s.Collider;
            return c.m_Enabled && c.gameObject.m_ActiveInHierarchy && (c.attachedRigidbody == null || s.Body != null);
        }

        /// <summary>Rebuilds a collider's fixtures now, after building the ones still waiting (their changes came first).</summary>
        void Rebuild(ColliderState s)
        {
            Settle();
            Reshape(s);
            BuildFixtures(s);
        }

        /// <summary>
        /// A rebuild's part that happens when the collider changes, whenever its fixtures are built: the snapshot and step
        /// bookkeeping, the grid and mover lists, the scale its box is built at, and the layer and contact mask.
        /// </summary>
        void Reshape(ColliderState s)
        {
            if (_loaded && s.Body != _player && !s.Tracked)
            {
                s.Tracked = true;
                _changed.Add(s);
            }
            if (s.Tracked) _changedCapture = null;
            if ((s.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(s);
            else _moversDirty = true;
            s.BuiltScale = s.Collider.transform.m_LossyScale;
            if (!s.Reshaped) { s.Reshaped = true; _reshaped.Add(s); }
            s.Layer = s.Collider.gameObject.m_Layer;
            s.ContactMask = ContactMask(_layerMasks, s.Collider);
        }

        void BuildFixtures(ColliderState s)
        {
            s.FixturesAs = null;
            b2Body body = s.Body?.Body ?? _ground;
            foreach (b2Fixture f in s.Fixtures) body.DestroyFixture(f);
            s.Fixtures.Clear();
            if (!Live(s))
            {
                if (_loaded && Unbodied(s)) _unbodied.Add(s);
                return;
            }
            float friction = s.Data.Friction, restitution = s.Data.Bounciness;
            if (s.Collider is BoxCollider2D box && s.Body != null && s.Body.Rigidbody.bodyType == RigidbodyType2D.Dynamic)
            {
                Vector3 scale = s.BuiltScale;
                float cx = box.m_Offset.x * scale.x, cy = box.m_Offset.y * scale.y;
                float hx = box.m_Size.x * MathF.Abs(scale.x) * 0.5f, hy = box.m_Size.y * MathF.Abs(scale.y) * 0.5f;
                var shape = new b2PolygonShape();
                _box[0] = new b2Vec2(cx + hx, cy - hy); _box[1] = new b2Vec2(cx + hx, cy + hy);
                _box[2] = new b2Vec2(cx - hx, cy + hy); _box[3] = new b2Vec2(cx - hx, cy - hy);
                shape.Set(_box, 4);
                shape.m_radius = _contactOffset + box.edgeRadius;
                AddFixture(s, body, shape, friction, restitution);
                return;
            }
            foreach (ShapeData shape in Shapes(s))
            {
                switch (shape.Type)
                {
                    case "Polygon":
                    {
                        var poly = new b2PolygonShape();
                        var points = shape.Vertices.Select(v => new b2Vec2(v.x, v.y)).ToArray();
                        if (points.Length < 3 || !poly.Set(points, points.Length)) break;
                        poly.m_radius = _contactOffset + shape.Radius;
                        AddFixture(s, body, poly, friction, restitution);
                        break;
                    }
                    case "Edges":
                    {
                        var chain = new b2ChainShape();
                        var points = shape.Vertices.Select(v => new b2Vec2(v.x, v.y)).ToArray();
                        if (points.Length < 2) break;
                        chain.CreateChain(points, points.Length,
                            shape.UseAdjacentStart ? new b2Vec2(shape.AdjacentStart.x, shape.AdjacentStart.y) : points[0],
                            shape.UseAdjacentEnd ? new b2Vec2(shape.AdjacentEnd.x, shape.AdjacentEnd.y) : points[points.Length - 1]);
                        chain.m_radius = _contactOffset + shape.Radius;
                        AddFixture(s, body, chain, friction, restitution);
                        break;
                    }
                    case "Circle":
                    {
                        var circle = new b2CircleShape { m_p = new b2Vec2(shape.Vertices[0].x, shape.Vertices[0].y), m_radius = shape.Radius };
                        AddFixture(s, body, circle, friction, restitution);
                        break;
                    }
                    case "Capsule":
                    {
                        var edge = new b2EdgeShape();
                        edge.SetTwoSided(new b2Vec2(shape.Vertices[0].x, shape.Vertices[0].y), new b2Vec2(shape.Vertices[1].x, shape.Vertices[1].y));
                        edge.m_radius = shape.Radius;
                        AddFixture(s, body, edge, friction, restitution);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// A composite off at the export has no shapes there, only its paths: its geometry with every source on, each path a
        /// closed chain in the body's frame (two-sided edges, this Box2D's chains). Unity builds a Polygons composite from convex
        /// polygons (its decomposition isn't known here; the outline's surface is the same, its inside and its seams aren't:
        /// docs/fidelity-log.md, *Upgrade trees grown by hand*), an Outlines one from edges.
        /// </summary>
        ShapeData[] PathShapes(ColliderState s)
        {
            Collider2D c = s.Collider;
            if (s.Data.Paths == null || s.Data.Paths.Length == 0) return null;
            // ponytail: the paths in the body's frame only for a composite on its body's object at offset 0 (every export's).
            if (c.attachedRigidbody == null || c.attachedRigidbody.gameObject != c.gameObject || c.m_Offset != Vector2.zero) return null;
            Vector3 scale = c.transform.m_LossyScale;
            float radius = c is CompositeCollider2D composite ? composite.edgeRadius : 0f;
            var shapes = new List<ShapeData>();
            foreach (Vector2[] path in s.Data.Paths)
            {
                if (path.Length < 2) continue;
                var loop = new Vector2[path.Length + 1];
                for (int i = 0; i < path.Length; i++) loop[i] = new Vector2(path[i].x * scale.x, path[i].y * scale.y);
                loop[path.Length] = loop[0];
                // b2ChainShape.CreateLoop: the last point again, the ghosts the points before and after the seam.
                shapes.Add(new ShapeData { Type = "Edges", Radius = radius, Vertices = loop, UseAdjacentStart = true,
                    AdjacentStart = loop[path.Length - 1], UseAdjacentEnd = true, AdjacentEnd = loop[1] });
            }
            return shapes.ToArray();
        }

        // Colliders that were on without their body at a rebuild (Unbodied): the next step ends the run if one still is.
        readonly HashSet<ColliderState> _unbodied = new HashSet<ColliderState>();

        /// <summary>
        /// On, its Rigidbody2D on too, but that body is dynamic or kinematic and was off at the world export, so the engine has
        /// no body for it (static ones are built). A composite has nothing without its sources. On only between two steps, it
        /// is never seen.
        /// </summary>
        static bool Unbodied(ColliderState s)
        {
            Collider2D c = s.Collider;
            Rigidbody2D rb = c.attachedRigidbody;
            return s.Body == null && rb != null && rb.simulated && c.m_Enabled && c.gameObject.m_ActiveInHierarchy
                && rb.gameObject.m_ActiveInHierarchy && c is not CompositeCollider2D;
        }

        // A dynamic box's corners (b2PolygonShape.Set copies them).
        readonly b2Vec2[] _box = new b2Vec2[4];

        ShapeData[] Shapes(ColliderState s)
        {
            if (s.Body == null && (_sim.Rebased || OffLoaded(s))) return MovedShapes(s);
            if (s.Sources == null) return s.Data.Shapes;
            int live = 0;
            bool atLoad = true;
            for (int i = 0; i < s.Sources.Length; i++)
            {
                bool l = Live(s.Sources[i]);
                if (l) live++;
                if (l != s.SourcesLiveAtLoad[i]) atLoad = false;
            }
            if (atLoad) return s.Data.Shapes;
            if (live == 0) return Array.Empty<ShapeData>();
            if (live == s.Sources.Length && s.Data.SourceShapes != null) return s.Data.SourceShapes;
            if (_sim.Engine.IsRunning)
                _sim.Engine.Abort("Unmodeled physics: " + s.Collider.name + "'s composite geometry with its sources toggled is not in this world export");
            return Array.Empty<ShapeData>();
        }

        /// <summary>A script moved the collider's transform off its loaded pose (a tree box falling into place, IE_Grow).</summary>
        static bool OffLoaded(ColliderState s)
        {
            Transform t = s.Collider.transform;
            return !SameBits(t.m_Position, s.LoadedPosition) || !SameBits(t.m_LossyScale, s.LoadedScale) || !SameBits(t.m_Rotation, s.LoadedRotation);
        }

        /// <summary>
        /// A body-less collider's shapes after a rebase or a script moved its transform: Unity builds them again from the
        /// collider's own geometry and the transform (world-space vertices, float math); the export's shapes are the capture
        /// pose's. Boxes and capsules from their geometry; other shapes (the polygon camera zones) move with the origin, and one
        /// a script moved (before any rebase) ends the run.
        /// </summary>
        ShapeData[] MovedShapes(ColliderState s)
        {
            ShapeData[] captured = s.Data.Shapes;
            if (captured.Length == 0) return captured;
            Transform t = s.Collider.transform;
            (float m00, float m01, float m10, float m11) = Rotation2D(t.m_Rotation);
            Vector3 p = t.m_Position, scale = t.m_LossyScale;
            Vector2 Point(float lx, float ly)
            {
                float sx = lx * scale.x, sy = ly * scale.y;
                float rx = m00 * sx, ry = m01 * sy, ux = m10 * sx, uy = m11 * sy;
                float x = rx + ry, y = ux + uy;
                return new Vector2(x + p.x, y + p.y);
            }
            Vector2 o = s.Collider.m_Offset;
            switch (s.Collider)
            {
                case BoxCollider2D box when captured.Length == 1 && captured[0].Type == "Polygon":
                {
                    float hx = box.m_Size.x * 0.5f, hy = box.m_Size.y * 0.5f;
                    float x0 = o.x - hx, x1 = o.x + hx, y0 = o.y - hy, y1 = o.y + hy;
                    return new[] { Moved(captured[0], Point(x1, y0), Point(x1, y1), Point(x0, y1), Point(x0, y0)) };
                }
                case CapsuleCollider2D capsule when captured.Length == 1 && captured[0].Type == "Capsule":
                {
                    Vector2 size = capsule.m_Size;
                    if (capsule.m_Direction == CapsuleDirection2D.Vertical)
                    {
                        float half = size.y * 0.5f - size.x * 0.5f;
                        return new[] { Moved(captured[0], Point(o.x, o.y - half), Point(o.x, o.y + half)) };
                    }
                    float along = size.x * 0.5f - size.y * 0.5f;
                    return new[] { Moved(captured[0], Point(o.x - along, o.y), Point(o.x + along, o.y)) };
                }
            }
            // ponytail: a rebase moves every transform off its loaded pose, so only a script's move before any rebase is caught.
            if (!_sim.Rebased && OffLoaded(s) && _sim.Engine.IsRunning)
                _sim.Engine.Abort("Unmodeled physics: " + s.Collider.name + "'s " + s.Collider.GetType().Name + " moved by a script (only boxes and capsules are rebuilt)");
            ScenePos by = _sim.FromCapture(new CapturePos(0f, 0f));
            return captured.Select(c => Moved(c, c.Vertices.Select(v => new Vector2(v.x + by.x, v.y + by.y)).ToArray())).ToArray();
        }

        /// <summary>The rotation's xy block (Unity's Quaternion-to-matrix, float math).</summary>
        internal static (float m00, float m01, float m10, float m11) Rotation2D(Quaternion q)
        {
            float x2 = q.x * 2f, y2 = q.y * 2f, z2 = q.z * 2f;
            float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2, xy = q.x * y2, wz = q.w * z2;
            return (1f - (yy + zz), xy - wz, xy + wz, 1f - (xx + zz));
        }

        static ShapeData Moved(ShapeData shape, params Vector2[] vertices)
        {
            return new ShapeData { Type = shape.Type, Radius = shape.Radius, Vertices = vertices, UseAdjacentStart = shape.UseAdjacentStart,
                UseAdjacentEnd = shape.UseAdjacentEnd, AdjacentStart = shape.AdjacentStart, AdjacentEnd = shape.AdjacentEnd };
        }

        /// <summary>A FloatingOrigin rebase moved the world's transforms (Simulation.MoveWorld).</summary>
        public void WorldMoved() { _worldMoved = true; }

        /// <summary>
        /// A query after FloatingOrigin moved the world sees it moved, as the game's do (a 1x probe of Test-Pillar-Ghost: the
        /// FixedUpdate after MoveOrigin read the spiked wall where it now is, not the geometry left at the old place). Bodies take
        /// their moved transforms only at the next physics step's sync, as Unity's do: a query sync that moved the player's body
        /// took the first rendered frame's interpolation blend and lost the rest of the tick's step (30 units on VMAN's rebase),
        /// and grounded the player a tick before turbo does.
        /// </summary>
        void SyncMovedWorld()
        {
            if (!_worldMoved || _staticsMoved) return;
            _staticsMoved = true;
            RebuildBodyless();
        }

        void RebuildBodyless()
        {
            for (int i = 0; i < _colliderList.Length; i++) { ColliderState s = _colliderList[i]; if (s.Body == null) Rebuild(s); }
        }

        bool _staticsMoved;

        void AddFixture(ColliderState s, b2Body body, b2Shape shape, float friction, float restitution)
        {
            var def = new b2FixtureDef
            {
                shape = shape,
                density = s.Body != null ? 1f : 0f,
                friction = friction,
                restitution = restitution,
                isSensor = s.Collider.m_IsTrigger,
                userData = s,
            };
            s.Fixtures.Add(body.CreateFixture(def));
        }

        /// <summary>Bumped by every collider toggle, layer change and restore, suspended or not (and by any other write of a
        /// collider's enabled or active state): unchanged, every collider is live as it was (<see cref="DistanceField"/>'s version).</summary>
        internal int Generation;

        public void ColliderChanged(Collider2D collider)
        {
            Generation++;
            if (!Suspended && _colliders.TryGetValue(collider, out ColliderState s)) Changed(s);
        }

        public void ActiveChanged(GameObject gameObject) { LayerChanged(gameObject); }

        /// <summary>
        /// A GameObject changed layer (PlatformMover.MakeIntangible): Unity filters its contacts and queries by the new layer at
        /// once, so its colliders are rebuilt like any other collider change.
        /// </summary>
        public void LayerChanged(GameObject gameObject)
        {
            Generation++;
            if (!Suspended && _byObject.TryGetValue(gameObject, out var list))
                foreach (ColliderState s in list) Changed(s);
        }

        void Changed(ColliderState s)
        {
            if (BuildsLater(s)) RebuildLater(s);
            else Rebuild(s);
            if (s.Composite != null) Rebuild(s.Composite);
        }

        // Colliders changed with their fixtures not built yet (RebuildLater), in the order of their last change.
        readonly List<ColliderState> _unbuilt = new List<ColliderState>();

        /// <summary>
        /// A change to a box on a dynamic body builds its fixture at the next Settle, not at once: Movement writes the player
        /// box's offset and then its size, and each write rebuilt it. Such a box is one fixture with one broadphase proxy, and
        /// a rebuild frees its leaf node last and takes it first again, so building it once gives it the same proxy id (the
        /// broadphase's pair order) as building it twice; a collider of several proxies would get them back in another order.
        /// </summary>
        static bool BuildsLater(ColliderState s)
            => s.Collider is BoxCollider2D && s.Body != null && s.Body.Rigidbody.bodyType == RigidbodyType2D.Dynamic;

        void RebuildLater(ColliderState s)
        {
            Reshape(s);
            // Last changed, built last: each at-once rebuild put its new fixture at the head of the body's fixture list, so
            // the list ends up in the order of the colliders' last rebuilds (the mass sum and the proxy syncs walk it).
            if (s.Unbuilt) _unbuilt.Remove(s);
            s.Unbuilt = true;
            _unbuilt.Add(s);
        }

        /// <summary>
        /// Builds the fixtures still waiting (RebuildLater), in the order of their colliders' last changes. Whatever reads or
        /// changes the physics world calls it first: syncs and steps, queries, body reads and writes, contacts, captures and
        /// restores (and a restore's suspension), and the snapshot store's fixture lookups.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Settle()
        {
            if (_unbuilt.Count != 0) BuildWaiting();
        }

        void BuildWaiting()
        {
            for (int i = 0; i < _unbuilt.Count; i++)
            {
                ColliderState s = _unbuilt[i];
                s.Unbuilt = false;
                BuildFixtures(s);
            }
            _unbuilt.Clear();
        }

        // Body-less colliders whose transforms a script wrote since the last sync (ColliderState.Moved).
        readonly List<ColliderState> _movedByScript = new List<ColliderState>();

        public void TransformChanged(Transform transform, bool pose)
        {
            Rigidbody2D rb = transform.m_GameObject.GetComponent<Rigidbody2D>();
            if (rb == null)
            {
                if (_byObject.TryGetValue(transform.m_GameObject, out var list))
                    foreach (ColliderState c in list)
                        if (c.Body == null && c.Collider.attachedRigidbody == null && !c.Moved) { c.Moved = true; _movedByScript.Add(c); }
                return;
            }
            if (!_bodies.TryGetValue(rb, out BodyState b)) return;
            if (pose) b.TransformDirty = true;
            else b.ScaleDirty = true;
        }

        public void SyncTransforms()
        {
            Settle();
            if (_worldMoved)
            {
                _worldMoved = false;
                for (int i = 0; i < _bodyList.Length; i++) _bodyList[i].TransformDirty = true;
                if (!_staticsMoved) RebuildBodyless();
                _staticsMoved = false;
            }
            for (int bi = 0; bi < _bodyList.Length; bi++)
            {
                BodyState b = _bodyList[bi];
                if (!b.TransformDirty && !b.ScaleDirty) continue;
                Transform t = b.Rigidbody.transform;
                if (b.TransformDirty)
                {
                    Moving(b);
                    b.Body.SetTransform(new b2Vec2(t.m_Position.x, t.m_Position.y), t.m_Rotation.eulerAngles.z * (MathF.PI / 180f));
                }
                b.TransformDirty = b.ScaleDirty = false;
                foreach (ColliderState s in b.Colliders)
                    if (s.BuiltScale != t.m_LossyScale) { Rebuild(s); s.Rescaled = true; _rescaled.Add(s); }
            }
            if (_movedByScript.Count != 0) SyncMoved();
        }

        /// <summary>The body-less colliders a script moved, rebuilt where their transforms are now, by collider id (the order the
        /// writes came in depends on what ran before a restore).</summary>
        void SyncMoved()
        {
            _movedByScript.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (ColliderState s in _movedByScript)
            {
                s.Moved = false;
                Rebuild(s);
            }
            _movedByScript.Clear();
        }

        public void Simulate(float deltaTime)
        {
            SyncTransforms();
            if (_unbodied.Count != 0 && _sim.Engine.IsRunning)
                foreach (ColliderState s in _unbodied)
                    if (Unbodied(s))
                    {
                        _sim.Engine.Abort("Unmodeled physics: " + s.Collider.name + "'s " + s.Collider.GetType().Name
                            + " is on, its dynamic or kinematic Rigidbody2D off at the world export (the engine has no body for it)");
                        break;
                    }
            FindLapsedTouches();
            _b2.Step(deltaTime, _velocityIterations, _positionIterations);
            for (int bi = 0; bi < _bodyList.Length; bi++)
            {
                BodyState b = _bodyList[bi];
                if (b.Rigidbody.bodyType == RigidbodyType2D.Static) continue;
                b2Vec2 p = b.Body.GetPosition();
                Transform t = b.Rigidbody.transform;
                t.StoreWorldPosition(new Vector3(p.x, p.y, t.m_Position.z));
            }
            DispatchCallbacks();
            for (int i = 0; i < _rescaled.Count; i++) _rescaled[i].Rescaled = false;
            _rescaled.Clear();
            ClearMoved();
        }

        void Shift(BodyState b)
        {
            if (b.Shifted) return;
            b.Shifted = true;
            _shifted.Add(b);
        }

        readonly List<Pair> _pairsNow = new List<Pair>();
        readonly Dictionary<Pair, List<b2Contact>> _contactsNow = new Dictionary<Pair, List<b2Contact>>();
        // Parallel to _pairsNow (both grow in the same branch of CollectTouching): DispatchCallbacks reads a pair's
        // contacts by index instead of hashing the pair in _contactsNow again.
        readonly List<List<b2Contact>> _pairContacts = new List<List<b2Contact>>();
        readonly Stack<List<b2Contact>> _contactLists = new Stack<List<b2Contact>>();
        List<Pair> _pairsBefore = new List<Pair>();
        readonly List<ColliderState> _rescaled = new List<ColliderState>();
        // Trigger pairs overlapping after this step's move (CollectTouching), and the ones whose touch lapsed at its start
        // (FindLapsedTouches); bodies moved and shapes rebuilt since the last step ends, which that check tests again.
        readonly HashSet<Pair> _overlapNext = new HashSet<Pair>(), _lapsed = new HashSet<Pair>();
        readonly List<BodyState> _shifted = new List<BodyState>();
        readonly List<ColliderState> _reshaped = new List<ColliderState>();

        public int ContactCount { get { Settle(); return _b2.GetContactCount(); } }
        public int TouchingPairs { get { return _pairsBefore.Count; } }
        public int BodyCount { get { return _b2.GetBodyCount(); } }

        /// <summary>The player's touches after the last step (<see cref="DispatchCallbacks"/>): a spike trigger (spikeScript: its
        /// OnTriggerEnter2D/Stay2D pairs), and the side (-1: the surface is at -x) of a solid surface it touches whose normal
        /// is neither axis-aligned: facing down (an overhang: y &lt; 0) or up (a slope: y &gt; 0). The tech contacts read it
        /// (ContactLog.Step).</summary>
        public struct PlayerTouches
        {
            public bool Spike;
            public float Overhang, Slope;
            /// <summary>A surface over the player's head (its normal within 45° of straight down): a head bonk (the waste audit).</summary>
            public bool Ceiling;
        }
        public PlayerTouches PlayerTouch;
        const float SlantedNormal = .02f;

        void CollectPlayerTouch()
        {
            var touch = new PlayerTouches();
            for (int i = 0; i < _pairsNow.Count; i++)
            {
                Pair p = _pairsNow[i];
                bool selfIsA = p.A.Body == _player;
                if (!selfIsA && p.B.Body != _player) continue;
                ColliderState other = selfIsA ? p.B : p.A;
                if (p.Trigger)
                {
                    if (other.TouchKind < 0) other.TouchKind = other.Collider.GetComponent<spikeScript>() != null ? (sbyte)1 : (sbyte)0;
                    if (other.TouchKind == 1) touch.Spike = true;
                    continue;
                }
                List<b2Contact> contacts = _pairContacts[i];
                for (int k = 0; k < contacts.Count; k++)
                {
                    b2Contact c = contacts[k];
                    c.GetWorldManifold(_worldManifold);
                    // Box2D's normal points from fixture A to B; the player sees the other surface's normal (toward itself).
                    bool selfIsB = ((ColliderState)c.GetFixtureB().UserData).Body == _player;
                    float nx = selfIsB ? _worldManifold.normal.x : -_worldManifold.normal.x, ny = selfIsB ? _worldManifold.normal.y : -_worldManifold.normal.y;
                    if (ny < -.7f) touch.Ceiling = true;
                    if (MathF.Abs(nx) < SlantedNormal || MathF.Abs(ny) < SlantedNormal) continue;
                    float side = nx > 0f ? -1f : 1f;
                    if (ny < 0f) touch.Overhang = side;
                    else touch.Slope = side;
                }
            }
            PlayerTouch = touch;
        }

        readonly List<(Pair pair, int message, Collision2D a, Collision2D b)> _sends = new List<(Pair, int, Collision2D, Collision2D)>();

        void DispatchCallbacks()
        {
            CollectTouching();
            CollectPlayerTouch();
            _sends.Clear();
            _collisionsUsed = 0;
            for (int i = 0; i < _pairsBefore.Count; i++)
            {
                Pair p = _pairsBefore[i];
                if (!_touchingNext.Contains(p)) Queue(p, PhaseExit, null);
            }
            for (int i = 0; i < _pairsNow.Count; i++)
            {
                Pair p = _pairsNow[i];
                Queue(p, _touching.Contains(p) && (_lapsed.Count == 0 || !_lapsed.Contains(p)) ? PhaseStay : PhaseEnter, _pairContacts[i]);
                if (_startOnly.Count > 0 && _startOnly.Contains(p)) Queue(p, PhaseExit, null);
            }
            for (int i = 0; i < _sends.Count; i++)
            {
                var (pair, message, a, b) = _sends[i];
                Send(pair, message, a, b);
            }
            AdvanceTouching();
        }

        void CollectTouching()
        {
            for (int i = 0; i < _pairContacts.Count; i++)
            {
                List<b2Contact> list = _pairContacts[i];
                list.Clear();
                _contactLists.Push(list);
            }
            _pairContacts.Clear();
            _contactsNow.Clear();
            _pairsNow.Clear();
            _touchingNext.Clear();
            _overlapNext.Clear();
            _startOnly.Clear();
            for (b2Contact c = _b2.GetContactList(); c != null; c = c.GetNext())
            {
                if (!c.IsEnabled()) continue;
                var sa = (ColliderState)c.GetFixtureA().UserData;
                var sb = (ColliderState)c.GetFixtureB().UserData;
                if (!sa.Listens && !sb.Listens) continue;
                b2Fixture fa = c.GetFixtureA(), fb = c.GetFixtureB();
                bool trigger = fa.IsSensor() || fb.IsSensor();
                var pair = new Pair((ColliderState)fa.UserData, (ColliderState)fb.UserData, trigger);
                bool overlap = trigger && Overlap(c, fa, fb);
                if (overlap) _overlapNext.Add(pair);
                // A touch only at the step's start (a write moved the body into the trigger and the step moved it out) enters
                // and exits in the step: the next step's overlap enters again (FalseEnding-Any% 353.32's dash pull-forward
                // into Course 3's CashPerLoop strip at tick 14122: 1x buys on the Stay at 14124, not 14123).
                bool startOnly = trigger && !overlap && c.IsTouching() && !sa.Rescaled && !sb.Rescaled && !_touching.Contains(pair);
                bool touching = trigger ? overlap || startOnly : c.IsTouching();
                if (!touching) continue;
                if (startOnly) { if (!_touchingNext.Contains(pair)) _startOnly.Add(pair); }
                else if (_startOnly.Remove(pair)) _touchingNext.Add(pair);
                if (!_contactsNow.TryGetValue(pair, out List<b2Contact> contacts))
                {
                    contacts = _contactLists.Count > 0 ? _contactLists.Pop() : new List<b2Contact>(2);
                    _contactsNow.Add(pair, contacts);
                    _pairsNow.Add(pair);
                    _pairContacts.Add(contacts);
                    if (!startOnly) _touchingNext.Add(pair);
                }
                contacts.Add(c);
            }
            for (int i = 0; i < _pairContacts.Count; i++)
                if (_pairContacts[i].Count > 1) SortByTouchOrder(_pairContacts[i]);
        }

        /// <summary>The trigger contact's shapes overlap where their bodies stand (b2TestOverlap, kept per contact: b2Contact.m_overlap).</summary>
        bool Overlap(b2Contact c, b2Fixture fa, b2Fixture fb)
        {
            b2Transform xfA = fa.GetBody().GetTransform(), xfB = fb.GetBody().GetTransform();
            if (c.m_overlap >= 0 && SameBits(c.m_overlapXfA, xfA) && SameBits(c.m_overlapXfB, xfB)) return c.m_overlap != 0;
            bool overlap = b2TestOverlap(fa.GetShape(), c.GetChildIndexA(), fb.GetShape(), c.GetChildIndexB(), xfA, xfB, _proxyBufferA, _proxyBufferB);
            c.m_overlapXfA = xfA;
            c.m_overlapXfB = xfB;
            c.m_overlap = (sbyte)(overlap ? 1 : 0);
            return overlap;
        }

        static bool SameBits<T>(in T a, in T b) where T : unmanaged
            => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)));

        // A contact's touch order and its place in the contact list walk: CollectTouching's sort key.
        readonly struct TouchOrdered
        {
            public readonly long Order;
            public readonly int Walk;
            public readonly b2Contact Contact;
            public TouchOrdered(long order, int walk, b2Contact contact) { Order = order; Walk = walk; Contact = contact; }
        }

        static readonly Comparison<TouchOrdered> ByTouchOrder = (a, b) => a.Order != b.Order ? (a.Order < b.Order ? -1 : 1) : a.Walk - b.Walk;
        TouchOrdered[] _touchOrdered = new TouchOrdered[8];

        /// <summary>
        /// A pair's contacts (gathered in the contact list's order) in b2Contact.m_touchOrder order, equal orders keeping the
        /// list's order: a stable sort, as the insertion into the sorted list before it did (whose shifting cost quadratic
        /// time). The walk index breaks ties, so every key differs and the sort needs no stability of its own. A list already
        /// in order is left as it is.
        /// </summary>
        void SortByTouchOrder(List<b2Contact> contacts)
        {
            int n = contacts.Count;
            int i = 1;
            while (i < n && contacts[i - 1].m_touchOrder <= contacts[i].m_touchOrder) i++;
            if (i == n) return;
            if (_touchOrdered.Length < n) _touchOrdered = new TouchOrdered[System.Math.Max(n, 2 * _touchOrdered.Length)];
            Span<TouchOrdered> keys = _touchOrdered.AsSpan(0, n);
            for (int k = 0; k < n; k++) keys[k] = new TouchOrdered(contacts[k].m_touchOrder, k, contacts[k]);
            keys.Sort(ByTouchOrder);
            for (int k = 0; k < n; k++) contacts[k] = keys[k].Contact;
        }

        void AdvanceTouching()
        {
            (_touching, _touchingNext) = (_touchingNext, _touching);
            _pairsBefore.Clear();
            for (int i = 0; i < _pairsNow.Count; i++)
            {
                Pair p = _pairsNow[i];
                if (_startOnly.Count > 0 && _startOnly.Contains(p)) continue;
                _pairsBefore.Add(p.Trigger && _overlapNext.Contains(p) ? p.Overlapping() : p);
            }
        }

        /// <summary>
        /// The trigger pairs touching after the last step whose touch lapsed at this step's start: Box2D collides a step's
        /// contacts before it moves the bodies and destroys those whose broadphase proxies no longer overlap there (a transform
        /// write between the steps moved a body out, or a shape was rebuilt away from the other); overlapping again after the
        /// step's move begins a new contact: Enter, not Stay (turbo: a dash's interpolation pull-back out of Course 1's start
        /// gate, on a dynamic body, the last step's move had just reached starts no timer, `startGate` running on Stay only).
        /// A trigger on a static body or on none (the ground body) never lapses: its touch goes on across any pull-back, rebase
        /// or not (turbo: a dash's pull-back out of the static `Spikes` composite killed on the step that moved back in, with
        /// Stay; a body-less upgrade box bought on Stay). Shapes rebuilt for a new scale keep the older rule (their
        /// start-of-step touch doesn't count; see the facing-flip entry in docs/fidelity-log.md).
        /// </summary>
        void FindLapsedTouches()
        {
            _lapsed.Clear();
            for (int i = 0; i < _pairsBefore.Count; i++)
            {
                Pair p = _pairsBefore[i];
                if (!p.Trigger || p.A.Rescaled || p.B.Rescaled || OnStaticBody(p.A) || OnStaticBody(p.B)) continue;
                // Unmoved and unrebuilt, both shapes stand where the last step left them: its overlap test still holds.
                bool overlapping = Moved(p.A) || Moved(p.B) ? ProxiesOverlap(p.A, p.B) : p.Overlapped;
                if (!overlapping) _lapsed.Add(p);
            }
        }

        static bool OnStaticBody(ColliderState s) => s.Body == null || s.Body.Body.Type() == b2BodyType.Static;

        static bool Moved(ColliderState s) => s.Reshaped || s.Body != null && s.Body.Shifted;

        // The two proxies' vertex buffers of this world's overlap and distance queries (b2DistanceProxy.Set).
        readonly b2Vec2[] _proxyBufferA = new b2Vec2[2], _proxyBufferB = new b2Vec2[2];

        /// <summary>Some child proxies of the two overlap in the broadphase: Box2D's Collide keeps their contact.</summary>
        bool ProxiesOverlap(ColliderState a, ColliderState b)
        {
            b2BroadPhase broadPhase = _b2.m_contactManager.m_broadPhase;
            foreach (b2Fixture fa in a.Fixtures)
                for (int ia = 0; ia < fa.m_proxyCount; ia++)
                {
                    b2AABB aabbA = broadPhase.GetFatAABB(fa.m_proxies[ia].proxyId);
                    foreach (b2Fixture fb in b.Fixtures)
                        for (int ib = 0; ib < fb.m_proxyCount; ib++)
                            if (Box2D.Collision.TestOverlap(aabbA, broadPhase.GetFatAABB(fb.m_proxies[ib].proxyId))) return true;
                }
            return false;
        }

        void ClearMoved()
        {
            for (int i = 0; i < _shifted.Count; i++) _shifted[i].Shifted = false;
            _shifted.Clear();
            for (int i = 0; i < _reshaped.Count; i++) _reshaped[i].Reshaped = false;
            _reshaped.Clear();
        }

        // Message index offsets for Queue's phase (message = (Trigger ? 0 : 3) + phase), as the old "Enter"/"Stay"/"Exit" compares gave.
        const int PhaseEnter = 0, PhaseStay = 1, PhaseExit = 2;

        void Queue(Pair p, int phase, List<b2Contact> contacts)
        {
            int message = (p.Trigger ? 0 : 3) + phase;
            if (p.Trigger) { _sends.Add((p, message, null, null)); return; }
            _sends.Add((p, message, p.A.Handlers[message].Length > 0 ? MakeCollision(p.A, p.B, contacts) : null,
                p.B.Handlers[message].Length > 0 ? MakeCollision(p.B, p.A, contacts) : null));
        }

        void Send(Pair p, int message, Collision2D a, Collision2D b)
        {
            if (p.Trigger)
            {
                Call(p.A, message, p.B.Collider);
                Call(p.B, message, p.A.Collider);
                return;
            }
            if (a != null) Call(p.A, message, a);
            if (b != null) Call(p.B, message, b);
        }

        // The Collision2Ds of one DispatchCallbacks, reused by the next: a handler gets one only for its call. No game handler
        // keeps it or its contacts past the call (Movement and PlatformMover keep only its collider or rigidbody).
        readonly List<Collision2D> _collisions = new List<Collision2D>();
        int _collisionsUsed;

        Collision2D MakeCollision(ColliderState self, ColliderState other, List<b2Contact> contacts)
        {
            Collision2D collision;
            if (_collisionsUsed < _collisions.Count)
            {
                collision = _collisions[_collisionsUsed];
                collision.relativeVelocity = default;
                collision.enabled = true;
                collision.Contacts.Clear();
            }
            else
            {
                collision = new Collision2D();
                _collisions.Add(collision);
            }
            _collisionsUsed++;
            collision.collider = other.Collider;
            collision.otherCollider = self.Collider;
            if (contacts == null) return collision;
            foreach (b2Contact contact in contacts) AddPoints(collision.Contacts, self, other, contact);
            return collision;
        }

        readonly b2WorldManifold _worldManifold = new b2WorldManifold();

        void AddPoints(List<ContactPoint2D> into, ColliderState self, ColliderState other, b2Contact contact)
        {
            bool selfIsB = contact.GetFixtureB().UserData == self;
            b2WorldManifold wm = _worldManifold;
            contact.GetWorldManifold(wm);
            b2Manifold manifold = contact.GetManifold();
            Vector2 normal = selfIsB ? new Vector2(wm.normal.x, wm.normal.y) : new Vector2(-wm.normal.x, -wm.normal.y);
            for (int i = 0; i < manifold.pointCount; i++)
                into.Add(new ContactPoint2D
                {
                    point = new Vector2(wm.points[i].x, wm.points[i].y),
                    normal = normal,
                    separation = wm.separations[i],
                    normalImpulse = manifold.points[i].normalImpulse,
                    tangentImpulse = manifold.points[i].tangentImpulse,
                    collider = other.Collider,
                    otherCollider = self.Collider,
                    enabled = true,
                });
        }

        static readonly string[] MessageNames =
            { "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D", "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D" };

        static (MonoBehaviour script, Action<object> call)[][] Handlers(MonoBehaviour[] scripts)
        {
            var table = new (MonoBehaviour, Action<object>)[MessageNames.Length][];
            for (int m = 0; m < MessageNames.Length; m++)
            {
                var list = new List<(MonoBehaviour, Action<object>)>();
                foreach (MonoBehaviour script in scripts)
                {
                    System.Reflection.MethodInfo method = Messages.Find(script.GetType(), MessageNames[m]);
                    if (method == null || method.GetMethodBody().GetILAsByteArray().Length <= 1) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        var call = (Action)Delegate.CreateDelegate(typeof(Action), script, method);
                        list.Add((script, _ => call()));
                    }
                    else if (parameters[0].ParameterType == typeof(Collider2D))
                    {
                        var call = (Action<Collider2D>)Delegate.CreateDelegate(typeof(Action<Collider2D>), script, method);
                        list.Add((script, arg => call((Collider2D)arg)));
                    }
                    else
                    {
                        var call = (Action<Collision2D>)Delegate.CreateDelegate(typeof(Action<Collision2D>), script, method);
                        list.Add((script, arg => call((Collision2D)arg)));
                    }
                }
                table[m] = list.ToArray();
            }
            return table;
        }

        void Call(ColliderState target, int message, object argument)
        {
            (MonoBehaviour script, Action<object> call)[] handlers = target.Handlers[message];
            for (int i = 0; i < handlers.Length; i++)
            {
                _sim.Touch(handlers[i].script);
                handlers[i].call(argument);
            }
        }

        public RaycastHit2D Raycast(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
            Settle();
            SyncMovedWorld();
            if (distance == 0f) return default;
            Vector2 dir = direction.normalized;
            if (distance < 0f) { dir = -dir; distance = -distance; }
            if (float.IsPositiveInfinity(distance)) distance = 100000f;
            var p1 = new b2Vec2(origin.x, origin.y);
            var p2 = new b2Vec2(origin.x + dir.x * distance, origin.y + dir.y * distance);
            if (_queriesStartInColliders)
            {
                ColliderState inside = FindInside(p1, layerMask);
                if (inside != null) return new RaycastHit2D(inside.Collider, origin, -dir, 0f, 0f);
            }
            if (!FindRayHit(p1, p2, layerMask))
            {
                _ray.Reset(layerMask);
                _b2.RayCast(_ray, p1, p2);
            }
            if (_ray.Hit == null) return default;
            return new RaycastHit2D(_ray.Hit.Collider, new Vector2(_ray.Point.x, _ray.Point.y), new Vector2(_ray.Normal.x, _ray.Normal.y),
                _ray.Fraction * distance, _ray.Fraction);
        }

        readonly InsideQuery _inside;
        readonly ClosestRay _ray;

        const float CellSize = 64f;
        struct StaticShape { public b2Fixture Fixture; public int Child; public ColliderState State; public b2AABB Box; }
        readonly List<ColliderState> _regrid = new List<ColliderState>();
        int[] _seen = Array.Empty<int>();
        int _stamp;

        /// <summary>
        /// The static shapes' query grid: a slot per child proxy of every static fixture (Static), listed in each 64-unit cell
        /// its box covers. A layout puts the cells in one array (CellStart/CellSlots, a counting sort over the slots); a
        /// shape regridded since is listed in Extra; a slot freed by a regrid stays listed where it was, skipped (its Fixture
        /// is null), until the next layout. A cell's order doesn't matter to its queries (each takes a lone hit, else asks
        /// Box2D, or a minimum).
        /// </summary>
        sealed class Grid
        {
            public StaticShape[] Static = Array.Empty<StaticShape>();
            public int StaticCount, Dead;
            public int[] CellStart = Array.Empty<int>(), CellFill = Array.Empty<int>(), CellSlots = Array.Empty<int>();
            public List<int>[] Extra = Array.Empty<List<int>>();
            public readonly List<int> ExtraUsed = new List<int>();
            public float X, Y;
            public int CellsX, CellsY;
            // Per collider (by Id): its slots, and what they hold: these fixtures (this many children; -1 for a collider
            // off the grid, on a body that isn't static) at this body pose.
            public readonly List<int>[] Slots;
            public readonly b2Fixture[][] Fixtures;
            public readonly int[] Children;
            public readonly b2Transform[] Poses;

            public Grid(int colliders)
            {
                Slots = new List<int>[colliders];
                Fixtures = new b2Fixture[colliders][];
                Children = new int[colliders];
                Poses = new b2Transform[colliders];
                for (int i = 0; i < colliders; i++) { Slots[i] = new List<int>(); Fixtures[i] = Array.Empty<b2Fixture>(); Children[i] = -1; }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ReadOnlySpan<int> Built(int c) => new ReadOnlySpan<int>(CellSlots, CellStart[c], CellStart[c + 1] - CellStart[c]);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ReadOnlySpan<int> Added(int c) => CollectionsMarshal.AsSpan(Extra[c]);
        }

        Grid _grid;
        // Grids laid out before, newest first: a restore back across a FloatingOrigin rebase returns every static shape to
        // where one of them has it, and that grid is taken back instead of laid out again (Matches checks every collider).
        readonly List<Grid> _spareGrids = new List<Grid>();
        const int SpareGrids = 2;
        // A regrid that changes this many shapes lays the grid out again (a rebase, a restore across one) instead.
        const int LayOutAbove = 1024;

        // The fixtures of non-static bodies by layer, built again (into the same lists) at the first query after a rebuild or
        // restore marks them dirty.
        readonly List<(b2Fixture fixture, ColliderState state)>[] _movers = new List<(b2Fixture, ColliderState)>[32];
        bool _moversDirty = true;
        // The layers whose _movers list holds fixtures: a query walks only the ones its mask shares, not every bit of the mask.
        uint _moverLayers;

        void BuildMovers()
        {
            for (uint layers = _moverLayers; layers != 0; layers &= layers - 1)
                _movers[System.Numerics.BitOperations.TrailingZeroCount(layers)].Clear();
            _moverLayers = 0;
            for (b2Body body = _b2.GetBodyList(); body != null; body = body.GetNext())
                if (body.Type() != b2BodyType.Static)
                    for (b2Fixture f = body.GetFixtureList(); f != null; f = f.GetNext())
                    {
                        var s = (ColliderState)f.UserData;
                        (_movers[s.Layer] ??= new List<(b2Fixture, ColliderState)>()).Add((f, s));
                        _moverLayers |= 1u << s.Layer;
                    }
            _moversDirty = false;
        }

        void RegridLater(ColliderState s)
        {
            if (_grid == null || s.Regrid) return;
            s.Regrid = true;
            _regrid.Add(s);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureGrid()
        {
            if (_grid == null || _regrid.Count != 0) UpdateGrid();
        }

        void UpdateGrid()
        {
            if (_grid == null)
            {
                _grid = new Grid(_colliderList.Length);
                LayOut(_grid);
                return;
            }
            Grid g = _grid;
            int stale = 0;
            bool outside = false;
            foreach (ColliderState s in _regrid)
                if (!Holds(g, s))
                {
                    stale += System.Math.Max(Children(s), g.Slots[s.Id].Count);
                    outside = outside || !InBounds(g, s);
                }
            // A shape leaving the grid (a rebase's first rebuilds) lays out a grid too: the current one is kept whole as a spare.
            if (stale > LayOutAbove || outside)
            {
                foreach (ColliderState s in _regrid) s.Regrid = false;
                _regrid.Clear();
                // The current grid is whole (no regrid touched it): it becomes a spare.
                Grid back = null;
                foreach (Grid spare in _spareGrids)
                    if (Matches(spare)) { back = spare; break; }
                if (back != null) _spareGrids.Remove(back);
                else if (_spareGrids.Count == SpareGrids) { back = _spareGrids[SpareGrids - 1]; _spareGrids.RemoveAt(SpareGrids - 1); LayOut(back); }
                else LayOut(back = new Grid(_colliderList.Length));
                _spareGrids.Insert(0, g);
                _grid = back;
                return;
            }
            foreach (ColliderState s in _regrid)
            {
                s.Regrid = false;
                Regrid(g, s);
            }
            _regrid.Clear();
            // Freed slots cost every query that walks their cells: past half the slots, lay the grid out again.
            if (g.Dead > g.StaticCount / 2) LayOut(g);
        }

        /// <summary>The grid holds the collider as it is now: the same fixtures (a fixture's shape never changes) at the same
        /// body pose, or none for a collider on a body that isn't static.</summary>
        bool Holds(Grid g, ColliderState s)
        {
            if ((s.Body?.Body ?? _ground).Type() != b2BodyType.Static) return g.Children[s.Id] < 0 && g.Slots[s.Id].Count == 0;
            return Gridded(g, s);
        }

        bool Matches(Grid g)
        {
            for (int i = 0; i < _colliderList.Length; i++)
                if (!Holds(g, _colliderList[i])) return false;
            return true;
        }

        static b2AABB GridBox(b2Fixture f, int child)
        {
            f.GetShape().ComputeAABB(out b2AABB box, f.GetBody().GetTransform(), child);
            box.lowerBound -= new b2Vec2(1f, 1f);
            box.upperBound += new b2Vec2(1f, 1f);
            return box;
        }

        void LayOut(Grid g)
        {
            foreach (ColliderState s in _regrid) s.Regrid = false;
            _regrid.Clear();
            for (int i = 0; i < g.Slots.Length; i++) g.Slots[i].Clear();
            g.StaticCount = 0;
            g.Dead = 0;
            bool first = true;
            float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;
            void Cover(in b2AABB box)
            {
                if (first) { minX = box.lowerBound.x; minY = box.lowerBound.y; maxX = box.upperBound.x; maxY = box.upperBound.y; first = false; }
                minX = MathF.Min(minX, box.lowerBound.x); minY = MathF.Min(minY, box.lowerBound.y);
                maxX = MathF.Max(maxX, box.upperBound.x); maxY = MathF.Max(maxY, box.upperBound.y);
            }
            for (b2Body body = _b2.GetBodyList(); body != null; body = body.GetNext())
            {
                if (body.Type() != b2BodyType.Static) continue;
                // A static body whose transform moved (a rebase: the next sync moves every body) is covered where it goes too,
                // so its regrid then stays inside the grid (a translation: its boxes move by the same amount, plus a margin).
                float dx = 0f, dy = 0f;
                if (body.UserData is BodyState moving && (_worldMoved || moving.TransformDirty))
                {
                    Vector3 to = moving.Rigidbody.transform.m_Position;
                    b2Vec2 at = body.GetPosition();
                    dx = to.x - at.x; dy = to.y - at.y;
                }
                for (b2Fixture f = body.GetFixtureList(); f != null; f = f.GetNext())
                    for (int c = 0; c < f.m_proxyCount; c++)
                    {
                        b2AABB box = GridBox(f, c);
                        Cover(box);
                        if (dx != 0f || dy != 0f)
                            Cover(new b2AABB { lowerBound = new b2Vec2(box.lowerBound.x + dx - 2f, box.lowerBound.y + dy - 2f),
                                upperBound = new b2Vec2(box.upperBound.x + dx + 2f, box.upperBound.y + dy + 2f) });
                        var state = (ColliderState)f.UserData;
                        int i = NewSlot(g);
                        g.Static[i] = new StaticShape { Fixture = f, Child = c, State = state, Box = box };
                        g.Slots[state.Id].Add(i);
                    }
            }
            g.X = minX; g.Y = minY;
            g.CellsX = (int)((maxX - minX) / CellSize) + 1;
            g.CellsY = (int)((maxY - minY) / CellSize) + 1;
            int cells = g.CellsX * g.CellsY;
            // A larger layout's arrays are kept (grids laid out across a rebase alternate between two sizes).
            if (g.CellStart.Length < cells + 1)
            {
                g.CellStart = new int[cells + 1];
                g.CellFill = new int[cells];
                g.Extra = new List<int>[cells];
                g.ExtraUsed.Clear();
            }
            else Array.Clear(g.CellStart, 0, cells + 1);
            foreach (int c in g.ExtraUsed) g.Extra[c].Clear();
            g.ExtraUsed.Clear();
            // Counting sort: each cell's count, its start, then the slots in slot order.
            int[] start = g.CellStart, fill = g.CellFill;
            int cellsX = g.CellsX;
            for (int i = 0; i < g.StaticCount; i++)
            {
                CellRange(g, g.Static[i].Box, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y <= y1; y++)
                    for (int x = y * cellsX + x0 + 1, end = y * cellsX + x1 + 1; x <= end; x++) start[x]++;
            }
            for (int c = 0; c < cells; c++) { fill[c] = start[c]; start[c + 1] += start[c]; }
            if (g.CellSlots.Length < start[cells]) g.CellSlots = new int[System.Math.Max(start[cells], 2 * g.CellSlots.Length)];
            int[] slots = g.CellSlots;
            for (int i = 0; i < g.StaticCount; i++)
            {
                CellRange(g, g.Static[i].Box, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y <= y1; y++)
                    for (int x = y * cellsX + x0, end = y * cellsX + x1; x <= end; x++) slots[fill[x]++] = i;
            }
            foreach (ColliderState s in _colliderList)
                if ((s.Body?.Body ?? _ground).Type() == b2BodyType.Static) NoteGrid(g, s);
                else { g.Children[s.Id] = -1; g.Fixtures[s.Id] = Array.Empty<b2Fixture>(); }
        }

        int NewSlot(Grid g)
        {
            if (g.StaticCount == g.Static.Length) Array.Resize(ref g.Static, System.Math.Max(256, g.Static.Length * 2));
            if (g.StaticCount >= _seen.Length) Array.Resize(ref _seen, g.Static.Length);
            return g.StaticCount++;
        }

        /// <summary>The collider's slots as it is now; its boxes lie inside the grid (UpdateGrid checked <see cref="InBounds"/>).</summary>
        void Regrid(Grid g, ColliderState s)
        {
            bool isStatic = (s.Body?.Body ?? _ground).Type() == b2BodyType.Static;
            if (isStatic && Gridded(g, s)) return;
            List<int> slots = g.Slots[s.Id];
            foreach (int i in slots)
            {
                g.Static[i] = default;
                g.Dead++;
            }
            slots.Clear();
            if (!isStatic) { g.Children[s.Id] = -1; g.Fixtures[s.Id] = Array.Empty<b2Fixture>(); return; }
            foreach (b2Fixture f in s.Fixtures)
                for (int c = 0; c < f.m_proxyCount; c++) AddShape(g, f, c, s, GridBox(f, c));
            NoteGrid(g, s);
        }

        /// <summary>Every box of the collider's fixtures lies inside the grid.</summary>
        bool InBounds(Grid g, ColliderState s)
        {
            if ((s.Body?.Body ?? _ground).Type() != b2BodyType.Static) return true;
            float right = g.X + g.CellsX * CellSize, top = g.Y + g.CellsY * CellSize;
            foreach (b2Fixture f in s.Fixtures)
                for (int c = 0; c < f.m_proxyCount; c++)
                {
                    b2AABB box = GridBox(f, c);
                    if (box.lowerBound.x < g.X || box.lowerBound.y < g.Y || box.upperBound.x >= right || box.upperBound.y >= top) return false;
                }
            return true;
        }

        static int Children(ColliderState s)
        {
            int children = 0;
            foreach (b2Fixture f in s.Fixtures) children += f.m_proxyCount;
            return children;
        }

        void NoteGrid(Grid g, ColliderState s)
        {
            g.Fixtures[s.Id] = s.Fixtures.ToArray();
            g.Children[s.Id] = Children(s);
            g.Poses[s.Id] = (s.Body?.Body ?? _ground).GetTransform();
        }

        /// <summary>
        /// Its slots already hold every child of its fixtures where it is now: the same fixtures (a fixture's shape never changes)
        /// at the same body pose. A restore marks the colliders of every static body ever moved (after a FloatingOrigin rebase:
        /// the big composites among them) and of every rebuilt collider for a regrid; most are unchanged.
        /// </summary>
        bool Gridded(Grid g, ColliderState s)
        {
            b2Fixture[] held = g.Fixtures[s.Id];
            if (s.Fixtures.Count != held.Length || Children(s) != g.Children[s.Id]) return false;
            bool same = true;
            for (int k = 0; k < held.Length; k++)
                if (!ReferenceEquals(s.Fixtures[k], held[k]))
                {
                    if (!SameShape(s.Fixtures[k].b2Shape, held[k].b2Shape)) return false;
                    same = false;
                }
            b2Transform now = (s.Body?.Body ?? _ground).GetTransform(), pose = g.Poses[s.Id];
            if (!(now.p.x == pose.p.x && now.p.y == pose.p.y && now.q == pose.q)) return false;
            if (!same) Retarget(g, s, held);
            return true;
        }

        /// <summary>A rebuild of the same geometry (a rebase's rebuild on a run that crossed it before, a toggle off and back): the
        /// slots take the new fixtures in place of the held ones, whose shapes and pose, so every query's answer, are the same.</summary>
        static void Retarget(Grid g, ColliderState s, b2Fixture[] held)
        {
            foreach (int i in g.Slots[s.Id])
                g.Static[i].Fixture = s.Fixtures[Array.IndexOf(held, g.Static[i].Fixture)];
            g.Fixtures[s.Id] = s.Fixtures.ToArray();
        }

        /// <summary>The two shapes are the same, bit for bit (the fields every query and AABB of theirs reads).</summary>
        static bool SameShape(b2Shape a, b2Shape b)
        {
            if (a.GetType() != b.GetType() || !Same(a.m_radius, b.m_radius)) return false;
            switch (a)
            {
                case b2PolygonShape p:
                {
                    var q = (b2PolygonShape)b;
                    if (p.m_count != q.m_count || !Same(p.m_centroid, q.m_centroid)) return false;
                    for (int i = 0; i < p.m_count; i++)
                        if (!Same(p.m_vertices[i], q.m_vertices[i]) || !Same(p.m_normals[i], q.m_normals[i])) return false;
                    return true;
                }
                case b2ChainShape c:
                {
                    var d = (b2ChainShape)b;
                    if (c.m_count != d.m_count || !Same(c.m_prevVertex, d.m_prevVertex) || !Same(c.m_nextVertex, d.m_nextVertex)) return false;
                    for (int i = 0; i < c.m_count; i++) if (!Same(c.m_vertices[i], d.m_vertices[i])) return false;
                    return true;
                }
                case b2CircleShape c: return Same(c.m_p, ((b2CircleShape)b).m_p);
                case b2EdgeShape e:
                {
                    var f = (b2EdgeShape)b;
                    return e.m_oneSided == f.m_oneSided && Same(e.m_vertex1, f.m_vertex1) && Same(e.m_vertex2, f.m_vertex2)
                        && e.m_vertex0.HasValue == f.m_vertex0.HasValue && (!e.m_vertex0.HasValue || Same(e.m_vertex0.Value, f.m_vertex0.Value))
                        && e.m_vertex3.HasValue == f.m_vertex3.HasValue && (!e.m_vertex3.HasValue || Same(e.m_vertex3.Value, f.m_vertex3.Value));
                }
                default: return false;
            }
        }

        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        static bool Same(b2Vec2 a, b2Vec2 b) => Same(a.x, b.x) && Same(a.y, b.y);

        void AddShape(Grid g, b2Fixture f, int child, ColliderState s, in b2AABB box)
        {
            int i = NewSlot(g);
            g.Static[i] = new StaticShape { Fixture = f, Child = child, State = s, Box = box };
            g.Slots[s.Id].Add(i);
            CellRange(g, box, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = y * g.CellsX + x;
                    List<int> added = g.Extra[c] ??= new List<int>();
                    if (added.Count == 0) g.ExtraUsed.Add(c);
                    added.Add(i);
                }
        }

        const float InvCellSize = 1f / CellSize;

        // Floor-free, same cells: x / 64 == x * (1 / 64f) exactly (a power of two); below 0 a lower bound clamps to 0 and an
        // upper bound's range is empty either way, and at or above 0 (or NaN) truncation is Floor.
        static void CellRange(Grid g, in b2AABB box, out int x0, out int y0, out int x1, out int y1)
        {
            float lx = (box.lowerBound.x - g.X) * InvCellSize, ly = (box.lowerBound.y - g.Y) * InvCellSize;
            float ux = (box.upperBound.x - g.X) * InvCellSize, uy = (box.upperBound.y - g.Y) * InvCellSize;
            x0 = System.Math.Max(0, (int)lx);
            y0 = System.Math.Max(0, (int)ly);
            x1 = ux < 0f ? -1 : System.Math.Min(g.CellsX - 1, (int)ux);
            y1 = uy < 0f ? -1 : System.Math.Min(g.CellsY - 1, (int)uy);
        }

        // Accept's trigger half. A mover list holds only its own layer's colliders (every Layer write marks _movers dirty), so the
        // mover loops need only this.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Hittable(ColliderState s) => _queriesHitTriggers || !s.Collider.m_IsTrigger;

        ColliderState FindInside(b2Vec2 p, int layerMask)
        {
            EnsureGrid();
            Grid g = _grid;
            ColliderState found = null;
            int count = 0;
            // CellRange of the point alone: its one cell, or none.
            float cx = (p.x - g.X) * InvCellSize, cy = (p.y - g.Y) * InvCellSize;
            int x = (int)cx, y = (int)cy;
            if (!(cx < 0f) && !(cy < 0f) && x < g.CellsX && y < g.CellsY)
            {
                int cell = y * g.CellsX + x;
                Inside(g, g.Built(cell), p, layerMask, ref found, ref count);
                Inside(g, g.Added(cell), p, layerMask, ref found, ref count);
            }
            if (_moversDirty) BuildMovers();
            for (uint layers = (uint)layerMask & _moverLayers; layers != 0; layers &= layers - 1)
                foreach (var (f, s) in CollectionsMarshal.AsSpan(_movers[System.Numerics.BitOperations.TrailingZeroCount(layers)]))
                {
                    if (!Hittable(s)) continue;
                    // A shape of several children (a chain) never holds a point: one test per fixture.
                    if (f.m_proxyCount > 0 && f.TestPoint(p)) { found = s; count++; }
                }
            if (count <= 1) return found;
            _inside.Reset(p, layerMask);
            _b2.QueryAABB(_inside, new b2AABB { lowerBound = p, upperBound = p });
            return _inside.Hit;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Inside(Grid g, ReadOnlySpan<int> slots, b2Vec2 p, int layerMask, ref ColliderState found, ref int count)
        {
            StaticShape[] shapes = g.Static;
            foreach (int i in slots)
            {
                ref StaticShape s = ref shapes[i];
                if (s.Fixture == null || !Contains(s.Box, p) || !Accept(s.State, layerMask) || !s.Fixture.TestPoint(p)) continue;
                found = s.State;
                count++;
            }
        }

        /// <summary>The cell's slots the segment may hit, each once per query (_seen); false once a second hit shows.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool RayCell(Grid g, ReadOnlySpan<int> slots, b2Vec2 p1, b2Vec2 p2, int layerMask, in b2RayCastInput input, ref int count)
        {
            StaticShape[] shapes = g.Static;
            foreach (int i in slots)
            {
                if (_seen[i] == _stamp) continue;
                _seen[i] = _stamp;
                ref StaticShape s = ref shapes[i];
                if (s.Fixture == null || SegmentMisses(s.Box, p1, p2)) continue;
                if (Accept(s.State, layerMask) && RayHit(s.Fixture, s.Child, s.State, input) && ++count > 1) return false;
            }
            return true;
        }

        bool FindRayHit(b2Vec2 p1, b2Vec2 p2, int layerMask)
        {
            EnsureGrid();
            Grid g = _grid;
            _ray.Reset(layerMask);
            b2RayCastInput input;
            input.maxFraction = 1.0f;
            input.p1 = p1;
            input.p2 = p2;
            int count = 0;
            CellRange(g, new b2AABB { lowerBound = b2Vec2.Min(p1, p2), upperBound = b2Vec2.Max(p1, p2) }, out int x0, out int y0, out int x1, out int y1);
            _stamp++;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * g.CellsX + x;
                    if (!RayCell(g, g.Built(cell), p1, p2, layerMask, input, ref count) || !RayCell(g, g.Added(cell), p1, p2, layerMask, input, ref count)) return false;
                }
            if (_moversDirty) BuildMovers();
            for (uint layers = (uint)layerMask & _moverLayers; layers != 0; layers &= layers - 1)
                foreach (var (f, s) in CollectionsMarshal.AsSpan(_movers[System.Numerics.BitOperations.TrailingZeroCount(layers)]))
                {
                    if (!Hittable(s)) continue;
                    for (int c = 0; c < f.m_proxyCount; c++)
                        if (RayHit(f, c, s, input) && ++count > 1) return false;
                }
            return true;
        }

        // A grid shape's Box is its AABB grown by 1 on every side (GridBox): a point inside the shape, or a ray's hit on it,
        // lies inside the AABB, so a point or segment outside the Box can't test inside or hit, and skipping it changes nothing.
        static bool Contains(in b2AABB box, b2Vec2 p)
        {
            return p.x >= box.lowerBound.x && p.x <= box.upperBound.x && p.y >= box.lowerBound.y && p.y <= box.upperBound.y;
        }

        /// <summary>
        /// The segment p1–p2 surely misses the box: separated on x, on y, or along the segment's normal (Gino's axis, as
        /// b2DynamicTree.RayCast). The Box's margin of 1 dwarfs the float error of the test.
        /// </summary>
        static bool SegmentMisses(in b2AABB box, b2Vec2 p1, b2Vec2 p2)
        {
            if (MathF.Max(p1.x, p2.x) < box.lowerBound.x || MathF.Min(p1.x, p2.x) > box.upperBound.x
                || MathF.Max(p1.y, p2.y) < box.lowerBound.y || MathF.Min(p1.y, p2.y) > box.upperBound.y) return true;
            float dx = p2.x - p1.x, dy = p2.y - p1.y;
            float cx = (box.lowerBound.x + box.upperBound.x) * 0.5f, cy = (box.lowerBound.y + box.upperBound.y) * 0.5f;
            float hx = (box.upperBound.x - box.lowerBound.x) * 0.5f, hy = (box.upperBound.y - box.lowerBound.y) * 0.5f;
            return MathF.Abs(dx * (p1.y - cy) - dy * (p1.x - cx)) > MathF.Abs(dy) * hx + MathF.Abs(dx) * hy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool RayHit(b2Fixture fixture, int child, ColliderState s, in b2RayCastInput input)
        {
            if (!fixture.RayCast(out b2RayCastOutput output, input, child)) return false;
            float fraction = output.fraction;
            b2Vec2 point = (1f - fraction) * input.p1 + fraction * input.p2;
            _ray.Hit = s; _ray.Point = point; _ray.Normal = output.normal; _ray.Fraction = fraction;
            return true;
        }

        sealed class InsideQuery : b2QueryCallback
        {
            readonly PhysicsWorld _owner;
            b2Vec2 _point;
            int _mask;
            public ColliderState Hit;
            public InsideQuery(PhysicsWorld owner) { _owner = owner; }
            public void Reset(b2Vec2 point, int mask) { _point = point; _mask = mask; Hit = null; }

            public override bool ReportFixture(b2Fixture fixture)
            {
                var s = (ColliderState)fixture.UserData;
                if (!_owner.Accept(s, _mask) || !fixture.TestPoint(_point)) return true;
                Hit = s;
                return false;
            }
        }

        sealed class ClosestRay : b2RayCastCallback
        {
            readonly PhysicsWorld _owner;
            int _mask;
            public ColliderState Hit;
            public b2Vec2 Point, Normal;
            public float Fraction;
            public ClosestRay(PhysicsWorld owner) { _owner = owner; }
            public void Reset(int mask) { _mask = mask; Hit = null; Fraction = 1f; }

            public override float ReportFixture(b2Fixture fixture, in b2Vec2 point, in b2Vec2 normal, float fraction)
            {
                var s = (ColliderState)fixture.UserData;
                if (!_owner.Accept(s, _mask)) return -1f;
                Hit = s; Point = point; Normal = normal; Fraction = fraction;
                return fraction;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Accept(ColliderState s, int layerMask)
        {
            return (layerMask & (1 << s.Layer)) != 0 && (_queriesHitTriggers || !s.Collider.m_IsTrigger);
        }

        /// <summary>Whether the two colliders' layers and include/exclude masks let them touch at all (the contact filter's
        /// test, <see cref="Filter"/>): of the player's two boxes only the 40×54 hurtbox meets the Default-layer course gates,
        /// the body box excludes Default.</summary>
        public bool CanTouch(Collider2D a, Collider2D b)
        {
            return (ContactMask(_layerMasks, a) & (1 << b.gameObject.layer)) != 0 && (ContactMask(_layerMasks, b) & (1 << a.gameObject.layer)) != 0;
        }

        public ColliderDistance2D Distance(Collider2D a, Collider2D b)
        {
            Settle();
            SyncMovedWorld();
            if (!_colliders.TryGetValue(a, out ColliderState sa) || !_colliders.TryGetValue(b, out ColliderState sb)
                || sa.Fixtures.Count == 0 || sb.Fixtures.Count == 0)
                return new ColliderDistance2D { isValid = false };
            if ((sb.Body?.Body ?? _ground).Type() == b2BodyType.Static && NearbyDistance(sa, sb, out float nearest))
                return new ColliderDistance2D { distance = nearest, isValid = true };
            float best = float.PositiveInfinity;
            foreach (b2Fixture fa in sa.Fixtures)
                foreach (b2Fixture fb in sb.Fixtures)
                    for (int ca = 0; ca < fa.GetShape().GetChildCount(); ca++)
                        for (int cb = 0; cb < fb.GetShape().GetChildCount(); cb++)
                            best = MathF.Min(best, ChildDistance(fa, ca, fb, cb));
            return new ColliderDistance2D { distance = best, isValid = true };
        }

        float ChildDistance(b2Fixture fa, int ca, b2Fixture fb, int cb)
        {
            var input = new b2DistanceInput();
            input.proxyA.Set(fa.GetShape(), ca, _proxyBufferA);
            input.proxyB.Set(fb.GetShape(), cb, _proxyBufferB);
            input.transformA = fa.GetBody().GetTransform();
            input.transformB = fb.GetBody().GetTransform();
            input.useRadii = true;
            var cache = new b2SimplexCache();
            b2Distance(out b2DistanceOutput output, ref cache, input);
            if (output.distance > 0f) return output.distance;
            // Unity's Collider2D.Distance is negative for overlapping shapes: the depth to separate them. GJK stops at 0, which
            // hid Movement's squish check (distance < squishDistance -20) offline: turbo died squished, offline ran on.
            float separation = MathF.Max(MaxSeparation(input.proxyA, input.transformA, input.proxyB, input.transformB),
                MaxSeparation(input.proxyB, input.transformB, input.proxyA, input.transformA));
            return float.IsNegativeInfinity(separation) ? output.distance : MathF.Min(0f, separation - input.proxyA._radius - input.proxyB._radius);
        }

        // Separating-axis separation of b from a along a's face normals (an edge has both of its normals); a lone vertex
        // (circle) has no faces.
        static float MaxSeparation(in b2DistanceProxy a, in b2Transform xfA, in b2DistanceProxy b, in b2Transform xfB)
        {
            int count = a._count;
            if (count < 2) return float.NegativeInfinity;
            float best = float.NegativeInfinity;
            for (int i = 0; i < (count == 2 ? 1 : count); i++)
            {
                b2Vec2 v1 = Box2D.Math.Mul(xfA, a._vertices[i]), v2 = Box2D.Math.Mul(xfA, a._vertices[(i + 1) % count]);
                b2Vec2 normal = b2Vec2.Normalize(Vectex.Cross(v2 - v1, 1f));
                float front = float.PositiveInfinity, back = float.PositiveInfinity;
                for (int j = 0; j < b._count; j++)
                {
                    float d = b2Vec2.Dot(normal, Box2D.Math.Mul(xfB, b._vertices[j]) - v1);
                    front = MathF.Min(front, d);
                    back = MathF.Min(back, -d);
                }
                best = MathF.Max(best, count == 2 ? MathF.Max(front, back) : front);
            }
            return best;
        }

        const float NearbyRange = 32f;

        bool NearbyDistance(ColliderState sa, ColliderState sb, out float best)
        {
            EnsureGrid();
            Grid g = _grid;
            best = float.PositiveInfinity;
            b2AABB box = default;
            bool first = true;
            foreach (b2Fixture fa in sa.Fixtures)
                for (int ca = 0; ca < fa.GetShape().GetChildCount(); ca++)
                {
                    fa.GetShape().ComputeAABB(out b2AABB c, fa.GetBody().GetTransform(), ca);
                    if (first) box = c;
                    else box = new b2AABB { lowerBound = b2Vec2.Min(box.lowerBound, c.lowerBound), upperBound = b2Vec2.Max(box.upperBound, c.upperBound) };
                    first = false;
                }
            box.lowerBound -= new b2Vec2(NearbyRange, NearbyRange);
            box.upperBound += new b2Vec2(NearbyRange, NearbyRange);
            CellRange(g, box, out int x0, out int y0, out int x1, out int y1);
            _stamp++;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * g.CellsX + x;
                    NearbyCell(g, g.Built(cell), sa, sb, ref best);
                    NearbyCell(g, g.Added(cell), sa, sb, ref best);
                }
            return best < NearbyRange - 1f;
        }

        void NearbyCell(Grid g, ReadOnlySpan<int> slots, ColliderState sa, ColliderState sb, ref float best)
        {
            StaticShape[] shapes = g.Static;
            foreach (int i in slots)
            {
                // A freed slot's State is null, never sb.
                if (_seen[i] == _stamp || shapes[i].State != sb) continue;
                _seen[i] = _stamp;
                foreach (b2Fixture fa in sa.Fixtures)
                    for (int ca = 0; ca < fa.GetShape().GetChildCount(); ca++)
                        best = MathF.Min(best, ChildDistance(fa, ca, shapes[i].Fixture, shapes[i].Child));
            }
        }

        public Vector2 GetPosition(Rigidbody2D body) { Settle(); b2Vec2 p = State(body).Body.GetPosition(); return new Vector2(p.x, p.y); }
        public void SetPosition(Rigidbody2D body, Vector2 position) { Settle(); BodyState s = State(body); Moving(s); b2Body b = s.Body; b.SetTransform(new b2Vec2(position.x, position.y), b.GetAngle()); }

        void Moving(BodyState body)
        {
            Shift(body);
            if (body.Body.Type() != b2BodyType.Static) return;
            if (!body.MovedStatic) { body.MovedStatic = true; _movedStatic.Add(body); }
            foreach (ColliderState s in body.Colliders) RegridLater(s);
        }
        public Vector2 GetVelocity(Rigidbody2D body) { Settle(); b2Vec2 v = State(body).Body.GetLinearVelocity(); return new Vector2(v.x, v.y); }
        public void SetVelocity(Rigidbody2D body, Vector2 velocity) { Settle(); State(body).Body.SetLinearVelocity(new b2Vec2(velocity.x, velocity.y)); }
        public float GetAngularVelocity(Rigidbody2D body) { Settle(); return State(body).Body.GetAngularVelocity() * (180f / MathF.PI); }
        public void SetAngularVelocity(Rigidbody2D body, float velocity) { Settle(); State(body).Body.SetAngularVelocity(velocity * (MathF.PI / 180f)); }

        BodyState State(Rigidbody2D body) { return (BodyState)body.BackendData; }

        public void ResetPlayer(Vector2 position, Vector2 velocity, float rotation, float angularVelocity)
        {
            SyncTransforms();
            _player.Body.SetTransform(new b2Vec2(position.x, position.y), rotation * (MathF.PI / 180f));
            _player.Body.SetLinearVelocity(new b2Vec2(velocity.x, velocity.y));
            _player.Body.SetAngularVelocity(angularVelocity * (MathF.PI / 180f));
            _player.TransformDirty = _player.ScaleDirty = false;
            ClearMoved();
            var contacts = _b2.GetContactManager();
            contacts.FindNewContacts();
            contacts.Collide();
            CollectTouching();
            _touching.Clear();
            AdvanceTouching();
        }

        public (float px, float py, float angle, float vx, float vy, float w) PlayerBody()
        {
            Settle();
            b2Vec2 p = _player.Body.GetPosition(), v = _player.Body.GetLinearVelocity();
            return (p.x, p.y, _player.Body.GetAngle(), v.x, v.y, _player.Body.GetAngularVelocity());
        }

        /// <summary>Game code wrote the player's transform since the last sync (a write moves the body at the next sync).</summary>
        public bool PlayerTransformDirty { get { return _player.TransformDirty; } }

        sealed class PhysicsSnapshot
        {
            public object World;
            public bool WorldMoved, StaticsMoved;
            /// <summary>The player's colliders' built scale, layer and contact mask (their fixtures are the body's).</summary>
            public (Vector3 scale, int layer, int mask)[] Player;
            public Pair[] Touching;
            public Built[] Changed;
            /// <summary>
            /// What the next step reads of the syncs and moves since the last one: each body's pending transform and scale sync
            /// and teleport (<see cref="BodyFlag"/> by body index), each collider rebuilt or rescaled since (by collider id). A
            /// capture between steps (a search's, after a tick's frames) can hold some: a spring's push synced after the step.
            /// </summary>
            public int[] Bodies, Colliders;
            /// <summary>
            /// Every body's transform but the player's (<see cref="Simulation.Restore"/> puts that back), by body index. A step
            /// writes each moving body's position back to it, and a script's write waits in it for the next sync (a zip mover's
            /// end-of-move localPosition, TransformDirty), which then moves the body there: a restore that left another run's
            /// transforms in place teleported the body to them.
            /// </summary>
            public Pose[] Poses;
        }

        /// <summary>A body's transform as a snapshot holds it (<see cref="PhysicsSnapshot.Poses"/>), and a transform a script moved
        /// (<see cref="SimSnapshot.Transforms"/>).</summary>
        internal struct Pose
        {
            public Vector3 Position, LocalPosition, LocalScale, LossyScale;
            public Quaternion Rotation, LocalRotation;

            public Pose(Transform t)
            {
                Position = t.m_Position; LocalPosition = t.m_LocalPosition; LocalScale = t.m_LocalScale; LossyScale = t.m_LossyScale;
                Rotation = t.m_Rotation; LocalRotation = t.m_LocalRotation;
            }

            /// <summary>Bit for bit: a -0 is another value to the digest, and a NaN never matches (the capture makes a new list).</summary>
            public bool Is(Transform t)
            {
                return SameBits(Position, t.m_Position) && SameBits(LocalPosition, t.m_LocalPosition) && SameBits(LocalScale, t.m_LocalScale)
                    && SameBits(LossyScale, t.m_LossyScale) && SameBits(Rotation, t.m_Rotation) && SameBits(LocalRotation, t.m_LocalRotation);
            }

            public void WriteTo(Transform t)
            {
                t.m_Position = Position; t.m_LocalPosition = LocalPosition; t.m_LocalScale = LocalScale; t.m_LossyScale = LossyScale;
                t.m_Rotation = Rotation; t.m_LocalRotation = LocalRotation;
            }

        }

        /// <summary>A rebuilt collider as a snapshot holds it: its fixtures and what they were built with.</summary>
        sealed class Built
        {
            public ColliderState State;
            public b2Fixture[] Fixtures;
            public Vector3 Scale;
            public int Layer, Mask;
        }

        [Flags]
        enum BodyFlag { TransformDirty = 1, ScaleDirty = 2, Shifted = 4 }

        [Flags]
        enum ColliderFlag { Reshaped = 1, Rescaled = 2, Moved = 4 }
        const int FlagBits = 3;

        // The rebuilt colliders as a snapshot holds them; null once one is rebuilt again. Shared by every snapshot taken
        // until then (after a rebase that is every body-less collider, each capture's largest part), and after a restore
        // the restored snapshot's until a rebuild.
        Built[] _changedCapture = Array.Empty<Built>();

        // The bodies' transforms as the last capture saw them (PhysicsSnapshot.Poses), shared by the captures after it while
        // none moves (a body at rest, the static ones), and after a restore the restored snapshot's.
        Pose[] _posesCapture;

        Pose[] CapturePoses()
        {
            Pose[] last = _posesCapture;
            if (last != null)
            {
                int i = 0;
                while (i < _bodyList.Length && (_bodyList[i] == _player || last[i].Is(_bodyList[i].Rigidbody.transform))) i++;
                if (i == _bodyList.Length) return last;
            }
            var poses = new Pose[_bodyList.Length];
            for (int i = 0; i < poses.Length; i++)
                if (_bodyList[i] != _player) poses[i] = new Pose(_bodyList[i].Rigidbody.transform);
            return _posesCapture = poses;
        }

        public object Capture()
        {
            Settle();
            if (_changedCapture == null)
            {
                _changedCapture = new Built[_changed.Count];
                for (int i = 0; i < _changedCapture.Length; i++)
                {
                    ColliderState c = _changed[i];
                    c.FixturesAs = c.Fixtures.ToArray();
                    _changedCapture[i] = new Built { State = c, Fixtures = c.FixturesAs, Scale = c.BuiltScale, Layer = c.Layer, Mask = c.ContactMask };
                }
            }
            var player = new (Vector3 scale, int layer, int mask)[_player.Colliders.Count];
            for (int i = 0; i < player.Length; i++)
            {
                ColliderState c = _player.Colliders[i];
                player[i] = (c.BuiltScale, c.Layer, c.ContactMask);
            }
            return new PhysicsSnapshot { World = _b2.SaveState(), WorldMoved = _worldMoved, StaticsMoved = _staticsMoved, Player = player,
                Touching = _pairsBefore.Count == 0 ? Array.Empty<Pair>() : _pairsBefore.ToArray(), Changed = _changedCapture,
                Bodies = PendingBodies(), Colliders = PendingColliders(), Poses = CapturePoses() };
        }

        int[] PendingBodies()
        {
            int n = 0;
            foreach (BodyState b in _bodyList) if (b.TransformDirty || b.ScaleDirty || b.Shifted) n++;
            if (n == 0) return Array.Empty<int>();
            var pending = new int[n];
            n = 0;
            foreach (BodyState b in _bodyList)
                if (b.TransformDirty || b.ScaleDirty || b.Shifted)
                    pending[n++] = b.Index << FlagBits | (int)((b.TransformDirty ? BodyFlag.TransformDirty : 0) | (b.ScaleDirty ? BodyFlag.ScaleDirty : 0) | (b.Shifted ? BodyFlag.Shifted : 0));
            return pending;
        }

        int[] PendingColliders()
        {
            if (_reshaped.Count == 0 && _rescaled.Count == 0 && _movedByScript.Count == 0) return Array.Empty<int>();
            var pending = new List<int>(_reshaped.Count + _rescaled.Count + _movedByScript.Count);
            ColliderFlag Moved(ColliderState c) => c.Moved ? ColliderFlag.Moved : 0;
            foreach (ColliderState c in _reshaped) pending.Add(c.Id << FlagBits | (int)(ColliderFlag.Reshaped | (c.Rescaled ? ColliderFlag.Rescaled : 0) | Moved(c)));
            foreach (ColliderState c in _rescaled) if (!c.Reshaped) pending.Add(c.Id << FlagBits | (int)(ColliderFlag.Rescaled | Moved(c)));
            foreach (ColliderState c in _movedByScript) if (!c.Reshaped && !c.Rescaled) pending.Add(c.Id << FlagBits | (int)ColliderFlag.Moved);
            return pending.ToArray();
        }

        public void Restore(object snapshot)
        {
            Generation++;
            Settle();
            var s = (PhysicsSnapshot)snapshot;
            _b2.LoadState(s.World);
            for (int i = 0; i < _bodyList.Length; i++)
                if (_bodyList[i] != _player) s.Poses[i].WriteTo(_bodyList[i].Rigidbody.transform);
            _posesCapture = s.Poses;
            // Not provably unchanged: the restored bodies' fixture lists (and the layers below) may differ from the lists'.
            _moversDirty = true;
            foreach (ColliderState c in _player.Colliders) { c.Fixtures.Clear(); c.FixturesAs = null; }
            for (b2Fixture f = _player.Body.GetFixtureList(); f != null; f = f.GetNext()) ((ColliderState)f.UserData).Fixtures.Add(f);
            // _changed only grows: every collider the snapshot lists is in it, and the ones it doesn't list were pristine then.
            foreach (Built saved in s.Changed) saved.State.Restoring = saved;
            foreach (ColliderState c in _changed)
            {
                Built saved = c.Restoring;
                c.Restoring = null;
                b2Fixture[] fixtures = saved?.Fixtures ?? c.Pristine;
                c.BuiltScale = saved?.Scale ?? c.PristineScale;
                c.Layer = saved?.Layer ?? c.PristineLayer;
                c.ContactMask = saved?.Mask ?? c.PristineMask;
                // The same array restored again (or the one its fixtures were captured into): nothing to compare.
                if (ReferenceEquals(fixtures, c.FixturesAs)) continue;
                c.FixturesAs = fixtures;
                if (Holds(c.Fixtures, fixtures)) continue;
                c.Fixtures.Clear();
                c.Fixtures.AddRange(fixtures);
                if ((c.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(c);
            }
            // Every collider now holds what the snapshot lists (or its pristine fixtures): a capture before the next rebuild
            // holds the same.
            _changedCapture = s.Changed;
            foreach (BodyState b in _movedStatic)
                foreach (ColliderState c in b.Colliders) RegridLater(c);
            _worldMoved = s.WorldMoved;
            _staticsMoved = s.StaticsMoved;
            for (int i = 0; i < _player.Colliders.Count; i++)
            {
                ColliderState c = _player.Colliders[i];
                (c.BuiltScale, c.Layer, c.ContactMask) = s.Player[i];
            }
            _touching.Clear();
            foreach (Pair p in s.Touching) _touching.Add(p);
            _pairsBefore.Clear();
            _pairsBefore.AddRange(s.Touching);
            ClearMoved();
            for (int i = 0; i < _rescaled.Count; i++) _rescaled[i].Rescaled = false;
            _rescaled.Clear();
            foreach (BodyState b in _bodyList) b.TransformDirty = b.ScaleDirty = false;
            foreach (ColliderState c in _movedByScript) c.Moved = false;
            _movedByScript.Clear();
            foreach (int pending in s.Bodies)
            {
                BodyState b = _bodyList[pending >> FlagBits];
                var flag = (BodyFlag)(pending & ((1 << FlagBits) - 1));
                b.TransformDirty = (flag & BodyFlag.TransformDirty) != 0;
                b.ScaleDirty = (flag & BodyFlag.ScaleDirty) != 0;
                if ((flag & BodyFlag.Shifted) != 0) Shift(b);
            }
            foreach (int pending in s.Colliders)
            {
                ColliderState c = _byId[pending >> FlagBits];
                var flag = (ColliderFlag)(pending & ((1 << FlagBits) - 1));
                if ((flag & ColliderFlag.Reshaped) != 0) { c.Reshaped = true; _reshaped.Add(c); }
                if ((flag & ColliderFlag.Rescaled) != 0) { c.Rescaled = true; _rescaled.Add(c); }
                if ((flag & ColliderFlag.Moved) != 0) { c.Moved = true; _movedByScript.Add(c); }
            }
        }

        static bool Holds(List<b2Fixture> list, b2Fixture[] fixtures)
        {
            if (list.Count != fixtures.Length) return false;
            for (int i = 0; i < fixtures.Length; i++)
                if (!ReferenceEquals(list[i], fixtures[i])) return false;
            return true;
        }

        // --- SnapshotStore: binds a persisted snapshot to this world's physics objects (Runtime/SnapshotStore.cs). ---

        internal static readonly Type ColliderStateType = typeof(ColliderState);

        internal int IdOfState(object state) => ((ColliderState)state).Id;

        internal object StateAt(int id) => _byId.TryGetValue(id, out ColliderState s) ? s
            : throw new KeyNotFoundException("no collider " + id + " in this world export");

        internal (List<b2Fixture> fixtures, b2Fixture[] pristine) FixturesAt(int id) { Settle(); return (_byId[id].Fixtures, _byId[id].Pristine); }

        internal bool IsPlayerCollider(int id) => _byId.TryGetValue(id, out ColliderState s) && s.Body == _player;

        internal IEnumerable<int> ChangedIdsOf(object snapshot) => ((PhysicsSnapshot)snapshot).Changed.Select(c => c.State.Id);

        /// <summary>Which collider a fixture belongs to and where in its fixture list (its load fixtures' if it is gone).</summary>
        internal (int owner, int index) LocateFixture(b2Fixture fixture)
        {
            Settle();
            if (!(fixture.UserData is ColliderState s)) return (-1, -1);
            int index = s.Fixtures.IndexOf(fixture);
            if (index < 0 && s.Pristine != null) index = Array.IndexOf(s.Pristine, fixture);
            return (s.Id, index);
        }

        internal void RebuildCollider(int id) { Changed(_byId[id]); }

        internal int RigidbodyIdOf(b2Body body)
        {
            if (ReferenceEquals(body, _ground)) return -1;
            if (body.UserData is BodyState s && ReferenceEquals(s.Body, body)) return s.Rigidbody.GetInstanceID();
            throw new NotSupportedException("a snapshot holds a physics body this world doesn't; not saved");
        }

        internal b2Body BodyAt(int rigidbodyId)
        {
            if (rigidbodyId == -1) return _ground;
            return _world.Objects.TryGetValue(rigidbodyId, out UnityEngine.Object o) && o is Rigidbody2D rb
                && _bodies.TryGetValue(rb, out BodyState s) ? s.Body
                : throw new KeyNotFoundException("no body " + rigidbodyId + " in this world export");
        }

        /// <summary>
        /// A restored snapshot's changed colliders that this world never rebuilt: line them up as Restore's loop would have
        /// (their fixtures as the snapshot has them, what this world loaded as the pristine ones).
        /// </summary>
        internal void AdoptChanged(object snapshot)
        {
            Settle();
            _b2.Renumber(((PhysicsSnapshot)snapshot).World);
            foreach (Built saved in ((PhysicsSnapshot)snapshot).Changed)
            {
                ColliderState state = saved.State;
                if (state.Tracked) continue;
                // It changed first in the snapshot's world; what this world loaded takes the pristine fixtures' place.
                state.Tracked = true;
                _changed.Add(state);
                state.Fixtures.Clear();
                state.Fixtures.AddRange(saved.Fixtures);
                state.FixturesAs = saved.Fixtures;
                state.BuiltScale = saved.Scale;
                if (state.Layer != saved.Layer || state.ContactMask != saved.Mask)
                {
                    state.Layer = saved.Layer;
                    state.ContactMask = saved.Mask;
                    if (state.Body != null) _moversDirty = true;
                }
                if ((state.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(state);
            }
            _changedCapture = null;
        }

        /// <summary>
        /// The physics state a step reads, bit for bit, as a digest that is the same in every process for the same state (the
        /// determinism check, Cli/Determinism.cs): the Box2D world (<see cref="b2World.StateDigest"/>, a fixture named by its
        /// collider, its place in the collider's fixtures and its shape), each collider's fixtures, layer, contact mask and
        /// built scale, the bodies' pending syncs, the touching pairs in order and the rebase flags.
        /// </summary>
        internal ulong StateDigest(System.Text.StringBuilder log = null)
        {
            Settle();
            var h = b2Digest.Start(log);
            h.Add((long)_b2.StateDigest(FixtureKey, log));
            foreach (ColliderState s in _colliderList)
            {
                h.Line("collider " + s.Id);
                h.Add(s.Fixtures.Count);
                foreach (b2Fixture f in s.Fixtures) h.Add(FixtureKey(f));
                h.Add(s.Layer); h.Add(s.ContactMask);
                h.Add(s.BuiltScale.x); h.Add(s.BuiltScale.y); h.Add(s.BuiltScale.z);
                h.Add(s.Reshaped); h.Add(s.Rescaled);
            }
            h.Line("syncs");
            foreach (BodyState b in _bodyList)
            {
                h.Add(b.TransformDirty); h.Add(b.ScaleDirty); h.Add(b.Shifted);
                Transform t = b.Rigidbody.transform;
                h.Add(t.m_Position.x); h.Add(t.m_Position.y); h.Add(t.m_Position.z);
                h.Add(t.m_Rotation.x); h.Add(t.m_Rotation.y); h.Add(t.m_Rotation.z); h.Add(t.m_Rotation.w);
                h.Add(t.m_LossyScale.x); h.Add(t.m_LossyScale.y); h.Add(t.m_LossyScale.z);
            }
            h.Line("pairs");
            h.Add(_pairsBefore.Count);
            foreach (Pair p in _pairsBefore) { h.Add(p.A.Id); h.Add(p.B.Id); h.Add(p.Trigger); h.Add(p.Overlapped); }
            var touching = new List<long>(_touching.Count);
            foreach (Pair p in _touching) touching.Add(((long)p.A.Id << 32) | (uint)p.B.Id);
            touching.Sort();
            h.Add(touching.Count);
            foreach (long p in touching) h.Add(p);
            h.Add(_worldMoved); h.Add(_staticsMoved);
            return h.Value;
        }

        static long FixtureKey(b2Fixture f)
        {
            if (!(f.UserData is ColliderState s)) return -1;
            return ((long)s.Id << 40) ^ ((long)(s.Fixtures.IndexOf(f) + 1) << 32) ^ (uint)FingerprintOf(f);
        }

        /// <summary>What a fixture's shape is: a restored fixture must have been built from the same geometry. Deterministic
        /// (a plain mix): a state file outlives the process that wrote it.</summary>
        public static int FingerprintOf(b2Fixture fixture)
        {
            ulong h = SnapshotStore.HashSeed;
            void MixF(float v) { MixL(BitConverter.SingleToInt32Bits(v)); }
            void MixL(long v) { h = SnapshotStore.Feed(h, v, 8); }
            void Shape(b2Vec2 v) { MixF(v.x); MixF(v.y); }
            switch (fixture.b2Shape)
            {
                case b2PolygonShape p:
                    MixL(1); MixF(p.m_radius); MixL(p.m_count);
                    for (int i = 0; i < p.m_count; i++) Shape(p.m_vertices[i]);
                    break;
                case b2CircleShape c: MixL(2); MixF(c.m_radius); Shape(c.m_p); break;
                case b2EdgeShape e:
                    MixL(3); MixF(e.m_radius); MixL(e.m_oneSided ? 1 : 0);
                    if (e.m_vertex0.HasValue) Shape(e.m_vertex0.Value);
                    if (e.m_vertex3.HasValue) Shape(e.m_vertex3.Value);
                    Shape(e.m_vertex1); Shape(e.m_vertex2);
                    break;
                case b2ChainShape ch:
                    MixL(4); MixF(ch.m_radius); MixL(ch.m_count);
                    for (int i = 0; i < ch.m_count; i++) Shape(ch.m_vertices[i]);
                    break;
                default: MixL(0); break;
            }
            return (int)(h ^ (h >> 32));
        }
    }

    static class Messages
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), System.Reflection.MethodInfo> Cache =
            new System.Collections.Concurrent.ConcurrentDictionary<(Type, string), System.Reflection.MethodInfo>();

        public static System.Reflection.MethodInfo Find(Type type, string name) => Cache.GetOrAdd((type, name), key =>
        {
            System.Reflection.MethodInfo found = null;
            for (Type t = key.Item1; t != null && found == null; t = t.BaseType)
                found = t.GetMethod(key.Item2, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
            return found;
        });
    }
}
