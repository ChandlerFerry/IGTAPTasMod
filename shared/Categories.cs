using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace IGTAPTasMod
{
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

    public enum CategoryScope
    {
        Range,
        All,
        // Targets scope: elsewhere it plays as a category the target already runs, duplicating that record (RULES.md 19).
        Targets,
    }

    public sealed class CategoryDef
    {
        public readonly Caps Caps;
        public readonly CategoryScope Scope;
        public readonly string[] Targets;
        public string Id { get { return Caps.Id; } }
        public string Label { get { return Caps.Label; } }
        public CategoryDef(string label, CategoryScope scope) { Caps = Caps.Parse(label); Scope = scope; Targets = new string[0]; }
        public CategoryDef(string label, params string[] targets) { Caps = Caps.Parse(label); Scope = CategoryScope.Targets; Targets = targets; }

        public bool RunsOn(string token, int minStep, int maxStep)
        {
            if (Scope == CategoryScope.All) return true;
            if (Scope == CategoryScope.Targets) return Listed(Targets, token);
            int step = Categories.LadderStep(Id);
            return step >= minStep && step <= maxStep;
        }

        static bool Listed(string[] targets, string token)
        {
            return Array.IndexOf(targets, token) >= 0 || token.StartsWith("tree") && Array.IndexOf(targets, "tree*") >= 0;
        }
    }

    public static class Categories
    {
        public static readonly string[] Ladder = { "none", "wj", "wjds", "wjdsdj", "wjdsdjbs" };

        // Orb/zip reach (RULES.md 19): game grouping or searched routes within 150 u; 11-15 use base maps; trees as bonus11.
        static readonly string[] OrbTargets =
        {
            "4", "6", "7", "8", "9", "10", "12", "14", "15",
            "bonus7", "bonus8", "bonus9", "bonus10", "bonus11", "bonus12", "bonus14", "bonus16", "tree*",
        };
        static readonly string[] ZipTargets =
        {
            "4", "7", "8", "10", "14", "15", "bonus4", "bonus9", "bonus10", "bonus11", "bonus12", "bonus13", "bonus14", "tree*",
        };
        // These targets' ends need the zip movers: only categories with ZIP run there, whatever their ladder (RULES.md 19).
        static readonly string[] ZipOnlyTargets = { "bonus4", "bonus12" };

        // Bonuses ending at an unlock box run no category (RULES.md 19); bonus17-21, 23 and unlockJumpDash.
        static readonly string[] NoCategoryTargets = { "bonus17", "bonus18", "bonus19", "bonus20", "bonus21", "bonus23", "unlockJumpDash" };

        public static bool IsTarget(string token) { return Array.IndexOf(NoCategoryTargets, token) < 0; }

        public static bool CanFinish(string token, Caps caps) { return caps.Zip || Array.IndexOf(ZipOnlyTargets, token) < 0; }

        public static readonly CategoryDef[] All =
        {
            new CategoryDef("NONE", CategoryScope.Range),
            new CategoryDef("WJ", CategoryScope.Range),
            new CategoryDef("WJ_DS", CategoryScope.Range),
            new CategoryDef("WJ_DS_DJ", CategoryScope.Range),
            new CategoryDef("WJ_DS_DJ_BS", CategoryScope.Range),
            new CategoryDef("WJ_DS_DJ_BS_ORB", OrbTargets),
            new CategoryDef("WJ_DS_DJ_BS_ZIP", ZipTargets),
            new CategoryDef("WJ_DS_BS_ZIP_OM", CategoryScope.All),
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

        public static CategoryDef Find(string idOrLabel)
        {
            return Caps.TryParse(idOrLabel, out Caps caps) && ById.TryGetValue(caps.Id, out CategoryDef def) ? def : null;
        }

        public static bool IsCategory(string idOrLabel) { return Find(idOrLabel) != null; }

        public static readonly (string name, string block)[] Directions = { ("forwards", "avoid-backwards"), ("backwards", "avoid-forwards") };

        public static int LadderStep(string id) { return Array.IndexOf(Ladder, id); }
    }
}
