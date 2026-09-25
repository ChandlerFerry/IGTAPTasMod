using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    // cooldown for the rest of the run. A buy-max box's IE_BuyMax (upgradeBox.buyUpgrade) resumes at the end of every
    // tick like a WaitForEndOfFrame at 50 fps; left to Unity it bought once per touch and the rest after the run.
    internal static class TurboRunner
    {
        static readonly MethodInfo FixedUpdateMethod = AccessTools.Method(typeof(Movement), "FixedUpdate");
        static readonly MethodInfo UpdateMethod = AccessTools.Method(typeof(Movement), "Update");
        static readonly MethodInfo CourseFixedUpdate = AccessTools.Method(typeof(courseScript), "FixedUpdate");
        static readonly List<(int due, MonoBehaviour target, string method)> Pending = new List<(int, MonoBehaviour, string)>();
        // The buy-max boxes' IE_BuyMax coroutines this run started, in start order, each waiting for the frame's end.
        static readonly List<(upgradeBox box, IEnumerator routine)> BuyMax = new List<(upgradeBox, IEnumerator)>();
        static Movement _movement;
        static int _tick;

        public static bool Active { get; private set; }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "Invoke", new[] { typeof(string), typeof(float) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeInvoke))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "CancelInvoke", new[] { typeof(string) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeCancelInvoke))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeStartCoroutine))));
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

        // upgradeBox.buyUpgrade's IE_BuyMax in a run: its first iteration now, as StartCoroutine runs it (the
        // affordability check and DoBuyUpgrade up to the first WaitForEndOfFrame), the rest at each tick's end (EndOfFrame).
        // Unity's own coroutine would wait for the real frame's end, after the whole run.
        static bool BeforeStartCoroutine(MonoBehaviour __instance, IEnumerator routine, ref Coroutine __result)
        {
            if (!Active || !(__instance is upgradeBox box) || routine == null || !routine.GetType().Name.Contains("IE_BuyMax")) return true;
            __result = null;
            if (routine.MoveNext()) BuyMax.Add((box, routine));
            return false;
        }

        // WaitForEndOfFrame at the end of a tick (one frame per tick): every waiting IE_BuyMax takes its next iteration,
        // one started during this tick's physics included. Unity stops a coroutine whose object went inactive.
        static void EndOfFrame()
        {
            for (int i = 0; i < BuyMax.Count; i++)
            {
                var (box, routine) = BuyMax[i];
                if (box == null || !box.gameObject.activeInHierarchy || !routine.MoveNext()) BuyMax.RemoveAt(i--);
            }
        }

        // Everything a turbo run changes (turbo steps nothing else in the scene), taken at the end of a tick. Unity's
        // contacts aren't in it: Restore rebuilds them for the restored pose (SettleContacts).
        internal sealed class Snapshot
        {
            public int tick;
            public object[] movement, engine;
            // SpawnClone rolls UnityEngine.Random: the RNG state where the snapshot was taken. Restore puts it back
            // (the fixed Full-TAS seed, UnityEngine.Random.InitState, is applied before the new game's start snapshot),
            // and the trace records it as "random": [s0, s1, s2, s3].
            public UnityEngine.Random.State random;
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
        // cost, active, buy cooldown) and every course's clones (they pay Cash). A start state carries it, so a run
        // restored from a snapshot has exactly the economy its start had.
        internal sealed class Economy
        {
            public Dictionary<globalStats.Currencies, double> currencies;
            public Dictionary<globalStats.globalUpgradeSet, double> globalUpgrades;
            public (upgradeBox box, object[] fields)[] boxes;
            public (localUpgrades script, Dictionary<localUpgrades.localUpgradeSet, double> upgrades)[] locals;
            // The clone objects under the script when the snapshot was taken (a clone bought later is destroyed on restore).
            public (clonesScript clones, object[] fields, HashSet<Transform> children)[] clones;
            public Scene scene;
        }

        // What the trip breaker (Course 5's prestige box: upgradeBox.BoostOldCourses, tripBreakerScript.TripBreaker) changes
        // outside the fields above: the boxes' GameObjects and enabled flags, Area 1's state, the breaker's lights and
        // arrays, and which coloured blocks collide. A run past the breaker then restores a start from before it.
        internal sealed class Scene
        {
            public (upgradeBox box, bool active, bool enabled)[] boxes;
            public globalStats.area1states a1State;
            public (tripBreakerScript script, bool[] activeLights, bool[] fixedAreas, (GameObject obj, bool active)[] objects,
                Behaviour light, bool lightEnabled, float outer, float inner)[] breakers;
            public (Collider2D collider, bool enabled)[] blocks;
            public bool blueActive;
        }

        static readonly AccessTools.FieldRef<tripBreakerScript, bool[]> BreakerActiveLights = AccessTools.FieldRefAccess<tripBreakerScript, bool[]>("activeLights");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject> BreakerGlobalLight = AccessTools.FieldRefAccess<tripBreakerScript, GameObject>("globalLight");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject> BreakerAllLights = AccessTools.FieldRefAccess<tripBreakerScript, GameObject>("allLights");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject[]> BreakerAreaLights = AccessTools.FieldRefAccess<tripBreakerScript, GameObject[]>("areaLights");
        // A Light2D (URP, not referenced here): enabled plus its point radii by reflection.
        static readonly FieldInfo BreakerPlayerLight = AccessTools.Field(typeof(tripBreakerScript), "playerLight");
        static readonly AccessTools.FieldRef<colouredBlockSwapper, GameObject[]> OrangeBlocks = AccessTools.FieldRefAccess<colouredBlockSwapper, GameObject[]>("orange");
        static readonly AccessTools.FieldRef<colouredBlockSwapper, GameObject[]> BlueBlocks = AccessTools.FieldRefAccess<colouredBlockSwapper, GameObject[]>("blue");

        static Scene CaptureScene(upgradeBox[] boxes)
        {
            var breakers = new List<(tripBreakerScript, bool[], bool[], (GameObject, bool)[], Behaviour, bool, float, float)>();
            foreach (tripBreakerScript t in UnityEngine.Object.FindObjectsByType<tripBreakerScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var objects = new List<(GameObject, bool)>();
                foreach (GameObject o in new[] { BreakerGlobalLight(t), BreakerAllLights(t) }.Concat(BreakerAreaLights(t) ?? new GameObject[0]))
                    if (o != null) objects.Add((o, o.activeSelf));
                var light = BreakerPlayerLight.GetValue(t) as Behaviour;
                breakers.Add((t, (bool[])BreakerActiveLights(t)?.Clone(), (bool[])t.fixedAreas?.Clone(), objects.ToArray(),
                    light, light != null && light.enabled, LightRadius(light, "pointLightOuterRadius"), LightRadius(light, "pointLightInnerRadius")));
            }
            colouredBlockSwapper swapper = UnityEngine.Object.FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            var blocks = new List<(Collider2D, bool)>();
            if (swapper != null)
                foreach (GameObject o in (OrangeBlocks(swapper) ?? new GameObject[0]).Concat(BlueBlocks(swapper) ?? new GameObject[0]))
                    if (o != null) foreach (Collider2D c in o.GetComponents<Collider2D>()) blocks.Add((c, c.enabled));
            return new Scene {
                boxes = Array.ConvertAll(boxes, b => (b, b.gameObject.activeSelf, b.enabled)),
                a1State = Singleton<globalStats>.Instance.currentA1State,
                breakers = breakers.ToArray(), blocks = blocks.ToArray(), blueActive = swapper != null && swapper.isBlueActive };
        }

        static float LightRadius(Behaviour light, string property)
        {
            return light == null ? 0f : (float)light.GetType().GetProperty(property).GetValue(light);
        }

        static void RestoreScene(Scene s)
        {
            foreach (var (box, active, enabled) in s.boxes)
            {
                if (box == null) continue;
                if (box.gameObject.activeSelf != active) box.gameObject.SetActive(active);
                box.enabled = enabled;
            }
            Singleton<globalStats>.Instance.currentA1State = s.a1State;
            foreach (var (script, activeLights, fixedAreas, objects, light, lightEnabled, outer, inner) in s.breakers)
            {
                if (script == null) continue;
                script.CancelInvoke();
                if (activeLights != null) BreakerActiveLights(script) = (bool[])activeLights.Clone();
                if (fixedAreas != null) script.fixedAreas = (bool[])fixedAreas.Clone();
                foreach (var (obj, active) in objects) if (obj != null && obj.activeSelf != active) obj.SetActive(active);
                if (light == null) continue;
                light.enabled = lightEnabled;
                light.GetType().GetProperty("pointLightOuterRadius").SetValue(light, outer);
                light.GetType().GetProperty("pointLightInnerRadius").SetValue(light, inner);
            }
            foreach (var (collider, enabled) in s.blocks) if (collider != null) collider.enabled = enabled;
            colouredBlockSwapper swapper = UnityEngine.Object.FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            if (swapper != null) swapper.isBlueActive = s.blueActive;
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
        internal static FieldInfo[] EconomyFields(Type type)
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

        // Clone objects some economy snapshot holds. Restoring a snapshot that doesn't (the fresh game's has none) hides
        // them instead of destroying them: every later restore of the snapshot that holds them needs them back (a
        // destroyed clone pays nothing, so a purchase that waits for clone income would never happen).
        static readonly HashSet<Transform> HeldClones = new HashSet<Transform>();

        static Economy CaptureEconomy()
        {
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            localUpgrades[] locals = UnityEngine.Object.FindObjectsByType<localUpgrades>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return new Economy {
                currencies = globalStats.currencyLookup == null ? null : new Dictionary<globalStats.Currencies, double>(globalStats.currencyLookup),
                globalUpgrades = globalStats.globalUpgradeDict == null ? null : new Dictionary<globalStats.globalUpgradeSet, double>(globalStats.globalUpgradeDict),
                boxes = Array.ConvertAll(boxes, b => (b, CaptureFields(b))),
                scene = CaptureScene(boxes),
                locals = Array.ConvertAll(locals, l => (l, l.localUpgradeDict == null ? null : new Dictionary<localUpgrades.localUpgradeSet, double>(l.localUpgradeDict))),
                clones = Array.ConvertAll(clones, c =>
                {
                    var children = new HashSet<Transform>();
                    foreach (Transform child in c.transform) { children.Add(child); HeldClones.Add(child); }
                    return (c, CaptureFields(c), children);
                }) };
        }

        static void RestoreEconomy(Economy e)
        {
            RestoreDictionary(globalStats.currencyLookup, e.currencies);
            RestoreDictionary(globalStats.globalUpgradeDict, e.globalUpgrades);
            // GameObjects first: a box enabled again runs its OnEnable before its fields go back.
            if (e.scene != null) RestoreScene(e.scene);
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
                    if (!children.Contains(child))
                    {
                        if (HeldClones.Contains(child)) child.gameObject.SetActive(false);
                        else UnityEngine.Object.Destroy(child.gameObject);
                    }
                    else if (active.Contains(child.gameObject)) child.gameObject.SetActive(clones.onScreen);
                    else if (pooled.Contains(child.gameObject)) child.gameObject.SetActive(false);
                }
            }
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

        // `beforeTick`: taken inside a tick's Movement.FixedUpdate prefix (CaptureNewGameStart takes it there), after that
        // tick's due Invokes fired: the restored run's first tick is the rest of this one. `economy`: false for the
        // per-tick trace snapshots (written out, never restored).
        public static Snapshot Capture(Movement movement, courseScript[] courses, object[] engine, int tick, bool beforeTick = false, bool economy = true)
        {
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            var pending = new (int, MonoBehaviour, string)[Pending.Count];
            // Relative to the first tick after the restore.
            int next = _tick + (beforeTick ? 0 : 1);
            for (int i = 0; i < pending.Length; i++) pending[i] = (Pending[i].due - next, Pending[i].target, Pending[i].method);
            return new Snapshot {
                tick = tick, movement = FieldState.Capture(movement), engine = engine, random = UnityEngine.Random.state,
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
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            var own = new List<Collider2D>();
            body.GetAttachedColliders(own);
            // The colliders the last run's pose touches: their contacts end in the settling step.
            var muted = new Dictionary<Collider2D, LayerMask>();
            MuteAround(own, muted);
            BoxCollider2D[] colliders = movement.GetComponents<BoxCollider2D>();
            for (int i = 0; i < colliders.Length && i < s.colliders.Length; i++)
            {
                colliders[i].offset = s.colliders[i].offset;
                colliders[i].size = s.colliders[i].size;
                colliders[i].excludeLayers = s.colliders[i].exclude;
                colliders[i].enabled = s.colliders[i].enabled;
            }
            movement.transform.position = s.position;
            body.position = s.bodyPosition;
            body.rotation = s.rotation;
            Physics2D.SyncTransforms();
            MuteAround(own, muted);
            SettleContacts(body, muted);
            // Last: nothing the settling step did to the scripts' state survives.
            FieldState.Restore(movement, s.movement);
            foreach (var (course, fields) in s.courses) FieldState.Restore(course, fields);
            // SpawnClone rolls UnityEngine.Random: the state the snapshot carried goes back with the scripts' state.
            UnityEngine.Random.state = s.random;
            if (s.economy != null) RestoreEconomy(s.economy);
            movement.transform.position = s.position;
            body.position = s.bodyPosition;
            body.rotation = s.rotation;
            body.linearVelocity = s.velocity;
            body.angularVelocity = s.angularVelocity;
            Physics2D.SyncTransforms();
        }

        static readonly List<Collider2D> Overlaps = new List<Collider2D>();

        // Mutes the callbacks of the player's colliders and of every collider within a unit of them (a contact starts or
        // ends only within the contact offsets, 0.01).
        static void MuteAround(List<Collider2D> own, Dictionary<Collider2D, LayerMask> muted)
        {
            var filter = new ContactFilter2D { useTriggers = true };
            foreach (Collider2D c in own)
            {
                Mute(c, muted);
                if (!c.enabled) continue;
                Bounds b = c.bounds;
                Physics2D.OverlapBox(b.center, (Vector2)b.size + new Vector2(2f, 2f), 0f, filter, Overlaps);
                foreach (Collider2D other in Overlaps) Mute(other, muted);
            }
            Overlaps.Clear();
        }

        static void Mute(Collider2D c, Dictionary<Collider2D, LayerMask> muted)
        {
            if (muted.ContainsKey(c)) return;
            muted[c] = c.callbackLayers;
            c.callbackLayers = 0;
        }

        // Unity's contacts are the last run's, wherever it ended. Left as they are, the restored run's first step reports
        // Exit for those and Enter for what the restored pose touches, where the run the snapshot was taken from reports
        // Stay (and enginesim, which primes the start pose's contacts as already touching): Movement.collidingBodies lists
        // a collider twice, OnCollisionStay2D doesn't zero a head bump's Velocity.y, and a trigger's Stay (an upgrade box's
        // buy) comes a tick late. One step with those callbacks muted and the player standing still makes Unity's contacts
        // the restored pose's; every body it moved goes back.
        static void SettleContacts(Rigidbody2D player, Dictionary<Collider2D, LayerMask> muted)
        {
            Rigidbody2D[] bodies = UnityEngine.Object.FindObjectsByType<Rigidbody2D>(FindObjectsSortMode.None);
            var poses = new (Vector2 position, float rotation, Vector2 velocity, float angular)[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
                poses[i] = (bodies[i].position, bodies[i].rotation, bodies[i].linearVelocity, bodies[i].angularVelocity);
            player.linearVelocity = Vector2.zero;
            player.angularVelocity = 0f;
            SimulationMode2D savedMode = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
            try { Physics2D.Simulate(Time.fixedDeltaTime); }
            finally
            {
                Physics2D.simulationMode = savedMode;
                foreach (KeyValuePair<Collider2D, LayerMask> m in muted) if (m.Key != null) m.Key.callbackLayers = m.Value;
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (bodies[i] == null || bodies[i].bodyType == RigidbodyType2D.Static) continue;
                    bodies[i].position = poses[i].position;
                    bodies[i].rotation = poses[i].rotation;
                    bodies[i].linearVelocity = poses[i].velocity;
                    bodies[i].angularVelocity = poses[i].angular;
                }
            }
        }

        // Steps until keepRunning() is false or maxTicks elapse. Returns ticks stepped. `resume` re-arms the
        // snapshot's pending Invokes (its state must already be restored).
        public static int Run(Movement movement, Func<bool> keepRunning, int maxTicks, Action afterTick = null, Snapshot resume = null)
        {
            if (movement == null) throw new InvalidOperationException("Player not found.");
            SimulationMode2D savedMode = Physics2D.simulationMode;
            // Inactive ones too: the per-tick isActiveAndEnabled check is Unity's FixedUpdate rule, and the trip breaker
            // activates a box (SwapBlocksOnDash) and disables another mid-run.
            courseScript[] courses = UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            float dt = Time.fixedDeltaTime;
            // The floating numbers (a finish's reward, a clone's pay) come from a pool of 200 that only their own
            // FixedUpdate, never stepped here, refills. Drained mid-run, the next one throws out of courseScript.stopTracking
            // (no finish) or clonesScript.DestroyClone (the clone stays and pays again), depending on how much real time
            // refilled it since the last run. They are presentation: a turbo run spawns none.
            FloatingNumberScript numbers = Singleton<FloatingNumberScript>.Instance;
            bool spawnNumbers = numbers != null && numbers.ActuallySpawnText;
            _movement = movement;
            Pending.Clear();
            BuyMax.Clear();
            if (resume != null) Pending.AddRange(resume.pending);
            Active = true;
            if (numbers != null) numbers.ActuallySpawnText = false;
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
                    EndOfFrame();
                    afterTick?.Invoke();
                }
                return _tick;
            }
            finally
            {
                Active = false;
                if (numbers != null) numbers.ActuallySpawnText = spawnNumbers;
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
                // A buy-max box's purchases still waiting end with the run: the state it restores next has none.
                BuyMax.Clear();
                _movement = null;
            }
        }
    }
}
