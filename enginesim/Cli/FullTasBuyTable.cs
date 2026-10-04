using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;
using FullTasGoal = IGTAPTasMod.FullTasGoal;
using Caps = IGTAPTasMod.Caps;

namespace IGTAP.EngineSim.Cli
{
    // The measured time-to-buy table (docs/fulltas.md, "Buy table"): for each place the run can stand at a buy phase (the
    // phase's start, its course's start/restart landings and finish, and each box the run buys there), the physics ticks to
    // reach and buy each box and each ordered 2-3-box set in one go, per technique: `walk` (the library's pieces and a
    // plain search), `jump-through` (a jump carrying sideways momentum whose arc passes the set's boxes, buying each on the
    // way: seeded jumps from a run-up and from the momentum the state already has) and `dash-through` (a dash along the box
    // row buying every box it passes; only where the phase's unlocks have the dash). Every entry is measured by the engine:
    // a short piece search whose goals are the set's buys, so the engine confirms each box is bought and stamps the tick,
    // seeded with that technique's inputs and the library's pieces (FullTasPlan.cs's machinery).
    //
    // `fulltas-buytable <trace> --from <goal> [--until <goal>] [--out file] [--max-set 3] [--entry-minutes m]
    //   [--entry-stall m] [--triple-reach u] [--techniques walk,jump,dash] [--retry-miss] [--targets reward:bonusN,lap:N]`
    // writes fulltas/<run>.buytable.json, cached per run and trace (an entry measured before is skipped; a miss is cached
    // too and retried only with --retry-miss). The format (tools/plan-buytable.py is its first reader):
    //   { run, trace, from, until, boxes[],
    //     places: [{id, name, kind: start|restart|gate|finish|box, box?, x, y, caps, tick, restart?: {press, land}}],
    //     entries: [{place: <id>, boxes: [<labels in the order bought>], technique: walk|jump-through|dash-through|miss,
    //                ticks | null, searched, caps, inputs: Solve.EncodeTicks(...)}] }
    // `tick` is the snapshot's own physics tick; a restart's `press` is the tick the reset is played from and `land` the
    // landing's, so the planner adds land - press to every restart walk. Entries come in every box-set order (order
    // matters) and per technique; the planner prefers the cheapest technique per box set.
    // --targets measures these instead of the phase's box sets, from every place: `reward:bonusN` (the bonus's reward box as a
    // one-box set, the box nearest its end rect: the run's goal, else NewGoal's) and `lap:N` (Course N's gate then its finish,
    // played as the plan plays a lap: FullTasPlan.Lap). The entry's `boxes` is the target's id (`["lap:2"]`), its ticks run to
    // the reward box's buy or the lap's finish.
    sealed partial class FullTas
    {
        public static int BuyTable(string[] args) { return new FullTas(args).BuyTableRun(args); }

        sealed class TablePlace
        {
            public string Id, Name, Kind, Box;
            public float X, Y;
            public string Caps;
            public long Tick, Press;
            public bool Restart;
        }

