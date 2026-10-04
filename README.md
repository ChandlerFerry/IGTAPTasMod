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
    anywhere else they play exactly as `WJ_DS_DJ_BS`, and `WJ_DS_DJ_BS_ZIP_OM` as `WJ_DS_DJ_BS_ORB_ZIP_OM`. Bonus 4's end
    needs the zip movers: it runs only the categories with `ZIP`, none of its ladder. Bonuses 17–21
    and 23 run no category: their end is an unlock box (refill orbs, clone dust, buy max, the 1x and ∞x Start a VMAN
    speedrun boxes, block swap on dash), not a bonus stretch.
20. Courses 6–10 are Area 2 courses: a start gate is on only while its zone is loaded (Courses 1–5 zone 1, 6–10
    zone 2).
21. Every quick restart onto a course, bonus or transition loads the zone of the start gate or checkpoint nearest its
    start the game's way (`ZoneLoader`, as the zone activators do); the breaker plays no part. Courses 6–15 start as if the free first atom had been bought: the atom
    room's door (`AtomColliderArea/Door`) is open, as a game load opens it with PlayerPrefs `OpenA2Door` (the atom box's
    `doorToOpen.JumpToNextState()`), without buying the atom, its prestige or any PlayerPrefs write (`CourseStart.OpenAtomDoor`,
    in the plugin's category start and the offline engine's course trace load). Course 9's end is reachable only through it.
    On a machine without the `OpenA2Door` key, the atom box's first buy then shuts the door: the game's first-buy branch
    (upgradeBox.cs:644-646) applied to a state a real game never has (a load opens the door only with the key or the box
    bought, upgradeBox.cs:264).
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
52. Only save strictly faster times: record by the game timer, farm by a lap of the steady loop in live time (30); human
    classes: strictly lower score. A two-way course (Courses 1-3) also keeps a record of each direction, forwards and
    backwards, by the game timer among routes of that direction (`AGENTS.md`, route names).
53. Trace every new best and name its trick.
54. A Full-TAS run keeps four bests, its classes: `major` (any tech) and `nmg` (no major tech) by live time, `human-major`
    and `human-nmg` the same two by score. Score: live time plus 2 s a spike-grace tick, 5 s a frame-perfect input and
    20 s a back-to-back pair of the whole route's difficulty (per run, `fulltas.json` `"human": {"grace", "framePerfect",
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
    (`techs-used` lists them).

#### Exceptions

1. Relaxes rule 6. The one exception is the dash freeze's stick (`turnX`/`turnY`, `MacroStep.dashTurnX`/`dashTurnY`): a
   dash pressed with the stick at rest takes the stick the game reads before the next tick, as a player tilting the
   stick during the freeze turns it. Without the omnidash only its sideways part counts; with it the vertical does too
   (such as (0.35, 1): the game snaps the vertical to full tilt). It is legal.
