using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;
using Caps = IGTAPTasMod.Caps;

namespace IGTAP.EngineSim.Cli
{
    // --parts goals (docs/fulltas.md, "Parts"): the run's economy goals edited when the whole run gets faster. Every stretch of a
    // Full-TAS economy is Cash-bound at its target buy, so a lap or walk made faster only turns into a wait there; what pays is a
    // goal list that needs fewer laps (the clones pay more after a record lap, or the laps before a buy earn more than it needs)
    // or one that laps where the route stands and waits. Each edit is played on a copy: the best's inputs spliced, then idle ticks
    // before the quick restart ahead of each goal it misses (the Cash comes later), and it's adopted only when the whole run is
    // strictly faster and reaches every goal. The run's end goal, its area and reward goals and every unlock buy stay
    // (FullTasGoal.EditRefused); an adopted list goes with the route (the run file's goals, the offline best's and the gate's), and
    // the game updates fulltas.json when it judges that route a record.
    sealed partial class FullTas
    {
        /// <summary>A proposed goal list with the best's inputs spliced for it.</summary>
        sealed class GoalEdit
        {
            public string Kind, Text, Key;
            public string[] Labels;
            /// <summary>The spliced route: its inputs from <see cref="NewAt"/> on are the best's from <see cref="OldAt"/> on.</summary>
            public List<InputTick> Inputs;
            public int NewAt, OldAt;
            /// <summary>Each new goal's goal in the best (0-based; -1: a goal the best doesn't have).</summary>
            public int[] OldOf;
            /// <summary>Old goal numbers (1-based) the edit touches, for the log.</summary>
            public int First, Last;
            /// <summary>The ticks the splice itself removes (before any wait): the most it can save.</summary>
            public long Saves;
            /// <summary>An add in place of a walk (<see cref="AddEdits"/>): the goals it reaches through its laps; the rest are re-found
            /// as an investment (0: not one).</summary>
            public int Refind;
            /// <summary>A ladder (<see cref="LadderEdits"/>): the first goal after it (0-based, new numbering), from which the waits are
            /// re-timed to their Cash once it plays through (0: none).</summary>
            public int Retune;
            /// <summary>The run the splice is made in when it isn't the best (a ladder's, its earlier laps held back: <see cref="LadderEdits"/>).</summary>
            public Route Base;
        }

        /// <summary>One lap of the best: goals K (start:N) and K+1 (finish:N) (0-based), played from the quick restart at input A
        /// to the input before the quick restart at B that starts the step after it. A walk-in lap (<see cref="WalkIn"/>) has no
        /// quick restart before its gate: A is where a record lap puts one, just after the walk touches the gate (the restart then
        /// lands at this course's start), and B is the next quick restart after its finish (-1: none before the goal after).</summary>
        sealed class RunLap
        {
            public int K, A, B, Course;
            public bool WalkIn;
        }

        /// <summary>The earlier goal lists of this run (oldest first: the trace's), which the current one was derived from by
        /// adopted edits. The run file, the offline bests and the gate submissions carry them (goalsFrom): the game adopts a
        /// route's goals only when its own list is one of them.</summary>
        readonly List<string[]> _goalLineage = new List<string[]>();
        /// <summary>The adopted edit's words while its Commit runs (its record event carries them to the studio's feed).</summary>
        string _goalsEdit;
        Route _goalsFor;
        TimeSpan _goalsAt;
        /// <summary>The last goals pull adopted an edit: the next one runs at once on the new goals.</summary>
        bool _goalsAgain;
        List<(int course, Caps caps, string name, List<InputTick> inputs)> _courseRoutes;
        /// <summary>A goals pull tries its edits again only after a new best and this long since the last pull (it replays every
        /// lap edit of the run: about a CPU minute a pull on FalseEnding-Any%), or at once after it adopted one.</summary>
        const double GoalsEveryMinutes = 5;
        const int MaxPadFixes = 6, RecordRoutes = 12, RecordReached = 4, InvestTries = 2;
        /// <summary>A record lap's repair budget: CPU seconds per goal it re-finds (its lead where it stops is a few ticks).</summary>
        const double RecordRepairCpuPerGoal = 20;
        /// <summary>A walk-in lap's quick restart goes 0..WalkInRestarts-1 inputs after the walk touches the gate.</summary>
        const int WalkInRestarts = 4;
        /// <summary>The best's own laps of a course a record lap tries (the shortest, one per distinct input list).</summary>
        const int OwnLapRoutes = 6;
        /// <summary>--record-search-minutes: the wall minutes (every thread) a goals pull searches a lap from the quick restart for a
        /// course and unlock set with no saved -restart lap (once per run; 0: never).</summary>
        double _recordSearchMinutes = 2;
        /// <summary>The (course, unlock set) keys a goals pull already searched a restart lap for (kept in the memory).</summary>
        readonly HashSet<string> _lapSearched = new HashSet<string>();

        bool GoalsDue()
        {
            if (!Part("goals") || ReferenceEquals(_goalsFor, _best)) return false;
            return _goalsFor == null || _goalsAgain || (_clock.Elapsed - _goalsAt).TotalMinutes >= GoalsEveryMinutes;
        }

