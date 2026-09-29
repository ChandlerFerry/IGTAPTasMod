using System;
using System.Collections.Generic;
using System.Linq;

namespace IGTAP.EngineSim.Cli
{
    // Stage 3 (docs/fulltas.md, "Auto"): the pull scheduler. Where the old --auto bandit ranked (lever, goal) arms by expected
    // ticks per CPU second and pulled the winner for minutes at a time (its long pulls: 2,780 CPU s on VMAN goal 7 and 1,278
    // CPU s on FalseEnding goal 181, both for no gain), this runs successive halving over every goal region at once. Each
    // region gets a small first slice (FirstSliceCpu on SliceThreads threads); after each round the top third by DAG
    // improvement per CPU second stays in and its next slice doubles (the region's slice doubles per pull); the rest are
    // dropped until a neighbour's gain re-opens them. Every pull is capped at MaxPullCpu, so no single repair can burn
    // minutes. Next() returns null once every region is dropped or retired ("nothing left worth pulling").
    //
    // Wiring (FullTasAuto.cs): build the regions from the goal labels (one region per distinct label: FalseEnding's 300 goals
    // are ~36 lap phases, and first-round screening cost scales with the region count), then loop: a batch of NextWave(max)
    // pulls runs concurrently (max = MaxConcurrent), each is Reported with the whole run's gain and the CPU it used.
    sealed class PullScheduler : IPullScheduler
    {
        /// <summary>A goal region: the goals [First, Limit] a pull covers (1-based, inclusive).</summary>
        public sealed class Region
        {
            public int First, Limit;
            /// <summary>Its name (the goal label for a label group): what a log line says.</summary>
            public string Key;
            /// <summary>The model's expected headroom in ticks (0: unknown); only breaks ties where no pull has gained yet.</summary>
            public double Expected;

            public Region() { }
            public Region(int first, int limit, string key = null, double expected = 0)
            {
                First = first; Limit = limit; Key = key ?? (first == limit ? first.ToString() : first + "-" + limit); Expected = expected;
            }

            /// <summary>One region per distinct label, over the goals that carry it (the lap phases of a repeated run).</summary>
            public static List<Region> FromLabels(string[] labels, double[] expected = null)
            {
                var groups = new List<Region>();
                var byKey = new Dictionary<string, Region>();
                for (int k = 1; k <= labels.Length; k++)
                {
                    string key = labels[k - 1] ?? "";
                    if (!byKey.TryGetValue(key, out Region r))
                    {
                        byKey[key] = r = new Region(k, k, key);
                        groups.Add(r);
                    }
                    r.Limit = k;
                    if (expected != null && k - 1 < expected.Length) r.Expected = Math.Max(r.Expected, expected[k - 1]);
                }
                return groups;
            }
        }

        public sealed class Options
        {
            /// <summary>Every region's first slice (CPU seconds; 10 CPU s = 5 wall s on the default 2 slice threads).</summary>
            public double FirstSliceCpu = 10;
            /// <summary>Threads per pull (1-2: small concurrent slices).</summary>
            public int SliceThreads = 2;
            /// <summary>The hard cap on one pull's CPU seconds; the slice never grows past it.</summary>
            public double MaxPullCpu = 120;
            /// <summary>Keep this top share of the regions each round (1/3).</summary>
            public int KeepNumerator = 1, KeepDenominator = 3;
            /// <summary>Retire a capped region after this many pulls in a row with no gain.</summary>
            public int Patience = 2;
            /// <summary>Retire a region once it has spent this much CPU in total (0: only the cap and patience retire it).</summary>
            public double MaxRegionCpu = 0;
            /// <summary>Goals of slack around a gained pull's span whose regions count as neighbours (re-opened on a gain).</summary>
            public int NeighbourSlack = 1;
            /// <summary>The lever a pull names (the wiring's window search).</summary>
            public string Lever = "window";
            /// <summary>The machine's thread count: how many pulls a batch runs side by side.</summary>
            public int Threads = 24;
            /// <summary>The whole run's CPU budget; shrinks the first slice if screening every region would eat over a third of it.</summary>
            public double TotalCpuSeconds = 0;
        }

        sealed class State
        {
            public Region Region;
            public int Pulls, Fruitless;
            public double Cpu, Gain, Dag, Slice;
            public bool Dropped, Retired, Reopened;
        }

        readonly Options _o;
        readonly List<State> _states;
        readonly Dictionary<int, State> _byPull = new Dictionary<int, State>();
        readonly Queue<State> _round = new Queue<State>();
        readonly List<State> _justPulled = new List<State>();
        int _outstanding, _pullNo;
        double _totalCpu;
        long _totalGain;

        public PullScheduler(string[] labels, int threads, double totalCpuSeconds = 0, double[] expected = null, Options options = null)
            : this(Region.FromLabels(labels, expected), threads, totalCpuSeconds, options) { }

        public PullScheduler(IReadOnlyList<Region> regions, int threads, double totalCpuSeconds = 0, Options options = null)
        {
            if (regions == null || regions.Count == 0) throw new ArgumentException("the scheduler needs at least one goal region");
            _o = options ?? new Options();
            if (threads > 0) _o.Threads = threads;
            _o.TotalCpuSeconds = totalCpuSeconds;
            if (_o.SliceThreads < 1) _o.SliceThreads = 1;
            if (_o.FirstSliceCpu <= 0) _o.FirstSliceCpu = 1;
            // Screening every region once must not eat over a third of the run's budget.
            if (_o.TotalCpuSeconds > 0 && regions.Count * _o.FirstSliceCpu > _o.TotalCpuSeconds / 3)
                _o.FirstSliceCpu = Math.Max(1, _o.TotalCpuSeconds / (3.0 * regions.Count));
            _states = regions.Select(r => new State { Region = r, Slice = _o.FirstSliceCpu }).ToList();
        }

