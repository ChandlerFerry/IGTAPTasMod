using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MacroEngine = IGTAPTasMod.MacroEngine;

namespace IGTAP.EngineSim.Cli
{
    // --class (docs/fulltas.md, "Classes"): every acceptance of a Full-TAS solve goes through Better and Ahead, which rank by the
    // run class. major: goals, then live time (the solve as it always was, nothing below runs). nmg: goals, then fewer major
    // spans, then live time; a route with a major span is never saved. human-major: goals, then score (live time plus the
    // penalty, HumanWeights: spike-grace ticks, and a whole route's difficulty term: its frame-perfect inputs and back-to-back
    // pairs); human-nmg: goals, fewer major spans, then score. A route's GlitchReport is its replay with a TechSteps recorder
    // (Glitches.Classify), and a whole route's Tally its difficulty measure, each computed when a ranking needs it and kept on
    // the route. The search's own pricing (windows, repair bounds, the cost-to-go) prices the penalty only: a measure takes
    // seconds, a window prices thousands of finds, and a partial run has none.
    sealed partial class FullTas
    {
        readonly RunClass _class;
        /// <summary>--tag T: the run files of one of several solves of a class (fulltas/&lt;run&gt;.&lt;class&gt;.&lt;T&gt;.json).</summary>
        readonly string _tag;
        /// <summary>The run's score weights (the trace header's fulltas.human).</summary>
        HumanWeights _weights = new HumanWeights();
        /// <summary>The run's files' name: the run's, then the class and tag when either is given (<see cref="Suffix"/>).</summary>
        string _runKey;
        /// <summary>Full replays from the run's start by the route's inputs and goals (<see cref="ArchiveHash"/>): what they use and
        /// how they ended.</summary>
        readonly Dictionary<ulong, (GlitchReport full, RunResult run)> _reports = new Dictionary<ulong, (GlitchReport, RunResult)>();
        readonly HashSet<ulong> _candidates = new HashSet<ulong>();
        /// <summary>A human class's difficulty measures, when its weights price difficulty (made in <see cref="Open"/>).</summary>
        Difficulty.Meter _meter;
        /// <summary>Whole routes' difficulty by inputs and goals (<see cref="ArchiveHash"/>), measured once each.</summary>
        readonly Dictionary<ulong, Difficulty.Tally> _tallies = new Dictionary<ulong, Difficulty.Tally>();
        /// <summary>Complete replays the class may save, filed as candidates once their difficulty is measured (<see cref="Tally"/>).</summary>
        readonly Dictionary<ulong, Route> _unfiled = new Dictionary<ulong, Route>();

        /// <summary>Live units (MacroEngine.LiveUnits) per second.</summary>
        const long UnitsPerSecond = 1400;

        bool Classed => _class != RunClass.Major;
        bool Human => Glitches.Human(_class);
        bool NoMajors => Glitches.NoMajors(_class);
        /// <summary>A whole route's score adds its difficulty term: a human class whose weights price it.</summary>
        bool PricesDifficulty => Human && _weights.PricesDifficulty;

