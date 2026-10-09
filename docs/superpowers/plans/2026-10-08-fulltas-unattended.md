# Full-TAS economy stretches, unattended: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `IGTAP.EngineSim fulltas <trace>` with no flags carries a run's economy goals to its last goal, or to a stop
that names its blocker, deterministically and unattended.

**Architecture:**
- The movement table becomes a ledger: what realize failed to build is saved as a fact and never planned again.
- Every step gets one fixed budget of simulated ticks for all of its searches.
- Piece searches in this mode run as independent single-worker solvers with fixed seeds, merged in index order.
- The planner is capped by states, not seconds.
- The stage loop moves behind `fulltas`, which writes a report. `fulltas-plan` keeps only `--try`.

**Tech Stack:** C# / .NET 10 (`enginesim/Cli`, `enginesim/Runtime`), `dotnet build`, the engine's own `plan-selftest`.

**Spec:** `docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md`

## Global Constraints

- README `### Rules` hold unchanged; Zip% keeps strict buys (rule 38). No game-judged search loops (rule 47).
- The new behaviour is opt-in through `FullTas._unattended`. With it off (every existing caller: the studio's solve,
  `--auto`, the goals pull, course Improve), budgets, solvers and output stay exactly as today.
- Failed facts are keyed `(from place, to, unlocks, tree group)` and dropped when the engine build changes (the Runtime
  assembly's MVID).
- Budgets are simulated ticks, never minutes: `PieceTicks` = 20,000,000 per search, `StepTicks` = 2 × `PieceTicks` per
  step, `StallTicks` = 2,000,000; `SearchWorkers` = 16 independent single-worker solvers, seeds 1..16;
  `ArchiveBytes` = 1 GiB per solver; planner `BeamStates` = 1,000,000 states played; drift re-plan past 300 ticks, once
  per goal position.
- Build to a private folder: `dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o C:\tmp\igtap\zip\engNN`.
  Never touch other agents' engine processes; stop your own by PID.
- Every engine command in a check runs under `timeout 300` (or a larger timeout only where the step says so).
- Other agents have uncommitted edits in `enginesim/Runtime/OfflineSolver.cs`, `Planner.cs`, `Simulation.cs`,
  `CourseLoop.cs` and `Program.cs`. Commit only your own hunks there:
  `git diff -U0 <file> > my.patch`, trim the patch to your hunks, then `git apply --cached my.patch`.
- `enginesim/Cli/FullTas.cs` is LF: edit with the Edit tool, or Python with `newline=''`.
- Commit after each task. End commit messages with `Co-Authored-By: Claude <noreply@anthropic.com>`.

## Review Focus

1. A failure recorded under a place the planner doesn't plan from (a step after a quick restart) would let the same step
   be planned again forever. Task 2 records the failure under every place the step started from, and stops when a
   failure teaches nothing new.
2. A move that played once and later failed at the same key: the failure wins, and `Chain` must not route through it
   (Task 1, tested).
3. Non-unattended callers changing behaviour. Task 3 and Task 4 check that `_unattended == false` keeps today's code path
   (the self-test still passes, and `--try` on the old path still prints the same lines).
4. The search's shared `FullTas` state (graft tallies, `_findModes`, visits) mutated from parallel solvers in an order
   that depends on timing. Task 4 processes finds after all solvers end, in index order, and checks determinism twice
   under load.
5. The Area 2 offer set offering a box whose touch is an unlock: harmless under the rules (rule 15), but each purchase is
   a goal. Task 6 asserts the set's contents in the self-test.

---

### Task 1: Failed facts in the movement table

**Files:**
- Modify: `enginesim/Cli/EconomyMoves.cs` (`MoveTable`: fields near line 60-80, `Load` line 84-98, `Blocked`/`Best`
  lines 100-111, `Save` 147-174, `Read` 177-192, `Chain` 326-347)
- Test: `enginesim/Cli/EconomyPlanCheck.cs` (`Run`, after the tree-transition check at line 122-125)

**Interfaces:**
- Produces:
  - `internal bool MoveTable.Failed(string from, string to, string caps, string why)`: true when this is a new fact.
  - `internal bool MoveTable.HasFailed(string from, string to, string caps)`.
  - `internal IEnumerable<(string from, string to, string caps, int tree, string why)> MoveTable.Failures`.
  - `internal static readonly string MoveTable.Build`.
  - `Best` returns null for a failed move.

- [ ] **Step 1: Write the failing test** (in `EconomyPlanCheck.Run`, after the `TreeLevelPlays` check):

```csharp
            // The ledger (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, section 1): a failed move is a fact at its
            // key, never offered again, saved, and dropped by another engine build.
            {
                var ledger = new MoveTable();
                ledger.AtTreeLevel(7);
                ledger.Measured(new Move("a", "b", 100, "measured", false), "wj");
                ledger.Measured(new Move("b", "c", 50, "measured", false), "wj");
                Check(ledger.Best("a", "b", "wj")?.Ticks == 100, "a played move is offered");
                Check(ledger.Failed("a", "b", "wj", "no way") && !ledger.Failed("a", "b", "wj", "again"), "a failure is a new fact once");
                Check(ledger.Best("a", "b", "wj") == null, "a failed move is never offered");
                Check(ledger.Chain("a", "c", "wj") < 0, "no chain runs through a failed move");
                ledger.AtTreeLevel(11);
                Check(ledger.Best("a", "b", "wj")?.Ticks == 100, "a failure holds only at its tree group");
                ledger.AtTreeLevel(7);
                string file = Path.Combine(Path.GetTempPath(), "plan-selftest-ledger.json");
                File.Delete(file);
                ledger.Save(file);
                MoveTable back = MoveTable.Read(file);
                back.AtTreeLevel(7);
                Check(back.Best("a", "b", "wj") == null && back.Failures.Count() == 1, "Save then Read keeps the failure");
                ledger.Save(file);
                Check(MoveTable.Read(file).Failures.Count() == 1, "a second Save keeps one failure, not two");
                File.WriteAllText(file, File.ReadAllText(file).Replace(MoveTable.Build, "another-build"));
                Check(!MoveTable.Read(file).Failures.Any(), "a failure from another engine build is dropped");
                File.Delete(file);
            }
```

- [ ] **Step 2: Build and run the test, and confirm it fails to compile**

Run:
`dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o 'C:\tmp\igtap\zip\eng61' 2>&1 | grep -E " error " | head`
Expected: errors that `Failed`, `Failures`, `Build` and `HasFailed` are not defined.

- [ ] **Step 3: Implement.**

In `MoveTable`, add next to `_misses`:

```csharp
        /// <summary>Moves realize tried from a real state and couldn't build, by (from, to, unlocks, tree group), with why: never
        /// offered again on this engine build (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, section 1).</summary>
        readonly Dictionary<(string from, string to, string caps, int tree), string> _failed = new Dictionary<(string, string, string, int), string>();
        /// <summary>This engine build (the Runtime assembly's MVID): a failure from another build is dropped on Read.</summary>
        internal static readonly string Build = typeof(Simulation).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        int Group => Math.Max(0, _treeGroup);

        /// <summary>Records that the move doesn't play here; true when it is a new fact.</summary>
        internal bool Failed(string from, string to, string caps, string why)
        {
            if (!_failed.TryAdd((from, to, caps, Group), why)) return false;
            _chains.Clear();
            return true;
        }

        internal bool HasFailed(string from, string to, string caps) => _failed.ContainsKey((from, to, caps, Group));

        internal IEnumerable<(string from, string to, string caps, int tree, string why)> Failures =>
            _failed.OrderBy(kv => kv.Key.from, StringComparer.Ordinal).ThenBy(kv => kv.Key.to, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.caps, StringComparer.Ordinal).ThenBy(kv => kv.Key.tree).Select(kv => (kv.Key.from, kv.Key.to, kv.Key.caps, kv.Key.tree, kv.Value));
```

Delete `Blocked` (its field and summary, lines 100-102). In `Best`, replace `if (Blocked.Contains(to)) return null;` with:

```csharp
            if (HasFailed(from, to, caps)) return null;
```

In `Chain`, change the skip test to:

```csharp
                        if (m.Estimated || end == null || HasFailed(at, next, caps) || dist.TryGetValue(end, out int was) && was <= d + m.Ticks) continue;
```

In `Save`, inside `if (File.Exists(file))`, after the measured moves merge, add:

```csharp
                foreach (var f in old.Failures) _failed.TryAdd((f.from, f.to, f.caps, f.tree), f.why);
```

Then add to the JSON object, after `["moves"] = ...`:

```csharp
                ["failed"] = new JsonArray(Failures.Select(f => (JsonNode)new JsonObject
                {
                    ["from"] = f.from, ["to"] = f.to, ["caps"] = f.caps, ["tree"] = f.tree, ["why"] = f.why, ["build"] = Build,
                }).ToArray()),
```

In `Read`, after the moves loop:

```csharp
            foreach (JsonNode f in json["failed"]?.AsArray() ?? new JsonArray())
                if (f["build"]?.GetValue<string>() == Build)
                    table._failed[(f["from"].GetValue<string>(), f["to"].GetValue<string>(), f["caps"].GetValue<string>(), f["tree"].GetValue<int>())] = f["why"].GetValue<string>();
```

In `Load`, replace the measured-moves block with:

```csharp
            if (File.Exists(movesFile))
            {
                MoveTable saved = Read(movesFile);
                foreach (var kv in saved._moves)
                    if (kv.Value.Source == "measured") table.Put(kv.Value, kv.Key.caps);
                foreach (var f in saved.Failures) table._failed[(f.from, f.to, f.caps, f.tree)] = f.why;
            }
```

- [ ] **Step 4: Fix the `Blocked` users in `EconomyPlan.cs`.** Lines 1709-1722 and 1753-1754 fail to compile. Leave them
  to Task 2: delete only these lines now, so the build passes:
  - `moves.Blocked.Add(...)` and its `Log`;
  - the whole `else if (course > 0 && ...)` block;
  - `moves.Blocked.Clear();` and the `if` that guards it.

- [ ] **Step 5: Build and run the test**

Run:
`dotnet build enginesim/Cli/IGTAP.EngineSim.csproj -c Release -o 'C:\tmp\igtap\zip\eng61' 2>&1 | grep -E " error |Error\(s\)"; cd /c/tmp && timeout 300 'C:\tmp\igtap\zip\eng61\IGTAP.EngineSim.exe' plan-selftest | grep -E "FAIL|ledger|failure|failed|plan-selftest"`
Expected: `0 Error(s)`; every new line `PASS`; `plan-selftest: PASS`.

- [ ] **Step 6: Commit**

```bash
git add enginesim/Cli/EconomyMoves.cs enginesim/Cli/EconomyPlanCheck.cs enginesim/Cli/EconomyPlan.cs
git commit -m "Movement table: failed moves are saved facts, never offered again on this engine build"
```

---

### Task 2: Realize records every failure and plans again until nothing new is learned

**Files:**
- Modify: `enginesim/Cli/EconomyPlan.cs`: `Realize` (lines 1620-1770), the fields `_maxRounds`/`_offTicks`
  (line 1597), and the option parse of `--rounds`/`--off-ticks` (around line 1327)

**Interfaces:**
- Consumes: `MoveTable.Failed(from, to, caps, why)` from Task 1.
- Produces: `Realize` returns with `z.Why` set only when a re-plan finds no plan, or when a failure taught nothing new.
  No round cap.

- [ ] **Step 1: Replace the round cap with a drift-once rule.** Replace `int _maxRounds = 3, _offTicks = 10;` with:

```csharp
        /// <summary>A step built more than this many ticks off its plan's tick is planned again from, once per goal position.</summary>
        const int DriftTicks = 300;
```

Delete the `_maxRounds = Solve.Option(args, "--rounds", ...)` and `_offTicks = Solve.Option(args, "--off-ticks", ...)`
lines. In `Realize`, add after `int i = 0;`:

```csharp
            // Goal positions a drift or a slow walk was planned again from: once each, so drift can't loop.
            var driftAt = new HashSet<int>();
```

In `Again`, delete `if (z.Rounds == _maxRounds) return false;`.

- [ ] **Step 2: Record failures.** In the `if (played == null)` block, replace everything between `Log("realize: " + why);`
  and the `z.Log.Add(...)` line with:

```csharp
                    // A fact under every place the step started from (its restarts' landings too, where the plan stood), so no plan
                    // makes it again on this engine build; a failure that teaches nothing new ends the stage (it would loop).
                    bool learned = false;
                    foreach (string f in froms) learned |= moves.Failed(f, to, caps, why);
                    if (!learned) { z.Why = why + "; already known not to play, nothing new to plan on"; continue; }
```

- [ ] **Step 3: Apply drift-once to the two drift re-plans.** Replace the slow-walk check's condition
  `if (walked > Math.Max(3L * planTicks, planTicks + 500L))` with
  `if (walked > Math.Max(3L * planTicks, planTicks + 500L) && driftAt.Add(z.Planned.Count))`, and its body's last line
  `if (!Again(why)) z.Why ??= why; continue;` with `if (Again(why)) continue;`. When no plan comes, the slow step is kept
  and the build goes on. Replace the last drift line with:

```csharp
                if (Math.Abs(off) > DriftTicks && driftAt.Add(z.Planned.Count)) Again($"built {off:+0;-0} ticks off the plan");
```

- [ ] **Step 4: Build and self-test**

Run: the Task 1 Step 5 command with `eng62`.
Expected: `0 Error(s)`, `plan-selftest: PASS`.

- [ ] **Step 5: Check one failure end to end (bounded).** From goal 987, `buy:buyclonemult7` straight from `buy:buytree0`
  is known not to play. With the old searches it would take minutes, so first check that the failure is written. Use a
  `--try` whose step can't play quickly: a buy of a Course 8 box behind its door.

Run:
`timeout 300 'C:\tmp\igtap\zip\eng62\IGTAP.EngineSim.exe' fulltas-plan "$TR" --from 987 --target buy:buytree7 --try "buy:buycd8@50" | grep "^try"`
Expected: `try buy:buycd8: doesn't play ...`. Realize records failures; `--try` does not, by design, since it is a
diagnostic and writes nothing. This step only checks that the build runs on the real trace. `$TR` is the Zip% trace:
`C:/Program Files (x86)/Steam/steamapps/common/IGTAP an Incremental Game That's Also a Platformer/BepInEx/config/IGTAPTasMod/enginesim/fulltas-Zip%.json`.

- [ ] **Step 6: Commit**

```bash
git add enginesim/Cli/EconomyPlan.cs
git commit -m "Realize: every failure is a fact under each place the step started from; no round cap; drift re-plans once per goal"
```

---

### Task 3: One bounded search budget per step, in simulated ticks

**Files:**
- Modify: `enginesim/Cli/FullTas.cs`:
  - fields near lines 78-88;
  - `TimeUp` (line 685);
  - `Explore` (lines 911-1019): its signature, loop condition and stall test.
- Modify: `enginesim/Cli/FullTasBuild.cs`: `SolvePiece` (lines 390-461).
- Modify: `enginesim/Cli/EconomyPlan.cs`: `Realize`'s `PlayStep` call (line 1696); `TrySteps` (line 1460).

**Interfaces:**
- Produces:
  - `internal bool FullTas._unattended`.
  - `const long PieceTicks = 20_000_000, StepTicks = 2 * PieceTicks, StallTicks = 2_000_000` in `FullTas`.
  - `long _stepTicks`: the step's search ticks left.
  - `Explore(..., long budgetTicks = 0)`: when > 0, bounded by simulated ticks.
  - `long _lastSearchTicks`: the ticks the last `Explore` simulated.

- [ ] **Step 1: Fields and `TimeUp`.** In `FullTas`, next to `_pieceMinutes`:

```csharp
        /// <summary>The unattended economy loop (fulltas with no flags, fulltas-plan --try): budgets in simulated ticks, no wall clock
        /// (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, sections 2-3). Off for every other caller.</summary>
        internal bool _unattended;
        internal const long PieceTicks = 20_000_000, StepTicks = 2 * PieceTicks, StallTicks = 2_000_000;
        /// <summary>The current step's search ticks left (unattended); each piece search takes what it simulated.</summary>
        long _stepTicks = long.MaxValue;
        /// <summary>The ticks the last Explore simulated.</summary>
        long _lastSearchTicks;
```

Change `TimeUp` to:

```csharp
        bool TimeUp { get { return _stopRequested || !_unattended && _clock.Elapsed.TotalMinutes >= _minutes; } }
```

- [ ] **Step 2: `Explore` in ticks.** Add a parameter `long budgetTicks = 0` at the end of `Explore`'s signature. Replace
  the loop head and the stall line (lines 978 and 981) with:

```csharp
            long ticksAtLast = 0;
            bool Over() => budgetTicks > 0 ? Interlocked.Read(ref solver.Ticks) >= budgetTicks : (_clock.Elapsed - begin).TotalMinutes >= budgetMinutes;
            while (!TimeUp && !Over())
            {
                Thread.Sleep(250);
                lock (window)
                    if (finished && (budgetTicks > 0 ? Interlocked.Read(ref solver.Ticks) - ticksAtLast >= StallTicks : (_clock.Elapsed - last).TotalMinutes >= stallMinutes)) break;
```

In the `Improved` handler, next to `last = _clock.Elapsed;`, add
`ticksAtLast = Interlocked.Read(ref solver.Ticks);`. In the `walks` block, next to its `last = _clock.Elapsed;`, add
the same line. After `solver.Stop();` and the joins, add
`_lastSearchTicks = Interlocked.Read(ref solver.Ticks);`.

- [ ] **Step 3: `SolvePiece`, one attempt within the step's budget when unattended.** Replace the `for (int attempt ...)`
  head and its budget line with:

```csharp
            for (int attempt = 1; attempt <= (_unattended ? 1 : 2) && !TimeUp; attempt++)
            {
                double budget = attempt == 1 ? budgetMinutes : budgetMinutes * 3;
                long ticks = _unattended ? Math.Min(PieceTicks, _stepTicks) : 0;
                if (_unattended && ticks <= 0) { Log($"{title}: the step's search budget is spent"); return null; }
```

Pass `budgetTicks: ticks` to the `Explore` call. After it, add `if (_unattended) _stepTicks -= _lastSearchTicks;`.

- [ ] **Step 4: Give each realize step its budget.** In `Realize`, change the `PlayStep` try/finally to:

```csharp
                _stepTicks = _unattended ? StepTicks : long.MaxValue;
                try { played = PlayStep(s, z, goalOf, out goals, out walked, i < plan.Stands.Length && plan.Stands[i], i < plan.Levels.Length ? plan.Levels[i] : 1); }
                finally { _playBy = long.MaxValue; _stepTicks = long.MaxValue; }
```

In `TrySteps`, set `_unattended = true;` first, and give each `PlayStep` call the same `_stepTicks = StepTicks;` before
it and `_stepTicks = long.MaxValue;` after it.

- [ ] **Step 5: Build and self-test.** Run the Task 1 Step 5 command with `eng63`.
Expected: `0 Error(s)`, `plan-selftest: PASS`.

- [ ] **Step 6: The bounded step on the real case.** `record:7` from goal 987 searched for over 10 minutes this session.
  Now it must end within its budget.

Run:
`timeout 900 'C:\tmp\igtap\zip\eng63\IGTAP.EngineSim.exe' fulltas-plan "$TR" --from 987 --target buy:buytree7 --try "record:7@450" > /c/tmp/igtap/zip/t3.log; grep -E "^try|budget is spent|piece for goal .*finish" /c/tmp/igtap/zip/t3.log | tail -5`
Expected: it ends in under 15 minutes (`timeout` didn't fire), with either a `try record:7: tick ...` line or
`doesn't play` after a `the step's search budget is spent` line. Note the wall time from the log's last timestamp: it
calibrates `PieceTicks`. If one search took over 6 minutes, halve `PieceTicks` and `StallTicks`, rebuild, and rerun
this step. The constants are deterministic whatever their value.

- [ ] **Step 7: Commit**

```bash
git add enginesim/Cli/FullTas.cs enginesim/Cli/FullTasBuild.cs enginesim/Cli/EconomyPlan.cs
git commit -m "Unattended: one search budget per step in simulated ticks, one attempt per piece, no wall clock"
```

---

### Task 4: Deterministic piece searches

**Files:**
- Modify: `enginesim/Runtime/OfflineSolver.cs`: fields near `public bool NoResets` (line 152); `Start` (line 240-268,
  `_archiveBudget = available / 4;`); `Offer` (line 435-448); `Worker.Loop` (line 829-859). Commit only these hunks
  (Global Constraints).
- Modify: `enginesim/Cli/FullTas.cs`: `Explore`.
- Modify: `enginesim/Cli/EconomyPlan.cs`: `TrySteps`, which prints an inputs hash.
- Modify: `docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md`, section 3: the worker count from 8 to 16.

**Interfaces:**
- Produces:
  - `public long OfflineSolver.ArchiveBudget` (0: today's `available / 4`).
  - `public long OfflineSolver.TickLimit` and `StallTicks` (0: off): the worker ends its loop itself, at a run boundary,
    once `Ticks` reaches `TickLimit`, or once a best exists and `StallTicks` passed since the last improvement. With one
    worker that point doesn't depend on timing.
  - In unattended mode, `Explore` runs `SearchWorkers` = 16 solvers of one worker each, seeds 1..16, each with budget
    `budgetTicks / 16`, with no resume and no walks. It returns their finds in solver order, and processes `GraftFind`
    and `_findModes` after they all end, in that order.

- [ ] **Step 1: `ArchiveBudget`.** In `OfflineSolver`, next to `public int Rng` (or wherever `Rng` is declared):

```csharp
        /// <summary>The archive's byte budget (0: a quarter of the machine's memory). Fixed, the eviction is the same on any machine.</summary>
        public long ArchiveBudget;
```

In `Start`, change `_archiveBudget = available / 4;` to
`_archiveBudget = ArchiveBudget > 0 ? ArchiveBudget : available / 4;`.

Add the worker-side stop. Next to `ArchiveBudget`:

```csharp
        /// <summary>A one-worker solver's own end (0: off): its worker stops at a run boundary once Ticks reaches TickLimit, or once a
        /// best exists and StallTicks passed since the last improvement; a stop from outside lands wherever the clock says.</summary>
        public long TickLimit, StallTicks;
        long _improvedAt;
        internal bool Spent => TickLimit > 0 && (Interlocked.Read(ref Ticks) >= TickLimit
            || StallTicks > 0 && Volatile.Read(ref Shared.Version) > 0 && Interlocked.Read(ref Ticks) - Interlocked.Read(ref _improvedAt) >= StallTicks);
```

In `Offer`, inside the lock after `Shared.Version++;`, add `Interlocked.Exchange(ref _improvedAt, Interlocked.Read(ref Ticks));`.
In `Worker.Loop`, change `while (!_owner._stop)` to `while (!_owner._stop && !_owner.Spent)`.

- [ ] **Step 2: The unattended branch of `Explore`.** At the top of `Explore`, after `var found = ...; ReloadAvoids(goals);
  OfflineSolver.ForcedPath path = WithStrays(create, goals);`, add:

```csharp
            if (_unattended && budgetTicks > 0) return ExploreFixed(title, span, create, caps, seeds, path, baseline, budgetTicks, goals, out bestInputs);
```

Add the method below `Explore`:

```csharp
        const int SearchWorkers = 16;

        /// <summary>
        /// Explore, unattended (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, section 3): SearchWorkers solvers of
        /// one worker each, seeds 1.., a fixed archive budget, no archive resume and no walks, each to its share of the ticks (or
        /// StallTicks / SearchWorkers after a find without a faster one); their finds in solver order, the grafts' tallies after
        /// all end. Nothing depends on the threads' timing, so the same start gives the same routes.
        /// </summary>
        List<List<InputTick>> ExploreFixed(string title, string span, Func<Simulation> create, string caps, List<List<InputTick>> seeds,
            OfflineSolver.ForcedPath path, long baseline, long budgetTicks, ICollection<string> goals, out List<InputTick> bestInputs)
        {
            long share = budgetTicks / SearchWorkers, stall = StallTicks / SearchWorkers;
            var finds = new List<(List<InputTick> inputs, string how, long atGoal)>[SearchWorkers];
            var bests = new List<InputTick>[SearchWorkers];
            var ticks = new long[SearchWorkers];
            var grafts = _mined ?? AutoGrafts(caps);
            Log($"{title} {span}: {SearchWorkers} fixed searches of {share} ticks each");
            Parallel.For(0, SearchWorkers, new ParallelOptions { MaxDegreeOfParallelism = SearchWorkers }, i =>
            {
                var mine = finds[i] = new List<(List<InputTick>, string, long)>();
                var solver = new OfflineSolver(create, caps, seeds, null, path, null, grafts, rng: 1 + i) { ArchiveBudget = 1L << 30, TickLimit = share, StallTicks = stall };
                solver.Resume = _states.Index;
                solver.NoResets = _noResets;
                solver.Improved += (time, runTicks, inputs, how) =>
                {
                    long atGoal = runTicks - Simulation.StrictFinishTicks;
                    if (atGoal < baseline) lock (mine) mine.Add((inputs, how, atGoal));
                };
                // The worker ends itself (TickLimit, StallTicks): no stop from this thread, whose timing would decide where.
                foreach (Thread t in solver.Start(1)) t.Join();
                solver.Stop();
                lock (solver.Shared) bests[i] = solver.Shared.Inputs;
                ticks[i] = Interlocked.Read(ref solver.Ticks);
            });
            var found = new List<List<InputTick>>();
            for (int i = 0; i < SearchWorkers; i++)
                foreach (var (inputs, how, _) in finds[i])
                {
                    found.Add(inputs);
                    _findModes?.Add(how);
                    GraftFind(how);
                }
            _lastSearchTicks = ticks.Sum();
            bestInputs = Enumerable.Range(0, SearchWorkers).Where(i => bests[i] != null)
                .Select(i => (inputs: bests[i], at: finds[i].Count == 0 ? long.MaxValue : finds[i].Min(f => f.atGoal), i))
                .OrderBy(b => b.at).ThenBy(b => b.i).Select(b => b.inputs).FirstOrDefault();
            Log($"{title} {span}: {found.Count} finds, {_lastSearchTicks} ticks simulated");
            return found;
        }
```


- [ ] **Step 3: Print an inputs hash in `--try`.** In `TrySteps`, on the success line
  (`Console.WriteLine($"try {arg}: tick {from} -> {z.Tick}, ...`), append
  `+ $"; inputs {SnapshotIndex.Hash(z.Inputs, z.Inputs.Count):x16}"`.

- [ ] **Step 4: Build and self-test.** Run the Task 1 Step 5 command with `eng64`.
Expected: `0 Error(s)`, `plan-selftest: PASS`.

- [ ] **Step 5: Determinism under load.** Use a step that needs a search: `lap:7` from goal 987. Run it twice, the
  second time with 24 busy processes alongside:

```bash
EXE='C:\tmp\igtap\zip\eng64\IGTAP.EngineSim.exe'
timeout 1200 "$EXE" fulltas-plan "$TR" --from 987 --target buy:buytree7 --try "lap:7@600" | grep "^try" > /c/tmp/igtap/zip/d1.txt
for k in $(seq 24); do python -c "while True: pass" & done
timeout 1200 "$EXE" fulltas-plan "$TR" --from 987 --target buy:buytree7 --try "lap:7@600" | grep "^try" > /c/tmp/igtap/zip/d2.txt
kill $(jobs -p)
diff /c/tmp/igtap/zip/d1.txt /c/tmp/igtap/zip/d2.txt && echo IDENTICAL
```

Expected: `IDENTICAL`. The lines may differ only in their wall-time `(N s)` suffix: compare with
`sed 's/ ([0-9.]* s)//'` on both files first. If they differ, find the shared state that depends on order:
`_visits`/`VisitCell` is passed as null above, then check `AutoGrafts` caching, `WithStrays`, and `Prefixed`'s
`_states`. Fix it and repeat.

- [ ] **Step 6: Commit** (only your `OfflineSolver.cs` hunk, per Global Constraints)

```bash
git diff -U0 enginesim/Runtime/OfflineSolver.cs > /c/tmp/igtap/zip/os.patch   # trim to the ArchiveBudget/TickLimit/Spent/_improvedAt hunks
git apply --cached /c/tmp/igtap/zip/os.patch
git add enginesim/Cli/FullTas.cs enginesim/Cli/EconomyPlan.cs docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md
git commit -m "Unattended piece searches: 16 fixed single-worker solvers, seeds 1-16, merged in order; same start, same routes"
```

---

### Task 5: The planner capped by states

**Files:**
- Modify: `enginesim/Cli/EconomyPlan.cs`: `Search`'s beam stop (line 617); the stage's `PlanSettings` in `PlanEconomy`
  (line 1266-1268).

**Interfaces:**
- Produces: `internal const int BeamStates = 1_000_000;` in `EconomyPlan`. When `_unattended`, `PlanSettings` is
  `Mode: "beam"`, `Width: _stageWidth > 0 ? _stageWidth : EconomyPlan.DefaultWidth`, `MaxStates: BeamStates`,
  `Seconds: double.PositiveInfinity`.

- [ ] **Step 1: The beam stops on states.** At line 617, change the stop to:

```csharp
                    if (clock.Elapsed.TotalSeconds >= maxSeconds || result.States >= maxStates) { why = $"stopped after {result.States} states ({clock.Elapsed.TotalSeconds:0} s) at tick {priority.tick}"; break; }
```

Check first that `result.States` is the beam's played-state counter, the one the beam's progress line prints as
"states played". If the beam counts in another variable, use that.

- [ ] **Step 2: The unattended settings.** In `PlanEconomy`, replace the `var settings = new PlanSettings(...)` with:

```csharp
            var settings = _unattended
                ? new PlanSettings(targets, bonuses, "beam", _stageWidth > 0 ? _stageWidth : EconomyPlan.DefaultWidth, EconomyPlan.BeamStates,
                    double.PositiveInfinity, levels, only, null)
                : new PlanSettings(targets, bonuses, mode, _stageWidth > 0 ? _stageWidth : Solve.Option(args, "--beam", EconomyPlan.DefaultWidth), maxStates,
                    Solve.Option(args, "--minutes", 30.0) * 60, levels, only, Solve.Option<string>(args, "--stand", null));
```

- [ ] **Step 3: A self-test that the cap holds.** In `EconomyPlanCheck.Run`, right after the `PlanResult wide = Beam(1000, false);`
  check, add:

```csharp
            toy.Restore(new Toy().Capture());
            PlanResult byStates = EconomyPlan.Search(toy, 0, "c1", p => Options(toy, p, false), 5, double.PositiveInfinity, width: 1000, remaining: p => Remaining(toy, p));
            Check(byStates.Why?.Contains("stopped after") == true, $"a beam with no time cap stops at its state cap ({byStates.Why})");
```

- [ ] **Step 4: Build and self-test.** Run the Task 1 Step 5 command with `eng65`.
Expected: `plan-selftest: PASS`.

- [ ] **Step 5: Commit**

```bash
git add enginesim/Cli/EconomyPlan.cs enginesim/Cli/EconomyPlanCheck.cs
git commit -m "Planner: capped by states played when unattended; no seconds cap"
```

---

### Task 6: `fulltas <trace>` runs the economy stages unattended, with a report; `fulltas-plan` keeps only `--try`

**Files:**
- Modify: `enginesim/Cli/FullTas.cs`: `Run` (line 181-188).
- Modify: `enginesim/Cli/EconomyPlan.cs`:
  - `EconomyPlanCommand` and `Stages` (lines 1098-1119);
  - `PlanEconomy` (lines 1181-1325);
  - `RealizeRows` (1326-1395);
  - delete `ReplanCheck`'s CLI branch and `JoinAgain`.
- Modify: `enginesim/Cli/Program.cs`: the `fulltas-plan` usage text. Commit only your hunk.
- Modify: `docs/zip-handoff.md`: the resume command.
- Test: `enginesim/Cli/EconomyPlanCheck.cs`: the Area 2 offer set.

**Interfaces:**
- Consumes: `_unattended` (Task 3), `MoveTable.Failures` (Task 1).
- Produces:
  - `static int FullTas.Unattended(string[] args)`;
  - `static readonly HashSet<string> EconomyPlan.AreaTwoOffer`;
  - `fulltas/<run>.report.json`.

- [ ] **Step 1: The Area 2 offer set**, in `EconomyPlan`, next to `Gates`:

```csharp
        /// <summary>
        /// What the unattended planner offers in Area 2 (a target box under zone 2/): every Area 2 economy box and tree box (Courses
        /// 6-10's, the atom box, buytree0-27), Courses 6-9's laps and record laps. The game's doors (Gates, Never) still apply; a box
        /// or lap realize can't build becomes a failed fact (MoveTable.Failed). Area 1 targets offer as before (FullTasGoal.IsEconomy).
        /// </summary>
        internal static readonly HashSet<string> AreaTwoOffer = new HashSet<string>(
            FullTasGoal.Boxes.Where(b => b.path.StartsWith("zone 2/", StringComparison.Ordinal) || b.path.StartsWith("AtomUpgrades/", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(b.path, @"^Courses/[Cc]ourse ([6-9]|10)/"))
                .Select(b => "buy:" + b.token)
                .Concat(new[] { 6, 7, 8, 9 }.SelectMany(n => new[] { "lap:" + n, "record:" + n })));
```

In `PlanEconomy`, after `only` is parsed:

```csharp
            if (_unattended && GoalOf(targets[^1])?.boxes is [string targetBox] && targetBox.StartsWith("zone 2/", StringComparison.Ordinal)) only = EconomyPlan.AreaTwoOffer;
```

Move this line below the `GoalOf` definition. Self-test, in `EconomyPlanCheck.Run`:

```csharp
            Check(EconomyPlan.AreaTwoOffer.Contains("buy:buytree0") && EconomyPlan.AreaTwoOffer.Contains("buy:buytree25") && EconomyPlan.AreaTwoOffer.Contains("buy:buyatom")
                && EconomyPlan.AreaTwoOffer.Contains("buy:buyatomlevel9") && EconomyPlan.AreaTwoOffer.Contains("lap:7") && !EconomyPlan.AreaTwoOffer.Contains("lap:10")
                && EconomyPlan.AreaTwoOffer.Contains("buy:buyreward6") && !EconomyPlan.AreaTwoOffer.Contains("buy:buyreward5") && !EconomyPlan.AreaTwoOffer.Contains("buy:buywj"),
                $"the Area 2 offer: {EconomyPlan.AreaTwoOffer.Count} boxes and laps, Area 1's out");
```

- [ ] **Step 2: The unattended loop.** Replace `EconomyPlanCommand` and `Stages` with:

```csharp
        internal static int EconomyPlanCommand(string[] args)
        {
            if (Solve.Option<string>(args, "--try", null) == null) throw new ArgumentException("fulltas-plan keeps only --try; run `fulltas <trace>` for the economy stages");
            return new FullTas(args) { _unattended = true }.PlanEconomy(args);
        }

        /// <summary>
        /// fulltas &lt;trace&gt; with no flags (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md, section 4): from the best's
        /// furthest goal, plan to the next buy and realize, install when the build gets further, again, until the best reaches every
        /// goal, the next goal isn't an economy buy (the reach solve's: Search), or a stage installs nothing twice in a row (the second
        /// with the beam twice as wide). Each stage opens the run afresh. Writes fulltas/&lt;run&gt;.report.json after each stage.
        /// </summary>
        internal static int Unattended(string[] args)
        {
            var report = new List<object>();
            for (int stage = 1, fails = 0; fails < 2; stage++)
            {
                var run = new FullTas(args) { _unattended = true, _stageWidth = fails == 1 ? EconomyPlan.DefaultWidth * 2 : EconomyPlan.DefaultWidth };
                Console.WriteLine($"stage {stage}" + (fails > 0 ? $" ({fails} without progress before it)" : ""));
                int code = run.PlanEconomy(args);
                report.Add(run._stageReport);
                File.WriteAllText(run.ReportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                if (run._allReached) return 0;
                if (code == NotEconomy) return new FullTas(args).Search();
                fails = run._installed ? 0 : fails + 1;
            }
            Console.WriteLine($"unattended: two stages in a row got no further; see the report");
            return 3;
        }

        /// <summary>PlanEconomy's answer when the next goal is no economy buy (affordable already, or no buy): the reach solve's.</summary>
        const int NotEconomy = 4;
        internal string ReportFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_trace)), "fulltas", _name + ".report.json");
        /// <summary>This stage's report entry: target, plan and built ticks, the failed facts it added, its install and why it stopped.</summary>
        object _stageReport;
```

In `PlanEconomy`, every argument read becomes the unattended value when `_unattended`:
- `stages` is `_unattended`;
- `--from` is `furthest` and `--target` is `next`;
- `--bonuses`, `--compare` and `--out` are empty or null.

Right after `SimSnapshot at = GoalState(kept).at;`, add the hand-over test:

```csharp
            if (_unattended && Solve.Option<string>(args, "--try", null) == null
                && (!targets[^1].StartsWith("buy:", StringComparison.Ordinal) || Affordable(at, GoalOf(targets[^1])?.boxes ?? Array.Empty<string>()) == true))
            {
                _stageReport = new { target = targets[^1], from = kept, stop = "the next goal is no economy buy: the reach solve's" };
                return NotEconomy;
            }
```

Before `RealizeRows`, capture `var failuresBefore = moves.Failures.ToHashSet();` and pass it to `RealizeRows` as a new
last parameter `ISet<(string from, string to, string caps, int tree, string why)> failuresBefore`. At the end of
`RealizeRows`, before its `return`, set:

```csharp
            var z0 = realized.FirstOrDefault().z;
            _stageReport = new
            {
                target = _labels[targetIndex], from = kept, fromTick = startTick, planTicks = z0?.PlanTicks, built = z0 != null && z0.Done ? z0.Tick : -1,
                installed = _installed, reaches = written.run?.Progress, stop = z0?.Why,
                failed = moves.Failures.Where(f => !failuresBefore.Contains(f)).Select(f => new { f.from, f.to, f.caps, f.tree, f.why }),
            };
```

Delete from `PlanEconomy` the branches for `--join` (`JoinAgain`), `--replan` (`ReplanCheck`), `--compare`/`--skip-free`
(the `compares` loop and `skipFree`), `--out` (the `outFile` JSON write in `PlanEconomy` and `RealizeRows`), and
`--route`. Delete the methods `JoinAgain` and `ReplanCheck`. Keep `PlanFrom`, `Replan` and `Join`: the goals pull of
`fulltas --auto` uses them. Install in `RealizeRows` when `_unattended` (the replacement for
`args.Contains("--install") || args.Contains("--stages")`), never with `--replace`.

- [ ] **Step 3: `fulltas` dispatch.** In `FullTas.Run`, before the last `return`:

```csharp
            // The trace alone: the unattended economy stages (docs/superpowers/specs/2026-10-08-fulltas-unattended-design.md), which hand
            // over to the reach solve when the next goal isn't an economy buy.
            if (args.Length == 1) return Unattended(args);
```

- [ ] **Step 4: Usage text and handoff.** In `Program.cs`, replace the `fulltas-plan` usage lines with one line:
  `fulltas-plan <trace> --from <goal> --target <buy:box> --try STEPS   realize's steps one at a time (a diagnostic; fulltas
  <trace> runs the stages)`. In `docs/zip-handoff.md`, replace the Resume command with
  `& <engine>\IGTAP.EngineSim.exe fulltas $traces\fulltas-Zip%.json` and the line "the report:
  fulltas\Zip%.report.json".

- [ ] **Step 5: Build, self-test, `--try` still works**

Run: the Task 1 Step 5 command with `eng66`. Then:
`timeout 300 'C:\tmp\igtap\zip\eng66\IGTAP.EngineSim.exe' fulltas-plan "$TR" --from 987 --target buy:buytree7 --try "wait@50" | grep "^try"`
Expected: `plan-selftest: PASS`; one `try wait@50: the build stood 50 ticks ...` line.

- [ ] **Step 6: Commit**

```bash
git add enginesim/Cli/FullTas.cs enginesim/Cli/EconomyPlan.cs enginesim/Cli/EconomyPlanCheck.cs docs/zip-handoff.md
# Program.cs: only the usage hunk (Global Constraints)
git commit -m "fulltas <trace>: the economy stages unattended, with a report; fulltas-plan keeps only --try"
```

---

### Task 7: Shared-code regressions, then the acceptance run

**Files:** none changed unless a check fails; then the fix goes in the task that owns the code.

- [ ] **Step 1: Other runs replay unchanged.** For each of `Tree%`, `TouchGrass-ANY%` and `FalseEnding%-280`:
  `timeout 600 "$EXE" fulltas "<trace>" --check "<its installed best route json>"`. Expected: each reaches all its goals
  in its recorded ticks. Their traces and routes are in the config folder's `enginesim/` and `enginesim/fulltas/`.
- [ ] **Step 2: `tools/validate.py` and `tools/tech-check.py`.** These need the game; the game is running. Run each as
  its usage says. Expected: no new failures against their last logs.
- [ ] **Step 3: Acceptance.** `"$EXE" fulltas "$TR" > /c/tmp/igtap/zip/accept.log 2>&1`, in the background.
  - Bound: stage 1's planning ends within 1,000,000 beam states, and each realize step within `StepTicks`.
  - Expected: it ends by itself with `fulltas/Zip%.report.json` written. Either `buy:buytree7` is installed, or the
    report's `stop` names what blocked it.
  - Check that `runs/Zip%.json` changed only by an install.
- [ ] **Step 4: Update `docs/zip-handoff.md`** with the acceptance result (ticks, report summary) and commit.
