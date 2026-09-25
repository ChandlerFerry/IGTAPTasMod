# IGTAP TAS Studio

### Rules

- Every route must be human-playable on a real controller.
- Only send stick values a real controller can produce.
- Presses and releases behave exactly like real button presses.
- Keep the game's buffer timers: jump, dash, jump cut.
- Never write `jumpBuffer`, `dashBuffer`, `jumpCutBuffer` or similar directly.
- Inputs change only on the 50 Hz physics tick.
- Unlocks match the category exactly, on every tick.
- Turbo, offline and playback use identical unlocks.
- New-game chains gain unlocks only through the game's purchases.
- "Don't touch my unlocks" on: never write an unlock.
- Bonuses run only in their first category.
- Time runs from the course's start gate to end gate.
- Finishing a different course never counts.
- Flying starts allowed: time counts from the last start-gate crossing.
- Score courses by the game timer, not tick counts.
- Bonuses, transitions, purchases: count physics ticks, dash/spring ticks included.
- Nothing after the finish is part of the route.
- No teleports, position offsets or moving the player.
- Chain segments start exactly where the previous segment ended.
- Only reset: the game's quick restart, `Movement.respawn(true)`.
- Deaths respawn at the course reset point, never a checkpoint.
- Purchases count only when the game itself buys the box.
- Spike grace is allowed: up to two overlapping ticks.
- Never replace game logic; patches only read, input, skip saves.
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
- Only save strictly faster times.
- Never delete favorite routes; candidates become new routes.
- Trace every new best and name its trick.

Details: [`AGENTS.md`](AGENTS.md). Runs (chains, the new-game run, its parts): [`docs/runs.md`](docs/runs.md). The whole
game as one run with goals: [`docs/fulltas.md`](docs/fulltas.md).

## Layout

| Piece | What it does |
|---|---|
| `TasPlugin.cs` | BepInEx plugin: route runner, recorder, HTTP API on `http://127.0.0.1:17426/`. |
| `MacroEngine.cs` | Injects route inputs at the 50 Hz physics boundary. Shared with `enginesim`. |
| `TurboRunner.cs` | Deterministic faster-than-real-time replay inside the game. |
| `Solver.cs` | Optimize tab and `SaveSolverRoute`, the only way a category route gets saved. |
| `EngineSimBridge.cs` | Trace, submit and raycast endpoints for the offline engine. |
| `enginesim/` | The game's own code running offline for search. See [`enginesim/README.md`](enginesim/README.md). |
| `FullTas.cs`, `FullTasPlugin.cs` | Full-TAS: goals (shared with `enginesim`), gate, import, trace, playback, solve. |
| `web/` | Next.js studio, served by the plugin. |
| `tools/` | Capture, redeploy, `validate.py`, `audit-traces.py`. |

## Build

```powershell
dotnet build .\IGTAPTasMod.csproj -c Release     # web UI + plugin, copied into the game
```

Close the game before building (the plugin DLL is locked), then relaunch it. `python tools/enginesim-redeploy.py` does
this for a plugin-only build.

## Solving

See [`enginesim/README.md`](enginesim/README.md#use).
