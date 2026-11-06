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
    public enum Contact : byte
    {
        Died, LeftGround, Landed, TouchedWall, LeftWall, CornerHyper, WallBehindLeft, WallBehindRight, WallAheadLeft, WallAheadRight,
        NearGateLeft, NearGateRight, WallAboveLeft, WallAboveRight,
        SpringHitLeft, SpringHitRight, SpringHitUp, SpringHitDown, SpikeTouched, SpikeCleared, OverhangLeft, OverhangRight,
        SlopeLeft, SlopeRight, DoubleJump, DashJump, WallBoostLeft, WallBoostRight, Paused, SteppedUpLeft, SteppedUpRight
    }

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

    public static class ContactRays
    {
        internal static readonly AccessTools.FieldRef<Movement, LayerMask> GroundLayer = AccessTools.FieldRefAccess<Movement, LayerMask>("groundRaycastLayer");
        internal static readonly AccessTools.FieldRef<Movement, bool> CanHyper = AccessTools.FieldRefAccess<Movement, bool>("canHyper");
        internal static readonly AccessTools.FieldRef<Movement, float> CoyoteFrames = AccessTools.FieldRefAccess<Movement, float>("coyoteFrames");

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

        public static float WallBehind(Movement player)
        {
            if (player.onGround || player.dashActive || player.Velocity.y >= 580f) return 0f;
            float back = player.facingRight ? -1f : 1f;
            Vector3 p = player.transform.position;
            return Physics2D.Raycast(new Vector2(p.x + back * 15f, p.y - 18f), new Vector2(back, 0f), 12f, GroundLayer(player)) ? back : 0f;
        }

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

    public sealed class ContactProbe
    {
        readonly Simulation _sim;

        public ContactProbe(Simulation sim) { _sim = sim; }

        // Gate box is the 40x54 hurtbox (x + 5*facing +-20, y +-27); the 41x72 body box excludes their Default layer.
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

        public float Behind => _wasBehind;

        public void DropBefore(int tick)
        {
            int kept = 0;
            for (int i = 0; i < Contacts.Count; i++)
                if (Contacts[i].tick >= tick) Contacts[kept++] = Contacts[i];
            Contacts.RemoveRange(kept, Contacts.Count - kept);
        }

        // Holds the last tick's contact state so a run resumed from a snapshot sees the same contacts as the run that took it.
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

        public void Slide(int runTicks, Movement player)
        {
            bool dash = player.cutsceneMode == Movement.cutsceneModes.dash;
            if (dash && !_wasDash) _dashStart = runTicks - 1;
            bool slide = dash && player.onGround && ContactRays.CanHyper(player) && ContactRays.CoyoteFrames(player) < player.maxCoyoteFrames;
            if (slide && !_wasSlide) Contacts.Add((_dashStart, Contact.CornerHyper));
            _wasDash = dash;
            _wasSlide = slide;
        }

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

    public static class DemoStart
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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

        public static void ApplySavestate(Simulation sim, string path)
        {
            WorldPos position = sim.PlayerWorld;
            Vector2 velocity = sim.Physics.PlayerBody() is var body ? new Vector2(body.vx, body.vy) : default;
            float? scaleX = null;
            ApplySavestate(sim, path, ref position, ref velocity, ref scaleX);
            Place(sim, position, velocity, scaleX);
        }

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
            colouredBlockSwapper swapper = sim.World.All<colouredBlockSwapper>().FirstOrDefault();
            if (s.blueActive is bool blue && swapper != null) swapper.swapBlocks(blue);
            else sim.SetLabColours(s.blueActive ?? swapper?.isBlueActive ?? true);
            // A lab start is often mid-air: demo inputs start on the first tick, inside it (Simulation.StartsInsideTick), as in-game.
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

    public sealed class TechPattern
    {
        public string Tech;
        public string Name;
        public string Category;
        public Caps Requires;
        public Contact Kind;
        public int First, Last;
        public InputTick[] Ticks;
        public bool Key;
        public bool Mirrored;
        public int DemoContact;
        public string DemoStart;
        public int Dir;
        public int ShiftLo = -TechGrafts.Jitter, ShiftHi = TechGrafts.Jitter;
        public PatternVary[] Vary;
        public float[] CoastX;
        public bool? CoastHeld;
        public int[] CoastHolds;
        public int Tries = 1;
        public int Weight = 1;
        public PatternChain Chain;
        public bool Spec;
        public bool Resets;

        [JsonIgnore] public string Mode => _mode ??= "tech graft " + Tech;
        string _mode;

        public override string ToString() => Tech + " " + Kind + " " + First + ".." + Last;

        internal TechPattern Copy() => (TechPattern)MemberwiseClone();
    }

    public sealed class PatternVary
    {
        public int Index, Run;
        public int[] Ticks;
        public float[] X;
        public float[][] Stick;
        public int[] DashJump;
        public bool RunButton;
    }

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

    public sealed class TechDemo
    {
        public string Tech, Name, Category, Requires, Start, Source, Error;
        public string Detector;
        public Contact? KeyContact;
        public int? KeyTick;
        public int InputTicks, PhysicsTicks;
        public bool Finished, Died;
        public string Marker;
        public List<(int tick, Contact kind)> Contacts = new List<(int, Contact)>();
        public List<TechPattern> Patterns = new List<TechPattern>();
        public List<string> Notes = new List<string>();
    }

    public sealed partial class TechGrafts
    {
        public List<TechDemo> Demos = new List<TechDemo>();
        public List<TechPattern> Patterns = new List<TechPattern>();
        public string CacheFile, Key;
        public bool FromCache;

        public const int WindowBefore = 10, WindowAfter = 30;
        public const int MaxContacts = 8;
        public const int Jitter = 3;
        public const int GraftCoast = 40;

        public const double KeyShare = .6;

        // Miner version is part of the cache key: bump it when detection changes.
        const int MinerVersion = 4;

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

        public TechPattern Pick(Contact kind, System.Random rng, bool resets = false)
        {
            Ready ready = (resets ? _readyResets : _ready)[(int)kind];
            if (ready.Techs.Length == 0) return null;
            int pick = rng.Next(ready.Total);
            ReadyTech tech = ready.Techs[^1];
            foreach (ReadyTech t in ready.Techs) { if (pick < t.Weight) { tech = t; break; } pick -= t.Weight; }
            return tech.Key.Length > 0 && rng.NextDouble() < KeyShare ? tech.Key[rng.Next(tech.Key.Length)] : tech.All[rng.Next(tech.All.Length)];
        }

        public TechPattern PickChain(Contact kind, string tech, System.Random rng, bool resets = false)
        {
            if (tech == null) return Pick(kind, rng, resets);
            foreach (ReadyTech t in (resets ? _readyResets : _ready)[(int)kind].Techs)
                if (t.Tech == tech) return t.Key.Length > 0 ? t.Key[rng.Next(t.Key.Length)] : t.All[rng.Next(t.All.Length)];
            return null;
        }

        public bool Offers(Contact kind, bool resets = false) { return (resets ? _readyResets : _ready)[(int)kind].Techs.Length > 0; }

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
            int skip = 0;
            if (start < Math.Max(lo, 1)) { skip = Math.Max(lo, 1) - start; start = Math.Max(lo, 1); }
            if (skip >= window.Count || start > run.Count) return false;
            var t = run.GetRange(0, start);
            InputTick first = window[skip];
            bool down = start > 0 && (t[start - 1].held || t[start - 1].press) && !t[start - 1].release;
            if (down && !first.held && !first.release) first.release = true;
            else if (!down && first.release && !first.press) first.release = false;
            if (!down && first.held && !first.press && !(first.dash && first.dashJump > 0)) first.press = true;
            for (int i = skip; i < window.Count; i++) t.Add(i == skip ? first : window[i]);
            InputTick last = t[t.Count - 1];
            bool endDown = (last.held || last.press || last.dash && last.dashJump > 0) && !last.release;
            float coastX = p.CoastX != null ? p.CoastX[rng.Next(p.CoastX.Length)] * (dir == 0 ? 1 : dir) : last.x;
            int hold = !endDown ? 0 : p.CoastHolds != null ? p.CoastHolds[rng.Next(p.CoastHolds.Length)] : p.CoastHeld ?? true ? GraftCoast : 0;
            hold = Math.Min(hold, GraftCoast - 1);
            for (int i = 0; i < GraftCoast; i++) t.Add(new InputTick { x = coastX, y = last.y, held = i < hold, release = endDown && i == hold });
            grafted = t;
            return true;
        }

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

        public static bool Sided(Contact kind) { return Mirror(kind) != kind; }

        internal sealed class TechDef
        {
            public string id, name, category, requires, start, routeId, labmap, labDemo, detector;
            public string LiveLabmap, LiveLabDemo;
            public GraftKeyDef graftKey;
            public string[] graftDemos;
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
            public string Trace, Savestate;
            public MacroDefinition Macro;
            public bool Lab;
            public string LabId, LabDemo, LabMapPath, LabDemoPath;
            public List<string> KeyFiles = new List<string>();
        }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true,
            Converters = { new JsonStringEnumConverter(), new CapsConverter() } };

        sealed class CapsConverter : JsonConverter<Caps>
        {
            public override Caps Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Caps.Parse(reader.GetString());
            public override void Write(Utf8JsonWriter writer, Caps value, JsonSerializerOptions options) => writer.WriteStringValue(value.Id);
        }

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
                        return cached.WithRoutes(tracesDir, refresh, cache);
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
            return mined.WithRoutes(tracesDir, refresh, cache);
        }

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
                foreach ((string id, string techId, _) in labmaps) if (techId == tech.id && !labs.Any(l => l.id == id)) AddLab(id, null);
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

        public sealed class CheckDemo
        {
            public string Tech, Category, Detector, Name, Label, Error;
            public bool Ghost;
            internal DemoSource Source;

            public T Play<T>(Func<Simulation, List<InputTick>, T> run) => WithDemo(Source, (sim, inputs, _, _) => run(sim, inputs));
        }

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

        public static string ResolveTechDir(string tracesDir, string techDir = null)
        {
            if (!string.IsNullOrEmpty(techDir)) return Path.GetFullPath(techDir);
            string canonical = Path.Combine(
                Environment.GetEnvironmentVariable("IGTAP_GAME")
                    ?? @"C:\Program Files (x86)\Steam\steamapps\common\IGTAP an Incremental Game That's Also a Platformer",
                "BepInEx", "config", "IGTAPTasMod", "enginesim");
            return Directory.Exists(canonical) ? canonical : Path.GetFullPath(tracesDir);
        }

        public static List<(string id, string category, string detector)> Categories(string tracesDir, string techDir = null)
        {
            tracesDir = ResolveTechDir(tracesDir, techDir);
            string config = Directory.Exists(Path.GetDirectoryName(tracesDir)) ? Path.GetDirectoryName(tracesDir) : null;
            return Catalog(config).Select(t => (t.id, t.category, t.detector)).ToList();
        }

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

        static string SeedTechs()
        {
            string root = SourceRoot();
            if (root != null && File.Exists(Path.Combine(root, "data", "techs.json"))) return Path.Combine(root, "data", "techs.json");
            string copy = Path.Combine(AppContext.BaseDirectory, "techs.json");
            return File.Exists(copy) ? copy : null;
        }

        public static string SourceRoot()
        {
            static bool IsRoot(string dir) => File.Exists(Path.Combine(dir, "data", "techs.json")) && Directory.Exists(Path.Combine(dir, "enginesim", "Runtime"));
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (IsRoot(dir.FullName)) return dir.FullName;
            string built = typeof(TechGrafts).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IgtapRepo")?.Value;
            return !string.IsNullOrEmpty(built) && Directory.Exists(built) && IsRoot(built) ? Path.GetFullPath(built) : null;
        }

        static IEnumerable<string> LabDirs(string config)
        {
            string root = SourceRoot();
            if (root != null) yield return Path.Combine(root, "data", "labmaps");
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

        public static string CategoryStart(string tracesDir, string target, string capsId)
        {
            foreach (string prefix in new[] { "start-c", "optimize-c" })
            {
                string trace = Path.Combine(tracesDir, prefix + target + "-" + capsId + ".json");
                if (File.Exists(trace)) return trace;
            }
            return null;
        }

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
                    // A demo finishing inside a dash never saw its last steps' contact changes; the finish files them at the dash's tick.
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
                using JsonDocument solved = JsonDocument.Parse(File.ReadAllText(source.LabDemoPath));
                inputs = SolveInputs(solved.RootElement);
                specs = GraftSpec.Parse(solved.RootElement);
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

        static void Cut(TechDemo demo, List<InputTick> inputs)
        {
            int action = -1;
            for (int i = 0; i < inputs.Count; i++) if (inputs[i].press || inputs[i].release || inputs[i].dash) action = i;
            if (action < 0) action = demo.Contacts.Where(c => TouchContact(c.kind)).Select(c => c.tick).DefaultIfEmpty(0).First();
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
            var near = cuts.OrderBy(c => Math.Abs(c.tick - action)).ThenBy(c => c.kind == keyKind ? 0 : 1).ThenBy(c => KeyRank(c.kind)).ThenBy(c => c.tick).ToList();
            var keep = new HashSet<TechPattern>(near.Take(MaxContacts).Select(c => c.pattern));
            foreach ((int tick, Contact kind, TechPattern pattern) in cuts)
            {
                if (!keep.Contains(pattern)) continue;
                pattern.Key = ReferenceEquals(pattern, near[0].pattern);
                AddWithTwin(demo, pattern);
            }
        }

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

        static int DirOf(Contact kind, IReadOnlyList<InputTick> window, int contact)
        {
            string name = kind.ToString();
            if (Sided(kind)) return name.EndsWith("Left", StringComparison.Ordinal) ? -1 : 1;
            for (int i = Math.Max(0, contact); i < window.Count; i++) if (window[i].x != 0f) return Math.Sign(window[i].x);
            for (int i = Math.Min(contact, window.Count) - 1; i >= 0; i--) if (window[i].x != 0f) return Math.Sign(window[i].x);
            return 0;
        }

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
                        Vary = vary.Count > 0 ? vary.ToArray() : null, CoastX = spec.CoastX, CoastHeld = spec.CoastHeld, CoastHolds = spec.CoastHolds, Tries = spec.Tries,
                        Weight = a.Weight ?? spec.Weight, Chain = chain, Spec = true, Resets = window.Any(k => k.reset),
                    };
                    Tag(pattern, demo.Requires, demo.Contacts, lo, hi);
                    AddWithTwin(demo, pattern);
                }
            }
        }

        sealed class GraftSpec
        {
            public Contact Contact;
            public List<At> As = new List<At>();
            public int? Tick;
            public int? Dir;
            public int WindowLo = -WindowBefore, WindowHi = WindowAfter, ShiftLo = -Jitter, ShiftHi = Jitter, Tries = 1, Weight = 1;
            public List<Run> Vary = new List<Run>();
            public float[] CoastX;
            public bool? CoastHeld;
            public int[] CoastHolds;
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
                        if (c.TryGetProperty("held", out JsonElement ch))
                        {
                            if (ch.ValueKind == JsonValueKind.Array) spec.CoastHolds = Ints(ch);
                            else spec.CoastHeld = ch.GetBoolean();
                        }
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

            static int[] Ints(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                string[] ends = e.GetString().Split("..");
                int a = int.Parse(ends[0], CultureInfo.InvariantCulture), b = int.Parse(ends[1], CultureInfo.InvariantCulture);
                return Enumerable.Range(a, b - a + 1).ToArray();
            }

            static float[] Floats(JsonElement e) { return e.EnumerateArray().Select(x => x.GetSingle()).ToArray(); }
        }

        static bool TouchContact(Contact kind)
        {
            return kind is Contact.SpringHitLeft or Contact.SpringHitRight or Contact.SpringHitUp or Contact.SpringHitDown
                or Contact.SpikeTouched or Contact.SpikeCleared or Contact.OverhangLeft or Contact.OverhangRight
                or Contact.SlopeLeft or Contact.SlopeRight or Contact.SteppedUpLeft or Contact.SteppedUpRight;
        }

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
            if (pattern.Ticks.Any(k => k.dash || k.dashJump > 0 || k.springDash > 0)) caps.Dashes = Math.Max(caps.Dashes, 1);
            if (contacts.Any(c => c.kind == Contact.DoubleJump && c.tick - 1 >= lo && c.tick - 1 <= hi)) caps.AirJumps = Math.Max(caps.AirJumps, 1);
            pattern.Requires = caps;
        }

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
            Console.WriteLine();
            Console.WriteLine("by contact kind (tech key/all windows, xN: pick weight):");
            foreach (var kind in mined.Patterns.GroupBy(p => p.Kind).OrderBy(g => g.Key))
                Console.WriteLine($"  {kind.Key,-16} " + string.Join(", ", kind.GroupBy(p => p.Tech).OrderByDescending(g => g.Max(p => p.Weight)).ThenBy(g => g.Key)
                    .Select(g => g.Key + " " + g.Count(p => p.Key) + "/" + g.Count() + (g.Max(p => p.Weight) > 1 ? " x" + g.Max(p => p.Weight) : ""))));
            return 0;
        }

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
            if (p.CoastHolds != null) s.Append(" coast held ").Append(Choices(p.CoastHolds));
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
