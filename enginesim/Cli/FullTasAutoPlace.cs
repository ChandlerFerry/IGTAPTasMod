using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Caps = IGTAPTasMod.Caps;

namespace IGTAP.EngineSim.Cli
{
    // --auto's place lever: a window from goal k-1 seeded with what the best's way there could become, then searched as the window
    // lever's is.
    // - Splice by place: the known category routes (standing seeds with a category start trace) that pass where the best does, by
    //   position, velocity and facing (the transplant's Reference.NearestInput), whatever their goal: Zip%'s way from the atom box
    //   up the shaft (goal 511) passes bonus 11's route through the atom room, and bonus 11 is no goal of the run. Each is picked up
    //   at the best's state nearest its path and played on, alone and back into the best's own inputs where its path meets the
    //   best's again.
    // - Pause a lifting dash: every dash of the best's window and of those splices tried with the game paused on it (pause frames,
    //   the dash tick and the frame it opens on); one that peaks LiftMin higher than unpaused lifted the player inside a wall (a
    //   pauselift), and the route goes on from the top as it was, or by a known route's moves from there.
    // The window's finds get the same pauses and splices again (their climbs are new), searched once more.
    sealed partial class FullTas
    {
        // A known route serves a window when its category start is this near the best's way through it (world units).
        const float PlaceReach = 3000f;
        // A paused dash peaking this much above the same dash unpaused is a pauselift (unpaused, a turned dash's step-ups lift
        // about 6 × 2 u a tick: Test-Pauselift's 6 against 35 paused).
        const float LiftMin = 40f;
        // The inputs after a dash a pause is also tried on, and the inputs after those its peak is measured over; exits kept per
        // entry; the splices lifted; the finds lifted again; the seeds a window takes (the solver plays them on one worker first).
        const int LiftOn = 2, LiftAfter = 3, PlaceExits = 3, PlaceBases = 6, PlaceFinds = 3, PlaceSeedCap = 192;
        static readonly int[] LiftPauses = { 20, 40, 60 }, LiftTicks = { 0, 1, 2 }, LiftFrames = { 0, 1, 2, 3, 4, 5 };

        readonly Dictionary<string, Reference> _placePaths = new Dictionary<string, Reference>();
        readonly Dictionary<string, SeedStart> _placeStarts = new Dictionary<string, SeedStart>();
        /// <summary>Goals (and the best's tick there) the place lever has run at: its seeds are the same until the best changes.</summary>
        readonly HashSet<(int, long)> _placed = new HashSet<(int, long)>();

        bool PlaceDone(int k) => _placed.Contains((k, _best.GoalPhysics[k - 1]));

        /// <summary>The player after each count of a stretch's inputs played from at (1-based; a dash's no-input ticks: its last).</summary>
        sealed class PlaceRow
        {
            public int N;
            public WorldPos P;
            public UnityEngine.Vector2 V;
            public bool Right;
        }

