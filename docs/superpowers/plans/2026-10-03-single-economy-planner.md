# One Economy Planner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the five economy planners with one `fulltas-plan`: Dijkstra over whole lap/buy/bonus schedules, the
engine's own economy as the Cash model (Area 2 included), measured ticks as the movement model.

**Architecture:** Phase A teaches `enginesim`'s `Economy`/`Shop` the rest of the game's economy (Area 2), each piece
checked against 1x. Phase B adds an economy-only drive of the simulation, a movement table and the search, and realizes
the winning goal list with the existing loop builder. Phase C swaps the `--auto` goals part onto it and deletes the old
planners.

**Tech Stack:** C# (.NET 10, `enginesim`), the game's decompiled source for reference (`C:\tmp\igtap\b640raw\`, build
25640326), Python 3 for the one comparison tool.

**Spec:** [`docs/superpowers/specs/2026-10-03-economy-planner-design.md`](../specs/2026-10-03-economy-planner-design.md)

## Global Constraints

- README `### Rules` are non-negotiable; rule 49: an `enginesim` gap is fixed in `enginesim`, never worked around.
- Rule 50: the offline engine matches the game tick for tick. Every economy change in Phase A is checked against a 1x
  probe (`tools/probe.py`) and `fulltas --check --ticks-out` (compare each currency as the sequence of values it takes:
  1x's physics count runs ahead through dash freezes, `enginesim/README.md` "probe").
- Rule 45–48: search offline only; the game is started only to capture traces/probes and to judge a new best.
- The engine exe is shared: build a private copy for long runs (`dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c
  Release -o <dir>`); stop only your own engine processes, by PID.
- Regression gate after every Phase A task: these replays' goal ticks, live time and purchases don't change:
  `& $exe fulltas $traces\fulltas-FalseEnding%-280.json --check $traces\fulltas\FalseEnding%-280.json` and the same for
  `TouchGrass-ANY%` (`$exe`, `$traces` as in `enginesim/README.md:81-82`). Record the before numbers in Task 1 Step 1.
- Code style: match the surrounding code (doc comments citing the game's file:line, `Unmodeled(...)` for what the
  engine refuses, no new dependencies). CRLF where the file already uses it.
- Docs are part of the change: `docs/fulltas.md`, `enginesim/README.md`, `docs/fidelity-log.md`, `AGENTS.md`, README's
  Layout table follow the code in the same task.

## Review Focus

1. **A schedule the build can't play.** The plan buys with Cash it predicts; the strict build strays into an affordable
   box on the way (rule 38) or arrives short. Expected: the realize loop measures, re-plans, and reports where it
   stopped and why; it never writes a goal list that didn't replay end to end as "done". Test in Task 12.
2. **Area 1 routes unchanged by the Area 2 port.** Expected: the regression gate's numbers identical. Every Phase A
   task's last step runs it.
3. **A step the movement table has no measurement for.** Expected: a flagged estimate, listed in the output
   (`estimated: [...]`), measured during realize; never silently treated as measured. Test in Task 10.
4. **Dominance pruning a better state.** Expected: a state is dropped only if another at the same place, unlocks, box
   levels and rewards taken is no later with at least as much of every currency. Test in Task 11's self-check.
5. **Restore fidelity of the new economy state.** Expected: `Capture`/`Restore`/`AddTo` carry every new field (atoms,
   NP, colour counters, comp boost NP effect), so a seek/snapshot replay equals a straight replay. Test in Task 7.

---

## Phase A — Area 2's economy in the engine

### Task 1: Every currency's income, not just Cash

**Files:**
- Modify: `enginesim/Runtime/Economy.cs` (`Earned` → per currency; `State`, `Capture` :888, `Restore` :901, `AddTo` :917)
- Modify: `enginesim/Runtime/Shop.cs:91-94` (`Available`)
- Modify: `enginesim/Cli/FullTasAutoLadder.cs:468` (reads `Earned`; deleted in Task 15, keep compiling until then)

**Interfaces:**
- Produces: `double[] Economy.Income` (index `(int)globalStats.Currencies`), `Economy.Earned` kept as `Income[Cash]`
  getter; `void Economy.Add(globalStats.Currencies c, double v)`.

- [ ] **Step 1: Record the regression baseline.** Build (`dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release -o C:\tmp\igtap\es-plan`)
  and run both `--check` replays from Global Constraints; save their output to `C:\tmp\igtap\baseline-FE280.txt` and
  `baseline-TG.txt`.
- [ ] **Step 2: Replace the Cash-only accumulator.**

```csharp
// Economy.cs
internal readonly double[] Income = new double[7];   // per globalStats.Currencies, what the economy paid since the start
public double Earned => Income[(int)globalStats.Currencies.Cash];
internal void Add(globalStats.Currencies c, double v) { Income[(int)c] += v; }
```

  Replace every `Earned += x` with `Add(globalStats.Currencies.Cash, x)`; the resets at :580 and :854 set
  `Income[(int)globalStats.Currencies.Cash] = 0.0` (only Cash: `doNewAtomPrestige` zeroes Cash, upgradeBox.cs:687).
  `State.Earned` becomes `double[] Income` (cloned in `Capture`, copied back in `Restore`, every element hashed in `AddTo`).
- [ ] **Step 3: `Shop.Available` reads every currency.**

```csharp
double Available(int currency)
{
    return _start[currency] - _spent[currency] + _awarded[currency] + (Economy?.Income[currency] ?? 0.0);
}
```

- [ ] **Step 4: Build and run the regression gate.** Expected: output identical to the baseline files (`fc`).
- [ ] **Step 5: Commit** `git commit -m "Economy: income per currency (Shop.Available reads them all)"`.

### Task 2: Course rewards for every currency, clone dust, colour clones

**Files:**
- Modify: `enginesim/Runtime/Economy.cs` — `UpdateReward` :405, `SpawnClone` :320, `DestroyClone` :379,
  `CourseFinished` :445, `ColourCheck` :372, `Clone` struct :54, `CloneValues` :61
- Reference: `b640raw/courseScript.cs:497-499` (gp/rp/Cd rewards), `:396-407` (finish CD ×10), `clonesScript.cs:240-285`
  (off-screen pays), `:330-360` (DestroyClone pays), `:379-401` (`checkIfColourClone`), `:462-480` (`AssignCloneColour`),
  `globalStats.cs:1122-1136` (`RecalcCloneDustMult`)

**Interfaces:**
- Consumes: `Economy.Add` (Task 1).
- Produces: per-course `double Gp, Rp, Cd` rewards kept in a new `double[][] _rewards` indexed like `_clones` (courseScript
  has `gpReward`, `rpReward`, `CdReward` fields: write them on the live object as `UpdateReward` already writes `reward`);
  `double _cdMult` (`CloneDustGpCdMult`) in `State`.

- [ ] **Step 1: Port `UpdateReward`'s other three rewards** (courseScript.cs:497-499) next to the `reward` line, writing
  `course.gpReward`, `course.rpReward`, `course.CdReward`. Read `baseGpReward`/`baseRpReward`/`baseCdReward` by reflection
  as `BaseRewardField` is.
- [ ] **Step 2: Port `RecalcCloneDustMult`** as `void RecalcCdMult()` on the held CloneDust
  (`_sim.Shop.Holds(CloneDust)`), into `_cdMult`; call it at construction and after every CD payout.

```csharp
void RecalcCdMult()
{
    double cd = _sim.Shop.Holds(globalStats.Currencies.CloneDust);
    double m = cd == 0.0 ? 0.0 : Math.Log10(cd);   // math.log10: Unity.Mathematics, double
    m += Global(globalStats.globalUpgradeSet.additionalCDdigits);
    _cdMult = Math.Pow(m, 1.7) * 1.05 + 1.0;
}
```

- [ ] **Step 3: Colour clones.** `CloneValues` already has `Red, Green, Blue` (the game's `ColourCloneCounter`, start
  50 each). Replace `ColourCheck` with the game's `checkIfColourClone` (two `UnityEngine.Random.Range(-15, 15)` draws when
  a counter passes 100; the red counter grows only off Area 2: `course.area != 2`), returning `(bool red, bool green)`.
  On screen the flags go on the new `Clone.Red`/`Clone.Green` (replacing `Coloured`); off screen they pay at once.
