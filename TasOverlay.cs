using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        sealed class InputRow
        {
            public long tick;
            public string tickText;
            public float x, y;
            public string stickText;
            public bool jumpPress, jumpRelease, jumpCut, dash;
            /// <summary>The stick held in a dash's freeze (turnX/turnY): its own row under the dash's tick, the input that turns the dash.</summary>
            public bool freeze;
            public bool jump1, jump2, jump1Press, jump2Press, dash1, dash2, dash1Press, dash2Press;
            /// <summary>A respawn came between this row and the one listed before it (RowBreak).</summary>
            public RowBreak breakBefore;
        }

        enum RowBreak : byte { None, QuickRestart, Death }

        const float InputRowHeight = 30f, InputBandHeight = 14f + 26f + 22f + 22f + 12f, InputColumnLine = 28f;
        readonly List<InputRow> _inputRows = new List<InputRow>();
        readonly List<BoxCollider2D> _hitboxes = new List<BoxCollider2D>();
        Movement _overlayPlayer;
        Vector2 _overlayAxis;
        /// <summary>The stick the last listed tick left held (its freeze stick when it had one): a tech demo lists the tick that lets it go.</summary>
        Vector2 _overlayHeldStick;
        long _overlayLastTick;
        long _overlayTicks;
        string _overlayRoute;
        string _overlayCourse = "—";
        string _overlayClock;
        int _overlayJumpPresses, _overlayDashPresses, _overlayJumpReleases;
        long _overlayRunTicks;
        bool _padJump1, _padJump2, _padJump2Newer, _padDash1Last;
        // The running route's inert inputs (TasOverlayInert.cs), numbered from the counts before its first tick; whether each
        // pad button's hold is shown, and whether the last release did nothing (its jump cut buffer neither).
        InertInputs _overlayInert;
        int _inertJumpBase, _inertDashBase, _inertReleaseBase;
        bool _padJump1Shown = true, _padJump2Shown = true, _lastReleaseInert;
        // Manual play (OverlayListsManual): the list shows the player's own ticks. A session starts at the first tick that
        // holds an input, or at a respawn, so the rows a playback left stay until the player plays.
        bool _overlayManual, _manualRestart;
        bool _manualJump1Press, _manualJump2Press, _manualDash1Press, _manualDash2Press, _manualRelease;
        // A respawn since the last listed row: the next row the list adds draws a line above it.
        RowBreak _pendingBreak;
        static readonly AccessTools.FieldRef<Movement, bool> JumpCutBuffer = AccessTools.FieldRefAccess<Movement, bool>("jumpCutBuffer");
        GUIStyle _overlayTitle, _overlayText, _overlayTicksStyle, _overlaySmall, _overlaySmallRight, _overlayGraze, _overlayPadButton;
        int _overlayStyleHeight;

        int _ticksSinceFrame;
        int _droppedFrames, _worstFrameTicks;
        long _lastDropTick;
        const float DroppedFramesLingerSeconds = 10f;
        float _droppedFramesShownUntil;
        // DrawDroppedFrames' text, built again (null) only when a drop or a new route changes its numbers.
        string _droppedFramesBanner;

        void OverlayFrame()
        {
            CountFrame();
            SampleLiveInput();
            if (_overlayPlayer != null) SampleDebug(_overlayPlayer);
            if (_settings.showOverlay) SampleClones();
            int ticks = _ticksSinceFrame;
            _ticksSinceFrame = 0;
            if (ticks <= 1 || !OverlayListsRun) return;
            if (_droppedFrames == 0)
                Log.LogWarning("Playback is dropping frames: " + ticks + " physics ticks in one frame at tick " + _engine.PhysicsTicks
                    + ". Update-driven game state (clone pay) may differ from the route's; the overlay counts every drop.");
            _droppedFrames += ticks - 1;
            _worstFrameTicks = Mathf.Max(_worstFrameTicks, ticks);
            _lastDropTick = _engine.PhysicsTicks;
            _droppedFramesBanner = null;
            _droppedFramesShownUntil = Time.unscaledTime + DroppedFramesLingerSeconds;
        }

        const float LiveFlashSeconds = 0.15f;
        InputAction _liveJump1, _liveJump2, _liveDash1, _liveDash2;
        float _liveJump1At = float.NegativeInfinity, _liveJump2At = float.NegativeInfinity, _liveDash1At = float.NegativeInfinity,
            _liveDash2At = float.NegativeInfinity, _liveReleaseAt = float.NegativeInfinity;
        readonly InputRow _liveRow = new InputRow();

        void SampleLiveInput()
        {
            if (_liveJump1 == null)
            {
                InputActionAsset actions = InputSystem.actions;
                if (actions == null) return;
                _liveJump1 = actions.FindAction("Jump");
                _liveJump2 = actions.FindAction("Jump2");
                _liveDash1 = actions.FindAction("Dash");
                _liveDash2 = actions.FindAction("Dash2");
                if (_liveJump1 == null || _liveJump2 == null || _liveDash1 == null || _liveDash2 == null) { _liveJump1 = null; return; }
            }
            float now = Time.unscaledTime;
            bool jump1 = _liveJump1.WasPressedThisFrame(), jump2 = _liveJump2.WasPressedThisFrame();
            bool dash1 = _liveDash1.WasPressedThisFrame(), dash2 = _liveDash2.WasPressedThisFrame();
            bool release = _liveJump1.WasReleasedThisFrame() || _liveJump2.WasReleasedThisFrame();
            if (jump1) _liveJump1At = now;
            if (jump2) _liveJump2At = now;
            if (dash1) _liveDash1At = now;
            if (dash2) _liveDash2At = now;
            if (release) _liveReleaseAt = now;
            // Movement.Update takes a press or release only outside the pause menu and GamePaused; the next tick reads it.
            Movement p = _overlayPlayer;
            if (!OverlayListsManual || p == null || p.GamePaused || p.pauseMenu != null && p.pauseMenu.menuOpen) return;
            _manualJump1Press |= jump1;
            _manualJump2Press |= jump2;
            _manualDash1Press |= dash1;
            _manualDash2Press |= dash2;
            _manualRelease |= release;
        }

        InputRow LiveRow(Movement player)
        {
            if (_liveJump1 == null || player == null) return null;
            float now = Time.unscaledTime;
            InputRow row = _liveRow;
            row.x = player.MoveAxis.x;
            row.y = player.MoveAxis.y;
            row.jump1 = _liveJump1.IsPressed();
            row.jump2 = _liveJump2.IsPressed();
            row.dash1 = _liveDash1.IsPressed();
            row.dash2 = _liveDash2.IsPressed();
            row.jump1Press = now - _liveJump1At < LiveFlashSeconds;
            row.jump2Press = now - _liveJump2At < LiveFlashSeconds;
            row.dash1Press = now - _liveDash1At < LiveFlashSeconds;
            row.dash2Press = now - _liveDash2At < LiveFlashSeconds;
            row.jumpRelease = now - _liveReleaseAt < LiveFlashSeconds;
            row.jumpCut = JumpCutBuffer(player);
            return row;
        }

        string DroppedFramesText()
        {
            return _droppedFrames == 0 ? "" : " · " + _droppedFrames + " dropped frame" + (_droppedFrames == 1 ? "" : "s")
                + " (up to " + _worstFrameTicks + " ticks in one frame, last at tick " + _lastDropTick + ")";
        }

        bool OverlayListsRun { get { return _engine.IsRunning && !_engine.Quiet && !_silentEvaluation && !TurboRunner.Active; } }

        /// <summary>The player plays by hand: no route, turbo run or silent evaluation drives the ticks.</summary>
        bool OverlayListsManual { get { return !_engine.IsRunning && !_silentEvaluation && !TurboRunner.Active; } }

        /// <summary>
        /// Movement.respawn (a quick restart or a death's respawn): the list draws a line between the ticks before and after it.
        /// Manual play also counts its ticks from 1 again; a route's keep its own count.
        /// </summary>
        void OverlayRespawned(bool quickRestart)
        {
            if (!OverlayListsRun && !OverlayListsManual) return;
            _pendingBreak = quickRestart ? RowBreak.QuickRestart : RowBreak.Death;
            if (OverlayListsManual) _manualRestart = true;
            // The checkpoint coyote jump's respawn: the tick it came after (manual play counts from 0 again).
            else _techRespawnTick = _overlayTicks;
        }

        void OverlayCaptureAxis(Movement player)
        {
            if (_engine.IsRunning) _overlayAxis = player.MoveAxis;
        }

        void OverlayTick(Movement player)
        {
            _overlayPlayer = player;
            ApplyTasSkin(player);
            // Every tick can change the list, the velocity and the pad: the cached panels (DrawCachedPanels) are drawn again.
            _panelCacheDirty = true;
            if (!OverlayListsRun)
            {
                _overlayLastTick = 0;
                if (OverlayListsManual) ManualOverlayTick(player);
            }
            else if (_engine.PhysicsTicks != _overlayLastTick)
            {
                if (_overlayLastTick == 0 || _engine.PhysicsTicks < _overlayLastTick || !ReferenceEquals(_engine.ActiveMacroName, _overlayRoute))
                {
                    _inputRows.Clear();
                    _overlayTicks = 0;
                    _droppedFrames = _worstFrameTicks = 0;
                    _overlayManual = _manualRestart = false;
                    _pendingBreak = RowBreak.None;
                    _lastDropTick = 0;
                    _droppedFramesBanner = null;
                    _ticksSinceFrame = 0;
                    _padJump1 = _padJump2 = _padJump2Newer = _padDash1Last = false;
                    _overlayRoute = _engine.ActiveMacroName;
                    _overlayCourse = _runCourse != null ? _runCourse.Token : "—";
                    _overlayHeldStick = Vector2.zero;
                    // Numbered from the route's first input: a list started later (a TAS editor seek) can't number them.
                    _overlayInert = _engine.RunTicks == 1 ? LoadInertInputs(_engine.ActiveMacro) : null;
                    _inertJumpBase = _overlayJumpPresses;
                    _inertDashBase = _overlayDashPresses;
                    _inertReleaseBase = _overlayJumpReleases;
                    _overlayRunTicks = 0;
                    _lastReleaseInert = false;
                    ResetTechs(player);
                }
                _overlayLastTick = _engine.PhysicsTicks;
                _overlayTicks++;
                _ticksSinceFrame++;
                _overlayClock = null;
                bool jumpPress = _engine.JumpPresses != _overlayJumpPresses, jumpRelease = _engine.JumpReleases != _overlayJumpReleases,
                    dash = _engine.DashPresses != _overlayDashPresses;
                // The route's inert inputs are left out. A tick that took no input (a dash's) holds the next input's y, unless a lock
                // or a jump on the dash's frame sets it.
                InertInputs inert = _overlayInert;
                bool played = _engine.RunTicks != _overlayRunTicks;
                long input = played ? _engine.RunTicks - 1 : _engine.RunTicks;
                int release = _engine.JumpReleases - _inertReleaseBase - 1;
                bool pressShown = jumpPress && (inert == null || !inert.Jumps.Contains(_engine.JumpPresses - _inertJumpBase - 1));
                bool releaseShown = jumpRelease && (inert == null || !inert.Releases.Contains(release));
                if (jumpRelease) _lastReleaseInert = !releaseShown;
                // A press's hold shows while the press or its release (the next one: it lets go of both buttons) does something.
                bool holdShown = pressShown || inert == null || !inert.Releases.Contains(jumpRelease ? release : release + 1);
                bool dashShown = dash && (inert == null || !inert.Dashes.Contains(_engine.DashPresses - _inertDashBase - 1));
                Vector2 axis = _overlayAxis;
                if (inert != null && played && InertInputs.At(inert.X, input)) axis.x = 0f;
                if (inert != null && (played || !jumpPress && !_engine.TickStickFromLockY) && InertInputs.At(inert.Y, input)) axis.y = 0f;
                // A tech demo lists only the ticks that hold an input: its idle tail (and the stick at rest between presses) is noise.
                // Letting go of a held stick (a freeze turn's, or the tick's) is an input, so that tick is listed.
                Vector2? freezeStick = _engine.FreezeStick;
                bool idle = !jumpPress && !jumpRelease && !dash && !_engine.JumpHeld && _overlayAxis == Vector2.zero && _overlayHeldStick == Vector2.zero;
                if (!(idle && IsTechDemo(_engine.ActiveMacro)))
                {
                    InputRow row = NextInputRow();
                    row.tickText = _overlayTicks.ToString();
                    row.x = axis.x;
                    row.y = axis.y;
                    row.stickText = StickText(row.x, row.y);
                    row.jumpPress = jumpPress;
                    row.jumpRelease = releaseShown;
                    row.jumpCut = JumpCutBuffer(player) && !_lastReleaseInert;
                    row.dash = dashShown;
                    AssignPadButtons(row, _engine.JumpHeld || row.jumpPress, pressShown, holdShown);
                    _inputRows.Add(row);
                    row.tick = _overlayTicks;
                    // A dash's freeze stick (turnX/turnY) comes between this tick and the next and turns the dash: its own row, so
                    // the dash's two inputs read in order (press dash with this tick's stick, then this stick in the freeze).
                    if (freezeStick is Vector2 freeze && (inert == null || !inert.Turns.Contains(_engine.DashPresses - _inertDashBase - 1)))
                    {
                        InputRow turn = NextInputRow();
                        turn.tick = _overlayTicks;
                        turn.tickText = "freeze";
                        turn.freeze = true;
                        turn.x = freeze.x;
                        turn.y = freeze.y;
                        turn.stickText = StickText(freeze.x, freeze.y);
                        turn.jumpPress = turn.jumpRelease = turn.jumpCut = turn.dash = false;
                        turn.jump1 = turn.jump2 = turn.dash1 = turn.dash2 = false;
                        turn.jump1Press = turn.jump2Press = turn.dash1Press = turn.dash2Press = false;
                        _inputRows.Add(turn);
                    }
                }
                else _padJump1 = _padJump2 = false;
                _overlayHeldStick = freezeStick ?? _overlayAxis;
                TechAfterTick(player, _overlayTicks, jumpPress, dash);
            }
            _overlayJumpPresses = _engine.JumpPresses;
            _overlayDashPresses = _engine.DashPresses;
            _overlayJumpReleases = _engine.JumpReleases;
            _overlayRunTicks = _engine.RunTicks;
            _manualJump1Press = _manualJump2Press = _manualDash1Press = _manualDash2Press = _manualRelease = false;
            SampleVelocity(player);
        }

        /// <summary>A row for the list's newest tick: a new one until the list fills, then its oldest. It takes the pending break.</summary>
        InputRow NextInputRow()
        {
            int slots = InputRowSlots;
            while (_inputRows.Count > slots) _inputRows.RemoveAt(0);
            InputRow row;
            if (_inputRows.Count < slots) row = new InputRow();
            else
            {
                row = _inputRows[0];
                _inputRows.RemoveAt(0);
            }
            row.breakBefore = _inputRows.Count > 0 ? _pendingBreak : RowBreak.None;
            _pendingBreak = RowBreak.None;
            row.freeze = false;
            return row;
        }

        /// <summary>
        /// One tick of manual play: the stick the tick read (MoveAxis) and the presses and releases Movement.Update took since the
        /// last tick (SampleLiveInput). Idle ticks count but aren't listed, as a tech demo's.
        /// </summary>
        void ManualOverlayTick(Movement player)
        {
            bool jump1 = _manualJump1Press || _liveJump1 != null && _liveJump1.IsPressed();
            bool jump2 = _manualJump2Press || _liveJump2 != null && _liveJump2.IsPressed();
            bool jumpPress = _manualJump1Press || _manualJump2Press, dash = _manualDash1Press || _manualDash2Press;
            Vector2 stick = player.MoveAxis;
            bool idle = !jumpPress && !_manualRelease && !dash && !jump1 && !jump2 && stick == Vector2.zero;
            if (_manualRestart || !_overlayManual && !idle)
            {
                if (!_overlayManual)
                {
                    _overlayCourse = "—";
                    _inputRows.Clear();
                    _pendingBreak = RowBreak.None;
                }
                bool respawned = _manualRestart;
                _overlayTicks = 0;
                _overlayManual = true;
                _manualRestart = false;
                // The ticks count from 1 again, so the tech bars do too; a respawn's coyote ticks follow tick 0.
                ResetTechs(player);
                if (respawned) _techRespawnTick = 0;
            }
            if (!_overlayManual) return;
            _overlayTicks++;
            _overlayClock = null;
            TechAfterTick(player, _overlayTicks, jumpPress, dash);
            if (idle) return;
            InputRow row = NextInputRow();
            row.tick = _overlayTicks;
            row.tickText = _overlayTicks.ToString();
            row.x = stick.x;
            row.y = stick.y;
            row.stickText = StickText(row.x, row.y);
            row.jumpPress = jumpPress;
            row.jumpRelease = _manualRelease;
            row.jumpCut = JumpCutBuffer(player);
            row.dash = dash;
            row.jump1 = jump1;
            row.jump2 = jump2;
            row.jump1Press = _manualJump1Press;
            row.jump2Press = _manualJump2Press;
            row.dash1 = row.dash1Press = _manualDash1Press;
            row.dash2 = row.dash2Press = _manualDash2Press;
            _inputRows.Add(row);
        }

        void AssignPadButtons(InputRow row, bool jumpDown, bool pressShown, bool holdShown)
        {
            if (!jumpDown) _padJump1 = _padJump2 = false;
            else if (row.jumpPress)
            {
                bool second = _padJump1 && (!_padJump2 || !_padJump2Newer);
                if (second) { _padJump2 = true; _padJump2Shown = holdShown; }
                else { _padJump1 = true; _padJump1Shown = holdShown; }
                _padJump2Newer = second;
            }
            else if (!_padJump1 && !_padJump2) _padJump1 = _padJump1Shown = true;
            row.jump1 = _padJump1 && _padJump1Shown;
            row.jump2 = _padJump2 && _padJump2Shown;
            row.jump1Press = pressShown && !_padJump2Newer;
            row.jump2Press = pressShown && _padJump2Newer;
            row.dash1 = row.dash && !_padDash1Last;
            row.dash2 = row.dash && _padDash1Last;
            _padDash1Last = row.dash1;
            row.dash1Press = row.dash1;
            row.dash2Press = row.dash2;
        }

        // The last stick's text: a route or a player holds one stick over many ticks, so a listed tick rarely builds it again.
        float _stickTextX = float.NaN, _stickTextY = float.NaN;
        string _stickText;

        string StickText(float x, float y)
        {
            if (x == _stickTextX && y == _stickTextY) return _stickText;
            string text = x < 0f ? "←" : x > 0f ? "→" : "";
            if (x != 0f && Mathf.Abs(x) < 1f) text += " " + Mathf.RoundToInt(Mathf.Abs(x) * 100f) + "%";
            if (y != 0f) text += (text.Length > 0 ? " " : "") + (y > 0f ? "↑" : "↓") + (Mathf.Abs(y) < 1f ? " " + Mathf.RoundToInt(Mathf.Abs(y) * 100f) + "%" : "");
            _stickTextX = x;
            _stickTextY = y;
            return _stickText = text.Length > 0 ? text : "·";
        }

        static int _sceneGeneration;
        static bool _sceneGenerationHooked;

        /// <summary>
        /// Counts scene loads and unloads (hooked on first use). The scan caches below compare it: the game creates no spike,
        /// checkpoint, clone course or breaker at run time, so a scene's set changes only with a load or an unload.
        /// </summary>
        static int SceneGeneration
        {
            get
            {
                if (!_sceneGenerationHooked)
                {
                    _sceneGenerationHooked = true;
                    UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => _sceneGeneration++;
                    UnityEngine.SceneManagement.SceneManager.sceneUnloaded += scene => _sceneGeneration++;
                }
                return _sceneGeneration;
            }
        }

        int _menuCanvasScene = -1;
        readonly List<Canvas> _menuCanvases = new List<Canvas>();

        /// <summary>
        /// The player has the game's pause menu (or the settings under it) open: its canvases (pauseMenuScript's, found once per
        /// scene) are enabled. A playback's own pause (MacroStep.pauseFrames, the pauselift) doesn't count: its overlay stays.
        /// </summary>
        bool GameMenuOpen
        {
            get
            {
                if (PlaybackActive) return false;
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                if (scene != _menuCanvasScene || _menuCanvases.Exists(c => c == null))
                {
                    _menuCanvasScene = scene;
                    _menuCanvases.Clear();
                    foreach (pauseMenuScript menu in Resources.FindObjectsOfTypeAll<pauseMenuScript>())
                        if (menu.gameObject.scene.IsValid())
                            foreach (Canvas canvas in menu.GetComponentsInChildren<Canvas>(true))
                                if (canvas.isRootCanvas || canvas.overrideSorting) _menuCanvases.Add(canvas);
                }
                return _menuCanvases.Exists(c => c.enabled && c.gameObject.activeInHierarchy);
            }
        }

        private void OnGUI()
        {
            PerfGuiStart();
            try { OverlayGui(); } finally { PerfGuiEnd(); }
        }

        void OverlayGui()
        {
            // Only Repaint draws (useGUILayout is off, so the other events are input): they skip even the checks below.
            if (Event.current.type != EventType.Repaint) return;
            // Under the game's pause menu nothing anchored in the world is drawn (it would sit on top of the menu): the lab
            // ghosts' key and badges, spikes, checkpoints, the hitbox and the graze. The screen panels stay.
            bool goals = !string.IsNullOrEmpty(_settings.fullTasGoalsRun), origin = OriginOverlayShown, trainer = LabTrainerShown, ghosts = LabGhostOverlayShown;
            bool techs = TechPathsShown;
            if (!origin) ResetOriginOverlay();
            if (!(_settings.showOverlay || goals || origin || trainer || ghosts || techs)) return;
            bool menu = GameMenuOpen;
            ghosts &= !menu;
            float scale = Screen.height / 1080f;
            if (_overlayTitle == null || _overlayStyleHeight != Screen.height) BuildOverlayStyles(scale);
            if (ghosts) { DrawLabGhostBadges(scale); DrawLabGhostLegend(scale); }
            PerfSection(0);
            if (trainer) DrawLabTrainer();
            PerfSection(1);
            if (goals) DrawFullTasGoals(scale);
            PerfSection(2);
            if (origin) DrawOriginOverlay(scale);
            PerfSection(3);
            if (techs && !menu) DrawTechPaths(scale);
            PerfSection(4);
            if (!_settings.showOverlay) return;
            if (_overlayPlayer != null) SampleSpikes(_overlayPlayer, FixedStep());
            if (!menu)
            {
                DrawSpikes(scale);
                PerfSection(5);
                DrawCheckpointLabels(scale);
                PerfSection(6);
                DrawGraze(scale);
                PerfSection(7);
            }
            DrawCachedPanels(scale);
            if (_fullTasPlay?.idles != null && _fullTasPlay.started) DrawFullTasIdle(scale);
            PerfSection(8);
            if (_droppedFrames > 0 && (OverlayListsRun || Time.unscaledTime < _droppedFramesShownUntil)) DrawDroppedFrames(scale);
            PerfSection(9);
        }

        /// <summary>While a Full-TAS playback stands in one of its idles (the run waiting at a box for its Cash): a banner naming the
        /// buy and the seconds left, so a viewer sees the run waiting, not dawdling.</summary>
        void DrawFullTasIdle(float scale)
        {
            FullTasIdle idle = CurrentIdle(_fullTasPlay);
            if (idle == null) return;
            long tick = _engine.PhysicsTicks, held = _engine.HeldFrames;
            string goal = idle.label ?? "the next buy";
            double left = System.Math.Max(0.0, MacroEngine.LiveSeconds(idle.afford[0], idle.afford[1]) - MacroEngine.LiveSeconds(tick, held));
            // Rebuilt only when the tenths change: a new string every frame is IMGUI garbage (README "Studio").
            int tenths = (int)System.Math.Ceiling(left * 10);
            if (_idleBanner == null || _idleBannerGoal != idle.id || _idleBannerTenths != tenths)
            {
                _idleBanner = "IDLE · waiting for " + goal + " · " + (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
                _idleBannerGoal = idle.id;
                _idleBannerTenths = tenths;
            }
            float width = 620 * scale, height = 40 * scale;
            Rect box = new Rect((Screen.width - width) / 2f, 64 * scale, width, height);
            Fill(box, new Color(0.1f, 0.25f, 0.55f, 0.85f));
            Label(new Rect(box.x + 14 * scale, box.y, width - 28 * scale, height), _idleBanner, _overlayText, Color.white);
        }

        string _idleBanner;
        string _idleBannerGoal;
        int _idleBannerTenths;

        /// <summary>The idle the playback stands in now (its tick between the player's arrival at the box and the Cash), or null.</summary>
        FullTasIdle CurrentIdle(FullTasPlay play)
        {
            if (play?.idles == null || !play.started) return null;
            long tick = _engine.PhysicsTicks;
            foreach (FullTasIdle i in play.idles)
                if (tick >= i.arrive[0] && tick < i.afford[0]) return i;
            return null;
        }

        void DrawDroppedFrames(float scale)
        {
            if (_droppedFramesBanner == null)
                _droppedFramesBanner = "DROPPING FRAMES · " + _droppedFrames + " tick" + (_droppedFrames == 1 ? "" : "s") + " without a frame · worst "
                    + _worstFrameTicks + " ticks/frame · last at tick " + _lastDropTick;
            float width = 760 * scale, height = 40 * scale;
            Rect box = new Rect((Screen.width - width) / 2f, 16 * scale, width, height);
            Fill(box, new Color(0.55f, 0f, 0f, 0.85f));
            Label(new Rect(box.x + 14 * scale, box.y, width - 28 * scale, height), _droppedFramesBanner, _overlayText, Color.white);
        }

        void BuildOverlayStyles(float scale)
        {
            _overlayStyleHeight = Screen.height;
            float panelScale = PanelScale(scale);
            _overlayTitle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(20 * panelScale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
            _overlayTitle.normal.textColor = Color.white;
            _overlayText = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(18 * panelScale), fontStyle = FontStyle.Normal };
            _overlayTicksStyle = new GUIStyle(_overlayText) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow };
            _overlaySmall = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(14 * panelScale) };
            _overlaySmallRight = new GUIStyle(_overlaySmall) { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Overflow };
            _overlayPadButton = new GUIStyle(_overlaySmall) { alignment = TextAnchor.MiddleCenter };
            _overlayGraze = new GUIStyle(_overlayTitle) { fontSize = Mathf.RoundToInt(18 * scale), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
        }

        const float PanelWidth = 440f;
        static float PanelScale(float scale) { return Mathf.Min(scale, 1f); }

        static int InputRowSlots
        {
            get
            {
                float scale = PanelScale(Screen.height / 1080f);
                float header = (InputBandHeight + InputColumnLine) * scale + 1 + 12 * scale;
                return Mathf.Max(1, Mathf.FloorToInt((Screen.height - header - PadHeight * scale) / (InputRowHeight * scale)));
            }
        }

        static Rect InputPadRect(float scale)
        {
            return new Rect(0f, Screen.height - PadHeight * scale, PanelWidth * scale, PadHeight * scale);
        }

        static readonly AccessTools.FieldRef<Movement, BoxCollider2D> NormalCollider = AccessTools.FieldRefAccess<Movement, BoxCollider2D>("normalCollider");

        static BoxCollider2D Hurtbox(Movement player, List<BoxCollider2D> buffer)
        {
            BoxCollider2D body = NormalCollider(player);
            player.GetComponents(buffer);
            foreach (BoxCollider2D box in buffer)
                if (box.enabled && !ReferenceEquals(box, body)) return box;
            return null;
        }

        static Rect ScreenRect(Camera cam, Bounds bounds)
        {
            Vector3 a = cam.WorldToScreenPoint(bounds.min), b = cam.WorldToScreenPoint(bounds.max);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
        }

        static readonly AccessTools.FieldRef<spikeScript, int> SpikeCounter = AccessTools.FieldRefAccess<spikeScript, int>("counter");
        static readonly AccessTools.FieldRef<spikeScript, int> SpikeGrace = AccessTools.FieldRefAccess<spikeScript, int>("graceFrames");
        static readonly AccessTools.FieldRef<spikeScript, bool> SpikeOnDash = AccessTools.FieldRefAccess<spikeScript, bool>("killPlayerOnDash");
        static readonly AccessTools.FieldRef<spikeScript, bool> SpikeKilled = AccessTools.FieldRefAccess<spikeScript, bool>("playerKilled");

        sealed class SpikeShape
        {
            public Vector2[] points;
            public bool closed;
            public Rect bounds;
        }

        sealed class SpikeCollider
        {
            public Collider2D collider;
            public spikeScript script;
            public bool moving;
            public int shapeCount = -1;
            public Bounds bounds;
            public readonly List<SpikeShape> shapes = new List<SpikeShape>();
        }

        const float SpikeScanSeconds = 1f;
        readonly List<SpikeCollider> _spikeColliders = new List<SpikeCollider>();
        // checkpointScript triggers (script null): touching one sets respawnPoint to its position.
        readonly List<SpikeCollider> _checkpointColliders = new List<SpikeCollider>();
        readonly Dictionary<Collider2D, SpikeCollider> _spikeByCollider = new Dictionary<Collider2D, SpikeCollider>();
        readonly List<Collider2D> _colliderBuffer = new List<Collider2D>();
        readonly List<Collider2D> _overlaps = new List<Collider2D>();
        readonly PhysicsShapeGroup2D _shapeGroup = new PhysicsShapeGroup2D();
        readonly List<Vector2> _shapeVertices = new List<Vector2>();
        readonly List<Vector2> _screenPoints = new List<Vector2>();
        float _spikeScanTime = float.NegativeInfinity;
        Material _overlayMaterial;
        bool _overlayMaterialMissing;

        long _spikeSampleStep = long.MinValue;
        readonly HashSet<spikeScript> _grazeSpikes = new HashSet<spikeScript>();
        Bounds _grazeHurtbox;
        int _grazeTicks, _grazeLimit;
        bool _grazeKilled, _grazeGrounded, _grazeDashing;
        const float GrazeLingerSeconds = 1.5f;
        int _lastGrazeTicks, _lastGrazeLimit;
        bool _lastGrazeKilled;
        float _lastGrazeTime = float.NegativeInfinity;
        // DrawGraze's text: null after SampleSpikes changes a value it shows.
        string _grazeText;

        static long FixedStep() { return (long)System.Math.Round(Time.fixedTimeAsDouble / Time.fixedDeltaTime); }

        void OverlayBeforeTick(Movement player)
        {
            _positionBeforeTick = player.transform.position;
            TechBeforeTick(player);
            if (_settings.showOverlay) SampleSpikes(player, FixedStep() - 1);
        }

        void SampleSpikes(Movement player, long step)
        {
            if (step == _spikeSampleStep) return;
            _spikeSampleStep = step;
            if (Time.unscaledTime - _spikeScanTime >= SpikeScanSeconds) ScanSpikes();
            int ticks = 0, limit = int.MaxValue;
            bool killed = false, dashing = player.dashActive;
            _grazeSpikes.Clear();
            BoxCollider2D hurtbox = Hurtbox(player, _hitboxes);
            if (hurtbox != null)
            {
                var filter = new ContactFilter2D { useTriggers = true, useLayerMask = true };
                filter.layerMask = (Physics2D.GetLayerCollisionMask(hurtbox.gameObject.layer) | hurtbox.includeLayers) & ~hurtbox.excludeLayers;
                hurtbox.Overlap(filter, _overlaps);
                foreach (Collider2D c in _overlaps)
                {
                    spikeScript spike = c.GetComponent<spikeScript>();
                    if (spike == null || !spike.enabled || !_grazeSpikes.Add(spike)) continue;
                    int counter = SpikeCounter(spike);
                    ticks = Mathf.Max(ticks, counter + 1);
                    killed |= SpikeKilled(spike);
                    limit = Mathf.Min(limit, dashing && SpikeOnDash(spike) ? 1 : SpikeGrace(spike) + 1);
                }
                _grazeHurtbox = hurtbox.bounds;
            }
            if (ticks == 0 && _grazeTicks > 0)
            {
                _lastGrazeTicks = _grazeTicks;
                _lastGrazeLimit = _grazeLimit;
                _lastGrazeKilled = _grazeKilled;
                _lastGrazeTime = Time.unscaledTime;
            }
            int grazeLimit = ticks > 0 ? limit : 0;
            bool grounded = player.onGround;
            // DrawGraze's text is built again only when what it shows changed (the lingering text follows a change of ticks).
            if (ticks != _grazeTicks || grazeLimit != _grazeLimit || killed != _grazeKilled || dashing != _grazeDashing || grounded != _grazeGrounded)
                _grazeText = null;
            _grazeTicks = ticks;
            _grazeLimit = grazeLimit;
            _grazeKilled = killed;
            _grazeDashing = dashing;
            _grazeGrounded = grounded;
        }

        // The scene's spikes and checkpoints, inactive ones too: found again only after a scene load or unload, or a lab
        // injection (InjectLab adds its spikes before it sets _lab), not with every rescan.
        spikeScript[] _spikeScripts;
        checkpointScript[] _checkpointScripts;
        int _spikeScriptsGeneration;
        LabSession _spikeScriptsLab;

        /// <summary>
        /// Every SpikeScanSeconds: the colliders of the spikes and checkpoints on active objects (what a find that leaves out
        /// inactive objects returns), their shapes built again where they changed.
        /// </summary>
        void ScanSpikes()
        {
            _spikeScanTime = Time.unscaledTime;
            int generation = SceneGeneration;
            if (_spikeScripts == null || generation != _spikeScriptsGeneration || !ReferenceEquals(_lab, _spikeScriptsLab))
            {
                _spikeScriptsGeneration = generation;
                _spikeScriptsLab = _lab;
                _spikeScripts = FindObjectsByType<spikeScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                _checkpointScripts = FindObjectsByType<checkpointScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            }
            _spikeColliders.Clear();
            foreach (spikeScript spike in _spikeScripts)
                if (spike != null && spike.gameObject.activeInHierarchy) ScanColliders(spike, spike, _spikeColliders);
            _checkpointColliders.Clear();
            foreach (checkpointScript checkpoint in _checkpointScripts)
                if (checkpoint != null && checkpoint.gameObject.activeInHierarchy) ScanColliders(checkpoint, null, _checkpointColliders);
        }

        void ScanColliders(Component owner, spikeScript spike, List<SpikeCollider> into)
        {
            owner.GetComponents(_colliderBuffer);
            foreach (Collider2D c in _colliderBuffer)
            {
                if (!c.enabled || c.compositeOperation != Collider2D.CompositeOperation.None) continue;
                if (!_spikeByCollider.TryGetValue(c, out SpikeCollider entry))
                    _spikeByCollider[c] = entry = new SpikeCollider { collider = c, script = spike };
                Rigidbody2D body = c.attachedRigidbody;
                entry.moving = body != null && body.bodyType != RigidbodyType2D.Static;
                if (!entry.moving && (entry.shapeCount != c.shapeCount || entry.bounds != c.bounds)) BuildShapes(entry);
                into.Add(entry);
            }
        }

        void BuildShapes(SpikeCollider entry)
        {
            Collider2D c = entry.collider;
            entry.shapes.Clear();
            entry.shapeCount = c.shapeCount;
            entry.bounds = c.bounds;
            int count = c.GetShapes(_shapeGroup);
            Matrix4x4 m = _shapeGroup.localToWorldMatrix;
            float radiusScale = Mathf.Max(Mathf.Abs(m.lossyScale.x), Mathf.Abs(m.lossyScale.y));
            for (int i = 0; i < count; i++)
            {
                PhysicsShape2D shape = _shapeGroup.GetShape(i);
                _shapeVertices.Clear();
                _shapeGroup.GetShapeVertices(i, _shapeVertices);
                for (int v = 0; v < _shapeVertices.Count; v++) _shapeVertices[v] = m.MultiplyPoint3x4(_shapeVertices[v]);
                Vector2[] points;
                bool closed = true;
                float radius = shape.radius * radiusScale;
                switch (shape.shapeType)
                {
                    case PhysicsShapeType2D.Circle:
                        points = RoundCap(_shapeVertices[0], _shapeVertices[0] + Vector2.right, radius, 0, 16);
                        break;
                    case PhysicsShapeType2D.Capsule:
                        Vector2[] a = RoundCap(_shapeVertices[0], _shapeVertices[1], radius, 1, 8), b = RoundCap(_shapeVertices[1], _shapeVertices[0], radius, 1, 8);
                        points = new Vector2[a.Length + b.Length];
                        a.CopyTo(points, 0);
                        b.CopyTo(points, a.Length);
                        break;
                    case PhysicsShapeType2D.Edges:
                        points = _shapeVertices.ToArray();
                        closed = false;
                        break;
                    default:
                        points = _shapeVertices.ToArray();
                        break;
                }
                if (points.Length == 0) continue;
                Vector2 lo = points[0], hi = points[0];
                foreach (Vector2 p in points) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
                entry.shapes.Add(new SpikeShape { points = points, closed = closed, bounds = Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y) });
            }
        }

        static Vector2[] RoundCap(Vector2 centre, Vector2 toward, float radius, int half, int segments)
        {
            Vector2 d = toward - centre;
            float start = Mathf.Atan2(d.y, d.x) + (half == 1 ? Mathf.PI / 2 : 0);
            float sweep = half == 1 ? Mathf.PI : 2 * Mathf.PI;
            int n = half == 1 ? segments + 1 : segments;
            var points = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float t = start + sweep * i / segments;
                points[i] = centre + radius * new Vector2(Mathf.Cos(t), Mathf.Sin(t));
            }
            return points;
        }

        Material OverlayMaterial()
        {
            if (_overlayMaterial != null || _overlayMaterialMissing) return _overlayMaterial;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                _overlayMaterialMissing = true;
                Log.LogWarning("TAS overlay: shader Hidden/Internal-Colored is missing; spikes are not drawn.");
                return null;
            }
            _overlayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _overlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _overlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _overlayMaterial.SetInt("_Cull", (int)CullMode.Off);
            _overlayMaterial.SetInt("_ZWrite", 0);
            _overlayMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            return _overlayMaterial;
        }

        void DrawSpikes(float scale)
        {
            _checkpointLabels.Clear();
            Camera cam = Camera.main;
            if (cam == null || _spikeColliders.Count + _checkpointColliders.Count == 0) return;
            Material material = OverlayMaterial();
            if (material == null) return;
            Vector3 v0 = cam.ViewportToWorldPoint(Vector3.zero), v1 = cam.ViewportToWorldPoint(Vector3.one);
            Rect view = Rect.MinMaxRect(Mathf.Min(v0.x, v1.x), Mathf.Min(v0.y, v1.y), Mathf.Max(v0.x, v1.x), Mathf.Max(v0.y, v1.y));
            Rect hurt = Rect.MinMaxRect(_grazeHurtbox.min.x - 1, _grazeHurtbox.min.y - 1, _grazeHurtbox.max.x + 1, _grazeHurtbox.max.y + 1);
            Color touched = _grazeKilled ? KilledColor : GraceColor;
            float half = Mathf.Max(0.75f, scale);

            Rect pixels = cam.pixelRect;
            material.SetPass(0);
            GL.PushMatrix();
            GL.Viewport(pixels);
            GL.LoadPixelMatrix(pixels.xMin, pixels.xMax, Screen.height - pixels.yMin, Screen.height - pixels.yMax);
            GL.Begin(GL.QUADS);
            foreach (SpikeCollider entry in _spikeColliders)
            {
                if (Culled(entry, view, out _)) continue;
                bool grazed = _grazeTicks > 0 && _grazeSpikes.Contains(entry.script);
                foreach (SpikeShape shape in entry.shapes)
                {
                    if (!view.Overlaps(shape.bounds)) continue;
                    DrawShape(cam, shape, grazed && hurt.Overlaps(shape.bounds) ? touched : SpikeColor, half);
                }
            }
            Movement player = _overlayPlayer;
            Vector2 origin = CurrentOrigin();
            foreach (SpikeCollider entry in _checkpointColliders)
            {
                if (Culled(entry, view, out Bounds bounds)) continue;
                // The game writes respawnPoint = checkpoint position - origin on touch; a rebase moves both, so compare loosely.
                bool active = player != null && (player.respawnPoint - ((Vector2)entry.collider.transform.position - origin)).sqrMagnitude < 1f;
                foreach (SpikeShape shape in entry.shapes)
                    if (view.Overlaps(shape.bounds)) DrawShape(cam, shape, active ? CheckpointActiveColor : CheckpointColor, half);
                _checkpointLabels.Add(new KeyValuePair<Rect, bool>(ScreenRect(cam, bounds), active));
            }
            GL.End();
            GL.PopMatrix();
            GL.Viewport(new Rect(0, 0, Screen.width, Screen.height));
        }

        readonly List<KeyValuePair<Rect, bool>> _checkpointLabels = new List<KeyValuePair<Rect, bool>>();

        void DrawCheckpointLabels(float scale)
        {
            float height = 22 * scale;
            foreach (KeyValuePair<Rect, bool> label in _checkpointLabels)
                Label(new Rect(label.Key.xMin + 4 * scale, label.Key.yMin - height, 300 * scale, height), label.Value ? "CHECKPOINT ✓ (respawnPoint)" : "checkpoint",
                    _overlaySmall, label.Value ? CheckpointActiveColor : CheckpointColor);
        }

        /// <summary>One collider shape, faint fill and solid outline, inside DrawSpikes' GL.QUADS block.</summary>
        void DrawShape(Camera cam, SpikeShape shape, Color color, float half)
        {
            _screenPoints.Clear();
            foreach (Vector2 p in shape.points)
            {
                Vector3 s = cam.WorldToScreenPoint(p);
                _screenPoints.Add(new Vector2(s.x, Screen.height - s.y));
            }
            if (shape.closed && _screenPoints.Count >= 3)
            {
                GL.Color(new Color(color.r, color.g, color.b, 0.18f));
                for (int i = 1; i + 1 < _screenPoints.Count; i++)
                {
                    GL.Vertex(_screenPoints[0]);
                    GL.Vertex(_screenPoints[i]);
                    GL.Vertex(_screenPoints[i + 1]);
                    GL.Vertex(_screenPoints[i + 1]);
                }
            }
            GL.Color(color);
            int edges = shape.closed ? _screenPoints.Count : _screenPoints.Count - 1;
            for (int i = 0; i < edges; i++)
            {
                Vector2 a = _screenPoints[i], b = _screenPoints[(i + 1) % _screenPoints.Count];
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-6f) continue;
                Vector2 n = new Vector2(-d.y, d.x).normalized * half;
                GL.Vertex(a + n);
                GL.Vertex(b + n);
                GL.Vertex(b - n);
                GL.Vertex(a - n);
            }
        }

        /// <summary>
        /// A spike or checkpoint collider not to draw this frame. A static one's shapes lie inside the bounds they were built
        /// from (padded for rounding): off screen there, the collider isn't asked anything. A moving one's shapes are built
        /// again only when its bounds moved, not every frame.
        /// </summary>
        bool Culled(SpikeCollider entry, Rect view, out Bounds bounds)
        {
            bounds = entry.bounds;
            if (!entry.moving && entry.shapeCount >= 0 && !view.Overlaps(Rect.MinMaxRect(bounds.min.x - 1f, bounds.min.y - 1f,
                bounds.max.x + 1f, bounds.max.y + 1f))) return true;
            Collider2D c = entry.collider;
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) return true;
            bounds = c.bounds;
            if (!view.Overlaps(BoundsRect(bounds))) return true;
            if (entry.moving && (bounds != entry.bounds || c.shapeCount != entry.shapeCount)) BuildShapes(entry);
            return false;
        }

        static Rect BoundsRect(Bounds b) { return Rect.MinMaxRect(b.min.x, b.min.y, b.max.x, b.max.y); }

        void DrawGraze(float scale)
        {
            Movement player = _overlayPlayer;
            Camera cam = Camera.main;
            if (player == null || cam == null) return;
            // The text (_grazeText) is built once per change, not every frame.
            Color color;
            if (_grazeTicks > 0)
            {
                if (_grazeText == null)
                {
                    _grazeText = _grazeKilled ? "SPIKE ✗ " + _grazeTicks + "/" + _grazeLimit : "SPIKE " + _grazeTicks + "/" + _grazeLimit;
                    if (_grazeDashing) _grazeText += " · DASH";
                    if (_grazeGrounded) _grazeText += " · GROUNDED";
                }
                color = _grazeKilled ? KilledColor : GraceColor;
            }
            else if (Time.unscaledTime - _lastGrazeTime < GrazeLingerSeconds)
            {
                if (_grazeText == null) _grazeText = _lastGrazeKilled ? "SPIKE ✗" : "SPIKE GRACE " + _lastGrazeTicks + "/" + _lastGrazeLimit;
                color = _lastGrazeKilled ? KilledColor : GraceColor;
                color.a = 1f - (Time.unscaledTime - _lastGrazeTime) / GrazeLingerSeconds;
            }
            else return;
            BoxCollider2D box = Hurtbox(player, _hitboxes);
            if (box == null) return;
            Rect r = ScreenRect(cam, box.bounds);
            float width = 360 * scale, height = 28 * scale;
            Rect label = new Rect(r.center.x - width / 2, r.yMin - height - 6 * scale, width, height);
            Fill(new Rect(label.x + width / 2 - 130 * scale, label.y, 260 * scale, height), new Color(0f, 0f, 0f, 0.55f * color.a));
            Label(label, _grazeText, _overlayGraze, color);
        }

        FullTasRunDef _goalOverlayRun;
        FullTasGoal[] _goalOverlayGoals;
        // Each goal's label as drawn ("3  label"), built with the goals.
        string[] _goalOverlayLabels;

        void DrawFullTasGoals(float scale)
        {
            Camera cam = Camera.main;
            // A loop rather than List.Find: Find's predicate would be a new closure every frame.
            FullTasRunDef run = null;
            if (_fullTasRuns != null)
                foreach (FullTasRunDef candidate in _fullTasRuns)
                    if (string.Equals(candidate.name, _settings.fullTasGoalsRun, System.StringComparison.OrdinalIgnoreCase)) { run = candidate; break; }
            if (cam == null || run == null) return;
            if (!ReferenceEquals(run, _goalOverlayRun))
            {
                _goalOverlayRun = run;
                try { _goalOverlayGoals = ResolveFullTasGoals(run); }
                catch (System.InvalidOperationException) { _goalOverlayGoals = new FullTasGoal[0]; }
                _goalOverlayLabels = new string[_goalOverlayGoals.Length];
                for (int i = 0; i < _goalOverlayGoals.Length; i++) _goalOverlayLabels[i] = i + "  " + _goalOverlayGoals[i].label;
            }
            FullTasTracker tracker = _fullTasPlay != null && _fullTasPlay.run.name == run.name ? _fullTasPlay.tracker : null;
            Vector2 origin = CurrentOrigin();
            float line = Mathf.Max(1f, Mathf.Round(2 * scale)), labelHeight = 22 * scale;
            foreach (float[] b in FullTasAvoids(run.name))
            {
                var world = new Bounds(new Vector3((b[0] + b[2]) / 2f + origin.x, (b[1] + b[3]) / 2f + origin.y, 0f), new Vector3(Mathf.Abs(b[2] - b[0]), Mathf.Abs(b[3] - b[1]), 0f));
                Rect r = ScreenRect(cam, world);
                if (r.xMax < 0 || r.yMax < 0 || r.xMin > Screen.width || r.yMin > Screen.height) continue;
                Fill(r, new Color(AvoidColor.r, AvoidColor.g, AvoidColor.b, 0.18f));
                Fill(new Rect(r.xMin, r.yMin, r.width, line), AvoidColor);
                Fill(new Rect(r.xMin, r.yMax - line, r.width, line), AvoidColor);
                Fill(new Rect(r.xMin, r.yMin, line, r.height), AvoidColor);
                Fill(new Rect(r.xMax - line, r.yMin, line, r.height), AvoidColor);
                Label(new Rect(r.xMin + 4 * scale, r.yMax - labelHeight, Mathf.Max(r.width, 120 * scale), labelHeight), "avoid", _overlaySmall, AvoidColor);
            }
            for (int i = 0; i < _goalOverlayGoals.Length; i++)
            {
                FullTasGoal g = _goalOverlayGoals[i];
                if (g.kind != FullTasGoal.At && g.kind != FullTasGoal.Reward) continue;
                var world = new Bounds(new Vector3(g.x + origin.x, g.y + origin.y, 0f), new Vector3(g.w, g.h, 0f));
                Rect r = ScreenRect(cam, world);
                if (r.xMax < 0 || r.yMax < 0 || r.xMin > Screen.width || r.yMin > Screen.height) continue;
                Color color = tracker == null ? GoalColor : i < tracker.Next ? GoalReachedColor : i == tracker.Next ? GoalNextColor : GoalColor;
                Fill(r, new Color(color.r, color.g, color.b, 0.12f));
                Fill(new Rect(r.xMin, r.yMin, r.width, line), color);
                Fill(new Rect(r.xMin, r.yMax - line, r.width, line), color);
                Fill(new Rect(r.xMin, r.yMin, line, r.height), color);
                Fill(new Rect(r.xMax - line, r.yMin, line, r.height), color);
                float thick = line * 4;
                if ((g.from & FullTasGoal.FromLeft) != 0) Fill(new Rect(r.xMin - thick, r.yMin, thick, r.height), color);
                if ((g.from & FullTasGoal.FromRight) != 0) Fill(new Rect(r.xMax, r.yMin, thick, r.height), color);
                if ((g.from & FullTasGoal.FromBottom) != 0) Fill(new Rect(r.xMin, r.yMax, r.width, thick), color);
                if ((g.from & FullTasGoal.FromTop) != 0) Fill(new Rect(r.xMin, r.yMin - thick, r.width, thick), color);
                Label(new Rect(r.xMin + 4 * scale, r.yMin - labelHeight, Mathf.Max(r.width, 420 * scale), labelHeight), _goalOverlayLabels[i], _overlaySmall, color);
            }
        }

        static readonly Color GoalColor = new Color(0.3f, 0.85f, 1f, 0.95f);
        static readonly Color GoalNextColor = new Color(1f, 0.85f, 0.2f, 0.95f);
        static readonly Color GoalReachedColor = new Color(0.35f, 1f, 0.45f, 0.95f);
        static readonly Color AvoidColor = new Color(1f, 0.15f, 0.15f, 0.95f);

        static readonly Color HurtboxColor = Hex(0xa98bff, 0.95f);
        static readonly Color KilledColor = new Color(1f, 0.2f, 0.25f, 0.95f);
        static readonly Color SpikeColor = new Color(1f, 0.55f, 0.1f, 0.9f);
        static readonly Color CheckpointColor = new Color(0.3f, 0.85f, 1f, 0.9f);
        static readonly Color CheckpointActiveColor = new Color(0.35f, 1f, 0.45f, 0.95f);
        static readonly Color GraceColor = new Color(0.35f, 1f, 0.45f, 0.95f);
        static readonly Color TasColor = Hex(0xffa3b4, 1f);
        static readonly Color WallJumpColor = Hex(0x5bf08a, 1f);
        static readonly Color DashCapColor = Hex(0x6cc8ff, 1f);
        static readonly Color DoubleJumpColor = Hex(0xffd84d, 1f);
        static readonly Color BlockSwapColor = Hex(0xffab70, 1f);
        static readonly Color OmniDashColor = Hex(0xdab3ff, 1f);
        static readonly Color PanelBack = Hex(0x0d1a2d, 0.92f);
        static readonly Color PanelBand = Hex(0x12243d, 0.95f);
        static readonly Color PanelStripe = Hex(0x12243d, 0.45f);
        static readonly Color PanelLine = Hex(0x203653, 1f);
        static readonly Color PanelCurrent = Hex(0x1976ff, 0.32f);
        static readonly Color PanelText = Hex(0xf5f9ff, 1f);
        static readonly Color MutedColor = Hex(0x8fa8c4, 1f);
        static readonly Color JumpColor = Hex(0xffbf5b, 1f);
        static readonly Color HeldColor = Hex(0xffbf5b, 0.5f);
        static readonly Color DashColor = Hex(0x48b9ff, 1f);

        static Color Hex(int rgb, float alpha)
        {
            return new Color((rgb >> 16 & 0xff) / 255f, (rgb >> 8 & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);
        }

        static void Fill(Rect r, Color color)
        {
            Color saved = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = saved;
        }

        static void Label(Rect r, string text, GUIStyle style, Color color)
        {
            if (GlyphLabel(r, text, style, color)) return;
            Color saved = GUI.contentColor;
            GUI.contentColor = color;
            GUI.Label(r, text, style);
            GUI.contentColor = saved;
        }

        static readonly GUIContent OverlayMeasure = new GUIContent();

        static float Segment(float x, float y, float height, float gap, string text, GUIStyle style, Color color)
        {
            float width = GlyphWidth(text, style);
            if (width < 0f)
            {
                OverlayMeasure.text = text;
                width = style.CalcSize(OverlayMeasure).x;
            }
            Label(new Rect(x, y, width, height), text, style, color);
            return x + width + gap;
        }

        void DrawPanelTitle(float x, float y, float height, float scale)
        {
            float gap = 6 * scale;
            x = Segment(x, y, height, gap, _overlayManual ? "MANUAL" : "TAS", _overlayTitle, TasColor);
            x = Segment(x, y, height, gap, "-", _overlayTitle, MutedColor);
            x = Segment(x, y, height, gap, _overlayCourse, _overlayTitle, PanelText);
            Movement p = _overlayPlayer;
            if (p == null) return;
            x = Segment(x, y, height, gap, "-", _overlayTitle, MutedColor);
            bool any = false;
            if (p.wallJumpUnlocked) { x = Segment(x, y, height, gap, "WJ", _overlayTitle, WallJumpColor); any = true; }
            if (p.dashUnlocked) { x = Segment(x, y, height, gap, "DS", _overlayTitle, DashCapColor); any = true; }
            if (p.doubleJumpUnlocked) { x = Segment(x, y, height, gap, "DJ", _overlayTitle, DoubleJumpColor); any = true; }
            if (p.blockSwapUnlocked) { x = Segment(x, y, height, gap, "BS", _overlayTitle, BlockSwapColor); any = true; }
            if (p.omniDashUnlocked) { x = Segment(x, y, height, gap, "OM", _overlayTitle, OmniDashColor); any = true; }
            if (!any) Segment(x, y, height, gap, "NONE", _overlayTitle, MutedColor);
        }

        /// <summary>The input list; with <paramref name="withPad"/>, the pad under it showing the newest listed tick.</summary>
        void DrawInputPanel(float screenScale, bool withPad)
        {
            float scale = PanelScale(screenScale);
            if (_overlayClock == null)
                _overlayClock = _overlayTicks + " ticks · " + (_overlayTicks * Time.fixedDeltaTime).ToString("0.00") + " s";
            float pad = 20 * scale, rowHeight = InputRowHeight * scale, titleLine = 26 * scale, clockLine = 22 * scale, columnLine = InputColumnLine * scale, width = PanelWidth * scale;
            float bandHeight = InputBandHeight * scale;
            float headerHeight = bandHeight + columnLine + 1;
            int slots = InputRowSlots;
            int shown = Mathf.Min(slots, _inputRows.Count);
            Rect panel = new Rect(0f, 0f, width, headerHeight + slots * rowHeight + 12 * scale);
            Fill(panel, PanelBase);
            Fill(new Rect(panel.x, panel.y, width, bandHeight), PanelBand);
            Fill(new Rect(panel.xMax - 1, panel.y, 1, panel.height), PanelLine);
            Fill(new Rect(panel.x, panel.yMax - 1, width, 1), PanelLine);

            float x = panel.x + pad, y = panel.y + 14 * scale, inner = width - 2 * pad;
            DrawPanelTitle(x, y, titleLine, scale);
            Label(new Rect(x, y + titleLine, inner, clockLine), _overlayClock, _overlaySmall, MutedColor);
            Label(new Rect(x, y, inner, titleLine), _playbackSpeedText, _overlaySmallRight, _walkScaled ? TasColor : MutedColor);
            if (_techOrder.Count > 0) DrawTechSummary(x, y + titleLine + clockLine, inner, clockLine, scale);
            y = panel.y + bandHeight;
            Fill(new Rect(panel.x, y, width, 1), PanelLine);

            float gutter = panel.x + 6 * scale, cx = gutter + TechLanes * 16 * scale + 4 * scale;
            float tickWidth = 56 * scale, stickX = cx + 84 * scale, jumpX = cx + 200 * scale, dashX = cx + 290 * scale;
            Label(new Rect(cx, y, tickWidth, columnLine), "TICK", _overlaySmallRight, MutedColor);
            Label(new Rect(stickX, y, 110 * scale, columnLine), "STICK", _overlaySmall, MutedColor);
            Label(new Rect(jumpX, y, 80 * scale, columnLine), "JUMP", _overlaySmall, MutedColor);
            Label(new Rect(dashX, y, 80 * scale, columnLine), "DASH", _overlaySmall, MutedColor);
            Fill(new Rect(panel.x, y + columnLine, width, 1), PanelLine);
            y = panel.y + headerHeight;
            float listTop = y;

            bool live = _overlayLastTick != 0 || _overlayManual && OverlayListsManual;
            for (int i = _inputRows.Count - 1, n = 0; i >= _inputRows.Count - shown; i--, n++, y += rowHeight)
            {
                InputRow row = _inputRows[i];
                Rect band = new Rect(panel.x, y, width - 1, rowHeight);
                if (live && n == 0) Fill(band, PanelCurrent);
                else if (n % 2 == 1) Fill(band, PanelStripe);
                // A freeze row (the stick held in a dash's freeze) reads in the dash's colour, its stick across the empty button columns.
                Label(new Rect(cx, y, tickWidth, rowHeight), row.tickText, _overlayTicksStyle, row.freeze ? DashColor : n == 0 ? PanelText : MutedColor);
                Label(new Rect(stickX, y, row.freeze ? dashX - stickX : 110 * scale, rowHeight), row.stickText, _overlayText,
                    row.freeze ? DashColor : row.x == 0f && row.y == 0f ? MutedColor : PanelText);
                if (row.jump1) Label(new Rect(jumpX, y, 40 * scale, rowHeight), "J1", _overlayText, row.jump1Press ? JumpColor : HeldColor);
                if (row.jump2) Label(new Rect(jumpX + 40 * scale, y, 40 * scale, rowHeight), "J2", _overlayText, row.jump2Press ? JumpColor : HeldColor);
                if (!row.jump1 && !row.jump2 && (row.jumpRelease || row.jumpCut))
                    Label(new Rect(jumpX, y, 90 * scale, rowHeight), row.jumpRelease ? "release" : "cut", _overlayText, row.jumpRelease ? PanelText : MutedColor);
                // Letting go of one button while the other stays down still cuts the jump: the cut goes in the released button's slot.
                else if (row.jump1 != row.jump2 && (row.jumpRelease || row.jumpCut))
                    Label(new Rect(row.jump1 ? jumpX + 40 * scale : jumpX, y, 40 * scale, rowHeight), "cut", _overlayText, row.jumpRelease ? PanelText : MutedColor);
                if (row.dash1) Label(new Rect(dashX, y, 40 * scale, rowHeight), "D1", _overlayText, DashColor);
                if (row.dash2) Label(new Rect(dashX + 40 * scale, y, 40 * scale, rowHeight), "D2", _overlayText, DashColor);
            }
            for (int n = shown; n < slots; n++, y += rowHeight)
                if (n % 2 == 1) Fill(new Rect(panel.x, y, width - 1, rowHeight), PanelStripe);
            DrawTechBars(gutter, listTop, rowHeight, _inputRows[_inputRows.Count - 1].tick, shown, scale);
            // A respawn between two listed ticks: a line under the first tick after it (the list runs newest first).
            float breakLine = Mathf.Max(2f, Mathf.Round(3 * scale));
            for (int i = _inputRows.Count - 1, n = 0; n < shown - 1; i--, n++)
            {
                RowBreak rowBreak = _inputRows[i].breakBefore;
                if (rowBreak == RowBreak.None) continue;
                float bottom = listTop + (n + 1) * rowHeight;
                Fill(new Rect(panel.x, bottom - breakLine / 2f, width - 1, breakLine), rowBreak == RowBreak.Death ? KilledColor : GoalNextColor);
            }
            if (withPad) DrawInputPad(InputPadRect(scale), _inputRows[_inputRows.Count - 1], scale);
        }

        const float PadHeight = 230f, PadStickHeight = 150f;
        static readonly AccessTools.FieldRef<Movement, int> GroundType = AccessTools.FieldRefAccess<Movement, int>("currentGroundType");
        static readonly string[] PadCountNames = { "DASH", "AIR JUMP", "WALL JUMP" };
        static readonly float[] PadCountWeights = { 0.27f, 0.35f, 0.38f };
        static readonly Color MossColor = Hex(0x9ccf5a, 1f);
        readonly int[] _padCountLeft = { int.MinValue, int.MinValue, int.MinValue }, _padCountMax = new int[3];
        readonly string[] _padCountText = new string[3];

        void DrawInputPad(Rect pad, InputRow row, float scale)
        {
            Fill(pad, PanelBase);
            Fill(new Rect(pad.xMax - 1, pad.y, 1, pad.height), PanelLine);
            Fill(new Rect(pad.x, pad.y, pad.width, 1), PanelLine);
            float inset = 16 * scale, size = PadStickHeight * scale - 2 * inset;
            Rect stick = new Rect(pad.x + 20 * scale, pad.y + inset, size, size);
            Fill(stick, PanelBand);
            Fill(new Rect(stick.center.x, stick.y, 1, stick.height), PanelLine);
            Fill(new Rect(stick.x, stick.center.y, stick.width, 1), PanelLine);
            float dot = 14 * scale, reach = (size - dot) / 2f;
            bool neutral = row.x == 0f && row.y == 0f;
            Fill(new Rect(stick.center.x + row.x * reach - dot / 2f, stick.center.y - row.y * reach - dot / 2f, dot, dot), neutral ? MutedColor : PanelText);

            float gap = 8 * scale, left = stick.xMax + 28 * scale, button = (pad.xMax - 20 * scale - left - gap) / 2f, height = (size - 2 * gap) / 3f;
            float middle = stick.y + height + gap, bottom = middle + height + gap;
            PadButton(new Rect(left, stick.y, button, height), "JUMP 1", row.jump1, row.jump1Press, JumpColor);
            PadButton(new Rect(left + button + gap, stick.y, button, height), "JUMP 2", row.jump2, row.jump2Press, JumpColor);
            PadButton(new Rect(left, middle, button, height), "DASH 1", row.dash1, row.dash1Press, DashColor);
            PadButton(new Rect(left + button + gap, middle, button, height), "DASH 2", row.dash2, row.dash2Press, DashColor);
            PadButton(new Rect(left, bottom, 2 * button + gap, height), "JUMP CUT", row.jumpCut || row.jumpRelease, row.jumpRelease, PanelText);

            Movement p = _overlayPlayer;
            if (p == null) return;
            float x = pad.x + 20 * scale, inner = pad.xMax - 20 * scale - x, line = 28 * scale, y = pad.y + PadStickHeight * scale;
            float cells = inner - 2 * gap, textPad = 8 * scale, cellX = x;
            for (int i = 0; i < 3; i++)
            {
                int max = i == 0 ? p.maxAirDashes : i == 1 ? p.maxAirJumps : p.maxWallJumps;
                int count = Mathf.Max(0, i == 0 ? p.airDashesLeft : i == 1 ? p.airJumpsLeft : p.wallJumpsLeft);
                bool unlocked = i == 0 ? p.dashUnlocked : i == 1 ? p.doubleJumpUnlocked : p.wallJumpUnlocked;
                if (count != _padCountLeft[i] || max != _padCountMax[i])
                {
                    _padCountLeft[i] = count;
                    _padCountMax[i] = max;
                    _padCountText[i] = count + "/" + max;
                }
                Rect r = new Rect(cellX, y, cells * PadCountWeights[i], line);
                cellX = r.xMax + gap;
                Fill(r, PanelBand);
                Label(new Rect(r.x + textPad, r.y, r.width - 2 * textPad, r.height), PadCountNames[i], _overlaySmall, MutedColor);
                Color color = i == 0 ? DashCapColor : i == 1 ? DoubleJumpColor : WallJumpColor;
                Label(new Rect(r.x + textPad, r.y, r.width - 2 * textPad, r.height), _padCountText[i], _overlaySmallRight, unlocked && count > 0 ? color : MutedColor);
            }
            y += line + gap;
            float flag = (inner - 3 * gap) / 4f;
            bool moss = GroundType(p) == 1;
            PadButton(new Rect(x, y, flag, line), "GROUND", p.onGround, p.onGround, GraceColor);
            PadButton(new Rect(x + (flag + gap), y, flag, line), "WALL", p.OnWall, p.OnWall, WallJumpColor);
            PadButton(new Rect(x + 2 * (flag + gap), y, flag, line), "DASHING", p.dashActive, p.dashActive, DashColor);
            PadButton(new Rect(x + 3 * (flag + gap), y, flag, line), "MOSS", moss, moss, MossColor);
        }

        void PadButton(Rect r, string text, bool down, bool pressed, Color color)
        {
            Fill(r, !down ? PanelBand : new Color(color.r, color.g, color.b, pressed ? 0.9f : 0.35f));
            Label(r, text, _overlayPadButton, pressed ? PanelBack : down ? PanelText : MutedColor);
        }
    }
}
