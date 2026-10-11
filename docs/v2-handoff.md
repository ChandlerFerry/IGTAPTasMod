# Full-TAS v2: handoff (2026-10-10)

On **main** (not pushed). Design: `docs/superpowers/specs/2026-10-09-fulltas-v2-design.md`. Plans:
`docs/superpowers/plans/2026-10-09-fulltas-v2.md`, `docs/superpowers/plans/2026-10-10-course-improve-v2.md`. How it works:
`docs/fulltas.md` "Solving (v2)". Main's uncommitted old-stack edits from before the merge are on branch
`wip/old-stack-before-v2` (a stash commit).

## Results (2026-10-10 17:15, all judged by the gate on today's engine)

| Run | Before v2 (stale: invalidated by the fidelity fixes) | Now |
|---|---|---|
| TouchGrass-ANY% | 699.29 | **697.49** |
| FalseEnding%-280 | 352.78 | 353.29 |
| VMAN | 33.92 was ticks only: **41.67 live** (the old route on its own engine; it dies at action 191 today) | **43.94** (rejoin repair of the old route, then movement polish; 54.96 rebuilt from scratch this morning) |
| Tree% | 725.62 for 456 goals | 763.73 for 461: the 5 goals added since are income-bound (buyfree6c waits ~15 s); at goal 456 it is 727.88, and the old prefix's 2.26 s lead doesn't survive the waits |
| ZipEscape% | its old route reached 974 of 979, stopping before buytree13 | **847 of 851** (offline), past buytree13: further than any route before |
| Zip% | 861 of 865 (an older goal list) | 809 of 1494, building on (about a goal a minute) |

ZipEscape%'s next goal, `buy:buyteleporters` (Course 9's local `EnableTeleporters`), costs about 2E+07 GreenPower after the atom
reset, against about 100-200 GreenPower per 1000 ticks from every producer in reach. No goal list of this run (the old 979 or
today's) ever planned a GreenPower economy for it: the run definition needs one (Course 9's clones, or the teleporters before
the atom reset). The income moves say so ("needs a GreenPower economy built before it") instead of climbing by slivers.

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
