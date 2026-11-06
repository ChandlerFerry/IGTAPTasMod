using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        // Engine vs gate live time tolerance: under a held frame (1/280 s), above the JSON rounding.
        const float FullTasTechsLiveSlack = .0005f;
        const int FullTasTechsTimeoutMs = 300000;

        sealed class FullTasCandidate
        {
            public string cls;
            public float? score, time;
            public int major, nmg, graceTicks, reached;
            public List<FullTasSpan> spans;
            public List<string> goals;
            public FullTasDifficulty difficulty;
        }
        readonly CachedFiles<FullTasCandidate> _fullTasCandidates = new CachedFiles<FullTasCandidate>(StringComparer.OrdinalIgnoreCase);
        readonly CachedFiles<JObject> _fullTasDifficulties = new CachedFiles<JObject>(StringComparer.OrdinalIgnoreCase);

        static bool FullTasRouteOf(string name, out string run, out string cls, out float time)
        {
            Match m = FullTasClassRoute.Match(name ?? "");
            run = m.Success ? m.Groups["run"].Value : null;
            cls = !m.Success ? null : m.Groups["class"].Success ? m.Groups["class"].Value.ToLowerInvariant() : MajorClass;
            time = m.Success && float.TryParse(m.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ? t : 0f;
            return m.Success;
        }

        MacroDefinition FullTasClassBest(string run, string cls, out float rank)
        {
            rank = float.PositiveInfinity;
            MacroDefinition best = null;
            foreach (MacroDefinition m in _library.macros)
            {
                if (!FullTasRouteOf(m.name, out string r, out string c, out float time) || c != cls || !string.Equals(r, run, StringComparison.OrdinalIgnoreCase)) continue;
                float value = HumanClass(cls) && _fullTasClasses.TryGetValue(m.id, out FullTasClassRecord record) ? record.score
                    : !HumanClass(cls) && _fullTasSplits.TryGetValue(m.id, out List<FullTasSplit> splits) && splits.Count > 0 ? Math.Min(time, splits[splits.Count - 1].Live)
                    : time;
                if (value < rank) { best = m; rank = value; }
            }
            return best;
        }

        void PruneFullTasClass(string run, string cls, MacroDefinition kept)
        {
            int removed = _library.macros.RemoveAll(m => m != kept && FullTasRouteOf(m.name, out string r, out string c, out _) && c == cls
                && string.Equals(r, run, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;
            _library.selectedIndex = Math.Max(0, Math.Min(_library.selectedIndex, _library.macros.Count - 1));
            SaveLibrary();
        }

        void StoreFullTasClass(MacroDefinition route, FullTasClassRecord record)
        {
            _fullTasClasses[route.id] = record;
            foreach (string id in _fullTasClasses.Keys.Where(k => !_library.macros.Any(m => m.id == k)).ToList()) _fullTasClasses.Remove(id);
            WriteJsonAtomic(FullTasClassesPath, _fullTasClasses, false);
        }

        void LoadFullTasClasses()
        {
            try
            {
                if (File.Exists(FullTasClassesPath))
                    _fullTasClasses = JsonConvert.DeserializeObject<Dictionary<string, FullTasClassRecord>>(File.ReadAllText(FullTasClassesPath))
                        ?? new Dictionary<string, FullTasClassRecord>();
            }
            catch (Exception ex) { Logger.LogWarning("fulltas-classes.json rejected: " + ex.Message); }
        }

        string FullTasClassViolation(FullTasClassJudge judge, IList<string> goals, List<SolverTick> inputs, float time)
        {
            FullTasHumanWeights weights = judge.run.Human;
            JObject used = FullTasTechsUsed(judge.run, goals, inputs, HumanClass(judge.cls), out string error);
            FullTasClassRecord record = used == null ? null : FullTasClassRecordOf(used, judge.cls, time, weights, out error);
            if (record == null)
            {
                error = "the engine's tech checks (techs-used) " + error;
                if (judge.cls != MajorClass) return error;
                FullTasLog("Full-TAS " + judge.run.name + ": the major route has no class result: " + error);
                return null;
            }
            judge.record = record;
            if (NoMajorClass(judge.cls) && record.major > 0)
            {
                FullTasSpan span = record.spans.FirstOrDefault(s => s.category == MajorClass);
                return "uses " + (span == null ? record.major + " major tech spans" : span.tech + " (major) at tick " + span.tick
                    + (record.major > 1 ? " (" + record.major + " major spans)" : ""));
            }
            if (HumanClass(judge.cls) && !(record.score < judge.best - .0001f))
                return "score " + FullTasScoreText(record, weights) + " not lower than " + FullTasClassCategory(judge.run.name, judge.cls) + "'s "
                    + judge.best.ToString("0.00", CultureInfo.InvariantCulture);
            return null;
        }

        static FullTasClassRecord FullTasClassRecordOf(JObject used, string cls, float time, FullTasHumanWeights weights, out string error)
        {
            error = null;
            if ((bool?)used["finished"] != true)
            {
                error = "replay reached " + (int?)used["progress"] + " of " + (int?)used["goals"] + " goals";
                return null;
            }
            float live = (float?)used["live"] ?? float.NaN;
            if (!(Math.Abs(live - time) <= FullTasTechsLiveSlack))
            {
                error = "replay took " + live.ToString("0.0000", CultureInfo.InvariantCulture) + " s live, not " + time.ToString("0.0000", CultureInfo.InvariantCulture);
                return null;
            }
            FullTasDifficulty difficulty = (used["difficulty"] as JObject)?.ToObject<FullTasDifficulty>();
            if (difficulty == null && HumanClass(cls) && weights.PricesDifficulty())
            {
                error = "measured no difficulty (an engine from before the difficulty counts?)";
                return null;
            }
            var record = new FullTasClassRecord { cls = cls, live = time, major = (int?)used["major"] ?? 0, nmg = (int?)used["nmg"] ?? 0,
                graceTicks = (int?)used["graceTicks"] ?? 0, spans = used["spans"]?.ToObject<List<FullTasSpan>>() ?? new List<FullTasSpan>(), difficulty = difficulty };
            record.score = FullTasScoreOf(record, weights);
            return record;
        }

        static float FullTasScoreOf(FullTasClassRecord r, FullTasHumanWeights w)
        {
            return r.live + w.grace * r.graceTicks + FullTasDifficultySeconds(r.difficulty, w);
        }

        static float FullTasDifficultySeconds(FullTasDifficulty d, FullTasHumanWeights w)
        {
            return d == null ? 0f : w.framePerfect * d.framePerfect + w.backToBack * d.backToBack;
        }

        static string FullTasScoreText(FullTasClassRecord r, FullTasHumanWeights w)
        {
            string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
            return r.score.ToString("0.00", CultureInfo.InvariantCulture) + " (" + r.live.ToString("0.00", CultureInfo.InvariantCulture) + " live"
                + (r.graceTicks > 0 ? " + " + r.graceTicks + " grace ticks x " + F(w.grace) : "")
                + (r.difficulty != null && r.difficulty.framePerfect > 0 ? " + " + r.difficulty.framePerfect + " frame-perfect x " + F(w.framePerfect) : "")
                + (r.difficulty != null && r.difficulty.backToBack > 0 ? " + " + r.difficulty.backToBack + " back-to-back x " + F(w.backToBack) : "") + ")";
        }

        void RescoreFullTasClassBest(FullTasRunDef run, string cls)
        {
            if (!HumanClass(cls)) return;
            MacroDefinition best = FullTasClassBest(run.name, cls, out _);
            if (best == null || !_fullTasClasses.TryGetValue(best.id, out FullTasClassRecord old)) return;
            FullTasHumanWeights weights = run.Human;
            if (!(old.difficulty == null && weights.PricesDifficulty()) && Math.Abs(old.score - FullTasScoreOf(old, weights)) <= .005f) return;
            JObject used = FullTasTechsUsed(run, FullTasLabels(run), ToTicks(best.steps), true, out string error);
            FullTasClassRecord record = used == null ? null : FullTasClassRecordOf(used, cls, old.live, weights, out error);
            if (record == null)
            {
                FullTasLog("Full-TAS " + run.name + " " + cls + ": " + best.name + " kept its old score: the engine's tech checks (techs-used) " + error);
                return;
            }
            StoreFullTasClass(best, record);
            FullTasLog("Full-TAS " + run.name + " " + cls + ": " + best.name + " scored again: " + FullTasScoreText(record, weights));
        }

        JObject FullTasTechsUsed(FullTasRunDef run, IList<string> goals, List<SolverTick> inputs, bool difficulty, out string error)
        {
            error = null;
            string trace = FullTasTracePath(run.name);
            if (!FullTasTraceCurrent(run, trace, false)) { error = "need a current trace of " + run.name + " (Capture start)"; return null; }
            string file = Path.Combine(EngineSimDir, "fulltas-" + run.name + "-classes.json");
            File.WriteAllText(file, JsonConvert.SerializeObject(new { name = run.name, goals, ticks = inputs }));
            return TechsUsedJson(trace, file, difficulty && run.Human.PricesDifficulty() ? "" : " --no-difficulty", out error);
        }

        JObject TechsUsedJson(string trace, string file, string options, out string error)
        {
            error = null;
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { error = "need the engine built (" + exe + ")"; return null; }
            using (Process process = StartEngineSim("techs-used " + Quote(trace) + " " + Quote(file) + " --json" + options))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(FullTasTechsTimeoutMs))
                {
                    try { process.Kill(); } catch (Exception) { }
                    error = "gave no answer within " + FullTasTechsTimeoutMs / 1000 + " s";
                    return null;
                }
                process.WaitForExit();
                string line = output.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal));
                if (process.ExitCode != 0 || line == null)
                {
                    string last = errors.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                    error = "exited with code " + process.ExitCode + (last == null ? "" : ": " + last);
                    return null;
                }
                try { return JObject.Parse(line); }
                catch (JsonException ex) { error = "answered unreadably: " + ex.Message; return null; }
            }
        }

        Dictionary<string, object> FullTasClassesWebState(FullTasRunDef run)
        {
            var classes = new Dictionary<string, object>();
            foreach (string cls in FullTasClasses)
            {
                MacroDefinition best = FullTasClassBest(run.name, cls, out _);
                FullTasClassRecord r = null;
                if (best != null) _fullTasClasses.TryGetValue(best.id, out r);
                FullTasRouteOf(best?.name, out _, out _, out float time);
                classes[cls] = best == null ? null : new { routeId = best.id, name = best.name, time, score = r == null ? (float?)null : FullTasScoreOf(r, run.Human),
                    major = r?.major, nmg = r?.nmg, graceTicks = r?.graceTicks, spans = r?.spans, difficulty = r?.difficulty };
            }
            return classes;
        }

        object[] FullTasCandidatesWebState(FullTasRunDef run, string[] labels)
        {
            string dir = FullTasCandidatesDir(run.name);
            if (!Directory.Exists(dir)) return new object[0];
            string maps = Path.Combine(dir, "difficulty");
            FullTasHumanWeights w = run.Human;
            return Directory.GetFiles(dir, "*.json").Select(p => new { file = Path.GetFileName(p), c = ReadFullTasCandidate(p) })
                .Where(x => x.c != null && x.c.goals.SequenceEqual(labels))
                .Select(x =>
                {
                    JObject map = ReadFullTasDifficulty(Path.Combine(maps, x.file));
                    FullTasDifficulty counts = x.c.difficulty ?? map?.ToObject<FullTasDifficulty>();
                    float? term = counts != null ? FullTasDifficultySeconds(counts, w) : w.PricesDifficulty() ? (float?)null : 0f;
                    float? score = x.c.time == null ? x.c.score : x.c.time + w.grace * x.c.graceTicks + (term ?? 0f);
                    return new { x.file, x.c, map, counts, term, score };
                })
                .OrderBy(x => x.score ?? float.PositiveInfinity).ThenBy(x => x.file, StringComparer.Ordinal)
                .Select(x => (object)new { file = x.file, @class = x.c.cls, score = x.score, difficultySeconds = x.term, time = x.c.time, major = x.c.major,
                    nmg = x.c.nmg, graceTicks = x.c.graceTicks, spans = x.c.spans, reached = x.c.reached, goals = x.c.goals.Count,
                    difficulty = x.counts, difficultyMap = x.map })
                .ToArray();
        }

        JObject ReadFullTasDifficulty(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                JObject map = _fullTasDifficulties.Get(path, p => JObject.Parse(File.ReadAllText(p)));
                return map?.ToObject<FullTasDifficulty>() != null && map["events"] is JArray && map["pairs"] is JArray ? map : null;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS difficulty " + path + " unreadable: " + ex.Message);
                return null;
            }
        }

        object StartFullTasDifficulty(JObject body)
        {
            if (_fullTasDifficultyProcess != null) throw new InvalidOperationException("A difficulty batch is already running (" + _fullTasDifficultyRun + ").");
            FullTasRunDef run = FindFullTasRun(Text(body, "name"));
            string trace = FullTasTracePath(run.name), dir = FullTasCandidatesDir(run.name), exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new InvalidOperationException("The offline solver is not built (" + exe + "): dotnet build enginesim\\Cli\\IGTAP.EngineSim.csproj -c Release");
            if (!FullTasTraceCurrent(run, trace, false)) throw new InvalidOperationException("Need a current trace of " + run.name + " (Capture start).");
            if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.json").Length == 0) throw new InvalidOperationException("No candidates of " + run.name + " in " + dir + ".");
            int threads = Math.Max(1, Environment.ProcessorCount - 4);
            string args = "difficulty " + Quote(trace) + " --candidates " + Quote(dir) + " --threads " + threads;
            while (_fullTasDifficultyOutput.TryDequeue(out _)) { }
            _fullTasDifficultyProcess = StartEngineSim(args, _fullTasDifficultyOutput);
            _fullTasDifficultyRun = run.name;
            _fullTasDifficultyDone = 0;
            _fullTasDifficultyOf = null;
            FullTasLog("Measuring the difficulty of " + run.name + "'s candidates on " + threads + " threads");
            return new { ok = true, command = exe + " " + args };
        }

        void FullTasDifficultyLine(string line)
        {
            if (line.StartsWith("@difficulty ", StringComparison.Ordinal))
            {
                JObject e = null;
                try { e = JObject.Parse(line.Substring("@difficulty ".Length)); }
                catch (JsonException) { }
                if (e != null)
                {
                    _fullTasDifficultyDone = (int?)e["done"] ?? _fullTasDifficultyDone;
                    _fullTasDifficultyOf = (int?)e["of"] ?? _fullTasDifficultyOf;
                    return;
                }
            }
            FullTasLog("difficulty: " + line);
        }

        void PumpFullTasDifficulty()
        {
            if (_fullTasDifficultyProcess == null) return;
            try
            {
                while (_fullTasDifficultyOutput.TryDequeue(out string line)) FullTasDifficultyLine(line);
                if (!_fullTasDifficultyProcess.HasExited) return;
                // WaitForExit waits for the last stdout/stderr lines, which EndFullTasDifficulty reads.
                _fullTasDifficultyProcess.WaitForExit();
                int code = _fullTasDifficultyProcess.ExitCode;
                if (code == 0) EndFullTasDifficulty("Measured the difficulty of " + _fullTasDifficultyRun + "'s candidates (" + FullTasDifficultyCount() + ")");
                else EndFullTasDifficulty("The difficulty batch of " + _fullTasDifficultyRun + " exited with code " + code + " (" + FullTasDifficultyCount() + "): the log has its last words", true);
            }
            catch (Exception ex) { Logger.LogError(ex); EndFullTasDifficulty("Difficulty batch error: " + ex.Message, true); }
        }

        void StopFullTasDifficulty()
        {
            if (_fullTasDifficultyProcess != null) EndFullTasDifficulty("Stopped measuring the difficulty of " + _fullTasDifficultyRun + "'s candidates (" + FullTasDifficultyCount() + ")");
        }

        void EndFullTasDifficulty(string result, bool error = false)
        {
            KillEngineSim(ref _fullTasDifficultyProcess, "difficulty batch");
            while (_fullTasDifficultyOutput.TryDequeue(out string line)) FullTasDifficultyLine(line);
            SetFullTasResult(result, error: error);
        }

        string FullTasDifficultyCount() { return _fullTasDifficultyDone + " of " + (_fullTasDifficultyOf?.ToString(CultureInfo.InvariantCulture) ?? "?") + " done"; }

        FullTasCandidate ReadFullTasCandidate(string path)
        {
            try
            {
                return _fullTasCandidates.Get(path, p =>
                {
                    var o = new JObject();
                    using (var reader = new JsonTextReader(new StreamReader(p)))
                    {
                        reader.Read();
                        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                        {
                            string name = (string)reader.Value;
                            reader.Read();
                            if (name == "class" || name == "score" || name == "live" || name == "goals" || name == "glitches" || name == "difficulty")
                                o[name] = JToken.ReadFrom(reader);
                            else reader.Skip();
                        }
                    }
                    var glitches = o["glitches"] as JObject ?? new JObject();
                    float live = (float?)o["live"] ?? -1f;
                    return new FullTasCandidate { cls = (string)o["class"], score = (float?)o["score"], time = live >= 0f ? live : (float?)null,
                        major = (int?)glitches["major"] ?? 0, nmg = (int?)glitches["nmg"] ?? 0, graceTicks = (int?)glitches["graceTicks"] ?? 0,
                        spans = glitches["spans"]?.ToObject<List<FullTasSpan>>() ?? new List<FullTasSpan>(),
                        goals = (o["goals"] as JArray ?? new JArray()).Select(g => (string)g["label"]).ToList(),
                        reached = (o["goals"] as JArray ?? new JArray()).TakeWhile(g => (long)g["input"] >= 0).Count(),
                        difficulty = (o["difficulty"] as JObject)?.ToObject<FullTasDifficulty>() };
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS candidate " + path + " unreadable: " + ex.Message);
                return null;
            }
        }
    }
}
