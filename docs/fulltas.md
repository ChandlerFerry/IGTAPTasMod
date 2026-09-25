# Full-TAS

The whole game from a fresh new game as one continuous input stream. A run is a list of goals, game events that must
happen in order; its time is the physics ticks from the new game's first settled tick to the last goal. Goals split
the run for display and for the offline search; nothing restarts, teleports or re-times between them. A quick restart
(`Movement.respawn(true)`, the game's QuickReset) is an input the route chooses (`reset` on a tick), so a route can
restart or run on, whichever is faster. The new game is loaded by `POST /api/session/newgame`: the menu's New Game with
no save file, except that `Saveloader` neither loads nor saves for the rest of the session.

## fulltas.json

In the game's `BepInEx/config/IGTAPTasMod/` (edits are picked up within a second, never mid-run):

```json
[{ "name": "main", "goals": ["start:1", "finish:1", "buy:buyreward", "start:1", "finish:1", "buy:buywj", "start:2"] }]
```

| Goal | Reached when |
|---|---|
| `start:N` | Course N's start gate starts its timer (it wasn't timing). |
| `finish:N` | The player's own finish at course N's end gate. |
| `buy:buyreward+buyclone` | The game bought each of these boxes (any order). Course 1: `buyreward`, `buyclone`, `buywj`; Course 2: `buyreward2`, `buyclone2`, `buyclonemult2`, `buyglobalreward2`, `buydash`. |
| `reward:bonusN` | The game buys bonus N's reward box (the upgrade box nearest its end rect: the bonus's unlock). |

An event that isn't the next goal changes nothing (buying another box, finishing another course). Unlocks come only from
the game's purchases; the plugin writes none.

## Routes

`fulltas/<run>_NO_WJ <time>`, saved only by the gate (`SaveSolverRoute`), only when strictly faster. The gate replays the
route in turbo from the new game with the usual buffer audit, checks the unlocks against the run's own purchases so
far, and refuses a quick restart on two ticks in a row (a real button is released in between). Splits per goal are kept
in `fulltas-splits.json`.

## Studio: Full-TAS tab

- **Goals** (`POST /api/fulltas/goals {name, goals}`), **Capture start** (`/api/fulltas/capture`: start trace + world,
  seeded with the saved best), **Play** (real time from a new game), **Solve** / **Stop**
  (`/api/fulltas/solve {name, minutes, windowMinutes, window}` runs `IGTAP.EngineSim fulltas`; its output shows in the
  tab). `/api/state` has `fullTas`.

## Offline search

```powershell
& $exe fulltas $traces\fulltas-main.json --minutes 60 --window-minutes 1 --window 2   # --no-submit, --fresh, --threads N
```

- The best is `<traces>\fulltas\<run>.json` (or the trace's inputs when better or when there is none).
- Windows of W goals (default 2): each worker replays the best up to goal i-1 (`Simulation.Advance`), starts there
  (`SetStart`) and searches for the fastest way through goal i+W-1. The first goal's split is kept only as part of the
  whole: a later goal reached sooner is what counts, so a lap that ends slower but sets up the next one wins.
- A window's best is spliced into the run with the old tail after it; when the old tail no longer reaches the rest (the
  state differs), the following windows are searched again from the new state, seeded with the old tail, and the run
  is kept only if it ends sooner. Before a run is complete, the search pushes the first goal not reached yet.
- Routes found from a start with the same movement state for the same next goal seed each other (the repeated laps).
- A complete, faster run goes to `offline-best\fulltas-<run>-<time>.json` and to the gate (`/api/fulltas/submit`).

## Offline economy

The whole run needs Cash offline. A start state whose economy carries the clones (every Full-TAS trace) runs the
economy natively (`enginesim/Runtime/Economy.cs`): course-finish rewards, comp boost, new-best clone paths, clone spawns
and payouts on and off screen (the clonesLoD trigger), what the courses' local boxes change (reward, clone count, clone
multiplier) and the global reward box (`GLOBALcashPerLoop`: its own course's reward; the game updates the others on a
random 5 s phase). Cases the model can't follow end the run (`Unmodeled economy: ...`): clone speed/size/colour rolls,
clone dust, Vman, other global boxes and prestige boxes.

## Status (2026-09-25)

- `main` has 200 goals: Course 1 through `reward:bonus1`, then Course 2's economy as planned by
  `python tools/plan-fulltas.py --write` (Course 2 laps and box buys), with dash bought at ~236 s in the plan.
- The offline search seeds its laps with the saved standalone course, bonus and transition routes.
- Real-time playback warns on the overlay when it drops frames: Update-driven state (clone pay, purchases waiting on
  it) can then differ from the route's.
