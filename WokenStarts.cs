using System.Reflection;
using UnityEngine;

namespace IGTAPTasMod
{
    /// <summary>
    /// The <c>Start</c> of an <c>upgradeBox</c> a run wakes for the first time (a zone load turning zone 2 on in a new game):
    /// Unity runs it before the box's first Update, but a turbo run is one Unity frame, so its real Start comes after the run,
    /// and the offline engine has no Start at all. Neither models it; a woken box whose Start would change gameplay state
    /// ends the run instead (TurboRunner.StartPass, the engine's Economy.StartPass). Shared source: the plugin and
    /// enginesim/Runtime (linked) compile it against their own game assembly.
    /// </summary>
    internal static class WokenStarts
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo Course = typeof(upgradeBox).GetField("course", Fields), Tree = typeof(upgradeBox).GetField("upgradeTree", Fields),
            Door = typeof(upgradeBox).GetField("doorToOpen", Fields), Reference = typeof(upgradeBox).GetField("ObjectReference", Fields),
            Local = typeof(upgradeBox).GetField("localUpgradeScript", Fields);

        /// <summary>
        /// Why the box's Start (upgradeBox.cs:199-298) would change what a run reads, or null when it leaves it as it is: its cap,
        /// cost, tree, visibility, statue text, door, the Vman omnidash box, its key prompt and its local upgrades.
        /// <paramref name="openA2Door"/>: the machine's PlayerPrefs hold "OpenA2Door".
        /// </summary>
        internal static string Unmodeled(upgradeBox box, bool openA2Door)
        {
            var course = Course.GetValue(box) as courseScript;
            string why = null;
            if (box.baseCap != box.Cap || System.Math.Round(box.Cap * box.baseCapMult) != box.Cap) why = "its cap";
            else if (course != null || (box.init ? box.baseUpgradeCost != box.upgradeCost : box.TimesUsed != 0 || box.upgradeCost != box.baseUpgradeCost))
                why = "its cost";
            else if (Tree.GetValue(box) is TreeController tree && tree != null && box.TimesUsed > 0) why = "its tree's growth";
            else if (box.Cap <= box.TimesUsed || !box.visible) why = "its visibility";
            else if (!box.isActive && (box.globalUpgrade == globalStats.globalUpgradeSet.unlockPrestige || box.globalUpgrade == globalStats.globalUpgradeSet.area2DoorOpen))
                why = "its statue text or door";
            else if (box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && Door.GetValue(box) is PlatformMover door && door != null
                && (box.TimesUsed != 0 || openA2Door)) why = "its door";
            else if (box.upgrade == localUpgrades.localUpgradeSet.Movement && box.movementUpgrade == upgradeBox.movementUpgrades.freeVmanOmnidash)
                why = "the Vman omnidash box's visibility";
            else if (Reference.GetValue(box) is GameObject prompt && prompt != null && prompt.GetComponent<KeypromptSetter>() != null
                && prompt.activeSelf != (box.upgrade == localUpgrades.localUpgradeSet.Movement)) why = "its key prompt";
            else if (!ReferenceEquals(Local.GetValue(box), box.GetComponentInParent<localUpgrades>())) why = "its local upgrades";
            return why == null ? null : "Unmodeled: " + box.name + "'s Start, the first since a run woke it, would change " + why;
        }
    }
}