- [ ] **Step 4: Off-screen payouts** (clonesScript.cs:240-285), after the Cash line, each `Math.Ceiling(... / divisor)`:
  red → RedPower capped at `rpCap` (read `globalStats.rpCap` from the world's globalStats; Unmodeled if RP cap boxes are
  bought: Task 4 handles them), green → GreenPower × `_cdMult`, clone dust (local `enableCloneDustGeneration` ≥ 1) →
  CloneDust × `_cdMult` then `RecalcCdMult()`. Delete the three `Unmodeled("clone dust")`/`("colour clone")` returns.
- [ ] **Step 5: On-screen payouts** in `DestroyClone` (clonesScript.cs:330-360) the same, with the comp boost factor and
  no divisor; the blue branch never fires on this build (`array[2] = false`), so leave it out with a comment citing :398.
- [ ] **Step 6: Finish payouts** in `CourseFinished`: with clone dust on, `ceil(CdReward × 10 × boost × _cdMult)` CloneDust
  then `RecalcCdMult()` (courseScript.cs:396-407), before `TryActivateCompBoost`.
- [ ] **Step 7: `Capture`/`Restore`/`AddTo`** carry `_cdMult` and the clone flags.
- [ ] **Step 8: Build, regression gate** (Area 1 never turns clone dust or green chance on: identical output expected).
- [ ] **Step 9: Commit** `"Economy: GP/RP/CD rewards, colour clones, clone dust (courseScript/clonesScript, build 25640326)"`.

### Task 3: The comp boost's NP effect

