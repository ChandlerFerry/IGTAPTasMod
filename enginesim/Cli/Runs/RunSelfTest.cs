using System;
using System.Collections.Generic;
using System.Linq;
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
                Console.WriteLine($"run-selftest: {_passed} checks passed");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(ex.Message);
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
    }
}
