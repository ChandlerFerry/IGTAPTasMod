using System;
using System.Collections.Generic;
using System.Linq;
using FullTasGoal = IGTAPTasMod.FullTasGoal;

namespace IGTAP.EngineSim.Cli
{
    // --parts idle (docs/fulltas.md, "Parts"): every wait for Cash stands under the box it waits for. The wait fixes put idle
    // ticks before the quick restart ahead of a buy (the restart puts the player back as the old route had it, so the join is
    // exact), which leaves a buy's wait wherever that restart was: FalseEnding-Any% stood 114 ticks after a Course 1 lap before
    // restarting for buy:buywj, and 25 after the lap at goal 51. Here the wait stands on the ground under the box and jumps
    // in once the Cash is there, so every idle tick is where the route waits for: the box.
    sealed partial class FullTas
    {
        Route _idledFor;
        /// <summary>An idle run this long or longer between the goal before a buy and the buy is a wait for it.</summary>
        const int IdleWaitRun = 3;
        /// <summary>Under the box: an input at most UnderBoxWindow before the buy after which the player is on the ground within
        /// UnderBoxDx (world units) of where it touched the box; the UnderBoxSpots latest are tried.</summary>
        const int UnderBoxWindow = 150, UnderBoxSpots = 3;
        const float UnderBoxDx = 48f;
        /// <summary>The jump into the box: held 1..MaxJumpHold ticks, then up to JumpFall idle ticks for the touch.</summary>
        const int MaxJumpHold = 20, JumpFall = 40;

        /// <summary>
        /// Each buy goal's waits (idle runs of IdleWaitRun or more since the goal before) taken out, and put back as the fewest
        /// idle ticks that still buy it standing under the box (the latest few ground spots below where it touched the box,
        /// with no quick restart between them and the buy), then a jump straight up into it; the best's inputs from its old
        /// touch on follow. Kept goal by goal when the whole run still reaches every goal and is no slower; the run then goes
        /// to Commit (the fresh check) when it's faster, or is the best from then on when it's as fast.
        /// </summary>
        void IdlePull()
        {
            Route best = _best, current = best;
            _idledFor = best;
            double c0 = CpuSeconds();
            int moved = 0;
            long saved = 0;
            var notes = new List<string>();
            (bool ground, float x)[] trace = null;
            for (int g = 0; g < _goals && !TimeUp; g++)
            {
                if (_runGoals.Goals[g].kind != FullTasGoal.Buy) continue;
                trace ??= Trace(current.Inputs);
                List<InputTick> inputs = current.Inputs;
                int from = g == 0 ? 0 : (int)Math.Min(current.GoalInput[g - 1], inputs.Count);
                int buy = (int)Math.Min(current.GoalInput[g], inputs.Count);
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
                int wait = runs.Sum(r => r.length);
                int lastWaitEnd = runs[runs.Count - 1].at + runs[runs.Count - 1].length;
                // Under the box: the last inputs before the buy where the player is on the ground below where it touched the box,
                // after the last wait and with no quick restart between them and the buy.
                var spots = new List<int>();
                for (int q = buy - 1; q > Math.Max(lastWaitEnd, buy - UnderBoxWindow) && spots.Count < UnderBoxSpots; q--)
                {
                    if (inputs[q].reset) break;
                    if (trace[q].ground && Math.Abs(trace[q].x - trace[buy].x) <= UnderBoxDx) spots.Add(q);
                }
                var gone = new HashSet<int>(runs.SelectMany(r => Enumerable.Range(r.at, r.length)));
                List<InputTick> without = inputs.Where((_, i) => !gone.Contains(i)).ToList();
                int after = buy - gone.Count(i => i < buy);
                Route pick = null;
                string how = null;
                foreach (int spot in spots)
                {
                    if (TimeUp || pick != null) break;
                    int at = spot - gone.Count(i => i < spot);
                    _eval.SetStart(_origin, 0);
                    _eval.Goals.Limit = _goals;
                    SimSnapshot there = _eval.Advance(without.GetRange(0, at));
                    if (there == null) continue;
                    bool held = at > 0 && without[at - 1].held;
                    // Stand w ticks, then jump straight up holding it k ticks, until the box is touched: its inputs to the touch.
                    List<InputTick> Into(int w, int k)
                    {
                        var list = new List<InputTick>();
                        for (int i = 0; i < w; i++) list.Add(new InputTick { held = held });
                        list.Add(new InputTick { held = true, press = true });
                        for (int i = 1; i < k; i++) list.Add(new InputTick { held = true });
                        for (int i = 0; i < JumpFall; i++) list.Add(new InputTick());
                        _eval.SetStart(there, at);
                        _eval.Goals.Limit = g + 1;
                        RunResult rr = _eval.Run(list, null, null, 0);
                        if (rr.Progress < g + 1) return null;
                        int touch = (int)rr.GoalInputTicks[g] - at;
                        return touch <= list.Count ? list.GetRange(0, touch) : null;
                    }
                    int hold = -1;
                    for (int k = 1; k <= MaxJumpHold && hold < 0; k++) if (Into(wait, k) != null) hold = k;
                    if (hold < 0) continue;
                    // The fewest idle ticks that still buy it there (a stray buy isn't monotone: scan up from 0).
                    for (int w = 0; w <= wait && pick == null && !TimeUp; w++)
                    {
                        List<InputTick> into = Into(w, hold);
                        if (into == null) continue;
                        Route whole = Evaluate(without.Take(at).Concat(into).Concat(without.Skip(after)).ToList());
                        if (whole.Progress == _goals && whole.Live <= current.Live) { pick = whole; how = $"{w} idle ticks under the box at input {at}, a jump held {hold} ticks"; }
                        else break;
                    }
                }
                if (pick == null) continue;
                moved++;
                saved += current.Last - pick.Last;
                notes.Add($"goal {g + 1} ({_labels[g]}): {wait} idle ticks in {runs.Count} waits -> {how}");
                current = pick;
                trace = null;
            }
            Log($"auto: idle under the box: {moved} buys' waits moved ({saved} ticks saved) in {CpuSeconds() - c0:0} CPU s"
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

        /// <summary>For each input count t, whether the player is on the ground after the route's first t inputs, and its x.</summary>
        (bool ground, float x)[] Trace(List<InputTick> inputs)
        {
            var at = new (bool, float)[inputs.Count + 1];
            _eval.SetStart(_origin, 0);
            _eval.Goals.Limit = _goals;
            _eval.Run(inputs, null, tick =>
            {
                if (tick <= inputs.Count) at[tick] = (_eval.Player.onGround, _eval.PlayerWorld.x);
                return true;
            }, 0);
            return at;
        }
    }
}
