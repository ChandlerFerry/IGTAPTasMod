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
    /// physics). Turbo and enginesim step Update once per tick, so this is the ground truth for anything frame-rate bound.
    /// </summary>
    public sealed partial class TasPlugin
    {
        const string ProbeRouteId = "probe";

        sealed class ProbeRun
        {
            public string name, route, savestateId, file;
            public long captureAt = -1;
            public string capturedSavestateId;
            public bool gameInterpolation;
            /// <summary>The playback's frame rate (<see cref="PacePlayback"/>; unset: its usual 280 fps).</summary>
            public int fps;
            /// <summary>Play with the omnidirectional dash on top of the route's unlocks (Vman techs), as a tech demo can.</summary>
            public bool omniDash;
            public bool started;
            public List<SolverTick> ticks;
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
                    string route = Text(body, "route").Trim();
                    if (!TechStart.IsMatch(route + " ") || TechStart.Match(route).Length != route.Length)
                        throw new InvalidOperationException("route is a category prefix (1_WJ_DS): it picks the course, zone and unlocks.");
                    string savestateId = Text(body, "savestateId");
                    if (savestateId.Length > 0 && _savestates.Find(s => s.id == savestateId) == null) throw new InvalidOperationException("Savestate not found.");
                    List<SolverTick> ticks = body["ticks"] is JArray list ? list.ToObject<List<SolverTick>>() : null;
                    _probe = new ProbeRun { name = name, route = route, savestateId = savestateId, ticks = ticks,
                        captureAt = body["captureAt"] == null ? -1 : (long)body["captureAt"],
                        gameInterpolation = Text(body, "interpolation") == "game",
                        fps = Number(body, "fps", 0), omniDash = body["omniDash"] != null && (bool)body["omniDash"] };
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
                    return new { ok = true, running = _probe != null, name = _probe?.name, lastFile = _lastProbeFile,
                        gameInterpolation = _gameInterpolation?.ToString() };
            }
            throw new InvalidOperationException("Unknown probe command: " + path);
        }

        /// <summary>A probe with interpolation "game" plays with the body's interpolation as the game set it (the TAS keeps it off).</summary>
        bool ProbeKeepsGameInterpolation(Movement movement)
        {
            return _probe != null && _probe.gameInterpolation && _gameInterpolation != null && !TurboRunner.Active && ProbeActive(movement);
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
            _probe.rows.Add(new
            {
                kind, frame = Time.frameCount, unscaled = Time.unscaledTime, timeScale = Time.timeScale,
                physics = _engine.PhysicsTicks, input = _engine.RunTicks,
                x = p.x - origin.x, y = p.y - origin.y, vx = v.x, vy = v.y, mx = m.x, my = m.y, bvx = body.x, bvy = body.y,
                bodyX = bodyAt.x, bodyY = bodyAt.y, interpolation = _playerBody != null ? _playerBody.interpolation.ToString() : null,
                ground = movement.onGround, wall = movement.OnWall, mode = ProbeCutscene.GetValue(movement).ToString(),
                dash = ProbeDashActive.GetValue(movement), f = ProbeSinceDash.GetValue(movement), facingRight = ProbeFacing.GetValue(movement),
                dashDir = new[] { d.x, d.y }, originX = origin.x, originY = origin.y,
                lastHyperY = TechLastHyperYField == null ? (float?)null : (float)TechLastHyperYField.GetValue(movement),
            });
        }

        void ProbeAfterTick(Movement movement)
        {
            if (!ProbeActive(movement)) return;
            ProbeRow(movement, "tick");
            if (_probe.captureAt >= 0 && _probe.capturedSavestateId == null && _engine.RunTicks >= _probe.captureAt)
            {
                Savestate savestate = SavestateService.Capture(movement, "probe/" + _probe.name + " @" + _engine.RunTicks, MacroLibrary.CurrentSchemaVersion);
                _savestates.Insert(0, savestate);
                WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(savestate.id) + ".json"), savestate, false);
                _probe.capturedSavestateId = savestate.id;
            }
        }

        /// <summary>
        /// A lab savestate for probes (/api/savestates/place): a copy of a savestate (<c>from</c>) or of the player now, with
        /// any of <c>x</c>, <c>y</c> (world frame), <c>vx</c>, <c>vy</c> (Velocity; the body gets Velocity + momentum),
        /// <c>facing</c> (±1: scale and facingRight) and Movement <c>fields</c> ({name: value}) replaced, so a probe can start
        /// anywhere, inside a wall too. Named "lab/…": a start for experiments, never a route's.
        /// </summary>
        Savestate PlaceSavestate(JObject body)
        {
            Movement movement = FindWorldMovement() ?? throw new InvalidOperationException("No player in the world.");
            string fromId = Text(body, "from");
            Savestate source = fromId.Length == 0 ? SavestateService.Capture(movement, "", MacroLibrary.CurrentSchemaVersion)
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
            _savestates.Insert(0, placed);
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(placed.id) + ".json"), placed, false);
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
            if (Instance != null && Instance.ProbeActive(__instance)) Instance.ProbeRow(__instance, "frame");
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
                _probe.name, _probe.route, _probe.savestateId, capturedSavestateId = _probe.capturedSavestateId,
                result = _engine.Status, targetFrameRate = _probe.fps > 0 ? _probe.fps : PlaybackFrameRate, vSync = 0,
                gameInterpolation = _gameInterpolation?.ToString(), keptGameInterpolation = _probe.gameInterpolation,
                ticks = _probe.ticks, rows = _probe.rows,
            }, false);
            Logger.LogInfo("Probe " + _probe.name + ": " + _probe.rows.Count + " rows -> " + file);
            _lastProbeFile = file;
            _probe = null;
        }
    }
}
