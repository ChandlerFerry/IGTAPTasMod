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
for ~0.05 s of real time while `Movement.Update` keeps running every frame. A dash pressed with no sideways stick goes
the way the player faces and takes the first sideways stick an `Update` reads before the next tick
(`WaitingForDashInput`, Movement.cs:817-834). A dash tick's `turnX` (`MacroStep.dashTurnX`) is that stick: the dash
turns to it, as a player tilting the stick during the freeze turns it. It is legal (README "### Rules"): the one input
that changes between two ticks. Nothing else reads the stick in that window. The recorder writes it when a recorded
dash's first dash frame goes the other way from its start (`InputRecorder.DashFrame`). It stays its own field and not
part of `lockMoveX` (the stick through the dash lock): 28 dash steps in saved routes press dash with no sideways stick
and hold `lockMoveX` sideways, and reading the lock stick in the freeze would turn the ones whose lock points against
the dash.

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
`JumpCutBufferTicks` 15 (game 0.26 s after a release = 13 ticks), each +2 slack. `tools/audit-traces.py` checks
captured traces against the same windows.

The game reads two jump actions (`Jump`, `Jump2`) and two dash actions (`Dash`, `Dash2`), and only a press or a jump
release reaches it (`Movement.Update`); holding is never read. A jump press while one jump button is down is the other
going down; with both down, one is released and pressed again within the tick, where the press clears the release's jump
cut before the tick reads it. A release lifts both. Dash presses on consecutive ticks alternate the two dash buttons. The
overlay's JUMP and DASH columns and the pad under the input list show each tick this way (J1/J2 and D1/D2: bright on the
tick a button goes down, dim while held), with "release" and "cut" while a release's jump cut is armed.

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

  How many lifts one tick gets depends on the frame rate and the body's interpolation. In the game at 1x
  (`probe.py --fps`, from a `lab/` savestate clinging at y −88): 1, 2, 3, 4 and 6 lifts at 50, 100, 150, 200 and 280
  fps with interpolation off; with it on, as the game ships it, 0, 0, 1, 1 and 2. The Course 1 demo chain at 280 fps
  with interpolation on climbs 112, 56, 56, 56 and its wallbounce reaches the roof (494), so a player can do it. The
  TAS keeps interpolation on and plays back on a fixed 280 fps clock; turbo and the offline engine run the same frames
  (`MacroEngine.FrameGrid`, README "### Rules"), step-ups ungated, so a turbo-judged route plays back the same at 1x.
  The overlay labels the chain and its wallbounce INWALL WALLBOUNCE
  (`Tech.InwallWallBounce`: a step-up whose forward rays, `wallJumpFacing`, looked away from the dot's facing, then a
  wall kick within 6 ticks).

### Omnidirectional dash (Vman)

A Vman run has three movement sets: omnidash with one dash (`buy:buyomni`), plus wall jump (`buy:buywj`), plus a
second dash (`buy:buyds`). The omnidash aims the dash with the stick, 8 ways (`Movement.getPlayerControlledInput`: y
±1 with `|x| < 1` drops x). Each trick's jump is pressed inside the dash (`dashJump`), buffered 0.2 s. Numbers are
from an offline lab on a flat Ground floor from a 460 u/s run, over 40 ticks (0.8 s); a run covers 377 u and a
ground dash 623 u, and a full jump peaks 258 u high.

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
level is always drawn. **Gates, checkpoints, teleporters, refills** marks start and end gates, checkpoints, teleporters
and dash, jump and full refill orbs in every zone. The
course and Full-TAS maps draw all of this the same way (`ultra-level.tsx`, shared with the public viewer's `level.tsx`).

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

While a route plays, the in-game input list marks the ticks of each trick with a colored bar (`TasOverlayTechs.cs`):
the superbounce, reversed super and backwards dash-jump, the wall clip, coyote and wall-coyote jumps, super jumps,
hypers, chained hypers, ground bounces and hyper ground bounces, wall bounces, spring dashes, spike grace
(overlay on), the ultra (a hyper that gives back the pre-dash momentum, "Did an ultra!") and the float ultra (a chained
hyper that passes that check only because a rebase moved the dot since the first hyper), each read from the game's
state as it happens.
Each bar carries the name of the tech with that detector in the studio's Tech tab (`TechCatalog.cs`).
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
