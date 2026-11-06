using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    [AttributeUsage(AttributeTargets.Method)]
    sealed class TechCheckAttribute : Attribute
    {
        public readonly string[] Techs;
        public TechCheckAttribute(params string[] techs) { Techs = techs; }
    }

    public static partial class TechChecks
    {
        public readonly struct Span
        {
            public readonly string Tech;
            public readonly int Start, End;
            public Span(string tech, int start, int end) { Tech = tech; Start = start; End = end; }
            public override string ToString() => Tech + " " + Start + ".." + End;
        }

        static Dictionary<string, Func<TechSteps, int, int, int>> _own;

        static Dictionary<string, Func<TechSteps, int, int, int>> Own
        {
            get
            {
                if (_own != null) return _own;
                var own = new Dictionary<string, Func<TechSteps, int, int, int>>(StringComparer.Ordinal);
                foreach (MethodInfo m in typeof(TechChecks).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    TechCheckAttribute a = m.GetCustomAttribute<TechCheckAttribute>();
                    if (a == null) continue;
                    var check = (Func<TechSteps, int, int, int>)Delegate.CreateDelegate(typeof(Func<TechSteps, int, int, int>), m);
                    foreach (string tech in a.Techs) own.Add(tech, check);
                }
                return _own = own;
            }
        }

        public static bool Checkable(string tech, string detector) => !string.IsNullOrEmpty(detector) && Detectors.Contains(detector) || Own.ContainsKey(tech);

        public static int Find(string tech, string detector, TechSteps run, int from, int to)
        {
            if (run.Steps.Count == 0) return -1;
            from = Math.Max(0, from);
            to = Math.Min(run.Steps.Count - 1, to);
            if (to < from) return -1;
            if (Own.TryGetValue(tech, out Func<TechSteps, int, int, int> check)) return check(run, from, to);
            if (string.IsNullOrEmpty(detector)) return -1;
            foreach (Span s in run.Spans)
                if (s.Tech == detector && s.End >= from && s.End <= to) return s.End;
            return -1;
        }

        public static int StepAt(TechSteps run, int tick)
        {
            for (int i = 0; i < run.Steps.Count; i++) if (run.Steps[i].Tick >= tick) return i;
            return run.Steps.Count;
        }

        static readonly HashSet<string> Detectors = new HashSet<string>(StringComparer.Ordinal)
        {
            "GcWallClip", "CoyoteJump", "WallCoyoteJump", "SuperJump", "ReversedSuper", "BackwardsDashJump", "Wavedash", "Ultra", "DashBounce",
            "WaveBounce", "Superbounce", "WallBounce", "SpringDash", "SpikeGrace", "UltraBonus", "SuperGroundBounce", "WallDash", "InwallWallBounce",
            "FloatUltra", "FreezeAim", "StoredWallJump", "HighSuper", "SuperbounceDoubleJump", "BoostedDoubleJump", "Pauselift", "DashCoyoteJump",
            "CheckpointCoyoteJump", "ZipIntangible", "KbWallClip", "FinalClingFloat", "ReverseWallJump",
        };

        const int FlipWindowTicks = 4, InwallChainTicks = 6, MaxSpan = 60;
        const int NoTick = int.MinValue / 2;

        // Overlay detectors port: ticks there count from 1, here step t is tick t + 1.
        internal static List<Span> Detect(TechSteps run)
        {
            IReadOnlyList<TechStep> steps = run.Steps;
            var spans = new List<Span>();
            var pending = new List<(string tech, int start)>();
            if (steps.Count == 0) return spans;
            MoveState first = steps[0].Calls.Count > 0 ? steps[0].Calls[0].State : steps[0].After;
            bool facing = first.Right, wasSpring = false, dashAtLastGround = false, pauselifted = false;
            bool waveAir = false, dashInWave = false, superAir = false, dashInSuper = false;
            float endVy = first.Vy, flipStick = 0f, lastHyperY = first.LastHyperY, lastHyperOriginY = first.SceneY - first.Y;
            int lastFlip = NoTick, flipStart = NoTick, dashStart = NoTick, springStart = NoTick, springCancel = NoTick;
            int lastGround = NoTick, lastWall = NoTick, backwardsDash = NoTick, backwardsStart = NoTick, lastSuperbounce = NoTick, respawnTick = NoTick;
            int waveStart = NoTick, superStart = NoTick, inwallStart = NoTick, inwallLast = NoTick;
            void Add(string tech, int start, int end)
            {
                if (start == NoTick || start > end || end - start > MaxSpan) start = end;
                if (start < 1) start = 1;
                spans.Add(new Span(tech, start - 1, end - 1));
            }
            for (int i = 0; i < steps.Count; i++)
            {
                TechStep step = steps[i];
                int tick = i + 1;
                MoveState pre = i > 0 ? steps[i - 1].After : first;
                MoveState input = default, super = default;
                bool inputOpen = false, superOpen = false, wallKick = false;
                bool superBuffer = false, superHyper = false, superUltra = false, superFloat = false;
                bool preGround = pre.Ground, preDashing = pre.Dash, preSpring = pre.Cutscene == Movement.cutsceneModes.spring;
                foreach (TechEvent e in step.Calls)
                {
                    MoveState m = e.State;
                    switch (e.Call)
                    {
                        case TechCall.Respawn:
                            respawnTick = tick;
                            preGround = true;
                            preDashing = preSpring = false;
                            break;
                        case TechCall.Input:
                            input = m;
                            inputOpen = true;
                            superOpen = false;
                            wallKick = false;
                            break;
                        case TechCall.Super:
                            super = m;
                            superOpen = true;
                            inputOpen = false;
                            superBuffer = m.Buffer;
                            superHyper = m.CanHyper;
                            float lifted = m.SceneY + 2f;
                            superUltra = Math.Sign(m.PreDashX) == Math.Sign(m.DashX) && lifted < m.LastHyperY - 4f;
                            superFloat = superUltra && dashInWave && lifted - (m.SceneY - m.Y) >= m.LastHyperY - lastHyperOriginY - 4f;
                            break;
                        case TechCall.Kick:
                            wallKick = true;
                            if (tick - inwallLast <= InwallChainTicks)
                            {
                                pending.Add(("InwallWallBounce", inwallStart));
                                inwallLast = NoTick;
                            }
                            else if (e.Value >= 1f) pending.Add(("WallBounce", dashStart));
                            else
                            {
                                pending.Add(("Superbounce", dashStart));
                                lastSuperbounce = tick;
                            }
                            if (!m.Wall && m.WallCoyote > 0f) pending.Add(("StoredWallJump", lastWall + 1));
                            break;
                        case TechCall.Intangible:
                            pending.Add(("ZipIntangible", tick));
                            break;
                        case TechCall.Sprite:
                            if (inputOpen)
                            {
                                inputOpen = false;
                                if (!input.Buffer || m.Buffer || wallKick) break;
                                if (m.FinalCling) pending.Add(("FinalClingFloat", tick));
                                if (m.WallJumps < input.WallJumps)
                                {
                                    if (!input.Wall && input.WallCoyote > 0f) pending.Add(("StoredWallJump", lastWall + 1));
                                    else if (input.WallCoyote < run.MaxWallCoyote) pending.Add(("WallCoyoteJump", lastWall + 1));
                                    else if (input.Wall && Math.Sign(m.Vx) == Math.Sign(input.RaysX)) pending.Add(("ReverseWallJump", tick));
                                }
                                else if (m.AirJumps < input.AirJumps)
                                {
                                    if (input.Vy >= 200f)
                                    {
                                        if (tick - lastSuperbounce <= 2) pending.Add(("SuperbounceDoubleJump", lastSuperbounce));
                                        else pending.Add(("BoostedDoubleJump", tick));
                                    }
                                }
                                else if (input.Ground)
                                {
                                    if (input.DashBounce > 0)
                                    {
                                        if (dashInWave) pending.Add(("WaveBounce", waveStart));
                                        else if (dashInSuper) pending.Add(("SuperGroundBounce", superStart));
                                        else pending.Add(("DashBounce", dashStart));
                                    }
                                    else if (input.Coyote < run.MaxCoyote)
                                    {
                                        if (respawnTick != NoTick && lastGround <= respawnTick) pending.Add(("CheckpointCoyoteJump", respawnTick + 1));
                                        else if (dashAtLastGround) pending.Add(("DashCoyoteJump", dashStart));
                                        else pending.Add(("CoyoteJump", lastGround + 1));
                                    }
                                    else if (!preGround && endVy > 0f && tick - lastFlip <= FlipWindowTicks)
                                        pending.Add((flipStick >= 1f ? "KbWallClip" : "GcWallClip", flipStart));
                                }
                            }
                            else if (superOpen)
                            {
                                superOpen = false;
                                if (!superBuffer || m.Buffer) break;
                                if (superHyper)
                                {
                                    if (dashInWave) pending.Add(("Ultra", waveStart));
                                    else
                                    {
                                        pending.Add(("Wavedash", dashStart));
                                        waveStart = dashStart;
                                    }
                                    waveAir = true;
                                    if (superUltra) pending.Add((superFloat ? "FloatUltra" : "UltraBonus", dashStart));
                                }
                                else if (backwardsDash == dashStart && dashStart != NoTick) pending.Add(("BackwardsDashJump", backwardsStart));
                                else
                                {
                                    float stick = super.MoveX;
                                    pending.Add((super.DashY > 0f ? "HighSuper"
                                        : stick != 0f && super.DashX != 0f && Math.Sign(stick) != Math.Sign(super.DashX) ? "ReversedSuper" : "SuperJump", dashStart));
                                    superAir = true;
                                    superStart = dashStart;
                                }
                            }
                            break;
                    }
                }

                MoveState after = step.After;
                pauselifted = false;
                if (preSpring && !wasSpring) springStart = tick;
                wasSpring = preSpring;
                if (preSpring && step.DashPress && after.Cutscene == Movement.cutsceneModes.none) springCancel = tick;
                if (after.Right != facing)
                {
                    if (tick - lastFlip > FlipWindowTicks)
                    {
                        flipStart = tick;
                        flipStick = Math.Abs(after.MoveX);
                    }
                    lastFlip = tick;
                    facing = after.Right;
                }
                if (after.Ground && after.Coyote >= run.MaxCoyote)
                {
                    lastGround = tick;
                    dashAtLastGround = after.Dash;
                }
                if (after.Wall && after.WallCoyote >= run.MaxWallCoyote) lastWall = tick;
                if (after.Dash && !preDashing && after.SinceDash == 0f)
                {
                    dashStart = tick;
                    dashInWave = waveAir;
                    dashInSuper = superAir;
                    if (tick - springCancel <= 1) Add("SpringDash", springStart, tick);
                    if (Math.Abs(after.DashX) == 1f && after.DashY != 0f) Add("WallDash", tick, tick);
                    if (step.JumpPress && preGround && after.DashX != 0f && tick - lastFlip <= FlipWindowTicks
                        && (after.DashX > 0f ? step.WallLeft : step.WallRight))
                    {
                        backwardsDash = tick;
                        backwardsStart = flipStart;
                    }
                }
                if (after.Dash && after.SinceDash == 1f && tick - dashStart == 1 && after.DashX != 0f && after.DashY != 0f && Math.Abs(after.DashX) < 0.7f)
                    Add("FreezeAim", dashStart, tick);
                if (after.Ground && !after.Dash) waveAir = superAir = false;
                foreach ((string tech, int start) in pending) Add(tech, start, tick);
                pending.Clear();
                // A spike kills on its second stay: a graze is touching and living on.
                if (step.Spike && !Dies(steps, i, 3)) Add("SpikeGrace", tick, tick);
                endVy = after.Vy;
                if (after.LastHyperY != lastHyperY)
                {
                    lastHyperY = after.LastHyperY;
                    lastHyperOriginY = after.SceneY - after.Y;
                }

                foreach (TechEvent e in step.Calls)
                {
                    if (e.Call != TechCall.StepUp) continue;
                    float rays = e.State.RaysX;
                    if (rays == 0f || rays > 0f == e.State.Right) continue;
                    if (tick - inwallLast > InwallChainTicks) inwallStart = tick;
                    inwallLast = tick;
                    if ((step.Events & StepEvents.Paused) != 0 && !pauselifted)
                    {
                        pauselifted = true;
                        Add("Pauselift", inwallStart, tick);
                    }
                }
            }
            return spans;
        }

        internal static bool Dies(IReadOnlyList<TechStep> steps, int i, int ahead)
        {
            for (int k = i; k < steps.Count && k <= i + ahead; k++) if (steps[k].Died) return true;
            return false;
        }
    }
}
