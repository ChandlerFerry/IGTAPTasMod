using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
        public sealed class CourseDef
        {
            public int id;
            public string name;
            public float x, y;
            public float endX, endY, endW, endH;
            public string from, to;
            public int toCourse;
            public string minCapabilities;
            public string maxCapabilities;
            [JsonIgnore] public bool HasFinish { get { return endW > 0f && endH > 0f; } }
            [JsonIgnore] public bool IsTransition { get { return !string.IsNullOrEmpty(to); } }
            [JsonIgnore] public bool IsBonus { get { return HasFinish && !IsTransition; } }
            [JsonIgnore] public string Token { get { return string.IsNullOrEmpty(name) ? id.ToString() : name; } }
            [JsonIgnore] public int MinLevel { get { return CapsLevel(minCapabilities); } }
            [JsonIgnore] public int MaxLevel { get { return !IsBonus ? 3 : string.IsNullOrEmpty(maxCapabilities) ? MinLevel : CapsLevel(maxCapabilities); } }
            public bool HasCategory(string caps) { int level = CapsLevel(caps); return level >= MinLevel && level <= MaxLevel; }
        }

        sealed class SolverTick
        {
            public float x, y; public bool held, press, release, dash; public int dashJump;
            public int springDash;
            public float? lockX, lockY;
            public bool reset;
            public SolverTick Clone() { return (SolverTick)MemberwiseClone(); }
        }


        static readonly FieldInfo CourseTrackingFlag = AccessTools.Field(typeof(courseScript), "tracking");

        List<CourseDef> _courses = new List<CourseDef>();
        MacroDefinition _pendingSilentMacro;
        bool _silentEvaluation, _silentFinished;
        float _silentTime;

        bool _solverRunning;
        string _solverStatus = "Idle", _solverSpeed = "";
        CourseDef _solverTarget;
        string _solverCaps = "";
        courseScript[] _solverCourses;
        TurboRunner.Snapshot _startState;
        readonly List<string> _solverLog = new List<string>();

        Process _solveProcess;
        readonly ConcurrentQueue<string> _solveOutput = new ConcurrentQueue<string>();
        int _solveCourse;
        string _solveCaps = "";
        string[] _solveOptional = new string[0];
        long _solveRuns, _solveFinishes;
        int _turboChecks, _turboSaved;
        float _solveBest = float.PositiveInfinity;

        sealed class SolveJob
        {
            public int course, saved;
            public string caps, label;
            public string[] optional;
            public string result;
        }

        readonly List<SolveJob> _solveQueue = new List<SolveJob>();
        int _solveIndex = -1;
        float _solveMinutes = 60f, _solveMaxMinutes = 60f;
        bool _solveRepeat;
        int _solvePass = 1;

        string CoursesPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "courses.json"); } }

        static CourseDef[] GameCourses()
        {
            return new[] {
                new CourseDef { id = 1, x = 2241f, y = -292f, minCapabilities = "none" },
                new CourseDef { id = 2, x = 4783.43359f, y = 1878.2251f, minCapabilities = "none" },
                new CourseDef { id = 3, x = 9505f, y = 6967.01465f, minCapabilities = "wjds" },
                new CourseDef { id = 4, x = 20200.4004f, y = 10554.5449f, minCapabilities = "wjdsdj" },
                new CourseDef { id = 5, x = -3281f, y = -295.455078f, minCapabilities = "wjdsdj" },
                new CourseDef { id = 6, x = 26174.6016f, y = -476.427734f, minCapabilities = "wjdsdj" },
                new CourseDef { id = 7, x = 19891f, y = 3039f, minCapabilities = "wjdsdj" },
                new CourseDef { id = 8, x = 26495.6016f, y = -2372.42773f, minCapabilities = "wjdsdj" },
                new CourseDef { id = 9, x = 17138.6016f, y = 6648.11475f, minCapabilities = "wjdsdj" },
                new CourseDef { id = 10, x = 30186.6016f, y = 5792.57227f, minCapabilities = "wjdsdj" } };
        }

        void LoadCourses()
        {
            try
            {
                if (File.Exists(CoursesPath))
                {
                    _courses = JsonConvert.DeserializeObject<List<CourseDef>>(File.ReadAllText(CoursesPath));
                    CourseDef[] missing = GameCourses().Where(g => !_courses.Any(c => c.id == g.id)).ToArray();
                    if (missing.Length == 0) return;
                    int at = _courses.FindLastIndex(c => string.IsNullOrEmpty(c.name) && c.id < 100) + 1;
                    _courses.InsertRange(at, missing);
                    SaveCourses();
                    return;
                }
            }
            catch (Exception ex) { Logger.LogWarning("courses.json rejected: " + ex.Message); }
            _courses = GameCourses().ToList();
            _courses.AddRange(new[] {
                new CourseDef { id = 101, name = "bonus1", x = 7392.427f, y = 2807.015f, minCapabilities = "wj",
                    endX = 5239.43f, endY = 681f, endW = 127.5f, endH = 6f },
                new CourseDef { id = 102, name = "bonus2", x = 7670f, y = 5207.015f, minCapabilities = "wjds",
                    endX = 8622f, endY = 5622.977f, endW = 127.5f, endH = 6f },
                new CourseDef { id = 103, name = "bonus3", x = 9505f, y = 6967.01465f, minCapabilities = "wjds",
                    endX = 11200.0996f, endY = 7363.977f, endW = 127.5f, endH = 6f } });
            SaveCourses();
        }

        void SaveCourses() { WriteJsonAtomic(CoursesPath, _courses, false); }

        object AddCourseFromLastRecording()
        {
            RecordingDefinition recording = _recordings.OrderByDescending(r => r.createdUtc).FirstOrDefault();
            if (recording == null || recording.samples == null || recording.samples.Count == 0)
                throw new InvalidOperationException("No recording to take the course start from.");
            int id = 1;
            while (_courses.Any(c => c.id == id)) id++;
            Vector2 first = Absolute(recording.samples[0]);
            _courses.Add(new CourseDef { id = id, x = first.x, y = first.y + 12f });
            SaveCourses();
            CreateRouteFromRecording(recording);
            _library.macros[_library.macros.Count - 1].name = id + "_REC " + recording.name;
            SaveLibrary();
            return new { ok = true, course = id };
        }

        static Vector2 Absolute(RawTickSample t) { return new Vector2(t.positionX - t.originX, t.positionY - t.originY); }

        static readonly Regex TransitionName = new Regex(@"^(Course|Bonus)(\d+)_To_(Course|Bonus)(\d+)(?:_(NONE|WJ_DS_DJ|WJ_DS|WJ))?\b", RegexOptions.IgnoreCase);
        const float BonusArrivalSize = 32f;

        object AddTransitionFromRecording(RecordingDefinition recording)
        {
            if (recording.samples == null || recording.samples.Count == 0) throw new InvalidOperationException("The recording is empty.");
            Match m = TransitionName.Match(recording.name ?? "");
            if (!m.Success)
                throw new InvalidOperationException("Name the recording \"Course1_To_Course2\" or \"Course2_To_Bonus2\" (optionally with _WJ, _WJ_DS, ...) first.");
            bool fromBonus = m.Groups[1].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase);
            bool toBonus = m.Groups[3].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase);
            string origin = (fromBonus ? "bonus" : "") + m.Groups[2].Value, target = (toBonus ? "bonus" : "") + m.Groups[4].Value;
            Vector2 start = Absolute(recording.samples[0]) + new Vector2(0f, 12f);
            if (!fromBonus)
            {
                CourseDef course = _courses.Find(c => c.id == int.Parse(origin) && !c.IsBonus && !c.IsTransition);
                if (course == null) throw new InvalidOperationException("Course " + origin + " is not in courses.json.");
                if ((new Vector2(course.x, course.y) - start).sqrMagnitude > 64f * 64f)
                    throw new InvalidOperationException("The recording does not start at course " + origin + "'s start (quick restart there, then record).");
                start = new Vector2(course.x, course.y);
            }
            else if (BonusStart(origin, out Vector2 known)) start = known;
            var transition = _courses.Find(c => c.IsTransition && c.from == origin && c.to == target);
            if (transition == null)
            {
                int id = 201;
                while (_courses.Any(c => c.id == id)) id++;
                Movement player = FindWorldMovement();
                int level = m.Groups[5].Success ? CapsLevel(CapsFor(m.Groups[5].Value.ToUpperInvariant())) : player == null ? 0 : SaveUnlockLevel(player);
                transition = new CourseDef { id = id, name = origin + "to" + target, x = start.x, y = start.y, from = origin, to = target,
                    minCapabilities = CapabilityLabels.Keys.First(k => CapsLevel(k) == level) };
                if (!toBonus)
                {
                    transition.toCourse = int.Parse(target);
                    if (_courses.All(c => c.id != transition.toCourse || c.IsBonus || c.IsTransition))
                        throw new InvalidOperationException("Course " + target + " is not in courses.json.");
                }
                else if (BonusStart(target, out Vector2 end))
                {
                    transition.endX = end.x; transition.endY = end.y - 12f;
                    transition.endW = transition.endH = BonusArrivalSize;
                }
                else throw new InvalidOperationException("Where Bonus " + m.Groups[4].Value + " starts is unknown: record \"Bonus"
                    + m.Groups[4].Value + "_To_...\" from its start first.");
                _courses.Add(transition);
                SaveCourses();
            }
            List<RawTickSample> samples = recording.samples;
            if (transition.HasFinish)
            {
                int arrival = samples.FindIndex(t => Mathf.Abs(Absolute(t).x - transition.endX) <= transition.endW / 2f
                    && Mathf.Abs(Absolute(t).y - transition.endY) <= transition.endH / 2f);
                if (arrival >= 0 && arrival + 2 < samples.Count) samples = samples.GetRange(0, arrival + 2);
            }
            CreateRouteFromRecording(recording, samples);
            _library.macros[_library.macros.Count - 1].name = transition.Token + "_REC " + recording.name;
            SaveLibrary();
            return new { ok = true, course = transition.id, token = transition.Token };
        }

        bool BonusStart(string bonus, out Vector2 start)
        {
            CourseDef known = _courses.Find(c => string.Equals(c.name, bonus, StringComparison.OrdinalIgnoreCase) && c.IsBonus)
                ?? _courses.Find(c => c.IsTransition && c.from == bonus);
            if (known != null) { start = new Vector2(known.x, known.y); return true; }
            foreach (RecordingDefinition r in _recordings)
            {
                Match m = TransitionName.Match(r.name ?? "");
                if (!m.Success || r.samples == null || r.samples.Count == 0 || !m.Groups[1].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase)
                    || "bonus" + m.Groups[2].Value != bonus) continue;
                start = Absolute(r.samples[0]) + new Vector2(0f, 12f);
                return true;
            }
            start = Vector2.zero;
            return false;
        }

        static readonly Dictionary<string, string> CapabilityLabels = new Dictionary<string, string> {
            { "none", "NONE" }, { "wj", "WJ" }, { "wjds", "WJ_DS" }, { "wjdsdj", "WJ_DS_DJ" } };

        void CheckCategory(int course, string capabilities)
        {
            CourseDef def = _courses.Find(c => c.id == course);
            if (def == null || def.HasCategory(capabilities)) return;
            string Label(int level) => CapabilityLabels.First(p => CapsLevel(p.Key) == level).Value;
            throw new InvalidOperationException((def.IsBonus ? "Bonus " : "Course ") + def.Token + " has no " + CapabilityLabels[capabilities]
                + " category; it " + (!def.IsBonus ? "starts at " + Label(def.MinLevel)
                    : def.MaxLevel == def.MinLevel ? "is only run in " + Label(def.MinLevel)
                    : "is run in " + Label(def.MinLevel) + " to " + Label(def.MaxLevel)) + ".");
        }

        static string EngineSimExe
        {
            get { return typeof(TasPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "EngineSimExe")?.Value; }
        }

        SolveJob CourseJob(int course, string capabilities, string[] optional)
        {
            CourseDef def = _courses.Find(c => c.id == course);
            if (def == null) throw new InvalidOperationException("Unknown course " + course + ".");
            if (!CapabilityLabels.ContainsKey(capabilities ?? "")) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, capabilities);
            string[] known = OptionalBlocks(course).Where(b => b.caps.Contains(capabilities)).Select(b => b.name).ToArray();
            string missing = optional.FirstOrDefault(n => !known.Contains(n));
            if (missing != null) throw new InvalidOperationException("No optional block '" + missing + "' for " + def.Token + " " + capabilities + ".");
            return new SolveJob { course = course, caps = capabilities, optional = optional,
                label = def.Token + "_" + CapabilityLabels[capabilities] + (optional.Length == 0 ? "" : " +" + string.Join(" +", optional)) };
        }

        static string[] OptionalNames(JObject body)
        {
            return body["optional"] is JArray names ? names.Select(n => (string)n).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray() : new string[0];
        }

        static StringBuilder AppendOptional(StringBuilder args, string[] optional)
        {
            foreach (string name in optional) args.Append(" --optional ").Append(Quote(name));
            return args;
        }

        sealed class OptionalBlock
        {
            public int course;
            public string name, note;
            public string[] caps;
        }

        OptionalBlock[] _optionalBlocks = new OptionalBlock[0];
        DateTime _optionalBlocksRead;

        // The optional blocks of forced-paths.json next to the offline solver, reread when the file changes.
        IEnumerable<OptionalBlock> OptionalBlocks(int course)
        {
            string exe = EngineSimExe;
            string file = string.IsNullOrEmpty(exe) ? null : Path.Combine(Path.GetDirectoryName(exe), "forced-paths.json");
            DateTime stamp = file != null && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default(DateTime);
            if (stamp != _optionalBlocksRead)
            {
                _optionalBlocksRead = stamp;
                _optionalBlocks = stamp == default(DateTime) ? new OptionalBlock[0]
                    : ((JArray)JObject.Parse(File.ReadAllText(file))["paths"]).OfType<JObject>()
                        .SelectMany(rule => (rule["optional"] as JArray ?? new JArray()).OfType<JObject>().Select(b => new OptionalBlock {
                            course = (int)rule["course"], name = (string)b["name"], note = (string)b["note"] ?? "",
                            caps = rule["capabilities"].Select(c => (string)c).ToArray() })).ToArray();
            }
            return _optionalBlocks.Where(b => b.course == course);
        }

        void StartSolver(JObject body)
        {
            if (_solverRunning) throw new InvalidOperationException("The solver is already running.");
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new InvalidOperationException("The offline solver is not built (" + exe + "): dotnet build enginesim\\Cli\\IGTAP.EngineSim.csproj -c Release");
            List<JObject> items = body["queue"] is JArray queue ? queue.OfType<JObject>().ToList() : new List<JObject> { body };
            List<SolveJob> jobs = items.Select(i => CourseJob(Number(i, "course", 0), Text(i, "capabilities"), OptionalNames(i))).ToList();
            if (jobs.Count == 0) throw new InvalidOperationException("Nothing selected to optimize.");
            float minutes = FloatNumber(body, "minutes", 60f);
            if (!(minutes >= 0.25f)) throw new InvalidOperationException("Minutes per target must be at least 0.25.");
            _solveQueue.Clear();
            _solveQueue.AddRange(jobs);
            _solveMinutes = minutes;
            _solveMaxMinutes = Math.Max(minutes, FloatNumber(body, "maxMinutes", minutes));
            _solveRepeat = body["repeat"] != null && body["repeat"].Type == JTokenType.Boolean && (bool)body["repeat"];
            _solvePass = 1;
            _solverLog.Clear();
            StartSolveJob(0);
        }

        void StartSolveJob(int index)
        {
            SolveJob job = _solveQueue[index];
            if (CapabilitiesLocked) throw new InvalidOperationException(CapabilitiesLockedError);
            _solveIndex = index;
            _solveCourse = job.course; _solveCaps = job.caps; _solveOptional = job.optional;
            _solveRuns = _solveFinishes = 0;
            _turboChecks = _turboSaved = 0;
            _solveBest = float.PositiveInfinity;
            _solverSpeed = "";
            while (_solveOutput.TryDequeue(out _)) { }
            if (_solveQueue.Count > 1 || _solveRepeat)
                _solverLog.Add("── " + (_solveRepeat ? "pass " + _solvePass + " · " : "") + (index + 1) + "/" + _solveQueue.Count + " " + job.label);
            _solverRunning = true;
            _solverStatus = "Capturing the start state in turbo";
            EnsureGameUnpaused();
        }

        void KillSolveProcess()
        {
            if (_solveProcess == null) return;
            try { if (!_solveProcess.HasExited) _solveProcess.Kill(); }
            catch (Exception ex) { Logger.LogWarning("Could not stop the offline solver: " + ex.Message); }
            _solveProcess.Dispose();
            _solveProcess = null;
        }

        void EndSolveJob(string outcome)
        {
            KillSolveProcess();
            SolveJob job = _solveQueue[_solveIndex];
            job.saved = _turboSaved;
            job.result = outcome + (float.IsInfinity(_solveBest) ? "" : ", best " + _solveBest.ToString("0.00", CultureInfo.InvariantCulture) + "s")
                + ", " + _turboSaved + " saved";
            if (StartNextSolveJob(_solveIndex + 1)) return;
            if (_solveRepeat)
            {
                _solverLog.Add("Pass " + _solvePass + " done: " + _solveQueue.Sum(j => j.saved) + " new routes saved");
                _solvePass++;
                _solveMinutes = Math.Min(_solveMaxMinutes, _solveMinutes * 2f);
                foreach (SolveJob j in _solveQueue) { j.result = null; j.saved = 0; }
                if (StartNextSolveJob(0)) return;
            }
            _solverRunning = false;
            _solverStatus = _solveQueue.Count == 1 && !_solveRepeat ? outcome
                : "Done: " + _solveQueue.Count + " targets, " + _solveQueue.Sum(j => j.saved) + " new routes saved";
        }

        bool StartNextSolveJob(int from)
        {
            for (int next = from; next < _solveQueue.Count; next++)
            {
                try { StartSolveJob(next); return true; }
                catch (Exception ex)
                {
                    _solveQueue[next].result = "Skipped: " + ex.Message;
                    _solverLog.Add("Skipped " + _solveQueue[next].label + ": " + ex.Message);
                }
            }
            return false;
        }

        void SkipSolveJob()
        {
            if (!_solverRunning) throw new InvalidOperationException("The solver is not running.");
            EndSolveJob("Skipped");
        }

        int _parkedFrame = -1000;

        bool CourseLoaded()
        {
            _solverCourses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            Movement player = FindWorldMovement();
            CourseDef target = _solverTarget;
            Vector2 start = new Vector2(target.x, target.y) + CurrentOrigin();
            bool parked = player != null && ((Vector2)player.transform.position - start).sqrMagnitude < 64f * 64f;
            if (!parked)
            {
                RestoreRouteStart(new MacroDefinition { name = target.Token + "_" });
                _parkedFrame = Time.frameCount;
            }
            else if (Time.frameCount - _parkedFrame >= 10 && ZonesSettled() && (target.HasFinish
                || _solverCourses.Any(c => c.courseNumber == (target.IsTransition ? target.toCourse : target.id) && c.isActiveAndEnabled))) return true;
            _solverStatus = "Loading course " + target.Token;
            return false;
        }

        static readonly FieldInfo AllZonesField = AccessTools.Field(typeof(ZoneLoader), "allZones");

        static bool ZonesSettled()
        {
            ZoneLoader loader = FindAnyObjectByType<ZoneLoader>();
            if (loader == null || !(AllZonesField?.GetValue(loader) is GameObject[] zones)) return true;
            for (int i = 0; i < zones.Length; i++)
                if (zones[i] != null && zones[i].activeSelf != (i == loader.activeZone - 1)) return false;
            return true;
        }

        void StopSolver(string status)
        {
            KillSolveProcess();
            if (_solverRunning && _solveIndex >= 0 && _solveIndex < _solveQueue.Count)
            {
                _solveQueue[_solveIndex].saved = _turboSaved;
                _solveQueue[_solveIndex].result = status + ", " + _turboSaved + " saved";
            }
            _solverRunning = false;
            _solverStatus = status;
        }

        object BuildSolverWebState()
        {
            return new {
                running = _solverRunning, status = _solverStatus, course = _solveCourse, capabilities = _solveCaps, optional = _solveOptional, speed = _solverSpeed,
                evaluations = _solveRuns, finishes = _solveFinishes, turboChecks = _turboChecks, turboSaved = _turboSaved, toValidate = _offlineWaiting,
                best = float.IsInfinity(_solveBest) ? (float?)null : _solveBest,
                log = _solverLog.AsEnumerable().Reverse().Take(12).ToArray(),
                courses = _courses.Select(c => c.id).ToArray(),
                minutes = _solveMinutes, repeat = _solveRepeat, pass = _solvePass,
                queue = _solveQueue.Select((j, i) => new { label = j.label, result = j.result, active = _solverRunning && i == _solveIndex }).ToArray() };
        }

        object[] BuildCourseWebState()
        {
            return _courses.Select(c => (object)new { id = c.id, token = c.Token, bonus = c.IsBonus, minLevel = c.MinLevel,
                maxLevel = c.MaxLevel, @from = c.from, to = c.to, hint = c.IsBonus ? BonusHint(c) : null,
                optional = OptionalBlocks(c.id).Select(b => new { name = b.name, note = b.note, capabilities = b.caps }).ToArray() }).ToArray();
        }

        static readonly FieldInfo BoxCurrency = AccessTools.Field(typeof(upgradeBox), "upgradeCurrency");
        static readonly Regex RichTextTag = new Regex("<[^>]*>"), Whitespace = new Regex(@"\s+");

        string BonusHint(CourseDef bonus)
        {
            upgradeBox box = BonusRewardBox(bonus, out _);
            if (box == null) return null;
            var currency = (globalStats.Currencies)BoxCurrency.GetValue(box);
            string BoxText(string field) => Whitespace.Replace(RichTextTag.Replace(Traverse.Create(box).Field(field).Property("text").GetValue<string>() ?? "", ""), " ").Trim();
            string reward = BoxText("upgradeNameDisplay"), tier = BoxText("tierEffectDisplay");
            if (reward.Length == 0) reward = box.upgrade == localUpgrades.localUpgradeSet.GLOBAL ? box.globalUpgrade.ToString()
                : box.upgrade == localUpgrades.localUpgradeSet.Movement ? box.movementUpgrade.ToString() : box.upgrade.ToString();
            string price;
            try { price = RichTextTag.Replace(CashDisplay.updateCashDisplay(box.upgradeCost, currency).Item1, ""); }
            catch (Exception) { price = box.upgradeCost.ToString("0.##", CultureInfo.InvariantCulture) + " " + currency; }
            return "Reward: " + reward + (tier.Length > 0 ? " " + tier : "") + "\nPrice: " + price
                + (box.TimesUsed > 0 ? " (bought)" : "");
        }

        Vector2 _startOrigin;

        void PumpSolver()
        {
            if (!_solverRunning) return;
            try
            {
                if (_solveProcess == null) { LaunchSolve(); return; }
                while (_solveOutput.TryDequeue(out string line)) ReadSolveLine(line);
                if (!_solveProcess.HasExited) return;
                _solveProcess.WaitForExit();
                while (_solveOutput.TryDequeue(out string line)) ReadSolveLine(line);
                int code = _solveProcess.ExitCode;
                EndSolveJob(code == 0 ? "Finished" : "The offline solver exited with code " + code);
            }
            catch (Exception ex) { Logger.LogError(ex); EndSolveJob("Solver error: " + ex.Message); }
        }

        void LaunchSolve()
        {
            if (PlaybackActive || _recorder.IsRecording) return;
            JObject target = new JObject { ["course"] = _solveCourse, ["capabilities"] = _solveCaps };
            SelectSolverTarget(target);
            if (_newGamePhase != 0 || _bootstrapRequest != null) { _solverStatus = _newGamePhase != 0 ? "Loading a new game" : "Waiting for the bootstrap"; return; }
            if (!CourseLoaded()) return;
            CourseDef course = _solverTarget;
            string name = "optimize-c" + course.Token + "-" + _solveCaps;
            JObject body = (JObject)target.DeepClone();
            body["ticks"] = new JArray(); body["name"] = name; body["world"] = true; body["freshStart"] = true;
            JObject captured = JObject.FromObject(EngineSimTrace(new WebStudioRequest(), body));
            if (captured["ok"] == null || !(bool)captured["ok"])
            {
                EndSolveJob("Start state not captured: " + captured["error"]);
                return;
            }
            var args = new StringBuilder(Quote((string)captured["path"]));
            foreach (string old in Directory.GetFiles(EngineSimDir, name + "-seed*.json")) File.Delete(old);
            string standalonePrefix = course.Token + "_";
            int seeds = 0;
            foreach (MacroDefinition route in _library.macros.Where(m => m.name != null
                && m.name.StartsWith(standalonePrefix, StringComparison.OrdinalIgnoreCase)
                && m.steps.All(s => s.type == MacroStepType.RawInput)))
            {
                string seed = Path.Combine(EngineSimDir, name + "-seed" + seeds++ + ".json");
                File.WriteAllText(seed, JsonConvert.SerializeObject(new { ticks = ToTicks(route.steps) }));
                args.Append(" --seed ").Append(Quote(seed));
            }
            int threads = Math.Max(1, Environment.ProcessorCount - 2);
            AppendOptional(args, _solveOptional).Append(" --threads ").Append(threads)
                .Append(" --minutes ").Append(_solveMinutes.ToString(CultureInfo.InvariantCulture))
                .Append(" --stall-minutes ").Append(Math.Min(5f, _solveMinutes / 2f).ToString(CultureInfo.InvariantCulture));
            var process = new Process { StartInfo = new ProcessStartInfo(EngineSimExe, "solve " + args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(EngineSimExe) } };
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) _solveOutput.Enqueue(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) _solveOutput.Enqueue(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _solveProcess = process;
            _solverStatus = "Solving offline on " + threads + " threads from " + seeds + " seed routes; turbo checks every new best";
            Logger.LogInfo("Optimize: " + EngineSimExe + " solve " + args);
        }

        static string Quote(string path) { return "\"" + path + "\""; }

        volatile bool _mapRunning;

        object EngineSimMap(WebStudioRequest request, JObject body)
        {
            int id = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef course = _courses.FirstOrDefault(c => c.id == id);
            if (course == null || (caps != "none" && CapsLevel(caps) == 0) || !course.HasCategory(caps))
            {
                request.StatusCode = 400;
                return new { ok = false, error = "Unknown course " + id + " or capabilities '" + caps + "'." };
            }
            string trace = new[] { "optimize-c", "start-c" }.Select(p => Path.Combine(EngineSimDir, p + course.Token + "-" + caps + ".json")).FirstOrDefault(File.Exists);
            if (trace == null)
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No start capture for " + course.Token + " " + caps + " yet: solve it once first." };
            }
            if (_mapRunning)
            {
                request.StatusCode = 409;
                return new { ok = false, error = "A map is already being drawn." };
            }
            string name = "map-c" + course.Token + "-" + caps;
            foreach (string old in Directory.GetFiles(EngineSimDir, name + "-seed*.json")) File.Delete(old);
            string output = Path.Combine(EngineSimDir, name + ".json");
            var args = new StringBuilder("map ").Append(Quote(trace)).Append(" --out ").Append(Quote(output));
            int seeds = 0;
            foreach (MacroDefinition route in _library.macros.Where(m => m.name != null
                && m.name.StartsWith(course.Token + "_", StringComparison.OrdinalIgnoreCase)
                && m.steps.All(s => s.type == MacroStepType.RawInput)))
            {
                string seed = Path.Combine(EngineSimDir, name + "-seed" + seeds++ + ".json");
                File.WriteAllText(seed, JsonConvert.SerializeObject(new { name = route.name, ticks = ToTicks(route.steps) }));
                args.Append(" --seed ").Append(Quote(seed));
            }
            AppendOptional(args, OptionalNames(body));
            _mapRunning = true;
            request.AsyncHandling = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (var process = new Process { StartInfo = new ProcessStartInfo(EngineSimExe, args.ToString()) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(EngineSimExe) } })
                    {
                        process.Start();
                        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                        var errors = process.StandardError.ReadToEndAsync();
                        process.StandardOutput.ReadToEnd();
                        process.WaitForExit();
                        if (process.ExitCode == 0) request.Response = File.ReadAllText(output);
                        else
                        {
                            request.StatusCode = 500;
                            string error = errors.Result.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "exit code " + process.ExitCode;
                            request.Response = JsonConvert.SerializeObject(new { ok = false, error = "map failed: " + error.Trim() });
                        }
                    }
                }
                catch (Exception ex)
                {
                    request.StatusCode = 500;
                    request.Response = JsonConvert.SerializeObject(new { ok = false, error = "map failed: " + ex.Message });
                }
                finally
                {
                    _mapRunning = false;
                    request.Completed.Set();
                }
            });
            return null;
        }

        static readonly Regex SolveProgress = new Regex(@"^\[[\d:]+\] (\d+) runs/s, (\d+) ticks/s, (\d+) runs, (\d+) finishes");
        static readonly Regex SolveBest = new Regex(@"offline best ([\d.,]+)s via");

        void ReadSolveLine(string line)
        {
            Match m = SolveProgress.Match(line);
            if (m.Success)
            {
                _solveRuns = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                _solveFinishes = long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                _solverSpeed = m.Groups[1].Value + " runs/s, " + m.Groups[2].Value + " ticks/s offline";
                return;
            }
            m = SolveBest.Match(line);
            if (m.Success) _solveBest = Math.Min(_solveBest, float.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
            if (line.Contains("turbo check of") && !line.Contains("deferred")) _turboChecks++;
            if (line.Contains("saved as the new route")) _turboSaved++;
            _solverLog.Add(line);
            Logger.LogInfo("solve: " + line);
        }

        const int JumpBufferTicks = 12;
        const int DashBufferTicks = 8;
        const int JumpCutBufferTicks = 15;
        List<string> _audit;

        void AuditTick(Movement player, int tick)
        {
            if (_audit.Count >= 5) return;
            int now = _engine.Clock;
            if (GetMovementBool(player, "jumpBuffer", false) && now - _engine.LastJumpPress > JumpBufferTicks)
                _audit.Add("tick " + tick + ": jump buffered with no jump press within the game's buffer window");
            if (GetMovementBool(player, "dashBuffer", false) && now - _engine.LastDashPress > DashBufferTicks)
                _audit.Add("tick " + tick + ": dash buffered with no dash press within the game's buffer window");
            if (GetMovementBool(player, "jumpCutBuffer", false) && now - _engine.LastJumpRelease > JumpCutBufferTicks)
                _audit.Add("tick " + tick + ": jump cut buffered with no jump release within the game's buffer window");
            string caps = _fullTas != null ? FullTasCaps : _solverCaps;
            int level = CapsLevel(caps);
            if (GetMovementBool(player, "wallJumpUnlocked", false) != (level >= 1)
                || GetMovementBool(player, "dashUnlocked", false) != (level >= 2)
                || !Equals(GetMovementField(player, "maxAirDashes"), level >= 2 ? 1 : 0)
                || GetMovementBool(player, "doubleJumpUnlocked", false) != (level >= 3)
                || !Equals(GetMovementField(player, "maxAirJumps"), level >= 3 ? 1 : 0))
                _audit.Add("tick " + tick + ": unlocks differ from category " + CapabilityLabels[caps]);
        }

        string RouteViolation(List<SolverTick> inputs, float time)
        {
            if (_solverFullTas != null) return FullTasViolation(inputs, time);
            var audit = _audit = new List<string>();
            bool finished;
            float replayed;
            try { finished = Evaluate(inputs, out replayed, out _, out _); }
            finally { _audit = null; }
            if (!finished || Mathf.Abs(replayed - time) > .0001f)
                audit.Insert(0, "a replay from the course start "
                    + (finished ? "finished in " + replayed.ToString("0.00") : "did not finish on " + _solverTarget.Token));
            return audit.Count == 0 ? null : string.Join("; ", audit);
        }

        bool Evaluate(List<SolverTick> inputs, out float time, out int finishTick, out Vector2 end)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot from = _startState, settling = null;
            CourseDef course = _solverTarget;
            Vector2 savedReset = _preservedCourseResetPoint;
            bool savedHasReset = _hasPreservedCourseResetPoint;
            _preservedCourseResetPoint = new Vector2(course.x, course.y);
            _hasPreservedCourseResetPoint = true;
            List<(string field, object value)> savedAbilities = ApplyAbilities(player, _solverCaps);
            _silentEvaluation = true;
            _engine.Quiet = true;
            _silentFinished = false;
            _silentTime = 0f;
            if (from == null)
            {
                foreach (courseScript c in _solverCourses) if (c != null) CourseTrackingFlag.SetValue(c, false);
                _pendingSilentMacro = new MacroDefinition { id = "solver", name = "Solver", steps = ToSteps(inputs) };
            }
            else
            {
                TurboRunner.Restore(from, player);
                _engine.Resume(from.engine, ToSteps(inputs), 0);
            }
            int lastTick = -1, idleTicks = 0;
            try
            {
                TurboRunner.Run(player, () => _pendingSilentMacro != null || (_engine.IsRunning && idleTicks < 20),
                    inputs.Count + 400, () =>
                    {
                        _engine.Clock++;
                        _engineSimTickHook?.Invoke(player);
                        if (!_engine.IsRunning) return;
                        if (_engine.InputsExhausted) idleTicks++;
                        int tick = (int)_engine.RunTicks;
                        if (_startState == null && tick == 0) settling = TurboRunner.Capture(player, _solverCourses, _engine.CaptureState(), 0);
                        if (_startState == null && tick > 0) { _startState = settling; _startOrigin = CurrentOrigin(); }
                        if (_audit != null) AuditTick(player, tick);
                        lastTick = tick;
                    }, from);
                if (_engine.IsRunning) _engine.Abort("Solver run ended");
            }
            finally
            {
                _pendingSilentMacro = null;
                _silentEvaluation = false;
                _engine.Quiet = false;
                _preservedCourseResetPoint = savedReset;
                _hasPreservedCourseResetPoint = savedHasReset;
                foreach (var (field, value) in savedAbilities) SetMovementField(player, field, value);
            }
            time = _silentTime;
            finishTick = Math.Min(inputs.Count - 1, Math.Max(0, lastTick));
            end = player.transform.position;
            return _silentFinished && time > 0f;
        }

        string SolverCategory { get { return _solverFullTas != null ? FullTasCategory(_solverFullTas.name)
            : _solverTarget.Token + "_" + CapabilityLabels[_solverCaps]; } }

        // Farm routes: a course's fewest physics ticks from the route start (the quick restart) to the finish. Bonuses and
        // transitions are timed in those ticks already, and Full-TAS runs have no laps, so only courses keep one.
        bool FarmTracked { get { return _solverFullTas == null && !_solverTarget.IsBonus && !_solverTarget.IsTransition; } }

        static string FarmSeconds(long ticks) { return (ticks * (double)Time.fixedDeltaTime).ToString("0.00", CultureInfo.InvariantCulture); }

        static long FarmTicks(float seconds) { return (long)Math.Round(seconds / Time.fixedDeltaTime); }

        bool SaveSolverRoute(List<SolverTick> inputs, float time, long farmTicks = 0)
        {
            string category = SolverCategory;
            string name = category + (farmTicks > 0 ? " farm " + FarmSeconds(farmTicks) : " " + time.ToString("0.00", CultureInfo.InvariantCulture));
            string violation = RouteViolation(inputs, time);
            if (violation == null && farmTicks > 0 && _engine.PhysicsTicks != farmTicks)
                violation = "a replay from the course start took " + _engine.PhysicsTicks + " physics ticks, not " + farmTicks;
            if (violation != null)
            {
                string line = "REJECTED " + name + ": " + violation;
                _solverLog.Add(line);
                Logger.LogWarning(line);
                return false;
            }
            var route = new MacroDefinition { name = name, steps = ToSteps(inputs) };
            _library.macros.Add(route);
            PruneSlowerRoutes();
            SaveLibrary();
            return true;
        }

        static readonly Regex CategoryRoute = new Regex(@"^([^_\s]+_(?:NONE|WJ_DS_DJ|WJ_DS|WJ)) ([\d.]+)$", RegexOptions.IgnoreCase);
        static readonly Regex FarmRoute = new Regex(@"^([^_\s]+_(?:NONE|WJ_DS_DJ|WJ_DS|WJ)) farm ([\d.]+)$", RegexOptions.IgnoreCase);

        // A record route ranks by its name's game time, a farm route by its name's farm time; each kind keeps its own best.
        static bool RankedRoute(string name, out string key, out float time)
        {
            Match g = CategoryRoute.Match(name ?? "");
            bool farm = !g.Success && (g = FarmRoute.Match(name ?? "")).Success;
            key = g.Success ? g.Groups[1].Value.ToUpperInvariant() + (farm ? " FARM" : "") : null;
            time = g.Success ? float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture) : 0f;
            return g.Success;
        }

        bool PruneSlowerRoutes()
        {
            var fastest = new Dictionary<string, float>();
            foreach (MacroDefinition m in _library.macros)
                if (RankedRoute(m.name, out string key, out float time) && (!fastest.TryGetValue(key, out float best) || time < best)) fastest[key] = time;
            var kept = new Dictionary<string, MacroDefinition>();
            foreach (MacroDefinition m in _library.macros)
                if (RankedRoute(m.name, out string key, out float time) && time <= fastest[key]) kept[key] = m;
            int removed = _library.macros.RemoveAll(m => RankedRoute(m.name, out string key, out _) && kept[key] != m);
            _library.selectedIndex = Mathf.Clamp(_library.selectedIndex, 0, Math.Max(0, _library.macros.Count - 1));
            return removed > 0;
        }

        static List<SolverTick> ToTicks(List<MacroStep> steps)
        {
            var ticks = new List<SolverTick>();
            foreach (MacroStep s in steps)
                for (int i = 0; i < Math.Max(1, s.durationTicks); i++)
                    ticks.Add(new SolverTick { x = s.rawMoveX, y = s.rawMoveY, held = s.rawJumpHeld,
                        press = i == 0 && s.rawJumpPressed, release = i == 0 && s.rawJumpReleased, dash = i == 0 && s.rawDashPressed,
                        dashJump = i == 0 && s.rawDashPressed ? s.dashJumpFrame : 0, springDash = i == 0 ? s.springDashFrame : 0,
                        lockX = i == 0 ? s.lockMoveX : null, lockY = i == 0 ? s.lockMoveY : null,
                        reset = i == 0 && s.rawQuickRestart });
            return ticks;
        }

        static List<MacroStep> ToSteps(List<SolverTick> ticks)
        {
            var steps = new List<MacroStep>();
            foreach (SolverTick k in ticks)
            {
                MacroStep last = steps.Count > 0 ? steps[steps.Count - 1] : null;
                bool arms = k.springDash != 0 || k.lockX.HasValue || k.lockY.HasValue;
                bool lastArms = last != null && (last.springDashFrame != 0 || last.lockMoveX.HasValue || last.lockMoveY.HasValue);
                if (last != null && !k.press && !k.release && !k.dash && !k.reset && !arms && !lastArms
                    && last.rawMoveX == k.x && last.rawMoveY == k.y && last.rawJumpHeld == k.held)
                {
                    last.durationTicks++;
                    last.durationMs = Mathf.RoundToInt(last.durationTicks * Time.fixedDeltaTime * 1000f);
                    continue;
                }
                steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1,
                    durationMs = Mathf.RoundToInt(Time.fixedDeltaTime * 1000f), endCondition = ActionEndCondition.FixedTicks,
                    direction = k.x > 0f ? 1 : k.x < 0f ? -1 : 0, rawMoveX = k.x, rawMoveY = k.y, rawJumpHeld = k.held,
                    rawJumpPressed = k.press, rawJumpReleased = k.release, rawDashPressed = k.dash,
                    dashJumpFrame = k.dash ? k.dashJump : 0, springDashFrame = k.springDash, lockMoveX = k.lockX, lockMoveY = k.lockY,
                    rawQuickRestart = k.reset });
            }
            return steps;
        }
    }
}