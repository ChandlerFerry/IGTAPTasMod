using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        /// <summary>A planned goal list with its route's inputs.</summary>
        sealed class GoalEdit
        {
            public string Kind, Text, Key;
            public string[] Labels;
            /// <summary>The inputs: from <see cref="NewAt"/> on they are the best's from <see cref="OldAt"/> on (a stray rescue's change).</summary>
            public List<InputTick> Inputs;
            public int NewAt, OldAt;
            /// <summary>Each new goal's goal in the best (0-based; -1: a goal the best doesn't have).</summary>
            public int[] OldOf;
            /// <summary>Old goal numbers (1-based) the edit touches, for the log.</summary>
            public int First, Last;
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
        /// <summary>A goals pull plans again only after a new best and this long since the last pull, or at once after it adopted
        /// a list.</summary>
        const double GoalsEveryMinutes = 5;
        const int MaxPadFixes = 6;

        /// <summary>--stretch-minutes: a goals pull's time a stretch (Replan's four searches, realize between them, the join).
        /// FalseEnding%-280 (2026-10-04, beam 200): buy:buywj to buy:buyds searches about 11 minutes a round, reward:bonus3 to
        /// buy:buystatue 19.</summary>
        double _stretchMinutes = 60;
        /// <summary>The stretches planned to the end (the route planned from, to its start, the target and --stretch-minutes; only
        /// a Replan that is <see cref="PlanResult.Complete"/>: one that timed out, threw or was cut short is planned again), kept in
        /// the memory with the movement table's stamp then (<see cref="MovesStamp"/>): a pull skips them until the table changes.</summary>
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
                // Planned to the end before from this route with these moves and minutes (an earlier pull or session): it plans the
                // same way again.
                string key = Hex(SnapshotIndex.Hash(from.Inputs, (int)Math.Min(from.GoalInput[kept - 1], from.Inputs.Count))) + ":" + labels[t]
                    + ":" + _stretchMinutes.ToString(CultureInfo.InvariantCulture);
                if (_stretchPulled.Contains(key))
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
                if (p.Complete) _stretchPulled.Add(key);
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
        /// entries made as the planner's are (<see cref="NewGoal"/>), so GoalsOf and an adopted header have them.</summary>
        void Known(string[] labels)
        {
            foreach (string l in labels.Distinct())
                if (!l.Contains('+') && !_insertedGoals.ContainsKey(l) && !_runGoals.Goals.Any(g => g.label == l) && NewGoal(l) is { } n)
                    _insertedGoals[l] = (0, n.header, n.goal);
        }

        /// <summary>The goals of a label list: the run's own goal objects, and for a set of boxes the run buys one by one (buy:a+b),
        /// one goal for them all.</summary>
        FullTasGoal[] GoalsOf(string[] labels)
        {
            var byLabel = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in _runGoals.Goals) byLabel.TryAdd(g.label, g);
            return labels.Select(l => byLabel.TryGetValue(l, out FullTasGoal g) ? g : _insertedGoals.TryGetValue(l, out var x) ? x.goal : Grouped(l, byLabel)).ToArray();
        }

        /// <summary>The goals the trace's header doesn't have, each with its header entry and goal (<see cref="NewGoal"/>): the buys and
        /// reward goals the planner took that the run doesn't have (at 0).</summary>
        readonly Dictionary<string, (int at, JsonNode header, FullTasGoal goal)> _insertedGoals = new Dictionary<string, (int, JsonNode, FullTasGoal)>();

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

        static bool Idle(InputTick t) { return t.Equals(new InputTick { held = t.held }) && t.x == 0f && t.y == 0f; }

        /// <summary>The labels of the goals that buy a GLOBAL cashPerLoop box (Shop.GlobalCashAt), the run's and the made ones'
        /// (<see cref="FindCashGoals"/>; a box's kind doesn't change, so the set only grows).</summary>
        readonly HashSet<string> _cashLabels = new HashSet<string>();

        void FindCashGoals()
        {
            foreach (FullTasGoal g in _runGoals.Goals.Concat(_insertedGoals.Values.Select(x => x.goal)))
                if ((g.kind == FullTasGoal.Buy || g.kind == FullTasGoal.Reward) && g.boxes?.Length == 1 && _eval.Shop.GlobalCashAt(g.boxes[0]) != null) _cashLabels.Add(g.label);
        }

        /// <summary>
        /// <see cref="FullTasGoal.EditRefused"/> as the goals part holds its edits to it: the cash goals and
        /// the reward goals the planner took aren't the skeleton here, so they may be added, dropped and moved. The game still holds them to
        /// it: it won't adopt a list that adds, drops or moves a reward goal from a judged route (put it in fulltas.json by hand).
        /// </summary>
        string GoalsRefused(IList<string> from, IList<string> to)
        {
            bool Free(string l) => _cashLabels.Contains(l) || l.StartsWith("reward:", StringComparison.Ordinal) && _insertedGoals.ContainsKey(l);
            return FullTasGoal.EditRefused(from.Where(l => !Free(l)).ToList(), to.Where(l => !Free(l)).ToList());
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
                lead = Math.Max(lead, -waited);
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
        /// <paramref name="inserted"/>'s, a planned goal's), a grouped buy (buy:a+b) made of its boxes' entries.</summary>
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
            // A reward or single buy goal the trace doesn't have came from a planned list (the loop adopts those past
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
            foreach (string l in made) _insertedGoals.Remove(l); // not goals the loop planned
            RebindEval();
            Log($"auto goals: fulltas/{Path.GetFileName(_runFile)}'s adopted goals ({goals.Length}) replay as {r.Progress}/{goals.Length} goals"
                + (r.Progress == goals.Length ? $" ({Describe(r)}), no faster than the trace's best" : "") + "; the trace's goals stay");
        }
    }
}
