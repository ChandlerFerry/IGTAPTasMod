using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    sealed record RunPlan(string[] Ids, string[] Labels)
    {
        public int Count => Labels.Length;

        public bool Same(RunPlan o) => o != null && Ids.SequenceEqual(o.Ids) && Labels.SequenceEqual(o.Labels);

        public RunPlan Insert(int at, string label)
        {
            var ids = Ids.ToList();
            var labels = Labels.ToList();
            ids.Insert(at, FullTasGoalIds.New(Ids));
            labels.Insert(at, label);
            return new RunPlan(ids.ToArray(), labels.ToArray());
        }

        public RunPlan RemoveAt(int at)
        {
            var ids = Ids.ToList();
            var labels = Labels.ToList();
            ids.RemoveAt(at);
            labels.RemoveAt(at);
            return new RunPlan(ids.ToArray(), labels.ToArray());
        }

        public RunPlan Move(int from, int to)
        {
            var ids = Ids.ToList();
            var labels = Labels.ToList();
            string id = ids[from], label = labels[from];
            ids.RemoveAt(from);
            labels.RemoveAt(from);
            ids.Insert(to, id);
            labels.Insert(to, label);
            return new RunPlan(ids.ToArray(), labels.ToArray());
        }
    }

    static class RunGoals
    {
        // A skeleton goal (unlock, area, reward) is never made up: its box or rect must come from the run's capture.
        public static FullTasGoal Goal(string label, IReadOnlyDictionary<string, FullTasGoal> known)
        {
            if (known.TryGetValue(label, out FullTasGoal g)) return g;
            int course = FullTasGoal.CourseOf(label);
            if (course > 0) return new FullTasGoal { label = label, kind = label.Substring(0, label.IndexOf(':')), course = course };
            if (FullTasGoal.IsEconomy(label))
                return new FullTasGoal
                {
                    label = label,
                    kind = FullTasGoal.Buy,
                    boxes = label.Substring(4).Split('+').Select(t => FullTasGoal.Boxes.First(b => b.token == t)).Select(b => FullTasGoal.BoxId(b.path, b.sibling)).ToArray(),
                };
            throw new ArgumentException($"goal {label}: not in the run's trace, and only laps and economy buys are made");
        }

        public static JsonObject HeaderEntry(FullTasGoal g, string id)
        {
            var e = new JsonObject { ["id"] = id, ["label"] = g.label, ["kind"] = g.kind };
            if (g.IsCourse) e["course"] = g.course;
            if (g.boxes != null) e["boxes"] = new JsonArray(g.boxes.Select(b => (JsonNode)b).ToArray());
            if (g.kind == FullTasGoal.At) e["rect"] = new JsonArray(g.x, g.y, g.w, g.h);
            if (g.from != 0) e["from"] = g.from;
            return e;
        }

        public static bool Editable(string label) => FullTasGoal.IsEconomy(label);
    }
}
