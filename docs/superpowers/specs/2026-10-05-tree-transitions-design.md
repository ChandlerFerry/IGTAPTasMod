# Tree transitions: the checkpoint by Course 6 to Courses 6-10, by tree level (design)

## Problem

Area 2's routes depend on how far The Tree (`zone 2/TreeArea/The Tree`) has grown: each segment's ground
(`Tree*Ground`) is a surface, the Area 2 door box (TreePart7) opens the facility's lower section, and the OmniDash box
(TreePart23) opens the Zone 2→1 path. Category starts run in the save's own tree today (nothing sets it), so a
transition's route is judged against whatever tree the save happens to have, and the one Area 2 transition, `6to10`,
can't tell the levels apart.

## The tree (read from the game's data, `b640-world-c1.world.json`)

A box's first buy grows every segment in its `SegmentsToTrigger` (upgradeBox.cs:668-671, `growSegments` skips grown
ones); later buys toward its cap grow nothing. A segment with `autoTriggerNext` grows the next when its own growth ends.

| Box (TreePart) | Grows | | Box (TreePart) | Grows |
|---|---|---|---|---|
| PlantTreeBox | 0 | | 12 UnlockCompBoost | 13, 26 |
| 0 GreenCloneChance | 1, 2 | | 13 CompBoostStrength | 14, 15 (15 auto-grows 16) |
| 1 IncreasedWatts | 5 | | 14 CompBoostDuration | 17 |
| 2 IncreasedGP | 3 | | 16 ZipMoversUnlock | 21 |
| 3 GlobalFastClone | 4 | | 17 Cdmult | 18 (18 auto-regrows 0) |
| 5 GlobalNPincrease | 6, 25 | | 18 Cdmult | 19 (19 auto-grows 20) |
| 6 TripleThreat | 7, 10 | | 20 MoreOrbs | 27 |
| 7 Area 2 door open | 8, 9, 10, 12; opens the door | | 21 5xIncWatts | 23, 24 |
| 10 ExemptC6 | 11 | | 23 OmniDash | nothing; opens the Zone 2→1 path |
| 11 exempt c7 | 22 | | 4, 8, 9, 19, 22, 24-27 | nothing |

TreePart15 has no box. Every segment's grower has a lower number than the segment, so buying in box-number order is a
real buy order.

## Design

### Tree level

**Level L** = the plant and the first L tree boxes in number order bought, once each (boxes 0-14, then 16-27: 27 boxes,
L 0-27). Levels whose box changes nothing in the world (grows no segment, opens nothing) play as the level below, so
each group is one target, named by its highest level (the maxed tree is a target):

| Target level | Covers | | Target level | Covers |
|---|---|---|---|---|
| 0 | 0 | | 13 | 13 |
| 1 | 1 | | 14 | 14 |
| 2 | 2 | | 15 | 15 |
| 3 | 3 | | 16 | 16 |
| 5 | 4-5 | | 17 | 17 |
| 6 | 6 | | 19 | 18-19 |
| 7 | 7 | | 20 | 20 |
| 10 | 8-10 | | 22 | 21-22 |
| 11 | 11 | | 27 | 23-27 |
| 12 | 12 | | | |

19 levels.

### Targets (`courses.json`)

- `6to10` is removed, with its routes and offline bests.
- `CourseDef` gets `tree` (int, the level; unset: the save's own tree, as today).
- 95 transitions `tree<L>to<C>`, L the 19 target levels, C 6-10: `from: "tree<L>"`, `to: "<C>"`, finishing on Course C's start checkpoint
  (a 32 u arrival box on its reset point, as a transition to a bonus; no `toCourse`, so the start gate doesn't end it),
  `minCapabilities: "wjdsdjbs"`, `tree: L`. The start is the in-game checkpoint nearest (22649, -376) (bonus11's start,
  22643.9, -364.685), placed with the `move-target-start` skill.
- Categories are what README `### Rules` 18-19 give a transition: `WJ_DS_DJ_BS` and the outside-ladder categories where
  they run. No new category.

### Setting the tree (plugin)

Every quick restart onto a target with `tree` sets The Tree to that level as a game load at that level would leave it
(`TreeController.load`), beside `EnsureArea1State` in `Solver.cs`:

1. Every segment off (as `TreeController.Awake` leaves them), `GrownSegments` cleared.
2. Each box's `TimesUsed`: 1 for the level's boxes, 0 for the rest; `PlantTreeBox` 1.
3. `instaGrowSegments` with the segments those buys grow (the table above, auto-grows included).
4. The Area 2 door (`upgradeBox.doorToOpen`, a `PlatformMover`): `JumpToState(0)` below level 8 (as its `Awake`), its
   `JumpToNextState` from 8 (as the bought box's `Start`, upgradeBox.cs:260).
5. From level 23: `Zone2toZone1PathOpen` on and `Zone2to1TransitionController.updateState(true)` (its `Start`), and Area 1
   overgrown through `SetArea1State`, as the OmniDash box's first buy leaves a game (upgradeBox.cs:825-830). Below 23:
   the path closed and Area 1 the save's own.
6. No unlock is written: the boxes' global upgrades (zip movers, more orbs, omnidash) stay what the category sets
   (README `### Rules` 10, 12).

The save's tree (each tree's `GrownSegments`, segments on, boxes' `TimesUsed`/`isActive`/`Cap`), the door's state and
the path flag are kept before the first change and put back after, as the Area 1 state is (`RestoreArea1State`); saves
wait until they are back (rule 25). README `### Rules` gets the tree level beside 23-24 (a target's `tree`, set the
game's load way, the save's put back after).

### Offline

The engine builds its world from the captured scene (objects on, colliders, `TreeController` fields), so a target's
trace captured with its level set carries that level: `tools/enginesim-capture.py` per target, nothing new in the engine.
If a check below shows the engine reading the tree elsewhere (the trace header's economy, `segments`), that is fixed in
the engine (rule 49).

### Solve order and pruning

1. Level 27 to C6, C7, C8, C9, C10; then each level below, down to 0.
2. Each target: `solve --auto` per category (`enginesim/README.md#use`), the bests through `SaveSolverRoute`.
3. A target no category can finish is removed from `courses.json` and listed in the report.

## Testing

- Turbo against the offline engine at levels 0, 10, 22 and 27 (door closed, door open, path closed, path open): a
  route's trace ticks equal (`tools/validate.py`).
- A quick restart onto `tree0to6` after `tree27to6` and back: the segments, door and path match the level each time,
  and leaving puts the save's tree back (checked against the save's own tree before).
- `/api/state` lists the 95 targets with their categories; `6to10` is gone.
