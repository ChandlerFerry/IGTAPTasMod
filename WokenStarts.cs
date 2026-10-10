using System.Reflection;
using UnityEngine;

namespace IGTAPTasMod
{
    /// <summary>
    /// The <c>Start</c> of an <c>upgradeBox</c> a run wakes for the first time (a tree segment's box, a zone load turning a zone on
    /// in a new game): Unity runs it before the box's first Update, but a turbo run is one Unity frame, so its real Start comes
    /// after the run, and the offline engine has no Unity. Both run the game's own Start (<see cref="Start"/>) at the modelled
    /// frame 1x runs it in (README ### Rules, exception 12: TurboRunner.StartPass, which puts the box's fields back around Unity's
    /// later real Start; the engine's Economy.StartPass, where updateCostTextDisplay and setBoxText, text only, are cut). Shared
    /// source: the plugin and enginesim/Runtime (linked) compile it against their own game assembly.
    /// </summary>
    internal static class WokenStarts
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        /// <summary>upgradeBox.Start (upgradeBox.cs:199-298).</summary>
        internal static readonly MethodInfo Start = typeof(upgradeBox).GetMethod("Start", Fields);
        static readonly FieldInfo Door = typeof(upgradeBox).GetField("doorToOpen", Fields);

        /// <summary>
        /// Why the box's Start can't be run at the modelled frame, or null. Its door branches: the atom box's reads the machine's
        /// PlayerPrefs "OpenA2Door" (upgradeBox.cs:264-267), which the engine doesn't hold, and both jump the door
        /// (PlatformMover.JumpToNextState, upgradeBox.cs:260-267) at the modelled frame. (Turbo puts the door's stop back around
        /// Unity's later real Start, TurboRunner.AfterDrawingStart, for a box bought out after its modelled Start.)
        /// </summary>
        internal static string Unmodeled(upgradeBox box)
        {
            string why = null;
            if (box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && Door.GetValue(box) is PlatformMover door && door != null)
                why = "the atom box's door (upgradeBox.cs:264-267)";
            else if (box.globalUpgrade == globalStats.globalUpgradeSet.area2DoorOpen && !box.isActive) why = "its door (upgradeBox.cs:260-263)";
            return why == null ? null : "Unmodeled: " + box.name + "'s Start, the first since a run woke it, would move " + why;
        }

        /// <summary>A box as the economy rows list it (<c>treeBoxes</c>, joined by "; "): its segment and name, buys/cap, base cap, cost,
        /// base cost, active.</summary>
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
