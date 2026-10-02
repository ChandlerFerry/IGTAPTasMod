# How the game times a course

What starts, runs and stops a course's timer, which box the gates see, and the position writes that move the player
between physics ticks. Read from the decompiled game (`courseScript`, `startGate`, `endGate`, `Movement`, build
25640326) and measured in the offline engine, which runs that code (`IGTAP.EngineSim probe`, whose tick rows carry
`courseTime`). Rules on what a route may do are README `### Rules`; this is how the game behaves.

## The timer

- `courseScript.FixedUpdate` adds `0.02f` to `currentPathTime` every physics tick while `tracking` is on. Nothing reads
  a clock or a frame: the time is a tick count, 50 a second, and no part of a tick is ever timed.
- The display rounds to the nearest hundredth (`ToString("0.00")`). The float sum stays within half a hundredth of
  ticks × 0.02 up to tick 5069 (101.38 s; 500 ticks sum to 10.000039, 3000 to 60.001183), so every course time shown is
  exactly ticks × 0.02. Past that the drift can show 0.01 low (101.394997 for 101.40); Unity's own float formatting there
  is unchecked.
- A best is replaced only by a strictly lower raw float (`currentPathTime < bestPathTime`): the same tick count sums to the
  same float, so a tie is never a new best.
- `startTracking` and `stopTracking` are the only writers of `tracking`. Their only callers are the two gate scripts.

## Start gate

- `startGate.OnTriggerStay2D` (Player tag) calls `startTracking`, which zeroes the timer, and sets the quick restart's
  `courseResetPoint`. It acts only while the course isn't timing: going back through the gate mid-run restarts nothing.
- It runs on Stay only. The first tick of a touch is the Enter and starts nothing, so **the timer starts on the second
  physics tick in a row the hurtbox overlaps the gate**, and counts from the tick after that. A one-tick touch starts
  nothing.
- Measured (walking right into Course 1's gate): the hurtbox first overlaps after tick 17's move, the timer reads 0.00
  after tick 18 and 0.02 after tick 19.
- Everything before the start, the run-up from the quick restart, is untimed: free on the record, counted on the farm.

## End gate

- `endGate.OnTriggerEnter2D` calls `stopTracking(collision.gameObject, isEndOfCourse)`, with no tag check. It acts only
  while timing, so an end gate passed before the start does nothing.
- It runs on Enter: **the timer stops on the first physics tick the hurtbox overlaps the gate**.
- `isEndOfCourse` on (`RealEnd`): the finish (reward, best time, clone path). Off (`End (n)`, the studio map's grey
  "Course exit"): the timer stops with no finish.

## Which box, and what counts as a touch

The Player carries two boxes; every start and end gate is a trigger on layer 0 (Default):

| Box | Size | Offset | Excludes | Includes | Touches gates |
|---|---|---|---|---|---|
| Body (`normalCollider`, walls and floors) | 41×72; 41×54 at (5, 0) while dashing or after a jump | (5, 2) | Default, Spikes | Ground | no |
| Hurtbox | 40×54 | (5, 0) | Ground | Default, Spikes | **yes** |

- The offset follows the facing (the scale's sign): turning around moves the hurtbox 10 u.
- A touch is any overlap, not containment: each Box2D shape carries a skin of the contact offset (0.01 u, the project's
  `defaultContactOffset`), so boxes about 0.02 u apart already touch.
- The engine applies the same filter (`PhysicsWorld.ContactMask`, `CanTouch`).

## When a tick sees the gates

- A trigger touch starts on the tick whose move first brings the hurtbox into the gate (Enter), and goes on (Stay) while
  it stays in.
- Position writes before a step count too. A write that puts the hurtbox in a gate only at the step's start (the step
  moves it back out) enters and exits in that step: an end gate finishes there.
- A write that takes the hurtbox out before a step, with the step moving it back in, ends the touch and begins a new one
  (Enter again) when the gate sits on a non-static body. The start gates of Courses 1 and 3–10 sit on dynamic bodies
  (`Start`); Course 2's has none and its touch never lapses (the world of `route-c1-none-1.40`). Courses 11–15 use
  Courses 1–5's gates.
- Rendered frames never check gates. A frame matters only through the writes below.

## Position writes and the interpolation lag

`Movement` moves the player by writing `transform.position` directly:

| When | Write |
|---|---|
| Jump | +5 up (a second jump path: +2 up) |
| Wall jump | +1 sideways, +1 up |
| Dash start | −20 along the dash direction |
| Spring hit | +10 along the spring's up |
| Step-up (`Update`, every frame) | onto the step's top |
| Respawn | to the reset point or checkpoint, then −12 down |

The player body interpolates, so between ticks the transform holds the last rendered frame's position, which trails the
body. `transform.position += d` in `Movement.FixedUpdate` (the jumps, wall jumps and dash starts) writes that drawn
position plus `d`, so such a write also pulls the player back along the last tick's movement by

pull-back = distance moved over the last tick × lag / 20 ms,

the lag being how far the last frame trails the tick on the fixed 280 fps clock: 0.36, 2.50, 1.07, 3.21, 1.79 ms in
turn, set by the physics ticks since the quick restart (`MacroEngine.FrameGrid`). Measured: walking right at 9.2 u a tick
and jumping on ticks 10–14 against a branch that kept walking:

| Jump on tick | Lag | Predicted | Measured |
|---|---|---|---|
| 11 | 0.36 ms | 0.17 u | 0.16 u |
| 13 | 1.07 ms | 0.49 u | 0.49 u |
| 10 | 1.79 ms | 0.82 u | 0.82 u |
| 12 | 2.50 ms | 1.15 u | 1.15 u |
| 14 | 3.21 ms | 1.48 u | 1.48 u |

At dash speed (33.4 u a tick) the same lag is 0.6–5.4 u. Picking the lag: 1–4 idle ticks before the run-up rotate it for
every later tick.

## The dash start

A dash start writes `dashDirection * -20` before that tick's move, then sets the dash velocity. Measured from a walk to
the right (9.2 u a tick, the 2.50 ms lag: 1.15 u back to the left), positions after the dash tick's move of 33.4 u:

| Dash | Before the move | After the tick |
|---|---|---|
| Neutral, facing right | 20 left + 1.15 left | x 2406.29 |
| Right | 20 left + 1.15 left | x 2406.29 |
| Left | 20 right − 1.15 | x 2379.49 |
| Up (`OM`) | 20 down, 1.15 left | y −295.58 (−308.99 − 20 + 33.4) |
| Up-right (`OM`) | 14.1 left + 14.1 down, 1.15 left | x 2402.37, y −299.51 |

- A neutral dash goes the way the player faces, so its 20 u go behind; without `OM`, a stick held only up or down is
  neutral. A turn in the frames after the press redirects the velocity, not the 20 u already written.
- The dash reads the game's normalised stick (`NormaliseInput`), snapped to eight directions. Measured with `OM`: (0.5,
  0.7), (0.7, 0.7) and (0.9, 0.5) dash diagonally, (0.3, 1) straight up, (1, 0.3) and (0.5, 0.3) sideways.
