# IGTAP TAS route rules

What makes a route count. The gate (`Solver.cs` `SaveSolverRoute`) enforces them; code and docs cite them by number
(`RULES.md 30`, `RULES.md exception 14`).

### Rules

1. Every route must be human-playable on a real controller.
2. Only send stick values a real controller can produce.
3. Presses and releases behave exactly like real button presses (the gate: every buffer must come from a press or
   release `MacroEngine` made within the game's window, counted in physics ticks — `MacroEngine.Clock`).
4. Keep the game's buffer timers: jump, dash, jump cut (the gate's windows, `Solver.cs` `AuditTick`: `JumpBufferTicks`
   12, `DashBufferTicks` 8, `JumpCutBufferTicks` 15, each +2 slack; a dash the game holds through a wall jump's lock,
   `DashHoldTicks` 6 after its last hold).
5. Never write `jumpBuffer`, `dashBuffer`, `jumpCutBuffer` or similar directly (the gate reads the game's own buffers,
   `Solver.cs` `AuditTick`).
6. Inputs change only on the 50 Hz physics tick.
7. The player body's interpolation stays on, as the game ships it (the TAS keeps it on), and a route is judged on what
   a player sees with it: `Movement.FixedUpdate` reads the last rendered frame's blend of the body's last two positions,
   so its position writes (a jump's `+2`/`+5`, a wall jump's `+1`, the dash's `−20`) pull the body back by that frame's
   lag, and `Movement.Update`'s step-up runs in every frame, ungated.
8. 1x playback runs on a fixed clock at 280 fps (`MacroEngine.PlaybackFrameRate`, `TasPlugin.FixedClockStep`): from a
   run's second physics tick a frame every 1/280 s of game time, half a frame off its first tick, as a player at the
   game's 280 cap sees them (28 frames every five ticks: 6, 5, 6, 5, 6); before that each tick's frames start half a
   frame after it. Turbo and the offline engine model the same frames (`MacroEngine.FrameGrid`, by the run's physics
   ticks), dash freezes included, so all three play a route alike.
9. The overlay shows `INTERPOLATION ON (280 FPS CLOCK)`.
10. Unlocks match the category exactly, on every tick (the gate: `wallJumpUnlocked`, `dashUnlocked`/`maxAirDashes`,
    `doubleJumpUnlocked`/`maxAirJumps`, `Solver.cs` `AuditTick`).
11. A category's label lists its unlocks in a fixed order: `WJ` wall jump, `DS`/`DSn` n air dashes
    (`dashUnlocked`/`maxAirDashes`), `DJ`/`DJn` n air jumps (`doubleJumpUnlocked`/`maxAirJumps`), `BS` block swap on
    dash (`blockSwapUnlocked`), `ORB` every refill orb (`globalUpgradeDict` `unlockJiggleDrops` 1, `moreRefreshOrbs`
    2), `ZIP` the zip movers (`zipMoversUnlocked` 1), `OM` the omnidirectional dash (`omniDashUnlocked`, the OmniDash
    box's toggle); `NONE` when empty. The registry is [`Categories.cs`](shared/Categories.cs).
12. Every unlock not in the label is off.
13. Without `BS` the coloured blocks start unswapped (blue active, as a new game has them; the save's state is
    put back after); with `BS` the game's own respawn sets them.
14. Turbo, offline and playback use identical unlocks.
15. Full-TAS runs gain unlocks only through the game's purchases (the gate: a Full-TAS run is checked against the
    unlocks its own purchases gave so far).
16. "Don't touch my unlocks" on: never write an unlock.
17. Bonuses run only in their first ladder category (`minCapabilities`), or up to their `maxCapabilities` in
    `courses.json`.
18. Courses and transitions run the ladder from `minCapabilities` up to `maxCapabilities`, or up to `WJ_DS_DJ_BS` when
    it's unset: Courses 1–5 and the Area 1 transitions stop at `WJ_DS_DJ`.
19. Outside the ladder, every course, bonus and transition runs `WJ_DS_BS_ZIP_OM`, `WJ_DS2_BS_ZIP_OM` and
    `WJ_DS_DJ_BS_ORB_ZIP_OM`. `WJ_DS_DJ_BS_ORB` and `WJ_DS_DJ_BS_ZIP_OM` run only where refill orbs are in reach,
    `WJ_DS_DJ_BS_ZIP` only where zip movers are (the targets are listed in [`Categories.cs`](shared/Categories.cs); the tree transitions count as bonus11, whose start they share):
    anywhere else they play exactly as `WJ_DS_DJ_BS`, and `WJ_DS_DJ_BS_ZIP_OM` as `WJ_DS_DJ_BS_ORB_ZIP_OM`. The ends of Bonuses 4 and 12
    need the zip movers: they run only the categories with `ZIP`, none of their ladder. Bonuses 17–21
    and 23 run no category, and neither does `unlockJumpDash` (the jump and dash refresh orbs bonus17 ends at, The Tree
    at level 10): their end is an unlock box (refill orbs, clone dust, buy max, the 1x and ∞x Start a VMAN
    speedrun boxes, block swap on dash), not a bonus stretch.
20. Courses 6–10 are Area 2 courses: a start gate is on only while its zone is loaded (Courses 1–5 zone 1, 6–10
    zone 2).
21. Every quick restart onto a course, bonus or transition loads the zone of the start gate or checkpoint nearest its
    start the game's way (`ZoneLoader`, as the zone activators do); the breaker plays no part. Courses 6–15 and bonus 11 start as if the free first atom had been bought: the atom
    room's door (`AtomColliderArea/Door`) is open, as a game load opens it with PlayerPrefs `OpenA2Door` (the atom box's
    `doorToOpen.JumpToNextState()`), without buying the atom, its prestige or any PlayerPrefs write (`CourseStart.OpenAtomDoor`,
    in the plugin's category start and the offline engine's course trace load). Course 9's end is reachable only through it.
    On a machine without the `OpenA2Door` key, the atom box's first buy then shuts the door: the game's first-buy branch
    (upgradeBox.cs:644-646) applied to a state a real game never has (a load opens the door only with the key or the box
    bought, upgradeBox.cs:264).
    A tree transition (`tree<L>to<C>`, courses.json `tree`: from bonus11's checkpoint by the atom room, its door open, to
    Course C's start checkpoint, its reset point, reached as a transition reaches a bonus, not its start gate) sets The
    Tree at level L the game's load way at every quick restart onto it (`TreeLevel`: the plant and the first L tree boxes
    in segment order bought once, `TimesUsed` and `instaGrowSegments` as `TreeController.load`, a bought-out box
    deactivated as its `Start`, the segments above off as `Awake` leaves them), the Area 2 door open from level 8, the
    Zone 2→1 path open and Area 1 overgrown from 23; no unlock is written. The save's tree and path are put back before a
    save. Levels whose box changes nothing in the world are one target, by the highest (0, 1, 2, 3, 5, 6, 7, 10, 11, 12,
    13, 14, 15, 16, 17, 19, 20, 22, 27). Course 10 keeps only levels 13 and up (`tree13to10` through `tree27to10`):
    `tree0to10` through `tree12to10` are dropped. Course 6 keeps only level 0 (`tree0to6`): its start is accessible on
    Course 0, so `tree1to6` through `tree27to6` are dropped. Courses 7 and 9 keep only levels 0 and 3 (`tree0to7`,
    `tree3to7`, `tree0to9`, `tree3to9`): levels 1 and 2 play as level 0, and levels 5 up are dropped. Course 8 keeps only
    level 10 (`tree10to8`); its other tree targets are dropped.