        string PullPlace(int k, double budget)
        {
            Route best = _best;
            _placed.Add((k, best.GoalPhysics[k - 1]));
            int limit = Math.Min(_goals, k + 1), prefixLength = PrefixAt(best, k);
            int end = (int)Math.Min(best.GoalInput[limit - 1], best.Inputs.Count) - prefixLength;
            SimSnapshot at = AdvanceFrom(_origin, 0, best.Inputs.GetRange(0, prefixLength));
            if (at == null) return $"goals {k}-{limit}: the best doesn't replay to goal {k - 1}";
            List<InputTick> window = best.Inputs.Skip(prefixLength).ToList();
            List<PlaceRow> rows = PlaceRows(at, prefixLength, window, end);
            if (rows.Count == 0) return $"goals {k}-{limit}: no way to splice into";
            string caps = WindowCaps(_runGoals.Goals.Skip(k - 1).Take(limit - k + 1));
            List<(string name, List<InputTick> inputs, Reference path)> paths = PlacePaths(rows, caps);
            var seeds = new List<(double rank, string how, List<InputTick> inputs)>();
            List<(double rank, string how, List<InputTick> inputs)> spliced = Placed(rows, window, 0, paths, rows, window);
            seeds.AddRange(spliced);
            int lifts = 0;
            foreach (List<InputTick> b in new[] { window }.Concat(spliced.OrderBy(s => s.rank).Take(PlaceBases).Select(s => s.inputs)))
                foreach (var lift in Lifts(at, prefixLength, b))
                {
                    lifts++;
                    // Ahead of every splice: a lift is the rarer find.
                    seeds.Add((-1e6 - lift.rise, lift.how, lift.inputs));
                    seeds.AddRange(Placed(PlaceRows(at, prefixLength, lift.inputs, lift.inputs.Count), lift.inputs, lift.d + 1, paths, rows, window));
                }
            Log($"place goals {k}-{limit}: {paths.Count} known routes near ({string.Join(", ", paths.Select(p => p.name))}), {spliced.Count} splices, {lifts} pauselifts, {seeds.Count} seeds");
            if (seeds.Count == 0) return $"goals {k}-{limit}: no known route passes here and no dash lifts paused";
            var ordered = new List<List<InputTick>> { window };
            ordered.AddRange(seeds.OrderBy(s => s.rank).Take(PlaceSeedCap).Select(s => s.inputs));
            List<List<InputTick>> found = SearchWindow("place", best, prefixLength, limit, ordered, best.GoalPhysics[limit - 1], budget, 0);
            int first = found.Count;
            if (found.Count > 0 && !TimeUp)
            {
                var again = new List<(double rank, string how, List<InputTick> inputs)>();
                foreach (List<InputTick> f in Enumerable.Reverse(found).Take(PlaceFinds).ToList())
                    foreach (var lift in Lifts(at, prefixLength, f))
                    {
                        again.Add((-1e6 - lift.rise, lift.how, lift.inputs));
                        again.AddRange(Placed(PlaceRows(at, prefixLength, lift.inputs, lift.inputs.Count), lift.inputs, lift.d + 1, paths, rows, window));
                    }
                Log($"place goals {k}-{limit}: {found.Count} finds; {again.Count} seeds from their pauselifts");
                if (again.Count > 0)
                    found.AddRange(SearchWindow("place", best, prefixLength, limit, again.OrderBy(s => s.rank).Take(PlaceSeedCap).Select(s => s.inputs).ToList(),
                        best.GoalPhysics[limit - 1], budget / 2, 0));
            }
            if (found.Count == 0) return $"goals {k}-{limit}: nothing faster ({paths.Count} known routes, {lifts} pauselifts, {seeds.Count} seeds)";
            List<InputTick> prefix = best.Inputs.Take(prefixLength).ToList();
            string how = $"place {k}-{limit} #{_pullNo}";
            foreach (List<InputTick> f in Enumerable.Reverse(found).Take(5)) Collect(Evaluate(prefix.Concat(f).ToList()), how);
            _grafted.Add((how, limit));
            MergeAuto($"faster through goals {k}-{limit} (place, auto)", best, prefixLength, limit, found);
            return $"goals {k}-{limit}: {found.Count} faster through goal {limit} ({found.Count - first} after the finds' pauselifts)";
        }

        List<PlaceRow> PlaceRows(SimSnapshot at, int prefixLength, List<InputTick> inputs, int count)
        {
            var rows = new Dictionary<int, PlaceRow>();
            _eval.SetStart(at, prefixLength);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs, null, n => n <= count, 0, _ =>
            {
                int n = (int)_eval.Engine.RunTicks;
                if (n >= 1 && n <= count) rows[n] = new PlaceRow { N = n, P = _eval.PlayerWorld, V = _eval.Player.Velocity + _eval.Player.momentum, Right = _eval.Player.facingRight };
            });
            return rows.Values.OrderBy(r => r.N).ToList();
        }

        /// <summary>The known category routes (standing seeds the window's unlocks cover) whose category start is within PlaceReach of
        /// the rows, with their own paths from it (each played once, then kept).</summary>
        List<(string name, List<InputTick> inputs, Reference path)> PlacePaths(List<PlaceRow> rows, string windowCaps)
        {
            var paths = new List<(string, List<InputTick>, Reference)>();
            Caps caps = Caps.Parse(windowCaps);
            string dir = Path.GetDirectoryName(Path.GetFullPath(_trace));
            float x0 = rows.Min(r => r.P.x) - PlaceReach, x1 = rows.Max(r => r.P.x) + PlaceReach;
            float y0 = rows.Min(r => r.P.y) - PlaceReach, y1 = rows.Max(r => r.P.y) + PlaceReach;
            foreach (Seed s in _seeds.Where(s => s.Standing && s.At == null && s.Inputs.Count > 0 && s.Caps.IsSubsetOf(caps)))
            {
                string trace = TechGrafts.CategoryStart(dir, s.Name.Split('_')[0].ToLowerInvariant(), s.Caps.Id);
                if (trace == null) continue;
                if (!_placeStarts.TryGetValue(trace, out SeedStart start))
                {
                    try { start = StartOf(trace); } catch (Exception) { start = null; }
                    _placeStarts[trace] = start;
                }
                if (start == null || start.X < x0 || start.X > x1 || start.Y < y0 || start.Y > y1) continue;
                if (!_placePaths.TryGetValue(s.Name, out Reference path)) _placePaths[s.Name] = path = CategoryPath(trace, s.Inputs);
                if (path != null) paths.Add((s.Name, s.Inputs, path));
            }
            return paths;
        }

