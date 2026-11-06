using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    static class FullTas
    {
        internal sealed record GoalList(string[] Ids, string[] Labels)
        {
            public object Json => Ids.Select((id, i) => new { id, goal = Labels[i] }).ToArray();
            public bool Same(GoalList o) => o != null && Ids.SequenceEqual(o.Ids) && Labels.SequenceEqual(o.Labels);
        }

        internal static GoalList HeaderGoals(JsonElement fulltas)
        {
            JsonElement[] goals = fulltas.GetProperty("goals").EnumerateArray().ToArray();
            var ids = goals.Select(g => g.TryGetProperty("id", out JsonElement id) ? id.GetString() : null).ToArray();
            var labels = goals.Select(g => g.GetProperty("label").GetString()).ToArray();
            string invalid = FullTasGoalIds.Invalid(ids, labels.Length);
            if (invalid != null) throw new InvalidOperationException("The trace's goals have no ids (" + invalid + "): capture it again, or run tools/migrate-goal-ids.py");
            return new GoalList(ids, labels);
        }

        internal static string LabelOf(JsonElement goal)
        {
            if (goal.ValueKind == JsonValueKind.String) return goal.GetString();
            return goal.TryGetProperty("goal", out JsonElement g) ? g.GetString() : goal.GetProperty("label").GetString();
        }

        internal static GoalList ReadGoalList(JsonElement list)
        {
            JsonElement[] goals = list.EnumerateArray().ToArray();
            return new GoalList(goals.Select(g => g.GetProperty("id").GetString()).ToArray(), goals.Select(LabelOf).ToArray());
        }

        internal sealed class SavedGoal
        {
            public string Id, Label;
            public long Input = -1, Physics = -1, Held = -1;
        }

        internal static SavedGoal[] ReadSavedGoals(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("goals", out JsonElement goals) || goals.ValueKind != JsonValueKind.Array) return null;
            long Tick(JsonElement g, string name) => g.TryGetProperty(name, out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : -1;
            return goals.EnumerateArray().Select(g => g.ValueKind != JsonValueKind.Object ? null : new SavedGoal
            {
                Id = g.TryGetProperty("id", out JsonElement id) ? id.GetString() : null,
                Label = LabelOf(g),
                Input = Tick(g, "input"),
                Physics = Tick(g, "physics"),
                Held = Tick(g, "held"),
            }).ToArray() is SavedGoal[] rows && rows.All(r => r != null) ? rows : null;
        }

        internal static (string[] Labels, long[] Input) SolvedGoals(JsonElement root)
        {
            SavedGoal[] rows = ReadSavedGoals(root) ?? throw new InvalidOperationException("not a saved route: its \"goals\" aren't [{id, label, input, ...}]");
            return (rows.Select(g => g.Label).ToArray(), rows.Select(g => g.Input).ToArray());
        }

        internal static int CurrentInputs(string[] solved, long[] goalInputTicks, string[] labels, int count)
        {
            int changed = 0;
            while (changed < solved.Length && changed < labels.Length && solved[changed] == labels[changed]) changed++;
            if (changed == solved.Length && changed == labels.Length) return count;
            int reached = 0;
            while (reached < changed && reached < goalInputTicks.Length && goalInputTicks[reached] >= 0) reached++;
            if (reached < changed) return count;
            return changed == 0 ? 0 : (int)Math.Min(goalInputTicks[changed - 1], count);
        }

        internal static JsonElement GoalsHeader(JsonElement header, string[] labels)
        {
            JsonNode node = JsonNode.Parse(header.GetRawText());
            JsonArray old = node["fulltas"]["goals"].AsArray();
            var byLabel = new Dictionary<string, JsonNode>();
            foreach (JsonNode g in old) byLabel.TryAdd(g["label"].GetValue<string>(), g);
            var goals = new JsonArray();
            foreach (string l in labels)
            {
                if (byLabel.TryGetValue(l, out JsonNode g)) { goals.Add(g.DeepClone()); continue; }
                string[] parts = l.Substring("buy:".Length).Split('+');
                JsonNode first = byLabel["buy:" + parts[0]].DeepClone();
                first["label"] = l;
                first["boxes"] = new JsonArray(parts.SelectMany(p => byLabel["buy:" + p]["boxes"].AsArray().Select(b => (JsonNode)b.GetValue<string>())).ToArray());
                goals.Add(first);
            }
            foreach (JsonNode g in goals) g.AsObject().Remove("id");
            node["fulltas"]["goals"] = goals;
            using JsonDocument doc = JsonDocument.Parse(node.ToJsonString(), new JsonDocumentOptions { MaxDepth = 256 });
            return doc.RootElement.Clone();
        }
    }
}
