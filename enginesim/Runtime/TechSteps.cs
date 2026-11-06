using System;
using System.Collections.Generic;
using HarmonyLib;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public struct MoveState
    {
        public float X, Y, Vx, Vy, Mx, My, MoveX, MoveY;
        public bool Ground, Wall, Dash, Buffer, CanHyper, Right, FinalCling, WallBounce, Dead;
        public float Coyote, WallCoyote, SinceDash, LastHyperY, SceneY;
        public int AirJumps, WallJumps, AirDashes, DashBounce;
        public float DashX, DashY, PreDashX, PreDashY, RaysX;
        public float ColliderW, ColliderH;
        public int Squished;
        public Movement.cutsceneModes Cutscene;

        static readonly AccessTools.FieldRef<Movement, bool> JumpBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpBuffer"),
            CanHyperField = AccessTools.FieldRefAccess<Movement, bool>("canHyper"), FinalClingField = AccessTools.FieldRefAccess<Movement, bool>("isFinalWallClingActive"),
            WallBounceField = AccessTools.FieldRefAccess<Movement, bool>("wallBouncePossible"), DeadField = AccessTools.FieldRefAccess<Movement, bool>("isDead");
        static readonly AccessTools.FieldRef<Movement, float> CoyoteField = AccessTools.FieldRefAccess<Movement, float>("coyoteFrames"),
            WallCoyoteField = AccessTools.FieldRefAccess<Movement, float>("wallCoyoteFrames"), LastHyperField = AccessTools.FieldRefAccess<Movement, float>("lastHyperY");
        static readonly AccessTools.FieldRef<Movement, int> DashBounceField = AccessTools.FieldRefAccess<Movement, int>("DashBounceFrames"),
            SquishedField = AccessTools.FieldRefAccess<Movement, int>("framesSquished");
        static readonly AccessTools.FieldRef<Movement, BoxCollider2D> NormalCollider = AccessTools.FieldRefAccess<Movement, BoxCollider2D>("normalCollider");
        static readonly AccessTools.FieldRef<Movement, Vector2> DashDirection = AccessTools.FieldRefAccess<Movement, Vector2>("dashDirection"),
            PreDashMomentum = AccessTools.FieldRefAccess<Movement, Vector2>("preDashMomentum"), WallJumpFacing = AccessTools.FieldRefAccess<Movement, Vector2>("wallJumpFacing");

        public static MoveState Of(Simulation sim, Movement m)
        {
            WorldPos p = sim.PlayerWorld;
            Vector2 dash = DashDirection(m), pre = PreDashMomentum(m), size = NormalCollider(m) != null ? NormalCollider(m).size : default;
            return new MoveState
            {
                X = p.x, Y = p.y, SceneY = m.transform.position.y, Vx = m.Velocity.x, Vy = m.Velocity.y, Mx = m.momentum.x, My = m.momentum.y,
                MoveX = m.MoveAxis.x, MoveY = m.MoveAxis.y,
                Ground = m.onGround, Wall = m.OnWall, Dash = m.dashActive, Buffer = JumpBuffer(m), CanHyper = CanHyperField(m), Right = m.facingRight,
                FinalCling = FinalClingField(m), WallBounce = WallBounceField(m), Dead = DeadField(m),
                Coyote = CoyoteField(m), WallCoyote = WallCoyoteField(m), SinceDash = m.framesSinceLastDash, LastHyperY = LastHyperField(m),
                AirJumps = m.airJumpsLeft, WallJumps = m.wallJumpsLeft, AirDashes = m.airDashesLeft, DashBounce = DashBounceField(m),
                DashX = dash.x, DashY = dash.y, PreDashX = pre.x, PreDashY = pre.y, RaysX = WallJumpFacing(m).x, Cutscene = m.cutsceneMode,
                ColliderW = size.x, ColliderH = size.y, Squished = SquishedField(m),
            };
        }
    }

    public enum TechCall : byte
    {
        Input,
        Sprite,
        Super,
        Kick,
        StepUp,
        Intangible,
        Respawn,
    }

    public struct TechEvent
    {
        public TechCall Call;
        public float Value;
        public MoveState State;
    }

    public sealed class TechStep
    {
        public int Tick, Index;
        public long Physics;
        public readonly List<TechEvent> Calls = new List<TechEvent>();
        public StepEvents Events;
        public bool JumpPress, DashPress;
        public bool Spike;
        public bool Died;
        public List<string> Boxes;
        public bool WallLeft, WallRight;
        public bool SpikeUnder;
        public bool Timing;
        public float SpringX, SpringY;
        public float Overhang, Slope;
        public bool Swapped, Blue;
        public MoveState After;
    }

    public sealed class TechSteps
    {
        readonly Simulation _sim;
        static readonly AccessTools.FieldRef<courseScript, bool> CourseTracking = AccessTools.FieldRefAccess<courseScript, bool>("tracking");
        const int SpikeLayer = 6;
        public readonly List<TechStep> Steps = new List<TechStep>();
        public float MaxCoyote, MaxWallCoyote;
        TechStep _now = new TechStep();
        int _jumps, _dashes;
        List<TechChecks.Span> _spans;
        Action<Movement> _input;

        public TechSteps(Simulation sim) { _sim = sim; }

        public List<TechChecks.Span> Spans => _spans ??= TechChecks.Detect(this);

        public void Attach()
        {
            _input = m => Add(TechCall.Input, m);
            _sim.BeforeInput = _input + _sim.BeforeInput;
            _sim.AfterInput = m => Add(TechCall.Sprite, m);
            _sim.SuperJumpCheck = m => Add(TechCall.Super, m);
            _sim.WallKick = mod => Add(TechCall.Kick, _sim.Player, mod);
            _sim.SteppedUp = m => Add(TechCall.StepUp, m);
            _sim.Intangible = _ => Add(TechCall.Intangible, _sim.Player);
            _sim.Respawned = m => Add(TechCall.Respawn, m);
            _sim.Swapped = blue => { _now.Swapped = true; _now.Blue = blue; };
            _sim.BoxTouched = (box, collision) =>
            {
                if (collision == null || collision.gameObject != _sim.Player.gameObject || !box.isActive || !box.isActiveAndEnabled) return;
                string id = _sim.Shop.Id(box);
                (_now.Boxes ??= new List<string>()).Remove(id);
                _now.Boxes.Add(id);
            };
        }

        public void Detach()
        {
            _sim.BeforeInput -= _input;
            _sim.AfterInput = _sim.SuperJumpCheck = _sim.SteppedUp = _sim.Respawned = null;
            _sim.WallKick = null;
            _sim.Swapped = null;
            _sim.Intangible = null;
            _sim.BoxTouched = null;
        }

        public void Clear()
        {
            Steps.Clear();
            _spans = null;
            _now = new TechStep();
            _jumps = _sim.Engine.JumpPresses;
            _dashes = _sim.Engine.DashPresses;
            MaxCoyote = _sim.Player.maxCoyoteFrames;
            MaxWallCoyote = _sim.Player.maxWallCoyoteFrames;
        }

        void Add(TechCall call, Movement m, float value = 0f)
        {
            _now.Calls.Add(new TechEvent { Call = call, Value = value, State = MoveState.Of(_sim, m) });
        }

        public void Step()
        {
            _spans = null;
            Movement m = _sim.Player;
            TechStep s = _now;
            s.Index = Steps.Count;
            s.Tick = (int)_sim.Engine.RunTicks;
            s.Physics = _sim.Engine.PhysicsTicks;
            s.Events = _sim.Events;
            s.JumpPress = _sim.Engine.JumpPresses != _jumps;
            s.DashPress = _sim.Engine.DashPresses != _dashes;
            _jumps = _sim.Engine.JumpPresses;
            _dashes = _sim.Engine.DashPresses;
            PhysicsWorld.PlayerTouches touch = _sim.Physics.PlayerTouch;
            s.Spike = touch.Spike;
            s.Overhang = touch.Overhang;
            s.Slope = touch.Slope;
            s.Timing = _sim.TargetCourse < 0 ? Array.Exists(_sim.Courses, c => CourseTracking(c)) : _sim.TimingCourse() != null;
            if ((s.Events & StepEvents.SpringHit) != 0) { s.SpringX = _sim.SpringUp.x; s.SpringY = _sim.SpringUp.y; }
            s.After = MoveState.Of(_sim, m);
            if (m.dashActive && m.framesSinceLastDash == 0f)
            {
                Vector3 p = m.transform.position;
                int layer = ContactRays.GroundLayer(m);
                s.WallLeft = Physics2D.Raycast(new Vector2(p.x, p.y), Vector2.left, 40f, layer);
                s.WallRight = Physics2D.Raycast(new Vector2(p.x, p.y), Vector2.right, 40f, layer);
            }
            if (m.onGround && !s.Spike)
            {
                Vector3 p = m.transform.position;
                RaycastHit2D under = Physics2D.Raycast(new Vector2(p.x, p.y - 18f), Vector2.down, 22f, ContactRays.GroundLayer(m) | 1 << SpikeLayer);
                s.SpikeUnder = under && under.collider.GetComponent<spikeScript>() != null;
            }
            Steps.Add(s);
            _now = new TechStep();
        }

        public void End(RunResult run)
        {
            if (Steps.Count > 0 && run.EndReason != null && run.EndReason.StartsWith("Player death", StringComparison.Ordinal)) Steps[Steps.Count - 1].Died = true;
        }
    }
}
