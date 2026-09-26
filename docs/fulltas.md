# Full-TAS

The whole game from a fresh new game as one continuous input stream. A run is a list of goals, game events that must
happen in order; its time is the physics ticks from the new game's first settled tick to the last goal. Goals split
the run for display and for the offline search; nothing restarts, teleports or re-times between them. A quick restart
(`Movement.respawn(true)`, the game's QuickReset) is an input the route chooses (`reset` on a tick), so a route can
restart or run on, whichever is faster. The new game is loaded by `POST /api/session/newgame`: the menu's New Game with
no save file, except that `Saveloader` neither loads nor saves for the rest of the session.

## fulltas.json

In the game's `BepInEx/config/IGTAPTasMod/` (edits are picked up within a second, never mid-run):

```json
[{ "name": "main", "goals": ["start:1", "finish:1", "buy:buyreward", "start:1", "finish:1", "buy:buywj", "start:2"] }]
```

| Goal | Reached when |
|---|---|
| `start:N` | Course N's start gate starts its timer (it wasn't timing). |
| `finish:N` | The player's own finish at course N's end gate. |
| `buy:buyreward+buyclone` | The game bought each of these boxes (any order). Course 1: `buyreward`, `buyclone`, `buywj`; Course 2: `buyreward2`, `buyclone2`, `buyclonemult2`, `buyglobalreward2`, `buyds`; Course 3: `buyreward3`, `buyclone3`, `buyclonemult3`, `buyfast3`, `buyglobalreward3`, `buydj`, `buyfree3`, `buyclonespeed`, `buystatue`; Course 4: `buyreward4`, `buyclone4`, `buyclonemult4`, `buybig4`, `buyfast4`, `buymaxsize4`, `buyglobalreward4`, `buyswap`, `buyfree4a`, `buyfree4b`, `buyfree4c`, `buyprestige4`; Course 5: `buyreward5`, `buyclone5`, `buyclonemult5`, `buyglobalreward5`, `buybig5`, `buyfast5`, `buyprestige5` (trips the breaker), `buyblockswap5` (swap coloured blocks when dashing; the box sits on Course 4's row and appears only after the breaker trips), `buyfree5` (secret #2). |
| `reward:bonusN` | The game buys bonus N's reward box (the upgrade box nearest its end rect: the bonus's unlock). |

An event that isn't the next goal changes nothing (buying another box, finishing another course). Unlocks come only from
the game's purchases; the plugin writes none.

Run names are letters, digits and `%` (`ANY%`).

