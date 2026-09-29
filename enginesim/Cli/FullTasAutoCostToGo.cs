using System;
using System.Collections.Generic;

namespace IGTAP.EngineSim.Cli
{
    // Stage 2 of --auto (docs/fulltas.md, "Auto"): the cost-to-go. Every arrival at a goal gets an estimate of the physics ticks
    // from it to the run's end, so a window can be scored "arrival tick + ticks-to-finish" and prefer an arrival that reaches its
    // goal later but heads for the end faster. It is learned, per goal, from the library's own arrivals that have a known way on
    // to the end (ICostToGo.Fit): a k-nearest-neighbour regression over the arrival features (place, velocity, momentum, dashes
    // and air jumps, facing and ground, the run's purchases and cash), with the median split times between goals as the fallback
    // where no arrival is known yet. Neighbours come from the goal's own arrivals; one with a different purchase set is kept in
    // but pays a penalty, so an arrival after a changed buy still gets an estimate.
    sealed class FullTasAutoCostToGo : ICostToGo
    {
        /// <summary>One arrival with a known way on to the end: its features and the true ticks to the run's end from it.</summary>
        public struct Sample
        {
            public int Goal;
            public LibFeatures F;
            /// <summary>Physics ticks from the arrival to the run's end (the library's ToEnd).</summary>
            public double ToEnd;
            /// <summary>Where the arrival sits on its run (for the goal-to-goal step medians).</summary>
            public long Tick;
            public int Run;
        }

        sealed class GoalModel
        {
            public Sample[] Samples = Array.Empty<Sample>();
            /// <summary>Each feature's spread over the goal's arrivals (floor-clamped): one unit of distance is one spread.</summary>
            public double[] Scale;
            /// <summary>The median ticks to the end over the goal's arrivals: the fallback's anchor.</summary>
            public double Median;
        }

        // The continuous features' order in a distance: X, Y, VX, VY, MX, MY, dashes, air jumps, cash.
        const int Dims = 9;
        const int Neighbors = 8;
        /// <summary>Goals with fewer arrivals than this are blended with the fallback (the median split times).</summary>
        const int BlendAt = 4;
        const double Eps = 0.04;
        // Facing and ground cost this much in distance squared when they differ, and a different purchase set this much more.
        const double FacePenalty = 0.25, GroundPenalty = 0.35, BuysPenalty = 2.25;
        static readonly double[] Floor = { 16.0, 16.0, 24.0, 24.0, 24.0, 24.0, 1.0, 1.0, 50.0 };
        static readonly double[] Weight = { 1.0, 1.0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.25 };

        GoalModel[] _model;
        /// <summary>Step medians as a prefix sum: the chain from goal g to goal j is _stepPrefix[j] - _stepPrefix[g].</summary>
        double[] _stepPrefix;
        /// <summary>The nearest goal at or after g with arrivals (0: none), and the nearest before it.</summary>
        int[] _nextWith, _prevWith;

        /// <summary>The run's goal count when known (0: the library's states say where the goals end).</summary>
        readonly int _goals;

        public FullTasAutoCostToGo(int goals) { _goals = goals; }

        /// <summary>Estimated physics ticks from an arrival at <paramref name="goal"/> to the run's end.</summary>
        public double Ticks(int goal, in LibFeatures features)
        {
            if (_model == null) throw new InvalidOperationException("the cost-to-go is fitted first (Fit)");
            int g = Math.Max(1, Math.Min(goal, _model.Length - 1));
            GoalModel m = _model[g];
            int n = m.Samples.Length;
            if (n == 0) return Fallback(g);
            Span<double> bestD = stackalloc double[Neighbors];
            Span<int> bestI = stackalloc int[Neighbors];
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                double d = Dist2(in features, in m.Samples[i], m.Scale);
                if (count == Neighbors && d >= bestD[Neighbors - 1]) continue;
                int at = count;
                while (at > 0 && bestD[at - 1] > d)
                {
                    if (at < Neighbors) { bestD[at] = bestD[at - 1]; bestI[at] = bestI[at - 1]; }
                    at--;
                }
                if (at < Neighbors)
                {
                    bestD[at] = d;
                    bestI[at] = i;
                    if (count < Neighbors) count++;
                }
            }
            double sumW = 0, sum = 0;
            for (int i = 0; i < count; i++)
            {
                double w = 1.0 / (bestD[i] + Eps);
                sumW += w;
                sum += w * m.Samples[bestI[i]].ToEnd;
            }
            double knn = sum / sumW;
            return n < BlendAt ? (n * knn + (BlendAt - n) * Fallback(g)) / BlendAt : knn;
        }

