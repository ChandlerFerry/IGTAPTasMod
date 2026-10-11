# Full-TAS v2: handoff (2026-10-10, 17:40)

One commit on **main** over 6b52557 (tag `pre-v2`; not pushed): `git revert` it to go back. Its 59 commits one by one: tag
`v2-2026-10-10`. Design: `docs/superpowers/specs/2026-10-09-fulltas-v2-design.md`. How it works: `docs/fulltas.md`
"Solving (v2)". Main's pre-merge uncommitted old-stack edits: branch `wip/old-stack-before-v2`.

Since 17:40: the movement polish keeps the best's own way on after a faster segment (`Improver.TailRejoin`); before it,
every polish rebuild was slower and Solve stopped "converged" at 43.94. VMAN 43.94 -> 42.94 in 7 minutes on 8 threads,
saved by the gate. The solver now logs and shows a submission the game refuses (it retried 30 times in silence).

## Scoreboard (judged by the gate on today's engine)

| Run | Before v2 (stale: invalidated by the fidelity fixes) | Now |
|---|---|---|
| TouchGrass-ANY% | 699.29 | **697.49** |
| FalseEnding%-280 | 352.78 | 353.29 |
| VMAN | 33.92 was ticks only: **41.67 live** (and it dies at action 191 today) | **42.94** (54.96 this morning) |
| Tree% | 725.62 for 456 goals | 763.73 for 461 (5 goals added since, income-bound; at goal 456: 727.88) |
| ZipEscape% | old route stopped before goal 975 of 979 | **847 of 851** offline, past buytree13 (further than ever); not complete |
| Zip% | 861 of 865 (older goal list) | 809+ of 1494, building (~1 goal/min) |

**ZipEscape% needs a route decision:** `buy:buyteleporters` (Course 9's `EnableTeleporters`) costs ~2E+07 GreenPower after the atom
reset; everything in reach pays 100-200 per 1000 ticks. No goal list ever planned that economy (Course 9's clones, or the
teleporters before the atom reset). The 847/851 state: `C:\tmp\igtap-income\enginesim\fulltas\ZipEscape%.json` (installed in the config).

## What improved today

Solver (enginesim/Cli/Runs):
- **Rejoin repair** (`Rejoin.cs`, the deleted old transplant ported): a broken route keeps its inputs, patched at each break until
  it rejoins its own path (`--repair FILE`, `--repair-path`). VMAN 54.96 -> 47.91 from the old route's inputs.
- **Movement polish**: when economy moves converge, each goal's segment is searched again (longest first) with a full rebuild after
  it (up to 25 searches), escalating 20M -> 80M ticks, searching on for a faster way (stall = a quarter of the budget). VMAN 47.91 -> 43.94.
- **Income-aware moves** (`Income.cs`): on `no-income`, insert what actually pays the currency (economy-only projection); skip
  producers that can't cover the shortfall in 120k ticks; a stuck run ranks higher only for a full point of the price. ZipEscape% 840 -> 847.
- Stuck runs climb plateaus; frontier misses searched first; per-miss snapshots dropped (half the memory, the 41 GB OOM).
- Goal-to-goal builds = straight replays (lock-tick capture; resume keeps an early press); avoids placed by plan ids; `solve-run`
  resumes on its own goal list and adopts a route's stray economy buys (Zip% 807 -> 809); `--threads` really bounds searches.
- Course Improve runs on v2; the old `FullTas` stack is deleted (-13k lines); same bests in about half the time.

Fidelity / tooling:
- Turbo matches 1x: exact pose compares, first-frame Starts, and frames read the player at the blend (README exception 14):
  417 of 480 corpus traces identical to the engine (was 149). Corpus recaptured, 81 dead traces retired (backups in `C:\tmp\igtap-corpus-backup*`).
- `replay` skips the ending tick; `check` baseline clean (488 compared, 0 differ); pre-commit hook stamps the right commit.
- 1x confirmation (Task 16): Fast probes, idle 1x job, `align-1x` (FalseEnding%-280 aligns on all 16973 ticks), 1x refs in `check`.
- techs.json: 4 broken demos fixed; probe's first `cashPre` fixed.

## Running now

Nothing. Solve on the Full-TAS page (or `solve-run <config>\enginesim\fulltas-<run>.json --threads 8`) resumes each run
from its saved best; logs of the last runs in `C:\tmp\igtap-long\logs\` (VMAN: `vman7.log`).

## Open

1. ZipEscape%: the GreenPower economy for teleporters (owner's route call, above).
2. VMAN: 1.3 s to the old route's 41.67; most of the loss is goals 2-5 (Course 1 to Course 2). Solve goes on from 42.94.
3. Engine-side residuals the 1x probes name (resting-contact y velocity ~1e-6, 50 traces; go5/rand-c4/seed-c3): the engine needs the
   start's contact impulses. T5 (bonus10 tick 0) is Box2D warm-start state Unity can't set.
4. 1x references for ZipEscape%/TouchGrass-ANY% need a window with no gate submissions; stale bonus7 4.62 records need retiring
   and re-solving (they don't finish at 1x).
5. Push to origin.
