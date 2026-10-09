# Zip% handoff (2026-10-08, second session): state, tools, and what's open

Read this section first; the 2026-10-07 notes below it are history (still useful for the planner's internals).

## State

- **Installed best** (`fulltas/Zip%.json` in the config folder, `runs/Zip%.json`: 991 goals): goals 1-513 as before, then the
  stages from goal 513 built by this session: `buytree5` on tick 50,130 (goal 826), `buytree6` on tick 52,115 (goal 831), then
  Course 6/7 investment and tree-box levels to about tick 58,000-60,000 (goal ~987). Next goal: `buy:buytree7` (goal 988),
  then `buytree12`, `buytree13`, `buytree16`.
- The tree is at about level 7 (buytree0-6 bought), `buytree0` (green clone chance) still at level 1 in the installed route:
  the planner could never buy it until `70d99f0` (below). Expect the next stages to level it.
- **Nothing is running.** The last engine build is `C:	mp\igtap\zip\eng53` (HEAD `70d99f0`).

## Resume

```
& C:	mp\igtap\zip\eng53\IGTAP.EngineSim.exe fulltas-plan $tracesulltas-Zip%.json --stages --mode beam --beam 4 --minutes 30 --rounds 4 --off-ticks 300 --only <list> --piece-minutes 2
```

`<list>`: the full `--only` list below without `lap:9,record:9` (Course 9 is closed under strict buys, see open issue 3). Run
from the repo (`--install` writes `runs/Zip%.json`). Each stage plans to the next tree goal, realizes, and installs. Watch the
`realize: ... planned ... (C Cash, G GP), built ... (C Cash, G GP)` lines: they show where the plan and the build part.

## The feedback loop (use it; full `--realize` reruns cost 10-15 minutes before they reach the step you changed)

`fulltas-plan <trace> --from <goal> --target <a later goal> --try STEPS` plays steps from the best's state at `--from`:
- realize's own steps: `lap:7`, `record:6`, `buy:box`, `all:box`, `atoms:N`, each with an optional `@T` (planned ticks) and `!`
  (a trail: player, courses on screen, GP every 25 inputs);
- `wait@T`: the build stands T ticks (full simulation); `idle:PLACE@T` / `finish:N@T`: the plan's economy (SimEconomy) does;
- `where:PLACE`: the movement table's place against the box's touch spot.

Write the expected result before running; if a check hasn't answered in 5 minutes, stop it and think.

## Measured this session (don't re-derive)

- **Saved course routes after a quick restart** replay only on the category start's exact state: the restart's landing moves
  with the motion before it (sub-unit), and the 280 fps frame phase matters. Course 7's 8.78 from its own start trace,
  restarted after k idle ticks: only k=4 of 0-80 finishes. Realize therefore repairs: gate walk, restart onto the course,
  the saved route as a search seed (seconds), then the farm loop from the finish (Course 6 1.35 s loop: 61-63 ticks a lap;
  Course 7 11.33 s loop: 521 ticks).
- **Economy fidelity:** plan economy and build agree tick for tick when standing (`idle` vs `wait`: same Cash and GP over 10,000
  ticks). Courses' clones go off screen by LoD exactly as the game; no zone unloads courses in Area 2.
- **GP:** only green clones pay it (counter += greenCloneClance per spawn, a green at 100). Off screen a green pays
  gpReward x size / divisor (~11.7 x at Course 6's 0.94 s path): lumps of hundreds of GP. Lapping a course keeps its own
  on-screen clones from paying. With buytree0 at level 1 (1% chance) GP income is tiny; five more levels gave ~6x.
- **Course 9 under strict buys:** the way there from the tree passes buytree0's box; with NP held the touch buys a level, a
  stray buy that ends a strict run (rule 38).

## Open issues, ranked (from the session and an adversarial review; not fixed)

1. **Tree transitions ignore the tree level** (`EconomyMoves.AddSavedTransitions`): every `tree<L>to<C>` is a `lap:C` move from
   bonus 11's landing whatever the level (README rule 21: Courses 7/9 keep levels 0 and 3 only). Gate by the run's tree
   level, or drop them past level 4 and leave lap:C to measured moves. This drove the Course 7/9 lap churn.
2. **`CloneRates` (the beam estimate's GP rate) uses the on-screen spawn rate** (Count/PathLength) and no ceiling; off screen
   a course spawns once a second and pays ceil(x)/divisor. The estimate mis-ranks Courses 7-9's clones.
3. **Course 9 / `buyatomlevel9`** (the earlier study: every zip-mover plan buys it; a GLOBAL box, kept through atom touches):
   either set `"buys": "loose"` in `runs/Zip%.json` (the user's call) or plan `buy:buytree0` as the goal right before the
   Course 9 path when NP allows (it's the best NP buy anyway); at buytree0's cap (20 levels, ~1e24 NP) the stray is gone.
4. **Atom touches wipe Course 6's local upgrades** (upgradeBox.cs:688-695); since goal 513 the goals hold ~70 buygreen6, ~64
   buyclone6, ~77 buyreward6 for 4 touches. Nothing in `Estimate` charges a touch for that, so `buytree10` (exempt Course 6,
   750k NP) never ranks. Charge the rebuy, or make buytree10 an intermediate target.
5. **`LapGiveUp` is wall-clock** (`stepClock`): blocking depends on machine load (other agents share the CPU). Use sim ticks or
   plays.
6. **Plan vs build GP**: with `ExpectedGreens` (planning only) the beam no longer chases lucky green lumps, but the build's GP
   still runs well under the plan's away from Course 6 in some steps (`all:` steps buying fewer levels than planned compounds
   it). Read the per-step GP columns; `--explain` (a plan replay that prints each course's clones at big GP steps) was
   written and lost in a checkout, worth re-adding.
7. Smaller: `SavedLap`'s `_lapMisses` never clear on tree growth; `Waited`'s downward pass replays from the stand's start
   each 5 ticks; a failing `Lap()` costs up to 12x5 saved-lap plays plus RecordLap twice plus a search.
8. Not run after this session's changes: `tools/validate.py`, `tools/tech-check.py`, and replays of Tree%, TouchGrass-ANY%,
   FalseEnding%-280 (they share realize's code).

## This session's commits (oldest first)

`3c81ca0` farm restart laps first; `7b464b1` NP priced in atoms (NpWait); `de867f0` goals reset; `7624af8` farm loop laps
whole; `37d057a`/`5ec2199` category checks, phase-aware restarts; `c308876` AdvanceFrom past the last goal (laps were cut
away); `3d40618` NP over cap replans; `481832d`/`30d8383` GP logging, `--try`; `30ff670`/`d291018` stand waits linear;
`eda98b0` gate walk before record lap; `1448dff`/`fe2c10c` lap blocking; `c92b20a` `finish:` diag; `dd40f83` ExpectedGreens;
`70d99f0` buys from the box's touch spot (buytree0), review fixes.

# History: Zip% handoff (2026-10-07), every lesson from the buytree0 → zip movers push

Read this before touching Zip% again. The plan when resuming: **start over from goal 513 (`buy:buytree0`, tick 36328)**
and rebuild Area 2 with every course, fixing each problem below as it shows up instead of letting a loop run on it.
Continues [issue #26](https://github.com/ChandlerFerry/IGTAPTasMod/issues/26).

## Where things stand

- **Goals 1-513 are good.** They replay the same offline, in turbo and at 1x (probe-checked through tick 36643). Goal 513 is
  `buy:buytree0` on tick 36328.
- **Everything after 513 is throwaway.** The installed best (`fulltas/Zip%.json` in the config folder) reaches goal 703
  (`buy:buytree1`, tick 51369). It was built with 5.9 s Course 6 laps (see "Laps") and a weak plan, so don't keep it.
  `runs/Zip%.json` has 709 goals: 703 built ones, then `buytree5, 6, 7, 12, 13, 16`.
- **To restart from 513:** set the goals to the first 513 plus `buy:buytree5, buy:buytree6, buy:buytree7, buy:buytree12,
  buy:buytree13, buy:buytree16`, in both `runs/Zip%.json` and the trace header (`fulltas-Zip%.json`'s `"fulltas": {"goals"}`).
  Each header entry looks like
  `{"label":"buy:buytree5","kind":"buy","course":0,"boxes":["zone 2/TreeArea/The Tree/TreePart5/GlobalNPincreaseBox#0"]}`.
  Box paths and sibling indexes are in `FullTas.cs`'s `Boxes` table. The best route itself can stay: it replays to 513
  whatever comes after.
- **Backups:**
  - `fulltas/Zip%.before-replan-c6only.json`: the Course 6-only build that reached `buytree13` (goal 3402, tick 178998,
    on the fixed engine).
  - `C:\tmp\igtap\zip9d\Zip%-runs-before-replan.json`: its run definition.
- **Nothing is running.** Don't start engine sims without the user's go-ahead.
- **First thing on resume:** regenerate `fulltas-Zip%-seeds.txt`. Start a Full-TAS reach solve of Zip% in the studio and stop
  it at once; the plugin rewrites the seed list and its seed traces from `routes.json`. The list is from Oct 6 and lacks the
  Course 6 record `6_WJ_DS_DJ_BS 0.94`, so every stage has been using stale routes.

## What the user knows about Area 2 (don't plan against it)

- **Courses 6, 7 and 9** are reachable from the start of Area 2.
- **Course 8** opens with the "bottom area" unlock. The code assumes that is `buytree7` ("Area 2 door open"); confirm.
- **Course 10's boxes** (its bonus) open with `buytree13`. **Course 10 itself can't be finished** before the zip movers:
  all its saved routes use them. Which reward box is Course 10's bonus is still unknown, so it isn't offered yet.
- **GP comes from every course's clones** (`clonesScript.cs`, `courseScript.cs`):
  - Each clone spawn adds the green-clone chance (`globalStats.greenCloneClance`) toward a counter, and at 100 that clone is
    green.
  - A green clone pays its course's `gpReward`: base 1, × (1 + its course's green box level: `buygreen6`/`7`/`9`), × (1 +
    `buytree2` × 50% + `buytree6` triple threat), × `CloneDustGpCdMult`.
  - So **GP a tick ≈ Σ courses (clones ÷ path) × green chance × gpReward**.
- **`buytree0` (GreenCloneChanceBox)** is +1% green-clone chance a level, up to 20 levels. It is paid in **NP**: about 1, 5,
  17, 51, 169, 600 NP for the first levels (cost → ((cost + 1)^1.1) × 2.3).
- **NP:**
  - The cap is `NPrewardPerTick × 6000` (`globalStats.cs:1157`, 50 × 120).
  - `buytree5` costs 22,500 NP, so it needs a rate of at least 3.75 a tick.
  - `buytree16` (the zip movers) costs 625M NP, about 104,000 NP a tick.
  - Only atoms raise the rate. `buyatomlevel9` and `buyatomlevel10` make new atoms land a tier up; the earlier Area 2 study in
    `docs/fulltas.md` found every zip-mover plan buys `buyatomlevel9` first.
  - Atoms cost GP. An atom touch is a buy-max: it buys every atom the GP pays for, and it resets Cash and the courses (their
    clones and boxes) not exempt: `buytree10` exempts Course 6 (750,000 NP), `buytree11` Course 7 (100M NP), `buytree22`
    Course 8 (800M, past the zip box); RP boxes (50 RP: GP atoms reset nothing; 60,000 RP: a course) are out of Zip%'s reach.
- **The user's route knowledge:**
  - The Course 6 record in this category is **0.94 s**.
  - The run itself has found 1.06 s Course 6 laps.
  - Anything near 5.9 s is wrong.

## Engine fidelity (fixed: keep it that way)

- **Tree boxes falling into place** (`295ac86`, `docs/fidelity-log.md`):
  - IE_Grow drops each new tree box from 2000 u above, writing its transform at every frame's end.
  - Unity syncs such moves at the next rendered frame's start (Physics2DEarlyUpdate), before that frame's FixedUpdate. The
    engine and turbo synced them only at the physics step.
  - On tick 36328 the 1x wall rays hit `GreenCloneChanceBox`'s side and the engine's didn't. That left the run 11 u short of
    a checkpoint near Course 6, and the quick restart went to bonus 11's checkpoint instead.
  - Fixed by `PhysicsWorld.FrameStart` and `TurboRunner.FrameStart`.
  - Verified: the engine matches a 1x probe tick for tick through 36643, and turbo-then-1x from 36440 for 202 ticks. Tree%,
    TouchGrass-ANY% and FalseEnding%-280 replay unchanged.
  - **`tools/tech-check.py` and `tools/validate.py` were not run after it. Run them.**
- **How to check a stretch against 1x without guessing:**
  1. `python tools/probe.py <name> "fulltas/Zip%_NONE" <ticks.json> --from-tick N` (turbo to N, then 1x, every tick and
     frame logged).
  2. `fulltas <trace> --check <route.json> --ticks-out <file>` (now with `wall`, `respawn`, `--watch [PARENT/]NAME`).
  3. Compare the probe's `bodyX/bodyY` on tick t with the engine row for t-1. The probe's tick row is logged after
     FixedUpdate, before the physics step.

  The probe also logs the upgrade boxes within 300 u (transform and solid collider bottom).
- **Stray-buy avoids:**
  - Boxes the run could buy by touching them become discourage boxes, read with the window's start restored (`WithStrays`).
  - Their Simulation is built on its own thread (`Solve.OnThread`). A second Simulation built on a worker's thread takes over
    the engine hooks, and every run then ends at input 0 ("Solver run ended").

## Planner (`fulltas-plan`, `EconomyPlan.cs`): what was wrong and what's fixed

- **Atom touches:**
  - No `atoms` step from the atom box itself (`cf89915`). The buy-max routine's later buys are no goals
    (`Simulation.Bought`'s `continuing`), so a second touch planned there is a goal the run never reaches.
  - Each atom touch records how many atoms it buys (the step's `levels`, `c73ae78`). Realize stands for that price, up to
    `--off-ticks` past the plan, then touches with what it holds (`12032a7`). The plan stands at the box; realize stands
    where the step before left it, and the on-screen clones pay differently there.
- **Beam speed:**
  - An atom option stands at most 300 ticks (`AtomStand`). One short of its price with no income isn't played.
  - 82% of the beam's time had gone to atom options that failed. This took the plan from goal 630 to `buytree5` from 341 s
    to 50 s.
- **Beam estimate (`Estimate`); all three were needed:**
  - **A target box not grown yet** (`buytree5` before its segment) **was priced at nothing**. The beam then chased the next
    cheap tree box and laid up Cash. Now priced at its loaded cost (`PriceAhead`, `c01f228`).
  - **NP sitting at its cap** earned nothing over the window, so every state read as "no income". Now a price over the cap
    ranks by the cap's own rate, plus the next atom's missing GP (`fc50721`).
  - **Investments only counted once their money showed** over a 300-tick window. The estimate now also takes the economy's
    current rates (`Economy.CloneRates`: each course's clones' Cash and green-clone GP a tick; and the NP rate).
- **Doors** (`e993625`): Course 8's steps need `buytree7` bought, Course 10's boxes `buytree13`, and Course 10 laps are never
  offered (`Gates`, `Never`).
- **`--only`:** the planner offers only these boxes and laps. The Course 6-only list (`lap:6, record:6`, Course 6's boxes,
  `buyatom`, `buytree0-6`) is why the earlier build used Course 6 the whole way and why no plan ever reached `buytree16`.
  The full list used last:

  ```
  lap:6,record:6,lap:7,record:7,lap:8,record:8,lap:9,record:9,buy:buyreward6,buy:buygreen6,buy:buyclone6,buy:buyatom,
  buy:buyreward7,buy:buyclone7,buy:buyclonemult7,buy:buygreen7,buy:buyclonecd8,buy:buyclonegp8,buy:buycd8,buy:buycddigits8,
  buy:buyreward9,buy:buyclone9,buy:buygreen9,buy:buycd9,buy:buyclonemult9,buy:buyatomlevel9,buy:buyreward10,buy:buyclone10,
  buy:buyclonemult10,buy:buywatts10,buy:buynp10,buy:buyatomlevel10,buy:buygreen10,buy:buytree0,buy:buytree2,buy:buytree3,
  buy:buytree4,buy:buytree5,buy:buytree6,buy:buytree8,buy:buytree9,buy:buytree10,buy:buytree11,buy:buytree14
  ```

  (no spaces: one argument).
- **Still wrong, the main open problem:** the plan is mostly NP-driven and undervalues GP investments.
  - With all courses and the fixes above, the plan from 513 to `buytree5` comes out at tick 61,191 (4 minutes of beam).
    The Course 6-only plan was about 52,000-54,000.
  - It never buys a second `buytree0` level, though each level multiplies all GP.
  - It runs Course 7 for Cash, but the chain Cash → clone and green boxes on every course → GP → atoms → NP rate is invisible
    to a 4-wide beam, which prunes investment states before they pay.
  - Beam 32 is far too slow (about 4,000 plan ticks in 8 minutes). Beam 8 is slow too.
  - Next ideas:
    - Rank by projected time to the target from the current rates, including what each box buy would add.
    - Or let the user give the macro order (e.g. level `buytree0` and the green boxes once NP allows) as intermediate goals
      or a `--compare` prefix, and let the beam fill in the details.
- **One plan straight to `buytree16` doesn't work:** two 60-minute beams from 513 never got past tick about 103,000. Keep the
  intermediate tree targets (`buytree5, 6, 7, 12, 13`) as goals so `--stages` climbs one at a time. The planner adds the tree
  boxes on the way (`buytree1-4` etc.) as earlier targets itself.

## Realize (building the plan into inputs): what was wrong and what's fixed

- **Slow library pieces:**
  - A realize step that isn't a stand can't play a library piece reaching its goal later than the step's bound: 3× the
    planned ticks, at least 500 more; for laps and record laps 2×, at least 100 more. It searches instead (`10499f6`,
    `fd08cac`).
  - A step that still comes out walking past the bound isn't kept; the move is measured and the plan made again
    (`cac4975`).
  - Before this, a 4,884-tick library walk to Course 6's gate (plan: 227) was kept and set the build back 93 s.
- **Record laps:**
  - Searched only from past the course's gate (`d8e3774`). From before the gate, through gate and finish together, the
    search found nothing for 12-36 minutes. A plain lap (the gate, then the finish, each searched) takes seconds.
  - `RecordLap`'s gate walk has the same slowness bound.
- **Unreachable places:**
  - A step that fails for want of a way (not of Cash) blocks its place until a tree box is bought (`8d6ab67`).
  - `buytree2` was planned before `buytree1` grew the way up, and searched 12 minutes a round.
- **Laps use only the course's fastest saved route** the run's unlocks allow (`fd08cac`):
  - By its name's time: `6_WJ_DS_DJ_BS 0.94`; a chain's times are added.
  - It is used for the plan's lap and record steps, their library filing and their searches' seeds.
  - Course 6's `seed 300fps 5.94` had been filed as a lap piece. Wherever the record didn't play from a restart (the 1.20 s
    one always died at action 39, landing in the air at 26174.6, -488.85), laps took 5.9 s.
  - If a plan ever needs a slower lap (record-break clones), derive it from the fastest; that isn't written yet.
  - **Unverified:** no engine run since this change.
- **The old `buytree5` failure:** realize reached the atom box 1,000 ticks early with 64 GP instead of the plan's 213, bought
  one atom, and NP stayed at 0.38 a tick against 22,500. Fixed by the atom `levels` above.
- **Waits:**
  - `realize: buy:buyatom waited N ticks for its Cash` is normal.
  - Large waits mean the plan and the build disagree about income: the build arrives earlier with less.
- **Searches failing:** the solver's status line now lists the top three ways its runs end (`ends: ...`). Read it before
  guessing why a window finds nothing.

## Process lessons (the expensive ones)

- **Test one step, not the whole build (the feedback loop).**
  - A change to how realize plays a step is checked on that step alone: `--try STEPS` for laps, buys and atom touches
    (seconds to minutes from the best's state at `--from`), `--join` for a saved route. Never by restarting a whole `--realize`: its beam and every
    step before the changed one cost 10-15 minutes a try (2026-10-08: eight such restarts for the Course 6 and 7 laps).
  - Before each run, write down what result would prove the change right and what would prove it wrong; if a check hasn't
    answered in 5 minutes, stop it and think instead of waiting.
  - A search that runs minutes without a candidate is the wrong question: ask what saved route should have covered it
    (Course 7 had an 8.78 record and an 11.33 farm; the run just wasn't where they start) before tuning the search.
- **Check the game, not just the engine.**
  - Everything built after `buytree0` was on a physics mismatch for a whole day.
  - After an engine change, or before trusting a long build, probe a stretch at 1x (above).
- **Don't run hour-long loops on an untested idea.**
  - Every long run this session died on the first new problem: an unreachable box, a slow lap, the beam's estimate, a
    stripped goal list.
  - Watch the first stage closely (first 10-15 minutes), and look at the plan's steps
    (`grep "beam: tick" | tail -1`: its trail) and the build's `planned X, built Y` lines.
- **Read the planner's numbers.**
  - The beam's progress line shows the rates and the estimate (`a tick: Cash, GP, NP; F n`). An estimate of a few hundred
    ticks for a 22,500 NP target was the tell for the unpriced `buytree5`.
- **Installs:**
  - Stages install only at a stage's end; `--rounds 4` keeps that under about 30 minutes.
  - `--replace` installs a replan however short. It now requires getting past `--from`; a failed one once replaced the goal
    list with `513 + buytree16`.
  - `install` writes `runs/<run>.json` and the trace header together.
- **Killing processes from bash:**
  - `powershell ... Where-Object { $_.CommandLine -like '*run.sh*' } | Stop-Process` also matches the bash running that
    command and kills your own shell.
  - Use `ps -ef | grep "[r]un.sh"`, then kill the engine by PID.
  - Stop only your own `IGTAP.EngineSim` processes; other sessions share the machine.
- **Private engine builds:** `dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o C:\tmp\igtap\zip\engNN`. The
  shared exe is locked while anything runs it.
- **Line endings:** `enginesim/Cli/FullTas.cs` is LF. A Python edit through text mode wrote CRLF once and the diff became the
  whole file. Edit with `newline=''` or the Edit tool.
- **Commits:** commit each fix as it lands; the user asked for that.

## Commits this session (oldest first)

| Commit | What |
|---|---|
| `cf89915` | No atom step from the atom box; 7× faster beam (`AtomStand`, no short atom options); stray avoids read at the window's start, on their own thread; `--dump` past the best |
| `c73ae78` | Realize stands for the plan's atoms' price |
| `a0f9db9` | Record-lap search from its start's own goal; solver status shows how runs end |
| `295ac86` | **Engine and turbo sync script-moved colliders at each frame's start** (the 1x divergence) |
| `cac4975` | No realize step kept far slower than planned |
| `d8e3774` | Record-lap search only from past the gate |
| `10499f6` | No library piece past a step's bound; search instead |
| `4cba9b0`, `12032a7` | Atom touch short of the plan's atoms: bounded wait, then touch with what it holds |
| `8d6ab67` | Unreachable places blocked until a tree box is bought |
| `e993625` | Area 2's doors in the planner; `--replace` |
| `7dba000` | `--replace` only past `--from` |
| `fc50721` | Capped NP ranks by its rate and the next atom's GP |
| `c01f228` | Not-yet-grown tree boxes priced; clone income modelled in the estimate |
| `fd08cac` | Laps use only the course's fastest saved route; tighter lap bound |

## Resume checklist

Done 2026-10-07 evening (no engine run since): goals reset to 513 plus the six tree targets (both files; the old 709 in
`C:\tmp\igtap\zip10`); private engine `C:\tmp\igtap\zip\eng20`; lap pieces now take a farm route's quick-restart laps
first (`FastestLap(..., restart: true)`; `6_WJ_DS_DJ_BS farm 1.3500`, `8_... farm 1.1536`; record laps still the standing
record); the beam prices NP in atoms (`NpWait`, docs/fulltas.md's beam: GP is what an NP target costs, so GP investments
rank at once). Waiting on: the Course 7 and 9 farm routes (another agent), then step 1 and the rest.

1. **Seeds:** regenerate `fulltas-Zip%-seeds.txt` (reach solve started and stopped in the studio). Check that it lists the
   0.94 s Course 6 route and the farm routes (`... farm ... from its quick restart`).
2. **Goals:** reset to 513 plus the tree targets (above).
3. **Engine:** build a private engine.
4. **Course 6 laps:** check them before anything else. Read the beam's estimate too: an NP target should now show a GP wait
   (the progress line's `F`), not `NoIncome`. One plan from 513 to `buytree5` with the full `--only` and
   `--realize --rounds 1`, then read the lap steps' built ticks. They should come out near 1 s (about 50-60 ticks), not 5.9.
5. **GP:** fix the planner's undervaluing of GP investments (or get the macro order from the user) before a long run.
   Compare the `buytree5` tick against the Course 6-only build's.
6. **Stages:** run them with `--rounds 4`. Watch the first one. Probe the new stretch at 1x once it installs.
7. **Last stretch:** for `buytree13` → `buytree16`, check that the plan buys `buyatomlevel9`, atoms and the comp-boost boxes;
   the Course 6-only economy never planned it.
