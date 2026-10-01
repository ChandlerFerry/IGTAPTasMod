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
- **Where:** `TurboRunner.Run`, `Simulation.InterpolatedFrames`, `Shop.StepClones`, `ZipPlan`.

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
- **Fix:** the engine's spawn does the same (`Economy.cs:318`); turbo runs the game's own. The engine matches the game's
  turbo trace (`fe-b640-34674`) in RNG, Cash and position on every tick to the death at 5270. `plan_fulltas.py` fills
  bought clones and a break's refill one an interval.
- **Where:** `Economy.cs:318`, `tools/plan_fulltas.py`.

## Game build 25630570: clones and buy-max

- **Symptom:** Steam updated the game on 2026-09-30 10:34 (build 25630570); routes judged before it pay different Cash
  at 1x (the 353.32 s `FalseEnding-Any%` route, replayed with the new clone code: 8.7 % more Cash at goal 110).
- **Cause:** two statements changed. `clonesScript.SpawnClone` on screen takes the rolled interval off
  `timeSinceLastClone` instead of setting it to 0, so a course keeps `Start`'s 10000 (until it pays off screen) and spawns
  one clone a rendered frame while a slot is free (drawing the RNG every frame); `upgradeBox.IE_BuyMax` waits `WaitForFixedUpdate` instead of
  `WaitForEndOfFrame`, one level a physics step instead of one a rendered frame. The model:
  [`docs/fulltas.md`](fulltas.md#clones-and-buy-max-build-25630570).
- **Fix:** ed1784b: the engine's spawn keeps the overshoot, and the engine and turbo resume `IE_BuyMax` after every
  physics step, the touch's included. The engine then matches the new-build turbo through tick 14122 of the 353.32 s
  route. A scene diff of the builds (4,036 objects each side) changes no gameplay field, so nothing was captured again.
  `ZipPlan.ClonePay` and `plan_fulltas.py` are ported too. Buy-max: a 1x probe of the route (inputs to 15200) buys 2 levels on the touch's tick, then 1 a tick
  (offline Cash equal level by level from tick 15040). Routes judged before the build are judged again.
- **Where:** `Economy.cs:308`, `Economy.CloneStates`, `Shop.FixedStep` (`Shop.cs:148`, `Simulation.cs:1313`),
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
