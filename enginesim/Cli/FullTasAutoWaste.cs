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
        // Slow ground: progress over SlowWindow ticks under WasteSlowShare of the top path speed for the unlocks, the rise under
        // SlowRise; its stake counts SustainShare of that speed as what the span could make.
        const int SlowWindow = 50;
        const float WasteSlowShare = .8f, SustainShare = .9f, SlowRise = 150f, FlatRise = 20f;
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
                if (p != capsFor) { capsFor = p; caps = PlayerCaps.Of(m).Id; }
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
                    Fresh = fresh, Press = fresh && t.press, Release = fresh && t.release, Reset = fresh && t.reset });
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
                    float rate = DistTo(i - 1, k) - DistTo(i, k);
                    if (rate > 0f && !Jumped(s[i - 1], s[i])) rates.Add(rate);
                }
                rates.Sort();
                attain[k] = rates.Count == 0 ? 1 : Math.Max(1, rates[(int)(rates.Count * .8)]);
            }
            long Raw(int a, int b)
            {
                int k = GoalOf(a);
                double made = DistTo(a, k) - DistTo(b, k);
                return Math.Max(0, s[b].Tick - s[a].Tick - (long)Math.Round(Math.Max(0, made) / attain[k]));
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
                    && Enumerable.Range(s[restarts[e]].Progress, s[restarts[e + 1]].Progress - s[restarts[e]].Progress).All(g => _labels[g].StartsWith("start:", StringComparison.Ordinal))) e++;
                if (e > c)
                {
                    int q0 = restarts[c], q1 = restarts[e], p = s[q0 - 1].Progress;
                    long goal = p == 0 ? 0 : r.GoalPhysics[p - 1], anchor = s[q0].Tick - goal <= ChainLead ? goal : s[q0 - 1].Tick;
                    int a = q0 - 1;
                    while (a > 0 && s[a].Tick > anchor) a--;
                    // The seed: the last restart played right after the anchor (its inputs up to there cut).
                    Add("filler", a, q1, Math.Max(0, s[q1].Tick - s[a].Tick - 1), $"{e - c + 1} restarts in {s[q1].Tick - s[q0].Tick} ticks at ticks "
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
                long raw = Math.Max(0, s[at[k]].Tick - s[q].Tick - Math.Max(BuyCooldown, (long)Math.Round(before / attain[k])));
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
            // Slow ground: flat progress well under the top flat speed for the unlocks (the fastest 10 ticks the run makes sideways
            // with under FlatRise of rise: a dash's, not a fall's), where movement pays (no lap, spare Cash).
            var flatTop = new Dictionary<string, float>();
            for (int i = 0; i + SpeedWindow < n; i++)
            {
                int b = i + SpeedWindow;
                if (s[b].Caps != s[i].Caps || Math.Abs(s[b].Y - s[i].Y) >= FlatRise || Enumerable.Range(i + 1, SpeedWindow).Any(m => Jumped(s[m - 1], s[m]))) continue;
                float v = Math.Abs(s[b].X - s[i].X) / SpeedWindow;
                if (!flatTop.TryGetValue(s[i].Caps, out float top) || v > top) flatTop[s[i].Caps] = v;
            }
            for (int k = 1; k <= goals; k++)
            {
                GoalRow row = rows[k - 1];
                if (row.Economy || row.Spare <= 0) continue;
                int i = at[k - 1] + 1, open = -1;
                float vref = 1f;
                for (; i + SlowWindow <= at[k]; i++)
                {
                    int b = i + SlowWindow;
                    vref = flatTop.TryGetValue(s[i].Caps, out float v) ? Math.Max(1f, v) : 1f;
                    float made = DistTo(i, k) - DistTo(b, k);
                    bool flat = Math.Abs(s[b].Y - s[i].Y) < SlowRise && !Enumerable.Range(i + 1, SlowWindow).Any(m => Jumped(s[m - 1], s[m]));
                    bool slow = flat && made > 0 && made / SlowWindow < WasteSlowShare * vref;
                    if (slow && open < 0) open = i;
                    if (!slow && open >= 0) { AddSlow(open, i - 1 + SlowWindow, vref); open = -1; }
                }
                if (open >= 0) AddSlow(open, Math.Min(at[k], i - 1 + SlowWindow), vref);
            }
            void AddSlow(int a, int b, float vref)
            {
                float made = DistTo(a, GoalOf(a)) - DistTo(b, GoalOf(a));
                long raw = Math.Max(0, s[b].Tick - s[a].Tick - (long)Math.Round(made / (SustainShare * vref)));
                if (raw >= 3) Add("slow", a, b, raw, $"{made / Math.Max(1, s[b].Tick - s[a].Tick):0.0} u/tick over {s[b].Tick - s[a].Tick} ticks against {vref:0.0} for {s[a].Caps}");
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
        /// --waste-audit &lt;route.json&gt; [--expect a-b,c-d]: the audit of a route (a run file's "inputs" or an offline best's "ticks")
        /// under the trace's goals, every finding with its span, ticks at stake and kind. --expect: physics tick spans each finding
        /// must overlap (one line each, exit 1 when one is missed).
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
            string expect = Solve.Option(_args, "--expect", (string)null);
            if (expect == null) return 0;
            int missed = 0;
            foreach (string span in expect.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] ends = span.Split('-');
                long a = long.Parse(ends[0], CultureInfo.InvariantCulture), b = long.Parse(ends[^1], CultureInfo.InvariantCulture);
                List<Waste> hits = found.Where(x => x.From <= b && x.To >= a).ToList();
                if (hits.Count == 0) missed++;
                Console.WriteLine($"expect {a}-{b}: " + (hits.Count == 0 ? "MISSED" : "flagged by " + string.Join(", ", hits.Select(x => $"{x.Kind} goal {x.Goal} {x.From}-{x.To} stake {x.Stake}"))));
            }
            Console.WriteLine(missed == 0 ? "expect: all flagged" : $"expect: {missed} missed");
            return missed == 0 ? 0 : 1;
        }

        /// <summary>The route <see cref="_wasteFound"/> audits: a model built on another route (the goals part's ModelOf) audits
        /// that one, and the best's audit is made again when next asked for.</summary>
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
        /// jump dropped (press to release). What follows is the best's own, for the search to mend.
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
                    default: continue;
                }
                seeds.Add(v.Skip(prefixLength).ToList());
            }
            return seeds;
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
                if (all.Patterns.Count == 0) { Log("auto: grafts: the tech library is empty"); return null; }
                Log($"auto: grafts: {all.Patterns.Count} tech windows of {all.Patterns.Select(p => p.Tech).Distinct().Count()} techs ({(all.FromCache ? "cache" : "mined")})");
                return _graftLib = all;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is JsonException)
            {
                Log("auto: grafts: the tech library didn't load: " + ex.GetBaseException().Message);
                return null;
            }
        }

        /// <summary>The library's windows the unlocks allow (a techs.json "requires" within them), for one solver (null: --parts
        /// grafts is off, or no --auto). Each solver gets its own instance: OfflineSolver prepares it for its caps.</summary>
        TechGrafts AutoGrafts(string caps)
        {
            if (!_auto || !Part("grafts") || GraftLibrary() is not TechGrafts lib) return null;
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
            if (!_auto || !Part("grafts")) return null;
            Caps? from = _model != null && k >= 1 && k <= _model.Count && Caps.TryParse(_model[k - 1].Caps, out Caps c) ? c : (Caps?)null;
            return AutoGrafts(WindowCaps(_runGoals.Goals.Skip(k - 1).Take(limit - k + 1), from));
        }

        /// <summary>A grafting solver's share and focus: GraftShareIdle of its explore runs, or, in a window whose goals hold audit
        /// findings (the best's inputs from prefixLength), GraftShareWaste with the findings' input span as its focus.</summary>
        void TuneGrafts(OfflineSolver solver, int k, int limit, int prefixLength)
        {
            if (solver.Mined == null) return;
            _graftSolvers++;
            solver.GraftRate = GraftShareIdle;
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
            if (!_auto || !Part("grafts")) return;
            lock (_graftFinds)
                Log($"auto: grafts: {_graftsTried} tech grafts tried over {_graftSolvers} windows; finds via a graft: "
                    + (_graftFinds.Count == 0 ? "none" : string.Join(", ", _graftFinds.OrderByDescending(f => f.Value).Select(f => $"{f.Key.Substring("tech graft ".Length)} {f.Value}"))));
        }
    }
}
