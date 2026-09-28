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
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>Where a run's tech lives: the ticks where the player's contact changes. Recorded by <see cref="ContactLog"/>;
    /// <see cref="OfflineSolver"/>'s grafts and the mined tech grafts (<see cref="TechGrafts"/>) both key on it.</summary>
    public enum Contact : byte
    {
        Died, LeftGround, Landed, TouchedWall, LeftWall, CornerHyper, WallBehindLeft, WallBehindRight, WallAheadLeft, WallAheadRight,
        NearGateLeft, NearGateRight, WallAboveLeft, WallAboveRight
    }

    /// <summary>The ray probes a contact is made of, and the Movement getters they need: the same detection the solver's
    /// grafts and the demo miner share.</summary>
    public static class ContactRays
    {
        public static readonly Func<Movement, LayerMask> GroundLayer = FieldGetter<LayerMask>("groundRaycastLayer");
        public static readonly Func<Movement, bool> CanHyper = FieldGetter<bool>("canHyper");
        public static readonly Func<Movement, float> CoyoteFrames = FieldGetter<float>("coyoteFrames");

        public static Func<Movement, T> FieldGetter<T>(string name)
        {
            FieldInfo field = typeof(Movement).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(Movement), name);
            ParameterExpression player = Expression.Parameter(typeof(Movement));
            return Expression.Lambda<Func<Movement, T>>(Expression.Field(player, field), player).Compile();
        }

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
        /// hitbox (x + 5 × facing ± 20.5, y ± 36) at its height, 0 otherwise.</summary>
        public float NearGate()
        {
            if (_sim.TimingCourse() != null) return 0f;
            Movement player = _sim.Player;
            ScenePos p = ScenePos.From(player.transform.position);
            float cx = p.x + (player.facingRight ? 5f : -5f);
            foreach (Rect box in Gates())
            {
                if (p.y + 36f < box.yMin || p.y - 36f > box.yMax) continue;
                if (cx + 20.5f < box.xMin && box.xMin - (cx + 20.5f) <= 16f) return 1f;
                if (cx - 20.5f > box.xMax && (cx - 20.5f) - box.xMax <= 16f) return -1f;
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
                if (course == null || _sim.TargetCourse != 0 && course.courseNumber != _sim.TargetCourse) continue;
                BoxCollider2D box = gate.GetComponent<BoxCollider2D>();
                if (box == null) continue;
                Vector3 s = gate.transform.lossyScale, at = gate.transform.position;
                float w = Math.Abs(box.size.x * s.x), h = Math.Abs(box.size.y * s.y);
                _gates.Add(new Rect(at.x + box.offset.x * s.x - w / 2f, at.y + box.offset.y * s.y - h / 2f, w, h));
            }
            return _gates;
        }
        List<Rect> _gates;
        ScenePos _gatesOrigin;
    }

    /// <summary>The contacts of one run: every tick where the player's contact changes (it leaves the ground at a ledge, lands,
    /// touches or leaves a wall, dies) plus the wall clip's set-up probes behind and ahead and the start gate in reach, and
    /// (on every physics step, because a dash's ticks consume no input) the corner hyper's flat dash leaving the floor's edge.</summary>
    public sealed class ContactLog
    {
        public readonly List<(int tick, Contact kind)> Contacts = new List<(int, Contact)>();
        bool _primed, _wasGround, _wasWall, _wasDash, _wasSlide;
        float _wasBehind, _wasAbove, _wasAhead, _wasNearGate;
        int _dashStart;

        public void Reset() { Contacts.Clear(); _primed = false; _wasDash = _wasSlide = false; }

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
            // A lab start is often mid-air: the demo's inputs start on the first tick (no wait for three grounded ticks and no
            // start alignment), as an in-game lab demo's do.
            SetEngineField(sim, "_waitingForStable", false);
            SetEngineField(sim, "_preRollTicksRemaining", 0);
            SetEngineField(sim, "_requireCourseFinish", false);
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
    /// for the contact's opposite side.</summary>
    public sealed class TechPattern
    {
        public string Tech;          // techs.json id
        public string Name;
        public string Category;      // glitchless | nmg | major
        public string Requires;      // techs.json "requires": NONE, WJ, WJ_DS, WJ_DS_DJ, WJ_DS_DJ_BS, OMNI
        public Contact Kind;
        public int First, Last;      // window offsets around the contact tick
        public InputTick[] Ticks;
        public int MinLevel;         // category unlock level the window needs (0 none .. 4 block swap)
        public bool NeedsOmni;       // the omnidirectional dash
        public bool NeedsDash;       // the window presses dash
        public bool Key;             // the demo's key contact: the one its tech happens at
        public bool Mirrored;        // x values negated for the contact's opposite side
        public int DemoContact;      // the contact's tick in the demo
        public string DemoStart;     // where the demo played (its trace)

        public override string ToString() => Tech + " " + Kind + " " + First + ".." + Last;
    }

    /// <summary>What the miner saw in one tech's demo: where it replayed, the contacts along it and the patterns cut out.</summary>
    public sealed class TechDemo
    {
        public string Tech, Name, Category, Requires, Start, Source, Error;
        public int InputTicks, PhysicsTicks;
        public bool Finished, Died;
        public List<(int tick, Contact kind)> Contacts = new List<(int, Contact)>();
        public List<TechPattern> Patterns = new List<TechPattern>();
    }

    /// <summary>The tech demos of the studio's Tech tab (techs.json) mined into solver grafts: each demo replays offline from
    /// its start (its category's start trace, its own savestate, or a captured tech-<id>.json trace), the contact events
    /// along it are detected with the solver's own contact detection (<see cref="ContactLog"/>), and the input window around
    /// each contact becomes a pattern. <c>solve</c> grafts them at the same contact kinds (filter by the run's unlocks),
    /// in addition to the hand-written grafts; <c>--no-tech-grafts</c> turns them off. The mining is cached in the trace
    /// folder, keyed by the catalog content and the engine build.</summary>
    public sealed class TechGrafts
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
        /// <summary>The contact tick's placement jitter at graft time, as the hand-written grafts jitter theirs.</summary>
        public const int Jitter = 3;
        public const int GraftCoast = 40;

        // The category unlocks ("none", "wj", "wjds", "wjdsdj", "wjdsdjbs", "wjdsdjbsom", or the full-tas "omni") as a level
        // and the omnidirectional dash on top, as OfflineSolver's Worker reads them.
        public static (int level, bool omni) Caps(string caps)
        {
            switch (caps)
            {
                case "wj": return (1, false);
                case "wjds": return (2, false);
                case "wjdsdj": return (3, false);
                case "wjdsdjbs": return (4, false);
                case "wjdsdjbsom":
                case "omni": return (5, true);
                default: return (0, false);
            }
        }

        /// <summary>The patterns ready for one solve's category unlocks, by contact kind (read-only once prepared).</summary>
        public void Prepare(string capabilities)
        {
            (int level, bool omni) = Caps(capabilities);
            _ready = new List<TechPattern>[Enum.GetValues(typeof(Contact)).Length];
            for (int i = 0; i < _ready.Length; i++)
                _ready[i] = Patterns.Where(p => p.Kind == (Contact)i && p.MinLevel <= level && (!p.NeedsOmni || omni)
                    && (!p.NeedsDash || level >= 2 || omni)).ToList();
        }
        List<TechPattern>[] _ready;

        /// <summary>A pattern for this contact kind the run's unlocks allow, drawn uniformly; null when there is none.</summary>
        public TechPattern Pick(Contact kind, System.Random rng)
        {
            List<TechPattern> list = _ready[(int)kind];
            return list.Count == 0 ? null : list[rng.Next(list.Count)];
        }

        /// <summary>The pattern's input window on <paramref name="run"/>, its contact at the run's contact tick plus the jitter:
        /// the run is cut at the window's first tick and the window's own inputs follow, then GraftCoast ticks of its stick
        /// and hold (released at the end). A press or release at the cut keeps the jump button's edges real (docs/human-input.md).
        /// False: the window doesn't fit after the run's kept prefix.</summary>
        public static bool Compose(List<InputTick> run, int contactTick, int jitter, int lo, TechPattern p, out List<InputTick> grafted)
        {
            grafted = null;
            int at = Math.Min(contactTick, run.Count) + jitter;
            int start = at + p.First;
            if (start > run.Count) return false;
            // The window may reach before the run's kept prefix: drop the head it can't have (the run starts from a cell's
            // state there), as long as some of the tech's own inputs survive.
            int skip = 0;
            if (start < Math.Max(lo, 1)) { skip = Math.Max(lo, 1) - start; start = Math.Max(lo, 1); }
            if (skip >= p.Ticks.Length || start > run.Count) return false;
            var t = run.GetRange(0, start);
            InputTick first = p.Ticks[skip];
            bool down = start > 0 && (t[start - 1].held || t[start - 1].press) && !t[start - 1].release;
            if (down && !first.held && !first.release) first.release = true;
            else if (!down && first.release && !first.press) first.release = false;
            if (!down && first.held && !first.press) first.press = true;
            for (int i = skip; i < p.Ticks.Length; i++) t.Add(i == skip ? first : p.Ticks[i]);
            // The coast keeps the window's last stick and hold, as the demo's follow-up would; the hold ends at the run's end.
            InputTick last = t[t.Count - 1];
            bool endDown = (last.held || last.press) && !last.release;
            var coast = new InputTick { x = last.x, y = last.y, held = endDown };
            for (int i = 0; i < GraftCoast; i++) t.Add(coast);
            if (endDown)
            {
                InputTick r = t[t.Count - 1];
                r.release = true;
                r.held = false;
                t[t.Count - 1] = r;
            }
            grafted = t;
            return true;
        }

        // ---- mining ----

        sealed class TechDef
        {
            public string id, name, category, requires, start, routeId, labmap;
            public bool omniDash, floatingOrigin;
            public int fastForward;
        }

        sealed class DemoSource
        {
            public TechDef Tech;
            public string Trace, Savestate;   // the trace the demo replays over, and its own savestate (macro demos)
            public MacroDefinition Macro;
            public bool Lab;                  // a lab map's solved demo (labmaps/<id>.demo.json)
            public string LabId, LabMapPath, LabDemoPath;
            public List<string> KeyFiles = new List<string>();   // everything the replay reads (the cache key)
        }

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true,
            Converters = { new JsonStringEnumConverter() } };

        public static TechGrafts Load(string tracesDir, bool refresh = false)
        {
            tracesDir = Path.GetFullPath(tracesDir);
            string config = Directory.Exists(Path.GetDirectoryName(tracesDir)) ? Path.GetDirectoryName(tracesDir) : null;
            List<TechDef> techs = Catalog(config);
            Dictionary<string, MacroDefinition> macros = Macros(config);
            var sources = new List<DemoSource>();
            List<(string id, string tech, string path)> labmaps = LabMaps(config);
            foreach (TechDef tech in techs)
            {
                var forTech = new List<DemoSource> { new DemoSource { Tech = tech } };
                var labs = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(tech.labmap)) labs.Add(tech.labmap);
                foreach ((string id, string techId, _) in labmaps) if (techId == tech.id) labs.Add(id);
                foreach (string id in labs) forTech.Add(new DemoSource { Tech = tech, Lab = true, LabId = id });
                foreach (DemoSource source in forTech)
                {
                    try { Resolve(source, tracesDir, config, macros); }
                    catch (Exception ex) { source.Trace = ex.Message; }
                    sources.Add(source);
                }
            }
            string key = CacheKey(sources, tracesDir);
            string cache = Path.Combine(tracesDir, "tech-grafts.cache.json");
            if (!refresh && File.Exists(cache))
            {
                try
                {
                    TechGrafts cached = JsonSerializer.Deserialize<TechGrafts>(File.ReadAllText(cache), Json);
                    if (cached != null && cached.Key == key)
                    {
                        cached.CacheFile = cache;
                        cached.FromCache = true;
                        return cached;
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
                Directory.CreateDirectory(tracesDir);
                File.WriteAllText(cache, JsonSerializer.Serialize(mined, Json));
            }
            catch (Exception ex) { Console.Error.WriteLine("tech grafts: cache not written (" + ex.Message + ")"); }
            return mined;
        }

        // The config folder's techs.json (the Tech tab's live copy) with the repo's seed filling in whatever it lacks.
        static List<TechDef> Catalog(string config)
        {
            var byId = new Dictionary<string, TechDef>();
            foreach (string file in new[] { RepoTechs(), config == null ? null : Path.Combine(config, "techs.json") })
            {
                if (file == null || !File.Exists(file)) continue;
                try
                {
                    foreach (TechDef tech in JsonSerializer.Deserialize<List<TechDef>>(File.ReadAllText(file), Json) ?? new List<TechDef>())
                        if (!string.IsNullOrEmpty(tech.id)) byId[tech.id] = tech;
                }
                catch (Exception ex) { Console.Error.WriteLine("tech grafts: " + file + ": " + ex.Message); }
            }
            return byId.Values.OrderBy(t => t.id, StringComparer.Ordinal).ToList();
        }

        static string RepoTechs()
        {
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string file = Path.Combine(dir.FullName, "techs.json");
                if (File.Exists(file)) return file;
            }
            return null;
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
                source.LabDemoPath = FindLab(config, source.LabId + ".demo.json");
                if (source.LabMapPath == null) throw new InvalidDataException("lab demo: no labmaps/" + source.LabId + ".json");
                if (source.LabDemoPath == null) throw new InvalidDataException("lab demo: no labmaps/" + source.LabId + ".demo.json solved yet");
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
                throw new InvalidDataException("no demo route");
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

        // The lab maps on disk (the config folder's copy, else the repo's): their ids and the techs.json id each demos.
        static List<(string id, string tech, string path)> LabMaps(string config)
        {
            var found = new List<(string, string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in new[] { config == null ? null : Path.Combine(config, "labmaps"), Path.Combine(AppContext.BaseDirectory, "labmaps") })
            {
                if (dir == null || !Directory.Exists(dir)) continue;
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

        // labmaps/<name>: the config folder's copy (the studio's live one), else the repo's.
        static string FindLab(string config, string name)
        {
            foreach (string dir in new[] { config == null ? null : Path.Combine(config, "labmaps"), Path.Combine(AppContext.BaseDirectory, "labmaps") })
            {
                if (dir == null) continue;
                string file = Path.Combine(dir, name);
                if (File.Exists(file)) return file;
            }
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string file = Path.Combine(dir.FullName, "labmaps", name);
                if (File.Exists(file)) return file;
            }
            return null;
        }

        static string WorldOf(string trace) { return Path.ChangeExtension(trace, null) + ".world.json"; }

        // The demo's category start: its start trace (start-<course>-<caps>.json), else a route captured from that same
        // quick restart (route-<course>-<caps>-*.json); a full-tas demo plays from its run's fresh start.
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
            if (course != null)
            {
                string exact = Path.Combine(tracesDir, "start-c" + course + "-" + caps + ".json");
                if (File.Exists(exact)) return exact;
                string route = Directory.Exists(tracesDir)
                    ? Directory.GetFiles(tracesDir, "route-c" + course + "-" + caps + "-*.json")
                        .Where(f => !f.EndsWith(".world.json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
                    : null;
                if (route != null) return route;
            }
            throw new InvalidDataException("no start trace for " + start);
        }

        // The cache key: the catalog content, the demo macros and every file a demo replays over, plus the engine build.
        static string CacheKey(List<DemoSource> sources, string tracesDir)
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
            Add(RepoTechs());
            string config = Path.GetDirectoryName(tracesDir);
            Add(Path.Combine(config, "techs.json"));
            Add(Path.Combine(config, "routes.json"));
            foreach (string file in sources.SelectMany(s => s.KeyFiles).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal)) Add(file);
            foreach (string file in new[] { typeof(TechGrafts).Assembly.Location, typeof(Movement).Assembly.Location })
                if (!string.IsNullOrEmpty(file)) Add(file);
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(hash.ToString())));
        }

        static TechDemo Mine(DemoSource source, string tracesDir)
        {
            TechDef tech = source.Tech;
            var demo = new TechDemo
            {
                Tech = tech.id, Name = tech.name, Category = tech.category, Requires = tech.requires, Start = tech.start,
                Source = source.Lab ? "lab " + source.LabId + (source.LabDemoPath == null ? "" : " (" + Path.GetFileName(source.LabDemoPath) + ")")
                    : source.Macro != null ? Path.GetFileName(source.Trace) + (source.Savestate != null ? " + savestate" : "")
                    : source.Trace != null && source.Trace.EndsWith(".json") && File.Exists(source.Trace) ? Path.GetFileName(source.Trace) : null,
            };
            if (source.Trace == null || !File.Exists(source.Trace))
            {
                demo.Error = source.Trace ?? "no demo route";
                return demo;
            }
            try
            {
                using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(source.Trace), new JsonDocumentOptions { MaxDepth = 256 });
                JsonElement root = trace.RootElement;
                LabMapDef lab = source.Lab ? LabMapDef.Load(source.LabMapPath) : null;
                World world = World.Load(WorldOf(source.Trace));
                if (lab != null) LabMap.Inject(world, lab);
                var sim = new Simulation(world);
                sim.SetTarget(root);
                sim.LoadStart(root.GetProperty("start"));
                List<InputTick> inputs;
                if (lab != null)
                {
                    // A lab demo's start is the lab's own (its geometry injected above), and its inputs are `lab solve`'s; a lab
                    // run is judged on its own finish box, not the host trace's course (Cli\Lab.cs's lab replay).
                    using JsonDocument solved = JsonDocument.Parse(File.ReadAllText(source.LabDemoPath));
                    inputs = SolveInputs(solved.RootElement);
                    DemoStart.ApplyLabStart(sim, lab);
                    (float fx, float fy, float fw, float fh) = lab.Finish;
                    (float ax, float ay) = lab.Anchor;
                    sim.TargetCourse = -1;
                    CapturePos finishAt = sim.ToCapture(new WorldPos(fx + ax, fy + ay));
                    sim.TargetFinish = new CaptureRect(finishAt, fw, fh);
                }
                else if (source.Macro == null)
                {
                    inputs = SolveInputs(root);
                    demo.Source = Path.GetFileName(source.Trace);
                }
                else
                {
                    inputs = ToInputs(source.Macro);
                    if (source.Savestate != null) DemoStart.ApplySavestate(sim, source.Savestate);
                    // A demo plays with the omnidirectional dash on top of its start's unlocks (TechCatalog.ApplyTechAbilities).
                    if (tech.omniDash || tech.requires == "OMNI") DemoStart.SetField(sim.Player, "omniDashUnlocked", "true");
                    sim.SetStart(sim.Capture(0), 0);
                }
                demo.InputTicks = inputs.Count;
                var log = new ContactLog();
                var probe = new ContactProbe(sim);
                int lastTick = 0;
                RunResult run = sim.Run(inputs, afterTick: tick =>
                {
                    lastTick = tick;
                    log.Tick(tick, sim.Player, probe.NearGate());
                    return true;
                }, extraTicks: 200, onStep: _ => log.Slide((int)sim.Engine.RunTicks, sim.Player));
                demo.PhysicsTicks = run.Stepped;
                demo.Finished = run.Finished;
                demo.Died = run.EndReason != null && run.EndReason.StartsWith("Player death", StringComparison.Ordinal);
                if (demo.Died) log.Contacts.Add((lastTick, Contact.Died));
                demo.Contacts.AddRange(log.Contacts);
                Cut(demo, inputs);
            }
            catch (Exception ex)
            {
                demo.Error = ex.GetBaseException().Message;
            }
            return demo;
        }

        static List<InputTick> SolveInputs(JsonElement trace)
        {
            JsonElement list = trace.TryGetProperty("inputs", out JsonElement inputs) ? inputs : trace.GetProperty("ticks");
            return list.EnumerateArray().Select(InputTick.FromJson).ToList();
        }

        // A demo macro is raw input (TechCatalog refuses anything else): one input tick per duration tick, with the press,
        // release, dash, dash jump, spring dash, lock and dash turn on the step's first tick (Solver.ToTicks's shape).
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
                        turnX = i == 0 && s.rawDashPressed ? s.dashTurnX : null,
                        reset = i == 0 && s.rawQuickRestart,
                    });
            }
            return ticks;
        }

        // The input window around every contact of the demo, tagged by the contact type. The tech's key contact is the one
        // nearest the demo's last press, release or dash (the input that does the tech); its window is flagged "key".
        static void Cut(TechDemo demo, List<InputTick> inputs)
        {
            int action = 0;
            for (int i = 0; i < inputs.Count; i++) if (inputs[i].press || inputs[i].release || inputs[i].dash) action = i;
            var cuts = new List<(int tick, Contact kind, TechPattern pattern)>();
            foreach ((int tick, Contact kind) in demo.Contacts)
            {
                int lo = Math.Max(0, tick - WindowBefore), hi = Math.Min(inputs.Count - 1, tick + (kind == Contact.Died ? -1 : WindowAfter));
                if (hi < lo) continue;
                var window = inputs.GetRange(lo, hi - lo + 1);
                // A graft's run never quick-restarts (a route resets only the game's own way, and never two ticks in a row),
                // and a window with no button in it is travel, not tech.
                if (window.Any(k => k.reset) || !window.Any(k => k.press || k.release || k.dash)) continue;
                var pattern = new TechPattern
                {
                    Tech = demo.Tech, Name = demo.Name, Category = demo.Category, Requires = demo.Requires,
                    Kind = kind, First = lo - tick, Last = hi - tick, Ticks = window.ToArray(), DemoContact = tick, DemoStart = demo.Source,
                };
                Tag(pattern, demo.Requires);
                cuts.Add((tick, kind, pattern));
            }
            if (cuts.Count == 0) return;
            // Only the contacts around the demo's action make patterns: a full-tas demo's travel touches walls and gates
            // the whole way, and its windows are travel, not tech.
            var near = cuts.OrderBy(c => Math.Abs(c.tick - action)).ThenBy(c => c.tick).ToList();
            var keep = new HashSet<TechPattern>(near.Take(MaxContacts).Select(c => c.pattern));
            foreach ((int tick, Contact kind, TechPattern pattern) in cuts)
            {
                if (!keep.Contains(pattern)) continue;
                pattern.Key = ReferenceEquals(pattern, near[0].pattern);
                demo.Patterns.Add(pattern);
                Contact other = Mirror(kind);
                if (other != kind)
                {
                    var twin = new TechPattern
                    {
                        Tech = pattern.Tech, Name = pattern.Name, Category = pattern.Category, Requires = pattern.Requires,
                        Kind = other, First = pattern.First, Last = pattern.Last, DemoContact = pattern.DemoContact,
                        DemoStart = pattern.DemoStart, Key = pattern.Key, Mirrored = true,
                        MinLevel = pattern.MinLevel, NeedsOmni = pattern.NeedsOmni, NeedsDash = pattern.NeedsDash,
                        Ticks = pattern.Ticks.Select(Mirror).ToArray(),
                    };
                    demo.Patterns.Add(twin);
                }
            }
        }

        static void Tag(TechPattern pattern, string requires)
        {
            (int level, bool omni) = requires == "OMNI" ? (0, true) : requires == "WJ" ? (1, false) : requires == "WJ_DS" ? (2, false)
                : requires == "WJ_DS_DJ" ? (3, false) : requires == "WJ_DS_DJ_BS" ? (4, false) : (0, false);
            pattern.MinLevel = level;
            pattern.NeedsOmni = omni;
            pattern.NeedsDash = pattern.Ticks.Any(k => k.dash || k.dashJump > 0 || k.springDash > 0);
            if (pattern.NeedsDash) pattern.MinLevel = Math.Max(pattern.MinLevel, 2);
        }

        static Contact Mirror(Contact kind)
        {
            switch (kind)
            {
                case Contact.WallBehindLeft: return Contact.WallBehindRight;
                case Contact.WallBehindRight: return Contact.WallBehindLeft;
                case Contact.WallAheadLeft: return Contact.WallAheadRight;
                case Contact.WallAheadRight: return Contact.WallAheadLeft;
                case Contact.WallAboveLeft: return Contact.WallAboveRight;
                case Contact.WallAboveRight: return Contact.WallAboveLeft;
                case Contact.NearGateLeft: return Contact.NearGateRight;
                case Contact.NearGateRight: return Contact.NearGateLeft;
                default: return kind;
            }
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
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Patterns.RemoveAll(p => !seen.Add(Signature(p)));
        }

        static string Signature(TechPattern p)
        {
            var text = new StringBuilder().Append(p.Kind).Append('|').Append(p.First).Append('|');
            foreach (InputTick t in p.Ticks)
                text.Append(t.x.ToString("R", CultureInfo.InvariantCulture)).Append(t.y.ToString("R", CultureInfo.InvariantCulture))
                    .Append(t.held ? 'h' : '-').Append(t.press ? 'p' : '-').Append(t.release ? 'r' : '-').Append(t.dash ? 'd' : '-')
                    .Append(t.dashJump).Append(t.springDash).Append(t.lockX).Append(t.lockY).Append(t.turnX).Append(t.reset ? 'q' : '-').Append(';');
            return text.ToString();
        }

        // ---- the CLI listing ----

        /// <summary>techgrafts &lt;traces dir or trace&gt; [--refresh]: every tech's demo replayed and mined, per contact.</summary>
        public static int Command(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: techgrafts <traces dir or trace.json> [--refresh]   mine the tech tab's demos into graft patterns");
                return 2;
            }
            string path = args[0];
            string tracesDir = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
            TechGrafts mined = Load(tracesDir, args.Contains("--refresh"));
            Console.WriteLine($"tech grafts: {mined.Patterns.Count} patterns over {mined.Demos.Count(d => d.Patterns.Count > 0)} of {mined.Demos.Count} demos"
                + $" ({(mined.FromCache ? "cache" : "mined")}, key {mined.Key.Substring(0, 12)}, {mined.CacheFile})");
            foreach (TechDemo demo in mined.Demos)
            {
                string head = $"{demo.Tech} ({demo.Name}, {demo.Category}, needs {demo.Requires})";
                if (demo.Error != null) { Console.WriteLine($"{head,-70} no demo: {demo.Error}"); continue; }
                Console.WriteLine($"{head,-70} {demo.Source}: {demo.InputTicks} input ticks, {demo.PhysicsTicks} physics, "
                    + $"{(demo.Finished ? "finished" : demo.Died ? "died" : "ran out")}, {demo.Contacts.Count} contacts");
                foreach (TechPattern p in demo.Patterns)
                    Console.WriteLine("  " + (p.Key ? "key " : "    ") + (p.Mirrored ? "mirrored " : "") + p.Kind + " @" + p.DemoContact
                        + ": " + p.First + ".." + p.Last + " -> " + p.Ticks.Length + " ticks"
                        + (p.NeedsOmni ? " [omni]" : "") + (p.MinLevel > 0 ? " [level " + p.MinLevel + "]" : ""));
                foreach (TechPattern p in demo.Patterns.Where(p => p.Key))
                    Console.WriteLine("      " + Format(p));
            }
            return 0;
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
                if (t.turnX.HasValue) text.Append(" turn").Append(t.turnX.Value.ToString("0.##", CultureInfo.InvariantCulture));
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
