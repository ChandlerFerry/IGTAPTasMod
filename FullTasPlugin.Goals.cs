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

        string[] FullTasLabels(FullTasRunDef run)
        {
            try { return ResolveFullTasGoals(run).Select(g => g.label).ToArray(); }
            catch (InvalidOperationException) { return (run.goals ?? new List<string>()).ToArray(); }
        }

        bool FullTasRunChanged(FullTasRunDef old, FullTasRunDef run)
        {
            return old == null || old.start != run.start || old.savestate != run.savestate || old.RebaseAt != run.RebaseAt
                || !FullTasLabels(old).SequenceEqual(FullTasLabels(run));
        }

        void RetireOutdatedFullTasRoutes(FullTasRunDef run, bool changed)
        {
            string[] labels;
            try { labels = ResolveFullTasGoals(run).Select(g => g.label).ToArray(); }
            catch (InvalidOperationException) { return; }
            string category = FullTasCategory(run.name);
            bool retired = false;
            foreach (MacroDefinition m in _library.macros)
            {
                Match g = MatchCategoryRoute(m.name);
                if (!g.Success || !string.Equals(g.Groups["category"].Value, category, StringComparison.OrdinalIgnoreCase)) continue;
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
            if (!RunName.IsMatch(run.name ?? "")) throw new InvalidOperationException("Full-TAS run names are letters, digits, \"%\" and \"-\" only (\"" + run.name + "\").");
            if (run.goals == null || run.goals.Count == 0) throw new InvalidOperationException("Full-TAS run " + run.name + " has no goals.");
            if (run.start != null && run.start != VmanStart)
                throw new InvalidOperationException("Full-TAS run " + run.name + ": start is \"" + VmanStart + "\" (a Vman speedrun's world) or unset (a new game), not \"" + run.start + "\".");
            if (run.savestate != null && _savestates.Find(s => s.id == run.savestate) == null)
                throw new InvalidOperationException("Full-TAS run " + run.name + ": no savestate " + run.savestate + ".");
            if (run.rebaseAt != null && (run.savestate == null || run.rebaseAt.Length != 2))
                throw new InvalidOperationException("Full-TAS run " + run.name + ": rebaseAt is a world [x, y] and needs a savestate start.");
            if (run.buys != null && run.buys != "strict" && run.buys != "loose")
                throw new InvalidOperationException("Full-TAS run " + run.name + ": buys is \"strict\" or \"loose\", not \"" + run.buys + "\".");
            return run.goals.Select(ResolveFullTasGoal).ToArray();
        }

        /// <summary>A goal tracker for the run under its buy rule (strict, or loose: <see cref="FullTasTracker.LooseBuys"/>).</summary>
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

        FullTasRunDef SetFullTasGoals(string name, IEnumerable<string> tokens, string start)
        {
            string trimmed = (name ?? "").Trim();
            FullTasRunDef old = _fullTasRuns.Find(r => string.Equals(r.name, trimmed, StringComparison.OrdinalIgnoreCase));
            var run = new FullTasRunDef { name = trimmed, start = start == null ? old?.start : start.Length == 0 ? null : start, savestate = old?.savestate, rebaseAt = old?.rebaseAt, seeds = old?.seeds, buys = old?.buys, goals = tokens.ToList() };
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
                Match g = MatchCategoryRoute(m.name);
                if (!g.Success || !string.Equals(g.Groups["category"].Value, category, StringComparison.OrdinalIgnoreCase)) continue;
                float time = float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture);
                if (time < bestTime) { best = m; bestTime = time; }
            }
            return best;
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
                splits.Add(new FullTasSplit { label = tracker.Goals[i].label, ticks = tracker.PhysicsTicks[i], held = tracker.HeldFrames[i] });
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
            if (best == null) throw new InvalidOperationException("Full-TAS goal reward:" + bonus.name + ": no upgrade box near the bonus's end rect.");
            string id = FullTasBoxId(best);
            _fullTasRewardBoxes[bonus.name] = id;
            return id;
        }

        upgradeBox BonusRewardBox(CourseDef bonus, out bool loaded)
        {
            loaded = true;
            if (_bonusRewardBoxes.TryGetValue(bonus.name, out upgradeBox known) && known != null) return known;
            // A bonus with no box near its end stays without one until the scenes change (the studio's course hints ask again
            // on every static state).
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

        /// <summary>A savestate start's respawnPoint is no checkpoint: a quick restart to it ends the run.</summary>
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
                    if (_engine.InputsExhausted) idleTicks++;
                    int tick = (int)_engine.RunTicks;
                    if (_audit != null) AuditTick(player, tick);
                    lastTick = tick;
                }, from);
            }
            finally
            {
                // Also when the run throws: a resumed engine left running would play the route's inputs on in real time.
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
            try { finished = EvaluateFullTas(_solverFullTas, inputs, out replayed, out _, out tracker); }
            finally { _audit = null; }
            ParkAtNewGameStart();
            if (!finished || Mathf.Abs(replayed - time) > .0001f)
                audit.Insert(0, "a replay from the new game's start " + (finished ? "finished in " + replayed.ToString("0.00", CultureInfo.InvariantCulture)
                    : "reached " + tracker.Next + " of " + tracker.Goals.Length + " goals"));
            return audit.Count == 0 ? null : string.Join("; ", audit);
        }

        /// <summary>
        /// A route's own goals (`goals`, with `goalsFrom`: the goal lists they were derived from), as `fulltas --auto`'s goals part
        /// sends and writes them with a route of goals it adopted; null when the route is of the run's goals.
        /// </summary>
        static (List<string> goals, List<List<string>> from) FullTasRouteGoals(JObject body)
        {
            if (!(body["goals"] is JArray goals)) return (null, null);
            var from = body["goalsFrom"] is JArray lists ? lists.OfType<JArray>().Select(l => l.Select(g => (string)g).ToList()).ToList() : new List<List<string>>();
            return (goals.Select(g => (string)g).ToList(), from);
        }

        FullTasVerdict FullTasJudge(FullTasRunDef run, List<SolverTick> inputs, List<string> goals = null, List<List<string>> goalsFrom = null)
        {
            // A route of other goals (the engine's goals part dropped or added economy laps): judged under its own goals, and when
            // it's saved as the record those become the run's. Only when they were derived from the run's goals as they are now, and
            // keep its end, area, reward and unlock goals (FullTasGoal.EditRefused).
            FullTasRunDef judged = run;
            string[] current = FullTasLabels(run);
            if (goals != null && !goals.SequenceEqual(current))
            {
                string refused = goalsFrom == null || !goalsFrom.Any(l => l.SequenceEqual(current))
                    ? "its goals (" + goals.Count + ") come from another goal list than " + run.name + "'s " + current.Length + " goals"
                    : FullTasGoal.EditRefused(current, goals);
                if (refused != null)
                {
                    string text = "Full-TAS " + run.name + ": route not judged: " + refused;
                    SetFullTasResult(text, new { kind = "judge", text, run = run.name, error = true, finished = false, saved = false, reason = refused });
                    return new FullTasVerdict { finished = false, reason = refused, splits = new List<FullTasSplit>() };
                }
                judged = JsonConvert.DeserializeObject<FullTasRunDef>(JsonConvert.SerializeObject(run));
                judged.goals = goals.ToList();
                ResolveFullTasGoals(judged);
            }
            bool finished = EvaluateFullTas(judged, inputs, out float time, out int finishTick, out FullTasTracker tracker);
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
                _solverFullTas = judged;
                try { verdict.saved = SaveSolverRoute(route, time); }
                finally { _solverFullTas = null; }
                if (verdict.saved)
                {
                    StoreFullTasSplits(_library.macros.LastOrDefault(m => m.name == name), verdict.splits);
                    verdict.reason = "saved as " + name;
                    if (judged != run) verdict.reason += "; " + AdoptFullTasGoals(run, judged.goals);
                }
                else verdict.reason = _solverLog[_solverLog.Count - 1];
            }
            string line = "Full-TAS " + run.name + ": " + (finished ? time.ToString("0.00", CultureInfo.InvariantCulture) + "s live in turbo (" + runTicks + " frames)" : "no finish in turbo")
                + ", " + verdict.reason;
            SetFullTasResult(line, new { kind = "judge", text = line, run = run.name, error = !finished, finished, time = finished ? time : (float?)null, saved = verdict.saved,
                reason = verdict.reason, reached = tracker.Next, of = tracker.Goals.Length, next = tracker.Current?.label, splits = verdict.splits });
            return verdict;
        }

        /// <summary>
        /// A record of other goals was saved: they become the run's in fulltas.json (kept first as
        /// fulltas.json.&lt;yyyyMMdd-HHmmss&gt;.bak beside it), which retires the routes of the old goals as a goals edit does; the
        /// next Improve captures the start again with them.
        /// </summary>
        string AdoptFullTasGoals(FullTasRunDef run, List<string> goals)
        {
            int was = run.goals.Count;
            string backup = FullTasPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bak";
            if (File.Exists(FullTasPath)) File.Copy(FullTasPath, backup, true);
            FullTasRunDef adopted = SetFullTasGoals(run.name, goals, null);
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
