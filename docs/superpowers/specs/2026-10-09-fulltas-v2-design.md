# Full-TAS v2: one command, exact plans, breakage caught before it is built on

Branch `solver-v2`. Approved direction: the analysis and change list of 2026-10-09 (this session), with the user's
corrections: breakage must be caught faster than "within the hour", and a join between two pieces of a route must cost
no idle ticks.

## Why (measured 2026-10-09)

- **Routes break silently.** On `6b52557`, Zip% (`fulltas/Zip%_NONE 2899.60`, "complete") replays to goal 807 of 1492
  and TouchGrass-ANY% to 407 of 428; nothing flagged either. 64 commits changed the simulator since Oct 1; no check runs
  the complete runs, `validate.py` skips Full-TAS, there are no hooks and no CI, and the 636-trace replay corpus has no
  runner.
- **The route is one tape.** Every input is valid only for the exact state all earlier inputs left, so an engine fix, a
  game patch or a faster earlier lap breaks everything after it, and the planner's movement table (estimated durations,
  the player placed at a spot) never matches the build, so plans drift and are re-made.
- **Laps don't transfer.** A quick restart resets position and velocity exactly but carries the last landing and jump
  heights (`lastHyperY`, `heightOfLastJumpStart`), facing (`facingRight`, `wallJumpFacing`), a running or buffered dash
  (`dashActive`, `dashFramesRemaining`, `dashCooldown`, `dashBuffer`, `dashDirection`, `preDashMomentum`), ground type and
  short timers, plus the frame phase (tick mod 5). Course records are solved from a parked start with several of those
  written (`CourseStart.Apply`), so they miss from a run's restart; today's workaround pads every lap with 6-21 idle ticks
  (`docs/fulltas.md` "Course laps from a quick restart").
- **Economy is most of a run.** Ticks whose next goal is a lap or an economy-box buy: Tree% 71%, FalseEnding%-280 75%,
  Zip% (first 807 goals) 74%. Standing still 8+ ticks: 4.0%, 0.7%, 5.1% (Zip%: one 1,424-tick stand before `buytree6`).
  No run file has its `idles` written.
- **Speeds.** Engine replay 7-9k ticks/s per thread (Zip% whole: about 16 s). 1x Fast playback 1.46x real time at
  3440x1440 (73 ticks/s).

## Goals

1. An engine change that alters any complete run or reference trace is refused at commit time, in under a minute.
2. Every new best the solver finds is confirmed by the game in turbo within minutes, and a disagreement names the goal,
   the tick and the field.
3. A route is a chain of segments; a segment is reused wherever it replays exactly, and a join costs no idle ticks.
4. A plan is scored by building it: no estimated move durations, so the plan and the route are the same thing.
5. Waits are plan steps: known before building, listed per best, and turned into laps or buys when that is faster.
6. One command per run (`solve-run <trace>`, threads only); it always holds the best complete confirmed route, improves
   it until stopped, and reports what it can't do as a fix the user can make (an avoid box, a waypoint).
7. The old Full-TAS solver stack is deleted once the new one matches or beats every complete run.

## Non-goals

- No change to the README rules, the gate (`/api/fulltas/submit`, `SaveSolverRoute`), course records, the course
  Optimize tab or the plugin's playback. No game-logic patches. No Rust.
- No write to game state to make a join exact: every join is inputs only.

## Part 1: catching breakage

### 1.1 One-command clean build

`dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o <dir>` fails on a clean checkout because the Shim and
Runtime projects run Prep and Shim from hard-coded `bin\Release` paths that `-o` never fills. Resolve them through
MSBuild (`GetTargetPath` on the referenced project) in `enginesim/Shim/IGTAP.UnityShim.csproj` (`MonoFloatSemantics`)
and `enginesim/Runtime/IGTAP.EngineSim.Runtime.csproj` (`RetargetGame`). Done when a fresh `git worktree` builds with
the one command.

### 1.2 `check`: the pre-commit gate

`IGTAP.EngineSim check [--accept] [--full]`, no game needed:

