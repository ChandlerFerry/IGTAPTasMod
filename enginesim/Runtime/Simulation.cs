using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // One solver input tick (TasPlugin.SolverTick).
    public struct InputTick : IEquatable<InputTick>
    {
        public float x;
        public bool held, press, release, dash;
        public int dashJump;

        public bool Equals(InputTick o) { return x == o.x && held == o.held && press == o.press && release == o.release && dash == o.dash && dashJump == o.dashJump; }
        public override bool Equals(object o) { return o is InputTick t && Equals(t); }
        public override int GetHashCode() { return HashCode.Combine(x, held, press, release, dash, dashJump); }
    }
    public sealed class RunResult
    {
        public bool Finished;
        public float Time;
        public int FinishTick;
        // Physics ticks from the run start through the finish (dash ticks included): the whole run, a flying start's
        // setup before its last start-gate crossing included.
        public long PhysicsTicks;
        public int Stepped;
        public Vector2 End;
        public string EndReason;
    }

    // Everything a run changes, captured after a tick so another run can continue from it.
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
        // Shop: what the run spent, per currency.
        public double[] Spent;
        // Simulation.LapStart at this point.
        public long LapStart;
    }

    // Steps the game's own code exactly like TurboRunner.Run inside TasPlugin.Evaluate: pending Invokes,
    // Movement.FixedUpdate (the macro engine injects inputs from its getPlayerControlledInput hook), every active
    // course's FixedUpdate, Physics2D.Simulate, Movement.Update.
    public sealed class Simulation
    {
        static readonly FieldInfo TrackingField = typeof(courseScript).GetField("tracking", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo PathTimeField = typeof(courseScript).GetField("currentPathTime", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo FixedUpdateMethod = typeof(Movement).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo RespawnMethod = typeof(Movement).GetMethod("respawn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo UpdateMethod = typeof(Movement).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo CourseFixedUpdateMethod = typeof(courseScript).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly string[] CoursePathLists = { "currentPlayerPath", "currentPlayerSprites", "currentPlayerScales" };

        public readonly World World;
        public readonly Movement Player;
        public readonly courseScript[] Courses;
        internal readonly MacroEngine Engine = new MacroEngine { Quiet = true };
        public readonly Scheduler Scheduler = new Scheduler();
        public readonly PhysicsWorld Physics;
        public readonly Shop Shop;
        readonly Action _fixedUpdate, _update;
        readonly Action[] _courseFixedUpdates;
        readonly System.Collections.IList[][] _coursePathLists;
        readonly BoxCollider2D[] _playerColliders;
        SimSnapshot _start;
        bool _finished;
        float _finishTime;
        string _endReason;
        // Game scripts whose fields a physics callback may have changed since the last restore, with their fields
        // as loaded, so a restore can put them back.
        readonly Dictionary<MonoBehaviour, object[]> _pristine = new Dictionary<MonoBehaviour, object[]>();
        readonly HashSet<MonoBehaviour> _touched = new HashSet<MonoBehaviour>();

        public Simulation(World world)
        {
            World = world;
            IGTAP.EngineSim.Engine.Scheduler = Scheduler;
            Player = world.All<Movement>().First(m => m.gameObject.activeInHierarchy && !(bool)typeof(Movement).GetField("isOnMainMenu",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(m));
            // TurboRunner: FindObjectsByType<courseScript>(None) finds courses on active objects.
            Courses = world.All<courseScript>().Where(c => c.gameObject.activeInHierarchy).ToArray();
            Physics = new PhysicsWorld(world, this);
            IGTAP.EngineSim.Engine.Physics = Physics;
            Shop = new Shop(this, world);
            _fixedUpdate = (Action)Delegate.CreateDelegate(typeof(Action), Player, FixedUpdateMethod);
            _update = (Action)Delegate.CreateDelegate(typeof(Action), Player, UpdateMethod);
            _courseFixedUpdates = Courses.Select(c => (Action)Delegate.CreateDelegate(typeof(Action), c, CourseFixedUpdateMethod)).ToArray();
            _coursePathLists = Courses.Select(c => CoursePathLists
                .Select(n => typeof(courseScript).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c) as System.Collections.IList)
                .Where(l => l != null).ToArray()).ToArray();
            _playerColliders = Player.GetComponents<BoxCollider2D>();
            Engine.AttemptEnded += (success, reason, step, ticks) =>
            {
                _finished = success && MacroEngine.IsFinishReason(reason);
                _finishTime = Engine.AuthoritativeCompletionTime;
                _endReason = reason;
            };
            Hooks.PlayerInput = m => Engine.FixedTick((Movement)m);
            Hooks.DashFrame = m => Engine.DashFrame((Movement)m);
            Hooks.Death = m =>
            {
                // TasPlugin.AfterPlayerDeath (a postfix there; nothing onDeath does reads the injected input).
                if (!Engine.IsRunning) return;
                Engine.ClearInjectedInput((Movement)m);
                Engine.Abort("Player death at action " + (Engine.StepIndex + 1));
            };
            Hooks.CourseStop = CourseStop;
            Hooks.UpgradeBoxStay = Shop.Stay;
        }

        // courseScript.stopTracking as far as a run can observe it, plus TasPlugin's Before/AfterCourseFinish.
        void CourseStop(object courseObject, GameObject player, bool savePositionData)
        {
            var course = (courseScript)courseObject;
            if (!(bool)TrackingField.GetValue(course)) return;
            float time = (float)PathTimeField.GetValue(course);
            TrackingField.SetValue(course, false);
            player.GetComponent<Movement>().courseResetPoint = Vector2.zero;
            foreach (string list in CoursePathLists)
                (typeof(courseScript).GetField(list, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(course) as System.Collections.IList)?.Clear();
            PathTimeField.SetValue(course, 0f);
            if (savePositionData && (TargetCourse == 0 || course.courseNumber == TargetCourse))
                Engine.CompleteFromGame("Course finish detected", FromRunStart ? (Engine.PhysicsTicks - Math.Max(0L, LapStart)) * World.FixedDeltaTime : time);
        }

        // The bonus course's reward box (endX, endY, endW, endH) as a world rect the player's colliders may touch.
        bool TouchesReward()
        {
            float x0 = TargetFinish.x - TargetFinish.w / 2f, x1 = TargetFinish.x + TargetFinish.w / 2f;
            float y0 = TargetFinish.y - TargetFinish.h / 2f, y1 = TargetFinish.y + TargetFinish.h / 2f;
            foreach (BoxCollider2D box in _playerColliders)
            {
                if (!box.enabled) continue;
                // PhysicsWorld's player-box geometry: offset and size in lossy scale around the transform.
                Vector3 scale = box.transform.m_LossyScale;
                Vector3 center = Player.transform.m_Position;
                center.x += box.offset.x * scale.x;
                center.y += box.offset.y * scale.y;
                float hx = box.size.x * MathF.Abs(scale.x) / 2f, hy = box.size.y * MathF.Abs(scale.y) / 2f;
                if (center.x + hx > x0 && center.x - hx < x1 && center.y + hy > y0 && center.y - hy < y1) return true;
            }
            return false;
        }

        internal void Touch(MonoBehaviour script)
        {
            if (script == Player || script is courseScript || !_touched.Add(script)) return;
            if (!_pristine.ContainsKey(script)) _pristine[script] = FieldState.Capture(script);
        }

        // The course a run is scored on (the trace's course); 0 accepts any. Finishing another course is not a finish.
        public int TargetCourse;

        // The bonus course's reward box, or a transition's end box (the trace's "finish": endX, endY, endW, endH); zero
        // size = a gated course or a purchase (Shop: the game's buy rule finishes it).
        public (float x, float y, float w, float h) TargetFinish;

        // A transition (the trace's "transition"): timed in physics ticks from the run start (dash ticks included), and
        // finishes at its end box or when course `TargetStart`'s start gate starts its timer (the trace's "finishStart").
        public bool Transition { get; private set; }
        public int TargetStart { get; private set; }
        courseScript _targetStartCourse;

        // Bonus, transition and purchase runs have no course timer of their own; they are timed from the run start.
        public bool TimedFromRunStart { get { return TargetFinish.w > 0f || Transition || Shop.IsPurchase; } }

        // A chain segment (the trace's "fromRunStart"): a course segment is timed from the run start too (TasPlugin.
        // ScoreFromRunStart), and one after a quick restart does it first (the trace's "restart").
        public bool FromRunStart { get; private set; }
        // Settable here for SeedAlign's analysis runs (where a standalone route's quick restart puts the player).
        public bool RestartAtStart { get; internal set; }

        // A lap run with its lead-in (the trace's "lead": TasPlugin.EngineSimTrace): the run starts where the transition
        // before the course segment starts, and the lap starts when the course's start gate first starts its timer, the
        // transition's finish. The lap is timed in physics ticks from there. A run whose transition would take more than
        // `LeadDeadline` physics ticks (the transition's saved route) is cut. 0: no lead-in.
        public long LeadDeadline { get; private set; }
        // Physics ticks from the run start to the lap's start: 0 without a lead-in, -1 before the gate.
        public long LapStart { get; private set; }
        courseScript _lapCourse;

        // What the solver ranks a run by so far: physics ticks from the lap start after a lead-in (-1 before it), from the
        // run start for bonus, transition, purchase and chain segment runs, else the course timer (-1 before it runs).
        public float Elapsed(courseScript active)
        {
            if (LeadDeadline > 0) return LapStart < 0 ? -1f : (Engine.PhysicsTicks - LapStart) * World.FixedDeltaTime;
            if (TimedFromRunStart || FromRunStart) return Engine.PhysicsTicks * World.FixedDeltaTime;
            return active == null ? -1f : CourseTime(active);
        }

        // What a run is scored on, from a trace or start-trace header (TasPlugin.EngineSimTrace writes it).
        public void SetTarget(JsonElement header)
        {
            TargetCourse = header.GetProperty("course").GetInt32();
            if (header.TryGetProperty("finish", out JsonElement finish))
                TargetFinish = (finish[0].GetSingle(), finish[1].GetSingle(), finish[2].GetSingle(), finish[3].GetSingle());
            Transition = header.TryGetProperty("transition", out JsonElement transition) && transition.GetBoolean();
            FromRunStart = header.TryGetProperty("fromRunStart", out JsonElement fromStart) && fromStart.GetBoolean();
            RestartAtStart = header.TryGetProperty("restart", out JsonElement restart) && restart.GetBoolean();
            if (header.TryGetProperty("purchase", out JsonElement purchase)) Shop.SetTarget(purchase);
            if (header.TryGetProperty("finishStart", out JsonElement next))
            {
                TargetStart = next.GetInt32();
                _targetStartCourse = Courses.FirstOrDefault(c => c.courseNumber == TargetStart)
                    ?? throw new InvalidOperationException("Course " + TargetStart + " is not active in this world export.");
            }
            LeadDeadline = header.TryGetProperty("lead", out JsonElement lead) ? lead.GetProperty("ticks").GetInt64() : 0;
            LapStart = LeadDeadline > 0 ? -1 : 0;
            _lapCourse = LeadDeadline > 0 ? Courses.FirstOrDefault(c => c.courseNumber == TargetCourse)
                ?? throw new InvalidOperationException("Course " + TargetCourse + " is not active in this world export.") : null;
        }

        public courseScript TimingCourse()
        {
            foreach (courseScript c in Courses) if ((TargetCourse == 0 || c.courseNumber == TargetCourse) && (bool)TrackingField.GetValue(c)) return c;
            return null;
        }

        public static float CourseTime(courseScript course) { return (float)PathTimeField.GetValue(course); }

        // The turbo start state (the solver's _startState) from a trace export.
        public void LoadStart(JsonElement start)
        {
            ApplyFields(Player, start.GetProperty("movement"));
            var engineFields = FieldState.For(typeof(MacroEngine));
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
            foreach (JsonElement p in start.GetProperty("pending").EnumerateArray())
                Scheduler.Queue.Add(new Scheduler.Pending { Due = p[0].GetInt32(), Target = Player, Method = p[1].GetString(), Repeat = -1f });
            Physics.ResetPlayer(World.Vec2(start.GetProperty("bodyPosition")), World.Vec2(start.GetProperty("velocity")),
                World.ReadFloat(start.GetProperty("rotation")), World.ReadFloat(start.GetProperty("angularVelocity")));
            if (start.TryGetProperty("economy", out JsonElement economy)) Shop.LoadStart(economy, ApplyFields);
            _start = Capture(0);
        }

        void ApplyFields(object target, JsonElement fields)
        {
            foreach (JsonElement entry in fields.EnumerateArray())
            {
                FieldInfo f = World.FindField(target.GetType(), entry[0].GetString(), entry[1].GetString());
                if (f == null) continue;
                object value = World.Read(entry[2], f.FieldType);
                // Engine-side plain objects (InputAction, ...) export as null; keep the world's inert stand-in.
                if (value == null && f.FieldType.Assembly == typeof(UnityEngine.Object).Assembly && !typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                    continue;
                f.SetValue(target, value);
            }
        }

        public SimSnapshot Start { get { return _start; } }

        // `tick` is the input index the snapshot resumes at. Pending Invokes are stored relative to the next tick.
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
                Movement = FieldState.Capture(Player),
                Engine = Engine.CaptureState(),
                Courses = Courses.Select(c => FieldState.Capture(c)).ToArray(),
                Position = Player.transform.m_Position,
                LocalScale = Player.transform.m_LocalScale,
                Rotation = Player.transform.m_Rotation,
                Colliders = _playerColliders.Select(c => (c.offset, c.size, c.excludeLayers.value, c.enabled)).ToArray(),
                Pending = pending,
                Physics = Physics.Capture(),
                Touched = _touched.Select(s => (s, FieldState.Capture(s))).ToArray(),
                Spent = Shop.Capture(),
                LapStart = LapStart,
            };
        }

        // A hash of everything the rest of a run depends on besides its inputs and the (time-driven) rest of the world:
        // the player's scripts, transform, colliders and body, the courses, pending Invokes, and the scripts a callback
        // touched. Two runs of the same inputs with equal fingerprints at the same tick continue identically (up to a
        // hash collision or Box2D contact caches; a false match only costs the solver one mutation).
        public int Fingerprint()
        {
            var h = new HashCode();
            AddFields(ref h, Player);
            foreach (courseScript c in Courses) AddFields(ref h, c);
            Transform t = Player.transform;
            h.Add(t.m_Position); h.Add(t.m_LocalScale); h.Add(t.m_Rotation);
            foreach (BoxCollider2D c in _playerColliders) { h.Add(c.m_Offset); h.Add(c.m_Size); h.Add(c.m_ExcludeLayers); h.Add(c.m_Enabled); }
            h.Add(Physics.PlayerBody());
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
            h.Add(LapStart);
            Shop.AddTo(ref h);
            return h.ToHashCode();
        }

        static void AddFields(ref HashCode h, object target)
        {
            foreach (FieldInfo f in FieldState.For(target.GetType())) AddValue(ref h, f.GetValue(target));
        }

        static void AddValue(ref HashCode h, object value)
        {
            switch (value)
            {
                case null: h.Add(0); break;
                case UnityEngine.Object o: h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o)); break;
                case string s: h.Add(s); break;
                case System.Collections.IEnumerable items:
                    foreach (object item in items) AddValue(ref h, item);
                    h.Add(-1);
                    break;
                default:
                    h.Add(value.GetType().IsValueType ? value.GetHashCode() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value));
                    break;
            }
        }

        public void Restore(SimSnapshot s)
        {
            foreach (MonoBehaviour script in _touched) FieldState.Restore(script, _pristine[script]);
            _touched.Clear();
            foreach (var (target, fields) in s.Touched)
            {
                FieldState.Restore(target, fields);
                _touched.Add(target);
            }
            FieldState.Restore(Player, s.Movement);
            for (int i = 0; i < Courses.Length; i++)
            {
                FieldState.Restore(Courses[i], s.Courses[i]);
                // The ghost-replay recording FieldState skips; nothing a run is judged by reads it, but it must not grow forever.
                foreach (System.Collections.IList list in _coursePathLists[i]) list.Clear();
            }
            Transform t = Player.transform;
            t.m_Position = s.Position; t.m_LocalPosition = t.m_Parent == null ? s.Position : s.Position - t.m_Parent.m_Position;
            t.m_LocalScale = s.LocalScale; t.m_LossyScale = s.LocalScale;
            t.m_Rotation = s.Rotation; t.m_LocalRotation = s.Rotation;
            for (int i = 0; i < _playerColliders.Length; i++)
            {
                BoxCollider2D c = _playerColliders[i];
                c.m_Offset = s.Colliders[i].offset;
                c.m_Size = s.Colliders[i].size;
                c.m_ExcludeLayers = s.Colliders[i].exclude;
                c.m_Enabled = s.Colliders[i].enabled;
            }
            Scheduler.Queue.Clear();
            Scheduler.Tick = 0;
            foreach (var (due, target, method, repeat) in s.Pending)
                Scheduler.Queue.Add(new Scheduler.Pending { Due = due, Target = target, Method = method, Repeat = repeat });
            Physics.Restore(s.Physics);
            LapStart = s.LapStart;
            Shop.Restore(s.Spent);
        }

        // TasPlugin.Evaluate for one input list resumed from `from` (the start state when null). `afterTick` sees the
        // engine's run tick after every stepped tick; returning false stops the run (the solver's "losing" cut).
        public RunResult Run(IReadOnlyList<InputTick> inputs, SimSnapshot from = null, Func<int, bool> afterTick = null, int extraTicks = 400,
            Action<int> onStep = null)
        {
            from ??= _start;
            if (from.Tick >= inputs.Count) from = _start;
            // A chain segment's quick restart is its first act (TasPlugin.BeforeMovementFixedUpdate), before the end-box
            // check can see it; a run resumed past the start already did it.
            bool restart = RestartAtStart && ReferenceEquals(from, _start);
            Restore(from);
            int step = from.Tick;
            while (step > 0 && Merges(inputs[step - 1], inputs[step])) step--;
            Engine.Resume(from.Engine, ToSteps(inputs, step), from.Tick - step);
            _finished = false;
            _finishTime = 0f;
            _endReason = null;
            int maxTicks = inputs.Count - from.Tick + extraTicks;
            // The run `from` came from already reported its tick (ticks mid-dash take no input, so RunTicks can stay put).
            int idleTicks = 0, lastTick = from.Tick > 0 ? from.Tick : -1, stepped = 0;
            bool stop = false;
            for (Scheduler.Tick = 0; Scheduler.Tick < maxTicks && Engine.IsRunning && idleTicks < 20 && !stop; Scheduler.Tick++)
            {
                bool nextWasTracking = _targetStartCourse != null && (bool)TrackingField.GetValue(_targetStartCourse);
                bool lapWasTracking = LapStart < 0 && (bool)TrackingField.GetValue(_lapCourse);
                Scheduler.FireDue();
                if (restart)
                {
                    restart = false;
                    RespawnMethod.Invoke(Player, new object[] { true });
                }
                _fixedUpdate();
                Engine.CountPhysicsTick();
                for (int i = 0; i < Courses.Length; i++)
                    if (Courses[i].isActiveAndEnabled) _courseFixedUpdates[i]();
                Shop.TickCooldowns(World.FixedDeltaTime);
                Physics.Simulate(World.FixedDeltaTime);
                _update();
                // TasPlugin.BeforeMovementFixedUpdate: a bonus run finishes on its reward box (no end gate), a transition
                // on its end box; both in physics ticks from the run start (dash ticks included).
                if (Engine.IsRunning && TargetFinish.w > 0f && TouchesReward())
                    Engine.CompleteFromGame(Transition ? MacroEngine.TransitionFinishReason : MacroEngine.RewardFinishReason,
                        Engine.PhysicsTicks * World.FixedDeltaTime);
                // TasPlugin.BeforeCourseStart: a transition finishes when the next course's start gate starts its
                // timer (this tick's triggers), in physics ticks from the run start.
                if (Engine.IsRunning && !nextWasTracking && _targetStartCourse != null && (bool)TrackingField.GetValue(_targetStartCourse))
                    Engine.CompleteFromGame(MacroEngine.TransitionFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
                // A lead-in: the lap starts where the transition would finish, and a run slower to get there is cut.
                if (Engine.IsRunning && LapStart < 0)
                {
                    if (!lapWasTracking && (bool)TrackingField.GetValue(_lapCourse)) LapStart = Engine.PhysicsTicks;
                    else if (Engine.PhysicsTicks >= LeadDeadline) Engine.Abort("Lead-in slower than the transition's route");
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
            if (Engine.IsRunning) Engine.Abort("Solver run ended");
            return new RunResult
            {
                Finished = _finished && _finishTime > 0f,
                Time = _finishTime,
                FinishTick = Math.Min(inputs.Count - 1, Math.Max(0, lastTick)),
                Stepped = stepped,
                PhysicsTicks = Engine.PhysicsTicks,
                End = Player.transform.m_Position,
                EndReason = _endReason,
            };
        }

        static bool Merges(InputTick previous, InputTick k)
        {
            return !k.press && !k.release && !k.dash && previous.x == k.x && previous.held == k.held;
        }

        // TasPlugin.ToSteps: consecutive identical ticks merge into one raw-input step.
        public static List<MacroStep> ToSteps(IReadOnlyList<InputTick> ticks, int from)
        {
            var steps = new List<MacroStep>();
            int tickMs = Mathf.RoundToInt(Time.fixedDeltaTime * 1000f);
            for (int i = from; i < ticks.Count; i++)
            {
                InputTick k = ticks[i];
                MacroStep last = steps.Count > 0 ? steps[steps.Count - 1] : null;
                if (last != null && Merges(ticks[i - 1], k))
                {
                    last.durationTicks++;
                    last.durationMs = Mathf.RoundToInt(last.durationTicks * Time.fixedDeltaTime * 1000f);
                    continue;
                }
                steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1,
                    durationMs = tickMs, endCondition = ActionEndCondition.FixedTicks,
                    direction = k.x > 0f ? 1 : k.x < 0f ? -1 : 0, rawMoveX = k.x, rawJumpHeld = k.held,
                    rawJumpPressed = k.press, rawJumpReleased = k.release, rawDashPressed = k.dash,
                    dashJumpFrame = k.dash ? k.dashJump : 0 });
            }
            if (steps.Count == 0) steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1, durationMs = tickMs,
                endCondition = ActionEndCondition.FixedTicks });
            return steps;
        }
    }
}
