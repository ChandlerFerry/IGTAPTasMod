# Full-TAS

The whole game from a fresh new game as one continuous input stream. A run is a list of goals, game events that must
happen in order; its time is live time from the new game's first settled tick to the last goal: the physics ticks at
0.02 s plus the frames the run stands still in, each dash freeze's and pause's, at 1/280 s (README `### Rules` 31;
`MacroEngine.LiveSeconds`). Every time is reported with its game frames (the ticks) beside it. Goals split
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
409.34 s best to goal 282 of 300 and wrote that back. When the engine change is meant (a fidelity fix), the solve now
repairs on its own: it keeps the file as `fulltas/<run>.before-repair-<n>-of-<m>.json` and re-finds the run from where the
replay stops.

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

An event that isn't the next goal changes nothing (buying another box, finishing another course), except under loose
buys (below). Unlocks come only from the game's purchases; the plugin writes none.

### Loose buys

A run's `"buys"` is `"strict"` (the default, also when unset) or `"loose"` (`VMAN`:
`{ "name": "VMAN", "start": "vman", "buys": "loose", "goals": [...] }`).

- Strict: every purchase is a goal. Offline (`Simulation.StrictBuys`) a purchase that isn't the next goal's box ends the
  run; see the loop builder's "Every purchase is a goal" below for why.
- Loose: buys are places the route must reach, and buying more on the way is allowed when it is faster. A purchase that
  isn't the next goal's box stands and doesn't end the run. When a later `buy:` or `reward:` goal names that box, the
  purchase is kept for that goal (one purchase per box). Once the goal is current and every box it names is owned (bought
  for it, or kept from before), it is reached, on that same tick when all of them were bought earlier. `VMAN` is loose so
  its route can walk through Course 1's and Course 2's `Clone` boxes (free in a Vman world) as the old 34.88 s route did.

The same `FullTasTracker` (`FullTas.cs`, `LooseBuys`) applies the rule in the turbo judge, 1x playback, the offline engine
(the trace header's `fulltas.buys`, written by the capture; a trace whose `buys` differs from the run's is recaptured),
every search window, the piece library, polish and `fulltas-plan`. Its state (the kept boxes) is part of every snapshot
and of the state hash (`Simulation` state dedup); the search's cell key has only the goal progress. The turbo judge
never ended a run on another box, strict or loose: only the offline engine enforces strict buys.

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

The run's time is still counted from that start. The snapshot carries VmanScript's clock and score (turbo steps
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

Status (2026-09-29): the record is `fulltas/VMAN_NONE 33.92` (1696 ticks, judged turbo), under the old 34.88. Its splits are
81/48/49/30/82/105/62/37/30/94/97/91/103/67/211/74/44/93/85/53/9/54/97. How it got there from 41.34:

- 36.62 and 35.32, two `--transplant` grafts of the old 34.88 s inputs. From 41.34's state at goal 15 (`at:12500`) the old
  inputs replay the down-dash landing chain over goals 16–18 again (74/44/93 against 41.34's 98/109/193). Then the old
  route's Clone-buying way through Courses 1–2 (loose buys: Course 1 `Clone`, Course 2 `Clone` and `CashPerLoop`; `finish:2`
  at tick 458 against 508) was bridged onto it by repair windows. The route also walks through Course 2's
  `GLOBALcashPerLoop` and Course 4's `GLOBALfastCloneChance`.
- 35.16, a `polish` (six 1–2 tick re-timings in inputs 142–594).
- 34.28, a window over goals 10–14 scored at goal 14 (`fulltas --goal 14 --window 5`): goal 11 in 100 ticks against 144.
  35.16's dash off the wall at goal 10 went up-left and looped back; the new one goes up-right, as the old route did.
  Windows scored at goal 11 (`--window 2`) found it 56 ticks sooner, but they reached the large goal 12–13 boxes in a state
  the goals after couldn't use, and their repair came out slower. Scoring at the small goal 14 box kept it.
- 34.22, 34.20, 33.98 and 33.92, one unattended `--auto` run from 34.28 (`--parts lib,tail,combine --window-minutes 0.5
  --threads 6`, 75 minutes, 591 pulls, 24,300 CPU s), with the 54 distinct saved VMAN routes and probes as `--ref`s. At 9 s
  `combine` spliced goal 11 from an older `--auto` best (100 → 97 ticks). At 640 s a window over goals 21–22 cut 1 tick
  (its tail re-found by a tail window). At 1196 s a span window over goals 15–19 reached goal 19 14 ticks sooner. Its tail
  broke at goal 20, and the `graft` bridge that kept its way to goal 15 won 11 ticks (goal 15: 222 → 211). At 2929 s a
  lookahead window over goals 19–21 was bridged the same way for 3 ticks (19: 86 → 85, 20: 55 → 53). Nothing was gained in
  the remaining 25 minutes.

Against the old route, goal 15 is still 46 ticks behind (211 against 165). On 33.92 the `--auto` model's most expected
headroom is goal 15 (42 ticks), then 18 (27), 6 (12), 19 (11), 5 (11) and 16 (9). The bound is 396 ticks, and the known
slow stretches add up to 121 ticks. 30 s is 1500 ticks, 196 under 33.92. That is more than the expected headroom of all 23
goals together (181 ticks).

Finds that the loop doesn't convert. In the 75-minute run, 35 windows were faster through their own last goal but gained
nothing as whole runs:

- goals 17–18: goal 18 at 1382 against 1409;
- goals 16–18: goal 18 at 1380 against 1398;
- goals 3–6: goal 6 at 379 against 395;
- goals 4–5: goal 5 at 278 against 290.

The planner found goal 15 in 187 ticks against 211, and goal 18 in 71 against 93. Every one of these left a state that the
goals after it couldn't use. From goal 18 reached 27 ticks sooner, the tail window over goals 19–21 reached goal 21 at 1586
against the best's 1559, and the repair was dropped at goal 21, at tick 1699. The same happened earlier, by hand: a window
over goals 15–18 reached goal 18 30 ticks sooner and lost it after.

Where the CPU went:

| Lever | Share of CPU | Pulls | Ticks gained |
|---|---|---|---|
| span | 24% | 14 | 0 |
| window | 22% | 27 | 1 |
| graft | 16% | 87 | 14 |
| lookahead | 15% | 10 | 0 |
| polish | 14% | 444 | 0 |
| plan | 8% | 3 | 0 |

The graft's 14 ticks were the bridges of the span and lookahead finds. `polish` re-ran the same deterministic sweep on an
unchanged best: the same input ranges, the same tick counts, nothing kept. Combine splices that broke were dropped with no
quick restart to wait at (goal 15 from three 40-41 s `--auto` bests, 8–10 ticks ahead there, died at goal 17). Unlike
window finds, they get no tail window or route-sync.

The `--reset-study` rows so far (on 35.16): the resets at inputs 61, 81, 103 and 138 (Course 1 and `buy:buywj`) are
teleports. Without them goals 3–6 come 18–53 ticks later. `--auto`'s `dropreset` found the reset before goal 15 (input
692) a teleport too: it saves 70 ticks to goal 17. The bench of goals 17–19 on the momentum cell key and `bouncechain`
(4 min, 2 threads per arm, one run each) found no slowdown: 17–20 M ticks simulated per arm, goal 19 at 1716–1728 with
either change, 1760 with neither.

