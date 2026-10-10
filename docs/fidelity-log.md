# Fidelity log

Where the offline engine, turbo or playback first diverged from 1x, and what made them match again. Current behaviour
is the reference in [`enginesim/README.md`](../enginesim/README.md) (Fidelity facts); this is the history. Entries are
ordered as the sections they came from.

## Turbo never rebased

- **Symptom:** a route that crossed the FloatingOrigin sphere played differently at 1x than turbo judged it (the bonus1
  8.28 seed, captured at origin (-20200, -10543), crosses it at tick 334).
- **Cause:** turbo never rebased while 1x did.
- **Fix:** turbo runs the game's own `LateUpdate` and `MoveOrigin` after each tick's `Update` and resumes the coroutine
  right after the next `Physics2D.Simulate`; a turbo snapshot carries `currentOrigin` and a waiting rebase, and a
  restore moves the world back to it.
- **Where:** `TurboRunner.OriginFrame`, `ResumeRebase`, `PutOrigin`.

## The tick after a rebase lost its step

- **Symptom:** `VMAN`'s routes split from turbo at its rebase (tick 1588 of the 39.28 s best: offline 30 units behind and
  grounded a tick early, then 0.6 units behind); every offline route past it died in turbo (at action 470).
- **Cause:** a query's sync after `MoveOrigin` moved the player's body to its transform, the first rendered frame's
  interpolation blend, so the tick lost the rest of its step, and the body stood in the moved world a tick before Unity's
  does; the next tick's sync then took the transform's FixedUpdate lag blend instead of the step's end.
- **Fix:** a query after a rebase rebuilds the moved static colliders only; bodies take their transforms at the next
  physics step's sync, and before it a transform FixedUpdate didn't write goes back to the step's end, as turbo's
  `Place(player, _stepTo)`. The trace now matches turbo but for 1e-3 units of rounding.
- **Where:** `PhysicsWorld.SyncMovedWorld`, `Simulation.Run` (`LagForFixedUpdate`).

## Rebase body snap

- **Symptom:** `VMAN` 46.96 finished offline but died at action 486 in turbo and at 1x; `div-vman-46.96` split at tick
  1675 by 0.00017 u and drifted from there.
- **Cause:** after a rebase inside the run, turbo's pre-step `Place(player, _stepTo)` writes the transform through the
  moved parent, which reads back rounded, and the sync moves the body there every tick. Offline kept the exact body.
- **Fix:** once the run has rebased (`Rebased`), a tick whose FixedUpdate made no write marks the player's transform
  dirty when its read-back differs from the body. Captures taken after the game's own rebases don't snap (turbo neither).
  `fulltas-VMAN` positions now match on every tick (body y 7.7e-6 at 1653 and 1731); isolated in a build without it,
  both VMAN traces split at 1675 and `fulltas-VMAN` at 1590. No other corpus trace changes.
- **Where:** `Simulation.Run` (after `LagForFixedUpdate`).
- **Superseded 2026-10-09** (*Turbo's own transform writes rounded the body*, below): 1x never snaps; turbo's snap was the
  divergence, and both now keep the exact body.

## Trigger touch across a pull-back

- **Symptom:** `div-vman-46.96` tick 2140: the landing dash's -20 write pulled the player 16 u out of the rebased
  `Spikes` composite before the step and the step moved it back in; turbo killed (Stay with `dashActive`), offline sent
  Enter and the run finished.
- **Cause (first model, inferred, wrong):** a static body the rebase moved keeps a swept broadphase proxy, so its
  trigger contacts survive any pull-back. `FindLapsedTouches` tested a side table of swept extents (`_swept`). It fit
  `div-vman-46.96` only.
- **Ground truth:** walk into a trigger, then dash on the next input tick, so the dash's pull-back moves the player out
  before the step and the step moves it back in. Turbo keeps the touch in both cases, and the kill comes on the dash's
  own step:
  - `gt-staticspike-prerebase-c3to4`: the main `Spikes` composite, a static `Rigidbody2D`, with no rebase.
  - `gt-bodyless-dash-freeze-aim` (walk-only control `gt-bodyless-walk-freeze-aim`): the lab spike bed, a body-less
    `BoxCollider2D` under `LabMap freeze-aim`, after a rebase in the run.

  The swept model died a step late on both (215/214 and 9/8 ticks). `fulltas-TouchGrass-ANY%` tick 31084 points the
  same way: turbo bought from the body-less upgrade box `global cash` on Stay across a pull-back, and the swept model
  sent Enter, didn't buy, and split RNG and cash there. The one trigger turbo does let lapse is Course 1's start gate
  (`Start`, on a dynamic body): `c11-bogus-0.56` (see "Trigger touch lapsed at the step's start").
- **Fix:** a trigger on a static body or on no body (the ground body) never lapses. Only pairs whose colliders both sit
  on non-static bodies are tested, and they are tested against the broadphase fat AABBs (`ProxiesOverlap`). The Box2D
  mechanism behind the static/dynamic difference is not established; the rule is fit to the four ground-truth points.
  Kinematic bodies are grouped with dynamic ones by inference: no corpus trigger pair has one. The sweep (`SweepProxies`,
  `_swept`, its snapshot field) is gone. Full corpus (398 traces, verbose) against the swept build: 378 MATCH in both; traces with the
  same tick count as turbo 353 -> 355, RNG identical on every tick 347 -> 350.
  Three outputs change, all towards turbo: the two ground-truth dash traces now die on turbo's tick, and TouchGrass's
  RNG is identical on every tick (cash still splits at 13095, as before). `div-vman-46.96` still MATCHes (death at
  action 486, now through the static rule), and so do `fulltas-VMAN` and `c11-bogus-0.56` (identical on every tick).
- **Where:** `PhysicsWorld.FindLapsedTouches` (`OnStaticBody`), `ProxiesOverlap`.

## Far teleport rebased every other tick

- **Symptom:** a far teleport (a quick restart or death across the map) rebased again every tick or two and the player's
  world position jumped by the origin each time (Test-No-Omni-Skip's windows reached world (-21055, -15606) and ±k ×
  (20933, 14987)).
- **Cause:** the tick's rendered frames were read at the Box2D body, which follows a rebase only at the next sync.
- **Fix:** after a rebase the tick's rendered frames end with the player at the step's end moved with the world
  (`_stepTo`, as turbo's `Place(player, _stepTo)`).
- **Where:** `TurboRunner.Place`, `_stepTo`.

## Replay start pinned past a rebase

- **Symptom:** a route replay to a far course (Course 6 from a fresh game, origin (0, 0)) never started: the player hung
  in the fall animation at the course start's scene position, `Waiting for 3 stable ticks` forever.
- **Cause:** the stable-tick wait pins the player to the scene position it had just after the quick restart. The
  restart put it past Threshold, `MoveOrigin` moved the world back, and the pin put the player where the course had
  been: no ground, never `onGround`, and a rebase again every frame.
- **Fix:** the pin moves with the world: `MacroEngine.WorldMoved` on every `FloatingOrigin.MoveRootTransforms` (1x,
  turbo, snapshot restores) and on the offline engine's rebase.
- **Where:** `MacroEngine.WorldMoved`, `TasPlugin.AfterMoveRootTransforms`, `Simulation.ResumeRebase`.

## Statics shared between solver threads

- **Symptom:** after a rebase, a worker's quick restart or death put the player at `respawnPoint` plus another world's
  `FloatingOrigin.currentOrigin` (and a checkpoint stored `respawnPoint` against it); `TrueEnding-ANY%`'s windows past
  its rebase drew chains of cells ~(12000, 11700) off the route, e.g. world (25147, 23760) and (567, -15539).
- **Cause:** the game's mutable statics were shared by every solver thread, so the last world loaded won.
- **Fix:** `[ThreadStatic]` in the engine's copy of the game, a thread's `World.Load` setting its own world's; the
  visits map is versioned (`VisitsVersion` 2), so the maps drop every visit recorded before this.
- **Where:** `Prep` `ThreadStaticFields`, `World.Load`, `VisitsVersion`.

## Sideways spring lost its push

- **Symptom:** a sideways spring came out 10 u short of 1x (Course 5's `Spring (1)`: `5_WJ_DS_DJ 13.46` finished in
  turbo and offline, and died on `Spikes` at 1x).
- **Cause:** both dropped the push: the tick's frames and the next tick blended from the step's end.
- **Fix:** a write from a physics callback inside the step moves the body at once and drops the interpolation — turbo
  syncs right after `Physics2D.Simulate` when the transform has moved off the body, the engine when the player's
  transform is dirty after `Physics.Simulate`.
- **Where:** `TurboRunner`, `Simulation.Run`, `SpringScript.OnTriggerStay2D` → `Movement.hitSpring`.

## Corner wedge contact order

- **Symptom:** a player wedged in a corner of one composite collider finished 13.14 offline and died in turbo
  (`diverge-c5-13.14` ticks 881-896).
- **Cause:** contact order — the port resolved pairs, the world list, a body's list and stopped contacts in Box2D
  2.4.1's order, not Unity's 2.3.x lineage's.
- **Fix:** Box2D 2.3.x order throughout: new broadphase pairs sorted by proxy id before their contacts are created, the
  world contact list oldest first (first of equally early TOI hits wins), a body's newest first, a contact that starts
  touching goes to the end and one that stops is replaced by the last.
- **Where:** `Box2D/` contact creation and `Collision2D`'s contact list.

## Trace start Invokes one tick early

- **Symptom:** a start inside a jump's `recentlyJumped` window (every omnidash tech demo) ended it a tick early offline.
- **Cause:** `LoadStart` counted a trace start's pending Invokes (`endRecentJump`, the buffer cancels) one tick less
  than turbo's snapshot.
- **Fix:** the due times are counted from the start's next tick, in turbo's snapshot and in `Simulation.LoadStart`
  alike.
- **Where:** `Simulation.LoadStart`, `TurboRunner` snapshot.

## Statue text left on

- **Symptom:** a run that bought the statue left it on, so the next run from the same new game skipped that draw and
  every clone roll after it (`TrueEnding-ANY%`'s 484.96 s route passed the gate once, then stopped at goal 318 on
  every later check).
- **Cause:** `buystatue` turns the statue's `StatuePrestigeText` (an ObjectBobber) on, and `ObjectBobber.OnEnable`
  draws UnityEngine.Random.
- **Fix:** a turbo snapshot keeps every active-scene object's on/off state, and a Full-TAS trace exports its world
  after restoring the start.
- **Where:** `TurboRunner.Scene.objects`, `FullTasTrace`, `ObjectBobber.OnEnable`, `buystatue`.

## Crossing frame step and presentation draws

- **Symptom:** after the rewards fix, a 1x probe of the 356.64 s inputs still split from turbo: Course 1's clone path
  pointer read 0.9999996 at 1x against 0.99999994 after a tick, and the RNG split at tick 7.
- **Cause:** 1x's ticks come 0.0199999929 s apart on the fixed clock, so the crossing frame's Update reads
  Time.deltaTime 0.00214285006, not the model's 0.0021428566 (a clone pointer grows exactly 1 a tick, so the last bit
  decides the tick it ends its path). And the run animation's footstep event (`PlayerAnimationEventProxy.RelayFootstep`
  → `Movement.PlayFootstep` → `Audio.PlayAudio`, two draws) and floating numbers fire only at 1x.
- **Fix:** `FrameGrid.Step(0)` is the measured 0.00214285006 at the playback rate; an animation footstep or floating
  number in a Full-TAS run gives `UnityEngine.Random`'s state back (README exception 11); Movement's own `PlayFootstep`
  calls in the physics step draw as before. Clone state (timers, pointers, counts) then matches 1x on every tick of the
  356.64 s inputs (16,675 ticks). A dash freeze logs the tick's physics count twice in both (turbo's trace and a probe):
  compare by the engine's physics count, not the trace's `tick`.
- **Where:** `MacroEngine.FrameGrid`, `TasPlugin.PresentationDraw`.

## Course rewards refreshed only at 1x

- **Symptom:** `FalseEnding-Any%` 356.64 s (Bonus 2 added; judged in turbo) died at 1x at action 5257 after 238 goals,
  short of Cash; the 361.34 s route before it played at 1x to the tick. A 1x probe split from turbo's Cash at physics
  tick 6798, 65 ticks after `reward:bonus1`: a Course 1 clone paid 13 at 1x, 9 in turbo.
- **Cause:** `courseScript.Start` repeats `UpdateReward` every 5 s (`InvokeRepeating`, phase `Random.Range(0, 5)`), and a
  global `cashPerLoop` buy updates only its own course's reward. At 1x the timer carried Bonus 1's increase to Course 1
  within 5 s; turbo (Unity's invoke clock stands still in a turbo run) and the engine never ran it. The phase differs
  from session to session, so even 1x wasn't reproducible for a route whose Cash depends on it.
- **Fix:** a Full-TAS run turns the timer off at 1x and updates every active course's reward every 250 physics ticks
  from its start (`MacroEngine.RefreshRewards`: `TasPlugin.RefreshFullTasRewards`, `Economy.RefreshReward`); README
  exception 10. The 356.64 s inputs now die at action 5257 in turbo and offline alike, Cash and RNG identical on every
  tick (`div-fe-356.64`).
- **Where:** `MacroEngine.CountPhysicsTick`, `FullTasPlugin.Playback.cs`, `Simulation`, `Economy.RefreshReward`.

## Buy-max box texts drew on the invoke clock

- **Symptom:** `fulltas/FalseEnding-Any%_NONE 361.78` (judged in turbo) died at 1x at action 5354 after 271 goals, on the
  climb to `buy:buydj`. A 1x probe matched offline Cash on every tick to physics tick 14994, 88 ticks after
  `buy:buystatue`, where Course 3's off-screen clone paid 768.8M at 1x and 1037.9M offline (1.35x: the previous spawn's
  fast-clone roll). Clone Update order was the same (lowest instance id first), and the RNG states matched as sets.
