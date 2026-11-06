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
            public Vector3 PristineScale;
            public int PristineLayer, PristineMask;
            public Vector3 LoadedPosition, LoadedScale;
            public Quaternion LoadedRotation;
            public bool Moved;
            public (MonoBehaviour script, Action<object> call)[][] Handlers;
            public bool Listens;
            public ColliderState Composite;
            public ColliderState[] Sources;
            public bool[] SourcesLiveAtLoad;
            public b2Fixture[] Pristine;
            public Built Restoring;
            public b2Fixture[] FixturesAs;
            public bool Unbuilt;
            public bool Tracked;
            public bool Rescaled;
            public bool Reshaped;
            public bool Regrid;
            public sbyte TouchKind = -1;
        }

        sealed class BodyState
        {
            public Rigidbody2D Rigidbody;
            public b2Body Body;
            public bool TransformDirty, ScaleDirty;
            public bool Shifted;
            public bool MovedStatic;
            public int Index;
            public readonly List<ColliderState> Colliders = new List<ColliderState>();
        }

        readonly struct Pair : IEquatable<Pair>
        {
            public readonly ColliderState A, B;
            public readonly bool Trigger;
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
        // _bodies/_colliders are never removed from, so these lists hold the order a foreach over .Values would walk.
        BodyState[] _bodyList = Array.Empty<BodyState>();
        ColliderState[] _colliderList = Array.Empty<ColliderState>();
        readonly int[] _layerMasks;
        readonly float _contactOffset;
        readonly int _velocityIterations, _positionIterations;
        readonly bool _queriesHitTriggers, _queriesStartInColliders;
        HashSet<Pair> _touching = new HashSet<Pair>(), _touchingNext = new HashSet<Pair>();
        // Pairs touched only at the step's start (Box2D Collide) enter and exit in the step and aren't touching after it.
        readonly HashSet<Pair> _startOnly = new HashSet<Pair>();
        readonly BodyState _player;
        readonly List<ColliderState> _changed = new List<ColliderState>();
        readonly List<BodyState> _movedStatic = new List<BodyState>();
        bool _loaded;
        bool _suspended;

        // Setting Suspended first builds waiting fixtures (Settle) from colliders as they are before the restore writes them.
        internal bool Suspended
        {
            get { return _suspended; }
            set { if (value) Settle(); _suspended = value; }
        }

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

            foreach (Rigidbody2D rb in world.Bodies)
            {
                if (!rb.simulated) continue;
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
                    enabled = rb.gameObject.activeInHierarchy || rb.bodyType == RigidbodyType2D.Static,
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

        void Rebuild(ColliderState s)
        {
            Settle();
            Reshape(s);
            BuildFixtures(s);
        }

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
            if (!Live(s)) return;
            // Body goes on before its fixtures, as Unity makes a body before the colliders it attaches (proxy order).
            if (s.Body != null) SyncEnabled(s.Body);
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

        ShapeData[] PathShapes(ColliderState s)
        {
            Collider2D c = s.Collider;
            if (s.Data.Paths == null || s.Data.Paths.Length == 0) return null;
            // shortcut: paths in the body's frame only for a composite on its body's object at offset 0 (every export's).
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
                shapes.Add(new ShapeData { Type = "Edges", Radius = radius, Vertices = loop, UseAdjacentStart = true,
                    AdjacentStart = loop[path.Length - 1], UseAdjacentEnd = true, AdjacentEnd = loop[1] });
            }
            return shapes.ToArray();
        }

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

        static bool OffLoaded(ColliderState s)
        {
            Transform t = s.Collider.transform;
            return !SameBits(t.m_Position, s.LoadedPosition) || !SameBits(t.m_LossyScale, s.LoadedScale) || !SameBits(t.m_Rotation, s.LoadedRotation);
        }

        // Unity rebuilds body-less colliders from geometry and transform in float math; the export's shapes are capture pose.
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
            // shortcut: a rebase moves every transform off its loaded pose, so only a script's move before any rebase is caught.
            if (!_sim.Rebased && OffLoaded(s) && _sim.Engine.IsRunning)
                _sim.Engine.Abort("Unmodeled physics: " + s.Collider.name + "'s " + s.Collider.GetType().Name + " moved by a script (only boxes and capsules are rebuilt)");
            ScenePos by = _sim.FromCapture(new CapturePos(0f, 0f));
            return captured.Select(c => Moved(c, c.Vertices.Select(v => new Vector2(v.x + by.x, v.y + by.y)).ToArray())).ToArray();
        }

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

        public void WorldMoved() { _worldMoved = true; }

        // Queries after FloatingOrigin moved the world see it moved; bodies take moved transforms only at the next step's sync.
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

        internal int Generation;

        public void ColliderChanged(Collider2D collider)
        {
            Generation++;
            if (!Suspended && _colliders.TryGetValue(collider, out ColliderState s)) Changed(s);
        }

        public void ActiveChanged(GameObject gameObject)
        {
            Rigidbody2D rb = gameObject.GetComponent<Rigidbody2D>();
            if (!Suspended && rb != null && _bodies.TryGetValue(rb, out BodyState b)) SyncEnabled(b);
            LayerChanged(gameObject);
        }

        // Unity has no body for an off object and remakes it at its transform's pose, at rest (velocity reset), when it comes on.
        void SyncEnabled(BodyState b)
        {
            b2Body body = b.Body;
            bool on = b.Rigidbody.gameObject.m_ActiveInHierarchy;
            if (on == body.IsEnabled() || body.Type() == b2BodyType.Static) return;
            if (on)
            {
                Transform t = b.Rigidbody.transform;
                body.SetTransform(new b2Vec2(t.m_Position.x, t.m_Position.y), t.m_Rotation.eulerAngles.z * (MathF.PI / 180f));
                body.SetLinearVelocity(b2Vec2.Zero);
                body.SetAngularVelocity(0f);
                body.SetAwake(true);
                b.TransformDirty = false;
            }
            body.SetEnabled(on);
            _moversDirty = true;
        }

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

        readonly List<ColliderState> _unbuilt = new List<ColliderState>();

        // Dynamic-body box builds at the next Settle in last-change order, matching Unity's fixture-list and proxy order.
        static bool BuildsLater(ColliderState s)
            => s.Collider is BoxCollider2D && s.Body != null && s.Body.Rigidbody.bodyType == RigidbodyType2D.Dynamic;

        void RebuildLater(ColliderState s)
        {
            Reshape(s);
            if (s.Unbuilt) _unbuilt.Remove(s);
            s.Unbuilt = true;
            _unbuilt.Add(s);
        }

        // Everything that reads or changes the physics world must call this first (syncs, steps, queries, captures, restores).
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

        // Unity's Physics2DEarlyUpdate: script-moved body-less colliders sit at their transforms before FixedUpdate and Update.
        public void FrameStart()
        {
            Settle();
            if (_movedByScript.Count != 0) SyncMoved();
        }

        // Rebuild in collider id order: the order of the writes depends on what ran before a restore.
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
            FindLapsedTouches();
            _b2.Step(deltaTime, _velocityIterations, _positionIterations);
            for (int bi = 0; bi < _bodyList.Length; bi++)
            {
                BodyState b = _bodyList[bi];
                if (b.Rigidbody.bodyType == RigidbodyType2D.Static || !b.Body.IsEnabled()) continue;
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
        readonly List<List<b2Contact>> _pairContacts = new List<List<b2Contact>>();
        readonly Stack<List<b2Contact>> _contactLists = new Stack<List<b2Contact>>();
        List<Pair> _pairsBefore = new List<Pair>();
        readonly List<ColliderState> _rescaled = new List<ColliderState>();
        readonly HashSet<Pair> _overlapNext = new HashSet<Pair>(), _lapsed = new HashSet<Pair>();
        readonly List<BodyState> _shifted = new List<BodyState>();
        readonly List<ColliderState> _reshaped = new List<ColliderState>();

        public int ContactCount { get { Settle(); return _b2.GetContactCount(); } }
        public int TouchingPairs { get { return _pairsBefore.Count; } }
        public int BodyCount { get { return _b2.GetBodyCount(); } }

        public struct PlayerTouches
        {
            public bool Spike;
            public float Overhang, Slope;
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
                    // Box2D's normal points from fixture A to B; the player needs the other surface's normal, toward itself.
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
                // A touch only at the step's start enters and exits in the step; the next overlap enters again (1x buys on the Stay).
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

        readonly struct TouchOrdered
        {
            public readonly long Order;
            public readonly int Walk;
            public readonly b2Contact Contact;
            public TouchOrdered(long order, int walk, b2Contact contact) { Order = order; Walk = walk; Contact = contact; }
        }

        static readonly Comparison<TouchOrdered> ByTouchOrder = (a, b) => a.Order != b.Order ? (a.Order < b.Order ? -1 : 1) : a.Walk - b.Walk;
        TouchOrdered[] _touchOrdered = new TouchOrdered[8];

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

        // Box2D collides a step's contacts before moving bodies and destroys those whose proxies no longer overlap there.
        void FindLapsedTouches()
        {
            _lapsed.Clear();
            for (int i = 0; i < _pairsBefore.Count; i++)
            {
                Pair p = _pairsBefore[i];
                if (!p.Trigger || p.A.Rescaled || p.B.Rescaled || OnStaticBody(p.A) || OnStaticBody(p.B)) continue;
                bool overlapping = Moved(p.A) || Moved(p.B) ? ProxiesOverlap(p.A, p.B) : p.Overlapped;
                if (!overlapping) _lapsed.Add(p);
            }
        }

        static bool OnStaticBody(ColliderState s) => s.Body == null || s.Body.Body.Type() == b2BodyType.Static;

        static bool Moved(ColliderState s) => s.Reshaped || s.Body != null && s.Body.Shifted;

        readonly b2Vec2[] _proxyBufferA = new b2Vec2[2], _proxyBufferB = new b2Vec2[2];

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

        public bool PlayerOverlaps(Collider2D collider, Vector2 at)
        {
            Settle();
            SyncMovedWorld();
            if (!_colliders.TryGetValue(collider, out ColliderState s)) return false;
            b2Transform xfPlayer = _player.Body.GetTransform();
            xfPlayer.p = new b2Vec2(at.x, at.y);
            foreach (ColliderState player in _player.Colliders)
            {
                if (!CanTouch(player.Collider, collider)) continue;
                foreach (b2Fixture fp in player.Fixtures)
                {
                    if (fp.m_proxyCount <= 0) continue;
                    foreach (b2Fixture f in s.Fixtures)
                    {
                        if (f.m_proxyCount <= 0) continue;
                        b2Transform xf = f.GetBody().GetTransform();
                        for (int ip = 0; ip < fp.GetShape().GetChildCount(); ip++)
                            for (int i = 0; i < f.GetShape().GetChildCount(); i++)
                                if (b2TestOverlap(fp.GetShape(), ip, f.GetShape(), i, xfPlayer, xf, _proxyBufferA, _proxyBufferB)) return true;
                    }
                }
            }
            return false;
        }

        readonly InsideQuery _inside;
        readonly ClosestRay _ray;

        const float CellSize = 64f;
        struct StaticShape { public b2Fixture Fixture; public int Child; public ColliderState State; public b2AABB Box; }
        readonly List<ColliderState> _regrid = new List<ColliderState>();
        int[] _seen = Array.Empty<int>();
        int _stamp;

        sealed class Grid
        {
            public StaticShape[] Static = Array.Empty<StaticShape>();
            public int StaticCount, Dead;
            public int[] CellStart = Array.Empty<int>(), CellFill = Array.Empty<int>(), CellSlots = Array.Empty<int>();
            public List<int>[] Extra = Array.Empty<List<int>>();
            public readonly List<int> ExtraUsed = new List<int>();
            public float X, Y;
            public int CellsX, CellsY;
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
        readonly List<Grid> _spareGrids = new List<Grid>();
        const int SpareGrids = 2;
        const int LayOutAbove = 1024;

        readonly List<(b2Fixture fixture, ColliderState state)>[] _movers = new List<(b2Fixture, ColliderState)>[32];
        bool _moversDirty = true;
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
            if (stale > LayOutAbove || outside)
            {
                foreach (ColliderState s in _regrid) s.Regrid = false;
                _regrid.Clear();
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
            if (g.Dead > g.StaticCount / 2) LayOut(g);
        }

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

        static void Retarget(Grid g, ColliderState s, b2Fixture[] held)
        {
            foreach (int i in g.Slots[s.Id])
                g.Static[i].Fixture = s.Fixtures[Array.IndexOf(held, g.Static[i].Fixture)];
            g.Fixtures[s.Id] = s.Fixtures.ToArray();
        }

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

        static void CellRange(Grid g, in b2AABB box, out int x0, out int y0, out int x1, out int y1)
        {
            float lx = (box.lowerBound.x - g.X) * InvCellSize, ly = (box.lowerBound.y - g.Y) * InvCellSize;
            float ux = (box.upperBound.x - g.X) * InvCellSize, uy = (box.upperBound.y - g.Y) * InvCellSize;
            x0 = System.Math.Max(0, (int)lx);
            y0 = System.Math.Max(0, (int)ly);
            x1 = ux < 0f ? -1 : System.Math.Min(g.CellsX - 1, (int)ux);
            y1 = uy < 0f ? -1 : System.Math.Min(g.CellsY - 1, (int)uy);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Hittable(ColliderState s) => _queriesHitTriggers || !s.Collider.m_IsTrigger;

        ColliderState FindInside(b2Vec2 p, int layerMask)
        {
            EnsureGrid();
            Grid g = _grid;
            ColliderState found = null;
            int count = 0;
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

        static bool Contains(in b2AABB box, b2Vec2 p)
        {
            return p.x >= box.lowerBound.x && p.x <= box.upperBound.x && p.y >= box.lowerBound.y && p.y <= box.upperBound.y;
        }

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
            // Unity's Collider2D.Distance is negative for overlaps; GJK stops at 0, which hid Movement's squish check offline.
            float separation = MathF.Max(MaxSeparation(input.proxyA, input.transformA, input.proxyB, input.transformB),
                MaxSeparation(input.proxyB, input.transformB, input.proxyA, input.transformA));
            return float.IsNegativeInfinity(separation) ? output.distance : MathF.Min(0f, separation - input.proxyA._radius - input.proxyB._radius);
        }

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

        public bool PlayerTransformDirty { get { return _player.TransformDirty; } }

        sealed class PhysicsSnapshot
        {
            public object World;
            public bool WorldMoved, StaticsMoved;
            public (Vector3 scale, int layer, int mask)[] Player;
            public Pair[] Touching;
            public Built[] Changed;
            public int[] Bodies, Colliders;
            public Pose[] Poses;
        }

        internal struct Pose
        {
            public Vector3 Position, LocalPosition, LocalScale, LossyScale;
            public Quaternion Rotation, LocalRotation;

            public Pose(Transform t)
            {
                Position = t.m_Position; LocalPosition = t.m_LocalPosition; LocalScale = t.m_LocalScale; LossyScale = t.m_LossyScale;
                Rotation = t.m_Rotation; LocalRotation = t.m_LocalRotation;
            }

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

        Built[] _changedCapture = Array.Empty<Built>();

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
            _moversDirty = true;
            foreach (ColliderState c in _player.Colliders) { c.Fixtures.Clear(); c.FixturesAs = null; }
            for (b2Fixture f = _player.Body.GetFixtureList(); f != null; f = f.GetNext()) ((ColliderState)f.UserData).Fixtures.Add(f);
            foreach (Built saved in s.Changed) saved.State.Restoring = saved;
            foreach (ColliderState c in _changed)
            {
                Built saved = c.Restoring;
                c.Restoring = null;
                b2Fixture[] fixtures = saved?.Fixtures ?? c.Pristine;
                c.BuiltScale = saved?.Scale ?? c.PristineScale;
                c.Layer = saved?.Layer ?? c.PristineLayer;
                c.ContactMask = saved?.Mask ?? c.PristineMask;
                if (ReferenceEquals(fixtures, c.FixturesAs)) continue;
                c.FixturesAs = fixtures;
                if (Holds(c.Fixtures, fixtures)) continue;
                c.Fixtures.Clear();
                c.Fixtures.AddRange(fixtures);
                if ((c.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(c);
            }
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

        internal static readonly Type ColliderStateType = typeof(ColliderState);

        internal int IdOfState(object state) => ((ColliderState)state).Id;

        internal object StateAt(int id) => _byId.TryGetValue(id, out ColliderState s) ? s
            : throw new KeyNotFoundException("no collider " + id + " in this world export");

        internal (List<b2Fixture> fixtures, b2Fixture[] pristine) FixturesAt(int id) { Settle(); return (_byId[id].Fixtures, _byId[id].Pristine); }

        internal bool IsPlayerCollider(int id) => _byId.TryGetValue(id, out ColliderState s) && s.Body == _player;

        internal IEnumerable<int> ChangedIdsOf(object snapshot) => ((PhysicsSnapshot)snapshot).Changed.Select(c => c.State.Id);

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

        internal void AdoptChanged(object snapshot)
        {
            Settle();
            _b2.Renumber(((PhysicsSnapshot)snapshot).World);
            foreach (Built saved in ((PhysicsSnapshot)snapshot).Changed)
            {
                ColliderState state = saved.State;
                if (state.Tracked) continue;
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

        // Deterministic plain mix: a state file outlives the process that wrote it.
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
