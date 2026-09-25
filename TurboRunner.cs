using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    internal static class TurboRunner
    {
        static readonly MethodInfo FixedUpdateMethod = AccessTools.Method(typeof(Movement), "FixedUpdate");
        static readonly MethodInfo UpdateMethod = AccessTools.Method(typeof(Movement), "Update");
        static readonly MethodInfo CourseFixedUpdate = AccessTools.Method(typeof(courseScript), "FixedUpdate");
        static readonly List<(int due, MonoBehaviour target, string method)> Pending = new List<(int, MonoBehaviour, string)>();
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
            Pending.Add((_tick + Mathf.FloorToInt(time / Time.fixedDeltaTime) + 1, __instance, methodName));
            return false;
        }

        static bool BeforeCancelInvoke(MonoBehaviour __instance, string methodName)
        {
            if (!Active) return true;
            Pending.RemoveAll(p => p.target == __instance && p.method == methodName);
            return false;
        }

        static bool BeforeStartCoroutine(MonoBehaviour __instance, IEnumerator routine, ref Coroutine __result)
        {
            if (!Active || !(__instance is upgradeBox box) || routine == null || !routine.GetType().Name.Contains("IE_BuyMax")) return true;
            __result = null;
            if (routine.MoveNext()) BuyMax.Add((box, routine));
            return false;
        }

        static void EndOfFrame()
        {
            for (int i = 0; i < BuyMax.Count; i++)
            {
                var (box, routine) = BuyMax[i];
                if (box == null || !box.gameObject.activeInHierarchy || !routine.MoveNext()) BuyMax.RemoveAt(i--);
            }
        }

        internal sealed class Snapshot
        {
            public int tick;
            public object[] movement, engine;
            public UnityEngine.Random.State random;
            public (courseScript course, object[] fields)[] courses;
            public Vector3 position;
            public Vector2 bodyPosition, velocity;
            public float rotation, angularVelocity;
            public (Vector2 offset, Vector2 size, int exclude, bool enabled)[] colliders;
            public (int due, MonoBehaviour target, string method)[] pending;
            public Economy economy;
        }

        internal sealed class Economy
        {
            public Dictionary<globalStats.Currencies, double> currencies;
            public Dictionary<globalStats.globalUpgradeSet, double> globalUpgrades;
            public (upgradeBox box, object[] fields)[] boxes;
            public (localUpgrades script, Dictionary<localUpgrades.localUpgradeSet, double> upgrades)[] locals;
            public (clonesScript clones, object[] fields, HashSet<Transform> children)[] clones;
            public Scene scene;
        }

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
            if (e.scene != null) RestoreScene(e.scene);
            foreach (var (box, fields) in e.boxes) if (box != null) RestoreFields(box, fields);
            foreach (var (script, upgrades) in e.locals) if (script != null) RestoreDictionary(script.localUpgradeDict, upgrades);
            foreach (var (clones, fields, children) in e.clones)
            {
                if (clones == null) continue;
                RestoreFields(clones, fields);
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

        public static Snapshot Capture(Movement movement, courseScript[] courses, object[] engine, int tick, bool beforeTick = false, bool economy = true)
        {
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            var pending = new (int, MonoBehaviour, string)[Pending.Count];
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
            FieldState.Restore(movement, s.movement);
            foreach (var (course, fields) in s.courses) FieldState.Restore(course, fields);
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

        public static int Run(Movement movement, Func<bool> keepRunning, int maxTicks, Action afterTick = null, Snapshot resume = null)
        {
            if (movement == null) throw new InvalidOperationException("Player not found.");
            SimulationMode2D savedMode = Physics2D.simulationMode;
            courseScript[] courses = UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            float dt = Time.fixedDeltaTime;
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
                foreach (var (due, target, method) in Pending.ToArray())
                {
                    if (target == null) continue;
                    if (target == movement) movement.Invoke(method, Mathf.Max(0f, (due - _tick) * Time.fixedDeltaTime));
                    else AccessTools.Method(target.GetType(), method)?.Invoke(target, null);
                }
                Pending.Clear();
                BuyMax.Clear();
                _movement = null;
            }
        }
    }
}
