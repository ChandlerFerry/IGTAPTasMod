using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        /// <summary>This frame's unscaled length: the fixed clock's step while it runs, else the wall clock's.</summary>
        static float FixedFrameStep()
        {
            return Instance != null && Instance._playbackPaced ? Instance._clockFrame : Time.unscaledDeltaTime;
        }

        /// <summary>Unscaled time: the fixed clock's frames summed from its start while it runs, else the wall clock's.</summary>
        static float FixedUnscaledTime()
        {
            return Instance != null && Instance._playbackPaced ? (float)Instance._clockTotal : Time.unscaledTime;
        }

        private void Brake(Movement movement)
        {
            if (_engine.IsRunning || ++_brakeTicks > MaxBrakeTicks) { _brakeTicks = -1; return; }
            float momentumX = movement.momentum.x;
            if (_brakeTicks == 1) _brakeAxis = Mathf.Abs(momentumX) > 1f ? -Mathf.Sign(momentumX) : 0f;
            if (momentumX * _brakeAxis >= -1f) _brakeAxis = 0f;
            if (_brakeAxis == 0f && movement.onGround
                && Mathf.Abs(movement.GetComponent<Rigidbody2D>().linearVelocity.x) <= 1f)
            {
                _brakeTicks = -1;
                return;
            }
            movement.MoveAxis = new Vector2(_brakeAxis, 0f);
        }

        internal static long GridTicks { get { return Instance != null ? Instance._engine.GridTicks : 0; } }

        /// <summary>Ends the route a turbo run plays (TurboRunner: what it can't model), logged.</summary>
        internal static void AbortRun(string reason)
        {
            Log?.LogWarning("Turbo: " + reason);
            if (Instance != null && Instance._engine.IsRunning) Instance._engine.Abort(reason);
        }

        /// <summary>Turbo's frames the run stood still in (TurboRunner's frame loop), onto the engine's count.</summary>
        internal static void CountHeldFrames(int frames)
        {
            if (Instance != null) Instance._engine.CountHeldFrames(frames);
        }

        void PacePlayback(bool on)
        {
            if (on != _playbackPaced)
            {
                _playbackPaced = on;
                if (on)
                {
                    _savedMaximumDeltaTime = Time.maximumDeltaTime;
                    _savedVSyncCount = QualitySettings.vSyncCount;
                    _savedTargetFrameRate = Application.targetFrameRate;
                    Time.maximumDeltaTime = Time.fixedDeltaTime;
                    _pacedFps = _probe != null && _probe.fps > 0 ? _probe.fps : MacroEngine.PlaybackFrameRate;
                    _pacedGrid = _pacedFps == MacroEngine.PlaybackFrameRate ? MacroEngine.FrameGrid.Playback : new MacroEngine.FrameGrid(_pacedFps);
                    _clockNext = 1f / _pacedFps;
                    _clockTotal = 0.0;
                    _paceClock.Restart();
                    _paceDue = 0.0;
                    _pacedTime = double.NaN;
                }
                else
                {
                    Time.maximumDeltaTime = _savedMaximumDeltaTime;
                    QualitySettings.vSyncCount = _savedVSyncCount;
                    Application.targetFrameRate = _savedTargetFrameRate;
                    Time.captureDeltaTime = 0f;
                }
            }
            if (!_playbackPaced) return;
            // SettingsScript sets the player's own frame rate and vsync when a scene loads (a Full-TAS new game): hold ours.
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            // Only the wall clock is paced here: every frame's game time is the fixed step below (captureDeltaTime). Not by
            // Unity's cap: off a run and on its first tick the grid's frames aren't 1/fps of game time (six make a 0.02 s
            // tick). Playback waits for the fixed clock's own time (WaitForFixedClock): a walk for slowdown times it, fast
            // playback (settings fastPlayback, or a probe's own fast) not at all. A probe at another frame rate that isn't walked
            // keeps Unity's cap: its fps is the experiment.
            bool probeFast = _probe != null && _pacedFps == MacroEngine.PlaybackFrameRate && (_probe.fast || _settings.fastPlayback);
            bool probeCap = _probe != null && !_walkScaled && !probeFast;
            int targetFrameRate = probeCap ? _pacedFps : -1;
            if (Application.targetFrameRate != targetFrameRate) Application.targetFrameRate = targetFrameRate;
            // This frame ran on the step set last frame; the wall-clock readers (PatchWallClock) see it and its running total.
            _clockFrame = _clockNext;
            _clockTotal += _clockFrame;
            // A frame whose game time stood still (a dash freeze, the pause menu) is one the run stands still in: its live time
            // (MacroEngine.HeldFrames), the frames turbo and the offline engine repeat (MacroEngine.FrameRepeats). Not
            // Time.timeScale == 0: the freeze starts in the FixedUpdate of the frame that crosses the tick, so that frame's Update
            // runs at timeScale 0 though its time moved (the step that ran the tick): one frame too many per dash freeze.
            double gameTime = Time.timeAsDouble;
            bool held = gameTime == _pacedTime;
            if (held) _engine.CountHeldFrames(1);
            _pacedTime = gameTime;
            bool skipping = _fastUntilTick > 0 && (_pendingReplay != null || _engine.RunTicks < _fastUntilTick);
            long ticks = _engine.GridTicks;
            if (!probeCap) WaitForFixedClock(_walkScaled ? _walkSlowdown : _settings.fastPlayback || probeFast || skipping ? 0f : 1f,
                held || ticks >= 2 ? _clockFrame : EvenFrameStep(_pacedFps));
            _clockNext = FixedClockStep(_pacedGrid, ticks);
            Time.captureDeltaTime = _clockNext;
        }

        /// <summary>
        /// Holds this frame until <paramref name="wallStep"/> times <paramref name="scale"/> after the last one was due, so 1x
        /// plays at most in real time. A late frame is never made up: playback never speeds up, it plays on that much later.
        /// Scale 0: no wait.
        /// </summary>
        void WaitForFixedClock(float scale, double wallStep)
        {
            double now = _paceClock.Elapsed.TotalSeconds, start = now;
            _paceDue = scale > 0f ? Math.Max(_paceDue + wallStep * scale, now) : now;
            // Sleep only while a coarse (15.6 ms) timer tick can't overshoot, then yield.
            for (double left; (left = _paceDue - now) > 0; now = _paceClock.Elapsed.TotalSeconds)
                if (left > 0.016) System.Threading.Thread.Sleep(1); else System.Threading.Thread.Yield();
            _perfNow.wait += (float)((now - start) * 1000);
        }

        /// <summary>
        /// At a playback's start: the camera eased on the wall clock until now, so it starts settled on the player.
        /// </summary>
        void SnapCameraAtPlaybackStart(Movement movement)
        {
            if (movement != null) SavestateService.SnapCamera(movement.transform.position);
        }

        /// <summary>
        /// The wall time a frame that moved game time is shown for where the grid starts again at each tick (off a run, its
        /// first tick): its tick's time split evenly over the tick's frames (six at 280 fps, 3.33 ms each), not its own step.
        /// The crossing frame there steps only 2.14 ms, less than a frame takes to render, so pacing by the step lost time on
        /// every tick. On the run's cycle every frame steps 1/fps and is shown for its step.
        /// </summary>
        static double EvenFrameStep(int fps)
        {
            double frame = 1.0 / fps, tick = Time.fixedDeltaTime;
            return tick / (Math.Floor((tick - frame / 2) / frame) + 1);
        }

        /// <summary>
        /// The next frame's game-time step (<c>Time.captureDeltaTime</c>) for a fixed clock: the frame after this one on
        /// <paramref name="grid"/> (MacroEngine.FrameGrid), whose frames are by the run's physics ticks, so every run sees the
        /// same frame times whatever the wall clock does and no frame lands on a tick boundary. With interpolation on, the
        /// render position a FixedUpdate write starts from (the last frame's blend) is then the same every run. Re-anchored
        /// to the last tick every frame, so the float step's rounding never accumulates. The step goes to the grid point after
        /// the one nearest now: a frame that lands a hair short of its grid point (the float step's rounding) would otherwise
        /// get a near-zero next step, an extra Movement.Update in that tick.
        /// </summary>
        static float FixedClockStep(MacroEngine.FrameGrid grid, long ticks)
        {
            double frame = grid.Frame, first = grid.Frames(ticks).First, tick = Time.fixedDeltaTime;
            double since = Time.timeAsDouble - Time.fixedTimeAsDouble;
            double next = first + (Math.Floor((since - first) / frame + 0.5) + 1) * frame;
            if (next >= tick) next = tick + grid.Frames(MacroEngine.FrameGrid.NextTicks(ticks)).First;
            return (float)(next - since);
        }

        /// <summary>
        /// A walk plays 1x's frames at 1/slowdown of the paced frame rate (PacePlayback's cap). Time.timeScale stays: the
        /// fixed clock's captureDeltaTime is scaled by it, so a slowed timeScale took every frame off 1x's grid (other
        /// interpolation blends, other step-ups and FixedUpdate write origins) and the walk played another run than 1x.
        /// </summary>
        void PaceWalk()
        {
            if (!PlaybackActive && _pendingReplay == null) _walkSlowdown = 0f;
            bool on = _walkSlowdown > 0f && (_engine.IsRunning || _pendingReplay != null) && !TurboRunner.Active;
            if (on == _walkScaled) return;
            _walkScaled = on;
            _playbackSpeedText = on ? (1f / _walkSlowdown).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "x" : "1x";
        }

        private void ReplaySelected(int startDelayTicks = 0, float walkSlowdown = 0f, int fromTick = 0)
        {
            TasClose("Route playback started");
            MacroDefinition macro = TechPlayable(SelectedMacro);
            if (macro == null) return;
            _walkSlowdown = walkSlowdown;
            Match fullTasRoute = FullTasRouteName.Match(macro.name ?? "");
            if (fullTasRoute.Success)
            {
                StartFullTasPlayback(FindFullTasRun(fullTasRoute.Groups[1].Value), macro, null, fromTick);
                return;
            }
            _fastUntilTick = Mathf.Max(0, fromTick);
            EnsureGameUnpaused();
            _pendingReplay = walkSlowdown > 0f ? HumanVersionOf(macro) ?? macro : macro;
            // A farm route plays as its farm time is judged (EvaluateLoop): its lead, then its loop LoopRepeats times, through every
            // finish. Its inputs alone end at the lead's finish, which ended the playback there: one lap.
            _farmPlayback = false;
            if (_pendingReplay.loopFrom > 0)
            {
                List<SolverTick> ticks = ToTicks(_pendingReplay.steps.Where(st => st.enabled).ToList());
                if (_pendingReplay.loopFrom < ticks.Count)
                {
                    var played = ticks.Take(_pendingReplay.loopFrom).ToList();
                    for (int k = 0; k < LoopRepeats; k++) played.AddRange(ticks.Skip(_pendingReplay.loopFrom).Select(t => t.Clone()));
                    _pendingReplay = new MacroDefinition { id = _pendingReplay.id, name = _pendingReplay.name, savestateId = _pendingReplay.savestateId, steps = ToSteps(played) };
                    _farmPlayback = true;
                }
            }
            _pendingReplayStartDelayTicks = Mathf.Max(0, startDelayTicks);
        }

        private void OnAttemptEnded(bool success, string reason)
        {
            _fastUntilTick = 0;
            _farmPlayback = false;
            if (_fullTasPlay != null && _fullTasPlay.started && !_silentEvaluation)
            {
                FinishFullTasPlayback(success, reason);
                return;
            }
            // A TAS session keeps its category's unlocks until it closes: a rewind plays on from before the end.
            if (_tas != null) { TasEngineEnded(success, reason); return; }
            RestorePlaybackAbilities();
            if (_silentEvaluation)
            {
                _silentFinished = success && MacroEngine.IsFinishReason(reason);
                _silentTime = _engine.AuthoritativeCompletionTime;
                return;
            }
            if (success && reason == MacroEngine.TransitionFinishReason) _brakeTicks = 0;
        }
    }
}
