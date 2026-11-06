using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli.Runs
{
    static class Confirm
    {
        public static bool Compare(Built b, JsonElement verdict, Action<object> emit, Action<string> log)
        {
            if (!verdict.TryGetProperty("splits", out JsonElement splits) || splits.ValueKind != JsonValueKind.Array)
            {
                emit(new { type = "confirm", ok = (bool?)null, text = "the gate returned no splits" });
                return true;
            }
            JsonElement[] rows = splits.EnumerateArray().ToArray();
            for (int g = 0; g < Math.Min(rows.Length, b.Reached); g++)
            {
                long ticks = rows[g].GetProperty("ticks").GetInt64(), held = rows[g].TryGetProperty("held", out JsonElement h) ? h.GetInt64() : -1;
                if (ticks == b.Physics[g] && (held < 0 || held == b.Held[g])) continue;
                string label = b.Plan.Labels[g];
                log($"confirm: the game and the engine part at goal {g + 1} {label}: engine physics {b.Physics[g]} held {b.Held[g]}, game {ticks} held {held}");
                emit(new { type = "confirm", ok = (bool?)false, goal = g + 1, id = b.Plan.Ids[g], label, engine = new[] { b.Physics[g], b.Held[g] }, game = new[] { ticks, held } });
                return false;
            }
            bool all = rows.Length >= b.Reached;
            log(all ? $"confirm: the game's splits match the engine's on all {b.Reached} goals" : $"confirm: the game reached {rows.Length} of the engine's {b.Reached} goals");
            emit(new { type = "confirm", ok = (bool?)all, goals = rows.Length });
            return all;
        }

        public static void Investigate(RunContext run, Built b, HttpClient http, Action<object> emit, Action<string> log)
        {
            string name = "confirm-" + run.Name.Replace("%", "pct");
            string body = JsonSerializer.Serialize(new { fulltas = run.Name, ticks = b.Inputs.Select(t => t.ToJson()), name, world = true });
            string tracePath = null;
            for (int attempt = 1; attempt <= 60 && tracePath == null; attempt++)
            {
                using JsonDocument r = JsonDocument.Parse(Solve.Post(http, "/api/enginesim/trace", body));
                if (r.RootElement.TryGetProperty("trace", out JsonElement t) && t.ValueKind == JsonValueKind.String) tracePath = t.GetString();
                else Thread.Sleep(2000);
            }
            if (tracePath == null) { log("confirm: the game wouldn't capture a trace of the route"); return; }
            var psi = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("replay");
            psi.ArgumentList.Add(tracePath);
            using Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            string[] lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            string summary = lines.FirstOrDefault(l => l.Contains(" ticks, ")) ?? "replay printed no summary";
            string[] diffs = lines.Where(l => l.StartsWith("  ", StringComparison.Ordinal)).Take(8).Select(l => l.Trim()).ToArray();
            File.WriteAllText(RunFiles.FileOf(run, ".confirm.json"), JsonSerializer.Serialize(new { engine = BuildInfo.Commit, trace = tracePath, summary, diffs }, new JsonSerializerOptions { WriteIndented = true }));
            log("confirm: " + summary);
            foreach (string d in diffs) log("confirm:   " + d);
            emit(new { type = "confirm", ok = (bool?)false, summary, diffs });
        }
    }
}
