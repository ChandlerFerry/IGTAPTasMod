using System;
using System.Collections.Generic;
using System.Linq;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    sealed class Wait
    {
        public int Goal;
        public string Label, Currency;
        public float X, Y;
        public long Ticks;
        public double Short, PerTick;
    }

    sealed class Failure
    {
        public string Kind, Label, Text, Key;
        public int Goal;
    }

    sealed class Built
    {
        public RunPlan Plan;
        internal Builder Owner;
        public List<InputTick> Inputs = new List<InputTick>();
        public List<int> StepEnd = new List<int>();
        public List<SimSnapshot> States = new List<SimSnapshot>();
        public List<long> InputAt = new List<long>(), Physics = new List<long>(), Held = new List<long>();
        public List<Wait> Waits = new List<Wait>();
        public Failure Failure;
        public int Searches;
        public int Reached => StepEnd.Count;
        public bool Complete => Plan != null && Reached == Plan.Count;
        public double Live => Reached == 0 ? 0 : Math.Round(MacroEngine.LiveSeconds(Physics[^1], Held[^1]), 4);

        public bool Better(Built o)
        {
            if (o == null) return true;
            if (Complete != o.Complete) return Complete;
            if (Complete) return MacroEngine.LiveUnits(Physics[^1], Held[^1]) < MacroEngine.LiveUnits(o.Physics[^1], o.Held[^1]);
            (int n, long at) = Skeleton();
            (int on, long oat) = o.Skeleton();
            return n != on ? n > on : n > 0 && at < oat;
        }

        // Plans differ in their economy goals; the skeleton (unlocks, areas, rewards) is the same, so progress counts it alone.
        (int reached, long tick) Skeleton()
        {
            int n = 0;
            long at = long.MaxValue;
            for (int g = 0; g < Reached; g++)
                if (!RunGoals.Editable(Plan.Labels[g])) { n++; at = Physics[g]; }
            return (n, at);
        }
    }

    // One per thread: it owns its Simulation, and the states it keeps belong to that Simulation alone.
    sealed class Builder
    {
        const int Settle = 20, IdleCap = 60_000, IncomeWindow = 500, QuietWindows = 3, InactiveCap = 10_000;
        public static readonly long[] Budgets = { 20_000_000, 80_000_000, 320_000_000 };

        readonly RunContext _run;
        readonly SegmentLibrary _lib;
        readonly SegmentSearch _search;
        readonly SegmentFacts _facts;
        readonly bool _searchMisses;
        readonly Simulation _sim;
        readonly FullTasTracker _base;
        readonly SimSnapshot _origin;
        public readonly List<(int goal, byte[] state, List<InputTick> prefix, string key)> Misses = new List<(int, byte[], List<InputTick>, string)>();
        public Action<string> Log = _ => { };
        public int MaxSearches = int.MaxValue;
        public long MaxBudget = Budgets[^1];

        public Builder(RunContext run, SegmentLibrary lib, SegmentSearch search, SegmentFacts facts, bool searchMisses)
        {
            (_run, _lib, _search, _facts, _searchMisses) = (run, lib, search, facts, searchMisses);
            _sim = run.Make();
            _base = _sim.Goals;
            _origin = _sim.Start;
        }

        public Built Build(RunPlan plan, Built reuse = null)
        {
            FullTasGoal[] goals = _run.GoalsOf(plan);
            _sim.UseGoals(_base.For(goals));
            var built = new Built { Plan = plan, Owner = this };
            int keep = 0;
            if (reuse != null && reuse.Owner == this)
                while (keep < reuse.Reached && keep < plan.Count && reuse.Plan.Labels[keep] == plan.Labels[keep]) keep++;
            if (keep > 0)
            {
                built.Inputs.AddRange(reuse.Inputs.Take(reuse.StepEnd[keep - 1]));
                built.StepEnd.AddRange(reuse.StepEnd.Take(keep));
                built.States.AddRange(reuse.States.Take(keep));
                built.InputAt.AddRange(reuse.InputAt.Take(keep));
                built.Physics.AddRange(reuse.Physics.Take(keep));
                built.Held.AddRange(reuse.Held.Take(keep));
                built.Waits.AddRange(reuse.Waits.Where(w => w.Goal < keep));
            }
            for (int j = keep; j < plan.Count && built.Failure == null; j++) Step(built, goals, j, null);
            return built;
        }

        public Built Harvest(RunPlan plan, List<InputTick> route, string source, Built reuse = null)
        {
            FullTasGoal[] goals = _run.GoalsOf(plan);
            _sim.UseGoals(_base.For(goals));
            var built = new Built { Plan = plan, Owner = this };
            int keep = 0;
            if (reuse != null && reuse.Owner == this)
                while (keep < reuse.Reached && keep < plan.Count && reuse.Plan.Labels[keep] == plan.Labels[keep] && reuse.StepEnd[keep] <= route.Count
                    && SameInputs(reuse.Inputs, route, keep == 0 ? 0 : reuse.StepEnd[keep - 1], reuse.StepEnd[keep])) keep++;
            if (keep > 0)
            {
                built.Inputs.AddRange(route.Take(reuse.StepEnd[keep - 1]));
                built.StepEnd.AddRange(reuse.StepEnd.Take(keep));
                built.States.AddRange(reuse.States.Take(keep));
                built.InputAt.AddRange(reuse.InputAt.Take(keep));
                built.Physics.AddRange(reuse.Physics.Take(keep));
                built.Held.AddRange(reuse.Held.Take(keep));
                built.Waits.AddRange(reuse.Waits.Where(w => w.Goal < keep));
            }
            for (int j = keep; j < plan.Count && built.Failure == null; j++)
            {
                int from = j == 0 ? 0 : built.StepEnd[j - 1];
                if (from >= route.Count) break;
                Step(built, goals, j, (route, from, source));
            }
            if (built.Failure?.Kind == "harvest") built.Failure = null;
            return built;
        }

        static bool SameInputs(List<InputTick> a, List<InputTick> b, int from, int to)
        {
            for (int i = from; i < to; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        SimSnapshot StartOf(Built built, FullTasGoal[] goals, int j)
        {
            SimSnapshot s = j == 0 ? _origin : built.States[j - 1];
            int n = goals.Length;
            var progress = new FullTasTracker.State { Next = j, Bought = 0, Banked = s.Goal?.Banked ?? 0, InputTicks = new long[n], PhysicsTicks = new long[n], HeldFrames = new long[n] };
            for (int g = 0; g < n; g++)
            {
                bool done = g < j;
                progress.InputTicks[g] = done ? built.InputAt[g] : -1;
                progress.PhysicsTicks[g] = done ? built.Physics[g] : -1;
                progress.HeldFrames[g] = done ? built.Held[g] : -1;
            }
            return s.WithGoal(progress);
        }

        sealed class Context
        {
            public float X, Y;
            public int Phase, Facing;
            public bool Ground;
            public string Unlocks;
            public int Fp;
        }

        Context Read(SimSnapshot s)
        {
            _sim.Restore(s);
            WorldPos p = _sim.PlayerWorld;
            return new Context { X = p.x, Y = p.y, Phase = (int)(_sim.Engine.PhysicsTicks % 5), Facing = _sim.Player.facingRight ? 1 : -1, Ground = _sim.Player.onGround,
                Unlocks = PlayerCaps.Of(_sim).Id, Fp = _sim.MovementFingerprint() };
        }

        (List<InputTick> inputs, SimSnapshot at)? TryPlay(SimSnapshot start, int inputBase, IReadOnlyList<InputTick> inputs, int j)
        {
            // Explicit idle inputs after the segment: a goal reached in ticks after its last input is cut on inputs that exist.
            // The engine counts ticks past the list's end too, so a goal reached there is replayed once with that many more.
            var list = new List<InputTick>(inputs.Count + Settle);
            list.AddRange(inputs);
            for (int extra = Settle, attempt = 0; attempt < 2; attempt++)
            {
                for (int i = 0; i < extra; i++) list.Add(new InputTick());
                _sim.SetStart(start, inputBase);
                int limit = _sim.Goals.Limit;
                _sim.Goals.Limit = j + 1;
                SimSnapshot at;
                int consumed;
                try { at = _sim.AdvanceToGoal(list, j + 1, out consumed); }
                finally { _sim.Goals.Limit = limit; }
                if (at == null) return null;
                if (consumed <= list.Count) return (list.GetRange(0, consumed), at);
                extra = consumed - list.Count + Settle;
            }
            return null;
        }

        void Append(Built built, int j, List<InputTick> inputs, SimSnapshot at)
        {
            built.Inputs.AddRange(inputs);
            built.StepEnd.Add(built.Inputs.Count);
            built.States.Add(at);
            built.InputAt.Add(at.Goal.InputTicks[j]);
            built.Physics.Add(at.Goal.PhysicsTicks[j]);
            built.Held.Add(at.Goal.HeldFrames[j]);
        }

        void File(Built built, int j, Context c, List<InputTick> inputs, SimSnapshot at, string source)
        {
            long before = j == 0 ? 0 : built.Physics[j - 1], beforeHeld = j == 0 ? 0 : built.Held[j - 1];
            _lib.Add(new Segment
            {
                To = built.Plan.Labels[j], After = j == 0 ? null : built.Plan.Labels[j - 1], X = c.X, Y = c.Y, Phase = c.Phase, Facing = c.Facing, Ground = c.Ground,
                Unlocks = c.Unlocks, Inputs = inputs, Ticks = at.Goal.PhysicsTicks[j] - before, Held = at.Goal.HeldFrames[j] - beforeHeld, Source = source, Fp = c.Fp,
            });
        }

        void Step(Built built, FullTasGoal[] goals, int j, (List<InputTick> route, int from, string source)? harvest)
        {
            SimSnapshot start = StartOf(built, goals, j);
            int inputBase = j == 0 ? 0 : built.StepEnd[j - 1];
            Context c = Read(start);
            string label = built.Plan.Labels[j];
            if (harvest is var (route, from, source))
            {
                for (int window = 4096; ; window *= 4)
                {
                    int n = Math.Min(window, route.Count - from);
                    var played = TryPlay(start, inputBase, route.GetRange(from, n), j);
                    if (played is var (inputs, at))
                    {
                        File(built, j, c, inputs, at, source);
                        Append(built, j, inputs, at);
                        if (goals[j].kind == FullTasGoal.Buy || goals[j].kind == FullTasGoal.Reward) NoteWait(built, goals, j, start, c, inputs);
                        return;
                    }
                    if (from + n >= route.Count) { built.Failure = new Failure { Kind = "harvest", Goal = j, Label = label, Text = "the route stops before " + label }; return; }
                }
            }
            bool afterReset = built.Inputs.Count > 0 && built.Inputs[^1].reset;
            FullTasGoal goal = goals[j];
            bool buy = (goal.kind == FullTasGoal.Buy || goal.kind == FullTasGoal.Reward) && goal.boxes != null;
            if (Best(start, inputBase, c, label, j, afterReset) is var (bestInputs, bestAt))
            {
                if (buy) (bestInputs, bestAt) = TrimIdle(start, inputBase, j, bestInputs, bestAt);
                Append(built, j, bestInputs, bestAt);
                if (buy) NoteWait(built, goals, j, start, c, bestInputs);
                return;
            }
            _sim.Restore(start);
            if (buy && _sim.Shop.Affordable(goal.boxes) != true)
            {
                WaitStep(built, goals, j, start, inputBase, c, afterReset);
                return;
            }
            if (Searched(built, goals, j, start, inputBase, c, afterReset, built.Inputs) is var (found, foundAt))
            {
                Append(built, j, found, foundAt);
                if (buy) NoteWait(built, goals, j, start, c, found);
            }
        }

        (List<InputTick>, SimSnapshot)? Best(SimSnapshot start, int inputBase, Context c, string label, int j, bool afterReset)
        {
            // The first that replays, nearest start first: what was filed from this very state reproduces what followed it.
            foreach (Segment s in _lib.Candidates(label, c.Unlocks, c.X, c.Y, c.Phase, c.Facing, fp: c.Fp))
            {
                if (afterReset && s.Inputs.Count > 0 && s.Inputs[0].reset) continue;
                if (TryPlay(start, inputBase, s.Inputs, j) is var (inputs, at)) return (inputs, at);
            }
            return null;
        }

        const int MinWait = 8;

        static bool Neutral(InputTick t) => t.x == 0 && t.y == 0 && !t.held && !t.press && !t.release && !t.dash && !t.reset && t.dashJump == 0 && t.pause == 0;

        // The fewest leading idle inputs after which the rest still buys: the economy may pay sooner than when it was filed.
        (List<InputTick>, SimSnapshot) TrimIdle(SimSnapshot start, int inputBase, int j, List<InputTick> inputs, SimSnapshot at)
        {
            int n = 0;
            while (n < inputs.Count && Neutral(inputs[n])) n++;
            if (n == 0 || n == inputs.Count) return (inputs, at);
            List<InputTick> rest = inputs.GetRange(n, inputs.Count - n);
            var idle = new InputTick();
            (List<InputTick>, SimSnapshot)? With(int k) => TryPlay(start, inputBase, Enumerable.Repeat(idle, k).Concat(rest).ToList(), j);
            int lo = 0, hi = n;
            while (lo < hi) { int mid = (lo + hi) / 2; if (With(mid) != null) hi = mid; else lo = mid + 1; }
            return lo < n && With(lo) is var (trimmed, trimmedAt) ? (trimmed, trimmedAt) : (inputs, at);
        }

        // A buy step's wait: its idle inputs before it moves off and while it stands in the box.
        void NoteWait(Built built, FullTasGoal[] goals, int j, SimSnapshot start, Context c, List<InputTick> inputs)
        {
            int lead = 0, tail = 0;
            while (lead < inputs.Count && Neutral(inputs[lead])) lead++;
            while (tail < inputs.Count - lead && Neutral(inputs[inputs.Count - 1 - tail])) tail++;
            if (lead + tail < MinWait) return;
            _sim.Restore(start);
            double[] need = _sim.Shop.Need(goals[j].boxes);
            var currencies = (globalStats.Currencies[])Enum.GetValues(typeof(globalStats.Currencies));
            int cur = need == null ? -1 : Enumerable.Range(0, Math.Min(need.Length, currencies.Length)).Where(k => need[k] > 0)
                .OrderByDescending(k => need[k] - _sim.Shop.Holds(currencies[k])).DefaultIfEmpty(-1).First();
            double shortBy = cur < 0 ? 0 : Math.Max(0, need[cur] - _sim.Shop.Holds(currencies[cur]));
            built.Waits.Add(new Wait { Goal = j, Label = built.Plan.Labels[j], X = c.X, Y = c.Y, Ticks = lead + tail, Currency = cur < 0 ? null : currencies[cur].ToString(),
                Short = shortBy, PerTick = shortBy / (lead + tail) });
        }

        string KeyOf(string label, Context c) => $"{label}|{c.Unlocks}|{(int)MathF.Floor(c.X / 16)},{(int)MathF.Floor(c.Y / 16)}|{c.Phase}";

        (List<InputTick>, SimSnapshot)? Searched(Built built, FullTasGoal[] goals, int j, SimSnapshot start, int inputBase, Context c, bool afterReset, List<InputTick> prefix)
        {
            string label = built.Plan.Labels[j], key = KeyOf(label, c);
            long budget = Budgets.FirstOrDefault(b => b <= MaxBudget && !_facts.Failed(key, b));
            if (budget == 0)
            {
                built.Failure = Unreachable(j, label, c, Budgets[^1]);
                return null;
            }
            _sim.Restore(start);
            byte[] bytes = null;
            try { bytes = new SnapshotStore(_sim).Save(start); }
            catch (NotSupportedException) { }
            if (!_searchMisses || built.Searches >= MaxSearches)
            {
                Misses.Add((j, bytes, prefix.ToList(), key));
                built.Failure = new Failure { Kind = "miss", Goal = j, Label = label, Key = key, Text = $"goal {j + 1} {label}: no library segment from ({c.X:0}, {c.Y:0}); queued for a search" };
                return null;
            }
            List<List<InputTick>> seeds = _lib.Candidates(label, c.Unlocks, c.X, c.Y, c.Phase, c.Facing, 8, float.MaxValue).Select(s => s.Inputs).ToList();
            for (; budget != 0; budget = Budgets.FirstOrDefault(b => b > budget && b <= MaxBudget && !_facts.Failed(key, b)))
            {
                Log($"search: goal {j + 1} {label} from ({c.X:0}, {c.Y:0}) with {c.Unlocks}, {budget / 1_000_000}M ticks, {seeds.Count} seeds");
                built.Searches++;
                List<List<InputTick>> finds = _search.Find(bytes, prefix, goals, j, seeds, budget, out string nearest);
                if (_search.Stopped)
                {
                    built.Failure = new Failure { Kind = "stopped", Goal = j, Label = label, Text = "stopped during a search" };
                    return null;
                }
                (List<InputTick> inputs, SimSnapshot at)? best = null;
                foreach (List<InputTick> f in finds)
                {
                    if (afterReset && f.Count > 0 && f[0].reset) continue;
                    if (TryPlay(start, inputBase, f, j) is var (inputs, at) && (best == null || at.Goal.PhysicsTicks[j] < best.Value.at.Goal.PhysicsTicks[j]))
                        best = (inputs, at);
                }
                if (best is var (bi, ba))
                {
                    File(built, j, c, bi, ba, "search");
                    Log($"search: goal {j + 1} {label} found ({finds.Count} finds)");
                    return (bi, ba);
                }
                _facts.Record(key, budget, finds.Count == 0 ? nearest : $"{finds.Count} finds, none replays from the real start");
                Log($"search: goal {j + 1} {label}: nothing within {budget / 1_000_000}M ticks ({nearest})");
            }
            built.Failure = Unreachable(j, label, c, Budgets[^1]);
            return null;
        }

        static Failure Unreachable(int j, string label, Context c, long budget) => new Failure
        {
            Kind = "unreachable", Goal = j, Label = label,
            Text = $"no way from ({c.X:0}, {c.Y:0}) to goal {j + 1} {label} with {c.Unlocks} after {budget / 1_000_000}M ticks: add an avoid box or a waypoint (at:) here",
        };

        // A buy short of its price idles where the last step left off until it's paid for, walks from there, then takes the
        // fewest idle ticks after which that walk still buys it: those idle ticks are the step's wait.
        void WaitStep(Built built, FullTasGoal[] goals, int j, SimSnapshot start, int inputBase, Context c, bool afterReset)
        {
            FullTasGoal goal = goals[j];
            string label = built.Plan.Labels[j];
            _sim.Restore(start);
            double[] need = _sim.Shop.Need(goal.boxes);
            var currencies = (globalStats.Currencies[])Enum.GetValues(typeof(globalStats.Currencies));
            int currency = need == null ? -1 : Enumerable.Range(0, Math.Min(need.Length, currencies.Length)).Where(k => need[k] > 0)
                .OrderByDescending(k => need[k] - _sim.Shop.Holds(currencies[k])).DefaultIfEmpty(-1).First();
            double startHeld = currency < 0 ? 0 : _sim.Shop.Holds(currencies[currency]);
            double shortBy = currency < 0 ? 0 : need[currency] - startHeld;

            // The idling itself can buy it: the player may be standing in the box.
            int affordAt = -1, quiet = 0, inactive = 0;
            double lastHeld = startHeld;
            string why = null;
            var idle = Enumerable.Repeat(new InputTick(), IdleCap).ToList();
            _sim.SetStart(start, inputBase);
            int limit = _sim.Goals.Limit;
            _sim.Goals.Limit = j + 1;
            try
            {
                _sim.Run(idle, null, t =>
                {
                    if (_sim.Progress > j || _sim.Shop.Affordable(goal.boxes) == true) { affordAt = t; return false; }
                    if (t % IncomeWindow != 0) return true;
                    bool? active = _sim.Shop.Affordable(goal.boxes);
                    if (active == null) { if ((inactive += IncomeWindow) >= InactiveCap) { why = $"{label}'s box never came on"; return false; } return true; }
                    inactive = 0;
                    double held = currency < 0 ? 0 : _sim.Shop.Holds(currencies[currency]);
                    if (held <= lastHeld) { if (++quiet >= QuietWindows) { why = $"nothing paid {currencies[Math.Max(0, currency)]} toward {label} for {quiet * IncomeWindow} ticks"; return false; } }
                    else quiet = 0;
                    lastHeld = held;
                    return true;
                }, 0);
            }
            finally { _sim.Goals.Limit = limit; }
            if (affordAt < 0)
            {
                built.Failure = new Failure { Kind = "no-income", Goal = j, Label = label, Text = why ?? $"{label} not paid for after {IdleCap} ticks of waiting" };
                return;
            }
            var wait = new Wait { Goal = j, Label = label, X = c.X, Y = c.Y, Currency = currency < 0 ? null : currencies[currency].ToString(), Short = shortBy,
                PerTick = affordAt == 0 ? 0 : shortBy / affordAt };
            if (TryPlay(start, inputBase, idle.GetRange(0, affordAt + 1), j) is var (stood, stoodAt))
            {
                wait.Ticks = stood.Count;
                built.Waits.Add(wait);
                Append(built, j, stood, stoodAt);
                return;
            }
            _sim.SetStart(start, inputBase);
            _sim.Goals.Limit = j + 1;
            SimSnapshot paid;
            try { paid = affordAt == 0 ? start : _sim.Advance(idle.GetRange(0, affordAt)); }
            finally { _sim.Goals.Limit = limit; }
            if (paid == null) { built.Failure = new Failure { Kind = "no-income", Goal = j, Label = label, Text = $"the wait for {label} ended the run" }; return; }
            Context pc = Read(paid);
            var walkPrefix = built.Inputs.Concat(idle.GetRange(0, affordAt)).ToList();
            List<InputTick> walk = Best(paid, inputBase + affordAt, pc, label, j, afterReset && affordAt == 0)?.Item1;
            if (walk == null)
            {
                var probe = new Built { Plan = built.Plan, Owner = this };
                probe.Inputs.AddRange(walkPrefix);
                probe.InputAt.AddRange(built.InputAt);
                probe.Physics.AddRange(built.Physics);
                probe.Held.AddRange(built.Held);
                walk = Searched(probe, goals, j, paid, inputBase + affordAt, pc, afterReset && affordAt == 0, walkPrefix)?.Item1;
                built.Searches += probe.Searches;
                if (walk == null) { built.Failure = probe.Failure; return; }
            }
            // The fewest idle ticks after which the walk still buys it (cash only grows while idling; the 5 below catch a frame phase).
            bool Buys(int k) => TryPlay(start, inputBase, idle.GetRange(0, k).Concat(walk).ToList(), j) != null;
            int lo = 0, hi = affordAt;
            while (lo < hi) { int mid = (lo + hi) / 2; if (Buys(mid)) hi = mid; else lo = mid + 1; }
            for (int k = Math.Max(0, lo - 5); k < lo; k++) if (Buys(k)) { lo = k; break; }
            if (!(TryPlay(start, inputBase, idle.GetRange(0, lo).Concat(walk).ToList(), j) is var (steps, at)))
            {
                built.Failure = new Failure { Kind = "unreachable", Goal = j, Label = label, Text = $"the walk to {label} found after the wait doesn't buy it" };
                return;
            }
            wait.Ticks = lo;
            if (lo > 0) built.Waits.Add(wait);
            Append(built, j, steps, at);
        }
    }
}
