# Matching human input

Every route must be playable on a real controller (`README.md` "### Rules"). This is the mechanics behind that rule:
what the game reads from a human, what a human cannot produce, and how playback, turbo and the offline engine produce
exactly what a human would. The route gate checks it (see "The gate" below), so a route that matches human input is not
a convention here, it is enforced.

## The stick

A real controller gives `x` 0, ±1 or a short-of-full deflection (0.3..0.8 in size), and `y` 0 or ±1. Anything else is
not human-playable: 0.1 creeps where a stick gives 0, 0.9 is 90 % where a stick gives full speed. Playback sets
`MoveAxis` after the game read the controller (`Movement.NormaliseInput` applies to it the same way it would to a real
stick), and `MacroEngine.ControllerAxis` clamps to the values a controller can give. `y` down is fast fall
(`Movement.cs:1104`) and a faster wall slide, up a slower one (`ApplyWallslideFriction`).

Unity samples the controller once per frame, so one stick holds for a whole physics tick. The game reads it in
`Movement.FixedUpdate` before the input hook (`NormaliseInput`: fast fall, wall-slide speed), and a run's stick reaches
that read as `MacroEngine.TickStick`: the vertical of the input tick due next, `x` at rest. The hook then sets the
tick's whole stick; nothing between the read and the hook uses `x`, and only the read's `y` is ever used. On a tick in a
dash or spring lock (no hook), the read is all there is: it is the stick the last input tick held for the lock
(`MacroStep.lockMoveX/lockMoveY`; unset: `x` at rest, `y` of the next input tick). Its `x` is a super jump's direction
(`Movement.CheckForSuperJump`), so a dash one way with the stick held the other way and a jump on a dash frame is a
reversed super jump.

The dash freeze is the one place a player moves the stick between two physics ticks: a dash's `TimeStop` holds physics
for ~0.05 s of real time while `Movement.Update` keeps running every frame. A dash pressed with no sideways stick (with
the omnidash: with the stick at rest) goes the way the player faces and takes the stick every `Update` reads before the
next tick, the last one winning (`WaitingForDashInput`, Movement.cs:817-834). A dash tick's `turnX`/`turnY`
(`MacroStep.dashTurnX`/`dashTurnY`) is that stick: the dash turns to it, as a player tilting the stick during the freeze
turns it. It is legal (README "### Rules"): the one input that changes between two ticks. Nothing else reads the stick
in that window. Without the omnidash `Update` drops the vertical, so only `turnX` counts. The recorder writes them when
a recorded dash's first dash frame goes another way than its start (`InputRecorder.DashFrame`). They stay their own
fields and not part of `lockMoveX` (the stick through the dash lock): 28 dash steps in saved routes press dash with no
sideways stick and hold `lockMoveX` sideways, and reading the lock stick in the freeze would turn the ones whose lock
points against the dash. The overlay's input list gives it its own row under the dash's tick, `freeze` in the dash's
colour (`→ 35% ↑ 94%`: 0.35 sideways and 0.94 up in the freeze, after pressing dash with the tick's stick); a tech
demo also lists the tick that lets the stick go (`·`), so its list reads press, turn, release. The freeze's frames only exist while playback runs
on the fixed clock, so a replay starts only once it does (`TasPlugin.PacePlayback`): a replay that started inside a
long frame (a loaded CPU) played its first ticks back to back, with no `Update` between them, and lost the turn.

## Presses, releases and the buffer timers

A press or release does exactly what `Movement.Update` does for a real one, including the buffer expiry timers: a jump
press stays buffered 0.12 s (0.2 s mid-dash), a dash press 0.12 s, a release's jump cut 0.26 s. Nothing ever writes
`jumpBuffer`, `dashBuffer` or `jumpCutBuffer` directly; the buffers only ever hold what a press or release put there.

The game takes presses only in `Movement.Update` (Movement.cs:741-767), and Unity never runs `Update` inside a
`FixedUpdate`: a player's press is in place before the whole physics tick. So a route's presses go in at the tick start
(`MacroEngine.BeforeTick`, before `Movement.FixedUpdate`), not at the input hook partway through it, where the game has
already read the buttons (a wall bounce, a dash's wall jump: Movement.cs:1033-1056). A dash that hits a wall ends in
that tick's wall branch and the tick takes its input tick after all; its presses go in at the branch's first call
(`MacroEngine.WallTouch`, `touchedSolidGround`/`touchedMossyGround` prefix), before the branch reads the buttons. A dash
whose frames run out ends in that tick's `FixedUpdate`, before its input hook; a tick in a dash or spring lock takes no
input of its own, and the next input tick's presses wait for the first tick out of the lock.

Every press and release is stamped with `MacroEngine.Clock`, which advances once per physics tick (dash ticks
included), so each buffer can be checked against the press or release that made it. The gate's windows
(`Solver.cs`): `JumpBufferTicks` 12 (game 0.12 s; 10 ticks mid-dash), `DashBufferTicks` 8 (game 0.12 s = 6 ticks),
`JumpCutBufferTicks` 15 (game 0.26 s after a release = 13 ticks), each +2 slack. The game itself keeps a buffered dash
alive through a wall jump's first 5 lock ticks (`getPlayerControlledInput`, Movement.cs 1596: while
`wallJumpMovementLockRemaining >= wallJumpMovementLockFrames - 5` it re-arms `cancelDashBuffer` at 0.06 s each tick), so
a press's dash buffer may outlive its 0.12 s; the gate lets a buffer that was a press's live `DashHoldTicks` 6 ticks
past the last tick the game held it (0.06 s = 3 ticks, +1 for the next tick's hold, +2 slack). `tools/audit-traces.py`
checks captured traces against the same windows.

The game reads two jump actions (`Jump`, `Jump2`) and two dash actions (`Dash`, `Dash2`), and only a press or a jump
release reaches it (`Movement.Update`); holding is never read. A jump press while one jump button is down is the other
going down; with both down, one is released and pressed again within the tick, where the press clears the release's jump
cut before the tick reads it. A release lifts both. Dash presses on consecutive ticks alternate the two dash buttons. The
overlay's JUMP and DASH columns and the pad under the input list show each tick this way (J1/J2 and D1/D2: bright on the
tick a button goes down, dim while held), with "release" and "cut" while a release's jump cut is armed.

With no route playing (manual play, a recording included), the list shows the player's own ticks under a `MANUAL`
title: the stick the tick read (`MoveAxis`) and each press or release `Movement.Update` took since the tick before
(read from the same four actions, outside the pause menu), J1/J2 and D1/D2 as the buttons actually pressed. Idle ticks
count but aren't listed, as in a tech demo. The count starts at the first tick with an input (a playback's list stays
until then) and again from tick 1 after every respawn. Tech bars are playback-only.

