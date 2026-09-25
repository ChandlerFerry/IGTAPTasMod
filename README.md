# IGTAP TAS Studio

Recording, replay, editing and route solving for IGTAP, driven from a browser studio. **Route rules are in
[`AGENTS.md`](AGENTS.md); read them first.** Open work is tracked in GitHub issues.

## Components

| Piece | What it does |
|---|---|
| `TasPlugin.cs` | BepInEx plugin: route runner, recorder, HTTP API on `http://127.0.0.1:17426/`, attempt results, settings. |
| `MacroEngine.cs` | Injects route inputs at the 50 Hz physics boundary. Presses and releases do exactly what `Movement.Update` does, including buffer expiry timers. Shared with `enginesim`. |
| `TurboRunner.cs` | "Turbo": steps `Movement.FixedUpdate` → `courseScript.FixedUpdate` → `Physics2D.Simulate` → `Movement.Update` in a loop inside one frame (`Physics2D.simulationMode = Script`). Every script's `Invoke` timer (Movement's and the world's, e.g. a spring's 0.15 s cooldown) is intercepted and fired on the tick the frame loop would fire it (≥50 fps semantics); the world's timers still pending when a run ends fire then. ~45× real time; verified identical to real-time runs. |
| `Solver.cs` | The studio's Optimize tab (captures the start state, runs the offline `solve`, shows its progress; agents run `solve` themselves, see `AGENTS.md` rule 7) and `SaveSolverRoute`, the single gated save for category routes. |
| `EngineSimBridge.cs` | Endpoints the offline engine uses: `trace`, `submit` (turbo is the judge), `raycast`. Also judges offline bests (`enginesim/offline-best/*.json`) automatically once the game is in the world and idle, so they reach the studio. |
| `enginesim/` | The game's own `Assembly-CSharp.dll` running in .NET on a Unity shim and a C# Box2D port: millions of runs without the game. See [`enginesim/README.md`](enginesim/README.md). |
| `web/` | Next.js studio, served by the plugin. |
| `simulator/` | Older hand-written physics model (FastSim). Only an idea generator now; not used by the solver. |
| `tools/` | `enginesim-capture.py` (traces), `enginesim-redeploy.py`, `validate.py` (re-run every category route in turbo), `audit-traces.py` (rule 3 over captured traces), `fidelity-sweep.py` (FastSim only). |

## Build and deploy

```powershell
dotnet build .\IGTAPTasMod.csproj -c Release     # web UI + plugin, copied into the game
```

The plugin DLL is locked while the game runs: close it first (`taskkill //IM IGTAPfullGame.exe //F`), build, relaunch
`IGTAPfullGame.exe`, then `POST /api/oracle/bootstrap` (or load a save) to get onto a course.
`python tools/enginesim-redeploy.py` does all of that for a plugin-only build.

## Studio

- **Top bar:** `DO NOT MODIFY CAPABILITIES`. When on, the plugin never writes an unlock, and the solver/turbo tools
  refuse (setting `lockCapabilities` in `settings.json`, `POST /api/settings/lock-capabilities {locked}`).
- **Top bar:** `ONLY ROUTES I CAN RUN` (kept in the browser). When on, each course/transition in the sidebar shows only
  its fastest route the save's own unlocks can execute (`unlockLevel` in `/api/state`: the widest category the save's
  wall jump / dash + air dash / air jump cover, not a category playback's); a course that needs more says so.
- **Routes:** select, edit, duplicate, favorite (favorites are never deleted by any tool), run, record.
  Playing a category route (`<course>_<NO_WJ|WJ|WJDASH|WJ_DASH_AJ> <time>`) applies that category's unlocks for the attempt, then
  restores the save's.
- **Sidebar:** each course, then transitions, bonuses last, with its fastest route per category, from its first
  category up (a bonus only has its first category: the unlocks it needs); every other route is under "Other routes".
- **Course checkpoints are always off:** the plugin pins the game's checkpoint setting (`isRespawningAtCheckpoints`)
  to false, so a death respawns at the course's reset point, never at the last checkpoint touched, and the in-game
  toggle stays off.
