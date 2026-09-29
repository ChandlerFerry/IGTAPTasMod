# Full-TAS

The whole game from a fresh new game as one continuous input stream. A run is a list of goals, game events that must
happen in order; its time is the physics ticks from the new game's first settled tick to the last goal. Goals split
the run for display and for the offline search; nothing restarts, teleports or re-times between them. A quick restart
(`Movement.respawn(true)`, the game's QuickReset) is an input the route chooses (`reset` on a tick), so a route can
restart or run on, whichever is faster. The new game is loaded by `POST /api/session/newgame`: the menu's New Game with
no save file, except that `Saveloader` neither loads nor saves for the rest of the session. A loaded new game stays fresh
(runs, traces and judgings reuse its start) only while `FloatingOrigin` is where it was when the start was taken: real-time
play far from it rebases the world, and the start's player position would then lie elsewhere, so the next run loads a new
game.

While a new game loads and settles, no keyboard or pad input reaches the player (the stick reads zero and the jump and
dash buffers are cleared each tick), and the start is refused when the player stands elsewhere than it spawned (more than
0.5 units sideways, or facing the other way: `The player moved before the start was taken ...`). A start taken after input
walked the player is another start: `FalseEnding2-Any%`'s stood 95.6 units left of the spawn, facing left, and its best
replayed to goal 1 only.

The run file (`fulltas/<run>.json`) records the start it was solved from (`startAt`: world x, y, facing). A solve whose
trace starts more than 0.5 units from it, or facing the other way, stops before touching it (`... was solved from ...;
the run file is left as it is`). Capture the start again from a fresh new game, or pass `--fresh` to solve from the new
start anyway. A solve also stops when the run file's best, replayed, reaches fewer of its unchanged goals than the file
records (`... recorded N goals, but replayed ... it reaches M`): the engine plays it differently (another build) or the
start differs, and solving on would save the shorter replay over the best. That happened twice: the moved start rebuilt
the whole run from goal 1, about 200 s slower, and a build without the buy-max rule below replayed `FalseEnding2-Any%`'s
409.34 s best to goal 282 of 300 and wrote that back. Rebuild the engine, or pass `--repair` (the studio's solve body
`repair: true`) when the engine change is meant and the run should be re-found from where the replay stops.

## fulltas.json

In the game's `BepInEx/config/IGTAPTasMod/` (edits are picked up within a second, never mid-run):

```json
[{ "name": "main", "goals": ["start:1", "finish:1", "buy:buyreward", "start:1", "finish:1", "buy:buywj", "start:2"] }]
```

| Goal | Reached when |
|---|---|
| `start:N` | Course N's start gate starts its timer (it wasn't timing). |
| `finish:N` | The player's own finish at course N's end gate. |
| `buy:buyreward+buyclone` | The game bought each of these boxes (any order). Course 1: `buyreward`, `buyclone`, `buywj`, `buyomni` (the Vman speedrun's free omnidirectional dash + 1 dash, there only in Vman with `VmanStartsWithOmniDash`); Course 2: `buyreward2`, `buyclone2`, `buyclonemult2`, `buyglobalreward2`, `buyds`; Course 3: `buyreward3`, `buyclone3`, `buyclonemult3`, `buyfast3`, `buyglobalreward3`, `buydj`, `buyfree3`, `buyclonespeed`, `buystatue`; Course 4: `buyreward4`, `buyclone4`, `buyclonemult4`, `buybig4`, `buyfast4`, `buymaxsize4`, `buyglobalreward4`, `buyswap`, `buyfree4a`, `buyfree4b`, `buyfree4c`, `buyprestige4`; Course 5: `buyreward5`, `buyclone5`, `buyclonemult5`, `buyglobalreward5`, `buybig5`, `buyfast5`, `buyprestige5` (trips the breaker), `buyblockswap5` (swap coloured blocks when dashing; the box sits on Course 4's row and appears only after the breaker trips), `buyfree5` (secret #2). Area 2: Course 6 `buyreward6`, `buygreen6`, `buyclone6`, `buyfree6a`–`buyfree6e` (the free global cash boxes); Course 7 `buyreward7`, `buyclone7`, `buyclonemult7`, `buygreen7`; Course 8 `buyclonecd8`, `buyclonegp8`, `buycd8` (clone dust on), `buycddigits8`; Course 9 `buyreward9`, `buyclone9`, `buygreen9`, `buycd9`, `buyatomlevel9`, `buyclonemult9`; Course 10 `buyreward10`, `buyfree10a`, `buyfree10b`, `buyclone10`, `buyclonemult10`, `buywatts10`, `buynp10`, `buyatomlevel10`, `buygreen10`; the atom box `buyatom`; the upgrade tree `buyplant` and `buytree<N>` (TreePart N's box: `buytree16` activates the zip movers; no `buytree23`, the OmniDash toggle); Tree 2's `buyplant2`. The table is `FullTasGoal.Boxes` (`FullTas.cs`), shared with enginesim. |
| `reward:bonusN` | The game buys bonus N's reward box (the upgrade box nearest its end rect: the bonus's unlock). |
| `at:x,y,w,h` | An area goal: the player's colliders touch the rect centred on (x, y), w by h, in game-world coordinates (`courses.json`'s frame, the studio map's readout; see [`enginesim/README.md`](../enginesim/README.md)) (checked after each tick, as a bonus's end rect is). A search aid: it splits a long stretch into windows, and dropping it lets the search find another way. Not the game's checkpoints (`checkpointScript`), which set where a death or quick restart off a course respawns. |
| `at:x,y,w,h,from=left+bottom` | The same area goal, entered from one of these sides (`left`, `right`, `bottom`, `top`): on the tick the colliders first touch the rect, after the tick before they were wholly outside it on that side. Touching it any other way doesn't count; the player must leave and come in again. |

A quick restart right after an area goal (the last goal reached is an `at:`) takes that goal back: the run has to
pass it again before the next goal counts. An area goal marks the way the run goes on, so a window can't touch it and
restart to hand the next window a standing start. Quick restarts after any other goal (a purchase, a finish) are
untouched. A strict run's finish, due 12 ticks after its last goal, is called off when such a restart takes that goal
back: it used to fire anyway, and every window that touched an area goal and restarted counted as reaching it, then
missed it replayed from the start (`a window route missed its goals ...`). That message now says where the window's
replays part (from the stored state, after a straight replay, played through), each on a thread of its own. Stored goal
states carry each type's field layout: a state of another build is dropped and replayed, not misread.

An event that isn't the next goal changes nothing (buying another box, finishing another course). Unlocks come only from
the game's purchases; the plugin writes none.

Run names are letters, digits, `%` and `-` (`ANY%`, `Test-No-Omni-Skip`).