A respawn doesn't clear the list, in manual play or a route's playback: a line splits the ticks before it from the ticks
after, yellow for a quick restart (`respawn(true)`), red for a death's respawn. A route's ticks keep counting across it.

## Ticks that take no input

Dash frames and spring ticks run no input hook (`Movement.FixedUpdate`'s spring branch skips
`getPlayerControlledInput`), so they take no stick and no press of their own. They still cost physics ticks: bonuses,
transitions and purchases are timed in physics ticks with dash and spring ticks counted.

- **Dash frames.** A jump on a dash frame is the only way to jump mid-dash (a late super jump). A human makes it by
  pressing jump during the dash: the press is filed on the dash's sample as `dashJumpFrame`, the
  `Movement.framesSinceLastDash` frame it was pressed in (0 = none). It shows up in no input sample of its own;
  playback presses it again at the start of that dash frame (`MacroEngine.BeforeTick`) via `MacroStep.dashJumpFrame`
  (1 = the first dash frame), with the stick the input tick held for the lock.
- **Spring ticks.** A sideways spring's lock spans several ticks with no input hook — yet the spring branch cancels the
  lock when a dash is buffered, so a player presses dash whenever they like during it. `MacroStep.springDashFrame`
  (1 = the first tick of the lock) presses dash on that spring tick, as a player's press in the frame before it does;
  the dash starts on the next tick. Only the input tick right before the spring ticks can ask for their dash (a
  spring-dash tick is a step of its own: `ToSteps` never merges the next tick into it), and a dash pressed during the
  lock is a mutation the offline solver makes too.

`MacroEngine.RunTicks` counts input ticks only; physics tick counts (dash and spring ticks included) are the ones
routes are timed in.

## Quick restart

The quick restart is the QuickReset button, nothing more (`Movement.Update` on a QuickReset press: `respawn(true)`).
A route makes it at the start of a step's first tick, before `Movement.FixedUpdate`
(`MacroStep.rawQuickRestart`) — where `TasPlugin.BeforeMovementFixedUpdate` does it and where enginesim does it.

A real button is released in between presses, so a quick restart is never held on two ticks in a row. The gate refuses
a route that does, and the solver's mutations keep to the same rule (also not on a window's first input right after
the prefix's own restart).

`respawn(true)` mid-dash skips `endDash`, so `dashCooldown` would stay at the dash's 100 s lock; playback instead
starts dash-ready (the -1 floor a wait reaches) with the dash's flags cleared.

## Jump holds and cuts

A normal jump or double jump reaches full height only while held: held until it rises slower than 150 (~27 ticks). A
release earlier cuts it (to 50, up to 13 ticks later), and the cut is the release's jump-cut buffer (0.26 s). A
stacked double jump climbs higher the later it is pressed. The solver's holds reach past full height and its mutations
move a press or a dash a few ticks while keeping where the hold ends.

## Recording what a human did

- Every dash frame (`Movement.CheckForSuperJump` prefix, before the game reads `jumpBuffer`): a jump press there is
  filed on the dash's sample as `dashJumpFrame` — the frame `MacroEngine.BeforeTick` presses it again in playback.
- `wallCoyoteDir` identifies the wall; a wall jump must steer away from it. Raw input is often still held toward the
  wall on the exact trigger tick, so using `moveX` reverses the recorded route.
