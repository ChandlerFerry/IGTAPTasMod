using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
            if (other == null) return "checkpoint has no compatibility fingerprint";
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
    public sealed class SectionCheckpoint
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Section checkpoint";
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
        public Dictionary<string, SnapshotValue> movementFields = new Dictionary<string, SnapshotValue>();
        public Dictionary<string, SnapshotValue> bodyFields = new Dictionary<string, SnapshotValue>();
    }

    public sealed class CheckpointOperationResult
    {
        public bool Success { get; internal set; }
        public string Message { get; internal set; }
    }

    public static class SectionCheckpointService
    {
        private static readonly string[] MovementFieldNames = {
            "Velocity", "momentum", "MoveAxis", "onGround", "OnWall", "wallCoyoteFrames",
            "wallCoyoteDir", "coyoteFrames", "jumpBuffer", "jumpCutBuffer", "dashBuffer",
            "airJumpsLeft", "airDashesLeft", "dashActive", "dashCooldown", "dashDirection",
            "atJumpApex", "isDead", "cutsceneMode", "facingRight", "blocked", "movementMode"
        };
        private static readonly string[] BodyFieldNames = {
            "linearVelocity", "velocity", "angularVelocity", "gravityScale", "linearDamping", "drag",
            "angularDamping", "angularDrag", "bodyType", "constraints", "simulated"
        };

        public static SectionCheckpoint Capture(Movement movement, string name, int pluginSchemaVersion)
        {
            if (movement == null) throw new ArgumentNullException(nameof(movement));
            Transform transform = movement.transform;
            SectionCheckpoint result = new SectionCheckpoint {
                name = string.IsNullOrWhiteSpace(name) ? "Section checkpoint" : name,
                fingerprint = CreateFingerprint(movement, pluginSchemaVersion),
                positionX = transform.position.x, positionY = transform.position.y, positionZ = transform.position.z,
                rotationX = transform.rotation.x, rotationY = transform.rotation.y,
                rotationZ = transform.rotation.z, rotationW = transform.rotation.w,
                scaleX = transform.localScale.x, scaleY = transform.localScale.y, scaleZ = transform.localScale.z
            };
            CaptureMembers(movement, MovementFieldNames, result.movementFields);
            Component body = movement.GetComponent("Rigidbody2D");
            if (body != null) CaptureMembers(body, BodyFieldNames, result.bodyFields);
            return result;
        }

        public static CheckpointOperationResult Restore(Movement movement, SectionCheckpoint checkpoint,
            int pluginSchemaVersion)
        {
            CheckpointOperationResult result = new CheckpointOperationResult();
            if (movement == null || checkpoint == null) {
                result.Message = "Movement or checkpoint is missing";
                return result;
            }
            if (checkpoint.schemaVersion != SectionCheckpoint.CurrentSchemaVersion) {
                result.Message = "Unsupported checkpoint schema " + checkpoint.schemaVersion;
                return result;
            }
            CompatibilityFingerprint current = CreateFingerprint(movement, pluginSchemaVersion);
            if (!current.Matches(checkpoint.fingerprint)) {
                result.Message = "Checkpoint rejected: " + current.DescribeMismatch(checkpoint.fingerprint);
                return result;
            }

            Transform transform = movement.transform;
            Component body = movement.GetComponent("Rigidbody2D");
            SetBodySimulation(body, false);
            transform.position = new Vector3(checkpoint.positionX, checkpoint.positionY, checkpoint.positionZ);
            transform.rotation = new Quaternion(checkpoint.rotationX, checkpoint.rotationY, checkpoint.rotationZ, checkpoint.rotationW);
            transform.localScale = new Vector3(checkpoint.scaleX, checkpoint.scaleY, checkpoint.scaleZ);
            RestoreMembers(movement, checkpoint.movementFields);
            if (body != null) RestoreMembers(body, checkpoint.bodyFields);
            SetBodySimulation(body, ReadBool(checkpoint.bodyFields, "simulated", true));
            Physics2D.SyncTransforms();
            result.Success = true;
            result.Message = "Checkpoint restored";
            return result;
        }

        public static CompatibilityFingerprint CreateFingerprint(Movement movement, int pluginSchemaVersion)
        {
            return new CompatibilityFingerprint {
                gameVersion = Application.version ?? "",
                assemblySha256 = HashAssembly(typeof(Movement).Assembly),
                scene = movement.gameObject.scene.name,
                pluginSchemaVersion = pluginSchemaVersion
            };
        }

        private static void CaptureMembers(object target, IEnumerable<string> names, Dictionary<string, SnapshotValue> destination)
        {
            Type type = target.GetType();
            foreach (string name in names) {
                FieldInfo field = AccessTools.Field(type, name);
                PropertyInfo property = field == null ? AccessTools.Property(type, name) : null;
                try {
                    object value = field != null ? field.GetValue(target) : property != null && property.CanRead ? property.GetValue(target, null) : null;
                    SnapshotValue encoded;
                    if (value != null && TryEncode(value, out encoded)) destination[name] = encoded;
                } catch { }
            }
        }

        private static void RestoreMembers(object target, Dictionary<string, SnapshotValue> values)
        {
            if (values == null) return;
            Type type = target.GetType();
            foreach (KeyValuePair<string, SnapshotValue> pair in values) {
                FieldInfo field = AccessTools.Field(type, pair.Key);
                PropertyInfo property = field == null ? AccessTools.Property(type, pair.Key) : null;
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

        private static void SetBodySimulation(Component body, bool value)
        {
            if (body == null) return;
            PropertyInfo property = AccessTools.Property(body.GetType(), "simulated");
            try { if (property != null && property.CanWrite) property.SetValue(body, value, null); }
            catch { }
        }

        private static bool ReadBool(Dictionary<string, SnapshotValue> values, string key, bool fallback)
        {
            SnapshotValue value;
            bool parsed;
            return values != null && values.TryGetValue(key, out value) && bool.TryParse(value.value, out parsed) ? parsed : fallback;
        }



        private static string HashAssembly(Assembly assembly)
        {
            try {
                using (SHA256 sha = SHA256.Create()) using (FileStream stream = File.OpenRead(assembly.Location))
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            } catch { return "unavailable"; }
        }

        private static string F(float value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static float P(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }
    }

}
