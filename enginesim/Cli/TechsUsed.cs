using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    static class TechsUsed
    {
        public static int Run(string[] args)
        {
            if (args.FirstOrDefault() == "--demos") return Demos(args.Skip(1).ToArray());
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("usage: techs-used <trace.json> <route.json> [--json [--out F]] [--no-difficulty] [--threads T] [--tech-weight <tech>=<s>] | techs-used --demos [tech ...] [--tech-dir D]");
            string tracePath = args[0], routePath = args[1];
            var (header, labels, inputs) = Load(tracePath, routePath, "techs-used");
            bool fullTas = labels != null;
            HumanWeights weights = HumanWeights.Of(fullTas ? header.GetProperty("fulltas") : default).WithTechWeights(args);

            Simulation sim = Create(tracePath, header, labels);
            var record = new TechSteps(sim);
            record.Attach();
            record.Clear();
            RunResult run = sim.Run(inputs, null, null, fullTas ? 1000000 : 400, _ => record.Step());
            record.End(run);
            record.Detach();
            GlitchReport report = Glitches.Classify(record, weights);

            int goalCount = fullTas ? labels.Length : 1, progress = fullTas ? run.Progress : run.Finished ? 1 : 0;
            bool finished = progress == goalCount;
            float live = !fullTas ? run.Time
                : progress == 0 ? 0f : MacroEngine.LiveSeconds(run.GoalPhysicsTicks[progress - 1], run.GoalHeldFrames[progress - 1]);
            // Nothing after a Full-TAS route's last goal is part of it (RULES.md 32).
            if (fullTas && finished) report = report.Within(-1, run.GoalPhysicsTicks[progress - 1]);
            Difficulty.Tally? tally = null;
            if (finished && !args.Contains("--no-difficulty"))
                using (var meter = new Difficulty.Meter(tracePath, header, labels, Solve.Option(args, "--threads", Environment.ProcessorCount)))
                    tally = meter.Measure(Path.GetFileName(routePath), inputs);
            float difficultySeconds = tally is Difficulty.Tally t ? weights.DifficultySeconds(t.FramePerfect, t.BackToBack) : 0f;
            string humanBlock = null;
            if (!fullTas && finished)
            {
                sim.Engine.HumanClass = true;
                RunResult human = sim.Run(inputs, null, null, 400);
                sim.Engine.HumanClass = false;
                if (!human.Finished) humanBlock = human.EndReason ?? "the human replay doesn't finish";
            }
            float? humanMajor = humanBlock != null ? (float?)null : live + report.Penalty(weights) + difficultySeconds;
            float? humanNmg = report.Major > 0 ? (float?)null : humanMajor;

            if (args.Contains("--json"))
            {
                string json = JsonSerializer.Serialize(new
                {
                    finished, progress, goals = goalCount, live, humanBlock,
                    spans = report.Spans.Select(s => new { tech = s.Tech, category = s.Category, tick = s.Tick, physics = s.Physics }),
                    major = report.Major, nmg = report.Nmg, graceTicks = report.GraceTicks, undetected = report.Undetected,
                    difficulty = tally?.Json,
                    weights = new { grace = weights.Grace, framePerfect = weights.FramePerfect, backToBack = weights.BackToBack, techs = weights.Techs },
                    score = new Dictionary<string, float?> { ["human-major"] = humanMajor, ["human-nmg"] = humanNmg },
                });
                string outPath = Solve.Option(args, "--out", (string)null);
                if (outPath != null) File.WriteAllText(outPath, json);
                else Console.WriteLine(json);
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
            Console.WriteLine("difficulty: " + (tally is Difficulty.Tally d ? $"{d}, {S(difficultySeconds)} s"
                : finished ? "not measured (--no-difficulty)" : "none (not finished)"));
            if (!fullTas && finished) Console.WriteLine("human inputs: " + (humanBlock ?? "ok (digital stick, two jump buttons)"));
            Console.WriteLine($"score (weights grace {weights.Grace}, frame-perfect {weights.FramePerfect}, back-to-back {weights.BackToBack}"
                + (weights.Techs.Count > 0 ? ", " + weights.TechsText : "") + "): human-major "
                + (humanMajor == null ? "- (not human inputs)" : S(humanMajor.Value) + " s") + ", human-nmg "
                + (humanNmg == null ? humanMajor == null ? "-" : "- (a major span)" : S(humanNmg.Value) + " s"));
            return 0;
        }

        internal static (JsonElement header, string[] labels, List<InputTick> inputs) Load(string tracePath, string routePath, string tool)
        {
            JsonElement header = Solve.ReadHeader(tracePath);
            string[] goals = null;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routePath), new JsonDocumentOptions { MaxDepth = 256 }))
                if (doc.RootElement.TryGetProperty("goals", out JsonElement g) && g.ValueKind == JsonValueKind.Array)
                    goals = g.EnumerateArray().Select(FullTas.LabelOf).ToArray();
            List<InputTick> inputs = Plan.ReadRoute(routePath);
            if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) return (header, null, inputs);
            string[] labels = fulltas.GetProperty("goals").EnumerateArray().Select(e => e.GetProperty("label").GetString()).ToArray();
            if (goals != null && !goals.SequenceEqual(labels))
            {
                try { header = FullTas.GoalsHeader(header, goals); labels = goals; }
                catch (KeyNotFoundException) { Console.Error.WriteLine($"{tool}: the route's goals aren't made of the trace's ({goals.FirstOrDefault(l => !labels.Contains(l))}): judged under the trace's"); }
            }
            return (header, labels, inputs);
        }

        internal static Simulation Create(string tracePath, JsonElement header, string[] labels)
        {
            Simulation sim = Solve.Creator(tracePath, header)();
            if (labels != null)
            {
                sim.StrictBuys = true;
                sim.Goals.Limit = labels.Length;
            }
            return sim;
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
