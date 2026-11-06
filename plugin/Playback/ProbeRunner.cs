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
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        const string ProbeRouteId = "probe";

        sealed class ProbeRun
        {
            public string name, route, savestateId, file;
            public long captureAt = -1;
            public long logFrom;
            public string capturedSavestateId;
            public int fps;
            public bool omniDash;
            public bool lean;
            public bool fast;
            public TechDef tech;
            public long pauseAt = -1;
            public int pauseOffset, pauseFrames;
            public bool pauseTaken;
            public bool started;
            public List<SolverTick> ticks;
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
                        captureAt = body["captureAt"] == null ? -1 : (long)body["captureAt"], logFrom = Number(body, "logFrom", 0),
                        pauseAt = body["pauseAt"] == null ? -1 : (long)body["pauseAt"], pauseOffset = Number(body, "pauseOffset", 0),
                        pauseFrames = Number(body, "pauseFrames", 0),
                        fps = Number(body, "fps", 0), omniDash = Truthy(body, "omniDash"), lean = Truthy(body, "lean"), fast = Truthy(body, "fast") };
                    _cashPre = globalStats.currencyLookup[globalStats.Currencies.Cash];
                    var macro = new MacroDefinition { id = ProbeRouteId, name = route + " probe " + name, savestateId = savestateId, steps = ToSteps(ticks) };
                    Match fullTas = FullTasRouteName.Match(macro.name);
                    if (fullTas.Success && savestateId.Length == 0)
                    {
                        StartFullTasPlayback(FindFullTasRun(fullTas.Groups[1].Value), macro, null, Number(body, "fromTick", 0));
                        return new { ok = true };
                    }
                    EnsureGameUnpaused();
                    _pendingReplay = macro;
                    _pendingReplayStartDelayTicks = 0;
                    return new { ok = true };
                }
                case "/api/probe/status":
                    return new { ok = true, running = _probe != null, name = _probe?.name, lastFile = _lastProbeFile };
                case "/api/probe/1x":
                    if (!OneXIdle()) throw new InvalidOperationException("The game is busy: stop playback, recording, the solver, the probe or the lab first.");
                    return StartOneX(body["name"] == null ? null : FindFullTasRun(Text(body, "name"))) ?? throw new InvalidOperationException("Every run's current best has its 1x log already.");
                case "/api/probe/1x/idle":
                    _settings.idle1x = Truthy(body, "on");
                    SaveSettings();
                    return new { ok = true, on = _settings.idle1x };
                case "/api/probe/1x/status":
                    return new { ok = true, idle = _settings.idle1x, running = _oneX?.run, idleFor = Time.realtimeSinceStartup - _lastCommandAt, last = _oneXLast, dir = OneXDir };
            }
            throw new InvalidOperationException("Unknown probe command: " + path);
        }

        bool ProbeActive(Movement movement)
        {
            return _probe != null && movement != null && !IsMenuPlayer(movement) && _engine.IsRunning && _engine.ActiveMacro != null
                && _engine.ActiveMacro.id == ProbeRouteId && !TurboRunner.Active && _engine.PhysicsTicks >= _probe.logFrom;
        }

        void ProbeRow(Movement movement, string kind)
        {
            _probe.started = true;
            Vector2 origin = CurrentOrigin();
            Vector3 p = movement.transform.position;
            Vector2 v = (Vector2)ProbeVelocity.GetValue(movement), m = (Vector2)ProbeMomentum.GetValue(movement), d = (Vector2)ProbeDashDirection.GetValue(movement);
            Vector2 body = _playerBody != null ? _playerBody.linearVelocity : Vector2.zero;
            Vector2 bodyAt = _playerBody != null ? _playerBody.position - origin : Vector2.zero;
            if (_probe.lean)
            {
                _probe.rows.Add(new
                {
                    kind, frame = Time.frameCount, physics = _engine.PhysicsTicks, input = _engine.RunTicks, held = _engine.HeldFrames,
                    x = p.x - origin.x, y = p.y - origin.y, bodyX = bodyAt.x, bodyY = bodyAt.y, vx = v.x, vy = v.y, mx = m.x, my = m.y,
                    originX = origin.x, originY = origin.y, cash = globalStats.currencyLookup[globalStats.Currencies.Cash], cashPre = _cashPre,
                });
                return;
            }
            var fields = new Dictionary<string, object>();
            foreach (FieldInfo f in _probe.fields)
            {
                object value = f.GetValue(movement);
                fields[f.Name] = value is UnityEngine.Object o ? (o == null ? null : o.name) : value is Vector2 v2 ? new[] { v2.x, v2.y } : value;
            }
            var movers = new List<object>();
            foreach (PlatformMover mover in FindObjectsByType<PlatformMover>(FindObjectsSortMode.None))
            {
                Vector3 q = mover.transform.position;
                if (Mathf.Abs(q.x - p.x) < 300f && Mathf.Abs(q.y - p.y) < 300f)
                    movers.Add(new { x = q.x - origin.x, y = q.y - origin.y, layer = mover.gameObject.layer });
            }
            var boxes = new List<object>();
            foreach (upgradeBox box in FindObjectsByType<upgradeBox>(FindObjectsSortMode.None))
            {
                Vector3 q = box.transform.position;
                if (Mathf.Abs(q.x - p.x) >= 300f || Mathf.Abs(q.y - p.y) >= 300f) continue;
                Collider2D solid = box.GetComponents<Collider2D>().FirstOrDefault(c => !c.isTrigger);
                boxes.Add(new { name = box.name, x = q.x - origin.x, y = q.y - origin.y, bottom = solid != null ? solid.bounds.min.y - origin.y : float.NaN });
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
                physics = _engine.PhysicsTicks, input = _engine.RunTicks, held = _engine.HeldFrames,
                x = p.x - origin.x, y = p.y - origin.y, vx = v.x, vy = v.y, mx = m.x, my = m.y, bvx = body.x, bvy = body.y,
                bodyX = bodyAt.x, bodyY = bodyAt.y, interpolation = _playerBody != null ? _playerBody.interpolation.ToString() : null,
                ground = movement.onGround, wall = movement.OnWall, mode = ProbeCutscene.GetValue(movement).ToString(),
                dash = ProbeDashActive.GetValue(movement), f = ProbeSinceDash.GetValue(movement), facingRight = ProbeFacing.GetValue(movement),
                dashDir = new[] { d.x, d.y }, originX = origin.x, originY = origin.y,
                lastHyperY = TechLastHyperYField == null ? (float?)null : (float)TechLastHyperYField.GetValue(movement),
                fields, movers, boxes, econ = EconomyNow(), econPre = kind == "tick" ? _econPre : null,
            });
        }

        static readonly AccessTools.FieldRef<AtomColliderSystem, List<List<GameObject>>> AtomContents =
            AccessTools.FieldRefAccess<AtomColliderSystem, List<List<GameObject>>>("contents");
        static readonly AccessTools.FieldRef<AtomColliderSystem, int> AtomWaiting = AccessTools.FieldRefAccess<AtomColliderSystem, int>("waitingAtoms");
        static AtomColliderSystem _econAtoms;
        static Dictionary<string, object> _econPre;
        static double _cashPre;

        private static void BeforeProbeStatsFixedUpdate()
        {
            if (Instance == null || !Instance.ProbeLogging) return;
            _cashPre = globalStats.currencyLookup[globalStats.Currencies.Cash];
            _econPre = Instance._probe.lean ? null : EconomyNow();
        }
        static int _gridCalls;
        static TreeController[] _econTrees;

        internal static Dictionary<string, object> EconomyNow()
        {
            if (_econAtoms == null) _econAtoms = FindAnyObjectByType<AtomColliderSystem>(FindObjectsInactive.Include);
            if (_econTrees == null || Array.Exists(_econTrees, t => t == null))
                _econTrees = FindObjectsByType<TreeController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            globalStats stats = Singleton<globalStats>.Instance;
            Dictionary<globalStats.Currencies, double> c = globalStats.currencyLookup;
            return new Dictionary<string, object>
            {
                ["cash"] = c[globalStats.Currencies.Cash], ["gp"] = c[globalStats.Currencies.GreenPower], ["np"] = c[globalStats.Currencies.AtomicPower],
                ["cd"] = c[globalStats.Currencies.CloneDust], ["rp"] = c[globalStats.Currencies.RedPower],
                ["atoms"] = _econAtoms == null ? new int[0] : AtomContents(_econAtoms).Select(tier => tier.Count).ToArray(),
                ["waiting"] = _econAtoms == null ? 0 : AtomWaiting(_econAtoms),
                ["npTick"] = stats == null ? 0.0 : stats.NPrewardPerTick, ["npCap"] = stats == null ? 0.0 : stats.NPrewardCap,
                ["npBoost"] = stats == null ? 1.0 : stats.EffectOfNpCompBoost, ["cdMult"] = stats == null ? 1.0 : stats.CloneDustGpCdMult,
                ["gridCalls"] = _gridCalls, ["segments"] = _econTrees.Sum(t => t.GrownSegments.Count),
                ["doorAtom"] = EconDoor(globalStats.globalUpgradeSet.spawnNewAtom), ["doorArea2"] = EconDoor(globalStats.globalUpgradeSet.area2DoorOpen),
                ["treeBoxes"] = EconTreeBoxes(),
            };
        }

        static readonly AccessTools.FieldRef<upgradeBox, TreeController> EconBoxTree = AccessTools.FieldRefAccess<upgradeBox, TreeController>("upgradeTree");
        static upgradeBox[] _econTreeBoxes;

        static string EconTreeBoxes()
        {
            if (_econTreeBoxes == null || Array.Exists(_econTreeBoxes, b => b == null))
                _econTreeBoxes = FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(b => EconBoxTree(b) != null).ToArray();
            return _econTreeBoxes.Where(b => b.gameObject.activeInHierarchy).Select(b => WokenStarts.Describe(b)).OrderBy(d => d, StringComparer.Ordinal).Aggregate("", WokenStarts.Join);
        }

        static readonly AccessTools.FieldRef<upgradeBox, PlatformMover> EconDoorToOpen = AccessTools.FieldRefAccess<upgradeBox, PlatformMover>("doorToOpen");
        static upgradeBox[] _econDoorBoxes;

        static double EconDoor(globalStats.globalUpgradeSet kind)
        {
            if (_econDoorBoxes == null || Array.Exists(_econDoorBoxes, b => b == null))
                _econDoorBoxes = FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(b => EconDoorToOpen(b) != null).ToArray();
            foreach (upgradeBox b in _econDoorBoxes)
                if (b.upgrade == localUpgrades.localUpgradeSet.GLOBAL && b.globalUpgrade == kind)
                    return Math.Round(Vector2.Distance(EconDoorToOpen(b).transform.position, b.transform.position), 2);
            return -1;
        }

        // Turbo/engine step clones lowest instance id first; two courses spawning in one frame swap RNG rolls otherwise.
        static int _cloneOrderFrame = -1;
        static readonly List<int> _cloneOrder = new List<int>();
        static readonly FieldInfo CloneCourse = AccessTools.Field(typeof(clonesScript), "course"), CloneOnScreen = AccessTools.Field(typeof(clonesScript), "onScreen"),
            CloneDivisor = AccessTools.Field(typeof(clonesScript), "nextOffscreenCloneDivisor"), CloneNextDue = AccessTools.Field(typeof(clonesScript), "nextCloneDue");
        static double _spawnCash;
        static int[] _spawnRandom;
        static float _spawnDivisor;

        bool ProbeLogging { get { return _probe != null && _probe.started && _engine.IsRunning && _engine.ActiveMacro != null && _engine.ActiveMacro.id == ProbeRouteId && !TurboRunner.Active
            && _engine.PhysicsTicks >= _probe.logFrom; } }
        bool ProbeLogsClones { get { return ProbeLogging && !_probe.lean; } }

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
            PatchOptional(typeof(zoneChanger), "OnTriggerEnter2D", nameof(BeforeProbeZoneEnter), null);
            PatchOptional(typeof(ZoneLoader), "_LoadZone", nameof(BeforeProbeZoneLoad), null);
            PatchOptional(typeof(AtomColliderSystem), "OnEnable", nameof(BeforeProbeAtomEnable), null);
            PatchOptional(typeof(AtomColliderSystem), "Start", nameof(BeforeProbeAtomStart), null);
            PatchOptional(typeof(upgradeBox), "Start", nameof(BeforeProbeBoxStart), null);
            PatchOptional(typeof(PlatformMover), "OnEnable", nameof(BeforeProbeMoverEnable), null);
        }

        static readonly AccessTools.FieldRef<zoneChanger, int> ZoneToLoad = AccessTools.FieldRefAccess<zoneChanger, int>("zoneToLoad");
        static readonly AccessTools.FieldRef<ZoneLoader, GameObject> ZoneLoadingText = AccessTools.FieldRefAccess<ZoneLoader, GameObject>("loadingText");

        private static void BeforeProbeZoneEnter(zoneChanger __instance, Collider2D collision)
        {
            ProbeZoneRow("enter", __instance, ZoneToLoad(__instance), collision == null ? null : collision.name);
        }

        private static void BeforeProbeZoneLoad(ZoneLoader __instance, int zoneToLoad) { ProbeZoneRow("load", __instance, zoneToLoad, null); }
        private static void BeforeProbeAtomEnable(AtomColliderSystem __instance) { ProbeZoneRow("atomEnable", __instance, 0, null); }
        private static void BeforeProbeAtomStart(AtomColliderSystem __instance) { ProbeZoneRow("atomStart", __instance, 0, null); }
        private static void BeforeProbeBoxStart(upgradeBox __instance) { ProbeZoneRow("boxStart", __instance, 0, WokenStarts.Unmodeled(__instance)); }
        private static void BeforeProbeMoverEnable(PlatformMover __instance) { ProbeZoneRow("moverEnable", __instance, 0, null); }

        static void ProbeZoneRow(string what, Component at, int zone, string by)
        {
            if (Instance == null || !Instance.ProbeLogsClones) return;
            ZoneLoader loader = at as ZoneLoader ?? FindAnyObjectByType<ZoneLoader>();
            GameObject loading = loader == null ? null : ZoneLoadingText(loader);
            Instance._probe.rows.Add(new
            {
                kind = "zone", what, frame = Time.frameCount, physics = Instance._engine.PhysicsTicks, input = Instance._engine.RunTicks,
                time = Time.timeAsDouble, fixedTime = Time.fixedTimeAsDouble, inFixedStep = Time.inFixedTimeStep, obj = at.gameObject.name, zone, by,
                activeZone = loader == null ? -1 : loader.activeZone, loadingText = loading != null && loading.activeSelf, random = CourseStart.Words(UnityEngine.Random.state),
            });
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

        private static void BeforeProbeAtomCall(AtomColliderSystem __instance)
        {
            _gridCalls++;
            if (Instance == null || !Instance.ProbeLogsClones) return;
            Instance._probe.rows.Add(new { kind = "atom", frame = Time.frameCount, physics = Instance._engine.PhysicsTicks, input = Instance._engine.RunTicks,
                time = Time.timeAsDouble, fixedTime = Time.fixedTimeAsDouble, waiting = AtomWaiting(__instance) });
        }

        void ProbeAfterTick(Movement movement)
        {
            if (_probe != null && _probe.captureAt >= 0 && _probe.capturedSavestateId == null && _engine.IsRunning && _engine.ActiveMacro?.id == ProbeRouteId
                && !TurboRunner.Active && _engine.RunTicks >= _probe.captureAt)
            {
                Savestate savestate = SavestateService.Capture(movement, "probe/" + _probe.name + " @" + _engine.RunTicks);
                _savestates.Insert(0, savestate);
                WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(savestate.id) + ".json"), savestate, false);
                _probe.capturedSavestateId = savestate.id;
            }
            if (!ProbeActive(movement)) return;
            ProbeRow(movement, "tick");
            if (_probe.pauseAt >= 0 && !_probe.pauseTaken && _engine.PhysicsTicks == _probe.pauseAt)
            {
                _probe.pauseTaken = true;
                OpenPlaybackPause(movement, _probe.pauseFrames, _probe.pauseOffset);
            }
        }

        int _pauseLeft, _pausedFrames, _pauseDueFrames, _pauseDueIn;

        // Paused: timeScale 0 and no FixedUpdate, but Movement.Update's step-up (806-816) still runs every frame.
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

        Savestate PlaceSavestate(JObject body)
        {
            Savestate placed = PlacedSavestate(body);
            _savestates.Insert(0, placed);
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(placed.id) + ".json"), placed, false);
            return placed;
        }

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
            if (Instance.ProbeActive(__instance) && !Instance._probe.lean) Instance.ProbeRow(__instance, "frame");
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

        void PumpProbe()
        {
            if (_probe == null || _engine.IsRunning || _pendingReplay != null || _fullTasPlay != null) return;
            if (!_probe.started)
            {
                Logger.LogWarning("Probe " + _probe.name + " did not start: " + _engine.Status);
                _probe = null;
                return;
            }
            string file = _probe.file ?? Path.Combine(ProbesDir, _probe.name + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
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

        sealed class OneXJob { public string run, file, reference; public ProbeRun probe; }
        OneXJob _oneX;
        string _oneXLast;
        float _lastCommandAt, _oneXScanAt;
        const float OneXIdleSeconds = 60f;
        string OneXDir { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "1x"); } }

        bool OneXIdle()
        {
            return !PlaybackActive && _pendingReplay == null && !_recorder.IsRecording && !_solverRunning && _probe == null && _fullTasPending == null
                && _newGamePhase == 0 && _bootstrapRequest == null && !_silentEvaluation && !TurboRunner.Active && !LabInjected
                && (_fullTasQueue == null || _fullTasQueue.index >= _fullTasQueue.files.Count);
        }

        void OneXCommand(string path)
        {
            _lastCommandAt = Time.realtimeSinceStartup;
            if (_oneX != null && !path.StartsWith("/api/probe/1x", StringComparison.Ordinal)) StopOneX("a command came (" + path + ")");
        }

        void StopOneX(string why)
        {
            if (_probe == _oneX.probe)
            {
                _probe = null;
                StopPlayback("1x job stopped");
            }
            _oneXLast = "Stopped " + _oneX.run + "'s 1x play: " + why;
            Logger.LogInfo(_oneXLast);
            _oneX = null;
        }

        static bool PlayerInput()
        {
            if (Keyboard.current != null && Keyboard.current.anyKey.isPressed) return true;
            if (Gamepad.current == null) return false;
            foreach (InputControl c in Gamepad.current.allControls)
                if (c is ButtonControl b && b.isPressed) return true;
            return false;
        }

        void PumpOneX()
        {
            float now = Time.realtimeSinceStartup;
            bool input = PlayerInput();
            if (input) _lastCommandAt = now;
            OneXJob job = _oneX;
            if (job != null)
            {
                if (input) StopOneX("the player is back");
                else if (_probe != job.probe)
                {
                    _oneX = null;
                    bool reference = job.probe.started && File.Exists(job.file) && job.reference != null && !File.Exists(job.reference);
                    if (reference) File.Copy(job.file, job.reference);
                    _oneXLast = job.probe.started && File.Exists(job.file) ? "Played " + job.run + " at 1x: " + job.file + (reference ? ", its 1x reference now" : "")
                        : job.run + "'s 1x play did not start";
                    Logger.LogInfo(_oneXLast);
                }
                return;
            }
            if (!_settings.idle1x || now - _lastCommandAt < OneXIdleSeconds || now < _oneXScanAt || !OneXIdle()) return;
            _oneXScanAt = now + OneXIdleSeconds;
            try { StartOneX(null); }
            catch (Exception ex)
            {
                _oneXLast = "1x job: " + ex.Message;
                Logger.LogWarning(_oneXLast);
            }
        }

        object StartOneX(FullTasRunDef only)
        {
            List<FullTasRunDef> runs = only != null ? new List<FullTasRunDef> { only } : _fullTasRuns.Where(r => r.Section != "test").OrderBy(r => r.Section == "complete" ? 0 : 1).ToList();
            foreach (FullTasRunDef run in runs)
            {
                MacroDefinition best = FullTasPlayableRoute(run);
                if (best == null) continue;
                string ticks = JsonConvert.SerializeObject(ToTicks(best.steps));
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(ticks))).Replace("-", "").Substring(0, 10).ToLowerInvariant();
                string file = Path.Combine(OneXDir, run.name + "-" + hash + ".json");
                if (only == null && File.Exists(file)) continue;
                HandleProbe("/api/probe/run", new JObject { ["name"] = run.name + " 1x", ["route"] = "fulltas/" + run.name + "_NONE", ["ticks"] = JArray.Parse(ticks),
                    ["lean"] = true, ["fast"] = true });
                _probe.file = file;
                _oneX = new OneXJob { run = run.name, file = file, probe = _probe, reference = run.Section == "complete" ? Path.Combine(OneXDir, run.name + ".json") : null };
                _oneXLast = "Playing " + run.name + "'s current best at 1x (" + best.name + ")";
                Logger.LogInfo(_oneXLast);
                return new { ok = true, run = run.name, route = best.name, file };
            }
            return null;
        }
    }
}
