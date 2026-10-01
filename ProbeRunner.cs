using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    /// <summary>
    /// 1x probes: play exact inputs at normal speed from a category's quick restart or a studio savestate, and log the player
    /// on every physics tick and every rendered frame (Movement.Update runs per frame, also while a dash's TimeStop holds
    /// physics). Turbo and enginesim model those frames (MacroEngine.FrameGrid); this is the ground truth they are checked on.
    /// </summary>
    public sealed partial class TasPlugin
    {
        const string ProbeRouteId = "probe";

        sealed class ProbeRun
        {
            public string name, route, savestateId, file;
            public long captureAt = -1;
            public string capturedSavestateId;
            /// <summary>The playback's frame rate (<see cref="PacePlayback"/>; unset: MacroEngine.PlaybackFrameRate, the rate turbo and enginesim model).</summary>
            public int fps;
            /// <summary>Play with the omnidirectional dash on top of the route's unlocks (Vman techs), as a tech demo can.</summary>
            public bool omniDash;
            /// <summary>A tech demo's probe (<c>tech</c>): its start, inputs and lab map (a lab demo's start is placed as its playback's is).</summary>
            public TechDef tech;
            /// <summary>
            /// The pause menu (<see cref="OpenPlaybackPause"/>): opened <c>pauseOffset</c> Movement.Update frames after physics
            /// tick <c>pauseAt</c> (the log's <c>physics</c>: a dash's locked ticks share one input tick; 0: right after the tick,
            /// before its first frame) and closed after <c>pauseFrames</c> frames with it open. A frame-exact test; a route's
            /// own pause is MacroStep.pauseFrames.
            /// </summary>
            public long pauseAt = -1;
            public int pauseOffset, pauseFrames;
            public bool pauseTaken;
            public bool started;
            public List<SolverTick> ticks;
            /// <summary>Extra Movement fields logged on every row (as enginesim's <c>probe</c> <c>fields</c>).</summary>
            public FieldInfo[] fields;
            public readonly List<object> rows = new List<object>();
        }

        ProbeRun _probe;
        string _lastProbeFile;
        static readonly FieldInfo ProbeVelocity = AccessTools.Field(typeof(Movement), "Velocity"), ProbeMomentum = AccessTools.Field(typeof(Movement), "momentum"),
            ProbeCutscene = AccessTools.Field(typeof(Movement), "cutsceneMode"), ProbeDashActive = AccessTools.Field(typeof(Movement), "dashActive"),
            ProbeSinceDash = AccessTools.Field(typeof(Movement), "framesSinceLastDash"), ProbeFacing = AccessTools.Field(typeof(Movement), "facingRight"),
            ProbeDashDirection = AccessTools.Field(typeof(Movement), "dashDirection");

        string ProbesDir { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "probes"); } }

        object HandleProbe(string path, JObject body)
        {
            switch (path)
            {
                case "/api/probe/run":
                {
                    if (PlaybackActive || _recorder.IsRecording || _solverRunning || _probe != null)
                        throw new InvalidOperationException("Stop playback, recording, the solver or the running probe first.");
                    string name = Text(body, "name").Trim();
                    if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("A probe needs a file-safe name.");
                    // tech: the tech's lab demo (lab: which of its labs, else its first), its start and inputs, unless given.
                    List<SolverTick> demo = null;
                    TechDef tech = body["tech"] == null ? null : TechLabDemo(FindTech(Text(body, "tech")), body["lab"] == null ? null : Text(body, "lab"), out demo);
                    string route = Text(body, "route").Trim();
                    if (route.Length == 0 && tech != null) route = tech.start;
                    if (route.Length == 0 || MatchTechStart(route).Length != route.Length)
                        throw new InvalidOperationException("route is a category prefix (1_WJ_DS): it picks the course, zone and unlocks.");
                    string savestateId = Text(body, "savestateId");
                    if (savestateId.Length > 0 && _savestates.Find(s => s.id == savestateId) == null) throw new InvalidOperationException("Savestate not found.");
                    List<SolverTick> ticks = body["ticks"] is JArray list ? list.ToObject<List<SolverTick>>() : demo;
                    FieldInfo[] fields = body["fields"] is JArray names ? names.Select(n => AccessTools.Field(typeof(Movement), (string)n)
                        ?? throw new InvalidOperationException("Movement has no field " + n + ".")).ToArray() : new FieldInfo[0];
                    _probe = new ProbeRun { name = name, route = route, savestateId = savestateId, ticks = ticks, fields = fields, tech = tech,
                        captureAt = body["captureAt"] == null ? -1 : (long)body["captureAt"],
                        pauseAt = body["pauseAt"] == null ? -1 : (long)body["pauseAt"], pauseOffset = Number(body, "pauseOffset", 0),
                        pauseFrames = Number(body, "pauseFrames", 0),
                        fps = Number(body, "fps", 0), omniDash = Truthy(body, "omniDash") };
                    var macro = new MacroDefinition { id = ProbeRouteId, name = route + " probe " + name, savestateId = savestateId, steps = ToSteps(ticks) };
                    Match fullTas = FullTasRouteName.Match(macro.name);
                    if (fullTas.Success && savestateId.Length == 0)
                    {
                        // A Full-TAS run's probe starts from that run's new game (its unlocks come from what the ticks buy).
                        StartFullTasPlayback(FindFullTasRun(fullTas.Groups[1].Value), macro);
                        return new { ok = true };
                    }
                    EnsureGameUnpaused();
                    _pendingReplay = macro;
                    _pendingReplayStartDelayTicks = 0;
                    return new { ok = true };
                }
                case "/api/probe/status":
                    return new { ok = true, running = _probe != null, name = _probe?.name, lastFile = _lastProbeFile };
            }
            throw new InvalidOperationException("Unknown probe command: " + path);
        }

        bool ProbeActive(Movement movement)
        {
            return _probe != null && movement != null && !IsMenuPlayer(movement) && _engine.IsRunning && _engine.ActiveMacro != null
                && _engine.ActiveMacro.id == ProbeRouteId;
        }

        void ProbeRow(Movement movement, string kind)
        {
            _probe.started = true;
            Vector2 origin = CurrentOrigin();
            Vector3 p = movement.transform.position;
            Vector2 v = (Vector2)ProbeVelocity.GetValue(movement), m = (Vector2)ProbeMomentum.GetValue(movement), d = (Vector2)ProbeDashDirection.GetValue(movement);
            Vector2 body = _playerBody != null ? _playerBody.linearVelocity : Vector2.zero;
            Vector2 bodyAt = _playerBody != null ? _playerBody.position - origin : Vector2.zero;
            var fields = new Dictionary<string, object>();
            foreach (FieldInfo f in _probe.fields)
            {
                object value = f.GetValue(movement);
                fields[f.Name] = value is UnityEngine.Object o ? (o == null ? null : o.name) : value is Vector2 v2 ? new[] { v2.x, v2.y } : value;
            }
            // Zip movers within 300 u and their layer: MakeIntangible takes one off Ground (7) for 0.15 s after a jump off it.
            var movers = new List<object>();
            foreach (PlatformMover mover in FindObjectsByType<PlatformMover>(FindObjectsSortMode.None))
            {
                Vector3 q = mover.transform.position;
                if (Mathf.Abs(q.x - p.x) < 300f && Mathf.Abs(q.y - p.y) < 300f)
                    movers.Add(new { x = q.x - origin.x, y = q.y - origin.y, layer = mover.gameObject.layer });
            }
            Camera cam = Camera.main;
            Vector3 camAt = cam != null ? cam.transform.position : Vector3.zero;
            _probe.rows.Add(new
            {
                kind, frame = Time.frameCount, unscaled = Time.unscaledTime, timeScale = Time.timeScale, time = Time.timeAsDouble,
                fixedTime = Time.fixedTimeAsDouble, dt = Time.deltaTime, clock = _clockFrame,
                paused = movement.pauseMenu != null && movement.pauseMenu.menuOpen,
                camX = camAt.x - origin.x, camY = camAt.y - origin.y, camSize = cam != null ? cam.orthographicSize : 0f,
                cash = globalStats.currencyLookup[globalStats.Currencies.Cash],
                random = CourseStart.Words(UnityEngine.Random.state),
                physics = _engine.PhysicsTicks, input = _engine.RunTicks,
                x = p.x - origin.x, y = p.y - origin.y, vx = v.x, vy = v.y, mx = m.x, my = m.y, bvx = body.x, bvy = body.y,
                bodyX = bodyAt.x, bodyY = bodyAt.y, interpolation = _playerBody != null ? _playerBody.interpolation.ToString() : null,
                ground = movement.onGround, wall = movement.OnWall, mode = ProbeCutscene.GetValue(movement).ToString(),
                dash = ProbeDashActive.GetValue(movement), f = ProbeSinceDash.GetValue(movement), facingRight = ProbeFacing.GetValue(movement),
                dashDir = new[] { d.x, d.y }, originX = origin.x, originY = origin.y,
                lastHyperY = TechLastHyperYField == null ? (float?)null : (float)TechLastHyperYField.GetValue(movement),
                fields, movers,
            });
        }

        // A probe's clone log (rows of kind "clone"): every clonesScript.SpawnClone with the clonesScripts whose Update ran
        // before it in the same frame. Turbo and enginesim step the courses' clones lowest instance id first; a spawn's rolls
        // come off the one RNG, so a frame where two courses spawn in another order hands them each other's rolls.
        static int _cloneOrderFrame = -1;
        static readonly List<int> _cloneOrder = new List<int>();
        static readonly FieldInfo CloneCourse = AccessTools.Field(typeof(clonesScript), "course"), CloneOnScreen = AccessTools.Field(typeof(clonesScript), "onScreen"),
            CloneDivisor = AccessTools.Field(typeof(clonesScript), "nextOffscreenCloneDivisor"), CloneNextDue = AccessTools.Field(typeof(clonesScript), "nextCloneDue");
        static double _spawnCash;
        static int[] _spawnRandom;
        static float _spawnDivisor;

        bool ProbeLogsClones { get { return _probe != null && _probe.started && _engine.IsRunning && _engine.ActiveMacro != null && _engine.ActiveMacro.id == ProbeRouteId; } }

        private static void BeforeProbeCloneUpdate(clonesScript __instance)
        {
            if (Instance == null || !Instance.ProbeLogsClones) return;
            if (Time.frameCount != _cloneOrderFrame) { _cloneOrderFrame = Time.frameCount; _cloneOrder.Clear(); }
            _cloneOrder.Add(__instance.GetInstanceID());
        }

        private static void BeforeProbeSpawn(clonesScript __instance)
        {
            if (Instance == null || !Instance.ProbeLogsClones) return;
            _spawnCash = globalStats.currencyLookup[globalStats.Currencies.Cash];
            _spawnRandom = CourseStart.Words(UnityEngine.Random.state);
            _spawnDivisor = (float)CloneDivisor.GetValue(__instance);
        }

        private static void AfterProbeSpawn(clonesScript __instance)
        {
            if (Instance == null || !Instance.ProbeLogsClones) return;
            var course = CloneCourse.GetValue(__instance) as courseScript;
            Instance._probe.rows.Add(new
            {
                kind = "clone", frame = Time.frameCount, physics = Instance._engine.PhysicsTicks, input = Instance._engine.RunTicks,
                course = course != null ? course.courseNumber : -1, id = __instance.GetInstanceID(), onScreen = CloneOnScreen.GetValue(__instance),
                divisor = _spawnDivisor, nextDue = CloneNextDue.GetValue(__instance), cashBefore = _spawnCash, cash = globalStats.currencyLookup[globalStats.Currencies.Cash],
                random = _spawnRandom, order = Time.frameCount == _cloneOrderFrame ? _cloneOrder.ToArray() : new int[0],
            });
        }

        // A probe's RNG log (rows of kind "rng"): each call of a game method that draws UnityEngine.Random (the ones the decompiled
        // game has: audio picks, lights' and animators' Start, bobbers' and boxes' OnEnable, a zip mover's state, a course's Start,
        // Movement.playSFX) with the draws it made, so a run's draws line up with turbo's and enginesim's by frame and object.
        static readonly (Type type, string method)[] ProbeRngCallers =
        {
            (typeof(Audio), "PlayAudio"), (typeof(Audio), "PlayAudioAtPoint"), (typeof(Audio), "PlayAudioSetPitch"),
            (typeof(JiggleDropScript), "Start"), (typeof(LightFlicker), "Start"), (typeof(playerLightFlickerVariant), "Start"),
            (typeof(RandomAnimSpeedComponent), "Start"), (typeof(ObjectBobber), "OnEnable"), (typeof(courseScript), "Start"),
            (typeof(upgradeBox), "OnEnable"), (typeof(Movement), "playSFX"),
        };
        void PatchProbeRng()
        {
            foreach (var (type, method) in ProbeRngCallers) PatchOptional(type, method, nameof(BeforeProbeRng), nameof(AfterProbeRng));
        }

        private static void BeforeProbeRng(out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
        }

        private static void AfterProbeRng(object __instance, MethodBase __originalMethod, UnityEngine.Random.State __state)
        {
            if (Instance == null || !Instance.ProbeLogsClones) return;
            UnityEngine.Random.State after = UnityEngine.Random.state;
            if (__state.Equals(after)) return;
            Instance._probe.rows.Add(new
            {
                kind = "rng", frame = Time.frameCount, physics = Instance._engine.PhysicsTicks, input = Instance._engine.RunTicks,
                method = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name, obj = (__instance as Component)?.gameObject.name,
                random = CourseStart.Words(after),
            });
        }

        void ProbeAfterTick(Movement movement)
        {
            if (!ProbeActive(movement)) return;
            ProbeRow(movement, "tick");
            if (_probe.pauseAt >= 0 && !_probe.pauseTaken && _engine.PhysicsTicks == _probe.pauseAt)
            {
                _probe.pauseTaken = true;
                OpenPlaybackPause(movement, _probe.pauseFrames, _probe.pauseOffset);
            }
            if (_probe.captureAt >= 0 && _probe.capturedSavestateId == null && _engine.RunTicks >= _probe.captureAt)
            {
                Savestate savestate = SavestateService.Capture(movement, "probe/" + _probe.name + " @" + _engine.RunTicks);
                _savestates.Insert(0, savestate);
                WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(savestate.id) + ".json"), savestate, false);
                _probe.capturedSavestateId = savestate.id;
            }
        }

        int _pauseLeft, _pausedFrames, _pauseDueFrames, _pauseDueIn;

        /// <summary>
        /// 1x playback's pause (a route step's MacroStep.pauseFrames, or a probe's pauseAt): the Pause button's
        /// pauseMenuScript.menuButtonPressed, at once or after <paramref name="afterFrames"/> Movement.Update frames
        /// (MacroStep.pauseFrame, a probe's pauseOffset), closed the same way after <paramref name="frames"/> frames with it
        /// open (<see cref="AfterMovementUpdate"/>). Paused, timeScale is 0 and no FixedUpdate runs, but Movement.Update's
        /// step-up (806-816) still runs every frame. Turbo models a route's pause instead (TurboRunner.Pause).
        /// </summary>
        void OpenPlaybackPause(Movement movement, int frames, int afterFrames)
        {
            if (frames <= 0 || TurboRunner.Active) return;
            if (afterFrames > 0)
            {
                _pauseDueFrames = frames;
                _pauseDueIn = afterFrames;
                return;
            }
            if (movement.pauseMenu == null || movement.pauseMenu.menuOpen) return;
            movement.pauseMenu.menuButtonPressed();
            if (!movement.pauseMenu.menuOpen) return;
            _pauseLeft = frames;
            _pausedFrames = 0;
            Logger.LogInfo("Pause menu opened for " + frames + " frames at physics tick " + _engine.PhysicsTicks);
        }

        /// <summary>
        /// A lab savestate for probes (/api/savestates/place): a copy of a savestate (<c>from</c>) or of the player now, with
        /// any of <c>x</c>, <c>y</c> (world frame), <c>vx</c>, <c>vy</c> (Velocity; the body gets Velocity + momentum),
        /// <c>facing</c> (±1: scale and facingRight) and Movement <c>fields</c> ({name: value}) replaced, so a probe can start
        /// anywhere, inside a wall too. Named "lab/…": a start for experiments, never a route's.
        /// </summary>
        Savestate PlaceSavestate(JObject body)
        {
            Savestate placed = PlacedSavestate(body);
            _savestates.Insert(0, placed);
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(placed.id) + ".json"), placed, false);
            return placed;
        }

        /// <summary>The placed savestate of <see cref="PlaceSavestate"/>, not stored (a lab map's start played by hand).</summary>
        Savestate PlacedSavestate(JObject body)
        {
            Movement movement = FindWorldMovement() ?? throw new InvalidOperationException("No player in the world.");
            string fromId = Text(body, "from");
            Savestate source = fromId.Length == 0 ? SavestateService.Capture(movement, "")
                : _savestates.Find(s => s.id == fromId) ?? throw new InvalidOperationException("Savestate not found.");
            Savestate placed = JsonConvert.DeserializeObject<Savestate>(JsonConvert.SerializeObject(source));
            placed.id = Guid.NewGuid().ToString("N");
            string name = Text(body, "name").Trim();
            placed.name = "lab/" + (name.Length > 0 ? name : "placed");
            placed.createdUtc = DateTime.UtcNow.ToString("o");
            if (!placed.hasOrigin)
            {
                Vector2 now = CurrentOrigin();
                placed.hasOrigin = true;
                placed.originX = now.x;
                placed.originY = now.y;
            }
            if (body["x"] != null) placed.positionX = (float)body["x"] + placed.originX;
            if (body["y"] != null) placed.positionY = (float)body["y"] + placed.originY;
            if (body["rebaseAtX"] != null && body["rebaseAtY"] != null)
            {
                placed.hasRebaseAt = true;
                placed.rebaseAtX = (float)body["rebaseAtX"];
                placed.rebaseAtY = (float)body["rebaseAtY"];
            }
            if (body["facing"] != null)
            {
                bool right = (float)body["facing"] > 0f;
                placed.scaleX = right ? 1f : -1f;
                placed.movementFields["facingRight"] = new SnapshotValue { type = "Boolean", value = right ? "true" : "false" };
            }
            if (body["fields"] is JObject fields)
                foreach (JProperty field in fields.Properties())
                    placed.movementFields[field.Name] = new SnapshotValue { type = "String", value = field.Value.Type == JTokenType.Boolean
                        ? ((bool)field.Value ? "true" : "false") : field.Value.Type == JTokenType.Array
                        ? string.Join(",", field.Value.Select(v => ((float)v).ToString("R", CultureInfo.InvariantCulture)))
                        : Convert.ToString(((JValue)field.Value).Value, CultureInfo.InvariantCulture) };
            if (body["vx"] != null || body["vy"] != null)
            {
                Vector2 v = ReadSnapshotVector(placed.movementFields, "Velocity"), m = ReadSnapshotVector(placed.movementFields, "momentum");
                if (body["vx"] != null) v.x = (float)body["vx"];
                if (body["vy"] != null) v.y = (float)body["vy"];
                placed.movementFields["Velocity"] = SnapshotVector(v);
                placed.bodyFields["linearVelocity"] = SnapshotVector(v + m);
                placed.bodyFields.Remove("velocity");
            }
            return placed;
        }

        static Vector2 ReadSnapshotVector(Dictionary<string, SnapshotValue> values, string key)
        {
            if (!values.TryGetValue(key, out SnapshotValue s) || s?.value == null) return Vector2.zero;
            string[] p = s.value.Split(',');
            return new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
        }

        static SnapshotValue SnapshotVector(Vector2 v)
        {
            return new SnapshotValue { type = "Vector2", value = v.x.ToString("R", CultureInfo.InvariantCulture) + ","
                + v.y.ToString("R", CultureInfo.InvariantCulture) };
        }

        private static void AfterMovementUpdate(Movement __instance)
        {
            if (Instance == null) return;
            if (Instance.ProbeActive(__instance)) Instance.ProbeRow(__instance, "frame");
            if (Instance._pauseLeft > 0 && (__instance.pauseMenu == null || !__instance.pauseMenu.menuOpen)) Instance._pauseLeft = 0;
            if (Instance._pauseLeft > 0 && ++Instance._pausedFrames >= Instance._pauseLeft)
            {
                Instance._pauseLeft = 0;
                if (__instance.pauseMenu != null && __instance.pauseMenu.menuOpen) __instance.pauseMenu.menuButtonPressed();
                Instance.Logger.LogInfo("Pause menu closed after " + Instance._pausedFrames + " frames");
            }
            if (Instance._pauseDueIn > 0 && --Instance._pauseDueIn == 0)
                Instance.OpenPlaybackPause(__instance, Instance._pauseDueFrames, 0);
        }

        /// <summary>Writes the probe's log once its playback is over (plugin Update).</summary>
        void PumpProbe()
        {
            if (_probe == null || _engine.IsRunning || _pendingReplay != null || _fullTasPlay != null) return;
            if (!_probe.started)
            {
                Logger.LogWarning("Probe " + _probe.name + " did not start: " + _engine.Status);
                _probe = null;
                return;
            }
            Directory.CreateDirectory(ProbesDir);
            string file = Path.Combine(ProbesDir, _probe.name + ".json");
            WriteJsonAtomic(file, new
            {
                _probe.name, _probe.route, tech = _probe.tech?.id, _probe.savestateId, capturedSavestateId = _probe.capturedSavestateId,
                result = _engine.Status, targetFrameRate = _probe.fps > 0 ? _probe.fps : MacroEngine.PlaybackFrameRate, vSync = 0,
                ticks = _probe.ticks, rows = _probe.rows,
            }, false);
            Logger.LogInfo("Probe " + _probe.name + ": " + _probe.rows.Count + " rows -> " + file);
            _lastProbeFile = file;
            _probe = null;
        }
    }
}
