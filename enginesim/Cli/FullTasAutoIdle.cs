using System;
using System.Collections.Generic;
using System.Linq;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts idle (docs/fulltas.md, "Parts"): every wait for Cash stands under the box it waits for. The wait fixes put idle
    // ticks before the quick restart ahead of a buy (the restart puts the player back as the old route had it, so the join is
    // exact), which leaves a buy's wait wherever that restart was: FalseEnding-Any% stood 114 ticks after a Course 1 lap before
    // restarting for buy:buywj, and 25 after the lap at goal 51. Here the wait stands on the ground under the box's trigger
    // (a box is bought from below) and the player goes in once the Cash is there, so every idle tick is where the route waits
    // for: the box.
    sealed partial class FullTas
    {
        Route _idledFor;
        /// <summary>An idle run this long or longer between the goal before a buy and the buy is a wait for it (shorter ones are
        /// the pauses of a hop).</summary>
        const int IdleWaitRun = 8;
        /// <summary>Ground spots (inputs before the buy, after the last quick restart) a wait may stand at, and how many of the
        /// ones that come to rest under the box (closest to its middle first) are tried.</summary>
        const int UnderBoxWindow = 200, UnderBoxSpots = 6;
        /// <summary>A stick released on the ground slides this far (world units) before the player rests: spots this much
        /// further out than the trigger are looked at, and RestTicks idle ticks are played to see where the player rests.</summary>
        const float SlideReach = 120f;
        const int RestTicks = 12;
        /// <summary>The last ground spot is run on this many ticks at most, to reach the trigger.</summary>
        const int MaxRunOn = 60;
        /// <summary>The jump into the box: pressed, held up to JumpFall ticks (to the touch).</summary>
        const int JumpFall = 14;
        /// <summary>A wait may be this many ticks over the ones it replaces; MaxWhole spot and way in pairs a goal are played whole.</summary>
        const int WaitSlack = 30, MaxWhole = 6, MaxFixes = 2;
        /// <summary>Stands closer than this (world units) to one already tried count as the same.</summary>
        const float UnderBoxApart = 6f;

        struct Pose
        {
            public bool ground;
            public float x, y, vx;
        }

        /// <summary>A box's trigger (its colliders' union, world frame) and the player's half width.</summary>
        struct Zone
        {
            public float x0, x1, y0, y1, playerHalf;
            public bool Under(float x) { return x >= x0 && x <= x1; }
            public bool Near(float x) { return x >= x0 - playerHalf && x <= x1 + playerHalf; }
            public float Middle { get { return (x0 + x1) / 2f; } }
        }

        /// <summary>
        /// Each buy goal's waits (idle runs of IdleWaitRun or more since the goal before) that don't stand under the box are taken
        /// out and put back as idle ticks standing under its trigger: at one of the ground spots on the way in where the player
        /// comes to rest within the trigger's x-range (no quick restart between the spot and the buy), then either the route's
        /// own inputs on from the spot, or a jump straight up into the box and the route's inputs on from its old touch. The
        /// fewest ticks that keep every goal (a stretch that touches the box sooner shifts the Cash and can stray into another box
        /// later) and are no slower than the run is kept, goal by goal; the run then goes to Commit (the fresh check) when it's
        /// faster, or is the best from then on when it's as fast.
        /// </summary>
        void IdlePull()
        {
            Route best = _best, current = best;
            _idledFor = best;
            double c0 = CpuSeconds();
            int moved = 0, tried = 0;
            long saved = 0;
            var notes = new List<string>();
            // current's state after chainAt inputs, moved forward goal by goal (a kept change starts after chainAt).
            SimSnapshot chain = null;
            int chainAt = -1;
            SimSnapshot Snapshot(int index)
            {
                if (chain == null || chainAt > index)
                {
                    _eval.SetStart(_origin, 0);
                    _eval.Goals.Limit = _goals;
                    chain = index == 0 ? _origin : _eval.Advance(current.Inputs.GetRange(0, index));
                    chainAt = index;
                }
                else if (chainAt < index)
                {
                    _eval.SetStart(chain, chainAt);
                    _eval.Goals.Limit = _goals;
                    chain = _eval.Advance(current.Inputs.GetRange(chainAt, index - chainAt));
                    chainAt = index;
                }
                return chain;
            }
            // First the waits before the quick restarts re-timed from each buy on (the Cash comes on other ticks than the run was
            // made for once anything before it moved).
            if (RetuneSweep(current) is Route swept)
            {
                moved++;
                saved += current.Last - swept.Last;
                notes.Add($"the waits re-timed: {Describe(swept)}");
                current = swept;
            }
            // Then the filler before a last quick restart made standing.
            if (Defiller(current) is Route stood)
            {
                moved++;
                saved += current.Last - stood.Last;
                notes.Add($"filler made standing: {Describe(stood)}");
                current = stood;
            }
            for (int g = 0; g < _goals && !TimeUp; g++)
            {
                if (_runGoals.Goals[g].kind != FullTasGoal.Buy) continue;
                List<InputTick> inputs = current.Inputs;
                int from = g == 0 ? 0 : (int)Math.Min(current.GoalInput[g - 1], inputs.Count);
                int buy = (int)Math.Min(current.GoalInput[g], inputs.Count);
                // Walking to the box and standing under it, when that beats the run's way there (a restart and dash to a far box, a
                // loop, a jump that doesn't gain).
                if (g > 0)
                {
                    var (walked, walkHow) = WalkIn(current, g, from, buy, Snapshot(from));
                    if (walked != null && walked.Live < current.Live)
                    {
                        tried++;
                        moved++;
                        saved += current.Last - walked.Last;
                        notes.Add($"goal {g + 1} ({_labels[g]}): {walkHow}");
                        Log($"auto: idle under the box: {notes[notes.Count - 1]}: {Describe(walked)}");
                        current = walked;
                        continue;
                    }
                }
                // The waits: idle runs since the goal before.
                var runs = new List<(int at, int length)>();
                for (int i = from; i < buy;)
                {
                    if (!Idle(inputs[i])) { i++; continue; }
                    int j = i;
                    while (j < buy && Idle(inputs[j])) j++;
                    if (j - i >= IdleWaitRun) runs.Add((i, j - i));
                    i = j;
                }
                if (runs.Count == 0) continue;
                SimSnapshot atFrom = Snapshot(from);
                if (atFrom == null) break;
                Pose[] poses = PoseTrace(atFrom, from, inputs.GetRange(from, buy - from));
                Zone zone = BoxZone(_runGoals.Goals[g]);
                if (float.IsNaN(zone.x0)) continue;
                // A wait that ends within reach of the trigger is under the box already: its ticks at rest there draw the RNG.
                var far = runs.Where(r => !zone.Near(poses[r.at + r.length - from].x)).ToList();
                Route pick = null;
                string how = null;
                foreach (var (at, length) in runs.Except(far))
                {
                    int ws = at + length;
                    while (ws > at && poses[ws - 1 - from].ground && zone.Near(poses[ws - 1 - from].x) && poses[ws - 1 - from].vx == 0f) ws--;
                    SimSnapshot atWs = Snapshot(ws);
                    if (atWs == null || TimeUp) break;
                    (Route r, string rhow) = RngTickets(current, g, inputs, ws, at + length, atWs, ws, zone, current.GoalPhysics[g]);
                    if (r != null && r.Live < current.Live && (pick == null || r.Live < pick.Live)) (pick, how) = (r, rhow);
                }
                if (pick == null && far.Count == 0) continue;
                tried++;
                if (pick == null) (pick, how) = MoveWait(current, g, from, buy, far, zone, poses, Snapshot);
                if (pick == null) continue;
                moved++;
                saved += current.Last - pick.Last;
                notes.Add($"goal {g + 1} ({_labels[g]}): {far.Sum(r => r.length)} idle ticks in {far.Count} waits -> {how}");
                Log($"auto: idle under the box: {notes[notes.Count - 1]}: {Describe(pick)}");
                current = pick;
            }
            Log($"auto: idle under the box: {moved} of {tried} buys' waits moved ({saved} ticks saved) in {CpuSeconds() - c0:0} CPU s"
                + (notes.Count == 0 ? "" : ": " + string.Join("; ", notes.Take(12)) + (notes.Count > 12 ? $"; and {notes.Count - 12} more" : "")));
            if (!ReferenceEquals(current, best) && ReferenceEquals(_best, best))
            {
                if (current.Live < best.Live) Commit(current, $"idle under the box ({moved} waits moved)");
                else
                {
                    // As fast, and every wait stands under its box: the best from here on (not a record, so no gate submission).
                    _best = current;
                    _idledFor = current;
                    WriteRunFile(current);
                    Collect(current, "idle under the box");
                }
            }
        }

        /// <summary>
        /// Goal g's buy with its waits away from the box (far: idle runs of the inputs) moved under it. Returns the whole run and
        /// how, or null when no spot and wait keeps every goal at no more than the current live time.
        /// </summary>
        (Route, string) MoveWait(Route current, int g, int from, int buy, List<(int at, int length)> far, Zone zone, Pose[] poses, Func<int, SimSnapshot> snapshot)
        {
            List<InputTick> inputs = current.Inputs;
            var gone = new bool[buy - from];
            foreach (var r in far) for (int i = 0; i < r.length; i++) gone[r.at + i - from] = true;
            int removed = gone.Count(x => x);
            List<InputTick> without = inputs.Where((_, i) => i < from || i >= buy || !gone[i - from]).ToList();
            int buyW = buy - removed;
            int div = far[0].at;
            // The spawn after the first quick restart since the goal before: the player stands there (not on the ground yet on
            // the restart's tick), and the dash hops after it are what a stand replaces (Course 3's buys: spawn 9506,6958, the
            // triggers at x 9040-9168). It is run on from, as the last ground spot is.
            int spawn = -1;
            for (int i = from; i < buyW - 1 && spawn < 0; i++) if (without[i].reset) spawn = i + 1;
            SimSnapshot atSpawn = spawn >= 0 && spawn < div ? snapshot(spawn) : null;
            SimSnapshot atDiv = snapshot(div);
            if (atDiv == null) return (null, null);
            // Where the run without the waits goes: the ground spots on the way in.
            Pose[] way = PoseTrace(atDiv, div, without.GetRange(div, buyW - div));
            int lastReset = -1;
            for (int i = div; i < buyW; i++) if (without[i].reset) lastReset = i;
            int lo = Math.Max(Math.Max(div + 1, lastReset + 1), buyW - UnderBoxWindow);
            var spots = new List<int>();
            int lastGround = -1;
            for (int q = lo; q < buyW; q++)
            {
                if (!way[q - div].ground) continue;
                lastGround = q;
                if (way[q - div].x >= zone.x0 - SlideReach && way[q - div].x <= zone.x1 + SlideReach) spots.Add(q);
            }
            if (lastGround >= 0 && !spots.Contains(lastGround)) spots.Add(lastGround);
            if (spawn >= 0 && !spots.Contains(spawn)) spots.Insert(0, spawn);
            // Where the player can stand: at a ground spot, or on the ground the route's last spot runs on with the stick it had
            // there (the route jumped off at it, short of the trigger), then rests within the trigger's x-range after a release.
            var stands = new List<(int q, int run, SimSnapshot state, InputTick step, float x)>();
            SimSnapshot s = atDiv;
            int sAt = div;
            for (int k = 0; k < spots.Count; k++)
            {
                int q = spots[k];
                if (TimeUp) return (null, null);
                if (q > sAt)
                {
                    _eval.SetStart(s, sAt);
                    _eval.Goals.Limit = _goals;
                    s = _eval.Advance(without.GetRange(sAt, q - sAt));
                    sAt = q;
                }
                if (s == null) break;
                SimSnapshot at = q < div ? atSpawn : s;
                bool held = q > 0 && without[q - 1].held;
                InputTick step = RunStep(without, q, held, zone.Middle < (q <= div ? poses[q - from] : way[q - div]).x ? -1f : 1f);
                int maxRun = q == lastGround || q == spawn ? MaxRunOn : 0;
                for (int run = 0; run <= maxRun; run++)
                {
                    Pose rest = default;
                    var list = Enumerable.Repeat(step, run).Concat(Enumerable.Repeat(new InputTick { held = held }, RestTicks)).ToList();
                    _eval.SetStart(at, q);
                    _eval.Goals.Limit = _goals;
                    _eval.Run(list, null, tick =>
                    {
                        if (tick == list.Count) rest = new Pose { ground = _eval.Player.onGround, x = _eval.PlayerWorld.x, y = _eval.PlayerWorld.y, vx = _eval.Player.Velocity.x };
                        return true;
                    }, 0);
                    if (rest.ground && zone.Under(rest.x)) stands.Add((q, run, at, step, rest.x));
                    // Past the trigger: further runs only go further.
                    if (step.x < 0f && rest.x < zone.x0 || step.x > 0f && rest.x > zone.x1) break;
                }
            }
            // Inside the trigger's edge the route comes from.
            float inset = Math.Min(25f, (zone.x1 - zone.x0) / 4f);
            var standList = new List<(int q, int run, SimSnapshot state, InputTick step, float x)>();
            foreach (var t in stands.OrderBy(t => Math.Abs(t.x - (t.step.x < 0f ? zone.x1 - inset : zone.x0 + inset))))
                if (standList.Count < UnderBoxSpots && standList.All(u => Math.Abs(u.x - t.x) >= UnderBoxApart)) standList.Add(t);
            long old = current.GoalPhysics[g];
            int cap = removed + WaitSlack;
            int tailCount = without.Count - buyW;
            // The ways in: from each stand, the route's own inputs on (a stand at a spot of its own way), a jump straight up, and
            // a jump with the stick toward the trigger (the first two stands).
            var ways = new List<(int stand, string kind, float stick)>();
            for (int i = 0; i < standList.Count; i++)
            {
                if (standList[i].run == 0) ways.Add((i, "own inputs", 0f));
                ways.Add((i, "jump", 0f));
            }
            for (int i = 0; i < Math.Min(2, standList.Count); i++) ways.Add((i, "jump with the stick", standList[i].step.x));
            int played = 0, fixes = 0;
            // The best way in with hops for the RNG (the first way's stand, at the fewest idle ticks that touch: the jump in as the
            // Cash comes), kept when faster than every plain way.
            Route lucky = null;
            string luckyHow = null;
            foreach (var (index, kind, stick) in ways)
            {
                if (TimeUp || played >= MaxWhole) break;
                var (q, run, state, step, _) = standList[index];
                bool held = q > 0 && without[q - 1].held;
                List<InputTick> head = without.GetRange(0, q);
                List<InputTick> onward = Enumerable.Repeat(step, run).ToList();
                List<InputTick> Stand(int w) { return Enumerable.Repeat(new InputTick { held = held }, w).ToList(); }
                List<InputTick> Jump(int w)
                {
                    var list = new List<InputTick>(onward);
                    for (int i = 0; i < w; i++) list.Add(new InputTick { held = false, release = held && i == 0 });
                    list.Add(new InputTick { held = true, press = true, x = stick });
                    for (int i = 0; i < JumpFall; i++) list.Add(new InputTick { held = true, x = stick });
                    return list;
                }
                // The run from the stand's spot with these inputs: the goal's touch input and physics tick (-1: none).
                (long input, long physics) Play(List<InputTick> list)
                {
                    _eval.SetStart(state, q);
                    _eval.Goals.Limit = g + 1;
                    RunResult rr = _eval.Run(list, null, null, 0);
                    return rr.Progress >= g + 1 ? (rr.GoalInputTicks[g], rr.GoalPhysicsTicks[g]) : (-1, -1);
                }
                bool own = kind == "own inputs";
                List<InputTick> Way(int w) { return own ? Stand(w).Concat(without.GetRange(q, Math.Min(buyW + 30, without.Count) - q)).ToList() : Jump(w); }
                // The whole run for w idle ticks (null: the jump never touches, or the button state doesn't join).
                List<InputTick> Whole(int w)
                {
                    if (own) return head.Concat(Stand(w)).Concat(without.Skip(q)).ToList();
                    List<InputTick> list = Jump(w);
                    long touchInput = Play(list).input;
                    if (touchInput < 0) return null;
                    List<InputTick> tail = without.Skip(buyW).ToList();
                    if (tail.Count > 0)
                    {
                        // The button state the route's inputs go on from: a release when it lets go, none to start held.
                        InputTick t0 = tail[0];
                        if (!t0.held && !t0.release) { t0.release = true; tail[0] = t0; }
                        else if (t0.held && !t0.press) return null;
                    }
                    return head.Concat(list.GetRange(0, (int)touchInput - q)).Concat(tail).ToList();
                }
                // The wait window: the fewest idle ticks that touch (low), and the fewest that touch no sooner than the old route did
                // (high: the old timing; a run that no longer touches counts as later, a stray).
                int low = FirstWait(w => Play(Way(w)).input, cap);
                if (low < 0) continue;
                int a = low, b = cap;
                while (a < b)
                {
                    int mid = (a + b) / 2;
                    var (input, physics) = Play(Way(mid));
                    if (input < 0 || physics >= old) b = mid; else a = mid + 1;
                }
                int high = a;
                if (played++ == 0 && Whole(low) is List<InputTick> whole && !TimeUp)
                {
                    int ws = q + (own ? 0 : run);
                    var (r, rhow) = RngTickets(current, g, whole, ws, ws + low, state, q, zone, Play(Way(low)).physics);
                    if (r != null && r.Live < current.Live)
                        (lucky, luckyHow) = (r, $"{low} idle ticks under the box at input {q} ({kind}{(run > 0 ? $" after {run} ticks on" : "")}), {rhow}");
                }
                Route Play2(int w) { List<InputTick> list = Whole(w); return list == null ? null : Evaluate(list); }
                Route top = null;
                int hi = high;
                for (; hi <= Math.Min(high + 2, cap) && (top == null || top.Progress < _goals); hi++) top = Play2(hi);
                hi--;
                if (top == null) continue;
                Route pick = top;
                string how = null;
                if (top.Progress < _goals)
                {
                    // The Cash comes on other ticks now (the clones' rolls re-time) and a later goal misses: the wait fix.
                    if (fixes++ >= MaxFixes) continue;
                    // With the re-timing of a stray buy (the stray part's, whatever the parts are): the later goals' waits move with it.
                    bool retime = _strayRetime;
                    _strayRetime = true;
                    try { pick = WaitFix(top, current, top.Inputs.Count - tailCount, $"idle under the box at goal {g + 1}", far[0].at); }
                    finally { _strayRetime = retime; }
                    if (pick == null) continue;
                    how = $", {pick.Inputs.Count - top.Inputs.Count} more idle ticks later on";
                }
                else
                {
                    b = hi;
                    a = low;
                    while (a < b)
                    {
                        int mid = (a + b) / 2;
                        Route r = Play2(mid);
                        if (r != null && r.Progress == _goals) { b = mid; pick = r; } else a = mid + 1;
                    }
                    hi = b;
                }
                if (lucky != null && lucky.Live < pick.Live) break;
                if (pick.Live <= current.Live)
                    return (pick, $"{hi} idle ticks under the box at input {q} ({kind}{(run > 0 ? $" after {run} ticks on" : "")}), the buy at tick {pick.GoalPhysics[g]} against {old}{how}");
            }
            return lucky != null ? (lucky, luckyHow) : (null, null);
        }

        /// <summary>The smallest wait of 0..cap for which play(w) is a touch (>= 0): every fourth wait is looked at, then the ones before the first that touches.</summary>
        static int FirstWait(Func<int, long> play, int cap)
        {
            const int step = 4;
            for (int w = 0; w <= cap; w += step)
            {
                if (play(w) < 0) continue;
                for (int v = Math.Max(0, w - step + 1); v < w; v++) if (play(v) >= 0) return v;
                return w;
            }
            return -1;
        }

        /// <summary>The ground run on from spot q toward the trigger (dir: -1 left, 1 right): the stick x, and y as the route had it just
        /// before q (after its last quick restart), else none.</summary>
        static InputTick RunStep(List<InputTick> inputs, int q, bool held, float dir)
        {
            float y = 0f;
            for (int i = q - 1; i >= Math.Max(0, q - 6) && !inputs[i].reset; i--)
                if (inputs[i].x == dir) { y = inputs[i].y; break; }
            return new InputTick { x = dir, y = y, held = held };
        }

        /// <summary>
        /// For each input t of the list (1..count) whether the player is on the ground after the first t inputs from the snapshot,
        /// and where; index 0 is unset. One more tick than the list is played (the engine stops with its inputs, and the last
        /// tick reports nothing then).
        /// </summary>
        Pose[] PoseTrace(SimSnapshot from, int at, List<InputTick> inputs)
        {
            var poses = new Pose[inputs.Count + 2];
            if (inputs.Count == 0) return poses;
            List<InputTick> run = inputs.Concat(new[] { inputs[inputs.Count - 1] }).ToList();
            _eval.SetStart(from, at);
            _eval.Goals.Limit = _goals;
            _eval.Run(run, null, tick =>
            {
                if (tick <= inputs.Count)
                {
                    WorldPos p = _eval.PlayerWorld;
                    poses[tick] = new Pose { ground = _eval.Player.onGround, x = p.x, y = p.y, vx = _eval.Player.Velocity.x };
                }
                return tick < inputs.Count;
            }, 0);
            return poses;
        }

        /// <summary>
        /// Goal's box trigger in the world frame as the simulation stands (call it right after a run, in that run's frame): the
        /// union of the enabled trigger colliders of the goal's boxes (axis aligned, scaled by the transform); x0 is NaN when
        /// there is none.
        /// </summary>
        Zone BoxZone(FullTasGoal goal)
        {
            var zone = new Zone { x0 = float.NaN };
            if (goal.boxes == null) return zone;
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (upgradeBox b in _eval.World.All<upgradeBox>())
            {
                string path = b.transform.name;
                for (UnityEngine.Transform p = b.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
                string id = FullTasGoal.BoxId(path, _eval.World.SiblingIndex[b.transform]);
                if (!goal.boxes.Contains(id) && !goal.boxes.Contains(path)) continue;
                foreach (UnityEngine.BoxCollider2D c in b.GetComponents<UnityEngine.BoxCollider2D>())
                {
                    if (!c.isTrigger || !c.enabled) continue;
                    UnityEngine.Vector3 pos = b.transform.position, scale = b.transform.lossyScale;
                    WorldPos middle = _eval.ToWorld(new ScenePos(pos.x + c.offset.x * scale.x, pos.y + c.offset.y * scale.y));
                    float hw = c.size.x * Math.Abs(scale.x) / 2f, hh = c.size.y * Math.Abs(scale.y) / 2f;
                    x0 = Math.Min(x0, middle.x - hw); x1 = Math.Max(x1, middle.x + hw);
                    y0 = Math.Min(y0, middle.y - hh); y1 = Math.Max(y1, middle.y + hh);
                }
            }
            if (x0 > x1) return zone;
            float half = 0f;
            foreach (UnityEngine.BoxCollider2D c in _eval.Player.GetComponents<UnityEngine.BoxCollider2D>())
                if (!c.isTrigger && c.enabled) half = Math.Max(half, c.size.x * Math.Abs(_eval.Player.transform.lossyScale.x) / 2f);
            return new Zone { x0 = x0, x1 = x1, y0 = y0, y1 = y1, playerHalf = half };
        }
    }
}
