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
        public List<string> Warnings { get; private set; } = new List<string>();
    }

    public static class SectionCheckpointService
    {
        // Only route-relevant, value-like state is captured. Object references are deliberately excluded.
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
            int pluginSchemaVersion, bool permitFingerprintMismatch = false)
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
            if (!current.Matches(checkpoint.fingerprint) && !permitFingerprintMismatch) {
                result.Message = "Checkpoint rejected: " + current.DescribeMismatch(checkpoint.fingerprint);
                return result;
            }

            Transform transform = movement.transform;
            Component body = movement.GetComponent("Rigidbody2D");
            SetBodySimulation(body, false, result.Warnings);
            transform.position = new Vector3(checkpoint.positionX, checkpoint.positionY, checkpoint.positionZ);
            transform.rotation = new Quaternion(checkpoint.rotationX, checkpoint.rotationY, checkpoint.rotationZ, checkpoint.rotationW);
            transform.localScale = new Vector3(checkpoint.scaleX, checkpoint.scaleY, checkpoint.scaleZ);
            RestoreMembers(movement, checkpoint.movementFields, result.Warnings);
            if (body != null) RestoreMembers(body, checkpoint.bodyFields, result.Warnings);
            SetBodySimulation(body, ReadBool(checkpoint.bodyFields, "simulated", true), result.Warnings);
            Physics2DSyncTransforms();
            result.Success = true;
            result.Message = result.Warnings.Count == 0 ? "Checkpoint restored" : "Checkpoint restored with warnings";
            return result;
        }

        public static CompatibilityFingerprint CreateFingerprint(Movement movement, int pluginSchemaVersion)
        {
            return new CompatibilityFingerprint {
                gameVersion = Application.version ?? "",
                assemblySha256 = HashAssembly(typeof(Movement).Assembly),
                scene = ReadSceneName(movement),
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

        private static void RestoreMembers(object target, Dictionary<string, SnapshotValue> values, List<string> warnings)
        {
            if (values == null) return;
            Type type = target.GetType();
            foreach (KeyValuePair<string, SnapshotValue> pair in values) {
                FieldInfo field = AccessTools.Field(type, pair.Key);
                PropertyInfo property = field == null ? AccessTools.Property(type, pair.Key) : null;
                Type valueType = field != null ? field.FieldType : property != null && property.CanWrite ? property.PropertyType : null;
                if (valueType == null) { warnings.Add("Missing state member: " + pair.Key); continue; }
                try {
                    object decoded;
                    if (!TryDecode(pair.Value, valueType, out decoded)) { warnings.Add("Unsupported state value: " + pair.Key); continue; }
                    if (field != null) field.SetValue(target, decoded); else property.SetValue(target, decoded, null);
                } catch (Exception ex) { warnings.Add("Could not restore " + pair.Key + ": " + ex.GetType().Name); }
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

        private static void SetBodySimulation(Component body, bool value, List<string> warnings)
        {
            if (body == null) return;
            PropertyInfo property = AccessTools.Property(body.GetType(), "simulated");
            try { if (property != null && property.CanWrite) property.SetValue(body, value, null); }
            catch (Exception ex) { warnings.Add("Could not change body simulation: " + ex.GetType().Name); }
        }

        private static bool ReadBool(Dictionary<string, SnapshotValue> values, string key, bool fallback)
        {
            SnapshotValue value;
            bool parsed;
            return values != null && values.TryGetValue(key, out value) && bool.TryParse(value.value, out parsed) ? parsed : fallback;
        }

        private static void Physics2DSyncTransforms()
        {
            Type physics = Type.GetType("UnityEngine.Physics2D, UnityEngine.Physics2DModule");
            MethodInfo sync = physics == null ? null : AccessTools.Method(physics, "SyncTransforms");
            if (sync != null) sync.Invoke(null, null);
        }

        private static string ReadSceneName(Movement movement)
        {
            try {
                object scene = movement.gameObject.GetType().GetProperty("scene").GetValue(movement.gameObject, null);
                PropertyInfo name = scene.GetType().GetProperty("name");
                return name == null ? "" : (string)name.GetValue(scene, null);
            } catch { return ""; }
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

    [Flags]
    public enum GhostMarker { None = 0, Jump = 1, Dash = 2, Contact = 4, Apex = 8, Failure = 16 }

    [Serializable]
    public sealed class GhostPoint
    {
        public long tick;
        public float x;
        public float y;
        public float velocityX;
        public float velocityY;
        public GhostMarker markers;
    }

    [Serializable]
    public sealed class GhostTrace
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public string routeId;
        public string attemptId;
        public List<GhostPoint> points = new List<GhostPoint>();

        public static GhostTrace FromSamples(IEnumerable<RawTickSample> samples, string routeId = null, string attemptId = null)
        {
            GhostTrace trace = new GhostTrace { routeId = routeId, attemptId = attemptId };
            if (samples == null) return trace;
            bool wasGrounded = false, wasOnWall = false;
            foreach (RawTickSample sample in samples) {
                GhostMarker markers = GhostMarker.None;
                if (sample.jumpPressed) markers |= GhostMarker.Jump;
                if (sample.dashPressed) markers |= GhostMarker.Dash;
                if ((!wasGrounded && sample.onGround) || (!wasOnWall && sample.onWall)) markers |= GhostMarker.Contact;
                if (sample.atJumpApex) markers |= GhostMarker.Apex;
                trace.points.Add(new GhostPoint { tick = sample.tick, x = sample.positionX, y = sample.positionY,
                    velocityX = sample.velocityX, velocityY = sample.velocityY, markers = markers });
                wasGrounded = sample.onGround; wasOnWall = sample.onWall;
            }
            return trace;
        }
    }

    // LineRenderer only: no collider, rigidbody, transform writes to the player, or FixedUpdate behavior.
    public sealed class GhostPathRenderer : MonoBehaviour
    {
        private LineRenderer _reference;
        private LineRenderer _current;
        public bool Visible { get { return gameObject.activeSelf; } set { gameObject.SetActive(value); } }

        public static GhostPathRenderer Create(string name = "TAS Ghost Paths")
        {
            GameObject root = new GameObject(name);
            GhostPathRenderer renderer = root.AddComponent<GhostPathRenderer>();
            renderer._reference = renderer.CreateLine("Reference", new Color(0.1f, 0.85f, 1f, 0.8f));
            renderer._current = renderer.CreateLine("Current", new Color(1f, 0.72f, 0.1f, 0.9f));
            return renderer;
        }

        public void SetReference(GhostTrace trace) { SetLine(_reference, trace); }
        public void SetCurrent(GhostTrace trace) { SetLine(_current, trace); }
        public void Clear() { SetLine(_reference, null); SetLine(_current, null); }

        private LineRenderer CreateLine(string lineName, Color color)
        {
            GameObject child = new GameObject(lineName);
            child.transform.SetParent(transform, false);
            LineRenderer line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.startWidth = line.endWidth = 0.08f;
            line.startColor = line.endColor = color;
            line.numCapVertices = 2;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null) line.material = new Material(shader);
            return line;
        }

        private static void SetLine(LineRenderer line, GhostTrace trace)
        {
            if (line == null) return;
            int count = trace == null || trace.points == null ? 0 : trace.points.Count;
            line.positionCount = count;
            for (int i = 0; i < count; i++) line.SetPosition(i, new Vector3(trace.points[i].x, trace.points[i].y, 0f));
        }
    }

    public enum BundleParameterType { Integer, Float, Boolean, String }

    [Serializable]
    public sealed class BundleParameterDefinition
    {
        public string name;
        public BundleParameterType type;
        public string defaultValue;
    }

    [Serializable]
    public sealed class BundleParameterBinding
    {
        public string target; // direction, durationTicks, timeoutTicks, label, or nested.<parameter>
        public string expression; // literal or $parameterName
    }

    [Serializable]
    public sealed class BundleActionTemplate
    {
        public MacroStep step = new MacroStep();
        public string nestedBundleId;
        public List<BundleParameterBinding> bindings = new List<BundleParameterBinding>();
    }

    [Serializable]
    public sealed class ParameterizedBundleDefinition
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "New bundle";
        public int version = 1;
        public List<BundleParameterDefinition> parameters = new List<BundleParameterDefinition>();
        public List<BundleActionTemplate> actions = new List<BundleActionTemplate>();
    }

    public sealed class BundleExpansionResult
    {
        public bool Success { get { return Errors.Count == 0; } }
        public List<MacroStep> Steps { get; private set; } = new List<MacroStep>();
        public List<string> Errors { get; private set; } = new List<string>();
    }

    public static class ParameterizedBundleExpander
    {
        public static BundleExpansionResult Expand(string bundleId, IEnumerable<ParameterizedBundleDefinition> bundles,
            IDictionary<string, string> overrides = null, int maximumDepth = 32, int maximumActions = 10000)
        {
            BundleExpansionResult result = new BundleExpansionResult();
            Dictionary<string, ParameterizedBundleDefinition> index = new Dictionary<string, ParameterizedBundleDefinition>();
            if (bundles != null) foreach (ParameterizedBundleDefinition bundle in bundles)
                if (bundle != null && !string.IsNullOrEmpty(bundle.id)) index[bundle.id] = bundle;
            ExpandInto(bundleId, index, overrides, new List<string>(), result, maximumDepth, maximumActions);
            if (!result.Success) result.Steps.Clear();
            return result;
        }

        private static void ExpandInto(string id, Dictionary<string, ParameterizedBundleDefinition> index,
            IDictionary<string, string> supplied, List<string> stack, BundleExpansionResult result, int maxDepth, int maxActions)
        {
            ParameterizedBundleDefinition bundle;
            if (!index.TryGetValue(id ?? "", out bundle)) { result.Errors.Add("Bundle not found: " + id); return; }
            int cycleAt = stack.IndexOf(id);
            if (cycleAt >= 0) { result.Errors.Add("Bundle cycle: " + string.Join(" -> ", stack.GetRange(cycleAt, stack.Count - cycleAt).ToArray()) + " -> " + id); return; }
            if (stack.Count >= maxDepth) { result.Errors.Add("Bundle nesting exceeds " + maxDepth); return; }

            Dictionary<string, string> values = ResolveParameters(bundle, supplied, result);
            if (!result.Success) return;
            stack.Add(id);
            foreach (BundleActionTemplate template in bundle.actions) {
                if (result.Steps.Count >= maxActions) { result.Errors.Add("Expanded action count exceeds " + maxActions); break; }
                if (!string.IsNullOrEmpty(template.nestedBundleId)) {
                    Dictionary<string, string> nested = new Dictionary<string, string>();
                    foreach (BundleParameterBinding binding in template.bindings)
                        nested[StripNested(binding.target)] = Evaluate(binding.expression, values, result);
                    ExpandInto(template.nestedBundleId, index, nested, stack, result, maxDepth, maxActions);
                } else {
                    MacroStep step = (template.step ?? new MacroStep()).Copy();
                    foreach (BundleParameterBinding binding in template.bindings)
                        Apply(step, binding.target, Evaluate(binding.expression, values, result), result);
                    result.Steps.Add(step);
                }
                if (!result.Success) break;
            }
            stack.RemoveAt(stack.Count - 1);
        }

        private static Dictionary<string, string> ResolveParameters(ParameterizedBundleDefinition bundle,
            IDictionary<string, string> supplied, BundleExpansionResult result)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleParameterDefinition parameter in bundle.parameters) {
                string value;
                if (supplied == null || !supplied.TryGetValue(parameter.name, out value)) value = parameter.defaultValue;
                if (!Validate(value, parameter.type)) result.Errors.Add("Invalid " + parameter.type + " value for " + bundle.name + "." + parameter.name);
                else values[parameter.name] = value;
            }
            if (supplied != null) foreach (string key in supplied.Keys)
                if (!values.ContainsKey(key)) result.Errors.Add("Unknown parameter " + bundle.name + "." + key);
            return values;
        }

        private static string Evaluate(string expression, Dictionary<string, string> values, BundleExpansionResult result)
        {
            if (expression == null || !expression.StartsWith("$", StringComparison.Ordinal)) return expression ?? "";
            string name = expression.Substring(1);
            string value;
            if (values.TryGetValue(name, out value)) return value;
            result.Errors.Add("Unbound bundle parameter: " + name);
            return "";
        }

        private static void Apply(MacroStep step, string target, string value, BundleExpansionResult result)
        {
            int number;
            switch ((target ?? "").ToLowerInvariant()) {
                case "direction": if (int.TryParse(value, out number)) step.direction = Mathf.Clamp(number, -1, 1); else result.Errors.Add("direction must be an integer"); break;
                case "durationticks": if (int.TryParse(value, out number)) { step.durationTicks = Math.Max(1, number); step.endCondition = ActionEndCondition.FixedTicks; } else result.Errors.Add("durationTicks must be an integer"); break;
                case "timeoutticks": if (int.TryParse(value, out number)) step.timeoutTicks = Math.Max(1, number); else result.Errors.Add("timeoutTicks must be an integer"); break;
                case "durationms": if (int.TryParse(value, out number)) step.durationMs = Math.Max(0, number); else result.Errors.Add("durationMs must be an integer"); break;
                case "timeoutms": if (int.TryParse(value, out number)) step.timeoutMs = Math.Max(1, number); else result.Errors.Add("timeoutMs must be an integer"); break;
                case "label": step.label = value; break;
                default: result.Errors.Add("Unsupported bundle binding target: " + target); break;
            }
        }

        private static bool Validate(string value, BundleParameterType type)
        {
            int i; float f; bool b;
            switch (type) {
                case BundleParameterType.Integer: return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
                case BundleParameterType.Float: return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                case BundleParameterType.Boolean: return bool.TryParse(value, out b);
                default: return value != null;
            }
        }

        private static string StripNested(string target)
        {
            const string prefix = "nested.";
            return target != null && target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? target.Substring(prefix.Length) : target;
        }
    }
}
