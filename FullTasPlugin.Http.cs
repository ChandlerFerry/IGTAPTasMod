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
            // A Full-TAS run's time is live time: its ticks and the frames it stood still in (README ### Rules 31).
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
                    if (body["class"] != null)
                    {
                        string cls = FullTasClassOf(Text(body, "class"));
                        MacroDefinition best = FullTasClassBest(run.name, cls, out _)
                            ?? throw new InvalidOperationException("Full-TAS run " + run.name + " has no " + cls + " route yet.");
                        StartFullTasPlayback(run, best, null, Number(body, "fromTick", 0));
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
                    // A route of goals `fulltas --auto` adopted (its goals part) carries them and the goal lists they came from.
                    var (goals, from) = FullTasRouteGoals(body);
                    return FullTasWhenReady(request, run, "Judging a " + run.name + (cls == MajorClass ? "" : " " + cls) + " route", () => FullTasJudge(run, inputs, goals, from, cls));
                }
                case "/api/fulltas/avoid":
                {
                    if (!(body["box"] is JArray box) || box.Count != 4) throw new InvalidOperationException("box: [x0, y0, x1, y1] in game-world coordinates.");
                    float[] b = box.Select(v => (float)v).ToArray();
                    List<FullTasRunDef> defs = (body["runs"] is JArray list ? list.Select(r => (string)r) : new[] { Text(body, "name") })
                        .Select(FindFullTasRun).GroupBy(r => r.name).Select(g => g.First()).ToList();
                    string[] runs = defs.Select(r => r.name).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                    if (runs.Length == 0) throw new InvalidOperationException("runs: the Full-TAS runs the box is for.");
                    // A goal label, or its number as the studio shows it (from 0): the first run's label there.
                    string Goal(string key)
                    {
                        string goal = Text(body, key).Trim();
                        if (int.TryParse(goal, out int number))
                            goal = number >= 0 && number < defs[0].goals.Count ? defs[0].goals[number]
                                : throw new InvalidOperationException($"{key}: {defs[0].name} has goals 0 to {defs[0].goals.Count - 1}, not {number}.");
                        if (goal.Length > 0 && defs.FirstOrDefault(r => !r.goals.Contains(goal)) is FullTasRunDef missing)
                            throw new InvalidOperationException($"{key}: {missing.name} has no goal {goal}.");
                        return goal.Length > 0 ? goal : null;
                    }
                    // "goal": the box cuts only while a run heads for that goal (forced-paths.json's block "for"); "after": once a run
                    // has reached it (the block's "after", which the engine finds at the label's last place in the run).
                    string forGoal = Goal("goal"), after = Goal("after");
                    if (forGoal != null && after != null) throw new InvalidOperationException("goal or after, not both.");
                    if (after != null && int.TryParse(Text(body, "after").Trim(), out int at) && defs[0].goals.LastIndexOf(after) != at)
                        throw new InvalidOperationException($"after: goal {at}'s label {after} comes again at goal {defs[0].goals.LastIndexOf(after)}, where the box would start instead.");
                    // "from": the sides ("left+bottom") the box cuts a move into it across (the block's "from"); none: any tick inside.
                    string from = Text(body, "from").Trim();
                    if (from.Split('+').Any(s => s.Length > 0 && !FullTasGoal.SideNames.Contains(s)))
                        throw new InvalidOperationException($"from: sides {string.Join(", ", FullTasGoal.SideNames)} joined with +, not {from}.");
                    return new { ok = true, changed = EditFullTasAvoids(runs, b, Truthy(body, "remove"), forGoal, after, from.Length > 0 ? from : null) };
                }
                case "/api/fulltas/move-goal":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    int index = Number(body, "index", -1);
                    string label = Text(body, "label");
                    if (index < 0 || index >= run.goals.Count) throw new InvalidOperationException("index: goal " + (index + 1) + " is not one of " + run.name + "'s " + run.goals.Count + " goals.");
                    if (run.goals[index] != label)
                        throw new InvalidOperationException("Goal " + (index + 1) + " of " + run.name + " is " + run.goals[index] + " now, not " + label + ": redraw the map.");
                    string[] parts = label.Substring(3).Split(',');
                    if (!label.StartsWith("at:", StringComparison.Ordinal) || parts.Length < 4) throw new InvalidOperationException("Goal " + (index + 1) + " (" + label + ") is not an area goal.");
                    parts[0] = Math.Round(FloatNumber(body, "x", 0f), 1).ToString(CultureInfo.InvariantCulture);
                    parts[1] = Math.Round(FloatNumber(body, "y", 0f), 1).ToString(CultureInfo.InvariantCulture);
                    var goals = run.goals.ToList();
                    goals[index] = "at:" + string.Join(",", parts);
                    FullTasRunDef moved = SetFullTasGoals(run.name, goals, null);
                    FullTasLog("Moved " + run.name + " goal " + (index + 1) + " from " + label + " to " + moved.goals[index]);
                    return new { ok = true, index, from = label, to = moved.goals[index] };
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
                    string output = Path.Combine(EngineSimDir, "map-fulltas-" + run.name + ".json");
                    if (MapInFlight(output, body)) return RunEngineSimMap(request, null, output, true);
                    // The run's goals as they are now: the trace holds the goals it was captured with, older after a goals edit.
                    string goalsFile = Path.Combine(EngineSimDir, "map-fulltas-" + run.name + ".goals.json");
                    File.WriteAllText(goalsFile, JsonConvert.SerializeObject(ResolveFullTasGoals(run).Select(g => g.label)));
                    return RunEngineSimMap(request, "map " + Quote(trace) + " --goals " + Quote(goalsFile) + " --out " + Quote(output), output, true);
                }
            }
            request.StatusCode = 404;
            return new { ok = false, error = "Unknown Full-TAS endpoint." };
        }

        /// <summary>The runs (studio static state): goals, bests (the record and each class's), offline bests, closest attempts,
        /// human candidates; they change with files and routes.</summary>
        object[] BuildFullTasRunsWebState()
        {
            return _fullTasRuns.Select(r =>
            {
                string error = null;
                string[] labels;
                try { labels = ResolveFullTasGoals(r).Select(goal => goal.label).ToArray(); }
                catch (Exception ex) { error = ex.Message; labels = (r.goals ?? new List<string>()).ToArray(); }
                MacroDefinition best = FullTasBestRoute(r.name);
                Match g = best == null ? null : MatchCategoryRoute(best.name);
                List<FullTasSplit> splits;
                if (best == null || !_fullTasSplits.TryGetValue(best.id, out splits)) splits = new List<FullTasSplit>();
                splits = splits.TakeWhile((s, i) => i < labels.Length && s.label == labels[i]).ToList();
                FullTasOffline offline = FullTasOfflineBest(r.name, labels);
                FullTasClosest closest = FullTasClosestAttempt(r.name, labels);
                return new { name = r.name, start = r.start, savestate = r.savestate, section = r.Section, goals = labels, error = error,
                    breaker = FullTasBreakerGoal(labels),
                    best = best == null ? null : new { routeId = best.id, name = best.name, time = float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture), splits = splits },
                    offline = offline == null ? null : new { reached = offline.reached, time = offline.time, splits = offline.splits, segments = offline.segments, idles = offline.idles },
                    closest = closest == null ? null : new { reached = closest.reached, next = closest.next, distance = closest.distance, ticks = closest.inputs.Count },
                    classes = FullTasClassesWebState(r), human = r.Human, candidates = FullTasCandidatesWebState(r, labels),
                    playable = best != null || offline != null && offline.inputs.Count > 0,
                    traceReady = File.Exists(FullTasTracePath(r.name)) };
            }).ToArray();
        }

        /// <summary>The solve, the last result, the playback, the candidate queue and the difficulty batch (studio live state).</summary>
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
                    next = play.tracker.Current?.label, droppedFrames = _droppedFrames, tick = _engine.PhysicsTicks, idles = play.idles?.Count ?? 0, idle = CurrentIdle(play)?.goal,
                    cash = globalStats.currencyLookup != null && globalStats.currencyLookup.TryGetValue(globalStats.Currencies.Cash, out double cash) ? cash : 0.0 } };
        }

        /// <summary>
        /// A solve list (events, log) as the raw JSON the serialiser would write for it, serialised again only when its
        /// <paramref name="version"/> moved: the live state is checked every 33 ms and the lists stay full after a solve.
        /// </summary>
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
