# Full-TAS idles: waits for Cash shown as what they are

## Intent

A Full-TAS run spends much of its time waiting for Cash. Today the waits hide inside other stretches (filler: dashing away,
extra quick restarts, hops), which reads as bad play to a viewer. And a segment made faster can't keep its gain: the time it
saves has nowhere to go but a wait the route doesn't name, so the old way on breaks or the run comes out no faster and the
find is dropped.

An idle is a wait made explicit: the player at the box a buy goal waits for, until its Cash is there. It grows when the
stretch before it gets faster and shrinks when the Cash comes sooner.

## Decisions

- **On screen** (option 2): standing still under the box by default; deliberate visible moves inside an idle are allowed
  when they pick a better roll (the RNG passes), and the idle is labeled as waiting either way.
- **Placed by the engine** (option 1): wherever the best waits, never by hand. An idle comes and goes with the wait.
- **Unnumbered** (option 1): an idle belongs to the buy goal it waits for. Goal numbers, focus, splits and the planner
  count only the run's goals.
- **An annotation on the buy** (approach A), not a goal kind: the goal list, `runs/<run>.json`, the tracker shared with the
  plugin, `SaveSolverRoute` and the gate are unchanged.

## What an idle is

For buy goal k, from goal k-1's tick on:

- **arrive**: the first physics tick the player is at the box (within its trigger's x-range plus the player's half width,
  `Zone.Near`, and below the trigger's top within reach of it).
- **afford**: the first physics tick the run holds what every box goal k names costs (`Shop.Affordable`).

When arrive comes before afford, goal k has an idle from arrive to afford; it is recorded when it is at least 8 ticks long
(`IdleWaitRun`, the wait threshold the idle part already uses). A wait away from the box (filler) is not an idle: that is
what the search turns into one.

## Engine

- `FullTas.Idles(Route)` (enginesim/Cli/FullTasIdles.cs): one replay of the route with a per-tick check while the next goal
  is a buy; returns `{ goal, arrive: [physics, held], afford: [physics, held] }` per idle.
- **Run file**: `fulltas/<run>.json` carries `idles` (the studio and the overlay read them there; a record that plays the
  offline best's inputs shows them too). Written with the goal ticks (one replay
  per write, which only a new best makes).
- **Ranking**: at equal live time, the route with more idle ticks ranks ahead (`Ahead`). Idle ticks here are counted from
  the inputs (idle-input runs of 8+ ticks in each buy's stretch), cheap enough for every comparison. A faster segment whose
  time comes back as a longer wait is kept, and filler made standing (`Defiller`, `IdlePull`) is kept at equal time.
- **Submission**: a complete best goes to the gate only when strictly faster (the gate saves only strictly faster times, so
  a tie on time would only be refused).

## Studio and overlay

- **Goals table**: an idle row (no number) before its buy: `idle · waiting for buy:<box>`, its live duration. The plugin reads
  `idles` with the offline best.
- **Overlay**: during a Full-TAS playback whose inputs are the run file's, a banner `IDLE · waiting for <goal>` with the seconds
  left, from arrive to afford.

## Not in scope

- The economy lever (the goals part) under a focus or on an unfinished run: idles make the waits it would fill visible, and
  are its natural targets later.
- Idles in records saved before this (they get them when the engine writes the run file again).

## Testing

- `Idles` on Zip%'s best: the waits it reports sit before buys, end on the tick the Cash is there, and their durations
  add up against the splits.
- A focused `--auto` on Zip% 509-511 runs and keeps equal-time routes with more idle.