2. Relaxes rule 39. The interpolation/fixed-clock patch (rules 7–9), the Vman credits' crash (below) and playback's
   wall-clock reads (the dash freeze, the camera's easing and `ObjectBobber` count the fixed clock's frame instead of
   the wall clock, [`enginesim/README.md`](enginesim/README.md#probe)) change game logic; the other places a patch does are
   the exceptions below that relax rule 39 (3, 4, 5, 10, 11 and 12), each named there.
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
12. Relaxes rule 39. The `Start` draws of what a run wakes: a `LightFlicker` (a box's pulse light, 1 draw), a
    `RandomAnimSpeedComponent` (a tree segment's lights, 2, 3 when broken) and a `JiggleDropScript` (a refill orb, 1)
    that a run switches on before Unity ever started it. Unity runs `Start` before the behaviour's first `Update`: in the
    same rendered frame when it was woken in the physics step (a tree box's buy), in the next when woken at a frame's end
    (`IE_Grow` turning a segment's box on). Turbo draws them at that modelled frame and puts `UnityEngine.Random`'s state
    back around Unity's later real `Start` (`TurboRunner.StartPass`, as `TasPlugin.PresentationDraw` does for exception 11);
    the offline engine makes the same draws at the same frame (`Economy.StartPass`). 1x is untouched. A course's `Start`
    draws too (`InvokeRepeating("UpdateReward", Random.Range(0, 5), 5)`), but no run wakes a course (zone loads happen
    outside runs).

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
| `TasPlugin.Editor.cs`, `TasPlugin.Share.cs`, `TasMarkup.cs` | The TAS editor (studio **TAS** tab, `/api/tas/*`): frame advance, rewind and tick editing on the live game, re-recording, suggestions, sharing, and routes as text ([`docs/tas-markup.md`](docs/tas-markup.md)). |
| `web/` | Next.js studio, served by the plugin. |
| `tools/` | Capture, redeploy, `validate.py`, `audit-traces.py`, `enginesim-rays.py` (Unity vs offline raycasts), `upgrade-boxes.py` (every one-off box; adds free ones to `courses.json` as bonuses), `probe.py` (a 1x probe and its per-frame log), `economy-diff.py` (two legs' economy, field by field as value sequences; `--ticks` on the same ticks), `tech-check.py` (every tech demo, turbo against the offline engine), `tas-check.py` (the TAS editor's seek, rewind, text and share against 1x). |

## Build

```powershell
dotnet build .\IGTAPTasMod.csproj -c Release     # web UI + plugin, copied into the game
```

The game is the Steam install (`Directory.Build.props`, `tools/gamepaths.py`; `IGTAP_GAME` overrides it). Close the
game before building (the plugin DLL is locked), then relaunch it. `python tools/enginesim-redeploy.py` does this for a
plugin-only build.

## Studio

The plugin serves the studio at `http://127.0.0.1:17426/`. The studio keeps one WebSocket open to `/api/ws`
(`WebStudioServer.cs`, `web/app/studio-socket.ts`) and connects again when it drops. The plugin pushes the live state
(`{"type": "live"}`, what `GET /api/state/live` returns: playback, recording, settings, the Optimize solver, the Full-TAS
solve and playback, the Tech tab's last playback) whenever it changes, checked every 33 ms at most, and the static state
(`{"type": "static"}`, what `GET /api/state/static` returns: routes, recordings, savestates, courses, Full-TAS runs,
techs and lab maps) when its `staticVersion` changes: with every command but an open TAS session's own, every file the
plugin writes, and within a second of a file the offline solver or the lab tools write. The studio sends its commands
over the socket too (`{"id", "method", "path", "body"}`, answered `{"type": "reply", "id", "status", "data"}` after the
state they left). The HTTP API stays for tools; `GET /api/state` is both parts in one. Selecting a route saves it to
`settings.json` (`selectedRouteId`), not the whole `routes.json`.

1x playback runs at most in real time: each frame waits until the wall clock reaches the fixed clock's time since the
last frame (`WaitForFixedClock`: the frame's step, a dash freeze's frames included), and a late frame is never made up,
so playback never speeds up: a run's frames are 1/280 s each, 280 a second (before its first tick each tick holds six,
paced 3.33 ms apart). Real time is the route's time plus its
dash freezes only while every frame renders within its step; each late one leaves the rest of the run that much later
(`GET /api/perf`: the last 4096 frames' wall time, split into the pace wait, the plugin's Update and OnGUI and the
rest, and the playback's speed against real time).
**Fast** next to **Run route** (`POST /api/settings/fast-playback {"on": true}`, settings `fastPlayback`, off by
default) drops the wait, so a route plays as fast as the game renders. The simulation is unchanged: each frame's game
time is the fixed clock's step (`Time.captureDeltaTime` on the 280 fps grid, `FixedClockStep`), never the wall clock,
and vsync off, `Time.maximumDeltaTime` and the wall-clock patches stay as they are. A walk waits for its slowdown times
the fixed clock. A probe (`ProbeRunner`, its frame rate is what it measures) keeps Unity's cap
(`Application.targetFrameRate`).

A slower frame rate plays the same frames, only later. In every mode (a listed playback, hand play, turbo) the overlay
draws its input panel, pad and velocity panel into a texture once a physics tick, or when a setting, the unlocks or a
displayed count changes (`TasOverlayCache.cs`), and puts the panels' columns of the texture on screen each frame. Only
POS, ULTRA, TIMESTOP and the nudge are drawn live, and the pad too when no route lists its ticks (hand play shows the
buttons held now). Its text comes from a glyph atlas (`TasOverlayGlyphs.cs`): each font's glyphs drawn once by IMGUI
into a texture, each label then one batch of quads. IMGUI (Unity 6's TextCore) generates every new string it draws,
about 2 KB of garbage each, and the overlay draws new numbers every tick and frame: 6.6 KB a frame and a 10 ms garbage
collection every few seconds. With the atlas the overlay costs 0.18 ms a frame and 0.12 KB of garbage; without either,
it cost 0.35 ms, and a strictly paced 1x played at 0.94–0.97x real time instead of 0.99x.

## TAS editor

The studio's **TAS** tab edits a raw-input route tick by tick on the live game
([`docs/superpowers/specs/2026-09-29-manual-tas-design.md`](docs/superpowers/specs/2026-09-29-manual-tas-design.md)).
A session is a route: `<course>_<CAT> <text>` starts at the category start with its unlocks (and seeds that course's
Improve), a route with a savestate starts there; a record opens as an editable copy. The loop: record what you believe is
perfect, let Optimize's Improve search from it, open its best here (**Open solver best**), perfect it, repeat; **Judge
as record** sends a finished session through the gate (`/api/enginesim/submit`), the only way it becomes a record.

- **Hold.** The session plays as 1x playback and holds between physics ticks: at the start of a frame whose FixedUpdate
  is due, the main thread waits (answering the studio, reading hotkeys, audio paused) until told to go on. The fixed
  clock already set the frame's game time, so the game plays 1x's frames, only later: no clock change, no game logic
  skipped. Advance lets physics ticks through; play runs on to the end of the inputs (at a slowdown if asked). While
  held the game window is only kept responsive: it can't be moved or closed, so close the session first (F8, the studio).
- **Rewind and seek.** Greenzone turbo snapshots every 25 input ticks: a seek restores the last one before the target,
  turbos to one tick short and plays the last tick at 1x, so what is held on screen is a 1x frame. Before the first
  snapshot (the start) the session starts again. An edit at or before the held tick drops the snapshots from it and
  seeks back; one after it only re-feeds the engine (`MacroEngine.ResumeAt`). During a seek's turbo, dash freezes are
  modelled only (`TurboRunner.DropTimeStops`: no real `IE_TimeStop` left to freeze 1x after it), and the handoff puts the
  player at the last frame's blend, where 1x's next FixedUpdate reads it. A savestate session holds first after input
  0, so its input 0 presses nothing (the buffer timers a press starts there couldn't be put back on a rewind).
- **Editing.** A piano roll on TAStudio's and FCEUX TAS Editor's conventions: one row per input tick; the narrow left
  column goes there (double-click a number does too), click or drag the numbers to select. Click or drag stick and jump,
  right-click a tap, click a dash; the wheel on X steps through the part tilts (the whole selection's). Keys: arrows
  select, Enter go, Shift+Enter play to, Ins/Ctrl+Ins insert/clone, Del/Ctrl+Del clear/delete, Ctrl+X/C/V (Shift:
  insert), Ctrl+Z/Y, M marker, Shift+PgUp/PgDn markers. Edited rows flash. **Phys** is the physics tick an input plays
  on, `+n` the dash or spring ticks after it that take no input; x, y, vx, vy are the player there as played (the
  session's trace, `/api/tas/trace`, cut where the inputs change). **Play to** (`/api/tas/play {until, from}`) plays up
  to a tick and holds, from 50 inputs before it if it's behind. Also text ([`docs/tas-markup.md`](docs/tas-markup.md)),
  undo/redo, markers (go to one to load that point; each shows its segment's inputs and ticks). Sticks are a
  controller's values only, and jump is edited as a hold whose start presses, so every buffer comes from a press. A
  **Human version** session colours each input number by its humanize slack (red exact, amber window, violet band, green
  free; hover for the input), on the rows whose inputs are still the human version's. Edits go to the session journal
  (`tas-session.json`, the one route) and routes.json, the whole library, is written once when the session closes; a
  journal left by a game that stopped first is put back at the next start.
- **Keyboard input** (the TAS tab's **⌨ Keyboard input**): the page takes game keys, arrows or WASD the stick, Space
  jump, X dash (no page scroll, no row selection), and each `]` writes what they hold into the input it plays, then
  advances (Shift: ten inputs); a jump or dash pressed and let go between two advances goes in on the first. A tick
  whose stick and buttons don't change keeps its extras. Arrows and Space aren't hotkeys then: `]` `[` `\` are.
- **Re-record** drops the inputs from the held tick and records the controller (`InputRecorder`) at a slowdown; on stop
  its ticks go in and the session seeks to their end: what is kept is what replays. A death or finish stops it.
- **Techs here** (course and category sessions): a second after a tick is held, `enginesim techs`
  ([`enginesim/README.md`](enginesim/README.md#techs)) lists the named techs whose key window works from there, one button
  each, soonest first with when it starts (those over a second away behind **Show n more**). Hovering one draws its directions' paths in game (the held tick is seeked
  again, so its last 1x tick draws them; the walk before the tech thin, a box and its name where it starts) and opens
  its compass (a dashed arrow plays the same as another); an arrow, or the button for the soonest, puts its inputs in at
  the held tick and plays them.
- **Suggest** (course and category sessions) runs `enginesim suggest` on the route up to the held tick
  ([`enginesim/README.md`](enginesim/README.md#suggest)) and lists edits that reach the same spot sooner; apply splices one in.
- **Share**: a code (`IGTAS1:` + base64url gzip), `.igtas.json` or `.tas` text, with the savestate if any. An import is a
  plain route (a record's name gets ` shared`).
- **Hotkeys**, in the game or the studio's TAS tab: `]` advance, `[` rewind (in the studio also `→`/`←`, Shift for ten ticks), `\` play/hold (in the studio also Space),
  `'` re-record; F8 (game) closes. The tab's input box turbos to an input tick and holds there.
- **Placed loadstates** (Savestates tab): a copy of the player now or of a savestate with x, y, speed or facing replaced,
  and optionally the world rebased around a point after the restore (`RebaseWorldAround`, the Full-TAS `rebaseAt`'s moves),
  for floating-origin tests.

`tools/tas-check.py <route>` (game running) checks that a seek holds the same state as playing there at 1x, that rewind
and advance come back, and that the text and a share code carry the inputs unchanged.

## Solving

See [`enginesim/README.md`](enginesim/README.md#use).
