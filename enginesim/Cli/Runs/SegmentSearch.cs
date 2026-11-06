using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // Deterministic: fixed seeds and per-worker tick shares, so finds do not depend on the machine's load.
    sealed class SegmentSearch
    {
        public const int SearchWorkers = 16;
        const long StallTicks = 2_000_000;
        public const long DefaultStall = StallTicks;
        const double GraftRate = .15;

        readonly RunContext _run;
        public Func<bool> Stop = () => false;
        public int Parallelism = SearchWorkers;
        public long ArchiveBudget = 1L << 29, StateBudget = 1L << 30;
        public long Stall = StallTicks;
        public bool Stopped { get; private set; }
        readonly Lazy<TechGrafts> _grafts;
        readonly Dictionary<string, List<TechPattern>> _graftsFor = new Dictionary<string, List<TechPattern>>();

        public SegmentSearch(RunContext run)
        {
            _run = run;
            _grafts = new Lazy<TechGrafts>(() =>
            {
                try { return Solve.OnThread(() => TechGrafts.Load(_run.Dir)); }
                catch (Exception ex) { Console.WriteLine("search: the tech library didn't load: " + ex.GetBaseException().Message); return null; }
            });
        }

        public Simulation At(byte[] state, List<InputTick> prefix, FullTasGoal[] goals, int goal)
        {
            Simulation sim = _run.Make();
            FullTasTracker tracker = sim.Goals.For(goals);
            sim.UseGoals(tracker);
            SimSnapshot s = null;
            if (state != null)
                try { s = new SnapshotStore(sim).Load(state); }
                catch (NotSupportedException) { }
            if (s == null)
            {
                sim.SetStart(sim.Start.WithGoal(new FullTasTracker(goals).Capture()), 0);
                tracker.Limit = goals.Length + 1;
                s = sim.Advance(prefix) ?? throw new InvalidOperationException($"the {prefix.Count}-input prefix ended early on a fresh simulation: {sim.Engine.Status}");
            }
            sim.SetStart(s, prefix.Count);
            tracker.Limit = goal + 1;
            return sim;
        }

        internal OfflineSolver.ForcedPath Avoids(string[] ids, int goal, int from = -1) =>
            Solve.FullTasPaths(_run.Name, ids, new HashSet<int>(Enumerable.Range(from < 0 ? goal : from, goal - (from < 0 ? goal : from) + 1)));

        public List<List<InputTick>> Find(byte[] state, List<InputTick> prefix, FullTasGoal[] goals, string[] ids, int goal, List<List<InputTick>> seeds, long budgetTicks, out string nearest,
            int from = -1)
        {
            int first = from < 0 ? goal : from;
            (string caps, OfflineSolver.ForcedPath path) = Solve.OnThread(() =>
            {
                Simulation sim = At(state, prefix, goals, goal);
                sim.Restore(sim.Start);
                string[] boxes = Enumerable.Range(first, goal - first + 1).SelectMany(g => goals[g].boxes ?? Array.Empty<string>()).ToArray();
                OfflineSolver.ForcedPath p = Avoids(ids, goal, first);
                // Stray buys end a strict run (RULES.md 38): steer round boxes it could buy, never cut off from a graze.
                if (!_run.Loose) foreach (var (a, b, _) in sim.StrayTriggers(new HashSet<string>(boxes))) p.Discourage.Add(new OfflineSolver.Box(a, b));
                return (sim.Shop.AfterBuys(PlayerCaps.Of(sim), boxes).Id, p);
            });
            long share = Math.Max(1, budgetTicks / SearchWorkers);
            var finds = new List<List<InputTick>>[SearchWorkers];
            var nearestOf = new float[SearchWorkers];
            DistanceField.Enabled = true;
            var solvers = new OfflineSolver[SearchWorkers];
            for (int i = 0; i < SearchWorkers; i++)
            {
                var mine = finds[i] = new List<List<InputTick>>();
                var solver = solvers[i] = new OfflineSolver(() => At(state, prefix, goals, goal), caps, seeds, null, path, 16f, Grafts(caps), rng: 1 + i)
                    { ArchiveBudget = ArchiveBudget, StateBudget = StateBudget, TickLimit = share, StallTicks = Stall / SearchWorkers };
                if (solver.Mined != null) solver.GraftRate = GraftRate;
                solver.Improved += (time, runTicks, inputs, how) => { lock (mine) mine.Add(inputs); };
            }
            Stopped = false;
            using var done = new ManualResetEventSlim();
            var watch = new Thread(() =>
            {
                while (!done.Wait(250))
                    if (Stop()) { Stopped = true; foreach (OfflineSolver x in solvers) x.Stop(); return; }
            }) { IsBackground = true };
            watch.Start();
            Parallel.For(0, SearchWorkers, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Parallelism, 1, SearchWorkers) }, i =>
            {
                foreach (Thread t in solvers[i].Start(1)) t.Join();
                solvers[i].Stop();
                nearestOf[i] = solvers[i].Closest()?.Distance ?? float.NaN;
            });
            done.Set();
            watch.Join();
            if (Stopped) { nearest = "stopped"; return new List<List<InputTick>>(); }
            nearest = "nearest the goal: " + string.Join(" ", nearestOf.Select(d => float.IsNaN(d) ? "-" : d.ToString("0")));
            return finds.SelectMany(f => f).ToList();
        }

        TechGrafts Grafts(string caps)
        {
            TechGrafts lib = _grafts.Value;
            if (lib == null) return null;
            List<TechPattern> patterns;
            lock (_graftsFor)
                if (!_graftsFor.TryGetValue(caps, out patterns))
                {
                    Caps held = Caps.Parse(caps);
                    _graftsFor[caps] = patterns = lib.Patterns.Where(p => p.Requires.IsSubsetOf(held) && Glitches.Allows(_run.Class, p.Category)).ToList();
                }
            return patterns.Count == 0 ? null : new TechGrafts { Patterns = patterns, Key = lib.Key, CacheFile = lib.CacheFile };
        }
    }
}
