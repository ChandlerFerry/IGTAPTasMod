using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        sealed class FloatTrainer
        {
            public Movement Player;
            public int MossSide = -1;
            public bool Active, Done, WasFlag, EarlyPress, Reached;
            public int ClingTick = -1, TurnTick = -1, RefillTick = -1, JumpTick = -1, FlagEndTick = -1, LastTick = -1;
            public float JumpSpeed, Peak;
            public int Attempts, Successes;
        }

        const int FloatTurnWindow = 15, FloatJumpWindow = 8;

        static readonly FieldInfo TrainerFinalClingField = AccessTools.Field(typeof(Movement), "isFinalWallClingActive");
        static readonly FieldInfo TrainerFinalClingTimeField = AccessTools.Field(typeof(Movement), "finalWallClingTime");
        static readonly FieldInfo TrainerJumpBufferField = AccessTools.Field(typeof(Movement), "jumpBuffer");
        static readonly AccessTools.FieldRef<Movement, bool> TrainerFinalCling = TrainerFinalClingField == null ? null
            : AccessTools.FieldRefAccess<Movement, bool>(TrainerFinalClingField);
        static readonly AccessTools.FieldRef<Movement, bool> TrainerJumpBuffer = TrainerJumpBufferField == null ? null
            : AccessTools.FieldRefAccess<Movement, bool>(TrainerJumpBufferField);
        static readonly AccessTools.FieldRef<Movement, float> TrainerFinalClingTime = TrainerFinalClingTimeField == null ? null
            : AccessTools.FieldRefAccess<Movement, float>(TrainerFinalClingTimeField);

        FloatTrainer _floatTrainer;

        static int FloatTicks(Movement p)
        {
            float time = TrainerFinalClingTime != null ? TrainerFinalClingTime(p) : 0.7f;
            return Mathf.RoundToInt(time / Time.fixedDeltaTime);
        }

        void PumpFloatTrainer()
        {
            if (_floatTrainer == null) _floatTrainer = new FloatTrainer();
            FloatTrainer tr = _floatTrainer;
            if (tr.Player == null) tr.Player = FindWorldMovement();
            Movement p = tr.Player;
            if (p == null) return;
            int tick = Mathf.RoundToInt(Time.fixedTime / Time.fixedDeltaTime);
            if (tick == tr.LastTick) return;
            tr.LastTick = tick;
            bool flag = TrainerFinalCling != null && TrainerFinalCling(p);
            int facing = p.transform.localScale.x < 0f ? -1 : 1;

            if (flag && !tr.WasFlag && p.wallJumpsLeft < 0)
            {
                tr.Active = true;
                tr.Done = tr.EarlyPress = tr.Reached = false;
                tr.ClingTick = tick;
                tr.TurnTick = tr.RefillTick = tr.JumpTick = tr.FlagEndTick = -1;
                tr.JumpSpeed = 0f;
                tr.Peak = float.MinValue;
                tr.MossSide = facing;
            }
            if (tr.Active && !tr.Done)
            {
                if (tr.TurnTick < 0 && facing != tr.MossSide) tr.TurnTick = tick;
                if (flag && tr.RefillTick < 0 && p.wallJumpsLeft > 0) tr.RefillTick = tick;
                if (flag && tr.RefillTick >= 0 && tr.JumpTick < 0 && !p.onGround && p.Velocity.y > 600f)
                {
                    tr.JumpTick = tick;
                    tr.JumpSpeed = p.Velocity.y;
                }
                if (flag && tr.TurnTick < 0 && TrainerJumpBuffer != null && TrainerJumpBuffer(p)) tr.EarlyPress = true;
                if (!flag && tr.WasFlag) tr.FlagEndTick = tick;
                float y = p.transform.position.y - CurrentOrigin().y - (_lab.Lab.anchor != null ? _lab.Lab.anchor[1] : 0f);
                if (tr.JumpTick >= 0 && y > tr.Peak) tr.Peak = y;
                if (InLabFinish(p)) tr.Reached = true;
                if (!flag && (tr.Reached || p.Velocity.y <= 0f || p.onGround || tr.JumpTick < 0))
                {
                    tr.Done = true;
                    tr.Attempts++;
                    if (tr.Reached) tr.Successes++;
                }
            }
            tr.WasFlag = flag;
        }

        void DrawFloatTrainer()
        {
            FloatTrainer tr = _floatTrainer;
            Movement p = tr.Player;
            if (p == null) return;
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            if (_trainerTitle == null || _trainerStyleHeight != Screen.height) BuildTrainerStyles(scale);
            string jumpKeys = Keys(TrainerJumpActionField?.GetValue(p) as InputAction, "Jump");
            int now = Mathf.RoundToInt(Time.fixedTime / Time.fixedDeltaTime), floatTicks = FloatTicks(p);
            string moss = tr.MossSide < 0 ? "←" : "→", metal = tr.MossSide < 0 ? "→" : "←";
            bool attempt = tr.Active;
            bool noJumps = p.wallJumpsLeft <= 0;

            int cling = tr.ClingTick >= 0 && attempt ? 1 : 0;
            int zero = noJumps || attempt && !tr.Done ? 1 : -1;
            int turn = tr.RefillTick >= 0 ? 1 : tr.Done ? -1 : 0;
            int jump = tr.JumpTick >= 0 ? 1 : tr.EarlyPress || tr.Done ? -1 : 0;
            int top = tr.Reached ? 1 : tr.Done ? -1 : 0;
            int since = attempt ? now - tr.ClingTick : 0;
            string message;
            if (!attempt) message = noJumps ? "let go of the stick: cling to the moss" : "a quick restart keeps your wall jumps: place the lab start again";
            else if (tr.Done && tr.Reached)
                message = "top! refill +" + (tr.RefillTick - tr.ClingTick) + " · jump +" + (tr.JumpTick - tr.ClingTick) + " · floated "
                    + (floatTicks - (tr.JumpTick - tr.ClingTick)) + " ticks at " + Mathf.RoundToInt(tr.JumpSpeed);
            else if (tr.Done && tr.RefillTick < 0) message = "the 0.7 s ran out on the moss: " + metal + " within " + FloatTurnWindow + " ticks";
            else if (tr.EarlyPress && tr.JumpTick < 0) message = "jump came before the turn: it's spent on the moss";
            else if (tr.Done && tr.JumpTick < 0) message = "no jump before the 0.7 s ran out: " + jumpKeys + " with the turn or up to " + FloatJumpWindow + " ticks after";
            else if (tr.Done) message = "floated " + (floatTicks - (tr.JumpTick - tr.ClingTick)) + " ticks, peak " + Mathf.RoundToInt(tr.Peak) + " · missed the box: let go of the stick";
            else if (tr.RefillTick < 0) message = "clinging " + since + " ticks · now " + metal + " toward the metal";
            else if (tr.JumpTick < 0) message = "refilled on +" + (tr.RefillTick - tr.ClingTick) + " · now " + jumpKeys + " (" + (floatTicks - since) + " ticks of float left)";
            else message = "floating · " + (floatTicks - since) + " ticks left · let go of the stick";
            Color messageColor = top > 0 ? TrainerGreen : tr.Done || zero < 0 || jump < 0 ? TrainerRed : TrainerWaiting;

            string[] chips = { "0 wall jumps", "let go · cling " + moss, metal + " ≤" + FloatTurnWindow + " ticks", jumpKeys + " +0–" + FloatJumpWindow, "let go ↑" };
            int[] states = { zero, cling, turn, jump, top };
            float pad = 12f * scale, chipsW = ChipsWidth(chips, scale);
            float width = Mathf.Max(chipsW, 560f * scale) + 2f * pad, height = pad * 2f + 22f * scale + 34f * scale + 26f * scale + 40f * scale;
            var box = new Rect((Screen.width - width) / 2f, 12f * scale, width, height);
            Fill(box, new Color(0.02f, 0.04f, 0.08f, 0.8f));
            float x = box.x + pad, y = box.y + pad, w = width - 2f * pad;
            Label(new Rect(x, y, w, 22f * scale), "FINAL-CLING FLOAT" + (PlaybackActive ? " · DEMO" : ""), _trainerTitle, new Color(1f, 1f, 1f, 0.7f));
            if (tr.Attempts > 0)
                Label(new Rect(x, y, w, 22f * scale), tr.Successes + " / " + tr.Attempts, _trainerRight, new Color(1f, 1f, 1f, 0.7f));
            y += 22f * scale + 4f * scale;
            DrawChips(box.x + (width - chipsW) / 2f, y, chips, states, scale);
            y += 34f * scale;
            Label(new Rect(x, y, w, 26f * scale), message, _trainerText, messageColor);
            y += 26f * scale;
            DrawFloatTimeline(new Rect(x, y, w, 40f * scale), tr, floatTicks, now);
        }

        void DrawFloatTimeline(Rect r, FloatTrainer tr, int floatTicks, int now)
        {
            float span = floatTicks + 10f;
            float barY = r.y + r.height * 0.1f, barH = r.height * 0.45f;
            Fill(new Rect(r.x, barY, r.width, barH), new Color(1f, 1f, 1f, 0.08f));
            float X(float ticks) { return r.x + Mathf.Clamp01(ticks / span) * r.width; }
            Fill(new Rect(X(0f), barY, X(floatTicks) - X(0f), barH), new Color(0.25f, 0.55f, 1f, 0.35f));
            Fill(new Rect(X(0f), barY + barH * 0.6f, X(FloatTurnWindow) - X(0f), barH * 0.4f), new Color(1f, 0.8f, 0.2f, 0.4f));
            int turned = tr.Active && tr.TurnTick >= 0 ? tr.TurnTick - tr.ClingTick : 1;
            Fill(new Rect(X(turned), barY, Mathf.Max(X(turned + FloatJumpWindow + 1) - X(turned), 3f), barH * 0.6f), new Color(1f, 0.8f, 0.2f, 0.85f));
            if (!tr.Active) return;
            if (tr.JumpTick >= 0)
            {
                int end = tr.FlagEndTick >= 0 ? tr.FlagEndTick : now;
                Fill(new Rect(X(tr.JumpTick - tr.ClingTick), barY + barH - 4f, X(end - tr.ClingTick) - X(tr.JumpTick - tr.ClingTick), 4f), new Color(0.3f, 1f, 1f, 0.9f));
            }
            if (tr.TurnTick >= 0) Marker(r, X(tr.TurnTick - tr.ClingTick), barY, barH, "turn", tr.RefillTick >= 0 ? TrainerGreen : TrainerNext);
            if (tr.RefillTick >= 0 && tr.RefillTick != tr.TurnTick) Marker(r, X(tr.RefillTick - tr.ClingTick), barY, barH, "refill", TrainerGreen);
            if (tr.JumpTick >= 0) Marker(r, X(tr.JumpTick - tr.ClingTick), barY, barH, "jump", tr.Done && !tr.Reached ? TrainerRed : TrainerGreen);
            if (!tr.Done) Fill(new Rect(X(now - tr.ClingTick), barY - 3f, 2f, barH + 6f), Color.white);
        }
    }
}
