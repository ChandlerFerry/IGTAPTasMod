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
    // --parts goals (docs/fulltas.md, "Parts"): the run's economy goals planned again when the whole run gets faster. Every stretch
    // of a Full-TAS economy is Cash-bound at its target buy, so a lap or walk made faster only turns into a wait there; what pays
    // is a goal list that needs fewer laps. A pull plans each economy stretch (an unlock buy or reward goal to the next unlock buy)
    // with the economy planner (FullTasPlan.cs, Replan: search, realize, join), and adopts the planned list only when the whole
    // run is strictly faster and reaches every goal. The run's end goal, its area and reward goals and every unlock buy stay
    // (FullTasGoal.EditRefused), but for the cash goals (GLOBAL cashPerLoop boxes, GoalsRefused); an adopted list goes with the
    // route (the run file's goals, the offline best's and the gate's), and the game updates fulltas.json when it judges that route
    // a record. The edits a pull played before the planner (drops, adds, record laps, ...) are no longer played.
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
            /// <summary>A cash goal edit the best's inputs can't play (<see cref="CashEdits"/>): not played, re-found as an investment
            /// from this many goals in, the best's own (0: played).</summary>
            public int RefindFrom;
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
        /// <summary>A goals pull plans again only after a new best and this long since the last pull, or at once after it adopted
        /// a list.</summary>
        const double GoalsEveryMinutes = 5;
        /// <summary>The adds that play through but aren't faster whose later waits a goals pull re-times (Retune: a play per idle tick of each).</summary>
        const int AddRetunes = 2;
        const int MaxPadFixes = 6, RecordRoutes = 12, RecordReached = 4, InvestTries = 2;
        /// <summary>A walk-in lap's quick restart goes 0..WalkInRestarts-1 inputs after the walk touches the gate.</summary>
        const int WalkInRestarts = 4;
        /// <summary>The best's own laps of a course a record lap tries (the shortest, one per distinct input list).</summary>
        const int OwnLapRoutes = 6;
        /// <summary>--record-search-minutes: the wall minutes (every thread) a goals pull searches a lap from the quick restart for a
        /// course and unlock set with no saved -restart lap (once per run; 0: never).</summary>
        double _recordSearchMinutes = 2;
        /// <summary>The (course, unlock set) keys a goals pull already searched a restart lap for.</summary>
        readonly HashSet<string> _lapSearched = new HashSet<string>();

        /// <summary>--stretch-minutes: a goals pull's time a stretch (Replan's four searches, realize between them, the join).
        /// FalseEnding%-280 (2026-10-04, beam 200): buy:buywj to buy:buyds searches about 11 minutes a round, reward:bonus3 to
        /// buy:buystatue 19.</summary>
        double _stretchMinutes = 60;
        /// <summary>The stretches pulled (the route planned from, to its start, and the target), kept in the memory with the
        /// movement table's stamp then (<see cref="MovesStamp"/>): a pull skips them until the table changes.</summary>
        readonly HashSet<string> _stretchPulled = new HashSet<string>();
        string _stretchMoves;
        /// <summary>fulltas/&lt;run&gt;.moves.json's write time and length ("" when there is none).</summary>
        string MovesStamp() => File.Exists(MovesFile) ? $"{File.GetLastWriteTimeUtc(MovesFile).Ticks}/{new FileInfo(MovesFile).Length}" : "";
        /// <summary>When the last goals pull ended: the other parts get as long after it as it took (a pull plans for up to
        /// --stretch-minutes a stretch), so a short run's windows, idle, waste and grafts get turns between them.</summary>
        TimeSpan _goalsEnd;

        /// <summary>A goals pull is due (anyTime: whatever the waits, when nothing else is left to pull).</summary>
        bool GoalsDue(bool anyTime = false)
        {
            if (!Part("goals") || ReferenceEquals(_goalsFor, _best)) return false;
            if (_goalsFor == null || anyTime) return true;
            return _clock.Elapsed - _goalsEnd >= _goalsEnd - _goalsAt && (_goalsAgain || (_clock.Elapsed - _goalsAt).TotalMinutes >= GoalsEveryMinutes);
        }

        /// <summary>
        /// One goals pull: the economy planner (<see cref="Replan"/>: search, realize, join) on each economy stretch of the best in
        /// order (<see cref="Stretches"/>), each from the route the stretches before it left: the best, or a stretch's realized and
        /// joined plan once it buys its target sooner (the best's old way on after an earlier change can stop short: the next
        /// stretch is then planned from where it stops, on the best's own way on after that stretch's target, <see cref="Onto"/>).
        /// The pull's time (--stretch-minutes a stretch, at most half what the session has left) is shared by the stretches left
        /// by the best's ticks in each. The
        /// fastest whole route planned that beats the best is adopted (<see cref="AdoptGoals"/>). Returns whether the run's goals
        /// changed.
        /// </summary>
        bool GoalsPull()
        {
            Route best = _best;
            _goalsFor = best;
            _goalsAt = _clock.Elapsed;
            double c0 = CpuSeconds();
            var w0 = _clock.Elapsed;
            List<(int kept, int target, string[] bonuses)> own = Stretches(_labels);
            int count = own.Count;
            // Each stretch's share of the time left goes by the best's ticks in it (FalseEnding%-280: 5371, 3322 and 896).
            long[] weight = own.Select(s => Math.Max(1, s.target < best.Progress ? best.GoalPhysics[s.target] - best.GoalPhysics[s.kept - 1] : 1)).ToArray();
            if (count == 0)
            {
                Log("auto: goals: the run has no economy stretch (from an unlock buy or reward goal to the next unlock buy)");
                return false;
            }
            _pullNo++;
            int pull = _pullNo;
            // At most half the session's time left: the other parts get their turns.
            double deadline = _clock.Elapsed.TotalSeconds + Math.Max(60, Math.Min(_stretchMinutes * count, (_minutes - _clock.Elapsed.TotalMinutes) / 2) * 60);
            if (_stretchMoves != MovesStamp()) _stretchPulled.Clear();
            Log($"auto: pull {pull}: goals, the economy planner on {count} stretches in order, {deadline - _clock.Elapsed.TotalSeconds:0} s shared");
            // The route the next stretch is planned on, its goals and per goal the best's goal (-1: a planned one).
            Route on = best;
            string[] labels = _labels;
            int[] map = Enumerable.Range(0, _goals).ToArray();
            var planned = new List<string>();
            (Route route, string[] labels, int[] map, string text, int first) win = default;
            int first = 0;
            for (int i = 0; i < count && ReferenceEquals(_best, best) && !TimeUp; i++)
            {
                List<(int kept, int target, string[] bonuses)> stretches = Stretches(labels);
                if (i >= stretches.Count) break;
                var (kept, t, bonuses) = stretches[i];
                string what = $"stretch {i + 1}/{count}, goals {kept + 1}-{t + 1} ({labels[kept - 1]} to {labels[t]}{(bonuses.Length > 0 ? ", " + string.Join(" ", bonuses) + " offered" : "")})";
                int ot = map[t];
                long bestTick = ot >= 0 && ot < best.Progress ? best.GoalPhysics[ot] : -1;
                bool through = on.Progress > t;
                Route from = through ? on : Onto(on, labels, map, kept, t, best);
                if (from == null)
                {
                    Log($"auto goals: {what}: the route planned so far stops at goal {on.Progress + 1} ({(on.Progress < labels.Length ? labels[on.Progress] : "")}: {on.EndReason}), "
                        + "before the stretch's start or with no way of the best's on after its target; the stretches after it aren't planned");
                    break;
                }
                // Planned before from this route with these moves (an earlier pull or session): it plans the same way again.
                string key = Hex(SnapshotIndex.Hash(from.Inputs, (int)Math.Min(from.GoalInput[kept - 1], from.Inputs.Count))) + ":" + labels[t];
                if (!_stretchPulled.Add(key))
                {
                    Log($"auto goals: {what}: planned before from this route, the movement table unchanged since");
                    if (through) continue;
                    break;
                }
                // A plan that buys the target no sooner than the route it replaces (or, where that stops short, the best) isn't realized.
                long cap = through ? on.GoalPhysics[t] : bestTick < 0 ? best.Last : bestTick;
                double seconds = Math.Max(30, (deadline - _clock.Elapsed.TotalSeconds) * weight[i] / weight.Skip(i).Sum());
                PlanResult p;
                try { p = Replan(from, GoalsOf(labels), kept, new[] { labels[t] }, true, cap, seconds, bonuses); }
                catch (Exception ex)
                {
                    Log($"auto goals: {what}: {ex.GetBaseException().Message}");
                    if (through) continue;
                    break;
                }
                Route r = p.Run;
                bool whole = r != null && r.Progress == p.Goals.Length;
                string plans = p.PlanTicks == null || p.PlanTicks.Length == 0 ? "no plan" : "planned " + string.Join(" > ", p.PlanTicks.Select(x => x < 0 ? "-" : x.ToString(CultureInfo.InvariantCulture)));
                string verdict = !p.Done ? $"not built ({p.Why})"
                    : $"built tick {p.Tick} ({p.Tick - cap:+0;-0} against {(through ? "the route's" : "the best's")} {cap}{(through && bestTick >= 0 && bestTick != cap ? $", {p.Tick - bestTick:+0;-0} against the best's {bestTick}" : "")}); "
                    + (r == null ? "not joined" : whole ? $"whole route {r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s ({r.Last - best.Last:+0;-0} ticks against the best)"
                        : $"the route on stops at goal {r.Progress + 1} of {p.Goals.Length} ({(r.Progress < p.Goals.Length ? p.Goals[r.Progress] : "")}: {r.EndReason})");
                Log($"auto goals: {what}: {plans}, {verdict}");
                AutoWrite(new { type = "goalEdit", pull, kind = "plan", text = what, first = kept + 1, last = t + 1, goals = p.Goals.Length,
                    plans = p.PlanTicks, built = p.Done ? p.Tick : -1, cap, bestTick, time = whole ? r.LiveSeconds : (double?)null, gain = whole ? best.Last - r.Last : (long?)null, why = p.Why });
                // Built, but no sooner than the route it would replace: that route stays (realize checks the cap on plans, not builds).
                if (!p.Done || r == null || through && p.Tick >= cap)
                {
                    if (through) continue;
                    break;
                }
                int[] prev = map;
                map = p.OldOf.Select(j => j < 0 ? -1 : prev[j]).ToArray();
                labels = p.Goals;
                on = r;
                Known(labels);
                FindCashGoals();
                if (first == 0) first = kept + 1;
                planned.Add($"goals {kept + 1}-{t + 1} planned to {p.Target} on tick {p.Tick}");
                if (whole && (win.route == null || r.Live < win.route.Live)) win = (r, labels, map, "economy planned: " + string.Join("; ", planned), first);
            }
            bool adopted = false;
            if (win.route != null && win.route.Live < best.Live && ReferenceEquals(_best, best))
            {
                var e = new GoalEdit { Kind = "plan", Text = win.text, Key = "plan", Labels = win.labels, OldOf = win.map, Inputs = win.route.Inputs,
                    First = win.first, Last = win.labels.Length };
                adopted = AdoptGoals(e, win.route, best);
            }
            string outcome = win.route == null ? "no stretch's plan gave a whole route"
                : win.route.Live >= best.Live ? $"the fastest whole route planned, {win.route.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s on {win.labels.Length} goals, is no faster than the best" : $"{win.text}: {win.route.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s on {win.labels.Length} goals";
            _goalsAgain = adopted;
            _goalsEnd = _clock.Elapsed;
            _stretchMoves = MovesStamp();
            SaveMemory();
            double cpu = CpuSeconds() - c0, wall = (_clock.Elapsed - w0).TotalSeconds;
            long gain = GainTicks(best);
            _order.Add("goals" + (gain > 0 ? $"(-{gain})" : ""));
            Log($"auto: pull {pull} goals: {outcome}; {(gain > 0 ? $"GAINED {gain} ticks" : "no gain")} in {cpu:0} CPU s, {wall:0} s; best {Describe(_best)}");
            AutoWrite(new { type = "pull", pull, lever = "goals", goal = win.first, label = "goals", budget = 0, outcome, gain, cpu = Math.Round(cpu, 1),
                wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), best = _best.LiveSeconds, score = double.NaN });
            if (gain > 0)
            {
                _curve.Add(((_clock.Elapsed - _autoWall0).TotalSeconds, CpuSeconds() - _autoCpu0, _best.Last, "goals", win.first));
                AutoWrite(new { type = "record", lever = "goals", goal = win.first, gain, time = _best.LiveSeconds, ticks = _best.Last,
                    wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), cpu = Math.Round(CpuSeconds() - _autoCpu0, 1),
                    splits = Enumerable.Range(1, _goals).Select(k => Split(_best.GoalPhysics, k)).ToArray(), goals = _labels });
            }
            Outlook();
            return adopted;
        }

        /// <summary>
        /// The economy stretches of a goal list, in order: per unlock buy (a buy of anything but economy boxes), the goals before it
        /// back to the last that is neither an economy goal nor a reward goal, from the first unlock buy or reward goal there on. Each
        /// is its start (the goals kept before the plan: the start goal's 1-based number), its target (0-based) and the reward goals
        /// in it, which the planner may take or skip. FalseEnding%-280's 272 goals: buy:buywj to buy:buyds (reward:bonus1 offered),
        /// reward:bonus3 to buy:buystatue, buy:buystatue to buy:buydj; Course 1's laps before buy:buywj have no start.
        /// </summary>
        internal static List<(int kept, int target, string[] bonuses)> Stretches(string[] labels)
        {
            static bool Unlock(string l) => l.StartsWith("buy:", StringComparison.Ordinal) && !FullTasGoal.IsEconomy(l);
            static bool Reward(string l) => l.StartsWith("reward:", StringComparison.Ordinal);
            var list = new List<(int, int, string[])>();
            for (int t = 0; t < labels.Length; t++)
            {
                if (!Unlock(labels[t])) continue;
                int s = t - 1;
                while (s >= 0 && (FullTasGoal.IsEconomy(labels[s]) || Reward(labels[s]))) s--;
                int start = Enumerable.Range(Math.Max(s, 0), t - Math.Max(s, 0)).FirstOrDefault(k => Unlock(labels[k]) || Reward(labels[k]), -1);
                if (start >= 0) list.Add((start + 1, t, labels.Skip(start + 1).Take(t - start - 1).Where(Reward).ToArray()));
            }
            return list;
        }

        /// <summary>
        /// A route planned so far (<paramref name="on"/>, under <paramref name="labels"/>, <paramref name="map"/> its goals' goals in
        /// <paramref name="best"/>) that stops before the stretch's target (goal <paramref name="target"/>, 0-based), carried on with
        /// the best's own way on after its buy of the target: its inputs up to the goal it last reached, then the best's from that
        /// buy's input tick (the join plays the best's way on from its first quick restart after it, Replan's Join), the goal input
        /// ticks after the target the best's shifted there. Null when it stops before the stretch's start or the best has no such buy.
        /// </summary>
        static Route Onto(Route on, string[] labels, int[] map, int kept, int target, Route best)
        {
            int ot = map[target];
            if (on.Progress < kept || ot < 0 || ot >= best.Progress) return null;
            int cut = (int)Math.Min(on.GoalInput[on.Progress - 1], on.Inputs.Count);
            long from = Math.Min(best.GoalInput[ot], best.Inputs.Count);
            long[] input = new long[labels.Length];
            Array.Copy(on.GoalInput, input, on.Progress);
            for (int j = on.Progress; j < labels.Length; j++) input[j] = map[j] >= ot ? best.GoalInput[map[j]] - from + cut : cut;
            return new Route { Inputs = on.Inputs.Take(cut).Concat(best.Inputs.Skip((int)from)).ToList(), Progress = on.Progress,
                GoalInput = input, GoalPhysics = on.GoalPhysics, GoalHeld = on.GoalHeld, EndReason = on.EndReason };
        }

        /// <summary>A planned list's goals the run's header doesn't have (a box the planner buys that the best never does): their
        /// entries made as an --insert-goal's are (<see cref="NewGoal"/>), so GoalsOf and an adopted header have them.</summary>
        void Known(string[] labels)
        {
            foreach (string l in labels.Distinct())
                if (!l.Contains('+') && !_insertedGoals.ContainsKey(l) && !_runGoals.Goals.Any(g => g.label == l) && NewGoal(l) is { } n)
                    _insertedGoals[l] = (0, n.header, n.goal);
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
            return labels.Select(l => byLabel.TryGetValue(l, out FullTasGoal g) ? g : _insertedGoals.TryGetValue(l, out var x) ? x.goal : Grouped(l, byLabel)).ToArray();
        }

        /// <summary>The goals the trace's header doesn't have, each with its header entry and goal (<see cref="NewGoal"/>): --insert-goal
        /// reward:bonusN@K's (repeatable: put in at goal K, 1-based; the goals from K on move one on) and the cash goals' (at 0: no
        /// forced insert).</summary>
        readonly Dictionary<string, (int at, JsonNode header, FullTasGoal goal)> _insertedGoals = new Dictionary<string, (int, JsonNode, FullTasGoal)>();

        void InsertGoalOption(string option)
        {
            string[] p = option.Split('@');
            if (p.Length != 2 || !p[0].StartsWith("reward:bonus", StringComparison.Ordinal) || !int.TryParse(p[1], out int at))
                throw new ArgumentException($"--insert-goal {option}: reward:bonusN@K");
            var (header, goal) = NewGoal(p[0]) ?? throw new ArgumentException($"--insert-goal {option}: no {p[0].Substring("reward:".Length)} in courses.json, or no upgrade box near its end");
            _insertedGoals[p[0]] = (at, header, goal);
            Log($"auto goals: --insert-goal {p[0]} at goal {at} (box {goal.boxes[0]})");
        }

        /// <summary>A goal the trace's header doesn't have, its entry and goal as the game resolves the label: reward:bonusN's box the one
        /// nearest the bonus's end rect in the config folder's courses.json, buy:token's the token's box (FullTasGoal.Boxes). Null when
        /// there is no such bonus, box or token.</summary>
        (JsonNode header, FullTasGoal goal)? NewGoal(string label)
        {
            if (label.StartsWith("buy:", StringComparison.Ordinal))
            {
                var t = FullTasGoal.Boxes.FirstOrDefault(b => "buy:" + b.token == label);
                if (t.token == null) return null;
                string id = FullTasGoal.BoxId(t.path, t.sibling);
                return (new JsonObject { ["label"] = label, ["kind"] = FullTasGoal.Buy, ["course"] = 0, ["boxes"] = new JsonArray(id) },
                    new FullTasGoal { label = label, kind = FullTasGoal.Buy, boxes = new[] { id } });
            }
            if (!label.StartsWith("reward:bonus", StringComparison.Ordinal)) return null;
            string token = label.Substring("reward:".Length);
            JsonElement c = Bonuses().FirstOrDefault(e => e.GetProperty("name").GetString() == token);
            if (c.ValueKind != JsonValueKind.Object) return null;
            float x = c.GetProperty("endX").GetSingle(), y = c.GetProperty("endY").GetSingle(), w = c.GetProperty("endW").GetSingle(), h = c.GetProperty("endH").GetSingle();
            _eval.Restore(_origin); // the box positions in the start's frame, the rect's (a replay may have rebased the world)
            string box = _eval.Shop.NearestBox(x, y, FullTasGoal.RewardBoxRange);
            if (box == null) return null;
            return (new JsonObject { ["label"] = label, ["kind"] = FullTasGoal.Reward, ["course"] = 0, ["boxes"] = new JsonArray(box), ["rect"] = new JsonArray(x, y, w, h) },
                new FullTasGoal { label = label, kind = FullTasGoal.Reward, boxes = new[] { box }, x = x, y = y, w = w, h = h });
        }

        /// <summary>The config folder's courses.json entries named bonusN with an end rect.</summary>
        List<JsonElement> Bonuses()
        {
            string courses = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(_trace))), "courses.json");
            if (!File.Exists(courses)) return new List<JsonElement>();
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(courses));
            return doc.RootElement.EnumerateArray().Where(e => e.TryGetProperty("name", out JsonElement n) && n.GetString() is string s
                && Regex.IsMatch(s, @"^bonus\d+$") && e.TryGetProperty("endX", out _)).Select(e => e.Clone()).ToList();
        }

        /// <summary>
        /// The --insert-goal edits the run's goals don't have yet: the best's inputs under the list with the reward goal put in. The
        /// old way on doesn't buy its box, so it is re-found as an investment from the goal before it (<see cref="InvestRepair"/>),
        /// as an inserted record lap is. It adds a reward goal to the skeleton, so the game won't adopt the list from a judged
        /// route (<see cref="FullTasGoal.EditRefused"/>): put it in fulltas.json by hand. While one is due a pull offers nothing else.
        /// </summary>
        IEnumerable<GoalEdit> RewardInsertEdits()
        {
            foreach (var (label, (at, _, _)) in _insertedGoals)
            {
                if (_labels.Contains(label) || at < 2 || at > _goals) continue;
                int k = at - 1;
                int[] oldOf = Enumerable.Range(0, k).Append(-1).Concat(Enumerable.Range(k, _goals - k)).ToArray();
                yield return new GoalEdit
                {
                    Kind = "reward", Key = $"reward:{label}@{at}", Text = $"Inserted {label} at goal {at} (before {_labels[k]})", Inputs = _best.Inputs,
                    OldOf = oldOf, Labels = oldOf.Select(o => o < 0 ? label : _labels[o]).ToArray(), First = k, Last = k,
                };
            }
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
            var keys = new HashSet<string>();
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
                            if (r >= 0 && Unbuffered(all, r) && keys.Add($"{k}:{r}:{i}:{n}")) yield return AddLaps(best, k, a, fin, r, GoalsBefore(r), i, j, n);
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
            // Copies of any course's lap, not only the one before the wait: the shortest lap of each course the route laps (from a
            // quick restart with no press buffered across it) put in at the last quick restart before a wait of MinIdle idle ticks
            // or more that lands at that course (the last gate the run crossed before it is the course's), the wait cut. Only at a
            // restart with no press or dash buffered across it: one pressed before it fires after it, into the copy.
            var shortest = new Dictionary<int, (int k, int a, int fin)>();
            for (int k = 0; k + 2 < _goals; k++)
            {
                FullTasGoal s = _runGoals.Goals[k], f = _runGoals.Goals[k + 1];
                if (s.kind != FullTasGoal.Start || f.kind != FullTasGoal.Finish || s.course != f.course) continue;
                int a = (int)Math.Min(best.GoalInput[k], all.Count) - 1;
                while (a >= 0 && !all[a].reset) a--;
                int fin = (int)Math.Min(best.GoalInput[k + 1], all.Count);
                if (a < 0 || k > 0 && a < best.GoalInput[k - 1] || fin <= a || !Unbuffered(all, a)) continue;
                if (!shortest.TryGetValue(s.course, out var l) || fin - a < l.fin - l.a) shortest[s.course] = (k, a, fin);
            }
            int last = (int)Math.Min(best.GoalInput[_goals - 1], all.Count);
            for (int i = 0; i < last;)
            {
                if (!Idle(all[i])) { i++; continue; }
                int j = i;
                while (j < last && Idle(all[j])) j++;
                int r = i - 1;
                while (r >= 0 && !all[r].reset) r--;
                int g0 = 0;
                while (g0 < _goals && best.GoalInput[g0] <= r) g0++;
                int gate = Array.FindLastIndex(_runGoals.Goals, Math.Max(0, g0 - 1), g0, x => x.kind == FullTasGoal.Start);
                // No goal touched from the restart through the wait (a wait that spans a lap's finish touch isn't one).
                if (j - i >= MinIdle && r >= 0 && g0 < _goals && best.GoalInput[g0] >= j && Unbuffered(all, r) && gate >= 0 && shortest.TryGetValue(_runGoals.Goals[gate].course, out var lap))
                    for (int n = 1; n * (lap.fin - lap.a) < j - i; n++)
                        if (keys.Add($"{lap.k}:{r}:{i}:{n}")) yield return AddLaps(best, lap.k, lap.a, lap.fin, r, g0, i, j, n);
                i = j;
            }
        }

        /// <summary>No press or dash in the inputs before input <paramref name="at"/> within the longest buffer window (jump cut's
        /// 15 ticks + 2 slack): nothing pressed before a quick restart there fires after it.</summary>
        static bool Unbuffered(List<InputTick> inputs, int at)
        {
            for (int i = Math.Max(0, at - 17); i < at; i++) if (inputs[i].press || inputs[i].dash) return false;
            return true;
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
                    || !(e.Kind == "insert" || e.Kind == "reward" || e.Kind == "cash" || (e.Kind == "add" || drops && e.Kind == "drop") && AddAhead(e, p, best) || (e.Kind == "payout" || e.Kind == "ladder") && PayoutMayPay(e, p, best))) continue;
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

        /// <summary>The labels of the goals that buy a GLOBAL cashPerLoop box (Shop.GlobalCashAt), the run's and the made ones'
        /// (<see cref="FindCashGoals"/>; a box's kind doesn't change, so the set only grows).</summary>
        readonly HashSet<string> _cashLabels = new HashSet<string>();
        /// <summary>A cash goal moves this many goals earlier and later (<see cref="CashEdits"/>).</summary>
        static readonly int[] CashMoves = { 1, 4, 16 };
        /// <summary>A cash goal the run doesn't have is offered only where the best stands this near its box (world units) at a goal.</summary>
        const float CashNear = 4000f;

        void FindCashGoals()
        {
            foreach (FullTasGoal g in _runGoals.Goals.Concat(_insertedGoals.Values.Select(x => x.goal)))
                if ((g.kind == FullTasGoal.Buy || g.kind == FullTasGoal.Reward) && g.boxes?.Length == 1 && _eval.Shop.GlobalCashAt(g.boxes[0]) != null) _cashLabels.Add(g.label);
        }

        /// <summary>
        /// <see cref="FullTasGoal.EditRefused"/> as the goals part holds its edits to it: the cash goals (<see cref="CashEdits"/>) and
        /// --insert-goal's reward goals aren't the skeleton here, so they may be added, dropped and moved. The game still holds them to
        /// it: it won't adopt a list that adds, drops or moves a reward goal from a judged route (put it in fulltas.json by hand).
        /// </summary>
        string GoalsRefused(IList<string> from, IList<string> to)
        {
            bool Free(string l) => _cashLabels.Contains(l) || l.StartsWith("reward:", StringComparison.Ordinal) && _insertedGoals.ContainsKey(l);
            return FullTasGoal.EditRefused(from.Where(l => !Free(l)).ToList(), to.Where(l => !Free(l)).ToList());
        }

        /// <summary>The best as reached through its first <paramref name="goals"/> goals, numbered for a list of <paramref name="length"/>
        /// that keeps them: the run an edit there is re-found from (<see cref="InvestRepair"/>).</summary>
        static Route BestUpTo(Route best, int goals, int length)
        {
            long[] Cut(long[] a) => a == null ? null : Enumerable.Range(0, length).Select(i => i < goals ? a[i] : -1).ToArray();
            return new Route { Inputs = best.Inputs, Progress = goals, GoalInput = Cut(best.GoalInput), GoalPhysics = Cut(best.GoalPhysics), GoalHeld = Cut(best.GoalHeld),
                EndReason = best.EndReason };
        }

        /// <summary>
        /// Cash goals: a goal that buys a GLOBAL cashPerLoop box (+1 level of every course's reward: bonuses 1-16's and 22's boxes,
        /// buyglobalreward2-5). One taken earlier raises the income of everything after it and costs its detour or price sooner. Each
        /// of the best's is dropped and moved CashMoves goals earlier and later, a buy one bought once more before its first and after
        /// its last; one the run doesn't have (a bonus in courses.json, an economy box) goes in after the goal where the best stands
        /// nearest its box, within CashNear. A reward goal whose detour ends in a quick restart before the next goal is cut out (from
        /// the quick restart before its touch, else the goal before) and, when it starts with one, put in before the quick restart
        /// that starts the step it moves to; those play as spliced. Every other edit keeps the best's inputs, which don't play it, and
        /// is re-found as an investment from the goal before the change (<see cref="GoalEdit.RefindFrom"/>, <see cref="InvestRepair"/>).
        /// </summary>
        List<GoalEdit> CashEdits(Route best)
        {
            var edits = new List<GoalEdit>();
            FindCashGoals();
            List<InputTick> all = best.Inputs;
            int In(int k) => (int)Math.Min(best.GoalInput[k], all.Count);
            // The last quick restart between goal k-1 and goal k (-1: none), and the first after goal k before goal k+1.
            int Before(int k) { for (int i = In(k) - 1; k > 0 && i >= In(k - 1); i--) if (all[i].reset) return i; return -1; }
            int After(int k) { for (int i = In(k); k + 1 < _goals && i < In(k + 1); i++) if (all[i].reset) return i; return -1; }
            // The best's goals with old goal k taken out (-1: none) and a goal put in before old goal j (-1: none), re-found from the
            // first goal the list changes.
            GoalEdit Edit(string key, string text, int k, int j, string label)
            {
                var oldOf = Enumerable.Range(0, _goals).Where(o => o != k).ToList();
                if (j >= 0) oldOf.Insert(oldOf.IndexOf(j), -1);
                string[] labels = oldOf.Select(o => o < 0 ? label : _labels[o]).ToArray();
                int first = 0;
                while (first < Math.Min(labels.Length, _goals) && labels[first] == _labels[first]) first++;
                return new GoalEdit { Kind = "cash", Key = key, Text = text, Inputs = all, OldOf = oldOf.ToArray(), Labels = labels, First = first, Last = first,
                    RefindFrom = Math.Max(1, first) };
            }
            for (int k = 1; k + 1 < _goals; k++)
            {
                string l = _labels[k];
                if (!_cashLabels.Contains(l) || k >= best.Progress) continue;
                bool reward = _runGoals.Goals[k].kind == FullTasGoal.Reward;
                int a = Before(k), b = After(k);
                bool detour = reward && b >= 0;
                edits.Add(detour ? Spliced(best, new List<(int, int, List<InputTick>)> { (a >= 0 ? a : In(k - 1), b, new List<InputTick>()) }, new[] { k }, "cash",
                        $"cash:drop:{k}", $"Dropped goal {k + 1} ({l}; its detour cut)", k, k)
                    : Edit($"cash:drop:{k}", $"Dropped goal {k + 1} ({l})", k, -1, null));
                foreach (int d in CashMoves)
                    foreach (int j in new[] { k - d, k + 1 + d })
                    {
                        if (j < 1 || j >= _goals) continue;
                        string text = $"Moved goal {k + 1} ({l}) {(j < k ? $"{d} goals earlier" : $"{d} goals later")}, before goal {j + 1} ({_labels[j]})";
                        int r = Before(j);
                        if (detour && a >= 0 && r >= 0)
                        {
                            List<InputTick> trip = all.GetRange(a, b - a);
                            GoalEdit s = Spliced(best, new List<(int, int, List<InputTick>)> { (a, b, new List<InputTick>()), (r, r, trip) }, new[] { k }, "cash", $"cash:move:{k}:{j}",
                                text + " (its detour moved)", Math.Min(j, k), Math.Min(j, k));
                            int put = Array.IndexOf(s.OldOf, j);
                            s.OldOf = s.OldOf.Take(put).Append(-1).Concat(s.OldOf.Skip(put)).ToArray();
                            s.Labels = s.OldOf.Select(o => o < 0 ? l : _labels[o]).ToArray();
                            edits.Add(s);
                        }
                        else edits.Add(Edit($"cash:move:{k}:{j}", text, k, j, l));
                    }
            }
            // Another level of each paid one: before its first buy and after its last.
            foreach (var g in Enumerable.Range(1, Math.Max(0, Math.Min(best.Progress, _goals - 1) - 1))
                .Where(k => _cashLabels.Contains(_labels[k]) && _runGoals.Goals[k].kind == FullTasGoal.Buy).GroupBy(k => _labels[k]))
            {
                edits.Add(Edit($"cash:add:{g.Key}:{g.Min()}", $"Bought {g.Key} once more before goal {g.Min() + 1}", -1, g.Min(), g.Key));
                edits.Add(Edit($"cash:add:{g.Key}:{g.Max() + 1}", $"Bought {g.Key} once more after goal {g.Max() + 1}", -1, g.Max() + 1, g.Key));
            }
            // The ones the run doesn't have: the cash bonuses and the economy boxes. Where the best stands at each goal (one replay).
            var missing = Bonuses().Select(c => "reward:" + c.GetProperty("name").GetString())
                .Concat(FullTasGoal.Boxes.Where(b => FullTasGoal.EconomyBoxes.Contains(b.token)).Select(b => "buy:" + b.token))
                .Where(x => !_labels.Contains(x)).ToList();
            var made = new List<(string label, float x, float y)>();
            foreach (string x in missing)
            {
                if (!_insertedGoals.TryGetValue(x, out var entry))
                {
                    if (NewGoal(x) is not { } n) continue;
                    entry = (0, n.header, n.goal);
                }
                _eval.Restore(_origin); // the box in the start's frame
                // A one-off box a reward goal of the run already takes isn't missing.
                if (_eval.Shop.GlobalCashAt(entry.goal.boxes[0]) is not { } p
                    || _runGoals.Goals.Any(g => g.kind == FullTasGoal.Reward && g.boxes?.Contains(entry.goal.boxes[0]) == true)) continue;
                _insertedGoals.TryAdd(x, entry);
                _cashLabels.Add(x);
                ScenePos o = _eval.WorldOriginInScene;
                made.Add((x, p.x - o.x, p.y - o.y));
            }
            if (made.Count == 0) return Changed(best, edits);
            var at = new WorldPos[_goals];
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            int progress = 0;
            _eval.Run(all, null, null, 1000000, _ =>
            {
                for (; progress < _eval.Progress && progress < _goals; progress++) at[progress] = _eval.PlayerWorld;
            });
            // A bonus whose end needs an unlock the run never gets (bonus4's zip movers) can't be taken anywhere in it.
            IGTAPTasMod.Caps end = PlayerCaps.Of(_eval);
            foreach (var (x, bx, by) in made)
            {
                if (x.StartsWith("reward:", StringComparison.Ordinal) && !IGTAPTasMod.Categories.CanFinish(x.Substring("reward:".Length), end)) continue;
                float Far(int k) => (at[k].x - bx) * (at[k].x - bx) + (at[k].y - by) * (at[k].y - by);
                int near = Enumerable.Range(0, Math.Min(progress, _goals - 1)).OrderBy(Far).DefaultIfEmpty(-1).First();
                if (near < 0 || Far(near) > CashNear * CashNear) continue;
                edits.Add(Edit($"cash:add:{x}:{near + 1}", $"Added {x} after goal {near + 1} ({_labels[near]}, {Math.Sqrt(Far(near)):0} from its box)", -1, near + 1, x));
            }
            return Changed(best, edits);
        }

        /// <summary>The edits that change the best's goals or inputs, one of each (a buy moved next to a buy of its own box is the best).</summary>
        List<GoalEdit> Changed(Route best, List<GoalEdit> edits)
        {
            return edits.Where(e => !ReferenceEquals(e.Inputs, best.Inputs) || !e.Labels.SequenceEqual(_labels)).GroupBy(e => (SnapshotIndex.Hash(e.Inputs, e.Inputs.Count), string.Join("|", e.Labels))).Select(g => g.First()).ToList();
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
            // Per course and unlock set the fastest RecordRoutes, and on a two-way course the fastest RecordRoutes of each direction
            // too (the file's "directions", IGTAPTasMod.Categories.Directions): a run reaches the course in states the overall
            // fastest may not play from. Fastest first across unlock sets: a record lap stops after RecordReached routes that play
            // from the restart.
            var picked = new List<(int course, Caps caps, double time, string path, List<InputTick> ticks)>();
            foreach (var g in files.GroupBy(f => (f.course, f.caps.Id)))
            {
                var kept = new Dictionary<string, int>();
                int read = 0;
                foreach (var f in g.OrderBy(f => f.time).GroupBy(f => Path.GetFileName(f.path)).Select(x => x.First()))
                {
                    if (kept.GetValueOrDefault("") >= RecordRoutes && IGTAPTasMod.Categories.Directions.All(d => kept.GetValueOrDefault(d.name) >= RecordRoutes)
                        || ++read > RecordRoutes * 10) break;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f.path), new JsonDocumentOptions { MaxDepth = 256 });
                        if (!doc.RootElement.TryGetProperty("ticks", out JsonElement ticks)) continue;
                        string[] directions = doc.RootElement.TryGetProperty("directions", out JsonElement d) && d.ValueKind == JsonValueKind.Array
                            ? d.EnumerateArray().Select(x => x.GetString()).ToArray() : new string[0];
                        string[] counts = directions.Prepend("").Where(k => kept.GetValueOrDefault(k) < RecordRoutes).ToArray();
                        if (counts.Length == 0) continue;
                        foreach (string k in counts) kept[k] = kept.GetValueOrDefault(k) + 1;
                        picked.Add((f.course, f.caps, f.time, f.path, ticks.EnumerateArray().Select(InputTick.FromJson).ToList()));
                    }
                    catch (Exception ex) when (ex is IOException || ex is JsonException) { }
                }
            }
            foreach (var f in picked.OrderBy(f => f.time))
                _courseRoutes.Add((f.course, f.caps, Path.GetFileNameWithoutExtension(f.path), f.ticks));
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
            Caps has = PlayerCaps.Of(sim);
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
            Caps has = PlayerCaps.Of(sim);
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
            string caps = PlayerCaps.Of(_eval).Id;
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
                if (GoalsRefused(_labels, labels) != null) continue;
                sim.UseGoals(sim.Goals.For(GoalsOf(labels)));
                sim.SetStart(origin, 0);
                sim.Goals.Limit = labels.Length;
                RunResult r = sim.Run(inputs, null, null, 1000000);
                // Progress in the old numbering's terms: a goal put in counts one more, a goal dropped one less.
                int reach = r.Progress - (labels.Length - e.Labels.Length);
                // Level with the stray counts when the run now ends otherwise: a walk that buys a row of boxes early takes one fit a box.
                if ((reach > p || reach == p && IGTAPTasMod.FullTasTracker.Unexplained(r.EndReason) != IGTAPTasMod.FullTasTracker.Unexplained(rr.EndReason)) && (pick.how == null || reach > pick.progress)) pick = (how, labels, o.ToArray(), reach, r.Progress == labels.Length ? "complete" : r.EndReason);
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
            if (!e.Labels.SequenceEqual(_labels) && GoalsRefused(_labels, e.Labels) is string refused)
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
            if (Classed)
            {
                Log($"auto goals: {e.Text} not adopted: a {Glitches.Key(_class)} solve keeps the run's goals");
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

        /// <summary>The run's goals become these: the trace's header with this goal list (<see cref="GoalsHeader"/>) makes every
        /// simulation from here on, the loop's own included.</summary>
        void SwitchGoals(string[] labels)
        {
            JsonElement header = GoalsHeader(AutoHeader(), labels, l => _insertedGoals.TryGetValue(l, out var x) ? x.header : null);
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

        /// <summary>A Full-TAS trace's header with this goal list: each goal's own entry from the header (else
        /// <paramref name="inserted"/>'s, an --insert-goal's), a grouped buy (buy:a+b) made of its boxes' entries.</summary>
        internal static JsonElement GoalsHeader(JsonElement header, string[] labels, Func<string, JsonNode> inserted = null)
        {
            JsonNode node = JsonNode.Parse(header.GetRawText());
            JsonArray old = node["fulltas"]["goals"].AsArray();
            var byLabel = new Dictionary<string, JsonNode>();
            foreach (JsonNode g in old) byLabel.TryAdd(g["label"].GetValue<string>(), g);
            var goals = new JsonArray();
            foreach (string l in labels)
            {
                if (byLabel.TryGetValue(l, out JsonNode g)) { goals.Add(g.DeepClone()); continue; }
                if (inserted?.Invoke(l) is JsonNode x) { goals.Add(x.DeepClone()); continue; }
                string[] parts = l.Substring("buy:".Length).Split('+');
                JsonNode first = byLabel["buy:" + parts[0]].DeepClone();
                first["label"] = l;
                first["boxes"] = new JsonArray(parts.SelectMany(p => byLabel["buy:" + p]["boxes"].AsArray().Select(b => (JsonNode)b.GetValue<string>())).ToArray());
                goals.Add(first);
            }
            node["fulltas"]["goals"] = goals;
            using JsonDocument doc = JsonDocument.Parse(node.ToJsonString(), new JsonDocumentOptions { MaxDepth = 256 });
            return doc.RootElement.Clone();
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
            if (goals.SequenceEqual(_labels) || mine < 0) return;
            // A reward or single buy goal the trace doesn't have came from --insert-goal or a cash goal edit (the loop adopts those past
            // EditRefused: GoalsRefused; the trace's header has no entry for it): its entry is made again as they make it. Anything else
            // that changes the skeleton stays refused.
            string[] added = goals.Distinct().Where(l => !_labels.Contains(l) && (l.StartsWith("reward:bonus", StringComparison.Ordinal)
                || l.StartsWith("buy:", StringComparison.Ordinal) && !l.Contains('+'))).ToArray();
            var made = new List<string>();
            foreach (string l in added)
            {
                if (_insertedGoals.ContainsKey(l)) continue;
                if (NewGoal(l) is not { } n)
                {
                    foreach (string m in made) _insertedGoals.Remove(m);
                    Log($"auto goals: fulltas/{Path.GetFileName(_runFile)}'s adopted goals add {l}, whose goal can't be made again; the trace's goals stay");
                    return;
                }
                _insertedGoals[l] = (0, n.header, n.goal);
                made.Add(l);
            }
            FindCashGoals();
            if (GoalsRefused(_labels, goals) is string refused)
            {
                foreach (string m in made) _insertedGoals.Remove(m);
                Log($"auto goals: fulltas/{Path.GetFileName(_runFile)}'s adopted goals ({goals.Length}) aren't an edit of the trace's ({refused}); the trace's goals stay");
                return;
            }
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
            foreach (string l in made) _insertedGoals.Remove(l); // not an --insert-goal the loop was asked for
            RebindEval();
            Log($"auto goals: fulltas/{Path.GetFileName(_runFile)}'s adopted goals ({goals.Length}) replay as {r.Progress}/{goals.Length} goals"
                + (r.Progress == goals.Length ? $" ({Describe(r)}), no faster than the trace's best" : "") + "; the trace's goals stay");
        }
    }
}