        /// <summary>Pulls a batch runs side by side (the machine's threads over a slice's threads).</summary>
        public int MaxConcurrent => Math.Max(1, _o.Threads / _o.SliceThreads);
        public int Regions => _states.Count;
        public double TotalCpu => _totalCpu;
        public long TotalGain => _totalGain;

        /// <summary>The region a pull was handed out for.</summary>
        public Region RegionOf(Pull pull) { return pull != null && _byPull.TryGetValue(pull.Id, out State s) ? s.Region : null; }

        /// <summary>The contract's one-at-a-time face: the next pull, or null while its earlier pulls are still unreported (the
        /// next round keeps the top third of this one, so it waits for their results) or when nothing is worth pulling.</summary>
        public Pull Next()
        {
            State s = NextState();
            return s == null ? null : MakePull(s);
        }

        /// <summary>Up to <paramref name="max"/> pulls of the current round (the batch to run concurrently).</summary>
        public List<Pull> NextWave(int max)
        {
            var batch = new List<Pull>();
            while (batch.Count < max)
            {
                State s = NextState();
                if (s == null) break;
                batch.Add(MakePull(s));
            }
            return batch;
        }

        State NextState()
        {
            for (;;)
            {
                if (_round.Count > 0) return _round.Dequeue();
                if (_outstanding > 0) return null;              // the round's results decide who is in the next one
                if (!StartRound()) return null;                 // every region is dropped or retired
            }
        }

        Pull MakePull(State s)
        {
            var pull = new Pull
            {
                Id = ++_pullNo,
                Lever = _o.Lever,
                First = s.Region.First,
                Limit = s.Region.Limit,
                CpuSeconds = Math.Min(s.Slice, _o.MaxPullCpu),
                Threads = _o.SliceThreads,
            };
            _byPull[pull.Id] = s;
            _justPulled.Add(s);
            _outstanding++;
            return pull;
        }

        /// <summary>The next round: every kept region (and every neighbour a gain re-opened) gets its next slice, promising
        /// regions first so a gain lands early in the round.</summary>
        bool StartRound()
        {
            foreach (State s in _states)
                if (s.Reopened) { s.Dropped = s.Retired = false; s.Reopened = false; }
            var active = _states.Where(s => !s.Dropped && !s.Retired).OrderByDescending(Score).ThenByDescending(s => s.Region.Expected)
                .ThenBy(s => s.Cpu).ThenBy(s => s.Region.First).ToList();
            if (active.Count == 0) return false;
            foreach (State s in active) _round.Enqueue(s);
            return true;
        }

        double Score(State s) { return s.Cpu > 0 ? (s.Gain + s.Dag) / s.Cpu : 0; }

        /// <summary>What the pull gave: the whole run's gain (the library's shortest path), the CPU it used, and optionally the
        /// extra DAG improvement (an arrival or segment win that is not yet in the shortest path).</summary>
        public void Report(Pull pull, long ticksGained, double cpuSeconds) { Report(pull, ticksGained, cpuSeconds, 0); }

        public void Report(Pull pull, long ticksGained, double cpuSeconds, long dagGain)
        {
            if (pull == null || !_byPull.TryGetValue(pull.Id, out State s)) throw new ArgumentException("the scheduler did not hand this pull out");
            double cpu = Math.Max(0, cpuSeconds);
            s.Pulls++;
            s.Cpu += cpu;
            s.Gain += Math.Max(0, ticksGained);
            s.Dag += Math.Max(0, dagGain);
            _totalCpu += cpu;
            _totalGain += Math.Max(0, ticksGained);
            _outstanding--;
            // The region's slice doubles for its next pull (halving's doubling); MaxPullCpu caps what a pull is handed.
            s.Slice *= 2;
            if (ticksGained > 0 || dagGain > 0)
            {
                s.Fruitless = 0;
                ReopenNeighbours(s);
            }
            // Retire only after Patience pulls that already ran at the cap gave nothing: a smaller cap must not retire a
            // region one pull before its slice could still have paid for a find's repair.
            else if (pull.CpuSeconds >= _o.MaxPullCpu && ++s.Fruitless >= _o.Patience) s.Retired = true;
            if (_o.MaxRegionCpu > 0 && s.Cpu >= _o.MaxRegionCpu) s.Retired = true;
            // The round ends when its last pull is in: the bottom two thirds drop out until a neighbour's gain re-opens them.
            if (_outstanding == 0 && _round.Count == 0 && _justPulled.Count > 0)
            {
                int keep = Math.Max(1, (_justPulled.Count * _o.KeepNumerator + _o.KeepDenominator - 1) / _o.KeepDenominator);
                foreach (State d in _justPulled.OrderByDescending(Score).ThenByDescending(x => x.Region.Expected).ThenBy(x => x.Cpu)
                    .ThenBy(x => x.Region.First).Skip(keep))
                    d.Dropped = true;
                _justPulled.Clear();
            }
        }

        /// <summary>A gain through goals [First, Limit] changes the states its neighbours arrive at, so their old "nothing
        /// there" verdicts are stale: they go back into the next round.</summary>
        void ReopenNeighbours(State changed)
        {
            int a = changed.Region.First - _o.NeighbourSlack, b = changed.Region.Limit + _o.NeighbourSlack;
            foreach (State s in _states)
                if (s != changed && s.Region.First <= b && s.Region.Limit >= a) s.Reopened = true;
        }
    }
}
