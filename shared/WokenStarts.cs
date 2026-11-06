using System.Reflection;
using UnityEngine;

namespace IGTAPTasMod
{
    // Runs a woken upgradeBox Start at the modelled 1x frame (RULES.md exception 12); turbo restores fields after.
    internal static class WokenStarts
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly MethodInfo Start = typeof(upgradeBox).GetMethod("Start", Fields);
        static readonly FieldInfo Door = typeof(upgradeBox).GetField("doorToOpen", Fields);

        // The door boxes' Start (atom box reads OpenA2Door, upgradeBox.cs:260-267) can't be modelled: such a run ends.
        internal static string Unmodeled(upgradeBox box)
        {
            string why = null;
            if (box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && Door.GetValue(box) is PlatformMover door && door != null)
                why = "the atom box's door (upgradeBox.cs:264-267)";
            else if (box.globalUpgrade == globalStats.globalUpgradeSet.area2DoorOpen && !box.isActive) why = "its door (upgradeBox.cs:260-263)";
            return why == null ? null : "Unmodeled: " + box.name + "'s Start, the first since a run woke it, would move " + why;
        }

        internal static string Describe(upgradeBox box)
        {
            Transform parent = box.transform.parent;
            return (parent == null ? "" : parent.name + "/") + box.name + " " + box.TimesUsed + "/" + box.Cap + " base " + box.baseCap
                + " cost " + box.upgradeCost.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)
                + " base " + box.baseUpgradeCost.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + (box.isActive ? "" : " inactive");
        }

        internal static string Join(string list, string box) { return list.Length == 0 ? box : list + "; " + box; }
    }
}
