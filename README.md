# IGTAP TAS Studio

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
8. 1x playback runs on a fixed clock at 280 fps (`MacroEngine.PlaybackFrameRate`, `TasPlugin.FixedClockStep`: game time
   steps on a grid half a frame off each physics tick), and turbo and the offline engine model the same frames
   (`MacroEngine.FrameGrid`), dash freezes included, so all three play a route alike.
9. The overlay shows `INTERPOLATION ON (280 FPS CLOCK)`.
10. Unlocks match the category exactly, on every tick (the gate: `wallJumpUnlocked`, `dashUnlocked`/`maxAirDashes`,
    `doubleJumpUnlocked`/`maxAirJumps`, `Solver.cs` `AuditTick`).
11. A category's label lists its unlocks in a fixed order: `WJ` wall jump, `DS`/`DSn` n air dashes
    (`dashUnlocked`/`maxAirDashes`), `DJ`/`DJn` n air jumps (`doubleJumpUnlocked`/`maxAirJumps`), `BS` block swap on
    dash (`blockSwapUnlocked`), `ORB` every refill orb (`globalUpgradeDict` `unlockJiggleDrops` 1, `moreRefreshOrbs`
    2), `ZIP` the zip movers (`zipMoversUnlocked` 1), `OM` the omnidirectional dash (`omniDashUnlocked`, the OmniDash
    box's toggle); `NONE` when empty. The registry is [`Categories.cs`](Categories.cs).
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
    `WJ_DS_DJ_BS_ZIP` only where zip movers are (the targets are listed in [`Categories.cs`](Categories.cs)):
    anywhere else they play exactly as `WJ_DS_DJ_BS`, and `WJ_DS_DJ_BS_ZIP_OM` as `WJ_DS_DJ_BS_ORB_ZIP_OM`. Bonuses 17–21
    and 23 run no category: their end is an unlock box (refill orbs, clone dust, buy max, the 1x and ∞x Start a VMAN
    speedrun boxes, block swap on dash), not a bonus stretch.
20. Courses 6–10 are Area 2 courses: a start gate is on only while its zone is loaded (Courses 1–5 zone 1, 6–10
    zone 2).
21. Every quick restart onto a course, bonus or transition loads the zone of the start gate or checkpoint nearest its
    start the game's way (`ZoneLoader`, as the zone activators do); the breaker plays no part. Courses 6–15 start as if the free first atom had been bought: the atom
    room's door (`AtomColliderArea/Door`) is open, as a game load opens it with PlayerPrefs `OpenA2Door` (the atom box's
    `doorToOpen.JumpToNextState()`), without buying the atom, its prestige or any PlayerPrefs write (`CourseStart.OpenAtomDoor`,
    in the plugin's category start and the offline engine's course trace load). Course 9's end is reachable only through it.
22. Their lowest category is `WJ_DS_DJ_BS`.
23. Courses 11–15 are Courses 1–5 with Area 1 overgrown: the game's own Course 1–5 gates and timer on the overgrown
    terrain (spikes, moss, zip movers).
24. Every quick restart onto one sets Area 1 overgrown the game's way (`currentA1State`, then
    `OvergrowthLoadScript.OnLoad`, as the zone loader does); onto Courses 1–5 it sets it back to not overgrown, and
    every other route runs in the save's own state.
25. Saves wait until the save's state is back.
26. Their only categories are `WJ_DS_DJ_BS` and the categories outside the ladder that run there (19); transitions to
    or from them aren't supported.
27. Time runs from the course's start gate to end gate.
28. Finishing a different course never counts.
29. Score courses by the game timer, not tick counts: that is the runs record (the gate: a replay from the course start
    must finish in the recorded time, `Solver.cs` `RouteViolation`).
30. A course's farm time counts physics ticks from the route start (the quick restart) to the finish (the gate: the
    replay's physics tick count must match).