`buy:buyprestige5` is Course 5's prestige box with `tripBreaker` set: after `BoostOldCourses` it resets Courses 1–5,
sets Cash to 0, swaps Course 4's `buyswap` box for `buyblockswap5`, turns the Course 1–3 movement boxes into
breaker-light boxes and turns Area 1's lights off. The studio's goal list marks that goal red and every goal after it
dark (`/api/state` `fullTas.runs[].breaker`: the goal's index).

## Vman speedrun runs

`"start": "vman"` on a run (`{ "name": "VMAN", "start": "vman", "goals": [...] }`; `/api/fulltas/goals` takes `start`
too) starts it in a Vman speedrun's world instead of a new game. `POST /api/session/newgame {"vman": true}` loads it
(every Vman run, trace and judge does it on its own):

1. a fresh new game, as above (the Vman statics a fresh process has: not in a Vman speedrun, score 0);
2. Area 1 overgrown the way Courses 11–15 set it, `vmanTime` set to every duration box at its cap (Σ `Cap` ×
   `effectMult`: 5174 s) and `VmanStartsWithOmniDash` to its box's (1): the one setup the plugin writes, standing in
   for the save that bought them;
3. the player on the floor under the 1x "Start a VMAN speedrun" box (`a1 overgrowth/StartVmanSpeedrun0`) jumps; the
   game itself buys the box (`SetupVmanSpeedrun(1)`) and loads the Overworld as a Vman world (no save loaded,
   `vmanTimeLeft` 5175 s, costs not deducted, the free omnidash box on Course 1's row);
4. the Vman world's player spawns in the air; once it has stood still on the ground (grounded, no x velocity, no
   cutscene) for 10 physics ticks, that tick is the run's start, seeded like a new game. A new game's start waits for
   the same rest. Capturing on a frame count instead caught the player mid-fall on a frame-rate-dependent tick, so each
   load had a different start and a route found from one trace's start died in the gate's.

The run's time is still physics ticks from that start. The snapshot carries VmanScript's clock and score (turbo steps
`VmanScript.FixedUpdate` like Unity does; a run whose clock runs out ends there). A course's first clear pays its
`completionReward` (Course 1 1000 W, Course 2 4e6 W) and nothing else: no clone path, no comp boost. The unlock audit
follows the run's purchases box by box, so `buyomni` (dash, omnidash, +1 air dash) and a later `buyds` (+1) are what
a VMAN route may have.

Each search window (`IGTAP.EngineSim fulltas`) presses what the run's movement allows there: a dash once the window
starts with one or buys one, and from `buyomni` on the omnidirectional tricks too (`OfflineSolver` `Omni`: aimed dashes,
the freeze aim, hyper, chained hyper, ground bounce, hyper ground bounce, the up-diagonal dash into a wall; see [`human-input.md`](human-input.md#omnidirectional-dash-vman)).

`VMAN` is the first such run: `buy:buyomni`, Course 1, `buy:buywj`, the climb to Course 2 (`at:2400,1500,400,1000`),
Course 2, `buy:buyds`, then area goals inside Course 2's box (`at:4300,2850,400,700`), the left end of the wall at
6080,4000 (`at:6105,4146,250,328`), the shaft up to Course 3 (`at:7645,6050,230,1500`), up the left of Course 3's box
(`at:8320,8100,1040,1600`), the column above it to the 10000 floor (`at:9225,9585,950,1030`), a 300 by 300 box at
9139,10326 (`at:9139,10326,300,300`), up past the 11000 roof (`at:12500,11950,1000,500`), the platform left of Course 4
(`at:16400,12050,2200,1000`), Course 4's start
(`at:19250,12060,1500,1880`), a 100 by 1000 column at 20200.4,13553.8 (`at:20200.4,13553.8,100,1000`), up the left
wall past a 220 by 200 box at 20489.1,15030 (`at:20489.1,15030,220,200`), out of the tunnel floor's top-left corner
with both dashes (`at:20495,15785,130,30,from=bottom`, see below), up into the spike tunnel above Course 4
(`at:20940,15925,480,350`),
out of the tunnel's right end (`at:22200,15900,500,500`) and
into the credits trigger over the teleporters (`at:24067,13456,1440,1080`: `FakeCredits`'s box, whose
`FakeCreditsControlScript::OnTriggerEnter2D` starts the end-game credits cutscene and stops the player). The Vman
credits end by crashing the game on purpose (`Utils.ForceCrash`, after a PlayerPrefs write that adds 10 Vman score at
the next `VmanScript.Start`); the plugin drops both calls, so judging this goal no longer kills the game.

The corner is where the run gets its dashes back for the tunnel. The tunnel floor block's top-left corner at
20416,15689 has spikes on its left face (up to y 15686) and along its top (from x 20419), so the only ground is the
3-unit gap between them, and the ground rays find it only in a few units of landing x. The run arrives with no dash
left, and landing there dies on the top spikes unless it leaves within spike grace; a jump (16 per tick) is too slow.
What works is a dash pressed up to 4 ticks before the landing (the game's dash buffer, which holds it while no dash
is left): it fires on the tick the ground rays refill the dashes, right with a jump on dash frame 3 (superjump), and
leaves the corner with both dashes, which is enough to arc straight into the tunnel and dash twice right out of it.

The goal is on that arc, not on the corner: x 20430–20560, y 15770–15800, entered from below (the hurtbox, x−15..x+25,
y−27..y+27, wholly under y 15770 the tick before). A run can be below it there only by leaving the corner, so a
landing that dies on the spikes (it falls in from above) never reaches it. The old corner goal
(`at:20422,15697,4,16,from=top`) did the opposite: every fatal landing in x 20395–20439 counted, so the best reached
goal 20 on the tick before a spike death and the search for goal 21 started from a dead player, while a living exit
that slid in from the left missed its `from=top`.

### Avoids and the map

`enginesim\Cli\forced-paths.json` rules with `"fulltas": ["<run>", ...]` list a run's `avoid` boxes (game-world
`x0,y0,x1,y1`): every search window of the run cuts a run that enters one, which keeps the search in the corridor
between area goals. The Full-TAS tab's **Show map** (`POST /api/fulltas/map {name}`) draws where the run's windows
went (`archive\fulltas-<run>.visits.json`, every solve of its current goals), the best so far, the `at:` goals and the
avoid boxes, with a world-coordinate readout. On the map, **Draw avoid** and drag a box, tick the runs it's for and
**Add avoid** (`POST /api/fulltas/avoid {box: [x0, y0, x1, y1], runs: [...]}`): it goes into the `"drawn": true` rule
for exactly those runs. **Remove** under the map (`remove: true`, `name`) takes a box out of every rule of the run, and
so out of the other runs sharing that rule. The plugin writes both `enginesim\Cli\forced-paths.json` and the solver's
copy beside its exe, and a solve rereads the boxes before every window, so a box drawn mid-solve applies from the next
window on. A hand edit of the source file needs an engine rebuild
(`dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release`) to reach the solver's copy. Course search maps draw
their rules the same way (**Draw avoid**, `POST /api/enginesim/avoid {course, capabilities, box, name?, on?, remove?}`;
a named box becomes a pickable optional block of the rule covering the map's category).

A Full-TAS rule's `optional` blocks (`name`, `note`, `avoid`, `offFor`) are avoid boxes that are on except in windows
heading for one of the goals in `offFor` (a reach or polish window whose goals include it, or a loop piece for it).
A block's `after` (a goal label of the run) makes its boxes cut a run only once the run has reached that goal (the label's
last place in the run's goals), so a window that starts below the box isn't cut before it gets there.
`TrueEnding-ANY%` has three: `bonus3-wall` (x 10305.6 to 10373.1, y 7200.9 to 7538.2, between Course 3 and bonus 3's
end) is off only for `reward:bonus3`, `statue-wall` (x 7198.2 to 7476.9, y 6900.4 to 7786.2, over the statue box) only
for `buy:buystatue`, and `above-374` cuts everything below y 17997 once goal 374 (the gap under the spiked pillar) is
reached. The map draws `offFor` blocks as always on and leaves `after` blocks out; the in-game goals overlay doesn't draw
them, and the studio's **Remove** doesn't reach them: edit `enginesim\Cli\forced-paths.json` and rebuild.

Goals chain, and a window's search follows that: its explore cells keep what the player has left in the air (wall
jumps, midair jumps, air dashes), so a faster arrival that spent its dash doesn't replace one that still has it for the
next goal, and until the window reaches its last goal, cells that have reached more of the run's goals are picked more
(`OfflineSolver` `Weight`; run time doesn't count there: a run that wanders longer isn't closer to the next goal).
While the next goal is an `at:` rect, cells nearer it are picked more among those with the same goals reached: a
cell's nearness is (nearest + 48) / (its distance + 48), the distance from the player to the rect (the guide's walkable
distance with `--guide`, `enginesim/README.md`'s guide) and the nearest any of the worker's cells came
(`GoalReach`), so avoid boxes cut the corridor's dead ends and the rect pulls the search along it.

## Savestate starts

`"savestate": "<id>"` on a run (a file in `savestates/`) starts it from that savestate: once the new game (with
`"start": "vman"`, the Vman world) has settled, the savestate is restored, and the run's start is the player's next
rest there (10 grounded, still ticks), after `FloatingOrigin` has rebased around the new position. The start's
unlocks are what the savestate carries: a savestate captures none, but its `movementFields` may list
`wallJumpUnlocked`, `dashUnlocked`, `omniDashUnlocked`, `doubleJumpUnlocked`, `blockSwapUnlocked`, `maxAirDashes` and
`maxAirJumps`, and the unlock audit starts from them (then follows the run's purchases). With "Don't touch my
unlocks" on, a restore skips those fields and a savestate start that has them fails. `POST /api/session/newgame`
takes `savestate` too. Changing a run's savestate retires its routes like a goals edit.

A savestate in the air (its `movementFields.onGround` false) can't rest: after the restore (and the rebase) the plugin
restores it again on every physics tick, holding the player where the savestate put it, and takes the start after the
same 10 ticks, exactly at the savestate. Its routes start in the air on their first input.

`Test-Pauselift-TrueEnd` starts in the air from `lab/pauselift-trueend-wall-17600` (`91d31236…`: world (28944.5, 17600)
against the face at x 28960 below the spiked wall, facing away, 1 dash and 1 double jump) and pauselifts over the
spikes (21932–23686) onto the block top at 23753 (`at:29048,23842,100,100`, 0.08 s). `Test-Pauselift-TrueEnd-Runup` is
the same lift from a real start, `lab/pauselift-trueend` (`474ef8ab…`, standing at world (30653, 16779) under the Start
Vman speedrun box, the same unlocks): it walks left to `Test-Right-Ghost`'s start (29010, 16779), plays that run's
approach (the checkpoint trigger at x 29407–29707, the cling in the slab gap above the box, the quick restart whose wall coyote jump
launches from the checkpoint: tech `checkpoint-midair-walljump`, the restart keeps the cling's `wallCoyoteFrames`
and the dash-boosted wall jump adds 410 momentum up), clings to the face at (28934.5, 17762) with its dash, and
pauselifts from the cling with the pause 5 frames into the turned tick (`pauseFrame`, [`human-input.md`](human-input.md)): 8.22 s.

`Test-No-Omni-Skip` is the first: `VMAN`'s last four goals (the tunnel floor's corner exit, the spike tunnel, its
right end, the credits trigger) from savestate `test-no-omni-skip` (`c1382aa8df724bcc92c07a3cd8c577df`), standing at
world (20933, 14987) under the tunnel with wall jump, 1 dash (no omnidash) and 1 double jump. A test of whether the
stretch goes without the omnidash; it teleports and writes unlocks, so it is not a whole-game route.

`"rebaseAt": [x, y]` (world) on a savestate run moves the world once the restored player has rested: FloatingOrigin's own
moves (roots, camera, backgrounds, particles, trails, lines, `currentOrigin`) by that point's scene position, then a physics
sync, as if the game had rebased with the player standing there. The player then rests again and the start is taken, so
the trace, the offline search and playback all start in that frame, and the run's next rebase comes 17500 from the point
(the player has to be inside that, or the start fails). `Test-Pillar-Ghost` uses it: from the GlobalCash box under the
spiked pillar, a cling lift on the pillar's base crosses 17500 from its point at the lift's top. That run showed that
the game's first FixedUpdate after a rebase already sees the moved world: the offline engine used to read the old place
for that tick (a "ghost" wall that ended the dash in the spikes), and dies there now as the game does
(`PhysicsWorld.SyncMovedWorld`).

`Test-Pauselift` (savestate `lab/pauselift`, `02db5c474fdb4e9cb3230c042ce328b5`: standing on the floating block at world
(19730.86, 10933.51), facing left, with wall jump, 1 dash and 1 double jump, on a plain new game) is the pauselift's
start ([`human-input.md`](human-input.md), tech demo `pauselift`). Its goal (`at:19880,14200,200,100`) is out of reach, so a
playback runs through all its inputs. Its routes pause; turbo and the offline engine model the pause as 1x plays it.

A savestate holds no checkpoint, and the plugin writes none: `respawnPoint` stays the new game's (the Vman spawn, world
(-61, -301)), a place the run never reached. Until a real checkpoint (`checkpointScript`) moves it, or a start gate sets
a course reset point, a quick restart would teleport there, so it ends the run (`Quick restart with no checkpoint
touched since the savestate start`, `FullTasTracker.RestartRefused`) in the offline search, the gate and playback
alike. A death ends every run anyway. Traces and routes from before this (`startSetup` 1) respawned at the savestate's
own spot, a checkpoint the game never set: `Test-No-Omni-Skip`'s best quick-restarted there on ticks 3, 85 and 96.

A savestate carries the game's `Assembly-CSharp` hash, and a game update (Steam) makes every older savestate
unrestorable (`Savestate rejected: Assembly-CSharp hash changed`): the run then never leaves the Vman start. A restore
snaps the camera onto the player (cameraMover's position, lerp and zoom), so it doesn't pan across the map.

## Run seeds

`"seeds": ["<run>", ...]` on a run hands those runs' offline bests (`enginesim/fulltas/<run>.json`) to its solve as
seed routes, once from each goal they reach: the inputs after their goal k, for their goals k+1 on that this run has
too. When the solve's best reaches one of those goals, the seed's inputs are tried from there (as the course seeds
are) and spliced in if the whole run gets better, also when that goal is touched in the dash ticks after its input tick.
Unlocks aren't checked up front: the simulation decides.

A seed from its run's start also carries that run's trace (`"trace"`): its player stood still at the trace's start
position, facing the start's way, before the first input, and replayed from anywhere else the inputs miss (a start
13 units off, or facing the other way, dies in the first spike dash). So the solve first walks there from where this
run is: it holds toward the spot for n ticks (the last one partial), waits until the player stands, taps the start's
way once when the walk faced the other, stands 10 ticks, then plays the seed. It keeps the three stands closest to the
spot (within 8 units on the same floor, facing the start's way, at most 1500 units away) as saved-route starts. A reach
window also walks there from the cells its search explored near the spot (within 1500 units across and 96 up or down,
the nearest four new cells every 15 s), since its start may be nowhere near that floor: `TrueEnding-ANY%` touches goal
370 in mid-air 1700 units below `Test-No-Omni-Skip`'s start, and its search reaches the start's floor. `TrueEnding-ANY%`
lands at world (20471.7, 14990.6) after goal 433; walking 52 ticks right to `Test-No-Omni-Skip`'s start (20933.5,
14987, against the wall, facing left) and playing its route reaches all 437 goals at 666.76 s, where no search from the
landing had reached goal 434.

`TrueEnding-ANY%` is the first: a plain new game (no `"start"`, not a Vman speedrun), its own buy order through
`buy:buyds`, `VMAN`'s goals 9–11 (to the shaft up to Course 3), Course 3's gate and `reward:bonus3` right away, Course 3
farmed until it buys `buystatue` ("Awaken the statue's power", 11.09 GW) and then `buydj` (+1 air jump), then `VMAN`'s
goals 12–19 (the way to the tunnel) and `Test-No-Omni-Skip`'s four, seeded with `Test-No-Omni-Skip`'s path and with
`TrueEnding-ANY%-v1-tail` (the 665.48 s route from its `buy:buyfast3` on: the walk to `buydj` and the way from it to the
credits, `enginesim/fulltas/TrueEnding-ANY%-v1-tail.json`). That stretch needs the double jump (the savestate has it),
so the farm comes before it.
Its Course 1 buys (20 `buyreward`, 9 `buyclone`, then `buywj`) are timed for 82-tick
laps: an ordering planned for slower laps asks for a clone before the clones have paid for it, and the run stalls at
that box. Its Course 2 goals (after `reward:bonus1` through `buy:buyds`) are planned from its own best:

```
python tools/plan-fulltas.py --run TrueEnding-ANY% --measure --write
```

`--measure` reads the run's offline best (`enginesim/fulltas/<run>.json`) and its trace (`fulltas-<run>.json`, of that
best or of an earlier best it extends, reaching `reward:bonus1`: **Capture start** first if it isn't) and takes from them the Cash at `reward:bonus1`, the ticks
from there to Course 2's start gate, Course 2's lap (start gate to finish) and restart-to-gate lead, a repeat buy's
ticks and the extra per box row crossed, and Course 2's best run (game timer), which is the clones' path. `--write`
replaces the goals from `reward:bonus1` through `buy:buyds` and keeps the run's goals after it. Rerun it after the
run's Course 2 laps or box walks get faster; a goals edit makes the saved routes stale, so solve again afterwards.

The plan's Course 2 clones follow the game: a clone spawns from the pool only once `BaseCloneInterval` (best path ÷
clone count) has passed since the last spawn, runs the path (best run + 1 tick), pays, and is back in the pool a tick
later. Replaying `TrueEnding-ANY%`'s buys through it lands within ~6 ticks of the best's buy ticks (`buy:buyds` 3539
planned against 3533 after `reward:bonus1`).