**Files:**
- Modify: `enginesim/Runtime/Economy.cs` — `TryActivateCompBoost` :430, `State`
- Modify: `enginesim/Runtime/Simulation.cs:1353-1357` (the stepped `courseScript.FixedUpdate` halves
  `globalStats.EffectOfNpCompBoost` natively: confirm it touches the globalStats object so a restore puts it back; if not,
  call `Touch(globalStats instance)` before the course FixedUpdates)
- Reference: `b640raw/courseScript.cs:441-450`, `:298-311`

- [ ] **Step 1:** In `TryActivateCompBoost`, before setting the time: `if (course.compBoostTimeLeft <= 0.0) stats.EffectOfNpCompBoost *= 2.0;`
  (`stats` = the world's `globalStats`, found once in the constructor as `own` is).
- [ ] **Step 2:** Verify the native halving's write is captured: run `--check` of the TouchGrass route to a tick after a
  comp boost expires with `--ticks-out`, add `npBoost` to the row writer (`FullTas.cs` near :215) and confirm it halves.
- [ ] **Step 3: Regression gate; commit** `"Economy: the comp boost doubles NP (TryActivateCompBoost)"`.

### Task 4: NP income, the NP/atom/other global boxes

**Files:**
- Modify: `enginesim/Runtime/Economy.cs` — new `FixedStep()`, `RecalcNp()`, `BoughtGlobal` :534
- Modify: `enginesim/Runtime/Shop.cs:279-284` (the `known` list) and `Shop.FixedStep` (call `Economy.FixedStep()`)
- Reference: `b640raw/globalStats.cs:1100-1121` (`DoConstantCurrency`), `:1154-1159` (`RecalcNpRewardPerTick`),
  `upgradeBox.cs:698-780` (`DoBuyUpgrade`'s switch)

**Interfaces:**
- Produces: `Economy.FixedStep()` (once per physics tick, after the step), `Economy.RecalcNp()`, fields `_npTick`,
  `_npCap`, `_effectiveAtoms`, `_whiteAtoms`, `_grossAtoms` in `State`.

- [ ] **Step 1: Port `RecalcNpRewardPerTick` and `DoConstantCurrency`.**

```csharp
internal void RecalcNp()
{
    var e = globalStats.globalUpgradeEffectDict;
    _npTick = _effectiveAtoms * (1.0 + _whiteAtoms / 60.0)
        * (1.0 + Global(G.increasedNuclearPower) * (e[G.increasedNuclearPower] / 100.0) + Global(G.tripleThreatIncrease) * (e[G.tripleThreatIncrease] / 100.0));
    _npCap = _npTick * 50.0 * 120.0 * _vmanMult;
}

internal void FixedStep()
{
    double np = _sim.Shop.Holds(globalStats.Currencies.AtomicPower), add;
    double rate = _npTick * (1.0 + Global(G.NpGenerationSpeed)) * _gameSpeed;
    if (np < _npCap) add = rate;
    else if (Global(G.NpCapIsSoftcap) != 0.0) add = rate * Math.Pow(_npCap / np, 3.0);
    else add = 0.0;
    if (Global(G.compBoostDoublesNP) != 0.0) add *= _stats.EffectOfNpCompBoost;
    Add(globalStats.Currencies.AtomicPower, add);
}
```

  (`G` = `using G = globalStats.globalUpgradeSet;`). Order inside the tick: the game's `globalStats.FixedUpdate` runs in
  the script execution order of the physics step; put `FixedStep()` where `Shop.FixedStep()` runs (Simulation.cs:1381)
  and confirm the order in Task 7 against the probe.
- [ ] **Step 2: `BoughtGlobal` follows `DoBuyUpgrade`'s switch** (upgradeBox.cs:715-760): `atomLevelChance` (+effectMult,
  then `ClearAtoms()` — Task 5), `increasedNuclearPower`/`tripleThreatIncrease` (+effectMult, `RecalcNp()`),
  `RPcap`/`MoreRpCap` (+effectMult, recompute rp cap as `UpdateRpCap`, globalStats.cs:1138), `exemptCourseFromAtomPrestige`
  (`courseToExemptReference.exemptFromAtomPrestige = true`, touched), `makeCourseBuyMax` (`EnableBuyMax()` on the course:
  check courseScript.cs:711 and port its effect on its boxes' `buyMax`), `IncreasedCloneDust` and `default` (+effectMult);
  after any change to a reward input (`increasedWatts`, `moreWatts`, `wattPower`, `increasedGreenPower`, `moreGP`,
  `increasedRP`, `moreRP`, `IncreasedCloneDust`, `tripleThreatIncrease`), `UpdateReward` every course (the game's courses
  re-read them on their next `fullUpdate`; find that call site in courseScript.cs and match its timing).
  Keep `Unmodeled` only for `startVmanSpeedrun`, `openGate`, `area2DoorOpen`, `unlockTeleporters`, `blueCloneChance`/
  `redCloneChance`'s first buy (`visible` side effect: port it if simple) — each with the game line it would need.
- [ ] **Step 3: Shop's `known` list** becomes "everything `BoughtGlobal` doesn't refuse": delete the list and let
  `BoughtGlobal`'s return decide (the abort message comes from `Unmodeled`).
- [ ] **Step 4: Regression gate; commit** `"Economy: NP income and cap, the NP/RP/atom-level/exempt/buy-max global boxes"`.

### Task 5: Atoms

**Files:**
- Modify: `enginesim/Runtime/Economy.cs` — new region `// Atoms (AtomColliderSystem)`; `BoughtGlobal`'s `spawnNewAtom`
  (:539-541) becomes `_waitingAtoms++`
- Modify: `enginesim/Runtime/Shop.cs` `StepClones` (per rendered frame: call `Economy.AtomFrame(dt)`)
- Reference: `b640raw/AtomColliderSystem.cs` (whole file, quoted in this plan's research; key lines 111-138, 179-211,
  213-265, 278-332, 140-172)

**Interfaces:**
- Produces: `Economy.AtomTick()` (the 0.4 s `InvokeRepeating` grid: called by the scheduler — see Step 2),
  `Economy.AtomFrame(float dt)` (`Update`'s `HandleTierLogic` round robin), `Economy.ClearAtoms()`; state
  `int[] _atoms` (count per tier), `int _waitingAtoms`, `double _tierUpCounter`, `bool[] _crafting`, `float[] _craftProgress`,
  `bool _isCrafting`, `int _handledTier`.

- [ ] **Step 1: State and arithmetic.** Port `determineAtomTier`, `AddNewAtom`'s counts (`gross += 1 + T(T-1)`,
  `effective += (float)Mathf.Pow(100 + 10T, T) * 0.001f`, `white += Factorial(2+T)/2`, then `RecalcNp()`),
  `startCrafting`, `FinishCrafting` (moves 3+T atoms of tier T to one of T+1, the counts as :297-312, NP
  `+= _npTick * 1000` on a new tier), `ClearAtoms` (everything 0, `RecalcNp()`, the atom box's `onPrestige(force: true)`
  — call the game method on the live box, touched — and `TimesUsed++`). Read `craftingProgressPerStep`,
  `craftingProgressPerStepPerTier`, `craftingStartPoint`, `startPointPerTier`, `craftingEndValue` from the world's
  `AtomColliderSystem` by reflection once. Keep float arithmetic where the game uses float (progress, `Mathf.Pow`).
- [ ] **Step 2: The 0.4 s grid.** `OnEnable` starts `InvokeRepeating("callAddNewAtom", 0, 0.4)`. The engine's scheduler
  runs Invokes (`Runtime/Scheduler.cs`); add `AtomColliderSystem::callAddNewAtom` as a `replace` hook in `hooks.txt` calling
  `Hooks.OnAtomTick` → `Economy.AtomTick()` (one atom: AtomicPower +1, tier, add, `_waitingAtoms--`), so the game's own
  Invoke timing is used. If the collider's `OnEnable` never runs in the engine (it's in zone 2; zone streaming is cut,
  `cuts.txt:15-16`), find out when 1x enables it (zone 2 loaded) and enable it at the same moment; document what you
  find in `docs/fidelity-log.md`.
- [ ] **Step 3: Crafting per rendered frame.** `Update` handles one tier a frame (`currentlyHandledTier` round robin) with
  `Time.deltaTime` = the frame's step; call `AtomFrame(dt)` from `Shop.StepClones` (every rendered frame, the dash
  freeze's included) only while the collider is enabled.
- [ ] **Step 4: `Capture`/`Restore`/`AddTo`** carry every atom field (arrays cloned).
- [ ] **Step 5: Regression gate; commit** `"Economy: atoms (AtomColliderSystem: 0.4 s landing, tiers, crafting, first-merge NP, ClearAtoms)"`.

### Task 6: The tree grows as the game grows it

**Files:**
- Modify: `enginesim/Runtime/Shop.cs:222-241` (`GrowTree`)
- Modify: `enginesim/Runtime/Scheduler.cs` / `Simulation.cs:1394` (`Wait.EndOfFrame` resumes after every rendered frame,
  as `Shop.EndOfFrame` already does for buy-max: `Simulation.InterpolatedFrames`)
- Modify (if needed): `enginesim/Shim/` (no-op stubs for presentation APIs `IE_Grow` touches: `SpriteRenderer.bounds`,
  `Renderer.material.SetFloat`, `Tilemap.color`, `Light2D.enabled`), or `enginesim/cuts.txt`
- Reference: `b640raw/TreeController.cs` (`onNewGrowCommandRecieved`), `TreeSegmentActivationComponent.cs` (`IE_Grow`)

- [ ] **Step 1:** `GrowTree` calls the game's `tree.onNewGrowCommandRecieved(segment)` for each segment (the audio draw is
  already the `Audio::PlayAudio` hook; delete the hand-made draws), so `IE_Grow` runs as a coroutine: trunk(s) at 0.6/s,
  the land part's collider off then on after 1/3.5 s, the box 2000 units up lerping down at 3/s, `autoTriggerNext`.
