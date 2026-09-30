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
            StartNewGame(null, run.Vman, run.savestate, run.RebaseAt);
            EnsureGameUnpaused();
            FullTasOffline offline = FullTasOfflineBest(run.name, tracker.Goals.Select(g => g.label).ToList());
            int expected = expectedGoals ?? (IsTechDemo(route) || route.id == ProbeRouteId ? 0 : FullTasBestRoute(run.name) == route ? tracker.Goals.Length : offline?.reached ?? 0);
            TechDef tech = _techs.Find(t => t.routeId == route.id);
            _fullTasPlay = new FullTasPlay { run = run, route = route, tracker = tracker, expected = expected, fastForward = fromTick > 0 ? fromTick : tech?.fastForward ?? 0, fastForwardFrames = fromTick > 0 };
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
            // Only a complete route waits for its last goal after its inputs; a partial one (the offline best so far, Test best's
            // attempt) ends with its inputs, instead of idling on until the player falls, dies and respawns at the world start.
            bool complete = play.expected == play.tracker.Goals.Length;
            SnapCameraAtPlaybackStart(movement);
            _engine.Run(play.route, false, complete && !IsTechDemo(play.route) && play.route.id != ProbeRouteId, 0, null);
            if (!_engine.IsRunning) { FinishFullTasPlayback(false, _engine.Status); return; }
            if (_engine.TakeQuickRestart()) QuickRestart(movement);
        }

        /// <summary>
        /// A demo's fast-forward, or a play from a tick (plugin Update, outside the physics loop): its first input ticks in turbo, the way the gate replays
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
            bool frames = play.fastForwardFrames;
            TurboRunner.Run(player, () => _engine.IsRunning && (frames ? _engine.PhysicsTicks : _engine.RunTicks) < until, until * 4 + 1000, () =>
            {
                _engine.Clock++;
                _engineSimTickHook?.Invoke(player);
            });
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
                : " stopped after " + tracker.Next + " of " + tracker.Goals.Length + " goals" + (next == null ? "" : " (next " + next.label + ")") + ": " + reason)
                + DroppedFramesText() + (splitText.Length > 0 ? " [" + splitText + "]" : "");
            SetFullTasResult(line, new { kind = "play", text = line, run = play.run.name, error = !finished && tracker.Next < play.expected, finished, time,
                reached = tracker.Next, of = tracker.Goals.Length, expected = play.expected, next = next?.label, reason = finished ? null : reason,
                droppedFrames = _droppedFrames, splits });
            // 1x is the judge and plays a route the same every time: a saved route that dies or runs out of inputs short of its
            // goals (not a stop, not a dropped frame) is no longer a time, whatever turbo judged it.
            bool broken = !finished && _droppedFrames == 0 && (reason.StartsWith("Player death", StringComparison.Ordinal) || reason == "Timeline complete");
            if (broken && play.expected == tracker.Goals.Length && MatchCategoryRoute(play.route.name).Success && _library.macros.Contains(play.route))
            {
                FullTasLog("Retired " + play.route.name + ": its 1x play " + reason.ToLowerInvariant() + " after " + tracker.Next + " of " + tracker.Goals.Length + " goals");
                play.route.name += " stale";
                SaveLibrary();
            }
            if (finished && FullTasBestRoute(play.run.name) == play.route && !_fullTasSplits.ContainsKey(play.route.id)
                && Mathf.Abs(CategoryBest(FullTasCategory(play.run.name)) - time) <= .0001f)
                StoreFullTasSplits(play.route, splits);
        }

        /// <summary>MacroEngine.RefreshRewards in a Full-TAS run (1x and turbo): each active course with local upgrades updates its
        /// reward, the set enginesim's Economy.RefreshReward updates.</summary>
        void RefreshFullTasRewards()
        {
            if (_fullTas == null) return;
            foreach (courseScript c in FindObjectsByType<courseScript>(FindObjectsSortMode.None))
                if (c.gameObject.activeInHierarchy && c.localUpgradesScript != null) c.UpdateReward();
        }

        /// <summary>The game's own 5 s UpdateReward timers (courseScript.Start, a random phase): off while a Full-TAS run plays at 1x,
        /// which refreshes rewards on MacroEngine's fixed phase instead, and back on after it.</summary>
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
