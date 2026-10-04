# Zone load mid-run: turbo and the offline engine

Why: a Full-TAS run can't go past "touch grass" (`Zone 1-2 transition`, the root at export pose (16677, 3248), its
`checkpointScript` the TouchGrass-ANY% end): 1x loads zone 2 two frames after `zoneChanger.OnTriggerEnter2D`, turbo only
after its run returns, the engine never (`cuts.txt`). The True Ending run (TrueEnding%: block swap, touch grass, then the
zip movers, `buytree16`, 625M NP) needs all three to load it alike (README ### Rules 7-8, 49-50).

## Facts (decompile, repo, the new-game export)

- `_LoadZone` (ZoneLoader.cs:35-63) toggles only the zone roots and their `ZoneDisableLoadList.toLoad` (zone 1: Courses
  1-5 `DisableBits`; zone 2: Courses 6-10 `DisableBits`: start gate, end gates, checkpoints, orbs, zip movers).
  Courses and clones sit under the root `Courses` and never turn off: no run wakes a course.
- Zone 2's terrain tilemaps are under `Grid`/`biggerGrid`, on and exported. The only off composites under zone 2 are
  the `TreePart*Ground`, which `PhysicsWorld.PathShapes` builds already. No new tilemap export.
- The geometry blocker is bodies: 16 kinematic zip movers and 5 dynamic course `Start` gates are off at export, and
  `PhysicsWorld.cs:157-162` skips off non-static bodies, so the first step after the load aborts (`Unbodied`).
- OnEnable with effect: `upgradeBox.OnEnable` (2 draws, upgradeBox.cs:317-324), `ObjectBobber.OnEnable` (1 draw,
  exception 11), `PlatformMover.OnEnable` → `JumpToState` (moves the mover, PlatformMover.cs:221-233),
  `AtomColliderSystem.OnEnable` (cancel, `InvokeRepeating("callAddNewAtom",0,0.4)`, :111-115). No game `OnDisable`.
- Start on first wake (next frame): LightFlicker 1 draw, RandomAnimSpeedComponent 2 (3 broken), JiggleDropScript 1,
  `AtomColliderSystem.Start` → `RecalcNpRewardPerTick` (pure). The 17 never-started zone 2 `upgradeBox.Start`s are
  idempotent in a new game (`course` null, `baseCapMult` 1, `TimesUsed` 0).
- Zone 1 off stops its coroutines (Tree 2's `IE_Grow`, zone 1 zip movers mid-move); invokes run on.
- Unknown until measured: whether `_LoadZone` runs in frame k=1 or k=2 of the trigger's tick, the first
  `callAddNewAtom` frame, the Start-draw frames, whether later grid calls keep one k.

## Tasks

- **T0, 1x ground truth.** ProbeRunner `zone` rows (prefixes on `zoneChanger.OnTriggerEnter2D`, `ZoneLoader._LoadZone`:
  frame, tick, time, `activeZone`; `AtomColliderSystem.OnEnable`/`Start`). A Full-TAS savestate just left of the
  activator (with and without `rebaseAt`), inputs walking through into the Tree area; `tools/probe.py`. Settles the
  four unknowns above.
- **T1, turbo** (TurboRunner.cs): `ZoneLoader` in the parked set (:353); `Resume(Wait.Frame)` once per frame (and held
  repeat) in the frame loop (:1284-1329); the atom grid due carries a frame phase (`BeforeInvokeRepeating`, the k==0
  call :1320, `HandAtomGrid`, snapshots); orbs found with inactive included (:1195); `Resume` drops routines whose
  target is inactive; restore runs `RestoreMovers` after `RestoreEconomy`, keeps `activeZone`, stops `ZoneLoader`'s
  coroutines; StartPass recomputes NP for a woken atom collider; a woken never-started box whose Start isn't
  idempotent ends the run loudly. Check: `tools/tech-check.py`, `tools/validate.py`, FalseEnding%-280 judged unchanged.
- **T2, trace start** (EngineSimBridge.cs): `neverStarted` (turbo's started/modelled sets plus the atom collider),
  `activeZone`; refuse a start with an `IE_Load` pending.
- **T3, engine bodies**: port `b2Body::SetEnabled` (v2.4.1) into `enginesim/Box2D/src/Dynamics/Bodies/Body.cs`; build
  off non-static bodies disabled (PhysicsWorld.cs:157-162); `ActiveChanged` toggles them, an enabled body takes its
  transform's pose; the flag in `b2WorldState`. Check: `replay` of FalseEnding%-280 and TouchGrass-ANY% unchanged,
  `determinism` DETERMINISTIC.
- **T4, engine zone load**: drop `cuts.txt:16`; `zoneChanger::OnTriggerEnter2D`, `PlatformMover::OnEnable`,
  `OvergrowthLoadScript::OnLoad` into entries.txt; `ZoneLoader` in `Continues` (Simulation.cs:238); `Wait.Frame`
  resumed per frame in `InterpolatedFrames` and EconomyDrive.cs:159; woken `PlatformMover.OnEnable` from the
  `Activated` hook; hooks.txt `replace AtomColliderSystem::OnEnable` → `Economy.GridStart(tick, frame)`, the
  `AtomsLand` abort gone; `_neverStarted` from the trace; the Scheduler drops routines of targets gone inactive.
- **T5, docs**: README exception 12 (rule text: the user approves), enginesim/README.md:1832-1840 and :1884-1886,
  `cuts.txt` header, a fidelity-log entry.
- **T6, three-way check**: the T0 savestate and ticks through turbo (`/api/enginesim/trace`, `world: true`) and the
  engine (`replay --ticks-out`, `probe frames:true`); `tools/economy-diff.py --ticks` both ways; `rng` words per frame,
  positions per tick, zone and atom rows.

Hard parts: the yield and first-invoke frames; turbo's and the engine's never-started sets agreeing; zone 1's
coroutines stopping; FloatingOrigin rebasing near the border (the activator is about 16.7k from the new-game origin
against 17.5k), so the `rebaseAt` variant.
