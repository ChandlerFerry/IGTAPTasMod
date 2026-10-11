using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        FullTasOfflineFile ReadFullTasOffline(string run)
        {
            string path = Path.Combine(EngineSimDir, "fulltas", run + ".json");
            if (!File.Exists(path)) return null;
            try
            {
                return _fullTasOffline.Get(path, p =>
                {
                    JObject o = JObject.Parse(File.ReadAllText(p));
                    return new FullTasOfflineFile { goals = o["goals"].ToObject<List<FullTasSavedGoal>>(), inputs = o["inputs"].ToObject<List<SolverTick>>() };
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS offline best " + path + " unreadable: " + ex.Message);
                return null;
            }
        }

        FullTasOffline FullTasOfflineBest(string run, IList<string> labels)
        {
            FullTasOfflineFile file = ReadFullTasOffline(run);
            if (file == null) return null;
            var offline = new FullTasOffline { splits = new List<FullTasSplit>() };
            for (int i = 0; i < labels.Count && i < file.goals.Count && file.goals[i].physics >= 0 && file.goals[i].label == labels[i]; i++)
                offline.splits.Add(new FullTasSplit { id = file.goals[i].id, label = labels[i], ticks = file.goals[i].physics, held = Math.Max(0, file.goals[i].held) });
            offline.reached = offline.splits.Count;
            offline.time = offline.reached == 0 ? 0f : offline.splits[offline.reached - 1].Live;
            List<long> inputTicks = file.goals.Select(g => g.input).ToList();
            int keep = offline.reached == 0 ? 0 : (int)Math.Min(inputTicks[offline.reached - 1], file.inputs.Count);
            offline.inputs = file.inputs.GetRange(0, keep);
            offline.segments = ResetSegments(offline.inputs, inputTicks, offline.reached);
            offline.idles = file.goals.Take(offline.reached).Where(g => g.idle?.arrive?.Length == 2 && g.idle.afford?.Length == 2)
                .Select(g => new FullTasIdle { id = g.id, label = g.label, arrive = g.idle.arrive, afford = g.idle.afford }).ToList();
            offline.waits = new int[offline.reached];
            for (int g = 0; g < offline.reached; g++)
            {
                int from = g == 0 ? 0 : (int)Math.Min(inputTicks[g - 1], offline.inputs.Count), to = (int)Math.Min(inputTicks[g], offline.inputs.Count);
                for (int i = from, length = 0; i <= to; i++)
                {
                    SolverTick t = i < to ? offline.inputs[i] : null;
                    bool still = t != null && t.x == 0f && t.y == 0f && !t.held && !t.press && !t.release && !t.dash && !t.reset && t.dashJump == 0;
                    if (still) { length++; continue; }
                    if (length >= 25) offline.waits[g] += length;
                    length = 0;
                }
            }
            return offline;
        }

        /// <summary>enginesim FullTas.Segments: a segment ends at each quick restart after the first input, the last one at the route's end.</summary>
        static List<int[]> ResetSegments(List<SolverTick> inputs, List<long> goalInputTicks, int reached)
        {
            var segments = new List<int[]>();
            int Goals(int count)
            {
                int n = 0;
                while (n < reached && goalInputTicks[n] <= count) n++;
                return n;
            }
            int start = 0;
            for (int i = 1; i < inputs.Count; i++)
            {
                if (!inputs[i].reset) continue;
                segments.Add(new[] { Goals(start), Goals(i), i - start, start });
                start = i + 1;
            }
            segments.Add(new[] { Goals(start), reached, inputs.Count - start, start });
            return segments;
        }

        /// <summary>The solver's reach attempt furthest toward the goals (`fulltas/&lt;run&gt;.closest.json`), if it was searched for these goals.</summary>
        FullTasClosest FullTasClosestAttempt(string run, IList<string> labels)
        {
            string path = Path.Combine(EngineSimDir, "fulltas", run + ".closest.json");
            if (!File.Exists(path)) return null;
            FullTasClosest closest;
            try
            {
                closest = _fullTasClosest.Get(path, p =>
                {
                    JObject o = JObject.Parse(File.ReadAllText(p));
                    return new FullTasClosest { goals = o["goals"].Select(g => (string)g["goal"]).ToList(), reached = (int)o["reached"],
                        next = (string)o["next"], distance = (float?)o["distance"], inputs = o["inputs"].ToObject<List<SolverTick>>() };
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS closest attempt " + path + " unreadable: " + ex.Message);
                return null;
            }
            return closest.inputs.Count > 0 && closest.goals.SequenceEqual(labels) ? closest : null;
        }

        /// <summary>
        /// Solve: `solve-run` builds what the run's best doesn't reach yet and improves the whole run until it converges or Stop
        /// (docs/superpowers/specs/2026-10-09-fulltas-v2-design.md); `stretch` (a goal's id) searches that goal's way again
        /// (`--segment`), `class` (README ### Rules 54) solves for that class. It leaves the game 2 cores by default.
        /// </summary>
        void LaunchFullTasSolve(FullTasRunDef run, string trace, JObject body)
        {
            int threads = Number(body, "threads", 0);
            if (threads <= 0) threads = Math.Max(1, Environment.ProcessorCount - 2);
            string cls = FullTasClassOf(Text(body, "class"));
            var solve = new StringBuilder("solve-run ").Append(Quote(trace)).Append(" --threads ").Append(threads);
            if (cls != MajorClass) solve.Append(" --class ").Append(cls);
            string goalId = Text(body, "stretch");
            if (goalId.Length > 0) solve.Append(" --segment ").Append(goalId);
            StartFullTasProcess(run, solve.ToString(), goalId.Length > 0
                ? "Searching " + run.name + "'s goal " + run.goals[FullTasGoalAt(run, goalId)].goal + " again on " + threads + " threads"
                : "Solving " + run.name + (cls == MajorClass ? "" : " (" + cls + ")") + " on " + threads + " threads", "until Stop");
        }

        void StartFullTasProcess(FullTasRunDef run, string args, string what, string limit)
        {
            while (_fullTasOutput.TryDequeue(out _)) { }
            _fullTasProcess = StartEngineSim(args, _fullTasOutput, stdin: true);
            _fullTasKillAt = null;
            _fullTasSolveRun = run.name;
            _fullTasEvents.Clear();
            _fullTasEventsVersion++;
            _fullTasProgress = null;
            _fullTasOutlook = null;
            _fullTasPulls.Clear();
            _fullTasSolveError = null;
            _fullTasCommand = EngineSimExe + " " + args;
            _fullTasSolveStatus = what;
            FullTasLog(what + " (" + limit + ")");
        }

        /// <summary>The offline best of every run in <c>run.seeds</c> (<c>enginesim/fulltas/&lt;seed&gt;.json</c>), once from each goal it reaches:
        /// its inputs after goal k with its goals from k+1 on, so a window of this run reaching one of them can reuse the rest.</summary>
        void WriteFullTasRunSeeds(FullTasRunDef run, HashSet<string> labels, List<(bool stale, float time, string key, string json)> found)
        {
            foreach (string source in run.seeds ?? new List<string>())
            {
                string file = Path.Combine(EngineSimDir, "fulltas", source + ".json");
                if (!File.Exists(file)) { FullTasLog("Seed run " + source + " has no offline best (" + file + ")"); continue; }
                JObject saved = JObject.Parse(File.ReadAllText(file));
                var goals = saved["goals"].Select(g => (string)g["label"]).ToList();
                var goalInput = saved["goals"].Select(g => (long)g["input"]).ToList();
                var inputs = (JArray)saved["inputs"];
                int reached = goalInput.TakeWhile(t => t >= 0).Count();
                for (int k = 0; k < reached; k++)
                {
                    List<string> rest = goals.GetRange(k, reached - k).Where(labels.Contains).ToList();
                    if (rest.Count == 0) continue;
                    int from = k == 0 ? 0 : (int)goalInput[k - 1];
                    var ticks = new JArray(inputs.Skip(from).Take((int)goalInput[reached - 1] - from));
                    string name = source + (k == 0 ? " from its start" : " after goal " + k);
                    var json = new JObject { ["name"] = name, ["goals"] = new JArray(rest), ["capabilities"] = "none",
                        ["standing"] = false, ["restart"] = false, ["ticks"] = ticks };
                    // From its start, the seed's player stood still where the run's trace starts: the solve walks there first.
                    if (k == 0 && File.Exists(FullTasTracePath(source))) json["trace"] = FullTasTracePath(source);
                    found.Add((false, 0f, "run|" + name, json.ToString(Formatting.None)));
                }
            }
        }

        object StartFullTasSolve(WebStudioRequest request, JObject body)
        {
            if (_fullTasProcess != null) throw new InvalidOperationException("A Full-TAS solve is already running (" + _fullTasSolveRun + ").");
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new InvalidOperationException("The offline solver is not built (" + exe + "): dotnet build enginesim\\Cli\\IGTAP.EngineSim.csproj -c Release");
            FullTasRunDef run = FindFullTasRun(Text(body, "name"));
            ResolveFullTasGoals(run);
            FullTasClassOf(Text(body, "class"));
            string trace = FullTasTracePath(run.name);
            bool current = FullTasTraceCurrent(run, trace);
            var options = (JObject)body.DeepClone();
            Func<object> launch = () =>
            {
                if (_fullTasProcess != null) throw new InvalidOperationException("A Full-TAS solve is already running (" + _fullTasSolveRun + ").");
                bool captured = false;
                try
                {
                    if (!current)
                    {
                        FullTasTrace(run, FullTasInputs(run, new JObject()), "fulltas-" + run.name, true);
                        captured = true;
                    }
                    LaunchFullTasSolve(run, trace, options);
                }
                catch (Exception ex)
                {
                    _fullTasSolveStatus = "Not started: " + ex.Message;
                    _fullTasSolveError = "The solve didn't start: " + ex.Message;
                    throw;
                }
                return new { ok = true, trace = trace, captured = captured, status = _fullTasSolveStatus };
            };
            if (current) return launch();
            _fullTasSolveRun = run.name;
            _fullTasSolveStatus = "Capturing the trace of " + run.name;
            try { return FullTasWhenReady(request, run, _fullTasSolveStatus, launch); }
            catch (Exception ex)
            {
                _fullTasSolveStatus = "Not started: " + ex.Message;
                _fullTasSolveError = "The solve didn't start: " + ex.Message;
                throw;
            }
        }

        /// <summary>
        /// Asks the solver to stop ("stop" on its stdin): it ends its window as its time limit would, writing the archive and the
        /// explored map, and exits. Killed if it hasn't within 30 s.
        /// </summary>
        void StopFullTasSolve()
        {
            if (_fullTasProcess == null || _fullTasKillAt != null) return;
            try
            {
                _fullTasProcess.StandardInput.WriteLine("stop");
                _fullTasProcess.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not ask the Full-TAS solve to stop: " + ex.Message);
                KillFullTasSolve("Stopped");
                return;
            }
            _fullTasKillAt = DateTime.UtcNow.AddSeconds(30);
            _fullTasSolveStatus = "Stopping: saving the search";
            FullTasLog(_fullTasSolveStatus);
        }

        void KillFullTasSolve(string status, string error = null)
        {
            if (_fullTasProcess == null) return;
            KillEngineSim(ref _fullTasProcess, "Full-TAS solve");
            while (_fullTasOutput.TryDequeue(out string line)) FullTasSolveLine(line);
            _fullTasSolveStatus = status;
            if (error != null) _fullTasSolveError = error;
            FullTasLog(status);
        }

        void FullTasSolveLine(string line)
        {
            if (line.StartsWith("@fulltas ", StringComparison.Ordinal))
            {
                JObject e = null;
                try { e = JObject.Parse(line.Substring("@fulltas ".Length)); }
                catch (JsonException) { }
                if (e != null)
                {
                    e["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string type = (string)e["type"];
                    if (type == "status") { _fullTasProgress = e; return; }
                    if (type == "outlook") { _fullTasOutlook = e; return; }
                    if (type == "pullStart" || type == "pull") _fullTasPulls.RemoveAll(p => (long?)p["pull"] == (long?)e["pull"]);
                    if (type == "pullStart") { _fullTasPulls.Add(e); return; }
                    if (FullTasDiskOnlyEvents.Contains(type) || type == "record" && e["lever"] != null) return;
                    _fullTasEvents.Add(e);
                    if (_fullTasEvents.Count > MaxFullTasEvents)
                    {
                        int drop = _fullTasEvents.FindIndex(1, x => !FullTasKeptEvents.Contains((string)x["type"]));
                        _fullTasEvents.RemoveAt(drop < 0 ? 1 : drop);
                    }
                    _fullTasEventsVersion++;
                    return;
                }
            }
            _fullTasLog.Add(line);
            if (_fullTasLog.Count > MaxFullTasLog) _fullTasLog.RemoveRange(0, _fullTasLog.Count - MaxFullTasLog);
            _fullTasLogVersion++;
            Logger.LogInfo("fulltas: " + line);
        }

        void PumpFullTasSolve()
        {
            if (_fullTasProcess == null) return;
            try
            {
                while (_fullTasOutput.TryDequeue(out string line)) FullTasSolveLine(line);
                if (!_fullTasProcess.HasExited)
                {
                    if (_fullTasKillAt != null && DateTime.UtcNow >= _fullTasKillAt) KillFullTasSolve("Stopped (killed: it didn't exit within 30 s of the stop)");
                    return;
                }
                _fullTasProcess.WaitForExit();
                // WaitForExit waits for the last stdout and stderr lines (a crash's exception comes last): read them too.
                while (_fullTasOutput.TryDequeue(out string last)) FullTasSolveLine(last);
                int code = _fullTasProcess.ExitCode;
                if (code == 0) KillFullTasSolve(_fullTasKillAt != null ? "Stopped" : "Finished");
                else KillFullTasSolve("The offline solver exited with code " + code, "The offline solver crashed (exit code " + code + "). The log below has its last words.");
            }
            catch (Exception ex) { Logger.LogError(ex); KillFullTasSolve("Solve error: " + ex.Message, "The plugin lost track of the solve: " + ex.Message); }
        }

        string FullTasSolvingGroup { get { return _fullTasProcess == null ? null : "fulltas-" + _fullTasSolveRun; } }

        /// <summary>
        /// A box drawn on a Full-TAS run's map, into the run definitions (runs/&lt;run&gt;.json; the engine's FullTasPaths reads them):
        /// with <paramref name="goal"/> (a goal's id) that goal's "avoid", cut only while the run heads for it; with
        /// <paramref name="after"/> its "avoidAfter", cut once the run has reached it; else each run's run-wide "avoid".
        /// <paramref name="from"/> ("left+bottom", ...) makes it one-way: it cuts only a move into it across those sides. A box joins
        /// the block of its sides there (made when none is). Remove takes it out of every block of the runs, dropping emptied ones.
        /// </summary>
        int EditFullTasAvoids(List<FullTasRunDef> runs, float[] b, bool remove, string goal = null, string after = null, string from = null)
        {
            float[] wanted = NormalizedBox(b);
            bool Same(float[] box) => box.Length == 4 && Enumerable.Range(0, 4).All(i => Math.Abs(box[i] - wanted[i]) < .5f);
            int changed = 0;
            if (remove)
            {
                foreach (FullTasRunDef run in runs)
                {
                    int was = changed;
                    List<FullTasAvoidBlock> Clean(List<FullTasAvoidBlock> blocks)
                    {
                        if (blocks == null) return null;
                        foreach (FullTasAvoidBlock block in blocks) changed += block.boxes.RemoveAll(Same);
                        blocks.RemoveAll(block => block.boxes.Count == 0);
                        return blocks.Count == 0 ? null : blocks;
                    }
                    run.avoid = Clean(run.avoid);
                    foreach (FullTasRunGoal g in run.goals) { g.avoid = Clean(g.avoid); g.avoidAfter = Clean(g.avoidAfter); }
                    if (changed > was) SaveFullTasRun(run);
                }
            }
            else
            {
                foreach (FullTasRunDef run in goal != null || after != null ? runs.Take(1) : runs)
                {
                    FullTasRunGoal on = (goal ?? after) is string id ? run.goals[FullTasGoalAt(run, id)] : null;
                    List<FullTasAvoidBlock> blocks = on == null ? run.avoid ?? (run.avoid = new List<FullTasAvoidBlock>())
                        : goal != null ? on.avoid ?? (on.avoid = new List<FullTasAvoidBlock>()) : on.avoidAfter ?? (on.avoidAfter = new List<FullTasAvoidBlock>());
                    FullTasAvoidBlock block = blocks.FirstOrDefault(x => x.from == from && x.offFor == null);
                    if (block == null) blocks.Add(block = new FullTasAvoidBlock { from = from, note = "Drawn on the Full-TAS map." });
                    if (block.boxes.Any(Same)) continue;
                    block.boxes.Add(wanted);
                    changed++;
                    SaveFullTasRun(run);
                }
            }
            _fullTasAvoids.Clear();
            return changed;
        }

        /// <summary>A run's run-wide avoid boxes (the overlay draws them), from its definition.</summary>
        List<float[]> FullTasAvoids(string run)
        {
            if (_fullTasAvoids.TryGetValue(run, out List<float[]> boxes)) return boxes;
            FullTasRunDef def = _fullTasRuns?.Find(r => string.Equals(r.name, run, StringComparison.OrdinalIgnoreCase));
            boxes = def?.avoid == null ? NoAvoids : def.avoid.SelectMany(block => block.boxes).Where(box => box.Length == 4).ToList();
            _fullTasAvoids[run] = boxes;
            return boxes;
        }
    }
}
