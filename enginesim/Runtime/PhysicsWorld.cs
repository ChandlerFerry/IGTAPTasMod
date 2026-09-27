using System;
using System.Collections.Generic;
using System.Linq;
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
            public b2Fixture[] Pristine;
            public bool Rescaled;
            public readonly List<int> GridSlots = new List<int>();
            public bool Regrid;
        }

        sealed class BodyState
        {
            public Rigidbody2D Rigidbody;
            public b2Body Body;
            public bool TransformDirty, ScaleDirty;
            public readonly List<ColliderState> Colliders = new List<ColliderState>();
        }

        readonly struct Pair : IEquatable<Pair>
        {
            public readonly ColliderState A, B;
            public readonly bool Trigger;
            public Pair(ColliderState a, ColliderState b, bool trigger)
            {
                if (a.Id <= b.Id) { A = a; B = b; } else { A = b; B = a; }
                Trigger = trigger;
            }
            public bool Equals(Pair o) { return ReferenceEquals(A, o.A) && ReferenceEquals(B, o.B); }
            public override bool Equals(object o) { return o is Pair p && Equals(p); }
            public override int GetHashCode() { return A.Id * 100003 + B.Id; }
        }

        readonly World _world;
        readonly Simulation _sim;
        readonly b2World _b2;
        readonly b2Body _ground;
        readonly Dictionary<Collider2D, ColliderState> _colliders = new Dictionary<Collider2D, ColliderState>();
        readonly Dictionary<Rigidbody2D, BodyState> _bodies = new Dictionary<Rigidbody2D, BodyState>();
        readonly Dictionary<GameObject, List<ColliderState>> _byObject = new Dictionary<GameObject, List<ColliderState>>();
        readonly int[] _layerMasks;
        readonly float _contactOffset;
        readonly int _velocityIterations, _positionIterations;
        readonly bool _queriesHitTriggers, _queriesStartInColliders;
        HashSet<Pair> _touching = new HashSet<Pair>(), _touchingNext = new HashSet<Pair>();
        readonly BodyState _player;
        readonly List<ColliderState> _changed = new List<ColliderState>();
        readonly List<BodyState> _movedStatic = new List<BodyState>();
        bool _loaded;
        internal bool Suspended;
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
                if (!_byObject.TryGetValue(c.gameObject, out var list)) _byObject[c.gameObject] = list = new List<ColliderState>();
                list.Add(state);
                state.Handlers = Handlers(CallbackScripts(c));
                state.Listens = state.Handlers.Any(h => h.Length > 0);
                Rebuild(state);
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
            if (_loaded && s.Body != _player && s.Pristine == null)
            {
                s.Pristine = s.Fixtures.ToArray();
                _changed.Add(s);
            }
            if ((s.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(s);
            else _movers = null;
            s.BuiltScale = s.Collider.transform.m_LossyScale;
            b2Body body = s.Body?.Body ?? _ground;
            foreach (b2Fixture f in s.Fixtures) body.DestroyFixture(f);
            s.Fixtures.Clear();
            s.Layer = s.Collider.gameObject.m_Layer;
            s.ContactMask = ContactMask(_layerMasks, s.Collider);
            if (!Live(s)) return;
            float friction = s.Data.Friction, restitution = s.Data.Bounciness;
            if (s.Collider is BoxCollider2D box && s.Body != null && s.Body.Rigidbody.bodyType == RigidbodyType2D.Dynamic)
            {
                Transform t = s.Collider.transform;
                Vector3 scale = t.m_LossyScale;
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
            Vector3 by = _sim.CurrentOrigin - _sim.Origin;
            return captured.Select(c => Moved(c, c.Vertices.Select(v => new Vector2(v.x + by.x, v.y + by.y)).ToArray())).ToArray();
        }

        static ShapeData Moved(ShapeData shape, params Vector2[] vertices)
        {
            return new ShapeData { Type = shape.Type, Radius = shape.Radius, Vertices = vertices, UseAdjacentStart = shape.UseAdjacentStart,
                UseAdjacentEnd = shape.UseAdjacentEnd, AdjacentStart = shape.AdjacentStart, AdjacentEnd = shape.AdjacentEnd };
        }

        /// <summary>A FloatingOrigin rebase moved the world's transforms (Simulation.MoveWorld).</summary>
        public void WorldMoved() { _worldMoved = true; }

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
            Rebuild(s);
            if (s.Composite != null) Rebuild(s.Composite);
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
            if (_worldMoved)
            {
                _worldMoved = false;
                foreach (BodyState b in _bodies.Values) b.TransformDirty = true;
                foreach (ColliderState s in _colliders.Values) if (s.Body == null) Rebuild(s);
            }
            foreach (BodyState b in _bodies.Values)
            {
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
            _b2.Step(deltaTime, _velocityIterations, _positionIterations);
            foreach (BodyState b in _bodies.Values)
            {
                if (b.Rigidbody.bodyType == RigidbodyType2D.Static) continue;
                b2Vec2 p = b.Body.GetPosition();
                Transform t = b.Rigidbody.transform;
                t.StoreWorldPosition(new Vector3(p.x, p.y, t.m_Position.z));
            }
            DispatchCallbacks();
            foreach (ColliderState s in _rescaled) s.Rescaled = false;
            _rescaled.Clear();
        }

        readonly List<Pair> _pairsNow = new List<Pair>();
        readonly Dictionary<Pair, List<b2Contact>> _contactsNow = new Dictionary<Pair, List<b2Contact>>();
        readonly Stack<List<b2Contact>> _contactLists = new Stack<List<b2Contact>>();
        List<Pair> _pairsBefore = new List<Pair>();
        readonly List<ColliderState> _rescaled = new List<ColliderState>();

        public int ContactCount { get { return _b2.GetContactCount(); } }
        public int TouchingPairs { get { return _pairsBefore.Count; } }
        public int BodyCount { get { return _b2.GetBodyCount(); } }

        readonly List<(Pair pair, int message, Collision2D a, Collision2D b)> _sends = new List<(Pair, int, Collision2D, Collision2D)>();

        void DispatchCallbacks()
        {
            CollectTouching();
            _sends.Clear();
            foreach (Pair p in _pairsBefore)
                if (!_touchingNext.Contains(p)) Queue(p, "Exit", null);
            foreach (Pair p in _pairsNow)
                Queue(p, _touching.Contains(p) ? "Stay" : "Enter", _contactsNow[p]);
            foreach (var (pair, message, a, b) in _sends) Send(pair, message, a, b);
            AdvanceTouching();
        }

        void CollectTouching()
        {
            foreach (List<b2Contact> list in _contactsNow.Values) { list.Clear(); _contactLists.Push(list); }
            _contactsNow.Clear();
            _pairsNow.Clear();
            _touchingNext.Clear();
            for (b2Contact c = _b2.GetContactList(); c != null; c = c.GetNext())
            {
                if (!c.IsEnabled()) continue;
                var sa = (ColliderState)c.GetFixtureA().UserData;
                var sb = (ColliderState)c.GetFixtureB().UserData;
                if (!sa.Listens && !sb.Listens) continue;
                b2Fixture fa = c.GetFixtureA(), fb = c.GetFixtureB();
                bool trigger = fa.IsSensor() || fb.IsSensor();
                var pair = new Pair((ColliderState)fa.UserData, (ColliderState)fb.UserData, trigger);
                bool touching = trigger
                    ? b2TestOverlap(fa.GetShape(), c.GetChildIndexA(), fb.GetShape(), c.GetChildIndexB(), fa.GetBody().GetTransform(), fb.GetBody().GetTransform())
                        || c.IsTouching() && !sa.Rescaled && !sb.Rescaled && !_touching.Contains(pair)
                    : c.IsTouching();
                if (!touching) continue;
                if (!_contactsNow.TryGetValue(pair, out List<b2Contact> contacts))
                {
                    contacts = _contactLists.Count > 0 ? _contactLists.Pop() : new List<b2Contact>(2);
                    _contactsNow[pair] = contacts;
                    _pairsNow.Add(pair);
                    _touchingNext.Add(pair);
                }
                int at = contacts.Count;
                while (at > 0 && contacts[at - 1].m_touchOrder > c.m_touchOrder) at--;
                contacts.Insert(at, c);
            }
        }

        void AdvanceTouching()
        {
            (_touching, _touchingNext) = (_touchingNext, _touching);
            _pairsBefore.Clear();
            _pairsBefore.AddRange(_pairsNow);
        }

        void Queue(Pair p, string phase, List<b2Contact> contacts)
        {
            int message = (p.Trigger ? 0 : 3) + (phase == "Enter" ? 0 : phase == "Stay" ? 1 : 2);
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

        static Collision2D MakeCollision(ColliderState self, ColliderState other, List<b2Contact> contacts)
        {
            var collision = new Collision2D { collider = other.Collider, otherCollider = self.Collider };
            if (contacts == null) return collision;
            foreach (b2Contact contact in contacts) AddPoints(collision.Contacts, self, other, contact);
            return collision;
        }

        static void AddPoints(List<ContactPoint2D> into, ColliderState self, ColliderState other, b2Contact contact)
        {
            bool selfIsB = contact.GetFixtureB().UserData == self;
            contact.GetWorldManifold(out b2WorldManifold wm);
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
            foreach (var (script, call) in target.Handlers[message])
            {
                _sim.Touch(script);
                call(argument);
            }
        }

        public RaycastHit2D Raycast(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
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
        List<(b2Fixture fixture, ColliderState state)>[] _movers;
        static readonly List<(b2Fixture, ColliderState)> NoMovers = new List<(b2Fixture, ColliderState)>();

        void BuildMovers()
        {
            _movers = new List<(b2Fixture, ColliderState)>[32];
            for (b2Body body = _b2.GetBodyList(); body != null; body = body.GetNext())
                if (body.Type() != b2BodyType.Static)
                    for (b2Fixture f = body.GetFixtureList(); f != null; f = f.GetNext())
                    {
                        var s = (ColliderState)f.UserData;
                        (_movers[s.Layer] ??= new List<(b2Fixture, ColliderState)>()).Add((f, s));
                    }
        }

        void RegridLater(ColliderState s)
        {
            if (_cells == null || s.Regrid) return;
            s.Regrid = true;
            _regrid.Add(s);
        }

        void EnsureGrid()
        {
            if (_cells == null) { BuildGrid(); return; }
            if (_regrid.Count == 0) return;
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
            if (!isStatic) return true;
            float right = _gridX + _cellsX * CellSize, top = _gridY + _cellsY * CellSize;
            foreach (b2Fixture f in s.Fixtures)
                for (int c = 0; c < f.m_proxyCount; c++)
                {
                    b2AABB box = GridBox(f, c);
                    if (box.lowerBound.x < _gridX || box.lowerBound.y < _gridY || box.upperBound.x >= right || box.upperBound.y >= top) return false;
                    AddShape(f, c, s, box);
                }
            return true;
        }

        /// <summary>
        /// Its slots already hold every child of its fixtures where it is now. A restore marks the colliders of every static body
        /// ever moved (after a FloatingOrigin rebase: all of them) and of every rebuilt collider for a regrid; most are unchanged.
        /// </summary>
        bool Gridded(ColliderState s)
        {
            int children = 0;
            foreach (b2Fixture f in s.Fixtures) children += f.m_proxyCount;
            if (children != s.GridSlots.Count) return false;
            foreach (int i in s.GridSlots)
            {
                ref StaticShape slot = ref _static[i];
                if (!s.Fixtures.Contains(slot.Fixture)) return false;
                b2AABB box = GridBox(slot.Fixture, slot.Child);
                if (box.lowerBound.x != slot.Box.lowerBound.x || box.lowerBound.y != slot.Box.lowerBound.y
                    || box.upperBound.x != slot.Box.upperBound.x || box.upperBound.y != slot.Box.upperBound.y) return false;
            }
            return true;
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

        void CellRange(in b2AABB box, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = System.Math.Max(0, (int)MathF.Floor((box.lowerBound.x - _gridX) / CellSize));
            y0 = System.Math.Max(0, (int)MathF.Floor((box.lowerBound.y - _gridY) / CellSize));
            x1 = System.Math.Min(_cellsX - 1, (int)MathF.Floor((box.upperBound.x - _gridX) / CellSize));
            y1 = System.Math.Min(_cellsY - 1, (int)MathF.Floor((box.upperBound.y - _gridY) / CellSize));
        }

        ColliderState FindInside(b2Vec2 p, int layerMask)
        {
            EnsureGrid();
            ColliderState found = null;
            int count = 0;
            CellRange(new b2AABB { lowerBound = p, upperBound = p }, out int x0, out int y0, out int x1, out int y1);
            if (x0 <= x1 && y0 <= y1)
                foreach (int i in _cells[y0 * _cellsX + x0])
                {
                    ref StaticShape s = ref _static[i];
                    if (!Accept(s.State, layerMask) || !s.Fixture.TestPoint(p)) continue;
                    found = s.State;
                    count++;
                }
            if (_movers == null) BuildMovers();
            for (uint layers = (uint)layerMask; layers != 0; layers &= layers - 1)
                foreach (var (f, s) in _movers[System.Numerics.BitOperations.TrailingZeroCount(layers)] ?? NoMovers)
                {
                    if (!Accept(s, layerMask)) continue;
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
                    foreach (int i in _cells[y * _cellsX + x])
                    {
                        if (_seen[i] == _stamp) continue;
                        _seen[i] = _stamp;
                        ref StaticShape s = ref _static[i];
                        if (Accept(s.State, layerMask) && RayHit(s.Fixture, s.Child, s.State, input) && ++count > 1) return false;
                    }
            if (_movers == null) BuildMovers();
            for (uint layers = (uint)layerMask; layers != 0; layers &= layers - 1)
                foreach (var (f, s) in _movers[System.Numerics.BitOperations.TrailingZeroCount(layers)] ?? NoMovers)
                {
                    if (!Accept(s, layerMask)) continue;
                    for (int c = 0; c < f.m_proxyCount; c++)
                        if (RayHit(f, c, s, input) && ++count > 1) return false;
                }
            return true;
        }

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

        bool Accept(ColliderState s, int layerMask)
        {
            return (layerMask & (1 << s.Layer)) != 0 && (_queriesHitTriggers || !s.Collider.m_IsTrigger);
        }

        public ColliderDistance2D Distance(Collider2D a, Collider2D b)
        {
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

        static float ChildDistance(b2Fixture fa, int ca, b2Fixture fb, int cb)
        {
            var input = new b2DistanceInput();
            input.proxyA.Set(fa.GetShape(), ca);
            input.proxyB.Set(fb.GetShape(), cb);
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

        public Vector2 GetPosition(Rigidbody2D body) { b2Vec2 p = State(body).Body.GetPosition(); return new Vector2(p.x, p.y); }
        public void SetPosition(Rigidbody2D body, Vector2 position) { BodyState s = State(body); Moving(s); b2Body b = s.Body; b.SetTransform(new b2Vec2(position.x, position.y), b.GetAngle()); }
        public float GetRotation(Rigidbody2D body) { return State(body).Body.GetAngle() * (180f / MathF.PI); }
        public void SetRotation(Rigidbody2D body, float degrees) { BodyState s = State(body); Moving(s); b2Body b = s.Body; b.SetTransform(b.GetPosition(), degrees * (MathF.PI / 180f)); }

        void Moving(BodyState body)
        {
            if (body.Body.Type() != b2BodyType.Static) return;
            if (!_movedStatic.Contains(body)) _movedStatic.Add(body);
            foreach (ColliderState s in body.Colliders) RegridLater(s);
        }
        public Vector2 GetVelocity(Rigidbody2D body) { b2Vec2 v = State(body).Body.GetLinearVelocity(); return new Vector2(v.x, v.y); }
        public void SetVelocity(Rigidbody2D body, Vector2 velocity) { State(body).Body.SetLinearVelocity(new b2Vec2(velocity.x, velocity.y)); }
        public float GetAngularVelocity(Rigidbody2D body) { return State(body).Body.GetAngularVelocity() * (180f / MathF.PI); }
        public void SetAngularVelocity(Rigidbody2D body, float velocity) { State(body).Body.SetAngularVelocity(velocity * (MathF.PI / 180f)); }

        BodyState State(Rigidbody2D body) { return (BodyState)body.BackendData; }

        readonly List<b2Contact> _bodyContacts = new List<b2Contact>();

        public int GetContacts(Rigidbody2D body, ContactPoint2D[] results)
        {
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
                Collision2D col = MakeCollision(selfIsB ? b : a, selfIsB ? a : b, new List<b2Contact> { mine[i] });
                foreach (ContactPoint2D p in col.Contacts) if (n < results.Length) results[n++] = p;
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
            var contacts = _b2.GetContactManager();
            contacts.FindNewContacts();
            contacts.Collide();
            CollectTouching();
            _touching.Clear();
            AdvanceTouching();
        }

        public (float px, float py, float angle, float vx, float vy, float w) PlayerBody()
        {
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

        public object Capture()
        {
            var changed = _changed.Count == 0 ? Array.Empty<(ColliderState, b2Fixture[])>() : new (ColliderState, b2Fixture[])[_changed.Count];
            for (int i = 0; i < changed.Length; i++) changed[i] = (_changed[i], _changed[i].Fixtures.ToArray());
            return new PhysicsSnapshot { World = _b2.SaveState(), TransformDirty = _player.TransformDirty, ScaleDirty = _player.ScaleDirty, WorldMoved = _worldMoved,
                Touching = _pairsBefore.ToArray(),
                BuiltScales = _player.Colliders.Select(c => c.BuiltScale).ToArray(), Changed = changed };
        }

        public void Restore(object snapshot)
        {
            var s = (PhysicsSnapshot)snapshot;
            _b2.LoadState(s.World);
            _movers = null;
            foreach (ColliderState c in _player.Colliders) c.Fixtures.Clear();
            for (b2Fixture f = _player.Body.GetFixtureList(); f != null; f = f.GetNext()) ((ColliderState)f.UserData).Fixtures.Add(f);
            foreach (ColliderState c in _changed)
            {
                b2Fixture[] fixtures = c.Pristine;
                foreach (var (state, saved) in s.Changed)
                    if (state == c) { fixtures = saved; break; }
                if (c.Layer != c.Collider.gameObject.m_Layer)
                {
                    c.Layer = c.Collider.gameObject.m_Layer;
                    c.ContactMask = ContactMask(_layerMasks, c.Collider);
                    if (c.Body != null) _movers = null;
                }
                if (c.Fixtures.SequenceEqual(fixtures)) continue;
                c.Fixtures.Clear();
                c.Fixtures.AddRange(fixtures);
                if ((c.Body?.Body ?? _ground).Type() == b2BodyType.Static) RegridLater(c);
            }
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
