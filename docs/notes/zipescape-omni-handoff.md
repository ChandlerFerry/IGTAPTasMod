# ZipEscape% and Omni%: handoff (2026-10-09)

Both are WIP Full-TAS runs built on Zip% (`fulltas/Zip%_NONE 2899.60`, complete: leave it alone). Their seeds list Zip% first,
so a run with no route of its own starts from Zip%'s best, cut where its goals part from Zip%'s (`FullTas.AdoptSeedBest`).

## ZipEscape%

Zip%, but escaping the tunnel by teleporter instead of the refill orbs. The user's rules:

- `buytree14` (TreePart14 CompBoostDuration at (23322, 3811), "the upgrade at ~23326, 3640") grows tree segment 17, which
  blocks the way out of the tunnel. It can't be bought until pad 102 (Area 1, (16639, 11761)) is visited, and an Area 1
  pad only registers a visit once the teleporters' box is bought (`buy:buyteleporters`, (19481.6, 6909.6), 20M GP).
- No MoreOrbs (`buytree20`) and no `buyorbs7`: pad 3 (28710, 5876) is visited by climbing to it before tree segment 16
  is placed. `buytree13` (CompBoostStrength, (24197, 4283)) places segment 16 (the zip box and its ground), so the visit
  comes before it. Area 2 pads register a visit without the box. Afterwards `buytree16` (the zip movers, (25843, 6091))
  is reached by teleporting to pad 3 once it's paid for.

**Goals** (`data/runs/ZipEscape%.json`, 842 since the replan below; the user's draft is in
`C:\tmp\igtap\zip\ZipEscape-before.json`): Zip%'s first 807, Zip%'s unlocks after them (29: `buytree0`-`4`, `6`, `7`, `12`,
`buyatom` twice, `buycd8`, `buyclonecd8` four times, `buycddigits8`), then `at:28710,5876,240,400` (pad 3), `buy:buytree13`,
`buy:buyteleporters`, `at:16639,11761,240,400` (pad 102), `buy:buytree14`, `buy:buytree16`. The run file and the trace
header are set together (`C:\tmp\igtap\zip\s3\setgoals.py <repo> <trace> <goals.json>`).

**Replanned (2026-10-09):** turbo's body fix (`e1a5521`, `TurboRunner.KeepBody`) broke the old best at goal 808
(`buy:buytree2`; old numbering 970), so the best reaches goal 807 (`buy:buyreward7`, tick 58,197), not pad 3. Rather than
rebuild Zip%'s economy goals after it (137 buys of economy boxes and laps, `FullTasGoal.IsEconomy`, planned for Zip%'s run),
they were dropped: the stages plan the economy between the unlocks, as between `buytree13` and `buytree14`. Before it
(run file, trace, offline best, the stale stretch bests 960 and 974): `C:\tmp\igtap\zip\replan-20261009`. The copy that
reached pad 3 before the turbo fix, `fulltas/ZipEscape%.before-repair-1158-of-1163.json` (979 goals), doesn't replay.

**To resume:** `IGTAP.EngineSim fulltas "<config>\enginesim\fulltas-ZipEscape%.json"` (no flags; the unattended stages).
Watch it: the user wants every stage monitored, a stall checked in the engine (`fulltas-plan --try`, `stick:X/Y@T`) before
anything else. What should happen next:
1. The economy from goal 807 through the 29 unlocks to pad 3 (the climb from (25513, -2313) took 1,234 ticks in the old
   best, its tree grown by the same unlocks), then `buytree13` (economy stage, from pad 3).
2. `buy:buyteleporters` (20M GP: the GP climb Zip% made with enablers).
3. Pad 102: an area goal, one piece search from the box (walk; realize takes a hop chain when it's cheaper).
4. `buytree14`, then out of the tunnel by teleporter (the planner's hop chains: pads 4 / 101 / 200 sit in a row at
   y 13076).
5. `buytree16` through pad 3 (pads 2 -> 3 or the chain), when paid for.

## Omni%

Zip% plus `buy:buytree23` (TreePart23's OmniDash box at (25623, 7236): its first buy turns Area 1 overgrown and opens the
Zone 2 -> 1 path, `Economy.OmniDashFirstBuy`). Goals: Zip%'s 1492 then `buy:buytree23` (1493; lap goals since `9711320`). No trace yet: capture it
(`POST /api/fulltas/capture {"name": "Omni%"}`, a new game in the live game), then the unattended stages: it starts from Zip%'s
whole best (seed adoption) and has one economy stage to `buytree23`. Open question for the user: should Omni% take
ZipEscape%'s teleporter route instead of Zip%'s orbs (seed it from ZipEscape% once that's complete)?

## Built today (all pushed)

| Commit | What |
|---|---|
| `6ccbdf5` | Refill orbs come on as they come on camera, in turbo and the engine (OnBecameVisible) |
| `bc8ee79` | Teleporters in the engine and turbo: the box's buy, visits, the stick poll, the teleport; the plugin's input patch |
| `6d5d968` | The planner and realize take hop chains (`MoveTable.TeleportWay`, realize's walk-hop-walk) |
| `bc3076d` | A new run starts from its seed runs' bests; a stage reaches an area goal with one piece search |
| `bde7605`, `69471e3`, `eeae600`, `01ad60a` | Optimize tab: Full-TAS transitions (a stretch's own map, its avoids by goal number `forGoal`, Improve focused on it, progress, explored cells) |
| `8c4567e` | Improve on a long run starts in about a minute (speed probes goal to goal, no waste audit under a focus) |

Teleporters: `docs/fulltas.md#teleporters`. Refill orbs: `docs/fidelity-log.md` (2026-10-09).

## Open issues

- **Not probed at 1x:** a teleport (the poll's frame is reasoned from Unity's player loop and the atom grid's measured
  order). Once a route takes one, probe it (`tools/probe.py --from-tick`) against `fulltas --check --ticks-out`.
- **Goal-state restore failure:** ZipEscape%'s goal 1156 state "can't be restored here: a collider's shapes can't be
  rebuilt to the snapshot's (collider 733 #0)"; every worker falls back to replaying to it (slow, correct). Find collider 733.
- **Comp boost:** the planner never rotates courses to keep boosts up (Zip%'s best: a boost on 43% of ticks after it's
  unlocked, never more than 2 courses; NP doubles per boosted course with `compBoostDoublesNP`). The economy is modelled
  (the beam plays the game's finish and countdown); the beam judges a lap by its immediate value.
- **Laps don't take hop chains** (only buy steps do); via boxes aren't supported for Full-TAS (only avoids).
- **The teleporter box reads 80 levels** in one engine test (buy max on Course 9, the cap scaled by its tier at Start): it
  only needs to be non-zero, but check it against the gate's turbo when a route buys it.
- VMAN's installed route dies at action 191 and bonus7's two OM routes fail `validate.py`: both predate today.
