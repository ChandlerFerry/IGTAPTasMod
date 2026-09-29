using System;
using System.Collections.Generic;

namespace Box2D
{
    public sealed class b2WorldState
    {
        internal b2Vec2 gravity;
        internal bool newContacts;
        internal bool stepComplete;
        internal float invDt0;
        internal bool allowSleep;
        internal int bodyCountGuard;
        internal BodyRec[] bodies;
        internal ContactRec[] contacts;
        internal object broadphaseSnapshot;
        internal long staticEpoch;
        internal long touchCount;

        internal sealed class BodyRec
        {
            internal b2Body body;
            internal b2BodyType type;
            internal b2Transform xf;
            internal b2Sweep sweep;
            internal b2Vec2 linearVelocity;
            internal float angularVelocity;
            internal b2Vec2 force;
            internal float torque;
            internal float sleepTime;
            internal b2BodyFlags flags;
            internal float mass;
            internal float invMass;
            internal float inertia;
            internal float invI;
            internal float gravityScale;
            internal float linearDamping;
            internal float angularDamping;
            internal b2Fixture[] fixtures;
            internal long staticEpoch;

            internal int[] fixtureProxyCount;
            internal int[][] fixtureProxyIds;
            internal b2AABB[][] fixtureProxyAabbs;

            internal bool[] fixtureSensor;
            internal b2Filter[] fixtureFilter;
            internal float[] fixtureDensity;
            internal float[] fixtureFriction;
            internal float[] fixtureRestitution;
            internal object[] fixtureUserData;
        }

        internal sealed class ContactRec
        {
            internal b2Contact contact;
            internal b2Fixture fA;
            internal b2Fixture fB;
            internal int iA;
            internal int iB;
            internal b2CollisionFlags flags;
            internal float friction;
            internal float restitution;
            internal float tangentSpeed;
            internal float toi;
            internal int toiCount;
            internal long touchOrder;
            internal b2Vec2 localNormal;
            internal b2Vec2 localPoint;
            internal b2ManifoldType type;
            internal int pointCount;
            internal b2Vec2 p0Local;
            internal float p0NormalImpulse;
            internal float p0TangentImpulse;
            internal uint p0Key;
            internal b2Vec2 p1Local;
            internal float p1NormalImpulse;
            internal float p1TangentImpulse;
            internal uint p1Key;
            internal int prevAIdx;
            internal int nextAIdx;
            internal int prevBIdx;
            internal int nextBIdx;
            internal bool prevAIsB;
            internal bool nextAIsB;
            internal bool prevBIsB;
            internal bool nextBIsB;
        }
    }

    public partial class b2World
    {
        // The static bodies' fixture state is numbered: a load skips them when the snapshot's number is the world's. The
        // numbers start at a random base per world, so a snapshot saved by another world (a state file of another process,
        // SnapshotStore) can't match by chance and have its static fixtures skipped.
        private long m_staticEpoch = (long)(uint)System.Random.Shared.Next() << 32;
        private long m_staticValuesEpoch = -1;

        internal void StaticChanged(b2Body body)
        {
            if (body == null || body.m_type == b2BodyType.Static) m_staticValuesEpoch = ++m_staticEpoch;
        }

        // Each body's last saved record. Its fixture arrays are shared with the next record while the fixtures match, and
        // the whole record is while every value matches too: LoadState only reads a BodyRec.
        private readonly System.Collections.Generic.Dictionary<b2Body, b2WorldState.BodyRec> m_savedBodyRecs =
            new System.Collections.Generic.Dictionary<b2Body, b2WorldState.BodyRec>();