- **Complete runs:** every `runs/<run>.json` with `"section": "complete"` and a saved best in the config folder's
  `fulltas/<run>.json`: replayed from its trace (`fulltas --check` logic), one process per run in parallel. Its result
  (goals reached, each goal's input/physics/held ticks, live time, end reason) is compared with
  `enginesim/check/baseline.json` (committed). Today's baseline records what each run actually reaches on `6b52557`
  (Zip% 807, TouchGrass-ANY% 407): the check fails on any change, and a broken run shows as broken until it is repaired.
- **Reference traces:** the corpus traces under 20 MB (565 today), sharded over all cores into `replay` processes; the
  sorted per-trace summaries are compared with `enginesim/check/replay.golden.txt`. A golden file rather than "every
  trace matches", because 20 traces differ today; a change in which ones differ, or where, fails. `--full` adds the large
  traces.
- `--accept` rewrites both files: an intended change (a fidelity fix) is committed together with its new baseline, so
  every changed result is in the diff.
- `plan-selftest`-style self-checks of the new solver (Part 3) run too.

Target: under 60 s on this machine. Output: one line per run or trace that changed, with the first goal or tick that
differs.

`.githooks/pre-commit` (enabled by `git config core.hooksPath .githooks`, set up by `tools/setup-hooks.py`) builds the
staged tree to a private folder and runs `check` when staged files touch `enginesim/**`, `runs/*.json`, or the shared
root sources compiled into the engine (`FullTas.cs`, `MacroEngine.cs`, `CourseStart.cs`, `TreeLevel.cs`, ...: the list
in `IGTAP.EngineSim.Runtime.csproj`). A failing check blocks the commit and prints the diff.

### 1.3 Build identity

Every engine output (run file, segment library, failure facts, check baseline) carries `engine`: the commit hash the
engine was built from plus a dirty flag (`git describe --always --dirty`, embedded at build). Failure facts and segment
validations are keyed by it, so a fidelity fix clears stale facts by itself (today `MoveTable.Build` is bumped by hand).

### 1.4 Confirming every new best in turbo

- The engine already submits each strictly faster complete run to the gate (`Submitter`, `FullTas.cs:2140`). The gate's
  reply already carries per-goal `splits` (`FullTasVerdict`); the new solver compares them with its own per-goal
  physics and held ticks and emits `confirm {ok, goal, engine, game}`. This is the first signal, within the gate's two
  turbo passes (seconds to a few minutes).
- On a disagreement the solver asks the plugin for a turbo trace of that route under a unique name
  (`/api/enginesim/trace {fulltas, ticks, name, world: true}`) and runs `replay` on it from the goal before the first
  differing one, streaming (`replay` learns to read `ticks` incrementally and to start at a tick). It reports the first
  tick and field that differ, writes `fulltas/<run>.confirm.json`, and stops improving that run: nothing is built on a
  disagreement.
- A best the game rejects is not kept as the solver's best.

### 1.5 1x confirmation (later phase)

The current best of each run is played at 1x with Fast playback when the game is idle, logging per tick (the probe's
rows); an offline aligner compares positions and Cash with the engine's `--ticks-out` rows by physics tick, held frames
accounted for. The first 1x log of each complete run is kept as its 1x reference and joins `check`.

### 1.6 Process

`AGENTS.md`: one agent edits `enginesim/` at a time; every engine change goes through `check`; long searches run on
private builds from committed code.

## Part 2: segments and zero-tick joins

A **segment** takes the run from the state at one goal to the next goal: its inputs, the physics ticks and held frames
it took, and where it started.

```json
{ "id": "s9f2k1", "to": "farm:6", "from": { "after": "buy:buyclone6", "x": 26174.6, "y": -476.4, "phase": 3,
  "facing": 1, "dashing": false, "ground": true }, "unlocks": "WJ_DS_DJ_BS", "inputs": "<Solve.EncodeTicks>",
  "ticks": 63, "held": 0, "source": "search", "engine": "6b52557" }
```

- **Library:** `fulltas/<run>.segments.json` in the config folder; a run also reads the libraries of its `seeds` runs.
  Harvested from every saved best (cut at its goal ticks) and filled by searches.
- **Reuse is checked, not assumed.** From an actual state, the candidates for a goal are the segments to that goal with
  the same unlocks that start within 64 units (sorted by distance, then matching phase, facing, dash and ground). Each is
  replayed from the actual state (a few dozen to a few hundred ticks, milliseconds); the fastest that reaches the goal is
  used. Whatever the leftovers are, a segment that replays is exact. The `from` fields only rank candidates.
- **Miss: search from the real state.** No candidate replays: a deterministic piece search (`ExploreFixed`'s
  fixed-seed single-worker solvers stopping at a tick budget) from the actual state, seeded with the candidates' inputs.
  Its result is filed under its own start, so the next time the run arrives that way it is a lookup. No idle padding is
  forced; a search may idle when idling is faster.
- **Failures are facts:** `{from, to, unlocks, budget, engine}` in `fulltas/<run>.failures.json`; the same search is not
  repeated with the same budget on the same engine. A plan that needs it may ask again with four times the budget.
- **Farm loops** fall out: the state after a finish and a restart repeats lap to lap, so one searched lap is reused for
  every lap of the loop.

## Part 3: plans, the builder and waits

### 3.1 Milestones and steps

- **Milestones** are the run's goals that are not economy goals (`FullTasGoal.IsEconomy` false: unlock and tree buys,
  rewards, area goals): the user's. They keep their ids and avoid boxes.
- **Steps** are what the planner chooses between milestones: `lap:N` (a lap of Course N; the goal is `farm:N` when the
  lap starts from the course's own finish, by a quick restart or the drop through the end gate, else `record:N`), `buy:box`, `all:box` (a box bought level after level while standing in it),
  `atoms:N`, `reward:bonusN`, and the milestones themselves. The vocabulary is the economy planner's (`docs/fulltas.md`
  "Economy planner"), which already matches the game.
