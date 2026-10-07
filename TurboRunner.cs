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
        static Action<globalStats> _statsFixedUpdate;
        static Action<AtomColliderSystem> _atomUpdate;
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
            _statsFixedUpdate = AccessTools.MethodDelegate<Action<globalStats>>(AccessTools.Method(typeof(globalStats), "FixedUpdate"));
            _atomUpdate = AccessTools.MethodDelegate<Action<AtomColliderSystem>>(AccessTools.Method(typeof(AtomColliderSystem), "Update"));
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
        internal static void KeepWindowAlive(System.Diagnostics.Stopwatch clock, ref long next)
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
        static readonly List<Routine> Routines = new List<Routine>();
        static readonly AccessTools.FieldRef<WaitForSeconds, float> WaitSeconds = AccessTools.FieldRefAccess<WaitForSeconds, float>("m_Seconds");
        static readonly Func<object, object> CloneObject = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));
        // FloatingOrigin.MoveOrigin, started by its LateUpdate and waiting for the next physics step (WaitForFixedUpdate).
        static IEnumerator _rebase;
        static int _tick;
        /// <summary>The run's rendered frames so far, a held frame (a freeze's, a pause's) included: the tick's physics step is in
        /// its first frame. A <c>yield return null</c> resumes in a later frame than the one it yielded in.</summary>
        static int _frame;
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
            harmony.Patch(AccessTools.Method(typeof(AtomColliderSystem), "OnEnable"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(AfterAtomEnable))));
            harmony.Patch(AccessTools.Method(typeof(AtomColliderSystem), "callAddNewAtom"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeAtomCall))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "InvokeRepeating", new[] { typeof(string), typeof(float), typeof(float) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeInvokeRepeating))));
            harmony.Patch(AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeStartCoroutine))));
            harmony.Patch(AccessTools.Method(typeof(ZoneLoader), "LoadZone"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeLoadZone))));
            harmony.Patch(AccessTools.Method(typeof(ZoneLoader), "_LoadZone"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeZoneLoaded))));
            harmony.Patch(AccessTools.Method(typeof(Movement), "TimeStop"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeTimeStop))));
            var modelDeltaTime = new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(UseModelDeltaTime)));
            harmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(PlatformMover), "IE_AdvanceToNextState")), transpiler: modelDeltaTime);
            // TreeSegmentActivationComponent.growTreePart's local IE_Grow (TreeSegmentActivationComponent.cs:32-125). Found by the
            // compiler's local-function name: a build that names it otherwise loses only this patch, not the ones after it.
            MethodInfo grow = AccessTools.FirstMethod(typeof(TreeSegmentActivationComponent), m => m.Name.Contains("IE_Grow"));
            MethodInfo growStep = grow == null ? null : AccessTools.EnumeratorMoveNext(grow);
            if (growStep != null) harmony.Patch(growStep, transpiler: modelDeltaTime);
            else TasPlugin.Log?.LogWarning("Turbo: TreeSegmentActivationComponent has no IE_Grow iterator; a tree grown in turbo reads the frame's Time.deltaTime.");
            harmony.Patch(AccessTools.Method(typeof(AtomColliderSystem), "HandleTierLogic"), transpiler: modelDeltaTime);
            harmony.Patch(AccessTools.Method(typeof(AtomColliderSystem), "HandleMovement"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeHandleAfterimages))));
            harmony.Patch(AccessTools.Method(typeof(Movement), "HandleAfterimages"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeHandleAfterimages))));
            foreach (Type drawer in StartWatched)
                harmony.Patch(AccessTools.Method(drawer, "Start"), prefix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(BeforeDrawingStart))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(AfterDrawingStart))));
        }

        // README ### Rules, exception 12: the Start draws of what a run wakes. Unity runs a woken behaviour's Start before its
        // first Update, in the rendered frame after the wake (the same frame when woken in the physics step, the next when
        // woken at a frame's end), but a run is one Unity frame: its Start would draw after the run, at another point of the
        // RNG's sequence than 1x's. Turbo draws them at the modelled frame (StartPass) and puts UnityEngine.Random's state
        // back around Unity's later real Start (as TasPlugin.PresentationDraw does for exception 11).
        static readonly Type[] StartDrawers = { typeof(LightFlicker), typeof(RandomAnimSpeedComponent), typeof(JiggleDropScript) };
        /// <summary>The drawers, the atom collider (its Start recomputes NP) and the upgrade boxes (WokenStarts): whose Start a woken one waits for.</summary>
        static readonly Type[] StartWatched = StartDrawers.Concat(new[] { typeof(AtomColliderSystem), typeof(upgradeBox) }).ToArray();

        /// <summary>Every watched behaviour whose Start Unity hasn't run nor a run modelled (the trace start's <c>neverStarted</c>).</summary>
        internal static IEnumerable<Behaviour> NeverStarted()
        {
            foreach (Type type in StartWatched)
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (o is Behaviour b && !StartsRun.Contains(b) && !_startsModelled.Contains(b)) yield return b;
        }
        static readonly AccessTools.FieldRef<RandomAnimSpeedComponent, bool> AnimIsBroken = AccessTools.FieldRefAccess<RandomAnimSpeedComponent, bool>("isBroken");
        /// <summary>Behaviours whose Start a run drew and Unity hasn't run yet (in snapshots: a restore before the wake undoes it).</summary>
        static HashSet<Behaviour> _startsModelled = new HashSet<Behaviour>();
        /// <summary>Behaviours whose real Start Unity ran (it never runs again, whatever a restore puts back).</summary>
        static readonly HashSet<Behaviour> StartsRun = new HashSet<Behaviour>();
        /// <summary>This run's candidates: drawing behaviours off at its start whose Start hasn't run or been drawn.</summary>
        static readonly List<Behaviour> StartWaiting = new List<Behaviour>();

        /// <summary>StartPass is running a woken box's Start itself: not Unity's real one.</summary>
        static bool _modellingStart;

        /// <summary>Unity's real Start of a behaviour a run modelled: the RNG and an upgrade box's fields (cap, base cap, costs) as the
        /// modelled Start left them, put back after it (README ### Rules, exception 12), so it doesn't apply twice.</summary>
        static void BeforeDrawingStart(Behaviour __instance, out (UnityEngine.Random.State rng, object[] box)? __state)
        {
            __state = null;
            if (_modellingStart) return;
            StartsRun.Add(__instance);
            if (_startsModelled.Remove(__instance)) __state = (UnityEngine.Random.state, __instance is upgradeBox box ? CaptureFields(box) : null);
        }

        static void AfterDrawingStart(Behaviour __instance, (UnityEngine.Random.State rng, object[] box)? __state)
        {
            if (__state == null) return;
            var (drawn, box) = __state.Value;
            if (box != null) RestoreFields(__instance, box);
            UnityEngine.Random.state = drawn;
        }

        static void CollectStartWaiting()
        {
            StartWaiting.Clear();
            foreach (Behaviour b in NeverStarted()) if (!b.isActiveAndEnabled) StartWaiting.Add(b);
        }

        /// <summary>A modelled frame's Start pass, before its Updates: each waiting behaviour now on draws what its Start draws
        /// (LightFlicker.cs:26: 1; RandomAnimSpeedComponent.cs:16-21: 2, 3 when broken; JiggleDropScript.cs:41: 1), the atom
        /// collider recomputes NP and an upgrade box runs the game's own Start (WokenStarts).</summary>
        static void StartPass()
        {
            for (int i = StartWaiting.Count - 1; i >= 0; i--)
            {
                Behaviour b = StartWaiting[i];
                if (b == null) { StartWaiting.RemoveAt(i); continue; }
                if (!b.isActiveAndEnabled) continue;
                StartWaiting.RemoveAt(i);
                if (b is upgradeBox box)
                {
                    // The game's own Start (upgradeBox.cs:199-298) at the modelled frame; Unity's after the run gets the box's fields put back.
                    if (WokenStarts.Unmodeled(box) is string why) { TasPlugin.AbortRun(why); continue; }
                    _modellingStart = true;
                    try { WokenStarts.Start.Invoke(box, null); }
                    finally { _modellingStart = false; }
                }
                // AtomColliderSystem.Start (AtomColliderSystem.cs:106-109) recomputes NP; Unity's own after the run does again.
                else if (b is AtomColliderSystem) Singleton<globalStats>.Instance?.RecalcNpRewardPerTick();
                else
                {
                    int draws = b is RandomAnimSpeedComponent anim ? (AnimIsBroken(anim) ? 3 : 2) : 1;
                    for (int d = 0; d < draws; d++) _ = UnityEngine.Random.value;
                }
                _startsModelled.Add(b);
            }
        }

        /// <summary>
        /// Movement.Update's afterimages are presentation only (the offline engine cuts them, enginesim/cuts.txt), but in turbo
        /// they ran on the real frame's Time.deltaTime, held for the whole run: a fast stretch could take all 21 pooled
        /// sprites, and the next <c>inactiveAfterimages.Pop()</c> threw "Stack empty." out of the run. Turbo skips them, and
        /// AtomColliderSystem.HandleMovement too (the atom sprites' easing, AtomColliderSystem.cs:251-262: presentation, which
        /// the engine skips).
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

        /// <summary>A zone activator started ZoneLoader.IE_Load and its _LoadZone hasn't run yet (Unity's or a run's): a trace start
        /// can't carry it (EngineSimExport.WriteSnapshot refuses one).</summary>
        static bool _zoneLoadPending;

        static void BeforeLoadZone(ZoneLoader __instance, int zoneToLoad, bool force)
        {
            if (__instance.activeZone != zoneToLoad || force) _zoneLoadPending = true;
        }

        static void BeforeZoneLoaded() { _zoneLoadPending = false; }

        static bool BeforeInvoke(MonoBehaviour __instance, string methodName, float time)
        {
            if (!Active) return true;
            Pending.Add((_tick + Mathf.FloorToInt(time / Time.fixedDeltaTime) + 1, __instance, methodName));
            return false;
        }

        // AtomColliderSystem's 0.4 s grid, OnEnable's InvokeRepeating("callAddNewAtom", 0, 0.4) (AtomColliderSystem.cs:111-115).
        // Unity has no API for an Invoke's time left, so the game time (Time.time, the clock Invokes run on) of the grid's
        // start and of its last call are kept: the next call is 0.4 s after the last, or at the start before any.
        // Only Unity's own calls count (outside a run); turbo keeps the grid itself in a run (_atomDue).
        static float _atomEnabledAt = float.NegativeInfinity, _atomLastCall = float.NegativeInfinity;

        static void AfterAtomEnable()
        {
            if (Active) return;
            _atomEnabledAt = Time.time;
            _atomHeldAt = float.NaN;
        }

        static void BeforeAtomCall()
        {
            if (Active) return;
            _atomLastCall = Time.time;
            _atomHeldAt = float.NaN;
        }

        const string AtomCall = "callAddNewAtom";
        // OnEnable's repeat rate (AtomColliderSystem.cs:114).
        const float AtomRepeat = 0.4f;

        static AtomColliderSystem FindAtomCollider()
        {
            return UnityEngine.Object.FindAnyObjectByType<AtomColliderSystem>(FindObjectsInactive.Include);
        }

        /// <summary>Seconds until the collider's Unity-run next callAddNewAtom, or null when none is pending (or its start wasn't seen).</summary>
        static float? AtomGridDueIn(AtomColliderSystem collider)
        {
            if (collider == null || float.IsNegativeInfinity(_atomEnabledAt) || !collider.IsInvoking(AtomCall)) return null;
            float next = _atomLastCall >= _atomEnabledAt ? _atomLastCall + AtomRepeat : _atomEnabledAt;
            return Mathf.Max(0f, next - Time.time);
        }

        /// <summary>
        /// The grid in a run, as the engine's Economy has it: the run tick in whose first rendered frame (after its Updates)
        /// callAddNewAtom comes next (-1: none), then every 0.4 s (20 ticks); one tick before what AtomGridTicks counts. Unity's own InvokeRepeating runs on
        /// Time.time, which stands still through a run (one Unity frame), so turbo takes the grid over: at the run's start
        /// from a restore or the last run (<see cref="_atomHeldDue"/>) or else from Unity's (<see cref="AtomGridDueIn"/>),
        /// and hands it back to Unity at the end (<see cref="HandAtomGrid"/>).
        /// </summary>
        static int _atomDue = -1;
        /// <summary>The grid's first call is due in this frame's coroutine pass (<see cref="BeforeInvokeRepeating"/>).</summary>
        static bool _atomNow;
        static int AtomRepeatTicks { get { return Math.Max(1, Mathf.FloorToInt(AtomRepeat / Time.fixedDeltaTime)); } }
        /// <summary>The run started on a grid Unity ran but whose start wasn't seen (<see cref="AtomGridTicks"/> null) and nothing
        /// in it has set or cancelled the grid since: a capture or the run's end leaves it Unity's (null), not cancelled (-1).</summary>
        static bool _atomUnseen;
        static AtomColliderSystem _atomCollider;
        /// <summary>
        /// The grid a restore or a run's end left, in ticks from the next tick (-1: none, null: Unity's, its start unseen),
        /// exact while no game time has passed since (<see cref="_atomHeldAt"/>, Time.time then) and Unity hasn't called it.
        /// </summary>
        static int? _atomHeldDue;
        static float _atomHeldAt = float.NaN;

        /// <summary>The grid now, in ticks from the next tick, as <see cref="_atomHeldDue"/> counts it; outside a run read
        /// from Unity's as the engine reads a trace start's (floor(dueIn / fixedDeltaTime)).</summary>
        static int? AtomGridTicks(AtomColliderSystem collider, int next, out float? dueIn)
        {
            dueIn = null;
            if (Active) return _atomDue >= 0 ? _atomDue + 1 - next : _atomUnseen ? (int?)null : -1;
            if (Time.time == _atomHeldAt) return _atomHeldDue;
            if (collider == null || !collider.IsInvoking(AtomCall)) return -1;
            dueIn = AtomGridDueIn(collider);
            return dueIn is float s ? Mathf.FloorToInt(s / Time.fixedDeltaTime) : (int?)null;
        }

        /// <summary>
        /// Turbo's grid back to Unity (a restore, a run's end), so 1x goes on from it: CancelInvoke and InvokeRepeating at the
        /// due tick's game time. A grid whose start wasn't seen (null) stays Unity's.
        /// </summary>
        static void HandAtomGrid(int? due)
        {
            _atomHeldDue = due;
            _atomHeldAt = Time.time;
            AtomColliderSystem collider = FindAtomCollider();
            if (collider == null || due == null) return;
            collider.CancelInvoke(AtomCall);
            if (due < 0) return;
            float delay = Mathf.Max(0f, (due.Value - 1) * Time.fixedDeltaTime + 0.5f / MacroEngine.PlaybackFrameRate);
            collider.InvokeRepeating(AtomCall, delay, AtomRepeat);
            _atomEnabledAt = Time.time + delay;
            _atomLastCall = float.NegativeInfinity;
        }

        /// <summary>The collider's OnEnable in a run (AtomColliderSystem.cs:111-115): its InvokeRepeating on the run's ticks, as an Invoke.</summary>
        static bool BeforeInvokeRepeating(MonoBehaviour __instance, string methodName, float time)
        {
            if (!Active || !(__instance is AtomColliderSystem collider) || methodName != AtomCall) return true;
            _atomCollider = collider;
            // Its 0 delay calls it in the frame's own pass (a zone load's frame, k = 2), then in the first frame of every 20th
            // tick after (k = 0): a 1x probe of the zone 1 → 2 crossing.
            _atomNow = time <= 0f;
            _atomDue = _tick + Mathf.FloorToInt(time / Time.fixedDeltaTime) + (_atomNow ? AtomRepeatTicks : 0);
            _atomUnseen = false;
            return false;
        }

        static bool BeforeCancelInvoke(MonoBehaviour __instance, string methodName)
        {
            if (!Active) return true;
            if (__instance is AtomColliderSystem && methodName == AtomCall) { _atomDue = -1; _atomNow = false; _atomUnseen = false; }
            Pending.RemoveAll(p => p.target == __instance && p.method == methodName);
            return false;
        }

        /// <summary>
        /// The TAS editor's seeks go on at 1x after the run: a dash's IE_TimeStop started in it (timeScale 0 until real frames
        /// pass; the run models the freeze itself, _freezeFor) would freeze 1x again after it. Off for every judged run.
        /// </summary>
        internal static bool DropTimeStops;

        /// <summary>After a run that 1x goes on from: the player where the last rendered frame's blend shows it, which is
        /// where 1x's next FixedUpdate reads it (the run leaves it at the step's end).</summary>
        internal static void ShowLastFrame(Movement movement)
        {
            Place(movement.transform, Vector2.Lerp(_stepFrom, _stepTo, MacroEngine.FrameGrid.Playback.Frames(TasPlugin.GridTicks).LastAlpha));
        }

        static bool BeforeStartCoroutine(MonoBehaviour __instance, IEnumerator routine, ref Coroutine __result)
        {
            if (!Active || routine == null) return true;
            if (DropTimeStops && __instance is Movement && routine.GetType().Name.Contains("IE_TimeStop"))
            {
                __result = null;
                return false;
            }
            if (__instance is FloatingOrigin)
            {
                // MoveOrigin runs to its WaitForFixedUpdate here (RebaseInProgress set); Run resumes it after the next physics step.
                __result = null;
                if (routine.MoveNext()) _rebase = routine;
                return false;
            }
            // IE_BuyMax yields WaitForFixedUpdate (build 25630570): one level per physics step, from the touch's step on. A tree
            // segment's IE_Grow (its ground off while its trunks grow, its box falling in) yields WaitForEndOfFrame: once a
            // modelled frame. A zone activator's ZoneLoader.IE_Load yields null twice, then loads the zone (_LoadZone).
            if (__instance is PlatformMover || __instance is upgradeBox && routine.GetType().Name.Contains("IE_BuyMax")
                || __instance is TreeSegmentActivationComponent || __instance is ZoneLoader)
            {
                // Unity refuses a coroutine on an object off in the hierarchy (it logs an error and runs none of it).
                if (!__instance.gameObject.activeInHierarchy)
                {
                    __result = null;
                    return false;
                }
                // A zip mover's coroutines are gameplay (MakeIntangible's 0.15 s off the Ground layer, PlayerExit's contact
                // clear, the move itself once unlocked): they step on the run's ticks, as enginesim's Scheduler does.
                __result = null;
                // Its first step runs now: outside a resume that is the run's physics step (a buy's trigger callback), whose
                // Time.deltaTime is the fixed step (IE_Grow's first trunk step reads it), as the engine's Time.deltaTime there.
                float? deltaTime = _modelDeltaTime;
                _modelDeltaTime = deltaTime ?? Time.fixedDeltaTime;
                try { if (routine.MoveNext()) Park(new Routine { target = __instance, body = routine }, routine.Current); }
                finally { _modelDeltaTime = deltaTime; }
                return false;
            }
            return true;
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
                case null:
                    // After the Updates of a later rendered frame than this one (_frame), never this one's: a load started in a
                    // tick's physics step loads in its third frame (k = 2, a 1x probe).
                    r.wait = Wait.Frame;
                    r.due = _frame;
                    break;
                default:
                    TasPlugin.Log?.LogWarning("Turbo: " + r.target.GetType().Name + " coroutine yields " + yielded.GetType().Name + "; Unity resumes it.");
                    r.target.StartCoroutine(Continue(r.body, yielded));
                    return;
            }
            Routines.Add(r);
        }

        /// <param name="frameDeltaTime">An end-of-frame wait's frame's Time.deltaTime (0 in a freeze's or pause's repeats).</param>
        static void Resume(Wait wait, float? frameDeltaTime = null)
        {
            if (Routines.Count == 0) return;
            // Unity stops a coroutine when its object goes off (a buy-max box deactivated, a zone unloaded: its zip movers mid-move,
            // a tree growing): one parked on an object off in the hierarchy never resumes.
            Routines.RemoveAll(r => r.target == null || !r.target.gameObject.activeInHierarchy);
            Routine[] parked = Routines.ToArray();
            // As the game's player loop: a WaitForFixedUpdate resumes in the physics step (deltaTime the fixed step), the
            // others in the rendered frame that crosses the tick (a zip mover's first tween step after its WaitForSeconds).
            // Seconds waits resume here before the tick, in the frame that will cross it.
            long ticks = wait == Wait.Seconds ? MacroEngine.FrameGrid.NextTicks(TasPlugin.GridTicks) : TasPlugin.GridTicks;
            _modelDeltaTime = frameDeltaTime ?? (wait == Wait.FixedUpdate ? Time.fixedDeltaTime : MacroEngine.FrameGrid.Playback.Step(ticks, 0));
            try
            {
                foreach (Routine r in parked)
                {
                    if (r.wait != wait || wait == Wait.Seconds && r.due > _tick || wait == Wait.Frame && r.due >= _frame) continue;
                    Routines.Remove(r);
                    // One an earlier routine's step switched off (a zone load's) is stopped.
                    if (r.target == null || !r.target.gameObject.activeInHierarchy) continue;
                    if (r.body.MoveNext()) Park(r, r.body.Current);
                }
            }
            finally { _modelDeltaTime = null; }
        }

        // Time.deltaTime inside a parked coroutine's step or a modelled frame's AtomColliderSystem.Update: turbo runs them from
        // its own loop in one Unity frame, whose real deltaTime is that frame's, not the step the game's player loop would run
        // them in (a held frame's is 0). The enginesim host does the same (Scheduler.Resume, Time.resumeDeltaTime).
        static float? _modelDeltaTime;

        static float ModelDeltaTime() { return _modelDeltaTime ?? Time.deltaTime; }

        static IEnumerable<CodeInstruction> UseModelDeltaTime(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo deltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo replacement = AccessTools.Method(typeof(TurboRunner), nameof(ModelDeltaTime));
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
            return Routines.ConvertAll(r => (r.target, (IEnumerator)CloneObject(r.body), r.yielded, r.wait, r.wait == Wait.Frame ? r.due - _frame : r.due - next)).ToArray();
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
            /// <summary>Behaviours whose Start a run drew that Unity hasn't run (<see cref="StartPass"/>).</summary>
            public Behaviour[] startsModelled;
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
            /// <summary>A zone load was under way (<see cref="_zoneLoadPending"/>).</summary>
            public bool zoneLoadPending;
            /// <summary>For the trace start: the watched behaviours whose Start hasn't run (<see cref="NeverStarted"/>).</summary>
            public Behaviour[] neverStarted;
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
            /// <summary>
            /// AtomColliderSystem's state (its atoms per tier, the sprite pool, crafting, waiting atoms, the tier counter) and
            /// its children then (an atom sprite instantiated after goes back to the pool on a restore).
            /// </summary>
            public (AtomColliderSystem collider, object[] fields, HashSet<Transform> children) atoms;
            /// <summary>globalStats' atom counts and NP rates (<see cref="AtomStats"/>).</summary>
            public double[] atomStats;
            /// <summary>The grid: ticks from the snapshot's next tick to the next callAddNewAtom (-1: none; null: Unity's, its start unseen).</summary>
            public int? atomDue;
            /// <summary>
            /// For the trace start: seconds until the next callAddNewAtom, null if none is pending. Read from Unity's grid (with the
            /// raw game times it came from: the grid's start, its last call, Time.time), or a quarter tick past turbo's due tick
            /// (floor(dueIn / fixedDeltaTime) is that tick; whole ticks don't all survive the float round trip).
            /// </summary>
            public float? atomGridDueIn, atomEnabledAt, atomLastCall, atomTimeNow;
            /// <summary>Whether the machine's PlayerPrefs hold "OpenA2Door", which the atom box's first buy reads (upgradeBox.cs:644)
            /// and every atom buy writes (:648); the trace start's, and a restore puts it back (SavestateService.SetAtomDoorKey).</summary>
            public bool openA2Door;
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
            /// <summary>
            /// The upgrade trees (TreeController's grown segments and audio cooldown) and what IE_Grow moves of each segment: its
            /// trunks' and box's local pose and its ground collider (the objects' on/off is in <see cref="objects"/>).
            /// </summary>
            public (TreeController tree, object[] fields)[] trees;
            public (Transform part, Vector3 local, Vector3 scale)[] treeParts;
            public (Collider2D ground, bool enabled)[] treeGrounds;
            /// <summary>ZoneLoader.activeZone (the zone roots' on/off is in <see cref="objects"/>).</summary>
            public int activeZone;
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
            var treeParts = new List<(Transform, Vector3, Vector3)>();
            var treeGrounds = new List<(Collider2D, bool)>();
            foreach (TreeSegmentActivationComponent segment in UnityEngine.Object.FindObjectsByType<TreeSegmentActivationComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                foreach (GameObject part in (SegmentTrunks(segment) ?? new GameObject[0]).Append(segment.box))
                    if (part != null) treeParts.Add((part.transform, part.transform.localPosition, part.transform.localScale));
                Collider2D ground = SegmentLand(segment) != null ? SegmentLand(segment).GetComponent<Collider2D>() : null;
                if (ground != null) treeGrounds.Add((ground, ground.enabled));
            }
            return new Scene {
                objects = all.Where(t => t.gameObject.scene == scene).Select(t => (t.gameObject, t.gameObject.activeSelf)).ToArray(),
                trees = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<TreeController>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    t => (t, CaptureFields(t))),
                treeParts = treeParts.ToArray(), treeGrounds = treeGrounds.ToArray(),
                boxes = Array.ConvertAll(boxes, b => (b, b.gameObject.activeSelf, b.enabled)),
                a1State = Singleton<globalStats>.Instance.currentA1State,
                breakers = breakers.ToArray(), blocks = blocks.ToArray(), blueActive = swapper != null && swapper.isBlueActive,
                activeZone = ZoneLoaderNow()?.activeZone ?? 0 };
        }

        static float LightRadius(Behaviour light, string property)
        {
            return light == null ? 0f : (float)light.GetType().GetProperty(property).GetValue(light);
        }

        static ZoneLoader ZoneLoaderNow() { return UnityEngine.Object.FindAnyObjectByType<ZoneLoader>(FindObjectsInactive.Include); }

        static void RestoreScene(Scene s)
        {
            // A load Unity runs would turn zones on and off after the snapshot's are put back; a run's own is a parked routine.
            ZoneLoader loader = ZoneLoaderNow();
            if (loader != null)
            {
                loader.StopAllCoroutines();
                loader.activeZone = s.activeZone;
            }
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
            // A growth Unity runs (1x, or one a run handed back) would go on changing what the snapshot puts back; turbo's own
            // are the snapshot's parked routines (RestoreRoutines).
            foreach (TreeSegmentActivationComponent segment in UnityEngine.Object.FindObjectsByType<TreeSegmentActivationComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                segment.StopAllCoroutines();
            if (s.trees == null) return;
            foreach (var (tree, fields) in s.trees) if (tree != null) RestoreFields(tree, fields);
            foreach (var (part, local, scale) in s.treeParts)
                if (part != null && !(part.localPosition.Equals(local) && part.localScale.Equals(scale))) { part.localPosition = local; part.localScale = scale; }
            foreach (var (ground, enabled) in s.treeGrounds) if (ground != null) ground.enabled = enabled;
        }

        static readonly AccessTools.FieldRef<TreeSegmentActivationComponent, GameObject[]> SegmentTrunks =
            AccessTools.FieldRefAccess<TreeSegmentActivationComponent, GameObject[]>("trunks");
        static readonly AccessTools.FieldRef<TreeSegmentActivationComponent, GameObject> SegmentLand =
            AccessTools.FieldRefAccess<TreeSegmentActivationComponent, GameObject>("landPart");

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

        /// <summary>globalStats' atom counts and NP rates: what AddNewAtom, FinishCrafting and ClearAtoms write
        /// (AtomColliderSystem.cs:202-209, 300-325, 153-156), the course finishes' NP comp boost (courseScript.cs:307, 447), the
        /// clone dust multiplier a clone dust payout recomputes (RecalcCloneDustMult, clonesScript.cs:282, courseScript.cs:407)
        /// and the RP cap its boxes set (UpdateRpCap, upgradeBox.cs:712-716): the engine's snapshots hold all of them.</summary>
        static double[] AtomStats(globalStats g)
        {
            return new[] { g.effectiveAtomCount, g.grossAtomCount, g.AtomCountInTermsOfWhiteAtoms, g.NPrewardPerTick, g.NPrewardCap, g.EffectOfNpCompBoost,
                g.CloneDustGpCdMult, g.rpCap };
        }

        static void RestoreAtomStats(globalStats g, double[] v)
        {
            g.effectiveAtomCount = v[0];
            g.grossAtomCount = v[1];
            g.AtomCountInTermsOfWhiteAtoms = v[2];
            g.NPrewardPerTick = v[3];
            g.NPrewardCap = v[4];
            g.EffectOfNpCompBoost = v[5];
            g.CloneDustGpCdMult = v[6];
            g.rpCap = v[7];
        }

        /// <summary>The collider's per-tier lists of lists (contents, contentsTargetLocs) copied to the inner list: EconomyFields' copy is one level deep.</summary>
        static object[] DeepAtomLists(object[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is List<List<GameObject>> sprites) values[i] = sprites.ConvertAll(l => new List<GameObject>(l));
                else if (values[i] is List<List<Vector2>> targets) values[i] = targets.ConvertAll(l => new List<Vector2>(l));
            }
            return values;
        }

        static readonly AccessTools.FieldRef<AtomColliderSystem, Stack<GameObject>> AtomPool = AccessTools.FieldRefAccess<AtomColliderSystem, Stack<GameObject>>("inactiveGameObjects");

        static Economy CaptureEconomy(int next)
        {
            upgradeBox[] boxes = UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            localUpgrades[] locals = UnityEngine.Object.FindObjectsByType<localUpgrades>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            clonesScript[] clones = UnityEngine.Object.FindObjectsByType<clonesScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            AtomColliderSystem collider = FindAtomCollider();
            globalStats stats = Singleton<globalStats>.Instance;
            int? atomDue = AtomGridTicks(collider, next, out float? dueIn);
            bool raw = !Active && Time.time != _atomHeldAt;
            return new Economy {
                currencies = globalStats.currencyLookup == null ? null : new Dictionary<globalStats.Currencies, double>(globalStats.currencyLookup),
                globalUpgrades = globalStats.globalUpgradeDict == null ? null : new Dictionary<globalStats.globalUpgradeSet, double>(globalStats.globalUpgradeDict),
                boxes = Array.ConvertAll(boxes, b => (b, CaptureFields(b))),
                scene = CaptureScene(boxes),
                paths = Array.ConvertAll(clones, c => (c, c.clonePath, c.cloneSprites, c.cloneScales)),
                atoms = collider == null ? default : (collider, DeepAtomLists(CaptureFields(collider)), new HashSet<Transform>(collider.transform.Cast<Transform>())),
                atomStats = stats == null ? null : AtomStats(stats),
                atomDue = atomDue,
                atomGridDueIn = raw ? dueIn : atomDue >= 0 ? (atomDue.Value + 0.25f) * Time.fixedDeltaTime : (float?)null,
                atomEnabledAt = raw && !float.IsNegativeInfinity(_atomEnabledAt) ? _atomEnabledAt : (float?)null,
                atomLastCall = raw && !float.IsNegativeInfinity(_atomLastCall) ? _atomLastCall : (float?)null,
                atomTimeNow = raw ? Time.time : (float?)null,
                openA2Door = PlayerPrefs.HasKey(SavestateService.AtomDoorKey),
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
            SavestateService.SetAtomDoorKey(e.openA2Door);
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
            RestoreAtoms(e);
        }

        /// <summary>
        /// The collider's state, globalStats' atom counts and the grid as the snapshot had them. The snapshot's sprites are on
        /// or off as it had them (<see cref="Scene.objects"/>); one AddNewAtom instantiated since goes off into the pool, so
        /// the sprites stay as many as the most atoms held (presentation: the counts are the lists').
        /// </summary>
        static void RestoreAtoms(Economy e)
        {
            var (collider, fields, children) = e.atoms;
            if (collider != null)
            {
                RestoreFields(collider, DeepAtomLists((object[])fields.Clone()));
                Stack<GameObject> pool = AtomPool(collider);
                var now = new List<Transform>();
                foreach (Transform child in collider.transform) now.Add(child);
                foreach (Transform child in now)
                {
                    if (children.Contains(child)) continue;
                    child.gameObject.SetActive(false);
                    if (pool != null && !pool.Contains(child.gameObject)) pool.Push(child.gameObject);
                }
            }
            globalStats stats = Singleton<globalStats>.Instance;
            if (stats != null && e.atomStats != null) RestoreAtomStats(stats, e.atomStats);
            HandAtomGrid(e.atomDue);
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
                pending = pending, startsModelled = _startsModelled.ToArray(), economy = economy ? CaptureEconomy(next) : null, vman = Vman.Capture(),
                camera = movement.cam != null ? FieldState.Capture(movement.cam) : null,
                origin = origin != null ? origin.currentOrigin : Vector3.zero, rebasing = _rebase != null,
                stepFrom = Active ? _stepFrom : body.position, stepTo = Active ? _stepTo : body.position,
                movers = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<PlatformMover>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    m => (m, FieldState.Capture(m), m.gameObject.layer, m.transform.localPosition, MoverPose(m))),
                orbs = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    o => (o, FieldState.Capture(o))),
                spikes = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    k => (k, SpikeCounter(k), SpikeKilled(k))),
                routines = CaptureRoutines(next), zoneLoadPending = _zoneLoadPending, neverStarted = economy ? NeverStarted().ToArray() : null };
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
            _startsModelled = s.startsModelled == null ? new HashSet<Behaviour>() : new HashSet<Behaviour>(s.startsModelled);
            if (s.canonical is Vector3 reference) PutCanonicalOrigin(reference); else PutOrigin(s.origin);
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
            // After the economy: a zip mover it turns back on ran PlatformMover.OnEnable (JumpToState and its coroutine).
            RestoreMovers(s);
            _zoneLoadPending = s.zoneLoadPending && Routines.Any(r => r.target is ZoneLoader && r.wait == Wait.Frame);
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
                SavestateService.SnapCamera(s.position, false);
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
            RecordPristineStatics();
        }

        /// <summary>Every static body under a recorded root at its local position as loaded (<see cref="RecordPristineRoots"/>).</summary>
        static readonly Dictionary<Transform, Vector3> PristineStatics = new Dictionary<Transform, Vector3>();

        /// <summary>
        /// A static body's transform drifts by a float step now and then (the moss tilemaps under biggerGrid: local x -16184,
        /// -16183.998, -16183.9961 as the session ran), though nothing in the game moves it: the start of a later run then
        /// differs by that step, and a bonus13 route that grazed the moss finished from one start and hit it from another.
        /// Recorded with the roots, put back with them (<see cref="PutCanonicalOrigin"/>).
        /// </summary>
        static void RecordPristineStatics()
        {
            PristineStatics.Clear();
            foreach (Transform root in PristineRoots.Keys)
                foreach (Rigidbody2D body in root.GetComponentsInChildren<Rigidbody2D>(true))
                    if (body.bodyType == RigidbodyType2D.Static && body.transform != root) PristineStatics[body.transform] = body.transform.localPosition;
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
                RecordPristineStatics();
            }
            Vector3 target = -reference;
            PutOrigin(target);
            foreach (KeyValuePair<Transform, Vector3> root in PristineRoots)
            {
                Vector3 canonical = root.Value - reference;
                if ((root.Key.position - canonical).sqrMagnitude < 1f) root.Key.position = canonical;
            }
            foreach (KeyValuePair<Transform, Vector3> body in PristineStatics)
                if (body.Key != null && body.Key.localPosition != body.Value && (body.Key.localPosition - body.Value).sqrMagnitude < 1f)
                    body.Key.localPosition = body.Value;
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

        /// <summary>
        /// A rendered frame's start, as Unity's Physics2DEarlyUpdate syncs it before the frame's FixedUpdate and Update: what
        /// scripts moved since (a tree box falling onto its spot, IE_Grow at each frame's end) is where its transform is for the
        /// frame's queries (the engine's PhysicsWorld.FrameStart). Turbo runs its frames inside one Unity frame, so it syncs
        /// them itself; the player's transform holds turbo's blend, never the body's pose, so it is put on the body first.
        /// </summary>
        static void FrameStart(Transform player, Rigidbody2D body)
        {
            Place(player, body.position);
            Physics2D.SyncTransforms();
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
            // Off ones too: a zone the run loads turns its orbs on.
            JiggleDropScript[] orbs = UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            // Steps in 1x's Update order (instance id, lowest first; a probe: Course 2's clones before Course 3's in one frame), as
            // the engine does: two courses spawning in one frame take their fast/big rolls in that order.
            Array.Sort(clones, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            float dt = Time.fixedDeltaTime;
            FloatingNumberScript numbers = Singleton<FloatingNumberScript>.Instance;
            bool spawnNumbers = numbers != null && numbers.ActuallySpawnText;
            Pending.Clear();
            _frame = 0;
            _atomNow = false;
            if (resume != null) Pending.AddRange(resume.pending);
            if (resume != null) RestoreRoutines(resume.routines);
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            Transform player = movement.transform;
            MacroEngine.FrameGrid frames = MacroEngine.FrameGrid.Playback;
            _stepFrom = resume != null ? resume.stepFrom : body.position;
            _stepTo = resume != null ? resume.stepTo : body.position;
            // globalStats.FixedUpdate pays NP (DoConstantCurrency, globalStats.cs:1100-1124) before every other FixedUpdate:
            // Singleton<T>'s DefaultExecutionOrder(-50) (the engine's Economy.FixedStep, Simulation.Run).
            globalStats stats = Singleton<globalStats>.Instance;
            _atomCollider = FindAtomCollider();
            int? atomStart = AtomGridTicks(_atomCollider, 0, out _);
            _atomDue = atomStart is int startDue && startDue >= 0 ? Math.Max(0, startDue - 1) : -1;
            _atomUnseen = atomStart == null;
            int atomRepeat = Math.Max(1, Mathf.FloorToInt(AtomRepeat / dt));
            CollectStartWaiting();
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
                    _frame++;
                    FrameStart(player, body);
                    // The tick's frame starts with the Start of what the last frame's end woke (IE_Grow's box), before its
                    // FixedUpdates, as Unity runs it (README ### Rules, exception 12); what the step wakes starts at the frame's pass.
                    if (StartWaiting.Count > 0) StartPass();
                    for (int i = 0; i < Pending.Count; i++)
                    {
                        if (Pending[i].due > _tick) continue;
                        var (_, target, method) = Pending[i];
                        Pending.RemoveAt(i--);
                        if (target != null) CallInvoked(target, method);
                    }
                    Resume(Wait.Seconds);
                    if (stats != null && stats.isActiveAndEnabled) _statsFixedUpdate(stats);
                    // FixedUpdate reads the last rendered frame's interpolation blend; its writes move the body from there.
                    Vector3 lag = Place(player, Vector2.Lerp(_stepFrom, _stepTo, frames.Frames(TasPlugin.GridTicks).LastAlpha));
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
                    long ticks = TasPlugin.GridTicks;
                    float[] alphas = frames.Frames(ticks).Alphas;
                    int frozen = _freezeFor >= 0f ? frames.FrozenFrames(ticks, _freezeFor) : 0;
                    // clonesScript.Update and AtomColliderSystem.Update run in every frame too, on that frame's Time.deltaTime (0 in a
                    // freeze's or pause's repeats), then the frame's WaitForEndOfFrame resumes (a tree's IE_Grow), as the engine's
                    // Simulation.InterpolatedFrames.
                    for (int k = 0; k < alphas.Length; k++)
                    {
                        int repeats = MacroEngine.FrameRepeats(k, alphas.Length, frozen, _pauseFrames, _pauseAfter);
                        // The repeats are frames the run stands still in: its live time (MacroEngine.HeldFrames), counted as the
                        // offline engine and 1x count them.
                        TasPlugin.CountHeldFrames(repeats);
                        for (int repeat = repeats; repeat >= 0; repeat--)
                        {
                            if (k > 0 || repeat < repeats)
                            {
                                _frame++;
                                FrameStart(player, body);
                            }
                            // Every frame's Start pass, before its Updates, as the engine's (README ### Rules, exception 12).
                            if (StartWaiting.Count > 0) StartPass();
                            Vector3 at = Place(player, Vector2.Lerp(_stepFrom, _stepTo, alphas[k]));
                            _update(movement);
                            if (player.position != at)
                            {
                                // The tick's last frame: the write reaches the body at the next step's sync, as at 1x, so the
                                // next FixedUpdate's squish check (Collider2D.Distance) still sees the body where it was.
                                if (k < alphas.Length - 1 || repeat > 0) Physics2D.SyncTransforms();
                                _stepFrom = _stepTo = player.position;
                            }
                            float frameDt = repeat == repeats ? frames.Step(ticks, k) : 0f;
                            foreach (clonesScript c in clones)
                                if (c != null && c.isActiveAndEnabled) StepClones(c, frameDt);
                            if (_atomCollider != null && _atomCollider.isActiveAndEnabled)
                            {
                                _modelDeltaTime = frameDt;
                                try { _atomUpdate(_atomCollider); }
                                finally { _modelDeltaTime = null; }
                            }
                            // The grid's call in the first rendered frame after the step of the tick before its due one, after
                            // the frame's Updates, as Unity runs it (the engine's Economy.GridFrame).
                            if (k == 0 && repeat == repeats && _atomDue >= 0 && _atomDue <= _tick)
                            {
                                _atomDue += atomRepeat;
                                if (_atomCollider != null) CallInvoked(_atomCollider, AtomCall);
                            }
                            // The frame's yield-null resumes after its Updates (a zone load, ZoneLoader.IE_Load), then the grid's
                            // first call when the load's OnEnable started it: InvokeRepeating's 0 delay runs in that same pass.
                            Resume(Wait.Frame, frameDt);
                            if (_atomNow)
                            {
                                _atomNow = false;
                                if (_atomCollider != null) CallInvoked(_atomCollider, AtomCall);
                            }
                            Resume(Wait.EndOfFrame, frameDt);
                        }
                    }
                    Place(player, _stepTo);
                    OriginFrame();
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
                    foreach (Routine r in Routines)
                {
                    if (r.target == null) continue;
                    object wait = r.wait == Wait.Seconds ? new WaitForSeconds(Mathf.Max(0, r.due - _tick) * Time.fixedDeltaTime) : r.yielded;
                    r.target.StartCoroutine(Continue(r.body, wait));
                }
                Routines.Clear();
                HandAtomGrid(_atomDue >= 0 ? _atomDue + 1 - _tick : _atomUnseen ? (int?)null : -1);
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