        // Bitwise, not ==: a restore must bring back -0 and NaN payloads exactly as saved.
        private static bool Same<T>(in T a, in T b) where T : unmanaged
            => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)));

        private static bool ValuesMatch(b2Body b, b2WorldState.BodyRec prev)
        {
            return prev.type == b.m_type && prev.flags == b.m_flags && Same(prev.xf, b.m_xf) && Same(prev.sweep, b.m_sweep)
                && Same(prev.linearVelocity, b.m_linearVelocity) && Same(prev.angularVelocity, b.m_angularVelocity)
                && Same(prev.force, b.m_force) && Same(prev.torque, b.m_torque) && Same(prev.sleepTime, b.m_sleepTime)
                && Same(prev.mass, b.m_mass) && Same(prev.invMass, b.m_invMass) && Same(prev.inertia, b.m_I)
                && Same(prev.invI, b.m_invI) && Same(prev.gravityScale, b.m_gravityScale)
                && Same(prev.linearDamping, b.m_linearDamping) && Same(prev.angularDamping, b.m_angularDamping);
        }

        private static bool FixturesMatch(b2Body b, b2WorldState.BodyRec prev)
        {
            int i = 0;
            for (b2Fixture f = b.m_fixtureList; f != null; f = f.m_next, i++)
            {
                if (i >= prev.fixtures.Length || !ReferenceEquals(prev.fixtures[i], f) || prev.fixtureSensor[i] != f.IsSensor()
                    || !prev.fixtureFilter[i].Equals(f.m_filter) || prev.fixtureDensity[i] != f.m_density
                    || prev.fixtureFriction[i] != f.m_friction || prev.fixtureRestitution[i] != f.m_restitution
                    || !ReferenceEquals(prev.fixtureUserData[i], f.UserData) || prev.fixtureProxyCount[i] != f.m_proxyCount)
                    return false;
                int pc = f.m_proxies?.Length ?? 0;
                if (prev.fixtureProxyIds[i].Length != pc) return false;
                for (int j = 0; j < pc; j++)
                {
                    b2AABB x = prev.fixtureProxyAabbs[i][j], y = f.m_proxies[j].aabb;
                    if (prev.fixtureProxyIds[i][j] != f.m_proxies[j].proxyId || x.lowerBound.x != y.lowerBound.x || x.lowerBound.y != y.lowerBound.y
                        || x.upperBound.x != y.upperBound.x || x.upperBound.y != y.upperBound.y)
                        return false;
                }
            }
            return i == prev.fixtures.Length;
        }

        public object SaveState()
        {
            var state = new b2WorldState
            {
                gravity = m_gravity,
                newContacts = m_newContacts,
                stepComplete = m_stepComplete,
                invDt0 = m_inv_dt0,
                allowSleep = m_allowSleep,
                bodyCountGuard = m_bodyCount,
                staticEpoch = m_staticValuesEpoch,
                touchCount = m_touchCount,
            };

            int nonStatic = 0;
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                nonStatic++;
            }

            state.bodies = new b2WorldState.BodyRec[nonStatic];
            int bi = 0;
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                bool known = m_savedBodyRecs.TryGetValue(b, out b2WorldState.BodyRec prev);
                bool fixturesMatch = known
                    && (b.m_type == b2BodyType.Static && prev.type == b2BodyType.Static && prev.staticEpoch == m_staticValuesEpoch || FixturesMatch(b, prev));
                if (fixturesMatch && ValuesMatch(b, prev))
                {
                    state.bodies[bi++] = prev;
                    continue;
                }

                var rec = new b2WorldState.BodyRec
                {
                    body = b,
                    type = b.m_type,
                    xf = b.m_xf,
                    sweep = b.m_sweep,
                    linearVelocity = b.m_linearVelocity,
                    angularVelocity = b.m_angularVelocity,
                    force = b.m_force,
                    torque = b.m_torque,
                    sleepTime = b.m_sleepTime,
                    flags = b.m_flags,
                    mass = b.m_mass,
                    invMass = b.m_invMass,
                    inertia = b.m_I,
                    invI = b.m_invI,
                    gravityScale = b.m_gravityScale,
                    linearDamping = b.m_linearDamping,
                    angularDamping = b.m_angularDamping,
                };

                if (fixturesMatch)
                {
                    rec.fixtures = prev.fixtures;
                    rec.fixtureSensor = prev.fixtureSensor;
                    rec.fixtureFilter = prev.fixtureFilter;
                    rec.fixtureDensity = prev.fixtureDensity;
                    rec.fixtureFriction = prev.fixtureFriction;
                    rec.fixtureRestitution = prev.fixtureRestitution;
                    rec.fixtureUserData = prev.fixtureUserData;
                    rec.fixtureProxyCount = prev.fixtureProxyCount;
                    rec.fixtureProxyIds = prev.fixtureProxyIds;
                    rec.fixtureProxyAabbs = prev.fixtureProxyAabbs;
                    // The epoch the shared arrays were recorded at (the static check above).
                    rec.staticEpoch = prev.staticEpoch;
                }
                else
                {
                    int fc = 0;
                    for (b2Fixture f = b.m_fixtureList; f != null; f = f.m_next)
                    {
                        fc++;
                    }

                    rec.fixtures = new b2Fixture[fc];
                    rec.fixtureSensor = new bool[fc];
                    rec.fixtureFilter = new b2Filter[fc];
                    rec.fixtureDensity = new float[fc];
                    rec.fixtureFriction = new float[fc];
                    rec.fixtureRestitution = new float[fc];
                    rec.fixtureUserData = new object[fc];
                    rec.fixtureProxyCount = new int[fc];
                    rec.fixtureProxyIds = new int[fc][];
                    rec.fixtureProxyAabbs = new b2AABB[fc][];

                    int fi = 0;
                    for (b2Fixture f = b.m_fixtureList; f != null; f = f.m_next)
                    {
                        rec.fixtures[fi] = f;
                        rec.fixtureSensor[fi] = f.IsSensor();
                        rec.fixtureFilter[fi] = f.m_filter;
                        rec.fixtureDensity[fi] = f.m_density;
                        rec.fixtureFriction[fi] = f.m_friction;
                        rec.fixtureRestitution[fi] = f.m_restitution;
                        rec.fixtureUserData[fi] = f.UserData;
                        rec.fixtureProxyCount[fi] = f.m_proxyCount;
                        int pc = f.m_proxies?.Length ?? 0;
                        rec.fixtureProxyIds[fi] = new int[pc];
                        rec.fixtureProxyAabbs[fi] = new b2AABB[pc];
                        for (int j = 0; j < pc; j++)
                        {
                            rec.fixtureProxyIds[fi][j] = f.m_proxies[j].proxyId;
                            rec.fixtureProxyAabbs[fi][j] = f.m_proxies[j].aabb;
                        }

                        fi++;
                    }
                    rec.staticEpoch = m_staticValuesEpoch;
                }

                m_savedBodyRecs[b] = rec;
                state.bodies[bi++] = rec;
            }

            int cc = m_contactManager.m_contactCount;
            state.contacts = new b2WorldState.ContactRec[cc];
            int ci = 0;
            for (b2Contact c = m_contactManager.m_contactList; c != null; c = c.m_next)
            {
                var cr = new b2WorldState.ContactRec
                {
                    contact = c,
                    fA = c.m_fixtureA,
                    fB = c.m_fixtureB,
                    iA = c.m_indexA,
                    iB = c.m_indexB,
                    flags = c.m_flags,
                    friction = c.m_friction,
                    restitution = c.m_restitution,
                    tangentSpeed = c.m_tangentSpeed,
                    toi = c.m_toi,
                    toiCount = c.m_toiCount,
                    touchOrder = c.m_touchOrder,
                };

                b2Manifold m = c.m_manifold;
                cr.localNormal = m.localNormal;
                cr.localPoint = m.localPoint;
                cr.type = m.type;
                cr.pointCount = m.pointCount;
                if (m.pointCount > 0 && m.points[0] != null)
                {
                    cr.p0Local = m.points[0].localPoint;
                    cr.p0NormalImpulse = m.points[0].normalImpulse;
                    cr.p0TangentImpulse = m.points[0].tangentImpulse;
                    cr.p0Key = m.points[0].id.key;
                }

                if (m.pointCount > 1 && m.points[1] != null)
                {
                    cr.p1Local = m.points[1].localPoint;
                    cr.p1NormalImpulse = m.points[1].normalImpulse;
                    cr.p1TangentImpulse = m.points[1].tangentImpulse;
                    cr.p1Key = m.points[1].id.key;
                }

                state.contacts[ci++] = cr;
            }

            for (int i = 0; i < state.contacts.Length; i++)
            {
                b2Contact c = state.contacts[i].contact;
                FindEdge(state, c.m_nodeA.prev, out state.contacts[i].prevAIdx, out state.contacts[i].prevAIsB);
                FindEdge(state, c.m_nodeA.next, out state.contacts[i].nextAIdx, out state.contacts[i].nextAIsB);
                FindEdge(state, c.m_nodeB.prev, out state.contacts[i].prevBIdx, out state.contacts[i].prevBIsB);
                FindEdge(state, c.m_nodeB.next, out state.contacts[i].nextBIdx, out state.contacts[i].nextBIsB);
            }

            state.broadphaseSnapshot = m_contactManager.m_broadPhase.SaveSnapshot();
            return state;
        }

        public void LoadState(object stateObject)
        {
            var state = (b2WorldState)stateObject;
            if (state.bodyCountGuard != m_bodyCount)
            {
                throw new InvalidOperationException(
                    "b2World.LoadState: body count differs from the snapshot (body creation/destruction between save and load is not supported).");
            }

            m_contactManager.m_broadPhase.LoadSnapshot(state.broadphaseSnapshot);

            m_gravity = state.gravity;
            m_newContacts = state.newContacts;
            m_stepComplete = state.stepComplete;
            m_inv_dt0 = state.invDt0;
            m_allowSleep = state.allowSleep;
            m_touchCount = state.touchCount;

            b2Contact discarded = m_contactManager.m_contactList;
            m_contactManager.m_contactList = null;
            m_contactManager.m_contactTail = null;
            m_contactManager.m_contactCount = 0;
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                b.m_contactList = null;
            }

            bool staticCurrent = state.staticEpoch == m_staticValuesEpoch;
            m_staticValuesEpoch = state.staticEpoch;
            foreach (b2WorldState.BodyRec rec in state.bodies)
            {
                b2Body b = rec.body;
                // The body now is this record: a save before it changes shares it (a body a run leaves alone, asleep).
                m_savedBodyRecs[b] = rec;
                b.m_type = rec.type;
                b.m_xf = rec.xf;
                b.m_sweep = rec.sweep;
                b.m_linearVelocity = rec.linearVelocity;
                b.m_angularVelocity = rec.angularVelocity;
                b.m_force = rec.force;
                b.m_torque = rec.torque;
                b.m_sleepTime = rec.sleepTime;
                b.m_flags = rec.flags;
                b.m_mass = rec.mass;
                b.m_invMass = rec.invMass;
                b.m_I = rec.inertia;
                b.m_invI = rec.invI;
                b.m_gravityScale = rec.gravityScale;
                b.m_linearDamping = rec.linearDamping;
                b.m_angularDamping = rec.angularDamping;
                if (staticCurrent && rec.type == b2BodyType.Static)
                {
                    continue;
                }

                int same = 0;
                b2Fixture rest = b.m_fixtureList;
                while (rest != null && same < rec.fixtures.Length && ReferenceEquals(rest, rec.fixtures[same]))
                {
                    rest = rest.m_next;
                    same++;
                }

                bool unchanged = rest == null && same == rec.fixtures.Length;
                for (b2Fixture f = unchanged ? null : b.m_fixtureList; f != null;)
                {
                    b2Fixture next = f.m_next;
                    f.m_next = null;
                    if (Array.IndexOf(rec.fixtures, f) < 0)
                    {
                        f.m_body = null;
                    }

                    f = next;
                }

                b.m_fixtureList = rec.fixtures.Length > 0 ? rec.fixtures[0] : null;
                b.m_fixtureCount = rec.fixtures.Length;
                for (int i = 0; i < rec.fixtures.Length; i++)
                {
                    b2Fixture f = rec.fixtures[i];
                    f.m_body = b;
                    f.m_next = i + 1 < rec.fixtures.Length ? rec.fixtures[i + 1] : null;
                    f.Sensor = rec.fixtureSensor[i];
                    f.m_filter = rec.fixtureFilter[i];
                    f.m_density = rec.fixtureDensity[i];
                    f.m_friction = rec.fixtureFriction[i];
                    f.m_restitution = rec.fixtureRestitution[i];
                    f.UserData = rec.fixtureUserData[i];
                    f.m_proxyCount = rec.fixtureProxyCount[i];
                    int pc = f.m_proxies?.Length ?? 0;
                    for (int j = 0; j < pc; j++)
                    {
                        f.m_proxies[j].proxyId = rec.fixtureProxyIds[i][j];
                        f.m_proxies[j].aabb = rec.fixtureProxyAabbs[i][j];
                    }
                }
            }

            for (int i = 0; i < state.contacts.Length; i++)
            {
                b2WorldState.ContactRec cr = state.contacts[i];
                b2Contact c = TakeMatch(ref discarded, cr);
                if (c == null)
                {
                    c = b2Contact.Create(cr.fA, cr.iA, cr.fB, cr.iB);
                }

                c.m_flags = cr.flags;
                c.m_friction = cr.friction;
                c.m_restitution = cr.restitution;
                c.m_tangentSpeed = cr.tangentSpeed;
                c.m_toi = cr.toi;
                c.m_toiCount = cr.toiCount;
                c.m_touchOrder = cr.touchOrder;

                b2Manifold m = c.m_manifold;
                m.localNormal = cr.localNormal;
                m.localPoint = cr.localPoint;
                m.type = cr.type;
                m.pointCount = cr.pointCount;
                if (cr.pointCount > 0)
                {
                    if (m.points[0] == null)
                    {
                        m.points[0] = new b2ManifoldPoint();
                    }

                    m.points[0].localPoint = cr.p0Local;
                    m.points[0].normalImpulse = cr.p0NormalImpulse;
                    m.points[0].tangentImpulse = cr.p0TangentImpulse;
                    m.points[0].id.key = cr.p0Key;
                }

                if (cr.pointCount > 1)
                {
                    if (m.points[1] == null)
                    {
                        m.points[1] = new b2ManifoldPoint();
                    }

                    m.points[1].localPoint = cr.p1Local;
                    m.points[1].normalImpulse = cr.p1NormalImpulse;
                    m.points[1].tangentImpulse = cr.p1TangentImpulse;
                    m.points[1].id.key = cr.p1Key;
                }

                cr.contact = c;
            }

            for (int i = 0; i < state.contacts.Length; i++)
            {
                b2Contact c = state.contacts[i].contact;
                c.m_prev = i > 0 ? state.contacts[i - 1].contact : null;
                c.m_next = i + 1 < state.contacts.Length ? state.contacts[i + 1].contact : null;
            }

            m_contactManager.m_contactList = state.contacts.Length > 0 ? state.contacts[0].contact : null;
            m_contactManager.m_contactTail = state.contacts.Length > 0 ? state.contacts[state.contacts.Length - 1].contact : null;
            m_contactManager.m_contactCount = state.contacts.Length;

            foreach (b2WorldState.ContactRec cr in state.contacts)
            {
                b2Contact c = cr.contact;
                b2Body bodyA = c.m_fixtureA.m_body;
                b2Body bodyB = c.m_fixtureB.m_body;

                c.m_nodeA.contact = c;
                c.m_nodeA.other = bodyB;
                c.m_nodeA.prev = GetEdge(state, cr.prevAIdx, cr.prevAIsB);
                c.m_nodeA.next = GetEdge(state, cr.nextAIdx, cr.nextAIsB);

                c.m_nodeB.contact = c;
                c.m_nodeB.other = bodyA;
                c.m_nodeB.prev = GetEdge(state, cr.prevBIdx, cr.prevBIsB);
                c.m_nodeB.next = GetEdge(state, cr.nextBIdx, cr.nextBIsB);

                if (cr.prevAIdx < 0)
                {
                    bodyA.m_contactList = c.m_nodeA;
                }

                if (cr.prevBIdx < 0)
                {
                    bodyB.m_contactList = c.m_nodeB;
                }
            }
        }

        private static void FindEdge(b2WorldState state, b2ContactEdge edge, out int index, out bool isSideB)
        {
            index = -1;
            isSideB = false;
            if (edge == null)
            {
                return;
            }

            for (int i = 0; i < state.contacts.Length; i++)
            {
                b2Contact c = state.contacts[i].contact;
                if (c.m_nodeA == edge)
                {
                    index = i;
                    isSideB = false;
                    return;
                }

                if (c.m_nodeB == edge)
                {
                    index = i;
                    isSideB = true;
                    return;
                }
            }

            throw new InvalidOperationException(
                "b2World.SaveState: contact edge list is inconsistent (edge does not belong to any listed contact).");
        }

        private static b2ContactEdge GetEdge(b2WorldState state, int index, bool isSideB)
        {
            if (index < 0)
            {
                return null;
            }

            b2Contact c = state.contacts[index].contact;
            return isSideB ? c.m_nodeB : c.m_nodeA;
        }

        private static b2Contact TakeMatch(ref b2Contact discarded, b2WorldState.ContactRec cr)
        {
            b2Contact prev = null;
            for (b2Contact c = discarded; c != null; c = c.m_next)
            {
                if (c.m_fixtureA == cr.fA && c.m_indexA == cr.iA && c.m_fixtureB == cr.fB && c.m_indexB == cr.iB)
                {
                    if (prev == null)
                    {
                        discarded = c.m_next;
                    }
                    else
                    {
                        prev.m_next = c.m_next;
                    }

                    c.m_next = null;
                    return c;
                }

                prev = c;
            }

            return null;
        }
    }

    public sealed partial class b2DynamicTree
    {
        private sealed class TreeSnapshot
        {
            internal Node[] baseNodes;
            internal int[] changedIndex;
            internal Node[] changedNode;
            internal int root;
            internal int freeNodes;
            internal int nodeCount;
        }

        private readonly Dictionary<int, Node[]> _snapshotBases = new Dictionary<int, Node[]>();

        private static bool Same(in Node a, in Node b)
            => a.Aabb.lowerBound.x == b.Aabb.lowerBound.x && a.Aabb.lowerBound.y == b.Aabb.lowerBound.y
            && a.Aabb.upperBound.x == b.Aabb.upperBound.x && a.Aabb.upperBound.y == b.Aabb.upperBound.y
            && a.Parent == b.Parent && a.Child1 == b.Child1 && a.Child2 == b.Child2
            && ReferenceEquals(a.UserData, b.UserData) && a.Height == b.Height && a.Moved == b.Moved;

        // The tree equals _synced (the snapshot last loaded or saved) except at the _dirty indices written since; null means
        // unknown (a pool growth, rebuild or origin shift), so the next load copies the whole base. Every node write in
        // DynamicTree.cs calls Touch. IGTAP_TREE_CHECK=1 checks each load and save against the full copy/compare.
        private TreeSnapshot _synced;
        private bool[] _dirtyFlag = new bool[256];
        private readonly List<int> _dirty = new List<int>();
        private static readonly bool CheckTree = TreeCheckOn();
        private static long TreeChecks, TreeMismatches;

        private static bool TreeCheckOn()
        {
            if (Environment.GetEnvironmentVariable("IGTAP_TREE_CHECK") != "1") return false;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Console.Error.WriteLine($"b2DynamicTree check: {TreeChecks} loads/saves checked, {TreeMismatches} mismatches");
            return true;
        }

        private static void TreeMismatch(string message)
        {
            System.Threading.Interlocked.Increment(ref TreeMismatches);
            throw new InvalidOperationException(message);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void Touch(int i)
        {
            if (!_dirtyFlag[i]) { _dirtyFlag[i] = true; _dirty.Add(i); }
        }

        private void Resync(TreeSnapshot snap)
        {
            foreach (int i in _dirty) _dirtyFlag[i] = false;
            _dirty.Clear();
            _synced = snap;
        }

        internal object SaveSnapshot()
        {
            // The base the tree last matched a snapshot on (the one loaded or saved): only the nodes written since, and that
            // snapshot's own delta, can differ from it. After a restore that is the restored snapshot's base, not the newest
            // one: across a rebase they differ almost everywhere, and comparing against the newest took a full scan and a new
            // base copy on every capture on the other side.
            Node[] baseNodes;
            bool tracked = _synced != null && _synced.baseNodes.Length == _capacity;
            if (tracked) baseNodes = _synced.baseNodes;
            else if (!_snapshotBases.TryGetValue(_capacity, out baseNodes))
                _snapshotBases[_capacity] = baseNodes = _nodes.AsSpan(0, _capacity).ToArray();
            // Count the changed nodes first so the delta arrays are exactly sized: no List growth chain, no ToArray
            // copies. The second pass reads the same unchanged nodes as the first, so the delta is identical.
            int changed = 0;
            if (tracked)
            {
                foreach (int i in _synced.changedIndex) Touch(i);
                _dirty.Sort();
                foreach (int i in _dirty)
                    if (!Same(_nodes[i], baseNodes[i])) changed++;
            }
            else
                for (int i = 0; i < _capacity; i++)
                    if (!Same(_nodes[i], baseNodes[i])) changed++;
            // A tree rebuilt since the base (a FloatingOrigin rebase rebuilds every body-less collider) differs from it almost
            // everywhere: this state becomes the base, so later snapshots near it hold only what moved. Older ones keep theirs.
            if (changed * 4 > _capacity)
            {
                _snapshotBases[_capacity] = baseNodes = _nodes.AsSpan(0, _capacity).ToArray();
                changed = 0;
            }
            int[] index = changed == 0 ? Array.Empty<int>() : new int[changed];
            Node[] node = changed == 0 ? Array.Empty<Node>() : new Node[changed];
            int n = 0;
            if (tracked)
                foreach (int i in _dirty)
                {
                    if (n == changed) break;
                    if (!Same(_nodes[i], baseNodes[i])) { index[n] = i; node[n] = _nodes[i]; n++; }
                }
            else
                for (int i = 0; i < _capacity && n < changed; i++)
                    if (!Same(_nodes[i], baseNodes[i])) { index[n] = i; node[n] = _nodes[i]; n++; }
            if (CheckTree)
            {
                int k = 0;
                for (int i = 0; i < _capacity; i++)
                    if (!Same(_nodes[i], baseNodes[i]) && (k >= changed || index[k++] != i))
                        TreeMismatch($"b2DynamicTree.SaveSnapshot: node {i} changed but not in the delta");
                if (k != changed) TreeMismatch("b2DynamicTree.SaveSnapshot: delta count differs from a full compare");
                System.Threading.Interlocked.Increment(ref TreeChecks);
            }
            var saved = new TreeSnapshot
            {
                baseNodes = baseNodes, changedIndex = index, changedNode = node,
                root = _root, freeNodes = _freeNodes, nodeCount = _nodeCount,
            };
            Resync(saved);
            return saved;
        }

        internal void AddTo(ref b2Digest h, Func<b2Fixture, long> fixtureKey)
        {
            h.Add(_capacity); h.Add(_root); h.Add(_freeNodes); h.Add(_nodeCount);
            for (int i = 0; i < _capacity; i++)
            {
                ref Node n = ref _nodes[i];
                h.Line("node " + i);
                h.Add(n.Aabb); h.Add(n.Parent); h.Add(n.Child1); h.Add(n.Child2); h.Add(n.Height); h.Add(n.Moved);
                if (n.UserData is FixtureProxy p) { h.Add(fixtureKey(p.fixture)); h.Add(p.childIndex); h.Add(p.proxyId); }
                else h.Add(n.UserData == null ? -1 : -2);
            }
        }

        internal void LoadSnapshot(object snapshot)
        {
            var snap = (TreeSnapshot)snapshot;
            int capacity = snap.baseNodes.Length;
            if (_synced != null && ReferenceEquals(_synced.baseNodes, snap.baseNodes))
            {
                foreach (int i in _dirty) _nodes[i] = snap.baseNodes[i];
                foreach (int i in _synced.changedIndex) _nodes[i] = snap.baseNodes[i];
            }
            else
            {
                if (_nodes.Length < capacity) { _nodes = new Node[capacity]; _dirtyFlag = new bool[capacity]; }
                Array.Copy(snap.baseNodes, _nodes, capacity);
            }
            for (int i = 0; i < snap.changedIndex.Length; i++) _nodes[snap.changedIndex[i]] = snap.changedNode[i];
            Resync(snap);
            if (CheckTree)
            {
                var full = (Node[])snap.baseNodes.Clone();
                for (int i = 0; i < snap.changedIndex.Length; i++) full[snap.changedIndex[i]] = snap.changedNode[i];
                for (int i = 0; i < capacity; i++)
                    if (!Same(_nodes[i], full[i])) TreeMismatch($"b2DynamicTree.LoadSnapshot: node {i} differs from a full restore");
                System.Threading.Interlocked.Increment(ref TreeChecks);
            }
            _capacity = capacity;
            _root = snap.root;
            _freeNodes = snap.freeNodes;
            _nodeCount = snap.nodeCount;
        }
    }

    public partial class b2BroadPhase
    {
        internal sealed class BroadPhaseSnapshot
        {
            internal object tree;
            internal int[] moveBuffer;
            internal int moveLength;
            internal int moveCapacity;
            internal int moveCount;
            internal Pair[] pairBuffer;
            internal int pairLength;
            internal int pairCapacity;
            internal int pairCount;
            internal int proxyCount;
            internal int queryProxyId;
        }

        internal object SaveSnapshot()
        {
            var snap = new BroadPhaseSnapshot
            {
                tree = m_tree.SaveSnapshot(),
                moveBuffer = new int[m_moveCount],
                moveLength = m_moveBuffer.Length,
                moveCapacity = m_moveCapacity,
                moveCount = m_moveCount,
                pairBuffer = new Pair[m_pairCount],
                pairLength = m_pairBuffer.Length,
                pairCapacity = m_pairCapacity,
                pairCount = m_pairCount,
                proxyCount = m_proxyCount,
                queryProxyId = m_queryProxyId,
            };
            Array.Copy(m_moveBuffer, snap.moveBuffer, m_moveCount);
            Array.Copy(m_pairBuffer, snap.pairBuffer, m_pairCount);
            return snap;
        }

        internal void AddTo(ref b2Digest h, Func<b2Fixture, long> fixtureKey)
        {
            h.Line("moves");
            h.Add(m_moveCount);
            for (int i = 0; i < m_moveCount; i++) h.Add(m_moveBuffer[i]);
            h.Add(m_pairCount);
            for (int i = 0; i < m_pairCount; i++) { h.Add(m_pairBuffer[i].proxyIdA); h.Add(m_pairBuffer[i].proxyIdB); }
            h.Add(m_proxyCount); h.Add(m_queryProxyId);
            m_tree.AddTo(ref h, fixtureKey);
        }

        internal void LoadSnapshot(object snapshot)
        {
            var snap = (BroadPhaseSnapshot)snapshot;
            m_tree.LoadSnapshot(snap.tree);
            if (m_moveBuffer.Length != snap.moveLength) m_moveBuffer = new int[snap.moveLength];
            Array.Copy(snap.moveBuffer, m_moveBuffer, snap.moveBuffer.Length);
            m_moveCapacity = snap.moveCapacity;
            m_moveCount = snap.moveCount;
            if (m_pairBuffer.Length != snap.pairLength) m_pairBuffer = new Pair[snap.pairLength];
            Array.Copy(snap.pairBuffer, m_pairBuffer, snap.pairBuffer.Length);
            m_pairCapacity = snap.pairCapacity;
            m_pairCount = snap.pairCount;
            m_proxyCount = snap.proxyCount;
            m_queryProxyId = snap.queryProxyId;
        }
    }
    /// <summary>
    /// FNV-1a over 64-bit values: a digest that is the same in every process (no HashCode seed, no identity hashes).
    /// </summary>
    public struct b2Digest
    {
        public ulong Value;
        /// <summary>When set, every value added as text (a failing determinism check's dumps, to diff).</summary>
        public System.Text.StringBuilder Log;
        public static b2Digest Start(System.Text.StringBuilder log = null) => new b2Digest { Value = 14695981039346656037UL, Log = log };
        public void Add(long v)
        {
            for (int i = 0; i < 8; i++) { Value ^= (byte)(v >> (8 * i)); Value *= 1099511628211UL; }
            Log?.Append(v).Append(' ');
        }
        public void Line(string label) => Log?.Append('\n').Append(label).Append(": ");
        public void Add(float f) => Add(BitConverter.SingleToInt32Bits(f));
        public void Add(bool b) => Add(b ? 1 : 0);
        public void Add(in b2Vec2 v) { Add(v.x); Add(v.y); }
        public void Add(in b2AABB box) { Add(box.lowerBound); Add(box.upperBound); }
        public void Add(in b2Transform xf) { Add(xf.p); Add(xf.q.M11); Add(xf.q.M12); Add(xf.q.M21); Add(xf.q.M22); Add(xf.q.M31); Add(xf.q.M32); }
        public void Add(in b2Sweep s) { Add(s.localCenter); Add(s.c0); Add(s.c); Add(s.a0); Add(s.a); Add(s.alpha0); }
    }

    public partial class b2World
    {
        /// <summary>
        /// Everything a step reads from this world, bit for bit, in list order: the world's values, each body (its values,
        /// fixtures and their proxies, its contact edge list), the contact list with each contact's values and manifold,
        /// and the broadphase (move and pair buffers, every tree node). <paramref name="fixtureKey"/> names a fixture the same
        /// way in every process. Two worlds that step differently from here differ in it (the determinism check's measure).
        /// </summary>
        public ulong StateDigest(Func<b2Fixture, long> fixtureKey, System.Text.StringBuilder log = null)
        {
            var h = b2Digest.Start(log);
            h.Line("world");
            h.Add(m_gravity); h.Add(m_newContacts); h.Add(m_stepComplete); h.Add(m_inv_dt0); h.Add(m_allowSleep);
            h.Add(m_bodyCount); h.Add(m_touchCount); h.Add(m_contactManager.m_contactCount);
            var index = new Dictionary<b2Contact, int>();
            int ci = 0;
            for (b2Contact c = m_contactManager.m_contactList; c != null; c = c.m_next) index[c] = ci++;
            h.Add(ci);
            h.Add(m_contactManager.m_contactTail == null ? -1 : index[m_contactManager.m_contactTail]);
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                h.Line("body");
                h.Add((int)b.m_type); h.Add((int)b.m_flags); h.Add(b.m_xf); h.Add(b.m_sweep);
                h.Add(b.m_linearVelocity); h.Add(b.m_angularVelocity); h.Add(b.m_force); h.Add(b.m_torque); h.Add(b.m_sleepTime);
                h.Add(b.m_mass); h.Add(b.m_invMass); h.Add(b.m_I); h.Add(b.m_invI); h.Add(b.m_gravityScale);
                h.Add(b.m_linearDamping); h.Add(b.m_angularDamping); h.Add(b.m_fixtureCount);
                for (b2Fixture f = b.m_fixtureList; f != null; f = f.m_next)
                {
                    h.Add(fixtureKey(f)); h.Add(f.IsSensor()); h.Add(f.m_density); h.Add(f.m_friction); h.Add(f.m_restitution);
                    h.Add(f.m_filter.categoryBits); h.Add(f.m_filter.groupIndex); h.Add(f.m_filter.maskBits);
                    h.Add(f.m_proxyCount);
                    for (int j = 0; j < f.m_proxyCount; j++) { h.Add(f.m_proxies[j].proxyId); h.Add(f.m_proxies[j].aabb); h.Add(f.m_proxies[j].childIndex); }
                }
                h.Add(-2);
                for (b2ContactEdge e = b.m_contactList; e != null; e = e.next)
                {
                    h.Add(index[e.contact]); h.Add(ReferenceEquals(e, e.contact.m_nodeB));
                }
                h.Add(-3);
            }
            for (b2Contact c = m_contactManager.m_contactList; c != null; c = c.m_next)
            {
                h.Line("contact");
                h.Add(fixtureKey(c.m_fixtureA)); h.Add(c.m_indexA); h.Add(fixtureKey(c.m_fixtureB)); h.Add(c.m_indexB);
                h.Add((int)c.m_flags); h.Add(c.m_friction); h.Add(c.m_restitution); h.Add(c.m_tangentSpeed);
                h.Add(c.m_toi); h.Add(c.m_toiCount); h.Add(c.m_touchOrder);
                h.Add(c.m_prev == null ? -1 : index[c.m_prev]);
                b2Manifold m = c.m_manifold;
                h.Add(m.localNormal); h.Add(m.localPoint); h.Add((int)m.type); h.Add(m.pointCount);
                for (int j = 0; j < m.pointCount; j++)
                {
                    b2ManifoldPoint p = m.points[j];
                    h.Add(p.localPoint); h.Add(p.normalImpulse); h.Add(p.tangentImpulse); h.Add(p.id.key);
                }
            }
            m_contactManager.m_broadPhase.AddTo(ref h, fixtureKey);
            return h.Value;
        }
    }
}