        int BuyTableRun(string[] args)
        {
            Open();
            int kept = GoalNumber(Solve.Option(args, "--from", ""), 1);
            int until = Math.Min(_goals + 1, GoalNumber(Solve.Option(args, "--until", ""), _goals + 1));
            if (kept < 1 || kept > _goals) throw new ArgumentException("--from wants a goal of the run");
            double entryMinutes = Solve.Option(args, "--entry-minutes", 0.2);
            double entryStall = Solve.Option(args, "--entry-stall", 0.03);
            // A lap's searches (FullTasPlan.Play's) take the piece budget: an entry's.
            _pieceMinutes = entryMinutes;
            int maxSet = Math.Clamp(Solve.Option(args, "--max-set", 3), 1, 3);
            // Sets of this many boxes are measured from the places near them only (a far place's multi-buy walk is chained
            // from its single buys; searching it blind costs minutes and finds nothing).
            float tripleReach = (float)Solve.Option(args, "--triple-reach", 600.0);
            string dir = Path.GetDirectoryName(Path.GetFullPath(_trace));
            string outPath = Solve.Option<string>(args, "--out", null) ?? Path.Combine(dir, "fulltas", _name + ".buytable.json");
            var want = new HashSet<string>(StringComparer.Ordinal);
            foreach (string t in (Solve.Option(args, "--techniques", "walk,jump,dash") ?? "").Split(','))
                if (t.Trim().Length > 0) want.Add(t.Trim());
            double deadline = _clock.Elapsed.TotalMinutes + _minutes;

            // The phase's boxes: every box a buy or reward goal in [kept, until) names, first goal first; and its lap goals
            // (a gate walk from a restart landing is an entry too: the planner prices a lap's walk in).
            var span = _runGoals.Goals.Skip(kept).Take(until - kept).ToList();
            var boxes = new List<FullTasGoal>();
            foreach (FullTasGoal g in span)
                if ((g.kind == FullTasGoal.Buy || g.kind == FullTasGoal.Reward) && !boxes.Any(b => b.label == g.label)) boxes.Add(g);
            var gates = span.Where(g => g.kind == FullTasGoal.Start).GroupBy(g => g.label).Select(g => g.First()).ToList();
            var only = new List<(List<FullTasGoal> set, string[] labels)>();
            foreach (string t in (Solve.Option<string>(args, "--targets", null) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                only.Add((TargetGoals(t), new[] { t }));
            Log($"buytable: goals {kept}..{until - 1}, {boxes.Count} boxes ({string.Join(" ", boxes.Select(b => b.label))}), gates {string.Join(" ", gates.Select(g => g.label))}");

            // The places the run stands at in the phase: its start, each goal cut (a course's start gate, its finish, a box
            // bought), and each quick restart's landing. One place per spot (within PieceReach, the library's own reach).
            var places = new List<(SimSnapshot at, int length, TablePlace place)>();
            // A snapshot's own physics tick on the goal timeline (MacroEngine._physicsTicks, what goal events stamp). At a
            // goal cut it must equal the goal state's tick; the check catches an unrestored engine counter.
            long TickOf(SimSnapshot s)
            {
                Read(s);
                return _eval.Engine.PhysicsTicks;
            }
            // expect: the best's own physics tick for the goal this place is cut at (-1 for a restart landing).
            void AddPlace(SimSnapshot at, int length, string name, string kind, string box, long press, long expect)
            {
                (Vector3 here, int unlocks) = Where(at);
                Read(at);
                string caps = PlayerCaps.Of(_eval).Id;
                foreach (var p in places)
                    if (p.place.Caps == caps && Math.Abs(p.place.X - here.x) <= PieceReach && Math.Abs(p.place.Y - here.y) <= PieceReach) return;
                long tick = TickOf(at);
                if (expect >= 0 && tick != expect)
                    Log($"buytable: {name}: the snapshot's tick {tick} is not the best's {expect} there: entry ticks would be wrong");
                places.Add((at, length, new TablePlace
                {
                    Id = "p" + places.Count, Name = name, Kind = kind, Box = box, X = here.x, Y = here.y, Caps = caps,
                    Tick = tick, Press = press, Restart = expect < 0,
                }));
            }
            int start = kept == 0 ? 0 : (int)Math.Min(_best.GoalInput[kept - 1], _best.Inputs.Count);
            var route = _best.Inputs.Take(start).ToList();
            if (route.Count < start) Pad(route, start);
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            SimSnapshot at = _eval.Advance(route);
            if (at == null || at.Goal.Next != kept) throw new InvalidOperationException($"the best doesn't replay to goal {kept}");
            AddPlace(at, route.Count, _labels[kept - 1] + " (the phase's start)", "start", null, -1, _best.GoalPhysics[kept - 1]);
            for (int k = kept; k < until && k <= _best.Progress; k++)
            {
                int end = (int)Math.Min(_best.GoalInput[k - 1], _best.Inputs.Count);
                if (end <= route.Count) continue;
                List<InputTick> chunk = _best.Inputs.GetRange(route.Count, end - route.Count);
                // A quick restart's landing is a place of its own (the run buys from there after every lap); its press tick
                // is the tick of the state the reset is played from.
                for (int i = 0; i < chunk.Count; i++)
                    if (chunk[i].reset)
                    {
                        SimSnapshot press = AdvanceFrom(at, route.Count, chunk.GetRange(0, i));
                        SimSnapshot landed = AdvanceFrom(at, route.Count, chunk.GetRange(0, i + 1));
                        if (landed != null) AddPlace(landed, route.Count + i + 1, "restart landing in " + _labels[k - 1], "restart", null, press == null ? -1 : TickOf(press), -1);
                    }
                at = AdvanceFrom(at, route.Count, chunk);
                if (at == null)
                {
                    Log($"buytable: the best doesn't replay to goal {k} {_labels[k - 1]}: places stop there");
                    break;
                }
                route = _best.Inputs.Take(end).ToList();
                string label = _labels[k - 1];
                long reached = _best.GoalPhysics[k - 1];
                if (label.StartsWith("start:", StringComparison.Ordinal)) AddPlace(at, route.Count, label + " gate", "gate", null, -1, reached);
                else if (label.StartsWith("finish:", StringComparison.Ordinal)) AddPlace(at, route.Count, label + " (the finish)", "finish", null, -1, reached);
                else if (label.StartsWith("buy:", StringComparison.Ordinal) || label.StartsWith("reward:", StringComparison.Ordinal))
                    AddPlace(at, route.Count, label, "box", label, -1, reached);
            }
            Log($"buytable: {places.Count} places (" + string.Join("; ", places.Select(p => $"{p.place.Id} {p.place.Kind} {p.place.Name} {p.place.X:0},{p.place.Y:0} {p.place.Caps}")) + ")");

            // The cached table: an entry measured before (the same run, trace and phase) is skipped.
            JsonNode table = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath)) : null;
            if (table?["from"]?.GetValue<string>() != _labels[kept - 1] || table?["until"]?.GetValue<string>() != (until > _goals ? "" : _labels[until - 1])
                || table?["boxes"]?.AsArray().Select(b => b.GetValue<string>()).SequenceEqual(boxes.Select(b => b.label)) != true)
                table = new JsonObject
                {
                    ["run"] = _name, ["trace"] = Path.GetFileName(_trace), ["from"] = _labels[kept - 1],
                    ["until"] = until > _goals ? "" : _labels[until - 1],
                    ["boxes"] = new JsonArray(boxes.Select(b => (JsonNode)b.label).ToArray()),
                    ["places"] = new JsonArray(), ["entries"] = new JsonArray(),
                };
            // Place ids are the run's own order: a cached entry's place id must mean the same spot next run.
            foreach (JsonNode e in table["entries"].AsArray())
            {
                string was = e["place"].GetValue<string>();
                if (was.StartsWith("p", StringComparison.Ordinal)) continue;
                var p = places.FirstOrDefault(q => q.place.Name == was);
                e["place"] = p.place?.Id ?? was;
            }
            var seen = new HashSet<string>(table["entries"].AsArray().Select(e => EntryKey(e["place"].GetValue<string>(),
                e["boxes"].AsArray().Select(b => b.GetValue<string>()).ToArray(), e["technique"].GetValue<string>())));
            table["places"] = new JsonArray(places.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.place.Id, ["name"] = p.place.Name, ["kind"] = p.place.Kind,
                ["box"] = p.place.Box, ["x"] = Math.Round(p.place.X, 2), ["y"] = Math.Round(p.place.Y, 2),
                ["caps"] = p.place.Caps, ["tick"] = p.place.Tick,
                ["restart"] = p.place.Restart ? new JsonObject { ["press"] = p.place.Press, ["land"] = p.place.Tick } : null,
            }).ToArray());

