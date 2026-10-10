using System;
using System.Linq;
using System.Text.Json;

namespace IGTAP.EngineSim.Cli.Runs
{
    // The gate's own replay is the first check of the engine: its per-goal splits must be the engine's, tick for tick.
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
    }
}
