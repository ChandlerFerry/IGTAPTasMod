using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IGTAP.EngineSim.Cli
{
    // --auto's outlook (docs/fulltas.md, "Outlook"): after every pull, one `outlook` event (stdout only) that says whether more
    // search is likely to beat the best, and what else there is to try. Its odds rest on the other Improve runs in the same
    // folder (their .auto.jsonl: how often they gained after a dry spell as long as this one), updated with this run's own
    // gains (every earlier session of it counted), so a fresh or restarted Improve doesn't read a few seconds as a pace.
    sealed partial class FullTas
    {
        /// <summary>An .auto.jsonl read back: its sessions' search seconds, its gains on the run's search clock (sessions end to
        /// end), and each pull's lever with the misses at its spot (lever, goal) before it in its session.</summary>
        sealed class AutoHistory
        {
            public int Sessions;
            public double Search;
            public readonly List<(double at, long gain)> Gains = new List<(double, long)>();
            public readonly List<(string lever, int misses, bool gain)> Pulls = new List<(string, int, bool)>();

            public static AutoHistory Read(string path)
            {
                var h = new AutoHistory();
                double end = 0;
                var misses = new Dictionary<(string, int), int>();
                void Close()
                {
                    if (end > 0) { h.Sessions++; h.Search += end; }
                    end = 0;
                    misses.Clear();
                }
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Contains("\"type\":\"autoStart\"", StringComparison.Ordinal)) { Close(); continue; }
                    bool pull = line.Contains("\"type\":\"pull\"", StringComparison.Ordinal), record = line.Contains("\"type\":\"record\"", StringComparison.Ordinal);
                    if (!pull && !record) continue;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(line);
                        JsonElement e = doc.RootElement;
                        if (!e.TryGetProperty("wall", out JsonElement w) || w.ValueKind != JsonValueKind.Number) continue;
                        double wall = w.GetDouble();
                        end = Math.Max(end, wall);
                        long gain = e.TryGetProperty("gain", out JsonElement g) && g.ValueKind == JsonValueKind.Number ? (long)g.GetDouble() : 0;
                        if (record && gain > 0) h.Gains.Add((h.Search + wall, gain));
                        if (pull && e.TryGetProperty("lever", out JsonElement l) && l.ValueKind == JsonValueKind.String
                            && e.TryGetProperty("goal", out JsonElement k) && k.ValueKind == JsonValueKind.Number)
                        {
                            var spot = (l.GetString(), k.GetInt32());
                            int m = misses.TryGetValue(spot, out int was) ? was : 0;
                            h.Pulls.Add((spot.Item1, m, gain > 0));
                            misses[spot] = gain > 0 ? 0 : m + 1;
                        }
                    }
                    // A line a killed session left half written.
                    catch (JsonException) { }
                }
                Close();
                return h;
            }
        }

        /// <summary>This run's earlier sessions: how many, their search seconds, and each gain (search second, ticks).</summary>
        int _pastSessions;
        double _pastSearch;
        readonly List<(double at, long gain)> _pastGains = new List<(double, long)>();
        /// <summary>The other Improve runs in this run's folder: how many, their search seconds and gains, and their dry spells
        /// (search from the start or a gain to the next gain, or to the run's end: `gain` false).</summary>
        int _poolRuns, _poolGains;
        double _poolSearch;
        long _poolTicks;
        readonly List<(double length, bool gain)> _poolSpells = new List<(double, bool)>();
        /// <summary>Every pull of every run in the folder (this one's too, this session's as they end) by lever and the misses at
        /// its spot before it (capped at <see cref="MissCap"/>): pulls, and pulls that gained.</summary>
        readonly Dictionary<(string, int), (int pulls, int gains)> _leverRecord = new Dictionary<(string, int), (int, int)>();
        /// <summary>Every pull this session per (lever, goal), the scheduler's included (its arms aren't the bandit's): pulls, CPU
        /// s, ticks gained, and misses since its last gain.</summary>
        readonly Dictionary<(string, int), (int pulls, double cpu, long gain, int misses)> _spots = new Dictionary<(string, int), (int, double, long, int)>();

        /// <summary>The misses at a spot the lever record tells apart; more count as this many.</summary>
        const int MissCap = 5;
        /// <summary>The weight of the other runs' record, in seconds of this run's own search.</summary>
        const double PriorSearch = 1800;
        /// <summary>Own gains and search count with weight e^(-age / (this share of all search)): recent ones count most.</summary>
        const double RecentShare = .3;
        /// <summary>The chance of a gain in the next hour at or above which more search is "likely", below which "unlikely".</summary>
        const double LikelyHour = .6, UnlikelyHour = .25;

        void CountSpot(string lever, int goal, double cpu, long gain)
        {
            var was = Spot(lever, goal);
            _spots[(lever, goal)] = (was.pulls + 1, was.cpu + cpu, was.gain + gain, gain > 0 ? 0 : was.misses + 1);
            CountLever(lever, was.misses, gain > 0);
        }

        (int pulls, double cpu, long gain, int misses) Spot(string lever, int goal) { return _spots.TryGetValue((lever, goal), out var s) ? s : (0, 0, 0, 0); }

        void CountLever(string lever, int misses, bool gain)
        {
            var key = (lever, Math.Min(misses, MissCap));
            var was = _leverRecord.TryGetValue(key, out var r) ? r : (0, 0);
            _leverRecord[key] = (was.Item1 + 1, was.Item2 + (gain ? 1 : 0));
        }

        (int pulls, int gains) LeverRecord(string lever, int misses) { return _leverRecord.TryGetValue((lever, Math.Min(misses, MissCap)), out var r) ? r : (0, 0); }

        static readonly Dictionary<string, string> LeverWords = new Dictionary<string, string>
        {
            ["polish"] = "input polish", ["window"] = "window search", ["lookahead"] = "look-ahead window", ["span"] = "long window",
            ["dropreset"] = "dropping a quick restart", ["graft"] = "graft from a known route", ["plan"] = "contact planner",
            ["combine"] = "splicing known routes", ["revive"] = "reviving a known route", ["goals"] = "goal edits",
        };

        /// <summary>This run's earlier sessions and the folder's other runs (read before this session's autoStart is written).</summary>
        void ReadHistory()
        {
            string dir = Path.GetDirectoryName(_autoLog);
            foreach (string file in Directory.GetFiles(dir, "*.auto.jsonl"))
            {
                AutoHistory h;
                try { h = AutoHistory.Read(file); }
                // Another Improve writing it right now.
                catch (IOException) { continue; }
                foreach (var p in h.Pulls) CountLever(p.lever, p.misses, p.gain);
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(_autoLog), StringComparison.OrdinalIgnoreCase))
                {
                    _pastSessions = h.Sessions;
                    _pastSearch = h.Search;
                    _pastGains.AddRange(h.Gains);
                    continue;
                }
                if (h.Search <= 0) continue;
                _poolRuns++;
                _poolSearch += h.Search;
                _poolGains += h.Gains.Count;
                _poolTicks += h.Gains.Sum(g => g.gain);
                double from = 0;
                foreach (double at in h.Gains.Select(g => g.at).Distinct().OrderBy(a => a))
                {
                    _poolSpells.Add((at - from, true));
                    from = at;
                }
                _poolSpells.Add((h.Search - from, false));
            }
        }

        /// <summary>The other runs past a dry spell of `spell` seconds: the gains that ended a longer one, and their search past it.</summary>
        (int gains, double search) PoolAfter(double spell)
        {
            int k = 0;
            double e = 0;
            foreach (var (length, gain) in _poolSpells)
                if (length > spell) { e += length - spell; if (gain) k++; }
            return (k, e);
        }

        static string Span(double seconds)
        {
            if (seconds < 90) return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
            if (seconds < 5400) return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min";
            if (seconds < 172800) return (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h";
            return "days";
        }

        static string Percent(double p) { return Math.Round(100 * p).ToString(CultureInfo.InvariantCulture) + "%"; }

        /// <summary>
        /// The outlook: the chance of beating the best with more search. Gains are a Poisson process whose rate has a gamma
        /// prior from the other runs in the folder, conditioned on how long this run has gone without a gain (g): of their dry
        /// spells longer than g, k ended in a gain over E seconds of search past g, so the prior is shape (k + 1/2) P / (E + P)
        /// over P = <see cref="PriorSearch"/> seconds. This run's own gains and search update it, each weighted
        /// e^(-age / (0.3 × all search)). With shape a and scale b, P(a gain within t more) = 1 - (b / (b + t))^a and the
        /// median wait is b (2^(1/a) - 1). Backtested on the recorded runs (docs/fulltas.md, "Outlook"). The floor is the model's
        /// (the best less every searched goal's gap to its bound: no route on these goals is faster); headroom is the model's
        /// expected ticks. `exhausted`: the bandit has no arm left.
        /// </summary>
        void Outlook(bool exhausted = false)
        {
            if (_model == null) return;
            string unit = _course != null ? "waypoint" : "goal";
            double searched = _pastSearch + (_clock.Elapsed - _autoWall0).TotalSeconds;
            List<(double at, long gain)> gains = _pastGains
                .Concat(_curve.Skip(1).Select((c, i) => (at: _pastSearch + c.wall, gain: _curve[i].ticks - c.ticks))).Where(g => g.gain > 0).ToList();
            double spell = searched - (gains.Count > 0 ? gains.Max(g => g.at) : 0);
            var (poolK, poolE) = PoolAfter(spell);
            double tau = Math.Max(1, RecentShare * searched);
            double shape = (poolK + .5) * PriorSearch / (poolE + PriorSearch) + gains.Sum(g => Math.Exp(-(searched - g.at) / tau));
            double scale = PriorSearch + tau * (1 - Math.Exp(-searched / tau));
            double Chance(double more) => more <= 0 ? 0 : 1 - Math.Pow(scale / (scale + more), shape);
            double pHour = Chance(3600), median = scale * (Math.Pow(2, 1 / shape) - 1);
            double remaining = Math.Max(0, _minutes * 60 - _clock.Elapsed.TotalSeconds);
            double? typical = gains.Count > 0 ? Math.Round(gains.Average(g => (double)g.gain), 1)
                : _poolGains > 0 ? Math.Round((double)_poolTicks / _poolGains, 1) : (double?)null;
            double? lastAgo = gains.Count > 0 ? Math.Round(spell) : (double?)null;

            // The goals the loop searches and the timer counts (a course record's goals up to the start gate count for neither).
            List<GoalRow> searchedGoals = _model.Where(r => InFocus(r.K) && r.K > TimerGoal).ToList();
            long floor = _best.Last - searchedGoals.Sum(r => r.Room);
            double headroom = searchedGoals.Sum(r => r.Expected);
            List<GoalRow> hot = searchedGoals.Where(r => r.Expected >= .5).OrderByDescending(r => r.Expected).Take(3).ToList();
            List<(Arm arm, double score)> ranked = RankArms();
            exhausted |= ranked.Count == 0;
            var untried = ranked.Where(r => Spot(r.arm.Lever, r.arm.Goal).pulls == 0).GroupBy(r => r.arm.Lever)
                .Select(g => (lever: g.Key, goals: g.Select(r => r.arm.Goal).Distinct().OrderBy(k => k).ToList())).ToList();
            bool narrowed = _focusFrom > TimerGoal + 1 || _focusTo < _goals;

            string verdict, headline;
            var advice = new List<string>();
            if (floor >= _best.Last)
            {
                verdict = "floor";
                headline = $"Can't beat this: {Describe(_best)} is at the speed floor on every {unit}.";
                advice.Add($"Stop. Only a different line (a skip that changes the {unit}s, a new trick) could be faster.");
            }
            else if (exhausted)
            {
                verdict = "exhausted";
                headline = "Nothing left to try: no tactic the scheduler has is expected to pay any more.";
                advice.Add("Stop. More time repeats what already ran.");
            }
            else
            {
                verdict = pHour >= LikelyHour ? "likely" : pHour >= UnlikelyHour ? "possible" : "unlikely";
                headline = $"{(verdict == "likely" ? "Likely" : verdict == "possible" ? "Possible" : "Unlikely")} to improve: about {Percent(pHour)} chance of a gain in the next hour of search.";
                string left = remaining > 0 ? $"{Percent(Chance(remaining))} chance of one in this run's last {Span(remaining)}" : "this run's minutes are up";
                if (verdict == "likely") advice.Add($"Keep it running: {left}.");
                else if (verdict == "possible") advice.Add($"Half the time the next gain takes more than {Span(median)} of search; {left}. Worth it only if you can leave it that long.");
                else advice.Add($"Stop, or change the search: half the time the next gain takes more than {Span(median)}; {left}.");
            }
            // What else to try: only once more of the same looks poor.
            if (verdict == "unlikely" || verdict == "exhausted")
            {
                // Picking one goal narrows the search only where it searches several.
                if (hot.Count > 0 && searchedGoals.Count >= 3)
                    advice.Add($"Most headroom the model sees: {unit} {hot[0].K} (~{hot[0].Expected.ToString("0.0", CultureInfo.InvariantCulture)} ticks"
                        + (hot[0].Worst.Length > 0 ? $"; {hot[0].Worst}" : "") + $"). Pick it and Improve it alone.");
                if (untried.Count > 0)
                    advice.Add("Not tried yet on this run: " + string.Join("; ", untried.Select(u => $"{(LeverWords.TryGetValue(u.lever, out string w) ? w : u.lever)} at {unit}{(u.goals.Count == 1 ? "" : "s")} {Ranges(u.goals)}"))
                        + ". The scheduler ranks them low; it gets to them only if nothing better is left.");
                if (narrowed) advice.Add($"Only {unit}s {_focusFrom}-{_focusTo} were searched: Improve the whole route too.");
            }

            Emit(new
            {
                type = "outlook", verdict, headline, advice, unit,
                best = _best.LiveSeconds, bestTicks = _best.Last, floor = Seconds(Math.Max(0, floor)), floorTicks = Math.Max(0, floor),
                headroom = Math.Round(headroom, 1),
                hot = hot.Select(r => new { goal = r.K, label = r.Label, expected = Math.Round(r.Expected, 1), slow = r.Worst }),
                searched = Math.Round(searched), sessions = _pastSessions + 1, pulls = _pullNo,
                gains = gains.Count, gained = gains.Sum(g => g.gain), lastGainAgo = lastAgo, typicalGain = typical,
                chanceHour = Math.Round(pHour, 3), medianWait = Math.Round(median),
                remaining = Math.Round(remaining), chanceRemaining = Math.Round(Chance(remaining), 3),
                // What the odds rest on: the folder's other runs, and their gains past a dry spell as long as this one.
                basis = new { runs = _poolRuns, search = Math.Round(_poolSearch), gains = _poolGains, spell = Math.Round(spell), spellGains = poolK, spellSearch = Math.Round(poolE) },
                untried = untried.Select(u => new { lever = u.lever, goals = u.goals }),
            });
        }

        /// <summary>Goal numbers as runs: 3-5, 8, 11-12.</summary>
        static string Ranges(List<int> ks)
        {
            var parts = new List<string>();
            for (int i = 0; i < ks.Count;)
            {
                int j = i;
                while (j + 1 < ks.Count && ks[j + 1] == ks[j] + 1) j++;
                parts.Add(j == i ? ks[i].ToString(CultureInfo.InvariantCulture) : $"{ks[i]}-{ks[j]}");
                i = j + 1;
            }
            return string.Join(", ", parts);
        }
    }
}
