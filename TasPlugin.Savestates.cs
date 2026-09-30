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
        private static void ApplyRestartGeometry(Movement movement)
        {
            Vector2 reset = movement.courseResetPoint != Vector2.zero ? movement.courseResetPoint : movement.respawnPoint + CurrentOrigin();
            movement.transform.position = (Vector3)(reset + new Vector2(0f, -12f));
            try { movement.onGround = true; } catch { }
            try { movement.Velocity = Vector2.zero; } catch { }
            try { movement.momentum = Vector2.zero; } catch { }
            try { movement.movingPlatformVelocity = Vector2.zero; } catch { }
            SetMovementField(movement, "isDead", false);
            try
            {
                BoxCollider2D col = GetMovementField(movement, "normalCollider") as BoxCollider2D;
                if (col != null)
                {
                    if (GetMovementField(movement, "defaultColliderSize") is Vector2 s) col.size = s;
                    if (GetMovementField(movement, "defaultColliderOffset") is Vector2 o) col.offset = o;
                }
            }
            catch { }
            try
            {
                FieldInfo cutsceneField = AccessTools.Field(typeof(Movement), "cutsceneMode");
                if (cutsceneField != null) cutsceneField.SetValue(movement, Enum.Parse(cutsceneField.FieldType, "none"));
            }
            catch { }
        }

        private void CaptureSavestate()
        {
            Movement movement = FindWorldMovement();
            if (movement == null) return;
            Savestate savestate = SavestateService.Capture(movement,
                (SelectedMacro == null ? "Section" : SelectedMacro.name) + " start");
            _savestates.Insert(0, savestate);
            WriteJsonAtomic(Path.Combine(_savestatesPath, SafeId(savestate.id) + ".json"), savestate, false);
        }

        private void RestoreSavestate(Savestate savestate)
        {
            Movement movement = FindWorldMovement();
            SavestateResult result = SavestateService.Restore(movement, savestate);
            if (!result.Success) Logger.LogError(result.Message); else Logger.LogInfo(result.Message);
            if (result.Success) RebaseForSavestate(movement, savestate);
        }

        /// <summary>A placed savestate's rebaseAt, after its restore (floating-origin tests).</summary>
        void RebaseForSavestate(Movement movement, Savestate savestate)
        {
            if (!savestate.hasRebaseAt) return;
            string error = RebaseWorldAround(movement, new Vector2(savestate.rebaseAtX, savestate.rebaseAtY), out float distance);
            if (error != null) Logger.LogWarning("Savestate " + savestate.name + ": " + error);
            else Logger.LogInfo("Savestate " + savestate.name + ": world rebased around (" + savestate.rebaseAtX + ", " + savestate.rebaseAtY + "), player " + distance.ToString("0.#") + " from it");
        }

        private bool RestoreRouteStart(MacroDefinition route)
        {
            TechDef labTech = LabMapEnsureFor(route);
            Movement movement = FindWorldMovement();
            if (movement != null) _engine.ClearInjectedInput(movement);
            _canonicalReference = null;
            CourseDef named = CourseOfRoute(route.name);
            _runCourse = named;
            EnsureArea1State(named);
            EnsureZone(named);
            if (movement != null && labTech != null)
            {
                try
                {
                    PlaceLabDemoStart(labTech, movement);
                    LeaveCourses(movement);
                    return true;
                }
                catch (Exception ex) { Logger.LogWarning("Lab demo " + labTech.id + ": " + ex.Message + "; using quick restart."); }
            }
            Savestate savestate = string.IsNullOrEmpty(route.savestateId) ? null : _savestates.Find(c => c.id == route.savestateId);
            if (movement != null && savestate != null)
            {
                SavestateResult restored = SavestateService.Restore(movement, savestate);
                if (restored.Success)
                {
                    LeaveCourses(movement);
                    RebaseForSavestate(movement, savestate);
                    return true;
                }
                Logger.LogWarning(restored.Message + "; using quick restart.");
            }
            MethodInfo respawn = AccessTools.Method(typeof(Movement), "respawn");
            if (named != null)
            {
                _preservedCourseResetPoint = new Vector2(named.x, named.y);
                _hasPreservedCourseResetPoint = true;
            }
            if (movement != null && respawn != null)
            {
                if (!_hasPreservedCourseResetPoint)
                {
                    Vector2 initialResetPoint = movement.courseResetPoint != Vector2.zero
                        ? movement.courseResetPoint - CurrentOrigin()
                        : movement.respawnPoint;
                    if (initialResetPoint != Vector2.zero)
                    {
                        _preservedCourseResetPoint = initialResetPoint;
                        _hasPreservedCourseResetPoint = true;
                    }
                }
                if (_runCourse == null && _hasPreservedCourseResetPoint)
                    _runCourse = _courses.Find(c => c.x == _preservedCourseResetPoint.x && c.y == _preservedCourseResetPoint.y);
                // A course, bonus or transition start is judged at one FloatingOrigin, whatever ran before (TurboRunner.PutCanonicalOrigin).
                Vector2 reset = _preservedCourseResetPoint;
                _canonicalReference = _hasPreservedCourseResetPoint && _courses.Exists(c => c.x == reset.x && c.y == reset.y)
                    ? TurboRunner.CanonicalReference(reset) : (Vector3?)null;
                if (_canonicalReference is Vector3 reference) TurboRunner.PutCanonicalOrigin(reference);
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint + CurrentOrigin();
                movement.CancelInvoke("deathRespawn");
                movement.CancelInvoke("respawn");
                bool respawnRan = false;
                try
                {
                    respawn.Invoke(movement, new object[] { true });
                    respawnRan = true;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("respawn(true) failed (" + (ex.InnerException != null ? ex.InnerException.Message : ex.Message) + "); applying restart geometry directly.");
                }
                if (!respawnRan) ApplyRestartGeometry(movement);
                if (_hasPreservedCourseResetPoint) movement.courseResetPoint = _preservedCourseResetPoint + CurrentOrigin();
                Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    body.position = movement.transform.position;
                    body.linearVelocity = Vector2.zero;
                    body.angularVelocity = 0f;
                    Physics2D.SyncTransforms();
                }
                // The quick restart keeps the last run's history (facing, dash direction, RNG, clone timers): a category start
                // has fixed ones (CourseStart), as its captured start state does.
                CourseStart.Apply(movement, FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            }
            else Logger.LogWarning("No savestate or quick restart was available.");
            return false;
        }

        /// <summary>
        /// A savestate holds only the player, so a course the last run left tracking would keep writing its reset point into
        /// courseResetPoint every tick, and a quick restart would land there instead of at the checkpoint. The game's own
        /// off-course state (stopTracking without its rewards): no course tracking, courseResetPoint zero.
        /// </summary>
        static void LeaveCourses(Movement movement)
        {
            foreach (courseScript course in FindObjectsByType<courseScript>(FindObjectsSortMode.None)) CourseTrackingField.SetValue(course, false);
            movement.courseResetPoint = Vector2.zero;
        }

        private CourseDef CourseOfRoute(string routeName)
        {
            Match courseName = Regex.Match(routeName ?? "", @"^([^_\s]+)[_ ]");
            if (!courseName.Success) return null;
            string token = courseName.Groups[1].Value;
            return int.TryParse(token, out int courseId) ? _courses.Find(c => c.id == courseId)
                : _courses.Find(c => string.Equals(c.name, token, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The player body's interpolation stays on, as the game ships it (README "### Rules"): playback, turbo and the offline
        /// engine all model it (MacroEngine.FrameGrid).
        /// </summary>
        private void KeepInterpolation(Movement movement)
        {
            if (_playerBody == null || _playerBody.gameObject != movement.gameObject) _playerBody = movement.GetComponent<Rigidbody2D>();
            if (_playerBody != null && _playerBody.interpolation != RigidbodyInterpolation2D.Interpolate)
                _playerBody.interpolation = RigidbodyInterpolation2D.Interpolate;
        }
    }
}
