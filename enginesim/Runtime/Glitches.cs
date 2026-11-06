using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IGTAP.EngineSim
{
    public enum RunClass { Major, Nmg, HumanMajor, HumanNmg }

    public sealed class HumanWeights
    {
        public float Grace = 2, FramePerfect = 5, BackToBack = 20;
        public Dictionary<string, float> Techs = new Dictionary<string, float>(StringComparer.Ordinal);

        public float Of(GlitchSpan s) => Techs.TryGetValue(s.Tech, out float w) ? w : 0f;

        public bool PricesDifficulty => FramePerfect != 0f || BackToBack != 0f;

        public HumanWeights WithTechWeights(string[] args)
        {
            HashSet<string> known = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--tech-weight") continue;
                known ??= new HashSet<string>(Glitches.Catalog.Value.Select(t => t.id), StringComparer.Ordinal);
                foreach (string item in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int eq = item.IndexOf('=');
                    if (eq <= 0 || !float.TryParse(item.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds))
                        throw new ArgumentException("--tech-weight " + item + ": <tech>=<seconds>");
                    string id = item.Substring(0, eq);
                    if (!known.Contains(id)) throw new ArgumentException("--tech-weight " + item + ": no tech \"" + id + "\" in techs.json");
                    Techs[id] = seconds;
                }
            }
            return this;
        }

        public string TechsText => string.Join(", ", Techs.Select(t => $"{t.Value.ToString(CultureInfo.InvariantCulture)} s a {t.Key} span"));

        public float DifficultySeconds(int framePerfect, int backToBack) => FramePerfect * framePerfect + BackToBack * backToBack;

        public static HumanWeights Of(JsonElement fulltas)
        {
            var w = new HumanWeights();
            if (fulltas.ValueKind != JsonValueKind.Object || !fulltas.TryGetProperty("human", out JsonElement h) || h.ValueKind != JsonValueKind.Object) return w;
            float Read(string name, float fallback) => h.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;
            w.Grace = Read("grace", w.Grace);
            w.FramePerfect = Read("framePerfect", w.FramePerfect);
            w.BackToBack = Read("backToBack", w.BackToBack);
            return w;
        }
    }

    public sealed class GlitchSpan
    {
        public string Tech, Category;
        public int Step, Tick;
        public long Physics;
    }

    public sealed class GlitchReport
    {
        public List<GlitchSpan> Spans = new List<GlitchSpan>();
        public int Major, Nmg, GraceTicks;
        public List<long> GraceAt = new List<long>();
        public List<string> Undetected = new List<string>();

        public float Penalty(HumanWeights w)
        {
            float p = GraceTicks * w.Grace;
            foreach (GlitchSpan s in Spans) p += w.Of(s);
            return p;
        }

        public GlitchReport Within(long after, long until)
        {
            var r = new GlitchReport { Undetected = Undetected };
            foreach (GlitchSpan s in Spans)
                if (s.Physics > after && s.Physics <= until) r.Add(s);
            foreach (long t in GraceAt)
                if (t > after && t <= until) r.GraceAt.Add(t);
            r.GraceTicks = r.GraceAt.Count;
            return r;
        }

        internal void Add(GlitchSpan s)
        {
            Spans.Add(s);
            if (s.Category == Glitches.MajorCategory) Major++;
            else if (s.Category == Glitches.NmgCategory) Nmg++;
        }
    }

    public static class Glitches
    {
        public const string MajorCategory = "major", NmgCategory = "nmg", GlitchlessCategory = "glitchless";

        public static readonly Lazy<List<(string id, string category, string detector)>> Catalog = new Lazy<List<(string, string, string)>>(() =>
            TechGrafts.Categories(Directory.GetCurrentDirectory()).ToList());

        public static GlitchReport Classify(TechSteps run, HumanWeights weights = null)
        {
            var report = new GlitchReport();
            List<TechStep> steps = run.Steps;
            var found = new List<GlitchSpan>();
            foreach (var (id, category, detector) in Catalog.Value)
            {
                if (category != MajorCategory && category != NmgCategory && (weights == null || !weights.Techs.ContainsKey(id))) continue;
                if (!TechChecks.Checkable(id, detector)) { report.Undetected.Add(id); continue; }
                for (int from = 0, last = -2; from < steps.Count;)
                {
                    int at = TechChecks.Find(id, detector, run, from, steps.Count - 1);
                    if (at < from) break;
                    if (at != last + 1) found.Add(new GlitchSpan { Tech = id, Category = category, Step = at, Tick = steps[at].Tick, Physics = steps[at].Physics });
                    last = at;
                    from = at + 1;
                }
            }
            foreach (GlitchSpan s in found.OrderBy(s => s.Step).ThenBy(s => s.Tech, StringComparer.Ordinal)) report.Add(s);
            for (int i = 0; i < steps.Count; i++)
                if (steps[i].Spike && !steps[i].Died) report.GraceAt.Add(steps[i].Physics);
            report.GraceTicks = report.GraceAt.Count;
            return report;
        }

        public static RunClass Parse(string key)
        {
            switch (key)
            {
                case "major": return RunClass.Major;
                case "nmg": return RunClass.Nmg;
                case "human-major": return RunClass.HumanMajor;
                case "human-nmg": return RunClass.HumanNmg;
                default: throw new ArgumentException("unknown run class \"" + key + "\" (major, nmg, human-major, human-nmg)");
            }
        }

        public static string Key(RunClass c)
        {
            switch (c)
            {
                case RunClass.Nmg: return "nmg";
                case RunClass.HumanMajor: return "human-major";
                case RunClass.HumanNmg: return "human-nmg";
                default: return "major";
            }
        }

        public static bool Human(RunClass c) => c == RunClass.HumanMajor || c == RunClass.HumanNmg;

        public static bool NoMajors(RunClass c) => c == RunClass.Nmg || c == RunClass.HumanNmg;

        public static bool Allows(RunClass c, string category) => !NoMajors(c) || category == GlitchlessCategory || category == NmgCategory;
    }
}
