using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // A standalone route (made from its course's quick-restart start, standing still) as a chain segment's seed: the chain
    // arrives somewhere else and moving. Prefix the route with stick-only inputs (no jump, no dash) that land, walk to the
    // route's start and stand still there, as a player would before playing it. Loses the time a stop costs; the solver
    // optimizes from there.
    public static class SeedAlign
    {
        const float Tolerance = 0.5f;   // px from the route's start at rest
        const float LevelTolerance = 4f; // a start on another floor can't be walked to
        const int MaxSettle = 300, MaxWalk = 600, StillTicks = 3, Tries = 12;

        static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo OnGround = typeof(Movement).GetField("onGround", Fields);
        static readonly FieldInfo Velocity = typeof(Movement).GetField("Velocity", Fields);
        static readonly FieldInfo Momentum = typeof(Movement).GetField("momentum", Fields);

        // The fastest finishing aligned route, or null (no walkable prefix finishes it). `note` says what happened.
        public static List<InputTick> Align(Simulation sim, Vector2 target, List<InputTick> route, out string note)
        {
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
