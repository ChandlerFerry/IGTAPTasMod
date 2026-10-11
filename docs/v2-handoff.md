# Full-TAS v2: handoff (2026-10-10)

On **main** and origin over 6b52557 (tag `pre-v2`): v2 as one commit (1239a2d; its 59 commits one by one at tag
`v2-2026-10-10`), then the polish fixes (5cb9a2e) and window polish (3c65290). Revert those three to go back. Design:
`docs/superpowers/specs/2026-10-09-fulltas-v2-design.md`. How it works: `docs/fulltas.md` "Solving (v2)". Main's
pre-merge uncommitted old-stack edits (to files v2 deleted): branch `wip/old-stack-before-v2`, also `stash@{0}`.

Since 17:40: the movement polish keeps the best's own way on after a faster segment (`Improver.TailRejoin`); before it,
every polish rebuild was slower and Solve stopped "converged" at 43.94. VMAN 43.94 -> 42.94 in 7 minutes on 8 threads,
saved by the gate. The solver now logs and shows a submission the game refuses (it retried 30 times in silence).
Then: the segment library keeps a segment per start (it refused the best's own later segments from their new starts, so
every polish rebuild fell back to slower ones), the best's own segment seeds every polish search, and a polish rebuilds
from the library alone (its 25 later-goal searches took minutes and never won): VMAN 42.94 -> 41.38 in 19 minutes.

## Scoreboard (judged by the gate on today's engine)

| Run | Before v2 (stale: invalidated by the fidelity fixes) | Now |
|---|---|---|
| TouchGrass-ANY% | 699.29 | **697.49** |
| FalseEnding%-280 | 352.78 | 353.29 |
| VMAN | 33.92 was ticks only: **41.67 live** (and it dies at action 191 today) | **41.38** (1718 ticks; 54.96 this morning) |
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
2. VMAN: 41.38 s, 1718 ticks, under the old route's 41.67 live; 22 ticks from its 1696. Polish now also searches windows
   of 2, 3 and 5 goals scored at their last (the old route's multi-goal ways: goals 13-18's down-dash chain, Courses 1-2's
   Clone buys); checked by run-selftest (`--own-at 12 --span 5` on VMAN), not yet in a solve: press Solve on VMAN and watch
   for "goals N-M polished".
3. Engine-side residuals the 1x probes name (resting-contact y velocity ~1e-6, 50 traces; go5/rand-c4/seed-c3): the engine needs the
   start's contact impulses. T5 (bonus10 tick 0) is Box2D warm-start state Unity can't set.
4. 1x references for ZipEscape%/TouchGrass-ANY% need a window with no gate submissions; stale bonus7 4.62 records need retiring
   and re-solving (they don't finish at 1x).
5. The other runs (Tree%, FalseEnding%-280, TouchGrass-ANY%, Zip%) haven't been solved since the polish fixes.