- **Cause:** the statue turns Courses 1 and 2's boxes into buy-max boxes. Each box's `updateMultibuyText` runs from an
  `InvokeRepeating` (Unity's invoke clock, a 0.2-0.4 s rate) and turns its "buy N" text on (`setBoxText`), and that
  text's `ObjectBobber.OnEnable` draws `UnityEngine.Random`. At 1x these draws came one at a time on the timer's
  frames (ticks 14906-14916); turbo doesn't run the invoke clock, and offline made them when the box was touched. The
  count of draws caught up by tick 14930, but Course 3's roll at 14944 fell between them: 60 at 1x (no fast clone), under
  40 offline. Found with the probe's new `clone` and `rng` rows (every `SpawnClone`, and every call of a game method
  that draws, with its frame) against `fulltas --check --ticks-out`'s spawn and draw rows.
- **Fix:** `ObjectBobber.OnEnable` is presentation (a text's bob phase): in a Full-TAS run its draw is given back at 1x and
  in turbo like a footstep's (`TasPlugin.PresentationDraw`, README exception 11). The engine never made the
  texts' draws (it models a box's touch, not its multibuy timer) and now skips the statue text's too in a Full-TAS run
  (`Economy.Activate`), so the statue text's draw goes in all three.
- **Where:** `TasPlugin.Awake` (presentation drawers), `Economy.Activate`, `ProbeRunner` (`clone`, `rng` rows),
  `FullTas.Check` (`--ticks-out`).

## Clones stepped once a tick

- **Symptom:** `fulltas/FalseEnding-Any%_NONE 359.70` (judged in turbo, 283 goals) died at 1x at action 3164. 1x and
  turbo matched through goal 124 (tick 8935); at 1x `buy:buyreward2` came at 8940, turbo's at 8970, and from there 1x
  bought only on a subset of turbo's buy ticks: the player was where turbo had it, the Cash was not.
- **Cause:** turbo (`StepClones`) and the engine (`Economy.Step`) ran `clonesScript.Update` once per physics tick on
  0.02 s; the game runs it in each rendered frame (1/280 s). A spawn set `timeSinceLastClone` to 0 and dropped the
  overshoot (the pre-2026-09-30 build; see *Game build 25630570: clones and buy-max*), up to a tick per spawn per tick
  against up to a frame at 1x, so clones spawned less often and paid later.
- **Fix:** both step the clones in each frame of the tick's grid on that frame's `Time.deltaTime`
  (`MacroEngine.FrameGrid.Step`), 0 in a freeze's or pause's repeats. The engine's `--check` of the 359.70 route now
  meets every goal on 1x's tick (125 of them, `buy:buyreward2` at 8940) and stops there (strict buys). `zip-plan`'s
  clone frame is 1/280 s.
- **Where:** `TurboRunner.Run`, `Simulation.InterpolatedFrames`, `Shop.StepClones`, `ZipPlan` (deleted 2026-10-04).

## Clone order changed between sessions

- **Symptom:** the same route's clone rolls swapped at physics tick 16409 and one course's spawns ran 2 ticks late
  from then on (Course 3 before Course 1 in one game session, after it in the next).
- **Cause:** turbo used `FindObjectsSortMode.None`, whose order changed between game sessions.
- **Fix:** clones step in a fixed order, in turbo and the engine alike: instance id lowest first, 1x's Update order (a 1x
  probe of `FalseEnding-Any%` 356.64 s, tick 15637: Course 2 before Course 3 in one frame; the first fix, highest first,
  swapped their rolls and Course 3 paid 538M against 1x's 727M at tick 15688).
- **Where:** `TurboRunner.Run`, `Runtime/Economy.cs`.

## Floating number pool drained

- **Symptom:** a long replay drained the pool; the next pop threw out of `courseScript.stopTracking` (no finish) or
  `clonesScript.DestroyClone` (the clone stays and pays again), at a point that depended on how much real time refilled
  the pool since the last run.
- **Cause:** their pool of 200 is only refilled by their own `FixedUpdate`, which turbo doesn't step.
- **Fix:** turbo spawns no floating numbers (`FloatingNumberScript.ActuallySpawnText` off for the run).
- **Where:** `FloatingNumberScript.ActuallySpawnText`, `courseScript.stopTracking`, `clonesScript.DestroyClone`.

## Raycast tree walk returned the last shape

- **Symptom:** a dash step-up onto a ledge landed 2.3 units low.
- **Cause:** the port's unclipped tree walk returned the last shape in tree order instead of the nearest hit.
- **Fix:** the nearest hit wins across a composite's shapes too.
- **Where:** `Box2D/` raycast tree walk, `PhysicsWorld` raycasts.

## Squish death on the VMAN wall

- **Symptom:** a VMAN dash into the wall at 20776,15441 died squished in turbo and ran on offline.
- **Cause:** offline's `Collider2D.Distance` (GJK alone) stopped at 0 instead of returning the overlap depth.
- **Fix:** negative for overlapping colliders (separating-axis over the shapes' faces, minus both radii), as in Unity.
- **Where:** `Collider2D.Distance`, Movement's squish check (`squishDistance`, `SquishGraceFrames`).

## Presses at the input hook

- **Symptom:** traces captured before this differ wherever a dash met a wall with a jump pressed that tick.
- **Cause:** presses went in at the input hook, where the game had already read the buttons (Movement.cs:1033-1056).
- **Fix:** a route's presses go in at the tick start (`MacroEngine.BeforeTick`, after the pending Invokes), and a dash
  that hits a wall takes its input tick in that tick after all (`MacroEngine.WallTouch`).
- **Where:** `MacroEngine.BeforeTick`, `MacroEngine.WallTouch`, `Movement.Update`.

## Layers read once at load

- **Symptom:** an intangible zip mover's layer change was missed: the engine kept the layer it loaded with, and turbo
  never brought the mover back to Ground within a run.
- **Cause:** the engine read layers once at load and turbo never resumed the `WaitForSeconds(0.15)` coroutine within a
  run.
- **Fix:** a layer write rebuilds that object's colliders (`PhysicsWorld.LayerChanged`), and turbo and the engine step
  the movers' coroutines on the run's ticks.
- **Where:** `PhysicsWorld.LayerChanged`, `PlatformMover.MakeIntangible`, `TurboRunner`, `Scheduler`.

## Facing flip delayed a trigger

- **Symptom:** a flip that rebuilt the player's box into a spring's trigger entered in turbo one tick later, when the
  step's positions overlapped (`div-c4-ff` tick 322, with fast fall).
- **Cause:** a touch from shapes the step's transform sync rebuilt for a new scale was counted like a touch at the
  start of the step.
- **Fix:** a trigger touched only at the start of the step enters, except when the touch comes from rebuilt shapes —
  those enter on an ordinary post-step overlap.
- **Where:** `PhysicsWorld`'s trigger overlap test.

## Wall jump box the live save had bought

- **Symptom:** 1to2 NONE bought Course 1's Wall jump box offline and wall-jumped; the live save had it bought already,
  so turbo died.
- **Cause:** whether turbo can buy a box depends on the live save.
- **Fix:** none needed — save-dependent by design: a purchase that changes the category's unlocks ends the run
  (`Category unlocks changed`) and the gate rejects such a route, so a candidate is judged against the save it plays
  in.
- **Where:** `Runtime/Shop.cs` (the buy rule), `Category unlocks changed`.

## RNG state shifted before the judged run

- **Symptom:** a restored RNG state had already advanced when the judged run began.
- **Cause:** turning a box back on in `TurboRunner.Restore` runs `upgradeBox.OnEnable` (two draws).
- **Fix:** `TurboRunner.Restore` sets the RNG state last, after `RestoreEconomy` and the Vman restore.
- **Where:** `TurboRunner.Restore`, `upgradeBox.OnEnable`.

## Restores without contact priming

- **Symptom:** a start touching a collider (a start under an upgrade box) ran differently in turbo from run to run.
- **Cause:** the first tick after a restore reported a stale Exit plus a fresh Enter instead of Stay.
- **Fix:** one muted physics step at the restored pose rebuilds Unity's contacts, so the first tick reports Stay.
- **Where:** `TurboRunner.Restore`.

## Field captures copied into every snapshot

- **Symptom:** a VMAN search with 28 workers ran out of memory within minutes (a Full-TAS run touches ~100 scripts
  (bought boxes, springs, gates); copied into every snapshot they were 130 KB of each 160 KB).
- **Cause:** every touched script's and course's field capture was copied into every snapshot.
- **Fix:** a capture reuses the previous one's array while none of its fields changed.
- **Where:** `Simulation.CaptureShared`, `FieldState.Matches`.

## Snapshot every tick stalled the workers

- **Symptom:** the workers waited on the GC most of the time (C4 WJ_DS_DJ, 30 threads: ~1,000 runs/s before,
  ~7,000-8,000 after).
- **Cause:** a best's replay snapshotted every tick of Course 4's ~670-tick route on each drift.
- **Fix:** a snapshot every 16 input ticks; a mutation resumes from the one at or before its first changed tick, and
  its replay copies the best's record before it.
- **Where:** `OfflineSolver.StateEvery`, `OfflineSolver.Worker.Replay.CopyPrefix`.

## A snapshot per cell per worker

- **Symptom:** the same place was stored once per worker, and a worker never explored from a cell another worker found.
- **Cause:** each worker kept its own archive with a snapshot per cell.
- **Fix:** one shared explore archive of input prefixes (no snapshots), per-worker snapshot pools and cached draws
  (on C4 WJ_DS_DJ with 32 threads the replays are ~23% of all ticks simulated).
- **Later:** cells got owners (`Cell.Owner`: the worker that last kept the snapshot) and each worker draws mostly from
  its own cells with today's weights, so more threads no longer mean fewer cached picks (C4 WJ_DS_DJ, 24 threads: 22-37%
  cached before). A stale eviction record no longer evicts a freshly used snapshot.
- **Where:** `OfflineSolver._archive`, `StatesPerWorker`, `CachedDraws`, `ArchiveStatus`, `Cell.Owner`.

## Persisted states across runs

- **Symptom:** a saved state's fingerprint never matched the next solve's; restoring a snapshot that didn't list a
  collider put back a shape generation no replay would have (a composite whose sources had toggled, a body-less
  collider after a rebase) and the state stopped reproducing; a capture after a restore could leave a changed collider
  unreported.
- **Cause:** `System.HashCode` is seeded per process; `Pristine` was whatever the first rebuild made; a capture after
  a restore reused the restored snapshot's changed-collider list.
- **Fix:** a fixture's shape fingerprint is a plain deterministic mix (`PhysicsWorld.FingerprintOf`); `Pristine` is the
  fixtures the world loaded (`ColliderState.Tracked` marks the rebuilds that join `_changed`); a capture after a
  restore re-derives its changed-collider list (`PhysicsWorld._changedCapture`).
- **Later:** with `Pristine` the loaded fixtures, a collider a restored snapshot doesn't list holds what the world
  loaded, so a capture after a restore shares the restored snapshot's list again until a collider is rebuilt (*A
  restored run depended on the runs before it*; `determinism` checks stored snapshots in a fresh simulation).
- **Where:** `PhysicsWorld.FingerprintOf`, `ColliderState.Pristine`, `ColliderState.Tracked`,
  `PhysicsWorld._changed`, `PhysicsWorld._changedCapture`.

## Grid rebuilt on every change

- **Symptom:** `WJ_DS_DJ_BS` cost a full-world rebuild four times per dash (block swap toggles the blue/orange
  composites), with the LOH garbage stalling every worker (C11 at 30 threads: ~110 runs/s, 4,000 ticks/s).
- **Cause:** the static-shape query grid was rebuilt whole on each change.
- **Fix:** a collider whose static fixtures change, or whose static body moves, is regridded alone at the next query
  (throughput ~1,800 runs/s, 205,000 ticks/s).
- **Where:** the scene-query grid behind raycasts, point tests and `Collider2D.Distance`.

## A restored run depended on the runs before it

- **Symptom:** the same edge from the same snapshot ended differently depending on what the simulation ran before it.
  `determinism` on VMAN 45.20 (600 edges from 10 route snapshots and 200 from their mid-run captures, run in order,
  shuffled twice and in a second process): 21 of 2,256 restores gave another physics state than the capture saw, 5 of
  800 runs ended in another state between orders (in one, edge 134, the player too: x 6661.75 or 6661.7505 after 151
  ticks) and one in the second process. `enginebench`'s 17-tick branches from input 800 of 48.18,
  run after 3,000 from input 200, ended differently in 37 of 3,000 from a fresh process's. An earlier timed bench saw
  the player's x velocity 460 or 0 on one edge.
- **Cause:** a restore put back what the snapshot held, not everything the next step reads. The player's box's contact
  mask (what the contact filter reads) was rebuilt only when the box changed: `Simulation.Restore` writes the box's
  `excludeLayers` directly, so the box kept the last run's mask (a dash's excluded layer, -194 against -66) until it
  changed again. A capture between steps also dropped the syncs and moves pending for the next step (a body's transform
  or scale sync and teleport, a collider rebuilt or rescaled since the step), and a restore left the last run's
  `Rescaled` flags. A rebuilt collider's scale, layer and mask came back only when its layer differed. Box2D's touch
  counter wasn't rewound (only the order of touch numbers matters, so this changed no run, but the state wasn't the
  saved one).
- **Fix:** the physics snapshot holds the player's colliders' scale, layer and mask, each rebuilt collider's with its
  fixtures (unlisted ones go back to what the world loaded), and the pending body and collider flags; `b2WorldState`
  holds the touch counter. `determinism` digests the whole physics state (every body, fixture, proxy, contact in list
  order with its manifold and touch order, the move and pair buffers, every tree node, the host's collider and pair
  state) at each capture and after each restore, and every run's end state. Now every restore equals its capture and
  every run agrees across orders and processes on 45.20, 44.92 and 48.18, and stored snapshots loaded into a fresh
  simulation restore the same state. The branches above now end as a fresh process's do.