            _planBase = _runGoals;
            _borrow = (_labels.ToArray(), kept, Math.Min(until - 1, _best.Progress));
            _beforeSearch = () => SavePieces();
            // Piece searches' workers track the entry's goals (Track's list), as the evaluator does.
            Func<Simulation> make = _create;
            _create = () => { Simulation sim = make(); sim.UseGoals(_runGoals.For(_runGoals.Goals)); return sim; };
            void Save()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                Solve.WriteShared(outPath, table.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            int measured = 0, attempts = 0;
            // The box spots first: their entries are the walk chains' links. Every place's singles (and gate walks), then
            // every place's pairs, then the triples: a run cut short has the whole of the useful half.
            var ordered = places.OrderBy(p => p.place.Kind == "box" ? 0 : p.place.Kind == "restart" ? 1 : 2)
                .ThenBy(p => p.place.Name, StringComparer.Ordinal).ToList();
            bool retryMiss = args.Contains("--retry-miss");
            for (int size = 1; size <= (only.Count > 0 ? 1 : maxSet); size++)
                foreach (var (placeAt, length, place) in ordered)
                {
                    var due = only.Count > 0 ? only : Targets(boxes, gates, place, maxSet, places, tripleReach)
                        .Where(set => set.Count == size).Select(set => (set, set.Select(g => g.label).ToArray()));
                    foreach (var (set, labels) in due)
                    {
                        if (_clock.Elapsed.TotalMinutes >= deadline) goto done;
                        string key0 = EntryKey(place.Id, labels, "");
                        if (seen.Any(k => k.StartsWith(key0, StringComparison.Ordinal) && (!retryMiss || !k.EndsWith("(miss)", StringComparison.Ordinal)))) continue;
                        Measure(placeAt, length, place, set, labels, want, entryMinutes, entryStall, table, seen, ref measured);
                        if (++attempts % 25 == 0) Save();
                    }
                }
        done:
            Save();
            Log($"buytable: {measured} entries measured, {table["entries"].AsArray().Count} in {outPath}");
            SavePieces();
            return 0;
        }

