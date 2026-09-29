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
            public (MonoBehaviour script, Action<object> call)[][] Handlers;
            public bool Listens;
            public ColliderState Composite;
            public ColliderState[] Sources;
            public bool[] SourcesLiveAtLoad;
            public b2Fixture[] Pristine, Restoring;
            /// <summary>An array holding exactly what Fixtures holds now (null when none is known): Restore skips comparing it.</summary>
            public b2Fixture[] FixturesAs;
            /// <summary>Changed with its fixtures not built yet (PhysicsWorld.RebuildLater; built by Settle).</summary>
            public bool Unbuilt;
            /// <summary>Rebuilt since load, so it joins _changed and snapshots record its fixtures.</summary>
            public bool Tracked;
            public bool Rescaled;
            /// <summary>Rebuilt since the last step (PhysicsWorld.FindLapsedTouches).</summary>
            public bool Reshaped;
            public readonly List<int> GridSlots = new List<int>();
            public bool Regrid;
            // What its grid slots were built from: these fixtures (this many children) at this body pose.
            public b2Fixture[] GridFixtures = Array.Empty<b2Fixture>();
            public int GridChildren = -1;
            public b2Transform GridPose;
        }

        sealed class BodyState
        {
            public Rigidbody2D Rigidbody;
            public b2Body Body;
            public bool TransformDirty, ScaleDirty;
            /// <summary>Teleported (SetTransform) since the last step (PhysicsWorld.FindLapsedTouches).</summary>
            public bool Shifted;
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

            foreach (Rigidbody2D rb in world.Bodies)
            {
                if (!rb.gameObject.activeInHierarchy || !rb.simulated) continue;
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
                var state = new ColliderState { Data = data, Collider = c, Layer = c.gameObject.layer, Id = _colliders.Count };
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
            }
            _bodyList = new BodyState[_bodies.Count];
            _bodies.Values.CopyTo(_bodyList, 0);
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
            if (!Live(s)) return;
            float friction = s.Data.Friction, restitution = s.Data.Bounciness;
            if (s.Collider is BoxCollider2D box && s.Body != null && s.Body.Rigidbody.bodyType == RigidbodyType2D.Dynamic)
            {
                Vector3 scale = s.BuiltScale;
                float cx = box.m_Offset.x * scale.x, cy = box.m_Offset.y * scale.y;
                float hx = box.m_Size.x * MathF.Abs(scale.x) * 0.5f, hy = box.m_Size.y * MathF.Abs(scale.y) * 0.5f;
                var shape = new b2PolygonShape();
                shape.Set(new[] { new b2Vec2(cx + hx, cy - hy), new b2Vec2(cx + hx, cy + hy), new b2Vec2(cx - hx, cy + hy), new b2Vec2(cx - hx, cy - hy) }, 4);
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

        ShapeData[] Shapes(ColliderState s)
        {
            if (s.Body == null && _sim.Rebased) return MovedShapes(s);
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

        /// <summary>
        /// A body-less collider's shapes after a rebase moved its transform: Unity builds them again from the collider's own
        /// geometry and the transform (world-space vertices, float math); the export's shapes are the capture pose's.
        /// Boxes and capsules from their geometry; other shapes (the polygon camera zones) move with the origin.
        /// </summary>
        ShapeData[] MovedShapes(ColliderState s)
        {
            ShapeData[] captured = s.Data.Shapes;
            if (captured.Length == 0) return captured;
            Transform t = s.Collider.transform;
            Quaternion q = t.m_Rotation;
            float x2 = q.x * 2f, y2 = q.y * 2f, z2 = q.z * 2f;
            float xx = q.x * x2, yy = q.y * y2, zz = q.z * z2, xy = q.x * y2, wz = q.w * z2;
            float m00 = 1f - (yy + zz), m01 = xy - wz, m10 = xy + wz, m11 = 1f - (xx + zz);
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
            ScenePos by = _sim.FromCapture(new CapturePos(0f, 0f));
            return captured.Select(c => Moved(c, c.Vertices.Select(v => new Vector2(v.x + by.x, v.y + by.y)).ToArray())).ToArray();
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
        /// FixedUpdate after MoveOrigin read the spiked wall where it now is, not the geometry left at the old place).
        /// </summary>
        void SyncMovedWorld() { if (_worldMoved) SyncTransforms(); }

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

        public void ColliderChanged(Collider2D collider)
        {
            if (!Suspended && _colliders.TryGetValue(collider, out ColliderState s)) Changed(s);
        }

        public void ActiveChanged(GameObject gameObject)
        {
            if (!Suspended && _byObject.TryGetValue(gameObject, out var list))
                foreach (ColliderState s in list) Changed(s);
        }

        /// <summary>
        /// A GameObject changed layer (PlatformMover.MakeIntangible): Unity filters its contacts and queries by the new layer at
        /// once, so its colliders are rebuilt like any other collider change.
        /// </summary>
        public void LayerChanged(GameObject gameObject)
        {
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

        public void TransformChanged(Transform transform, bool pose)
        {
            Rigidbody2D rb = transform.m_GameObject.GetComponent<Rigidbody2D>();
            if (rb == null || !_bodies.TryGetValue(rb, out BodyState b)) return;
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
                for (int i = 0; i < _colliderList.Length; i++) { ColliderState s = _colliderList[i]; if (s.Body == null) Rebuild(s); }
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
        }

        public void Simulate(float deltaTime)
        {
            SyncTransforms();
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

        readonly List<(Pair pair, int message, Collision2D a, Collision2D b)> _sends = new List<(Pair, int, Collision2D, Collision2D)>();

        void DispatchCallbacks()
        {
            CollectTouching();
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
            for (b2Contact c = _b2.GetContactList(); c != null; c = c.GetNext())
            {
                if (!c.IsEnabled()) continue;
                var sa = (ColliderState)c.GetFixtureA().UserData;
                var sb = (ColliderState)c.GetFixtureB().UserData;
                if (!sa.Listens && !sb.Listens) continue;
                b2Fixture fa = c.GetFixtureA(), fb = c.GetFixtureB();
                bool trigger = fa.IsSensor() || fb.IsSensor();
                var pair = new Pair((ColliderState)fa.UserData, (ColliderState)fb.UserData, trigger);
                bool overlap = trigger && b2TestOverlap(fa.GetShape(), c.GetChildIndexA(), fb.GetShape(), c.GetChildIndexB(), fa.GetBody().GetTransform(), fb.GetBody().GetTransform(),
                    _proxyBufferA, _proxyBufferB);
                if (overlap) _overlapNext.Add(pair);
                bool touching = trigger
                    ? overlap || c.IsTouching() && !sa.Rescaled && !sb.Rescaled && !_touching.Contains(pair)
                    : c.IsTouching();
                if (!touching) continue;
                if (!_contactsNow.TryGetValue(pair, out List<b2Contact> contacts))
                {
                    contacts = _contactLists.Count > 0 ? _contactLists.Pop() : new List<b2Contact>(2);
                    _contactsNow.Add(pair, contacts);
                    _pairsNow.Add(pair);
                    _pairContacts.Add(contacts);
                    _touchingNext.Add(pair);
                }
                contacts.Add(c);
            }
            for (int i = 0; i < _pairContacts.Count; i++)
                if (_pairContacts[i].Count > 1) SortByTouchOrder(_pairContacts[i]);
        }

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
                _pairsBefore.Add(p.Trigger && _overlapNext.Contains(p) ? p.Overlapping() : p);
            }
        }

        /// <summary>
        /// The trigger pairs touching after the last step whose touch lapsed at this step's start: Box2D collides a step's
        /// contacts before it moves the bodies, so a pair that stops overlapping there (a transform write between the steps
        /// moved a body out, or a shape was rebuilt away from the other) ends its touch, and overlapping again after the
        /// step's move begins a new one: Enter, not Stay (turbo: a dash's interpolation pull-back out of a start gate the
        /// last step's move had just reached starts no timer, `startGate` running on Stay only). Shapes rebuilt for a new
        /// scale keep the older rule (their start-of-step touch doesn't count; see the facing-flip entry in docs/fidelity-log.md).
        /// </summary>
        void FindLapsedTouches()
        {
            _lapsed.Clear();
            for (int i = 0; i < _pairsBefore.Count; i++)
            {
                Pair p = _pairsBefore[i];
                if (!p.Trigger || p.A.Rescaled || p.B.Rescaled) continue;
                // Unmoved and unrebuilt, both shapes stand where the last step left them: its overlap test still holds.
                bool overlapping = Moved(p.A) || Moved(p.B) ? Overlap(p.A, p.B) : p.Overlapped;
                if (!overlapping) _lapsed.Add(p);
            }
        }

        static bool Moved(ColliderState s) => s.Reshaped || s.Body != null && s.Body.Shifted;

        // The two proxies' vertex buffers of this world's overlap and distance queries (b2DistanceProxy.Set).
        readonly b2Vec2[] _proxyBufferA = new b2Vec2[2], _proxyBufferB = new b2Vec2[2];

        bool Overlap(ColliderState a, ColliderState b)
        {
            foreach (b2Fixture fa in a.Fixtures)
                foreach (b2Fixture fb in b.Fixtures)
                    for (int ia = 0, na = fa.GetShape().GetChildCount(); ia < na; ia++)
                        for (int ib = 0, nb = fb.GetShape().GetChildCount(); ib < nb; ib++)
                            if (b2TestOverlap(fa.GetShape(), ia, fb.GetShape(), ib, fa.GetBody().GetTransform(), fb.GetBody().GetTransform(),
                                _proxyBufferA, _proxyBufferB)) return true;
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
        StaticShape[] _static = Array.Empty<StaticShape>();
        int _staticCount;
        readonly Stack<int> _freeSlots = new Stack<int>();
        readonly List<ColliderState> _regrid = new List<ColliderState>();
        List<int>[] _cells;
        int[] _seen = Array.Empty<int>();
        int _stamp, _cellsX, _cellsY;
        float _gridX, _gridY;
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
            if (_cells == null || s.Regrid) return;
            s.Regrid = true;
            _regrid.Add(s);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureGrid()
        {
            if (_cells == null || _regrid.Count != 0) UpdateGrid();
        }

        void UpdateGrid()
        {
            if (_cells == null) { BuildGrid(); return; }
            bool inBounds = true;
            foreach (ColliderState s in _regrid)
            {
                s.Regrid = false;
                if (inBounds) inBounds = Regrid(s);
            }
            _regrid.Clear();
            if (!inBounds) BuildGrid();
        }

        static b2AABB GridBox(b2Fixture f, int child)
        {
            f.GetShape().ComputeAABB(out b2AABB box, f.GetBody().GetTransform(), child);
            box.lowerBound -= new b2Vec2(1f, 1f);
            box.upperBound += new b2Vec2(1f, 1f);
            return box;
        }

        void BuildGrid()
        {
            foreach (ColliderState s in _colliders.Values) { s.GridSlots.Clear(); s.Regrid = false; }
            _regrid.Clear();
            _freeSlots.Clear();
            _staticCount = 0;
            bool first = true;
            float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;
            for (b2Body body = _b2.GetBodyList(); body != null; body = body.GetNext())
            {
                if (body.Type() != b2BodyType.Static) continue;
                for (b2Fixture f = body.GetFixtureList(); f != null; f = f.GetNext())
                    for (int c = 0; c < f.m_proxyCount; c++)
                    {
                        b2AABB box = GridBox(f, c);
                        if (first) { minX = box.lowerBound.x; minY = box.lowerBound.y; maxX = box.upperBound.x; maxY = box.upperBound.y; first = false; }
                        minX = MathF.Min(minX, box.lowerBound.x); minY = MathF.Min(minY, box.lowerBound.y);
                        maxX = MathF.Max(maxX, box.upperBound.x); maxY = MathF.Max(maxY, box.upperBound.y);
                    }
            }
            _gridX = minX; _gridY = minY;
            _cellsX = (int)((maxX - minX) / CellSize) + 1;
            _cellsY = (int)((maxY - minY) / CellSize) + 1;
            _cells = new List<int>[_cellsX * _cellsY];
            for (int i = 0; i < _cells.Length; i++) _cells[i] = new List<int>();
            for (b2Body body = _b2.GetBodyList(); body != null; body = body.GetNext())
            {
                if (body.Type() != b2BodyType.Static) continue;
                for (b2Fixture f = body.GetFixtureList(); f != null; f = f.GetNext())
                    for (int c = 0; c < f.m_proxyCount; c++)
                        AddShape(f, c, (ColliderState)f.UserData, GridBox(f, c));
            }
            foreach (ColliderState s in _colliders.Values)
                if ((s.Body?.Body ?? _ground).Type() == b2BodyType.Static) NoteGrid(s);
                else s.GridChildren = -1;
        }

        bool Regrid(ColliderState s)
        {
            bool isStatic = (s.Body?.Body ?? _ground).Type() == b2BodyType.Static;
            if (isStatic && Gridded(s)) return true;
            foreach (int i in s.GridSlots)
            {
                CellRange(_static[i].Box, out int x0, out int y0, out int x1, out int y1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        // A cell's order doesn't matter to its queries: swap-remove, no shift.
                        List<int> cell = _cells[y * _cellsX + x];
                        int at = cell.IndexOf(i);
                        if (at < 0) continue;
                        cell[at] = cell[cell.Count - 1];
                        cell.RemoveAt(cell.Count - 1);
                    }
                _static[i] = default;
                _freeSlots.Push(i);
            }
            s.GridSlots.Clear();
            if (!isStatic) { s.GridChildren = -1; return true; }
            float right = _gridX + _cellsX * CellSize, top = _gridY + _cellsY * CellSize;
            foreach (b2Fixture f in s.Fixtures)
                for (int c = 0; c < f.m_proxyCount; c++)
                {
                    b2AABB box = GridBox(f, c);
                    if (box.lowerBound.x < _gridX || box.lowerBound.y < _gridY || box.upperBound.x >= right || box.upperBound.y >= top) return false;
                    AddShape(f, c, s, box);
                }
            NoteGrid(s);
            return true;
        }

        static int Children(ColliderState s)
        {
            int children = 0;
            foreach (b2Fixture f in s.Fixtures) children += f.m_proxyCount;
            return children;
        }

        void NoteGrid(ColliderState s)
        {
            s.GridFixtures = s.Fixtures.ToArray();
            s.GridChildren = Children(s);
            s.GridPose = (s.Body?.Body ?? _ground).GetTransform();
        }

        /// <summary>
        /// Its slots already hold every child of its fixtures where it is now: the same fixtures (a fixture's shape never changes)
        /// at the same body pose. A restore marks the colliders of every static body ever moved (after a FloatingOrigin rebase:
        /// the big composites among them) and of every rebuilt collider for a regrid; most are unchanged.
        /// </summary>
        bool Gridded(ColliderState s)
        {
            if (s.Fixtures.Count != s.GridFixtures.Length || Children(s) != s.GridChildren) return false;
            for (int k = 0; k < s.GridFixtures.Length; k++)
                if (!ReferenceEquals(s.Fixtures[k], s.GridFixtures[k])) return false;
            b2Transform now = (s.Body?.Body ?? _ground).GetTransform();
            return now.p.x == s.GridPose.p.x && now.p.y == s.GridPose.p.y && now.q == s.GridPose.q;
        }

        void AddShape(b2Fixture f, int child, ColliderState s, in b2AABB box)
        {
            int i;
            if (_freeSlots.Count > 0) i = _freeSlots.Pop();
            else
            {
                if (_staticCount == _static.Length)
                {
                    int size = System.Math.Max(256, _static.Length * 2);
                    Array.Resize(ref _static, size);
                    Array.Resize(ref _seen, size);
                }
                i = _staticCount++;
            }
            _static[i] = new StaticShape { Fixture = f, Child = child, State = s, Box = box };
            s.GridSlots.Add(i);
            CellRange(box, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    _cells[y * _cellsX + x].Add(i);
        }

        const float InvCellSize = 1f / CellSize;

        // Floor-free, same cells: x / 64 == x * (1 / 64f) exactly (a power of two); below 0 a lower bound clamps to 0 and an
        // upper bound's range is empty either way, and at or above 0 (or NaN) truncation is Floor.
        void CellRange(in b2AABB box, out int x0, out int y0, out int x1, out int y1)
        {
            float lx = (box.lowerBound.x - _gridX) * InvCellSize, ly = (box.lowerBound.y - _gridY) * InvCellSize;
            float ux = (box.upperBound.x - _gridX) * InvCellSize, uy = (box.upperBound.y - _gridY) * InvCellSize;
            x0 = System.Math.Max(0, (int)lx);
            y0 = System.Math.Max(0, (int)ly);
            x1 = ux < 0f ? -1 : System.Math.Min(_cellsX - 1, (int)ux);
            y1 = uy < 0f ? -1 : System.Math.Min(_cellsY - 1, (int)uy);
        }

        // Accept's trigger half. A mover list holds only its own layer's colliders (every Layer write marks _movers dirty), so the
        // mover loops need only this.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Hittable(ColliderState s) => _queriesHitTriggers || !s.Collider.m_IsTrigger;

        ColliderState FindInside(b2Vec2 p, int layerMask)
        {
            EnsureGrid();
            ColliderState found = null;
            int count = 0;
            // CellRange of the point alone: its one cell, or none.
            float cx = (p.x - _gridX) * InvCellSize, cy = (p.y - _gridY) * InvCellSize;
            int x = (int)cx, y = (int)cy;
            if (!(cx < 0f) && !(cy < 0f) && x < _cellsX && y < _cellsY)
                foreach (int i in CollectionsMarshal.AsSpan(_cells[y * _cellsX + x]))
                {
                    ref StaticShape s = ref _static[i];
                    if (!Contains(s.Box, p) || !Accept(s.State, layerMask) || !s.Fixture.TestPoint(p)) continue;
                    found = s.State;
                    count++;
                }
            if (_moversDirty) BuildMovers();
            for (uint layers = (uint)layerMask & _moverLayers; layers != 0; layers &= layers - 1)
                foreach (var (f, s) in CollectionsMarshal.AsSpan(_movers[System.Numerics.BitOperations.TrailingZeroCount(layers)]))
                {
                    if (!Hittable(s)) continue;
                    for (int c = 0; c < f.m_proxyCount; c++)
                        if (f.TestPoint(p)) { found = s; count++; }
                }
            if (count <= 1) return found;
            _inside.Reset(p, layerMask);
            _b2.QueryAABB(_inside, new b2AABB { lowerBound = p, upperBound = p });
            return _inside.Hit;
        }

        bool FindRayHit(b2Vec2 p1, b2Vec2 p2, int layerMask)
        {
            EnsureGrid();
            _ray.Reset(layerMask);
            b2RayCastInput input;
            input.maxFraction = 1.0f;
            input.p1 = p1;
            input.p2 = p2;
            int count = 0;
            CellRange(new b2AABB { lowerBound = b2Vec2.Min(p1, p2), upperBound = b2Vec2.Max(p1, p2) }, out int x0, out int y0, out int x1, out int y1);
            _stamp++;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    foreach (int i in CollectionsMarshal.AsSpan(_cells[y * _cellsX + x]))
                    {
                        if (_seen[i] == _stamp) continue;
                        _seen[i] = _stamp;
                        ref StaticShape s = ref _static[i];
                        if (SegmentMisses(s.Box, p1, p2)) continue;
                        if (Accept(s.State, layerMask) && RayHit(s.Fixture, s.Child, s.State, input) && ++count > 1) return false;
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
            CellRange(box, out int x0, out int y0, out int x1, out int y1);
            _stamp++;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    foreach (int i in _cells[y * _cellsX + x])
                    {
                        if (_seen[i] == _stamp || _static[i].State != sb) continue;
                        _seen[i] = _stamp;
                        foreach (b2Fixture fa in sa.Fixtures)
                            for (int ca = 0; ca < fa.GetShape().GetChildCount(); ca++)
                                best = MathF.Min(best, ChildDistance(fa, ca, _static[i].Fixture, _static[i].Child));
                    }
            return best < NearbyRange - 1f;
        }

        public Vector2 GetPosition(Rigidbody2D body) { Settle(); b2Vec2 p = State(body).Body.GetPosition(); return new Vector2(p.x, p.y); }
        public void SetPosition(Rigidbody2D body, Vector2 position) { Settle(); BodyState s = State(body); Moving(s); b2Body b = s.Body; b.SetTransform(new b2Vec2(position.x, position.y), b.GetAngle()); }
        public float GetRotation(Rigidbody2D body) { Settle(); return State(body).Body.GetAngle() * (180f / MathF.PI); }
        public void SetRotation(Rigidbody2D body, float degrees) { Settle(); BodyState s = State(body); Moving(s); b2Body b = s.Body; b.SetTransform(b.GetPosition(), degrees * (MathF.PI / 180f)); }

        void Moving(BodyState body)
        {
            Shift(body);
            if (body.Body.Type() != b2BodyType.Static) return;
            if (!_movedStatic.Contains(body)) _movedStatic.Add(body);
            foreach (ColliderState s in body.Colliders) RegridLater(s);
        }
        public Vector2 GetVelocity(Rigidbody2D body) { Settle(); b2Vec2 v = State(body).Body.GetLinearVelocity(); return new Vector2(v.x, v.y); }
        public void SetVelocity(Rigidbody2D body, Vector2 velocity) { Settle(); State(body).Body.SetLinearVelocity(new b2Vec2(velocity.x, velocity.y)); }
        public float GetAngularVelocity(Rigidbody2D body) { Settle(); return State(body).Body.GetAngularVelocity() * (180f / MathF.PI); }
        public void SetAngularVelocity(Rigidbody2D body, float velocity) { Settle(); State(body).Body.SetAngularVelocity(velocity * (MathF.PI / 180f)); }

        BodyState State(Rigidbody2D body) { return (BodyState)body.BackendData; }

        readonly List<b2Contact> _bodyContacts = new List<b2Contact>();
        readonly List<ContactPoint2D> _bodyPoints = new List<ContactPoint2D>();

        public int GetContacts(Rigidbody2D body, ContactPoint2D[] results)
        {
            Settle();
            BodyState state = State(body);
            List<b2Contact> mine = _bodyContacts;
            mine.Clear();
            for (b2Contact c = _b2.GetContactList(); c != null; c = c.GetNext())
            {
                if (!c.IsTouching() || !c.IsEnabled()) continue;
                var a = (ColliderState)c.GetFixtureA().UserData;
                var b = (ColliderState)c.GetFixtureB().UserData;
                if (a.Body != state && b.Body != state) continue;
                int at = mine.Count;
                for (int i = 0; i < mine.Count; i++)
                {
                    var oa = (ColliderState)mine[i].GetFixtureA().UserData;
                    var ob = (ColliderState)mine[i].GetFixtureB().UserData;
                    if ((oa == a && ob == b || oa == b && ob == a) && mine[i].m_touchOrder > c.m_touchOrder) { at = i; break; }
                }
                mine.Insert(at, c);
            }
            int n = 0;
            for (int i = 0; i < mine.Count && n < results.Length; i++)
            {
                var a = (ColliderState)mine[i].GetFixtureA().UserData;
                var b = (ColliderState)mine[i].GetFixtureB().UserData;
                bool selfIsB = b.Body == state;
                _bodyPoints.Clear();
                AddPoints(_bodyPoints, selfIsB ? b : a, selfIsB ? a : b, mine[i]);
                foreach (ContactPoint2D p in _bodyPoints) if (n < results.Length) results[n++] = p;
            }
            return n;
        }

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
            public bool TransformDirty, ScaleDirty, WorldMoved;
            public Vector3[] BuiltScales;
            public Pair[] Touching;
            public (ColliderState state, b2Fixture[] fixtures)[] Changed;
        }

        // The rebuilt colliders' fixtures as a snapshot holds them; null once one is rebuilt again. Shared by every snapshot
        // taken until then (after a rebase that is every body-less collider, each capture's largest part).
        (ColliderState state, b2Fixture[] fixtures)[] _changedCapture = Array.Empty<(ColliderState, b2Fixture[])>();

        public object Capture()
        {
            Settle();
            if (_changedCapture == null)
            {
                _changedCapture = new (ColliderState, b2Fixture[])[_changed.Count];
                for (int i = 0; i < _changedCapture.Length; i++)
                {
                    ColliderState c = _changed[i];
                    c.FixturesAs = c.Fixtures.ToArray();
                    _changedCapture[i] = (c, c.FixturesAs);
                }
            }
            var builtScales = new Vector3[_player.Colliders.Count];
            for (int i = 0; i < builtScales.Length; i++) builtScales[i] = _player.Colliders[i].BuiltScale;
            return new PhysicsSnapshot { World = _b2.SaveState(), TransformDirty = _player.TransformDirty, ScaleDirty = _player.ScaleDirty, WorldMoved = _worldMoved,
                Touching = _pairsBefore.ToArray(), BuiltScales = builtScales, Changed = _changedCapture };
        }

        public void Restore(object snapshot)
        {
            Settle();
            var s = (PhysicsSnapshot)snapshot;
            _b2.LoadState(s.World);
            // Not provably unchanged: the restored bodies' fixture lists (and the layers below) may differ from the lists'.
            _moversDirty = true;
            foreach (ColliderState c in _player.Colliders) { c.Fixtures.Clear(); c.FixturesAs = null; }
            for (b2Fixture f = _player.Body.GetFixtureList(); f != null; f = f.GetNext()) ((ColliderState)f.UserData).Fixtures.Add(f);
            // _changed only grows: every collider the snapshot lists is in it, and the ones it doesn't list were pristine then.
            foreach (var (state, saved) in s.Changed) state.Restoring = saved;
            foreach (ColliderState c in _changed)
            {
                b2Fixture[] fixtures = c.Restoring ?? c.Pristine;
                c.Restoring = null;
                if (c.Layer != c.Collider.gameObject.m_Layer)
                {
                    c.Layer = c.Collider.gameObject.m_Layer;
                    c.ContactMask = ContactMask(_layerMasks, c.Collider);
                }
                // The same array restored again (or the one its fixtures were captured into): nothing to compare.
                if (ReferenceEquals(fixtures, c.FixturesAs)) continue;
                c.FixturesAs = fixtures;
                if (Holds(c.Fixtures, fixtures)) continue;
                c.Fixtures.Clear();
                c.Fixtures.AddRange(fixtures);
                if ((c.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(c);
            }
            // A capture after this must re-derive the changed set: every collider whose fixtures now differ from its
            // pristine ones, not the restored snapshot's own list.
            _changedCapture = null;
            foreach (BodyState b in _movedStatic)
                foreach (ColliderState c in b.Colliders) RegridLater(c);
            _player.TransformDirty = s.TransformDirty;
            _worldMoved = s.WorldMoved;
            _player.ScaleDirty = s.ScaleDirty;
            for (int i = 0; i < _player.Colliders.Count; i++) _player.Colliders[i].BuiltScale = s.BuiltScales[i];
            _touching.Clear();
            foreach (Pair p in s.Touching) _touching.Add(p);
            _pairsBefore.Clear();
            _pairsBefore.AddRange(s.Touching);
            ClearMoved();
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

        internal IEnumerable<int> ChangedIdsOf(object snapshot) => ((PhysicsSnapshot)snapshot).Changed.Select(c => c.state.Id);

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
            foreach (var pair in _bodies) if (ReferenceEquals(pair.Value.Body, body)) return pair.Key.GetInstanceID();
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
            foreach (var (state, saved) in ((PhysicsSnapshot)snapshot).Changed)
            {
                if (_changed.Contains(state)) continue;
                // It changed first in the snapshot's world; what this world loaded takes the pristine fixtures' place.
                state.Tracked = true;
                _changed.Add(state);
                state.Fixtures.Clear();
                state.Fixtures.AddRange(saved);
                state.FixturesAs = saved;
                if (state.Layer != state.Collider.gameObject.m_Layer)
                {
                    state.Layer = state.Collider.gameObject.m_Layer;
                    state.ContactMask = ContactMask(_layerMasks, state.Collider);
                    if (state.Body != null) _moversDirty = true;
                }
                if ((state.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(state);
            }
            _changedCapture = null;
        }

        /// <summary>What a fixture's shape is: a restored fixture must have been built from the same geometry. Deterministic
        /// (a plain mix): a state file outlives the process that wrote it.</summary>
        public static int FingerprintOf(b2Fixture fixture)
        {
            ulong h = 14695981039346656037UL;
            void MixF(float v) { MixL(BitConverter.SingleToInt32Bits(v)); }
            void MixL(long v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
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
        static readonly Dictionary<(Type, string), System.Reflection.MethodInfo> Cache = new Dictionary<(Type, string), System.Reflection.MethodInfo>();

        public static System.Reflection.MethodInfo Find(Type type, string name)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue((type, name), out var m)) return m;
                System.Reflection.MethodInfo found = null;
                for (Type t = type; t != null && found == null; t = t.BaseType)
                    found = t.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                return Cache[(type, name)] = found;
            }
        }
    }
}
