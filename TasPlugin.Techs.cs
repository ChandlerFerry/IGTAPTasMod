using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace IGTAPTasMod
{
    /// <summary>
    /// The TAS editor's Techs here (docs/superpowers/specs/2026-09-30-techs-here-design.md): once a course or category
    /// session has held an input tick for a second, <c>enginesim techs</c> lists the techs whose key window works from there,
    /// each direction with its inputs and path. Hovering one in the studio draws its paths in game (the held tick is seeked
    /// again so its last tick renders them); trying one puts its inputs in at the held tick and plays them.
    /// </summary>
    public sealed partial class TasPlugin
    {
        const long TechsAfterMs = 1000;

        /// <summary>One direction of a tech from the held tick: its inputs from there and the player's world positions.</summary>
        sealed class TasTechVariant
        {
            public string tech, name, dir;
            public float? angle;
            public int start, lead;
            public float speed;
            public bool died;
            public List<SolverTick> ticks;
            public Vector2[] path;
        }

        /// <summary>The techs found for one held input tick (the best variant per tech, direction and angle).</summary>
        sealed class TasTechs
        {
            public readonly List<TasTechVariant> variants = new List<TasTechVariant>();
            public bool done;
            public int tried;
            public string error;
        }

        /// <summary>Why this session gets no techs (null: it does): a course and category session, its start trace, the engine.</summary>
        string TasTechsUnavailable(TasSession s, out string trace, out string caps)
        {
            trace = caps = null;
            Match category = MatchRoutePrefix(s.route.name);
            CourseDef course = category.Success && string.IsNullOrEmpty(s.route.savestateId) ? CourseOfRoute(s.route.name) : null;
            if (course == null) return "Course and category sessions only.";
            caps = CapsOf(category.Groups["label"].Value).Id;
            trace = CourseTrace(course, caps);
            if (trace == null) return "No start trace for " + course.Token + "_" + CapsOf(caps).Label + " yet: run Improve once in the Optimize tab.";
            if (string.IsNullOrEmpty(EngineSimExe) || !File.Exists(EngineSimExe)) return "The offline engine isn't built.";
            return null;
        }

        /// <summary>Every poll (hold loop and Update): drops results the inputs outdated, reads what the search found so far, and
        /// starts one for an input tick held a second. Only a held session starts or stops one (a seek passes other ticks).</summary>
        void TasPollTechs()
        {
            TasSession s = _tas;
            if (s == null) return;
            if (s.techsVersion != s.version)
            {
                TasStopTechs(s);
                s.techsCache.Clear();
                s.techsVersion = s.version;
            }
            if (s.techs != null) TasReadTechs(s);
            if (!s.held || s.starting || s.recording) return;
            int at = (int)_engine.RunTicks;
            if (at != s.heldAt) { s.heldAt = at; s.heldSince = Stopwatch.GetTimestamp(); }
            if (s.techs != null && s.techsAt != at) { TasStopTechs(s); s.techsCache.Remove(s.techsAt); }
            if (s.techs != null || at < 1 || s.techsCache.ContainsKey(at)) return;
            if ((Stopwatch.GetTimestamp() - s.heldSince) * 1000 / Stopwatch.Frequency < TechsAfterMs) return;
            if (TasTechsUnavailable(s, out string trace, out string caps) != null) return;
            Directory.CreateDirectory(EngineSimDir);
            string file = Path.Combine(EngineSimDir, "tas-techs-" + SafeId(s.route.id) + ".json");
            File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(new { name = s.route.name, ticks = s.ticks }));
            s.techsFile = Path.ChangeExtension(file, null) + "-out.jsonl";
            if (File.Exists(s.techsFile)) File.Delete(s.techsFile);
            s.techsRead = 0;
            s.techsAt = at;
            s.techsCache[at] = new TasTechs();
            s.techsOutput = new ConcurrentQueue<string>();
            int threads = Mathf.Clamp(SystemInfo.processorCount / 2, 1, 4);
            s.techs = StartEngineSim("techs " + Quote(trace) + " " + Quote(file) + " --at " + at + " --caps " + caps + " --threads " + threads
                + " --out " + Quote(s.techsFile), s.techsOutput);
            BumpWebState();
        }

        void TasStopTechs(TasSession s)
        {
            if (s.techs == null) return;
            try { if (!s.techs.HasExited) s.techs.Kill(); s.techs.Dispose(); } catch (Exception) { }
            s.techs = null;
        }

        /// <summary>The search's new lines: each variant replaces its direction's (same tech, dir and angle) when it starts sooner,
        /// or as soon and faster. At the process's exit, a result without its done line is an error.</summary>
        void TasReadTechs(TasSession s)
        {
            TasTechs found;
            if (!s.techsCache.TryGetValue(s.techsAt, out found)) { TasStopTechs(s); return; }
            bool exited = s.techs.HasExited;
            string text = "";
            try
            {
                if (File.Exists(s.techsFile))
                    using (var stream = new FileStream(s.techsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(s.techsRead, SeekOrigin.Begin);
                        byte[] bytes = new byte[stream.Length - s.techsRead];
                        int n = stream.Read(bytes, 0, bytes.Length);
                        int end = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, n - 1));
                        if (end >= 0) { text = Encoding.UTF8.GetString(bytes, 0, end + 1); s.techsRead += end + 1; }
                    }
            }
            catch (IOException) { }
            bool changed = false;
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                JObject row = JObject.Parse(line);
                changed = true;
                if (row["done"] != null) { found.done = true; found.tried = row["tried"]?.Value<int>() ?? 0; continue; }
                float[] path = row["path"].ToObject<float[]>();
                var v = new TasTechVariant
                {
                    tech = (string)row["tech"], name = (string)row["name"], dir = (string)row["dir"] ?? "", angle = row["angle"]?.ToObject<float?>(),
                    start = row["start"].Value<int>(), lead = row["lead"]?.Value<int>() ?? 0, speed = row["speed"].Value<float>(), died = row["died"].Value<bool>(),
                    ticks = row["ticks"].ToObject<List<SolverTick>>().Select(TasSanitize).ToList(),
                    path = Enumerable.Range(0, path.Length / 2).Select(i => new Vector2(path[2 * i], path[2 * i + 1])).ToArray(),
                };
                int same = found.variants.FindIndex(o => o.tech == v.tech && o.dir == v.dir && o.angle == v.angle);
                if (same < 0) found.variants.Add(v);
                else if (v.start < found.variants[same].start || v.start == found.variants[same].start && v.speed > found.variants[same].speed) found.variants[same] = v;
            }
            if (exited)
            {
                if (!found.done)
                {
                    string[] lines = s.techsOutput?.ToArray() ?? new string[0];
                    string output = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 4))).Trim();
                    found.error = output.Length > 0 ? output : "enginesim techs stopped without a result";
                }
                TasStopTechs(s);
                changed = true;
            }
            if (changed) BumpWebState();
        }

        /// <summary>A tech's variants in the studio's order: its compass's (dir, then steepest first).</summary>
        static readonly string[] TechDirOrder = { "", "U", "UR", "R", "DR", "D", "DL", "L", "UL" };

        List<TasTechVariant> TasTechVariants(TasTechs found, string tech)
        {
            return found.variants.Where(v => v.tech == tech).OrderBy(v => Array.IndexOf(TechDirOrder, v.dir)).ThenBy(v => v.angle ?? 90f).ToList();
        }

        TasTechs TasTechsHere(TasSession s)
        {
            TasTechs found;
            return s.techsCache.TryGetValue((int)_engine.RunTicks, out found) ? found : null;
        }

        object TasTechsLive(TasSession s)
        {
            TasTechs found = TasTechsHere(s);
            return new
            {
                at = (int)_engine.RunTicks, running = s.techs != null && s.techsAt == (int)_engine.RunTicks, unavailable = TasTechsUnavailable(s, out _, out _),
                done = found?.done ?? false, tried = found?.tried ?? 0, error = found?.error,
                list = found == null ? null : found.variants.Select(v => v.tech).Distinct().Select(tech => new
                {
                    tech, name = found.variants.First(v => v.tech == tech).name,
                    variants = TasTechVariants(found, tech).Select(v => new { v.dir, v.angle, v.start, v.died }).ToArray(),
                }).OrderBy(t => t.variants.Min(v => v.start)).ToArray(),
            };
        }

        /// <summary>A variant by tech, direction and angle (a search still running adds variants, so no index is stable).</summary>
        TasTechVariant TasFindTech(TasTechs found, JObject body)
        {
            string tech = Text(body, "tech"), dir = (string)body["dir"] ?? "";
            float? angle = body["angle"]?.ToObject<float?>();
            return found?.variants.FirstOrDefault(v => v.tech == tech && v.dir == dir && v.angle == angle);
        }

        /// <summary>POST /api/tas/techs/hover {tech, dir?, angle?}: draw that tech's paths, the one in that direction bright ({}
        /// clears). A held session renders no frames, so the held physics tick is seeked again: its last tick plays at 1x and
        /// draws them.</summary>
        void TasTechsHover(TasSession s, JObject body)
        {
            string tech = Text(body, "tech");
            if (tech == "") tech = null;
            string dir = body["dir"] == null ? null : (string)body["dir"] ?? "";
            float? angle = body["angle"]?.ToObject<float?>();
            if (tech == s.hoverTech && dir == s.hoverDir && angle == s.hoverAngle) return;
            s.hoverTech = tech;
            s.hoverDir = tech == null ? null : dir;
            s.hoverAngle = angle;
            if (s.held && !s.starting && s.physics > 0 && s.seekInput < 0 && s.seekPhysics < 0) s.seekPhysics = s.physics;
        }

        /// <summary>POST /api/tas/techs/apply {tech, dir, angle, slowdown}: the inputs from the held tick become the variant's
        /// (undoable), played from the held tick to their end, then held.</summary>
        void TasTechsApply(TasSession s, JObject body)
        {
            if (!s.held) throw new InvalidOperationException("Hold first: a tech is tried from the held tick.");
            TasTechVariant variant = TasFindTech(TasTechsHere(s), body) ?? throw new InvalidOperationException("Tech not found for this tick.");
            int at = (int)_engine.RunTicks;
            List<SolverTick> ticks = CloneTicks(s.ticks.GetRange(0, Math.Min(at, s.ticks.Count)));
            ticks.AddRange(variant.ticks.Select(t => t.Clone()));
            s.hoverTech = s.hoverDir = null;
            TasSetTicks(s, ticks);
            TasPlay(s, FloatNumber(body, "slowdown", s.slowdown), ticks.Count, at);
        }

        // ---- drawing ----

        static readonly Color TechPathColor = new Color(.78f, .49f, 1f, .7f), TechPathHovered = new Color(.28f, .85f, 1f, 1f), TechPathDied = new Color(1f, .36f, .43f, 1f);

        bool TechPathsShown { get { TasSession s = _tas; return s != null && s.hoverTech != null; } }

        /// <summary>The hovered tech's paths in world space (the engine's origin-free positions plus the current origin): its
        /// other directions faint, the hovered one bright (red when it dies in its coast); the lead-in (the continuation before
        /// the tech's inputs) thin, and a box where the tech starts.</summary>
        void DrawTechPaths(float scale)
        {
            TasSession s = _tas;
            TasTechs found = s == null || s.hoverTech == null ? null : TasTechsHere(s);
            Camera cam = Camera.main;
            Material material = OverlayMaterial();
            if (found == null || cam == null || material == null) return;
            List<TasTechVariant> variants = TasTechVariants(found, s.hoverTech);
            Vector2 origin = CurrentOrigin();
            Rect pixels = cam.pixelRect;
            material.SetPass(0);
            GL.PushMatrix();
            GL.Viewport(pixels);
            GL.LoadPixelMatrix(pixels.xMin, pixels.xMax, Screen.height - pixels.yMin, Screen.height - pixels.yMax);
            GL.Begin(GL.QUADS);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < variants.Count; i++)
                {
                    bool hovered = variants[i].dir == s.hoverDir && variants[i].angle == s.hoverAngle;
                    if (hovered != (pass == 1)) continue;
                    TasTechVariant v = variants[i];
                    Color color = !hovered ? TechPathColor : v.died ? TechPathDied : TechPathHovered;
                    float half = Mathf.Max(.75f, scale) * (hovered ? 2f : 1f);
                    int lead = Mathf.Clamp(v.lead, 0, v.path.Length - 1);
                    Vector2[] points = v.path.Select(p => p + origin).ToArray();
                    if (lead > 0) DrawShape(cam, new SpikeShape { points = points.Take(lead + 1).ToArray() }, color, half * .5f);
                    DrawShape(cam, new SpikeShape { points = points.Skip(lead).ToArray() }, color, half);
                    Vector2 at = points[lead];
                    DrawShape(cam, new SpikeShape { closed = true, points = new[] { at + new Vector2(-8f, -8f), at + new Vector2(8f, -8f), at + new Vector2(8f, 8f), at + new Vector2(-8f, 8f) } }, color, half);
                }
            GL.End();
            GL.PopMatrix();
            GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
        }
    }
}
