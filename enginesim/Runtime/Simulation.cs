using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>What the player did in one physics step (its FixedUpdate, physics callbacks and rendered frames), from the game's
    /// own calls: the tech contacts <see cref="ContactLog.Step"/> files (Runtime\TechGrafts.cs).</summary>
    [Flags]
    public enum StepEvents
    {
        None = 0,
        /// <summary>An air jump spent (playSFX "airJump").</summary>
        DoubleJump = 1,
        /// <summary>A jump out of a dash: CheckForSuperJump's super or hyper (playSFX "bigJump" with dashActive still up), or a
        /// ground bounce's jump (DashBounceFrames up).</summary>
        DashJump = 2,
        /// <summary>Movement.hitSpring (<see cref="Simulation.SpringUp"/>: the spring's up).</summary>
        SpringHit = 4,
        /// <summary>Movement.SpecialWallJump: a wallbounce, a dash into a wall with the jump buffered, a dash-boosted wall jump.</summary>
        WallBoost = 8,
        /// <summary>The ground rays touched ground while onGround was off (touchedSolidGround/touchedMossyGround(true)).</summary>
        Landed = 16,
        /// <summary>The pause menu held open after this step (a route's pause: MacroStep.pauseFrames).</summary>
        Paused = 32,
        /// <summary>Movement.Update's step-up lifted the player onto a step in one of the step's rendered frames.</summary>
        SteppedUp = 64,
    }

    public struct InputTick : IEquatable<InputTick>
    {
        public float x, y;
        public bool held, press, release, dash;
        public int dashJump;
        public int springDash;
        public float? lockX, lockY;
        /// <summary>On a dash tick: the stick held in the dash's freeze, before the next tick (MacroStep.dashTurnX/dashTurnY; y
        /// only turns an omnidash).</summary>
        public float? turnX, turnY;
        public bool reset;
        /// <summary>The pause menu held open for this many frames after the tick's <see cref="pauseTick"/>-th physics tick and
        /// <see cref="pauseFrame"/> Update frames of it (MacroStep.pauseFrames/pauseTick/pauseFrame): the pauselift.</summary>
        public int pause, pauseTick, pauseFrame;

        public bool ArmsLock { get { return springDash != 0 || lockX.HasValue || lockY.HasValue; } }

        /// <summary>The tick after this one with the same stick and button, no new edge.</summary>
        public InputTick Coast() { return new InputTick { x = x, y = y, held = held && !release }; }

        public bool Equals(InputTick o) { return x == o.x && y == o.y && held == o.held && press == o.press && release == o.release && dash == o.dash && dashJump == o.dashJump && springDash == o.springDash && lockX == o.lockX && lockY == o.lockY && turnX == o.turnX && turnY == o.turnY && reset == o.reset && pause == o.pause && pauseTick == o.pauseTick && pauseFrame == o.pauseFrame; }
        public override bool Equals(object o) { return o is InputTick t && Equals(t); }
        public override int GetHashCode() { return HashCode.Combine(x, y, held, press, release, dash, dashJump, HashCode.Combine(springDash, lockX, lockY, turnX, turnY, reset, pause, HashCode.Combine(pauseTick, pauseFrame))); }

        public static InputTick FromJson(JsonElement t)
        {
            return new InputTick
            {
                x = t.GetProperty("x").GetSingle(),
                y = t.TryGetProperty("y", out JsonElement y) ? y.GetSingle() : 0f,
                held = t.GetProperty("held").GetBoolean(),
                press = t.GetProperty("press").GetBoolean(),
                release = t.GetProperty("release").GetBoolean(),
                dash = t.GetProperty("dash").GetBoolean(),
                dashJump = t.GetProperty("dashJump").GetInt32(),
                springDash = t.TryGetProperty("springDash", out JsonElement springDash) ? springDash.GetInt32() : 0,
                lockX = t.TryGetProperty("lockX", out JsonElement lockX) && lockX.ValueKind == JsonValueKind.Number ? lockX.GetSingle() : null,
                lockY = t.TryGetProperty("lockY", out JsonElement lockY) && lockY.ValueKind == JsonValueKind.Number ? lockY.GetSingle() : null,
                turnX = t.TryGetProperty("turnX", out JsonElement turnX) && turnX.ValueKind == JsonValueKind.Number ? turnX.GetSingle() : null,
                turnY = t.TryGetProperty("turnY", out JsonElement turnY) && turnY.ValueKind == JsonValueKind.Number ? turnY.GetSingle() : null,
                reset = t.TryGetProperty("reset", out JsonElement reset) && reset.GetBoolean(),
                pause = t.TryGetProperty("pause", out JsonElement pause) && pause.ValueKind == JsonValueKind.Number ? pause.GetInt32() : 0,
                pauseTick = t.TryGetProperty("pauseTick", out JsonElement pauseTick) && pauseTick.ValueKind == JsonValueKind.Number ? pauseTick.GetInt32() : 0,
                pauseFrame = t.TryGetProperty("pauseFrame", out JsonElement pauseFrame) && pauseFrame.ValueKind == JsonValueKind.Number ? pauseFrame.GetInt32() : 0,
            };
        }

        public object ToJson() { return new { x, y, held, press, release, dash, dashJump, springDash, lockX, lockY, turnX, turnY, reset, pause, pauseTick, pauseFrame }; }
    }
    public sealed class RunResult
    {
        public bool Finished;
        public float Time;
        public int FinishTick;
        public long PhysicsTicks;
        /// <summary>Frames the run stood still in (dash freezes, pauses): its live time is PhysicsTicks and these (MacroEngine.LiveSeconds).</summary>
        public long HeldFrames;
        public int Stepped;
        public string EndReason;
        public int Progress;
        public long[] GoalInputTicks, GoalPhysicsTicks, GoalHeldFrames;
    }

    public sealed class SimSnapshot
    {
        public int Tick;
        public object[] Movement, Engine;
        public object[][] Courses;
        public Vector3 Position, LocalScale;
        public Quaternion Rotation;
        public (Vector2 offset, Vector2 size, int exclude, bool enabled)[] Colliders;
        public (int due, MonoBehaviour target, string method, float repeat)[] Pending;
        public object Physics;
        public (MonoBehaviour target, object[] fields)[] Touched;
        public (GameObject target, bool active)[] Active;
        public (Behaviour target, bool enabled)[] Enabled;
        /// <summary>Layers changed since load (PlatformMover.MakeIntangible), and the coroutines parked on a yield.</summary>
        public (GameObject target, int layer)[] Layers;
        /// <summary>Transforms a script moved since load (a tree segment's trunks and box, IE_Grow), their pose now.</summary>
        internal (Transform target, PhysicsWorld.Pose pose)[] Transforms;
        public (MonoBehaviour target, System.Collections.IEnumerator body, Coroutine handle, Scheduler.Wait wait, int due)[] Routines;
        internal Shop.State Economy;
        internal FullTasTracker.State Goal;
        public UnityEngine.Random.State Random;
        public int Waypoints;
        public double VmanTimeLeft;
        /// <summary>The FloatingOrigin rebases so far (each MoveOrigin's reference position, in order), and one waiting for the next physics step.</summary>
        public Vector3[] Rebases;
        public Vector3 RebasePending;
        public bool RebaseDue;
        /// <summary>The player body's position at the start and end of the last physics step (its interpolation's ends).</summary>
        public Vector3 StepFrom, StepTo;
        /// <summary>The player animator's "Animation": Movement.FixedUpdate plays a sound (an RNG draw) leaving a wall cling (3).</summary>
        public int Animation;
        /// <summary>Each refill orb on camera after the last physics step (Simulation.SeeOrbs), in the world's orb order.</summary>
        public bool[] OrbSeen;

        /// <summary>This state with another goal tracker's progress (a search for goals the run doesn't list: <see cref="Simulation.UseGoals"/>).</summary>
        internal SimSnapshot WithGoal(FullTasTracker.State goal)
        {
            var s = (SimSnapshot)MemberwiseClone();
            s.Goal = goal;
            return s;
        }
    }

    public sealed partial class Simulation
    {
        // Read every tick: field references, as FieldInfo.GetValue boxes each read.
        static readonly AccessTools.FieldRef<courseScript, bool> Tracking = AccessTools.FieldRefAccess<courseScript, bool>("tracking");
        static readonly AccessTools.FieldRef<courseScript, float> PathTime = AccessTools.FieldRefAccess<courseScript, float>("currentPathTime");
        static readonly MethodInfo FixedUpdateMethod = typeof(Movement).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo RespawnMethod = typeof(Movement).GetMethod("respawn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo UpdateMethod = typeof(Movement).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo CourseFixedUpdateMethod = typeof(courseScript).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly string[] CoursePathLists = { "currentPlayerPath", "currentPlayerSprites", "currentPlayerScales" };
        static readonly MethodInfo MoverOnEnable = typeof(PlatformMover).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo OvergrowthCourses = typeof(OvergrowthLoadScript).GetField("courses", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo OrbFixedUpdateMethod = typeof(JiggleDropScript).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly AccessTools.FieldRef<JiggleDropScript, float> OrbCooldown = AccessTools.FieldRefAccess<JiggleDropScript, float>("cooldown");
        static readonly AccessTools.FieldRef<JiggleDropScript, int> OrbSequence = AccessTools.FieldRefAccess<JiggleDropScript, int>("ActivationSequence");
        static readonly AccessTools.FieldRef<JiggleDropScript, bool> OrbUsable = AccessTools.FieldRefAccess<JiggleDropScript, bool>("usable");

        public readonly World World;
        public readonly Movement Player;
        public readonly courseScript[] Courses;
        internal readonly MacroEngine Engine = new MacroEngine { Quiet = true };
        public readonly Scheduler Scheduler = new Scheduler();
        public readonly PhysicsWorld Physics;
        public readonly Shop Shop;
        readonly Action _fixedUpdate, _update;
        readonly Action[] _courseFixedUpdates;
        readonly JiggleDropScript[] _orbs;
        readonly Action[] _orbFixedUpdates;
        readonly bool[] _orbSeen;
        readonly System.Collections.IList[][] _coursePathLists;
        readonly BoxCollider2D[] _playerColliders;
        SimSnapshot _start;
        bool _finished;
        float _finishTime;
        string _endReason;
        readonly Dictionary<MonoBehaviour, object[]> _pristine = new Dictionary<MonoBehaviour, object[]>();
        readonly HashSet<MonoBehaviour> _touched = new HashSet<MonoBehaviour>(), _restoring = new HashSet<MonoBehaviour>();
        readonly Dictionary<object, object[]> _captured = new Dictionary<object, object[]>();

        object[] CaptureShared(object target)
        {
            if (_captured.TryGetValue(target, out object[] last) && FieldState.Matches(target, last)) return last;
            return ShareBoxes(target, FieldState.Capture(target));
        }

        // A capture's boxed value fields that are bit for bit the target's last capture's become that capture's boxes: a
        // state holds ~160 boxed Movement fields and few change between captures. Bits, not Equals: -0 isn't 0.
        object[] ShareBoxes(object target, object[] values)
        {
            if (_captured.TryGetValue(target, out object[] last))
                for (int i = 0; i < values.Length; i++)
                    if (values[i] is ValueType v && last[i] is ValueType l && v.GetType() == l.GetType() && SameBits(v, l))
                    {
                        values[i] = l;
                        SharedBytes += (BoxSizes[v.GetType()] + 16 + 7) & ~7;
                    }
            return _captured[target] = values;
        }

        /// <summary>The bytes of the boxes <see cref="ShareBoxes"/> dropped for the last capture's, since the simulation was made: a
        /// capture allocated them, but no state keeps them.</summary>
        public long SharedBytes;

        sealed class RawBox { public byte Data; }
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, int> BoxSizes = new System.Collections.Concurrent.ConcurrentDictionary<Type, int>();

        static bool SameBits(object a, object b)
        {
            int size = BoxSizes.GetOrAdd(a.GetType(), t => (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf").MakeGenericMethod(t).Invoke(null, null));
            return System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.As<RawBox>(a).Data, size)
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref System.Runtime.CompilerServices.Unsafe.As<RawBox>(b).Data, size));
        }
        readonly Dictionary<GameObject, bool> _activeAsLoaded = new Dictionary<GameObject, bool>();
        readonly Dictionary<Behaviour, bool> _enabledAsLoaded = new Dictionary<Behaviour, bool>();
        readonly Dictionary<GameObject, int> _layerAsLoaded = new Dictionary<GameObject, int>();
        // Transforms a script moved since load, each with its pose as loaded and its index in _moved (-1: a rebase doesn't move
        // it). The player's is RestorePose's, a body's the physics snapshot's, and a root's position is the rebases' (no script
        // the engine runs moves a root), so none of those is tracked.
        readonly Dictionary<Transform, (PhysicsWorld.Pose loaded, int moved)> _poseAsLoaded = new Dictionary<Transform, (PhysicsWorld.Pose, int)>();
        double _vmanTimeLeft;
        bool _vmanBlockEnding;

        public Simulation(World world)
        {
            World = world;
            IGTAP.EngineSim.Engine.Scheduler = Scheduler;
            Player = world.All<Movement>().First(m => m.gameObject.activeInHierarchy && !(bool)typeof(Movement).GetField("isOnMainMenu",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(m));
            Courses = world.All<courseScript>().Where(c => c.gameObject.activeInHierarchy).ToArray();
            Engine.RefreshRewards = () =>
            {
                if (Goals == null || Shop.Economy == null) return;
                foreach (courseScript c in World.All<courseScript>()) Shop.Economy.RefreshReward(c);
            };
            _floating = world.All<FloatingOrigin>().FirstOrDefault();
            Origin = _floating?.currentOrigin ?? Vector3.zero;
            _rebaseThreshold = _floating?.Threshold ?? float.PositiveInfinity;
            _rebase2D = _floating?.Use2DDistance ?? false;
            var moved = new List<Transform>();
            foreach (Transform root in world.MovedRoots) CollectTree(root, moved);
            _moved = moved.ToArray();
            _movedPristine = _moved.Select(t => (t.m_Position, t.m_LocalPosition)).ToArray();
            Physics = new PhysicsWorld(world, this);
            IGTAP.EngineSim.Engine.Physics = Physics;
            IGTAP.EngineSim.Engine.ActiveChanging = g => _activeAsLoaded.TryAdd(g, g.activeSelf);
            IGTAP.EngineSim.Engine.EnabledChanging = b => _enabledAsLoaded.TryAdd(b, b.enabled);
            IGTAP.EngineSim.Engine.LayerChanging = g => _layerAsLoaded.TryAdd(g, g.layer);
            IGTAP.EngineSim.Engine.TransformChanging = t =>
            {
                if (t.m_Parent != null && t != Player.transform && !_poseAsLoaded.ContainsKey(t) && t.m_GameObject.GetComponent<Rigidbody2D>() == null)
                    TrackTransform(t);
            };
            IGTAP.EngineSim.Engine.Activated = g =>
            {
                if (Physics.Suspended) return;
                Economy.Activate(g, Goals != null);
                Shop.Economy?.Woken(g);
                // The other OnEnables with an effect: a zip mover's JumpToState (its pose and its reset coroutine,
                // PlatformMover.cs:221-233) and the atom collider's grid (AtomColliderSystem.cs:111-115).
                foreach (PlatformMover m in g.GetComponentsInChildren<PlatformMover>(false))
                    if (m.enabled)
                    {
                        Touch(m);
                        MoverOnEnable.Invoke(m, null);
                    }
                foreach (AtomColliderSystem a in g.GetComponentsInChildren<AtomColliderSystem>(false))
                    if (a.enabled) Shop.Economy?.GridStart(a);
            };
            Hooks.OvergrowthLoad = script =>
            {
                foreach (courseScript c in (courseScript[])OvergrowthCourses.GetValue(script) ?? Array.Empty<courseScript>())
                    if ((object)c != null) foreach (upgradeBox b in c.transform.GetComponentsInChildren<upgradeBox>(true)) Touch(b);
            };
            // PlatformMover's coroutines are gameplay: MakeIntangible's 0.15 s off the Ground layer after a jump off a zip mover,
            // PlayerExit's contact clear two physics steps after the player leaves it, a zip mover's move once unlocked.
            // TreeSegmentActivationComponent.IE_Grow (Shop.GrowTree): the segment's ground off until its trunks have grown, its box
            // falling into place.
            // ZoneLoader.IE_Load: a zone activator's load, two yields after its trigger (zoneChanger.OnTriggerEnter2D).
            Scheduler.Continues = t => t is PlatformMover || t is TreeSegmentActivationComponent || t is ZoneLoader || t is TeleporterScript;
            Scheduler.InUpdate = t => t is TeleporterScript;
            Hooks.Teleporter = teleporter =>
            {
                Touch((MonoBehaviour)teleporter);
                if (Singleton<globalStats>.Instance is globalStats stats) Touch(stats);
            };
            Scheduler.Stepping = Touch;
            Scheduler.Unmodeled = what => { if (Engine.IsRunning) Engine.Abort("Unmodeled: " + what); };
            Shop = new Shop(this, world);
            _fixedUpdate = (Action)Delegate.CreateDelegate(typeof(Action), Player, FixedUpdateMethod);
            _update = (Action)Delegate.CreateDelegate(typeof(Action), Player, UpdateMethod);
            _courseFixedUpdates = Courses.Select(c => (Action)Delegate.CreateDelegate(typeof(Action), c, CourseFixedUpdateMethod)).ToArray();
            _coursePathLists = Courses.Select(c => CoursePathLists
                .Select(n => typeof(courseScript).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c) as System.Collections.IList)
                .Where(l => l != null).ToArray()).ToArray();
            _orbs = world.All<JiggleDropScript>();
            _orbFixedUpdates = _orbs.Select(o => (Action)Delegate.CreateDelegate(typeof(Action), o, OrbFixedUpdateMethod)).ToArray();
            _orbSeen = new bool[_orbs.Length];
            for (int i = 0; i < _orbs.Length; i++) _orbSeen[i] = OnCamera(_orbs[i], Player.transform.m_Position);
            _playerColliders = Player.GetComponents<BoxCollider2D>();
            Engine.AttemptEnded += (success, reason) =>
            {
                _finished = success && MacroEngine.IsFinishReason(reason);
                _finishTime = Engine.AuthoritativeCompletionTime;
                _endReason = reason;
            };
            Hooks.PlayerInput = m => { Engine.FixedTick((Movement)m); BeforeInput?.Invoke((Movement)m); };
            IGTAP.EngineSim.Engine.Stick = () => Engine.StickFor(Player);
            Hooks.WallTouch = (m, touchedGround) =>
            {
                if (touchedGround && !((Movement)m).onGround) Events |= StepEvents.Landed;
                Engine.WallTouch((Movement)m, touchedGround);
            };
            Hooks.HitSpring = (_, up) => { Events |= StepEvents.SpringHit; SpringUp = up; };
            Hooks.SpecialWallJump = (m, mod) =>
            {
                Events |= StepEvents.WallBoost;
                WallBoostSide = ((Movement)m).transform.localScale.x < 0f ? -1f : 1f;
                WallKick?.Invoke(mod);
            };
            Hooks.SteppedUp = _ => { Events |= StepEvents.SteppedUp; StepUpSide = Player.facingRight ? 1f : -1f; SteppedUp?.Invoke(Player); };
            Hooks.Death = m =>
            {
                if (!Engine.IsRunning) return;
                Engine.ClearInjectedInput((Movement)m);
                Engine.Abort("Player death at action " + (Engine.StepIndex + 1));
            };
            Hooks.CourseStop = CourseStop;
            Hooks.Respawn = m => { ((Movement)m).isRespawningAtCheckpoints = CheckpointRespawns; Respawned?.Invoke((Movement)m); };
            Hooks.UpgradeBoxStay = (box, collision) => { BoxTouched?.Invoke((upgradeBox)box, collision); Shop.Stay(box, collision); };
            Hooks.SuperJumpCheck = m => SuperJumpCheck?.Invoke((Movement)m);
            Hooks.SpriteDirection = m => AfterInput?.Invoke((Movement)m);
            Hooks.Intangible = mover => Intangible?.Invoke((PlatformMover)mover);
            Hooks.SwapBlocks = (swapper, blueActive) =>
            {
                Touch((MonoBehaviour)swapper);
                SetLabColours(blueActive);
                Swapped?.Invoke(blueActive);
            };
            Hooks.TimeStop = (_, duration) => { if (_freezeFor < 0f || duration < _freezeFor) _freezeFor = duration; };
            Hooks.RefillOrbState = RefillOrbState;
            Hooks.PlaySfx = (m, code) =>
            {
                switch (code)
                {
                    case "land":
                    case "grassLand": UnityEngine.Random.Draw(); UnityEngine.Random.Draw(); break;
                    case "bigJump":
                        if (((Movement)m).dashActive || DashBounceFrames((Movement)m) > 0) Events |= StepEvents.DashJump;
                        UnityEngine.Random.Draw();
                        break;
                    case "airJump":
                        Events |= StepEvents.DoubleJump;
                        UnityEngine.Random.Draw();
                        break;
                    case "smallJump":
                    case "wallSlide":
                    case "grassWallSlide":
                    case "dash":
                    case "death": UnityEngine.Random.Draw(); break;
                }
                UnityEngine.Random.Draw();
            };
            Hooks.PlayAudio = (_, _2, _3, pitchVariance) =>
            {
                UnityEngine.Random.Draw();
                if (pitchVariance != 0f) UnityEngine.Random.Draw();
            };
        }

        internal FullTasTracker Goals { get; private set; }
        public long InputBase;
        public int Progress { get { return Goals == null ? 0 : Goals.Next; } }
        /// <summary>The course timer of the last course finish (courseScript.stopTracking's currentPathTime): the lap's own time,
        /// whether or not it beat the course's best time.</summary>
        public float LastCourseTime;

        /// <summary>
        /// Tracks these goals from now on (an extra lap the run doesn't list, searched as its own goals). Snapshots restored after
        /// this must carry this tracker's state (<see cref="SimSnapshot.WithGoal"/>).
        /// </summary>
        internal void UseGoals(FullTasTracker goals) { Goals = goals; }

        /// <summary>
        /// A strict run's last goal was reached: it finishes this many ticks later (at a tick's end), so a box bought meanwhile (a
        /// trigger after the goal's, the frame's end, or a box the player still stands in once its 0.2 s cooldown is over) still
        /// ends it. Stopping at the goal would hide that purchase from the search, and the next piece would make it.
        /// </summary>
        internal const int StrictFinishTicks = 12;
        int _finishIn = -1;
        float _finishTimeAtGoal;

        void GoalEvent(bool reached)
        {
            if (!reached || !Goals.Done || !Engine.IsRunning) return;
            // A Full-TAS run's time is live time: its ticks and the frames it stood still in (README ### Rules 31).
            long held = TickTimed ? 0 : Engine.HeldFrames;
            if (!StrictBuys) Engine.CompleteFromGame(MacroEngine.FullTasFinishReason, MacroEngine.LiveSeconds(Engine.PhysicsTicks, held));
            else if (_finishIn < 0)
            {
                _finishIn = StrictFinishTicks;
                _finishTimeAtGoal = MacroEngine.LiveSeconds(Engine.PhysicsTicks, held);
            }
        }

        /// <summary>
        /// A purchase that isn't the next goal's box ends the run (the Full-TAS loop builder's pieces: a walk that also buys another
        /// box spends Cash and levels the plan counted on, and can cap a box a later goal buys). Not on a loose-buys run
        /// (runs/&lt;run&gt;.json "buys": "loose", <see cref="FullTasTracker.LooseBuys"/>): there any purchase stands.
        /// </summary>
        internal bool StrictBuys;
        /// <summary>A course run's goals timed in physics ticks, as its record (`solve --auto`, FullTas.TickTiming): the frames it
        /// stood still in don't count in its finish time, which a search ranks its finds by.</summary>
        internal bool TickTimed;
        /// <summary>Every purchase of a Full-TAS run (the box's path), goal or not: `fulltas --check` lists them.</summary>
        internal Action<string> Purchased;

        /// <param name="continuing">A buy-max box's later levels, one per physics step from the touch's own (a prestige-boosted
        /// course's boxes): they are the touch's own purchase, so they neither reach a goal nor count as a stray buy. One buy goal
        /// per touch of such a box.</param>
        internal void Bought(string path, string id, bool continuing)
        {
            if (Goals == null || Driving || !Engine.IsRunning || continuing) return;
            Purchased?.Invoke(path);
            FullTasGoal next = Goals.Current;
            if (StrictBuys && !Goals.Loose && (next?.boxes == null || Array.IndexOf(next.boxes, id) < 0 && Array.IndexOf(next.boxes, path) < 0))
            {
                Engine.Abort("Bought " + path + ", which isn't the next goal's box");
                return;
            }
            GoalEvent(Goals.Bought(path, id, InputBase + Engine.RunTicks, Engine.PhysicsTicks, Engine.HeldFrames));
        }

        public SimSnapshot Advance(IReadOnlyList<InputTick> prefix)
        {
            if (prefix.Count == 0) return _start;
            SimSnapshot at = null;
            var inputs = prefix.Concat(new[] { prefix[prefix.Count - 1] }).ToList();
            Run(inputs, null, tick =>
            {
                if (tick < prefix.Count) return true;
                Engine.RebaseInputs();
                at = Capture(0);
                return false;
            }, 1000000);
            return at;
        }

        /// <summary>
        /// The state where goal <paramref name="goal"/> (1-based) is reached replaying <paramref name="inputs"/> from the start: the end
        /// of the physics tick that reaches it, an input's tick or one of the ticks after an input that take none (a dash's, a
        /// spring's; TouchGrass-ANY%'s goals 478-479 come in a 269-tick fall). <see cref="Advance"/> stops where the next input is
        /// asked, before such ticks. Rebased as Advance's: from it the next input played is inputs[<paramref name="consumed"/>],
        /// after the rest of a no-input stretch. The goal never ends the run here (the tracker's Limit is past it while it plays),
        /// so a run's last goal is a start too. Null when the run stops or its inputs end first.
        /// </summary>
        public SimSnapshot AdvanceToGoal(IReadOnlyList<InputTick> inputs, int goal, out int consumed)
        {
            consumed = 0;
            if (goal <= 0) return _start;
            SimSnapshot at = null;
            int used = 0, limit = Goals.Limit;
            Goals.Limit = Math.Max(limit, goal + 1);
            try
            {
                Run(inputs, null, null, 1000000, _ =>
                {
                    if (at != null || !Engine.IsRunning || Goals.Next < goal) return;
                    used = (int)Engine.RunTicks;
                    Engine.RebaseInputs();
                    at = Capture(0);
                    Engine.Discard();
                });
            }
            finally { Goals.Limit = limit; }
            consumed = used;
            return at;
        }

        public void SetStart(SimSnapshot state, long inputBase)
        {
            _start = state;
            InputBase = inputBase;
        }

        void CourseStop(object courseObject, GameObject player, bool savePositionData)
        {
            var course = (courseScript)courseObject;
            if (!Tracking(course)) return;
            float time = PathTime(course);
            Tracking(course) = false;
            player.GetComponent<Movement>().courseResetPoint = Vector2.zero;
            if (savePositionData) LastCourseTime = time;
            if (savePositionData) Shop.CourseFinished(course, time);
            if (!(savePositionData && VmanScript.isCurrentlyVman))
            {
                foreach (string list in CoursePathLists)
                    (typeof(courseScript).GetField(list, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(course) as System.Collections.IList)?.Clear();
                PathTime(course) = 0f;
            }
            if (Goals != null)
            {
                if (savePositionData && Engine.IsRunning) GoalEvent(Goals.CourseFinished(course.courseNumber, InputBase + Engine.RunTicks, Engine.PhysicsTicks, Engine.HeldFrames));
            }
            else if (savePositionData && (TargetCourse == 0 || course.courseNumber == TargetCourse))
                Engine.CompleteFromGame("Course finish detected", time);
        }

        /// <summary>The bonus's reward box (the box nearest its end rect, TasPlugin.BonusRewardBox): it finishes on the box's hit,
        /// not the rect's touch (TasPlugin.RewardHit). Null for a transition or a bonus without one.</summary>
        string _rewardBox;

        internal bool RewardHit(upgradeBox box, Collider2D collision)
        {
            if (Driving || !Engine.IsRunning || _rewardBox == null || Shop.Id(box) != _rewardBox || collision.GetComponent<Movement>() == null) return false;
            Engine.CompleteFromGame(MacroEngine.RewardFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
            return true;
        }

        bool TouchesReward(Vector3? pose)
        {
            ScenePos at = FromCapture(TargetFinish.At);
            return Touches(at.x, at.y, TargetFinish.W, TargetFinish.H, pose);
        }

        /// <summary>
        /// The pose the plugin's box tests read (<c>TasPlugin.TouchesBox</c>, at the start of the next tick's Movement.FixedUpdate:
        /// an end box, a Full-TAS area goal), <c>Collider2D.bounds</c>: the body, not the transform. A step-up written in a tick's
        /// last frame is still pending then (<see cref="InterpolatedFrames"/>): the transform stands on the ledge, the body where
        /// the step left it, so a touch of the raised pose counts a tick later. The body's pose while one is pending, else null
        /// (the transform, which is the body's step end). Turbo measured it: a 60x6 goal only a last-frame step-up's 56 u lift
        /// reaches fired a tick after the offline engine's (docs/fidelity-log.md, *Area goals and end boxes were tested at a
        /// pending step-up's pose*).
        /// </summary>
        Vector3? BoundsPose() { return Physics.PlayerTransformDirty ? BodyPosition() : (Vector3?)null; }

        bool Touches(float x, float y, float w, float h, Vector3? pose = null)
        {
            float x0 = x - w / 2f, x1 = x + w / 2f;
            float y0 = y - h / 2f, y1 = y + h / 2f;
            foreach (BoxCollider2D box in _playerColliders)
                if (Extents(box, out float bx0, out float bx1, out float by0, out float by1, pose) && bx1 > x0 && bx0 < x1 && by1 > y0 && by0 < y1) return true;
            return false;
        }

        /// <summary>An enabled player box's extents in the scene frame (around the transform, or <paramref name="pose"/>); false for a disabled one.</summary>
        bool Extents(BoxCollider2D box, out float x0, out float x1, out float y0, out float y1, Vector3? pose = null)
        {
            x0 = x1 = y0 = y1 = 0f;
            if (!box.enabled) return false;
            Vector3 scale = box.transform.m_LossyScale;
            Vector3 center = pose ?? Player.transform.m_Position;
            center.x += box.offset.x * scale.x;
            center.y += box.offset.y * scale.y;
            float hx = box.size.x * MathF.Abs(scale.x) / 2f, hy = box.size.y * MathF.Abs(scale.y) / 2f;
            x0 = center.x - hx; x1 = center.x + hx; y0 = center.y - hy; y1 = center.y + hy;
            return true;
        }

        (float, float, float, float) PlayerBounds()
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            Vector3? pose = BoundsPose();
            foreach (BoxCollider2D box in _playerColliders)
                if (Extents(box, out float bx0, out float bx1, out float by0, out float by1, pose))
                {
                    x0 = MathF.Min(x0, bx0); x1 = MathF.Max(x1, bx1);
                    y0 = MathF.Min(y0, by0); y1 = MathF.Max(y1, by1);
                }
            return (x0, x1, y0, y1);
        }

        /// <summary>Keeps the script's fields in snapshots from now on (the player and the active courses always are: <see cref="Courses"/>).</summary>
        internal void Touch(MonoBehaviour script)
        {
            if (script == Player || script is courseScript course && Array.IndexOf(Courses, course) >= 0 || !_touched.Add(script)) return;
            if (!_pristine.ContainsKey(script)) _pristine[script] = FieldState.Capture(script);
        }

        /// <summary>A lab map's coloured shapes (<see cref="World.LabColours"/>) on for the active colour, off for the other, as
        /// swapBlocks sets the scene's coloured tilemaps (a toggle is tracked: snapshots put it back).</summary>
        public void SetLabColours(bool blueActive)
        {
            foreach (var (collider, blue) in World.LabColours)
                if (collider.enabled != (blue == blueActive)) collider.enabled = blue == blueActive;
        }

        public int TargetCourse;

        public CaptureRect TargetFinish;

        public bool Transition { get; private set; }
        public int TargetStart { get; private set; }
        courseScript _targetStartCourse;

        public bool TimedFromRunStart { get { return TargetFinish.W > 0f || Transition || Goals != null; } }

        public bool CheckpointRespawns;

        /// <summary>FloatingOrigin.currentOrigin when the world was captured: the frame of the trace's rects and goals.</summary>
        readonly Vector3 Origin;

        /// <summary>FloatingOrigin.currentOrigin now: each rebase lowers it by the position it moved the world back by.</summary>
        Vector3 CurrentOrigin { get { return _floating?.currentOrigin ?? Vector3.zero; } }

        /// <summary>The world frame's origin in the scene frame now: FloatingOrigin.currentOrigin.</summary>
        public ScenePos WorldOriginInScene { get { Vector3 o = CurrentOrigin; return new ScenePos(o.x, o.y); } }

        /// <summary>The world frame's origin in the capture's frame: FloatingOrigin.currentOrigin at the capture (<see cref="Origin"/>).</summary>
        public CapturePos WorldOriginInCapture { get { return new CapturePos(Origin.x, Origin.y); } }

        /// <summary>The player's position in the world frame (the scene position minus FloatingOrigin.currentOrigin).</summary>
        public WorldPos PlayerWorld { get { Vector3 p = Player.transform.m_Position - CurrentOrigin; return new WorldPos(p.x, p.y); } }

        /// <summary>Where a quick restart now puts the player (world frame): the course reset point while one is set, else the respawn point.</summary>
        public Vector2 RestartPoint
        {
            get
            {
                return Player.courseResetPoint != Vector2.zero
                    ? new Vector2(Player.courseResetPoint.x - CurrentOrigin.x, Player.courseResetPoint.y - CurrentOrigin.y) : Player.respawnPoint;
            }
        }

        /// <summary>Where the player was before this tick (after a quick restart's respawn), for a one-way avoid box
        /// (<see cref="OfflineSolver.Box.From"/>).</summary>
        public WorldPos PlayerStepFrom { get; private set; }

        /// <summary>The player's position in the capture's scene frame (the trace's rects, goals and map cells).</summary>
        public CapturePos PlayerAtCapture
        {
            get
            {
                if (_rebases.Length == 0) { Vector3 p = Player.transform.m_Position; return new CapturePos(p.x, p.y); }
                WorldPos world = PlayerWorld;
                return new CapturePos(world.x + Origin.x, world.y + Origin.y);
            }
        }

        /// <summary>A world point in the scene frame now (scene = world + FloatingOrigin.currentOrigin).</summary>
        public ScenePos ToScene(WorldPos w) { Vector3 o = CurrentOrigin; return new ScenePos(w.x + o.x, w.y + o.y); }

        /// <summary>A scene point now in the world frame (world = scene - FloatingOrigin.currentOrigin).</summary>
        public WorldPos ToWorld(ScenePos s) { Vector3 o = CurrentOrigin; return new WorldPos(s.x - o.x, s.y - o.y); }

        /// <summary>A capture point in the world frame (world = capture - the capture's origin).</summary>
        public WorldPos ToWorld(CapturePos c) { return new WorldPos(c.x - Origin.x, c.y - Origin.y); }

        /// <summary>A world point in the capture's frame (capture = world + the capture's origin).</summary>
        public CapturePos ToCapture(WorldPos w) { return new CapturePos(w.x + Origin.x, w.y + Origin.y); }

        /// <summary>A scene point now in the capture's frame (what <see cref="PlayerAtCapture"/> does).</summary>
        public CapturePos ToCapture(ScenePos s)
        {
            if (_rebases.Length == 0) return new CapturePos(s.x, s.y);
            Vector3 o = CurrentOrigin;
            return new CapturePos(s.x - o.x + Origin.x, s.y - o.y + Origin.y);
        }

        /// <summary>A capture point in the scene frame now (the trace's goal and finish rects are given in it).</summary>
        public ScenePos FromCapture(CapturePos c)
        {
            if (_rebases.Length == 0) return new ScenePos(c.x, c.y);
            Vector3 o = CurrentOrigin;
            return new ScenePos(c.x - Origin.x + o.x, c.y - Origin.y + o.y);
        }

        /// <summary>
        /// FloatingOrigin's rebase, as turbo and 1x run it (on by default). `ultras` turns it off: it models the rebase itself
        /// on starts placed anywhere.
        /// </summary>
        public bool Rebases = true;

        internal bool Rebased { get { return _rebases.Length > 0; } }

        readonly FloatingOrigin _floating;
        readonly float _rebaseThreshold;
        readonly bool _rebase2D;
        readonly Transform[] _moved;
        readonly (Vector3 position, Vector3 local)[] _movedPristine;
        Vector3[] _rebases = Array.Empty<Vector3>();
        Vector3 _rebasePending;
        bool _rebaseDue;

        static void CollectTree(Transform t, List<Transform> into)
        {
            into.Add(t);
            foreach (Transform child in t.m_Children) CollectTree(child, into);
        }

        /// <summary>
        /// FloatingOrigin.LateUpdate, after the tick's Update: past Threshold from scene (0, 0), the reference (the player)
        /// starts MoveOrigin, which waits for the next physics step (WaitForFixedUpdate).
        /// </summary>
        void FloatingOriginFrame()
        {
            if (!Rebases || _rebaseDue || _floating == null || !_floating.isActiveAndEnabled) return;
            Vector3 reference = (_floating.ReferenceObject ?? Player.transform).m_Position;
            if (_rebase2D) reference.y = 0f;
            if (reference.magnitude <= _rebaseThreshold) return;
            _rebasePending = reference;
            _rebaseDue = true;
        }

        /// <summary>MoveOrigin after the next physics step: every root object of the active scene moves back by the reference.</summary>
        void ResumeRebase()
        {
            if (!_rebaseDue) return;
            _rebaseDue = false;
            Vector3 by = _rebasePending;
            AddRebase(by);
            Engine.WorldMoved(by);
            Physics.WorldMoved();
        }

        /// <summary>
        /// MoveRootTransforms (`transform.position -= offset` on each root) and `currentOrigin -= offset`. A child keeps its
        /// local position and its world position moves by the same offset. Camera, background, particles and trails are
        /// presentation (cut).
        /// </summary>
        /// <summary>One more rebase: the world moved back by <paramref name="by"/>.</summary>
        void AddRebase(Vector3 by)
        {
            _rebases = [.. _rebases, by];
            MoveWorld(by);
        }

        void MoveWorld(Vector3 by)
        {
            foreach (Transform t in _moved)
            {
                t.m_Position -= by;
                if (t.m_Parent == null) t.m_LocalPosition = t.m_Position;
            }
            _floating.currentOrigin -= by;
        }

        /// <summary>
        /// Labs only (clip): the world rebased around a world point as FloatingOrigin would have done when the player stood
        /// there, and synced, so the next rebase measures from it.
        /// </summary>
        public void LabRebase(WorldPos world)
        {
            AddRebase(new Vector3(world.x + CurrentOrigin.x, world.y + CurrentOrigin.y, 0f));
            Physics.WorldMoved();
            Physics.SyncTransforms();
        }

        /// <summary>The world as a snapshot's rebases left it: from the capture's poses, each rebase again in order.</summary>
        void MaterializeRebases(Vector3[] rebases)
        {
            if (rebases.AsSpan().SequenceEqual(_rebases)) return;
            for (int i = 0; i < _moved.Length; i++) (_moved[i].m_Position, _moved[i].m_LocalPosition) = _movedPristine[i];
            _floating.currentOrigin = Origin;
            foreach (Vector3 by in rebases) MoveWorld(by);
            _rebases = rebases;
            Physics.WorldMoved();
        }

        /// <summary>A persisted snapshot's restore (SnapshotStore) re-materializes its rebases before its physics rebuilds.</summary>
        internal void PutRebases(Vector3[] rebases) { MaterializeRebases(rebases); }

        /// <summary>
        /// A persisted snapshot (SnapshotStore) comes from another simulation: its toggles can name objects this one never
        /// changed, which Restore would leave as they are (a blue block swap stored while the blue colliders were on loaded
        /// with them off, and the composite's shapes couldn't be rebuilt to the snapshot's). Tracks each such object from its
        /// value now, which is its loaded one, as the change hooks would have.
        /// </summary>
        internal void TrackToggles(SimSnapshot s)
        {
            foreach (var (g, _) in s.Active) _activeAsLoaded.TryAdd(g, g.activeSelf);
            foreach (var (b, _) in s.Enabled) _enabledAsLoaded.TryAdd(b, b.enabled);
            foreach (var (g, _) in s.Layers) _layerAsLoaded.TryAdd(g, g.layer);
            foreach (var (t, _) in s.Transforms ?? Array.Empty<(Transform, PhysicsWorld.Pose)>()) if (!_poseAsLoaded.ContainsKey(t)) TrackTransform(t);
        }

        public float Elapsed(courseScript active)
        {
            if (TimedFromRunStart) return Engine.PhysicsTicks * World.FixedDeltaTime;
            return active == null ? -1f : CourseTime(active);
        }

        public void SetTarget(JsonElement header)
        {
            TargetCourse = (header.TryGetProperty("gameCourse", out JsonElement gameCourse) ? gameCourse : header.GetProperty("course")).GetInt32();
            _traceCourse = header.GetProperty("course").GetInt32();
            if (header.TryGetProperty("finish", out JsonElement finish))
                TargetFinish = new CaptureRect(new CapturePos(finish[0].GetSingle(), finish[1].GetSingle()), finish[2].GetSingle(), finish[3].GetSingle());
            Transition = header.TryGetProperty("transition", out JsonElement transition) && transition.GetBoolean();
            ScenePos finishAt = FromCapture(TargetFinish.At);
            _rewardBox = TargetFinish.W > 0f && !Transition ? Shop.NearestBox(finishAt.x, finishAt.y, FullTasGoal.RewardBoxRange) : null;
            if (header.TryGetProperty("finishStart", out JsonElement next))
            {
                TargetStart = next.GetInt32();
                _targetStartCourse = Courses.FirstOrDefault(c => c.courseNumber == TargetStart)
                    ?? throw new InvalidOperationException("Course " + TargetStart + " is not active in this world export.");
            }
            if (header.TryGetProperty("fulltas", out JsonElement fulltas))
            {
                SetGoals(fulltas.GetProperty("goals"));
                if (fulltas.TryGetProperty("buys", out JsonElement buys) && buys.ValueKind == JsonValueKind.String)
                {
                    if (buys.GetString() == "loose") Goals.LooseBuys();
                    else if (buys.GetString() != "strict") throw new InvalidOperationException("fulltas buys is \"strict\" or \"loose\", not \"" + buys.GetString() + "\".");
                }
                _savestateStart = fulltas.TryGetProperty("savestate", out JsonElement savestate) && savestate.ValueKind == JsonValueKind.String;
            }
            CheckpointRespawns = header.TryGetProperty("checkpointRespawns", out JsonElement checkpoints) && checkpoints.GetBoolean();
            // Course --auto's synthetic trace is a course's header with goals added ("courseAuto"): its start is the course's.
            _categoryTrace = !header.TryGetProperty("tech", out _) && (!header.TryGetProperty("fulltas", out _)
                || header.TryGetProperty("courseAuto", out JsonElement courseAuto) && courseAuto.ValueKind == JsonValueKind.True);
        }

        /// <summary>A category trace's start (a course, bonus or transition from its quick restart): not a Full-TAS run's or a tech demo's.</summary>
        bool _categoryTrace;

        /// <summary>The trace header's course id (courses.json's: 11-15 are Courses 1-5 overgrown), for the atom door rule.</summary>
        int _traceCourse;

        bool _savestateStart;

        void SetGoals(JsonElement goals)
        {
            var list = new List<FullTasGoal>();
            foreach (JsonElement g in goals.EnumerateArray())
            {
                var goal = new FullTasGoal { label = g.GetProperty("label").GetString(), kind = g.GetProperty("kind").GetString() };
                if (g.TryGetProperty("course", out JsonElement course)) goal.course = course.GetInt32();
                if (g.TryGetProperty("boxes", out JsonElement boxes) && boxes.ValueKind == JsonValueKind.Array)
                    goal.boxes = boxes.EnumerateArray().Select(b => b.GetString()).ToArray();
                if (g.TryGetProperty("rect", out JsonElement rect) && rect.ValueKind == JsonValueKind.Array)
                {
                    goal.x = rect[0].GetSingle(); goal.y = rect[1].GetSingle(); goal.w = rect[2].GetSingle(); goal.h = rect[3].GetSingle();
                }
                if (g.TryGetProperty("from", out JsonElement from)) goal.from = from.GetInt32();
                if (goal.kind == FullTasGoal.Start || goal.kind == FullTasGoal.Finish)
                {
                    if (!Courses.Any(c => c.courseNumber == goal.course)) throw new InvalidOperationException("Goal " + goal.label + ": course " + goal.course + " is not active in this world export.");
                }
                else if (goal.kind == FullTasGoal.Buy || goal.kind == FullTasGoal.Reward)
                {
                    if (goal.kind == FullTasGoal.Reward && (goal.boxes == null || goal.boxes.Length == 0))
                    {
                        string box = Shop.NearestBox(goal.x, goal.y, FullTasGoal.RewardBoxRange);
                        if (box == null) throw new InvalidOperationException("Goal " + goal.label + ": no upgrade box near its rect in this world export.");
                        goal.boxes = new[] { box };
                    }
                    if (goal.boxes == null || goal.boxes.Length == 0) throw new InvalidOperationException("Goal " + goal.label + " names no box.");
                    foreach (string box in goal.boxes)
                        if (!Shop.HasBox(box)) throw new InvalidOperationException("Goal " + goal.label + ": upgrade box " + box + " is not in this world export.");
                }
                else if (goal.kind == FullTasGoal.At)
                {
                    if (!(goal.w > 0f) || !(goal.h > 0f)) throw new InvalidOperationException("Goal " + goal.label + " has no area rect.");
                }
                else throw new InvalidOperationException("Goal " + goal.label + ": unknown kind " + goal.kind + ".");
                list.Add(goal);
            }
            if (list.Count == 0) throw new InvalidOperationException("A Full-TAS trace needs at least one goal.");
            Goals = new FullTasTracker(list.ToArray());
        }

        public courseScript TimingCourse()
        {
            foreach (courseScript c in Courses) if ((TargetCourse == 0 || c.courseNumber == TargetCourse) && Tracking(c)) return c;
            return null;
        }

        /// <summary>The active course numbered <paramref name="number"/> (a loop: Run looks one up every tick).</summary>
        courseScript Course(int number)
        {
            foreach (courseScript c in Courses) if (c.courseNumber == number) return c;
            throw new InvalidOperationException("Course " + number + " is not active in this world export.");
        }

        public static float CourseTime(courseScript course) { return PathTime(course); }

        /// <summary>
        /// Loads a trace's start. <paramref name="canonical"/> (the searches) gives a category trace's start the history every
        /// category start has in the game now (<see cref="IGTAPTasMod.CourseStart"/>), so a trace captured before that searches
        /// from the start the game judges; a replay of what the game recorded (replay, path) loads it as captured.
        /// </summary>
        public void LoadStart(JsonElement start, bool canonical = false)
        {
            ApplyFields(Player, start.GetProperty("movement"));
            if (_savestateStart) Goals.NoCheckpointAt(Player.respawnPoint.x, Player.respawnPoint.y);
            foreach (JsonElement entry in start.GetProperty("engine").EnumerateArray())
            {
                FieldInfo f = World.FindField(typeof(MacroEngine), entry[0].GetString(), entry[1].GetString());
                if (f == null || f.Name == "_steps" || f.Name == "AttemptEnded") continue;
                f.SetValue(Engine, World.Read(entry[2], f.FieldType));
            }
            int index = 0;
            foreach (JsonElement course in start.GetProperty("courses").EnumerateArray())
            {
                int id = course.GetProperty("id").GetInt32();
                courseScript target = World.Objects.TryGetValue(id, out UnityEngine.Object o) ? o as courseScript : Courses.ElementAtOrDefault(index);
                if (target != null) ApplyFields(target, course.GetProperty("fields"));
                index++;
            }
            Transform t = Player.transform;
            t.position = World.Vec3(start.GetProperty("position"));
            if (start.TryGetProperty("localScale", out JsonElement scale)) t.localScale = World.Vec3(scale);
            if (start.TryGetProperty("transformRotation", out JsonElement rotation)) t.rotation = World.Quat(rotation);
            index = 0;
            foreach (JsonElement c in start.GetProperty("colliders").EnumerateArray())
            {
                BoxCollider2D box = _playerColliders[index++];
                box.offset = new Vector2(World.ReadFloat(c[0]), World.ReadFloat(c[1]));
                box.size = new Vector2(World.ReadFloat(c[2]), World.ReadFloat(c[3]));
                box.excludeLayers = c[4].GetInt32();
                box.enabled = c[5].GetBoolean();
            }
            Scheduler.Queue.Clear();
            Scheduler.Routines.Clear();
            foreach (JsonElement p in start.GetProperty("pending").EnumerateArray())
                Scheduler.Queue.Add(new Scheduler.Pending { Due = p[0].GetInt32(), Target = Player, Method = p[1].GetString(), Repeat = -1f });
            Physics.ResetPlayer(World.Vec2(start.GetProperty("bodyPosition")), World.Vec2(start.GetProperty("velocity")),
                World.ReadFloat(start.GetProperty("rotation")), World.ReadFloat(start.GetProperty("angularVelocity")));
            if (start.TryGetProperty("economy", out JsonElement economy))
            {
                Shop.LoadStart(economy, ApplyFields);
                // The zone loaded at the start (the world export can come some ticks after it): what a zone activator compares.
                if (economy.TryGetProperty("activeZone", out JsonElement zone) && World.All<ZoneLoader>().FirstOrDefault() is ZoneLoader loader)
                    loader.activeZone = zone.GetInt32();
            }
            if (start.TryGetProperty("vman", out JsonElement vman) != VmanScript.isCurrentlyVman)
                throw new InvalidOperationException(VmanScript.isCurrentlyVman ? "The world is a Vman speedrun's, but the trace's start has no Vman clock: capture both again."
                    : "The trace's start is a Vman speedrun's, but its world isn't: capture both again.");
            if (VmanScript.isCurrentlyVman)
            {
                _vmanTimeLeft = vman.GetProperty("timeLeft").GetDouble();
                _vmanBlockEnding = vman.GetProperty("blockEnding").GetBoolean();
            }
            if (start.TryGetProperty("random", out JsonElement random))
                UnityEngine.Random.state = new UnityEngine.Random.State
                {
                    s0 = random[0].GetInt32(), s1 = random[1].GetInt32(), s2 = random[2].GetInt32(), s3 = random[3].GetInt32(),
                };
            else UnityEngine.Random.InitState(0);
            if (canonical && _categoryTrace) IGTAPTasMod.CourseStart.Apply(Player, World.All<clonesScript>());
            // Courses 6-15 and bonus 11 start with the atom room's door open (README ### Rules 21), as the plugin's category start sets it:
            // a trace captured before that rule searches from the start the game now judges.
            if (_categoryTrace && IGTAPTasMod.CourseStart.AssumesAtomDoor(_traceCourse)) IGTAPTasMod.CourseStart.OpenAtomDoor(World.All<upgradeBox>());
            MaterializeRebases(Array.Empty<Vector3>());
            _rebasePending = Vector3.zero;
            _rebaseDue = false;
            // A start captured with FloatingOrigin's MoveOrigin waiting (a lab or savestate placed past its Threshold) has it
            // armed again from where the player stands, as turbo's resume runs FloatingOrigin.LateUpdate: it moves the world
            // back after the first physics step.
            if (start.TryGetProperty("rebasing", out JsonElement rebasing) && rebasing.GetBoolean()) FloatingOriginFrame();
            // The start's interpolation ends (older traces have none: at rest where it stands).
            _loadedStepAt = new Vector3(float.NaN, float.NaN, float.NaN);
            if (start.TryGetProperty("stepFrom", out JsonElement stepFrom) && start.TryGetProperty("stepTo", out JsonElement stepTo))
            {
                Vector3 at = Player.transform.m_Position;
                Vector2 from = World.Vec2(stepFrom), to = World.Vec2(stepTo);
                _loadedStepFrom = new Vector3(from.x, from.y, at.z);
                _loadedStepTo = new Vector3(to.x, to.y, at.z);
                _loadedStepAt = at;
            }
            if (canonical && _categoryTrace) PutCanonicalOrigin(start);
            // A tech demo's trace starts after its first input tick (the demo starts inside a tick, as its playback does).
            // The start's pending Invokes are due counted from its next tick, which Capture counts from Tick + 1.
            Scheduler.Tick = -1;
            _start = Capture(start.TryGetProperty("tick", out JsonElement startTick) ? startTick.GetInt32() : 0);
        }

        static readonly AccessTools.FieldRef<Movement, float> LastHyperY = AccessTools.FieldRefAccess<Movement, float>("lastHyperY"),
            JumpStartHeight = AccessTools.FieldRefAccess<Movement, float>("heightOfLastJumpStart");

        /// <summary>
        /// A category start at the FloatingOrigin the plugin judges it at (TurboRunner.PutCanonicalOrigin): as the game's rebase
        /// on the player at the quick restart point leaves it, currentOrigin minus that point (12 under the reset point). A trace
        /// captured at another origin (whatever ran before it in the game) is rebased there from its capture, as a rebase of its
        /// own (restores and the capture frame follow). The game's own values then differ at most by the rounding of the shifts
        /// it came through. A trace captured there is left as it is. The scene-frame fields the start writes move with the world:
        /// <c>courseResetPoint</c>, the interpolation ends, and standing, <c>lastHyperY</c> and <c>heightOfLastJumpStart</c>.
        /// </summary>
        void PutCanonicalOrigin(JsonElement start)
        {
            if (_floating == null) return;
            Vector2 reset = Player.courseResetPoint != Vector2.zero
                ? new Vector2(Player.courseResetPoint.x - Origin.x, Player.courseResetPoint.y - Origin.y) : Player.respawnPoint;
            var reference = new Vector3(reset.x, reset.y - 12f, 0f);
            if (Origin.x == -reference.x && Origin.y == -reference.y) return;
            var by = new Vector3(reference.x + Origin.x, reference.y + Origin.y, 0f);
            AddRebase(by);
            Engine.WorldMoved(by);
            Physics.WorldMoved();
            _rebasePending = Vector3.zero;
            _rebaseDue = false;
            if (Player.courseResetPoint != Vector2.zero) Player.courseResetPoint -= new Vector2(by.x, by.y);
            // Standing, the game writes both every tick; in the air CourseStart set the jump height from the player's y and
            // lastHyperY to its no-hyper value.
            JumpStartHeight(Player) -= by.y;
            if (Player.onGround) LastHyperY(Player) -= by.y;
            if (!float.IsNaN(_loadedStepAt.x))
            {
                _loadedStepFrom -= by;
                _loadedStepTo -= by;
                _loadedStepAt -= by;
            }
            Vector2 body = World.Vec2(start.GetProperty("bodyPosition"));
            Physics.ResetPlayer(new Vector2(body.x - by.x, body.y - by.y), World.Vec2(start.GetProperty("velocity")),
                World.ReadFloat(start.GetProperty("rotation")), World.ReadFloat(start.GetProperty("angularVelocity")));
        }

        void ApplyFields(object target, JsonElement fields)
        {
            foreach (JsonElement entry in fields.EnumerateArray())
            {
                FieldInfo f = World.FindField(target.GetType(), entry[0].GetString(), entry[1].GetString());
                if (f == null) continue;
                object value = World.Read(entry[2], f.FieldType);
                if (value == null && f.FieldType.Assembly == typeof(UnityEngine.Object).Assembly && !typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                    continue;
                f.SetValue(target, value);
            }
        }

        public SimSnapshot Start { get { return _start; } }

        /// <summary>
        /// The player body's interpolation is on, as the game ships it, and 1x playback runs on the plugin's fixed clock
        /// (MacroEngine.PlaybackFrameRate, TasPlugin.FixedClockStep). Unity shows the body blended between its position at
        /// the start of the last physics step and its end (alpha = frame time since the step / 0.02), and Movement reads that:
        /// FixedUpdate the last frame's blend, so its position writes (jump +2/+5, wall jump +1, dash −20) pull the body back
        /// by the lag; Update (the step-up) every frame's. A write drops the interpolation: the body moves there and the
        /// frames after show it. Turbo (TurboRunner) models the same frames.
        /// </summary>
        static readonly MacroEngine.FrameGrid Frames = MacroEngine.FrameGrid.Playback;
        Vector3 _stepFrom, _stepTo;
        float _freezeFor = -1f;
        int _pauseFrames, _pauseAfter;
        /// <summary>The last physics step's <see cref="StepEvents"/> (cleared at each step's start), the spring's up at its last
        /// hit, and the side (-1: -x) of the wall at its last special wall jump and of the step at its last step-up.</summary>
        public StepEvents Events;
        public Vector2 SpringUp;
        public float WallBoostSide, StepUpSide;
        static readonly AccessTools.FieldRef<Movement, int> DashBounceFrames = AccessTools.FieldRefAccess<Movement, int>("DashBounceFrames");
        bool _inRun;
        /// <summary>The trace start's interpolation ends (its last physics step's start and end, turbo's _stepFrom/_stepTo) and
        /// where it put the player; a start moved from there (a lab or probe placement) is at rest instead.</summary>
        Vector3 _loadedStepFrom, _loadedStepTo, _loadedStepAt = new Vector3(float.NaN, float.NaN, float.NaN);
        bool AtLoadedStart { get { return Player.transform.m_Position == _loadedStepAt; } }

        Vector3 BodyPosition()
        {
            var body = Physics.PlayerBody();
            return new Vector3(body.px, body.py, Player.transform.m_Position.z);
        }

        /// <summary>Before FixedUpdate: the transform holds the last rendered frame's blend (not a write, so the body stays).</summary>
        Vector3 LagForFixedUpdate()
        {
            Player.transform.StoreWorldPosition(Vector3.Lerp(_stepFrom, _stepTo, Frames.Frames(Engine.GridTicks).LastAlpha));
            // As stored: the world position reads back through the player's parent, rounded.
            return Player.transform.m_Position;
        }

        /// <summary>After each rendered frame's Movement.Update (probe's per-frame rows, as the plugin's 1x probe logs them):
        /// the frame's blend, and whether time stood still in it (a freeze or a pause).</summary>
        public Action<float, bool> OnFrame;

        /// <summary>At Movement.getPlayerControlledInput's start, once the tick's input is in (lab solve's wall control): the
        /// contact branch has run, so OnWall and wallCoyoteFrames say whether the wall rays hit on this tick.</summary>
        public Action<Movement> BeforeInput;

        /// <summary>The tech observers (<see cref="TechSteps"/>; null when nothing watches): CheckForSuperJump's entry (its state
        /// before it runs), determineSpriteDirection's (right after getPlayerControlledInput, also in the dash branch and the
        /// wall jump lock), a respawn, a step-up (in a rendered frame), a special wall jump's directionMagnitudeMod (1: a
        /// wallbounce, 0.6: a dash boost), a block swap (to blue or not), a zip mover made intangible and an upgrade box's
        /// trigger stay.</summary>
        public Action<Movement> SuperJumpCheck, AfterInput, Respawned, SteppedUp;
        public Action<float> WallKick;
        public Action<bool> Swapped;
        public Action<PlatformMover> Intangible;
        public Action<upgradeBox, Collider2D> BoxTouched;

        /// <summary>
        /// The run from the start snapshot begins inside its first tick, as an in-game lab demo or probe from a placed start
        /// does (TasPlugin starts the engine in Movement.FixedUpdate's prefix after MacroEngine.BeforeTick ran idle): that tick
        /// skips BeforeTick, so Movement's pre-hook stick read is at rest and the first step is pressed at the input hook.
        /// A trace's start is already the state after that tick.
        /// </summary>
        public bool StartsInsideTick;

        /// <summary>
        /// The tick's rendered frames: one Movement.Update and one clonesScript.Update per frame (on the frame's Time.deltaTime,
        /// 0 in a freeze's or pause's repeats) at its blend, then the frame's WaitForEndOfFrame resumes (on the same
        /// deltaTime: TreeSegmentActivationComponent.IE_Grow), the first frame repeated for a dash freeze's
        /// frames (time stands still, TimeStop), and a route's pause (the menu held open for MacroStep.pauseFrames frames)
        /// repeating the frame it holds (MacroEngine.FrameRepeats). A step-up write moves the body at once and later frames
        /// show it. The transform goes back to the body for the rest of the tick (goals, rebases, snapshots): the step's end,
        /// which a rebase this tick has moved with the world (the body itself follows at the next sync), as turbo's
        /// Place(player, _stepTo). Without the <paramref name="player"/> (the economy drive, EconomyDrive.cs) only the frames'
        /// economy runs: the Start pass, the clone and atom steps, the atom grid and the frame and end-of-frame resumes.
        /// </summary>
        void InterpolatedFrames(bool player = true)
        {
            long ticks = Engine.GridTicks;
            float[] alphas = Frames.Frames(ticks).Alphas;
            int frozen = _freezeFor >= 0f ? Frames.FrozenFrames(ticks, _freezeFor) : 0;
            for (int k = 0; k < alphas.Length; k++)
            {
                int repeats = MacroEngine.FrameRepeats(k, alphas.Length, frozen, _pauseFrames, _pauseAfter);
                // The repeats are frames the run stands still in: its live time (turbo counts its loop's the same way; at 1x
                // the plugin counts the frames whose Update ran at timeScale 0).
                Engine.CountHeldFrames(repeats);
                for (int repeat = repeats; repeat >= 0; repeat--)
                {
                    if (k > 0 || repeat < repeats)
                    {
                        Scheduler.Frame++;
                        Physics.FrameStart();
                    }
                    // The frame's Start pass comes before its Updates (README ### Rules, exception 12).
                    Shop.Economy?.StartPass();
                    if (player)
                    {
                        Player.transform.StoreWorldPosition(Vector3.Lerp(_stepFrom, _stepTo, alphas[k]));
                        _update();
                    }
                    void TakeWrite()
                    {
                        if (!player || !Physics.PlayerTransformDirty) return;
                        // At 1x the write reaches the body at the next frame's start. After the tick's last frame that is the
                        // next tick's step, so its FixedUpdate still queries the body where it was (the squish check's
                        // Collider2D.Distance) and the sync before the step moves it.
                        if (k < alphas.Length - 1 || repeat > 0) Physics.SyncTransforms();
                        _stepFrom = _stepTo = Player.transform.m_Position;
                    }
                    TakeWrite();
                    float dt = repeat == repeats ? Frames.Step(ticks, k) : 0f;
                    Shop.StepClones(dt);
                    if (k == 0 && repeat == repeats)
                    {
                        Shop.Economy?.GridFrame();
                        // A teleporter's poll (TeleporterScript.manualUpdate), an Invoke as the grid's: its teleport writes the
                        // player's transform. Turbo's TurboRunner.Polls the same.
                        Scheduler.FireInUpdate();
                        TakeWrite();
                    }
                    if (player) OnFrame?.Invoke(alphas[k], repeat > 0);
                    Scheduler.FrameDeltaTime = dt;
                    // The frame's yield-null resumes after its Updates (a zone load, ZoneLoader.IE_Load, in its third frame), then
                    // the atom grid's first call when the load woke the collider (Economy.GridStart): turbo's order.
                    Scheduler.Resume(Scheduler.Wait.Frame);
                    Shop.Economy?.GridNow();
                    Scheduler.Resume(Scheduler.Wait.EndOfFrame);
                }
            }
            if (player) Player.transform.StoreWorldPosition(_stepTo);
        }

        public SimSnapshot Capture(int tick)
        {
            var pending = new (int, MonoBehaviour, string, float)[Scheduler.Queue.Count];
            for (int i = 0; i < pending.Length; i++)
            {
                Scheduler.Pending p = Scheduler.Queue[i];
                pending[i] = (p.Due - Scheduler.Tick - 1, p.Target, p.Method, p.Repeat);
            }
            return new SimSnapshot
            {
                Tick = tick,
                Movement = ShareBoxes(Player, FieldState.Capture(Player)),
                Engine = ShareBoxes(Engine, Engine.CaptureState()),
                Courses = CaptureCourses(),
                Position = Player.transform.m_Position,
                LocalScale = Player.transform.m_LocalScale,
                Rotation = Player.transform.m_Rotation,
                Colliders = CaptureColliders(),
                Pending = pending,
                Physics = Physics.Capture(),
                Touched = CaptureTouched(),
                Active = Now(_activeAsLoaded, g => g.activeSelf),
                Enabled = Now(_enabledAsLoaded, b => b.enabled),
                Layers = Now(_layerAsLoaded, g => g.layer),
                Transforms = Now(_poseAsLoaded, t => new PhysicsWorld.Pose(t)),
                Routines = CaptureRoutines(),
                Economy = Shop.Capture(),
                Goal = Goals?.Capture(),
                Random = UnityEngine.Random.state,
                VmanTimeLeft = _vmanTimeLeft,
                Rebases = _rebases,
                RebasePending = _rebasePending,
                RebaseDue = _rebaseDue,
                // Outside a run the player is at rest where it stands (a probe's moved start), unless it still stands where a
                // trace's start put it: that start's own interpolation ends (turbo resumes from them).
                StepFrom = _inRun ? _stepFrom : AtLoadedStart ? _loadedStepFrom : Player.transform.m_Position,
                StepTo = _inRun ? _stepTo : AtLoadedStart ? _loadedStepTo : Player.transform.m_Position,
                Animation = Animation,
                OrbSeen = (bool[])_orbSeen.Clone(),
            };
        }

        object[][] CaptureCourses()
        {
            var courses = new object[Courses.Length][];
            for (int i = 0; i < courses.Length; i++) courses[i] = CaptureShared(Courses[i]);
            return courses;
        }

        (Vector2 offset, Vector2 size, int exclude, bool enabled)[] CaptureColliders()
        {
            var colliders = new (Vector2 offset, Vector2 size, int exclude, bool enabled)[_playerColliders.Length];
            for (int i = 0; i < colliders.Length; i++)
            {
                BoxCollider2D c = _playerColliders[i];
                colliders[i] = (c.offset, c.size, c.excludeLayers.value, c.enabled);
            }
            return colliders;
        }

        (MonoBehaviour target, object[] fields)[] CaptureTouched()
        {
            if (_touched.Count == 0) return Array.Empty<(MonoBehaviour, object[])>();
            var touched = new (MonoBehaviour target, object[] fields)[_touched.Count];
            int i = 0;
            foreach (MonoBehaviour script in _touched) touched[i++] = (script, CaptureShared(script));
            return touched;
        }

        /// <summary>Each object tracked since load with its value now, in the tracking dictionary's order.</summary>
        static (TKey, TValue)[] Now<TKey, TLoaded, TValue>(Dictionary<TKey, TLoaded> tracked, Func<TKey, TValue> now)
        {
            if (tracked.Count == 0) return Array.Empty<(TKey, TValue)>();
            var values = new (TKey, TValue)[tracked.Count];
            int i = 0;
            foreach (TKey key in tracked.Keys) values[i++] = (key, now(key));
            return values;
        }

        (MonoBehaviour target, System.Collections.IEnumerator body, Coroutine handle, Scheduler.Wait wait, int due)[] CaptureRoutines()
        {
            List<Scheduler.Routine> parked = Scheduler.Routines;
            if (parked.Count == 0) return Array.Empty<(MonoBehaviour, System.Collections.IEnumerator, Coroutine, Scheduler.Wait, int)>();
            var routines = new (MonoBehaviour target, System.Collections.IEnumerator body, Coroutine handle, Scheduler.Wait wait, int due)[parked.Count];
            for (int i = 0; i < routines.Length; i++)
            {
                Scheduler.Routine r = parked[i];
                routines[i] = (r.Target, Scheduler.Copy(r.Body), r.Handle, r.Wait, r.Wait == Scheduler.Wait.Frame ? r.Due - Scheduler.Frame : r.Due - Scheduler.Tick - 1);
            }
            return routines;
        }

        public int Fingerprint()
        {
            var h = new HashCode();
            AddFields(ref h, Player);
            foreach (courseScript c in Courses) AddFields(ref h, c);
            AddPlayer(ref h);
            h.Add(Animation);
            foreach (bool seen in _orbSeen) h.Add(seen);
            foreach (Scheduler.Pending p in Scheduler.Queue)
            {
                h.Add(p.Due - Scheduler.Tick); h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Target)); h.Add(p.Method); h.Add(p.Repeat);
            }
            int touched = 0;
            foreach (MonoBehaviour script in _touched)
            {
                var s = new HashCode();
                s.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(script));
                AddFields(ref s, script);
                touched ^= s.ToHashCode();
            }
            h.Add(touched);
            int toggled = 0;
            // Only what differs from the load: an object back at its loaded value hashes as one never toggled, whatever ran before.
            foreach (var (g, loaded) in _activeAsLoaded) if (g.activeSelf != loaded) toggled ^= HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g), g.activeSelf);
            foreach (var (b, loaded) in _enabledAsLoaded) if (b.enabled != loaded) toggled ^= HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b), b.enabled);
            foreach (var (g, loaded) in _layerAsLoaded) if (g.layer != loaded) toggled ^= HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g), g.layer);
            // Local poses: a rebase moves none of these (none is a root).
            foreach (var (t, (loaded, _)) in _poseAsLoaded)
                if (t.m_LocalPosition != loaded.LocalPosition || t.m_LocalRotation != loaded.LocalRotation || t.m_LocalScale != loaded.LocalScale)
                    toggled ^= HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(t), t.m_LocalPosition, t.m_LocalRotation, t.m_LocalScale);
            h.Add(toggled);
            foreach (Scheduler.Routine r in Scheduler.Routines)
            {
                h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Target)); h.Add(r.Wait);
                h.Add(r.Wait == Scheduler.Wait.Seconds ? r.Due - Scheduler.Tick : 0);
                // The iterator's state and locals; its current yield object is a fresh instance each run.
                FieldInfo[] fields = FieldState.For(r.Body.GetType());
                object[] values = FieldValues(r.Body);
                for (int i = 0; i < fields.Length; i++)
                    if (fields[i].FieldType.IsValueType) AddValue(ref h, values[i], false);
            }
            Shop.AddTo(ref h);
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            h.Add(rng.s0); h.Add(rng.s1); h.Add(rng.s2); h.Add(rng.s3);
            if (Goals != null) h.Add(Goals.Hash());
            foreach (Vector3 by in _rebases) h.Add(by);
            h.Add(_rebaseDue); h.Add(_rebasePending);
            return h.ToHashCode();
        }

        void AddFields(ref HashCode h, object target)
        {
            object[] values = FieldValues(target);
            int count = FieldState.For(target.GetType()).Length;
            for (int i = 0; i < count; i++) AddValue(ref h, values[i], false);
        }

        // The fingerprints' field reads (FieldState.Read), reused.
        object[] _fieldValues = Array.Empty<object>();

        /// <summary>The target's fields in <see cref="FieldState.For"/> order, in a buffer the next call overwrites.</summary>
        object[] FieldValues(object target)
        {
            int count = FieldState.For(target.GetType()).Length;
            if (_fieldValues.Length < count) _fieldValues = new object[count];
            FieldState.Read(target, _fieldValues);
            return _fieldValues;
        }

        /// <summary>The player's transform, box colliders, body and interpolation ends.</summary>
        void AddPlayer(ref HashCode h)
        {
            Transform t = Player.transform;
            h.Add(t.m_Position); h.Add(t.m_LocalScale); h.Add(t.m_Rotation);
            foreach (BoxCollider2D c in _playerColliders) { h.Add(c.m_Offset); h.Add(c.m_Size); h.Add(c.m_ExcludeLayers); h.Add(c.m_Enabled); }
            h.Add(Physics.PlayerBody());
            h.Add(_stepFrom); h.Add(_stepTo);
        }

        /// <param name="portable">The same in every process: objects by instance id and type name, not identity.</param>
        static void AddValue(ref HashCode h, object value, bool portable)
        {
            switch (value)
            {
                case null: h.Add(0); break;
                case UnityEngine.Object o: h.Add(portable ? o.GetInstanceID() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)); break;
                case string s: h.Add(s); break;
                case System.Collections.IEnumerable items:
                    foreach (object item in items) AddValue(ref h, item, portable);
                    h.Add(-1);
                    break;
                default:
                    h.Add(value.GetType().IsValueType ? value.GetHashCode()
                        : portable ? value.GetType().FullName.GetHashCode() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value));
                    break;
            }
        }

        public void Restore(SimSnapshot s)
        {
            MaterializeRebases(s.Rebases);
            RestoreToggles(s);
            RestoreTransforms(s);
            // A script the snapshot lists is written whole below: its pristine write would be overwritten field for field.
            foreach (var (target, _) in s.Touched) _restoring.Add(target);
            foreach (MonoBehaviour script in _touched)
                if (!_restoring.Contains(script))
                {
                    FieldState.Restore(script, _pristine[script]);
                    _captured[script] = _pristine[script];
                }
            _restoring.Clear();
            _touched.Clear();
            foreach (var (target, fields) in s.Touched)
            {
                // A snapshot from another simulation (a saved goal state) can hold objects this one never touched: its
                // pristine copy is the object as it is now, as Touch would have taken it.
                if (!_pristine.ContainsKey(target)) _pristine[target] = FieldState.Capture(target);
                FieldState.Restore(target, fields);
                _captured[target] = fields;
                _touched.Add(target);
            }
            FieldState.Restore(Player, s.Movement);
            for (int i = 0; i < Courses.Length; i++)
            {
                FieldState.Restore(Courses[i], s.Courses[i]);
                foreach (System.Collections.IList list in _coursePathLists[i]) list.Clear();
                _captured[Courses[i]] = s.Courses[i];
            }
            RestorePose(s);
            Scheduler.Queue.Clear();
            Scheduler.Tick = 0;
            Scheduler.Frame = 0;
            _driven = false;
            foreach (var (due, target, method, repeat) in s.Pending)
                Scheduler.Queue.Add(new Scheduler.Pending { Due = due, Target = target, Method = method, Repeat = repeat });
            Scheduler.Routines.Clear();
            foreach (var (target, body, handle, wait, due) in s.Routines)
                Scheduler.Routines.Add(new Scheduler.Routine { Target = target, Body = Scheduler.Copy(body), Handle = handle, Wait = wait, Due = due });
            Physics.Restore(s.Physics);
            Shop.Restore(s.Economy);
            if (Goals != null) Goals.Restore(s.Goal);
            UnityEngine.Random.state = s.Random;
            _vmanTimeLeft = s.VmanTimeLeft;
            _rebasePending = s.RebasePending;
            _rebaseDue = s.RebaseDue;
            _stepFrom = s.StepFrom;
            _stepTo = s.StepTo;
            Player.animator?.SetInteger("Animation", s.Animation);
            for (int i = 0; i < _orbs.Length; i++) _orbSeen[i] = s.OrbSeen != null ? s.OrbSeen[i] : OnCamera(_orbs[i], s.StepTo);
        }

        /// <summary>The objects toggled since load (active, enabled, layer) as the snapshot has them, the physics suspended meanwhile
        /// (a persisted snapshot's load pre-applies them too: SnapshotStore).</summary>
        internal void RestoreToggles(SimSnapshot s)
        {
            Physics.Suspended = true;
            int k = 0;
            foreach (var (g, loaded) in _activeAsLoaded)
            {
                bool active = Saved(s.Active, k++, g, loaded);
                if (g.activeSelf != active) g.SetActive(active);
            }
            k = 0;
            foreach (var (b, loaded) in _enabledAsLoaded)
            {
                bool enabled = Saved(s.Enabled, k++, b, loaded);
                if (b.enabled != enabled) b.enabled = enabled;
            }
            k = 0;
            foreach (var (g, loaded) in _layerAsLoaded) g.m_Layer = Saved(s.Layers, k++, g, loaded);
            Physics.Suspended = false;
        }

        /// <summary>The player's transform and box colliders as the snapshot has them (the colliders' enabled written directly:
        /// <see cref="PhysicsWorld.Generation"/> counts it).</summary>
        internal void RestorePose(SimSnapshot s)
        {
            Transform t = Player.transform;
            t.m_Position = s.Position; t.m_LocalPosition = t.m_Parent == null ? s.Position : s.Position - t.m_Parent.m_Position;
            t.m_LocalScale = s.LocalScale; t.m_LossyScale = s.LocalScale;
            t.m_Rotation = s.Rotation; t.m_LocalRotation = s.Rotation;
            for (int i = 0; i < _playerColliders.Length && i < s.Colliders.Length; i++)
            {
                BoxCollider2D c = _playerColliders[i];
                c.m_Offset = s.Colliders[i].offset;
                c.m_Size = s.Colliders[i].size;
                c.m_ExcludeLayers = s.Colliders[i].exclude;
                c.m_Enabled = s.Colliders[i].enabled;
            }
            Physics.Generation++;
        }

        /// <summary>
        /// The value a snapshot saved for the <paramref name="index"/>-th object tracked since load, or its loaded value if it
        /// was tracked later. A snapshot lists them in the tracking dictionary's order then, which only grows (insertion order).
        /// </summary>
        static TValue Saved<TKey, TValue>((TKey target, TValue value)[] saved, int index, TKey key, TValue loaded) where TKey : class
        {
            return TrySaved(saved, index, key, out TValue value) ? value : loaded;
        }

        static bool TrySaved<TKey, TValue>((TKey target, TValue value)[] saved, int index, TKey key, out TValue value) where TKey : class
        {
            value = default;
            if (index >= saved.Length) return false;
            if (ReferenceEquals(saved[index].target, key)) { value = saved[index].value; return true; }
            foreach (var (target, v) in saved) if (ReferenceEquals(target, key)) { value = v; return true; }
            return false;
        }

        void TrackTransform(Transform t)
        {
            var loaded = new PhysicsWorld.Pose(t);
            int moved = Array.IndexOf(_moved, t);
            if (moved >= 0) (loaded.Position, loaded.LocalPosition) = _movedPristine[moved];
            _poseAsLoaded.Add(t, (loaded, moved));
        }

        /// <summary>The transforms moved since load as the snapshot has them, written without the change hooks (the physics
        /// restore puts back what their colliders were built at). One the snapshot doesn't list wasn't moved yet: its loaded
        /// pose, moved by the snapshot's rebases as MoveWorld moves it. Runs after MaterializeRebases.</summary>
        internal void RestoreTransforms(SimSnapshot s)
        {
            var saved = s.Transforms ?? Array.Empty<(Transform, PhysicsWorld.Pose)>();
            int k = 0;
            foreach (var (t, (loaded, moved)) in _poseAsLoaded)
            {
                if (TrySaved(saved, k++, t, out PhysicsWorld.Pose pose))
                {
                    pose.WriteTo(t);
                    continue;
                }
                loaded.WriteTo(t);
                if (moved >= 0) foreach (Vector3 by in s.Rebases) t.m_Position -= by;
            }
        }

        int Animation { get { return Player.animator?.GetInteger("Animation") ?? 0; } }

        public RunResult Run(IReadOnlyList<InputTick> inputs, SimSnapshot from = null, Func<int, bool> afterTick = null, int extraTicks = 400,
            Action<int> onStep = null)
        {
            // The engine hooks and the game's statics are per thread, and the last Simulation built on it owns them: an older one
            // would play nothing ("Solver run ended" at input 0). Build a throwaway Simulation on its own thread (Solve.OnThread).
            if (IGTAP.EngineSim.Engine.Scheduler != Scheduler)
                throw new InvalidOperationException("Another Simulation was built on this thread since this one; it owns the thread's engine hooks now");
            from ??= _start;
            if (from.Tick >= inputs.Count) from = _start;
            Restore(from);
            _inRun = true;
            int step = from.Tick;
            while (step > 0 && Merges(inputs[step - 1], inputs[step])) step--;
            // ResumeOwned: ToSteps already emits only enabled RawInput steps (Expand's filter would pass them all), so
            // Expand's Copy is redundant work here; the engine never reads the copied step ids.
            Engine.ResumeOwned(from.Engine, ToSteps(inputs, step), from.Tick - step);
            _finished = false;
            _finishTime = 0f;
            _endReason = null;
            _finishIn = -1;
            // Dash and spring ticks take no input (a farm loop's laps run well past one tick an input), as turbo's cap (Solver.Evaluate).
            int maxTicks = (inputs.Count - from.Tick) * 4 + extraTicks;
            int idleTicks = 0, lastTick = from.Tick > 0 ? from.Tick : -1, stepped = 0;
            bool stop = false;
            for (Scheduler.Tick = 0; Scheduler.Tick < maxTicks && Engine.IsRunning && idleTicks < 20 && !stop; Scheduler.Tick++)
            {
                bool nextWasTracking = _targetStartCourse != null && Tracking(_targetStartCourse);
                FullTasGoal goal = Goals?.Current;
                courseScript startGoal = goal != null && goal.kind == FullTasGoal.Start ? Course(goal.course) : null;
                bool startWasTracking = startGoal != null && Tracking(startGoal);
                EconomyTickStart();
                (float px0, float px1, float py0, float py1) = Goals != null ? PlayerBounds() : default;
                int rebasesBefore = _rebases.Length;
                Vector3 lag = LagForFixedUpdate();
                _freezeFor = -1f;
                _pauseFrames = _pauseAfter = 0;
                Events = StepEvents.None;
                if (Engine.TakeQuickRestart())
                {
                    string refused = Goals?.RestartRefused(Player.respawnPoint.x, Player.respawnPoint.y, Player.courseResetPoint != Vector2.zero);
                    if (refused != null)
                    {
                        Engine.Abort(refused + " at action " + (Engine.StepIndex + 1));
                        continue;
                    }
                    RespawnMethod.Invoke(Player, new object[] { true });
                    Goals?.QuickRestarted();
                }
                PlayerStepFrom = PlayerWorld;
                // A start inside a tick: the game started the engine in Movement.FixedUpdate's prefix after BeforeTick had run
                // idle, so that tick's pre-hook stick read is at rest and its step is pressed at the input hook (FixedTick).
                // globalStats.FixedUpdate's NP (Economy.FixedStep) runs before every other FixedUpdate: globalStats inherits
                // [DefaultExecutionOrder(-50)] from Singleton<T> (Singleton.cs:3), Movement and courseScript run at 0. A start
                // inside a tick comes after it: that tick's NP was paid before the start.
                if (!(StartsInsideTick && from.Tick == 0 && Scheduler.Tick == 0))
                {
                    Shop.Economy?.FixedStep();
                    Engine.BeforeTick(Player);
                }
                // The tick's frame starts here at 1x (its FixedUpdate runs inside a rendered frame).
                Physics.FrameStart();
                _fixedUpdate();
                Engine.CountPhysicsTick();
                Engine.AfterFixedTick(Player);
                _pauseFrames = Engine.TakePause(out _pauseAfter);
                if (_pauseFrames > 0) Events |= StepEvents.Paused;
                EconomyFixedUpdates();
                // No write: the blend was never the body's, so the sync leaves the body where the step put it (turbo's too). After a
                // rebase every body syncs to its transform, and the blend there pulled the player back by the frame's lag.
                // Once a rebase has run in this run, turbo's step-end transform write (Place(player, _stepTo)) moves the body on
                // every tick to what the transform reads back through the moved parent, rounded (1x too: VMAN 46.96's route died
                // at action 486 in both while this engine finished it). A capture taken after the game's own rebases doesn't.
                if (Player.transform.m_Position == lag)
                {
                    Player.transform.StoreWorldPosition(_stepTo);
                    if (Rebased && Player.transform.m_Position != BodyPosition()) Physics.TransformChanged(Player.transform, true);
                }
                Physics.SyncTransforms();
                _stepFrom = BodyPosition();
                Physics.Simulate(World.FixedDeltaTime);
                // A physics callback's write (a sideways spring's push, Movement.hitSpring) moves the body there at once and drops
                // the interpolation, as at 1x (turbo's post-step sync): the tick's frames show it, the next tick blends from it.
                if (Physics.PlayerTransformDirty)
                {
                    Physics.SyncTransforms();
                    _stepFrom = BodyPosition();
                }
                _stepTo = BodyPosition();
                EconomyAfterStep();
                int rebasesBeforeResume = _rebases.Length;
                ResumeRebase();
                if (_rebases.Length != rebasesBeforeResume)
                {
                    Vector3 by = _rebases[_rebases.Length - 1];
                    _stepFrom -= by;
                    _stepTo -= by;
                }
                SeeOrbs();
                InterpolatedFrames();
                FloatingOriginFrame();
                // The plugin's box tests read the body (BoundsPose); a rebase this tick has moved the transform's world, not the body.
                Vector3? boundsPose = _rebases.Length == rebasesBefore ? BoundsPose() : null;
                if (Engine.IsRunning && TargetFinish.W > 0f && _rewardBox == null && TouchesReward(boundsPose))
                    Engine.CompleteFromGame(Transition ? MacroEngine.TransitionFinishReason : MacroEngine.RewardFinishReason,
                        Engine.PhysicsTicks * World.FixedDeltaTime);
                if (Engine.IsRunning && !nextWasTracking && _targetStartCourse != null && Tracking(_targetStartCourse))
                    Engine.CompleteFromGame(MacroEngine.TransitionFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
                if (Goals != null && Engine.IsRunning)
                {
                    if (startGoal != null && !startWasTracking && Goals.Current == goal && Tracking(startGoal))
                        GoalEvent(Goals.CourseStarted(goal.course, InputBase + Engine.RunTicks, Engine.PhysicsTicks, Engine.HeldFrames));
                    FullTasGoal now = Goals.Current;
                    ScenePos goalAt = now != null ? FromCapture(new CapturePos(now.x, now.y)) : default;
                    if (_rebases.Length != rebasesBefore)
                    {
                        Vector3 by = _rebases[_rebases.Length - 1];
                        px0 -= by.x; px1 -= by.x; py0 -= by.y; py1 -= by.y;
                    }
                    if (now != null && now.kind == FullTasGoal.At && Touches(goalAt.x, goalAt.y, now.w, now.h, boundsPose)
                        && now.EnteredFrom(goalAt.x - now.w / 2f, goalAt.x + now.w / 2f, goalAt.y - now.h / 2f, goalAt.y + now.h / 2f, px0, px1, py0, py1))
                        GoalEvent(Goals.Touched(InputBase + Engine.RunTicks, Engine.PhysicsTicks, Engine.HeldFrames));
                }
                // A quick restart in the strict finish's ticks after an area goal takes that goal back (FullTasTracker): the run
                // then hasn't reached its goals, and finishing anyway handed the solver windows that missed them on replay.
                if (_finishIn >= 0 && Goals != null && !Goals.Done) _finishIn = -1;
                if (_finishIn >= 0 && _finishIn-- == 0 && Engine.IsRunning)
                {
                    _finishIn = -1;
                    Engine.CompleteFromGame(MacroEngine.FullTasFinishReason, _finishTimeAtGoal);
                }
                stepped++;
                onStep?.Invoke(stepped - 1);
                if (!Engine.IsRunning) continue;
                if (Engine.InputsExhausted) idleTicks++;
                int tick = (int)Engine.RunTicks;
                if (tick == lastTick) continue;
                lastTick = tick;
                if (afterTick != null && !afterTick(tick)) stop = true;
            }
            _inRun = false;
            if (Engine.IsRunning) Engine.Abort("Solver run ended");
            return new RunResult
            {
                Finished = _finished && _finishTime > 0f,
                Time = _finishTime,
                FinishTick = Math.Min(inputs.Count - 1, Math.Max(0, lastTick)),
                Stepped = stepped,
                PhysicsTicks = Engine.PhysicsTicks,
                HeldFrames = Engine.HeldFrames,
                EndReason = Goals == null ? _endReason : Goals.Explain(_endReason, Shop.Describe),
                Progress = Progress,
                GoalInputTicks = Goals == null ? null : (long[])Goals.InputTicks.Clone(),
                GoalPhysicsTicks = Goals == null ? null : (long[])Goals.PhysicsTicks.Clone(),
                GoalHeldFrames = Goals == null ? null : (long[])Goals.HeldFrames.Clone(),
            };
        }

        /// <summary>
        /// A used refill orb counts its cooldown down (JiggleDropScript.FixedUpdate, while the orbs are unlocked) and grows
        /// back at its end (onCooldownEnd, the refresh <see cref="RefillOrbState"/> keeps).
        /// </summary>
        void StepOrbs()
        {
            for (int i = 0; i < _orbs.Length; i++)
            {
                JiggleDropScript orb = _orbs[i];
                if (!(OrbCooldown(orb) > 0f) || !orb.isActiveAndEnabled) continue;
                Touch(orb);
                _orbFixedUpdates[i]();
            }
        }

        /// <summary>
        /// JiggleDropScript.SetActiveVisualState without the sprites: the orb is usable when its cooldown is over and the refill
        /// upgrades (unlockJiggleDrops + moreRefreshOrbs) are above its ActivationSequence. The run's own global upgrades when
        /// it has the native economy (the Omni Dash box's first buy adds a refresh orb: Economy.OmniDashFirstBuy).
        /// </summary>
        void RefillOrbState(object target)
        {
            var orb = (JiggleDropScript)target;
            Touch(orb);
            Economy economy = Shop.Economy;
            double orbs;
            if (economy != null)
                orbs = economy.GlobalOf(globalStats.globalUpgradeSet.unlockJiggleDrops) + economy.GlobalOf(globalStats.globalUpgradeSet.moreRefreshOrbs);
            else
            {
                Dictionary<globalStats.globalUpgradeSet, double> upgrades = globalStats.globalUpgradeDict;
                orbs = upgrades[globalStats.globalUpgradeSet.unlockJiggleDrops] + upgrades[globalStats.globalUpgradeSet.moreRefreshOrbs];
            }
            OrbUsable(orb) = OrbCooldown(orb) <= 0f && orbs > OrbSequence(orb);
        }

        /// <summary>
        /// JiggleDropScript.OnBecameVisible: an orb coming on camera reads the refill upgrades (SetActiveVisualState(regrow: false)),
        /// its only refresh but a use and a cooldown's end, so one on screen when they change stays as it was until it leaves the view
        /// and comes back (bonus17's pit: the orbs its box unlocks are off screen, and come on as the player walks to them). On camera
        /// is MacroEngine.OrbOnCamera after the physics step, as turbo's TurboRunner.SeeOrbs.
        /// </summary>
        void SeeOrbs()
        {
            for (int i = 0; i < _orbs.Length; i++)
            {
                bool seen = OnCamera(_orbs[i], _stepTo);
                if (seen && !_orbSeen[i]) RefillOrbState(_orbs[i]);
                _orbSeen[i] = seen;
            }
        }

        static bool OnCamera(JiggleDropScript orb, Vector3 player) =>
            orb.gameObject.activeInHierarchy && MacroEngine.OrbOnCamera(orb.transform.position, player);

        void VmanClock()
        {
            if (!VmanScript.isCurrentlyVman || !(_vmanTimeLeft > 0.0)) return;
            _vmanTimeLeft -= World.FixedDeltaTime;
            if (_vmanTimeLeft <= 0.0 && !_vmanBlockEnding && Engine.IsRunning)
                Engine.Abort("Unmodeled: the Vman speedrun's time ran out (ExitVmanSpeedrun loads the Overworld)");
        }

        const int StillTicks = 3;

        public int Still(List<InputTick> walk, int limit)
        {
            var inputs = walk.Concat(Enumerable.Repeat(new InputTick(), limit)).ToList();
            int still = 0, found = -1;
            Run(inputs, afterTick: tick =>
            {
                if (tick < walk.Count) return true;
                bool rest = Player.onGround && Player.Velocity.sqrMagnitude < 1e-4f && Player.momentum.sqrMagnitude < 1e-4f;
                still = rest ? still + 1 : 0;
                if (still < StillTicks) return true;
                found = tick + 1;
                return false;
            }, extraTicks: 0);
            return found;
        }

        static bool Merges(InputTick previous, InputTick k)
        {
            return !k.press && !k.release && !k.dash && !k.reset && !k.ArmsLock && !previous.ArmsLock && k.pause == 0
                && previous.x == k.x && previous.y == k.y && previous.held == k.held;
        }

        // ToSteps' list and steps, reused run to run: the engine plays them only inside the Run that made them (Run ends it),
        // and a capture leaves them out (FieldState.For skips MacroEngine._steps).
        readonly List<MacroStep> _runSteps = new List<MacroStep>();
        readonly List<MacroStep> _stepPool = new List<MacroStep>();
        static readonly object[] FreshStep = FieldState.Capture(new MacroStep(false));

        List<MacroStep> ToSteps(IReadOnlyList<InputTick> ticks, int from)
        {
            List<MacroStep> steps = _runSteps;
            steps.Clear();
            for (int i = from; i < ticks.Count; i++)
            {
                InputTick k = ticks[i];
                MacroStep last = steps.Count > 0 ? steps[steps.Count - 1] : null;
                if (last != null && Merges(ticks[i - 1], k))
                {
                    last.durationTicks++;
                    continue;
                }
                MacroStep step = NextStep(steps);
                step.label = "Solver input"; step.type = MacroStepType.RawInput; step.durationTicks = 1;
                step.direction = k.x > 0f ? 1 : k.x < 0f ? -1 : 0; step.rawMoveX = k.x; step.rawMoveY = k.y; step.rawJumpHeld = k.held;
                step.rawJumpPressed = k.press; step.rawJumpReleased = k.release; step.rawDashPressed = k.dash;
                step.dashJumpFrame = k.dash ? k.dashJump : 0; step.springDashFrame = k.springDash; step.lockMoveX = k.lockX; step.lockMoveY = k.lockY;
                step.dashTurnX = k.dash ? k.turnX : null; step.dashTurnY = k.dash ? k.turnY : null;
                step.rawQuickRestart = k.reset; step.pauseFrames = k.pause; step.pauseTick = k.pause > 0 ? k.pauseTick : 0;
                step.pauseFrame = k.pause > 0 ? k.pauseFrame : 0;
                steps.Add(step);
            }
            if (steps.Count == 0)
            {
                MacroStep step = NextStep(steps);
                step.label = "Solver input"; step.type = MacroStepType.RawInput; step.durationTicks = 1;
                steps.Add(step);
            }
            return steps;
        }

        /// <summary>The pooled step for the next slot of <paramref name="steps"/>, every field as a new MacroStep(false) has it
        /// (the merge above and MacroEngine's NormalizeFrameTiming rewrite some).</summary>
        MacroStep NextStep(List<MacroStep> steps)
        {
            if (steps.Count == _stepPool.Count) _stepPool.Add(new MacroStep(false));
            MacroStep step = _stepPool[steps.Count];
            FieldState.Restore(step, FreshStep);
            return step;
        }

        public int MovementFingerprint()
        {
            var h = new HashCode();
            object[] values = FieldValues(Player);
            for (int i = 0; i < Presentation.Length; i++)
                if (!Presentation[i]) AddValue(ref h, values[i], true);
            AddPlayer(ref h);
            return h.ToHashCode();
        }

        internal static readonly HashSet<string> MovementPresentationFields = new HashSet<string> { "timeSinceLastAfterimage", "afterimageTimeAlive", "activeAfterimages", "inactiveAfterimages" };
        /// <summary>Per player field (<see cref="FieldState.For"/> order): one of <see cref="MovementPresentationFields"/>.</summary>
        bool[] Presentation => _presentation ??= FieldState.For(Player.GetType()).Select(f => MovementPresentationFields.Contains(f.Name)).ToArray();
        bool[] _presentation;
    }
}
