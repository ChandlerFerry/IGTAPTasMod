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
        static float FixedFrameStep()
        {
            return Instance != null && Instance._playbackPaced ? Instance._clockFrame : Time.unscaledDeltaTime;
        }

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

        internal static void AbortRun(string reason)
        {
            Log?.LogWarning("Turbo: " + reason);
            if (Instance != null && Instance._engine.IsRunning) Instance._engine.Abort(reason);
        }

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
            // SettingsScript sets the player's frame rate and vsync on scene load; hold ours.
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            bool probeFast = _probe != null && _pacedFps == MacroEngine.PlaybackFrameRate && (_probe.fast || _settings.fastPlayback);
            bool probeCap = _probe != null && !_walkScaled && !probeFast;
            int targetFrameRate = probeCap ? _pacedFps : -1;
            if (Application.targetFrameRate != targetFrameRate) Application.targetFrameRate = targetFrameRate;
            _clockFrame = _clockNext;
            _clockTotal += _clockFrame;
            // Held frame = game time stood still, not timeScale 0: a freeze starts in the crossing frame's FixedUpdate, Update sees 0.
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

        void WaitForFixedClock(float scale, double wallStep)
        {
            double now = _paceClock.Elapsed.TotalSeconds, start = now;
            _paceDue = scale > 0f ? Math.Max(_paceDue + wallStep * scale, now) : now;
            for (double left; (left = _paceDue - now) > 0; now = _paceClock.Elapsed.TotalSeconds)
                if (left > 0.016) System.Threading.Thread.Sleep(1); else System.Threading.Thread.Yield();
            _perfNow.wait += (float)((now - start) * 1000);
        }

        void SnapCameraAtPlaybackStart(Movement movement)
        {
            if (movement != null) SavestateService.SnapCamera(movement.transform.position);
        }

        static double EvenFrameStep(int fps)
        {
            double frame = 1.0 / fps, tick = Time.fixedDeltaTime;
            return tick / (Math.Floor((tick - frame / 2) / frame) + 1);
        }

        static float FixedClockStep(MacroEngine.FrameGrid grid, long ticks)
        {
            double frame = grid.Frame, first = grid.Frames(ticks).First, tick = Time.fixedDeltaTime;
            double since = Time.timeAsDouble - Time.fixedTimeAsDouble;
            double next = first + (Math.Floor((since - first) / frame + 0.5) + 1) * frame;
            if (next >= tick) next = tick + grid.Frames(MacroEngine.FrameGrid.NextTicks(ticks)).First;
            return (float)(next - since);
        }

        // Keep Time.timeScale: captureDeltaTime is scaled by it, and a slowed timeScale takes frames off 1x's grid.
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
