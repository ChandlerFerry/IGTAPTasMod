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
        void FullTasLog(string line)
        {
            _fullTasLog.Add(line);
            if (_fullTasLog.Count > MaxFullTasLog) _fullTasLog.RemoveRange(0, _fullTasLog.Count - MaxFullTasLog);
            _fullTasLogVersion++;
            Logger.LogInfo("Full-TAS: " + line);
        }

        void SetFullTasResult(string line, object detail = null, bool error = false)
        {
            _fullTasLastResult = detail ?? new { kind = "note", text = line, error };
            FullTasLog(line);
        }

        void FullTasEvent(bool reached)
        {
            if (reached && _fullTas.Done && _engine.IsRunning)
                _engine.CompleteFromGame(MacroEngine.FullTasFinishReason, MacroEngine.LiveSeconds(_engine.PhysicsTicks, _engine.HeldFrames));
        }

        void FullTasCourseStarted(int course)
        {
            if (_fullTas != null && _engine.IsRunning) FullTasEvent(_fullTas.CourseStarted(course, _engine.RunTicks, _engine.PhysicsTicks, _engine.HeldFrames));
        }

        void FullTasCourseFinished(int course)
        {
            if (_fullTas != null && _engine.IsRunning) FullTasEvent(_fullTas.CourseFinished(course, _engine.RunTicks, _engine.PhysicsTicks, _engine.HeldFrames));
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
                LoadNewGame(null, run.Vman, run.savestate, run.RebaseAt, run.tree);
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
                    CompleteFullTasPending(500, new { ok = false, error = pending.what + ": no fresh " + (pending.run.Vman ? "Vman speedrun" : "new game") + (pending.run.savestate == null ? "" : " from savestate " + pending.run.savestate) + " in the world." });
                    return;
                }
                LoadNewGame(null, pending.run.Vman, pending.run.savestate, pending.run.RebaseAt, pending.run.tree);
                pending.loaded = true;
                return;
            }
            if (!FullTasStartReady(pending.run)) return;
            object result;
            try { result = pending.action(); }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS: " + pending.what + ": " + ex);
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

        void PumpFullTas()
        {
            bool idle = _fullTas == null && _fullTasPending == null && !_silentEvaluation;
            long written = System.Threading.Interlocked.Exchange(ref _fullTasStat, NoFullTasStat);
            if (written != NoFullTasStat && idle && _fullTasWritten == _fullTasStatOf && written != _fullTasWritten) LoadFullTas();
            if (Time.unscaledTime >= _fullTasCheckAt && idle && System.Threading.Interlocked.CompareExchange(ref _fullTasStatBusy, 1, 0) == 0)
            {
                _fullTasCheckAt = Time.unscaledTime + 1f;
                _fullTasStatOf = _fullTasWritten;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    long stamp = NoFullTasStat;
                    try { stamp = FullTasRunsStamp(); }
                    catch (Exception) { }
                    System.Threading.Interlocked.Exchange(ref _fullTasStat, stamp);
                    System.Threading.Interlocked.Exchange(ref _fullTasStatBusy, 0);
                });
            }
            PumpFullTasPending();
            PumpFullTasSolve();
            PumpFullTasDifficulty();
            PumpFullTasQueue();
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
                    return new { ok = true, name = run.name, start = run.start, goals = run.goals.Select(g => new { g.id, label = g.goal }) };
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
                    if (body["class"] != null)
                    {
                        string cls = FullTasClassOf(Text(body, "class"));
                        MacroDefinition best = FullTasClassBest(run.name, cls, out _)
                            ?? throw new InvalidOperationException("Full-TAS run " + run.name + " has no " + cls + " route yet.");
                        StartFullTasPlayback(run, best, null, Number(body, "fromTick", 0));
                        return new { ok = true };
                    }
                    string stretchId = Text(body, "stretch");
                    if (stretchId.Length > 0)
                    {
                        int stretch = FullTasGoalAt(run, stretchId) + 1;
                        JObject s = FullTasStretchBests(run.name)[stretchId] as JObject
                            ?? throw new InvalidOperationException("No stretch best for " + run.goals[stretch - 1].goal + " (goal " + (stretch - 1) + ") of " + run.name + " yet: Improve this stretch first.");
                        List<SolverTick> route = ToTicks(FullTasPlayableRoute(run).steps);
                        int prefix = (int)s["prefixLength"];
                        if (prefix > route.Count) throw new InvalidOperationException("The run's best is shorter than the stretch's start: the stretch was found on another best.");
                        List<SolverTick> ticks = route.Take(prefix).Concat(s["inputs"].ToObject<List<SolverTick>>()).ToList();
                        long from = Math.Max(0, (long)s["startTick"] - 150);
                        StartFullTasPlayback(run, new MacroDefinition { name = "fulltas/" + run.name + " stretch to " + run.goals[stretch - 1].goal + " (" + ((long)s["bestTick"] - (long)s["startTick"]) + " ticks)",
                            steps = ToSteps(ticks) }, stretch, (int)from);
                        return new { ok = true };
                    }
                    if (!Truthy(body, "closest"))
                    {
                        StartFullTasPlayback(run, FullTasPlayableRoute(run), null, Number(body, "fromTick", 0));
                        return new { ok = true };
                    }
                    FullTasClosest closest = FullTasClosestAttempt(run.name, ResolveFullTasGoals(run).Select(g => g.label).ToList())
                        ?? throw new InvalidOperationException("Full-TAS run " + run.name + " has no search attempt for its current goals yet: run Reach missing goals.");
                    StartFullTasPlayback(run, new MacroDefinition { name = "fulltas/" + run.name + " closest attempt (" + closest.reached + " of " + run.goals.Count + " goals"
                        + (closest.next == null ? "" : ", next " + closest.next) + (closest.distance == null ? "" : " " + closest.distance.Value.ToString("0.#", CultureInfo.InvariantCulture) + " away") + ")",
                        steps = ToSteps(closest.inputs) }, closest.reached);
                    return new { ok = true };
                }
                case "/api/fulltas/queue":
                    return StartFullTasQueue(body);
                case "/api/fulltas/queue/stop":
                {
                    StopFullTasQueue();
                    if (_fullTasPlay != null) StopPlayback("Queue stopped from Web Studio");
                    return new { ok = true };
                }
                case "/api/fulltas/solve":
                    return StartFullTasSolve(request, body);
                case "/api/fulltas/stop":
                {
                    StopFullTasSolve();
                    if (_fullTasPending != null)
                    {
                        if (_fullTasProcess == null && _fullTasSolveStatus.StartsWith("Capturing", StringComparison.Ordinal)) _fullTasSolveStatus = "Stopped";
                        CompleteFullTasPending(409, new { ok = false, error = _fullTasPending.what + ": stopped." });
                    }
                    StopFullTasQueue();
                    StopFullTasDifficulty();
                    if (_fullTasPlay != null) StopPlayback("Stopped from Web Studio");
                    return new { ok = true };
                }
                case "/api/fulltas/submit":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    ResolveFullTasGoals(run);
                    if (!(body["ticks"] is JArray)) throw new InvalidOperationException("ticks: the run's inputs.");
                    List<SolverTick> inputs = body["ticks"].ToObject<List<SolverTick>>();
                    string cls = FullTasClassOf(Text(body, "class"));
                    var (goals, from) = FullTasRouteGoals(body);
                    return FullTasWhenReady(request, run, "Judging a " + run.name + (cls == MajorClass ? "" : " " + cls) + " route", () => FullTasJudge(run, inputs, goals, from, cls));
                }
                case "/api/fulltas/avoid":
                {
                    if (!(body["box"] is JArray box) || box.Count != 4) throw new InvalidOperationException("box: [x0, y0, x1, y1] in game-world coordinates.");
                    float[] b = box.Select(v => (float)v).ToArray();
                    List<FullTasRunDef> defs = (body["runs"] is JArray list ? list.Select(r => (string)r) : new[] { Text(body, "name") })
                        .Select(FindFullTasRun).GroupBy(r => r.name).Select(g => g.First()).ToList();
                    if (defs.Count == 0) throw new InvalidOperationException("runs: the Full-TAS runs the box is for.");
                    string goal = Text(body, "goal").Trim(), after = Text(body, "after").Trim();
                    if (goal.Length > 0 && after.Length > 0) throw new InvalidOperationException("goal or after, not both.");
                    if ((goal.Length > 0 || after.Length > 0) && defs.Count != 1) throw new InvalidOperationException("goal, after: one run's goal (its id).");
                    if (goal.Length > 0) FullTasGoalAt(defs[0], goal);
                    if (after.Length > 0) FullTasGoalAt(defs[0], after);
                    string from = Text(body, "from").Trim();
                    if (from.Split('+').Any(s => s.Length > 0 && !FullTasGoal.SideNames.Contains(s)))
                        throw new InvalidOperationException($"from: sides {string.Join(", ", FullTasGoal.SideNames)} joined with +, not {from}.");
                    return new { ok = true, changed = EditFullTasAvoids(defs, b, Truthy(body, "remove"), goal.Length > 0 ? goal : null, after.Length > 0 ? after : null,
                        from.Length > 0 ? from : null) };
                }
                case "/api/fulltas/move-goal":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    int index = FullTasGoalAt(run, Text(body, "goal"));
                    string label = Text(body, "label");
                    if (run.goals[index].goal != label)
                        throw new InvalidOperationException("Goal " + index + " of " + run.name + " is " + run.goals[index].goal + " now, not " + label + ": redraw the map.");
                    string[] parts = label.Substring(3).Split(',');
                    if (!label.StartsWith("at:", StringComparison.Ordinal) || parts.Length < 4) throw new InvalidOperationException("Goal " + (index + 1) + " (" + label + ") is not an area goal.");
                    parts[0] = Math.Round(FloatNumber(body, "x", 0f), 1).ToString(CultureInfo.InvariantCulture);
                    parts[1] = Math.Round(FloatNumber(body, "y", 0f), 1).ToString(CultureInfo.InvariantCulture);
                    List<string> goals = run.Labels;
                    goals[index] = "at:" + string.Join(",", parts);
                    FullTasRunDef moved = SetFullTasGoals(run.name, goals, null, run.Ids);
                    FullTasLog("Moved " + run.name + " goal " + index + " from " + label + " to " + moved.goals[index].goal);
                    return new { ok = true, goal = moved.goals[index].id, from = label, to = moved.goals[index].goal };
                }
                case "/api/fulltas/difficulty":
                    return StartFullTasDifficulty(body);
                case "/api/fulltas/map":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    string trace = FullTasTracePath(run.name);
                    if (!File.Exists(trace))
                    {
                        request.StatusCode = 404;
                        return new { ok = false, error = "No trace for " + run.name + " yet: capture its start or improve it once first." };
                    }
                    string stretch = Text(body, "stretch");
                    if (stretch.Length > 0) FullTasGoalAt(run, stretch);
                    string output = Path.Combine(EngineSimDir, "map-fulltas-" + run.name + (stretch.Length > 0 ? "-" + stretch : "") + ".json");
                    if (MapInFlight(output, body)) return RunEngineSimMap(request, null, output, true);
                    string goalsFile = Path.Combine(EngineSimDir, "map-fulltas-" + run.name + ".goals.json");
                    ResolveFullTasGoals(run);
                    File.WriteAllText(goalsFile, JsonConvert.SerializeObject(new { goals = run.goals.Select(g => new { g.id, g.goal }) }));
                    return RunEngineSimMap(request, "map " + Quote(trace) + " --goals " + Quote(goalsFile) + " --out " + Quote(output)
                        + (stretch.Length > 0 ? " --stretch " + stretch : ""), output, true);
                }
            }
            request.StatusCode = 404;
            return new { ok = false, error = "Unknown Full-TAS endpoint." };
        }

        JObject FullTasStretchBests(string run)
        {
            string file = Path.Combine(EngineSimDir, "fulltas", run + ".stretches.json");
            try { return File.Exists(file) ? JObject.Parse(File.ReadAllText(file)) : new JObject(); }
            catch (Exception ex) when (ex is IOException || ex is JsonException) { return new JObject(); }
        }

        object[] FullTasStretches(string run)
        {
            var list = new List<object>();
            foreach (JProperty p in FullTasStretchBests(run).Properties())
                if (p.Value is JObject s && s["startTick"] != null)
                {
                    long start = (long)s["startTick"];
                    list.Add(new { goal = p.Name, bestTicks = (long)s["bestTick"] - start, runTicks = (long)s["runTick"] - start });
                }
            return list.ToArray();
        }

        static int FullTasGoalAt(FullTasRunDef run, string id)
        {
            int i = run.goals.FindIndex(g => g.id == id);
            if (i < 0) throw new InvalidOperationException(run.name + " has no goal with id \"" + id + "\".");
            return i;
        }

        object[] BuildFullTasRunsWebState()
        {
            return _fullTasRuns.Select(r =>
            {
                string error = null;
                string[] labels;
                try { labels = ResolveFullTasGoals(r).Select(goal => goal.label).ToArray(); }
                catch (Exception ex) { error = ex.Message; labels = r.Labels.ToArray(); }
                MacroDefinition best = FullTasBestRoute(r.name);
                Match g = best == null ? null : MatchCategoryRoute(best.name);
                List<FullTasSplit> splits;
                if (best == null || !_fullTasSplits.TryGetValue(best.id, out splits)) splits = new List<FullTasSplit>();
                splits = splits.TakeWhile((s, i) => i < labels.Length && s.label == labels[i]).ToList();
                FullTasOffline offline = FullTasOfflineBest(r.name, labels);
                FullTasClosest closest = FullTasClosestAttempt(r.name, labels);
                int? breaker = FullTasBreakerGoal(labels);
                return new { name = r.name, start = r.start, savestate = r.savestate, section = r.Section,
                    goals = r.goals.Select((goal, i) => new { goal.id, label = i < labels.Length ? labels[i] : goal.goal }), error = error,
                    breaker = breaker == null ? null : r.goals[breaker.Value].id,
                    best = best == null ? null : new { routeId = best.id, name = best.name, time = float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture), splits = splits },
                    offline = offline == null ? null : new { reached = offline.reached, time = offline.time, splits = offline.splits, segments = offline.segments, idles = offline.idles, waits = offline.waits },
                    closest = closest == null ? null : new { reached = closest.reached, next = closest.next, distance = closest.distance, ticks = closest.inputs.Count },
                    classes = FullTasClassesWebState(r), human = r.Human, candidates = FullTasCandidatesWebState(r, labels),
                    playable = best != null || offline != null && offline.inputs.Count > 0, stretches = FullTasStretches(r.name),
                    traceReady = File.Exists(FullTasTracePath(r.name)) };
            }).ToArray();
        }

        object BuildFullTasLiveWebState()
        {
            FullTasPlay play = _fullTasPlay;
            return new {
                solve = new { running = _fullTasProcess != null, name = _fullTasSolveRun, status = _fullTasSolveStatus, error = _fullTasSolveError,
                    command = _fullTasCommand, events = FullTasListJson(_fullTasEvents, _fullTasEventsVersion, ref _fullTasEventsJson, ref _fullTasEventsJsonAt),
                    progress = _fullTasProgress, log = FullTasListJson(_fullTasLog, _fullTasLogVersion, ref _fullTasLogJson, ref _fullTasLogJsonAt),
                    outlook = _fullTasOutlook, pulls = _fullTasPulls },
                lastResult = _fullTasLastResult,
                goalsOverlay = _settings.fullTasGoalsRun,
                pending = _fullTasPending?.what,
                queue = FullTasQueueWebState(),
                difficulty = _fullTasDifficultyRun == null ? null : new { name = _fullTasDifficultyRun, running = _fullTasDifficultyProcess != null,
                    done = _fullTasDifficultyDone, of = _fullTasDifficultyOf },
                playing = play == null ? null : new { name = play.run.name, started = play.started, reached = play.tracker.Next, of = play.tracker.Goals.Length,
                    next = play.tracker.Current?.label, droppedFrames = _droppedFrames, tick = _engine.PhysicsTicks, idles = play.idles?.Count ?? 0, idle = CurrentIdle(play)?.id,
                    cash = globalStats.currencyLookup != null && globalStats.currencyLookup.TryGetValue(globalStats.Currencies.Cash, out double cash) ? cash : 0.0 } };
        }

        static JRaw FullTasListJson(object list, int version, ref string json, ref int jsonAt)
        {
            if (json == null || jsonAt != version)
            {
                json = JsonConvert.SerializeObject(list);
                jsonAt = version;
            }
            return new JRaw(json);
        }
    }
}
