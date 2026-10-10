# Full-TAS v2: handoff (2026-10-10)

Branch `solver-v2`, worktree `C:\Users\deafwave\Downloads\IGTAP\wt-solver-v2` (not merged, not pushed). Design:
`docs/superpowers/specs/2026-10-09-fulltas-v2-design.md`. Plan: `docs/superpowers/plans/2026-10-09-fulltas-v2.md`.
Ledger with every ruling: `.superpowers/sdd/2026-10-09-fulltas-v2/progress.md` (git-ignored, in the worktree).

## Done (15 commits on top of 6b52557)

| Task | What | Proof |
|---|---|---|
| 1 | One-command clean build (`dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o <dir>`) | fresh worktree builds |
| 2 | `IGTAP.EngineSim check [--accept]`: 5 complete runs + 566 traces against `enginesim/check/baseline.json`, 24 s | a planted skipped economy step fails it (Zip% 807 -> 461) |
| 3 | `.githooks/pre-commit` runs `check` on engine commits (`python tools/setup-hooks.py`); AGENTS.md process note | refused a planted commit in 30 s |
| 4 | Area 2 income boxes (atoms, clone dust, atom levels, free boxes, statue, buytree0) are economy goals | `run-selftest` |
| 5-8 | `enginesim/Cli/Runs/`: plan goals, segment library, deterministic segment search, the builder (exact replays, waits, typed failures) | Tree% and FalseEnding%-280 harvest and rebuild to the tick |
| 9 | The improver (fills waits with laps/buys, drops/moves economy goals, keeps a change only when the run replayed straight from the start is faster) | Tree% with a lap removed: it puts it back (44.30 -> 36.90 s) |
| 10 | `solve-run <trace> [--threads N] [--class C] [--segment ID]`: one command, stop on stdin, run file / waits / problems, gate submission | FalseEnding%-280 run, clean stop |
| 11 (half) | **TouchGrass-ANY% repaired**: complete again, 697.465 s engine, **saved by the gate as `fulltas/TouchGrass-ANY%_NONE 697.49`** (was 699.12, broken at goal 407) | gate verdict |
| 12 | Confirm: the gate's per-goal splits against the engine's; on a disagreement the game captures a trace and `replay` names the first tick and field | live round trip on TouchGrass |
| 13 | Studio: one **Solve** button (+ "Search goal N again" for one picked goal); plugin launches `solve-run`; feed shows waits, failures, confirmations | plugin + web build; **not yet deployed to the game** |
| (14 prep) | Classes on `solve-run` (`--class`): human-input mode, no major grafts/pauses for nmg, techs-used check before keeping | build + selftest |

## Running right now

`solve-run` on ZipEscape% (PID of `C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe`, log `C:\tmp\igtap-v2\ze-run.log`, started
00:57 by `C:\tmp\igtap-v2\tools\drive.py`, 28 threads, ~19 GB RAM). It built goals 813 -> 830, then **goal 831 (a second
`buy:buyatom` right after another) is not found**: 20M and 80M searches got within 0-2 u of the box without a buy; a 320M
search is running (~25 min). Likely cause: the atom box is buy-max; after goal 830's touch a new goal needs a new touch
after the buy-max routine ends. When the search fails the improver should try its moves (drop that atom buy, insert laps or
income before it). To stop it: `taskkill /PID <pid> /F` (it's mine), or let it run.

## Not done

1. **Zip% repair** (Task 11): `solve-run "$CFG\fulltas-Zip%.json"`; 685 goals after 807; hours. Then `check --accept`
   and commit the baseline (TouchGrass already shows "changed since baseline").
2. **Cutover** (Task 14): rename `solve-run` to `fulltas`, delete the economy planner (`EconomyPlan*`, `EconomyMoves`,
   `FullTasPlan`, `FullTasBuyTable`; 5,367 lines; 20 compile errors to clear in the remaining FullTas partials).
   **Spec defect:** course Optimize's Improve (`CourseAuto.cs`, `CourseLoop.cs`, `solve --auto`) runs on the old `FullTas`
   class, so the rest of the old stack stays until course Improve moves to v2.
3. **ZipEscape% improved** (Task 15, the goal): needs goal 831 solved (above), then pad 3, `buytree13`,
   `buyteleporters` (20M GP wait), pad 102, `buytree14`, `buytree16`.
4. **Studio in game**: redeploy the plugin from this branch (`python tools/enginesim-redeploy.py` from the worktree, game
   closed) and click Solve once.
5. **1x confirmation** (Task 16).

## Open issues found on the way

- **Goal-to-goal replay vs straight replay**: on TouchGrass-ANY% the build's goal-to-goal replay put goal 427
  (`at:...,from=top`) one tick earlier than a straight replay and the game. Every best is now replayed straight before it's
  kept (`Builder.Verify`) and takes those ticks, so nothing wrong is saved; root cause not found.
- A change mid-run moves every later state, so later steps often need new searches ("ripple"): `--segment` caps the rebuild
  at 25 searches; the improver caps searched candidates at 3 searches and the first budget.
- The 566-trace corpus: 428 traces already differ from their turbo captures and 29 no longer load; it needs recapturing
  (part of Task 16).
- VMAN's installed route dies at action 191 (predates this branch).

## How to run

```
cd C:\Users\deafwave\Downloads\IGTAP\wt-solver-v2
dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release -o C:\tmp\igtap-v2\eng
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe solve-run "<config>\enginesim\fulltas-<run>.json" --threads 28
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe check            # before any engine commit (the hook does it)
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe run-selftest      # unit checks; --trace/--best/--build/--improve-smoke for the run tests
```