        /// <summary>
        /// One goals pull: every edit of the best's economy goals played on a copy (in parallel), and the fastest that beats the
        /// best adopted. Returns whether the run's goals changed.
        /// </summary>
        bool GoalsPull()
        {
            Route best = _best;
            _goalsFor = best;
            _goalsAt = _clock.Elapsed;
            double c0 = CpuSeconds();
            var w0 = _clock.Elapsed;
            List<RunLap> laps = Laps(best);
            var edits = new List<GoalEdit>();
            edits.AddRange(DropEdits(best, laps));
            edits.AddRange(AddEdits(best, _model?.Count == _goals ? _model : null));
            edits.AddRange(RegroupEdits(best));
            List<GoalEdit> records = RecordEdits(best, laps);
            edits.AddRange(records);
            List<GoalEdit> inserts = InsertEdits(best, laps);
            edits.AddRange(inserts);
            edits.AddRange(PayoutEdits(best, laps));
            edits.AddRange(LadderEdits(best, laps));
            edits.RemoveAll(e => FullTasGoal.EditRefused(_labels, e.Labels) != null);
            // An edit is its whole spliced route: one played before (this session or an earlier one) plays the same way again.
            int played = edits.RemoveAll(e => _editsTried.Contains(EditKey(e)));
            if (edits.Count == 0)
            {
                Log($"auto: goals: all {played} edits of this best were played before");
                return false;
            }
            _pullNo++;
            int pull = _pullNo;
            Log($"auto: pull {pull}: goals, {edits.Count} goal-list edits of {laps.Count} laps ({edits.Count(e => e.Kind == "drop")} drops, {edits.Count(e => e.Kind == "add")} adds, "
                + $"{edits.Count(e => e.Kind == "regroup")} regroups, {edits.Count(e => e.Kind == "record")} record laps, {edits.Count(e => e.Kind == "insert")} inserted record laps, {edits.Count(e => e.Kind == "payout")} payout breaks, {edits.Count(e => e.Kind == "ladder")} ladders), "
                + "each played on a copy with waits before the quick restarts it needs");
            var results = new (Route route, Route partial, string why)[edits.Count];
            RunOnThreads(edits.Count, (sim, origin, tracker, i) =>
            {
                GoalEdit e = edits[i];
                sim.UseGoals(tracker.For(GoalsOf(e.Labels)));
                results[i] = PlayedRetuned(sim, origin, e, best);
            });
            (GoalEdit edit, Route route) win = default;
            for (int i = 0; i < edits.Count; i++)
            {
                (Route r, _, string why) = results[i];
                GoalEdit e = edits[i];
                string verdict = r == null ? why ?? "not played" : $"{r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s ({r.Last - best.Last:+0;-0} ticks, {r.LiveSeconds - best.LiveSeconds:+0.00;-0.00} s live){(why == null ? "" : ", " + why)}";
                Log($"auto goals: {e.Text}: {verdict}");
                AutoWrite(new { type = "goalEdit", pull, kind = e.Kind, text = e.Text, first = e.First, last = e.Last, goals = e.Labels.Length,
                    time = r == null ? (double?)null : r.LiveSeconds, gain = r == null ? (long?)null : best.Last - r.Last, why });
                if (r != null && r.Live < best.Live && (win.route == null || r.Live < win.route.Live)) win = (e, r);
            }
            bool adopted = false;
            if (win.route != null) adopted = AdoptGoals(win.edit, win.route, best);
            // A record lap keeps the goals: an edit of one that the old way on can't carry to the end (the faster lap's cash buys a
            // box the old inputs only brushed: FalseEnding-Any%'s Course 2 clone box at goal 265) has the goals after its last goal
            // level with the best or ahead re-found, as a faster window's does; the fewest goals to re-find first.
            else
            {
                // Each cut at the last goal before it first falls behind the best (the lap's cash can make the old way on lose ticks
                // well before it stops: FalseEnding-Any%'s Course 1 lap is 2 ticks ahead through goal 200, 23 behind by goal 250).
                Route Cut(Route p)
                {
                    int k = 0;
                    while (k < p.Progress && p.GoalPhysics[k] <= best.GoalPhysics[k]) k++;
                    return k == 0 ? null : new Route { Inputs = p.Inputs.Take((int)Math.Min(p.GoalInput[k - 1], p.Inputs.Count)).ToList(), Progress = k,
                        GoalInput = p.GoalInput, GoalPhysics = p.GoalPhysics, GoalHeld = p.GoalHeld, EndReason = p.EndReason };
                }
                var ahead = Enumerable.Range(0, edits.Count).Where(i => (edits[i].Kind == "record" || edits[i].Kind == "payout" || edits[i].Kind == "ladder") && results[i].partial != null)
                    .Select(i => (i, p: Cut(results[i].partial))).Where(c => c.p != null && c.p.Progress < best.Progress && c.p.Progress >= edits[c.i].Last)
                    .OrderByDescending(c => c.p.Progress).ToList();
                foreach (var (i, p) in ahead)
                {
                    if (!ReferenceEquals(_best, best) || TimeUp) break;
                    Log($"auto goals: {edits[i].Text} is {best.GoalPhysics[p.Progress - 1] - p.GoalPhysics[p.Progress - 1]} ticks ahead at goal {p.Progress}, where its old way on stops; re-finding goals {p.Progress + 1}-{best.Progress}");
                    _repairFloorCpu = RecordRepairCpuPerGoal * (best.Progress - p.Progress);
                    try { Repair("goals: " + edits[i].Text, best, p); }
                    finally { _repairFloorCpu = 0; }
                }
            }
            // Inserted record laps are investments: the lap costs ticks and the way on starts elsewhere, so none plays through as
            // spliced. The two of each kind that get furthest (then soonest) are re-found under their goals, held to a CPU budget, not
            // to the best. So is an add that stops ahead of the best (its extra Cash buys a box early on the way on), and one in place
            // of a walk, from its laps' finish.
            if (!adopted && ReferenceEquals(_best, best) && !TimeUp) adopted = Reroute(edits, results, best);
            GoalEdit invested = null;
            var investments = Investments(edits, results, best);
            var waiting = new HashSet<GoalEdit>(investments.Select(x => x.e));
            if (!adopted && ReferenceEquals(_best, best))
                foreach (var (e, p, _) in investments.Where(x => x.rank < InvestTries))
                {
                    if (!ReferenceEquals(_best, best) || TimeUp) break;
                    waiting.Remove(e);
                    if (adopted = InvestRepair(e, p, best)) { invested = e; break; }
                }
            // An edit waiting for its investment isn't played out: the next pull offers it again (a drop adopted first makes the
            // same splice again: FalseEnding-Any%'s insert that drops the post-statue Course 2 laps was never re-found).
            for (int i = 0; i < edits.Count; i++) if (results[i] != default && !waiting.Contains(edits[i])) _editsTried.Add(EditKey(edits[i]));
            SaveMemory();
            GoalEdit made = invested ?? win.edit;
            string outcome = invested != null ? $"{invested.Text}, re-found: {Describe(_best)}"
                : win.route == null ? $"no edit of {edits.Count} beats the best" : $"{win.edit.Text}: {Describe(win.route)}";
            _goalsAgain = adopted;
            double cpu = CpuSeconds() - c0, wall = (_clock.Elapsed - w0).TotalSeconds;
            long gain = GainTicks(best);
            _order.Add("goals" + (gain > 0 ? $"(-{gain})" : ""));
            Log($"auto: pull {pull} goals: {outcome}; {(gain > 0 ? $"GAINED {gain} ticks" : "no gain")} in {cpu:0} CPU s, {wall:0} s; best {Describe(_best)}");
            AutoWrite(new { type = "pull", pull, lever = "goals", goal = made?.First ?? 0, label = "goals", budget = 0, outcome, gain, cpu = Math.Round(cpu, 1),
                wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), best = _best.LiveSeconds, score = double.NaN });
            if (gain > 0)
            {
                _curve.Add(((_clock.Elapsed - _autoWall0).TotalSeconds, CpuSeconds() - _autoCpu0, _best.Last, "goals", made?.First ?? 0));
                AutoWrite(new { type = "record", lever = "goals", goal = made?.First ?? 0, gain, time = _best.LiveSeconds, ticks = _best.Last,
                    wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), cpu = Math.Round(CpuSeconds() - _autoCpu0, 1),
                    splits = Enumerable.Range(1, _goals).Select(k => Split(_best.GoalPhysics, k)).ToArray(), goals = _labels });
            }
            Outlook();
            return adopted;
        }

        /// <summary>Runs work(i) for i in 0..count-1 on up to --threads threads, each with a simulation of its own (and its start
        /// and goal tracker as made), until the time is up.</summary>
        void RunOnThreads(int count, Action<Simulation, SimSnapshot, FullTasTracker, int> work)
        {
            Solve.Workers(_threads, count, () => { Simulation sim = _create(); return (sim, origin: sim.Start, tracker: sim.Goals); }, (s, i) =>
            {
                if (TimeUp) return;
                try { work(s.sim, s.origin, s.tracker, i); }
                catch (Exception ex) { Log($"auto goals: edit {i + 1} failed: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"); }
            })();
        }

        /// <summary>The goals of a label list: the run's own goal objects, and for a set of boxes the run buys one by one (buy:a+b),
        /// one goal for them all.</summary>
        FullTasGoal[] GoalsOf(string[] labels)
        {
            var byLabel = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in _runGoals.Goals) byLabel.TryAdd(g.label, g);
            return labels.Select(l => byLabel.TryGetValue(l, out FullTasGoal g) ? g : Grouped(l, byLabel)).ToArray();
        }

        static FullTasGoal Grouped(string label, Dictionary<string, FullTasGoal> byLabel)
        {
            string[] parts = label.Substring("buy:".Length).Split('+');
            var boxes = new List<string>();
            foreach (string p in parts)
            {
                if (!byLabel.TryGetValue("buy:" + p, out FullTasGoal g)) throw new InvalidOperationException($"goal {label}: the run buys no {p} alone");
                boxes.AddRange(g.boxes);
            }
            return new FullTasGoal { label = label, kind = FullTasGoal.Buy, boxes = boxes.ToArray() };
        }

        // ---------------------------------------------------------------- the edits

        /// <summary>Every lap of the best that starts with a quick restart after the goal before it and ends where a quick restart
        /// starts the next step (a lap cut there leaves the route as whole steps).</summary>
        List<RunLap> Laps(Route best)
        {
            var laps = new List<RunLap>();
            List<InputTick> inputs = best.Inputs;
            for (int k = 0; k + 1 < _goals; k++)
            {
                FullTasGoal s = _runGoals.Goals[k], f = _runGoals.Goals[k + 1];
                if (s.kind != FullTasGoal.Start || f.kind != FullTasGoal.Finish || s.course != f.course) continue;
                int a = (int)Math.Min(best.GoalInput[k], inputs.Count) - 1;
                while (a >= 0 && !inputs[a].reset) a--;
                if (a < 0 || k > 0 && a < best.GoalInput[k - 1]) continue;
                int b = (int)Math.Min(best.GoalInput[k + 1], inputs.Count);
                while (b < inputs.Count && !inputs[b].reset) b++;
                if (b >= inputs.Count || k + 2 < _goals && b >= best.GoalInput[k + 2]) continue;
                laps.Add(new RunLap { K = k, A = a, B = b, Course = s.course });
            }
            return laps;
        }

        /// <summary>The laps in runs of back-to-back laps of one course (each lap's gate goal right after the one before's finish).</summary>
        static List<List<RunLap>> Blocks(List<RunLap> laps)
        {
            var blocks = new List<List<RunLap>>();
            foreach (RunLap l in laps)
            {
                List<RunLap> last = blocks.Count == 0 ? null : blocks[blocks.Count - 1];
                if (last != null && last[last.Count - 1].K + 2 == l.K && last[last.Count - 1].Course == l.Course && last[last.Count - 1].B == l.A) last.Add(l);
                else blocks.Add(new List<RunLap> { l });
            }
            return blocks;
        }

        static string Plural(int n, string what) { return n == 1 ? "1 " + what : n + " " + what + "s"; }

        /// <summary>The best with laps dropped (their goals and their inputs from the quick restart before the gate to the next step's).</summary>
        GoalEdit Drop(Route best, List<RunLap> drop, string kind, string text)
        {
            return Spliced(best, drop.Select(l => (l.A, l.B, new List<InputTick>())).ToList(), drop.SelectMany(l => new[] { l.K, l.K + 1 }), kind,
                kind + ":" + string.Join(",", drop.Select(l => l.K)), text, drop.Min(l => l.K) + 1, drop.Max(l => l.K) + 2);
        }

        /// <summary>The best with stretches of its inputs replaced: old inputs [from, to) by <c>with</c> (empty: cut), the goals in
        /// <paramref name="cut"/> (0-based) taken off the list.</summary>
        GoalEdit Spliced(Route best, List<(int from, int to, List<InputTick> with)> ops, IEnumerable<int> cut, string kind, string key, string text, int first, int last)
        {
            var inputs = new List<InputTick>(best.Inputs.Count);
            int newAt = 0, oldAt = 0;
            long saves = 0;
            foreach (var (from, to, with) in ops.OrderBy(o => o.from))
            {
                inputs.AddRange(best.Inputs.GetRange(oldAt, from - oldAt));
                inputs.AddRange(with);
                newAt = inputs.Count;
                oldAt = to;
                saves += to - from - with.Count;
            }
            inputs.AddRange(best.Inputs.GetRange(oldAt, best.Inputs.Count - oldAt));
            var gone = new HashSet<int>(cut);
            int[] oldOf = Enumerable.Range(0, _goals).Where(k => !gone.Contains(k)).ToArray();
            return new GoalEdit
            {
                Kind = kind, Key = key, Text = text, Inputs = inputs, NewAt = newAt, OldAt = oldAt, OldOf = oldOf,
                Labels = oldOf.Select(k => _labels[k]).ToArray(), Saves = saves, First = first, Last = last,
            };
        }

        static string Goals(List<RunLap> laps)
        {
            int first = laps.Min(l => l.K) + 1, last = laps.Max(l => l.K) + 2;
            return laps.Count == 1 || laps.Zip(laps.Skip(1), (a, b) => b.K == a.K + 2).All(x => x) ? $"goals {first}-{last}"
                : "goals " + string.Join(", ", laps.Select(l => $"{l.K + 1}-{l.K + 2}"));
        }

        /// <summary>Drops: the last lap of each run of laps, and its last two.</summary>
        IEnumerable<GoalEdit> DropEdits(Route best, List<RunLap> laps)
        {
            foreach (List<RunLap> block in Blocks(laps))
                for (int m = 1; m <= Math.Min(2, block.Count); m++)
                {
                    List<RunLap> drop = block.Skip(block.Count - m).ToList();
                    yield return Drop(best, drop, "drop", $"Dropped {Plural(m, $"Course {drop[0].Course} lap")} ({Goals(drop)})");
                }
        }

        /// <summary>
        /// Adds: more laps of a course in place of a wait (standing waiting for Cash is worse than lapping: a lap earns its reward).
        /// n laps, a copy of the nearest lap before the wait (its quick restart to its finish), are put in right after that lap's
        /// finish (the player stands where a run of laps has it, so the copy plays as it did), and one stretch of idle ticks (20 or
        /// more) after it, up to the next lap goal of any course, is cut out: on the restart's near side or beyond it. n runs 1..N
        /// while the laps still take fewer ticks than the wait (n laps of L ticks, the wait W: n * L &lt; W; a run that takes as
        /// many ticks or more can't be shorter, and nothing else is adopted). FalseEnding-Any%: 112 idle ticks after Course 1's
        /// last lap (a 107-tick lap), before the quick restart to <c>buy:buywj</c>, N = 1; its other waits (Course 1's 25 ticks
        /// at goal 51, Course 3's 22-34 ticks between buys, for a Course 3 lap is 185-204) fit no lap. The laps earn their reward
        /// earlier, so the wait needs fewer ticks: <see cref="Played"/> puts back only what the goals after them miss. A wait after
        /// a later quick restart also gets the laps there (the restart lands at the course's start: after the statue's Course 2
        /// buys, a restart to the top by Course 3's gate). A buy the best walks to as its Cash comes (its spare Cash under its
        /// gap to the bound, <paramref name="rows"/>: FalseEnding-Any%'s climb to buy:buydj, 605 ticks, bound 145, 17 spare) waits
        /// by walking, not standing: the laps go in at the lap's finish (the buy right after it) or at the last quick restart
        /// before the buy, nothing is cut, and the walk is re-found after them as an investment (<see cref="GoalEdit.Refind"/>).
        /// </summary>
        IEnumerable<GoalEdit> AddEdits(Route best, List<GoalRow> rows)
        {
            const int MinIdle = 20;
            List<InputTick> all = best.Inputs;
            for (int k = 0; k + 1 < _goals; k++)
            {
                FullTasGoal s = _runGoals.Goals[k], f = _runGoals.Goals[k + 1];
                if (s.kind != FullTasGoal.Start || f.kind != FullTasGoal.Finish || s.course != f.course || k + 2 >= _goals) continue;
                // The lap: from the quick restart before its gate (after the goal before) to its finish.
                int a = (int)Math.Min(best.GoalInput[k], all.Count) - 1;
                while (a >= 0 && !all[a].reset) a--;
                int fin = (int)Math.Min(best.GoalInput[k + 1], all.Count);
                if (a < 0 || k > 0 && a < best.GoalInput[k - 1] || fin <= a) continue;
                int lap = fin - a;
                // The stretch after it, to the next goal that starts or finishes a lap.
                int stop = k + 2;
                while (stop < _goals && _runGoals.Goals[stop].kind != FullTasGoal.Start && _runGoals.Goals[stop].kind != FullTasGoal.Finish) stop++;
                int end = stop < _goals ? (int)Math.Min(best.GoalInput[stop], all.Count) : all.Count;
                // The last quick restart after the lap's finish and before input i (-1: none), and the goals reached before it.
                int RestartBefore(int i) { for (int r = i - 1; r > fin; r--) if (all[r].reset) return r; return -1; }
                int GoalsBefore(int r) { int g = k + 2; while (g < _goals && best.GoalInput[g] <= r) g++; return g; }
                for (int i = fin; i < end;)
                {
                    if (!Idle(all[i])) { i++; continue; }
                    int j = i;
                    while (j < end && Idle(all[j])) j++;
                    int r = RestartBefore(i);
                    if (j - i >= MinIdle)
                        for (int n = 1; n * lap < j - i; n++)
                        {
                            yield return AddLaps(best, k, a, fin, fin, k + 2, i, j, n);
                            if (r >= 0) yield return AddLaps(best, k, a, fin, r, GoalsBefore(r), i, j, n);
                        }
                    i = j;
                }
                for (int g = k + 2; rows != null && g < stop; g++)
                {
                    if (_runGoals.Goals[g].kind != FullTasGoal.Buy || rows[g].Spare >= rows[g].Gap) continue;
                    int at = g == k + 2 ? fin : RestartBefore((int)Math.Min(best.GoalInput[g], all.Count));
                    if (at < 0 || g > k + 2 && at < best.GoalInput[g - 1]) continue;
                    int g0 = at == fin ? k + 2 : GoalsBefore(at);
                    for (int n = 1; n * lap < rows[g].Gap; n++)
                    {
                        GoalEdit e = AddLaps(best, k, a, fin, at, g0, at, at, n);
                        e.Refind = g0 + 2 * n;
                        e.Key += ":walk";
                        e.Text = $"Added {Plural(n, $"Course {_runGoals.Goals[k].course} lap")} (a copy of goals {k + 1}-{k + 2}) at input {at} in place of the walk to goal {g + 1} "
                            + $"({_labels[g]}: {rows[g].Split} ticks, bound {rows[g].Bound}, {rows[g].Spare} of spare Cash; a lap is {lap} ticks)";
                        yield return e;
                    }
                }
            }
        }

        /// <summary>The best with n copies of the lap of goals K, K+1 (inputs [a, fin)) put in at input <paramref name="at"/> (its
        /// finish, or a quick restart after it: <paramref name="g0"/> goals come before) and the idle inputs [from, to) cut out.</summary>
        GoalEdit AddLaps(Route best, int k, int a, int fin, int at, int g0, int from, int to, int n)
        {
            List<InputTick> all = best.Inputs;
            int lap = fin - a;
            var inputs = new List<InputTick>(all.Count + n * lap - (to - from));
            inputs.AddRange(all.GetRange(0, at));
            for (int m = 0; m < n; m++) inputs.AddRange(all.GetRange(a, lap));
            inputs.AddRange(all.GetRange(at, from - at));
            inputs.AddRange(all.GetRange(to, all.Count - to));
            int[] oldOf = Enumerable.Range(0, g0).Concat(Enumerable.Repeat(-1, 2 * n)).Concat(Enumerable.Range(g0, _goals - g0)).ToArray();
            int before = g0;
            while (before + 1 < _goals && best.GoalInput[before] <= from) before++;
            return new GoalEdit
            {
                Kind = "add", Key = $"add:{k}:{from}:{n}" + (at == fin ? "" : $":{at}"), Inputs = inputs, NewAt = from + n * lap, OldAt = to, OldOf = oldOf,
                Labels = oldOf.Select((o, i) => _labels[o >= 0 ? o : k + (i - g0) % 2]).ToArray(), Saves = (to - from) - n * lap,
                First = k + 1, Last = k + 2,
                Text = $"Added {(n == 1 ? "a Course " + _runGoals.Goals[k].course + " lap" : n + " Course " + _runGoals.Goals[k].course + " laps")} after goal {g0}"
                    + (at == fin ? "" : $" (at the quick restart at input {at})") + $" in place of {to - from} idle ticks at input {from} "
                    + $"(before goal {before + 1}, {_labels[before]}; a lap is {lap} ticks)",
            };
        }

        /// <summary>An add's partial run is worth re-finding as an investment when it stops ahead of the best: the last goal it
        /// reached (an old goal, not one of the added laps') comes sooner than the best's there. Its extra Cash then buys a box on
        /// the way on that the old inputs only brushed (FalseEnding-Any%'s Course 1 lap after goal 85: 5 ticks ahead through goal
        /// 240, then a Course 3 stray buy).</summary>
        static bool AddAhead(GoalEdit e, Route partial, Route best)
        {
            int p = partial.Progress - 1;
            int og = p >= 0 ? e.OldOf[p] : -1;
            return og >= 0 && partial.GoalPhysics[p] < best.GoalPhysics[og];
        }

        /// <summary>The edits re-found as investments (<see cref="InvestRepair"/>), each with the run it starts from: an insert, an add
        /// that stops ahead of the best, a payout break that may pay, an add in place of a walk (from its laps' finish, played through
        /// or not), and with <paramref name="drops"/> (a replan's) a drop that stops ahead. Ranked within each kind (walk adds their
        /// own), furthest (then soonest) first; a pull re-finds the first InvestTries of each: from one pool of two, the two
        /// inserts took both and every other edit, marked played, was never re-found.</summary>
        List<(GoalEdit e, Route partial, int rank)> Investments(List<GoalEdit> edits, (Route route, Route partial, string why)[] results, Route best, bool drops = false)
        {
            var picks = new List<(GoalEdit e, Route p)>();
            for (int i = 0; i < edits.Count; i++)
            {
                GoalEdit e = edits[i];
                Route p = results[i].partial;
                if (e.Refind > 0)
                {
                    Route r = results[i].route ?? p;
                    if (r == null || r.Progress < e.Refind) continue;
                    p = new Route { Inputs = r.Inputs, Progress = e.Refind, GoalInput = r.GoalInput, GoalPhysics = r.GoalPhysics, GoalHeld = r.GoalHeld, EndReason = r.EndReason };
                }
                else if (results[i].route != null || p == null
                    || !(e.Kind == "insert" || (e.Kind == "add" || drops && e.Kind == "drop") && AddAhead(e, p, best) || (e.Kind == "payout" || e.Kind == "ladder") && PayoutMayPay(e, p, best))) continue;
                picks.Add((e, p));
            }
            return picks.GroupBy(x => (x.e.Kind, x.e.Refind > 0))
                .SelectMany(g => g.OrderByDescending(x => x.p.Progress).ThenBy(x => x.p.GoalPhysics[x.p.Progress - 1]).Select((x, rank) => (x.e, x.p, rank)))
                .OrderBy(x => x.rank).ThenByDescending(x => x.p.Progress).ThenBy(x => x.p.GoalPhysics[x.p.Progress - 1]).ToList();
        }

        static bool Idle(InputTick t) { return t.Equals(new InputTick { held = t.held }) && t.x == 0f && t.y == 0f; }

        /// <summary>A place is the buy table's when the best stands this near it (world units) as it reaches the place's goal.</summary>
        const float TablePlaceNear = 24f;

        /// <summary>
        /// Regroups (the buy planner's grouped buys, docs/fulltas.md "Buy table"): where the best buys box x (goal j) and then a and
        /// b one by one (goals j+1, j+2, each its own walk), the buy table's measured way from x's spot that buys both on one
        /// pass, as one goal `buy:a+b` (either order the table has: a reorder too). The best's inputs from the quick restart that
        /// starts the step after b follow. Needs the run's table (fulltas/&lt;run&gt;.buytable.json beside the trace).
        /// </summary>
        List<GoalEdit> RegroupEdits(Route best)
        {
            var edits = new List<GoalEdit>();
            string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".buytable.json");
            if (!File.Exists(file)) return edits;
            var places = new Dictionary<string, (string box, float x, float y)>();
            var entries = new List<(string place, string[] boxes, string technique, List<InputTick> inputs)>();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                foreach (JsonElement p in doc.RootElement.GetProperty("places").EnumerateArray())
                    if (p.GetProperty("box").ValueKind == JsonValueKind.String)
                        places[p.GetProperty("id").GetString()] = (p.GetProperty("box").GetString(), p.GetProperty("x").GetSingle(), p.GetProperty("y").GetSingle());
                foreach (JsonElement e in doc.RootElement.GetProperty("entries").EnumerateArray())
                {
                    string technique = e.GetProperty("technique").GetString();
                    string[] boxes = e.GetProperty("boxes").EnumerateArray().Select(b => b.GetString()).ToArray();
                    if (boxes.Length != 2 || technique == "miss" || technique == "capped" || !places.ContainsKey(e.GetProperty("place").GetString())
                        || e.GetProperty("inputs").ValueKind != JsonValueKind.String) continue;
                    entries.Add((e.GetProperty("place").GetString(), boxes, technique, Solve.DecodeTicks(e.GetProperty("inputs").GetString())));
                }
            }
            if (entries.Count == 0) return edits;
            // Where the best stands as it reaches each goal (one replay).
            var at = new WorldPos[_goals];
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            int progress = 0;
            _eval.Run(best.Inputs, null, null, 1000000, _ =>
            {
                for (; progress < _eval.Progress && progress < _goals; progress++) at[progress] = _eval.PlayerWorld;
            });
            bool Single(int k) => _runGoals.Goals[k].kind == FullTasGoal.Buy && !_labels[k].Contains('+') && FullTasGoal.IsEconomy(_labels[k]);
            for (int j = 0; j + 3 < _goals; j++)
            {
                if (!Single(j) || !Single(j + 1) || !Single(j + 2) || _labels[j + 1] == _labels[j + 2]) continue;
                int from = (int)Math.Min(best.GoalInput[j], best.Inputs.Count);
                int b = (int)Math.Min(best.GoalInput[j + 2], best.Inputs.Count);
                while (b < best.Inputs.Count && !best.Inputs[b].reset) b++;
                if (b >= best.GoalInput[j + 3]) continue;
                string a1 = _labels[j + 1], a2 = _labels[j + 2];
                foreach (var e in entries)
                {
                    var p = places[e.place];
                    if (p.box != _labels[j] || Math.Abs(p.x - at[j].x) > TablePlaceNear || Math.Abs(p.y - at[j].y) > TablePlaceNear) continue;
                    if (!(e.boxes[0] == a1 && e.boxes[1] == a2 || e.boxes[0] == a2 && e.boxes[1] == a1)) continue;
                    string grouped = "buy:" + e.boxes[0].Substring(4) + "+" + e.boxes[1].Substring(4);
                    var inputs = best.Inputs.Take(from).Concat(e.inputs).Concat(best.Inputs.Skip(b)).ToList();
                    int[] oldOf = Enumerable.Range(0, j + 1).Concat(new[] { j + 2 }).Concat(Enumerable.Range(j + 3, _goals - j - 3)).ToArray();
                    edits.Add(new GoalEdit
                    {
                        Kind = "regroup", Key = $"regroup:{j}:{e.place}:{grouped}", Inputs = inputs, NewAt = from + e.inputs.Count, OldAt = b, OldOf = oldOf,
                        Labels = _labels.Take(j + 1).Append(grouped).Concat(_labels.Skip(j + 3)).ToArray(), Saves = b - from - e.inputs.Count,
                        First = j + 2, Last = j + 3,
                        Text = $"Grouped goals {j + 2}-{j + 3} as {grouped} ({e.technique} from {_labels[j]}'s spot, the buy table's {e.inputs.Count} ticks)",
                    });
                }
            }
            return edits;
        }

        /// <summary>
        /// Record laps: a lap of the best replaced by a quick restart, a few idle ticks and a faster lap played from standing at the
        /// course's reset point, when that lap leaves the course a faster best time: the clones' path, so every clone pays more from
        /// then on. The laps played there: the course's saved routes (offline-best, its judged routes included) and the best's own laps
        /// of the course. Costs ticks once; pays when the laps the extra income makes unneeded go. Each is offered alone, with the same
        /// lap in place of the slower laps right after it (the rest of that visit's back-to-back laps), and each of those with the last
        /// 1-3 laps of the course's run of laps before the stretch's next unlock or area goal dropped. Tried at the course's last lap
        /// before its first clone buy, its first lap after one, its first lap after each unlock buy (new unlocks: a faster way) and
        /// every lap the best walks into (no quick restart before the gate: the walk is kept to the gate, then a quick restart;
        /// FalseEnding-Any%'s first Course 2 lap with the dash walked in from the boxes in 103 ticks, where a lap from the restart
        /// takes 84). A course and unlock set with no lap saved from the restart gets one searched first (--record-search-minutes,
        /// once per run: <see cref="SearchRecordLap"/>).
        /// </summary>
        List<GoalEdit> RecordEdits(Route best, List<RunLap> laps)
        {
            var edits = new List<GoalEdit>();
            LoadCourseRoutes();
            List<RunLap> walkIns = WalkInLaps(best);
            List<RunLap> all = laps.Concat(walkIns).OrderBy(l => l.K).ThenBy(l => l.A).ToList();
            var spots = new List<RunLap>();
            void Spot(RunLap l) { if (l != null && !spots.Contains(l)) spots.Add(l); }
            int[] unlocks = Enumerable.Range(0, _goals).Where(k => _runGoals.Goals[k].kind == FullTasGoal.Buy && !FullTasGoal.IsEconomy(_labels[k])).ToArray();
            foreach (int course in all.Select(l => l.Course).Distinct())
            {
                string clone = course == 1 ? "buyclone" : "buyclone" + course;
                int firstClone = Array.FindIndex(_labels, l => l.StartsWith("buy:", StringComparison.Ordinal) && l.Substring(4).Split('+').Contains(clone));
                Spot(laps.LastOrDefault(l => l.Course == course && (firstClone < 0 || l.K < firstClone)));
                if (firstClone >= 0) Spot(laps.FirstOrDefault(l => l.Course == course && l.K > firstClone));
                foreach (int u in unlocks)
                {
                    RunLap next = all.FirstOrDefault(l => l.Course == course && l.K > u);
                    if (next != null) foreach (RunLap l in all.Where(l => l.K == next.K)) Spot(l);
                }
            }
            foreach (RunLap l in walkIns) Spot(l);
            if (spots.Count == 0) return edits;
            Dictionary<int, List<(string name, List<InputTick> inputs)>> own = all.Select(l => l.Course).Distinct().ToDictionary(c => c, c => OwnLaps(best, c));
            var found = new List<(List<InputTick> lap, int fin, float path, long tick, string how)>[spots.Count];
            var why = new string[spots.Count];
            var capsAt = new Caps?[spots.Count];
            void Play(List<int> which) => RunOnThreads(which.Count, (sim, origin, tracker, j) =>
            {
                int i = which[j];
                sim.UseGoals(tracker.For(_runGoals.Goals));
                found[i] = RecordLapOf(sim, origin, best, spots[i], own[spots[i].Course], out capsAt[i], out why[i]);
            });
            Play(Enumerable.Range(0, spots.Count).ToList());
            // A course and unlock set with no lap saved from the quick restart: one searched (from its earliest spot), then its spots
            // played again with it. The search's file is the run's from then on (offline-best), so this happens once per unlock set;
            // the memory keeps the ones that found nothing.
            if (_recordSearchMinutes > 0)
            {
                var again = new List<int>();
                foreach (var key in Enumerable.Range(0, spots.Count).Where(i => capsAt[i] != null).GroupBy(i => (course: spots[i].Course, caps: capsAt[i].Value.Id)))
                {
                    string id = $"{key.Key.course}_{key.Key.caps}";
                    if (TimeUp) break;
                    if (_lapSearched.Contains(id) || _courseRoutes.Any(r => r.course == key.Key.course && r.caps.Id == key.Key.caps && r.name.Contains("-restart"))) continue;
                    _lapSearched.Add(id);
                    RunLap from = spots[key.OrderBy(i => spots[i].K).ThenBy(i => spots[i].A).First()];
                    TimeSpan until = _clock.Elapsed + TimeSpan.FromMinutes(_recordSearchMinutes);
                    if (!SearchRecordLap(from, best, () => TimeUp || _clock.Elapsed >= until, false)) continue;
                    _courseRoutes = null;
                    LoadCourseRoutes();
                    again.AddRange(key);
                }
                SaveMemory();
                if (again.Count > 0) Play(again);
            }
            for (int i = 0; i < spots.Count; i++)
                if (found[i] == null)
                    Log($"auto goals: no record lap at goals {spots[i].K + 1}-{spots[i].K + 2}{(spots[i].WalkIn ? $" (walked in, a quick restart at input {spots[i].A})" : "")} (Course {spots[i].Course}): {why[i] ?? "not played"}");
            // A walk-in lap is tried with its restart at each of a few inputs after the gate: of all of them, the fastest lap and the
            // soonest finish.
            var keep = new List<(int i, (List<InputTick> lap, int fin, float path, long tick, string how) way)>();
            foreach (var g in Enumerable.Range(0, spots.Count).Where(i => found[i] != null).GroupBy(i => spots[i].K))
            {
                keep.AddRange(g.Where(i => !spots[i].WalkIn).SelectMany(i => found[i].Select(w => (i, w))));
                var walked = g.Where(i => spots[i].WalkIn).SelectMany(i => found[i].Select(w => (i, w))).ToList();
                if (walked.Count == 0) continue;
                var fastest = walked.OrderBy(x => x.w.path).ThenBy(x => x.w.tick).First();
                var soonest = walked.OrderBy(x => x.w.tick).ThenBy(x => x.w.path).First();
                keep.Add(fastest);
                if (soonest.w.tick < fastest.w.tick) keep.Add(soonest);
            }
            List<List<RunLap>> blocks = Blocks(laps);
            foreach (var (i, way) in keep)
            {
                RunLap l = spots[i];
                var (lapInputs, _, _, _, how) = way;
                int oldAfter = (int)Math.Min(best.GoalInput[l.K + 1], best.Inputs.Count);
                string text = $"Record Course {l.Course} lap at goals {l.K + 1}-{l.K + 2}{(l.WalkIn ? " (walked in: a quick restart at the gate)" : "")} ({how})";
                // The same lap in place of each slower lap right after it (back to back, same course).
                var follow = new List<(int, int, List<InputTick>)>();
                var followed = new List<RunLap>();
                for (RunLap cur = l, f; (f = laps.FirstOrDefault(x => x.Course == l.Course && x.K == cur.K + 2)) != null; cur = f)
                {
                    int to = (int)Math.Min(best.GoalInput[f.K + 1], best.Inputs.Count);
                    if (lapInputs.Count >= to - f.A) continue;
                    follow.Add((f.A, to, lapInputs));
                    followed.Add(f);
                }
                // The course's last run of laps before the next goal that isn't economy (an unlock buy, an area or reward goal).
                int stop = Enumerable.Range(l.K + 2, _goals - l.K - 2).FirstOrDefault(k => !FullTasGoal.IsEconomy(_labels[k]), _goals);
                foreach (bool withFollow in followed.Count == 0 ? new[] { false } : new[] { false, true })
                {
                    var ops = new List<(int, int, List<InputTick>)> { (l.A, oldAfter, lapInputs) };
                    if (withFollow) ops.AddRange(follow);
                    int lastK = withFollow ? followed[followed.Count - 1].K : l.K;
                    string t = withFollow ? $"{text}; the same lap at {Goals(followed)}" : text;
                    string key = $"record:{l.K}:{l.A}" + (withFollow ? ":f" + followed.Count : "");
                    List<RunLap> block = blocks.LastOrDefault(b => b[0].Course == l.Course && b[0].K > lastK + 1 && b[b.Count - 1].K + 1 < stop);
                    for (int m = 0; m <= Math.Min(3, block?.Count ?? 0); m++)
                    {
                        List<RunLap> drop = m == 0 ? new List<RunLap>() : block.Skip(block.Count - m).ToList();
                        edits.Add(Spliced(best, ops.Concat(drop.Select(d => (d.A, d.B, new List<InputTick>()))).ToList(), drop.SelectMany(d => new[] { d.K, d.K + 1 }), "record",
                            m == 0 ? key : key + ":" + string.Join(",", drop.Select(d => d.K)),
                            m == 0 ? t : $"{t}; dropped {Plural(m, $"Course {l.Course} lap")} ({Goals(drop)})", l.K + 1, m == 0 ? lastK + 2 : drop.Max(d => d.K) + 2));
                    }
                }
            }
            return edits;
        }

        /// <summary>
        /// Inserts: a record lap right after an unlock buy, as start:N finish:N goals after it: a quick restart as the buy is made,
        /// 0-40 idle ticks and the course's lap there (<see cref="InsertLapOf"/>) when it beats the course's best time. The clones
        /// then run that path through everything after (FalseEnding-Any%: Course 2's lap with the dash right after buy:buyds,
        /// before the run leaves Course 2, not on the drop back after the statue). Offered alone and with the course's next visit's
        /// laps dropped (its back-to-back laps, walked-in or not, first after the next unlock buy: with the record path set, the
        /// return there only buys; the quick restart that ended that visit is kept, so the run goes on from where it lands). The
        /// best's way on starts from the buy's box, not the lap's finish, so an insert is re-found as an investment
        /// (<see cref="InvestRepair"/>). Tried for every course the run laps after the buy.
        /// </summary>
        List<GoalEdit> InsertEdits(Route best, List<RunLap> laps)
        {
            var edits = new List<GoalEdit>();
            LoadCourseRoutes();
            int[] unlocks = Enumerable.Range(0, _goals - 1).Where(k => _runGoals.Goals[k].kind == FullTasGoal.Buy && !FullTasGoal.IsEconomy(_labels[k])).ToArray();
            // Every lap of the run, a walked-in one once (its restart at the gate).
            List<RunLap> all = laps.Concat(WalkInLaps(best).GroupBy(l => l.K).Select(g => g.First())).OrderBy(l => l.K).ToList();
            var specs = unlocks.SelectMany(k => all.Where(l => l.K > k).Select(l => l.Course).Distinct().Select(c => (k, c))).ToList();
            if (specs.Count == 0) return edits;
            Dictionary<int, List<(string name, List<InputTick> inputs)>> own = all.Select(l => l.Course).Distinct().ToDictionary(c => c, c => OwnLaps(best, c));
            string[] Labels((int k, int c) s) => _labels.Take(s.k + 1).Concat(new[] { $"start:{s.c}", $"finish:{s.c}" }).Concat(_labels.Skip(s.k + 1)).ToArray();
            var found = new List<(List<InputTick> lap, int fin, float path, long tick, string how)>[specs.Count];
            var why = new string[specs.Count];
            RunOnThreads(specs.Count, (sim, origin, tracker, i) =>
            {
                sim.UseGoals(tracker.For(GoalsOf(Labels(specs[i]))));
                found[i] = InsertLapOf(sim, origin, best, specs[i].k, specs[i].c, own[specs[i].c], out why[i]);
            });
            for (int i = 0; i < specs.Count; i++)
            {
                var (k, c) = specs[i];
                if (found[i] == null)
                {
                    Log($"auto goals: no Course {c} lap to insert after goal {k + 1} ({_labels[k]}): {why[i] ?? "not played"}");
                    continue;
                }
                int head = (int)Math.Min(best.GoalInput[k], best.Inputs.Count);
                // The course's next visit: its first laps after the next unlock buy, back to back (the last one with or without a quick
                // restart after it: FalseEnding-Any%'s second Course 2 lap after the statue climbs to buy:buydj from its finish).
                int next = unlocks.FirstOrDefault(u => u > k, -1);
                var visit = new List<RunLap>();
                for (RunLap l = next < 0 ? null : all.FirstOrDefault(x => x.Course == c && x.K > next); l != null;
                    l = all.FirstOrDefault(x => x.Course == c && x.K == l.K + 2) ?? (RestartLap(best, l.K + 2) is RunLap r && r.Course == c ? r : null))
                    visit.Add(l);
                foreach (var way in found[i])
                    foreach (bool drop in visit.Count == 0 ? new[] { false } : new[] { false, true })
                    {
                        var ops = new List<(int, int, List<InputTick>)> { (head, head, way.lap) };
                        var cut = new List<int>();
                        if (drop)
                        {
                            // From the goal before the visit's first gate to its end: the quick restart that ended it, or a quick
                            // restart in place of its last lap's finish (FalseEnding-Any%: after the statue's Course 2 buys the
                            // restart lands at the top, by the shaft to buy:buydj, not at Course 2's gate).
                            RunLap last = visit[visit.Count - 1];
                            int from = (int)Math.Min(best.GoalInput[visit[0].K - 1], best.Inputs.Count);
                            ops.Add(last.B >= 0 ? (from, last.B, new List<InputTick>())
                                : (from, (int)Math.Min(best.GoalInput[last.K + 1], best.Inputs.Count), new List<InputTick> { new InputTick { reset = true } }));
                            cut.AddRange(visit.SelectMany(l => new[] { l.K, l.K + 1 }));
                        }
                        string text = $"Inserted a Course {c} record lap after goal {k + 1} ({_labels[k]}; {way.how})"
                            + (drop ? $"; dropped its next visit's {Plural(visit.Count, "lap")} ({Goals(visit)})" : "");
                        GoalEdit e = Spliced(best, ops, cut, "insert", $"insert:{k}:{c}:{way.tick}" + (drop ? ":drop" : ""), text, k + 2, k + 3);
                        // The two new goals go right after goal k.
                        int at = Array.IndexOf(e.OldOf, k) + 1;
                        e.OldOf = e.OldOf.Take(at).Concat(new[] { -1, -1 }).Concat(e.OldOf.Skip(at)).ToArray();
                        e.Labels = e.OldOf.Select((o, j) => o >= 0 ? _labels[o] : j == at ? $"start:{c}" : $"finish:{c}").ToArray();
                        edits.Add(e);
                    }
            }
            return edits;
        }

        /// <summary>The laps the best walks into: no quick restart between the goal before and the gate (start:N), then the finish
        /// (finish:N). Each is offered with a quick restart 0..WalkInRestarts-1 inputs after the walk touches the gate.</summary>
        List<RunLap> WalkInLaps(Route best)
        {
            var laps = new List<RunLap>();
            List<InputTick> inputs = best.Inputs;
            for (int k = 1; k + 1 < _goals; k++)
            {
                FullTasGoal s = _runGoals.Goals[k], f = _runGoals.Goals[k + 1];
                if (s.kind != FullTasGoal.Start || f.kind != FullTasGoal.Finish || s.course != f.course) continue;
                int from = (int)Math.Min(best.GoalInput[k - 1], inputs.Count), gate = (int)Math.Min(best.GoalInput[k], inputs.Count);
                int fin = (int)Math.Min(best.GoalInput[k + 1], inputs.Count);
                if (inputs.GetRange(from, gate - from).Any(t => t.reset)) continue;
                int b = fin;
                while (b < inputs.Count && !inputs[b].reset) b++;
                if (b >= inputs.Count || k + 2 < _goals && b >= best.GoalInput[k + 2]) b = -1;
                for (int d = 0; d < WalkInRestarts && gate + d < fin; d++)
                    laps.Add(new RunLap { K = k, A = gate + d, B = b, Course = s.course, WalkIn = true });
            }
            return laps;
        }

        /// <summary>The best's own laps of a course from their quick restart (the inputs after it, through the finish goal's), the
        /// OwnLapRoutes shortest distinct ones.</summary>
        List<(string name, List<InputTick> inputs)> OwnLaps(Route best, int course)
        {
            var seen = new HashSet<ulong>();
            var laps = new List<(string, List<InputTick>)>();
            foreach (RunLap l in Laps(best).Where(l => l.Course == course).OrderBy(l => best.GoalInput[l.K + 1] - l.A))
            {
                List<InputTick> lap = best.Inputs.GetRange(l.A + 1, (int)Math.Min(best.GoalInput[l.K + 1], best.Inputs.Count) - l.A - 1);
                if (!seen.Add(SnapshotIndex.Hash(lap, lap.Count))) continue;
                laps.Add(($"the best's lap at goals {l.K + 1}-{l.K + 2}", lap));
                if (laps.Count == OwnLapRoutes) break;
            }
            return laps;
        }

        /// <summary>The saved course routes (offline-best and offline-best/judged beside the trace: &lt;course&gt;_&lt;caps&gt;-&lt;time&gt;[-restart][-human].json,
        /// -restart for a lap <see cref="RecordLapSearch"/> found from a run's quick-restart landing),
        /// the fastest RecordRoutes per course and unlock set.</summary>
        void LoadCourseRoutes()
        {
            if (_courseRoutes != null) return;
            _courseRoutes = new List<(int, Caps, string, List<InputTick>)>();
            var name = new Regex(@"^(\d+)_([a-z0-9]+)-(\d+(?:\.\d+)?)(-restart)?(-human)?\.json$");
            string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "offline-best");
            var files = new List<(int course, Caps caps, double time, string path)>();
            foreach (string dir in new[] { root, Path.Combine(root, "judged") }.Where(Directory.Exists))
                foreach (string f in Directory.GetFiles(dir, "*.json"))
                {
                    Match m = name.Match(Path.GetFileName(f));
                    if (!m.Success || !Caps.TryParse(m.Groups[2].Value, out Caps caps)) continue;
                    files.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), caps, double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), f));
                }
            // Fastest first across unlock sets: a record lap stops after RecordReached routes that play from the restart.
            foreach (var f in files.GroupBy(f => (f.course, f.caps.Id))
                .SelectMany(g => g.OrderBy(f => f.time).GroupBy(f => Path.GetFileName(f.path)).Select(x => x.First()).Take(RecordRoutes)).OrderBy(f => f.time))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f.path), new JsonDocumentOptions { MaxDepth = 256 });
                        if (!doc.RootElement.TryGetProperty("ticks", out JsonElement ticks)) continue;
                        _courseRoutes.Add((f.course, f.caps, Path.GetFileNameWithoutExtension(f.path), ticks.EnumerateArray().Select(InputTick.FromJson).ToList()));
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException) { }
                }
            if (_courseRoutes.Count > 0)
                Log($"auto goals: {_courseRoutes.Count} saved course routes for record laps ({string.Join(", ", _courseRoutes.GroupBy(r => r.course).Select(g => $"Course {g.Key}: {g.Count()}"))})");
        }

        /// <summary>
        /// Lap l replayed as a quick restart, 0-MaxRecordPad idle ticks and each lap it can play there (the first pad that reaches the
        /// lap's finish): the saved routes of its course whose unlocks the player has (fastest first, until RecordReached play), then
        /// the best's own laps of the course (<paramref name="own"/>: a walk-in lap is often the slowest of its visit). When one beats
        /// the course's best time after the best's own lap: the one that leaves it fastest (then the soonest finish), and the one that
        /// finishes soonest while still beating it (a faster time can cost ticks: FalseEnding-Any%'s 1.50 s Course 2 lap from the
        /// restart finishes 9 ticks after its 1.68 s one). Each is the lap's inputs (from the restart to its finish goal's input);
        /// null when none beats it. <paramref name="caps"/> is the player's unlock set there (null when the best doesn't get there).
        /// </summary>
        List<(List<InputTick> lap, int fin, float path, long tick, string how)> RecordLapOf(Simulation sim, SimSnapshot origin, Route best, RunLap l,
            List<(string name, List<InputTick> inputs)> own, out Caps? caps, out string why)
        {
            int finishGoal = l.K + 2;
            why = null;
            caps = null;
            sim.SetStart(origin, 0);
            sim.Goals.Limit = finishGoal;
            SimSnapshot at = sim.Advance(best.Inputs.GetRange(0, l.A));
            if (at == null) { why = $"the best ends before input {l.A}"; return null; }
            sim.Restore(at);
            Caps has = PlayerCaps.Of(sim.Player);
            caps = has;
            float Path() => (float)BestPathTime.GetValue(sim.Courses.First(c => c.courseNumber == l.Course));
            sim.SetStart(at, l.A);
            RunResult old = sim.Run(best.Inputs.GetRange(l.A, (int)best.GoalInput[l.K + 1] - l.A), null, null, 400);
            if (old.Progress < finishGoal) { why = $"the best's own lap replays to goal {old.Progress} ({old.EndReason})"; return null; }
            float before = Path();
            InputTick restart = l.WalkIn ? new InputTick { reset = true } : best.Inputs[l.A];
            var tried = new List<string>();
            var ways = LapWays(sim, at, l.A, restart, finishGoal, l.Course, has, own, before, tried);
            if (ways != null) return ways;
            why = $"best time {before:0.00}s ({has.Label}); " + (tried.Count == 0 ? "no lap of these unlocks to play" : string.Join("; ", tried));
            return null;
        }

        /// <summary>
        /// From state <paramref name="at"/> (input <paramref name="a"/>): the restart tick, 0-MaxRecordPad idle ticks and each lap of
        /// the course (the first pad that reaches <paramref name="finishGoal"/>): the saved routes whose unlocks the player has (fastest
        /// first, until RecordReached play), then <paramref name="own"/>. When one leaves the course's best time under
        /// <paramref name="before"/>: the fastest (then the soonest finish), and the soonest finish that still beats it; else null.
        /// <paramref name="lapTime"/>: each lap's path is its own course time, not the best time it leaves (a ladder's rungs).
        /// </summary>
        List<(List<InputTick> lap, int fin, float path, long tick, string how)> LapWays(Simulation sim, SimSnapshot at, int a, InputTick restart, int finishGoal,
            int course, Caps has, List<(string name, List<InputTick> inputs)> own, float before, List<string> tried, bool lapTime = false)
        {
            float Path() => (float)BestPathTime.GetValue(sim.Courses.First(c => c.courseNumber == course));
            (List<InputTick> lap, int fin, float path, long tick, string how)? pick = null, soon = null;
            bool Try(string name, List<InputTick> route)
            {
                string end = null;
                // Pad -1: the route's first tick rides on the restart's (its stick only). A saved route's start stands one settling
                // tick behind a run's restart landing (Course 2's 2.20 s route dies on its spikes from pad 0, plays from here).
                InputTick first = route[0], merged = restart;
                merged.x = first.x;
                merged.y = first.y;
                bool stickOnly = first.Equals(new InputTick { x = first.x, y = first.y });
                bool reached = false;
                for (int pad = stickOnly ? -1 : 0; pad <= MaxRecordPad; pad++)
                {
                    var lap = new List<InputTick>(1 + Math.Max(0, pad) + route.Count) { pad < 0 ? merged : restart };
                    for (int i = 0; i < pad; i++) lap.Add(new InputTick());
                    lap.AddRange(pad < 0 ? route.Skip(1) : route);
                    sim.SetStart(at, a);
                    sim.Goals.Limit = finishGoal;
                    RunResult rr = sim.Run(lap, null, null, 400);
                    if (rr.Progress < finishGoal) { end ??= $"{rr.Progress - (finishGoal - 2)} of the lap's 2 goals ({rr.EndReason})"; continue; }
                    int fin = (int)rr.GoalInputTicks[finishGoal - 1];
                    float path = lapTime ? sim.LastCourseTime : Path();
                    end = $"{path:0.00}s after {pad} idle ticks";
                    reached = true;
                    long tick = rr.GoalPhysicsTicks[finishGoal - 1];
                    // The finish can come in the idle ticks the run plays after the lap's inputs (a dash carries it over the line).
                    while (lap.Count < fin - a) lap.Add(new InputTick());
                    var way = (lap.GetRange(0, fin - a), fin, path, tick, $"{name} after {pad} idle ticks, best time {before:0.00}s -> {path:0.00}s");
                    if (pick == null || path < pick.Value.path || path == pick.Value.path && tick < pick.Value.tick) pick = way;
                    if (path < before && (soon == null || tick < soon.Value.tick || tick == soon.Value.tick && path < soon.Value.path)) soon = way;
                    break;
                }
                tried.Add($"{name}: {end}");
                return reached;
            }
            int reachedSaved = 0;
            // The saved routes come fastest first: the first few that play set the best time a record lap can.
            foreach (var r in _courseRoutes.Where(r => r.course == course && r.caps.IsSubsetOf(has)))
                if (Try(r.name, r.inputs) && ++reachedSaved == RecordReached) break;
            foreach (var (name, inputs) in own) if (inputs.Count > 0) Try(name, inputs);
            if (pick == null || pick.Value.path >= before) return null;
            var ways = new List<(List<InputTick>, int, float, long, string)> { pick.Value };
            if (soon.Value.tick < pick.Value.tick) ways.Add(soon.Value);
            return ways;
        }

        /// <summary>
        /// A lap after goal k (0-based) under the edited goals (start:N, finish:N right after it): a quick restart as the best reaches
        /// goal k, then <see cref="LapWays"/> against the course's best time there. Null (and why) when the best doesn't get there or
        /// no lap of the course finishes from where the restart lands faster than its best time.
        /// </summary>
        List<(List<InputTick> lap, int fin, float path, long tick, string how)> InsertLapOf(Simulation sim, SimSnapshot origin, Route best, int k, int course,
            List<(string name, List<InputTick> inputs)> own, out string why)
        {
            why = null;
            int head = (int)Math.Min(best.GoalInput[k], best.Inputs.Count);
            sim.SetStart(origin, 0);
            sim.Goals.Limit = k + 3;
            SimSnapshot at = sim.Advance(best.Inputs.GetRange(0, head));
            if (at == null) { why = $"the best ends before input {head}"; return null; }
            sim.Restore(at);
            Caps has = PlayerCaps.Of(sim.Player);
            float before = (float)BestPathTime.GetValue(sim.Courses.First(c => c.courseNumber == course));
            var tried = new List<string>();
            var ways = LapWays(sim, at, head, new InputTick { reset = true }, k + 3, course, has, own, before, tried);
            if (ways == null) why = $"best time {before:0.00}s ({has.Label}); " + (tried.Count == 0 ? "no lap of these unlocks to play" : string.Join("; ", tried));
            return ways;
        }

        /// <summary>--record-lap K: <see cref="SearchRecordLap"/> for the lap whose finish is goal K (from its quick restart, or for
        /// a lap the best walks into from a quick restart as the walk touches the gate), for --minutes on every thread, and its
        /// human version.</summary>
        void RecordLapSearch(int finish)
        {
            int k = finish - 2;
            RunLap l = k < 0 || finish > _goals ? null
                : Laps(_best).FirstOrDefault(x => x.K == k) ?? RestartLap(_best, k) ?? WalkInLaps(_best).FirstOrDefault(x => x.K == k);
            if (l == null)
                throw new ArgumentException($"--record-lap {finish}: goal {finish} isn't the finish of a lap (a start and a finish goal, from a quick restart or walked into) of the best");
            SearchRecordLap(l, _best, () => TimeUp, true);
        }

        /// <summary>A lap of the best from a quick restart after the goal before it, with or without a quick restart after its finish
        /// (B = -1: none), or null.</summary>
        RunLap RestartLap(Route best, int k)
        {
            if (k + 1 >= _goals || _runGoals.Goals[k].kind != FullTasGoal.Start || _runGoals.Goals[k + 1].kind != FullTasGoal.Finish
                || _runGoals.Goals[k].course != _runGoals.Goals[k + 1].course) return null;
            int a = (int)Math.Min(best.GoalInput[k], best.Inputs.Count) - 1;
            while (a >= 0 && !best.Inputs[a].reset) a--;
            if (a < 0 || k > 0 && a < best.GoalInput[k - 1]) return null;
            return new RunLap { K = k, A = a, B = -1, Course = _runGoals.Goals[k].course };
        }

        /// <summary>
        /// A search for lap l's lap from where its quick restart lands, scored on the course timer, until <paramref name="stop"/>.
        /// The search runs without the run's goals, so the solver judges it as a course solve does (the course's path time) and not
        /// by the goal's tick: a lap that crosses the gate later and faster wins. Seeded with the course's saved routes of the unlocks
        /// the player has there (as saved and without their first tick) and the best's own laps of the course. The fastest lap is
        /// written to offline-best beside the trace as &lt;course&gt;_&lt;caps&gt;-&lt;time&gt;-restart.json (with its human version when
        /// <paramref name="humanize"/>), which record laps then play after the restart (pad 0). Returns whether a lap finished.
        /// </summary>
        bool SearchRecordLap(RunLap l, Route best, Func<bool> stop, bool humanize)
        {
            List<InputTick> prefix = best.Inputs.GetRange(0, l.A);
            prefix.Add(l.WalkIn ? new InputTick { reset = true } : best.Inputs[l.A]);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot there = _eval.Advance(prefix);
            if (there == null)
            {
                Log($"record lap at goals {l.K + 1}-{l.K + 2}: the best with a quick restart at input {l.A} ends before it; no search");
                return false;
            }
            _eval.Restore(there);
            string caps = PlayerCaps.Of(_eval.Player).Id;
            LoadCourseRoutes();
            var seeds = _courseRoutes.Where(r => r.course == l.Course && r.caps.IsSubsetOf(Caps.Parse(caps)))
                .SelectMany(r => new[] { r.inputs, r.inputs.Skip(1).ToList() }).ToList();
            seeds.AddRange(OwnLaps(best, l.Course).Select(o => o.inputs));
            string title = $"record lap {l.K + 1}-{l.K + 2}";
            // Each worker replays the prefix in its own world (a snapshot is its world's).
            Simulation Create()
            {
                (Simulation sim, SimSnapshot at) = Prefixed(prefix, title);
                sim.SetStart(at, prefix.Count);
                sim.UseGoals(null);
                sim.TargetCourse = l.Course;
                return sim;
            }
            var solver = new OfflineSolver(Create, caps, seeds);
            (float time, long runTicks, List<InputTick> inputs, string how)? found = null;
            var guard = new object();
            solver.Improved += (time, runTicks, inputs, how) =>
            {
                lock (guard) found = (time, runTicks, inputs, how);
                Log($"record lap at goals {l.K + 1}-{l.K + 2} (Course {l.Course}, {caps}): {time:0.00}s via {how}, finish at tick {runTicks}");
            };
            Log($"record lap at goals {l.K + 1}-{l.K + 2}: searching Course {l.Course} ({caps}) from the {(l.WalkIn ? "quick restart at the gate" : "restart")} at input {l.A} on {_threads} threads, {seeds.Count} seeds");
            Thread[] threads = solver.Start(_threads);
            while (!stop()) Thread.Sleep(250);
            solver.Stop();
            foreach (Thread t in threads) t.Join(60000);
            if (found == null) { Log($"record lap at goals {l.K + 1}-{l.K + 2}: no lap finished"); return false; }
            var (time, ticks, lap, _) = found.Value;
            // The solver keeps the inputs it was given; a finish in the idle ticks after them needs those ticks written too, as a
            // record lap cuts its route at the finish's input. (Replayed here, not in Improved: a simulation made on a solver
            // worker's thread takes over that thread's game hooks from the worker's own.)
            List<InputTick> idled = lap.Concat(Enumerable.Repeat(new InputTick(), 60)).ToList();
            RunResult rr = Create().Run(idled, null, null, 0);
            if (rr.Finished) lap = idled.GetRange(0, rr.FinishTick + 1);
            string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "offline-best");
            Directory.CreateDirectory(outDir);
            string file = Path.Combine(outDir, $"{l.Course}_{caps}-{time.ToString("0.00", CultureInfo.InvariantCulture)}-restart.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { course = l.Course, capabilities = caps, time, runTicks = ticks, ticks = lap.Select(t => t.ToJson()) }));
            Log($"record lap at goals {l.K + 1}-{l.K + 2}: {time:0.00}s, {lap.Count} inputs after the restart -> {Path.GetFileName(file)}");
            if (_submit) SubmitCourseLap(l.Course, caps, time, lap);
            if (humanize)
            {
                var human = new Humanize.Background(Create, null, l.Course, caps, _threads, line => Log("record lap: " + line));
                human.Set(file, lap);
                human.Finish();
            }
            return true;
        }

        /// <summary>
        /// A lap found from the run's quick restart is a course route too: the restart lands where the category's quick restart
        /// starts it (FalseEnding-Any%'s 2_wjds-1.50-restart was saved as 2_WJ_DS 1.50 over the 1.62 record, as it is). The game
        /// judges it from the course start and saves it when it is a new record or farm best; the reply is logged.
        /// </summary>
        void SubmitCourseLap(int course, string caps, float time, List<InputTick> lap)
        {
            string body = JsonSerializer.Serialize(new { course, capabilities = caps, ticks = lap.Select(t => t.ToJson()) });
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            for (int attempt = 0; attempt < 20; attempt++)
            {
                string reply;
                try { reply = Solve.Post(http, "/api/enginesim/submit", body); }
                catch (Exception ex) { Log($"record lap: {time:0.00}s as Course {course} {caps} not sent: {ex.GetBaseException().Message}"); return; }
                // A new best's start reloads from a quick restart, and a playback holds the game: both clear in a moment.
                if (reply.Contains("retry in a moment") || reply.Contains("loading"))
                {
                    Thread.Sleep(3000);
                    continue;
                }
                Log($"record lap: {time:0.00}s as Course {course} {caps}: {reply}");
                return;
            }
            Log($"record lap: {time:0.00}s as Course {course} {caps}: the game stayed busy");
        }

        // ---------------------------------------------------------------- playing an edit

        /// <summary>
        /// The edit played from the start under its goals. Where it misses a goal after the change (a buy it reaches before the
        /// Cash is there, or a walk that runs on and buys a neighbouring box instead), idle ticks go before the last quick restart
        /// ahead of that goal: the fewest that pass it (every 5 up to the lead the edit had there plus 60, then each below the first
        /// that passes: a stray buy isn't monotone in the wait), at most MaxPadFixes times. Returns the whole run, or null and why.
        /// </summary>
        (Route route, Route partial, string why) Played(Simulation sim, SimSnapshot origin, GoalEdit e, Route best)
        {
            int n = e.Labels.Length;
            List<InputTick> inputs = e.Inputs;
            int newAt = e.NewAt, oldAt = e.OldAt;
            var waits = new List<string>();
            long waited = 0;
            int fits = 0;
            for (int fix = 0; ; fix++)
            {
                sim.SetStart(origin, 0);
                sim.Goals.Limit = n;
                RunResult rr = sim.Run(inputs, null, null, 1000000);
                var part = new Route { Inputs = inputs, Progress = rr.Progress, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, GoalHeld = rr.GoalHeldFrames, EndReason = rr.EndReason };
                if (rr.Progress == n)
                    return (new Route { Inputs = inputs, Progress = n, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, GoalHeld = rr.GoalHeldFrames, EndReason = rr.EndReason }, null,
                        waits.Count + fits == 0 ? null : string.Join("; ", new[] { waits.Count == 0 ? null : "waits " + string.Join(", ", waits), fits == 0 ? null : $"{fits} stray fits" }.Where(s => s != null)));
                // A stray buy isn't a dead end: the goals are fitted to what the run buys (StrayFit), before any wait.
                if (fits < MaxStrayFits && StrayFit(sim, origin, e, inputs, rr) is (string[] labels, int[] oldOf))
                {
                    e.Labels = labels;
                    e.OldOf = oldOf;
                    n = labels.Length;
                    fits++;
                    fix--;
                    continue;
                }
                int g = rr.Progress + 1;
                string missed = $"misses goal {g} ({e.Labels[g - 1]}: {rr.EndReason})";
                if (fix == MaxPadFixes) return (null, part, $"{missed} after {MaxPadFixes} waits");
                int og = e.OldOf[g - 1];
                if (og < 0 || best.GoalInput[og] <= oldAt) return (null, part, missed + ", inside the change");
                int ni = (int)(best.GoalInput[og] - oldAt + newAt);
                int r = -1;
                for (int i = Math.Min(ni, inputs.Count) - 1; i >= newAt; i--) if (inputs[i].reset) { r = i; break; }
                if (r < 0) return (null, part, missed + "; no quick restart after the change to wait at");
                // Up to what the edit is ahead: at the goal before the one it misses, or (when that goal is before the change) the
                // ticks the splice took out less the waits so far.
                long lead = g >= 2 && e.OldOf[g - 2] >= 0 ? best.GoalPhysics[e.OldOf[g - 2]] - rr.GoalPhysicsTicks[g - 2] : 0;
                lead = Math.Max(lead, e.Saves - waited);
                int max = (int)Math.Clamp(lead + 60, 60, 900);
                sim.SetStart(origin, 0);
                sim.Goals.Limit = n;
                SimSnapshot at = sim.Advance(inputs.GetRange(0, r));
                if (at == null) return (null, part, missed + $"; the route ends before input {r}");
                bool held = r > 0 && inputs[r - 1].held;
                List<InputTick> tail = inputs.GetRange(r, inputs.Count - r);
                bool Passes(int w)
                {
                    var list = new List<InputTick>(w + tail.Count);
                    for (int i = 0; i < w; i++) list.Add(new InputTick { held = held });
                    list.AddRange(tail);
                    sim.SetStart(at, r);
                    sim.Goals.Limit = g;
                    return sim.Run(list, null, null, 1000000).Progress >= g;
                }
                int wait = -1;
                for (int w = 5; w <= max; w += 5) if (Passes(w)) { wait = w; break; }
                if (wait < 0) return (null, part, missed + $"; no wait of up to {max} idle ticks before the quick restart at input {r} passes it");
                for (int w = Math.Max(1, wait - 4); w < wait; w++) if (Passes(w)) { wait = w; break; }
                var padded = new List<InputTick>(inputs.Count + wait);
                padded.AddRange(inputs.Take(r));
                for (int i = 0; i < wait; i++) padded.Add(new InputTick { held = held });
                padded.AddRange(tail);
                inputs = padded;
                oldAt += r - newAt;
                newAt = r + wait;
                waited += wait;
                waits.Add($"{wait} idle ticks before input {r} (goal {g})");
            }
        }

        /// <summary>Ladders a pull reroutes (each a planner run, --piece-minutes a piece).</summary>
        const int Reroutes = 1;

        /// <summary>
        /// Economy change, then reroute: the fastest ladders that played through (their goals fitted to their strays) but don't beat
        /// the best get the goals from the ladder up to the next unlock buy planned again on their route by the engine farm planner
        /// (<see cref="Replan"/>, joined: the plan, the old way on after the target, the waits re-timed from the start). A planned run
        /// that beats the best is adopted as a goals edit.
        /// </summary>
        bool Reroute(List<GoalEdit> edits, (Route route, Route partial, string why)[] results, Route best)
        {
            foreach (int i in Enumerable.Range(0, edits.Count).Where(i => edits[i].Kind == "ladder" && results[i].route != null)
                .OrderBy(i => results[i].route.Live).Take(Reroutes))
            {
                GoalEdit e = edits[i];
                int kept = e.Retune;
                string target = e.Labels.Skip(kept).FirstOrDefault(l => l.StartsWith("buy:", StringComparison.Ordinal) && !FullTasGoal.IsEconomy(l));
                if (target == null || TimeUp) continue;
                PlanResult p;
                try { p = Replan(results[i].route, GoalsOf(e.Labels), kept, new[] { target }, true); }
                catch (Exception ex) { Log($"auto goals: reroute of {e.Text}: {ex.GetBaseException().Message}"); continue; }
                Route r = p.Run;
                bool complete = r != null && r.Progress == p.Goals.Length;
                Log($"auto goals: reroute of {e.Text}: goals {kept + 1}.. to {target} planned again ({p.Planned?.Length ?? 0} goals): "
                    + (complete ? $"{r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s, {r.Last - best.Last:+0;-0} ticks against the best" : "doesn't play through"));
                if (!complete || r.Live >= best.Live) continue;
                // The planned goals map to no old goal; those before the ladder and after the target to the edit's.
                int tp = Array.IndexOf(p.Goals, target, kept), te = Array.IndexOf(e.Labels, target, kept);
                int[] oldOf = Enumerable.Range(0, p.Goals.Length).Select(j => j < kept ? e.OldOf[j] : j >= tp ? e.OldOf[j - tp + te] : -1).ToArray();
                var planned = new GoalEdit { Kind = "reroute", Text = $"{e.Text}, rerouted to {target}", Key = e.Key + ":reroute", Labels = p.Goals, OldOf = oldOf,
                    Inputs = r.Inputs, First = e.First, Last = e.Last };
                if (AdoptGoals(planned, r, best)) return true;
            }
            return false;
        }

        /// <summary>A play fits its goals to its strays at most this often; a move looks this many goals ahead for the stray box's buy.</summary>
        const int MaxStrayFits = 40, StrayFitWindow = 16;
        static readonly object StrayLog = new object();

        /// <summary>
        /// StrayFit: a run <paramref name="rr"/> of <paramref name="e"/>'s inputs that bought box B off its goal list (strict buys,
        /// README Rules 38: every buy is a goal) gets its goals fitted to what it buys: B's next buy goal within StrayFitWindow moved
        /// up to the stray, <c>buy:B</c> put in there, or the goal it stopped at dropped (an economy buy whose touch can't pay any
        /// more). Each is replayed; the one that gets furthest (past the stray, or level with it and ending otherwise) is returned with its old-goal map, null when none
        /// does or B has no economy buy label in the run. Leaves <paramref name="sim"/> on the fitted goals.
        /// </summary>
        (string[] labels, int[] oldOf)? StrayFit(Simulation sim, SimSnapshot origin, GoalEdit e, List<InputTick> inputs, RunResult rr)
        {
            string box = StrayBox(rr.EndReason);
            int p = rr.Progress;
            if (box == null || p >= e.Labels.Length) return null;
            string label = _runGoals.Goals.FirstOrDefault(g => g.kind == FullTasGoal.Buy && g.boxes?.Length == 1 && g.boxes[0].Split('#')[0] == box && FullTasGoal.IsEconomy(g.label))?.label;
            var options = new List<(string how, List<string> labels, List<int> oldOf)>();
            if (label != null)
            {
                int later = Enumerable.Range(p + 1, Math.Max(0, Math.Min(e.Labels.Length, p + 1 + StrayFitWindow) - p - 1)).FirstOrDefault(k => e.Labels[k] == label);
                if (later > p)
                {
                    var l = e.Labels.ToList(); var o = e.OldOf.ToList();
                    l.RemoveAt(later); l.Insert(p, label);
                    o.Insert(p, o[later]); o.RemoveAt(later + 1);
                    options.Add(($"move goal {later + 1} ({label}) to {p + 1}", l, o));
                }
                var li = e.Labels.ToList(); var oi = e.OldOf.ToList();
                li.Insert(p, label); oi.Insert(p, -1);
                options.Add(($"insert {label} at goal {p + 1}", li, oi));
            }
            if (e.Labels[p].StartsWith("buy:", StringComparison.Ordinal) && FullTasGoal.IsEconomy(e.Labels[p]))
            {
                var ld = e.Labels.ToList(); var od = e.OldOf.ToList();
                ld.RemoveAt(p); od.RemoveAt(p);
                options.Add(($"drop goal {p + 1} ({e.Labels[p]})", ld, od));
            }
            (string how, string[] labels, int[] oldOf, int progress, string why) pick = default;
            foreach (var (how, l, o) in options)
            {
                string[] labels = l.ToArray();
                if (FullTasGoal.EditRefused(_labels, labels) != null) continue;
                sim.UseGoals(sim.Goals.For(GoalsOf(labels)));
                sim.SetStart(origin, 0);
                sim.Goals.Limit = labels.Length;
                RunResult r = sim.Run(inputs, null, null, 1000000);
                // Progress in the old numbering's terms: a goal put in counts one more, a goal dropped one less.
                int reach = r.Progress - (labels.Length - e.Labels.Length);
                // Level with the stray counts when the run now ends otherwise: a walk that buys a row of boxes early takes one fit a box.
                if ((reach > p || reach == p && r.EndReason != rr.EndReason) && (pick.how == null || reach > pick.progress)) pick = (how, labels, o.ToArray(), reach, r.Progress == labels.Length ? "complete" : r.EndReason);
            }
            lock (StrayLog)
            {
                Log($"auto stray fit: {e.Text}: bought {box} at goal {p + 1} ({e.Labels[p]}): " + (pick.how == null ? $"none of {options.Count} fits gets past it" : $"{pick.how}, reaches goal {pick.progress} ({pick.why})"));
                if (_autoLog != null)
                    AutoWrite(new { type = "strayFit", edit = e.Text, box, goal = p + 1, fit = pick.how, tried = options.Count, reach = pick.how == null ? p : pick.progress, result = pick.why });
            }
            sim.UseGoals(sim.Goals.For(GoalsOf(pick.how == null ? e.Labels : pick.labels)));
            return pick.how == null ? null : (pick.labels, pick.oldOf);
        }

        // ---------------------------------------------------------------- adopting an edit

        /// <summary>
        /// The edit's goals become the run's, and its route the best (Commit: the fresh check, the run file with the goals it came
        /// from, the offline best and the gate). The loop's goal-numbered state starts over on the new goals: the references, the
        /// arms, the library, the model and the parts. Undone when the fresh check doesn't keep the route.
        /// </summary>
        bool AdoptGoals(GoalEdit e, Route route, Route old)
        {
            if (!e.Labels.SequenceEqual(_labels) && FullTasGoal.EditRefused(_labels, e.Labels) is string refused)
            {
                Log($"auto goals: {e.Text} refused: {refused}");
                return false;
            }
            bool changed = !e.Labels.SequenceEqual(_labels);
            if (!changed)
            {
                Commit(route, "goals: " + e.Text);
                return false;
            }
            var saved = (header: AutoHeader(), create: _create, eval: _eval, origin: _origin, tracker: _runGoals, labels: _labels, goals: _goals, best: _best);
            string[] from = _labels;
            SwitchGoals(e.Labels);
            // Commit compares with the best under these goals: the old route's end with its goal ticks where the goals are its.
            Route standIn = StandIn(e, old, false);
            _best = standIn;
            _goalLineage.Add(from);
            string words = $"{e.Text}: -{(old.LiveSeconds - route.LiveSeconds).ToString("0.00", CultureInfo.InvariantCulture)} s";
            _goalsEdit = words;
            try { Commit(route, "goals: " + words); }
            finally { _goalsEdit = null; }
            if (ReferenceEquals(_best, standIn))
            {
                _goalLineage.RemoveAt(_goalLineage.Count - 1);
                (_autoHeader, _create, _eval, _origin, _runGoals, _labels, _goals, _best) = saved;
                RebindEval();
                Log($"auto goals: {e.Text}: the fresh check didn't keep it; the goals stay");
                return false;
            }
            Log($"auto goals: ADOPTED {e.Text}: {from.Length} -> {_goals} goals, {old.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s -> {Describe(_best)}");
            AutoWrite(new { type = "goals", text = words, from = from.Length, to = _goals, goals = _labels, time = _best.LiveSeconds, gain = old.Last - _best.Last });
            RestartOnGoals();
            return true;
        }

        /// <summary>The old best under an edit's goals: each goal's tick (and, with <paramref name="mapInputs"/>, its input) the
        /// old goal's it maps to, a goal the old list doesn't have the one before's; the end is the old end (or
        /// <paramref name="bar"/>'s: the time to beat).</summary>
        Route StandIn(GoalEdit e, Route old, bool mapInputs, Route bar = null)
        {
            bar ??= old;
            long[] Map(long[] of) => e.OldOf.Select((k, i) => k >= 0 ? of[k] : of[Math.Max(0, e.OldOf.Take(i).LastOrDefault(x => x >= 0))]).ToArray();
            long[] physics = Map(old.GoalPhysics);
            physics[physics.Length - 1] = bar.End;
            long[] held = old.GoalHeld == null ? null : Map(old.GoalHeld);
            if (held != null) held[held.Length - 1] = bar.GoalHeld[bar.Progress - 1];
            long[] input = mapInputs ? Map(old.GoalInput) : Enumerable.Repeat((long)old.Inputs.Count, e.Labels.Length).ToArray();
            return new Route { Inputs = old.Inputs, Progress = e.Labels.Length, GoalInput = input, GoalPhysics = physics, GoalHeld = held, EndReason = old.EndReason };
        }

        /// <summary>A goals edit being re-found (<see cref="InvestRepair"/>): the repair's hopeless test keeps only its CPU budget.</summary>
        bool _investing;
        /// <summary>An investment's repair budget: CPU seconds per goal it re-finds. At 60, FalseEnding-Any%'s Course 2 lap after
        /// buy:buyds (110 goals to re-find) ran out at goal 250 of 284, each repair window running its whole half minute.</summary>
        const double InvestCpuPerGoal = 150;

        /// <summary>
        /// An investment: an edit that costs ticks where it changes the goals and pays later through Cash (a record lap inserted
        /// right after an unlock buy: its clones run the faster path through everything after), whose old way on no longer plays
        /// from where the change leaves the player. Its goals become the run's for the repair: the goals after where its partial
        /// run stops are re-found as a window's repair does (the old best's inputs as seeds), except that falling behind the best
        /// at a goal doesn't end it: an investment is behind until its income pays. It ends when its CPU budget is spent
        /// (InvestCpuPerGoal a goal left) or it has every goal; kept only when the whole run then beats the best (the fresh
        /// check, as every best). One re-found to the end but no faster has the goals after its change planned again on it
        /// (<see cref="Replan"/>). The goals change back when it isn't kept. <paramref name="bar"/>: the time to beat when
        /// <paramref name="old"/> (the seeds) isn't the best (a replan's investments).
        /// </summary>
        bool InvestRepair(GoalEdit e, Route partial, Route old, Route bar = null)
        {
            _refound = null;
            if (partial == null || partial.Progress < e.Last || TimeUp) return false;
            bar ??= old;
            var saved = (header: AutoHeader(), create: _create, eval: _eval, origin: _origin, tracker: _runGoals, labels: _labels, goals: _goals, best: _best);
            var hooks = (collect: _collect, hopeless: _hopeless);
            string[] from = _labels;
            SwitchGoals(e.Labels);
            Route standIn = StandIn(e, old, true, bar);
            _best = standIn;
            _goalLineage.Add(from);
            var run = new Route { Inputs = partial.Inputs.Take((int)Math.Min(partial.GoalInput[partial.Progress - 1], partial.Inputs.Count)).ToList(), Progress = partial.Progress,
                GoalInput = partial.GoalInput, GoalPhysics = partial.GoalPhysics, GoalHeld = partial.GoalHeld, EndReason = partial.EndReason };
            Log($"auto goals: {e.Text}: {partial.GoalPhysics[partial.Progress - 1] - standIn.GoalPhysics[partial.Progress - 1]:+0;-0} ticks against the best at goal {partial.Progress}, "
                + $"where its old way on stops; re-finding goals {partial.Progress + 1}-{_goals} as an investment ({InvestCpuPerGoal * (_goals - partial.Progress):0} CPU s)");
            _investing = true;
            // Its references are numbered by these goals, not the loop's: the repair's runs only keep the fastest complete one.
            Route full = null;
            _collect = (r, _) => { if (r.Progress == _goals && (full == null || r.Live < full.Live)) full = r; };
            _repairFloorCpu = InvestCpuPerGoal * (_goals - partial.Progress);
            _goalsEdit = e.Text;
            try { Repair("goals: " + e.Text, standIn, run); }
            finally { _investing = false; _repairFloorCpu = 0; _goalsEdit = null; (_collect, _hopeless) = hooks; }
            if (ReferenceEquals(_best, standIn))
            {
                if (full != null && !_replanning)
                {
                    Log($"auto goals: {e.Text}: re-found to the end at {Describe(full)}, {full.Last - bar.Last:+0;-0} ticks against the best; planning the goals after it again");
                    _replanning = true;
                    try { if (Replan(e, full, bar)) return true; }
                    finally { _replanning = false; }
                }
                _goalLineage.RemoveAt(_goalLineage.Count - 1);
                (_autoHeader, _create, _eval, _origin, _runGoals, _labels, _goals, _best) = saved;
                RebindEval();
                _refound = full;
                Log($"auto goals: {e.Text}: re-found, no faster than the best; the goals stay");
                return false;
            }
            string words = $"{e.Text}: -{(bar.LiveSeconds - _best.LiveSeconds).ToString("0.00", CultureInfo.InvariantCulture)} s";
            Log($"auto goals: ADOPTED {words}: {from.Length} -> {_goals} goals, {bar.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s -> {Describe(_best)}");
            AutoWrite(new { type = "goals", text = words, from = from.Length, to = _goals, goals = _labels, time = _best.LiveSeconds, gain = bar.Last - _best.Last });
            RestartOnGoals();
            return true;
        }

        /// <summary>A replan runs (<see cref="Replan"/>): its own investments don't plan again.</summary>
        bool _replanning;
        /// <summary>The last investment that wasn't kept: its run re-found to the end (null: none).</summary>
        Route _refound;

        /// <summary>
        /// An investment re-found to the end, no faster than the best (<paramref name="bar"/>): the goals after its change planned
        /// again on its route, as a goals pull plans the best's (<see cref="DropEdits"/>, <see cref="AddEdits"/> with its own
        /// model: the laps its income makes unneeded, laps in place of a wait). The fastest edit that beats the investment
        /// becomes it, and is planned again, until one beats the best (adopted) or none beats the investment. With none played
        /// through faster, its adds and the drops that stop ahead of it (the extra Cash strays into a box the old buy walk only
        /// brushed) are re-found as investments (<see cref="Investments"/>): one that beats the best is adopted, the fastest
        /// that beats the investment becomes it. FalseEnding-Any%'s Course 2 lap after buy:buyds: Course 3's laps and buys, and
        /// Course 3 laps at the top in place of the climb to buy:buydj.
        /// </summary>
        bool Replan(GoalEdit e, Route inv, Route bar)
        {
            while (!TimeUp)
            {
                Route standIn = _best;
                List<GoalRow> rows = ModelOf(inv);
                var edits = DropEdits(inv, Laps(inv)).Concat(AddEdits(inv, rows)).Where(x => x.First > e.Last && FullTasGoal.EditRefused(_labels, x.Labels) == null).ToList();
                if (edits.Count == 0) return false;
                double c0 = CpuSeconds();
                var results = new (Route route, Route partial, string why)[edits.Count];
                RunOnThreads(edits.Count, (sim, origin, tracker, i) =>
                {
                    sim.UseGoals(tracker.For(GoalsOf(edits[i].Labels)));
                    results[i] = Played(sim, origin, edits[i], inv);
                });
                int win = -1;
                for (int i = 0; i < edits.Count; i++)
                {
                    Route r = results[i].route;
                    Log($"auto goals: replan of {e.Text}: {edits[i].Text}: " + (r == null ? results[i].why ?? "not played"
                        : $"{r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s ({r.Last - inv.Last:+0;-0} ticks against the investment, {r.Last - bar.Last:+0;-0} against the best)"));
                    if (r != null && r.Live < inv.Live && (win < 0 || r.Live < results[win].route.Live)) win = i;
                }
                Log($"auto goals: replan of {e.Text}: {edits.Count} edits in {CpuSeconds() - c0:0} CPU s" + (win < 0 ? ", none faster" : $", fastest {edits[win].Text}"));
                (GoalEdit edit, Route route) next = win < 0 ? default : (edits[win], results[win].route);
                if (next.route == null)
                    foreach (var (x, p, _) in Investments(edits, results, inv, true).Where(x => x.rank < InvestTries))
                    {
                        if (TimeUp) break;
                        if (InvestRepair(x, p, inv, bar)) return true;
                        if (_refound != null && _refound.Live < (next.route ?? inv).Live) next = (x, _refound);
                    }
                if (next.route == null) return false;
                GoalEdit w = next.edit;
                inv = next.route;
                SwitchGoals(w.Labels);
                if (inv.Live >= bar.Live)
                {
                    _best = StandIn(w, standIn, true, bar);
                    continue;
                }
                Route stand = StandIn(w, standIn, false, bar);
                _best = stand;
                string words = $"{e.Text}; {w.Text}: -{(bar.LiveSeconds - inv.LiveSeconds).ToString("0.00", CultureInfo.InvariantCulture)} s";
                _goalsEdit = words;
                try { Commit(inv, "goals: " + words); }
                finally { _goalsEdit = null; }
                if (ReferenceEquals(_best, stand)) return false;
                Log($"auto goals: ADOPTED {words}: {_goalLineage[_goalLineage.Count - 1].Length} -> {_goals} goals, {bar.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s -> {Describe(_best)}");
                AutoWrite(new { type = "goals", text = words, from = _goalLineage[_goalLineage.Count - 1].Length, to = _goals, goals = _labels, time = _best.LiveSeconds, gain = bar.Last - _best.Last });
                RestartOnGoals();
                return true;
            }
            return false;
        }

        /// <summary>The gap model's rows of a route under the current goals (<see cref="BuildModel"/> on it; the loop's model stays).</summary>
        List<GoalRow> ModelOf(Route r)
        {
            var keep = (best: _best, model: _model, bounds: _bounds);
            _best = r;
            try
            {
                BuildModel();
                return _model;
            }
            finally { (_best, _model, _bounds) = keep; }
        }

        /// <summary>The run's goals become these: the trace's header with this goal list (its goals' own entries, a grouped buy made
        /// of its boxes' entries) makes every simulation from here on, the loop's own included.</summary>
        void SwitchGoals(string[] labels)
        {
            JsonNode node = JsonNode.Parse(AutoHeader().GetRawText());
            JsonArray old = node["fulltas"]["goals"].AsArray();
            var byLabel = new Dictionary<string, JsonNode>();
            foreach (JsonNode g in old) byLabel.TryAdd(g["label"].GetValue<string>(), g);
            var goals = new JsonArray();
            foreach (string l in labels)
            {
                if (byLabel.TryGetValue(l, out JsonNode g)) { goals.Add(g.DeepClone()); continue; }
                string[] parts = l.Substring("buy:".Length).Split('+');
                JsonNode first = byLabel["buy:" + parts[0]].DeepClone();
                first["label"] = l;
                first["boxes"] = new JsonArray(parts.SelectMany(p => byLabel["buy:" + p]["boxes"].AsArray().Select(b => (JsonNode)b.GetValue<string>())).ToArray());
                goals.Add(first);
            }
            node["fulltas"]["goals"] = goals;
            JsonElement header;
            using (JsonDocument doc = JsonDocument.Parse(node.ToJsonString(), new JsonDocumentOptions { MaxDepth = 256 })) header = doc.RootElement.Clone();
            _autoHeader = header;
            Func<Simulation> make = Solve.Creator(_trace, header);
            _create = () => { Simulation sim = make(); sim.StrictBuys = true; return sim; };
            _eval = _create();
            _origin = _eval.Start;
            _runGoals = _eval.Goals;
            _labels = labels;
            _goals = labels.Length;
            _focusTo = _goals;
        }

        /// <summary>After the goals change back: the loop's simulation made again. A Simulation made on this thread since (SwitchGoals')
        /// took over the thread's game statics and engine hooks (World.Load, the Simulation constructor), so the saved one played
        /// every run to no goal: after a goals pull whose investment wasn't kept, every window find was dropped as "merged onto
        /// this best already" (its replay never reached the window's goal) and the model's per-goal table came out empty.</summary>
        void RebindEval()
        {
            _eval = _create();
            _origin = _eval.Start;
        }

        /// <summary>After adopted goals: every goal-numbered piece of the loop starts again from the new best (as a new --auto would,
        /// without the speed probes, which are per unlock set).</summary>
        void RestartOnGoals()
        {
            _refs.Clear(); _refKeys.Clear(); _arms.Clear(); _grafted.Clear(); _studied.Clear(); _planDead.Clear(); _polished.Clear();
            _polishedBest = null; _combineTried.Clear(); _reviveTried.Clear(); _revivedOn.Clear(); _combinedFor = null; _revivedFor = null; _mergedOn = null; _merged.Clear();
            _memo.Clear();
            if (_lib != null)
            {
                _lib = new StateLibrary(_goals, _lib.MaxRuns);
                TraceRun(_best.Inputs, "the best after a goals edit");
                _chain0 = Chaining(_best.Inputs);
                _chainStart = _best.Inputs;
            }
            Collect(_best, "the best after a goals edit");
            BuildModel();
            PrintModel("goals edit");
            MakeParts();
            RefitParts();
            SaveRefs();
        }

        /// <summary>
        /// At the start: a run file whose goals an earlier --auto adopted from this trace's (its goalsFrom lists them) is the run's
        /// best under those goals; the loop takes them when that route beats the best (a new capture of the start has them anyway).
        /// </summary>
        void ResumeAdoptedGoals()
        {
            if (!File.Exists(_runFile)) return;
            string[] goals;
            List<string[]> from;
            List<InputTick> inputs;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_runFile), new JsonDocumentOptions { MaxDepth = 256 }))
            {
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("goalsFrom", out JsonElement f) || f.ValueKind != JsonValueKind.Array) return;
                goals = root.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).ToArray();
                from = f.EnumerateArray().Select(l => l.EnumerateArray().Select(g => g.GetString()).ToArray()).ToList();
                inputs = Solve.ReadInputs(root);
            }
            int mine = from.FindIndex(l => l.SequenceEqual(_labels));
            if (goals.SequenceEqual(_labels) || mine < 0 || FullTasGoal.EditRefused(_labels, goals) != null) return;
            var saved = (header: AutoHeader(), create: _create, eval: _eval, origin: _origin, tracker: _runGoals, labels: _labels, goals: _goals, best: _best);
            string[] trace = _labels;
            SwitchGoals(goals);
            Route r = Evaluate(inputs);
            if (r.Progress == _goals && (saved.best.Progress < saved.goals || r.Live < saved.best.Live))
            {
                _goalLineage.AddRange(from.Skip(mine));
                _best = r;
                Log($"auto goals: fulltas/{Path.GetFileName(_runFile)} has goals adopted from the trace's ({trace.Length} -> {_goals} goals) at {Describe(r)}: the loop runs on them");
                return;
            }
            (_autoHeader, _create, _eval, _origin, _runGoals, _labels, _goals, _best) = saved;
            RebindEval();
            Log($"auto goals: fulltas/{Path.GetFileName(_runFile)}'s adopted goals ({goals.Length}) replay as {r.Progress}/{goals.Length} goals"
                + (r.Progress == goals.Length ? $" ({Describe(r)}), no faster than the trace's best" : "") + "; the trace's goals stay");
        }
    }
}
