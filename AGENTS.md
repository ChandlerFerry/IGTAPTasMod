# IGTAP TAS: route rules (non-negotiable)

A route is only valid if a human with the category's unlocks could produce the same run with a controller.
Every time a solver found a "huge improvement" so far, it was breaking one of these. Treat a big jump in time as a
suspected exploit of our own tooling until proven otherwise.

1. **Right course.** The run starts the course's timer at its start gate and stops it at that course's end gate.
   Finishing another course never counts (`BeforeCourseFinish`, `Simulation.TargetCourse`). A flying start is allowed:
   the time counts from the last start-gate crossing, so a route may loop back and re-enter the start gate at speed
   (user decision 2026-09-24).
2. **Right unlocks.** Wall jump / dash / air jump are exactly the category's (`NO_WJ`: none, `WJ`: wall jump,
   `WJDASH`: wall jump + dash, `WJ_DASH_AJ`: wall jump + dash + air jump) on every tick, in turbo, offline, and in
   playback.
3. **Inputs behave like real presses.** Injected jump/dash presses and releases must do exactly what `Movement.Update`
   does, including the buffer expiry timers (jump 0.12 s, 0.2 s mid-dash; dash 0.12 s; jump cut 0.26 s). Never set
   game input fields (`jumpBuffer`, `dashBuffer`, `jumpCutBuffer`, ...) without the game's own timers.
4. **The gate is the only way in.** Category routes (`<course>_<NO_WJ|WJ|WJDASH|WJ_DASH_AJ> <time>`) are saved only by
   `SaveSolverRoute`, which replays the route from the course start and refuses it if any tick breaks 1–3. Don't add
   another save path, and don't loosen the gate to make a route pass. A chain segment's routes
   (`<chain>/<segment>_<CAPS> <time>`, `chains.json`) go through the same gate, replayed from where the chain's routes
   before it end; a segment starts exactly there (no teleport), and a chain goes back only through its `restart` (the
   game's own quick restart, `Movement.respawn(true)`, nothing else).
5. **Never edit the game's save files** (`%USERPROFILE%/AppData/LocalLow/Pepper tango games/IGTAPfullGame/Savedata`).
   Change the running game live instead: `POST /api/session/setup {"abilities": {"dashUnlocked": true, "maxAirDashes": 1,
   "airDashesLeft": 1}}` sets Movement fields on the live player and replies with them. `/api/oracle/bootstrap` loads
   the scene without going through the menu, and the game autosaves, so treat relaunches as able to change the save.
6. **"DO NOT MODIFY CAPABILITIES"** (studio toggle) means the plugin never writes an unlock; tools that need to,
   refuse.
7. **Search offline; the game only confirms.** Every search runs in `enginesim` (`IGTAP.EngineSim solve`), never
   in the game. The game is started for exactly three things: capturing a trace/world the offline engine doesn't
   have yet (`tools/enginesim-capture.py`: a course/category never captured, a seed route, or a fidelity check after
   the game or plugin changed), submitting an offline best that beats the saved route (`/api/enginesim/submit`,
   which `solve` does by itself), and `tools/validate.py`. No game-judged search loops, no looping over
   `/api/enginesim/submit` as an evaluator, no in-game solver, no pressing the studio's Optimize tab
   (`/api/solver/start`, it launches `solve` for the user), no extra game instances. If the offline engine can't do
   something a search needs, fix `enginesim`.

Current targets: `bonus1` (`WJ`), Course 4 and Course 5 (`WJ_DASH_AJ`). How: [`enginesim/README.md`](enginesim/README.md#use).

Before calling a new best real: trace it (`IGTAP.EngineSim path <trace> <route.json>`), check where the timer starts
and stops, look at the jumps, and say what the new trick is. `python tools/audit-traces.py` checks captured traces
against rule 3.
