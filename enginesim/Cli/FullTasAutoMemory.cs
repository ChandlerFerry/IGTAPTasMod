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
    // played, revived routes, studied resets, dead plans, followed leads, the bandit's arms) comes back
    // whenever the goals match; what holds for one best (merged finds, polished goals, window leads shown) only on that best.
    sealed partial class FullTas
    {
        string _memoryFile;

        static string Hex(ulong v) { return v.ToString("x16", CultureInfo.InvariantCulture); }
        static ulong FromHex(string s) { return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }
        ulong BestKey() { return SnapshotIndex.Hash(_best.Inputs, _best.Inputs.Count); }

        void SaveMemory()
        {
            // Followed leads are kept by their route's inputs: a reloaded reference's How gains " (earlier --auto)".
            Dictionary<string, ulong> keyOf = _refs.GroupBy(r => r.How).ToDictionary(g => g.Key, g => g.First().Key);
            // Goals by their ids (IdOf; 0, no goal, is "").
            string Id(int k) => IdOf(k) ?? "";
            Solve.WriteShared(_memoryFile, JsonSerializer.Serialize(new
            {
                name = _name, goals = _ids, best = Hex(BestKey()),
                combine = _combineTried.Select(Hex),
                revive = _reviveTried.Select(kv => new { key = Hex(kv.Key), n = kv.Value }),
                revivedOn = _revivedOn.Select(Hex),
                studied = _studied.Select(x => new object[] { Id(x.goal), x.offset }),
                wallStudied = _wallStudied.Select(x => new object[] { Id(x.goal), x.offset }),
                planDead = _planDead.Select(Id),
                stretches = _stretchPulled, stretchMoves = _stretchMoves,
                grafted = _grafted.Where(g => keyOf.ContainsKey(g.Item1)).Select(g => new { key = Hex(keyOf[g.Item1]), goal = Id(g.Item2) }),
                arms = _arms.Values.Where(a => a.Pulls > 0).Select(a => new { lever = a.Lever, goal = Id(a.Goal), pulls = a.Pulls, wins = a.Wins, cpu = a.Cpu, gain = a.Gain }),
                spots = _spots.Select(kv => new { lever = kv.Key.Item1, goal = Id(kv.Key.Item2), kv.Value.pulls, kv.Value.cpu, kv.Value.gain, kv.Value.misses }),
                levers = _leverCost.Select(kv => new { lever = kv.Key, kv.Value.pulls, kv.Value.cpu }),
                merged = (ReferenceEquals(_mergedOn, _best) ? _merged : new HashSet<ulong>()).Select(Hex),
                polished = (ReferenceEquals(_polishedBest, _best) ? _polished : new HashSet<int>()).Select(Id),
                leads = (ReferenceEquals(_leadFor, _best) ? _leadSeen : new Dictionary<(int, int), long>()).Select(kv => new object[] { Id(kv.Key.Item1), Id(kv.Key.Item2), kv.Value }),
            }));
        }

        void LoadMemory()
        {
            if (!File.Exists(_memoryFile)) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_memoryFile));
                JsonElement m = doc.RootElement;
                if (!m.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).SequenceEqual(_ids)) return;
                IEnumerable<JsonElement> All(string name) => m.GetProperty(name).EnumerateArray();
                // A goal by its id: k from 1 ("": 0).
                int K(JsonElement id) => id.GetString() is { Length: > 0 } s ? Array.IndexOf(_ids, s) + 1 : 0;
                foreach (JsonElement h in All("combine")) _combineTried.Add(FromHex(h.GetString()));
                foreach (JsonElement r in All("revive")) _reviveTried[FromHex(r.GetProperty("key").GetString())] = r.GetProperty("n").GetInt32();
                if (m.TryGetProperty("revivedOn", out JsonElement on)) foreach (JsonElement h in on.EnumerateArray()) _revivedOn.Add(FromHex(h.GetString()));
                foreach (JsonElement s in All("studied")) _studied.Add((K(s[0]), s[1].GetInt32()));
                if (m.TryGetProperty("wallStudied", out JsonElement wall)) foreach (JsonElement s in wall.EnumerateArray()) _wallStudied.Add((K(s[0]), s[1].GetInt32()));
                foreach (JsonElement k in All("planDead")) _planDead.Add(K(k));
                // The stretches pulled count only while the movement table is the one they were planned on.
                if (m.TryGetProperty("stretchMoves", out JsonElement sm) && sm.GetString() == MovesStamp() && m.TryGetProperty("stretches", out JsonElement st))
                {
                    _stretchMoves = sm.GetString();
                    foreach (JsonElement k in st.EnumerateArray()) _stretchPulled.Add(k.GetString());
                }
                Dictionary<ulong, string> howOf = _refs.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().How);
                foreach (JsonElement g in All("grafted"))
                    if (howOf.TryGetValue(FromHex(g.GetProperty("key").GetString()), out string how)) _grafted.Add((how, K(g.GetProperty("goal"))));
                foreach (JsonElement a in All("arms"))
                {
                    var arm = new Arm { Lever = a.GetProperty("lever").GetString(), Goal = K(a.GetProperty("goal")), Pulls = a.GetProperty("pulls").GetInt32(),
                        Wins = a.GetProperty("wins").GetInt32(), Cpu = a.GetProperty("cpu").GetDouble(), Gain = a.GetProperty("gain").GetDouble() };
                    _arms[(arm.Lever, arm.Goal)] = arm;
                }
                foreach (JsonElement s in All("spots"))
                    _spots[(s.GetProperty("lever").GetString(), K(s.GetProperty("goal")))] = (s.GetProperty("pulls").GetInt32(), s.GetProperty("cpu").GetDouble(), s.GetProperty("gain").GetInt64(),
                        // A memory saved before misses were kept: every pull a miss unless the spot gained.
                        s.TryGetProperty("misses", out JsonElement mi) ? mi.GetInt32() : s.GetProperty("gain").GetInt64() > 0 ? 0 : s.GetProperty("pulls").GetInt32());
                foreach (JsonElement l in All("levers")) _leverCost[l.GetProperty("lever").GetString()] = (l.GetProperty("pulls").GetInt32(), l.GetProperty("cpu").GetDouble());
                bool same = m.GetProperty("best").GetString() == Hex(BestKey());
                if (same)
                {
                    _mergedOn = _best;
                    foreach (JsonElement h in All("merged")) _merged.Add(FromHex(h.GetString()));
                    _polishedBest = _best;
                    foreach (JsonElement k in All("polished")) _polished.Add(K(k));
                    _leadFor = _best;
                    foreach (JsonElement l in All("leads")) _leadSeen[(K(l[0]), K(l[1]))] = l[2].GetInt64();
                }
                Log($"auto: memory from {Path.GetFileName(_memoryFile)}: {_combineTried.Count} splices, {_reviveTried.Count} revived routes, "
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
