# IGTAP TAS Studio

### Rules

- Every route must be human-playable on a real controller.
- Only send stick values a real controller can produce.
- Presses and releases behave exactly like real button presses.
- Keep the game's buffer timers: jump, dash, jump cut.
- Never write `jumpBuffer`, `dashBuffer`, `jumpCutBuffer` or similar directly.
- Inputs change only on the 50 Hz physics tick. The one exception is the dash freeze's stick (`turnX`,
  `MacroStep.dashTurnX`): a dash pressed with no sideways stick takes the first sideways stick the game reads before
  the next tick, as a player tilting the stick during the freeze turns it. It is legal.
- The player body's interpolation stays off (the TAS forces it), and the step-up gate keeps what that changes to turbo's:
  `Movement.Update`'s step-up lifts the player at most once between two physics ticks (`MacroEngine.AllowStepUp`, at
  most 56 units). With interpolation off, the step-up otherwise lifts again in every rendered frame until the next tick
  (six 56-unit lifts at playback's 280 fps), so 1x playback would leave the route turbo judged. The overlay shows it as
  `INTERPOLATION OFF (56u STEP CAP)`. This, the Vman credits' crash (below) and a fixed-clock probe's dash freeze
  (`fixedClock`, [`enginesim/README.md`](enginesim/README.md#probe): `Movement.TimeStop` counts the fixed frame instead
  of the wall clock's) are the only places a patch changes game logic.
- Unlocks match the category exactly, on every tick. Categories stack: `NONE`, `WJ` (wall jump), `WJ_DS` (+ dash),
  `WJ_DS_DJ` (+ double jump), `WJ_DS_DJ_BS` (+ block swap on dash, `blockSwapUnlocked`). No category has the
  omnidirectional dash (`omniDashUnlocked`, the OmniDash box's toggle): it is off in every one.
- Turbo, offline and playback use identical unlocks.
- Full-TAS runs gain unlocks only through the game's purchases.
- "Don't touch my unlocks" on: never write an unlock.
- Bonuses run only in their first category, or up to their `maxCapabilities` in `courses.json`. Courses and
  transitions run from `minCapabilities` up to `maxCapabilities`, or up to the top category when it's unset: Courses
  1–5 and the Area 1 transitions stop at `WJ_DS_DJ`.
- Courses 6–10 are Area 2 courses: a start gate is on only while its zone is loaded (Courses 1–5 zone 1, 6–10 zone 2).
  Every quick restart onto a course loads the zone of the start gate nearest it the game's way (`ZoneLoader`, as the
  zone activators do); the breaker plays no part. Their lowest category is `WJ_DS_DJ_BS`.
- Courses 11–15 are Courses 1–5 with Area 1 overgrown: the game's own Course 1–5 gates and timer on the overgrown
  terrain (spikes, moss, zip movers). Every quick restart onto one sets Area 1 overgrown the game's way
  (`currentA1State`, then `OvergrowthLoadScript.OnLoad`, as the zone loader does); onto Courses 1–5 it sets it back to
  not overgrown, and every other route runs in the save's own state. Saves wait until the save's state is back. Their
  only category is `WJ_DS_DJ_BS`; transitions to or from them aren't supported.
- Time runs from the course's start gate to end gate.
- Finishing a different course never counts.
- Flying starts allowed: time counts from the last start-gate crossing.
- Score courses by the game timer, not tick counts: that is the runs record.
- A course's farm time counts physics ticks from the route start (the quick restart) to the finish.
- Bonuses, transitions, purchases: count physics ticks, dash/spring ticks included.
- Nothing after the finish is part of the route.
- No teleports, position offsets or moving the player.
- Only reset: the game's quick restart, `Movement.respawn(true)`.
- Deaths respawn where the game's checkpoint setting sends them: the studio's **Checkpoint respawns** (one setting for
  every route, trace and search; the overlay shows it) off, at the course reset point; on, at the last in-game
  checkpoint touched. Off a course both go to that checkpoint. Area goals (`at:`) are not checkpoints.
- Purchases count only when the game itself buys the box.
- Spike grace is allowed: up to two overlapping ticks.
- Never replace game logic; patches only read, input, skip saves (and the step-up gate above). The one other patch: a
  Vman speedrun's credits (`FakeCreditsControlScript.IE_DoVmanCredits`, the `VMAN` run's last goal) crash the game on
  purpose (`Utils.ForceCrash`) after writing PlayerPrefs `VmanCoursesCompleteTemp`; the plugin drops both calls. Both
  come after the goal, so no route sees them.
- The one RNG write: the Full-TAS runs' fixed seed, `UnityEngine.Random.InitState` at the fresh new game before its start snapshot; nothing else writes the RNG.
- The one other setup write: a Vman Full-TAS start (`docs/fulltas.md`) sets, in a fresh new game, Area 1 overgrown,
  `vmanTime` and `VmanStartsWithOmniDash` to their boxes' full effect and the player under the 1x Vman start box; the
  game itself buys the box and loads the Vman world the run starts in.
- The one exception to "no teleports" and "unlocks only through the game's purchases": a Full-TAS run with a
  `savestate` (`docs/fulltas.md#savestate-starts`) starts from that savestate, restored with its unlocks in the settled
  new game. It tests a stretch; it is not a whole-game route.
- Never change Unity's clock or fixed timestep.
- Never edit the game's save files.
- Never autosave while category unlocks or turbo are applied.
- Save category routes only through `SaveSolverRoute`.
- Never loosen the gate or add another save path.
- Search offline in `enginesim`, never in the game.
- Game only captures traces, confirms bests, runs `validate.py`.
- No game-judged search loops or extra game instances.
- Agents never start the studio's Optimize tab.
- `enginesim` can't do something? Fix `enginesim`, don't work around.
- Offline replay must match the game tick for tick.
- `FloatingOrigin` rebases in turbo and offline as at 1x: turbo runs the game's own `LateUpdate` and `MoveOrigin`, and a
  turbo restore puts the world back at its snapshot's origin (`enginesim/README.md`).
- Only save strictly faster times: record by the game timer, farm by physics ticks.
- Trace every new best and name its trick.

Details: [`AGENTS.md`](AGENTS.md). The whole game from a new game as one run with goals:
[`docs/fulltas.md`](docs/fulltas.md). What a human can put into the game, and how routes match it:
[`docs/human-input.md`](docs/human-input.md).

## Layout

| Piece | What it does |
|---|---|
| `TasPlugin.cs` | BepInEx plugin: route runner, recorder, HTTP API on `http://127.0.0.1:17426/`. |
| `MacroEngine.cs` | Injects route inputs at the 50 Hz physics boundary. Shared with `enginesim`. |
| `TurboRunner.cs` | Deterministic faster-than-real-time replay inside the game. |
| `Solver.cs` | Optimize tab and `SaveSolverRoute`, the only way a category route gets saved. |
| `EngineSimBridge.cs` | Trace, submit and raycast endpoints for the offline engine. |
| `enginesim/` | The game's own code running offline for search. See [`enginesim/README.md`](enginesim/README.md). |
| `FullTas.cs`, `FullTasPlugin.cs` | Full-TAS: goals (shared with `enginesim`), gate, trace, playback, solve. |
| `TechCatalog.cs`, `techs.json` | The studio's Tech tab: every named tech (glitchless, NMG, major), who named it, and a demo route that performs it. `techs.json` seeds the config folder's `techs.json`, which the tab edits. |
| `ProbeRunner.cs` | 1x probes: exact inputs at normal speed from a category start or savestate, logged every tick and rendered frame (`enginesim/README.md#probe`). |
| `web/` | Next.js studio, served by the plugin. |
| `tools/` | Capture, redeploy, `validate.py`, `audit-traces.py`, `enginesim-rays.py` (Unity vs offline raycasts), `plan-fulltas.py`, `upgrade-boxes.py` (every one-off box; adds free ones to `courses.json` as bonuses), `probe.py` (a 1x probe and its per-frame log), `tech-check.py` (every tech demo, turbo against the offline engine). |

## Build

```powershell
dotnet build .\IGTAPTasMod.csproj -c Release     # web UI + plugin, copied into the game
```

The game is the Steam install (`Directory.Build.props`, `tools/gamepaths.py`; `IGTAP_GAME` overrides it). Close the
game before building (the plugin DLL is locked), then relaunch it. `python tools/enginesim-redeploy.py` does this for a
plugin-only build.

## Solving

See [`enginesim/README.md`](enginesim/README.md#use).
