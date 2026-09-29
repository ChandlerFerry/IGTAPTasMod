using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IGTAP.EngineSim.Cli
{
    // --auto's outlook (docs/fulltas.md, "Outlook"): after every pull, one `outlook` event (stdout only) that says whether more
    // search is likely to beat the best, and what else there is to try. It counts this run's earlier --auto sessions (its
    // .auto.jsonl) as search already spent, so a restarted Improve doesn't start its odds over.
    sealed partial class FullTas
    {
        /// <summary>Earlier --auto sessions of this run: how many, their search seconds, and each gain (search second, ticks).</summary>
        int _pastSessions;
        double _pastSearch;
        readonly List<(double at, long gain)> _pastGains = new List<(double, long)>();
        /// <summary>Every pull this session per (lever, goal), the scheduler's included (its arms aren't the bandit's): pulls, CPU s, ticks gained.</summary>
        readonly Dictionary<(string, int), (int pulls, double cpu, long gain)> _spots = new Dictionary<(string, int), (int, double, long)>();

        void CountSpot(string lever, int goal, double cpu, long gain)
        {
            var was = Spot(lever, goal);
            _spots[(lever, goal)] = (was.pulls + 1, was.cpu + cpu, was.gain + gain);
        }

        (int pulls, double cpu, long gain) Spot(string lever, int goal) { return _spots.TryGetValue((lever, goal), out var s) ? s : (0, 0, 0); }

        /// <summary>Less search than this (seconds, every session) and no gain yet: too early to judge.</summary>
        const double EarlySearch = 600;
        /// <summary>The chance of a gain in the next hour at or above which more search is "likely", below which "unlikely".</summary>
        const double LikelyHour = .6, UnlikelyHour = .25;

        static readonly Dictionary<string, string> LeverWords = new Dictionary<string, string>
        {
            ["polish"] = "input polish", ["window"] = "window search", ["lookahead"] = "look-ahead window", ["span"] = "long window",
            ["dropreset"] = "dropping a quick restart", ["graft"] = "graft from a known route", ["plan"] = "contact planner",
            ["combine"] = "splicing known routes", ["revive"] = "reviving a known route", ["goals"] = "goal edits",
        };

        /// <summary>The run's earlier sessions from its .auto.jsonl (read before this session's autoStart is written).</summary>
        void ReadPastSessions()
        {
            if (!File.Exists(_autoLog)) return;
            double end = 0;
            void Close()
            {
                if (end <= 0) return;
                _pastSessions++;
                _pastSearch += end;
                end = 0;
            }
            foreach (string line in File.ReadLines(_autoLog))
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
                    if (record && e.TryGetProperty("gain", out JsonElement g) && g.ValueKind == JsonValueKind.Number && g.GetInt64() > 0)
                        _pastGains.Add((_pastSearch + wall, g.GetInt64()));
                }
                // A line a killed session left half written.
                catch (JsonException) { }
            }
            Close();
        }

        static string Span(double seconds)
        {
            if (seconds < 90) return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
            if (seconds < 5400) return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min";
            return (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h";
        }

        static string Percent(double p) { return Math.Round(100 * p).ToString(CultureInfo.InvariantCulture) + "%"; }

        /// <summary>
        /// The outlook: the chance of beating the best with more search, from when the gains came. Gains are taken as a Poisson
        /// process whose rate is estimated over the recent half of all search (every session, at least EarlySearch), with a
        /// Jeffreys prior: k gains in that window W give P(a gain within t more) = 1 - (W / (W + t))^(k + 1/2), and a median wait of
        /// W (2^(1/(k + 1/2)) - 1). The recent half, not all of it, because an anytime search's gains thin out: early gains don't
        /// promise later ones. The floor is the model's (the best less every searched goal's gap to its bound: no route on these
        /// goals is faster); headroom is the model's expected ticks. `exhausted`: the bandit has no arm left.
        /// </summary>
        void Outlook(bool exhausted = false)
        {
            if (_model == null) return;
            string unit = _course != null ? "waypoint" : "goal";
            double searched = _pastSearch + (_clock.Elapsed - _autoWall0).TotalSeconds;
            List<(double at, long gain)> gains = _pastGains
                .Concat(_curve.Skip(1).Select((c, i) => (at: _pastSearch + c.wall, gain: _curve[i].ticks - c.ticks))).Where(g => g.gain > 0).ToList();
            double window = Math.Max(searched / 2, Math.Min(searched, EarlySearch));
            int recent = gains.Count(g => g.at >= searched - window);
            double shape = recent + .5;
            double Chance(double more) => window <= 0 || more <= 0 ? 0 : 1 - Math.Pow(window / (window + more), shape);
            double pHour = Chance(3600), median = window * (Math.Pow(2, 1 / shape) - 1);
            double remaining = Math.Max(0, _minutes * 60 - _clock.Elapsed.TotalSeconds);
            var sized = recent > 0 ? gains.Where(g => g.at >= searched - window).ToList() : gains;
            double? typical = sized.Count > 0 ? Math.Round(sized.Average(g => (double)g.gain), 1) : (double?)null;
            double? lastAgo = gains.Count > 0 ? Math.Round(searched - gains.Max(g => g.at)) : (double?)null;

            // The goals the loop searches and the timer counts (a course record's goals up to the start gate count for neither).
            List<GoalRow> searchedGoals = _model.Where(r => InFocus(r.K) && r.K > TimerGoal).ToList();
            long floor = _best.Last - searchedGoals.Sum(r => r.Gap);
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
            else if (searched < EarlySearch && gains.Count == 0)
            {
                verdict = "early";
                headline = $"Too early to tell: {Span(searched)} of search so far.";
                advice.Add($"Let it run at least {Span(EarlySearch - searched)} more before judging.");
            }
            else
            {
                verdict = pHour >= LikelyHour ? "likely" : pHour >= UnlikelyHour ? "possible" : "unlikely";
                headline = $"{(verdict == "likely" ? "Likely" : verdict == "possible" ? "Possible" : "Unlikely")} to improve: about {Percent(pHour)} chance of a gain in the next hour of search.";
                if (verdict == "likely") advice.Add($"Keep it running: {recent} gain{(recent == 1 ? "" : "s")} in the last {Span(window)} of search.");
                else if (verdict == "possible") advice.Add($"Worth about {Span(median)} more: at this pace, half the time the next gain comes by then.");
                else advice.Add($"Stop, or change the search: at this pace the next gain is a coin flip only after {Span(median)} more.");
            }
            if (verdict == "possible" || verdict == "unlikely" || verdict == "exhausted")
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
                best = Seconds(_best.Last), bestTicks = _best.Last, floor = Seconds(Math.Max(0, floor)), floorTicks = Math.Max(0, floor),
                headroom = Math.Round(headroom, 1),
                hot = hot.Select(r => new { goal = r.K, label = r.Label, expected = Math.Round(r.Expected, 1), slow = r.Worst }),
                searched = Math.Round(searched), sessions = _pastSessions + 1, pulls = _pullNo,
                gains = gains.Count, gained = gains.Sum(g => g.gain), lastGainAgo = lastAgo, typicalGain = typical,
                window = Math.Round(window), recent, chanceHour = Math.Round(pHour, 3), medianWait = Math.Round(median),
                remaining = Math.Round(remaining), chanceRemaining = Math.Round(Chance(remaining), 3),
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
