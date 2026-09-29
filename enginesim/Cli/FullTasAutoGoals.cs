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
        }

        /// <summary>One lap of the best: goals K (start:N) and K+1 (finish:N) (0-based), played from the quick restart at input A
        /// to the input before the quick restart at B that starts the step after it.</summary>
        sealed class RunLap
        {
            public int K, A, B, Course;
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
        const int MaxPadFixes = 6, RecordRoutes = 12, RecordReached = 4;

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
            _pullNo++;
            int pull = _pullNo;
            List<RunLap> laps = Laps(best);
            var edits = new List<GoalEdit>();
            edits.AddRange(DropEdits(best, laps));
            edits.AddRange(AddEdits(best, laps));
            edits.AddRange(RegroupEdits(best));
            List<GoalEdit> records = RecordEdits(best, laps);
            edits.AddRange(records);
            edits.RemoveAll(e => FullTasGoal.EditRefused(_labels, e.Labels) != null);
            Log($"auto: pull {pull}: goals, {edits.Count} goal-list edits of {laps.Count} laps ({edits.Count(e => e.Kind == "drop")} drops, {edits.Count(e => e.Kind == "add")} adds, "
                + $"{edits.Count(e => e.Kind == "regroup")} regroups, {records.Count} record laps), each played on a copy with waits before the quick restarts it needs");
            var results = new (Route route, string why)[edits.Count];
            RunOnThreads(edits.Count, (sim, origin, tracker, i) =>
            {
                GoalEdit e = edits[i];
                sim.UseGoals(tracker.For(GoalsOf(e.Labels)));
                results[i] = Played(sim, origin, e, best);
            });
            (GoalEdit edit, Route route) win = default;
            for (int i = 0; i < edits.Count; i++)
            {
                (Route r, string why) = results[i];
                GoalEdit e = edits[i];
                string verdict = r == null ? why ?? "not played" : $"{(r.Last * _dt).ToString("0.00", CultureInfo.InvariantCulture)}s ({r.Last - best.Last:+0;-0} ticks){(why == null ? "" : ", " + why)}";
                Log($"auto goals: {e.Text}: {verdict}");
                AutoWrite(new { type = "goalEdit", pull, kind = e.Kind, text = e.Text, first = e.First, last = e.Last, goals = e.Labels.Length,
                    time = r == null ? (double?)null : Seconds(r.Last), gain = r == null ? (long?)null : best.Last - r.Last, why });
                if (r != null && r.Last < best.Last && (win.route == null || r.Last < win.route.Last)) win = (e, r);
            }
            bool adopted = false;
            if (win.route != null) adopted = AdoptGoals(win.edit, win.route, best);
            string outcome = win.route == null ? $"no edit of {edits.Count} beats the best" : $"{win.edit.Text}: {Describe(win.route)}";
            _goalsAgain = adopted;
            double cpu = CpuSeconds() - c0, wall = (_clock.Elapsed - w0).TotalSeconds;
            long gain = Math.Max(0, best.Last - _best.Last);
            _order.Add("goals" + (gain > 0 ? $"(-{gain})" : ""));
            Log($"auto: pull {pull} goals: {outcome}; {(gain > 0 ? $"GAINED {gain} ticks" : "no gain")} in {cpu:0} CPU s, {wall:0} s; best {Describe(_best)}");
            AutoWrite(new { type = "pull", pull, lever = "goals", goal = win.edit?.First ?? 0, label = "goals", budget = 0, outcome, gain, cpu = Math.Round(cpu, 1),
                wall = Math.Round((_clock.Elapsed - _autoWall0).TotalSeconds, 1), best = Seconds(_best.Last), score = double.NaN });
            if (gain > 0)
            {
                _curve.Add(((_clock.Elapsed - _autoWall0).TotalSeconds, CpuSeconds() - _autoCpu0, _best.Last, "goals", win.edit.First));
                AutoWrite(new { type = "record", lever = "goals", goal = win.edit.First, gain, time = Seconds(_best.Last), ticks = _best.Last,
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
            int next = -1;
            var threads = new List<Thread>();
            for (int t = 0; t < Math.Min(_threads, count); t++)
            {
                var thread = new Thread(() =>
                {
                    Simulation sim = _create();
                    SimSnapshot origin = sim.Start;
                    FullTasTracker tracker = sim.Goals;
                    for (int i; (i = Interlocked.Increment(ref next)) < count && !TimeUp;)
                        try { work(sim, origin, tracker, i); }
                        catch (Exception ex) { Log($"auto goals: edit {i + 1} failed: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"); }
                }, 64 << 20);
                thread.Start();
                threads.Add(thread);
            }
            foreach (Thread t in threads) t.Join();
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
        GoalEdit Drop(Route best, List<RunLap> drop, string kind, string text, List<InputTick> head = null, int headAt = 0, int oldFrom = 0)
        {
            var cut = new HashSet<int>(drop.SelectMany(l => new[] { l.K, l.K + 1 }));
            var inputs = new List<InputTick>(best.Inputs.Count);
            int newAt = 0, oldAt = oldFrom;
            if (head != null) { inputs.AddRange(head); newAt = headAt; }
            foreach (RunLap l in drop.OrderBy(l => l.A))
            {
                inputs.AddRange(best.Inputs.Skip(oldAt).Take(l.A - oldAt));
                newAt = inputs.Count;
                oldAt = l.B;
            }
            inputs.AddRange(best.Inputs.Skip(oldAt));
            int[] oldOf = Enumerable.Range(0, _goals).Where(k => !cut.Contains(k)).ToArray();
            return new GoalEdit
            {
                Kind = kind, Text = text, Inputs = inputs, NewAt = newAt, OldAt = oldAt, OldOf = oldOf,
                Labels = oldOf.Select(k => _labels[k]).ToArray(), Saves = drop.Sum(l => (long)(l.B - l.A)),
                First = drop.Min(l => l.K) + 1, Last = drop.Max(l => l.K) + 2,
                Key = kind + ":" + string.Join(",", drop.Select(l => l.K)),
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
        /// Adds: where the route stands idle before its next buy (20 ticks or more after a run of laps), one more lap of that
        /// course in place of as many of those idle ticks (a lap earns its reward and sets no worse a path; standing earns nothing).
        /// </summary>
        IEnumerable<GoalEdit> AddEdits(Route best, List<RunLap> laps)
        {
            const int MinIdle = 20;
            foreach (List<RunLap> block in Blocks(laps))
            {
                RunLap l = block[block.Count - 1];
                int next = l.K + 2;
                if (next >= _goals) continue;
                int end = (int)Math.Min(best.GoalInput[next], best.Inputs.Count);
                // The longest run of idle inputs (the stick at rest, no press) between the lap and that goal.
                int bestAt = -1, bestLen = 0;
                for (int i = l.B; i < end;)
                {
                    if (!Idle(best.Inputs[i])) { i++; continue; }
                    int j = i;
                    while (j < end && Idle(best.Inputs[j])) j++;
                    if (j - i > bestLen) { bestAt = i; bestLen = j - i; }
                    i = j;
                }
                if (bestLen < MinIdle) continue;
                int lap = l.B - l.A, rm = Math.Min(bestLen, lap);
                var inputs = best.Inputs.Take(l.B).Concat(best.Inputs.Skip(l.A).Take(lap)).Concat(best.Inputs.Skip(l.B).Take(bestAt - l.B))
                    .Concat(best.Inputs.Skip(bestAt + rm)).ToList();
                int[] oldOf = Enumerable.Range(0, l.K + 2).Concat(new[] { -1, -1 }).Concat(Enumerable.Range(l.K + 2, _goals - l.K - 2)).ToArray();
                yield return new GoalEdit
                {
                    Kind = "add", Key = $"add:{l.K}", Inputs = inputs, NewAt = bestAt + lap, OldAt = bestAt + rm, OldOf = oldOf,
                    Labels = oldOf.Select((k, i) => k >= 0 ? _labels[k] : _labels[l.K + (i - l.K - 2)]).ToArray(), Saves = rm - lap,
                    First = l.K + 1, Last = l.K + 2,
                    Text = $"Added a Course {l.Course} lap after goal {l.K + 2} in place of {rm} of the {bestLen} idle ticks before goal {next + 1} ({_labels[next]})",
                };
            }
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
        /// Record laps: a lap of the best replaced by a quick restart, a few idle ticks and a saved course route played from standing
        /// at the course's reset point (offline-best, its judged routes included), when that lap leaves the course a faster best
        /// time: the clones' path, so every clone pays more from then on. Costs ticks once; pays when the laps the extra income
        /// makes unneeded go: each is offered with the last 0-3 laps of the course's run of laps before the stretch's next unlock
        /// or area goal dropped. At the course's last lap before its first clone buy, and at its first lap after one.
        /// </summary>
        List<GoalEdit> RecordEdits(Route best, List<RunLap> laps)
        {
            var edits = new List<GoalEdit>();
            LoadCourseRoutes();
            if (_courseRoutes.Count == 0) return edits;
            var spots = new List<RunLap>();
            foreach (int course in laps.Select(l => l.Course).Distinct())
            {
                if (!_courseRoutes.Any(r => r.course == course)) continue;
                string clone = course == 1 ? "buyclone" : "buyclone" + course;
                int firstClone = Array.FindIndex(_labels, l => l.StartsWith("buy:", StringComparison.Ordinal) && l.Substring(4).Split('+').Contains(clone));
                RunLap before = laps.LastOrDefault(l => l.Course == course && (firstClone < 0 || l.K < firstClone));
                RunLap after = firstClone < 0 ? null : laps.FirstOrDefault(l => l.Course == course && l.K > firstClone);
                foreach (RunLap l in new[] { before, after }) if (l != null && !spots.Contains(l)) spots.Add(l);
            }
            if (spots.Count == 0) return edits;
            var found = new (List<InputTick> lap, int fin, string how)?[spots.Count];
            var why = new string[spots.Count];
            RunOnThreads(spots.Count, (sim, origin, tracker, i) =>
            {
                sim.UseGoals(tracker.For(_runGoals.Goals));
                found[i] = RecordLapOf(sim, origin, best, spots[i], out why[i]);
            });
            List<List<RunLap>> blocks = Blocks(laps);
            for (int i = 0; i < spots.Count; i++)
            {
                if (found[i] == null)
                {
                    Log($"auto goals: no record lap at goals {spots[i].K + 1}-{spots[i].K + 2} (Course {spots[i].Course}): {why[i] ?? "not played"}");
                    continue;
                }
                RunLap l = spots[i];
                var (lapInputs, fin, how) = found[i].Value;
                int oldAfter = (int)Math.Min(best.GoalInput[l.K + 1], best.Inputs.Count);
                List<InputTick> head = best.Inputs.Take(l.A).Concat(lapInputs).ToList();
                string text = $"Record Course {l.Course} lap at goals {l.K + 1}-{l.K + 2} ({how})";
                // The course's last run of laps before the next goal that isn't economy (an unlock buy, an area or reward goal).
                int stop = Enumerable.Range(l.K + 2, _goals - l.K - 2).FirstOrDefault(k => !FullTasGoal.IsEconomy(_labels[k]), _goals);
                List<RunLap> block = blocks.LastOrDefault(b => b[0].Course == l.Course && b[0].K > l.K + 1 && b[b.Count - 1].K + 1 < stop);
                for (int m = 0; m <= Math.Min(3, block?.Count ?? 0); m++)
                {
                    if (m == 0)
                    {
                        var inputs = head.Concat(best.Inputs.Skip(oldAfter)).ToList();
                        edits.Add(new GoalEdit
                        {
                            Kind = "record", Key = $"record:{l.K}", Text = text, Inputs = inputs, NewAt = head.Count, OldAt = oldAfter,
                            OldOf = Enumerable.Range(0, _goals).ToArray(), Labels = _labels.ToArray(), Saves = oldAfter - l.A - lapInputs.Count, First = l.K + 1, Last = l.K + 2,
                        });
                        continue;
                    }
                    List<RunLap> drop = block.Skip(block.Count - m).ToList();
                    GoalEdit e = Drop(best, drop, "record", $"{text}; dropped {Plural(m, $"Course {l.Course} lap")} ({Goals(drop)})", head, head.Count, oldAfter);
                    e.Key = $"record:{l.K}:" + string.Join(",", drop.Select(d => d.K));
                    e.First = l.K + 1;
                    e.Saves += oldAfter - l.A - lapInputs.Count;
                    edits.Add(e);
                }
            }
            return edits;
        }

        /// <summary>The saved course routes (offline-best and offline-best/judged beside the trace: &lt;course&gt;_&lt;caps&gt;-&lt;time&gt;[-human].json),
        /// the fastest RecordRoutes per course and unlock set.</summary>
        void LoadCourseRoutes()
        {
            if (_courseRoutes != null) return;
            _courseRoutes = new List<(int, Caps, string, List<InputTick>)>();
            var name = new Regex(@"^(\d+)_([a-z0-9]+)-(\d+(?:\.\d+)?)(-human)?\.json$");
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
        /// Lap l replayed as a quick restart, 0-MaxRecordPad idle ticks and each saved route of its course whose unlocks the player
        /// has there (the first pad that reaches the lap's finish); the one that leaves the course the fastest best time (then the
        /// soonest finish) when that beats the best's own lap. Returns the lap's inputs (from the restart to its finish goal's input),
        /// or null.
        /// </summary>
        (List<InputTick> lap, int fin, string how)? RecordLapOf(Simulation sim, SimSnapshot origin, Route best, RunLap l, out string why)
        {
            int finishGoal = l.K + 2;
            why = null;
            sim.SetStart(origin, 0);
            sim.Goals.Limit = finishGoal;
            SimSnapshot at = sim.Advance(best.Inputs.GetRange(0, l.A));
            if (at == null) { why = $"the best ends before input {l.A}"; return null; }
            sim.Restore(at);
            Caps caps = PlayerCaps.Of(sim.Player);
            float Path() => (float)BestPathTime.GetValue(sim.Courses.First(c => c.courseNumber == l.Course));
            sim.SetStart(at, l.A);
            RunResult old = sim.Run(best.Inputs.GetRange(l.A, (int)best.GoalInput[l.K + 1] - l.A), null, null, 400);
            if (old.Progress < finishGoal) { why = $"the best's own lap replays to goal {old.Progress} ({old.EndReason})"; return null; }
            float before = Path();
            (List<InputTick> lap, int fin, float path, long tick, string how)? pick = null;
            InputTick restart = best.Inputs[l.A];
            var tried = new List<string>();
            int reached = 0;
            foreach (var r in _courseRoutes.Where(r => r.course == l.Course && r.caps.IsSubsetOf(caps)))
            {
                string end = null;
                for (int pad = 0; pad <= MaxRecordPad; pad++)
                {
                    var lap = new List<InputTick>(1 + pad + r.inputs.Count) { restart };
                    for (int i = 0; i < pad; i++) lap.Add(new InputTick());
                    lap.AddRange(r.inputs);
                    sim.SetStart(at, l.A);
                    sim.Goals.Limit = finishGoal;
                    RunResult rr = sim.Run(lap, null, null, 400);
                    if (rr.Progress < finishGoal) { end ??= $"{rr.Progress - l.K} of the lap's 2 goals ({rr.EndReason})"; continue; }
                    int fin = (int)rr.GoalInputTicks[finishGoal - 1];
                    float path = Path();
                    end = $"{path:0.00}s after {pad} idle ticks";
                    reached++;
                    if (pick == null || path < pick.Value.path || path == pick.Value.path && rr.GoalPhysicsTicks[finishGoal - 1] < pick.Value.tick)
                        pick = (lap.GetRange(0, fin - l.A), fin, path, rr.GoalPhysicsTicks[finishGoal - 1], $"{r.name} after {pad} idle ticks, best time {before:0.00}s -> {path:0.00}s");
                    break;
                }
                tried.Add($"{r.name}: {end}");
                // The routes come fastest first: the first few that play set the best time a record lap can.
                if (reached == RecordReached) break;
            }
            if (pick != null && pick.Value.path < before) return (pick.Value.lap, pick.Value.fin, pick.Value.how);
            why = $"best time {before:0.00}s ({caps.Label}); " + (tried.Count == 0 ? "no saved route of these unlocks" : string.Join("; ", tried));
            return null;
        }

        // ---------------------------------------------------------------- playing an edit

        /// <summary>
        /// The edit played from the start under its goals. Where it misses a goal after the change (a buy it reaches before the
        /// Cash is there, or a walk that runs on and buys a neighbouring box instead), idle ticks go before the last quick restart
        /// ahead of that goal: the fewest that pass it (every 5 up to the lead the edit had there plus 60, then each below the first
        /// that passes: a stray buy isn't monotone in the wait), at most MaxPadFixes times. Returns the whole run, or null and why.
        /// </summary>
        (Route route, string why) Played(Simulation sim, SimSnapshot origin, GoalEdit e, Route best)
        {
            int n = e.Labels.Length;
            List<InputTick> inputs = e.Inputs;
            int newAt = e.NewAt, oldAt = e.OldAt;
            var waits = new List<string>();
            long waited = 0;
            for (int fix = 0; ; fix++)
            {
                sim.SetStart(origin, 0);
                sim.Goals.Limit = n;
                RunResult rr = sim.Run(inputs, null, null, 1000000);
                if (rr.Progress == n)
                    return (new Route { Inputs = inputs, Progress = n, GoalInput = rr.GoalInputTicks, GoalPhysics = rr.GoalPhysicsTicks, EndReason = rr.EndReason },
                        waits.Count == 0 ? null : "waits " + string.Join(", ", waits));
                int g = rr.Progress + 1;
                string missed = $"misses goal {g} ({e.Labels[g - 1]}: {rr.EndReason})";
                if (fix == MaxPadFixes) return (null, $"{missed} after {MaxPadFixes} waits");
                int og = e.OldOf[g - 1];
                if (og < 0 || best.GoalInput[og] <= oldAt) return (null, missed + ", inside the change");
                int ni = (int)(best.GoalInput[og] - oldAt + newAt);
                int r = -1;
                for (int i = Math.Min(ni, inputs.Count) - 1; i >= newAt; i--) if (inputs[i].reset) { r = i; break; }
                if (r < 0) return (null, missed + "; no quick restart after the change to wait at");
                // Up to what the edit is ahead: at the goal before the one it misses, or (when that goal is before the change) the
                // ticks the splice took out less the waits so far.
                long lead = g >= 2 && e.OldOf[g - 2] >= 0 ? best.GoalPhysics[e.OldOf[g - 2]] - rr.GoalPhysicsTicks[g - 2] : 0;
                lead = Math.Max(lead, e.Saves - waited);
                int max = (int)Math.Clamp(lead + 60, 60, 900);
                sim.SetStart(origin, 0);
                sim.Goals.Limit = n;
                SimSnapshot at = sim.Advance(inputs.GetRange(0, r));
                if (at == null) return (null, missed + $"; the route ends before input {r}");
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
                if (wait < 0) return (null, missed + $"; no wait of up to {max} idle ticks before the quick restart at input {r} passes it");
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
            long[] physics = e.OldOf.Select((k, i) => k >= 0 ? old.GoalPhysics[k] : old.GoalPhysics[Math.Max(0, e.OldOf.Take(i).LastOrDefault(x => x >= 0))]).ToArray();
            physics[physics.Length - 1] = old.End;
            var standIn = new Route { Inputs = old.Inputs, Progress = _goals, GoalInput = Enumerable.Repeat((long)old.Inputs.Count, _goals).ToArray(), GoalPhysics = physics, EndReason = old.EndReason };
            _best = standIn;
            _goalLineage.Add(from);
            string words = $"{e.Text}: -{((old.Last - route.Last) * _dt).ToString("0.00", CultureInfo.InvariantCulture)} s";
            _goalsEdit = words;
            try { Commit(route, "goals: " + words); }
            finally { _goalsEdit = null; }
            if (ReferenceEquals(_best, standIn))
            {
                _goalLineage.RemoveAt(_goalLineage.Count - 1);
                (_autoHeader, _create, _eval, _origin, _runGoals, _labels, _goals, _best) = saved;
                Log($"auto goals: {e.Text}: the fresh check didn't keep it; the goals stay");
                return false;
            }
            Log($"auto goals: ADOPTED {e.Text}: {from.Length} -> {_goals} goals, {(old.Last * _dt).ToString("0.00", CultureInfo.InvariantCulture)}s -> {Describe(_best)}");
            AutoWrite(new { type = "goals", text = words, from = from.Length, to = _goals, goals = _labels, time = Seconds(_best.Last), gain = old.Last - _best.Last });
            RestartOnGoals();
            return true;
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

        /// <summary>After adopted goals: every goal-numbered piece of the loop starts again from the new best (as a new --auto would,
        /// without the speed probes, which are per unlock set).</summary>
        void RestartOnGoals()
        {
            _refs.Clear(); _refKeys.Clear(); _arms.Clear(); _grafted.Clear(); _studied.Clear(); _planDead.Clear(); _polished.Clear();
            _polishedBest = null; _combineTried.Clear(); _reviveTried.Clear(); _combinedFor = null; _revivedFor = null; _mergedOn = null; _merged.Clear();
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
            if (r.Progress == _goals && (saved.best.Progress < saved.goals || r.Last < saved.best.Last))
            {
                _goalLineage.AddRange(from.Skip(mine));
                _best = r;
                Log($"auto goals: fulltas/{Path.GetFileName(_runFile)} has goals adopted from the trace's ({trace.Length} -> {_goals} goals) at {Describe(r)}: the loop runs on them");
                return;
            }
            (_autoHeader, _create, _eval, _origin, _runGoals, _labels, _goals, _best) = saved;
            Log($"auto goals: fulltas/{Path.GetFileName(_runFile)}'s adopted goals ({goals.Length}) replay as {r.Progress}/{goals.Length} goals"
                + (r.Progress == goals.Length ? $" ({Describe(r)}), no faster than the trace's best" : "") + "; the trace's goals stay");
        }
    }
}
