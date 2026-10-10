using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    static class RunSelfTest
    {
        static int _passed;

        static void Check(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + what);
            _passed++;
        }

        public static int Run(string[] args)
        {
            try
            {
                EconomyGoals();
                PlanEdits();
                MadeGoals();
                Library();
                string trace = Solve.Option<string>(args, "--trace", null), best = Solve.Option<string>(args, "--best", null);
                if (trace != null && best != null && !args.Contains("--no-search")) Search(trace, best);
                if (trace != null && best != null && args.Contains("--build")) Rebuild(trace, best, Solve.Option(args, "--live", double.MaxValue));
                if (trace != null && best != null && args.Contains("--improve-smoke")) ImproveSmoke(trace, best);
                Console.WriteLine($"run-selftest: {_passed} checks passed");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(ex.Message);
                if (ex.InnerException != null) Console.WriteLine(ex.InnerException);
                return 1;
            }
        }

        static void EconomyGoals()
        {
            foreach (string token in new[] { "buyatom", "buyclonecd8", "buyclonegp8", "buycd8", "buycddigits8", "buyatomlevel9", "buyatomlevel10",
                "buywatts10", "buynp10", "buyfree6b", "buyfree10a", "buystatue", "buytree0", "buyclone6" })
                Check(FullTasGoal.IsEconomy("buy:" + token), "buy:" + token + " is an economy goal");
            foreach (string label in new[] { "buy:buytree5", "buy:buytree16", "buy:buywj", "buy:buyteleporters", "at:16639,11761,240,400", "reward:bonus3" })
                Check(!FullTasGoal.IsEconomy(label), label + " stays in the run's skeleton");
            Check(FullTasGoal.IsEconomy("farm:6") && FullTasGoal.IsEconomy("record:7"), "laps are economy goals");
        }

        static void PlanEdits()
        {
            var p = new RunPlan(new[] { "a00001", "b00002", "c00003" }, new[] { "record:1", "buy:buyreward", "buy:buywj" });
            RunPlan q = p.Insert(1, "farm:1");
            Check(q.Count == 4 && q.Labels[1] == "farm:1" && q.Ids[0] == "a00001" && q.Ids[2] == "b00002" && q.Ids[3] == "c00003", "insert keeps the others' ids");
            Check(FullTasGoalIds.Invalid(q.Ids, 4) == null && !p.Ids.Contains(q.Ids[1]), "an inserted goal gets a new valid id");
            Check(q.RemoveAt(1).Same(p), "remove undoes insert");
            RunPlan m = p.Move(2, 0);
            Check(m.Labels.SequenceEqual(new[] { "buy:buywj", "record:1", "buy:buyreward" }) && m.Ids.SequenceEqual(new[] { "c00003", "a00001", "b00002" }), "move carries its id");
            Check(p.Count == 3 && p.Labels[1] == "buy:buyreward", "edits leave the plan they came from as it was");
        }

        static void MadeGoals()
        {
            var known = new Dictionary<string, FullTasGoal>();
            FullTasGoal farm = RunGoals.Goal("farm:6", known);
            Check(farm.kind == FullTasGoal.Farm && farm.course == 6 && farm.boxes == null && farm.label == "farm:6", "farm:6 is made: a farm goal of course 6");
            var clone6 = FullTasGoal.Boxes.First(b => b.token == "buyclone6");
            FullTasGoal buy = RunGoals.Goal("buy:buyclone6", known);
            Check(buy.kind == FullTasGoal.Buy && buy.boxes.Length == 1 && buy.boxes[0] == FullTasGoal.BoxId(clone6.path, clone6.sibling), "buy:buyclone6 names its box");
            Check(RunGoals.Goal("buy:buyreward6+buyclone6", known).boxes.Length == 2, "a set buy names each box");
            bool threw = false;
            try { RunGoals.Goal("reward:bonus3", known); } catch (ArgumentException) { threw = true; }
            Check(threw, "a skeleton goal the trace doesn't have isn't made up");
            var at = new FullTasGoal { label = "at:1,2,30,40", kind = FullTasGoal.At, x = 1, y = 2, w = 30, h = 40, from = FullTasGoal.FromLeft };
            known[at.label] = at;
            Check(ReferenceEquals(RunGoals.Goal(at.label, known), at), "a goal the trace has is the trace's");
            JsonObject e = RunGoals.HeaderEntry(buy, "x12345");
            Check((string)e["id"] == "x12345" && (string)e["label"] == "buy:buyclone6" && (string)e["kind"] == "buy" && e["boxes"].AsArray().Count == 1, "a buy's header entry");
            JsonObject f = RunGoals.HeaderEntry(farm, "y12345");
            Check((int)f["course"] == 6 && f["boxes"] == null && f["rect"] == null, "a lap's header entry");
            JsonObject a = RunGoals.HeaderEntry(at, "z12345");
            Check(a["rect"].AsArray().Select(v => (float)v).SequenceEqual(new[] { 1f, 2f, 30f, 40f }) && (int)a["from"] == FullTasGoal.FromLeft, "an area goal's header entry");
            Check(!RunGoals.Editable("buy:buytree5") && RunGoals.Editable("buy:buyatom") && RunGoals.Editable("farm:6"), "only economy goals are editable");
        }

        static Segment Seg(string to, string unlocks, float x, float y, int phase, params InputTick[] inputs) =>
            new Segment { To = to, Unlocks = unlocks, X = x, Y = y, Phase = phase, Facing = 1, Inputs = inputs.ToList(), Ticks = inputs.Length, Source = "test" };

        static void Library()
        {
            string dir = Path.Combine(Path.GetTempPath(), "igtap-run-selftest-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "lib.segments.json");
            var run = new InputTick { x = 1f };
            var jump = new InputTick { x = 1f, held = true, press = true };
            var dash = new InputTick { x = 0f, dash = true, turnX = 0.35f, turnY = 1f };
            var reset = new InputTick { reset = true };
            SegmentLibrary lib = SegmentLibrary.Load(file, Array.Empty<string>());
            Segment s1 = Seg("farm:6", "WJ", 100, 100, 0, reset, run, jump, dash);
            Check(lib.Add(s1) && !lib.Add(Seg("farm:6", "WJ", 140, 90, 2, reset, run, jump, dash)) && lib.Count == 1, "the same inputs to the same goal with the same unlocks are one segment");
            Check(lib.Candidates("farm:6", "WJ", 120, 100, 0, 1).Count == 1, "a segment starting 20 u away is a candidate");
            Check(lib.Candidates("farm:6", "WJ", 200, 100, 0, 1).Count == 0, "one 100 u away isn't");
            Check(lib.Candidates("farm:6", "WJ_DS", 100, 100, 0, 1).Count == 0 && lib.Candidates("farm:7", "WJ", 100, 100, 0, 1).Count == 0, "another goal or other unlocks aren't");
            Segment s2 = Seg("farm:6", "WJ", 110, 100, 3, run, run), s3 = Seg("farm:6", "WJ", 105, 100, 0, run, jump);
            lib.Add(s2);
            lib.Add(s3);
            Check(lib.Candidates("farm:6", "WJ", 100, 100, 3, 1).Select(c => c.X).SequenceEqual(new[] { 100f, 105f, 110f }), "candidates nearest first");
            Check(lib.Candidates("farm:6", "WJ", 100, 100, 0, 1, 2).Count == 2, "at most max candidates");
            Check(lib.Candidates("farm:6", "WJ", 5000, 100, 0, 1, radius: float.MaxValue).Count == 3, "any distance for seeds");
            lib.Save();
            SegmentLibrary back = SegmentLibrary.Load(file, Array.Empty<string>());
            Segment b1 = back.Candidates("farm:6", "WJ", 100, 100, 0, 1)[0];
            Check(back.Count == 3 && b1.Inputs.SequenceEqual(s1.Inputs) && b1.Ticks == 4 && b1.Source == "test", "save and load keep each segment's inputs");
            var facts = new SegmentFacts(Path.Combine(dir, "lib.failures.json"));
            facts.Record("farm:6|WJ|100,100|0", 1000, "no finds");
            Check(facts.Failed("farm:6|WJ|100,100|0", 1000) && !facts.Failed("farm:6|WJ|100,100|0", 4000) && !facts.Failed("other", 1000), "a failure holds for its budget, not a bigger one");
            Check(new SegmentFacts(Path.Combine(dir, "lib.failures.json")).Failed("farm:6|WJ|100,100|0", 1000), "failures are kept on disk");
            Directory.Delete(dir, true);
        }

        static List<InputTick> Inputs(string file)
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            return Solve.ReadInputs(d.RootElement);
        }

        static void Search(string trace, string best)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            var (k, state, prefix) = Solve.OnThread(() =>
            {
                Simulation sim = ctx.Make();
                RunResult r = sim.Run(inputs, null, null, 1000000);
                int pick = Enumerable.Range(50, Math.Max(0, r.Progress - 51))
                    .Where(g => !FullTasGoal.IsLap(ctx.HeaderGoals[g].label) && ctx.HeaderGoals[g].kind == FullTasGoal.Buy)
                    .OrderBy(g => r.GoalPhysicsTicks[g] - r.GoalPhysicsTicks[g - 1]).First();
                SimSnapshot at = sim.AdvanceToGoal(inputs, pick, out int consumed);
                sim.Restore(at);
                return (pick, new SnapshotStore(sim).Save(at), inputs.Take(consumed).ToList());
            });
            var search = new SegmentSearch(ctx);
            List<List<InputTick>> a = search.Find(state, prefix, ctx.HeaderGoals, k, new List<List<InputTick>>(), 2_000_000, out string nearA);
            List<List<InputTick>> b = search.Find(state, prefix, ctx.HeaderGoals, k, new List<List<InputTick>>(), 2_000_000, out _);
            Check(a.Count > 0, $"a search finds goal {k + 1} {ctx.HeaderGoals[k].label} of {ctx.Name} from its state ({nearA})");
            Check(a.Count == b.Count && a[0].SequenceEqual(b[0]), "the same search finds the same routes");
        }

        static void Rebuild(string trace, string best, double live)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            string dir = Path.Combine(Path.GetTempPath(), "igtap-run-build-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            (Built harvested, Built built, Built greedy) = Solve.OnThread(() =>
            {
                SegmentLibrary lib = SegmentLibrary.Load(Path.Combine(dir, "lib.segments.json"), Array.Empty<string>());
                var builder = new Builder(ctx, lib, new SegmentSearch(ctx), new SegmentFacts(Path.Combine(dir, "lib.failures.json")), searchMisses: false);
                Built h = builder.Harvest(plan, inputs, "harvest");
                return (h, builder.Build(plan, h), builder.Build(plan));
            });
            Check(harvested.Complete && harvested.Live <= live + 1e-4, $"{ctx.Name}: the best harvested: {harvested.Reached} of {plan.Count} goals, {harvested.Live:0.0000} s (no slower than {live})");
            Check(built.Complete && built.Searches == 0 && built.Live == harvested.Live, $"{ctx.Name}: the plan built on the harvest: {built.Reached} goals, {built.Live:0.0000} s, {built.Searches} searches");
            Console.WriteLine($"  {ctx.Name}: a greedy rebuild from the library alone reaches {greedy.Reached} of {plan.Count} goals" + (greedy.Failure == null ? $", {greedy.Live:0.0000} s" : $" ({greedy.Failure.Text})"));
            Directory.Delete(dir, true);
        }

        static void ImproveSmoke(string trace, string best)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            string dir = Path.Combine(Path.GetTempPath(), "igtap-run-improve-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var full = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            SegmentLibrary lib = SegmentLibrary.Load(Path.Combine(dir, "lib.segments.json"), Array.Empty<string>());
            var facts = new SegmentFacts(Path.Combine(dir, "lib.failures.json"));
            Built harvested = Solve.OnThread(() => new Builder(ctx, lib, new SegmentSearch(ctx), facts, false).Harvest(full, inputs, "harvest"));
            // A lap right before an economy buy, early in the run: without it the buy waits for its Cash.
            int lap = Enumerable.Range(10, full.Count - 20).First(i => FullTasGoal.IsLap(full.Labels[i]) && full.Labels[i + 1].StartsWith("buy:", StringComparison.Ordinal) && RunGoals.Editable(full.Labels[i + 1]));
            int end = lap + 6;
            var shortPlan = new RunPlan(full.Ids.Take(end).ToArray(), full.Labels.Take(end).ToArray());
            double original = Math.Round(MacroEngine.LiveSeconds(harvested.Physics[end - 1], harvested.Held[end - 1]), 4);
            RunPlan without = shortPlan.RemoveAt(lap);
            var improver = new Improver(ctx, lib, new SegmentSearch(ctx), facts, threads: 4) { MaxSearchesPerBuild = 0, Log = Console.WriteLine };
            improver.Run(without, inputs.Take(harvested.StepEnd[lap - 1]).ToList(), () => false);
            Built got = improver.Best;
            Check(got.Complete, $"{ctx.Name}: the improver builds goals 1-{end} without lap {lap + 1} ({full.Labels[lap]}): {got.Reached} of {without.Count} ({got.Failure?.Text})");
            Check(got.Live <= original + 1e-4, $"{ctx.Name}: improved to {got.Live:0.0000} s against the original {original:0.0000} s ({string.Join(", ", improver.Accepted)})");
            Directory.Delete(dir, true);
        }
    }
}
