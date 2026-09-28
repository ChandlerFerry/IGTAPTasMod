using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using FullTasTracker = IGTAPTasMod.FullTasTracker;

namespace IGTAP.EngineSim.Cli
{
    // Reset segments (docs/fulltas.md, "Reset segments"): the route cut at its quick restarts. A segment is polished on its own:
    // its goals are searched from the state after the restart that opens it, the way found ends with the restart that closed the
    // old segment, and the old route after that restart is kept as it is. That is only sound when the player leaves the new
    // restart exactly as it left the old one, so every candidate's state after its restart tick is compared with the old one's,
    // field by field; one that differs is dropped, never re-solved.
    sealed partial class FullTas
    {
        /// <summary>
        /// The route between two quick restarts: inputs Start..Reset-1 (Start is 0 or the input after the restart that opens it),
        /// closed by the restart at input Reset (-1: the last segment, closed by the route's end). First and Last are the goals
        /// reached before its first input and before its closing restart.
        /// </summary>
        sealed class Segment
        {
            public int Number, Start, Reset, First, Last;
        }

        /// <summary>The player as a quick restart leaves it: every gameplay field, after the restart's own tick.</summary>
        sealed class Carried
        {
            public string[] Names;
            public object[] Values;
        }

        static List<Segment> Segments(Route r)
        {
            var list = new List<Segment>();
            int start = 0;
            int Goals(int inputs)
            {
                int n = 0;
                while (n < r.Progress && r.GoalInput[n] <= inputs) n++;
                return n;
            }
            int end = r.Progress == 0 ? 0 : (int)Math.Min(r.GoalInput[r.Progress - 1], r.Inputs.Count);
            for (int i = 1; i < end; i++)
            {
                if (!r.Inputs[i].reset) continue;
                list.Add(new Segment { Number = list.Count + 1, Start = start, Reset = i, First = Goals(start), Last = Goals(i) });
                start = i + 1;
            }
            list.Add(new Segment { Number = list.Count + 1, Start = start, Reset = -1, First = Goals(start), Last = r.Progress });
            return list;
        }

        static readonly FieldInfo[] CarriedFields = IGTAPTasMod.FieldState.For(typeof(Movement));

        /// <summary>The player now (inside a run's afterTick, or after a restore).</summary>
        Carried CarriedNow()
        {
            SimSnapshot s = _eval.Capture(0);
            var names = new List<string>();
            var values = new List<object>();
            for (int i = 0; i < CarriedFields.Length; i++)
            {
                if (Simulation.MovementPresentationFields.Contains(CarriedFields[i].Name)) continue;
                names.Add(CarriedFields[i].Name);
                values.Add(s.Movement[i]);
            }
            void Add(string name, object value) { names.Add(name); values.Add(value); }
            Add("position (world)", _eval.PlayerWorld);
            Add("floating origin", _eval.CurrentOrigin);
            Add("physics body", _eval.Physics.PlayerBody());
            Add("interpolation ends", (s.StepFrom, s.StepTo));
            Add("transform scale", s.LocalScale);
            Add("transform rotation", s.Rotation);
            Add("player colliders", s.Colliders);
            Add("animator Animation", s.Animation);
            // Invokes and coroutines on the player (Invoke("cancelDashBuffer"), a respawn pending), by how many ticks they're due in.
            Add("player invokes", s.Pending.Where(p => ReferenceEquals(p.target, _eval.Player)).Select(p => p.method + " in " + p.due + (p.repeat > 0f ? " every " + p.repeat : "")).ToArray());
            Add("player coroutines", s.Routines.Where(p => ReferenceEquals(p.target, _eval.Player)).Select(p => p.body?.GetType().Name + " " + p.wait + " in " + p.due).ToArray());
            return new Carried { Names = names.ToArray(), Values = values.ToArray() };
        }

