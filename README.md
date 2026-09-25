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
  wall jump / dash + air dash / air jump cover, not a category playback's); a course with no such timed route is hidden.
- **Top bar:** `SHOW TAS OVERLAY` (on by default; `showOverlay` in `settings.json`, `POST /api/settings/overlay {shown}`).
  The game draws the player's hurtbox (red; collides with `Default` and `Spikes`: what spikes and gates touch; the
  `Movement.normalCollider` body box is not drawn), every spike collider on screen (orange; its physics shapes), and
  the spike grace: while the hurtbox is inside a spike, it and the spike shapes it touches turn green and a label above
  it counts the steps (`SPIKE 1/2`, `SPIKE 2/2`; `1/1 · DASH` while dashing, `✗` on the killing step, `· GROUNDED`
  when the game counts the player as on the ground there), and for 1.5 s after it leaves, `SPIKE GRAZE 2/2 SURVIVED`
  (see *Spikes* below). On the left, the inputs playback presses, in columns: `TICKS` (how many physics ticks the
  row's inputs stay the same; 1 tick = 20 ms), `STICK` (← → ↑ ↓, `→ 53%` for a stick short of full, `·` for none),
  `JUMP` while held, `DASH` on its press tick. Newest last, the current row highlighted, with the run's tick count and
  time. A chain lists every segment under its route's name. Solver and turbo runs are not listed. The overlay only reads
  playback; it never changes an input.
- **Stick values are a controller's:** playback passes a route's stick (`rawMoveX`, `rawMoveY`) through the game's own
  `Movement.NormaliseInput` (`MacroEngine.ControllerAxis`) before the tick uses it, as the game does with a real
  controller: x ends up 0, ±1 or 0.3–0.8 in size, y 0 or ±1. Before 2026-09-24 playback set it raw, so a route could
  creep at 0.1 or run at 90 %, which no controller does; routes that relied on that no longer finish as saved.
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
- **Chains** (`config/IGTAPTasMod/chains.json`, tab *Chains*, sidebar *CHAINS*): the game in order, each segment
  starting exactly where the one before ended (position, speed, held jump, timers, Cash and upgrades; no teleport).
  Defaults: `wj` (WJ) = `1`, `1to2`, `2`, `2tobonus1`, `bonus1`, `restart`, `bonus1to2`; `dash` (WJDASH) = `2`,
  `2tobonus2`, `bonus2`, `restart`, `bonus2to3`, `bonus3`, `restart`, `3`. A segment is a course id (finishes at its
  end gate), a bonus (its reward box), `<from>to<to>` (the next course's start gate starting its timer, or the 32 px
  box at a bonus's start; one courses.json doesn't have, like `bonus1to2`, needs no recording: it starts wherever the
  chain arrives), or a purchase: `buyreward`, `buyclone`, `buywj` (Course 1's `CashPerLoop`, `Clone` and `Wall jump`
  upgrade boxes, `Courses/course 1/localUpgrades/…`), which finishes when the game itself buys that box
  (`upgradeBox.DoBuyUpgrade`: enough Cash, box active, its 0.2 s touch cooldown over), timed in physics ticks like a
  bonus. `restart` is the game's quick restart (QuickReset: `Movement.respawn(true)`, nothing else) as the next
  segment's first act: back to the running course's reset point, else the last checkpoint touched. The chain starts
  like a category route (quick restart at its first segment's start, 3 stable ticks). Every segment, a course's too,
  is timed in physics ticks from its start, so a chain's time is the sum (a course segment counts the walk to its
  start gate; a course may already be timing, e.g. Course 3 after `bonus2to3`).
  - **Repeats:** a token may appear any number of times. Its first occurrence is keyed by the token, the k-th by
    `<token>#<k>` (`1`, `1#2`, …, `buyreward#12`); the key names the segment's routes and shows in `/api/state`
    (`chains[].segments[].key`, with its `category`).
  - **From a new game:** `"newgame"` as the first entry (not a segment) starts the chain from a fresh game in memory:
    `POST /api/session/newgame` loads the Overworld scene the way the menu's New Game does with no save file, except
    that `Saveloader` neither loads nor saves for the rest of the session (its `IsSaving`/`isLoading` are false before
    its `Awake`); `Movement.Start`'s NORMAL case puts Cash at 0 and the player at the scene's `respawnPoint`, nothing of
    ours moves it. It replies once the player is in the world and its zone settled with `position`, `respawnPoint`,
    `cash`, `unlocks`; `/api/state` shows `freshGame: true` while that game is loaded (`/api/oracle/bootstrap` ends
    it). The chain's start state is that game's first settled tick. *Run chain* loads a new game itself and starts
    segment 0 on that tick (no quick restart, no unlock written, no stable wait); solving or capturing a segment needs
    a fresh session and says so otherwise. `newgameto<N>` (only as segment 0) runs from the spawn to course N's start
    gate starting its timer. The plugin never writes an unlock in such a chain, so it runs with DO NOT MODIFY
    CAPABILITIES on: each segment's category is the chain's, at least WJ for every segment after a `buywj`
    (`newgame/1#22_WJ 2.18`; the `buywj` segment itself is `newgame/buywj_NO_WJ …`).
  - A segment's routes are `<chain>/<key>_<CAPS> <time>` (`wj/2tobonus1_WJ 9.10`, `newgame/buyreward#3_NO_WJ 1.52`),
    saved only by the gate, which replays them from where the chain's current routes before them end. *Solve* on a
    segment captures that start (the chain's routes before it, replayed in turbo) and searches offline, seeded with
    the chain's routes for the same token (every occurrence, stale ones too) and the standalone routes of its
    course/transition. A new best re-times every later segment of the chain; the first one that no longer finishes
    (or fails a gate check) and every one after it get ` stale` appended (kept as seeds).
  - A purchase segment offline: `python tools/enginesim-capture.py chain newgame K` (loads a fresh game first when
    needed) writes `start-chain-newgame-K.json`, whose `finish` is the box's buy trigger (the 127.5 × 6 strip under it,
    read from the live collider) and `purchase` the box, its cost, the Cash and its cooldown at the start; then
    `IGTAP.EngineSim solve <that trace>`. Offline has no economy (`enginesim/cuts.txt`), so it finishes on touching the
    strip; every offline best is judged in turbo, where only the game's own purchase finishes it.
  - *Run chain* (Chains tab), *▶ Play* next to the chain in the sidebar, or *Run whole chain* in the top bar while one
    of its segment routes is selected plays the whole chain in real time with the chain's unlocks (enabled once every
    segment has a route). *To here*, or selecting a segment's route and *Run route*, plays the chain through that
    segment. One result per chain, with its splits.
  - Turbo and offline runs from a segment start see a settled world (the world's own timers, e.g. a spring's cooldown,
    fired when the capture ended); real-time playback carries them across the boundary. Turbo snapshots carry the
    economy: every Cash/upgrade value (`globalStats.currencyLookup`, `globalUpgradeDict`, each `localUpgrades`), every
    upgrade box's state and every course's clones (and `UnityEngine.Random`, which clones roll); turbo counts the boxes'
    buy cooldowns down and runs the clones (`clonesScript.Update` at 50 fps, without the drawing) every tick.
