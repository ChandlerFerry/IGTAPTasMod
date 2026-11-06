using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        /// <summary>
        /// The input trainer of a lab map with <c>"trainer": "pauselift"</c> (labmaps/pauselift-wall.json), shown while the lab
        /// is injected, in hand play and in its demo's playback alike. It lists the buttons in order with their timing, turns
        /// each one green when the game shows it worked (red when it didn't, and why), and draws the attempt on a real-time
        /// timeline from the dash press: the dash freeze (where the tilt goes), the frames the step-up lifted the dot on (where
        /// the pause has to land: the pause menu stops FixedUpdate but not Movement.Update's step-up, so a menu opened on a
        /// lifting frame keeps lifting every paused frame), and where the tilt and the pause actually came. The lifting frames
        /// are measured on every attempt, so the window it shows is the one at the player's own frame rate.
        /// All times are real time (Time.unscaledTime): the freeze and the pause stop game time, not the player's hands.
        /// </summary>
        sealed class PauseliftTrainer
        {
            public Movement Player;
            /// <summary>The cling's wall: -1 on the left, +1 on the right (the sprite faces the wall while it clings).</summary>
            public int WallSide = -1;
            public bool Clinging, WasClinging, WasDashing, WasPaused, WasFrozen;
            /// <summary>The attempt: from the frame the dash started (its freeze began) to the frame it ended with the menu shut.</summary>
            public bool Active, Done;
            public float DashAt, FreezeEnd = -1f, TurnAt = -1f, PauseAt = -1f, UnpauseAt = -1f;
            public bool FromCling, StickAtRest, Turned, Reached;
            public readonly List<float> Lifts = new List<float>();
            public readonly List<bool> LiftPaused = new List<bool>();
            /// <summary>Each lift's frame length (unscaledDeltaTime): a pause pressed anywhere in that frame lands on it.</summary>
            public readonly List<float> LiftFrames = new List<float>();
            public int PausedLifts;
            public string PauseVerdict;
            public int Attempts, Successes;
            /// <summary>The last attempt's unpaused lifting frames, from the end of the freeze (seconds), the last one's length
            /// included: the window the pause has to hit. Fixed per attempt, so the display doesn't move with every frame.</summary>
            public float WindowFrom = -1f, WindowTo = -1f;
            /// <summary>The windows of the last five attempts (from the freeze's end): their spread is the frame-to-frame wobble.</summary>
            public readonly List<Vector2> Windows = new List<Vector2>();
            public float Freeze = -1f;
            /// <summary>The start the dash came from: a cling, or flush with the back against the wall (<see cref="FlushGap"/>).</summary>
            public bool FromFlush;
            /// <summary>The back's distance to the wall this frame when facing away from it (-1 when facing it or none in reach).</summary>
            public float Gap = -1f, WasGap = -1f;
            public bool WasFlush, WasFacingAway;
            /// <summary>
            /// The reverse super that sets up a flush start: 0 not started, 1 its dash, 2 the super (momentum toward the wall),
            /// 3 carried flush with the back to the wall, 4 the pauselift dash taken from there; negative: what went wrong.
            /// </summary>
            public int RsStage;
        }

        /// <summary>The largest back gap that still counts as flush: every frame of the lifting tick lifts up to 0.2 u out, and
        /// 4 of 6 at 1 u (offline, labmaps/pauselift-wall.json).</summary>
        const float FlushGap = 1f;

        static readonly FieldInfo TrainerGroundMaskField = AccessTools.Field(typeof(Movement), "groundRaycastLayer");
        // Read every frame while the trainer is shown (BackGap): by reference, not FieldInfo.GetValue's boxed copy.
        static readonly AccessTools.FieldRef<Movement, LayerMask> TrainerGroundMask = TrainerGroundMaskField == null ? null
            : AccessTools.FieldRefAccess<Movement, LayerMask>(TrainerGroundMaskField);
        static readonly FieldInfo TrainerJumpActionField = AccessTools.Field(typeof(Movement), "jumpAction");

        /// <summary>The back's gap to the wall behind (the hitbox reaches 15.5 u behind the centre: width 41, offset 5 forward).</summary>
        static float BackGap(Movement p, int wallSide)
        {
            int mask = TrainerGroundMask != null ? TrainerGroundMask(p).value : Physics2D.DefaultRaycastLayers;
            RaycastHit2D hit = Physics2D.Raycast(p.transform.position, new Vector2(wallSide, 0f), 400f, mask);
            return hit.collider == null ? -1f : Mathf.Max(0f, hit.distance - 15.5f);
        }

        /// <summary>Follows the reverse super into a flush start (<see cref="PauseliftTrainer.RsStage"/>).</summary>
        void PumpReverseSuper(PauseliftTrainer tr, Movement p, bool facingAway, bool flush)
        {
            if (tr.RsStage == 1 || tr.RsStage == 2)
            {
                bool towardWall = p.momentum.x != 0f && Mathf.Sign(p.momentum.x) == tr.WallSide && Mathf.Abs(p.momentum.x) > 300f;
                if (tr.RsStage == 1 && towardWall) tr.RsStage = 2;
                else if (tr.RsStage == 1 && !p.dashActive) tr.RsStage = -1;
                if (tr.RsStage == 2 && !facingAway) tr.RsStage = -2;
                else if (tr.RsStage == 2 && flush) tr.RsStage = 3;
            }
            else if (tr.RsStage == 3 && !flush && !p.dashActive) tr.RsStage = -3;
            tr.WasGap = tr.Gap;
            tr.WasFlush = flush;
            tr.WasFacingAway = facingAway;
        }

        PauseliftTrainer _trainer;
        GUIStyle _trainerTitle, _trainerText, _trainerSmall, _trainerRight;
        int _trainerStyleHeight;

        static readonly FieldInfo TrainerWallCoyoteField = AccessTools.Field(typeof(Movement), "wallCoyoteFrames");
        static readonly AccessTools.FieldRef<Movement, float> TrainerWallCoyote = TrainerWallCoyoteField == null ? null
            : AccessTools.FieldRefAccess<Movement, float>(TrainerWallCoyoteField);
        static readonly FieldInfo TrainerWaitingField = AccessTools.Field(typeof(Movement), "WaitingForDashInput");
        static readonly FieldInfo TrainerDashDirectionField = AccessTools.Field(typeof(Movement), "dashDirection");
        static readonly FieldInfo TrainerFreezeField = AccessTools.Field(typeof(Movement), "dashTimeStopTime");
        static readonly AccessTools.FieldRef<Movement, float> TrainerFreeze = TrainerFreezeField == null ? null
            : AccessTools.FieldRefAccess<Movement, float>(TrainerFreezeField);
        static readonly FieldInfo TrainerDashActionField = AccessTools.Field(typeof(Movement), "dashAction");
        static readonly FieldInfo TrainerPauseActionField = AccessTools.Field(typeof(pauseMenuScript), "pauseAction");

        static readonly Color TrainerGreen = new Color(0.35f, 1f, 0.45f), TrainerRed = new Color(1f, 0.38f, 0.33f),
            TrainerWaiting = new Color(0.85f, 0.87f, 0.9f), TrainerNext = new Color(1f, 0.86f, 0.3f);

        bool LabTrainerShown { get { return _lab != null && (_trainer != null || _floatTrainer != null); } }

        /// <summary>Every rendered frame (plugin Update): follows the cling, the dash, its freeze, the pause menu and the lab's finish.</summary>
        void PumpLabTrainer()
        {
            if (_lab == null || _lab.Lab.trainer != "final-cling-float") _floatTrainer = null;
            else PumpFloatTrainer();
            if (_lab == null || _lab.Lab.trainer != "pauselift") { _trainer = null; return; }
            if (_trainer == null) _trainer = new PauseliftTrainer();
            PauseliftTrainer tr = _trainer;
            if (tr.Player == null) tr.Player = FindWorldMovement();
            Movement p = tr.Player;
            if (p == null) return;
            float now = Time.unscaledTime;
            bool paused = p.pauseMenu != null && p.pauseMenu.menuOpen;
            bool frozen = Time.timeScale == 0f && !paused;
            float wallCoyote = TrainerWallCoyote != null ? TrainerWallCoyote(p) : 0f;
            tr.Clinging = p.OnWall && wallCoyote >= p.maxWallCoyoteFrames && !p.dashActive;
            if (tr.Clinging) tr.WallSide = p.transform.localScale.x < 0f ? -1 : 1;
            bool facingAway = (p.transform.localScale.x < 0f ? -1 : 1) == -tr.WallSide;
            tr.Gap = facingAway ? BackGap(p, tr.WallSide) : -1f;
            bool flush = tr.Gap >= 0f && tr.Gap <= FlushGap;

            if (p.dashActive && !tr.WasDashing)
            {
                // The reverse super's own dash (away from the wall, from out in the open) sets up the flush start; it is not a
                // pauselift attempt.
                if (!tr.WasClinging && !tr.WasFlush && tr.WasFacingAway && tr.WasGap > 20f)
                {
                    tr.RsStage = 1;
                }
                else
                {
                    tr.Active = true;
                    tr.Done = false;
                    tr.DashAt = now;
                    tr.FreezeEnd = tr.TurnAt = tr.PauseAt = tr.UnpauseAt = -1f;
                    tr.Lifts.Clear();
                    tr.LiftPaused.Clear();
                    tr.LiftFrames.Clear();
                    tr.PausedLifts = 0;
                    tr.PauseVerdict = null;
                    tr.Turned = tr.Reached = false;
                    tr.FromCling = tr.WasClinging;
                    tr.FromFlush = tr.WasFlush;
                    if (tr.FromFlush && tr.RsStage == 3) tr.RsStage = 4;
                    // A dash pressed with the stick at rest waits for the freeze's stick (Movement.getPlayerControlledInput); any
                    // other stick sets its direction at once, and from a cling it flips away from the wall for good.
                    tr.StickAtRest = TrainerWaitingField != null && (bool)TrainerWaitingField.GetValue(p);
                }
            }
            PumpReverseSuper(tr, p, facingAway, flush);
            if (tr.Active && !tr.Done)
            {
                if (frozen && tr.FreezeEnd < 0f && tr.TurnAt < 0f && p.MoveAxis.x != 0f && Mathf.Sign(p.MoveAxis.x) == tr.WallSide) tr.TurnAt = now;
                if (!frozen && tr.WasFrozen && tr.FreezeEnd < 0f)
                {
                    tr.FreezeEnd = now;
                    tr.Freeze = now - tr.DashAt;
                    Vector2 dash = TrainerDashDirectionField != null ? (Vector2)TrainerDashDirectionField.GetValue(p) : Vector2.zero;
                    tr.Turned = dash.x != 0f && Mathf.Sign(dash.x) == tr.WallSide;
                }
                if (paused && !tr.WasPaused && tr.PauseAt < 0f) tr.PauseAt = now;
                if (!paused && tr.WasPaused && tr.UnpauseAt < 0f) tr.UnpauseAt = now;
                if (InLabFinish(p)) tr.Reached = true;
                if (!p.dashActive && !paused && now - tr.DashAt > 0.25f) FinishPauseliftAttempt(tr);
            }
            tr.WasDashing = p.dashActive;
            tr.WasPaused = paused;
            tr.WasFrozen = frozen;
            tr.WasClinging = tr.Clinging;
        }

        /// <summary>A step-up (BeforeSteppedUp): one 56 u lift, paused or not.</summary>
        void TrainerSteppedUp(Movement player)
        {
            PauseliftTrainer tr = _trainer;
            if (tr == null || !tr.Active || tr.Done || player != tr.Player) return;
            bool paused = player.pauseMenu != null && player.pauseMenu.menuOpen;
            tr.Lifts.Add(Time.unscaledTime);
            tr.LiftPaused.Add(paused);
            tr.LiftFrames.Add(Time.unscaledDeltaTime);
            if (paused) tr.PausedLifts++;
        }

        bool InLabFinish(Movement p)
        {
            float[] f = _lab.Lab.finish, anchor = _lab.Lab.anchor;
            if (f == null || f.Length < 4 || anchor == null) return false;
            Vector2 at = (Vector2)p.transform.position - CurrentOrigin() - new Vector2(anchor[0], anchor[1]);
            return Mathf.Abs(at.x - f[0]) <= f[2] / 2f + 20f && Mathf.Abs(at.y - f[1]) <= f[3] / 2f + 27f;
        }

        void FinishPauseliftAttempt(PauseliftTrainer tr)
        {
            tr.Done = true;
            tr.Attempts++;
            if (tr.Reached) tr.Successes++;
            // The unpaused lifting frames of this attempt, timed from the end of the freeze (the freeze's own length is rounded up
            // to a whole frame, so timing from the dash press would move with it): where a pause would have had to land.
            float from = -1f, to = -1f;
            for (int i = 0; i < tr.Lifts.Count && tr.FreezeEnd >= 0f; i++)
            {
                if (tr.LiftPaused[i]) continue;
                float at = tr.Lifts[i] - tr.FreezeEnd;
                if (tr.PauseAt >= 0f && tr.UnpauseAt >= 0f && tr.Lifts[i] > tr.UnpauseAt) at -= tr.UnpauseAt - tr.PauseAt;
                if (from < 0f || at < from) from = at;
                if (at + tr.LiftFrames[i] > to) to = at + tr.LiftFrames[i];
            }
            if (from >= 0f)
            {
                tr.WindowFrom = from;
                tr.WindowTo = to;
                tr.Windows.Add(new Vector2(from, to));
                if (tr.Windows.Count > 5) tr.Windows.RemoveAt(0);
            }
            if (tr.PausedLifts > 0) tr.PauseVerdict = "on time · " + tr.PausedLifts + " lifts";
            else if (tr.PauseAt < 0f) tr.PauseVerdict = "no pause";
            else if (!tr.Turned) tr.PauseVerdict = "dash didn't turn";
            else if (tr.FreezeEnd < 0f || tr.PauseAt < tr.FreezeEnd) tr.PauseVerdict = "paused in the freeze: too early";
            else
            {
                float pause = tr.PauseAt - tr.FreezeEnd;
                if (from >= 0f && pause < from) tr.PauseVerdict = "early " + Ms(from - pause);
                else if (to >= 0f && pause > to) tr.PauseVerdict = "late " + Ms(pause - to);
                else tr.PauseVerdict = "missed the lift";
            }
        }

        static string Ms(float seconds)
        {
            return Mathf.RoundToInt(seconds * 1000f).ToString(CultureInfo.InvariantCulture) + " ms";
        }

        static string Keys(InputAction action, string fallback)
        {
            string keys = action == null ? null : action.GetBindingDisplayString();
            return string.IsNullOrEmpty(keys) ? fallback : keys;
        }

        void BuildTrainerStyles(float scale)
        {
            _trainerStyleHeight = Screen.height;
            _trainerTitle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            _trainerText = new GUIStyle(_trainerTitle) { fontSize = Mathf.RoundToInt(18 * scale), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            _trainerSmall = new GUIStyle(_trainerText) { fontSize = Mathf.RoundToInt(13 * scale), fontStyle = FontStyle.Normal };
            _trainerRight = new GUIStyle(_trainerTitle) { alignment = TextAnchor.MiddleRight };
        }

        /// <summary>
        /// The trainer panel, top centre: one chip per button in order (grey to do, green done, red failed), the one message that
        /// matters (the first thing that went wrong, or the result), and the attempt's timeline. Everything it shows is fixed
        /// per attempt (no per-frame numbers), so nothing jumps while the menu is open.
        /// </summary>
        void DrawLabTrainer()
        {
            if (_floatTrainer != null) { DrawFloatTrainer(); return; }
            PauseliftTrainer tr = _trainer;
            Movement p = tr.Player;
            if (p == null) return;
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            if (_trainerTitle == null || _trainerStyleHeight != Screen.height) BuildTrainerStyles(scale);
            // The freeze as it happened on this dash once it has ended (live), else the last one measured, else the game's setting.
            float freeze = tr.Active && tr.FreezeEnd >= 0f ? tr.FreezeEnd - tr.DashAt
                : tr.Freeze > 0f ? tr.Freeze : TrainerFreeze != null ? TrainerFreeze(p) : 0.05f;
            bool measured = tr.WindowFrom >= 0f;
            // The pause window, timed from the end of the freeze. Before any attempt: the lifts come in the frames before the
            // dash's second physics tick after the freeze.
            float from = measured ? tr.WindowFrom : Time.fixedDeltaTime, to = measured ? tr.WindowTo : 2f * Time.fixedDeltaTime;
            string dashKeys = Keys(TrainerDashActionField?.GetValue(p) as InputAction, "Dash");
            string pauseKeys = Keys(p.pauseMenu != null ? TrainerPauseActionField?.GetValue(p.pauseMenu) as InputAction : null, "Pause");
            string wall = tr.WallSide < 0 ? "←" : "→";
            bool attempt = tr.Active;

            bool start = tr.FromCling || tr.FromFlush;
            int cling = tr.Clinging || tr.Gap >= 0f && tr.Gap <= FlushGap || attempt && start ? 1 : attempt ? -1 : 0;
            int dash = attempt ? (start && tr.StickAtRest ? 1 : -1) : 0;
            int tilt = tr.FreezeEnd >= 0f ? (tr.Turned ? 1 : -1) : 0;
            int pause = tr.PausedLifts > 0 ? 1 : tr.Done ? -1 : 0;
            int top = tr.Reached ? 1 : tr.Done ? -1 : 0;
            string message = !attempt ? "pause " + Ms(from) + "–" + Ms(to) + " after the freeze ends"
                : !start ? "dash from the cling, or flush with your back to the wall" : !tr.StickAtRest ? "let go of the stick before " + dashKeys
                : tr.FreezeEnd >= 0f && !tr.Turned ? (tr.TurnAt >= 0f ? "tilt came after the freeze" : "no tilt in the freeze")
                : tr.Done && tr.PausedLifts == 0 ? tr.PauseVerdict
                : tr.Done ? (tr.Reached ? tr.PauseVerdict + " · top!" : "unpaused before the top")
                : tr.PausedLifts > 0 ? tr.PausedLifts + " lifts · hold the menu" : "";
            Color messageColor = top > 0 ? TrainerGreen : tr.Done || cling < 0 || dash < 0 || tilt < 0 ? TrainerRed : TrainerWaiting;

            string[] chips = { wall + " cling / flush", dashKeys, wall + " in freeze", pauseKeys + " +" + Ms(from) + (measured ? "" : "≈"), "hold " + wall };
            int[] states = { cling, dash, tilt, pause, top };
            float pad = 12f * scale, chipsW = ChipsWidth(chips, scale);
            float width = Mathf.Max(chipsW, 560f * scale) + 2f * pad, height = pad * 2f + 22f * scale + 34f * scale + 26f * scale + 40f * scale;
            var box = new Rect((Screen.width - width) / 2f, 12f * scale, width, height);
            Fill(box, new Color(0.02f, 0.04f, 0.08f, 0.8f));
            float x = box.x + pad, y = box.y + pad, w = width - 2f * pad;
            Label(new Rect(x, y, w, 22f * scale), "PAUSELIFT" + (PlaybackActive ? " · DEMO" : ""), _trainerTitle, new Color(1f, 1f, 1f, 0.7f));
            if (tr.Attempts > 0)
                Label(new Rect(x, y, w, 22f * scale), tr.Successes + " / " + tr.Attempts, _trainerRight, new Color(1f, 1f, 1f, 0.7f));
            y += 22f * scale + 4f * scale;
            DrawChips(box.x + (width - chipsW) / 2f, y, chips, states, scale);
            y += 34f * scale;
            Label(new Rect(x, y, w, 26f * scale), message, _trainerText, messageColor);
            y += 26f * scale;
            DrawTrainerTimeline(new Rect(x, y, w, 40f * scale), tr, freeze, from, to);
            DrawReverseSuper(tr, p, new Rect(box.x, box.yMax + 8f * scale, width, 0f), dashKeys, scale);
        }

        /// <summary>
        /// Below the pauselift: the reverse super that starts it flush with the back against the wall, where the pause lifts on
        /// every frame of the lifting tick instead of the last one (offline, 6 of 6 frames at 280 fps against 1 from a cling).
        /// Its chips follow the setup live: out in the open facing away, the reverse super, the ride back to the wall (the gap).
        /// </summary>
        void DrawReverseSuper(PauseliftTrainer tr, Movement p, Rect at, string dashKeys, float scale)
        {
            string jumpKeys = Keys(TrainerJumpActionField?.GetValue(p) as InputAction, "Jump");
            string wall = tr.WallSide < 0 ? "←" : "→", away = tr.WallSide < 0 ? "→" : "←";
            int s = tr.RsStage;
            bool ready = p.onGround && tr.Gap >= 40f;
            string gap = tr.Gap >= 0f ? tr.Gap.ToString(tr.Gap < 10f ? "0.0" : "0", CultureInfo.InvariantCulture) + " u" : "–";
            string[] chips = { "stand " + away + " facing out", dashKeys + " " + away + " · hold " + wall + " · " + jumpKeys, "let go · gap " + gap, "then the pauselift ↑" };
            int[] states = { s >= 1 || ready ? 1 : 0, s >= 2 ? 1 : s == -1 ? -1 : 0, s >= 3 ? 1 : s == -2 || s == -3 ? -1 : 0, s == 4 ? 1 : 0 };
            string message = s == -1 ? "no super: hold " + wall + " through the dash and press " + jumpKeys + " on it"
                : s == -2 ? "you turned toward the wall: let go of the stick after the super"
                : s == -3 ? "slid off the wall: the dash has to start flush"
                : s == 3 ? "flush · now " + dashKeys + " with the stick released, " + wall + " in the freeze, pause any frame"
                : s == 4 ? "flush start: the whole tick lifts"
                : s >= 1 ? "keep facing out, the momentum carries you back" : "OR, for a wider pause: start about 100 u out, facing away from the wall";
            float pad = 12f * scale, chipsW = ChipsWidth(chips, scale);
            float width = Mathf.Max(chipsW + 2f * pad, at.width), height = pad * 2f + 22f * scale + 34f * scale + 26f * scale;
            var box = new Rect(at.x + (at.width - width) / 2f, at.y, width, height);
            Fill(box, new Color(0.02f, 0.04f, 0.08f, 0.8f));
            float x = box.x + pad, y = box.y + pad, w = width - 2f * pad;
            Label(new Rect(x, y, w, 22f * scale), "REVERSE SUPER PAUSELIFT · wider pause", _trainerTitle, new Color(1f, 1f, 1f, 0.7f));
            y += 22f * scale + 4f * scale;
            DrawChips(box.x + (width - chipsW) / 2f, y, chips, states, scale);
            y += 34f * scale;
            Label(new Rect(x, y, w, 26f * scale), message, _trainerText, s < 0 ? TrainerRed : s >= 3 ? TrainerGreen : TrainerWaiting);
        }

        // The chips' measuring content, its text set per chip: a new GUIContent per chip was garbage on every drawn frame.
        static readonly GUIContent TrainerChipContent = new GUIContent();

        float ChipsWidth(string[] chips, float scale)
        {
            float w = -10f * scale;
            foreach (string chip in chips)
            {
                TrainerChipContent.text = chip;
                w += _trainerText.CalcSize(TrainerChipContent).x + 28f * scale + 10f * scale;
            }
            return w;
        }

        /// <summary>One row of chips: grey to do, green done, red failed.</summary>
        void DrawChips(float x, float y, string[] chips, int[] states, float scale)
        {
            for (int i = 0; i < chips.Length; i++)
            {
                TrainerChipContent.text = chips[i];
                float width = _trainerText.CalcSize(TrainerChipContent).x + 28f * scale;
                Color fill = states[i] > 0 ? new Color(0.15f, 0.6f, 0.25f, 0.95f) : states[i] < 0 ? new Color(0.7f, 0.18f, 0.15f, 0.95f) : new Color(1f, 1f, 1f, 0.12f);
                var chip = new Rect(x, y, width, 34f * scale);
                Fill(chip, fill);
                Label(chip, chips[i], _trainerText, Color.white);
                x += width + 10f * scale;
            }
        }

        /// <summary>
        /// Real time from the dash press: the freeze (blue: tilt here; this dash's own once it ends), the pause window placed after
        /// the freeze's end (gold: the last dash's lifting frames; faint: the spread of the last five), every lift (cyan, paused
        /// ones taller), the tilt and the pause as they came (green on time, red off), and a cursor while the attempt runs.
        /// Paused time is taken out, so the scale stays the game's; the scale itself never changes.
        /// </summary>
        void DrawTrainerTimeline(Rect r, PauseliftTrainer tr, float freeze, float from, float to)
        {
            float setting = TrainerFreeze != null ? TrainerFreeze(tr.Player) : 0.05f;
            float span = setting + 0.025f + 3f * Time.fixedDeltaTime + 0.02f;
            float barY = r.y + r.height * 0.1f, barH = r.height * 0.45f;
            Fill(new Rect(r.x, barY, r.width, barH), new Color(1f, 1f, 1f, 0.08f));
            float X(float seconds) { return r.x + Mathf.Clamp01(seconds / span) * r.width; }
            Fill(new Rect(X(0f), barY, X(freeze) - X(0f), barH), new Color(0.25f, 0.55f, 1f, 0.55f));
            if (tr.Windows.Count > 1)
            {
                float lo = tr.Windows.Min(v => v.x), hi = tr.Windows.Max(v => v.y);
                Fill(new Rect(X(freeze + lo), barY, Mathf.Max(X(freeze + hi) - X(freeze + lo), 3f), barH), new Color(1f, 0.8f, 0.2f, 0.25f));
            }
            float w0 = X(freeze + from), w1 = Mathf.Max(X(freeze + to), w0 + 3f);
            Fill(new Rect(w0, barY, w1 - w0, barH), new Color(1f, 0.8f, 0.2f, 0.85f));
            if (!tr.Active) return;
            float pausedFor = tr.PauseAt >= 0f ? (tr.UnpauseAt >= 0f ? tr.UnpauseAt : Time.unscaledTime) - tr.PauseAt : 0f;
            float Game(float real) { return real - tr.DashAt - (tr.PauseAt >= 0f && real > tr.PauseAt ? Mathf.Min(real - tr.PauseAt, pausedFor) : 0f); }
            for (int i = 0; i < tr.Lifts.Count; i++)
            {
                float lx = X(tr.LiftPaused[i] ? tr.PauseAt - tr.DashAt : Game(tr.Lifts[i]));
                float lh = tr.LiftPaused[i] ? barH : barH * 0.5f;
                Fill(new Rect(lx, barY + barH - lh, 2f, lh), new Color(0.3f, 1f, 1f, 0.9f));
            }
            if (tr.TurnAt >= 0f) Marker(r, X(tr.TurnAt - tr.DashAt), barY, barH, "tilt", tr.FreezeEnd < 0f || tr.Turned ? TrainerGreen : TrainerRed);
            if (tr.PauseAt >= 0f) Marker(r, X(tr.PauseAt - tr.DashAt), barY, barH, "pause", tr.PausedLifts > 0 ? TrainerGreen : tr.Done ? TrainerRed : TrainerNext);
            if (!tr.Done && tr.PauseAt < 0f) Fill(new Rect(X(Game(Time.unscaledTime)), barY - 3f, 2f, barH + 6f), Color.white);
        }

        void Marker(Rect r, float x, float barY, float barH, string text, Color color)
        {
            Fill(new Rect(x - 1.5f, barY - 3f, 3f, barH + 6f), color);
            Label(new Rect(x - 50f, barY + barH, 100f, r.yMax - barY - barH), text, _trainerSmall, color);
        }
    }
}
