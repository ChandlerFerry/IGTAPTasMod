using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// <c>techs-used &lt;trace.json&gt; &lt;route.json&gt; [--json]</c>: the nmg and major techs a route uses (<see cref="Glitches"/>),
    /// every occurrence, its spike-grace ticks and its human scores (docs/fulltas.md "Classes"). The route replays from the
    /// trace's start as the searches play it: a course trace's to its finish, a Full-TAS trace's under its goals with strict buys
    /// (as <c>fulltas --check</c>), or under the route's own "goals" when it carries them (an adopted goal list); a Full-TAS
    /// route's spans count up to its last goal. The route file's "inputs" (a run file) or "ticks" (an offline best, a submit
    /// body). <c>--json</c>: one line, what the plugin's gate reads.
    /// <c>techs-used --demos [tech ...] [--tech-dir D]</c>: the same on every tech's demos (<c>techs --demos</c>' runs): an nmg or
    /// major tech's demo must show its own tech, a glitchless tech's no nmg or major span.
    /// </summary>
    static class TechsUsed
    {
        public static int Run(string[] args)
        {
            if (args.FirstOrDefault() == "--demos") return Demos(args.Skip(1).ToArray());
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("usage: techs-used <trace.json> <route.json> [--json] | techs-used --demos [tech ...] [--tech-dir D]");
            string tracePath = args[0], routePath = args[1];
            JsonElement header = Solve.ReadHeader(tracePath);
            string[] goals = null;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routePath), new JsonDocumentOptions { MaxDepth = 256 }))
                if (doc.RootElement.TryGetProperty("goals", out JsonElement g) && g.ValueKind == JsonValueKind.Array)
                    goals = g.EnumerateArray().Select(e => e.GetString()).ToArray();
            List<InputTick> inputs = Plan.ReadRoute(routePath);
            bool fullTas = header.TryGetProperty("fulltas", out JsonElement fulltas);
            string[] labels = fullTas ? fulltas.GetProperty("goals").EnumerateArray().Select(e => e.GetProperty("label").GetString()).ToArray() : null;
            if (fullTas && goals != null && !goals.SequenceEqual(labels))
            {
                // A goal list made of the trace's goals (an adopted one) is judged; a run file's stale list (the goals edited since) isn't.
                try { header = FullTas.GoalsHeader(header, goals); labels = goals; }
                catch (KeyNotFoundException) { Console.Error.WriteLine($"techs-used: the route's goals aren't made of the trace's ({goals.FirstOrDefault(l => !labels.Contains(l))}): judged under the trace's"); }
            }
            HumanWeights weights = HumanWeights.Of(fulltas);

            Simulation sim = Solve.Creator(tracePath, header)();
            if (fullTas)
            {
                sim.StrictBuys = true;
                sim.Goals.Limit = labels.Length;
            }
            var record = new TechSteps(sim);
            record.Attach();
            record.Clear();
            // A Full-TAS run ends a few ticks after its last goal (fulltas --check's replay); a course run at its finish.
            RunResult run = sim.Run(inputs, null, null, fullTas ? 1000000 : 400, _ => record.Step());
            record.End(run);
            record.Detach();
            GlitchReport report = Glitches.Classify(record);

            int goalCount = fullTas ? labels.Length : 1, progress = fullTas ? run.Progress : run.Finished ? 1 : 0;
            bool finished = progress == goalCount;
            float live = !fullTas ? run.Time
                : progress == 0 ? 0f : MacroEngine.LiveSeconds(run.GoalPhysicsTicks[progress - 1], run.GoalHeldFrames[progress - 1]);
            // Nothing after a Full-TAS route's last goal is part of it (README ### Rules 32).
            if (fullTas && finished) report = report.Within(-1, run.GoalPhysicsTicks[progress - 1]);
            float humanMajor = live + report.Penalty(weights);
            float? humanNmg = report.Major > 0 ? (float?)null : humanMajor;

            if (args.Contains("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    finished, progress, goals = goalCount, live,
                    spans = report.Spans.Select(s => new { tech = s.Tech, category = s.Category, tick = s.Tick, physics = s.Physics }),
                    major = report.Major, nmg = report.Nmg, graceTicks = report.GraceTicks, undetected = report.Undetected,
                    weights = new { major = weights.Major, nmg = weights.Nmg, grace = weights.Grace },
                    score = new Dictionary<string, float?> { ["human-major"] = humanMajor, ["human-nmg"] = humanNmg },
                }));
                return 0;
            }
            string S(float v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
            Console.WriteLine($"{Path.GetFileName(routePath)}: {(finished ? "finished" : "not finished (" + run.EndReason + ")")}, "
                + (fullTas ? $"{progress} of {goalCount} goals, {S(live)} s live" : $"{S(live)} s course time"));
            Console.WriteLine($"spans: {report.Major} major, {report.Nmg} nmg");
            foreach (GlitchSpan s in report.Spans)
                Console.WriteLine($"  input tick {s.Tick,6}  physics {s.Physics,6}  {s.Tech,-34} {s.Category}");
            Console.WriteLine($"spike grace: {report.GraceTicks} ticks");
            Console.WriteLine("undetected nmg/major techs: " + (report.Undetected.Count == 0 ? "none" : string.Join(", ", report.Undetected)));
            Console.WriteLine($"score (weights major {weights.Major}, nmg {weights.Nmg}, grace {weights.Grace}): human-major {S(humanMajor)} s, human-nmg "
                + (humanNmg == null ? "- (a major span)" : S(humanNmg.Value) + " s"));
            return 0;
        }

        static int Demos(string[] args)
        {
            var only = new HashSet<string>(args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)), StringComparer.Ordinal);
            List<TechGrafts.CheckDemo> demos = TechGrafts.CheckDemos(Directory.GetCurrentDirectory(), Solve.Option(args, "--tech-dir", (string)null))
                .Where(d => !d.Ghost && (only.Count == 0 || only.Contains(d.Tech))).ToList();
            int missed = 0, stray = 0;
            foreach (TechGrafts.CheckDemo demo in demos)
            {
                string head = $"{demo.Tech,-34} {demo.Category,-10} {demo.Name,-48}";
                if (demo.Error != null) { Console.WriteLine(head + " ERROR " + demo.Error); continue; }
                try
                {
                    GlitchReport report = demo.Play((sim, inputs) =>
                    {
                        var record = new TechSteps(sim);
                        record.Attach();
                        record.Clear();
                        // Past a lab's finish box, as techs --demos plays it.
                        sim.TargetFinish = default;
                        RunResult run = sim.Run(inputs, extraTicks: 200, onStep: _ => record.Step());
                        record.End(run);
                        record.Detach();
                        return Glitches.Classify(record);
                    });
                    bool glitch = demo.Category == Glitches.MajorCategory || demo.Category == Glitches.NmgCategory;
                    string verdict;
                    if (glitch)
                    {
                        verdict = report.Spans.Any(s => s.Tech == demo.Tech) ? "ok" : report.Undetected.Contains(demo.Tech) ? "undetected" : "MISSED";
                        if (verdict == "MISSED") missed++;
                    }
                    else
                    {
                        verdict = report.Spans.Count == 0 ? "ok" : "FALSE";
                        if (verdict == "FALSE") stray++;
                    }
                    Console.WriteLine($"{head} {verdict,-10} " + string.Join(" ", report.Spans.Select(s => s.Tech + "@" + s.Tick)));
                }
                catch (Exception ex) { Console.WriteLine(head + " ERROR " + ex.GetBaseException().Message); }
            }
            Console.WriteLine($"techs-used --demos: {demos.Count} demos, {missed} nmg/major demos without their own tech, {stray} glitchless demos with an nmg/major span");
            return missed + stray == 0 ? 0 : 1;
        }
    }
}
