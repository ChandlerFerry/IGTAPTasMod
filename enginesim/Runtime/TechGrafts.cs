using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>Where a run's tech lives: the ticks where the player's contact changes. Recorded by <see cref="ContactLog"/>;
    /// <see cref="OfflineSolver"/>'s grafts and the mined tech grafts (<see cref="TechGrafts"/>) both key on it. A Left/Right
    /// kind names the side of the thing touched (-x: Left), except a spring's, which names the side it launches toward.</summary>
    public enum Contact : byte
    {
        Died, LeftGround, Landed, TouchedWall, LeftWall, CornerHyper, WallBehindLeft, WallBehindRight, WallAheadLeft, WallAheadRight,
        NearGateLeft, NearGateRight, WallAboveLeft, WallAboveRight,
        // Per physics step (ContactLog.Step): the game's own calls and the player's touches.
        SpringHitLeft, SpringHitRight, SpringHitUp, SpringHitDown, SpikeTouched, SpikeCleared, OverhangLeft, OverhangRight,
        SlopeLeft, SlopeRight, DoubleJump, DashJump, WallBoostLeft, WallBoostRight, Paused, SteppedUpLeft, SteppedUpRight
    }

    /// <summary>The unlocks a run's player holds now, as the <see cref="Caps"/> they make (a Full-TAS window's, what its search
    /// may press): the Movement's own fields, and the refill orbs and zip movers from the run's global upgrades (its own
    /// economy's when it has one: a buy mid-run, like the Omni Dash box's refresh orb, changes only those).</summary>
    public static class PlayerCaps
    {
        public static Caps Of(Simulation sim)
        {
            Movement player = sim.Player;
            var caps = new Caps
            {
                WallJump = player.wallJumpUnlocked,
                Dashes = !player.dashUnlocked ? 0 : Math.Max(1, player.maxAirDashes),
                AirJumps = !player.doubleJumpUnlocked ? 0 : Math.Max(1, player.maxAirJumps),
                BlockSwap = player.blockSwapUnlocked,
                Omni = player.omniDashUnlocked,
            };
            Economy economy = sim.Shop?.Economy;
            Dictionary<globalStats.globalUpgradeSet, double> upgrades = globalStats.globalUpgradeDict;
            double Global(globalStats.globalUpgradeSet upgrade)
            {
                if (economy != null) return economy.GlobalOf(upgrade);
                return upgrades != null && upgrades.TryGetValue(upgrade, out double value) ? value : 0.0;
            }
            caps.Orbs = Global(globalStats.globalUpgradeSet.unlockJiggleDrops) + Global(globalStats.globalUpgradeSet.moreRefreshOrbs) > 0;
            caps.Zip = Global(globalStats.globalUpgradeSet.zipMoversUnlocked) > 0;
            return caps;
        }
    }

    /// <summary>The ray probes a contact is made of, and the Movement getters they need: the same detection the solver's
    /// grafts and the demo miner share.</summary>
    public static class ContactRays
    {
        internal static readonly AccessTools.FieldRef<Movement, LayerMask> GroundLayer = AccessTools.FieldRefAccess<Movement, LayerMask>("groundRaycastLayer");
        internal static readonly AccessTools.FieldRef<Movement, bool> CanHyper = AccessTools.FieldRefAccess<Movement, bool>("canHyper");
        internal static readonly AccessTools.FieldRef<Movement, float> CoyoteFrames = AccessTools.FieldRefAccess<Movement, float>("coyoteFrames");

        /// <summary>The side of a wall at the player's back that isn't clip-ready yet because its bottom is still above the ground
        /// rays (the back ray at y + 19 or y + 45 hits it within 12 u, the one at y - 18 doesn't), 0 for none: rising up
        /// the wall's side with the stick at rest makes it clip-ready.</summary>
        public static float WallBehindAbove(Movement player)
        {
            if (player.onGround || player.dashActive) return 0f;
            float back = player.facingRight ? -1f : 1f;
            Vector3 p = player.transform.position;
            var dir = new Vector2(back, 0f);
            int layer = GroundLayer(player);
            return Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y + 19f), dir, 12f, layer)
                || Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y + 45f), dir, 12f, layer) ? back : 0f;
        }

        /// <summary>The side of a wall behind the player (-1: at -x, 1: at +x) that a wall clip can use now, 0 for none. The turn
        /// swings the hitbox's back edge (x ∓ 15.5, offset 5, width 41) 10 u into the wall, and the front ground ray (x ∓ 25
        /// after the turn) grounds the player from inside it while Velocity.y is under 200; a clip needs the back edge
        /// within ~8 u, so the ray looks 12 u out from x ∓ 15 at the ground rays' height (y - 18).</summary>
        public static float WallBehind(Movement player)
        {
            if (player.onGround || player.dashActive || player.Velocity.y >= 580f) return 0f;
            float back = player.facingRight ? -1f : 1f;
            Vector3 p = player.transform.position;
            return Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y - 18f), new Vector2(back, 0f), 12f, GroundLayer(player)) ? back : 0f;
        }

        /// <summary>The side of a wall in front of the player within 12 u of the hitbox's front edge (x ± 25.5), at the wall rays'
        /// heights (y + 19, y - 18) or just over the head (y + 45: a wall whose bottom the player is rising up to), 0 for none.</summary>
        public static float WallAhead(Movement player)
        {
            if (player.onGround || player.dashActive || player.OnWall) return 0f;
            float front = player.facingRight ? 1f : -1f;
            Vector3 p = player.transform.position;
            var dir = new Vector2(front, 0f);
            int layer = GroundLayer(player);
            foreach (float dy in AheadHeights)
                if (Physics2D.Raycast(new Vector2(p.x + front * 25f, p.y + dy), dir, 12f, layer)) return front;
            return 0f;
        }
        static readonly float[] AheadHeights = { -18f, 19f, 45f };
    }

    /// <summary>The target course's start gate within 16 u of the hitbox while the timer is off (the late gate touch): the gate
    /// boxes in the scene frame, cached until FloatingOrigin moves the world.</summary>
    public sealed class ContactProbe
    {
        readonly Simulation _sim;

        public ContactProbe(Simulation sim) { _sim = sim; }

        /// <summary>The side of the target course's start gate (-1: at -x) when the timer is off and the gate is within 16 u of the
        /// hurtbox (x + 5 × facing ± 20, y ± 27: the 40×54 box the gates touch; the 41×72 body box excludes their Default layer)
        /// at its height, 0 otherwise.</summary>
        public float NearGate()
        {
            if (_sim.TimingCourse() != null) return 0f;
            Movement player = _sim.Player;
            ScenePos p = ScenePos.From(player.transform.position);
            float cx = p.x + (player.facingRight ? 5f : -5f);
            foreach (Rect box in Gates())
            {
                if (p.y + 27f < box.yMin || p.y - 27f > box.yMax) continue;
                if (cx + 20f < box.xMin && box.xMin - (cx + 20f) <= 16f) return 1f;
                if (cx - 20f > box.xMax && (cx - 20f) - box.xMax <= 16f) return -1f;
            }
            return 0f;
        }

        List<Rect> Gates()
        {
            ScenePos origin = _sim.WorldOriginInScene;
            if (_gates != null && origin == _gatesOrigin) return _gates;
            _gates = new List<Rect>();
            _gatesOrigin = origin;
            foreach (startGate gate in _sim.World.All<startGate>())
            {
                if (!gate.gameObject.activeInHierarchy) continue;
                courseScript course = gate.transform.parent != null ? gate.transform.parent.GetComponentInParent<courseScript>() : null;
                // A lab run (TargetCourse -1) takes any course's gate: a gate-touch lab stands by one.
                if (course == null || _sim.TargetCourse > 0 && course.courseNumber != _sim.TargetCourse) continue;
                BoxCollider2D box = gate.GetComponent<BoxCollider2D>();
                if (box == null) continue;
                Vector3 at = gate.transform.position;
                _gates.Add(DistanceField.GateRect(box, at.x, at.y, gate.transform.lossyScale));
            }
            return _gates;
        }
        List<Rect> _gates;
        ScenePos _gatesOrigin;
    }

    /// <summary>The contacts of one run: every tick where the player's contact changes (it leaves the ground at a ledge, lands,
    /// touches or leaves a wall, dies) plus the wall clip's set-up probes behind and ahead and the start gate in reach, and
    /// (on every physics step, because a dash's ticks consume no input) the corner hyper's flat dash leaving the floor's edge
    /// and the per-step tech contacts (<see cref="Step"/>).</summary>
    public sealed class ContactLog
    {
        public readonly List<(int tick, Contact kind)> Contacts = new List<(int, Contact)>();
        bool _primed, _wasGround, _wasWall, _wasDash, _wasSlide, _stepPrimed, _wasSpike, _wasStepUp;
        float _wasBehind, _wasAbove, _wasAhead, _wasNearGate, _wasOverhang, _wasSlope;
        int _dashStart;

        public void Reset()
        {
            Contacts.Clear();
            _primed = false; _wasDash = _wasSlide = false;
            _stepPrimed = _wasSpike = _wasStepUp = false; _wasOverhang = _wasSlope = 0f;
        }

        /// <summary>The last <see cref="Tick"/>'s <see cref="ContactRays.WallBehind"/> probe, for a caller that needs it on the
        /// same tick (the ray is cast once).</summary>
        public float Behind => _wasBehind;

        /// <summary>Drops the contacts before <paramref name="tick"/>, keeping the rest in order.</summary>
        public void DropBefore(int tick)
        {
            int kept = 0;
            for (int i = 0; i < Contacts.Count; i++)
                if (Contacts[i].tick >= tick) Contacts[kept++] = Contacts[i];
            Contacts.RemoveRange(kept, Contacts.Count - kept);
        }

        /// <summary>What the next tick's detection compares with (the last tick's contact state), so a run resumed from a
        /// snapshot taken after that tick sees the same contacts as the run that took it (the planner's edges, <see cref="Planner"/>).</summary>
        public readonly struct Mark
        {
            internal readonly bool Primed, Ground, Wall, Dash, Slide, StepPrimed, Spike, StepUp;
            internal readonly float Behind, Above, Ahead, NearGate, Overhang, Slope;
            internal readonly int DashStart;

            internal Mark(bool primed, bool ground, bool wall, bool dash, bool slide, float behind, float above, float ahead, float nearGate, int dashStart,
                bool stepPrimed, bool spike, bool stepUp, float overhang, float slope)
            {
                Primed = primed; Ground = ground; Wall = wall; Dash = dash; Slide = slide;
                Behind = behind; Above = above; Ahead = ahead; NearGate = nearGate; DashStart = dashStart;
                StepPrimed = stepPrimed; Spike = spike; StepUp = stepUp; Overhang = overhang; Slope = slope;
            }
        }

        public Mark Save()
        {
            return new Mark(_primed, _wasGround, _wasWall, _wasDash, _wasSlide, _wasBehind, _wasAbove, _wasAhead, _wasNearGate, _dashStart,
                _stepPrimed, _wasSpike, _wasStepUp, _wasOverhang, _wasSlope);
        }

        /// <summary>Clears the contacts and picks the detection up where <paramref name="m"/> left it.</summary>
        public void Load(Mark m)
        {
            Contacts.Clear();
            _primed = m.Primed; _wasGround = m.Ground; _wasWall = m.Wall; _wasDash = m.Dash; _wasSlide = m.Slide;
            _wasBehind = m.Behind; _wasAbove = m.Above; _wasAhead = m.Ahead; _wasNearGate = m.NearGate; _dashStart = m.DashStart;
            _stepPrimed = m.StepPrimed; _wasSpike = m.Spike; _wasStepUp = m.StepUp; _wasOverhang = m.Overhang; _wasSlope = m.Slope;
        }

        public void Tick(int tick, Movement player, float gate)
        {
            bool ground = player.onGround, wall = player.OnWall;
            float behind = ContactRays.WallBehind(player), above = behind == 0f ? ContactRays.WallBehindAbove(player) : 0f, ahead = ContactRays.WallAhead(player);
            if (_primed)
            {
                if (ground != _wasGround) Contacts.Add((tick, ground ? Contact.Landed : Contact.LeftGround));
                if (wall != _wasWall) Contacts.Add((tick, wall ? Contact.TouchedWall : Contact.LeftWall));
                if (behind != 0f && behind != _wasBehind) Contacts.Add((tick, behind < 0f ? Contact.WallBehindLeft : Contact.WallBehindRight));
                if (above != 0f && above != _wasAbove) Contacts.Add((tick, above < 0f ? Contact.WallAboveLeft : Contact.WallAboveRight));
                if (ahead != 0f && ahead != _wasAhead) Contacts.Add((tick, ahead < 0f ? Contact.WallAheadLeft : Contact.WallAheadRight));
                if (gate != 0f && gate != _wasNearGate) Contacts.Add((tick, gate < 0f ? Contact.NearGateLeft : Contact.NearGateRight));
            }
            else
            {
                // An explore from a clip-ready cell (or one by the gate, or under a wall at its back) starts in it: that
                // is the contact too.
                if (behind != 0f) Contacts.Add((tick, behind < 0f ? Contact.WallBehindLeft : Contact.WallBehindRight));
                if (above != 0f) Contacts.Add((tick, above < 0f ? Contact.WallAboveLeft : Contact.WallAboveRight));
                if (gate != 0f) Contacts.Add((tick, gate < 0f ? Contact.NearGateLeft : Contact.NearGateRight));
            }
            _primed = true;
            _wasGround = ground;
            _wasWall = wall;
            _wasBehind = behind;
            _wasAbove = above;
            _wasAhead = ahead;
            _wasNearGate = gate;
        }

        /// <summary>Every physics step of a run (a dash runs ticks with no input tick, which Tick never sees): the input tick a dash
        /// was pressed on, and a CornerHyper contact there when its flat hyper dash leaves the floor.</summary>
        public void Slide(int runTicks, Movement player)
        {
            bool dash = player.cutsceneMode == Movement.cutsceneModes.dash;
            if (dash && !_wasDash) _dashStart = runTicks - 1;
            bool slide = dash && player.onGround && ContactRays.CanHyper(player) && ContactRays.CoyoteFrames(player) < player.maxCoyoteFrames;
            if (slide && !_wasSlide) Contacts.Add((_dashStart, Contact.CornerHyper));
            _wasDash = dash;
            _wasSlide = slide;
        }

        /// <summary>Every physics step of a run, after it (a dash's ticks consume no input, and a spring, a wall boost or a spike
        /// touch happens inside a physics callback): the game's own calls this step made (<see cref="Simulation.Events"/>) and
        /// the player's touches (<see cref="PhysicsWorld.PlayerTouch"/>), filed at the input tick <see cref="Tick"/> files that
        /// step's contacts at. A spring hit (SpringHitLeft/Right when its up has an x, launching that way, else Up/Down); an air
        /// jump (DoubleJump); a jump out of a dash (DashJump: a super or hyper, or a ground bounce's jump); a special wall jump
        /// (WallBoostLeft/Right: the wall's side; a wallbounce, a dash into a wall with the jump buffered, a dash-boosted wall
        /// jump); a landing the same tick jumps out of (Landed when onGround is already off again; one it stays on is Tick's);
        /// a pause (Paused); a step-up that follows a step without one (SteppedUpLeft/Right: the step's side, the facing); a
        /// spike trigger touched (SpikeTouched) or left alive (SpikeCleared); a slanted surface newly touched, facing down
        /// (OverhangLeft/Right) or up (SlopeLeft/Right), by the surface's side.</summary>
        public void Step(Simulation sim)
        {
            int tick = (int)sim.Engine.RunTicks;
            StepEvents e = sim.Events;
            if (e != StepEvents.None)
            {
                if ((e & StepEvents.SpringHit) != 0)
                {
                    Vector2 up = sim.SpringUp;
                    Contacts.Add((tick, Math.Abs(up.x) > .01f ? (up.x < 0f ? Contact.SpringHitLeft : Contact.SpringHitRight)
                        : up.y < 0f ? Contact.SpringHitDown : Contact.SpringHitUp));
                }
                if ((e & StepEvents.DoubleJump) != 0) Contacts.Add((tick, Contact.DoubleJump));
                if ((e & StepEvents.DashJump) != 0) Contacts.Add((tick, Contact.DashJump));
                if ((e & StepEvents.WallBoost) != 0) Contacts.Add((tick, sim.WallBoostSide < 0f ? Contact.WallBoostLeft : Contact.WallBoostRight));
                if ((e & StepEvents.Landed) != 0 && !sim.Player.onGround) Contacts.Add((tick, Contact.Landed));
                if ((e & StepEvents.Paused) != 0) Contacts.Add((tick, Contact.Paused));
                if ((e & StepEvents.SteppedUp) != 0 && !_wasStepUp) Contacts.Add((tick, sim.StepUpSide < 0f ? Contact.SteppedUpLeft : Contact.SteppedUpRight));
            }
            _wasStepUp = (e & StepEvents.SteppedUp) != 0;
            PhysicsWorld.PlayerTouches touch = sim.Physics.PlayerTouch;
            // A run's first step only primes the touches: a spike, overhang or slope it starts in wasn't touched in this run.
            if (_stepPrimed)
            {
                if (touch.Spike != _wasSpike && (touch.Spike || sim.Engine.IsRunning)) Contacts.Add((tick, touch.Spike ? Contact.SpikeTouched : Contact.SpikeCleared));
                if (touch.Overhang != 0f && touch.Overhang != _wasOverhang) Contacts.Add((tick, touch.Overhang < 0f ? Contact.OverhangLeft : Contact.OverhangRight));
                if (touch.Slope != 0f && touch.Slope != _wasSlope) Contacts.Add((tick, touch.Slope < 0f ? Contact.SlopeLeft : Contact.SlopeRight));
            }
            _stepPrimed = true;
            _wasSpike = touch.Spike;
            _wasOverhang = touch.Overhang;
            _wasSlope = touch.Slope;
        }
    }

    /// <summary>A studio savestate (or a probe's) applied onto a simulation, as the probe and the tech demo miner start:
    /// its world position is its scene position minus the FloatingOrigin it recorded.</summary>
    public static class DemoStart
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The start: a studio savestate (its world position is its scene position minus the FloatingOrigin it recorded),
        /// then any of position [x, y] and velocity [vx, vy] in the world frame, scaleX, and Movement fields by name.</summary>
        public static void ApplyStart(Simulation sim, JsonElement start, string baseDir)
        {
            WorldPos position = sim.PlayerWorld;
            Vector2 velocity = sim.Physics.PlayerBody() is var body ? new Vector2(body.vx, body.vy) : default;
            float? scaleX = null;
            if (start.TryGetProperty("savestate", out JsonElement ss))
            {
                string path = Path.IsPathRooted(ss.GetString()) ? ss.GetString() : Path.Combine(baseDir, ss.GetString());
                ApplySavestate(sim, path, ref position, ref velocity, ref scaleX);
            }
            if (start.TryGetProperty("position", out JsonElement pos)) position = new WorldPos(pos[0].GetSingle(), pos[1].GetSingle());
            if (start.TryGetProperty("velocity", out JsonElement vel)) velocity = new Vector2(vel[0].GetSingle(), vel[1].GetSingle());
            if (start.TryGetProperty("scaleX", out JsonElement sx)) scaleX = sx.GetSingle();
            if (start.TryGetProperty("fields", out JsonElement fields))
                foreach (JsonProperty field in fields.EnumerateObject())
                    SetField(sim.Player, field.Name, field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText());
            Place(sim, position, velocity, scaleX);
        }

        /// <summary>One studio savestate: its Movement fields and body velocity, its world position and facing.</summary>
        public static void ApplySavestate(Simulation sim, string path)
        {
            WorldPos position = sim.PlayerWorld;
            Vector2 velocity = sim.Physics.PlayerBody() is var body ? new Vector2(body.vx, body.vy) : default;
            float? scaleX = null;
            ApplySavestate(sim, path, ref position, ref velocity, ref scaleX);
            Place(sim, position, velocity, scaleX);
        }

        /// <summary>The player at a lab map's start (what Cli\Lab.cs places for a lab run): position and velocity in the world
        /// frame, facing, Movement fields; a lab's inputs start on the first tick, with no start alignment.</summary>
        public static void ApplyLabStart(Simulation sim, LabMapDef lab)
        {
            LabStart s = lab.start ?? new LabStart();
            (float ax, float ay) = lab.Anchor;
            var fields = new Dictionary<string, object>();
            if (s.fields != null)
                foreach (var (name, value) in s.fields) fields[name] = value;
            float vx = s.vx ?? 0f, vy = s.vy ?? 0f, mx = s.mx ?? 0f, my = s.my ?? 0f;
            if (s.vx.HasValue || s.vy.HasValue || s.mx.HasValue || s.my.HasValue)
            {
                fields["Velocity"] = vx.ToString("R", CultureInfo.InvariantCulture) + "," + vy.ToString("R", CultureInfo.InvariantCulture);
                fields["momentum"] = mx.ToString("R", CultureInfo.InvariantCulture) + "," + my.ToString("R", CultureInfo.InvariantCulture);
            }
            if (s.facing.HasValue) fields["facingRight"] = s.facing.Value > 0f ? "true" : "false";
            var start = new Dictionary<string, object>
            {
                ["position"] = new[] { ax + (s.x ?? 0f), ay + (s.y ?? 0f) },
                ["velocity"] = new[] { vx + mx, vy + my },
                ["scaleX"] = (s.facing ?? 1f) > 0f ? 1f : -1f,
                ["fields"] = fields,
            };
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(start));
            ApplyStart(sim, doc.RootElement, ".");
            // The coloured blocks: the lab start's state (the game's own swap: the scene's tilemaps and the lab's coloured shapes),
            // else the host trace's, which the lab's coloured shapes follow.
            colouredBlockSwapper swapper = sim.World.All<colouredBlockSwapper>().FirstOrDefault();
            if (s.blueActive is bool blue && swapper != null) swapper.swapBlocks(blue);
            else sim.SetLabColours(s.blueActive ?? swapper?.isBlueActive ?? true);
            // A lab start is often mid-air: the demo's inputs start on the first tick (no wait for three grounded ticks and no
            // start alignment), as an in-game lab demo's do, and inside that tick (Simulation.StartsInsideTick).
            SetEngineField(sim, "_waitingForStable", false);
            SetEngineField(sim, "_preRollTicksRemaining", 0);
            SetEngineField(sim, "_requireCourseFinish", false);
            sim.StartsInsideTick = true;
            sim.SetStart(sim.Capture(0), 0);
        }

        static void SetEngineField(Simulation sim, string name, object value)
        {
            typeof(MacroEngine).GetField(name, Fields).SetValue(sim.Engine, value);
        }

        static void ApplySavestate(Simulation sim, string path, ref WorldPos position, ref Vector2 velocity, ref float? scaleX)
        {
            using JsonDocument save = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement s = save.RootElement;
            if (!s.TryGetProperty("hasOrigin", out JsonElement has) || !has.GetBoolean())
                throw new InvalidOperationException("The savestate has no FloatingOrigin (captured by an older plugin); capture it again.");
            JsonElement ox = s.GetProperty("originX"), oy = s.GetProperty("originY");
            position = new WorldPos(s.GetProperty("positionX").GetSingle() - ox.GetSingle(), s.GetProperty("positionY").GetSingle() - oy.GetSingle());
            scaleX = s.GetProperty("scaleX").GetSingle();
            foreach (JsonProperty field in s.GetProperty("movementFields").EnumerateObject())
                SetField(sim.Player, field.Name, field.Value.GetProperty("value").GetString());
            if (s.GetProperty("bodyFields").TryGetProperty("linearVelocity", out JsonElement lv))
                velocity = ParseVector(lv.GetProperty("value").GetString());
        }

        static void Place(Simulation sim, WorldPos position, Vector2 velocity, float? scaleX)
        {
            Transform t = sim.Player.transform;
            ScenePos scene = sim.ToScene(position);
            if (scaleX.HasValue) t.localScale = new Vector3(scaleX.Value, t.localScale.y, t.localScale.z);
            sim.Physics.ResetPlayer(scene.AsVector2(), velocity, 0f, 0f);
            t.position = new Vector3(scene.x, scene.y, t.position.z);
            WorldPos now = sim.PlayerWorld;
            if (Math.Abs(now.x - position.x) > 0.01f || Math.Abs(now.y - position.y) > 0.01f)
                throw new InvalidOperationException("The start position did not stick: " + now + " for " + position + ".");
            // The trace's engine state still pins the player to the route start while it waits for stable ticks.
            sim.Engine.GetType().GetField("_hasSynchronizationPosition", Fields).SetValue(sim.Engine, false);
            sim.SetStart(sim.Capture(0), 0);
        }

        public static void SetField(Movement player, string name, string text)
        {
            FieldInfo f = typeof(Movement).GetField(name, Fields);
            if (f == null) return;
            Type type = f.FieldType;
            text = text.Trim('"', '[', ']');
            object value;
            if (type == typeof(Vector2)) value = ParseVector(text);
            else if (type == typeof(Vector3)) { float[] p = Floats(text); value = new Vector3(p[0], p[1], p.Length > 2 ? p[2] : 0f); }
            else if (type.IsEnum) value = Enum.Parse(type, text, true);
            else if (type == typeof(bool)) value = bool.Parse(text);
            else value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            f.SetValue(player, value);
        }

        static Vector2 ParseVector(string text) { float[] p = Floats(text); return new Vector2(p[0], p[1]); }

        static float[] Floats(string text)
        {
            return text.Trim('[', ']').Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        }
    }

    /// <summary>A tech demo's own input window around one of its contact ticks, relative to that tick: the demo's real
    /// stick values, presses/holds/releases, dash, dash jump and dash turn (human-legal as the demo recorded them),
    /// tagged by the contact type and the unlocks it needs. A window is also offered mirrored (x negated, side flipped)
    /// for the contact's opposite side; one at a contact with no side is turned to the run's own direction when grafted.
    /// A lab demo's graft spec (<see cref="GraftSpec"/>, the demo file's "grafts") gives its window, placement, the spread
    /// its runs are sampled over, its coast, how often it is tried and what it chains into.</summary>
    public sealed class TechPattern
    {
        public string Tech;          // techs.json id
        public string Name;
        public string Category;      // glitchless | nmg | major
        public Caps Requires;        // the unlocks the window needs: techs.json "requires" (a Caps label, e.g. DS_OM) plus its own dash presses
        public Contact Kind;
        public int First, Last;      // window offsets around the contact tick
        public InputTick[] Ticks;
        public bool Key;             // the demo's key contact: the one its tech happens at
        public bool Mirrored;        // x values negated for the contact's opposite side
        public int DemoContact;      // the contact's tick in the demo
        public string DemoStart;     // where the demo played (its trace)
        /// <summary>The window's direction: a sided contact's side (-1: Left), else the sign of its stick at or after the
        /// contact (0: none); a stick <see cref="PatternVary.X"/>/<see cref="CoastX"/> value is relative to it.</summary>
        public int Dir;
        /// <summary>Where the window's contact goes relative to the run's: uniform in [ShiftLo, ShiftHi].</summary>
        public int ShiftLo = -TechGrafts.Jitter, ShiftHi = TechGrafts.Jitter;
        /// <summary>The runs of equal ticks sampled at graft time (null: the window as the demo played it).</summary>
        public PatternVary[] Vary;
        /// <summary>The coast's stick choices (relative to <see cref="Dir"/>; null: the window's last stick) and hold (null: as the window ends).</summary>
        public float[] CoastX;
        public bool? CoastHeld;
        /// <summary>How many sampled grafts one contact gets (a gate tap brushes the gate only at the right height).</summary>
        public int Tries = 1;
        /// <summary>How often a pick draws it over the other windows at its contact kind (the tech's weight is its largest).</summary>
        public int Weight = 1;
        public PatternChain Chain;
        /// <summary>Cut by a graft spec (A/B: --no-graft-specs drops these).</summary>
        public bool Spec;
        /// <summary>The window quick-restarts: grafted only where a run may restart (a Full-TAS window; the game's own
        /// RestartRefused still aborts a restart it refuses), never in a course solve.</summary>
        public bool Resets;

        /// <summary>The solver's mode for a graft of this pattern ("tech graft &lt;id&gt;"), as a new best's "via" names it.</summary>
        [JsonIgnore] public string Mode => _mode ??= "tech graft " + Tech;
        string _mode;

        public override string ToString() => Tech + " " + Kind + " " + First + ".." + Last;

        internal TechPattern Copy() => (TechPattern)MemberwiseClone();
    }

    /// <summary>One run of equal ticks in a pattern's window (from <see cref="Index"/>, <see cref="Run"/> ticks), sampled at graft
    /// time: its length, its stick (x relative to the tick's own sign; a pair: x relative to the pattern's direction, and y) and
    /// its dash's jump frame, each drawn from its choices (null: as the demo played it).</summary>
    public sealed class PatternVary
    {
        public int Index, Run;
        public int[] Ticks;
        public float[] X;
        public float[][] Stick;
        public int[] DashJump;
        /// <summary>The run's jump button as the grafted run's was at the placement (held on, or off), no edge of its own.</summary>
        public bool RunButton;
    }

    /// <summary>What a graft's own run grafts next: the first contact of an <see cref="On"/> kind at least 2 ticks after its
    /// placement gets a pattern of that kind (a key window of <see cref="With"/>'s when given), placed <see cref="ShiftLo"/>..
    /// <see cref="ShiftHi"/> after it, up to <see cref="Depth"/> deep, 85% of the time. With <see cref="Expect"/>, only when
    /// the run shows that contact where the demo did (<see cref="ExpectAt"/> after the placement, -1..+2): the graft worked.</summary>
    public sealed class PatternChain
    {
        public int Depth;
        public Contact[] On;
        public string With;
        public Contact? Expect;
        public int ExpectAt;
        public int ShiftLo, ShiftHi;

        internal PatternChain Copy() => (PatternChain)MemberwiseClone();
    }

    /// <summary>What the miner saw in one tech's demo: where it replayed, the contacts along it and the patterns cut out.</summary>
    public sealed class TechDemo
    {
        public string Tech, Name, Category, Requires, Start, Source, Error;
        /// <summary>The tech's overlay detector (techs.json "detector", the TAS overlay's Tech name; null: none): what
        /// <see cref="TechChecks"/> looks for in a run to say the tech happened.</summary>
        public string Detector;
        /// <summary>The tech's graftKey (techs.json): the contact kind its key window sits on, near this demo input tick (optional).</summary>
        public Contact? KeyContact;
        public int? KeyTick;
        public int InputTicks, PhysicsTicks;
        public bool Finished, Died;
        /// <summary>The tech's techs.json "graft" marker when it has no demo to mine yet: "todo" (not demonstrable yet, no lab);
        /// null for a mined tech. A marked tech isn't counted as missing.</summary>
        public string Marker;
        public List<(int tick, Contact kind)> Contacts = new List<(int, Contact)>();
        public List<TechPattern> Patterns = new List<TechPattern>();
        /// <summary>What the graft specs couldn't cut (a contact the demo never shows, a vary outside its window).</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>The tech demos of the studio's Tech tab (techs.json) mined into solver grafts: each demo replays offline from
    /// its start (its category's start trace, its own savestate, or a captured tech-<id>.json trace), the contact events
    /// along it are detected with the solver's own contact detection (<see cref="ContactLog"/>), and the input window around
    /// each contact becomes a pattern. <c>solve</c> grafts them at the same contact kinds (filter by the run's unlocks): they
    /// are its only grafts; <c>--no-tech-grafts</c> turns them off. The mining is cached in the configured
    /// traces folder (where the demos resolve from; <c>--tech-dir</c>), keyed by the catalog content and the engine build.</summary>
    public sealed partial class TechGrafts
    {
        public List<TechDemo> Demos = new List<TechDemo>();
        public List<TechPattern> Patterns = new List<TechPattern>();
        public string CacheFile, Key;
        public bool FromCache;

        // The input window cut around a contact: WindowBefore ticks before it (the tech's set-up: a hop, a turn, the
        // approach), WindowAfter after it (the tech's own inputs and its hold). A death's window stops at the death: a run
        // ends there, so only the inputs that can prevent it matter.
        public const int WindowBefore = 10, WindowAfter = 30;
        /// <summary>The contacts per demo that make patterns: the ones nearest its action.</summary>
        public const int MaxContacts = 8;
        /// <summary>The contact tick's placement jitter at graft time (a pattern's default shift).</summary>
        public const int Jitter = 3;
        public const int GraftCoast = 40;

        /// <summary>The share of picks that take the tech's key window (the contact its demo does the tech at) over the
        /// demo's other contact windows, which are mostly its set-up and travel.</summary>
        public const double KeyShare = .6;

        /// <summary>What the miner detects and cuts, versioned: part of the cache key (bump it when the detection changes).
        /// 2: the per-step tech contacts (ContactLog.Step) and the repo catalog/lab maps read from the source tree.
        /// 3: lab demos' graft specs ("grafts"), their variant demos and techs.json's graftDemos, the window's direction, windows
        /// with a quick restart.</summary>
        const int MinerVersion = 3;

        /// <summary>The patterns ready for one solve's unlocks (its caps id, <see cref="Caps"/>), by contact kind and then by
        /// tech (read-only once prepared): those whose <see cref="TechPattern.Requires"/> is a subset of the run's unlocks,
        /// each listed <see cref="TechPattern.Weight"/> times, once without the windows that quick-restart and once with them
        /// (a run that may restart: a Full-TAS window's). A pick draws the tech first (by its weight there: its largest
        /// window's), so a demo with many contacts doesn't crowd out a short one.</summary>
        public void Prepare(string capabilities)
        {
            Caps caps = Caps.Parse(capabilities);
            int kinds = Enum.GetValues(typeof(Contact)).Length;
            _ready = new Ready[kinds];
            _readyResets = new Ready[kinds];
            for (int i = 0; i < kinds; i++)
            {
                _ready[i] = Ready.Of(Patterns.Where(p => p.Kind == (Contact)i && p.Requires.IsSubsetOf(caps) && !p.Resets));
                _readyResets[i] = Ready.Of(Patterns.Where(p => p.Kind == (Contact)i && p.Requires.IsSubsetOf(caps)));
            }
        }
        sealed class ReadyTech { public string Tech; public int Weight; public TechPattern[] Key, All; }
        sealed class Ready
        {
            public ReadyTech[] Techs;
            public int Total;

            public static Ready Of(IEnumerable<TechPattern> patterns)
            {
                ReadyTech[] techs = patterns.GroupBy(p => p.Tech, StringComparer.Ordinal)
                    .Select(g => new ReadyTech
                    {
                        Tech = g.Key, Weight = g.Max(p => Math.Max(1, p.Weight)),
                        Key = g.Where(p => p.Key).SelectMany(p => Enumerable.Repeat(p, Math.Max(1, p.Weight))).ToArray(),
                        All = g.SelectMany(p => Enumerable.Repeat(p, Math.Max(1, p.Weight))).ToArray(),
                    }).ToArray();
                return new Ready { Techs = techs, Total = techs.Sum(t => t.Weight) };
            }
        }
        Ready[] _ready, _readyResets;

        /// <summary>A pattern for this contact kind the run's unlocks allow (with <paramref name="resets"/>, the windows that
        /// quick-restart too): a tech drawn by its weight, then its key window <see cref="KeyShare"/> of the time (when it has
        /// one here), else any of its windows; null when there is none.</summary>
        public TechPattern Pick(Contact kind, System.Random rng, bool resets = false)
        {
            Ready ready = (resets ? _readyResets : _ready)[(int)kind];
            if (ready.Techs.Length == 0) return null;
            int pick = rng.Next(ready.Total);
            ReadyTech tech = ready.Techs[^1];
            foreach (ReadyTech t in ready.Techs) { if (pick < t.Weight) { tech = t; break; } pick -= t.Weight; }
            return tech.Key.Length > 0 && rng.NextDouble() < KeyShare ? tech.Key[rng.Next(tech.Key.Length)] : tech.All[rng.Next(tech.All.Length)];
        }

        /// <summary>A chained graft's pattern at this contact kind (<see cref="PatternChain"/>): a key window of the tech
        /// <paramref name="tech"/> when given (else of any tech), else any of that tech's windows; null when there is none.</summary>
        public TechPattern PickChain(Contact kind, string tech, System.Random rng, bool resets = false)
        {
            if (tech == null) return Pick(kind, rng, resets);
            foreach (ReadyTech t in (resets ? _readyResets : _ready)[(int)kind].Techs)
                if (t.Tech == tech) return t.Key.Length > 0 ? t.Key[rng.Next(t.Key.Length)] : t.All[rng.Next(t.All.Length)];
            return null;
        }

        /// <summary>Whether <see cref="Pick"/> has a pattern for this contact kind (after <see cref="Prepare"/>).</summary>
        public bool Offers(Contact kind, bool resets = false) { return (resets ? _readyResets : _ready)[(int)kind].Techs.Length > 0; }

        /// <summary>The pattern's input window on <paramref name="run"/>, its contact at the run's contact tick plus a shift drawn
        /// from the pattern's (<see cref="TechPattern.ShiftLo"/>..<see cref="TechPattern.ShiftHi"/>): the run is cut at the
        /// window's first tick and the window's own inputs follow, its <see cref="TechPattern.Vary"/> runs sampled, then
        /// GraftCoast ticks of its coast (the window's last stick and hold, or the pattern's coast choices; a hold released at
        /// the end). A window at a contact with no side (<see cref="Sided"/>) is turned to the run's direction (its last stick
        /// before the placement, else either way), the other way 30% of the time. A press or release at the cut keeps the jump
        /// button's edges real (docs/human-input.md). <paramref name="placed"/>: the run tick the window's contact went to;
        /// <paramref name="shift"/> overrides the pattern's own range (a chained graft's); <paramref name="turn"/> the direction a
        /// window with no side is turned to (-1, 1: no random turn).
        /// False: the window doesn't fit after the run's kept prefix.</summary>
        public static bool Compose(List<InputTick> run, int contactTick, int lo, TechPattern p, System.Random rng, out List<InputTick> grafted, out int placed,
            (int lo, int hi)? shift = null, int turn = 0)
        {
            grafted = null;
            (int shiftLo, int shiftHi) = shift ?? (p.ShiftLo, p.ShiftHi);
            int at = placed = Math.Min(contactTick, run.Count) + rng.Next(shiftLo, shiftHi + 1);
            int dir = p.Dir;
            bool flip = false;
            int before = Math.Clamp(at, 1, run.Count) - 1;
            if (!Sided(p.Kind))
            {
                float runX = run.Count > 0 ? run[before].x : 0f;
                int want = turn != 0 ? turn : runX != 0f ? Math.Sign(runX) : rng.NextDouble() < .5 ? -1 : 1;
                if (turn == 0 && rng.NextDouble() < .3) want = -want;
                flip = dir != 0 && want != dir;
                dir = want;
            }
            bool runHeld = run.Count > 0 && (run[before].held || run[before].press) && !run[before].release;
            List<InputTick> window = Sample(p, rng, flip, dir, runHeld, out int contactIndex);
            int start = at - contactIndex;
            if (start > run.Count) return false;
            // The window may reach before the run's kept prefix: drop the head it can't have (the run starts from a cell's
            // state there), as long as some of the tech's own inputs survive.
            int skip = 0;
            if (start < Math.Max(lo, 1)) { skip = Math.Max(lo, 1) - start; start = Math.Max(lo, 1); }
            if (skip >= window.Count || start > run.Count) return false;
            var t = run.GetRange(0, start);
            InputTick first = window[skip];
            bool down = start > 0 && (t[start - 1].held || t[start - 1].press) && !t[start - 1].release;
            if (down && !first.held && !first.release) first.release = true;
            else if (!down && first.release && !first.press) first.release = false;
            // A dash with a jump frame holds the button from that frame (its own press): no press on the dash tick.
            if (!down && first.held && !first.press && !(first.dash && first.dashJump > 0)) first.press = true;
            for (int i = skip; i < window.Count; i++) t.Add(i == skip ? first : window[i]);
            // The coast keeps the window's last stick and hold, as the demo's follow-up would (or the pattern's coast); the hold
            // ends at the run's end.
            InputTick last = t[t.Count - 1];
            bool endDown = (last.held || last.press) && !last.release;
            bool held = endDown && (p.CoastHeld ?? true);
            var coast = new InputTick { x = p.CoastX != null ? p.CoastX[rng.Next(p.CoastX.Length)] * (dir == 0 ? 1 : dir) : last.x, y = last.y, held = held };
            for (int i = 0; i < GraftCoast; i++) t.Add(coast);
            if (endDown && !held)
            {
                InputTick r = t[t.Count - GraftCoast];
                r.release = true;
                t[t.Count - GraftCoast] = r;
            }
            else if (held)
            {
                InputTick r = t[t.Count - 1];
                r.release = true;
                r.held = false;
                t[t.Count - 1] = r;
            }
            grafted = t;
            return true;
        }

        /// <summary>The pattern's window with its <see cref="TechPattern.Vary"/> runs sampled (turned the other way when
        /// <paramref name="flip"/>; a run whose button follows the run's takes <paramref name="runHeld"/>), and the index its
        /// contact tick landed on.</summary>
        static List<InputTick> Sample(TechPattern p, System.Random rng, bool flip, int dir, bool runHeld, out int contactIndex)
        {
            var w = new List<InputTick>(p.Ticks.Length + 16);
            int contact = -p.First;
            contactIndex = -1;
            for (int i = 0; i < p.Ticks.Length;)
            {
                PatternVary v = null;
                if (p.Vary != null) foreach (PatternVary c in p.Vary) if (c.Index == i) { v = c; break; }
                if (v == null)
                {
                    if (i == contact) contactIndex = w.Count;
                    w.Add(flip ? Mirror(p.Ticks[i]) : p.Ticks[i]);
                    i++;
                    continue;
                }
                InputTick k = flip ? Mirror(p.Ticks[i]) : p.Ticks[i];
                if (v.X != null)
                {
                    float sign = k.x != 0f ? Math.Sign(k.x) : dir == 0 ? 1 : dir;
                    k.x = sign * v.X[rng.Next(v.X.Length)];
                }
                if (v.Stick != null)
                {
                    float[] s = v.Stick[rng.Next(v.Stick.Length)];
                    k.x = s[0] * (dir == 0 ? 1 : dir);
                    k.y = s[1];
                }
                if (v.DashJump != null && k.dash) k.dashJump = v.DashJump[rng.Next(v.DashJump.Length)];
                if (v.RunButton) { k.held = runHeld; k.press = k.release = false; }
                int n = v.Ticks != null ? v.Ticks[rng.Next(v.Ticks.Length)] : v.Run;
                if (contact >= i && contact < i + v.Run) contactIndex = w.Count + Math.Min(contact - i, Math.Max(0, n - 1));
                for (int j = 0; j < n; j++) w.Add(k);
                i += v.Run;
            }
            if (contactIndex < 0) contactIndex = Math.Clamp(contact, 0, Math.Max(0, w.Count - 1));
            return w;
        }

        /// <summary>Whether a contact kind names a side (its window is mirrored for the other one at mining time).</summary>
        public static bool Sided(Contact kind) { return Mirror(kind) != kind; }

        // ---- mining ----

        internal sealed class TechDef
        {
            public string id, name, category, requires, start, routeId, labmap, labDemo, detector;
            /// <summary>The live entry's own demo wiring when the repo seed's differs (Catalog): the seed's is the tech's, this is mined too.</summary>
            public string LiveLabmap, LiveLabDemo;
            /// <summary>Where the tech happens when the last button isn't it (a demo with trailing inputs): {"contact": a Contact
            /// name, "tick": the demo's input tick near it (optional)}. The key window and the kept windows centre on it.</summary>
            public GraftKeyDef graftKey;
            /// <summary>More demos of its lab mined besides its default (labmaps/&lt;labmap&gt;.&lt;name&gt;.demo.json): a demo that
            /// shows the tech in full where the default is a short or trimmed one. Not the comparison ghosts, which fail on purpose.</summary>
            public string[] graftDemos;
            /// <summary>"todo" (not demonstrable yet, no lab): it has
            /// no demo to mine and the listing says so instead of counting it missing.</summary>
            public string graft;
            public bool floatingOrigin;
            public int fastForward;
        }

        internal sealed class GraftKeyDef
        {
            public string contact;
            public int? tick;
        }

        const string NoDemoRoute = "no demo route";

        internal sealed class DemoSource
        {
            public TechDef Tech;
            public string Trace, Savestate;   // the trace the demo replays over, and its own savestate (macro demos)
            public MacroDefinition Macro;
            public bool Lab;                  // a lab map's solved demo (labmaps/<id>.demo.json, or <id>.<LabDemo>.demo.json)
            public string LabId, LabDemo, LabMapPath, LabDemoPath;
            public List<string> KeyFiles = new List<string>();   // everything the replay reads (the cache key)
        }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true,
            Converters = { new JsonStringEnumConverter(), new CapsConverter() } };

        /// <summary>The cache's <see cref="TechPattern.Requires"/> as its caps id ("dsom"); a cache written in another shape
        /// fails to read and is mined again (<see cref="Load"/>'s try).</summary>
        sealed class CapsConverter : JsonConverter<Caps>
        {
            public override Caps Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Caps.Parse(reader.GetString());
            public override void Write(Utf8JsonWriter writer, Caps value, JsonSerializerOptions options) => writer.WriteStringValue(value.Id);
        }

        /// <param name="tracesDir">The trace's own folder (the fallback when there is no configured traces dir).</param>
        /// <param name="refresh">Re-mine instead of reading the cache.</param>
        /// <param name="techDir">Where the demos' traces, savestates, lab maps and the cache resolve from
        /// (<c>--tech-dir</c>); the configured traces dir when it is absent.</param>
        /// <param name="cacheFile">Where the mining is cached (<c>--tech-cache</c>); <c>tech-grafts.cache.json</c> in the tech dir
        /// when it is absent.</param>
        public static TechGrafts Load(string tracesDir, bool refresh = false, string techDir = null, string cacheFile = null)
        {
            tracesDir = ResolveTechDir(tracesDir, techDir);
            List<DemoSource> sources = Sources(tracesDir);
            string key = CacheKey(sources);
            string cache = cacheFile == null ? Path.Combine(tracesDir, "tech-grafts.cache.json") : Path.GetFullPath(cacheFile);
            if (!refresh && File.Exists(cache))
            {
                try
                {
                    TechGrafts cached = JsonSerializer.Deserialize<TechGrafts>(File.ReadAllText(cache), Json);
                    if (cached != null && cached.Key == key)
                    {
                        cached.CacheFile = cache;
                        cached.FromCache = true;
                        return cached.WithRoutes(tracesDir, refresh, cache);   // route-corpus grafts (RouteGrafts.cs)
                    }
                }
                catch (Exception) { }
            }
            var mined = new TechGrafts { CacheFile = cache, Key = key };
            foreach (DemoSource source in sources) mined.Demos.Add(Mine(source, tracesDir));
            foreach (TechDemo demo in mined.Demos) mined.Patterns.AddRange(demo.Patterns);
            mined.Dedupe();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache));
                File.WriteAllText(cache, JsonSerializer.Serialize(mined, Json));
            }
            catch (Exception ex) { Console.Error.WriteLine("tech grafts: cache not written (" + ex.Message + ")"); }
            return mined.WithRoutes(tracesDir, refresh, cache);   // route-corpus grafts (RouteGrafts.cs)
        }

        /// <summary>Every tech's demo sources, as <see cref="Load"/> mines them (a "todo" tech's has no trace).</summary>
        static List<DemoSource> Sources(string tracesDir)
        {
            string config = Directory.Exists(Path.GetDirectoryName(tracesDir)) ? Path.GetDirectoryName(tracesDir) : null;
            List<TechDef> techs = Catalog(config);
            Dictionary<string, MacroDefinition> macros = Macros(config);
            var sources = new List<DemoSource>();
            List<(string id, string tech, string path)> labmaps = LabMaps(config);
            List<(string lab, string demo, string tech)> specDemos = SpecDemos(config, labmaps);
            foreach (TechDef tech in techs)
            {
                if (tech.graft == "todo") { sources.Add(new DemoSource { Tech = tech }); continue; }
                var labs = new List<(string id, string demo)>();
                void AddLab(string id, string demo) { if (!string.IsNullOrEmpty(id) && !labs.Contains((id, demo))) labs.Add((id, demo)); }
                AddLab(tech.labmap, tech.labDemo);
                AddLab(tech.LiveLabmap, tech.LiveLabDemo);
                // A lab map naming this tech ("tech") demos it with its default demo, unless the tech already plays one on it.
                foreach ((string id, string techId, _) in labmaps) if (techId == tech.id && !labs.Any(l => l.id == id)) AddLab(id, null);
                // The tech's graftDemos (techs.json) of its own lab, and every lab variant demo with a graft spec made for it.
                if (!string.IsNullOrEmpty(tech.labmap)) foreach (string name in tech.graftDemos ?? Array.Empty<string>()) AddLab(tech.labmap, name);
                foreach ((string lab, string demo, string owner) in specDemos) if (owner == tech.id) AddLab(lab, demo);
                var forTech = new List<DemoSource>();
                foreach (var (id, labDemo) in labs)
                {
                    var source = new DemoSource { Tech = tech, Lab = true, LabId = id, LabDemo = labDemo };
                    try { Resolve(source, tracesDir, config, macros); }
                    catch (Exception ex) { source.Trace = ex.Message; }
                    forTech.Add(source);
                }
                // A tech with a lab demo that resolves is mined from its lab demos alone: its captured tech-<id>.json trace or demo
                // macro (a recording from a course start, with the travel and noise around the tech) is mined only without one.
                if (!forTech.Any(s => s.LabDemoPath != null && File.Exists(s.Trace)))
                {
                    var own = new DemoSource { Tech = tech };
                    try { Resolve(own, tracesDir, config, macros); }
                    catch (Exception ex) { own.Trace = ex.Message; }
                    forTech.Insert(0, own);
                }
                sources.AddRange(forTech);
            }
            return sources;
        }

        /// <summary>A demo the tech checks are measured on (<c>techs --demos</c>, <see cref="TechChecks"/>): one a tech is mined
        /// from, or a comparison ghost of its lab (labmaps/&lt;lab&gt;.json "ghosts": most miss the tech on purpose).</summary>
        public sealed class CheckDemo
        {
            public string Tech, Category, Detector, Name, Label, Error;
            public bool Ghost;
            internal DemoSource Source;

            /// <summary>Plays <paramref name="run"/> on the demo's simulation at its start, with its inputs.</summary>
            public T Play<T>(Func<Simulation, List<InputTick>, T> run) => WithDemo(Source, (sim, inputs, _, _) => run(sim, inputs));
        }

        /// <summary>Every tech's mined demos and its labs' ghosts (<see cref="CheckDemo"/>).</summary>
        public static List<CheckDemo> CheckDemos(string tracesDir, string techDir = null)
        {
            tracesDir = ResolveTechDir(tracesDir, techDir);
            string config = Directory.Exists(Path.GetDirectoryName(tracesDir)) ? Path.GetDirectoryName(tracesDir) : null;
            List<DemoSource> sources = Sources(tracesDir).Where(s => s.Tech.graft != "todo").ToList();
            var demos = new List<CheckDemo>();
            foreach (DemoSource source in sources)
            {
                string name = source.Lab ? source.LabId + (string.IsNullOrEmpty(source.LabDemo) ? "" : "." + source.LabDemo) : Path.GetFileName(source.Trace);
                bool resolved = source.Trace != null && File.Exists(source.Trace);
                demos.Add(new CheckDemo { Tech = source.Tech.id, Category = source.Tech.category, Detector = source.Tech.detector, Name = name, Source = source,
                    Error = resolved ? null : source.Trace ?? NoDemoRoute });
            }
            foreach (DemoSource source in sources)
            {
                if (!source.Lab || !string.IsNullOrEmpty(source.LabDemo) || source.LabMapPath == null) continue;
                using JsonDocument lab = JsonDocument.Parse(File.ReadAllText(source.LabMapPath));
                if (!lab.RootElement.TryGetProperty("ghosts", out JsonElement ghosts) || ghosts.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement g in ghosts.EnumerateArray())
                {
                    if (!g.TryGetProperty("demo", out JsonElement demo) || demo.ValueKind != JsonValueKind.String) continue;
                    string name = source.LabId + "." + demo.GetString();
                    if (demos.Any(d => d.Tech == source.Tech.id && d.Name == name)) continue;
                    var ghost = new DemoSource { Tech = source.Tech, Lab = true, LabId = source.LabId, LabDemo = demo.GetString() };
                    string error = null;
                    try { Resolve(ghost, tracesDir, config, new Dictionary<string, MacroDefinition>()); }
                    catch (Exception ex) { error = ex.Message; }
                    demos.Add(new CheckDemo
                    {
                        Tech = source.Tech.id, Category = source.Tech.category, Detector = source.Tech.detector, Name = name, Ghost = true, Source = ghost, Error = error,
                        Label = g.TryGetProperty("label", out JsonElement label) ? label.GetString() : null,
                    });
                }
            }
            return demos;
        }

        // The configured traces dir (tools/gamepaths.py's TRACES: the game's config folder's enginesim dir) when it exists:
        // the catalog, the demo traces and the cache resolve from there whatever trace is being solved, so a copied trace
        // mines (or reads) the same patterns as the real folder. --tech-dir overrides it; without a configured folder the
        // trace's own folder is used.
        public static string ResolveTechDir(string tracesDir, string techDir = null)
        {
            if (!string.IsNullOrEmpty(techDir)) return Path.GetFullPath(techDir);
            string canonical = Path.Combine(
                Environment.GetEnvironmentVariable("IGTAP_GAME")
                    ?? @"C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer",
                "BepInEx", "config", "IGTAPTasMod", "enginesim");
            return Directory.Exists(canonical) ? canonical : Path.GetFullPath(tracesDir);
        }

        /// <summary>Every tech of the catalog (<see cref="Catalog"/>: the config folder's techs.json merged with the repo seed, as
        /// <see cref="Load"/> reads it) with its category and detector.</summary>
        public static List<(string id, string category, string detector)> Categories(string tracesDir, string techDir = null)
        {
            tracesDir = ResolveTechDir(tracesDir, techDir);
            string config = Directory.Exists(Path.GetDirectoryName(tracesDir)) ? Path.GetDirectoryName(tracesDir) : null;
            return Catalog(config).Select(t => (t.id, t.category, t.detector)).ToList();
        }

        // The catalog as the plugin merges it (TechCatalog.MergeSeedTechs): the config folder's techs.json (the Tech tab's live
        // copy) keeps its own entries, and the repo's seed (SeedTechs) adds the techs the live copy lacks, except one the Tech tab
        // deleted (listed in techs.seeded.json: offered once already). Unlike the plugin, which merges only on the game's next
        // launch, the miner merges on every load, so a tech or demo added to the repo mines before the game has run. The repo is
        // the source of truth for the offline-only demo wiring: a seed's labmap/labDemo and graftKey replace a live entry's (a
        // live labmap that differs is mined too, LiveLabmap/LiveLabDemo), and a live one fills in only where the seed has none.
        static List<TechDef> Catalog(string config)
        {
            List<TechDef> Read(string file)
            {
                if (file == null || !File.Exists(file)) return null;
                try { return (JsonSerializer.Deserialize<List<TechDef>>(File.ReadAllText(file), Json) ?? new List<TechDef>()).Where(t => !string.IsNullOrEmpty(t.id)).ToList(); }
                catch (Exception ex) { Console.Error.WriteLine("tech grafts: " + file + ": " + ex.Message); return null; }
            }
            List<TechDef> live = Read(config == null ? null : Path.Combine(config, "techs.json"));
            var offered = new HashSet<string>(StringComparer.Ordinal);
            string seededFile = config == null ? null : Path.Combine(config, "techs.seeded.json");
            if (live != null && seededFile != null && File.Exists(seededFile))
            {
                try { offered.UnionWith(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(seededFile)) ?? new List<string>()); }
                catch (Exception ex) { Console.Error.WriteLine("tech grafts: " + seededFile + ": " + ex.Message); }
            }
            var byId = new Dictionary<string, TechDef>(StringComparer.Ordinal);
            foreach (TechDef tech in live ?? new List<TechDef>()) byId[tech.id] = tech;
            foreach (TechDef seed in Read(SeedTechs()) ?? new List<TechDef>())
            {
                if (byId.TryGetValue(seed.id, out TechDef mine))
                {
                    // graftKey, graftDemos and graft are offline-only: the repo's win, the live ones fill a gap, and a live "todo"
                    // goes once the seed has a lab and no marker (as TechCatalog.FillSeedGaps). So does the detector: the code
                    // that runs it (the overlay, TechChecks) is the repo's.
                    mine.graftKey = seed.graftKey ?? mine.graftKey;
                    mine.graftDemos = seed.graftDemos ?? mine.graftDemos;
                    mine.graft = seed.graft ?? (mine.graft == "todo" && !string.IsNullOrEmpty(seed.labmap) ? null : mine.graft);
                    mine.detector = seed.detector ?? mine.detector;
                    if (!string.IsNullOrEmpty(seed.labmap) && (seed.labmap != mine.labmap || seed.labDemo != mine.labDemo))
                    {
                        if (!string.IsNullOrEmpty(mine.labmap)) { mine.LiveLabmap = mine.labmap; mine.LiveLabDemo = mine.labDemo; }
                        mine.labmap = seed.labmap;
                        mine.labDemo = seed.labDemo;
                    }
                }
                else if (live == null || !offered.Contains(seed.id)) byId[seed.id] = seed;
            }
            return byId.Values.OrderBy(t => t.id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The repo's techs.json (the plugin's embedded seed): the source tree's (<see cref="SourceRoot"/>), else the copy
        /// the build put beside the binary.</summary>
        static string SeedTechs()
        {
            string root = SourceRoot();
            if (root != null && File.Exists(Path.Combine(root, "techs.json"))) return Path.Combine(root, "techs.json");
            string copy = Path.Combine(AppContext.BaseDirectory, "techs.json");
            return File.Exists(copy) ? copy : null;
        }

        /// <summary>The source tree the engine was built from (the folder holding techs.json, labmaps\ and enginesim\): walked up
        /// from the running binary, else the one the build recorded (IgtapRepo, IGTAP.EngineSim.Runtime.csproj) for a solver
        /// copied out of the tree; null when neither exists (the build's copies beside the binary are used).</summary>
        public static string SourceRoot()
        {
            static bool IsRoot(string dir) => File.Exists(Path.Combine(dir, "techs.json")) && Directory.Exists(Path.Combine(dir, "enginesim", "Runtime"));
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (IsRoot(dir.FullName)) return dir.FullName;
            string built = typeof(TechGrafts).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IgtapRepo")?.Value;
            return !string.IsNullOrEmpty(built) && Directory.Exists(built) && IsRoot(built) ? Path.GetFullPath(built) : null;
        }

        /// <summary>Where lab maps resolve, first wins: the source tree's labmaps\ (the source of truth: a lab edited there wins over
        /// a stale copy), then the config folder's (the studio's live copies: they fill in only the labs the repo lacks), then the
        /// build's copies.</summary>
        static IEnumerable<string> LabDirs(string config)
        {
            string root = SourceRoot();
            if (root != null) yield return Path.Combine(root, "labmaps");
            if (config != null) yield return Path.Combine(config, "labmaps");
            yield return Path.Combine(AppContext.BaseDirectory, "labmaps");
        }

        static Dictionary<string, MacroDefinition> Macros(string config)
        {
            string file = config == null ? null : Path.Combine(config, "routes.json");
            if (file == null || !File.Exists(file)) return new Dictionary<string, MacroDefinition>();
            try
            {
                MacroLibrary library = JsonSerializer.Deserialize<MacroLibrary>(File.ReadAllText(file), Json);
                return (library?.macros ?? new List<MacroDefinition>()).Where(m => !string.IsNullOrEmpty(m.id)).ToDictionary(m => m.id, m => m);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("tech grafts: " + file + ": " + ex.Message);
                return new Dictionary<string, MacroDefinition>();
            }
        }

        // Where a demo replays: a captured tech-<id>.json trace when the game has captured one (lab demos included), a lab
        // map's solved demo over the trace it was solved in, else its macro's raw inputs over its category's start trace,
        // from its own savestate when it has one.
        static void Resolve(DemoSource source, string tracesDir, string config, Dictionary<string, MacroDefinition> macros)
        {
            TechDef tech = source.Tech;
            if (source.Lab)
            {
                source.LabMapPath = FindLab(config, source.LabId + ".json");
                string demoName = source.LabId + (string.IsNullOrEmpty(source.LabDemo) ? "" : "." + source.LabDemo) + ".demo.json";
                source.LabDemoPath = FindLab(config, demoName);
                if (source.LabMapPath == null) throw new InvalidDataException("lab demo: no labmaps/" + source.LabId + ".json");
                if (source.LabDemoPath == null) throw new InvalidDataException("lab demo: no labmaps/" + demoName + " solved yet");
                using JsonDocument demo = JsonDocument.Parse(File.ReadAllText(source.LabDemoPath));
                source.Trace = Path.Combine(tracesDir, demo.RootElement.GetProperty("solvedIn").GetString());
                if (!File.Exists(source.Trace)) throw new InvalidDataException("no " + Path.GetFileName(source.Trace));
                source.KeyFiles.Add(source.LabMapPath);
                source.KeyFiles.Add(source.LabDemoPath);
                source.KeyFiles.Add(source.Trace);
                source.KeyFiles.Add(WorldOf(source.Trace));
                return;
            }
            string captured = Path.Combine(tracesDir, "tech-" + tech.id + ".json");
            if (File.Exists(captured))
            {
                source.Trace = captured;
                source.KeyFiles.Add(captured);
                source.KeyFiles.Add(WorldOf(captured));
                return;
            }
            if (string.IsNullOrEmpty(tech.routeId) || !macros.TryGetValue(tech.routeId, out source.Macro))
                throw new InvalidDataException(NoDemoRoute);
            source.Trace = StartTrace(tracesDir, tech.start);
            source.KeyFiles.Add(source.Trace);
            source.KeyFiles.Add(WorldOf(source.Trace));
            if (!string.IsNullOrEmpty(source.Macro.savestateId) && !tech.floatingOrigin)
            {
                string savestate = Path.Combine(Path.GetDirectoryName(tracesDir), "savestates", source.Macro.savestateId + ".json");
                if (!File.Exists(savestate)) throw new InvalidDataException("no savestate " + source.Macro.savestateId);
                source.Savestate = savestate;
                source.KeyFiles.Add(savestate);
            }
        }

        // The lab maps on disk (LabDirs: the source tree's copy, else the config folder's, else the build's): their ids and the
        // techs.json id each demos.
        static List<(string id, string tech, string path)> LabMaps(string config)
        {
            var found = new List<(string, string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in LabDirs(config))
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    if (file.EndsWith(".demo.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(Path.GetFileNameWithoutExtension(file))) continue;
                    string tech = null;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (doc.RootElement.TryGetProperty("tech", out JsonElement t) && t.ValueKind == JsonValueKind.String) tech = t.GetString();
                    }
                    catch (Exception) { }
                    found.Add((Path.GetFileNameWithoutExtension(file), tech, file));
                }
            }
            return found;
        }

        // A lab's variant demos (labmaps/<lab>.<name>.demo.json, first of LabDirs wins) that carry a graft spec ("grafts"): each is
        // mined for the tech the demo file names ("tech"), else its lab's.
        static List<(string lab, string demo, string tech)> SpecDemos(string config, List<(string id, string tech, string path)> labmaps)
        {
            var found = new List<(string, string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var labTech = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, string tech, _) in labmaps) labTech[id] = tech;
            foreach (string dir in LabDirs(config))
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.demo.json"))
                {
                    string name = Path.GetFileName(file);
                    if (!seen.Add(name)) continue;
                    string stem = name.Substring(0, name.Length - ".demo.json".Length);
                    int dot = stem.IndexOf('.');
                    if (dot <= 0 || !labTech.TryGetValue(stem.Substring(0, dot), out string owner)) continue;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (!doc.RootElement.TryGetProperty("grafts", out _)) continue;
                        if (doc.RootElement.TryGetProperty("tech", out JsonElement t) && t.ValueKind == JsonValueKind.String) owner = t.GetString();
                    }
                    catch (Exception) { continue; }
                    if (!string.IsNullOrEmpty(owner)) found.Add((stem.Substring(0, dot), stem.Substring(dot + 1), owner));
                }
            }
            return found;
        }

        // labmaps/<name>, the first of LabDirs that has it.
        static string FindLab(string config, string name)
        {
            foreach (string dir in LabDirs(config))
            {
                string file = Path.Combine(dir, name);
                if (File.Exists(file)) return file;
            }
            return null;
        }

        static string WorldOf(string trace) { return Path.ChangeExtension(trace, null) + ".world.json"; }

        /// <summary>A course target's category start: its start trace (start-c&lt;target&gt;-&lt;caps&gt;.json), else the Optimize
        /// tab's capture of the same start (optimize-c&lt;target&gt;-&lt;caps&gt;.json); null when neither is there.</summary>
        public static string CategoryStart(string tracesDir, string target, string capsId)
        {
            foreach (string prefix in new[] { "start-c", "optimize-c" })
            {
                string trace = Path.Combine(tracesDir, prefix + target + "-" + capsId + ".json");
                if (File.Exists(trace)) return trace;
            }
            return null;
        }

        // The demo's category start (CategoryStart); a full-tas demo plays from its run's fresh start.
        static string StartTrace(string tracesDir, string start)
        {
            string course = null, caps = null;
            if (!string.IsNullOrEmpty(start))
            {
                if (start.StartsWith("fulltas/", StringComparison.OrdinalIgnoreCase))
                {
                    string run = start.Substring("fulltas/".Length);
                    int cut = run.LastIndexOf('_');
                    string trace = Path.Combine(tracesDir, "fulltas-" + (cut > 0 ? run.Substring(0, cut) : run) + ".json");
                    if (File.Exists(trace)) return trace;
                    throw new InvalidDataException("no " + Path.GetFileName(trace));
                }
                int under = start.IndexOf('_');
                if (under > 0 && int.TryParse(start.Substring(0, under), out int number))
                {
                    course = number.ToString(CultureInfo.InvariantCulture);
                    caps = start.Substring(under + 1).Replace("_", "").ToLowerInvariant();
                }
            }
            if (course != null && CategoryStart(tracesDir, course, caps) is string found) return found;
            throw new InvalidDataException("no start trace for " + start);
        }

        // The cache key: what the miner reads and nothing else, so a note edit in techs.json or a new route saved to routes.json
        // doesn't re-mine: per demo source its tech's mined fields (TechDef), where it resolved (or why it didn't), its macro,
        // and every file it replays over; plus the engine build.
        static string CacheKey(List<DemoSource> sources)
        {
            using var sha = SHA256.Create();
            var hash = new StringBuilder();
            void Add(string what)
            {
                hash.Append(what).Append('\n');
                if (File.Exists(what) && new FileInfo(what).Length < 8 << 20) hash.Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(what))));
                else if (File.Exists(what)) hash.Append(new FileInfo(what).Length).Append(':').Append(File.GetLastWriteTimeUtc(what).Ticks);
                hash.Append('\n');
            }
            foreach (DemoSource s in sources)
                hash.Append(JsonSerializer.Serialize(s.Tech, Json)).Append('|').Append(s.Lab).Append(s.LabId).Append('|').Append(s.LabDemo).Append('|')
                    .Append(s.Trace).Append('|').Append(s.Macro == null ? "" : JsonSerializer.Serialize(s.Macro, Json)).Append('\n');
            foreach (string file in sources.SelectMany(s => s.KeyFiles).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal)) Add(file);
            // The miner's own version: a change to what it detects re-mines even when the build hashes below didn't move.
            hash.Append("miner ").Append(MinerVersion).Append('\n');
            foreach (string file in new[] { typeof(TechGrafts).Assembly.Location, typeof(Movement).Assembly.Location })
                if (!string.IsNullOrEmpty(file)) Add(file);
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(hash.ToString())));
        }

        static TechDemo Mine(DemoSource source, string tracesDir)
        {
            TechDef tech = source.Tech;
            var demo = new TechDemo
            {
                Tech = tech.id, Name = tech.name, Category = tech.category, Requires = tech.requires, Start = tech.start, Detector = tech.detector,
                KeyContact = tech.graftKey?.contact != null && Enum.TryParse(tech.graftKey.contact, out Contact want) ? want : null,
                KeyTick = tech.graftKey?.tick,
                Source = source.Lab ? "lab " + source.LabId + (source.LabDemoPath == null ? "" : " (" + Path.GetFileName(source.LabDemoPath) + ")")
                    : source.Macro != null ? Path.GetFileName(source.Trace) + (source.Savestate != null ? " + savestate" : "")
                    : source.Trace != null && source.Trace.EndsWith(".json") && File.Exists(source.Trace) ? Path.GetFileName(source.Trace) : null,
            };
            if (tech.graft == "todo") { demo.Marker = tech.graft; return demo; }
            if (source.Trace == null || !File.Exists(source.Trace))
            {
                demo.Error = source.Trace ?? NoDemoRoute;
                return demo;
            }
            try
            {
                WithDemo(source, (sim, inputs, specs, requires) =>
                {
                    if (requires != null) demo.Requires = requires;
                    demo.InputTicks = inputs.Count;
                    var log = new ContactLog();
                    var probe = new ContactProbe(sim);
                    int lastTick = 0;
                    RunResult run = sim.Run(inputs, afterTick: tick =>
                    {
                        lastTick = tick;
                        log.Tick(tick, sim.Player, probe.NearGate());
                        return true;
                    }, extraTicks: 200, onStep: _ =>
                    {
                        log.Slide((int)sim.Engine.RunTicks, sim.Player);
                        log.Step(sim);
                    });
                    demo.PhysicsTicks = run.Stepped;
                    demo.Finished = run.Finished;
                    demo.Died = run.EndReason != null && run.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                    if (demo.Died) log.Contacts.Add((lastTick, Contact.Died));
                    // A demo that finishes inside a dash (its steps consume no input tick) never had its last steps' contact changes
                    // looked at (a dash off a wall cling leaves the wall mid-dash): the finish files them at the dash's tick.
                    if (demo.Finished && (int)sim.Engine.RunTicks == lastTick) log.Tick(lastTick, sim.Player, probe.NearGate());
                    demo.Contacts.AddRange(log.Contacts);
                    if (specs != null) CutSpecs(demo, inputs, specs);
                    else Cut(demo, inputs);
                    return 0;
                });
            }
            catch (Exception ex)
            {
                demo.Error = ex.GetBaseException().Message;
            }
            return demo;
        }

        /// <summary>
        /// The demo's simulation at its start and its inputs, handed to <paramref name="run"/>: a lab demo on its lab (its own
        /// finish box judges it), a captured trace from its own start, a macro over its category start (from its savestate, with
        /// the omnidash its tech needs). Also the lab demo's graft specs (null: none) and what its windows need (null: the tech's
        /// own requires): the demo's own "requires", else the tech's narrowed to the lab's category.
        /// </summary>
        static T WithDemo<T>(DemoSource source, Func<Simulation, List<InputTick>, List<GraftSpec>, string, T> run)
        {
            TechDef tech = source.Tech;
            using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(source.Trace), new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = trace.RootElement;
            LabMapDef lab = source.Lab ? LabMapDef.Load(source.LabMapPath) : null;
            World world = World.Load(WorldOf(source.Trace));
            if (lab != null) LabMap.Inject(world, lab);
            var sim = new Simulation(world);
            sim.SetTarget(root);
            sim.LoadStart(root.GetProperty("start"));
            List<InputTick> inputs;
            List<GraftSpec> specs = null;
            string requires = null;
            if (lab != null)
            {
                // A lab demo's start is the lab's own (its geometry injected above), and its inputs are `lab solve`'s; a lab
                // run is judged on its own finish box, not the host trace's course (Cli\Lab.cs's lab replay).
                using JsonDocument solved = JsonDocument.Parse(File.ReadAllText(source.LabDemoPath));
                inputs = SolveInputs(solved.RootElement);
                specs = GraftSpec.Parse(solved.RootElement);
                // A lab demo's windows need what its tech needs and its lab runs with: the demo's own "requires", else the tech's
                // requires narrowed to the lab's category (a lab variant may need less than the tech's label: a side spring
                // without the omnidash, a spring double hit with no double jump; and a lab run with every unlock needs no more).
                if (solved.RootElement.TryGetProperty("requires", out JsonElement needs) && needs.ValueKind == JsonValueKind.String) requires = needs.GetString();
                else if (lab.CategoryGiven && Caps.TryParse(lab.category, out Caps labCaps) && Caps.TryParse(tech.requires ?? "NONE", out Caps techCaps))
                    requires = Narrow(techCaps, labCaps).Label;
                lab.UseDemoStart(solved.RootElement);
                DemoStart.ApplyLabStart(sim, lab);
                (float fx, float fy, float fw, float fh) = lab.Finish;
                (float ax, float ay) = lab.Anchor;
                sim.TargetCourse = -1;
                CapturePos finishAt = sim.ToCapture(new WorldPos(fx + ax, fy + ay));
                sim.TargetFinish = new CaptureRect(finishAt, fw, fh);
            }
            else if (source.Macro == null) inputs = SolveInputs(root);
            else
            {
                inputs = ToInputs(source.Macro);
                if (source.Savestate != null) DemoStart.ApplySavestate(sim, source.Savestate);
                // A demo plays with the omnidirectional dash on top of its start's unlocks (TechCatalog.ApplyTechAbilities).
                if (Caps.TryParse(tech.requires, out Caps needs) && needs.Omni) DemoStart.SetField(sim.Player, "omniDashUnlocked", "true");
                sim.SetStart(sim.Capture(0), 0);
            }
            return run(sim, inputs, specs, requires);
        }

        static List<InputTick> SolveInputs(JsonElement trace)
        {
            JsonElement list = trace.TryGetProperty("inputs", out JsonElement inputs) ? inputs : trace.GetProperty("ticks");
            return list.EnumerateArray().Select(InputTick.FromJson).ToList();
        }

        // A demo macro is raw input (TechCatalog refuses anything else): one input tick per duration tick, with the press,
        // release, dash, dash jump, spring dash, lock, dash turn and pause on the step's first tick (Solver.ToTicks's shape).
        static List<InputTick> ToInputs(MacroDefinition macro)
        {
            var ticks = new List<InputTick>();
            foreach (MacroStep s in macro.steps)
            {
                if (!s.enabled) continue;
                if (s.type != MacroStepType.RawInput) throw new InvalidDataException("demo step is " + s.type + ", not raw input");
                for (int i = 0; i < Math.Max(1, s.durationTicks); i++)
                    ticks.Add(new InputTick
                    {
                        x = s.rawMoveX, y = s.rawMoveY, held = s.rawJumpHeld,
                        press = i == 0 && s.rawJumpPressed, release = i == 0 && s.rawJumpReleased, dash = i == 0 && s.rawDashPressed,
                        dashJump = i == 0 && s.rawDashPressed ? s.dashJumpFrame : 0, springDash = i == 0 ? s.springDashFrame : 0,
                        lockX = i == 0 ? s.lockMoveX : null, lockY = i == 0 ? s.lockMoveY : null,
                        turnX = i == 0 && s.rawDashPressed ? s.dashTurnX : null, turnY = i == 0 && s.rawDashPressed ? s.dashTurnY : null,
                        reset = i == 0 && s.rawQuickRestart, pause = i == 0 ? s.pauseFrames : 0,
                        pauseTick = i == 0 && s.pauseFrames > 0 ? s.pauseTick : 0, pauseFrame = i == 0 && s.pauseFrames > 0 ? s.pauseFrame : 0,
                    });
            }
            return ticks;
        }

        // The input window around every contact of the demo, tagged by the contact type. The tech's key contact is the one
        // nearest the demo's last press, release or dash (the input that does the tech), or in a demo with no button at all its
        // first contact the player ran into (TouchContact); its window is flagged "key".
        static void Cut(TechDemo demo, List<InputTick> inputs)
        {
            int action = -1;
            for (int i = 0; i < inputs.Count; i++) if (inputs[i].press || inputs[i].release || inputs[i].dash) action = i;
            if (action < 0) action = demo.Contacts.Where(c => TouchContact(c.kind)).Select(c => c.tick).DefaultIfEmpty(0).First();
            // The tech's graftKey: its contact (nearest its tick) is where the tech happens.
            Contact? keyKind = null;
            if (demo.KeyContact is Contact want)
                foreach ((int tick, Contact kind) in demo.Contacts.Where(c => c.kind == want).OrderBy(c => demo.KeyTick is int t ? Math.Abs(c.tick - t) : 0).ThenBy(c => c.tick).Take(1))
                {
                    action = tick;
                    keyKind = kind;
                }
            var cuts = new List<(int tick, Contact kind, TechPattern pattern)>();
            foreach ((int tick, Contact kind) in demo.Contacts)
            {
                int lo = Math.Max(0, tick - WindowBefore), hi = Math.Min(inputs.Count - 1, tick + (kind == Contact.Died ? -1 : WindowAfter));
                if (hi < lo) continue;
                var window = inputs.GetRange(lo, hi - lo + 1);
                // A window with no button in it is travel, not tech, unless its contact is something the player ran into (a
                // spring, a spike, an overhang, a slope, a step): there the stick that meets it is the tech. A quick restart counts
                // as a button (the window is flagged Resets: only a run that may restart grafts it).
                if (!TouchContact(kind) && !window.Any(k => k.press || k.release || k.dash || k.reset)) continue;
                var pattern = new TechPattern
                {
                    Tech = demo.Tech, Name = demo.Name, Category = demo.Category,
                    Kind = kind, First = lo - tick, Last = hi - tick, Ticks = window.ToArray(), DemoContact = tick, DemoStart = demo.Source,
                    Dir = DirOf(kind, window, tick - lo), Resets = window.Any(k => k.reset),
                };
                Tag(pattern, demo.Requires, demo.Contacts, lo, hi);
                cuts.Add((tick, kind, pattern));
            }
            if (cuts.Count == 0) return;
            // Only the contacts around the demo's action make patterns: a full-tas demo's travel touches walls and gates
            // the whole way, and its windows are travel, not tech. At the same distance a per-step tech contact (a spring, a dash
            // jump, an air jump ...) goes before a plain one, and a step-up (mostly incidental: a dash along the floor) after.
            var near = cuts.OrderBy(c => Math.Abs(c.tick - action)).ThenBy(c => c.kind == keyKind ? 0 : 1).ThenBy(c => KeyRank(c.kind)).ThenBy(c => c.tick).ToList();
            var keep = new HashSet<TechPattern>(near.Take(MaxContacts).Select(c => c.pattern));
            foreach ((int tick, Contact kind, TechPattern pattern) in cuts)
            {
                if (!keep.Contains(pattern)) continue;
                pattern.Key = ReferenceEquals(pattern, near[0].pattern);
                AddWithTwin(demo, pattern);
            }
        }

        /// <summary>The pattern, and for a sided contact its twin for the other side (x negated, the chain's kinds mirrored).</summary>
        static void AddWithTwin(TechDemo demo, TechPattern pattern)
        {
            demo.Patterns.Add(pattern);
            Contact other = Mirror(pattern.Kind);
            if (other == pattern.Kind) return;
            TechPattern twin = pattern.Copy();
            twin.Kind = other;
            twin.Mirrored = true;
            twin.Ticks = pattern.Ticks.Select(Mirror).ToArray();
            twin.Dir = -pattern.Dir;
            if (pattern.Chain != null)
            {
                twin.Chain = pattern.Chain.Copy();
                twin.Chain.On = pattern.Chain.On.Select(Mirror).Distinct().ToArray();
                twin.Chain.Expect = pattern.Chain.Expect is Contact e ? Mirror(e) : null;
            }
            demo.Patterns.Add(twin);
        }

        /// <summary>A pattern's direction: a sided contact's side, else the sign of the window's first stick x at or after the
        /// contact (before it when there is none), 0 when the window never moves sideways.</summary>
        static int DirOf(Contact kind, IReadOnlyList<InputTick> window, int contact)
        {
            string name = kind.ToString();
            if (Sided(kind)) return name.EndsWith("Left", StringComparison.Ordinal) ? -1 : 1;
            for (int i = Math.Max(0, contact); i < window.Count; i++) if (window[i].x != 0f) return Math.Sign(window[i].x);
            for (int i = Math.Min(contact, window.Count) - 1; i >= 0; i--) if (window[i].x != 0f) return Math.Sign(window[i].x);
            return 0;
        }

        /// <summary>A demo with graft specs is cut only where they say, each spec's window a key pattern at each kind it
        /// grafts at (and its twin).</summary>
        static void CutSpecs(TechDemo demo, List<InputTick> inputs, List<GraftSpec> specs)
        {
            foreach (GraftSpec spec in specs)
            {
                var found = demo.Contacts.Where(c => c.kind == spec.Contact).OrderBy(c => spec.Tick is int t ? Math.Abs(c.tick - t) : 0).ThenBy(c => c.tick).ToList();
                if (found.Count == 0) { demo.Notes.Add($"graft spec: no {spec.Contact} contact"); continue; }
                int tick = found[0].tick;
                int lo = Math.Max(0, tick + spec.WindowLo), hi = Math.Min(inputs.Count - 1, tick + spec.WindowHi);
                if (hi < lo) { demo.Notes.Add($"graft spec at {spec.Contact} @{tick}: empty window"); continue; }
                var window = inputs.GetRange(lo, hi - lo + 1);
                int first = lo - tick;
                var vary = new List<PatternVary>();
                foreach (GraftSpec.Run r in spec.Vary)
                {
                    int index = r.At - first;
                    if (index < 0 || index >= window.Count) { demo.Notes.Add($"graft spec at {spec.Contact} @{tick}: vary at {r.At} is outside the window"); continue; }
                    int run = 1;
                    while (index + run < window.Count && window[index + run].Equals(window[index])) run++;
                    vary.Add(new PatternVary { Index = index, Run = run, Ticks = r.Ticks, X = r.X, Stick = r.Stick, DashJump = r.DashJump, RunButton = r.RunButton });
                }
                PatternChain chain = null;
                if (spec.ChainDepth > 0)
                {
                    chain = new PatternChain { Depth = spec.ChainDepth, On = spec.ChainOn, With = spec.ChainWith, ShiftLo = spec.ChainShiftLo, ShiftHi = spec.ChainShiftHi };
                    if (spec.ChainExpect is Contact expect)
                    {
                        var seen = demo.Contacts.Where(c => c.kind == expect && c.tick > tick && c.tick <= tick + WindowAfter).Select(c => (int?)c.tick).FirstOrDefault();
                        if (seen is int at) { chain.Expect = expect; chain.ExpectAt = at - tick; }
                        else demo.Notes.Add($"graft spec at {spec.Contact} @{tick}: the demo shows no {expect} after it");
                    }
                }
                var kinds = spec.As.Count > 0 ? spec.As : new List<GraftSpec.At> { new GraftSpec.At { Kind = spec.Contact } };
                foreach (GraftSpec.At a in kinds)
                {
                    var pattern = new TechPattern
                    {
                        Tech = demo.Tech, Name = demo.Name, Category = demo.Category,
                        Kind = a.Kind, First = first, Last = hi - tick, Ticks = window.ToArray(), DemoContact = tick, DemoStart = demo.Source,
                        Key = true, Dir = !Sided(a.Kind) && spec.Dir is int d ? d : DirOf(a.Kind, window, tick - lo),
                        ShiftLo = a.ShiftLo ?? spec.ShiftLo, ShiftHi = a.ShiftHi ?? spec.ShiftHi,
                        Vary = vary.Count > 0 ? vary.ToArray() : null, CoastX = spec.CoastX, CoastHeld = spec.CoastHeld, Tries = spec.Tries,
                        Weight = a.Weight ?? spec.Weight, Chain = chain, Spec = true, Resets = window.Any(k => k.reset),
                    };
                    Tag(pattern, demo.Requires, demo.Contacts, lo, hi);
                    AddWithTwin(demo, pattern);
                }
            }
        }

        /// <summary>One of a lab demo's graft specs (the demo file's "grafts" list): which of its contacts (the one of kind
        /// "contact" nearest its input "tick"), the window cut around it ("window": [first, last] offsets, default -10..30),
        /// the kinds it grafts at ("as": [{"kind", "shift", "weight"}], default its own contact's), where the window's contact
        /// goes relative to the run's ("shift": [lo, hi], default ±3), the runs of equal ticks sampled at graft time ("vary":
        /// [{"at": offset, "ticks", "x", "stick", "dashJump"}], each a choice list or an "a..b" integer range), the coast
        /// ("coast": {"x": choices relative to the window's direction, "held"}), the grafts per contact ("tries"), its pick
        /// weight ("weight") and what its run grafts next ("chain": {"depth", "on": kinds, "with": tech, "expect": kind,
        /// "shift": [lo, hi]}).</summary>
        sealed class GraftSpec
        {
            public Contact Contact;
            public List<At> As = new List<At>();
            public int? Tick;
            /// <summary>The window's direction at a contact with no side ("dir": the way the run was going, e.g. +1 for a backwards
            /// dash pressed to the left out of a run to the right); default the window's own first stick.</summary>
            public int? Dir;
            public int WindowLo = -WindowBefore, WindowHi = WindowAfter, ShiftLo = -Jitter, ShiftHi = Jitter, Tries = 1, Weight = 1;
            public List<Run> Vary = new List<Run>();
            public float[] CoastX;
            public bool? CoastHeld;
            public int ChainDepth, ChainShiftLo, ChainShiftHi = 8;
            public Contact[] ChainOn;
            public string ChainWith;
            public Contact? ChainExpect;

            public sealed class Run
            {
                public int At;
                public int[] Ticks, DashJump;
                public float[] X;
                public float[][] Stick;
                public bool RunButton;
            }

            public sealed class At
            {
                public Contact Kind;
                public int? ShiftLo, ShiftHi, Weight;
            }

            /// <summary>The demo's specs; null when it has none (its windows are cut as a plain demo's).</summary>
            public static List<GraftSpec> Parse(JsonElement demo)
            {
                if (!demo.TryGetProperty("grafts", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return null;
                var specs = new List<GraftSpec>();
                foreach (JsonElement s in list.EnumerateArray())
                {
                    var spec = new GraftSpec { Contact = Kind(s.GetProperty("contact")) };
                    if (s.TryGetProperty("as", out JsonElement a))
                        foreach (JsonElement k in a.EnumerateArray())
                            spec.As.Add(new At
                            {
                                Kind = Kind(k.GetProperty("kind")),
                                ShiftLo = k.TryGetProperty("shift", out JsonElement ks) ? ks[0].GetInt32() : null,
                                ShiftHi = k.TryGetProperty("shift", out JsonElement kh) ? kh[1].GetInt32() : null,
                                Weight = k.TryGetProperty("weight", out JsonElement kw) ? Math.Max(1, kw.GetInt32()) : null,
                            });
                    if (s.TryGetProperty("tick", out JsonElement t)) spec.Tick = t.GetInt32();
                    if (s.TryGetProperty("window", out JsonElement w)) (spec.WindowLo, spec.WindowHi) = (w[0].GetInt32(), w[1].GetInt32());
                    if (s.TryGetProperty("shift", out JsonElement sh)) (spec.ShiftLo, spec.ShiftHi) = (sh[0].GetInt32(), sh[1].GetInt32());
                    if (s.TryGetProperty("tries", out JsonElement tr)) spec.Tries = Math.Max(1, tr.GetInt32());
                    if (s.TryGetProperty("dir", out JsonElement di)) spec.Dir = Math.Sign(di.GetInt32());
                    if (s.TryGetProperty("weight", out JsonElement we)) spec.Weight = Math.Max(1, we.GetInt32());
                    if (s.TryGetProperty("vary", out JsonElement vary))
                        foreach (JsonElement v in vary.EnumerateArray())
                            spec.Vary.Add(new Run
                            {
                                At = v.GetProperty("at").GetInt32(),
                                Ticks = v.TryGetProperty("ticks", out JsonElement n) ? Ints(n) : null,
                                DashJump = v.TryGetProperty("dashJump", out JsonElement d) ? Ints(d) : null,
                                X = v.TryGetProperty("x", out JsonElement x) ? Floats(x) : null,
                                Stick = v.TryGetProperty("stick", out JsonElement st) ? st.EnumerateArray().Select(p => new[] { p[0].GetSingle(), p[1].GetSingle() }).ToArray() : null,
                                RunButton = v.TryGetProperty("button", out JsonElement b) && b.GetString() == "run",
                            });
                    if (s.TryGetProperty("coast", out JsonElement c))
                    {
                        if (c.TryGetProperty("x", out JsonElement cx)) spec.CoastX = Floats(cx);
                        if (c.TryGetProperty("held", out JsonElement ch)) spec.CoastHeld = ch.GetBoolean();
                    }
                    if (s.TryGetProperty("chain", out JsonElement chain))
                    {
                        spec.ChainDepth = chain.GetProperty("depth").GetInt32();
                        spec.ChainOn = chain.GetProperty("on").EnumerateArray().Select(Kind).ToArray();
                        if (chain.TryGetProperty("with", out JsonElement with)) spec.ChainWith = with.GetString();
                        if (chain.TryGetProperty("expect", out JsonElement expect)) spec.ChainExpect = Kind(expect);
                        if (chain.TryGetProperty("shift", out JsonElement cs)) (spec.ChainShiftLo, spec.ChainShiftHi) = (cs[0].GetInt32(), cs[1].GetInt32());
                    }
                    specs.Add(spec);
                }
                return specs;
            }

            static Contact Kind(JsonElement e) { return Enum.Parse<Contact>(e.GetString()); }

            // A choice list, or an "a..b" integer range.
            static int[] Ints(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                string[] ends = e.GetString().Split("..");
                int a = int.Parse(ends[0], CultureInfo.InvariantCulture), b = int.Parse(ends[1], CultureInfo.InvariantCulture);
                return Enumerable.Range(a, b - a + 1).ToArray();
            }

            static float[] Floats(JsonElement e) { return e.EnumerateArray().Select(x => x.GetSingle()).ToArray(); }
        }

        /// <summary>A contact the player runs into (a spring, a spike, a slanted surface, a step), where the stick meeting it is
        /// the tech even with no button pressed.</summary>
        static bool TouchContact(Contact kind)
        {
            return kind is Contact.SpringHitLeft or Contact.SpringHitRight or Contact.SpringHitUp or Contact.SpringHitDown
                or Contact.SpikeTouched or Contact.SpikeCleared or Contact.OverhangLeft or Contact.OverhangRight
                or Contact.SlopeLeft or Contact.SlopeRight or Contact.SteppedUpLeft or Contact.SteppedUpRight;
        }

        /// <summary>The unlocks both hold (a label Caps parses: block swap and the omnidash only with a dash).</summary>
        static Caps Narrow(Caps a, Caps b)
        {
            var both = new Caps
            {
                WallJump = a.WallJump && b.WallJump, Dashes = Math.Min(a.Dashes, b.Dashes), AirJumps = Math.Min(a.AirJumps, b.AirJumps),
                Orbs = a.Orbs && b.Orbs, Zip = a.Zip && b.Zip,
            };
            both.BlockSwap = a.BlockSwap && b.BlockSwap && both.Dash;
            both.Omni = a.Omni && b.Omni && both.Dash;
            return both;
        }

        static int KeyRank(Contact kind)
        {
            if (kind is Contact.SteppedUpLeft or Contact.SteppedUpRight) return 2;
            return kind >= Contact.SpringHitLeft ? 0 : 1;
        }

        static void Tag(TechPattern pattern, string requires, List<(int tick, Contact kind)> contacts, int lo, int hi)
        {
            Caps caps = Caps.Parse(requires);
            // The window presses dash: it needs the dash unlock on top of the tech's own label; an air jump the demo spent in it
            // (a DoubleJump contact inside it) needs the double jump.
            if (pattern.Ticks.Any(k => k.dash || k.dashJump > 0 || k.springDash > 0)) caps.Dashes = Math.Max(caps.Dashes, 1);
            if (contacts.Any(c => c.kind == Contact.DoubleJump && c.tick - 1 >= lo && c.tick - 1 <= hi)) caps.AirJumps = Math.Max(caps.AirJumps, 1);
            pattern.Requires = caps;
        }

        /// <summary>A sided contact's other side (its name's Left and Right swapped); any other kind is its own.</summary>
        static Contact Mirror(Contact kind)
        {
            string name = kind.ToString();
            return name.EndsWith("Left", StringComparison.Ordinal) ? Enum.Parse<Contact>(name[..^4] + "Right")
                : name.EndsWith("Right", StringComparison.Ordinal) ? Enum.Parse<Contact>(name[..^5] + "Left") : kind;
        }

        static InputTick Mirror(InputTick t)
        {
            t.x = t.x == 0f ? 0f : -t.x;
            if (t.lockX.HasValue) t.lockX = t.lockX == 0f ? 0f : -t.lockX;
            if (t.turnX.HasValue) t.turnX = t.turnX == 0f ? 0f : -t.turnX;
            return t;
        }

        // The same window (every InputTick field, InputTick.Equals) at the same contact kind and offset is one pattern.
        void Dedupe()
        {
            var seen = new HashSet<TechPattern>(SameWindow.Instance);
            Patterns.RemoveAll(p => !seen.Add(p));
        }

        sealed class SameWindow : IEqualityComparer<TechPattern>
        {
            public static readonly SameWindow Instance = new SameWindow();
            public bool Equals(TechPattern a, TechPattern b) { return a.Kind == b.Kind && a.First == b.First && a.Ticks.SequenceEqual(b.Ticks); }
            public int GetHashCode(TechPattern p)
            {
                var hash = new HashCode();
                hash.Add(p.Kind);
                hash.Add(p.First);
                foreach (InputTick t in p.Ticks) hash.Add(t);
                return hash.ToHashCode();
            }
        }

        // ---- the CLI listing ----

        /// <summary>techgrafts &lt;traces dir or trace&gt; [--refresh] [--tech-dir &lt;dir&gt;]: every tech's demo replayed and mined,
        /// per contact. The demos' traces, savestates, lab maps and the cache resolve from the configured traces dir
        /// (the game's config folder's enginesim dir when it exists) whatever trace is listed; --tech-dir overrides it.</summary>
        public static int Command(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: techgrafts <traces dir or trace.json> [--refresh] [--tech-dir <dir>]   mine the tech tab's demos into graft patterns");
                return 2;
            }
            string path = args[0];
            string tracesDir = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
            int at = Array.IndexOf(args, "--tech-dir");
            TechGrafts mined = Load(tracesDir, args.Contains("--refresh"), at >= 0 && at + 1 < args.Length ? args[at + 1] : null);
            Console.WriteLine($"tech grafts: {mined.Patterns.Count} patterns over {mined.Demos.Count(d => d.Patterns.Count > 0)} of {mined.Demos.Count(d => d.Marker == null)} demos"
                + string.Concat(mined.Demos.Where(d => d.Marker != null).GroupBy(d => d.Marker).Select(g => $" ({g.Count()} {g.Key} techs)"))
                + $" ({(mined.FromCache ? "cache" : "mined")}, key {mined.Key.Substring(0, 12)}, {mined.CacheFile})");
            foreach (TechDemo demo in mined.Demos)
            {
                string head = $"{demo.Tech} ({demo.Name}, {demo.Category}, needs {demo.Requires})";
                if (demo.Marker == "todo") { Console.WriteLine($"{head,-70} todo: not demonstrable yet (no graft)"); continue; }
                if (demo.Error != null) { Console.WriteLine($"{head,-70} no demo: {demo.Error}"); continue; }
                Console.WriteLine($"{head,-70} {demo.Source}: {demo.InputTicks} input ticks, {demo.PhysicsTicks} physics, "
                    + $"{(demo.Finished ? "finished" : demo.Died ? "died" : "ran out")}, {demo.Contacts.Count} contacts");
                if (args.Contains("--contacts")) Console.WriteLine("  contacts: " + string.Join(" ", demo.Contacts.Select(c => c.kind + "@" + c.tick)));
                foreach (string note in demo.Notes) Console.WriteLine("  note: " + note);
                foreach (TechPattern p in demo.Patterns)
                    Console.WriteLine("  " + (p.Key ? "key " : "    ") + (p.Mirrored ? "mirrored " : "") + p.Kind + " @" + p.DemoContact
                        + ": " + p.First + ".." + p.Last + " -> " + p.Ticks.Length + " ticks"
                        + (p.Requires.Id == "none" ? "" : " [needs " + p.Requires.Label + "]") + Spread(p));
                foreach (TechPattern p in demo.Patterns.Where(p => p.Key && !p.Mirrored))
                    Console.WriteLine("      " + Format(p));
            }
            // Per contact kind: the techs with a window there (key windows, all windows; a spec'd tech's weight).
            Console.WriteLine();
            Console.WriteLine("by contact kind (tech key/all windows, xN: pick weight):");
            foreach (var kind in mined.Patterns.GroupBy(p => p.Kind).OrderBy(g => g.Key))
                Console.WriteLine($"  {kind.Key,-16} " + string.Join(", ", kind.GroupBy(p => p.Tech).OrderByDescending(g => g.Max(p => p.Weight)).ThenBy(g => g.Key)
                    .Select(g => g.Key + " " + g.Count(p => p.Key) + "/" + g.Count() + (g.Max(p => p.Weight) > 1 ? " x" + g.Max(p => p.Weight) : ""))));
            return 0;
        }

        // A spec'd pattern's spread: its shift, sampled runs, coast, tries, weight and chain.
        static string Spread(TechPattern p)
        {
            var s = new StringBuilder();
            if (p.ShiftLo != -Jitter || p.ShiftHi != Jitter) s.Append($" shift {Offset(p.ShiftLo)}..{Offset(p.ShiftHi)}");
            string Choices<T>(T[] c) => c.Length > 6 ? c[0] + "|..|" + c[^1] + " (" + c.Length + ")" : string.Join("|", c);
            foreach (PatternVary v in p.Vary ?? Array.Empty<PatternVary>())
            {
                s.Append($" vary@{Offset(p.First + v.Index)}");
                if (v.Ticks != null) s.Append(" ticks ").Append(Choices(v.Ticks));
                if (v.X != null) s.Append(" x ").Append(Choices(v.X));
                if (v.Stick != null) s.Append(" stick ").Append(Choices(v.Stick.Select(k => k[0] + ";" + k[1]).ToArray()));
                if (v.DashJump != null) s.Append(" dashjump ").Append(Choices(v.DashJump));
                if (v.RunButton) s.Append(" button as the run's");
            }
            if (p.CoastX != null) s.Append(" coast x ").Append(Choices(p.CoastX));
            if (p.CoastHeld == false) s.Append(" coast released");
            if (p.Tries > 1) s.Append($" tries {p.Tries}");
            if (p.Weight > 1) s.Append($" weight {p.Weight}");
            if (p.Chain is PatternChain c)
                s.Append($" chain {c.Depth} on {string.Join("|", c.On)}{(c.With != null ? " with " + c.With : "")}{(c.Expect != null ? $" expect {c.Expect}@{Offset(c.ExpectAt)}" : "")} shift {c.ShiftLo}..{c.ShiftHi}");
            return s.ToString();
        }

        static string Format(TechPattern p)
        {
            var text = new StringBuilder();
            for (int i = 0; i < p.Ticks.Length;)
            {
                int j = i + 1;
                while (j < p.Ticks.Length && p.Ticks[j].Equals(p.Ticks[i])) j++;
                InputTick t = p.Ticks[i];
                if (text.Length > 0) text.Append(' ');
                text.Append(Offset(p.First + i));
                if (j - i > 1) text.Append("..").Append(Offset(p.First + j - 1));
                text.Append(" x").Append(t.x.ToString("0.##", CultureInfo.InvariantCulture));
                if (t.y != 0f) text.Append(" y").Append(t.y.ToString("0.#", CultureInfo.InvariantCulture));
                if (t.press) text.Append(" press");
                if (t.release) text.Append(" release");
                if (t.held) text.Append(" hold");
                if (t.dash) text.Append(" dash");
                if (t.dashJump > 0) text.Append(" dashjump@").Append(t.dashJump);
                if (t.turnX.HasValue || t.turnY.HasValue) text.Append(" turn").Append(t.turnX?.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(t.turnY.HasValue ? "," + t.turnY.Value.ToString("0.##", CultureInfo.InvariantCulture) : "");
                if (t.pause > 0) text.Append(" pause").Append(t.pause).Append('@').Append(t.pauseTick).Append('.').Append(t.pauseFrame);
                if (t.springDash > 0) text.Append(" springdash@").Append(t.springDash);
                if (t.lockX.HasValue || t.lockY.HasValue) text.Append(" lock").Append(t.lockX).Append(',').Append(t.lockY);
                if (t.reset) text.Append(" reset");
                i = j;
            }
            return text.ToString();
        }

        static string Offset(int offset) { return offset >= 0 ? "+" + offset : offset.ToString(CultureInfo.InvariantCulture); }
    }
}
