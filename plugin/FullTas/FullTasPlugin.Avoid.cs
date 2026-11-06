using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        static float[] NormalizedBox(float[] b)
        {
            float x0 = Math.Min(b[0], b[2]), x1 = Math.Max(b[0], b[2]), y0 = Math.Min(b[1], b[3]), y1 = Math.Max(b[1], b[3]);
            return new[] { x0, y0, x1, y1 }.Select(v => (float)Math.Round(v, 1)).ToArray();
        }

        static Func<JToken, bool> SameBox(float[] wanted) =>
            t => t is JArray a && a.Count == 4 && Enumerable.Range(0, 4).All(i => Math.Abs((float)a[i] - wanted[i]) < .5f);

        static string BoxNote(float[] b) => "x " + b[0].ToString("0.#", CultureInfo.InvariantCulture) + " to " + b[2].ToString("0.#", CultureInfo.InvariantCulture)
            + ", y " + b[1].ToString("0.#", CultureInfo.InvariantCulture) + " to " + b[3].ToString("0.#", CultureInfo.InvariantCulture);

        static int EditForcedPaths(Func<JArray, int> edit)
        {
            string built = ForcedPathsFile ?? throw new InvalidOperationException("The offline solver's path isn't known to this build.");
            string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(built), "..", "..", "..", "forced-paths.json"));
            string[] files = new[] { source, built }.Where(File.Exists).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("No forced-paths.json at " + source + " or " + built + ".");
            JObject root = JObject.Parse(File.ReadAllText(files[0]));
            int changed = edit((JArray)root["paths"]);
            string text = CompactScalarArrays.Replace(root.ToString(Formatting.Indented).Replace("\r\n", "\n"), m =>
                "[" + string.Join(", ", m.Groups[1].Value.Split(',').Select(s => s.Trim())) + "]");
            text = CompactBoxLists.Replace(text, m => "[" + string.Join(", ", InnerArrays.Matches(m.Groups[1].Value).Cast<Match>().Select(i => i.Value)) + "]") + "\n";
            foreach (string file in new[] { source, built }.Where(f => Directory.Exists(Path.GetDirectoryName(f)))) File.WriteAllText(file, text);
            _forcedPathsCheckAt = float.NegativeInfinity;
            return changed;
        }

        static string ForcedPathsFile
        {
            get { string exe = EngineSimExe; return string.IsNullOrEmpty(exe) ? null : Path.Combine(Path.GetDirectoryName(exe), "forced-paths.json"); }
        }

        static readonly CachedFiles<JObject> _forcedPaths = new CachedFiles<JObject>();
        static float _forcedPathsCheckAt = float.NegativeInfinity;
        static JObject _forcedPathsRoot;

        static JObject ForcedPaths()
        {
            if (Time.unscaledTime < _forcedPathsCheckAt) return _forcedPathsRoot;
            _forcedPathsCheckAt = Time.unscaledTime + 1f;
            string file = ForcedPathsFile;
            try { _forcedPathsRoot = file != null && File.Exists(file) ? _forcedPaths.Get(file, p => JObject.Parse(File.ReadAllText(p))) : null; }
            catch (Exception ex) { Log.LogWarning("forced-paths.json unreadable: " + ex.Message); _forcedPathsRoot = null; }
            return _forcedPathsRoot;
        }

        static int EditCourseAvoids(int course, string caps, string[] categories, float[] b, string name, bool on, bool remove)
        {
            float[] wanted = NormalizedBox(b);
            Func<JToken, bool> same = SameBox(wanted);
            return EditForcedPaths(paths =>
            {
                List<JObject> rules = paths.OfType<JObject>().Where(r => (int?)r["course"] == course
                    && r["capabilities"] is JArray list && list.Any(c => (string)c == caps)).ToList();
                int changed = 0;
                if (remove)
                {
                    foreach (JObject hit in rules)
                    {
                        foreach (string kind in new[] { "avoid", "via" })
                            if (hit[kind] is JArray boxes)
                                foreach (JToken box in boxes.Where(same).ToList()) { box.Remove(); changed++; }
                        if (hit["optional"] is JArray optional)
                            foreach (JObject entry in optional.OfType<JObject>().ToList())
                            {
                                int left = 0;
                                foreach (string kind in new[] { "avoid", "via" })
                                    if (entry[kind] is JArray inner)
                                    {
                                        foreach (JToken box in inner.Where(same).ToList()) { box.Remove(); changed++; }
                                        left += inner.Count;
                                    }
                                if (left == 0) entry.Remove();
                            }
                        if ((bool?)hit["drawn"] == true && new[] { "avoid", "via", "optional" }.All(k => !(hit[k] is JArray a) || a.Count == 0)) hit.Remove();
                    }
                    return changed;
                }
                JObject rule = rules.OrderBy(r => r["capabilities"] is JArray list ? list.Count : int.MaxValue).FirstOrDefault();
                if (name != null)
                {
                    JObject block = rules.SelectMany(r => (r["optional"] as JArray ?? new JArray()).OfType<JObject>())
                        .FirstOrDefault(o => (string)o["name"] == name);
                    if (block != null)
                    {
                        var boxes = (JArray)(block["avoid"] ?? (block["avoid"] = new JArray()));
                        if (boxes.Any(same)) return 0;
                        boxes.Add(new JArray(wanted));
                        return 1;
                    }
                    rule = rule ?? NewCourseRule(paths, course, categories);
                    block = new JObject { ["name"] = name, ["note"] = BoxNote(wanted) + ". " + (on ? "On unless turned off." : "Off unless picked.") };
                    if (on) block["default"] = true;
                    block["avoid"] = new JArray { new JArray(wanted) };
                    block["via"] = new JArray();
                    ((JArray)(rule["optional"] ?? (rule["optional"] = new JArray()))).Add(block);
                    return 1;
                }
                rule = rule ?? NewCourseRule(paths, course, categories);
                var standing = (JArray)(rule["avoid"] ?? (rule["avoid"] = new JArray()));
                if (standing.Any(same)) return 0;
                standing.Add(new JArray(wanted));
                return 1;
            });
        }

        static JObject NewCourseRule(JArray paths, int course, string[] categories)
        {
            var rule = new JObject { ["course"] = course, ["capabilities"] = new JArray(categories),
                ["note"] = "Drawn on the course map.", ["drawn"] = true, ["avoid"] = new JArray(), ["via"] = new JArray() };
            paths.Add(rule);
            return rule;
        }

        object HandleCourseAvoid(JObject body)
        {
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (!Categories.IsCategory(caps)) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, caps);
            if (!(body["box"] is JArray box) || box.Count != 4) throw new InvalidOperationException("box: [x0, y0, x1, y1] in game-world coordinates.");
            string name = Text(body, "name").Trim();
            return new { ok = true, changed = EditCourseAvoids(course, caps, def.CategoryIds,
                box.Select(v => (float)v).ToArray(), name.Length == 0 ? null : name, Truthy(body, "on"), Truthy(body, "remove")) };
        }
    }
}
