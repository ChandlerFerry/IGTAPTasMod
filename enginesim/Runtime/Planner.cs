using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// The contact-node planner (issue #19, `IGTAP.EngineSim plan`): a beam search over exact snapshots, layered by physics
    /// ticks. A node is the state after an input tick where the player's contact changed (<see cref="ContactLog"/>), or after
    /// <see cref="Options.Cap"/> ticks without one. An edge is one primitive played from the node's snapshot until the next
    /// contact after its own inputs: a decision at the node (the stick, a jump press or release, a dash with its jump frame,
    /// the omnidash's aims), a <see cref="Primitives"/> input at its window around the node's contact or around the
    /// contact a coast from the node runs into (a buffered jump before a landing, a coyote jump after a ledge, a wall clip), or
    /// a mined tech demo window (<see cref="TechGrafts"/>) at those contacts. Every layer keeps its best
    /// <see cref="Options.Width"/> nodes: most goals or forced-path boxes passed, then the <see cref="DistanceField"/>
    /// distance to the target, then speed. A coarse key (position cell, velocity and momentum buckets, contact, counts left,
    /// facing, the jump button, the timer, progress) only merges: per key a few Pareto labels (earliest, faster) survive,
    /// and every expansion starts from the node's exact snapshot. Only inputs a controller makes are played (README "### Rules").
    /// After a finish, nodes that can't beat it at the fastest speed seen are cut; the search then runs again at twice the
    /// width while time is left (anytime).
    /// </summary>
    public sealed class Planner
    {
        public sealed class Options
        {
            public int Threads = Environment.ProcessorCount;
            /// <summary>Nodes kept per physics-tick layer in the first pass; each later pass doubles it up to <see cref="MaxWidth"/>.</summary>
            public int Width = 48, MaxWidth = 768;
            /// <summary>Pareto labels kept per coarse key.</summary>
            public int Labels = 2;
            /// <summary>The key's position cell, in units.</summary>
            public float Cell = 16f;
            /// <summary>Input ticks after a decision without a contact before a node is made anyway.</summary>
            public int Cap = 8;
            /// <summary>Mid-air inputs between nodes (the Step 0 gaps): in the air, after a coast of <see cref="MidAirDelays"/>
            /// ticks, a double jump or a jump cut, a stick change, a dash or a dash jump with its lock stick either way.</summary>
            public bool MidAir = true;
            public static readonly int[] MidAirDelays = { 2, 4, 6 };
            /// <summary>Omnidash nodes also play the down-dash landing chain as one edge (Worker.BounceChain).</summary>
            public bool Chains = true;
            /// <summary>A Full-TAS window's first goal (the goals reached at its start; -1: the start's own count).</summary>
            public int FromGoal = -1;
            /// <summary>Finishes up to this many physics ticks behind the fastest are kept when they carry more speed (a window's next
            /// goal scores arrival and momentum); 0: only faster ones.</summary>
            public int MomentumSlack;
            public double Seconds = 300;
            /// <summary>Stop after the first pass that finishes.</summary>
            public bool FirstOnly;
            /// <summary>Known finished routes whose cost to go ranks the nodes instead of the field's distance (the backward half:
            /// issue #19's "the best's per-cell first arrival"): a node's estimate is the physics ticks a route point near it (same
            /// stage, within <see cref="GuideRadius"/>) still had to its finish, plus its distance to that point at
            /// <see cref="GuideSpeed"/>; a node near none of them ranks last.</summary>
            public List<List<InputTick>> Guides = new List<List<InputTick>>();
            public float GuideRadius = 160f, GuideSpeed = 10f, GuideAccel = 60f;
            /// <summary>Follow edges: a node within <see cref="FollowRadius"/> units and <see cref="FollowSpeed"/> units/s of a guide
            /// route's point (same stage) plays that route's next <see cref="FollowTicks"/> inputs (0: off), the known routes'
            /// pieces as primitives (issue #19's backward half, as Splice uses the best's suffix).</summary>
            public int FollowTicks = 16;
            public float FollowRadius = 48f, FollowSpeed = 200f;
            /// <summary>A guide point counts only for nodes at most this many physics ticks behind the point's own time: a route
            /// that passes one place twice (Course 4 starts under its end) doesn't pull a node early to its late pass.</summary>
            public int GuideAhead = 60;
            /// <summary>Faster known runs that don't replay here (<see cref="Trail"/>): their places rank nodes as guide points do and
            /// their inputs are followed from nodes within <see cref="FollowRadius"/> and <see cref="TrailFollowSpeed"/> of them.</summary>
            public List<Trail> Trails = new List<Trail>();
            public float TrailFollowSpeed = 400f;
        }

        /// <summary>
        /// A known run's path, per physics tick from the run's start (a 1x probe of an older route: its place in the world frame,
        /// Velocity + momentum, and the input tick it had played), with its inputs. It doesn't replay on this engine; its
        /// goals are found along its path (an area goal where its place touches the goal's rect; a buy, start or finish goal
        /// on the tick after the goal before it), and its ticks from goal to goal are the costs to go its points give.
        /// </summary>
        public sealed class Trail
        {
            public string Name;
            public List<InputTick> Inputs;
            public WorldPos[] At;
            public Vector2[] V;
            public int[] Input;
        }

        public sealed class Route
        {
            public List<InputTick> Inputs;
            public float Time;
            public long RunTicks;
            public long Ticks;
            public double Wall;
            public int Width;
            public string How;
            /// <summary>Velocity + momentum at the finish (a window's goal): what it carries on.</summary>
            public float Speed;
        }

        readonly Func<Simulation> _create;
        readonly Caps _caps;
        readonly OfflineSolver.ForcedPath _path;
        readonly Options _o;
        readonly Dictionary<Contact, TechPattern[]> _patterns = new Dictionary<Contact, TechPattern[]>();
        public long Ticks, Expanded, Edges, Stolen, StolenTicks, Cut;
        /// <summary>Stopwatch ticks spent in edge runs and in captures, all workers.</summary>
        public long RunTime, CaptureTime;
        public Route Best { get; private set; }
        public event Action<Route> Found;
        readonly Stopwatch _clock = new Stopwatch();
        /// <summary>The fastest straight-line speed an edge has made, units per physics tick: the bound's speed after a finish.</summary>
        float _vmax = 1f;

        public Planner(Func<Simulation> create, string capabilities, OfflineSolver.ForcedPath path, TechGrafts mined, Options options)
        {
            _create = create;
            _caps = Caps.Parse(capabilities);
            _path = path != null && !path.IsEmpty ? path : null;
            _o = options;
            if (mined != null)
                foreach (var g in mined.Patterns.Where(p => p.Requires.IsSubsetOf(_caps) && !p.Resets).GroupBy(p => p.Kind))
                    _patterns[g.Key] = g.ToArray();
            // The per-step tech contacts (ContactLog.Step) become nodes only when a mined window grafts at one of their kinds.
            _stepContacts = _patterns.Keys.Any(k => !Primitives.ContainsKey(k));
        }

        /// <summary>The planner's primitive library, per contact: the window its inputs start in relative to the contact (lo..hi),
        /// the plain inputs and the omnidash ones (null: all; the expansion's Grafts builds them). The solver grafts only mined
        /// tech windows (<see cref="TechGrafts"/>); the planner enumerates these as edges beside the mined windows.</summary>
        internal static readonly Dictionary<Contact, (int lo, int hi, string[] plain, string[] omni)> Primitives = new Dictionary<Contact, (int, int, string[], string[])>
        {
            [Contact.Died] = (-7, -1, new[] { "jump", "jump", "dash", "dashjump", "dashjump", "primed", "primed", "superbounce" }, null),
            [Contact.LeftGround] = (-4, 3, new[] { "jump", "jump", "dashjump", "dashjump", "backdash", "primed" },
                new[] { "wavedash", "ultra", "superground", "wavejump", "aim" }),
            [Contact.Landed] = (-4, 1, new[] { "jump", "dashjump", "dashjump", "primed", "primed", "dash" },
                new[] { "dashbounce", "wavebounce", "superground", "wavedash" }),
            [Contact.TouchedWall] = (-3, 3, new[] { "jump", "jump", "superbounce", "superbounce", "dash", "primed" }, new[] { "aim", "dashbounce" }),
            [Contact.LeftWall] = (0, 4, new[] { "jump", "jump", "dashjump" }, new[] { "aim", "wavejump" }),
            [Contact.CornerHyper] = (-3, 3, new[] { "dashjump" }, new[] { "cornerhyper", "cornerhyper", "cornerultra" }),
            [Contact.WallBehindLeft] = (0, 24, new[] { "wallclip" }, new[] { "wallclip" }),
            [Contact.WallBehindRight] = (0, 24, new[] { "wallclip" }, new[] { "wallclip" }),
            [Contact.WallAheadLeft] = (-2, 10, new[] { "turnaway" }, new[] { "turnaway" }),
            [Contact.WallAheadRight] = (-2, 10, new[] { "turnaway" }, new[] { "turnaway" }),
            [Contact.NearGateLeft] = (0, 0, new[] { "gatetap" }, new[] { "gatetap" }),
            [Contact.NearGateRight] = (0, 0, new[] { "gatetap" }, new[] { "gatetap" }),
            [Contact.WallAboveLeft] = (0, 0, new[] { "rest" }, new[] { "rest" }),
            [Contact.WallAboveRight] = (0, 0, new[] { "rest" }, new[] { "rest" }),
        };

        readonly bool _stepContacts;

        // ---- search state ----

        sealed class Node
        {
            public Node Parent;
            public InputTick[] Edge;
            public int Tick;
            public long Physics;
            public SimSnapshot State;
            public int Owner;
            public long Key;
            public int Progress;
            public float Distance, Speed, Guide;
            /// <summary>Velocity + momentum.</summary>
            public Vector2 V;
            /// <summary>The guide route this node lies exactly on (its inputs from the start are the route's), -2 for the start
            /// (on every guide), -1 for none; <see cref="GuideAt"/> is the route's input tick here.</summary>
            public int GuideRoute = -1, GuideAt;
            public Contact? Kind;
            public ContactLog.Mark Mark;
            public InputTick Last;
            public bool Ground, Wall, Dashes, Omni, AirJumps;
            public float Facing, Behind;
            public Vector2 At;
            public string How;

            /// <summary>The node's inputs from the start, into <paramref name="into"/> (cleared) or a new list.</summary>
            public List<InputTick> Inputs(List<InputTick> into = null)
            {
                var parts = new List<InputTick[]>();
                for (Node n = this; n.Parent != null; n = n.Parent) parts.Add(n.Edge);
                List<InputTick> all = into ?? new List<InputTick>(Tick + 1);
                all.Clear();
                for (int i = parts.Count - 1; i >= 0; i--) all.AddRange(parts[i]);
                return all;
            }
        }

        static bool Better(Node a, Node b)
        {
            if (a.Progress != b.Progress) return a.Progress > b.Progress;
            if (a.Guide != b.Guide) return a.Guide < b.Guide;
            float da = float.IsNaN(a.Distance) ? float.PositiveInfinity : a.Distance, db = float.IsNaN(b.Distance) ? float.PositiveInfinity : b.Distance;
            if (da != db) return da < db;
            if (a.Speed != b.Speed) return a.Speed > b.Speed;
            return a.Key < b.Key;
        }

        // ---- the guide routes' cost to go ----

        const float GuideCell = 64f;
        readonly Dictionary<long, List<(Vector2 at, Vector2 v, float cost, int route, int tick, long physics)>> _guide = new Dictionary<long, List<(Vector2, Vector2, float, int, int, long)>>();
        /// <summary>The guide routes that finish, as <see cref="_guide"/>'s points index them.</summary>
        readonly List<List<InputTick>> _guideRoutes = new List<List<InputTick>>();

        static long GuideKey(int stage, int gx, int gy) { return ((long)stage << 42) ^ ((long)(gx & 0x1FFFFF) << 21) ^ (gy & 0x1FFFFF); }

        /// <summary>The planner's stage: goals reached in goal mode; for a course its forced-path boxes passed and the timer
        /// running (its start gate is the first target); else the boxes passed.</summary>
        static int Stage(Simulation sim, int waypoints)
        {
            if (sim.Goals != null) return sim.Progress;
            if (sim.TimedFromRunStart) return waypoints;
            return 2 * waypoints + (sim.TimingCourse() != null ? 1 : 0);
        }

        /// <summary>Replays each guide route (its own thread and world) and files every input tick's place with the physics
        /// ticks it still had to its finish; a route that doesn't finish is left out.</summary>
        void BuildGuide(Action<string> log)
        {
            if (_o.Guides.Count == 0 && _o.Trails.Count == 0) return;
            Exception failure = null;
            var t = new Thread(() =>
            {
                try
                {
                    Simulation sim = _create();
                    foreach (List<InputTick> route in _o.Guides)
                    {
                        var points = new List<(int stage, Vector2 at, Vector2 v, long physics, int tick)>();
                        int waypoints = 0;
                        Action<int> onStep = _path == null ? null : _ =>
                        {
                            if (_path != null && waypoints < _path.Via.Count && _path.Via[waypoints].Contains(sim.PlayerWorld)) waypoints++;
                        };
                        RunResult r = sim.Run(route, null, tick =>
                        {
                            points.Add((Stage(sim, waypoints), sim.PlayerAtCapture.AsVector2(), sim.Player.Velocity + sim.Player.momentum, sim.Engine.PhysicsTicks, tick));
                            return true;
                        }, 1000000, onStep);
                        if (!r.Finished) { log?.Invoke($"guide route of {route.Count} inputs doesn't finish ({r.EndReason}): left out"); continue; }
                        int index = _guideRoutes.Count;
                        _guideRoutes.Add(route);
                        foreach (var (stage, at, v, physics, tick) in points)
                        {
                            long key = GuideKey(stage, Mathf.FloorToInt(at.x / GuideCell), Mathf.FloorToInt(at.y / GuideCell));
                            if (!_guide.TryGetValue(key, out var list)) _guide[key] = list = new List<(Vector2, Vector2, float, int, int, long)>();
                            list.Add((at, v, r.PhysicsTicks - physics, index, tick, physics));
                        }
                        log?.Invoke($"guide route {r.Time:0.00}s ({r.PhysicsTicks} physics ticks, {points.Count} points)");
                    }
                    sim.Restore(sim.Start);
                    foreach (Trail trail in _o.Trails) FileTrail(sim, trail, log);
                }
                catch (Exception ex) { failure = ex; }
            });
            t.Start();
            t.Join();
            if (failure != null) throw new InvalidOperationException("guide route replay failed", failure);
        }

        /// <summary>Route indexes (into <see cref="_guideRoutes"/>) that are trails: followed from near points, never exactly.</summary>
        readonly HashSet<int> _trailRoutes = new HashSet<int>();

        /// <summary>Finds the planner's goals along a trail (Full-TAS goals only) and files its points of the planned stages with the
        /// physics ticks the trail took from there to the last goal (+ the strict finish), timed as if it had started with the
        /// planner's start: <see cref="Trail"/>.</summary>
        void FileTrail(Simulation sim, Trail trail, Action<string> log)
        {
            FullTasTracker goals = sim.Goals;
            if (goals == null) { log?.Invoke($"trail {trail.Name}: not a Full-TAS plan, left out"); return; }
            // A window's start can come a few ticks before its first goal is logged (touched in a dash's no-input ticks).
            int first = Math.Max(sim.Progress, _o.FromGoal), limit = Math.Min(goals.Limit, goals.Goals.Length);
            long start = sim.Engine.PhysicsTicks;
            // reached[g]: the trail's physics tick when goal g (0-based) was reached.
            var reached = Enumerable.Repeat(-1L, goals.Goals.Length).ToArray();
            var stageAt = new int[trail.At.Length];
            int stage = 0;
            for (int p = 0; p < trail.At.Length; p++)
            {
                if (trail.Input[p] < 0) { stageAt[p] = -1; continue; }
                CapturePos c = sim.ToCapture(trail.At[p]);
                while (stage < limit)
                {
                    FullTasGoal g = goals.Goals[stage];
                    // The player's hitbox (about 41 x 72) touching the goal's rect (centred on x, y).
                    if (g.kind == FullTasGoal.At && !(Math.Abs(c.x - g.x) <= g.w / 2f + 21f && Math.Abs(c.y - g.y) <= g.h / 2f + 36f)) break;
                    reached[stage++] = p;
                }
                stageAt[p] = stage;
            }
            if (stage < limit || first > 0 && reached[first - 1] < 0)
            {
                log?.Invoke($"trail {trail.Name}: reaches goal {stage} of {limit} along its path, left out");
                return;
            }
            long from = first == 0 ? 0 : reached[first - 1], end = reached[limit - 1] + Simulation.StrictFinishTicks;
            int index = _guideRoutes.Count;
            _guideRoutes.Add(trail.Inputs);
            _trailRoutes.Add(index);
            int filed = 0;
            for (int p = (int)from; p < trail.At.Length && stageAt[p] < limit; p++)
            {
                if (stageAt[p] < first) continue;
                Vector2 at = sim.ToCapture(trail.At[p]).AsVector2();
                long key = GuideKey(stageAt[p], Mathf.FloorToInt(at.x / GuideCell), Mathf.FloorToInt(at.y / GuideCell));
                if (!_guide.TryGetValue(key, out var list)) _guide[key] = list = new List<(Vector2, Vector2, float, int, int, long)>();
                list.Add((at, trail.V[p], end - p, index, trail.Input[p], start + p - from));
                filed++;
            }
            log?.Invoke($"trail {trail.Name}: goals {first + 1}-{limit} in {end - from} physics ticks along it (strict finish included), {filed} points; "
                + $"its goals at physics ticks {string.Join(" ", reached.Take(limit))}, origin {sim.ToCapture(new WorldPos(0f, 0f)).x:0.#},{sim.ToCapture(new WorldPos(0f, 0f)).y:0.#}");
        }

        /// <summary>The estimated physics ticks to the finish from here with this velocity (Velocity + momentum): the best guide
        /// point of the stage within <see cref="Options.GuideRadius"/> (else three times it) the node isn't too early for
        /// (<see cref="Options.GuideAhead"/>), its ticks to go plus the distance at <see cref="Options.GuideSpeed"/> and the
        /// velocity change at <see cref="Options.GuideAccel"/>; 0 without guide routes, infinite away from them.</summary>
        float GuideCost(int stage, Vector2 at, Vector2 v, long physics)
        {
            if (_guide.Count == 0) return 0f;
            foreach (float radius in new[] { _o.GuideRadius, 3f * _o.GuideRadius })
            {
                int span = Mathf.CeilToInt(radius / GuideCell), gx = Mathf.FloorToInt(at.x / GuideCell), gy = Mathf.FloorToInt(at.y / GuideCell);
                float best = float.PositiveInfinity, r2 = radius * radius;
                for (int y = gy - span; y <= gy + span; y++)
                    for (int x = gx - span; x <= gx + span; x++)
                    {
                        if (!_guide.TryGetValue(GuideKey(stage, x, y), out var list)) continue;
                        foreach (var (p, pv, cost, _, _, when) in list)
                        {
                            float d2 = (p - at).sqrMagnitude;
                            if (d2 > r2 || when > physics + _o.GuideAhead) continue;
                            float c = cost + MathF.Sqrt(d2) / _o.GuideSpeed + Behind(pv, v) / _o.GuideAccel;
                            if (c < best) best = c;
                        }
                    }
                if (float.IsFinite(best)) return best;
            }
            return float.PositiveInfinity;
        }

        /// <summary>How far velocity <paramref name="v"/> is from a guide point's <paramref name="pv"/>, not counting speed beyond
        /// the point's own along its direction (a node faster than the guide the same way isn't behind it).</summary>
        static float Behind(Vector2 pv, Vector2 v)
        {
            Vector2 dv = pv - v;
            float speed = pv.magnitude;
            if (speed < 1f) return dv.magnitude;
            Vector2 u = pv / speed;
            float along = Vector2.Dot(dv, u);
            return Math.Max(0f, along) + (dv - along * u).magnitude;
        }

        /// <summary>Per guide route, its point nearest this place and velocity within <see cref="Options.FollowRadius"/> and
        /// <see cref="Options.FollowSpeed"/> (same stage): the input tick to follow it from.</summary>
        List<(int route, int tick)> Follows(int stage, Vector2 at, Vector2 v, long physics)
        {
            var found = new List<(int route, int tick, float score)>();
            if (_o.FollowTicks <= 0 || _guideRoutes.Count == 0) return new List<(int, int)>();
            float radius = _o.FollowRadius, r2 = radius * radius;
            int span = Mathf.CeilToInt(radius / GuideCell), gx = Mathf.FloorToInt(at.x / GuideCell), gy = Mathf.FloorToInt(at.y / GuideCell);
            for (int y = gy - span; y <= gy + span; y++)
                for (int x = gx - span; x <= gx + span; x++)
                {
                    if (!_guide.TryGetValue(GuideKey(stage, x, y), out var list)) continue;
                    foreach (var (p, pv, _, route, tick, when) in list)
                    {
                        float d2 = (p - at).sqrMagnitude, dv = (pv - v).magnitude, speed = _trailRoutes.Contains(route) ? _o.TrailFollowSpeed : _o.FollowSpeed;
                        if (d2 > r2 || dv > speed || when > physics + _o.GuideAhead) continue;
                        float score = MathF.Sqrt(d2) / radius + dv / speed;
                        int i = found.FindIndex(f => f.route == route);
                        if (i < 0) found.Add((route, tick, score));
                        else if (score < found[i].score) found[i] = (route, tick, score);
                    }
                }
            return found.Select(f => (f.route, f.tick)).ToList();
        }

        sealed class Layer
        {
            public readonly Dictionary<long, Node> ByKey = new Dictionary<long, Node>();
            /// <summary>Nodes exactly on a guide route (its inputs from the start): kept past the width and the labels, so the
            /// planner has every guide's own states to branch from and never ends slower than its best guide.</summary>
            public readonly List<Node> Guided = new List<Node>();

            public void AddGuided(Node n) { lock (this) Guided.Add(n); }
            Node _worst;

            /// <summary>Adds the node unless the layer is full of better ones (a same-key node keeps the better of the two).</summary>
            public bool Admits(Node n, int cap)
            {
                lock (this) return ByKey.TryGetValue(n.Key, out Node old) ? Better(n, old) : ByKey.Count < cap || _worst == null || Better(n, _worst);
            }

            public void Add(Node n, int cap)
            {
                lock (this)
                {
                    if (ByKey.TryGetValue(n.Key, out Node old))
                    {
                        if (!Better(n, old)) return;
                        ByKey[n.Key] = n;
                        if (ReferenceEquals(old, _worst)) _worst = null;
                    }
                    else if (ByKey.Count < cap) ByKey[n.Key] = n;
                    else
                    {
                        _worst ??= ByKey.Values.Aggregate((a, b) => Better(a, b) ? b : a);
                        if (!Better(n, _worst)) return;
                        ByKey.Remove(_worst.Key);
                        _worst.State = null;
                        ByKey[n.Key] = n;
                        _worst = null;
                    }
                    if (_worst != null && Better(_worst, n)) _worst = n;
                }
            }
        }

        readonly Dictionary<long, Layer> _layers = new Dictionary<long, Layer>();
        readonly object _layersGate = new object();
        // Written only between layers (single-threaded), read by the workers during one.
        Dictionary<long, List<(long g, float speed)>> _labels = new Dictionary<long, List<(long, float)>>();
        int _width;

        Layer LayerAt(long g)
        {
            lock (_layersGate)
            {
                if (!_layers.TryGetValue(g, out Layer l)) _layers[g] = l = new Layer();
                return l;
            }
        }

        bool Dominated(long key, long g, float speed)
        {
            if (!_labels.TryGetValue(key, out var labels)) return false;
            foreach (var (lg, ls) in labels) if (lg <= g && ls >= speed - SpeedSlack) return true;
            return false;
        }
        const float SpeedSlack = 25f;

        void Label(Node n)
        {
            if (!_labels.TryGetValue(n.Key, out var labels)) _labels[n.Key] = labels = new List<(long, float)>();
            if (labels.Count >= _o.Labels)
            {
                int slowest = 0;
                for (int i = 1; i < labels.Count; i++) if (labels[i].speed < labels[slowest].speed) slowest = i;
                labels.RemoveAt(slowest);
            }
            labels.Add((n.Physics, n.Speed));
        }

        bool Bounded(Node n)
        {
            Route best = Best;
            if (best == null || float.IsNaN(n.Distance) || float.IsInfinity(n.Distance)) return false;
            return n.Physics + n.Distance / _vmax >= best.RunTicks + _o.MomentumSlack;
        }

        readonly List<Route> _finishes = new List<Route>();

        /// <summary>A finish is kept when it is the fastest so far, or (<see cref="Options.MomentumSlack"/> physics ticks behind the
        /// fastest at most) no finish as early carries as much speed: the window's next goal is scored by arrival and momentum.</summary>
        void Offer(List<InputTick> inputs, float time, long runTicks, string how, float speed)
        {
            Route route;
            lock (_layersGate)
            {
                bool faster = Best == null || OfflineSolver.Beats(time, runTicks, Best.Time, Best.RunTicks);
                if (!faster && (runTicks > Best.RunTicks + _o.MomentumSlack || _finishes.Any(f => f.RunTicks <= runTicks && f.Speed >= speed))) return;
                route = new Route { Inputs = inputs, Time = time, RunTicks = runTicks, Ticks = Interlocked.Read(ref Ticks), Wall = _clock.Elapsed.TotalSeconds, Width = _width, How = how, Speed = speed };
                _finishes.Add(route);
                if (faster) Best = route;
            }
            Found?.Invoke(route);
        }

        void RaiseVmax(float v)
        {
            float old;
            while (v > (old = Volatile.Read(ref _vmax)) && Interlocked.CompareExchange(ref _vmax, v, old) != old) { }
        }

        // ---- the passes ----

        Worker[] _workers;
        Barrier _barrier;
        Node[] _work;
        int[] _claims;
        volatile bool _quit;

        /// <summary>Plans until <see cref="Options.Seconds"/> or <see cref="Options.MaxWidth"/> is done; the best route found (null: none).</summary>
        public Route Run(Action<string> log)
        {
            _clock.Start();
            int threads = Math.Max(1, _o.Threads);
            BuildGuide(log);
            _workers = new Worker[threads];
            _barrier = new Barrier(threads + 1);
            var ready = new CountdownEvent(threads);
            Exception failure = null;
            for (int i = 0; i < threads; i++)
            {
                int index = i;
                new Thread(() =>
                {
                    try { _workers[index] = new Worker(this, index, _create()); }
                    catch (Exception ex) { failure = ex; }
                    ready.Signal();
                    if (_workers[index] != null) _workers[index].Loop();
                }) { IsBackground = true, Name = "planner-" + i }.Start();
            }
            ready.Wait();
            if (failure != null) throw new InvalidOperationException("planner worker failed to load", failure);
            try
            {
                for (_width = _o.Width; _width <= _o.MaxWidth && _clock.Elapsed.TotalSeconds < _o.Seconds; _width *= 2)
                {
                    Route before = Best;
                    Pass(log);
                    log?.Invoke($"pass width {_width}: {(Best == null ? "no finish" : $"best {Best.Time:0.00}s ({Best.RunTicks} physics ticks, at {Best.Wall:0.0}s)")}, "
                        + $"{Interlocked.Read(ref Ticks)} ticks, {Expanded} nodes, {_clock.Elapsed.TotalSeconds:0.0}s");
                    if (_o.FirstOnly && Best != null) break;
                    if (before != null && Best == before && _width >= _o.MaxWidth) break;
                }
            }
            finally
            {
                _quit = true;
                _barrier.SignalAndWait();
            }
            return Best;
        }

        void Pass(Action<string> log)
        {
            lock (_layersGate) _layers.Clear();
            _labels = new Dictionary<long, List<(long, float)>>();
            // The root: every worker has the start (its own world's).
            _work = new[] { (Node)null };
            _claims = new int[1];
            _rootPending = true;
            Dispatch();
            double lastLog = _clock.Elapsed.TotalSeconds;
            while (!_quit && _clock.Elapsed.TotalSeconds < _o.Seconds)
            {
                long g;
                Layer layer;
                lock (_layersGate)
                {
                    if (_layers.Count == 0) break;
                    g = _layers.Keys.Min();
                    layer = _layers[g];
                    _layers.Remove(g);
                }
                var kept = new List<Node>();
                foreach (Node n in layer.ByKey.Values.OrderBy(n => n, Comparer<Node>.Create((a, b) => a == b ? 0 : Better(a, b) ? -1 : 1)))
                {
                    if (kept.Count >= _width || Dominated(n.Key, n.Physics, n.Speed) || Bounded(n)) { n.State = null; Interlocked.Increment(ref Cut); continue; }
                    Label(n);
                    kept.Add(n);
                }
                foreach (Node n in layer.Guided)
                {
                    if (Bounded(n)) { n.State = null; Interlocked.Increment(ref Cut); continue; }
                    Label(n);
                    kept.Add(n);
                }
                if (kept.Count == 0) continue;
                _work = kept.ToArray();
                _claims = new int[_work.Length];
                Dispatch();
                foreach (Node n in kept) n.State = null;
                if (_clock.Elapsed.TotalSeconds - lastLog >= 10)
                {
                    lastLog = _clock.Elapsed.TotalSeconds;
                    Node lead = kept[0];
                    log?.Invoke($"  layer {g}: {kept.Count} nodes, lead progress {lead.Progress} guide {lead.Guide:0} distance {lead.Distance:0} at input {lead.Tick}, "
                        + $"{Interlocked.Read(ref Ticks) / Math.Max(1, _clock.Elapsed.TotalSeconds):0} ticks/s, {Expanded} nodes, {Edges} edges, {Stolen} replayed ({StolenTicks} ticks), "
                        + $"run {RunTime * 1.0 / Stopwatch.Frequency:0}s capture {CaptureTime * 1.0 / Stopwatch.Frequency:0}s cpu, "
                        + (Best == null ? "no finish" : $"best {Best.Time:0.00}s"));
                }
            }
        }

        bool _rootPending;

        void Dispatch()
        {
            _barrier.SignalAndWait();
            _barrier.SignalAndWait();
        }

        // ---- workers ----

        sealed class Worker
        {
            readonly Planner _p;
            readonly int _index;
            readonly Simulation _sim;
            readonly Movement _player;
            readonly ContactLog _log = new ContactLog();
            readonly ContactProbe _gate;
            readonly DistanceField _field;
            readonly colouredBlockSwapper _swapper;
            readonly Func<int, bool> _afterTick;
            readonly Action<int> _onStep;

            public Worker(Planner p, int index, Simulation sim)
            {
                _p = p;
                _index = index;
                _sim = sim;
                _player = sim.Player;
                _gate = new ContactProbe(sim);
                _field = DistanceField.For(sim);
                if (sim.Goals == null && p._path != null) _field.SetVia(p._path.Via);
                if (p._path != null) _field.SetAvoid(p._path.Avoid);
                _swapper = sim.World.All<colouredBlockSwapper>().FirstOrDefault();
                _afterTick = AfterTick;
                _onStep = OnStep;
            }

            public void Loop()
            {
                while (true)
                {
                    _p._barrier.SignalAndWait();
                    if (_p._quit) return;
                    try { Work(); }
                    catch (Exception ex) { Console.Error.WriteLine("planner worker " + _index + ": " + ex); }
                    _p._barrier.SignalAndWait();
                }
            }

            void Work()
            {
                Node[] work = _p._work;
                if (work.Length == 1 && work[0] == null)
                {
                    if (_index == 0 && Interlocked.Exchange(ref _p._claims[0], 1) == 0) Root();
                    return;
                }
                // Own nodes first (their snapshots are this world's), then any left: replayed from the start.
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < work.Length; i++)
                    {
                        Node n = work[i];
                        if (pass == 0 && n.Owner != _index) continue;
                        if (Interlocked.Exchange(ref _p._claims[i], 1) != 0) continue;
                        Expand(n);
                    }
            }

            void Root()
            {
                _sim.Restore(_sim.Start);
                _log.Reset();
                var root = new Node { Tick = 0, Owner = -1, State = _sim.Start, How = "start", Physics = _sim.Engine.PhysicsTicks, GuideRoute = -2 };
                Describe(root, _sim.Start.Waypoints);
                root.Mark = _log.Save();
                Expand(root);
            }

            // ---- expansion ----

            // The expanded node's inputs, then an edge's after them (Edge cuts it back to the node's _prefix ticks): no run keeps it.
            readonly List<InputTick> _inputs = new List<InputTick>();
            int _prefix;
            Node _node;
            int _commit, _capAt, _waypoints, _lastTick;
            bool _avoided, _replaying;
            Contact? _hit;
            int _hitTick;
            string _how;
            SimSnapshot _captured;

            void Expand(Node n)
            {
                Interlocked.Increment(ref _p.Expanded);
                SimSnapshot state = n.Tick == 0 ? _sim.Start : n.Owner == _index && n.State != null ? n.State : Replay(n);
                if (state == null) return;
                if (n.Tick > 0) Cache(n, state);
                _prefix = n.Inputs(_inputs).Count;
                _node = n;
                var predicted = new List<(InputTick coast, int tick, Contact kind)>();
                foreach (var (ticks, how, coast) in Decisions(n))
                {
                    Edge(state, ticks, how);
                    if (coast && _hit is Contact kind) predicted.Add((ticks[0], _hitTick, kind));
                }
                // The node's own contact: its window's later ticks (a coyote jump off a ledge, a wall clip once clip-ready).
                if (n.Kind is Contact own && Primitives.TryGetValue(own, out var mine))
                    foreach (int o in Offsets(1, mine.hi))
                        foreach (var (ticks, how) in Grafts(n, own, mine, n.Last, o))
                            Edge(state, Concat(Coast(n.Last, o), ticks), how);
                // The contacts the coasts run into: their windows' earlier ticks (a buffered jump or dash before a landing,
                // a wall, a death).
                foreach (var (coast, tick, kind) in predicted)
                {
                    if (!Primitives.TryGetValue(kind, out var at)) continue;
                    foreach (int o in Offsets(at.lo, -1))
                    {
                        int lead = tick + o - n.Tick;
                        if (lead <= 0) continue;
                        foreach (var (ticks, how) in Grafts(n, kind, at, coast, o))
                            Edge(state, Concat(Coast(coast, lead), ticks), how);
                    }
                    Mined(n, state, coast, tick, kind);
                }
                if (n.Kind is Contact k) Mined(n, state, n.Last, n.Tick, k);
                // The guide routes' own inputs: exactly on from a node on one (its next ticks, the node after it on it too), and
                // from the point of each route nearest this state.
                for (int route = 0; route < _p._guideRoutes.Count; route++)
                    if ((n.GuideRoute == -2 || n.GuideRoute == route) && !_p._trailRoutes.Contains(route)) Follow(state, route, n.GuideRoute == -2 ? 0 : n.GuideAt, true);
                foreach (var (route, at) in _p.Follows(n.Progress, n.At, n.V, n.Physics))
                    if (_p._trailRoutes.Contains(route) || n.GuideRoute != -2 && n.GuideRoute != route) Follow(state, route, at, false);
                _node = null;
            }

            // The guide edge being run: its route and the offset from the planner's input ticks to the route's (-1: not exact).
            int _edgeGuide = -1, _edgeGuideOffset;

            /// <summary>A guide route's next <see cref="Options.FollowTicks"/> inputs from its input tick <paramref name="at"/>, then
            /// its own ticks on to the next node (not a coast), as they are when <paramref name="exact"/>.</summary>
            void Follow(SimSnapshot state, int route, int at, bool exact)
            {
                List<InputTick> g = _p._guideRoutes[route];
                int count = Math.Min(_p._o.FollowTicks, g.Count - at);
                if (count <= 0) return;
                int tail = Math.Min(_p._o.Cap + 2, g.Count - at - count);
                _edgeGuide = exact ? route : -1;
                _edgeGuideOffset = at - _node.Tick;
                Edge(state, g.GetRange(at, count).ToArray(), (exact ? "guide " : "follow ") + route + "@" + at, g.GetRange(at + count, tail), exact);
                _edgeGuide = -1;
            }

            static IEnumerable<int> Offsets(int lo, int hi)
            {
                // A wide window (a wall clip's 0..24) is sampled, a narrow one taken whole.
                if (hi - lo <= 8) { for (int o = lo; o <= hi; o++) yield return o; yield break; }
                foreach (int o in new[] { 0, 1, 2, 4, 8, 12, 16, 24 }) if (o >= lo && o <= hi) yield return o;
            }

            void Mined(Node n, SimSnapshot state, InputTick coast, int tick, Contact kind)
            {
                if (!_p._patterns.TryGetValue(kind, out TechPattern[] patterns)) return;
                foreach (TechPattern p in patterns)
                {
                    int start = tick + p.First, skip = Math.Max(0, n.Tick - start);
                    if (skip >= p.Ticks.Length) continue;
                    start += skip;
                    var ticks = new List<InputTick>();
                    for (int i = skip; i < p.Ticks.Length; i++) ticks.Add(p.Ticks[i]);
                    InputTick before = start > n.Tick ? coast.Coast() : n.Last;
                    ticks[0] = Edge0(before, ticks[0]);
                    Edge(state, Concat(Coast(coast, start - n.Tick), ticks.ToArray()), p.Mode);
                }
            }

            /// <summary>A window's first tick after <paramref name="before"/>: its button edges made real (TechGrafts.Compose).</summary>
            static InputTick Edge0(InputTick before, InputTick first)
            {
                bool down = before.held;
                if (down && !first.held && !first.release) first.release = true;
                else if (!down && first.release && !first.press) first.release = false;
                if (!down && first.held && !first.press) first.press = true;
                return first;
            }

            static InputTick[] Coast(InputTick last, int count)
            {
                var c = new InputTick[Math.Max(0, count)];
                InputTick k = last.Coast();
                for (int i = 0; i < c.Length; i++) c[i] = k;
                return c;
            }

            static InputTick[] Concat(InputTick[] a, InputTick[] b)
            {
                var c = new InputTick[a.Length + b.Length];
                a.CopyTo(c, 0);
                b.CopyTo(c, a.Length);
                return c;
            }

            static InputTick Stick(float x, float y, bool held, bool press = false, bool release = false)
            {
                return new InputTick { x = x, y = y, held = held || press, press = press, release = release && !held && !press };
            }

            static readonly float[] Xs = { -1f, 0f, 1f };
            static readonly int[] DashJumps = { 3, 5, 7, 9 }, OmniJumps = { 4, 8 }, MidAirJumps = { 3, 7 };
            static readonly (float x, float y)[] Aims = { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) };

            /// <summary>The decisions at the node's own tick: every stick (x -1/0/1, y 0 or down in the air, up on a wall), the
            /// jump pressed, kept or released, and a dash with its jump frame (the omnidash: its eight aims and the freeze's steep
            /// aim). Coasts are flagged: their first contact is where the buffered windows go.</summary>
            IEnumerable<(InputTick[] ticks, string how, bool coast)> Decisions(Node n)
            {
                bool down = n.Last.held && !n.Last.release;
                var ys = new List<float> { 0f };
                if (!n.Ground) ys.Add(-1f);
                if (n.Wall) ys.Add(1f);
                foreach (float x in Xs)
                    foreach (float y in ys)
                    {
                        if (down)
                        {
                            yield return (new[] { Stick(x, y, true) }, "hold", true);
                            yield return (new[] { Stick(x, y, false, release: true) }, "release", true);
                            // Released and pressed again on the next tick.
                            yield return (new[] { Stick(x, y, false, release: true), Stick(x, y, true, press: true) }, "jump", false);
                        }
                        else
                        {
                            yield return (new[] { Stick(x, y, false) }, "coast", true);
                            yield return (new[] { Stick(x, y, true, press: true) }, "jump", false);
                        }
                    }
                if (_p._o.MidAir && !n.Ground && !n.Wall)
                    foreach (int d in Options.MidAirDelays)
                    {
                        if (d >= _p._o.Cap) continue;
                        InputTick c = n.Last.Coast();
                        InputTick[] coast = Coast(n.Last, d);
                        if (down) yield return (Concat(coast, new[] { Stick(c.x, c.y, false, release: true) }), "jump cut +" + d, false);
                        else if (n.AirJumps) yield return (Concat(coast, new[] { Stick(c.x, c.y, true, press: true) }), "double jump +" + d, false);
                        foreach (float x in Xs)
                            if (x != c.x) yield return (Concat(coast, new[] { Stick(x, c.y, down) }), "stick +" + d, false);
                        if (!_p._caps.Dash || !n.Dashes || n.Omni) continue;
                        foreach (float dd in new[] { -1f, 1f })
                        {
                            yield return (Concat(coast, new[] { Dash(Stick(dd, c.y, down), 0) }), "dash +" + d, false);
                            foreach (int j in MidAirJumps)
                                foreach (float lockX in new[] { dd, -dd })
                                {
                                    InputTick t = Dash(Stick(dd, c.y, down), j);
                                    t.lockX = lockX;
                                    yield return (Concat(coast, new[] { t, Stick(dd, c.y, true) }), (lockX == dd ? "super +" : "reverse super +") + d, false);
                                }
                        }
                    }
                if (!_p._caps.Dash || !(n.Ground || n.Wall || n.Dashes)) yield break;
                if (!n.Omni)
                {
                    // A dash each way; with a jump on one of its frames the lock's stick (MacroStep.lockMoveX) aims the super
                    // jump: the dash's way or back (a reversed super).
                    foreach (float d in new[] { -1f, 1f })
                        foreach (float y in ys)
                        {
                            yield return (new[] { Dash(Stick(d, y, down), 0) }, "dash", false);
                            foreach (int j in DashJumps)
                                foreach (float lockX in new[] { d, -d })
                                {
                                    InputTick t = Dash(Stick(d, y, down), j);
                                    t.lockX = lockX;
                                    yield return (new[] { t, Stick(d, y, true) }, lockX == d ? "super" : "reverse super", false);
                                }
                        }
                    if (!down)
                        foreach (float side in new[] { -1f, 1f }) yield return (new[] { Dash(Stick(side, 0f, true, press: true), 0) }, "dash press", false);
                    yield break;
                }
                foreach (var (ax, ay) in Aims)
                    foreach (float side in new[] { -1f, 1f })
                    {
                        if (ax != 0f && ax != side) continue;
                        yield return (OmniDash(ax, ay, 0, side, down), "aim", false);
                        foreach (int j in OmniJumps) yield return (OmniDash(ax, ay, j, side, down), "aim jump", false);
                    }
                foreach (float side in new[] { -1f, 1f })
                    foreach (float up in new[] { .94f, -.94f })
                        foreach (int j in new[] { 0, 4 })
                        {
                            InputTick[] t = OmniDash(0f, 0f, j, side, down);
                            t[0].turnX = side * .35f;
                            t[0].turnY = up;
                            yield return (t, "steep", false);
                        }
                // The down-dash landing chain (the old VMAN route's goals 15-17): a dash straight down with a jump frame, the jump
                // out of the landing held a few ticks, some air, again; the momentum grows at each landing, which a node per
                // bounce ranks too late to keep.
                if (_p._o.Chains)
                    foreach (float side in new[] { -1f, 1f })
                        foreach (int links in ChainLinks)
                            foreach (int gap in ChainGaps)
                                foreach (int j in ChainJumps)
                                    yield return (BounceChain(side, links, gap, j, down), $"bounce chain {links}x gap {gap} frame {j}", false);
            }

            static readonly int[] ChainLinks = { 2, 4 }, ChainGaps = { 3, 7 }, ChainJumps = { 3, 6 };

            /// <summary><paramref name="links"/> dashes straight down (jump frame <paramref name="jump"/>, the stick sideways after),
            /// the jump held 3 ticks and released, then <paramref name="gap"/> ticks of air, the stick <paramref name="side"/>.</summary>
            static InputTick[] BounceChain(float side, int links, int gap, int jump, bool down)
            {
                var t = new List<InputTick>();
                for (int k = 0; k < links; k++)
                {
                    InputTick d = Dash(Stick(0f, -1f, down && k == 0), jump);
                    d.lockX = side;
                    t.Add(d);
                    for (int i = 0; i < 3; i++) t.Add(Stick(side, 0f, true));
                    t.Add(Stick(side, 0f, false, release: true));
                    for (int i = 0; i < gap; i++) t.Add(Stick(side, 0f, false));
                }
                return t.ToArray();
            }

            static InputTick Dash(InputTick t, int jump)
            {
                t.dash = true;
                t.dashJump = jump;
                if (jump > 0) t.press = false;
                return t;
            }

            /// <summary>OfflineSolver.OmniDash's shape: the dash aimed (x, y) with its jump frame, then the stick sideways for the lock,
            /// the jump held a few ticks after a dash jump; a dash jump's lock stick sideways too (the super's way).</summary>
            static InputTick[] OmniDash(float x, float y, int jump, float side, bool down)
            {
                bool held = jump > 0;
                InputTick dash = Dash(Stick(x, y, down), jump);
                if (held) dash.lockX = side;
                return new[]
                {
                    dash,
                    Stick(side, 0f, held, release: down && !held),
                    Stick(side, 0f, held),
                };
            }

            /// <summary>The primitive inputs for this contact at offset <paramref name="o"/> from it, on the coast's stick: the jump
            /// (a buffered, coyote or wall jump), the dash and dash jump, the primed jump into a dash, the superbounce, the
            /// backwards dash-jump, the wall clip, the turn away and rest that set one up, the gate tap, the omnidash kinds.</summary>
            IEnumerable<(InputTick[] ticks, string how)> Grafts(Node n, Contact contact, (int lo, int hi, string[] plain, string[] omni) window, InputTick coast, int o)
            {
                bool down = coast.held && !coast.release;
                float side = coast.x != 0f ? Math.Sign(coast.x) : n.Facing;
                string suffix = " at " + contact + (o >= 0 ? "+" : "") + o;
                var kinds = new List<string>();
                if (!_p._caps.Dash) kinds.Add("jump");
                else
                {
                    kinds.AddRange(window.plain);
                    if (n.Omni) kinds.AddRange(window.omni ?? new[] { "aim", "wavedash", "ultra", "dashbounce", "wavebounce", "wavejump", "superground" });
                }
                if (contact is Contact.WallBehindLeft or Contact.WallBehindRight) { kinds.Clear(); kinds.Add("wallclip"); }
                foreach (string kind in kinds.Distinct())
                {
                    float y = coast.y;
                    switch (kind)
                    {
                        case "jump":
                            yield return (down ? new[] { Stick(coast.x, y, false, release: true), Stick(coast.x, y, true, press: true) }
                                : new[] { Stick(coast.x, y, true, press: true) }, "jump" + suffix);
                            break;
                        case "dash":
                            yield return (new[] { Dash(Stick(coast.x, y, down), 0) }, "dash" + suffix);
                            break;
                        case "dashjump":
                            foreach (int j in new[] { 1, 3 })
                            {
                                InputTick t = Dash(Stick(coast.x, y, down), j);
                                t.lockX = side;
                                yield return (new[] { t, Stick(coast.x, y, true) }, "dash jump" + suffix);
                            }
                            break;
                        case "primed":
                            if (down) break;
                            foreach (int k in new[] { 2, 4 })
                            {
                                var t = new List<InputTick> { Stick(coast.x, y, true, press: true) };
                                for (int i = 1; i < k; i++) t.Add(Stick(coast.x, y, true));
                                t.Add(Dash(Stick(coast.x, y, true), 0));
                                yield return (t.ToArray(), "primed" + suffix);
                            }
                            break;
                        case "superbounce":
                            if (down) break;
                            foreach (int d in new[] { 0, 2, 4 })
                            {
                                var t = new List<InputTick> { Dash(Stick(side, 0f, d == 0, press: d == 0), 0) };
                                for (int i = 1; i <= d; i++) t.Add(Stick(side, 0f, i == d, press: i == d));
                                for (int i = 0; i < 3; i++) t.Add(Stick(side, 0f, true));
                                yield return (t.ToArray(), "superbounce" + suffix);
                            }
                            break;
                        case "backdash":
                            if (down) break;
                            yield return (new[] { Dash(Stick(-side, 0f, true, press: true), 0), Stick(-side, 0f, true), Stick(-side, 0f, true), Stick(-side, 0f, true) }, "backdash" + suffix);
                            break;
                        case "wallclip":
                        {
                            float toward = contact == Contact.WallBehindLeft ? -1f : contact == Contact.WallBehindRight ? 1f : n.Behind;
                            if (toward == 0f) break;
                            foreach (float back in new[] { .5f, 1f })
                                foreach (int hold in new[] { 6, 16 })
                                {
                                    var t = new List<InputTick> { Stick(toward * .45f, 0f, false, release: down), Stick(-toward * back, 0f, true, press: true) };
                                    for (int i = 0; i < hold; i++) t.Add(Stick(0f, 0f, true));
                                    t.Add(Stick(0f, 0f, false, release: true));
                                    yield return (t.ToArray(), "wall clip" + suffix);
                                }
                            break;
                        }
                        case "turnaway":
                        case "rest":
                        {
                            float wall = contact is Contact.WallAheadLeft or Contact.WallAboveLeft ? -1f : 1f;
                            int turn = kind == "rest" ? 0 : 2;
                            foreach (int rest in new[] { 6, 16 })
                            {
                                var t = new List<InputTick>();
                                for (int i = 0; i < turn; i++) t.Add(Stick(-wall * .5f, 0f, down));
                                for (int i = 0; i < rest; i++) t.Add(Stick(0f, 0f, down));
                                yield return (t.ToArray(), kind + suffix);
                            }
                            break;
                        }
                        case "gatetap":
                        {
                            float gate = contact == Contact.NearGateLeft ? -1f : 1f;
                            foreach (int delay in new[] { 0, 8 })
                            {
                                var t = new List<InputTick>();
                                for (int i = 0; i < delay; i++) t.Add(Stick(0f, 0f, down));
                                t.Add(Stick(gate * .5f, 0f, down));
                                t.Add(Stick(-gate * .5f, 0f, down));
                                for (int i = 0; i < 6; i++) t.Add(Stick(0f, 0f, down));
                                yield return (t.ToArray(), "gate tap" + suffix);
                            }
                            break;
                        }
                        case "aim":
                            foreach (var (ax, ay) in Aims) yield return (OmniDash(ax, ay, 0, ax != 0f ? ax : side, down), "aim" + suffix);
                            break;
                        case "wavedash":
                            yield return (OmniDash(side, -1f, 6, side, down), "wavedash" + suffix);
                            break;
                        case "ultra":
                        {
                            InputTick[] a = OmniDash(side, -1f, 6, side, down), b = OmniDash(side, -1f, 6, side, true);
                            yield return (Concat(Concat(a, Coast(a[a.Length - 1], 2)), b), "ultra" + suffix);
                            break;
                        }
                        case "dashbounce":
                            yield return (OmniDash(0f, -1f, 3, side, down), "dash bounce" + suffix);
                            break;
                        case "wavebounce":
                        {
                            InputTick[] a = OmniDash(side, -1f, 6, side, down), b = OmniDash(0f, -1f, 3, side, true);
                            yield return (Concat(Concat(a, Coast(a[a.Length - 1], 4)), b), "wave bounce" + suffix);
                            break;
                        }
                        case "wavejump":
                            yield return (OmniDash(side, 1f, 0, side, down), "wavejump" + suffix);
                            yield return (OmniDash(side, 1f, 5, side, down), "wavejump" + suffix);
                            break;
                        case "superground":
                        {
                            InputTick[] a = OmniDash(side, 0f, 3, side, down), b = OmniDash(0f, -1f, 3, side, true);
                            yield return (Concat(Concat(a, Coast(a[a.Length - 1], 6)), b), "super ground bounce" + suffix);
                            break;
                        }
                        case "cornerhyper":
                        case "cornerultra":
                            foreach (int j in new[] { 5, 8, 11 }) yield return (OmniDash(side, -1f, j, side, down), "corner hyper" + suffix);
                            break;
                    }
                }
            }

            // ---- one edge ----

            void Edge(SimSnapshot state, InputTick[] ticks, string how, List<InputTick> tail = null, bool raw = false)
            {
                if (ticks.Length == 0) return;
                Node n = _node;
                List<InputTick> inputs = _inputs;
                inputs.RemoveRange(_prefix, inputs.Count - _prefix);
                if (!raw) ticks[0] = Edge0(n.Last, ticks[0]);
                inputs.AddRange(ticks);
                // After the edge's own ticks: a guide's next ticks, else its last stick and button held.
                if (tail != null) inputs.AddRange(tail);
                InputTick coast = inputs[inputs.Count - 1].Coast();
                for (int i = tail?.Count ?? 0; i < _p._o.Cap + 2; i++) inputs.Add(coast);
                _commit = n.Tick + ticks.Length;
                _capAt = n.Tick + Math.Max(ticks.Length, _p._o.Cap);
                _log.Load(n.Mark);
                _seen = 0;
                _waypoints = state.Waypoints;
                _avoided = false;
                _hit = null;
                _hitTick = -1;
                _lastTick = n.Tick;
                _how = how;
                _replaying = false;
                _runInputs = inputs;
                Interlocked.Increment(ref _p.Edges);
                long t0 = Stopwatch.GetTimestamp();
                RunResult r = _sim.Run(inputs, state, _afterTick, 1, _onStep);
                Interlocked.Add(ref _p.RunTime, Stopwatch.GetTimestamp() - t0);
                Interlocked.Add(ref _p.Ticks, r.Stepped);
                _runInputs = null;
                if (r.Finished && !_avoided && (_p._path == null || _waypoints == _p._path.Via.Count))
                {
                    _p.Offer(inputs.GetRange(0, r.FinishTick + 1), r.Time, r.PhysicsTicks, how, (_player.Velocity + _player.momentum).magnitude);
                    return;
                }
                if (r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal) && _hit == null)
                {
                    _hit = Contact.Died;
                    _hitTick = _lastTick + 1;
                }
            }

            List<InputTick> _runInputs;

            void OnStep(int step)
            {
                if (_node != null && _node.Omni && !_replaying) _log.Slide((int)_sim.Engine.RunTicks, _player);
                if (_p._stepContacts && !_replaying) _log.Step(_sim);
                OfflineSolver.ForcedPath path = _p._path;
                if (path == null) return;
                WorldPos p = _sim.PlayerWorld;
                if (path.Step(p, _sim.Progress, ref _waypoints)) _avoided = true;
            }

            bool AfterTick(int tick)
            {
                if (_avoided) return false;
                if (_replaying) return ReplayTick(tick);
                _lastTick = tick;
                int before = _seen;
                _log.Tick(tick, _player, _gate.NearGate());
                _seen = _log.Contacts.Count;
                if (tick < _commit) return true;
                // The first contact since the last input tick (a corner hyper's slide is filed on its dash's tick, in between).
                Contact? hit = before < _seen ? _log.Contacts[before].kind : null;
                if (hit == null && tick < _capAt) return true;
                _hit = hit;
                _hitTick = tick;
                Route best = _p.Best;
                if (best != null && _sim.Elapsed(_sim.TimingCourse()) > best.Time + _p._o.MomentumSlack * _sim.World.FixedDeltaTime + .0001f) return false;
                Child(tick, hit);
                return false;
            }

            void Child(int tick, Contact? kind)
            {
                Node parent = _node;
                long physics = _sim.Engine.PhysicsTicks;
                var n = new Node { Tick = tick, Physics = physics, Kind = kind, How = _how, Last = _runInputs[tick - 1] };
                Describe(n, _waypoints);
                CapturePos at = _sim.PlayerAtCapture;
                float moved = physics > parent.Physics ? Vector2.Distance(n.At, parent.At) / (physics - parent.Physics) : 0f;
                if (moved > 0f) _p.RaiseVmax(moved);
                bool guided = _edgeGuide >= 0;
                if (guided) { n.GuideRoute = _edgeGuide; n.GuideAt = tick + _edgeGuideOffset; }
                else if (_p.Dominated(n.Key, physics, n.Speed)) return;
                Layer layer = _p.LayerAt(physics);
                if (!guided && !layer.Admits(n, _p._width * 3)) return;
                n.Parent = parent;
                n.Edge = _runInputs.GetRange(parent.Tick, tick - parent.Tick).ToArray();
                n.Last = n.Edge.Length > 0 ? n.Edge[n.Edge.Length - 1] : parent.Last;
                n.Mark = _log.Save();
                n.Owner = _index;
                long c0 = Stopwatch.GetTimestamp();
                n.State = _sim.Capture(tick);
                Interlocked.Add(ref _p.CaptureTime, Stopwatch.GetTimestamp() - c0);
                n.State.Waypoints = _waypoints;
                if (guided) layer.AddGuided(n);
                else layer.Add(n, _p._width * 3);
            }

            int _seen;

            /// <summary>The node's key, rank and the flags its decisions read, from the player now.</summary>
            void Describe(Node n, int waypoints)
            {
                Movement m = _player;
                CapturePos at = _sim.PlayerAtCapture;
                n.At = at.AsVector2();
                bool timing = _sim.TimingCourse() != null;
                if (_sim.Goals != null) { n.Progress = _sim.Progress; n.Distance = _field.Distance(at, waypoints); }
                else if (_sim.TimedFromRunStart) { n.Progress = waypoints; n.Distance = _field.Distance(at, waypoints); }
                else
                {
                    // A course: its start gate first (an end gate reached with the timer off finishes nothing), then its
                    // forced-path boxes and end gate.
                    n.Progress = 2 * waypoints + (timing ? 1 : 0);
                    n.Distance = timing ? _field.Distance(at, waypoints) : _field.StartGateDistance(at);
                }
                Vector2 v = m.Velocity, mo = m.momentum;
                n.Speed = v.magnitude + mo.magnitude;
                n.V = v + mo;
                n.Guide = _p.GuideCost(n.Progress, n.At, v + mo, n.Physics);
                n.Ground = m.onGround;
                n.Wall = m.OnWall;
                n.Dashes = m.airDashesLeft > 0;
                n.AirJumps = m.airJumpsLeft > 0;
                n.Omni = m.omniDashUnlocked;
                n.Facing = m.facingRight ? 1f : -1f;
                n.Behind = ContactRays.WallBehind(m);
                ulong h = 14695981039346656037UL;
                void Mix(long x) { h ^= (ulong)x; h *= 1099511628211UL; h ^= h >> 29; }
                float cell = _p._o.Cell;
                Mix(Mathf.FloorToInt(at.x / cell));
                Mix(Mathf.FloorToInt(at.y / cell));
                Mix(Mathf.FloorToInt(v.x / 100f));
                Mix(Mathf.FloorToInt(v.y / 150f));
                Mix(Mathf.FloorToInt(mo.x / 250f));
                Mix(Mathf.FloorToInt(mo.y / 250f));
                Mix((m.onGround ? 1 : 0) | (m.OnWall ? 2 : 0) | (m.dashActive ? 4 : 0) | (m.facingRight ? 8 : 0) | (timing ? 16 : 0)
                    | (n.Last.held && !n.Last.release ? 32 : 0) | ((object)_swapper != null && _swapper.isBlueActive ? 64 : 0));
                Mix(m.airJumpsLeft);
                Mix(m.airDashesLeft);
                Mix(m.wallJumpsLeft);
                Mix(n.Progress);
                n.Key = (long)h;
            }

            // Snapshots of nodes this worker expanded or replayed through (this world's): a replay starts from the nearest one.
            readonly Dictionary<Node, SimSnapshot> _cache = new Dictionary<Node, SimSnapshot>(ReferenceEqualityComparer.Instance);
            readonly Queue<Node> _cached = new Queue<Node>();
            const int CacheSize = 1024;
            List<Node> _replayPath;
            int _replayNext;

            void Cache(Node n, SimSnapshot s)
            {
                if (s == null || _cache.ContainsKey(n)) return;
                _cache[n] = s;
                _cached.Enqueue(n);
                while (_cache.Count > CacheSize) _cache.Remove(_cached.Dequeue());
            }

            /// <summary>Another worker's node: its inputs replayed to its tick from the nearest ancestor this world has a
            /// snapshot of (or the start), keeping the ancestors passed on the way.</summary>
            SimSnapshot Replay(Node n)
            {
                var path = new List<Node> { n };
                SimSnapshot from = null;
                for (Node a = n.Parent; a != null && a.Tick > 0; a = a.Parent)
                {
                    if (_cache.TryGetValue(a, out from)) break;
                    path.Add(a);
                }
                path.Reverse();
                List<InputTick> inputs = n.Inputs();
                inputs.Add(inputs.Count > 0 ? inputs[inputs.Count - 1].Coast() : new InputTick());
                _replaying = true;
                _replayPath = path;
                _replayNext = 0;
                _captured = null;
                _waypoints = from?.Waypoints ?? 0;
                _avoided = false;
                _node = null;
                RunResult r = _sim.Run(inputs, from, _afterTick, 1, _onStep);
                _replaying = false;
                _replayPath = null;
                Interlocked.Increment(ref _p.Stolen);
                Interlocked.Add(ref _p.StolenTicks, r.Stepped);
                Interlocked.Add(ref _p.Ticks, r.Stepped);
                SimSnapshot s = _captured;
                _captured = null;
                return s;
            }

            bool ReplayTick(int tick)
            {
                Node next = _replayPath[_replayNext];
                if (tick < next.Tick) return true;
                SimSnapshot s = _sim.Capture(tick);
                s.Waypoints = _waypoints;
                Cache(next, s);
                if (++_replayNext < _replayPath.Count) return true;
                _captured = s;
                return false;
            }
        }
    }
}
