# How the game times a course

What starts, runs and stops a course's timer, which box the gates see, and the position writes that move the player
between physics ticks. Read from the decompiled game (`courseScript`, `startGate`, `endGate`, `Movement`, build
25640326) and measured in the offline engine, which runs that code (`IGTAP.EngineSim probe`, whose tick rows carry
`courseTime`). Rules on what a route may do are README `### Rules`; this is how the game behaves.

## The timer

- `courseScript.FixedUpdate` adds `0.02f` to `currentPathTime` every physics tick while `tracking` is on. Nothing reads
  a clock or a frame: the time is a tick count, 50 a second, and no part of a tick is ever timed.
- The display rounds to the hundredth (`ToString("0.00")`). The game's Mono (`mscorlib` 4.6.57) formats a float to 7
  significant digits first and then rounds at 2 decimals, so the float sum shows exactly ticks × 0.02 up to tick 5083
  (101.66 s): tick 5070's 101.394997 shows 101.40. The first tick that shows 0.01 low is 5084 (101.674950 shows 101.67);
  measured by running the game's own `mono-2.0-bdwgc.dll` over ticks 1–30000. 500 ticks sum to 10.000039, 3000 to
  60.001183.
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
| Body (`normalCollider`, walls and floors) | 41×72; 41×54 at (5, 0) while dashing or after a jump | (5, 2) | Default, Spikes (and Ground for 0.01 s after a wall jump) | Ground | no |
| Hurtbox | 40×54, never changed | (5, 0) | Ground | Default, Spikes | **yes** |

- The offset follows the facing (the scale's sign): turning around moves the hurtbox 10 u.
- A touch is any overlap, not containment: each Box2D shape carries a skin of the contact offset (0.01 u, the project's
  `defaultContactOffset`), so boxes about 0.02 u apart already touch.
- The engine applies the same filter (`PhysicsWorld.ContactMask`, `CanTouch`).
- Across 423 traces (401,063 tick states) the hurtbox was always 40×54 at (5, 0), excluding Ground, enabled; no script
  touches it, and zip intangibility (`PlatformMover.MakeIntangible`) re-layers the mover, not the player. The only way
  past a gate is not to overlap it at any step's end or start: going around, or, for the 20 u-wide exit gates, more than
  ~60 u in one tick (the 60 u start gates need ~100 u, the game's cap per step).

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

| When | Write | Interpolation pull-back |
|---|---|---|
| Jump | +5 up | yes |
| Dash jump (super, hyper) | +2 up, the tick after the dash start (`CheckForSuperJump`) | yes |
| Wall jump | +1 sideways, +1 up | yes |
| Dash start | −20 along the dash direction | yes |
| Foot-ray pop | +10 up when a foot ray's origin is 0–5 u above Ground (the body sunk 11–16 u); no saved route shows one | yes |
| Spring hit (physics callback) | +10 along the spring's up, exactly | **no**, and the next tick's write gets none either |
| Step-up (`Update`, a rendered frame) | onto the ledge's top (3–46 u up, 56 dashing), x at the frame's blend | loses the rest of that tick's move: (1 − blend) × the move |
| Respawn | to the reset point or checkpoint, then −12 down | |

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

At dash speed (33.4 u a tick) the same lag is 0.6–5.4 u. A spring hit, a physics callback, lands after the step has set
the transform to the body, so it adds exactly its 10 u (1x probes of the four spring techs: residual 10.000 at lags
0.36–2.50 ms; engine: 19.200 = 9.2 + 10 at all five lags). A step-up's raise is first seen by a gate on the next tick's
step (a start-of-step touch there counts).

Choosing the lag (1–4 idle ticks before the run-up) is not a record lever: of 11 routes that still finish, every
converged one polished back to its own ticks from every shifted start, and most shifts broke the route outright. What
did gain was letting polish move a route's first input (2_WJ_DS_DJ_BS_ORB_ZIP_OM 1.86 → 1.74): see the tools below.

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
- The stick is normalised before any of this (`Movement.NormaliseInput`): x becomes ±1 when |x| > 0.8, or when
  |x| > 0.4 with |y| > 0.3, and 0 below 0.3.
- The dash reads the game's normalised stick (`NormaliseInput`), snapped to eight directions. Measured with `OM`: (0.5,
  0.7), (0.7, 0.7) and (0.9, 0.5) dash diagonally, (0.3, 1) straight up, (1, 0.3) and (0.5, 0.3) sideways.
- Without `OM` the dash's vertical part is zeroed (a diagonal stick dashes sideways); on the ground its down part is.
- A dash in full wall coyote flips the facing and dashes away from the wall: the 20 u go toward it.
- An up dash is 20 u down at the step's start (plus the lag's pull down if rising), then about 13 u up net over the
  tick: an end gate under the hurtbox finishes on that tick (the start-of-step touch above). Confirmed in turbo on Course
  1's `RealEnd` (`1_WJ_DS_BS_ZIP_OM`): a 13.4 u gap finished on the dash tick (1.42 against 1.44 falling); a 19.15 u gap
  finished (1.40); a 20.0 u gap missed, the pull-back of a falling player taking it out of reach. It gains a tick over a
  plain fall only when the gap is above ~12.5 u (a fall would touch anyway) and below ~18–19.8 u. It starts no start
  gate, which needs two ticks.

## Respawns

No respawn calls `stopTracking`, but every one while timing lands in an exit gate that does. Measured offline on
Courses 1–15 (11–15 identical to 1–5); the game's code paths read from the decompile:

