# Full-TAS v2: handoff (2026-10-10)

On **main** (not pushed). Design: `docs/superpowers/specs/2026-10-09-fulltas-v2-design.md`. Plans:
`docs/superpowers/plans/2026-10-09-fulltas-v2.md`, `docs/superpowers/plans/2026-10-10-course-improve-v2.md`. How it works:
`docs/fulltas.md` "Solving (v2)". Main's uncommitted old-stack edits from before the merge are on branch
`wip/old-stack-before-v2` (a stash commit).

## Results

- **TouchGrass-ANY%**: complete, saved by the gate as `fulltas/TouchGrass-ANY%_NONE 697.49` (was 699.12).
- **ZipEscape%**: goal 831 (a second atom buy) was no bug: an atom buy resets every course, so nothing earns GreenPower
  after it. `runs/ZipEscape%.json` now buys `buytree0` and `buytree6` before its one atom touch and drops the second (846
  goals, 834 reached by hand), and has the avoids drawn for `buytree7` and `at:28710` back (the old stages' 845-goal list had
  lost them). The improver alone, from the old 830-goal best, reached **840 of 849** (1554.86 s at `buy:buycd8`; private copy
  `C:\tmp\igtap-plateau\run6state\`, not installed in the config). Next frontier: goal 841 `buy:buyclonecd8`, no CloneDust
  income after the atom reset removed the clones that make it.
- **VMAN**: its 2026-09-28 route dies at action 191 because it was found on the old six-frames-a-tick grid (fidelity log).
  A private `solve-run` rebuilt it complete at 54.96 s (`C:\tmp\igtap-vman\solve3\`), not judged; the old 33.92 no longer plays.
- **Course Improve** runs on v2 (`CourseAuto.Polish`/`Reach` over `Improver.RunSegment`, scored by `RunContext.Score`);
  `enginesim/Cli/FullTas*.cs` (−13k lines) is gone but `FullTasGoalList.cs`. Acceptance: same bests as the old path in
  about half the wall time; record Improve at half the memory.

## Fixed 2026-10-10

- A goal reached in a dash/spring lock is cut after the next input (`Simulation.AdvanceToGoal`): goal-to-goal builds replay
  as straight ones (TouchGrass-ANY%'s goal 427).
- A resume keeps a press a wall touch in a dash already made (`MacroEngine.ResumeOwned`; turbo too): VMAN's builds replay straight.
- Segment searches place avoid boxes by the plan's own goal ids.
- The improver climbs `no-income` plateaus (stuck runs rank by stuck buys, then the share of the price held; failure moves
  3 goals back; frontier misses searched first) and keeps no per-miss snapshots (half the memory: the 41 GB OOM).
- `solve-run` resumes on its own best's goal list (an incomplete best's plan was lost on restart).
- Turbo compares poses exactly and draws on-but-unstarted Starts in the first frame (`TurboRunner`): the corpus recaptured
  twice, 210 of 480 traces match the engine (was 149); 81 traces of removed categories/targets/techs retired to
  `C:\tmp\igtap-corpus-backup\retired\`, full backup in `C:\tmp\igtap-corpus-backup\`.
- 1x confirmation (plan Task 16): Fast probes, the idle 1x job, `align-1x`, 1x references in `check` (offline-tested).

## Not done

1. **`check --accept`** against the recaptured corpus, and commit `enginesim/check/baseline.json` (every check prints ~563
   "changed since baseline" warnings until then).
2. **Turbo's remaining drift** (190 + 45 traces, about a float step): any `Rigidbody2D` write recomputes Box2D's sweep
   centre, and turbo writes the frame blend into the player's transform, which syncs into the body; 1x and the engine never
   write it there. The fix is a Harmony transpiler feeding `Movement` the blend directly (a rule-39 exception): owner's call.
   Also: `replay` should not compare a run's ending tick (the engine stops mid-tick at a death/finish; 1x logs no row there).
3. **Long runs**: ZipEscape% from 840/849 (install `run6state` into the config first), Zip% repair (685 after 807), VMAN.
   Keep each ≤ 12 threads; one or two at a time.
4. **1x confirmation in the game**: deploy main, then the verification steps in docs/fulltas.md / the Task 16 notes (Fast
   leaves the sim unchanged, a 1x job writes a reference, the aligner's held/body offsets across dash freezes and restarts,
   the job yields to commands).
5. Stale: `validate.py` fails `bonus7_*_ZIP_OM 4.62` (don't finish at 1x either); 4 tech demos can't capture (their
   category or lab data is gone).

## Open issues

- **Ripple**: a change mid-run moves every later state, so later goals often need new searches.
- Course polish converges fast (minutes) and Improve exits early; budgets are `ponytail:`-capped in `CourseAuto`.

## How to run

```
dotnet build enginesim\Cli\IGTAP.EngineSim.csproj -c Release -o C:\tmp\igtap-v2\eng
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe fulltas "<config>\enginesim\fulltas-<run>.json" --threads 12
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe check          # before any engine commit (the hook runs it)
C:\tmp\igtap-v2\eng\IGTAP.EngineSim.exe run-selftest    # unit checks; --trace T --best B --build / --improve-smoke for run tests
```
