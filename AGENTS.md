# IGTAP TAS: agent notes

The route rules are [`RULES.md`](RULES.md). Read them first. They are non-negotiable, and
a route that breaks any of them doesn't count, however fast it is.

Current targets: `bonus1` (`WJ`), Course 4 (`WJ_DS` and `WJ_DS_DJ`), Course 5 (`WJ_DS_DJ`), the `VMAN` Full-TAS run (a
Vman speedrun's start, split by area goals: [`docs/fulltas.md`](docs/fulltas.md#vman-speedrun-runs)), and the
`FalseEnding%-280` Full-TAS run (notes and open ideas from its predecessor, the deleted `FalseEnding-Any%`, in
[`docs/fulltas.md`](docs/fulltas.md#falseending-any-and-falseending2-any-2026-09-28); solving: `fulltas <trace>`
(`solve-run`, [`docs/fulltas.md`](docs/fulltas.md#solving-v2-fulltas-trace-is-solve-run), handoff `docs/notes/v2-handoff.md`)), and the `Zip%` Full-TAS run (`Tree%` on to the zip
movers, [`docs/fulltas.md`](docs/fulltas.md#touchgrass-any-tree-and-zip-2026-10-03)). How to search:
[`enginesim/README.md`](enginesim/README.md#use).

## Route names

- Category route (the runs record, game timer): `<course>_<CAT> <time>`, `CAT` a registered category: the tokens
  `WJ`, `DS`/`DSn`, `DJ`/`DJn`, `BS`, `ORB`, `ZIP`, `OM` in that order, `NONE` when empty (the registry is
  [`Categories.cs`](shared/Categories.cs)). The ladder (`NONE`–`WJ_DS_DJ_BS`) runs per the target's `courses.json` range:
  Courses 1–5 and the Area 1 transitions stop at `WJ_DS_DJ`, Courses 6–10 start at `WJ_DS_DJ_BS`. Courses 11–15 are
  Courses 1–5 overgrown (the game's Course 1–5 gates, Area 1 overgrown) and run only `WJ_DS_DJ_BS` of the ladder.
  Outside the ladder every course (1–15), bonus and transition runs `WJ_DS_BS_ZIP_OM`, `WJ_DS2_BS_ZIP_OM` and
  `WJ_DS_DJ_BS_ORB_ZIP_OM` (the old omnidash top category of the ladder, every refill orb and the zip movers included;
  renamed, no alias); `WJ_DS_DJ_BS_ORB` and `WJ_DS_DJ_BS_ZIP_OM` run only on the targets with refill orbs in reach and
  `WJ_DS_DJ_BS_ZIP` only on those with zip movers (`Categories.cs` lists them; RULES.md 19); bonus4 and bonus12 need the zip
  movers, so they run only the `ZIP` categories. The tree transitions `tree<L>to<C>` (RULES.md 21) run
  `WJ_DS_DJ_BS` and every outside-ladder category. Bonuses 17–21
  and 23 (unlock boxes, the Start a VMAN speedrun boxes among them) run no category, and neither does `unlockJumpDash`
  (the jump and dash refresh orbs box at The Tree level 10, courses.json `tree`).
- Direction record (two-way courses: both `avoid-forwards` and `avoid-backwards` in `forced-paths.json`, Courses 1-3):
  `<course>_<CAT> forwards <time>` and `<course>_<CAT> backwards <time>`, the category's best by the game timer of a
  route that never cuts `avoid-backwards` (forwards) or `avoid-forwards` (backwards) (`Categories.Directions`). The gate
  checks the blocks on its own replay and saves a record as each direction record it keeps and beats, beside or instead
  of the category record; a Full-TAS run reaching the course in another state gets both directions to pick from (its
  seeds, and `LoadCourseRoutes`' fastest of each direction by the offline bests' `directions`).
- Human record (courses only, not bonuses or transitions): `<course>_<CAT> human-major <score>` and
  `<course>_<CAT> human-nmg <score>`, the category's best of a human class (RULES.md 54: digital stick, two
  jump buttons; human-nmg no major span) by its score, the game timer plus the default weights' seconds for spike grace,
  frame-perfect inputs and back-to-back pairs. Improve with `class` (`solve --auto --class`) writes
  `offline-best\<course>_<caps>-<class>-<score>.json`; the gate judges it as a record, scores it with `techs-used` on the
  course trace and saves it under a strictly lower score.
- Farm route (courses only; farm this course): `<course>_<CAT> farm <lap>`, a lap of the course's steady-state loop
  (finish to finish, by a quick restart or the drop through the end gate) in live time, seconds to 4 places (RULES.md 30);
  the route is its lead and its loop (`loopFrom`, `loopLaps`). A bonus or transition is timed in ticks from
  its start already, so its record is its farm.
- Full-TAS route: `fulltas/<run>_NONE <time>` (runs are defined in `data/runs/<run>.json`, [`docs/fulltas.md`](docs/fulltas.md)); `time`
  is live time, physics ticks plus dash freezes and pauses (RULES.md 31), and its splits carry both the
  ticks and the held frames (`fulltas-splits.json`). A record saved when the time was ticks alone is re-timed on its
  next trace (`RememberBestSplits`). That is the run's `major` class; its other classes (RULES.md 54) are
  `fulltas/<run>_NONE nmg <time>`, `fulltas/<run>_NONE human-major <time>` and `fulltas/<run>_NONE human-nmg <time>`,
  one best each, a human one ranked by its score (`fulltas-classes.json`).
- All are saved only by `SaveSolverRoute`, which replays them from their start: a category route from the course start,
  a farm route from it through its loop four times, a Full-TAS route from a fresh new game. A judged plain candidate is
  checked for a new record; a farm loop (`offline-best\<course>_<caps>-loop-<lap>.json`) for a faster farm lap.

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
- Rebuilding the plugin: `python tools/enginesim-redeploy.py`, from this checkout only. The plugin reads `data/runs/` from the
  folder it was built in (`IgtapRepo`, plugin/IGTAPTasMod.csproj): one deployed from a worktree loses its runs when the worktree is
  removed, and every Full-TAS submit then fails `No Full-TAS run named ...`.
- Engine changes: one agent edits `enginesim/` (and `shared/`, which it compiles too) at a time. Every engine change goes
  through `IGTAP.EngineSim check`, which replays the complete Full-TAS runs and the trace corpus against
  `enginesim/check/baseline.json` (about 25 s). The pre-commit hook runs it (`python tools/setup-hooks.py` turns the
  hooks on). An intended change commits its new baseline (`check --accept`) in the same commit, so every changed result is
  in the diff. Long searches run on a private build of committed code.
- Several searches share `enginesim\Cli\bin\Release\net10.0\IGTAP.EngineSim.exe` (the plugin's Optimize and Full-TAS
  solves, other agents). Stop only your own engine processes, by PID, never `taskkill /IM IGTAP.EngineSim.exe`; while
  others run, build an engine of your own to a private folder (`dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c
  Release -o <dir>`), since the shared exe is locked while it runs.
- The player body's interpolation stays on, as the game ships it: `Movement.FixedUpdate` reads the last frame's blend
  and its position writes pull the body back by the lag, and the step-up runs ungated in every frame. 1x playback runs
  on a fixed 280 fps clock, a frame every 1/280 s across ticks as at the game's 280 cap (RULES.md 8); turbo
  and the offline engine model the same frames (`MacroEngine.FrameGrid`), so all three play alike. The overlay shows
  `INTERPOLATION ON (280 FPS CLOCK)`. Routes found before this (interpolation off, 56u step cap) have to be solved again,
  and routes judged before 2026-09-30's continuous grid (six frames a tick, 300 a second) judged again.
- Turbo and the offline engine run `FloatingOrigin`'s rebase as 1x does (the world moves back once the player is
  17500 from scene (0, 0)), so Float Ultras and any route that crosses the sphere judge, replay and play back alike.
- Turbo and the offline engine keep a physics callback's position write as 1x does (a sideways spring's 10u push,
  `Movement.hitSpring`). Routes judged before this that take a sideways spring may die at 1x (`5_WJ_DS_DJ 13.46` does);
  judge them again.
- Turbo and the offline engine keep the player's body exact through turbo's own transform writes, as 1x does
  (`TurboRunner.KeepBody`, 2026-10-09; [`docs/fidelity-log.md`](docs/fidelity-log.md)): before it turbo rounded the body
  through the player's moved parent every frame, and the engine did after an in-run rebase. Routes judged before it can
  play differently at 1x (`ZipEscape%`'s 1158-goal offline best stops at goal 970 at 1x and offline): judge them again.
  Since 2026-10-10 turbo doesn't write the player's transform or body at all where the game doesn't: its frames' game code
  reads the blend through `TurboRunner.BlendRead` (RULES.md exception 14), so the body keeps the sweep centre 1x carries.
- The game is build 25706930 (Steam, 2026-10-04): an on-screen clone spawn resets a timer at two intervals or more,
  so a full pool's banked timer no longer refills a freed slot on the next frame, and every clone bought recomputes
  the interval ([`docs/fulltas.md`](docs/fulltas.md#clones-on-build-25706930)). Full-TAS routes judged before it pay
  different Cash (`TouchGrass-ANY%` 704.67 strays at goal 255): judge them again.
- Build 25640326 (Steam, 2026-09-30 16:37): a course's first on-screen clone spawn spends `Start`'s spawn
  reserve, so bought clones and a record break's refill come one an interval, not one a frame, and record-break ladders
  no longer pay for good (a break only brings about C/2 payouts forward); a full pool still refills a freed slot on the
  next frame. It also adds solid `Moss` tilemaps (a `MossBlue` patch on the blue blocks at x 1474-1662, y 334-510 from
  the new game's origin; the others empty for now): traces captured before it lack them. Build 25630570 (10:34) made
  buy-max boxes buy one level a physics step. Routes judged before either pay different Cash: judge them again
  (`FalseEnding-Any%` 346.74 dies at goal 86, `buy:buywj` unaffordable). The model:
  [`docs/fulltas.md`](docs/fulltas.md#clones-on-build-25640326).

## Before calling a new best real

Trace it (`IGTAP.EngineSim path <trace> <route.json>`), check where the timer starts and stops, look at the jumps, and
say what the new trick is. `python tools/audit-traces.py` checks captured traces for input-buffer violations. How the
gates time a course (the start on the second tick of the hurtbox's touch, the finish on the first, the position writes
between ticks): [`docs/course-timer.md`](docs/course-timer.md).
