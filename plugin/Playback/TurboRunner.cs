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
        static Action<Movement> _fixedUpdate, _update;
        static Action<courseScript> _courseFixedUpdate;
        static Action<VmanScript> _vmanFixedUpdate;
        static Action<JiggleDropScript> _orbFixedUpdate;
        static Action<FloatingOrigin> _originLateUpdate;
        static Action<globalStats> _statsFixedUpdate;
        static Action<AtomColliderSystem> _atomUpdate;
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

        // Windows flags a window ignoring its message queue for 5 s Not Responding; a PM_NOREMOVE peek keeps it alive.
        internal static void KeepWindowAlive(System.Diagnostics.Stopwatch clock, ref long next)
        {
            if (clock.ElapsedMilliseconds < next) return;
            next = clock.ElapsedMilliseconds + KeepAliveMs;
            if (Application.platform == RuntimePlatform.WindowsPlayer) PeekMessage(out _, IntPtr.Zero, 0, 0, PeekNoRemove | PeekNoYield);
        }

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
        static readonly List<(int due, TeleporterScript target)> Polls = new List<(int, TeleporterScript)>();
        const string PollCall = "manualUpdate";
        static readonly List<Routine> Routines = new List<Routine>();
        static readonly AccessTools.FieldRef<WaitForSeconds, float> WaitSeconds = AccessTools.FieldRefAccess<WaitForSeconds, float>("m_Seconds");
        static readonly Func<object, object> CloneObject = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));
        static IEnumerator _rebase;
        static int _tick;
        static int _frame;
        static Vector2 _stepFrom, _stepTo;
        static float _freezeFor = -1f;
        static int _pauseFrames, _pauseAfter;

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
            PatchBlendReads(harmony);
        }

        static readonly Type[] BlendReaders = { typeof(Movement), typeof(courseScript), typeof(TeleporterScript) };

        static void PatchBlendReads(Harmony harmony)
        {
            MethodInfo getPosition = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(TurboRunner), nameof(ReadBlend)));
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            int patched = 0;
            foreach (Type reader in BlendReaders)
                foreach (Type type in new[] { reader }.Concat(reader.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
                    foreach (MethodInfo method in type.GetMethods(declared))
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                        if (!PatchProcessor.ReadMethodBody(method).Any(op => Equals(op.Value, getPosition))) continue;
                        harmony.Patch(method, transpiler: transpiler);
                        patched++;
                    }
            if (patched == 0) TasPlugin.Log?.LogWarning("Turbo: no player position reads found to patch; turbo frames read the body's pose.");
        }

        static IEnumerable<CodeInstruction> ReadBlend(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getPosition = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
            MethodInfo read = AccessTools.Method(typeof(TurboRunner), nameof(BlendRead));
            foreach (CodeInstruction instruction in instructions)
                yield return instruction.Calls(getPosition) ? new CodeInstruction(System.Reflection.Emit.OpCodes.Call, read).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction) : instruction;
        }

        // exception 14: turbo frames read the player's blend via BlendRead; a transform write moves Box2D's sweep centre.
        static Transform _blendOf, _readBack;
        static Vector3 _blendBase, _blendAt;

        public static Vector3 BlendRead(Transform t)
        {
            Vector3 p = t.position;
            return ReferenceEquals(t, _blendOf) && Same(p, _blendBase) ? _blendAt : p;
        }

        static void ShowBlend(Transform player, Vector2 blend)
        {
            _blendOf = null;
            _blendBase = player.position;
            _blendAt = ReadBack(player, blend);
            _blendOf = player;
        }

        static void EndBlend() { _blendOf = null; }

        static Vector3 ReadBack(Transform player, Vector2 at)
        {
            if (_readBack == null) _readBack = new GameObject("TurboReadBack") { hideFlags = HideFlags.HideInHierarchy }.transform;
            if (_readBack.parent != player.parent) _readBack.SetParent(player.parent, false);
            _readBack.position = new Vector3(at.x, at.y, player.position.z);
            return _readBack.position;
        }

        // RULES.md exception 12: model Unity's Start draws at their 1x frame; a run is one Unity frame and would draw after it.
        static readonly Type[] StartDrawers = { typeof(LightFlicker), typeof(RandomAnimSpeedComponent), typeof(JiggleDropScript) };
        static readonly Type[] StartWatched = StartDrawers.Concat(new[] { typeof(AtomColliderSystem), typeof(upgradeBox) }).ToArray();

        internal static IEnumerable<Behaviour> NeverStarted()
        {
            foreach (Type type in StartWatched)
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (o is Behaviour b && !StartsRun.Contains(b) && !_startsModelled.Contains(b)) yield return b;
        }
        static readonly AccessTools.FieldRef<RandomAnimSpeedComponent, bool> AnimIsBroken = AccessTools.FieldRefAccess<RandomAnimSpeedComponent, bool>("isBroken");
        static HashSet<Behaviour> _startsModelled = new HashSet<Behaviour>();
        static readonly HashSet<Behaviour> StartsRun = new HashSet<Behaviour>();
        static readonly List<Behaviour> StartWaiting = new List<Behaviour>();

        static bool _modellingStart;

        static readonly AccessTools.FieldRef<upgradeBox, PlatformMover> BoxDoor = AccessTools.FieldRefAccess<upgradeBox, PlatformMover>("doorToOpen");
        static readonly AccessTools.FieldRef<PlatformMover, int> MoverStop = AccessTools.FieldRefAccess<PlatformMover, int>("indexInPositions");

        // Real Start of a modelled behaviour: restore RNG and box fields, no double apply (exception 12; upgradeBox.cs:260).
        static void BeforeDrawingStart(Behaviour __instance, out (UnityEngine.Random.State rng, object[] box, PlatformMover door, int stop, Vector3 local)? __state)
        {
            __state = null;
            if (_modellingStart) return;
            StartsRun.Add(__instance);
            if (!_startsModelled.Remove(__instance)) return;
            upgradeBox box = __instance as upgradeBox;
            PlatformMover door = box != null ? BoxDoor(box) : null;
            __state = (UnityEngine.Random.state, box != null ? CaptureFields(box) : null, door,
                door != null ? MoverStop(door) : 0, door != null ? door.transform.localPosition : Vector3.zero);
        }

        static void AfterDrawingStart(Behaviour __instance, (UnityEngine.Random.State rng, object[] box, PlatformMover door, int stop, Vector3 local)? __state)
        {
            if (__state == null) return;
            var (drawn, box, door, stop, local) = __state.Value;
            if (box != null) RestoreFields(__instance, box);
            if (door != null && (MoverStop(door) != stop || door.transform.localPosition != local))
            {
                MoverStop(door) = stop;
                door.transform.localPosition = local;
            }
            UnityEngine.Random.state = drawn;
        }

        static void CollectStartWaiting()
        {
            StartWaiting.Clear();
            StartWaiting.AddRange(NeverStarted());
        }

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
                    if (WokenStarts.Unmodeled(box) is string why) { TasPlugin.AbortRun(why); continue; }
                    _modellingStart = true;
                    try { WokenStarts.Start.Invoke(box, null); }
                    finally { _modellingStart = false; }
                }
                else if (b is AtomColliderSystem) Singleton<globalStats>.Instance?.RecalcNpRewardPerTick();
                else
                {
                    int draws = b is RandomAnimSpeedComponent anim ? (AnimIsBroken(anim) ? 3 : 2) : 1;
                    for (int d = 0; d < draws; d++) _ = UnityEngine.Random.value;
                }
                _startsModelled.Add(b);
            }
        }

        // Skip afterimages and HandleMovement in turbo: held deltaTime drained the 21-sprite pool and threw Stack empty.
        static bool BeforeHandleAfterimages() { return !Active; }

        static void BeforeTimeStop(float duration)
        {
            if (Active && (_freezeFor < 0f || duration < _freezeFor)) _freezeFor = duration;
        }

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

        // Unity has no API for an Invoke's time left, so keep the grid's start and last-call game times.
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
        const float AtomRepeat = 0.4f;

        static AtomColliderSystem FindAtomCollider()
        {
            return UnityEngine.Object.FindAnyObjectByType<AtomColliderSystem>(FindObjectsInactive.Include);
        }

        static float? AtomGridDueIn(AtomColliderSystem collider)
        {
            if (collider == null || float.IsNegativeInfinity(_atomEnabledAt) || !collider.IsInvoking(AtomCall)) return null;
            float next = _atomLastCall >= _atomEnabledAt ? _atomLastCall + AtomRepeat : _atomEnabledAt;
            return Mathf.Max(0f, next - Time.time);
        }

        static int _atomDue = -1;
        static bool _atomNow;
        static int AtomRepeatTicks { get { return Math.Max(1, Mathf.FloorToInt(AtomRepeat / Time.fixedDeltaTime)); } }
        static bool _atomUnseen;
        static AtomColliderSystem _atomCollider;
        static int? _atomHeldDue;
        static float _atomHeldAt = float.NaN;

        static int? AtomGridTicks(AtomColliderSystem collider, int next, out float? dueIn)
        {
            dueIn = null;
            if (Active) return _atomDue >= 0 ? _atomDue + 1 - next : _atomUnseen ? (int?)null : -1;
            if (Time.time == _atomHeldAt) return _atomHeldDue;
            if (collider == null || !collider.IsInvoking(AtomCall)) return -1;
            dueIn = AtomGridDueIn(collider);
            return dueIn is float s ? Mathf.FloorToInt(s / Time.fixedDeltaTime) : (int?)null;
        }

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

        static bool BeforeInvokeRepeating(MonoBehaviour __instance, string methodName, float time)
        {
            if (Active && __instance is TeleporterScript teleporter && methodName == PollCall)
            {
                Polls.RemoveAll(p => p.target == teleporter);
                Polls.Add((_tick + Mathf.FloorToInt(time / Time.fixedDeltaTime), teleporter));
                return false;
            }
            if (!Active || !(__instance is AtomColliderSystem collider) || methodName != AtomCall) return true;
            _atomCollider = collider;
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
            if (methodName == PollCall) Polls.RemoveAll(p => p.target == __instance);
            return false;
        }

        internal static bool DropTimeStops;

        static Vector2? _handoff;

        // Place the player at the last blend only for 1x's next FixedUpdate; a transform write is synced into the body for good.
        internal static void ShowLastFrame(Movement movement)
        {
            _handoff = Vector2.Lerp(_stepFrom, _stepTo, MacroEngine.FrameGrid.Playback.Frames(TasPlugin.GridTicks).LastAlpha);
        }

        internal static void BeforeHandoffTick(Movement movement)
        {
            if (Active || _handoff == null) return;
            ShowBlend(movement.transform, _handoff.Value);
        }

        internal static void AfterHandoffTick(Movement movement)
        {
            if (Active || _handoff == null) return;
            _handoff = null;
            EndBlend();
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
                __result = null;
                if (routine.MoveNext()) _rebase = routine;
                return false;
            }
            // IE_BuyMax yields WaitForFixedUpdate; IE_Grow yields WaitForEndOfFrame; ZoneLoader.IE_Load yields null twice, then loads.
            if (__instance is PlatformMover || __instance is upgradeBox && routine.GetType().Name.Contains("IE_BuyMax")
                || __instance is TreeSegmentActivationComponent || __instance is ZoneLoader)
            {
                if (!__instance.gameObject.activeInHierarchy)
                {
                    __result = null;
                    return false;
                }
                __result = null;
                float? deltaTime = _modelDeltaTime;
                _modelDeltaTime = deltaTime ?? Time.fixedDeltaTime;
                try { if (routine.MoveNext()) Park(new Routine { target = __instance, body = routine }, routine.Current); }
                finally { _modelDeltaTime = deltaTime; }
                return false;
            }
            return true;
        }

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
                    r.wait = Wait.Seconds;
                    r.due = _tick + Mathf.FloorToInt(WaitSeconds(seconds) / Time.fixedDeltaTime) + 1;
                    break;
                case WaitForFixedUpdate _: r.wait = Wait.FixedUpdate; break;
                case WaitForEndOfFrame _: r.wait = Wait.EndOfFrame; break;
                case null:
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

        static void Resume(Wait wait, float? frameDeltaTime = null)
        {
            if (Routines.Count == 0) return;
            Routines.RemoveAll(r => r.target == null || !r.target.gameObject.activeInHierarchy);
            Routine[] parked = Routines.ToArray();
            long ticks = wait == Wait.Seconds ? MacroEngine.FrameGrid.NextTicks(TasPlugin.GridTicks) : TasPlugin.GridTicks;
            _modelDeltaTime = frameDeltaTime ?? (wait == Wait.FixedUpdate ? Time.fixedDeltaTime : MacroEngine.FrameGrid.Playback.Step(ticks, 0));
            try
            {
                foreach (Routine r in parked)
                {
                    if (r.wait != wait || wait == Wait.Seconds && r.due > _tick || wait == Wait.Frame && r.due >= _frame) continue;
                    Routines.Remove(r);
                    if (r.target == null || !r.target.gameObject.activeInHierarchy) continue;
                    if (r.body.MoveNext()) Park(r, r.body.Current);
                }
            }
            finally { _modelDeltaTime = null; }
        }

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

        static IEnumerator AfterNextFixedStep(IEnumerator routine)
        {
            yield return new WaitForFixedUpdate();
            routine.MoveNext();
        }

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
            public (PlatformMover mover, object[] fields, int layer, Vector3 local, (Vector2 position, Vector2 velocity) body)[] movers;
            public (JiggleDropScript orb, object[] fields)[] orbs;
            public JiggleDropScript[] orbsSeen;
            public (spikeScript spike, int counter, bool killed)[] spikes;
            public (MonoBehaviour target, IEnumerator body, object yielded, Wait wait, int due)[] routines;
            public float rotation, angularVelocity;
            public (Vector2 offset, Vector2 size, int exclude, bool enabled)[] colliders;
            public (int due, MonoBehaviour target, string method)[] pending;
            public (int due, TeleporterScript target)[] polls;
            public Behaviour[] startsModelled;
            public Economy economy;
            public Vman vman;
            // Restore mutes OnTriggerExit2D, so a camZoneScript's hold on the camera must be restored or it stays on the last zone.
            public object[] camera;
            public Vector3 origin;
            public bool rebasing;
            public Vector3? canonical;
            public Vector2 stepFrom, stepTo;
            public bool zoneLoadPending;
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
            public (clonesScript clones, Vector2[] path, Sprite[] sprites, Vector2[] scales)[] paths;
            public List<int> visitedTeleporters;
            public Scene scene;
            public (AtomColliderSystem collider, object[] fields, HashSet<Transform> children) atoms;
            public double[] atomStats;
            public int? atomDue;
            public float? atomGridDueIn, atomEnabledAt, atomLastCall, atomTimeNow;
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
            // Restore objects a run switched on: ObjectBobber.OnEnable draws RNG, so a left-on one shifts every later clone roll.
            public (GameObject obj, bool active)[] objects;
            public (TreeController tree, object[] fields)[] trees;
            public (Transform part, Vector3 local, Vector3 scale)[] treeParts;
            public (Collider2D ground, bool enabled)[] treeGrounds;
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
            colouredBlockSwapper swapper = UnityEngine.Object.FindAnyObjectByType<colouredBlockSwapper>(FindObjectsInactive.Include);
            if (swapper != null) swapper.swapBlocks(s.blueActive);
            foreach (var (collider, enabled) in s.blocks) if (collider != null) collider.enabled = enabled;
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
                pending = pending, polls = Polls.Select(p => (p.due - next, p.target)).ToArray(), startsModelled = _startsModelled.ToArray(), economy = economy ? CaptureEconomy(next) : null, vman = Vman.Capture(),
                camera = movement.cam != null ? FieldState.Capture(movement.cam) : null,
                origin = origin != null ? origin.currentOrigin : Vector3.zero, rebasing = _rebase != null,
                stepFrom = Active ? _stepFrom : body.position, stepTo = Active ? _stepTo : body.position,
                movers = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<PlatformMover>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    m => (m, FieldState.Capture(m), m.gameObject.layer, m.transform.localPosition, MoverPose(m))),
                orbs = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    o => (o, FieldState.Capture(o))),
                orbsSeen = Active ? OrbsSeen.ToArray() : SeenFrom(body.position).ToArray(),
                spikes = Array.ConvertAll(UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    k => (k, SpikeCounter(k), SpikeKilled(k))),
                routines = CaptureRoutines(next), zoneLoadPending = _zoneLoadPending, neverStarted = economy ? NeverStarted().ToArray() : null };
        }

        static readonly AccessTools.FieldRef<spikeScript, int> SpikeCounter = AccessTools.FieldRefAccess<spikeScript, int>("counter");
        static readonly AccessTools.FieldRef<spikeScript, bool> SpikeKilled = AccessTools.FieldRefAccess<spikeScript, bool>("playerKilled");

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

        static void RestoreOrbs(Snapshot s)
        {
            OrbsSeen.Clear();
            OrbsSeen.UnionWith(s.orbsSeen ?? SeenFrom(s.bodyPosition));
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

        static readonly HashSet<JiggleDropScript> OrbsSeen = new HashSet<JiggleDropScript>();

        static IEnumerable<JiggleDropScript> SeenFrom(Vector2 player) =>
            UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(o => o.gameObject.activeInHierarchy && MacroEngine.OrbOnCamera(o.transform.position, player));

        // JiggleDropScript.OnBecameVisible never fires in turbo; an orb coming on camera reads the refill upgrades.
        static void SeeOrbs(JiggleDropScript[] orbs, Vector2 player)
        {
            foreach (JiggleDropScript orb in orbs)
            {
                if (orb == null) continue;
                bool seen = orb.gameObject.activeInHierarchy && MacroEngine.OrbOnCamera(orb.transform.position, player);
                if (seen && OrbsSeen.Add(orb)) OrbState.Invoke(orb, new object[] { false });
                else if (!seen) OrbsSeen.Remove(orb);
            }
        }

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
            if (s.economy != null) RestoreEconomy(s.economy);
            RestoreMovers(s);
            _zoneLoadPending = s.zoneLoadPending && Routines.Any(r => r.target is ZoneLoader && r.wait == Wait.Frame);
            Physics2D.SyncTransforms();
            MuteAround(own, muted);
            SettleContacts(body, muted);
            FieldState.Restore(movement, s.movement);
            foreach (var (course, fields) in s.courses) FieldState.Restore(course, fields);
            RestoreOrbs(s);
            RestoreSpikes(s);
            s.vman?.Restore();
            if (s.camera != null && movement.cam != null)
            {
                FieldState.Restore(movement.cam, s.camera);
                SavestateService.SnapCamera(s.position, false);
            }
            UnityEngine.Random.state = s.random;
            movement.transform.position = s.position;
            body.position = s.bodyPosition;
            body.rotation = s.rotation;
            body.linearVelocity = s.velocity;
            body.angularVelocity = s.angularVelocity;
            Physics2D.SyncTransforms();
        }

        static void RestoreMovers(Snapshot s)
        {
            if (s.movers == null) return;
            foreach (var (mover, fields, layer, local, pose) in s.movers)
            {
                if (mover == null) continue;
                mover.StopAllCoroutines();
                FieldState.Restore(mover, fields);
                if (mover.gameObject.layer != layer) mover.gameObject.layer = layer;
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

        public static void PutOrigin(Vector3 target)
        {
            _rebase = null;
            FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
            if (origin == null) return;
            origin.StopAllCoroutines();
            OriginRebasing(origin) = false;
            if (origin.currentOrigin != target) ShiftOrigin(origin, origin.currentOrigin - target);
        }

        static readonly Dictionary<Transform, Vector3> PristineRoots = new Dictionary<Transform, Vector3>();
        static bool _pristineWarned;

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

        static readonly Dictionary<Transform, Vector3> PristineStatics = new Dictionary<Transform, Vector3>();

        // Static body transforms drift by float steps at times (moss tilemaps); record pristine locals so starts are identical.
        static void RecordPristineStatics()
        {
            PristineStatics.Clear();
            foreach (Transform root in PristineRoots.Keys)
                foreach (Rigidbody2D body in root.GetComponentsInChildren<Rigidbody2D>(true))
                    if (body.bodyType == RigidbodyType2D.Static && body.transform != root) PristineStatics[body.transform] = body.transform.localPosition;
        }

        public static void PutCanonicalOrigin(Vector3 reference)
        {
            FloatingOrigin origin = TasPlugin.CurrentFloatingOrigin();
            if (origin == null) return;
            RecordPristineRoots(origin);
            if (PristineRoots.Count == 0 || PristineRoots.Keys.Any(t => t == null))
            {
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

        static void FrameStart(Transform player, Rigidbody2D body)
        {
            EndBlend();
            Vector2 at = body.position;
            if (Same(player.position, ReadBack(player, at)))
            {
                Physics2D.SyncTransforms();
                return;
            }
            Place(player, at);
            Physics2D.SyncTransforms();
            KeepBody(body, at);
        }

        // Restore the body's exact pose after a transform sync: it reads back rounded through the parent, which 1x never writes.
        static void KeepBody(Rigidbody2D body, Vector2 at)
        {
            if (!Same(body.position, at)) body.position = at;
        }

        // Exact equality: Unity's Vector ==/!= treat anything within 1e-5 as equal and would miss small writes.
        static bool Same(Vector2 a, Vector2 b) { return a.x == b.x && a.y == b.y; }
        static bool Same(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }

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
            JiggleDropScript[] orbs = UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Array.Sort(clones, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            float dt = Time.fixedDeltaTime;
            FloatingNumberScript numbers = Singleton<FloatingNumberScript>.Instance;
            bool spawnNumbers = numbers != null && numbers.ActuallySpawnText;
            Pending.Clear();
            _frame = 0;
            _atomNow = false;
            _handoff = null;
            if (resume != null) Pending.AddRange(resume.pending);
            Polls.Clear();
            if (resume?.polls != null) Polls.AddRange(resume.polls);
            if (resume != null) RestoreRoutines(resume.routines);
            else
            {
                OrbsSeen.Clear();
                OrbsSeen.UnionWith(SeenFrom(movement.GetComponent<Rigidbody2D>().position));
            }
            Rigidbody2D body = movement.GetComponent<Rigidbody2D>();
            Transform player = movement.transform;
            MacroEngine.FrameGrid frames = MacroEngine.FrameGrid.Playback;
            _stepFrom = resume != null ? resume.stepFrom : body.position;
            _stepTo = resume != null ? resume.stepTo : body.position;
            // globalStats.FixedUpdate pays NP before every other FixedUpdate (Singleton DefaultExecutionOrder -50).
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
                    // A tick's frame starts with the Start of what the last frame's end woke, before its FixedUpdates (RULES.md exception 12).
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
                    ShowBlend(player, Vector2.Lerp(_stepFrom, _stepTo, frames.Frames(TasPlugin.GridTicks).LastAlpha));
                    Vector3 lag = _blendBase;
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
                    foreach (JiggleDropScript orb in orbs)
                        if (orb != null && orb.isActiveAndEnabled && OrbCooldown(orb) > 0f) _orbFixedUpdate(orb);
                    EndBlend();
                    bool wrote = !Same(player.position, lag);
                    if (!wrote && !Same(_stepTo, body.position)) Place(player, _stepTo);
                    Physics2D.SyncTransforms();
                    _stepFrom = body.position;
                    Physics2D.Simulate(dt);
                    // A physics callback's write (hitSpring) moves the body at once and drops the interpolation, as at 1x.
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
                    SeeOrbs(orbs, _stepTo);
                    long ticks = TasPlugin.GridTicks;
                    float[] alphas = frames.Frames(ticks).Alphas;
                    int frozen = _freezeFor >= 0f ? frames.FrozenFrames(ticks, _freezeFor) : 0;
                    for (int k = 0; k < alphas.Length; k++)
                    {
                        int repeats = MacroEngine.FrameRepeats(k, alphas.Length, frozen, _pauseFrames, _pauseAfter);
                        TasPlugin.CountHeldFrames(repeats);
                        for (int repeat = repeats; repeat >= 0; repeat--)
                        {
                            if (k > 0 || repeat < repeats)
                            {
                                _frame++;
                                FrameStart(player, body);
                            }
                            if (StartWaiting.Count > 0) StartPass();
                            ShowBlend(player, Vector2.Lerp(_stepFrom, _stepTo, alphas[k]));
                            Vector3 at = _blendBase;
                            _update(movement);
                            if (!Same(player.position, at))
                            {
                                // The last frame's write reaches the body at the next step's sync, so the next squish check sees the old body position.
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
                            if (k == 0 && repeat == repeats && _atomDue >= 0 && _atomDue <= _tick)
                            {
                                _atomDue += atomRepeat;
                                if (_atomCollider != null) CallInvoked(_atomCollider, AtomCall);
                            }
                            if (k == 0 && repeat == repeats && Polls.Count > 0)
                            {
                                Vector3 before = player.position;
                                foreach (var (due, teleporter) in Polls.ToArray())
                                {
                                    if (due > _tick) continue;
                                    int i = Polls.IndexOf((due, teleporter));
                                    if (i >= 0) Polls[i] = (_tick + 1, teleporter);
                                    if (teleporter != null) CallInvoked(teleporter, PollCall);
                                }
                                if (!Same(player.position, before))
                                {
                                    if (k < alphas.Length - 1 || repeat > 0) Physics2D.SyncTransforms();
                                    _stepFrom = _stepTo = player.position;
                                }
                            }
                            Resume(Wait.Frame, frameDt);
                            if (_atomNow)
                            {
                                _atomNow = false;
                                if (_atomCollider != null) CallInvoked(_atomCollider, AtomCall);
                            }
                            Resume(Wait.EndOfFrame, frameDt);
                        }
                    }
                    EndBlend();
                    OriginFrame();
                    afterTick?.Invoke();
                }
                return _tick;
            }
            finally
            {
                Active = false;
                EndBlend();
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
                foreach (var (due, teleporter) in Polls)
                    if (teleporter != null) teleporter.InvokeRepeating(PollCall, Mathf.Max(0f, (due - _tick) * Time.fixedDeltaTime), Time.fixedDeltaTime);
                Polls.Clear();
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