31. Bonuses, transitions, purchases: count physics ticks, dash/spring ticks included. A Full-TAS run is timed in live
    time, what it takes at 1x: its physics ticks at 0.02 s plus the frames it stood still in (a dash freeze's, a pause's,
    1/280 s each on the 280 fps clock; `MacroEngine.HeldFrames`, `MacroEngine.LiveSeconds`), and reported with both,
    live time and game frames (the ticks). Turbo, the offline engine and 1x count the same frames (the gate: a replay's
    live time must match).
32. Nothing after the finish is part of the route.
33. No teleports, position offsets or moving the player.
34. Only reset: the game's quick restart, `Movement.respawn(true)` (the gate refuses "a quick restart on two ticks in a
    row").
35. Deaths respawn where the game's checkpoint setting sends them: the studio's **Checkpoint respawns** (one setting
    for every route, trace and search; the overlay shows it) off, at the course reset point; on, at the last in-game
    checkpoint touched.
36. Off a course both go to that checkpoint.
37. Area goals (`at:`) are not checkpoints.
38. Purchases count only when the game itself buys the box. A Full-TAS run buys only its next goal's box unless its
    `fulltas.json` entry sets `"buys": "loose"` (`VMAN`): then other purchases stand, and a box a later `buy:` goal names,
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
52. Only save strictly faster times: record by the game timer, farm by physics ticks.
53. Trace every new best and name its trick.

#### Exceptions

1. Relaxes rule 6. The one exception is the dash freeze's stick (`turnX`/`turnY`, `MacroStep.dashTurnX`/`dashTurnY`): a
   dash pressed with the stick at rest takes the stick the game reads before the next tick, as a player tilting the
   stick during the freeze turns it. Without the omnidash only its sideways part counts; with it the vertical does too
   (such as (0.35, 1): the game snaps the vertical to full tilt). It is legal.
