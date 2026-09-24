using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace IGTAPTasMod
{
    // One-button route solver, judged only by the real game (TurboRunner). Two alternating modes:
    // explore keeps the earliest-known input prefix for every map cell (with/without the course timer
    // running) and extends random cells with random run/jump/dash bursts, so it finds new paths without a
    // seed; optimize mutates the best finish (cut, dash, dash-jump, hop, insert, re-aim). A finish counts
    // only when the game timed it (start gate -> real end); middle checkpoints are irrelevant.
    public sealed partial class TasPlugin
    {
        public sealed class CourseDef
        {
            public int id;
            // Category routes are "<course>_<CAPS> <time>"; a named course ("bonus1") takes its name as the course.
            public string name;
            public float x, y;
            // A bonus course has no start/stop gates: its run finishes when the player touches this box (the
            // reward's buy trigger) and is timed in ticks from the run start. Zero size = a gated course.
            public float endX, endY, endW, endH;
            // The narrowest category the course is run in (none|wj|wjdash|wjdashaj): Course 2 needs wall jump,
            // Course 3 wall jump + dash, Course 4 all three. Missing = every category.
            public string minCapabilities;
            [JsonIgnore] public bool HasFinish { get { return endW > 0f && endH > 0f; } }
            [JsonIgnore] public string Token { get { return string.IsNullOrEmpty(name) ? id.ToString() : name; } }
            [JsonIgnore] public int MinLevel { get { return CapsLevel(minCapabilities); } }
        }

        sealed class SolverTick
        {
            public float x; public bool held, press, release, dash; public int dashJump;
            public SolverTick Clone() { return (SolverTick)MemberwiseClone(); }
        }

        // A cell's prefix is the first `length` ticks of an evaluated input list; those lists are never mutated.
        // `state` is the game right after that prefix, so a branch can resume there instead of replaying it.
        sealed class Cell { public List<SolverTick> source; public int length, picks; public TurboRunner.Snapshot state; }

        static readonly FieldInfo CourseTrackingFlag = AccessTools.Field(typeof(courseScript), "tracking");
        const float CellSize = 48f;

        List<CourseDef> _courses = new List<CourseDef>();
        MacroDefinition _pendingSilentMacro;
        bool _silentEvaluation, _silentFinished;
        float _silentTime;

        bool _solverRunning;
        string _solverStatus = "Idle";
        int _solverCourse, _solverEvaluations, _solverFinishes, _solverBestAt;
        string _solverCaps = "", _solverSpeed = "";
        float _solverBest = float.PositiveInfinity;
        List<SolverTick> _solverBestInputs;
        // _bestStates[k]: the game after the best route's first k inputs (from its verifying replay).
        TurboRunner.Snapshot[] _bestStates;
        // Course time at which the best route first entered each timed cell. An optimize run that reaches one of
        // them more than SegmentSlack later has lost time in the segment it changed and is cut there.
        // ponytail: first visit only, so a route that loops back through a cell can cut a good run; key by visit if so.
        Dictionary<long, float> _bestArrival = new Dictionary<long, float>();
        const float SegmentSlack = .2f;
        courseScript[] _solverCourses;
        // The course start as the solver's first run reached it (respawned and settling, one tick before its first
        // input). Every later run from the start resumes here: a respawn alone leaves state from the previous run
        // behind, and that produced finishes that die in real play.
        TurboRunner.Snapshot _startState;
        // Branched runs re-checked by a replay from the start, and how many of them matched it.
        int _branchChecks, _branchMatches;
        readonly Dictionary<long, Cell> _archive = new Dictionary<long, Cell>();
        readonly Queue<List<SolverTick>> _solverSeeds = new Queue<List<SolverTick>>();
        readonly List<string> _solverLog = new List<string>();
        System.Random _rng = new System.Random();
        // Speed breakdown since the last status update: runs, ticks, time to reach the first tick (reset), total run time.
        int _profRuns; long _profTicks; double _profSetupMs, _profRunMs;
        readonly Stopwatch _profWall = new Stopwatch();

        string CoursesPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "courses.json"); } }

        void LoadCourses()
        {
            try
            {
                if (File.Exists(CoursesPath)) { _courses = JsonConvert.DeserializeObject<List<CourseDef>>(File.ReadAllText(CoursesPath)); return; }
            }
            catch (Exception ex) { Logger.LogWarning("courses.json rejected: " + ex.Message); }
            // Start points (quick restarts for 1-4); the game places the player 12 px below. bonus1 has no gates:
            // runs start in the course 2 secret shaft and finish on the free secret box at the corridor's end.
            _courses = new List<CourseDef> {
                new CourseDef { id = 1, x = 2241f, y = -292f, minCapabilities = "none" },
                new CourseDef { id = 2, x = 4783.43359f, y = 1878.2251f, minCapabilities = "wj" },
                new CourseDef { id = 3, x = 9505f, y = 6967.01465f, minCapabilities = "wjdash" },
                new CourseDef { id = 4, x = 20200.4004f, y = 10554.5449f, minCapabilities = "wjdashaj" },
                new CourseDef { id = 5, name = "bonus1", x = 7392.427f, y = 2807.015f, minCapabilities = "wj",
                    endX = 5239.43f, endY = 681f, endW = 127.5f, endH = 6f } };
            SaveCourses();
        }

        void SaveCourses() { WriteJsonAtomic(CoursesPath, _courses, false); }

        object AddCourseFromLastRecording()
        {
            RecordingDefinition recording = _recordings.OrderByDescending(r => r.createdUtc).FirstOrDefault();
            if (recording == null || recording.samples == null || recording.samples.Count == 0)
                throw new InvalidOperationException("No recording to take the course start from.");
            // The lowest unused id: bonus areas hold 101+, so the game's courses keep their own numbers.
            int id = 1;
            while (_courses.Any(c => c.id == id)) id++;
            _courses.Add(new CourseDef { id = id, x = recording.samples[0].positionX, y = recording.samples[0].positionY + 12f });
            SaveCourses();
            CreateRouteFromRecording(recording);
            _library.macros[_library.macros.Count - 1].name = id + "_REC " + recording.name;
            SaveLibrary();
            return new { ok = true, course = id };
        }

        static readonly Dictionary<string, string> CapabilityLabels = new Dictionary<string, string> {
            { "none", "NO_WJ" }, { "wj", "WJ" }, { "wjdash", "WJDASH" }, { "wjdashaj", "WJ_DASH_AJ" } };

        // Course 2 has no NO_WJ category, Course 3 starts at WJDASH, Course 4 at WJ_DASH_AJ (courses.json minCapabilities).
        void CheckCategory(int course, string capabilities)
        {
            CourseDef def = _courses.Find(c => c.id == course);
            if (def != null && CapsLevel(capabilities) < def.MinLevel)
                throw new InvalidOperationException("Course " + def.Token + " has no " + CapabilityLabels[capabilities] + " category; it starts at "
                    + CapabilityLabels[def.minCapabilities] + ".");
        }

        void StartSolver(int course, string capabilities)
        {
            if (_courses.All(c => c.id != course)) throw new InvalidOperationException("Unknown course " + course + ".");
            if (!CapabilityLabels.ContainsKey(capabilities)) throw new InvalidOperationException("Unknown capabilities.");
            if (_engine.IsRunning || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (CapabilitiesLocked) throw new InvalidOperationException(CapabilitiesLockedError);
            CheckCategory(course, capabilities);
            _solverCourse = course; _solverCaps = capabilities;
            _solverEvaluations = _solverFinishes = _solverBestAt = 0;
            _solverBest = float.PositiveInfinity; _solverBestInputs = null; _bestStates = null; _bestArrival.Clear();
            _branchChecks = _branchMatches = 0;
            _solverCourses = new courseScript[0];
            _startState = null;
            _archive.Clear(); _solverSeeds.Clear(); _solverLog.Clear();
            _rng = new System.Random();
            // Every route already made for this course seeds the search, whatever category it was made for.
            string courseToken = _courses.Find(c => c.id == course).Token;
            foreach (MacroDefinition route in _library.macros.Where(m => m.name.StartsWith(courseToken + "_")))
                if (route.steps.All(s => s.type == MacroStepType.RawInput)) _solverSeeds.Enqueue(ToTicks(route.steps));
            _solverSeeds.Enqueue(new List<SolverTick>());
            _solverRunning = true;
            _solverStatus = "Evaluating " + (_solverSeeds.Count - 1) + " seed routes";
            EnsureGameUnpaused();
        }

        // A course's zone loads in real frames around the player (turbo does not step the loader), and FloatingOrigin
        // shifts the world in LateUpdate once the player is far from the origin (Course 4's start is). A run captured
        // before either settles runs through an empty course or from the wrong place, so park the player at the start
        // (absolute course coordinates + currentOrigin) and let a few real frames pass first.
        int _parkedFrame = -1000;

        bool CourseLoaded()
        {
            _solverCourses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            CourseDef target = _courses.Find(c => c.id == _solverCourse);
            Movement player = FindWorldMovement();
            Vector2 start = new Vector2(target.x, target.y) + CurrentOrigin();
            bool parked = player != null && ((Vector2)player.transform.position - start).sqrMagnitude < 64f * 64f;
            if (!parked)
            {
                RestoreRouteStart(new MacroDefinition { name = target.Token + "_" });
                _parkedFrame = Time.frameCount;
            }
            // A bonus course has no courseScript of its own; settled zones are all its runs need.
            else if (Time.frameCount - _parkedFrame >= 10 && ZonesSettled() && (target.HasFinish
                || _solverCourses.Any(c => c.courseNumber == _solverCourse && c.isActiveAndEnabled))) return true;
            _solverStatus = "Loading course " + target.Token;
            return false;
        }

        static readonly FieldInfo AllZonesField = AccessTools.Field(typeof(ZoneLoader), "allZones");

        // ZoneLoader.LoadZone applies a zone two frames after it is asked (Movement.Start and /api/oracle/bootstrap
        // both ask); until then every zone is live, and the first run after a launch died on Overgrowth spikes.
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
            _solverRunning = false;
            _solverStatus = status;
        }

        object BuildSolverWebState()
        {
            return new {
                running = _solverRunning, status = _solverStatus, course = _solverCourse, capabilities = _solverCaps,
                speed = _solverSpeed + (_branchChecks == 0 ? "" : " - branch runs matching a full replay: " + _branchMatches + "/" + _branchChecks),
                evaluations = _solverEvaluations, finishes = _solverFinishes, cells = _archive.Count,
                best = float.IsInfinity(_solverBest) ? (float?)null : _solverBest,
                log = _solverLog.AsEnumerable().Reverse().Take(12).ToArray(),
                courses = _courses.Select(c => c.id).ToArray() };
        }

        // The studio's sidebar groups category routes by these; a course with a reward box is a bonus.
        object[] BuildCourseWebState()
        {
            return _courses.Select(c => (object)new { id = c.id, token = c.Token, bonus = c.HasFinish, minLevel = c.MinLevel }).ToArray();
        }

        // Snapshots hold world positions. After a FloatingOrigin shift they would put the player where the course no
        // longer is, so the start state, the archive and the best's states are rebuilt (the best replays as a seed).
        Vector2 _startOrigin;

        void ForgetStartState()
        {
            _startState = null;
            _archive.Clear();
            _bestStates = null;
            _bestArrival.Clear();
            if (_solverBestInputs != null) _solverSeeds.Enqueue(_solverBestInputs);
            _solverBest = float.PositiveInfinity;
            _solverBestInputs = null;
        }

        void PumpSolver()
        {
            if (!_solverRunning || _engine.IsRunning || _recorder.IsRecording) return;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                if (_startState != null && CurrentOrigin() != _startOrigin) ForgetStartState();
                if (_startState == null && !CourseLoaded()) return;
                while (clock.ElapsedMilliseconds < 80)
                {
                    if (_solverSeeds.Count > 0) { Try(_solverSeeds.Dequeue(), "seed"); continue; }
                    if (_solverBestInputs == null || _rng.NextDouble() < .5) Explore();
                    else Optimize();
                }
                _solverStatus = (_solverBestInputs == null ? "Exploring for a first finish" : "Exploring and optimizing")
                    + (_solverBestInputs == null ? "" : " - no improvement for " + (_solverEvaluations - _solverBestAt) + " runs");
                if (!_profWall.IsRunning) _profWall.Start();
                if (_profWall.ElapsedMilliseconds >= 2000 && _profRuns > 0)
                {
                    _solverSpeed = string.Format("{0:0} runs/s - {1:0} ticks/run - reset {2:0.00} ms/run - step {3:0} us/tick - solver busy {4:0}% of wall time",
                        _profRuns * 1000.0 / _profWall.ElapsedMilliseconds, (double)_profTicks / _profRuns, _profSetupMs / _profRuns,
                        (_profRunMs - _profSetupMs) * 1000.0 / Math.Max(1, _profTicks), _profRunMs * 100.0 / _profWall.ElapsedMilliseconds);
                    _profRuns = 0; _profTicks = 0; _profSetupMs = _profRunMs = 0; _profWall.Restart();
                }
            }
            catch (Exception ex) { StopSolver("Solver error: " + ex.Message); Logger.LogError(ex); }
        }

        void Explore()
        {
            if (_archive.Count == 0) { Try(RandomBurst(new List<SolverTick>()), "explore"); return; }
            // Count-based cell choice (rarely picked first), favoring cells reached with the timer running.
            double total = 0;
            foreach (KeyValuePair<long, Cell> pair in _archive) total += Weight(pair.Key, pair.Value);
            double pick = _rng.NextDouble() * total;
            Cell chosen = null;
            foreach (KeyValuePair<long, Cell> pair in _archive)
            {
                chosen = pair.Value;
                if ((pick -= Weight(pair.Key, pair.Value)) <= 0) break;
            }
            chosen.picks++;
            Try(RandomBurst(chosen.source.Take(chosen.length).Select(t => t.Clone()).ToList()), "explore", chosen.state);
        }

        static double Weight(long key, Cell cell) { return (key < 0 ? 3.0 : 1.0) / Math.Sqrt(cell.picks + 1); }

        List<SolverTick> RandomBurst(List<SolverTick> inputs)
        {
            bool dash = CapsLevel(_solverCaps) >= 2;
            float x = inputs.Count > 0 ? inputs[inputs.Count - 1].x : 1f;
            for (int chunk = _rng.Next(1, 4); chunk > 0; chunk--)
            {
                if (_rng.NextDouble() < .35) x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                int length = _rng.Next(3, 21);
                int start = inputs.Count;
                for (int i = 0; i < length; i++) inputs.Add(new SolverTick { x = x });
                if (_rng.NextDouble() < .4) Hop(inputs, start, new[] { 1, 3, 6, 10, 16 }[_rng.Next(5)]);
                if (dash && _rng.NextDouble() < .25)
                {
                    SolverTick d = inputs[start + _rng.Next(Math.Min(length, 4))];
                    d.dash = true;
                    // Jump mid-dash: a super jump wherever the dash touches ground, ledge corners included.
                    if (_rng.NextDouble() < .4) d.dashJump = _rng.Next(1, 11);
                }
            }
            return inputs;
        }

        static void Hop(List<SolverTick> t, int at, int hold)
        {
            if (at >= t.Count) return;
            t[at].press = true; t[at].held = true; t[at].release = false;
            for (int i = at + 1; i < Math.Min(t.Count, at + hold); i++) t[i].held = true;
            if (at + hold < t.Count) { t[at + hold].held = false; t[at + hold].release = true; }
        }

        void Optimize()
        {
            List<SolverTick> t = _solverBestInputs.Select(tick => tick.Clone()).ToList();
            int n = t.Count, p = _rng.Next(n);
            string[] kinds = CapsLevel(_solverCaps) >= 2
                ? new[] { "cut", "cut", "hop", "nojump", "stick", "insert", "dash", "dash", "dash", "dashjump", "dashjump", "latejump", "latejump" }
                : new[] { "cut", "cut", "hop", "nojump", "stick", "insert" };
            switch (kinds[_rng.Next(kinds.Length)])
            {
                case "cut": Cut(t, p, new[] { 1, 1, 2, 3, 5, 8 }[_rng.Next(6)]); break;
                case "insert":
                    // Back up for a run-up, wait out a hazard, or extend a hold.
                    float x = new[] { -1f, 0f, 1f }[_rng.Next(3)];
                    int count = new[] { 1, 2, 4, 8, 15 }[_rng.Next(5)];
                    for (int i = 0; i < count; i++) t.Insert(p, new SolverTick { x = x, held = t[p].held });
                    break;
                case "dash":
                    // A dash only pays off with the run after it retimed.
                    t[p].dash = true;
                    Cut(t, p + _rng.Next(1, 25), _rng.Next(0, 20));
                    break;
                case "dashjump":
                    t[p].dash = true;
                    int j = Math.Min(n - 1, p + _rng.Next(0, 7));
                    Hop(t, j, new[] { 2, 5, 10, 16 }[_rng.Next(4)]);
                    Cut(t, j + _rng.Next(1, 25), _rng.Next(0, 20));
                    break;
                case "latejump":
                    // Super jump late in a ground dash: keeps the dash distance and gets the dash back.
                    t[p].dash = true;
                    t[p].press = false;
                    t[p].dashJump = _rng.Next(1, 11);
                    for (int i = p + 1; i < Math.Min(n, p + 1 + new[] { 0, 2, 5, 10 }[_rng.Next(4)]); i++) t[i].held = true;
                    Cut(t, p + _rng.Next(1, 25), _rng.Next(0, 20));
                    break;
                case "hop":
                    Hop(t, p, new[] { 1, 2, 4, 7, 11, 16 }[_rng.Next(6)]);
                    Cut(t, p + _rng.Next(1, 30), _rng.Next(0, 10));
                    break;
                case "nojump":
                    List<int> presses = Enumerable.Range(0, n).Where(i => t[i].press || t[i].dash).ToList();
                    if (presses.Count == 0) return;
                    int q = presses[_rng.Next(presses.Count)];
                    t[q].press = t[q].dash = false;
                    t[q].dashJump = 0;
                    break;
                case "stick":
                    float aim = new[] { -1f, 0f, 1f, Mathf.Clamp(t[p].x + (_rng.NextDouble() < .5 ? -.1f : .1f), -1f, 1f) }[_rng.Next(4)];
                    for (int i = p; i < Math.Min(n, p + _rng.Next(1, 15)); i++) t[i].x = aim;
                    break;
            }
            // Resume from the best route's state just before the first input this mutation changed.
            int k = 0;
            while (k < t.Count && k < _solverBestInputs.Count && Same(t[k], _solverBestInputs[k])) k++;
            k = Math.Min(Math.Min(k, t.Count - 1), _bestStates.Length - 1);
            while (k > 0 && _bestStates[k] == null) k--;
            Try(t, "optimize", k > 0 ? _bestStates[k] : null);
        }

        static bool Same(SolverTick a, SolverTick b)
        {
            return a.x == b.x && a.held == b.held && a.press == b.press && a.release == b.release && a.dash == b.dash && a.dashJump == b.dashJump;
        }

        static void Cut(List<SolverTick> t, int at, int count)
        {
            if (at < t.Count) t.RemoveRange(at, Math.Min(count, t.Count - at));
        }

        // Runs inputs in the real game (resuming from `from` when given), files every map cell reached under its
        // earliest prefix, and keeps the fastest timed finish as the category's route. A new best is always
        // re-run from the course start, since a resumed run can differ slightly; the replay's time is what counts.
        void Try(List<SolverTick> inputs, string mode, TurboRunner.Snapshot from = null)
        {
            if (inputs.Count == 0) inputs.Add(new SolverTick());
            if (from != null && from.tick >= inputs.Count) from = null;
            _solverEvaluations++;
            bool finished = Evaluate(inputs, from, null, null, mode == "optimize", out float time, out _, out Vector2 end);
            if (from != null && _rng.Next(100) == 0)
            {
                // The branch never judged the prefix (its source's), so the replay judges the same ticks only.
                bool matchFinished = Evaluate(inputs, null, null, null, mode == "optimize", out float replayTime, out _, out Vector2 replayEnd, from.tick);
                _branchChecks++;
                if (matchFinished == finished && Mathf.Abs(replayTime - time) < .0001f && (replayEnd - end).sqrMagnitude < .01f) _branchMatches++;
                else Logger.LogInfo("Branch mismatch (" + mode + ", resumed at input " + from.tick + " of " + inputs.Count + "): branch "
                    + finished + " " + time + " " + end + ", replay " + matchFinished + " " + replayTime + " " + replayEnd);
            }
            if (!finished) return;
            _solverFinishes++;
            if (time >= _solverBest - .0001f) return;
            var states = new TurboRunner.Snapshot[inputs.Count];
            var arrivals = new Dictionary<long, float>();
            if (!Evaluate(inputs, null, states, arrivals, false, out float replayed, out int finishTick, out _) || replayed >= _solverBest - .0001f)
            {
                _solverLog.Add(time.ToString("0.00") + "s " + mode + " branch did not hold up in a replay from the start");
                return;
            }
            List<SolverTick> best = inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList();
            // A seed that only ties the category's saved route is the search's starting best, not a new route.
            if (replayed < CategoryBest(SolverCategory) - .0001f && !SaveSolverRoute(best, replayed)) return;
            _solverBest = replayed; _solverBestAt = _solverEvaluations;
            _solverBestInputs = best;
            _bestStates = states;
            _bestArrival = arrivals;
            _solverLog.Add(replayed.ToString("0.00") + "s via " + mode + " after " + _solverEvaluations + " runs");
        }

        // ---- Route rules. Every category route is saved through SaveSolverRoute, which replays it from the course start
        // and refuses it unless every tick obeys what a human with that category's unlocks can do. The checks read the
        // game's own state, so they hold however inputs are injected: every buffer the game holds must come from a press
        // or release MacroEngine made within the game's window, counted in physics ticks (MacroEngine.Clock).
        const int JumpBufferTicks = 12;   // game: 0.12 s, 0.2 s mid-dash (10 ticks), +2 slack
        const int DashBufferTicks = 8;    // game: 0.12 s (6 ticks), +2 slack
        const int JumpCutBufferTicks = 15; // game: 0.26 s after a release (13 ticks), +2 slack
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
            if (GetMovementBool(player, "wallJumpUnlocked", false) != (CapsLevel(_solverCaps) >= 1)
                || GetMovementBool(player, "dashUnlocked", false) != (CapsLevel(_solverCaps) >= 2)
                || !Equals(GetMovementField(player, "maxAirDashes"), CapsLevel(_solverCaps) >= 2 ? 1 : 0)
                || GetMovementBool(player, "doubleJumpUnlocked", false) != (CapsLevel(_solverCaps) >= 3)
                || !Equals(GetMovementField(player, "maxAirJumps"), CapsLevel(_solverCaps) >= 3 ? 1 : 0))
                _audit.Add("tick " + tick + ": unlocks differ from category " + CapabilityLabels[_solverCaps]);
        }

        // Null when the route may be saved, else why not.
        string RouteViolation(List<SolverTick> inputs, float time)
        {
            var audit = _audit = new List<string>();
            bool finished;
            float replayed;
            try { finished = Evaluate(inputs, null, null, null, false, out replayed, out _, out _); }
            finally { _audit = null; }
            if (!finished || Mathf.Abs(replayed - time) > .0001f)
                audit.Insert(0, "a replay from the course start " + (finished ? "finished in " + replayed.ToString("0.00") : "did not finish on course " + _solverCourse));
            return audit.Count == 0 ? null : string.Join("; ", audit);
        }

        // `states`/`arrivals`, when given, receive a snapshot after every input tick and the course time of each
        // timed cell's first visit. `cut` stops the run once it falls behind the best route (see _bestArrival); a run
        // is judged behind only after input tick `judgeAfter`.
        bool Evaluate(List<SolverTick> inputs, TurboRunner.Snapshot from, TurboRunner.Snapshot[] states,
            Dictionary<long, float> arrivals, bool cut, out float time, out int finishTick, out Vector2 end, int judgeAfter = -1)
        {
            Movement player = FindWorldMovement();
            if (from == null) from = _startState;
            TurboRunner.Snapshot settling = null;
            CourseDef course = _courses.First(c => c.id == _solverCourse);
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
                // A run cut short mid-course leaves its course timing; clear it like a real restart would.
                foreach (courseScript c in _solverCourses) if (c != null) CourseTrackingFlag.SetValue(c, false);
                _pendingSilentMacro = new MacroDefinition { id = "solver", name = "Solver", steps = ToSteps(inputs) };
            }
            else
            {
                TurboRunner.Restore(from, player);
                BeginDeterministicPhysics(player);
                int step = from.tick;
                while (step > 0 && Merges(inputs[step - 1], inputs[step])) step--;
                _engine.Resume(from.engine, ToSteps(inputs.Skip(step).ToList()), from.tick - step);
            }
            // The run `from` came from already reported its tick (ticks mid-dash take no input, so RunTicks can stay put).
            int lastTick = from != null && from.tick > 0 ? from.tick : -1, idleTicks = 0;
            bool losing = false;
            Stopwatch clock = Stopwatch.StartNew();
            double setupMs = -1;
            try
            {
                int stepped = TurboRunner.Run(player, () => _pendingSilentMacro != null || (_engine.IsRunning && idleTicks < 20 && !losing),
                    inputs.Count - (from == null ? 0 : from.tick) + 400, () =>
                    {
                        if (setupMs < 0) setupMs = clock.Elapsed.TotalMilliseconds;
                        _engine.Clock++;
                        _engineSimTickHook?.Invoke(player);
                        if (!_engine.IsRunning) return;
                        if (_engine.InputsExhausted) idleTicks++;
                        int tick = (int)_engine.RunTicks;
                        if (_startState == null && tick == 0) settling = TurboRunner.Capture(player, _solverCourses, _engine.CaptureState(), 0);
                        if (_startState == null && tick > 0) { _startState = settling; _startOrigin = CurrentOrigin(); }
                        // Every tick, dash ticks (which take no input) included.
                        if (_audit != null) AuditTick(player, tick);
                        if (tick == lastTick) return;
                        lastTick = tick;
                        courseScript active = _solverCourses.FirstOrDefault(c => c != null && c.courseNumber == _solverCourse && CourseTrackingFlag.GetValue(c) is bool tracking && tracking);
                        // A bonus course has no course timer; its runs are timed in ticks from the run start.
                        bool timing = course.HasFinish || active != null;
                        long key = CellKey(player.transform.position, timing);
                        float elapsed = -1f;
                        if (course.HasFinish) elapsed = tick * Time.fixedDeltaTime;
                        else if (timing && CourseCurrentPathTimeField.GetValue(active) is float pathTime) elapsed = pathTime;
                        if (elapsed >= 0f)
                        {
                            // Already slower than the best finish: nothing later in this run can matter.
                            if (tick > judgeAfter && elapsed > _solverBest) losing = true;
                            if (cut && tick > judgeAfter && _bestArrival.TryGetValue(key, out float bestAt) && elapsed > bestAt + SegmentSlack) losing = true;
                            if (arrivals != null && !arrivals.ContainsKey(key)) arrivals[key] = elapsed;
                        }
                        FileCell(inputs, Math.Min(tick, inputs.Count), player, key, states);
                    }, from);
                if (_engine.IsRunning) _engine.Abort("Solver run ended");
                _profRuns++; _profTicks += stepped;
                _profSetupMs += Math.Max(0, setupMs); _profRunMs += clock.Elapsed.TotalMilliseconds;
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

        static long CellKey(Vector2 position, bool timing)
        {
            long key = ((long)Mathf.FloorToInt(position.x / CellSize) << 24) ^ (Mathf.FloorToInt(position.y / CellSize) & 0xFFFFFF);
            return timing ? -1 - Math.Abs(key) : key;
        }

        void FileCell(List<SolverTick> inputs, int tick, Movement player, long key, TurboRunner.Snapshot[] states)
        {
            bool better = !_archive.TryGetValue(key, out Cell cell) || cell.length > tick;
            // Only a state that is still consuming inputs can be resumed with different ones.
            bool resumable = tick > 0 && tick < inputs.Count && !_engine.InputsExhausted;
            TurboRunner.Snapshot state = resumable && (better || states != null)
                ? TurboRunner.Capture(player, _solverCourses, _engine.CaptureState(), tick) : null;
            if (states != null && state != null) states[tick] = state;
            // A cell reached sooner is new ground (a skip or a faster line): picks restart so it gets explored.
            // ponytail: one snapshot per cell kept in memory (~10 KB each); evict if cells reach the 100k range.
            if (better) _archive[key] = new Cell { source = inputs, length = tick, state = state };
        }

        // "<course token>_<CAPS>": the solver's course and category as category routes name them ("bonus1_WJ").
        string SolverCategory { get { return _courses.Find(c => c.id == _solverCourse).Token + "_" + CapabilityLabels[_solverCaps]; } }

        // The only place a category route is saved. False (and nothing saved) when the route breaks a rule.
        bool SaveSolverRoute(List<SolverTick> inputs, float time)
        {
            string category = SolverCategory;
            string violation = RouteViolation(inputs, time);
            if (violation != null)
            {
                string line = "REJECTED " + category + " " + time.ToString("0.00") + ": " + violation;
                _solverLog.Add(line);
                Logger.LogWarning(line);
                return false;
            }
            var route = new MacroDefinition { name = category + " " + time.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                steps = ToSteps(inputs) };
            route.timeline = TimelineDocument.FromSequentialSteps(route.steps, Time.fixedDeltaTime * 1000f);
            _library.macros.Add(route);
            PruneSlowerRoutes();
            SaveLibrary();
            return true;
        }

        static readonly Regex CategoryRoute = new Regex(@"^([^_\s]+_(?:NO_WJ|WJDASH|WJ_DASH_AJ|WJ)) ([\d.]+)$", RegexOptions.IgnoreCase);

        // Keeps only the fastest "<course>_<CAPS> <time>" route per category; favorites are never removed. True if any was removed.
        bool PruneSlowerRoutes()
        {
            var fastest = new Dictionary<string, float>();
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (!g.Success) continue;
                string key = g.Groups[1].Value.ToUpperInvariant();
                float time = float.Parse(g.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (!fastest.TryGetValue(key, out float best) || time < best) fastest[key] = time;
            }
            var kept = new HashSet<string>();
            int removed = _library.macros.RemoveAll(m =>
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (!g.Success || m.favorite) return false;
                string key = g.Groups[1].Value.ToUpperInvariant();
                // Ties: keep the first route listed.
                if (float.Parse(g.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) > fastest[key] || !kept.Add(key)) return true;
                return false;
            });
            _library.selectedIndex = Mathf.Clamp(_library.selectedIndex, 0, Math.Max(0, _library.macros.Count - 1));
            return removed > 0;
        }

        static List<SolverTick> ToTicks(List<MacroStep> steps)
        {
            var ticks = new List<SolverTick>();
            foreach (MacroStep s in steps)
                for (int i = 0; i < Math.Max(1, s.durationTicks); i++)
                    ticks.Add(new SolverTick { x = s.rawMoveX, held = s.rawJumpHeld,
                        press = i == 0 && s.rawJumpPressed, release = i == 0 && s.rawJumpReleased, dash = i == 0 && s.rawDashPressed,
                        dashJump = i == 0 && s.rawDashPressed ? s.dashJumpFrame : 0 });
            return ticks;
        }

        // ToSteps' rule: `k` continues the step `previous` is in.
        static bool Merges(SolverTick previous, SolverTick k)
        {
            return !k.press && !k.release && !k.dash && previous.x == k.x && previous.held == k.held;
        }

        static List<MacroStep> ToSteps(List<SolverTick> ticks)
        {
            var steps = new List<MacroStep>();
            foreach (SolverTick k in ticks)
            {
                MacroStep last = steps.Count > 0 ? steps[steps.Count - 1] : null;
                if (last != null && !k.press && !k.release && !k.dash && last.rawMoveX == k.x && last.rawJumpHeld == k.held)
                {
                    last.durationTicks++;
                    last.durationMs = Mathf.RoundToInt(last.durationTicks * Time.fixedDeltaTime * 1000f);
                    continue;
                }
                steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1,
                    durationMs = Mathf.RoundToInt(Time.fixedDeltaTime * 1000f), endCondition = ActionEndCondition.FixedTicks,
                    direction = k.x > 0f ? 1 : k.x < 0f ? -1 : 0, rawMoveX = k.x, rawJumpHeld = k.held,
                    rawJumpPressed = k.press, rawJumpReleased = k.release, rawDashPressed = k.dash,
                    dashJumpFrame = k.dash ? k.dashJump : 0 });
            }
            return steps;
        }
    }
}