- Keep the boundary sample: a second press can be the wall jump itself, and consuming it produces a route that waits
  for the wrong wall.
- After a played transition the player would coast into the next course; instead it brakes like a player, holding
  against its momentum until it is spent.

## The tricks, as a player makes them

- **Superbounce** (`Solver.cs` `SuperBounce`): dash into a wall and press jump 0-5 ticks later, jump buffered in the
  first dash frames; optionally steer away after. A jump pressed by the tick the dash hits the wall is the wall
  branch's dash wall jump (Movement.cs:1051-1054, a 5-tick lock); only a press after it gets the input hook's (1712-1714,
  a 3-tick lock).
- **Reversed super jump**: dash one way holding the stick the other way through the dash (`lockMoveX`), and jump on a
  dash frame while grounded or in coyote time: the super jump's momentum goes the way the stick points.
- **Backwards dash-jump** (wall jump + dash): standing against a wall, turn away from it and press dash and jump on the
  same tick. The dash start moves the player 20 units against the dash
  (`Movement.getPlayerControlledInput`), back into the wall, and the buffered jump is a grounded dash jump from inside
  it. The wall side is the stick before the turn; the turn is 0-4 ticks of stick away, and the dash tick holds the
  stick away, or neutral after a turn long enough to flip facing (~3 ticks from a push) so the dash follows facing.
- **Wall coyote jump** (lab `wall-coyote-window`): let go of a wall (slide off its bottom, or drift away) and press jump
  on any of the 8 ticks after the last tick the wall rays hit. It always kicks away from that wall: while `OnWall` holds,
  the animator block sets `localScale.x` to `wallCoyoteDir` every tick, whatever the stick says.
- **Stored wall jump** (lab `stored-walljump`): land straight from a cling (slide down onto a floor) and the
  `wallCoyoteFrames` count stays; walk anywhere, jump, then press jump again 2-7 ticks after the ground jump: a wall jump
  off no wall. It kicks against `localScale.x`, the way the dot faces: running forward it kicks backward, so to kick
  forward hold the stick back until `Velocity.x` changes sign (~3 ticks from a full run) before the press. With a super
  instead of the ground jump and the press 2-4 ticks after the dash, it is the dash boost (the stored superbounce, lab
  `stored-superbounce`).
- **GC Wall Clip** (game controller; Course 1 NONE): rising with your back to a wall, flick the stick lightly toward it
  and back. The player's box is offset 5 units to the front (`defaultColliderOffset`/`dashColliderOffset` x 5, width
  41), so the facing flip swings its back edge 10 units into the wall; depenetration pushes it only part way out, and
  the next tick the front ground ray (x−25, Movement.cs:926-940) starts inside the wall and grounds the player mid-air
  while vertical speed is under 200. A jump buffered then is a full ground jump. It needs no unlock. The turn must start
  with the box's back edge within ~8 units of the wall (after the push, the ray must still be 0.5 inside); the light
  flick back keeps the player within that, so the 1.64 route clips the same wall twice.
- **KB Wall Clip** (keyboard): the same clip with full tilt. 1-3 ticks toward the wall, with jump pressed up to 0.12 s
  before or on the turn tick or the tick after, clip the same way (Course 1 NONE: any tick from 43 to 59). A full-tilt
  turn back ends ~0.6 units further out than the light flick (8.3 from the wall where the 1.64 route's second clip
  needs at most 8.0), so a second clip on the same wall needs another way back in.
- **Inwall wallbounce** (turned-dash step climb; `WJ_DS` and up, Course 1's hanging wall in issue #10): slide down a
  wall holding down, dash with the stick straight down (no sideways stick: from a full cling the dash flips away from the
  wall, Movement.cs:1635-1642), tilt the stick toward the wall during the dash freeze (`turnX`), and press jump. The
  turned dash reaches the wall facing it while `wallJumpFacing`, set at the top of that FixedUpdate, still points away.
  So `Movement.Update`'s step-up (806-816) finds its forward rays clear and its down ray starting inside the wall (a hit
  at fraction 0 at its own origin), and lifts the dot to that hit + 36: 56 units up (the down ray starts 20 up while
  dashing). The game does so in every `Update` until the next FixedUpdate turns the rays around, each from the new
  height, and stops on the wall's top (the ray's hit is then the top face). The jump next tick is the dash-into-wall
  wallbounce (`SpecialWallJump(0.6, …)`, 1093-1131 u/s) from wherever the climb stopped. Every dash from the cling
  flips away and turns back for another climb (dash, turned tick, two cling ticks), so a chain of them climbs the wall;
  the speed the dot fell at doesn't change it (the dash replaces `Velocity` and zeroes `momentum`).

  The cling only sets up the facing: any turned dash that reaches a wall on its turned tick climbs. From the air, with
  the back to a wall and no cling, a dash with no sideways stick turned toward the wall climbs the same way
  (`enginesim/labs/c1-air-turn-climb.json`, Course 1's wall face at x 3488, facing away, y −390..−200, 120 starts a
  distance): with the hitbox's back edge 14.5-32.5 u from the face every start lifts 56 u on the turned tick, 6.5-12.5 u
  112 u, 2.5-4.5 u 112-225 u, then clings with the dash back. From 34.5 u out the turned dash reaches the wall a tick
  late, with the rays already turned, and doesn't climb, nor does a dash pressed toward the wall from any of those
  starts. A wall jump away leaves the dot too far out: its lock holds the dash until the back edge is ~88 u from the wall.

  How many lifts one tick gets depends on the frame rate and the body's interpolation. In the game at 1x
  (`probe.py --fps`, from a `lab/` savestate clinging at y −88): 1, 2, 3, 4 and 6 lifts at 50, 100, 150, 200 and 280
  fps with interpolation off; with it on, as the game ships it, 0, 0, 1, 1 and 2. The Course 1 demo chain at 280 fps
  with interpolation on climbs 112, 56, 56, 56 and its wallbounce reaches the roof (494), so a player can do it. The
  TAS keeps interpolation on and plays back on a fixed 280 fps clock; turbo and the offline engine run the same frames
  (`MacroEngine.FrameGrid`, README "### Rules"), step-ups ungated, so a turbo-judged route plays back the same at 1x.
  The overlay labels the chain and its wallbounce INWALL WALLBOUNCE
  (`Tech.InwallWallBounce`: a step-up whose forward rays, `wallJumpFacing`, looked away from the dot's facing, then a
  wall kick within 6 ticks).
