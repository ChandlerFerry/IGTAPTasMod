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

## Far teleport rebased every other tick

- **Symptom:** a far teleport (a quick restart or death across the map) rebased again every tick or two and the player's
  world position jumped by the origin each time (Test-No-Omni-Skip's windows reached world (-21055, -15606) and ±k ×
  (20933, 14987)).
- **Cause:** the tick's rendered frames were read at the Box2D body, which follows a rebase only at the next sync.
- **Fix:** after a rebase the tick's rendered frames end with the player at the step's end moved with the world
  (`_stepTo`, as turbo's `Place(player, _stepTo)`).
- **Where:** `TurboRunner.Place`, `_stepTo`.

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
- **Where:** `OfflineSolver._archive`, `StatesPerWorker`, `CachedDraws`, `ArchiveStatus`.

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
- **Where:** `PhysicsWorld.FingerprintOf`, `ColliderState.Pristine`, `ColliderState.Tracked`,
  `PhysicsWorld._changed`, `PhysicsWorld._changedCapture`.

## Grid rebuilt on every change

- **Symptom:** `WJ_DS_DJ_BS` cost a full-world rebuild four times per dash (block swap toggles the blue/orange
  composites), with the LOH garbage stalling every worker (C11 at 30 threads: ~110 runs/s, 4,000 ticks/s).
- **Cause:** the static-shape query grid was rebuilt whole on each change.
- **Fix:** a collider whose static fixtures change, or whose static body moves, is regridded alone at the next query
  (throughput ~1,800 runs/s, 205,000 ticks/s).
- **Where:** the scene-query grid behind raycasts, point tests and `Collider2D.Distance`.
