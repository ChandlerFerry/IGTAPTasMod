using System;
using System.Collections.Generic;

namespace Box2D
{
    /// <summary>
    ///  Exact world snapshot for route branching. Created by b2World.SaveState()
    ///  and consumed by b2World.LoadState(). After LoadState, stepping is
    ///  bit-identical to stepping the world at SaveState time.
    ///
    ///  Invariants (per contract):
    ///   - Static bodies and static fixtures are not copied; their geometry is
    ///     covered by the broadphase/dynamic-tree snapshot. They must not change
    ///     between save and load.
    ///   - Non-static bodies keep identity; their mutable state and fixture lists
    ///     are restored exactly as saved (saved b2Fixture objects are restored
    ///     themselves; UserData is preserved).
    ///   - Contacts restore manifolds (impulses + ids), flags and link order
    ///     exactly; live contact objects are reused when the fixture pair is
    ///     unchanged, otherwise contacts are re-created through the same factory
    ///     b2ContactManager uses.
    ///   - Body creation/destruction between save and load is not supported;
    ///     LoadState throws InvalidOperationException if the body count differs.
    /// </summary>
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
        internal int staticEpoch;

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
            internal int staticEpoch; // m_staticValuesEpoch when these fixture records were taken

            // Fixture proxy bookkeeping (FixtureProxy objects are shared with the
            // dynamic tree and mutate across save/load: proxyId is cleared and
            // m_proxyCount zeroed on teardown, aabb updates on sync).
            internal int[] fixtureProxyCount;
            internal int[][] fixtureProxyIds;
            internal b2AABB[][] fixtureProxyAabbs;

            // Fixture value state (so setter changes between save/load are undone).
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
        // Static bodies' fixtures change only through the b2Body calls that report StaticChanged. Each change starts a
        // new epoch; m_staticValuesEpoch names the static configuration the world holds now (a load brings back the
        // snapshot's), so a snapshot from the same epoch has nothing static to restore.
        private int m_staticEpoch, m_staticValuesEpoch;

        internal void StaticChanged(b2Body body)
        {
            if (body == null || body.m_type == b2BodyType.Static) m_staticValuesEpoch = ++m_staticEpoch;
        }

        private readonly System.Collections.Generic.Dictionary<b2Body, b2WorldState.BodyRec> m_savedBodyRecs =
            new System.Collections.Generic.Dictionary<b2Body, b2WorldState.BodyRec>();

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

