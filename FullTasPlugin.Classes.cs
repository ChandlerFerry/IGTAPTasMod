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
        /// <summary>How far the engine's live time of a route may sit from the gate's and still be the same run: under a held
        /// frame (1/280 s), above the rounding of its JSON.</summary>
        const float FullTasTechsLiveSlack = .0005f;
        const int FullTasTechsTimeoutMs = 300000;

        /// <summary>A human candidate file's summary (`fulltas/candidates/&lt;run&gt;/*.json`, the run file's shape); its inputs are
        /// read only when it plays.</summary>
        sealed class FullTasCandidate
        {
            public string cls;
            public float? score, time;
            public int major, nmg, graceTicks, reached;
            public List<FullTasSpan> spans;
            public List<string> goals;
        }
        readonly CachedFiles<FullTasCandidate> _fullTasCandidates = new CachedFiles<FullTasCandidate>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A route name's Full-TAS run, class and live time, when it's a saved Full-TAS route (not stale).</summary>
        static bool FullTasRouteOf(string name, out string run, out string cls, out float time)
        {
            Match m = FullTasClassRoute.Match(name ?? "");
            run = m.Success ? m.Groups["run"].Value : null;
            cls = !m.Success ? null : m.Groups["class"].Success ? m.Groups["class"].Value.ToLowerInvariant() : MajorClass;
            time = m.Success && float.TryParse(m.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ? t : 0f;
            return m.Success;
        }

        /// <summary>
        /// A run's saved best of a class and what ranks it: live time for major and nmg, the score (fulltas-classes.json) for a
        /// human class. A human route with no class result ranks at its live time, its score's floor, so a route beating less
        /// never replaces it.
        /// </summary>
        MacroDefinition FullTasClassBest(string run, string cls, out float rank)
        {
            rank = float.PositiveInfinity;
            MacroDefinition best = null;
            foreach (MacroDefinition m in _library.macros)
            {
                if (!FullTasRouteOf(m.name, out string r, out string c, out float time) || c != cls || !string.Equals(r, run, StringComparison.OrdinalIgnoreCase)) continue;
                float value = HumanClass(cls) && _fullTasClasses.TryGetValue(m.id, out FullTasClassRecord record) ? record.score : time;
                if (value < rank) { best = m; rank = value; }
            }
            return best;
        }

        /// <summary>A class best was saved: the run's other routes of that class go, as PruneSlowerRoutes keeps one major record.</summary>
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

        /// <summary>
        /// The class half of the gate (README ### Rules 54): the engine's tech checks replay the route from the run's trace, an
        /// nmg class refuses any major span, and a human class's score has to be strictly lower than its best's. The class
        /// result goes to <c>judge.record</c>. A major route is ranked by live time alone: when the checks can't class it, it
        /// is judged as before and saved without a class result.
        /// </summary>
        string FullTasClassViolation(FullTasClassJudge judge, IList<string> goals, List<SolverTick> inputs, float time)
        {
            FullTasHumanWeights weights = judge.run.Human;
            JObject used = FullTasTechsUsed(judge.run, goals, inputs, out string error);
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

        /// <summary>
        /// A route's class result from the engine's `techs-used --json` line, timed by the gate's own live time; null, with why
        /// (<paramref name="error"/>), when the engine's replay isn't the run the gate judged: short of its goals, or another
        /// live time. Score: live time plus the weights' seconds for each major span, nmg span and spike-grace tick.
        /// </summary>
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
            var record = new FullTasClassRecord { cls = cls, live = time, major = (int?)used["major"] ?? 0, nmg = (int?)used["nmg"] ?? 0,
                graceTicks = (int?)used["graceTicks"] ?? 0, spans = used["spans"]?.ToObject<List<FullTasSpan>>() ?? new List<FullTasSpan>() };
            record.score = time + weights.major * record.major + weights.nmg * record.nmg + weights.grace * record.graceTicks;
            return record;
        }

        static string FullTasScoreText(FullTasClassRecord r, FullTasHumanWeights w)
        {
            string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
            return r.score.ToString("0.00", CultureInfo.InvariantCulture) + " (live " + r.live.ToString("0.00", CultureInfo.InvariantCulture)
                + (r.major > 0 ? " + " + r.major + " major x " + F(w.major) : "") + (r.nmg > 0 ? " + " + r.nmg + " nmg x " + F(w.nmg) : "")
                + (r.graceTicks > 0 ? " + " + r.graceTicks + " grace ticks x " + F(w.grace) : "") + ")";
        }

        /// <summary>
        /// The engine's `techs-used &lt;trace&gt; &lt;route&gt; --json` on a route of the run, replayed from the run's trace, with the goals
        /// it is judged under: its one JSON line. Null, with why (<paramref name="error"/>), with no current trace or when the
        /// engine fails.
        /// </summary>
        JObject FullTasTechsUsed(FullTasRunDef run, IList<string> goals, List<SolverTick> inputs, out string error)
        {
            error = null;
            string trace = FullTasTracePath(run.name), exe = EngineSimExe;
            if (!FullTasTraceCurrent(run, trace, false)) { error = "need a current trace of " + run.name + " (Capture start)"; return null; }
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { error = "need the engine built (" + exe + ")"; return null; }
            string file = Path.Combine(EngineSimDir, "fulltas-" + run.name + "-classes.json");
            File.WriteAllText(file, JsonConvert.SerializeObject(new { name = run.name, goals, ticks = inputs }));
            using (Process process = StartEngineSim("techs-used " + Quote(trace) + " " + Quote(file) + " --json"))
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

        /// <summary>The run's best of each class (studio static state `classes`, every key, null with no route): its route, live
        /// time and class result (null fields for a route never classed).</summary>
        Dictionary<string, object> FullTasClassesWebState(string run)
        {
            var classes = new Dictionary<string, object>();
            foreach (string cls in FullTasClasses)
            {
                MacroDefinition best = FullTasClassBest(run, cls, out _);
                FullTasClassRecord r = null;
                if (best != null) _fullTasClasses.TryGetValue(best.id, out r);
                FullTasRouteOf(best?.name, out _, out _, out float time);
                classes[cls] = best == null ? null : new { routeId = best.id, name = best.name, time, score = r?.score, major = r?.major, nmg = r?.nmg,
                    graceTicks = r?.graceTicks, spans = r?.spans };
            }
            return classes;
        }

        /// <summary>The human candidates of the run's current goals (studio static state `candidates`), by score.</summary>
        object[] FullTasCandidatesWebState(string run, string[] labels)
        {
            string dir = FullTasCandidatesDir(run);
            if (!Directory.Exists(dir)) return new object[0];
            return Directory.GetFiles(dir, "*.json").Select(p => new { file = Path.GetFileName(p), c = ReadFullTasCandidate(p) })
                .Where(x => x.c != null && x.c.goals.SequenceEqual(labels))
                .OrderBy(x => x.c.score ?? float.PositiveInfinity).ThenBy(x => x.file, StringComparer.Ordinal)
                .Select(x => (object)new { file = x.file, @class = x.c.cls, score = x.c.score, time = x.c.time, major = x.c.major, nmg = x.c.nmg,
                    graceTicks = x.c.graceTicks, spans = x.c.spans, reached = x.c.reached, goals = x.c.goals.Count })
                .ToArray();
        }

        /// <summary>A candidate file's summary, its inputs skipped (a whole run's each); null when it can't be read.</summary>
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
                            if (name == "class" || name == "score" || name == "live" || name == "goals" || name == "goalInputTicks" || name == "glitches")
                                o[name] = JToken.ReadFrom(reader);
                            else reader.Skip();
                        }
                    }
                    var glitches = o["glitches"] as JObject ?? new JObject();
                    float live = (float?)o["live"] ?? -1f;
                    return new FullTasCandidate { cls = (string)o["class"], score = (float?)o["score"], time = live >= 0f ? live : (float?)null,
                        major = (int?)glitches["major"] ?? 0, nmg = (int?)glitches["nmg"] ?? 0, graceTicks = (int?)glitches["graceTicks"] ?? 0,
                        spans = glitches["spans"]?.ToObject<List<FullTasSpan>>() ?? new List<FullTasSpan>(),
                        goals = o["goals"]?.ToObject<List<string>>() ?? new List<string>(),
                        reached = (o["goalInputTicks"] as JArray ?? new JArray()).TakeWhile(t => (long)t >= 0).Count() };
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
