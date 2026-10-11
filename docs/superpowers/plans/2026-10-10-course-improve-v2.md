# Course Improve on v2, then delete the old `FullTas` stack (2026-10-10)

Goal: course Optimize's Improve (`solve --auto`, `enginesim/Cli/CourseAuto.cs`, `CourseLoop.cs`) runs on the Full-TAS v2
pieces (`enginesim/Cli/Runs/`), and the old `sealed partial class FullTas` (`enginesim/Cli/FullTas*.cs`, ~12.9k lines) goes.
Handoff item 3 of `docs/v2-handoff.md`.

## Findings that set the scope

- **Plugin/studio need no change.** `Solver.cs` `CourseSolveArgs` (1041-1062) builds `solve <trace> --seed… --walk-seed…
  [--optional/--no-optional] --threads N --minutes M --auto [--farm] [--class C] [--focus a-b]`; `EngineSimWaypoints`
  (1176-1202) adds `--waypoints-out FILE`; `PreviewSolver` (1209-1220) reuses `CourseSolveArgs`. It reads from stdout
  (1302-1358) `offline best X.XXs via`, `offline best farm loop X.XXXXs via`, `offline human-(major|nmg) best`,
  `waypoints {json}`, the progress line, and optional `@fulltas` `outlook`/`pullStart`/`pull` events (missing → the card is
  hidden, page.tsx:494). Files: `offline-best\<course>_<caps>-<time>.json`, `-loop-<lap>.json`, `-<class>-<score>.json`.
  Keep that CLI contract.
- **Improve never submits to the gate itself**: every old path passes `--no-submit` (CourseAuto.cs:561, :816, :542;
  CourseLoop.cs:424); the plugin's queue judges the `offline-best\` files. Keep it so (rules 44, 47).
- **Human classes already run outside the old stack**: CourseAuto.cs:115 returns `Difficulty.Ease(args)` for `--class`
  unless `--waypoints-out`. `HumanReach` (289-328) and the human branches of `CourseCommitted` (746-750) and
  `WriteClassBest` (785-793) are dead. Only `FullTas.Digital` (CourseAuto.cs:268, Ease.cs:133) is still used.
- **The old `--auto` loop was Improve's weak half** (enginesim/README.md:788-825: no gain in 7,500 CPU s, 7.6-10.5 GB).
  Gains came from the plain whole-route search (`WholeRoute` → `Solve.Run`) and the farm lap search
  (`StartLoop`/`SearchLoopLap` → `OfflineSolver`), both already off the old stack. Keep those; replace the old loop's
  polish and reach windows with v2's `Builder`, `SegmentSearch`, `Improver.RunSegment`.
- **Root `FullTas.cs` stays** (`FullTasGoal`, `FullTasTracker`, shared with the plugin). Only `enginesim/Cli/FullTas*.cs`
  is the old stack.

## Call graph: course code → old `FullTas` (enginesim/Cli)

| Course code | Old-stack member |
|---|---|
| CourseAuto.cs:231-236, 301-303, 539-541; CourseLoop.cs:419-421, 536-538 | static `TimerGoal`, `LoopTiming`, `TickTiming` (FullTas.cs:67-75; `Route.Origin/Last/Held` 34-46, `sim.TickTimed` 552) |
| CourseAuto.cs:254, 310, 545; CourseLoop.cs:424, 555 | `new FullTas(args)` (FullTas.cs:164) |
| …:255, 311, 545; CourseLoop.cs:425, 556 | `_course` (read at FullTas.cs:477, 543, 2027; FullTasAuto.cs:206, 234, 1536, 1614, 1653; FullTasAutoJoin.cs:106; FullTasAutoOutlook.cs:184) |
| …:256, 312; CourseLoop.cs:557 | `_committed` (FullTas.cs:2085; `Commit` 2023, 2069) |
| …:263, 313, 546; CourseLoop.cs:426, 564 | `Search()` (FullTas.cs:351) → `Auto` or `--unsolved` reach |
| …:259; CourseLoop.cs:559 | `Open()` (FullTas.cs:532) |
| …:260, 314, 547; CourseLoop.cs:427, 561 | `_best`, `Route` |
| …:311, 545; CourseLoop.cs:425 | `_reachOnly` |
| CourseAuto.cs:726-759 `CourseCommitted` | `_goals`, `_dt`, `Log`, `Describe`, `Clean`/`ScoreSeconds` (FullTasClasses.cs:162, 165) |
| CourseAuto.cs:873-896, CourseLoop.cs:703-724 (waypoints) | `_labels`, `_eval`, `_goals`, `Route.GoalPhysics`, `TimerGoal`, `Describe` |
| CourseLoop.cs:615-662 `RouteLoop`/`LoopKeeps`/`LoopCommitted` | `_best`, `_goals`, `Log`, `Describe`, `Route`; `LoopKeeps` from `Commit` (FullTas.cs:2027) |
| CourseAuto.cs:503-528 `PlainWalks` | `Seed`/`SeedStart` (131, 148), `ReadSeed` (1745), `StartOf` (1772), `WalkedStarts` (1442), `MaxWalk*`, `MaxWalks`, `StandTicks` (156-157), `CellWalksPerCheck`, `CellWalkRise` (1498-1499) |