- **Pauselift** (`WJ_DS` and up, Full-TAS run `Test-Pauselift`, tech demo `pauselift`): an inwall wallbounce with the
  pause menu open. Pausing (`pauseMenuScript.menuButtonPressed`) sets `timeScale` 0, so no FixedUpdate runs, but the
  step-up (Movement.cs 801-816) sits outside `Update`'s pause check and keeps running every rendered frame. Paused in the
  frames after the turned dash's tick (the dash's second physics tick), every paused frame lifts the dot 56 units until
  its down ray hits the wall's top face: one dash climbs the whole wall, whatever the frame rate. One frame earlier (the
  tick before, or the dash freeze) the turn hasn't happened and the pause lifts nothing. On Course 3's `3_WJ_DS 3.78`
  wall it reaches the top (8557) on the first dash; on Course 4's left column (face x 19936) 40 paused frames lift the
  dot 35 times, 11111.6 to the slab top at 13037. A shorter pause stops the climb partway: 26 frames leave it at 12847.6,
  under the slab. A route step's `pauseFrames` / `pauseTick` / `pauseFrame` (`MacroStep`, `SolverTick` and offline
  `InputTick` `pause` / `pauseTick` / `pauseFrame`) opens the menu after that step's `pauseTick`-th physics tick and
  `pauseFrame` of that tick's Update frames (0: at once), and closes it after `pauseFrames` frames. 1x opens the real
  menu; turbo (`TurboRunner.Pause`) and the offline engine (`Simulation.InterpolatedFrames`) run the same frames
  (`MacroEngine.FrameRepeats`): opened at once, time holds on the tick's first frame, whose blend is the step's start plus
  0.089 of the step (a freeze on the same tick overlaps it; the longer counts); opened after frame k, it holds frame k's
  blend for `pauseFrames` more Updates. `tools/tech-check.py pauselift` matches turbo and offline, and offline climbs to
  13037.0 as the 1x probe does.

  The pause lifts only if that first-frame blend already has the step-up's down ray inside the wall, so the dash must
  start pressed flush against the face with the hitbox's short side (15.5 u; the long side is 25.5), facing away: the
  first dash tick then moves 13.4 u away and the ray (29 u ahead) reaches 0.1 u into the face. One unit off and nothing
  lifts. A super's momentum toward the wall holds the player there (the demo; Course 4's right column from the zip mover
  at x 21951–22401: lab start x 21967.5–21967.6 climbs 13043.9 → 15725, the column's top, 21968.0 doesn't). A turned dash
  from a cling or from the air starts 13 u too far out and lifts only on the tick's last frames (56–112 u), as unpaused:
  paused on one of those frames (`pauseFrame` 5 at 280 fps) it climbs the whole wall too, so a plain wall cling is enough.
  `Test-Pauselift-TrueEnd-Runup` does it on the face at x 28960 from the cling at (28934.5, 17762): 17762 → 23789 over the
  spikes (21932–23686), then the dash carries onto the block top.
