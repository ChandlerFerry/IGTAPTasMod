using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        /// <summary>A box as forced-paths.json keeps them: corners in order, rounded to 0.1.</summary>
        static float[] NormalizedBox(float[] b)
        {
            float x0 = Math.Min(b[0], b[2]), x1 = Math.Max(b[0], b[2]), y0 = Math.Min(b[1], b[3]), y1 = Math.Max(b[1], b[3]);
            return new[] { x0, y0, x1, y1 }.Select(v => (float)Math.Round(v, 1)).ToArray();
        }

        static Func<JToken, bool> SameBox(float[] wanted) =>
            t => t is JArray a && a.Count == 4 && Enumerable.Range(0, 4).All(i => Math.Abs((float)a[i] - wanted[i]) < .5f);

        /// <summary>"x 19800.2 to 19978.2, y 3538.6 to 3581.9", as every box's note says it.</summary>
        static string BoxNote(float[] b) => "x " + b[0].ToString("0.#", CultureInfo.InvariantCulture) + " to " + b[2].ToString("0.#", CultureInfo.InvariantCulture)
            + ", y " + b[1].ToString("0.#", CultureInfo.InvariantCulture) + " to " + b[3].ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>
        /// forced-paths.json (the source copy by the solver's project and the built copy the solver and the studio's
        /// optional blocks read): load it, let <paramref name="edit"/> change its "paths", and write both copies back in
        /// the file's compact format (scalar arrays and arrays of boxes on one line). Returns what the edit counted.
        /// </summary>
        static int EditForcedPaths(Func<JArray, int> edit)
        {
            string exe = EngineSimExe;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("The offline solver's path isn't known to this build.");
            string built = Path.Combine(Path.GetDirectoryName(exe), "forced-paths.json");
            string source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", "..", "..", "forced-paths.json"));
            string[] files = new[] { source, built }.Where(File.Exists).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("No forced-paths.json at " + source + " or " + built + ".");
            JObject root = JObject.Parse(File.ReadAllText(files[0]));
            int changed = edit((JArray)root["paths"]);
            string text = CompactScalarArrays.Replace(root.ToString(Formatting.Indented).Replace("\r\n", "\n"), m =>
                "[" + string.Join(", ", m.Groups[1].Value.Split(',').Select(s => s.Trim())) + "]");
            text = CompactBoxLists.Replace(text, m => "[" + string.Join(", ", InnerArrays.Matches(m.Groups[1].Value).Cast<Match>().Select(i => i.Value)) + "]") + "\n";
            foreach (string file in new[] { source, built }.Where(f => Directory.Exists(Path.GetDirectoryName(f)))) File.WriteAllText(file, text);
            return changed;
        }

        /// <summary>
        /// A box drawn on a course's search map (game-world coordinates). Rules match like the engine reads them (its
        /// course, its "capabilities" listing the map's). Unnamed, the box is cut for every solve those rules cover (the
        /// rule's standing "avoid"); named, it goes to that optional block of the rule covering this map, created with the
        /// box's description as its note and "default": true when it starts on. Remove takes the box out of the standing
        /// avoids and the optional blocks of every matching rule, dropping blocks left without boxes.
        /// </summary>
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
                    // One name stays one block: it joins the block wherever the matching rules already have it.
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

        /// <summary>A drawn course rule: every category the course runs in, so a box drawn on one of its maps is cut on all of them.</summary>
        static JObject NewCourseRule(JArray paths, int course, string[] categories)
        {
            var rule = new JObject { ["course"] = course, ["capabilities"] = new JArray(categories),
                ["note"] = "Drawn on the course map.", ["drawn"] = true, ["avoid"] = new JArray(), ["via"] = new JArray() };
            paths.Add(rule);
            return rule;
        }

        /// <summary>POST /api/enginesim/avoid {course, capabilities, box, name?, on?, remove?}: an avoid box drawn on
        /// the course map's search (see <see cref="EditCourseAvoids"/> for where it lands).</summary>
        object HandleCourseAvoid(JObject body)
        {
            int course = Number(body, "course", 0);
            string caps = Text(body, "capabilities");
            CourseDef def = _courses.Find(c => c.id == course) ?? throw new InvalidOperationException("Unknown course " + course + ".");
            if (!CapabilityLabels.ContainsKey(caps)) throw new InvalidOperationException("Unknown capabilities.");
            CheckCategory(course, caps);
            if (!(body["box"] is JArray box) || box.Count != 4) throw new InvalidOperationException("box: [x0, y0, x1, y1] in game-world coordinates.");
            string name = Text(body, "name").Trim();
            return new { ok = true, changed = EditCourseAvoids(course, caps, CapsLadder.Where(def.HasCategory).ToArray(),
                box.Select(v => (float)v).ToArray(), name.Length == 0 ? null : name, Truthy(body, "on"), Truthy(body, "remove")) };
        }
    }
}