        double Dist2(in LibFeatures f, in Sample s, double[] scale)
        {
            Span<double> a = stackalloc double[Dims];
            Span<double> b = stackalloc double[Dims];
            a[0] = f.X; a[1] = f.Y; a[2] = f.VX; a[3] = f.VY; a[4] = f.MX; a[5] = f.MY;
            a[6] = f.DashesLeft; a[7] = f.AirJumpsLeft; a[8] = f.Cash;
            b[0] = s.F.X; b[1] = s.F.Y; b[2] = s.F.VX; b[3] = s.F.VY; b[4] = s.F.MX; b[5] = s.F.MY;
            b[6] = s.F.DashesLeft; b[7] = s.F.AirJumpsLeft; b[8] = s.F.Cash;
            double d = 0;
            for (int i = 0; i < Dims; i++)
            {
                double delta = (a[i] - b[i]) / scale[i];
                d += Weight[i] * delta * delta;
            }
            if (f.FacingRight != s.F.FacingRight) d += FacePenalty;
            if (f.OnGround != s.F.OnGround) d += GroundPenalty;
            if (f.BuysHash != s.F.BuysHash) d += BuysPenalty;
            return d;
        }

        /// <summary>What to expect when the goal has no arrival of its own: the median splits on to the next goal that has some.</summary>
        double Fallback(int g)
        {
            int j = _nextWith[g];
            if (j > 0) return Math.Max(1, _stepPrefix[j] - _stepPrefix[g] + _model[j].Median);
            int i = _prevWith[g];
            if (i > 0) return Math.Max(1, _model[i].Median - (_stepPrefix[g] - _stepPrefix[i]));
            return 1;
        }

        /// <summary>Refits from every library state with a known way on to the end (call after each new best or segment).</summary>
        public void Fit(IStateLibrary library)
        {
            var samples = new List<Sample>();
            var ticks = new List<(int run, int goal, long tick)>();
            var seen = new HashSet<(int, int)>();
            int goals = 0, limit = _goals > 0 ? _goals : 4096;
            for (int g = 1; g <= limit; g++)
            {
                IReadOnlyList<LibState> at = library.StatesAt(g);
                if (at.Count == 0)
                {
                    // A gap: keep probing a little past it before calling the run's goals done.
                    if (_goals == 0 && g > goals + 2) break;
                    continue;
                }
                goals = g;
                foreach (LibState s in at)
                {
                    if (s.Goal != g) continue;
                    if (seen.Add((s.Run, g))) ticks.Add((s.Run, g, s.Tick));
                    long toEnd = library.ToEnd(s.Id);
                    if (toEnd != long.MaxValue)
                        samples.Add(new Sample { Goal = g, F = s.Features, ToEnd = toEnd, Tick = s.Tick, Run = s.Run });
                }
            }
            FitCore(Math.Max(goals, 1), samples, ticks);
        }