- **Final-cling float** (`WJ` and up, tech demo `final-cling-float` on its lab map): a cling on a mossy wall with no
  wall jumps left is the final cling (`outOfWallJumpsCling`: `isFinalWallClingActive` on for 0.7 s, `Velocity.y` 0, no
  gravity), and moss refills nothing. A refill of the wall jumps before the flag clears (a Ground-tagged wall or floor)
  leaves it on, so the next jump rises at its launch speed with no gravity until 0.7 s after the cling. By hand on the lab
  map (a chimney: moss at x 80-120, metal at 172-212, 52 u apart; placed in the air against the moss with 0 wall jumps):
  1. Let go of the stick: the dot clings to the moss and hangs there (it doesn't slide).
  2. Within 15 ticks (0.3 s) of the cling, press toward the metal wall. One tick is enough; holding longer is fine. The
     turn puts the metal under the front ground ray, which grounds the dot in mid-air (as a GC wall clip does) and
     refills the wall jumps.
  3. Press jump on the turn's tick or up to 8 ticks after it (0.16 s), and hold it. Not before the turn: a press on the
     moss is used up there (no wall jumps, no double jump) and the dot then hangs on the metal without jumping.
  4. Let go of the stick. The dot rises at 805 u/s until 0.7 s after the cling (35 ticks), then arcs normally: about 850
     at the finish box (1096 at the peak) against 556 for the same inputs with wall jumps left. Held sideways it floats
     just as high but drifts out of the box.
  The float lasts 35 ticks from the cling, and everything before the jump comes out of it: turning at once and jumping
  with the turn is the highest. Keyboard is enough (all full-tilt presses). A quick restart doesn't reset the wall jumps,
  so place the lab start again for every try. Offline gap check (same inputs): 50-52 u grounds the dot in mid-air (a
  ground jump, 805), 54-60 u clings it to the metal instead (a wall jump, 730, same float), 70 u misses. In a level it
  needs a mossy wall reached with all 4 wall jumps spent off moss (no Ground-tagged touch in between) and a Ground-tagged
  wall or floor in reach before the flag clears. The overlay labels the jump FINAL-CLING FLOAT (`Tech.FinalClingFloat`:
  a jump with the flag still on). While the lab is injected, its trainer panel (`LabTrainerFloat.cs`) follows each attempt
  as the pauselift's does: these steps as chips (green done, red failed and why) and a timeline in ticks from the cling.

### Omnidirectional dash (Vman)

A Vman run has three movement sets: omnidash with one dash (`buy:buyomni`), plus wall jump (`buy:buywj`), plus a
second dash (`buy:buyds`). The omnidash aims the dash with the stick, 8 ways (`Movement.getPlayerControlledInput`: y
±1 with `|x| < 1` drops x; the freeze aim below is the one way past that). Each trick's jump is pressed inside the dash
(`dashJump`), buffered 0.2 s. Numbers are from an offline lab on a flat Ground floor from a 460 u/s run, over 40 ticks
(0.8 s); a run covers 377 u and a ground dash 623 u, and a full jump peaks 258 u high.

Terms, as the Tech tab locks them in: a **super** is a flat dash on the ground then a jump, a **high super** the same
with an up-diagonal dash, a **hyper** a down-diagonal dash then a jump, an **ultra** a hyper 4 units lower (the game's own
bonus), and a **ground bounce** a straight-down dash into the ground then a jump.

- **Hyper** (also called wavedash): hop, dash down-diagonal from the air, and press jump in the dash. The landing turns
  the dash horizontal with 3 more dash ticks (`canHyper`), and the jump, which the game takes no sooner than 4 dash ticks
  after the landing, is the hyper (`CheckForSuperJump`): it leaves the ground with 547 up and 1650 sideways momentum,
  but only with the stick held sideways at that jump (`MoveAxis.x`; a route holds it with the dash tick's `lockX`, else
  the hyper gets no momentum at all). It covers 1310 u, and on a Ground floor the hyper refills the dashes, so one dash
  is enough.
- **Ultra**: a hyper whose jump is at least 4 u below the last hyper's height (`lastHyperY`), with the dash going the
  way the dot already moved: the game adds back the momentum from before the dash (`preDashMomentum.x`).
- **Chained hyper**: two hypers chained. The second dash starts in the air from the first one's jump. It covers 1400 u
  in 40 ticks and 1957 u in 60 (a run covers 561 u); it is an ultra when the second jumps 4 u lower.
- **Float Ultra**: a chained hyper whose drop comes from `FloatingOrigin`. The game keeps the player within 17500 of
  scene (0, 0), the sphere's centre; past the edge it moves the world back by the player's scene position, so the
  player's scene y drops by its height above the sphere's centre line, while `lastHyperY` (a scene number) stays.
  The rule: **cross the edge ABOVE the centre line** between two chained hypers (no ground stay in between) and the
  second hyper lands that far under `lastHyperY`: a float ultra (the game adds back the pre-dash momentum, 1609 +
  1131 = 2740 in the demo). Crossing BELOW the line raises the player over `lastHyperY`: none. Each move re-centres
  the sphere on where the player crossed, so the next float ultra is 17,500 u up and to the right of it: from the
  Vman start that is the platform above and left of Course 4 (floor y 11625), from the platform the top of the map
  (the floor at y 26057 above Course 4's spike tunnel).

  The Tech tab's Float Ultra demo loads a fresh VMAN game, fast-forwards the run's best to its quick restart on that
  platform in turbo (`fastForward` 635 input ticks: the world as the run left it, which a savestate can't hold), then
  at 1x: walk, hyper, hyper (float ultra; the sphere moves to the platform), down to the left end of Course 1's floor
  (crossed below the line: the sphere moves there, no float ultra), and back up to the platform for a second float
  ultra. Both routes were found with `fulltas`; turbo and the offline engine rebase as 1x does (`enginesim/README.md`),
  so the demo's turbo trace replays offline with the float ultra at the same tick (`tools/tech-check.py`). While it
  plays, the overlay (`TasOverlayOrigin.cs`) draws the sphere's edge and centre line green above / red below, tags the
  player ABOVE/BELOW the line with its height, shows the distance to the edge, the step it is on (ARMED after an
  above-line crossing, FLOAT ULTRA when it lands) and a minimap of every sphere the playback was in, where each was
  crossed and the player's path. A tech with `floatingOrigin` gets that overlay and is never cut to a savestate.
- **High super**: dash up-diagonal on the ground and press jump in the dash, on any of its first 6 dash ticks. The
  dash lifts off, but `onGround` lasts through the coyote frames, so the jump is a super (805 up, the same 900 sideways
  momentum as a flat super) from up to 129 u above the floor, and it gives the dash back: one dash is enough. The
  dash's end (`endDash`, dash tick 11) still cuts `Velocity` to 35 %, so from standing it peaks 167 u (jump on dash
  tick 1) to 232 u (tick 6), against a flat super's 240 u.
- **Ground bounce**: dash straight down onto the floor and press jump in the dash. The landing ends the dash with
  `DashBounceFrames` 4, and a jump then adds `dashBounceMagnitude`: 805 + 600 up, it peaks 556 u high.
- **Hyper ground bounce**: a ground bounce from a hyper's jump (diagonal down dash, jump, dash down and jump). A
  straight-down dash gives back 95 % of the momentum from before it when it lands (`endDash`, `dashDirection.y == -1`),
  so the bounce has both: 1390 up and ~1840 sideways (a ground bounce alone: 0). It is as high as a ground bounce and
  covers 1437 u across in 40 ticks.
- **Super ground bounce**: the same from a super's jump: 1390 up and ~870 sideways momentum, a bit less than from a
  hyper (~1740).
- **Wallbounce** (wall jump): dash up-diagonal into a wall and jump in it. The dash turns straight up at full dash speed
  (1670 u/s, not the diagonal's 1181), the wall touch refills the dash, and the jump is the wall bounce (507 u high).
- **Wall dash**: a dash from a wall cling goes away from the wall with x set to ±1 after the normalize, so a diagonal
  one moves at (1670, 1181) u/s, 2045 u/s in all. Down-diagonal onto a floor, it becomes a wavedash off the wall.
- **Freeze aim** (`WJ_DS_DJ_BS_ORB_ZIP_OM`, Vman): press dash with the stick at rest, then during the dash freeze push the stick
  nearly straight up (or down) with a slight lean, sideways 0.3-0.4 (`turnX`/`turnY`, e.g. (0.35, 0.94)). The stick
  that turns a waiting dash (`Movement.Update`, 821-833) skips the press's snap to straight up, so `dashDirection` is
  (0.33, 0.944): 19° off vertical, 552 u/s sideways and 1576 u/s up, an angle no dash pressed on a tick gets (17-22° for
  sideways 0.3-0.4; `NormaliseInput` takes a sideways 0.4-0.8 with a vertical to full tilt). The dash's first tick
  still goes the way the player faces (the −20 shove and that tick's `Velocity` come before the freeze). From standing
  on Course 1's floor it peaks 268 u up and 124 u across with the stick at rest after it (a straight-up dash: 298 u up,
  none across). Down in the air it still lands as a wavedash (`canHyper` flattens it on the floor).
  It needs an analog stick (a keyboard or d-pad has no 0.3-0.4); the timing is the dash turn's, any frame of the
  freeze with the last one counting. The overlay labels it FREEZE AIM (`Tech.FreezeAim`: a dash whose direction on its
  first dash tick is steeper than the diagonal, `0 < |x| < 0.7` with `y ≠ 0`). Tech demo `freeze-aim` plays it on the
  `freeze-aim` lab map: from a ledge under a ceiling it leans out past the ceiling's end and over a spike bed onto a
  spiked pillar's top. A keyboard can't: up in the freeze goes straight into the ceiling, up-right into the spikes, and
  none of 3,556 keyboard dash tries (every direction pressed or turned in the freeze, 0-16 ticks of walking first,
  right held after) lands; the closest (walk left 9 ticks, dash up-right) hits the spiked face 37 u below the top. The
  lab shows three of them as ghosts beside the freeze aim.

The studio's **Ultra map** tab (`POST /api/enginesim/ultras`, `IGTAP.EngineSim ultras`) shows where the game's ultra
bonus can be had in the VMAN run's world. The game's check (`Sign(preDashMomentum.x) == Sign(dashDirection.x)` and the
lifted `y < lastHyperY - 4`) compares against `lastHyperY`, which is the height of the last hyper or of the floor last
stood on (written on every grounded tick), so an ultra joins two floors: the one it comes from and a lower one where
the ultra's hyper happens. First the map finds every floor a hyper can land on: the top face of a Ground-layer
collider, triggers included (the ground rays hit upgrade boxes too), where the dash box fits and no spike is near (floors only in the zone the capture had
loaded, Zone 1; the level drawn shows every zone). Then it pairs every floor with every other floor more than 4 lower,
at most 2600 across and 2000 down, joined by a flight line no solid crosses; a floor sloping more than 4 pairs with
itself. A flight line runs between the dot's centres over the two floors, from either end or the middle of the upper
floor or just past either end (walking off it), to the same spots on the lower one (coming in beside it, under a
ledge), straight or over a hop as high as a held jump (measured in the engine, 237 u). The pair's drop is the biggest
height difference over a clear line. These are lines, not jump arcs. To check them, from launch points every 32 u along
the floors the dot can stand on, the engine plays a hop plus wavedash and a second wavedash 1–14 input ticks later, each
way, with the unlocks a VMAN route has there (dash, omnidash, wall jump, 2 air dashes). Every ultra it gets confirms
the pair it joins (from the launch floor, or the first hyper's floor for the second hyper of a chain, to the ultra's
floor), and adds the pair if the lines missed it (dashed on the map).

The trees' ground (the platforms each `TreeController` segment turns on as its tree upgrade is bought) is drawn as
dashed tan outlines, **Tree ground + purchase order**, each numbered with the fewest tree purchases that grow it: Plant
Tree is 1 and grows the first segments, each grown segment shows its own box, and that box's purchase grows the
segments it names (`upgradeBox.SegmentsToTrigger`; `autoTriggerNext` segments come along). Hovering a number names the
box that grows it and the box it carries. A fresh world has none of it grown, so it has no floors or pairs, and the
engine doesn't model growing it. Spikes are red; coloured blocks and spikes are drawn in both colours (blue and
orange, the export's `sourceShapes`), only the active colour being solid. Springs are level too: their trigger box in
yellow, with an arrow the way they launch (`transform.up`, what `SpringScript` passes to `Movement.hitSpring`). The
level is always drawn. **Game art** draws the level with the game's own tilemaps and sprites (spikes, springs, zip
movers, refill orbs, moss, vines, and the grown tree with its purchase numbers on its own **Tree (grown)** layer), from
WebP tiles in `web/public/art` that `igtap-ultra-map/tools/render-art.py` renders per world state. Decoration that
neither collides nor does anything (backdrops, pillars, pistons, plants) is its own **Background art** layer, off by
default. Every spike (overgrowth vines and the hidden spikes too) is tinted red over the art. Only the tiles in view
load, at the coarsest level that fits the zoom, and the last loaded tiles stay until new ones arrive. The collision
shapes are then a toggle. A checkpoint is its spawn box (the standing player box where a death or quick restart puts
the dot: the checkpoint's position 12 down), with its trigger area dashed; a start or end gate is its trigger box; a buy
box is its trigger box with its name and cost, and says what it is (secret, bonus reward, achievement box, ability,
tree, one-off or course upgrade), what it gives and what it costs, on hover or tap. Hovering (or tapping) a zip mover
or moving platform draws where it goes: the corridor its box sweeps, its path and its box at each stop
(`PlatformMover.Positions`, run in order and back to the first). Refill orbs are their own sprites, shown and hidden by their marker toggles; an end gate that only stops the timer (`endGate.isEndOfCourse` off: leaving the course that way ends the run without a finish) is a grey **Course exit**. On a course map the level is drawn once per view, so dragging an avoid box or hovering a zip only redraws what follows the pointer.
The **Area 1** picker switches the level between Area 1's world states, **Normal** and
**Overgrown** (`OvergrowthLoadScript`: when Area 1 is overgrown its `overgrowthEnable` objects, the moss, spikes, zip
movers and their boxes and checkpoints under and around Courses 1–5, are on and its `overgrowthDisable` ones are off;
overgrowth also hides the Area 1 courses' movement, prestige and breaker-light boxes, a bonus's reward among them, and
opens the Zone 2 path above Course 10: the omnidash box sets both). It starts on the capture's state; floors, ultras, routes and explored cells are always the
capture's. Ground that is off in the capture is filled from its outline (an off composite has no shapes, only `paths`).
**Gates, checkpoints, teleporters, refills,
secrets** marks start and end gates, checkpoints, teleporters, dash, jump and full refill orbs, and every secret (a
gold star at its box's trigger: an `upgradeBox` with `isSecret`, named by its achievement number and cost; a bonus's
finish is one) in every zone. Course labels sit at each course's start and bonus labels at the bonus's finish (its
reward box in `courses.json`), not at the course it starts from. The map spans the whole level, not only the floors of
the zones the capture had loaded. The course and Full-TAS maps draw all of this the same way (`ultra-level.tsx`,
shared with the public viewer's `level.tsx`).

With nothing selected, each floor is coloured by the biggest drop onto it. Clicking a floor draws arrows to every floor
its ultras reach (coloured by drop) and from every floor whose ultras reach it (cyan), and lists the biggest drops.
Clicking a listed pair picks out its arrow and, for an engine-confirmed pair, draws the engine ultra's moves and its
inputs. **Pairs** switches between every height drop, only engine-confirmed ones, and origin-shift ultras;
**Selected floor** shows its ultras out, in, or both. The result is cached in `enginesim\ultras-VMAN.json`;
**Recompute** reruns it (about a minute and a half on all cores). Its launches are placed anywhere, so it models
`FloatingOrigin`'s rebase itself instead of the engine's (the VMAN world's scene sets `Threshold` 17500, `Use2DDistance` off): once the
player is more than 17500 from the origin, the next physics step moves the world by the player's position but leaves
`lastHyperY` alone, and the next hyper passes the check on any floor. A pair only such an ultra makes is green. The
search only models a rebase for launches that start inside the threshold, from origin 0. The map also draws that first
rebase line (dashed green) and, for each later rebase the VMAN run's best so far makes, the line it crossed. The
**First shift at** slider moves the first rebase along its line (it starts at the run's) and draws the next line
around it (cyan).

While a route plays, and in manual play once its list starts, the in-game input list marks the ticks of each trick with a
colored bar (`TasOverlayTechs.cs`): the superbounce, reversed super and backwards dash-jump, the GC and KB wall clips (the
flick's stick below or at full tilt), coyote jumps (plain; the dash coyote jump, whose dash was still on at the last ground
tick; the checkpoint coyote jump, with no full ground tick since a respawn), wall-coyote jumps, stored wall jumps
(a wall jump off no wall from a `wallCoyoteFrames` count a landing or respawn kept: `OnWall` off, the count above 0), super jumps,
high supers (the dash going up), hypers, chained hypers, ground bounces and hyper ground bounces, wall bounces, spring dashes,
spike grace (overlay on), the ultra (a hyper that gives back the pre-dash momentum, "Did an ultra!") and the float ultra (a
chained hyper that passes that check only because a rebase moved the dot since the first hyper), the superbounce double jump
and the boosted double jump (a double jump at `Velocity.y` 200 or more, which only the 700 gate after a jump that wasn't a
normal one lets through), the pauselift (an inwall climb with the pause menu open, once per pause) and the zip mover
intangibility (`PlatformMover.MakeIntangible`), each read from the game's state as it happens.
Each bar carries the name of the tech with that detector in the studio's Tech tab (`TechCatalog.cs`). The Tech tab's live
catalog (the config folder's `techs.json`) keeps its own edits; a plugin build adds the techs and detectors its built-in
list has that the live one lacks, once each (`techs.seeded.json` remembers them, so a deletion in the tab sticks).
Manual play's techs show on the overlay only: the tab's demos are cut from a route's playback.
The first time a playback does a tech that has no demo route yet, the tab saves one (`tech/<name>` in the route
library): while a route plays, the overlay keeps a player savestate for each of the last 256 input ticks, and the demo
gets its own copy of the latest one at least 25 ticks before the tech starts, taken on a tick with no dash or cutscene
running, as a savestate of the same name that is attached to it. Its inputs run from that tick to 25 ticks after the
tech. Playing an older demo that has no savestate gives it one the same way; removing a demo or its tech deletes its
savestate. A savestate holds only the player (position, velocity, buffers), so a demo cut from a Full-TAS run plays in
whatever world is loaded, with the unlocks you have now. Starting from a savestate also takes the dot off every course
(no course tracking, `courseResetPoint` zero), so a quick restart goes to the last checkpoint, not to the start of a
course an earlier run left timing. A course demo still sets the course's zone and its
category's unlocks. A demo with no savestate (nothing clean in the window, or **Use selected route** on a route
without one) starts from its route's start.

A tech is **confirmed** only while it has a demo route: the Tech tab lists techs with one under Confirmed and the rest
under Unconfirmed (reported, not reproduced). A reproduction found offline becomes a demo with `POST /api/techs/demo
{id, from: "ticks", start: "1_WJ_DS_DJ", ticks, savestateId?}`: raw input ticks with that category's unlocks, from its
quick restart or from a savestate (a `lab/` one from `/api/savestates/place` puts the dot right where the tech starts).
With `start: "fulltas/VMAN_NONE"` and no savestate the ticks play from a new game of that Full-TAS run.

## The gate

`SaveSolverRoute` (the only place a category route is saved) replays the route from the course start and refuses it
unless every tick obeys what a human with that category's unlocks can do. The checks read the game's own state, so
they hold however inputs are injected: every buffer the game holds must come from a press or release `MacroEngine`
made within the game's window, counted in physics ticks (`MacroEngine.Clock`). Unlocks must match the category exactly
on every tick (`wallJumpUnlocked`, `dashUnlocked`/`maxAirDashes`, `doubleJumpUnlocked`/`maxAirJumps`); a Full-TAS run
is checked against the unlocks its own purchases gave so far. A Full-TAS route is replayed the same way from the new
game's start with every tick audited.
