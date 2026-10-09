using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace IGTAP.EngineSim.Cli
{
    sealed partial class FullTas
    {
        /// <summary>
        /// fulltas &lt;trace&gt; --stretch N: one stretch of the run alone, goal N-1 to goal N (N from 1), searched window after window
        /// from the best's state at goal N-1 until Stop, each window seeded with the fastest way found so far. A way is judged by the
        /// tick it reaches goal N: the rest of the run isn't replayed or re-found (that is a whole run's Improve, which can take the
        /// stretch's best from its file). The best way so far is kept in fulltas/&lt;run&gt;.stretch-&lt;N&gt;.json with its arrival
        /// tick and the run's, and announced as a `stretch` event.
        /// </summary>
        int StretchSearch(int goal)
        {
            string from = Open();
            if (goal < 1 || goal > _best.Progress) throw new ArgumentException($"--stretch wants a goal the best reaches (1..{_best.Progress}), not {goal}");
            Route best = _best;
            int prefixLength = goal == 1 ? 0 : (int)Math.Min(best.GoalInput[goal - 2], best.Inputs.Count);
            int end = (int)Math.Min(best.GoalInput[goal - 1], best.Inputs.Count);
            long startTick = goal == 1 ? 0 : best.GoalPhysics[goal - 2], runTick = best.GoalPhysics[goal - 1];
            List<InputTick> prefix = best.Inputs.GetRange(0, prefixLength);
            string file = Path.Combine(Path.GetDirectoryName(_runFile), $"{_runKey}.stretch-{goal}.json");
            string prefixKey = string.Join(",", prefix.Select(t => t.GetHashCode())).GetHashCode().ToString("x8");
            List<InputTick> stretchBest = best.Inputs.GetRange(prefixLength, end - prefixLength);
            long bestTick = runTick;
            // A stretch best kept by an earlier session from the same prefix seeds this one.
            if (File.Exists(file))
            {
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = saved.RootElement;
                if (root.GetProperty("prefix").GetString() == prefixKey && root.GetProperty("bestTick").GetInt64() < bestTick)
                {
                    stretchBest = Solve.ReadInputs(root);
                    bestTick = root.GetProperty("bestTick").GetInt64();
                }
            }
            Console.WriteLine($"Full-TAS {_name}: stretch to goal {goal - 1} ({_labels[goal - 1]}) from goal {goal - 2}, {threadsText()}; "
                + $"the run's: {runTick - startTick} ticks; best so far: {bestTick - startTick} ticks (best from {from})");
            string threadsText() => _threads + " threads";
            Emit(new { type = "session", run = _name, goals = _labels, window = 1, threads = _threads, minutes = _minutes, windowMinutes = _windowMinutes,
                from, best = RouteJson(_best) });
            void Announce(bool improved) => Emit(new { type = "stretch", goal = goal - 1, label = _labels[goal - 1], runTicks = runTick - startTick,
                bestTicks = bestTick - startTick, improved });
            Announce(false);
            LoadSeeds();
            new Thread(WatchStdin) { IsBackground = true }.Start();
            for (int attempt = 0; !TimeUp; attempt++)
            {
                List<List<InputTick>> found = SearchWindow("stretch", best, prefixLength, goal, new List<List<InputTick>> { stretchBest }, bestTick, _windowMinutes, attempt);
                // The fastest few finds replayed from the start: their arrival at goal N decides.
                foreach (List<InputTick> f in found.Take(8))
                {
                    Route r = Evaluate(prefix.Concat(f).ToList());
                    if (r.Progress < goal || r.GoalPhysics[goal - 1] >= bestTick) continue;
                    bestTick = r.GoalPhysics[goal - 1];
                    stretchBest = f.Take((int)Math.Min(r.GoalInput[goal - 1], prefixLength + f.Count) - prefixLength).ToList();
                }
                if (bestTick < runTick && found.Count > 0)
                {
                    File.WriteAllText(file, JsonSerializer.Serialize(new { run = _name, goal = goal - 1, label = _labels[goal - 1], prefix = prefixKey,
                        startTick, runTick, bestTick, saved = (runTick - bestTick), inputs = stretchBest.Select(t => t.ToJson()) }));
                    Log($"stretch: goal {goal - 1} in {bestTick - startTick} ticks (the run's {runTick - startTick}): {runTick - bestTick} ticks faster -> {Path.GetFileName(file)}");
                }
                Announce(found.Count > 0);
            }
            Console.WriteLine($"[{_clock.Elapsed:hh\\:mm\\:ss}] Done. Stretch to goal {goal - 1}: {bestTick - startTick} ticks (the run's {runTick - startTick})");
            Emit(new { type = "done", best = RouteJson(_best) });
            return 0;
        }
    }
}