Other users that must move first: Solve.cs:28 `FullTas.CourseAuto`, :61 `FullTas.FirstFinish`, :135 `FullTas.PlainWalks`;
Ease.cs:133 `Digital`; Map.cs:264-319 (`HeaderGoals`, `ReadGoalList`, `SolvedGoals`, `CurrentInputs`, visits readers);
Plan.cs:304 and SearchBench.cs:331 (`HeaderGoals`); TechsUsed.cs:113, 119 (`LabelOf`, `GoalsHeader`); TimeLossFloor.cs:114-217
(`Sample`, `SpeedWindow`, `Sustained`, `Jumped`, `SpeedMoves`); Ultras.cs:298-299; Program.cs :49 `fulltas --check` /
`--waste-audit`, :50 `macros`, :60 `join-check`, :328 and :822 `EconomyRow`.

## Course targets as v2 plans

The synthetic Full-TAS trace `WriteCourseTrace` writes (CourseAuto.cs:590-619: `courseAuto: true`, `buys: loose`, ids from
`FullTasGoalIds.Derived`) opens with `RunContext.Open`; `SegmentSearch.Find` gets the course's avoid/discourage boxes through
`Solve.FullTasPaths(name)` → `Solve.CourseRuns`. `CourseWaypoints.Build`, `PlayCourse`, `MeasureLoop`, `Laps`, `AsLoop`,
`StartLoop`, `SearchLoopLap` stay.