Its Course 3 (`start:3` through `buy:buydj`, after the shaft goal `at:7645,6050,230,1500`) is planned the same way:

```
python tools/plan-fulltas.py --run TrueEnding-ANY% --measure --dj-farm at:7645,6050,230,1500 --write
```

`--dj-farm GOAL` replays the run's goals from `reward:bonus1` through `buy:buyds` in the model, then plans Course 3
from its start gate: `reward:bonus3` first (170 ticks, one more global cash box), then the greedy phases main's plan
uses (`buyreward3`, `buyclone3`, `buyclonemult3`, `buyglobalreward3`, `buyfast3`, laps) to `buy:buystatue`, then to
`buy:buydj`. The statue adds 1 to the global clone multiplier and boosts Courses 1 and 2 to Course 3's tier, so their
clones pay off screen from then on. It puts the goals right after GOAL, replacing the Course 3 goals (and
`reward:bonus3`) already there; `--write` touches only those. The ticks from `buy:buyds` to Course 3's gate come from the run's best (its
`start:3` there, or GOAL plus a straight-line guess to the gate) or `--dj-arrive`; the Course 3 lap (start gate to
finish) and restart lead (to the start gate) from the best's farm laps, else 233 and 29. Without a trace of the best, `--measure` refuses: pass `--cash`, `--arrive`, `--lap`, `--lead`,
`--clone-path`, `--repeat` and `--row-step` instead. A planned buy is one the run can pay for when it gets to the box:
the plan never has the player stand at a box waiting for clone income (a search window can't find hundreds of idle
ticks, and a reach window on such a buy never ends); when Cash is short, it runs the course again. Only `--measure`'s
replay of the run's own goals waits, as the route did. The greedy prices the walk to a phase's last box from where the
player stands (`buydj` is 130 ticks from `buyreward3`, not a repeat buy's 22): priced as a repeat buy, it put three
`buyreward3` (~20 GW that never paid back) between the last lap and `buydj` in the 482.98 s route. The plan buys
`buystatue` 5288 ticks after Course 3's gate and `buydj` 10555 ticks (211 s) after it, against 17697 without bonus 3
and the statue. The model pays ~20% more than the
665.48 s route earned over its late laps (its farm replayed: `buydj` 23191 ticks after `reward:bonus1` against the
route's 24767, which stood 1868 ticks at the box), so expect the route to need a few more laps than planned. The
run's avoid box over Course 3's start gate was removed for it.

A Course 2 clone bought before `reward:bonus1` doesn't pay for itself: Cash at `finish:2` is 761 W against the clone's
800 W, so the run would wait ~100 ticks near the box and walk ~90 more, and the model's break-even detour is 100–190
ticks (`buy:buyds` −41 ticks with the clone 700 ticks before the bonus and a 100-tick detour, +59 with 200).

## Seed

Unity's RNG is deterministic for the runs: every Full-TAS new game starts it from one fixed seed (the plugin's
`FullTasSeed`, `UnityEngine.Random.InitState` at the fresh new game right before its start snapshot, and again before a
real-time playback's first tick). The snapshot therefore carries the seeded `UnityEngine.Random.State`
(`"random": [s0, s1, s2, s3]` in the trace's start state and after every tick, and the trace header's `fulltas` carries
the `seed`), so the turbo judge, the trace and the offline search all start from the same state. The seed is fixed and
unoptimized (`SEED (unoptimized) <value>` on the overlay); nothing user-settable writes the RNG.

Real-time play makes draws a turbo run never does: the `Start` of objects a run turns on (lights, plants), each box's
`updateMultibuyText` (InvokeRepeating) turning a buy-max box's count text on, Unity-run coroutines (tree growth, zone
loads). A turbo run is one synchronous batch, so it runs only `OnEnable` draws and the ticks' own (sounds, clone rolls,
buys); see `enginesim/README.md`'s consumer audit. So a real-time playback's rolls can differ from the turbo replay's
even from the same seed; the gate (turbo) is the judge.

