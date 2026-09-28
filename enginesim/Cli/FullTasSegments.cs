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
    // its goals are searched from the state after the Quick Restart that opens it, the way found ends with the restart that closed the
    // old segment, and the old route after that restart is kept as it is. That is only sound when the player leaves the new
    // restart exactly as it left the old one, so every candidate's state after its Quick Restart tick is compared with the old one's,
    // field by field; one that differs is dropped, never re-solved.
    sealed partial class FullTas
    {
        /// <summary>
        /// The route between two quick restarts: inputs Start..Reset-1 (Start is 0 or the input after the Quick Restart that opens it),
        /// closed by the restart at input Reset (-1: the last segment, closed by the route's end). First and Last are the goals
        /// reached before its first input and before its closing Quick Restart.
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

        /// <summary>
        /// A segment of the best the solver has worked on, kept in <c>fulltas/&lt;run&gt;.faster.json</c> while the best still holds
        /// it as it was (OldSegment at Start). With a Way: a faster way its Quick Restart check passed but the whole run didn't
        /// take (a later buy broke); the segment isn't searched again, only this way retried, and the studio marks it. Without
        /// one: searched for Minutes per window and nothing faster found; skipped until the route there changes or the window
        /// gets longer.
        /// </summary>
        sealed class KnownWay
        {
            public int Start, Reset, First, Last;
            public double Saves, Minutes;
            public string Why;
            public List<InputTick> OldSegment, Way;
        }

        List<KnownWay> _known;
        string _knownFile;

        static int SegmentLength(Segment s, Route r) { return (s.Reset < 0 ? r.Inputs.Count : s.Reset) - s.Start; }

        void LoadKnown(string traceDir)
        {
            _knownFile = Path.Combine(traceDir, "fulltas", _name + ".faster.json");
            _known = new List<KnownWay>();
            if (_fresh || !File.Exists(_knownFile)) return;
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_knownFile));
            System.Text.Json.JsonElement root = doc.RootElement;
            if (!root.GetProperty("goals").EnumerateArray().Select(g => g.GetString()).SequenceEqual(_labels)) return;
            KnownWay Read(System.Text.Json.JsonElement w, bool way) => new KnownWay { Start = w.GetProperty("start").GetInt32(), Reset = w.GetProperty("reset").GetInt32(),
                First = w.GetProperty("first").GetInt32(), Last = w.GetProperty("last").GetInt32(), OldSegment = Solve.DecodeTicks(w.GetProperty("old").GetString()),
                Saves = way ? w.GetProperty("saves").GetDouble() : 0.0, Why = way ? w.GetProperty("why").GetString() : null,
                Way = way ? Solve.DecodeTicks(w.GetProperty("way").GetString()) : null,
                Minutes = w.TryGetProperty("minutes", out System.Text.Json.JsonElement m) ? m.GetDouble() : 0.0 };
            foreach (System.Text.Json.JsonElement w in root.GetProperty("ways").EnumerateArray()) _known.Add(Read(w, true));
            if (root.TryGetProperty("searched", out System.Text.Json.JsonElement searched))
                foreach (System.Text.Json.JsonElement w in searched.EnumerateArray()) _known.Add(Read(w, false));
            SaveKnown();
        }

        /// <summary>Keeps the entries whose segment the best still holds as it was (moved to its inputs now), and writes them.</summary>
        void SaveKnown()
        {
            if (_knownFile == null) return;
            List<Segment> segments = Segments(_best);
            var kept = new List<KnownWay>();
            foreach (KnownWay k in _known)
            {
                Segment s = segments.FirstOrDefault(x => x.First == k.First && x.Last == k.Last && (x.Reset < 0) == (k.Reset < 0)
                    && SegmentLength(x, _best) == k.OldSegment.Count && _best.Inputs.Skip(x.Start).Take(k.OldSegment.Count).SequenceEqual(k.OldSegment));
                if (s == null || kept.Any(o => o.Start == s.Start && (o.Way == null) == (k.Way == null))) continue;
                k.Start = s.Start;
                k.Reset = s.Reset;
                kept.Add(k);
            }
            _known = kept;
            Directory.CreateDirectory(Path.GetDirectoryName(_knownFile));
            Solve.WriteShared(_knownFile, System.Text.Json.JsonSerializer.Serialize(new
            {
                name = _name,
                goals = _labels,
                bestTicks = _best.Progress == 0 ? -1 : _best.Last,
                ways = _known.Where(k => k.Way != null).Select(k => new { start = k.Start, reset = k.Reset, first = k.First, last = k.Last, saves = k.Saves, why = k.Why,
                    old = Solve.EncodeTicks(k.OldSegment), way = Solve.EncodeTicks(k.Way) }),
                searched = _known.Where(k => k.Way == null).Select(k => new { start = k.Start, reset = k.Reset, first = k.First, last = k.Last, minutes = k.Minutes,
                    old = Solve.EncodeTicks(k.OldSegment) }),
            }));
        }

        KnownWay KnownFor(Segment seg) { return _known?.FirstOrDefault(k => k.Way != null && k.Start == seg.Start && k.First == seg.First && k.Last == seg.Last); }

        /// <summary>Minutes per window this segment was last searched for with nothing faster found (0: not since the route there changed).</summary>
        double SearchedFor(Segment seg) { return _known?.FirstOrDefault(k => k.Way == null && k.Start == seg.Start && k.First == seg.First && k.Last == seg.Last)?.Minutes ?? 0.0; }

        void RememberFaster(Segment seg, Route best, List<InputTick> way, double saves, string why)
        {
            if (_known == null) return;
            _known.RemoveAll(k => k.Start == seg.Start);
            _known.Add(new KnownWay { Start = seg.Start, Reset = seg.Reset, First = seg.First, Last = seg.Last, Saves = Math.Round(saves, 2), Why = why,
                OldSegment = best.Inputs.Skip(seg.Start).Take(SegmentLength(seg, best)).ToList(), Way = way });
            SaveKnown();
            _segmentsShown = null;
        }

        /// <summary>A segment searched with nothing faster kept: skipped by later passes at no more minutes per window.</summary>
        void RememberSearched(Segment seg, Route best)
        {
            if (_known == null || _best != best || KnownFor(seg) != null) return;
            _known.RemoveAll(k => k.Way == null && k.Start == seg.Start);
            _known.Add(new KnownWay { Start = seg.Start, Reset = seg.Reset, First = seg.First, Last = seg.Last, Minutes = _windowMinutes,
                OldSegment = best.Inputs.Skip(seg.Start).Take(SegmentLength(seg, best)).ToList() });
            SaveKnown();
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
        /// Polish by reset segments (--resets): each segment of the best (or only --segment N), from the last one back. A segment
        /// saves time without touching the route before it, and the later it is the less it changes after it: an early faster lap
        /// changes the Cash of every buy after it (a buy walk then takes a box it can now pay for), a late one has few or no buys
        /// left to break. A segment with goals is searched for its last goal from the state after its opening Quick Restart, then
        /// closed by the old Quick Restart; a segment with none (an extra lap for Cash, a few idle ticks) is tried without, and an
        /// extra lap is searched as its course's lap.
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
            _skipped = 0;
            // Last segment first: a commit changes only the inputs from its segment on, so the segments before keep their numbers.
            for (int n = end; n >= first && !TimeUp; n--)
            {
                segments = Segments(_best);
                if (n > segments.Count) continue;
                searched |= PolishSegment(segments[n - 1], segments.Count);
            }
            // Segment numbers after a dropped one move down one: --segment N would polish another segment from here on.
            if (_onlySegment > 0 && Segments(_best).Count < before) return ("info", $"segment {first} was dropped (the route has one Quick Restart fewer), so its number is another segment now");
            if (searched || TimeUp) return (null, null);
            if (_skipped > 0 && _onlySegment == 0)
                return ("info", $"every segment left has been searched at {_windowMinutes:0.##} min per window since the route there last changed ({_skipped} skipped); raise Minutes per window to search them again");
            if (_onlySegment > 0 && first <= Segments(_best).Count && KnownFor(Segments(_best)[first - 1]) is KnownWay k)
                return ("info", $"segment {first} already has a faster way ({k.Saves:0.00}s) that {k.Why.Replace("the run after its Quick Restart ", "the run ")}; it isn't searched again until the route after it changes");
            return ("warn", _onlySegment > 0 ? $"segment {first} has no goal or lap to search, and can't be dropped" : "no segment has a goal or lap to search");
        }

        void EmitSegments(List<Segment> segments)
        {
            Route best = _best;
            List<int> resets = segments.Where(s => s.Reset >= 0).Select(s => s.Reset).ToList();
            Dictionary<int, Carried> states = ResetStates(best.Inputs, resets);
            Dictionary<int, string> targets = QuickRestartTargets(best.Inputs, resets);
            double At(int goals) { return goals == 0 ? 0.0 : Seconds(best.GoalPhysics[goals - 1]); }
            Emit(new
            {
                type = "segments",
                segments = segments.Select(s => new
                {
                    n = s.Number, first = s.First, last = s.Last, start = s.Start, reset = s.Reset, inputs = (s.Reset < 0 ? best.Inputs.Count : s.Reset) - s.Start,
                    from = At(s.First), to = At(s.Last),
                    carries = s.Reset >= 0 && states.TryGetValue(s.Reset, out Carried c) ? Carries(c) : null,
                    known = KnownFor(s)?.Saves,
                    quickRestart = s.Reset >= 0 && targets.TryGetValue(s.Reset, out string target) ? target : null,
                }).ToArray(),
            });
            Log($"reset segments: {segments.Count} ({segments.Count(s => s.Last == s.First)} without a goal)");
        }

        /// <summary>
        /// Where each of these Quick Restarts (input indices) sends the player, read on the tick before it: Movement.respawn(true)
        /// goes to courseResetPoint while a course's start gate has set it (the course's reset point, until its finish clears
        /// it), else to respawnPoint, the last Checkpoint touched. The course-checkpoints toggle (isRespawningAtCheckpoints) only
        /// changes where a death respawns.
        /// </summary>
        Dictionary<int, string> QuickRestartTargets(List<InputTick> inputs, ICollection<int> resets)
        {
            var targets = new Dictionary<int, string>();
            if (resets.Count == 0) return targets;
            var at = new HashSet<int>(resets);
            int last = resets.Max();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs, null, tick =>
            {
                if (at.Contains(tick))
                {
                    Movement p = _eval.Player;
                    courseScript course = _eval.TimingCourse();
                    targets[tick] = p.courseResetPoint != UnityEngine.Vector2.zero
                        ? (course != null ? $"Course {course.courseNumber} reset point" : "course reset point") + $" ({p.courseResetPoint.x - _eval.CurrentOrigin.x:0}, {p.courseResetPoint.y - _eval.CurrentOrigin.y:0})"
                        : $"Checkpoint ({p.respawnPoint.x:0}, {p.respawnPoint.y:0})";
                }
                return tick < last;
            }, 1000000);
            return targets;
        }

        /// <summary>One segment polished; false when there was nothing in it to search.</summary>
        bool PolishSegment(Segment seg, int count)
        {
            Route best = _best;
            string title = $"segment {seg.Number}/{count}";
            FullTasGoal[] lap = seg.Last > seg.First || seg.Reset < 0 ? null : LapOf(seg, best);
            Emit(new { type = "segment", n = seg.Number, of = count, first = seg.First, last = seg.Last, start = seg.Start, reset = seg.Reset,
                lap = lap == null ? null : lap[0].label + " + " + lap[1].label });
            // Searched already with nothing faster since the route there last changed: skipped, unless the window is longer now
            // or this segment was asked for by name.
            double searchedFor = SearchedFor(seg);
            if (_onlySegment == 0 && searchedFor >= _windowMinutes && KnownFor(seg) == null)
            {
                _skipped++;
                Log($"{title}: searched at {searchedFor:0.##} min per window already, nothing faster; skipped");
                return false;
            }
            if (seg.Reset < 0)
            {
                // The route's end closes it: nothing after it to keep. The whole segment is one search, from its Quick Restart
                // to the route's last goal (a budget of Minutes per window for each of its goals), so no way is spliced in front
                // of an old tail it doesn't fit.
                if (seg.Last <= seg.First) return false;
                var tail = new List<List<InputTick>> { best.Inputs.Skip(seg.Start).ToList() };
                List<List<InputTick>> last = SearchWindow("segment", best, seg.Start, seg.Last, tail, best.GoalPhysics[seg.Last - 1],
                    _windowMinutes * (seg.Last - seg.First), 0);
                if (last.Count > 0) Merge($"faster through segment {seg.Number} (goals {seg.First + 1}-{seg.Last}, after the last Quick Restart)", best, seg.Start, seg.Last, last);
                if (!TimeUp && _best == best) RememberSearched(seg, best);
                return true;
            }
            Dictionary<int, Carried> states = ResetStates(best.Inputs, seg.Start > 0 ? new[] { seg.Start - 1, seg.Reset } : new[] { seg.Reset });
            if (!states.TryGetValue(seg.Reset, out Carried closing))
            {
                Log($"{title}: the best doesn't replay to its Quick Restart at input {seg.Reset}; skipped");
                return false;
            }
            if (seg.Last == seg.First && seg.Start > 0 && TryDrop(seg, best, states[seg.Start - 1], closing, title)) return true;
            if (seg.Last == seg.First && lap == null)
            {
                Log($"{title}: no goal and no lap in it; kept");
                return false;
            }
            string where = lap == null ? $"faster through segment {seg.Number} (goals {seg.First + 1}-{seg.Last}), same state after its Quick Restart"
                : $"faster extra lap in segment {seg.Number} ({lap[1].label}), same state after its Quick Restart";
            KnownWay known = KnownFor(seg);
            if (known != null)
            {
                // Already optimized: the faster way is known and waits for the run after it to take it. Retried, not searched again.
                Log($"{title}: a faster way ({known.Saves:0.00}s) is known but {known.Why}; retrying it instead of searching");
                CloseSegment(seg, best, lap, closing, new List<List<InputTick>> { known.Way }, where + " (the known faster way)", title, repair: false);
                return false;
            }
            List<List<InputTick>> found = lap == null
                ? SearchWindow("segment", best, seg.Start, seg.Last, new List<List<InputTick>> { best.Inputs.Skip(seg.Start).Take(seg.Reset - seg.Start).ToList() },
                    best.GoalPhysics[seg.Last - 1], _windowMinutes, 0)
                : SearchLap(seg, best, lap, title);
            if (found.Count > 0) CloseSegment(seg, best, lap, closing, found, where, title);
            if (!TimeUp && _best == best) RememberSearched(seg, best);
            return true;
        }

        int _skipped;

        /// <summary>
        /// A segment without goals taken out, when one restart then leaves the player as the closing one did: the opening Quick Restart
        /// as it is, or the closing Quick Restart pressed in its place.
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
                Log($"{title}: can't drop it, no restart in its place leaves the player as its closing Quick Restart did ({string.Join("; ", diff.Take(4))})");
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
        /// What closing a segment of a route needs: the route before it and the state at its start (Prefix, Start), and from the
        /// old route it replaces: the old way's inputs between its last goal and its Quick Restart, the restart's input, the state that
        /// restart left (State) and the old route after it. Seg is the segment in the route being closed; OldReset the old
        /// restart's input in Old.
        /// </summary>
        sealed class Closing
        {
            public Segment Seg;
            public FullTasGoal[] Lap;
            public List<InputTick> Prefix, OldRunout, After, Follow;
            public SimSnapshot Start;
            public InputTick Restart;
            public Carried State;
            public List<Carried> OldFollow;
            public Route Old;
            public int OldReset;
        }

        Closing ClosingOf(Segment seg, Route route, Segment oldSeg, Route old, FullTasGoal[] lap, Carried state)
        {
            var c = new Closing { Seg = seg, Lap = lap, Old = old, OldReset = oldSeg.Reset, State = state, Prefix = route.Inputs.Take(seg.Start).ToList() };
            c.Start = SegmentStart(seg, route);
            if (c.Start == null) return c;
            List<InputTick> oldSegment = old.Inputs.Skip(oldSeg.Start).Take(oldSeg.Reset - oldSeg.Start).ToList();
            int oldCut = lap == null ? (int)old.GoalInput[oldSeg.Last - 1] - oldSeg.Start
                : LapCut(ReferenceEquals(route, old) ? c.Start : SegmentStart(oldSeg, old), oldSeg.Start, oldSegment, lap);
            c.OldRunout = oldCut >= 0 && oldCut <= oldSegment.Count ? oldSegment.Skip(oldCut).ToList() : new List<InputTick>();
            c.Restart = old.Inputs[oldSeg.Reset];
            c.After = old.Inputs.Skip(oldSeg.Reset + 1).ToList();
            // The old route's inputs after the restart, up to its next restart: what a close that differs only in buffers is followed under.
            c.Follow = c.After.TakeWhile(k => !k.reset).Take(ConvergeTicks).ToList();
            return c;
        }

        /// <summary>
        /// One way through a segment, cut at its last goal and closed with the old Quick Restart and the old route after it. Between the
        /// goal and the restart it plays the old way's inputs there, or 0.. idle ticks: the first close that leaves the player as
        /// the old Quick Restart did is replayed as the whole run (run). Otherwise run is null, with the smallest difference seen (diff),
        /// or missed when the way doesn't reach the segment's goals.
        /// </summary>
        (Route run, List<string> diff, string extra, bool missed) TryWay(Closing c, List<InputTick> way, string title)
        {
            Segment seg = c.Seg;
            int cut;
            if (c.Lap != null) cut = LapCut(c.Start, seg.Start, way, c.Lap);
            else
            {
                _eval.SetStart(c.Start, seg.Start);
                _eval.Goals.Limit = seg.Last;
                RunResult r = _eval.Run(way.Concat(Enumerable.Repeat(new InputTick(), Settle)).ToList());
                cut = r.Progress < seg.Last ? -1 : (int)(r.GoalInputTicks[seg.Last - 1] - seg.Start);
            }
            if (cut < 0) return (null, null, null, true);
            List<InputTick> head = way.Concat(Enumerable.Repeat(new InputTick { held = way.Count > 0 && way[way.Count - 1].held }, Math.Max(0, cut - way.Count))).Take(cut).ToList();
            _eval.SetStart(c.Start, seg.Start);
            _eval.Goals.Limit = _goals;
            SimSnapshot there = head.Count == 0 ? c.Start : _eval.Advance(head);
            if (there == null) return (null, null, null, true);
            InputTick idle = new InputTick { held = head.Count > 0 ? head[head.Count - 1].held : c.Prefix.Count > 0 && c.Prefix[c.Prefix.Count - 1].held };
            var runouts = new List<List<InputTick>> { c.OldRunout };
            for (int wait = 0; wait <= c.OldRunout.Count + RestartWaits; wait++)
                if (wait != c.OldRunout.Count || !c.OldRunout.All(k => k.Equals(idle))) runouts.Add(Enumerable.Repeat(idle, wait).ToList());
            List<string> smallest = null;
            foreach (List<InputTick> runout in runouts.OrderBy(r => r.Count))
            {
                InputTick before = runout.Count > 0 ? runout[runout.Count - 1] : head.Count > 0 ? head[head.Count - 1] : c.Prefix.Count > 0 ? c.Prefix[c.Prefix.Count - 1] : default;
                if (before.reset) continue;
                List<InputTick> close = runout.Append(c.Restart).ToList();
                Carried now = CarriedAfter(there, seg.Start + head.Count, close);
                if (now == null) continue;
                List<string> diff = CarriedDifference(c.State, now);
                int caughtUp = 0;
                if (diff.Count > 0 && c.Follow.Count > 0 && MovesAlike(c.State, now))
                {
                    if (c.OldFollow == null)
                    {
                        _eval.SetStart(_origin, 0);
                        _eval.Goals.Limit = _goals;
                        SimSnapshot oldAfter = _eval.Advance(c.Old.Inputs.Take(c.OldReset + 1).ToList());
                        c.OldFollow = oldAfter == null ? new List<Carried>() : Follow(oldAfter, c.OldReset + 1, c.Follow);
                    }
                    _eval.SetStart(there, seg.Start + head.Count);
                    _eval.Goals.Limit = _goals;
                    SimSnapshot nowAfter = _eval.Advance(close);
                    List<Carried> nowFollow = nowAfter == null ? new List<Carried>() : Follow(nowAfter, seg.Start + head.Count + close.Count, c.Follow);
                    caughtUp = Converges(c.State, now, c.OldFollow, nowFollow);
                    if (caughtUp > 0)
                    {
                        Log($"{title}: its Quick Restart differs only in {string.Join("; ", diff.Take(3))}; moving the same, it is the same {caughtUp} tick{(caughtUp == 1 ? "" : "s")} after");
                        diff.Clear();
                    }
                }
                if (diff.Count > 0)
                {
                    if (smallest == null || diff.Count < smallest.Count) smallest = diff;
                    continue;
                }
                Route run = Evaluate(c.Prefix.Concat(head).Concat(close).Concat(c.After).ToList());
                string extra = (runout.Count == c.OldRunout.Count ? "" : $" ({runout.Count} ticks before it, the old way {c.OldRunout.Count})")
                    + (caughtUp > 0 ? $" (a buffer differed, the same {caughtUp} tick{(caughtUp == 1 ? "" : "s")} after it)" : "");
                return (run, null, extra, false);
            }
            return (null, smallest ?? new List<string>(), null, false);
        }

        /// <summary>
        /// Closes each found way (fastest first) with the old Quick Restart and the old route after it (<see cref="TryWay"/>). When the
        /// whole run then stops at a later goal (the world differs: Cash, clones), the segments that break are rebuilt one by one
        /// from their own restarts (<see cref="RepairSegments"/>). A way whose restart never matches is dropped.
        /// </summary>
        void CloseSegment(Segment seg, Route best, FullTasGoal[] lap, Carried closing, List<List<InputTick>> found, string where, string title, bool repair = true)
        {
            Closing c = ClosingOf(seg, best, seg, best, lap, closing);
            if (c.Start == null)
            {
                Log($"{title}: the best doesn't replay to the segment's start; skipped");
                return;
            }
            int unmatched = 0, slower = 0, missed = 0;
            List<string> firstDiff = null;
            string slowerWhy = null;
            bool repairTried = false;
            foreach (List<InputTick> way in Enumerable.Reverse(found).Distinct(SequenceComparer.Instance).Take(5))
            {
                var t = TryWay(c, way, title);
                if (t.missed) { missed++; continue; }
                if (t.run == null)
                {
                    unmatched++;
                    if (firstDiff == null || t.diff.Count < firstDiff.Count) firstDiff = t.diff;
                    continue;
                }
                Route run = t.run;
                if (Better(run, _best))
                {
                    Commit(run, where + t.extra);
                    return;
                }
                slower++;
                if (run.Progress >= best.Progress)
                {
                    slowerWhy ??= $"the whole run ends at {Describe(run)}, not before {Describe(_best)}";
                    continue;
                }
                string brokeAt = $"the run after its Quick Restart stops after goal {run.Progress}/{_goals} (goal {run.Progress + 1}, {(run.Progress < _goals ? _labels[run.Progress] : "")}, missed: {run.EndReason})";
                // Rebuilding searches a window per broken segment: once, for the fastest way whose restart matched; never on a retry.
                if (repairTried || !repair)
                {
                    slowerWhy ??= brokeAt;
                    continue;
                }
                repairTried = true;
                (Route repaired, List<int> rebuilt, string stuck) = RepairSegments(run, best, title);
                string segments = $"segment{(rebuilt.Count == 1 ? "" : "s")} {string.Join(", ", rebuilt)}";
                if (repaired != null && Better(repaired, _best))
                {
                    Commit(repaired, where + t.extra + (rebuilt.Count > 0 ? $", then rebuilt {segments} from their Quick Restarts" : ""));
                    return;
                }
                slowerWhy = brokeAt + (stuck != null ? $"; rebuilding the segments after it stopped ({(rebuilt.Count > 0 ? "rebuilt " + segments + ", then " : "")}{stuck})"
                    : repaired != null ? $"; with {segments} rebuilt the run ends at {Describe(repaired)}" : "");
                // Found, and its Quick Restart matches: known from now on, so the segment isn't searched again (only this way retried).
                int reached = run.Progress - 1;
                double saves = reached < 0 ? 0.0 : Seconds(best.GoalPhysics[reached] - run.GoalPhysics[reached]);
                RememberFaster(seg, best, way, saves, brokeAt);
            }
            string text = unmatched > 0 && slower == 0 && missed == 0
                ? $"Segment {seg.Number} is faster, but no Quick Restart after it leaves the player as the old one did ({string.Join("; ", (firstDiff ?? new List<string>()).Take(4))}); dropped."
                : slower > 0 ? $"Segment {seg.Number} is faster with the same state after its Quick Restart, but {slowerWhy}; dropped."
                : $"Segment {seg.Number}: the way found didn't reach its goals when replayed in the run; dropped.";
            Log(title + ": " + text);
            Emit(new { type = "dropped", missed, slower, unmatched, text });
        }

        /// <summary>
        /// A run whose old route after a new segment stops at a later goal: the old segment holding that goal is searched again
        /// from its own opening Quick Restart (a <c>repair</c> window, seeded with its old way) and closed with its old Quick Restart, left
        /// exactly as before, so the old route after it stays good; then the next segment that breaks, until the run reaches every
        /// goal the old one did, it can no longer beat the record, or a segment has no such way. Each segment is rebuilt at most
        /// once. Returns the run (null when it stopped), the segments rebuilt, and why it stopped.
        /// </summary>
        (Route run, List<int> rebuilt, string stuck) RepairSegments(Route run, Route old, string title)
        {
            var rebuilt = new List<int>();
            List<Segment> oldSegments = Segments(old);
            while (run.Progress < old.Progress)
            {
                if (TimeUp) return (null, rebuilt, "the solve's time is up");
                int g = run.Progress;
                if (g > 0 && run.GoalPhysics[g - 1] >= _best.GoalPhysics[g - 1])
                    return (null, rebuilt, $"it reaches goal {g} no sooner than the record, so it can't end sooner");
                int delta = run.Inputs.Count - old.Inputs.Count;
                Segment Shifted(Segment s) => new Segment { Number = s.Number, Start = s.Start + delta, Reset = s.Reset < 0 ? -1 : s.Reset + delta, First = s.First, Last = s.Last };
                Segment os = oldSegments.FirstOrDefault(s => s.First <= g && g < s.Last);
                if (os == null) return (null, rebuilt, $"goal {g + 1} lies in no segment of the old route");
                var ns = Shifted(os);
                // A run can stop on a segment's way out, after its last goal (it walks into a box): it never reaches the next
                // goal's segment, so the segment it stops in, the one before, is rebuilt.
                while (ns.Start > 0 && os.Number > 1 && SegmentStart(ns, run) == null)
                {
                    os = oldSegments[os.Number - 2];
                    ns = Shifted(os);
                }
                if (os.Last <= os.First) return (null, rebuilt, $"it stops in segment {os.Number}, which has no goal to search ({run.EndReason})");
                if (rebuilt.Contains(os.Number)) return (null, rebuilt, $"segment {os.Number} breaks again after rebuilding it ({run.EndReason})");
                if (ns.Reset >= run.Inputs.Count || ns.Reset >= 0 && !run.Inputs[ns.Reset].reset || ns.Start > 0 && !run.Inputs[ns.Start - 1].reset)
                    return (null, rebuilt, $"segment {os.Number} doesn't line up with the new run's restarts");
                string span = ns.Last - ns.First == 1 ? $"goal {ns.Last}" : $"goals {ns.First + 1}-{ns.Last}";
                Log($"{title}: rebuilding segment {os.Number} ({span}) from its Quick Restart: {run.EndReason}");
                Emit(new { type = "note", level = "info", text = $"The old route stops at goal {g + 1} ({_labels[g]}): {run.EndReason}. Rebuilding segment {os.Number} ({span}) from its own Quick Restart." });
                var seeds = new List<List<InputTick>> { old.Inputs.Skip(os.Start).Take((os.Reset < 0 ? old.Inputs.Count : os.Reset) - os.Start).ToList() };
                // Any way through the segment will do (the whole run is judged after): the window ends at the first one found.
                List<List<InputTick>> found = SearchWindow("repair", run, ns.Start, ns.Last, seeds, long.MaxValue, _windowMinutes, 0, 0.0);
                Route next = null;
                if (os.Reset < 0)
                {
                    // The route's last segment: nothing after it to keep.
                    foreach (List<InputTick> way in Enumerable.Reverse(found).Take(5))
                    {
                        Route r = Splice(run, ns.Start, way, ns.Last, run);
                        if (r != null && (next == null || Better(r, next))) next = r;
                    }
                }
                else
                {
                    Carried state = ResetStates(old.Inputs, new[] { os.Reset }).TryGetValue(os.Reset, out Carried s) ? s : null;
                    Closing c = state == null ? null : ClosingOf(ns, run, os, old, null, state);
                    if (c?.Start == null) return (null, rebuilt, $"segment {os.Number}'s start doesn't replay");
                    foreach (List<InputTick> way in Enumerable.Reverse(found).Distinct(SequenceComparer.Instance).Take(5))
                    {
                        Route r = TryWay(c, way, title).run;
                        if (r != null && (next == null || Better(r, next))) next = r;
                    }
                }
                if (next == null || next.Progress <= g)
                    return (null, rebuilt, $"no way through segment {os.Number} reaches {span} and leaves its Quick Restart as before");
                rebuilt.Add(os.Number);
                run = next;
            }
            return (run, rebuilt, null);
        }
    }
}
