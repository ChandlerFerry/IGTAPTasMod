using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.LowLevel;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        sealed class TasGreen
        {
            public int physics, input;
            public TurboRunner.Snapshot snapshot;
        }

        sealed class TasSession
        {
            public MacroDefinition route;
            public List<SolverTick> ticks;
            public readonly List<List<SolverTick>> undo = new List<List<SolverTick>>(), redo = new List<List<SolverTick>>();
            public readonly List<TasGreen> greenzone = new List<TasGreen>();
            public bool starting = true, held, playing, recording, reopening;
            public int release;
            public int holdAtInput = -1, holdAtPhysics = -1;
            public int seekInput = -1, seekPhysics = -1, seekAfterStart = -1;
            public bool refeed, reopen;
            public string recordStop;
            public int recordFrom;
            public float slowdown = 1f;
            public int playUntil = int.MaxValue;
            public bool playAfterSeek;
            public readonly List<float[]> trace = new List<float[]>();
            public int traceVersion;
            public int physics;
            public Vector2 stepFrom;
            public int version;
            public string ended;
            public bool finished;
            public float endTime;
            public int endPhysics;
            public bool dirty;
            public Process suggest;
            public System.Collections.Concurrent.ConcurrentQueue<string> suggestOutput;
            public string suggestFile, suggestError;
            public int suggestUntil;
            public bool suggestStale;
            public JObject suggestions;
            public Process techs;
            public System.Collections.Concurrent.ConcurrentQueue<string> techsOutput;
            public string techsFile;
            public long techsRead, heldSince;
            public long techsPolled;
            public int techsAt = -1, techsVersion = -1, heldAt = -1;
            public readonly Dictionary<int, TasTechs> techsCache = new Dictionary<int, TasTechs>();
            public string hoverTech, hoverDir;
            public float? hoverAngle;
            public int redrawAt = -1;
            public bool redrawAgain;

            public bool SavestateStart { get { return !string.IsNullOrEmpty(route.savestateId); } }

            public int BaseInput { get { return greenzone.Count == 0 ? 0 : greenzone[0].input; } }
        }

        const int TasGreenEvery = 25, TasUndoDepth = 200;

        struct TasHoldPoint { }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

        static readonly int[] TasKeys = { 0xDD, 0xDB, 0xDC, 0xDE, 0x77 };
        readonly bool[] _tasKeyDown = new bool[5];

        void InstallTasHoldPoint()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.EarlyUpdate)) continue;
                PlayerLoopSystem early = loop.subSystemList[i];
                var systems = new List<PlayerLoopSystem>(early.subSystemList ?? new PlayerLoopSystem[0]);
                systems.RemoveAll(s => s.type == typeof(TasHoldPoint));
                systems.Insert(0, new PlayerLoopSystem { type = typeof(TasHoldPoint), updateDelegate = TasBoundary });
                early.subSystemList = systems.ToArray();
                loop.subSystemList[i] = early;
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
            Logger.LogWarning("TAS editor: no EarlyUpdate in the player loop; frame advance is unavailable.");
        }

        static void RemoveTasHoldPoint()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                PlayerLoopSystem group = loop.subSystemList[i];
                if (group.subSystemList == null || !group.subSystemList.Any(s => s.type == typeof(TasHoldPoint))) continue;
                group.subSystemList = group.subSystemList.Where(s => s.type != typeof(TasHoldPoint)).ToArray();
                loop.subSystemList[i] = group;
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
        }

        void TasBoundary()
        {
            if (_tas == null || !_playbackPaced || TurboRunner.Active) return;
            if (Time.timeAsDouble - Time.fixedTimeAsDouble < Time.fixedDeltaTime) return;
            bool waited = false;
            Stopwatch clock = _tasHoldClock;
            clock.Restart();
            long keepAlive = 0;
            try
            {
                while (_tas != null && TasMustHold(_tas))
                {
                    if (!waited) { waited = true; AudioListener.pause = true; }
                    TurboRunner.KeepWindowAlive(clock, ref keepAlive);
                    _webServer?.Pump(HandleWebRequest);
                    TasHotkeys();
                    TasPollSuggest();
                    TasPollTechs();
                    Thread.Sleep(4);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("TAS editor: " + ex);
                TasClose("TAS editor error: " + ex.Message);
            }
            finally
            {
                if (waited) AudioListener.pause = false;
                if (waited) _paceDue = _paceClock.Elapsed.TotalSeconds;
            }
        }

        bool TasMustHold(TasSession s)
        {
            s.held = false;
            if (s.reopen) { TasReopen(s); return false; }
            if (s.starting)
            {
                if (!_engine.IsRunning) return _pendingReplay == null && TasHold(s);
                if (!_engine.StartsInputNextTick && _engine.RunTicks < 1) return false;
                TasCaptureBase(s);
                if (s.seekAfterStart > s.BaseInput) s.seekInput = s.seekAfterStart;
                s.seekAfterStart = -1;
            }
            if (s.recordStop != null) TasEndRecord(s);
            if (s.recording) return false;
            if (s.seekInput >= 0 || s.seekPhysics >= 0) return TasSeek(s);
            if (s.refeed) TasRefeed(s);
            if (s.ended != null) return TasHold(s);
            if (s.holdAtInput >= 0 || s.holdAtPhysics >= 0)
            {
                if (_engine.RunTicks < s.holdAtInput || s.physics < s.holdAtPhysics) return !TasEnsureInput(s);
                s.holdAtInput = s.holdAtPhysics = -1;
            }
            if (s.playAfterSeek) { s.playAfterSeek = false; s.playing = true; _walkSlowdown = s.slowdown > 1f ? s.slowdown : 0f; }
            if (s.release > 0)
            {
                s.release--;
                return !TasEnsureInput(s);
            }
            if (s.playing && _engine.RunTicks < Math.Min(s.ticks.Count, s.playUntil)) return false;
            return TasHold(s);
        }

        bool TasHold(TasSession s)
        {
            if (s.playing) { s.playing = false; _walkSlowdown = 0f; }
            s.playUntil = int.MaxValue;
            s.release = 0;
            s.held = true;
            s.redrawAt = -1;
            if (s.redrawAgain) TasRedraw(s);
            return true;
        }

        bool TasEnsureInput(TasSession s)
        {
            if (!_engine.IsRunning) return false;
            if (_engine.RunTicks < s.ticks.Count) return true;
            TasPushUndo(s);
            s.ticks.Add(new SolverTick());
            TasTicksChanged(s, s.ticks.Count - 1, false);
            TasRefeed(s);
            return true;
        }

        void TasCaptureBase(TasSession s)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot start = TurboRunner.Capture(player, AllCourses(), _engine.CaptureState(), (int)_engine.RunTicks);
            // Carry the last tick's interpolation ends as turbo does (TurboRunner._stepFrom/_stepTo); FixedUpdate reads the blend.
            start.stepFrom = s.stepFrom;
            start.stepTo = player.GetComponent<Rigidbody2D>().position;
            // Course-start restores put the world at its canonical origin exactly, as the gate's start does (Solver.Evaluate).
            start.canonical = _canonicalReference;
            s.greenzone.Clear();
            TasCutTrace(s, 0);
            s.greenzone.Add(new TasGreen { physics = 0, input = (int)_engine.RunTicks, snapshot = start });
            s.physics = 0;
            s.starting = false;
        }

        static courseScript[] AllCourses()
        {
            return FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        bool TasSeek(TasSession s)
        {
            int input = s.seekInput, physics = s.seekPhysics;
            s.seekInput = s.seekPhysics = -1;
            s.refeed = false;
            s.playing = false;
            s.release = 0;
            _walkSlowdown = 0f;
            TasGreen from = s.greenzone.LastOrDefault(g => input >= 0 ? g.input < input : g.physics < physics);
            if (from == null)
            {
                s.seekAfterStart = input >= 0 && input > 0 ? input : -1;
                TasReopen(s);
                return false;
            }
            Movement player = FindWorldMovement();
            s.ended = null;
            s.finished = false;
            TurboRunner.Restore(from.snapshot, player);
            _engine.ResumeAt(from.snapshot.engine, TasSteps(s, from.input));
            s.physics = from.physics;
            courseScript[] courses = AllCourses();
            int green = from.input;
            Func<bool> more = input >= 0 ? (Func<bool>)(() => _engine.RunTicks < input - 1) : () => s.physics < physics - 1;
            int most = input >= 0 ? (input - from.input) * 4 + 2000 : physics - from.physics + 10;
            TurboRunner.DropTimeStops = true;
            try
            {
                TurboRunner.Run(player, () => _engine.IsRunning && more(), most, () =>
                {
                    _engine.Clock++;
                    _engineSimTickHook?.Invoke(player);
                    int at = (int)_engine.RunTicks;
                    if (!_engine.IsRunning || at < green + TasGreenEvery) return;
                    green = at;
                    if (s.greenzone.Exists(g => g.input == at)) return;
                    var added = new TasGreen { physics = s.physics, input = at, snapshot = TurboRunner.Capture(player, courses, _engine.CaptureState(), at) };
                    int index = s.greenzone.FindIndex(g => g.physics > added.physics);
                    s.greenzone.Insert(index < 0 ? s.greenzone.Count : index, added);
                }, from.snapshot);
            }
            finally { TurboRunner.DropTimeStops = false; }
            TurboRunner.ShowLastFrame(player);
            if (input >= 0) { s.holdAtInput = Math.Min(input, s.ticks.Count); s.holdAtPhysics = -1; }
            else { s.holdAtPhysics = physics; s.holdAtInput = -1; }
            if (!_engine.IsRunning) { s.holdAtInput = -1; s.holdAtPhysics = s.physics + 1; }
            return !TasEnsureInput(s) && _engine.IsRunning;
        }

        void TasRefeed(TasSession s)
        {
            s.refeed = false;
            if (_engine.IsRunning) _engine.ResumeAt(_engine.CaptureState(), TasSteps(s, (int)_engine.RunTicks));
        }

        List<MacroStep> TasSteps(TasSession s, int from)
        {
            return from >= s.ticks.Count ? new List<MacroStep>() : ToSteps(s.ticks.GetRange(from, s.ticks.Count - from));
        }

        void TasReopen(TasSession s)
        {
            s.reopen = false;
            s.starting = true;
            s.greenzone.Clear();
            TasCutTrace(s, 0);
            s.ended = null;
            s.finished = false;
            s.playing = false;
            s.release = 0;
            s.holdAtInput = s.holdAtPhysics = -1;
            s.physics = 0;
            _walkSlowdown = 0f;
            s.reopening = true;
            try { if (_engine.IsRunning) _engine.Abort("TAS session restarted"); }
            finally { s.reopening = false; }
            _pendingReplay = s.route;
            _pendingReplayStartDelayTicks = 0;
        }

        void TasAfterTick(Movement player)
        {
            TasSession s = _tas;
            if (s == null) return;
            s.physics++;
            s.stepFrom = TurboRunner.BlendRead(player.transform);
            // BeforePlayerInput is skipped on dash and spring ticks, so they stay on the input before them.
            int input = (int)_engine.RunTicks - 1;
            if (s.starting || s.recording || input < s.BaseInput || !_engine.IsRunning) return;
            Vector2 at = (Vector2)TurboRunner.BlendRead(player.transform) - CurrentOrigin();
            Rigidbody2D body = player.GetComponent<Rigidbody2D>();
            Vector2 v = body != null ? body.linearVelocity : Vector2.zero;
            while (s.trace.Count <= input) s.trace.Add(null);
            s.trace[input] = new[] { s.physics, at.x, at.y, v.x, v.y };
        }

        void TasCutTrace(TasSession s, int from)
        {
            from = Math.Max(0, from);
            if (from >= s.trace.Count) return;
            s.trace.RemoveRange(from, s.trace.Count - from);
            s.traceVersion++;
        }

        void TasEngineEnded(bool success, string reason)
        {
            TasSession s = _tas;
            if (s == null || s.reopening) return;
            s.ended = reason;
            s.finished = success && MacroEngine.IsFinishReason(reason);
            s.endTime = _engine.AuthoritativeCompletionTime;
            s.endPhysics = s.physics;
        }

        static List<SolverTick> CloneTicks(List<SolverTick> ticks) { return ticks.Select(t => t.Clone()).ToList(); }

        void TasJournal(TasSession s)
        {
            s.dirty = true;
            WriteTasJournal(s.route);
        }

        void TasPushUndo(TasSession s)
        {
            s.undo.Add(CloneTicks(s.ticks));
            if (s.undo.Count > TasUndoDepth) s.undo.RemoveAt(0);
            s.redo.Clear();
        }

        void TasTicksChanged(TasSession s, int first, bool reseek = true)
        {
            s.version++;
            // A snapshot at input `first` may have pressed it already (a wall touch in a dash lock presses the next input early).
            TasGreen start = s.greenzone.FirstOrDefault();
            s.greenzone.RemoveAll(g => g != start && g.input >= first);
            TasCutTrace(s, first);
            if (first < s.suggestUntil) { s.suggestions = null; s.suggestStale = s.suggest != null; }
            s.route.steps = ToSteps(s.ticks);
            TasJournal(s);
            if (!reseek) return;
            if (s.starting) { s.refeed = true; return; }
            int cursor = (int)_engine.RunTicks;
            if (first > cursor && s.ended == null) { s.refeed = true; return; }
            if (first < s.BaseInput) { s.seekAfterStart = Math.Min(cursor, s.ticks.Count); s.reopen = true; return; }
            if (first == cursor && s.ended == null)
            {
                if (s.physics > 0) s.seekPhysics = s.physics;
                else { s.seekAfterStart = -1; s.reopen = true; }
                return;
            }
            s.seekInput = Math.Max(s.BaseInput + 1, Math.Min(s.ended != null ? s.ticks.Count : cursor, s.ticks.Count));
        }

        bool TasSetTicks(TasSession s, List<SolverTick> ticks, bool undo = true)
        {
            if (ticks.Count == 0) ticks.Add(new SolverTick());
            if (s.SavestateStart) TasQuiet(ticks[0]);
            int first = 0;
            while (first < ticks.Count && first < s.ticks.Count && JsonConvert.SerializeObject(ticks[first]) == JsonConvert.SerializeObject(s.ticks[first])) first++;
            if (first == ticks.Count && first == s.ticks.Count) return false;
            if (undo) TasPushUndo(s);
            s.ticks = ticks;
            TasTicksChanged(s, first);
            return true;
        }

        static bool TasPresses(SolverTick t) { return t.press || t.release || t.dash || t.reset || t.springDash != 0 || t.pause != 0; }

        // Savestate input 0 queues no game timer (buffer cancel, endRecentJump): the base snapshot is 1x, uncapturable invokes.
        static void TasQuiet(SolverTick t)
        {
            t.press = t.release = t.dash = t.reset = false;
            t.dashJump = t.springDash = t.pause = t.pauseTick = t.pauseFrame = 0;
            t.turnX = t.turnY = null;
        }

        static SolverTick TasSanitize(SolverTick t)
        {
            Vector2 stick = MacroEngine.ControllerAxis(t.x, t.y);
            t.x = stick.x;
            t.y = stick.y;
            if (!t.dash) { t.turnX = t.turnY = null; t.dashJump = 0; }
            if (t.turnX.HasValue) t.turnX = Mathf.Abs(t.turnX.Value) < 0.3f ? 0f : Mathf.Abs(t.turnX.Value) > 0.8f ? Mathf.Sign(t.turnX.Value) : t.turnX;
            if (t.turnY.HasValue) t.turnY = t.turnY > 0.4f ? 1f : t.turnY < -0.4f ? -1f : 0f;
            return t;
        }

        void TasStartRecord(TasSession s, float slowdown)
        {
            if (s.ended != null) throw new InvalidOperationException("The run has ended here; rewind first.");
            if (!s.held) throw new InvalidOperationException("Hold first.");
            int from = (int)_engine.RunTicks;
            TasPushUndo(s);
            if (from < s.ticks.Count) s.ticks.RemoveRange(from, s.ticks.Count - from);
            TasTicksChanged(s, from, false);
            s.recordFrom = from;
            s.recording = true;
            // Keep the engine running to take the finish but with no inputs left: a quick restart step would still fire.
            _engine.ResumeAt(_engine.CaptureState(), new List<MacroStep>());
            _walkSlowdown = slowdown > 1f ? slowdown : 0f;
            _recorder.Start(s.route.name + " re-record");
            _recorder.SeedEdges(FindWorldMovement());
            s.held = false;
        }

        void TasEndRecord(TasSession s)
        {
            string reason = s.recordStop;
            s.recordStop = null;
            if (!s.recording) return;
            s.recording = false;
            _walkSlowdown = 0f;
            // Read before Stop: Stop trims leading idle ticks, which here are part of the route.
            List<RawTickSample> samples = _recorder.Current != null ? new List<RawTickSample>(_recorder.Current.samples) : new List<RawTickSample>();
            _recorder.Stop(reason);
            List<SolverTick> recorded = ToTicks(ExactInputCompressor.Compress(samples));
            s.ticks.AddRange(recorded.Select(TasSanitize));
            if (s.ticks.Count == 0) s.ticks.Add(new SolverTick());
            if (s.SavestateStart) TasQuiet(s.ticks[0]);
            s.route.rerecords++;
            TasTicksChanged(s, s.recordFrom, false);
            Logger.LogInfo("TAS re-record: " + recorded.Count + " input ticks from " + s.recordFrom + " (" + reason + ")");
            s.seekInput = Math.Max(s.BaseInput + 1, s.recordFrom + recorded.Count);
        }

        void TasOpen(MacroDefinition route)
        {
            if (!route.steps.All(st => st.type == MacroStepType.RawInput))
                throw new InvalidOperationException("Only raw-input routes can be edited tick by tick.");
            if (FullTasRouteName.Match(route.name ?? "").Success)
                throw new InvalidOperationException("A Full-TAS route starts from a new game; the editor can't hold one yet.");
            List<SolverTick> ticks = ToTicks(route.steps.Where(st => st.enabled).ToList());
            if (!string.IsNullOrEmpty(route.savestateId) && ticks.Count > 0 && TasPresses(ticks[0]))
                throw new InvalidOperationException("Its first input presses something. A savestate session holds first after input 0, so input 0 "
                    + "must press nothing (the game's own buffer timers it starts can't be put back on a rewind): add a neutral first tick to a copy and open that.");
            if (_tas != null) TasClose("Another TAS session opened");
            if (_recorder.IsRecording) StopRecording("TAS session opened");
            StopPlayback("TAS session opened");
            int index = _library.macros.IndexOf(route);
            if (index >= 0 && index != _library.selectedIndex) { _library.selectedIndex = index; SaveSelection(); }
            var s = new TasSession { route = route, ticks = ticks };
            if (s.ticks.Count == 0)
            {
                s.ticks.Add(new SolverTick());
                route.steps = ToSteps(s.ticks);
                SaveLibrary();
            }
            _tas = s;
            EnsureGameUnpaused();
            _pendingReplay = route;
            _pendingReplayStartDelayTicks = 0;
            Logger.LogInfo("TAS session opened: " + route.name + " (" + s.ticks.Count + " input ticks)");
        }

        void TasClose(string reason)
        {
            TasSession s = _tas;
            if (s == null) return;
            if (s.recording) { s.recordStop = reason; TasEndRecord(s); }
            if (s.dirty) { SaveLibrary(); DeleteTasJournal(); }
            _tas = null;
            _walkSlowdown = 0f;
            if (s.suggest != null) { try { if (!s.suggest.HasExited) s.suggest.Kill(); s.suggest.Dispose(); } catch (Exception) { } }
            TasStopTechs(s);
            StopPlayback(reason);
            if (_playbackSavedAbilities.Count > 0) RestorePlaybackAbilities();
            Logger.LogInfo("TAS session closed: " + s.route.name + " (" + reason + ")");
        }

        void TasUpdate()
        {
            if (_tas == null) return;
            TasHotkeys();
            TasPollSuggest();
            TasPollTechs();
        }

        void TasHotkeys()
        {
            TasSession s = _tas;
            if (s == null) return;
            bool focused = GetActiveWindow() != IntPtr.Zero;
            for (int i = 0; i < TasKeys.Length; i++)
            {
                bool down = focused && (GetAsyncKeyState(TasKeys[i]) & 0x8000) != 0;
                bool pressed = down && !_tasKeyDown[i];
                _tasKeyDown[i] = down;
                if (!pressed || _tas == null) continue;
                try
                {
                    if (i == 0) TasAdvance(s, 1);
                    else if (i == 1) TasRewind(s, 1);
                    else if (i == 2) { if (s.playing) TasHold(s); else if (s.held) TasPlay(s, s.slowdown); }
                    else if (i == 3) { if (s.recording) s.recordStop = "Stopped (hotkey)"; else if (s.held) TasStartRecord(s, 4f); }
                    else TasClose("Emergency stop");
                }
                catch (InvalidOperationException ex) { Logger.LogWarning("TAS: " + ex.Message); }
            }
        }

        void TasAdvance(TasSession s, int ticks)
        {
            if (s.starting) throw new InvalidOperationException("The session is still starting.");
            if (s.ended != null) throw new InvalidOperationException("The run has ended here (" + s.ended + "); rewind or edit.");
            s.release += Math.Max(1, ticks);
        }

        void TasRewind(TasSession s, int ticks)
        {
            if (s.starting) throw new InvalidOperationException("The session is still starting.");
            s.seekPhysics = Math.Max(0, s.physics - Math.Max(1, ticks));
            if (s.seekPhysics == 0) { s.seekPhysics = -1; s.seekAfterStart = -1; s.reopen = true; }
        }

        void TasPlay(TasSession s, float slowdown, int until = -1, int from = -1)
        {
            if (s.ended != null && from < 0) throw new InvalidOperationException("The run has ended here; rewind or edit.");
            s.slowdown = Mathf.Clamp(slowdown, 1f, 100f);
            s.playUntil = until >= 0 ? until : int.MaxValue;
            if (from >= 0)
            {
                TasSeekTo(s, from);
                s.playAfterSeek = true;
                return;
            }
            _walkSlowdown = s.slowdown > 1f ? s.slowdown : 0f;
            s.playing = true;
        }

        void TasSeekTo(TasSession s, int tick)
        {
            if (s.starting && (_engine.IsRunning || _pendingReplay != null)) throw new InvalidOperationException("The session is still starting.");
            tick = Mathf.Clamp(tick, 0, s.ticks.Count);
            if (tick <= s.BaseInput) { s.seekAfterStart = -1; s.reopen = true; }
            else s.seekInput = tick;
        }

        void TasSuggest(TasSession s)
        {
            if (s.suggest != null) throw new InvalidOperationException("Suggestions are already running.");
            Match category = MatchRoutePrefix(s.route.name);
            CourseDef course = CourseOfRoute(s.route.name);
            if (!category.Success || course == null || !string.IsNullOrEmpty(s.route.savestateId))
                throw new InvalidOperationException("Suggestions need a course and category session (a <course>_<CAT> route, no savestate).");
            string caps = CapsOf(category.Groups["label"].Value).Id;
            string trace = CourseTrace(course, caps) ?? throw new InvalidOperationException(
                "No start trace for " + course.Token + "_" + CapsOf(caps).Label + " yet: run Improve once in the Optimize tab.");
            if (string.IsNullOrEmpty(EngineSimExe) || !File.Exists(EngineSimExe)) throw new InvalidOperationException("The offline engine isn't built.");
            int until = Math.Min((int)_engine.RunTicks, s.ticks.Count);
            if (until < 2) throw new InvalidOperationException("Play a few ticks first: suggestions improve the route up to where you are.");
            Directory.CreateDirectory(EngineSimDir);
            string file = Path.Combine(EngineSimDir, "tas-suggest-" + SafeId(s.route.id) + ".json");
            File.WriteAllText(file, JsonConvert.SerializeObject(new { name = s.route.name, ticks = s.ticks }));
            s.suggestFile = Path.ChangeExtension(file, null) + "-out.json";
            if (File.Exists(s.suggestFile)) File.Delete(s.suggestFile);
            s.suggestUntil = until;
            s.suggestError = null;
            s.suggestions = null;
            s.suggestStale = false;
            // Read output as it comes: a full pipe would block the process.
            s.suggestOutput = new System.Collections.Concurrent.ConcurrentQueue<string>();
            s.suggest = StartEngineSim("suggest " + Quote(trace) + " " + Quote(file) + " --until " + until + " --out " + Quote(s.suggestFile), s.suggestOutput);
        }

        void TasPollSuggest()
        {
            TasSession s = _tas;
            if (s == null || s.suggest == null || !s.suggest.HasExited) return;
            Process p = s.suggest;
            s.suggest = null;
            p.Dispose();
            string[] lines = s.suggestOutput?.ToArray() ?? new string[0];
            string output = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 6))).Trim();
            try
            {
                if (s.suggestStale) throw new InvalidOperationException("The inputs before input " + s.suggestUntil + " changed while it searched: run it again.");
                if (!File.Exists(s.suggestFile)) throw new InvalidOperationException(output.Length > 0 ? output : "enginesim suggest wrote no result");
                s.suggestions = JObject.Parse(File.ReadAllText(s.suggestFile));
            }
            catch (Exception ex) { s.suggestError = ex.Message; }
        }

        static readonly JsonSerializer TasTickJson = JsonSerializer.Create(new JsonSerializerSettings {
            DefaultValueHandling = DefaultValueHandling.Ignore, NullValueHandling = NullValueHandling.Ignore });

        object HandleTas(WebStudioRequest request, JObject body)
        {
            string path = request.Path.Substring("/api/tas/".Length);
            if (path == "open")
            {
                MacroDefinition route = _library.macros.Find(r => r.id == Text(body, "routeId")) ?? throw new InvalidOperationException("Route not found.");
                Match record = MatchGatedRoute(route.name);
                if (record.Success)
                {
                    route = new MacroDefinition { name = TasUniqueName(record.Groups["category"].Value + " edit " + record.Groups["time"].Value),
                        savestateId = route.savestateId, steps = route.steps.Select(st => st.Copy()).ToList() };
                    _library.macros.Add(route);
                    SaveLibrary();
                }
                TasOpen(route);
                return new { ok = true, routeId = route.id };
            }
            if (path == "new")
            {
                string name = Text(body, "name").Trim();
                var route = new MacroDefinition();
                int courseId = Number(body, "course", 0);
                string savestate = Text(body, "savestateId");
                if (courseId > 0)
                {
                    CourseDef course = _courses.Find(c => c.id == courseId) ?? throw new InvalidOperationException("Unknown course " + courseId + ".");
                    string caps = Text(body, "capabilities");
                    if (!Categories.IsCategory(caps)) throw new InvalidOperationException("Unknown category " + caps + ".");
                    CheckCategory(courseId, Caps.Parse(caps).Id);
                    route.name = TasUniqueName(course.Token + "_" + Caps.Parse(caps).Label + " " + (name.Length > 0 ? name : "manual"));
                }
                else if (savestate.Length > 0)
                {
                    if (_savestates.Find(c => c.id == savestate) == null) throw new InvalidOperationException("Savestate not found.");
                    route.name = TasUniqueName(name.Length > 0 ? name : "tas " + _savestates.Find(c => c.id == savestate).name);
                    route.savestateId = savestate;
                }
                else throw new InvalidOperationException("Pick a course and category, or a savestate.");
                RejectCategoryName(route.name);
                route.steps = ToSteps(new List<SolverTick> { new SolverTick() });
                _library.macros.Add(route);
                SaveLibrary();
                TasOpen(route);
                return new { ok = true, routeId = route.id };
            }
            if (path == "from-best")
            {
                int courseId = Number(body, "course", 0);
                CourseDef course = _courses.Find(c => c.id == courseId) ?? throw new InvalidOperationException("Unknown course " + courseId + ".");
                string caps = Text(body, "capabilities");
                if (!Categories.IsCategory(caps)) throw new InvalidOperationException("Unknown category " + caps + ".");
                bool human = Truthy(body, "human");
                var newest = OfflineBestFiles(courseId, Caps.Parse(caps).Id, human ? HumanBestFile : OfflineBestFile).FirstOrDefault();
                if (newest.path == null) throw new InvalidOperationException("No offline best" + (human ? " with a human version" : "") + " for "
                    + course.Token + "_" + Caps.Parse(caps).Label + ".");
                List<SolverTick> ticks = JObject.Parse(File.ReadAllText(newest.path))["ticks"].ToObject<List<SolverTick>>();
                var route = new MacroDefinition { name = TasUniqueName(course.Token + "_" + Caps.Parse(caps).Label + (human ? " human " : " solver ")
                    + newest.match.Groups[3].Value), steps = ToSteps(ticks) };
                _library.macros.Add(route);
                SaveLibrary();
                TasOpen(route);
                return new { ok = true, routeId = route.id };
            }
            if (path.StartsWith("share/", StringComparison.Ordinal)) return HandleShare(path.Substring(6), body);

            TasSession s = _tas ?? throw new InvalidOperationException("No TAS session is open.");
            switch (path)
            {
                case "close": TasClose("Closed from the studio"); break;
                case "advance": TasAdvance(s, Number(body, "ticks", 1)); break;
                case "rewind": TasRewind(s, Number(body, "ticks", 1)); break;
                case "seek": TasSeekTo(s, Number(body, "tick", 0)); break;
                case "play": TasPlay(s, FloatNumber(body, "slowdown", 1f), Number(body, "until", -1), Number(body, "from", -1)); break;
                case "hold":
                    s.playing = s.playAfterSeek = false;
                    s.playUntil = int.MaxValue;
                    s.release = 0;
                    s.holdAtInput = s.holdAtPhysics = -1;
                    _walkSlowdown = 0f;
                    break;
                case "edit":
                {
                    if (s.recording) throw new InvalidOperationException("Stop re-recording first.");
                    int from = Mathf.Clamp(Number(body, "from", 0), 0, s.ticks.Count);
                    int delete = Mathf.Clamp(Number(body, "deleteCount", 0), 0, s.ticks.Count - from);
                    List<SolverTick> insert = body["ticks"] == null ? new List<SolverTick>() : body["ticks"].ToObject<List<SolverTick>>();
                    if (s.ticks.Count - delete + insert.Count > TasMarkup.MaxTicks) throw new InvalidOperationException("A route holds at most " + TasMarkup.MaxTicks + " input ticks.");
                    var ticks = CloneTicks(s.ticks);
                    ticks.RemoveRange(from, delete);
                    ticks.InsertRange(from, insert.Select(TasSanitize));
                    if (TasSetTicks(s, ticks) && insert.Count != delete && s.route.markers != null)
                        foreach (RouteMarker m in s.route.markers)
                            if (m.tick >= from + delete) m.tick += insert.Count - delete;
                            else if (m.tick > from) m.tick = from;
                    break;
                }
                case "undo":
                case "redo":
                {
                    if (s.recording) throw new InvalidOperationException("Stop re-recording first.");
                    List<List<SolverTick>> from = path == "undo" ? s.undo : s.redo, to = path == "undo" ? s.redo : s.undo;
                    if (from.Count == 0) throw new InvalidOperationException("Nothing to " + path + ".");
                    to.Add(CloneTicks(s.ticks));
                    List<SolverTick> ticks = from[from.Count - 1];
                    from.RemoveAt(from.Count - 1);
                    TasSetTicks(s, ticks, false);
                    break;
                }
                case "record":
                    if (s.recording) throw new InvalidOperationException("Already re-recording.");
                    TasStartRecord(s, FloatNumber(body, "slowdown", 4f));
                    break;
                case "record/stop":
                    if (!s.recording) throw new InvalidOperationException("Not re-recording.");
                    s.recordStop = "Stopped from the studio";
                    break;
                case "marker":
                {
                    int tick = Number(body, "tick", -1);
                    string note = Text(body, "note").Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (s.route.markers == null) s.route.markers = new List<RouteMarker>();
                    s.route.markers.RemoveAll(m => m.tick == tick);
                    if (note.Length > 0) s.route.markers.Add(new RouteMarker { tick = tick, note = note });
                    s.route.markers.Sort((a, b) => a.tick.CompareTo(b.tick));
                    s.version++;
                    TasJournal(s);
                    break;
                }
                case "ticks":
                    return new { ok = true, version = s.version, routeId = s.route.id,
                        ticks = JArray.FromObject(s.ticks, TasTickJson), markers = s.route.markers ?? new List<RouteMarker>() };
                case "trace":
                {
                    int from = Mathf.Clamp(Number(body, "from", 0), 0, s.trace.Count);
                    return new { ok = true, traceVersion = s.traceVersion, from, trace = s.trace.GetRange(from, s.trace.Count - from) };
                }
                case "text":
                {
                    if (body["text"] == null) return new { ok = true, text = TasMarkup.Format(s.route.name, ToSteps(s.ticks), s.route.markers) };
                    if (s.recording) throw new InvalidOperationException("Stop re-recording first.");
                    List<SolverTick> ticks = ToTicks(TasMarkup.Parse(Text(body, "text"), out List<RouteMarker> markers)).Select(TasSanitize).ToList();
                    s.route.markers = markers;
                    s.version++;
                    TasSetTicks(s, ticks);
                    TasJournal(s);
                    break;
                }
                case "suggest": TasSuggest(s); break;
                case "techs/hover": TasTechsHover(s, body); break;
                case "techs/apply": TasTechsApply(s, body); break;
                case "suggest/apply":
                {
                    JArray found = s.suggestions?["suggestions"] as JArray;
                    int index = Number(body, "index", -1);
                    if (found == null || index < 0 || index >= found.Count) throw new InvalidOperationException("Suggestion not found.");
                    int arrival = found[index]["arrival"]?.Value<int>() ?? s.suggestUntil;
                    List<SolverTick> ticks = found[index]["ticks"].ToObject<List<SolverTick>>().Take(arrival).Select(TasSanitize).ToList();
                    if (s.ticks.Count > s.suggestUntil) ticks.AddRange(CloneTicks(s.ticks.GetRange(s.suggestUntil, s.ticks.Count - s.suggestUntil)));
                    TasSetTicks(s, ticks);
                    s.seekInput = Mathf.Clamp(arrival, s.BaseInput + 1, s.ticks.Count);
                    s.suggestions = null;
                    break;
                }
                default: throw new InvalidOperationException("Unknown TAS command: " + path);
            }
            return new { ok = true };
        }

        string TasUniqueName(string name)
        {
            string unique = name;
            for (int n = 2; _library.macros.Exists(m => string.Equals(m.name, unique, StringComparison.OrdinalIgnoreCase)); n++) unique = name + " " + n;
            return unique;
        }

        object BuildTasLiveState(Movement player)
        {
            TasSession s = _tas;
            if (s == null) return null;
            Match category = MatchRoutePrefix(s.route.name);
            CourseDef course = category.Success && string.IsNullOrEmpty(s.route.savestateId) ? CourseOfRoute(s.route.name) : null;
            object state = null;
            if (player != null)
            {
                Vector2 origin = CurrentOrigin();
                Vector2 at = (Vector2)player.transform.position - origin;
                Rigidbody2D body = player.GetComponent<Rigidbody2D>();
                float? courseTime = null;
                foreach (courseScript c in FindObjectsByType<courseScript>(FindObjectsSortMode.None))
                    if (CourseTracking(c)) courseTime = CourseCurrentPathTime(c);
                state = new {
                    x = at.x, y = at.y, vx = body != null ? body.linearVelocity.x : 0f, vy = body != null ? body.linearVelocity.y : 0f,
                    velX = player.Velocity.x, velY = player.Velocity.y,
                    momentumX = player.momentum.x, momentumY = player.momentum.y, onGround = player.onGround, onWall = player.OnWall,
                    dashActive = player.dashActive, dead = GetMovementBool(player, "isDead", false),
                    airJumpsLeft = GetMovementField(player, "airJumpsLeft"), airDashesLeft = GetMovementField(player, "airDashesLeft"),
                    jumpBuffer = GetMovementBool(player, "jumpBuffer", false), dashBuffer = GetMovementBool(player, "dashBuffer", false),
                    coyote = GetMovementField(player, "coyoteFrames"), cutscene = player.cutsceneMode.ToString(),
                    originX = origin.x, originY = origin.y, courseTime };
            }
            return new {
                open = true, routeId = s.route.id, name = s.route.name, savestateId = s.route.savestateId,
                course = course?.id, capabilities = course != null ? CapsOf(category.Groups["label"].Value).Id : null,
                phase = s.starting ? "starting" : s.recording ? "recording" : s.playing ? "playing" : s.held || s.redrawAt >= 0 ? "held" : "running",
                cursor = s.recording && _recorder.Current != null ? s.recordFrom + _recorder.Current.samples.Count : TasHeldAt(s), physics = s.physics, tickCount = s.ticks.Count, baseInput = s.BaseInput,
                rerecords = s.route.rerecords, version = s.version, canUndo = s.undo.Count > 0, canRedo = s.redo.Count > 0,
                slowdown = s.slowdown, greenzone = s.greenzone.Select(g => g.input).ToArray(), traced = s.trace.Count, traceVersion = s.traceVersion,
                playUntil = s.playing && s.playUntil != int.MaxValue ? s.playUntil : (int?)null,
                ended = s.ended, finished = s.finished, endTime = s.endTime, endPhysics = s.endPhysics, player = state,
                suggest = new { running = s.suggest != null, error = s.suggestError, until = s.suggestUntil,
                    results = s.suggestions?["suggestions"] is JArray list ? list.Select((r, i) => new { index = i,
                        description = (string)r["description"], kind = (string)r["kind"], saved = r["saved"]?.Value<int>() ?? 0,
                        from = r["from"]?.Value<int>() ?? 0, arrival = r["arrival"]?.Value<int>() ?? 0, speed = r["speed"]?.Value<float>() ?? 0f }).ToArray() : null,
                    tried = s.suggestions?["tried"]?.Value<int>() },
                techs = TasTechsLive(s)
            };
        }
    }
}