- **Optimize:** pick course and capabilities, press Solve. The plugin captures the course start and world in turbo
  (`enginesim/optimize-c<course>-<caps>.json`), writes the course's routes as seeds, and runs
  `IGTAP.EngineSim solve` (repo `enginesim\Cli\bin\Release`, build it first; cores − 2 threads, 60 min, stops after
  5 min without a new best). Every offline best is replayed in turbo; one that holds up is saved as
  `<course>_<CAPS> <time>` through the gate and replaces slower non-favorite routes of that category.
- **Optimize many** (right of Solve): a matrix of every course/transition/bonus × category and every chain segment;
  tick cells (a row or column header ticks all of it; *All*, *With a time*, *None*; kept in the browser) and press
  *Optimize N selected*. `POST /api/solver/start {queue: [{course, capabilities} | {chain, segment}, ...], minutes}`
  solves them one after another in matrix order (a chain's earlier segments first), each exactly like Solve but for up
  to `minutes` (stall limit min(5, minutes)). A target that can't start when its turn comes (a chain segment whose
  earlier routes don't exist) is skipped. `POST /api/solver/skip` ends the current target, `/api/solver/stop` the run;
  `solver.queue` in `/api/state` lists each target and how it ended.

## New-game run (in progress, 2026-09-24)

Goal: one chain `newgame` from a New Game: spawn → Course 1, then `1` + `buyreward` ×10, then Course 1 / base reward /
clones until 700 W, `buywj`, then the `wj` chain's segments. Every loop goes back through `restart`, which (Course 1's
checkpoint touched) lands at Course 1's quick-restart point, so every course and purchase segment starts from the same
state and the standalone `1_NO_WJ 1.68` route (109 ticks) fits every course segment.