        static string ParseTag(string[] args)
        {
            string tag = Solve.Option(args, "--tag", (string)null);
            if (tag != null && !Regex.IsMatch(tag, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("--tag " + tag + ": letters, digits, - and _ only");
            return tag;
        }

        /// <summary>What the run's file names add to its name: nothing for a major solve without a tag, else .&lt;class&gt;[.&lt;tag&gt;].</summary>
        string Suffix => _class == RunClass.Major && _tag == null ? "" : "." + Glitches.Key(_class) + (_tag == null ? "" : "." + _tag);

        /// <summary>A run file beside the run's own that another class's solves keep (fulltas/&lt;run&gt;.&lt;class&gt;.*): not a
        /// known route of a major solve's.</summary>
        bool OtherClassFile(string fileName)
        {
            if (Suffix.Length > 0) return false;
            string rest = fileName.Substring(_runKey.Length + 1);
            return new[] { RunClass.Major, RunClass.Nmg, RunClass.HumanMajor, RunClass.HumanNmg }.Any(c => rest.StartsWith(Glitches.Key(c) + ".", StringComparison.Ordinal));
        }

        /// <summary>The route's nmg and major spans and spike-grace ticks up to its last goal: its replay from the run's start with a
        /// recorder (on <see cref="_eval"/>, left at the run's start), once per inputs and goals. A route may claim goal ticks its
        /// inputs don't play (combine's composite of a known route and the best's tail, a stand-in): its report is cut at the ticks it
        /// claims, for ranking. A human class files the replay itself as a candidate when it finishes every goal and the class may
        /// save it (human-nmg: no major span; <see cref="WriteCandidate"/>), never the route's claimed ticks; when its weights price
        /// difficulty, once the route's difficulty is measured (<see cref="Tally"/>: only a route that could win is).</summary>
        GlitchReport Report(Route r)
        {
            if (r.Report != null) return r.Report;
            ulong key = ArchiveHash(r.Inputs, _labels);
            (GlitchReport full, RunResult run) = Replayed(r.Inputs, key);
            r.Report = full.Within(-1, r.Progress == 0 ? -1 : r.GoalPhysics[r.Progress - 1]);
            if (Human && run.Progress == _goals && _candidates.Add(key))
            {
                var played = new Route { Inputs = r.Inputs, Progress = run.Progress, GoalInput = run.GoalInputTicks, GoalPhysics = run.GoalPhysicsTicks,
                    GoalHeld = run.GoalHeldFrames, EndReason = run.EndReason };
                played.Report = full.Within(-1, played.End);
                if (NoMajors && played.Report.Major > 0) return r.Report;
                if (PricesDifficulty) _unfiled[key] = played;
                else WriteCandidate(played, key);
            }
            return r.Report;
        }

        (GlitchReport full, RunResult run) Replayed(List<InputTick> inputs, ulong key)
        {
            if (_reports.TryGetValue(key, out var known)) return known;
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Restore(_origin);
            var record = new TechSteps(_eval);
            record.Attach();
            record.Clear();
            RunResult run;
            try
            {
                run = _eval.Run(inputs, null, null, 1000000, _ => record.Step());
                record.End(run);
            }
            finally { record.Detach(); }
            return _reports[key] = (Glitches.Classify(record, _weights), run);
        }

        /// <summary>The route's inputs play from the run's start to the goal ticks it claims (its report is the route's own): what a
        /// class writes or submits must.</summary>
        bool Replays(Route r)
        {
            RunResult run = Replayed(r.Inputs, ArchiveHash(r.Inputs, _labels)).run;
            return run.Progress == r.Progress && run.GoalPhysicsTicks.Take(r.Progress).SequenceEqual(r.GoalPhysics.Take(r.Progress))
                && (r.GoalHeld == null || run.GoalHeldFrames.Take(r.Progress).SequenceEqual(r.GoalHeld.Take(r.Progress)));
        }

        /// <summary>
        /// A whole route's difficulty (<see cref="Difficulty.Meter"/>: humanize's version of its inputs, each input's timing window
        /// with <c>difficulty</c>'s defaults), once per inputs and goals and kept on the route. Only a ranking that could go the
        /// route's way asks (<see cref="Ahead"/>: its score without the term under the other's whole score; the best's own). A
        /// complete replay waiting to be filed (<see cref="Report"/>) is written as a candidate with it.
        /// </summary>
        Difficulty.Tally Tally(Route r)
        {
            if (r.Tally is Difficulty.Tally known) return known;
            ulong key = ArchiveHash(r.Inputs, _labels);
            if (!_tallies.TryGetValue(key, out Difficulty.Tally tally))
            {
                var watch = Stopwatch.StartNew();
                tally = _tallies[key] = _meter.Measure($"{_runKey} {(uint)key:x8}", r.Inputs);
                Log($"difficulty of {(uint)key:x8} ({Describe(r)}): {tally}, "
                    + $"{_weights.DifficultySeconds(tally.FramePerfect, tally.BackToBack):0.00} s in {watch.Elapsed.TotalSeconds:0.0} s");
            }
            r.Tally = tally;
            if (_unfiled.Remove(key, out Route played))
            {
                played.Tally = tally;
                WriteCandidate(played, key);
            }
            return tally;
        }

        /// <summary>The seconds a whole route's difficulty adds to its score (<see cref="HumanWeights.DifficultySeconds"/>); none for a
        /// route short of the last goal, or when the class doesn't price difficulty.</summary>
        float DifficultySeconds(Route r)
        {
            if (!PricesDifficulty || r.Progress != _goals) return 0f;
            Difficulty.Tally t = Tally(r);
            return _weights.DifficultySeconds(t.FramePerfect, t.BackToBack);
        }

        /// <summary>The route's score in live units: its live time, its penalty and (unless <paramref name="difficulty"/> is off: its
        /// floor, no measure made) its difficulty term.</summary>
        long ScoreUnits(Route r, bool difficulty = true)
            => r.Live + (long)Math.Round((Report(r).Penalty(_weights) + (difficulty ? DifficultySeconds(r) : 0f)) * UnitsPerSecond);

        double ScoreSeconds(Route r) => Math.Round(r.LiveSeconds + Report(r).Penalty(_weights) + DifficultySeconds(r), 4);

        /// <summary>The route may be saved in the class: no major span in an nmg class.</summary>
        bool Clean(Route r) => !NoMajors || Report(r).Major == 0;

        /// <summary>
        /// The live time a route must come in under to rank ahead of <paramref name="b"/> at the same goals: b's own (major, nmg), its
        /// score (a human class; without <paramref name="difficulty"/>, before its difficulty term, the search's glitch-only scale),
        /// none when b has a major span in an nmg class (a route with fewer ranks ahead at any time).
        /// </summary>
        long Bar(Route b, bool difficulty = true)
        {
            if (!Classed) return b.Live;
            if (b.Progress == 0 || NoMajors && Report(b).Major > 0) return long.MaxValue;
            return Human ? ScoreUnits(b, difficulty) : b.Live;
        }

        /// <summary>Route <paramref name="a"/> ranks ahead of <paramref name="b"/> (both at the same goals) in the class. a's
        /// report is made only when its live time is under <see cref="Bar"/>, its difficulty only when its score without it is.</summary>
        bool Ahead(Route a, Route b)
        {
            if (!Classed) return a.Live < b.Live;
            if (a.Progress == 0 || a.Live >= Bar(b)) return false;
            if (b.Progress == 0) return true;
            GlitchReport ra = Report(a), rb = Report(b);
            if (NoMajors && ra.Major != rb.Major) return ra.Major < rb.Major;
            if (!Human) return a.Live < b.Live;
            long bar = ScoreUnits(b);
            return ScoreUnits(a, false) < bar && ScoreUnits(a) < bar;
        }

        /// <summary>
        /// A window's baseline (the best's tick at its last goal, what a find must beat there) in the class: a human class takes a
        /// find up to the best's penalty inside the window later (in ticks; glitches only, no difficulty term), and an nmg class any
        /// find when the best has a major span in it; the merge then judges the whole route by the class (<see cref="Better"/>).
        /// </summary>
        long Allowance(Route best, int first, int limit, long baseline)
        {
            if (!Classed || baseline == long.MaxValue || best.Progress < limit) return baseline;
            GlitchReport w = Report(best).Within(first == 0 ? -1 : best.GoalPhysics[first - 1], best.GoalPhysics[limit - 1]);
            if (NoMajors && w.Major > 0) return long.MaxValue;
            return Human ? baseline + (long)Math.Ceiling(w.Penalty(_weights) / _dt) : baseline;
        }

        /// <summary>A human class's penalty of route r up to goal k, in ticks (0 in any other class: no report made).</summary>
        long PenaltyTicksTo(Route r, int k) => !Human || k == 0 ? 0 : (long)Math.Ceiling(Report(r).Within(-1, r.GoalPhysics[k - 1]).Penalty(_weights) / _dt);

        /// <summary>Route r at goal k on the class's scale, in ticks: its clock there, and in a human class its penalty so far (what
        /// the repair bounds and the "falls behind" checks compare; major and nmg: the clock alone).</summary>
        long ScoreAt(Route r, int k) => r.Clock(k) + PenaltyTicksTo(r, k);

        /// <summary>Route a is ahead of b at goal k: by its tick there (major and nmg, as always), by <see cref="ScoreAt"/> in a human
        /// class.</summary>
        bool AheadAt(Route a, Route b, int k) => Human ? ScoreAt(a, k) < ScoreAt(b, k) : a.GoalPhysics[k - 1] < b.GoalPhysics[k - 1];

        /// <summary>The best's end on the class's scale in ticks, what a repair's bound must come in under: its last tick, and in a
        /// human class its penalty (glitches only, as the partial runs it bounds are priced); none (long.MaxValue) when an nmg class's
        /// best has a major span (any cleaner route wins).</summary>
        long EndBar(Route b)
        {
            if (!Classed) return b.Last;
            if (NoMajors && Report(b).Major > 0) return long.MaxValue;
            return b.Last + PenaltyTicksTo(b, b.Progress);
        }

        /// <summary>
        /// A window solver's price of its finished runs in the class (<see cref="OfflineSolver.Penalty"/>): each run played whole
        /// from the window's start again with a recorder on the worker's simulation, its spans and spike-grace ticks up to its last
        /// goal priced as the class scores them, so a slower but cleaner way ranks ahead in the window; a major span in an nmg class
        /// never counts. Priced from goal <paramref name="first"/>'s tick (the goals before the window, as <see cref="Allowance"/>
        /// prices the best, so the best's own way scores alike: a window's run starts at its prefix's last input, which can come
        /// ticks before that goal is touched); null: the goals the window's start has. Without <paramref name="priced"/> (a window
        /// held to a time bound) only that refusal. Null for major (and human-major unpriced): the window ranks by time alone.
        /// </summary>
        Func<Simulation, Func<List<InputTick>, RunResult, double>> WindowPenalty(int? first, bool priced = true)
        {
            bool price = priced && Human, noMajors = NoMajors;
            if (!price && !noMajors) return null;
            HumanWeights weights = _weights;
            return sim =>
            {
                var record = new TechSteps(sim);
                return (inputs, result) =>
                {
                    sim.Restore(sim.Start);
                    int from = first ?? sim.Progress;
                    record.Attach();
                    record.Clear();
                    try { record.End(sim.Run(inputs, null, null, 400 + inputs.Count, _ => record.Step())); }
                    finally { record.Detach(); }
                    GlitchReport g = Glitches.Classify(record, weights).Within(from == 0 ? -1 : result.GoalPhysicsTicks[from - 1], result.GoalPhysicsTicks[result.Progress - 1]);
                    if (noMajors && g.Major > 0) return double.PositiveInfinity;
                    return price ? g.Penalty(weights) : 0.0;
                };
            };
        }

        /// <summary>A window find's log text: its time, and in a priced window its penalty and score (major: as always).</summary>
        string FindText(OfflineSolver solver, List<InputTick> inputs, long atGoal)
        {
            long priced = solver.Penalty == null ? 0 : solver.PenaltyTicks(inputs);
            string text = $"{Seconds(atGoal - priced):0.00}s ({atGoal - priced} physics ticks)";
            return solver.Penalty == null ? text : text + $" + {priced} ticks of penalty = score {atGoal}";
        }

        /// <summary>A window's finds a merge splices, the latest (fastest) first: a classed solve takes the slower ones too, which a
        /// human class may rank ahead.</summary>
        int MergeFinds => Classed ? 16 : 5;

        /// <summary>A window's first finds a merge splices too: its splices onto the best's own inputs (MergeAuto).</summary>
        const int FirstFinds = 2;

        /// <summary>A route's class, score (with its difficulty term) and difficulty and spans in its run file, after the run file's
        /// own fields.</summary>
        string RunJson(Route r, object run)
        {
            if (!Classed) return JsonSerializer.Serialize(run);
            JsonObject node = JsonSerializer.SerializeToNode(run).AsObject();
            GlitchReport g = Report(r);
            bool whole = Human && r.Progress == _goals;
            node["class"] = Glitches.Key(_class);
            node["score"] = whole ? ScoreSeconds(r) : null;
            node["difficulty"] = whole && PricesDifficulty ? JsonSerializer.SerializeToNode(Tally(r).Json) : null;
            node["glitches"] = JsonSerializer.SerializeToNode(new
            {
                major = g.Major, nmg = g.Nmg, graceTicks = g.GraceTicks,
                spans = g.Spans.Select(s => new { tech = s.Tech, category = s.Category, tick = s.Tick, physics = s.Physics }),
            });
            return node.ToJsonString();
        }

        /// <summary>A complete route a human class judged, in the run file's shape, for a human to pick from:
        /// fulltas/candidates/&lt;run&gt;/&lt;class&gt;-&lt;score&gt;-&lt;hash8&gt;.json.</summary>
        void WriteCandidate(Route r, ulong key)
        {
            string dir = Path.Combine(Path.GetDirectoryName(_runFile), "candidates", _name);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{Glitches.Key(_class)}-{ScoreSeconds(r).ToString("0.00", CultureInfo.InvariantCulture)}-{(uint)key:x8}.json");
            if (!File.Exists(file)) Solve.WriteShared(file, RunJson(r, RunFields(r)));
        }

        /// <summary>The class's counts for the log, once the route's report is made (nothing for a major solve); a whole route's
        /// score, its floor (">=") while its difficulty isn't measured (the log measures nothing).</summary>
        string ClassNote(Route r)
        {
            if (!Classed || r.Report == null) return "";
            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            string score = !Human || r.Progress != _goals ? ""
                : PricesDifficulty && r.Tally == null ? ", score >= " + F(r.LiveSeconds + r.Report.Penalty(_weights)) + " (difficulty not measured)"
                : ", score " + F(ScoreSeconds(r)) + (r.Tally is Difficulty.Tally t ? $", {t}" : "");
            return $" [{r.Report.Major} major, {r.Report.Nmg} nmg, {r.Report.GraceTicks} grace{score}]";
        }
    }
}
