# Runs (chains)

A run is the game played in order: each segment starts exactly where the one before it ended (position, speed, held
jump, timers, Cash, upgrades). The full run is `newgame`; the other chains (`wj`, `dash`) are its parts, solved on
their own too. Defined in the game's `BepInEx/config/IGTAPTasMod/chains.json` (not in the repo); edits are picked up
within a second, never mid-run.

## chains.json

```json
{ "name": "newgame", "capabilities": "none",
  "segments": ["newgame", "newgameto1", "1", "restart", "buyreward", "restart", "…", "buywj", "restart", "chain:wj",
               "restart", "buyreward2", "…", "restart", "buydash", "restart", "chain:dash"] }
```

| Entry | What it is | Finishes |
|---|---|---|
| `"newgame"` | First entry only, not a segment: the run starts from a fresh new game (below). | — |
| `2`, `bonus1` | A course / a bonus. | Its end gate / its reward box. |
| `<from>to<to>` | A transition (`1to2`, `2tobonus1`, `bonus1to2`, `newgameto1`). One courses.json lacks starts wherever the run arrives. | The next course's start gate starting its timer, or the 32 px box at a bonus's start. |
| `buyreward`, `buyclone`, `buywj` | Course 1's `CashPerLoop`, `Clone`, `Wall jump` boxes (`Courses/course 1/localUpgrades/…`). | The game itself buying the box (`upgradeBox.DoBuyUpgrade`). |
| `buyreward2`, `buyclone2`, `buyclonemult2`, `buyglobalreward2`, `buydash` | Course 2's `CashPerLoop`, `Clone`, `CloneMult`, `GLOBALcashPerLoop` and `DashUnlock` boxes (`Courses/course 2/localUpgrades/…`), in a row left of its quick-restart point. bonus1's free reward box has the same `GLOBALcashPerLoop` path; bonus1 buys it. `buydash` after `buywj` only. | Same. |
| `buyreward+buyclone`, … | One segment that buys several boxes (up to 8; a token twice = bought twice), e.g. one bump under two boxes. `buywj` / `buydash` in it unlock like on their own. | The game having bought every one of them in this segment. |
| `restart` | The game's quick restart (`Movement.respawn(true)`) as the next segment's first act: the running course's reset point, else the last checkpoint. | — |
| `chain:<name>` | That chain's segments (and its restarts) in place. | — |

- **Timing:** every segment, a course's too, in physics ticks from its start; the run's time is the sum.
- **Unlocks:** the run's category, then what it bought: WJ after `buywj`, WJDASH after `buydash`. The plugin never
  writes an unlock in a run from a new game ("Don't touch my unlocks" stays on); the game's purchases do.
- **Repeats:** a token's first occurrence is keyed by the token, the k-th by `<token>#<k>` (`1`, `1#2`, `buyreward#12`).
- **Parts:** `chain:<name>` goes on from where the run is; nothing teleports or restarts the player to the part's
  start (write `restart` before it if the run needs one). The part's category must be the run's there (`chain:wj`
  after `buywj`, `chain:dash` after `buydash`); a chain from a new game can't be a part, nor include itself. Its
  segments are keyed in the run like the run's own (`newgame`'s `chain:wj` gives `1#22`, `1to2`, …) and carry
  `module` in `/api/state` (`chains[].segments[]`).
- **A chain that starts at a course** (`wj`, `dash`, on their own) starts like a category route: a quick restart at
  its first segment's start, 3 stable ticks. Its first segment can't be a transition or a purchase.

## From a new game

`POST /api/session/newgame` loads the Overworld scene the way the menu's New Game does with no save file, except that
`Saveloader` neither loads nor saves for the rest of the session. `Movement.Start` puts Cash at 0 and the player at the
scene's `respawnPoint`. `/api/state` shows `freshGame: true` while it is loaded (`/api/oracle/bootstrap` ends it). The
run's start state is that game's first settled tick; *Run* loads a new game itself, and so does the solver when a
segment it is about to solve needs one and the session isn't fresh. Capturing a segment (`/api/enginesim/*`) still
needs a fresh session. `newgameto<N>` (segment 0 only) runs from the spawn to course N's start gate.

## Routes

A segment's routes are `<chain>/<key>_<CAPS> <time>` (`newgame/1#22_WJ 2.18`, `newgame/buyreward#3_NO_WJ 1.52`),
saved only by the gate, which replays them from where the run's current routes before them end. A new best re-times
every later segment; the first that no longer finishes (or fails a gate check), and every one after it, gets
` stale` appended (kept as seeds).

## Solving a segment

*Solve* captures the segment's start (the run's routes before it, replayed in turbo) and searches offline, seeded with:

- every chain's routes for the same token (every occurrence, stale ones too): a part's own routes seed the run's;
- the standalone routes of its course / transition / bonus, any category.