- **Transitions:** a recording named `Course1_To_Course2`, `Course2_To_Bonus2` or `Bonus2_To_Course3` (optionally with
  `_WJ`, `_WJDASH`, ...) → Recordings → *Create transition*. It adds a transition to `courses.json` (ids 201+, token
  `1to2`, `2tobonus2`, `bonus2to3`; category floor = the name's suffix, else the save's unlocks) and the recording as
  the seed route `<token>_REC <recording>`. A transition starts at the course's start (a bonus's: where its
  `Bonus<n>_To_…` recording starts), and finishes when the next course's start gate starts that course's timer, or on
  touching a 32 px box around the bonus's start. It is timed in physics ticks from the run start (dash and spring
  ticks included); finishing any course on the way doesn't count, and whatever happens after the finish (a death)
  isn't part of the route: a seed from a recording to a bonus stops one tick after the player reaches the end box.
  Played in the studio, a finished transition then brakes (holds against its momentum, then lets go) so the player
  stops instead of coasting into the next course's spikes. Optimize and category routes (`1to2_WJ 14.56`) work as
  for courses.
- **Chains** (`config/IGTAPTasMod/chains.json`, tab *Chains*, sidebar *CHAINS*): the game in order after an unlock,
  each segment starting exactly where the one before ended (position, speed, held jump, timers; no teleport).
  Defaults: `wj` (WJ) = `1`, `1to2`, `2`, `2tobonus1`, `bonus1`, `restart`, `bonus1to2`; `dash` (WJDASH) = `2`,
  `2tobonus2`, `bonus2`, `restart`, `bonus2to3`, `bonus3`, `restart`, `3`. A segment is a course id (finishes at its
  end gate), a bonus (its reward box), or `<from>to<to>` (the next course's start gate starting its timer, or the 32 px
  box at a bonus's start; one courses.json doesn't have, like `bonus1to2`, needs no recording: it starts wherever the
  chain arrives). `restart` is the game's quick restart (QuickReset: `Movement.respawn(true)`, nothing else) as the next
  segment's first act: back to the running course's reset point, else the last checkpoint touched. The chain starts
  like a category route (quick restart at its first segment's start, 3 stable ticks). Every segment, a course's too,
  is timed in physics ticks from its start, so a chain's time is the sum (a course segment counts the walk to its
  start gate; a course may already be timing, e.g. Course 3 after `bonus2to3`).
  - A segment's routes are `<chain>/<segment>_<CAPS> <time>` (`wj/2tobonus1_WJ 9.10`), saved only by the gate, which
    replays them from where the chain's current routes before them end. *Solve* on a segment captures that start
    (the chain's routes before it, replayed in turbo) and searches offline, seeded with the segment's own and the
    standalone routes of its course/transition. A new best re-times every later segment of the chain; the first one
    that no longer finishes (or fails a gate check) and every one after it get ` stale` appended (kept as seeds).
  - *Run chain* (Chains tab), *▶ Play* next to the chain in the sidebar, or *Run whole chain* in the top bar while one
    of its segment routes is selected plays the whole chain in real time with the chain's unlocks (enabled once every
    segment has a route). *To here*, or selecting a segment's route and *Run route*, plays the chain through that
    segment. One result per chain, with its splits.
  - Turbo and offline runs from a segment start see a settled world (the world's own timers, e.g. a spring's cooldown,
    fired when the capture ended); real-time playback carries them across the boundary.
- **Optimize:** pick course and capabilities, press Solve. The plugin captures the course start and world in turbo
  (`enginesim/optimize-c<course>-<caps>.json`), writes the course's routes as seeds, and runs
  `IGTAP.EngineSim solve` (repo `enginesim\Cli\bin\Release`, build it first; cores − 2 threads, 60 min, stops after
  5 min without a new best). Every offline best is replayed in turbo; one that holds up is saved as
  `<course>_<CAPS> <time>` through the gate and replaces slower non-favorite routes of that category.

## Solver