- [ ] **Step 2:** `WaitForEndOfFrame` resumes once per rendered frame with that frame's `Time.deltaTime`. Check the
  other `WaitForEndOfFrame` users (`grep -rn WaitForEndOfFrame C:\tmp\igtap\b640raw`) and list them in the commit body;
  the regression gate decides whether any route changed.
- [ ] **Step 3:** Collider and `SetActive` changes go through the engine's tracked paths (`Engine.EnabledChanging`,
  `Simulation.Touch`) so a restore puts the tree back; add the tree segment objects to what `Capture` covers if not.
- [ ] **Step 4: Regression gate; commit** `"Tree growth runs the game's IE_Grow (end of frame per rendered frame)"`.

### Task 7: Check Area 2 against 1x

**Files:**
- Modify: `ProbeRunner.cs` (probe rows: every currency, NP rate/cap, atoms per tier, waiting atoms, tree segments grown)
- Modify: `enginesim/Cli/FullTas.cs` `--ticks-out` row writer (the same fields)
- Create: `tools/economy-diff.py` (compare each field as the sequence of values it takes)
- Modify: `docs/fidelity-log.md`, `enginesim/README.md` (Fidelity facts), `docs/fulltas.md` "Offline economy"

- [ ] **Step 1: Write `tools/economy-diff.py`.**

