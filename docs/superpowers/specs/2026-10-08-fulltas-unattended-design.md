# Full-TAS economy stretches, unattended

## Problem

Getting Zip% from `buytree6` towards `buytree16` has needed an operator for weeks: picking flags (`--only`, `--rounds`,
`--minutes`, `--piece-minutes`, `--beam`, `--off-ticks`, `--stages`, `--replace`), picking the goal to plan from, watching
logs, stopping searches by hand. The 2026-10-08 session lost most of its time that way. The code map of that day
(`fulltas-plan --stages`, EconomyPlan.cs / FullTasPlan.cs / FullTasBuild.cs):

- **Nothing a failure teaches is kept.** A lap that reaches its gate but misses its finish blocks nothing, so the replan picks
  the same lap again (EconomyPlan.cs:1717-1721). `MoveTable.Blocked`, `_lapMisses`, `_recordPads` and `_recordWays` live
  for one stage and aren't saved; only successful walks reach the moves file.
- **One step can search for over an hour.** `Lap`'s fallback chain (FullTasPlan.cs:810-855) can run a gate search (4B), the
  record search (12B) and a lap search (8B): 24 × `--piece-minutes`, about 72 min at the default. The 2026-10-08 stage spent
  12 of its 25 minutes on one Course 7 lap with 0 candidates.
- **Every budget is wall-clock.** The planner's exact search and beam, `Explore`, `SolvePiece`, the per-stage `TimeUp`. Which
  plan wins, whether a piece is found and when a stage stops all depend on machine load (other agents share the CPU).
