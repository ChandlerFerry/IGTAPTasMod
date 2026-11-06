using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    static class Techs
    {
        const int Ahead = 75, MaxContacts = 8;
        // From 0.4 up MacroEngine.ControllerAxis snaps x to full tilt (the 45 degree diagonal); 0.4f is just over the steep aim.
        static readonly float[] Steep = { .30f, .35f };
        static readonly (float x, float y)[] Compass = { (0f, 1f), (1f, 1f), (1f, 0f), (1f, -1f), (0f, -1f), (-1f, -1f), (-1f, 0f), (-1f, 1f) };
        static readonly (float x, float y)[] Sideways = { (-1f, 0f), (1f, 0f) };

        sealed class Runner
        {
            readonly Simulation _sim;
            readonly Rigidbody2D _body;
            readonly ContactLog _log = new ContactLog();
            readonly ContactProbe _gate;
            SimSnapshot _at;
            ContactLog.Mark _mark;
            public WorldPos Start;
            public bool Ground, Wall;
            public readonly TechSteps Record;

            public Runner(Func<Simulation> create)
            {
                _sim = create();
                _body = _sim.Player.GetComponent<Rigidbody2D>();
                _gate = new ContactProbe(_sim);
                Record = new TechSteps(_sim);
                Record.Attach();
            }

            public void Prime(List<InputTick> inputs, int n)
            {
                if (_at != null) return;
                int reached = 0;
                RunResult r = _sim.Run(inputs, null, tick =>
                {
                    reached = tick;
                    _log.Tick(tick, _sim.Player, _gate.NearGate());
                    if (tick < n) return true;
                    _at = _sim.Capture(tick);
                    _mark = _log.Save();
                    Start = _sim.PlayerWorld;
                    Ground = _sim.Player.onGround;
                    Wall = _sim.Player.OnWall;
                    return false;
                }, extraTicks: 1000000, onStep: _ => Step());
                if (_at == null)
                    throw new RunEndedException($"the run ends on input tick {reached}, before input tick {n} ("
                        + (r.Finished ? "the finish" : r.EndReason ?? "it stops") + "): hold a tick before that");
            }

            void Step()
            {
                _log.Slide((int)_sim.Engine.RunTicks, _sim.Player);
                _log.Step(_sim);
            }

            public (List<(int tick, Contact kind)> contacts, int died, List<float> path, int marked, float speed) Play(List<InputTick> inputs, int mark = -1)
            {
                _log.Load(_mark);
                Record.Clear();
                var path = new List<float> { Start.x, Start.y };
                int last = _at.Tick, marked = 0;
                RunResult r = _sim.Run(inputs, _at, tick =>
                {
                    last = tick;
                    if (tick == mark) marked = path.Count / 2 - 1;
                    _log.Tick(tick, _sim.Player, _gate.NearGate());
                    return tick < inputs.Count;
                }, 60, _ =>
                {
                    Step();
                    Record.Step();
                    WorldPos p = _sim.PlayerWorld;
                    path.Add(p.x);
                    path.Add(p.y);
                });
                Record.End(r);
                bool died = r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                var contacts = new List<(int, Contact)>(_log.Contacts);
                if (died) contacts.Add((last + 1, Contact.Died));
                return (contacts, died ? last + 1 : -1, path, marked, _sim.Physics.GetVelocity(_body).magnitude);
            }
        }

        sealed class RunEndedException : Exception
        {
            public RunEndedException(string message) : base(message) { }
        }

        sealed class Job
        {
            public TechPattern Pattern;
            public List<InputTick> Inputs;
            public int Placed, Start;
            public string Dir, Key;
            public float? Angle;
        }

        static string Label(float x, float y) => (y > 0f ? "U" : y < 0f ? "D" : "") + (x > 0f ? "R" : x < 0f ? "L" : "");

        static IEnumerable<(string dir, float? angle, List<InputTick> ticks)> Aims(List<InputTick> g, int from, int side, Caps caps)
        {
            int d = -1;
            for (int i = from; i < g.Count - TechGrafts.GraftCoast; i++) if (g[i].dash) { d = i; break; }
            if (d < 0) { yield return (side < 0 ? "L" : side > 0 ? "R" : "", null, g); yield break; }
            bool freeze = g[d].turnX.HasValue || g[d].turnY.HasValue;
            foreach ((float x, float y) in caps.Omni ? Compass : Sideways)
            {
                yield return (Label(x, y), null, Aimed(g, d, x, y, freeze));
                if (freeze && caps.Omni && x != 0f && y != 0f)
                    foreach (float s in Steep)
                        yield return (Label(x, y), MathF.Round(MathF.Atan(s) * 180f / MathF.PI, 1), Aimed(g, d, x * s, y, freeze));
            }
        }

        static List<InputTick> Aimed(List<InputTick> g, int d, float x, float y, bool freeze)
        {
            var t = new List<InputTick>(g);
            InputTick k = t[d];
            if (freeze) { k.x = 0f; k.y = 0f; k.turnX = x; k.turnY = y; }
            else { k.x = x; k.y = y; k.turnX = k.turnY = null; }
            t[d] = k;
            return t;
        }

        static List<InputTick> Continue(List<InputTick> prefix, float x, bool held)
        {
            InputTick prev = prefix[prefix.Count - 1];
            bool down = (prev.held || prev.press) && !prev.release;
            var c = new List<InputTick>(prefix);
            for (int i = 0; i < Ahead; i++)
                c.Add(new InputTick { x = x, held = held, press = i == 0 && held && !down, release = i == 0 && !held && down });
            return c;
        }

        static int Demos(string[] args)
        {
            var only = new HashSet<string>(args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)), StringComparer.Ordinal);
            string dump = Solve.Option(args, "--dump", (string)null);
            if (dump != null) Directory.CreateDirectory(dump);
            List<TechGrafts.CheckDemo> demos = TechGrafts.CheckDemos(Directory.GetCurrentDirectory(), Solve.Option(args, "--tech-dir", (string)null))
                .Where(d => only.Count == 0 || only.Contains(d.Tech)).ToList();
            var json = new JsonSerializerOptions { IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            int missed = 0;
            foreach (TechGrafts.CheckDemo demo in demos)
            {
                string head = $"{demo.Tech,-34} {(demo.Ghost ? "ghost" : "demo "),-5} {demo.Name,-48}";
                bool checkable = TechChecks.Checkable(demo.Tech, demo.Detector);
                if (demo.Error != null) { Console.WriteLine(head + " ERROR " + demo.Error); continue; }
                try
                {
                    string line = demo.Play((sim, inputs) =>
                    {
                        var record = new TechSteps(sim);
                        record.Attach();
                        record.Clear();
                        // Past a lab's finish box too: a box's buy trigger stay comes the step after the touch.
                        sim.TargetFinish = default;
                        RunResult run = sim.Run(inputs, extraTicks: 200, onStep: _ => record.Step());
                        record.End(run);
                        record.Detach();
                        if (dump != null)
                            File.WriteAllText(Path.Combine(dump, demo.Name + (demo.Tech == demo.Name.Split('.')[0] ? "" : "@" + demo.Tech) + ".steps.json"),
                                JsonSerializer.Serialize(new { demo.Tech, demo.Name, demo.Ghost, demo.Label, record.MaxCoyote, record.MaxWallCoyote, record.Spans, record.Steps }, json));
                        int at = checkable ? TechChecks.Find(demo.Tech, demo.Detector, record, 0, record.Steps.Count - 1) : -1;
                        if (checkable && at < 0 && !demo.Ghost) missed++;
                        string seen = string.Join(" ", record.Spans.Select(s => s.Tech + "@" + record.Steps[s.End].Tick).Distinct());
                        return (checkable ? at >= 0 ? "tick " + record.Steps[at].Tick : "-" : "no check").PadRight(9)
                            + (run.Finished ? " finish " : run.EndReason != null && run.EndReason.StartsWith("Player death", StringComparison.Ordinal) ? " died   " : " ended  ")
                            + seen + (demo.Ghost && demo.Label != null ? "  (" + demo.Label + ")" : "");
                    });
                    Console.WriteLine(head + " " + line);
                }
                catch (Exception ex) { Console.WriteLine(head + " ERROR " + ex.GetBaseException().Message); }
            }
            Console.WriteLine($"techs --demos: {demos.Count} runs, {missed} demos whose check missed their tech");
            return missed == 0 ? 0 : 1;
        }

        public static int Run(string[] args)
        {
            if (args.FirstOrDefault() == "--demos") return Demos(args.Skip(1).ToArray());
            if (args.Length < 2 || !args.Contains("--at") || !args.Contains("--caps"))
                throw new ArgumentException("techs <start-trace.json> <route.json> --at N --caps <id> [--out file.jsonl] [--threads T] [--tech-dir D]\n"
                    + "techs --demos [tech ...] [--dump dir] [--tech-dir D]");
            string trace = args[0], routePath = args[1];
            int at = Solve.Option(args, "--at", -1), threads = Solve.Option(args, "--threads", Math.Max(1, Environment.ProcessorCount / 2));
            Caps caps = Caps.Parse(Solve.Option(args, "--caps", ""));
            string outPath = Solve.Option(args, "--out",
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(routePath)), Path.GetFileNameWithoutExtension(routePath) + "-techs.jsonl"));
            List<InputTick> route = Plan.ReadRoute(routePath);
            if (at < 1 || at > route.Count) throw new ArgumentException($"--at must be 1..{route.Count} (the route's input ticks)");
            var clock = Stopwatch.StartNew();
            List<InputTick> prefix = route.Take(at).ToList();
            var continuations = new List<List<InputTick>>();
            if (route.Count > at)
            {
                List<InputTick> own = route.Take(at + Ahead).ToList();
                while (own.Count < at + Ahead) own.Add(own[own.Count - 1].Coast());
                continuations.Add(own);
            }
            foreach (float x in new[] { -1f, 0f, 1f })
                foreach (bool held in new[] { false, true })
                    continuations.Add(Continue(prefix, x, held));

            TechGrafts mined = TechGrafts.Load(Path.GetDirectoryName(Path.GetFullPath(trace)), techDir: Solve.Option(args, "--tech-dir", (string)null));
            Dictionary<string, string> detectors = mined.Demos.GroupBy(d => d.Tech).ToDictionary(g => g.Key, g => g.Select(d => d.Detector).FirstOrDefault(d => d != null));
            ILookup<Contact, TechPattern> keys = mined.Patterns.Where(p => p.Key && !p.Resets && p.Kind != Contact.Died && p.Requires.IsSubsetOf(caps)
                    && TechChecks.Checkable(p.Tech, detectors.GetValueOrDefault(p.Tech)))
                .Select(p => { TechPattern q = p.Copy(); q.Vary = null; return q; }).ToLookup(p => p.Kind);
            Func<Simulation> create = Solve.Creator(trace, Solve.ReadHeader(trace));
            var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(_ => new Worker<Runner>(() => new Runner(create), "techs")).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            void Write(object line)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line) + "\n");
                lock (output) { output.Write(bytes, 0, bytes.Length); output.Flush(); }
            }
            try
            {
                void Parallel<T>(IReadOnlyList<T> items, Action<Runner, T> work)
                {
                    int next = -1;
                    Exception failure = null;
                    using var done = new CountdownEvent(workers.Count);
                    foreach (Worker<Runner> w in workers)
                        w.Post(j =>
                        {
                            try
                            {
                                j.Prime(continuations[0], at);
                                for (int i; failure == null && (i = Interlocked.Increment(ref next)) < items.Count;) work(j, items[i]);
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { done.Signal(); }
                        });
                    done.Wait();
                    if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                }

                var contacts = new List<(int tick, Contact kind)>[continuations.Count];
                Runner first = null;
                Parallel(Enumerable.Range(0, continuations.Count).ToList(), (j, c) =>
                {
                    first ??= j;
                    contacts[c] = j.Play(continuations[c]).contacts.Where(t => t.tick >= at && t.tick < at + Ahead).Take(MaxContacts).ToList();
                });
                var jobs = new List<Job>();
                var rng = new System.Random(1);
                for (int c = 0; c < continuations.Count; c++)
                {
                    List<(int tick, Contact kind)> here = contacts[c];
                    if (first.Ground) here.Insert(0, (at, Contact.Landed));
                    if (first.Wall) here.Insert(0, (at, Contact.TouchedWall));
                    foreach ((int tick, Contact kind) in here.Distinct())
                        foreach (TechPattern p in keys[kind])
                            foreach (int turn in TechGrafts.Sided(p.Kind) || p.Dir == 0 ? new[] { 0 } : new[] { -1, 1 })
                                for (int shift = p.ShiftLo; shift <= p.ShiftHi; shift++)
                                {
                                    if (!TechGrafts.Compose(continuations[c], tick, at, p, rng, out List<InputTick> g, out int placed, (shift, shift), turn)) continue;
                                    int start = at;
                                    while (start < g.Count && start < continuations[c].Count && g[start].Equals(continuations[c][start])) start++;
                                    foreach ((string dir, float? angle, List<InputTick> ticks) in Aims(g, start, turn != 0 ? turn : p.Dir, caps))
                                        jobs.Add(new Job { Pattern = p, Inputs = ticks, Placed = placed, Start = start - at, Dir = dir, Angle = angle,
                                            Key = p.Tech + "|" + dir + "|" + angle });
                                }
                }
                jobs = jobs.OrderBy(j => j.Start).ToList();
                var found = new Dictionary<string, int>();
                int tried = 0, works = 0;
                Parallel(jobs, (j, job) =>
                {
                    lock (found) if (found.TryGetValue(job.Key, out int best) && best <= job.Start) return;
                    Interlocked.Increment(ref tried);
                    var (hits, died, path, lead, speed) = j.Play(job.Inputs, at + job.Start);
                    if (!hits.Any(h => h.kind == job.Pattern.Kind && h.tick >= job.Placed - 1 && h.tick <= job.Placed + 2)) return;
                    int end = job.Inputs.Count - TechGrafts.GraftCoast;
                    if (died >= 0 && died < end) return;
                    if (TechChecks.Find(job.Pattern.Tech, detectors.GetValueOrDefault(job.Pattern.Tech), j.Record,
                        TechChecks.StepAt(j.Record, at + job.Start), TechChecks.StepAt(j.Record, end + 1) - 1) < 0) return;
                    lock (found)
                    {
                        if (found.TryGetValue(job.Key, out int best) && best <= job.Start) return;
                        found[job.Key] = job.Start;
                    }
                    Interlocked.Increment(ref works);
                    Write(new
                    {
                        tech = job.Pattern.Tech, name = job.Pattern.Name, dir = job.Dir, angle = job.Angle, start = job.Start, lead, speed, died = died >= 0,
                        ticks = job.Inputs.Skip(at).Select(t => t.ToJson()), path,
                    });
                });
                Write(new { done = true, tried });
                Console.WriteLine($"techs: at {at}, {continuations.Count} continuations, {jobs.Count} placements, {tried} tried, {works} work, "
                    + $"{found.Keys.Select(k => k.Split('|')[0]).Distinct().Count()} techs ({clock.Elapsed.TotalSeconds:0.0}s, {workers.Count} threads) -> {outPath}");
            }
            catch (RunEndedException ex)
            {
                Console.Error.WriteLine("techs: " + ex.Message);
                return 1;
            }
            finally
            {
                foreach (Worker<Runner> w in workers) w.Stop();
            }
            return 0;
        }
    }
}