Searching is offline (`IGTAP.EngineSim solve`, [`enginesim/README.md`](enginesim/README.md#use)), from the command line
or the Optimize tab; the game only captures traces and judges new bests.

- **Seeds:** the trace's inputs, `--seed` traces (the Optimize tab passes every route whose name starts with `<course>_`,
  and for a chain segment also `<chain>/<segment>_`), plus an empty route. A chain segment's seed best is submitted too
  (its seeds were made from another start).
- **Wider categories:** a route is valid in every category with more unlocks (NO_WJ ⊂ WJ ⊂ WJDASH ⊂ WJ_DASH_AJ), so the offline
  `solve` submits each best to its own category and every wider one; a wider category is never slower than a narrower.
  Bonuses have only their first category (`CourseDef.HasCategory`); submit, trace and the solver refuse any other.
- **Explore (Go-Explore):** archive keyed by 48 px map cell × "course timer running", each cell keeping the earliest input
  prefix that reached it. Pick a cell (rarely picked first, timed cells ×3), resume there, append a random 1–3 chunk burst
  (stick −1/0/1, jump with a 1–40 tick hold, dash if allowed; superbounce offline). A prefix that ends mid-hold keeps
  holding for 0–60 ticks instead of releasing at the resume, which would cut every jump crossing a cell boundary. Only
  arrivals with a snapshot are filed (one reached after the inputs ran out would replay its prefix from the start).
- **Optimize:** mutate the best finish (cut, insert, dash/dash-jump/hop + retime, drop a press, re-aim, change a jump's
  hold length, move a jump press or dash ±1–8 ticks). 50/50 with explore. A mutation that keeps the route's length
  stops once its state fingerprint (`Simulation.Fingerprint`) equals the best's at the same tick with the same inputs
  ahead: it would finish exactly like the best.
- **Scoring:** the game's own timer (`courseScript.currentPathTime`). A finish counts only when the solver's course stops
  while tracking.
- **Gate:** `SaveSolverRoute` replays the route from the course start and rejects it (`REJECTED …` in the solver log,
  `rejected` in the submit reply) unless it finishes on the course in the same time, every jump/dash/jump-cut buffer
  the game holds (checked every physics tick, dash ticks included) came from a press or release `MacroEngine` made
  within the game's window (stamped in physics ticks, so a dash that starts late and its dash-jump are judged where they
  really happened), and the unlocks match the category on every tick.

## Game facts

- Score by the game timer, not "finish tick − start tick"; they differ on dash routes (cause not identified).
- Course starts (quick-restart point; the player spawns 12 px lower): C1 (2241, −292), C2 (4783.43359, 1878.2251),
  C3 (9505, 6967.01465), C4 (20200.4004, 10554.5449), C5 (−3281, −295.455078: its `resetPoint`, the course 1 layout
  mirrored). Courses live in `config/IGTAPTasMod/courses.json`: a course's id is the game's `courseNumber`, bonus
  areas hold 101+ (bonus1 = 101, bonus2 = 102, bonus3 = 103). `minCapabilities` is the course's first category: C1
  `none`, C2 `wj`, C3 `wjdash`, C4 and C5 `wjdashaj`, bonus1 `wj`, bonus2 and bonus3 `wjdash`. Narrower categories are
  refused by the solver, submit and trace.
- Bonuses finish when the player touches a free secret box's buy trigger (`endX/endY/endW/endH`, 127.5 × 6): bonus1
  starts in the Course 2 secret shaft; bonus2 on the ledge where `Course2_To_Bonus2` arrives (7670, 5207), box course 3
  `GLOBALcash` (8622, 5623); bonus3 at Course 3's start, box course 3 `GLOBALclonespeedmax` (11200, 7364). Bonuses
  and transitions are timed in physics ticks from the run start (`MacroEngine.PhysicsTicks`), checked every tick.
  Until 2026-09-24 bonuses counted input ticks, which skip dash/spring ticks: `bonus1_WJDASH 5.40` was really 5.70.
- Floating origin: once the player is 17500 px from the world origin, `FloatingOrigin` moves every root object by the
  player's position and adds it to `currentOrigin`. Course coordinates are absolute (world = absolute + currentOrigin);
  Course 4's start is past the threshold, so the world shifts as soon as the player gets there. Automated runs park the
  player at the start and let the shift happen before capturing the start state, as a real attempt would.
- `Time.fixedDeltaTime` is 0.0199999921, so float counters get an extra tick (wall coyote lasts 8 ticks, not 7).
- Buffers: jump press 0.12 s (0.2 s mid-dash), dash press 0.12 s, jump cut after release 0.26 s.
- A clip into a wall corner can make the foot raycast see ground; with a buffered press that fires a full ground jump
  (looks like a wall jump). Legit only with a real press inside the buffer window.
