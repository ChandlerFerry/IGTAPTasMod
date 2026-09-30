using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// "What can I do here?" (the TAS editor's Techs here, docs/superpowers/specs/2026-09-30-techs-here-design.md): the
    /// techs whose key window works from a route's input tick N. The contacts along a few continuations from N (the route's
    /// own, the stick left, at rest or right, jump held or not) place each tech's key window (<see cref="TechGrafts.Compose"/>),
    /// in each direction it can take; one works when its key contact happens where its demo had it. Suggestions, not judged.
    /// </summary>
    static class Techs
    {
        /// <summary>Input ticks each continuation plays past N, and the contacts along it that place windows.</summary>
        const int Ahead = 75, MaxContacts = 8;
        /// <summary>A steep freeze aim's x with y at full tilt (16.7°, 19.3°, 21.8° from vertical).</summary>
        static readonly float[] Steep = { .30f, .35f, .40f };
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

            public Runner(Func<Simulation> create)
            {
                _sim = create();
                _body = _sim.Player.GetComponent<Rigidbody2D>();
                _gate = new ContactProbe(_sim);
            }

            /// <summary>The state and the contact detection after the first <paramref name="n"/> of <paramref name="inputs"/>.</summary>
            public void Prime(List<InputTick> inputs, int n)
            {
                if (_at != null) return;
                RunResult r = _sim.Run(inputs, null, tick =>
                {
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
                    throw new InvalidOperationException($"the route doesn't reach input tick {n} offline (" + (r.Finished ? "it finishes before" : r.EndReason ?? "it stops") + ")");
            }

            void Step()
            {
                _log.Slide((int)_sim.Engine.RunTicks, _sim.Player);
                _log.Step(_sim);
            }

            /// <summary><paramref name="inputs"/> played from input tick N to their end (not the idle steps the engine runs after
            /// them): the contacts from there, the input tick it died on (-1: none), the player's world position after every physics
            /// step (the first: at N) and the speed at the end.</summary>
            public (List<(int tick, Contact kind)> contacts, int died, List<float> path, float speed) Play(List<InputTick> inputs)
            {
                _log.Load(_mark);
                var path = new List<float> { Start.x, Start.y };
                int last = _at.Tick;
                RunResult r = _sim.Run(inputs, _at, tick => { last = tick; _log.Tick(tick, _sim.Player, _gate.NearGate()); return tick < inputs.Count; }, 60, _ =>
                {
                    Step();
                    WorldPos p = _sim.PlayerWorld;
                    path.Add(p.x);
                    path.Add(p.y);
                });
                bool died = r.EndReason != null && r.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                var contacts = new List<(int, Contact)>(_log.Contacts);
                if (died) contacts.Add((last + 1, Contact.Died));
                return (contacts, died ? last + 1 : -1, path, _sim.Physics.GetVelocity(_body).magnitude);
            }
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

        /// <summary>The window's directions: its first dash's stick (with the omnidash all 8, else left and right; a dash that
        /// aims in the freeze turns the freeze stick instead, the diagonals also steep), else the side it was turned to.</summary>
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

        /// <summary>The route up to <paramref name="at"/>, then <see cref="Ahead"/> ticks of the stick at <paramref name="x"/> with
        /// jump held or not (a press or release on the first where the button changes).</summary>
        static List<InputTick> Continue(List<InputTick> prefix, float x, bool held)
        {
            InputTick prev = prefix[prefix.Count - 1];
            bool down = (prev.held || prev.press) && !prev.release;
            var c = new List<InputTick>(prefix);
            for (int i = 0; i < Ahead; i++)
                c.Add(new InputTick { x = x, held = held, press = i == 0 && held && !down, release = i == 0 && !held && down });
            return c;
        }

        public static int Run(string[] args)
        {
            if (args.Length < 2 || !args.Contains("--at") || !args.Contains("--caps"))
                throw new ArgumentException("techs <start-trace.json> <route.json> --at N --caps <id> [--out file.jsonl] [--threads T] [--tech-dir D]");
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
            ILookup<Contact, TechPattern> keys = mined.Patterns.Where(p => p.Key && !p.Resets && p.Kind != Contact.Died && p.Requires.IsSubsetOf(caps))
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
                // Each worker replays the route to N once (a snapshot holds its own world); the continuations' contacts place the jobs.
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
                    if (failure != null) throw new InvalidOperationException("techs replay failed", failure);
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
                    // Where the player already is counts: standing on the ground, clinging to a wall.
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
                // Soonest first: a direction found once is skipped where it would start later.
                jobs = jobs.OrderBy(j => j.Start).ToList();
                var found = new Dictionary<string, int>();
                int tried = 0, works = 0;
                Parallel(jobs, (j, job) =>
                {
                    lock (found) if (found.TryGetValue(job.Key, out int best) && best <= job.Start) return;
                    Interlocked.Increment(ref tried);
                    var (hits, died, path, speed) = j.Play(job.Inputs);
                    if (!hits.Any(h => h.kind == job.Pattern.Kind && h.tick >= job.Placed - 1 && h.tick <= job.Placed + 2)) return;
                    // A death before the window's own inputs end is the tech failing (one in its coast is shown, marked).
                    if (died >= 0 && died < job.Inputs.Count - TechGrafts.GraftCoast) return;
                    lock (found)
                    {
                        if (found.TryGetValue(job.Key, out int best) && best <= job.Start) return;
                        found[job.Key] = job.Start;
                    }
                    Interlocked.Increment(ref works);
                    Write(new
                    {
                        tech = job.Pattern.Tech, name = job.Pattern.Name, dir = job.Dir, angle = job.Angle, start = job.Start, speed, died = died >= 0,
                        ticks = job.Inputs.Skip(at).Select(t => t.ToJson()), path,
                    });
                });
                Write(new { done = true, tried });
                Console.WriteLine($"techs: at {at}, {continuations.Count} continuations, {jobs.Count} placements, {tried} tried, {works} work, "
                    + $"{found.Keys.Select(k => k.Split('|')[0]).Distinct().Count()} techs ({clock.Elapsed.TotalSeconds:0.0}s, {workers.Count} threads) -> {outPath}");
            }
            finally
            {
                foreach (Worker<Runner> w in workers) w.Stop();
            }
            return 0;
        }
    }
}