        void FitCore(int goals, List<Sample> samples, List<(int run, int goal, long tick)> ticks)
        {
            foreach (Sample s in samples) goals = Math.Max(goals, s.Goal);
            foreach (var t in ticks) goals = Math.Max(goals, t.goal);
            var model = new GoalModel[goals + 2];
            for (int g = 0; g < model.Length; g++) model[g] = new GoalModel();
            var byGoal = new List<Sample>[model.Length];
            foreach (Sample s in samples)
            {
                int g = Math.Min(Math.Max(1, s.Goal), model.Length - 1);
                (byGoal[g] ??= new List<Sample>()).Add(s);
            }
            for (int g = 1; g < model.Length; g++)
            {
                List<Sample> list = byGoal[g];
                if (list == null || list.Count == 0) continue;
                GoalModel m = model[g];
                m.Samples = list.ToArray();
                m.Scale = Scales(m.Samples);
                var ends = new double[m.Samples.Length];
                for (int i = 0; i < ends.Length; i++) ends[i] = m.Samples[i].ToEnd;
                Array.Sort(ends);
                m.Median = ends[ends.Length / 2];
            }
            // The median step from each goal to the next, over the runs that reach both.
            var runTicks = new Dictionary<int, SortedDictionary<int, long>>();
            foreach (var (run, goal, tick) in ticks)
            {
                if (!runTicks.TryGetValue(run, out SortedDictionary<int, long> map)) runTicks[run] = map = new SortedDictionary<int, long>();
                map[goal] = map.TryGetValue(goal, out long was) ? Math.Min(was, tick) : tick;
            }
            var step = new double[goals + 2];
            var stepCount = new int[goals + 2];
            foreach (SortedDictionary<int, long> map in runTicks.Values)
            {
                int last = -1;
                long at = 0;
                foreach (KeyValuePair<int, long> kv in map)
                {
                    if (last >= 0 && kv.Key == last + 1 && kv.Value > at)
                    {
                        int g = Math.Min(last, step.Length - 2);
                        step[g] += kv.Value - at;
                        stepCount[g]++;
                    }
                    last = kv.Key;
                    at = kv.Value;
                }
            }
            double all = 0;
            int allCount = 0;
            for (int g = 0; g < step.Length; g++)
                if (stepCount[g] > 0) { step[g] /= stepCount[g]; all += step[g]; allCount++; }
            double typical = allCount > 0 ? all / allCount : 24.0;
            for (int g = 1; g < step.Length; g++) if (stepCount[g] == 0) step[g] = typical;
            var prefix = new double[step.Length + 1];
            for (int g = 0; g < step.Length; g++) prefix[g + 1] = prefix[g] + step[g];
            var next = new int[model.Length];
            var prev = new int[model.Length];
            int at2 = 0;
            for (int g = model.Length - 1; g >= 1; g--)
            {
                if (model[g].Samples.Length > 0) at2 = g;
                next[g] = at2;
            }
            at2 = 0;
            for (int g = 1; g < model.Length; g++)
            {
                if (model[g].Samples.Length > 0) at2 = g;
                prev[g] = at2;
            }
            _model = model;
            _stepPrefix = prefix;
            _nextWith = next;
            _prevWith = prev;
        }

        static double[] Scales(Sample[] samples)
        {
            Span<double> lo = stackalloc double[Dims];
            Span<double> hi = stackalloc double[Dims];
            for (int i = 0; i < Dims; i++) { lo[i] = double.MaxValue; hi[i] = double.MinValue; }
            Span<double> a = stackalloc double[Dims];
            foreach (Sample s in samples)
            {
                a[0] = s.F.X; a[1] = s.F.Y; a[2] = s.F.VX; a[3] = s.F.VY; a[4] = s.F.MX; a[5] = s.F.MY;
                a[6] = s.F.DashesLeft; a[7] = s.F.AirJumpsLeft; a[8] = s.F.Cash;
                for (int i = 0; i < Dims; i++)
                {
                    if (a[i] < lo[i]) lo[i] = a[i];
                    if (a[i] > hi[i]) hi[i] = a[i];
                }
            }
            var scale = new double[Dims];
            for (int i = 0; i < Dims; i++)
                scale[i] = Math.Max((hi[i] - lo[i]) / 2, Floor[i]);
            return scale;
        }
    }
}