- **Where:** `PhysicsWorld.Capture`/`Restore` (`PhysicsSnapshot`, `Built`), `b2World.SaveState`/`LoadState`,
  `Cli/Determinism.cs`.

## Zip movers once unlocked (WJ_DS_DJ_BS_ORB_ZIP_OM)

- **Symptom:** with `zipMoversUnlocked` on, every Course 7 offline best failed its turbo check (no finish), and the
  offline solver crashed on a mover's end boom (`MissingMethodException: AudioSource.set_minDistance`).
- **Cause:** turbo resumed a mover's tween with the Unity frame's `Time.deltaTime` (the host's shim used the fixed
  step), turbo snapshots kept a moved mover where the last run left it, the live movers started wherever play had left
  them, and `Audio.PlayAudioAtPoint` was neither cut nor hooked.
- **Fix:** a coroutine step sees the player loop's deltaTime in both (the fixed step after `WaitForFixedUpdate`, the
  playback clock's crossing frame after `WaitForSeconds`); turbo snapshots keep each mover's position and velocity; a
  category start puts every mover at its first stop, still; `PlayAudioAtPoint` is cut with its draws kept. Course 7
  bests then agreed in turbo (7.58 to 6.86).
- **Where:** `TurboRunner.Resume`/`UseCoroutineDeltaTime`/`RestoreMovers`, `Scheduler.Resume`, shim `Time.deltaTime`,
  `TasPlugin.ResetZipMovers`, `hooks.txt`/`cuts.txt`.

## Lab starts one half-tick ahead

- **Symptom:** the stored superbounce lab demo finished offline (`lab replay`) and matched in `tools/tech-check.py`, but
  at 1x and in turbo it did no super and no boost: the overlay marked nothing.
- **Cause:** the plugin starts a demo's engine inside `Movement.FixedUpdate`'s prefix, after `MacroEngine.BeforeTick`
  already ran idle, so the first tick's pre-hook stick read is at rest (the step is pressed at the input hook). Offline,
  a lab run from its placed start ran `BeforeTick` on that tick, so the first tick's stick down sped the wall slide up
  (Velocity.y −37.8 against the game's −27), the dot landed a tick early, and the dash meant for the landing tick came
  in the air in the game. Tech-check couldn't see it: its turbo trace starts after that first tick.
- **Fix:** `Simulation.StartsInsideTick` (set by `DemoStart.ApplyLabStart`, which `lab solve`/`replay` now share) skips
  `BeforeTick` on the first tick from the start. Offline then matches the turbo trace tick for tick (−10504.92 on tick
  11); the demo holds the cling one tick longer to dash on the landing tick. The other five lab demos are unchanged
  (their first tick has no vertical stick).
- **Where:** `Simulation.Run`, `TechGrafts.DemoStart.ApplyLabStart`, `Cli/Lab.cs` `Place`, `labmaps/stored-superbounce.demo.json`.

## Trigger touch lapsed at the step's start

- **Symptom:** Course 11 `WJ_DS_DJ_BS_ORB_ZIP_OM` offline bests of 0.56–0.96 s (the real route is 4.56 s) all failed their turbo
  check (no finish): offline, a landing dash into Course 1's start gate started the timer again on the far side of the
  course, 28 ticks from the finish.
- **Cause:** the landing tick's move reached the gate by 0.2 units (Enter), and the next tick's dash transform write
  (interpolation pull-back) moved the player 20 units out before the step, whose move took it back in. The engine kept
  the pair touching and sent `startGate`'s `OnTriggerStay2D`; Box2D collides a step's contacts before it moves the
  bodies, so in turbo the touch ended there and began again: Enter, and `startGate` starts only on Stay.
- **Fix:** before each step, a trigger pair touching after the last one whose shapes don't overlap at the step's start
  (tested again only for teleported bodies and rebuilt shapes) sends Enter instead of Stay if it overlaps afterwards.
  Scale-rebuilt shapes keep the facing-flip rule. `c11-bogus-0.56` now matches on every tick; the other 264 corpus
  traces replay unchanged. Every Course 11 sub-second best now ends without a finish offline.
  Later narrowed to triggers on non-static bodies ("Trigger touch across a pull-back").
- **Where:** `PhysicsWorld.FindLapsedTouches`, `DispatchCallbacks`.

## Start past the rebase threshold, and its interpolation

- **Symptom:** `tools/tech-check.py` split from turbo on tick 0 for lab demos captured with the player placed past
  FloatingOrigin's Threshold (`freeze-aim`, `boosted-double-jump`: turbo moved the world back after the first step,
  offline never did), and with the player at rest there, `boosted-double-jump` still split on tick 0 by 0.01 u in
  `heightOfLastJumpStart` and `lastHyperY`.
- **Cause:** a trace start carried `rebasing` (MoveOrigin waiting) but `Simulation.LoadStart` dropped it, where turbo's
  resume runs FloatingOrigin's `LateUpdate` again. And the start's interpolation ends (its last step's `stepFrom`/`stepTo`)
  were never exported, so offline started at rest while turbo's first FixedUpdate read the blend of the placement tick's
  fall (0.54 u, alpha 0.98).
- **Fix:** `LoadStart` arms the rebase again when the start has `rebasing`; the bridge exports `stepFrom`/`stepTo` and
  `LoadStart` resumes from them while the player stays where the start put it (a moved start, a lab or probe placement,
  is at rest as before; older traces have none). `boosted-double-jump` matches on every tick to its finish; `high-super`
  (tick 0 -> 67), `reverse-super` (2 -> 16) and `super-ground-bounce` (2 -> 18) split later, at 1e-5 u of rounding.
- **Where:** `Simulation.LoadStart`, `Simulation.Capture`, `EngineSimBridge` (trace start).

## Lab demo started from a stale savestate

- **Symptom:** `stored-superbounce`'s turbo trace and 1x playback started on the Course 1 floor walking left, never on
  the post, so no stored superbounce happened (1x showed only SUPER; `tech-check.py` still matched, turbo against its
  own wrong start). The boosted double jump's first 1x playback kept a `wallCoyoteFrames` from earlier play.
- **Cause:** a lab demo's start was a savestate stored when the demo was installed: the lab fields over a copy of the
  player at that moment (its leftovers included). The game update of 2026-09-28 changed Assembly-CSharp's hash, the
  savestate was rejected, and the start fell back to the course's quick restart.
- **Fix:** every lab start is built from the lab files: value-type Movement fields and hitbox colliders from the start
  trace the demo was solved from (`solvedIn`), the save's own fields kept, then the demo's start placed over it, as
  `lab replay` does. All 8 lab demos match `lab replay` in turbo (0.05 u until the run's last tick) and show their tech at
  1x; the boosted double jump lab drops its `OnWall`/`wallCoyoteFrames` pin (`lab replay` identical without it).
- **Where:** `LabMapService.PlaceLabDemoStart`, `ResetToLabBase`, `TasPlugin.RestoreRouteStart`.

## Spike state leaked between turbo runs

- **Symptom:** `div-vman-46.96.world.json` held `spikeScript.playerKilled = true` on a `Spikes` object, from a gate death
  before it. A trace captured right after a run that died in a spike exported the same (Course 4 `Spikes` 166318:
  `counter` 2, `playerKilled` true), and the next turbo run from a cached start kept it too: that spike wouldn't kill.
- **Cause:** a run that ends in a spike never gets the spike's `OnTriggerExit2D` (the reset): a turbo restore mutes the
  callbacks around the player's old position while it moves it, and a world export right after the run sees it before
  any step. Snapshots didn't hold spike state.
- **Fix:** turbo snapshots keep every spike's `counter` and `playerKilled` and a restore puts them back (a new game's
  start, fresh, has them clear), and the world export clears them first: no start is inside a spike.
- **Where:** `TurboRunner.Snapshot.spikes`, `RestoreSpikes`, `ClearSpikes`, `EngineSimExport.WriteWorld`.

## Cached start judged in another zone's world

- **Symptom:** the offline-best pump logged `no finish in turbo` for good routes in their own category:
  `10_wjdsdjbszip-17.82` right after it judged a Course 4 file (zone 1), and `7_wjds2bszipom-9.10` earlier, which a
  direct submit then saved.
- **Cause:** a cached start restores the player and the snapshot's objects, but not which zone the `ZoneLoader` has
  loaded or the Area 1 state's scenery. After the pump judged a course in the other zone, the cached start ran in the
  wrong world. `Loaded zone 2 for 10` was logged only after the run, by `ParkAtStart`.
- **Fix:** a cached start is dropped when the course's zone isn't the loaded one or its Area 1 state isn't set, so the
  start is captured again, which loads the course's world first.
- **Where:** `EngineSimBridge.PrepareEngineSimRun`, `ZoneReady`/`TargetZone` in `Solver.cs`.

## Course start captured with its zone switched off, and the quick restart's history

- **Symptom:** right after a game launch, `10_WJ_DS_DJ_BS_ZIP 17.84` (the saved record) doesn't finish: submitted
  twice, no finish at physics tick 288. A freshStart trace moments later finishes 17.84. The pump failed
  `10_wjdsdjbszip-15.00` the same way seconds after a launch, but a freshStart trace later finishes it at 15.0002
  (finishTick 742). It is parked in `offline-best/hold-GameOwner2/`, unjudged. With the plugin logging the start gate's
  and zip movers' state (5 launches, `go4-bad-1..5`): 3 of 5 launches failed, each one where the first submit had to
  wait for "loading".
- **Cause:** the start was captured with Course 10's zone switched off. Its `DisableBits` (zone 2's
  `ZoneDisableLoadList.toLoad`: the course's `Start` gate and zip movers; the `courseScript` sits outside the zone) were
  inactive at every failing capture and active at every good one. `RestoreRouteStart` loads the course's zone
  (`ZoneLoader._LoadZone`), but a zone load the game still had queued (`ZoneLoader.LoadZone`'s coroutine, the
  bootstrap's `LoadZone(1, force)`) switched it back, and `CourseLoaded` let a player already parked at the reset
  point start without checking the zone; `ZonesSettled` looked at the zone roots only. So no run from that start started
  the timer: `go3-bad`/`go3-good` have identical player state on every tick up to 191, and at tick 119, where the good
  run enters the start gate and `tracking` goes on, the bad one's course never tracks; at 191 the zip mover the good run
  touches isn't there.
  The quick restart (`Movement.respawn(true)`) also keeps a set of Movement history fields, the RNG state and the clone
  timers, so every capture's start differed with what ran before (`go3-bad` after launch vs `go3-good` after other runs:
  `facingRight` true/false, `wallJumpFacing` (1,0)/(-1,0), `dashDirection` (0,0)/(-1,0), `wallCoyoteDir` 0/1,
  `framesSinceLastDash` 119/326, `MostRecentUpwardsMoveWasNormalJump` false/true, `timeSinceLastAfterimage`,
  `emitWallSlideParticlesThisFrame`, the RNG, `clonesScript.timeSinceLastClone` 10002.85/10010.63; Bonus 5's also
  `heightOfLastJumpStart`, `lastHyperY`, `preDashMomentum`, `finalWallClingTimeLeft`). That was not this failure:
  offline, 17.84 from `start-c10-wjdsdjbszip.json` with every `go3-bad` value, each alone and all together, still
  finishes 17.840218.
- **Repro:** relaunch the game and bootstrap, then at once `POST /api/enginesim/submit` course 10, `wjdsdjbszip`,
  with the 17.84 route's ticks: no finish, twice. Then a trace with `freshStart` finishes 17.84.
- **Fix:** `CourseLoaded` parks only in the course's zone (`ZoneReady`), and `ZonesSettled` also wants the active
  zone's `toLoad` objects on (true in every world export checked, the Full-TAS new games' too); a switch back makes the
  wait start over. The category start's history is fixed (`CourseStart`, one file shared by the plugin and the engine):
  the fields above at a launched game's values (`framesSinceLastDash` 1000, past every window it opens; in the air
  `lastHyperY` 1e8), one RNG state and every clone timer at 10000, set after the quick restart and again when the start
  is captured. The searches load a category trace's start the same way (`Simulation.LoadStart(start, canonical: true)`
  from `Solve.Creator`, a course `--auto` trace's too), so an older trace searches from the start the game judges;
  `replay`/`path` load a trace as captured. Offline, all 64 saved category and farm routes replay exactly the same from
  their start with and without the fixed history (`bonus5_WJ_DS_DJ_BS_ORB_ZIP_OM 4.66` fails offline either way).
  After the fix, 5 of 5 fresh launches judged 17.84 at once (every capture with zone 2 settled and the gate on), including
  the 3 whose first submit waited for "loading".
- **Where:** `CourseLoaded`, `ZonesSettled` in `Solver.cs`; `CourseStart.cs`, `Solver.Evaluate` (start capture),
  `TasPlugin.RestoreRouteStart`; `Simulation.LoadStart`/`SetTarget`, `Solve.Creator`.

## Bonus starts captured in whichever zone the last course left

- **Symptom:** `start-cbonus5-wjdsdjbsorbzipom.world.json` and every `start-cbonus1-*` capture had zone 2 loaded, with
  Zone 1's root (the bonus's own checkpoint included) switched off. After a full `tools/validate.py` run, a bonus5
  trace with `freshStart` waited for "loading" for over 2 minutes.
- **Cause:** `TargetZone` took the zone of the nearest start gate within 1000 u. Bonus starts are checkpoints
  2500-4300 u from any gate (bonus1, bonus2, bonus2to3, bonus5, bonus6, bonus7), so they got zone 0 ("any zone"): no zone
  load, and `ZoneReady` passed in the zone the previous target had loaded.