- **A plan** is the ordered list of steps for the whole run. Its goal list (milestones plus the economy steps' goals) is
  what the run file's `goals` hold and what the gate judges; the run file format doesn't change. Economy goals a user
  writes in `runs/<run>.json` are a starting plan, not fixed.

### 3.2 The builder

Builds a plan into inputs on the engine, step by step, from the run's start:

1. Find the step's segment from the current state (Part 2): a replayed library segment, else a search.
2. A buy that arrives short of its currency **stands** (idle inputs) until the game buys the box; the stand is a
   **wait**: `{step, goal, x, y, ticks, currency, short, perTick}` (short: what was missing on arrival; perTick: the
   income over the stand). A stand whose currency doesn't rise for 2,000 ticks more than the economy's longest quiet
   stretch (`IEconomy.Quiet`) fails the step with `no income for <box>`.
3. A step that can't be built fails with a typed failure (Part 5).
4. After each step the builder keeps the state (`SnapshotStore` bytes) so a later change rebuilds from the step it
   touches, not from the start.

The build is the score: the plan's live time is the built route's.

### 3.3 Waits

Every build writes its waits to `fulltas/<run>.waits.json` and emits them; the studio lists them under the run, longest
first, with what the improver tried in each and why it lost. `idles` in the run file is filled from the same data.

## Part 4: the improver

Starts from the run's best: its goal list as the plan (rebuilt on the current engine: segments replayed or searched again,
waits re-timed), else from the milestones alone (every buy a stand). Then improves until stopped:

- **Moves**, generated in a fixed order, the largest waits first:
  1. Fill a wait: insert `lap:N` before the stand for each course N the library or a search can reach from there; insert
     an affordable economy-box buy; move a later step that doesn't depend on the stand's box ahead of it.
  2. Unaffordable or `no income`: insert an income step (a lap, an economy box, `atoms:N`) before it.
  3. Sweep: delete a lap; swap two adjacent buys; move a buy one step earlier or later; one more or one fewer lap in a
     run of laps; `atoms:N` with the next N up or down.
- **Scoring:** build from the changed step to the run's end. Complete runs compare live time; incomplete ones compare
  goals reached, then the tick of the last.
- **Parallel and deterministic:** a batch of candidates is built on all threads (one Simulation per thread); the best
  gain is taken, ties by move order. The same inputs give the same result.
- **Converged:** a whole sweep finds no gain and no search is pending. The solver reports it and idles; a new engine,
  new avoid boxes or more budget restarts it.
- New bests are written, submitted and confirmed (1.4) at once.

## Part 5: one command and the studio

- `IGTAP.EngineSim solve-run <trace> [--threads N]`: nothing else. Budgets are simulated ticks (segment search 20M
  ticks, raised four times per retry up to 320M); seeds fixed.
- `--segment <goal id>`: improve one segment with a long search from its real start, the run's avoid boxes for that goal
  applied, and keep it when the whole run gets faster (the studio's stretch optimize).
- **Typed failures**, emitted and kept in `fulltas/<run>.failures.json`:
  - `unreachable {from, to, unlocks, budget}`: "no way from (x, y) to <goal> with <unlocks> after N ticks: add an avoid
    box or a waypoint (`at:`) here";
  - `no-income {box, currency}`: "nothing pays <currency> before <box>";
  - `disagree {goal, tick, field}`: the game and the engine part (1.4).
  A milestone that can't be reached stops the run with its failure; anything else just removes that move.
- **Studio:** the Full-TAS tab's Solve button runs `solve-run` (it replaces Reach missing goals and Improve); the run
  shows its best, the waits list, the failures and each confirmation. Stretch optimize calls `--segment`. Events stay
  `@fulltas {json}` lines; new types `plan`, `waits`, `failure`, `confirm`.

## Part 6: cutover

When `solve-run` matches or beats every complete run: it takes the name `fulltas`; delete the old stack (the 26
`enginesim/Cli/FullTas*.cs`, `EconomyPlan*.cs`, `EconomyMoves.cs`, `Polish.cs`, about 19,400 lines, and the plugin and
web code only they use); replace `docs/fulltas.md`'s solver sections with this design's; update README "Layout" and
`AGENTS.md`.

## Acceptance

1. A clean worktree builds with one command; `check` runs in under 60 s and fails on a planted change to the engine.
2. Tree% and FalseEnding%-280 rebuilt by `solve-run` from their bests reach every goal at no more than 763.73 s and
   353.29 s.
3. Zip% and TouchGrass-ANY% complete again, confirmed by the gate.
4. ZipEscape% runs unattended to its last goal or to a typed failure.
5. No lap is padded with idle ticks it doesn't need: every lap in those routes is a library segment or a search from
   its real start.
6. The old stack is deleted and `check` still passes.

## Phases

1. Build fix, `check` with baselines, the hook, build identity, `AGENTS.md`.
2. Segment library, harvest, segment search, the builder; acceptance 2 by rebuilding Tree% and FalseEnding%-280 from
   their goal lists.
3. Waits, the improver, typed failures, `solve-run`; acceptance 3 and 5.
4. Confirmation (1.4), the studio's Solve button and lists, `--segment`; acceptance 4.
5. Cutover (Part 6); acceptance 6.
6. 1x confirmation and references (1.5).
