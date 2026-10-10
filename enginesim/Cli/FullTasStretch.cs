using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    sealed partial class FullTas
    {
        /// <summary>
        /// fulltas &lt;trace&gt; --stretch ID: one stretch of the run alone, the goal before goal ID to goal ID, searched window after window
        /// from the best's state at goal N-1 until Stop, each window seeded with the fastest way found so far. A way is judged by the
        /// tick it reaches goal N: the rest of the run isn't replayed or re-found (that is a whole run's Improve, which can take the
        /// stretch's best from its file). The best way so far is kept in fulltas/&lt;run&gt;.stretches.json under the goal's id with its
        /// arrival tick and the run's, and announced as a `stretch` event.
        /// </summary>
        /// <summary>A run's stretch bests: fulltas/&lt;run&gt;.stretches.json, { goal id: { label, prefix, ..., inputs } }.</summary>
        internal static string StretchesFile(string runFile, string runKey) => Path.Combine(Path.GetDirectoryName(runFile), runKey + ".stretches.json");

        static bool IsStill(InputTick t) => t.x == 0f && t.y == 0f && !t.held && !t.press && !t.release && !t.dash && t.dashJump == 0 && !t.reset;

        int StretchSearch(string id)
        {
            string from = Open();
            int goal = GoalAt(id) + 1;
            if (goal > _best.Progress) throw new ArgumentException($"--stretch wants a goal the best reaches, not {id} ({_labels[goal - 1]}, goal {goal - 1}; the best reaches {_best.Progress})");
            if (FullTasGoal.IsLap(_labels[goal - 1])) throw new ArgumentException($"--stretch {id} is a lap ({_labels[goal - 1]}), improved as its course's route");
            Route best = _best;
            int prefixLength = goal == 1 ? 0 : (int)Math.Min(best.GoalInput[goal - 2], best.Inputs.Count);
            int end = (int)Math.Min(best.GoalInput[goal - 1], best.Inputs.Count);
            long startTick = goal == 1 ? 0 : best.GoalPhysics[goal - 2], runTick = best.GoalPhysics[goal - 1];
            // A buy the run waits for under its box (an idle, FullTasIdles.cs) is two things: the walk to the box, and the wait for its
            // Cash, which is the idles' to optimize. The stretch is the walk: its goal is the box's spot (where an idle counts the
            // player as at the box), judged by the arrival, the run's own arrival the tick to beat.
            string target = null;
            if (_runGoals.Goals[goal - 1].kind == FullTasGoal.Buy && Idles(best).FirstOrDefault(i => i.Goal == goal) is IdleSpan idle)
            {
                Zone z = BoxZone(_runGoals.Goals[goal - 1]);
                float bottom = z.y0 - IdleReachBelow;
                target = FormattableString.Invariant($"at:{z.Middle:0},{(bottom + z.y1) / 2:0},{z.x1 - z.x0 + 2 * z.playerHalf:0},{z.y1 - bottom:0}");
                runTick = idle.ArrivePhysics;
                var goals = _runGoals.Goals.ToList();
                goals[goal - 1] = NewGoal(target)?.goal ?? throw new InvalidOperationException("no area goal " + target);
                _planBase = _runGoals;
                Track(goals);
                Log($"stretch: goal {goal - 1} {_labels[goal - 1]} waits {idle.AffordPhysics - idle.ArrivePhysics} ticks for its Cash under its box: "
                    + $"the stretch is the walk to the box ({target}), the run's arrival on tick {runTick}");
            }
            // A stretch that opens with a wait (quick restarts and 25 or more still inputs: waiting for Cash) starts where it moves:
            // the wait is the idles' to optimize, not the stretch's (ZipEscape%'s buytree7: 1,800 still ticks, then 316 moving).
            {
                int i = prefixLength, still = 0;
                while (i < end && (best.Inputs[i].reset || IsStill(best.Inputs[i]))) { if (!best.Inputs[i].reset) still++; i++; }
                if (still >= 25 && i < end)
                {
                    startTick += i - prefixLength;
                    Log($"stretch: goal {goal - 1} opens with {i - prefixLength} still or restart inputs (a wait): searched from input {i}, tick {startTick}");
                    prefixLength = i;
                }
            }
            List<InputTick> prefix = best.Inputs.GetRange(0, prefixLength);
            // --minutes is the whole session: TimeUp stops it (Stop too).
            string file = StretchesFile(_runFile, _runKey);
            List<InputTick> stretchBest = best.Inputs.GetRange(prefixLength, end - prefixLength);
            long bestTick = runTick;
            // A stretch best kept by an earlier session from the same start seeds this one, and a find must beat it to replace it:
            // the same stretch start (input and tick), the run's same arrival there, judged the same way (an arrival or the buy).
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?[id] is JsonObject kept)
            {
                using JsonDocument saved = JsonDocument.Parse(kept.ToJsonString());
                JsonElement root = saved.RootElement;
                bool same = root.GetProperty("prefixLength").GetInt32() == prefixLength && root.GetProperty("startTick").GetInt64() == startTick
                    && root.GetProperty("runTick").GetInt64() == runTick && (root.TryGetProperty("arrival", out JsonElement a) && a.GetBoolean()) == (target != null);
                if (same && root.GetProperty("bestTick").GetInt64() < bestTick)
                {
                    stretchBest = Solve.ReadInputs(root);
                    bestTick = root.GetProperty("bestTick").GetInt64();
                }
            }
            Console.WriteLine($"Full-TAS {_name}: stretch to goal {goal - 1} ({_labels[goal - 1]}) from goal {goal - 2}, {threadsText()}; "
                + $"the run's: {runTick - startTick} ticks; best so far: {bestTick - startTick} ticks (best from {from})");
            string threadsText() => _threads + " threads";
            Emit(new { type = "session", run = _name, goals = Goals.Json, window = 1, threads = _threads, minutes = _minutes, windowMinutes = _windowMinutes,
                from, best = RouteJson(_best) });
            void Announce(bool improved) => Emit(new { type = "stretch", goal = id, label = _labels[goal - 1], runTicks = runTick - startTick,
                bestTicks = bestTick - startTick, improved });
            Announce(false);
            LoadSeeds();
            new Thread(WatchStdin) { IsBackground = true }.Start();
            // Each find kept the moment it's found: a window's last goal is goal N, so its tick there is the find's arrival.
            _onFind = (inputs, atGoal) =>
            {
                if (atGoal >= bestTick) return;
                bestTick = atGoal;
                stretchBest = inputs.ToList();
                JsonObject all = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)).AsObject() : new JsonObject();
                all[id] = JsonSerializer.SerializeToNode(new { label = _labels[goal - 1], prefixLength, arrival = target != null,
                    startTick, runTick, bestTick, saved = runTick - bestTick, inputs = stretchBest.Select(t => t.ToJson()) });
                Solve.WriteShared(file, all.ToJsonString());
                Log($"stretch: goal {id} ({_labels[goal - 1]}) in {bestTick - startTick} ticks (the run's {runTick - startTick}): {runTick - bestTick} ticks faster -> {Path.GetFileName(file)}");
                Announce(true);
            };
            // One window until Stop (the whole --minutes, no stall cut): a window per 30 s replayed to the stretch's start and
            // reloaded its archive each time, a good share of every half minute.
            for (int attempt = 0; !TimeUp; attempt++)
                SearchWindow("stretch", best, prefixLength, goal, new List<List<InputTick>> { stretchBest }, bestTick, _minutes, attempt);
            Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Stretch to goal {goal - 1}: {bestTick - startTick} ticks (the run's {runTick - startTick})");
            Emit(new { type = "done", best = RouteJson(_best) });
            return 0;
        }
    }
}