A route that starts standing at its course's quick-restart point (a standalone one, or the first segment's of a chain
that starts at a course) is carried over by `solve` (`enginesim/Runtime/SeedAlign.cs`), each way kept only if it
finishes from the segment's start:

- **splice:** its inputs from its last start-gate crossing on (a course segment starts at that gate). An analysis run
  (same start, the game's quick restart first) finds where the route really starts and where its course time starts;
- **walk:** stick-only inputs that land, walk to its start and stand still, then the route.

**Lead-in:** a course segment right after a transition that ends at its start gate (no `restart` between, e.g.
`newgameto1` then `1`) is searched from the transition's start, because the transition's last ticks belong to the lap
too. `1_NO_WJ 2.02` jumps 5 ticks before the gate, and a transition that stays on the ground up to the gate throws that
jump away, at no cost to its own time. `solve --lead` (the capture's `-lead` trace) seeds with the transition's saved
route followed by each lap seed, cuts every run whose gate crossing comes later than the transition's saved time,
and ranks by the lap's time in physics ticks from that crossing. A best (`chain-<chain>-<k>_<caps>-lead-<time>.json`)
is judged in two steps: its inputs up to the gate go through the gate as the transition's route (unless they match the
saved route), then the rest as the lap from where they end. If the lap isn't saved, the transition's route is put back.

Purchase segments offline: `python tools/enginesim-capture.py chain newgame K` (loads a fresh game when needed) writes
`start-chain-newgame-K.json`. Its header's `purchase` lists the boxes (hierarchy paths) and `income`, the Cash the clones
pay through each physics tick of the segment (recorded in turbo standing still, 1500 ticks); its `start.economy` holds
every currency and every upgrade box's state. Offline, `upgradeBox.OnTriggerStay2D` runs the game's buy rule
(`enginesim/Runtime/Shop.cs`): the box buys on a trigger stay (the tick after the player first overlaps its strip) when
its 0.2 s cooldown is up, the Cash covers its cost and it is active. So a bump under another box spends Cash there first,
a graze that never stays doesn't buy, and a purchase waiting on clone income happens when the clones have paid. A start
captured since the native economy (`start.economy.clones`) runs rewards, clones and Course 1's box effects offline instead
of `income` (`docs/fulltas.md`, Offline economy). Prestige and buy-max boxes, and global boxes other than the global
reward one (`GLOBALcashPerLoop`, native economy only), end an offline run. Offline, a global reward purchase updates its
own course's reward only; the game updates the other courses' on their own 5 s repeat (a random phase). Trace
ticks carry `cash`, which `replay` compares. `python tools/validate.py` re-runs every route, segment routes from where
the run's routes before them end (segment numbers from the plugin's `/api/state`).

## Studio

- **Sidebar:** *New game* at the top: its category, segments with a route, *▶ Play*, the next segment to solve, and
  its parts, collapsed (open: the one holding the selected route). Courses, transitions and bonuses below.
- **Runs tab:** each run with *Next to solve · Solve*, *⟳ Optimize whole run*, *⟳ Solve new segments*, its parts collapsed; per segment
  *Select*, *▶ To here*, *Solve*. The segment being solved is highlighted, with *Skip segment* / *Stop* on top.
- **Optimize whole run:** every segment in order, each for the minutes set (pass 1, default 1); a segment whose
  earlier routes don't exist yet is skipped. *Loop until stopped* (default on) then starts over, doubling the minutes
  each pass up to the max (default 15): quick passes give every segment a route, later ones dig deeper (overnight).
  A search also ends once half its minutes (at most 5) pass without a new best. Settings are kept in the browser and
  shared with Optimize many.
- **Solve new segments:** only the segments without a route, in order, the pass-1 minutes each; solving one makes the
  next startable in the same pass. A pass that leaves some without a route starts over on those alone, doubling the
  minutes up to the max, and it ends once every segment has a route (`unsolved: true`, chain segments only).
- **Offline bests:** a solve's offline bests left in the game's `enginesim/offline-best/` are judged by
  the game on its own (a turbo replay each, a run segment's from a fresh new game's start), also while the studio's
  solver runs: only its start of each target holds the queue, and the running target's own files wait for its process
  to end (it submits its bests itself). The Solve panel's *To validate* shows how many wait; each result goes to the
  BepInEx log only.
- **Optimize many:** the run first, one row per part (expand for its segments, tick all of it), then every course ×
  category, with the same pass settings. `POST /api/solver/start {queue: [{course, capabilities} | {chain, segment},
  ...], minutes, repeat, maxMinutes}` (`minutes` fractional, at least 0.25).
- **Runs only from a new game:** the Runs and Optimize tabs list only the runs from a new game; a part's own chain
  (wj, dash) shows inside its part, never on its own.

## Economy (new game)

