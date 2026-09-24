# IGTAP TAS: route rules (non-negotiable)

A route is only valid if a human with the category's unlocks could produce the same run with a controller.
Every time a solver found a "huge improvement" so far, it was breaking one of these. Treat a big jump in time as a
suspected exploit of our own tooling until proven otherwise.

1. **Right course.** The run starts the course's timer at its start gate and stops it at that course's end gate.
   Finishing another course never counts (`BeforeCourseFinish`, `Simulation.TargetCourse`).
2. **Right unlocks.** Wall jump / dash are exactly the category's (`NO_WJ`: neither, `WJ`: wall jump, `WJDASH`: both)
   on every tick, in turbo, offline, and in playback.
3. **Inputs behave like real presses.** Injected jump/dash presses and releases must do exactly what `Movement.Update`
   does, including the buffer expiry timers (jump 0.12 s, 0.2 s mid-dash; dash 0.12 s; jump cut 0.26 s). Never set
   game input fields (`jumpBuffer`, `dashBuffer`, `jumpCutBuffer`, ...) without the game's own timers.
4. **The gate is the only way in.** Category routes (`<course>_<NO_WJ|WJ|WJDASH> <time>`) are saved only by
   `SaveSolverRoute`, which replays the route from the course start and refuses it if any tick breaks 1–3. Don't add
   another save path, and don't loosen the gate to make a route pass.
5. **"DO NOT MODIFY CAPABILITIES"** (studio toggle) means the plugin never writes an unlock; tools that need to,
   refuse.

Before calling a new best real: trace it (`IGTAP.EngineSim path <trace> <route.json>`), check where the timer starts
and stops, look at the jumps, and say what the new trick is. `python tools/audit-traces.py` checks captured traces
against rule 3.
