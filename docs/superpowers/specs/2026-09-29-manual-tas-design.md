# Manual TAS module — design

Date: 2026-09-29. Branch `manual-tas`.

## Goal

Hand-made TAS in the studio, in a loop with the solver: a human records and perfects a session, hands it to the solver
(Optimize tab's Improve seeds with it), takes the solver's best back into the editor, perfects it, and so on.

Features: frame advance and rewind on the live game, a per-tick input editor (click a tick's input and edit it),
re-recording from any tick with a real controller, undo/redo and markers, "optimize so far" suggestions (offline), solver
round trip, route sharing, placeable loadstates (floating-origin tests), and the savestate camera fix.

Out of scope: Recharge (later).

## Rules this has to keep (README `### Rules`)

- No new save path for category routes (43, 44): a session is a plain route; a category record only ever comes from
  `SaveSolverRoute` (the studio's "Judge as record" calls the existing `/api/enginesim/submit {routeId}`).
- No clock change, no replaced game logic (39, 40): the hold withholds the next frame; it never touches `timeScale`, the
  fixed step or any game method.
- Inputs change only on the physics tick, stick values a controller gives (2, 6): the editor offers only those
  (`docs/human-input.md`: x 0, ±1 or ±0.3..0.8; y 0, ±1), and `MacroEngine.ControllerAxis` clamps anyway.
- Presses are real presses (3): the editor edits jump as *held*; press and release are its edges (a *tap* is press and
  release in one tick), so every buffer comes from an engine press.
- Search offline only (45): suggestions run `enginesim suggest`, never game-judged loops.
- Imports never bypass the gate: a shared route with a category or farm name is renamed (`4_WJ_DS shared 13.28`) and
  can be judged like any other session.

## Session

A session is a raw-input route in `routes.json`:

- `<course>_<CAT> <text>` (not a reserved record name): starts at the category start (quick restart, category unlocks,
  zone, Area 1 state, `CourseStart`), as playback already does for that prefix; a seed for that course's Improve
  (`Solver.CourseSeedArgs`).
- any name with `savestateId`: starts from that savestate (free-form, never a record).
- New optional route fields: `markers` (`{tick, note}`), `rerecords`.
- Opening a reserved-name route (a record) opens an editable copy (`<course>_<CAT> edit <time>`).

The editor works on the route's ticks (`Solver.ToTicks`/`ToSteps`), one entry per input tick (dash and spring ticks
take none).

## Plugin runtime (`TasPlugin.Editor.cs`)

- **Open**: the route plays as a normal 1x playback (`_pendingReplay`), with a neutral tail so the engine never runs
  out of inputs. At the first hold point after the start synchronizes (input tick 0), the start snapshot is taken
  (`TurboRunner.Capture`, the player at rest) and the session holds.
- **Hold point**: the start of a frame (a PlayerLoop system first in `EarlyUpdate`, before any `FixedUpdate`) in which a
  `FixedUpdate` is due (`Time.timeAsDouble - Time.fixedTimeAsDouble >= fixedDeltaTime`; the 280 fps grid keeps frames
  half a frame off tick boundaries, so this is never ambiguous). Holding = the main thread waits there: it looks at the
  window's messages (`PeekMessage` no-remove, as turbo's `KeepWindowAlive`), answers studio requests
  (`_webServer.Pump`), reads hotkeys (`GetAsyncKeyState`, game window focused), and pauses audio. The frame's game time
  is already fixed by the fixed clock (`captureDeltaTime`), so the game sees exactly 1x's frames, only later.
- **Advance** n physics ticks: release frames until n `FixedUpdate`s have run, hold at the next hold point.
- **Play/hold**: release continuously (1x, or a walk slowdown), hold again on request or at the end of the ticks.
- **Seek** to input tick i (click a row, rewind, after an edit): restore the latest greenzone snapshot before it
  (`TurboRunner.Restore`, `_engine.Resume(snapshot.engine, ToSteps(ticks from its tick))`), turbo until one input tick
  short (`TurboRunner.Run`, as `PumpFastForward` does for tech demos), capture greenzone snapshots every 25 input ticks
  on the way, then advance the last tick at 1x so the screen shows 1x's frames, and hold.
- **Rewind** one tick = seek to the previous hold point's input tick.
- **Edit** (`splice(from, deleteCount, ticks)`): at or after the cursor, the engine is re-fed
  (`_engine.Resume(_engine.CaptureState(), ToSteps(ticks from cursor))`); before it, greenzone snapshots after the
  edit drop and the session seeks back to the cursor. Undo/redo keep the tick lists (200 deep).
- **Re-record** from the cursor: the session's ticks from the cursor go, the recorder (`InputRecorder`) records the
  controller at a chosen slowdown (the walk's frame-rate cap, same frames), and on stop its ticks are spliced in,
  `rerecords` counts up and the session seeks to the new end (what is kept is what replays).
- **Finish**: the course finishing ends the engine; the session stays open at the finish (nothing after it is part of
  the route) and shows the course time; rewind and edits work.
- Hotkeys (game window focused): `]` advance a tick, `[` rewind a tick, `\` play/hold, `'` re-record start/stop,
  F8 closes the session (emergency stop).

## HTTP (`/api/tas/*`)

`open {routeId}`, `new {name, course?, capabilities?, savestateId?}`, `close`, `advance {ticks}`, `rewind {ticks}`,
`seek {tick}`, `play {slowdown?}`, `hold`, `edit {from, deleteCount, ticks}`, `undo`, `redo`, `record {slowdown}`,
`record/stop`, `marker {tick, note}`, `ticks` (the session's tick list and markers), `suggest`, `suggest/apply {index}`,
`from-best {course, capabilities, human}`, `share/export {routeId}`, `share/import {code | json}`.
Live state gains `tas` (open, route, held, playing, recording, cursor, physics ticks, tick count, rerecords, version,
undo/redo, finished and course time, the held tick's player state, suggest status).

## Suggestions (`enginesim suggest`)

`suggest <start-trace> <route.json> --until N [--out file]`: replays the route offline to input tick N, takes the
player's state there as the target, and tries local edits of the inputs before N: each press, release, dash and stick
change moved ±1..6 ticks, each stick run set to the other legal values, each press or dash dropped. An edit counts if
the player reaches the target (within 4 units, speed along the target's velocity at least as high) in fewer ticks.
Output: the edits ranked by ticks saved, each with a description and its edited ticks up to the new arrival plus the
old route's tail after N. Course and category sessions only (the trace is the course's start capture). The plugin
runs it in the background (`StartEngineSim`), the studio lists the results; apply splices one in (undoable).

## Solver round trip

- "Improve in Optimize" jumps to the Optimize tab's target (the session is already a seed).
- "Open solver best" makes a session from the newest offline best (or its human version) of the course and category.
- "Judge as record" sends the session to the existing gate (`/api/enginesim/submit {course, capabilities, routeId}`).

## Share

Export: `{format: "igtap-tas-route", version: 1, name, steps, markers, rerecords, savestate?, game {version, assembly},
plugin, exportedUtc}`; the share code is `IGTAS1:` + base64url(gzip(json)). Import checks the format, renames reserved
names, gives an embedded savestate a new id, warns when the game assembly differs.

## Placeable loadstates

The Savestates tab gets a place form over the existing `/api/savestates/place` (from the current state or a
savestate; x, y, vx, vy, facing) plus `rebaseAtX/Y`: after the restore the world is rebased around that point the way
the Full-TAS `rebaseAt` does (`ApplyNewGameRebase`'s moves, shared), so the next `FloatingOrigin` rebase happens where
the test needs it. The TAS state panel's "Place here" prefills it.

## Savestate camera

Root cause first (decompiled `cameraMover`/`camZoneScript`), then one fix in `SavestateService.Restore`, the path every
savestate restore takes.

## Testing

- Plugin and enginesim compile (`dotnet build -p:SkipDeploy=true`), studio type-checks (`tsc --noEmit`).
- `enginesim suggest` on a saved trace and route: the unedited route is not suggested, every suggestion replays to the
  target sooner.
- `tools/tas-check.py` (needs the game): opens a session on a saved route, seeks to tick T by turbo and holds, and
  compares the held state with a straight 1x playback's at T; exports and imports a route and compares the ticks.