- `courseResetPoint` is the start `Checkpoint` (written by the start gate's Stay) while the hurtbox is in the start gate,
  else the course's own `End (2)` (written by `courseScript.FixedUpdate` every timed tick): a 20×400 `endGate` with
  `isEndOfCourse` off. Both spots overlap `End (2)` on every course.
- **Quick restart** (`respawn(true)`, which ignores the checkpoint setting): lands there, and `End (2)`'s Enter on the
  restart tick stops the timer without a finish (Course 1: x 2243.04 from the gate, 2227.33 after it).
- **Death**: 30 frozen ticks, the timer running through them (Course 1: 0.20 at the death, 0.80 on the last frozen
  tick), and control again on the 31st, when the respawn lands as above (checkpoints off) or at the last checkpoint
  touched (on). The course's start `Checkpoint` is the only one within 540 u of each start gate and overlaps `End (2)`
  too, so a death warp keeps the timer only if it lands on a checkpoint that touches no end gate (measured: a respawn
  point forced past Course 1's gate kept counting). A checkpoint further into a course, touched during the untimed
  run-up, is unmeasured.
- TAS routes can't use a death: the plugin and the engine abort any judged run on a death. Saved records ran with the
  studio's **Checkpoint respawns** off (`settings.json` and every trace header that carries it).

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
  start starts it a tick later. Confirmed in turbo: Course 1 (dynamic gate), walking in, first overlap tick 23, the
  timer from 24, from 25 with a dash on 24; Course 2 (no body), the timer from 25 whatever the dash tick. A recovery when
  standing or landing in the gate, not a gain over entering fast.
- **Passing untimed:** one tick of overlap (or a start-of-step touch) starts nothing; crossing a 50–60 u gate whole in
  one tick takes ~90–100 u, so go around or clip a corner, reach the far side untimed (end gates do nothing yet) and
  brush the gate for two ticks at the last moment: the late gate touch (Course 1 NONE 1.62 → 1.40).
- **Brush the near edge:** any overlap counts, so touch the edge or corner nearest the route's way on.
- **Finish:** the end gate counts the first tick of overlap, a start-of-step write included: an up dash, a jump's +5 or a
  dash away from a gate behind can catch it a tick sooner when it is within the write's reach.

Saved with these, 2026-10-01, each confirmed in turbo before the plugin judged it: `1_WJ_DS_BS_ZIP_OM` 1.42 → 0.78
(the `1_WJ_DS` 0.80 route with an up dash on its last fall, 12.54 u above `RealEnd`: the finish comes on the dash tick),
`1_WJ_DS` 1.42 → 0.80, `1_WJ_DS2_BS_ZIP_OM` 1.42 → 0.66, `1_WJ` 1.42 → 1.40 (the `1_NONE` route, never judged in `WJ`
before), and `1_WJ_DS_DJ` and `1_WJ_DS_DJ_BS_ORB_ZIP_OM` 0.86 from the `WJ_DS` route.

## In the tools

- `IGTAP.EngineSim probe`: every tick row carries `courseTime` (null while no course is timing).
- The near misses (`bus`, `CourseAuto.MeasureBus`, the Optimize tab's **Near misses**): hurtbox-to-gate gaps after each
  step. Start gate: its Enter tick, the tick before the timer; a shallow overlap there (`at`) means a hair less and the
  timer starts a tick later. Finish: a small gap the tick before (`before`) means a hair further finishes a tick sooner.
  A shallow `at` is a real chance only if the hurtbox can still overlap the gate on the tick after the timer's start
  (`after` ≤ 0; the Optimize tab says "no tick to gain" otherwise). `1_NONE` 1.40's 0.036 u and `2_WJ` 2.22's 1.14 u are
  false ones: a facing flip there (a ceiling-corner pass, a wall jump) takes the hurtbox out of the gate on that tick, so
  both routes already start the timer on the last tick they can (Improves around each gate, ~70,000 probe branches on
  `1_NONE` and a hill-climb on `2_WJ` found nothing).
- The late gate touch: `ContactProbe.NearGate` (the gate within 16 u of the hurtbox while untimed), the tech's grafts
  (taps of 2–6 ticks at 0.5–1) and the planner's gate tap (2 or 4 ticks at full tilt), which start the timer where a
  one-tick tap from a stand doesn't.
- `polish` (course routes) also moves a route's front: `Lead ±d` adds or cuts leading idle ticks, and after the seed's
  own polish it polishes again from the seed with 1–`--lead-ticks` (default 4) idle ticks in front (`--no-lead` turns it
  off). A route that moves from tick 0 used to keep its first input fixed.
- `solve --auto` writes its starting best (a library seed already faster than the saved record) to offline-best for the
  plugin to judge; before, only strict improvements over it were written, so a faster seed was never judged.

## Bonuses and transitions

They have no game timer: their end is the plugin's own rect (`courses.json` `endX/endY/endW/endH`, a trace header's
`finish`), tested at the start of every `Movement.FixedUpdate` (`TasPlugin.BeforeMovementFixedUpdate`) against the
`Collider2D.bounds` of **both** player boxes, so tick k's end pose finishes at k+1's prefix with physics ticks × 0.02. A
transition into a course also ends when that course's `startTracking` runs. `bounds` reads the body: after a step-up in
a tick's last frame the transform stands on the ledge while the body hasn't moved yet, and the box is reached a tick
later than the transform suggests (turbo-confirmed; the engine tests the body's pose there too, `Simulation.BoundsPose`;
the Full-TAS `at:` goals use the same test). docs/fidelity-log.md, "Area goals and end boxes were tested at a pending
step-up's pose".
