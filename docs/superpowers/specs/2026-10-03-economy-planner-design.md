# One economy planner (design)

## Problem

A Full-TAS run's time is set by its economy: which boxes it buys, in what order, and how many laps of which course
earn the Cash between them. Five tools plan that today, and none sees the whole schedule:

| Tool | Economy | Movement | Scope |
|---|---|---|---|
| `tools/plan_fulltas.py` | a Python copy of the game's | guessed walks | whole run, greedy late phases |
| `tools/plan-buytable.py` + `fulltas-buytable` | the Python copy | measured (buy table) | `reward:bonus1`..`buy:buyds` only |
| `fulltas-plan` (`FullTasPlan.cs`) | the game's own (`Economy.cs`) | full physics per step | one stretch, greedy |
| `zip-plan` (`ZipPlan.cs`) | a third copy (Area 2) | distance guesses | Area 2 |
| `--auto --parts goals` | full replay | full re-search | local edits of the best's list, ~7 min a pull |

So a question like "Course 2 lap, then bonus 1; or bonus 1 first; or two laps and a clone, then bonus 1" is never
asked: `plan-buytable.py` starts at `reward:bonus1`, `fulltas-plan` is greedy in one stretch, and the goals part moves a
reward goal only 1/4/16 goals at a time and then needs minutes of movement re-search per try.

## Design

One planner, `fulltas-plan` (C#, `enginesim/Cli/EconomyPlan*.cs`):

1. **Cash model = the game's economy, without physics.** The engine's own `Economy`/`Shop` (the decompiled game rules)
   are driven by the schedule instead of by the player: `Simulation.EconomyIdle(ticks, place)` steps every frame's clone
   step, every tick's course timers, NP and atoms; `EconomyBuy(box)` runs `Shop.Buy`; `EconomyFinish(course, time)` runs
   the finish's reward, clone dust, comp boost and record break. No hand-written copy of the economy remains anywhere.
2. **Movement model = measured ticks.** A table of ticks between places (`MoveTable`): the buy table's entries, the
   best's own splits, saved course laps (`<course>_<CAT> farm <lap>` and records) and quick restarts; a distance
   estimate only where nothing is measured, flagged as such.
3. **Search = Dijkstra over whole schedules.** A step is a lap (or a record lap, or a ladder of K record laps), a buy of
   one or more boxes on one walk, a reward goal (a bonus), a quick restart, or the target. State: tick, place, unlocks,
   box levels, rewards taken, the economy snapshot. Dominance: same place/levels/rewards, no later and no less Cash. A
   bonus is a step like any other, so its position in the schedule is searched, not fixed.
4. **Realize.** The winning goal list is built into inputs by the loop builder (`FullTasPlan`'s `Play`/`Borrowed`/
   `RecordLap`/join machinery, kept), replayed strictly. Measured ticks go back into the table and the plan is redone,
   until the plan and the build agree (at most 3 rounds). `--auto` then polishes movement only.

The engine must model Area 2's economy for (1) to cover what `zip-plan` covered (README rule 49: fix `enginesim`, don't
work around it): green/red power and colour clones, clone dust, the comp boost's NP doubling, NP's constant income and
cap, atoms (queue, 0.4 s landing, tiers, crafting, first-merge NP), the atom level and NP global boxes, and the tree's
growth (the game's own coroutine, its `WaitForEndOfFrame` resumed per rendered frame). Each is checked against a 1x
probe's currency rows.

## Deleted

`tools/plan_fulltas.py`, `tools/test_plan_fulltas.py`, `tools/plan-buytable.py`, `enginesim/Cli/ZipPlan.cs`,
`enginesim/Cli/zip-plan.json`, `FullTasPlan.cs`'s greedy loop, income guess (`Rate`, `Clones`, `ClonePay`, `Rolls`) and
ladder credit (`Ladders`, `Credited`, `HoldBackLadders`), `FullTasAutoLadder.cs`, `FullTasAutoPayout.cs`, the goals
part's edit generators (drops, adds, walk adds, cash moves, regroups, record laps, inserted laps, payout breaks, ladders,
investments, `--insert-goal`, `Reroute`), `--ladder-study`, and their doc sections. The goals part becomes one pull:
plan from a goal of the best, build, adopt when faster.

## Kept

The buy table (`fulltas-buytable`, now the planner's movement source), the loop builder and piece library, the goals
part's adoption machinery (`GoalEdit`, `Played`, `StrayFit`, `AdoptGoals`, `GoalsRefused`, `ResumeAdoptedGoals`), `Retune`,
`Idle`, and every route rule in README `### Rules`.