22. Their lowest category is `WJ_DS_DJ_BS`.
23. Courses 11–15 are Courses 1–5 with Area 1 overgrown: the game's own Course 1–5 gates and timer on the overgrown
    terrain (spikes, moss, zip movers) and boxes (no movement, prestige, breaker-light or unlock-prestige box in Courses
    1–5; the RP boxes and Course 6's atom RP box shown).
24. Every quick restart onto one sets Area 1 overgrown the game's way (`currentA1State`, then
    `OvergrowthLoadScript.OnLoad`, as the zone loader does). A save that never bought the OmniDash box
    (`Zone2toZone1PathOpen` off) never ran its `SetA1CourseTiers`, so the restart stands in for that call's boxes too
    (which go and come, the caps and buy max), not its course prestige; it undoes them when Area 1 is set back. Onto
    Courses 1–5 it sets Area 1 back to not overgrown, and every other route runs in the save's own state.
25. Saves wait until the save's state is back.
26. Their only categories are `WJ_DS_DJ_BS` and the categories outside the ladder that run there (19); transitions to
    or from them aren't supported.
27. Time runs from the course's start gate to end gate ([`docs/course-timer.md`](docs/course-timer.md)).
28. Finishing a different course never counts.
29. Score courses by the game timer, not tick counts: that is the runs record (the gate: a replay from the course start
    must finish in the recorded time, `Solver.cs` `RouteViolation`).
