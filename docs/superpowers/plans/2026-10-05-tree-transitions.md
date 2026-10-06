# Tree transitions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 95 transitions `tree<L>to<C>` from the checkpoint by the atom room to Courses 6-10, each judged with The Tree
set to level L the game's load way, then solved offline and pruned.

**Architecture:** `courses.json` targets get a `tree` level. The plugin sets The Tree at every quick restart onto one
(`TreeLevel.cs`, called beside `EnsureArea1State`), reopens the Area 2 door after the movers' reset, and puts the save's
tree back before a save. The offline engine needs nothing new: it builds its world from the captured scene. Categories
list the tree transitions as orb and zip targets (bonus11's start).

**Tech Stack:** C# BepInEx plugin (Harmony, Unity 6), shared `CourseStart.cs` linked into `enginesim`, Python tools.

**Spec:** `docs/superpowers/specs/2026-10-05-tree-transitions-design.md`

## Global Constraints

- README `### Rules` 39: setup writes only as a game load makes them (`TreeController.load`: box `TimesUsed`,
  `instaGrowSegments`; `upgradeBox.reactivateAndReset`, `deactivate`; `PlatformMover` stops).
- Rules 10/12/16: the tree setup writes no unlock (`globalUpgradeDict`, `Movement` fields). With "Don't touch my
  unlocks" on, it still runs (it writes no unlock).
- Rule 25/42: no autosave while the tree differs from the save's; the save's tree is put back first.
- Rule 41: never edit save files. `courses.json` is the TAS's config, not the game's save.
- Rule 49/50: if offline differs from turbo, fix `enginesim`.
- AGENTS.md: never `taskkill /IM IGTAP.EngineSim.exe`; a build while another engine runs goes to a private folder.
- No commits unless the user asks: `Solver.cs`, `CourseStart.cs`, `Categories.cs` and others carry the user's
  uncommitted work. The "Commit" steps are checkpoints: run `git diff --stat` and move on.
- Target levels: `0, 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 22, 27`; courses `6, 7, 8, 9, 10`.
- Ids: `1000 + 10 * L + C` (tree27to6 = 1276, tree0to10 = 1010). Start `(22643.9, -364.685)`.
- The Tree is the `TreeController` with `treeNumber == 1`; The Tree 2 (`treeNumber == 2`) is never touched.

## Review Focus

1. A 1x playback of a tree target in a session that never had the level's segments on: a woken Area 2 door box's
   `Start` toggles the door after the reopen. Expected: the door is open at tick 0 from level 8 (Task 2 Step 3 defers
   the start a frame; Task 5 Step 3 plays one at 1x right after a restart of the game).
2. Leaving a tree target (a normal course, the main menu, an autosave): the save's tree comes back before the save is
   written. Expected: the save's segment count, boxes and path flag (Task 5 Step 2).
3. Going down a level (tree27to6 then tree0to6): segments above the level turn off and their boxes go back to unbought.
   Expected: exactly the level's segments (Task 5 Step 2 runs high then low).
4. A cached turbo start from another level: the bridge reuses `_startStates[key]` across targets of the same key.
   Expected: a target's start is recaptured when the tree isn't at its level (Task 2 Step 4).
5. The categories: each tree target runs `WJ_DS_DJ_BS` and the 6 outside-ladder ones (`/api/state` `categories` per
   course) (Task 3 Step 3).

---

### Task 1: `TreeLevel.cs`: set The Tree to a level, keep and put back the save's

**Files:**
- Create: `TreeLevel.cs`
- Modify: `IGTAPTasMod.csproj` only if it lists sources explicitly (check `grep -n "Compile Include" IGTAPTasMod.csproj`;
  SDK projects glob `*.cs`, then nothing to add)

**Interfaces:**
- Produces:
  - `static class TreeLevel`
  - `TreeController TheTree()`: the controller with `treeNumber == 1`, or null.
  - `int[] Segments(TreeController tree, int level)`: the segments the plant and the first `level` boxes (segment order,
    boxless segments skipped) grow, `autoTriggerNext` followed.
  - `bool Apply(TreeController tree, int level)`: sets segments and boxes; true when anything changed.
  - `bool At(TreeController tree, int level)`: the tree is exactly at that level.
  - `sealed class Saved` and `Saved Keep(TreeController tree)`, `void Restore(TreeController tree, Saved saved)`.

- [ ] **Step 1: Write `TreeLevel.cs`**

```csharp
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
```

- [ ] **Step 2: Build**

Run: `dotnet build .\IGTAPTasMod.csproj -c Release -o $env:TEMP\igtap-tree-build` (a private folder: the game holds the
deployed DLL).
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add TreeLevel.cs
git commit -m "TreeLevel: The Tree at a level, as a game load leaves it"
```

### Task 2: Plugin hooks: `tree` on a target, set at its start, the door, the save's tree back

**Files:**
- Modify: `Solver.cs` (`CourseDef` at :21-50; `EnsureArea1State`/`WantedArea1State`/`RestoreArea1State` at :331-367;
  `CourseLoaded` at :772; the course list's `hint` at :877)
- Modify: `TasPlugin.Savestates.cs:82` (`RestoreRouteStart`)
- Modify: `TasPlugin.Patches.cs:20-30` (`BlockSharedSave`), `:177-215` (the pending silent macro and replay)
- Modify: `TasPlugin.Session.cs:583-603` (`ResetZipMovers`)
- Modify: `CourseStart.cs:88-115` (`AssumesAtomDoor`, `OpenAtomDoor`)
- Modify: `EngineSimBridge.cs:90`

**Interfaces:**
- Consumes: `TreeLevel.TheTree`, `Apply`, `At`, `Keep`, `Restore`, `Saved` (Task 1).
- Produces: `CourseDef.tree` (`int?`), `void EnsureTree(CourseDef def)`, `bool TreeReady(CourseDef def)`,
  `void RestoreTree()`, `CourseStart.OpenArea2Door(IEnumerable<upgradeBox>)`, `CourseStart.IsTreeTransition(int id)`.

- [ ] **Step 1: `CourseDef.tree` and the tree state in `Solver.cs`**

In `CourseDef` after `public bool overgrown;`:

```csharp
            /// <summary>The Tree's level on this target (README ### Rules 21: the tree transitions); null: the save's own.</summary>
            public int? tree;
```

After `RestoreArea1State` (:367), add:

```csharp
        TreeLevel.Saved _savedTree;
        TreeController _savedTreeOwner;
        int _treeChangedFrame = -1;

        /// <summary>The Tree at the target's level (<see cref="TreeLevel"/>), the save's kept first; a target without one
        /// puts the save's back. From level 23 the OmniDash box's buy has opened the Zone 2→1 path (upgradeBox.cs:825-830).</summary>
        void EnsureTree(CourseDef def)
        {
            TreeController tree = TreeLevel.TheTree();
            if (def == null || tree == null) return;
            if (def.tree == null) { RestoreTree(); return; }
            if (_savedTreeOwner != tree) { _savedTree = TreeLevel.Keep(tree); _savedPathOpen = Singleton<globalStats>.Instance.Zone2toZone1PathOpen; _savedTreeOwner = tree; }
            if (TreeLevel.Apply(tree, def.tree.Value)) { _treeChangedFrame = Time.frameCount; Logger.LogInfo("The Tree at level " + def.tree + " for " + def.Token + "."); }
            SetPath(def.tree.Value >= 23);
        }

        bool _savedPathOpen;

        static void SetPath(bool open)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if (stats == null || stats.Zone2toZone1PathOpen == open) return;
            stats.Zone2toZone1PathOpen = open;
            foreach (Zone2to1TransitionController path in FindObjectsByType<Zone2to1TransitionController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                path.updateState(open);
        }

        /// <summary>The tree is at the target's level and a frame has passed since it changed, so every box it woke has run
        /// its Start (the Area 2 door box's toggles the door, upgradeBox.cs:260) before a start sets the door.</summary>
        bool TreeReady(CourseDef def)
        {
            TreeController tree = TreeLevel.TheTree();
            return def?.tree == null || tree == null || TreeLevel.At(tree, def.tree.Value) && Time.frameCount > _treeChangedFrame;
        }

        bool TreeSaved { get { return _savedTreeOwner != null && _savedTreeOwner == TreeLevel.TheTree(); } }

        void RestoreTree()
        {
            if (TreeSaved)
            {
                TreeLevel.Restore(_savedTreeOwner, _savedTree);
                SetPath(_savedPathOpen);
                _treeChangedFrame = Time.frameCount;
                Logger.LogInfo("The Tree back to the save's.");
            }
            _savedTree = null;
            _savedTreeOwner = null;
        }
```

In `WantedArea1State` after `if (def.overgrown) return ...Overgrown;`:

```csharp
            if (def.tree >= 23) return globalStats.area1states.Overgrown;
```

In `CourseLoaded` (:772) append `&& TreeReady(target)` to `parked`.

In the course list (:877) put the tree first in the hint chain:

```csharp
                hint = c.tree != null ? "From the checkpoint by the atom room to Course " + c.toCourse + ", The Tree at level " + c.tree + "."
                    : c.IsBonus ? BonusHint(c) : c.overgrown ? ... (unchanged)
```

- [ ] **Step 2: Start and save hooks**

`TasPlugin.Savestates.cs:82`, after `EnsureArea1State(named);`:

```csharp
            EnsureTree(named);
```

`TasPlugin.Patches.cs` `BlockSharedSave`: replace `if (Instance.SavedArea1State == null) return true;` with
`if (Instance.SavedArea1State == null && !Instance.TreeSaved) return true;` and after `Instance.RestoreArea1State();` add
`Instance.RestoreTree();`.

`EngineSimBridge.cs:90`: `if (!ZoneReady(_solverTarget) || !Area1StateReady(_solverTarget) || !TreeReady(_solverTarget)) _startStates.Remove(key);`

- [ ] **Step 3: 1x starts wait a frame after the tree changes**

`TasPlugin.Patches.cs`, at the top of the `_pendingSilentMacro` block (:177) and of the `_pendingReplay` block (:200),
before anything else inside each `if`:

Silent block (first lines inside `if (Instance._pendingSilentMacro != null && ...)`, before the macro is taken):

```csharp
                CourseDef treeCourse = Instance.CourseOfRoute(Instance._pendingSilentMacro.name);
                Instance.EnsureTree(treeCourse);
                if (!Instance.TreeReady(treeCourse)) return true;
```

Replay block (first lines inside `if (Instance._pendingReplay != null && ...)`):

```csharp
                CourseDef treeCourse = Instance.CourseOfRoute(Instance._pendingReplay.name);
                Instance.EnsureTree(treeCourse);
                if (!Instance.TreeReady(treeCourse)) return true;
```

Both keep their macro pending until the next physics step, by which the boxes the tree woke have run their Start.

- [ ] **Step 4: Doors in `CourseStart.cs` and `ResetZipMovers`**

`CourseStart.cs`: replace `AssumesAtomDoor` and the body of `OpenAtomDoor` with:

```csharp
        public static bool AssumesAtomDoor(int course) { return course >= 6 && course <= 15 || course == 111 || IsTreeTransition(course); }

        /// <summary>A tree transition (courses.json id 1000 + 10 × level + course): from bonus11's checkpoint by the atom room.</summary>
        public static bool IsTreeTransition(int course) { return course >= 1000 && course < 2000; }

        public static bool OpenAtomDoor(IEnumerable<upgradeBox> boxes) { return OpenDoors(boxes, IsAtomBox); }

        /// <summary>The Area 2 door (TreePart7's box, <c>area2DoorOpen</c>) open, as the bought box's Start leaves it.</summary>
        public static bool OpenArea2Door(IEnumerable<upgradeBox> boxes)
        {
            return OpenDoors(boxes, b => b != null && b.globalUpgrade == globalStats.globalUpgradeSet.area2DoorOpen && DoorToOpen.GetValue(b) is PlatformMover d && d != null);
        }

        static bool OpenDoors(IEnumerable<upgradeBox> boxes, System.Func<upgradeBox, bool> which)
        {
            bool moved = false;
            foreach (upgradeBox box in boxes)
            {
                if (!which(box) || !(DoorToOpen.GetValue(box) is PlatformMover door)
                    || !(MoverPositions.GetValue(door) is System.Array stops) || stops.Length < 2 || (int)MoverIndex.GetValue(door) != 0) continue;
                object open = stops.GetValue(1);
                MoverIndex.SetValue(door, 1);
                door.transform.localPosition = (Vector2)open.GetType().GetField("position").GetValue(open);
                moved = true;
            }
            if (moved) Physics2D.SyncTransforms();
            return moved;
        }
```

(Keep `OpenAtomDoor`'s doc comment on it.)

`TasPlugin.Session.cs` `ResetZipMovers`: replace the `assumed` line and add the Area 2 door after the atom door:

```csharp
            bool assumed = course != null && (!course.IsTransition || course.tree != null) && CourseStart.AssumesAtomDoor(course.id);
            if (assumed || PlayerPrefs.HasKey("OpenA2Door") || Array.Exists(boxes, b => CourseStart.IsAtomBox(b) && b.TimesUsed != 0))
                CourseStart.OpenAtomDoor(boxes);
            // The Area 2 door as the tree's level leaves it: from level 8 its box is bought (README ### Rules 21).
            if (course?.tree >= 8) CourseStart.OpenArea2Door(boxes);
```

- [ ] **Step 5: Build the plugin and the engine (private folders)**

Run: `dotnet build .\IGTAPTasMod.csproj -c Release -o $env:TEMP\igtap-tree-build`
Run: `dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release -o $env:TEMP\igtap-tree-engine`
Expected: both `Build succeeded`, 0 errors (the engine links `CourseStart.cs`).

- [ ] **Step 6: Commit**

```bash
git add Solver.cs TasPlugin.Savestates.cs TasPlugin.Patches.cs TasPlugin.Session.cs CourseStart.cs EngineSimBridge.cs
git commit -m "Tree transitions: The Tree at a target's level at its start, the save's back before a save"
```

### Task 3: Targets: `courses.json` and the category registry

**Files:**
- Create: `tools/tree-transitions.py`
- Modify: `Categories.cs:131-136` (`RunsOn`'s target match), `:155-163` (`OrbTargets`, `ZipTargets`)

**Interfaces:**
- Consumes: `CourseDef.tree` (Task 2), `CourseStart.IsTreeTransition` id range (Task 2).
- Produces: targets `tree<L>to<C>` in `courses.json`.

- [ ] **Step 1: Write `tools/tree-transitions.py`**

```python
"""The tree transitions (docs/superpowers/specs/2026-10-05-tree-transitions-design.md): from the checkpoint by the atom
room (bonus11's start) to Courses 6-10, The Tree at each level that changes the world. Drops 6to10.

  tree-transitions.py            what it would write
  tree-transitions.py --write    write courses.json (a .bak-tree copy first)
  tree-transitions.py --drop L C remove tree<L>to<C> (a target no category can finish)
"""
import json, shutil, sys

from gamepaths import CONFIG

COURSES = CONFIG / "courses.json"
# Each group of levels whose boxes change nothing in the world, by its highest (the spec's table).
LEVELS = [0, 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 22, 27]
START = (22643.9, -364.685)


def entry(level, course):
    return {"id": 1000 + 10 * level + course, "name": f"tree{level}to{course}", "x": START[0], "y": START[1],
            "endX": 0.0, "endY": 0.0, "endW": 0.0, "endH": 0.0, "from": f"tree{level}", "to": str(course),
            "toCourse": course, "minCapabilities": "wjdsdjbs", "maxCapabilities": None, "overgrown": False, "tree": level}


def main():
    courses = json.load(open(COURSES, encoding="utf-8-sig"))
    if len(sys.argv) > 1 and sys.argv[1] == "--drop":
        name = f"tree{sys.argv[2]}to{sys.argv[3]}"
        kept = [c for c in courses if c.get("name") != name]
    else:
        kept = [c for c in courses if c.get("name") != "6to10" and not 1000 <= c["id"] < 2000]
        kept += [entry(level, course) for level in reversed(LEVELS) for course in range(6, 11)]
    print(f"{len(courses)} -> {len(kept)} targets; removed: {sorted({c.get('name') for c in courses} - {c.get('name') for c in kept})}")
    if "--write" in sys.argv or sys.argv[1:2] == ["--drop"]:
        shutil.copy(COURSES, COURSES.with_suffix(".json.bak-tree"))
        json.dump(kept, open(COURSES, "w", encoding="utf-8"), indent=2)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Dry run, then write**

Run: `python tools/tree-transitions.py`
Expected: `46 -> 140 targets; removed: ['6to10']`
Run: `python tools/tree-transitions.py --write`

- [ ] **Step 3: Tree transitions are orb and zip targets**

`Categories.cs`: add `"tree*"` at the end of both `OrbTargets` and `ZipTargets` (bonus11's checkpoint has both in
reach), and in `CategoryDef.RunsOn` (:135) replace `Array.IndexOf(Targets, token) >= 0` with `Listed(Targets, token)`:

```csharp
        /// <summary>The token is in the list; "tree*" stands for every tree transition (tree&lt;L&gt;to&lt;C&gt;).</summary>
        static bool Listed(string[] targets, string token)
        {
            return Array.IndexOf(targets, token) >= 0 || token.StartsWith("tree") && Array.IndexOf(targets, "tree*") >= 0;
        }
```

Extend the `OrbTargets` doc comment: "The tree transitions start at bonus11's checkpoint and count as it does."

Build (Task 2 Step 5's command). Expected: `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add tools/tree-transitions.py Categories.cs
git commit -m "Tree transitions: 95 targets from bonus11's checkpoint to Courses 6-10; 6to10 dropped"
```

### Task 4: Rules and docs

**Files:**
- Modify: `README.md` rule 21 (append), rule 19 (one clause)
- Modify: `AGENTS.md` (Route names: the transition line)

- [ ] **Step 1: README rule 21: append at the end of its text:**

```markdown
    A tree transition (`tree<L>to<C>`, courses.json `tree`: from bonus11's checkpoint by the atom room, its door open, to
    Course C's start checkpoint, its reset point, reached as a transition reaches a bonus) sets The Tree at level L the game's load way at every quick restart onto it (`TreeLevel`: the
    plant and the first L tree boxes in segment order bought once, `TimesUsed` and `instaGrowSegments` as
    `TreeController.load`, a bought-out box deactivated as its `Start`, the segments above off as `Awake` leaves them),
    the Area 2 door open from level 8, the Zone 2→1 path open and Area 1 overgrown from 23; no unlock is written. The
    save's tree and path are put back before a save. Levels whose box changes nothing in the world are one target, by
    the highest (0, 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16, 17, 19, 20, 22, 27).
```

Rule 19, after "(the targets are listed in [`Categories.cs`](Categories.cs))": add "(the tree transitions count as
bonus11, whose start they share)".

- [ ] **Step 2: AGENTS.md**: in Route names' category line, after the bonus4/bonus12 sentence add:
  "The tree transitions `tree<L>to<C>` (README `### Rules` 21) run `WJ_DS_DJ_BS` and every outside-ladder category."

- [ ] **Step 3: Commit**

```bash
git add README.md AGENTS.md
git commit -m "Docs: the tree transitions"
```

### Task 5: Deploy and check against the game

**Files:**
- Create: `tools/tree-check.py`

- [ ] **Step 1: Write `tools/tree-check.py`**

```python
"""Checks the tree transitions against the running game (docs/superpowers/plans/2026-10-05-tree-transitions.md):
each level's start trace has The Tree's segments and the Area 2 door as the level leaves them, a normal course start
after them has the save's tree back, and offline replays a random run from each level as turbo played it.

  tree-check.py [engine.exe]
"""
import random, subprocess, sys

from gamepaths import TRACES, call_retry, course_ids

IDS = course_ids()
# The Tree's segments grown at each checked level (the spec's table); Tree 2's count is the save's, the same in all.
GROWN = {0: 1, 7: 10, 10: 13, 22: 28, 27: 28}


def start(token, name):
    r = call_retry("/api/enginesim/trace", {"course": IDS[token], "capabilities": "wjdsdjbs", "ticks": [], "name": name}, 30, 2)
    assert r.get("ok", True) and "path" in r, r
    import json
    first = json.load(open(r["path"], encoding="utf-8"))["ticks"][0]["econ"]
    return first["segments"], first["doorArea2"]


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else r"enginesim\Cli\bin\Release\net10.0\IGTAP.EngineSim.exe"
    save_before = start("6", "treecheck-c6-before")[0]
    seen = {}
    for level in (27, 22, 10, 7, 0):
        seen[level] = start(f"tree{level}to6", f"treecheck-tree{level}")
        print(level, seen[level])
    base = seen[0][0] - GROWN[0]
    for level, (segments, door) in seen.items():
        assert segments == base + GROWN[level], (level, segments, base + GROWN[level])
    assert seen[7][1] != seen[10][1], "the Area 2 door must move between levels 7 and 10"
    assert start("6", "treecheck-c6-after")[0] == save_before, "the save's tree must be back"
    rng = random.Random(7)
    paths = []
    for level in (27, 10, 0):
        ticks = [{"x": rng.choice([-1, 0, 1]), "y": 0, "held": rng.random() < 0.4, "dash": rng.random() < 0.05} for _ in range(300)]
        r = call_retry("/api/enginesim/trace", {"course": IDS[f"tree{level}to6"], "capabilities": "wjdsdjbs", "ticks": ticks,
                                                "name": f"treecheck-rand-{level}"}, 30, 2)
        paths.append(r["path"])
    out = subprocess.run([exe, "replay", *paths], capture_output=True, text=True)
    print(out.stdout[-3000:])
    assert out.returncode == 0 and "differ" not in out.stdout.lower(), "offline must replay the tree starts as turbo did"
    print("tree-check: OK")


if __name__ == "__main__":
    main()
```

Before running, open `tools/enginesim-capture.py`'s `trace(body)` (:30) and use the same endpoint and body it posts
(replace `/api/enginesim/trace` above if it differs), and `random_inputs` (:33) for the tick fields; run the engine's
`replay` once on an existing `rand-*.json` to learn its pass/fail output, and match the last assert to it.

- [ ] **Step 2: Deploy and run the check**

Run: `python tools/enginesim-redeploy.py` (closes the game, builds, copies, relaunches; ask the user first if a solve
or playback is running in it).
Run: `python tools/tree-check.py $env:TEMP\igtap-tree-engine\IGTAP.EngineSim.exe`
Expected: five level lines, then `tree-check: OK`.

- [ ] **Step 3: 1x door check (Review Focus 1)**

After a fresh game launch (redeploy), play `tree10to6` at 1x from the studio's route list with an empty route
(`POST /api/playback` as the TAS tab's start does; or the Run route button) and read the overlay or
`GET /api/state/live`: the run starts; then capture `treecheck-tree10` again and compare `doorArea2` with Step 2's level
10 value. Expected: equal.

- [ ] **Step 4: Commit**

```bash
git add tools/tree-check.py
git commit -m "tools/tree-check.py: the tree levels' starts, the save's tree back, offline against turbo"
```

### Task 6: Solve in order and prune

**Files:**
- Create: `tools/tree-solve.py`

- [ ] **Step 1: Learn the Improve command**

Run: `python -c "from tools.gamepaths import call; import json; print(json.dumps(call('/api/solver/preview', {'course': 1276, 'capabilities': 'wjdsdjbs'}), indent=1))"`
(from the repo root with `tools` importable, or `cd tools` and `from gamepaths import call`). Read the returned command:
its exe, trace path and flags.

- [ ] **Step 2: Write `tools/tree-solve.py`**

```python
"""Solves the tree transitions in the spec's order (level 27 to C6-C10, then each level below), one `solve --auto` per
target and category, as the studio's Improve would (POST /api/solver/preview: the same command), offline. A target none
of whose categories finishes is dropped from courses.json (tree-transitions.py --drop) and listed at the end.

  tree-solve.py [--minutes M] [--levels 27,22,...]
"""
import subprocess, sys

from gamepaths import call, course_ids

LEVELS = [27, 22, 20, 19, 17, 16, 15, 14, 13, 12, 11, 10, 7, 6, 5, 3, 2, 1, 0]


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def main():
    minutes = arg("--minutes", "10")
    levels = [int(x) for x in arg("--levels", ",".join(map(str, LEVELS))).split(",")]
    ids = course_ids()
    cats = {c["name"] if "name" in c else str(c["id"]): c["categories"] for c in call("/api/state")["courses"]}
    impossible = []
    for level in levels:
        for course in range(6, 11):
            token = f"tree{level}to{course}"
            finished = False
            for caps in cats[token]:
                preview = call("/api/solver/preview", {"course": ids[token], "capabilities": caps})
                command = preview["command"] + ["--minutes", minutes]
                print(token, caps, " ".join(command), flush=True)
                run = subprocess.run(command, capture_output=True, text=True)
                tail = run.stdout[-400:]
                print(tail, flush=True)
                finished |= "best" in tail.lower() and "no finish" not in tail.lower()
            if not finished:
                impossible.append(token)
                subprocess.run([sys.executable, "tools/tree-transitions.py", "--drop", str(level), str(course)])
    print("dropped (no category finishes):", impossible)


if __name__ == "__main__":
    main()
```

Adjust from Step 1: the preview's key for the command (`command` or `args`), whether it already has `--minutes`, and the
words `solve --auto` prints for "finished" and "never finished" (run one target by hand first and read its last lines;
also the `/api/state` courses' token key). Capture each target's start trace first if the preview says it's missing
(`python tools/enginesim-capture.py start <token> <caps>`).

- [ ] **Step 3: Run level 27 first, then the rest**

Run: `python tools/tree-solve.py --levels 27`
Expected: five targets × seven categories solved; bests land in `offline-best\` and the plugin judges them through
`SaveSolverRoute` (records `tree27to<C>_<CAT> <time>`). Then `python tools/tree-solve.py` for the remaining levels
(`--levels 22,20,...`), in the background.

- [ ] **Step 4: Report and commit**

List per target the best per category and the dropped ones. Trace each new best (`IGTAP.EngineSim path <trace> <route>`)
and name its trick (README rule 53).

```bash
git add tools/tree-solve.py
git commit -m "tools/tree-solve.py: the tree transitions solved in order, unfinishable ones dropped"
```