- **Plan:** `python tools/plan-newgame.py [--loops 10] [--beam 60 --depth 70] --write` searches the purchase order and
  writes the `newgame` chain into the game's `chains.json` (not in the repo; rerun to regenerate). Its segment times
  (`COURSE`, `TO`, `MOVE`, `REPEAT`, clone lap `P`) are estimates: replace them with solved times and rerun. Current
  plan: 61 segments, wall jump at ~117 s after the first course segment, ending with a ~27 s wait under the wall jump
  box (the search stops buying at 12 clones / reward 21; worth re-examining with a deeper search).
- **Economy facts:** Course 1 pays `1 + base-reward buys`; base reward costs 1, then `ceil(prev × 1.1)` (1..11, 13, 15,
  17 …: the first 10 loops break exactly even); clones cost 8, then `ceil(prev × 1.153)` (8, 10, 12, 14, 17, 20 …);
  a clone pays `ceil(0.1 × reward)` per lap, lap = best Course 1 run (1.68 s ⇒ 84 ticks), a new clone spawns every
  lap / N. So the first Course 1 finish should be the fastest route. A bump before the box is affordable still starts
  its 0.2 s cooldown. Real time pays clones per render frame (turbo: 50 fps), so leave a tick or two of slack before
  each purchase that depends on clone income.
- **Done:** `POST /api/session/newgame` (fresh game in memory, spawn (−60.6, −307.1), Cash 0, save files untouched,
  verified); chain parses (61 segments with keys/categories, verified); segment 0 `newgame/newgameto1_NO_WJ 5.50`
  solved offline and saved through the gate.
- **Not yet exercised:** any purchase segment through the gate, turbo economy stepping/restore against real time,
  a real-time *Run chain* of `newgame`. Next: for K = 1, 2, … `python tools\enginesim-capture.py chain newgame K`, then
  `IGTAP.EngineSim solve <traces>\start-chain-newgame-K.json` (see enginesim/README.md); check segment 2
  (`buyreward`) is accepted by the gate first, it's the first purchase.

## Solver

