# Full-TAS

The whole game from a fresh new game as one continuous input stream. A run is a list of goals, game events that must
happen in order; its time is the physics ticks from the new game's first settled tick to the last goal. Goals split
the run for display and for the offline search; nothing restarts, teleports or re-times between them. A quick restart
(`Movement.respawn(true)`, the game's QuickReset) is an input the route chooses (`reset` on a tick), so a route can
restart or run on, whichever is faster. This lives next to the chains (`docs/runs.md`); nothing there changed.

## fulltas.json

In the game's `BepInEx/config/IGTAPTasMod/` (edits are picked up within a second, never mid-run):

```json
[{ "name": "main", "goals": ["start:1", "finish:1", "buy:buyreward", "start:1", "finish:1", "buy:buywj", "start:2"] }]
```

| Goal | Reached when |
|---|---|
| `start:N` | Course N's start gate starts its timer (it wasn't timing). |
| `finish:N` | The player's own finish at course N's end gate. |
| `buy:buyreward+buyclone` | The game bought each of these boxes (any order; the chains' purchase tokens). |
| `reward:bonusN` | The player touches bonus N's reward box. |

An event that isn't the next goal changes nothing (buying another box, finishing another course). Unlocks come only from
the game's purchases; the plugin writes none.

## Routes

`fulltas/<run>_NO_WJ <time>`, saved only by the gate (`SaveSolverRoute`), only when strictly faster. The gate replays the
route in turbo from the new game with the usual buffer audit, checks the unlocks against the run's own purchases so
far, and refuses a quick restart on two ticks in a row (a real button is released in between). Splits per goal are kept
in `fulltas-splits.json`.

## Studio: Full-TAS tab

- **Import from chain** (`POST /api/fulltas/import {name, chain}`): the chain's goals, and its saved routes as one input
  stream (each cut where it finished in a turbo replay of the chain, restarts as `reset` ticks). A chain solved to its
  end is judged by the gate; one solved in part becomes the run's seed (`enginesim/fulltas-<run>-seed.json`).
- **Goals** (`POST /api/fulltas/goals {name, goals}`), **Capture start** (`/api/fulltas/capture`: start trace + world,
  seeded with the saved best or the import's seed), **Play** (real time from a new game), **Solve** / **Stop**
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
random 5 s phase). Older traces keep the recorded `purchase.income`. Cases the model can't follow end the run
(`Unmodeled economy: ...`): clone speed/size/colour rolls, clone dust, Vman, other global boxes and prestige boxes.

## Status (2026-09-25)

- `main` imported from `newgame` (72 of 79 segments solved there): 114 goals, the seed reaches 104 (through
  `bonus1to2`). Its offline replay matches turbo on every tick's position and Cash through all 104 goals; the only
  differences are sub-micro velocities on ground seams that never move the player.
- Next: goal 105 (`buy:buydash`, 3,000,000 W) needs Course 2's economy first: the `newgame` chain's plan
  (`docs/runs.md`, Economy) buys Course 2's boxes (`buy:buyreward2`, `buy:buyclone2`, …) before it.
- `short` (the first 8 goals of `main`: three Course 1 laps with two reward buys) is the end-to-end check: seeded
  from `main`'s import at 14.62, a 4-minute studio solve reached 14.58 (laps 2 and 3 after the quick restart now take
  102 ticks like lap 1, not 103), both saved by the gate, and real-time playback finishes it in 14.58.
