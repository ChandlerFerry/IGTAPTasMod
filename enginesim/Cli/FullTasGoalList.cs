using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli
{
    // Goal ids (docs/fulltas.md, "Goal ids"): a file, a flag, the gate or an event names a goal by its id, never by its place in
    // the list. The search itself works on places (_labels, Route.GoalInput): they never leave the process.
    sealed partial class FullTas
    {
        /// <summary>The run's goal ids, one per <see cref="_labels"/> entry.</summary>
        string[] _ids;

        /// <summary>A goal list with its ids: what a file or the gate takes (<see cref="Json"/>: [{id, goal}]).</summary>
        internal sealed record GoalList(string[] Ids, string[] Labels)
        {
            public object Json => Ids.Select((id, i) => new { id, goal = Labels[i] }).ToArray();
            public bool Same(GoalList o) => o != null && Ids.SequenceEqual(o.Ids) && Labels.SequenceEqual(o.Labels);
        }

        GoalList Goals => new GoalList(_ids, _labels);

        /// <summary>
        /// The run's goals become <paramref name="labels"/>: with <paramref name="ids"/>, those; without, a goal the old list shares
        /// keeps its id and a new one gets a new id (<see cref="FullTasGoalIds.Carry"/>).
        /// </summary>
        void SetGoals(string[] labels, string[] ids = null)
        {
            ids ??= FullTasGoalIds.Carry(_ids, _labels, labels);
            string invalid = FullTasGoalIds.Invalid(ids, labels.Length);
            if (invalid != null) throw new InvalidOperationException($"{_name}: {invalid}");
            _ids = ids;
            _labels = labels;
            _goals = labels.Length;
        }

        /// <summary>Goal k's id (k from 1, as the search counts goals); null for no goal (0, or past the list).</summary>
        string IdOf(int k) => k >= 1 && k <= _ids.Length ? _ids[k - 1] : null;

        /// <summary>A goal's place in the run's list by its id (a flag's, a request's); throws for an id the run doesn't have.</summary>
        int GoalAt(string id)
        {
            int i = Array.IndexOf(_ids, id);
            if (i < 0) throw new ArgumentException($"{_name} has no goal with id \"{id}\" (goal ids are runs/{_name}.json's \"goals\"[].id)");
            return i;
        }

        /// <summary>A goal named for a flag (`--from`, `--until`, `--stretch`): its id, else its label's first place.</summary>
        int GoalNamed(string idOrLabel)
        {
            int i = Array.IndexOf(_ids, idOrLabel);
            if (i < 0) i = Array.IndexOf(_labels, idOrLabel);
            if (i < 0) throw new ArgumentException($"{_name} has no goal \"{idOrLabel}\" (an id or a label)");
            return i;
        }

        /// <summary>
        /// The run definition's goals (runs/&lt;run&gt;.json "goals") as the run's are now: [{id, goal, ...}], each goal's own fields (its
        /// avoids) kept by its id; a goal no longer in the run takes them with it.
        /// </summary>
        JsonArray RunDefGoals(JsonArray old)
        {
            var byId = new Dictionary<string, JsonObject>();
            foreach (JsonNode g in old ?? new JsonArray())
                if (g is JsonObject o && o["id"]?.GetValue<string>() is string id) byId[id] = o;
            return new JsonArray(_ids.Select((id, i) =>
            {
                var goal = new JsonObject { ["id"] = id, ["goal"] = _labels[i] };
                if (byId.TryGetValue(id, out JsonObject was))
                    foreach (var field in was)
                        if (field.Key != "id" && field.Key != "goal") goal[field.Key] = field.Value?.DeepClone();
                return (JsonNode)goal;
            }).ToArray());
        }

        /// <summary>The run's avoid boxes (<see cref="Solve.FullTasPaths"/>) for a window heading for the goals with these labels
        /// (null: every block on).</summary>
        OfflineSolver.ForcedPath Avoids(IEnumerable<string> heading)
        {
            HashSet<string> to = heading == null ? null : new HashSet<string>(heading);
            return Solve.FullTasPaths(_name, _ids, to == null ? null : Enumerable.Range(0, _goals).Where(i => to.Contains(_labels[i])).ToHashSet());
        }

        /// <summary>A Full-TAS trace header's goals ("fulltas"."goals"[]: {id, label, ...}).</summary>
        internal static GoalList HeaderGoals(JsonElement fulltas)
        {
            JsonElement[] goals = fulltas.GetProperty("goals").EnumerateArray().ToArray();
            var ids = goals.Select(g => g.TryGetProperty("id", out JsonElement id) ? id.GetString() : null).ToArray();
            var labels = goals.Select(g => g.GetProperty("label").GetString()).ToArray();
            string invalid = FullTasGoalIds.Invalid(ids, labels.Length);
            if (invalid != null) throw new InvalidOperationException("The trace's goals have no ids (" + invalid + "): capture it again, or run tools/migrate-goal-ids.py");
            return new GoalList(ids, labels);
        }

        /// <summary>A goal list file entry: {id, goal} (a run file, goalsFrom) or a plain label (a seed's: matched by label).</summary>
        internal static string LabelOf(JsonElement goal)
        {
            if (goal.ValueKind == JsonValueKind.String) return goal.GetString();
            return goal.TryGetProperty("goal", out JsonElement g) ? g.GetString() : goal.GetProperty("label").GetString();
        }

        /// <summary>[{id, goal}] lists (a route's goalsFrom).</summary>
        internal static GoalList ReadGoalList(JsonElement list)
        {
            JsonElement[] goals = list.EnumerateArray().ToArray();
            return new GoalList(goals.Select(g => g.GetProperty("id").GetString()).ToArray(), goals.Select(LabelOf).ToArray());
        }

        /// <summary>One goal of a saved route (an offline best, a candidate, a class's best): where the route reached it (-1: not).</summary>
        internal sealed class SavedGoal
        {
            public string Id, Label;
            public long Input = -1, Physics = -1, Held = -1;
        }

        /// <summary>A saved route's goals: "goals"[] {id, label, input, physics, held} (null: the file isn't one).</summary>
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

        /// <summary>A saved route's goal labels and the input tick it reached each at (-1: not), for <see cref="CurrentInputs"/>.</summary>
        internal static (string[] Labels, long[] Input) SolvedGoals(JsonElement root)
        {
            SavedGoal[] rows = ReadSavedGoals(root) ?? throw new InvalidOperationException("not a saved route: its \"goals\" aren't [{id, label, input, ...}]");
            return (rows.Select(g => g.Label).ToArray(), rows.Select(g => g.Input).ToArray());
        }

        /// <summary>A route's goals as a saved route keeps them: each goal's id and label, where the route reached it and its idle.</summary>
        object SavedGoalsJson(Route r)
        {
            Dictionary<int, IdleSpan> idles = null;
            try { idles = Idles(r).ToDictionary(i => i.Goal - 1); }
            catch (InvalidOperationException ex) { Log("idles: not measured: " + ex.Message); }
            return _labels.Select((label, g) =>
            {
                bool reached = g < r.Progress;
                IdleSpan idle = idles != null && idles.TryGetValue(g, out IdleSpan i) ? i : null;
                return new
                {
                    id = _ids[g],
                    label,
                    input = reached ? r.GoalInput[g] : -1,
                    physics = reached ? r.GoalPhysics[g] : -1,
                    held = reached && r.GoalHeld != null ? r.GoalHeld[g] : -1,
                    // A buy's wait for its Cash, standing at its box (FullTasIdles.cs): the studio's wait rows, the overlay's banner.
                    idle = idle == null ? null : new { arrive = new[] { idle.ArrivePhysics, idle.ArriveHeld }, afford = new[] { idle.AffordPhysics, idle.AffordHeld } },
                };
            }).ToArray();
        }
    }
}