- **Fix:** `TargetZone` takes the nearest start gate or `checkpointScript` (`ZoneMarkers`: the zone root or
  `ZoneDisableLoadList.toLoad` each sits under). Every target's start is 11-16 u from a checkpoint. Targets that had a zone
  keep it, and those six get zone 1. `ZonesSettled` is unchanged: zone 1's `toLoad` (Courses 1-5 `DisableBits`) shares no
  object with another zone or with `OvergrowthLoadScript`'s lists, and all 166 start captures checked have their zone
  settled. Fresh launch: `validate.py` passes everything it passed before except `bonus1_WJ_DS_DJ_BS_ORB_ZIP_OM 5.02`
  (see below). After a Course 10 submit, the start traces of the six targets each load zone 1 and settle in 2.8-3.4 s.
  3 of 3 fresh launches judged `10_WJ_DS_DJ_BS_ZIP 17.74` (after one zone reload each time the bootstrap's queued load
  switched zone 2 back) and captured bonus5 on the first try.
- **Not this:** `bonus5_WJ_DS_DJ_BS_ORB_ZIP_OM 4.66` is stale. It was solved from bonus5's old start (Course 3's restart,
  (9505, 6955)), before `courses.json` moved the start to Course 3's Checkpoint (2) (6825, 4535) on 9/28. Offline it
  finishes 4.659998 from the old start, and from the new one it runs away from the finish, so it has to be solved again.
  `bonus1_WJ_DS_DJ_BS_ORB_ZIP_OM 5.02`'s validate failure was the start's FloatingOrigin (next entry).
- **Where:** `TargetZone`, `ZoneMarkers` in `Solver.cs`.

## Category start judged at whatever FloatingOrigin the last target left

- **Symptom:** the judge depended on what ran before it. After the validate order, `bonus1_WJ_DS_DJ_BS_ORB_ZIP_OM 5.02`
  failed a submit, a cached trace and a freshStart trace, while a freshStart trace on the launch before finished it.
  `4_WJ_DS_DJ 13.28` passed `validate.py` and then failed its submit and freshStart trace (tick 285) on the same launch
  after bonus start captures. Neither route had changed.
- **Cause:** FloatingOrigin rebases only once the player is 17500 u from scene (0, 0), so a start's scene frame is where
  the last rebase left it, and that depends on the targets before it. The saved captures of Course 4 have 4 different
  origins. The physics runs in the scene frame, and routes are float-sensitive to it. Offline, the same inputs from the
  same world position: 13.28 finishes at origins (-9505, -6955) and (-20200, -10542) and dies at tick 274 at (-6825,
  -4523). 5.02 finishes at (-9505, -6955) and (-6825, -4523) and not at (-20200, -10542). Tonight's records were judged
  at (-9505, -6955): bonus5's old start (Course 3's restart), which the offline-best pump visited. Visits to bonus5's
  new start rebase to (-6825, -4523). A restore's `PutOrigin` shifts the world back by the difference, which also rounds
  according to the origin it comes from.
