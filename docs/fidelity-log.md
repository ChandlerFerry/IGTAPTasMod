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

## Clone order changed between sessions

- **Symptom:** the same route's clone rolls swapped at physics tick 16409 and one course's spawns ran 2 ticks late
  from then on (Course 3 before Course 1 in one game session, after it in the next).
- **Cause:** turbo used `FindObjectsSortMode.None`, whose order changed between game sessions.
- **Fix:** clones step in scene-load order, instance id highest first, in turbo and the engine alike.
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

## Quick-restart start carries the last run's history (open)

- **Symptom:** right after a game launch, `10_WJ_DS_DJ_BS_ZIP 17.84` (the saved record) doesn't finish: submitted
  twice, no finish at physics tick 288. A freshStart trace moments later finishes 17.84. The pump failed
  `10_wjdsdjbszip-15.00` the same way seconds after a launch, but a freshStart trace later finishes it at 15.0002
  (finishTick 742). It is parked in `offline-best/hold-GameOwner2/`, unjudged.
- **Cause:** the quick restart (`Movement.respawn(true)`) resets position and the run's state, but not a set of Movement
  history fields, the RNG state or the clone timers. A start captured after a run carries that run's leftovers, and a
  start captured right after launch has the launch values. Routes solved against a start trace that had leftovers need
  those leftovers. Start diff, after launch (`go3-bad.json`) vs after other runs (`go3-good.json`), Course 10
  `wjdsdjbszip`: `facingRight` true/false, `wallJumpFacing` (1,0)/(-1,0), `dashDirection` (0,0)/(-1,0),
  `wallCoyoteDir` 0/1, `framesSinceLastDash` 119/326, `MostRecentUpwardsMoveWasNormalJump` false/true,
  `timeSinceLastAfterimage`, `emitWallSlideParticlesThisFrame`, `UnityEngine.Random` state, and
  `clonesScript.timeSinceLastClone` 10002.85/10010.63. The runs part by 1 u in x at tick 191. The offline start trace
  `start-c10-wjdsdjbszip.json` has `facingRight` false and `wallJumpFacing` (-1,0). Bonus 5's starts also differ in
  `heightOfLastJumpStart`, `lastHyperY`, `preDashMomentum` and `finalWallClingTimeLeft`.
- **Repro:** relaunch the game and bootstrap, then at once `POST /api/enginesim/submit` course 10, `wjdsdjbszip`,
  with the 17.84 route's ticks: no finish, twice. Then a trace with `freshStart` finishes 17.84.
- **Proposed fix:** make the category start canonical. When the start is captured, set these history fields, the RNG
  state and the clone timers to fixed values, as a lab start already does from its start trace. That changes every
  category's start, so the start traces would have to be recaptured and the saved routes judged again. A Full-TAS run
  starts from a fresh new game, so VMAN is not affected. Deferred for now.
- **Where:** `EngineSimBridge.PrepareEngineSimRun` (start capture), `TasPlugin.RestoreRouteStart`.