- **The planner prices moves no one has played.** Grid-walk estimates count air as walkable; realize then tries to build them
  (the 2026-10-08 walk from `buytree0` to Course 7's box: two piece searches, nothing).
- **No pre-flight.** `--try` tests a step by hand; the stages never use it.

## Goal

`fulltas <trace>` (the entry the studio's Full-TAS solve already runs), with no flags, carries a run whose next goals are
economy buys (the `buy:` goals `FullTasGoal.IsEconomy` names, Zip%'s `buytree7` … `buytree16`) to its last goal, unattended.

**Success:**
1. The same trace, routes, run definition, moves file and engine build give the same installed route and the same report,
   on any machine, under any load.
2. Every run ends in one of two ways: the run's last goal installed, or a stop whose report names the fact that blocks it
   (a goal no plan reaches with the moves the engine could build).
3. No step's worst case exceeds a fixed simulated budget; a run's worst case is bounded by the number of distinct moves.
4. The same dead end is never searched twice, within a run or across runs on one engine build.

**Not in scope:** the non-economy stretches (`fulltas`'s reach solve and `--auto` stay as they are); course Improve; a Rust
port (the engine's fidelity is the game's own code running offline; a port would re-implement it by hand and lose
README ### Rules 50's tick-for-tick match); README rule changes. Zip% keeps strict buys (rule 38).

## Design

### 1. The move ledger: every attempt is a fact

`MoveTable` becomes a ledger of facts keyed by **(from place, to, unlocks, tree group)**, the tree group `MoveTable.TreeGroup`
of the boxes bought (a grown segment changes the world). A fact is:

- **Played**: the ticks a build took (today's `measured`).
- **Failed**: realize's bounded attempt (section 2) didn't play it from a real state at that key: no way, the finish missed,
  a stray buy, short of Cash past its stand. The reason is kept for the report.

Facts are saved in the run's moves file (`fulltas/<run>.moves.json`), with the engine build's id (the Runtime assembly's
MVID). Failed facts from another build are dropped on load: an engine fix can make a move playable. Played facts are kept as
today.

The planner never offers a Failed move. It replaces `Blocked`, `_lapMisses`, `_recordPads`' misses and `_recordSearched`.
An estimated move (no fact) may be planned; realize's attempt turns it into a fact either way.

*Known approximation:* a move failed from one real state at a key counts as failed from every state at that key (Cash, frame
phase and NP are not in the key). The report lists every Failed fact, so what was excluded is visible.

### 2. One bounded attempt per step

A step's build is: (a) the deterministic replays it has (library pieces, saved routes and farm loops, from the pads they
list today), then (b) at most one piece search per goal of the step (a lap: its gate, then its finish), each with a fixed
budget of **`PieceTicks` simulated ticks**. If neither plays, the step's move is recorded Failed and the step fails. The
record search's 3× budget, the gate's second search, the restart retries and the "Resuming from piece" second attempt go:
a lap step's worst case is 2 × `PieceTicks`.

`PieceTicks` is a constant, set from measurement to what today's 3-minute piece search simulates on this machine (the
2026-10-08 logs: about 5M ticks per 15 s at full load), so search strength stays the same and only the clock goes.

### 3. Deterministic budgets

- **Planner:** the exact search and the beam are bounded by states expanded (constants, sized from today's runs), not seconds.
  Auto mode's "beam with what the exact search left" becomes the exact search to its state cap, then the beam to its own.
- **Piece search (`Explore`):** a fresh archive each search (no resume from disk: that made a result depend on earlier runs), a
  fixed worker count (8, not `ProcessorCount`), each worker its own seeded RNG, run in
  generations: every worker plays a fixed batch of runs from the archive as it stood at the generation's start, then the
  batches are merged in worker order. The budget is simulated ticks summed over the generations. Same seed, same archive in,
  same result out, whatever the threads' timing.
- **No `TimeUp`, no `PlanOver`.** A stage ends when its plan is built or fails; the run ends per section 4.

Explore is shared with every other solve (course Improve, the reach solve, Tree%, TouchGrass-ANY%, FalseEnding%-280); the
generational merge applies to all of them. They are re-checked (Testing).

### 4. The loop

```
loop:
  next = the first goal the best doesn't reach
  if none: done (report)
  if next isn't an economy buy: hand over to fulltas's reach solve (as today) and stop
  plan from the best's furthest goal to next (the planner, offering every economy box and every lap/record of a course
    with a gate, except the game's own doors: EconomyPlan.Gates, Never)
  if no plan: stop, report "no plan reaches <next>" with the facts that excluded the moves
  realize the plan step by step (section 2):
    a step fails  -> its move is now Failed; plan again from here (no round cap)
    a step drifts -> past a fixed tick drift, plan again from here, once per plan step
  install when the build gets past the best (as today), then loop
```

Termination: each plan-again after a failure adds a Failed fact, and the facts are finite (places × tos × unlocks × tree
groups); a drift re-plan happens at most once per step of a plan; every step is bounded (section 2), every plan bounded
(section 3).

### 5. No knobs

Every setting the operator chose becomes a constant or comes from the run: the offered boxes and laps (all, less the game's
doors; `--only` existed for speed and for courses that failed, which section 1 now handles), the stage targets (the run's
goals), the buy rule (`runs/<run>.json` `buys`), widths, rounds, drift, piece budget, planner caps.

`fulltas-plan` keeps only `--try`, the step-at-a-time diagnostic. Its stage path and flags (`--stages`, `--realize`,
`--replace`, `--compare`, `--skip-free`, `--only`, `--rounds`, `--off-ticks`, `--piece-minutes`, `--beam`, `--mode`,
`--minutes`, `--stand`, `--dominance`) are deleted with the code only they reached. The stage loop moves into `fulltas`.

### 6. The report

Each run writes `fulltas/<run>.report.json` and prints its summary. It holds, per stage:
- the target, the plan's tick and the built tick;
- the Failed facts added, each with its key and reason;
- the install, if any;
- the stop reason.

The studio can show it later.

## Testing

- **`plan-selftest`:**
  - the ledger saves and loads both kinds of fact, and drops Failed facts on a build change;
  - the planner never offers a Failed move;
  - a toy run whose only route fails stops with a report instead of looping.
- **Determinism:** one short Zip% stretch (plan plus its first 20 steps) run twice, once with a CPU-hog process alongside,
  gives byte-identical inputs and report.
- **Shared-code regressions:** `tools/validate.py`, `tools/tech-check.py`, and replays of Tree%, TouchGrass-ANY% and
  FalseEnding%-280 after the Explore change.
- **Acceptance:** `fulltas <Zip% trace>` with no flags, from the installed best (goal 987), runs to an install of
  `buytree7` or a stop with a report, unattended.

## Rollout (each step committed and checked before the next)

1. Ledger with Failed facts, saved and keyed (section 1). This is the biggest waste, and the existing tests cover it.
2. Bounded step attempts (section 2).
3. Deterministic budgets: the planner's state caps, `PieceTicks`, Explore's generations (section 3).
4. The loop in `fulltas`, the flags deleted, the report (sections 4-6).
5. Acceptance run on Zip%.