Earlier (2026-09-28, late): the record was `fulltas/VMAN_NONE 45.20`, the second whole-run `polish` sweep over 48.18 (48.54,
48.42, 48.18 and 46.18 were saved before it that evening; the polish cut held ticks before inputs across goals 1–20).
The 09-26 route `fulltas/VMAN_NONE 34.88 stale` ran the same goals along the
same paths on the map, found with the 09-26 engine and turbo; it and the 34.62/34.98 bests now die on Course 1. Its splits were
81/48/49/30/82/105/61/37/30/94/97/91/103/65/165/74/44/103/127/98/9/54/97 ticks, against 46.18's
81/48/48/31/92/105/109/52/76/119/151/135/152/60/234/144/89/181/102/99/21/54/126. A 1x probe of an old route (probes\vman-credits-nocrash.json, the same inputs as 34.88 for its first 958 inputs) replayed
against the judge trace of 48.18 (the player's position and velocity each tick, [Transplant](#transplant)) shows where
the time goes:

- Goals 15–17 (`at:12500` to Course 4's start, the long floor at y 11659): the old route chains straight-down dashes
  onto the floor and jumps out of each landing, so its momentum grows 2528 → 3449 → 3879 → 4800 → 5165 → 5832 and it
  covers 53 and 81 units a tick. The new routes hyper once or twice (momentum 764–2669) and cover 22 and 37. Played from
  the best's dash at x 12147, where the old route dashed the same way at x 12167, the old inputs reach goal 16 33–39
  ticks sooner on this engine, then lose the chain after it.
- `buy:buywj` and Course 2 (goals 4 and 6): the old route bought Course 1's and Course 2's `Clone` boxes on its way
  through (free in a Vman run). Strict buys refuse that, so the new routes go around them.
- Goals 9–13 and the column `at:20200.4,13553.8`: the old route moves about 30 units a tick where the new routes move
  14–21, along the same path. The old inputs from the best's state there reach goal 13 64 ticks sooner (tick 1200 against
  48.18's 1264); the goals after it were being re-found when the transplant was stopped.

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
snaps the camera onto the player (cameraMover's position, lerp and zoom), so it doesn't pan across the map: it takes
the camera back from a camera room that held it, looks down as the restored speed would, and cancels a death's queued
respawn (turbo's restore cancels every invoke).

## Shared goal pieces

Every solve (and `fulltas-plan`) first files every run's saved best in the piece library
(`archive\fulltas-pieces.json`, see [Loop builder](#loop-builder)), with no `"seeds"` entry needed. A best is
`fulltas/<run>.json` with its trace `fulltas-<run>.json` beside it. Each best is played from its own trace's start,
under the trace's goals, on a thread of its own: the game's statics are per thread, and a world load on the solve's
thread would leave its own simulation on the other world. Each goal it reaches, of any kind (area goals and rewards too),
is filed as a piece: its inputs from where the goal before left the player, under the goal's label, the player's unlocks
and that spot. `archive\fulltas-harvested.json` records what was filed, so a best is filed again only when it, its
trace's start or its trace's goals change (`FalseEnding2-Any%`'s 294 goals take about 40 s, counted in the solve's minutes). `--fresh` skips this.

Each search window (reach or polish) is then seeded with the library's way through its goals: from the window's start,
for each of its goals in turn, the fastest piece that reaches it from within 48 units with the same unlocks (as the
builder tries them, restart pads included), until one has none. The log shows `library pieces play goals a-b`. So a
goal another run reaches from the same spot with the same tech (an `at:` label is its box) is played rather than
searched. More tech doesn't count as the same: `VMAN`'s way through its tunnel goals (unlocks 83) isn't tried by
`FalseEnding2-Any%`, which reaches them with an air jump (551). A double jump turns a mid-air jump press into
another jump, so the inputs wouldn't play the same.

## Run seeds

`"seeds": ["<run>", ...]` on a run hands those runs' offline bests (`enginesim/fulltas/<run>.json`) to its solve as
seed routes, once from each goal they reach: the inputs after their goal k, for their goals k+1 on that this run has
too. When the solve's best reaches one of those goals, the seed's inputs are tried from there (as the course seeds
are) and spliced in if the whole run gets better, also when that goal is touched in the dash ticks after its input tick.
Unlocks aren't checked up front: the simulation decides. Routes reused at the same spot with the same unlocks don't need
this ([Shared goal pieces](#shared-goal-pieces)). A seed run replays whole stretches from the goal on, and a run's
start is walked to (below).

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
python tools/plan_fulltas.py --run TrueEnding-ANY% --measure --write
```

`--measure` reads the run's offline best (`enginesim/fulltas/<run>.json`) and its trace (`fulltas-<run>.json`, of that
best or of an earlier best it extends, reaching `reward:bonus1`: **Capture start** first if it isn't) and takes from them the Cash at `reward:bonus1`, the ticks
from there to Course 2's start gate, Course 2's lap (start gate to finish) and restart-to-gate lead, a repeat buy's
ticks and the extra per box row crossed, and Course 2's best run (game timer), which is the clones' path. `--write`
replaces the goals from `reward:bonus1` through `buy:buyds` and keeps the run's goals after it. Rerun it after the
run's Course 2 laps or box walks get faster; a goals edit makes the saved routes stale, so solve again afterwards.

The plan's Course 2 clones follow the pre-2026-09-30 build: a clone spawns from the pool only once `BaseCloneInterval`
(best path ÷ clone count) has passed since the last spawn, runs the path (best run + 1 tick), pays, and is back in the
pool a tick later. The current build spawns into a free slot every frame while the course stays on screen
([Clones and buy-max](#clones-and-buy-max-build-25630570)); the model isn't ported yet. Replaying `TrueEnding-ANY%`'s
buys through it lands within ~6 ticks of the best's buy ticks (`buy:buyds` 3539 planned against 3533 after
`reward:bonus1`).

Its Course 3 (`start:3` through `buy:buydj`, after the shaft goal `at:7645,6050,230,1500`) is planned the same way:

```
python tools/plan_fulltas.py --run TrueEnding-ANY% --measure --dj-farm at:7645,6050,230,1500 --write
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
(`"random": [s0, s1, s2, s3]` in the trace's start state and after every tick), so the turbo judge, the trace and the offline search all start from the same state. The seed is fixed and
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
in `fulltas-splits.json`, each with its `ticks` and `held` frames. The offline search keeps ticks as its clock (splits,
rests, windows, the library) and compares whole runs by live time (`Route.Live`), so what it submits is what the gate
scores; its run file carries `goalHeldFrames` beside `goalPhysicsTicks`. A record named when a run's time was its
ticks alone is renamed to its live time on its next trace (`RememberBestSplits`).

A turbo judge or trace (`TurboRunner.Run`) runs every tick of the route inside one frame on Unity's main thread, so a
long run (a `FalseEnding-Any%` judge, about 19,500 ticks) holds the picture frozen for tens of seconds. Every 250 ms it
looks at the window's message queue without taking a message (`PeekMessage`, `PM_NOREMOVE`), so Windows keeps the
window responsive instead of marking it "Not Responding". It doesn't yield frames: a rendered frame would run
`Movement.Update`'s step-up, coroutines, Invokes, the offline-best pump and HTTP handlers in the middle of the run, and
the judge's state would no longer be the route's alone.

A goals edit (the studio's **Save goals** or `fulltas.json` edited on disk) or a changed `start` renames every saved
route of that run solved for other goals to `<name> stale`: it's out of the record and never played or captured. The
offline best counts only its unchanged leading goals it reaches, and **Play** and **Capture start** use its inputs only
up to the last of them (past it they head for the old goals, or miss the next one). The solver drops a saved best's
inputs past the last unchanged goal the same way.

## Studio: Full-TAS tab

- **Goals** (`POST /api/fulltas/goals {name, goals}`), **Capture start** (`/api/fulltas/capture`: start trace + world,
  seeded with the saved best), **Play** (real time from a new game), **Test best** (`/api/fulltas/play {name, closest:
  true}`: the search's attempt furthest toward the goals, see below; allowed while a search runs), **Improve** /
  **Stop** (`/api/fulltas/solve {name, focus?, threads?, minutes?}`, `/api/fulltas/stop`). `/api/state` has `fullTas`;
  its `solve` has the solver's events (`events`, the latest `status` as `progress`), `error` (didn't start, crashed) and
  the raw `log`; `lastResult` is the last Play or gate check (`kind`, `error`, `reached`, `expected`, ...).
- **Improve** is one button: it runs [`fulltas --auto`](#auto) (the default [parts](#parts-goal-state-library-tail-windows-combine-scheduler-cost-to-go-macros-goals),
  `--window-minutes 0.5`) on the run from its record, until **Stop** (`minutes` default 1440). The plugin writes the saved
  record (`fulltas/<run>_NONE <time>`) to `fulltas-<run>-record.json` and passes it as `--ref`: `--auto` starts from a
  complete `--ref` that beats the engine's run file (a record found and judged elsewhere, as `FalseEnding-Any%`'s 379.40 s
  against its 390.38 s run file). Every new best goes to the gate (`/api/fulltas/submit`), which replays it in turbo and
  saves it when it holds up, as every solve's did. `threads` 0 or missing (the site sends none) is cores − 4, leaving the
  game room to run and judge.
- **Improve selected goals**: click a goal in the goals table (or a goal in the panel's bar, or a segment's bar for its
  goals), shift-click to extend, then **Improve goals a–b** (`focus: [a, b]`, `--focus a-b`): the search's windows all
  start in goals a..b, while combine still splices known faster stretches anywhere. The pick shows as **Improve
  selected: goals a–b** with its time in the record; **Clear selection** (or clicking the same pick again) goes back to
  the whole run.
- A run whose best misses goals can't be improved yet (`--auto` needs a complete route): the button is **Reach missing
  goals** instead (`unsolved: true`, the solver's `--unsolved`, with the seed routes, an hour), which searches for the
  missing goals in order. Either recaptures the trace (seeded with the saved best) when the run's goals or start changed
  since it was taken, or when its header's `startSetup` isn't the plugin's `FullTasStartSetup` (bumped whenever what a
  new game's start state holds changes) or its `format` isn't `FullTasTraceFormat` (bumped whenever the per-tick export
  changes; format 2 drops `MacroEngine._steps`, which made a 437-goal trace 3.8 GB and crashed the solver reading it): a
  search works on the goals in its trace's header.
- The goals table draws a numbered bar down each reset segment's goals (the saved best cut at its Quick Restarts, see
  [Reset segments](#reset-segments); `/api/state` `fullTas.runs[].offline.segments`: `[goals before, goals by its Quick
  Restart, inputs]` each): its number, goals and inputs in its tooltip; clicking it picks its goals. Under each
  segment's goals a strip marks its closing Quick Restart (↻; ⚑ at the route's end). The bar is amber or orange while a buy goal comes from the segment's first goal on (time saved there changes
  the Cash that buy sees) and green or teal after the last buy (time saved there is kept); neighbours alternate between
  the two shades.
- The Improve panel shows the record, this Improve's gain and record count, the goal it is searching now (from the log's
  `auto: pull` lines) and whether it searches the whole run or the picked goals, a bar of every goal (cyan: made faster
  by this Improve, amber: being searched) and a feed in plain words: `Improve started from 6:19.40`, `Improved goals
  181–186: −1.58s` (the goals whose own splits changed, and the gain), the game's replay and save of each, and at the end
  `No improvement found` when it found none. The plugin keeps `--auto`'s bookkeeping events (`model`, `pull`, `check`,
  `joins`, `chaining`, `ablation` and its own `record`) out of `events`: they are in `fulltas/<run>.auto.jsonl`, and the
  capped list keeps the session, the records and the gate checks. Any error gets a card whose **Copy details** puts a
  report (problem, best, events, command, log) on the clipboard.
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
& $exe fulltas $traces\fulltas-main.json --minutes 60 --window-minutes 1 --window 2   # --unsolved, --no-submit, --fresh, --threads N, --goal N, --piece-minutes P, --seed FILE, --seed-list FILE
```

- The best is `<traces>\fulltas\<run>.json` (or the trace's inputs when better or when there is none).
- The studio's **Reach missing goals** writes its seeds (`fulltas-<run>-seed-N.json`) and passes them as one list file
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
  goal (it doesn't search on all night: a different plan, goal list or avoid box is needed). Before its reach windows,
  each new frontier tries the **known tails** (`GraftKnownTails`): the run file's copies beside it
  (`fulltas/<run>.*.json` with their goals and goal ticks: the `.before-repair-*` backups, older bests kept by hand)
  whose goals from some goal i to the end are the run's, spliced as the best's inputs to goal i and theirs from where
  they reached it (shifted -4..+4 inputs), from the frontier and up to 8 goals before it; the furthest (then soonest)
  is kept when the whole run is better. After a goals edit the reach windows stalled in `FalseEnding-Any%`'s ending
  (goals 277-280), which the old route's ending from `buy:buydj` on finished when spliced in by hand. Polishing starts
  once a run reaches every goal.
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
  and the run is kept only if it ends sooner. The same happens when the old tail still reaches every goal but the run
  ends later (its inputs were made for the old state), and for a loop stretch rebuilt faster to its last goal (each
  rebuilt stretch once a solve). A repaired run that is complete but no faster is searched on from the goal where it
  first falls behind the best, as long as that goal moves forward; before this, all of these were dropped.
- `--check <route.json>` replays a route (a run file's `inputs` or an offline best's `ticks`) from the trace's start
  under the trace's goals and prints each goal's physics and input tick, its live time and held frames, and how the
  replay ended; it searches and writes nothing. `--ticks-out FILE` also writes the replay's physics ticks, clone spawns
  and RNG draws as JSON lines, to line up with a 1x probe ([enginesim/README.md](../enginesim/README.md#probe)).
  `--waste-audit <route.json> [--expect a-b,...]` replays a route the same way and prints its waste audit (Parts 8 under
  [Auto](#auto)); `--expect` lists physics tick spans each finding must overlap, and exits 1 when one is missed.
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
  (`reach`/`build`/`polish`), `window` (`kind`: `reach`, `polish`, `repair` or `piece`, goals `first`..`limit`, `target`,
  `budget`; a piece window's `piece` says what it heads for), `status` (each second), `found`,
  `windowEnd`, `record` (`previous`, `best`), `dropped`, `note`, `gate`, `done`. Times are seconds from the run's start.

### Transplant

`--auto`'s `graft` lever (`enginesim\Cli\FullTasTransplant.cs`, [Auto](#auto)) splices another route's inputs into the
best. The other route's own replay gives its path: the player's world position, velocity and facing on each physics tick.

1. For goal k, the best's state after each of its inputs on the way to goal k is matched against that path (position,
   velocity at 1/50, facing at 20 units). At the 8 nearest spots (within 40) the other route's inputs are played on from
   there, 4 offsets either way. The ones that reach goal k sooner than the best go to the solve's merge. It keeps one when
   the whole run gets faster, else repairs the goals after it.
2. The soonest splice is followed on along the other path: where it fails is the first input where it leaves the path
   (more than 6 units from where that route's player was within 3 ticks either way, for 3 ticks in a row: a jump's `+5`
   and a dash's `−20` position writes, pulled back by the frame's lag with interpolation on, put it 5–40 units off for one
   tick and back, so a single tick doesn't count).
3. A local repair in the 24 (then 60) inputs before the failure: every single change (1–3 ticks held, 1–2 dropped, a
   press, release or dash a tick earlier or later, the stick one or two steps along the controller's values: 0,
   ±0.3–0.8 in 0.05 steps, ±1; README Rules 2), then pairs of changes from the best six. Each is played from the state at
   the window's start on lanes of their own (a simulation per thread) up to two goals on or 250 inputs past the failure.
   It has to reach more goals, the last one sooner, or follow the path more ticks; staying alive longer off the path
   isn't a repair. The kept change is replayed from the run's start before the next round. Every goal where a graft got
   there sooner than the best is a *lead*, handed to the merge.

The old route's down-dash landing chain (goals 15-17) doesn't transplant as inputs: its quick restart at the checkpoint
(12094.6, 11673) lands in a jump on the old engine, while from the new best's state the same inputs play a ground hyper
(a restart pressed during a dash is taken only after it), and the old inputs from there reach goal 16 394 ticks after
goal 15. The search finds it as a move instead: `OfflineSolver`'s omni kind `bouncechain` (an ultra, then 2-5 dashes
straight down onto the floor, each jumped out of), and with the omnidirectional dash the cell key keeps momentum at 250,
1000 and 2500 apart (it lumped everything past 750 together).

### Route-corpus grafts

`TechGrafts.Load` adds the saved routes' windows to the tech demos' (`enginesim\Runtime\RouteGrafts.cs`, one hook where
it returns; `routegrafts <traces dir>` lists them). Every `routes.json` route named `<target>_<CAT>[ farm] <time>`
replays from its start trace (`start-c<target>-<caps>.json`, else a `route-c<target>-<caps>-*.json`, loaded canonical as a
solve does); one that doesn't reach its finish offline gives nothing. Each contact (`ContactLog`, the solver's own
detection) gives the window −10..+30 around it (`TechGrafts.Cut`'s: travel without a button dropped), scored by its net
displacement per physics tick from the contact to the window's end. A window's start key is what the run brings: the
unlocks the window spends, the contact kind, the facing, |vx| in 200 u/s bands and rising/level/falling, the dashes and
air jumps left. Identical windows merge (the faster kept); per key those at or above the key's median stay, the fastest
3, a sided one with its mirror. A window needs what it spends (its dashes and air jumps, a wall jump: a press on a wall,
the omnidash: a dash or freeze turn with a vertical, block swap with a dash), never more than its route's category, plus
the category's refill orbs and zip movers. Its tech is `route <target>_<CAT>`: one pick weight per route family, as a
lab demo has, so a new best found by one says `via tech graft route 4_WJ_DS_DJ`. The planner leaves them out (it takes
every window at a contact as an edge). `--no-route-grafts` drops them. Cached in `route-grafts.cache.json` beside the
tech cache (`--tech-cache`'s folder), keyed by the routes' inputs, their trace and world files and the engine build.

Measured 2026-09-30: 63 of the 65 saved routes replay to their finish (Course 15's two have no start trace); 1,410
patterns (mirrors included) from 50 of them (13 repeat another's windows: farm routes with their record's inputs, `1_WJ` with `1_NONE`'s), mined in 14 s. Offered per
category: `NONE` 106, `WJ` 116, `WJ_DS` 307, `WJ_DS_DJ` 520, `WJ_DS_DJ_BS` 680, `WJ_DS2_BS_ZIP_OM` 490,
`WJ_DS_DJ_BS_ORB_ZIP_OM` 1,380 (wall contacts about half, then touches, ground, air jumps and dash jumps, the gate), against
the lab demos' 354. By kind: `WallAhead*` 300, `DoubleJump` 102, `WallAbove*` 194, `Landed` 86, `DashJump` 80, `Spike*`
143, `SteppedUp*` 132, `LeftGround` 64, `WallBoost*` 98, `TouchedWall`/`LeftWall` 88, `NearGate*` 50, `WallBehind*` 38,
`CornerHyper` 10, springs 21, slopes and overhangs 4.

A/B, plain `solve` (the course search that grafts; `solve --auto`'s Full-TAS windows don't graft yet), arms at once, same
threads and minutes, `--no-route-grafts` against the corpus:

| Target, start | Threads × min | Without | With |
|---|---|---|---|
| Course 5 `WJ_DS_DJ`, seed 12.14 | 2 × 15 | **12.00** (splice, explore) | 12.14 |
| Course 5 `WJ_DS_DJ`, seed 12.14, `--rng 1` | 1 × 15 | 12.14 | 12.14 (one tech for the whole corpus: 12.14) |
| Bonus 1 `WJ`, seed 6.90 | 2 × 15 | 6.90 | 6.90 |
| Course 4 `WJ_DS`, no route | 2 × 15 | no finish | no finish |
| Course 4 `WJ_DS_DJ`, first finish (its seed, judged 13.24, doesn't finish offline) | 1 × 15 | 14.82 (15.5 at 592 s) | **14.62** (15.5 at 446 s; one tech: 14.76) |
| Course 4 `WJ_DS_DJ`, first finish, `--rng 2` | 1 × 15 | 14.62 (15.5 at 462 s) | **14.14** (15.5 at 163 s); without Course 4's own routes **14.24** (15.5 at 119 s) |

The corpus helps a search that is still finding the course (Course 4's first finish, also with Course 4's own routes held
out: the other courses' movement transfers), and not the polish of a seeded near-record route: there it takes graft
picks from the lab windows and the runs finish less often (Course 5: 6,895 finishes against 9,767, 3,931 against 7,055).
One best in all runs names a route graft (`4_WJ_DS_DJ` at 26.14 s, early); the rest reach their gains through cells the
grafted runs opened, credited to splice and optimize. Single runs each: noisy.

### Reset study and lookahead

A quick restart throws the player's momentum away. `--reset-study` (`FullTasResets.cs`) takes each reset in the best (only those in the `--focus` goals, when given), in
order, and searches the way on from the input before it with no resets at all (`OfflineSolver.NoResets`: no mutation adds
or moves one, and seeds holding one are dropped; its archive is `<first>-<limit>-<hash>-noreset.json`) through the goal the
reset is in and the two after it. The log's table gives, per reset, the best's and the no-reset way's tick at each of
those goals and the speed (|Velocity + momentum|) 12 ticks after the last: a *teleport* saves ticks there; a *momentum
killer* loses to the no-reset way (sooner, or within 12 ticks and 1.25 times as fast). A no-reset way that is sooner or
faster goes to the merge, which keeps it when the whole run gets faster, else re-finds the goals after it. The study then
stops.

The window ends before the next goal whose own way has a reset (a lap's return to its gate), since the study bans every
reset in it. In a buy goal it is also seeded with the builder's hop ways (`HopWays`, `Stand`'s: a short walk, then hops
under the box), the way back into a box between two of its buys. With the scheduler part on, `--auto` studies each reset
between two buys of the same box once, after the scheduler's windows run out (they never pull the reset study), and
not at a buy with no spare Cash. Studied first, they held `FalseEnding-Any%`'s windows off for 8 minutes for nothing.

`--record-lap K` searches the lap finishing at goal K from where the best's quick restart before it lands (for a lap the
best walks into, a quick restart as the walk touches the gate), scored on the course timer (the course's path time), for
`--minutes`, and writes it to `offline-best` as `<course>_<caps>-<time>-restart.json` for record laps, with its human
version. The goals part runs the same search on its own (see Record laps under [Auto](#auto)). A record lap also tries a
saved route with its first tick on the restart's own tick (its stick only): a saved route starts one settling tick
behind a run's restart landing (Course 2's 2.20 s route dies on its spikes one tick later).

`--lookahead N` makes each polish and repair window search on N goals past its own last goal and scores it there, so a
window whose own goal is reached later but with more speed wins when it gains by the goal after next.

### Auto

```powershell
& $exe fulltas $traces\fulltas-VMAN.json --auto --minutes 75 --window-minutes 0.5 --threads 24 --no-submit   # --parallel K, --probe-minutes p, --repair-minutes r, --repair-cpu-per-tick c, --repair-slack T, --ref FILE, --focus a-b; --minutes 0: the table only
```

`--auto` (`enginesim\Cli\FullTasAuto.cs`) improves a complete route with no one choosing goals or levers, and needs no
reference route. It is the same solve (windows, merge, repair, reset study, the transplant's splices, polish, plan), driven
by a measured gap. The studio's **Improve** runs it ([Studio](#studio-full-tas-tab)).

**Focus.** `--focus a-b` (or `--focus a`) keeps the search on goals a..b: the scheduler's regions are those goals' labels
(numbered within the focus, the loop adds `a − 1` back), the first-round probes and the bandit's arms are goals a..b only,
so every pull's window starts there (a window of goal b still runs to goal b+1). Combine splices only runs of goals that
reach into the focus, and revive only known routes whose lead ends at goal a−1..b: elsewhere their repairs searched goals
the focus keeps out (`--focus 274-283` spent its first 20 minutes on combine at goal 95 and revives at goals 94-125). A
merge still repairs the tail after the window. The `autoStart` event carries `focus: [a, b]` (`[1, goals]`
without it). On `FalseEnding-Any%` with `--focus 181-186` every pull of a 2-minute run was at goals 181-186.

**The gap model**, rebuilt after every new best (the log's per-goal table, and a `model` line in `fulltas/<run>.auto.jsonl`):

- *Top path speed* per unlock set (`PlayerCaps`): the fastest 10-tick path speed (units per physics tick, teleports left
  out: a tick's move longer than twice |Velocity + momentum| × dt + 20) on the best's own path and on speed probes played
  from the best's state at every goal: a dash each way, dash jumps at frames 3/5/7, and with the omnidash the down-dash
  landing chain (`Planner`'s bounce chain edge: 2-6 links, gaps 3/7, jump frames 3/6, both sides). Every later best
  raises it when it goes faster.
- *Lower bound* of goal k: the walkable distance (`DistanceField`, the run's avoid boxes for that goal as walls; an area
  goal never less than the straight line to its rect; a buy or reward goal the straight line to where the best reached
  it) from where the best starts goal k, or from where a teleport in it (a quick restart) lands plus a tick, over the top
  speed for the goal's unlocks. It's an empirical bound (no route has shown a faster speed with those unlocks), not a
  proof; `gap` = split − bound. A first replay grows the field's grid over the whole route, so one measured pass reads
  one grid (the grid coarsens as it grows).
- *Speed deficit*: along the best, the progress toward the goal's target each tick against the 80th percentile of what it
  makes in that goal; a stretch of 6 or more ticks under 35% of that is flagged, with the ticks it would save, as
  `reset`, `loops back`, `standing/wait` or `slow`.
- *Known*: every route the loop has seen (below): each goal's soonest arrival and shortest split among them.
- *Economy goals* (a Full-TAS run's laps and economy-box buys, `FullTasGoal.IsEconomy`): cash, not distance, sets their
  time, so they get no deficit and no gap. Their *slack* is the laps the best's spare cash covers: a lap (a course's start
  gate and finish back to back, or a finish alone) earns the course reward and the clones' income over its ticks, and
  it could go if every later tick still held that much cash (every later buy still paid). Laps are dropped most ticks
  per cash first, each drop lowering the cash after it; a dropped lap's slack is its ticks less the straight walk at top
  speed across it. On `FalseEnding-Any%` (364.22 s) it is 0: every lap's cash is spent before the next one ends.
- *Spare Cash* of a buy: the ticks before the best buys it that it already held the price (its Cash drop there). A buy
  reached sooner than that waits for its Cash, so the movement headroom (known lead, deficit, gap) of the goals since the
  buy before is held, between them, to its spare Cash. A price reached on a goal's own tick (a lap's finish, a reward)
  came from the player, not the clones, and leaves the stretch unbound; so do the goals after the last buy (the
  ending). On `FalseEnding-Any%` (361.54 s) `buywj` has 0 spare ticks, `buystatue` 1, `buyds` and `buydj` 7: the
  expected headroom went from 908 to 392 ticks, `buydj`'s climb from 139 to 7.
- *Expected* headroom of goal k = 0.6 × (the known routes' lead there: a shorter split, or an arrival lead the goal
  itself made, i.e. its lead less the previous goal's, so a lead carried through later goals counts once) + 0.3 ×
  (deficit + slack) + 0.05 × gap (not for economy goals) + 0.5 × the waste audit's ticks there (`--parts waste`), its
  movement part held to the spare Cash above. Goals rank by
  it. The outlook's floor is the best less every searched goal's gap within the spare Cash (an economy goal's slack
  instead); the repair's hopeless test keeps the distance bounds.

**The bandit.** An arm is a lever at a goal: `polish` (Polish.cs over goal k's inputs only, 3 sweeps), `window` (goals
k..k+1 from goal k−1's state), `lookahead` (goals k..k+2, scored at the goal after next: a way that reaches goal k later
with more speed can win), `span` (a long window from goal k scored at the first small goal 3-5 goals on: a buy, a
gate or an area rect of at most 300×300; scored at a large area box a window wins by touching its near edge in a state the
next goals can't use, as Vman4 saw on `VMAN` goals 10-14 and 15-18; twice a window's budget), `dropreset` (the reset study of the next quick restart in goal k not studied yet), `graft` (a
known route that does goal k better: reached sooner, its way there kept and the later goals re-found as the transplant's
bridges do; a shorter split, its inputs picked up from the best's states nearest its path by position, velocity and
facing, the transplant's splices), `plan` (the contact planner on goal k's window, the best's window as its guide; a
goal whose plan finds no finish is dropped from the plan lever), and `waste` (a window from goal k to the goal after its
last waste finding, Parts 8, seeded with each finding undone; prior 0.5 × the audit's ticks there). First every goal label (a lap phase: every
`buy:buyclone2` is one) gets one window probe of `--probe-minutes` (half of `--window-minutes`) at its goal with the most
expected headroom, most expected first (one per goal took longer than 10 minutes on `FalseEnding-Any%`'s 300 goals). Then each pull takes the arm with the most expected
ticks per CPU second, (lever share × expected + ticks gained) / (the lever's CPU seconds per pull + CPU seconds spent on
the arm; a pull's cost is its budget's CPU seconds counted as one pull, averaged with every pull the lever made, so one
that ended early doesn't make a lever look free): an arm that pays keeps winning, one that doesn't fades as its CPU grows. Lever shares (from the `VMAN` work: the goal 11 gain came from a window scored at goal 14's small box, windows scored
at large area boxes found arrivals the next goals couldn't use, and the early resets proved to be teleports): polish
0.15, window 0.2, lookahead and plan 0.3, span 0.45, dropreset 0.15, graft 0.6 (of the proven lead: a known route's lead at a goal a graft or merge has already followed, and not kept, is
no longer proven). A window arm pulled again searches
longer (`--window-minutes` × pulls, up to 4×; its archive resumes it), a plan pull 1.5× that and at least 2 minutes. A
graft tries each known route's source once per goal (a window pull's finds are one source, and the goal its merge already
repaired from is skipped). CPU is the process's
(`Process.TotalProcessorTime`).

**Every window gain gets its tail repaired**: the merge splices it with the old tail and, where the tail no longer
follows on, re-finds the later goals from the new state (`Repair`). In `--auto` a repair gets `--repair-cpu-per-tick`
(15) CPU seconds per tick of the lead it chases, its windows what is left of that on every thread (at most
`--repair-minutes`), and is dropped once it can't win: its last goal's tick plus the later goals' lower bounds is no
sooner than the best's end, it falls behind the best at a goal (`--repair-slack`, default 0), or its budget is spent.
Window arms run side by side: the best non-overlapping window arms, up to `--parallel` (threads / 4) at once, each an
`OfflineSolver` on its share of the threads from the same best; they are merged latest window first, so each later
gain lands on an unchanged prefix. Polish, graft, reset study and plan pulls run alone on every thread. Checking a spliced tail is one replay (about 2 × 10³ ticks)
against a window's 10⁶-10⁷, so the loop doesn't need polish's route sync there.

**Self-harvest.** Every route the loop sees is filed as a reference: the window finishes faster than the best through
their window (the five fastest of a window), each partial run a repair reaches, plan finishes, polish results, graft leads,
every checked best and the starting best. They give the model's *known* column and the graft lever's candidates, so a
later goal can graft from what an earlier pull found. The useful ones (3 soonest arrivals and 3 shortest splits per goal)
are kept in `fulltas/<run>.auto-refs.json` and read again by the next `--auto` (not with `--fresh`). `--ref FILE` adds a
route that replays on this engine; a complete one faster than the best (the run file, or the trace's inputs) is the loop's
start and is written to the run file (`auto: --ref ... beats the best`). The studio passes the saved record this way, which
the run file trails when that record was found and judged elsewhere.

**Memory.** What the run's sessions tried is kept in `fulltas/<run>.auto-memory.json` (not read with `--fresh`) and read
back when the goals match, so a restarted Improve goes on where the last one stopped: the combine splices played, the
known routes revived (at most twice each), the goal edits played (each is its whole spliced route, so it plays the same
way again; a goals pull with none new is skipped), the resets studied, the goals whose plan found no finish, the leads
grafted, and the bandit's arms, lever costs and per-goal pulls (the outlook's "not tried yet on this run"). On the same
best it also keeps the finds merged, the goals polished and the leads each window showed. Before it, six restarts of
`FalseEnding-Any%` at 361.54 s spent 43 of their 79 minutes of pulls replaying the same combine, revive, goals and
reset-study pulls. A window's finds are merged once per best by the state and tick they arrive in at the window's last
goal, not by their inputs: a resumed window re-found goal 116 at 8723 with other inputs after it, and its three repairs
took 4,600 CPU s.

**Every new best** is replayed from the start on a new simulation on its own thread before it is kept (`VerifyFresh`, the
`--check` replay: the engine can play a route differently after other runs on the same simulation) and kept as that replay
has it, only if it still beats the best. Then it is merged, written (`fulltas/<run>.json`, `offline-best/`), and logged:
`auto: record` with the lever, the goal and the splits that changed, a `record` line in the jsonl (time, ticks, all
splits, wall and CPU seconds since the start).

The log ends with the time-to-gain curve (wall and CPU seconds to each new best, with the lever and goal), each lever's
pulls, CPU and ticks gained, and the pull order. `fulltas/<run>.auto.jsonl` has every pull (`pull`: lever, goal, budget,
outcome, gain, CPU, wall), `check`, `record`, `model` and `autoEnd` (the curve).

**The outlook** (`FullTasAutoOutlook.cs`): after every pull, an `outlook` event on stdout only, which the studio shows as
"Should it keep going?" (the Optimize tab for a course, the Full-TAS tab's Improve for a run). The engine also prints a
`pullStart` event as each pull starts: lever, goal, budget, what that lever and goal gave this session, its misses in a
row there (`misses`), and `history`: how many pulls of that lever after as many misses at their spot (5+ counted
together) gained, over every `.auto.jsonl` in the folder.

The chance of a gain rests on the other Improve runs in the same folder (`course-auto/fulltas` for courses, `fulltas`
for Full-TAS runs): their dry spells, the search from a start or a gain to the next gain or to the run's end. With g the
search since this target's last gain (every earlier session counted; since the start with none), their dry spells longer
than g hold k gains in E seconds of search past g. Gains are a Poisson process whose rate has a gamma prior of shape
(k + ½)·P/(E + P) over P = 1800 s, updated with this target's gains and search, each weighted e^(−age/(0.3 × all
search)). With shape a and scale b, P(a gain within t more) = 1 − (b/(b+t))^a and the median wait is b(2^(1/a) − 1).
The event's `basis` has the numbers (`runs`, `search`, `gains`, `spell` = g, `spellGains` = k, `spellSearch` = E).

The model it replaced (a Poisson rate over the recent half of this target's own search, Jeffreys prior) read 20 s of
search as "92% an hour" and one early gain as "100%". Backtest over the 55 recorded `.auto.jsonl` (2026-09-30), a
checkpoint every 30 s of search, each run scored against the others, Brier score of "a gain within the next H" (lower is
better; always guessing the base rate scores p(1−p)):

| runs | H | old | new | base rate |
|---|---|---|---|---|
| courses (0 gains) | 5 min | 0.273 | 0.002 | 0 |
| courses | 10 min | 0.419 | 0.005 | 0 |
| Full-TAS | 5 min | 0.235 | 0.229 | 0.248 |
| Full-TAS | 10 min | 0.189 | 0.178 | 0.231 |
| Full-TAS | 20 min | 0.167 | 0.139 | 0.183 |

Full-TAS at 10 min, binned by the new chance: 0–20% gained 23% of the time, 20–40% 21%, 40–60% 60%, 60–80% 78%,
80–100% 87%. In the same logs `polish` never gained (0 of 1448 pulls) and course Improves gained once in 1371 pulls;
a window pull at a spot that had missed 3 times or more gained 1 of 294 times.

`verdict`:

- `floor`: the best is at the model's floor (every searched goal at its bound).
- `exhausted`: the bandit has no arm left.
- `likely`, `possible` or `unlikely`: a chance of a gain in the next hour of ≥ 60%, ≥ 25%, or less. The first advice line
  gives the median wait and the chance within the run's remaining minutes.

With `unlikely` or `exhausted`, `advice` names the goal with the most expected headroom (when 3 or more are searched),
the levers and goals this session hasn't pulled, and a `--focus` that left goals out. The Optimize matrix's queue rows
end with the verdict.

#### Parts: goal-state library, tail windows, combine, scheduler, cost-to-go, macros, goals

```powershell
& $exe fulltas $traces\fulltas-FalseEnding-Any%.json --auto --minutes 10 --window-minutes 0.5 --threads 16 --no-submit   # --parts LIST, --tail-minutes t, --combine-repair-minutes m, --library-runs R (150)
```

`--parts LIST` picks the loop's parts, comma-separated: `lib`, `tail`, `combine`, `sched`, `ctg`, `macros`, `stray`, `goals`, `idle`,
`wallreset`, `waste`, `grafts` (`tail`, `combine`, `ctg`, `macros` and `stray` need `lib`); `--parts none` is the loop above as it was (the measured
baseline). The default is set by the measurements below, and has every part except `wallreset` (opt-in: add it to the list).

1. **Goal-state library** (`lib`, `enginesim\Cli\FullTasAutoLib.cs`, contracts in `FullTasAutoContracts.cs`). Every run the loop
   plays from the start (the starting best, each window find spliced with the old tail, every stitched or waited run, every
   new best, the references read back) is traced once (0.5 CPU s for `FalseEnding-Any%`'s 17,858 inputs) and files a state at
   every goal touch and chain point: the tick after a quick restart, a landing, a wall contact, a dash's end. A state carries
   the input count that gives it back from the start (the snapshot handle), its features (place, velocity, momentum, dashes
   and jumps left, facing, ground, the buys hash, Cash) and two keys: the **full digest** (`Simulation.Fingerprint`: the
   player, every course, the economy with its Cash, box levels and clones, the RNG, the goal tracker, pending invokes and
   coroutines, touched scripts and toggles, the rebases; `dashCooldown`, `wallCoyoteFrames` and `framesSinceLastDash` taken
   at the threshold the game compares them with) plus the input engine's pending press state, and the **player key** (the
   same without the world). Joins are made only on the full digest; the best known route is the shortest path through those
   joins, and a stitched path is kept only after a replay from the start reproduces it (a join that doesn't is banned).
   Runs are capped (`--library-runs`), keeping every run that holds one of the 3 soonest or 3 fastest states of some
   (goal, point kind).

   What joins on `FalseEnding-Any%` (10 min, 28 runs): 1,699 join points on the full digest against 2,063 on the player
   alone, and the same 1,699 with the Box2D world digest added (a since-removed `--join-physics`). Every one is a state two
   runs share because their inputs are the same up to there. None joins two runs at different ticks: the clones' Cash runs
   on the clock, so two runs reach the same whole state only on the same tick, and the shortest path never beat the best.
   With the player key alone (the first build) the path found 382.56 s predicted and replayed 194 of 300 goals: a join at
   goals 183-185 left the Cash different and the route bought Course 3's `CashPerLoop` box on a later walk. So on this run
   the library's gains come from the two things around it:

   - **Extend to the end.** A window find is spliced with the old tail and replayed to the end; a complete, faster run is
     kept (the fresh check still replays it on a new simulation).
   - **The wait fix.** A spliced run that is ahead and then misses a later buy (its Cash comes on the old route's ticks, and
     the clones' rolls re-time) gets w idle ticks before the quick restart ahead of that goal, the fewest that get it past
     (binary search from a snapshot at the restart); the restart puts the player back as the old route had it. Up to 6
     waits. Window 128-129's 218-tick find needed 13 idle ticks at input 13930 to pass goal 247 (`buy:buyglobalreward3`).
     When the widest wait fails, every wait of 1-40 idle ticks is tried: a stray buy (a re-timed walk standing in a
     neighbouring box when its 0.2 s cooldown ends) isn't monotone in the wait. It rescued goal 245 (15-20 idle ticks); it
     can't rescue the course 3 stray buys at goals 193-195 and 248-252, where waiting in the box is what buys it.
   - **Route-sync against every library run.** When the tail still breaks: wherever after the change the player is exactly
     as on another library run (the player key), that run's inputs from there; then, at the first goals after the change,
     the tails other complete runs play from that goal (at most 16 replays). Only then the goal-by-goal repair.
   - **Tail windows** (`tail`). A broken splice still ahead of the best at its last goal p re-searches goals p+1..p+3 as one
     window from its own state there (every thread, `--tail-minutes`, default `--window-minutes`; seeds: the best's and its
     own inputs from there), scored at goal p+3 against the best; each find is spliced with the best's tail, replayed, wait-fixed
     and route-synced, up to 3 windows while it stays ahead. On a strict run a way that stray-buys aborts, so this window is
     also the detour around the stray box.
   - **Combine** (`combine`). At the start and after every new best: every known route (the references: window finds spliced
     to the end, repairs' partial runs, old bests, `--ref` routes) where its lead over the best (the best's arrival minus its
     own) climbs from a low at goal a-1 to a high at goal b (at least 2 ticks; each route's 4 largest) is spliced in: the
     best's inputs to goal a-1, that route's to its goal b, the best's after its goal b; replayed from the start, wait-fixed,
     kept when the whole run is faster (16 splices a pass). Gains that separate searches found at different goals join:
     380.52 s + the 380.62 s route's goals 275-278 made 380.00 s (17 s, 3 CPU s), and 380.00 s + the 380.06 s route's goals
     186-266 made 379.40 s (13 s, 3 CPU s).
     A stretch is played as spliced only where the best and the route have the same full digest (StateKey + WorldDigest,
     the economy included) at the splice-in. Where they differ (a route that branched off an older best: before, every
     splice of a 381.14 s-era route onto 379.40 s failed at once at goal 186), goals a..a+1 are re-searched as a tail
     window from the best's state (`--combine-repair-minutes`, default a third of `--tail-minutes`; seeded with the route's
     inputs), followed by the route's stretch and the best's tail; at most 4 repairs a pass. The wait fix then runs against
     that route-then-best-tail. Measured: on the known routes 12 of 16 splices shared the splice-in state and all 4 repairs
     got past it; live (2 threads) a repaired splice of goals 152-155 from the 378.46 s route onto 377.80 s made 376.76 s
     (52 ticks, 43 CPU s), and goals 159-183 from an old arm's find made 376.70 s.
   - **Stray buys** (`stray`). A strict run that dies on a stray buy of box B (bought while the player crosses it, cooldown
     over and the Cash there) gets three more levers. **Re-timing** (inside the wait fix, when no wait at the last restart
     cures it or there is no restart after the change): at one quick restart, from the last one before the change (the
     whole stretch moves) through the 5 nearest before the stray, the idle ticks before it are cut or 1-40 added; the fewest
     ticks that pass goal g win. **Avoid**: its tail windows get B's trigger (grown by the player's colliders) as a temporary
     avoid box, unless a goal in the window buys B. **Revive**: at the start and after every new best, the 3 known routes
     with the largest lead over the best at their last goal (each at most twice) are spliced with the best's tail and
     rescued as a merge would be (wait fix with re-timing, then tail windows).

     Measured (a since-removed `--stray-study`) on the known routes that were ahead of the best at their last goal: from
     378.94 s, 2 threads, `--tail-minutes 0.15`, 86 such routes (this run's finds and the Integrator arms' refs): 52 died on
     a stray buy, 29 on a death, 5 otherwise. Of the 52 (whole runs / faster than the best / ticks summed / CPU s): plain
     wait fix 0/0/0/20; wait fix with re-timing 17/3/8/240; tail window 3/3/99/382; tail window with the avoid box 0/0/0/342;
     all together (as MergeAuto plays them) 20/9/120/597. The avoid box alone saved none, so its share in "all together" is
     unmeasured (`--study-levers all,all-avoid` in a scratch build separates it). Two rescues were new records: an A1 find
     51 ticks ahead at goal 137 (378.28 s) and one 108 ticks ahead at goal 152 (377.80 s).

   Every kept best logs the **chaining check**: goal touches crossed at |Velocity + momentum| ≥ 600 u/s on the touch tick
   against the starting record, and which goals changed (a diagnostic, not a gate; `--check ROUTE --chain-vs OLD` prints it
   for any two routes). The speed probes are kept in `fulltas/<run>.auto-speeds.json` per start state and goals, so a restart
   skips them (a minute and a half on `FalseEnding-Any%`).
2. **Cost-to-go** (`ctg`, `FullTasAutoCostToGo.cs`): estimated ticks from a goal arrival to the end, k nearest
   library states with a known way to the end. Refit at the start and after each new best; a repair whose partial run's
   arrival plus cost-to-go ends more than 10 ticks after the best is dropped, and so is a window's broken find before its
   wait fix, tail windows and repair (`auto: cost-to-go dropped N of M broken finds`; a window whose finds all drop feeds
   the scheduler no lead). On Course 4 this dropped a goal 6 find 10 ticks ahead whose arrival state ends at 714 against
   664, where the repair it replaced spent 55 s. A resumed window's finds already merged onto the same best aren't merged
   again, and a goal polished on the current best isn't polished again until the best changes (VMAN: 444 identical
   polish pulls, 3,400 CPU s).
3. **Scheduler** (`sched`, `FullTasAutoScheduler.cs`): successive halving over label regions (one per
   distinct goal label: the lap phases of a repeated run). A region's pull searches one of its goals (most expected first,
   the next one each pull) as a window of it and the goal after; its waves replace the first-round probes and the bandit
   while it has pulls; each pull is reported with the whole run's gain and, when that is 0, the window's own lead at its
   last goal.
4. **Macros** (`macros`, `FullTasAutoMacros.cs`): patterns mined from the library's good routes, played
   from a window's start state (its anchor contact and unlocks) as up to 5 extra seeds. Mining runs on its own thread: on
   the loop's thread it left the loop's simulation replaying the best to no goal at all.
5. **Goals** (`goals`, `FullTasAutoGoals.cs`): edits of the run's goal list. Every stretch of a Full-TAS economy is
   Cash-bound at its target buy, so a lap or walk made faster only turns into a wait there; what pays is a list that needs
   fewer laps. A goals pull runs at the start, at once after it adopted an edit, and otherwise after a new best at most
   every 5 minutes; it plays every edit of the best on a copy, in parallel:
   - **Drops**: the last lap (start and finish goals, the inputs from the quick restart before the gate to the next step's)
     of each run of back-to-back laps of a course, and its last two.
   - **Adds**: 1..N more laps of a course in place of a wait (a lap earns its reward, standing earns nothing): n copies of
     the nearest lap before an idle stretch of 20 ticks or more (its quick restart to its finish) are put in at that lap's
     finish, and the idle stretch, up to the next lap goal of any course and on either side of the quick restart, is cut
     out; the goals after it put back only the idle ticks they miss. n runs while the laps take fewer ticks than the wait
     (n laps of L ticks, wait W, n * L < W; a run that takes as many ticks or more can't be shorter, and only a shorter run
     is adopted). On `FalseEnding-Any%` that is the 112 idle ticks after Course 1's last lap (goal 85, a 107-tick lap)
     before the quick restart to `buy:buywj`: N = 1, at most 5 ticks. Its other waits fit no lap (Course 1's 25 ticks at
     goal 51; Course 3's 22-34 ticks between buys, a Course 3 lap being 185-204), and the wait before `buy:buydj` isn't
     idle ticks (a walk of 480 inputs with loops in it). The lap offered plays 5 ticks ahead at every goal from
     `buy:buywj` to goal 240 of 284 with no wait put back (`--check`), then its extra Cash buys a Course 3 box early on
     the walk to goal 241 (`buy:buyglobalreward3`, a stray buy) and no wait passes it. An add whose partial run stops
     ahead of the best (`AddAhead`) is therefore re-found as an investment, as an inserted record lap is (the two that
     get furthest, 150 CPU s a goal left): from the 360.17 s best, `--parts goals` on 8 threads re-found goals 241-284
     in 463 s wall (3545 CPU s) and adopted 284 goals at 358.69 s (17202 ticks, -74; the fresh check kept it, and
     `--check` of the route under the 284-goal list reaches every goal).
     A wait after a later quick restart gets the laps at that restart too (it lands at the course's start: after the
     statue's Course 2 buys the restart lands at the top, by Course 3's gate). A buy reached as its Cash comes waits by
     walking, not standing: the climb to `buy:buydj` is 605 ticks against a bound of 145 with 17 ticks of spare Cash (the
     model's `Gap` and `Spare`, [Auto](#auto)), and has no loop in it to cut (no two inputs 20 or more apart where the
     player stands within 16 units at the same speed). So a Cash-bound buy (spare under its gap) gets n laps (n * L under
     the gap) at the lap's finish (the buy right after it) or at the last quick restart before it, nothing cut, and the
     goals from the buy on re-found as an investment from the laps' finish (`GoalEdit.Refind`; a buy the laps' walk
     passes after its Cash came is a stray: 3 Course 3 laps at the top bought `buydj` on the way). On the 357.08 s best
     (278 goals): 1-6 Course 2 laps in place of the climb, 3.2 min on 8 threads to re-find the 11 goals from `buydj`
     (1650 CPU s): 5 laps re-found 363.10 s, and the replan dropped them back to 1 (357.46 s, then 357.25 s, +16 ticks);
     on 357.73 s the same to +9. A Course 2 lap earned about what the clones pay while standing: none paid (on the
     pre-2026-09-30 build).
   - **Regroups**: two single buys after a buy of box x, as one `buy:a+b` goal played with the buy table's measured way
     from x's spot (`fulltas/<run>.buytable.json`, [Buy table](#buy-table); either order it has, so a reorder too).
   - **Record laps**: a lap replaced by a quick restart, 0-40 idle ticks and a faster lap played from there: the saved
     course routes of that course (`offline-best` and `offline-best\judged` beside the trace, the 12 fastest of each
     unlock set the player holds there, fastest first, until 4 play from the restart) and the best's own laps of the
     course (the 6 shortest), when it leaves the course a faster best time: the clones' path, so every clone pays more
     from then on, and the clones in flight pay at the finish (a record break,
     [Clones and buy-max](#clones-and-buy-max-build-25630570)). Each spot offers two laps when they differ: the one
     that leaves the fastest best time, and the one that finishes soonest while still beating it (a faster time can cost
     ticks: `2_wjds-1.50-restart` crosses the
     line 9 ticks after the best's own 1.68 s lap from the same restart). Tried at the course's last lap before its
     first clone buy, its first lap after one, its first lap after each unlock buy (new unlocks, a faster way), and
     every lap the best walks into (no quick restart before the gate): the walk is kept to the gate and a quick restart
     goes 0-3 inputs after it, the fastest and the soonest of those kept.
     `FalseEnding-Any%`'s first Course 2 lap with the dash walked in from the boxes in 103 ticks; its next lap, from a
     quick restart, took 84. Each is offered alone, with the same lap in place of each slower lap right after it (the
     visit's back-to-back laps), and each of those with the last 1-3 laps of the course's last run of laps before the
     next goal that isn't economy dropped. A course and unlock set with no `-restart` lap saved gets one searched first
     (the `--record-lap` search from its earliest spot, `--record-search-minutes`, default 2, on every thread; 0: off),
     once per run (the memory's `lapSearched`), and its spots are played again with it. The lap found is sent to the
     game as a course route too (not with `--no-submit`): the run's quick restart lands where the category starts, so
     it is judged as `<course>_<CAT>` and saved when it's a record (`2_wjds-1.50-restart` became `2_WJ_DS 1.50` over
     1.62).
   - **Inserted record laps**: `start:N finish:N` right after an unlock buy, played as a quick restart there, 0-40 idle
     ticks and the course's fastest lap from where it lands (saved routes and the best's own laps, when it beats the
     course's best time), for every course the run laps after the buy. The clones then run that path through everything
     after: `FalseEnding-Any%`'s Course 2 lap with the dash right after `buy:buyds`, before the run leaves Course 2, not
     on the drop back after the statue. Each is offered alone and with the course's next visit dropped (its back-to-back
     laps first after the next unlock buy, the last one with or without a quick restart after it, cut from the goal
     before them to the quick restart that ended them, or to a quick restart in place of the last finish: after the
     statue's Course 2 buys the restart lands at the top, by the shaft to `buy:buydj`; both post-statue laps go, the
     second climbing to `buydj` from its finish). The best's way on starts at the buy's box, not the lap's finish, so none
     plays as spliced: they are re-found as **investments**, InvestTries (2) of each kind (inserts, adds ahead, walk adds,
     payout breaks), furthest (then soonest) first. From one pool of two, the two inserts took both, and every edit a pull
     plays is marked played (memory `edits`), so an add or payout break behind them was never re-found; an edit still
     waiting for its investment (the pull adopted a drop first, or it ranked past InvestTries) isn't marked, and the next
     pull offers it again. The edit's goals become the run's for it, the goals after where its partial run stops are
     repaired as a window's are (the old best's inputs as seeds), and falling behind the best doesn't end it (an
     investment is behind until its income pays; the model's bounds and cost-to-go are the old goals', and the repair
     doesn't search again from where a complete run falls behind): only its CPU budget does (150 CPU s a goal left), or
     the whole run then not beating the best (the fresh check, as every best). One re-found to the end but no faster is
     **replanned**: the goals after its change get the goals part's drops and adds on its route (its own model for the
     walk adds), the fastest that beats the investment becomes it and is planned again; with none played through faster,
     its adds and the drops that stop ahead of it (the extra Cash strays into a box the old buy walk only brushed) are
     re-found as investments of their own, held to the best, the fastest that beats the investment becoming it. The
     goals change back when none is kept. First run on the 361.68 s best: the Course 2 lap after `buyds` cost 121 ticks
     at its finish; each repair reached goal 250 of 284 on a 60 CPU s budget, which is why it's 150 now. At 150 both
     complete, in a fraction of it: on the 357.73 s best (280 goals) the insert re-found goals 177-282 in 11.6 min on 8
     threads (361.10 s, +141 ticks) and with the post-statue laps dropped goals 177-278 in 7.3 min (360.02 s, +102); on
     the 357.08 s best (278 goals) 9.5 min (359.99 s, +118). Their replans: the insert alone drops one post-statue
     Course 2 lap (359.13 s, +55 on 357.73); every Course 3 lap drop after it misses a buy (the lap's Cash comes sooner,
     the old buy walk strays into `CloneCount`, `CloneMult`, `CashPerLoop` or `FastCloneChance`, and no wait passes it);
     with the laps dropped the restart to the top waits 829 ticks for `buydj` (bound 133, 67 spare), and 3 Course 3 laps
     there bought `buydj` on the way (a stray).
   - **Payout breaks** (`FullTasAutoPayout.cs`, the Tech tab's *Record-break payout*): a finish that beats the course's
     best time replaces the clone path, and every clone on the old path pays on the spot (`clonesScript.updateClonePath`;
     what a break and a ladder of them pay on the current build: [Clones and buy-max](#clones-and-buy-max-build-25630570)).
     A lap at the best time without beating it pays nothing extra. This edit plays every earlier lap that set or tied the
     best time (back to the last prestige that reset it, at most 40) one tick slower, so a later lap f breaks the record
     and the clones in flight pay there, up to one best time sooner. A lap f slower than the best time plays the lap that
     set it again (both from a quick restart): the record held back to f. Each slowed lap is its own lap with one tick
     added that holds the input before it (no new press), put in at each point after the gate: of those whose course
     timer comes out slower, the one that finishes soonest. Offered at the last lap of each run of back-to-back laps, at
     the one or two laps before it with the laps after it dropped, and at the last with the last lap of one of the next
     three runs of laps dropped; the 48 with the fewest slowed laps play. A break that doesn't play through spliced is
     re-found as an investment, as an inserted record lap is, when it is behind the best by less than the best time it
     breaks where its old way on stops (the clones' Cash comes forward by at most that). Adopted edits stack: the next
     pull can break again one lap earlier (the slowed laps one more tick slower), so a course climbs down a ladder of
     best times, one payout each. On `FalseEnding-Any%` (359.46 s, 2026-09-30, the pre-update build, where a faster
     path raised the income less) none paid: its only breaks are Course 1's
     1.40 s lap (goals 22-23) or Course 3's 3.80 s lap (goals 193-194) held back, which costs 101-155 ticks (the record
     lap plays twice and the clones run the slower path in between), the earlier Cash makes the walks through the box
     rows buy a box early, and the two re-found as investments (142 and 166 ticks behind) came out no faster. Not yet
     measured on the current build, where a break gains for good only while its course still holds `Start`'s spawn
     timer reserve ([Clones and buy-max](#clones-and-buy-max-build-25630570)).
     **Ladders** (`FullTasAutoLadder.cs`): K = 2-8 laps back to back, the course's fastest lap from the quick restart
     (T ticks on the course timer) played T+K-1, ..., T+1, T, so each finish breaks the record the one before set. A lap
     is made exactly k ticks slower by k ticks that hold the input before them (no new press) after the gate touch, of
     every point the one that finishes soonest; a k no point gives is split over two or more. Each lap is built where the
     one before leaves the player (from a finish's restart Course 3's 190-tick lap plays as 194, so T rises to 194).
     Offered in place of each run of back-to-back laps (all of it, or its last K), right after each unlock buy and at the
     last quick restart before one; when T+K-1 doesn't beat the best time there, the laps that set it are held back to
     exactly T+K and the waits after them re-timed (`Retune`) first. The waits from the ladder on are re-timed too; one
     that doesn't play through is re-found as an investment. Only where the course has clones and keeps the reserve, or
     right before a Cash-bound buy. `fulltas <trace> --ladder-study C --k K` (0: every K) prints each spot's laps (course
     time, finish, held ticks) and each ladder played whole, with every finish's course time and pay.

   An edit that misses a goal after the change gets idle ticks before the last quick restart ahead of that goal (every 5
   up to its lead there plus 60, then each below the first that passes: a stray buy isn't monotone in the wait), up to 6
   times. The fastest edit that reaches every goal and beats the best is adopted, never one that drops or reorders the
   end goal, an area or reward goal or an unlock buy (`FullTasGoal.EditRefused`). Adopting: the trace's header with the
   new list makes every simulation from then on, the fresh check keeps the route or the old goals stay, and the loop's
   goal-numbered state (references, arms, library, model, parts) starts again from the new best. The run file, the
   offline best and the gate submission carry the list (`goals`) and the lists it came from (`goalsFrom`, oldest first);
   a later `--auto` on the same trace resumes on them when that route is the faster one. The game judges such a route
   under its own goals, only when one of its `goalsFrom` lists is the run's
   list in `fulltas.json` as it is now and the edit keeps those goals (`EditRefused` again); when it saves it as the
   record, the list becomes the run's (`fulltas.json` is kept first as `fulltas.json.<yyyyMMdd-HHmmss>.bak`, and the old
   list's routes retire as after a goals edit in the studio). The studio feed shows the adopted edit as the record's
   title; `fulltas/<run>.auto.jsonl` has every edit tried (`goalEdit`) and every adopted list (`goals`). Off with
   `--course` and `--focus`.

   Measured, `--parts goals` alone on 2 threads from the judged 375.52 s (300 goals), the record FE360 then took by hand
   to 373.90 s (294 goals: a record lap and three lap drops, measured one by one with `--check`): pull 1 (45 edits, 29 s,
   45 CPU s) adopted a record Course 1 lap at goals 25-26 (the human `1_none-1.42` route from the quick restart: best time
   1.62 → 1.42 s) with 2 Course 1 laps dropped (goals 88-91), 373.58 s at 38 s. Then, each pull 23 s and 33-35 CPU s,
   one lap each: Course 1 goals 89-90 (371.86 s, 62 s), Course 2 goals 282-283 (371.62 s, 85 s), Course 1 goals 50-51
   (371.42 s, 107 s, 290 goals); pull 5 found nothing more. The two regroups the Course 2 buy table offered gained 2
   ticks and 0.

   From the 369.84 s `--auto` best of FE360's 294 goals (same settings): pull 1 (34 edits, 29 s, 33 CPU s) dropped the
   Course 1 lap at goals 89-90, 367.46 s at 87 s (FE360 found the same drop by hand at the same time); then a Course 2
   lap (367.32 s), the regroup `buy:buyclonemult2+buyclone2` at goals 110-111 (367.28 s) and a Course 3 lap (367.26 s,
   287 goals, 170 s); pull 5 found nothing more. The game judged the 367.26 s route in turbo from its submission (its
   `goalsFrom` held the game's 292-goal list), saved it as the record and made its 287 goals the run's, keeping
   `fulltas.json.20260929-063956.bak`.

   Measured on private copies (2026-09-30, `--parts goals`, 8 threads), the drops the pull after the 358.69 s adoption
   never played: from 358.69 s, Course 3 laps 257-258 (358.28 s) and 259-260 (357.73 s, 280 goals), the list the gate
   took with 357.71 s; from 357.35 s one of the two post-statue Course 2 laps (goals 268-269: 357.08 s, 278 goals), and
   from the 355.92 s record the same (355.67 s: the same ticks, the lap's 70 frozen frames gone, 109 idle ticks before the
   climb's quick restart). Dropping both misses `buydj`: the climb starts from the lap's finish. The item-2 plan (issue
   22: a 2_WJ_DS lap before leaving Course 2, the restart to the top, Course 3 laps until `buydj`) came out behind: the
   insert re-found +102 to +184 ticks, its replans +55 at best, and no Course 3 drop after it re-found to the end (the
   extra Cash strays the old buy walks, and one re-found as an investment found no way to the next `start:3`).

6. **Wall-coyote restart** (`wallreset`, opt-in; `enginesim\Cli\FullTasAutoWallReset.cs`; the Tech tab's *Wall coyote restart*,
   `techs.json` `wall-coyote-restart`, and the community's `checkpoint-midair-walljump`): a quick restart leaves
   `wallCoyoteFrames` and `wallJumpsLeft` as a wall cling set them, and the ground never counts the first down, so a cling
   just before a restart starts the lap with a stored wall jump. Engine numbers (`lab replay` of `checkpoint-midair-walljump`,
   WJ): cling at input 24 (`OnWall`, count 7); the restart at input 25 keeps count 7 and `wallJumpsLeft` 4, `OnWall` off
   (the ground tick clears it), `onGround` on; the count holds at 7 on the ground; the ground jump at input 28 comes first
   (`jumpBuffer && onGround` is checked before the wall branch; Velocity.y 805) and starts it down 6, 5, 4, 3, 2; the jump
   pressed again on input 34 (count 1 at the check) is a wall jump off no wall, Velocity (−460, 730), and on input 36 it no
   longer fires (the lab's `late` demo). It goes against the facing of the tick before, and peaks 292 u above the floor
   against a jump's 258. With a dash 1-5 ticks before it is the superbounce's boost (`stored-superbounce`).
   `fulltas <trace> --wallreset-study` lists every quick restart of the best with the player's state and count, and which
   holds of the stick either way for the last 1-10 ticks before it leave a count above 0 (buttons as they were). On
   `FalseEnding-Any%` (360.17 s): 157 restarts, 107 after `buy:buywj`, nearly all in mid-air right after a buy; 7 carry a
   count already (Course 3's `buyreward3` row: a cling at 9200,7084 before goals 179, 220, 223 and 244, and the restarts
   7 ticks later with 1-2 left); none of the 157 has a stick hold that gives one (0 of 3,140), so the cling needs another
   way into the box, not a change at the end, and the part searches. It takes each new best's restarts after the wall jump
   is unlocked, by their goal g, the one with a cling nearest first, and pulls a window over goals g..g+2 (`--window-minutes`)
   whose `OfflineSolver` grafts the tech's mined windows (`TechGrafts`, restricted to `checkpoint-midair-walljump` and
   `wall-coyote-restart` at `TouchedWall`, `LeftWall`, `WallAheadLeft/Right`: the stick into the wall, the restart, 0-3 idle
   ticks, the ground jump held 1-4, the wall jump) at the wall contacts of its runs, merged like any window's finds
   (`MergeAuto`), at most `--wallreset-minutes` (5) a pass, once per restart (memory `wallStudied`). `Explore` hands its
   solver `_mined` (the other windows get the whole library from `--parts grafts`, item 9). The pass runs before the goals part (behind
   it, a goals pull's investments took all 20 minutes and the pass never ran), and mines on a thread of its own (mining on
   the loop's thread took over its Simulation's thread-static hooks: the first pass saw every run end at once, 0 restarts).
   Status: opt-in. Measured, A/B at the same time, `--minutes 20 --threads 5` each, private copies of the 358.69 s run
   (284 goals): with `wallreset` 358.67 s at 103 s (goal 180, -1 tick), 358.26 s at 593 s, 357.71 s at 1084 s; the
   defaults 358.28 s at 203 s, 357.73 s at 253 s, then a 935 s goals pull with no gain (both later gains are the goals
   part's two Course 3 lap drops). 4 passes, 6 pulls (goals 180 and 221 each pass), 32,970 tech grafts tried, none in a
   find: the finds came by `optimize`, `splice`, `explore`, `seed`, `plateau`, and the one merged (goal 180) is the quick
   restart at the Course 3 spawn a tick sooner, ground jumps only; no find used the stored wall jump. The part took 3,715
   of the arm's 4,459 CPU s and 943 of its 1,206 wall s: goal 221's pulls ran their 1-minute window to 5-6 minutes
   repairing finds that stray-buy at goals 224-248, past the 5-minute pass cap. A tick for 3,700 CPU s, with the goals'
   gains 6-14 minutes later.

7. **Idle under the box** (`idle`; `enginesim\Cli\FullTasAutoIdle.cs`): every wait for Cash stands under the box it waits
   for, so the blanks in a route are where the route waits. The wait fix puts a buy's idle ticks before the quick restart
   ahead of it (the restart puts the player back as the old route had it, so the join is exact), which leaves the wait
   wherever that restart was: `FalseEnding-Any%` stood 112 ticks at the end of a Course 1 lap before restarting for
   `buy:buywj` (goal 86), and 15-25 before each `buy:buyclone` restart (goals 30, 33, 52). A box is bought from below:
   its `BoxCollider2D` trigger is a thin strip under the sprite (goal 30's: x 1483..1611, y -209..-203), entered by
   jumping up into it. At the start and after each new best, per buy goal, the idle runs of 8 ticks or more since the
   goal before that end further than the player's half width from that x-range are taken out and put back under it:
   - **Stands.** The ground spots after the last quick restart within 120 units of the trigger, and the last ground spot
     run on up to 60 ticks with the stick toward the trigger, and so is the spawn after the first quick restart since the
     goal before (not on the ground on the restart's tick; a stand there replaces the dash hops after it: Course 3's spawn
     9506,6958 is a 37-43 tick run from its x 9040-9168 triggers); a stand is one where 12 idle ticks after it leave the player
     at rest inside the trigger's x-range (the route's jump-off spot is short of it: goal 30's left the ground at
     x 1670, the range ends at 1611). The 6 nearest the edge the route comes from, at least 6 units apart, are tried.
   - **Ways in.** From a stand, w idle ticks and then the route's own inputs on (a spot on the route's own way), or a jump
     straight up (or with the stick toward the trigger), held to the touch, then the route's inputs from its old touch. w:
     the fewest that touch (looked at every 4th tick, then the ticks before), up to the fewest that touch when the old
     route did (binary search from snapshots at the stand, `Simulation.Advance`; a run that no longer touches counts as
     later). The whole run is replayed at that w (and the next 2): a touch on the old tick leaves the Cash as it was, but the
     removed wait also skipped what happened in it (likely the fall's landing sound: 5 RNG draws at input 4903 of the old route), so the clones' rolls shift
     and a later goal can stray into another box (goals 86 and 92: 237 and 96 of 282 goals). A run that misses gets the
     wait fix with stray re-timing (`Retime`, on for this call whatever the parts), and the fewest w that reaches every goal
     is searched down from there. Kept goal by goal when the whole run reaches every goal and is no slower than the run;
     then Commit (the fresh check) when faster, else the run is the best from then on.

   Measured on `FalseEnding-Any%` (360.35 s, one pass, `--threads 4`, 20 buy goals with far waits of 8+ ticks tried, 43 s wall
   after the start on an idle machine, 7 CPU s): goal 30's 17 ticks after the Course 1 lap became a run of 8 ticks on past the last ground
   spot (rests at x 1581.6) and a jump straight up, the buy at tick 1941 against 1950: 9 ticks faster, 360.17 s (the fresh
   check kept it). Goal 52's 25 ticks became 17 standing at x 1581.6 (inputs 3103-3119, the jump at 3120), the buy at
   the old tick 3126; goal 33's 15 became 7, also at the old tick. Goal 86 (112 ticks) and 92 (9) reach every goal
   only with re-timing (goal 86: 360.35 s against 360.17 s, 9 ticks slower, not taken; goal 92: none passes): their waits stay.

   On the 284-goal list (358.69 s, a private copy) the old goals 86 and 92 are 88 and 94; the Course 1 lap added before
   `buy:buywj` took goal 88's wait (no idle run of 8+ left). Course 3 (goals 222-245) now finds stands at the spawn at 12
   of its 18 buys tried (a 37-43 tick run to the x 9040-9168 triggers, 56-60 to the x 8870-8998 one; the x 8320-8658 ones
   are past the 60-tick run); none is kept. At goals 225-228 and 238-239 the run is slower than the dash hop it replaces
   (the fewest w that touch no sooner than the old route: 0). Every move also changes the sounds played (a dash's, a
   landing's: `Hooks.PlaySfx` draws the RNG), so the clones' rolls fall differently and a later Course 3 buy strays: goal 222's move misses goal 240, and the
   wait fix gets it through at 358.92 s (+0.23 s). Goal 94: every stand misses goal 99 (a stray `CashPerLoop`). Tried on
   94 and 222 and dropped: every sooner wait of the window (low..high−1) played whole (none reaches every goal), and tail
   windows (`--parts tail`) from the sooner run furthest ahead, as a revive does (94: no way through goals 99-101; 222:
   goals 227-232 re-found up to 46 ticks sooner, no faster whole run; 946 CPU s a pass). A pass is 81-92 CPU s (was 14:
   the spawn stands' whole replays). Goals 229, 231, 240 and 274 wait in the air after a hop with no restart, and the run
   without the wait never lands: no spot. Open: a move that keeps the RNG draws (the sounds) as they were.

   Acceptance, `--auto --parts idle --minutes 15 --threads 6` from 358.69 s: 0 of 20 waits moved (81 CPU s), 31 window pulls
   (4,660 CPU s), no gain. Shorter runs from the same start found `buy:buydj` sooner with a window at goal 274 (the climb
   after the Course 2 laps, item 4 of issue #22): 605 -> 595 ticks, 358.54 s, in 5 minutes (`--parts idle`); 605 -> 587,
   358.33 s, in 12 (`--parts lib,tail,stray,idle`; judged, the record then). The 15-minute run's goal 274 window was 27
   ticks ahead at goal 275, and its repair fell 24 behind at goal 277.

   **The RNG under the box** (`enginesim\Cli\FullTasAutoIdleRng.cs`). A wait for Cash is time the player can spend on the
   RNG, and every later clone roll depends on it. What draws it (`--rng-study`, 357.35 s, pre-2026-09-30 build: 6,026
   draws): the clones' spawn rolls (`Economy.RollCount`, 2 a roll, one more per success; 3,150), the player's sounds
   (`Hooks.PlaySfx`/`PlayAudio`: a jump 2, a landing's footstep 2, 5 after 0.8 s in the air, a dash 2 and its end 1, a
   wall slide 2, a quick restart 1), a buy 3. Walking draws nothing (the run animation's footstep is presentation, README
   exception 11). The RNG's state after n draws is the same whenever they come, so a hop only makes another class where
   a clone roll falls between its jump and landing and the next hop; and a hop under a box bonks its underside, about 20
   ticks up and down whatever the hold. On the current build an on-screen course with clones rolls in every rendered
   frame (12 draws a tick, [Clones and buy-max](#clones-and-buy-max-build-25630570)), so a roll falls inside every hop and
   the classes and measurements below are the old build's (the new build's: **Build 25630570** at the end of this
   item). A pass
   (at the start and after each new best), in order:
   - **Re-timing** (`Retune`): from a goal on, each goal with a quick restart since the goal before (a buy, a lap's start)
     gets the idle ticks before that restart cut or added (up to 60) to the count whose touch of the goal before the next
     such goal comes soonest; one whose goals are lost either way takes the one before back and checks through them (up to
     3). A restart's input moves only with the cuts before it (the base run's restarts, moved), a touch comes as the Cash
     does. One idle tick stays between two restarts (the gate refuses restarts on ticks in a row). Every goal with a restart
     is a start (on every thread); the fastest is kept. The same re-timing plays every ticket below.
   - **Filler**: where a stretch has more than one restart, everything before the last one (dashes away, restarts, hops that
     only burn ticks) becomes as many idle ticks, re-timed from there.
   - **Walk-in**, every buy: from the touch of the goal before, the stick toward the trigger until 12 idle ticks leave the
     player at rest under it (at most 150 ticks), the fewest idle ticks whose straight jump touches, then the run's inputs from
     its old touch.
   - **Tickets**: the idle ticks under the box (a walk-in's, a moved wait's at the fewest that touch, a wait already standing
     there) get every single hop and 300 random sets of hops 20+ apart; those that leave the player on the ground within reach
     of the trigger are sorted by the RNG's state there, and each class (up to 40, the plain wait first) is played whole,
     re-timed. A class counts only when it buys the box by the plain wait's tick (the jump in comes as the Cash does).

   Measured on `FalseEnding-Any%` (private copies, `--auto --parts idle --minutes 20 --threads 6`, window pulls between
   passes): 357.35 -> 355.92 s (re-timing after goal 52, 126, 225 and 248's moves: 357.35 -> 356.15, goal 126's alone
   -0.76 s; windows at 176/177/270/272/273/276 the rest; judged), 355.92 -> 355.70 (re-timing from goal 95 -0.06), and from
   355.67 (278 goals) -> 355.43 s: goal 249's two restarts and dashes became a 20-tick walk-in and 71 ticks under the box with
   one hop (-0.10), goal 248's filler before its last restart idle (-0.06), re-timing from goal 95 (-0.02), windows at 176
   and 268 (judged, the record). Tickets: 1 RNG class at 11 of 14 standing waits (13-23 ticks: no room for a hop and a roll
   between), 2-5 at goals 174, 231 and 249; a hop class beat the plain wait once (goal 249). A pass is 60-150 CPU s on an
   idle machine (re-timing 50, filler 16, walk-ins and tickets the rest), 400 on a loaded one.

   `buy:buyds` (goal 174): the walk-in stands under the box after 135 ticks and buys at tick 10429 against the route's
   10394 (its run and jump carry the fall's speed), 357.19 s whole; the old jump into the box's underside costs nothing (the
   Cash is there at 10394).

   Two more ways under the box, then the same tickets:
   - **Wait before a restart** (in the pass): where a buy's wait is idle ticks before its last quick restart and the restart
     and a dash take the player in (Course 3's rows), the wait starts with a walk toward the trigger until the player rests
     under it and stands there for the rest; the restart and the dash stay, so the way on after the buy is the run's.
     Re-timed from that goal (the wait is the restart's).
   - **Stand window** (`--rng-study --stand K`, one minute): for a buy no walk reaches (a climb, a far box), the solver
     searches from the goal before to a line as wide as the trigger at the height the run stood below it (or the floor a
     raycast finds under it), scored at the earliest arrival and seeded with the run's own way; after 12 settling idle ticks
     (an arrival in a dash takes no input), the walk-in, idle ticks and a jump that holds the stick the run held into the
     box. When the way on after the buy dies, it is re-found (`TailFix`: up to 3 windows of two goals from the last goal
     reached, seeded with the run's way). A study only: on the measurements below it never paid.

   Measured from 354.29 s (`--auto --parts idle --minutes 20 --threads 6`, 278 goals): 353.32 s. Re-timing from goal 254
   (-0.12 s), filler before the last restart made standing at goals 227 and 245 (-0.02, -0.23), walk-ins with winning
   hop tickets at goal 248 (one hop 5 ticks into 73 standing, -0.05; later one in 37, -0.08) and 249 (three hops in 85, -0.14),
   re-timing from goal 95, windows at 268 (the `buydj` climb, -3 ticks), 271 and 273.

   `buy:buystatue` (goal 262): on 354.72 its wait is a looping walk 450-1,500 units right of the trigger, then a dash left
   into it at the Cash tick (14787; the price is there from 14786). The stand window gets under the box by tick 14399 and
   has 342-420 idle ticks there (8 RNG classes of 640-720 hop schedules; a hop bonks the ceiling at y 7041), but the run's
   way on after the buy starts from that dash: from a stand it dies (action 5126-5135), and re-found it loses 72 ticks by
   goal 266 and finds no way through goals 267-268 in a minute. Open. `buy:buydj` (goal 268): no floor under the trigger
   (the shaft), so no stand; the climb's windows took 3 ticks, and the buy is Cash-bound again (on 355.43 the price was
   there 9 ticks before the touch). The filler after the Course 3 buy near tick 12340 on 355.92 (a dash right, restart,
   jump and dash, restart, idle, restart) is gone on 354.72: buy, restart, a dash to the Course 3 gate, restart, lap.

   **Build 25630570** (2026-09-30): the numbers above are the old build's. An on-screen course with clones now rolls on every
   rendered frame (`clonesScript.SpawnClone` takes the interval off its timer instead of zeroing it, and Start's 10,000 never
   runs down: 2 draws a frame, more on a success, 12 a physics tick on the studio's clock, against about 0.3), and a roll on
   a frame with no free slot is drawn and thrown away. So every extra draw moves which rolls fall on the frames that spawn,
   from then on, not only a roll between a hop's jump and landing: nearly every hop schedule is its own RNG class. The
   tickets now take their 40 classes in turns from the single hops (spread over the wait) and the random sets, not the
   singles first. A hop that buys the box doesn't count (the jump in buys it), and a buy-max box's levels, which come in one
   a physics tick while the player is in the trigger, are the jump in's as far as it reaches them (the run goes on from the
   last one's old touch, re-timed from the goal after). Waits under a box shorter than a hop (about 20 ticks) still have
   one class.

   Measured on the new engine (ed1784b, provisional until proven against the game), 353.32 s as the seed: it strays at
   goal 127 (a Course 2 `Clone` level bought that isn't the next goal's box: the goal list is the old build's), and
   re-timing its waits (`--rng-study --base-check FILE`, the old build's `--check` of it for the waits' places) doesn't get
   past it (the Course 2 buys between goals 95 and 180 have no restart to wait at). To goal 126: 30,751 clone rolls and
   62,607 draws (the old build: 1,561 and 5,917 over all 278 goals), 61,502 of them the rolls'; goal 52's 17-tick wait has
   102 rolls in it (6 a tick, Course 1 on screen), against 3. While a course's fast and big clone chances are 0 (Course 1,
   Course 2 before its fast/big boxes), every roll draws exactly 2 and fails, so the rolls can't change a clone and the RNG's
   state after a wait is its draw count alone: hops at goal 52's stand make 1, 2, 3, 4 and 6 classes in 14, 34, 54, 94 and
   174 ticks on the new build (1, 2, 4, 7 and 12 on the old; `--rng-study --census --census-extend 20,40,80,160`, hops
   that buy the box counted). The RNG only pays where a chance is above 0 (Course 3 from `buy:buyfast3`, the global
   fast/big boxes), and there, on the new build, every frame's roll is re-dealt by an extra draw. The existing stands in
   the reached goals are 13-17 ticks (one class); walk-ins there have no class (the player isn't at rest within reach where
   their wait ends). Open: a new-build route through Course 3 to measure the tickets on, and skipping hop tickets where
   every on-screen course's chances are 0 (Economy doesn't expose them to the loop yet).

8. **Waste audit** (`waste`; `enginesim\Cli\FullTasAutoWaste.cs`; `fulltas <trace> --waste-audit <route.json> [--expect a-b,..]
   [--clean kind:a-b,..]`): at the end of every model build (the start, each new best; a model of another route, the goals
   part's `ModelOf`, audits that route, and the best is audited again when next asked for) the best is replayed tick by
   tick (place, Velocity, the stick in effect, ground, wall, dash, a ceiling touched: `PhysicsWorld.PlayerTouch.Ceiling`, a
   contact whose normal is within 45° of straight down) and scanned for what a person watching it calls silly. Every
   measure is live time, a dash's freeze frames included. A finding is a span, its raw ticks (the span's live ticks less
   what its goal's own pace, the deficit's 80th percentile, makes of its progress) and its stake (raw held to the target
   buy's spare Cash: a Cash-bound stretch loses nothing to waste). Kinds:
   - `bonk`: Velocity.y from 300+ to under 30% of it in a tick against a ceiling, not in a dash, no goal within 3 ticks (a box
     is bought by jumping into its underside); the span runs to the landing.
   - `stall`: 3+ ticks with the stick at least half held and x moving under 0.5 u a tick (not a cling a jump press uses
     within 2 ticks).
   - `dashpair`: two dashes within 60 ticks, each 100+ u sideways, opposite ways, netting under 35% of their sum.
   - `filler`: restarts at most 60 ticks apart with no goal but a start gate between them; the span reaches back to the goal
     before when it is at most 30 ticks before the first.
   - `away`: a restart landing 150+ u further from the next box than the player was, that buys it 15+ ticks later than
     staying would (the box's 10-tick buy cooldown, or the walk from where the player was). A restart and a dash back into a
     box 500 u off (the quick rebuy) is 8 later and isn't flagged.
   - `walk`: a buy with spare Cash walked to from 150+ u (from the goal before or the last restart) on a path longer than
     1.25 × the walk distance + 60.
   - `slow`: flat ground (under 150 u of rise over 50 ticks) where movement pays (no lap, spare Cash), sideways progress
     toward the goal per live tick under 0.9 × what the flat allows: with the dash, the super chain played from the span's
     start (6 cycles of `FalseEnding-Any%` 354.72 s's flat cycle, the best of 0-4 ticks' wait, after its first 20 ticks; a
     chain under 9.2 u a tick there, a wall or a gap, says nothing); without it, the run's fastest flat 10 ticks with those
     unlocks.
   - `laps`: the last lap of each run of back-to-back laps of a course; its stake is the model's economy slack there, and
     its lever the goals part's lap drop (no arm).

   Each goal's stake adds 0.5 × it to the goal's movement headroom (held to the spare Cash with the rest) and gives it a
   `waste` arm: a window from the goal to the goal after its last finding. Every window over a finding's goal is seeded with
   each finding undone: a filler chain's inputs cut (its last restart right after the goal before), the away restart
   dropped, the dash pair's dashes dropped, the bonking jump dropped, slow flat ground played as the super chain (the
   probe's wait, cycles over the span's inputs). Such a window's tech grafts (item 9) take half its explore runs, their
   contact in the findings' input span 3 picks in 4 (`OfflineSolver.GraftFocus`).

   Acceptance, the six spots a person saw in the 355.92 s record (280 goals, `fulltas-FE-WASTE.json` a copy of FE-CMB's
   trace), 11 s:

   ```powershell
   & $exe fulltas $traces\fulltas-FE-WASTE.json --waste-audit "$traces\handback\fulltas-FalseEnding-Any%-RNG-355.92.json" --expect 10288-10327,12343-12381,13691-13793,15166-15338,15338-15926,16300-16450
   ```

   ```
   waste audit: 95 findings (bonk 14, stall 10, away 23, laps 3, filler 20, slow 5, walk 11, dashpair 9), stake 1509 ticks at 45 goals
   expect 10288-10327: flagged by stall goal 174 10307-10312 stake 0
   expect 12343-12381: flagged by dashpair goal 208 12323-12353 stake 28, filler goal 210 12346-12381 stake 39
   expect 13691-13793: flagged by filler goal 248 13687-13707 stake 0, away goal 248 13690-13793 stake 0, filler goal 249 13793-13848 stake 0
   expect 15166-15338: flagged by laps goal 268 15242-15338 stake 0
   expect 15338-15926: flagged by laps goal 268 ..., away goal 270 15352-15926 stake 10, walk goal 270 15353-15926 stake 10, stall goal 270 15609-15618 stake 4, dashpair goal 270 15625-15650 stake 10, dashpair goal 270 15707-15802 stake 10, bonk goal 270 15771-15801 stake 10, stall goal 270 15808-15812 stake 2
   expect 16300-16450: flagged by slow goal 275 16302-16572 stake 55
   expect: all as expected
   ```

   At 10297 Velocity.y goes 589 to 50 on the jump cut of the release at 10296, with no ceiling touched; the waste there is the
   stall against a box's side at 10307-10312, and `buy:buyds` holds its price 0 ticks before it buys (Cash-bound, as C:
   goal 248's restart lands 882 u from the box it stood under, and buys 0 ticks after the price). E is held to `buy:buydj`'s
   10 spare ticks. F reads 19.4 u a live tick against 24.3 for the chain. On the 354.72 s route (the chain on that flat)
   `--clean slow:16295-16542` is clean.

   The loop's model of the goals part: after an investment that isn't kept the goals change back, and the loop's simulation
   is made again (`RebindEval`): the one saved had lost the thread's game statics and engine hooks to SwitchGoals' new
   Simulation, so every run of it ended at no goal. Both arms of a 20-minute A/B from 355.92 s (with the goals part) lost
   their last 7 minutes to it: every window find was dropped as "merged onto this best already" (MergeAuto now says when
   finds replay short of the window's goal) and the end model was empty.

9. **Tech grafts in every window** (`grafts`, on by default, also in a plain `fulltas` solve; `--no-tech-grafts` turns them
   off, `--graft-rate r` sets the share): the tech library (`TechGrafts.Load`, `--tech-dir`, `--tech-cache`: the Tech tab's
   mined demos and whatever else fills its `Patterns`, and the super chain of item 8 as a window at a landing chained into
   itself up to 8 cycles), loaded once on a thread of its own, goes to every Full-TAS window's `OfflineSolver`: the window
   arms, `Explore` (reach, polish, repair and the goals part's investments) and the tail windows; the wallreset part's own
   subset replaces it in its windows. Each solver gets the patterns whose techs.json `requires` the unlocks at the window's
   first goal (the model's row) and the window's unlock buys meet, as its own instance (`Prepare` isn't shared), grafted on
   0.15 of its explore runs with a contact (`OfflineSolver.GraftRate`; 0.5 over audit findings).

   Measured, `--auto`, 3 threads each, side by side, 20 minutes (private copies FE-WASTE/FE-WASTE0 from 355.92 s, with the
   goals part; FE-WASTE2/FE-WASTE3 from 355.67 s, 278 goals, without it):

   | Arms | Curve (wall s: time) | Grafts |
   |---|---|---|
   | from 355.92, defaults + `waste,grafts` | 226: 355.67 (goals) | 37,376 in 22 windows, 1 find via a graft (landing-tick-jump), none kept |
   | from 355.92, defaults | 221: 355.67 (goals) | - |
   | from 355.67, `lib,tail,combine,sched,ctg,macros,stray,idle,waste,grafts` | 538: 355.62 (window 254), 600: 355.61 (idle 244), 775: 355.47 (combine 240), 968: 355.25 (combine 41), 1017: 355.18 (idle) | 68,069 in 40 windows (1-4% of runs), finds via grafts: gc-wall-clip 1, roof-squeeze 1, none kept |
   | from 355.67, the same without `waste,grafts` | 199: 355.65, 329: 355.61, 436: 355.55, 621: 355.54, 856: 355.34, 1017: 355.30 | - |

   The goals part's two pulls took 13 of the first pair's 20 minutes (both dropped the post-statue Course 2 lap, spot D).
   In the second pair the audit's probe order put goals 41 and 254 in the first round: goal 41's window (the `away` finding:
   a restart 431 u from the box the player stood under, bought 48 ticks later) found the split 49 -> 23, which combine kept
   (-11 ticks); goal 254's (a `dashpair`) -2. Neither arm pulled a `waste` arm in 20 minutes on 3 threads (the first-round
   probes and combine took the time). A 5-minute `--focus 272-274` run on 6 threads from the second pair's 355.30 made
   354.55 s by windows and a lookahead on the ending's flat (-38 ticks), none by a graft or the chain seed.

   Planted-waste bench (`searchbench --suite grafts --seeds 2 --minutes 1 --threads 6`, the Full-TAS cases, arms
   `--no-tech-grafts` and the default; median CPU s to the target, median best less the target): fe176-bonk 0.29/-4 and
   0.19/-3; fe270-dashpair 17.8/+1 and 37.6/+2; fe-c3lap-restart 2.8/0 and 3.1/0; fe174-open neither (Cash-bound);
   fe270-open 30.6/0 and 95.2/-1; fe275-open 9.8/0 and 11.3/-3. The grafts reach a planted waste's target no sooner (the
   dash-pair climb later) and end the open flat and climb windows 1-3 ticks lower.

The parts are found by the interface they implement, so a build without one runs without it (the log says so).

Measured on `FalseEnding-Any%`, 10 minutes, `--window-minutes 0.5` (wall seconds to each new best). SearchDesign's first
builds, from 390.38 s on 24 threads:

| Loop | Curve | 10 min |
|---|---|---|
| `--parts none` (baseline) | 118 s 390.16, 130 s 387.46 | 387.46 |
| `lib`, player-key joins | 190 s 386.12, 239 s 383.42, 356 s 382.38, 383 s 382.26, 524 s 381.86, 597 s 381.14 | 381.14 |
| `lib`, full-digest joins (with the Box2D hash, 9 CPU s a trace) | 164 s 390.12, 300 s 385.76, 409 s 384.84, 564 s 384.06 | 384.06 |

One after another from 381.14 s on 16 threads (one binary; the speed probes read from the cache, so the loop starts at
13-16 s):

| Parts | Records (wall s, CPU s) | 10 min | Chaining |
|---|---|---|---|
| `lib` (per-goal probes, no wait scan) | 242 s/2284: 380.62 (window 275) | 380.62 | 227 → 228 |
| `lib` | 434 s/4832: 381.04 (window 299); 597 s/6588: 380.52 (lookahead 295) | 380.52 | 227 → 228 |
| `lib,tail` | 178 s/2053: 380.78 (window 186, route-synced); 248 s/2835: 380.30 (window 275); 307 s/3302: 380.06 (window 264, wait fix) | 380.06 | 227 → 229 |

Side by side (the fairest comparison: every arm on 2 of the 16 threads at once, same load, one binary), from 381.14 s:

| Arm | Records (wall s) | Ticks gained | CPU s | Ticks per 1000 CPU s |
|---|---|---|---|---|
| plain `fulltas` (no `--auto`) | none (it re-polishes from goal 1) | 0 | ≈1200 | 0 |
| `lib` | 397 s 380.68, 427 s 380.52 | 31 | 965 | 32.1 |
| `lib,tail,sched` | 202 s 381.04, 253 s 380.94 | 10 | 976 | 10.2 |
| `lib,tail,sched,ctg` | 148 s 380.92, 351 s 380.66, 445 s 380.62 | 26 | 1031 | 25.2 |
| `lib,tail,sched,ctg,macros` | 55 s 380.90, 293 s 380.86 | 14 | 978 | 14.3 |
| `lib,tail,combine,sched,ctg,macros` (the default) | 56 s 380.90, 424 s 380.70, 488 s 380.42 | 36 | 928 | 38.8 |
| `lib,tail,combine` | none | 0 | 1030 | 0 |

Each row is one 10-minute run, and the rows differ by as much as the parts do (`lib,tail,combine` found nothing where
`lib,tail` alone made 380.06 on 16 threads), so no single part's share is measured to better than that noise. What
holds across every run: the plain search gains nothing in 10 minutes from a polished route, and every `--auto` build does.
The default is the arm that gained most per CPU second. Where the time goes: the windows find faster ways (47-88 per
10 minutes on 16 threads) and most die in the tail after them: course 3 stray buys (`CashPerLoop` at goal 195,
`CloneCount` at 193 and 248, `CloneMult` at 252) that no wait cures, and deaths in the ending (goals 277-297) with no
quick restart to wait at. Window search is 98-242 s of each 600; merges, repairs and replays take the rest.

Combine joins what separate runs found: the judged 380.52 s and 380.62 s made 380.00 s, and 380.00 s with the 380.06 s
route's goals 186-266 made 379.40 s, each in under 20 s. Judged in turbo (GameOwner4, GameOwner5): 380.62, 380.52, 380.00,
379.40, 378.74. The chaining check stayed at 227-230 of 300 goal touches at speed on every kept best. The continuous run
then went 378.94, 378.74, 378.72, 378.50, 378.48, 378.46 (bin6, windows), then 377.80 (stray study), 376.76 and 376.70
(combine's splice-in repair); candidates after 378.74 had not been judged when this was written.

**Regression bench.** A change to the grafts or the window search is measured on `searchbench --suite grafts --seeds 3
--minutes 1 --threads 4` ([enginesim/README.md](../enginesim/README.md#planted-waste-suite---suite-grafts)): wastes
planted in known-good windows of the `FalseEnding-Any%` 355.92 record and course records (a super chain played as dash
and run, a jump into a ceiling, a cancelling dash pair, a filler restart), each searched from the planted route as
`fulltas --goal` (or `solve`) and scored on whether it gets back within 2 ticks and the CPU it takes, plus the record's
own windows at goals 174, 270 and 275, where the waste was seen by eye. The baseline recovers every planted waste in the
minute and beats the record's split at goals 270 and 275 (window-local), not at 174.

### Reset segments

Terms: a *goal* is one of the run's Full-TAS goals (numbered, where to go next); a *Checkpoint* is the game's
`checkpointScript` trigger, which sets `respawnPoint`; a *Quick Restart* is the TAS's `reset` input, the game's
`Movement.respawn(true)`. A Quick Restart goes to `courseResetPoint` while a course is running (its start gate sets its
reset point, `courseScript.FixedUpdate` keeps it there, the finish clears it), else to the last Checkpoint touched. The
course-checkpoints toggle (`isRespawningAtCheckpoints`) only changes where a death respawns: `respawn(true)` ignores it,
so while a course runs a Quick Restart goes to its reset point even after a Checkpoint. A death ends every Full-TAS run,
so only Quick Restarts split one.

### Loop builder

A run's buys and laps repeat: the same quick restart, the same walk to a box, the same lap, dozens of times. Searching
each of those goals afresh (a window per goal) spent most of a night's solve re-finding them, and a buy the run couldn't
pay for yet had no route at all, so its reach window never ended. The builder (`enginesim\Cli\FullTasBuild.cs`) puts
them together from pieces instead:

- A piece is the inputs from one state to one goal (a box, a start gate, a finish), filed under the goal's label, the
  player's unlocks and where the player stood, in
  `<traces>\archive\fulltas-pieces.json`. The library is shared by every run, and kept across solves (`--fresh` starts
  without it); per spot it keeps the 4 shortest. It also holds every goal of every run's best, area goals included,
  which the search windows play ([Shared goal pieces](#shared-goal-pieces)).
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
- Every purchase is a goal on a strict run (the default; [Loose buys](#loose-buys) lifts it): a route buys only the box of
  its next goal (`Simulation.StrictBuys`, on for every Full-TAS simulation: the builder, every search window and the saved
  best's replay). A walk that also bought another box spent
  Cash and levels the plan counted on, and could cap a box a later goal buys (a build that ran through `FastCloneChance`
  twelve times had nothing left for its `buy:buyfast3` goals; `TrueEnding-ANY%`'s 482.98 s route bought a `buyreward3`
  on its way to Course 3's gate and walked through Course 2's row buying neighbours, so its best stops at goal 127). A
  Full-TAS run (strict or loose) finishes 12 ticks after its last goal, not at it, so a neighbouring box the player still
  stands in once its 0.2 s cooldown is over also ends a strict one (the search would otherwise hand the next piece a spot
  where any input buys it). A
  buy goal whose box is inactive (capped, or not unlocked by the goals so far) stops the build at once, with the box's
  state (`Shop.Describe`: bought/cap, cost, what the run holds); before a searched buy the log shows the same state.
- The build is saved as the run's best every minute and when it stops.

### Engine farm planner

```powershell
& $exe fulltas-plan $traces\fulltas-TrueEnding-ANY%.json --from reward:bonus3 --target buy:buydj --out plan.json --piece-minutes 0.3 --threads 6
```

`fulltas-plan` plans a run's buys and laps with the game's own economy instead of `plan_fulltas.py`'s model of it (whose
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

`--group-buys` also weighs two boxes on one walk: for every ordered pair x, y of the stretch's boxes that the run can pay
for together now, x then y from where x left the player, scored the same way (its end tick, then the target's wait over
the income after both). "x and y in Z ticks" then competes with "x alone in B ticks" (and a lap): the step from x to y
is often far shorter than a lap and a second walk. It is off by default because each pair with no library piece between
its two boxes costs a piece search (`--piece-minutes`) once; a pair with no way is dropped. `plan_fulltas.py` already
prices walking along a row to the next box (`buy(b, walk_row)`), so it needs no change for this.

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

A clone pays at its path's end, and its path is the course's best time, so every step also offers a **record lap**: the
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
of the run's labels) under its own goals from the run's start and files each of its goals as a library
piece. After a plan changes the goals, the builder then plays the old route's laps and box walks wherever the player
stands as it did, and only searches the steps that changed: `FalseEnding-Any%`'s run after the new Course 1 plan (goal
129 on) was rebuilt in 3 minutes with the 499.42 s route harvested, where the build had done 25 goals in 3 minutes
without it.

### Buy table

```powershell
& $exe fulltas-buytable $traces\fulltas-FalseEnding-Any%.json --from reward:bonus1 --until buy:buyds --out buytable.json --entry-minutes 0.1 --threads 4
```

`fulltas-buytable` measures the time to buy: for each place the run stands at in one buy phase (the phase's start, the
course's start gate, each quick restart's landing, its finish, and each box spot), the physics ticks to reach and buy each
box and each ordered two- and three-box set in one go, per technique. `walk` is the library's pieces and a plain piece
search; `jump-through` is a jump carrying sideways momentum whose arc passes the set's boxes, buying each on the way
(generated seeds: jumps held 6 or 14 ticks with the stick full sideways, run-ups of 0-24 ticks, both directions); and
`dash-through` is a dash along the box row buying every box its pass touches (only where the place's unlocks have the
dash, read from its caps id such as `wjds`: the buy cooldown is per box). Every entry is measured in the engine, as a short piece search whose goals are the
set's buys (the set's goals appended to the run's, as a plan step's): a way that buys any other box ends the run
there and is not measured, so each named box is confirmed bought and the tick is the goal's. The table prices movement,
not income: each entry is measured with the place's Cash plus the set's price, on a copy of the place's snapshot (the
buy planner adds the economy itself). With unlimited Cash every box a walk passes buys (a strict stray), and standing in
the set's last box past its 0.2 s cooldown buys it again (the first run's pairs from Course 2's clone box all missed);
with the set's price the other
boxes stay as affordable as the run had them there. A set whose box is capped or locked at the place is filed as
`technique: "capped"` (its buy goal could never count there). The run's own ways play first (`Borrowed`), then a piece
search from the place with the best's route up to it; `--entry-minutes` bounds each search and `--entry-stall` stops it
once it stops finding. The search's workers replay that route, then start from the entry's own state: its goal list
(the run's goals to the place, then the set's) and its Cash, neither of which a replay reaches (each entry's goals are
built from the run's, not the previous entry's list); the search tracks every goal of the set and a way is cut at the
set's last box. Every place's single buys are measured first, then every place's pairs, then the triples, so a run cut
short has whole sizes done.

The output is `fulltas/<run>.buytable.json`, cached per run and trace: `places` (`id`, `name`, `kind` of `start`,
`restart`, `gate`, `finish` or `box`, the `box` label where it is one, `x`/`y`, `caps`, `tick` the snapshot's own physics
tick, and a restart's `restart` `{press, land}`), and `entries` (`place` an id, `boxes` in the order bought, `technique`,
`ticks` from the place's tick to the last box's, `searched` seconds, `caps`, and `inputs` as `Solve.EncodeTicks` runs). An
entry found no way in its budget is cached as `technique: "miss"` with `ticks: null` and retried only with `--retry-miss`,
so a table fills in across runs (an entry measured before is skipped). The boxes' order matters: every ordered set gets
its own entry, and the buy planner (which prefers the cheapest technique per set and emits `buy:a+b` goals where one walk
buys several) chains single buys at places a set has no entry from.

### Buy planner

```powershell
python tools/plan-buytable.py --table $traces\fulltas\FalseEnding-Any%.buytable.json --out planned.json   # --run R, --best FILE, --trace FILE, --goals FILE, --record S, --max-set 3, --verbose
```

`plan-buytable.py` chooses a buy phase's buy order and grouping from the measured time-to-buy table
([Buy table](#buy-table)): the engine's ticks from each place the run stands at (the phase's start, the start gate,
the finish, each box's spot, the quick restart's landing) to buy each box and each ordered set of 2-3 boxes in one pass.
On top of it runs `plan_fulltas.py`'s Course 2 economy (clone pool and spawn gate, the pre-2026-09-30 build's
([Clones and buy-max](#clones-and-buy-max-build-25630570)); box costs, lap rewards, Course 1's clones off screen),
and a Dijkstra search over its states (tick first, the model's dominance on box levels, place and
Cash) finds the goal list that buys the target soonest. Each step is a lap (`start:2 finish:2`, or `finish:2` from the
gate), a quick restart to the gate (`start:2`), or a buy of a set: a walk from where the player stands or a quick
restart and a walk from its landing, whichever the table has cheaper. Until the plan first crosses Course 2's gate (a
box bought on the way from the phase's start), a quick restart lands at the checkpoint before the course: those restarts
use the phase's earliest landing and its measured walks (its gate walk is ~404 ticks against 25 from Course 2's own), so
a plan doesn't buy before the gate and then count on a cheap restart lap. A set of several boxes is one goal,
`buy:buyreward2+buyclone2` (each of these boxes, any order), so the list stays strict. A plan never waits at a box for
income: when the Cash isn't there on arrival, a lap earns it. Only Course 2's stretch (`reward:bonus1` through
`buy:buyds`) has the economy model; a table of another phase is refused.

The model is calibrated on the run's offline best and its trace (streamed, not loaded: a Full-TAS trace is ~600 MB):
Cash, Course 1's levels and path and Course 2's reward at `reward:bonus1`, Course 2's best path at the first
`buy:buyclone2` (the clones' path; the first lap sets it, 2.70 s -> 2.44 s on `FalseEnding-Any%`), the ticks to the
gate, the typical lap and restart-to-gate lead. The run's current list is replayed through the same model and walks,
so both predictions compare like for like. A replayed buy the model can't pay for on arrival either waits at the box
or runs the course on its walk first (a finish that isn't the next goal pays and changes nothing else:
`FalseEnding-Any%`'s goal 100 walk runs Course 2 and is paid 1128 W for it), whichever is sooner. With that, the
replay of the best's own walks lands within 40 ticks of the best's buy ticks through goal 126.

It prints the calibration, both predictions (the target's tick after the phase's start, laps, buy goals, grouped buys,
ticks walking and waiting), the predicted total (the best's, and `--record`'s), the box levels the plan reaches
soonest against the current list, and the changed goal blocks, largest first. A single buy the table hasn't measured
falls back to the model's distance estimate and is listed. `--out` writes the whole planned goal list (the goals
before `reward:bonus1` and after `buy:buyds` kept) with the prediction, for a copy of the run to be solved from
(`fulltas --auto` on a trace whose header has the planned goals); the real run's goals change only by hand.

First measurement (2026-09-29, `FalseEnding-Any%`): the Course 2 table from the 390.38 base has 363 entries over 14
places (all singles and ordered pairs, 23 triples; jump-through won 52, dash-through never applies there: no dash before
`buyds`). The planner predicts `buy:buyds` 3746 ticks after `reward:bonus1` against 3927 for the current list (-181),
with 18 grouped goals. Built alike from goal 97 on a copy of the record's capture (`fulltas --unsolved --harvest` of the
record's best, no `--auto`), the planned list reaches `buy:buyds` at tick 11236 and the current list at 11237: a 1-tick
gain. The build follows the model within 22 ticks through goal 113, then falls behind it (+137 by goal 124, +304 by the
last lap), mostly waiting for Cash at the clone buys and before `buyds`: the Course 2 income the model is calibrated
with is optimistic (it also puts the current list 247 ticks ahead of the record's own), and more so for a plan that buys
earlier. The model's income needs calibrating against the trace's Cash before a plan's prediction can be trusted.

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

- **Clones** (`clonesScript.Update`/`SpawnClone`), frame by frame as 1x, turbo and the engine step them (one frame per
  rendered 280 fps frame, `cloneFrameSeconds`): off screen a course with clones and a path spawns once
  `timeSinceLastClone` reaches 1 s and pays `ceil(value (2j+1) / previous interval)`; on screen (the player
  inside the course's `activeCloneArea`, its `clonesLoD` polygon: Course 9's covers most of zone 2, or lapping it) one
  spawns once the rolled interval's frames pass and the pool has one free, and pays `ceil(value (2j+1))` at its path's
  end (the pre-2026-09-30 rule; the current build spawns into a free slot every frame,
  [Clones and buy-max](#clones-and-buy-max-build-25630570), not ported here yet). The interval is path / clones with
  the speed and size rolls (`Random.Range(0, 101)` against the chances, per tier) averaged exactly. Watts are `reward ×
  clone multiplier`, green power `gpReward × clone dust multiplier` on a
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
and payouts on and off screen (the clonesLoD trigger; the current build's rule:
[Clones and buy-max](#clones-and-buy-max-build-25630570)), what the courses' local boxes change (reward, clone count, clone
multiplier) and the global boxes `GLOBALcashPerLoop` (its own course's reward; the game updates the others on a random
5 s phase), the global clone speed and size caps (`maxCloneFastness`, `maxCloneBigness`: dictionary only while both
clone chances are 0) and the statue (`unlockPrestige`: a global clone multiplier instead of its own entry). A prestige
box — the statue and Course 4's `buyprestige4` — also runs `BoostOldCourses`: each old course's reward tier becomes the
buying course's tier plus its own mod and its reward updates, and its boxes gain half their cap multiplier, become
buy-max and turn back on. A buy-max box's touch buys what `GetNumPlayerCouldBuy` counts (`IE_BuyMax`): the first during
the tick's physics, then one a physics step while affordable, the touch's step included (`Shop.FixedStep`; the
touch-tick count awaits a 1x probe: [Clones and buy-max](#clones-and-buy-max-build-25630570)). Such a touch is one buy
goal: the levels bought after the first continue it, so they neither reach a goal nor end a strict run as a stray
buy (`FalseEnding2-Any%` buys Course 2's three boosted boxes to their caps with one goal each after the
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

### Clones and buy-max (build 25630570)

Steam's update of 2026-09-30 10:34 (build 25630570) changed two game statements: `clonesScript.SpawnClone` on screen
went from `timeSinceLastClone = 0f` to `timeSinceLastClone -= nextCloneDue`, and `upgradeBox.IE_BuyMax` from
`WaitForEndOfFrame` to `WaitForFixedUpdate`. The numbers below come from a float32 simulation of the decompiled
`clonesScript.Update`/`SpawnClone` of both builds, which reproduces the measured pre-update table (the old column).

**Engine status.** The engine and turbo play the new build (ed1784b). An on-screen spawn keeps the overshoot
(`Economy.cs:308`), and the snapshots keep each course's timer as it is. `IE_BuyMax` resumes after every physics step,
the touch's included (`Shop.FixedStep`, `Shop.cs:148`, called at `Simulation.cs:1313`; turbo parks it as a
`WaitForFixedUpdate` routine, `TurboRunner.cs:181`, resumed at 238-239). `--ticks-out` rows carry each course's clones
(`Economy.CloneStates`: count, path, on screen, in flight, spawn timer). The engine matches the new-build turbo through
tick 14122 of the 353.32 s route. Still on the old rule: `ZipPlan.ClonePay`'s on-screen branch and `plan_fulltas.py`'s
Course 2 spawn gate ([Buy planner](#buy-planner), `--dj-farm`). Pending a 1x probe: how many buy-max levels come on the
touch's tick. A scene diff of the two builds (4,036 objects each side) finds no collider, `upgradeBox`, `courseScript`,
`clonesScript`, spring, spike, mover or checkpoint field changed, only presentation and runtime state (the statue's
`StatuePrestigeText` lost a MeshFilter): no world or trace needs capturing again for the build.

**Terms** (both builds). T the course's best time (the clones' path, `pathLength`); C the clone count; I = T/C
(`BaseCloneInterval`); a clone's speed s = (1 + 0.35 fast) · 0.75^big, its rolled interval I/s; its lifetime L =
⌈T · fps / s⌉ frames (+1 when float rounding falls short); it pays on the first frame its pointer reaches N = T/0.02.
On screen a clone pays `ceil(reward (2 big + 1) cloneMult boost)` (cloneMult 0.1 + 0.2 a global level + 0.1 a local).
Every `SpawnClone` call draws the RNG at least twice (the fast and big roll loops, one more per success), spawn or not;
at most one spawn a frame. Off screen (unchanged): one call per 1 s counted in frames, paying about C s/T of a clone's
pay, income ×1.000 on the studio grid, ×0.992 at 120 fps, ×0.983 at 60, ×1.000 at 30. The studio's "280 fps" clock
renders 6 frames a 0.02 s tick (one short crossing frame, five of 1/280 s): 300 frames a second. Numbers at 280 are on
that grid.

**Old build (to 2026-09-30).** A spawn set the timer to 0: the gap was I rounded up to whole frames, in flight
min(C, L/gap), income ∝ (I/dt)/⌈I/dt⌉: a sawtooth in T, with magic spots where I/dt is just under an integer.

**New build.** `Start` sets the timer to 10000 and a spawn only takes an interval off, so on screen the timer stays due:
`SpawnClone` runs every rendered frame, a clone spawns in every frame with a free slot, and each slot recycles every
L+1 frames. In flight min(C, L), L ≈ ⌈T fps⌉; income over the ideal C/T ≈ 1 − 1.5/(T fps) when C ≤ T fps, else
T fps / C. A course's rolls are tied to its frame count, not its spawns: any other draw (a sound) moves every roll after
it. What still depends on the frame rate: one spawn a frame (binds only at low fps), the lifetime's rounding plus the
one-frame respawn, and the off-screen 1 s counted in frames.

**The reserve.** A course keeps `Start`'s 10000 from its first clone to its first off-screen tick. The off-screen
call (with clones and a path; it returns before the reset while the course has none) sets the timer to 0 and the due
to 1 s, and nothing in a run puts the 10000 back (only `clonesScript.Start` sets it). In the 353.32 s route Course 1
keeps it to tick 5550, Course 2 from tick 7717 to 10367, Course 3 from 11655 to 14346; the post-statue Course 2 laps
have none. Back on screen the first spawn waits up to 1 s. The timer is then at about 1 − I, so about 1/I − 1 spawns
come one a frame, then one an interval with the remainder kept (no rounding loss) until the pool is full; from then the
timer gains about 1.5 frames' time a lap. Clones in flight when the course left stay where they were (hidden, not
stepped) and go on. Steady income is the same with or without the reserve. What it changes is the refill after a
record break: 52 clones after 2.20 → 1.90 s refill in 0.17 s with it, 1.84 s without.

| T, C = 88 | old build | new build |
|---|---|---|
| all 88 in flight | at 280: T 1.74-1.76 s and 3.20 s; at 60: 1.46, 2.92, 4.36-4.40 s | at 280: any T ≥ 0.30 s; at 60: T ≥ 1.46 s |
| T 1.50 s, in flight | 76 at 280, 46 at 60 | 88 at 280, 88 at 60, 45 at 30 |
| T 1.06 s, in flight | 80 at 280, 64 at 60 | 88 at 280, 64 at 60, 32 at 30 |
| on-screen income | best at 280; 120 about −5 % (−15 % at a magic spot); 30 about −25 % | of ideal: 0.993-0.997 at 280, 0.99-0.995 at 120, 0.976-0.99 at 60, 0.97-0.98 at 30 |
| magic spots | yes | gone (±0.3 % at 280, ±1.5 % at 60 left) |
| off screen against on screen | about −0.8 % | about +0.3-0.5 % at 280, equal at 120 |
| RNG | about 0.3 draws a tick per on-screen course | 2 draws a rendered frame per on-screen course with clones: 12 a tick at 280 |

`FalseEnding-Any%`'s courses at 280 (in flight, income over ideal):

| Course | old build | new build |
|---|---|---|
| Course 1, 9 clones, 1.40 s | 9.0, 0.993 | 9.0, 0.994 |
| Course 2, 23 clones, 2.20 s | 22.8, 0.990 | 23.0, 0.997 (+0.7 %) |
| Course 2, 52 clones, 2.20 s | 50.0, 0.962 | 51.9, 0.997 (+3.6 %) |
| Course 2, 52 clones, 1.90 s | 47.5, 0.913 | 51.9, 0.997 (+9.2 %) |
| Course 3, 19 clones, 3.80 s | 19.0, 0.999 | 19.0, 1.001 |

The 353.32 s route replayed with the new clone code has 8.7 % more Cash at goal 110. That is a timing lead, not a
higher rate: a clone bought on screen spawns the next frame instead of an interval later, so its payouts start up to
one interval sooner. At tick 8500 the route is one Course 2 payout (1,008 W, about 0.5 s of income) ahead, and the old
buy order strays at goal 127.

**Record breaks.** A finish that beats the best time calls `updateClonePath`, which destroys every clone in flight and
pays each in full at once (both builds); the spawn timer runs on. With the reserve the pool refills at one a frame and
runs in step, and the new path's income is the full T/T′ of the old: Course 2 2.20 → 1.90 s +15.7 % (old build +9.9 %,
its sawtooth). Back-to-back breaks (the next lap T′ after the restart's overhead o) catch the pool just after it paid.
What each extra break gains for good, in clone payouts (payouts plus the in-flight clones' progress, float32
simulation of the new build):

| Course, clones, T′ | o | reserve kept | reserve lost |
|---|---|---|---|
| Course 2, 23, 2.00 s | 0.5 s | 17.1 | −0.2 |
| Course 2, 23, 2.00 s | 1.14 s | 9.7 | −0.3 |
| Course 2, 52, 1.90 s | 0.3 s | 43.3 | about −0.6 |
| Course 2, 52, 1.90 s | 0.5 s | 44.0 (K = 2) to 39.7 (K = 8) | about −0.6 |
| Course 2, 52, 1.90 s | 0.8 s | 29.7 | about −0.6 |
| Course 2, 52, 1.50 s | 0.5 s | 34.2 | −0.6 |
| Course 3, 19, 3.30 s | 0.2 s | 17.8 | about −0.1 |
| Course 3, 19, 3.30 s | 0.6 s | 15.4 | about −0.1 |

With the reserve lost a break gains nothing for good: its flush pays about C/2, but refilling at one per interval
leaves slot m empty for m·I, which costs the same C/2. It only brings Cash forward. So a break ladder pays only while
the course holds the reserve: in the 353.32 s route Course 2 between ticks 7717 and 10367 and Course 3 between 11655
and 14346, not the post-statue Course 2 laps. Planning model, pending a measured route: a K-break ladder is the K
fastest distinct known laps from the quick restart, slowest first, each breaking the record the one before set.

**Buy-max.** Old build: the first level on the touch (`OnTriggerStay2D`, in the physics step), then one a rendered
frame (300 a second on the studio clock, a dash freeze's and pause's repeat frames too): L levels in ⌈(L−1)/6⌉ ticks,
the touch's tick counted. New build: the first on the touch, then one a physics step, 50 a second at any frame rate.
The engine and turbo buy the second on the touch's own tick (the coroutine starts in the step's trigger callback);
a 1x probe hasn't confirmed that count yet. L levels in about L−1 ticks. The statue's `buyclone2` (29 levels) went from
5 ticks to about 28, `buyclonemult2` (9) from 2 to about 8. The coroutine buys on after the player leaves the box, but
the boosts come up to 0.5 s later.

## FalseEnding-Any% and FalseEnding2-Any% (2026-09-28)

Both end at the credits trigger after `buy:buydj`, `VMAN`'s way up (goals `at:8570,8300` to `at:22200,15900`).
`FalseEnding2-Any%` is the copy the economy work was tried on first. `FalseEnding-Any%` has its own recalculated economy
(below, 390.38 s); `fulltas --auto` took it from there to the record, 379.40 s ([Parts](#parts-goal-state-library-tail-windows-combine-scheduler-cost-to-go-macros-goals)).

**Build 25630570 (2026-09-30).** Every time and measurement below was made on the pre-update build. On the current one
([Clones and buy-max](#clones-and-buy-max-build-25630570)) on-screen clones pay more (Course 2's 52 clones at 1.90 s
+9.2 %), a clone bought on screen starts paying up to an interval sooner (the 353.32 s route holds 8.7 % more Cash at
goal 110, a timing lead), a faster Course 2 path raises its income by the full ratio (2.20 → 1.90 s +15.7 %, was
+9.9 %), and the statue's buy-max boxes buy one level a tick (`buyclone2`'s 29 levels about 28 ticks, was 5). The
Cash-bound waits and strays below move with it: on the ported engine 353.32 s strays at goal 127 (a Course 2 `Clone`
level it can now afford). Record-break ladders gain for good only while a course holds its spawn timer's reserve:
Course 2 before the run leaves it for Course 3 (ticks 7717-10367 in 353.32 s) and Course 3 (11655-14346), not the
post-statue Course 2 laps (planning model in that section, pending measurement).

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

**Record lap and fewer laps, 294 goals (2026-09-29).** Every stretch of the run is Cash-bound at its target buy (`buywj`,
`buyds`, `buystatue`, `buydj`). A faster lap or walk alone gains nothing: the time comes back as a wait at the next
target. A Course 3 lap 29 ticks faster (the 187-tick lap, which at goals 202, 260 and 270 strays into Course 3's
`CashPerLoop` box whenever the run can afford it, so those laps run 216 ticks) needed 35 idle ticks before the statue
walk and ended 12 ticks later. What pays is income. A clone pays at its path's end, and its path is the course's best
time, so one **record lap** raises a course's clone income for the rest of the run (and pays the clones in flight at
once: [Clones and buy-max](#clones-and-buy-max-build-25630570)). `fulltas-plan`'s record-lap line
(`record lap seeds: ...`) shows which course routes survive from the run's quick-restart landing. The human routes do
(`offline-best/1_none-1.4x-human.json`, `2_wj-2.2x-human.json`, given `"standing": true`): `1_none-1.42-human` and
`2_wj-2.22-human` play, while the 1.40 and 2.20 records die from the restart. With `1_none-1.42-human` as the lap at
goals 22-23 (the quick restart, the route, cut at its finish, then the old route on), Course 1's path goes from 1.62 s to
1.42 s. The lap costs about 105 ticks once, and then two of the laps before `buy:buywj` can go (three can't: `buywj`
isn't affordable). The statue walk then needs 34 idle ticks before its quick restart: arriving before the statue is
affordable, the walk runs on and buys Course 2's `Clone` box (a strict stray). The feasible waits aren't monotone
(40 passes, 50 strays, 60 on pass), so they are scanned. Dropping the sixth post-statue dash lap too (86-88 idle ticks
before the climb's quick restart; `buydj` is Cash-bound, and a dash lap earned about what standing does on the
pre-2026-09-30 build) gave 373.90 s
against 375.52 s. `FalseEnding-Any%` now has 294 goals: the old goals 88-91 (`start:1 finish:1` x2) and 288-289
(`start:2 finish:2`) are gone (`fulltas.json.before-fe-c1-record-lap`). A reorder of Course 2's buys from the buy
planner, built and measured, gained 1 tick.

**287 goals, 366.30 s (2026-09-29).** `fulltas --auto` from the 371.12 s record gave 370.28 s (judged) and 369.84 s. A
second `--auto` with `--focus 284-294` (the climb and the ending) found a climb 9 ticks faster. On the 369.84 s route that
climb needs 2 idle ticks before its quick restart (1 dies under the `buydj` box: the box isn't affordable yet), so it gave
369.70 s. Dropping the Course 1 lap right after `buy:buywj` (goals 89-90) runs straight from the `buywj` box to Course 2
with every later buy still strict: 367.32 s (-119 ticks, 292 goals; backup `fulltas.json.before-fe-drop-postwj-lap`).
The goals part of `--auto` found the same drop plus a dash lap, a Course 3 lap and a regrouped Course 2 buy (367.26 s,
287 goals, adopted through its submission; see [Parts](#parts-goal-state-library-tail-windows-combine-scheduler-cost-to-go-macros)).
On the 287-goal route, the climb found earlier only pads back to the same tick (98 idle ticks pass, 97 don't):
`buy:buydj` is Cash-bound to the tick, so time before it pays only through income. The only stretch that is pure
movement is the ending after `buydj` (10 goals, 1319 ticks): 30-second windows there found partial gains that
didn't survive the rest of the ending, and a run with 2-minute windows was stopped before its first pull. Course 3's
slow laps (216 ticks) get the 187-tick lap once a few idle ticks come before their quick restart. The buy walk before
the restart uses up the dash, and the 187-tick lap's opening dash needs it back. The laps then need a few idle ticks
before the statue walk too, or the walk buys Course 2's `Clone` box. FELaps2's swaps of those laps, merged with
`--auto`'s climb edits, gave 366.30 s (judged), and on `--auto`'s 366.14 s, 365.76 s. `--auto` then gave 365.56 s
offline (not judged). Two runs' routes combine by diffing each against their common base and splicing in the regions
that don't overlap. Where they overlap (both changed the same Course 3 lap), restarting `--auto` with the other route
as `--ref` works better than splicing by hand.

**Stalled at 361.54 s (2026-09-29).** Six `--auto` sessions after the 361.54 s record found nothing. Their windows and
reset studies found goals reached sooner (goal 41 by 27 ticks, 116 by 17, 263 by 26), and none lasted to the end: a
run ahead holds more Cash at every place after, and the old way on then brushes boxes it can now afford (Course 2's
`CashPerLoop` and `Clone` after goal 116, Course 3's row at goals 247-248), which the strict buys refuse. The idle ticks
that keep it out of them cost the lead back. The model had put 908 ticks of headroom where the target buys wait on
the clones' Cash (`buydj`'s climb 139 ticks, `buyds` and `buystatue`); held to the buys' spare Cash (above) it is 392.
Goal 91 keeps its 195: `reward:bonus1`'s own Cash pays the buy after it, so reaching it sooner moves that buy.

**Super chain on the ending's flat (2026-09-30).** After `buy:buydj` the run crosses the flat at y ~11660 (x 12160 to
17400, `FloatingOrigin` rebases on it) to the wall at x 19910. Each dash freezes time 14 frames at its start, and again
at its end unless a super cancels it (`endDash`: `Mathf.Sign(0)` is +1, so a rightward dash with no momentum pays it
too). The old route mixed plain ground dashes, double jumps and air dashes: 20.1-20.4 u per live tick (ticks plus
held frames / 5.6). The super chain makes 23.7 u per live tick. Each cycle: a ground dash on the landing tick, the jump
at dash frame 10 (the super on the dash's last tick: no end freeze, momentum 876), the jump cut, an air dash 2-4 ticks
after the super (momentum back at 0.95x when it ends), a fast-fall (stick down), and the next ground dash buffered 3
ticks before landing. Without the air dash a cycle makes 23.2. The chain is 0.71 s ahead of the old route at x 17300.
No flight from the flat's edge (super, air dash, double jump) holds its height to the wall: all drop to the lower
ground, so the old way on (a super on the lower ground, an air dash into the wall, the dash-boost climb) stays. Spliced
with the old climb where the air dash meets the wall: 355.43 to 354.72 s, then 354.54 s after `--auto --focus
271-276`. On the 355.18 s prefix (10 ticks sooner at `buydj`) it made 354.27 s. `--auto --focus 271-278` from there
made 353.55 s: its idle re-timing moved 8 waits under the boxes (353.57 s), and one window saved a tick at goal 271.
Before the chain, windows on this stretch had found only a few ticks. A chain gains only after several cycles, and
the windows scored at goal 273, 400 ticks on.

**The statue boost.** `buy:buystatue` (`BoostOldCourses`) sets Courses 1 and 2 to Course 3's reward tier plus their mod
and turns their boxes back on as buy-max boxes with 1.5x caps, at their old costs: `buyreward2` 39 -> 68 levels,
`buyclone2` 23 -> 52, `buyclonemult2` 6 -> 15, for about 7e5 W against 3.2e10 held. `FalseEnding-Any%` never went back
for them and lapped Course 3 for 6180 ticks to afford `buydj` (5.55e11). `FalseEnding2-Any%` drops from the statue down
the shaft (`at:7645,6050`, `at:6827.5,4537.8`, `at:4300,2850`), crosses `start:2`, touches the three boxes (one goal
each, 0.9 s; on the current build their levels come one a tick after the touch), climbs back to Course 3, stands
under the `buydj` box (`at:7951,6950,120,50`) and hops into it until the clones have paid (`buy:buydj` at 374.96 s
against 461.66 s). Records: 409.34 s (Course 3 lap before `buydj`), 408.28 s
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
`offline-best/fulltas-FalseEnding2-Any%-403.32.json`). Polish an ending with **Improve** (goals picked) instead.

## Status (2026-09-25)

- `main` has 609 goals, all from `python tools/plan_fulltas.py --write` after `reward:bonus1`: Course 2's economy up
  to dash (~236 s), the dash bonuses, then Course 3 to `buyclonespeed` and `buystatue`, Courses 1 and 2 bought to
  their boosted caps with a lap each, Course 3 to `buydj`, and Course 4 lapped, `buyprestige4`, then `buyswap`, with
  clone speed/size chance buys on Courses 3 and 4 (the plan counts their rolls at their average; ~15.8 min in the
  plan; its 3 -> 1 and 3 -> 4 transitions are guesses, no route exists yet).
- Not in the goals: the free global boxes with no known route (`buyfree3`, `buyfree4a/b/c`) and Course 4's 3e12 W
  `buyglobalreward4`.
- `ANY%` has 789 goals from `python tools/plan_fulltas.py --any-pct --out FILE`: `main`'s goals through `buy:buydj`,
  `reward:bonus5`, Course 5 farmed (with `reward:bonus4`, `reward:bonus15`, `buyfree5`, two `buyglobalreward5`) to
  `buy:buyprestige5`, then Course 4 farmed (its reward tier is 29.5 after the breaker; Course 5's 16.5 would take
  ~1e15 ticks to 9.49e34 W) to `buy:buyblockswap5` (~19.7 min in the plan). Its 3 -> 5 and 5 -> 4 transitions and the
  bonus detours are guesses; no route exists yet.
- The offline search seeds its laps with the saved standalone course, bonus and transition routes.
- Real-time playback warns on the overlay when it drops frames: Update-driven state (clone pay, purchases waiting on
  it) can then differ from the route's.