`buy:buyprestige5` is Course 5's prestige box with `tripBreaker` set: after `BoostOldCourses` it resets Courses 1–5,
sets Cash to 0, swaps Course 4's `buyswap` box for `buyblockswap5`, turns the Course 1–3 movement boxes into
breaker-light boxes and turns Area 1's lights off. The studio's goal list marks that goal red and every goal after it
dark (`/api/state` `fullTas.runs[].breaker`: the goal's index).

## Seed

Unity's RNG is deterministic for the runs: every Full-TAS new game starts it from one fixed seed (the plugin's
`FullTasSeed`, `UnityEngine.Random.InitState` at the fresh new game right before its start snapshot, and again before a
real-time playback's first tick). The snapshot therefore carries the seeded `UnityEngine.Random.State`
(`"random": [s0, s1, s2, s3]` in the trace's start state and after every tick, and the trace header's `fulltas` carries
the `seed`), so the turbo judge, the trace and the offline search all start from the same state. The seed is fixed and
unoptimized (`SEED (unoptimized) <value>` on the overlay); nothing user-settable writes the RNG.

Real-time play draws `UnityEngine.Random` on every rendered frame (LightFlicker, animation speeds), so a real-time
playback's rolls differ from the turbo replay's even from the same seed; the gate (turbo) is the judge.

## Routes

`fulltas/<run>_NONE <time>`, saved only by the gate (`SaveSolverRoute`), only when strictly faster. The gate replays the
route in turbo from the new game with the usual buffer audit, checks the unlocks against the run's own purchases so
far, and refuses a quick restart on two ticks in a row (a real button is released in between). Splits per goal are kept
in `fulltas-splits.json`.

## Studio: Full-TAS tab

- **Goals** (`POST /api/fulltas/goals {name, goals}`), **Capture start** (`/api/fulltas/capture`: start trace + world,
  seeded with the saved best), **Play** (real time from a new game), **Solve** / **Stop**
  (`/api/fulltas/solve {name, minutes, windowMinutes, window}` runs `IGTAP.EngineSim fulltas`; its output shows in the
  tab). `/api/state` has `fullTas`.

## Offline search

```powershell
& $exe fulltas $traces\fulltas-main.json --minutes 60 --window-minutes 1 --window 2   # --no-submit, --fresh, --threads N
```

- The best is `<traces>\fulltas\<run>.json` (or the trace's inputs when better or when there is none).
- Windows of W goals (default 2): each worker replays the best up to goal i-1 (`Simulation.Advance`), starts there
  (`SetStart`) and searches for the fastest way through goal i+W-1. The first goal's split is kept only as part of the
  whole: a later goal reached sooner is what counts, so a lap that ends slower but sets up the next one wins.
- A window's best is spliced into the run with the old tail after it; when the old tail no longer reaches the rest (the
  state differs), the following windows are searched again from the new state, seeded with the old tail, and the run
  is kept only if it ends sooner. Before a run is complete, the search pushes the first goal not reached yet.
- Routes found from a start with the same movement state for the same next goal seed each other (the repeated laps).
- A complete, faster run goes to `offline-best\fulltas-<run>-<time>.json` and to the gate (`/api/fulltas/submit`).

## Offline economy

The whole run needs Cash offline. A start state whose economy carries the clones (every Full-TAS trace) runs the
economy natively (`enginesim/Runtime/Economy.cs`): course-finish rewards, comp boost, new-best clone paths, clone spawns
and payouts on and off screen (the clonesLoD trigger), what the courses' local boxes change (reward, clone count, clone
multiplier) and the global boxes `GLOBALcashPerLoop` (its own course's reward; the game updates the others on a random
5 s phase), the global clone speed and size caps (`maxCloneFastness`, `maxCloneBigness`: dictionary only while both
clone chances are 0) and the statue (`unlockPrestige`: a global clone multiplier instead of its own entry). A prestige
box — the statue and Course 4's `buyprestige4` — also runs `BoostOldCourses`: each old course's reward tier becomes the
buying course's tier plus its own mod and its reward updates, and its boxes gain half their cap multiplier, become
buy-max and turn back on. A buy-max box's touch buys what `GetNumPlayerCouldBuy` counts (`IE_BuyMax`): the first during
the tick's physics, the next at that tick's end, then one at the end of every following tick while affordable (one frame
per tick, `TurboRunner.EndOfFrame`, `Shop.EndOfFrame`). Real-time playback renders more frames per tick and so buys
faster. The block swap (`buyswap`) is modeled as the purchase's bookkeeping
and its buy goal only — the colliders it swaps aren't — so it works as a run's last goal and any run past it ends
(`Unmodeled purchase: block swap (collider changes)`). Cases the model can't follow end the run (`Unmodeled economy:
...`): clone speed/size/colour rolls, clone dust, Vman, the other global boxes and a prestige buy while Vman is active.

The trip breaker (`buyprestige5`) is modeled in full: `BoostOldCourses`, then each of Courses 1–5 through
`localUpgrades.OnPrestige` (local upgrades, clones and best path reset; Courses 1–3 get the breaker layer's tier mods,
Course 4 `c5.tier + c4.tier - 1 + 4`, Course 5 keeps its tier), the Course 1–3 movement boxes turned into
`activateNextBreakerLights` boxes (their purchase is modeled too), `PrestigeUpgradeBox4` disabled, then
`tripBreakerScript.TripBreaker`: Cash 0, `buyswap`'s box off and `buyblockswap5`'s on, and the coloured blocks swapped
to orange (Courses 4–5 collide differently afterwards). The swap needs both colours' block geometry: the world export
writes each coloured-block composite's `sourceShapes` (its geometry with its tilemap on); an older export stops the run
at the breaker (`Unmodeled physics: ...`). `buyblockswap5` sets `blockSwapUnlocked` and ends the run (`Unmodeled
purchase: block swap on dash`), so it works as a run's last goal. The turbo gate's snapshot restores what the breaker
changes (box GameObjects and enabled flags, Area 1's state, the breaker's lights, the blocks' colliders).

## Status (2026-09-25)

- `main` has 609 goals, all from `python tools/plan-fulltas.py --write` after `reward:bonus1`: Course 2's economy up
  to dash (~236 s), the dash bonuses, then Course 3 to `buyclonespeed` and `buystatue`, Courses 1 and 2 bought to
  their boosted caps with a lap each, Course 3 to `buydj`, and Course 4 lapped, `buyprestige4`, then `buyswap`, with
  clone speed/size chance buys on Courses 3 and 4 (the plan counts their rolls at their average; ~15.8 min in the
  plan; its 3 -> 1 and 3 -> 4 transitions are guesses, no route exists yet).
- Not in the goals: the free global boxes with no known route (`buyfree3`, `buyfree4a/b/c`) and Course 4's 3e12 W
  `buyglobalreward4`.
- `ANY%` has 789 goals from `python tools/plan-fulltas.py --any-pct --out FILE`: `main`'s goals through `buy:buydj`,
  `reward:bonus5`, Course 5 farmed (with `reward:bonus4`, `reward:bonus15`, `buyfree5`, two `buyglobalreward5`) to
  `buy:buyprestige5`, then Course 4 farmed (its reward tier is 29.5 after the breaker; Course 5's 16.5 would take
  ~1e15 ticks to 9.49e34 W) to `buy:buyblockswap5` (~19.7 min in the plan). Its 3 -> 5 and 5 -> 4 transitions and the
  bonus detours are guesses; no route exists yet.
- The offline search seeds its laps with the saved standalone course, bonus and transition routes.
- Real-time playback warns on the overlay when it drops frames: Update-driven state (clone pay, purchases waiting on
  it) can then differ from the route's.
