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
            if (probe.TryGetProperty("start", out JsonElement start)) DemoStart.ApplyStart(sim, start, Path.GetDirectoryName(Path.GetFullPath(args[1])));
            List<InputTick> baseTicks = Ticks(probe, "base");
            if (baseTicks.Count > 0)
            {
                SimSnapshot at = sim.Advance(baseTicks) ?? throw new InvalidOperationException("The base inputs end the run before their last tick.");
                sim.SetStart(at, sim.InputBase + baseTicks.Count);
            }

            List<InputTick> prefix = Ticks(probe, "prefix");
            int extraTicks = probe.TryGetProperty("extraTicks", out JsonElement e) ? e.GetInt32() : 30;
            bool frames = probe.TryGetProperty("frames", out JsonElement fr) && fr.GetBoolean();
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
                int stepped = 0;
                sim.OnFrame = !frames ? null : (alpha, still) =>
                {
                    Dictionary<string, object> row = Row(sim, stepped, extraFields, extraInfos);
                    row["kind"] = "frame";
                    row["alpha"] = Round(alpha);
                    row["still"] = still;
                    log.Add(row);
                };
                RunResult r = sim.Run(inputs, null, extraTicks: extraTicks, onStep: step => { log.Add(Row(sim, step, extraFields, extraInfos)); stepped = step + 1; });
                sim.OnFrame = null;
                output.Add(new { name, finished = r.Finished, time = r.Time, end = r.EndReason, ticks = log });
            }
            string json = JsonSerializer.Serialize(new { branches = output }, new JsonSerializerOptions { WriteIndented = false });
            int outAt = Array.IndexOf(args, "--out");
            if (outAt >= 0 && outAt + 1 < args.Length) File.WriteAllText(args[outAt + 1], json);
            else Console.WriteLine(json);
            return 0;
        }

        static readonly FieldInfo Dir = typeof(Movement).GetField("dashDirection", Fields);

        internal static Dictionary<string, object> Row(Simulation sim, int step, string[] extraFields, FieldInfo[] extraInfos)
        {
            WorldPos p = sim.PlayerWorld;
            Movement pl = sim.Player;
            Vector2 v = pl.Velocity, m = pl.momentum, d = (Vector2)Dir.GetValue(pl);
            var (_, _, _, bvx, bvy, _) = sim.Physics.PlayerBody();
            var row = new Dictionary<string, object>
            {
                ["step"] = step, ["input"] = (int)sim.Engine.RunTicks - 1,
                ["x"] = Round(p.x), ["y"] = Round(p.y), ["vx"] = Round(v.x), ["vy"] = Round(v.y), ["mx"] = Round(m.x), ["my"] = Round(m.y),
                ["bvx"] = Round(bvx), ["bvy"] = Round(bvy),
                ["ground"] = pl.onGround, ["wall"] = pl.OnWall, ["mode"] = pl.cutsceneMode.ToString(),
                ["dash"] = pl.dashActive, ["f"] = pl.framesSinceLastDash, ["facingRight"] = pl.facingRight,
                ["dashDir"] = new[] { Round(d.x), Round(d.y) },
                ["courseTime"] = sim.TimingCourse() is courseScript timing ? (object)Round(Simulation.CourseTime(timing)) : null,
                ["zone"] = sim.World.All<ZoneLoader>().FirstOrDefault()?.activeZone ?? -1, ["random"] = UnityEngine.Random.state.s3,
                ["gridCalls"] = sim.Shop.Economy?.GridCalls ?? -1,
            };
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




    }
}
