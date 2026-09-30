using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    internal static class TurboRunner
    {
        static readonly MethodInfo FixedUpdateMethod = AccessTools.Method(typeof(Movement), "FixedUpdate");
        static readonly MethodInfo UpdateMethod = AccessTools.Method(typeof(Movement), "Update");
        static readonly MethodInfo CourseFixedUpdate = AccessTools.Method(typeof(courseScript), "FixedUpdate");
        static readonly MethodInfo VmanFixedUpdate = AccessTools.Method(typeof(VmanScript), "FixedUpdate");
        static readonly MethodInfo OrbFixedUpdate = AccessTools.Method(typeof(JiggleDropScript), "FixedUpdate");
        static readonly MethodInfo OrbState = AccessTools.Method(typeof(JiggleDropScript), "SetActiveVisualState");
        // The per-tick calls as open delegates, made on the first run (after TasPlugin's patches, which they go through as
        // Unity's own calls do).
        static Action<Movement> _fixedUpdate, _update;
        static Action<courseScript> _courseFixedUpdate;
        static Action<VmanScript> _vmanFixedUpdate;
        static Action<JiggleDropScript> _orbFixedUpdate;
        static Action<FloatingOrigin> _originLateUpdate;
        // Invoke(name) targets' methods by type and name, for the pending ones turbo runs itself.
        static readonly Dictionary<(Type, string), MethodInfo> InvokedMethods = new Dictionary<(Type, string), MethodInfo>();

        static void MakeCalls()
        {
            if (_fixedUpdate != null) return;
            _fixedUpdate = AccessTools.MethodDelegate<Action<Movement>>(FixedUpdateMethod);
            _update = AccessTools.MethodDelegate<Action<Movement>>(UpdateMethod);
            _courseFixedUpdate = AccessTools.MethodDelegate<Action<courseScript>>(CourseFixedUpdate);
            _vmanFixedUpdate = AccessTools.MethodDelegate<Action<VmanScript>>(VmanFixedUpdate);
            _orbFixedUpdate = AccessTools.MethodDelegate<Action<JiggleDropScript>>(OrbFixedUpdate);
            _originLateUpdate = AccessTools.MethodDelegate<Action<FloatingOrigin>>(OriginLateUpdate);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeMessage { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

        [DllImport("user32.dll")]
        static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        const uint PeekNoRemove = 0x0000, PeekNoYield = 0x0002;
        const long KeepAliveMs = 250;

        /// <summary>
        /// A long run (a Full-TAS judge is tens of seconds of one frame) holds the main thread, and Windows calls a window
        /// that hasn't looked at its message queue for 5 s "Not Responding". Looking without taking a message (PM_NOREMOVE)
        /// keeps it responsive and runs no frame, Update, FixedUpdate or coroutine, so nothing of the run moves.
        /// </summary>
        static void KeepWindowAlive(System.Diagnostics.Stopwatch clock, ref long next)
        {
            if (clock.ElapsedMilliseconds < next) return;
            next = clock.ElapsedMilliseconds + KeepAliveMs;
            if (Application.platform == RuntimePlatform.WindowsPlayer) PeekMessage(out _, IntPtr.Zero, 0, 0, PeekNoRemove | PeekNoYield);
        }

        /// <summary>What <c>MonoBehaviour.Invoke(method)</c> on the target would run: its type's method of that name, none if it has none.</summary>
        static void CallInvoked(MonoBehaviour target, string method)
        {
            Type type = target.GetType();
            if (!InvokedMethods.TryGetValue((type, method), out MethodInfo info))
                InvokedMethods[(type, method)] = info = AccessTools.Method(type, method);
            info?.Invoke(target, null);
        }
        static readonly AccessTools.FieldRef<JiggleDropScript, float> OrbCooldown = AccessTools.FieldRefAccess<JiggleDropScript, float>("cooldown");
        static readonly AccessTools.FieldRef<JiggleDropScript, bool> OrbUsable = AccessTools.FieldRefAccess<JiggleDropScript, bool>("usable");
        static readonly AccessTools.FieldRef<PlatformMover, Rigidbody2D> MoverBody = AccessTools.FieldRefAccess<PlatformMover, Rigidbody2D>("body");
        static readonly MethodInfo OriginLateUpdate = AccessTools.Method(typeof(FloatingOrigin), "LateUpdate");
        static readonly AccessTools.FieldRef<FloatingOrigin, bool> OriginRebasing = AccessTools.FieldRefAccess<FloatingOrigin, bool>("RebaseInProgress");
        static readonly List<(int due, MonoBehaviour target, string method)> Pending = new List<(int, MonoBehaviour, string)>();
        static readonly List<(upgradeBox box, IEnumerator routine)> BuyMax = new List<(upgradeBox, IEnumerator)>();
        static readonly List<Routine> Routines = new List<Routine>();
        static readonly AccessTools.FieldRef<WaitForSeconds, float> WaitSeconds = AccessTools.FieldRefAccess<WaitForSeconds, float>("m_Seconds");
        static readonly Func<object, object> CloneObject = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));
        // FloatingOrigin.MoveOrigin, started by its LateUpdate and waiting for the next physics step (WaitForFixedUpdate).
        static IEnumerator _rebase;
        static int _tick;
        // Interpolation (MacroEngine.FrameGrid, as enginesim's Simulation): the body's position at the start and end of the
        // last physics step, the shortest TimeStop the tick asked for (its freeze's Update frames), and the pause menu's
        // frames a route step opens after the tick (MacroStep.pauseFrames, MacroEngine.TakePause).
        static Vector2 _stepFrom, _stepTo;
        static float _freezeFor = -1f;
        static int _pauseFrames, _pauseAfter;

        /// <summary>
        /// The route's pause after this tick's FixedUpdate (TasPlugin.AfterMovementFixedUpdate): at 1x the menu stops time
        /// and Movement.Update runs once per frame it is open, at the blend of the frame it holds (the tick's first, or the
        /// one <paramref name="afterFrames"/> frames in: MacroEngine.FrameRepeats); the step-up in it lifts a turned dash
        /// every frame (the pauselift).
        /// </summary>
        public static void Pause(int frames, int afterFrames)
        {
            if (!Active || frames <= _pauseFrames) return;
            _pauseFrames = frames;
            _pauseAfter = afterFrames;
        }

        public static bool Active { get; private set; }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "Invoke", new[] { typeof(string), typeof(float) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeInvoke))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "CancelInvoke", new[] { typeof(string) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeCancelInvoke))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeStartCoroutine))));
            harmony.Patch(AccessTools.Method(typeof(Movement), "TimeStop"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeTimeStop))));
            harmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(PlatformMover), "IE_AdvanceToNextState")),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(UseCoroutineDeltaTime))));
            harmony.Patch(AccessTools.Method(typeof(Movement), "HandleAfterimages"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeHandleAfterimages))));
        }

        /// <summary>
        /// Movement.Update's afterimages are presentation only (the offline engine cuts them, enginesim/cuts.txt), but in turbo
        /// they ran on the real frame's Time.deltaTime, held for the whole run: a fast stretch could take all 21 pooled
        /// sprites, and the next <c>inactiveAfterimages.Pop()</c> threw "Stack empty." out of the run. Turbo skips them.
        /// </summary>
        static bool BeforeHandleAfterimages() { return !Active; }

        /// <summary>
        /// Two freezes the same tick run side by side and the shorter ends it (its end puts timeScale back), so the tick's
        /// freeze is the shortest. The coroutine still runs (it stops time, as before).
        /// </summary>
        static void BeforeTimeStop(float duration)
        {
            if (Active && (_freezeFor < 0f || duration < _freezeFor)) _freezeFor = duration;
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
            if (!Active || routine == null) return true;
            if (__instance is FloatingOrigin)
            {
                // MoveOrigin runs to its WaitForFixedUpdate here (RebaseInProgress set); Run resumes it after the next physics step.
                __result = null;
                if (routine.MoveNext()) _rebase = routine;
                return false;
            }
            if (__instance is PlatformMover)
            {
                // A zip mover's coroutines are gameplay (MakeIntangible's 0.15 s off the Ground layer, PlayerExit's contact
                // clear, the move itself once unlocked): they step on the run's ticks, as enginesim's Scheduler does.
                __result = null;
                if (routine.MoveNext()) Park(new Routine { target = __instance, body = routine }, routine.Current);
                return false;
            }
            if (!(__instance is upgradeBox box) || !routine.GetType().Name.Contains("IE_BuyMax")) return true;
            __result = null;
            if (routine.MoveNext()) BuyMax.Add((box, routine));
            return false;
        }

        /// <summary>Where a parked coroutine resumes in a tick (Unity's player loop at playback's frame rate).</summary>
        internal enum Wait { Seconds, FixedUpdate, Frame, EndOfFrame }

        internal sealed class Routine
        {
            public MonoBehaviour target;
            public IEnumerator body;
            public object yielded;
            public Wait wait;
            public int due;
        }

        static void Park(Routine r, object yielded)
        {
            r.yielded = yielded;
            switch (yielded)
            {
                case WaitForSeconds seconds:
                    // Due at the start of that tick, as an Invoke of the same delay (a rendered frame between two ticks resumes it).
                    r.wait = Wait.Seconds;
                    r.due = _tick + Mathf.FloorToInt(WaitSeconds(seconds) / Time.fixedDeltaTime) + 1;
                    break;
                case WaitForFixedUpdate _: r.wait = Wait.FixedUpdate; break;
                case WaitForEndOfFrame _: r.wait = Wait.EndOfFrame; break;
                case null: r.wait = Wait.Frame; break;
                default:
                    TasPlugin.Log?.LogWarning("Turbo: " + r.target.GetType().Name + " coroutine yields " + yielded.GetType().Name + "; Unity resumes it.");
                    r.target.StartCoroutine(Continue(r.body, yielded));
                    return;
            }
            Routines.Add(r);
        }

        static void Resume(Wait wait)
        {
            if (Routines.Count == 0) return;
            Routine[] parked = Routines.ToArray();
            // As the game's player loop: a WaitForFixedUpdate resumes in the physics step (deltaTime the fixed step), the
            // others in the rendered frame that crosses the tick (a zip mover's first tween step after its WaitForSeconds).
            _resumeDeltaTime = wait == Wait.FixedUpdate ? Time.fixedDeltaTime : MacroEngine.FrameGrid.Playback.CrossStep;
            try
            {
                foreach (Routine r in parked)
                {
                    if (r.wait != wait || wait == Wait.Seconds && r.due > _tick) continue;
                    Routines.Remove(r);
                    if (r.target == null) continue;
                    if (r.body.MoveNext()) Park(r, r.body.Current);
                }
            }
            finally { _resumeDeltaTime = 0f; }
        }

        // Time.deltaTime inside a parked coroutine's step: turbo resumes it from its own loop in one Unity frame, whose real
        // deltaTime is that frame's, not the step the game's player loop would resume it in. The enginesim host does the same
        // (Scheduler.Resume, Time.resumeDeltaTime).
        static float _resumeDeltaTime;

        static float CoroutineDeltaTime() { return _resumeDeltaTime > 0f ? _resumeDeltaTime : Time.deltaTime; }

        static IEnumerable<CodeInstruction> UseCoroutineDeltaTime(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo deltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo replacement = AccessTools.Method(typeof(TurboRunner), nameof(CoroutineDeltaTime));
            foreach (CodeInstruction i in instructions)
            {
                if (i.Calls(deltaTime)) i.operand = replacement;
                yield return i;
            }
        }

        /// <summary>A coroutine a run left parked, handed to Unity: it waits out what's left of its yield, then runs on.</summary>
        static IEnumerator Continue(IEnumerator body, object yielded)
        {
            yield return yielded;
            while (body.MoveNext()) yield return body.Current;
        }

        static (MonoBehaviour target, IEnumerator body, object yielded, Wait wait, int due)[] CaptureRoutines(int next)
        {
            return Routines.ConvertAll(r => (r.target, (IEnumerator)CloneObject(r.body), r.yielded, r.wait, r.due - next)).ToArray();
        }

        static void RestoreRoutines((MonoBehaviour target, IEnumerator body, object yielded, Wait wait, int due)[] saved)
        {
            Routines.Clear();
            if (saved == null) return;
            foreach (var (target, body, yielded, wait, due) in saved)
                Routines.Add(new Routine { target = target, body = (IEnumerator)CloneObject(body), yielded = yielded, wait = wait, due = due });
        }

        /// <summary>
        /// FloatingOrigin.LateUpdate, as a rendered frame runs it after Update: past Threshold from scene (0, 0) it starts
        /// MoveOrigin, which moves every root object back by the player's scene position after the next physics step.
        /// </summary>
        static void OriginFrame()
        {
            FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
            if (origin != null && origin.isActiveAndEnabled && origin.ReferenceObject != null) _originLateUpdate(origin);
        }

        static void ResumeRebase()
        {
            IEnumerator routine = _rebase;
            _rebase = null;
            routine?.MoveNext();
        }

        /// <summary>A rebase still waiting when the run ends goes back to Unity's loop, which resumes it after the next physics step.</summary>
        static IEnumerator AfterNextFixedStep(IEnumerator routine)
        {
            yield return new WaitForFixedUpdate();
            routine.MoveNext();
        }

        /// <summary>
        /// Puts the world back at a snapshot's FloatingOrigin: what MoveOrigin does, by the difference (every root object, the
        /// camera, the background scrollers, particles, trails, lines, currentOrigin).
        /// </summary>
        static void ShiftOrigin(FloatingOrigin origin, Vector3 offset)
        {
            foreach (string method in new[] { "MoveRootTransforms", "MoveParticles", "MoveTrailRenderers", "MoveLineRenderers" })
            {
                if (method == "MoveParticles" && !origin.UpdateParticles || method == "MoveTrailRenderers" && !origin.UpdateTrailRenderers
                    || method == "MoveLineRenderers" && !origin.UpdateLineRenderers) continue;
                AccessTools.Method(typeof(FloatingOrigin), method).Invoke(origin, new object[] { offset });
            }
            Camera cam = Camera.main;
            if (cam != null)
            {
                cameraMover mover = cam.GetComponent<cameraMover>();
                if (mover != null) mover.Translate(-offset);
                cam.transform.position += offset;
            }
            foreach (backgroundScroller scroller in UnityEngine.Object.FindObjectsByType<backgroundScroller>(FindObjectsSortMode.None)) scroller.Translate(offset);
            origin.currentOrigin -= offset;
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
            /// <summary>Every zip mover's fields, layer (MakeIntangible moves it off Ground) and pose, and the coroutines parked on a yield.</summary>
            public (PlatformMover mover, object[] fields, int layer, Vector3 local, (Vector2 position, Vector2 velocity) body)[] movers;
            /// <summary>Every refill orb's fields (its cooldown after a use and whether it is usable).</summary>
            public (JiggleDropScript orb, object[] fields)[] orbs;
            /// <summary>
            /// Every spike's live state (spikeScript <c>counter</c>, <c>playerKilled</c>). A restore mutes the callbacks around the
            /// player's old position, so a run that ended in a spike never got its OnTriggerExit2D reset: without this the next run
            /// (and a world exported after it) kept <c>playerKilled</c> on and passed through that spike.
            /// </summary>
            public (spikeScript spike, int counter, bool killed)[] spikes;
            public (MonoBehaviour target, IEnumerator body, object yielded, Wait wait, int due)[] routines;
            public float rotation, angularVelocity;
            public (Vector2 offset, Vector2 size, int exclude, bool enabled)[] colliders;
            public (int due, MonoBehaviour target, string method)[] pending;
            public Economy economy;
            public Vman vman;
            /// <summary>
            /// The cameraMover's fields: a camZoneScript's OnTriggerStay2D points the camera at its zone, and only its
            /// OnTriggerExit2D gives it back to the player, which a restore mutes. Without this the camera stayed on the last
            /// zone a run entered (a FalseEnding-Any% turbo left it at the far end of the map, the player back at the start).
            /// </summary>
            public object[] camera;
            /// <summary>FloatingOrigin.currentOrigin, and whether a MoveOrigin was waiting for the next physics step.</summary>
            public Vector3 origin;
            public bool rebasing;
            /// <summary>A category start's canonical rebase reference (<see cref="PutCanonicalOrigin"/>): a restore puts the
            /// world exactly there again instead of shifting it back by the difference (which rounds by what ran since).</summary>
            public Vector3? canonical;
            /// <summary>The body's position at the start and end of the last physics step (its interpolation's ends).</summary>
            public Vector2 stepFrom, stepTo;
        }

        internal sealed class Vman
        {
            public bool active, meta, blockEnding;
            public double timeLeft, score;

            public static Vman Capture()
            {
                VmanScript script = VmanScript.Instance;
                return new Vman { active = VmanScript.isCurrentlyVman, meta = VmanScript.metaVman, timeLeft = VmanScript.vmanTimeLeft,
                    score = VmanScript.vmanScore, blockEnding = script != null && script.BlockEnding };
            }

            public void Restore()
            {
                VmanScript.isCurrentlyVman = active;
                VmanScript.metaVman = meta;
                VmanScript.vmanTimeLeft = timeLeft;
                VmanScript.vmanScore = score;
                if (VmanScript.Instance != null) VmanScript.Instance.BlockEnding = blockEnding;
            }
        }

        internal sealed class Economy
        {
            public Dictionary<globalStats.Currencies, double> currencies;
            public Dictionary<globalStats.globalUpgradeSet, double> globalUpgrades;
            public (upgradeBox box, object[] fields)[] boxes;
            public (localUpgrades script, Dictionary<localUpgrades.localUpgradeSet, double> upgrades)[] locals;
            public (clonesScript clones, object[] fields, HashSet<Transform> children)[] clones;
            /// <summary>Each clonesScript's best path arrays (updateClonePath replaces them, never edits them in place).</summary>
            public (clonesScript clones, Vector2[] path, Sprite[] sprites, Vector2[] scales)[] paths;
            public List<int> visitedTeleporters;
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
            /// <summary>
            /// Whether each object of the active scene is on. A run turns objects on and off (a purchase's StatuePrestigeText, whose
            /// ObjectBobber.OnEnable draws UnityEngine.Random): one a run left on skipped that draw in the next run, and every clone
            /// roll after it shifted.
            /// </summary>
            public (GameObject obj, bool active)[] objects;
        }

        static readonly FieldInfo BreakerPlayerLight = AccessTools.Field(typeof(tripBreakerScript), "playerLight");
        static readonly AccessTools.FieldRef<colouredBlockSwapper, GameObject[]> OrangeBlocks = AccessTools.FieldRefAccess<colouredBlockSwapper, GameObject[]>("orange");
        static readonly AccessTools.FieldRef<colouredBlockSwapper, GameObject[]> BlueBlocks = AccessTools.FieldRefAccess<colouredBlockSwapper, GameObject[]>("blue");

        static Scene CaptureScene(upgradeBox[] boxes)
        {
            var breakers = new List<(tripBreakerScript, bool[], bool[], (GameObject, bool)[], Behaviour, bool, float, float)>();
            foreach (tripBreakerScript t in UnityEngine.Object.FindObjectsByType<tripBreakerScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                (GameObject, bool)[] objects = TasPlugin.GameBreakerLights(t).ToArray();
                var light = BreakerPlayerLight.GetValue(t) as Behaviour;
                breakers.Add((t, (bool[])TasPlugin.BreakerActiveLights(t)?.Clone(), (bool[])t.fixedAreas?.Clone(), objects,
                    light, light != null && light.enabled, LightRadius(light, "pointLightOuterRadius"), LightRadius(light, "pointLightInnerRadius")));
            }
            colouredBlockSwapper swapper = UnityEngine.Object.FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            var blocks = new List<(Collider2D, bool)>();
            if (swapper != null)
                foreach (GameObject o in (OrangeBlocks(swapper) ?? new GameObject[0]).Concat(BlueBlocks(swapper) ?? new GameObject[0]))
                    if (o != null) foreach (Collider2D c in o.GetComponents<Collider2D>()) blocks.Add((c, c.enabled));
            UnityEngine.SceneManagement.Scene scene = SceneManager.GetActiveScene();
            Transform[] all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return new Scene {
                objects = all.Where(t => t.gameObject.scene == scene).Select(t => (t.gameObject, t.gameObject.activeSelf)).ToArray(),
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
            if (s.objects != null)
                foreach (var (obj, active) in s.objects) if (obj != null && obj.activeSelf != active) obj.SetActive(active);
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
                if (activeLights != null) TasPlugin.BreakerActiveLights(script) = (bool[])activeLights.Clone();
                if (fixedAreas != null) script.fixedAreas = (bool[])fixedAreas.Clone();
                foreach (var (obj, active) in objects) if (obj != null && obj.activeSelf != active) obj.SetActive(active);
                if (light == null) continue;
                light.enabled = lightEnabled;
                light.GetType().GetProperty("pointLightOuterRadius").SetValue(light, outer);
                light.GetType().GetProperty("pointLightInnerRadius").SetValue(light, inner);
            }
            // The game's own swap first, for its tilemap visuals (opacity, tint, sorting); then the captured colliders exactly.
            colouredBlockSwapper swapper = UnityEngine.Object.FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            if (swapper != null) swapper.swapBlocks(s.blueActive);
            foreach (var (collider, enabled) in s.blocks) if (collider != null) collider.enabled = enabled;
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
                paths = Array.ConvertAll(clones, c => (c, c.clonePath, c.cloneSprites, c.cloneScales)),
                visitedTeleporters = Singleton<globalStats>.Instance.visitedTeleporters == null ? null : new List<int>(Singleton<globalStats>.Instance.visitedTeleporters),
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
            Singleton<globalStats>.Instance.visitedTeleporters = e.visitedTeleporters == null ? null : new List<int>(e.visitedTeleporters);
            if (e.paths != null)
                foreach (var (script, path, sprites, scales) in e.paths)
                    if (script != null) { script.clonePath = path; script.cloneSprites = sprites; script.cloneScales = scales; }
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
            FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
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
                pending = pending, economy = economy ? CaptureEconomy() : null, vman = Vman.Capture(),
                camera = movement.cam != null ? FieldState.Capture(movement.cam) : null,
                origin = origin != null ? origin.currentOrigin : Vector3.zero, rebasing = _rebase != null,
                stepFrom = Active ? _stepFrom : body.position, stepTo = Active ? _stepTo : body.position,
                movers = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<PlatformMover>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    m => (m, FieldState.Capture(m), m.gameObject.layer, m.transform.localPosition, MoverPose(m))),
                orbs = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    o => (o, FieldState.Capture(o))),
                spikes = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    k => (k, SpikeCounter(k), SpikeKilled(k))),
                routines = CaptureRoutines(next) };
        }

        static readonly AccessTools.FieldRef<spikeScript, int> SpikeCounter = AccessTools.FieldRefAccess<spikeScript, int>("counter");
        static readonly AccessTools.FieldRef<spikeScript, bool> SpikeKilled = AccessTools.FieldRefAccess<spikeScript, bool>("playerKilled");

        /// <summary>The spikes' counters and kill flags as the snapshot had them (<see cref="Snapshot.spikes"/>).</summary>
        static void RestoreSpikes(Snapshot s)
        {
            if (s.spikes == null) return;
            foreach (var (spike, counter, killed) in s.spikes)
            {
                if (spike == null) continue;
                SpikeCounter(spike) = counter;
                SpikeKilled(spike) = killed;
            }
        }

        /// <summary>The refill orbs as the snapshot had them; their sprites follow (the game's refresh), the usable flag stays the snapshot's.</summary>
        static void RestoreOrbs(Snapshot s)
        {
            if (s.orbs == null) return;
            foreach (var (orb, fields) in s.orbs)
            {
                if (orb == null) continue;
                FieldState.Restore(orb, fields);
                bool usable = OrbUsable(orb);
                OrbState.Invoke(orb, new object[] { false });
                OrbUsable(orb) = usable;
            }
        }

        /// <summary>Every spike as no touch left it (spikeScript.OnTriggerExit2D: <c>counter</c> 0, <c>playerKilled</c> off).</summary>
        public static void ClearSpikes()
        {
            foreach (spikeScript spike in UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                SpikeCounter(spike) = 0;
                SpikeKilled(spike) = false;
            }
        }

        public static void Restore(Snapshot s, Movement movement)
        {
            movement.CancelInvoke();
            if (s.canonical is Vector3 reference) PutCanonicalOrigin(reference); else PutOrigin(s.origin);
            RestoreMovers(s);
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
            // The scene (coloured block colliders, zone objects) before the settle, so contacts settle against the snapshot's
            // colliders, not the live game's: a block the last run swapped on under the player would otherwise make a fresh
            // contact (and its enter callbacks) on the first judged tick. Blocks it turns off were muted above.
            if (s.economy != null) RestoreEconomy(s.economy);
            Physics2D.SyncTransforms();
            MuteAround(own, muted);
            SettleContacts(body, muted);
            FieldState.Restore(movement, s.movement);
            foreach (var (course, fields) in s.courses) FieldState.Restore(course, fields);
            // After the economy: the orbs' sprites follow the refill upgrades the snapshot had.
            RestoreOrbs(s);
            RestoreSpikes(s);
            s.vman?.Restore();
            if (s.camera != null && movement.cam != null)
            {
                FieldState.Restore(movement.cam, s.camera);
                SavestateService.SnapCamera(s.position);
            }
            // Last: RestoreEconomy's SetActive/enabled on boxes runs their OnEnable (upgradeBox.OnEnable draws twice).
            UnityEngine.Random.state = s.random;
            movement.transform.position = s.position;
            body.position = s.bodyPosition;
            body.rotation = s.rotation;
            body.linearVelocity = s.velocity;
            body.angularVelocity = s.angularVelocity;
            Physics2D.SyncTransforms();
        }

        /// <summary>
        /// The zip movers as the snapshot had them: fields, layer, and the coroutines a turbo run had parked. Unity-run ones
        /// (a run's leftovers handed back, real-time play) stop: the snapshot's state replaces what they would still change.
        /// </summary>
        static void RestoreMovers(Snapshot s)
        {
            if (s.movers == null) return;
            foreach (var (mover, fields, layer, local, pose) in s.movers)
            {
                if (mover == null) continue;
                mover.StopAllCoroutines();
                FieldState.Restore(mover, fields);
                if (mover.gameObject.layer != layer) mover.gameObject.layer = layer;
                // Where a zip mover stood: once unlocked a run moves it (IE_AdvanceToNextState), and nothing puts it back.
                mover.transform.localPosition = local;
                Rigidbody2D body = MoverBody(mover);
                if (body != null)
                {
                    body.position = pose.position;
                    body.linearVelocity = pose.velocity;
                }
            }
            RestoreRoutines(s.routines);
        }

        static (Vector2 position, Vector2 velocity) MoverPose(PlatformMover mover)
        {
            Rigidbody2D body = MoverBody(mover);
            return body == null ? default : (body.position, body.linearVelocity);
        }

        /// <summary>
        /// The world at a FloatingOrigin a turbo run started from (a run may rebase it on the way), with no MoveOrigin waiting
        /// (a Unity-scheduled one from real frames included). A snapshot taken while one waited gets it back when its run
        /// starts (Run's resume).
        /// </summary>
        public static void PutOrigin(Vector3 target)
        {
            _rebase = null;
            FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
            if (origin == null) return;
            origin.StopAllCoroutines();
            OriginRebasing(origin) = false;
            if (origin.currentOrigin != target) ShiftOrigin(origin, origin.currentOrigin - target);
        }

        /// <summary>Every scene root's position at FloatingOrigin (0, 0), as the scene loaded them (<see cref="RecordPristineRoots"/>).</summary>
        static readonly Dictionary<Transform, Vector3> PristineRoots = new Dictionary<Transform, Vector3>();
        static bool _pristineWarned;

        /// <summary>
        /// The scene roots FloatingOrigin moves (MoveRootTransforms' own set), recorded while currentOrigin is still (0, 0): the
        /// scene as loaded, before its first rebase (TasPlugin's MoveRootTransforms prefix, or the first canonical origin).
        /// A reloaded scene (the recorded roots destroyed) is recorded again.
        /// </summary>
        internal static void RecordPristineRoots(FloatingOrigin origin)
        {
            if (origin == null || origin.currentOrigin.x != 0f || origin.currentOrigin.y != 0f || origin.currentOrigin.z != 0f) return;
            if (PristineRoots.Count > 0 && PristineRoots.Keys.All(t => t != null)) return;
            PristineRoots.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = SceneManager.GetSceneAt(i);
                if (!origin.UpdateAllScenes && scene != SceneManager.GetActiveScene() || !scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects()) PristineRoots[root.transform] = root.transform.position;
            }
        }

        /// <summary>
        /// A category start's world, whatever ran before: FloatingOrigin as its own MoveOrigin leaves it after a rebase on the
        /// player standing at the start's quick restart point, from the scene as loaded. Every scene root at its loaded position
        /// minus <paramref name="reference"/> (MoveRootTransforms' <c>position -= offset</c> from origin (0, 0)), currentOrigin
        /// <c>-reference</c>. PutOrigin alone shifts by the difference from wherever the last target left the world, and that
        /// rounds differently for each origin it comes from (a Course 4 route died at tick 274 from one and finished from two others).
        /// A root the game moved itself (the camera, HUD) only shifts.
        /// </summary>
        public static void PutCanonicalOrigin(Vector3 reference)
        {
            FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
            if (origin == null) return;
            RecordPristineRoots(origin);
            if (PristineRoots.Count == 0 || PristineRoots.Keys.Any(t => t == null))
            {
                // Not recorded at (0, 0) (a scene loaded before the plugin saw it): the loaded positions as the shifts left them.
                if (!_pristineWarned) TasPlugin.Log?.LogWarning("FloatingOrigin: scene roots not recorded at (0, 0); taking them from origin " + origin.currentOrigin + ".");
                _pristineWarned = true;
                PristineRoots.Clear();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    foreach (GameObject root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                        PristineRoots[root.transform] = root.transform.position - origin.currentOrigin;
            }
            Vector3 target = -reference;
            PutOrigin(target);
            foreach (KeyValuePair<Transform, Vector3> root in PristineRoots)
            {
                Vector3 canonical = root.Value - reference;
                if ((root.Key.position - canonical).sqrMagnitude < 1f) root.Key.position = canonical;
            }
            origin.currentOrigin = target;
        }

        /// <summary>The rebase reference of a category start: the player where <c>Movement.respawn</c> puts it at the quick
        /// restart point (world frame, from origin (0, 0): the reset point, 12 down).</summary>
        public static Vector3 CanonicalReference(Vector2 resetPoint) { return new Vector3(resetPoint.x, resetPoint.y - 12f, 0f); }

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

        /// <summary>Puts the player's transform at a position (its z kept) and returns what it reads back, to tell a game write from it.</summary>
        static Vector3 Place(Transform t, Vector2 at)
        {
            t.position = new Vector3(at.x, at.y, t.position.z);
            return t.position;
        }

        public static int Run(Movement movement, Func<bool> keepRunning, int maxTicks, Action afterTick = null, Snapshot resume = null)
        {
            if (movement == null) throw new InvalidOperationException("Player not found.");
            MakeCalls();
            SimulationMode2D savedMode = Physics2D.simulationMode;
            courseScript[] courses = UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            JiggleDropScript[] orbs = UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsSortMode.None);
            // Steps in 1x's Update order (instance id, lowest first; a probe: Course 2's clones before Course 3's in one frame), as
            // the engine does: two courses spawning in one frame take their fast/big rolls in that order.
            Array.Sort(clones, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            float dt = Time.fixedDeltaTime;
            FloatingNumberScript numbers = Singleton<FloatingNumberScript>.Instance;
            bool spawnNumbers = numbers != null && numbers.ActuallySpawnText;
            Pending.Clear();
            BuyMax.Clear();
            if (resume != null) Pending.AddRange(resume.pending);
            if (resume != null) RestoreRoutines(resume.routines);
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            Transform player = movement.transform;
            MacroEngine.FrameGrid frames = MacroEngine.FrameGrid.Playback;
            _stepFrom = resume != null ? resume.stepFrom : body.position;
            _stepTo = resume != null ? resume.stepTo : body.position;
            Active = true;
            if (resume != null && resume.rebasing) OriginFrame();
            if (numbers != null) numbers.ActuallySpawnText = false;
            Physics2D.simulationMode = SimulationMode2D.Script;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long keepAliveAt = KeepAliveMs;
            try
            {
                for (_tick = 0; _tick < maxTicks && keepRunning(); _tick++)
                {
                    KeepWindowAlive(clock, ref keepAliveAt);
                    for (int i = 0; i < Pending.Count; i++)
                    {
                        if (Pending[i].due > _tick) continue;
                        var (_, target, method) = Pending[i];
                        Pending.RemoveAt(i--);
                        if (target != null) CallInvoked(target, method);
                    }
                    Resume(Wait.Seconds);
                    // FixedUpdate reads the last rendered frame's interpolation blend; its writes move the body from there.
                    Vector3 lag = Place(player, Vector2.Lerp(_stepFrom, _stepTo, frames.LastAlpha));
                    _freezeFor = -1f;
                    _pauseFrames = _pauseAfter = 0;
                    _fixedUpdate(movement);
                    foreach (courseScript course in courses)
                        if (course != null && course.isActiveAndEnabled) _courseFixedUpdate(course);
                    if (VmanScript.isCurrentlyVman && VmanScript.Instance != null && VmanScript.Instance.isActiveAndEnabled)
                        _vmanFixedUpdate(VmanScript.Instance);
                    foreach (upgradeBox box in boxes)
                    {
                        if (box == null || !box.isActiveAndEnabled) continue;
                        ref float cooldown = ref BoxCooldown(box);
                        if (cooldown >= 0f) cooldown -= dt;
                    }
                    // A used refill orb counts its cooldown down and grows back at its end (JiggleDropScript.FixedUpdate).
                    foreach (JiggleDropScript orb in orbs)
                        if (orb != null && orb.isActiveAndEnabled && OrbCooldown(orb) > 0f) _orbFixedUpdate(orb);
                    // No write: the blend was never the body's, so the sync before the step leaves the body where it is.
                    if (player.position == lag) Place(player, _stepTo);
                    Physics2D.SyncTransforms();
                    _stepFrom = body.position;
                    Physics2D.Simulate(dt);
                    // A physics callback's write (a sideways spring's push, Movement.hitSpring) moves the body there at once and
                    // drops the interpolation, as at 1x: the tick's frames show the written position, and the next tick blends from it.
                    // Unity's own pose write reads back through the parent a few 1e-5 off the body, so only a real move counts.
                    if (((Vector2)player.position - body.position).sqrMagnitude > 1e-4f)
                    {
                        Physics2D.SyncTransforms();
                        _stepFrom = body.position;
                    }
                    _stepTo = body.position;
                    Resume(Wait.FixedUpdate);
                    FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
                    Vector3 originBefore = origin != null ? origin.currentOrigin : Vector3.zero;
                    ResumeRebase();
                    if (origin != null && origin.currentOrigin != originBefore)
                    {
                        Vector2 by = origin.currentOrigin - originBefore;
                        _stepFrom += by;
                        _stepTo += by;
                    }
                    // The tick's rendered frames, the dash freeze's first: Update at each blend. A step-up write moves the body
                    // there and drops the interpolation, so the frames after show it. A pause holds its frame's time as a
                    // freeze does (MacroEngine.FrameRepeats).
                    int frozen = _freezeFor >= 0f ? frames.FrozenFrames(_freezeFor) : 0;
                    // clonesScript.Update runs in every frame too, on that frame's Time.deltaTime (0 in a freeze's or pause's repeats).
                    for (int k = 0; k < frames.Alphas.Length; k++)
                    {
                        int repeats = MacroEngine.FrameRepeats(k, frames.Alphas.Length, frozen, _pauseFrames, _pauseAfter);
                        // The repeats are frames the run stands still in: its live time (MacroEngine.HeldFrames), counted as the
                        // offline engine and 1x count them.
                        TasPlugin.CountHeldFrames(repeats);
                        for (int repeat = repeats; repeat >= 0; repeat--)
                        {
                            Vector3 at = Place(player, Vector2.Lerp(_stepFrom, _stepTo, frames.Alphas[k]));
                            _update(movement);
                            if (player.position != at)
                            {
                                Physics2D.SyncTransforms();
                                _stepFrom = _stepTo = player.position;
                            }
                            float frameDt = repeat == repeats ? frames.Step(k) : 0f;
                            foreach (clonesScript c in clones)
                                if (c != null && c.isActiveAndEnabled) StepClones(c, frameDt);
                        }
                    }
                    Place(player, _stepTo);
                    Resume(Wait.Frame);
                    OriginFrame();
                    EndOfFrame();
                    Resume(Wait.EndOfFrame);
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
                    else CallInvoked(target, method);
                }
                Pending.Clear();
                BuyMax.Clear();
                foreach (Routine r in Routines)
                {
                    if (r.target == null) continue;
                    object wait = r.wait == Wait.Seconds ? new WaitForSeconds(Mathf.Max(0, r.due - _tick) * Time.fixedDeltaTime) : r.yielded;
                    r.target.StartCoroutine(Continue(r.body, wait));
                }
                Routines.Clear();
                if (_rebase != null)
                {
                    FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
                    if (origin != null) origin.StartCoroutine(AfterNextFixedStep(_rebase));
                    _rebase = null;
                }
            }
        }
    }
}
