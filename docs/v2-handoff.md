# Full-TAS v2: handoff (2026-10-10, 01:30)

Branch `solver-v2`, worktree `C:\Users\deafwave\Downloads\IGTAP\wt-solver-v2`: **not merged, not pushed**. Design:
`docs/superpowers/specs/2026-10-09-fulltas-v2-design.md`. Plan: `docs/superpowers/plans/2026-10-09-fulltas-v2.md`.
Ledger with every ruling: `.superpowers/sdd/2026-10-09-fulltas-v2/progress.md` (git-ignored, in the worktree). How it
works now: `docs/fulltas.md` "Solving (v2)".

## Results

- **TouchGrass-ANY% repaired**: it had broken at goal 407 on this engine; now complete, **saved by the gate as
  `fulltas/TouchGrass-ANY%_NONE 697.49`** (the old record was 699.12).
- **ZipEscape% improved**: its best reached 813 of 845 goals on this engine; `solve-run` built it to **830 of 845**
  (`fulltas/ZipEscape%.json`, the old file kept as `ZipEscape%.before-v2.json`). Goal 831 (a second `buy:buyatom` right after
  one) is a typed problem, `no-income`: after goal 830's buy-max touch spends the GreenPower, nothing pays it back within 1,500
  ticks. The improver is working on it now (moves: laps or income buys before it, dropping that atom buy).

## Running right now

`solve-run` on ZipEscape%: PID 53572 (`C:\tmp\igtap-v2\eng3\IGTAP.EngineSim.exe`, ~36 GB RAM, 28 threads), log
`C:\tmp\igtap-v2\ze-run2.log`, started 01:13 by `C:\tmp\igtap-v2\tools\drive.py` (sends "stop" after 20 h). It writes the
run file on every new best and submits complete faster runs to the gate. Stop it with `taskkill /PID 53572 /F` or let it run.

## Done (20 commits on top of 6b52557)

| Task | What |
|---|---|
| 1 | One-command clean build (`dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o <dir>`) |
| 2-3 | `IGTAP.EngineSim check [--accept]` (5 complete runs + 566 traces vs `enginesim/check/baseline.json`, 25-45 s) and the pre-commit hook (`python tools/setup-hooks.py`); AGENTS.md process note |
| 4 | Area 2 income boxes are economy goals (plans may add, drop, move them; the gate adopts) |
| 5-9 | `enginesim/Cli/Runs/`: plan goals, segment library, deterministic search, builder (exact replays, waits, typed failures, buy-max aware), improver (fills waits, drops/moves economy goals, keeps a change only if the straight replay is faster) |
| 10 | `fulltas <trace>` = `solve-run <trace> [--threads N] [--class C] [--segment ID]` |
| 12 | Confirm: gate splits vs the engine's; on a disagreement the game's trace is replayed to name the first tick and field |
| 13 | Studio: one **Solve** button + "Search goal N again"; the plugin launches `solve-run` for every class |
| 14 | Cutover: the economy planner, stages and old stretch search are deleted (~6,500 lines); classes on `solve-run` |

## Not done

1. **Deploy and click Solve in the game**: the plugin and web are built on this branch but not deployed
   (`python tools/enginesim-redeploy.py` from the worktree with the game closed; then the Full-TAS tab's Solve).
2. **Zip% repair** (685 goals after 807; hours): `fulltas "$CFG\fulltas-Zip%.json"`. Then `check --accept` and commit the
   baseline (TouchGrass-ANY% and ZipEscape% already show "changed since baseline" warnings).
3. **The rest of the old stack**: course Optimize's Improve (`solve --auto`, `CourseAuto.cs`, `CourseLoop.cs`) runs on the
   old `FullTas` partial class (a spec defect: the spec assumed it was Full-TAS only), so it stays until course Improve moves
   to v2.
4. **1x confirmation** (plan Task 16) and recapturing the trace corpus (428 of 566 traces already differ from their turbo
   captures, 29 no longer load).
5. **Merge**: review the branch (`git log 6b52557..solver-v2`), then merge to main.

## Open issues

- **Goal-to-goal replay vs straight replay**: on TouchGrass-ANY% the build put goal 427 (`at:...,from=top`) one tick early;
  every best is now replayed straight before it's kept (`Builder.Verify`), so nothing wrong is saved; root cause not found.
- **Ripple**: a change mid-run moves every later state, so later goals often need new searches; `--segment` caps its
  rebuild at 25 searches, the improver's searched candidates at 3 searches of 20M ticks.
- VMAN's installed route dies at action 191 (predates this branch).

## How to run

```
cd C:\Users\deafwave\Downloads\IGTAP\wt-solver-v2
dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release -o C:\tmp\igtap-v2\eng
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe fulltas "<config>\enginesim\fulltas-<run>.json" --threads 28
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe check          # before any engine commit (the hook runs it)
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe run-selftest    # unit checks; --trace T --best B --build / --improve-smoke for run tests
```
