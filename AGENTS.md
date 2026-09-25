# IGTAP TAS: agent notes

The route rules are the `### Rules` section of [`README.md`](README.md). Read them first. They are non-negotiable, and
a route that breaks any of them doesn't count, however fast it is.

Current targets: `bonus1` (`WJ`), Course 4 and Course 5 (`WJ_DASH_AJ`). How to search:
[`enginesim/README.md`](enginesim/README.md#use).

## Route names

- Category route: `<course>_<NO_WJ|WJ|WJDASH|WJ_DASH_AJ> <time>`.
- Chain segment route: `<chain>/<segment>_<CAPS> <time>` (chains live in `chains.json`).
- Both are saved only by `SaveSolverRoute`, which replays them from their start: a category route from the course
  start, a segment from where the chain's earlier routes end.

## Using the game

Start the game only to:

- capture a trace or world the offline engine doesn't have yet (`tools/enginesim-capture.py`),
- submit an offline best that beats the saved route (`solve` does this on its own),
- run `tools/validate.py`.

Don't loop over `/api/enginesim/submit` as an evaluator, and don't call `/api/solver/start`.

- Change the live game, never its save files: `POST /api/session/setup {"abilities": {...}}` sets `Movement` fields on
  the live player. It refuses while "Don't touch my unlocks" is on, and the game autosaves these edits.
- `POST /api/oracle/bootstrap` loads the scene without the menu. The game autosaves, so a relaunch can change the save.
- Rebuilding the plugin: `python tools/enginesim-redeploy.py`.

## Before calling a new best real

Trace it (`IGTAP.EngineSim path <trace> <route.json>`), check where the timer starts and stops, look at the jumps, and
say what the new trick is. `python tools/audit-traces.py` checks captured traces for input-buffer violations.
