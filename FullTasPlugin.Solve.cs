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
                    return new FullTasOfflineFile { goals = o["goals"].ToObject<List<string>>(), ticks = o["goalPhysicsTicks"].ToObject<List<long>>(),
                        held = o["goalHeldFrames"]?.ToObject<List<long>>(), inputTicks = o["goalInputTicks"].ToObject<List<long>>(), inputs = o["inputs"].ToObject<List<SolverTick>>(),
                        idles = o["idles"]?.Type == JTokenType.Array ? o["idles"].ToObject<List<FullTasIdle>>() : null };
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
            for (int i = 0; i < labels.Count && i < file.goals.Count && i < file.ticks.Count && file.ticks[i] >= 0 && file.goals[i] == labels[i]; i++)
                offline.splits.Add(new FullTasSplit { label = labels[i], ticks = file.ticks[i], held = file.held != null && i < file.held.Count ? Math.Max(0, file.held[i]) : 0 });
            offline.reached = offline.splits.Count;
            offline.time = offline.reached == 0 ? 0f : offline.splits[offline.reached - 1].Live;
            int keep = offline.reached == 0 ? 0 : (int)Math.Min(file.inputTicks[offline.reached - 1], file.inputs.Count);
            offline.inputs = file.inputs.GetRange(0, keep);
            offline.segments = ResetSegments(offline.inputs, file.inputTicks, offline.reached);
            offline.idles = file.idles?.Where(i => i.goal >= 1 && i.goal <= offline.reached && i.arrive?.Length == 2 && i.afford?.Length == 2).ToList();
            offline.waits = new int[offline.reached];
            for (int g = 0; g < offline.reached; g++)
            {
                int from = g == 0 ? 0 : (int)Math.Min(file.inputTicks[g - 1], offline.inputs.Count), to = (int)Math.Min(file.inputTicks[g], offline.inputs.Count);
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
                    return new FullTasClosest { goals = o["goals"].ToObject<List<string>>(), reached = (int)o["reached"],
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
        /// Improve (the default): `fulltas --auto` from the run's best, with the saved record as a --ref (the engine's run file can
        /// trail it) and `--focus a-b` when goals are picked. `unsolved: true`: the old reach search (`--unsolved`, with the seed
        /// routes) for the goals a best misses (--auto improves the goals it reaches). Either leaves the game 2 cores by default, as course Optimize does.
        /// `class` (README ### Rules 54) solves for that class (`--class`), its own best the --ref.
        /// </summary>
        void LaunchFullTasSolve(FullTasRunDef run, string trace, JObject body)
        {
            var args = new StringBuilder("fulltas ").Append(Quote(trace));
            int threads = Number(body, "threads", 0);
            if (threads <= 0) threads = Math.Max(1, Environment.ProcessorCount - 2);
            args.Append(" --threads ").Append(threads);
            string cls = FullTasClassOf(Text(body, "class"));
            if (cls != MajorClass) args.Append(" --class ").Append(cls);
            bool unsolved = Truthy(body, "unsolved");
            // Improve runs until Stop (a day's worth of minutes); the reach search keeps its hour.
            float minutes = FloatNumber(body, "minutes", unsolved ? 60f : 24 * 60f);
            args.Append(" --minutes ").Append(minutes.ToString(CultureInfo.InvariantCulture));
            string what, of = run.name + (cls == MajorClass ? "" : " (" + cls + ")");
            // "stretch": one stretch alone (its goal number from 1), judged at its goal (the engine's --stretch, FullTasStretch.cs).
            int stretch = Number(body, "stretch", 0);
            if (stretch > 0)
            {
                if (stretch > run.goals.Count) throw new InvalidOperationException("stretch: a goal within 1-" + run.goals.Count + ".");
                args.Append(" --window-minutes 0.5 --stretch ").Append(stretch);
                what = "Optimizing " + of + "'s stretch to goal " + (stretch - 1) + " (" + run.goals[stretch - 1] + ") on " + threads + " threads";
            }
            else if (unsolved)
            {
                args.Append(" --unsolved");
                int seeds = WriteFullTasSeeds(run, args);
                what = "Reaching the missing goals of " + of + " on " + threads + " threads from " + seeds + " seed routes";
            }
            else
            {
                int[] focus = body["focus"] is JArray range && range.Count == 2 ? new[] { (int)range[0], (int)range[1] } : null;
                if (focus != null && (focus[0] < 1 || focus[1] < focus[0] || focus[1] > run.goals.Count))
                    throw new InvalidOperationException("focus: a goal range [first, last] within 1-" + run.goals.Count + ".");
                MacroDefinition record = FullTasClassBest(run.name, cls, out _);
                // --auto improves the goals the offline best reaches (all of them, or those of a best that misses goals): picked
                // goals must be among them.
                int reached = FullTasOfflineBest(run.name + (cls == MajorClass ? "" : "." + cls), run.goals)?.reached ?? 0;
                if (focus != null && record == null && focus[1] > reached)
                    throw new InvalidOperationException("focus: the offline best reaches goal " + (reached - 1) + " of " + (run.goals.Count - 1) + "; pick goals up to it, or Reach missing goals first.");
                args.Append(" --auto --window-minutes 0.5");
                if (focus != null) args.Append(" --focus ").Append(focus[0]).Append('-').Append(focus[1]);
                if (record != null)
                {
                    string file = Path.Combine(EngineSimDir, "fulltas-" + run.name + (cls == MajorClass ? "" : "-" + cls) + "-record.json");
                    File.WriteAllText(file, JsonConvert.SerializeObject(new { name = record.name, ticks = ToTicks(record.steps) }));
                    args.Append(" --ref ").Append(Quote(file));
                }
                what = "Improving " + of + (focus == null ? "" : " goals " + (focus[0] - 1) + "-" + (focus[1] - 1))
                    + (record == null && reached < run.goals.Count ? " (the best reaches " + reached + " of " + run.goals.Count + " goals)" : "") + " on " + threads + " threads";
            }
            while (_fullTasOutput.TryDequeue(out _)) { }
            _fullTasProcess = StartEngineSim(args.ToString(), _fullTasOutput, stdin: true);
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
            FullTasLog(what + " (" + minutes.ToString(CultureInfo.InvariantCulture) + " min at most)");
        }

        int WriteFullTasSeeds(FullTasRunDef run, StringBuilder args)
        {
            Directory.CreateDirectory(EngineSimDir);
            string prefix = "fulltas-" + run.name + "-seed-";
            var numbered = new Regex("^" + Regex.Escape(prefix) + @"\d+\.json$", RegexOptions.IgnoreCase);
            foreach (string old in Directory.GetFiles(EngineSimDir, prefix + "*.json"))
                if (numbered.IsMatch(Path.GetFileName(old))) File.Delete(old);
            var labels = new HashSet<string>(ResolveFullTasGoals(run).Select(g => g.label));
            var found = new List<(bool stale, float time, string key, string json)>();
            foreach (MacroDefinition route in _library.macros)
            {
                Match m = route.name == null ? Match.Empty : FullTasSeedRoute.Match(route.name);
                if (!m.Success || !route.steps.All(s => s.type == MacroStepType.RawInput)) continue;
                string token = m.Groups[1].Value.ToLowerInvariant();
                CourseDef target = null;
                int course = 0;
                var goals = new List<string>();
                Match transition = FullTasSeedTransition.Match(token);
                if (transition.Success)
                {
                    string to = transition.Groups[2].Value.ToLowerInvariant();
                    if (to.StartsWith("bonus", StringComparison.Ordinal)) goals.Add("reward:" + to);
                    else { goals.Add("start:" + to); course = int.Parse(to, CultureInfo.InvariantCulture); }
                }
                else
                {
                    target = _courses.Find(c => !c.IsTransition && string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;
                    if (target.IsBonus) goals.Add("reward:" + token);
                    else { goals.Add("start:" + target.id); goals.Add("finish:" + target.id); course = target.id; }
                }
                // Every goal stays whether or not the run's goals have it: the economy planner laps any course it can reach, and --auto's
                // place lever splices any category route in where the run passes (Zip% passes bonus 11's way through the atom room).
                bool stale = route.name.EndsWith(" stale", StringComparison.Ordinal);
                string plain = stale ? route.name.Substring(0, route.name.Length - " stale".Length) : route.name;
                Match category = MatchGatedRoute(plain);
                string caps = string.IsNullOrEmpty(target?.minCapabilities) ? default(Caps).Id : target.minCapabilities;
                float time = float.MaxValue;
                if (category.Success)
                {
                    caps = CapsOf(category.Groups["label"].Value).Id;
                    if (!float.TryParse(category.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out time)) time = float.MaxValue;
                }
                bool standing = target != null, restart = false;
                List<SolverTick> ticks = ToTicks(route.steps);
                string json = JsonConvert.SerializeObject(new { name = route.name, token, course, goals, capabilities = caps, standing, restart, ticks });
                found.Add((stale, time, token + "|" + caps + "|" + standing + "|" + restart + "|" + JsonConvert.SerializeObject(ticks), json));
                // A course route's laps from its quick restart (a farm route's lead restarts onto the course): a run's quick restart
                // lands as the route's did, where the standing start (the category start, at its own origin) doesn't.
                int reset = course > 0 ? ticks.FindIndex(t => t.reset) : -1;
                if (reset > 0)
                {
                    List<SolverTick> laps = ticks.GetRange(reset, ticks.Count - reset);
                    json = JsonConvert.SerializeObject(new { name = route.name + " from its quick restart", token, course, goals, capabilities = caps,
                        standing = false, restart = true, ticks = laps });
                    found.Add((stale, time, token + "|" + caps + "|False|True|" + JsonConvert.SerializeObject(laps), json));
                }
            }
            WriteFullTasRunSeeds(run, labels, found);
            var written = new HashSet<string>();
            var files = new List<string>();
            foreach (var seed in found.OrderBy(s => s.stale).ThenBy(s => s.time))
            {
                if (!written.Add(seed.key)) continue;
                string file = Path.Combine(EngineSimDir, prefix + files.Count + ".json");
                File.WriteAllText(file, seed.json);
                files.Add(file);
            }
            // One path per line: a run's seeds (every goal of each seed run) can pass Windows' 32767-character command line.
            string list = Path.Combine(EngineSimDir, "fulltas-" + run.name + "-seeds.txt");
            File.WriteAllLines(list, files);
            args.Append(" --seed-list ").Append(Quote(list));
            return files.Count;
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
                var goals = saved["goals"].Select(g => (string)g).ToList();
                var goalInput = saved["goalInputTicks"].Select(t => (long)t).ToList();
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

        /// <summary>A box drawn on a Full-TAS run's map: under the rule with `"fulltas": [runs]` those runs share
        /// (created when none is drawn yet), so every window of their solves that enters it is cut. With a goal label, it goes into
        /// that rule's optional block "for" the goal: it cuts only while a run heads for that goal; with <paramref name="after"/>
        /// (a goal label), the block "after" it: it cuts once a run has reached that goal. <paramref name="from"/> ("left+bottom",
        /// ...) makes it one-way, the block's "from": it cuts only a move into it across those sides. Remove takes it out of all.</summary>
        int EditFullTasAvoids(string[] runs, float[] b, bool remove, string goal = null, string after = null, string from = null, int? only = null)
        {
            float[] wanted = NormalizedBox(b);
            Func<JToken, bool> same = SameBox(wanted);
            return EditForcedPaths(paths =>
            {
                List<JObject> rules = paths.OfType<JObject>().Where(r => r["fulltas"] is JArray).ToList();
                int changed = 0;
                if (remove)
                {
                    foreach (JObject rule in rules.Where(r => r["fulltas"].Any(n => runs.Contains((string)n))))
                    {
                        if (rule["avoid"] is JArray avoid)
                            foreach (JToken hit in avoid.Where(same).ToList()) { hit.Remove(); changed++; }
                        if (rule["optional"] is JArray optional)
                            foreach (JObject block in optional.OfType<JObject>().Where(o => o["avoid"] is JArray).ToList())
                            {
                                var boxes = (JArray)block["avoid"];
                                foreach (JToken hit in boxes.Where(same).ToList()) { hit.Remove(); changed++; }
                                if (boxes.Count == 0) block.Remove();
                            }
                        bool empty(string key) => !(rule[key] is JArray list && list.Count > 0);
                        if (empty("avoid") && empty("via") && empty("optional")) rule.Remove();
                    }
                }
                else
                {
                    JObject rule = rules.FirstOrDefault(r => (bool?)r["drawn"] == true
                        && r["fulltas"].Select(n => (string)n).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(runs));
                    if (rule == null)
                    {
                        rule = new JObject { ["fulltas"] = new JArray(runs), ["note"] = "Drawn on the Full-TAS map.", ["drawn"] = true, ["avoid"] = new JArray() };
                        paths.Add(rule);
                    }
                    var avoid = (JArray)rule["avoid"];
                    if (goal != null || after != null || from != null || only != null)
                    {
                        if (!(rule["optional"] is JArray optional)) rule["optional"] = optional = new JArray();
                        JObject block = optional.OfType<JObject>().FirstOrDefault(o => (string)o["for"] == goal && (string)o["after"] == after
                            && (string)o["from"] == from && (int?)o["forGoal"] == only && o["offFor"] == null && o["avoid"] is JArray);
                        if (block == null)
                        {
                            string name = (only != null ? "goal " + only : goal != null ? "for " + goal : after != null ? "after " + after : "always") + (from != null ? " from " + from : "");
                            block = new JObject { ["name"] = name, ["note"] = "Drawn on the Full-TAS map: cuts "
                                + (only != null ? "only while the run heads for goal " + only : goal != null ? "only while a run heads for " + goal
                                    : after != null ? "once a run has reached " + after : "for every goal")
                                + (from != null ? ", only a move into it across its " + from + " side" : "") + "." };
                            if (only != null) block["forGoal"] = only;
                            if (goal != null) block["for"] = goal;
                            if (after != null) block["after"] = after;
                            if (from != null) block["from"] = from;
                            block["avoid"] = new JArray();
                            optional.Add(block);
                        }
                        avoid = (JArray)block["avoid"];
                    }
                    if (!avoid.Any(same)) { avoid.Add(new JArray(wanted)); changed++; }
                }
                return changed;
            });
        }

        List<float[]> FullTasAvoids(string run)
        {
            JObject root = ForcedPaths();
            if (root != _fullTasAvoidsOf)
            {
                _fullTasAvoidsOf = root;
                _fullTasAvoids.Clear();
                if (root != null)
                    foreach (JObject rule in ((JArray)root["paths"]).OfType<JObject>())
                        if (rule["fulltas"] is JArray runs && rule["avoid"] is JArray avoid)
                            foreach (string name in runs.Select(r => (string)r))
                            {
                                if (!_fullTasAvoids.TryGetValue(name, out List<float[]> list)) _fullTasAvoids[name] = list = new List<float[]>();
                                list.AddRange(avoid.OfType<JArray>().Where(b => b.Count == 4).Select(b => b.Select(v => (float)v).ToArray()));
                            }
            }
            return _fullTasAvoids.TryGetValue(run, out List<float[]> boxes) ? boxes : NoAvoids;
        }
    }
}
