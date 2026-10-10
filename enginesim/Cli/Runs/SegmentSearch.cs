using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // Deterministic: SearchWorkers single-worker solvers with fixed seeds, each ending itself at its share of the tick budget,
    // so the same state and seeds give the same finds whatever the machine's load.
    sealed class SegmentSearch
    {
        public const int SearchWorkers = 16;
        const long StallTicks = 2_000_000;
        const double GraftRate = .15;

        readonly RunContext _run;
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

        public List<List<InputTick>> Find(byte[] state, List<InputTick> prefix, FullTasGoal[] goals, int goal, List<List<InputTick>> seeds, long budgetTicks, out string nearest)
        {
            string[] ids = _run.HeaderIds.Length == goals.Length ? _run.HeaderIds : null;
            (string caps, OfflineSolver.ForcedPath path) = Solve.OnThread(() =>
            {
                Simulation sim = At(state, prefix, goals, goal);
                sim.Restore(sim.Start);
                string[] boxes = goals[goal].boxes ?? Array.Empty<string>();
                OfflineSolver.ForcedPath p = Solve.FullTasPaths(_run.Name, ids, ids == null ? null : new HashSet<int> { goal });
                // Stray buys end a strict run: the search is steered round the boxes it could buy, never cut off from a graze.
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
                    { ArchiveBudget = 1L << 29, StateBudget = 1L << 30, TickLimit = share, StallTicks = StallTicks / SearchWorkers };
                if (solver.Mined != null) solver.GraftRate = GraftRate;
                solver.Improved += (time, runTicks, inputs, how) => { lock (mine) mine.Add(inputs); };
            }
            Parallel.For(0, SearchWorkers, new ParallelOptions { MaxDegreeOfParallelism = SearchWorkers }, i =>
            {
                foreach (Thread t in solvers[i].Start(1)) t.Join();
                solvers[i].Stop();
                nearestOf[i] = solvers[i].Closest()?.Distance ?? float.NaN;
            });
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
                    _graftsFor[caps] = patterns = lib.Patterns.Where(p => p.Requires.IsSubsetOf(held)).ToList();
                }
            return patterns.Count == 0 ? null : new TechGrafts { Patterns = patterns, Key = lib.Key, CacheFile = lib.CacheFile };
        }
    }
}
