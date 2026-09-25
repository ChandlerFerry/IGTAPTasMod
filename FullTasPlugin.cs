using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    // Full-TAS (FullTas.cs): the whole game from a fresh new game (NewGameStart) as one
    // continuous input stream, scored in physics ticks from the start to its last goal. Its route
    // ("fulltas/<run>_NO_WJ <time>") is saved only by the gate (SaveSolverRoute), which replays it in turbo from the new
    // game's start with the goals tracked: unlocks come only from the run's own purchases, quick restarts are its inputs.
    public sealed partial class TasPlugin
    {
        // fulltas.json: [{"name": "main", "goals": ["start:1", "finish:1", "buy:buyreward", "reward:bonus1", ...]}].
        public sealed class FullTasRunDef
        {
            // Letters, digits and "%": route names and offline-best files carry it.
            public string name;
            public List<string> goals = new List<string>();
        }

        // The one RNG write (README "### Rules"): every Full-TAS new game starts Unity's RNG from this fixed seed,
        // UnityEngine.Random.InitState(FullTasSeed) before its start snapshot (CaptureNewGameStart).
        const int FullTasSeed = 20260925;

        // Physics ticks from the run start when a goal was reached (FullTasTracker.PhysicsTicks).
        public sealed class FullTasSplit
        {
            public string label;
            public long ticks;
        }

        // A turbo judge of a Full-TAS route: whether it reached every goal, its time, whether the gate saved it, and why not.
        sealed class FullTasVerdict
        {
            public bool ok = true, finished, saved;
            public float time;
            public long runTicks;
            public string reason;
            public List<FullTasSplit> splits;
        }

        // A studio playback of a Full-TAS route in real time, queued until the fresh game's first tick (CaptureNewGameStart).
        sealed class FullTasPlay
        {
            public FullTasRunDef run;
            public MacroDefinition route;
            public FullTasTracker tracker;
            public bool waitingForNewGame = true, started;
        }

        // A request that needs the fresh new game at its start (a turbo judge, a trace): answered once it is there.
        sealed class FullTasPending
        {
            public WebStudioRequest request;
            public string what;
            public Func<object> action;
            public int since;
            // A new game was loaded for it: not being fresh after that is a failure, not a reason to load another.
            public bool loaded;
        }

        List<FullTasRunDef> _fullTasRuns = new List<FullTasRunDef>();
        DateTime _fullTasWritten;
        float _fullTasCheckAt;
        // Route id -> its splits, as the gate or a playback of it measured them (fulltas-splits.json).
        Dictionary<string, List<FullTasSplit>> _fullTasSplits = new Dictionary<string, List<FullTasSplit>>();

        // The goal tracker of the Full-TAS run in progress (a turbo judge or trace, or a playback); null otherwise. While it
        // is set, no course, bonus, transition or purchase target ends the run (RunCourse is null): only its last goal does.
        FullTasTracker _fullTas;
        // The unlocks the run's own purchases gave so far: the gate's unlock check (AuditTick).
        bool _fullTasWallJump, _fullTasDash, _fullTasAirJump;
        // BeforeCourseStart: the course was timing before this startTracking call.
        bool _fullTasStartWasTracking;
        // BeforeCourseFinish: the course whose timer this stopTracking call stops; -1 none.
        int _fullTasFinishCourse = -1;
        // The gate's Full-TAS target (SolverCategory, RouteViolation, SaveSolverRoute); null for every other target.
        FullTasRunDef _solverFullTas;
        FullTasPlay _fullTasPlay;
        FullTasPending _fullTasPending;
        int _fullTasParkedFrame = -1000;
        string _fullTasLastResult;
        // Bonus name -> its reward box's id (FullTasRewardBox), once the world had it.
        readonly Dictionary<string, string> _fullTasRewardBoxes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The Full-TAS tab's offline search (`IGTAP.EngineSim fulltas`) and what it printed.
        Process _fullTasProcess;
        readonly ConcurrentQueue<string> _fullTasOutput = new ConcurrentQueue<string>();
        string _fullTasSolveRun;
        string _fullTasSolveStatus = "Idle";
        readonly List<string> _fullTasLog = new List<string>();

        static readonly Regex FullTasToken = new Regex(@"^(start|finish|buy|reward):(.+)$");
        static readonly Regex FullTasRouteName = new Regex(@"^fulltas/([A-Za-z0-9%]+)_NO_WJ ", RegexOptions.IgnoreCase);
        static readonly Regex OfflineFullTasBestFile = new Regex(@"^fulltas-([A-Za-z0-9%]+)-([\d.]+)\.json$");
        // buy: tokens and their boxes (FullTasGoal.BoxId: hierarchy path in the Overworld scene plus sibling index —
        // Transform.GetSiblingIndex, the index within the box's direct parent, "SwapBlocksOnce/endDemo" being one box's
        // name; paths aren't unique: course 2's GLOBALcashPerLoop #3 is the 55000 W global reward box, #5 bonus1's free
        // reward box, which reward:bonus1 buys).
        static readonly (string token, string path, int sibling)[] FullTasBoxes = {
            ("buyreward", "Courses/course 1/localUpgrades/CashPerLoop", 0),
            ("buyclone", "Courses/course 1/localUpgrades/Clone", 1),
            ("buywj", "Courses/course 1/localUpgrades/Wall jump", 2),
            ("buyreward2", "Courses/course 2/localUpgrades/CashPerLoop", 0),
            ("buyclone2", "Courses/course 2/localUpgrades/Clone", 1),
            ("buyclonemult2", "Courses/course 2/localUpgrades/CloneMult", 2),
            ("buyglobalreward2", "Courses/course 2/localUpgrades/GLOBALcashPerLoop", 3),
            ("buydash", "Courses/course 2/localUpgrades/DashUnlock", 4),
            ("buyreward3", "Courses/course 3/localUpgrades/CashPerLoop", 0),
            ("buyclone3", "Courses/course 3/localUpgrades/CloneCount", 1),
            ("buyclonemult3", "Courses/course 3/localUpgrades/CloneMult", 2),
            ("buyfast3", "Courses/course 3/localUpgrades/FastCloneChance", 3),
            ("buyglobalreward3", "Courses/course 3/localUpgrades/GLOBALcash", 4),
            ("buyaj", "Courses/course 3/localUpgrades/Double jump", 5),
            ("buyfree3", "Courses/course 3/localUpgrades/GLOBALWatt", 6),
            ("buyclonespeed", "Courses/course 3/localUpgrades/GLOBALclonespeedmax", 9),
            ("buystatue", "Courses/course 3/localUpgrades/enablePrestige", 10),
            ("buyreward4", "Courses/course 4/localUpgrades/CashPerloop", 0),
            ("buyclone4", "Courses/course 4/localUpgrades/Clone", 1),
            ("buyclonemult4", "Courses/course 4/localUpgrades/CloneMult", 2),
            ("buybig4", "Courses/course 4/localUpgrades/BigCloneChance", 3),
            ("buyfast4", "Courses/course 4/localUpgrades/FastCloneChance", 4),
            ("buymaxsize4", "Courses/course 4/localUpgrades/MaxCloneSize", 5),
            ("buyglobalreward4", "Courses/course 4/localUpgrades/MaxCloneSize", 6),
            ("buyswap", "Courses/course 4/localUpgrades/SwapBlocksOnce/endDemo", 7),
            ("buyfree4a", "Courses/course 4/localUpgrades/GLOBALfastCloneChance", 8),
            ("buyfree4b", "Courses/course 4/localUpgrades/GLOBALcashperloop", 9),
            ("buyfree4c", "Courses/course 4/localUpgrades/GLOBALcash", 10),
            ("buyprestige4", "Courses/course 4/localUpgrades/PrestigeUpgradeBox4", 11),
            ("buyreward5", "Courses/course 5/localUpgrades/cashPerLoop", 0),
            ("buyclone5", "Courses/course 5/localUpgrades/Clones", 1),
            ("buyclonemult5", "Courses/course 5/localUpgrades/Clone mult", 2),
            ("buyglobalreward5", "Courses/course 5/localUpgrades/global cash ", 3),
            ("buybig5", "Courses/course 5/localUpgrades/Big clone chance", 4),
            ("buyfast5", "Courses/course 5/localUpgrades/Fast clone chance", 5),
            ("buyprestige5", "Courses/course 5/localUpgrades/prestige upgrade box", 6),
            ("buyblockswap5", "Courses/course 5/localUpgrades/SwapBlocksOnDash", 7),
            ("buyfree5", "Courses/course 5/localUpgrades/GLOBALCash", 8) };
        // Buy tokens whose box trips the breaker (upgradeBox.tripBreaker: BoostOldCourses resets Courses 1-5, Cash goes
        // to 0 and Area 1's lights go out); the studio marks the goals from there on.
        static readonly HashSet<string> FullTasBreakerTokens = new HashSet<string> { "buyprestige5" };
        // The most boxes one buy goal may name.
        const int MaxBuyGoalBoxes = 8;

        string FullTasPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas.json"); } }
        string FullTasSplitsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-splits.json"); } }
        string FullTasTracePath(string run) { return Path.Combine(EngineSimDir, "fulltas-" + run + ".json"); }
        static string FullTasCategory(string run) { return "fulltas/" + run + "_" + CapabilityLabels["none"]; }
        // The category the run's own purchases put it in so far (dash without wall jump, or air jump without both, is
        // the lower label: the audit flags it).
        string FullTasCaps { get { return _fullTasWallJump ? (_fullTasDash ? (_fullTasAirJump ? "wjdashaj" : "wjdash") : "wj") : "none"; } }

        // ---- Config

        void LoadFullTas()
        {
            try
            {
                if (File.Exists(FullTasSplitsPath))
                    _fullTasSplits = JsonConvert.DeserializeObject<Dictionary<string, List<FullTasSplit>>>(File.ReadAllText(FullTasSplitsPath))
                        ?? new Dictionary<string, List<FullTasSplit>>();
            }
            catch (Exception ex) { Logger.LogWarning("fulltas-splits.json rejected: " + ex.Message); }
            try
            {
                string text = File.Exists(FullTasPath) ? File.ReadAllText(FullTasPath) : "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _fullTasWritten = File.GetLastWriteTimeUtc(FullTasPath);
                    _fullTasRuns = JsonConvert.DeserializeObject<List<FullTasRunDef>>(text) ?? new List<FullTasRunDef>();
                    return;
                }
            }
            catch (Exception ex) { Logger.LogWarning("fulltas.json rejected: " + ex.Message); return; }
            _fullTasRuns = new List<FullTasRunDef>();
            SaveFullTasRuns();
        }

        void SaveFullTasRuns()
        {
            WriteJsonAtomic(FullTasPath, _fullTasRuns, false);
            _fullTasWritten = File.GetLastWriteTimeUtc(FullTasPath);
        }

        FullTasRunDef FindFullTasRun(string name)
        {
            return _fullTasRuns.Find(r => string.Equals(r.name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("No Full-TAS run named \"" + name + "\" in fulltas.json.");
        }

        // A run's goals, in order; throws on a token that doesn't resolve.
        FullTasGoal[] ResolveFullTasGoals(FullTasRunDef run)
        {
            if (!RunName.IsMatch(run.name ?? "")) throw new InvalidOperationException("Full-TAS run names are letters, digits and \"%\" only (\"" + run.name + "\").");
            if (run.goals == null || run.goals.Count == 0) throw new InvalidOperationException("Full-TAS run " + run.name + " has no goals.");
            return run.goals.Select(ResolveFullTasGoal).ToArray();
        }

        // "start:<N>" / "finish:<N>" (a course), "buy:<t>[+<t>...]" (FullTasBoxes tokens), "reward:bonus<N>" (the game buying
        // the bonus's reward box, FullTasRewardBox; the goal keeps the bonus's end rect, courses.json endX/endY/endW/endH).
        FullTasGoal ResolveFullTasGoal(string token)
        {
            string text = (token ?? "").Trim().ToLowerInvariant();
            Match m = FullTasToken.Match(text);
            if (!m.Success) throw new InvalidOperationException("Unknown Full-TAS goal \"" + token + "\": start:<course>, finish:<course>, buy:<box>[+<box>...] or reward:bonus<N>.");
            string kind = m.Groups[1].Value, arg = m.Groups[2].Value.Trim();
            var goal = new FullTasGoal { kind = kind };
            if (kind == FullTasGoal.Start || kind == FullTasGoal.Finish)
            {
                if (!int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int course)
                    || !_courses.Any(c => c.id == course && !c.IsBonus && !c.IsTransition))
                    throw new InvalidOperationException("Full-TAS goal \"" + token + "\": course " + arg + " is not in courses.json.");
                goal.course = course;
                goal.label = kind + ":" + course;
            }
            else if (kind == FullTasGoal.Buy)
            {
                string[] parts = arg.Split('+').Select(p => p.Trim()).ToArray();
                int[] boxes = parts.Select(p => Array.FindIndex(FullTasBoxes, b => b.token == p)).ToArray();
                if (boxes.Any(i => i < 0))
                    throw new InvalidOperationException("Full-TAS goal \"" + token + "\": boxes are " + string.Join(", ", FullTasBoxes.Select(b => b.token)) + " (joined with \"+\").");
                if (parts.Length > MaxBuyGoalBoxes) throw new InvalidOperationException("Full-TAS goal \"" + token + "\" buys more than " + MaxBuyGoalBoxes + " boxes.");
                goal.boxes = boxes.Select(i => FullTasGoal.BoxId(FullTasBoxes[i].path, FullTasBoxes[i].sibling)).ToArray();
                goal.label = "buy:" + string.Join("+", parts);
            }
            else
            {
                CourseDef bonus = _courses.Find(c => c.IsBonus && string.Equals(c.name, arg, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Full-TAS goal \"" + token + "\": no bonus " + arg + " with a reward box in courses.json.");
                goal.x = bonus.endX; goal.y = bonus.endY; goal.w = bonus.endW; goal.h = bonus.endH;
                string box = FullTasRewardBox(bonus);
                if (box != null) goal.boxes = new[] { box };
                goal.label = "reward:" + bonus.Token.ToLowerInvariant();
            }
            return goal;
        }

        // Creates or replaces a run's goals (validated, stored as their labels).
        FullTasRunDef SetFullTasGoals(string name, IEnumerable<string> tokens)
        {
            var run = new FullTasRunDef { name = (name ?? "").Trim(), goals = tokens.ToList() };
            run.goals = ResolveFullTasGoals(run).Select(g => g.label).ToList();
            int at = _fullTasRuns.FindIndex(r => string.Equals(r.name, run.name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) _fullTasRuns[at] = run;
            else _fullTasRuns.Add(run);
            SaveFullTasRuns();
            return run;
        }

        // The run's fastest gated route; null if it has none.
        MacroDefinition FullTasBestRoute(string run)
        {
            string category = FullTasCategory(run);
            MacroDefinition best = null;
            float bestTime = float.PositiveInfinity;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (!g.Success || !string.Equals(g.Groups[1].Value, category, StringComparison.OrdinalIgnoreCase)) continue;
                float time = float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture);
                if (time < bestTime) { best = m; bestTime = time; }
            }
            return best;
        }

        // What the offline search keeps for a run (enginesim `fulltas`: <EngineSimDir>/fulltas/<run>.json), read again only
        // when the file changes: its inputs and when it reached each goal. A partial run (not every goal) never goes to the
        // gate, so this is the only place its progress shows.
        sealed class FullTasOffline
        {
            public DateTime written;
            public List<SolverTick> inputs;
            public int reached;
            public List<FullTasSplit> splits;
            public float time;
        }
        readonly Dictionary<string, FullTasOffline> _fullTasOffline = new Dictionary<string, FullTasOffline>(StringComparer.OrdinalIgnoreCase);

        FullTasOffline FullTasOfflineBest(string run)
        {
            string path = Path.Combine(EngineSimDir, "fulltas", run + ".json");
            if (!File.Exists(path)) { _fullTasOffline.Remove(run); return null; }
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (_fullTasOffline.TryGetValue(run, out FullTasOffline cached) && cached.written == written) return cached;
            try
            {
                JObject o = JObject.Parse(File.ReadAllText(path));
                var goals = o["goals"].ToObject<List<string>>();
                var ticks = o["goalPhysicsTicks"].ToObject<List<long>>();
                var offline = new FullTasOffline { written = written, inputs = o["inputs"].ToObject<List<SolverTick>>(), splits = new List<FullTasSplit>() };
                for (int i = 0; i < goals.Count && i < ticks.Count && ticks[i] >= 0; i++)
                    offline.splits.Add(new FullTasSplit { label = goals[i], ticks = ticks[i] });
                offline.reached = offline.splits.Count;
                offline.time = offline.reached == 0 ? 0f : offline.splits[offline.reached - 1].ticks * Time.fixedDeltaTime;
                _fullTasOffline[run] = offline;
                return offline;
            }
            catch (Exception ex)
            {
                // Written mid-read (the search replaces it atomically, so this is rare): the next poll reads it again.
                Logger.LogWarning("Full-TAS offline best " + path + " unreadable: " + ex.Message);
                return null;
            }
        }

        // What Play plays: the saved best, else the offline search's best so far (a partial run: it stops at the goal it
        // reaches last). Null: nothing yet.
        MacroDefinition FullTasPlayableRoute(FullTasRunDef run)
        {
            MacroDefinition best = FullTasBestRoute(run.name);
            if (best != null) return best;
            FullTasOffline offline = FullTasOfflineBest(run.name);
            return offline == null || offline.inputs.Count == 0 ? null
                : new MacroDefinition { name = "fulltas/" + run.name + " offline best (" + offline.reached + " of " + run.goals.Count + " goals)",
                    steps = ToSteps(offline.inputs) };
        }

        static List<FullTasSplit> FullTasSplits(FullTasTracker tracker)
        {
            var splits = new List<FullTasSplit>();
            for (int i = 0; i < tracker.Next && i < tracker.Goals.Length; i++)
                splits.Add(new FullTasSplit { label = tracker.Goals[i].label, ticks = tracker.PhysicsTicks[i] });
            return splits;
        }

        void StoreFullTasSplits(MacroDefinition route, List<FullTasSplit> splits)
        {
            if (route == null) return;
            _fullTasSplits[route.id] = splits;
            // Only routes that still exist keep theirs.
            foreach (string id in _fullTasSplits.Keys.Where(k => !_library.macros.Any(m => m.id == k)).ToList()) _fullTasSplits.Remove(id);
            WriteJsonAtomic(FullTasSplitsPath, _fullTasSplits, false);
        }

        void FullTasLog(string line)
        {
            _fullTasLog.Add(line);
            if (_fullTasLog.Count > 200) _fullTasLog.RemoveRange(0, _fullTasLog.Count - 200);
            Logger.LogInfo("Full-TAS: " + line);
        }

        void SetFullTasResult(string line)
        {
            _fullTasLastResult = line;
            FullTasLog(line);
        }

        // ---- Goal events (Harmony hooks, BeforeMovementFixedUpdate), exactly where enginesim's goal mode sees them.

        void FullTasEvent(bool reached)
        {
            if (reached && _fullTas.Done && _engine.IsRunning)
                _engine.CompleteFromGame(MacroEngine.FullTasFinishReason, _engine.PhysicsTicks * Time.fixedDeltaTime);
        }

        // AfterCourseStart: the course's start gate started its timer (it wasn't timing before).
        void FullTasCourseStarted(int course)
        {
            if (_fullTas != null && _engine.IsRunning) FullTasEvent(_fullTas.CourseStarted(course, _engine.RunTicks, _engine.PhysicsTicks));
        }

        // AfterCourseFinish: the player's own finish at the course's end gate (stopTracking with savePositionData, while timing).
        void FullTasCourseFinished(int course)
        {
            if (_fullTas != null && _engine.IsRunning) FullTasEvent(_fullTas.CourseFinished(course, _engine.RunTicks, _engine.PhysicsTicks));
        }

        // AfterBuyUpgrade: the game bought `box` (a buy goal's box, or a bonus's reward box). Wall jump, dash and air
        // jump come with their boxes.
        void FullTasBought(upgradeBox box)
        {
            if (_fullTas == null || !_engine.IsRunning) return;
            string path = HierarchyPath(box.transform);
            if (path == FullTasBoxes.First(b => b.token == "buywj").path) _fullTasWallJump = true;
            if (path == FullTasBoxes.First(b => b.token == "buydash").path) _fullTasDash = true;
            if (path == FullTasBoxes.First(b => b.token == "buyaj").path) _fullTasAirJump = true;
            FullTasEvent(_fullTas.Bought(path, FullTasBoxId(box), _engine.RunTicks, _engine.PhysicsTicks));
        }

        // A box's Full-TAS id (FullTasGoal.BoxId): its hierarchy path and sibling index. Paths aren't unique (course 2's
        // GLOBALcashPerLoop and bonus 1's reward box share one); enginesim computes the same from its world export.
        static string FullTasBoxId(upgradeBox box)
        {
            return FullTasGoal.BoxId(HierarchyPath(box.transform), box.transform.GetSiblingIndex());
        }

        // A bonus's reward box: the upgrade box nearest the centre of its end rect (courses.json endX/endY + CurrentOrigin()),
        // closer than FullTasGoal.RewardBoxRange; buying it is the bonus's unlock. Null while no upgrade box is loaded (no
        // world yet); enginesim applies the same rule to a trace whose reward goal names no box.
        string FullTasRewardBox(CourseDef bonus)
        {
            if (_fullTasRewardBoxes.TryGetValue(bonus.name, out string cached)) return cached;
            upgradeBox best = BonusRewardBox(bonus, out bool loaded);
            if (!loaded) return null;
            if (best == null) throw new InvalidOperationException("Full-TAS goal reward:" + bonus.name + ": no upgrade box near the bonus's end rect.");
            string id = FullTasBoxId(best);
            _fullTasRewardBoxes[bonus.name] = id;
            return id;
        }

        // Bonus name -> its reward box (BonusRewardBox), once the world had it.
        readonly Dictionary<string, upgradeBox> _bonusRewardBoxes = new Dictionary<string, upgradeBox>(StringComparer.OrdinalIgnoreCase);

        // The upgrade box nearest the centre of the bonus's end rect, closer than FullTasGoal.RewardBoxRange; null when
        // none is (`loaded`: false when no upgrade box is loaded at all).
        upgradeBox BonusRewardBox(CourseDef bonus, out bool loaded)
        {
            loaded = true;
            if (_bonusRewardBoxes.TryGetValue(bonus.name, out upgradeBox known) && known != null) return known;
            upgradeBox[] boxes = FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (boxes.Length == 0) { loaded = false; return null; }
            Vector2 centre = new Vector2(bonus.endX, bonus.endY) + CurrentOrigin();
            upgradeBox best = null;
            float bestDistance = FullTasGoal.RewardBoxRange * FullTasGoal.RewardBoxRange;
            foreach (upgradeBox box in boxes)
            {
                float d = ((Vector2)box.transform.position - centre).sqrMagnitude;
                if (d < bestDistance) { best = box; bestDistance = d; }
            }
            if (best != null) _bonusRewardBoxes[bonus.name] = best;
            return best;
        }

        // ---- Turbo

        // The fresh new game's player is at its start with the zones there settled; parks it there when it is elsewhere.
        // Needs a fresh game in the world.
        bool FullTasStartReady()
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot fresh = NewGameStart();
            if (((Vector2)player.transform.position - (Vector2)fresh.position).sqrMagnitude >= 64f * 64f)
            {
                TurboRunner.Restore(fresh, player);
                _fullTasParkedFrame = Time.frameCount;
                return false;
            }
            return Time.frameCount - _fullTasParkedFrame >= 10 && ZonesSettled();
        }

        // Not busy, and the fresh game is at its start: a turbo judge can run now (the offline-best queue's check).
        bool FullTasCanJudge()
        {
            return _fullTasPending == null && _fullTas == null && _newGamePhase == 0 && _bootstrapRequest == null && !PlaybackActive
                && !_recorder.IsRecording && FreshGameInWorld && FullTasStartReady();
        }

        // Back to the new game's start after a turbo run (economy included: nothing a run bought stays).
        void ParkAtNewGameStart()
        {
            Movement player = FindWorldMovement();
            if (FreshGame && player == _newGamePlayer) TurboRunner.Restore(_newGameStart, player);
        }

        // A turbo run of `inputs` from the fresh new game's first tick with the run's goals tracked; it finishes at the last
        // goal (time = physics ticks x fixedDeltaTime). Nothing writes an unlock: the game's purchases make them. With
        // _audit set every tick goes through the gate's checks (AuditTick, unlocks against FullTasCaps).
        bool EvaluateFullTas(FullTasRunDef run, List<SolverTick> inputs, out float time, out int finishTick, out FullTasTracker tracker)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot from = NewGameStart();
            tracker = new FullTasTracker(ResolveFullTasGoals(run));
            _fullTas = tracker;
            _fullTasWallJump = _fullTasDash = _fullTasAirJump = false;
            _silentEvaluation = true;
            _engine.Quiet = true;
            _silentFinished = false;
            _silentTime = 0f;
            int lastTick = -1, idleTicks = 0;
            try
            {
                TurboRunner.Restore(from, player);
                BeginDeterministicPhysics(player);
                _engine.Resume(from.engine, ToSteps(inputs), 0);
                // Dash and spring ticks take no input: room for them on top of the inputs.
                TurboRunner.Run(player, () => _engine.IsRunning && idleTicks < 20, inputs.Count + 300 * (tracker.Goals.Length + 1), () =>
                {
                    _engine.Clock++;
                    _engineSimTickHook?.Invoke(player);
                    if (!_engine.IsRunning) return;
                    if (_engine.InputsExhausted) idleTicks++;
                    int tick = (int)_engine.RunTicks;
                    if (_audit != null) AuditTick(player, tick);
                    lastTick = tick;
                }, from);
                if (_engine.IsRunning) _engine.Abort("Full-TAS run ended");
            }
            finally
            {
                _fullTas = null;
                _silentEvaluation = false;
                _engine.Quiet = false;
            }
            time = _silentTime;
            finishTick = Math.Min(inputs.Count - 1, Math.Max(0, lastTick));
            return _silentFinished && time > 0f;
        }

        // The gate's replay of a Full-TAS route (RouteViolation for _solverFullTas): from the new game's start, every tick
        // audited, the same time; a quick restart is never held on two ticks in a row (a real button is released in between).
        string FullTasViolation(List<SolverTick> inputs, float time)
        {
            var audit = _audit = new List<string>();
            for (int i = 1; i < inputs.Count; i++)
                if (inputs[i].reset && inputs[i - 1].reset)
                {
                    audit.Add("ticks " + (i - 1) + " and " + i + ": a quick restart on two ticks in a row");
                    break;
                }
            bool finished;
            float replayed;
            FullTasTracker tracker;
            try { finished = EvaluateFullTas(_solverFullTas, inputs, out replayed, out _, out tracker); }
            finally { _audit = null; }
            ParkAtNewGameStart();
            if (!finished || Mathf.Abs(replayed - time) > .0001f)
                audit.Insert(0, "a replay from the new game's start " + (finished ? "finished in " + replayed.ToString("0.00", CultureInfo.InvariantCulture)
                    : "reached " + tracker.Next + " of " + tracker.Goals.Length + " goals"));
            return audit.Count == 0 ? null : string.Join("; ", audit);
        }

        // Turbo replay of a Full-TAS route; saved through the gate (SaveSolverRoute) only when strictly faster than the run's
        // saved route. Needs the fresh new game at its start (FullTasStartReady).
        FullTasVerdict FullTasJudge(FullTasRunDef run, List<SolverTick> inputs)
        {
            bool finished = EvaluateFullTas(run, inputs, out float time, out int finishTick, out FullTasTracker tracker);
            long runTicks = _engine.PhysicsTicks;
            string status = _engine.Status;
            ParkAtNewGameStart();
            string category = FullTasCategory(run.name);
            float best = CategoryBest(category);
            var verdict = new FullTasVerdict { finished = finished, time = time, runTicks = runTicks, splits = FullTasSplits(tracker) };
            if (!finished)
            {
                FullTasGoal next = tracker.Current;
                verdict.reason = "reached " + tracker.Next + " of " + tracker.Goals.Length + " goals" + (next == null ? "" : ", not " + next.label) + " (" + status + ")";
            }
            else if (!(time < best - .0001f))
                verdict.reason = "not faster than " + category + " " + best.ToString("0.00", CultureInfo.InvariantCulture);
            else
            {
                List<SolverTick> route = inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList();
                string name = category + " " + time.ToString("0.00", CultureInfo.InvariantCulture);
                _solverFullTas = run;
                try { verdict.saved = SaveSolverRoute(route, time); }
                finally { _solverFullTas = null; }
                if (verdict.saved)
                {
                    StoreFullTasSplits(_library.macros.LastOrDefault(m => m.name == name), verdict.splits);
                    verdict.reason = "saved as " + name;
                }
                else verdict.reason = _solverLog[_solverLog.Count - 1];
            }
            SetFullTasResult("Full-TAS " + run.name + ": " + (finished ? time.ToString("0.00", CultureInfo.InvariantCulture) + "s in turbo" : "no finish in turbo")
                + ", " + verdict.reason);
            return verdict;
        }

        // An offline best (offline-best/fulltas-<run>-<time>.json, PumpOfflineBests): judged like a submit. Its log line.
        string JudgeFullTasFile(string path, string run)
        {
            List<SolverTick> inputs = JObject.Parse(File.ReadAllText(path))["ticks"].ToObject<List<SolverTick>>();
            FullTasVerdict v = FullTasJudge(FindFullTasRun(run), inputs);
            return (v.finished ? "turbo " + v.time.ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish in turbo") + ", " + v.reason;
        }

        // Runs `action` once the fresh new game is at its start: now if it is, else once a new game has loaded (one is loaded
        // when the session has none) and settled, answering the request then.
        object FullTasWhenReady(WebStudioRequest request, string what, Func<object> action)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_fullTasPending != null) throw new InvalidOperationException("Busy: " + _fullTasPending.what + ".");
            bool fresh = FreshGameInWorld;
            if (fresh && _newGamePhase == 0 && _bootstrapRequest == null && !(_solverRunning && _solveProcess == null) && FullTasStartReady())
                return action();
            var pending = new FullTasPending { request = request, what = what, action = action, since = Time.frameCount };
            if (!fresh && _newGamePhase == 0)
            {
                // The Optimize run's targets start from their own state: a new game under it would pull the world away.
                if (_solverRunning) throw new InvalidOperationException("A Full-TAS run needs a fresh new game: stop the Optimize run first.");
                LoadNewGame(null);
                pending.loaded = true;
            }
            _fullTasPending = pending;
            request.AsyncHandling = true;
            return null;
        }

        void PumpFullTasPending()
        {
            FullTasPending pending = _fullTasPending;
            if (pending == null) return;
            if (Time.frameCount - pending.since > 3000)
            {
                CompleteFullTasPending(500, new { ok = false, error = pending.what + ": the new game's start was not ready within 3000 frames." });
                return;
            }
            if (_newGamePhase != 0 || _bootstrapRequest != null || PlaybackActive || _recorder.IsRecording || _solverRunning && _solveProcess == null) return;
            if (!FreshGameInWorld)
            {
                if (pending.loaded || _solverRunning)
                {
                    CompleteFullTasPending(500, new { ok = false, error = pending.what + ": no fresh new game in the world." });
                    return;
                }
                LoadNewGame(null);
                pending.loaded = true;
                return;
            }
            if (!FullTasStartReady()) return;
            object result;
            try { result = pending.action(); }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS: " + pending.what + ": " + ex.Message);
                CompleteFullTasPending(500, new { error = ex.Message });
                return;
            }
            CompleteFullTasPending(200, result);
        }

        void CompleteFullTasPending(int status, object response)
        {
            WebStudioRequest request = _fullTasPending.request;
            _fullTasPending = null;
            // A solve whose trace capture failed never starts.
            if (status != 200 && _fullTasProcess == null && _fullTasSolveStatus.StartsWith("Capturing", StringComparison.Ordinal)) _fullTasSolveStatus = "Not started";
            request.StatusCode = status;
            request.Response = JsonConvert.SerializeObject(response);
            request.Completed.Set();
        }

        // ---- Trace

        // A trace of the run from the new game's start (what enginesim's fulltas mode loads): the usual trace header
        // (fixedDeltaTime, inputs, start with its economy), course 0, capabilities none and the run's goals, then the state
        // after every tick; with `world` the world export as this run saw it.
        object FullTasTrace(FullTasRunDef run, List<SolverTick> inputs, string name, bool world)
        {
            FullTasGoal[] goals = ResolveFullTasGoals(run);
            Directory.CreateDirectory(EngineSimDir);
            string path = Path.Combine(EngineSimDir, name + ".json");
            // Parked at the start with no frame in between.
            if (world) EngineSimExport.WriteWorld(Path.Combine(EngineSimDir, name + ".world.json"));
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot start = NewGameStart();
            courseScript[] courses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            Vector2 origin = CurrentOrigin();
            bool finished;
            float time;
            int finishTick;
            FullTasTracker tracker;
            using (var writer = new StreamWriter(path))
            {
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                w.WritePropertyName("course"); w.WriteValue(0);
                w.WritePropertyName("capabilities"); w.WriteValue("none");
                w.WritePropertyName("fulltas"); w.WriteStartObject();
                w.WritePropertyName("name"); w.WriteValue(run.name);
                w.WritePropertyName("seed"); w.WriteValue(FullTasSeed);
                w.WritePropertyName("goals"); w.WriteStartArray();
                foreach (FullTasGoal g in goals)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("label"); w.WriteValue(g.label);
                    w.WritePropertyName("kind"); w.WriteValue(g.kind);
                    w.WritePropertyName("course"); w.WriteValue(g.course);
                    if (g.boxes != null)
                    {
                        w.WritePropertyName("boxes"); w.WriteStartArray();
                        foreach (string box in g.boxes) w.WriteValue(box);
                        w.WriteEndArray();
                    }
                    if (g.kind == FullTasGoal.Reward)
                    {
                        // In this run's world coordinates, like a bonus trace's "finish".
                        w.WritePropertyName("rect"); w.WriteStartArray();
                        json.Float(g.x + origin.x); json.Float(g.y + origin.y); json.Float(g.w); json.Float(g.h);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WritePropertyName("fixedDeltaTime"); json.Float(Time.fixedDeltaTime);
                w.WritePropertyName("inputs"); w.WriteRawValue(JsonConvert.SerializeObject(inputs));
                w.WritePropertyName("start"); EngineSimExport.WriteSnapshot(json, start, player, false);
                w.WritePropertyName("ticks");
                w.WriteStartArray();
                _engineSimTickHook = p =>
                {
                    var snapshot = TurboRunner.Capture(p, courses, _engine.CaptureState(), (int)_engine.RunTicks, false, false);
                    EngineSimExport.WriteSnapshot(json, snapshot, p, true);
                };
                try { finished = EvaluateFullTas(run, inputs, out time, out finishTick, out tracker); }
                finally { _engineSimTickHook = null; }
                Vector2 end = player.transform.position;
                ParkAtNewGameStart();
                w.WriteEndArray();
                w.WritePropertyName("result");
                w.WriteStartObject();
                w.WritePropertyName("finished"); w.WriteValue(finished);
                w.WritePropertyName("time"); json.Float(time);
                w.WritePropertyName("finishTick"); w.WriteValue(finishTick);
                w.WritePropertyName("endX"); json.Float(end.x);
                w.WritePropertyName("endY"); json.Float(end.y);
                w.WritePropertyName("progress"); w.WriteValue(tracker.Next);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            RememberBestSplits(run, inputs, finished, time, tracker);
            FullTasLog("Trace " + path + ": " + (finished ? "finished in " + time.ToString("0.00", CultureInfo.InvariantCulture) + "s"
                : "reached " + tracker.Next + " of " + goals.Length + " goals"));
            return new { ok = true, path = path, trace = path, finished = finished, time = time, finishTick = finishTick, progress = tracker.Next };
        }

        // A run of the saved best (same inputs, same time) measured its splits: kept for the studio.
        void RememberBestSplits(FullTasRunDef run, List<SolverTick> inputs, bool finished, float time, FullTasTracker tracker)
        {
            MacroDefinition best = FullTasBestRoute(run.name);
            if (!finished || best == null || _fullTasSplits.ContainsKey(best.id) || Mathf.Abs(CategoryBest(FullTasCategory(run.name)) - time) > .0001f) return;
            List<SolverTick> saved = ToTicks(best.steps);
            if (saved.Count > inputs.Count || JsonConvert.SerializeObject(saved) != JsonConvert.SerializeObject(inputs.Take(saved.Count).ToList())) return;
            StoreFullTasSplits(best, FullTasSplits(tracker));
        }

        // The run's inputs a request names: `ticks`, or `routeId`'s, else the saved best's (none: no inputs).
        List<SolverTick> FullTasInputs(FullTasRunDef run, JObject body)
        {
            string routeId = Text(body, "routeId");
            if (!string.IsNullOrEmpty(routeId))
            {
                MacroDefinition route = _library.macros.Find(m => m.id == routeId);
                if (route == null || !route.steps.All(s => s.type == MacroStepType.RawInput))
                    throw new InvalidOperationException("Route " + routeId + " is missing or not raw input.");
                return ToTicks(route.steps);
            }
            if (body["ticks"] != null) return body["ticks"].ToObject<List<SolverTick>>();
            MacroDefinition best = FullTasBestRoute(run.name);
            if (best != null) return ToTicks(best.steps);
            // No gated route yet: the offline search's best so far (a partial run).
            FullTasOffline offline = FullTasOfflineBest(run.name);
            return offline != null && offline.inputs.Count > 0 ? offline.inputs : new List<SolverTick>();
        }

        // POST /api/enginesim/trace {fulltas: <run>, ticks | routeId, name, world}.
        object FullTasTraceRequest(WebStudioRequest request, JObject body)
        {
            FullTasRunDef run = FindFullTasRun(Text(body, "fulltas"));
            ResolveFullTasGoals(run);
            List<SolverTick> inputs = FullTasInputs(run, body);
            string name = string.IsNullOrEmpty(Text(body, "name")) ? "fulltas-" + run.name : Text(body, "name");
            bool world = Truthy(body, "world");
            return FullTasWhenReady(request, "Capturing trace " + name, () => FullTasTrace(run, inputs, name, world));
        }

        // ---- Playback

        // Studio playback in real time from a fresh new game's first tick, goals tracked.
        void StartFullTasPlayback(FullTasRunDef run, MacroDefinition route)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            if (_fullTasPending != null) throw new InvalidOperationException("Busy: " + _fullTasPending.what + ".");
            if (route == null) throw new InvalidOperationException("Full-TAS run " + run.name + " has no route yet.");
            var tracker = new FullTasTracker(ResolveFullTasGoals(run));
            StartNewGame(null);
            EnsureGameUnpaused();
            _fullTasPlay = new FullTasPlay { run = run, route = route, tracker = tracker };
        }

        // Movement.FixedUpdate prefix of the fresh game's first tick (CaptureNewGameStart took it): no unlock written, no wait.
        void BeginFullTasPlayback(Movement movement)
        {
            FullTasPlay play = _fullTasPlay;
            play.started = true;
            // The fixed seed again before the first tick (CaptureNewGameStart already seeded the start snapshot this
            // tick's prefix captured): the real-time run starts at the same RNG state the trace records, then diverges
            // as every rendered frame draws Random (docs/fulltas.md).
            UnityEngine.Random.InitState(FullTasSeed);
            BeginDeterministicPhysics(movement);
            _brakeTicks = -1;
            play.tracker.Reset();
            _fullTas = play.tracker;
            _fullTasWallJump = _fullTasDash = _fullTasAirJump = false;
            _engine.Run(play.route, _library, false, true, 0, null);
            if (!_engine.IsRunning) { FinishFullTasPlayback(false, _engine.Status); return; }
            // Its first tick's quick restart, where every later one is done (BeforeMovementFixedUpdate).
            if (_engine.TakeQuickRestart()) QuickRestart(movement);
        }

        // The end of a Full-TAS playback (finished, failed or stopped): one result.
        void FinishFullTasPlayback(bool success, string reason)
        {
            FullTasPlay play = _fullTasPlay;
            _fullTasPlay = null;
            _fullTas = null;
            FullTasTracker tracker = play.tracker;
            bool finished = success && reason == MacroEngine.FullTasFinishReason;
            float time = finished ? _engine.AuthoritativeCompletionTime : _engine.PhysicsTicks * Time.fixedDeltaTime;
            List<FullTasSplit> splits = FullTasSplits(tracker);
            string splitText = string.Join(" + ", splits.Select(s => s.label + " " + (s.ticks * Time.fixedDeltaTime).ToString("0.00", CultureInfo.InvariantCulture)));
            FullTasGoal next = tracker.Current;
            SetFullTasResult("Full-TAS " + play.run.name + (finished ? " finished in " + time.ToString("0.00", CultureInfo.InvariantCulture) + "s"
                : " stopped after " + tracker.Next + " of " + tracker.Goals.Length + " goals" + (next == null ? "" : " (next " + next.label + ")") + ": " + reason)
                + DroppedFramesText() + (splitText.Length > 0 ? " [" + splitText + "]" : ""));
            if (finished && FullTasBestRoute(play.run.name) == play.route && !_fullTasSplits.ContainsKey(play.route.id)
                && Mathf.Abs(CategoryBest(FullTasCategory(play.run.name)) - time) <= .0001f)
                StoreFullTasSplits(play.route, splits);
            var result = new AttemptResult {
                routeId = play.route.id,
                routeName = play.route.name,
                success = finished,
                completionTicks = Mathf.RoundToInt(time / Time.fixedDeltaTime),
                completionTime = time,
                timingSource = "Physics ticks from the new game's start to the last goal: " + splitText,
                deterministicPhysics = _deterministicBody != null,
                failureAction = finished ? -1 : tracker.Next,
                failureReason = finished ? null : reason };
            _results.Insert(0, result);
            WriteJsonAtomic(Path.Combine(_resultsPath, result.timestampUtc.Replace(":", "-") + "-" + result.id + ".json"), result, false);
            EndDeterministicPhysics();
        }

        // StopPlayback / FailNewGame: a playback still waiting for its new game never starts.
        void DropPendingFullTasPlayback(string reason)
        {
            if (_fullTasPlay == null || _fullTasPlay.started) return;
            SetFullTasResult("Full-TAS " + _fullTasPlay.run.name + " did not start: " + reason);
            _fullTasPlay = null;
        }

        // ---- Offline search

        void LaunchFullTasSolve(FullTasRunDef run, string trace, JObject body)
        {
            var args = new StringBuilder("fulltas ").Append(Quote(trace));
            int threads = Number(body, "threads", 0);
            if (threads <= 0) threads = Math.Max(1, Environment.ProcessorCount - 2);
            args.Append(" --threads ").Append(threads);
            float minutes = FloatNumber(body, "minutes", 0f), windowMinutes = FloatNumber(body, "windowMinutes", 0f), stallMinutes = FloatNumber(body, "stallMinutes", 0f);
            int window = Number(body, "window", 0);
            if (minutes > 0f) args.Append(" --minutes ").Append(minutes.ToString(CultureInfo.InvariantCulture));
            if (windowMinutes > 0f) args.Append(" --window-minutes ").Append(windowMinutes.ToString(CultureInfo.InvariantCulture));
            if (stallMinutes > 0f) args.Append(" --stall-minutes ").Append(stallMinutes.ToString(CultureInfo.InvariantCulture));
            if (window > 0) args.Append(" --window ").Append(window);
            if (Truthy(body, "fresh")) args.Append(" --fresh");
            if (Truthy(body, "noSubmit")) args.Append(" --no-submit");
            int seeds = WriteFullTasSeeds(run, args);
            while (_fullTasOutput.TryDequeue(out _)) { }
            var process = new Process { StartInfo = new ProcessStartInfo(EngineSimExe, args.ToString()) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(EngineSimExe) } };
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) _fullTasOutput.Enqueue(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) _fullTasOutput.Enqueue(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _fullTasProcess = process;
            _fullTasSolveRun = run.name;
            _fullTasSolveStatus = "Solving " + run.name + " offline on " + threads + " threads from " + seeds + " seed routes";
            FullTasLog(EngineSimExe + " " + args);
        }

        static readonly Regex FullTasSeedRoute = new Regex(@"^([A-Za-z0-9]+)_");
        static readonly Regex FullTasSeedTransition = new Regex(@"^(bonus\d+|\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);

        // A solve's window seeds (fulltas-<run>-seed-<k>.json in the enginesim dir, the old ones deleted first), passed as
        // --seed: every saved raw-input category route ("2_WJ 15.02", "1to2_NO_WJ 3.40") of the courses, bonuses and
        // transitions the run's goals name, once per distinct inputs, faster ones first. Each says which of the run's goals
        // it reaches (a course: start:N, finish:N; a bonus: its reward; a transition: where it ends), its category, and
        // whether it starts standing at its course's quick-restart point (course and bonus routes do; `align` is that
        // point). Returns how many.
        int WriteFullTasSeeds(FullTasRunDef run, StringBuilder args)
        {
            Directory.CreateDirectory(EngineSimDir);
            string prefix = "fulltas-" + run.name + "-seed-";
            var numbered = new Regex("^" + Regex.Escape(prefix) + @"\d+\.json$", RegexOptions.IgnoreCase);
            foreach (string old in Directory.GetFiles(EngineSimDir, prefix + "*.json"))
                if (numbered.IsMatch(Path.GetFileName(old))) File.Delete(old);
            var labels = new HashSet<string>(ResolveFullTasGoals(run).Select(g => g.label));
            var found = new List<(bool stale, float time, string key, string json)>();
            foreach (MacroDefinition route in _library.macros)
            {
                Match m = route.name == null ? Match.Empty : FullTasSeedRoute.Match(route.name);
                if (!m.Success || !route.steps.All(s => s.type == MacroStepType.RawInput)) continue;
                string token = m.Groups[1].Value.ToLowerInvariant();
                CourseDef target = null;
                int course = 0;
                var goals = new List<string>();
                Match transition = FullTasSeedTransition.Match(token);
                if (transition.Success)
                {
                    string to = transition.Groups[2].Value.ToLowerInvariant();
                    if (to.StartsWith("bonus", StringComparison.Ordinal)) goals.Add("reward:" + to);
                    else { goals.Add("start:" + to); course = int.Parse(to, CultureInfo.InvariantCulture); }
                }
                else
                {
                    target = _courses.Find(c => !c.IsTransition && string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase));
                    if (target == null) continue;
                    if (target.IsBonus) goals.Add("reward:" + token);
                    else { goals.Add("start:" + target.id); goals.Add("finish:" + target.id); course = target.id; }
                }
                goals.RemoveAll(g => !labels.Contains(g));
                if (goals.Count == 0) continue;
                bool stale = route.name.EndsWith(" stale", StringComparison.Ordinal);
                Match category = CategoryRoute.Match(stale ? route.name.Substring(0, route.name.Length - " stale".Length) : route.name);
                string caps = string.IsNullOrEmpty(target?.minCapabilities) ? "none" : target.minCapabilities;
                float time = float.MaxValue;
                if (category.Success)
                {
                    caps = CapabilityLabels.OrderByDescending(p => p.Value.Length)
                        .First(p => category.Groups[1].Value.EndsWith("_" + p.Value, StringComparison.OrdinalIgnoreCase)).Key;
                    if (!float.TryParse(category.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out time)) time = float.MaxValue;
                }
                // A category route of a course or bonus starts standing at its quick-restart point; a transition's doesn't.
                bool standing = target != null, restart = false;
                List<SolverTick> ticks = ToTicks(route.steps);
                object align = null;
                if (standing && target != null)
                {
                    Vector2 at = new Vector2(target.x, target.y - 12f) + CurrentOrigin();
                    align = new { at.x, at.y };
                }
                string json = JsonConvert.SerializeObject(new { name = route.name, token, course, goals, capabilities = caps, standing, restart, align, ticks });
                found.Add((stale, time, token + "|" + caps + "|" + standing + "|" + restart + "|" + JsonConvert.SerializeObject(ticks), json));
            }
            var written = new HashSet<string>();
            int count = 0;
            foreach (var seed in found.OrderBy(s => s.stale).ThenBy(s => s.time))
            {
                if (!written.Add(seed.key)) continue;
                string file = Path.Combine(EngineSimDir, prefix + count++ + ".json");
                File.WriteAllText(file, seed.json);
                args.Append(" --seed ").Append(Quote(file));
            }
            return count;
        }

        // POST /api/fulltas/solve {name, minutes, windowMinutes, window}: the trace first when it is missing.
        object StartFullTasSolve(WebStudioRequest request, JObject body)
        {
            if (_fullTasProcess != null) throw new InvalidOperationException("A Full-TAS solve is already running (" + _fullTasSolveRun + ").");
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new InvalidOperationException("The offline solver is not built (" + exe + "): dotnet build enginesim\\Cli\\IGTAP.EngineSim.csproj -c Release");
            FullTasRunDef run = FindFullTasRun(Text(body, "name"));
            ResolveFullTasGoals(run);
            string trace = FullTasTracePath(run.name);
            var options = (JObject)body.DeepClone();
            Func<object> launch = () =>
            {
                if (_fullTasProcess != null) throw new InvalidOperationException("A Full-TAS solve is already running (" + _fullTasSolveRun + ").");
                bool captured = false;
                try
                {
                    if (!File.Exists(trace))
                    {
                        FullTasTrace(run, FullTasInputs(run, new JObject()), "fulltas-" + run.name, true);
                        captured = true;
                    }
                    LaunchFullTasSolve(run, trace, options);
                }
                catch (Exception ex)
                {
                    _fullTasSolveStatus = "Not started: " + ex.Message;
                    throw;
                }
                return new { ok = true, trace = trace, captured = captured, status = _fullTasSolveStatus };
            };
            if (File.Exists(trace)) return launch();
            _fullTasSolveRun = run.name;
            _fullTasSolveStatus = "Capturing the trace of " + run.name;
            // The solve starts once the new game's start is ready (now, or in PumpFullTasPending).
            try { return FullTasWhenReady(request, "Capturing the trace of " + run.name, launch); }
            catch (Exception ex)
            {
                _fullTasSolveStatus = "Not started: " + ex.Message;
                throw;
            }
        }

        void KillFullTasSolve(string status)
        {
            if (_fullTasProcess == null) return;
            try { if (!_fullTasProcess.HasExited) _fullTasProcess.Kill(); }
            catch (Exception ex) { Logger.LogWarning("Could not stop the Full-TAS solve: " + ex.Message); }
            _fullTasProcess.Dispose();
            _fullTasProcess = null;
            while (_fullTasOutput.TryDequeue(out string line)) FullTasSolveLine(line);
            _fullTasSolveStatus = status;
            FullTasLog(status);
        }

        void FullTasSolveLine(string line)
        {
            _fullTasLog.Add(line);
            if (_fullTasLog.Count > 200) _fullTasLog.RemoveRange(0, _fullTasLog.Count - 200);
            Logger.LogInfo("fulltas: " + line);
        }

        void PumpFullTasSolve()
        {
            if (_fullTasProcess == null) return;
            try
            {
                while (_fullTasOutput.TryDequeue(out string line)) FullTasSolveLine(line);
                if (!_fullTasProcess.HasExited) return;
                // Exited: the parameterless wait also waits for the redirected output to end.
                _fullTasProcess.WaitForExit();
                int code = _fullTasProcess.ExitCode;
                KillFullTasSolve(code == 0 ? "Finished" : "The offline solver exited with code " + code);
            }
            catch (Exception ex) { Logger.LogError(ex); KillFullTasSolve("Solve error: " + ex.Message); }
        }

        // The offline-best queue (PumpOfflineBests) leaves the running solve's own bests to it: it submits them itself.
        string FullTasSolvingGroup { get { return _fullTasProcess == null ? null : "fulltas-" + _fullTasSolveRun; } }

        // Update: fulltas.json edits (within a second, never mid-run), requests waiting for the new game, the solve's output.
        void PumpFullTas()
        {
            if (Time.unscaledTime >= _fullTasCheckAt && _fullTas == null && _fullTasPending == null && !_silentEvaluation)
            {
                _fullTasCheckAt = Time.unscaledTime + 1f;
                if (File.Exists(FullTasPath) && File.GetLastWriteTimeUtc(FullTasPath) != _fullTasWritten) LoadFullTas();
            }
            PumpFullTasPending();
            PumpFullTasSolve();
        }

        // ---- HTTP

        object HandleFullTas(WebStudioRequest request, JObject body)
        {
            switch (request.Path)
            {
                case "/api/fulltas/goals":
                {
                    // {name, goals: ["start:1", ...]}: creates or replaces the run's goals.
                    if (!(body["goals"] is JArray goals)) throw new InvalidOperationException("goals: an array of goal tokens.");
                    FullTasRunDef run = SetFullTasGoals(Text(body, "name"), goals.Select(g => g.Type == JTokenType.Null ? "" : g.ToString()));
                    return new { ok = true, name = run.name, goals = run.goals };
                }
                case "/api/fulltas/capture":
                {
                    // {name}: the start trace + world, seeded with the saved best's inputs.
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    ResolveFullTasGoals(run);
                    List<SolverTick> inputs = FullTasInputs(run, new JObject());
                    return FullTasWhenReady(request, "Capturing the trace of " + run.name, () => FullTasTrace(run, inputs, "fulltas-" + run.name, true));
                }
                case "/api/fulltas/play":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    StartFullTasPlayback(run, FullTasPlayableRoute(run));
                    return new { ok = true };
                }
                case "/api/fulltas/solve":
                    return StartFullTasSolve(request, body);
                case "/api/fulltas/stop":
                {
                    KillFullTasSolve("Stopped");
                    if (_fullTasPending != null)
                    {
                        if (_fullTasProcess == null && _fullTasSolveStatus.StartsWith("Capturing", StringComparison.Ordinal)) _fullTasSolveStatus = "Stopped";
                        CompleteFullTasPending(409, new { ok = false, error = _fullTasPending.what + ": stopped." });
                    }
                    if (_fullTasPlay != null) StopPlayback("Stopped from Web Studio");
                    return new { ok = true };
                }
                case "/api/fulltas/submit":
                {
                    // {name, ticks}: judged through the gate; {saved, finished, time, reason, splits}.
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    ResolveFullTasGoals(run);
                    if (!(body["ticks"] is JArray)) throw new InvalidOperationException("ticks: the run's inputs.");
                    List<SolverTick> inputs = body["ticks"].ToObject<List<SolverTick>>();
                    return FullTasWhenReady(request, "Judging a " + run.name + " route", () => FullTasJudge(run, inputs));
                }
            }
            request.StatusCode = 404;
            return new { ok = false, error = "Unknown Full-TAS endpoint." };
        }

        object BuildFullTasWebState()
        {
            var runs = _fullTasRuns.Select(r =>
            {
                string error = null;
                string[] labels;
                try { labels = ResolveFullTasGoals(r).Select(goal => goal.label).ToArray(); }
                catch (Exception ex) { error = ex.Message; labels = (r.goals ?? new List<string>()).ToArray(); }
                MacroDefinition best = FullTasBestRoute(r.name);
                Match g = best == null ? null : CategoryRoute.Match(best.name);
                List<FullTasSplit> splits;
                if (best == null || !_fullTasSplits.TryGetValue(best.id, out splits)) splits = new List<FullTasSplit>();
                FullTasOffline offline = FullTasOfflineBest(r.name);
                return new { name = r.name, goals = labels, tokens = labels, error = error,
                    // The first goal that trips the breaker (FullTasBreakerTokens); null when none does.
                    breaker = FullTasBreakerGoal(labels),
                    best = best == null ? null : new { routeId = best.id, name = best.name, time = float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture), splits = splits },
                    // The offline search's best so far (partial runs included): goals reached, when, and the time to the last one.
                    offline = offline == null ? null : new { reached = offline.reached, time = offline.time, splits = offline.splits },
                    playable = best != null || offline != null && offline.inputs.Count > 0,
                    traceReady = File.Exists(FullTasTracePath(r.name)) };
            }).ToArray();
            FullTasPlay play = _fullTasPlay;
            return new {
                runs = runs,
                solve = new { running = _fullTasProcess != null, name = _fullTasSolveRun, status = _fullTasSolveStatus,
                    log = _fullTasLog.AsEnumerable().Reverse().Take(30).ToArray() },
                lastResult = _fullTasLastResult,
                pending = _fullTasPending?.what,
                playing = play == null ? null : new { name = play.run.name, started = play.started, reached = play.tracker.Next, of = play.tracker.Goals.Length,
                    next = play.tracker.Current?.label, droppedFrames = _droppedFrames } };
        }

        // The index of the first goal ("buy:a+b" labels) buying a FullTasBreakerTokens box; null when none does.
        static int? FullTasBreakerGoal(string[] labels)
        {
            for (int i = 0; i < labels.Length; i++)
                if (labels[i].StartsWith("buy:", StringComparison.Ordinal) && labels[i].Substring(4).Split('+').Any(t => FullTasBreakerTokens.Contains(t.Trim())))
                    return i;
            return null;
        }
    }
}