## Routes

`fulltas/<run>_NONE <time>`, saved only by the gate (`SaveSolverRoute`), only when strictly faster. The gate replays the
route in turbo from the run's start with the usual buffer audit, checks the unlocks against the run's own purchases so
far, and refuses a quick restart on two ticks in a row (a real button is released in between). Splits per goal are kept
in `fulltas-splits.json`.

A goals edit (the studio's **Save goals** or `fulltas.json` edited on disk) or a changed `start` renames every saved
route of that run solved for other goals to `<name> stale`: it's out of the record and never played or captured. The
offline best counts only its unchanged leading goals it reaches, and **Play** and **Capture start** use its inputs only
up to the last of them (past it they head for the old goals, or miss the next one). The solver drops a saved best's
inputs past the last unchanged goal the same way.

## Studio: Full-TAS tab

- **Goals** (`POST /api/fulltas/goals {name, goals}`), **Capture start** (`/api/fulltas/capture`: start trace + world,
  seeded with the saved best), **Play** (real time from a new game), **Test best** (`/api/fulltas/play {name, closest:
  true}`: the search's attempt furthest toward the goals, see below; allowed while a solve runs), **Solve** / **Solve
  unsolved** / **Stop** (`/api/fulltas/solve {name, minutes, windowMinutes, window, threads, goal, unsolved}` runs
  `IGTAP.EngineSim fulltas`). `/api/state` has
  `fullTas`; its `solve` has the solver's events (`events`, the latest `status` as `progress`), `error` (didn't start,
  crashed) and the raw `log`; `lastResult` is the last Play or gate check (`kind`, `error`, `reached`, `expected`, ...).
- **Solve** polishes the goals the best reaches, windows from goal 1 on; **Solve unsolved** (`unsolved: true`, the
  solver's `--unsolved`) reaches the goals the best misses first, starting at the first of them. Both recapture the
  trace (seeded with the saved best) when the run's goals or start changed since it was taken, or when its header's
  `startSetup` isn't the plugin's `FullTasStartSetup` (bumped whenever what a new game's start state holds changes) or
  its `format` isn't `FullTasTraceFormat` (bumped whenever the per-tick export changes; format 2 drops
  `MacroEngine._steps`, which made a 437-goal trace 3.8 GB and crashed the solver reading it): a
  solve searches the goals in its trace's header. **Only goal** N (`goal: N`, the solver's `--goal N`) makes **Solve** polish just the
  **Window** goals ending at goal N (window 2: from goal N−2 through N−1 to N), window after window, keeping the rest
  of the route as it is and scoring the time at goal N. `threads` 0 or missing (the site sends none) is cores − 2.
- **Solve by resets** (`resets: true`, the solver's `--resets`) polishes the best one reset segment at a time (see
  [Reset segments](#reset-segments)); **Only segment** N (`segment: N`, `--segment N`) searches just that one, over and
  over (**Solve segment N**). The goals table draws a numbered bar down each segment's goals, as the TAS overlay marks a
  tech's ticks (`/api/state`
  `fullTas.runs[].offline.segments`: `[goals before, goals by its Quick Restart, inputs]` each): its number, goals and
  inputs in its tooltip; clicking the bar picks it for **Only segment**. Under each segment's goals an icon strip marks its
  closing Quick Restart (↻, hover for where it sends the player; ⚑ at the route's end). Once a solve has sent them
  (`fullTas.solve.segments`, the solver's `segments` event) the strip also shows what the restart leaves on: ✓ for
  nothing, else one amber icon per carry-over (↗ momentum, ⇤ wall jump lock, ≋ zip mover momentum, ⤒ ground bounce
  window, » dash still running, J / D jump or dash buffered; hover for the solver's wording). A ⚡ chip gives a known
  faster way's saving. A legend under the table keys the icons.
  The bar is amber or orange while a buy goal comes from the segment's first goal on (time saved there changes the Cash
  that buy sees) and green or teal after the last buy (time saved there is kept); neighbours alternate between the two
  shades. A bar glows when a faster way is known for its segment (`fullTas.runs[].offline.faster`, from `.faster.json`
  when that file is of the saved best).
- The Solve panel shows the route so far or the record, this solve's gain, what the search is on right now, and a feed
  of records, finds, drops and gate checks. Any error gets a card whose **Copy details** puts a report (problem, best,
  events, command, log) on the clipboard.
- **Goals in game** (`POST /api/settings/fulltas-goals {name}`, `""` turns it off; settings `fullTasGoalsRun`, `/api/state`
  `fullTas.goalsOverlay`) draws the run's `at:` and `reward:` rects in game, numbered, where the goal check tests them
  (`courses.json`'s frame plus the current origin), independent of the main overlay. While that run plays, reached
  goals are green and the next one yellow. The run's avoid boxes (forced-paths.json, the solver's copy) are drawn in
  red, and a box added or removed on the map shows up within a second.
- **Show map** draws the run's `at:` goals as they are in `fulltas.json` now (the plugin passes them to `map` as
  `--goals`; the trace's own goals are the ones it was captured with). **Move goals** there: drag a green goal box, or
  type its new centre, then **Save goal** (`POST /api/fulltas/move-goal {name, index, label, x, y}`: `index` 0-based,
  `label` the goal as the map drew it, refused if the goal list changed since). The goal keeps its size and any
  `from=`; the edit is a goals edit like **Save goals** (disabled while a solve or playback runs).

## Offline search

```powershell
& $exe fulltas $traces\fulltas-main.json --minutes 60 --window-minutes 1 --window 2   # --unsolved, --no-submit, --fresh, --repair, --threads N, --goal N, --piece-minutes P, --resets, --segment N, --seed FILE, --seed-list FILE
```

- The best is `<traces>\fulltas\<run>.json` (or the trace's inputs when better or when there is none).
- The studio's solve writes its seeds (`fulltas-<run>-seed-N.json`) and passes them as one list file
  (`--seed-list fulltas-<run>-seeds.txt`, a path per line): a seed run adds a seed per goal it reaches, and 372 seeds
  (`FalseEnding2-Any%`'s 294 goals seeding `FalseEnding-Any%`) as `--seed` arguments passed Windows' 32767-character
  command line (`The filename or extension is too long`).
- By default the search polishes the goals the best reaches (all of them once it's complete); a best that reaches none
  is reached first. `--unsolved`: a complete route first: until every goal is reached, only the first goal not reached
  yet is worked on. A loop goal (`buy:`, `start:`, `finish:`) is built from pieces (below). Any other goal is searched
  (`reach` windows): from the last reached goal, then redoing one, two, three goals before it, each round with a longer
  budget (`--window-minutes` × round). A reach window ends as soon as it finds a route to its goal: that route is
  spliced in and saved right away, and the next window searches on from it (the rest of the budget isn't spent). After
  3 rounds without the goal, or when the builder finds no way to a goal, the solve stops with an error saying which
  goal (it doesn't search on all night: a different plan, goal list or avoid box is needed). Polishing starts once a
  run reaches every goal.
- Polishing: first the loop goals are rebuilt from the piece library and the best's own ways, stretch by stretch (kept
  when the whole run gets faster). Then each piece the best plays there is searched for a faster one (`--window-minutes`,
  most-played first, each at most once a solve), from the best's own states, and when any is found the stretch is
  rebuilt with it right away (see [Loop builder](#loop-builder)). Then windows of W goals (default 2) from goal 1 up to the last goal
  the best reaches, skipping windows made only of loop goals: each worker replays the best up to goal i-1
  (`Simulation.Advance`), starts there (`SetStart`) and searches for the fastest way through goal i+W-1. The first
  goal's split is kept only as part of the whole: a later goal reached sooner is what counts, so a lap that ends slower
  but sets up the next one wins. `--goal N` polishes its window only, loop goals or not. A window scores its way at its
  last goal's tick: a strict run ends 12 ticks after that goal (`Simulation.StrictFinishTicks`), and windows used to
  compare that end with the goal's tick, so a way had to be 13 ticks faster before it counted.
- A window's best is spliced into the run with the old tail after it; when the old tail no longer reaches the rest (the
  state differs), the following windows are searched again from the new state (`repair`), seeded with the old tail,
  and the run is kept only if it ends sooner.
- Routes found from a start with the same movement state for the same next goal seed each other (the repeated laps).
- Each window's explore archive (input prefixes, as a course solve's) is kept in
  `archive\fulltas-<run>\<first>-<limit>-<hash>.json`: a retry or a later solve of the same window from the same prefix
  resumes it; `--fresh` ignores it.
- The run's goal-boundary states are kept in `archive\fulltas-<run>\states\`: when the run first reaches goal k, or a
  better arrival does, its state is written there, keyed by the hash of the run's inputs up to it (a route edit hashes
  differently; the start.key drops them with the archive). A window starts from the stored state at its prefix's end
  instead of replaying the run up to there, and an archive cell replays its prefix from the later of its worker's own
  ancestor and a stored goal state on the same inputs. A state is a byte-for-byte snapshot walk (`enginesim/README.md`
  "Memory"); one whose world this can't write is simply not kept, and the run replays to it as before.
- A complete, faster run goes to `offline-best\fulltas-<run>-<time>.json` and to the gate (`/api/fulltas/submit`).
- Reach windows also write `<traces>\fulltas\<run>.closest.json`: this session's attempt furthest toward the goals (most
  goals reached, then nearest the next `at:` rect, then fewest inputs; `OfflineSolver.Closest`), from the run's start,
  rewritten whenever it gets closer. The studio's **Test best** plays it when its goals are the run's; `/api/state`
  `fullTas.runs[].closest` has `reached`, `next`, `distance` and `ticks`. A playback of a partial route (this, or
  **Play** of an incomplete offline best) ends with its inputs; only a complete route waits for its last goal.
- Besides its log lines the solver prints one `@fulltas {json}` line per event (`FullTas.Emit`): `session`, `phase`
  (`reach`/`build`/`polish`), `window` (`kind`: `reach`, `polish`, `repair`, `piece` or `segment`, goals `first`..`limit`, `target`,
  `budget`; a piece window's `piece` says what it heads for, a segment's extra lap too), `status` (each second), `found`,
  `windowEnd`, `record` (`previous`, `best`), `dropped`, `note`, `gate`, `done`; with `--resets` also `segments` (every
  segment: `n`, goals `first`..`last`, `start` and `reset` inputs, `from`/`to` times, `carries`, `quickRestart`: where
  its Quick Restart sends the player) and `segment` (the one searched next). Times are seconds from the run's start.

### Reset segments

Terms: a *goal* is one of the run's Full-TAS goals (numbered, where to go next); a *Checkpoint* is the game's
`checkpointScript` trigger, which sets `respawnPoint`; a *Quick Restart* is the TAS's `reset` input, the game's
`Movement.respawn(true)`. A Quick Restart goes to `courseResetPoint` while a course is running (its start gate sets its
reset point, `courseScript.FixedUpdate` keeps it there, the finish clears it), else to the last Checkpoint touched. The
course-checkpoints toggle (`isRespawningAtCheckpoints`) only changes where a death respawns: `respawn(true)` ignores it,
so while a course runs a Quick Restart goes to its reset point even after a Checkpoint. A death ends every Full-TAS run,
so only Quick Restarts split one.

`--resets` (`enginesim\Cli\FullTasSegments.cs`) cuts the best at its Quick Restarts: segment k runs from the input
after Quick Restart k−1 (or the run's start) to Quick Restart k, the last one to the route's end. The studio's list
shows where each segment's Quick Restart goes (`Course 1 reset point (x, y)` or `Checkpoint (x, y)`, read on the tick
before it). Polishing a goal window splices the old tail in at a goal, where the new state differs, so the tail breaks
and `repair` re-finds it. A segment is spliced at a Quick Restart instead:

Segments are polished from the last one back. A segment saves time without touching the route before it, and the later
it is the less it can break after it: an early faster lap changes the Cash of every buy after it, so a later buy walk takes
a box it can now pay for and the run stops there. The last segment (after the last Quick Restart) is one search from its
Quick Restart to the route's last goal, with Minutes per window for each of its goals; no buy comes after it in
`FalseEnding-Any%` (its goals after `buy:buydj` are all area goals). Searched in windows of two goals it took the record
from 511.22 s to 509.96 s, but each window's faster way broke the old goals after it and re-finding those cost more than it saved.

- Its goals are searched (a `segment` window) from the state after the Quick Restart that opens it, for its last goal, faster
  than the best reaches it there.
- Each way found (the last five, fastest first) is cut at that goal and closed with the old Quick Restart's own input tick,
  after the old segment's inputs between the goal and the Quick Restart, or after 0 to (old gap + 24) idle ticks, shortest
  first. The old route after the Quick Restart follows unchanged.
- The first close that leaves the player as the old Quick Restart did is replayed as a whole run. "As the old Quick Restart did"
  means after the Quick Restart tick, every `Movement` field (`respawn` zeroes `Velocity`, `momentum` and
  `movingPlatformVelocity` and keeps the rest: the wall jump's lock, the zip mover's history, the ground bounce's window,
  coyote, the buffers, the dash, `respawnPoint`/`courseResetPoint`), the world position and origin, the body's position
  and velocity, the interpolation's two ends, scale, rotation, colliders, the animator's `Animation`, and the invokes
  and coroutines on the player (`cancelDashBuffer`) are equal, exactly. There are three exceptions, timers the game only
  compares with thresholds (from every read in `Movement`): `dashCooldown` and `wallCoyoteFrames` once both are below 0, and
  `framesSinceLastDash` once both are at least 4 and `dashBoostMax` × 1.4. A close that differs only off the player's
  motion (position, origin, body, interpolation, `Velocity`, `momentum`, `movingPlatformVelocity`) is followed under the
  old route's inputs after the Quick Restart, up to its next Quick Restart and at most 60 ticks: it counts when it moves exactly as
  the old one every tick and has become equal in every field. `FalseEnding-Any%`'s first Course 1 lap found 0.12 s whose
  Quick Restart differed only in `cancelJumpCutBuffer` pending 13 ticks instead of 11 (the jump release came 2 ticks later);
  a press or release cancels that invoke, so the old route's next input decides whether it ever matters. Any other
  close whose state differs isn't used, and a way with no close that matches is dropped with the fields that differed
  (`Segment N is faster, but no Quick Restart after it leaves the player as the old one did (...)`); nothing is rebuilt for it.
- The whole run still has to end sooner. The player is the same after the Quick Restart but the world may not be:
  Cash and clone payouts come at other ticks, the RNG has drawn differently, the game's objects are elsewhere in their
  cycles. When the old route after it then stops at a later goal, the old segment holding that goal is searched again
  from its own opening Quick Restart (a `repair` window, seeded with its old way) and closed with its old Quick Restart
  under the same state check, so everything after it stays good; then the next segment that stops, each at most once,
  until the run reaches every goal the old one did (kept if it ends sooner), reaches a goal no sooner than the record
  does (it can't end sooner any more), or a segment has no way through. Only the first way whose Quick Restart matched
  is repaired, as each rebuilt segment is a window of its own. `FalseEnding-Any%`'s 0.12 s faster Course 1 laps are the
  case: with one, the old walk to goal 230 or 283 (`buy:buyclone3`) buys Course 3's `CashPerLoop` box on the way
  (`Bought Courses/course 3/localUpgrades/CashPerLoop, which isn't the next goal's box`).
- A way whose Quick Restart matched but whose whole run still failed is kept in `<traces>\fulltas\<run>.faster.json`
  (the segment as the best holds it, the way, the time it saves there and why the run failed) while the best holds that
  segment unchanged. That segment isn't searched again: each pass retries the known way in the whole run (no rebuild) and
  keeps it once the route after it takes it.
- A segment searched with nothing faster found is kept in the same file (`searched`, with its minutes per window) and
  skipped by later passes while the best holds it unchanged, unless **Minutes per window** is longer now or it is the
  **Only segment**. A pass that has only such segments left stops (`every segment left has been searched at N min per
  window`).
- A segment with no goal is first tried without: one Quick Restart where the two were (the opening one as it is, or the
  closing one's input in its place), kept when that Quick Restart leaves the player as the closing one did and the run ends
  sooner. An extra lap (the builder's, for Cash) is then searched as its course's gate and finish (`start:N + finish:N`,
  `FullTasTracker.For`) and closed the same way. Anything else without a goal is kept as it is.
- The last segment ends with the route, so its way is spliced in as a polish window's.

`FalseEnding-Any%`'s 511.22 s best has 252 segments, 47 without a goal (the new game's first two Quick Restarts, the builder's
extra Course 3 laps before `buy:buydj`, and a few idle-tick Quick Restarts in Course 3's buys). After 231 of its 251 Quick Restart
ticks the player carries nothing of the tech's list; 19 leave a dash running or buffered, or a jump buffered, from the
Quick Restart tick's own input; and the Quick Restart at input 12606, right after a wall jump, keeps the wall jump's lock: the
Quick Restart tick runs at the locked 460 right and the next one does too (`wallJumpMovementLockRemaining` 0,
`wallJumpDirection` 1). That is the tech's first carry-over, and it isn't a problem: a new way for the segment before
counts only if its Quick Restart keeps the same lock (3 or more idle ticks before the Quick Restart leave `Velocity` x 0 and the
lock −1). Its segment 2 (the new game's second Quick Restart) can't be dropped: the one Quick Restart left leaves
`heightOfLastJumpStart` and `lastHyperY` 1.9 lower and `Velocity` y −27 instead of −37.8. Delaying most Quick Restarts by
1–8 idle ticks changes only the three timers above.

### Loop builder

A run's buys and laps repeat: the same quick restart, the same walk to a box, the same lap, dozens of times. Searching
each of those goals afresh (a window per goal) spent most of a night's solve re-finding them, and a buy the run couldn't
pay for yet had no route at all, so its reach window never ended. The builder (`enginesim\Cli\FullTasBuild.cs`) puts
them together from pieces instead:

- A piece is the inputs from one state to one goal (a box, a start gate, a finish), filed under the goal's label, the
  player's unlocks and where the player stood, in
  `<traces>\archive\fulltas-pieces.json`. The library is shared by every run, and kept across solves (`--fresh` starts
  without it); per spot it keeps the 4 shortest.
- For its next goal the builder tries every piece filed within 48 units of the player, and, when the run may quick
  restart there, every piece filed at the spot a restart takes it to, after an idle pad and the restart (pads of 0–24
  ticks, shortest first: a restart keeps `framesSinceLastDash`, so a dash pressed soon after it fires a tick late while
  the last dash cools down, and a piece's timing needs the same pad-to-dash distance it was found with). It plays each
  in the run from the current state and keeps the one that reaches the goal soonest. A piece is only
  ever used after it has been played in the run: a spot that differs (a grown tree, another speed) just fails the check.
- A polish rebuild also plays the best's own way to the goal from where the build stands. When that and the library's
  fastest differ, it takes the one after which the next goal comes sooner (the next goal by the library or the best's own
  way), and only on a tie the one that reaches its own goal first. Picking the soonest piece alone traded 3 ticks for 141:
  `FalseEnding-Any%`'s rebuild reached its second `start:1` 3 ticks early, at a gate height where only a 222-tick
  `finish:1` piece played, so every rebuild was slower than the best and was thrown away.
- Polish searches a piece from the state the best plays it in. When the best's next step is the run's next goal, the
  search runs through that goal too and is scored there (a `piece` window for `start:1 + finish:1`, say), with the best's
  tick there as its "To beat": a way that reaches its own goal later but the next one sooner is kept and filed as two
  pieces, and the pair rule above lets the rebuild take it.
- When no piece reaches it, the goal is searched once (a `piece` window of up to `--piece-minutes`, default 3; three
  times as long once more if that finds nothing), seeded with the library's pieces for it and the saved course routes.
  The build takes the first way found once a quarter minute passes without a faster one (polish searches the pieces
  it plays for longer), and files it for reuse. A way that quick restarts within its first 25 ticks is filed from the
  restart on, so every later restart to that spot reuses it.
- A buy the run can't pay for yet (`Shop.Affordable`: the boxes' costs against the run's Cash now) gets an extra lap of
  its course first (the course of the last `start:`/`finish:` goal before it), up to 64, until it can. An extra lap isn't
  one of the run's goals (an event that isn't the next goal changes nothing): its
  gate and its finish are built as two steps, each checked against a goal of its own (`FullTasTracker.For`,
  `Simulation.UseGoals`), from the same `start:N` and `finish:N` pieces as the run's own laps. A lap step is cut at the
  first input by which its goal is reached; a lap with dashes plays more physics ticks than inputs (a dash's ticks
  read no input of their own), so the check runs on past the cut's inputs rather than stopping at as many ticks.
- Except when the clones pay for it soon: when standing still for 900 ticks (`ShortWaitTicks`) would pay for the box, the
  builder first tries a walk of 0-60 ticks either way and then hops in place (every 30, 40 or 50 ticks, jump held 6 or 14)
  until it buys, then a piece search, and laps only when neither buys it. Boxes buy on a touch of their trigger, a strip
  along their underside (`buydj`'s is 6 units tall, 140 above the floor), so standing under one never buys: the hops do.
  Put an area goal under the box before the buy (`FalseEnding2-Any%`: `at:7951,6950,120,50` then `buy:buydj`). The course
  it would lap is the last `start:`/`finish:` goal's, which after a trip is the wrong one: `FalseEnding2-Any%`'s build lapped
  Course 2 five times for `buydj` from Course 3's shaft before this.
- Every purchase is a goal: a route buys only the box of its next goal (`Simulation.StrictBuys`, on for every Full-TAS
  simulation: the builder, every search window and the saved best's replay). A walk that also bought another box spent
  Cash and levels the plan counted on, and could cap a box a later goal buys (a build that ran through `FastCloneChance`
  twelve times had nothing left for its `buy:buyfast3` goals; `TrueEnding-ANY%`'s 482.98 s route bought a `buyreward3`
  on its way to Course 3's gate and walked through Course 2's row buying neighbours, so its best stops at goal 127). A
  strict run finishes 12 ticks after its goal, not at it, so a neighbouring box the player still stands in once its
  0.2 s cooldown is over also ends it (the search would otherwise hand the next piece a spot where any input buys it). A
  buy goal whose box is inactive (capped, or not unlocked by the goals so far) stops the build at once, with the box's
  state (`Shop.Describe`: bought/cap, cost, what the run holds); before a searched buy the log shows the same state.
- The build is saved as the run's best every minute and when it stops.

### Engine farm planner

```powershell
& $exe fulltas-plan $tracesulltas-TrueEnding-ANY%.json --from reward:bonus3 --target buy:buydj --out plan.json --piece-minutes 0.3 --threads 6
```

`fulltas-plan` plans a run's buys and laps with the game's own economy instead of `plan-fulltas.py`'s model of it (whose
walk and restart guesses put junk buys before `buydj` and cost extra laps). It replays the saved best (strict buys) to
`--from` (a goal number or label; the goals up to it stay), then chooses one step at a time until it buys `--target`:
a lap of the stretch's course, each box the stretch buys (when the run can pay for it on arrival), or the target. Each
choice is played with the loop builder's pieces (a piece search only when none plays, `--piece-minutes`), and scored by
when it would buy the target: its end tick, plus the Cash the target still needs over the income laps would earn from
there, plus the walk to the target. The income is the course's lap reward over a lap (the best's own back-to-back
laps) plus the clones' income measured standing still after a quick restart for 2000 ticks: one lap's clone payouts
are too few to average their rolls, and picking the best of several one-lap guesses bought every box to its cap (Course
3: `buydj` 1718 ticks later than the best). It never waits at a box: laps fill. A lap's gate piece is the one after which
the finish comes soonest, not the fastest to the gate: the fastest gate piece crossed Course 1's gate where only a slow
finish piece played, and the plan's laps took 248 ticks from the box for the best's 107. Before planning it files the best's
own steps in the span as library pieces (a polished route passes spots the builder never filed). It prints the planned
goals and a line per step (tick, Cash, estimate, box levels); `--out` writes the run's whole goal list with them in
place of the old stretch (the goals up to and after it kept), for `fulltas.json` by hand. Planning is greedy and takes
a few seconds a step.

Before a piece search the planner replays cheaper ways to the goal (`Borrowed`): the library's pieces for it from any
distance (the library plays only those within 48 units, and each box of a row leaves the player elsewhere), then the
best's own inputs to the same goal in the stretch it replaces, from each quick restart and goal cut up to 12 goals before
it. A quick restart lands at a checkpoint wherever it's pressed, so the best's way on from one plays from anywhere: the
climb from Course 2 to `buydj`, which no search found. When none plays they seed the search. A box whose buy-max touch
is still buying is skipped (it goes on without another touch; searching for a second touch took 1.5 minutes a box). The
target is tried whenever the run holds its price, even when its box reads inactive because its course isn't loaded
(`buydj` from Course 2). Once it's paid for and no way reaches it, the plan stops (`stuck at tick N: ... is paid for
but no way reaches it`) instead of lapping: before this it lapped Course 2 1724 times with 7.9e13 Cash for a 5.55e11 box.

Stretches can be planned in parallel: the loop pieces (`archive/fulltas-pieces.json`) are written under a lock
(`fulltas-pieces.json.lock`) and merged with what other processes filed since, and shared files get a per-process temp
name (two plans crashed on one `.tmp`). The planner plays the goals of the trace's header, not `fulltas.json`: to plan a
stretch on top of a planned route, capture the start again with its goals first.

A clone pays once per path, and its path is the course's best time, so every step also offers a **record lap**: the
gate crossed, a quick restart to the course's reset point, and each saved course route (`--seed`) for the course played
from standing there after 0-40 idle ticks; the one leaving the lowest best time wins, and the estimate weighs the faster
clones. A route that misses from there is searched once per course from the restart, seeded with it. Course 2: the
`2_WJ 2.20` route dies on its spikes from the restart (its recording takes them on the last grace tick, and the restart
differs from its start by hundredths of a unit), and the seeded search, which scores the finish tick and not the
course timer, finds a 2.44 s lap, the one `FalseEnding-Any%` already runs. A 2.20 s lap from the restart needs a search
scored on the course timer (the model: `buyds` about 5 s sooner at a 2.20 s path than at 2.44 s).

`--route FILE` writes the planned route too, under the planned goal list (the goals up to `--from`, the plan, the goals
after the target). When the best Quick Restarts after the target, the old route from that Quick Restart on is joined to the
plan's end, after the old inputs between the two or 0-24 idle ticks, and kept when the whole run then reaches every goal:
the file is then a complete route (`total` set). When it doesn't play on the log says so, and why when the plan's end
leaves a course running (its Quick Restart goes to that course's reset point, not the old route's Checkpoint). To use a
complete route: save the `--out` goals (**Save goals**, which marks the run's saved routes stale), put the route file in
`fulltas/<run>.json`, submit it (`/api/fulltas/submit`) and **Capture start** again. `FalseEnding-Any%`'s statue to
`buy:buydj` (`--from 298 --target buy:buydj`) bought `buydj` 86 ticks sooner with 64 goals for 66; joined after 23 idle
ticks it made the record 507.14 s (from 509.28 s).

`fulltas-plan` takes `--seed` files as the solve does: the saved course routes that run the stretch's course from
standing at its start are played from the spot a quick restart lands at, and filed as its lap pieces (the gate's part and
the finish's) when they reach both. The planner laps from the library, and a lap the route polished in the run is often
slower than the course's own: `FalseEnding-Any%`'s Course 1 laps finish 12 ticks later after the same quick restart than
`1_NONE farm 2.12`, and with it filed Course 1's plan buys `buywj` 252 ticks sooner (tick 5328). When the old route
after the target doesn't play on to the end, the join that gets furthest is written (with where and why it stops: an
earlier `buywj` has the Cash to buy Course 2's `CashPerLoop` box on the walk to Course 2's gate), for the next stretch
to be planned or solved from.

`--harvest FILE` (the solve and `fulltas-plan`) plays a saved route (a `fulltas/<run>.json`: its goals and inputs, goals
of the run's labels) under its own goals from the run's start and files each of its gates, finishes and buys as a library
piece. After a plan changes the goals, the builder then plays the old route's laps and box walks wherever the player
stands as it did, and only searches the steps that changed: `FalseEnding-Any%`'s run after the new Course 1 plan (goal
129 on) was rebuilt in 3 minutes with the 499.42 s route harvested, where the build had done 25 goals in 3 minutes
without it.

### Zip planner

```powershell
& $exe zip-plan $traces\fulltas-TouchGrass-ANY%.json --route $traces\fulltas\TouchGrass-ANY%.json --minutes 5 --out zip.json
```

`zip-plan` plans Area 2's buy order from a run's state to the zip movers (`buy:buytree16`, `ZipMoversUnlock`, 625,000,000
NP) on a model of the game's economy (`enginesim/Cli/ZipPlan.cs`), because the engine's economy ends a run at Area 2
(green clones, clone dust, atoms and the tree are unmodeled, below). The start is the trace's start, or `--route FILE`
(a `fulltas/<run>.json` or any `{inputs}` file) replayed on it to input `--ticks N` (default: all of it): the currencies,
global and local upgrades, clone counts and paths, box levels and costs, the tree's grown segments and the atoms, as the
engine holds them there.

The model follows the decompiled code:

- **Clones** (`clonesScript.Update`/`SpawnClone`), frame by frame as the engine and the gate step them (one frame per
  physics tick; `cloneFrameSeconds` 1/280 for 1x): off screen a course with clones and a path spawns once
  `timeSinceLastClone` reaches 1 s (51 frames) and pays `ceil(value (2j+1) / previous interval)`; on screen (the player
  inside the course's `activeCloneArea`, its `clonesLoD` polygon: Course 9's covers most of zone 2, or lapping it) one
  spawns once the rolled interval's frames pass and the pool has one free, and pays `ceil(value (2j+1))` at its path's
  end. The interval is path / clones with the speed and size rolls (`Random.Range(0, 101)` against the chances, per
  tier) averaged exactly. Watts are `reward × clone multiplier`, green power `gpReward × clone dust multiplier` on a
  green clone (a fraction `greenCloneClance / 100` of spawns: TreePart0's box, 1 % a level), clone dust `CdReward ×
  clone dust multiplier` with clone dust on. The `ceil` makes every course with one clone worth at least one GP per
  green spawn off screen, and one more clone or reward level often worth nothing until a few come together. Against
  the engine's own clone economy (`FalseEnding-Any%` replayed, 60-70 s idle, Courses 1-3 with Course 3 on screen) the
  model's W per second is within 1-3 %.
- **Rewards** (`courseScript.UpdateReward`): W, GP, CD rewards and the comp boost (`2 + 0.5 × strength`, `30 + 10 ×
  time` s, renewed by a lap once TreePart12's box is bought).
- **Atoms** (`AtomColliderSystem`): the atom box (`AtomUpgrades/AtomUpgrade`, GP, buy-max) first resets Courses 1-10
  that aren't exempt (local upgrades, clone counts; paths kept) and sets W to 0 (`doNewAtomPrestige`), then queues its
  atoms; one lands every 0.4 s (+1 NP), its tier from `atomLevelChance × 25` per atom, `3 + t` atoms of tier t merge
  into one of t + 1 (craft time from `HandleTierLogic`), the first merge into a new tier pays 1000 ticks of NP. The
  collider stops while zone 1 is loaded. The atom level boxes (`buyatomlevel9`, `buyatomlevel10`) clear the atoms and
  the atom box's price.
- **NP** (`globalStats.DoConstantCurrency`): `NPrewardPerTick` per physics tick while NP is under the cap, 6000 ticks'
  worth. The zip box needs the cap at 625,000,000: about 104,000 NP per tick.
- **Boxes** (`upgradeBox`): local costs `ceil` their scaling and reset with an atom, global ones round it and stay (the
  model's costs match the engine's on every box `FalseEnding-Any%` bought); a touch buys once per 0.2 s cooldown, a
  buy-max box all `GetNumPlayerCouldBuy` counts; exempting and buy-max boxes, tree growth (each trunk 1/0.6 s, the land
  1/3.5 s, the box's 2000-unit drop ln(400)/3 s, auto-grown segments).
- **Where**: the player's position is followed; a walk is estimated from the distance (`travel` in `zip-plan.json`:
  units per tick sideways, up and down, plus overhead ticks) unless `legs` names it (`"<from>><to>": seconds`). Zone 2
  is a drop from zone 1: once in it, nothing in zone 1 is reachable (`wayBackToZone1` or the game's
  `Zone2toZone1PathOpen` lifts that).

**Course times.** A lap takes the course's farm time from its quick restart (the player is at its start) or its record
plus the walk to it; the clones' path is the record. Both come from the saved routes (`routes.json`'s `<course>_<CAT>
<time>` and `<course>_<CAT> farm <time>`, fastest in `categories`, non-stale first) as they're saved, and from
`zip-plan.json`'s `laps` until then (Courses 7-10 have none yet: their estimates are printed as `ESTIMATE`; `"force":
true` on an entry overrides a saved route). Which courses the plan may lap: `lapCourses` (default 1-10).

**The plan** is a list of steps (`buy:<token>` once, `all:<token>` every level it can pay for, `lap:<course>`,
`atoms:<count>`: saves GP for that many and touches the box); whatever it can't pay for yet it earns first, lapping the
course that earns it fastest (its reward and the comp boost on its clones) or standing. After the steps an autopilot
finishes the run: the tree's boxes from the root to the zip box as it can pay for them, the free first atom, and when
the NP cap can't reach the next box's price, the purchase (1 to 32 levels of a box, or a course's first clone or clone
dust with the lap after it) that pays for the atoms it needs soonest, or those atoms. The search builds the steps
greedily, then polishes them on every core (delete, swap, move, insert one or two, resize) for `--minutes` (default
5), and drops steps that don't help. `--plan "all:buynp10,buy:buyatomlevel10"` starts from a
given list instead (with `--minutes 0`: only evaluates it). It prints the timeline (each step's times, currencies,
rates, NP cap and atoms), the plan as `--plan` text and the Full-TAS goals (`start:N finish:N` per lap, `buy:<token>` per
purchase); `--out` writes them as JSON.

From `TouchGrass-ANY%`'s best replayed (goal 529 of 543: Course 4 farmed after the breaker, 9.5e24 W, no clones): 11.2
min in the model after a 4-minute search. The W is spent before the first atom resets it: 100 Course 9 clones with
clone dust on (`buycd9`) and a lap (the clone dust stays through the atoms' resets and multiplies green power from then
on), `buyatomlevel10` and 20 `buynp10` (+175 % NP a level); then the tree, green clones on Courses 6 and 7, atoms in
three buys (3, 4, then 21 for the cap: 104,000 NP per tick) and the zip box. Courses 7-10's laps are estimates until
their routes are saved, and the walks are distance guesses. The engine can't play these goals yet (Area 2's economy is
unmodeled there); the plan is for building the run's goals.

## Offline economy

The whole run needs Cash offline. A start state whose economy carries the clones (every Full-TAS trace) runs the
economy natively (`enginesim/Runtime/Economy.cs`): course-finish rewards, comp boost, new-best clone paths, clone spawns
and payouts on and off screen (the clonesLoD trigger), what the courses' local boxes change (reward, clone count, clone
multiplier) and the global boxes `GLOBALcashPerLoop` (its own course's reward; the game updates the others on a random
5 s phase), the global clone speed and size caps (`maxCloneFastness`, `maxCloneBigness`: dictionary only while both
clone chances are 0) and the statue (`unlockPrestige`: a global clone multiplier instead of its own entry). A prestige
box — the statue and Course 4's `buyprestige4` — also runs `BoostOldCourses`: each old course's reward tier becomes the
buying course's tier plus its own mod and its reward updates, and its boxes gain half their cap multiplier, become
buy-max and turn back on. A buy-max box's touch buys what `GetNumPlayerCouldBuy` counts (`IE_BuyMax`): the first during
the tick's physics, the next at that tick's end, then one at the end of every following tick while affordable (one frame
per tick, `TurboRunner.EndOfFrame`, `Shop.EndOfFrame`). Real-time playback renders more frames per tick and so buys
faster. Such a touch is one buy goal: the levels bought after the first continue it, so they neither reach a goal nor end
a strict run as a stray buy (`FalseEnding2-Any%` buys Course 2's three boosted boxes to their caps with one goal each after the
statue, `buy:buydj` 83 s sooner). The block swap (`buyswap`) is modeled as the purchase's bookkeeping
and its buy goal only — the colliders it swaps aren't — so it works as a run's last goal and any run past it ends
(`Unmodeled purchase: block swap (collider changes)`). Cases the model can't follow end the run (`Unmodeled economy:
...`): clone speed/size/colour rolls, clone dust, the other global boxes and a prestige buy while Vman is active. A Vman
course completion pays its `completionReward` once (`Shop`), and a Vman clock that runs out ends the run.

The trip breaker (`buyprestige5`) is modeled in full: `BoostOldCourses`, then each of Courses 1–5 through
`localUpgrades.OnPrestige` (local upgrades, clones and best path reset; Courses 1–3 get the breaker layer's tier mods,
Course 4 `c5.tier + c4.tier - 1 + 4`, Course 5 keeps its tier), the Course 1–3 movement boxes turned into
`activateNextBreakerLights` boxes (their purchase is modeled too), `PrestigeUpgradeBox4` disabled, then
`tripBreakerScript.TripBreaker`: Cash 0, `buyswap`'s box off and `buyblockswap5`'s on, and the coloured blocks swapped
to orange (Courses 4–5 collide differently afterwards). The swap needs both colours' block geometry: the world export
writes each coloured-block composite's `sourceShapes` (its geometry with its tilemap on); an older export stops the run
at the breaker (`Unmodeled physics: ...`). `buyblockswap5` sets `blockSwapUnlocked` and the run goes on: a dash then swaps
the blocks through the game's own `colouredBlockSwapper`, as in the `WJ_DS_DJ_BS` courses (`TouchGrass-ANY%` drops down the
hole left of Course 4 after it). The turbo gate's snapshot restores what the breaker
changes (box GameObjects and enabled flags, Area 1's state, the breaker's lights, the blocks' colliders). The lights it
keeps are the game's own (`GameBreakerLights`: GlobalLight and BreakerLights on unless Area 1 is overgrown, GlobalLight
off once tripped, each area light per `activeLights`), never the studio's **Breaker lights** forcing, so a new game's or a
Vman speedrun's start is lit with no breaker light on.

## FalseEnding-Any% and FalseEnding2-Any% (2026-09-28)

Both end at the credits trigger after `buy:buydj`, `VMAN`'s way up (goals `at:8570,8300` to `at:22200,15900`).
`FalseEnding2-Any%` is the copy the economy work was tried on first. `FalseEnding-Any%` now has its own recalculated economy
(below), 390.38 s.

**Economy re-evaluation** (`fulltas-plan` from `FalseEnding-Any%`'s 487.14 s best, per stretch; ticks of the target buy):
Course 1 to `buywj` 5445 against the best's 5327, `reward:bonus1` to `buyds` 12142 against 11672, statue to `buydj` 23192
against 23083, and `reward:bonus3` to `buystatue` 16726 against 16903 (177 sooner, but with fewer Course 3 levels
`buydj` then came at 23457). The greedy planner beats none of the polished buy orders; the one gain, an earlier statue, only
pays once the Course 3 levels it skips no longer carry the income after it (below: `FalseEnding2-Any%`'s statue plan saved 1 s).

**Recalculated economy, 390.38 s** (from the 403.28 s route, all four stretches planned again with the planner
above). Course 1 to `buywj` (5458 against 5311) and `reward:bonus1` to `buyds` (12345 against 11656) are still slower than
the polished orders. `reward:bonus3` to `buystatue` bought the statue at tick 16629 against 16836 (Course 3's buys
reordered: 88 goals for 86). The statue to `buydj`, planned again on top of that: buy the three boosted Course 2 boxes
straight off the shaft (`buyclone2`, `buyclonemult2`, `buyreward2`; 338.78-339.62 s), then run **six Course 2 laps with
the dash** (quick restart to the gate, about 1.9 s a lap, 340.34-351.80 s). The laps are the open idea below: a dash lap
makes a new best path, so Course 2's 52 clones run a shorter path and pay faster, and each lap pays the boosted reward
too. Then the old route's climb (borrowed from its quick restart) buys `buydj` on arrival at 362.02 s (374.96 s before),
with no hopping under the box. Splits: `buy:buystatue` 332.58, `buy:buydj` 362.02, the credits 390.40 offline, 390.38 s
judged in turbo (`fulltas/FalseEnding-Any%_NONE 390.38`). The area goals down the shaft and under the `buydj` box aren't
in its 300 goals.

**The statue boost.** `buy:buystatue` (`BoostOldCourses`) sets Courses 1 and 2 to Course 3's reward tier plus their mod
and turns their boxes back on as buy-max boxes with 1.5x caps, at their old costs: `buyreward2` 39 -> 68 levels,
`buyclone2` 23 -> 52, `buyclonemult2` 6 -> 15, for about 7e5 W against 3.2e10 held. `FalseEnding-Any%` never went back
for them and lapped Course 3 for 6180 ticks to afford `buydj` (5.55e11). `FalseEnding2-Any%` drops from the statue down
the shaft (`at:7645,6050`, `at:6827.5,4537.8`, `at:4300,2850`), crosses `start:2`, touches the three boxes (one goal
each, 0.9 s), climbs back to Course 3, stands under the `buydj` box (`at:7951,6950,120,50`) and hops into it until the
clones have paid (`buy:buydj` at 374.96 s against 461.66 s). Records: 409.34 s (Course 3 lap before `buydj`), 408.28 s
(the statue plan), then the goals edit that dropped the lap staled it: 411.02 s, and 403.30 s once the ending was polished.

Splits of the 410.22 s best (the 403.30 s record's ending is 7 s faster): `buy:buywj` 106.22, `reward:bonus1` 142.60,
`buy:buyds` 233.12, `start:3` 247.06, `reward:bonus3` 251.40, `buy:buystatue` 336.72, `start:2` 346.20, the last Course 2
box 347.10, under the `buydj` box 361.86, `buy:buydj` 374.96, the credits 410.22 (`FalseEnding-Any%`'s ending from
`buydj` is 25.7 s). `buydj` waits on Cash, not the walk: a faster climb saves nothing unless the income rises.

Open ideas, estimates only:
- Course 2's clones pay per best path: its laps were all run before the dash (about 2.4 s) and the `2_WJ_DS` record is
  1.6 s. Taken in `FalseEnding-Any%`'s 390.38 s (six dash laps after the statue, `buydj` 13 s sooner); its laps run about
  1.9 s, so a faster Course 2 lap still pays.
- Every second the statue comes sooner moves the whole second half; the Course 3 economy (85 s) is the stretch to cut,
  and faster Course 3 laps (`3_WJ_DS`) shorten both the laps and the clones' path.
- `reward:bonus2` (+1 global reward level, ~10% on every reward after it) costs ~8 s: break-even unless taken early.
- Course 1's boosted boxes: no clone multiplier box, about a sixth of Course 2's income, ~9 s detour: likely a loss.
- Reaching Course 3 without the dash and buying `buyds` with post-statue Cash would skip most of the 90 s Course 2
  economy. Searched (`Test-FE-NoDash`, area goals along the dash climb): without the dash the run reaches Course 2's box
  and `at:5600,3400`, and 6 minutes found no way on to `at:6900,3600`. Reaching Course 2 before `buywj` (Course 1's 35 laps
  for ~700 W against one Course 2 lap) found no way from the wall at x 1900 without wall jumps in 10 minutes.

Since the redeploy on 2026-09-28 `FalseEnding-Any%`'s own routes (487.34 and 487.14 s) stop at `at:22200,15900`
without entering the credits trigger; `FalseEnding2-Any%`'s solve re-found that goal 45 ticks slower (488.04 s), cause
not found. Each goals edit stales the run's records: try goal changes on a copy of the run and move the goals and
route over only when they beat the record.

`--goal N` scores at goal N and keeps what it finds even when the goals after it are then re-found slower: run over
`FalseEnding2-Any%`'s ending goal by goal it left the run file at 409.46 s under the 403.30 s record (restored from
`offline-best/fulltas-FalseEnding2-Any%-403.32.json`). Polish an ending with **Solve** or `--resets` instead.

## Status (2026-09-25)

- `main` has 609 goals, all from `python tools/plan-fulltas.py --write` after `reward:bonus1`: Course 2's economy up
  to dash (~236 s), the dash bonuses, then Course 3 to `buyclonespeed` and `buystatue`, Courses 1 and 2 bought to
  their boosted caps with a lap each, Course 3 to `buydj`, and Course 4 lapped, `buyprestige4`, then `buyswap`, with
  clone speed/size chance buys on Courses 3 and 4 (the plan counts their rolls at their average; ~15.8 min in the
  plan; its 3 -> 1 and 3 -> 4 transitions are guesses, no route exists yet).
- Not in the goals: the free global boxes with no known route (`buyfree3`, `buyfree4a/b/c`) and Course 4's 3e12 W
  `buyglobalreward4`.
- `ANY%` has 789 goals from `python tools/plan-fulltas.py --any-pct --out FILE`: `main`'s goals through `buy:buydj`,
  `reward:bonus5`, Course 5 farmed (with `reward:bonus4`, `reward:bonus15`, `buyfree5`, two `buyglobalreward5`) to
  `buy:buyprestige5`, then Course 4 farmed (its reward tier is 29.5 after the breaker; Course 5's 16.5 would take
  ~1e15 ticks to 9.49e34 W) to `buy:buyblockswap5` (~19.7 min in the plan). Its 3 -> 5 and 5 -> 4 transitions and the
  bonus detours are guesses; no route exists yet.
- The offline search seeds its laps with the saved standalone course, bonus and transition routes.
- Real-time playback warns on the overlay when it drops frames: Update-driven state (clone pay, purchases waiting on
  it) can then differ from the route's.