30. A course's farm is its steady-state loop: the laps that repeat finish to finish, by a quick restart after the finish
    (what a lap carries into the next, a buffered jump or a coyote count, carries) or by the drop through the end gate
    into the next lap. A farm route is a lead (the course start through its last lead finish) and the loop (the laps after
    it, through the last one's finish; more than one only when what a lap carries alternates). Its time is a lap's share
    of the loop's cycle in live time (31's ticks and held frames), once the loop repeats exactly (the gate: a replay from
    the course start plays the lead and the loop four times, every loop finishing on its own last input, the last two
    cycles equal and the cycle the claimed one, `Solver.cs` `EvaluateLoop`). Named `<course>_<CAT> farm <lap>`, seconds to
    4 places. A farm route saved as one lap from the quick restart is stale.
31. Bonuses, transitions, purchases: count physics ticks, dash/spring ticks included. A bonus ends when its reward box (the
    box nearest its end rect) takes the player's hit, its `OnTriggerStay2D` past the box's cooldown, the step the game buys
    it, not at the first touch of the rect (a trigger's first step is Enter, never a buy). A Full-TAS run is timed in live
    time, what it takes at 1x: its physics ticks at 0.02 s plus the frames it stood still in (a dash freeze's, a pause's,
    1/280 s each on the 280 fps clock; `MacroEngine.HeldFrames`, `MacroEngine.LiveSeconds`), and reported with both,
    live time and game frames (the ticks). Turbo, the offline engine and 1x count the same frames (the gate: a replay's
    live time must match). Every class of a run (54) is timed so and named by it: `fulltas/<run>_NONE <time>` (major),
    `fulltas/<run>_NONE nmg <time>`, `fulltas/<run>_NONE human-major <time>`, `fulltas/<run>_NONE human-nmg <time>`.
32. Nothing after the finish is part of the route; a farm route's loop is (30).
33. No teleports, position offsets or moving the player.
34. Only reset: the game's quick restart, `Movement.respawn(true)` (the gate refuses "a quick restart on two ticks in a
    row").
35. Deaths respawn where the game's checkpoint setting sends them: the studio's **Checkpoint respawns** (one setting
    for every route, trace and search; the overlay shows it) off, at the course reset point; on, at the last in-game
    checkpoint touched.
36. Off a course both go to that checkpoint.
37. Area goals (`at:`) are not checkpoints.
38. Purchases count only when the game itself buys the box. A Full-TAS run buys only its next goal's box unless its
    `data/runs/<run>.json` sets `"buys": "loose"` (`VMAN`): then other purchases stand, and a box a later `buy:` goal names,
    bought early, counts for that goal ([`docs/fulltas.md`](docs/fulltas.md#loose-buys)).
39. Never replace game logic; patches only read, input, skip saves (and playback's wall-clock reads, exception 2).
40. Never change Unity's clock or fixed timestep.
41. Never edit the game's save files.
42. Never autosave while category unlocks, turbo or an injected lab map are applied.
43. Save category routes only through `SaveSolverRoute` (the gate: it replays the route from the course start and
    refuses it unless every tick obeys what a human with that category's unlocks can do).
44. Never loosen the gate or add another save path.
45. Search offline in `enginesim`, never in the game.
46. Game only captures traces, confirms bests, runs `validate.py`.
47. No game-judged search loops or extra game instances.
48. Agents never start the studio's Optimize tab.
49. `enginesim` can't do something? Fix `enginesim`, don't work around.
50. Offline replay must match the game tick for tick.
51. `FloatingOrigin` rebases in turbo and offline as at 1x: turbo runs the game's own `LateUpdate` and `MoveOrigin`, and
    a turbo restore puts the world back at its snapshot's origin (`enginesim/README.md`). A course, bonus or transition
    start is judged at one origin, whatever ran before: where the game's own rebase on the player standing there leaves it.
52. Only save strictly faster times: record by the game timer, farm by a lap of the steady loop in live time (30); human
    classes: strictly lower score. A two-way course (Courses 1-3) also keeps a record of each direction, forwards and
    backwards, by the game timer among routes of that direction (`AGENTS.md`, route names).
53. Trace every new best and name its trick.
54. A Full-TAS run keeps four bests, its classes: `major` (any tech) and `nmg` (no major tech) by live time, `human-major`
    and `human-nmg` the same two by score. Score: live time plus 2 s a spike-grace tick, 5 s a frame-perfect input and
    20 s a back-to-back pair of the whole route's difficulty (per run, `data/runs/<run>.json` `"human": {"grace", "framePerfect",
    "backToBack"}`). A major or nmg span adds nothing of its own: what makes a glitch hard is the inputs in it. The
    difficulty is the engine's count over humanize's version of the route (`difficulty`): each input (a stick change, a
    jump press or release, a dash, a jump on a dash frame) moved alone a tick at a time; frame-perfect: it works on one
    tick only (20 ms); back-to-back: two actions (inputs on one tick are one) under 3 ticks (60 ms) apart that no timing of
    the inputs inside their windows avoids. A grace tick is a step on spikes the run lives through. A human class has two
    jump buttons (`Jump`, `Jump2`, a release lifting both): a jump press while both are down ends its replay, and
    its stick is digital: any part tilt (the tick's, the lock's or the dash freeze's stick anything but 0 or full) ends
    it too, so the KB wall clip stands and the GC one doesn't (`MacroEngine.HumanClass`); `major` and `nmg` may make
    either. A tech's category (glitchless, nmg, major) is
    `techs.json`'s; spike grace stays glitchless (exception 8). A class is judged by the engine's tech checks: after its
    replay the gate runs `IGTAP.EngineSim techs-used <trace> <route> --json` from the run's trace, refuses an `nmg` or
    `human-nmg` route with a major span (naming the tech, its category and tick), scores a human one (its difficulty
    measured there) and keeps each route's result (`fulltas-classes.json`). A tech no check detects isn't counted
    (`techs-used` lists them). A course (not a bonus or transition) keeps the two human classes of its record too,
    `<course>_<CAT> human-major <score>` and `<course>_<CAT> human-nmg <score>`, the score on the game timer with the
    default weights: the gate replays the route from the course start as a record, then runs `techs-used` from the
    course's start trace, and saves it only under a strictly lower score (by its name's 0.01).

#### Exceptions

1. Relaxes rule 6. The one exception is the dash freeze's stick (`turnX`/`turnY`, `MacroStep.dashTurnX`/`dashTurnY`): a
   dash pressed with the stick at rest takes the stick the game reads before the next tick, as a player tilting the
   stick during the freeze turns it. Without the omnidash only its sideways part counts; with it the vertical does too
   (such as (0.35, 1): the game snaps the vertical to full tilt). It is legal.
2. Relaxes rule 39. The interpolation/fixed-clock patch (rules 7–9), the Vman credits' crash (below) and playback's
   wall-clock reads (the dash freeze, the camera's easing and `ObjectBobber` count the fixed clock's frame instead of
   the wall clock, [`enginesim/README.md`](enginesim/README.md#probe)) change game logic; the other places a patch does are
   the exceptions below that relax rule 39 (3, 4, 5, 10, 11, 12, 13 and 14), each named there.
3. Relaxes rule 39. The one other patch: a Vman speedrun's credits (`FakeCreditsControlScript.IE_DoVmanCredits`, the
   `VMAN` run's last goal) crash the game on purpose (`Utils.ForceCrash`) after writing PlayerPrefs
   `VmanCoursesCompleteTemp`; the plugin drops both calls. Both come after the goal, so no route sees them.
4. Relaxes rule 39. The one RNG write: the Full-TAS runs' fixed seed, `UnityEngine.Random.InitState` at the fresh new
   game before its start snapshot; nothing else writes the RNG.
5. Relaxes rule 39. A setup write: a Vman Full-TAS start (`docs/fulltas.md`) sets, in a fresh new game,
   Area 1 overgrown, `vmanTime` and `VmanStartsWithOmniDash` to their boxes' full effect and the player under the 1x
   Vman start box; the game itself buys the box and loads the Vman world the run starts in.
6. Relaxes rules 33 and 15. The one exception to "no teleports" and "unlocks only through the game's purchases": a
   Full-TAS run with a `savestate` (`docs/fulltas.md#savestate-starts`) starts from that savestate, restored with its
   unlocks in the settled new game. It sets no checkpoint: a quick restart before a real checkpoint (or a course reset
   point) ends the run. With `rebaseAt` it also rebases the world around that point once the restored player rests, as
   FloatingOrigin would have with the player there, so the run's next rebase happens where the test needs it. It tests
   a stretch; it is not a whole-game route. With `tree` it also sets The Tree right after the restore, at a level as
   rule 21 sets a tree transition's (Area 1 overgrown and the path open from 23), or with an array exactly those grown
   segments (the save's `_grownSegments`; the boxes that grow one of them bought), and loads the zone at the player.
7. Relaxes rule 33. The one exception to "no teleports" for a lab: a lab map (`data/labmaps/<id>.json`,
   [`enginesim/README.md`](enginesim/README.md#lab-maps)) is a custom level piece injected additively into empty map
   space (a `lab space` region, far from routes) to demonstrate one tech exactly, with its own start and finish box. It
   is lab-only: never a route, never on a route's path, and never saved — `SaveSolverRoute` refuses while one is
   injected. It is always restored afterwards (the injected objects destroyed, anything the session touched put back):
   at the demo's end, on a scene change, or by `POST /api/labmap/restore`.
8. Relaxes rule 1. Spike grace is allowed: up to two overlapping ticks.
9. Relaxes rule 27. Flying starts allowed: time counts from the last start-gate crossing.
10. Relaxes rule 39. The Full-TAS reward refresh: each course's 5 s `UpdateReward` timer (`courseScript.Start`'s
    `InvokeRepeating`, a random phase per session) is off while a Full-TAS run plays at 1x, and the courses update their
    reward every 250 physics ticks from the run's start instead (`MacroEngine.RefreshRewards`), in turbo and the offline
    engine alike; the timer is back on after the run. A course's clones pay its reward as last updated, so the phase
    decides the Cash.
11. Relaxes rule 39. Presentation doesn't draw the Full-TAS RNG: the run animation's footstep event
    (`PlayerAnimationEventProxy.RelayFootstep`, on rendered frames), a floating number (`spawnNewFloatingNumber`) and a
    text bobber's bob phase (`ObjectBobber.OnEnable`: the statue's text, a buy-max box's "buy N" text, which its multibuy
    timer turns on at 1x only) play and put `UnityEngine.Random`'s state back after them
    (`TasPlugin.PresentationDraw`; the offline engine makes no bobber draw in a Full-TAS
    run, `Economy.Activate`), so the fixed seed's draws are the same at 1x as judged.
12. Relaxes rule 39. The `Start` of what a run wakes, switched on before Unity ever started it: the draws of a
    `LightFlicker` (a box's pulse light, 1 draw), a `RandomAnimSpeedComponent` (a tree segment's lights, 2, 3 when
    broken) and a `JiggleDropScript` (a refill orb, 1), and an `upgradeBox`'s gameplay writes (a tree segment's box,
    `buytree0` first; upgradeBox.cs:199-298): `baseCap = Cap`, `Cap = round(baseCap × baseCapMult)`, the `init` base
    cost (course tier scaling) or `CalcBoxCost`, `deactivate` when `Cap ≤ TimesUsed`, off when not `visible`, the statue
    text, the Vman omnidash box, `SetBoxPromptActiveState`, `localUpgradeScript` and, bought already, its tree's
    `growSegments`. Unity runs `Start` before the behaviour's first `Update`: in the same rendered frame when it was woken
    in the physics step (a tree box's buy), first thing in the next when woken at a frame's end (`IE_Grow` turning a
    segment's box on), before that frame's `FixedUpdate` when it has one (a 1x probe: a box on in tick 961's last frame
    starts before tick 962's). Turbo makes them at that modelled frame (`TurboRunner.StartPass`, for a box the game's own
    `Start`) and puts `UnityEngine.Random`'s state and the box's fields back around Unity's later real `Start`
    (`BeforeDrawingStart`, as `TasPlugin.PresentationDraw` does for exception 11), so it doesn't apply twice; the offline
    engine makes the same at the same frame (`Economy.StartPass`, the box's texts cut). The atom collider's `Start` (an
    NP recompute) is made the same way. A box whose `Start` would move a door (the Area 2 door box's bought out before it,
    the atom box's, which reads the machine's PlayerPrefs; upgradeBox.cs:260-267) ends the run (`WokenStarts.Unmodeled`);
    the Area 2 door box bought out after its modelled `Start` has a real one that jumps the door, whose stop is put back
    with the box's fields. Its Steam and text parts are presentation, left to Unity. 1x is untouched. A course's `Start` draws too
    (`InvokeRepeating("UpdateReward", Random.Range(0, 5), 5)`), but no run wakes a course: a zone load in a run (a zone
    activator's `ZoneLoader.IE_Load`, in its tick's third rendered frame) turns zones on and off, never the `Courses` root.
13. Relaxes rule 39. The one PlayerPrefs write: the machine's `OpenA2Door` key, which outlives saves (only the pause menu's
    delete save clears it, pauseMenuScript.cs:360). The atom box's `Start` opens the atom room's door when it is set
    (upgradeBox.cs:264), its first buy opens the door only when it isn't (:644) and every buy sets it (:648), so a new game
    on a machine that ever bought an atom starts with that door open. The TAS holds the key as state
    (`SavestateService.SetAtomDoorKey`): a Full-TAS new game loads without it (a savestate start: with the savestate's),
    a turbo restore puts its snapshot's back, and a savestate keeps it and puts it back. Category starts leave it as it is
    (21).
14. Relaxes rule 39. Turbo's frames read the player where 1x shows it: the game code a turbo frame runs (every
    `Movement` method, its coroutines included, `courseScript`'s and `TeleporterScript`'s, and the plugin's hooks in
    them) reads `Transform.position` through `TurboRunner.BlendRead`, which returns the frame's interpolation blend for
    the player's transform while turbo runs that frame's `FixedUpdate`s or `Update`s and the game hasn't written it
    since, and the transform itself otherwise (at 1x, but for the `Movement.FixedUpdate` right after a turbo handoff).
    Turbo used to write the blend into the transform; Unity syncs a written transform into the `Rigidbody2D`, and any
    body write makes Box2D recompute its sweep centre from the position, a float step off the one 1x carries from step
    to step, so turbo drifted from 1x. The transform and body are now written only where the game writes them (and a
    turbo restore); the game's writes land as at 1x.

Details: [`AGENTS.md`](AGENTS.md). The whole game from a new game as one run with goals:
[`docs/fulltas.md`](docs/fulltas.md). What a human can put into the game, and how routes match it:
[`docs/human-input.md`](docs/human-input.md). Where past divergences from 1x were found and fixed:
[`docs/fidelity-log.md`](docs/fidelity-log.md).
