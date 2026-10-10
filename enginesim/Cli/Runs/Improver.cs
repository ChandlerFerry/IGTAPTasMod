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
        const int MissesPerPass = 8, NextSteps = 3, FillBack = 3, SegmentRebuildSearches = 25;

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

        sealed record Move(string What, RunPlan Plan, int Keep = int.MaxValue);

        enum Outcome { Gained, Searching, Converged }

        readonly HashSet<string> _triedMisses = new HashSet<string>();

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
                if (!Accept(start, "start", coord, workers))
                {
                    Log("the start's straight replay falls short of its build: starting from its harvest");
                    Accept(coord.Do(b => coord.Mine = b.Harvest(plan, inputs, "harvest")).Result, "start (harvest)", coord, workers);
                }
                while (!stop() && Pass(coord, workers, stop) != Outcome.Converged) { }
                Event?.Invoke(new { type = "plan", converged = !stop(), reached = Best.Reached, of = Best.Plan.Count, live = Best.Live, waits = Best.Waits.Sum(w => w.Ticks) });
            }
            finally
            {
                foreach (Worker w in workers) w.Dispose();
            }
        }

        // One goal's step searched again from its real start with a long budget (the studio's stretch), kept when the whole run
        // gets faster.
        public void RunSegment(RunPlan plan, List<InputTick> inputs, int goal, Func<bool> stop)
        {
            using var coord = new Worker(() => new Builder(_run, _lib, _search, _facts, searchMisses: true) { Log = Log });
            Built start = coord.Do(b => coord.Mine = b.Harvest(plan, inputs, "harvest")).Result;
            if (!Accept(start, "start", coord, Array.Empty<Worker>())) return;
            if (goal >= start.Reached) { Log($"segment: the best doesn't reach goal {goal + 1} yet"); return; }
            Built c = coord.Do(b =>
            {
                (b.ForceSearchAt, b.MaxSearches) = (goal, SegmentRebuildSearches);
                try { return b.Build(plan, coord.Mine, goal); }
                finally { (b.ForceSearchAt, b.MaxSearches) = (-1, int.MaxValue); }
            }).Result;
            Log($"segment: goal {goal + 1} {plan.Labels[goal]} searched again: {Describe(c)} against {Describe(Best)}");
            if (!stop() && c.Better(Best)) Accept(c, $"goal {goal + 1} {plan.Labels[goal]} searched again", coord, Array.Empty<Worker>());
            Event?.Invoke(new { type = "plan", converged = true, reached = Best.Reached, of = Best.Plan.Count, live = Best.Live, waits = Best.Waits.Sum(w => w.Ticks) });
        }

        // A candidate is kept only as its straight replay from the run's start plays it (Builder.Verify).
        bool Accept(Built b, string what, Worker coord, Worker[] workers)
        {
            int claimed = b.Reached;
            double claimedLive = b.Live;
            Built v = coord.Do(x => x.Verify(b)).Result;
            if (v == null) { Log($"refused ({what}): its straight replay falls short of the {claimed} goals its build reached"); return false; }
            if (Best != null && !v.Better(Best)) { Log($"refused ({what}): {claimedLive:0.0000} s built, {v.Live:0.0000} s replayed straight, not better"); return false; }
            if (Math.Abs(v.Live - claimedLive) > 1e-6) Log($"note ({what}): built {claimedLive:0.0000} s, replayed straight {v.Live:0.0000} s");
            b = v;
            Best = b;
            _triedMisses.Clear();
            Accepted.Add(what);
            Log($"best: {Describe(b)} ({what})");
            NewBest?.Invoke(b, what);
            RunPlan plan = b.Plan;
            List<InputTick> inputs = b.Inputs;
            Task.WaitAll(workers.Append(coord).Select(w => (Task)w.Do(x => w.Mine = x.Harvest(plan, inputs, "sync", w.Mine))).ToArray());
            return true;
        }

        static string Describe(Built b) => b.Complete ? $"{b.Live:0.0000} s, {b.Waits.Sum(w => w.Ticks)} wait ticks"
            : $"{b.Reached} of {b.Plan.Count} goals ({b.Failure?.Text ?? "stopped"})";

        Outcome Pass(Worker coord, Worker[] workers, Func<bool> stop)
        {
            // The same best has the same moves and builds: a pass that only searched misses goes on with the next ones. A segment
            // a search filed reaches the later misses as they're built.
            if (_missesOf != Best) Evaluate(coord, workers, stop);
            if (_missesOf != Best) return stop() ? Outcome.Searching : Outcome.Gained;
            var untried = _misses.Where(m => !_triedMisses.Contains(Key(m.move))).OrderByDescending(m => m.gain).ToList();
            foreach (var (move, _) in untried.Take(MissesPerPass))
            {
                if (stop()) return Outcome.Searching;
                _triedMisses.Add(Key(move));
                Built c = coord.Do(b =>
                {
                    (b.MaxSearches, b.MaxBudget) = (MaxSearchesPerBuild, Builder.Budgets[0]);
                    try { return b.Build(move.Plan, coord.Mine, move.Keep); }
                    finally { (b.MaxSearches, b.MaxBudget) = (int.MaxValue, Builder.Budgets[^1]); }
                }).Result;
                if (c.Better(Best) && Accept(c, move.What + " (searched)", coord, workers)) return Outcome.Gained;
            }
            return untried.Count > MissesPerPass ? Outcome.Searching : Outcome.Converged;
        }

        Built _missesOf;
        List<(Move move, long gain)> _misses;

        void Evaluate(Worker coord, Worker[] workers, Func<bool> stop)
        {
            List<Move> moves = Moves(Best);
            var misses = new List<(Move move, long gain)>();
            for (int i = 0; i < moves.Count; i += workers.Length)
            {
                if (stop()) return;
                Move[] batch = moves.Skip(i).Take(workers.Length).ToArray();
                Task<Built>[] runs = batch.Select((m, k) => workers[k].Do(b => b.Build(m.Plan, workers[k].Mine, m.Keep))).ToArray();
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
                if (win >= 0 && Accept(got[win], batch[win].What, coord, workers)) return;
            }
            (_missesOf, _misses) = (Best, misses);
        }

        // How far ahead of the best a candidate that stopped at a miss is, at the last goal both have (a goal the move put in isn't
        // the best's). A miss no later than an incomplete best within the goals its failure's moves edit is the only way past
        // that failure: searched first, the goals the best reached last first (where the player stands: short searches; a lap of a
        // course left long ago has no distance to steer by).
        // ponytail: recency stands in for the distance to the goal; a distance field per miss if it misorders.
        long? Ahead(Built c)
        {
            const long Frontier = 1L << 40;
            for (int k = c.Reached - 1; k >= 0; k--)
            {
                int at = Array.IndexOf(Best.Plan.Ids, c.Plan.Ids[k]);
                if (at < 0) continue;
                long gain = at >= Best.Reached ? 0 : Best.Physics[at] - c.Physics[k];
                return at >= Best.Reached || !Best.Complete && gain >= 0 && at >= Best.Reached - 1 - FillBack
                    ? Frontier + Array.LastIndexOf(Best.Plan.Labels, c.Failure.Label, Math.Max(0, Best.Reached - 1)) : gain;
            }
            return null;
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
                if (RunGoals.Editable(p.Labels[f])) Add($"drop goal {f + 1} {p.Labels[f]} ({best.Failure.Kind})", p.RemoveAt(f));
                // What a goal lacks is often set by a step before it (a buy-max touch spends all it holds the first tick one level
                // is paid for): a lap or a buy before that step makes it wait, and buy more at once.
                for (int back = 0; back <= FillBack && f - back >= 0; back++) Fill(f - back, $"{best.Failure.Kind} at goal {f + 1}");
            }
            foreach (Wait w in best.Waits.OrderByDescending(w => w.Ticks).ThenBy(w => w.Goal))
            {
                // A shortfall shows at a later buy than the step that caused it: fill a few steps back too.
                for (int back = 0; back <= FillBack && w.Goal - back >= 0; back++) Fill(w.Goal - back, $"goal {w.Goal + 1}'s {w.Ticks}-tick wait");
                for (int h = w.Goal + 1, taken = 0; h < best.Reached && taken < NextSteps; h++)
                    if (RunGoals.Editable(p.Labels[h])) { Add($"goal {h + 1} {p.Labels[h]} ahead of goal {w.Goal + 1}'s wait", p.Move(h, w.Goal)); taken++; }
            }
            // A class without major techs rebuilds every step that still holds a pause (a start taken from the major best).
            if (Glitches.NoMajors(_run.Class))
                for (int j = 0; j < best.Reached; j++)
                {
                    int from = j == 0 ? 0 : best.StepEnd[j - 1];
                    for (int i = from; i < best.StepEnd[j]; i++)
                        if (best.Inputs[i].pause > 0) { moves.Add(new Move($"goal {j + 1} {p.Labels[j]} without its pause", p, j)); break; }
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

        static string Key(RunPlan p) => string.Join("|", p.Labels);

        static string Key(Move m) => Key(m.Plan) + "#" + m.Keep;

        static bool Buy(string label) => label.StartsWith("buy:", StringComparison.Ordinal) && RunGoals.Editable(label);
    }
}
