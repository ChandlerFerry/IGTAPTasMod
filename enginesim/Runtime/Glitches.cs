using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IGTAP.EngineSim
{
    /// <summary>A Full-TAS run class (docs/fulltas.md "Classes"): what a route may use and what ranks it. <c>major</c>: anything, by
    /// live time; <c>nmg</c>: no major-glitch span, by live time; <c>human-major</c> and <c>human-nmg</c>: the same, by score.</summary>
    public enum RunClass { Major, Nmg, HumanMajor, HumanNmg }

    /// <summary>A human class's score weights (fulltas.json's "human", a trace header's <c>fulltas.human</c>): seconds per major
    /// span, per nmg span and per spike-grace tick.</summary>
    public sealed class HumanWeights
    {
        public float Major = 60, Nmg = 20, Grace = 2;

        /// <summary>A trace header's <c>fulltas</c> object's "human" weights; the defaults for the ones it leaves out.</summary>
        public static HumanWeights Of(JsonElement fulltas)
        {
            var w = new HumanWeights();
            if (fulltas.ValueKind != JsonValueKind.Object || !fulltas.TryGetProperty("human", out JsonElement h) || h.ValueKind != JsonValueKind.Object) return w;
            float Read(string name, float fallback) => h.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;
            w.Major = Read("major", w.Major);
            w.Nmg = Read("nmg", w.Nmg);
            w.Grace = Read("grace", w.Grace);
            return w;
        }
    }

    /// <summary>One occurrence of an nmg or major tech in a run: the step it was seen on (<see cref="TechStep"/>), its input tick
    /// and its physics tick.</summary>
    public sealed class GlitchSpan
    {
        public string Tech, Category;
        public int Step, Tick;
        public long Physics;
    }

    /// <summary>What a run uses of the techs that aren't glitchless (<see cref="Glitches.Classify"/>).</summary>
    public sealed class GlitchReport
    {
        public List<GlitchSpan> Spans = new List<GlitchSpan>();
        public int Major, Nmg, GraceTicks;
        /// <summary>The physics tick of each spike-grace tick (<see cref="GraceTicks"/> of them).</summary>
        public List<long> GraceAt = new List<long>();
        /// <summary>The nmg and major techs nothing detects (no detector, no check of their own): a run may use them unseen.</summary>
        public List<string> Undetected = new List<string>();

        /// <summary>The score's penalty in seconds; without <paramref name="majors"/> the major spans count nothing.</summary>
        public float Penalty(HumanWeights w, bool majors = true) => (majors ? Major * w.Major : 0f) + Nmg * w.Nmg + GraceTicks * w.Grace;

        /// <summary>The part of the run after physics tick <paramref name="after"/> up to <paramref name="until"/> (inclusive).</summary>
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
            else Nmg++;
        }
    }

    /// <summary>
    /// The nmg and major techs a recorded run (<see cref="TechSteps"/>) uses, every occurrence: the tech's own check
    /// (<see cref="TechChecks.Find"/>) asked again from the step after each hit, else every span of its techs.json detector,
    /// one per (tech, step) and one for hits on consecutive steps; and its spike-grace ticks. Categories are techs.json's (the
    /// config folder's copy merged with the repo seed, <see cref="TechGrafts.Categories"/>).
    /// </summary>
    public static class Glitches
    {
        public const string MajorCategory = "major", NmgCategory = "nmg", GlitchlessCategory = "glitchless";

        static readonly Lazy<List<(string id, string category, string detector)>> Techs = new Lazy<List<(string, string, string)>>(() =>
            TechGrafts.Categories(Directory.GetCurrentDirectory()).Where(t => t.category == MajorCategory || t.category == NmgCategory).ToList());

        public static GlitchReport Classify(TechSteps run)
        {
            var report = new GlitchReport();
            List<TechStep> steps = run.Steps;
            var found = new List<GlitchSpan>();
            foreach (var (id, category, detector) in Techs.Value)
            {
                if (!TechChecks.Checkable(id, detector)) { report.Undetected.Add(id); continue; }
                // A hit on the step right after the last is the same occurrence going on (a slope slide, a clip's two steps in the
                // spikes): one span from its first step.
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

        /// <summary>The class is ranked by score (live time plus the penalty), not live time.</summary>
        public static bool Human(RunClass c) => c == RunClass.HumanMajor || c == RunClass.HumanNmg;

        /// <summary>The class refuses a route with a major span.</summary>
        public static bool NoMajors(RunClass c) => c == RunClass.Nmg || c == RunClass.HumanNmg;

        /// <summary>A tech of this techs.json category may be grafted in the class (an nmg class: glitchless and nmg only).</summary>
        public static bool Allows(RunClass c, string category) => !NoMajors(c) || category == GlitchlessCategory || category == NmgCategory;
    }
}
