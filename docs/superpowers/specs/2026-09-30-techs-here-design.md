# Techs here: tech suggestions in the TAS editor

A human TASing a course reaches a spot and doesn't know what to do. The TAS tab lists the named techs (`techs.json`) that
fit where the player is, one button per tech. Hovering a button draws in game where each of its directions would take
the player; clicking tries it: its inputs go in at the held tick and play at the session's speed.

Suggestions, not judged: nothing is saved, and a suggestion is not claimed to save time (Suggest does that).

## Engine: `enginesim techs`

`techs <start-trace> <route.json> --at N --caps <id> [--out file.jsonl] [--threads T]`

- Replays the route's first N input ticks from the category start trace (as `suggest`): course and category sessions only.
- **Surroundings.** From input tick N it plays continuations of 75 input ticks: the route's own inputs after N (when it
  has any), and the stick left, at rest and right, each with jump released and with jump held (a press where it wasn't
  held). `ContactLog` files the contacts along each; the player's contact at N counts too (on the ground: Landed, on a
  wall: TouchedWall).
- **Techs.** Each tech's key window (`TechPattern.Key`, mined by `TechGrafts`) whose `Requires` the category allows and
  that doesn't quick-restart, at each contact of its kind: `TechGrafts.Compose` places it (every shift in the pattern's
  range, no `Vary` sampling), cut at N at the earliest, then its coast.
- **Directions.** A window with no side is tried both ways (a sided one is mined mirrored already). A window with a
  dash: with the omnidash, the dash stick in all 8 directions; without it, left and right. A window whose dash aims in
  the freeze (`turnX`/`turnY`): the freeze stick in 8 directions, and the 4 diagonals also at x 0.30 and 0.35 with y at
  full tilt (16.7°, 19.3° from vertical; from 0.40 up `ControllerAxis` snaps x to full tilt, the 45° diagonal); without
  the omnidash left and right.
- **Works.** The key contact happens where the demo had it (−1..+2 ticks of where Compose placed it), and the tech itself
  happens between the window's first and last input: its overlay detector marks it (`TechChecks.Detect`, the TAS overlay's
  detectors on the recorded run), or its own check does (`[TechCheck]`: a jump-through buy is one jump through the buy
  triggers of two or more boxes). The key contact alone only places the window (a KB wall clip's is the wall coming ahead,
  which a turn makes without any clip). A tech with no check is never listed. A variant that dies in its coast still counts
  (marked `died`); one that dies before its window's inputs end doesn't.
- **Output**, one JSON line per working variant as it is found: `tech`, `name`, `dir` (`L`, `R`, `U`, `D`, `UL`, `UR`,
  `DL`, `DR`, or `""` for none), `angle` (degrees from vertical, a steep freeze aim only), `start` (input ticks after N
  its window starts), `speed` (at the end), `died`, `ticks` (from N: continuation, window, coast) and `path` (the player's
  world position, origin-free, every physics step from N: `[x0, y0, x1, y1, …]`). Last line `{"done": true, "tried": n}`.

## Plugin

- **Automatic.** A held course/category session starts `techs` for the held input tick once it has stayed there 1 s.
  Moving on or editing kills it; results are kept per input tick until the inputs change, so going back is instant. A run
  that ended (a death, the finish) has no player to try techs from: past its end the panel says so (`unavailable`) instead
  of starting a search, and a search's error shows its message, not its stack.
- **Best per direction.** The plugin keeps, per tech and direction (and angle), the variant that starts soonest, then
  the fastest. Directions that play the same path (within 0.5 u every step) are one move, shown by the simplest input
  (a cardinal before a diagonal before a steep aim) with the others listed as the same: a ground dash aimed down-right
  plays as one aimed right (a quarter of the omnidash's variants at Course 4 input 100).
- **Live state** (`tas.techs`): `at`, `running`, `unavailable`, `done`, `tried`, `error`, and per tech its name and
  variants (`dir`, `angle`, `start`, `died`); not the ticks or paths.
- **Hover** (`POST /api/tas/techs/hover {tech, dir, angle}`; `{}` clears): the overlay draws every variant of that tech
  faint and the hovered one bright, in world space (path + the game's current origin). A held session renders no frames,
  so the hover re-seeks the held physics tick (`seekPhysics = physics`): its last tick plays at 1x, and those frames
  draw the paths, the lead-in (the continuation before the tech) thin, a box where the tech starts and its name there.
  Through the re-seek the session still reports that tick, held (`redrawAt`), and a hover that arrives during one
  redraws once more after it.
- **Try it** (`POST /api/tas/techs/apply {tech, dir, angle, slowdown}`; a variant is named by its direction, since a
  running search adds variants): the session's inputs from the held tick are replaced
  by the variant's (undoable), and it plays from the held tick to the variant's end at the given speed, then holds.

## Studio

A **Techs here** panel under Held tick: a status line (waiting / searching, n found / none / error) and one button per
tech named by its name, soonest first, with when it starts (dimmed past half a second; those starting over a second
away are hidden behind **Show n more**). Hovering a button opens its compass: the directions found, arrows in a 3×3 grid; the diagonals
with steep freeze aims take the wheel to step 45° → 19° → 17°, and a dashed arrow is a direction that plays the same as
another. Hovering the button shows the soonest direction bright, an arrow that variant; a click tries it. A click on the button itself tries the tech's soonest direction.

## Not in scope

Savestate and Full-TAS sessions (no category start trace), ranking by time saved, techs without a mined key window.
