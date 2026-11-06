using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// <c>ease &lt;course-trace.json&gt; [route.json] [--seed route.json ...] [--minutes M] [--search-minutes S] [--slack S] [--max-time S]
    /// [--class human-nmg|human-major] [--threads T]</c>: easier routes for a human class than the fastest, slower ones included, to
    /// pick from; a human class's Improve (<c>solve --auto --class</c>) runs it with the studio's seeds. Two parts, one clock
    /// (<c>--minutes</c>, 10):
    /// <list type="bullet">
    /// <item>Breadth (<c>--search-minutes</c>, 40 %): the offline solver searches the course unseeded, so any way, not only the known
    /// route's, with only inputs a player spaces out (<see cref="HumanSpaced"/>), keeping every finish up to <c>--slack</c> (1.5) s
    /// behind its best. The finishes are grouped by their way (<see cref="Way"/>: the walls and floors they settle on, by side) and
    /// each way's fastest is measured.</item>
    /// <item>Depth: from every seed that finishes as a human class plays it (each also with its part tilts snapped) and every way
    /// measured, edits near the inputs that make a route hard (its frame-perfect
    /// inputs and back-to-back pairs: <see cref="Mutate"/>), each child that still finishes as a human class plays it, under the time
    /// limit (<c>--max-time</c>, else the best plus the slack), measured. Spacing is no longer required: the measure judges.</item>
    /// </list>
    /// The search measures quickly (its own inputs, a radius of 3: a window it cuts short only counts against a route); the routes
    /// no other beats on time, frame-perfect inputs and back-to-back pairs at once are measured again as the gate's techs-used does
    /// (humanize's version, spike grace, spans), listed and written to traces\human-finds\&lt;course&gt;_&lt;caps&gt;-&lt;class&gt;\. The lowest
    /// score, when it beats every seed's, is written to offline-best for the plugin's gate (the class's record: a strictly lower
    /// score, RULES.md 54); any other pick copied there under the name the list gives is judged the same way.
    /// </summary>
    static partial class Difficulty
    {
        const string EaseUsage = "usage: ease <course-trace.json> [route.json] [--minutes M] [--search-minutes S] [--slack S] [--max-time S] [--class human-nmg|human-major] [--threads T]";
        /// <summary>Finishes kept per 0.02 s of course time (a reservoir: a way found often doesn't crowd out one found once).</summary>
        const int PerBucket = 40;
        /// <summary>The ways measured at most, fastest first.</summary>
        const int Ways = 300;
        /// <summary>The routes kept as parents besides those no other beats: the least back-to-back, then frame-perfect, then time.</summary>
        const int Elite = 24;

        sealed class Find
        {
            public string Name;
            public List<InputTick> Inputs;
            public float Time;
            public long RunTicks;
            public int FramePerfect, BackToBack, Grace, Major;
            /// <summary>The input ticks of its frame-perfect inputs and forced back-to-back pairs (every input's when none): where edits go.</summary>
            public int[] Hot;
            public double Score;

            public bool Dominates(Find o)
            {
                float a = MathF.Round(Time, 2), b = MathF.Round(o.Time, 2);
                return a <= b && FramePerfect <= o.FramePerfect && BackToBack <= o.BackToBack && (a < b || FramePerfect < o.FramePerfect || BackToBack < o.BackToBack);
            }
        }

        public static int Ease(string[] args)
        {
            if (args.Length < 1 || args[0].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(EaseUsage);
            string tracePath = Path.GetFullPath(args[0]);
            string routePath = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
            JsonElement header = Solve.ReadHeader(tracePath);
            if (header.TryGetProperty("fulltas", out _)) throw new ArgumentException("ease takes a course trace");
            int course = header.GetProperty("course").GetInt32();
            string caps = header.GetProperty("capabilities").GetString();
            double minutes = Solve.Option(args, "--minutes", 10.0), searchMinutes = Solve.Option(args, "--search-minutes", minutes * .4);
            float slack = Solve.Option(args, "--slack", 1.5f), maxTime = Solve.Option(args, "--max-time", float.PositiveInfinity);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            string cls = Solve.Option(args, "--class", "human-nmg");
            if (cls != "human-nmg" && cls != "human-major") throw new ArgumentException(EaseUsage);
            var clock = Stopwatch.StartNew();
            void Log(string line) => Console.WriteLine($"[{clock.Elapsed:hh\\:mm\\:ss}] {line}");

            // Breadth: the solver, unseeded, human inputs only, every finish kept.
            var kept = new Dictionary<int, List<List<InputTick>>>();
            var seen = new Dictionary<int, int>();
            var hashes = new HashSet<ulong>();
            var rng = new Random(1);
            float best = float.PositiveInfinity;
            object gate = new object();
            Func<Simulation> plain = Solve.Creator(tracePath, header);
            DistanceField.Enabled = true;
            var solver = new OfflineSolver(() => { Simulation sim = plain(); sim.Engine.HumanClass = true; return sim; }, caps, new List<List<InputTick>>(), null,
                Solve.ForcedPaths(args, course, caps), rng: Solve.Option(args, "--rng", 0))
            {
                Accept = HumanSpaced,
                LosingSlack = slack,
                FinishedRun = (inputs, time) =>
                {
                    if (time > maxTime) return;
                    ulong hash = SnapshotIndex.Hash(inputs, inputs.Count);
                    lock (gate)
                    {
                        if (!hashes.Add(hash)) return;
                        best = Math.Min(best, time);
                        int b = (int)MathF.Round(time / .02f);
                        if (!kept.TryGetValue(b, out List<List<InputTick>> list)) kept[b] = list = new List<List<InputTick>>();
                        int n = seen[b] = seen.GetValueOrDefault(b) + 1;
                        if (list.Count < PerBucket) list.Add(inputs);
                        else if (rng.Next(n) < PerBucket) list[rng.Next(PerBucket)] = inputs;
                    }
                },
            };
            Log($"Course {course} ({caps}), {cls}: {searchMinutes:0.##} of {minutes:0.##} min searching every way with spaced inputs (digital stick, "
                + $"{Apart}+ ticks apart), finishes up to {slack:0.##} s behind the best" + (float.IsFinite(maxTime) ? $" and under {maxTime:0.00} s" : ""));
            Thread[] workers = solver.Start(threads);
            while (clock.Elapsed.TotalMinutes < searchMinutes)
            {
                Thread.Sleep((int)Math.Min(10000, Math.Max(100, (searchMinutes - clock.Elapsed.TotalMinutes) * 60000)));
                lock (gate) Log($"{Interlocked.Read(ref solver.Runs)} runs, {Interlocked.Read(ref solver.Finishes)} finishes, {hashes.Count} distinct, best {best:0.00}s");
                // ponytail: unseeded, the solver stopped finishing Course 2 WJ after the first run (2026-10-06, cause not found):
                // a quarter of the breadth with no finish gives the rest to the depth, which works from the seeds.
                if (Interlocked.Read(ref solver.Finishes) == 0 && clock.Elapsed.TotalMinutes >= searchMinutes / 4)
                {
                    Log("no finish yet: the rest of the minutes go to the seeds");
                    break;
                }
            }
            solver.Stop();
            foreach (Thread t in workers) t.Join(60000);

            using var pool = new Pool(tracePath, header, null, threads);
            // The seeds (the route given, every --seed, the trace's own), each also with its part tilts snapped (CourseAuto.Digital):
            // the ones that finish as a human class plays them are parents, and the best of them by score the one to beat.
            var seeds = new List<(string name, List<InputTick> inputs)> { (Path.GetFileName(tracePath), Solve.ReadInputs(header)) };
            foreach (string path in (routePath == null ? new string[0] : new[] { routePath }).Concat(Solve.Args(args, "--seed")))
                seeds.Add((Path.GetFileName(path), Plan.ReadRoute(path)));
            seeds.AddRange(seeds.ToList().Select(s => (s.name + " (digital)", CourseAuto.Digital(s.inputs))));
            RunResult[] seedRuns = pool.Play(seeds.Select(s => s.inputs).ToList(), true);
            float fastest = seedRuns.Where(r => r.Finished).Select(r => r.Time).DefaultIfEmpty(float.PositiveInfinity).Min();
            float cap = float.IsFinite(maxTime) ? maxTime : Math.Min(best, fastest) + slack;

            var quick = new Settings { Threads = threads, Raw = true, Radius = 3 };
            var archive = new Dictionary<ulong, Find>();
            Find Judge(List<InputTick> inputs, RunResult played)
            {
                List<InputTick> cut = inputs.Take(played.FinishTick + 1).ToList();
                Result r = Measure(pool, "find", cut, null, quick);
                int[] hot = r.Inputs.Where(t => t.FramePerfect).Select(t => t.Tick)
                    .Concat(r.Pairs.Where(p => p.Forced).SelectMany(p => new[] { r.Inputs[p.First].Tick, r.Inputs[p.Second].Tick })).Distinct().ToArray();
                if (hot.Length == 0) hot = r.Inputs.Select(t => t.Tick).Distinct().ToArray();
                var f = new Find { Inputs = cut, Time = played.Time, RunTicks = played.PhysicsTicks, FramePerfect = r.FramePerfect, BackToBack = r.BackToBack, Hot = hot };
                archive[SnapshotIndex.Hash(cut, cut.Count)] = f;
                return f;
            }

            // Each way's fastest, measured.
            List<List<InputTick>> routes;
            lock (gate) routes = kept.Where(kv => kv.Key * .02f <= cap + 1e-4f).OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).ToList();
            RunResult[] played = pool.Play(routes, true);
            List<int> finished = Enumerable.Range(0, routes.Count).Where(i => played[i].Finished).ToList();
            List<Anchor>[] anchors = pool.AnchorsOf(finished.Select(i => routes[i]).ToList());
            var ways = finished.Select((i, k) => (i, way: Way(anchors[k]))).GroupBy(p => p.way)
                .Select(g => g.OrderBy(p => played[p.i].Time).First().i).OrderBy(i => played[i].Time).Take(Ways).ToList();
            Log($"{routes.Count} finishes kept go {ways.Count} ways; measuring each way's fastest");
            foreach (int i in ways) Judge(routes[i], played[i]);

            var seedFinds = new List<Find>();
            var seedHashes = new HashSet<ulong>();
            for (int i = 0; i < seeds.Count; i++)
            {
                if (!seedRuns[i].Finished || seedRuns[i].Time > cap + 1e-4f) continue;
                Find f = Judge(seeds[i].inputs, seedRuns[i]);
                if (!seedHashes.Add(SnapshotIndex.Hash(f.Inputs, f.Inputs.Count))) continue;
                f.Name = seeds[i].name;
                seedFinds.Add(f);
            }
            Log($"{seedFinds.Count} of {seeds.Count} seeds finish as a {cls} plays them under {cap:0.00} s");

            // Depth: edits near each parent's hard inputs, every child that finishes under the limit measured.
            List<Find> Front() => archive.Values.Where(f => f.Time <= cap + 1e-4f).Where(f => !archive.Values.Any(o => o.Time <= cap + 1e-4f && o.Dominates(f)))
                .GroupBy(f => (MathF.Round(f.Time, 2), f.FramePerfect, f.BackToBack)).Select(g => g.First()).OrderBy(f => f.Time).ToList();
            var tried = new HashSet<ulong>(archive.Keys);
            int rounds = 0;
            TimeSpan lastLog = clock.Elapsed;
            while (clock.Elapsed.TotalMinutes < minutes)
            {
                List<Find> front = Front();
                List<Find> parents = front.Concat(archive.Values.Where(f => f.Time <= cap + 1e-4f).OrderBy(f => f.BackToBack).ThenBy(f => f.FramePerfect)
                    .ThenBy(f => f.Time).Take(Elite)).Distinct().ToList();
                if (parents.Count == 0) break;
                var children = new List<List<InputTick>>();
                for (int k = 0; k < 4 * threads; k++)
                {
                    Find p = parents[rng.Next(parents.Count)];
                    List<InputTick> c = Mutate(p.Inputs, p.Hot, rng);
                    if (c != null && tried.Add(SnapshotIndex.Hash(c, c.Count))) children.Add(c);
                }
                RunResult[] results = pool.Play(children, true);
                for (int k = 0; k < children.Count; k++)
                    if (results[k].Finished && results[k].Time <= cap + 1e-4f) Judge(children[k], results[k]);
                rounds++;
                if ((clock.Elapsed - lastLog).TotalSeconds >= 10)
                {
                    lastLog = clock.Elapsed;
                    Find easiest = parents[0];
                    foreach (Find f in parents)
                        if ((f.BackToBack, f.FramePerfect, f.Time).CompareTo((easiest.BackToBack, easiest.FramePerfect, easiest.Time)) < 0) easiest = f;
                    Log($"{rounds} rounds, {archive.Count} measured, {front.Count} unbeaten; easiest {easiest.Time:0.00}s {easiest.FramePerfect} frame-perfect {easiest.BackToBack} back-to-back");
                }
            }

            // The unbeaten, as the gate scores them: humanize's version measured, spike grace and spans from a replay.
            HumanWeights weights = HumanWeights.Of(default);
            void Gate(Find f)
            {
                var (own, human) = pool.Routes(new List<string> { f.Name }, new List<List<InputTick>> { f.Inputs }, false)[0];
                Result r = Measure(pool, f.Name, own, human, new Settings { Threads = threads });
                GlitchReport g = pool.Report(f.Inputs, weights);
                (f.FramePerfect, f.BackToBack, f.Grace, f.Major) = (r.FramePerfect, r.BackToBack, g.GraceTicks, g.Major);
                f.Score = Math.Round(f.Time + g.Penalty(weights) + weights.DifficultySeconds(r.FramePerfect, r.BackToBack), 2);
            }
            List<Find> list = Front().Where(f => !seedHashes.Contains(SnapshotIndex.Hash(f.Inputs, f.Inputs.Count))).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Name = $"{i + 1:00}";
                Gate(list[i]);
            }
            list = list.Where(f => cls == "human-major" || f.Major == 0).ToList();
            foreach (Find f in seedFinds) Gate(f);
            Find yours = seedFinds.Where(f => cls == "human-major" || f.Major == 0).OrderBy(f => f.Score).FirstOrDefault();

            string dir = Path.Combine(Path.GetDirectoryName(tracePath), "human-finds", $"{course}_{caps}-{cls}");
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir, "*.json")) File.Delete(old);
            Console.WriteLine();
            Console.WriteLine($"{archive.Count} measured; the {list.Count} no other beats on time, frame-perfect inputs and back-to-back pairs at once (as the gate measures them):");
            Console.WriteLine("  #      time  frame-perfect  back-to-back  grace  score  file");
            if (yours != null) Console.WriteLine($"  seed  {yours.Time,5:0.00} {yours.FramePerfect,14} {yours.BackToBack,13} {yours.Grace,6} {yours.Score,6:0.00}  {yours.Name}");
            foreach (Find f in list)
            {
                string file = Path.Combine(dir, $"{f.Name}-{f.Time.ToString("0.00", CultureInfo.InvariantCulture)}s-{f.FramePerfect}fp-{f.BackToBack}b2b.json");
                File.WriteAllText(file, JsonSerializer.Serialize(new
                {
                    course, capabilities = caps, @class = cls, score = f.Score, time = f.Time, runTicks = f.RunTicks,
                    difficulty = new { framePerfect = f.FramePerfect, backToBack = f.BackToBack }, graceTicks = f.Grace, ticks = f.Inputs.Select(t => t.ToJson()),
                }));
                Console.WriteLine($"  {f.Name,-5} {f.Time,5:0.00} {f.FramePerfect,14} {f.BackToBack,13} {f.Grace,6} {f.Score,6:0.00}  {Path.GetFileName(file)}");
            }
            Console.WriteLine($"in {dir}");
            Console.WriteLine($@"to judge a pick: copy it to offline-best\{course}_{caps}-{cls}-<score>.json (the gate saves it under a strictly lower score)");
            // The lowest score, when it beats every seed's, goes to the plugin's gate (offline-best\<course>_<caps>-<class>-<score>.json).
            Find lowest = list.OrderBy(f => f.Score).FirstOrDefault();
            if (lowest != null && (yours == null || lowest.Score < yours.Score))
            {
                string text = lowest.Score.ToString("0.00", CultureInfo.InvariantCulture);
                string file = Path.Combine(Path.GetDirectoryName(tracePath), "offline-best", $"{course}_{caps}-{cls}-{text}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Solve.WriteShared(file, JsonSerializer.Serialize(new { course, capabilities = caps, @class = cls, score = lowest.Score, time = lowest.Time,
                    runTicks = lowest.RunTicks, ticks = lowest.Inputs.Select(t => t.ToJson()) }));
                Log($"offline {cls} best {text} (time {lowest.Time:0.00}s) via ease {lowest.Name} ({lowest.Inputs.Count} ticks) -> {Path.GetFileName(file)}");
            }
            return 0;
        }

        /// <summary>A route's way: the walls and floors it settles on (its wall and land anchors), each by its kind and x to 150 u.</summary>
        static string Way(List<Anchor> anchors)
        {
            return string.Join(" ", anchors.Where(a => a.Kind == "wall" || a.Kind == "land")
                .Select(a => a.Kind[0] + ((int)MathF.Round(a.Move.X / 150f)).ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// One to three edits near the route's hard inputs (<paramref name="hot"/>), each within 4 ticks of one: an input moved 1-4
        /// ticks, a stick change dropped (the stick before it held on) or made another digital value, a jump press dropped, or the
        /// rest of the route held 1-6 ticks later or brought 1-3 ticks sooner (ticks with no press, release or dash cut), as a player
        /// who comes in late or early plays the rest alike. Null when nothing changed.
        /// </summary>
        static List<InputTick> Mutate(List<InputTick> route, int[] hot, Random rng)
        {
            List<InputTick> c = route;
            for (int k = 1 + rng.Next(3); k > 0; k--)
            {
                int h = hot[rng.Next(hot.Length)];
                List<Humanizer.Event> near = Humanizer.Events(c, c).Where(e => !e.Start && Math.Abs(e.Tick - h) <= 4).ToList();
                Humanizer.Event e = near.Count == 0 ? null : near[rng.Next(near.Count)];
                switch (rng.Next(5))
                {
                    case 0 when e != null:
                        c = Humanizer.Shift(c, e, rng.Next(1, 5) * (rng.Next(2) * 2 - 1), out _) ?? c;
                        break;
                    case 1 when e != null && (e.Kind == Humanizer.Kind.StickX || e.Kind == Humanizer.Kind.StickY):
                    {
                        bool y = e.Kind == Humanizer.Kind.StickY;
                        float before = y ? c[e.Tick - 1].y : c[e.Tick - 1].x;
                        float[] others = new[] { -1f, 0f, 1f }.Where(v => v != e.Value).ToArray();
                        c = Humanizer.SetStick(c, y, e.Tick, e.End, rng.Next(2) == 0 ? before : others[rng.Next(others.Length)]);
                        break;
                    }
                    case 2 when e != null && e.Kind == Humanizer.Kind.Press:
                        c = Humanizer.DropPress(c, e.Tick);
                        break;
                    case 3:
                    {
                        int t = Math.Clamp(h + rng.Next(-3, 4), 1, c.Count - 1);
                        InputTick hold = c[t - 1].Coast();
                        c = new List<InputTick>(c);
                        c.InsertRange(t, Enumerable.Repeat(hold, rng.Next(1, 7)));
                        break;
                    }
                    case 4:
                    {
                        int t = Math.Clamp(h + rng.Next(-3, 4), 1, c.Count - 1), n = rng.Next(1, 4);
                        if (t + n > c.Count || Enumerable.Range(t, n).Any(i => c[i].press || c[i].dash || Released(c, i))) break;
                        c = new List<InputTick>(c);
                        c.RemoveRange(t, n);
                        break;
                    }
                }
            }
            return ReferenceEquals(c, route) ? null : c;
        }

        /// <summary>
        /// Inputs a human class plays and a player spaces out: every stick (lock and turn too) at rest or full, and each input (a
        /// stick change, a jump press or release, a dash, its jump on a dash frame) at least <see cref="Apart"/> ticks after the one
        /// before (inputs on one tick are one; a stick held from the start is none).
        /// </summary>
        internal static bool HumanSpaced(List<InputTick> inputs)
        {
            static bool Digital(float? v) => v is not float f || f == 0f || f == 1f || f == -1f;
            int n = inputs.Count;
            Span<bool> act = n <= 4096 ? stackalloc bool[n + 32] : new bool[n + 32];
            for (int i = 0; i < n; i++)
            {
                InputTick k = inputs[i];
                if (!Digital(k.x) || !Digital(k.y) || !Digital(k.lockX) || !Digital(k.lockY) || !Digital(k.turnX) || !Digital(k.turnY)) return false;
                if (k.press || k.dash || Released(inputs, i) || i > 0 && (k.x != inputs[i - 1].x || k.y != inputs[i - 1].y)) act[i] = true;
                if (k.dash && k.dashJump > 0 && i + k.dashJump < act.Length) act[i + k.dashJump] = true;
            }
            int last = -Apart;
            for (int i = 0; i < act.Length; i++)
            {
                if (!act[i]) continue;
                if (i - last < Apart) return false;
                last = i;
            }
            return true;
        }
    }
}
