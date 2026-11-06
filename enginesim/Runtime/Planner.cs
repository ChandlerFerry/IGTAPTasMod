using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class Planner
    {
        public sealed class Options
        {
            public int Threads = Environment.ProcessorCount;
            public int Width = 48, MaxWidth = 768;
            public int Labels = 2;
            public float Cell = 16f;
            public int Cap = 8;
            public bool MidAir = true;
            public static readonly int[] MidAirDelays = { 2, 4, 6 };
            public bool Chains = true;
            public int FromGoal = -1;
            public int MomentumSlack;
            public double Seconds = 300;
            public bool FirstOnly;
            public List<List<InputTick>> Guides = new List<List<InputTick>>();
            public float GuideRadius = 160f, GuideSpeed = 10f, GuideAccel = 60f;
            public int FollowTicks = 16;
            public float FollowRadius = 48f, FollowSpeed = 200f;
            public int GuideAhead = 60;
            public List<Trail> Trails = new List<Trail>();
            public float TrailFollowSpeed = 400f;
        }

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
            public float Speed;
        }

        readonly Func<Simulation> _create;
        readonly Caps _caps;
        readonly OfflineSolver.ForcedPath _path;
        readonly Options _o;
        readonly Dictionary<Contact, TechPattern[]> _patterns = new Dictionary<Contact, TechPattern[]>();
        public long Ticks, Expanded, Edges, Stolen, StolenTicks, Cut;
        public long RunTime, CaptureTime;
        public Route Best { get; private set; }
        public event Action<Route> Found;
        readonly Stopwatch _clock = new Stopwatch();
        float _vmax = 1f;

        public Planner(Func<Simulation> create, string capabilities, OfflineSolver.ForcedPath path, TechGrafts mined, Options options)
        {
            _create = create;
            _caps = Caps.Parse(capabilities);
            _path = path != null && !path.IsEmpty ? path : null;
            _o = options;
            if (mined != null)
                foreach (var g in mined.Patterns.Where(p => p.Requires.IsSubsetOf(_caps) && !p.Resets && p.Category != TechGrafts.RouteCategory).GroupBy(p => p.Kind))
                    _patterns[g.Key] = g.ToArray();
            _stepContacts = _patterns.Keys.Any(k => !Primitives.ContainsKey(k));
        }

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
            public Vector2 V;
            public int GuideRoute = -1, GuideAt;
            public Contact? Kind;
            public ContactLog.Mark Mark;
            public InputTick Last;
            public bool Ground, Wall, Dashes, Omni, AirJumps;
            public float Facing, Behind;
            public Vector2 At;
            public string How;

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

        const float GuideCell = 64f;
        readonly Dictionary<long, List<(Vector2 at, Vector2 v, float cost, int route, int tick, long physics)>> _guide = new Dictionary<long, List<(Vector2, Vector2, float, int, int, long)>>();
        readonly List<List<InputTick>> _guideRoutes = new List<List<InputTick>>();

        static long GuideKey(int stage, int gx, int gy) { return ((long)stage << 42) ^ ((long)(gx & 0x1FFFFF) << 21) ^ (gy & 0x1FFFFF); }

        static int Stage(Simulation sim, int waypoints)
        {
            if (sim.Goals != null) return sim.Progress;
            if (sim.TimedFromRunStart) return waypoints;
            return 2 * waypoints + (sim.TimingCourse() != null ? 1 : 0);
        }

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

        readonly HashSet<int> _trailRoutes = new HashSet<int>();

        void FileTrail(Simulation sim, Trail trail, Action<string> log)
        {
            FullTasTracker goals = sim.Goals;
            if (goals == null) { log?.Invoke($"trail {trail.Name}: not a Full-TAS plan, left out"); return; }
            int first = Math.Max(sim.Progress, _o.FromGoal), limit = Math.Min(goals.Limit, goals.Goals.Length);
            long start = sim.Engine.PhysicsTicks;
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

        static float Behind(Vector2 pv, Vector2 v)
        {
            Vector2 dv = pv - v;
            float speed = pv.magnitude;
            if (speed < 1f) return dv.magnitude;
            Vector2 u = pv / speed;
            float along = Vector2.Dot(dv, u);
            return Math.Max(0f, along) + (dv - along * u).magnitude;
        }

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
            public readonly List<Node> Guided = new List<Node>();

            public void AddGuided(Node n) { lock (this) Guided.Add(n); }
            Node _worst;

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

        Worker[] _workers;
        Barrier _barrier;
        Node[] _work;
        int[] _claims;
        volatile bool _quit;

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
                if (n.Kind is Contact own && Primitives.TryGetValue(own, out var mine))
                    foreach (int o in Offsets(1, mine.hi))
                        foreach (var (ticks, how) in Grafts(n, own, mine, n.Last, o))
                            Edge(state, Concat(Coast(n.Last, o), ticks), how);
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
                for (int route = 0; route < _p._guideRoutes.Count; route++)
                    if ((n.GuideRoute == -2 || n.GuideRoute == route) && !_p._trailRoutes.Contains(route)) Follow(state, route, n.GuideRoute == -2 ? 0 : n.GuideAt, true);
                foreach (var (route, at) in _p.Follows(n.Progress, n.At, n.V, n.Physics))
                    if (_p._trailRoutes.Contains(route) || n.GuideRoute != -2 && n.GuideRoute != route) Follow(state, route, at, false);
                _node = null;
            }

            int _edgeGuide = -1, _edgeGuideOffset;

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
                    foreach (float up in new[] { 1f, -1f })
                        foreach (int j in new[] { 0, 4 })
                        {
                            InputTick[] t = OmniDash(0f, 0f, j, side, down);
                            t[0].turnX = side * .35f;
                            t[0].turnY = up;
                            yield return (t, "steep", false);
                        }
                if (_p._o.Chains)
                    foreach (float side in new[] { -1f, 1f })
                        foreach (int links in ChainLinks)
                            foreach (int gap in ChainGaps)
                                foreach (int j in ChainJumps)
                                    yield return (BounceChain(side, links, gap, j, down), $"bounce chain {links}x gap {gap} frame {j}", false);
            }

            static readonly int[] ChainLinks = { 2, 4 }, ChainGaps = { 3, 7 }, ChainJumps = { 3, 6 };

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
                            // Timer starts on the second consecutive tick the hitbox overlaps the gate, so the tap holds 2 or 4 ticks.
                            float gate = contact == Contact.NearGateLeft ? -1f : 1f;
                            foreach (int delay in new[] { 0, 8 })
                                foreach (int tap in new[] { 2, 4 })
                                {
                                    var t = new List<InputTick>();
                                    for (int i = 0; i < delay; i++) t.Add(Stick(0f, 0f, down));
                                    for (int i = 0; i < tap; i++) t.Add(Stick(gate, 0f, down));
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

            void Edge(SimSnapshot state, InputTick[] ticks, string how, List<InputTick> tail = null, bool raw = false)
            {
                if (ticks.Length == 0) return;
                Node n = _node;
                List<InputTick> inputs = _inputs;
                inputs.RemoveRange(_prefix, inputs.Count - _prefix);
                if (!raw) ticks[0] = Edge0(n.Last, ticks[0]);
                inputs.AddRange(ticks);
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
                if (path.Step(_sim.PlayerStepFrom, p, _sim.Progress, ref _waypoints, !path.OnCourseOnly || _sim.TimingCourse() != null)) _avoided = true;
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
