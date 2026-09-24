using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Box2D;
using static Box2D.b2Collision;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // Unity's Physics2D on the Box2D port: colliders become fixtures built from the shapes Unity exported
    // (Collider2D.GetShapes), static colliders sit on one ground body at the origin (their vertices are in world
    // space, as in Unity), and after each step transforms follow bodies and the contact callbacks are sent.
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
        }

        sealed class BodyState
        {
            public Rigidbody2D Rigidbody;
            public b2Body Body;
            public bool TransformDirty;
            public readonly List<ColliderState> Colliders = new List<ColliderState>();
        }

        // A touching collider pair, independent of which fixture Box2D made A (A has the lower collider id).
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
            _b2.SetContinuousPhysics(Environment.GetEnvironmentVariable("ES_NOTOI") == null);
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
        }

        float Setting(string name, float fallback)
        {
            return _world.Physics2DSettings.TryGetValue(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : fallback;
        }

        bool SettingBool(string name, bool fallback)
        {
            return _world.Physics2DSettings.TryGetValue(name, out JsonElement e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) ? e.GetBoolean() : fallback;
        }

        // Unity sends a collider's callbacks to the scripts on its GameObject and, if different, its Rigidbody2D's.
        static MonoBehaviour[] CallbackScripts(Collider2D c)
        {
            var scripts = c.gameObject.GetComponents<MonoBehaviour>().ToList();
            Rigidbody2D rb = c.attachedRigidbody;
            if (rb != null && rb.gameObject != c.gameObject) scripts.AddRange(rb.gameObject.GetComponents<MonoBehaviour>());
            return scripts.ToArray();
        }

        // Unity's per-collider contact mask: the layer matrix row, plus include, minus exclude layers.
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

        // (Re)creates a collider's fixtures from its current properties.
        void Rebuild(ColliderState s)
        {
            s.BuiltScale = s.Collider.transform.m_LossyScale;
            b2Body body = s.Body?.Body ?? _ground;
            foreach (b2Fixture f in s.Fixtures) body.DestroyFixture(f);
            s.Fixtures.Clear();
            s.ContactMask = ContactMask(_layerMasks, s.Collider);
            if (!Live(s)) return;
            float friction = s.Data.Friction, restitution = s.Data.Bounciness;
            if (s.Collider is BoxCollider2D box && s.Body != null && s.Body.Rigidbody.bodyType == RigidbodyType2D.Dynamic)
            {
                // The player's boxes change size, offset and facing (scale) at run time; Unity rebuilds them from these.
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
            foreach (ShapeData shape in s.Data.Shapes)
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
                        // A capsule is a segment with a radius; Box2D v2's edge shape carries exactly that.
                        var edge = new b2EdgeShape();
                        edge.SetTwoSided(new b2Vec2(shape.Vertices[0].x, shape.Vertices[0].y), new b2Vec2(shape.Vertices[1].x, shape.Vertices[1].y));
                        edge.m_radius = shape.Radius;
                        AddFixture(s, body, edge, friction, restitution);
                        break;
                    }
                }
            }
        }

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

        // Unity recreates a collider's shapes as soon as one of its properties changes.
        public void ColliderChanged(Collider2D collider)
        {
            if (_colliders.TryGetValue(collider, out ColliderState s)) Rebuild(s);
        }

        public void ActiveChanged(GameObject gameObject)
        {
            if (_byObject.TryGetValue(gameObject, out var list))
                foreach (ColliderState s in list) Rebuild(s);
        }

        public void TransformChanged(Transform transform)
        {
            Rigidbody2D rb = transform.m_GameObject.GetComponent<Rigidbody2D>();
            if (rb == null || !_bodies.TryGetValue(rb, out BodyState b)) return;
            b.TransformDirty = true;
        }

        // Unity syncs a moved Transform into its body (rebuilding shapes if its scale changed) when the simulation
        // steps or when asked explicitly.
        public void SyncTransforms()
        {
            foreach (BodyState b in _bodies.Values)
            {
                if (!b.TransformDirty) continue;
                b.TransformDirty = false;
                Transform t = b.Rigidbody.transform;
                b.Body.SetTransform(new b2Vec2(t.m_Position.x, t.m_Position.y), t.m_Rotation.eulerAngles.z * (MathF.PI / 180f));
                foreach (ColliderState s in b.Colliders)
                    if (s.BuiltScale != t.m_LossyScale) Rebuild(s);
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
                t.m_Position = new Vector3(p.x, p.y, t.m_Position.z);
                t.m_LocalPosition = t.m_Parent == null ? t.m_Position : t.m_Position - t.m_Parent.m_Position;
            }
            DispatchCallbacks();
        }

        // Unity sends Enter on the first step a collider pair touches, Stay on later steps while it does, Exit when it
        // stops. Collisions use Box2D's touching flags (computed at the start of the step); triggers are overlap tests
        // at the positions after the step (turbo traces: a start gate starts the timer on the step the player's box
        // first overlaps it, one step before the sensor contact's own touching flag would). Pairs keep Box2D's
        // contact-list order so callbacks run in a deterministic order.
        readonly List<Pair> _pairsNow = new List<Pair>();
        readonly Dictionary<Pair, List<b2Contact>> _contactsNow = new Dictionary<Pair, List<b2Contact>>();
        readonly Stack<List<b2Contact>> _contactLists = new Stack<List<b2Contact>>();
        List<Pair> _pairsBefore = new List<Pair>();

        public int ContactCount { get { return _b2.GetContactCount(); } }
        public int TouchingPairs { get { return _pairsBefore.Count; } }
        public int BodyCount { get { return _b2.GetBodyCount(); } }

        void DispatchCallbacks()
        {
            CollectTouching();
            foreach (Pair p in _pairsBefore)
                if (!_touchingNext.Contains(p)) Send(p, "Exit", null);
            foreach (Pair p in _pairsNow)
                Send(p, _touching.Contains(p) ? "Stay" : "Enter", _contactsNow[p]);
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
                // A pair nobody listens to has no observable callbacks.
                var sa = (ColliderState)c.GetFixtureA().UserData;
                var sb = (ColliderState)c.GetFixtureB().UserData;
                if (!sa.Listens && !sb.Listens) continue;
                b2Fixture fa = c.GetFixtureA(), fb = c.GetFixtureB();
                bool trigger = fa.IsSensor() || fb.IsSensor();
                bool touching = trigger
                    ? b2TestOverlap(fa.GetShape(), c.GetChildIndexA(), fb.GetShape(), c.GetChildIndexB(), fa.GetBody().GetTransform(), fb.GetBody().GetTransform())
                    : c.IsTouching();
                if (!touching) continue;
                var pair = new Pair((ColliderState)fa.UserData, (ColliderState)fb.UserData, trigger);
                if (!_contactsNow.TryGetValue(pair, out List<b2Contact> contacts))
                {
                    contacts = _contactLists.Count > 0 ? _contactLists.Pop() : new List<b2Contact>(2);
                    _contactsNow[pair] = contacts;
                    _pairsNow.Add(pair);
                    _touchingNext.Add(pair);
                }
                contacts.Add(c);
            }
        }

        void AdvanceTouching()
        {
            (_touching, _touchingNext) = (_touchingNext, _touching);
            _pairsBefore.Clear();
            _pairsBefore.AddRange(_pairsNow);
        }

        void Send(Pair p, string phase, List<b2Contact> contacts)
        {
            int message = (p.Trigger ? 0 : 3) + (phase == "Enter" ? 0 : phase == "Stay" ? 1 : 2);
            if (p.Trigger)
            {
                Call(p.A, message, p.B.Collider);
                Call(p.B, message, p.A.Collider);
                return;
            }
            if (p.A.Handlers[message].Length > 0) Call(p.A, message, MakeCollision(p.A, p.B, contacts));
            if (p.B.Handlers[message].Length > 0) Call(p.B, message, MakeCollision(p.B, p.A, contacts));
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
            // Box2D's normal points from A to B; Unity reports it pointing from the other collider to this one.
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

        // Unity sends these messages to every script on the collider's object (and its body's), enabled or not.
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
                    // Methods emptied by enginesim/cuts.txt are just `ret`: nothing to call.
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
                _inside.Reset(p1, layerMask);
                _b2.QueryAABB(_inside, new b2AABB { lowerBound = p1, upperBound = p1 });
                if (_inside.Hit != null) return new RaycastHit2D(_inside.Hit.Collider, origin, -dir, 0f, 0f);
            }
            _ray.Reset(layerMask);
            _b2.RayCast(_ray, p1, p2);
            if (_ray.Hit == null) return default;
            return new RaycastHit2D(_ray.Hit.Collider, new Vector2(_ray.Point.x, _ray.Point.y), new Vector2(_ray.Normal.x, _ray.Normal.y),
                _ray.Fraction * distance, _ray.Fraction);
        }

        readonly InsideQuery _inside;
        readonly ClosestRay _ray;

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

        // Closest hit, as Box2D's RayCast reports it (clipping to each accepted fraction).
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
            float best = float.PositiveInfinity;
            foreach (b2Fixture fa in sa.Fixtures)
                foreach (b2Fixture fb in sb.Fixtures)
                    for (int ca = 0; ca < fa.GetShape().GetChildCount(); ca++)
                        for (int cb = 0; cb < fb.GetShape().GetChildCount(); cb++)
                        {
                            var input = new b2DistanceInput();
                            input.proxyA.Set(fa.GetShape(), ca);
                            input.proxyB.Set(fb.GetShape(), cb);
                            input.transformA = fa.GetBody().GetTransform();
                            input.transformB = fb.GetBody().GetTransform();
                            input.useRadii = true;
                            var cache = new b2SimplexCache();
                            b2Distance(out b2DistanceOutput output, ref cache, input);
                            best = MathF.Min(best, output.distance);
                        }
            return new ColliderDistance2D { distance = best, isValid = true };
        }

        public Vector2 GetPosition(Rigidbody2D body) { b2Vec2 p = State(body).Body.GetPosition(); return new Vector2(p.x, p.y); }
        public void SetPosition(Rigidbody2D body, Vector2 position) { b2Body b = State(body).Body; b.SetTransform(new b2Vec2(position.x, position.y), b.GetAngle()); }
        public float GetRotation(Rigidbody2D body) { return State(body).Body.GetAngle() * (180f / MathF.PI); }
        public void SetRotation(Rigidbody2D body, float degrees) { b2Body b = State(body).Body; b.SetTransform(b.GetPosition(), degrees * (MathF.PI / 180f)); }
        public Vector2 GetVelocity(Rigidbody2D body) { b2Vec2 v = State(body).Body.GetLinearVelocity(); return new Vector2(v.x, v.y); }
        public void SetVelocity(Rigidbody2D body, Vector2 velocity) { State(body).Body.SetLinearVelocity(new b2Vec2(velocity.x, velocity.y)); }
        public float GetAngularVelocity(Rigidbody2D body) { return State(body).Body.GetAngularVelocity() * (180f / MathF.PI); }
        public void SetAngularVelocity(Rigidbody2D body, float velocity) { State(body).Body.SetAngularVelocity(velocity * (MathF.PI / 180f)); }

        BodyState State(Rigidbody2D body) { return (BodyState)body.BackendData; }

        public int GetContacts(Rigidbody2D body, ContactPoint2D[] results)
        {
            int n = 0;
            BodyState state = State(body);
            for (b2Contact c = _b2.GetContactList(); c != null && n < results.Length; c = c.GetNext())
            {
                if (!c.IsTouching() || !c.IsEnabled()) continue;
                var a = (ColliderState)c.GetFixtureA().UserData;
                var b = (ColliderState)c.GetFixtureB().UserData;
                if (a.Body != state && b.Body != state) continue;
                bool selfIsB = b.Body == state;
                Collision2D col = MakeCollision(selfIsB ? b : a, selfIsB ? a : b, new List<b2Contact> { c });
                foreach (ContactPoint2D p in col.Contacts) if (n < results.Length) results[n++] = p;
            }
            return n;
        }

        // Diagnostics: every live contact with its fixtures, touching flag and manifold.
        public IEnumerable<string> DescribeContacts()
        {
            for (b2Contact c = _b2.GetContactList(); c != null; c = c.GetNext())
            {
                var a = (ColliderState)c.GetFixtureA().UserData;
                var b = (ColliderState)c.GetFixtureB().UserData;
                c.GetWorldManifold(out b2WorldManifold wm);
                b2Manifold m = c.GetManifold();
                yield return $"{a.Collider.name}#{a.Collider.GetInstanceID()} / {b.Collider.name}#{b.Collider.GetInstanceID()} touching={c.IsTouching()} "
                    + $"points={m.pointCount} normal=({wm.normal.x:R},{wm.normal.y:R}) sep=({wm.separations[0]:R},{wm.separations[1]:R}) "
                    + $"p0=({wm.points[0].x:R},{wm.points[0].y:R}) imp=({(m.pointCount > 0 ? m.points[0].normalImpulse : 0):R})";
            }
        }

        // The player's body at the start state (the transform is already placed).
        public void ResetPlayer(Vector2 position, Vector2 velocity, float rotation, float angularVelocity)
        {
            SyncTransforms();
            _player.Body.SetTransform(new b2Vec2(position.x, position.y), rotation * (MathF.PI / 180f));
            _player.Body.SetLinearVelocity(new b2Vec2(velocity.x, velocity.y));
            _player.Body.SetAngularVelocity(angularVelocity * (MathF.PI / 180f));
            _player.TransformDirty = false;
            // The live game had been stepping before this state: its contacts exist and have been reported (the
            // player's collidingBodies already lists what it rests on). Create and evaluate them now, and count them
            // as already touching, so the first step reports Stay rather than a second Enter.
            var contacts = _b2.GetContactManager();
            contacts.FindNewContacts();
            contacts.Collide();
            CollectTouching();
            _touching.Clear();
            AdvanceTouching();
        }

        // Physics state a run changes: the Box2D world's dynamic state (bodies, the player's fixtures, contacts,
        // broadphase), the player's pending transform sync, and the touching pairs the callbacks are based on.
        sealed class PhysicsSnapshot
        {
            public object World;
            public bool TransformDirty;
            public Vector3[] BuiltScales;
            public Pair[] Touching;
        }

        public object Capture()
        {
            object world;
            try { world = _b2.SaveState(); }
            catch (NotImplementedException) { world = null; }
            return new PhysicsSnapshot { World = world, TransformDirty = _player.TransformDirty, Touching = _pairsBefore.ToArray(),
                BuiltScales = _player.Colliders.Select(c => c.BuiltScale).ToArray() };
        }

        public void Restore(object snapshot)
        {
            var s = (PhysicsSnapshot)snapshot;
            if (s.World != null) _b2.LoadState(s.World);
            foreach (ColliderState c in _player.Colliders) c.Fixtures.Clear();
            for (b2Fixture f = _player.Body.GetFixtureList(); f != null; f = f.GetNext()) ((ColliderState)f.UserData).Fixtures.Add(f);
            _player.TransformDirty = s.TransformDirty;
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