```python
"""Compare a 1x probe's currency rows with `fulltas --check --ticks-out` rows, field by field, as value sequences.
python tools/economy-diff.py <probe.jsonl> <ticks-out.jsonl> [--fields cash,gp,np,cd,rp,atoms]"""
import json, sys, argparse

def seq(path, field):
    out = []
    for line in open(path, encoding="utf-8"):
        r = json.loads(line)
        if r.get("type", "tick") != "tick" or field not in r: continue
        v = r[field]
        if not out or out[-1] != v: out.append(v)
    return out

def close(a, b):
    if isinstance(a, list): return len(a) == len(b) and all(close(x, y) for x, y in zip(a, b))
    return a == b or abs(a - b) <= 1e-12 * max(abs(a), abs(b))

def main():
    ap = argparse.ArgumentParser(); ap.add_argument("probe"); ap.add_argument("offline")
    ap.add_argument("--fields", default="cash,gp,np,cd,rp,atoms"); a = ap.parse_args()
    bad = 0
    for f in a.fields.split(","):
        p, o = seq(a.probe, f), seq(a.offline, f)
        i = next((k for k, (x, y) in enumerate(zip(p, o)) if not close(x, y)), None)
        if i is None and len(p) == len(o): print(f"{f}: {len(p)} values, equal"); continue
        bad += 1
        i = min(len(p), len(o)) if i is None else i
        print(f"{f}: first difference at value {i}: 1x {p[i] if i < len(p) else 'end'} offline {o[i] if i < len(o) else 'end'}")
    sys.exit(1 if bad else 0)

if __name__ == "__main__":
    assert close(1.0, 1.0 + 1e-13) and not close(1.0, 1.0001) and close([1, 2], [1, 2])
    main()
```

- [ ] **Step 2:** Add the fields to both row writers (same names: `cash, gp, np, cd, rp, atoms` [per-tier counts],
  `waiting`, `npTick`, `npCap`, `segments`).
- [ ] **Step 3 (game):** probe the TouchGrass-ANY% best through its Area 2 stretch (`python tools/probe.py`, see
  `enginesim/README.md#probe`) and capture any trace the engine lacks (`tools/enginesim-capture.py`). Then
  `& $exe fulltas <trace> --check <route> --ticks-out C:\tmp\igtap\tg-offline.jsonl` and run the diff.
  Expected: every field equal. Each difference found is a Phase A bug: fix it in the task that owns it, re-run.
