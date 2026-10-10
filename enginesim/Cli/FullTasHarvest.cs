using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;

namespace IGTAP.EngineSim.Cli
{
    // The loop builder's harvest of saved routes into its piece library, and the goal state a replay starts from: what the
    // course Improve (CourseAuto.cs, solve --auto) still runs on. The Full-TAS economy planner they came with is gone (solve-run).
    sealed partial class FullTas
    {
        internal string RoutesFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "..", "routes.json");
        static readonly System.Reflection.FieldInfo BestPathTime = typeof(courseScript).GetField("bestPathTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);


        /// <summary>
        /// Files the best's own steps from goal from on (each goal's inputs from where the goal before left the player) as library
        /// pieces: the builder files only the pieces it builds, and a polished route passes spots none starts from (after the
        /// statue a quick restart lands at its checkpoint, and the lap from there crosses Course 3's gate where no finish piece began).
        /// Every goal is filed, area goals and rewards too: the search windows try them wherever another run stands where this one did.
        /// </summary>
        void Harvest(SimSnapshot at, List<InputTick> route, int from, int until)
        {
            int filed = _pieces.Count, length = route.Count;
            _holdPieces = true;
            try
            {
                for (int k = from; k < until; k++)
                {
                    int end = (int)Math.Min(_best.GoalInput[k], _best.Inputs.Count);
                    if (end > length)
                    {
                        var step = new Step { Inputs = _best.Inputs.GetRange(length, Math.Min(end + 2, _best.Inputs.Count) - length) };
                        Keep(step, at, _best.Inputs.GetRange(0, length), new Target { Goal = k, Key = _labels[k] });
                        if (_runGoals.Goals[k].kind == FullTasGoal.Record)
                        {
                            var (gate, finish) = LapParts(k, _runGoals.Goals[k].course);
                            KeepParts(step, at, _best.Inputs.GetRange(0, length), gate, finish);
                        }
                    }
                    // The goal after the last is never played to (a run's last goal ends it: no state after it).
                    if (k == until - 1) break;
                    SimSnapshot next = null;
                    int used = end;
                    for (int extra = 0; extra <= 2 && next?.Goal.Next != k + 1 && end + extra <= _best.Inputs.Count; extra++)
                    {
                        used = end + extra;
                        next = used == length ? at : AdvanceFrom(at, length, _best.Inputs.GetRange(length, used - length));
                    }
                    if (next == null || next.Goal.Next != k + 1) { Log($"harvest stops at goal {k + 1}: the best doesn't replay to it"); break; }
                    at = next;
                    length = used;
                }
            }
            finally
            {
                _holdPieces = false;
                SavePieces();
            }
            Log($"harvest: {_pieces.Count - filed} pieces from the best's goals {from + 1}-{until}");
        }

        /// <summary>
        /// --harvest FILE: a saved route (a fulltas/&lt;run&gt;.json: goals and inputs, for goals of this run's labels) played under
        /// its own goals from the run's start, its steps filed as library pieces (<see cref="Harvest"/>). A build or reach after a
        /// plan changed the goals then plays the old route's laps and box walks where the player stands as it did, instead of
        /// searching each goal again.
        /// </summary>
        void HarvestRoute(string file)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            string[] labels = doc.RootElement.GetProperty("goals").EnumerateArray().Select(LabelOf).ToArray();
            List<InputTick> inputs = Solve.ReadInputs(doc.RootElement);
            var byLabel = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in _runGoals.Goals) byLabel.TryAdd(g.label, g);
            string unknown = labels.FirstOrDefault(l => !byLabel.ContainsKey(l));
            if (unknown != null) { Log($"harvest {Path.GetFileName(file)}: goal {unknown} isn't one of the run's; skipped"); return; }
            FullTasTracker goals = _runGoals;
            string[] runLabels = _labels;
            int count = _goals;
            Route best = _best;
            try
            {
                _runGoals = goals.For(labels.Select(l => byLabel[l]).ToArray());
                _labels = labels;
                _goals = labels.Length;
                _eval.UseGoals(_runGoals);
                SimSnapshot start = _origin.WithGoal(Fresh(_goals, 0));
                _eval.SetStart(start, 0);
                _eval.Goals.Limit = _goals;
                RunResult r = _eval.Run(inputs, null, null, 1000000);
                _best = new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames };
                Log($"harvest {Path.GetFileName(file)}: plays {r.Progress} of its {labels.Length} goals");
                Harvest(start, new List<InputTick>(), 0, r.Progress);
            }
            finally
            {
                _runGoals = goals;
                _labels = runLabels;
                _goals = count;
                _best = best;
                _eval.UseGoals(_runGoals);
            }
        }

        /// <summary>
        /// Files every run's saved best (fulltas/&lt;run&gt;.json with its trace fulltas-&lt;run&gt;.json beside) in the library, each
        /// played from its own trace's start under the trace's goals (<see cref="Harvest"/>), so a run reuses another's way to a goal
        /// it shares (the same label: an area goal's label is its box) wherever it stands where the other did, with the same
        /// unlocks, without a "seeds" entry. A best is harvested again only when it, its trace's start or its trace's goals change
        /// (archive/fulltas-harvested.json).
        /// </summary>
        void HarvestRuns(string traceDir)
        {
            string bests = Path.Combine(traceDir, "fulltas");
            if (_fresh || !Directory.Exists(bests)) return;
            string indexFile = Path.Combine(traceDir, "archive", "fulltas-harvested.json");
            var done = File.Exists(indexFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(indexFile)) : new Dictionary<string, string>();
            bool changed = false;
            foreach (string runFile in Directory.GetFiles(bests, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string run = Path.GetFileNameWithoutExtension(runFile);
                string trace = Path.Combine(traceDir, "fulltas-" + run + ".json");
                // Only a run's own best has a trace of that name (not its .closest, .faster or .before-* copies).
                if (!File.Exists(trace) || !File.Exists(Path.ChangeExtension(trace, null) + ".world.json")) continue;
                string text = File.ReadAllText(runFile);
                JsonElement header = Solve.ReadHeader(trace);
                if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) continue;
                string[] labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
                string key = TextHash(text).ToString("x16") + "-" + StartKey(header).ToString("x16") + "-" + TextHash(string.Join("\n", labels)).ToString("x16");
                if (done.TryGetValue(run, out string was) && was == key) continue;
                HarvestRun(run, text, trace, header, labels);
                done[run] = key;
                changed = true;
            }
            if (!changed) return;
            Directory.CreateDirectory(Path.GetDirectoryName(indexFile));
            Solve.WriteShared(indexFile, JsonSerializer.Serialize(done));
        }

        static ulong TextHash(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text)) hash = Feed(hash, b, 1);
            return hash;
        }

        /// <summary>
        /// One run's saved best (its goals still the trace's leading ones) played in a simulation of its own trace and harvested. The
        /// game's statics are per thread and a world load sets its thread's, so the harvest loads and plays that world on a thread of
        /// its own: loaded here, it would leave the solve's own simulation running on the other world's statics.
        /// </summary>
        void HarvestRun(string run, string text, string trace, JsonElement header, string[] labels)
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            List<InputTick> inputs = Solve.ReadInputs(root);
            (string[] solved, long[] goalInput) = SolvedGoals(root);
            int keep = CurrentInputs(solved, goalInput, labels, inputs.Count);
            if (keep < inputs.Count) inputs.RemoveRange(keep, inputs.Count - keep);
            if (inputs.Count == 0) return;
            Simulation eval = _eval;
            FullTasTracker goals = _runGoals;
            string[] runLabels = _labels;
            int count = _goals;
            Route best = _best;
            try
            {
                Solve.OnThread(() =>
                {
                    Simulation sim = Solve.Creator(trace, header)();
                    sim.StrictBuys = true;
                    _eval = sim;
                    _runGoals = sim.Goals;
                    _labels = labels;
                    _goals = labels.Length;
                    SimSnapshot start = sim.Start;
                    sim.SetStart(start, 0);
                    sim.Goals.Limit = _goals;
                    RunResult r = sim.Run(inputs, null, null, 1000000);
                    _best = new Route { Inputs = inputs, Progress = r.Progress, GoalInput = r.GoalInputTicks, GoalPhysics = r.GoalPhysicsTicks, GoalHeld = r.GoalHeldFrames };
                    Log($"harvest run {run}: its best plays {r.Progress} of its {labels.Length} goals");
                    Harvest(start, new List<InputTick>(), 0, r.Progress);
                });
            }
            finally
            {
                _eval = eval;
                _runGoals = goals;
                _labels = runLabels;
                _goals = count;
                _best = best;
            }
        }

        static FullTasTracker.State Fresh(int count, int next)
        {
            var s = new FullTasTracker.State { Next = next, InputTicks = new long[count], PhysicsTicks = new long[count], HeldFrames = new long[count] };
            Array.Fill(s.InputTicks, -1L);
            Array.Fill(s.PhysicsTicks, -1L);
            Array.Fill(s.HeldFrames, -1L);
            return s;
        }
    }
}
