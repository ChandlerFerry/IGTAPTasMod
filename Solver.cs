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
            public bool overgrown;
            [JsonIgnore] public int GameCourse { get { return overgrown ? id - 10 : id; } }
            [JsonIgnore] public bool HasFinish { get { return endW > 0f && endH > 0f; } }
            [JsonIgnore] public bool IsTransition { get { return !string.IsNullOrEmpty(to); } }
            [JsonIgnore] public bool IsBonus { get { return HasFinish && !IsTransition; } }
            [JsonIgnore] public string Token { get { return string.IsNullOrEmpty(name) ? id.ToString() : name; } }
            /// <summary>The lowest <see cref="Categories.Ladder"/> step it runs (courses.json's minCapabilities; none when unset).</summary>
            [JsonIgnore] public int MinStep { get { return Math.Max(0, Categories.LadderStep(minCapabilities)); } }
            /// <summary>The highest ladder step it runs (README ### Rules 17-18): maxCapabilities, a bonus's own step, or up to
            /// the ladder's top when it's unset.</summary>
            [JsonIgnore] public int MaxStep { get { return !string.IsNullOrEmpty(maxCapabilities) ? Math.Max(0, Categories.LadderStep(maxCapabilities)) : IsBonus ? MinStep : Categories.Ladder.Length - 1; } }
            /// <summary>The category is run here (README ### Rules 17-19, 26): <see cref="CategoryDef.RunsOn"/>.</summary>
            public bool HasCategory(string idOrLabel)
            {
                CategoryDef def = Categories.Find(idOrLabel);
                return def != null && Categories.IsTarget(Token) && def.RunsOn(Token, MinStep, MaxStep);
            }
            /// <summary>Every category it runs, in registry order.</summary>
            [JsonIgnore] public string[] CategoryIds { get { return Categories.All.Where(d => HasCategory(d.Id)).Select(d => d.Id).ToArray(); } }
        }

        sealed class SolverTick
        {
            public float x, y; public bool held, press, release, dash; public int dashJump;
            public int springDash;
            public float? lockX, lockY;
            public float? turnX, turnY;
            public bool reset;
            /// <summary>MacroStep.pauseFrames / pauseTick / pauseFrame.</summary>
            public int pause, pauseTick, pauseFrame;
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
                new CourseDef { id = 1, x = 2241f, y = -292f, minCapabilities = "none", maxCapabilities = "wjdsdj" },
                new CourseDef { id = 2, x = 4783.43359f, y = 1878.2251f, minCapabilities = "none", maxCapabilities = "wjdsdj" },
                new CourseDef { id = 3, x = 9505f, y = 6967.01465f, minCapabilities = "wj", maxCapabilities = "wjdsdj" },
                new CourseDef { id = 4, x = 20200.4004f, y = 10554.5449f, minCapabilities = "wjds", maxCapabilities = "wjdsdj" },
                new CourseDef { id = 5, x = -3281f, y = -295.455078f, minCapabilities = "wjdsdj", maxCapabilities = "wjdsdj" },
                new CourseDef { id = 6, x = 26174.6016f, y = -476.427734f, minCapabilities = "wjdsdjbs" },
                new CourseDef { id = 7, x = 19891f, y = 3039f, minCapabilities = "wjdsdjbs" },
                new CourseDef { id = 8, x = 26495.6016f, y = -2372.42773f, minCapabilities = "wjdsdjbs" },
                new CourseDef { id = 9, x = 17138.6016f, y = 6648.11475f, minCapabilities = "wjdsdjbs" },
                new CourseDef { id = 10, x = 30186.6016f, y = 5792.57227f, minCapabilities = "wjdsdjbs" },
                new CourseDef { id = 11, x = 2241f, y = -292f, minCapabilities = "wjdsdjbs", overgrown = true },
                new CourseDef { id = 12, x = 4783.43359f, y = 1878.2251f, minCapabilities = "wjdsdjbs", overgrown = true },
                new CourseDef { id = 13, x = 9505f, y = 6967.01465f, minCapabilities = "wjdsdjbs", overgrown = true },
                new CourseDef { id = 14, x = 20200.4004f, y = 10554.5449f, minCapabilities = "wjdsdjbs", overgrown = true },
                new CourseDef { id = 15, x = -3281f, y = -295.455078f, minCapabilities = "wjdsdjbs", overgrown = true } };
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

        static readonly Regex TransitionName = new Regex(@"^(Course|Bonus)(\d+)_To_(Course|Bonus)(\d+)(?:_(?<label>" + Caps.LabelPattern + @"))?\b", RegexOptions.IgnoreCase);
        const float BonusArrivalSize = 32f;

        /// <summary>The transition this name is ("Course1_To_Course2", optionally with _WJ_DS), or empty when its category part
        /// is no category ("Course1_To_Course2_almost" names none).</summary>
        static Match MatchTransitionName(string name)
        {
            Match m = TransitionName.Match(name ?? "");
            return !m.Groups["label"].Success || Categories.IsCategory(m.Groups["label"].Value.ToUpperInvariant()) ? m : Match.Empty;
        }

        object AddTransitionFromRecording(RecordingDefinition recording)
        {
            if (recording.samples == null || recording.samples.Count == 0) throw new InvalidOperationException("The recording is empty.");
            Match m = MatchTransitionName(recording.name);
            if (!m.Success)
                throw new InvalidOperationException("Name the recording \"Course1_To_Course2\" or \"Course2_To_Bonus2\" (optionally with _WJ, _WJ_DS, ...) first.");
            bool fromBonus = m.Groups[1].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase);
            bool toBonus = m.Groups[3].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase);
            string origin = (fromBonus ? "bonus" : "") + m.Groups[2].Value, target = (toBonus ? "bonus" : "") + m.Groups[4].Value;
            if (!fromBonus && IsOvergrownCourse(origin) || !toBonus && IsOvergrownCourse(target))
                throw new InvalidOperationException("Transitions to or from the overgrown Courses 11-15 aren't supported.");
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
                // Its min is always a ladder step (README ### Rules 18): the name's category when it is one of them, else the
                // largest the save's unlocks have (a course's, capped at the ladder's top).
                CategoryDef named = !m.Groups["label"].Success ? null : Categories.Find(m.Groups["label"].Value.ToUpperInvariant());
                string step = named != null && named.Scope == CategoryScope.Range ? named.Id
                    : player == null ? default(Caps).Id : LadderStepFor(SaveUnlocks(player));
                transition = new CourseDef { id = id, name = origin + "to" + target, x = start.x, y = start.y, from = origin, to = target,
                    minCapabilities = step };
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
                Match m = MatchTransitionName(r.name);
                if (!m.Success || r.samples == null || r.samples.Count == 0 || !m.Groups[1].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase)
                    || "bonus" + m.Groups[2].Value != bonus) continue;
                start = Absolute(r.samples[0]) + new Vector2(0f, 12f);
                return true;
            }
            start = Vector2.zero;
            return false;
        }

        void CheckCategory(int course, string capabilities)
        {
            CourseDef def = _courses.Find(c => c.id == course);
            if (def == null || def.HasCategory(capabilities)) return;
            throw new InvalidOperationException((def.IsBonus ? "Bonus " : "Course ") + def.Token + " has no " + Caps.Parse(capabilities).Label
                + " category; it is run in " + string.Join(", ", def.CategoryIds.Select(Caps.Parse).Select(c => c.Label)) + ".");
        }

        static readonly MethodInfo LoadZoneNow = AccessTools.Method(typeof(ZoneLoader), "_LoadZone");
        List<(startGate gate, int zone)> _gateZones = new List<(startGate, int)>();

        void EnsureZone(CourseDef def)
        {
            int zone = TargetZone(def, out ZoneLoader loader);
            if (zone == 0 || zone == loader.activeZone && ZonesSettled()) return;
            LoadZoneNow.Invoke(loader, new object[] { zone });
            Logger.LogInfo("Loaded zone " + zone + " for " + def.Token + ".");
        }

        /// <summary>Whether the live world has the course's zone loaded (and only it), as its start state was captured in.</summary>
        bool ZoneReady(CourseDef def)
        {
            int zone = TargetZone(def, out ZoneLoader loader);
            return zone == 0 || zone == loader.activeZone && ZonesSettled();
        }

        /// <summary>The zone of the start gate nearest the course's start; 0 when there is no zone loader or no gate nearby.</summary>
        int TargetZone(CourseDef def, out ZoneLoader loader)
        {
            loader = FindAnyObjectByType<ZoneLoader>();
            if (def == null || loader == null || !(AllZonesField?.GetValue(loader) is GameObject[] zones)) return 0;
            if (_gateZones.Count == 0 || _gateZones.Exists(g => g.gate == null)) _gateZones = GateZones(zones);
            Vector2 start = new Vector2(def.x, def.y) + CurrentOrigin();
            int zone = 0;
            float nearest = 1000f * 1000f;
            foreach (var (gate, gateZone) in _gateZones)
            {
                float d = ((Vector2)gate.transform.position - start).sqrMagnitude;
                if (d < nearest) { nearest = d; zone = gateZone; }
            }
            return zone;
        }

        bool IsOvergrownCourse(string token) { return int.TryParse(token, out int id) && _courses.Exists(c => c.id == id && c.overgrown); }

        globalStats.area1states? _savedArea1StateValue;
        globalStats _savedArea1StateOwner;

        globalStats.area1states? SavedArea1State
        {
            get { return _savedArea1StateOwner != null && _savedArea1StateOwner == Singleton<globalStats>.Instance ? _savedArea1StateValue : null; }
        }

        void EnsureArea1State(CourseDef def)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if (def == null || stats == null) return;
            globalStats.area1states want = WantedArea1State(def, stats, out globalStats.area1states own);
            _savedArea1StateValue = want == own ? (globalStats.area1states?)null : own;
            _savedArea1StateOwner = want == own ? null : stats;
            if (stats.currentA1State != want) SetArea1State(stats, want, def.Token);
        }

        globalStats.area1states WantedArea1State(CourseDef def, globalStats stats, out globalStats.area1states own)
        {
            own = SavedArea1State ?? stats.currentA1State;
            if (def.overgrown) return globalStats.area1states.Overgrown;
            if (!def.IsBonus && !def.IsTransition && def.GameCourse >= 1 && def.GameCourse <= 5 && own == globalStats.area1states.Overgrown)
                return globalStats.area1states.normal;
            return own;
        }

        bool Area1StateReady(CourseDef def)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            return def == null || stats == null || stats.currentA1State == WantedArea1State(def, stats, out _);
        }

        void RestoreArea1State()
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if (SavedArea1State is globalStats.area1states own && stats.currentA1State != own) SetArea1State(stats, own, "the save");
            _savedArea1StateValue = null;
            _savedArea1StateOwner = null;
        }

        void SetArea1State(globalStats stats, globalStats.area1states state, string reason)
        {
            bool wasOvergrown = stats.currentA1State == globalStats.area1states.Overgrown, overgrown = state == globalStats.area1states.Overgrown;
            stats.currentA1State = state;
            OvergrowthLoadScript overgrowth = FindAnyObjectByType<OvergrowthLoadScript>(FindObjectsInactive.Include);
            if (overgrowth != null) overgrowth.OnLoad();
            else Logger.LogWarning("No OvergrowthLoadScript: Area 1's terrain was not switched.");
            if (wasOvergrown != overgrown) SwapArea1CourseBests();
            Logger.LogInfo("Area 1 " + state + " for " + reason + ".");
        }

        static readonly FieldInfo CourseBestPathTimeField = AccessTools.Field(typeof(courseScript), "bestPathTime");
        static readonly FieldInfo CourseClonesField = AccessTools.Field(typeof(courseScript), "clones");

        sealed class CourseBest
        {
            public float time, pathLength;
            public Vector2[] path, scales;
            public Sprite[] sprites;
            public Vector2 endVelocity;

            public static CourseBest Read(courseScript course, clonesScript clones)
            {
                return new CourseBest { time = (float)CourseBestPathTimeField.GetValue(course), pathLength = clones.pathLength, path = clones.clonePath,
                    scales = clones.cloneScales, sprites = clones.cloneSprites, endVelocity = clones.cloneEndVelocity };
            }

            public CourseBest Reset() { var b = (CourseBest)MemberwiseClone(); b.time = 1E+11f; b.pathLength = 1E+16f; return b; }

            public void Apply(courseScript course, clonesScript clones)
            {
                if (path != null && path.Length >= 10) clones.updateClonePath(path, sprites, scales, pathLength);
                else { clones.clonePath = path; clones.cloneSprites = sprites; clones.cloneScales = scales; clones.pathLength = pathLength; }
                clones.cloneEndVelocity = endVelocity;
                clones.updateCloneInterval();
                CourseBestPathTimeField.SetValue(course, time);
                course.updateStringLocalisations();
            }
        }

        readonly Dictionary<courseScript, CourseBest> _otherArea1Bests = new Dictionary<courseScript, CourseBest>();

        void SwapArea1CourseBests()
        {
            foreach (courseScript course in FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (course.courseNumber < 1 || course.courseNumber > 5 || !(CourseClonesField.GetValue(course) is clonesScript clones) || clones == null) continue;
                CourseBest current = CourseBest.Read(course, clones);
                CourseBest other = _otherArea1Bests.TryGetValue(course, out CourseBest kept) ? kept : current.Reset();
                _otherArea1Bests[course] = current;
                other.Apply(course, clones);
            }
        }

        static List<(startGate, int)> GateZones(GameObject[] zones)
        {
            var result = new List<(startGate, int)>();
            foreach (startGate gate in FindObjectsByType<startGate>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                for (Transform t = gate.transform; t != null; t = t.parent)
                {
                    int i = Array.FindIndex(zones, z => z != null && (z == t.gameObject
                        || z.GetComponent<ZoneDisableLoadList>()?.toLoad?.Contains(t.gameObject) == true));
                    if (i < 0) continue;
                    result.Add((gate, i + 1));
                    break;
                }
            return result;
        }

        static string EngineSimExe
        {
            get { return typeof(TasPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "EngineSimExe")?.Value; }
        }

        SolveJob CourseJob(int course, string capabilities, string[] optional)
        {
            CourseDef def = _courses.Find(c => c.id == course);
            if (def == null) throw new InvalidOperationException("Unknown course " + course + ".");
            if (!Categories.IsCategory(capabilities ?? "")) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, capabilities);
            string[] known = OptionalBlocks(course).Where(b => b.caps.Contains(capabilities)).Select(b => b.name).ToArray();
            string missing = optional.FirstOrDefault(n => !known.Contains(n));
            if (missing != null) throw new InvalidOperationException("No optional block '" + missing + "' for " + def.Token + " " + capabilities + ".");
            return new SolveJob { course = course, caps = capabilities, optional = optional,
                label = def.Token + "_" + Caps.Parse(capabilities).Label + (optional.Length == 0 ? "" : " +" + string.Join(" +", optional)) };
        }

        // The body's picked optional blocks, or the course's default-on blocks when it names none.
        string[] OptionalNames(JObject body, int course, string capabilities)
        {
            IEnumerable<string> names = body["optional"] is JArray picked ? picked.Select(n => (string)n).Where(n => !string.IsNullOrEmpty(n))
                : OptionalBlocks(course).Where(b => b.on && b.caps.Contains(capabilities)).Select(b => b.name);
            return names.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }

        // The engine turns default-on blocks on by itself, so the ones left unpicked go as `--no-optional`.
        StringBuilder AppendOptional(StringBuilder args, int course, string capabilities, string[] optional)
        {
            foreach (string name in optional) args.Append(" --optional ").Append(Quote(name));
            foreach (OptionalBlock block in OptionalBlocks(course).Where(b => b.on && b.caps.Contains(capabilities) && !optional.Contains(b.name)))
                args.Append(" --no-optional ").Append(Quote(block.name));
            return args;
        }

        sealed class OptionalBlock
        {
            public int course;
            public string name, note;
            public bool on;
            public string[] caps;
        }

        OptionalBlock[] _optionalBlocks = new OptionalBlock[0];
        DateTime _optionalBlocksRead;
        // forced-paths.json is looked at once a second at most (the studio's courses ask for every course); EditForcedPaths,
        // the plugin's own writer, makes the next call look again.
        static float _optionalBlocksCheckAt = float.NegativeInfinity;

        IEnumerable<OptionalBlock> OptionalBlocks(int course)
        {
            if (Time.unscaledTime >= _optionalBlocksCheckAt)
            {
                _optionalBlocksCheckAt = Time.unscaledTime + 1f;
                string exe = EngineSimExe;
                string file = string.IsNullOrEmpty(exe) ? null : Path.Combine(Path.GetDirectoryName(exe), "forced-paths.json");
                DateTime stamp = file != null && File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default(DateTime);
                if (stamp != _optionalBlocksRead)
                {
                    _optionalBlocksRead = stamp;
                    _optionalBlocks = stamp == default(DateTime) ? new OptionalBlock[0]
                        : ((JArray)JObject.Parse(File.ReadAllText(file))["paths"]).OfType<JObject>().Where(rule => rule["course"] != null)
                            .SelectMany(rule => (rule["optional"] as JArray ?? new JArray()).OfType<JObject>().Select(b => new OptionalBlock {
                                course = (int)rule["course"], name = (string)b["name"], note = (string)b["note"] ?? "", on = (bool?)b["default"] ?? false,
                                caps = rule["capabilities"].Select(c => (string)c).ToArray() })).ToArray();
                }
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
            List<SolveJob> jobs = items.Select(i => { int course = Number(i, "course", 0); string caps = Text(i, "capabilities"); return CourseJob(course, caps, OptionalNames(i, course, caps)); }).ToList();
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

        int _parkedFrame = -1000, _airborneFrame = -1;

        bool CourseLoaded()
        {
            _solverCourses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            Movement player = FindWorldMovement();
            CourseDef target = _solverTarget;
            Vector2 start = new Vector2(target.x, target.y) + CurrentOrigin();
            bool parked = player != null && ((Vector2)player.transform.position - start).sqrMagnitude < 64f * 64f && Area1StateReady(target);
            // respawn keeps coyoteFrames and the stable-tick wait pins the player at the reset point (Course 7's is 42 u over its
            // floor), where only coyote time holds onGround: restart from standing (full coyote), or a run that ended airborne
            // never gets 3 stable ticks, or starts with other coyote frames than the trace. Starts that never land: 600 frames.
            bool grounded = player != null && player.onGround && Convert.ToSingle(GetMovementField(player, "coyoteFrames")) >= player.maxCoyoteFrames;
            if (!parked)
            {
                RestoreRouteStart(new MacroDefinition { name = target.Token + "_" });
                _parkedFrame = Time.frameCount;
                _airborneFrame = -1;
            }
            else
            {
                if (grounded) _airborneFrame = -1;
                else if (_airborneFrame < 0) _airborneFrame = Time.frameCount;
                if (Time.frameCount - _parkedFrame >= 10 && (grounded || Time.frameCount - _airborneFrame >= 600) && ZonesSettled() && (target.HasFinish
                    || _solverCourses.Any(c => c.courseNumber == (target.IsTransition ? target.toCourse : target.GameCourse) && c.isActiveAndEnabled)))
                {
                    if (!grounded) Logger.LogWarning("Course " + target.Token + ": starting without the player standing at its reset point (it never landed there).");
                    _airborneFrame = -1;
                    return true;
                }
            }
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
            return _courses.Select(c => (object)new { id = c.id, token = c.Token, bonus = c.IsBonus,
                categories = c.CategoryIds, @from = c.from, to = c.to,
                hint = c.IsBonus ? BonusHint(c) : c.overgrown ? "Course " + c.GameCourse + " with Area 1 overgrown (overgrowth spikes, moss, zip movers)." : null,
                optional = OptionalBlocks(c.id).Select(b => new { name = b.name, note = b.note, on = b.on, capabilities = b.caps }).ToArray() }).ToArray();
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

        /// <summary>Why the last start capture failed (engine status and the player's state at the end), for the caller's error.</summary>
        string _captureFailure;
        /// <summary>A tech demo's own start for the next start capture (EngineSimTechTrace): its savestate and the omnidirectional dash.</summary>
        TechDef _techStart;

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
            if (!CapabilitiesLocked) ApplyAbilities(FindWorldMovement(), _solveCaps);
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
            AppendOptional(args, _solveCourse, _solveCaps, _solveOptional).Append(" --threads ").Append(threads)
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

        // One job per map file: a request for a map already being drawn (a second click, a page's second mount) waits for
        // that job's result instead of failing, and different maps queue, one engine process at a time.
        sealed class MapJob
        {
            public readonly System.Threading.ManualResetEvent Done = new System.Threading.ManualResetEvent(false);
            public int Status = 200;
            public string Response;
        }
        readonly Dictionary<string, MapJob> _mapJobs = new Dictionary<string, MapJob>(StringComparer.OrdinalIgnoreCase);
        readonly object _mapQueue = new object();

        bool MapInFlight(string output)
        {
            lock (_mapJobs) return _mapJobs.ContainsKey(output);
        }

        object EngineSimMap(WebStudioRequest request, JObject body)
        {
            int id = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef course = _courses.FirstOrDefault(c => c.id == id);
            if (course == null || !course.HasCategory(caps))
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
            string name = "map-c" + course.Token + "-" + caps;
            string output = Path.Combine(EngineSimDir, name + ".json");
            // This map's job is running or queued: join it, leaving its seed files alone.
            if (MapInFlight(output)) return RunEngineSimMap(request, null, output);
            foreach (string old in Directory.GetFiles(EngineSimDir, name + "-seed*.json")) File.Delete(old);
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
            AppendOptional(args, id, caps, OptionalNames(body, id, caps));
            return RunEngineSimMap(request, args.ToString(), output);
        }

        // The Ultra map: every floor a hyper lands on in the VMAN Full-TAS run's world (the Vman speedrun's start, the
        // only place with the omnidirectional dash) and the ultras (the game's "Did an ultra!" hyper) two chained
        // wavedashes reach from launch points along them. Served from the last result unless `refresh`.
        object EngineSimUltras(WebStudioRequest request, JObject body)
        {
            string trace = FullTasTracePath("VMAN");
            if (!File.Exists(trace))
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No VMAN trace yet: capture the VMAN Full-TAS run's start or solve it once first." };
            }
            string output = Path.Combine(EngineSimDir, "ultras-VMAN.json");
            if (File.Exists(output) && !Truthy(body, "refresh"))
            {
                request.AsyncHandling = true;
                request.Response = File.ReadAllText(output);
                request.Completed.Set();
                return null;
            }
            return RunEngineSimMap(request, "ultras " + Quote(trace) + " --courses " + Quote(CoursesPath) + " --out " + Quote(output), output);
        }

        // Draws `output` with the engine (`args`), or joins the job already drawing it (`args` unused then).
        object RunEngineSimMap(WebStudioRequest request, string args, string output)
        {
            MapJob job;
            bool owner;
            lock (_mapJobs)
            {
                owner = !_mapJobs.TryGetValue(output, out job);
                if (owner) _mapJobs[output] = job = new MapJob();
            }
            request.AsyncHandling = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                if (owner)
                {
                    try { lock (_mapQueue) RunMapProcess(job, args, output); }
                    finally
                    {
                        lock (_mapJobs) _mapJobs.Remove(output);
                        job.Done.Set();
                    }
                }
                else job.Done.WaitOne();
                request.StatusCode = job.Status;
                request.Response = job.Response;
                request.Completed.Set();
            });
            return null;
        }

        void RunMapProcess(MapJob job, string args, string output)
        {
            try
            {
                using (var process = new Process { StartInfo = new ProcessStartInfo(EngineSimExe, args) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(EngineSimExe) } })
                {
                    process.Start();
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                    var errors = process.StandardError.ReadToEndAsync();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0) job.Response = File.ReadAllText(output);
                    else
                    {
                        job.Status = 500;
                        string error = errors.Result.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "exit code " + process.ExitCode;
                        job.Response = JsonConvert.SerializeObject(new { ok = false, error = "map failed: " + error.Trim() });
                    }
                }
            }
            catch (Exception ex)
            {
                job.Status = 500;
                job.Response = JsonConvert.SerializeObject(new { ok = false, error = "map failed: " + ex.Message });
            }
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
        // Movement.getPlayerControlledInput holds a buffered dash through a wall jump's first 5 lock ticks: while
        // wallJumpMovementLockRemaining >= wallJumpMovementLockFrames - 5 it re-arms Invoke("cancelDashBuffer", 0.06f) each
        // tick, so a press's buffer outlives its 0.12 s. After the last hold it lives 0.06 s = 3 ticks, +1 (the hold runs on
        // the next tick) +2 slack.
        const int DashHoldTicks = 6;
        const int JumpCutBufferTicks = 15;
        List<string> _audit;
        // MacroEngine.Clock of the last audited tick after which the game held a press's dash buffer (DashHoldTicks).
        int _dashHeldAt;

        List<string> StartAudit()
        {
            _dashHeldAt = int.MinValue / 2;
            return _audit = new List<string>();
        }

        /// <summary>The next tick's getPlayerControlledInput holds the buffered dash (its first check, Movement.cs 1596).</summary>
        static bool DashBufferHeld(Movement player)
        {
            return player.dashCooldown < 0f && GetMovementBool(player, "dashUnlocked", false)
                && !(player.wallJumpMovementLockRemaining < player.wallJumpMovementLockFrames - 5f);
        }

        void AuditTick(Movement player, int tick)
        {
            if (_audit.Count >= 5) return;
            int now = _engine.Clock;
            if (GetMovementBool(player, "jumpBuffer", false) && now - _engine.LastJumpPress > JumpBufferTicks)
                _audit.Add("tick " + tick + ": jump buffered with no jump press within the game's buffer window");
            if (GetMovementBool(player, "dashBuffer", false))
            {
                if (now - _engine.LastDashPress > DashBufferTicks && now - _dashHeldAt > DashHoldTicks)
                    _audit.Add("tick " + tick + ": dash buffered with no dash press within the game's buffer window");
                else if (DashBufferHeld(player)) _dashHeldAt = now;
            }
            if (GetMovementBool(player, "jumpCutBuffer", false) && now - _engine.LastJumpRelease > JumpCutBufferTicks)
                _audit.Add("tick " + tick + ": jump cut buffered with no jump release within the game's buffer window");
            if (_fullTas != null)
            {
                FullTasUnlocks u = _fullTasUnlocks;
                if (GetMovementBool(player, "wallJumpUnlocked", false) != u.wallJump
                    || GetMovementBool(player, "dashUnlocked", false) != u.dash
                    || !Equals(GetMovementField(player, "maxAirDashes"), u.airDashes)
                    || GetMovementBool(player, "omniDashUnlocked", false) != u.omniDash
                    || GetMovementBool(player, "doubleJumpUnlocked", false) != u.doubleJump
                    || !Equals(GetMovementField(player, "maxAirJumps"), u.airJumps)
                    || GetMovementBool(player, "blockSwapUnlocked", false) != u.blockSwap)
                    _audit.Add("tick " + tick + ": unlocks differ from the run's purchases (" + u + ")");
                return;
            }
            // The unlocks must match the category exactly on every tick (README ### Rules 10): the expected values come from
            // the same list its start applies, less what a run changes on its way (CategoryRunState).
            if (CategoryAbilities(_solverCaps).Any(a => !CategoryRunState.Contains(a.field) && !Equals(GetMovementField(player, a.field), a.value)))
                _audit.Add("tick " + tick + ": unlocks differ from category " + Caps.Parse(_solverCaps).Label);
        }

        string RouteViolation(List<SolverTick> inputs, float time)
        {
            if (_solverFullTas != null) return FullTasViolation(inputs, time);
            var audit = StartAudit();
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
            if (_techStart != null && _techStart.omniDash)
            {
                savedAbilities.Add(("omniDashUnlocked", GetMovementField(player, "omniDashUnlocked")));
                SetMovementField(player, "omniDashUnlocked", true);
            }
            _silentEvaluation = true;
            _engine.Quiet = true;
            _silentFinished = false;
            _silentTime = 0f;
            if (from == null)
            {
                foreach (courseScript c in _solverCourses) if (c != null) CourseTrackingFlag.SetValue(c, false);
                _pendingSilentMacro = new MacroDefinition { id = "solver", name = "Solver", steps = ToSteps(inputs),
                    savestateId = _techStart == null ? null : TechRoute(_techStart)?.savestateId };
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
                        if (_startState == null && tick > 0) _startState = settling;
                        if (_audit != null) AuditTick(player, tick);
                        lastTick = tick;
                    }, from);
                if (from == null && _startState == null)
                    _captureFailure = "engine '" + _engine.Status + "', dead " + GetMovementField(player, "isDead") + ", onGround " + GetMovementField(player, "onGround")
                        + ", coyote " + GetMovementField(player, "coyoteFrames") + ", cutscene " + GetMovementField(player, "cutsceneMode")
                        + ", at " + ((Vector2)player.transform.position - CurrentOrigin());
            }
            finally
            {
                // Also when the run throws: a resumed engine left running would play the inputs on in real time.
                if (_engine.IsRunning) _engine.Abort("Solver run ended");
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
            : _solverTarget.Token + "_" + Caps.Parse(_solverCaps).Label; } }

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

        // A route name's category part ("4_WJ_DS"): the label is matched in any case but must be one of the categories, so
        // "4_almost_completed 13.46" is no route.
        static readonly Regex CategoryRoute = new Regex(@"^(?<category>(?<token>[^_\s]+)_(?<label>" + Caps.LabelPattern + @")) (?<time>[\d.]+)$", RegexOptions.IgnoreCase);
        static readonly Regex FarmRoute = new Regex(@"^(?<category>(?<token>[^_\s]+)_(?<label>" + Caps.LabelPattern + @")) farm (?<time>[\d.]+)$", RegexOptions.IgnoreCase);
        static readonly Regex RoutePrefix = new Regex(@"^(?<category>(?<token>[^_\s]+)_(?<label>" + Caps.LabelPattern + @"))(?: |$)", RegexOptions.IgnoreCase);

        static Match MatchRoute(Regex pattern, string name)
        {
            Match m = pattern.Match(name ?? "");
            return m.Success && Categories.IsCategory(m.Groups["label"].Value.ToUpperInvariant()) ? m : Match.Empty;
        }
        static Match MatchCategoryRoute(string name) { return MatchRoute(CategoryRoute, name); }
        static Match MatchFarmRoute(string name) { return MatchRoute(FarmRoute, name); }
        static Match MatchRoutePrefix(string name) { return MatchRoute(RoutePrefix, name); }

        static bool RankedRoute(string name, out string key, out float time)
        {
            Match g = MatchCategoryRoute(name);
            bool farm = !g.Success && (g = MatchFarmRoute(name)).Success;
            key = g.Success ? g.Groups["category"].Value.ToUpperInvariant() + (farm ? " FARM" : "") : null;
            time = g.Success ? float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture) : 0f;
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
                        turnX = i == 0 && s.rawDashPressed ? s.dashTurnX : null, turnY = i == 0 && s.rawDashPressed ? s.dashTurnY : null,
                        reset = i == 0 && s.rawQuickRestart, pause = i == 0 ? s.pauseFrames : 0, pauseTick = i == 0 ? s.pauseTick : 0,
                        pauseFrame = i == 0 ? s.pauseFrame : 0 });
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
                if (last != null && !k.press && !k.release && !k.dash && !k.reset && k.pause == 0 && !arms && !lastArms
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
                    dashTurnX = k.dash ? k.turnX : null, dashTurnY = k.dash ? k.turnY : null,
                    rawQuickRestart = k.reset, pauseFrames = k.pause, pauseTick = k.pause > 0 ? k.pauseTick : 0,
                    pauseFrame = k.pause > 0 ? k.pauseFrame : 0 });
            }
            return steps;
        }
    }
}