        /// <summary>A --targets id's goals: `reward:bonusN` its reward box (the run's goal, else as the game resolves it, NewGoal),
        /// `lap:N` Course N's gate and finish.</summary>
        List<FullTasGoal> TargetGoals(string target)
        {
            if (target.StartsWith("lap:", StringComparison.Ordinal) && int.TryParse(target.Substring(4), out int course))
                return new List<FullTasGoal> { RunGoal("start:" + course, FullTasGoal.Start, course), RunGoal("finish:" + course, FullTasGoal.Finish, course) };
            if (target.StartsWith("reward:bonus", StringComparison.Ordinal))
                return new List<FullTasGoal> { _runGoals.Goals.FirstOrDefault(g => g.label == target) ?? NewGoal(target)?.goal
                    ?? throw new ArgumentException($"--targets {target}: no such bonus in courses.json, or no box near its end") };
            throw new ArgumentException($"--targets {target}: reward:bonusN or lap:N");
        }

        static string EntryKey(string place, string[] boxes, string technique) => place + " | " + string.Join(",", boxes) + " | " + technique;

        /// <summary>What to measure from one place: every ordered box set up to maxSet (triples only near their boxes) and,
        /// from a restart landing, the course's gate walk (a lap's walk in is one of the table's prices).</summary>
        static IEnumerable<List<FullTasGoal>> Targets(List<FullTasGoal> boxes, List<FullTasGoal> gates, TablePlace place, int maxSet,
            List<(SimSnapshot at, int length, TablePlace place)> places, float tripleReach)
        {
            foreach (List<FullTasGoal> set in Sets(boxes, maxSet))
                if (set.Count < 3 || Near(places, place, set, tripleReach))
                    yield return set;
            if (place.Kind == "restart")
                foreach (FullTasGoal g in gates)
                    yield return new List<FullTasGoal> { g };
        }

        /// <summary>Whether every box in the set has its own spot (the place the run buys it at) within reach of this place:
        /// only such places measure multi-box walks (the rest chain their single buys).</summary>
        static bool Near(List<(SimSnapshot at, int length, TablePlace place)> places, TablePlace place, List<FullTasGoal> set, float reach)
        {
            foreach (FullTasGoal g in set)
                if (!places.Any(p => p.place.Box == g.label && Math.Abs(p.place.X - place.X) <= reach && Math.Abs(p.place.Y - place.Y) <= reach))
                    return false;
            return true;
        }

        /// <summary>Every ordered set of 1..maxSet distinct boxes (order matters: a walk a then b costs its own ticks).</summary>
        static IEnumerable<List<FullTasGoal>> Sets(List<FullTasGoal> boxes, int maxSet)
        {
            foreach (FullTasGoal b in boxes) yield return new List<FullTasGoal> { b };
            if (maxSet < 2) yield break;
            foreach (List<FullTasGoal> pair in Pairs(boxes))
            {
                yield return pair;
                if (maxSet < 3) continue;
                foreach (FullTasGoal b in boxes)
                    if (!pair.Contains(b)) yield return pair.Concat(new[] { b }).ToList();
            }
        }

        static IEnumerable<List<FullTasGoal>> Pairs(List<FullTasGoal> boxes)
        {
            foreach (FullTasGoal a in boxes)
                foreach (FullTasGoal b in boxes)
                    if (a != b) yield return new List<FullTasGoal> { a, b };
        }