- **Record**: plan `[at:… (run-up), start:N, at:…, farm:N]` (a transition ends at `start:M`, a bonus at its finish rect).
  Score = game timer by `PlayCourse` (rules 27-29; another course's finish or off the forced path: `long.MaxValue`), ties
  by `RunTicks`: `round(Time/dt) * 1e6 + RunTicks`. Bonuses/transitions: `RunTicks`. Tick-timed sims (`sim.TickTimed =
  true`) by wrapping `run.Make` in CourseAuto.
- **Farm loop**: plan `[farm:N × LeadLaps, (at:…, farm:N) × LoopLaps]` (`LoopGoals`, CourseLoop.cs:591). Score =
  `MeasureLoop(AsLoop(Laps(inputs) cut to LeadLaps+LoopLaps finishes, LoopLaps)).Live` if steady, else `long.MaxValue`
  (rule 30). Cut the laps: `Laps`' 60 idle ticks can add a finish on a short lap.
- **Polish** (what `--focus` drives): per goal j in the span, `Improver.RunSegment(plan, bestInputs, j, stop)`, kept only
  if `Better` under the score. Default span: a record's goals after `start:N` through the finish; a bonus/transition all;
  a farm the goals after the lead. `--focus a-b` (1-based) = exactly goals a..b, skipping a `start:` goal.
- **Reach** (replaces `--unsolved`; FirstFinish, RepairLoopLap): `Builder.Harvest(plan, carried)` then `Builder.Build(plan,
  harvested)`; a waypoint failing `unreachable` is dropped (`RunPlan.RemoveAt`) and built again; finish unreachable → null.

## Tasks

Work in a worktree on its own branch; build privately (`-o C:\tmp\igtap-course\new`); every commit through `check` (the hook).

0. **Old-path baseline** (no code): build main to `C:\tmp\igtap-course\old`; copy `optimize-c4-wjds.json` (+ world,
   `-seed0..9`) and `optimize-c8-wjdsdjbs.json` (+ world, `-seed0..1`) from the config `enginesim\` into
   `C:\tmp\igtap-course\traces\` (never write the config); run the old exe: (a) `solve optimize-c4-wjds.json --seed …
   --threads 24 --minutes 20 --auto`, (b) the same on c8 with `--farm`, (c) `--waypoints-out` for record, `--farm`, and
   `--class human-major` on c4. Record best time/lap, wall time, peak working set; keep the `waypoints-*.json`.
1. **Score hook in v2** (`RunContext.cs`, `Builder.cs`, `Improver.cs`, `RunSelfTest.cs`): `RunContext.Score`
   (`Func<List<InputTick>, long>`, null = live time); `Built.Score` cached; `Better` compares scores when both complete and
   `Score != null`. `Improver.SegmentBudget`/`SegmentSearches` public (defaults `Builder.Budgets[1]`, 25) used by
   `RunSegment`. Check: a `run-selftest` case where `Score` reverses live-time order.
2. **Reach on v2** (`CourseAuto.cs`, `CourseLoop.cs`): rewrite `Reach` (533-548) per above with a wall-minutes stop through
   `SegmentSearch.Stop`; point `FirstFinish` (377-394) and `RepairLoopLap` (CourseLoop.cs:410-435) at it. Delete `HumanReach`,
   `OtherWays`, `Corridor`, `--other-ways` (210-216, 398-494). Check: `run-selftest --course-reach <trace> <best>` reaches the
   finish from half the best's inputs and `PlayCourse` finishes on the forced path.
3. **Record polish on v2** (`CourseAuto.cs`): replace the old loop launch (227-263, `FullArgs` 556-584, `CourseCommitted`
   726-759) with `Polish(run, synthetic, span, minutes)` (~60 lines): open, wrapped `Make`, score, library/facts/search as
   RunSolve.cs does, a fresh `Improver(…, 1) { SegmentBudget = Builder.Budgets[0], SegmentSearches = 3 }` per goal
   (`ponytail:` on the low budget), skip (goal, best-inputs hash) pairs tried, `@fulltas {"type":"pull","pull":n}` per segment.
   On `NewBest` (not "start"): `PlayCourse`, `OfflineSolver.DropIdlePauses` (751-757), `WriteRecordBest`, the candidate to
   `course-auto\fulltas\<run>.json`, the waypoints line. `--focus a-b` → span; the whole-route search stays. Checks: a planted
   seed (C4 best + 10 idle ticks after the start gate) → `--auto --no-whole --minutes 8` writes a faster offline best;
   `--focus 4-5` searches only goals 4-5.
4. **Waypoints without the old stack**: static `WriteWaypoints`/`WriteLoopWaypoints` (splits `CourseWaypoints.Check`, rects
   capture − `WorldOriginInCapture`, `timerGoal` = index of `start:` + 1 or `LeadLaps`). Check: equal to Task 0(c) but `route`.
5. **Farm on v2** (`CourseLoop.cs`): `LoopAuto` (518-565) = `StartLoop` for half the minutes, `WriteStartingLoop`, `Polish` over
   the loop goals with the loop score; `NewBest` → `WriteLoopBest` + loop waypoints. Delete `RouteLoop`, `_loopChecked`,
   `LoopCycleOf`, `LoopKeeps`, `LoopCommitted`, the `TimerGoal`/`LoopTiming` writes. Check: `--farm --minutes 10` on c8 writes
   `-loop-` files only from steady loops; a 2-lap loop's harvest reaches every goal.
6. **Cut course code loose**: `sealed partial class FullTas` → `static partial class CourseAuto` (entry `Run`); move the
   `PlainWalks` dependencies in; call sites Solve.cs:28/61/135, Ease.cs:133. Check: no old-stack reference in Course*.cs,
   Solve.cs, Ease.cs; `run-selftest` and `check` pass.
7. **Delete the old stack**: FullTas.cs, FullTasAuto*.cs, FullTasBuild.cs, FullTasClasses.cs, FullTasHarvest.cs,
   FullTasIdles.cs, FullTasResets.cs, FullTasStates.cs, FullTasTransplant.cs (enginesim/Cli). Keep `FullTasGoalList.cs` as a
   `static class FullTas` (GoalList, HeaderGoals, LabelOf, ReadGoalList, SolvedGoals, plus `CurrentInputs`, `GoalsHeader`).
   `EconomyRow`/`Door`/`BoxTree`/`DoorToOpen` → Program.cs; speed probes → TimeLossFloor.cs; Map.cs visits → empty
   (`ponytail:`). Remove `macros`, `join-check`, `--waste-audit`; **keep a `--ticks-out` path for Full-TAS runs** (the 1x
   aligner, `align-1x`, and `tools/economy-diff.py` use `fulltas --check --ticks-out`): move it to `check --one-run` or keep
   `fulltas --check` as a thin replay. Check: build, no `partial class FullTas`, `check`, `run-selftest`, and
   `timeloss floor`, `map`, `techs-used`, `align-1x` each run once.
8. **Docs**: enginesim/README.md "Course --auto" (511-682), docs/fulltas.md, docs/v2-handoff.md item 3, Program.cs help.
   Optional separate commit: remove the dead outlook card (Solver.cs `_solveOutlook`/`_solvePulls`, web `OutlookCard`).

## Risks

Greedy segments (a sooner waypoint leaving a state that dies later: the score decides; caps keep rebuilds cheap);
throughput/memory (`SegmentSearch`'s 16 workers; measure, lower per-solver budgets for course runs if needed); a converged
polish ends early (exit 0 = Finished); old `course-auto\` files are left alone.

## Acceptance (offline only; agents never click Optimize, rule 48)

A. Record C4 `WJ_DS`, 20 min: best ≤ old, wall ≤ 22 min, peak memory ≤ old. B. Planted gain found. C. Farm C8
`WJ_DS_DJ_BS` 20 min: lap ≤ old, every `-loop-` file steady. D. Waypoints parity. E. `check`, `run-selftest` pass. F. A
target where `solve` needs FirstFinish (e.g. tree27to10 `WJ_DS_DJ_BS`) reaches a first finish in about the old time.
