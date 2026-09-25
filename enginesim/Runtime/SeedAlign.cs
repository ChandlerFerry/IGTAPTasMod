using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // A standalone route (made from its course's quick-restart start, standing still) as a chain segment's seed: the chain
    // arrives somewhere else and moving. Two ways to carry it over, each judged by a run from the segment's start:
    // - splice: a course segment starts where the chain crosses the course's start gate, so the route's inputs from its
    //   own last start-gate crossing on (where its course time starts) go on from there as they are;
    // - walk: stick-only inputs (no jump, no dash) that land, walk to the route's start and stand still there, as a player
    //   would before playing it; loses the time a stop costs.
    // Where the route starts and where its gate crossing is come from an analysis run: the same start with the game's
    // quick restart first (Movement.respawn(true), the standalone route's own start), standing still, then the route.
    // Only the inputs it finds are kept; the solver optimizes from there.
    public static class SeedAlign
    {
        const float Tolerance = 0.5f;   // px from the route's start at rest
        const float LevelTolerance = 4f; // a start on another floor can't be walked to
        const float RestartMatch = 16f;  // px between the given start and where the quick restart rests
        const int MaxSettle = 300, MaxWalk = 600, StillTicks = 3, Tries = 12, SpliceShift = 3;

        // Where a standalone route really starts and where its course time starts, found on `analysis` (a simulation of
        // the segment's start with the quick restart first). A simulation runs on the thread that made it (the engine's
        // hooks are thread-static), so the caller runs this on the analysis simulation's own thread.
        public sealed class Analysis
        {
            public Vector2 Start;
            // The route's input index right after its last start-gate crossing; -1: none (or not a course segment).
            public int GateInput = -1;
            public string Note;
        }

        public static Analysis Analyze(Simulation analysis, Vector2 target, List<InputTick> route)
        {
            var result = new Analysis { Start = target };
            analysis.RestartAtStart = true;
            var (settle, rest) = Still(analysis, new List<InputTick>(), MaxSettle);
            if (settle < 0 || Vector2.Distance(rest, target) > RestartMatch)
            {
                result.Note = settle < 0 ? "the quick restart never comes to rest" : $"the quick restart rests at {rest.x:0.0},{rest.y:0.0}, not at the route's start";
                return result;
            }
            result.Start = rest;
            if (analysis.TimedFromRunStart) { result.Note = "not a course segment"; return result; }
            var inputs = Enumerable.Repeat(new InputTick(), settle).Concat(route).ToList();
            int gate = -1;
            bool wasTracking = false;
            float lastTime = 0f;
            RunResult own = analysis.Run(inputs, afterTick: tick =>
            {
                courseScript course = analysis.TimingCourse();
                float time = course != null ? Simulation.CourseTime(course) : 0f;
                // The course's timer starts, or restarts on a later crossing of its start gate (a flying start); while
                // standing before the route, only its state is noted (the chain's course may still be timing).
                if (tick > settle && course != null && (!wasTracking || time < lastTime)) gate = tick;
                wasTracking = course != null;
                lastTime = time;
                return true;
            });
            if (!own.Finished)
            {
                result.Note = $"the route does not finish from the quick restart (rest {rest.x:0.0},{rest.y:0.0}; {own.EndReason ?? "no finish"} at {own.End.x:0.0},{own.End.y:0.0})";
                return result;
            }
            if (gate < 0) { result.Note = "no start-gate crossing seen in the route"; return result; }
            result.GateInput = gate - settle;
            result.Note = $"its course time starts at input {result.GateInput} of {route.Count}";
            return result;
        }

        // The route's seeds for the segment `sim` runs, fastest first (empty: none finishes). `note` says what happened.
        public static List<List<InputTick>> Seeds(Simulation sim, Analysis analysis, List<InputTick> route, out string note)
        {
            var found = new List<(float time, List<InputTick> inputs)>();
            var notes = new List<string> { analysis.Note };
            if (analysis.GateInput >= 0)
            {
                var splice = Splice(sim, analysis.GateInput, route, out string how);
                notes.Add("splice: " + how);
                if (splice != null) found.Add(splice.Value);
            }
            List<InputTick> walked = Align(sim, analysis.Start, route, out string walkNote, out float walkTime);
            notes.Add("walk: " + walkNote);
            if (walked != null) found.Add((walkTime, walked));
            note = string.Join("; ", notes);
            return found.OrderBy(f => f.time).Select(f => f.inputs).ToList();
        }

        // The route from its last start-gate crossing on, from the segment's start (which is at that course's start gate);
        // the inputs a few ticks either side of the crossing are tried too (the chain arrives at another speed). The fastest
        // that finishes, or null.
        static (float time, List<InputTick> inputs)? Splice(Simulation sim, int gate, List<InputTick> route, out string note)
        {
            (float time, List<InputTick> inputs)? best = null;
            int bestShift = 0;
            for (int shift = -SpliceShift; shift <= SpliceShift; shift++)
            {
                int k = gate + shift;
                if (k < 0 || k >= route.Count) continue;
                List<InputTick> suffix = route.GetRange(k, route.Count - k);
                RunResult r = sim.Run(suffix);
                if (r.Finished && (best == null || r.Time < best.Value.time)) { best = (r.Time, suffix); bestShift = shift; }
            }
            note = best != null ? $"inputs from {gate + bestShift} on finish in {best.Value.time:0.00}s"
                : $"inputs from {gate}±{SpliceShift} on do not finish";
            return best;
        }

        static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo OnGround = typeof(Movement).GetField("onGround", Fields);
        static readonly FieldInfo Velocity = typeof(Movement).GetField("Velocity", Fields);
        static readonly FieldInfo Momentum = typeof(Movement).GetField("momentum", Fields);

        // The fastest finishing walked-in route, or null (no walkable prefix finishes it); `time` is its segment time.
        static List<InputTick> Align(Simulation sim, Vector2 target, List<InputTick> route, out string note, out float time)
        {
            time = float.PositiveInfinity;
            // Land and stop without steering.
            var (settle, at) = Still(sim, new List<InputTick>(), MaxSettle);
            if (settle < 0) { note = "never came to rest"; return null; }
            if (Math.Abs(at.y - target.y) > LevelTolerance) { note = $"rests at y {at.y:0.0}, the route starts at y {target.y:0.0}"; return null; }
            List<InputTick> Walk(int dir, int full, float last)
            {
                var p = Enumerable.Repeat(new InputTick(), settle).ToList();
                p.AddRange(Enumerable.Repeat(new InputTick { x = dir }, full));
                if (last > 0f) p.Add(new InputTick { x = dir * last });
                return p;
            }
            var candidates = new List<(float err, List<InputTick> prefix)>();
            void Consider(List<InputTick> walk)
            {
                var (still, pos) = Still(sim, walk, MaxSettle);
                if (still < 0 || Math.Abs(pos.y - target.y) > LevelTolerance) return;
                var prefix = walk.Concat(Enumerable.Repeat(new InputTick(), still - walk.Count)).ToList();
                candidates.Add((Math.Abs(pos.x - target.x), prefix));
            }
            if (Math.Abs(at.x - target.x) <= Tolerance) Consider(Walk(0, 0, 0f));
            else
            {
                int dir = Math.Sign(target.x - at.x);
                // Full-stick ticks until the rest point passes the start, then the last tick's stick on both sides of it
                // (what a stick gives short of full: 0.3..0.8, MacroEngine.ControllerAxis).
                int k = 0;
                for (; k <= MaxWalk; k++)
                {
                    var (still, pos) = Still(sim, Walk(dir, k, 0f), MaxSettle);
                    if (still >= 0 && dir * (pos.x - target.x) >= 0f) break;
                }
                for (int full = Math.Max(0, k - 2); full <= Math.Min(k, MaxWalk); full++)
                {
                    Consider(Walk(dir, full, 0f));
                    for (int s = 6; s <= 16; s++) Consider(Walk(dir, full, s / 20f));
                    Consider(Walk(dir, full, 1f));
                }
            }
            List<InputTick> best = null;
            float bestTime = float.PositiveInfinity, bestErr = 0f;
            foreach (var (err, prefix) in candidates.OrderBy(c => c.err).Take(Tries))
            {
                var inputs = prefix.Concat(route).ToList();
                RunResult r = sim.Run(inputs);
                if (r.Finished && r.Time < bestTime) { best = inputs; bestTime = r.Time; bestErr = err; }
            }
            note = best != null ? $"{best.Count - route.Count} ticks to rest {bestErr:0.00} px from its start, finishes in {bestTime:0.00}s"
                : candidates.Count == 0 ? "no walk comes to rest on its floor"
                : $"closest rest {candidates.Min(c => c.err):0.00} px from its start; none of {Math.Min(Tries, candidates.Count)} finish";
            time = bestTime;
            return best;
        }

        // Plays `walk`, then no input until the player stands still on the ground for StillTicks ticks. Returns the input
        // count at that point (-1 if not within `limit` more ticks) and the position.
        static (int ticks, Vector2 pos) Still(Simulation sim, List<InputTick> walk, int limit)
        {
            var inputs = walk.Concat(Enumerable.Repeat(new InputTick(), limit)).ToList();
            int still = 0, found = -1;
            sim.Run(inputs, afterTick: tick =>
            {
                if (tick < walk.Count) return true;
                bool rest = (bool)OnGround.GetValue(sim.Player) && ((Vector2)Velocity.GetValue(sim.Player)).sqrMagnitude < 1e-4f
                    && ((Vector2)Momentum.GetValue(sim.Player)).sqrMagnitude < 1e-4f;
                still = rest ? still + 1 : 0;
                if (still < StillTicks) return true;
                found = tick + 1;
                return false;
            }, extraTicks: 0);
            return (found, found < 0 ? default : (Vector2)sim.Player.transform.position);
        }
    }
}
