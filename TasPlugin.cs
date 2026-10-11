using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;

namespace IGTAPTasMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class TasPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.deafwave.igtap.tas";
        public const string PluginName = "IGTAP TAS Studio";
        public const string PluginVersion = "7.0.0";

        internal static TasPlugin Instance;
        internal static ManualLogSource Log;

        private readonly MacroEngine _engine = new MacroEngine();
        private readonly InputRecorder _recorder = new InputRecorder();
        private WebStudioServer _webServer;
        private Harmony _harmony;
        private MacroLibrary _library;
        private string _libraryPath;
        private string _recordingsPath;
        private string _savestatesPath;
        private string _settingsPath;
        private StudioSettings _settings = new StudioSettings();
        private readonly List<RecordingDefinition> _recordings = new List<RecordingDefinition>();
        private readonly List<Savestate> _savestates = new List<Savestate>();
        private float _pendingCourseTime;
        private bool _pendingCourseWasTracking;
        private MacroDefinition _pendingReplay;
        private int _pendingReplayStartDelayTicks;
        // Run route from a tick: playback runs fast (no wall-clock wait, the same frames) until the run has played this many ticks.
        private int _fastUntilTick;
        private Rigidbody2D _playerBody;
        // The Movement _playerBody was fetched for (KeepInterpolation): a reference check per tick instead of two gameObject lookups.
        private Movement _playerBodyOwner;
        private bool _hasPreservedCourseResetPoint;
        private Vector2 _preservedCourseResetPoint;
        /// <summary>The canonical FloatingOrigin reference the last quick restart onto a course, bonus or transition start put the world at; null otherwise.</summary>
        private Vector3? _canonicalReference;
        private CourseDef _runCourse;
        private int _brakeTicks = -1;
        private float _brakeAxis;
        const int MaxBrakeTicks = 150;
        // Read by the studio's live state (every push) and the course hooks: typed refs, as FieldInfo.GetValue boxes.
        private static readonly AccessTools.FieldRef<courseScript, float> CourseCurrentPathTime = AccessTools.FieldRefAccess<courseScript, float>("currentPathTime");
        private static readonly AccessTools.FieldRef<courseScript, bool> CourseTracking = AccessTools.FieldRefAccess<courseScript, bool>("tracking");

        private WebStudioRequest _bootstrapRequest;
        private int _bootstrapPhase;
        private JObject _bootstrapBody;
        private readonly List<string> _bootstrapNotes = new List<string>();

        private static readonly AccessTools.FieldRef<Movement, bool> MovIsOnMainMenu = AccessTools.FieldRefAccess<Movement, bool>("isOnMainMenu");

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            // The overlay draws on Repaint only and uses no GUILayout: no Layout event, one OnGUI call per frame fewer.
            useGUILayout = false;
            _libraryPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "routes.json");
            _recordingsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "recordings");
            _savestatesPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "savestates");
            _settingsPath = Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "settings.json");
            LoadSettings();
            LoadLibrary();
            LoadRecordings();
            LoadSavestates();
            LoadCourses();
            LoadFullTas();
            LoadTechs();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => EngineSimExport.RememberCompositePaths();
            _engine.AttemptEnded += OnAttemptEnded;
            _engine.RefreshRewards = RefreshFullTasRewards;
            Application.runInBackground = true;
            _webServer = new WebStudioServer(BuildLiveWebState, WebStateVersion, BuildStaticWebState);
            try
            {
                _webServer.Start(Path.Combine(Paths.PluginPath, "IGTAPTasWeb"));
                Logger.LogInfo("Web Studio listening at " + WebStudioServer.Url);
                if (_settings.autoOpenWebStudio) Application.OpenURL(WebStudioServer.Url);
            }
            catch (Exception ex) { Logger.LogError("Could not start Web Studio: " + ex); }
            _harmony = new Harmony(PluginGuid);
            MethodInfo target = AccessTools.Method(typeof(Movement), "getPlayerControlledInput");
            MethodInfo prefix = AccessTools.Method(typeof(TasPlugin), nameof(BeforePlayerInput));
            if (target == null || prefix == null)
            {
                Logger.LogError("Movement.getPlayerControlledInput was not found.");
                return;
            }
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            PatchOptional(typeof(Movement), "FixedUpdate", nameof(BeforeMovementFixedUpdate), nameof(AfterMovementFixedUpdate));
            PatchOptional(typeof(Movement), "Update", null, nameof(AfterMovementUpdate));
            PatchOptional(typeof(courseScript), "FixedUpdate", nameof(BeforeOrderedFixedUpdate), null);
            PatchOptional(typeof(VmanScript), "FixedUpdate", nameof(BeforeOrderedFixedUpdate), null);
            TurboRunner.Patch(_harmony);
            PatchOptional(typeof(courseScript), "startTracking", nameof(BeforeCourseStart), nameof(AfterCourseStart));
            PatchOptional(typeof(courseScript), "stopTracking", nameof(BeforeCourseFinish), nameof(AfterCourseFinish));
            PatchOptional(typeof(Movement), "onDeath", null, nameof(AfterPlayerDeath));
            PatchOptional(typeof(Movement), "CheckForSuperJump", nameof(BeforeDashFrame), null);
            PatchOptional(typeof(Movement), "NormaliseInput", nameof(BeforeNormaliseInput), null);
            PatchOptional(typeof(Movement), "touchedSolidGround", nameof(BeforeWallTouch), null);
            PatchOptional(typeof(Movement), "touchedMossyGround", nameof(BeforeWallTouch), null);
            PatchOptional(typeof(Saveloader), "autosave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "manualSave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "delayedManualSave", nameof(BlockSharedSave), null);
            PatchOptional(typeof(Saveloader), "Awake", nameof(BeforeSaveloaderAwake), null);
            PatchOptional(typeof(upgradeBox), "DoBuyUpgrade", null, nameof(AfterBuyUpgrade));
            PatchOptional(typeof(upgradeBox), "OnTriggerStay2D", nameof(BeforeUpgradeBoxStay), null);
            PatchOptional(typeof(Movement), "respawn", nameof(BeforeRespawn), null);
            PatchOptional(typeof(Movement), "ToggleCheckpointUse", null, nameof(AfterToggleCheckpointUse));
            PatchOptional(typeof(StepUpAnimationScript), "onSteppedUp", nameof(BeforeSteppedUp), null);
            PatchOptional(typeof(Movement), "TimeStop", nameof(BeforeOverlayTimeStop), null);
            PatchOptional(typeof(clonesScript), "Update", nameof(BeforeProbeCloneUpdate), null);
            PatchOptional(typeof(clonesScript), "SpawnClone", nameof(BeforeProbeSpawn), nameof(AfterProbeSpawn));
            PatchProbeRng();
            PatchOptional(typeof(AtomColliderSystem), "callAddNewAtom", nameof(BeforeProbeAtomCall), null);
            PatchOptional(typeof(globalStats), "FixedUpdate", nameof(BeforeProbeStatsFixedUpdate), null);
            PatchOptional(typeof(FloatingOrigin), "MoveRootTransforms", nameof(BeforeMoveRootTransforms), nameof(AfterMoveRootTransforms));
            PatchTechHooks();
            PatchVmanCreditsCrash();
            PatchWallClock();
            InstallTasHoldPoint();
            foreach (var drawer in new[] { AccessTools.Method(typeof(PlayerAnimationEventProxy), "RelayFootstep"),
                         AccessTools.Method(typeof(FloatingNumberScript), "spawnNewFloatingNumber"), AccessTools.Method(typeof(ObjectBobber), "OnEnable") })
                _harmony.Patch(drawer, prefix: new HarmonyMethod(AccessTools.Method(typeof(PresentationDraw), nameof(PresentationDraw.Before))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PresentationDraw), nameof(PresentationDraw.After))));
            Logger.LogInfo("TAS Studio " + PluginVersion + " loaded. Use " + WebStudioServer.Url + ". F8 is emergency stop.");
        }

        static string _wallClockTarget;

        internal bool CheckpointRespawns { get { return _settings.checkpointRespawns; } }

        // Turbo (TurboRunner.Run) and the offline engine step a tick as Movement.FixedUpdate, then every courseScript's, then
        // VmanScript's. Unity's own order between them isn't fixed: after a new game it can run a course's first, which sets
        // courseResetPoint to its resetPoint before the player's quick restart reads it (a route whose quick restart falls on
        // the tick its start gate starts tracking then respawns elsewhere at 1x and dies). While a route plays at 1x, a course's
        // or Vman's FixedUpdate that comes before the player's this tick waits and runs right after it, in turbo's order.
        static readonly MethodInfo CourseFixedUpdateMethod = AccessTools.Method(typeof(courseScript), "FixedUpdate");
        static readonly MethodInfo VmanFixedUpdateMethod = AccessTools.Method(typeof(VmanScript), "FixedUpdate");
        static readonly List<MonoBehaviour> DeferredFixedUpdates = new List<MonoBehaviour>();
        // The same scripts, for the membership check each deferred FixedUpdate makes (List.Contains was a scan of every course
        // already waiting). The same comparer as the list's (UnityEngine.Object.Equals, GetHashCode: the instance id).
        static readonly HashSet<MonoBehaviour> DeferredQueued = new HashSet<MonoBehaviour>();
        // Their FixedUpdate as open delegates (made on first use, after the patches: a call goes through
        // BeforeOrderedFixedUpdate as Unity's does), and a reused copy of the list to run them from.
        static Action<courseScript> _courseFixedUpdate;
        static Action<VmanScript> _vmanFixedUpdate;
        static readonly List<MonoBehaviour> DeferredRunning = new List<MonoBehaviour>();
        static float _playerFixedTime = -1f;
        static bool _runningDeferred;

        // The studio's static state version (TasPlugin.Http.cs WebStateVersion) and what it was last checked against.
        long _webStateVersion;
        long _webStateStamp;
        float _webStateCheckAt = float.NegativeInfinity;

        private static bool IsMenuPlayer(Movement movement)
        {
            return MovIsOnMainMenu(movement);
        }

        CourseDef RunCourse { get { return _fullTas != null ? null : _silentEvaluation ? _solverTarget : _runCourse; } }

        private bool TouchesEndBox(Movement player, CourseDef course)
        {
            return TouchesBox(player, course.endX, course.endY, course.endW, course.endH);
        }

        static readonly List<BoxCollider2D> TouchBoxes = new List<BoxCollider2D>();

        private static bool TouchesBox(Movement player, float x, float y, float w, float h)
        {
            Vector2 origin = CurrentOrigin();
            Rect zone = new Rect(x - w / 2f + origin.x, y - h / 2f + origin.y, w, h);
            player.GetComponents(TouchBoxes);
            foreach (BoxCollider2D box in TouchBoxes)
            {
                if (!box.enabled) continue;
                Bounds bounds = box.bounds;
                if (new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y).Overlaps(zone)) return true;
            }
            return false;
        }

        private static Movement FindWorldMovement()
        {
            Movement[] all = FindObjectsByType<Movement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return null;
            Movement fallback = null;
            foreach (Movement m in all)
            {
                if (m == null) continue;
                if (!IsMenuPlayer(m)) return m;
                if (fallback == null) fallback = m;
            }
            return fallback ?? all[0];
        }

        /// <summary>Why the world can't be used (null: it can): at the main menu there is no world player, only the menu's.</summary>
        static string WorldNotLoaded()
        {
            Movement m = FindWorldMovement();
            return m != null && !IsMenuPlayer(m) ? null : "The world scene isn't loaded (the game is at its main menu): POST /api/oracle/bootstrap loads it.";
        }

        // The studio's live state reads the world player on every push (every 33 ms or more), and FindWorldMovement is a full
        // FindObjectsByType scan. The push keeps the last world player it found while that one lives and is still the world
        // one (a scene load destroys it, the bootstrap's flip makes a menu player the world one). It can only differ from a
        // scan while two world players are loaded at once, when the scan's pick is arbitrary anyway; display only.
        Movement _livePlayer;

        private Movement LiveWorldMovement()
        {
            if (_livePlayer != null && !IsMenuPlayer(_livePlayer)) return _livePlayer;
            Movement found = FindWorldMovement();
            _livePlayer = found != null && !IsMenuPlayer(found) ? found : null;
            return found;
        }

        // The coloured blocks' state (colouredBlockSwapper.isBlueActive) as a pseudo field of the player, so a category start
        // saves and puts it back with the unlocks: without block swap nothing resets it (Movement.respawn swaps only when
        // blockSwapUnlocked), so a save's swapped blocks (Course 4's SwapBlocksOnce box, dashes with block swap) would carry in.
        internal const string BlueActiveField = "blockSwapper.isBlueActive";
        static readonly FieldInfo BlockSwapperField = AccessTools.Field(typeof(Movement), "blockSwapper");

        // A global upgrade (globalStats.globalUpgradeDict) as a pseudo field of the player, saved and put back with the
        // unlocks: ORB has every refill orb on and ZIP the zip movers (README ### Rules 11); every category sets them from
        // its own unlocks and nothing else turns either on.
        internal const string GlobalUpgradePrefix = "globalUpgradeDict.";
        internal static string GlobalUpgradeField(globalStats.globalUpgradeSet upgrade) { return GlobalUpgradePrefix + upgrade; }
        static readonly MethodInfo RefillOrbState = AccessTools.Method(typeof(JiggleDropScript), "SetActiveVisualState");

        /// <summary>
        /// Each refill orb's usable flag and sprites from the refill upgrades now (JiggleDropScript.SetActiveVisualState, the
        /// game's own refresh): the game runs it only on an orb's use, cooldown end or coming on camera, so one on screen
        /// would keep the state from before the upgrades changed.
        /// </summary>
        internal static void RefreshRefillOrbs()
        {
            foreach (JiggleDropScript orb in FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (orb != null) RefillOrbState.Invoke(orb, new object[] { false });
        }

        const string CapabilitiesLockedError = "\"Don't touch my unlocks\" is on; turn it off to use this.";

        static FloatingOrigin _floatingOrigin;

        internal static FloatingOrigin CurrentFloatingOrigin()
        {
            if (_floatingOrigin == null) _floatingOrigin = FindAnyObjectByType<FloatingOrigin>();
            return _floatingOrigin;
        }

        internal static Vector2 CurrentOrigin()
        {
            FloatingOrigin origin = CurrentFloatingOrigin();
            return origin == null ? Vector2.zero : (Vector2)origin.currentOrigin;
        }

        bool _newGameSession;
        // The save's unlocks when a new game replaced its world (SaveUnlocks reports these until the save is loaded again).
        Caps? _newGameSaveUnlocks;
        int _newGamePhase;
        int _newGameFrame;
        WebStudioRequest _newGameRequest;
        readonly List<string> _newGameNotes = new List<string>();
        TurboRunner.Snapshot _newGameStart;
        Movement _newGamePlayer;
        bool _newGameVman;
        // A Full-TAS savestate start: the savestate (id) restored once the new game (or Vman world) is at rest; the start is
        // captured when the player is at rest again from there, with the unlocks the savestate carries. A savestate in the air
        // (its onGround false) can't rest: it is held there instead (_newGameAirStart, restored every tick) and the start is
        // taken on the same count of ticks, exactly at the savestate.
        string _newGameSavestate;
        bool _newGameSavestateApplied;
        Savestate _newGameAirStart;
        Vector2? _newGameRebaseAt;
        bool _newGameRebased;
        JToken _newGameTree;
        FullTasUnlocks _newGameUnlocks;
        Movement _vmanSetupPlayer;
        bool _vmanSetupQuiet;
        int _newGameRestTicks;
        // Physics ticks the player must stand still on the ground before the start is captured. A Vman world's player spawns
        // in the air and lands on a frame-rate-dependent tick; capturing mid-fall gave every load a different start. Past
        // this many grounded ticks every tick-driven threshold (dash boost window 6 × 1.4, platform momentum history 7) is
        // passed, so each load's start plays the same.
        const int NewGameRestTicks = 10;
        // Where the loading player first stood (world position, facing) and whether it still stands so when the start is taken.
        // Real input reaches the player while a new game loads unless NewGameLoading neutralises it, and a start taken after it
        // walked is another start: FalseEnding2-Any%'s stood 95.6 units left of the spawn, facing left, and its best replayed
        // to goal 1 only. Reset whenever the start moves the player on purpose (a savestate, a rebase, the Vman world).
        Vector2? _newGameSpawn;
        bool _newGameSpawnFacing;
        const float NewGameSpawnDrift = 0.5f;
        /// <summary>The new game's world player is loading and settling: no input of the player's reaches it (patches).</summary>
        bool NewGameLoading { get { return _newGamePhase == 2 || _newGamePhase == 3; } }
        // The FloatingOrigin the start was captured at. Real-time play far from it rebases the world (FloatingOrigin.LateUpdate),
        // and a restore puts back only the player's scene position, which then lies somewhere else in the world: such a game
        // is no longer fresh, and the next Full-TAS run loads a new one.
        Vector2 _newGameOrigin;
        bool FreshGame { get { return _newGameStart != null && _newGamePlayer != null; } }
        // The origin first: it is a field read, the world player a scan of every Movement.
        bool FreshGameInWorld { get { return FreshGame && CurrentOrigin() == _newGameOrigin && _newGamePlayer == FindWorldMovement(); } }

        static readonly FieldInfo SaveloaderIsSaving = AccessTools.Field(typeof(Saveloader), "IsSaving");
        static readonly FieldInfo SaveloaderIsLoading = AccessTools.Field(typeof(Saveloader), "isLoading");

        static readonly AccessTools.FieldRef<PlatformMover, bool> MoverReset = AccessTools.FieldRefAccess<PlatformMover, bool>("reset");

        const float DefaultWalkSlowdown = 10f;
        bool _playbackPaced;
        float _savedMaximumDeltaTime;
        int _savedVSyncCount, _savedTargetFrameRate;
        float _walkSlowdown;
        bool _walkScaled;
        string _playbackSpeedText = "1x";

        int _pacedFps;
        MacroEngine.FrameGrid _pacedGrid = MacroEngine.FrameGrid.Playback;

        float _clockFrame, _clockNext;
        double _clockTotal;
        readonly System.Diagnostics.Stopwatch _paceClock = new System.Diagnostics.Stopwatch();
        double _paceDue;
        /// <summary>The last paced frame's game time (Time.timeAsDouble), NaN at a playback's start: a frame that doesn't move it stood still.</summary>
        double _pacedTime;

        private void Update()
        {
            PerfFrameStart();
            try { UpdateFrame(); } finally { PerfUpdateEnd(); }
        }

        void UpdateFrame()
        {
            // Paced from the moment a playback is asked for, so its first ticks already run on the fixed clock: before, a hitch
            // at the start (the savestate restore) could run several ticks in one frame, with no frames between them.
            PacePlayback((PlaybackActive || _pendingReplay != null) && !TurboRunner.Active);
            PaceWalk();
            OverlayFrame();
            BreakerLightsFrame();
            double pumpStart = PerfMs(_perfClock);
            if (_webServer != null) _webServer.Pump(HandleWebRequest);
            _perfNow.pump = (float)(PerfMs(_perfClock) - pumpStart);
            PumpSolver();
            PumpBootstrap();
            PumpNewGame();
            PumpOfflineBests();
            PumpFullTas();
            PumpFastForward();
            PumpProbe();
            PumpOneX();
            PumpLabMap();
            PumpLabTrainer();
            TasUpdate();
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f8Key.wasPressedThisFrame)
            {
                if (_recorder.IsRecording) StopRecording("Emergency stop");
                StopPlayback("Emergency stop");
                return;
            }
        }

        private readonly Dictionary<string, object> _playbackSavedAbilities = new Dictionary<string, object>();

        /// <summary>The open TAS editor session (TasPlugin.Editor.cs), or null.</summary>
        TasSession _tas;
        // TasBoundary's clock (TasPlugin.Editor.cs), restarted at every tick boundary it checks: one Stopwatch, not one a tick.
        readonly System.Diagnostics.Stopwatch _tasHoldClock = new System.Diagnostics.Stopwatch();
        // DrawTechPaths' hovered tech's variants in draw order (TasTechVariants, TasPlugin.Techs.cs), kept while the tech and its
        // grouping stay.
        List<TasTechVariant> _techsDrawn;
        TasTechs _techsDrawnFrom;
        string _techsDrawnTech;
        int _techsDrawnGrouping;

        // Movement's fields by name, as AccessTools.Field finds them (null: none), looked up once (MovementField,
        // TasPlugin.Session.cs): the studio's live state and the solver's per-tick audit read some every push or tick. Main
        // thread only, as every caller is.
        static readonly Dictionary<string, FieldInfo> MovementFields = new Dictionary<string, FieldInfo>();

        private MacroDefinition SelectedMacro { get { ClampSelections(); return _library.macros.Count == 0 ? null : _library.macros[_library.selectedIndex]; } }
        private void ClampSelections()
        {
            _library.selectedIndex = Mathf.Clamp(_library.selectedIndex, 0, Mathf.Max(0, _library.macros.Count - 1));
        }

        private static string TypeName(MacroStepType type) { return type == MacroStepType.WallJump ? "WALLJUMP" : type.ToString().ToUpperInvariant(); }

        private static bool IsGamePaused(Movement movement)
        {
            if (movement == null) return Time.timeScale == 0f;
            pauseMenuScript menu = movement.pauseMenu;
            return (object)menu != null && menu.menuOpen;
        }

        private static void EnsureGameUnpaused()
        {
            Movement movement = FindWorldMovement();
            if (movement == null) return;
            pauseMenuScript menu = movement.pauseMenu;
            if ((object)menu != null && menu.menuOpen) menu.menuButtonPressed();
        }

        private void OnDestroy()
        {
            if (_solverRunning) StopSolver("Game closed");
            TasClose("Game closed");
            RemoveTasHoldPoint();
            if (_recorder.IsRecording) StopRecording("Game closed");
            PacePlayback(false);
            SaveLibrary();
            SaveSettings();
            if (_webServer != null) _webServer.Dispose();
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
