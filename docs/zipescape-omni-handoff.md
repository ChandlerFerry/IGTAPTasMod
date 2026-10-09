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

**Goals** (`runs/ZipEscape%.json`, 1163; the user's draft is in `C:\tmp\igtap\zip\ZipEscape-before.json`): Zip%'s first
1157, then `at:28710,5876,240,400` (pad 3), `buy:buytree13`, `buy:buyteleporters`, `at:16639,11761,240,400` (pad 102),
`buy:buytree14`, `buy:buytree16`. Zip%'s laps between `buytree13` and `buytree14` were dropped: the stages plan the economy.
The run file and the trace header were set together (`C:\tmp\igtap\zip\s3\setgoals.py <repo> <trace> <goals.json>`).

**Where it stands:** the trace is captured (`fulltas-ZipEscape%.json`). The best (`fulltas/ZipEscape%.json`) reaches goal
1158: Zip%'s best through goal 1157 (tick 75,670, the player at (25513, -2313)), then the climb to pad 3, found by the
area-goal stage on its second search (1,234 ticks, pad reached on tick 76,904). The next stage was planning
`buy:buyclone7` then `buy:buytree13` from pad 3 when it was stopped for the user to improve stretches.

**To resume:** `IGTAP.EngineSim fulltas "<config>\enginesim\fulltas-ZipEscape%.json"` (no flags; the unattended stages).
Watch it: the user wants every stage monitored, a stall checked in the engine (`fulltas-plan --try`, `stick:X/Y@T`) before
anything else. What should happen next:
1. `buytree13` (economy stage, from pad 3).
2. `buy:buyteleporters` (20M GP: the GP climb Zip% made with enablers).
3. Pad 102: an area goal, one piece search from the box (walk; realize takes a hop chain when it's cheaper).
4. `buytree14`, then out of the tunnel by teleporter (the planner's hop chains: pads 4 / 101 / 200 sit in a row at
   y 13076).
5. `buytree16` through pad 3 (pads 2 -> 3 or the chain), when paid for.

## Omni%

Zip% plus `buy:buytree23` (TreePart23's OmniDash box at (25623, 7236): its first buy turns Area 1 overgrown and opens the
Zone 2 -> 1 path, `Economy.OmniDashFirstBuy`). Goals: Zip%'s 1717 then `buy:buytree23` (1718). No trace yet: capture it
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