- Without `OM` the dash's vertical part is zeroed (a diagonal stick dashes sideways); on the ground its down part is.
- A dash in full wall coyote flips the facing and dashes away from the wall: the 20 u go toward it.
- An up dash is 20 u down at the step's start (plus the lag's pull down if rising), then about 13 u up net over the
  tick: an end gate up to ~20 u under the hurtbox finishes on that tick (the start-of-step touch above). It starts no
  start gate, which needs two ticks.

## Respawns

- No respawn calls `stopTracking`.
- A quick restart (`respawn(true)`) while timing goes to the course's reset point. On Course 1 (engine) the timer was off
  from the restart's tick, the player at x 2241: the game's own triggers there, presumably an exit gate (not confirmed).
  Other courses unmeasured.
- A death with checkpoint respawns on goes to the last checkpoint touched, and nothing in the respawn code stops the
  timer: a checkpoint touched during the untimed run-up, then a death after the start, warps there with the timer
  running. Read from the code, not run; a death also costs its respawn delay.

## Using it on a record

Course 1's start gate, walking right into it, a dash on the tick shown, the timer when the player passes x 2700:

| Dash on tick | First overlap | Timer starts | Timer at x 2700 |
|---|---|---|---|
| 14 | 15 | 16 | 0.14 |
| 15 | 16 | 17 | **0.12** |
| 16 | 16 | 17 | 0.14 |
| 17 | 17 | 18 | 0.14 |
| 18 | 17 | **19** (lapsed) | 0.14 |
| 19 | 17 | 18 | 0.16 |
| 20 | 17 | 18 | 0.18 |

- **A fast free tick:** the Enter tick is untimed, so arrive at top speed: a dash that enters at 33 u a tick beats walking
  in and dashing (0.12 against 0.14).
- **Re-arming:** a write out of a dynamic-body gate before the step re-enters it: a dash on the tick the timer would
  start started it a tick later (tick 18). A recovery when standing or landing in the gate, not a gain over entering fast.
- **Passing untimed:** one tick of overlap (or a start-of-step touch) starts nothing; crossing a 50–60 u gate whole in
  one tick takes ~90–100 u, so go around or clip a corner, reach the far side untimed (end gates do nothing yet) and
  brush the gate for two ticks at the last moment: the late gate touch (Course 1 NONE 1.62 → 1.40).
- **Brush the near edge:** any overlap counts, so touch the edge or corner nearest the route's way on.
- **Finish:** the end gate counts the first tick of overlap, a start-of-step write included: an up dash, a jump's +5 or a
  dash away from a gate behind can catch it a tick sooner when it is within the write's reach.

## In the tools

- `IGTAP.EngineSim probe`: every tick row carries `courseTime` (null while no course is timing).
- The near misses (`bus`, `CourseAuto.MeasureBus`, the Optimize tab's **Near misses**): hurtbox-to-gate gaps after each
  step. Start gate: its Enter tick, the tick before the timer; a shallow overlap there (`at`) means a hair less and the
  timer starts a tick later. Finish: a small gap the tick before (`before`) means a hair further finishes a tick sooner.
- The late gate touch: `ContactProbe.NearGate` (the gate within 16 u of the hurtbox while untimed), the tech's grafts
  (taps of 2–6 ticks at 0.5–1) and the planner's gate tap (2 or 4 ticks at full tilt), which start the timer where a
  one-tick tap from a stand doesn't.
