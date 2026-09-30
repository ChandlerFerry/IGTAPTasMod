using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts wallreset and --wallreset-study (docs/fulltas.md, "Parts"; the Tech tab's "Wall coyote restart",
    // checkpoint-midair-walljump's family): Movement.respawn leaves wallCoyoteFrames and wallJumpsLeft alone, and the ground
    // branch never counts wallCoyoteFrames down, so a wall cling just before a quick restart (or any cling followed only by ground
    // ticks) starts the next lap with a stored wall jump: after the lap's first ground jump, a jump press on the 2nd-7th tick in
    // the air is a wall jump off no wall (Velocity.y 730, 460 sideways), or the superbounce's boost within 5 ticks of a dash.
    //
    // The part grafts that tech the way the solver grafts any tech: a window search over the goals around each quick restart
    // whose OfflineSolver draws the mined windows of the wall-coyote techs (TechGrafts, techs.json) at the wall contacts of its
    // explore runs, nothing else, then the usual merge. Once per restart per session (and remembered).
    sealed partial class FullTas
    {
        /// <summary>The tech graft library an Explore hands its OfflineSolver (null: none, as every other window).</summary>
        TechGrafts _mined;
        /// <summary>The modes ("tech graft &lt;id&gt;", "explore", ...) of the finds of the window running, when a caller wants them.</summary>
        HashSet<string> _findModes;

        /// <summary>The techs.json entries the part grafts, and the contacts of a wall (the touch, the leaving, a wall ahead) they
        /// graft at: the windows of these techs at a landing or a ledge would restart in the middle of a lap.</summary>
        static readonly string[] WallResetTechs = { "checkpoint-midair-walljump", "wall-coyote-restart" };
        static readonly Contact[] WallResetContacts = { Contact.TouchedWall, Contact.LeftWall, Contact.WallAheadLeft, Contact.WallAheadRight };
        /// <summary>Parts --auto leaves off unless --parts names them (unmeasured in the loop).</summary>
        static readonly string[] OptInParts = { "wallreset" };

        Route _wallFor;
        string _wallStudiedFor;
        readonly HashSet<(int goal, int offset)> _wallStudied = new HashSet<(int, int)>();
        TechGrafts _wallLibrary;
        bool _wallLibraryTried;

        const BindingFlags MovementFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo WallCoyoteField = typeof(Movement).GetField("wallCoyoteFrames", MovementFields);

        /// <summary>The wall state of the player after an input tick.</summary>
        struct WallState
        {
            public float X, Y, Coyote;
            public bool OnWall, Ground, Facing, WallJump;
            public int WallJumps;
        }

        /// <summary>The player's wall state after each of the first <paramref name="until"/> input ticks of the inputs (by the
        /// count of inputs consumed: index t is the state after t inputs).</summary>
        WallState[] WallTrace(List<InputTick> inputs, int until)
        {
            var states = new WallState[until + 1];
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs.GetRange(0, Math.Min(until, inputs.Count)), null, tick =>
            {
                if (tick <= until)
                {
                    Movement m = _eval.Player;
                    WorldPos p = _eval.PlayerWorld;
                    states[tick] = new WallState { X = p.x, Y = p.y, Coyote = (float)WallCoyoteField.GetValue(m), OnWall = m.OnWall, Ground = m.onGround,
                        Facing = m.facingRight, WallJump = m.wallJumpUnlocked, WallJumps = m.wallJumpsLeft };
                }
                return tick < until;
            }, 2);
            return states;
        }

        /// <summary>The inputs with the stick held toward dir on the n ticks before input q (the buttons as they were).</summary>
        static List<InputTick> HoldToward(List<InputTick> inputs, int q, int n, float dir)
        {
            List<InputTick> v = inputs.ToList();
            for (int i = Math.Max(0, q - n); i < q; i++)
            {
                InputTick t = v[i];
                t.x = dir;
                v[i] = t;
            }
            return v;
        }

        /// <summary>
        /// --wallreset-study: every quick restart of the best (the --focus goals): where the player is and what it carries through
        /// (the wall coyote count after the restart), and which holds of the stick toward a wall on the ticks before it (each side,
        /// 1-10 ticks, the buttons as they were) leave a count above 0 there: "L3:7" a hold left for 3 ticks, 7 carried, an x when
        /// the change moves a goal before the restart.
        /// </summary>
        void WallResetStudy()
        {
            Route best = _best;
            List<int> resets = Enumerable.Range(1, best.Inputs.Count - 1).Where(i => best.Inputs[i].reset && GoalsBy(best, i) < best.Progress && InFocus(GoalsBy(best, i) + 1)).ToList();
            Log($"wallreset study: {resets.Count} quick restarts in the best");
            WallState[] states = WallTrace(best.Inputs, best.Inputs.Count);
            foreach (int q in resets)
            {
                int g = GoalsBy(best, q);
                long from = g == 0 ? 0 : best.GoalInput[g - 1];
                WallState s = states[q + 1], before = states[q];
                var found = new List<string>();
                foreach (float dir in new[] { -1f, 1f })
                    for (int n = 1; n <= 10; n++)
                    {
                        List<InputTick> v = HoldToward(best.Inputs, q, n, dir);
                        WallState[] t = WallTrace(v, q + 1);
                        if (!(t[q + 1].Coyote > 0f)) continue;
                        Route r = Evaluate(v.GetRange(0, q + 1), g);
                        bool keeps = r.Progress >= g && Enumerable.Range(0, g).All(k => r.GoalPhysics[k] == best.GoalPhysics[k]);
                        found.Add($"{(dir < 0 ? "L" : "R")}{n}:{t[q + 1].Coyote:0}{(keeps ? "" : "x")}");
                    }
                Log($"reset at input {q} (goal {g + 1} {(g < _goals ? _labels[g] : "")}, {q - from} after goal {g}): before ({before.X:0},{before.Y:0}) ground {before.Ground} "
                    + $"wall {before.OnWall} wallJump {before.WallJump} wc {before.Coyote:0}; after ({s.X:0},{s.Y:0}) ground {s.Ground} wc {s.Coyote:0}; "
                    + $"hold variants: {(found.Count == 0 ? "none" : string.Join(" ", found))}");
            }
        }

        /// <summary>The mined windows of <see cref="WallResetTechs"/> at <see cref="WallResetContacts"/>, alone (null: the catalog has
        /// none). TechGrafts.Load reads the mining cache of the configured traces dir, or mines the Tech tab's demos.</summary>
        TechGrafts WallResetLibrary()
        {
            if (_wallLibraryTried) return _wallLibrary;
            _wallLibraryTried = true;
            try
            {
                TechGrafts all = TechGrafts.Load(Path.GetDirectoryName(Path.GetFullPath(_trace)), techDir: Solve.Option(_args, "--tech-dir", (string)null));
                all.Patterns.RemoveAll(p => Array.IndexOf(WallResetTechs, p.Tech) < 0 || Array.IndexOf(WallResetContacts, p.Kind) < 0 || !p.Resets);
                if (all.Patterns.Count == 0)
                {
                    Log("wallreset: no wall-coyote tech window is mined (techs.json " + string.Join(", ", WallResetTechs) + ")");
                    return null;
                }
                Log($"wallreset: {all.Patterns.Count} tech windows: " + string.Join(", ", all.Patterns.GroupBy(p => p.Tech + " " + p.Kind).Select(g => $"{g.Key} x{g.Count()}")));
                return _wallLibrary = all;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.Text.Json.JsonException)
            {
                Log("wallreset: the tech windows didn't load: " + ex.GetBaseException().Message);
                return null;
            }
        }

        /// <summary>--wallreset-minutes: the most one pass over a best's restarts takes (default 5).</summary>
        double WallResetMinutes { get { return Solve.Option(_args, "--wallreset-minutes", 5.0); } }

        /// <summary>
        /// The quick restarts of the best a wall-coyote graft can use, by their goal g (goals reached before it: the window is goals
        /// g..g+2, the touch of goal g before the restart, the lap after): the wall wall jump is unlocked by then, and not studied
        /// yet. Ordered by how near a wall cling is before them (input ticks since the last tick on a wall, the least first).
        /// </summary>
        List<(int g, int sinceWall, List<int> resets)> WallTargets(Route best)
        {
            string goalsKey = string.Join(",", _labels);
            if (_wallStudiedFor != null && _wallStudiedFor != goalsKey) _wallStudied.Clear();
            _wallStudiedFor = goalsKey;
            WallState[] states = WallTrace(best.Inputs, best.Inputs.Count);
            var lastWall = new int[states.Length];
            int seen = int.MinValue / 2;
            for (int t = 0; t < states.Length; t++)
            {
                if (states[t].OnWall) seen = t;
                lastWall[t] = seen;
            }
            var byGoal = new Dictionary<int, List<int>>();
            for (int q = 1; q < best.Inputs.Count; q++)
            {
                if (!best.Inputs[q].reset) continue;
                int g = GoalsBy(best, q);
                if (g < 1 || g >= best.Progress || !InFocus(g + 1) || !states[q].WallJump) continue;
                if (!byGoal.TryGetValue(g, out List<int> list)) byGoal[g] = list = new List<int>();
                list.Add(q);
            }
            return byGoal.Select(kv => (g: kv.Key, since: kv.Value.Min(q => q - lastWall[q]), resets: kv.Value))
                .Where(x => x.resets.Any(q => !_wallStudied.Contains((x.g, q - (int)best.GoalInput[x.g - 1]))))
                .OrderBy(x => x.since).ThenBy(x => x.g).Select(x => (x.g, x.since, x.resets)).ToList();
        }

        /// <summary>
        /// One pass over the best's restarts (--parts wallreset), each goal's a window pull: the goals g..g+2 searched from the
        /// state after goal g-1 with the wall-coyote tech windows grafted at the wall contacts of the search's runs, merged as any
        /// window's find. Nothing is kept unless the whole run gets faster (MergeAuto). A pass takes at most --wallreset-minutes;
        /// what it didn't get to waits for the next best (or the next session: the memory keeps the restarts studied).
        /// </summary>
        void WallResetPass()
        {
            Route best = _best;
            _wallFor = best;
            TechGrafts library = WallResetLibrary();
            if (library == null) return;
            List<(int g, int sinceWall, List<int> resets)> targets = WallTargets(best);
            Log($"auto: wallreset: {targets.Count} restart goals to study on the {Describe(best)} best"
                + (targets.Count == 0 ? "" : $" (walls: {string.Join(", ", targets.Take(8).Select(t => $"goal {t.g} {(t.sinceWall > 1000000 ? "-" : t.sinceWall + " ticks")}"))}{(targets.Count > 8 ? ", ..." : "")})"));
            TimeSpan begin = _clock.Elapsed;
            foreach (var target in targets)
            {
                if (TimeUp || (_clock.Elapsed - begin).TotalMinutes >= WallResetMinutes || !ReferenceEquals(_best, best)) break;
                WallResetPull(best, target.g, target.resets, library);
            }
        }

        void WallResetPull(Route best, int g, List<int> resets, TechGrafts library)
        {
            foreach (int q in resets) _wallStudied.Add((g, q - (int)best.GoalInput[g - 1]));
            int prefixLength = PrefixAt(best, g), limit = Math.Min(best.Progress, g + 2);
            _pullNo++;
            int pull = _pullNo;
            Route before = _best;
            double c0 = CpuSeconds();
            TimeSpan w0 = _clock.Elapsed;
            if (!_arms.TryGetValue(("wallreset", g), out Arm arm)) _arms[("wallreset", g)] = arm = new Arm { Lever = "wallreset", Goal = g };
            Log($"auto: pull {pull}: wallreset at goal {g} ({_labels[g - 1]}), restarts at inputs {string.Join(",", resets)}, window goals {g}-{limit}, budget {_windowMinutes:0.00} min");
            string outcome;
            try
            {
                var seeds = new List<List<InputTick>> { best.Inputs.Skip(prefixLength).ToList() };
                _mined = library;
                _findModes = new HashSet<string>();
                List<List<InputTick>> found;
                try { found = SearchWindow("wallreset", best, prefixLength, limit, seeds, best.GoalPhysics[limit - 1], _windowMinutes, 0); }
                finally { _mined = null; }
                string modes = _findModes.Count == 0 ? "-" : string.Join(", ", _findModes);
                _findModes = null;
                if (found.Count == 0) outcome = $"goals {g}-{limit}: nothing faster";
                else
                {
                    foreach (List<InputTick> f in Enumerable.Reverse(found).Take(5)) Collect(Evaluate(best.Inputs.Take(prefixLength).Concat(f).ToList()), $"wallreset of goal {g} #{pull}");
                    MergeAuto($"faster through goals {g}-{limit} (wallreset, auto)", best, prefixLength, limit, found);
                    outcome = $"goals {g}-{limit}: {found.Count} faster through goal {limit}, via {modes}";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is NullReferenceException || ex is IndexOutOfRangeException)
            {
                _mined = null;
                _findModes = null;
                outcome = "failed: " + ex.GetBaseException().Message;
            }
            Account(arm, pull, before, CpuSeconds() - c0, (_clock.Elapsed - w0).TotalSeconds, outcome, _windowMinutes, double.NaN);
        }
    }
}