- **Fix:** one origin per start. The quick restart onto a course, bonus or transition start
  (`RestoreRouteStart`) puts the world where the game's own rebase on the player standing there leaves it:
  `TurboRunner.PutCanonicalOrigin` sets each scene root to its position as the scene loaded it (recorded at origin (0, 0),
  before the first `MoveRootTransforms`) minus the reference, which is the reset point 12 down, where `Movement.respawn`
  puts the player. It also sets `currentOrigin` to minus the reference, and the player then stands at scene (0, 0). The
  start snapshot carries the reference, so each restore puts the world there exactly again. Roots the game moves itself
  (camera, HUD) only shift. The offline engine's category starts (`Simulation.LoadStart` canonical: the searches, `path
  --canonical`) rebase a trace captured elsewhere to the same origin as a rebase of its own. It moves the scene-frame
  fields the start writes (`courseResetPoint`, the interpolation ends, `heightOfLastJumpStart`, and when standing,
  `lastHyperY`) along with the world. A trace captured at the canonical origin loads unchanged. Offline, 13.28 and 5.02
  finish from all 3 origins' captures. All 63 saved course, bonus and transition routes in `validate.py`'s set finish at
  their saved times from the canonical origin. In game (fresh launch): Course 4's start captured after bonus5 and
  after Course 10, and bonus1's after Course 4 and after bonus5, each have one origin ((-20200.4004, -10542.5449) and
  (-7392.42676, -2795.01489)), the player at scene (0, 0), and the same positions for every transform but the UI's and
  particles'. `4_WJ_DS_DJ 13.24` and `bonus1_WJ_DS_DJ_BS_ORB_ZIP_OM 5.02` judged finished after each. `validate.py`'s 63
  routes, submitted in routes.json order and then in reverse: 63 of 63 OK both times, the same time and run ticks for each.
  Full-TAS runs start from a new game and don't go through this.
- **Where:** `TurboRunner.PutCanonicalOrigin`/`RecordPristineRoots`/`Snapshot.canonical`, `TasPlugin.RestoreRouteStart`,
  `Solver.Evaluate`; `Simulation.PutCanonicalOrigin`, `path --canonical`.

## Turbo ran the afterimages on the real frame's deltaTime

- **Symptom:** right after a fresh game launch, a `VMAN` 34.28 trace failed with "Stack empty." in 4 of 5 launches, and a
  1x "New game start" playback of the route's inputs kept running until it timed out.
- **Cause:** `Movement.Update` calls `HandleAfterimages` (presentation only; the offline engine cuts it), which ran in
  turbo on the real frame's `Time.deltaTime`, held for the whole run. On a fast stretch it could make all 21 pooled
  afterimage sprites active (the failing trace went from 20 pooled to 0 by tick 1314; a good one never dropped below 5),
  and `inactiveAfterimages.Pop()` threw out of `TurboRunner.Run`. `EvaluateFullTas` and `Solver.Evaluate` aborted the
  resumed engine only on a normal return, so after the throw it played the inputs on at 1x.
- **Fix:** turbo skips `HandleAfterimages` (a Harmony prefix while `TurboRunner.Active`), and both evaluators abort a
  running engine in `finally`. A failed Full-TAS request logs the whole exception. 5 of 5 fresh launches then traced
  34.28 (23/23), with no playback left running.
- **Where:** `TurboRunner.BeforeHandleAfterimages`, `TasPlugin.EvaluateFullTas`, `TasPlugin.Evaluate`,
  `TasPlugin.PumpFullTasPending`.

## A restore left the other run's body transforms (zip movers)

- **Symptom:** `determinism` on Course 7 `WJ_DS_DJ_BS_ZIP` (`7_wjdsdjbszip-7.96` over `start-c7-wjdsdjbszip.json`):
  467 of 1,137 restores gave another physics state than their capture. The only difference was the Unity transform of
  `ZipMoverMovingPart`, a kinematic body, which kept wherever the run before had left it: restoring input 170 after a
  run from input 60 left it at y 844.54 instead of 1344.54. Restored from a route capture and continued on the route's
  own inputs, 2 of 278 boundaries (inputs 170 and 180) left the uninterrupted run one tick later, and 1 of 79 mid-edge
  captures ended elsewhere. 4 of the 5 route snapshots that store, loaded into a fresh simulation, restored another
  state than their capture. Course 13 `WJ_DS_DJ_BS` and FalseEnding (`fulltas/FalseEnding-Any%.json`) were exact.
- **Cause:** `Simulation.Restore` puts back the player's transform, and `PhysicsWorld.Restore` puts back the bodies in
  Box2D, but nothing restored the other bodies' transforms. A moving body's transform normally follows its body (the
  step writes it back). At the end of a move, though, `PlatformMover.IE_AdvanceToNextState` writes the mover's
  `transform.localPosition`, and the next sync reads that write back into the body (`TransformDirty`). A capture taken
  in that window restored the pending flag while the transform still held the last run's value, so the next sync
  teleported the mover there.
- **Fix:** the physics snapshot holds every body's transform except the player's (position, local position,
  rotations, scales; `PhysicsSnapshot.Poses`), and a restore writes them back. Captures share the list until a body's
  transform changes (bit for bit), so a capture with every body at rest allocates nothing new. `determinism` also
  continues each mid-edge capture on its edge's own inputs, from the same loop budget, after other runs have gone
  between, and requires it to end in the edge's own end state. The stored-snapshot check now saves every snapshot
  before it makes the fresh simulation: a simulation takes over its thread's hooks when it is made. Before, the fresh
  simulation's arrival made 9 of 25 stored edges look different on Course 7, which was a harness artefact. After the
  fix, Course 7 has every restore equal to its capture (1,137/1,137), every continuation equal to its edge (79/79) and
  every stored snapshot restoring its capture (5/5), and is DETERMINISTIC. Course 13 and FalseEnding stay exact. The
  502-trace verbose replay corpus is byte-identical. 5 of Course 7's 10 route snapshots couldn't be stored
  (`SnapshotStore`: collider 448's shapes can't be rebuilt), before and after: fixed in *A stored state lost the
  toggles its simulation never made (block swap)*.
- **Not the cause of** Macros' "restore drift" (median 2.04 u, p95 9.5 u): that was its harness. It replayed
  `inputs[0..stop)`, and on a list that ends there `MacroEngine.Finish` zeroes the stick on the last tick, so every end
  position was a tick short. The same truncated list from a fresh start (no restore) drifts the same. Fixed in
  `FullTasAutoMacros.Apply`: each list runs a tick past the stop. Rerun on this build (`macros --samples 120`, 86,873
  applications on the held-out routes): drift median 0, p95 0; 125 over 0.1, all at 12 anchors of 4 held-out routes
  whose own run ends before the window does (VMAN 34.62 and 34.88 die in a spike at physics tick 296 on today's trace,
  `1_wjdsdj` second finishes at 52, `Test-No-Omni-Skip` 7.92 stops at 90), so the replay has no position there to
  compare with. The hit rate is 5.3% in position and speed (position 12.5%, speed 28.4%; down-dash-landing 22.0%).
  The 2.3% before was on the tick-short lists and a smaller corpus (39,839 applications).
- **Where:** `PhysicsWorld.Capture`/`Restore` (`PhysicsSnapshot.Poses`, `Pose`), `Cli/Determinism.cs`,
  `Cli/FullTasAutoMacros.cs`.

## A stored state lost the toggles its simulation never made (block swap)

- **Symptom:** `determinism` on Course 7 `WJ_DS_DJ_BS_ZIP` (`7_wjdsdjbszip-7.96`): 5 of the 10 route snapshots
  (inputs 25-127) couldn't be loaded into a fresh simulation: "a collider's shapes can't be rebuilt to the snapshot's
  (collider 448 #15)". A Full-TAS solve drops such a stored state and replays the run up to it. Collider 448 is not a
  zip mover: it is `/Grid/colouredBlocks/blueSpikes`, the static `CompositeCollider2D` over the blue spikes' tilemap
  collider (447). 447 is off at load, and the route's dashes (`colouredBlockSwapper.swapBlocks`) had it on at those
  inputs, so the snapshot listed the composite's 16 rebuilt fixtures. The same happened on the other block-swap courses:
  Course 10 zip 3 of 10, Course 10 orb 3 of 9, Course 8 zipom 2 of 9, Course 8 orb 1 of 2, Course 15 zip and orb 2 of
  10 each. On Course 10 zip the snapshot at input 518 loaded with its capture's physics digest, but 5 of its 5 edges
  ended differently from the same edges in the simulation that took it (NOT DETERMINISTIC).
- **Cause:** a simulation tracks a GameObject's active flag, a Behaviour's enabled flag or a layer from its first
  change (`Engine.ActiveChanging`/`EnabledChanging`/`LayerChanging` into `_activeAsLoaded` and the others), and a
  restore puts back only the objects it tracks. A stored state comes from another simulation. The fresh one had never
  toggled 447, so `SnapshotStore.ApplyToggles` (and `Simulation.Restore` after it) left it off. The composite then
  rebuilt with no shapes, and the snapshot's fixtures had nothing to bind to.
- **A second finding, on the orb courses:** `Simulation.Fingerprint` XOR'd every tracked object, including one back at
  its loaded value, so the same state hashed differently depending on whether some earlier run had toggled the object
  (an orb taken, say). `determinism` compares that hash between passes and on continuations, so it reported NOT
  DETERMINISTIC on Course 8 zipom (389/400 runs, 85/88 continuations) and Course 10 orb (375/400, 92/99). Every player
  end state and physics digest matched in those runs. The solver's convergence check (`OfflineSolver`) compares the same
  hash, so it could miss a convergence the same way [inference: not measured]. It would also have hashed the toggles a
  stored state brings in.
- **Fix:** before it applies a stored state's toggles, `SnapshotStore.Load` tracks every object they name that this
  simulation hasn't tracked yet (`Simulation.TrackToggles`). It records each one's value now, which is its loaded value,
  as the change hooks would have. The fingerprint now hashes only the toggles that differ from their loaded value.
- **Results:** `determinism` on the start traces as recaptured on 2026-09-29, before/after. Stored route snapshots:
  Course 7 zip 5/10 → 10/10 (50/50 edges); Course 8 zip 10/10 → 10/10 (archive route); Course 8 zipom 7/9 → 9/9;
  Course 8 orb 1/2 → 2/2 (the route's run ends at input 176); Course 10 zip 7/10 with 30/35 edges → 10/10 with 50/50;
  Course 10 orb 6/9 → 9/9; Course 14 zip and orb 10/10 → 10/10; Course 15 zip and orb 8/10 → 10/10; Course 13
  `WJ_DS_DJ_BS` and FalseEnding 10/10 → 10/10. After the fix every case is DETERMINISTIC: every restore equals its
  capture, every continuation ends as its edge, and every run is identical across orders and processes. Each case's
  run-set digest is the same before and after. The 8/10/14/15 routes are stand-ins: an `archive/` source for 8 and 10
  orb, `judged/10_wjdsdjbszip-15.00` for 10 zip, and the `EngineSpeed` corpus seeds' inputs for 14 and 15. The
  502-trace verbose replay corpus is byte-identical to the build before.
- **Where:** `Simulation.TrackToggles`, `Simulation.Fingerprint`, `SnapshotStore.Load`.

## Walk route played another run than 1x

- **Symptom:** a walk (the studio's Walk route, 10× slower, the human version when as fast) missed where the same route
  at 1x finished (`1_NONE 1.40 human`, 2026-09-29).
- **Cause:** the walk slowed `Time.timeScale` to 1/10. The fixed clock's `Time.captureDeltaTime` is scaled by it, so each
  frame advanced a tenth of the grid step `FixedClockStep` asked for: ten times the frames per tick, every one off 1x's
  grid, so other interpolation blends, other step-ups and other FixedUpdate write origins. Harmless before interpolation
  was always on; since then a frame's time is part of the run.
- **Fix:** a walk leaves `Time.timeScale` alone and paces the wall clock instead: `Application.targetFrameRate` is the
  paced rate / slowdown (28 fps for 10×), so it plays 1x's exact frames, slower.
- **Where:** `TasPlugin.PacePlayback`, `PaceWalk`.

## 1x playback ran slower than real time

- **Symptom:** `fulltas/FalseEnding-Any%_NONE 351.58` took 6:51 of real time at 1x (2026-09-30). The probe `fe-full6`
  played 333.5 s of game time in 402.9 s.
- **Cause:** `Application.targetFrameRate` 280 paced each frame at 1/280 s of wall time, but the fixed clock puts six
  frames in a 0.02 s tick (21.4 ms of wall time at the cap, so 0.933x). Also, a frame that ran past 3.57 ms was never
  made up (fe-full6 averaged 257.7 fps, p90 4.7 ms). The game time was right; only the wall clock lagged.
- **Fix:** playback runs uncapped and waits each frame for the fixed clock's own time since the start (its steps summed,
  a dash freeze's frames included, times a walk's slowdown). Frames after a late one catch up, up to 0.1 s behind. A
  probe keeps the cap. Real time is then the route's time plus its dash freezes (fe-full6 froze 4035 frames, 14.4 s),
  as long as the game can render the ~310 fps it needs to catch up.
- **Where:** `TasPlugin.PacePlayback`, `WaitForFixedClock`.

## Savestate restore kept a death's respawn

- **Symptom:** a savestate restored within 0.6 s of a death (the studio's Restore, a savestate route or probe start, a
  lab placement) teleported the player back to the checkpoint moments later, and the camera panned across the map.
  Restores in a camera room or while falling left the camera on the old room, or up to 1180 below the player.
- **Cause:** `onDeath` queues `Invoke("deathRespawn", 0.6f)`, which a savestate restore never cancelled (turbo's
  restore cancels every invoke). A `camZoneScript` holds the camera until its `OnTriggerExit2D`, and `SnapCamera` used
  the fall look-down offset the last tick before the restore left.
- **Fix:** `SavestateService.Restore` cancels `deathRespawn` only (a blanket `CancelInvoke` would drop a wall jump's
  `reEnableNormalCollider`, which a savestate can't restore), gives the camera back to the player, restores the zoom
  (`camSize`, 0 in older savestates) and snaps with the look-down of the restored speed.
- **Where:** `SavestateService.Restore`, `SnapCamera`, `Savestate.camSize`.

## Buy-max levels bought once a tick

- **Symptom:** a 1x probe of `fulltas/FalseEnding-Any%_NONE 361.68` (`fe36168fix`) split from offline Cash at physics tick
  15216, `buy:buyclone2` right after `buy:buystatue` (the statue made Course 2's boxes buy-max). Both sides paid the same
  costs in the same order (64,314, 77,820, 94,163, 113,938, 137,865, ... 29 levels of Course 2's clone box), but 1x paid
  seven on the touch's tick (Cash -856,766) and six on each tick after (-2,487,077, -7,805,541, -24,497,159), offline two
  (-142,134) and then one a tick (-94,163, -113,938, ...). Offline Cash then stayed off 1x's on every tick to the end of
  the replay (243.48e9 against 247.54e9 at tick 16,413, 1.6% short), and the RNG states parted at tick 15219 and never
  rejoined.
- **Cause:** `IE_BuyMax` bought a level, then `yield return new WaitForEndOfFrame()` (the pre-2026-09-30 build; build
  25630570 waits `WaitForFixedUpdate`), so it bought one level per rendered frame. 1x renders six frames per 0.02 s
  tick on the 280 fps clock (probe frames 96549-96554 are tick 15216's), so it buys the touch's level in the physics step
  and one at each frame's end. Turbo (`TurboRunner.EndOfFrame`) and the engine (`Shop.EndOfFrame`) resumed it once per
  tick, after all six frames: the 29 levels took 28 ticks there against 5 at 1x,
  so Course 2's extra clones (and their payouts) came about 23 ticks late. The 40-level touch that matched turbo tick for
  tick (`enginesim/README.md`, Fidelity facts) showed the engine matched turbo, not 1x.
- **Fix:** both resume the buy-max routines after every rendered frame's Update and clone step, in the tick's frame grid
  (a dash freeze's or pause's repeats included: each is a rendered frame with its own end of frame). Offline, `fulltas
  --check ... --ticks-out` of the 361.68 route gives 1x's Cash on every tick of the probe: 17,352 ticks compared (offline
  tick p against the probe's tick row p + 1), largest relative difference 1.5e-13, none differing; before, 1,198 ticks
  from 15216 on. The RNG state differs on 1,116 of them in runs of 1-4 ticks (861 single), the same 919 ticks up to 15215 as
  before the fix, and none stays apart. Goal ticks, live times, held frames and purchases of `FalseEnding-Any%`, `FE-C2A`
  and `FE-C2DS` (282, 282 and 280 goals) are unchanged. The turbo edit is the same change in `TurboRunner.Run`; it isn't
  replayed yet (it needs the plugin redeployed). `--ticks-out` now runs to the route's end (its 400 extra ticks had ended it
  at tick 16,413 of the probe's 17,353).
- **Where:** `Simulation.InterpolatedFrames` (`Shop.EndOfFrame`), `TurboRunner.Run` (`EndOfFrame`), `FullTas.Check`.

## 1x counted the dash freeze's first frame as held

- **Symptom:** `fulltas/FalseEnding-Any%_NONE 358.69` played at 1x in 359.74 s live against 358.69 s judged in turbo and
  offline (2026-09-30; the 359.85 s record likewise read 360.90 s), 294 frames more: one per dash freeze.
- **Cause:** 1x counted a held frame for every frame whose Update ran at `Time.timeScale` 0. `Movement.TimeStop` sets it
  in the `FixedUpdate` of the frame that crosses the tick, so that frame's Update runs at 0 though its game time moved
  (it is the tick's own). A 1x probe (`fe358`) shows 15 frames at timeScale 0 per freeze and 14 with the game time
  standing still, turbo's and the engine's `FrozenFrames` (4,380 against 4,088 over the probe).
- **Fix:** 1x counts a frame as held when its game time (`Time.timeAsDouble`) is the frame before's. The same 358.69 s
  route then plays at 1x in 358.69 s live (0 dropped frames), as turbo and offline time it.
- **Where:** `TasPlugin.PacePlayback` (`_pacedTime`).

## Game build 25640326: the spawn reserve

- **Symptom:** Steam updated the game on 2026-09-30 16:37 (build 25640326); `FalseEnding-Any%` 346.74 dies at goal 86
  in turbo (`buy:buywj` not yet affordable).
- **Cause:** one statement: an on-screen spawn sets `timeSinceLastClone` to 0 when it is at least 4000, so a course's
  first spawn spends `Start`'s 10000 and bought clones fill one an interval
  ([`docs/fulltas.md`](fulltas.md#clones-on-build-25640326)). The scenes gained `Moss` tilemaps (no other collider
  changed).
- **Fix:** the engine's spawn does the same (`Economy.SpawnClone`); turbo runs the game's own. The engine matches the
  game's turbo trace (`fe-b640-34674`) in RNG, Cash and position on every tick to the death at 5270. The planners filled
  bought clones and a break's refill one an interval too (`plan_fulltas.py`, deleted 2026-10-04: the economy planner
  plays the engine's own economy, `Economy.SpawnClone`). `FullTasAutoLib.cs`'s `ClonePay`/`IncomeFeature` assume the
  same, but they are the `--auto` library's ranking feature (`LibFeatures.Income`), not a Cash model: nothing's Cash
  comes from them.
- **Where:** `Economy.SpawnClone`, `FullTasAutoLib.cs`.

## Game build 25630570: clones and buy-max

- **Symptom:** Steam updated the game on 2026-09-30 10:34 (build 25630570); routes judged before it pay different Cash
  at 1x (the 353.32 s `FalseEnding-Any%` route, replayed with the new clone code: 8.7 % more Cash at goal 110).
- **Cause:** two statements changed. `clonesScript.SpawnClone` on screen takes the rolled interval off
  `timeSinceLastClone` instead of setting it to 0, so a course keeps `Start`'s 10000 (until it pays off screen) and spawns
  one clone a rendered frame while a slot is free (drawing the RNG every frame); `upgradeBox.IE_BuyMax` waits `WaitForFixedUpdate` instead of
  `WaitForEndOfFrame`, one level a physics step instead of one a rendered frame. The model:
  [`docs/fulltas.md`](fulltas.md#clones-and-buy-max-build-25630570).
- **Fix:** ed1784b: the engine's spawn keeps the overshoot (`Economy.SpawnClone`), and the engine and turbo resume `IE_BuyMax` after every
  physics step, the touch's included. The engine then matches the new-build turbo through tick 14122 of the 353.32 s
  route. A scene diff of the builds (4,036 objects each side) changes no gameplay field, so nothing was captured again.
  `ClonePay` (then in `ZipPlan.cs`, since 2026-10-04 in `FullTasAutoLib.cs` as the `--auto` library's ranking feature, not a
  Cash model) and `plan_fulltas.py` (deleted 2026-10-04) were ported too. Buy-max: a 1x probe of the route (inputs to 15200) buys 2 levels on the touch's tick, then 1 a tick
  (offline Cash equal level by level from tick 15040). Routes judged before the build are judged again.
- **Where:** `Economy.SpawnClone`, `Economy.CloneStates`, `Shop.FixedStep` (`Shop.cs:148`, `Simulation.cs:1313`),
  `TurboRunner.cs:181` and 238-239.

The RNG runs of *Buy-max levels bought once a tick* (~1,100 ticks, runs of 1-6) aren't a divergence: over the 358.69 s
route both sides make the same 6,759 draws in the same order (every per-tick state of both lies on one xorshift sequence,
the probe's tick row always between the engine's rows for the tick before and the tick), and all 1,741 clone spawns
match (tick, course, on screen, divisor, rolls). The probe writes its tick row after `Movement.FixedUpdate`
(`AfterMovementFixedUpdate`), before `Physics2D.Simulate`'s trigger callbacks (a box's buy sound) and the tick's
rendered frames (`Movement.playSFX` landings, clone spawns); `--ticks-out` writes its row after all of them.

## Trigger entered on a touch only at the step's start

- **Symptom:** on build 25630570, `FalseEnding-Any%` 353.32 s bought Course 3's `CashPerLoop` box offline at tick 14123;
  turbo and a 1x probe (`fe353new`) buy it at 14124. Cash and the RNG split there (167 of 16,285 ticks against the probe).
- **Cause:** at tick 14122 the dash's position write moved the player forward from the frame's blend into the box's
  trigger strip, and the step's dash moved it back out; the next step moved it in again. The engine counted Box2D's touch
  at the step's start (a pair not touching after the last step) as touching after the step: Enter at 14122, Stay (the
  buy) at 14123. At 1x such a touch enters and ends in its step, so the next step's overlap enters again: Enter at
  14123, Stay at 14124.
- **Fix:** a trigger pair touched only at the step's start gets Enter and Exit in that step and isn't touching after it
  (`PhysicsWorld.CollectTouching`, `_startOnly`); the start-of-step Enter itself stays (a wall jump's push into a wall
  enters the checkpoint behind it). The 461-trace replay corpus is byte-identical before and after (dropping the
  start-of-step touch altogether left it identical too, so the corpus doesn't hold that case). The 353.32 s capture on
  the new build now matches turbo on every tick (1-17,028: position, Cash, RNG) and the probe's Cash on all 16,285 ticks
  compared (engine row p against probe row p + 1); the 279-goal route judged in the game at 353.32 s.
- **Where:** `PhysicsWorld.CollectTouching`, `DispatchCallbacks`, `AdvanceTouching`.

## 1x played six frames a tick, not the game's 280 cap

- **Symptom:** the overlay read 300 FPS at 1x on the "280 fps" clock, and a route recorded at the game's own 280 fps
  setting (its FPS dropdown, `Application.targetFrameRate`) can't be shown in real time: `FalseEnding-Any%` 5:52.84 took
  about 6:30 with OBS running (2026-09-30).
- **Cause:** the fixed clock started its grid again half a frame after every tick: five 1/280 s frames and a 2.14 ms
  crossing frame, 300 frames a second, each tick's last frame 0.36 ms before the next tick. A player at the 280 cap gets
  280 frames a second, a frame every 1/280 s across the ticks, and the lag cycles 0.36, 2.50, 1.07, 3.21, 1.79 ms over
  five ticks (6, 5, 6, 5, 6 frames). The route's interpolation pullbacks, step-ups, clone frames and freezes were a
  300 fps player's, and rendering them in real time needs 300 frames a second.
- **Fix:** from a run's second physics tick the grid runs on (`MacroEngine.FrameGrid` by `GridTicks`, the run's physics
  ticks while it runs; the restart grid before that, so trace and savestate starts are unchanged), and a run frame is
  paced 1/280 s of wall time. A 280 fps probe (`grid280`) measured the clock: Time.deltaTime 0x3b6a0ea1 (1/280f) after a
  crossing and 0x3b6a0e82 at one (ticks 0.0199999929 s apart), while the clock's own steps, which `IE_TimeStop` adds
  up, are 0x3b6a0ebf and 0x3b6a0e9d. A 0.05 s dash freeze lands on 14 of them and holds 13 frames at 1x (14 before);
  the model took (float)(1/280) for the clock's steps at first and said 14. `IGTAP.EngineSim grid` checks the cycle and
  these steps. Every tech demo plays the same at 1x as in turbo as before the change (57 of 70 on every tick; the rest
  split as they did on the old grid, or only where the probe logs the body before a step-up's sync), turbo matches the
  engine on all 66 that trace, and freezes hold 13 frames in all three. Every route judged before has to be solved again:
  `validate.py` finished all 67 category routes on the old grid and 2 on this one (`bonus2_WJ_DS` in 1.06 s against
  1.04, `bonus2_WJ_DS_DJ_BS_ORB_ZIP_OM` in 0.76 s); the rest die or run out of inputs.
- **Where:** `MacroEngine.FrameGrid`, `TasPlugin.FixedClockStep`, `PacePlayback`, `TurboRunner.Run`,
  `Simulation.InterpolatedFrames`.

## A step-up in a tick's last frame reached the body too early

- **Symptom:** `2_WJ_DS 1.20` (a pauselift, Course 2) was judged 1.20 in turbo and offline and ran 1.32 at 1x
  (2026-10-01): two ticks after the pause, a step-up in the last rendered frame before a tick (y 2781 to 2829) was
  followed at 1x by a physics step that didn't move the player, then a jump off the ledge a tick later.
- **Cause:** at 1x a transform write in `Movement.Update` reaches the body at the next frame's start. After a tick's last
  frame that is the next tick's physics step, so that tick's `Movement.FixedUpdate` still sees the body where it was:
  the squish check's `normalCollider.Distance` (`Movement.FixedUpdate`, 846-870) measured the collider against the ledge
  it had stepped onto, called it squished, and set the body's velocity to the moving platform's (0) instead of
  `Velocity + momentum`; the step's sync then moved the body to the written position, where it stood for that tick.
  Turbo and the engine synced a step-up into the body at once, in every frame. 807 earlier last-frame step-ups in the
  probe logs moved normally (no squish), so only a squish or another body query in that FixedUpdate shows it.
- **Fix:** a write in a tick's last frame (not a freeze's or pause's repeat) stays pending for the next tick's sync before
  `Physics.Simulate`; earlier frames still sync at once. The 1.20's inputs now finish in 1.32 offline and in turbo,
  every body within 0.001 of the 1x probe's (`c2wjds120`); every tech demo still matches turbo.
- **Where:** `Simulation.InterpolatedFrames`, `TurboRunner.Run`.

## A route's last input was played at rest

- **Symptom:** bonus1's solve kept a 6.90 best (345 ticks) that every replay of the saved route finished in 6.92 (2026-10-01).
- **Cause:** `MacroEngine.Advance` called `Finish` on the last step's own tick, and `Finish` puts the stick at rest
  (`SetDirection(0)`) in that tick's input hook, so a route's last input never stood. The solver timed its best with the
  inputs that followed and saved it cut to the finish tick (`Adopt`: `finishTick + 1`), so the saved route ended a tick
  differently. 1x, turbo and the engine share `MacroEngine`, so all three played the cut route alike.
- **Fix:** `Advance` leaves the finish to the next `FixedTick` (which finishes when the steps are done), so the last input
  stands for its whole tick. The cut 6.90 now replays in 6.90; `validate.py`: all 31 saved category routes finish at
  their names' times.
- **Where:** `MacroEngine.Advance`.

## Area goals and end boxes were tested at a pending step-up's pose

- **Symptom:** (2026-10-01, found by reading the plugin's box test against the engine's, then run in turbo) a Full-TAS area
  goal that only a step-up written in a tick's last frame reaches fired a tick earlier offline than in turbo. A test run
  from `lab/c1 wall bottom cling` (Course 1's hanging wall, wall jump and 1 dash) pressing down with a turned dash every 3rd
  tick lifts 56 u in the last frame of ticks 1, 5, 9 and 13; the goal `at:2555,127,60,6` (only tick 9's raised pose reaches
  it) was reached at 0.3629 s in turbo and 0.3429 s offline, `at:2555,179,60,6` (tick 13) at 0.4893 s against 0.4693 s.
  Every tick's state was identical; the turbo trace has the transform 56 u above the body at the end of those ticks. A
  bonus or transition end box (`finish` in the trace header, courses.json `endX..endH`) is tested by the same plugin
  function, so it finished a tick early in the same case; no saved bonus or transition route has one.
- **Cause:** the plugin tests both at the start of the next tick's `Movement.FixedUpdate` (`TasPlugin.TouchesBox`, from
  `BeforeMovementFixedUpdate` and `FullTasAreaGoal`) with `Collider2D.bounds`, the physics pose, which a transform write
  reaches only at the next sync. After a last-frame step-up (the entry above) the transform stands on the ledge and the body
  does not. `Simulation.TouchesReward` and the area goal tested the transform.
- **Fix:** while that step-up is pending (`Physics.PlayerTransformDirty` after the frames, no rebase that tick) the boxes
  are tested around the body, and so is the previous-tick bounds an area goal's `from=` side reads (`BoundsPose`). Other
  ticks are unchanged: the transform is the body's step end there. Both turbo traces now replay `[MATCH]`; the other 262
  turbo traces in the traces folder (bonuses, transitions, courses, techs, Full-TAS runs) replay the same before and after,
  and so do the 158 lab demos.
- **Where:** `Simulation.BoundsPose`, `Simulation.TouchesReward`, `Simulation.PlayerBounds`, `Simulation.Run`.

## Atoms' 0.4 s grid

- **Symptom:** (2026-10-03, by reading the code; no route reaches Area 2) the engine models the atoms (`Economy`'s atoms
  region) but has nothing to start their grid. An atom would never land offline, while at 1x one lands every 0.4 s.
- **Cause:** at 1x the grid is `AtomColliderSystem.OnEnable`'s `InvokeRepeating("callAddNewAtom", 0, 0.4)`
  (AtomColliderSystem.cs:111-115). The collider sits under `zone 2/area2`, so it is enabled when
  `ZoneLoader._LoadZone(2)` turns zone 2 on. That happens two rendered frames after a zone 2 activator's
  `zoneChanger.OnTriggerEnter2D` (`Zone 1-2 transition/zone 2 activator` at (14436, 3552) in a new game's frame, and
  `Zone 2-1 transition/zone 2 activator 2`). The engine runs neither:
  - `cuts.txt` empties `zoneChanger.OnTriggerEnter2D`. A run loads no zone, so zone 2's geometry stays as the start had it.
  - The shim's `SetActive` runs no `OnEnable`.
  - A trace's start carries only the player's pending invokes.

  So a start with zone 2 loaded has the grid running at a phase the engine doesn't know. Unity is said not to cancel
  invokes when their object goes inactive; the game's own `CancelInvoke` before the `InvokeRepeating` suggests the same.
  If so, the grid keeps landing atoms (not crafting: that is `Update`) after zone 1 loads again. This is not verified at 1x.
- **Fix:** the plugin keeps the game time of the grid's start (an `OnEnable` postfix) and of its last call (a
  `callAddNewAtom` prefix), and a trace start with the invoke pending carries the seconds to its next call
  (`economy.atomGridDueIn`). The engine queues the invoke there, and `hooks.txt` routes it to `Economy.AtomTick`.
  A run that drops into zone 2 still never starts the grid, because the engine loads no zone mid-run. A start captured
  before this field has no grid either. In both cases a waiting atom ends the run (`Economy.AtomsLand`). Whether the
  grid keeps firing after zone 1 reloads is left to Task 7's probe. Crafting (`Update`) is modeled per rendered frame
  while the collider is on.
- **Where:** `TurboRunner.AtomGridDueIn`, `EngineSimBridge` (trace start), `Economy` (constructor, `AtomsLand`, `AtomTick`,
  `AtomFrame`), `hooks.txt`.

## Upgrade trees grown by hand

- **Symptom:** (2026-10-03, by reading the code; no route reaches Area 2) a tree box's buy only drew the growth sound and
  noted the segments (`Shop.GrowTree`), and the tree-planting, zip mover and refill orb boxes ended the run. At 1x a
  segment turns on, its ground goes off for its trunks' growth (462 rendered frames) and comes back, and its box falls
  2000 units into place over 557 more; the box can't be hit before. The zip movers and refill orbs read the static
  `globalUpgradeDict`, which the engine's buys didn't write.
- **Cause:** the engine ran other scripts' coroutines to their first yield only, resumed `WaitForEndOfFrame` once a
  tick, ran no `OnEnable` for a stepped script's `SetActive`, didn't move a body-less collider whose transform a script
  wrote, and kept no such transform in snapshots.
- **Fix:** `GrowTree` calls the game's `growSegments`; `IE_Grow` runs on (`Simulation`'s `Continues`). End-of-frame
  waits resume after each rendered frame on its `Time.deltaTime` (a held frame's 0: the shim's `Time.resumeDeltaTime` is
  null outside a resume now, so a 0 is a 0). Every `SetActive(true)` makes its woken objects' `OnEnable` draws (the
  shim's `Activated` hook, `Economy.Activate`). A body-less collider a script moves is rebuilt from its geometry at the
  next sync (`PhysicsWorld.SyncMoved`, pending moves in the physics snapshot), and the transforms a script moves are in
  snapshots (`SimSnapshot.Transforms`, `SnapshotStore` format 5). The zip mover and refill orb buys write through to the
  static dictionary (`Economy.WriteStatic`). A coroutine on an object off in the hierarchy doesn't start, as in Unity.
  A segment's ground is off at the export: its static `Rigidbody2D` is exported (the engine skipped it), and its
  composite's `paths`, but no `shapes`. The engine now builds static bodies that are off at the export, links an off
  source to the composite on its body's object, and builds the composite from its paths (`PhysicsWorld.PathShapes`).
  FalseEnding%-280 and TouchGrass-ANY% replay unchanged; `determinism` on FalseEnding%-280 is DETERMINISTIC.
- **Residual risk (for Task 7's 1x probe):**
  - The ground is a closed chain of two-sided edges per path, not Unity's convex polygons (a Polygons composite; Unity's
    decomposition isn't in the export and isn't matched). The top and side surfaces are the same lines. What differs:
    contacts at Unity's internal seams (ghost contacts on its polygon edges, none on the chain), polygon-vs-edge
    manifolds against a polygon-vs-polygon one at a corner, and the inside (a point or a body inside the outline is
    inside Unity's polygons, not the chain's).
  - `Start` draws (LightFlicker 1, RandomAnimSpeedComponent 2 or 3) of what a segment wakes the first time. Start runs
    before a behaviour's first Update: a segment woken in the buy's physics step draws in that same rendered frame,
    before its Movement.Update and clone draws; one woken at a frame's end (the box, an auto-triggered segment) in the
    next frame. Not made offline (turbo never reaches them).
- **Where:** `Shop.GrowTree`, `Simulation` (`InterpolatedFrames`, `Continues`, `RestoreTransforms`), `Scheduler`,
  `PhysicsWorld` (`TransformChanged`, `SyncMoved`, `Unbodied`), `Economy` (`WriteStatic`, `Activate`, `BoughtGlobal`),
  `Shim/Engine` (`Time`, `Transform`, `GameObject`, `Renderer.bounds`).

## Turbo ran no atoms, NP or tree growth

- **Symptom:** (2026-10-03, by reading the code; no route reaches Area 2, and this is not yet checked in game) turbo
  would play Area 2's economy differently from 1x and the engine: no atom would land and none would craft, no NP would be
  paid, and a tree segment would not grow within a run.
- **Cause:** a turbo run is one Unity frame, and it steps only what it calls itself.
  - `AtomColliderSystem`'s `InvokeRepeating("callAddNewAtom", 0, 0.4)` runs on `Time.time`, which stands still through
    the run.
  - Its `Update` (one tier's crafting a frame) wasn't stepped per modelled frame, as `clonesScript` is.
  - `globalStats.FixedUpdate` (the NP payout) wasn't called at all.
  - `TreeSegmentActivationComponent.IE_Grow` ran on Unity's loop, so it resumed only after the run. Turbo's
    `WaitForEndOfFrame` resumed once a tick, not once a frame.
  - The snapshot kept none of the collider's state, `globalStats`' atom counts and NP rates, or the trees.
- **Fix:** turbo now does the following, matching the engine.
  - **Atoms.** Turbo runs `AtomColliderSystem.Update` in each modelled frame, with `HandleTierLogic`'s `Time.deltaTime`
    set to the frame's (0 in a held frame). The sprites' `HandleMovement` is skipped as presentation.
  - **The grid.** Turbo keeps the grid in run ticks: it fires at the due tick's start, then every 20 ticks. It takes the
    grid from a restore or the last run, or else from Unity's (`floor(dueIn / fixedDeltaTime)`, as the engine reads a
    trace start). An `OnEnable` inside the run is intercepted (`InvokeRepeating`, `CancelInvoke`). At the run's end the
    grid goes back to Unity.
  - **NP.** Turbo pays NP at each tick's start, before Movement's FixedUpdate.
  - **Trees.** Turbo parks `IE_Grow` and resumes its end-of-frame waits after each modelled frame. Its first step runs
    on the fixed step.
  - **Snapshots.** The snapshot now holds the collider's fields, with its lists of lists copied. A sprite instantiated
    after the snapshot goes back to the pool on a restore.

    It also holds `globalStats`' six atom and NP fields, the grid, the trees' fields, the trunk and box local poses and
    the ground colliders. A restore stops any growth Unity is running.
  - **Trace start.** A 1x capture's trace start now carries the raw game times behind `atomGridDueIn`:
    `atomEnabledAt`, `atomLastCall` and `atomTimeNow`.

  FalseEnding%-280 and TouchGrass-ANY% replay unchanged offline (no engine file changed). The turbo side waits for
  Task 7's turbo-vs-engine-vs-1x check.
- **Where:** `TurboRunner` (`Run`, `BeforeInvokeRepeating`, `BeforeCancelInvoke`, `BeforeStartCoroutine`, `Resume`,
  `HandAtomGrid`, `AtomGridTicks`, `CaptureEconomy`, `RestoreAtoms`, `CaptureScene`, `RestoreScene`), `EngineSimBridge`
  (trace start).

## Clone dust multiplier at a start

- **Symptom:** (2026-10-03, Task 7's Area 2 probe: a new game with 1e4 clone dust set, Course 6's start) the engine
  started `CloneDustGpCdMult` at 12.08, while 1x and turbo had 1. Every green power and clone dust payout before the
  first clone dust payout would have paid 12 times as much offline.
- **Cause:** `Economy` computed it from the held clone dust (`RecalcCloneDustMult`). The game recomputes it only after a
  clone dust payout and in `RubbleDisplayScript`'s readout (its `Start` when clone dust is held, then every 0.2 s while
  on screen, RubbleDisplayScript.cs:28-55). So clone dust set or spent since (a box bought with it) leaves the stored
  value stale. A turbo restore also left it, and `rpCap`, as the last run had them.
- **Fix:** the engine starts from the world's `globalStats.CloneDustGpCdMult`. Turbo's snapshot keeps it and `rpCap`
  with `globalStats`' atom and NP fields. The probe's `cdMult` is now equal in all three legs (1). FalseEnding%-280 and
  TouchGrass-ANY% replay unchanged (their worlds hold 1, which 0 clone dust recomputes to).
- **Residual risk:** the engine doesn't run `RubbleDisplayScript` (Course 8's rubble display). While it is on screen at
  1x it recomputes the multiplier every 0.2 s.
- **Where:** `Economy` (constructor), `TurboRunner.AtomStats`.

## Area 2 economy checked against 1x

- **Symptom:** (2026-10-03) Area 2's economy (atoms, NP, the trees, colour clones, clone dust) had been ported to the
  engine and turbo by reading the code. No saved route reaches Area 2, so none of it had been compared with 1x.
- **Check:** a stretch built for it (Task 7).
  - Start: a new game, currencies set by `/api/session/setup` (1e6 Cash and NP, 1e9 GP, 1e4 RP and clone dust), then
    Course 6's WJ_DS_DJ_BS start.
  - Inputs (1129): walk to The Tree and buy its PlantTreeBox from below (0.1 NP). Climb onto the box with two wall
    jumps and wait there for TreePart0. Jump onto its ground (`Tree1Ground`: Moss, a closed chain offline) and buy its
    GreenCloneChanceBox three times from below (1, 5 and 17 NP; it grows TreeParts 1 and 2). Then wait.
  - Legs: a 1x probe from one fresh new game, a turbo trace from another, and `replay --ticks-out` on the trace,
    compared with `tools/economy-diff.py`.
- **Result:** every `econ` field takes the same values in all three legs (`economy-diff.py`, each field as the sequence of
  values it takes). Turbo and the engine also change each on the same physics tick (`economy-diff.py --ticks`, checked
  again 2026-10-04). The 1x probe shows every change one physics count later (417, 704, 749 and 794 against 416, 703,
  748 and 793), the same offset on each: its rows predate `econPre`, which `economy-diff.py` files under the tick before.
  - NP is spent at ticks 416, 703, 748 and 793. Segments go 0, 1, 3 at 416 and 703.
  - The player stands on the box and on the grown ground at the same height in all three (y 67.515 and 244.015).
  - `determinism` on the stretch is DETERMINISTIC. Two of its ten snapshots can't be stored (a collider's shapes can't be
    rebuilt), so they replay instead.
  - The atom grid fires on the same ticks in turbo and the engine from the trace start (16, 36, ...). At 1x, Unity calls
    `callAddNewAtom` in the first rendered frame after a physics tick, every 20 ticks.
- **Differences found:**
  - The clone dust multiplier at a start: fixed (above, *Clone dust multiplier at a start*).
  - **The `Start` draws of what a segment wakes** (since fixed: *Start draws of what a run wakes*, below). At 1x a
    segment woken by a buy runs `RandomAnimSpeedComponent.Start`
    on each trunk light in the buy's own frame (2 draws each, 3 when broken: TreePart0 has 3 lights, TreeParts 1 and 2
    have 3 between them). The box's `LightFlicker.Start` (1 draw) runs the frame after `IE_Grow` turns the box on. Turbo makes these draws after
    its run, in Unity's next Start pass. The engine makes none. So after a growth the RNG is off 1x's in both, and every
    later roll (clone speed and size, colour clones) differs. Not ported, because turbo can only match by putting the RNG
    back around the real `Start` (as exception 11 does for presentation draws), which needs a ruling.
  - **Turbo's position from a category start** is off the engine's and 1x's from the first tick: 0.0005 u on Courses 4,
    6 and 7, 1e-5 u on Course 1. On Course 6 it grew to 0.15 u by tick 400. Standing positions in the engine match 1x
    (22602.555, 22692.197, 22956.951); turbo's don't (22602.701, 22692.201, 22956.941). It is the same at 0804c9b, and
    record routes still finish at their times. Cause not found.
- **Not checked yet:**
  - Atoms landing and crafting, NP income, the atom level box and the comp boost: every atom box buy writes PlayerPrefs
    `OpenA2Door` on the machine (upgradeBox.cs:648). The engine also ends the run at a first buy, because it can't read
    that key. (Since ruled and checked, except the comp boost: *Atom grid a tick late, and the atom start*, below.)
  - Clones, green power and clone dust on an Area 2 course: a new game's Area 2 courses have no clone path, and a
    category run ends at its course's finish.
  - Whether the grid keeps firing after zone 1 loads again.
- **Where:** `tools/economy-diff.py`, `TasPlugin.EconomyNow` (probe rows, trace ticks, `atom` rows), `FullTas.EconomyRow`,
  `replay --ticks-out`.

## Start draws of what a run wakes

- **Symptom:** (2026-10-03, Task 7's tree probe) after a tree box's buy, turbo and the engine had drawn the RNG fewer
  times than 1x: at 1x, The Tree's first segment took 14 draws over its buy's tick, its box 4 the tick it came on, the next
  two segments 17 and their boxes 8. Turbo and the engine took 6, 1, 6 and 2 fewer (footsteps aside, which a category run
  draws at 1x only).
- **Cause:** Unity runs a woken behaviour's `Start` before its first `Update`: `RandomAnimSpeedComponent.Start` on a
  segment's lights in the buy's own frame, `LightFlicker.Start` on its box the frame after `IE_Grow` turns it on. A turbo
  run is one Unity frame, so Unity ran those `Start`s after the run. The engine ran none.
- **Fix:** README ### Rules, exception 12. At each modelled frame's start, turbo (`TurboRunner.StartPass`) and the engine
  (`Economy.StartPass`, woken through `Simulation`'s Activated hook) make the draws of each `LightFlicker`,
  `RandomAnimSpeedComponent` and `JiggleDropScript` the run woke whose `Start` hasn't run (off at the export, or at the
  run's start). Turbo then puts `UnityEngine.Random`'s state back around Unity's real `Start` (a Harmony prefix and
  postfix). Snapshots hold the woken set (engine `Economy.State`, `SnapshotStore` format 6; turbo `Snapshot.startsModelled`).
  The draws per event now match 1x (14, 4, 17, 8), turbo and the engine draw identically on every tick, and
  FalseEnding%-280 and TouchGrass-ANY% replay unchanged (no run of theirs wakes one).
- **Residual risk:** turbo takes "not started" from Unity's real `Start` calls it has seen and the run's start. An object
  started once, switched off before the export and woken again counts as never started in the engine (no draw at 1x).
- **Where:** `TurboRunner` (`StartPass`, `BeforeDrawingStart`, `AfterDrawingStart`), `Economy` (`Woken`, `StartPass`),
  `Simulation` (Activated hook, `InterpolatedFrames`), README ### Rules exception 12.

## Atom grid a tick late, and the atom start

- **Symptom:** (2026-10-03, Task 7's atom probe: Course 6's start in a new game, 1e9 GP and no NP, the atom box bought 88
  times from below) the atoms landed and crafted one tick later in turbo and the engine than at 1x. So the first merge's
  NP x1000 came a tick late and NP stayed 0.114 lower from then on. Also, after the first merge `effectiveAtomCount`
  was 1.7e-9 off turbo's in the engine. And from a start taken with atoms waiting (Course 10 after the atom stretch), the
  engine started with one more atom landed and one fewer waiting than the start had.
- **Cause:**
  - 1x calls `callAddNewAtom` in the first rendered frame after a physics tick, after that frame's Updates (the probe's
    atom rows: the tick's `fixedTime` + 0.0025 s). Turbo and the engine fired it at the next tick's start.
  - The game's Mono JIT keeps `Mathf.Pow(...) * 0.001f` in double precision on its way into the double field; the
    engine multiplied in float.
  - The engine read the collider's state and `globalStats`' atom and NP fields from the world export. That export is
    written after the start's capture run, some ticks later.
- **Fix:**
  - The grid fires in the first rendered frame of the tick before the one it used to, after the frame's clone and atom
    steps: `Economy.GridFrame` and turbo's frame loop.
  - The atom terms are double products.
  - The trace start carries the collider (`economy.atomCollider`) and the `globalStats` fields (`economy.atomStats`).
  - The atom box's door branch is modelled. The trace start says whether PlayerPrefs hold `OpenA2Door`
    (`economy.openA2Door`), and the engine runs the door's `ForceNextPhase` instead of ending the run. Only
    `AtomUpgrades/AtomUpgrade` has a door (its bottom trigger at world (21292, -266)), and a Course 6-15 start has
    already opened it (README ### Rules 21), so on a machine without the key that buy shuts it: the game's first-buy
    branch (upgradeBox.cs:644-646) applied to the stand-in's state, which a real game never has (a load opens the door
    only with the key or the box bought, upgradeBox.cs:264).
    `t7-atoms-nokey` (turbo, the key not yet on this machine) and the engine agree on every tick. That turbo run wrote
    the key; it has been on this machine since.
- **Check:**
  - The atom stretch: 1x, turbo and the engine equal on every economy value and on the same ticks. That covers 40
    landings, crafting up to tier 2, the first merge's NP x1000 and NP income.
  - Course 10 from the atom stretch's end (the NP box, then the atom level box's `ClearAtoms` with 8 atoms and 28
    waiting): turbo and the engine equal on every tick. It has no 1x leg, because 1x keeps landing atoms and paying NP
    between two runs, so no 1x run starts where the trace does.
  - `determinism` on both stretches is DETERMINISTIC. FalseEnding%-280 and TouchGrass-ANY% replay unchanged. The
    FalseEnding%-280 turbo judge and every non-farm category route judge as before.
- **Residual risk:** a grid whose phase falls in a later frame of a tick (a zone 2 load inside a frame grid) isn't
  modelled. Whether the grid keeps firing after zone 1 loads again is unchecked.
- **Where:** `Economy` (`GridFrame`, `AtomPrestige`, constructor), `Simulation.InterpolatedFrames`, `TurboRunner` (`Run`,
  `AtomGridTicks`, `HandAtomGrid`, `Economy.openA2Door`), `EngineSimBridge.WriteAtomStart`, `entries.txt`.

## The economy drive touched boxes in mid-air

- **Symptom:** `fulltas-plan`'s drive bought a tree box the tick `TreeSegmentActivationComponent.IE_Grow` turned it on,
  while it was still 2000 u above its spot and dropping (about 2 s, 99 ticks, to within 5 u); every tree box on the way
  to the zip movers came up to 2 s early in a plan.
- **Cause:** `Simulation.EconomyBuy` called `upgradeBox.OnTriggerStay2D` whatever the box's place; a real touch needs its
  trigger to overlap the player.
- **Fix:** the drive touches a box only where one of its own triggers, synced to where its transform is now
  (`PhysicsWorld.SyncTransforms`, as a physics step syncs them first), overlaps the player's boxes at the step's place
  (`PhysicsWorld.PlayerOverlaps`, the clonesLoD triggers' test); a box place the table makes is where the player touches
  it, 4 u into its trigger from below (`TouchSpot`). `plan-check --tree-drop`: TreePart0's box comes on 98 ticks after
  the plant box's buy, is within 5 u of its spot on tick 197 and is bought on 197 (before: on tick 108, 1026 u up).
- **Where:** `Simulation.EconomyBuy`, `Reaches`, `TouchSpot` (`Runtime/EconomyDrive.cs`).

## The Area 2 door box was refused

- **Symptom:** the engine refused `buy:buytree7` (TreePart7, `area2DoorOpen`), on every way to the zip movers.
- **Fix:** `Economy.BoughtGlobal` runs DoBuyUpgrade's own `doorToOpen.ForceNextPhase` (upgradeBox.cs:755-757), the door
  touched so a restore puts it back, as the atom box's door. Checked at 1x (`t13-door-q`: a Course 6 start on a fresh
  new game with Task 7's currencies, the tree climbed to `buytree7`): the door (`econ.doorArea2`, its distance from the
  box, in the probe, trace and `--ticks-out` rows) starts moving on the same physics tick in 1x, turbo and offline and
  passes the same 505 positions, 1x within 0.03 u of offline, turbo within 0.01 (rounding); Cash, GP, CD, RP, atoms and
  grown segments equal in all three. NP, NP per tick and waiting atoms differ at 1x from the first atom landings: the 1x
  run's atom grid phase is its own (a category start doesn't pin it), turbo and offline equal.
- **Where:** `Economy.BoughtGlobal`; `FullTas.EconomyRow`, `TasPlugin.EconomyNow` (`doorAtom`, `doorArea2`).

## The planner's root counted the schedule a tick early

- **Symptom:** a re-plan from a realize state found no plan: every step ended "a waiting atom with AtomColliderSystem's
  0.4 s grid not running".
- **Cause:** `Simulation.Capture` counts what is due from the tick after `Scheduler.Tick`, the drive's last tick; the
  planner captured its root right after a restore (Tick 0, nothing played), so every pending Invoke, routine and the
  atom grid was filed a tick early, and a grid due on the first tick was filed as none.
- **Fix:** `SimEconomy.Capture` hands back the snapshot it last restored while no drive tick has played since.
- **Where:** `SimEconomy` (`enginesim/Cli/EconomyPlan.cs`).

## Zone loads mid-run

- **Symptom:** a Full-TAS run couldn't go past touch grass (the zone 1 → 2 activator): 1x loads zone 2 two frames after
  `zoneChanger.OnTriggerEnter2D`, turbo only after its run returned (Unity ran `IE_Load` after the run's one frame), and
  the engine never (`cuts.txt` dropped the trigger). A run that bought an atom without the grid running ended.
- **Fix:** turbo parks `ZoneLoader.IE_Load` and resumes a `yield return null` once per modelled frame, in a later frame
  than it yielded in (`_frame`); the engine runs it on its Scheduler (`Scheduler.Frame`, `Continues`), with the woken
  zip movers' `OnEnable` and the atom collider's grid (`Economy.GridStart`: called in the load's frame, then at k = 0 of
  every 20th tick, turbo's `_atomNow`); both drop a parked coroutine whose object went off. The trace start carries
  `activeZone` and the never-started behaviours (`neverStarted`), and refuses a start with a load under way; a woken box
  whose Start would change what a run reads ends the run (`WokenStarts`). Turbo's restore restores the zip movers after
  the scene, keeps `activeZone` and stops Unity's own `IE_Load`; its orbs include the ones off at the run's start.
- **Checked:** a 1x probe, turbo trace and engine replay of the activator crossing from the same new-game savestate start
  (`Test-Zone12`, `Test-Zone12-Rebase`): the load at tick 70 k = 2, the grid at k = 2 then k = 0 of tick 90, ..., and
  positions, RNG and the economy equal on every tick. Three differences are older than this and not the load's: a Full-TAS
  start's first tick reads the stick at rest at 1x (the playback starts inside that FixedUpdate) but the next input's in
  turbo, so an air start whose first input holds the stick down falls faster in turbo (the check's first input is neutral);
  each new game has its own Unity grid phase until a load restarts it (it lands nothing without a waiting atom, and none
  waits before zone 2 first loads); and the unloaded Area 2 door's distance (`econ.doorArea2`) reads 0.01 u apart before
  the load.
- **Where:** `TurboRunner` (`BeforeStartCoroutine`, `Resume`, `StartPass`, `Restore`), `EngineSimExport.WriteSnapshot`,
  `Scheduler`, `Simulation` (`Activated`, `InterpolatedFrames`), `Economy` (`GridStart`, `GridNow`, `StartPass`),
  `WokenStarts.cs`.

## Woken upgrade boxes' Start

- **Symptom:** a Full-TAS run that plants the Area 2 tree ended at `buytree0` in turbo and the engine ("Unmodeled:
  GreenCloneChanceBox's Start, the first since a run woke it, would change its cap", `TouchGrass-Zip`, tick 34226), and
  every way to the zip movers goes through it.
- **Cause:** Unity runs a woken `upgradeBox`'s `Start` (upgradeBox.cs:199-298: `baseCap = Cap`, the cap times
  `baseCapMult`, the base cost, `deactivate`, visibility, ...) before its first Update, but a turbo run is one Unity
  frame, so Unity's real `Start` came after the run, and the engine has no Unity: both ended the run instead
  (`WokenStarts.Unmodeled`). The probe also showed what both legs' Start pass had wrong: Unity runs the `Start` of what a
  frame's end woke first thing in the next frame, before that frame's `FixedUpdate` when it has one, and both ran it after
  the tick's `FixedUpdate`s and physics step.
- **Fix:** README ### Rules, exception 12. Turbo's `StartPass` runs the game's own `Start` of a woken box
  (`WokenStarts.Start`) and puts the box's fields and the RNG back around Unity's later real `Start`
  (`BeforeDrawingStart`, `AfterDrawingStart`); the engine's `Economy.StartPass` runs it with the box and its tree touched
  and `upgradeBox.updateCostTextDisplay` and `setBoxText` cut (`cuts.txt`; a `PlayerPrefs` stub that throws,
  `Shim/Engine/Misc.cs`). Both make a Start pass at each tick's start, before its Invokes and `FixedUpdate`s, as well as at
  each frame's. `WokenStarts.Unmodeled` now refuses only a `Start` that would move a door. Probe rows, trace ticks and
  `--ticks-out` lines carry the tree boxes on (`econ.treeBoxes`), and `replay` compares a string field by its value.
- **Checked:** (1) a `WJ_DS_DJ_BS` Course 6 start on a fresh new game with 1e6 Cash and NP, 1e9 GP, 1e4 CD and RP, the
  1861 inputs of `t13-door-q` (the plant box, `buytree0` to `buytree7`, eleven box Starts; probe `tree-start2`, trace
  `tree-start-turbo`): turbo and the engine equal on all 2029 ticks (RNG, Cash, the economy, `treeBoxes`; positions as
  before); the probe's `treeBoxes` equal to both on every tick, three model rows aside (taken after a tick's last frame, they
  show a box woken there and not yet started; 1x's row is taken in the next tick's `globalStats.FixedUpdate`, after its
  Start). Each box starts in the same frame in all three: `GreenCloneChanceBox` in tick 513's frame k = 1 (woken at k = 0's
  end), `GlobalNPincreaseBox` on in tick 961's frame k = 5 and started before tick 962's `FixedUpdate` (the probe's
  `boxStart` row before tick 962's, the engine's `EconomyTickStart`). The probe's NP and RNG are its own, as a category
  start's always were. (2) `Test-Tree`, a Full-TAS run from Test-Zone12's savestate with the wall jump, dash and double
  jump (`lab/zone12-shaft-top-wjdsdj`, loose buys, waypoints from TouchGrass-Zip's way to Course 6): the fall through the
  zone 2 activator, the atom box (tick 1735), the plant (1753), `buytree0` (2184). 1x (probe `tree-ft`) against the engine
  (`fulltas --check --ticks-out`): every economy field equal on all 3100 ticks (NP, atoms, the grid, `treeBoxes`;
  `doorAtom` 0.01 apart in 71, rounding), positions within 0.01 but at 11 single landing ticks, the RNG on every tick but
  6 whose draws fall inside the tick (the zone load, the buys, a box's light), the box Starts in tick 1850's and 2281's
  frame k = 1 in both. Turbo isn't in (2): its trace drifts from 1x after the landing (tick 314 on, 0.002 u every few
  ticks: its x steps 9.19922 u where 1x's and the engine's alternate 9.1992 and 9.2012) and misses the atom box. Nothing
  runs a `Start` there before tick 1850, so the drift is older than this; open.
- **Where:** `WokenStarts.cs`, `TurboRunner` (`StartPass`, its tick loop, `BeforeDrawingStart`, `AfterDrawingStart`),
  `Economy.StartPass`, `EconomyTickStart`, `cuts.txt`, `Shim/Engine/Misc.cs`, `ProbeRunner.EconomyNow`,
  `FullTas.EconomyRow`, `Program.Compare`, `tools/economy-diff.py`.

## 2026-10-07: a tree box falling onto its spot, seen a frame late (Zip% goal 513)

Zip% played at 1x from tick 36150 restarted onto bonus 11's checkpoint at tick 36424 where the engine landed at the Course 6
side's (24931, -488). Found by a 1x probe against `fulltas --check --ticks-out` (both now log `wall`, `respawn`; the probe
logs the upgrade boxes within 300 u, the engine `--watch [PARENT/]NAME`): on tick 36328 the 1x `Movement.FixedUpdate` found
a wall (momentum 297 to 0) and the engine didn't, which left the player 11 u short of the checkpoint's trigger 95 ticks later.
The wall was `TreePart0/GreenCloneChanceBox`, still falling onto its spot after the plant (IE_Grow's box drop, a transform
write at each frame's end): Unity's Physics2DEarlyUpdate syncs those writes at every rendered frame's start, so the
FixedUpdate's wall rays saw the box where the last frame left it (bottom 489.2, under the ray at 491.3); the engine and turbo
synced them only at the step (bottom 495). Both now sync script-moved colliders at each frame's start
(`PhysicsWorld.FrameStart`, `TurboRunner.FrameStart`, the player's transform put on its body first in turbo). The engine then
matches the 1x probe body for body through tick 36643 and turbo-then-1x from 36440 for 202 ticks; Tree%, TouchGrass-ANY% and
FalseEnding%-280 replay unchanged.

## 2026-10-09: refill orbs bought on never came on (Zip% bonus17's pit)

Zip% bought the refill orbs' unlock (`buyorbs7`, bonus17's end, in a pit) and every search out of the pit stalled at the same
distance, with refresh orb levels 1 and 2 alike. A refill orb reads the upgrades only in `SetActiveVisualState`, which the game
calls on a use, a cooldown's end and `OnBecameVisible`: at 1x an orb turns usable as it comes on camera, but turbo and the engine
render no frame, so an orb never used kept the export's `usable` (off in a new game) for good. Both now call it when an orb
comes on camera after the physics step (`MacroEngine.OrbOnCamera`: the follow camera on the player, 550 high, 16:9 wide;
`Simulation.SeeOrbs`, `TurboRunner.SeeOrbs`), the orbs on camera in their snapshots (`SimSnapshot.OrbSeen`, snapshot format 8;
`Snapshot.orbsSeen`). Category starts set the orbs with the unlocks (`TasPlugin.RefreshRefillOrbs`), so only a Full-TAS buy of
`unlockJiggleDrops` or `moreRefreshOrbs` saw it. Zip%'s best replays unchanged through goal 1713.

## 2026-10-09: turbo to 1x mid-movement (Play stretch best, the TAS editor's seek)

**Play stretch best** for ZipEscape%'s stretch to goal 1157 (`at:28710,5876,240,400`) turbos the run to 150 ticks before the
stretch and plays on at 1x; it died before the stretch, then stopped short of it. A 1x probe from tick 73000 (mid-air, vx -460)
against `fulltas --check --ticks-out` found three causes, each fixed:

- A real `IE_TimeStop` turbo started froze 1x again after it (84 frames at timeScale 0): the fast-forward drops them as the
  editor's seek does (`TurboRunner.DropTimeStops`), turbos to one tick short and plays the last at 1x.
- The handoff placed the player's transform at the last frame's blend at once (`TurboRunner.ShowLastFrame`), and Unity synced
  that script write into the body at the next frame: the body lost the blend's lag (1.25 u at vx -460) and kept it. Now the
  blend is placed only around 1x's next `Movement.FixedUpdate` and taken back when it didn't write, as turbo's own ticks do
  (`BeforeHandoffTick`, `AfterHandoffTick`).
- Unity's real `Start` of the Area 2 door box, which turbo had modelled while the box was still for sale, ran after the run
  with the box bought out and jumped its door (`PlatformMover.JumpToNextState`, upgradeBox.cs:260-263) back to its first stop
  mid-opening, where it stood still for the player to land on at tick 73026. The put-back around that `Start` now restores
  the door's stop too (`TurboRunner.AfterDrawingStart`).

From 73000 the probe then matches a 1x run from tick 71000 (at rest) for 506 ticks within 0.006 u, velocities exact, and the
stretch play reaches its goal in the engine's 795 ticks. `tools/tas-check.py` still differs from 1x by an ulp or so: turbo's
own trace differs from 1x's that much from the route's 4th input on, before any handoff.

## 2026-10-09: turbo's own transform writes rounded the body

`tools/tas-check.py` found turbo and 1x a float step apart from a route's 4th input on (Course 5's record), and a 1x run from tick
71000 drifted from the engine by about 0.001 u a tick in ZipEscape%. The player's parent (`PlayerObject`) is moved by every
rebase and category start, so its transform's local position is large (x 24594 against a world x of -1382: float steps of 0.002
against 0.0001). Turbo put the transform on the body at every frame's start and before every step (`Place`, then
`Physics2D.SyncTransforms`), and the sync took the parent round trip's rounding into the body; the engine copied it after an
in-run rebase (*Rebase body snap*). A 1x probe logging the raw body, local position and parent showed the 1x body on the
parent's grid on 20 of 507 ticks (chance), so 1x never does: Unity doesn't sync its interpolation's writes back. Turbo now puts
the exact body back after those syncs (`TurboRunner.KeepBody`; real writes, a jump's or a step-up's, still sync as at 1x) and
the engine no longer snaps. ZipEscape%'s 1158-goal offline best, judged with the rounding, stops at goal 970 (`buy:buytree2`)
in the engine now and in a 1x play from its new game, the same goal and box. A seek at Course 5's input 40 now holds exactly
what 1x plays there. What's left: on about 7% of frames Unity's interpolation blend rounds to the neighbouring local step from
turbo's `Lerp` (no alpha or lerp form tried matches every frame; best 92.6%), which reaches the body only on a tick whose
FixedUpdate writes the position.
