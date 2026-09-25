using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    // Runs the real game faster than real time: Movement.FixedUpdate, Physics2D.Simulate and
    // Movement.Update are stepped in a loop inside one rendered frame, in Unity's per-frame order
    // (FixedUpdate -> physics step -> Update -> delayed Invokes). Every script's Invoke timer (Movement's, and the
    // world's: a spring's 0.15 s cooldown) is re-scheduled on fixed ticks so it fires where the frame loop would at
    // >= 50 fps; left to real time, a spring hit once stayed on cooldown for the rest of the run.
    internal static class TurboRunner
    {
        static readonly MethodInfo FixedUpdateMethod = AccessTools.Method(typeof(Movement), "FixedUpdate");
        static readonly MethodInfo UpdateMethod = AccessTools.Method(typeof(Movement), "Update");
        static readonly MethodInfo CourseFixedUpdate = AccessTools.Method(typeof(courseScript), "FixedUpdate");
        static readonly List<(int due, MonoBehaviour target, string method)> Pending = new List<(int, MonoBehaviour, string)>();
        static Movement _movement;
        static int _tick;

        public static bool Active { get; private set; }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "Invoke", new[] { typeof(string), typeof(float) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeInvoke))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "CancelInvoke", new[] { typeof(string) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeCancelInvoke))));
        }

        static bool BeforeInvoke(MonoBehaviour __instance, string methodName, float time)
        {
            if (!Active) return true;
            // A delayed call fires in the first frame whose time reaches it, after that frame's fixed
            // steps: 0.03 s scheduled on tick n is seen by tick n+1 and cleared before tick n+2.
            Pending.Add((_tick + Mathf.FloorToInt(time / Time.fixedDeltaTime) + 1, __instance, methodName));
            return false;
        }

        static bool BeforeCancelInvoke(MonoBehaviour __instance, string methodName)
        {
            if (!Active) return true;
            Pending.RemoveAll(p => p.target == __instance && p.method == methodName);
            return false;
        }

        // Everything a turbo run changes (turbo steps nothing else in the scene), taken at the end of a tick.
        // Physics contact/trigger bookkeeping is not included, so a restored run can differ slightly from a replay.
        internal sealed class Snapshot
        {
            public int tick;
            public object[] movement, engine;
            public (courseScript course, object[] fields)[] courses;
            public Vector3 position;
            public Vector2 bodyPosition, velocity;
            public float rotation, angularVelocity;
            public (Vector2 offset, Vector2 size, int exclude, bool enabled)[] colliders;
            public (int due, MonoBehaviour target, string method)[] pending;
        }

        // `beforeTick`: taken inside a tick's Movement.FixedUpdate prefix (a chain segment's start), after that tick's due
        // Invokes fired: the restored run's first tick is the rest of this one.
        public static Snapshot Capture(Movement movement, courseScript[] courses, object[] engine, int tick, bool beforeTick = false)
        {
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            var pending = new (int, MonoBehaviour, string)[Pending.Count];
            // Relative to the first tick after the restore.
            int next = _tick + (beforeTick ? 0 : 1);
            for (int i = 0; i < pending.Length; i++) pending[i] = (Pending[i].due - next, Pending[i].target, Pending[i].method);
            return new Snapshot {
                tick = tick, movement = FieldState.Capture(movement), engine = engine,
                courses = Array.ConvertAll(courses, c => (c, FieldState.Capture(c))),
                position = movement.transform.position, bodyPosition = body.position, velocity = body.linearVelocity,
                rotation = body.rotation, angularVelocity = body.angularVelocity,
                colliders = Array.ConvertAll(movement.GetComponents<BoxCollider2D>(),
                    c => (c.offset, c.size, (int)c.excludeLayers, c.enabled)),
                pending = pending };
        }

        public static void Restore(Snapshot s, Movement movement)
        {
            movement.CancelInvoke();
            FieldState.Restore(movement, s.movement);
            foreach (var (course, fields) in s.courses) FieldState.Restore(course, fields);
            BoxCollider2D[] colliders = movement.GetComponents<BoxCollider2D>();
            for (int i = 0; i < colliders.Length && i < s.colliders.Length; i++)
            {
                colliders[i].offset = s.colliders[i].offset;
                colliders[i].size = s.colliders[i].size;
                colliders[i].excludeLayers = s.colliders[i].exclude;
                colliders[i].enabled = s.colliders[i].enabled;
            }
            movement.transform.position = s.position;
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            body.position = s.bodyPosition;
            body.rotation = s.rotation;
            body.linearVelocity = s.velocity;
            body.angularVelocity = s.angularVelocity;
            Physics2D.SyncTransforms();
        }

        // Steps until keepRunning() is false or maxTicks elapse. Returns ticks stepped. `resume` re-arms the
        // snapshot's pending Invokes (its state must already be restored).
        public static int Run(Movement movement, Func<bool> keepRunning, int maxTicks, Action afterTick = null, Snapshot resume = null)
        {
            if (movement == null) throw new InvalidOperationException("Player not found.");
            SimulationMode2D savedMode = Physics2D.simulationMode;
            courseScript[] courses = UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsSortMode.None);
            _movement = movement;
            Pending.Clear();
            if (resume != null) Pending.AddRange(resume.pending);
            Active = true;
            Physics2D.simulationMode = SimulationMode2D.Script;
            try
            {
                for (_tick = 0; _tick < maxTicks && keepRunning(); _tick++)
                {
                    for (int i = 0; i < Pending.Count; i++)
                    {
                        if (Pending[i].due > _tick) continue;
                        var (_, target, method) = Pending[i];
                        Pending.RemoveAt(i--);
                        if (target != null) AccessTools.Method(target.GetType(), method)?.Invoke(target, null);
                    }
                    FixedUpdateMethod.Invoke(movement, null);
                    foreach (courseScript course in courses)
                        if (course != null && course.isActiveAndEnabled) CourseFixedUpdate.Invoke(course, null);
                    Physics2D.Simulate(Time.fixedDeltaTime);
                    UpdateMethod.Invoke(movement, null);
                    afterTick?.Invoke();
                }
                return _tick;
            }
            finally
            {
                Active = false;
                Physics2D.simulationMode = savedMode;
                // Hand the player's timers still pending (e.g. deathRespawn) back to Unity in real time. The world's
                // (a spring's cooldown) fire now: the next run starts from a settled world, as a real retry would.
                foreach (var (due, target, method) in Pending.ToArray())
                {
                    if (target == null) continue;
                    if (target == movement) movement.Invoke(method, Mathf.Max(0f, (due - _tick) * Time.fixedDeltaTime));
                    else AccessTools.Method(target.GetType(), method)?.Invoke(target, null);
                }
                Pending.Clear();
                _movement = null;
            }
        }
    }
}
