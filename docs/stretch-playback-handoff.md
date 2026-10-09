# Handoff: "Play stretch best" doesn't play the stretch (turbo -> 1x switch) — 2026-10-09

## The problem

Optimize -> Full-TAS transitions -> **Play stretch best** plays a run's best up to a stretch, then the stretch's best inputs
(`fulltas/<run>.stretch-<N>.json`). It plays the run in turbo to a tick, then at 1x (`FullTasPlugin.Playback.cs`
`PumpFastForward`). For ZipEscape%'s stretch to goal 1157 (`at:28710,5876,240,400`, pad 3; 795 ticks against the run's 1181)
the playback died before the stretch at first, and now reaches goal 1157 but not pad 3 ("Timeline complete").

## What is known (measured)

- **The routes are good.** ZipEscape%'s best (1662.88 s, 1158 goals) replays in the engine and in turbo (the gate:
  `/api/fulltas/submit` reached 1158). Prefix + stretch inputs replay in the engine to pad 3 in 795 ticks
  (`fulltas <trace> --check C:\tmp\igtap\zip\stretch1158.json`).
- **1x from a switch while the player stands still is fine**: playing the best from tick 70000 or 71000 (both inside the
  1800-tick wait before `buytree7`) reaches all 1158 goals at 1x.
- **1x from a switch while the player moves diverges at once.** From tick 73000 (mid-dash, vx -460) the first 1x tick was
  a full tick behind the engine, and the run missed `buycd8`. Two causes found and changed (deployed, NOT committed):
  1. A real `IE_TimeStop` started in turbo froze 1x again (84 frames at timeScale 0 after the switch). Fix: as the TAS
     editor's seek, `TurboRunner.DropTimeStops = true` around the fast-forward's `TurboRunner.Run`.
  2. Also as the editor's seek: turbo to one tick short (`until - 1`), then `TurboRunner.ShowLastFrame(player)`.
  After both: no freeze, but 1x still runs ~1.4-2.4 u behind the engine per tick while moving (about the interpolation lag
  of the 280 fps grid). Suspect: Unity's own Rigidbody2D interpolation keeps its poses from before turbo, so the first 1x
  frames' blend isn't the modelled one. Not confirmed.
- **Workaround in place (deployed, NOT committed)**: the stretch play switches to 1x at the last input before the stretch
  that is a quick restart or in a run of 10+ still inputs, counted in input ticks (`StartFullTasPlayback(..., fromInput:
  true)`, `FullTasPlugin.Http.cs` `/api/fulltas/play` `stretch`). With it the prefix reaches goal 1157 at 1x. But for
  this stretch the chosen switch (input 61708) is NOT at rest: the player is dashing there (vx 1670) with no inputs held.
  A 1x probe from tick 75619 vs the engine: 1x x 25294.16 against 25295.94 at the first tick, and at 75621 1x jumps (vy 805)
  a tick before the engine does. So the stretch's 1x failure is the same switch problem, not a stretch bug.

## How to measure (the tools used)

- 1x probe of a Full-TAS route from a tick: `python tools/probe.py <name> "fulltas/ZipEscape%_NONE" <ticks.json> --from-tick
  T --log-from T-10` -> `BepInEx/config/IGTAPTasMod/probes/<name>.json` (rows `kind: tick` / `frame`).
- Engine trace of the same inputs: `IGTAP.EngineSim fulltas <trace> --check <route.json> --ticks-out <file.jsonl>`.
- Compare: a probe `tick` row at physics t holds the state before step t; compare it with the engine's row t-1.
  Inputs used: `C:\tmp\igtap\zip\esc-probe-ticks.json` (the best, to goal 1144), `C:\tmp\igtap\zip\stretch1158-ticks.json`
  (prefix + stretch). Engine traces: `C:\tmp\igtap\zip\esc-best.ticks.jsonl`, `C:\tmp\igtap\zip\stretch1158.ticks.jsonl`.
- Probes taken: `esc73` (old fast-forward, from 73000), `esc73b`..`esc73e` (each fix), `esc71` (from 71000, at rest),
  `st1158` (stretch, from 75619).

## Next steps

1. Pick the switch by the player's physical rest (grounded, velocity and momentum 0) from the engine's trace of the route,
   not by idle inputs; or switch right after a quick restart only.
2. Find the remaining mid-movement offset: probe a switch mid-walk (no dash) and compare frame rows with the engine's frames
   (`--ticks-out` has the frames' blends); check Unity's Rigidbody2D interpolation state after `TurboRunner.Run`. Then run
   `tools/tas-check.py` on a route seeked mid-movement: the TAS editor's seek uses the same handoff and may have the same gap.
3. Commit the playback changes once a probe from a moving switch matches the engine tick for tick.

## Uncommitted changes (deployed in the game build)

- `FullTasPlugin.Playback.cs`: `PumpFastForward` (DropTimeStops, `until - 1`, `ShowLastFrame`), `StartFullTasPlayback`'s
  `fromInput`.
- `FullTasPlugin.Http.cs`: `/api/fulltas/play` `stretch` switch point (still / restart input).
- `enginesim/Cli/EconomyPlan.cs`: `UnderBox` (realize waits for Cash under the box) — built into the shared engine, never
  exercised yet (a `--try` reused a library piece); unrelated to this problem.