2. Relaxes rule 39. The interpolation/fixed-clock patch (rules 7–9), the Vman credits' crash (below) and playback's
   wall-clock reads (the dash freeze, the camera's easing and `ObjectBobber` count the fixed clock's frame instead of
   the wall clock, [`enginesim/README.md`](enginesim/README.md#probe)) are the only places a patch changes game logic.
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
   a stretch; it is not a whole-game route.
7. Relaxes rule 33. The one exception to "no teleports" for a lab: a lab map (`labmaps/<id>.json`,
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

Details: [`AGENTS.md`](AGENTS.md). The whole game from a new game as one run with goals:
[`docs/fulltas.md`](docs/fulltas.md). What a human can put into the game, and how routes match it:
[`docs/human-input.md`](docs/human-input.md). Where past divergences from 1x were found and fixed:
[`docs/fidelity-log.md`](docs/fidelity-log.md).

## Layout

| Piece | What it does |
|---|---|
| `TasPlugin.cs`, `TasPlugin.*.cs` | BepInEx plugin, split by responsibility: `TasPlugin.cs` (state, lifecycle, shared helpers), `TasPlugin.Patches.cs` (all Harmony hooks), `TasPlugin.Playback.cs` (pacing & fixed clock), `TasPlugin.Session.cs` (new-game setup, unlocks, bootstrap), `TasPlugin.Savestates.cs`, `TasPlugin.Recording.cs`, `TasPlugin.Persistence.cs`, `TasPlugin.Http.cs` (HTTP API on `http://127.0.0.1:17426/`). Fields stay in `TasPlugin.cs`/`FullTasPlugin.cs` to keep the field-initializer order the compiler emits. |
| `MacroEngine.cs` | Injects route inputs at the 50 Hz physics boundary. Shared with `enginesim`. |
| `TurboRunner.cs` | Deterministic faster-than-real-time replay inside the game. |
| `Solver.cs` | Optimize tab (course **Improve**: `solve --auto` per record or farm target, its waypoints and `--focus`) and `SaveSolverRoute`, the only way a category route gets saved. |
| `EngineSimBridge.cs` | Trace, submit and raycast endpoints for the offline engine; read-only ghost-compare and humanize-slack endpoints for the studio. |
| `enginesim/` | The game's own code running offline for search. See [`enginesim/README.md`](enginesim/README.md). |
| `FullTas.cs`, `FullTasPlugin.cs`, `FullTasPlugin.*.cs` | Full-TAS: goals (shared with `enginesim`), gate, trace, playback, solve. `FullTasPlugin.cs` holds the model & state; `FullTasPlugin.Goals.cs`, `FullTasPlugin.Trace.cs`, `FullTasPlugin.Playback.cs`, `FullTasPlugin.Solve.cs`, `FullTasPlugin.Session.cs`, `FullTasPlugin.Http.cs` the rest. |
| `TechCatalog.cs`, `techs.json` | The studio's Tech tab: every named tech (glitchless, NMG, major), who named it, and its labs (`labmaps/`), each with a demo to play or walk and the lab to load by hand. `techs.json` seeds the config folder's `techs.json`, which the tab edits. |
| `ProbeRunner.cs` | 1x probes: exact inputs at normal speed from a category start or savestate, logged every tick and rendered frame (`enginesim/README.md#probe`). |
| `web/` | Next.js studio, served by the plugin. |
| `tools/` | Capture, redeploy, `validate.py`, `audit-traces.py`, `enginesim-rays.py` (Unity vs offline raycasts), `plan_fulltas.py`, `upgrade-boxes.py` (every one-off box; adds free ones to `courses.json` as bonuses), `probe.py` (a 1x probe and its per-frame log), `tech-check.py` (every tech demo, turbo against the offline engine). |

## Build

```powershell
dotnet build .\IGTAPTasMod.csproj -c Release     # web UI + plugin, copied into the game
```

The game is the Steam install (`Directory.Build.props`, `tools/gamepaths.py`; `IGTAP_GAME` overrides it). Close the
game before building (the plugin DLL is locked), then relaunch it. `python tools/enginesim-redeploy.py` does this for a
plugin-only build.

## Studio

The plugin serves the studio at `http://127.0.0.1:17426/`. The studio polls `GET /api/state/live` (playback, recording,
settings, the Optimize solver, the Full-TAS solve and playback, the Tech tab's last playback) a quarter second after each
answer, and fetches `GET /api/state/static` (routes, recordings, savestates, courses, Full-TAS runs, techs and lab maps)
only when the live state's `staticVersion` changes: with every command, every file the plugin writes, and within a
second of a file the offline solver or the lab tools write. `GET /api/state` is both in one, for tools. Selecting a
route saves it to `settings.json` (`selectedRouteId`), not the whole `routes.json`.

1x playback runs in real time: each frame waits until the wall clock reaches the fixed clock's time since the start
(`WaitForFixedClock`: every frame's step summed, a dash freeze's frames included), and the frames after a late one
catch up, up to 0.1 s behind. It does not use a 280 fps cap: six frames make a 0.02 s tick, so a cap would play at
0.933x. Real time is the route's time plus its dash freezes, as long as the game renders the ~310 fps it needs.
**Fast** next to **Run route** (`POST /api/settings/fast-playback {"on": true}`, settings `fastPlayback`, off by
default) drops the wait, so a route plays as fast as the game renders. The simulation is unchanged: each frame's game
time is the fixed clock's step (`Time.captureDeltaTime` on the 280 fps grid, `FixedClockStep`), never the wall clock,
and vsync off, `Time.maximumDeltaTime` and the wall-clock patches stay as they are. A walk waits for its slowdown times
the fixed clock. A probe (`ProbeRunner`, its frame rate is what it measures) keeps Unity's cap
(`Application.targetFrameRate`).

A slower frame rate plays the same frames, only later. While a
playback lists its ticks, the overlay draws its input panel, pad and velocity panel into a texture once a physics tick
(`TasOverlayCache.cs`) and puts the texture on screen each frame. Only POS, ULTRA, TIMESTOP and the nudge are drawn
live. Drawing all of it every frame cost 0.65 ms of the 3.57 ms a 280 fps frame has; cached, it costs 0.26 ms.

## Solving

See [`enginesim/README.md`](enginesim/README.md#use).
