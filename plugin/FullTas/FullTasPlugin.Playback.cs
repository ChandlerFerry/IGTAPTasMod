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
        void StartFullTasPlayback(FullTasRunDef run, MacroDefinition route, int? expectedGoals = null, int fromTick = 0)
        {
            if (PlaybackActive || _recorder.IsRecording) throw new InvalidOperationException("Stop playback or recording first.");
            if (_solverRunning) throw new InvalidOperationException("Stop the solver first.");
            if (_fullTasPending != null) throw new InvalidOperationException("Busy: " + _fullTasPending.what + ".");
            if (route == null) throw new InvalidOperationException("Full-TAS run " + run.name + " has no route yet.");
            var tracker = NewFullTasTracker(run);
            StartNewGame(null, run.Vman, run.savestate, run.RebaseAt, run.tree);
            EnsureGameUnpaused();
            FullTasOffline offline = FullTasOfflineBest(run.name, tracker.Goals.Select(g => g.label).ToList());
            int expected = expectedGoals ?? (IsTechDemo(route) || route.id == ProbeRouteId ? 0 : FullTasSavedRoute(run.name, route) ? tracker.Goals.Length : offline?.reached ?? 0);
            TechDef tech = _techs.Find(t => t.routeId == route.id);
            _fullTasPlay = new FullTasPlay { run = run, route = route, tracker = tracker, expected = expected, fastForward = fromTick > 0 ? fromTick : tech?.fastForward ?? 0, fastForwardFrames = fromTick > 0,
                idles = offline?.idles != null && offline.idles.Count > 0 && SameInputs(route, offline.inputs) ? offline.idles : null };
        }

        static bool SameInputs(MacroDefinition route, List<SolverTick> inputs)
        {
            List<SolverTick> ticks = ToTicks(route.steps), own = ToTicks(ToSteps(inputs));
            return ticks.Count >= own.Count && JsonConvert.SerializeObject(ticks.GetRange(0, own.Count)) == JsonConvert.SerializeObject(own);
        }

        void BeginFullTasPlayback(Movement movement)
        {
            FullTasPlay play = _fullTasPlay;
            play.started = true;
            UnityEngine.Random.InitState(FullTasSeed);
            SetGameRewardTimers(false);
            _brakeTicks = -1;
            play.tracker.Reset();
            GuardSavestateRestarts(play.run, play.tracker, movement);
            _fullTas = play.tracker;
            _fullTasUnlocks = _newGameUnlocks;
            bool complete = play.expected == play.tracker.Goals.Length;
            SnapCameraAtPlaybackStart(movement);
            _engine.Run(play.route, false, complete && !IsTechDemo(play.route) && play.route.id != ProbeRouteId, 0, null);
            if (!_engine.IsRunning) { FinishFullTasPlayback(false, _engine.Status); return; }
            if (_engine.TakeQuickRestart()) QuickRestart(movement);
        }

        void PumpFastForward()
        {
            FullTasPlay play = _fullTasPlay;
            if (play == null || !play.started || play.fastForward <= 0 || !_engine.IsRunning || TurboRunner.Active) return;
            int until = play.fastForward;
            play.fastForward = 0;
            Movement player = FindWorldMovement();
            bool frames = play.fastForwardFrames;
            // Turbo models a dash freeze itself: drop real IE_TimeStop so it can't freeze 1x again after a seek.
            TurboRunner.DropTimeStops = true;
            try
            {
                TurboRunner.Run(player, () => _engine.IsRunning && (frames ? _engine.PhysicsTicks : _engine.RunTicks) < until - 1, until * 4 + 1000, () =>
                {
                    _engine.Clock++;
                    _engineSimTickHook?.Invoke(player);
                });
            }
            finally { TurboRunner.DropTimeStops = false; }
            TurboRunner.ShowLastFrame(player);
            Logger.LogInfo("Fast-forwarded " + play.route.name + " to " + (frames ? "frame " + _engine.PhysicsTicks : "input tick " + _engine.RunTicks) + " in turbo.");
        }

        void FinishFullTasPlayback(bool success, string reason)
        {
            FullTasPlay play = _fullTasPlay;
            _fullTasPlay = null;
            _fullTas = null;
            SetGameRewardTimers(true);
            FullTasTracker tracker = play.tracker;
            bool finished = success && reason == MacroEngine.FullTasFinishReason;
            float time = finished ? _engine.AuthoritativeCompletionTime : MacroEngine.LiveSeconds(_engine.PhysicsTicks, _engine.HeldFrames);
            List<FullTasSplit> splits = FullTasSplits(tracker);
            string splitText = string.Join(" + ", splits.Select(s => s.label + " " + s.Live.ToString("0.00", CultureInfo.InvariantCulture)));
            FullTasGoal next = tracker.Current;
            string line = "Full-TAS " + play.run.name + (finished ? " finished in " + time.ToString("0.00", CultureInfo.InvariantCulture) + "s live (" + _engine.PhysicsTicks + " frames)"
                : " stopped after " + tracker.Next + " of " + tracker.Goals.Length + " goals" + (next == null ? "" : " (next " + next.label + ")") + ": " + tracker.Explain(reason))
                + DroppedFramesText() + (splitText.Length > 0 ? " [" + splitText + "]" : "");
            SetFullTasResult(line, new { kind = "play", text = line, run = play.run.name, error = !finished && tracker.Next < play.expected, finished, time,
                reached = tracker.Next, of = tracker.Goals.Length, expected = play.expected, next = next?.label, reason = finished ? null : reason,
                droppedFrames = _droppedFrames, splits });
            if (!finished && _droppedFrames == 0 && play.expected == tracker.Goals.Length)
                RetireBrokenFullTasRoute(play.route, "1x play", reason, tracker);
            if (finished && FullTasSavedRoute(play.run.name, play.route) && !_fullTasSplits.ContainsKey(play.route.id)
                && FullTasRouteOf(play.route.name, out _, out _, out float named) && Mathf.Abs(named - time) <= .0001f)
                StoreFullTasSplits(play.route, splits);
            FullTasQueuePlayed(play, finished, time, tracker.Next, reason);
        }

        bool FullTasSavedRoute(string run, MacroDefinition route)
        {
            return FullTasRouteOf(route.name, out string r, out _, out _) && string.Equals(r, run, StringComparison.OrdinalIgnoreCase) && _library.macros.Contains(route);
        }

        void RefreshFullTasRewards()
        {
            if (_fullTas == null) return;
            foreach (courseScript c in FindObjectsByType<courseScript>(FindObjectsSortMode.None))
                if (c.gameObject.activeInHierarchy && c.localUpgradesScript != null) c.UpdateReward();
        }

        // The game's 5 s UpdateReward timers are off at 1x in a Full-TAS run; MacroEngine refreshes on a fixed phase (exc. 10).
        static void SetGameRewardTimers(bool on)
        {
            foreach (courseScript c in FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                c.CancelInvoke("UpdateReward");
                if (on) c.InvokeRepeating("UpdateReward", MacroEngine.RewardRefreshTicks * Time.fixedDeltaTime, MacroEngine.RewardRefreshTicks * Time.fixedDeltaTime);
            }
        }

        void DropPendingFullTasPlayback(string reason)
        {
            if (_fullTasPlay == null || _fullTasPlay.started) return;
            SetFullTasResult("Full-TAS " + _fullTasPlay.run.name + " did not start: " + reason, null, true);
            _fullTasPlay = null;
        }
    }
}