        /// <summary>One entry: the fastest way found to buy the set's boxes in order from the place, per technique, kept in the
        /// table and filed as a library piece (the pieces' key is the set's labels joined). The set's goals are appended to the
        /// run's goals for the measurement (as the plan appends a step's), so a walk that buys another box ends it and the
        /// engine confirms each named box is bought, in order.</summary>
        void Measure(SimSnapshot at, int length, TablePlace place, List<FullTasGoal> set, string[] boxLabels, HashSet<string> want, double minutes, double stall,
            JsonNode table, HashSet<string> seen, ref int measured)
        {
            // The run's own goals up to the place (_planBase): _runGoals is the previous entry's list once Track has run, which
            // for a place later in the phase ends short of it (Restarts then read a label past the list's end).
            var list = _planBase.Goals.Take(at.Goal.Next).Concat(set).ToList();
            Track(list);
            SimSnapshot here = at.WithGoal(Fresh(list.Count, at.Goal.Next));
            string key = set.Select(g => g.label).Aggregate((a, b) => a + " + " + b);
            double c0 = _clock.Elapsed.TotalSeconds;
            void FileEntry(JsonObject entry)
            {
                foreach (JsonNode e in table["entries"].AsArray().Where(e => e["place"].GetValue<string>() == place.Id
                        && e["boxes"].AsArray().Select(b => b.GetValue<string>()).SequenceEqual(boxLabels)).ToList())
                    table["entries"].AsArray().Remove(e);
                table["entries"].AsArray().Add(entry);
                seen.Add(EntryKey(place.Id, boxLabels, entry["technique"].GetValue<string>() == "miss" ? "(miss)" : entry["technique"].GetValue<string>()));
            }
            // A capped or locked box can't be bought here whatever the walk does (the goal would never count): the entry
            // says so instead of measuring a walk that cannot buy.
            string[] boxIds = set.Where(g => g.boxes != null).SelectMany(g => g.boxes).ToArray();
            if (boxIds.Length > 0 && Affordable(here, boxIds) == null)
            {
                FileEntry(new JsonObject
                {
                    ["place"] = place.Id, ["boxes"] = new JsonArray(boxLabels.Select(b => (JsonNode)b).ToArray()),
                    ["technique"] = "capped", ["ticks"] = null, ["searched"] = 0.0, ["caps"] = place.Caps,
                });
                Log($"buytable: {place.Id} {place.Name} {key}: capped or locked here");
                return;
            }
            // The table prices movement, not income: the place's Cash plus the set's price (a copy of the snapshot with its
            // own Shop state, so every restore keeps it). The other boxes stay as affordable as the run had them there: with
            // unlimited Cash every box a walk passes would buy (a strict stray), and standing in the set's last box once its
            // 0.2 s cooldown is over would buy it again; neither happens to the run, which spends what it holds.
            // A reward box is free: nothing to add.
            double price = boxIds.Length > 0 ? set.Where(g => g.boxes != null).Sum(g => Cost(here, g)) : 0.0;
            if (here.Economy?.Awarded != null && price > 0.0)
            {
                double need = price * 1.01 + 1.0;
                var awarded = (double[])here.Economy.Awarded.Clone();
                awarded[(int)globalStats.Currencies.Cash] += need;
                here.Economy = new Shop.State { Spent = here.Economy.Spent, Awarded = awarded, Economy = here.Economy.Economy, BuyMax = here.Economy.BuyMax };
            }
            // The piece search's workers replay the prefix, then start from this entry's goals and Cash, as Borrowed does.
            SimSnapshot start = here;
            _pieceState = s => { SimSnapshot t = s.WithGoal(start.Goal); t.Economy = start.Economy; return t; };
            var target = new Target { Goal = list.Count - 1, Key = key };
            (string technique, Step step) best = (null, null);
            // The searches play on from the place with the best's route up to it (a quick restart's pads read the route).
            var prefix = _best.Inputs.Take(length).ToList();
            if (prefix.Count < length) Pad(prefix, length);
            void Try(string technique, List<List<InputTick>> seeds, bool search)
            {
                Step step = Borrowed(here, prefix, target, seeds);
                if (step == null && search) step = SolvePiece(here, prefix, here.Goal.Next, target, minutes, stall, seeds);
                if (step != null && (best.step == null || step.Physics < best.step.Physics)) best = (technique, step);
            }
            // A lap is played as the plan plays one (FullTasPlan.Lap: library pieces as they are, else after a quick restart, then a
            // piece search per goal), from the place's own state (no Cash added: none is spent), the run's goals to it kept.
            bool lap = boxLabels[0].StartsWith("lap:", StringComparison.Ordinal);
            if (lap)
            {
                // A search's workers start at the step's goal (Play's target is its list's last, Track's _goals - 1): a stored
                // prefix state carries another entry's goal list.
                _pieceState = s => s.WithGoal(Fresh(_goals, _goals - 1));
                _kept = _planBase.Goals.Take(at.Goal.Next).ToArray();
                if (Lap(at, prefix, new List<FullTasGoal>(), set.ToArray()) is var (inputs, state, tick))
                    best = ("walk", new Step { Inputs = inputs.Skip(prefix.Count).ToList(), Physics = tick, Next = state });
            }
            // Walk: the library's pieces and the best's own ways to the same goals (BorrowedInputs), then a plain search.
            else if (want.Contains("walk")) Try("walk", BorrowedInputs(here, target), true);
            // Jump-through and dash-through: generated passes, both directions (the set's own order decides which wins).
            if (!lap && set.Count > 1 && want.Contains("jump")) Try("jump-through", PassSeeds(here, jump: true), true);
            // The place's caps are an id (`wjds`): its dash is the parsed caps', not an upper-case label's.
            if (!lap && set.Count > 1 && want.Contains("dash") && Caps.TryParse(place.Caps, out Caps caps) && caps.Dash)
                Try("dash-through", PassSeeds(here, jump: false), true);
            double spent = _clock.Elapsed.TotalSeconds - c0;
            if (best.step == null)
            {
                // A miss is cached too (a run that finds no way in its budget retries only with --retry-miss).
                FileEntry(new JsonObject
                {
                    ["place"] = place.Id, ["boxes"] = new JsonArray(boxLabels.Select(b => (JsonNode)b).ToArray()),
                    ["technique"] = "miss", ["ticks"] = null, ["searched"] = Math.Round(spent, 1), ["caps"] = place.Caps,
                });
                Log($"buytable: {place.Id} {place.Name} {string.Join(" + ", boxLabels)}: no way found ({spent:0.0} s)");
                return;
            }
            long ticks = best.step.Physics - place.Tick;
            FileEntry(new JsonObject
            {
                ["place"] = place.Id, ["boxes"] = new JsonArray(boxLabels.Select(b => (JsonNode)b).ToArray()),
                ["technique"] = best.technique, ["ticks"] = ticks, ["searched"] = Math.Round(spent, 1), ["caps"] = place.Caps,
                ["inputs"] = Solve.EncodeTicks(best.step.Inputs),
            });
            measured++;
            Log($"buytable: {place.Id} {place.Name} {string.Join(" + ", boxLabels)} [{best.technique}]: {ticks} ticks ({spent:0.0} s)");
        }

