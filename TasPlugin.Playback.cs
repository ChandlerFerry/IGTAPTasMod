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
            // The cap only paces the wall clock: every frame's game time is the fixed step below (captureDeltaTime), so fast
            // playback (settings fastPlayback) lifts it and plays the same frames sooner. Not for a walk (its slowdown is
            // wall-clock pacing) or a probe (its fps is the experiment).
            int targetFrameRate = _settings.fastPlayback && _walkSlowdown <= 0f && _probe == null ? -1 : _pacedFps;
            if (Application.targetFrameRate != targetFrameRate) Application.targetFrameRate = targetFrameRate;
            // This frame ran on the step set last frame; the wall-clock readers (PatchWallClock) see it and its running total.
            _clockFrame = _clockNext;
            _clockTotal += _clockFrame;
            _clockNext = FixedClockStep(_pacedFps);
            Time.captureDeltaTime = _clockNext;
        }

        /// <summary>
        /// At a playback's start: the camera eased on the wall clock until now, so it starts settled on the player, and the
        /// clones it frames (clonesLoD) pay the same from the first tick.
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

        void PaceWalk()
        {
            if (!PlaybackActive && _pendingReplay == null) _walkSlowdown = 0f;
            bool on = _walkSlowdown > 0f && _engine.IsRunning && !TurboRunner.Active;
            if (on)
            {
                if (!_walkScaled) _playbackSpeedText = (1f / _walkSlowdown).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "x";
                _walkScaled = true;
                if (Time.timeScale != 0f) Time.timeScale = globalStats.baseGameSpeed / _walkSlowdown;
            }
            else if (_walkScaled)
            {
                _walkScaled = false;
                _playbackSpeedText = "1x";
                if (Time.timeScale != 0f) Time.timeScale = globalStats.baseGameSpeed;
            }
        }

        private void ReplaySelected(int startDelayTicks = 0, float walkSlowdown = 0f)
        {
            MacroDefinition macro = TechPlayable(SelectedMacro);
            if (macro == null) return;
            _walkSlowdown = walkSlowdown;
            Match fullTasRoute = FullTasRouteName.Match(macro.name ?? "");
            if (fullTasRoute.Success)
            {
                StartFullTasPlayback(FindFullTasRun(fullTasRoute.Groups[1].Value), macro);
                return;
            }
            EnsureGameUnpaused();
            _pendingReplay = walkSlowdown > 0f ? HumanVersionOf(macro) ?? macro : macro;
            _pendingReplayStartDelayTicks = Mathf.Max(0, startDelayTicks);
        }

        private void OnAttemptEnded(bool success, string reason)
        {
            if (_fullTasPlay != null && _fullTasPlay.started && !_silentEvaluation)
            {
                FinishFullTasPlayback(success, reason);
                return;
            }
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
