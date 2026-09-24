# IGTAP TAS — Handoff (2026-09-23)

## Where things stand

**Best route that works in the game (Course 2): `2_WJ SIM2 A 835 (16.70s pred)` — 16.72s (836 course ticks).**
The route history: 18.42 (hand-made) → 17.78 (#4) → 16.72 (A). Route `2_WJ SIM2 B` did 16.96.

What changed this session:
- The offline optimizer can now find new route shapes, not just trim frames.
- The simulator matches the real game much more closely, checked against recorded game runs.
- The oracle (the second game instance) now tests candidates in the real game automatically.

Every candidate faster than A has **died in the game** so far. Each death came from a mismatch
between the simulator and the game. Fixing each mismatch is how progress gets made (see "Next steps").

## How the pieces fit

| Piece | What it does |
|---|---|
| `simulator/OfflineSuite.cs` (`suite`) | The optimizer the studio button runs. Repeated passes: route-shape changes (drop a jump / redirect a block, then retime nearby blocks) → timing beam (width 64) → restart from the new best until a pass stops improving. Candidates are rejected if they touch spikes (2px margin) or break when the start shifts ±1px. |
| `simulator/Program.cs` (`FastSimulator.Step`) | The physics model. |
| `simulator/RayWorld.cs` | Movement's raycasts cast against the exported colliders. **Exact: 0 errors across 76,910 oracle ticks × 13 rays** (`ray-validate`). Used for grounded/walk-off, airborne landing, and step-up decisions. |
| `tools/fidelity-sweep.py` | The regression suite. Replays every recorded game run (studio results + oracle corpus) through the simulator and reports where each first diverges. **Run it after every simulator change.** Current: 45 runs, 9/9 exact finish ticks, every death predicted as a failure. |
| `tools/oracle.py` | Runs routes on the oracle game instance (port 17427). `first_split()` / `trace()` compare the simulator and the game tick by tick. Every run is appended to `planning/fidelity-routes/oracle-corpus.json`. |
| `tools/search-loop.py` | The full closed loop: search → oracle-test the top distinct candidates → adopt only real-game wins → repeat → import the winner into the studio. **Written but not run end-to-end yet.** |
| `planning/fidelity-routes/` | Archive of every route version seen, plus the oracle corpus, so deleting routes in the studio doesn't shrink the test set. |

Debug commands (`IGTAP.FastSim.exe`):
- `route-trace <model> <routes.json> <route> <result.json> <from> <to>` — simulator vs game, tick by tick. It also prints the finish tick and first spike contact.
- `ray-probe <model> x y facingRight` — the 13 ray results at one pose.
- `ray-validate <model> <oracle results dir>` — checks the ray model against oracle diagnostics.

## Simulator fixes this session (each verified with the sweep)
- Wall-top forward ray: the 0.64px skin now applies only to underside hits.
- Instant wall slide when falling faster than 150 (was 400).
- Ledge-corner landing followed by a jump: the body pops to its resting pose + 2px first.
- TAS input semantics (`MacroEngine`): a press stays buffered until consumed; a cut lasts 2 ticks; held→not-held counts as a release.
- Wall ray and forward ray use the post-integration Y.
- Ceiling: the game zeroes upward speed on the ticks *after* impact (`OnCollisionStay2D`, not on impact), and the head height is +27.
- Coyote time is 6 ticks (not 5).
- Grounded state is decided by the real foot rays. Those rays are asymmetric: −15/+6/+25 when facing right.
- A body resting on an edge only holds its height and speed; onGround and coyote still come from the rays.
- Airborne landings are detected by the real rays; the jump happens on the next tick.
- A body over a block's top that falls onto it lands on the top instead of being pushed out sideways.
- Step-up uses the real r6/r7 rays.
- Course start/finish trigger timing: **simulated course ticks now equal the in-game timer exactly.**

## Open mismatches (the next work)
From the last oracle batch (`oracle-A4`; all 8 candidates died):
1. **Ceiling hit while moving sideways (tick 22, candidate with a hop):** the game stops X at 4966.485 (a wall/ceiling corner) and keeps upward speed. The simulator clamps Y instead. This is the ceiling-corner branch in `Step` (the `oldHorizontalPenetration < 7f` heuristic).
2. **Landing on the 6592 block top (tick 317):** the game lands at y=1645 one tick earlier than the simulator. The simulator slides past, grabs the wall, and wall-jumps.
3. `[oracle] #4/#5` in the sweep diverge at tick 361. Low priority: the simulator still predicts death.
4. Small landing-settle wobble (0.5–3px, recovers). Harmless so far.

The underlying cause of most of these: `Step` runs in a different order from the game (gravity → detection → input → integrate, with projections to compensate). The robust fix is to rewrite `Step` in the game's `FixedUpdate` order (see `scratch_movement.txt` 833–1160 and `getPlayerControlledInput` 1586+), using `RayWorld` for all probes. Validate it with `fidelity-sweep.py` and a one-tick check against the oracle diagnostics (they record the full state every tick).

## Next steps
1. Fix open mismatches 1 and 2 using the method: `oracle.first_split()` → `route-trace` → `ray-probe` / game code → minimal fix → sweep (no regressions allowed).
2. Improve shortlist diversity. `DistinctBy(ShapeChange)` treats hop variants as distinct, so the oracle batch was 8 copies of one idea. Group candidates by structural change and ignore hop details.
3. Run `python tools/search-loop.py "2_WJ SIM2 A 835 (16.70s pred)" <A's latest result json>`. It stops on its own and imports any confirmed win.
4. Speed: a 2M-simulation search takes about 9 minutes now (ray casts). Adding a cache or using a smaller budget per round in the loop would help.

## Housekeeping
- The game plugin and the `IGTAPFastSim` copy are built and deployed from the current source (the studio button uses the new optimizer). Restart the game if the plugin DLL changes.
- The folder is not a git repo. There are simulator backups one level up (`../Program.pre-*.cs`, `../Program.nodebug.cs`, `../OfflineSuite.pre-passes.cs`). Safe to delete.
- The dead routes `2_WJ SIM3 729` and the older SIM routes can be deleted from the studio. Their runs are preserved in the archive.
- Known limit: start/finish trigger offsets and the coyote change were validated on Course 2 only. Course 1 hasn't been checked (there's no Course 1 model export).
