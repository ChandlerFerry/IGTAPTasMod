using System;
using System.Collections.Generic;
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
            float momentumX = ((Vector2)MovMomentumField.GetValue(movement)).x;
            if (_brakeTicks == 1) _brakeAxis = Mathf.Abs(momentumX) > 1f ? -Mathf.Sign(momentumX) : 0f;
            if (momentumX * _brakeAxis >= -1f) _brakeAxis = 0f;
            if (_brakeAxis == 0f && (bool)MovOnGroundField.GetValue(movement)
                && Mathf.Abs(movement.GetComponent<Rigidbody2D>().linearVelocity.x) <= 1f)
            {
                _brakeTicks = -1;
                return;
            }
            movement.MoveAxis = new Vector2(_brakeAxis, 0f);
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
            // Unity's cap: the fixed clock's frames aren't 1/fps of game time (six make a 0.02 s tick, 0.933x at 280 fps) and
            // it never makes up a late frame. Playback waits for the fixed clock's own time (WaitForFixedClock): a walk for
            // slowdown times it, fast playback (settings fastPlayback) not at all. A probe that isn't walked keeps Unity's
            // cap: its fps is the experiment.
            bool probeCap = _probe != null && !_walkScaled;
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
            if (gameTime == _pacedTime) _engine.CountHeldFrames(1);
            _pacedTime = gameTime;
            bool skipping = _fastUntilTick > 0 && (_pendingReplay != null || _engine.RunTicks < _fastUntilTick);
            if (!probeCap) WaitForFixedClock(_walkScaled ? _walkSlowdown : _settings.fastPlayback || skipping ? 0f : 1f);
            _clockNext = FixedClockStep(_pacedFps);
            Time.captureDeltaTime = _clockNext;
        }

        /// <summary>
        /// Holds this frame until the wall clock reaches the fixed clock's time since the playback started (its frames' steps
        /// summed, a freeze's frames included, times <paramref name="scale"/>), so 1x plays in real time. The frames after a
        /// late one catch up, up to <see cref="MaxPaceDebt"/> behind; a longer stall (a load) is let go, not fast-forwarded.
        /// Scale 0: no wait.
        /// </summary>
        void WaitForFixedClock(float scale)
        {
            double now = _paceClock.Elapsed.TotalSeconds;
            _paceDue = scale > 0f ? Math.Max(_paceDue + _clockFrame * scale, now - MaxPaceDebt) : now;
            // Sleep only while a coarse (15.6 ms) timer tick can't overshoot, then yield.
            for (double left; (left = _paceDue - now) > 0; now = _paceClock.Elapsed.TotalSeconds)
                if (left > 0.016) System.Threading.Thread.Sleep(1); else System.Threading.Thread.Yield();
        }

        /// <summary>
        /// At a playback's start: the camera eased on the wall clock until now, so it starts settled on the player.
        /// </summary>
        void SnapCameraAtPlaybackStart(Movement movement)
        {
            if (movement != null) SavestateService.SnapCamera(movement.transform.position);
        }

        /// <summary>
        /// The next frame's game-time step (<c>Time.captureDeltaTime</c>) for a fixed clock: frames fall on a grid anchored to
        /// the last physics tick, half a frame off it (offset + k/fps), so every tick sees the same frame times whatever the
        /// wall clock does and no frame lands on a tick boundary. With interpolation on, the render position a FixedUpdate
        /// write starts from (the last frame's blend) is then the same every run. Re-anchored every frame, so the float
        /// step's rounding never accumulates. The step goes to the grid point after the one nearest now: a frame that lands a
        /// hair short of its grid point (the float step's rounding) would otherwise get a near-zero next step, an extra
        /// Movement.Update in that tick.
        /// </summary>
        static float FixedClockStep(int fps)
        {
            double frame = 1.0 / fps, offset = frame / 2, tick = Time.fixedDeltaTime;
            double since = Time.timeAsDouble - Time.fixedTimeAsDouble;
            double next = offset + (Math.Floor((since - offset) / frame + 0.5) + 1) * frame;
            if (next >= tick) next = tick + offset;
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
            _pendingReplayStartDelayTicks = Mathf.Max(0, startDelayTicks);
        }

        private void OnAttemptEnded(bool success, string reason)
        {
            _fastUntilTick = 0;
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
