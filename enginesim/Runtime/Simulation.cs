using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public struct InputTick : IEquatable<InputTick>
    {
        public float x, y;
        public bool held, press, release, dash;
        public int dashJump;
        public int springDash;
        public float? lockX, lockY;
        public bool reset;

        public bool ArmsLock { get { return springDash != 0 || lockX.HasValue || lockY.HasValue; } }

        public bool Equals(InputTick o) { return x == o.x && y == o.y && held == o.held && press == o.press && release == o.release && dash == o.dash && dashJump == o.dashJump && springDash == o.springDash && lockX == o.lockX && lockY == o.lockY && reset == o.reset; }
        public override bool Equals(object o) { return o is InputTick t && Equals(t); }
        public override int GetHashCode() { return HashCode.Combine(x, y, held, press, release, dash, dashJump, HashCode.Combine(springDash, lockX, lockY, reset)); }

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
                reset = t.TryGetProperty("reset", out JsonElement reset) && reset.GetBoolean(),
            };
        }

        public object ToJson() { return new { x, y, held, press, release, dash, dashJump, springDash, lockX, lockY, reset }; }
    }
    public sealed class RunResult
    {
        public bool Finished;
        public float Time;
        public int FinishTick;
        public long PhysicsTicks;
        public int Stepped;
        public string EndReason;
        public int Progress;
        public long[] GoalInputTicks, GoalPhysicsTicks;
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
        internal Shop.State Economy;
        internal FullTasTracker.State Goal;
        public UnityEngine.Random.State Random;
        public int Waypoints;
        public double VmanTimeLeft;
    }

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
        readonly Dictionary<MonoBehaviour, object[]> _pristine = new Dictionary<MonoBehaviour, object[]>();
        readonly HashSet<MonoBehaviour> _touched = new HashSet<MonoBehaviour>();
        // The last field capture of each touched script and course. A capture reuses it while nothing in it changed, so the
        // archive's snapshots share one array per unchanged script (a Full-TAS world's ~100 touched scripts were 130 KB of
        // each 160 KB snapshot). Sharing is safe: Restore copies what it applies, and nothing writes a captured array.
        readonly Dictionary<object, object[]> _captured = new Dictionary<object, object[]>();

        object[] CaptureShared(object target)
        {
            if (_captured.TryGetValue(target, out object[] last) && FieldState.Matches(target, last)) return last;
            return _captured[target] = FieldState.Capture(target);
        }
        readonly Dictionary<GameObject, bool> _activeAsLoaded = new Dictionary<GameObject, bool>();
        readonly Dictionary<Behaviour, bool> _enabledAsLoaded = new Dictionary<Behaviour, bool>();
        // A Vman speedrun's clock: VmanScript.vmanTimeLeft is a static in the game, kept per simulation here because the
        // search's workers share the process's statics. VmanScript.isCurrentlyVman comes from the world export's statics.
        double _vmanTimeLeft;
        bool _vmanBlockEnding;

        public Simulation(World world)
        {
            World = world;
            IGTAP.EngineSim.Engine.Scheduler = Scheduler;
            Player = world.All<Movement>().First(m => m.gameObject.activeInHierarchy && !(bool)typeof(Movement).GetField("isOnMainMenu",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(m));
            Courses = world.All<courseScript>().Where(c => c.gameObject.activeInHierarchy).ToArray();
            Origin = world.All<FloatingOrigin>().Select(o => o.currentOrigin).FirstOrDefault();
            Physics = new PhysicsWorld(world, this);
            IGTAP.EngineSim.Engine.Physics = Physics;
            // Every toggle in a run, the game's own included (colouredBlockSwapper.swapBlocks turns the blue/orange
            // colliders on and off), is put back by Restore: a branch must not start from the last run's blocks.
            IGTAP.EngineSim.Engine.ActiveChanging = g => _activeAsLoaded.TryAdd(g, g.activeSelf);
            IGTAP.EngineSim.Engine.EnabledChanging = b => _enabledAsLoaded.TryAdd(b, b.enabled);
            Shop = new Shop(this, world);
            _fixedUpdate = (Action)Delegate.CreateDelegate(typeof(Action), Player, FixedUpdateMethod);
            _update = (Action)Delegate.CreateDelegate(typeof(Action), Player, UpdateMethod);
            _courseFixedUpdates = Courses.Select(c => (Action)Delegate.CreateDelegate(typeof(Action), c, CourseFixedUpdateMethod)).ToArray();
            _coursePathLists = Courses.Select(c => CoursePathLists
                .Select(n => typeof(courseScript).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c) as System.Collections.IList)
                .Where(l => l != null).ToArray()).ToArray();
            _playerColliders = Player.GetComponents<BoxCollider2D>();
            Engine.AttemptEnded += (success, reason) =>
            {
                _finished = success && MacroEngine.IsFinishReason(reason);
                _finishTime = Engine.AuthoritativeCompletionTime;
                _endReason = reason;
            };
            Hooks.PlayerInput = m => Engine.FixedTick((Movement)m);
            IGTAP.EngineSim.Engine.Stick = () => Engine.TickStick;
            Hooks.WallTouch = (m, touchedGround) => Engine.WallTouch((Movement)m, touchedGround);
            Hooks.Death = m =>
            {
                if (!Engine.IsRunning) return;
                Engine.ClearInjectedInput((Movement)m);
                Engine.Abort("Player death at action " + (Engine.StepIndex + 1));
            };
            Hooks.CourseStop = CourseStop;
            Hooks.Respawn = m => ((Movement)m).isRespawningAtCheckpoints = CheckpointRespawns;
            Hooks.UpgradeBoxStay = Shop.Stay;
            Hooks.SwapBlocks = swapper => Touch((MonoBehaviour)swapper);
            Hooks.PlaySfx = (_, code) =>
            {
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
                UnityEngine.Random.Draw();
                if (pitchVariance != 0f) UnityEngine.Random.Draw();
            };
        }

        internal FullTasTracker Goals { get; private set; }
        public long InputBase;
        public int Progress { get { return Goals == null ? 0 : Goals.Next; } }

        void GoalEvent(bool reached)
        {
            if (reached && Goals.Done && Engine.IsRunning)
                Engine.CompleteFromGame(MacroEngine.FullTasFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
        }

        internal void Bought(string path, string id)
        {
            if (Goals != null && Engine.IsRunning) GoalEvent(Goals.Bought(path, id, InputBase + Engine.RunTicks, Engine.PhysicsTicks));
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

        public void SetStart(SimSnapshot state, long inputBase)
        {
            _start = state;
            InputBase = inputBase;
        }

        void CourseStop(object courseObject, GameObject player, bool savePositionData)
        {
            var course = (courseScript)courseObject;
            if (!(bool)TrackingField.GetValue(course)) return;
            float time = (float)PathTimeField.GetValue(course);
            TrackingField.SetValue(course, false);
            player.GetComponent<Movement>().courseResetPoint = Vector2.zero;
            if (savePositionData) Shop.CourseFinished(course, time);
            // stopTracking returns right after a Vman completion: the path lists and time stay as they are.
            if (!(savePositionData && VmanScript.isCurrentlyVman))
            {
                foreach (string list in CoursePathLists)
                    (typeof(courseScript).GetField(list, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(course) as System.Collections.IList)?.Clear();
                PathTimeField.SetValue(course, 0f);
            }
            if (Goals != null)
            {
                if (savePositionData && Engine.IsRunning) GoalEvent(Goals.CourseFinished(course.courseNumber, InputBase + Engine.RunTicks, Engine.PhysicsTicks));
            }
            else if (savePositionData && (TargetCourse == 0 || course.courseNumber == TargetCourse))
                Engine.CompleteFromGame("Course finish detected", time);
        }

        bool TouchesReward() { return Touches(TargetFinish.x, TargetFinish.y, TargetFinish.w, TargetFinish.h); }

        bool Touches(float x, float y, float w, float h)
        {
            float x0 = x - w / 2f, x1 = x + w / 2f;
            float y0 = y - h / 2f, y1 = y + h / 2f;
            foreach (BoxCollider2D box in _playerColliders)
            {
                if (!box.enabled) continue;
                Vector3 scale = box.transform.m_LossyScale;
                Vector3 center = Player.transform.m_Position;
                center.x += box.offset.x * scale.x;
                center.y += box.offset.y * scale.y;
                float hx = box.size.x * MathF.Abs(scale.x) / 2f, hy = box.size.y * MathF.Abs(scale.y) / 2f;
                if (center.x + hx > x0 && center.x - hx < x1 && center.y + hy > y0 && center.y - hy < y1) return true;
            }
            return false;
        }

        // The player's enabled colliders' bounds (x0, x1, y0, y1): an at: goal's from= side checks them after the tick before.
        (float, float, float, float) PlayerBounds()
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            foreach (BoxCollider2D box in _playerColliders)
            {
                if (!box.enabled) continue;
                Vector3 scale = box.transform.m_LossyScale;
                Vector3 center = Player.transform.m_Position;
                center.x += box.offset.x * scale.x;
                center.y += box.offset.y * scale.y;
                float hx = box.size.x * MathF.Abs(scale.x) / 2f, hy = box.size.y * MathF.Abs(scale.y) / 2f;
                x0 = MathF.Min(x0, center.x - hx); x1 = MathF.Max(x1, center.x + hx);
                y0 = MathF.Min(y0, center.y - hy); y1 = MathF.Max(y1, center.y + hy);
            }
            return (x0, x1, y0, y1);
        }

        internal void Touch(MonoBehaviour script)
        {
            if (script == Player || script is courseScript || !_touched.Add(script)) return;
            if (!_pristine.ContainsKey(script)) _pristine[script] = FieldState.Capture(script);
        }

        // The game's courseNumber of the target course: the header's `gameCourse` for Courses 11-15 (Courses 1-5 overgrown).
        public int TargetCourse;

        public (float x, float y, float w, float h) TargetFinish;

        public bool Transition { get; private set; }
        public int TargetStart { get; private set; }
        courseScript _targetStartCourse;

        public bool TimedFromRunStart { get { return TargetFinish.w > 0f || Transition || Goals != null; } }

        // The studio's "Checkpoint respawns" setting at capture (TasPlugin.CheckpointRespawns): a death respawns at the last
        // checkpoint when on, at the course's reset point when off. Traces without it were captured with it off.
        public bool CheckpointRespawns;

        // The game's FloatingOrigin at capture. The simulation never shifts it, so every position in a run is in the capture's
        // scene frame; the game world (courses.json's frame, the map's, forced-path boxes') is scene - Origin, the same for
        // every capture.
        public readonly Vector3 Origin;

        // The body's unrounded position in the game world (forced-path boxes are checked against it).
        public Vector3 PlayerWorld { get { return Player.transform.m_Position - Origin; } }

        public float Elapsed(courseScript active)
        {
            if (TimedFromRunStart) return Engine.PhysicsTicks * World.FixedDeltaTime;
            return active == null ? -1f : CourseTime(active);
        }

        public void SetTarget(JsonElement header)
        {
            TargetCourse = (header.TryGetProperty("gameCourse", out JsonElement gameCourse) ? gameCourse : header.GetProperty("course")).GetInt32();
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
            CheckpointRespawns = header.TryGetProperty("checkpointRespawns", out JsonElement checkpoints) && checkpoints.GetBoolean();
        }

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
            foreach (courseScript c in Courses) if ((TargetCourse == 0 || c.courseNumber == TargetCourse) && (bool)TrackingField.GetValue(c)) return c;
            return null;
        }

        public static float CourseTime(courseScript course) { return (float)PathTimeField.GetValue(course); }

        public void LoadStart(JsonElement start)
        {
            ApplyFields(Player, start.GetProperty("movement"));
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
            _start = Capture(0);
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
                Courses = Courses.Select(CaptureShared).ToArray(),
                Position = Player.transform.m_Position,
                LocalScale = Player.transform.m_LocalScale,
                Rotation = Player.transform.m_Rotation,
                Colliders = _playerColliders.Select(c => (c.offset, c.size, c.excludeLayers.value, c.enabled)).ToArray(),
                Pending = pending,
                Physics = Physics.Capture(),
                Touched = _touched.Select(s => (s, CaptureShared(s))).ToArray(),
                Active = _activeAsLoaded.Count == 0 ? Array.Empty<(GameObject, bool)>() : _activeAsLoaded.Keys.Select(g => (g, g.activeSelf)).ToArray(),
                Enabled = _enabledAsLoaded.Count == 0 ? Array.Empty<(Behaviour, bool)>() : _enabledAsLoaded.Keys.Select(b => (b, b.enabled)).ToArray(),
                Economy = Shop.Capture(),
                Goal = Goals?.Capture(),
                Random = UnityEngine.Random.state,
                VmanTimeLeft = _vmanTimeLeft,
            };
        }

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
            foreach (MonoBehaviour script in _touched)
            {
                FieldState.Restore(script, _pristine[script]);
                _captured[script] = _pristine[script];
            }
            _touched.Clear();
            foreach (var (target, fields) in s.Touched)
            {
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
            _vmanTimeLeft = s.VmanTimeLeft;
        }

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
            int idleTicks = 0, lastTick = from.Tick > 0 ? from.Tick : -1, stepped = 0;
            bool stop = false;
            for (Scheduler.Tick = 0; Scheduler.Tick < maxTicks && Engine.IsRunning && idleTicks < 20 && !stop; Scheduler.Tick++)
            {
                bool nextWasTracking = _targetStartCourse != null && (bool)TrackingField.GetValue(_targetStartCourse);
                FullTasGoal goal = Goals?.Current;
                courseScript startGoal = goal != null && goal.kind == FullTasGoal.Start ? Courses.First(c => c.courseNumber == goal.course) : null;
                bool startWasTracking = startGoal != null && (bool)TrackingField.GetValue(startGoal);
                Scheduler.FireDue();
                (float px0, float px1, float py0, float py1) = Goals != null ? PlayerBounds() : default;
                if (Engine.TakeQuickRestart())
                {
                    RespawnMethod.Invoke(Player, new object[] { true });
                    Goals?.QuickRestarted();
                }
                Engine.BeforeTick(Player);
                _fixedUpdate();
                Engine.CountPhysicsTick();
                for (int i = 0; i < Courses.Length; i++)
                    if (Courses[i].isActiveAndEnabled) _courseFixedUpdates[i]();
                VmanClock();
                Shop.TickCooldowns(World.FixedDeltaTime);
                Physics.Simulate(World.FixedDeltaTime);
                _update();
                Shop.StepClones();
                Shop.EndOfFrame();
                if (Engine.IsRunning && TargetFinish.w > 0f && TouchesReward())
                    Engine.CompleteFromGame(Transition ? MacroEngine.TransitionFinishReason : MacroEngine.RewardFinishReason,
                        Engine.PhysicsTicks * World.FixedDeltaTime);
                if (Engine.IsRunning && !nextWasTracking && _targetStartCourse != null && (bool)TrackingField.GetValue(_targetStartCourse))
                    Engine.CompleteFromGame(MacroEngine.TransitionFinishReason, Engine.PhysicsTicks * World.FixedDeltaTime);
                if (Goals != null && Engine.IsRunning)
                {
                    if (startGoal != null && !startWasTracking && Goals.Current == goal && (bool)TrackingField.GetValue(startGoal))
                        GoalEvent(Goals.CourseStarted(goal.course, InputBase + Engine.RunTicks, Engine.PhysicsTicks));
                    FullTasGoal now = Goals.Current;
                    if (now != null && now.kind == FullTasGoal.At && Touches(now.x, now.y, now.w, now.h)
                        && now.EnteredFrom(now.x - now.w / 2f, now.x + now.w / 2f, now.y - now.h / 2f, now.y + now.h / 2f, px0, px1, py0, py1))
                        GoalEvent(Goals.Touched(InputBase + Engine.RunTicks, Engine.PhysicsTicks));
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
                EndReason = _endReason,
                Progress = Progress,
                GoalInputTicks = Goals == null ? null : (long[])Goals.InputTicks.Clone(),
                GoalPhysicsTicks = Goals == null ? null : (long[])Goals.PhysicsTicks.Clone(),
            };
        }

        // VmanScript.FixedUpdate: the clock runs down by the fixed step; at 0 ExitVmanSpeedrun loads the Overworld.
        void VmanClock()
        {
            if (!VmanScript.isCurrentlyVman || !(_vmanTimeLeft > 0.0)) return;
            _vmanTimeLeft -= World.FixedDeltaTime;
            if (_vmanTimeLeft <= 0.0 && !_vmanBlockEnding && Engine.IsRunning)
                Engine.Abort("Unmodeled: the Vman speedrun's time ran out (ExitVmanSpeedrun loads the Overworld)");
        }

        static readonly FieldInfo OnGroundField = typeof(Movement).GetField("onGround", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo VelocityField = typeof(Movement).GetField("Velocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo MomentumField = typeof(Movement).GetField("momentum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        const int StillTicks = 3;

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
            return !k.press && !k.release && !k.dash && !k.reset && !k.ArmsLock && !previous.ArmsLock
                && previous.x == k.x && previous.y == k.y && previous.held == k.held;
        }

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
                    dashJumpFrame = k.dash ? k.dashJump : 0, springDashFrame = k.springDash, lockMoveX = k.lockX, lockMoveY = k.lockY,
                    rawQuickRestart = k.reset });
            }
            if (steps.Count == 0) steps.Add(new MacroStep { label = "Solver input", type = MacroStepType.RawInput, durationTicks = 1, durationMs = tickMs,
                endCondition = ActionEndCondition.FixedTicks });
            return steps;
        }

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

        internal static readonly HashSet<string> MovementPresentationFields = new HashSet<string> { "timeSinceLastAfterimage", "afterimageTimeAlive", "activeAfterimages", "inactiveAfterimages" };

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
