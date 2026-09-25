using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    // Runs the real game faster than real time: Movement.FixedUpdate, courseScript.FixedUpdate, the upgrade boxes' buy
    // cooldowns, Physics2D.Simulate, Movement.Update and the clones (clonesScript.Update without the drawing) are stepped
    // in a loop inside one rendered frame, in Unity's per-frame order (FixedUpdate -> physics step -> Update -> delayed
    // Invokes). Every script's Invoke timer (Movement's, and the world's: a spring's 0.15 s cooldown) is re-scheduled on
    // fixed ticks so it fires where the frame loop would at >= 50 fps; left to real time, a spring hit once stayed on
    // cooldown for the rest of the run.
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
            // Null for per-tick trace snapshots (nothing restores them).
            public Economy economy;
        }

        // The game's economy: Cash and every upgrade (global, each course's localUpgrades), every upgrade box (times used,
        // cost, active, buy cooldown) and every course's clones (they pay Cash). A purchase segment's start carries it, so a
        // run restored from a snapshot can afford exactly what the chain before it could.
        internal sealed class Economy
        {
            public Dictionary<globalStats.Currencies, double> currencies;
            public Dictionary<globalStats.globalUpgradeSet, double> globalUpgrades;
            public (upgradeBox box, object[] fields)[] boxes;
            public (localUpgrades script, Dictionary<localUpgrades.localUpgradeSet, double> upgrades)[] locals;
            // The clone objects under the script when the snapshot was taken (a clone bought later is destroyed on restore).
            public (clonesScript clones, object[] fields, HashSet<Transform> children)[] clones;
            // SpawnClone rolls UnityEngine.Random.
            public UnityEngine.Random.State random;
        }

        static readonly AccessTools.FieldRef<upgradeBox, float> BoxCooldown = AccessTools.FieldRefAccess<upgradeBox, float>("boxCooldown");
        static readonly AccessTools.FieldRef<clonesScript, float> CloneTimeSinceLast = AccessTools.FieldRefAccess<clonesScript, float>("timeSinceLastClone");
        static readonly AccessTools.FieldRef<clonesScript, float> CloneNextDue = AccessTools.FieldRefAccess<clonesScript, float>("nextCloneDue");
        static readonly AccessTools.FieldRef<clonesScript, List<float>> ClonePointers = AccessTools.FieldRefAccess<clonesScript, List<float>>("clonePathPointers");
        static readonly AccessTools.FieldRef<clonesScript, List<GameObject>> ActiveClones = AccessTools.FieldRefAccess<clonesScript, List<GameObject>>("activeClones");
        static readonly AccessTools.FieldRef<clonesScript, Stack<GameObject>> PooledClones = AccessTools.FieldRefAccess<clonesScript, Stack<GameObject>>("inactiveClones");
        static readonly Func<clonesScript, int, float> ClonePathIncrement =
            AccessTools.MethodDelegate<Func<clonesScript, int, float>>(AccessTools.Method(typeof(clonesScript), "determinePathIncrement"));
        static readonly Action<clonesScript, int> DestroyClone =
            AccessTools.MethodDelegate<Action<clonesScript, int>>(AccessTools.Method(typeof(clonesScript), "DestroyClone"));
        static readonly Dictionary<Type, FieldInfo[]> EconomyFieldCache = new Dictionary<Type, FieldInfo[]>();

        // A script's own state: its value fields and its lists/stacks (copied). Its references (texts, sprites, the clone
        // path) are the scene's and no run changes them.
        static FieldInfo[] EconomyFields(Type type)
        {
            if (EconomyFieldCache.TryGetValue(type, out FieldInfo[] cached)) return cached;
            var list = new List<FieldInfo>();
            foreach (FieldInfo f in FieldState.For(type))
            {
                Type t = f.FieldType;
                bool collection = t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Stack<>));
                if (t.IsValueType || t == typeof(string) || t == typeof(double[]) || collection) list.Add(f);
            }
            return EconomyFieldCache[type] = list.ToArray();
        }

        static object[] CaptureFields(object target)
        {
            FieldInfo[] fields = EconomyFields(target.GetType());
            var values = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++) values[i] = FieldState.Copy(fields[i].GetValue(target));
            return values;
        }

        static void RestoreFields(object target, object[] values)
        {
            FieldInfo[] fields = EconomyFields(target.GetType());
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(target, FieldState.Copy(values[i]));
        }

        static void RestoreDictionary<TKey>(Dictionary<TKey, double> live, Dictionary<TKey, double> saved)
        {
            if (live == null || saved == null) return;
            live.Clear();
            foreach (KeyValuePair<TKey, double> entry in saved) live[entry.Key] = entry.Value;
        }

        static Economy CaptureEconomy()
        {
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            localUpgrades[] locals = UnityEngine.Object.FindObjectsByType<localUpgrades>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return new Economy {
                currencies = globalStats.currencyLookup == null ? null : new Dictionary<globalStats.Currencies, double>(globalStats.currencyLookup),
                globalUpgrades = globalStats.globalUpgradeDict == null ? null : new Dictionary<globalStats.globalUpgradeSet, double>(globalStats.globalUpgradeDict),
                boxes = Array.ConvertAll(boxes, b => (b, CaptureFields(b))),
                locals = Array.ConvertAll(locals, l => (l, l.localUpgradeDict == null ? null : new Dictionary<localUpgrades.localUpgradeSet, double>(l.localUpgradeDict))),
                clones = Array.ConvertAll(clones, c =>
                {
                    var children = new HashSet<Transform>();
                    foreach (Transform child in c.transform) children.Add(child);
                    return (c, CaptureFields(c), children);
                }),
                random = UnityEngine.Random.state };
        }

        static void RestoreEconomy(Economy e)
        {
            RestoreDictionary(globalStats.currencyLookup, e.currencies);
            RestoreDictionary(globalStats.globalUpgradeDict, e.globalUpgrades);
            foreach (var (box, fields) in e.boxes) if (box != null) RestoreFields(box, fields);
            foreach (var (script, upgrades) in e.locals) if (script != null) RestoreDictionary(script.localUpgradeDict, upgrades);
            foreach (var (clones, fields, children) in e.clones)
            {
                if (clones == null) continue;
                RestoreFields(clones, fields);
                // Clone objects as the game keeps them: active ones shown while on screen, pooled ones hidden.
                var active = new HashSet<GameObject>(ActiveClones(clones));
                var pooled = new HashSet<GameObject>(PooledClones(clones));
                var now = new List<Transform>();
                foreach (Transform child in clones.transform) now.Add(child);
                foreach (Transform child in now)
                {
                    if (!children.Contains(child)) UnityEngine.Object.Destroy(child.gameObject);
                    else if (active.Contains(child.gameObject)) child.gameObject.SetActive(clones.onScreen);
                    else if (pooled.Contains(child.gameObject)) child.gameObject.SetActive(false);
                }
            }
            UnityEngine.Random.state = e.random;
        }

        // A field of `box` as the snapshot holds it (the trace header's purchase info).
        public static object BoxField(Snapshot s, upgradeBox box, string name)
        {
            int index = Array.FindIndex(EconomyFields(typeof(upgradeBox)), f => f.Name == name);
            if (s.economy == null || index < 0) return null;
            foreach (var (b, fields) in s.economy.boxes) if (b == box) return fields[index];
            return null;
        }

        public static double Cash(Snapshot s)
        {
            return s.economy?.currencies != null && s.economy.currencies.TryGetValue(globalStats.Currencies.Cash, out double cash) ? cash : 0.0;
        }

        // clonesScript.Update at 50 fps (dt = one tick): the next clone spawns when due (the real SpawnClone: on screen it
        // joins the path, off screen it pays at once), and a clone at the end of its path pays and goes back to the pool
        // (the real DestroyClone). Only the drawing is left out.
        static void StepClones(clonesScript clones, float dt)
        {
            ref float sinceLast = ref CloneTimeSinceLast(clones);
            sinceLast += dt;
            if (sinceLast >= CloneNextDue(clones) && (double)clones.pathLength > 0.1) clones.SpawnClone();
            if (!clones.onScreen) return;
            List<GameObject> active = ActiveClones(clones);
            List<float> pointers = ClonePointers(clones);
            List<int> done = null;
            for (int i = 0; i < active.Count; i++)
            {
                if (Mathf.FloorToInt(pointers[i]) >= clones.clonePath.Length) { (done ?? (done = new List<int>())).Add(i); continue; }
                pointers[i] += ClonePathIncrement(clones, i) * (50f * dt);
            }
            if (done != null) for (int k = done.Count - 1; k >= 0; k--) DestroyClone(clones, done[k]);
        }

        // `beforeTick`: taken inside a tick's Movement.FixedUpdate prefix (a chain segment's start), after that tick's due
        // Invokes fired: the restored run's first tick is the rest of this one. `economy`: false for the per-tick trace
        // snapshots (written out, never restored).
        public static Snapshot Capture(Movement movement, courseScript[] courses, object[] engine, int tick, bool beforeTick = false, bool economy = true)
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
                pending = pending, economy = economy ? CaptureEconomy() : null };
        }

        public static void Restore(Snapshot s, Movement movement)
        {
            movement.CancelInvoke();
            FieldState.Restore(movement, s.movement);
            foreach (var (course, fields) in s.courses) FieldState.Restore(course, fields);
            if (s.economy != null) RestoreEconomy(s.economy);
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
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsSortMode.None);
            float dt = Time.fixedDeltaTime;
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
                    // upgradeBox.FixedUpdate: the buy cooldown (0.2 s after every touch) counts down by the tick.
                    foreach (upgradeBox box in boxes)
                    {
                        if (box == null || !box.isActiveAndEnabled) continue;
                        ref float cooldown = ref BoxCooldown(box);
                        if (cooldown >= 0f) cooldown -= dt;
                    }
                    Physics2D.Simulate(dt);
                    UpdateMethod.Invoke(movement, null);
                    foreach (clonesScript c in clones)
                        if (c != null && c.isActiveAndEnabled) StepClones(c, dt);
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
