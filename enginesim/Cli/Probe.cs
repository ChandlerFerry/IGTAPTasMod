using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IGTAP.EngineSim;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// probe &lt;trace.json&gt; &lt;probe.json&gt; [--out file]: set up one state (the trace's start, optionally moved or loaded from a
    /// studio savestate), then run branches of inputs from it and log every physics tick. Optional 1x frame modelling:
    /// Update frames between ticks and the dash freeze's Update frames, which turbo and the default offline run never have.
    /// </summary>
    static class Probe
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: probe <trace.json> <probe.json> [--out file]");
                return 2;
            }
            var (sim, _, _) = Program.Open(args[0]);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(args[1]));
            JsonElement probe = doc.RootElement;
            if (probe.TryGetProperty("start", out JsonElement start)) ApplyStart(sim, start, Path.GetDirectoryName(Path.GetFullPath(args[1])));

            List<InputTick> prefix = Ticks(probe, "prefix");
            int updatesPerTick = probe.TryGetProperty("updatesPerTick", out JsonElement u) ? Math.Max(1, u.GetInt32()) : 1;
            float fps = probe.TryGetProperty("fps", out JsonElement f) ? f.GetSingle() : 0f;
            int extraTicks = probe.TryGetProperty("extraTicks", out JsonElement e) ? e.GetInt32() : 30;
            // 1x with interpolation on at this fixed-clock rate (Simulation.InterpolationFps): its frames replace updatesPerTick/fps.
            sim.InterpolationFps = probe.TryGetProperty("interpolationFps", out JsonElement ifps) ? ifps.GetInt32() : 0;
            bool legacyFrames = sim.InterpolationFps == 0;
            string[] extraFields = probe.TryGetProperty("fields", out JsonElement fe) ? fe.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
            FieldInfo[] extraInfos = extraFields.Select(n => typeof(Movement).GetField(n, Fields)
                ?? throw new InvalidOperationException("Movement has no field " + n + ".")).ToArray();

            var branches = probe.TryGetProperty("branches", out JsonElement b) && b.GetArrayLength() > 0
                ? b.EnumerateArray().Select(x => (name: x.GetProperty("name").GetString(), ticks: Ticks(x, "ticks"))).ToList()
                : new List<(string name, List<InputTick> ticks)> { ("prefix", new List<InputTick>()) };

            var output = new List<object>();
            foreach (var (name, ticks) in branches)
            {
                List<InputTick> inputs = prefix.Concat(ticks).ToList();
                var log = new List<Dictionary<string, object>>();
                float freeze = 0f;
                var gameTimeStop = Hooks.TimeStop;
                if (legacyFrames) Hooks.TimeStop = (_, duration) => freeze += duration;
                RunResult r;
                try
                {
                    r = sim.Run(inputs, null, extraTicks: extraTicks, onStep: step =>
                    {
                        Vector3 before = sim.PlayerWorld;
                        int frames = !legacyFrames ? 0 : updatesPerTick - 1 + (fps > 0f ? (int)Math.Round(freeze * fps) : 0);
                        bool froze = freeze > 0f;
                        freeze = 0f;
                        for (int i = 0; i < frames; i++) sim.ExtraUpdate();
                        Vector3 p = sim.PlayerWorld;
                        Dictionary<string, object> row = Row(sim, step, frames, extraFields, extraInfos);
                        if (frames > 0) { row["updateDy"] = Round(p.y - before.y); row["updateDx"] = Round(p.x - before.x); }
                        if (froze) row["freeze"] = true;
                        log.Add(row);
                    });
                }
                finally { Hooks.TimeStop = gameTimeStop; }
                output.Add(new { name, finished = r.Finished, time = r.Time, end = r.EndReason, ticks = log });
            }
            string json = JsonSerializer.Serialize(new { branches = output }, new JsonSerializerOptions { WriteIndented = false });
            int outAt = Array.IndexOf(args, "--out");
            if (outAt >= 0 && outAt + 1 < args.Length) File.WriteAllText(args[outAt + 1], json);
            else Console.WriteLine(json);
            return 0;
        }

        static FieldInfo F(string name) { return typeof(Movement).GetField(name, Fields); }

        static readonly FieldInfo Vel = F("Velocity"), Mom = F("momentum"), Ground = F("onGround"), Wall = F("OnWall"), Mode = F("cutsceneMode"),
            Dash = F("dashActive"), Since = F("framesSinceLastDash"), Facing = F("facingRight"), Dir = F("dashDirection");

        /// <summary>One logged physics tick: world position, Velocity, momentum, body velocity, ground/wall, mode, dash, facing, extra fields.</summary>
        internal static Dictionary<string, object> Row(Simulation sim, int step, int frames, string[] extraFields, FieldInfo[] extraInfos)
        {
            Vector3 p = sim.PlayerWorld;
            Vector2 v = (Vector2)Vel.GetValue(sim.Player), m = (Vector2)Mom.GetValue(sim.Player), d = (Vector2)Dir.GetValue(sim.Player);
            var (_, _, _, bvx, bvy, _) = sim.Physics.PlayerBody();
            var row = new Dictionary<string, object>
            {
                ["step"] = step, ["input"] = (int)sim.Engine.RunTicks - 1,
                ["x"] = Round(p.x), ["y"] = Round(p.y), ["vx"] = Round(v.x), ["vy"] = Round(v.y), ["mx"] = Round(m.x), ["my"] = Round(m.y),
                ["bvx"] = Round(bvx), ["bvy"] = Round(bvy),
                ["ground"] = Ground.GetValue(sim.Player), ["wall"] = Wall.GetValue(sim.Player), ["mode"] = Mode.GetValue(sim.Player).ToString(),
                ["dash"] = Dash.GetValue(sim.Player), ["f"] = Since.GetValue(sim.Player), ["facingRight"] = Facing.GetValue(sim.Player),
                ["dashDir"] = new[] { Round(d.x), Round(d.y) },
            };
            if (frames > 0) row["updates"] = frames;
            for (int i = 0; i < extraFields.Length; i++) row[extraFields[i]] = Plain(extraInfos[i].GetValue(sim.Player));
            return row;
        }

        static float Round(float value) { return (float)Math.Round(value, 3); }

        static object Plain(object value)
        {
            switch (value)
            {
                case Vector2 v: return new[] { Round(v.x), Round(v.y) };
                case Vector3 v: return new[] { Round(v.x), Round(v.y), Round(v.z) };
                case Enum en: return en.ToString();
                case UnityEngine.Object o: return o ? o.name : null;
                default: return value;
            }
        }

        static List<InputTick> Ticks(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out JsonElement list) ? list.EnumerateArray().Select(InputTick.FromJson).ToList() : new List<InputTick>();
        }

        /// <summary>
        /// The probe's start: a studio savestate (its world position is its scene position minus the FloatingOrigin it recorded),
        /// then any of position [x, y] and velocity [vx, vy] in the world frame, scaleX, and Movement fields by name.
        /// </summary>
        internal static void ApplyStart(Simulation sim, JsonElement start, string baseDir)
        {
            Vector2 position = sim.PlayerWorld, velocity = sim.Physics.PlayerBody() is var body ? new Vector2(body.vx, body.vy) : default;
            float? scaleX = null;
            if (start.TryGetProperty("savestate", out JsonElement ss))
            {
                string path = Path.IsPathRooted(ss.GetString()) ? ss.GetString() : Path.Combine(baseDir, ss.GetString());
                using JsonDocument save = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement s = save.RootElement;
                if (!s.TryGetProperty("hasOrigin", out JsonElement has) || !has.GetBoolean())
                    throw new InvalidOperationException("The savestate has no FloatingOrigin (captured by an older plugin); capture it again.");
                JsonElement ox = s.GetProperty("originX"), oy = s.GetProperty("originY");
                position = new Vector2(s.GetProperty("positionX").GetSingle() - ox.GetSingle(), s.GetProperty("positionY").GetSingle() - oy.GetSingle());
                scaleX = s.GetProperty("scaleX").GetSingle();
                foreach (JsonProperty field in s.GetProperty("movementFields").EnumerateObject())
                    SetField(sim.Player, field.Name, field.Value.GetProperty("value").GetString());
                if (s.GetProperty("bodyFields").TryGetProperty("linearVelocity", out JsonElement lv))
                    velocity = ParseVector(lv.GetProperty("value").GetString());
            }
            if (start.TryGetProperty("position", out JsonElement pos)) position = new Vector2(pos[0].GetSingle(), pos[1].GetSingle());
            if (start.TryGetProperty("velocity", out JsonElement vel)) velocity = new Vector2(vel[0].GetSingle(), vel[1].GetSingle());
            if (start.TryGetProperty("scaleX", out JsonElement sx)) scaleX = sx.GetSingle();
            if (start.TryGetProperty("fields", out JsonElement fields))
                foreach (JsonProperty field in fields.EnumerateObject())
                    SetField(sim.Player, field.Name, field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText());
            Transform t = sim.Player.transform;
            Vector3 scene = new Vector3(position.x + sim.Origin.x, position.y + sim.Origin.y, t.position.z);
            if (scaleX.HasValue) t.localScale = new Vector3(scaleX.Value, t.localScale.y, t.localScale.z);
            sim.Physics.ResetPlayer(new Vector2(scene.x, scene.y), velocity, 0f, 0f);
            t.position = scene;
            if (Math.Abs(sim.PlayerWorld.x - position.x) > 0.01f || Math.Abs(sim.PlayerWorld.y - position.y) > 0.01f)
                throw new InvalidOperationException("The start position did not stick: " + sim.PlayerWorld + " for " + position + ".");
            // The trace's engine state still pins the player to the route start while it waits for stable ticks.
            sim.Engine.GetType().GetField("_hasSynchronizationPosition", Fields).SetValue(sim.Engine, false);
            sim.SetStart(sim.Capture(0), 0);
        }

        static void SetField(Movement player, string name, string text)
        {
            FieldInfo f = typeof(Movement).GetField(name, Fields);
            if (f == null) return;
            Type type = f.FieldType;
            text = text.Trim('"', '[', ']');
            object value;
            if (type == typeof(Vector2)) value = ParseVector(text);
            else if (type == typeof(Vector3)) { float[] p = Floats(text); value = new Vector3(p[0], p[1], p.Length > 2 ? p[2] : 0f); }
            else if (type.IsEnum) value = Enum.Parse(type, text, true);
            else if (type == typeof(bool)) value = bool.Parse(text);
            else value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            f.SetValue(player, value);
        }

        static Vector2 ParseVector(string text) { float[] p = Floats(text); return new Vector2(p[0], p[1]); }

        static float[] Floats(string text)
        {
            return text.Trim('[', ']').Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }
    }
}
