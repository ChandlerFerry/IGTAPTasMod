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
    // The studio's Optimize tab. The search runs offline (`IGTAP.EngineSim solve`, AGENTS.md rule 7); the game only
    // captures the start state and world the offline engine loads, and judges every offline best in turbo
    // (/api/enginesim/submit), saving it through the gate (SaveSolverRoute) when it holds up and is faster.
    public sealed partial class TasPlugin
    {
        public sealed class CourseDef
        {
            public int id;
            // Category routes are "<course>_<CAPS> <time>"; a named course ("bonus1") takes its name as the course.
            public string name;
            public float x, y;
            // A bonus course has no start/stop gates: its run finishes when the player touches this box (the
            // reward's buy trigger) and is timed in physics ticks from the run start. Zero size = a gated course.
            public float endX, endY, endW, endH;
            // A transition runs from `from` (a course "2" or a bonus "bonus2"; start x, y) to `to`: course `toCourse`'s
            // start gate starting its timer, or for a bonus the end box around the bonus's start. Timed in physics ticks
            // from the run start; finishing any course on the way is not a finish.
            public string from, to;
            public int toCourse;
            // The narrowest category the course is run in (none|wj|wjdash|wjdashaj): Course 2 needs wall jump,
            // Course 3 wall jump + dash, Course 4 all three. Missing = every category.
            public string minCapabilities;
            [JsonIgnore] public bool HasFinish { get { return endW > 0f && endH > 0f; } }
            [JsonIgnore] public bool IsTransition { get { return !string.IsNullOrEmpty(to); } }
            [JsonIgnore] public bool IsBonus { get { return HasFinish && !IsTransition; } }
            [JsonIgnore] public string Token { get { return string.IsNullOrEmpty(name) ? id.ToString() : name; } }
            [JsonIgnore] public int MinLevel { get { return CapsLevel(minCapabilities); } }
            // A bonus is only run in its first category (the unlocks it needs); courses and transitions in every wider one too.
            public bool HasCategory(string caps) { int level = CapsLevel(caps); return level >= MinLevel && (!IsBonus || level == MinLevel); }
        }

        sealed class SolverTick
        {
            public float x; public bool held, press, release, dash; public int dashJump;
            public SolverTick Clone() { return (SolverTick)MemberwiseClone(); }
        }


        static readonly FieldInfo CourseTrackingFlag = AccessTools.Field(typeof(courseScript), "tracking");

        List<CourseDef> _courses = new List<CourseDef>();
        MacroDefinition _pendingSilentMacro;
        bool _silentEvaluation, _silentFinished;
        float _silentTime;

        bool _solverRunning;
        string _solverStatus = "Idle", _solverSpeed = "";
        // What every turbo run is made for (set per run by SelectSolverTarget): the course, bonus or transition it is
        // scored on, where its start is (the chain's first segment for a chain segment), and the chain segment if any.
        CourseDef _solverTarget, _solverPark;
        ChainDef _solverChain;
        List<ChainSegment> _solverSegments;
        int _solverSegment = -1;
        bool _solverRestart;
        string _solverCaps = "";
        courseScript[] _solverCourses;
        // The course start as the first turbo run reached it (respawned and settling, one tick before its first
        // input). Every later run resumes here: a respawn alone leaves state from the previous run behind, and that
        // produced finishes that die in real play.
        TurboRunner.Snapshot _startState;
        readonly List<string> _solverLog = new List<string>();

        // The Optimize tab's run: the offline solve process and what it has reported (its stdout, read on the main thread).
        Process _solveProcess;
        readonly ConcurrentQueue<string> _solveOutput = new ConcurrentQueue<string>();
        int _solveCourse;
        string _solveCaps = "";
        // A chain segment instead of a course (the Optimize tab's chain solve).
        string _solveChain;
        int _solveSegment = -1;
        long _solveRuns, _solveFinishes;
        int _turboChecks, _turboSaved;
        float _solveBest = float.PositiveInfinity;
        const string SolveLimits = " --minutes 60 --stall-minutes 5";

        string CoursesPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "courses.json"); } }

        void LoadCourses()
        {
            try
            {
                if (File.Exists(CoursesPath)) { _courses = JsonConvert.DeserializeObject<List<CourseDef>>(File.ReadAllText(CoursesPath)); return; }
            }
            catch (Exception ex) { Logger.LogWarning("courses.json rejected: " + ex.Message); }
            // Start points (quick restarts for 1-5); the game places the player 12 px below. Course ids are the game's
            // courseNumber; bonus areas hold 101+. Bonuses have no gates and finish on a free secret box's buy trigger:
            // bonus1 starts in the course 2 secret shaft (box at the corridor's end); bonus2 on the ledge past Course 2's
            // top where Course2_To_Bonus2 arrives (course 3's GLOBALcash box); bonus3 at Course 3's start (course 3's
            // GLOBALclonespeedmax box, right of the course).
            _courses = new List<CourseDef> {
                new CourseDef { id = 1, x = 2241f, y = -292f, minCapabilities = "none" },
                new CourseDef { id = 2, x = 4783.43359f, y = 1878.2251f, minCapabilities = "wj" },
                new CourseDef { id = 3, x = 9505f, y = 6967.01465f, minCapabilities = "wjdash" },
                new CourseDef { id = 4, x = 20200.4004f, y = 10554.5449f, minCapabilities = "wjdashaj" },
                new CourseDef { id = 5, x = -3281f, y = -295.455078f, minCapabilities = "wjdashaj" },
                new CourseDef { id = 101, name = "bonus1", x = 7392.427f, y = 2807.015f, minCapabilities = "wj",
                    endX = 5239.43f, endY = 681f, endW = 127.5f, endH = 6f },
                new CourseDef { id = 102, name = "bonus2", x = 7670f, y = 5207.015f, minCapabilities = "wjdash",
                    endX = 8622f, endY = 5622.977f, endW = 127.5f, endH = 6f },
                new CourseDef { id = 103, name = "bonus3", x = 9505f, y = 6967.01465f, minCapabilities = "wjdash",
                    endX = 11200.0996f, endY = 7363.977f, endW = 127.5f, endH = 6f } };
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

        // "Course1_To_Course2_WJ", "Course2_To_Bonus2", "Bonus2_To_Course3" (category suffix optional).
        static readonly Regex TransitionName = new Regex(@"^(Course|Bonus)(\d+)_To_(Course|Bonus)(\d+)(?:_(NO_WJ|WJDASH|WJ_DASH_AJ|WJ))?\b", RegexOptions.IgnoreCase);
        // A transition's end box when it ends at a bonus: around where the player stands at the bonus's start.
        const float BonusArrivalSize = 32f;

        // A transition from a recording named "<Course|Bonus><n>_To_<Course|Bonus><m>[_<CAPS>]". It starts at the course's
        // start (or where the recording starts, for a bonus) and ends when course m's start gate starts its timer, or on
        // reaching bonus m's start (a bonus course's start, a transition's from it, or a "Bonus<m>_To_..." recording's
        // first position). Transitions hold ids 201+ and are named "<from>to<to>" ("1to2", "2tobonus2"); the
        // recording becomes a seed route "<token>_REC <recording>". Without a category suffix the category floor is
        // the save's unlocks (what it was recorded with).
        object AddTransitionFromRecording(RecordingDefinition recording)
        {
            if (recording.samples == null || recording.samples.Count == 0) throw new InvalidOperationException("The recording is empty.");
            Match m = TransitionName.Match(recording.name ?? "");
            if (!m.Success)
                throw new InvalidOperationException("Name the recording \"Course1_To_Course2\" or \"Course2_To_Bonus2\" (optionally with _WJ, _WJDASH, ...) first.");
            bool fromBonus = m.Groups[1].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase);
            bool toBonus = m.Groups[3].Value.Equals("bonus", StringComparison.OrdinalIgnoreCase);
            string origin = (fromBonus ? "bonus" : "") + m.Groups[2].Value, target = (toBonus ? "bonus" : "") + m.Groups[4].Value;
            RawTickSample first = recording.samples[0];
            // The player stands 12 px below a start (a quick restart there).
            Vector2 start = new Vector2(first.positionX, first.positionY + 12f);
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
            // Recordings run to a death or a goal; a transition to a bonus ends at the end box, so its seed stops there
            // (one tick after the player's centre is inside: the colliders overlap it by then at the latest).
            List<RawTickSample> samples = recording.samples;
            if (transition.HasFinish)
            {
                int arrival = samples.FindIndex(t => Mathf.Abs(t.positionX - transition.endX) <= transition.endW / 2f
                    && Mathf.Abs(t.positionY - transition.endY) <= transition.endH / 2f);
                if (arrival >= 0 && arrival + 2 < samples.Count) samples = samples.GetRange(0, arrival + 2);
            }
            CreateRouteFromRecording(recording, samples);
            _library.macros[_library.macros.Count - 1].name = transition.Token + "_REC " + recording.name;
            SaveLibrary();
            return new { ok = true, course = transition.id, token = transition.Token };
        }

        // Where a bonus ("bonus2") starts: its bonus course, a transition from it, or a "Bonus2_To_..." recording.
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
                start = new Vector2(r.samples[0].positionX, r.samples[0].positionY + 12f);
                return true;
            }
            start = Vector2.zero;
            return false;
        }

        static readonly Dictionary<string, string> CapabilityLabels = new Dictionary<string, string> {
            { "none", "NO_WJ" }, { "wj", "WJ" }, { "wjdash", "WJDASH" }, { "wjdashaj", "WJ_DASH_AJ" } };

        // Course 2 has no NO_WJ category, Course 3 starts at WJDASH, Course 4 at WJ_DASH_AJ (courses.json minCapabilities);
        // a bonus has only its first category.
        void CheckCategory(int course, string capabilities)
        {
            CourseDef def = _courses.Find(c => c.id == course);
            if (def != null && !def.HasCategory(capabilities))
                throw new InvalidOperationException((def.IsBonus ? "Bonus " : "Course ") + def.Token + " has no " + CapabilityLabels[capabilities]
                    + " category; it " + (def.IsBonus ? "is only run in " : "starts at ") + CapabilityLabels[def.minCapabilities] + ".");
        }

        // Where the build put `IGTAP.EngineSim.exe` (IGTAPTasMod.csproj: AssemblyMetadata "EngineSimExe").
        static string EngineSimExe
        {
            get { return typeof(TasPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "EngineSimExe")?.Value; }
        }

        void StartSolver(int course, string capabilities)
        {
            if (_courses.All(c => c.id != course)) throw new InvalidOperationException("Unknown course " + course + ".");
            if (!CapabilityLabels.ContainsKey(capabilities)) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, capabilities);
            BeginSolve(course, capabilities, null, -1);
        }

        // A chain segment, from where the chain's routes before it end (each of them must exist).
        void StartChainSolver(string chainName, int segment)
        {
            ChainDef chain = FindChain(chainName);
            List<ChainSegment> segments = ChainSegments(chain);
            if (segment < 0 || segment >= segments.Count) throw new InvalidOperationException("Chain " + chain.name + " has no segment " + (segment + 1) + ".");
            ChainRoutes(chain, segments, segment);
            BeginSolve(segments[segment].target.id, chain.capabilities, chain.name, segment);
        }

        void BeginSolve(int course, string capabilities, string chain, int segment)
        {
            if (_solverRunning) throw new InvalidOperationException("The solver is already running.");
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (CapabilitiesLocked) throw new InvalidOperationException(CapabilitiesLockedError);
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new InvalidOperationException("The offline solver is not built (" + exe + "): dotnet build enginesim\\Cli\\IGTAP.EngineSim.csproj -c Release");
            _solveCourse = course; _solveCaps = capabilities;
            _solveChain = chain; _solveSegment = segment;
            _solveRuns = _solveFinishes = 0;
            _turboChecks = _turboSaved = 0;
            _solveBest = float.PositiveInfinity;
            _solverSpeed = "";
            _solverLog.Clear();
            while (_solveOutput.TryDequeue(out _)) { }
            _solverRunning = true;
            _solverStatus = "Capturing the start state in turbo";
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
            // A chain segment's runs start where its chain starts: the chain's first segment.
            CourseDef target = _solverPark;
            Movement player = FindWorldMovement();
            Vector2 start = new Vector2(target.x, target.y) + CurrentOrigin();
            bool parked = player != null && ((Vector2)player.transform.position - start).sqrMagnitude < 64f * 64f;
            if (!parked)
            {
                RestoreRouteStart(new MacroDefinition { name = target.Token + "_" });
                _parkedFrame = Time.frameCount;
            }
            // A bonus course has no courseScript of its own; settled zones are all its runs need. A transition needs the
            // course whose start gate ends it.
            else if (Time.frameCount - _parkedFrame >= 10 && ZonesSettled() && (target.HasFinish
                || _solverCourses.Any(c => c.courseNumber == (target.IsTransition ? target.toCourse : target.id) && c.isActiveAndEnabled))) return true;
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
            if (_solveProcess != null)
            {
                try { if (!_solveProcess.HasExited) _solveProcess.Kill(); }
                catch (Exception ex) { Logger.LogWarning("Could not stop the offline solver: " + ex.Message); }
                _solveProcess.Dispose();
                _solveProcess = null;
            }
            _solverRunning = false;
            _solverStatus = status;
        }

        object BuildSolverWebState()
        {
            return new {
                running = _solverRunning, status = _solverStatus, course = _solveCourse, capabilities = _solveCaps, speed = _solverSpeed,
                chain = _solveChain, segment = _solveSegment,
                evaluations = _solveRuns, finishes = _solveFinishes, turboChecks = _turboChecks, turboSaved = _turboSaved,
                best = float.IsInfinity(_solveBest) ? (float?)null : _solveBest,
                log = _solverLog.AsEnumerable().Reverse().Take(12).ToArray(),
                courses = _courses.Select(c => c.id).ToArray() };
        }

        // The studio's sidebar groups category routes by these: a course with a reward box is a bonus, one with `to` a
        // transition ("2" to "bonus2").
        object[] BuildCourseWebState()
        {
            return _courses.Select(c => (object)new { id = c.id, token = c.Token, bonus = c.IsBonus, minLevel = c.MinLevel,
                @from = c.from, to = c.to }).ToArray();
        }

        // Snapshots hold world positions: after a FloatingOrigin shift the start state is recaptured (PrepareEngineSimRun).
        Vector2 _startOrigin;

        void PumpSolver()
        {
            if (!_solverRunning) return;
            try
            {
                if (_solveProcess == null) { LaunchSolve(); return; }
                while (_solveOutput.TryDequeue(out string line)) ReadSolveLine(line);
                if (!_solveProcess.HasExited) return;
                // Exited: the parameterless wait also waits for the redirected output to end.
                _solveProcess.WaitForExit();
                while (_solveOutput.TryDequeue(out string line)) ReadSolveLine(line);
                int code = _solveProcess.ExitCode;
                StopSolver(code == 0 ? "Finished" : "The offline solver exited with code " + code);
            }
            catch (Exception ex) { StopSolver("Solver error: " + ex.Message); Logger.LogError(ex); }
        }

        // Once the course is loaded: a turbo trace of the start state plus the world export (what
        // `tools/enginesim-capture.py start` writes), the course's library routes as seed files, then `solve`, which
        // submits each new offline best to /api/enginesim/submit for the turbo check.
        void LaunchSolve()
        {
            if (PlaybackActive || _recorder.IsRecording) return;
            JObject target = _solveChain != null ? new JObject { ["chain"] = _solveChain, ["segment"] = _solveSegment }
                : new JObject { ["course"] = _solveCourse, ["capabilities"] = _solveCaps };
            SelectSolverTarget(target);
            if (!CourseLoaded()) return;
            CourseDef course = _solverTarget;
            string name = _solverChain != null ? "optimize-chain-" + _solverChain.name + "-" + _solverSegment : "optimize-c" + course.Token + "-" + _solveCaps;
            JObject body = (JObject)target.DeepClone();
            body["ticks"] = new JArray(); body["name"] = name; body["world"] = true; body["freshStart"] = true;
            JObject captured = JObject.FromObject(EngineSimTrace(new WebStudioRequest(), body));
            if (captured["ok"] == null || !(bool)captured["ok"])
            {
                StopSolver("Start state not captured: " + captured["error"]);
                return;
            }
            var args = new StringBuilder(Quote((string)captured["path"]));
            foreach (string old in Directory.GetFiles(EngineSimDir, name + "-seed*.json")) File.Delete(old);
            // Every route already made for this course seeds the search, whatever category it was made for; a chain
            // segment's also its own earlier (and stale) routes.
            var prefixes = new List<string> { course.Token + "_" };
            if (_solverChain != null) prefixes.Add(_solverChain.name + "/" + course.Token + "_");
            int seeds = 0;
            foreach (MacroDefinition route in _library.macros.Where(m => prefixes.Any(p => m.name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                && m.steps.All(s => s.type == MacroStepType.RawInput)))
            {
                string seed = Path.Combine(EngineSimDir, name + "-seed" + seeds++ + ".json");
                File.WriteAllText(seed, JsonConvert.SerializeObject(new { ticks = ToTicks(route.steps) }));
                args.Append(" --seed ").Append(Quote(seed));
            }
            int threads = Math.Max(1, Environment.ProcessorCount - 2);
            args.Append(" --threads ").Append(threads).Append(SolveLimits);
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

        static readonly Regex SolveProgress = new Regex(@"^\[[\d:]+\] (\d+) runs/s, (\d+) ticks/s, (\d+) runs, (\d+) finishes");
        static readonly Regex SolveBest = new Regex(@"offline best ([\d.,]+)s via");

        // One line of solve's output: progress updates the counters, everything else (new bests, turbo checks) goes to the log.
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
            try { finished = Evaluate(inputs, out replayed, out _, out _); }
            finally { _audit = null; }
            if (!finished || Mathf.Abs(replayed - time) > .0001f)
                audit.Insert(0, "a replay from the " + (_solverChain != null ? "segment's" : "course") + " start "
                    + (finished ? "finished in " + replayed.ToString("0.00") : "did not finish on " + _solverTarget.Token));
            return audit.Count == 0 ? null : string.Join("; ", audit);
        }

        // A turbo run of `inputs` from the course start (_startState; the first run from a respawn captures it).
        bool Evaluate(List<SolverTick> inputs, out float time, out int finishTick, out Vector2 end)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot from = _startState, settling = null;
            CourseDef course = _solverPark;
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
                // The start state is taken at tick 0, before the first input.
                _engine.Resume(from.engine, ToSteps(inputs), 0);
                // A chain segment after a quick restart does it first (BeforeMovementFixedUpdate).
                _restartBeforeTick = _solverRestart;
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
                        // Every tick, dash ticks (which take no input) included.
                        if (_audit != null) AuditTick(player, tick);
                        lastTick = tick;
                    }, from);
                if (_engine.IsRunning) _engine.Abort("Solver run ended");
            }
            finally
            {
                _pendingSilentMacro = null;
                _restartBeforeTick = false;
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

        // "<course token>_<CAPS>": the solver's course and category as category routes name them ("bonus1_WJ"); a chain
        // segment's "<chain>/<token>_<CAPS>" ("wj/2tobonus1_WJ").
        string SolverCategory { get { return _solverChain != null ? ChainCategory(_solverChain, _solverSegments[_solverSegment])
            : _solverTarget.Token + "_" + CapabilityLabels[_solverCaps]; } }

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
            if (_solverChain != null) RevalidateChainTail();
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