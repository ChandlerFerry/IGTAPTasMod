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
        // The stick: x as MacroEngine.ControllerAxis takes it, y 0 or ±1 (down: fast fall and faster wall slides, up:
        // slower wall slides).
        public float x, y;
        public bool held, press, release, dash;
        public int dashJump;
        // The game's quick restart before this tick (MacroStep.rawQuickRestart).
        public bool reset;

        public bool Equals(InputTick o) { return x == o.x && y == o.y && held == o.held && press == o.press && release == o.release && dash == o.dash && dashJump == o.dashJump && reset == o.reset; }
        public override bool Equals(object o) { return o is InputTick t && Equals(t); }
        public override int GetHashCode() { return HashCode.Combine(x, y, held, press, release, dash, dashJump, reset); }

        // A tick as routes, traces and offline bests write it (SolverTick's fields; y and reset may be absent).
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
                reset = t.TryGetProperty("reset", out JsonElement reset) && reset.GetBoolean(),
            };
        }

        // As FromJson reads it (and the plugin's SolverTick).
        public object ToJson() { return new { x, y, held, press, release, dash, dashJump, reset }; }
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
        // A Full-TAS run (Simulation.Goals): goals reached by the end, and when (inputs from the run start, i.e. with
        // Simulation.InputBase added; physics ticks from the run start). Null otherwise.
        public int Progress;
        public long[] GoalInputTicks, GoalPhysicsTicks;
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
        // GameObjects a run activated or deactivated and Behaviours it enabled or disabled (the trip breaker's), as
        // they are here; any other of them is as loaded.
        public (GameObject target, bool active)[] Active;
        public (Behaviour target, bool enabled)[] Enabled;
        // Shop: what the run spent, per currency, the native economy's state (Economy) and the buy-max purchases waiting.
        internal Shop.State Economy;
        // Full-TAS: the goal tracker's state (null outside a Full-TAS run).
        internal FullTasTracker.State Goal;
        // UnityEngine.Random's state (the trace's "random"), so a restored run rolls what the original did.
        public UnityEngine.Random.State Random;
        // OfflineSolver.ForcedPath: how many of the forced path's boxes the run had passed here (the solver sets it).
        public int Waypoints;
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
        // The GameObjects and Behaviours a run toggled (SetActive, SetEnabled), with their flag as loaded.
        readonly Dictionary<GameObject, bool> _activeAsLoaded = new Dictionary<GameObject, bool>();
        readonly Dictionary<Behaviour, bool> _enabledAsLoaded = new Dictionary<Behaviour, bool>();

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
            IGTAP.EngineSim.Engine.Stick = () => Engine.DeviceStick;
            Hooks.DashFrame = m => Engine.DashFrame((Movement)m);
            Hooks.Death = m =>
            {
                // TasPlugin.AfterPlayerDeath (a postfix there; nothing onDeath does reads the injected input).
                if (!Engine.IsRunning) return;
                Engine.ClearInjectedInput((Movement)m);
                Engine.Abort("Player death at action " + (Engine.StepIndex + 1));
            };
            Hooks.CourseStop = CourseStop;
            // TasPlugin.BeforeRespawn.
            Hooks.Respawn = m => ((Movement)m).isRespawningAtCheckpoints = false;
            Hooks.UpgradeBoxStay = Shop.Stay;
            // The sound code's UnityEngine.Random draws (cuts.txt empties playSFX and Audio.PlayAudio; hooks.txt keeps
            // the draws their game bodies make). Each draw moves the state one word (Random.Draw), so the counts are
            // the whole contract; the values go to sound only.
            Hooks.PlaySfx = (_, code) =>
            {
                // playSFX: a clip draw (or the wall-slide time draw), then always a pitch draw; land/grassLand
                // instead make an Audio.PlayAudio(..., pitchVariance: 0.15f): its clip and pitch draws, and then
                // playSFX's own pitch draw.
                switch (code)
                {
                    case "land":
                    case "grassLand": UnityEngine.Random.Draw(); UnityEngine.Random.Draw(); break;
                    case "bigJump":
                    case "smallJump":
                    case "wallSlide":
                    case "grassWallSlide":
                    case "dash":
                    case "airJump":
                    case "death": UnityEngine.Random.Draw(); break;
                }
                UnityEngine.Random.Draw();
            };
            Hooks.PlayAudio = (_, _2, _3, pitchVariance) =>
            {
                // Audio.PlayAudio: a clip draw (every AUDIO_ID's library has at least one clip) and a pitch draw when
                // the caller passed a pitch variance.
                UnityEngine.Random.Draw();
                if (pitchVariance != 0f) UnityEngine.Random.Draw();
            };
        }

        // ---- Full-TAS (FullTas.cs): the run goes on through every goal and finishes at the last one of `Goals.Limit`.
        // Goals are reached exactly where the plugin sees them: a course's start gate starting its timer, its end gate
        // stopping it (the player's own finish), the game buying a box (a buy goal's, or a bonus's reward box).
        internal FullTasTracker Goals { get; private set; }
        // Inputs consumed before this simulation's start (a window's prefix, SetStart): goal input ticks are counted from
        // the run start, like the route's inputs.
        public long InputBase;
        public int Progress { get { return Goals == null ? 0 : Goals.Next; } }

        void GoalEvent(bool reached)
        {
            if (reached && Goals.Done && Engine.IsRunning)
                Engine.CompleteFromGame(MacroEngine.FullTasFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
        }

        // Shop: the game bought the box at `path` with id `id` (FullTasGoal.BoxId).
        internal void Bought(string path, string id)
        {
            if (Goals != null && Engine.IsRunning) GoalEvent(Goals.Bought(path, id, InputBase + Engine.RunTicks, Engine.PhysicsTicks));
        }

        // Replays `prefix` from the start and returns the state right after its last input, as a start for runs of the
        // inputs that follow it (their own input count from 0, SetStart); null if the run ended first (a death, the
        // last goal). The goals reached so far are in the state.
        public SimSnapshot Advance(IReadOnlyList<InputTick> prefix)
        {
            if (prefix.Count == 0) return _start;
            SimSnapshot at = null;
            // One more input than the prefix, so the run is still going when its last input's tick ends.
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

        // Runs start from `state` from now on (Advance's): `inputBase` inputs were consumed before it.
        public void SetStart(SimSnapshot state, long inputBase)
        {
            _start = state;
            InputBase = inputBase;
        }

        // courseScript.stopTracking as far as a run can observe it, plus TasPlugin's Before/AfterCourseFinish.
        void CourseStop(object courseObject, GameObject player, bool savePositionData)
        {
            var course = (courseScript)courseObject;
            if (!(bool)TrackingField.GetValue(course)) return;
            float time = (float)PathTimeField.GetValue(course);
            TrackingField.SetValue(course, false);
            player.GetComponent<Movement>().courseResetPoint = Vector2.zero;
            // The finish's reward, the clones' new path (Economy; only with the native economy).
            if (savePositionData) Shop.CourseFinished(course, time);
            foreach (string list in CoursePathLists)
                (typeof(courseScript).GetField(list, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(course) as System.Collections.IList)?.Clear();
            PathTimeField.SetValue(course, 0f);
            if (Goals != null)
            {
                if (savePositionData && Engine.IsRunning) GoalEvent(Goals.CourseFinished(course.courseNumber, InputBase + Engine.RunTicks, Engine.PhysicsTicks));
            }
            else if (savePositionData && (TargetCourse == 0 || course.courseNumber == TargetCourse))
                Engine.CompleteFromGame("Course finish detected", time);
        }

        // The bonus course's reward box (endX, endY, endW, endH) as a world rect the player's colliders may touch.
        bool TouchesReward() { return Touches(TargetFinish.x, TargetFinish.y, TargetFinish.w, TargetFinish.h); }

        // Whether one of the player's colliders touches the world rect at (x, y) (centre) of size (w, h).
        bool Touches(float x, float y, float w, float h)
        {
            float x0 = x - w / 2f, x1 = x + w / 2f;
            float y0 = y - h / 2f, y1 = y + h / 2f;
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

        // GameObject.SetActive / Behaviour.enabled from a modeled game method, so snapshots carry the flag.
        internal void SetActive(GameObject gameObject, bool active)
        {
            if (!_activeAsLoaded.ContainsKey(gameObject)) _activeAsLoaded[gameObject] = gameObject.activeSelf;
            gameObject.SetActive(active);
        }

        internal void SetEnabled(Behaviour behaviour, bool enabled)
        {
            if (!_enabledAsLoaded.ContainsKey(behaviour)) _enabledAsLoaded[behaviour] = behaviour.enabled;
            behaviour.enabled = enabled;
        }

        // The course a run is scored on (the trace's course); 0 accepts any. Finishing another course is not a finish.
        public int TargetCourse;

        // The bonus course's reward box, or a transition's end box (the trace's "finish": endX, endY, endW, endH); zero
        // size = a gated course.
        public (float x, float y, float w, float h) TargetFinish;

        // A transition (the trace's "transition"): timed in physics ticks from the run start (dash ticks included), and
        // finishes at its end box or when course `TargetStart`'s start gate starts its timer (the trace's "finishStart").
        public bool Transition { get; private set; }
        public int TargetStart { get; private set; }
        courseScript _targetStartCourse;

        // Bonus, transition and Full-TAS runs have no course timer of their own; they are timed from the run start.
        public bool TimedFromRunStart { get { return TargetFinish.w > 0f || Transition || Goals != null; } }

        // What the solver ranks a run by so far: physics ticks from the run start for bonus, transition and Full-TAS
        // runs, else the course timer (-1 before it runs).
        public float Elapsed(courseScript active)
        {
            if (TimedFromRunStart) return Engine.PhysicsTicks * World.FixedDeltaTime;
            return active == null ? -1f : CourseTime(active);
        }

        // What a run is scored on, from a trace or start-trace header (TasPlugin.EngineSimTrace writes it).
        public void SetTarget(JsonElement header)
        {
            TargetCourse = header.GetProperty("course").GetInt32();
            if (header.TryGetProperty("finish", out JsonElement finish))
                TargetFinish = (finish[0].GetSingle(), finish[1].GetSingle(), finish[2].GetSingle(), finish[3].GetSingle());
            Transition = header.TryGetProperty("transition", out JsonElement transition) && transition.GetBoolean();
            if (header.TryGetProperty("finishStart", out JsonElement next))
            {
                TargetStart = next.GetInt32();
                _targetStartCourse = Courses.FirstOrDefault(c => c.courseNumber == TargetStart)
                    ?? throw new InvalidOperationException("Course " + TargetStart + " is not active in this world export.");
            }
            if (header.TryGetProperty("fulltas", out JsonElement fulltas)) SetGoals(fulltas.GetProperty("goals"));
        }

        // The trace's "fulltas": {name, goals: [{label, kind, course, boxes, rect: [x, y, w, h]}]} (TasPlugin writes it). A
        // reward goal without boxes (a trace from before reward goals were purchases) gets the box nearest its rect.
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
                else throw new InvalidOperationException("Goal " + goal.label + ": unknown kind " + goal.kind + ".");
                list.Add(goal);
            }
            if (list.Count == 0) throw new InvalidOperationException("A Full-TAS trace needs at least one goal.");
            Goals = new FullTasTracker(list.ToArray());
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
            // The start's "random": UnityEngine.Random's state (Random.State's s0..s3 in order) at the start snapshot.
            // A trace without one predates the export; its runs start from a fixed state instead.
            if (start.TryGetProperty("random", out JsonElement random))
                UnityEngine.Random.state = new UnityEngine.Random.State
                {
                    s0 = random[0].GetInt32(), s1 = random[1].GetInt32(), s2 = random[2].GetInt32(), s3 = random[3].GetInt32(),
                };
            else UnityEngine.Random.InitState(0);
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
                Active = _activeAsLoaded.Count == 0 ? Array.Empty<(GameObject, bool)>() : _activeAsLoaded.Keys.Select(g => (g, g.activeSelf)).ToArray(),
                Enabled = _enabledAsLoaded.Count == 0 ? Array.Empty<(Behaviour, bool)>() : _enabledAsLoaded.Keys.Select(b => (b, b.enabled)).ToArray(),
                Economy = Shop.Capture(),
                Goal = Goals?.Capture(),
                Random = UnityEngine.Random.state,
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
            int toggled = 0;
            foreach (GameObject g in _activeAsLoaded.Keys) toggled ^= HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g), g.activeSelf);
            foreach (Behaviour b in _enabledAsLoaded.Keys) toggled ^= HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b), b.enabled);
            h.Add(toggled);
            Shop.AddTo(ref h);
            // The RNG state the rest of the run rolls from.
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            h.Add(rng.s0); h.Add(rng.s1); h.Add(rng.s2); h.Add(rng.s3);
            if (Goals != null) h.Add(Goals.Hash());
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
            // Toggled objects first, without rebuilding their colliders: Physics.Restore brings their fixtures back.
            Physics.Suspended = true;
            foreach (var (g, loaded) in _activeAsLoaded)
            {
                bool active = loaded;
                foreach (var (target, value) in s.Active) if (target == g) { active = value; break; }
                if (g.activeSelf != active) g.SetActive(active);
            }
            foreach (var (b, loaded) in _enabledAsLoaded)
            {
                bool enabled = loaded;
                foreach (var (target, value) in s.Enabled) if (target == b) { enabled = value; break; }
                if (b.enabled != enabled) b.enabled = enabled;
            }
            Physics.Suspended = false;
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
            Shop.Restore(s.Economy);
            if (Goals != null) Goals.Restore(s.Goal);
            UnityEngine.Random.state = s.Random;
        }

        // TasPlugin.Evaluate for one input list resumed from `from` (the start state when null). `afterTick` sees the
        // engine's run tick after every stepped tick; returning false stops the run (the solver's "losing" cut).
        public RunResult Run(IReadOnlyList<InputTick> inputs, SimSnapshot from = null, Func<int, bool> afterTick = null, int extraTicks = 400,
            Action<int> onStep = null)
        {
            from ??= _start;
            if (from.Tick >= inputs.Count) from = _start;
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
                // Full-TAS: a start goal is reached when its course's start gate starts the timer (off before the tick, on
                // after it: TasPlugin counts it in startTracking only when the course wasn't timing).
                FullTasGoal goal = Goals?.Current;
                courseScript startGoal = goal != null && goal.kind == FullTasGoal.Start ? Courses.First(c => c.courseNumber == goal.course) : null;
                bool startWasTracking = startGoal != null && (bool)TrackingField.GetValue(startGoal);
                Scheduler.FireDue();
                // A route's own quick restart (InputTick.reset), where TasPlugin.BeforeMovementFixedUpdate does it.
                if (Engine.TakeQuickRestart()) RespawnMethod.Invoke(Player, new object[] { true });
                _fixedUpdate();
                Engine.CountPhysicsTick();
                for (int i = 0; i < Courses.Length; i++)
                    if (Courses[i].isActiveAndEnabled) _courseFixedUpdates[i]();
                Shop.TickCooldowns(World.FixedDeltaTime);
                Physics.Simulate(World.FixedDeltaTime);
                _update();
                // TurboRunner.StepClones: every course's clones, after Movement.Update.
                Shop.StepClones();
                // TurboRunner.EndOfFrame: the buy-max boxes' next purchases.
                Shop.EndOfFrame();
                // TasPlugin.BeforeMovementFixedUpdate: a bonus run finishes on its reward box (no end gate), a transition
                // on its end box; both in physics ticks from the run start (dash ticks included).
                if (Engine.IsRunning && TargetFinish.w > 0f && TouchesReward())
                    Engine.CompleteFromGame(Transition ? MacroEngine.TransitionFinishReason : MacroEngine.RewardFinishReason,
                        Engine.PhysicsTicks * World.FixedDeltaTime);
                // TasPlugin.BeforeCourseStart: a transition finishes when the next course's start gate starts its
                // timer (this tick's triggers), in physics ticks from the run start.
                if (Engine.IsRunning && !nextWasTracking && _targetStartCourse != null && (bool)TrackingField.GetValue(_targetStartCourse))
                    Engine.CompleteFromGame(MacroEngine.TransitionFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
                if (Goals != null && Engine.IsRunning)
                {
                    if (startGoal != null && !startWasTracking && Goals.Current == goal && (bool)TrackingField.GetValue(startGoal))
                        GoalEvent(Goals.CourseStarted(goal.course, InputBase + Engine.RunTicks, Engine.PhysicsTicks));
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
                Progress = Progress,
                GoalInputTicks = Goals == null ? null : (long[])Goals.InputTicks.Clone(),
                GoalPhysicsTicks = Goals == null ? null : (long[])Goals.PhysicsTicks.Clone(),
            };
        }

        static readonly FieldInfo OnGroundField = typeof(Movement).GetField("onGround", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo VelocityField = typeof(Movement).GetField("Velocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo MomentumField = typeof(Movement).GetField("momentum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        const int StillTicks = 3;

        // Plays `walk`, then no input until the player stands still on the ground for StillTicks ticks. Returns the input
        // count at that point, or -1 if not within `limit` more ticks.
        public int Still(List<InputTick> walk, int limit)
        {
            var inputs = walk.Concat(Enumerable.Repeat(new InputTick(), limit)).ToList();
            int still = 0, found = -1;
            Run(inputs, afterTick: tick =>
            {
                if (tick < walk.Count) return true;
                bool rest = (bool)OnGroundField.GetValue(Player) && ((Vector2)VelocityField.GetValue(Player)).sqrMagnitude < 1e-4f
                    && ((Vector2)MomentumField.GetValue(Player)).sqrMagnitude < 1e-4f;
                still = rest ? still + 1 : 0;
                if (still < StillTicks) return true;
                found = tick + 1;
                return false;
            }, extraTicks: 0);
            return found;
        }

        static bool Merges(InputTick previous, InputTick k)
        {
            return !k.press && !k.release && !k.dash && !k.reset && previous.x == k.x && previous.y == k.y && previous.held == k.held;
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
                    direction = k.x > 0f ? 1 : k.x < 0f ? -1 : 0, rawMoveX = k.x, rawMoveY = k.y, rawJumpHeld = k.held,
                    rawJumpPressed = k.press, rawJumpReleased = k.release, rawDashPressed = k.dash,
                    dashJumpFrame = k.dash ? k.dashJump : 0, rawQuickRestart = k.reset });
            }
            if (steps.Count == 0) steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1, durationMs = tickMs,
                endCondition = ActionEndCondition.FixedTicks });
            return steps;
        }

        // Full-TAS memo (fulltas CLI): a hash of the player alone (its script, transform, colliders and body), equal across
        // simulations of different worlds (objects hash by instance id). A window route found from one start is worth
        // trying from another start with the same player state (after a quick restart, say); it is verified by running it.
        public int MovementFingerprint()
        {
            var h = new HashCode();
            foreach (FieldInfo f in FieldState.For(Player.GetType()))
                if (!MovementPresentationFields.Contains(f.Name)) AddPortableValue(ref h, f.GetValue(Player));
            Transform t = Player.transform;
            h.Add(t.m_Position); h.Add(t.m_LocalScale); h.Add(t.m_Rotation);
            foreach (BoxCollider2D c in _playerColliders) { h.Add(c.m_Offset); h.Add(c.m_Size); h.Add(c.m_ExcludeLayers); h.Add(c.m_Enabled); }
            h.Add(Physics.PlayerBody());
            return h.ToHashCode();
        }

        // Movement fields written only by presentation code (afterimages).
        static readonly HashSet<string> MovementPresentationFields = new HashSet<string> { "timeSinceLastAfterimage", "afterimageTimeAlive", "activeAfterimages", "inactiveAfterimages" };

        static void AddPortableValue(ref HashCode h, object value)
        {
            switch (value)
            {
                case null: h.Add(0); break;
                case UnityEngine.Object o: h.Add(o.GetInstanceID()); break;
                case string s: h.Add(s); break;
                case System.Collections.IEnumerable items:
                    foreach (object item in items) AddPortableValue(ref h, item);
                    h.Add(-1);
                    break;
                default:
                    h.Add(value.GetType().IsValueType ? value.GetHashCode() : value.GetType().FullName.GetHashCode());
                    break;
            }
        }
    }
}
