using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    static class RunFiles
    {
        static readonly JsonSerializerOptions Compact = new JsonSerializerOptions(), Indented = new JsonSerializerOptions { WriteIndented = true };

        public static string FileOf(RunContext run, string ext) => Path.Combine(run.Dir, "fulltas", run.Key + ext);

        public static void WriteRunFile(RunContext run, Built b, (float x, float y, bool facingRight) startAt)
        {
            var waitAt = b.Waits.GroupBy(w => w.Goal).ToDictionary(g => g.Key, g => g.Sum(w => w.Ticks));
            var goals = new JsonArray(b.Plan.Labels.Select((label, g) =>
            {
                bool reached = g < b.Reached;
                var e = new JsonObject
                {
                    ["id"] = b.Plan.Ids[g], ["label"] = label,
                    ["input"] = reached ? b.InputAt[g] : -1, ["physics"] = reached ? b.Physics[g] : -1, ["held"] = reached ? b.Held[g] : -1,
                };
                if (reached && waitAt.TryGetValue(g, out long ticks))
                {
                    long arrive = g == 0 ? 0 : b.Physics[g - 1], held = g == 0 ? 0 : b.Held[g - 1];
                    e["idle"] = new JsonObject { ["arrive"] = new JsonArray(arrive, held), ["afford"] = new JsonArray(arrive + ticks, held) };
                }
                return (JsonNode)e;
            }).ToArray());
            var file = new JsonObject
            {
                ["name"] = run.Name, ["goals"] = goals,
                ["inputs"] = JsonSerializer.SerializeToNode(b.Inputs.Select(t => t.ToJson()).ToArray()),
                ["total"] = b.Complete ? b.Physics[^1] : -1, ["live"] = b.Complete ? b.Live : -1,
                ["startAt"] = new JsonObject { ["x"] = startAt.x, ["y"] = startAt.y, ["facingRight"] = startAt.facingRight },
                ["engine"] = BuildInfo.Commit,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(run.RunFile));
            Solve.WriteShared(run.RunFile, file.ToJsonString(Compact));
        }

        public static void WriteWaits(RunContext run, Built b)
        {
            var rows = new JsonArray(b.Waits.OrderByDescending(w => w.Ticks).Select(w => (JsonNode)new JsonObject
            {
                ["goal"] = w.Goal + 1, ["id"] = b.Plan.Ids[w.Goal], ["label"] = w.Label, ["x"] = Math.Round(w.X, 1), ["y"] = Math.Round(w.Y, 1),
                ["ticks"] = w.Ticks, ["seconds"] = Math.Round(w.Ticks * 0.02, 2), ["currency"] = w.Currency, ["short"] = w.Short,
            }).ToArray());
            Solve.WriteShared(FileOf(run, ".waits.json"), new JsonObject { ["engine"] = BuildInfo.Commit, ["total"] = b.Waits.Sum(w => w.Ticks), ["waits"] = rows }.ToJsonString(Indented));
        }

        public static void WriteFailure(RunContext run, Failure f, string id)
        {
            string file = FileOf(run, ".problems.json");
            var node = f == null ? new JsonObject() : new JsonObject { ["kind"] = f.Kind, ["goal"] = f.Goal + 1, ["id"] = id, ["label"] = f.Label, ["text"] = f.Text };
            Solve.WriteShared(file, new JsonObject { ["engine"] = BuildInfo.Commit, ["failure"] = node }.ToJsonString(Indented));
        }

        public static string RunDef(RunContext run)
        {
            string root = TechGrafts.SourceRoot();
            string def = root == null ? null : Path.Combine(root, "data", "runs", run.Name + ".json");
            return def != null && File.Exists(def) ? def : null;
        }

        // The plugin compares data/runs/<run>.json goals with the trace header's, so both change together.
        public static bool InstallGoals(RunContext run, RunPlan plan)
        {
            if (run.HeaderIds.SequenceEqual(plan.Ids) && run.HeaderGoals.Select(g => g.label).SequenceEqual(plan.Labels)) return false;
            string def = RunDef(run);
            if (def != null)
            {
                JsonObject root = JsonNode.Parse(File.ReadAllText(def)).AsObject();
                var byId = new Dictionary<string, JsonObject>();
                foreach (JsonNode g in root["goals"]?.AsArray() ?? new JsonArray())
                    if (g is JsonObject o && (string)o["id"] is string id) byId[id] = o;
                root["goals"] = new JsonArray(plan.Ids.Select((id, i) =>
                {
                    var goal = new JsonObject { ["id"] = id, ["goal"] = plan.Labels[i] };
                    if (byId.TryGetValue(id, out JsonObject was))
                        foreach (var field in was)
                            if (field.Key != "id" && field.Key != "goal") goal[field.Key] = field.Value?.DeepClone();
                    return (JsonNode)goal;
                }).ToArray());
                Solve.WriteShared(def, root.ToJsonString(Indented) + "\n");
            }
            FullTasGoal[] goals = run.GoalsOf(plan);
            SpliceHeaderGoals(run.Trace, new JsonArray(goals.Select((g, i) => (JsonNode)RunGoals.HeaderEntry(g, plan.Ids[i])).ToArray()).ToJsonString(Compact));
            run.HeaderIds = plan.Ids.ToArray();
            run.HeaderGoals = goals;
            foreach (FullTasGoal g in goals) run.Known.TryAdd(g.label, g);
            return true;
        }

        static void SpliceHeaderGoals(string trace, string goals)
        {
            byte[] head;
            using (FileStream f = File.OpenRead(trace))
            {
                head = new byte[Math.Min(16 << 20, f.Length)];
                f.ReadExactly(head);
            }
            int run = head.AsSpan().IndexOf("\"fulltas\":{"u8), found = run < 0 ? -1 : head.AsSpan(run).IndexOf("\"goals\":["u8);
            if (found < 0) throw new InvalidOperationException(trace + ": no \"fulltas\" goals in its header");
            int open = run + found + "\"goals\":".Length, end = open, depth = 0;
            for (bool text = false; end < head.Length; end++)
            {
                byte c = head[end];
                if (text) { if (c == '\\') end++; else if (c == '"') text = false; }
                else if (c == '"') text = true;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) break;
            }
            if (end >= head.Length) throw new InvalidOperationException(trace + ": the header's goals don't end in its first 16 MB");
            string temp = trace + "." + Environment.ProcessId + ".tmp";
            using (FileStream o = File.Create(temp))
            using (FileStream f = File.OpenRead(trace))
            {
                o.Write(head, 0, open);
                o.Write(System.Text.Encoding.UTF8.GetBytes(goals));
                f.Position = end + 1;
                f.CopyTo(o);
            }
            Solve.Replace(temp, trace);
        }

        public static string WriteOfflineBest(RunContext run, Built b)
        {
            string dir = Path.Combine(run.Dir, "offline-best");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "fulltas-" + run.Key + "-" + b.Live.ToString("0.00", CultureInfo.InvariantCulture) + ".json");
            Solve.WriteShared(file, JsonSerializer.Serialize(new
            {
                fulltas = run.Name, time = b.Live, runTicks = b.Physics[^1],
                goals = b.Plan.Ids.Select((id, i) => new { id, goal = b.Plan.Labels[i] }), ticks = b.Inputs.Select(t => t.ToJson()),
            }));
            return file;
        }
    }
}