        /// <summary>
        /// Generated passes over the box row: a run-up of 0-24 ticks, then a jump carrying the stick's sideways momentum
        /// (jump-through: the arc passes the boxes) or a dash along the row (dash-through), the stick held through both.
        /// Both directions: the set's order decides which pass buys in it. Controller-legal sticks (±1) and real presses.
        /// </summary>
        static List<List<InputTick>> PassSeeds(SimSnapshot at, bool jump)
        {
            var seeds = new List<List<InputTick>>();
            foreach (float dir in new[] { 1f, -1f })
                foreach (int runup in new[] { 0, 4, 8, 12, 16, 24 })
                    foreach (int start in new[] { 0, 2, 4, 6, 8, 12, 16, 20 })
                    {
                        var t = new List<InputTick>();
                        for (int i = 0; i < runup + start; i++) t.Add(new InputTick { x = dir, y = 0f });
                        if (jump)
                            foreach (int hold in new[] { 6, 14 })
                            {
                                var one = t.ToList();
                                one.Add(new InputTick { x = dir, y = 0f, held = true, press = true });
                                for (int i = 0; i < hold - 1; i++) one.Add(new InputTick { x = dir, y = 0f, held = true });
                                one.Add(new InputTick { x = dir, y = 0f, release = true });
                                for (int i = 0; i < 40; i++) one.Add(new InputTick { x = dir, y = 0f });
                                seeds.Add(one);
                            }
                        else
                        {
                            t.Add(new InputTick { x = dir, y = 0f, dash = true });
                            for (int i = 0; i < 40; i++) t.Add(new InputTick { x = dir, y = 0f });
                            seeds.Add(t);
                        }
                    }
            return seeds;
        }
    }
}
