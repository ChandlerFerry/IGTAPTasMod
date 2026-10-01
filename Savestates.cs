using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    [Serializable]
    public sealed class CompatibilityFingerprint
    {
        public string gameVersion;
        public string assemblySha256;
        public string scene;
        public int pluginSchemaVersion;

        public bool Matches(CompatibilityFingerprint other)
        {
            return other != null && gameVersion == other.gameVersion
                && assemblySha256 == other.assemblySha256 && scene == other.scene
                && pluginSchemaVersion == other.pluginSchemaVersion;
        }

        public string DescribeMismatch(CompatibilityFingerprint other)
        {
            if (other == null) return "savestate has no compatibility fingerprint";
            List<string> differences = new List<string>();
            if (gameVersion != other.gameVersion) differences.Add("game version");
            if (assemblySha256 != other.assemblySha256) differences.Add("Assembly-CSharp hash");
            if (scene != other.scene) differences.Add("scene");
            if (pluginSchemaVersion != other.pluginSchemaVersion) differences.Add("snapshot schema");
            return differences.Count == 0 ? "compatible" : string.Join(", ", differences.ToArray()) + " changed";
        }
    }

    [Serializable]
    public sealed class SnapshotValue
    {
        public string type;
        public string value;
    }

    [Serializable]
    public sealed class Savestate
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Savestate";
        public string createdUtc = DateTime.UtcNow.ToString("o");
        public CompatibilityFingerprint fingerprint;
        public float positionX;
        public float positionY;
        public float positionZ;
        public float rotationX;
        public float rotationY;
        public float rotationZ;
        public float rotationW = 1f;
        public float scaleX = 1f;
        public float scaleY = 1f;
        public float scaleZ = 1f;
        /// <summary>The FloatingOrigin at capture: a restore moves the position by the origin's change since (world frame kept).</summary>
        public bool hasOrigin;
        public float originX;
        public float originY;
        /// <summary>cameraMover.camSize (the zoom a camSizeTrigger sets and nothing resets); 0 in savestates from before it.</summary>
        public float camSize;
        /// <summary>Rebase the world around this world point after the restore (a placed savestate's floating-origin test).</summary>
        public bool hasRebaseAt;
        public float rebaseAtX;
        public float rebaseAtY;
        public Dictionary<string, SnapshotValue> movementFields = new Dictionary<string, SnapshotValue>();
        public Dictionary<string, SnapshotValue> bodyFields = new Dictionary<string, SnapshotValue>();
    }

    public sealed class SavestateResult
    {
        public bool Success { get; internal set; }
        public string Message { get; internal set; }
    }

    public static class SavestateService
    {
        private static readonly string[] MovementFieldNames = {
            "Velocity", "momentum", "MoveAxis", "onGround", "OnWall", "wallCoyoteFrames",
            "wallCoyoteDir", "coyoteFrames", "jumpBuffer", "jumpCutBuffer", "dashBuffer",
            "airJumpsLeft", "airDashesLeft", "wallJumpsLeft", "isFinalWallClingActive", "finalWallClingTimeLeft",
            "currentGroundType", "tiredSoundCooldown", "dashActive", "dashCooldown", "dashDirection",
            "atJumpApex", "isDead", "cutsceneMode", "facingRight", "blocked", "movementMode"
        };
        private static readonly string[] BodyFieldNames = {
            "linearVelocity", "angularVelocity", "gravityScale", "linearDamping", "angularDamping", "bodyType", "constraints",
            "simulated"
        };

        public static Savestate Capture(Movement movement, string name)
        {
            if (movement == null) throw new ArgumentNullException(nameof(movement));
            Transform transform = movement.transform;
            Savestate result = new Savestate {
                name = string.IsNullOrWhiteSpace(name) ? "Savestate" : name,
                fingerprint = CreateFingerprint(movement),
                positionX = transform.position.x, positionY = transform.position.y, positionZ = transform.position.z,
                rotationX = transform.rotation.x, rotationY = transform.rotation.y,
                rotationZ = transform.rotation.z, rotationW = transform.rotation.w,
                scaleX = transform.localScale.x, scaleY = transform.localScale.y, scaleZ = transform.localScale.z
            };
            Vector2 origin = TasPlugin.CurrentOrigin();
            result.hasOrigin = true;
            result.originX = origin.x;
            result.originY = origin.y;
            if (movement.cam != null) result.camSize = movement.cam.camSize;
            CaptureMembers(movement, MovementFieldNames, result.movementFields);
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            if (body != null) CaptureMembers(body, BodyFieldNames, result.bodyFields);
            return result;
        }

        public static SavestateResult Restore(Movement movement, Savestate savestate)
        {
            SavestateResult result = new SavestateResult();
            if (movement == null || savestate == null) {
                result.Message = "Movement or savestate is missing";
                return result;
            }
            if (savestate.schemaVersion != Savestate.CurrentSchemaVersion) {
                result.Message = "Unsupported savestate schema " + savestate.schemaVersion;
                return result;
            }
            CompatibilityFingerprint current = CreateFingerprint(movement);
            if (!current.Matches(savestate.fingerprint)) {
                result.Message = "Savestate rejected: " + current.DescribeMismatch(savestate.fingerprint);
                return result;
            }

            Transform transform = movement.transform;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            if (body != null) body.simulated = false;
            Vector3 position = new Vector3(savestate.positionX, savestate.positionY, savestate.positionZ);
            if (savestate.hasOrigin)
            {
                Vector2 origin = TasPlugin.CurrentOrigin();
                position += new Vector3(origin.x - savestate.originX, origin.y - savestate.originY, 0f);
            }
            transform.position = position;
            transform.rotation = new Quaternion(savestate.rotationX, savestate.rotationY, savestate.rotationZ, savestate.rotationW);
            transform.localScale = new Vector3(savestate.scaleX, savestate.scaleY, savestate.scaleZ);
            RestoreMembers(movement, savestate.movementFields, TasPlugin.UnlocksLocked);
            if (body != null)
            {
                RestoreMembers(body, savestate.bodyFields, false);
                body.simulated = ReadBool(savestate.bodyFields, "simulated", true);
            }
            Physics2D.SyncTransforms();
            // A savestate holds only the player. What it replaces may still have a death's respawn queued (onDeath's 0.6 s
            // Invoke, which would teleport the player back to the checkpoint; turbo's restore cancels every invoke, but a blanket
            // CancelInvoke here would drop a wall jump's reEnableNormalCollider) and a camZoneScript holding the camera (only
            // its OnTriggerExit2D gives it back). The camera follows the player, as the zone's exit leaves it; a zone the
            // restore lands in takes it again on the next physics step.
            movement.CancelInvoke("deathRespawn");
            if (movement.cam != null)
            {
                movement.cam.newTarget(movement.gameObject, movement.cam.defaultoffset, true, Vector2.zero);
                if (savestate.camSize > 0f) movement.cam.newCamSize(savestate.camSize);
            }
            SnapCamera(transform.position);
            result.Success = true;
            result.Message = "Savestate restored";
            return result;
        }

        static readonly FieldInfo CameraLerpedPos = AccessTools.Field(typeof(cameraMover), "lerpedPos"),
            CameraPlayerScript = AccessTools.Field(typeof(cameraMover), "playerScript"),
            CameraPlayerSetOffset = AccessTools.Field(typeof(cameraMover), "playerSetOffset"),
            MovementDashDirection = AccessTools.Field(typeof(Movement), "dashDirection");

        /// <summary>
        /// Puts the camera where cameraMover's follow settles on the player (no lookahead, the player's set offset, the target
        /// zoom), so a restore doesn't pan across the map from where the camera was. The set offset is Movement.FixedUpdate's
        /// fall look-down from the speed now: the one the last tick before a restore left points the camera up to 1180 low.
        /// </summary>
        internal static void SnapCamera(Vector3 player, bool lookDown = true)
        {
            Camera cam = Camera.main;
            cameraMover mover = cam != null ? cam.GetComponent<cameraMover>() : null;
            if (mover == null) return;
            // A turbo snapshot restores the camera's own set offset (lookDown false); a savestate has none.
            Vector3 offset = !lookDown && CameraPlayerSetOffset != null ? (Vector3)CameraPlayerSetOffset.GetValue(mover) : mover.defaultoffset;
            if (lookDown && CameraPlayerScript?.GetValue(mover) is Movement m)
            {
                float vy = m.Velocity.y + m.momentum.y;
                bool dashingDown = m.dashActive && MovementDashDirection?.GetValue(m) is Vector2 dash && dash.y < 0f;
                if (dashingDown) offset = (Vector3)CameraPlayerSetOffset.GetValue(mover);
                else if (vy < -850f) offset = new Vector3(0f, 620f + Mathf.Clamp(vy * 0.92f, -1600f, 100000000f));
                else if (vy < -150f && !m.OnWall && !m.onGround) offset = new Vector3(0f, mover.defaultoffset.y + vy / 3.3f, 0f);
                mover.NewPlayersetOffset(offset);
            }
            CameraLerpedPos?.SetValue(mover, (Vector3)(Vector2)player);
            mover.transform.position = new Vector3(Mathf.Round(player.x), Mathf.Round(player.y + offset.y), -100f);
            cam.orthographicSize = mover.camSize;
        }

        static CompatibilityFingerprint CreateFingerprint(Movement movement)
        {
            return new CompatibilityFingerprint {
                gameVersion = Application.version ?? "",
                assemblySha256 = HashAssembly(typeof(Movement).Assembly),
                scene = movement.gameObject.scene.name,
                pluginSchemaVersion = MacroLibrary.CurrentSchemaVersion
            };
        }

        // A member's FieldInfo or PropertyInfo by the target's type and its name: a tech overlay captures a state every tick.
        private static readonly Dictionary<(Type, string), (FieldInfo field, PropertyInfo property)> Members =
            new Dictionary<(Type, string), (FieldInfo, PropertyInfo)>();

        private static (FieldInfo field, PropertyInfo property) Member(Type type, string name)
        {
            if (!Members.TryGetValue((type, name), out var member))
            {
                FieldInfo field = AccessTools.Field(type, name);
                Members[(type, name)] = member = (field, field == null ? AccessTools.Property(type, name) : null);
            }
            return member;
        }

        private static void CaptureMembers(object target, IEnumerable<string> names, Dictionary<string, SnapshotValue> destination)
        {
            Type type = target.GetType();
            foreach (string name in names) {
                (FieldInfo field, PropertyInfo property) = Member(type, name);
                try {
                    object value = field != null ? field.GetValue(target) : property != null && property.CanRead ? property.GetValue(target, null) : null;
                    SnapshotValue encoded;
                    if (value != null && TryEncode(value, out encoded)) destination[name] = encoded;
                } catch { }
            }
        }

        /// <summary>Drops every field a capture doesn't take (a shared savestate's unlocks, anything else hand-added).</summary>
        internal static void KeepCapturedFields(Savestate savestate)
        {
            foreach (string key in savestate.movementFields.Keys.Where(k => Array.IndexOf(MovementFieldNames, k) < 0).ToList()) savestate.movementFields.Remove(key);
            foreach (string key in savestate.bodyFields.Keys.Where(k => Array.IndexOf(BodyFieldNames, k) < 0).ToList()) savestate.bodyFields.Remove(key);
        }

        /// <summary>An unlock (not a per-jump count like airDashesLeft): savestates capture none, but one may carry them (a Full-TAS savestate start).</summary>
        public static bool IsUnlockField(string name)
        {
            return name.EndsWith("Unlocked", StringComparison.Ordinal) || name == "maxAirDashes" || name == "maxAirJumps";
        }

        private static void RestoreMembers(object target, Dictionary<string, SnapshotValue> values, bool skipUnlocks)
        {
            if (values == null) return;
            Type type = target.GetType();
            foreach (KeyValuePair<string, SnapshotValue> pair in values) {
                if (skipUnlocks && IsUnlockField(pair.Key)) continue;
                (FieldInfo field, PropertyInfo property) = Member(type, pair.Key);
                Type valueType = field != null ? field.FieldType : property != null && property.CanWrite ? property.PropertyType : null;
                if (valueType == null) continue;
                try {
                    object decoded;
                    if (!TryDecode(pair.Value, valueType, out decoded)) continue;
                    if (field != null) field.SetValue(target, decoded); else property.SetValue(target, decoded, null);
                } catch { }
            }
        }

        private static bool TryEncode(object input, out SnapshotValue result)
        {
            Type type = input.GetType();
            result = new SnapshotValue();
            if (input is Vector2 v2) { result.type = "Vector2"; result.value = F(v2.x) + "," + F(v2.y); return true; }
            if (input is Vector3 v3) { result.type = "Vector3"; result.value = F(v3.x) + "," + F(v3.y) + "," + F(v3.z); return true; }
            if (type.IsEnum) { result.type = "Enum"; result.value = input.ToString(); return true; }
            if (input is bool) { result.type = "Boolean"; result.value = (bool)input ? "true" : "false"; return true; }
            if (input is string) { result.type = "String"; result.value = (string)input; return true; }
            if (input is IConvertible) { result.type = "Number"; result.value = Convert.ToString(input, CultureInfo.InvariantCulture); return true; }
            return false;
        }

        private static bool TryDecode(SnapshotValue input, Type targetType, out object result)
        {
            result = null;
            if (input == null) return false;
            try {
                string[] parts;
                if (targetType == typeof(Vector2)) { parts = input.value.Split(','); result = new Vector2(P(parts[0]), P(parts[1])); return true; }
                if (targetType == typeof(Vector3)) { parts = input.value.Split(','); result = new Vector3(P(parts[0]), P(parts[1]), P(parts[2])); return true; }
                if (targetType.IsEnum) { result = Enum.Parse(targetType, input.value, true); return true; }
                if (targetType == typeof(string)) { result = input.value; return true; }
                result = Convert.ChangeType(input.value, targetType, CultureInfo.InvariantCulture);
                return true;
            } catch { return false; }
        }

        private static bool ReadBool(Dictionary<string, SnapshotValue> values, string key, bool fallback)
        {
            SnapshotValue value;
            bool parsed;
            return values != null && values.TryGetValue(key, out value) && bool.TryParse(value.value, out parsed) ? parsed : fallback;
        }

        /// <summary>The game's Assembly-CSharp hash (a shared route plays the same only on the same build).</summary>
        internal static string GameAssemblyHash() { return HashAssembly(typeof(Movement).Assembly); }

        /// <summary>
        /// Stamps a savestate from another game build (a Steam update changes Assembly-CSharp's hash, and may change the
        /// version) with this one when every field it holds is still on the player or its body and its value still decodes to
        /// that member's type: a savestate is the player's state alone, which the update leaves meaning the same. True when it
        /// was stamped; <paramref name="problem"/> says why one from another build can't be (it stays rejected).
        /// </summary>
        internal static bool AdoptGameBuild(Savestate savestate, out string problem)
        {
            problem = null;
            CompatibilityFingerprint saved = savestate.fingerprint;
            if (saved == null) { problem = "it has no compatibility fingerprint"; return false; }
            string version = Application.version ?? "", hash = GameAssemblyHash();
            if (saved.gameVersion == version && saved.assemblySha256 == hash) return false;
            if (hash == "unavailable") { problem = "this build's Assembly-CSharp can't be hashed"; return false; }
            if (saved.pluginSchemaVersion != MacroLibrary.CurrentSchemaVersion) { problem = "its snapshot schema changed"; return false; }
            problem = UnrestorableField(typeof(Movement), savestate.movementFields) ?? UnrestorableField(typeof(Rigidbody2D), savestate.bodyFields);
            if (problem != null) return false;
            saved.gameVersion = version;
            saved.assemblySha256 = hash;
            return true;
        }

        static string UnrestorableField(Type type, Dictionary<string, SnapshotValue> values)
        {
            if (values == null) return null;
            foreach (KeyValuePair<string, SnapshotValue> pair in values)
            {
                (FieldInfo field, PropertyInfo property) = Member(type, pair.Key);
                Type valueType = field != null ? field.FieldType : property != null && property.CanWrite ? property.PropertyType : null;
                if (valueType == null) return type.Name + "." + pair.Key + " is gone";
                if (!TryDecode(pair.Value, valueType, out _)) return type.Name + "." + pair.Key + " no longer takes " + pair.Value?.value;
            }
            return null;
        }

        private static Assembly _hashedAssembly;
        private static string _assemblyHash;

        private static string HashAssembly(Assembly assembly)
        {
            if (assembly == _hashedAssembly) return _assemblyHash;
            string hash;
            try {
                using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(assembly.Location))
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            } catch { hash = "unavailable"; }
            _hashedAssembly = assembly;
            _assemblyHash = hash;
            return hash;
        }

        private static string F(float value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static float P(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }
    }

}
