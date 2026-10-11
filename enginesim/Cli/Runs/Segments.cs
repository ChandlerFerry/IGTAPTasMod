using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    sealed class Segment
    {
        public string Id, To, After, Unlocks, Source, Engine;
        public float X, Y;
        public int Phase, Facing;
        public bool Dashing, Ground;
        public List<InputTick> Inputs;
        public long Ticks, Held;
        // Simulation.MovementFingerprint of the start: HashCode is seeded per process, so it lives in memory only.
        public int? Fp;
    }

    // Reuse is checked by replay from the actual state, never assumed: a candidate that doesn't replay exactly is just skipped.
    sealed class SegmentLibrary
    {
        readonly string _file;
        readonly List<Segment> _all = new List<Segment>();
        readonly Dictionary<string, Segment> _byKey = new Dictionary<string, Segment>();
        readonly Dictionary<string, List<Segment>> _byTo = new Dictionary<string, List<Segment>>();
        readonly object _lock = new object();

        SegmentLibrary(string file) { _file = file; }

        public static SegmentLibrary Load(string file, IEnumerable<string> alsoRead)
        {
            var lib = new SegmentLibrary(file);
            lib.Read(file);
            foreach (string other in alsoRead) lib.Read(other);
            return lib;
        }

        void Read(string file)
        {
            if (!File.Exists(file)) return;
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement e in d.RootElement.GetProperty("segments").EnumerateArray())
            {
                string Str(string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                Add(new Segment
                {
                    Id = Str("id"), To = Str("to"), After = Str("after"), Unlocks = Str("unlocks"), Source = Str("source"), Engine = Str("engine"),
                    X = e.GetProperty("x").GetSingle(), Y = e.GetProperty("y").GetSingle(),
                    Phase = e.GetProperty("phase").GetInt32(), Facing = e.GetProperty("facing").GetInt32(),
                    Dashing = e.GetProperty("dashing").GetBoolean(), Ground = e.GetProperty("ground").GetBoolean(),
                    Inputs = Solve.DecodeTicks(Str("inputs")), Ticks = e.GetProperty("ticks").GetInt64(), Held = e.GetProperty("held").GetInt64(),
                });
            }
        }

        // The start is part of a segment: the same inputs filed from another state (a run whose earlier goals changed) are another
        // segment, found from there.
        static string Key(Segment s) => FormattableString.Invariant($"{s.To}|{s.Unlocks}|{s.X:R},{s.Y:R},{s.Phase},{s.Facing}|") + Solve.EncodeTicks(s.Inputs);

        public bool Add(Segment s)
        {
            lock (_lock)
            {
                string key = Key(s);
                if (_byKey.TryGetValue(key, out Segment o))
                {
                    o.Fp ??= s.Fp;
                    return false;
                }
                _byKey[key] = s;
                if (!_byTo.TryGetValue(s.To ?? "", out List<Segment> same)) _byTo[s.To ?? ""] = same = new List<Segment>();
                same.Add(s);
                s.Id ??= FullTasGoalIds.New(null);
                s.Engine ??= BuildInfo.Commit;
                _all.Add(s);
                return true;
            }
        }

        public int Count { get { lock (_lock) return _all.Count; } }

        public IReadOnlyList<Segment> Candidates(string to, string unlocks, float x, float y, int phase, int facing, int max = 8, float radius = 64f, int? fp = null)
        {
            lock (_lock)
                return (_byTo.TryGetValue(to ?? "", out List<Segment> same) ? same : new List<Segment>()).Where(s => s.Unlocks == unlocks)
                    .Select(s => (s, d: MathF.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y))))
                    .Where(c => c.d <= radius)
                    .OrderBy(c => fp != null && c.s.Fp == fp ? 0 : 1).ThenBy(c => c.d).ThenBy(c => c.s.Phase == phase ? 0 : 1).ThenBy(c => c.s.Facing == facing ? 0 : 1).ThenBy(c => c.s.Ticks)
                    .Take(max).Select(c => c.s).ToList();
        }

        public void Save()
        {
            JsonArray rows;
            lock (_lock)
                rows = new JsonArray(_all.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id, ["to"] = s.To, ["after"] = s.After, ["x"] = s.X, ["y"] = s.Y, ["phase"] = s.Phase, ["facing"] = s.Facing,
                    ["dashing"] = s.Dashing, ["ground"] = s.Ground, ["unlocks"] = s.Unlocks, ["inputs"] = Solve.EncodeTicks(s.Inputs),
                    ["ticks"] = s.Ticks, ["held"] = s.Held, ["source"] = s.Source, ["engine"] = s.Engine,
                }).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file)));
            Solve.WriteShared(_file, new JsonObject { ["engine"] = BuildInfo.Commit, ["segments"] = rows }.ToJsonString());
        }
    }

    // A search that found nothing isn't repeated with the same budget on the same engine build; a bigger budget may try again.
    sealed class SegmentFacts
    {
        readonly string _file;
        readonly Dictionary<string, (long budget, string why, string engine)> _facts = new Dictionary<string, (long, string, string)>();
        readonly object _lock = new object();

        public SegmentFacts(string file)
        {
            _file = file;
            if (!File.Exists(file)) return;
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement f in d.RootElement.GetProperty("failed").EnumerateArray())
                _facts[f.GetProperty("key").GetString()] = (f.GetProperty("budget").GetInt64(), f.GetProperty("why").GetString(), f.GetProperty("engine").GetString());
        }

        public bool Failed(string key, long budget)
        {
            lock (_lock) return _facts.TryGetValue(key, out var f) && f.engine == BuildInfo.Commit && budget <= f.budget;
        }

        public void Record(string key, long budget, string why)
        {
            lock (_lock)
            {
                if (_facts.TryGetValue(key, out var f) && f.engine == BuildInfo.Commit && f.budget >= budget) return;
                _facts[key] = (budget, why, BuildInfo.Commit);
                var rows = new JsonArray(_facts.Select(kv => (JsonNode)new JsonObject { ["key"] = kv.Key, ["budget"] = kv.Value.budget, ["why"] = kv.Value.why, ["engine"] = kv.Value.engine }).ToArray());
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file)));
                Solve.WriteShared(_file, new JsonObject { ["failed"] = rows }.ToJsonString());
            }
        }
    }
}
