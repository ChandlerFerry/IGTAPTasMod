# IGTAP TAS: agent notes

The route rules are the `### Rules` section of [`README.md`](README.md). Read them first. They are non-negotiable, and
a route that breaks any of them doesn't count, however fast it is.

Current targets: `bonus1` (`WJ`), Course 4 (`WJ_DS` and `WJ_DS_DJ`), Course 5 (`WJ_DS_DJ`), the `VMAN` Full-TAS run (a
Vman speedrun's start, split by area goals: [`docs/fulltas.md`](docs/fulltas.md#vman-speedrun-runs)), and the
`FalseEnding-Any%` Full-TAS run (notes and
open ideas in [`docs/fulltas.md`](docs/fulltas.md#falseending-any-and-falseending2-any-2026-09-28); unattended improvement:
`fulltas --auto`, [`docs/fulltas.md`](docs/fulltas.md#auto)). How to search:
[`enginesim/README.md`](enginesim/README.md#use).

## Route names

- Category route (the runs record, game timer): `<course>_<CAT> <time>`, `CAT` a registered category: the tokens
  `WJ`, `DS`/`DSn`, `DJ`/`DJn`, `BS`, `ORB`, `ZIP`, `OM` in that order, `NONE` when empty (the registry is
  [`Categories.cs`](Categories.cs)). The ladder (`NONE`–`WJ_DS_DJ_BS`) runs per the target's `courses.json` range:
  Courses 1–5 and the Area 1 transitions stop at `WJ_DS_DJ`, Courses 6–10 start at `WJ_DS_DJ_BS`. Courses 11–15 are
  Courses 1–5 overgrown (the game's Course 1–5 gates, Area 1 overgrown) and run only `WJ_DS_DJ_BS` of the ladder.
  Outside the ladder every course (1–15), bonus and transition runs `WJ_DS_BS_ZIP_OM`, `WJ_DS2_BS_ZIP_OM` and
  `WJ_DS_DJ_BS_ORB_ZIP_OM` (the old omnidash top category of the ladder, every refill orb and the zip movers included;
  renamed, no alias); `WJ_DS_DJ_BS_ORB` and `WJ_DS_DJ_BS_ZIP_OM` run only on the targets with refill orbs in reach and
  `WJ_DS_DJ_BS_ZIP` only on those with zip movers (`Categories.cs` lists them; README `### Rules` 19). Bonuses 17–21
  and 23 (unlock boxes, the Start a VMAN speedrun boxes among them) run no category.
- Farm route (courses only; farm this course): `<course>_<CAT> farm <time>`, physics ticks × 0.02 s from the route
  start (the quick restart) to the finish. A bonus or transition is timed that way already, so its record is its farm.
- Full-TAS route: `fulltas/<run>_NONE <time>` (runs live in `fulltas.json`, [`docs/fulltas.md`](docs/fulltas.md)); `time`
  is live time, physics ticks plus dash freezes and pauses (README `### Rules` 31), and its splits carry both the
  ticks and the held frames (`fulltas-splits.json`). A record saved when the time was ticks alone is re-timed on its
  next trace (`RememberBestSplits`).
- All are saved only by `SaveSolverRoute`, which replays them from their start: a category or farm route from the
  course start, a Full-TAS route from a fresh new game. Every judged candidate is checked for both: a new record and
  fewer physics ticks than the farm route.

## Using the game

Start the game only to:

- capture a trace or world the offline engine doesn't have yet (`tools/enginesim-capture.py`, or
  `tools/tech-check.py` for the tech demos after an engine or turbo change),
- submit an offline best that beats the saved route (`solve` does this on its own),
- run `tools/validate.py`.

Don't loop over `/api/enginesim/submit` as an evaluator, and don't call `/api/solver/start`: `POST /api/solver/preview`
(the same body) returns the engine command a studio Improve would run, which you can run offline yourself.

- Change the live game, never its save files: `POST /api/session/setup {"abilities": {...}}` sets `Movement` fields on
  the live player, `{"currencies": {"Cash": 1e22}}` sets `globalStats.currencyLookup`. Abilities are refused while
  "Don't touch my unlocks" is on, and the game autosaves these edits.
- `POST /api/oracle/bootstrap` loads the scene without the menu. The game autosaves, so a relaunch can change the save.
- Rebuilding the plugin: `python tools/enginesim-redeploy.py`.
- The player body's interpolation stays on, as the game ships it: `Movement.FixedUpdate` reads the last frame's blend
  and its position writes pull the body back by the lag, and the step-up runs ungated in every frame. 1x playback runs
  on a fixed 280 fps clock, a frame every 1/280 s across ticks as at the game's 280 cap (README `### Rules` 8); turbo
  and the offline engine model the same frames (`MacroEngine.FrameGrid`), so all three play alike. The overlay shows
  `INTERPOLATION ON (280 FPS CLOCK)`. Routes found before this (interpolation off, 56u step cap) have to be solved again,
  and routes judged before 2026-09-30's continuous grid (six frames a tick, 300 a second) judged again.
- Turbo and the offline engine run `FloatingOrigin`'s rebase as 1x does (the world moves back once the player is
  17500 from scene (0, 0)), so Float Ultras and any route that crosses the sphere judge, replay and play back alike.
- Turbo and the offline engine keep a physics callback's position write as 1x does (a sideways spring's 10u push,
  `Movement.hitSpring`). Routes judged before this that take a sideways spring may die at 1x (`5_WJ_DS_DJ 13.46` does);
  judge them again.
- The game is build 25640326 (Steam, 2026-09-30 16:37): a course's first on-screen clone spawn spends `Start`'s spawn
  reserve, so bought clones and a record break's refill come one an interval, not one a frame, and record-break ladders
  no longer pay for good (a break only brings about C/2 payouts forward); a full pool still refills a freed slot on the
  next frame. It also adds solid `Moss` tilemaps (a `MossBlue` patch on the blue blocks at x 1474-1662, y 334-510 from
  the new game's origin; the others empty for now): traces captured before it lack them. Build 25630570 (10:34) made
  buy-max boxes buy one level a physics step. Routes judged before either pay different Cash: judge them again
  (`FalseEnding-Any%` 346.74 dies at goal 86, `buy:buywj` unaffordable). The model:
  [`docs/fulltas.md`](docs/fulltas.md#clones-on-build-25640326).

## Before calling a new best real

Trace it (`IGTAP.EngineSim path <trace> <route.json>`), check where the timer starts and stops, look at the jumps, and
say what the new trick is. `python tools/audit-traces.py` checks captured traces for input-buffer violations.
