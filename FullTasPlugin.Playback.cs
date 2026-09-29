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
        void StartFullTasPlayback(FullTasRunDef run, MacroDefinition route, int? expectedGoals = null)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            if (_fullTasPending != null) throw new InvalidOperationException("Busy: " + _fullTasPending.what + ".");
            if (route == null) throw new InvalidOperationException("Full-TAS run " + run.name + " has no route yet.");
            var tracker = NewFullTasTracker(run);
            StartNewGame(null, run.Vman, run.savestate, run.RebaseAt);
            EnsureGameUnpaused();
            FullTasOffline offline = FullTasOfflineBest(run.name, tracker.Goals.Select(g => g.label).ToList());
            int expected = expectedGoals ?? (IsTechDemo(route) || route.id == ProbeRouteId ? 0 : FullTasBestRoute(run.name) == route ? tracker.Goals.Length : offline?.reached ?? 0);
            TechDef tech = _techs.Find(t => t.routeId == route.id);
            _fullTasPlay = new FullTasPlay { run = run, route = route, tracker = tracker, expected = expected, fastForward = tech?.fastForward ?? 0 };
        }

        void BeginFullTasPlayback(Movement movement)
        {
            FullTasPlay play = _fullTasPlay;
            play.started = true;
            UnityEngine.Random.InitState(FullTasSeed);
            _brakeTicks = -1;
            play.tracker.Reset();
            GuardSavestateRestarts(play.run, play.tracker, movement);
            _fullTas = play.tracker;
            _fullTasUnlocks = _newGameUnlocks;
            // Only a complete route waits for its last goal after its inputs; a partial one (the offline best so far, Test best's
            // attempt) ends with its inputs, instead of idling on until the player falls, dies and respawns at the world start.
            bool complete = play.expected == play.tracker.Goals.Length;
            SnapCameraAtPlaybackStart(movement);
            _engine.Run(play.route, false, complete && !IsTechDemo(play.route) && play.route.id != ProbeRouteId, 0, null);
            if (!_engine.IsRunning) { FinishFullTasPlayback(false, _engine.Status); return; }
            if (_engine.TakeQuickRestart()) QuickRestart(movement);
        }

        /// <summary>
        /// A demo's fast-forward (plugin Update, outside the physics loop): its first input ticks in turbo, the way the gate replays
        /// a run, so it starts at 1x where the tech begins, in the world the run made (boxes, unlocks, economy, FloatingOrigin
        /// rebases), which a savestate can't hold.
        /// </summary>
        void PumpFastForward()
        {
            FullTasPlay play = _fullTasPlay;
            if (play == null || !play.started || play.fastForward <= 0 || !_engine.IsRunning || TurboRunner.Active) return;
            int until = play.fastForward;
            play.fastForward = 0;
            Movement player = FindWorldMovement();
            TurboRunner.Run(player, () => _engine.IsRunning && _engine.RunTicks < until, until * 4 + 1000, () =>
            {
                _engine.Clock++;
                _engineSimTickHook?.Invoke(player);
            });
            Logger.LogInfo("Fast-forwarded " + play.route.name + " to input tick " + _engine.RunTicks + " in turbo.");
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
    }
}
