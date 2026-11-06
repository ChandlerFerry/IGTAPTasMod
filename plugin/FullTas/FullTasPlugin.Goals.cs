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
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        void LoadFullTas()
        {
            try
            {
                if (File.Exists(FullTasSplitsPath))
                    _fullTasSplits = JsonConvert.DeserializeObject<Dictionary<string, List<FullTasSplit>>>(File.ReadAllText(FullTasSplitsPath))
                        ?? new Dictionary<string, List<FullTasSplit>>();
            }
            catch (Exception ex) { Logger.LogWarning("fulltas-splits.json rejected: " + ex.Message); }
            LoadFullTasClasses();
            _fullTasWritten = FullTasRunsStamp();
            List<FullTasRunDef> previous = _fullTasRuns;
            var runs = new List<FullTasRunDef>();
            string[] files = Directory.Exists(FullTasRunsDir) ? Directory.GetFiles(FullTasRunsDir, "*.json") : new string[0];
            foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    FullTasRunDef run = JsonConvert.DeserializeObject<FullTasRunDef>(File.ReadAllText(file));
                    if (run == null || !string.Equals(run.name + ".json", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("its \"name\" is \"" + run?.name + "\", not its file's");
                    runs.Add(run);
                }
                catch (Exception ex) { Logger.LogWarning("data\\runs\\" + Path.GetFileName(file) + " rejected: " + ex.Message); }
            }
            _fullTasRuns = runs;
            _fullTasAvoids.Clear();
            foreach (FullTasRunDef run in _fullTasRuns)
                RetireOutdatedFullTasRoutes(run, previous != null
                    && FullTasRunChanged(previous.Find(r => string.Equals(r.name, run.name, StringComparison.OrdinalIgnoreCase)), run));
        }

        void SaveFullTasRun(FullTasRunDef run)
        {
            BumpWebState();
            string path = FullTasRunFile(run.name);
            try
            {
                Directory.CreateDirectory(FullTasRunsDir);
                File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(run, Formatting.Indented).Replace("\r\n", "\n") + "\n");
                ReplaceAtomic(path + ".tmp", path);
            }
            catch (Exception ex) { Logger.LogError("Could not save " + path + ": " + ex); }
            _fullTasWritten = FullTasRunsStamp();
        }

        static long FullTasRunsStamp()
        {
            if (!Directory.Exists(FullTasRunsDir)) return 0;
            long h = 17;
            foreach (FileInfo f in new DirectoryInfo(FullTasRunsDir).GetFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal))
                h = (h * 31 + f.Name.GetHashCode()) * 31 + f.LastWriteTimeUtc.Ticks;
            return h;
        }

        string[] FullTasLabels(FullTasRunDef run)
        {
            try { return ResolveFullTasGoals(run).Select(g => g.label).ToArray(); }
            catch (InvalidOperationException) { return run.Labels.ToArray(); }
        }

        static bool TreeIndex(JToken t) { return t.Type == JTokenType.Integer && (int)t >= 0 && (int)t <= 27; }

        bool FullTasRunChanged(FullTasRunDef old, FullTasRunDef run)
        {
            return old == null || old.start != run.start || old.savestate != run.savestate || old.RebaseAt != run.RebaseAt || !JToken.DeepEquals(old.tree, run.tree)
                || !FullTasLabels(old).SequenceEqual(FullTasLabels(run));
        }

        void RetireOutdatedFullTasRoutes(FullTasRunDef run, bool changed)
        {
            string[] labels;
            try { labels = ResolveFullTasGoals(run).Select(g => g.label).ToArray(); }
            catch (InvalidOperationException) { return; }
            bool retired = false;
            foreach (MacroDefinition m in _library.macros)
            {
                if (!FullTasRouteOf(m.name, out string r, out _, out _) || !string.Equals(r, run.name, StringComparison.OrdinalIgnoreCase)) continue;
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
                ?? throw new InvalidOperationException("No Full-TAS run named \"" + name + "\" in data\\runs\\.");
        }

        FullTasGoal[] ResolveFullTasGoals(FullTasRunDef run)
        {
            if (!RunName.IsMatch(run.name ?? "")) throw new InvalidOperationException("Full-TAS run names are letters, digits, \"%\" and \"-\" only (\"" + run.name + "\").");
            if (run.goals == null || run.goals.Count == 0) throw new InvalidOperationException("Full-TAS run " + run.name + " has no goals.");
            string invalid = FullTasGoalIds.Invalid(run.Ids, run.goals.Count);
            if (invalid != null) throw new InvalidOperationException("Full-TAS run " + run.name + ": " + invalid + " (data/runs/<run>.json \"goals\": [{\"id\", \"goal\"}]; tools/migrate-goal-ids.py gives a run its ids).");
            if (run.start != null && run.start != VmanStart)
                throw new InvalidOperationException("Full-TAS run " + run.name + ": start is \"" + VmanStart + "\" (a Vman speedrun's world) or unset (a new game), not \"" + run.start + "\".");
            if (run.savestate != null && _savestates.Find(s => s.id == run.savestate) == null)
                throw new InvalidOperationException("Full-TAS run " + run.name + ": no savestate " + run.savestate + ".");
            if (run.rebaseAt != null && (run.savestate == null || run.rebaseAt.Length != 2))
                throw new InvalidOperationException("Full-TAS run " + run.name + ": rebaseAt is a world [x, y] and needs a savestate start.");
            if (run.tree != null && (run.savestate == null || !(run.tree is JArray ? run.tree.All(TreeIndex) : TreeIndex(run.tree))))
                throw new InvalidOperationException("Full-TAS run " + run.name + ": tree is a level 0-27 or an array of segments 0-27 and needs a savestate start.");
            if (run.buys != null && run.buys != "strict" && run.buys != "loose")
                throw new InvalidOperationException("Full-TAS run " + run.name + ": buys is \"strict\" or \"loose\", not \"" + run.buys + "\".");
            return run.goals.Select(g =>
            {
                FullTasGoal goal = ResolveFullTasGoal(g.goal);
                goal.id = g.id;
                return goal;
            }).ToArray();
        }

        FullTasTracker NewFullTasTracker(FullTasRunDef run)
        {
            var tracker = new FullTasTracker(ResolveFullTasGoals(run));
            if (run.LooseBuys) tracker.LooseBuys();
            return tracker;
        }

        FullTasGoal ResolveFullTasGoal(string token)
        {
            string text = (token ?? "").Trim().ToLowerInvariant();
            Match m = FullTasToken.Match(text);
            if (!m.Success) throw new InvalidOperationException("Unknown Full-TAS goal \"" + token + "\": start:<course>, record:<course>, farm:<course>, buy:<box>[+<box>...], reward:<bonus> (a courses.json bonus name, such as bonus3 or unlockJumpDash) or at:<x>,<y>,<width>,<height>[,from=<side>[+<side>...]].");
            string kind = m.Groups[1].Value, arg = m.Groups[2].Value.Trim();
            var goal = new FullTasGoal { kind = kind };
            if (goal.IsCourse)
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
                int[] boxes = parts.Select(p => Array.FindIndex(FullTasGoal.Boxes, b => b.token == p)).ToArray();
                if (boxes.Any(i => i < 0))
                    throw new InvalidOperationException("Full-TAS goal \"" + token + "\": boxes are " + string.Join(", ", FullTasGoal.Boxes.Select(b => b.token)) + " (joined with \"+\").");
                if (parts.Length > MaxBuyGoalBoxes) throw new InvalidOperationException("Full-TAS goal \"" + token + "\" buys more than " + MaxBuyGoalBoxes + " boxes.");
                goal.boxes = boxes.Select(i => FullTasGoal.BoxId(FullTasGoal.Boxes[i].path, FullTasGoal.Boxes[i].sibling)).ToArray();
                goal.label = "buy:" + string.Join("+", parts);
            }
            else if (kind == FullTasGoal.At)
            {
                List<string> parts = arg.Split(',').Select(p => p.Trim()).ToList();
                string from = parts.Count == 5 && parts[4].StartsWith("from=", StringComparison.Ordinal) ? parts[4].Substring("from=".Length) : null;
                if (from != null) parts.RemoveAt(4);
                float[] rect = parts.Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN).ToArray();
                if (rect.Length != 4 || rect.Any(float.IsNaN) || !(rect[2] > 0f) || !(rect[3] > 0f))
                    throw new InvalidOperationException("Full-TAS goal \"" + token + "\": at:<x>,<y>,<width>,<height>[,from=<side>[+<side>...]], the area goal rect's centre and size in courses.json's frame; sides "
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

        FullTasRunDef SetFullTasGoals(string name, IEnumerable<string> tokens, string start, IList<string> ids = null)
        {
            string trimmed = (name ?? "").Trim();
            FullTasRunDef old = _fullTasRuns.Find(r => string.Equals(r.name, trimmed, StringComparison.OrdinalIgnoreCase));
            List<string> labels = tokens.Select(t => ResolveFullTasGoal(t).label).ToList();
            ids = ids ?? FullTasGoalIds.Carry(old?.Ids, old?.Labels, labels);
            var kept = (old?.goals ?? new List<FullTasRunGoal>()).ToDictionary(g => g.id ?? "");
            var run = new FullTasRunDef { name = trimmed, start = start == null ? old?.start : start.Length == 0 ? null : start, savestate = old?.savestate, rebaseAt = old?.rebaseAt,
                tree = old?.tree, seeds = old?.seeds, buys = old?.buys, human = old?.human, section = old?.section, avoid = old?.avoid,
                goals = labels.Select((l, i) => new FullTasRunGoal { id = ids[i], goal = l,
                    avoid = kept.TryGetValue(ids[i], out FullTasRunGoal was) ? was.avoid : null, avoidAfter = was?.avoidAfter }).ToList() };
            ResolveFullTasGoals(run);
            int at = _fullTasRuns.FindIndex(r => string.Equals(r.name, run.name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) _fullTasRuns[at] = run;
            else _fullTasRuns.Add(run);
            SaveFullTasRun(run);
            RetireOutdatedFullTasRoutes(run, FullTasRunChanged(old, run));
            return run;
        }

        MacroDefinition FullTasBestRoute(string run) { return FullTasClassBest(run, MajorClass, out _); }

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
                splits.Add(new FullTasSplit { id = tracker.Goals[i].id, label = tracker.Goals[i].label, ticks = tracker.PhysicsTicks[i], held = tracker.HeldFrames[i] });
            return splits;
        }

        void StoreFullTasSplits(MacroDefinition route, List<FullTasSplit> splits)
        {
            if (route == null) return;
            _fullTasSplits[route.id] = splits;
            foreach (string id in _fullTasSplits.Keys.Where(k => !_library.macros.Any(m => m.id == k)).ToList()) _fullTasSplits.Remove(id);
            WriteJsonAtomic(FullTasSplitsPath, _fullTasSplits, false);
        }

        void FullTasAreaGoal(Movement player)
        {
            Rect previous = _fullTasPlayerBounds;
            _fullTasPlayerBounds = PlayerBounds(player);
            FullTasGoal g = _fullTas?.Current;
            if (g == null || g.kind != FullTasGoal.At || !_engine.IsRunning || !TouchesBox(player, g.x, g.y, g.w, g.h)) return;
            Vector2 origin = CurrentOrigin();
            float x0 = g.x - g.w / 2f + origin.x, y0 = g.y - g.h / 2f + origin.y;
            if (!g.EnteredFrom(x0, x0 + g.w, y0, y0 + g.h, previous.xMin, previous.xMax, previous.yMin, previous.yMax)) return;
            FullTasEvent(_fullTas.Touched(_engine.RunTicks, _engine.PhysicsTicks, _engine.HeldFrames));
        }

        static Rect PlayerBounds(Movement player)
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            player.GetComponents(TouchBoxes);
            foreach (BoxCollider2D box in TouchBoxes)
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
            _fullTasUnlocks.Bought(FullTasGoal.Boxes.FirstOrDefault(b => b.path == path).token);
            FullTasEvent(_fullTas.Bought(path, FullTasBoxId(box), _engine.RunTicks, _engine.PhysicsTicks, _engine.HeldFrames));
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
            if (best == null) throw new InvalidOperationException("Full-TAS goal reward:" + bonus.name + ": " + (WorldNotLoaded() ?? "no upgrade box near the bonus's end rect."));
            string id = FullTasBoxId(best);
            _fullTasRewardBoxes[bonus.name] = id;
            return id;
        }

        upgradeBox BonusRewardBox(CourseDef bonus, out bool loaded)
        {
            loaded = true;
            if (_bonusRewardBoxes.TryGetValue(bonus.name, out upgradeBox known) && known != null) return known;
            int scenes = SceneManager.GetActiveScene().handle * 31 + SceneManager.sceneCount;
            if (scenes != _bonusRewardMissScenes)
            {
                _bonusRewardMissScenes = scenes;
                _bonusRewardMisses.Clear();
            }
            if (_bonusRewardMisses.Contains(bonus.name)) return null;
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
            else _bonusRewardMisses.Add(bonus.name);
            return best;
        }

        bool FullTasStartReady(FullTasRunDef run)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot fresh = NewGameStart(run);
            if (((Vector2)player.transform.position - (Vector2)fresh.position).sqrMagnitude >= 64f * 64f)
            {
                TurboRunner.Restore(fresh, player);
                _fullTasParkedFrame = Time.frameCount;
                return false;
            }
            return Time.frameCount - _fullTasParkedFrame >= 10 && ZonesSettled();
        }

        bool FreshGameFor(FullTasRunDef run) { return FreshGameInWorld && NewGameIs(run); }

        bool FullTasCanJudge(FullTasRunDef run)
        {
            return _fullTasPending == null && _fullTas == null && _newGamePhase == 0 && _bootstrapRequest == null && !PlaybackActive
                && !_recorder.IsRecording && FreshGameFor(run) && FullTasStartReady(run);
        }

        // A savestate start's respawnPoint is no checkpoint: a quick restart to it ends the run (RULES.md exception 6).
        static void GuardSavestateRestarts(FullTasRunDef run, FullTasTracker tracker, Movement player)
        {
            if (run.savestate != null) tracker.NoCheckpointAt(player.respawnPoint.x, player.respawnPoint.y);
        }

        bool EvaluateFullTas(FullTasRunDef run, List<SolverTick> inputs, out float time, out int finishTick, out FullTasTracker tracker)
        {
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot from = NewGameStart(run);
            tracker = NewFullTasTracker(run);
            _fullTas = tracker;
            _fullTasUnlocks = _newGameUnlocks;
            _silentEvaluation = true;
            _engine.Quiet = true;
            _silentFinished = false;
            _silentTime = 0f;
            int lastTick = -1, idleTicks = 0;
            try
            {
                TurboRunner.Restore(from, player);
                GuardSavestateRestarts(run, tracker, player);
                _engine.Resume(from.engine, ToSteps(inputs), 0);
                TurboRunner.Run(player, () => _engine.IsRunning && idleTicks < 20, inputs.Count + 300 * (tracker.Goals.Length + 1), () =>
                {
                    _engine.Clock++;
                    _engineSimTickHook?.Invoke(player);
                    if (!_engine.IsRunning) return;
                    if (run.Vman && !VmanScript.isCurrentlyVman) { _engine.Abort("The Vman speedrun's time ran out"); return; }
                    if (_engine.InputsExhausted && !_engine.TickLocked) idleTicks++;
                    int tick = (int)_engine.RunTicks;
                    if (_audit != null) AuditTick(player, tick);
                    lastTick = tick;
                }, from);
            }
            finally
            {
                // Also on a throw: a resumed engine left running would play the route's inputs on in real time.
                if (_engine.IsRunning) _engine.Abort("Full-TAS run ended");
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
            var audit = StartAudit();
            for (int i = 1; i < inputs.Count; i++)
                if (inputs[i].reset && inputs[i - 1].reset)
                {
                    audit.Add("ticks " + (i - 1) + " and " + i + ": a quick restart on two ticks in a row");
                    break;
                }
            bool finished;
            float replayed;
            FullTasTracker tracker;
            _engine.HumanClass = _solverFullTasClass != null && HumanClass(_solverFullTasClass.cls);
            try { finished = EvaluateFullTas(_solverFullTas, inputs, out replayed, out _, out tracker); }
            finally { _audit = null; _engine.HumanClass = false; }
            string status = _engine.Status;
            ParkAtNewGameStart();
            if (!finished || Mathf.Abs(replayed - time) > .0001f)
                audit.Insert(0, "a replay from the new game's start " + (finished ? "finished in " + replayed.ToString("0.00", CultureInfo.InvariantCulture)
                    : "reached " + tracker.Next + " of " + tracker.Goals.Length + " goals (" + tracker.Explain(status) + ")"));
            if (audit.Count == 0)
            {
                string refused = FullTasClassViolation(_solverFullTasClass, _solverFullTas.Labels, inputs, time);
                if (refused != null) audit.Add(refused);
            }
            return audit.Count == 0 ? null : string.Join("; ", audit);
        }

        static (List<FullTasRunGoal> goals, List<List<FullTasRunGoal>> from) FullTasRouteGoals(JObject body)
        {
            if (!(body["goals"] is JArray goals)) return (null, null);
            List<FullTasRunGoal> List(JArray list) => list.Select(g => new FullTasRunGoal { id = (string)g["id"], goal = (string)g["goal"] }).ToList();
            var from = body["goalsFrom"] is JArray lists ? lists.OfType<JArray>().Select(List).ToList() : new List<List<FullTasRunGoal>>();
            return (List(goals), from);
        }

        static bool SameGoals(IList<FullTasRunGoal> a, IList<FullTasRunGoal> b)
        {
            return a.Count == b.Count && a.Zip(b, (x, y) => x.id == y.id && x.goal == y.goal).All(s => s);
        }

        FullTasVerdict FullTasJudge(FullTasRunDef run, List<SolverTick> inputs, List<FullTasRunGoal> goals = null, List<List<FullTasRunGoal>> goalsFrom = null, string cls = MajorClass)
        {
            FullTasRunDef judged = run;
            string[] current = FullTasLabels(run);
            if (goals != null && !SameGoals(goals, run.goals))
            {
                string refused = goalsFrom == null || !goalsFrom.Any(l => SameGoals(l, run.goals))
                    ? "its goals (" + goals.Count + ") come from another goal list than " + run.name + "'s " + current.Length + " goals"
                    : cls != MajorClass ? "a " + cls + " route keeps the run's goals (only the major record adopts other goals)"
                    : FullTasGoalIds.Invalid(goals.Select(g => g.id).ToList(), goals.Count) ?? FullTasGoal.EditRefused(current, goals.Select(g => g.goal).ToList());
                if (refused != null)
                {
                    string text = "Full-TAS " + run.name + ": route not judged: " + refused;
                    SetFullTasResult(text, new { kind = "judge", text, run = run.name, @class = cls, error = true, finished = false, saved = false, reason = refused });
                    return new FullTasVerdict { finished = false, reason = refused, splits = new List<FullTasSplit>(), cls = cls };
                }
                judged = JsonConvert.DeserializeObject<FullTasRunDef>(JsonConvert.SerializeObject(run));
                judged.goals = goals.Select(g => new FullTasRunGoal { id = g.id, goal = g.goal }).ToList();
                ResolveFullTasGoals(judged);
            }
            bool finished;
            float time;
            int finishTick;
            FullTasTracker tracker;
            _engine.HumanClass = HumanClass(cls);
            try { finished = EvaluateFullTas(judged, inputs, out time, out finishTick, out tracker); }
            finally { _engine.HumanClass = false; }
            long runTicks = _engine.PhysicsTicks;
            string status = _engine.Status;
            ParkAtNewGameStart();
            string category = FullTasClassCategory(run.name, cls);
            RescoreFullTasClassBest(run, cls);
            FullTasClassBest(run.name, cls, out float best);
            bool human = HumanClass(cls);
            var verdict = new FullTasVerdict { finished = finished, time = time, runTicks = runTicks, splits = FullTasSplits(tracker), cls = cls };
            if (!finished)
            {
                FullTasGoal next = tracker.Current;
                verdict.reason = "reached " + tracker.Next + " of " + tracker.Goals.Length + " goals" + (next == null ? "" : ", not " + next.label) + " (" + tracker.Explain(status) + ")";
            }
            // A human score is live time and more: a time not under the best score can't make a lower one.
            else if (!(time < best - .0001f))
                verdict.reason = (human ? "live time not under " + category + "'s score " : "not faster than " + category + " ") + best.ToString("0.00", CultureInfo.InvariantCulture);
            else
            {
                List<SolverTick> route = inputs.Take(finishTick + 1).Select(k => k.Clone()).ToList();
                string name = category + " " + time.ToString("0.00", CultureInfo.InvariantCulture);
                var classJudge = new FullTasClassJudge { run = run, cls = cls, best = best };
                _solverFullTas = judged;
                _solverFullTasClass = classJudge;
                try { verdict.saved = SaveSolverRoute(route, time); }
                finally { _solverFullTas = null; _solverFullTasClass = null; }
                verdict.score = classJudge.record?.score;
                if (verdict.saved)
                {
                    MacroDefinition saved = _library.macros.LastOrDefault(m => m.name == name);
                    StoreFullTasSplits(saved, verdict.splits);
                    if (cls != MajorClass) PruneFullTasClass(run.name, cls, saved);
                    if (classJudge.record != null) StoreFullTasClass(saved, classJudge.record);
                    verdict.reason = "saved as " + name + (classJudge.record == null ? "" : ", score " + FullTasScoreText(classJudge.record, run.Human));
                    if (judged != run) verdict.reason += "; " + AdoptFullTasGoals(run, judged.goals);
                }
                else verdict.reason = _solverLog[_solverLog.Count - 1];
            }
            string line = "Full-TAS " + run.name + (cls == MajorClass ? "" : " " + cls) + ": "
                + (finished ? time.ToString("0.00", CultureInfo.InvariantCulture) + "s live in turbo (" + runTicks + " frames)" : "no finish in turbo") + ", " + verdict.reason;
            SetFullTasResult(line, new { kind = "judge", text = line, run = run.name, @class = cls, error = !finished, finished, time = finished ? time : (float?)null,
                score = verdict.score, saved = verdict.saved, reason = verdict.reason, reached = tracker.Next, of = tracker.Goals.Length, next = tracker.Current?.label,
                splits = verdict.splits });
            return verdict;
        }

        string AdoptFullTasGoals(FullTasRunDef run, List<FullTasRunGoal> goals)
        {
            int was = run.goals.Count;
            string backup = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "runs-backup", run.name + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            if (File.Exists(FullTasRunFile(run.name))) File.Copy(FullTasRunFile(run.name), backup, true);
            FullTasRunDef adopted = SetFullTasGoals(run.name, goals.Select(g => g.goal), null, goals.Select(g => g.id).ToList());
            string text = "the run's goals are now its " + adopted.goals.Count + " (were " + was + "; " + Path.GetFileName(backup) + " kept)";
            FullTasLog("Full-TAS " + run.name + ": " + text);
            return text;
        }

        string JudgeFullTasFile(string path, string run)
        {
            JObject file = JObject.Parse(File.ReadAllText(path));
            List<SolverTick> inputs = file["ticks"].ToObject<List<SolverTick>>();
            var (goals, from) = FullTasRouteGoals(file);
            FullTasVerdict v = FullTasJudge(FindFullTasRun(run), inputs, goals, from);
            return (v.finished ? "turbo " + v.time.ToString("0.00", CultureInfo.InvariantCulture) + "s" : "no finish in turbo") + ", " + v.reason;
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
