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
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        public sealed class FullTasRunDef
        {
            public string name;
            // "vman": the run starts in a Vman speedrun's world (LoadNewGame's Vman start); unset: a fresh new game.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string start;
            public List<string> goals = new List<string>();
            [JsonIgnore] public bool Vman { get { return start == VmanStart; } }
        }

        const string VmanStart = "vman";

        const int FullTasSeed = 20260925;

        public sealed class FullTasSplit
        {
            public string label;
            public long ticks;
        }

        sealed class FullTasVerdict
        {
            public bool ok = true, finished, saved;
            public float time;
            public long runTicks;
            public string reason;
            public List<FullTasSplit> splits;
        }

        sealed class FullTasPlay
        {
            public FullTasRunDef run;
            public MacroDefinition route;
            public FullTasTracker tracker;
            public int expected;
            public bool waitingForNewGame = true, started;
        }

        sealed class FullTasPending
        {
            public WebStudioRequest request;
            public FullTasRunDef run;
            public string what;
            public Func<object> action;
            public int since;
            public bool loaded;
        }

        List<FullTasRunDef> _fullTasRuns = new List<FullTasRunDef>();
        DateTime _fullTasWritten;
        float _fullTasCheckAt;
        Dictionary<string, List<FullTasSplit>> _fullTasSplits = new Dictionary<string, List<FullTasSplit>>();

        FullTasTracker _fullTas;
        FullTasUnlocks _fullTasUnlocks;
        bool _fullTasStartWasTracking;
        int _fullTasFinishCourse = -1;
        FullTasRunDef _solverFullTas;
        FullTasPlay _fullTasPlay;
        FullTasPending _fullTasPending;
        int _fullTasParkedFrame = -1000;
        object _fullTasLastResult;
        readonly Dictionary<string, string> _fullTasRewardBoxes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Process _fullTasProcess;
        readonly ConcurrentQueue<string> _fullTasOutput = new ConcurrentQueue<string>();
        string _fullTasSolveRun;
        string _fullTasSolveStatus = "Idle";
        readonly List<string> _fullTasLog = new List<string>();
        // The running solve's "@fulltas {...}" events (FullTas.Emit), stamped with "at" (Unix ms); "status" events only keep the latest.
        readonly List<JObject> _fullTasEvents = new List<JObject>();
        JObject _fullTasProgress;
        string _fullTasSolveError, _fullTasCommand;
        const int MaxFullTasEvents = 400, MaxFullTasLog = 300;

        static readonly Regex FullTasToken = new Regex(@"^(start|finish|buy|reward|at):(.+)$");
        static readonly Regex FullTasRouteName = new Regex(@"^fulltas/([A-Za-z0-9%]+)_NONE ", RegexOptions.IgnoreCase);
        static readonly Regex OfflineFullTasBestFile = new Regex(@"^fulltas-([A-Za-z0-9%]+)-([\d.]+)\.json$");
        static readonly (string token, string path, int sibling)[] FullTasBoxes = {
            ("buyreward", "Courses/course 1/localUpgrades/CashPerLoop", 0),
            ("buyclone", "Courses/course 1/localUpgrades/Clone", 1),
            ("buywj", "Courses/course 1/localUpgrades/Wall jump", 2),
            ("buyomni", "Courses/course 1/localUpgrades/FreeOmnidash", 5),
            ("buyreward2", "Courses/course 2/localUpgrades/CashPerLoop", 0),
            ("buyclone2", "Courses/course 2/localUpgrades/Clone", 1),
            ("buyclonemult2", "Courses/course 2/localUpgrades/CloneMult", 2),
            ("buyglobalreward2", "Courses/course 2/localUpgrades/GLOBALcashPerLoop", 3),
            ("buyds", "Courses/course 2/localUpgrades/DashUnlock", 4),
            ("buyreward3", "Courses/course 3/localUpgrades/CashPerLoop", 0),
            ("buyclone3", "Courses/course 3/localUpgrades/CloneCount", 1),
            ("buyclonemult3", "Courses/course 3/localUpgrades/CloneMult", 2),
            ("buyfast3", "Courses/course 3/localUpgrades/FastCloneChance", 3),
            ("buyglobalreward3", "Courses/course 3/localUpgrades/GLOBALcash", 4),
            ("buydj", "Courses/course 3/localUpgrades/Double jump", 5),
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
        static readonly HashSet<string> FullTasBreakerTokens = new HashSet<string> { "buyprestige5" };
        const int MaxBuyGoalBoxes = 8;

        string FullTasPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas.json"); } }
        string FullTasSplitsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-splits.json"); } }
        string FullTasTracePath(string run) { return Path.Combine(EngineSimDir, "fulltas-" + run + ".json"); }
        static string FullTasCategory(string run) { return "fulltas/" + run + "_" + CapabilityLabels["none"]; }

        // The unlocks a Full-TAS run has bought so far, as the game's boxes grant them (upgradeBox.DoBuyUpgrade).
        struct FullTasUnlocks
        {
            public bool wallJump, dash, doubleJump, blockSwap, omniDash;
            public int airDashes, airJumps;

            public void Bought(string token)
            {
                switch (token)
                {
                    case "buywj": wallJump = true; break;
                    case "buyds": dash = true; airDashes++; break;
                    case "buydj": doubleJump = true; airJumps++; break;
                    case "buyblockswap5": blockSwap = true; break;
                    case "buyomni": dash = true; omniDash = true; airDashes++; break;
                }
            }

            public override string ToString()
            {
                var parts = new List<string>();
                if (wallJump) parts.Add("wall jump");
                if (dash) parts.Add(airDashes + " dash" + (airDashes == 1 ? "" : "es") + (omniDash ? " (omnidirectional)" : ""));
                if (doubleJump) parts.Add(airJumps + " double jump" + (airJumps == 1 ? "" : "s"));
                if (blockSwap) parts.Add("block swap");
                return parts.Count == 0 ? "none" : string.Join(", ", parts);
            }
        }


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
                    List<FullTasRunDef> previous = _fullTasRuns;
                    _fullTasRuns = JsonConvert.DeserializeObject<List<FullTasRunDef>>(text) ?? new List<FullTasRunDef>();
                    foreach (FullTasRunDef run in _fullTasRuns)
                        RetireOutdatedFullTasRoutes(run, previous != null
                            && FullTasRunChanged(previous.Find(r => string.Equals(r.name, run.name, StringComparison.OrdinalIgnoreCase)), run));
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

        // The run's goal labels, or its raw tokens while one doesn't resolve.
        string[] FullTasLabels(FullTasRunDef run)
        {
            try { return ResolveFullTasGoals(run).Select(g => g.label).ToArray(); }
            catch (InvalidOperationException) { return (run.goals ?? new List<string>()).ToArray(); }
        }

        bool FullTasRunChanged(FullTasRunDef old, FullTasRunDef run)
        {
            return old == null || old.start != run.start || !FullTasLabels(old).SequenceEqual(FullTasLabels(run));
        }

        // A gate route replays the goals it was judged against: once the run's goals or start change, its inputs chase the
        // old goals and its time is no record of the new ones, so it's renamed "<name> stale" (out of FullTasBestRoute and
        // CategoryBest). Its splits name its goals; a route without splits is retired only when a change is seen.
        void RetireOutdatedFullTasRoutes(FullTasRunDef run, bool changed)
        {
            string[] labels;
            try { labels = ResolveFullTasGoals(run).Select(g => g.label).ToArray(); }
            catch (InvalidOperationException) { return; }
            string category = FullTasCategory(run.name);
            bool retired = false;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = CategoryRoute.Match(m.name ?? "");
                if (!g.Success || !string.Equals(g.Groups[1].Value, category, StringComparison.OrdinalIgnoreCase)) continue;
                if (_fullTasSplits.TryGetValue(m.id, out List<FullTasSplit> splits) ? splits.Select(s => s.label).SequenceEqual(labels) : !changed) continue;
                FullTasLog("Retired " + m.name + ": solved for other goals than " + run.name + "'s");
                m.name += " stale";
                retired = true;
            }
            if (retired) SaveLibrary();
        }

        FullTasRunDef FindFullTasRun(string name)
        {
            return _fullTasRuns.Find(r => string.Equals(r.name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("No Full-TAS run named \"" + name + "\" in fulltas.json.");
        }

        FullTasGoal[] ResolveFullTasGoals(FullTasRunDef run)
        {
            if (!RunName.IsMatch(run.name ?? "")) throw new InvalidOperationException("Full-TAS run names are letters, digits and \"%\" only (\"" + run.name + "\").");
            if (run.goals == null || run.goals.Count == 0) throw new InvalidOperationException("Full-TAS run " + run.name + " has no goals.");
            if (run.start != null && run.start != VmanStart)
                throw new InvalidOperationException("Full-TAS run " + run.name + ": start is \"" + VmanStart + "\" (a Vman speedrun's world) or unset (a new game), not \"" + run.start + "\".");
            return run.goals.Select(ResolveFullTasGoal).ToArray();
        }

        FullTasGoal ResolveFullTasGoal(string token)
        {
            string text = (token ?? "").Trim().ToLowerInvariant();
            Match m = FullTasToken.Match(text);
            if (!m.Success) throw new InvalidOperationException("Unknown Full-TAS goal \"" + token + "\": start:<course>, finish:<course>, buy:<box>[+<box>...], reward:bonus<N> or at:<x>,<y>,<width>,<height>[,from=<side>[+<side>...]].");
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
            else if (kind == FullTasGoal.At)
            {
                List<string> parts = arg.Split(',').Select(p => p.Trim()).ToList();
                string from = parts.Count == 5 && parts[4].StartsWith("from=", StringComparison.Ordinal) ? parts[4].Substring("from=".Length) : null;
                if (from != null) parts.RemoveAt(4);
                float[] rect = parts.Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN).ToArray();
                if (rect.Length != 4 || rect.Any(float.IsNaN) || !(rect[2] > 0f) || !(rect[3] > 0f))
                    throw new InvalidOperationException("Full-TAS goal \"" + token + "\": at:<x>,<y>,<width>,<height>[,from=<side>[+<side>...]], the checkpoint rect's centre and size in courses.json's frame; sides "
                        + string.Join(", ", FullTasGoal.SideNames) + ".");
                goal.x = rect[0]; goal.y = rect[1]; goal.w = rect[2]; goal.h = rect[3];
                goal.label = "at:" + string.Join(",", rect.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));
                if (from != null)
                {
                    foreach (string side in from.Split('+').Select(s => s.Trim()))
                    {
                        int bit = Array.IndexOf(FullTasGoal.SideNames, side);
                        if (bit < 0) throw new InvalidOperationException("Full-TAS goal \"" + token + "\": from=" + side + " is not a side (" + string.Join(", ", FullTasGoal.SideNames) + ", joined with \"+\").");
                        goal.from |= 1 << bit;
                    }
                    goal.label += ",from=" + string.Join("+", FullTasGoal.SideNames.Where((s, i) => (goal.from & 1 << i) != 0));
                }
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

        // start: null keeps the run's own, "" a new game, "vman" a Vman speedrun's world.
        FullTasRunDef SetFullTasGoals(string name, IEnumerable<string> tokens, string start)
        {
            string trimmed = (name ?? "").Trim();
            FullTasRunDef old = _fullTasRuns.Find(r => string.Equals(r.name, trimmed, StringComparison.OrdinalIgnoreCase));
            var run = new FullTasRunDef { name = trimmed, start = start == null ? old?.start : start.Length == 0 ? null : start, goals = tokens.ToList() };
            run.goals = ResolveFullTasGoals(run).Select(g => g.label).ToList();
            int at = _fullTasRuns.FindIndex(r => string.Equals(r.name, run.name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) _fullTasRuns[at] = run;
            else _fullTasRuns.Add(run);
            SaveFullTasRuns();
            RetireOutdatedFullTasRoutes(run, FullTasRunChanged(old, run));
            return run;
        }

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

        // The solver's best as written (enginesim/fulltas/<run>.json), with the goals it was solved against.
        sealed class FullTasOfflineFile
        {
            public DateTime written;
            public List<string> goals;
            public List<long> ticks, inputTicks;
            public List<SolverTick> inputs;
        }
        sealed class FullTasOffline
        {
            public List<SolverTick> inputs;
            public int reached;
            public List<FullTasSplit> splits;
            public float time;
        }
        readonly Dictionary<string, FullTasOfflineFile> _fullTasOffline = new Dictionary<string, FullTasOfflineFile>(StringComparer.OrdinalIgnoreCase);

        FullTasOfflineFile ReadFullTasOffline(string run)
        {
            string path = Path.Combine(EngineSimDir, "fulltas", run + ".json");
            if (!File.Exists(path)) { _fullTasOffline.Remove(run); return null; }
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (_fullTasOffline.TryGetValue(run, out FullTasOfflineFile cached) && cached.written == written) return cached;
            try
            {
                JObject o = JObject.Parse(File.ReadAllText(path));
                var file = new FullTasOfflineFile { written = written, goals = o["goals"].ToObject<List<string>>(), ticks = o["goalPhysicsTicks"].ToObject<List<long>>(),
                    inputTicks = o["goalInputTicks"].ToObject<List<long>>(), inputs = o["inputs"].ToObject<List<SolverTick>>() };
                _fullTasOffline[run] = file;
                return file;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS offline best " + path + " unreadable: " + ex.Message);
                return null;
            }
        }

        // The offline best measured against the run's current goals: its splits hold only while its goals match them, so
        // after a goals edit it reaches just the unchanged leading goals (a goal's time depends only on the goals before it).
        // Its inputs end at the last of them: past it they head for a changed goal, or are a solver's miss at the next one.
        FullTasOffline FullTasOfflineBest(string run, IList<string> labels)
        {
            FullTasOfflineFile file = ReadFullTasOffline(run);
            if (file == null) return null;
            var offline = new FullTasOffline { splits = new List<FullTasSplit>() };
            for (int i = 0; i < labels.Count && i < file.goals.Count && i < file.ticks.Count && file.ticks[i] >= 0 && file.goals[i] == labels[i]; i++)
                offline.splits.Add(new FullTasSplit { label = labels[i], ticks = file.ticks[i] });
            offline.reached = offline.splits.Count;
            offline.time = offline.reached == 0 ? 0f : offline.splits[offline.reached - 1].ticks * Time.fixedDeltaTime;
            int keep = offline.reached == 0 ? 0 : (int)Math.Min(file.inputTicks[offline.reached - 1], file.inputs.Count);
            offline.inputs = file.inputs.GetRange(0, keep);
            return offline;
        }

        MacroDefinition FullTasPlayableRoute(FullTasRunDef run)
        {
            MacroDefinition best = FullTasBestRoute(run.name);
            if (best != null) return best;
            FullTasOffline offline = FullTasOfflineBest(run.name, ResolveFullTasGoals(run).Select(g => g.label).ToList());
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
            foreach (string id in _fullTasSplits.Keys.Where(k => !_library.macros.Any(m => m.id == k)).ToList()) _fullTasSplits.Remove(id);
            WriteJsonAtomic(FullTasSplitsPath, _fullTasSplits, false);
        }

        void FullTasLog(string line)
        {
            _fullTasLog.Add(line);
            if (_fullTasLog.Count > MaxFullTasLog) _fullTasLog.RemoveRange(0, _fullTasLog.Count - MaxFullTasLog);
            Logger.LogInfo("Full-TAS: " + line);
        }

        // detail: what the Web Studio shows (kind "play" or "judge"); a plain line shows as a note.
        void SetFullTasResult(string line, object detail = null, bool error = false)
        {
            _fullTasLastResult = detail ?? new { kind = "note", text = line, error };
            FullTasLog(line);
        }


        void FullTasEvent(bool reached)
        {
            if (reached && _fullTas.Done && _engine.IsRunning)
                _engine.CompleteFromGame(MacroEngine.FullTasFinishReason, _engine.PhysicsTicks * Time.fixedDeltaTime);
        }

        void FullTasCourseStarted(int course)
        {
            if (_fullTas != null && _engine.IsRunning) FullTasEvent(_fullTas.CourseStarted(course, _engine.RunTicks, _engine.PhysicsTicks));
        }

        void FullTasCourseFinished(int course)
        {
            if (_fullTas != null && _engine.IsRunning) FullTasEvent(_fullTas.CourseFinished(course, _engine.RunTicks, _engine.PhysicsTicks));
        }

        // Checked where the category finish boxes are (before the next tick's inputs): the offline engine checks the same
        // collider overlap right after the tick. A from= side needs the player's bounds at the check before.
        void FullTasCheckpoint(Movement player)
        {
            Rect previous = _fullTasPlayerBounds;
            _fullTasPlayerBounds = PlayerBounds(player);
            FullTasGoal g = _fullTas?.Current;
            if (g == null || g.kind != FullTasGoal.At || !_engine.IsRunning || !TouchesBox(player, g.x, g.y, g.w, g.h)) return;
            Vector2 origin = CurrentOrigin();
            float x0 = g.x - g.w / 2f + origin.x, y0 = g.y - g.h / 2f + origin.y;
            if (!g.EnteredFrom(x0, x0 + g.w, y0, y0 + g.h, previous.xMin, previous.xMax, previous.yMin, previous.yMax)) return;
            FullTasEvent(_fullTas.Touched(_engine.RunTicks, _engine.PhysicsTicks));
        }

        Rect _fullTasPlayerBounds;

        static Rect PlayerBounds(Movement player)
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            foreach (BoxCollider2D box in player.GetComponents<BoxCollider2D>())
            {
                if (!box.enabled) continue;
                Bounds b = box.bounds;
                x0 = Mathf.Min(x0, b.min.x); y0 = Mathf.Min(y0, b.min.y); x1 = Mathf.Max(x1, b.max.x); y1 = Mathf.Max(y1, b.max.y);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        void FullTasBought(upgradeBox box)
        {
            if (_fullTas == null || !_engine.IsRunning) return;
            string path = HierarchyPath(box.transform);
            _fullTasUnlocks.Bought(FullTasBoxes.FirstOrDefault(b => b.path == path).token);
            FullTasEvent(_fullTas.Bought(path, FullTasBoxId(box), _engine.RunTicks, _engine.PhysicsTicks));
        }

        static string FullTasBoxId(upgradeBox box)
        {
            return FullTasGoal.BoxId(HierarchyPath(box.transform), box.transform.GetSiblingIndex());
        }

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

        readonly Dictionary<string, upgradeBox> _bonusRewardBoxes = new Dictionary<string, upgradeBox>(StringComparer.OrdinalIgnoreCase);

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


        bool FullTasStartReady(FullTasRunDef run)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot fresh = NewGameStart(run.Vman);
            if (((Vector2)player.transform.position - (Vector2)fresh.position).sqrMagnitude >= 64f * 64f)
            {
                TurboRunner.Restore(fresh, player);
                _fullTasParkedFrame = Time.frameCount;
                return false;
            }
            return Time.frameCount - _fullTasParkedFrame >= 10 && ZonesSettled();
        }

        bool FreshGameFor(FullTasRunDef run) { return FreshGameInWorld && _newGameVman == run.Vman; }

        bool FullTasCanJudge(FullTasRunDef run)
        {
            return _fullTasPending == null && _fullTas == null && _newGamePhase == 0 && _bootstrapRequest == null && !PlaybackActive
                && !_recorder.IsRecording && FreshGameFor(run) && FullTasStartReady(run);
        }

        const int VmanSetupJumpTicks = 45;
        static readonly AccessTools.FieldRef<upgradeBox, double> BoxEffectMult = AccessTools.FieldRefAccess<upgradeBox, double>("effectMult");

        void BuyVmanStart(Movement player)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            SetArea1State(stats, globalStats.area1states.Overgrown, "the Vman start");
            upgradeBox[] boxes = FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (globalStats.globalUpgradeSet upgrade in new[] { globalStats.globalUpgradeSet.vmanTime, globalStats.globalUpgradeSet.VmanStartsWithOmniDash })
            {
                globalStats.globalUpgradeDict[upgrade] = boxes.Where(b => b.upgrade == localUpgrades.localUpgradeSet.GLOBAL && b.globalUpgrade == upgrade)
                    .Sum(b => b.Cap * BoxEffectMult(b));
                _newGameNotes.Add(upgrade + " " + globalStats.globalUpgradeDict[upgrade].ToString(CultureInfo.InvariantCulture));
            }
            upgradeBox start = boxes.FirstOrDefault(b => b.upgrade == localUpgrades.localUpgradeSet.GLOBAL
                && b.globalUpgrade == globalStats.globalUpgradeSet.startVmanSpeedrun && BoxEffectMult(b) == 1.0);
            if (start == null || !start.gameObject.activeInHierarchy || !start.isActive)
            {
                FailNewGame("No active 1x \"Start a VMAN speedrun\" box with Area 1 overgrown.");
                return;
            }
            Bounds box = start.GetComponents<Collider2D>().Select(c => c.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            RaycastHit2D floor = Physics2D.Raycast(new Vector2(box.center.x, box.min.y - 1f), Vector2.down, 5000f, LayerMask.GetMask("Ground"));
            if (floor.collider == null)
            {
                FailNewGame("No floor under the Vman start box.");
                return;
            }
            float feet = player.transform.position.y - player.GetComponents<BoxCollider2D>().Where(c => c.enabled).Min(c => c.bounds.min.y);
            Rigidbody2D body = player.GetComponent<Rigidbody2D>();
            player.transform.position = new Vector3(box.center.x, floor.point.y + feet + .5f, player.transform.position.z);
            body.position = player.transform.position;
            body.linearVelocity = Vector2.zero;
            Physics2D.SyncTransforms();
            _newGameNotes.Add("player under " + HierarchyPath(start.transform) + " at " + ((Vector2)player.transform.position - CurrentOrigin()).ToString("0.#"));
            _vmanSetupQuiet = _engine.Quiet;
            _engine.Quiet = true;
            _engine.Run(new MacroDefinition { name = "Vman start box", steps = {
                new MacroStep { type = MacroStepType.RawInput, durationTicks = 1, endCondition = ActionEndCondition.FixedTicks, rawJumpPressed = true, rawJumpHeld = true },
                new MacroStep { type = MacroStepType.RawInput, durationTicks = VmanSetupJumpTicks, endCondition = ActionEndCondition.FixedTicks, rawJumpHeld = true } } });
            _vmanSetupPlayer = player;
            _newGamePhase = 4;
            _newGameFrame = Time.frameCount;
        }

        void EndVmanSetupJump()
        {
            _engine.Discard();
            _engine.Quiet = _vmanSetupQuiet;
        }

        void ParkAtNewGameStart()
        {
            Movement player = FindWorldMovement();
            if (FreshGame && player == _newGamePlayer) TurboRunner.Restore(_newGameStart, player);
        }

        bool EvaluateFullTas(FullTasRunDef run, List<SolverTick> inputs, out float time, out int finishTick, out FullTasTracker tracker)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot from = NewGameStart(run.Vman);
            tracker = new FullTasTracker(ResolveFullTasGoals(run));
            _fullTas = tracker;
            _fullTasUnlocks = default;
            _silentEvaluation = true;
            _engine.Quiet = true;
            _silentFinished = false;
            _silentTime = 0f;
            int lastTick = -1, idleTicks = 0;
            try
            {
                TurboRunner.Restore(from, player);
                _engine.Resume(from.engine, ToSteps(inputs), 0);
                TurboRunner.Run(player, () => _engine.IsRunning && idleTicks < 20, inputs.Count + 300 * (tracker.Goals.Length + 1), () =>
                {
                    _engine.Clock++;
                    _engineSimTickHook?.Invoke(player);
                    if (!_engine.IsRunning) return;
                    if (run.Vman && !VmanScript.isCurrentlyVman) { _engine.Abort("The Vman speedrun's time ran out"); return; }
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
            string line = "Full-TAS " + run.name + ": " + (finished ? time.ToString("0.00", CultureInfo.InvariantCulture) + "s in turbo" : "no finish in turbo")
                + ", " + verdict.reason;
            SetFullTasResult(line, new { kind = "judge", text = line, run = run.name, error = !finished, finished, time = finished ? time : (float?)null, saved = verdict.saved,
                reason = verdict.reason, reached = tracker.Next, of = tracker.Goals.Length, next = tracker.Current?.label, splits = verdict.splits });
            return verdict;
        }

        string JudgeFullTasFile(string path, string run)
        {
            List<SolverTick> inputs = JObject.Parse(File.ReadAllText(path))["ticks"].ToObject<List<SolverTick>>();
            FullTasVerdict v = FullTasJudge(FindFullTasRun(run), inputs);
            return (v.finished ? "turbo " + v.time.ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish in turbo") + ", " + v.reason;
        }

        object FullTasWhenReady(WebStudioRequest request, FullTasRunDef run, string what, Func<object> action)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_fullTasPending != null) throw new InvalidOperationException("Busy: " + _fullTasPending.what + ".");
            bool fresh = FreshGameFor(run);
            if (fresh && _newGamePhase == 0 && _bootstrapRequest == null && !(_solverRunning && _solveProcess == null) && FullTasStartReady(run))
                return action();
            var pending = new FullTasPending { request = request, run = run, what = what, action = action, since = Time.frameCount };
            if (!fresh && _newGamePhase == 0)
            {
                if (_solverRunning) throw new InvalidOperationException("A Full-TAS run needs a fresh new game: stop the Optimize run first.");
                LoadNewGame(null, run.Vman);
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
            if (!FreshGameFor(pending.run))
            {
                if (pending.loaded || _solverRunning)
                {
                    CompleteFullTasPending(500, new { ok = false, error = pending.what + ": no fresh " + (pending.run.Vman ? "Vman speedrun" : "new game") + " in the world." });
                    return;
                }
                LoadNewGame(null, pending.run.Vman);
                pending.loaded = true;
                return;
            }
            if (!FullTasStartReady(pending.run)) return;
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
            if (status != 200 && _fullTasProcess == null && _fullTasSolveStatus.StartsWith("Capturing", StringComparison.Ordinal)) _fullTasSolveStatus = "Not started";
            request.StatusCode = status;
            request.Response = JsonConvert.SerializeObject(response);
            request.Completed.Set();
        }


        object FullTasTrace(FullTasRunDef run, List<SolverTick> inputs, string name, bool world)
        {
            FullTasGoal[] goals = ResolveFullTasGoals(run);
            Directory.CreateDirectory(EngineSimDir);
            string path = Path.Combine(EngineSimDir, name + ".json");
            if (world) EngineSimExport.WriteWorld(Path.Combine(EngineSimDir, name + ".world.json"));
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot start = NewGameStart(run.Vman);
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
                if (run.Vman) { w.WritePropertyName("start"); w.WriteValue(VmanStart); }
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
                    if (g.kind == FullTasGoal.Reward || g.kind == FullTasGoal.At)
                    {
                        w.WritePropertyName("rect"); w.WriteStartArray();
                        json.Float(g.x + origin.x); json.Float(g.y + origin.y); json.Float(g.w); json.Float(g.h);
                        w.WriteEndArray();
                        if (g.from != 0) { w.WritePropertyName("from"); w.WriteValue(g.from); }
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

        void RememberBestSplits(FullTasRunDef run, List<SolverTick> inputs, bool finished, float time, FullTasTracker tracker)
        {
            MacroDefinition best = FullTasBestRoute(run.name);
            if (!finished || best == null || _fullTasSplits.ContainsKey(best.id) || Mathf.Abs(CategoryBest(FullTasCategory(run.name)) - time) > .0001f) return;
            List<SolverTick> saved = ToTicks(best.steps);
            if (saved.Count > inputs.Count || JsonConvert.SerializeObject(saved) != JsonConvert.SerializeObject(inputs.Take(saved.Count).ToList())) return;
            StoreFullTasSplits(best, FullTasSplits(tracker));
        }

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
            return FullTasOfflineBest(run.name, ResolveFullTasGoals(run).Select(g => g.label).ToList())?.inputs ?? new List<SolverTick>();
        }

        object FullTasTraceRequest(WebStudioRequest request, JObject body)
        {
            FullTasRunDef run = FindFullTasRun(Text(body, "fulltas"));
            ResolveFullTasGoals(run);
            List<SolverTick> inputs = FullTasInputs(run, body);
            string name = string.IsNullOrEmpty(Text(body, "name")) ? "fulltas-" + run.name : Text(body, "name");
            bool world = Truthy(body, "world");
            return FullTasWhenReady(request, run, "Capturing trace " + name, () => FullTasTrace(run, inputs, name, world));
        }


        void StartFullTasPlayback(FullTasRunDef run, MacroDefinition route)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            if (_fullTasPending != null) throw new InvalidOperationException("Busy: " + _fullTasPending.what + ".");
            if (route == null) throw new InvalidOperationException("Full-TAS run " + run.name + " has no route yet.");
            var tracker = new FullTasTracker(ResolveFullTasGoals(run));
            StartNewGame(null, run.Vman);
            EnsureGameUnpaused();
            FullTasOffline offline = FullTasOfflineBest(run.name, tracker.Goals.Select(g => g.label).ToList());
            int expected = FullTasBestRoute(run.name) == route ? tracker.Goals.Length : offline?.reached ?? 0;
            _fullTasPlay = new FullTasPlay { run = run, route = route, tracker = tracker, expected = expected };
        }

        void BeginFullTasPlayback(Movement movement)
        {
            FullTasPlay play = _fullTasPlay;
            play.started = true;
            UnityEngine.Random.InitState(FullTasSeed);
            _brakeTicks = -1;
            play.tracker.Reset();
            _fullTas = play.tracker;
            _fullTasUnlocks = default;
            _engine.Run(play.route, false, true, 0, null);
            if (!_engine.IsRunning) { FinishFullTasPlayback(false, _engine.Status); return; }
            if (_engine.TakeQuickRestart()) QuickRestart(movement);
        }

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
            string line = "Full-TAS " + play.run.name + (finished ? " finished in " + time.ToString("0.00", CultureInfo.InvariantCulture) + "s"
                : " stopped after " + tracker.Next + " of " + tracker.Goals.Length + " goals" + (next == null ? "" : " (next " + next.label + ")") + ": " + reason)
                + DroppedFramesText() + (splitText.Length > 0 ? " [" + splitText + "]" : "");
            SetFullTasResult(line, new { kind = "play", text = line, run = play.run.name, error = !finished && tracker.Next < play.expected, finished, time,
                reached = tracker.Next, of = tracker.Goals.Length, expected = play.expected, next = next?.label, reason = finished ? null : reason,
                droppedFrames = _droppedFrames, splits });
            if (finished && FullTasBestRoute(play.run.name) == play.route && !_fullTasSplits.ContainsKey(play.route.id)
                && Mathf.Abs(CategoryBest(FullTasCategory(play.run.name)) - time) <= .0001f)
                StoreFullTasSplits(play.route, splits);
        }

        void DropPendingFullTasPlayback(string reason)
        {
            if (_fullTasPlay == null || _fullTasPlay.started) return;
            SetFullTasResult("Full-TAS " + _fullTasPlay.run.name + " did not start: " + reason, null, true);
            _fullTasPlay = null;
        }


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
            if (Truthy(body, "unsolved")) args.Append(" --unsolved");
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
            _fullTasEvents.Clear();
            _fullTasProgress = null;
            _fullTasSolveError = null;
            _fullTasCommand = EngineSimExe + " " + args;
            _fullTasSolveStatus = "Solving " + run.name + " offline on " + threads + " threads from " + seeds + " seed routes";
            FullTasLog(_fullTasSolveStatus + " (" + minutes.ToString(CultureInfo.InvariantCulture) + " min)");
        }

        static readonly Regex FullTasSeedRoute = new Regex(@"^([A-Za-z0-9]+)_");
        static readonly Regex FullTasSeedTransition = new Regex(@"^(bonus\d+|\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);

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
                string plain = stale ? route.name.Substring(0, route.name.Length - " stale".Length) : route.name;
                Match category = CategoryRoute.Match(plain);
                if (!category.Success) category = FarmRoute.Match(plain);
                string caps = string.IsNullOrEmpty(target?.minCapabilities) ? "none" : target.minCapabilities;
                float time = float.MaxValue;
                if (category.Success)
                {
                    caps = CapabilityLabels.OrderByDescending(p => p.Value.Length)
                        .First(p => category.Groups[1].Value.EndsWith("_" + p.Value, StringComparison.OrdinalIgnoreCase)).Key;
                    if (!float.TryParse(category.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out time)) time = float.MaxValue;
                }
                bool standing = target != null, restart = false;
                List<SolverTick> ticks = ToTicks(route.steps);
                string json = JsonConvert.SerializeObject(new { name = route.name, token, course, goals, capabilities = caps, standing, restart, ticks });
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

        // unsolved: the search reaches the goals the best misses first (IGTAP.EngineSim fulltas --unsolved); otherwise it
        // polishes the goals the best reaches, from goal 1.
        object StartFullTasSolve(WebStudioRequest request, JObject body)
        {
            if (_fullTasProcess != null) throw new InvalidOperationException("A Full-TAS solve is already running (" + _fullTasSolveRun + ").");
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new InvalidOperationException("The offline solver is not built (" + exe + "): dotnet build enginesim\\Cli\\IGTAP.EngineSim.csproj -c Release");
            FullTasRunDef run = FindFullTasRun(Text(body, "name"));
            ResolveFullTasGoals(run);
            string trace = FullTasTracePath(run.name);
            bool current = FullTasTraceCurrent(run, trace);
            var options = (JObject)body.DeepClone();
            Func<object> launch = () =>
            {
                if (_fullTasProcess != null) throw new InvalidOperationException("A Full-TAS solve is already running (" + _fullTasSolveRun + ").");
                bool captured = false;
                try
                {
                    if (!current)
                    {
                        FullTasTrace(run, FullTasInputs(run, new JObject()), "fulltas-" + run.name, true);
                        captured = true;
                    }
                    LaunchFullTasSolve(run, trace, options);
                }
                catch (Exception ex)
                {
                    _fullTasSolveStatus = "Not started: " + ex.Message;
                    _fullTasSolveError = "The solve didn't start: " + ex.Message;
                    throw;
                }
                return new { ok = true, trace = trace, captured = captured, status = _fullTasSolveStatus };
            };
            if (current) return launch();
            _fullTasSolveRun = run.name;
            _fullTasSolveStatus = "Capturing the trace of " + run.name;
            try { return FullTasWhenReady(request, run, _fullTasSolveStatus, launch); }
            catch (Exception ex)
            {
                _fullTasSolveStatus = "Not started: " + ex.Message;
                _fullTasSolveError = "The solve didn't start: " + ex.Message;
                throw;
            }
        }

        // A trace searches the goals in its header, so one captured before the run's goals or start changed is recaptured.
        bool FullTasTraceCurrent(FullTasRunDef run, string trace)
        {
            if (!File.Exists(trace)) return false;
            try
            {
                JObject header;
                using (var reader = new JsonTextReader(new StreamReader(trace)))
                {
                    // The header (fulltas) precedes the per-tick states; read only up to it.
                    header = null;
                    while (reader.Read())
                        if (reader.TokenType == JsonToken.PropertyName && (string)reader.Value == "fulltas" && reader.Depth == 1)
                        {
                            reader.Read();
                            header = JObject.Load(reader);
                            break;
                        }
                }
                if (header == null) return false;
                string[] labels = header["goals"].Select(g => (string)g["label"]).ToArray();
                return (string)header["start"] == run.start && labels.SequenceEqual(ResolveFullTasGoals(run).Select(g => g.label));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS trace " + trace + " unreadable: " + ex.Message);
                return false;
            }
        }

        void KillFullTasSolve(string status, string error = null)
        {
            if (_fullTasProcess == null) return;
            try { if (!_fullTasProcess.HasExited) _fullTasProcess.Kill(); }
            catch (Exception ex) { Logger.LogWarning("Could not stop the Full-TAS solve: " + ex.Message); }
            _fullTasProcess.Dispose();
            _fullTasProcess = null;
            while (_fullTasOutput.TryDequeue(out string line)) FullTasSolveLine(line);
            _fullTasSolveStatus = status;
            if (error != null) _fullTasSolveError = error;
            FullTasLog(status);
        }

        void FullTasSolveLine(string line)
        {
            if (line.StartsWith("@fulltas ", StringComparison.Ordinal))
            {
                JObject e = null;
                try { e = JObject.Parse(line.Substring("@fulltas ".Length)); }
                catch (JsonException) { }
                if (e != null)
                {
                    e["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if ((string)e["type"] == "status") { _fullTasProgress = e; return; }
                    _fullTasEvents.Add(e);
                    if (_fullTasEvents.Count > MaxFullTasEvents) _fullTasEvents.RemoveRange(1, _fullTasEvents.Count - MaxFullTasEvents);
                    return;
                }
            }
            _fullTasLog.Add(line);
            if (_fullTasLog.Count > MaxFullTasLog) _fullTasLog.RemoveRange(0, _fullTasLog.Count - MaxFullTasLog);
            Logger.LogInfo("fulltas: " + line);
        }

        void PumpFullTasSolve()
        {
            if (_fullTasProcess == null) return;
            try
            {
                while (_fullTasOutput.TryDequeue(out string line)) FullTasSolveLine(line);
                if (!_fullTasProcess.HasExited) return;
                _fullTasProcess.WaitForExit();
                int code = _fullTasProcess.ExitCode;
                if (code == 0) KillFullTasSolve("Finished");
                else KillFullTasSolve("The offline solver exited with code " + code, "The offline solver crashed (exit code " + code + "). The log below has its last words.");
            }
            catch (Exception ex) { Logger.LogError(ex); KillFullTasSolve("Solve error: " + ex.Message, "The plugin lost track of the solve: " + ex.Message); }
        }

        string FullTasSolvingGroup { get { return _fullTasProcess == null ? null : "fulltas-" + _fullTasSolveRun; } }

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


        object HandleFullTas(WebStudioRequest request, JObject body)
        {
            switch (request.Path)
            {
                case "/api/fulltas/goals":
                {
                    if (!(body["goals"] is JArray goals)) throw new InvalidOperationException("goals: an array of goal tokens.");
                    FullTasRunDef run = SetFullTasGoals(Text(body, "name"), goals.Select(g => g.Type == JTokenType.Null ? "" : g.ToString()),
                        body["start"] == null ? null : body["start"].Type == JTokenType.Null ? "" : body["start"].ToString());
                    return new { ok = true, name = run.name, start = run.start, goals = run.goals };
                }
                case "/api/fulltas/capture":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    ResolveFullTasGoals(run);
                    List<SolverTick> inputs = FullTasInputs(run, new JObject());
                    return FullTasWhenReady(request, run, "Capturing the trace of " + run.name, () => FullTasTrace(run, inputs, "fulltas-" + run.name, true));
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
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    ResolveFullTasGoals(run);
                    if (!(body["ticks"] is JArray)) throw new InvalidOperationException("ticks: the run's inputs.");
                    List<SolverTick> inputs = body["ticks"].ToObject<List<SolverTick>>();
                    return FullTasWhenReady(request, run, "Judging a " + run.name + " route", () => FullTasJudge(run, inputs));
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
                splits = splits.TakeWhile((s, i) => i < labels.Length && s.label == labels[i]).ToList();
                FullTasOffline offline = FullTasOfflineBest(r.name, labels);
                return new { name = r.name, start = r.start, goals = labels, tokens = labels, error = error,
                    breaker = FullTasBreakerGoal(labels),
                    best = best == null ? null : new { routeId = best.id, name = best.name, time = float.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture), splits = splits },
                    offline = offline == null ? null : new { reached = offline.reached, time = offline.time, splits = offline.splits },
                    playable = best != null || offline != null && offline.inputs.Count > 0,
                    traceReady = File.Exists(FullTasTracePath(r.name)) };
            }).ToArray();
            FullTasPlay play = _fullTasPlay;
            return new {
                runs = runs,
                solve = new { running = _fullTasProcess != null, name = _fullTasSolveRun, status = _fullTasSolveStatus, error = _fullTasSolveError,
                    command = _fullTasCommand, events = _fullTasEvents, progress = _fullTasProgress, log = _fullTasLog },
                lastResult = _fullTasLastResult,
                goalsOverlay = _settings.fullTasGoalsRun,
                pending = _fullTasPending?.what,
                playing = play == null ? null : new { name = play.run.name, started = play.started, reached = play.tracker.Next, of = play.tracker.Goals.Length,
                    next = play.tracker.Current?.label, droppedFrames = _droppedFrames } };
        }

        static int? FullTasBreakerGoal(string[] labels)
        {
            for (int i = 0; i < labels.Length; i++)
                if (labels[i].StartsWith("buy:", StringComparison.Ordinal) && labels[i].Substring(4).Split('+').Any(t => FullTasBreakerTokens.Contains(t.Trim())))
                    return i;
            return null;
        }
    }
}
