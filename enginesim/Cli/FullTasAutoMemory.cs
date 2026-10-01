using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IGTAP.EngineSim.Cli
{
    // --auto's memory (docs/fulltas.md, "Memory"): what this run's --auto sessions tried, in <run>.auto-memory.json for the same
    // goals, so a restarted Improve goes on where the last one stopped instead of replaying it (FalseEnding-Any%: 43 of 79 minutes
    // over six restarts were the same combine, revive, goals and reset-study pulls again). What holds for any best (splices
    // played, revived routes, studied resets, dead plans, followed leads, goal edits played, the bandit's arms) comes back
    // whenever the goals match; what holds for one best (merged finds, polished goals, window leads shown) only on that best.
    sealed partial class FullTas
    {
        string _memoryFile;
        /// <summary>Goal edits played (EditKey): each is its whole spliced route, so playing it again gives the same run.</summary>
        readonly HashSet<ulong> _editsTried = new HashSet<ulong>();

        static string Hex(ulong v) { return v.ToString("x16", CultureInfo.InvariantCulture); }
        static ulong FromHex(string s) { return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }
        ulong BestKey() { return SnapshotIndex.Hash(_best.Inputs, _best.Inputs.Count); }
        // An add's key is new since adds re-time their later waits (AddRetunes): ones played before without it play again once.
        static ulong EditKey(GoalEdit e) { ulong h = Mix(SnapshotIndex.Hash(e.Inputs, e.Inputs.Count), ValueHash(string.Join("|", e.Labels))); return e.Kind == "add" ? Mix(h, 0xADD) : h; }

        void SaveMemory()
        {
            // Followed leads are kept by their route's inputs: a reloaded reference's How gains " (earlier --auto)".
            Dictionary<string, ulong> keyOf = _refs.GroupBy(r => r.How).ToDictionary(g => g.Key, g => g.First().Key);
            Solve.WriteShared(_memoryFile, JsonSerializer.Serialize(new
            {
                name = _name, goals = _labels, best = Hex(BestKey()),
                combine = _combineTried.Select(Hex),
                revive = _reviveTried.Select(kv => new { key = Hex(kv.Key), n = kv.Value }),
                revivedOn = _revivedOn.Select(Hex),
                edits = _editsTried.Select(Hex),
                lapSearched = _lapSearched,
                studied = _studied.Select(x => new[] { x.goal, x.offset }),
                wallStudied = _wallStudied.Select(x => new[] { x.goal, x.offset }),
                planDead = _planDead,
                grafted = _grafted.Where(g => keyOf.ContainsKey(g.Item1)).Select(g => new { key = Hex(keyOf[g.Item1]), goal = g.Item2 }),
                arms = _arms.Values.Where(a => a.Pulls > 0).Select(a => new { lever = a.Lever, goal = a.Goal, pulls = a.Pulls, wins = a.Wins, cpu = a.Cpu, gain = a.Gain }),
                spots = _spots.Select(kv => new { lever = kv.Key.Item1, goal = kv.Key.Item2, kv.Value.pulls, kv.Value.cpu, kv.Value.gain, kv.Value.misses }),
                levers = _leverCost.Select(kv => new { lever = kv.Key, kv.Value.pulls, kv.Value.cpu }),
                merged = (ReferenceEquals(_mergedOn, _best) ? _merged : new HashSet<ulong>()).Select(Hex),
                polished = ReferenceEquals(_polishedBest, _best) ? _polished : new HashSet<int>(),
                leads = (ReferenceEquals(_leadFor, _best) ? _leadSeen : new Dictionary<(int, int), long>()).Select(kv => new[] { kv.Key.Item1, kv.Key.Item2, kv.Value }),
            }));
        }

        void LoadMemory()
        {
            if (!File.Exists(_memoryFile)) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_memoryFile));
                JsonElement m = doc.RootElement;
                if (!m.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).SequenceEqual(_labels)) return;
                IEnumerable<JsonElement> All(string name) => m.GetProperty(name).EnumerateArray();
                foreach (JsonElement h in All("combine")) _combineTried.Add(FromHex(h.GetString()));
                foreach (JsonElement r in All("revive")) _reviveTried[FromHex(r.GetProperty("key").GetString())] = r.GetProperty("n").GetInt32();
                foreach (JsonElement h in All("edits")) _editsTried.Add(FromHex(h.GetString()));
                if (m.TryGetProperty("lapSearched", out JsonElement searched)) foreach (JsonElement s in searched.EnumerateArray()) _lapSearched.Add(s.GetString());
                if (m.TryGetProperty("revivedOn", out JsonElement on)) foreach (JsonElement h in on.EnumerateArray()) _revivedOn.Add(FromHex(h.GetString()));
                foreach (JsonElement s in All("studied")) _studied.Add((s[0].GetInt32(), s[1].GetInt32()));
                if (m.TryGetProperty("wallStudied", out JsonElement wall)) foreach (JsonElement s in wall.EnumerateArray()) _wallStudied.Add((s[0].GetInt32(), s[1].GetInt32()));
                foreach (JsonElement k in All("planDead")) _planDead.Add(k.GetInt32());
                Dictionary<ulong, string> howOf = _refs.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().How);
                foreach (JsonElement g in All("grafted"))
                    if (howOf.TryGetValue(FromHex(g.GetProperty("key").GetString()), out string how)) _grafted.Add((how, g.GetProperty("goal").GetInt32()));
                foreach (JsonElement a in All("arms"))
                {
                    var arm = new Arm { Lever = a.GetProperty("lever").GetString(), Goal = a.GetProperty("goal").GetInt32(), Pulls = a.GetProperty("pulls").GetInt32(),
                        Wins = a.GetProperty("wins").GetInt32(), Cpu = a.GetProperty("cpu").GetDouble(), Gain = a.GetProperty("gain").GetDouble() };
                    _arms[(arm.Lever, arm.Goal)] = arm;
                }
                foreach (JsonElement s in All("spots"))
                    _spots[(s.GetProperty("lever").GetString(), s.GetProperty("goal").GetInt32())] = (s.GetProperty("pulls").GetInt32(), s.GetProperty("cpu").GetDouble(), s.GetProperty("gain").GetInt64(),
                        // A memory saved before misses were kept: every pull a miss unless the spot gained.
                        s.TryGetProperty("misses", out JsonElement mi) ? mi.GetInt32() : s.GetProperty("gain").GetInt64() > 0 ? 0 : s.GetProperty("pulls").GetInt32());
                foreach (JsonElement l in All("levers")) _leverCost[l.GetProperty("lever").GetString()] = (l.GetProperty("pulls").GetInt32(), l.GetProperty("cpu").GetDouble());
                bool same = m.GetProperty("best").GetString() == Hex(BestKey());
                if (same)
                {
                    _mergedOn = _best;
                    foreach (JsonElement h in All("merged")) _merged.Add(FromHex(h.GetString()));
                    _polishedBest = _best;
                    foreach (JsonElement k in All("polished")) _polished.Add(k.GetInt32());
                    _leadFor = _best;
                    foreach (JsonElement l in All("leads")) _leadSeen[((int)l[0].GetInt64(), (int)l[1].GetInt64())] = l[2].GetInt64();
                }
                Log($"auto: memory from {Path.GetFileName(_memoryFile)}: {_combineTried.Count} splices, {_reviveTried.Count} revived routes, {_editsTried.Count} goal edits, "
                    + $"{_studied.Count} resets studied, {_arms.Count} arms ({_arms.Values.Sum(a => a.Pulls)} pulls){(same ? ", and this best's merged finds, polished goals and window leads" : "")}");
            }
            // A file a killed session left half written, or one from before a field was added: start without it.
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is FormatException || ex is InvalidOperationException)
            {
                Log($"auto: memory {Path.GetFileName(_memoryFile)} not read ({ex.GetType().Name}); starting without it");
            }
        }
    }
}
