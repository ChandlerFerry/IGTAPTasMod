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
                Splits();
                StuckRanking();
                ScoreRanking();
                OneXAlign();
                if (Solve.Option<string>(args, "--ticks-self", null) is string ticksOut) OneXSelf(ticksOut);
                ResumeKeepsEarlyPress();
                AvoidsByPlanIds();
                PolishBookkeeping();
                int reach = Array.IndexOf(args, "--course-reach");
                if (reach >= 0 && reach + 2 < args.Length)
                {
                    string why = CourseAuto.ReachSelfTest(args[reach + 1], Inputs(args[reach + 2]), Solve.Option(args, "--minutes", 10.0));
                    Check(why == null, $"a course reached from half its best's inputs finishes on its forced path ({why})");
                }
                string trace = Solve.Option<string>(args, "--trace", null), best = Solve.Option<string>(args, "--best", null);
                if (trace != null && best != null && args.Contains("--confirm-live")) ConfirmLive(trace, best);
                if (trace != null && best != null && args.Contains("--harvest-vs-straight")) HarvestVsStraight(trace, best, Solve.Option(args, "--rebuild-from", 300));
                if (trace != null && best != null && !args.Contains("--no-search")) Search(trace, best);
                if (trace != null && best != null && args.Contains("--build")) Rebuild(trace, best, Solve.Option(args, "--live", double.MaxValue));
                if (trace != null && best != null && args.Contains("--improve-smoke")) ImproveSmoke(trace, best);
                if (trace != null && best != null && Solve.Option(args, "--own-at", -1) is int ownAt && ownAt >= 0) OwnAt(trace, best, ownAt);
                if (trace != null && best != null && args.Contains("--rejoin")) RejoinBroken(trace, best);
                if (trace != null && best != null && Solve.Option(args, "--restore-at", -1) is int restoreAt && restoreAt >= 0) RestoreAt(trace, best, restoreAt);
                if (trace != null && best != null && Solve.Option(args, "--chain-to", -1) is int chainTo && chainTo >= 0) ChainTo(trace, best, chainTo);
                int producers = Array.IndexOf(args, "--producers");
                if (trace != null && best != null && producers >= 0 && producers + 2 < args.Length) Producers(trace, best, int.Parse(args[producers + 1]), args[producers + 2]);
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
            Check(lib.Add(s1) && !lib.Add(Seg("farm:6", "WJ", 100, 100, 0, reset, run, jump, dash)) && lib.Count == 1, "the same inputs from the same start to the same goal with the same unlocks are one segment");
            SegmentLibrary moved = SegmentLibrary.Load(Path.Combine(dir, "moved.segments.json"), Array.Empty<string>());
            Check(moved.Add(Seg("farm:6", "WJ", 100, 100, 0, reset, run, jump, dash)) && moved.Add(Seg("farm:6", "WJ", 140, 90, 2, reset, run, jump, dash))
                && moved.Candidates("farm:6", "WJ", 140, 90, 2, 1)[0].X == 140, "the same inputs from another start are another segment, found from there");
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
            List<List<InputTick>> a = search.Find(state, prefix, ctx.HeaderGoals, ctx.HeaderIds, k, new List<List<InputTick>>(), 2_000_000, out string nearA);
            List<List<InputTick>> b = search.Find(state, prefix, ctx.HeaderGoals, ctx.HeaderIds, k, new List<List<InputTick>>(), 2_000_000, out _);
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
            Check(harvested.Complete && harvested.Live <= live + 1e-4, $"{ctx.Name}: the best harvested: {harvested.Reached} of {plan.Count} goals, {harvested.Live:0.0000} s (no slower than {live}); {harvested.HarvestEnd}");
            Check(built.Complete && built.Searches == 0 && built.Live == harvested.Live, $"{ctx.Name}: the plan built on the harvest: {built.Reached} goals, {built.Live:0.0000} s, {built.Searches} searches");
            Console.WriteLine($"  {ctx.Name}: a greedy rebuild from the library alone reaches {greedy.Reached} of {plan.Count} goals" + (greedy.Failure == null ? $", {greedy.Live:0.0000} s" : $" ({greedy.Failure.Text})"));
            Directory.Delete(dir, true);
        }

        // A goal state captured in a dash lock whose wall touch pressed the next step early (VMAN goal 13).
        static void ResumeKeepsEarlyPress()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var engine = new MacroEngine { Quiet = true };
            int Field(string name) => (int)typeof(MacroEngine).GetField(name, flags).GetValue(engine);
            foreach (string name in new[] { "_stepIndex", "_pressedStep", "_quickRestartStep" }) typeof(MacroEngine).GetField(name, flags).SetValue(engine, 7);
            object[] state = engine.CaptureState();
            engine.ResumeOwned(state, new List<MacroStep>(), 0);
            Check(Field("_pressedStep") == 0 && Field("_quickRestartStep") == 0, "a resume at a step already pressed early keeps it pressed (no second press or restart)");
            engine.ResumeOwned(state, new List<MacroStep>(), 3);
            Check(Field("_pressedStep") == -1 && Field("_quickRestartStep") == -1, "a resume inside a step presses nothing it didn't");
        }

        static void Splits()
        {
            var b = new Built { Plan = new RunPlan(new[] { "a00001", "b00002" }, new[] { "record:1", "buy:buyreward" }) };
            b.StepEnd.AddRange(new[] { 10, 20 });
            b.Physics.AddRange(new long[] { 100, 200 });
            b.Held.AddRange(new long[] { 0, 6 });
            b.InputAt.AddRange(new long[] { 10, 20 });
            var events = new List<string>();
            JsonElement Verdict(string json) => JsonDocument.Parse(json).RootElement;
            Check(Confirm.Compare(b, Verdict("{\"splits\":[{\"ticks\":100,\"held\":0},{\"ticks\":200,\"held\":6}]}"), e => events.Add(JsonSerializer.Serialize(e)), _ => { }),
                "the game's splits equal the engine's: confirmed");
            Check(!Confirm.Compare(b, Verdict("{\"splits\":[{\"ticks\":100,\"held\":0},{\"ticks\":201,\"held\":6}]}"), e => events.Add(JsonSerializer.Serialize(e)), _ => { })
                && events[^1].Contains("\"goal\":2"), "a split one tick off: refused, naming goal 2");
            Check(!Confirm.Compare(b, Verdict("{\"splits\":[{\"ticks\":100,\"held\":0}]}"), e => events.Add(JsonSerializer.Serialize(e)), _ => { }),
                "the game short of the engine's goals: refused");
        }

        // ZipEscape%'s goal 831 plateau: every move failed at the same goal and tick, so none was kept.
        static void StuckRanking()
        {
            var full = new RunPlan(new[] { "t00001", "a00001", "a00002", "s00007" }, new[] { "buy:buytree1", "buy:buyatom", "buy:buyatom", "buy:buytree7" });
            Built Stuck(RunPlan plan, int at, string kind, double progress)
            {
                var b = new Built { Plan = plan, Failure = new Failure { Kind = kind, Goal = at, Label = plan.Labels[at], Progress = progress } };
                for (int g = 0; g < at; g++) { b.StepEnd.Add(10 * (g + 1)); b.Physics.Add(100 * (g + 1)); b.Held.Add(0); b.InputAt.Add(10 * (g + 1)); }
                return b;
            }
            Built best = Stuck(full, 2, "no-income", 0.25), dropped = Stuck(full.RemoveAt(2), 2, "no-income", 0.81);
            Check(dropped.Better(best) && !best.Better(dropped), "stuck paying for the next skeleton goal beats stuck on an economy buy before it");
            Check(Stuck(full.RemoveAt(2), 2, "no-income", 0.9).Better(dropped) && !Stuck(full.RemoveAt(2), 2, "no-income", 0.5).Better(dropped),
                "stuck at the same goal: more of its price held is ahead");
            Check(Stuck(full, 2, "no-income", 0.3).Better(best), "the same economy buy, more of its price held: ahead");
            Built inserted = Stuck(full.Insert(2, "buy:buytree0"), 2, "no-income", 0.95);
            Check(!inserted.Better(dropped) && !inserted.Better(best) && best.Better(inserted), "a buy put in and stuck at 95% is dead weight: behind both");
            Check(!Stuck(full.RemoveAt(2), 2, "miss", 0).Better(best), "a miss isn't ahead of a stuck run");
            Built sooner = Stuck(full.RemoveAt(2), 2, "no-income", 0.5), later = Stuck(full.RemoveAt(2), 2, "no-income", 0.81);
            sooner.Physics[0] = 99;
            later.Physics[0] = 101;
            Check(dropped.Better(sooner) && later.Better(sooner), "stuck: the price held counts before reaching the last skeleton goal sooner");
            Check(dropped.Better(later) && !later.Better(dropped), "stuck alike: the sooner is ahead");
            Built a = Stuck(full, 1, "unreachable", 0), b = Stuck(full, 1, "unreachable", 0);
            b.Physics[0] = 99;
            Check(b.Better(a) && !a.Better(b), "not stuck for income: the last skeleton goal's tick decides, as before");
        }

        // A course's own score (its game timer) ranks complete runs, not their live time.
        static void PolishBookkeeping()
        {
            var b = new Built { Plan = new RunPlan(new[] { "a00001", "b00002", "c00003" }, new[] { "at:1,1,1,1", "at:2,2,2,2", "at:3,3,3,3" }) };
            b.Inputs.AddRange(Enumerable.Range(0, 30).Select(i => new InputTick { x = i % 2 }));
            b.StepEnd.AddRange(new[] { 10, 20, 30 });
            b.Physics.AddRange(new long[] { 10, 40, 50 });
            b.Held.AddRange(new long[] { 0, 0, 0 });
            var tried = new HashSet<(string, ulong)>();
            Check(Improver.PolishOrder(b, tried).SequenceEqual(new[] { 1, 0, 2 }), "polish: the longest segment first, then in order");
            tried.Add(Improver.PolishKey(b, 1));
            Check(Improver.PolishOrder(b, tried).SequenceEqual(new[] { 0, 2 }), "polish: a goal tried on this best is skipped");
            b.Inputs[25] = new InputTick { x = 1, held = true };
            Check(Improver.PolishOrder(b, tried).SequenceEqual(new[] { 0, 2 }), "polish: a change after a tried goal's segment doesn't try it again");
            b.Inputs[5] = new InputTick { x = 1, held = true };
            Check(Improver.PolishOrder(b, tried).SequenceEqual(new[] { 1, 0, 2 }), "polish: a change before it does");
        }

        static void ScoreRanking()
        {
            var plan = new RunPlan(new[] { "a00001" }, new[] { "farm:4" });
            Built Done(long physics, Func<List<InputTick>, long> score)
            {
                var b = new Built { Plan = plan, Scorer = score };
                b.StepEnd.Add(1); b.Physics.Add(physics); b.Held.Add(0); b.InputAt.Add(1);
                return b;
            }
            Check(Done(100, null).Better(Done(120, null)), "no score: the sooner run is ahead");
            Check(Done(120, _ => 5).Better(Done(100, _ => 9)) && !Done(100, _ => 9).Better(Done(120, _ => 5)), "a score reverses live time: the lower score is ahead");
            Check(!Done(100, _ => 5).Better(Done(120, _ => 5)), "the same score: not ahead");
        }

        // A goal's avoid boxes follow it to its place in an edited plan (one goal more before it), not stay at its old index.
        static void AvoidsByPlanIds()
        {
            string root = TechGrafts.SourceRoot();
            if (root == null) return;
            foreach (string file in Directory.GetFiles(Path.Combine(root, "runs"), "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                Solve.RunAvoids def = Solve.RunDefinitionAvoids(name);
                string[] ids = def?.Goals?.Select(g => g.Id).ToArray();
                int i = ids == null ? -1 : Array.FindIndex(def.Goals, g => g.Avoid?.Any(b => b.Boxes?.Length > 0) == true);
                if (i < 0) continue;
                float[] box = def.Goals[i].Avoid.First(b => b.Boxes?.Length > 0).Boxes[0];
                RunPlan plan = new RunPlan(ids, def.Goals.Select(g => g.Goal).ToArray()).Insert(0, "farm:1");
                var search = new SegmentSearch(new RunContext { Name = name, HeaderIds = ids });
                bool At(int after) => search.Avoids(plan.Ids, i + 1).Avoid.Any(b => b.After == after && b.X0 == Math.Min(box[0], box[2]) && b.Y0 == Math.Min(box[1], box[3]));
                Check(At(i + 1) && !At(i), $"{name}: goal {i + 1} {def.Goals[i].Goal}'s avoid boxes are on it at its place {i + 2} in a plan with a goal put before it");
                return;
            }
        }

        static void ConfirmLive(string trace, string best)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            string dir = Path.Combine(Path.GetTempPath(), "igtap-run-confirm-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            Built built = Solve.OnThread(() => new Builder(ctx, SegmentLibrary.Load(Path.Combine(dir, "s.json"), Array.Empty<string>()), new SegmentSearch(ctx),
                new SegmentFacts(Path.Combine(dir, "f.json")), false).Harvest(plan, inputs, "harvest"));
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            Confirm.Investigate(ctx, built, http, e => Console.WriteLine("event " + JsonSerializer.Serialize(e)), m => Console.WriteLine(m));
            string confirm = RunFiles.FileOf(ctx, ".confirm.json");
            Check(File.Exists(confirm) && File.ReadAllText(confirm).Contains("[MATCH]"), $"{ctx.Name}: the game's trace of its best replays here: {(File.Exists(confirm) ? File.ReadAllText(confirm) : "no confirm file")}");
            Directory.Delete(dir, true);
        }

        // Goal to goal from captured states, the harvest and a rebuild after goal `from` out of its library (other segments than
        // the route's: TouchGrass-ANY% from 300 picks another goal 312) must replay straight to the same goals on the same ticks.
        static void HarvestVsStraight(string trace, string best, int from)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            string dir = Path.Combine(Path.GetTempPath(), "igtap-run-hvs-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var (h, hDiffer, b, bDiffer) = Solve.OnThread(() =>
            {
                var builder = new Builder(ctx, SegmentLibrary.Load(Path.Combine(dir, "s.json"), Array.Empty<string>()), new SegmentSearch(ctx), new SegmentFacts(Path.Combine(dir, "f.json")), false);
                string Differ(Built x)
                {
                    long[] built = x.Physics.ToArray();
                    if (builder.Verify(x) == null) return "its straight replay reaches fewer goals";
                    int g = Enumerable.Range(0, x.Reached).FirstOrDefault(i => built[i] != x.Physics[i], -1);
                    return g < 0 ? null : $"goal {g + 1} {plan.Labels[g]}: built {built[g]}, straight {x.Physics[g]}";
                }
                Built harvested = builder.Harvest(plan, inputs, "harvest");
                string hd = Differ(harvested);
                Built rebuilt = builder.Build(plan, harvested, from);
                return (harvested.Reached, hd, rebuilt.Reached, Differ(rebuilt));
            });
            Directory.Delete(dir, true);
            Console.WriteLine($"  {ctx.Name}: the harvest reaches {h} of {plan.Count} goals, the rebuild after goal {from} {b}");
            Check(hDiffer == null, $"{ctx.Name}: the harvest ({h} goals) replays straight: {hDiffer}");
            Check(bDiffer == null, $"{ctx.Name}: the rebuild after goal {from} ({b} goals) replays straight: {bDiffer}");
        }

        // A polish's forced search at a goal, seeded with the best's own way there: the own way reaches the goal on the harvest's tick.
        static void OwnAt(string trace, string best, int goal)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            string dir = Path.Combine(Path.GetTempPath(), "igtap-run-own-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var (harvestTick, builtTick) = Solve.OnThread(() =>
            {
                var builder = new Builder(ctx, SegmentLibrary.Load(Path.Combine(dir, "s.json"), Array.Empty<string>()), new SegmentSearch(ctx) { Parallelism = 4 },
                    new SegmentFacts(Path.Combine(dir, "f.json")), true);
                Built h = builder.Harvest(plan, inputs, "harvest");
                int from = goal == 0 ? 0 : h.StepEnd[goal - 1];
                (builder.ForceSearchAt, builder.ForceBudget, builder.MaxSearches, builder.ForceSeed) = (goal, 160_000, 1, h.Inputs.GetRange(from, h.StepEnd[goal] - from));
                Built c = builder.Build(plan, h, goal);
                return (h.Physics[goal], c.Reached > goal ? c.Physics[goal] : -1);
            });
            Directory.Delete(dir, true);
            Check(builtTick >= 0 && builtTick <= harvestTick, $"{ctx.Name}: goal {goal + 1} polished from its own way: {builtTick} against the harvest's {harvestTick}");
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

        // A best broken as a fidelity fix breaks a route (a jump pressed a tick late, mid-run), its own replay the old path and
        // goal ticks: the rejoin gets it back to every goal the best reaches.
        static void RejoinBroken(string trace, string best)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            var plan = new RunPlan(ctx.HeaderIds, ctx.HeaderGoals.Select(g => g.label).ToArray());
            (int reached, long[] at, long[] atPhysics, Rejoin.Path path, List<InputTick> broken, int where, int brokenReached) = Solve.OnThread(() =>
            {
                Simulation sim = ctx.Make();
                var rows = new List<(long, long, int, float, float)>();
                RunResult r = sim.Run(inputs, null, null, 1000000, _ => rows.Add((sim.Engine.PhysicsTicks, sim.Engine.RunTicks, sim.Progress, sim.PlayerWorld.x, sim.PlayerWorld.y)));
                long[] Pad(long[] a) => a.Take(r.Progress).Concat(Enumerable.Repeat(-1L, plan.Count - r.Progress)).ToArray();
                (long[] goalInput, long[] goalPhysics) = (Pad(r.GoalInputTicks), Pad(r.GoalPhysicsTicks));
                Rejoin.Path p = Rejoin.Path.Of(rows, r.GoalPhysicsTicks.Take(r.Progress).ToArray());
                // The first jump past mid-run whose press a tick later breaks the run.
                for (int i = inputs.Count / 2; i + 1 < inputs.Count; i++)
                {
                    if (!inputs[i].press || inputs[i + 1].press || inputs[i + 1].release || !inputs[i + 1].held) continue;
                    var b = inputs.ToList();
                    b[i] = b[i] with { press = false, held = false };
                    b[i + 1] = b[i + 1] with { press = true };
                    RunResult rb = sim.Run(b, null, null, 1000000);
                    if (rb.Progress < r.Progress) return (r.Progress, goalInput, goalPhysics, p, b, i, rb.Progress);
                }
                return (r.Progress, goalInput, goalPhysics, p, (List<InputTick>)null, -1, 0);
            });
            Check(broken != null, $"{ctx.Name}: a jump pressed a tick late past mid-run breaks the best");
            var rejoin = new Rejoin(ctx, plan, broken, at, atPhysics, path, new SegmentSearch(ctx) { Parallelism = 4 }, 4) { Log = Console.WriteLine, Budget = TimeSpan.FromMinutes(5) };
            List<InputTick> repaired = rejoin.Repair();
            int got = Solve.OnThread(() => ctx.Make().Run(repaired, null, null, 1000000).Progress);
            Check(got >= reached, $"{ctx.Name}: broken at input {where} (reaches {brokenReached} of {reached} goals), rejoined to {got}");
        }

        // A state captured at goal k and restored must play on exactly as the straight replay does.
        static void RestoreAt(string trace, string best, int k)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            string report = Solve.OnThread(() =>
            {
                Simulation sim = ctx.Make();
                RunResult straight = sim.Run(inputs, null, null, 1000000);
                SimSnapshot at = sim.AdvanceToGoal(inputs, k, out int consumed);
                sim.SetStart(at, consumed);
                RunResult resumed = sim.Run(inputs.Skip(consumed).ToList(), null, null, 1000000);
                var rows = Enumerable.Range(k, Math.Min(6, straight.Progress - k)).Select(g =>
                    $"goal {g + 1} {ctx.HeaderGoals[g].label}: straight {straight.GoalPhysicsTicks[g]} resumed {(g < resumed.Progress ? resumed.GoalPhysicsTicks[g].ToString() : "-")}");
                return $"straight reaches {straight.Progress}, resumed from goal {k} reaches {resumed.Progress} ({resumed.EndReason})" + Environment.NewLine + "  "
                    + string.Join(Environment.NewLine + "  ", rows);
            });
            Console.WriteLine(report);
        }

        // `--producers N LABEL`: where the best stands after goal N (1-based), the economy drive finds LABEL among the producers of the
        // currency goal N+1 is short of (ZipEscape%: after goal 840 buy:buycd8, record:8 pays the CloneDust buy:buyclonecd8 lacks).
        static void Producers(string trace, string best, int n, string label)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(best));
            string[] labels = d.RootElement.TryGetProperty("goals", out JsonElement g) ? g.EnumerateArray().Select(x => x.GetProperty("label").GetString()).ToArray()
                : ctx.HeaderGoals.Select(x => x.label).ToArray();
            FullTasGoal[] goals = labels.Select(l => RunGoals.Goal(l, ctx.Known)).ToArray();
            var (shortOf, found) = Solve.OnThread(() =>
            {
                Simulation sim = ctx.Make();
                sim.UseGoals(sim.Goals.For(goals));
                SimSnapshot at = sim.AdvanceToGoal(inputs, n, out _) ?? throw new InvalidOperationException($"FAIL: the best doesn't reach goal {n}");
                var need = Income.Short(sim, at, goals[n].boxes) ?? throw new InvalidOperationException($"FAIL: goal {n + 1} {labels[n]} is short of nothing");
                return (need, Income.Of(sim, at, need.currency));
            });
            Console.WriteLine($"  after goal {n}: {labels[n]} is {shortOf.by:G4} {shortOf.currency} short; producers: "
                + string.Join(", ", found.Select(p => $"{p.Label} {p.Gain:G3} in {p.Ticks:0} ticks")));
            Check(found.Any(p => p.Label == label), $"{ctx.Name}: {label} pays the {shortOf.currency} goal {n + 1} {labels[n]} lacks");
        }

        // Goal by goal from captured states (as the builder plays), against one straight replay: where do they part?
        static void ChainTo(string trace, string best, int k)
        {
            RunContext ctx = RunContext.Open(trace);
            List<InputTick> inputs = Inputs(best);
            Console.WriteLine(Solve.OnThread(() =>
            {
                Simulation sim = ctx.Make();
                RunResult straight = sim.Run(inputs, null, null, 1000000);
                SimSnapshot at = sim.Start;
                int used = 0;
                for (int g = 0; g < k; g++)
                {
                    sim.SetStart(at, used);
                    List<InputTick> rest = inputs.Skip(used).ToList();
                    SimSnapshot next = sim.AdvanceToGoal(rest, g + 1, out int consumed);
                    if (next == null) return $"chain: goal {g + 1} not reached from goal {g}'s state";
                    long tick = next.Goal.PhysicsTicks[g];
                    if (tick != straight.GoalPhysicsTicks[g]) return $"chain parts at goal {g + 1} {ctx.HeaderGoals[g].label}: straight {straight.GoalPhysicsTicks[g]}, chain {tick}";
                    at = next;
                    used += consumed;
                }
                sim.SetStart(at, used);
                RunResult on = sim.Run(inputs.Skip(used).ToList(), null, null, 1000000);
                return $"chain to goal {k} matches the straight replay; from there it reaches {on.Progress} of the straight replay's {straight.Progress} ({on.EndReason})";
            }));
        }

        // A probe's rows as 1x would log the engine's: every physics tick, its input and Velocity that tick's, its body, held frames
        // and Cash the tick before's (OneX).
        static List<OneX.Tick> AsProbe(IReadOnlyList<OneX.Tick> full)
        {
            var game = new List<OneX.Tick>();
            for (int i = 0; i < full.Count; i++)
            {
                OneX.Tick now = full[i], before = i == 0 ? new OneX.Tick() : full[i - 1];
                game.Add(new OneX.Tick { Physics = now.Physics, Input = now.Input, Vx = now.Vx, Vy = now.Vy, X = before.X, Y = before.Y, Held = before.Held, Cash = before.Cash });
            }
            OneX.Tick last = full[^1];
            game.Add(new OneX.Tick { Physics = last.Physics + 1, Input = last.Input + 1, X = last.X, Y = last.Y, Held = last.Held, Cash = last.Cash });
            return game;
        }

        static void OneXAlign()
        {
            // 60 ticks; a dash on input 10: two locked ticks (11, 12) take no input and a 12-frame freeze holds after tick 10.
            var full = new List<OneX.Tick>();
            long input = 0, held = 0;
            for (long p = 1; p <= 60; p++)
            {
                if (p != 11 && p != 12) input++;
                if (p == 10) held += 12;
                full.Add(new OneX.Tick { Physics = p, Input = input, Held = held, X = 100 + 9.2 * p, Y = -300 - p * p * 0.1, Vx = 460, Vy = -10 * p, Cash = 1e12 + 37 * p });
            }
            List<OneX.Tick> engine = full;
            OneX.Result same = OneX.Align(engine, AsProbe(full));
            Check(!same.Parted && same.Compared == engine.Count, $"1x rows of the engine's own run align on every tick ({same.Compared} of {engine.Count})");
            OneX.Result Changed(long physics, Func<OneX.Tick, OneX.Tick> edit) =>
                OneX.Align(engine, AsProbe(full).Select(t => t.Physics == physics ? edit(t) : t).ToList());
            OneX.Result shortFreeze = Changed(11, t => { t.Held--; return t; });
            Check(shortFreeze.Parted && shortFreeze.Field == "held" && shortFreeze.Physics == 10 && shortFreeze.Compared == 9, "a freeze a frame short at 1x parts on held at its tick (" + OneX.Describe(shortFreeze) + ")");
            OneX.Result later = OneX.Align(engine, AsProbe(full).Where(t => t.Physics >= 30).ToList());
            Check(!later.Parted && later.Compared == 31, "a log from a later tick (logFrom) compares from there");
            OneX.Result x = OneX.Align(engine, AsProbe(full).Select(t => { if (t.Physics >= 21) t.X += 0.05; return t; }).ToList());
            Check(x.Parted && x.Field == "x" && x.Physics == 20, "the body 0.05 off from a tick on parts on x at the tick before the row (" + OneX.Describe(x) + ")");
            Check(!Changed(21, t => { t.Y -= 3.86; return t; }).Parted, "the body off on one tick only (a step-up's transform the body follows a step later) doesn't part");
            Check(!OneX.Align(engine.Select(t => { if (t.Physics == 25) t.Vy = 0; return t; }).ToList(), AsProbe(full)).Parted, "engine vy 0 (OnCollisionStay2D after 1x's row) doesn't part");
            OneX.Result vy = Changed(25, t => { t.Vy = 0; return t; });
            Check(vy.Parted && vy.Field == "vy" && vy.Physics == 25, "1x vy 0 where the engine's isn't parts on vy (" + OneX.Describe(vy) + ")");
            Check(!Changed(31, t => { t.Cash *= 1 + 1e-13; return t; }).Parted, "Cash within a relative 1e-9 is the same");
            OneX.Result cash = Changed(31, t => { t.Cash *= 1 + 1e-8; return t; });
            Check(cash.Parted && cash.Field == "cash" && cash.Physics == 30, "Cash 1e-8 off parts on cash");
            OneX.Result input2 = Changed(40, t => { t.Input++; return t; });
            Check(input2.Parted && input2.Field == "input" && input2.Physics == 40, "another input count parts on input at its own tick");
            Check(!OneX.Align(engine, AsProbe(full).Select(t => { t.Held = -1; return t; }).ToList()).Parted, "a log without held frames (before they were logged) skips them");
        }

        static void OneXSelf(string ticksOut)
        {
            List<OneX.Tick> engine = OneX.EngineRows(ticksOut);
            var game = new Dictionary<long, OneX.Tick>();
            foreach (OneX.Tick e in engine)
            {
                game.TryGetValue(e.Physics, out OneX.Tick now);
                (now.Physics, now.Input, now.Vx, now.Vy) = (e.Physics, e.Input, e.Vx, e.Vy);
                game[e.Physics] = now;
                game.TryGetValue(e.Physics + 1, out OneX.Tick next);
                (next.Physics, next.X, next.Y, next.Held, next.Cash) = (e.Physics + 1, e.X, e.Y, e.Held, e.Cash);
                game[e.Physics + 1] = next;
            }
            OneX.Result r = OneX.Align(engine, game.Values.OrderBy(t => t.Physics).ToList());
            Check(!r.Parted && r.Compared == engine.Count, $"{Path.GetFileName(ticksOut)} against itself as 1x would log it: {OneX.Describe(r)}");
            OneX.Tick[] moved = engine.Select(t => { t.Held += t.Physics > engine[engine.Count / 2].Physics ? 1 : 0; return t; }).ToArray();
            OneX.Result off = OneX.Align(moved, game.Values.OrderBy(t => t.Physics).ToList());
            Check(off.Parted && off.Field == "held" && off.Physics > engine[engine.Count / 2].Physics, "one more held frame from mid-run parts there: " + OneX.Describe(off));
        }
    }
}
