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
                LoadNewGame(null, run.Vman, run.savestate, run.RebaseAt);
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
                LoadNewGame(null, pending.run.Vman, pending.run.savestate, pending.run.RebaseAt);
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
                    if (!Truthy(body, "closest"))
                    {
                        StartFullTasPlayback(run, FullTasPlayableRoute(run));
                        return new { ok = true };
                    }
                    FullTasClosest closest = FullTasClosestAttempt(run.name, ResolveFullTasGoals(run).Select(g => g.label).ToList())
                        ?? throw new InvalidOperationException("Full-TAS run " + run.name + " has no search attempt for its current goals yet: run Reach missing goals.");
                    StartFullTasPlayback(run, new MacroDefinition { name = "fulltas/" + run.name + " closest attempt (" + closest.reached + " of " + run.goals.Count + " goals"
                        + (closest.next == null ? "" : ", next " + closest.next) + (closest.distance == null ? "" : " " + closest.distance.Value.ToString("0.#", CultureInfo.InvariantCulture) + " away") + ")",
                        steps = ToSteps(closest.inputs) }, closest.reached);
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
                    if (_fullTasPlay != null) StopPlayback("Stopped from Web Studio");
                    return new { ok = true };
                }
                case "/api/fulltas/submit":
                {
                    FullTasRunDef run = FindFullTasRun(Text(body, "name"));
                    ResolveFullTasGoals(run);
                    if (!(body["ticks"] is JArray)) throw new InvalidOperationException("ticks: the run's inputs.");
                    List<SolverTick> inputs = body["ticks"].ToObject<List<SolverTick>>();
                    // A route of goals `fulltas --auto` adopted (its goals part) carries them and the goal lists they came from.
                    var (goals, from) = FullTasRouteGoals(body);
                    return FullTasWhenReady(request, run, "Judging a " + run.name + " route", () => FullTasJudge(run, inputs, goals, from));
                }
                case "/api/fulltas/avoid":
                {
                    if (!(body["box"] is JArray box) || box.Count != 4) throw new InvalidOperationException("box: [x0, y0, x1, y1] in game-world coordinates.");
                    float[] b = box.Select(v => (float)v).ToArray();
                    string[] runs = (body["runs"] is JArray list ? list.Select(r => (string)r) : new[] { Text(body, "name") })
                        .Select(r => FindFullTasRun(r).name).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToArray();
                    if (runs.Length == 0) throw new InvalidOperationException("runs: the Full-TAS runs the box is for.");
                    return new { ok = true, changed = EditFullTasAvoids(runs, b, Truthy(body, "remove")) };
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
                    // The run's goals as they are now: the trace holds the goals it was captured with, older after a goals edit.
                    string goalsFile = Path.Combine(EngineSimDir, "map-fulltas-" + run.name + ".goals.json");
                    File.WriteAllText(goalsFile, JsonConvert.SerializeObject(ResolveFullTasGoals(run).Select(g => g.label)));
                    return RunEngineSimMap(request, "map " + Quote(trace) + " --goals " + Quote(goalsFile) + " --out " + Quote(output), output);
                }
            }
            request.StatusCode = 404;
            return new { ok = false, error = "Unknown Full-TAS endpoint." };
        }

        /// <summary>The runs (studio static state): goals, bests, offline bests, closest attempts; they change with files and routes.</summary>
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
                return new { name = r.name, start = r.start, savestate = r.savestate, goals = labels, tokens = labels, error = error,
                    breaker = FullTasBreakerGoal(labels),
                    best = best == null ? null : new { routeId = best.id, name = best.name, time = float.Parse(g.Groups["time"].Value, CultureInfo.InvariantCulture), splits = splits },
                    offline = offline == null ? null : new { reached = offline.reached, time = offline.time, splits = offline.splits, segments = offline.segments, faster = offline.faster },
                    closest = closest == null ? null : new { reached = closest.reached, next = closest.next, distance = closest.distance, ticks = closest.inputs.Count },
                    playable = best != null || offline != null && offline.inputs.Count > 0,
                    traceReady = File.Exists(FullTasTracePath(r.name)) };
            }).ToArray();
        }

        /// <summary>The solve, the last result and the playback (studio live state).</summary>
        object BuildFullTasLiveWebState()
        {
            FullTasPlay play = _fullTasPlay;
            return new {
                solve = new { running = _fullTasProcess != null, name = _fullTasSolveRun, status = _fullTasSolveStatus, error = _fullTasSolveError,
                    command = _fullTasCommand, events = _fullTasEvents, progress = _fullTasProgress, log = _fullTasLog,
                    outlook = _fullTasOutlook, pulls = _fullTasPulls },
                lastResult = _fullTasLastResult,
                goalsOverlay = _settings.fullTasGoalsRun,
                pending = _fullTasPending?.what,
                playing = play == null ? null : new { name = play.run.name, started = play.started, reached = play.tracker.Next, of = play.tracker.Goals.Length,
                    next = play.tracker.Current?.label, droppedFrames = _droppedFrames, tick = _engine.PhysicsTicks,
                    cash = globalStats.currencyLookup != null && globalStats.currencyLookup.TryGetValue(globalStats.Currencies.Cash, out double cash) ? cash : 0.0 } };
        }
    }
}
