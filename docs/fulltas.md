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

## fulltas.json

In the game's `BepInEx/config/IGTAPTasMod/` (edits are picked up within a second, never mid-run):

```json
[{ "name": "main", "goals": ["start:1", "finish:1", "buy:buyreward", "start:1", "finish:1", "buy:buywj", "start:2"] }]
```

| Goal | Reached when |
|---|---|
| `start:N` | Course N's start gate starts its timer (it wasn't timing). |
| `finish:N` | The player's own finish at course N's end gate. |
| `buy:buyreward+buyclone` | The game bought each of these boxes (any order). Course 1: `buyreward`, `buyclone`, `buywj`, `buyomni` (the Vman speedrun's free omnidirectional dash + 1 dash, there only in Vman with `VmanStartsWithOmniDash`); Course 2: `buyreward2`, `buyclone2`, `buyclonemult2`, `buyglobalreward2`, `buyds`; Course 3: `buyreward3`, `buyclone3`, `buyclonemult3`, `buyfast3`, `buyglobalreward3`, `buydj`, `buyfree3`, `buyclonespeed`, `buystatue`; Course 4: `buyreward4`, `buyclone4`, `buyclonemult4`, `buybig4`, `buyfast4`, `buymaxsize4`, `buyglobalreward4`, `buyswap`, `buyfree4a`, `buyfree4b`, `buyfree4c`, `buyprestige4`; Course 5: `buyreward5`, `buyclone5`, `buyclonemult5`, `buyglobalreward5`, `buybig5`, `buyfast5`, `buyprestige5` (trips the breaker), `buyblockswap5` (swap coloured blocks when dashing; the box sits on Course 4's row and appears only after the breaker trips), `buyfree5` (secret #2). |
| `reward:bonusN` | The game buys bonus N's reward box (the upgrade box nearest its end rect: the bonus's unlock). |
| `at:x,y,w,h` | An area goal: the player's colliders touch the rect centred on (x, y), w by h, in game-world coordinates (`courses.json`'s frame, the studio map's readout; see [`enginesim/README.md`](../enginesim/README.md)) (checked after each tick, as a bonus's end rect is). A search aid: it splits a long stretch into windows, and dropping it lets the search find another way. Not the game's checkpoints (`checkpointScript`), which set where a death or quick restart off a course respawns. |
| `at:x,y,w,h,from=left+bottom` | The same area goal, entered from one of these sides (`left`, `right`, `bottom`, `top`): on the tick the colliders first touch the rect, after the tick before they were wholly outside it on that side. Touching it any other way doesn't count; the player must leave and come in again. |

A quick restart right after an area goal (the last goal reached is an `at:`) takes that goal back: the run has to
pass it again before the next goal counts. An area goal marks the way the run goes on, so a window can't touch it and
restart to hand the next window a standing start. Quick restarts after any other goal (a purchase, a finish) are
untouched.

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
hyper, chained hyper, ground bounce, hyper ground bounce, the up-diagonal dash into a wall; see [`human-input.md`](human-input.md#omnidirectional-dash-vman)).

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
(`dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release`) to reach the solver's copy.

Goals chain, and a window's search follows that: its explore cells keep what the player has left in the air (wall
jumps, midair jumps, air dashes), so a faster arrival that spent its dash doesn't replace one that still has it for the
next goal, and until the window reaches its last goal, cells that have reached more of the run's goals are picked more
(`OfflineSolver` `Weight`; run time doesn't count there: a run that wanders longer isn't closer to the next goal).
While the next goal is an `at:` rect, cells nearer it are picked more among those with the same goals reached: a
cell's nearness is (nearest + 48) / (its distance + 48), the distance from the player to the rect and the nearest any
of the worker's cells came (`GoalReach`), so avoid boxes cut the corridor's dead ends and the rect pulls the search
along it.

## Savestate starts

`"savestate": "<id>"` on a run (a file in `savestates/`) starts it from that savestate: once the new game (with
`"start": "vman"`, the Vman world) has settled, the savestate is restored, and the run's start is the player's next
rest there (10 grounded, still ticks), after `FloatingOrigin` has rebased around the new position. The start's
unlocks are what the savestate carries: a savestate captures none, but its `movementFields` may list
`wallJumpUnlocked`, `dashUnlocked`, `omniDashUnlocked`, `doubleJumpUnlocked`, `blockSwapUnlocked`, `maxAirDashes` and
`maxAirJumps`, and the unlock audit starts from them (then follows the run's purchases). With "Don't touch my
unlocks" on, a restore skips those fields and a savestate start that has them fails. `POST /api/session/newgame`
takes `savestate` too. Changing a run's savestate retires its routes like a goals edit.

`Test-No-Omni-Skip` is the first: `VMAN`'s last four goals (the tunnel floor's corner exit, the spike tunnel, its
right end, the credits trigger) from savestate `test-no-omni-skip` (`c1382aa8df724bcc92c07a3cd8c577df`), standing at
world (20933, 14987) under the tunnel with wall jump, 1 dash (no omnidash) and 1 double jump. A test of whether the
stretch goes without the omnidash; it teleports and writes unlocks, so it is not a whole-game route.

## Seed