- [ ] **Step 4: Restore fidelity.** `--check` the same route with snapshots taken and restored mid-Area-2 (the
  engine's existing snapshot replay path, e.g. `fulltas --check` from a `SimSnapshot` at goal k) equals the straight replay.
- [ ] **Step 5:** Fidelity-log entry (symptom/cause/fix/where, as the others), docs updated; regression gate; commit
  `"Area 2 economy checked against 1x (tools/economy-diff.py)"`.

## Phase B — The planner

### Task 8: Drive the economy without the player

**Files:**
- Create: `enginesim/Runtime/EconomyDrive.cs` (partial `Simulation`)

**Interfaces:**
- Consumes: Phase A's `Economy`/`Shop`.
- Produces (all on `Simulation`):
  - `void EconomyIdle(int ticks, Vector2 at)` — `ticks` physics ticks with the player standing at `at` (no physics step,
    no input): per tick the course FixedUpdates, `Shop.TickCooldowns`, `Shop.FixedStep` (NP, buy-max), the scheduler's
    FixedUpdate/Frame/EndOfFrame waits and Invokes, and per rendered frame of the frame grid `Shop.StepClones` and
    `AtomFrame`; on-screen state per course from whether `at` is inside its `clonesLoD` trigger
    (`Physics2D.OverlapPoint`-equivalent query on the engine's world).
  - `bool EconomyBuy(string boxId, Vector2 at)` — one touch of the box as `upgradeBox.OnTriggerStay2D` would make it now
    (cooldown and affordability as the hook checks; buy-max starts its routine); false when it can't buy.
  - `void EconomyFinish(int course, float time)` — the course's `stopTracking` economy (`Economy.CourseFinished`).
  A quick restart has no economy side: the planner only moves its place.

- [ ] **Step 1: Self-check that idle matches a real idle.** Add `IGTAP.EngineSim plan-check <trace> <route> --at <goal>`
  (Program.cs): replay the route to the goal, snapshot; (a) play 2000 idle inputs through the full simulation; (b) restore
  and `EconomyIdle(2000, player position)`; assert every currency, clone and atom field equal. Run it before
  implementing: fails (method missing).
- [ ] **Step 2: Implement `EconomyIdle`** by factoring the economy half out of the physics tick loop
  (Simulation.cs:1340-1400) into `EconomyTick()` that both the real tick and `EconomyIdle` call, so they can't drift.
- [ ] **Step 3:** `plan-check` passes at three goals of FalseEnding%-280 (a Course 1 lap phase, Course 2 with clones on
  screen, after `buy:buyds`) and two of TouchGrass-ANY%'s Area 2 goals.
- [ ] **Step 4:** `EconomyBuy`/`EconomyFinish` checked the same way: replay to just before a buy goal and a finish goal,
  compare the full sim's next-tick economy with the drive's.
- [ ] **Step 5: Commit** `"Simulation.EconomyIdle/Buy/Finish: the economy without the player (plan-check)"`.

### Task 9: The movement table

**Files:**
- Create: `enginesim/Cli/EconomyMoves.cs`
- Move: `ZipPlan.SavedLaps`/`RouteName` (ZipPlan.cs:1268-1300) into it

**Interfaces:**
- Produces:

```csharp
sealed record Place(string Id, string Kind, string Box, double X, double Y, string Caps);   // as the buy table's places
sealed record Move(string From, string To, int Ticks, string Source, bool Estimated);       // To: a place id, "lap:N", "reward:bonusN", "restart"
sealed class MoveTable
{
    public static MoveTable Load(FullTas run, string buyTableFile, string routesFile, Route best);
    public Move Best(string from, string to, string caps);       // null when unknown and not estimable
    public IEnumerable<string> Targets(string from, string caps); // every To with a Move from here
    public void Measured(Move m);                                 // realize feeds real ticks back
    public void Save(string file);                                // fulltas/<run>.moves.json, merged across runs
}
```

  Sources, in this order of trust: realize's measurements; the buy table's entries (walk/jump-through/dash-through:
  the cheapest per set); the best's own goal-to-goal splits (place = where it stood at the goal before); saved course
  laps (`<course>_<CAT> farm <lap>` → lap ticks from a restart; records → the clones' path); bonus reward boxes as places
  (the buy table measures them as boxes when `--targets` includes `reward:bonusN`, Task 10). An estimate (straight-line
  distance at the run's top path speed, `FullTasAuto`'s `PlayerCaps` top speed) only when none of those exists, marked
  `Estimated`.

- [ ] **Step 1: Self-check first.** `plan-check --moves <trace>` loads the FalseEnding%-280 table and asserts: the best's
  own split from `reward:bonus1` to the first `buy:buyclone2` is a Move with `Source == "best"` and its ticks; an
  unmeasured pair comes back `Estimated`. Fails before the class exists.
- [ ] **Step 2: Implement**, reading `fulltas/<run>.buytable.json` (format: `FullTasBuyTable.cs:23-33`).
- [ ] **Step 3:** Self-check passes. **Commit** `"Movement table for the economy planner (buy table, best's splits, saved laps)"`.

### Task 10: The buy table measures bonuses and laps too

**Files:**
- Modify: `enginesim/Cli/FullTasBuyTable.cs` — `Targets` :210 accepts `reward:bonusN` (the reward box as a one-box set)
  and `lap:N` (a lap from the place: gate then finish, via `FullTasPlan.Lap`/`RecordLap`), so a bonus's cost from every
  place is measured like a box's.

- [ ] **Step 1:** `fulltas-buytable <trace> --from 1 --until buy:buyds --targets reward:bonus1,lap:2` fills entries for
  bonus 1 and a Course 2 lap from every place of that phase; check two by hand against the best's own split.
- [ ] **Step 2: Commit** `"Buy table: reward boxes and laps as targets"`.

### Task 11: The search

**Files:**
- Create: `enginesim/Cli/EconomyPlan.cs`
- Modify: `enginesim/Cli/Program.cs:45` (`fulltas-plan` → `EconomyPlan.Run`), usage text :224-238

**Interfaces:**
- Consumes: `Simulation.EconomyIdle/Buy/Finish` (Task 8), `MoveTable` (Task 9).
- Produces:

```csharp
sealed record Step(string Kind, string Arg, int Ticks);  // Kind: lap | record | ladder | buy | reward | restart | target
sealed class PlanResult { public bool Done; public long Tick; public Step[] Steps; public string[] Goals; public string[] Estimated; }
static PlanResult EconomyPlan.Search(Simulation sim, SimSnapshot start, MoveTable moves, string startPlace,
                                     string[] targets, string[] bonuses, int maxStates);
```

  A node: `(tick, place, SimSnapshot)` plus a key `(place, unlocks, box levels, rewards taken)`. Expand a node by every
  step with a Move from its place: restore the snapshot, `EconomyIdle(move.Ticks - 1, path end)` then the step's event
  (`EconomyBuy` for buys/rewards — a strict buy needs the Cash on arrival, no waiting at a box; `EconomyFinish` for a lap's
  finish with the lap's course time; a record lap's time is the saved route's; a ladder of K is K record laps at T+K-1..T),
  capture. Buy steps offered: every box/set the place can pay for on arrival (`Shop.Affordable`) that some target needs
  (transitively: unlocks, the box's prerequisite tree), plus any economy box (`FullTasGoal.IsEconomy`). Reward steps:
  each `bonuses` entry not yet taken. Priority = tick + a lower bound (the target's price still missing over the best
  income any reachable state has shown, in ticks, never more than the true remaining ticks: use 0 when unsure).
  Dominance per key: drop a node if a kept one is no later and holds at least as much of every currency.

- [ ] **Step 1: A self-check without the game.** The search is generic over an `IEconomy` seam, so `plan-selftest`
  (Program.cs) runs it on a 30-line toy economy:

```csharp
interface IEconomy<TSnap> { TSnap Capture(); void Restore(TSnap s); void Idle(int ticks, string place);
                            bool Buy(string box); void Finish(string course); double[] Holds(); string Key(); }
```

  The toy: one course paying 10/lap (lap 100 ticks), a clone box costing 30 that adds 1 per tick off screen, a bonus box
  (free, reward ×2) 150 ticks away, target box costing 2000. Assert the planner's order equals a brute-force search over
  all orders of ≤ 12 steps, and that a dominated state was dropped at least once. Run: FAIL (types missing).
- [ ] **Step 2: Implement** `Search` over `IEconomy`, and `SimEconomy : IEconomy<SimSnapshot>` wrapping Task 8.
- [ ] **Step 3:** `plan-selftest` passes.
- [ ] **Step 4: The real question.** `& $exe fulltas-plan $traces\fulltas-FalseEnding%-280.json --from 1 --target buy:buyds
  --bonuses bonus1,bonus2,bonus3,bonus5 --out C:\tmp\igtap\plan-fe.json`. Print the schedule, and the three orders the
  user asked about scored side by side (`--compare "lap:2,reward:bonus1" "reward:bonus1,lap:2" "lap:2,lap:2,buy:buyclone2,reward:bonus1"`:
  play each prefix, then let the search finish; print each one's target tick).
- [ ] **Step 5: Commit** `"fulltas-plan: Dijkstra over lap/buy/bonus schedules on the engine's economy"`.

### Task 12: Realize the plan

**Files:**
- Modify: `enginesim/Cli/FullTasPlan.cs` — delete the greedy loop (`ReplanOn` :149-269), `Rate` :861-879, `Clones`/
  `ClonePay`/`Rolls` :945-994, `Ladders` :893-927, `Credited` :930-938, `HoldBackLadders` :797-830 and their fields
  (:20-40 `_credit`, `_ladderMax`, `_recordBefore`, `_planTickCap`); move `CloneIncome` :1002-1018 next to its last user
  (`FullTasAutoLib.cs:626`). Keep `Replan`'s facade, `Play`, `Borrowed*`, `Leading`, `Lap`, `RecordLap`, `SeedLaps`,
  `Harvest*`, the join tail (:277-351), `--out`/`--route` writing.
- Modify: `enginesim/Cli/EconomyPlan.cs` — `Realize(PlanResult)`: build the goal list with `Play` step by step from the
  start snapshot; each step's real ticks → `MoveTable.Measured`; when the build is more than 10 ticks off the plan at a
  step, or a strict buy strays, re-plan from that step's real state (at most 3 rounds); join the old tail (kept code),
  `Retune`; write `--out` goals and `--route` as today.

- [ ] **Step 1:** Realize the Task 11 Step 4 plan; `--check` the written route under the written goals reaches every goal.
  Expected: `done`, target tick within 10 ticks of the plan's, or a printed reason where it stopped.
- [ ] **Step 2:** If faster than the record: submit per AGENTS.md ("Before calling a new best real": trace it, say what
  changed — the bonus order), else report the numbers.
- [ ] **Step 3: Commit** `"fulltas-plan realizes its schedule with the loop builder, re-planning on measured ticks"`.

### Task 13: Area 2 through the same planner

**Files:**
- Modify: `enginesim/Cli/EconomyPlan.cs` — step kinds `atoms:N` (save the price, touch the atom box N times, Area 2's
  `spawnNewAtom`), `all:<box>` (buy-max), targets `buy:buytree16` (the zip movers)

- [ ] **Step 1:** `fulltas-plan $traces\fulltas-TouchGrass-ANY%.json --from <first Area 2 goal> --target buy:buytree16`.
  Compare with `zip-plan`'s 11.2 min plan (docs/fulltas.md "Zip planner") before ZipPlan is deleted: same or faster,
  and its build plays.
- [ ] **Step 2: Commit** `"fulltas-plan covers Area 2 (atoms, buy-max, the tree to the zip movers)"`.

## Phase C — One planner left

### Task 14: The goals part plans instead of editing

**Files:**
- Modify: `enginesim/Cli/FullTasAutoGoals.cs` — `GoalsPull` :97-243 becomes: for each economy stretch of the best (from
  an unlock buy or reward goal to the next unlock buy), `EconomyPlan.Search` from the best's state there, `Realize`,
  adopt through the existing `AdoptGoals`/`Played` when the whole run is faster. Keep `GoalEdit`, `RunLap`, `Laps`,
  `Spliced`, `Idle`, `RunOnThreads`, `GoalsOf`, `Grouped`, `GoalsRefused`, `FindCashGoals`, `Played`, `StrayFit`,
  `AdoptGoals`, `StandIn`, `SwitchGoals`, `RebindEval`, `RestartOnGoals`, `GoalsHeader`, `ResumeAdoptedGoals`.
- Modify: `FullTasAutoMemory.cs` (drop `edits`/`EditKey` :19,25,37,63; `lapSearched` :38,64 goes with record-lap search),
  `FullTasAuto.cs` (`_investing` :1159, `_repairFloorCpu` :661,1174), `FullTas.cs:1595`

- [ ] **Step 1:** `--auto --parts goals --minutes 20` on FalseEnding%-280: the pull runs the planner per stretch and logs
  each stretch's plan vs best; no 7-minute edit sweeps.
- [ ] **Step 2: Commit** `"--auto goals part: one planner pull per economy stretch"`.

### Task 15: Delete the old planners

**Files (delete):** `tools/plan_fulltas.py`, `tools/test_plan_fulltas.py`, `tools/plan-buytable.py`,
`enginesim/Cli/ZipPlan.cs`, `enginesim/Cli/zip-plan.json` (+ `IGTAP.EngineSim.csproj:25`), `enginesim/Cli/FullTasAutoLadder.cs`,
`enginesim/Cli/FullTasAutoPayout.cs`; in `FullTasAutoGoals.cs` the generators (`DropEdits`, `AddEdits`, `AddLaps`,
`AddAhead`, `Investments`, `RegroupEdits`, `CashMoves`, `CashNear`, `CashEdits`, `RecordEdits`, `InsertEdits`,
`WalkInLaps`, `OwnLaps`, `LoadCourseRoutes`, `RecordLapOf`, `LapWays`, `InsertLapOf`, `RecordLapSearch`, `RestartLap`,
`SearchRecordLap`, `SubmitCourseLap`, `Reroute`, `InvestRepair`, the edit `Replan`, `ModelOf`, `InsertGoalOption`,
`NewGoal`, `Bonuses`, `RewardInsertEdits`); `--ladder-study` (`FullTas.cs:332-340`); `Program.cs` `zip-plan` :47 and usage.

**Files (modify):** `README.md:202` (tools row), `AGENTS.md:79-80`, `docs/fulltas.md` (replace "Engine farm planner",
"Buy planner", "Zip planner", the goals part's edit list :1247-1442 and the Python mentions :413, :433, :2247, :2278,
:2388, :2560, :2567 with one "Economy planner" section: what it searches, its steps, the table, realize, the measured
result of Task 12/13), `docs/fidelity-log.md:747-763` (point at Economy.cs), `techs.json:328-330`
(`record-break-payout`'s text names the planner's record/ladder steps), `web/app/fulltas-solve.tsx:150` (lever label).

- [ ] **Step 1:** Delete; `dotnet build` both projects; `grep -rn "plan_fulltas\|plan-buytable\|ZipPlan\|zip-plan\|FullTasAutoLadder\|FullTasAutoPayout\|ladder-study"`
  outside `docs/fidelity-log.md`'s history returns nothing.
- [ ] **Step 2:** Regression gate, `plan-selftest`, `plan-check` (Task 8's goals) all pass; `python tools/validate.py` (game).
- [ ] **Step 3: Commit** `"One economy planner: delete plan_fulltas.py, plan-buytable.py, zip-plan, ladders/payout and the goals part's edits"`.