`python tools/plan-newgame.py [--keep N] [--evals N] --write` plans both purchase phases and writes the `newgame` chain.
Phase 1 searches Course 1's purchase order up to wall jump in time order (~1.5 min). The player never waits under a box
for clone income if running Course 1 gets the money sooner. Then `chain:wj` runs, and bonus1 ends at the free +40%
box. Phase 2 plans Course 2's purchases up to dash. Each purchase happens as early as the model allows: after k Course 2
laps and the walk from the restart, or straight on along the row. The player waits under the box while the clones pay.
The order starts greedy (best income gain per W) and is improved by a seeded local search (`--evals` tries,
~2 min at 30000). `--keep N` keeps the chain's first N segments (the solved ones; `chain:wj` counts its six) and plans
from where they end. Its segment times are estimates: replace them with solved times and rerun.

- A box costs its base × 10^(course tier) (`upgradeBox.Start`), then `ceil(prev × scale)` after each buy (a global
  box: `round`). A course pays `ceil(baseReward × (1 + 0.4 × global reward buys) × (1 + its reward buys) ×
  10^tier)`. A clone pays `ceil(reward × (0.1 + 0.1 × clone multiplier buys))` per lap (lap = the course's best run),
  and a new clone spawns every lap / N. A course's clones pay the same per second while the player is away from it.
- Course 1 (tier 0, baseReward 1): base reward 1, then ×1.1; clones 8, then ×1.153; wall jump 700 W.
- Course 2 (tier 2, baseReward 4; one run 400 W, 560 W after bonus1): reward 400, then ×1.14 (cap 45); clones 800,
  then ×1.21 (cap 35); clone multiplier 3800, then `(prev + 1000) × 2` (cap 10); global reward 55000, then 275000
  (cap 2); dash 3,000,000 W (30000 × 10²; the in-game "3.00MW").
- A bump before a box is affordable still starts its 0.2 s cooldown. Real time pays clones per render frame (turbo:
  50 fps): leave a tick or two of slack before a purchase that depends on clone income.

## Status (2026-09-25)

- Solved: `newgame/newgameto1_NO_WJ 5.50`, `newgame/1_NO_WJ 2.04`. The lead-in solve took the lap from 2.12 to 2.04 without
  slowing the transition: it now jumps on flat ground 4 ticks before the gate. The chain can't reach 2.02 here because
  of where the player is when the timer starts. From the spawn the player reaches 460 px/s in 4 ticks and holds it, so
  the positions are fixed at spawn + 9.2 px per tick; a stick below full only loses ground. The timer starts at
  x=2453.0 in the chain and at x=2457.7 in the standalone 2.02. Gaining the missing 4.7 px would cost the transition
  ticks: with the transition 1–2 ticks slower, spliced 2.02 laps ran 2.06 offline.
- `1_NO_WJ 1.68` is renamed `1_NO_WJ 1.68 stale`: it no longer finishes offline or in turbo (`finished: false`)
  because it holds the stick at ±0.1, which playback now plays as a controller gives it
  (`MacroEngine.ControllerAxis`: 0). The stale name takes it out of the category best, so a slower route can be
  saved; it still seeds Course 1 solves (every `1_` route does, and every chain's `1_` / `1#k_` route), where a small
  change could make it finish again. `tools/plan-newgame.py` now uses the 2.14 lap. `1_NO_WJ 4.14` (0.25 stick)
  doesn't finish either.
- `1_NO_WJ 2.02` (standalone): turns left on landing on the top ledge and re-jumps a tick later (the 2.14 jumped with
  jump buffered and turned mid-air), then cuts the last jump at its peak so the arc drops onto the end gate sooner.
  Found in seconds once `solve` kept equally fast mutations (neutral drift); before that it sat at 2.14 while the
  newgame chain's `1#k` laps already ran 2.06 on the game timer.
- Dash costs 3,000,000 W, not 30000 (Course 2's tier 2 multiplies its boxes' base costs by 100). The `newgame` chain
  now buys Course 2's economy between `chain:wj` and `buydash` (`plan-newgame.py --keep 72 --write`; the previous chain
  is `chains.before-dash-replan.json`). Model: arrival at 1007 W (captured: 911 W at bonus1to2's start, plus 8.36 s of
  Course 1 clones), dash at 64.54 s after `chain:wj`. The plan: 4 single laps while the clones are few, then it stays
  at the row: 38 reward buys, 26 clones, 6 clone multipliers, both global reward buys. Then 2 laps before dash. Clone
  income overtakes laps after the first clone multiplier. A lap earns about 560 × (1 + reward buys) W every 159 ticks,
  while the clones earn 26 × 24,025 W every 128 ticks by the end.
- Running the economy right after `wj`'s Course 2 lap, with bonus1's detour after it, is about 2 s faster in the model.
  It is left out: the model is less accurate than that, and it would split `chain:wj`.