Searching is offline (`IGTAP.EngineSim solve`, [`enginesim/README.md`](enginesim/README.md#use)), from the command line
or the Optimize tab; the game only captures traces and judges new bests.

- **Seeds:** the trace's inputs, `--seed` traces (the Optimize tab passes every route whose name starts with `<course>_`,
  and for a chain segment also `<chain>/<segment>_`), plus an empty route. A chain segment's seed best is submitted too
  (its seeds were made from another start). A standalone route seeding a chain segment carries its start (`align`: the
  course's quick-restart point, 12 px lower); `solve` first lands, walks there and stands still with stick-only inputs
  (`SeedAlign`, a few tenths of a second lost), then plays the route, so a route already made always gives the segment a
  finish to optimize from. It prints what each alignment did.
- **Start sync:** a quick restart mid-dash skips `endDash`, leaving `dashCooldown` at the dash's 100 s lock; the start
  sync (`NormalizeQuickRestartState`) starts dash-ready, so a run cut mid-dash can't take the dash away from later runs.
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
  and C2 `none`, C3 `wjdash`, C4 and C5 `wjdashaj`, bonus1 `wj`, bonus2 and bonus3 `wjdash`; transition `1to2` `none`
  (the wall clip). Narrower categories are refused by the solver, submit and trace.
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
- Wall ground jump (no wall jump needed): the foot rays are placed by `facingRight`, the body collider by `localScale.x`
  (offset ±5), and `determineSpriteDirection` flips both after the tick's raycasts. Facing away from a wall with the
  back edge less than 10 px from it, a one-tick stick tilt toward the wall swings the collider into the wall; Box2D
  pushes only part of the overlap out in one step, so the next tick's front foot ray (`x ± 25`, `y − 18`) starts inside
  the wall's polygon, hits at distance 0 (`queriesStartInColliders`), and sets `onGround` when vy < 200. A jump press
  on that tick is a full 805 ground jump. Tilting away again on that same tick stops the push-out, so a second one
  works from the smaller overlap left (partial tilts, 0.05–0.7; a full tilt moves too far). Both tilts toward the wall
  must last exactly one tick. `1_NO_WJ 1.68`: jump before the start gate, rise beside the block's right face (x 2528),
  wall ground jumps at y −66 and y 153 (a jump cut first gets vy under 200 sooner), then over the block to the end.
- Spikes (`spikeScript`): the first overlap tick is Enter, which spikeScript ignores; each later tick is a Stay that adds 1
  to `counter`. Death on the Stay where `counter > graceFrames` (1 on the level's spikes), or on any Stay while dashing
  (`killPlayerOnDash`). So a graze of up to two ticks is survivable in the real game, one if still dashing on the second;
  routes may use it (user decision 2026-09-24). `chain-dash-4_wjdash 3.04` dashes into the spike wall at tick 58 (Enter)
  and jumps off it at tick 59 (Stay, counter 1, dash over). `IGTAP.EngineSim path` prints every spike Stay along a
  route (`--verbose` adds `onGround`).
- Spike edges: the foot rays (`Movement.FixedUpdate`, y −18, 22 down, x by facing) are Ground-only (mask 128) at −15,
  +6, +25 and Spikes-only "blocking" (mask 64) at −9 and +21. Grounded when −15 hits and −9 is clear, +6 hits and both
  blocking rays are clear, or +25 hits, +21 is clear and vy < 200. So the player counts as landed (and can ground jump)
  with spikes under an outer foot ray (−15 or +25) as long as the blocking ray beside it (−9 or +21) misses them; where
  the hurtbox touches those spikes, the 2-tick grace leaves time to jump off. This is probably the community's
  "spike-edge jump" (not confirmed). enginesim runs these rays and `spikeScript` unchanged, so searches already can use
  it; the overlay shows it as `SPIKE n/2 · GROUNDED`.
- Superbounce: dash into a wall, jump 0–5 ticks later (inside `dashBoostMax` = 6 frames) → `SpecialWallJump(.6, 1,
  up * dashBoostUpwards = 410)`.
- Air jump: `Velocity.y = 745 + max(vy, 0) / 2` (vy after this tick's gravity). Allowed at vy < 200 after a normal
  jump (ground, wall, air, dash-jump), vy < 700 after a spring or superbounce, so a stacked one reaches ~1086; it keeps
  momentum. A jump release cuts vy to 50 at the first tick within 0.26 s where 150 < vy < 580 (normal jumps only), so a
  normal jump or air jump needs ~27 held ticks for full height; an air jump of 931+ can be released at once. Metal walls,
  springs and landing refill it; a foot ray over a ledge top counts as landing even while rising (only the front ray is
  gated by vy < 200).
- Real-time play has frame-timing variance (same route: 126/127/132 ticks); turbo is deterministic.
- New game (no save file): `globalStats.Awake` creates every currency and global upgrade at 0; `Movement.Start`'s
  NORMAL case sets Cash 0, zone 1 and puts the player at the scene's `respawnPoint` (logged by `/api/session/newgame`).
  Course 1 pays `Cash += ceil(reward)` when its timer stops (`courseScript.stopTracking`); reward = 1 + one per
  `buyreward`. A box buys on `upgradeBox.OnTriggerStay2D` when its `boxCooldown` ≤ 0 (then 0.2 s; `FixedUpdate` counts
  it down), Cash ≥ `upgradeCost` and the box is active (`DoBuyUpgrade`; the wall jump box sets `wallJumpUnlocked`
  and costs 700). Course 1's boxes: `CashPerLoop` (x ≈ 1744), `Clone` (x ≈ 1547), `Wall jump` (x ≈ 1357); the buy
  trigger is a 127.5 × 6 strip under each box (y ≈ −206.5). Clones (`clonesScript`) have no colliders but pay Cash
  (on screen when one reaches the end of its path, off screen when it spawns). A quick restart while no course is
  timing goes to `respawnPoint`, which after Course 1's checkpoint is Course 1's quick-restart point (2241, −292).
- Decompiled game code for reference: `obj/fulldecomp/*.cs` (the whole game), `obj/Movement.decompiled.cs`,
  `obj/courseScript.decompiled.cs`.

## What the plugin changes in the game

Audited 2026-09-24. No game method's logic is replaced; the patches only read state, feed input, or skip saves.

| Where | What it does | Why it's not a rule change |
|---|---|---|
| `Movement.getPlayerControlledInput` prefix | Sets `MoveAxis` and presses/releases jump and dash exactly like `Movement.Update` does for a real button (same buffer flags and expiry timers). | This is the controller. The game's own code then does all movement. |
| `Movement.CheckForSuperJump` prefix | Presses jump on a chosen dash frame (dash-jump input). | Same press a player makes during a dash. |
| `Movement.FixedUpdate` prefix / postfix | Starts queued runs; ends a bonus or transition run on its end box; counts physics ticks. | Bookkeeping only. |
| `courseScript.startTracking` / `stopTracking`, `Movement.onDeath` | Read the course time and end the run (a transition's on the next course's timer start). | Read-only; the game still does everything it normally does. |
| `upgradeBox.DoBuyUpgrade` postfix | Ends a purchase segment when the game bought its box. | Read-only; the game decides the purchase. |
| `Saveloader.autosave` / `manualSave` | Skipped while a category's unlocks or a turbo run are applied, and in a fresh new-game session. | Keeps our settings out of your save; the game plays the same. |
| `Saveloader.Awake` prefix | In a new-game session (`/api/session/newgame`) sets `IsSaving` and `isLoading` false: no load, no autosave. | The game's own switches (its vman mode sets the same); the game starts as a New Game without a save does. |
| Category unlocks | Solver runs and category playback set `wallJumpUnlocked`, `dashUnlocked`, `maxAirDashes`, `airDashesLeft` (`CategoryAbilities`) and restore them after. Off with DO NOT MODIFY CAPABILITIES. | Defines the category; these are the game's own progression values. |
| Run start | Quick-restarts to the course start (the game's `respawn`) and waits standing still. | A player's quick reset. |
| `Rigidbody2D.interpolation = None` during runs | Makes physics independent of render timing. | Changes how a render frame's pose feeds a jump nudge; see issue #1 (real-time check with interpolation on). |
| Turbo only | Steps physics itself (`Physics2D.simulationMode = Script`), fires every script's `Invoke` timers on the tick a ≥50 fps game would (world timers still pending at the end of a run fire then), doesn't wait out the 0.05 s dash freeze; counts the upgrade boxes' buy cooldowns down and runs the clones' `Update` (no drawing) per tick; restores Cash, upgrades, boxes and clones with the player. | Verified: turbo results match real-time playback for every route (the economy stepping, added 2026-09-24, not yet checked against real time). |

Removed 2026-09-24: `/api/routes/run` position offsets (`firstJumpPositionOffsetX`, `courseStartPositionOffsetX`), which moved the player.
Removed 2026-09-24: game-judged search (`tools/swarm.py` headless workers, `tools/turbo-search.py`, `tools/solve.py`,
`/api/turbo/evaluate`, `/api/simulation/import-plan`); search offline with `enginesim` instead.

## Safety

- Nothing the plugin does in turbo or offline is saved to the game; unlocks it sets for a run are restored.
- Real-time runs use Unity at native speed; never change Unity's clock or fixed timestep.
- Preserve source routes; candidates become new routes.
