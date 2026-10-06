using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace IGTAPTasMod
{
    /// <summary>
    /// A category's unlocks (README ### Rules 10-12). Its label lists them in a fixed order, each at most once:
    /// <c>WJ</c> wall jump, <c>DS</c>/<c>DS2</c>.. air dashes, <c>DJ</c>/<c>DJ2</c>.. air jumps, <c>BS</c> block swap on dash,
    /// <c>ORB</c> every refill orb, <c>ZIP</c> the zip movers, <c>OM</c> the omnidirectional dash; <c>NONE</c> when empty.
    /// One label per unlock set and back (<c>WJ_DS2_BS_ZIP_OM</c>); the id is the label lowercased without underscores
    /// (<c>wjds2bszipom</c>), the form file names, forced-paths.json and the HTTP API's "capabilities" use.
    /// </summary>
    public struct Caps : IEquatable<Caps>
    {
        public bool WallJump;
        public int Dashes;
        public int AirJumps;
        public bool BlockSwap;
        public bool Orbs;
        public bool Zip;
        public bool Omni;

        public bool Dash { get { return Dashes > 0; } }
        public bool DoubleJump { get { return AirJumps > 0; } }

        /// <summary>Route-name category part for regexes; <see cref="TryParse"/> decides whether it is a category.</summary>
        public const string LabelPattern = "[A-Z][A-Z0-9_]*";

        static readonly Regex IdGrammar = new Regex(
            "^(?<wj>wj)?(?:ds(?<ds>[2-9]|[1-9][0-9]+)?(?<dsOn>))?(?:dj(?<dj>[2-9]|[1-9][0-9]+)?(?<djOn>))?(?<bs>bs)?(?<orb>orb)?(?<zip>zip)?(?<om>om)?$",
            RegexOptions.CultureInvariant);

        public string Label { get { return Format("_", "WJ", "DS", "DJ", "BS", "ORB", "ZIP", "OM", "NONE"); } }
        public string Id { get { return Format("", "wj", "ds", "dj", "bs", "orb", "zip", "om", "none"); } }
        public override string ToString() { return Label; }

        string Format(string sep, string wj, string ds, string dj, string bs, string orb, string zip, string om, string none)
        {
            var parts = new List<string>(7);
            if (WallJump) parts.Add(wj);
            if (Dashes > 0) parts.Add(Dashes == 1 ? ds : ds + Dashes);
            if (AirJumps > 0) parts.Add(AirJumps == 1 ? dj : dj + AirJumps);
            if (BlockSwap) parts.Add(bs);
            if (Orbs) parts.Add(orb);
            if (Zip) parts.Add(zip);
            if (Omni) parts.Add(om);
            return parts.Count == 0 ? none : string.Join(sep, parts);
        }

        /// <summary>A canonical label (<c>WJ_DS2_OM</c>) or id (<c>wjds2om</c>); false for anything else, including the tokens
        /// out of order, <c>DS1</c>, and block swap or the omnidash without a dash.</summary>
        public static bool TryParse(string text, out Caps caps)
        {
            caps = default(Caps);
            if (string.IsNullOrEmpty(text)) return false;
            bool label = text.IndexOf('_') >= 0 || text == "NONE" || IsUpper(text);
            string id = label ? text.Replace("_", "").ToLowerInvariant() : text;
            if (id != "none")
            {
                Match m = IdGrammar.Match(id);
                if (!m.Success || id.Length == 0) return false;
                caps.WallJump = m.Groups["wj"].Success;
                caps.Dashes = !m.Groups["dsOn"].Success ? 0 : m.Groups["ds"].Success ? int.Parse(m.Groups["ds"].Value) : 1;
                caps.AirJumps = !m.Groups["djOn"].Success ? 0 : m.Groups["dj"].Success ? int.Parse(m.Groups["dj"].Value) : 1;
                caps.BlockSwap = m.Groups["bs"].Success;
                caps.Orbs = m.Groups["orb"].Success;
                caps.Zip = m.Groups["zip"].Success;
                caps.Omni = m.Groups["om"].Success;
                if ((caps.BlockSwap || caps.Omni) && !caps.Dash) return false;
            }
            return label ? caps.Label == text : caps.Id == text;
        }

        static bool IsUpper(string text)
        {
            foreach (char c in text) if (char.IsLower(c)) return false;
            return true;
        }

        public static Caps Parse(string text)
        {
            if (!TryParse(text, out Caps caps)) throw new FormatException("Not a category: '" + text + "'.");
            return caps;
        }

        /// <summary>Every unlock of this one is in <paramref name="other"/> (counts no higher): a narrower category.</summary>
        public bool IsSubsetOf(Caps other)
        {
            return (!WallJump || other.WallJump) && Dashes <= other.Dashes && AirJumps <= other.AirJumps
                && (!BlockSwap || other.BlockSwap) && (!Orbs || other.Orbs) && (!Zip || other.Zip) && (!Omni || other.Omni);
        }

        public bool Equals(Caps other)
        {
            return WallJump == other.WallJump && Dashes == other.Dashes && AirJumps == other.AirJumps && BlockSwap == other.BlockSwap
                && Orbs == other.Orbs && Zip == other.Zip && Omni == other.Omni;
        }
        public override bool Equals(object obj) { return obj is Caps c && Equals(c); }
        public override int GetHashCode() { return Id.GetHashCode(); }
        public static bool operator ==(Caps a, Caps b) { return a.Equals(b); }
        public static bool operator !=(Caps a, Caps b) { return !a.Equals(b); }
    }

    /// <summary>Where a category is run.</summary>
    public enum CategoryScope
    {
        /// <summary>A step of <see cref="Categories.Ladder"/>: run where it lies in the target's minCapabilities-maxCapabilities
        /// range (courses.json).</summary>
        Range,
        /// <summary>Run on every course, bonus and transition.</summary>
        All,
        /// <summary>Run only on <see cref="CategoryDef.Targets"/>: where the refill orbs or zip movers it adds are in reach.
        /// Anywhere else it plays exactly as a category the target already runs, and would only duplicate its record.</summary>
        Targets,
    }

    public sealed class CategoryDef
    {
        public readonly Caps Caps;
        public readonly CategoryScope Scope;
        /// <summary>A <see cref="CategoryScope.Targets"/> category's targets by courses.json token (a course's id, a bonus's or
        /// transition's name); empty for the other scopes.</summary>
        public readonly string[] Targets;
        public string Id { get { return Caps.Id; } }
        public string Label { get { return Caps.Label; } }
        public CategoryDef(string label, CategoryScope scope) { Caps = Caps.Parse(label); Scope = scope; Targets = new string[0]; }
        public CategoryDef(string label, params string[] targets) { Caps = Caps.Parse(label); Scope = CategoryScope.Targets; Targets = targets; }

        /// <summary>It is run on the target with this courses.json token and ladder range (README ### Rules 17-19, 26).</summary>
        public bool RunsOn(string token, int minStep, int maxStep)
        {
            if (Scope == CategoryScope.All) return true;
            if (Scope == CategoryScope.Targets) return Listed(Targets, token);
            int step = Categories.LadderStep(Id);
            return step >= minStep && step <= maxStep;
        }

        /// <summary>The token is in the list; "tree*" stands for every tree transition (tree&lt;L&gt;to&lt;C&gt;).</summary>
        static bool Listed(string[] targets, string token)
        {
            return Array.IndexOf(targets, token) >= 0 || token.StartsWith("tree") && Array.IndexOf(targets, "tree*") >= 0;
        }
    }

    /// <summary>
    /// The categories that exist, in display order (the batch solver's columns). Only these can be solved, saved or judged;
    /// any other unlock set is still a valid <see cref="Caps"/> (a Full-TAS window's, a tech's requirement) but no category.
    /// Adding a category is one line here, plus a capture of its start state (tools/enginesim-capture.py start).
    /// </summary>
    public static class Categories
    {
        /// <summary>The unlocks in the order the game sells them: courses.json's minCapabilities/maxCapabilities are steps of it.</summary>
        public static readonly string[] Ladder = { "none", "wj", "wjds", "wjdsdj", "wjdsdjbs" };

        /// <summary>Targets with refill orbs in reach, and with zip movers (README ### Rules 19): the game's own grouping (an orb
        /// or mover under <c>Courses/course N</c>, <c>OvergrowthStuff/cN</c>) or the target's searched runs and routes (enginesim
        /// <c>map</c>) coming within 150 u of one; a target never searched keeps one only if a straight-line visit at 3000 u/s
        /// fits inside its ladder record. Courses 11-15 count their base course's maps, with the overgrown objects. The tree
        /// transitions start at bonus11's checkpoint and count as it does.</summary>
        static readonly string[] OrbTargets =
        {
            "4", "6", "7", "8", "9", "10", "12", "14", "15",
            "bonus7", "bonus8", "bonus9", "bonus10", "bonus11", "bonus12", "bonus14", "bonus16", "tree*",
        };
        static readonly string[] ZipTargets =
        {
            "4", "7", "8", "10", "14", "15", "bonus4", "bonus9", "bonus10", "bonus11", "bonus12", "bonus13", "bonus14", "tree*",
        };
        /// <summary>Targets whose end needs the zip movers: they run only the categories with them, whatever their ladder range.</summary>
        static readonly string[] ZipOnlyTargets = { "bonus4", "bonus12" };

        /// <summary>courses.json bonuses that run no category (the batch solver has no row for them): their end is an unlock
        /// box, not a bonus stretch (bonus17 refill orbs, bonus18 clone dust, bonus19 buy max, bonus20/21 the 1x and ∞x
        /// Start a VMAN speedrun boxes, bonus23 block swap on dash, unlockJumpDash the jump and dash refresh orbs at The
        /// Tree level 10). Full-TAS <c>reward:</c> goals still name them.</summary>
        static readonly string[] NoCategoryTargets = { "bonus17", "bonus18", "bonus19", "bonus20", "bonus21", "bonus23", "unlockJumpDash" };

        /// <summary>The target with this courses.json token runs categories at all.</summary>
        public static bool IsTarget(string token) { return Array.IndexOf(NoCategoryTargets, token) < 0; }

        /// <summary>The category can finish the target at all (<see cref="ZipOnlyTargets"/>).</summary>
        public static bool CanFinish(string token, Caps caps) { return caps.Zip || Array.IndexOf(ZipOnlyTargets, token) < 0; }

        public static readonly CategoryDef[] All =
        {
            new CategoryDef("NONE", CategoryScope.Range),
            new CategoryDef("WJ", CategoryScope.Range),
            new CategoryDef("WJ_DS", CategoryScope.Range),
            new CategoryDef("WJ_DS_DJ", CategoryScope.Range),
            new CategoryDef("WJ_DS_DJ_BS", CategoryScope.Range),
            // Without an orb in reach it plays as WJ_DS_DJ_BS.
            new CategoryDef("WJ_DS_DJ_BS_ORB", OrbTargets),
            // Without a zip mover in reach it plays as WJ_DS_DJ_BS.
            new CategoryDef("WJ_DS_DJ_BS_ZIP", ZipTargets),
            // No other category drops the air jump or adds a second dash, so these two differ on every target.
            new CategoryDef("WJ_DS_BS_ZIP_OM", CategoryScope.All),
            // Without an orb in reach it plays as WJ_DS_DJ_BS_ORB_ZIP_OM.
            new CategoryDef("WJ_DS_DJ_BS_ZIP_OM", OrbTargets),
            new CategoryDef("WJ_DS2_BS_ZIP_OM", CategoryScope.All),
            new CategoryDef("WJ_DS_DJ_BS_ORB_ZIP_OM", CategoryScope.All),
        };

        static readonly Dictionary<string, CategoryDef> ById = BuildIndex();

        static Dictionary<string, CategoryDef> BuildIndex()
        {
            var index = new Dictionary<string, CategoryDef>(StringComparer.Ordinal);
            foreach (CategoryDef def in All) index.Add(def.Id, def);
            return index;
        }

        /// <summary>The category with this id or label, or null.</summary>
        public static CategoryDef Find(string idOrLabel)
        {
            return Caps.TryParse(idOrLabel, out Caps caps) && ById.TryGetValue(caps.Id, out CategoryDef def) ? def : null;
        }

        public static bool IsCategory(string idOrLabel) { return Find(idOrLabel) != null; }

        /// <summary>
        /// A two-way course's directions (a course with both blocks in forced-paths.json, Courses 1-3): a route of a direction
        /// never cuts that block's boxes. Its best is a direction record, <c>&lt;course&gt;_&lt;CAT&gt; &lt;direction&gt; &lt;time&gt;</c>,
        /// beside the category record, so a Full-TAS run reaching the course in another state has both to pick from.
        /// </summary>
        public static readonly (string name, string block)[] Directions = { ("forwards", "avoid-backwards"), ("backwards", "avoid-forwards") };

        /// <summary>The step of <see cref="Ladder"/> (-1 when the id isn't one).</summary>
        public static int LadderStep(string id) { return Array.IndexOf(Ladder, id); }
    }
}