Unity's RNG is deterministic for the runs: every Full-TAS new game starts it from one fixed seed (the plugin's
`FullTasSeed`, `UnityEngine.Random.InitState` at the fresh new game right before its start snapshot, and again before a
real-time playback's first tick). The snapshot therefore carries the seeded `UnityEngine.Random.State`
(`"random": [s0, s1, s2, s3]` in the trace's start state and after every tick, and the trace header's `fulltas` carries
the `seed`), so the turbo judge, the trace and the offline search all start from the same state. The seed is fixed and
unoptimized (`SEED (unoptimized) <value>` on the overlay); nothing user-settable writes the RNG.

Real-time play draws `UnityEngine.Random` on every rendered frame (LightFlicker, animation speeds), so a real-time
playback's rolls differ from the turbo replay's even from the same seed; the gate (turbo) is the judge.

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
  seeded with the saved best), **Play** (real time from a new game), **Solve** / **Solve unsolved** / **Stop**
  (`/api/fulltas/solve {name, minutes, windowMinutes, window, threads, goal, unsolved}` runs `IGTAP.EngineSim fulltas`). `/api/state` has
  `fullTas`; its `solve` has the solver's events (`events`, the latest `status` as `progress`), `error` (didn't start,
  crashed) and the raw `log`; `lastResult` is the last Play or gate check (`kind`, `error`, `reached`, `expected`, ...).
- **Solve** polishes the goals the best reaches, windows from goal 1 on; **Solve unsolved** (`unsolved: true`, the
  solver's `--unsolved`) reaches the goals the best misses first, starting at the first of them. Both recapture the
  trace (seeded with the saved best) when the run's goals or start changed since it was taken: a solve searches the
  goals in its trace's header. **Only goal** N (`goal: N`, the solver's `--goal N`) makes **Solve** polish just the
  **Window** goals ending at goal N (window 2: from goal N−2 through N−1 to N), window after window, keeping the rest
  of the route as it is and scoring the time at goal N. `threads` 0 or missing (the site sends none) is cores − 2.
- The Solve panel shows the route so far or the record, this solve's gain, what the search is on right now, and a feed
  of records, finds, drops and gate checks. Any error gets a card whose **Copy details** puts a report (problem, best,
  events, command, log) on the clipboard.
- **Goals in game** (`POST /api/settings/fulltas-goals {name}`, `""` turns it off; settings `fullTasGoalsRun`, `/api/state`
  `fullTas.goalsOverlay`) draws the run's `at:` and `reward:` rects in game, numbered, where the goal check tests them
  (`courses.json`'s frame plus the current origin), independent of the main overlay. While that run plays, reached
  goals are green and the next one yellow. The run's avoid boxes (forced-paths.json, the solver's copy) are drawn in
  red, and a box added or removed on the map shows up within a second.

## Offline search

```powershell
& $exe fulltas $traces\fulltas-main.json --minutes 60 --window-minutes 1 --window 2   # --unsolved, --no-submit, --fresh, --threads N, --goal N
```

- The best is `<traces>\fulltas\<run>.json` (or the trace's inputs when better or when there is none).
- By default the search polishes the goals the best reaches (all of them once it's complete); a best that reaches none
  is reached first. `--unsolved`: a complete route first: until every goal is reached, only the first goal not reached
  yet is searched (`reach` windows): from the last reached goal, then redoing one, two, three goals before it, each
  round with a longer budget (`--window-minutes` × round). A reach window ends as soon as it finds a route to its goal:
  that route is spliced in and saved right away, and the next window searches on from it (the rest of the budget isn't
  spent). Polishing starts once a run reaches every goal.
- Polishing: windows of W goals (default 2) from goal 1 up to the last goal the best reaches: each worker replays the
  best up to goal i-1 (`Simulation.Advance`), starts there (`SetStart`) and searches for the fastest way through goal
  i+W-1. The first goal's split is kept only as part of the whole: a later goal reached sooner is what counts, so a lap
  that ends slower but sets up the next one wins.
- A window's best is spliced into the run with the old tail after it; when the old tail no longer reaches the rest (the
  state differs), the following windows are searched again from the new state (`repair`), seeded with the old tail,
  and the run is kept only if it ends sooner.
- Routes found from a start with the same movement state for the same next goal seed each other (the repeated laps).
- Each window's explore archive (input prefixes, as a course solve's) is kept in
  `archive\fulltas-<run>\<first>-<limit>-<hash>.json`: a retry or a later solve of the same window from the same prefix
  resumes it; `--fresh` ignores it.
- A complete, faster run goes to `offline-best\fulltas-<run>-<time>.json` and to the gate (`/api/fulltas/submit`).
- Besides its log lines the solver prints one `@fulltas {json}` line per event (`FullTas.Emit`): `session`, `phase`
  (`reach`/`polish`), `window` (`kind`, goals `first`..`limit`, `target`, `budget`), `status` (each second), `found`,
  `windowEnd`, `record` (`previous`, `best`), `dropped`, `note`, `gate`, `done`. Times are seconds from the run's start.

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
faster. The block swap (`buyswap`) is modeled as the purchase's bookkeeping
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
at the breaker (`Unmodeled physics: ...`). `buyblockswap5` sets `blockSwapUnlocked` and ends the run (`Unmodeled
purchase: block swap on dash`), so it works as a run's last goal. The turbo gate's snapshot restores what the breaker
changes (box GameObjects and enabled flags, Area 1's state, the breaker's lights, the blocks' colliders). The lights it
keeps are the game's own (`GameBreakerLights`: GlobalLight and BreakerLights on unless Area 1 is overgrown, GlobalLight
off once tripped, each area light per `activeLights`), never the studio's **Breaker lights** forcing, so a new game's or a
Vman speedrun's start is lit with no breaker light on.

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
