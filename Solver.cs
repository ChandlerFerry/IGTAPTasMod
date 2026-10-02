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
                return def != null && Categories.IsTarget(Token) && Categories.CanFinish(Token, def.Caps) && def.RunsOn(Token, MinStep, MaxStep);
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
        /// <summary>The fastest offline best's farm loop cycle (live seconds, README ### Rules 30), for an Improve of the farm time.</summary>
        float _solveBestFarm = float.PositiveInfinity;
        /// <summary>Improve's last `waypoints {json}` line: the waypoints with the best's ticks at each (null before one).</summary>
        JObject _solveWaypoints;
        /// <summary>The running job is Improve (`solve --auto`), not the plain solve: its runs count pulls.</summary>
        bool _solveAuto;
        /// <summary>Improve's last `outlook` event: its chance of beating the best with more search, and what else to try (null
        /// before one). Kept after the job ends, so the panel still says why it stopped.</summary>
        JObject _solveOutlook;
        /// <summary>Improve's pulls running now (`pullStart` events not yet followed by their `pull`).</summary>
        readonly List<JObject> _solvePulls = new List<JObject>();

        sealed class SolveJob
        {
            public int course, saved;
            public string caps, label;
            public string[] optional;
            /// <summary>Improve scores by farm time (quick restart to finish, `--farm`) instead of the record's game timer.</summary>
            public bool farm;
            /// <summary>Improve's `--focus a-b`: waypoints first..last (1-based, the finish last); null: the whole course.</summary>
            public int[] focus;
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
        List<(Transform marker, int zone)> _zoneMarkers = new List<(Transform, int)>();

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

        /// <summary>
        /// The zone of the start gate or checkpoint nearest the course's start; 0 when there is no zone loader or none nearby.
        /// A bonus start is a checkpoint with no gate near it (bonus5's is 3100 u from one): with gates alone it had no zone, and
        /// was captured in whichever zone the last course left loaded.
        /// </summary>
        int TargetZone(CourseDef def, out ZoneLoader loader)
        {
            loader = FindAnyObjectByType<ZoneLoader>();
            if (def == null || loader == null || !(AllZonesField?.GetValue(loader) is GameObject[] zones)) return 0;
            if (_zoneMarkers.Count == 0 || _zoneMarkers.Exists(m => m.marker == null)) _zoneMarkers = ZoneMarkers(zones);
            Vector2 start = new Vector2(def.x, def.y) + CurrentOrigin();
            int zone = 0;
            float nearest = 1000f * 1000f;
            foreach (var (marker, markerZone) in _zoneMarkers)
            {
                float d = ((Vector2)marker.position - start).sqrMagnitude;
                if (d < nearest) { nearest = d; zone = markerZone; }
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
            OvergrowthLoadScript overgrowth = FindAnyObjectByType<OvergrowthLoadScript>(FindObjectsInactive.Include);
            // Before OnLoad, as the game's order is: the omnidash's purchase sets the tiers, the next zone load runs OnLoad.
            if (overgrown && !wasOvergrown && overgrowth != null && !stats.Zone2toZone1PathOpen) ApplyOmnidashBoxTiers(overgrowth);
            stats.currentA1State = state;
            if (overgrowth != null) overgrowth.OnLoad();
            else Logger.LogWarning("No OvergrowthLoadScript: Area 1's terrain was not switched.");
            if (!overgrown && wasOvergrown) UndoOmnidashBoxTiers();
            if (wasOvergrown != overgrown) SwapArea1CourseBests();
            Logger.LogInfo("Area 1 " + state + " for " + reason + ".");
        }

        static readonly FieldInfo OvergrowthCoursesField = AccessTools.Field(typeof(OvergrowthLoadScript), "courses");
        static readonly FieldInfo OvergrowthRpAtomBoxField = AccessTools.Field(typeof(OvergrowthLoadScript), "rpAtomBox");
        static readonly FieldInfo OvergrowthBoxesToEnableField = AccessTools.Field(typeof(OvergrowthLoadScript), "boxesToEnable");

        sealed class BoxTier
        {
            public bool active, visible, buyMax;
            public int cap;
            public double capMult, powerScale;
        }

        // Every box ApplyOmnidashBoxTiers may change, as the save had it; empty when the boxes are the save's own.
        readonly Dictionary<upgradeBox, BoxTier> _omnidashBoxUndo = new Dictionary<upgradeBox, BoxTier>();

        // A save turns Area 1 overgrown by buying the OmniDash box (zone 2's tree, upgradeBox OmniDash at TimesUsed 1): it sets
        // currentA1State, Zone2toZone1PathOpen and moreRefreshOrbs, and runs OvergrowthLoadScript.SetA1CourseTiers, whose box
        // changes the course saves keep from then on (a1Statesetter triggers and the zone loader's OnLoad switch only the
        // state and the terrain). So the boxes are overgrown's only in a save that bought it (Zone2toZone1PathOpen); in any
        // other this stands in for SetA1CourseTiers' boxes: in Courses 1-5 the movement, prestige, breaker-light and
        // unlock-prestige boxes go and the scaling boxes get the higher caps and buy max; the RP and atom boxes come. Not the
        // course prestige it runs (localUpgrades.OnPrestige: the save's course levels, no box comes or goes) nor the orbs and
        // the path (the category's unlocks set the orbs; the path is above Course 10). Undone when Area 1 is set back.
        void ApplyOmnidashBoxTiers(OvergrowthLoadScript overgrowth)
        {
            courseScript[] courses = OvergrowthCoursesField?.GetValue(overgrowth) as courseScript[] ?? new courseScript[0];
            upgradeBox atom = OvergrowthRpAtomBoxField?.GetValue(overgrowth) as upgradeBox;
            upgradeBox[] enable = OvergrowthBoxesToEnableField?.GetValue(overgrowth) as upgradeBox[] ?? new upgradeBox[0];
            foreach (upgradeBox stale in _omnidashBoxUndo.Keys.Where(b => b == null).ToList()) _omnidashBoxUndo.Remove(stale);
            void Keep(upgradeBox box)
            {
                if (box != null && !_omnidashBoxUndo.ContainsKey(box))
                    _omnidashBoxUndo[box] = new BoxTier { active = box.gameObject.activeSelf, visible = box.visible, buyMax = box.buyMax, cap = box.Cap,
                        capMult = box.baseCapMult, powerScale = box.upgradePowerScaleFactor };
            }
            foreach (courseScript course in courses.Where(c => c != null))
                foreach (upgradeBox box in course.GetComponentsInChildren<upgradeBox>(true)) Keep(box);
            Keep(atom);
            foreach (upgradeBox box in enable) Keep(box);
            if (atom != null) { atom.visible = true; atom.gameObject.SetActive(true); }
            int hidden = 0;
            for (int i = 0; i < courses.Length; i++)
            {
                if (courses[i] == null) continue;
                // The game's GetComponentsInChildren: the boxes that are on.
                foreach (upgradeBox box in courses[i].GetComponentsInChildren<upgradeBox>())
                {
                    if (box.upgradePowerScaleFactor == 1.0 && box.Cap > 1)
                    {
                        box.baseCapMult = 2.0 * (1.0 + 0.2 * i);
                        box.Cap = (int)Math.Round(box.baseCap * box.baseCapMult);
                        box.buyMax = true;
                    }
                    if (box.upgrade == localUpgrades.localUpgradeSet.activateNextBreakerLights || box.upgrade == localUpgrades.localUpgradeSet.prestige
                        || box.upgrade == localUpgrades.localUpgradeSet.Movement
                        || box.upgrade == localUpgrades.localUpgradeSet.GLOBAL && box.globalUpgrade == globalStats.globalUpgradeSet.unlockPrestige)
                    {
                        box.visible = false;
                        box.gameObject.SetActive(false);
                        hidden++;
                    }
                }
            }
            foreach (upgradeBox box in enable.Where(b => b != null)) { box.visible = true; box.gameObject.SetActive(true); }
            Logger.LogInfo("Area 1's boxes as the omnidash leaves them: " + hidden + " hidden, " + (enable.Count(b => b != null) + (atom != null ? 1 : 0)) + " shown.");
        }

        void UndoOmnidashBoxTiers()
        {
            foreach (var (box, was) in _omnidashBoxUndo.Select(kv => (kv.Key, kv.Value)))
            {
                if (box == null) continue;
                box.visible = was.visible;
                box.buyMax = was.buyMax;
                box.Cap = was.cap;
                box.baseCapMult = was.capMult;
                if (box.upgradePowerScaleFactor != was.powerScale) { box.upgradePowerScaleFactor = was.powerScale; box.CalcBoxCost(); }
                box.gameObject.SetActive(was.active);
            }
            _omnidashBoxUndo.Clear();
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

        /// <summary>Every start gate and checkpoint with its zone (the zone root or ZoneDisableLoadList.toLoad it is under;
        /// 0 outside every zone).</summary>
        static List<(Transform, int)> ZoneMarkers(GameObject[] zones)
        {
            var result = new List<(Transform, int)>();
            IEnumerable<Transform> markers = FindObjectsByType<startGate>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(g => g.transform)
                .Concat(FindObjectsByType<checkpointScript>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(c => c.transform));
            foreach (Transform marker in markers)
            {
                int zone = 0;
                for (Transform t = marker; t != null && zone == 0; t = t.parent)
                    zone = Array.FindIndex(zones, z => z != null && (z == t.gameObject
                        || z.GetComponent<ZoneDisableLoadList>()?.toLoad?.Contains(t.gameObject) == true)) + 1;
                result.Add((marker, zone));
            }
            return result;
        }

        // The build's metadata never changes: read once (a reflection lookup that allocated its attributes on every use, the
        // goals overlay's ForcedPaths among them).
        static string _engineSimExe;
        static bool _engineSimExeRead;

        static string EngineSimExe
        {
            get
            {
                if (!_engineSimExeRead)
                {
                    _engineSimExe = typeof(TasPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "EngineSimExe")?.Value;
                    _engineSimExeRead = true;
                }
                return _engineSimExe;
            }
        }

        SolveJob CourseJob(JObject item)
        {
            int course = Number(item, "course", 0);
            string capabilities = Text(item, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course);
            if (def == null) throw new InvalidOperationException("Unknown course " + course + ".");
            if (!Categories.IsCategory(capabilities ?? "")) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, capabilities);
            string[] optional = OptionalNames(item, course, capabilities);
            string[] known = OptionalBlocks(course).Where(b => b.caps.Contains(capabilities)).Select(b => b.name).ToArray();
            string missing = optional.FirstOrDefault(n => !known.Contains(n));
            if (missing != null) throw new InvalidOperationException("No optional block '" + missing + "' for " + def.Token + " " + capabilities + ".");
            bool farm = Truthy(item, "farm");
            // Bonuses and transitions are timed in ticks already (README ### Rules 31): their record is their farm time.
            if (farm && (def.IsBonus || def.IsTransition)) throw new InvalidOperationException(def.Token + " has no separate farm time: improve its record.");
            int[] focus = item["focus"] is JArray range && range.Count == 2 ? new[] { (int)range[0], (int)range[1] } : null;
            if (focus != null && (focus[0] < 1 || focus[1] < focus[0]))
                throw new InvalidOperationException("focus: a waypoint range [first, last], 1-based.");
            return new SolveJob { course = course, caps = capabilities, optional = optional, farm = farm, focus = focus,
                label = def.Token + "_" + Caps.Parse(capabilities).Label + (farm ? " farm" : "") + (optional.Length == 0 ? "" : " +" + string.Join(" +", optional))
                    + (focus == null ? "" : " waypoints " + focus[0] + "-" + focus[1]) };
        }

        /// <summary>
        /// The course has a category route to improve from: a record or farm route of this category or a narrower one (their
        /// unlocks a subset), the routes the solve seeds with. Without one the job is the plain solve, which finds a first finish.
        /// </summary>
        bool HasCourseRoute(CourseDef course, string capabilities)
        {
            Caps target = Caps.Parse(capabilities);
            return _library.macros.Any(m =>
            {
                Match g = MatchCategoryRoute(m.name);
                if (!g.Success) g = MatchFarmRoute(m.name);
                return g.Success && string.Equals(g.Groups["token"].Value, course.Token, StringComparison.OrdinalIgnoreCase)
                    && Caps.Parse(g.Groups["label"].Value.ToUpperInvariant()).IsSubsetOf(target);
            });
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
        JObject _optionalBlocksOf;

        IEnumerable<OptionalBlock> OptionalBlocks(int course)
        {
            JObject root = ForcedPaths();
            if (root != _optionalBlocksOf)
            {
                _optionalBlocksOf = root;
                _optionalBlocks = root == null ? new OptionalBlock[0]
                    : ((JArray)root["paths"]).OfType<JObject>().Where(rule => rule["course"] != null)
                        .SelectMany(rule => (rule["optional"] as JArray ?? new JArray()).OfType<JObject>().Select(b => new OptionalBlock {
                            course = (int)rule["course"], name = (string)b["name"], note = (string)b["note"] ?? "", on = (bool?)b["default"] ?? false,
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
            List<SolveJob> jobs = items.Select(CourseJob).ToList();
            if (jobs.Count == 0) throw new InvalidOperationException("Nothing selected to optimize.");
            float minutes = FloatNumber(body, "minutes", 60f);
            if (!(minutes >= 0.25f)) throw new InvalidOperationException("Minutes per target must be at least 0.25.");
            _solveQueue.Clear();
            _solveQueue.AddRange(jobs);
            _solveMinutes = minutes;
            _solveMaxMinutes = Math.Max(minutes, FloatNumber(body, "maxMinutes", minutes));
            _solveRepeat = Truthy(body, "repeat");
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
            _solveBest = _solveBestFarm = float.PositiveInfinity;
            _solveWaypoints = null;
            _solveOutlook = null;
            _solvePulls.Clear();
            _solveAuto = false;
            _solverSpeed = "";
            while (_solveOutput.TryDequeue(out _)) { }
            if (_solveQueue.Count > 1 || _solveRepeat)
                _solverLog.Add("── " + (_solveRepeat ? "pass " + _solvePass + " · " : "") + (index + 1) + "/" + _solveQueue.Count + " " + job.label);
            _solverRunning = true;
            _solverStatus = "Capturing the start state in turbo";
            EnsureGameUnpaused();
        }

        void KillSolveProcess() { KillEngineSim(ref _solveProcess, "offline solver"); }

        /// <summary>
        /// The offline engine run with <paramref name="args"/>, below the game: a solve takes nearly every core, and at normal
        /// priority 1x playback (280 rendered frames a second, each a fixed game-time step) lost frames to it; its children
        /// (humanize, confirm) inherit it. With <paramref name="output"/> its stdout and stderr lines go there as they come,
        /// else the caller reads them; <paramref name="stdin"/> keeps its stdin open for a "stop".
        /// </summary>
        static Process StartEngineSim(string args, ConcurrentQueue<string> output = null, bool stdin = false)
        {
            var process = new Process { StartInfo = new ProcessStartInfo(EngineSimExe, args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdin, WorkingDirectory = Path.GetDirectoryName(EngineSimExe) } };
            if (output != null)
            {
                process.OutputDataReceived += (sender, e) => { if (e.Data != null) output.Enqueue(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) output.Enqueue(e.Data); };
            }
            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            if (output != null)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            return process;
        }

        void KillEngineSim(ref Process process, string what)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) { Logger.LogWarning("Could not stop the " + what + ": " + ex.Message); }
            process.Dispose();
            process = null;
        }

        void EndSolveJob(string outcome)
        {
            KillSolveProcess();
            SolveJob job = _solveQueue[_solveIndex];
            job.saved = _turboSaved;
            float best = job.farm ? _solveBestFarm : _solveBest;
            job.result = outcome + (float.IsInfinity(best) ? "" : ", best " + (job.farm ? "farm loop " + best.ToString("0.0000", CultureInfo.InvariantCulture) : best.ToString("0.00", CultureInfo.InvariantCulture)) + "s")
                + ", " + _turboSaved + " saved" + OutlookNote();
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
            // Its zone too: a zone load the game still had queued (ZoneLoader.LoadZone's coroutine, e.g. the bootstrap's) can
            // switch zones back after RestoreRouteStart loaded the course's, and a start captured then has the course's
            // DisableBits (its start gate, zip movers) off: no run from it starts the timer.
            bool parked = player != null && ((Vector2)player.transform.position - start).sqrMagnitude < 64f * 64f && Area1StateReady(target)
                && ZoneReady(target);
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

        /// <summary>
        /// The world as ZoneLoader._LoadZone(activeZone) leaves it: the active zone's root and its ZoneDisableLoadList.toLoad on
        /// (a course's DisableBits: start gate, zip movers), every other zone's root off.
        /// </summary>
        static bool ZonesSettled()
        {
            ZoneLoader loader = FindAnyObjectByType<ZoneLoader>();
            if (loader == null || !(AllZonesField?.GetValue(loader) is GameObject[] zones)) return true;
            for (int i = 0; i < zones.Length; i++)
            {
                if (zones[i] == null) continue;
                bool active = i == loader.activeZone - 1;
                if (zones[i].activeSelf != active) return false;
                if (active && zones[i].GetComponent<ZoneDisableLoadList>()?.toLoad is GameObject[] toLoad)
                    foreach (GameObject part in toLoad)
                        if (part != null && !part.activeSelf) return false;
            }
            return true;
        }

        void StopSolver(string status)
        {
            KillSolveProcess();
            if (_solverRunning && _solveIndex >= 0 && _solveIndex < _solveQueue.Count)
            {
                _solveQueue[_solveIndex].saved = _turboSaved;
                _solveQueue[_solveIndex].result = status + ", " + _turboSaved + " saved" + OutlookNote();
            }
            _solverRunning = false;
            _solverStatus = status;
            _solvePulls.Clear();
        }

        /// <summary>The outlook's verdict for a queue row (empty before one): whether this target is worth another pass.</summary>
        string OutlookNote()
        {
            if (_solveOutlook == null) return "";
            string verdict = (string)_solveOutlook["verdict"];
            double hour = (double?)_solveOutlook["chanceHour"] ?? 0;
            switch (verdict)
            {
                case "floor": return " · can't beat: at the speed floor";
                case "exhausted": return " · nothing left to try";
                default: return " · " + verdict + " to improve (" + Math.Round(hour * 100).ToString(CultureInfo.InvariantCulture) + "% per hour)";
            }
        }

        object BuildSolverWebState()
        {
            SolveJob current = _solverRunning && _solveIndex >= 0 && _solveIndex < _solveQueue.Count ? _solveQueue[_solveIndex] : null;
            return new {
                running = _solverRunning, status = _solverStatus, course = _solveCourse, capabilities = _solveCaps, optional = _solveOptional, speed = _solverSpeed,
                farm = current?.farm ?? false, focus = current?.focus, auto = _solveAuto, waypoints = _solveWaypoints, outlook = _solveOutlook, pulls = _solvePulls,
                evaluations = _solveRuns, finishes = _solveFinishes, turboChecks = _turboChecks, turboSaved = _turboSaved, toValidate = _offlineWaiting,
                best = float.IsInfinity(_solveBest) ? (float?)null : _solveBest,
                bestFarm = float.IsInfinity(_solveBestFarm) ? (float?)null : _solveBestFarm,
                log = LatestSolverLog(),
                courses = _courses.Select(c => c.id).ToArray(),
                minutes = _solveMinutes, repeat = _solveRepeat, pass = _solvePass,
                queue = _solveQueue.Select((j, i) => new { label = j.label, result = j.result, active = _solverRunning && i == _solveIndex }).ToArray() };
        }

        /// <summary>The last 12 lines of the solver's log, newest first. The log isn't capped (a long solve writes thousands of
        /// lines), and Reverse() copied all of it on every studio check.</summary>
        string[] LatestSolverLog()
        {
            var lines = new string[Math.Min(12, _solverLog.Count)];
            for (int i = 0; i < lines.Length; i++) lines[i] = _solverLog[_solverLog.Count - 1 - i];
            return lines;
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
            if (!CapabilitiesLocked) ApplyAbilities(FindWorldMovement(), _solveCaps, course);
            string name = "optimize-c" + course.Token + "-" + _solveCaps;
            JObject body = (JObject)target.DeepClone();
            body["ticks"] = new JArray(); body["name"] = name; body["world"] = true; body["freshStart"] = true;
            JObject captured = JObject.FromObject(EngineSimTrace(new WebStudioRequest(), body));
            if (captured["ok"] == null || !(bool)captured["ok"])
            {
                EndSolveJob("Start state not captured: " + captured["error"]);
                return;
            }
            SolveJob job = _solveQueue[_solveIndex];
            string args = CourseSolveArgs(job, course, (string)captured["path"], name, _solveMinutes, out string what, out _solveAuto);
            _solveProcess = StartEngineSim(args, _solveOutput);
            _solverStatus = what + "; turbo checks every new best";
            Logger.LogInfo("Optimize: " + EngineSimExe + " " + args);
        }

        /// <summary>
        /// The engine's command line for a course job on <paramref name="trace"/> (its seed files written as
        /// <paramref name="name"/>-seedN.json): Improve, `solve --auto` (record, or `--farm`; `--focus a-b` over the waypoints),
        /// when the course has a category route to start from, else the plain solve that finds a first finish. Improve leaves
        /// the game 4 cores, as the Full-TAS Improve does.
        /// </summary>
        string CourseSolveArgs(SolveJob job, CourseDef course, string trace, string name, float minutes, out string what, out bool auto)
        {
            var args = new StringBuilder("solve ").Append(Quote(trace));
            args.Append(CourseSeedArgs(course, name, out int seeds));
            AppendOptional(args, job.course, job.caps, job.optional);
            auto = HasCourseRoute(course, job.caps);
            int threads = Math.Max(1, Environment.ProcessorCount - (auto ? 4 : 2));
            args.Append(" --threads ").Append(threads).Append(" --minutes ").Append(minutes.ToString(CultureInfo.InvariantCulture));
            if (!auto)
            {
                args.Append(" --stall-minutes ").Append(Math.Min(5f, minutes / 2f).ToString(CultureInfo.InvariantCulture));
                what = "Solving offline (no route to improve yet) on " + threads + " threads from " + seeds + " seed routes";
                return args.ToString();
            }
            args.Append(" --auto");
            if (job.farm) args.Append(" --farm");
            if (job.focus != null) args.Append(" --focus ").Append(job.focus[0]).Append('-').Append(job.focus[1]);
            what = "Improving " + (job.farm ? "the farm time" : "the record") + (job.focus == null ? "" : " (waypoints " + job.focus[0] + "-" + job.focus[1] + ")")
                + " on " + threads + " threads from " + seeds + " seed routes";
            return args.ToString();
        }

        /// <summary>` --seed "file"` per raw route of the course in the library (its name kept for the engine's log), written as
        /// <paramref name="name"/>-seedN.json after the old ones go.</summary>
        string CourseSeedArgs(CourseDef course, string name, out int seeds)
        {
            var args = new StringBuilder();
            foreach (string old in Directory.GetFiles(EngineSimDir, name + "-seed*.json")) File.Delete(old);
            seeds = 0;
            foreach (MacroDefinition route in _library.macros.Where(m => m.name != null
                && m.name.StartsWith(course.Token + "_", StringComparison.OrdinalIgnoreCase)
                && m.steps.All(s => s.type == MacroStepType.RawInput)))
            {
                string seed = Path.Combine(EngineSimDir, name + "-seed" + seeds++ + ".json");
                File.WriteAllText(seed, JsonConvert.SerializeObject(new { name = route.name, ticks = ToTicks(route.steps) }));
                args.Append(" --seed ").Append(Quote(seed));
            }
            return args.ToString();
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
            string trace = CourseTrace(course, caps);
            if (trace == null)
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No start capture for " + course.Token + " " + caps + " yet: solve it once first." };
            }
            string name = "map-c" + course.Token + "-" + caps;
            string output = Path.Combine(EngineSimDir, name + ".json");
            // This map's job is running or queued: join it, leaving its seed files alone.
            if (MapInFlight(output)) return RunEngineSimMap(request, null, output);
            var args = new StringBuilder("map ").Append(Quote(trace)).Append(" --out ").Append(Quote(output)).Append(CourseSeedArgs(course, name, out _));
            AppendOptional(args, id, caps, OptionalNames(body, id, caps));
            return RunEngineSimMap(request, args.ToString(), output);
        }

        /// <summary>The course's last start capture (the solve's, else the studio's), or null before its first solve.</summary>
        string CourseTrace(CourseDef course, string caps)
        {
            // The files come and go within a session (a solve writes them), so each call looks again; only the LINQ is gone (the
            // TAS session's techs check calls this on every studio push).
            string optimize = Path.Combine(EngineSimDir, "optimize-c" + course.Token + "-" + caps + ".json");
            if (File.Exists(optimize)) return optimize;
            string start = Path.Combine(EngineSimDir, "start-c" + course.Token + "-" + caps + ".json");
            return File.Exists(start) ? start : null;
        }

        /// <summary>
        /// A course target's waypoints for the studio (`solve --auto --waypoints-out`, which places them on the best seed and
        /// exits): the start gate (record timing), goals along the record's (or with `farm`, the farm route's) path, then the
        /// finish, with that route's tick at each. Improve's `focus` indexes this list. Run by the engine as a map is, one job per file.
        /// </summary>
        object EngineSimWaypoints(WebStudioRequest request, JObject body)
        {
            SolveJob job;
            try { job = CourseJob(body); }
            catch (InvalidOperationException ex) { request.StatusCode = 400; return new { ok = false, error = ex.Message }; }
            CourseDef course = _courses.Find(c => c.id == job.course);
            string trace = CourseTrace(course, job.caps);
            if (trace == null)
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No start capture for " + course.Token + " " + job.caps + " yet: improve it once first." };
            }
            if (!HasCourseRoute(course, job.caps))
            {
                request.StatusCode = 404;
                return new { ok = false, error = "No route for " + job.label + " yet: its first solve makes one." };
            }
            string name = "waypoints-c" + course.Token + "-" + job.caps + (job.farm ? "-farm" : "");
            string output = Path.Combine(EngineSimDir, name + ".json");
            if (MapInFlight(output)) return RunEngineSimMap(request, null, output);
            var args = new StringBuilder("solve ").Append(Quote(trace)).Append(CourseSeedArgs(course, name, out _));
            AppendOptional(args, job.course, job.caps, job.optional).Append(" --auto");
            if (job.farm) args.Append(" --farm");
            args.Append(" --waypoints-out ").Append(Quote(output));
            return RunEngineSimMap(request, args.ToString(), output);
        }

        /// <summary>
        /// What /api/solver/start would run for the same body, without starting anything: each job's engine command on the
        /// course's last start capture (the run itself captures a fresh one first) and whether it is Improve or the plain solve.
        /// Seed files go to preview-c*-seedN.json.
        /// </summary>
        object PreviewSolver(JObject body)
        {
            List<JObject> items = body["queue"] is JArray queue ? queue.OfType<JObject>().ToList() : new List<JObject> { body };
            float minutes = FloatNumber(body, "minutes", 60f);
            return new { ok = true, jobs = items.Select(CourseJob).Select(job =>
            {
                CourseDef course = _courses.Find(c => c.id == job.course);
                string trace = CourseTrace(course, job.caps) ?? Path.Combine(EngineSimDir, "optimize-c" + course.Token + "-" + job.caps + ".json");
                string args = CourseSolveArgs(job, course, trace, "preview-c" + course.Token + "-" + job.caps, minutes, out string what, out bool auto);
                return new { label = job.label, improve = auto, what, command = Quote(EngineSimExe) + " " + args };
            }).ToArray() };
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

        static readonly Regex EngineRefusal = new Regex(@"^System\.InvalidOperationException: (.+)$");

        void RunMapProcess(MapJob job, string args, string output)
        {
            try
            {
                using (Process process = StartEngineSim(args))
                {
                    var errors = process.StandardError.ReadToEndAsync();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0) job.Response = File.ReadAllText(output);
                    else
                    {
                        string error = (errors.Result.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "exit code " + process.ExitCode).Trim();
                        // The engine's InvalidOperationException is its refusal of a state ("No farm loop yet: ..."): its message alone,
                        // a note for the studio, not a failure.
                        Match refusal = EngineRefusal.Match(error);
                        job.Status = refusal.Success ? 409 : 500;
                        job.Response = JsonConvert.SerializeObject(new { ok = false,
                            error = refusal.Success ? refusal.Groups[1].Value : args.Substring(0, args.IndexOf(' ')) + " failed: " + error });
                    }
                }
            }
            catch (Exception ex)
            {
                job.Status = 500;
                job.Response = JsonConvert.SerializeObject(new { ok = false, error = args.Substring(0, args.IndexOf(' ')) + " failed: " + ex.Message });
            }
        }

        static readonly Regex SolveProgress = new Regex(@"^\[[\d:]+\] (\d+) runs/s, (\d+) ticks/s, (\d+) runs, (\d+) finishes");
        static readonly Regex SolveBest = new Regex(@"offline best ([\d.,]+)s via ");
        static readonly Regex SolveLoopBest = new Regex(@"offline best farm loop ([\d.,]+)s via ");
        const string AutoEvent = "@fulltas ", WaypointsLine = "waypoints ";

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
            // Improve (`solve --auto`): its waypoints on every new best, and its events. Pulls count (as runs); `pullStart` and
            // `pull` keep the list of pulls running now, `outlook` (after every pull) the chance of beating the best; its gains
            // show as the offline-best lines below, so the rest is dropped (it's all in the engine's own log).
            if (line.StartsWith(WaypointsLine, StringComparison.Ordinal))
            {
                try { _solveWaypoints = JObject.Parse(line.Substring(WaypointsLine.Length)); }
                catch (JsonException ex) { Logger.LogWarning("solve: unreadable waypoints line: " + ex.Message); }
                return;
            }
            if (line.StartsWith(AutoEvent, StringComparison.Ordinal))
            {
                JObject e;
                try { e = JObject.Parse(line.Substring(AutoEvent.Length)); }
                catch (JsonException) { return; }
                string type = (string)e["type"];
                if (type == "outlook") _solveOutlook = e;
                else if (type == "pullStart")
                {
                    // Once per pull number: a pull re-announced replaces its entry.
                    _solvePulls.RemoveAll(p => (long?)p["pull"] == (long?)e["pull"]);
                    _solvePulls.Add(e);
                }
                else if (type == "pull")
                {
                    _solveRuns = (long?)e["pull"] ?? _solveRuns + 1;
                    _solvePulls.RemoveAll(p => (long?)p["pull"] == _solveRuns);
                }
                return;
            }
            m = SolveLoopBest.Match(line);
            if (m.Success) _solveBestFarm = Math.Min(_solveBestFarm, float.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
            else if ((m = SolveBest.Match(line)).Success)
                _solveBest = Math.Min(_solveBest, float.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
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
            List<(string field, object value)> savedAbilities = ApplyAbilities(player, _solverCaps, course);
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
                        if (_startState == null && tick == 0)
                        {
                            // The category start's history (CourseStart): the snapshot, the trace written from it and this run
                            // all go on from the same values, whatever ran before. A tech demo's start is its own savestate's.
                            if (_techStart == null)
                                CourseStart.Apply(player, FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                            settling = TurboRunner.Capture(player, _solverCourses, _engine.CaptureState(), 0);
                            // Its restores put the world at the canonical origin exactly, whatever ran since (TurboRunner.PutCanonicalOrigin).
                            if (_techStart == null) settling.canonical = _canonicalReference;
                        }
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

        string SolverCategory { get { return _solverFullTas != null ? FullTasClassCategory(_solverFullTas.name, _solverFullTasClass.cls)
            : _solverTarget.Token + "_" + Caps.Parse(_solverCaps).Label; } }

        bool FarmTracked { get { return _solverFullTas == null && !_solverTarget.IsBonus && !_solverTarget.IsTransition; } }

        /// <summary>A farm route's time in its name: a lap's share of the loop's cycle, live seconds to 4 places.</summary>
        static string FarmSeconds(double seconds) { return seconds.ToString("0.0000", CultureInfo.InvariantCulture); }

        /// <summary>
        /// The only way a category route is saved (README ### Rules 43). A record (<paramref name="loopFrom"/> 0): when a replay
        /// from the course start finishes in <paramref name="time"/> and every tick obeys the category. A farm loop (README ###
        /// Rules 30; its loop from input <paramref name="loopFrom"/>, <paramref name="loopLaps"/> laps): when a replay from the
        /// course start loops in exactly <paramref name="loopLive"/> units a loop (<see cref="EvaluateLoop"/>) and every tick obeys
        /// the category; named by a lap's share, `<c>&lt;category&gt; farm &lt;lap&gt;</c>`.
        /// </summary>
        bool SaveSolverRoute(List<SolverTick> inputs, float time, int loopFrom = 0, int loopLaps = 1, long loopLive = 0)
        {
            string name;
            string violation;
            if (loopFrom > 0)
            {
                name = SolverCategory + " farm " + FarmSeconds(loopLive / 1400.0 / Math.Max(1, loopLaps));
                List<string> audit = StartAudit();
                LoopResult again;
                try { again = EvaluateLoop(inputs, loopFrom, loopLaps); }
                finally { _audit = null; }
                if (!again.steady || again.live != loopLive)
                    audit.Insert(0, "a replay from the course start " + (again.steady ? "loops in " + FarmSeconds(again.live / 1400.0) + "s" : "has " + again));
                violation = audit.Count == 0 ? null : string.Join("; ", audit);
            }
            else
            {
                name = SolverCategory + " " + time.ToString("0.00", CultureInfo.InvariantCulture);
                violation = RouteViolation(inputs, time);
            }
            if (violation != null) return Rejected(name, violation);
            _library.macros.Add(new MacroDefinition { name = name, steps = ToSteps(inputs), loopFrom = loopFrom, loopLaps = loopFrom > 0 ? Math.Max(1, loopLaps) : 0 });
            PruneSlowerRoutes();
            SaveLibrary();
            return true;
        }

        bool Rejected(string name, string violation)
        {
            string line = "REJECTED " + name + ": " + violation;
            _solverLog.Add(line);
            Logger.LogWarning(line);
            return false;
        }

        /// <summary>Loops played after a farm route's lead to find its steady cycle (the engine's FullTas.LoopRepeats).</summary>
        const int LoopRepeats = 4;
        // While a farm loop plays (EvaluateLoop): each finish of the target course (the input count, physics tick and held frames
        // at it); the finish doesn't end the run then.
        List<(long input, long physics, long held)> _loopFinishes;

        /// <summary>A farm loop's replay (README ### Rules 30): the lead's last finish, then each loop's cycle.</summary>
        sealed class LoopResult
        {
            /// <summary>Every loop finished on its own last input and the last two cycles are equal.</summary>
            public bool steady;
            /// <summary>The last loop's cycle: physics ticks and held frames from the finish before it; its laps.</summary>
            public long ticks, held;
            public int laps = 1;
            public long live { get { return MacroEngine.LiveUnits(ticks, held); } }
            /// <summary>The farm time: a lap's share of the cycle, in live seconds.</summary>
            public double lap { get { return MacroEngine.LiveSeconds(ticks, held) / laps; } }
            public readonly List<long> cycles = new List<long>();
            public override string ToString()
            {
                string all = string.Join(", ", cycles.Select(c => c < 0 ? "-" : FarmSeconds(c / 1400.0)));
                return steady ? FarmSeconds(lap) + "s a lap (" + ticks + " ticks, " + held + " held frames a loop of " + laps + "; cycles " + all + ")"
                    : "no steady loop (cycles " + all + ")";
            }
        }

        /// <summary>
        /// Plays a farm route's lead and then its loop (the inputs from <paramref name="loopFrom"/> on, <paramref name="laps"/>
        /// laps) LoopRepeats times, from the course start through every finish: each loop must finish on its own last input, and
        /// the last two cycles must be equal. A lap's share of the cycle is the farm time.
        /// </summary>
        LoopResult EvaluateLoop(List<SolverTick> inputs, int loopFrom, int laps)
        {
            var result = new LoopResult { laps = Math.Max(1, laps) };
            if (loopFrom <= 0 || loopFrom >= inputs.Count) return result;
            int length = inputs.Count - loopFrom;
            var played = inputs.Take(loopFrom).ToList();
            for (int k = 0; k < LoopRepeats; k++) played.AddRange(inputs.Skip(loopFrom).Select(t => t.Clone()));
            var finishes = new List<(long input, long physics, long held)>();
            _loopFinishes = finishes;
            try { Evaluate(played, out _, out _, out _); }
            finally { _loopFinishes = null; }
            int prev = finishes.FindIndex(f => f.input == loopFrom);
            for (int k = 1; k <= LoopRepeats; k++)
            {
                int at = prev < 0 ? -1 : finishes.FindIndex(f => f.input == loopFrom + (long)k * length);
                if (at < 0)
                {
                    result.cycles.Add(-1);
                    prev = -1;
                    continue;
                }
                result.ticks = finishes[at].physics - finishes[prev].physics;
                result.held = finishes[at].held - finishes[prev].held;
                result.cycles.Add(result.live);
                prev = at;
            }
            long last = result.cycles[LoopRepeats - 1];
            result.steady = last >= 0 && result.cycles[LoopRepeats - 2] == last;
            return result;
        }

        /// <summary>
        /// A farm route saved before farms were loops (README ### Rules 30) times one lap from the quick restart, not a loop: it
        /// can't be compared with one, so it is renamed stale (out of the ranking, kept for its inputs).
        /// </summary>
        bool RetireLapFarms()
        {
            bool retired = false;
            foreach (MacroDefinition m in _library.macros)
            {
                if (m.loopFrom > 0 || !MatchFarmRoute(m.name).Success) continue;
                Logger.LogInfo("Retired " + m.name + ": a farm is a loop now (finish to finish), and this route is one lap");
                m.name += " stale";
                retired = true;
            }
            return retired;
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
                    continue;
                }
                steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1,
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
