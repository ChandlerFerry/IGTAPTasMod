using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAPTasMod;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts waste and --waste-audit (docs/fulltas.md, "Parts" 8): the best replayed tick by tick and scanned for the waste a
    // person watching it sees and the gap model's per-goal numbers don't: a jump that bonks a ceiling, the stick held into a wall,
    // dashes that cancel each other, restarts in a row, a restart away from the next box, a Cash-bound walk that isn't the
    // straight one, slow flat ground, back-to-back laps. Each finding has a span and the ticks it loses at the goal's own pace
    // (held to its target buy's spare Cash); those ticks feed the model's expected headroom and a "waste" arm per goal, a window
    // over the finding's goals whose tech grafts go to its span first.
    //
    // --parts grafts: every Full-TAS window's OfflineSolver grafts the tech library (TechGrafts: the Tech tab's mined demos, and
    // whatever else fills its Patterns), gated by the unlocks held at the window's first goal plus the window's unlock buys, on a
    // share of its explore runs (GraftShareIdle; GraftShareWaste and the span as its focus in a window over audit findings).
    sealed partial class FullTas
    {
        sealed class Waste
        {
            public string Kind, What;
            /// <summary>The goal the span starts in, and the goal after the one it ends in (a waste arm's window).</summary>
            public int Goal, Limit;
            public long From, To;
            public int InFrom, InTo;
            /// <summary>Ticks the span loses at its goal's own pace, and those held to the target buy's spare Cash.</summary>
            public long Raw, Stake;
            /// <summary>Input indices a seed edits (-1: none): see <see cref="WasteSeeds"/>.</summary>
            public int E0 = -1, E1 = -1;
        }

        struct WasteTick
        {
            public float VX, VY, StickX;
            /// <summary>Live time so far in ticks (MacroEngine.LiveUnits: a dash freeze's held frames count, 5.6 to a tick).</summary>
            public double Live;
            public bool Ground, Wall, Dash, Ceiling, Fresh, Press, Release, Reset;
        }

        // Head bonk: Velocity.y at least this, then under this share of it, with a ceiling touched, no goal within BuyTouch ticks.
        const float BonkVy = 300f, BonkShare = .3f;
        const int BuyTouch = 3;
        // A box's buy cooldown (0.2 s): the soonest a box stood under is bought again.
        const long BuyCooldown = 10;
        // Wall stall: this many ticks in a row with the stick at least half held and x moving under StallMove a tick.
        const int StallTicks = 3;
        const float StallMove = .5f;
        // Cancelling dashes: two dashes within PairGap ticks, each moving at least PairMin sideways, opposite ways, netting under
        // PairNet of their sum.
        const int PairGap = 60;
        const float PairMin = 100f, PairNet = .35f;
        // Filler restarts: restarts at most ChainGap ticks apart with no goal but a start gate between; the span reaches back to
        // the goal before, at most ChainLead ticks.
        const int ChainGap = 60, ChainLead = 30;
        // A restart away from the next box: it lands this much further from it than it was, and buys it AwayTicks later than
        // staying would (a restart and a dash back into a box 500 u off, the quick rebuy, is 8 later).
        const float AwayMargin = 150f;
        const long AwayTicks = 15;
        // A Cash-bound walk: from at least WalkFrom away, its path longer than WalkSlack × the walk distance + WalkExtra.
        const float WalkSlack = 1.25f, WalkExtra = 60f, WalkFrom = 150f;
        // Slow ground: sideways progress on flat ground (under SlowRise of rise over SlowWindow ticks) under WasteSlowShare of what
        // the flat allows (the super chain's speed from there, FlatRise: the rise a flat 10-tick top allows).
        const int SlowWindow = 50;
        const float WasteSlowShare = .9f, SlowRise = 150f, FlatRise = 20f, ChainFloor = 9.2f;
        // The model's weight on the audit's ticks (between the proven lead's .6 and the deficit's .3).
        const double WasteWeight = .5;
        // Grafted share of explore runs: a window over no finding, and a window over one.
        const double GraftShareIdle = .15, GraftShareWaste = .5;

        List<Waste> _wasteFound = new List<Waste>();
        readonly Dictionary<int, double> _wasteAt = new Dictionary<int, double>();

        static bool IsMove(string kind) { return kind != "laps"; }

        /// <summary>The best's ticks with what the audit reads: the model's samples (place, speed, Cash, unlocks, the walk distance to
        /// the current goal) and per tick the velocity, the stick in effect, ground, wall, dash, a ceiling touched and the input
        /// consumed on it (a dash's ticks consume none).</summary>
        (List<Sample> s, List<WasteTick> w) WasteTrace(List<InputTick> inputs)
        {
            var s = new List<Sample>();
            var w = new List<WasteTick>();
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            DistanceField field = DistanceField.For(_eval);
            int avoidFor = -1, capsFor = -1;
            string caps = "";
            long seen = 0;
            float stick = 0f;
            _eval.Run(inputs, null, null, 1000000, _ =>
            {
                int p = _eval.Progress;
                Movement m = _eval.Player;
                if (p != capsFor) { capsFor = p; caps = PlayerCaps.Of(_eval).Id; }
                if (p != avoidFor && p < _goals)
                {
                    avoidFor = p;
                    field.SetAvoid(Solve.FullTasPaths(_name, new[] { _labels[p] }, _labels).Avoid);
                }
                WorldPos wp = _eval.PlayerWorld;
                CapturePos c = _eval.PlayerAtCapture;
                float d = p >= _goals ? float.NaN : field.Distance(c);
                long run = _eval.Engine.RunTicks;
                bool fresh = run != seen && run > 0 && run <= inputs.Count;
                seen = run;
                InputTick t = fresh ? inputs[(int)run - 1] : default;
                if (fresh) stick = t.x;
                s.Add(new Sample { Tick = _eval.Engine.PhysicsTicks, Input = (int)run, Progress = p, X = wp.x, Y = wp.y, CX = c.x, CY = c.y,
                    Dist = float.IsInfinity(d) ? float.NaN : d, Speed = (m.Velocity + m.momentum).magnitude, Cash = _eval.Shop.CashNow, Caps = caps });
                w.Add(new WasteTick { VX = m.Velocity.x, VY = m.Velocity.y, StickX = stick, Ground = m.onGround, Wall = m.OnWall,
                    Dash = m.cutsceneMode == Movement.cutsceneModes.dash, Ceiling = _eval.Physics.PlayerTouch.Ceiling,
                    Fresh = fresh, Press = fresh && t.press, Release = fresh && t.release, Reset = fresh && t.reset,
                    Live = MacroEngine.LiveUnits(_eval.Engine.PhysicsTicks, TickTiming ? 0 : _eval.Engine.HeldFrames) / 28.0 });
            });
            return (s, w);
        }

        /// <summary>The waste in a run (to the goals it reaches), by its ticks: see the class comment. <paramref name="rows"/> are the
        /// gap model's rows (the spare Cash of each goal's target buy, an economy goal's slack).</summary>
        List<Waste> Audit(Route r, List<Sample> s, List<WasteTick> w, List<GoalRow> rows)
        {
            var found = new List<Waste>();
            int n = s.Count, goals = Math.Min(r.Progress, rows.Count);
            if (n < 2 || goals == 0) return found;
            // at[k]: goal k's arrival sample (at[0] = -1).
            var at = new int[goals + 1];
            at[0] = -1;
            for (int k = 1, i = 0; k <= goals; k++)
            {
                while (i < n - 1 && s[i].Tick < r.GoalPhysics[k - 1]) i++;
                at[k] = i;
            }
            int GoalOf(int i) => Math.Clamp(s[i].Progress + 1, 1, goals);
            float Straight(int i, int k)
            {
                FullTasGoal g = _runGoals.Goals[k - 1];
                if (g.kind == FullTasGoal.At)
                {
                    float dx = Math.Max(0f, Math.Abs(s[i].CX - g.x) - g.w / 2f), dy = Math.Max(0f, Math.Abs(s[i].CY - g.y) - g.h / 2f);
                    return (float)Math.Sqrt(dx * dx + dy * dy);
                }
                float ex = s[i].X - s[at[k]].X, ey = s[i].Y - s[at[k]].Y;
                return (float)Math.Sqrt(ex * ex + ey * ey);
            }
            float DistTo(int i, int k)
            {
                float st = Straight(i, k);
                if (float.IsNaN(s[i].Dist) || s[i].Progress != k - 1) return st;
                return _runGoals.Goals[k - 1].kind == FullTasGoal.At ? Math.Max(st, s[i].Dist) : s[i].Dist;
            }
            // The goal's own pace: the 80th percentile of its positive progress per tick (the gap model's deficit's).
            var attain = new double[goals + 1];
            for (int k = 1; k <= goals; k++)
            {
                var rates = new List<float>();
                for (int i = at[k - 1] + 2; i <= at[k]; i++)
                {
                    float rate = (float)((DistTo(i - 1, k) - DistTo(i, k)) / Math.Max(1e-3, w[i].Live - w[i - 1].Live));
                    if (rate > 0f && !Jumped(s[i - 1], s[i])) rates.Add(rate);
                }
                rates.Sort();
                attain[k] = rates.Count == 0 ? 1 : Math.Max(1, rates[(int)(rates.Count * .8)]);
            }
            long Raw(int a, int b)
            {
                int k = GoalOf(a);
                double made = DistTo(a, k) - DistTo(b, k);
                return Math.Max(0, (long)Math.Round(w[b].Live - w[a].Live - Math.Max(0, made) / attain[k]));
            }
            void Add(string kind, int a, int b, long raw, string what, int e0 = -1, int e1 = -1)
            {
                int k = GoalOf(a), last = GoalOf(Math.Max(a, b - 1));
                long spare = rows[k - 1].Spare;
                found.Add(new Waste { Kind = kind, Goal = k, Limit = Math.Min(goals, Math.Max(k + 1, last + 1)), From = s[a].Tick, To = s[b].Tick,
                    InFrom = Math.Max(0, s[a].Input - 1), InTo = Math.Max(0, s[b].Input - 1), Raw = raw,
                    Stake = !IsMove(kind) ? rows[Math.Min(goals, last) - 1].Slack : spare == long.MaxValue ? raw : Math.Min(raw, spare), What = what, E0 = e0, E1 = e1 });
            }
            int LastPress(int input)
            {
                for (int q = input; q >= Math.Max(0, input - 40); q--) if (r.Inputs[q].press) return q;
                return -1;
            }
            int NextRelease(int input)
            {
                for (int q = input + 1; q < Math.Min(r.Inputs.Count, input + 60); q++) if (r.Inputs[q].release || r.Inputs[q].press) return q;
                return -1;
            }

            // Head bonk: rising fast, then Velocity.y gone in a tick against a ceiling (not a dash's, not a teleport, not a box's
            // underside the buy touches: a goal within BuyTouch ticks).
            for (int i = 1; i < n; i++)
            {
                if (!(w[i - 1].VY >= BonkVy && w[i].VY <= BonkShare * w[i - 1].VY) || w[i].Dash || w[i - 1].Dash || w[i].Ground || Jumped(s[i - 1], s[i])) continue;
                if (!w[i].Ceiling && !w[i - 1].Ceiling) continue;
                int ii = i;
                if (Enumerable.Range(1, goals).Any(k => Math.Abs(r.GoalPhysics[k - 1] - s[ii].Tick) <= BuyTouch)) continue;
                int j = i;
                while (j + 1 < n && j - i < 60 && !w[j].Ground && !Jumped(s[j], s[j + 1])) j++;
                int press = LastPress(s[i].Input - 1);
                Add("bonk", i, j, Raw(i, j), $"Velocity.y {w[i - 1].VY:0} -> {w[i].VY:0} under a ceiling at ({s[i].X:0},{s[i].Y:0}), lands {s[j].Tick - s[i].Tick} ticks later",
                    press, press < 0 ? -1 : NextRelease(press));
            }
            // Wall stall: the stick held, x stuck (a cling that a jump press uses within 2 ticks is a wall jump's set-up).
            for (int i = 1; i < n;)
            {
                bool Stuck(int m) => Math.Abs(w[m].StickX) >= .5f && Math.Abs(s[m].X - s[m - 1].X) < StallMove && !w[m].Dash && !w[m].Reset && !Jumped(s[m - 1], s[m]);
                if (!Stuck(i)) { i++; continue; }
                int j = i;
                while (j + 1 < n && Stuck(j + 1)) j++;
                bool cling = Enumerable.Range(i, j - i + 1).Any(m => w[m].Wall) && Enumerable.Range(i, Math.Min(n, j + 3) - i).Any(m => w[m].Press);
                if (j - i + 1 >= StallTicks && !cling && Raw(i - 1, j) >= 2)
                    Add("stall", i - 1, j, Raw(i - 1, j), $"stick {w[i].StickX:0.##} held, x stuck at {s[i].X:0} for {j - i + 1} ticks ({(w[i].Ground ? "ground" : w[i].Wall ? "wall" : "air")})");
                i = j + 1;
            }
            // Cancelling dashes.
            var dashes = new List<(int a, int b, float dx)>();
            for (int i = 1; i < n;)
            {
                if (!w[i].Dash) { i++; continue; }
                int j = i;
                while (j + 1 < n && w[j + 1].Dash && !Jumped(s[j], s[j + 1])) j++;
                dashes.Add((i, j, s[j].X - s[i - 1].X));
                i = j + 1;
            }
            for (int d = 1; d < dashes.Count; d++)
            {
                var (a1, b1, dx1) = dashes[d - 1];
                var (a2, b2, dx2) = dashes[d];
                if (s[a2].Tick - s[b1].Tick > PairGap || Math.Abs(dx1) < PairMin || Math.Abs(dx2) < PairMin || Math.Sign(dx1) == Math.Sign(dx2)
                    || Math.Abs(dx1 + dx2) > PairNet * (Math.Abs(dx1) + Math.Abs(dx2))) continue;
                if (Enumerable.Range(a1, b2 - a1 + 1).Any(m => m > 0 && Jumped(s[m - 1], s[m]))) continue;
                long raw = Raw(a1 - 1, b2);
                if (raw >= 3) Add("dashpair", a1 - 1, b2, raw, $"dashes {dx1:+0;-0} then {dx2:+0;-0} sideways, net {dx1 + dx2:+0;-0}, rise {s[b2].Y - s[a1 - 1].Y:0}",
                    Math.Max(0, s[a1].Input - 1), Math.Max(0, s[a2].Input - 1));
            }
            // Filler restarts: restarts in a row with no goal but a start gate between them.
            var restarts = Enumerable.Range(1, n - 1).Where(i => w[i].Reset).ToList();
            for (int c = 0; c < restarts.Count;)
            {
                int e = c;
                while (e + 1 < restarts.Count && s[restarts[e + 1]].Tick - s[restarts[e]].Tick <= ChainGap
                    && Enumerable.Range(s[restarts[e]].Progress, Math.Max(0, s[restarts[e + 1]].Progress - s[restarts[e]].Progress)).All(g => _labels[g].StartsWith("start:", StringComparison.Ordinal))) e++;
                if (e > c)
                {
                    int q0 = restarts[c], q1 = restarts[e], p = s[q0 - 1].Progress;
                    long goal = p == 0 ? 0 : r.GoalPhysics[p - 1], anchor = s[q0].Tick - goal <= ChainLead ? goal : s[q0 - 1].Tick;
                    int a = q0 - 1;
                    while (a > 0 && s[a].Tick > anchor) a--;
                    // The seed: the last restart played right after the anchor (its inputs up to there cut).
                    Add("filler", a, q1, Math.Max(0, (long)Math.Round(w[q1].Live - w[a].Live) - 1), $"{e - c + 1} restarts in {s[q1].Tick - s[q0].Tick} ticks at ticks "
                        + string.Join(",", restarts.Skip(c).Take(e - c + 1).Select(q => s[q].Tick)) + $", {s[q0].Tick - s[a].Tick} ticks after goal {p}",
                        s[a].Input, s[q1].Input - 1);
                }
                c = e + 1;
            }
            // A restart that lands further from the next box than the player was, and buys it later than staying would (the
            // box's buy cooldown, or the walk from where the player was): a restart and a dash back into a box is the quick rebuy.
            foreach (int q in restarts)
            {
                int k = GoalOf(q);
                if (_runGoals.Goals[k - 1].kind != FullTasGoal.Buy || at[k] <= q) continue;
                float before = DistTo(q - 1, k), after = DistTo(q, k);
                if (after < before + AwayMargin) continue;
                long raw = Math.Max(0, (long)Math.Round(w[at[k]].Live - w[q].Live) - Math.Max(BuyCooldown, (long)Math.Round(before / attain[k])));
                if (raw >= AwayTicks) Add("away", q - 1, at[k], raw, $"restart at tick {s[q].Tick} lands {after:0} u from goal {k}'s box, {before:0} u before it; bought {s[at[k]].Tick - s[q].Tick} ticks later",
                    s[q].Input - 1);
            }
            // A walk to a buy with spare Cash, longer than the walk distance.
            for (int k = 1; k <= goals; k++)
            {
                if (_runGoals.Goals[k - 1].kind != FullTasGoal.Buy || rows[k - 1].Spare <= 0) continue;
                int a = at[k - 1] + 1;
                for (int i = a + 1; i <= at[k]; i++) if (Jumped(s[i - 1], s[i])) a = i;
                if (at[k] - a < 10) continue;
                double path = 0;
                for (int i = a + 1; i <= at[k]; i++) path += Math.Sqrt(Math.Pow(s[i].X - s[i - 1].X, 2) + Math.Pow(s[i].Y - s[i - 1].Y, 2));
                float direct = DistTo(a, k);
                long raw = (long)Math.Round((path - direct) / attain[k]);
                // A box bought again from under it is a hop out and back in, not a walk.
                if (direct < WalkFrom || path <= WalkSlack * direct + WalkExtra || raw < 5) continue;
                Add("walk", a, at[k], raw,$"walks {path:0} u to the box where the way is {direct:0} u; "
                    + (rows[k - 1].Spare == long.MaxValue ? "not Cash-bound" : $"held the price {rows[k - 1].Spare} ticks before"));
            }
            // Slow ground: on flat ground (under SlowRise of rise over SlowWindow ticks), where movement pays (no lap, spare Cash), the
            // run's sideways progress toward the goal per live tick (a dash's freeze frames, 14 at its start and 14 at an end no
            // super cancels, are time the run spends) against what the flat allows: with the dash, the super chain played from the
            // span's start (SuperChainSpeed); without it, the fastest 10 ticks the run makes sideways on flat ground with those unlocks.
            var flatTop = new Dictionary<string, float>();
            for (int i = 0; i + SpeedWindow < n; i++)
            {
                int b = i + SpeedWindow;
                if (s[b].Caps != s[i].Caps || Math.Abs(s[b].Y - s[i].Y) >= FlatRise || Enumerable.Range(i + 1, SpeedWindow).Any(m => Jumped(s[m - 1], s[m]))) continue;
                float v = (float)(Math.Abs(s[b].X - s[i].X) / (w[b].Live - w[i].Live));
                if (!flatTop.TryGetValue(s[i].Caps, out float top) || v > top) flatTop[s[i].Caps] = v;
            }
            float TargetX(int k) => _runGoals.Goals[k - 1].kind == FullTasGoal.At ? _runGoals.Goals[k - 1].x : s[at[k]].CX;
            float Sideways(int a, int b, int k) => (s[b].X - s[a].X) * Math.Sign(TargetX(k) - s[a].CX);
            for (int k = 1; k <= goals; k++)
            {
                GoalRow row = rows[k - 1];
                if (row.Economy || row.Spare <= 0) continue;
                int i = at[k - 1] + 1, open = -1;
                for (; i + SlowWindow <= at[k]; i++)
                {
                    int b = i + SlowWindow;
                    bool flat = Math.Abs(s[b].Y - s[i].Y) < SlowRise && Sideways(i, b, k) > 0 && !Enumerable.Range(i + 1, SlowWindow).Any(m => Jumped(s[m - 1], s[m]));
                    if (flat && open < 0) open = i;
                    if (!flat && open >= 0) { AddSlow(open, i - 1 + SlowWindow, k); open = -1; }
                }
                if (open >= 0) AddSlow(open, Math.Min(at[k], i - 1 + SlowWindow), k);
            }
            void AddSlow(int a, int b, int k)
            {
                float made = Sideways(a, b, k);
                double live = w[b].Live - w[a].Live, speed = made / live;
                bool chain = Caps.TryParse(s[a].Caps, out Caps caps) && caps.Dash;
                int dir = Math.Sign(TargetX(k) - s[a].CX), wait = 0, from = Math.Max(0, s[a].Input);
                float vref = chain ? SuperChainSpeed(r, from, dir, out wait) : flatTop.TryGetValue(s[a].Caps, out float v) ? v : float.NaN;
                // A chain that can't get going there (a wall, a gap: under a walk's 9.2 u a tick) says nothing about the flat.
                if (made <= 0 || !(vref > ChainFloor) || speed >= WasteSlowShare * vref) return;
                long raw = Math.Max(0, (long)Math.Round(live - made / vref));
                // The chain seed (WasteSeeds): E0 the input it starts at, E1 its wait before the first press, times the side.
                if (raw >= 3) Add("slow", a, b, raw, $"{speed:0.0} u per live tick sideways over {s[b].Tick - s[a].Tick} ticks ({live:0} live) on flat ground, against "
                    + (chain ? $"{vref:0.0} for the super chain from there" : $"{vref:0.0} for {s[a].Caps}"), chain ? from : -1, chain ? (wait + 1) * dir : -1);
            }
            // Back-to-back laps: the last lap of each run of laps of one course (the goals part's drop candidates; their stake is
            // the model's economy slack there).
            var laps = new List<(int first, int finish, string course)>();
            for (int g = 0; g < goals; g++)
            {
                string label = _labels[g];
                if (label.StartsWith("start:", StringComparison.Ordinal) && g + 1 < goals && _labels[g + 1] == "finish:" + label.Substring(6)) { laps.Add((g, g + 1, label.Substring(6))); g++; }
                else if (label.StartsWith("finish:", StringComparison.Ordinal)) laps.Add((g, g, label.Substring(7)));
            }
            for (int l = 0; l < laps.Count;)
            {
                int e = l;
                while (e + 1 < laps.Count && laps[e + 1].first == laps[e].finish + 1 && laps[e + 1].course == laps[l].course) e++;
                if (e > l)
                {
                    var last = laps[e];
                    int a = last.first == 0 ? 0 : at[last.first], b = at[last.finish + 1];
                    Add("laps", a, b, s[b].Tick - s[a].Tick, $"the last of {e - l + 1} back-to-back course {last.course} laps (goals {laps[l].first + 1}-{last.finish + 1}): "
                        + $"the goals part's lap drop; the spare cash covers {rows[last.finish].Slack} ticks of it");
                }
                l = e + 1;
            }
            // One finding per kind and span: overlapping ones of a kind merge (their stake measured again over the union).
            var merged = new List<Waste>();
            foreach (Waste x in found.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.From))
            {
                Waste m = merged.Count > 0 ? merged[^1] : null;
                bool spans = x.Kind == "dashpair" || x.Kind == "stall" || x.Kind == "slow" || x.Kind == "bonk";
                if (m == null || m.Kind != x.Kind || x.From > m.To || !spans) { merged.Add(x); continue; }
                m.To = Math.Max(m.To, x.To);
                m.InTo = Math.Max(m.InTo, x.InTo);
                m.Limit = Math.Max(m.Limit, x.Limit);
                m.Raw = m.Kind == "slow" ? m.Raw + x.Raw : Raw(s.FindIndex(t => t.Tick >= m.From), s.FindIndex(t => t.Tick >= m.To));
                long spare = rows[m.Goal - 1].Spare;
                m.Stake = spare == long.MaxValue ? m.Raw : Math.Min(m.Raw, spare);
                m.What += "; " + x.What;
            }
            return merged.OrderBy(x => x.From).ToList();
        }

        /// <summary>Rows for a run the model wasn't built on (--waste-audit): each goal's split, unlocks and top speed, the target
        /// buys' spare Cash and the laps' economy slack, as BuildModel files them.</summary>
        List<GoalRow> WasteRows(Route r, List<Sample> s)
        {
            var rows = new List<GoalRow>();
            for (int k = 1; k <= r.Progress; k++)
            {
                long arrival = r.GoalPhysics[k - 1], start = k == 1 ? 0 : r.GoalPhysics[k - 2];
                int first = s.FindIndex(x => x.Tick > start);
                string caps = first < 0 ? "none" : s[first].Caps;
                rows.Add(new GoalRow { K = k, Label = _labels[k - 1], Split = arrival - start, Arrival = arrival, Caps = caps,
                    Economy = FullTasGoal.IsEconomy(_labels[k - 1]), VTop = _vtop.TryGetValue(caps, out var v) ? Math.Max(1f, v.speed) : 1f });
            }
            EconomySlack(rows, s);
            CashBound(rows, s);
            return rows;
        }

        static string WasteLine(Waste x, string label)
        {
            return $"{x.Kind,-8} goal {x.Goal,3} {label,-26} ticks {x.From}-{x.To} (inputs {x.InFrom}-{x.InTo}) raw {x.Raw,4} stake {x.Stake,4}: {x.What}";
        }

        /// <summary>
        /// --waste-audit &lt;route.json&gt; [--expect a-b,c-d] [--clean kind:a-b]: the audit of a route (a run file's "inputs" or an offline best's "ticks")
        /// under the trace's goals, every finding with its span, ticks at stake and kind. --expect: physics tick spans a finding
        /// must overlap; --clean: spans no finding (of that kind) may overlap; one line each, exit 1 when one isn't so.
        /// </summary>
        int WasteAuditCommand(string file)
        {
            JsonElement header = Solve.ReadHeader(_trace);
            if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) throw new InvalidOperationException("Not a Full-TAS trace (no \"fulltas\" header): " + _trace);
            _name = fulltas.GetProperty("name").GetString();
            _labels = fulltas.GetProperty("goals").EnumerateArray().Select(g => g.GetProperty("label").GetString()).ToArray();
            _goals = _labels.Length;
            _eval = Solve.Creator(_trace, header)();
            _eval.StrictBuys = true;
            _origin = _eval.Start;
            _dt = _eval.World.FixedDeltaTime;
            _runGoals = _eval.Goals;
            DistanceField.CellSize = 8f;
            List<InputTick> inputs = global::IGTAP.EngineSim.Cli.Plan.ReadRoute(file);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Route r = _best = Evaluate(inputs);
            // The field's grid grows over the whole route on a first pass (BuildModel's two passes).
            WasteTrace(inputs);
            var (s, w) = WasteTrace(inputs);
            Sustained(s, "the route's path");
            List<Waste> found = Audit(r, s, w, WasteRows(r, s));
            Console.WriteLine($"{Path.GetFileName(file)}: {r.Progress} of {_goals} goals" + (r.Progress == _goals ? $", {r.LiveSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s live ({r.Last} ticks)" : "")
                + $"; top path speed per unlock set: {string.Join(", ", _vtop.OrderBy(v => v.Key).Select(v => $"{v.Key} {v.Value.speed:0.0}"))}");
            foreach (Waste x in found) Console.WriteLine(WasteLine(x, _labels[x.Goal - 1]));
            Console.WriteLine($"waste audit: {found.Count} findings ({string.Join(", ", found.GroupBy(x => x.Kind).Select(g => $"{g.Key} {g.Count()}"))}), "
                + $"stake {found.Where(x => IsMove(x.Kind)).Sum(x => x.Stake)} ticks at {found.Where(x => IsMove(x.Kind) && x.Stake > 0).Select(x => x.Goal).Distinct().Count()} goals (laps: the goals part's), {clock.Elapsed.TotalSeconds:0.0} s");
            int missed = 0;
            IEnumerable<string> Spans(string option) => (Solve.Option(_args, option, (string)null) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<Waste> Hits(string span, string kind)
            {
                string[] ends = span.Split('-');
                long a = long.Parse(ends[0], CultureInfo.InvariantCulture), b = long.Parse(ends[^1], CultureInfo.InvariantCulture);
                return found.Where(x => x.From <= b && x.To >= a && (kind == null || x.Kind == kind)).ToList();
            }
            string Show(List<Waste> hits) => string.Join(", ", hits.Select(x => $"{x.Kind} goal {x.Goal} {x.From}-{x.To} stake {x.Stake}"));
            foreach (string span in Spans("--expect"))
            {
                List<Waste> hits = Hits(span, null);
                if (hits.Count == 0) missed++;
                Console.WriteLine($"expect {span}: " + (hits.Count == 0 ? "MISSED" : "flagged by " + Show(hits)));
            }
            // --clean kind:a-b: spans no finding of that kind may overlap (a fixed route's stretch).
            foreach (string clean in Spans("--clean"))
            {
                string[] parts = clean.Split(':');
                List<Waste> hits = Hits(parts[^1], parts.Length > 1 ? parts[0] : null);
                if (hits.Count > 0) missed++;
                Console.WriteLine($"clean {clean}: " + (hits.Count == 0 ? "clean" : "FLAGGED by " + Show(hits)));
            }
            if (Spans("--expect").Any() || Spans("--clean").Any()) Console.WriteLine(missed == 0 ? "expect: all as expected" : $"expect: {missed} not as expected");
            return missed == 0 ? 0 : 1;
        }

        /// <summary>The route <see cref="_wasteFound"/> audits (the best when <see cref="AuditWaste"/> ran): once the best changes,
        /// its audit is made again when next asked for.</summary>
        Route _wasteRoute;

        /// <summary>The best's findings (audited again when the best changed since).</summary>
        List<Waste> Findings()
        {
            if (!ReferenceEquals(_wasteRoute, _best)) AuditWaste();
            return _wasteFound;
        }

        /// <summary>--parts waste, at the end of every BuildModel (the start, each new best): the best's audit and its ticks per goal
        /// for the model (Refresh) and the waste arms (a window from the goal to the one after its last finding: WasteLimit).</summary>
        void AuditWaste()
        {
            _wasteRoute = _best;
            _wasteFound = new List<Waste>();
            _wasteAt.Clear();
            if (!_auto || !Part("waste") || _best == null || _best.Progress < _goals || _model == null || _model.Count < _goals) return;
            // A focus (one stretch, Optimize's Full-TAS transitions) pulls only windows starting in it: a whole run's audit (ZipEscape%:
            // 87 CPU s on one core before its first window) would find waste nothing searches.
            if (_focusFrom > 1 || _focusTo < _goals)
            {
                Log($"auto: no waste audit: focused on goals {_focusFrom}-{_focusTo}");
                return;
            }
            double c0 = CpuSeconds();
            var (s, w) = WasteTrace(_best.Inputs);
            _wasteFound = Audit(_best, s, w, _model);
            foreach (var g in _wasteFound.Where(x => IsMove(x.Kind) && x.Stake > 0).GroupBy(x => x.Goal)) _wasteAt[g.Key] = g.Sum(x => x.Stake);
            Log($"auto: waste audit of {Describe(_best)} in {CpuSeconds() - c0:0.0} CPU s: {_wasteFound.Count} findings ({string.Join(", ", _wasteFound.GroupBy(x => x.Kind).Select(g => $"{g.Key} {g.Count()}"))}), "
                + $"stake {_wasteAt.Values.Sum():0} ticks at {_wasteAt.Count} goals");
            foreach (Waste x in _wasteFound.Where(x => x.Stake > 0).OrderByDescending(x => x.Stake).Take(12)) Log("auto: waste " + WasteLine(x, _labels[x.Goal - 1]));
            AutoWrite(new { type = "waste", best = _best.LiveSeconds, findings = _wasteFound.Select(x => new { kind = x.Kind, goal = x.Goal, limit = x.Limit, from = x.From, to = x.To, raw = x.Raw, stake = x.Stake, what = x.What }) });
        }

        /// <summary>The audit's ticks at stake at goal k (0: none).</summary>
        double WasteAt(int k) { Findings(); return _wasteAt.TryGetValue(k, out double t) ? t : 0; }

        /// <summary>A waste arm's last goal: the goal after the last one its findings reach.</summary>
        int WasteLimit(int k)
        {
            List<Waste> at = Findings().Where(x => x.Goal == k && IsMove(x.Kind) && x.Stake > 0).ToList();
            return Math.Min(_goals, Math.Max(k + 1, at.Count == 0 ? 0 : at.Max(x => x.Limit)));
        }

        /// <summary>
        /// A waste arm's own seeds, from the best's inputs with its goal's findings undone one at a time: a filler chain's inputs cut
        /// (its last restart right after the goal before), the away restart dropped, the cancelling dashes dropped, the bonking
        /// jump dropped (press to release), slow flat ground played as the super chain (its probe's wait, cycles over the span's
        /// inputs). What follows is the best's own, for the search to mend.
        /// </summary>
        List<List<InputTick>> WasteSeeds(Route best, int k, int prefixLength)
        {
            var seeds = new List<List<InputTick>>();
            foreach (Waste x in Findings().Where(x => x.Goal == k && x.E0 >= prefixLength && x.E0 < best.Inputs.Count))
            {
                List<InputTick> v = best.Inputs.ToList();
                switch (x.Kind)
                {
                    case "filler":
                        if (x.E1 <= x.E0) continue;
                        v.RemoveRange(x.E0, x.E1 - x.E0);
                        break;
                    case "away":
                        v[x.E0] = v[x.E0] with { reset = false };
                        break;
                    case "dashpair":
                        foreach (int q in new[] { x.E0, x.E1 }.Where(q => q >= 0 && q < v.Count)) v[q] = v[q] with { dash = false, dashJump = 0 };
                        break;
                    case "bonk":
                        for (int q = x.E0; q <= (x.E1 < 0 ? x.E0 : x.E1) && q < v.Count; q++)
                            if (q == x.E0 || !v[q].press) v[q] = v[q] with { press = false, held = false, release = false };
                        break;
                    case "slow":
                        if (x.E1 == 0) continue;
                        int dir = Math.Sign(x.E1), cycles = Math.Max(2, (x.InTo - x.E0) / ChainCycle(dir).Count + 1);
                        var chain = Enumerable.Repeat(new InputTick { x = dir }, Math.Abs(x.E1) - 1).ToList();
                        for (int c = 0; c < cycles; c++) chain.AddRange(ChainCycle(dir));
                        v = v.Take(x.E0).Concat(chain).Concat(v.Skip(Math.Min(v.Count, x.InTo))).ToList();
                        break;
                    default: continue;
                }
                seeds.Add(v.Skip(prefixLength).ToList());
            }
            return seeds;
        }

        // ---------------------------------------------------------------- the super chain

        /// <summary>
        /// One cycle of the super chain on flat ground, the ending agent's (FalseEnding-Any% 354.72 s, inputs 15225-15240 on the
        /// 278-goal list, 23.7 u a live tick on goal 273's flat against the 355.92 s route's 20.2): the dash pressed in the air,
        /// buffered to the landing (index 3, the Landed contact), its jump at dash frame 10 so the super lands on the dash's last
        /// tick and cancels its end freeze, the jump cut, an air dash, then the fast-fall to the next cycle's landing.
        /// </summary>
        static List<InputTick> ChainCycle(int dir)
        {
            var t = new List<InputTick>
            {
                new InputTick { x = dir, dash = true, dashJump = 10 },
                new InputTick { x = dir, release = true },
                new InputTick { x = dir },
                new InputTick { x = dir },
                new InputTick { x = dir, dash = true },
            };
            t.AddRange(Enumerable.Repeat(new InputTick { x = dir, y = -1 }, 11));
            return t;
        }
        const int ChainLanding = 3, ChainCycles = 6, ChainSettle = 20;

        /// <summary>The super chain as a tech window at a landing, chained into itself at each next landing (up to 8 cycles), for
        /// the grafts: a flat with the dash available gets the cycle wherever a run lands on it.</summary>
        static TechPattern ChainPattern()
        {
            List<InputTick> cycle = ChainCycle(1);
            return new TechPattern
            {
                Tech = "super-chain", Name = "Super chain", Category = "glitchless", Requires = Caps.Parse("ds"), Kind = Contact.Landed,
                First = -ChainLanding, Last = cycle.Count - 1 - ChainLanding, Ticks = cycle.ToArray(), Key = true, Dir = 1, Weight = 3,
                DemoStart = "FalseEnding-Any% 354.72 s, inputs 15225-15240", ShiftLo = -2, ShiftHi = 4,
                Vary = new[] { new PatternVary { Index = 5, Run = 11, Ticks = new[] { 7, 9, 11, 13 } } },
                Chain = new PatternChain { Depth = 8, On = new[] { Contact.Landed }, With = "super-chain", ShiftLo = -2, ShiftHi = 4 },
            };
        }

        readonly Dictionary<(int input, int dir), (float speed, int wait)> _chainSpeed = new Dictionary<(int, int), (float, int)>();
        Route _chainFor;

        /// <summary>The super chain's sideways speed per live tick played from the run's state after <paramref name="input"/> inputs
        /// toward <paramref name="dir"/> (the best of 0-4 ticks' wait before the first press, <paramref name="wait"/>), after its
        /// first ChainSettle ticks: what that flat allows. NaN when it can't play (the state, a death, a wall within the first cycles).</summary>
        float SuperChainSpeed(Route r, int input, int dir) { return SuperChainSpeed(r, input, dir, out _); }

        float SuperChainSpeed(Route r, int input, int dir, out int wait)
        {
            wait = 0;
            if (dir == 0) return float.NaN;
            if (!ReferenceEquals(_chainFor, r)) { _chainSpeed.Clear(); _chainFor = r; }
            if (_chainSpeed.TryGetValue((input, dir), out var known)) { wait = known.wait; return known.speed; }
            float best = ChainProbe(r, input, dir, out wait);
            _chainSpeed[(input, dir)] = (best, wait);
            return best;
        }

        float ChainProbe(Route r, int input, int dir, out int bestWait)
        {
            bestWait = 0;
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = input == 0 ? _origin : _eval.Advance(r.Inputs.GetRange(0, Math.Min(input, r.Inputs.Count)));
            float best = float.NaN;
            for (int wait = 0; at != null && wait <= 4; wait++)
            {
                var moves = Enumerable.Repeat(new InputTick { x = dir }, wait).ToList();
                for (int c = 0; c < ChainCycles; c++) moves.AddRange(ChainCycle(dir));
                _eval.SetStart(at, input);
                _eval.Restore(at);
                var xs = new List<(float x, double live)>();
                _eval.Run(moves, null, null, 0, _ => xs.Add((_eval.PlayerWorld.x, MacroEngine.LiveUnits(_eval.Engine.PhysicsTicks, _eval.Engine.HeldFrames) / 28.0)));
                if (xs.Count < ChainSettle + 3 * SlowWindow / 2) continue;
                var (x0, l0) = xs[ChainSettle];
                var (x1, l1) = xs[^1];
                float v = (float)((x1 - x0) * dir / (l1 - l0));
                if (float.IsNaN(best) || v > best) { best = v; bestWait = wait; }
            }
            _eval.SetStart(_origin, 0);
            return best;
        }

        // ---------------------------------------------------------------- tech grafts in every window (--parts grafts)

        TechGrafts _graftLib;
        bool _graftLibTried;
        readonly Dictionary<string, List<TechPattern>> _graftsFor = new Dictionary<string, List<TechPattern>>();
        long _graftsTried, _graftSolvers;
        readonly Dictionary<string, int> _graftFinds = new Dictionary<string, int>();

        /// <summary>The whole tech library (the Tech tab's mined demos, --tech-dir, --tech-cache), loaded once on a thread of its own
        /// (mining builds a Simulation per demo, which on the loop's thread would take over the thread-static engine hooks its own
        /// Simulation plays with). Null when it doesn't load or holds nothing.</summary>
        TechGrafts GraftLibrary()
        {
            if (_graftLibTried) return _graftLib;
            _graftLibTried = true;
            try
            {
                string techDir = Solve.Option(_args, "--tech-dir", (string)null), cache = Solve.Option(_args, "--tech-cache", (string)null);
                TechGrafts all = Solve.OnThread(() => TechGrafts.Load(Path.GetDirectoryName(Path.GetFullPath(_trace)), techDir: techDir, cacheFile: cache));
                // An nmg class grafts glitchless and nmg techs only (techs.json category; the route corpus's windows aren't sorted so).
                all.Patterns.RemoveAll(p => !Glitches.Allows(_class, p.Category));
                all.Patterns.Add(ChainPattern());
                Log($"grafts: {all.Patterns.Count} tech windows of {all.Patterns.Select(p => p.Tech).Distinct().Count()} techs ({(all.FromCache ? "cache" : "mined")})"
                    + (Glitches.NoMajors(_class) ? $", {Glitches.Key(_class)}: glitchless and nmg techs only" : ""));
                return _graftLib = all;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException)
            {
                Log("grafts: the tech library didn't load: " + ex.GetBaseException().Message);
                return null;
            }
        }

        /// <summary>Every Full-TAS window grafts (a plain solve's too, as a course solve does): off with --no-tech-grafts, or an
        /// --auto whose --parts leaves grafts out.</summary>
        bool GraftsOn => _args != null && !_args.Contains("--no-tech-grafts") && Part("grafts");

        /// <summary>The library's windows the unlocks allow (a techs.json "requires" within them), for one solver (null: grafts
        /// off). Each solver gets its own instance: OfflineSolver prepares it for its caps.</summary>
        TechGrafts AutoGrafts(string caps)
        {
            if (!GraftsOn || GraftLibrary() is not TechGrafts lib) return null;
            if (!_graftsFor.TryGetValue(caps, out List<TechPattern> patterns))
            {
                Caps held = Caps.Parse(caps);
                _graftsFor[caps] = patterns = lib.Patterns.Where(p => p.Requires.IsSubsetOf(held)).ToList();
            }
            return patterns.Count == 0 ? null : new TechGrafts { Patterns = patterns, Key = lib.Key, CacheFile = lib.CacheFile };
        }

        /// <summary>The grafts for a window over goals k..limit: the unlocks held at goal k's start (the model's row) and the
        /// window's unlock buys.</summary>
        TechGrafts WindowGrafts(int k, int limit)
        {
            if (!GraftsOn) return null;
            Caps? from = _model != null && k >= 1 && k <= _model.Count && Caps.TryParse(_model[k - 1].Caps, out Caps c) ? c : (Caps?)null;
            return AutoGrafts(WindowCaps(_runGoals.Goals.Skip(k - 1).Take(limit - k + 1), from));
        }

        /// <summary>A grafting solver's share and focus: GraftShareIdle of its explore runs (--graft-rate), or, in a window whose
        /// goals hold audit findings (the best's inputs from prefixLength), GraftShareWaste with the findings' input span as its focus.</summary>
        void TuneGrafts(OfflineSolver solver, int k, int limit, int prefixLength)
        {
            if (solver.Mined == null) return;
            _graftSolvers++;
            solver.GraftRate = Solve.Option(_args, "--graft-rate", GraftShareIdle);
            if (k <= 0) return;
            List<Waste> spans = Findings().Where(x => IsMove(x.Kind) && x.Stake > 0 && x.Goal >= k && x.Goal <= limit && x.InFrom >= prefixLength).ToList();
            if (spans.Count == 0) return;
            solver.GraftRate = GraftShareWaste;
            solver.GraftFocus = (spans.Min(x => x.InFrom) - prefixLength, spans.Max(x => x.InTo) - prefixLength);
        }

        /// <summary>A window's find: counted by its tech when a graft made it.</summary>
        void GraftFind(string how)
        {
            if (how == null || !how.StartsWith("tech graft", StringComparison.Ordinal)) return;
            lock (_graftFinds) _graftFinds[how] = (_graftFinds.TryGetValue(how, out int was) ? was : 0) + 1;
        }

        void GraftTally(OfflineSolver solver) { if (solver.Mined != null) System.Threading.Interlocked.Add(ref _graftsTried, System.Threading.Interlocked.Read(ref solver.TechGrafted)); }

        /// <summary>The session's grafting, for the log's end.</summary>
        void LogGrafts()
        {
            if (!GraftsOn) return;
            lock (_graftFinds)
                Log($"auto: grafts: {_graftsTried} tech grafts tried over {_graftSolvers} windows; finds via a graft: "
                    + (_graftFinds.Count == 0 ? "none" : string.Join(", ", _graftFinds.OrderByDescending(f => f.Value).Select(f => $"{f.Key.Substring("tech graft ".Length)} {f.Value}"))));
        }
    }
}
