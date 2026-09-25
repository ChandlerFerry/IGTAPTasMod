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

## Presses, releases and the buffer timers

A press or release does exactly what `Movement.Update` does for a real one, including the buffer expiry timers: a jump
press stays buffered 0.12 s (0.2 s mid-dash), a dash press 0.12 s, a release's jump cut 0.26 s. Nothing ever writes
`jumpBuffer`, `dashBuffer` or `jumpCutBuffer` directly; the buffers only ever hold what a press or release put there.

The game takes presses only in `Movement.Update` (Movement.cs:741-767), and Unity never runs `Update` inside a
`FixedUpdate`: a player's press is in place before the whole physics tick. So a route's presses go in at the tick start
(`MacroEngine.BeforeTick`, before `Movement.FixedUpdate`), not at the input hook partway through it, where the game has
already read the buttons (a wall bounce, a dash's wall jump: Movement.cs:1033-1056). A dash that hits a wall ends in
that tick's wall branch and the tick takes its input tick after all; its presses go in at the branch's first call
(`MacroEngine.WallTouch`, `touchedSolidGround`/`touchedMossyGround` prefix), before the branch reads the buttons.

Every press and release is stamped with `MacroEngine.Clock`, which advances once per physics tick (dash ticks
included), so each buffer can be checked against the press or release that made it. The gate's windows
(`Solver.cs`): `JumpBufferTicks` 12 (game 0.12 s; 10 ticks mid-dash), `DashBufferTicks` 8 (game 0.12 s = 6 ticks),
`JumpCutBufferTicks` 15 (game 0.26 s after a release = 13 ticks), each +2 slack. `tools/audit-traces.py` checks
captured traces against the same windows.

A new press needs the button up on the tick before it: two presses with no release in between is not a human input.

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

A normal jump or air jump reaches full height only while held: held until it rises slower than 150 (~27 ticks). A
release earlier cuts it (to 50, up to 13 ticks later), and the cut is the release's jump-cut buffer (0.26 s). A
stacked air jump climbs higher the later it is pressed. The solver's holds reach past full height and its mutations
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

## The gate

`SaveSolverRoute` (the only place a category route is saved) replays the route from the course start and refuses it
unless every tick obeys what a human with that category's unlocks can do. The checks read the game's own state, so
they hold however inputs are injected: every buffer the game holds must come from a press or release `MacroEngine`
made within the game's window, counted in physics ticks (`MacroEngine.Clock`). Unlocks must match the category exactly
on every tick (`wallJumpUnlocked`, `dashUnlocked`/`maxAirDashes`, `doubleJumpUnlocked`/`maxAirJumps`); a Full-TAS run
is checked against the unlocks its own purchases gave so far. A Full-TAS route is replayed the same way from the new
game's start with every tick audited.
