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
  the freeze (`turnX`/`turnY`): the freeze stick in 8 directions, and the 4 diagonals also at x 0.30, 0.35, 0.40 with y at
  full tilt (16.7°, 19.3°, 21.8° from vertical); without the omnidash left and right.
- **Works.** The key contact happens where the demo had it (−1..+2 ticks of where Compose placed it). A variant that
  dies still counts (marked `died`).
- **Output**, one JSON line per working variant as it is found: `tech`, `name`, `dir` (`L`, `R`, `U`, `D`, `UL`, `UR`,
  `DL`, `DR`, or `""` for none), `angle` (degrees from vertical, a steep freeze aim only), `start` (input ticks after N
  its window starts), `speed` (at the end), `died`, `ticks` (from N: continuation, window, coast) and `path` (the player's
  world position, origin-free, every physics step from N: `[x0, y0, x1, y1, …]`). Last line `{"done": true, "tried": n}`.

## Plugin

- **Automatic.** A held course/category session starts `techs` for the held input tick once it has stayed there 1 s.
  Moving on or editing kills it; results are kept per input tick until the inputs change, so going back is instant.
- **Best per direction.** The plugin keeps, per tech and direction (and angle), the variant that starts soonest, then
  the fastest.
- **Live state** (`tas.techs`): `at`, `running`, `unavailable`, `done`, `tried`, `error`, and per tech its name and
  variants (`dir`, `angle`, `start`, `died`); not the ticks or paths.
- **Hover** (`POST /api/tas/techs/hover {tech, dir, angle}`; `{}` clears): the overlay draws every variant of that tech
  faint and the hovered one bright, in world space (path + the game's current origin). A held session renders no frames,
  so the hover re-seeks the held physics tick (`seekPhysics = physics`): its last tick plays at 1x, and those frames
  draw the paths.
- **Try it** (`POST /api/tas/techs/apply {tech, dir, angle, slowdown}`; a variant is named by its direction, since a
  running search adds variants): the session's inputs from the held tick are replaced
  by the variant's (undoable), and it plays from the held tick to the variant's end at the given speed, then holds.

## Studio

A **Techs here** panel under Held tick: a status line (waiting / searching, n found / none / error) and one button per
tech named by its name. Hovering a button opens its compass: the directions found, arrows in a 3×3 grid; the diagonals
with steep freeze aims take the wheel to step 45° → 22° → 19° → 17°. Hovering an arrow shows that variant in game; a
click tries it. A click on the button itself tries the tech's soonest direction.

## Not in scope

Savestate and Full-TAS sessions (no category start trace), ranking by time saved, techs without a mined key window.
