using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    /// <summary>
    /// The Tree (zone 2, <c>treeNumber</c> 1) at a level, as a game load at that level leaves it (README ### Rules 21):
    /// level L is the plant and the first L tree boxes in segment order bought once each (boxes 0-14, 16-27; TreePart15
    /// has none). <c>TreeController.load</c> sets each box's <c>TimesUsed</c> and calls <c>instaGrowSegments</c>; a box bought
    /// out (<c>Cap ≤ TimesUsed</c>) is deactivated as its <c>Start</c> does, an unbought one is the game's
    /// <c>reactivateAndReset</c>. Segments above the level go off as <c>TreeController.Awake</c> leaves them. No unlock is
    /// written: the boxes' global upgrades stay the category's.
    /// </summary>
    internal static class TreeLevel
    {
        static readonly AccessTools.FieldRef<TreeController, TreeSegmentActivationComponent[]> TreeSegments =
            AccessTools.FieldRefAccess<TreeController, TreeSegmentActivationComponent[]>("TreeSegments");
        static readonly AccessTools.FieldRef<TreeSegmentActivationComponent, bool> AutoTriggerNext =
            AccessTools.FieldRefAccess<TreeSegmentActivationComponent, bool>("autoTriggerNext");
        static readonly AccessTools.FieldRef<TreeSegmentActivationComponent, int> NextAuto =
            AccessTools.FieldRefAccess<TreeSegmentActivationComponent, int>("NextSegmentToAutoTriggerIndex");
        static readonly AccessTools.FieldRef<upgradeBox, int[]> SegmentsToTrigger =
            AccessTools.FieldRefAccess<upgradeBox, int[]>("SegmentsToTrigger");
        static readonly System.Reflection.MethodInfo Deactivate = AccessTools.Method(typeof(upgradeBox), "deactivate");

        internal static TreeController TheTree()
        {
            return UnityEngine.Object.FindObjectsByType<TreeController>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(t => t != null && t.treeNumber == 1);
        }

        /// <summary>Each segment's box, in segment order (null for a boxless segment).</summary>
        static upgradeBox[] Boxes(TreeController tree)
        {
            return TreeSegments(tree).Select(s => s == null || s.box == null ? null : s.box.GetComponent<upgradeBox>()).ToArray();
        }

        /// <summary>The boxes level L has bought: the first L boxes in segment order.</summary>
        static HashSet<upgradeBox> Bought(TreeController tree, int level)
        {
            return new HashSet<upgradeBox>(Boxes(tree).Where(b => b != null).Take(level));
        }

        internal static int[] Segments(TreeController tree, int level)
        {
            TreeSegmentActivationComponent[] segments = TreeSegments(tree);
            var grown = new SortedSet<int>();
            var queue = new Queue<int>(SegmentsToTrigger(tree.plantTreeBox) ?? new int[0]);
            foreach (upgradeBox box in Bought(tree, level))
                foreach (int s in SegmentsToTrigger(box) ?? new int[0]) queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int s = queue.Dequeue();
                if (s < 0 || s >= segments.Length || !grown.Add(s)) continue;
                if (AutoTriggerNext(segments[s])) queue.Enqueue(NextAuto(segments[s]));
            }
            return grown.ToArray();
        }

        internal static bool At(TreeController tree, int level)
        {
            if (tree == null) return true;
            int[] want = Segments(tree, level);
            TreeSegmentActivationComponent[] segments = TreeSegments(tree);
            HashSet<upgradeBox> bought = Bought(tree, level);
            return tree.plantTreeBox.TimesUsed > 0 && new SortedSet<int>(tree.GrownSegments).SetEquals(want)
                && Enumerable.Range(0, segments.Length).All(i => segments[i].gameObject.activeSelf == want.Contains(i))
                && Boxes(tree).Where(b => b != null).All(b => (b.TimesUsed > 0) == bought.Contains(b));
        }

        internal static bool Apply(TreeController tree, int level)
        {
            if (tree == null || At(tree, level)) return false;
            int[] want = Segments(tree, level);
            TreeSegmentActivationComponent[] segments = TreeSegments(tree);
            HashSet<upgradeBox> bought = Bought(tree, level);
            // ponytail: a growth a route started (a tree box bought mid-run) and this cut short keeps its trunk pose; a
            // scene reload resets it.
            for (int i = 0; i < segments.Length; i++) if (!want.Contains(i)) segments[i].gameObject.SetActive(false);
            tree.GrownSegments.Clear();
            tree.GrowingInProgress = false;
            Buy(tree.plantTreeBox, true);
            foreach (upgradeBox box in Boxes(tree).Where(b => b != null)) Buy(box, bought.Contains(box));
            tree.instaGrowSegments(want);
            Physics2D.SyncTransforms();
            return true;
        }

        static void Buy(upgradeBox box, bool bought)
        {
            if (!bought) { if (box.TimesUsed != 0 || !box.isActive) box.reactivateAndReset(); return; }
            if (box.TimesUsed == 0) box.TimesUsed = 1;
            if (box.Cap <= box.TimesUsed && box.isActive) Deactivate.Invoke(box, null);
        }

        internal sealed class Saved
        {
            public List<int> grown;
            public bool[] on;
            public (upgradeBox box, int used, bool active)[] boxes;
        }

        internal static Saved Keep(TreeController tree)
        {
            return new Saved
            {
                grown = new List<int>(tree.GrownSegments),
                on = TreeSegments(tree).Select(s => s.gameObject.activeSelf).ToArray(),
                boxes = Boxes(tree).Where(b => b != null).Prepend(tree.plantTreeBox).Select(b => (b, b.TimesUsed, b.isActive)).ToArray(),
            };
        }

        internal static void Restore(TreeController tree, Saved saved)
        {
            TreeSegmentActivationComponent[] segments = TreeSegments(tree);
            for (int i = 0; i < segments.Length; i++) segments[i].gameObject.SetActive(saved.on[i]);
            tree.GrownSegments.Clear();
            tree.GrownSegments.AddRange(saved.grown);
            foreach (var (box, used, active) in saved.boxes)
            {
                if (box == null) continue;
                if (active && !box.isActive) box.reactivateAndReset();
                box.TimesUsed = used;
                if (!active && box.isActive) Deactivate.Invoke(box, null);
            }
            Physics2D.SyncTransforms();
        }
    }
}