        /// <summary>The state after each of these restarts (input indices) in one replay of the inputs from the run's start.</summary>
        Dictionary<int, Carried> ResetStates(List<InputTick> inputs, ICollection<int> resets)
        {
            var states = new Dictionary<int, Carried>();
            if (resets.Count == 0) return states;
            var after = new HashSet<int>(resets.Select(r => r + 1));
            int last = resets.Max() + 1;
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs, null, tick =>
            {
                if (after.Contains(tick)) states[tick - 1] = CarriedNow();
                return tick < last;
            }, 1000000);
            return states;
        }

        /// <summary>The state after playing more from at (the route's first length inputs), its last input the restart; null when the run ends first.</summary>
        Carried CarriedAfter(SimSnapshot at, int length, List<InputTick> more)
        {
            _eval.SetStart(at, length);
            _eval.Goals.Limit = _goals;
            Carried state = null;
            _eval.Run(more.Append(more[more.Count - 1]).ToList(), null, tick =>
            {
                if (tick < more.Count) return true;
                state = CarriedNow();
                return false;
            }, 1000000);
            return state;
        }

        /// <summary>The player after each of these inputs from at (the route's first length inputs); shorter when the run ends first.</summary>
        List<Carried> Follow(SimSnapshot at, int length, List<InputTick> inputs)
        {
            var states = new List<Carried>();
            if (inputs.Count == 0) return states;
            _eval.SetStart(at, length);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs.Append(inputs[inputs.Count - 1]).ToList(), null, tick =>
            {
                if (tick > inputs.Count) return false;
                states.Add(CarriedNow());
                return tick < inputs.Count;
            }, 1000000);
            return states;
        }

        /// <summary>Where the player is and how it moves: these must match on the restart tick and on every tick until the rest does.</summary>
        static readonly HashSet<string> Kinematic = new HashSet<string>
            { "position (world)", "floating origin", "physics body", "interpolation ends", "Velocity", "momentum", "movingPlatformVelocity" };

        // Ticks of the old route after a restart within which a state that differs only in its buffers and pending invokes must catch up.
        const int ConvergeTicks = 60;

        /// <summary>
        /// Whether two states after a restart that differ only off the kinematic fields (a buffer's pending cancel due 2 ticks
        /// apart) become the same under the old route's inputs after it: moving identically every tick until every field is equal,
        /// before the next restart and within <see cref="ConvergeTicks"/>. From that tick on the player plays the same. Returns
        /// the ticks it took, -1 when it doesn't.
        /// </summary>
        static int Converges(Carried oldAtRestart, Carried nowAtRestart, List<Carried> old, List<Carried> now)
        {
            if (!MovesAlike(oldAtRestart, nowAtRestart)) return -1;
            for (int i = 0; i < old.Count && i < now.Count; i++)
            {
                if (!MovesAlike(old[i], now[i])) return -1;
                if (CarriedDifference(old[i], now[i]).Count == 0) return i + 1;
            }
            return -1;
        }

        static bool MovesAlike(Carried a, Carried b)
        {
            for (int i = 0; i < a.Names.Length; i++)
                if (Kinematic.Contains(a.Names[i]) && !Same(a.Values[i], b.Values[i])) return false;
            return true;
        }

        /// <summary>
        /// How the new state after a restart differs from the old one; empty when the old route's inputs after it play the same.
        /// Three timers the game only reads against thresholds count as the same once both are past them (Movement's every read):
        /// dashCooldown and wallCoyoteFrames below 0, framesSinceLastDash at or over dashBoostMax × 1.4 and 4.
        /// </summary>
        static List<string> CarriedDifference(Carried old, Carried now)
        {
            var diff = new List<string>();
            float boost = 0f;
            for (int i = 0; i < old.Names.Length; i++)
                if (old.Names[i] == "dashBoostMax") boost = Math.Max((float)old.Values[i], (float)now.Values[i]);
            float dashBound = Math.Max(boost * 1.4f, 4f);
            for (int i = 0; i < old.Names.Length; i++)
            {
                object a = old.Values[i], b = now.Values[i];
                if (Same(a, b)) continue;
                string name = old.Names[i];
                if ((name == "dashCooldown" || name == "wallCoyoteFrames") && (float)a < 0f && (float)b < 0f) continue;
                if (name == "framesSinceLastDash" && (float)a >= dashBound && (float)b >= dashBound) continue;
                diff.Add(name + " " + Show(a) + " -> " + Show(b));
            }
            return diff;
        }

        static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == b;
            if (a is UnityEngine.Object || b is UnityEngine.Object) return ReferenceEquals(a, b);
            if (a is string || !(a is System.Collections.IEnumerable)) return a.Equals(b);
            System.Collections.IEnumerator x = ((System.Collections.IEnumerable)a).GetEnumerator(), y = ((System.Collections.IEnumerable)b).GetEnumerator();
            while (true)
            {
                bool more = x.MoveNext();
                if (more != y.MoveNext()) return false;
                if (!more) return true;
                if (!Same(x.Current, y.Current)) return false;
            }
        }

        static string Show(object v)
        {
            switch (v)
            {
                case null: return "null";
                case UnityEngine.Object o: return o.name;
                case string s: return s;
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case System.Collections.IEnumerable e: return "[" + string.Join(", ", e.Cast<object>().Select(Show)) + "]";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// What of the game's carry-over a restart leaves on after its own tick (the "Momentum after quick restart" tech), for the
        /// studio. The body's velocity isn't listed: the restart tick's own gravity and stick give it one after every restart.
        /// </summary>
        static List<string> Carries(Carried c)
        {
            var on = new List<string>();
            object Get(string name) { int i = Array.IndexOf(c.Names, name); return i < 0 ? null : c.Values[i]; }
            if (Get("momentum") is UnityEngine.Vector2 m && (m.x != 0f || m.y != 0f)) on.Add($"momentum {m.x:0.#}, {m.y:0.#}");
            // MoveLeftAndRight locks Velocity.x to the wall jump's direction while the lock is at 0 or more (then counts it down).
            if (Get("wallJumpMovementLockRemaining") is float lockLeft && lockLeft >= 0f && Get("wallJumpDirection") is float dir && dir != 0f)
                on.Add($"wall jump lock ({lockLeft + 1:0} more tick{(lockLeft >= 1f ? "s" : "")} at full speed {(dir > 0f ? "right" : "left")})");
            if (Get("movingPlatformCoyote") is float zip && zip > 0f && Get("platformMomentumHistory") is IEnumerable<UnityEngine.Vector2> history
                && history.Any(v => v.x != 0f || v.y != 0f)) on.Add("zip mover momentum");
            if (Get("DashBounceFrames") is int bounce && bounce > 0) on.Add("ground bounce window " + bounce);
            if (Get("dashActive") is bool dashing && dashing) on.Add("dash still running");
            if (Get("jumpBuffer") is bool jump && jump) on.Add("jump buffered");
            if (Get("dashBuffer") is bool dash && dash) on.Add("dash buffered");
            return on;
        }

        /// <summary>The best whose segments the studio was last sent.</summary>
        Route _segmentsShown;

        /// <summary>
        /// Polish by reset segments (--resets): each segment of the best (or only --segment N), in order. A segment with goals is
        /// searched for its last goal from the state after its opening restart, then closed by the old restart; a segment with none
        /// (an extra lap for Cash, a few idle ticks) is tried without, and an extra lap is searched as its course's lap.
        /// </summary>
        /// <returns>Why the solve ends here (level "info" or "warn"), or nulls to go on.</returns>
        (string level, string text) PolishSegments()
        {
            List<Segment> segments = Segments(_best);
            if (_segmentsShown != _best) EmitSegments(segments);
            _segmentsShown = _best;
            int first = _onlySegment > 0 ? Math.Min(_onlySegment, segments.Count) : 1;
            int end = _onlySegment > 0 ? first : segments.Count;
            bool searched = false;
            int before = segments.Count;
            for (int n = first; n <= end && !TimeUp; n++)
            {
                segments = Segments(_best);
                if (n > segments.Count) break;
                searched |= PolishSegment(segments[n - 1], segments.Count);
            }
            // Segment numbers after a dropped one move down one: --segment N would polish another segment from here on.
            if (_onlySegment > 0 && Segments(_best).Count < before) return ("info", $"segment {first} was dropped (the route has one quick restart fewer), so its number is another segment now");
            if (searched || TimeUp) return (null, null);
            return ("warn", _onlySegment > 0 ? $"segment {first} has no goal or lap to search, and can't be dropped" : "no segment has a goal or lap to search");
        }

        void EmitSegments(List<Segment> segments)
        {
            Route best = _best;
            Dictionary<int, Carried> states = ResetStates(best.Inputs, segments.Where(s => s.Reset >= 0).Select(s => s.Reset).ToList());
            double At(int goals) { return goals == 0 ? 0.0 : Seconds(best.GoalPhysics[goals - 1]); }
            Emit(new
            {
                type = "segments",
                segments = segments.Select(s => new
                {
                    n = s.Number, first = s.First, last = s.Last, start = s.Start, reset = s.Reset, inputs = (s.Reset < 0 ? best.Inputs.Count : s.Reset) - s.Start,
                    from = At(s.First), to = At(s.Last),
                    carries = s.Reset >= 0 && states.TryGetValue(s.Reset, out Carried c) ? Carries(c) : null,
                }).ToArray(),
            });
            Log($"reset segments: {segments.Count} ({segments.Count(s => s.Last == s.First)} without a goal)");
        }

        /// <summary>One segment polished; false when there was nothing in it to search.</summary>
        bool PolishSegment(Segment seg, int count)
        {
            Route best = _best;
            string title = $"segment {seg.Number}/{count}";
            FullTasGoal[] lap = seg.Last > seg.First || seg.Reset < 0 ? null : LapOf(seg, best);
            Emit(new { type = "segment", n = seg.Number, of = count, first = seg.First, last = seg.Last, start = seg.Start, reset = seg.Reset,
                lap = lap == null ? null : lap[0].label + " + " + lap[1].label });
            if (seg.Reset < 0)
            {
                // The route's end closes it: nothing after it to keep.
                if (seg.Last <= seg.First) return false;
                var tail = new List<List<InputTick>> { best.Inputs.Skip(seg.Start).ToList() };
                List<List<InputTick>> last = SearchWindow("segment", best, seg.Start, seg.Last, tail, best.GoalPhysics[seg.Last - 1], _windowMinutes, 0);
                if (last.Count > 0) Merge($"faster through segment {seg.Number} (goals {seg.First + 1}-{seg.Last})", best, seg.Start, seg.Last, last);
                return true;
            }
            Dictionary<int, Carried> states = ResetStates(best.Inputs, seg.Start > 0 ? new[] { seg.Start - 1, seg.Reset } : new[] { seg.Reset });
            if (!states.TryGetValue(seg.Reset, out Carried closing))
            {
                Log($"{title}: the best doesn't replay to its restart at input {seg.Reset}; skipped");
                return false;
            }
            if (seg.Last == seg.First && seg.Start > 0 && TryDrop(seg, best, states[seg.Start - 1], closing, title)) return true;
            if (seg.Last == seg.First && lap == null)
            {
                Log($"{title}: no goal and no lap in it; kept");
                return false;
            }
            List<List<InputTick>> found = lap == null
                ? SearchWindow("segment", best, seg.Start, seg.Last, new List<List<InputTick>> { best.Inputs.Skip(seg.Start).Take(seg.Reset - seg.Start).ToList() },
                    best.GoalPhysics[seg.Last - 1], _windowMinutes, 0)
                : SearchLap(seg, best, lap, title);
            if (found.Count == 0) return true;
            string where = lap == null ? $"faster through segment {seg.Number} (goals {seg.First + 1}-{seg.Last}), same state after its restart"
                : $"faster extra lap in segment {seg.Number} ({lap[1].label}), same state after its restart";
            CloseSegment(seg, best, lap, closing, found, where, title);
            return true;
        }

        /// <summary>
        /// A segment without goals taken out, when one restart then leaves the player as the closing one did: the opening restart
        /// as it is, or the closing restart pressed in its place.
        /// </summary>
        bool TryDrop(Segment seg, Route best, Carried opening, Carried closing, string title)
        {
            List<string> diff = CarriedDifference(closing, opening);
            List<InputTick> inputs = best.Inputs.Take(seg.Start).Concat(best.Inputs.Skip(seg.Reset + 1)).ToList();
            if (diff.Count > 0 && seg.Start >= 2 && !best.Inputs[seg.Start - 2].reset)
            {
                _eval.SetStart(_origin, 0);
                _eval.Goals.Limit = _goals;
                SimSnapshot before = _eval.Advance(best.Inputs.Take(seg.Start - 1).ToList());
                Carried closed = before == null ? null : CarriedAfter(before, seg.Start - 1, new List<InputTick> { best.Inputs[seg.Reset] });
                if (closed != null && CarriedDifference(closing, closed).Count == 0)
                {
                    diff.Clear();
                    inputs = best.Inputs.Take(seg.Start - 1).Concat(best.Inputs.Skip(seg.Reset)).ToList();
                }
            }
            if (diff.Count > 0)
            {
                Log($"{title}: can't drop it, no restart in its place leaves the player as its closing restart did ({string.Join("; ", diff.Take(4))})");
                return false;
            }
            Route run = Evaluate(inputs);
            if (Better(run, _best))
            {
                Commit(run, $"dropped segment {seg.Number} ({seg.Reset - seg.Start} inputs without a goal), same state after the restart");
                return true;
            }
            Log($"{title}: dropping it reaches {Describe(run)}, no better (an extra lap's Cash, or the clones' time)");
            return false;
        }

        /// <summary>An extra lap's goals, when a segment without goals is one: its course's gate and finish (the builder's, <see cref="LoopCourse"/>), which the old segment reaches.</summary>
        FullTasGoal[] LapOf(Segment seg, Route best)
        {
            int course = seg.First < _goals ? LoopCourse(seg.First) : 0;
            if (course == 0) return null;
            FullTasGoal[] lap = { RunGoal("start:" + course, FullTasGoal.Start, course), RunGoal("finish:" + course, FullTasGoal.Finish, course) };
            SimSnapshot at = SegmentStart(seg, best);
            if (at == null) return null;
            int cut = LapCut(at, seg.Start, best.Inputs.Skip(seg.Start).Take(seg.Reset - seg.Start).ToList(), lap);
            return cut < 0 ? null : lap;
        }

        SimSnapshot SegmentStart(Segment seg, Route best)
        {
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            return _eval.Advance(best.Inputs.Take(seg.Start).ToList());
        }

        /// <summary>The inputs after which a lap's finish is reached, played from at (the route's first length inputs); -1 when it isn't.</summary>
        int LapCut(SimSnapshot at, int length, List<InputTick> inputs, FullTasGoal[] lap)
        {
            FullTasTracker goals = _runGoals.For(lap);
            _eval.UseGoals(goals);
            try
            {
                _eval.SetStart(at.WithGoal(goals.Capture()), length);
                RunResult r = _eval.Run(inputs.Concat(Enumerable.Repeat(new InputTick(), Settle)).ToList());
                return r.Progress < lap.Length ? -1 : (int)(r.GoalInputTicks[lap.Length - 1] - length);
            }
            finally { _eval.UseGoals(_runGoals); }
        }

        List<List<InputTick>> SearchLap(Segment seg, Route best, FullTasGoal[] lap, string title)
        {
            List<InputTick> prefix = best.Inputs.Take(seg.Start).ToList();
            SimSnapshot at = SegmentStart(seg, best);
            List<InputTick> old = best.Inputs.Skip(seg.Start).Take(seg.Reset - seg.Start).ToList();
            _eval.Restore(at);
            string caps = WindowCaps(lap);
            Func<Simulation> create = () =>
            {
                (Simulation sim, SimSnapshot state) = Prefixed(prefix, title);
                FullTasTracker goals = sim.Goals.For(lap);
                sim.UseGoals(goals);
                sim.SetStart(state.WithGoal(goals.Capture()), prefix.Count);
                return sim;
            };
            int id = ++_windowId;
            string span = lap[0].label + " + " + lap[1].label;
            Emit(new { type = "window", id, kind = "segment", first = seg.First, limit = seg.First, logged = seg.First, attempt = 0, budget = Math.Round(_windowMinutes * 60, 1),
                start = seg.First == 0 ? 0.0 : Seconds(best.GoalPhysics[seg.First - 1]), target = (double?)null, savedStarts = 0, piece = span });
            string archiveFile = Path.Combine(_archiveDir, "lap-" + seg.Number + "-" + ArchiveHash(prefix, lap.Select(g => g.label)).ToString("x16") + ".json");
            List<List<InputTick>> found = Explore(id, title, span + " (an extra lap)", create, caps, new List<List<InputTick>> { old }, archiveFile, long.MaxValue,
                _windowMinutes, _stallMinutes, null, lap.Select(g => g.label).ToList(), out List<InputTick> bestInputs);
            if (bestInputs != null && !found.Any(f => f.SequenceEqual(bestInputs))) found.Add(bestInputs);
            return found;
        }

        // Idle ticks tried before a candidate's restart beyond the old segment's own (a new way can need to wait out a dash or a lock).
        const int RestartWaits = 24;

        /// <summary>
        /// Closes each found way (fastest first) with the old restart and the old route after it. Between the way's last goal and
        /// the restart it plays the old segment's own inputs there, or 0.. idle ticks: the first that leaves the player after the
        /// restart as the old route did is judged in the whole run. A way whose restart never matches is dropped.
        /// </summary>
        void CloseSegment(Segment seg, Route best, FullTasGoal[] lap, Carried closing, List<List<InputTick>> found, string where, string title)
        {
            List<InputTick> prefix = best.Inputs.Take(seg.Start).ToList();
            SimSnapshot start = SegmentStart(seg, best);
            List<InputTick> oldSegment = best.Inputs.Skip(seg.Start).Take(seg.Reset - seg.Start).ToList();
            int oldCut = lap == null ? (int)best.GoalInput[seg.Last - 1] - seg.Start : LapCut(start, seg.Start, oldSegment, lap);
            List<InputTick> oldRunout = oldCut >= 0 && oldCut <= oldSegment.Count ? oldSegment.Skip(oldCut).ToList() : new List<InputTick>();
            InputTick restart = best.Inputs[seg.Reset];
            List<InputTick> after = best.Inputs.Skip(seg.Reset + 1).ToList();
            // The old route's inputs after the restart, up to its next restart: what a close that differs only in buffers is followed under.
            List<InputTick> follow = after.TakeWhile(k => !k.reset).Take(ConvergeTicks).ToList();
            List<Carried> oldFollow = null;
            int unmatched = 0, slower = 0, missed = 0;
            List<string> firstDiff = null;
            string slowerWhy = null;
            foreach (List<InputTick> way in Enumerable.Reverse(found).Distinct(SequenceComparer.Instance).Take(5))
            {
                int cut;
                if (lap != null) cut = LapCut(start, seg.Start, way, lap);
                else
                {
                    _eval.SetStart(start, seg.Start);
                    _eval.Goals.Limit = seg.Last;
                    RunResult r = _eval.Run(way.Concat(Enumerable.Repeat(new InputTick(), Settle)).ToList());
                    cut = r.Progress < seg.Last ? -1 : (int)(r.GoalInputTicks[seg.Last - 1] - seg.Start);
                }
                if (cut < 0) { missed++; continue; }
                List<InputTick> head = way.Concat(Enumerable.Repeat(new InputTick { held = way.Count > 0 && way[way.Count - 1].held }, Math.Max(0, cut - way.Count))).Take(cut).ToList();
                _eval.SetStart(start, seg.Start);
                _eval.Goals.Limit = _goals;
                SimSnapshot there = head.Count == 0 ? start : _eval.Advance(head);
                if (there == null) { missed++; continue; }
                InputTick idle = new InputTick { held = head.Count > 0 ? head[head.Count - 1].held : prefix.Count > 0 && prefix[prefix.Count - 1].held };
                var runouts = new List<List<InputTick>> { oldRunout };
                for (int wait = 0; wait <= oldRunout.Count + RestartWaits; wait++)
                    if (wait != oldRunout.Count || !oldRunout.All(k => k.Equals(idle))) runouts.Add(Enumerable.Repeat(idle, wait).ToList());
                bool matched = false;
                foreach (List<InputTick> runout in runouts.OrderBy(r => r.Count))
                {
                    InputTick before = runout.Count > 0 ? runout[runout.Count - 1] : head.Count > 0 ? head[head.Count - 1] : prefix.Count > 0 ? prefix[prefix.Count - 1] : default;
                    if (before.reset) continue;
                    List<InputTick> close = runout.Append(restart).ToList();
                    Carried now = CarriedAfter(there, seg.Start + head.Count, close);
                    if (now == null) continue;
                    List<string> diff = CarriedDifference(closing, now);
                    int caughtUp = 0;
                    if (diff.Count > 0 && follow.Count > 0 && MovesAlike(closing, now))
                    {
                        if (oldFollow == null)
                        {
                            _eval.SetStart(_origin, 0);
                            _eval.Goals.Limit = _goals;
                            SimSnapshot oldAfter = _eval.Advance(best.Inputs.Take(seg.Reset + 1).ToList());
                            oldFollow = oldAfter == null ? new List<Carried>() : Follow(oldAfter, seg.Reset + 1, follow);
                        }
                        _eval.SetStart(there, seg.Start + head.Count);
                        _eval.Goals.Limit = _goals;
                        SimSnapshot nowAfter = _eval.Advance(close);
                        List<Carried> nowFollow = nowAfter == null ? new List<Carried>() : Follow(nowAfter, seg.Start + head.Count + close.Count, follow);
                        caughtUp = Converges(closing, now, oldFollow, nowFollow);
                        if (caughtUp > 0)
                        {
                            Log($"{title}: its restart differs only in {string.Join("; ", diff.Take(3))}; moving the same, it is the same {caughtUp} tick{(caughtUp == 1 ? "" : "s")} after");
                            diff.Clear();
                        }
                    }
                    if (diff.Count > 0)
                    {
                        if (firstDiff == null || diff.Count < firstDiff.Count) firstDiff = diff;
                        continue;
                    }
                    matched = true;
                    List<InputTick> inputs = prefix.Concat(head).Concat(close).Concat(after).ToList();
                    Route run = Evaluate(inputs);
                    string extra = (runout.Count == oldRunout.Count ? "" : $" ({runout.Count} ticks before it, the old way {oldRunout.Count})")
                        + (caughtUp > 0 ? $" (a buffer differed, the same {caughtUp} tick{(caughtUp == 1 ? "" : "s")} after it)" : "");
                    if (Better(run, _best))
                    {
                        Commit(run, where + extra);
                        return;
                    }
                    string brokeAt = run.Progress < best.Progress
                        ? $"the run after its restart stops after goal {run.Progress}/{_goals} (goal {run.Progress + 1}, {(run.Progress < _goals ? _labels[run.Progress] : "")}, missed: {run.EndReason})" : null;
                    // Fallback: the old tail walked into a box the run can now pay for (a faster lap's clones pay more). Only those buys are rebuilt.
                    (Route repaired, string rebuilt, string stuck) = brokeAt == null ? (null, null, null) : RepairBuys(run, best, title);
                    if (repaired != null && Better(repaired, _best))
                    {
                        Commit(repaired, where + extra + ", then rebuilt " + rebuilt + " from loop pieces");
                        return;
                    }
                    slower++;
                    slowerWhy ??= brokeAt != null
                        ? brokeAt + (stuck != null ? "; rebuilding it failed (" + stuck + ")" : repaired != null ? $"; rebuilt {rebuilt}, the run ends at {Describe(repaired)}" : "")
                            + ": the player is the same, the world isn't (Cash, clones, the game's objects)"
                        : $"the whole run ends at {Describe(run)}, not before {Describe(_best)}";
                    break;
                }
                if (!matched) unmatched++;
            }
            string text = unmatched > 0 && slower == 0 && missed == 0
                ? $"Segment {seg.Number} is faster, but no restart after it leaves the player as the old one did ({string.Join("; ", (firstDiff ?? new List<string>()).Take(4))}); dropped."
                : slower > 0 ? $"Segment {seg.Number} is faster with the same state after its restart, but {slowerWhy}; dropped."
                : $"Segment {seg.Number}: the way found didn't reach its goals when replayed in the run; dropped.";
            Log(title + ": " + text);
            Emit(new { type = "dropped", missed, slower, unmatched, text });
        }

        const int MaxBuyRepairs = 3;

        /// <summary>
        /// A run whose old tail stops on a purchase that isn't its next goal (walking through a box it can pay for now): that buy
        /// goal is rebuilt from loop pieces (<see cref="BuildLoops"/>, a piece search when none fits) and the old route after it
        /// follows, up to <see cref="MaxBuyRepairs"/> times. Nothing else is re-solved. Returns the run (null when a repair
        /// doesn't reach its goal), the goals rebuilt, and why it stopped.
        /// </summary>
        (Route run, string rebuilt, string stuck) RepairBuys(Route run, Route old, string title)
        {
            var goals = new List<int>();
            for (int k = 0; k < MaxBuyRepairs && run.Progress < old.Progress && !TimeUp; k++)
            {
                int g = run.Progress;
                if (run.EndReason == null || !run.EndReason.StartsWith("Bought ", StringComparison.Ordinal) || g == 0 || !IsLoop(g) || _runGoals.Goals[g].kind != FullTasGoal.Buy)
                    return (goals.Count == 0 ? null : run, Rebuilt(goals), goals.Count == 0 ? null : $"goal {g + 1} ({(g < _goals ? _labels[g] : "")}) stops otherwise: {run.EndReason}");
                Log($"{title}: rebuilding goal {g + 1} ({_labels[g]}) from loop pieces: {run.EndReason}");
                Emit(new { type = "note", level = "info", text = $"The faster segment's run walks into another box on the way to goal {g + 1} ({_labels[g]}); rebuilding that buy from loop pieces." });
                Built built = BuildLoops(run, g, g + 1, old, null);
                if (built.Reached != g + 1) return (null, Rebuilt(goals), $"goal {g + 1}: {built.Stuck ?? "no way found"}");
                goals.Add(g);
                List<InputTick> inputs = built.Inputs.Concat(old.Inputs.Skip((int)old.GoalInput[g])).ToList();
                for (int i = 1; i < inputs.Count; i++)
                    if (inputs[i].reset && inputs[i - 1].reset) return (null, Rebuilt(goals), $"goal {g + 1}'s rebuilt buy would quick restart twice in a row");
                run = Evaluate(inputs);
            }
            return (run, Rebuilt(goals), run.Progress < old.Progress ? $"still stops at goal {run.Progress + 1}: {run.EndReason}" : null);
        }

        string Rebuilt(List<int> goals) { return string.Join(", ", goals.Select(g => $"goal {g + 1} ({_labels[g]})")); }
    }
}
