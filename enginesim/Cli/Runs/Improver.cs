using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // Edits the economy steps of the best plan and keeps an edit only when the built run gets better. Each worker thread owns a
    // Builder and its own build of the best, so a candidate rebuilds from its first changed goal only. Batches are evaluated in
    // parallel and resolved in move order, so the same start gives the same result.
    sealed class Improver
    {
        const int MissesPerPass = 8, NextSteps = 3, FillBack = 3;

        readonly RunContext _run;
        readonly SegmentLibrary _lib;
        readonly SegmentSearch _search;
        readonly SegmentFacts _facts;
        readonly int _threads;
        public int MaxSearchesPerBuild = 3;
        public Built Best { get; private set; }
        public readonly List<string> Accepted = new List<string>();
        public event Action<Built, string> NewBest;
        public event Action<object> Event;
        public Action<string> Log = _ => { };
        public bool LogCandidates;

        public Improver(RunContext run, SegmentLibrary lib, SegmentSearch search, SegmentFacts facts, int threads)
        {
            (_run, _lib, _search, _facts, _threads) = (run, lib, search, facts, Math.Max(1, threads));
        }

        sealed class Worker : IDisposable
        {
            readonly BlockingCollection<Action<Builder>> _jobs = new BlockingCollection<Action<Builder>>();
            public Built Mine;

            public Worker(Func<Builder> make)
            {
                new Thread(() =>
                {
                    Builder b = make();
                    foreach (Action<Builder> job in _jobs.GetConsumingEnumerable()) job(b);
                }, 64 << 20) { IsBackground = true }.Start();
            }

            public Task<T> Do<T>(Func<Builder, T> job)
            {
                var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _jobs.Add(b => { try { done.SetResult(job(b)); } catch (Exception ex) { done.SetException(ex); } });
                return done.Task;
            }

            public void Dispose() => _jobs.CompleteAdding();
        }

        sealed record Move(string What, RunPlan Plan);

        public void Run(RunPlan plan, List<InputTick> inputs, Func<bool> stop)
        {
            using var coord = new Worker(() => new Builder(_run, _lib, _search, _facts, searchMisses: true) { Log = Log });
            Worker[] workers = Enumerable.Range(0, _threads).Select(_ => new Worker(() => new Builder(_run, _lib, _search, _facts, searchMisses: false))).ToArray();
            try
            {
                Built start = coord.Do(b =>
                {
                    coord.Mine = b.Harvest(plan, inputs, "harvest");
                    if (!coord.Mine.Complete) coord.Mine = b.Build(plan, coord.Mine);
                    return coord.Mine;
                }).Result;
                Accept(start, "start", coord, workers);
                while (!stop() && Pass(coord, workers, stop)) { }
                Event?.Invoke(new { type = "plan", converged = !stop(), reached = Best.Reached, of = Best.Plan.Count, live = Best.Live, waits = Best.Waits.Sum(w => w.Ticks) });
            }
            finally
            {
                foreach (Worker w in workers) w.Dispose();
            }
        }

        void Accept(Built b, string what, Worker coord, Worker[] workers)
        {
            Best = b;
            Accepted.Add(what);
            Log($"best: {Describe(b)} ({what})");
            NewBest?.Invoke(b, what);
            RunPlan plan = b.Plan;
            List<InputTick> inputs = b.Inputs;
            Task.WaitAll(workers.Append(coord).Select(w => (Task)w.Do(x => w.Mine = x.Harvest(plan, inputs, "sync", w.Mine))).ToArray());
        }

        static string Describe(Built b) => b.Complete ? $"{b.Live:0.0000} s, {b.Waits.Sum(w => w.Ticks)} wait ticks"
            : $"{b.Reached} of {b.Plan.Count} goals ({b.Failure?.Text ?? "stopped"})";

        bool Pass(Worker coord, Worker[] workers, Func<bool> stop)
        {
            List<Move> moves = Moves(Best);
            var misses = new List<(Move move, long gain)>();
            for (int i = 0; i < moves.Count && !stop(); i += workers.Length)
            {
                Move[] batch = moves.Skip(i).Take(workers.Length).ToArray();
                Task<Built>[] runs = batch.Select((m, k) => workers[k].Do(b => b.Build(m.Plan, workers[k].Mine))).ToArray();
                Task.WaitAll(runs);
                Built[] got = runs.Select(r => r.Result).ToArray();
                if (LogCandidates)
                    for (int k = 0; k < got.Length; k++) Log($"  candidate {batch[k].What}: {Describe(got[k])}");
                int win = -1;
                for (int k = 0; k < got.Length; k++)
                {
                    if (got[k].Better(Best) && (win < 0 || got[k].Better(got[win]))) win = k;
                    if (got[k].Failure?.Kind == "miss" && Ahead(got[k]) is long gain && gain > 0) misses.Add((batch[k], gain));
                }
                Event?.Invoke(new { type = "status", moves = moves.Count, tried = Math.Min(moves.Count, i + batch.Length), best = Best.Live, reached = Best.Reached, misses = misses.Count });
                if (win >= 0) { Accept(got[win], batch[win].What, coord, workers); return true; }
            }
            foreach (var (move, _) in misses.OrderByDescending(m => m.gain).Take(MissesPerPass))
            {
                if (stop()) return false;
                Built c = coord.Do(b =>
                {
                    b.MaxSearches = MaxSearchesPerBuild;
                    try { return b.Build(move.Plan, coord.Mine); }
                    finally { b.MaxSearches = int.MaxValue; }
                }).Result;
                if (c.Better(Best)) { Accept(c, move.What + " (searched)", coord, workers); return true; }
            }
            return false;
        }

        long? Ahead(Built c)
        {
            if (c.Reached == 0) return null;
            int at = Array.IndexOf(Best.Plan.Ids, c.Plan.Ids[c.Reached - 1]);
            return at < 0 || at >= Best.Reached ? (long?)null : Best.Physics[at] - c.Physics[c.Reached - 1];
        }

        List<Move> Moves(Built best)
        {
            RunPlan p = best.Plan;
            var seen = new HashSet<string> { string.Join("\n", p.Labels) };
            var moves = new List<Move>();
            void Add(string what, RunPlan q) { if (seen.Add(string.Join("\n", q.Labels))) moves.Add(new Move(what, q)); }
            int[] courses = p.Labels.Select(FullTasGoal.CourseOf).Where(c => c > 0).Distinct().OrderBy(c => c).ToArray();
            string[] buys = p.Labels.Where(l => l.StartsWith("buy:", StringComparison.Ordinal) && RunGoals.Editable(l)).Distinct().ToArray();
            void Fill(int at, string why)
            {
                foreach (int n in courses)
                {
                    Add($"farm:{n} before goal {at + 1} ({why})", p.Insert(at, "farm:" + n));
                    Add($"record:{n} before goal {at + 1} ({why})", p.Insert(at, "record:" + n));
                }
                foreach (string b in buys) Add($"{b} before goal {at + 1} ({why})", p.Insert(at, b));
            }
            if (!best.Complete && best.Failure != null)
            {
                int f = best.Failure.Goal;
                Fill(f, best.Failure.Kind);
                if (RunGoals.Editable(p.Labels[f])) Add($"drop goal {f + 1} {p.Labels[f]} ({best.Failure.Kind})", p.RemoveAt(f));
            }
            foreach (Wait w in best.Waits.OrderByDescending(w => w.Ticks).ThenBy(w => w.Goal))
            {
                // A shortfall shows at a later buy than the step that caused it: fill a few steps back too.
                for (int back = 0; back <= FillBack && w.Goal - back >= 0; back++) Fill(w.Goal - back, $"goal {w.Goal + 1}'s {w.Ticks}-tick wait");
                for (int h = w.Goal + 1, taken = 0; h < best.Reached && taken < NextSteps; h++)
                    if (RunGoals.Editable(p.Labels[h])) { Add($"goal {h + 1} {p.Labels[h]} ahead of goal {w.Goal + 1}'s wait", p.Move(h, w.Goal)); taken++; }
            }
            int n = best.Reached;
            for (int i = 0; i < n; i++)
                if (FullTasGoal.IsLap(p.Labels[i])) Add($"drop lap {i + 1} {p.Labels[i]}", p.RemoveAt(i));
            for (int i = 0; i + 1 < n; i++)
                if (Buy(p.Labels[i]) && Buy(p.Labels[i + 1]) && p.Labels[i] != p.Labels[i + 1]) Add($"swap goals {i + 1}-{i + 2}", p.Move(i + 1, i));
            for (int i = 0; i < n; i++)
            {
                if (!Buy(p.Labels[i])) continue;
                if (i > 0) Add($"goal {i + 1} {p.Labels[i]} one earlier", p.Move(i, i - 1));
                if (i + 1 < p.Count) Add($"goal {i + 1} {p.Labels[i]} one later", p.Move(i, i + 1));
            }
            return moves;
        }

        static bool Buy(string label) => label.StartsWith("buy:", StringComparison.Ordinal) && RunGoals.Editable(label);
    }
}
