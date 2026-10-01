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
        static Vector2? RebaseOf(JObject header)
        {
            return header["rebaseAt"] is JArray a && a.Count == 2 ? new Vector2((float)a[0], (float)a[1]) : (Vector2?)null;
        }

        object FullTasTrace(FullTasRunDef run, List<SolverTick> inputs, string name, bool world)
        {
            FullTasGoal[] goals = ResolveFullTasGoals(run);
            Directory.CreateDirectory(EngineSimDir);
            string path = Path.Combine(EngineSimDir, name + ".json");
            Movement player = FindWorldMovement();
            TurboRunner.Snapshot start = NewGameStart(run);
            // The world as the run starts it: the scene still holds what the last turbo run left (a purchase's objects turned on).
            TurboRunner.Restore(start, player);
            if (world) EngineSimExport.WriteWorld(Path.Combine(EngineSimDir, name + ".world.json"));
            courseScript[] courses = FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            Vector2 origin = CurrentOrigin();
            bool finished;
            float time;
            int finishTick;
            FullTasTracker tracker;
            string status;
            string temporary = path + ".tmp";
            using (var writer = new StreamWriter(temporary))
            {
                var json = new EngineSimJson(writer);
                JsonTextWriter w = json.W;
                w.WriteStartObject();
                w.WritePropertyName("course"); w.WriteValue(0);
                w.WritePropertyName("capabilities"); w.WriteValue("none");
                w.WritePropertyName("checkpointRespawns"); w.WriteValue(CheckpointRespawns);
                w.WritePropertyName("fulltas"); w.WriteStartObject();
                w.WritePropertyName("name"); w.WriteValue(run.name);
                w.WritePropertyName("startSetup"); w.WriteValue(FullTasStartSetup);
                w.WritePropertyName("build"); w.WriteValue(Application.buildGUID);
                w.WritePropertyName("format"); w.WriteValue(FullTasTraceFormat);
                if (run.Vman) { w.WritePropertyName("start"); w.WriteValue(VmanStart); }
                if (run.savestate != null) { w.WritePropertyName("savestate"); w.WriteValue(run.savestate); }
                if (run.LooseBuys) { w.WritePropertyName("buys"); w.WriteValue("loose"); }
                if (run.human != null)
                {
                    w.WritePropertyName("human"); w.WriteStartObject();
                    w.WritePropertyName("major"); w.WriteValue(run.human.major);
                    w.WritePropertyName("nmg"); w.WriteValue(run.human.nmg);
                    w.WritePropertyName("grace"); w.WriteValue(run.human.grace);
                    w.WriteEndObject();
                }
                if (run.rebaseAt != null)
                {
                    w.WritePropertyName("rebaseAt"); w.WriteStartArray();
                    w.WriteValue(run.rebaseAt[0]); w.WriteValue(run.rebaseAt[1]);
                    w.WriteEndArray();
                }
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
                status = _engine.Status;
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
            // A run that ended before its first tick never played its inputs (after a goals edit it has, from a new game start
            // the engine couldn't resume): keep the trace there was, which the offline solver would otherwise load and replay to
            // nothing.
            if (inputs.Count > 1 && finishTick == 0 && tracker.Next == 0)
            {
                File.Delete(temporary);
                throw new InvalidOperationException("Trace " + name + ": the run ended before its first tick, so its inputs never played; the trace "
                    + "was not written. Load the scene again (POST /api/oracle/bootstrap) and capture again.");
            }
            ReplaceAtomic(temporary, path);
            RememberBestSplits(run, inputs, finished, time, tracker, status);
            FullTasLog("Trace " + path + ": " + (finished ? "finished in " + time.ToString("0.00", CultureInfo.InvariantCulture) + "s live (" + _engine.PhysicsTicks + " frames)"
                : "reached " + tracker.Next + " of " + goals.Length + " goals"));
            return new { ok = true, path = path, trace = path, finished = finished, time = time, finishTick = finishTick, progress = tracker.Next };
        }

        /// <summary>
        /// A trace that replayed the record's own inputs (the trace starts with them) keeps the record's splits, and puts the
        /// record's name to the time the replay took when it differs: a record saved when a Full-TAS time was its physics ticks
        /// alone (before dash freezes counted, README ### Rules 31) is re-timed here on its next trace. One whose replay dies or runs
        /// out of inputs short of its goals (a game update changed it) is retired, as a 1x play retires it.
        /// </summary>
        void RememberBestSplits(FullTasRunDef run, List<SolverTick> inputs, bool finished, float time, FullTasTracker tracker, string status)
        {
            MacroDefinition best = FullTasBestRoute(run.name);
            if (best == null || !_library.macros.Contains(best)) return;
            List<SolverTick> saved = ToTicks(best.steps);
            if (saved.Count > inputs.Count || JsonConvert.SerializeObject(saved) != JsonConvert.SerializeObject(inputs.Take(saved.Count).ToList())) return;
            if (!finished)
            {
                RetireBrokenFullTasRoute(best, "turbo replay", status, tracker);
                return;
            }
            string name = FullTasCategory(run.name) + " " + time.ToString("0.00", CultureInfo.InvariantCulture);
            if (!string.Equals(best.name, name, StringComparison.OrdinalIgnoreCase))
            {
                FullTasLog("Re-timed " + best.name + " as " + name + ": its replay took " + time.ToString("0.00", CultureInfo.InvariantCulture)
                    + " s live (" + tracker.PhysicsTicks[tracker.Next - 1] + " frames, " + tracker.HeldFrames[tracker.Next - 1] + " held)");
                best.name = name;
                PruneSlowerRoutes();
                SaveLibrary();
            }
            if (!_fullTasSplits.ContainsKey(best.id)) StoreFullTasSplits(best, FullTasSplits(tracker));
        }

        /// <summary>
        /// 1x and turbo play a route the same every time: a saved route that dies or runs out of inputs short of its goals (not a
        /// stop) is no longer a time, so it is renamed stale.
        /// </summary>
        void RetireBrokenFullTasRoute(MacroDefinition route, string how, string reason, FullTasTracker tracker)
        {
            bool broken = reason != null && (reason.StartsWith("Player death", StringComparison.Ordinal) || reason == "Timeline complete");
            if (!broken || tracker.Next >= tracker.Goals.Length || !FullTasRouteOf(route.name, out _, out _, out _) || !_library.macros.Contains(route)) return;
            FullTasLog("Retired " + route.name + ": its " + how + " " + reason.ToLowerInvariant() + " after " + tracker.Next + " of " + tracker.Goals.Length + " goals");
            route.name += " stale";
            SaveLibrary();
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

        /// <summary>The trace was captured for the run as it is now (start, goals, game build, ...); with <paramref name="weights"/>
        /// also for its human weights, which only a solve's scores read (the gate takes fulltas.json's), so an edit of them
        /// recaptures before a solve.</summary>
        bool FullTasTraceCurrent(FullTasRunDef run, string trace, bool weights = true)
        {
            if (!File.Exists(trace)) return false;
            try
            {
                JObject header;
                bool checkpoints = false;
                using (var reader = new JsonTextReader(new StreamReader(trace)))
                {
                    header = null;
                    while (reader.Read())
                    {
                        if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1) continue;
                        if ((string)reader.Value == "checkpointRespawns") checkpoints = reader.ReadAsBoolean() == true;
                        else if ((string)reader.Value == "fulltas")
                        {
                            reader.Read();
                            header = JObject.Load(reader);
                            break;
                        }
                    }
                }
                if (header == null) return false;
                string[] labels = header["goals"].Select(g => (string)g["label"]).ToArray();
                FullTasHumanWeights human = header["human"]?.ToObject<FullTasHumanWeights>() ?? new FullTasHumanWeights();
                return checkpoints == CheckpointRespawns && (int?)header["startSetup"] == FullTasStartSetup && (int?)header["format"] == FullTasTraceFormat
                    && (string)header["start"] == run.start && (string)header["savestate"] == run.savestate
                    && ((string)header["buys"] == "loose") == run.LooseBuys && (!weights || human.Same(run.Human))
                    && RebaseOf(header) == run.RebaseAt && (string)header["build"] == Application.buildGUID
                    && labels.SequenceEqual(ResolveFullTasGoals(run).Select(g => g.label));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Full-TAS trace " + trace + " unreadable: " + ex.Message);
                return false;
            }
        }
    }
}