- Spikes (`spikeScript`): the first overlap tick is Enter; each later tick is a Stay that adds 1 to `counter`. Death at
  `counter > graceFrames` (1 on the level's spikes), or at once while dashing (`killPlayerOnDash`). So a non-dashing
  graze of up to two ticks is survivable in the real game; routes may use it (user decision 2026-09-24).
  `IGTAP.EngineSim path` prints every spike Stay along a route.
- Superbounce: dash into a wall, jump 0–5 ticks later (inside `dashBoostMax` = 6 frames) → `SpecialWallJump(.6, 1,
  up * dashBoostUpwards = 410)`.
- Air jump: `Velocity.y = 745 + max(vy, 0) / 2` (vy after this tick's gravity). Allowed at vy < 200 after a normal
  jump (ground, wall, air, dash-jump), vy < 700 after a spring or superbounce, so a stacked one reaches ~1086; it keeps
  momentum. A jump release cuts vy to 50 at the first tick within 0.26 s where 150 < vy < 580 (normal jumps only), so a
  normal jump or air jump needs ~27 held ticks for full height; an air jump of 931+ can be released at once. Metal walls,
  springs and landing refill it; a foot ray over a ledge top counts as landing even while rising (only the front ray is
  gated by vy < 200).
- Real-time play has frame-timing variance (same route: 126/127/132 ticks); turbo is deterministic.
- Decompiled game code for reference: `obj/Movement.decompiled.cs`, `obj/courseScript.decompiled.cs`.

## What the plugin changes in the game

Audited 2026-09-24. No game method's logic is replaced; the patches only read state, feed input, or skip saves.

| Where | What it does | Why it's not a rule change |
|---|---|---|
| `Movement.getPlayerControlledInput` prefix | Sets `MoveAxis` and presses/releases jump and dash exactly like `Movement.Update` does for a real button (same buffer flags and expiry timers). | This is the controller. The game's own code then does all movement. |
| `Movement.CheckForSuperJump` prefix | Presses jump on a chosen dash frame (dash-jump input). | Same press a player makes during a dash. |
| `Movement.FixedUpdate` prefix / postfix | Starts queued runs; ends a bonus or transition run on its end box; counts physics ticks. | Bookkeeping only. |
| `courseScript.startTracking` / `stopTracking`, `Movement.onDeath` | Read the course time and end the run (a transition's on the next course's timer start). | Read-only; the game still does everything it normally does. |
| `Saveloader.autosave` / `manualSave` | Skipped while a category's unlocks or a turbo run are applied. | Keeps our settings out of your save; the game plays the same. |
| Category unlocks | Solver runs and category playback set `wallJumpUnlocked`, `dashUnlocked`, `maxAirDashes`, `airDashesLeft` (`CategoryAbilities`) and restore them after. Off with DO NOT MODIFY CAPABILITIES. | Defines the category; these are the game's own progression values. |
| Run start | Quick-restarts to the course start (the game's `respawn`) and waits standing still. | A player's quick reset. |
| `Rigidbody2D.interpolation = None` during runs | Makes physics independent of render timing. | Changes how a render frame's pose feeds a jump nudge; see issue #1 (real-time check with interpolation on). |
| Turbo only | Steps physics itself (`Physics2D.simulationMode = Script`), fires every script's `Invoke` timers on the tick a ≥50 fps game would (world timers still pending at the end of a run fire then), doesn't wait out the 0.05 s dash freeze. | Verified: turbo results match real-time playback for every route. |

Removed 2026-09-24: `/api/routes/run` position offsets (`firstJumpPositionOffsetX`, `courseStartPositionOffsetX`), which moved the player.
Removed 2026-09-24: game-judged search (`tools/swarm.py` headless workers, `tools/turbo-search.py`, `tools/solve.py`,
`/api/turbo/evaluate`, `/api/simulation/import-plan`); search offline with `enginesim` instead.

## Safety

- Nothing the plugin does in turbo or offline is saved to the game; unlocks it sets for a run are restored.
- Real-time runs use Unity at native speed; never change Unity's clock or fixed timestep.
- Preserve source routes; candidates become new routes.
