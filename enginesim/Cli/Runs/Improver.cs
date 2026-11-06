using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    sealed class Improver
    {
        const int MissesPerPass = 8, NextSteps = 3, FillBack = 3;

        readonly RunContext _run;
        readonly SegmentLibrary _lib;
        readonly SegmentSearch _search;
        readonly SegmentFacts _facts;
        readonly int _threads;
        public int MaxSearchesPerBuild = 3;
        public long SegmentBudget = Builder.Budgets[1], RebuildBudget = Builder.Budgets[^1];
        public int SegmentSearches = 25;
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
                while (!stop() && (Pass(coord, workers, stop) != Outcome.Converged || Polish(coord, workers, stop))) { }
                Event?.Invoke(new { type = "plan", converged = !stop(), reached = Best.Reached, of = Best.Plan.Count, live = Best.Live, waits = Best.Waits.Sum(w => w.Ticks) });
            }
            finally
            {
                foreach (Worker w in workers) w.Dispose();
            }
        }

        public void RunSegment(RunPlan plan, List<InputTick> inputs, int goal, Func<bool> stop, bool untilStop = false)
        {
            using var coord = new Worker(() => new Builder(_run, _lib, _search, _facts, searchMisses: true) { Log = Log });
            Built start = coord.Do(b => coord.Mine = b.Harvest(plan, inputs, "harvest")).Result;
            if (!Accept(start, "start", coord, Array.Empty<Worker>())) return;
            if (goal >= start.Reached) { Log($"segment: the best doesn't reach goal {goal + 1} yet"); return; }
            // shortcut: budget 4x a round with no cap; a round past a few billion ticks takes hours, Stop ends it.
            for (long budget = SegmentBudget; !stop();)
            {
                Built c = coord.Do(b =>
                {
                    (b.ForceSearchAt, b.ForceBudget, b.MaxSearches, b.MaxBudget, b.ForceSeed) = (goal, budget, SegmentSearches, RebuildBudget, Own(coord.Mine, goal));
                    if (untilStop) _search.Stall = Math.Max(SegmentSearch.DefaultStall, budget / 4);
                    try { return b.Build(Best.Plan, coord.Mine, goal); }
                    finally
                    {
                        (b.ForceSearchAt, b.ForceBudget, b.MaxSearches, b.MaxBudget, b.ForceSeed) = (-1, Builder.Budgets[1], int.MaxValue, Builder.Budgets[^1], null);
                        _search.Stall = SegmentSearch.DefaultStall;
                    }
                }).Result;
                GC.Collect();
                Log($"segment: goal {goal + 1} {plan.Labels[goal]} searched again at {budget / 1_000_000}M ticks: {Describe(c)} against {Describe(Best)}");
                bool gained = !stop() && (c.Better(Best) ? Accept(c, $"goal {goal + 1} {plan.Labels[goal]} searched again", coord, Array.Empty<Worker>())
                    : TailRejoin(c, goal, coord, Array.Empty<Worker>(), stop));
                if (!untilStop) break;
                budget = gained ? SegmentBudget : budget * 4;
                if (!stop()) Log($"segment: {(gained ? "a gain" : "nothing faster")}; searching again at {budget / 1_000_000}M ticks");
            }
            Event?.Invoke(new { type = "plan", converged = !untilStop, reached = Best.Reached, of = Best.Plan.Count, live = Best.Live, waits = Best.Waits.Sum(w => w.Ticks) });
        }

        HashSet<(string, ulong)> _polished = new HashSet<(string, ulong)>();
        int _polishLevel;

        // shortcut: fixed spans; a 5-goal window takes 5x a goal budget, add longer spans if a run polish stalls on chains.
        static readonly int[] Spans = { 1, 2, 3, 5 };

        internal static (string, ulong) PolishKey(Built b, int j, int span = 1) => (span == 1 ? b.Plan.Ids[j] : b.Plan.Ids[j] + "+" + span, SnapshotIndex.Hash(b.Inputs, b.StepEnd[j + span - 1]));

        static List<InputTick> Own(Built b, int j, int span = 1)
        {
            if (b == null || j + span > b.Reached) return null;
            int from = j == 0 ? 0 : b.StepEnd[j - 1];
            return b.Inputs.GetRange(from, b.StepEnd[j + span - 1] - from);
        }

        internal static List<(int j, int span)> PolishOrder(Built b, HashSet<(string, ulong)> tried) => Spans.SelectMany(span => Enumerable.Range(0, Math.Max(0, b.Reached - span + 1))
            .Where(j => !tried.Contains(PolishKey(b, j, span)))
            .OrderByDescending(j => b.Physics[j + span - 1] - (j == 0 ? 0 : b.Physics[j - 1]) - b.Waits.Where(w => w.Goal >= j && w.Goal < j + span).Sum(w => w.Ticks)).ThenBy(j => j)
            .Select(j => (j, span))).ToList();

        bool Polish(Worker coord, Worker[] workers, Func<bool> stop)
        {
            List<(int j, int span)> windows = MaxSearchesPerBuild == 0 ? new List<(int, int)>() : PolishOrder(Best, _polished);
            if (windows.Count == 0) return false;
            Log($"polish: {windows.Count} goals and windows searched again, each goal alone first");
            Event?.Invoke(new { type = "note", level = "info", text = $"Movement polish: searching {windows.Count} goals and windows of goals again" });
            foreach (var (j, span) in windows)
            {
                if (stop()) return false;
                _polished.Add(PolishKey(Best, j, span));
                RunPlan plan = Best.Plan;
                int last = j + span - 1;
                string what = span == 1 ? $"goal {j + 1} {plan.Labels[j]}" : $"goals {j + 1}-{last + 1}";
                long budget = Builder.Budgets[_polishLevel] * span;
                Event?.Invoke(new { type = "status", polish = j + 1, span, id = plan.Ids[j], best = Best.Live, reached = Best.Reached });
                Built c = coord.Do(b =>
                {
                    // shortcut: one search per window, rebuild from the library alone; restore MaxSearches if later goals lack library.
                    (b.ForceSearchAt, b.ForceSpan, b.ForceBudget, b.MaxSearches, b.MaxBudget, b.ForceSeed) = (j, span, budget, 1, budget, Own(coord.Mine, j, span));
                    _search.Stall = Math.Max(_search.Stall, budget / 4);
                    try { return b.Build(plan, coord.Mine, j); }
                    finally
                    {
                        (b.ForceSearchAt, b.ForceSpan, b.ForceBudget, b.MaxSearches, b.MaxBudget, b.ForceSeed) = (-1, 1, Builder.Budgets[1], int.MaxValue, Builder.Budgets[^1], null);
                        _search.Stall = SegmentSearch.DefaultStall;
                    }
                }).Result;
                // Server GC would let the finished searches' solvers pile up toward its hard limit; collect now.
                GC.Collect();
                Log($"polish: {what}: {Describe(c)} against {Describe(Best)}" + (c.Reached > last ? $" (goal {last + 1} at tick {c.Physics[last]}, the best's at {Best.Physics[last]})" : ""));
                if (!stop() && c.Better(Best) && Accept(c, $"{what} polished", coord, workers)) return true;
                if (!stop() && TailRejoin(c, last, coord, workers, stop)) return true;
            }
            Event?.Invoke(new { type = "note", level = "info", text = "Movement polish: no goal or window searched again saves time" });
            if (_polishLevel > 0 || stop()) return false;
            (_polishLevel, _polished) = (1, new HashSet<(string, ulong)>());
            Log($"polish: nothing at {Builder.Budgets[0] / 1_000_000}M ticks a goal; again at {Builder.Budgets[1] / 1_000_000}M");
            return true;
        }

        // shortcut: fixed few minutes a rejoin; raise it if polished segments that reach their goal sooner still lose the way on.
        static readonly TimeSpan TailBudget = TimeSpan.FromMinutes(3);

        bool TailRejoin(Built c, int j, Worker coord, Worker[] workers, Func<bool> stop)
        {
            Built best = Best;
            if (c.Reached <= j || j + 1 >= best.Reached || !c.Plan.Same(best.Plan)
                || MacroEngine.LiveUnits(c.Physics[j], c.Held[j]) >= MacroEngine.LiveUnits(best.Physics[j], best.Held[j])) return false;
            RunPlan plan = best.Plan;
            long[] input = Enumerable.Range(0, plan.Count).Select(g => g < best.Reached ? best.InputAt[g] : -1).ToArray();
            long[] physics = Enumerable.Range(0, plan.Count).Select(g => g < best.Reached ? best.Physics[g] : -1).ToArray();
            Rejoin.Path path = Rejoin.Path.Replayed(_run, plan, best.Inputs, physics);
            var rejoin = new Rejoin(_run, plan, best.Inputs, input, physics, path, _search, _threads) { Log = Log, Stop = stop, Budget = TailBudget };
            List<InputTick> inputs = rejoin.Repair(c.Inputs.Take(c.StepEnd[j]).ToList(), best.StepEnd[j]);
            Built r = coord.Do(b => b.Harvest(plan, inputs, "rejoin")).Result;
            Log($"polish: goal {j + 1} {plan.Labels[j]} with the best's way on rejoined: {Describe(r)} against {Describe(best)}");
            return !stop() && r.Better(best) && Accept(r, $"goal {j + 1} {plan.Labels[j]} polished, the best's way on rejoined", coord, workers);
        }

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
            if (Best.Failure?.Kind == "miss" && _triedMisses.Add(Key(Best.Plan) + "#best"))
            {
                Built built = coord.Do(b => b.Build(Best.Plan, coord.Mine)).Result;
                if (built.Better(Best) && Accept(built, $"goal {Best.Failure.Goal + 1} {Best.Failure.Label} searched", coord, workers)) return Outcome.Gained;
            }
            if (_incomeOf != Best)
            {
                _incomeOf = Best;
                if (IncomeFirst(coord, workers, stop)) return Outcome.Gained;
            }
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

        const int TopProducers = 6, MaxLaps = 12, ResetBack = 8, CoverCap = 120_000;
        Built _incomeOf;

        bool IncomeFirst(Worker coord, Worker[] workers, Func<bool> stop)
        {
            if (Best.Complete || Best.Failure?.Kind != "no-income" || workers.Length == 0) return false;
            RunPlan p = Best.Plan;
            int f = Best.Failure.Goal;
            string[] boxes = _run.GoalsOf(p)[f].boxes;
            if (boxes == null) return false;
            var income = coord.Do(b =>
            {
                Built mine = coord.Mine;
                if (mine == null || mine.Reached < f || !mine.Plan.Same(p)) return null;
                SimSnapshot Before(int g) => g == 0 ? b.Sim.Start : mine.States[g - 1];
                if (!(Income.Short(b.Sim, Before(f), boxes) is var (currency, by))) return null;
                int cut = -1;
                if (Income.Idle(b.Sim, Before(f), currency) <= 0)
                    for (int g = f - 1; g >= Math.Max(0, f - ResetBack); g--)
                        if (Income.Idle(b.Sim, Before(g), currency) > 0) { cut = g; break; }
                return ((globalStats.Currencies, double, List<Income.Producer>, int)?)(currency, by, Income.Of(b.Sim, Before(f), currency), cut);
            }).Result;
            if (!(income is var (cur, shortBy, producers, cut))) return false;
            Log($"income: goal {f + 1} {p.Labels[f]} is {shortBy:G4} {cur} short" + (cut >= 0 ? $", its income cut at goal {cut + 1} {p.Labels[cut]}" : "") + "; producers: "
                + (producers.Count == 0 ? "none" : string.Join(", ", producers.Take(TopProducers).Select(x => $"{x.Label} {x.Gain:G3} in {x.Ticks:0} ticks"))));
            var seen = new HashSet<string>();
            var moves = new List<Move>();
            void Add(string what, RunPlan q) { if (seen.Add(Key(q))) moves.Add(new Move(what, q)); }
            if (cut >= 0 && RunGoals.Editable(p.Labels[f])) Add($"goal {f + 1} {p.Labels[f]} before goal {cut + 1} {p.Labels[cut]}, which cut its {cur} income", p.Move(f, cut));
            var payable = producers.Where(pr => pr.Gain > 0 && shortBy / pr.Gain * Math.Max(pr.Ticks, Income.Window) <= CoverCap).ToList();
            if (payable.Count < producers.Count)
                Log($"income: {producers.Count - payable.Count} producer(s) of {cur} can't pay {shortBy:G4} within {CoverCap} ticks" + (payable.Count == 0 ? $": goal {f + 1} {p.Labels[f]} needs a {cur} economy built before it" : ""));
            foreach (Income.Producer pr in payable.Take(TopProducers))
            {
                string why = $"income: {pr.Gain:G3} {cur} in {pr.Ticks:0} ticks";
                int course = FullTasGoal.CourseOf(pr.Label);
                if (course == 0) { Add($"{pr.Label} before goal {f + 1} ({why})", p.Insert(f, pr.Label)); continue; }
                int laps = (int)Math.Clamp(Math.Ceiling(shortBy / pr.Gain), 1, MaxLaps);
                RunPlan q = p.Insert(f, "record:" + course);
                for (int i = 1; i < laps; i++) q = q.Insert(f + i, "farm:" + course);
                Add($"{laps} laps of course {course} before goal {f + 1} ({why} a lap)", q);
                Add($"record:{course} before goal {f + 1} ({why})", p.Insert(f, "record:" + course));
            }
            Built[] got = moves.Select((m, k) => workers[k % workers.Length].Do(b => b.Build(m.Plan, workers[k % workers.Length].Mine, m.Keep))).ToArray()
                .Select(t => t.Result).ToArray();
            int win = -1;
            for (int k = 0; k < got.Length; k++)
            {
                if (LogCandidates) Log($"  candidate {moves[k].What}: {Describe(got[k])}");
                if (got[k].Better(Best) && (win < 0 || got[k].Better(got[win]))) win = k;
            }
            if (win >= 0 && Accept(got[win], moves[win].What, coord, workers)) return true;
            for (int k = 0; k < got.Length; k++)
            {
                if (stop()) return false;
                if (got[k].Failure?.Kind != "miss" || !_triedMisses.Add(Key(moves[k]))) continue;
                Built c = coord.Do(b =>
                {
                    (b.MaxSearches, b.MaxBudget) = (MaxSearchesPerBuild + moves[k].Plan.Count - p.Count, Builder.Budgets[1]);
                    try { return b.Build(moves[k].Plan, coord.Mine, moves[k].Keep); }
                    finally { (b.MaxSearches, b.MaxBudget) = (int.MaxValue, Builder.Budgets[^1]); }
                }).Result;
                GC.Collect();
                Log($"income: {moves[k].What}, searched: {Describe(c)}");
                if (c.Better(Best) && Accept(c, moves[k].What + " (searched)", coord, workers)) return true;
            }
            return false;
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
                for (int back = 0; back <= FillBack && f - back >= 0; back++) Fill(f - back, $"{best.Failure.Kind} at goal {f + 1}");
            }
            foreach (Wait w in best.Waits.OrderByDescending(w => w.Ticks).ThenBy(w => w.Goal))
            {
                for (int back = 0; back <= FillBack && w.Goal - back >= 0; back++) Fill(w.Goal - back, $"goal {w.Goal + 1}'s {w.Ticks}-tick wait");
                for (int h = w.Goal + 1, taken = 0; h < best.Reached && taken < NextSteps; h++)
                    if (RunGoals.Editable(p.Labels[h])) { Add($"goal {h + 1} {p.Labels[h]} ahead of goal {w.Goal + 1}'s wait", p.Move(h, w.Goal)); taken++; }
            }
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
