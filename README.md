# IGTAP TAS Studio

Recording, replay, editing and route solving for IGTAP, driven from a browser studio. **Route rules are in
[`AGENTS.md`](AGENTS.md); read them first.** Open work is tracked in GitHub issues.

## Components

| Piece | What it does |
|---|---|
| `TasPlugin.cs` | BepInEx plugin: route runner, recorder, HTTP API on `http://127.0.0.1:17426/`, attempt results, settings. |
| `MacroEngine.cs` | Injects route inputs at the 50 Hz physics boundary. Presses and releases do exactly what `Movement.Update` does, including buffer expiry timers. Shared with `enginesim`. |
| `TurboRunner.cs` | "Turbo": steps `Movement.FixedUpdate` → `courseScript.FixedUpdate` → `Physics2D.Simulate` → `Movement.Update` in a loop inside one frame (`Physics2D.simulationMode = Script`). Movement's `Invoke` timers are intercepted and fired on the tick the frame loop would fire them (≥50 fps semantics). ~45× real time; verified identical to real-time runs. |
| `Solver.cs` | In-game solver (Optimize tab) and `SaveSolverRoute`, the single gated save for category routes. |
| `EngineSimBridge.cs` | Endpoints the offline engine uses: `trace`, `submit` (turbo is the judge), `raycast`. |
| `enginesim/` | The game's own `Assembly-CSharp.dll` running in .NET on a Unity shim and a C# Box2D port: millions of runs without the game. See [`enginesim/README.md`](enginesim/README.md). |
| `web/` | Next.js studio, served by the plugin. |
| `simulator/` | Older hand-written physics model (FastSim). Only an idea generator now; not used by the solver. |
| `tools/` | `enginesim-capture.py` (traces), `enginesim-redeploy.py`, `validate.py` (re-run every category route in turbo), `audit-traces.py` (rule 3 over captured traces), `turbo-search.py`, `solve.py`, `fidelity-sweep.py`. |

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
- **Routes:** select, edit, duplicate, favorite (favorites are never deleted by any tool), run, record.
  Playing a category route (`<course>_<NO_WJ|WJ|WJDASH> <time>`) applies that category's unlocks for the attempt, then
  restores the save's.
- **Optimize:** pick course and capabilities, press Solve. New bests are saved as `<course>_<CAPS> <time>` through the
  gate and replace slower non-favorite routes of that category.

## Solver

- **Seeds:** every route whose name starts with `<course>_`, plus an empty route.
- **Wider categories:** a route is valid in every category with more unlocks (NO_WJ ⊂ WJ ⊂ WJDASH), so the offline
  `solve` submits each best to its own category and every wider one; a wider category is never slower than a narrower.
- **Explore (Go-Explore):** archive keyed by 48 px map cell × "course timer running", each cell keeping the earliest input
  prefix that reached it. Pick a cell (rarely picked first, timed cells ×3), resume there, append a random 1–3 chunk burst
  (stick −1/0/1, jump with hold, dash if allowed; superbounce offline).
- **Optimize:** mutate the best finish (cut, insert, dash/dash-jump/hop + retime, drop a press, re-aim). 50/50 with explore.
- **Scoring:** the game's own timer (`courseScript.currentPathTime`). A finish counts only when the solver's course stops
  while tracking.
- **Gate:** `SaveSolverRoute` replays the route from the course start and rejects it (`REJECTED …` in the solver log,
  `rejected` in the submit reply) unless it finishes on the course in the same time, every jump/dash buffer has a real
  press within the game's window, and the unlocks match the category on every tick.

## Game facts

- Score by the game timer, not "finish tick − start tick"; they differ on dash routes (cause not identified).
- Course starts (quick-restart point; the player spawns 12 px lower): C1 (2241, −292), C2 (4783.43359, 1878.2251),
  C3 (9505, 6967.01465). Courses live in `config/IGTAPTasMod/courses.json`.
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
- Real-time play has frame-timing variance (same route: 126/127/132 ticks); turbo is deterministic.
- Decompiled game code for reference: `obj/Movement.decompiled.cs`, `obj/courseScript.decompiled.cs`.

## Safety

- Nothing the plugin does in turbo or offline is saved to the game; unlocks it sets for a run are restored.
- Real-time runs use Unity at native speed; never change Unity's clock or fixed timestep.
- Preserve source routes; candidates become new routes.
