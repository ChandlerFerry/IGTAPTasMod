using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    static class Program
    {
        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                switch (args.FirstOrDefault())
                {
                    case "replay": return Replay(args.Skip(1).ToArray());
                    case "bench": return Bench(args.Skip(1).ToArray());
                    case "determinism": return Determinism.Run(args.Skip(1).ToArray());
                    case "enginebench": return EngineBench.Run(args.Skip(1).ToArray());
                    case "solve": return Solve.Run(args.Skip(1).ToArray());
                    case "searchbench": return SearchBench.Run(args.Skip(1).ToArray());
                    case "plan": return Plan.Run(args.Skip(1).ToArray());
                    case "polish": return Polish.Command(args.Skip(1).ToArray());
                    case "coverage": return Coverage.Run(args.Skip(1).ToArray());
                    case "humanize": return Humanize.Run(args.Skip(1).ToArray());
                    case "suggest": return Suggest.Run(args.Skip(1).ToArray());
                    case "techs": return Techs.Run(args.Skip(1).ToArray());
                    case "techs-used": return TechsUsed.Run(args.Skip(1).ToArray());
                    case "difficulty": return Difficulty.Run(args.Skip(1).ToArray());
                    case "timeloss": return TimeLoss.Run(args.Skip(1).ToArray());
                    case "map": return Map.Run(args.Skip(1).ToArray());
                    case "ultras": return Ultras.Run(args.Skip(1).ToArray());
                    case "fulltas": return FullTas.Run(args.Skip(1).ToArray());
                    case "macros": return FullTasAutoMacros.Command(args.Skip(1).ToArray());
                    case "fulltas-plan": return EconomyPlan.Run(args.Skip(1).ToArray());
                    case "plan-selftest": return EconomyPlanCheck.Run();
                    case "body-selftest": return BodyEnableCheck.Run();
                    case "fulltas-buytable": return FullTas.BuyTable(args.Skip(1).ToArray());
                    case "plan-check": return PlanCheck(args.Skip(1).ToArray());
                    case "rays": return Rays(args[1], args[2]);
                    case "grid": return Grid();
                    case "oneway": return OneWay();
                    case "probe": return Probe.Run(args.Skip(1).ToArray());
                    case "techgrafts": return TechGrafts.Command(args.Skip(1).ToArray());
                    case "routegrafts": return TechGrafts.RouteCommand(args.Skip(1).ToArray());
                    case "clip": return ClipLab.Run(args.Skip(1).ToArray());
                    case "lab": return Lab.Run(args.Skip(1).ToArray());
                    case "path":
                    {
                        // --canonical: the start as the searches load it (Simulation.LoadStart canonical: the category start's
                        // history and FloatingOrigin), not as captured.
                        var (sim, _, _) = Open(args[1], args.Contains("--canonical"));
                        List<InputTick> inputs = Plan.ReadRoute(args[2]);
                        bool verbose = args.Contains("--verbose");
                        string[] fields = { "Velocity", "momentum", "onGround", "OnWall", "cutsceneMode", "framesSinceLastDash", "airDashesLeft", "jumpBuffer", "wallCoyoteFrames",
                            "coyoteFrames", "canHyper", "lastHyperY", "preDashMomentum" };
                        var infos = fields.Select(f => typeof(Movement).GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).ToArray();
                        bool was = false;
                        int lastRun = 0;
                        ScenePos origin = sim.WorldOriginInScene;
                        var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                        FieldInfo counter = typeof(spikeScript).GetField("counter", bf), grace = typeof(spikeScript).GetField("graceFrames", bf),
                            onDash = typeof(spikeScript).GetField("killPlayerOnDash", bf), killed = typeof(spikeScript).GetField("playerKilled", bf);
                        spikeScript[] spikes = sim.World.All<spikeScript>();
                        RunResult r = sim.Run(inputs, extraTicks: 1000000, onStep: st =>
                        {
                            WorldPos pos = sim.PlayerWorld;
                            ScenePos now = sim.WorldOriginInScene;
                            if (now != origin)
                            {
                                Console.WriteLine($"tick {st}: REBASE origin {origin.x:0.0},{origin.y:0.0} -> {now.x:0.0},{now.y:0.0} player {pos.x:0.0},{pos.y:0.0}");
                                origin = now;
                            }
                            foreach (spikeScript s in spikes)
                                if ((int)counter.GetValue(s) > 0)
                                    Console.WriteLine($"tick {st}: SPIKE {s.name} at {sim.ToWorld(ScenePos.From(s.transform.position)).x:0},{sim.ToWorld(ScenePos.From(s.transform.position)).y:0} counter {counter.GetValue(s)} graceFrames {grace.GetValue(s)}"
                                        + $" killOnDash {onDash.GetValue(s)} playerKilled {killed.GetValue(s)} dashActive {sim.Player.dashActive}"
                                        + $" player {pos.x:0.0},{pos.y:0.0}");
                            bool timing = sim.TimingCourse() != null;
                            if (verbose)
                            {
                                int run = (int)sim.Engine.RunTicks - 1;
                                InputTick k = run >= 0 && run < inputs.Count ? inputs[run] : default;
                                string input = run + 1 == lastRun ? "(no input)" : $"in[{run}] x={k.x}{(k.y != 0f ? " y=" + k.y : "")}{(k.press ? " press" : "")}{(k.held ? " held" : "")}"
                                    + (k.release ? " release" : "") + (k.dash ? " dash" + (k.dashJump > 0 ? "+jump@" + k.dashJump : "") + (k.turnX.HasValue || k.turnY.HasValue ? " turn=" + k.turnX + (k.turnY.HasValue ? "," + k.turnY : "") : "") : "")
                                    + (k.springDash > 0 ? " springdash@" + k.springDash : "")
                                    + (k.lockX.HasValue || k.lockY.HasValue ? " lock=" + k.lockX + "," + k.lockY : "")
                                    + (k.pause > 0 ? " pause=" + k.pause + "@" + k.pauseTick + (k.pauseFrame > 0 ? "+" + k.pauseFrame : "") : "");
                                lastRun = run + 1;
                                Console.WriteLine($"tick {st}: pos {pos.x:0.0},{pos.y:0.0}"
                                    + (timing ? " t=" + Simulation.CourseTime(sim.TimingCourse()).ToString("0.00") : "") + " " + input + " | "
                                    + string.Join(" ", fields.Select((f, i) => f + "=" + infos[i]?.GetValue(sim.Player))));
                            }
                            else if (timing != was || st % 25 == 0)
                                Console.WriteLine($"tick {st}: pos {pos.x:0.0},{pos.y:0.0} timing {timing}"
                                    + (timing ? " t=" + Simulation.CourseTime(sim.TimingCourse()) : ""));
                            was = timing;
                        });
                        Console.WriteLine($"finished {r.Finished} time {r.Time} tick {r.FinishTick}" + (r.Finished ? "" : " (" + r.EndReason + ")"));
                        return 0;
                    }
                    default:
                        Console.Error.WriteLine("usage:\n  replay <trace.json>... [--verbose]   compare offline runs with turbo traces tick by tick\n"
                            + "  bench <trace.json> [seconds]           runs/s and ticks/s on one core\n"
                            + "  determinism <trace.json> <route.json> [--edges N] [--seed S] [--passes P] [--no-child]\n"
                            + "                                         whether a run from a snapshot depends on anything but the snapshot:\n"
                            + "                                         a fixed set of edges from snapshots along the route, in order, shuffled\n"
                            + "                                         and in a second process, every restore and end state digested\n"
                            + "  solve <start-trace.json> [--threads N] [--minutes M] [--seed <trace.json>]... [--no-submit] [--fresh]\n"
                            + "        [--via x0,y0,x1,y1]... [--avoid x0,y0,x1,y1]... [--path-name name] [--optional name]... [--no-optional name]...\n"
                            + "        [--guide] [--guide-weight W] [--rng N] [--log-json file] [--no-humanize] [--tech-cache file]\n"
                            + "                                         offline solver from that trace's start state; bests that beat\n"
                            + "                                         the seeds are confirmed in turbo (/api/enginesim/submit) when the game runs;\n"
                            + "                                         --via/--avoid force a path (boxes passed in order / boxes that cut a run);\n"
                            + "                                         --optional/--no-optional turns a named optional block of forced-paths.json on/off\n"
                            + "                                         (blocks with \"default\": true start on)\n"
                            + "                                         --no-tech-grafts grafts only the hand-written patterns, not the tech tab's mined demos;\n"
                            + "                                         --no-route-grafts drops the windows mined from the saved routes (routegrafts);\n"
                            + "                                         --tech-dir <dir> mines them from that folder (default: the configured traces\n"
                            + "                                         dir, the game's config enginesim folder when it exists, whatever trace is solved)\n"
                            + "                                         --guide (experimental, off by default) weights picks by the walkable\n"
                            + "                                         distance to the target; --guide-weight W is how hard it pulls (default 1);\n"
                            + "                                         --rng N seeds the search's randomness (worker i gets N + i) instead of\n"
                            + "                                         the clock, so a one-thread run repeats exactly\n"
                            + "                                         each new best also gets a human version (humanize; --no-humanize skips it)\n"
                            + "                                         --log-json <file>: one JSON line per new best (wall s, time, runTicks, ticks, cpu s)\n"
                            + "                                         --tech-cache <file>: where the tech grafts' mining is cached\n"
                            + "                                         --plan [--plan-minutes M]: plan (and polish) first, its route a seed\n"
                            + "  searchbench <suite.json> [--seeds N] [--minutes M] [--threads T] [--arms {name: [solve args]}] [--out file] [--keep]\n"
                            + "                                         solve per course, arm and seed (--rng 1..N) on temp trace copies;\n"
                            + "                                         median/min/max of the time to the first finish, to the target, and the best\n"
                            + "  searchbench --suite grafts [--check] [--case a,b] [--slack k] [--seeds N] [--minutes M] [--threads T] [--arms ...]\n"
                            + "                                         planted-waste cases (searchbench-grafts.json): a saved route with a waste\n"
                            + "                                         planted seeds solve (or fulltas --goal); per case the seeds that get within\n"
                            + "                                         k ticks of the original and the CPU s it took (--check: plant only)\n"
                            + "  plan <trace.json> [--threads N] [--minutes M] [--width W] [--max-width W] [--cap K] [--cell C] [--labels L] [--first]\n"
                            + "       [--guide-route <best.json>]... [--follow-ticks K] [--no-midair] [--no-chains] [--no-polish] [--no-tech-grafts] [--out file]\n"
                            + "       [--route <best.json> --from-goal N [--to-goal M] [--guide-probe <probe.json>]... [--chain K]] [--profile <route.json>]\n"
                            + "                                         beam search over exact snapshots (contact nodes, known primitives,\n"
                            + "                                         guide routes' cost to go and pieces), then polish; a Full-TAS trace plans\n"
                            + "                                         goals N+1..M of --route and splices its rest (--guide-probe: an old run's\n"
                            + "                                         1x probe as a trail; --chain K: plan up to K more goals where the rest\n"
                            + "                                         doesn't follow on); writes <traces>/plan/<label>-<time>.json\n"
                            + "  polish <trace.json> <route.json> [--threads N] [--radius R] [--slack S] [--no-wide] [--no-sync] [--farm] [--from-goal N [--to-goal M]] [--out file]\n"
                            + "                                         every input shifted, re-valued, re-framed or dropped in order, strict gains\n"
                            + "                                         kept until a sweep keeps none, then the plateau families (wider shifts, pairs,\n"
                            + "                                         stick bands, added presses and dashes); reports the ticks spent;\n"
                            + "                                         --farm: a course's farm time (quick restart to finish physics ticks) first\n"
                            + "  coverage <trace.json> <route-human.json>\n"
                            + "                                         each input's contact, planner primitive, mined window and planner grid value\n"
                            + "  humanize <start-trace.json> <best.json> [--threads N] [--via x0,y0,x1,y1]... [--avoid x0,y0,x1,y1]...\n"
                            + "                                         same-time version of a best with fewer perfect inputs and each\n"
                            + "                                         input's timing/value slack: <best>-human.json and <best>-human.md\n"
                            + "  suggest <start-trace.json> <route.json> --until N [--out file] [--threads T] [--radius R] [--max-shift S]\n"
                            + "                                         the player after the route's first N input ticks as a target: humanize's\n"
                            + "                                         edits of those ticks (shifts, stick values, drops) that get there sooner,\n"
                            + "                                         ranked by input ticks saved: <route>-suggest.json\n"
                            + "  techs <start-trace.json> <route.json> --at N --caps <id> [--out file.jsonl] [--threads T]\n"
                            + "                                         the techs that happen from input tick N (their key window placed and\n"
                            + "                                         the tech itself seen in it), each direction with its inputs and path,\n"
                            + "                                         a line each as found: <route>-techs.jsonl\n"
                            + "  techs --demos [tech ...] [--dump dir]   each tech's check on its demos and its labs' ghosts\n"
                            + "  techs-used <trace.json> <route.json> [--json [--out F]] [--no-difficulty] [--threads T] [--tech-weight <tech>=<s>] | techs-used --demos [tech ...] [--tech-dir D]\n"
                            + "                                         the nmg and major techs a route uses (every occurrence), its spike-grace\n"
                            + "                                         ticks, difficulty (a finished route's, unless --no-difficulty) and human\n"
                            + "                                         scores (course or Full-TAS trace; a route's \"goals\" are judged instead\n"
                            + "                                         of the trace's); --json: one line for the gate;\n"
                            + "                                         --demos: every tech demo, its own tech and no stray glitch\n"
                            + "  difficulty <trace.json> <route.json> [--json] [--radius R] [--pos U] [--vel F] [--raw] [--threads T]\n"
                            + "  difficulty <trace.json> --candidates <dir> [same options]\n"
                            + "                                         how hard the route's inputs are to time: humanize's version of it\n"
                            + "                                         (--raw: its own inputs), each input moved alone up to R ticks either\n"
                            + "                                         way; frame-perfect: works on one tick only; back-to-back: two actions\n"
                            + "                                         under 3 ticks (60 ms) apart that no timing inside the windows avoids;\n"
                            + "                                         --candidates: every *.json of dir into dir/difficulty/<name>\n"
                            + "  timeloss split <trace.json> <route.json> --ref <route.json | captured trace.json> [--within U] [--json]\n"
                            + "  timeloss search <trace.json> <route.json> [--segment k | --window a-b] [--depth 1|2|3] [--radius R] [--beam B] [--threads T]\n"
                            + "  timeloss floor <trace.json> <route.json> [--json]\n"
                            + "  timeloss report <trace.json> <route.json> [--ref <route.json | captured trace.json>] [--threads T] [--depth D] [--json]\n"
                            + "                                         where a course route loses time, per segment between its anchors\n"
                            + "                                         (difficulty's, on its own inputs): split: its ticks and the timer's\n"
                            + "                                         against a reference's; search: the best small input change in it, by\n"
                            + "                                         ticks saved at the finish; floor: its ticks over the walkable distance\n"
                            + "                                         to the next anchor / the top path speed (slack); report: all three\n"
                            + "  fulltas <trace.json> [--threads N] [--minutes total] [--window-minutes m] [--stall-minutes s] [--window W]\n"
                            + "          [--piece-minutes p] [--seed <file>]... [--seed-list <file>] [--no-submit] [--fresh] [--unsolved] [--goal N]\n"
                            + "          [--reset-study] [--lookahead N] [--rng N] [--log-json file] [--no-tech-grafts] [--graft-rate r]\n"
                            + "          [--class major|nmg|human-major|human-nmg] [--tag T] [--tech-weight <tech>=<s>]\n"
                            + "          | --check <route.json> [--goals FILE] [--chain-vs <old route.json>] | --waste-audit <route.json> [--expect a-b,..] [--clean kind:a-b,..]\n"
                            + "          | --auto [--parallel K] [--probe-minutes p] [--repair-minutes r] [--repair-cpu-per-tick c] [--repair-slack T] [--ref <route.json>]...\n"
                            + "                   [--parts LIST] [--library-runs R] [--focus a-b] [--tail-minutes t]\n"
                            + "                   [--combine-repair-minutes m]\n"
                            + "                                         Full-TAS search in rolling windows of W goals from a fulltas trace;\n"
                            + "                                         polishes the goals the best reaches from goal 1 (--unsolved: reaches\n"
                            + "                                         the goals it misses first); buys and laps are built from loop pieces\n"
                            + "                                         (archive/fulltas-pieces.json; a missing piece is searched for up to\n"
                            + "                                         p minutes, default 3); run state in <traces>/fulltas/<run>.json,\n"
                            + "                                         (--class C, --tag T: fulltas/<run>.C[.T].json, ranked by the class, the\n"
                            + "                                         human candidates in fulltas/candidates/<run>/; docs/fulltas.md \"Classes\"),\n"
                            + "                                         bests to the gate (/api/fulltas/submit) when the game runs; --seed:\n"
                            + "                                         saved course routes ({goals, standing, restart, ticks}) as window starts\n"
                            + "                                         (--seed-list: a file of seed paths, one per line);\n"
                            + "                                         --goal N: polish only the W goals ending at goal N (from goal N-W on),\n"
                            + "                                         over and over\n"
                            + "                                         --auto: the unattended loop (docs/fulltas.md \"Auto\"): per-goal lower\n"
                            + "                                         bounds and slow stretches rank the goals, a bandit over (lever, goal)\n"
                            + "                                         spends the CPU where ticks per CPU second are; --minutes 0 prints the table;\n"
                            + "                                         --parts lib,tail,combine,sched,ctg,macros,stray (default) or none: the goal-state library\n"
                            + "                                         with the wait fix, route-sync and tail windows, the pull scheduler, the\n"
                            + "                                         cost-to-go, macro seeds, stray-buy re-timing, avoid and revive (docs/fulltas.md\n"
                            + "                                         \"Parts\"); --focus a-b: the scheduler and levers pull windows starting in goals\n"
                            + "                                         a..b only (combine still splices anywhere)\n"
                            + "  macros [--tech-dir D] [--no-default] [--mine route.json]... [--measure route.json]... [--min-uses N] [--dump N]\n"
                            + "         [--samples N] [--out file]    mines --auto's macro seeds from the saved and judged records (--mine: more\n"
                            + "                                         routes) and measures how often one beats a held-out route's own next ticks\n"
                            + "  fulltas-plan <trace.json> --from <goal> --target <buy:box>[,<buy:box>] [--bonuses bonus1,...] [--buytable FILE] [--moves FILE]\n"
                            + "               [--max-states N] [--minutes M] [--dominance key|levels] [--only ids] [--out file]\n"
                            + "               [--mode exact|beam|auto] [--beam W] [--stand on|off] [--compare \"order\" ...] [--skip-free]\n"
                            + "               [--realize [--route file] [--seed-list file] [--piece-minutes p]] [--join route.json]\n"
                            + "               [--replan [--replan-seconds S] [--replan-cap T]]\n"
                            + "                                         the economy planner (docs/fulltas.md \"Economy planner\"): Dijkstra over\n"
                            + "                                         schedules of laps, record laps, buys (all:box every level paid for now;\n"
                            + "                                         atoms:N Area 2's atom box), bonuses and quick restarts from the\n"
                            + "                                         best's state at goal <goal> to the target buy, on the engine's own economy\n"
                            + "                                         (driven without the player) and the movement table's ticks; --compare plays\n"
                            + "                                         each order (steps joined by commas) exactly, then searches on; --mode beam:\n"
                            + "                                         at most W states (200) per 10 ticks of plan, ranked by tick and income, for\n"
                            + "                                         far targets (not proven optimal); auto (default): exact, the beam if capped;\n"
                            + "                                         --realize plays each plan with the loop builder from the best's real state,\n"
                            + "                                         measures every move (fulltas/<run>.moves.json), re-plans when the build is\n"
                            + "                                         over 10 ticks off (3 rounds), joins the best's way on; --out the goals, --route\n"
                            + "                                         the fastest whole route; --join joins a written route again;\n"
                            + "                                         --replan checks the goals part's call (budget, goal map) once\n"
                            + "  plan-selftest                          the planner on a toy economy against a brute force over every order\n"
                            + "  body-selftest                          b2Body.SetEnabled on a toy world: built disabled, enabled, disabled, restored\n"
                            + "  fulltas-buytable <trace.json> --from <goal> [--until <goal>] [--out file] [--max-set N] [--entry-minutes m] [--techniques walk,jump,dash] [--targets ids]\n"
                            + "                                         the measured time-to-buy table: per place the run stands at in the goal span\n"
                            + "                                         (its start, the course's start/restart/finish spots, each box), the ticks to buy\n"
                            + "                                         each box and each ordered 2-3-box set in one go per technique (walk,\n"
                            + "                                         jump-through, dash-through), each engine-confirmed; cached in\n"
                            + "                                         fulltas/<run>.buytable.json (the planner reads it);\n"
                            + "                                         --targets reward:bonusN,lap:N measures those from every place instead\n"
                            + "  plan-check <trace.json> <route.json> --at N [--ticks T] [--buy | --finish | --flip on|off | --stray BOX]\n"
                            + "                                         the economy drive (Simulation.EconomyIdle/Buy/Finish) against the full\n"
                            + "                                         simulation: T (2000) idle ticks where the player rests after goal N of a\n"
                            + "                                         Full-TAS trace (else input N), or goal N's buy or finish tick; PASS/FAIL\n"
                            + "  plan-check --moves <trace.json> [--buytable FILE]\n"
                            + "                                         the movement table (EconomyMoves.cs) against the best it is built\n"
                            + "                                         from: its splits, an estimate, the buy table, Measured, Save/Read\n"
                            + "  plan-check --tree-drop <trace.json>      the drive buys a tree box only once IE_Grow has dropped it onto its spot\n"
                            + "  rays <trace.json> <rays.json>          offline raycasts in the /api/enginesim/raycast format\n"
                            + "  probe <trace.json> <probe.json> [--out file]\n"
                            + "                                         one start state (the trace's, moved, or a studio savestate), branches of\n"
                            + "                                         inputs from it, every tick logged\n"
                            + "  routegrafts <traces dir or trace.json> [--refresh] [--tech-dir <dir>] [--tech-cache <file>]\n"
                            + "                                         every saved route (routes.json) replayed from its start trace and mined into\n"
                            + "                                         graft patterns, the fastest per start key; counts per unlock set and contact kind\n"
                            + "  techgrafts <traces dir or trace.json> [--refresh] [--tech-dir <dir>]\n"
                            + "                                         every tech tab demo replayed from its start and mined into graft patterns\n"
                            + "                                         (contact type, input window, unlocks); --refresh re-mines instead of\n"
                            + "                                         reading <traces>/tech-grafts.cache.json; the demos' traces, savestates and\n"
                            + "                                         the cache resolve from --tech-dir (default: the configured traces dir)\n"
                            + "  clip map <trace.json> x0 y0 x1 y1 [step] | clip run <trace.json> <lab.json> [--threads N] [--out csv] [--top N]\n"
                            + "     | clip log <trace.json> <lab.json> <start> <branch> [--fields a,b,c]\n"
                            + "                                         lab for any start state (inside walls too): a grid of starts x branches\n"
                            + "                                         on all cores, ranked by the height reached; one run logged per tick\n"
                            + "  map <start-trace.json> --out <file> [--seed <file>]... [--threads N] [--optional name]... [--no-optional name]...\n"
                            + "                                         the target's routes, archive coverage, colliders and forced-path\n"
                            + "                                         boxes as one JSON for the studio's Optimize map; a Full-TAS trace\n"
                            + "                                         takes --goals <labels.json> (the run's goals now, drawn over the trace's)\n"
                            + "  ultras <fulltas-VMAN.json> --out <file> [--courses <courses.json>] [--spacing U] [--threads N]\n"
                            + "                                         floors a hyper lands on and the game's ultras two chained wavedashes\n"
                            + "                                         get from launch points along them, for the studio's Ultra map\n");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        internal static (Simulation sim, JsonDocument trace, List<InputTick> inputs) Open(string tracePath, bool canonical = false)
        {
            string worldPath = Path.ChangeExtension(tracePath, null) + ".world.json";
            World world = World.Load(worldPath);
            var sim = new Simulation(world);
            JsonDocument trace;
            using (FileStream file = File.OpenRead(tracePath)) trace = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 256 });
            sim.SetTarget(trace.RootElement);
            sim.LoadStart(trace.RootElement.GetProperty("start"), canonical);
            var inputs = trace.RootElement.GetProperty("inputs").EnumerateArray().Select(InputTick.FromJson).ToList();
            return (sim, trace, inputs);
        }

        static int Replay(string[] args)
        {
            bool verbose = args.Contains("--verbose");
            // --ticks-out FILE: the offline economy per physics tick (FullTas.EconomyRow), as `fulltas --check --ticks-out` writes
            // it, for tools/economy-diff.py against a 1x probe of the same inputs.
            string ticksOut = Solve.Option(args, "--ticks-out", (string)null);
            using StreamWriter econOut = ticksOut == null ? null : new StreamWriter(ticksOut);
            int failures = 0;
            var assetRef = new System.Text.RegularExpressions.Regex("\\{\"\\$ref\":(-?\\d+)\\}");
            foreach (string path in args.Where(a => !a.StartsWith("--") && a != ticksOut && !a.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase)))
            {
                var (sim, trace, inputs) = Open(path);
                _normalize = s => assetRef.Replace(s, m => sim.World.Objects.ContainsKey(int.Parse(m.Groups[1].Value)) ? m.Value : "null");
                JsonElement[] ticks = trace.RootElement.GetProperty("ticks").EnumerateArray().ToArray();
                JsonElement result = trace.RootElement.GetProperty("result");
                FieldInfo[] movementFields = FieldState.For(typeof(Movement));
                int firstDiff = -1;
                int firstRandomDiff = -1, firstCashDiff = -1;
                var diffs = new List<string>();
                string cashLine = null, randomLine = null, bigCashLine = null;
                RunResult run = sim.Run(inputs, extraTicks: 1000000, onStep: step =>
                {
                    Dictionary<string, object> econ = econOut == null && (step >= ticks.Length || !ticks[step].TryGetProperty("econ", out _)) ? null : FullTas.EconomyRow(sim);
                    econOut?.WriteLine(JsonSerializer.Serialize(new { physics = sim.Engine.PhysicsTicks, input = sim.Engine.RunTicks, econ }));
                    if (step >= ticks.Length || firstDiff >= 0 && !verbose) return;
                    var here = new List<string>();
                    JsonElement t = ticks[step];
                    Compare(here, "position", Json(sim.Player.transform.position), t.GetProperty("position"));
                    Compare(here, "bodyPosition", Json(sim.Physics.GetPosition(sim.Player.GetComponent<Rigidbody2D>())), t.GetProperty("bodyPosition"));
                    Compare(here, "velocity", Json(sim.Physics.GetVelocity(sim.Player.GetComponent<Rigidbody2D>())), t.GetProperty("velocity"));
                    foreach (JsonElement entry in t.GetProperty("movement").EnumerateArray())
                    {
                        FieldInfo f = World.FindField(typeof(Movement), entry[0].GetString(), entry[1].GetString());
                        if (f == null || Simulation.MovementPresentationFields.Contains(f.Name)) continue;
                        Compare(here, f.Name, Json(f.GetValue(sim.Player)), entry[2]);
                    }
                    foreach (JsonElement course in t.GetProperty("courses").EnumerateArray())
                    {
                        if (!sim.World.Objects.TryGetValue(course.GetProperty("id").GetInt32(), out UnityEngine.Object o) || !(o is courseScript c)) continue;
                        foreach (JsonElement entry in course.GetProperty("fields").EnumerateArray())
                        {
                            string name = entry[1].GetString();
                            if (name != "tracking" && name != "currentPathTime") continue;
                            FieldInfo f = World.FindField(typeof(courseScript), entry[0].GetString(), name);
                            Compare(here, "course" + c.courseNumber + "." + name, Json(f.GetValue(c)), entry[2]);
                        }
                    }
                    if (t.TryGetProperty("cash", out JsonElement cash) && Compare(here, "cash", Json(sim.Shop.CashNow), cash) && firstCashDiff < 0) firstCashDiff = step;
                    // Turbo's economy (the plugin's TasPlugin.EconomyNow on a trace tick) against the engine's, field by field (the
                    // grid's call count is the session's in the plugin and the run's here: tools/economy-diff.py lines up its steps).
                    if (t.TryGetProperty("econ", out JsonElement turboEcon))
                        foreach (JsonProperty f in turboEcon.EnumerateObject())
                            if (f.Name != "cash" && f.Name != "gridCalls" && econ.TryGetValue(f.Name, out object mine)) Compare(here, f.Name, Json(mine), f.Value);
                    if (bigCashLine == null && cash.ValueKind == JsonValueKind.Number && Math.Abs(sim.Shop.CashNow - cash.GetDouble()) >= 1.0)
                        bigCashLine = "tick " + step + ": cash " + Json(sim.Shop.CashNow) + " != " + cash.GetRawText();
                    if (t.TryGetProperty("random", out JsonElement random))
                    {
                        UnityEngine.Random.State rng = UnityEngine.Random.state;
                        if (Compare(here, "random", Json(new[] { rng.s0, rng.s1, rng.s2, rng.s3 }), random) && firstRandomDiff < 0) firstRandomDiff = step;
                    }
                    if (here.Count == 0) return;
                    if (firstCashDiff == step) cashLine = "tick " + step + ": " + string.Join("; ", here);
                    if (firstRandomDiff == step) randomLine = "tick " + step + ": " + string.Join("; ", here);
                    if (firstDiff < 0) firstDiff = step;
                    if (diffs.Count < 60) diffs.Add("tick " + step + ": " + string.Join("; ", here));
                });
                bool sameResult = run.Finished == result.GetProperty("finished").GetBoolean()
                    && Json(run.Time) == result.GetProperty("time").GetRawText();
                Console.WriteLine(Path.GetFileName(path) + ": " + run.Stepped + "/" + ticks.Length + " ticks, "
                    + (firstDiff < 0 ? "every tick identical" : "first difference at tick " + firstDiff)
                    + "; random " + (firstRandomDiff < 0 ? "identical on every tick" : "first difference at tick " + firstRandomDiff)
                    + ", cash " + (firstCashDiff < 0 ? "identical on every tick" : "first difference at tick " + firstCashDiff)
                    + ", result " + (run.Finished ? "finish " + Json(run.Time) : "no finish (" + run.EndReason + ")")
                    + " vs turbo " + (result.GetProperty("finished").GetBoolean() ? "finish " + result.GetProperty("time").GetRawText() : "no finish")
                    + (sameResult ? " [MATCH]" : " [MISMATCH]"));
                foreach (string d in diffs.Take(verbose ? 60 : 3)) Console.WriteLine("  " + d);
                // A late economy split hides behind 60 earlier position diffs: name its first tick either way.
                foreach (string d in new[] { cashLine, randomLine, bigCashLine }.Where(d => d != null && !diffs.Take(verbose ? 60 : 3).Contains(d)))
                    Console.WriteLine("  first split " + d);
                if (firstDiff >= 0 || !sameResult || run.Stepped != ticks.Length) failures++;
            }
            return failures == 0 ? 0 : 3;
        }

        static Func<string, string> _normalize = s => s;

        static bool Compare(List<string> diffs, string name, string offline, JsonElement turbo)
        {
            // A string compares by its value: the trace's escapes are Newtonsoft's ("+" raw), this side's System.Text.Json's.
            string expected = Numbers(_normalize(turbo.ValueKind == JsonValueKind.String ? Json(turbo.GetString()) : turbo.GetRawText()));
            string actual = Numbers(offline);
            if (actual == expected) return false;
            diffs.Add(name + " " + Short(offline) + " != " + Short(turbo.GetRawText()));
            return true;
        }

        static readonly System.Text.RegularExpressions.Regex Number = new System.Text.RegularExpressions.Regex(@"-?\d+(\.\d+)?([eE][-+]?\d+)?");

        static string Numbers(string s)
        {
            return Number.Replace(s, m =>
            {
                string digits = m.Value.Split('e', 'E')[0].Replace("-", "").Replace(".", "").TrimStart('0');
                return digits.Length <= 9
                    ? float.Parse(m.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
                    : double.Parse(m.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            });
        }

        static string Short(string s) { return s.Length > 120 ? s.Substring(0, 120) + "..." : s; }

        internal static string Json(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, int depth, HashSet<object> visiting)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case UnityEngine.Object o:
                    if (!o) sb.Append("null");
                    else sb.Append("{\"$ref\":").Append(o.GetInstanceID()).Append('}');
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "\"" + f.ToString(CultureInfo.InvariantCulture) + "\"" : f.ToString("G9", CultureInfo.InvariantCulture));
                    return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "\"" + d.ToString(CultureInfo.InvariantCulture) + "\"" : d.ToString("G17", CultureInfo.InvariantCulture));
                    return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: sb.Append(JsonSerializer.Serialize(s)); return;
                case LayerMask m: sb.Append(m.value); return;
                case Delegate _: case Type _: case MemberInfo _: case IntPtr _: case Coroutine _: case IEnumerator _: sb.Append("null"); return;
            }
            Type type = value.GetType();
            if (type.IsEnum) { sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)); return; }
            if (type.IsPrimitive) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (depth > 8 || !type.IsValueType && !visiting.Add(value)) { sb.Append("null"); return; }
            try
            {
                if (value is IDictionary dict)
                {
                    sb.Append("{\"$dict\":[");
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('[');
                        Write(sb, e.Key, depth + 1, visiting);
                        sb.Append(',');
                        Write(sb, e.Value, depth + 1, visiting);
                        sb.Append(']');
                    }
                    sb.Append("]}");
                    return;
                }
                if (value is IEnumerable seq)
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in seq)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item, depth + 1, visiting);
                    }
                    sb.Append(']');
                    return;
                }
                if (!type.IsValueType && type.Assembly != typeof(Movement).Assembly) { sb.Append("null"); return; }
                sb.Append('{');
                bool firstField = true;
                for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
                    foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(Delegate).IsAssignableFrom(field.FieldType)) continue;
                        if (!firstField) sb.Append(',');
                        firstField = false;
                        sb.Append(JsonSerializer.Serialize(t == type ? field.Name : t.Name + "::" + field.Name)).Append(':');
                        Write(sb, field.GetValue(value), depth + 1, visiting);
                    }
                sb.Append('}');
            }
            finally
            {
                if (!type.IsValueType) visiting.Remove(value);
            }
        }

        /// <summary>A one-way avoid box (OfflineSolver.Box.From) cuts a move in across its entry edge and nothing else: Course 3's
        /// avoid-backwards (from bottom) cuts the climb from the start, a fast one through it included, and not the farm lap's drop.</summary>
        static int OneWay()
        {
            var box = new OfflineSolver.Box(new WorldPos(9446.9f, 7369.6f), new WorldPos(9694.1f, 7484f), 0, IGTAPTasMod.FullTasGoal.FromBottom);
            var right = new OfflineSolver.Box(new WorldPos(9620.2f, 6908.6f), new WorldPos(9694.5f, 7170.5f), 0, IGTAPTasMod.FullTasGoal.FromRight);
            void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException("oneway: " + what); }
            Check(box.Cuts(new WorldPos(9500, 7360), new WorldPos(9500, 7380)), "climb in from below");
            Check(box.Cuts(new WorldPos(9500, 7300), new WorldPos(9520, 7550)), "climb through in one tick");
            Check(!box.Cuts(new WorldPos(9500, 7500), new WorldPos(9500, 7470)), "drop in from above");
            Check(!box.Cuts(new WorldPos(9500, 7400), new WorldPos(9500, 7380)), "falling inside");
            Check(!box.Cuts(new WorldPos(9400, 7300), new WorldPos(9420, 7380)), "climb beside it");
            Check(!box.Cuts(new WorldPos(9500, 6000), new WorldPos(9500, 7400)), "respawn inside");
            Check(right.Cuts(new WorldPos(9700, 7000), new WorldPos(9690, 7000)) && !right.Cuts(new WorldPos(9600, 7000), new WorldPos(9630, 7000)), "from right");
            Check(new OfflineSolver.Box(new WorldPos(0, 0), new WorldPos(10, 10)).Cuts(new WorldPos(5, 5), new WorldPos(5, 5)), "two-way box inside");
            Console.WriteLine("oneway: ok");
            return 0;
        }

        /// <summary>
        /// The 280 fps clock's frames (MacroEngine.FrameGrid), checked against a 280 fps probe (grid280): the restart's crossing,
        /// and on the run's cycle 28 frames in five ticks, lagging the next tick 0.36, 2.50, 1.07, 3.21, 1.79 ms, frame times as
        /// 1x reads them and a dash's 0.05 s freeze holding 13 frames.
        /// </summary>
        static int Grid()
        {
            MacroEngine.FrameGrid grid = MacroEngine.FrameGrid.Playback;
            double frame = grid.Frame;
            void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException("grid: " + what); }
            Check(BitConverter.SingleToInt32Bits(grid.Step(0, 0)) == 0x3b0c6f0f, "restart crossing");
            Check(grid.Frames(0).Alphas.Length == 6 && grid.Frames(1) == grid.Frames(0) && grid.FrozenFrames(1, 0.05f) == 14, "restart span");
            double[] lags = { 0.357, 2.500, 1.071, 3.214, 1.786 };
            int frames = 0;
            for (long t = 2; t < 7; t++)
            {
                MacroEngine.FrameGrid.Span span = grid.Frames(t), next = grid.Frames(t + 1);
                double lag = (0.02 - span.Last) * 1000;
                frames += span.Alphas.Length;
                Console.WriteLine($"ticks {t}: {span.Alphas.Length} frames, first {span.First * 1000:0.000} ms, lag {lag:0.000} ms, cross {grid.Step(t, 0) * 1000:0.000000000} ms,"
                    + $" alphas {string.Join(" ", span.Alphas.Select(a => a.ToString("0.0000")))}");
                Check(Math.Abs(lag - lags[(t - 1) % 5]) < 0.001, "lag at " + t);
                Check(Math.Abs(next.First + 0.02 - span.Last - frame) < 1e-12, "continuity at " + t);
                Check(BitConverter.SingleToInt32Bits(grid.Step(t, 1)) == 0x3b6a0ea1 && BitConverter.SingleToInt32Bits(grid.Step(t, 0)) == 0x3b6a0e82, "steps at " + t);
                Check(grid.FrozenFrames(t, 0.05f) == 13 && grid.FrozenFrames(t, 0.06f) == 16, "freezes at " + t);
            }
            Check(frames == 28, "28 frames in five ticks");
            Console.WriteLine("grid ok");
            return 0;
        }

        static int Rays(string tracePath, string raysPath)
        {
            var (sim, _, _) = Open(tracePath);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(raysPath));
            var output = new List<object>();
            foreach (JsonElement r in doc.RootElement.GetProperty("rays").EnumerateArray())
            {
                RaycastHit2D hit = Physics2D.Raycast(new Vector2(r[0].GetSingle(), r[1].GetSingle()), new Vector2(r[2].GetSingle(), r[3].GetSingle()),
                    r[4].GetSingle(), r[5].GetInt32());
                output.Add(hit.collider == null ? null : new object[] { hit.collider.GetInstanceID(), G9(hit.point.x), G9(hit.point.y),
                    G9(hit.normal.x), G9(hit.normal.y), G9(hit.distance), G9(hit.fraction) });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, hits = output }));
            return 0;
        }

        static string G9(float f) { return f.ToString("G9", CultureInfo.InvariantCulture); }

        /// <summary>
        /// plan-check &lt;trace&gt; &lt;route&gt; --at N [--ticks T] [--buy | --finish | --flip on|off | --stray BOX]: the economy drive
        /// (Simulation.EconomyIdle, EconomyBuy, EconomyFinish: Runtime\EconomyDrive.cs) against the full simulation from the same
        /// state. N is a goal of a Full-TAS trace (1-based, as `fulltas --check` numbers them), else an input tick; 0 the trace's
        /// start (idle only). The run's buys are strict, as `fulltas --check` plays them.
        /// Idle: the route to N, then no input until the player rests (3 still ticks) and a snapshot there; T (2000) more idle
        /// ticks of the full simulation against EconomyIdle(T) where the player stands.
        /// --buy / --finish: the state a physics tick before goal N's purchase (the first from N on) or course finish, the full
        /// simulation's next tick against EconomyBuy / EconomyFinish of it.
        /// --flip on|off: the first physics step from goal N on where the route's player takes a course's clones on (off) screen
        /// (a clonesLoD trigger's enter or exit), the full simulation's step against EconomyIdle(1) where the step leaves the
        /// player.
        /// --stray BOX: where the player rests after goal N, EconomyIdle(T) and then EconomyBuy(BOX), a box that isn't the next
        /// goal's: the drive must neither stop nor move the run's goals.
        /// Every check also asserts the drive left the goal tracker where it was. Prints each economy field PASS or FAIL (the
        /// random state: skipped where the full ticks drew for the player, its sounds, which the drive has no player for, and
        /// made as many other draws as the drive); exits 1 on a FAIL.
        /// </summary>
        static int PlanCheck(string[] args)
        {
            if (args.Contains("--moves")) return FullTas.MovesCheck(args.Where(a => a != "--moves").ToArray());
            if (args.Contains("--tree-drop")) return EconomyPlanCheck.TreeDrop(args.First(a => a != "--tree-drop"));
            string tracePath = args[0];
            List<InputTick> route = Plan.ReadRoute(args[1]);
            int at = Solve.Option(args, "--at", -1), idle = Solve.Option(args, "--ticks", 2000);
            bool buy = args.Contains("--buy"), finish = args.Contains("--finish");
            string flip = Solve.Option(args, "--flip", (string)null), stray = Solve.Option(args, "--stray", (string)null);
            if (at < 0 || at == 0 && (buy || finish || flip != null || stray != null))
                throw new ArgumentException("--at N: a goal (Full-TAS trace) or an input tick; 0, the start, for an idle check");
            Simulation sim = Solve.Creator(tracePath, Solve.ReadHeader(tracePath))();
            sim.StrictBuys = true;
            bool goals = sim.Goals != null;
            string where = at == 0 ? "the trace's start" : goals ? "goal " + at + " " + sim.Goals.Goals[at - 1].label : "input " + at;
            // The physics step goal N is reached on (or input tick N played on; --buy: the first from there that buys), the inputs
            // played by then and the boxes bought on it.
            int reached = -1, played = 0;
            var bought = new List<string>();
            if (at > 0)
            {
                upgradeBox[] boxes = sim.World.All<upgradeBox>();
                int[] used = boxes.Select(b => b.TimesUsed).ToArray();
                sim.Run(route, extraTicks: 1000000, afterTick: _ => reached < 0, onStep: s =>
                {
                    if (reached >= 0) return;
                    bool there = goals ? sim.Progress >= at : sim.Engine.RunTicks >= at;
                    for (int i = 0; i < boxes.Length; i++)
                        if (boxes[i].TimesUsed != used[i])
                        {
                            if (there) bought.Add(sim.Shop.Id(boxes[i]));
                            used[i] = boxes[i].TimesUsed;
                        }
                    if (!there || buy && bought.Count == 0) return;
                    reached = s;
                    played = (int)sim.Engine.RunTicks;
                });
                if (reached < 0) { Console.WriteLine($"FAIL: the route never reaches {where}" + (buy ? " and buys" : "")); return 1; }
            }
            clonesScript[] clonesScripts = sim.World.All<clonesScript>();
            bool[] OnScreen() => clonesScripts.Select(c => sim.Shop.Economy?.OnScreenOf(c) ?? false).ToArray();
            SimSnapshot snap = null;
            Vector2 place = default;
            SortedDictionary<string, string> full = null;
            // The full ticks' draws by caller, and those made for the player (Movement's sounds, footsteps, respawn).
            var fullDraws = new List<string>();
            int playerDraws = 0;
            bool counting = false;
            Action<string> countFull = caller =>
            {
                if (!counting) return;
                if (new StackTrace(false).GetFrames().Any(f => f.GetMethod()?.DeclaringType == typeof(Movement))) playerDraws++;
                else fullDraws.Add(caller);
            };
            UnityEngine.Random.Drawn = countFull;
            string box = null;
            float time = 0f;
            int course = 0;
            if (flip != null)
            {
                bool on = flip == "on";
                // Pass 1: the step the route's player flips a course's clones, and the course.
                int flipped = -1, which = -1;
                bool[] before = null;
                sim.Run(route, extraTicks: 1000000, afterTick: _ => flipped < 0, onStep: s =>
                {
                    bool[] now = OnScreen();
                    if (flipped < 0 && s > reached && before != null)
                        for (int i = 0; i < now.Length; i++)
                            if (now[i] == on && before[i] != on) { flipped = s; which = i; break; }
                    before = now;
                });
                if (flipped < 0) { Console.WriteLine($"FAIL: no course's clones go {flip} screen after {where}"); return 1; }
                // Pass 2: the state before it, and its step, where the route's player stands is the drive's place.
                bool agrees = false;
                sim.Run(route, extraTicks: 1000000, afterTick: _ => full == null, onStep: s =>
                {
                    if (s == flipped - 1) snap = sim.Capture((int)sim.Engine.RunTicks);
                    counting = s == flipped - 1;   // the next step's draws: the draw callback runs within it
                    if (s != flipped) return;
                    full = DriveFields(sim);
                    place = sim.Player.transform.position;
                    agrees = sim.InsideLod(clonesScripts[which], place) == on;
                });
                Console.WriteLine($"flip {flip}: course {clonesScripts[which].GetComponentInParent<courseScript>()?.courseNumber}'s clones on physics step {flipped} "
                    + $"at ({place.x:0.0}, {place.y:0.0}), from {where}");
                if (!agrees) { Console.WriteLine($"FAIL: the player's boxes there are {(on ? "outside" : "inside")} the trigger in the drive's test"); return 1; }
                counting = false;
            }
            else if (!buy && !finish)
            {
                if (at == 0)
                {
                    snap = sim.Start;
                    sim.Restore(snap);
                    place = sim.Player.transform.position;
                    counting = true;
                    sim.Run(Enumerable.Repeat(new InputTick(), idle + 1000).ToList(), extraTicks: 0, afterTick: _ => full == null, onStep: s =>
                    {
                        if (s == idle - 1) { full = DriveFields(sim); counting = false; }
                    });
                }
                else
                {
                    var inputs = route.Take(played).Concat(Enumerable.Repeat(new InputTick(), idle + 1000)).ToList();
                    int still = 0, from = -1;
                    sim.Run(inputs, extraTicks: 0, afterTick: _ => full == null, onStep: s =>
                    {
                        if (snap != null)
                        {
                            if (s == from + idle) { full = DriveFields(sim); counting = false; }
                            return;
                        }
                        if (s < reached) return;
                        bool rest = sim.Player.onGround && sim.Player.Velocity.sqrMagnitude < 1e-4f && sim.Player.momentum.sqrMagnitude < 1e-4f;
                        still = rest ? still + 1 : 0;
                        if (still < 3) return;
                        snap = sim.Capture((int)sim.Engine.RunTicks);
                        place = sim.Player.transform.position;
                        from = s;
                        counting = stray == null;
                    });
                }
                if (full == null) { Console.WriteLine($"FAIL: the player doesn't rest for {idle} ticks after {where}"); return 1; }
                Console.WriteLine($"idle: {idle} ticks at ({place.x:0.0}, {place.y:0.0}), from {where}");
            }
            else
            {
                sim.Run(route, extraTicks: 1000000, afterTick: _ => full == null, onStep: s =>
                {
                    counting = s == reached - 1;
                    if (s == reached - 1)
                    {
                        snap = sim.Capture((int)sim.Engine.RunTicks);
                        place = sim.Player.transform.position;
                        course = goals ? sim.Goals.Current.course : sim.TargetCourse;
                    }
                    else if (s == reached)
                    {
                        full = DriveFields(sim);
                        time = sim.LastCourseTime;
                    }
                });
                counting = false;
                if (snap == null || full == null) { Console.WriteLine($"FAIL: no physics step before {where}"); return 1; }
                if (buy && bought.Count != 1) { Console.WriteLine($"FAIL: {bought.Count} purchases on {where}'s step ({string.Join(", ", bought)}): EconomyBuy is one touch"); return 1; }
                if (buy) box = bought[0];
                Console.WriteLine((buy ? "buy " + box : "finish of course " + course + " in " + time.ToString("R", CultureInfo.InvariantCulture) + " s")
                    + $" on physics step {reached} from ({place.x:0.0}, {place.y:0.0}), {where}");
            }
            UnityEngine.Random.Drawn = null;
            sim.RestoreForDrive(snap);
            int progress = sim.Progress;
            IEnumerable<object[]> clones = sim.Shop.Economy?.CloneStates() ?? Enumerable.Empty<object[]>();
            Console.WriteLine("clones: " + string.Join(", ", clones.Select(c => "course " + c[0] + " " + c[1] + ((bool)c[3] ? " on screen" : " off screen"))));
            var driveDraws = new List<string>();
            UnityEngine.Random.Drawn = driveDraws.Add;
            try
            {
                if (stray != null)
                {
                    sim.EconomyIdle(idle, place);
                    Console.WriteLine($"EconomyBuy {stray} after {idle} idle ticks: " + (sim.EconomyBuy(stray, place) ? "bought" : "didn't buy"));
                }
                else if (buy) Console.WriteLine("EconomyBuy: " + (sim.EconomyBuy(box, place) ? "bought" : "didn't buy"));
                else if (finish) sim.EconomyFinish(course, time);
                else sim.EconomyIdle(flip != null ? 1 : idle, place);
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("FAIL drive: " + ex.Message);
                return 1;
            }
            finally { UnityEngine.Random.Drawn = null; }
            int failed = 0;
            if (sim.Progress == progress) Console.WriteLine($"PASS goals (the drive left the run at goal {progress})");
            else { failed++; Console.WriteLine($"FAIL goals: the drive moved the run from goal {progress} to {sim.Progress}"); }
            if (stray != null)
            {
                Console.WriteLine(failed == 0 ? "PASS" : "FAIL");
                return failed == 0 ? 0 : 1;
            }
            SortedDictionary<string, string> drive = DriveFields(sim);
            // Counted, not named: the caller a draw reports depends on what the JIT inlined (Economy.RollCount into SpawnClone).
            full["draws"] = fullDraws.Count.ToString(CultureInfo.InvariantCulture);
            drive["draws"] = driveDraws.Count.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine($"draws: full {fullDraws.Count} (and {playerDraws} for the player), drive {driveDraws.Count}");
            foreach (string key in full.Keys.Union(drive.Keys))
            {
                full.TryGetValue(key, out string a);
                drive.TryGetValue(key, out string b);
                if (a == b) { Console.WriteLine($"PASS {key}"); continue; }
                if (key == "random" && playerDraws > 0 && full["draws"] == drive["draws"]) { Console.WriteLine($"SKIP {key} (the full ticks drew {playerDraws} for the player)"); continue; }
                failed++;
                Console.WriteLine($"FAIL {key}\n  full:  {Clip(a)}\n  drive: {Clip(b)}");
            }
            Console.WriteLine(failed == 0 ? "PASS" : $"FAIL ({failed} fields)");
            return failed == 0 ? 0 : 1;
        }

        static string Clip(string s) { return s == null ? "(none)" : s.Length <= 3000 ? s : s.Substring(0, 3000) + "..."; }

        static readonly FieldInfo BoxCooldown = typeof(upgradeBox).GetField("boxCooldown", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>plan-check's fields: each currency held, the economy's clones, upgrades and atoms (Economy.State), the shop's
        /// spends and buy-max routines, the random state, the scheduler's Invokes and parked coroutines (but the player's, a jump
        /// buffer's cancel: the drive has no player), the courses' rewards and comp boosts,
        /// globalStats' NP and clone dust readouts, the boxes and trees, and the run's physics ticks.</summary>
        static SortedDictionary<string, string> DriveFields(Simulation sim)
        {
            var options = new JsonSerializerOptions { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
            string J(object v) => JsonSerializer.Serialize(v, options);
            var f = new SortedDictionary<string, string>();
            f["currencies"] = J(Enumerable.Range(0, 7).Select(c => sim.Shop.Holds((globalStats.Currencies)c)).ToArray());
            Economy.State e = sim.Shop.Economy?.Capture();
            if (e != null)
            {
                f["clones"] = J(e.Values);
                f["clones.active"] = J(e.Active);
                f["income"] = J(e.Income);
                f["upgrades"] = J(new { e.Locals, e.Global });
                f["atoms"] = J(new { e.Atoms, e.Crafting, e.CraftProgress, e.WaitingAtoms, e.HandledTier, e.TierUpCounter, e.IsCrafting, e.GridDue, e.OpenA2Door });
                f["economy"] = J(new { e.TripBroken, e.TransitionOpensIn, e.CdMult, woken = e.Woken.Length, starting = e.Starting.Length });
            }
            Shop.State shop = sim.Shop.Capture();
            f["shop"] = J(new { shop.Spent, shop.Awarded, buyMax = shop.BuyMax.Select(r => sim.Shop.Id(r.Box) + " " + r.Left).ToArray() });
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            f["random"] = J(new[] { rng.s0, rng.s1, rng.s2, rng.s3 });
            Scheduler q = sim.Scheduler;
            f["scheduler"] = J(new
            {
                invokes = q.Queue.Where(p => !(p.Target is Movement)).Select(p => p.Target.name + "." + p.Method + " in " + (p.Due - q.Tick) + " every " + p.Repeat).ToArray(),
                routines = q.Routines.Where(r => !(r.Target is Movement)).Select(r => r.Target.name + " " + r.Wait + (r.Wait == Scheduler.Wait.Seconds ? " in " + (r.Due - q.Tick) : "")).ToArray(),
            });
            f["courses"] = J(sim.Courses.Select(c => new object[] { c.courseNumber, c.reward, c.gpReward, c.rpReward, c.CdReward, c.compBoostTimeLeft, c.compBoostBonus, c.rewardTier }).ToArray());
            Dictionary<string, object> row = FullTas.EconomyRow(sim);
            row.Remove("gridCalls");   // a diagnostic count, not in snapshots
            f["globalStats"] = J(row);
            f["boxes"] = J(sim.World.All<upgradeBox>().Select(b => new object[] { b.TimesUsed, b.upgradeCost, b.isActive, (float)BoxCooldown.GetValue(b) }).ToArray());
            f["physicsTicks"] = J(new { sim.Engine.PhysicsTicks, sim.Engine.HeldFrames });
            return f;
        }

        static int Bench(string[] args)
        {
            var (sim, _, inputs) = Open(args[0]);
            double seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 5;
            sim.Run(inputs);
            var clock = Stopwatch.StartNew();
            long runs = 0, ticks = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                ticks += sim.Run(inputs).Stepped;
                runs++;
            }
            double s = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"{runs / s:0.0} runs/s, {ticks / s:0} ticks/s, {s * 1e6 / ticks:0.00} us/tick ({inputs.Count} inputs); "
                + $"{sim.Physics.BodyCount} bodies, {sim.Physics.ContactCount} contacts, {sim.Physics.TouchingPairs} touching pairs, "
                + $"GC gen0 {GC.CollectionCount(0)} gen2 {GC.CollectionCount(2)}, allocated {GC.GetTotalAllocatedBytes() / 1e9:0.0} GB");
            return 0;
        }
    }
}
