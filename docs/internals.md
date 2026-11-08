# Internals: code layout, studio, TAS editor

## Layout

```
plugin/        the BepInEx plugin (IGTAPTasMod.csproj), in the game
shared/        sources compiled into both the plugin and enginesim
enginesim/     the game's own code running offline, for search
web/           the Next.js studio, served by the plugin
data/          runs/ (Full-TAS run definitions), bests/ (every record as TAS text), labmaps/ (lab maps and demos), techs.json (the tech catalog)
tools/         Python scripts against the running game and the engine
docs/          reference docs; docs/notes/ holds dated handoffs and reports
```

| Piece | What it does |
|---|---|
| `plugin/Core/` | The plugin itself, split by responsibility: `TasPlugin.cs` (state, lifecycle, shared helpers), `TasPlugin.Patches.cs` (all Harmony hooks), `TasPlugin.Session.cs` (new-game setup, unlocks, bootstrap), `TasPlugin.Persistence.cs`, `TasPerf.cs` (`GET /api/perf`), and the game-state helpers `PlayerActions.cs`, `BreakerLights.cs`, `TreeLevel.cs`. Most plugin files are parts of one `partial class TasPlugin`, and the compiler runs its field initializers file by file in build order, so a static initializer never reads a field another file initializes. |
| `plugin/Playback/` | `TasPlugin.Playback.cs` (pacing & fixed clock), `TurboRunner.cs` (deterministic faster-than-real-time replay inside the game), `ProbeRunner.cs` (1x probes: exact inputs at normal speed from a category start or savestate, logged every tick and rendered frame, `enginesim/README.md#probe`), recording (`TasPlugin.Recording.cs`, `RecordingSystem.cs`) and savestates (`TasPlugin.Savestates.cs`, `Savestates.cs`). |
| `plugin/Solver/` | `Solver.cs`: Optimize tab (course **Improve**: `solve --auto` per record or farm target, its waypoints and `--focus`) and `SaveSolverRoute`, the only way a category route gets saved. `EngineSimBridge.cs`: trace, submit and raycast endpoints for the offline engine; read-only ghost-compare and humanize-slack endpoints for the studio. |
| `plugin/FullTas/` | Full-TAS: gate, trace, playback, solve. `FullTasPlugin.cs` holds the model & state; `FullTasPlugin.Goals.cs`, `FullTasPlugin.Trace.cs`, `FullTasPlugin.Playback.cs`, `FullTasPlugin.Solve.cs`, `FullTasPlugin.Session.cs`, `FullTasPlugin.Http.cs` and the rest. The goals themselves are `shared/FullTas.cs`. |
| `plugin/Editor/` | The TAS editor (studio **TAS** tab, `/api/tas/*`): `TasPlugin.Editor.cs`, `TasPlugin.Share.cs`, `TasPlugin.Techs.cs` (Techs here), `TasMarkup.cs`: frame advance, rewind and tick editing on the live game, re-recording, suggestions, sharing, and routes as text ([`docs/tas-markup.md`](tas-markup.md)). |
| `plugin/Techs/` | The studio's Tech tab: `TechCatalog.cs` (every named tech, glitchless, NMG, major) and its labs: `LabMapService.cs` (injecting a lab map), `LabGhost.cs`, `LabTrainer.cs`, `LabTrainerFloat.cs`. |
| `plugin/Overlay/` | The in-game overlay: `TasOverlay*.cs`, `TasSkin.cs`. |
| `plugin/Studio/` | `TasPlugin.Http.cs` (the HTTP API on `http://127.0.0.1:17426/`) and `WebStudioServer.cs` (the server and its WebSocket). |
| `shared/` | `MacroEngine.cs` (injects route inputs at the 50 Hz physics boundary), `MacroModels.cs`, `FullTas.cs` (Full-TAS goals), `Categories.cs`, `CourseStart.cs`, `WokenStarts.cs`, `FieldState.cs`: compiled into the plugin and into `enginesim/Runtime`, so a change here is an engine change (the pre-commit check runs). |
| `enginesim/` | The game's own code running offline for search. See [`enginesim/README.md`](../enginesim/README.md). |
| `enginesim/Cli/Runs/` | Full-TAS v2: `fulltas <trace>` / `solve-run` (segment library, builder, improver, confirmation) and `check` (complete runs and traces against `enginesim/check/baseline.json`; the pre-commit hook). [`docs/fulltas.md`](fulltas.md#solving-v2-fulltas-trace-is-solve-run). |
| `data/runs/` | The Full-TAS run definitions, one `<run>.json` each (goals, start, buy rule, section; [`docs/fulltas.md`](fulltas.md#run-definitions)). The plugin reads and writes them here; `fulltas-plan --install` and `--stages` update them as a route gets further ([`docs/fulltas.md`](fulltas.md#install-and-stages)). |
| `data/bests/` | Every record (the fastest route of each category, direction, farm and Full-TAS class) as TAS text ([`docs/tas-markup.md`](tas-markup.md)), `<category>.tas`, Full-TAS runs in `bests/fulltas/`; a farm's first line gives its loop. The plugin writes one whenever its category's record changes (`ExportBests`, in `SaveLibrary`) and never deletes one: commit them and git keeps every best. To play or improve one, paste it into the studio's share import (up to 100000 input ticks), then **Judge as record** (RULES.md 43: the gate is the only way back into `routes.json`). |
| `data/techs.json`, `data/labmaps/` | The Tech tab's built-in techs and labs, embedded in the plugin and copied beside the engine. `techs.json` seeds the config folder's `techs.json`, which the tab edits. |
| `web/` | Next.js studio, served by the plugin. |
| `tools/` | Capture, redeploy, `validate.py`, `audit-traces.py`, `enginesim-rays.py` (Unity vs offline raycasts), `upgrade-boxes.py` (every one-off box; adds free ones to `courses.json` as bonuses), `probe.py` (a 1x probe and its per-frame log), `economy-diff.py` (two legs' economy, field by field as value sequences; `--ticks` on the same ticks), `tech-check.py` (every tech demo, turbo against the offline engine), `tas-check.py` (the TAS editor's seek, rewind, text and share against 1x). |

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
the fixed clock. A probe (`ProbeRunner`) at the playback rate honours Fast (or its own `fast`); one at another frame rate
(its `fps`, what it measures) keeps Unity's cap (`Application.targetFrameRate`).

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

The studio's **TAS** tab edits a raw-input route tick by tick on the live game.
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
  player at the last frame's blend for 1x's next FixedUpdate only, the body left at the step's end, as a turbo tick does
  (a transform write before it would be synced into the body). A savestate session holds first after input
  0, so its input 0 presses nothing (the buffer timers a press starts there couldn't be put back on a rewind).
- **Editing.** A piano roll on TAStudio's and FCEUX TAS Editor's conventions: one row per input tick; the narrow left
  column goes there (double-click a number does too), click or drag the numbers to select. Click or drag stick and jump,
  right-click a tap, click a dash; the wheel on X steps through the part tilts (the whole selection's). Keys: arrows
  select, Enter go, Shift+Enter play to, Ins/Ctrl+Ins insert/clone, Del/Ctrl+Del clear/delete, Ctrl+X/C/V (Shift:
  insert), Ctrl+Z/Y, M marker, Shift+PgUp/PgDn markers. Edited rows flash. **Phys** is the physics tick an input plays
  on, `+n` the dash or spring ticks after it that take no input; x, y, vx, vy are the player there as played (the
  session's trace, `/api/tas/trace`, cut where the inputs change). **Play to** (`/api/tas/play {until, from}`) plays up
  to a tick and holds, from 50 inputs before it if it's behind. Also text ([`docs/tas-markup.md`](tas-markup.md)),
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
  ([`enginesim/README.md`](../enginesim/README.md#techs)) lists the named techs whose key window works from there, one button
  each, soonest first with when it starts (those over a second away behind **Show n more**). Hovering one draws its directions' paths in game (the held tick is seeked
  again, so its last 1x tick draws them; the walk before the tech thin, a box and its name where it starts) and opens
  its compass (a dashed arrow plays the same as another); an arrow, or the button for the soonest, puts its inputs in at
  the held tick and plays them.
- **Suggest** (course and category sessions) runs `enginesim suggest` on the route up to the held tick
  ([`enginesim/README.md`](../enginesim/README.md#suggest)) and lists edits that reach the same spot sooner; apply splices one in.
- **Share**: a code (`IGTAS1:` + base64url gzip), `.igtas.json` or `.tas` text, with the savestate if any. An import is a
  plain route (a record's name gets ` shared`).
- **Hotkeys**, in the game or the studio's TAS tab: `]` advance, `[` rewind (in the studio also `→`/`←`, Shift for ten ticks), `\` play/hold (in the studio also Space),
  `'` re-record; F8 (game) closes. The tab's input box turbos to an input tick and holds there.
- **Placed loadstates** (Savestates tab): a copy of the player now or of a savestate with x, y, speed or facing replaced,
  and optionally the world rebased around a point after the restore (`RebaseWorldAround`, the Full-TAS `rebaseAt`'s moves),
  for floating-origin tests.

`tools/tas-check.py <route>` (game running) checks that a seek holds the same state as playing there at 1x, that rewind
and advance come back, and that the text and a share code carry the inputs unchanged.