        /// <summary>Snapshot the whole world (exact, in-place restorable).</summary>
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
            };

            // All bodies are recorded. Value state is restored for every body too
            // (cheap); what matters for static level geometry is that fixture lists
            // and fixture value state restore exactly — Unity rebuilds static
            // colliders on activation as well, and the broadphase restore must
            // stay consistent with the restored fixture set.
            int nonStatic = 0;
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                nonStatic++;
            }

            state.bodies = new b2WorldState.BodyRec[nonStatic];
            int bi = 0;
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
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

                // Fixture records rarely change between snapshots (static level geometry never does); share the
                // previous snapshot's arrays when identical so per-tick snapshots don't copy the level each time.
                if (m_savedBodyRecs.TryGetValue(b, out b2WorldState.BodyRec prev)
                    && (b.m_type == b2BodyType.Static && prev.type == b2BodyType.Static && prev.staticEpoch == m_staticValuesEpoch || FixturesMatch(b, prev)))
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
                    m_savedBodyRecs[b] = rec;
                }

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

            // Capture contact-edge link order per body (affects island build order).
            // A body's edge list mixes m_nodeA and m_nodeB edges (contact (P,Q) puts
            // nodeA on P's list, contact (R,P) puts nodeB on P's list), so each link
            // is identified by (record index, side).
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

        /// <summary>Restore a snapshot in place. See b2WorldState for invariants.</summary>
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

            // Drop all live contacts (they are rebuilt from the records below).
            b2Contact discarded = m_contactManager.m_contactList;
            m_contactManager.m_contactList = null;
            m_contactManager.m_contactCount = 0;
            for (b2Body b = m_bodyList; b != null; b = b.m_next)
            {
                b.m_contactList = null;
            }

            // Non-static bodies: value state + fixture list exactly as saved.
            bool staticCurrent = state.staticEpoch == m_staticValuesEpoch;
            m_staticValuesEpoch = state.staticEpoch;
            foreach (b2WorldState.BodyRec rec in state.bodies)
            {
                b2Body b = rec.body;
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

                // Detach fixtures dropped by the restore (created after the save);
                // their proxies vanish with the broadphase restore. Usually the list
                // is unchanged (a level body has thousands of fixtures): skip the scan.
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

            // Contacts: reuse matching live objects where possible.
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

            // World contact list in saved order.
            for (int i = 0; i < state.contacts.Length; i++)
            {
                b2Contact c = state.contacts[i].contact;
                c.m_prev = i > 0 ? state.contacts[i - 1].contact : null;
                c.m_next = i + 1 < state.contacts.Length ? state.contacts[i + 1].contact : null;
            }

            m_contactManager.m_contactList = state.contacts.Length > 0 ? state.contacts[0].contact : null;
            m_contactManager.m_contactCount = state.contacts.Length;

            // Per-body contact edge lists in saved order (incl. static bodies).
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
        // Snapshots store only the nodes that differ from a shared base copy (static geometry never changes; a
        // moving proxy touches a handful of nodes), so a route's worth of per-tick snapshots stays small.
        private sealed class TreeSnapshot
        {
            internal Node[] baseNodes;       // shared between snapshots, never mutated
            internal int[] changedIndex;
            internal Node[] changedNode;
            internal int root;
            internal int freeNodes;
            internal int nodeCount;
        }

        private Node[] _snapshotBase;
        public static long DbgChanged, DbgSnaps; public static int DbgNodes;

        private static bool Same(in Node a, in Node b)
            => a.Aabb.lowerBound.x == b.Aabb.lowerBound.x && a.Aabb.lowerBound.y == b.Aabb.lowerBound.y
            && a.Aabb.upperBound.x == b.Aabb.upperBound.x && a.Aabb.upperBound.y == b.Aabb.upperBound.y
            && a.Parent == b.Parent && a.Child1 == b.Child1 && a.Child2 == b.Child2
            && ReferenceEquals(a.UserData, b.UserData) && a.Height == b.Height && a.Moved == b.Moved;

        internal object SaveSnapshot()
        {
            if (_snapshotBase == null || _snapshotBase.Length != _nodes.Length) _snapshotBase = (Node[])_nodes.Clone();
            var index = new List<int>();
            var node = new List<Node>();
            for (int i = 0; i < _nodes.Length; i++)
                if (!Same(_nodes[i], _snapshotBase[i])) { index.Add(i); node.Add(_nodes[i]); }
            System.Threading.Interlocked.Add(ref DbgChanged, index.Count); System.Threading.Interlocked.Increment(ref DbgSnaps); DbgNodes = _nodes.Length;
            return new TreeSnapshot
            {
                baseNodes = _snapshotBase, changedIndex = index.ToArray(), changedNode = node.ToArray(),
                root = _root, freeNodes = _freeNodes, nodeCount = _nodeCount,
            };
        }

        internal void LoadSnapshot(object snapshot)
        {
            var snap = (TreeSnapshot)snapshot;
            if (_nodes.Length != snap.baseNodes.Length) _nodes = new Node[snap.baseNodes.Length];
            Array.Copy(snap.baseNodes, _nodes, _nodes.Length);
            for (int i = 0; i < snap.changedIndex.Length; i++) _nodes[snap.changedIndex[i]] = snap.changedNode[i];
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
                // Only the live prefix; the lengths are kept so capacity growth replays identically.
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
}
