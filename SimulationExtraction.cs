using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace IGTAPTasMod
{
    [Serializable]
    public sealed class SimulationScalar
    {
        public string name;
        public string type;
        public string value;
    }

    [Serializable]
    public sealed class SimulationCollider
    {
        public int instanceId;
        public string name;
        public string tag;
        public int layer;
        public string colliderType;
        public bool enabled;
        public bool trigger;
        public float centerX;
        public float centerY;
        public float sizeX;
        public float sizeY;
        public float rotation;
        public List<float> points = new List<float>();
        public List<int> pathStarts = new List<int>();
        public List<string> components = new List<string>();
    }

    [Serializable]
    public sealed class SimulationModelExport
    {
        public int schemaVersion = 2;
        public string createdUtc = DateTime.UtcNow.ToString("o");
        public CompatibilityFingerprint fingerprint;
        public float fixedDeltaTime;
        public float playerX;
        public float playerY;
        public float playerRotation;
        public List<SimulationScalar> movement = new List<SimulationScalar>();
        public List<SimulationScalar> rigidbody = new List<SimulationScalar>();
        public List<SimulationCollider> colliders = new List<SimulationCollider>();
    }

    public static class SimulationModelExtractor
    {
        public static SimulationModelExport Capture(Movement player, int pluginSchemaVersion)
        {
            if (player == null) throw new InvalidOperationException("Player movement component was not found.");
            SimulationModelExport export = new SimulationModelExport {
                fingerprint = SectionCheckpointService.CreateFingerprint(player, pluginSchemaVersion),
                fixedDeltaTime = Time.fixedDeltaTime,
                playerX = player.transform.position.x,
                playerY = player.transform.position.y,
                playerRotation = player.transform.eulerAngles.z
            };
            CaptureValueFields(player, export.movement);
            Rigidbody2D body = player.GetComponent<Rigidbody2D>();
            if (body != null) CaptureValueFields(body, export.rigidbody);

            Collider2D[] colliders = UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsSortMode.None);
            foreach (Collider2D collider in colliders)
            {
                if (collider == null || !collider.gameObject.activeInHierarchy) continue;
                Bounds bounds = collider.bounds;
                SimulationCollider item = new SimulationCollider {
                    instanceId = collider.GetInstanceID(), name = HierarchyName(collider.transform),
                    tag = SafeTag(collider), layer = collider.gameObject.layer,
                    colliderType = collider.GetType().Name, enabled = collider.enabled,
                    trigger = collider.isTrigger, centerX = bounds.center.x, centerY = bounds.center.y,
                    sizeX = bounds.size.x, sizeY = bounds.size.y,
                    rotation = collider.transform.eulerAngles.z
                };
                Component[] components = collider.GetComponents<Component>();
                foreach (Component component in components)
                    if (component != null) item.components.Add(component.GetType().FullName ?? component.GetType().Name);
                PolygonCollider2D polygon = collider as PolygonCollider2D;
                if (polygon != null)
                {
                    for (int path = 0; path < polygon.pathCount; path++)
                    {
                        item.pathStarts.Add(item.points.Count / 2);
                        foreach (Vector2 point in polygon.GetPath(path))
                        {
                            Vector3 world = polygon.transform.TransformPoint(point + polygon.offset);
                            item.points.Add(world.x);
                            item.points.Add(world.y);
                        }
                    }
                }
                CompositeCollider2D composite = collider as CompositeCollider2D;
                if (composite != null)
                {
                    for (int path = 0; path < composite.pathCount; path++)
                    {
                        int count = composite.GetPathPointCount(path);
                        if (count <= 0) continue;
                        Vector2[] points = new Vector2[count];
                        int copied = composite.GetPath(path, points);
                        item.pathStarts.Add(item.points.Count / 2);
                        for (int pointIndex = 0; pointIndex < copied; pointIndex++)
                        {
                            Vector3 world = composite.transform.TransformPoint(points[pointIndex] + composite.offset);
                            item.points.Add(world.x);
                            item.points.Add(world.y);
                        }
                    }
                }
                export.colliders.Add(item);
            }
            return export;
        }

        public static string Save(SimulationModelExport export, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "live-simulation-model.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(export, Formatting.Indented));
            return path;
        }

        private static void CaptureValueFields(object source, List<SimulationScalar> destination)
        {
            for (Type type = source.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (FieldInfo field in fields)
                {
                    object value;
                    try { value = field.GetValue(source); }
                    catch { continue; }
                    string encoded;
                    if (!TryEncode(value, out encoded)) continue;
                    destination.Add(new SimulationScalar { name = field.Name, type = field.FieldType.FullName, value = encoded });
                }
            }
            destination.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
        }

        private static bool TryEncode(object value, out string encoded)
        {
            encoded = null;
            if (value == null) return false;
            Type type = value.GetType();
            if (type.IsEnum || type.IsPrimitive || value is decimal || value is string)
            { encoded = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); return true; }
            if (value is LayerMask lm) { encoded = lm.value.ToString(System.Globalization.CultureInfo.InvariantCulture); return true; }
            if (value is Vector2 v2) { encoded = F(v2.x) + "," + F(v2.y); return true; }
            if (value is Vector3 v3) { encoded = F(v3.x) + "," + F(v3.y) + "," + F(v3.z); return true; }
            if (value is Vector4 v4) { encoded = F(v4.x) + "," + F(v4.y) + "," + F(v4.z) + "," + F(v4.w); return true; }
            if (value is Quaternion q) { encoded = F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w); return true; }
            return false;
        }

        private static string HierarchyName(Transform transform)
        {
            List<string> names = new List<string>();
            for (Transform current = transform; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string SafeTag(Component component)
        {
            try { return component.tag; }
            catch { return ""; }
        }

        private static string F(float value) { return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
    }
}