        /// <summary>
        /// Each known route picked up where the stretch (its rows, after input from) stands nearest its path, a few inputs either way:
        /// played to its end, and back into the best's window inputs (exit rows) where its path comes nearest the best's later.
        /// Ranked by how far off the joins are.
        /// </summary>
        static List<(double rank, string how, List<InputTick> inputs)> Placed(List<PlaceRow> rows, List<InputTick> inputs, int from,
            List<(string name, List<InputTick> inputs, Reference path)> paths, List<PlaceRow> exits, List<InputTick> window)
        {
            var found = new List<(double, string, List<InputTick>)>();
            foreach (var (name, known, path) in paths)
            {
                var entries = rows.Where(r => r.N > from)
                    .Select(r => (r.N, j: path.NearestInput(r.P, r.V, r.Right, out float miss), miss))
                    .Where(e => e.j >= 0 && e.miss <= SpliceMiss).OrderBy(e => e.miss).Take(SplicePoints).ToList();
                foreach (var (n, j0, miss) in entries)
                    for (int d = -2; d <= 2; d++)
                    {
                        int j = j0 + 1 + d;
                        if (j < 0 || j >= known.Count) continue;
                        found.Add((miss, $"{name} from its input {j} after input {n}", inputs.Take(n).Concat(known.Skip(j)).ToList()));
                        if (path.FirstTick[j] < 0) continue;
                        // Its path from there against the best's later rows: the nearest per known input, the PlaceExits nearest.
                        var near = new Dictionary<int, (float miss, int b)>();
                        for (int t = path.FirstTick[j]; t < path.X.Length; t++)
                        {
                            int e = path.InputAt[t];
                            if (e < j) continue;
                            foreach (PlaceRow r in exits)
                            {
                                if (r.N <= n) continue;
                                float dx = r.P.x - path.X[t], dy = r.P.y - path.Y[t], dvx = r.V.x - path.VX[t], dvy = r.V.y - path.VY[t];
                                float m = (float)Math.Sqrt(dx * dx + dy * dy) + .02f * (float)Math.Sqrt(dvx * dvx + dvy * dvy) + (r.Right == path.Right[t] ? 0f : 20f);
                                if (m <= SpliceMiss && (!near.TryGetValue(e, out var was) || m < was.miss)) near[e] = (m, r.N);
                            }
                        }
                        foreach (var (e, x) in near.OrderBy(p => p.Value.miss).Take(PlaceExits))
                            found.Add((miss + x.miss, $"{name} inputs {j}-{e} after input {n}, then the best's from {x.b}",
                                inputs.Take(n).Concat(known.Skip(j).Take(e - j + 1)).Concat(window.Skip(x.b)).ToList()));
                    }
            }
            return found;
        }

        /// <summary>
        /// Each dash of the stretch with the game paused on it or on one of the LiftOn inputs after it (bonus11_WJ_DS_DJ_BS 2.86 pauses
        /// the input after its dash), the pause that peaks highest over those inputs and LiftAfter more, kept when it peaks LiftMin
        /// above the stretch unpaused. None for a class without major techs (a pauselift is one).
        /// </summary>
        List<(float rise, int d, string how, List<InputTick> inputs)> Lifts(SimSnapshot at, int prefixLength, List<InputTick> inputs)
        {
            var lifts = new List<(float, int, string, List<InputTick>)>();
            if (NoMajors) return lifts;
            for (int d = 0; d < inputs.Count && !TimeUp; d++)
            {
                if (!inputs[d].dash) continue;
                int span = Math.Min(inputs.Count - d, LiftOn + 1);
                if (Enumerable.Range(d, span).Any(i => inputs[i].pause > 0)) continue;
                SimSnapshot s = AdvanceFrom(at, prefixLength, inputs.GetRange(0, d));
                if (s == null) break;
                List<InputTick> seq = inputs.Skip(d).Take(span + LiftAfter).ToList();
                float Peak()
                {
                    _eval.SetStart(s, prefixLength + d);
                    _eval.Goals.Limit = _goals;
                    float top = float.MinValue;
                    RunResult r = _eval.Run(seq, null, null, 0, _ => top = Math.Max(top, _eval.PlayerWorld.y));
                    return r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal) ? float.NaN : top;
                }
                float plain = Peak(), high = float.MinValue;
                if (float.IsNaN(plain)) continue;
                (int on, InputTick k) lifted = default;
                for (int on = 0; on < span; on++)
                {
                    InputTick was = seq[on];
                    foreach (int pause in LiftPauses)
                        foreach (int tick in LiftTicks)
                            foreach (int frame in LiftFrames)
                            {
                                InputTick k = was;
                                k.pause = pause;
                                k.pauseTick = tick;
                                k.pauseFrame = frame;
                                seq[on] = k;
                                float y = Peak();
                                if (y > high) { high = y; lifted = (on, k); }
                            }
                    seq[on] = was;
                }
                if (high - plain < LiftMin) continue;
                var list = inputs.ToList();
                list[d + lifted.on] = lifted.k;
                lifts.Add((high - plain, d + lifted.on, $"dash at window input {d} paused {lifted.k.pause} frames on input {d + lifted.on}'s tick {lifted.k.pauseTick} "
                    + $"frame {lifted.k.pauseFrame}: peaks at y {high:0}, {high - plain:0} above unpaused", list));
            }
            return lifts;
        }
    }
}
