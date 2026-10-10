using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        public sealed class FullTasRunDef
        {
            public string name;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string start;
            /// <summary>A savestate id: the run starts from it, restored in the new game (or Vman world) with the unlocks it carries.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string savestate;
            /// <summary>
            /// A savestate start's rebase point (world x, y): once the restored player rests, the world is rebased around it as
            /// FloatingOrigin would have done with the player there, so the run's next rebase measures from it. Test runs
            /// only (docs/fulltas.md#savestate-starts).
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float[] rebaseAt;
            [JsonIgnore] public Vector2? RebaseAt { get { return rebaseAt == null || rebaseAt.Length < 2 ? (Vector2?)null : new Vector2(rebaseAt[0], rebaseAt[1]); } }
            /// <summary>
            /// A savestate start's tree (<see cref="TreeLevel"/>): a level (README ### Rules 21), set as a tree transition's start
            /// sets it, or an array of grown segments (a save's <c>_grownSegments</c>): exactly those grown, the boxes that grow
            /// one of them bought. After the restore the zone at the player is loaded. Test runs only
            /// (docs/fulltas.md#savestate-starts).
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JToken tree;
            /// <summary>Other runs whose offline bests seed this run's solve: from each of their goals on, their inputs are tried where this run reaches that goal.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<string> seeds;
            /// <summary>
            /// "loose": a purchase that isn't the next goal's box stands, and a box a later buy goal names counts toward it once
            /// it's current (docs/fulltas.md "Loose buys"). Unset or "strict": only the next goal's box may be bought.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string buys;
            /// <summary>The human classes' score weights (README ### Rules 54): seconds a spike-grace tick, a frame-perfect input
            /// and a forced back-to-back pair add to the live time. Unset: 2, 5 and 20.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public FullTasHumanWeights human;
            /// <summary>The studio's run list section: "complete", "wip" or "test". Unset: "test" with a savestate, else "wip".</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string section;
            public List<string> goals = new List<string>();
            [JsonIgnore] public string Section { get { return section ?? (savestate != null ? "test" : "wip"); } }
            [JsonIgnore] public bool LooseBuys { get { return buys == "loose"; } }
            [JsonIgnore] public bool Vman { get { return start == VmanStart; } }
            [JsonIgnore] public FullTasHumanWeights Human { get { return human ?? new FullTasHumanWeights(); } }
        }

        /// <summary>
        /// The human classes' score weights: score = live seconds + <see cref="grace"/> per spike-grace tick + <see cref="framePerfect"/>
        /// per frame-perfect input + <see cref="backToBack"/> per forced back-to-back pair of the route's humanized version
        /// (<see cref="FullTasDifficulty"/>). 0 for both of the last two: difficulty isn't scored (nor measured).
        /// </summary>
        public sealed class FullTasHumanWeights
        {
            public float grace = 2f, framePerfect = 5f, backToBack = 20f;

            public bool Same(FullTasHumanWeights o) { return grace == o.grace && framePerfect == o.framePerfect && backToBack == o.backToBack; }

            /// <summary>The weights price difficulty: a human class's route is measured and its score adds the counts' seconds.</summary>
            public bool PricesDifficulty() { return framePerfect != 0f || backToBack != 0f; }
        }

        const string VmanStart = "vman";

        const int FullTasSeed = 20260925;

        // What a new game's start state holds (the settle, a savestate start's respawnPoint, ...). Bump it whenever that changes:
        // a trace from another setup is recaptured before a solve (FullTasTraceCurrent), so the search never runs from a start
        // the game no longer makes (2: a savestate start keeps the new game's respawnPoint instead of one written at the savestate;
        // 3: the start restores every ObjectBobber's on/off state, and the world is exported from the start, not the last run;
        // 4: every scene object's on/off state, the clone path arrays and visitedTeleporters, and no start while a zip mover resets).
        const int FullTasStartSetup = 4;

        // What a Full-TAS trace file holds per tick. Bump it whenever the export changes: an older trace is recaptured before a
        // solve (FullTasTraceCurrent) even when its goals and start still match (2: MacroEngine._steps left out of every snapshot,
        // which made a 437-goal trace 3.8 GB, past what the offline engine can read; 3: a Full-TAS time is live time, README
        // ### Rules 31, so the trace's result and the record it re-times on capture (RememberBestSplits) count held frames).
        const int FullTasTraceFormat = 3;

        /// <summary>A goal's arrival: its physics tick (game frames) and the frames the run had stood still in by then (dash
        /// freezes, pauses); together its live time (MacroEngine.LiveSeconds).</summary>
        public sealed class FullTasSplit
        {
            public string label;
            public long ticks;
            public long held;
            [JsonIgnore] public float Live { get { return MacroEngine.LiveSeconds(ticks, held); } }
        }

        sealed class FullTasVerdict
        {
            public bool ok = true, finished, saved;
            public float time;
            public long runTicks;
            public string reason;
            public List<FullTasSplit> splits;
            [JsonProperty("class")] public string cls;
            /// <summary>The route's score (FullTasClassRecord.score) when the engine's tech checks classed it.</summary>
            public float? score;
        }

        /// <summary>
        /// The Full-TAS classes (README ### Rules 54): `major` today's runs, by live time; `nmg` without any major tech span, by
        /// live time; `human-major` and `human-nmg` the same two by score. Each keeps its own best.
        /// </summary>
        const string MajorClass = "major";
        static readonly string[] FullTasClasses = { MajorClass, "nmg", "human-major", "human-nmg" };
        static bool HumanClass(string cls) { return cls.StartsWith("human-", StringComparison.Ordinal); }
        static bool NoMajorClass(string cls) { return cls == "nmg" || cls == "human-nmg"; }

        /// <summary>A class key from a request (null or empty: major).</summary>
        static string FullTasClassOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return MajorClass;
            return FullTasClasses.FirstOrDefault(c => c == key)
                ?? throw new InvalidOperationException("class: " + string.Join(", ", FullTasClasses) + ", not \"" + key + "\".");
        }

        /// <summary>A tech span the engine's tech checks found on a route (`techs-used --json`): <see cref="tick"/> its input tick.</summary>
        public sealed class FullTasSpan
        {
            public string tech, category;
            public int tick;
            public long physics;
        }

        /// <summary>A saved route's class result (fulltas-classes.json, by route id): its counts, its difficulty (null when not
        /// measured: a major or nmg class, a run whose weights don't price it, or a record from before the counts) and its score,
        /// live time plus the weights' seconds for each spike-grace tick, frame-perfect input and forced back-to-back pair.</summary>
        public sealed class FullTasClassRecord
        {
            [JsonProperty("class")] public string cls;
            public float score, live;
            public int major, nmg, graceTicks;
            public List<FullTasSpan> spans;
            public FullTasDifficulty difficulty;
        }

        /// <summary>
        /// A route's difficulty as the engine counts it on humanize's version of the route (techs-used's, a run file's and a
        /// candidate's `difficulty`): its inputs, the frame-perfect ones (they work on one tick only) and the forced back-to-back
        /// pairs (two actions fewer than 3 ticks apart that no timing of the inputs inside their windows avoids). Null where it isn't measured;
        /// a route from before the counts has an old-shape object, which reads as null (<see cref="FullTasDifficultyConverter"/>).
        /// </summary>
        [JsonConverter(typeof(FullTasDifficultyConverter))]
        public sealed class FullTasDifficulty
        {
            public int inputs, framePerfect, backToBack;
        }

        /// <summary>Reads a difficulty object that has the three counts; any other object (the old shape) or null reads as null.</summary>
        public sealed class FullTasDifficultyConverter : JsonConverter
        {
            public override bool CanWrite { get { return false; } }
            public override bool CanConvert(Type type) { return type == typeof(FullTasDifficulty); }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) { throw new NotSupportedException(); }
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
            {
                JToken token = JToken.Load(reader);
                if (!(token is JObject o)) return null;
                JToken inputs = o["inputs"], framePerfect = o["framePerfect"], backToBack = o["backToBack"];
                if (inputs?.Type != JTokenType.Integer || framePerfect?.Type != JTokenType.Integer || backToBack?.Type != JTokenType.Integer) return null;
                return new FullTasDifficulty { inputs = (int)inputs, framePerfect = (int)framePerfect, backToBack = (int)backToBack };
            }
        }

        /// <summary>What the gate judges a Full-TAS route's class by (FullTasViolation): the run whose trace the engine replays it
        /// from, the class and its saved best (live time, or score for a human class), and the class result once it holds up.</summary>
        sealed class FullTasClassJudge
        {
            public FullTasRunDef run;
            public string cls;
            public float best;
            public FullTasClassRecord record;
        }

        sealed class FullTasPlay
        {
            public FullTasRunDef run;
            public MacroDefinition route;
            public FullTasTracker tracker;
            public int expected;
            public bool waitingForNewGame = true, started;
            /// <summary>Ticks still to run in turbo before 1x: a tech demo's fastForward in input ticks (it starts where the tech
            /// begins), a play from a frame in game frames (physics ticks, as the splits count them: <see cref="fastForwardFrames"/>).</summary>
            public int fastForward;
            public bool fastForwardFrames;
            /// <summary>The offline best's idles when this is its route (the overlay's IDLE banner); null otherwise.</summary>
            public List<FullTasIdle> idles;
        }

        sealed class FullTasPending
        {
            public WebStudioRequest request;
            public FullTasRunDef run;
            public string what;
            public Func<object> action;
            public int since;
            public bool loaded;
        }

        List<FullTasRunDef> _fullTasRuns = new List<FullTasRunDef>();
        long _fullTasWritten;
        float _fullTasCheckAt;
        // The run files' stamp (FullTasRunsStamp; 0: none), read on a pool thread for PumpFullTas's once-a-second check (a hand
        // edit or an engine's --install reloads them): NoFullTasStat until a read lands. _fullTasStatOf is _fullTasWritten when
        // that read started; a plugin write since then makes the read stale. _fullTasStatBusy is 1 while a read runs.
        long _fullTasStat = NoFullTasStat;
        const long NoFullTasStat = long.MinValue;
        long _fullTasStatOf;
        int _fullTasStatBusy;
        Dictionary<string, List<FullTasSplit>> _fullTasSplits = new Dictionary<string, List<FullTasSplit>>();
        /// <summary>fulltas-classes.json: each saved Full-TAS route's class result, by route id.</summary>
        Dictionary<string, FullTasClassRecord> _fullTasClasses = new Dictionary<string, FullTasClassRecord>();

        FullTasTracker _fullTas;
        FullTasUnlocks _fullTasUnlocks;
        bool _fullTasStartWasTracking;
        int _fullTasFinishCourse = -1;
        FullTasRunDef _solverFullTas;
        FullTasClassJudge _solverFullTasClass;
        FullTasPlay _fullTasPlay;
        FullTasPending _fullTasPending;
        int _fullTasParkedFrame = -1000;
        object _fullTasLastResult;
        readonly Dictionary<string, string> _fullTasRewardBoxes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Process _fullTasProcess;
        DateTime? _fullTasKillAt;
        readonly ConcurrentQueue<string> _fullTasOutput = new ConcurrentQueue<string>();
        string _fullTasSolveRun;
        string _fullTasSolveStatus = "Idle";
        readonly List<string> _fullTasLog = new List<string>();
        readonly List<JObject> _fullTasEvents = new List<JObject>();
        JObject _fullTasProgress;
        /// <summary>Improve's last `outlook` (the "Should it keep going?" card) and the pulls running now (`pullStart` until its `pull`).</summary>
        JObject _fullTasOutlook;
        readonly List<JObject> _fullTasPulls = new List<JObject>();
        string _fullTasSolveError, _fullTasCommand;
        const int MaxFullTasEvents = 400, MaxFullTasLog = 300;
        // Bumped on every change of _fullTasEvents and _fullTasLog: the studio's live state serialises each list again only then
        // (FullTasListJson), not on every check (400 events and 300 lines stay there after a solve).
        int _fullTasEventsVersion, _fullTasLogVersion;
        string _fullTasEventsJson, _fullTasLogJson;
        int _fullTasEventsJsonAt = -1, _fullTasLogJsonAt = -1;
        /// <summary>--auto's bookkeeping events: on disk in fulltas/&lt;run&gt;.auto.jsonl, not sent to the studio (a model is 300 rows;
        /// a pull comes every few seconds and would push the records out of the capped list). Its own "record" (with a lever)
        /// duplicates the solver's record event, which carries both routes; its "goals" (an adopted goal list) and "goalEdit" (each
        /// edit a goals pull tried) too: the record event of an adopted edit carries its words (goalsEdit).</summary>
        static readonly HashSet<string> FullTasDiskOnlyEvents = new HashSet<string> { "model", "pull", "check", "chaining", "goals", "goalEdit" };
        /// <summary>Events the capped list keeps when it trims: the session, what a solve found, and the game's checks of it.</summary>
        static readonly HashSet<string> FullTasKeptEvents = new HashSet<string> { "session", "autoStart", "record", "gate" };

        /// <summary>The difficulty batch (`/api/fulltas/difficulty`): the engine's `difficulty --candidates` over a run's candidate
        /// folder, a process of its own beside any solve; <c>done</c> / <c>of</c> from its `@difficulty` progress lines (<c>of</c>
        /// null until the first). The run stays named after the batch ends, for the studio's last count.</summary>
        Process _fullTasDifficultyProcess;
        readonly ConcurrentQueue<string> _fullTasDifficultyOutput = new ConcurrentQueue<string>();
        string _fullTasDifficultyRun;
        int _fullTasDifficultyDone;
        int? _fullTasDifficultyOf;

        static readonly Regex FullTasToken = new Regex(@"^(start|finish|buy|reward|at):(.+)$");
        static readonly Regex FullTasRouteName = new Regex(@"^fulltas/([A-Za-z0-9%-]+)_NONE ", RegexOptions.IgnoreCase);
        /// <summary>A saved Full-TAS route of any class (README "Route names"): `fulltas/&lt;run&gt;_NONE [&lt;class&gt; ]&lt;time&gt;`, no class for major.</summary>
        static readonly Regex FullTasClassRoute = new Regex(@"^fulltas/(?<run>[A-Za-z0-9%-]+)_NONE (?:(?<class>nmg|human-major|human-nmg) )?(?<time>[\d.]+)$", RegexOptions.IgnoreCase);
        static readonly Regex OfflineFullTasBestFile = new Regex(@"^fulltas-([A-Za-z0-9%-]+)-([\d.]+)\.json$");
        static readonly HashSet<string> FullTasBreakerTokens = new HashSet<string> { "buyprestige5" };
        const int MaxBuyGoalBoxes = 8;

        /// <summary>The Full-TAS run definitions, one file per run (runs\&lt;run&gt;.json, the run's name its file's): the source
        /// tree's the plugin was built from (IgtapRepo, IGTAPTasMod.csproj), kept with the code.</summary>
        static readonly string FullTasRunsDir = Path.Combine(typeof(TasPlugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IgtapRepo")?.Value ?? Path.Combine(Paths.ConfigPath, "IGTAPTasMod"), "runs");
        static string FullTasRunFile(string run) { return Path.Combine(FullTasRunsDir, run + ".json"); }
        string FullTasSplitsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-splits.json"); } }
        string FullTasClassesPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-classes.json"); } }
        string FullTasCandidatesDir(string run) { return Path.Combine(EngineSimDir, "fulltas", "candidates", run); }
        string FullTasTracePath(string run) { return Path.Combine(EngineSimDir, "fulltas-" + run + ".json"); }
        /// <summary>A Full-TAS run's route category (README "Route names"): its own unlocks, so the fixed NONE.</summary>
        static string FullTasCategory(string run) { return "fulltas/" + run + "_" + default(Caps).Label; }
        /// <summary>A Full-TAS run's route name for a class: the category, then the class unless it's major.</summary>
        static string FullTasClassCategory(string run, string cls) { return FullTasCategory(run) + (cls == MajorClass ? "" : " " + cls); }

        struct FullTasUnlocks
        {
            public bool wallJump, dash, doubleJump, blockSwap, omniDash;
            public int airDashes, airJumps;

            public static FullTasUnlocks Of(Movement player)
            {
                return new FullTasUnlocks {
                    wallJump = GetMovementBool(player, "wallJumpUnlocked", false), dash = GetMovementBool(player, "dashUnlocked", false),
                    doubleJump = GetMovementBool(player, "doubleJumpUnlocked", false), blockSwap = GetMovementBool(player, "blockSwapUnlocked", false),
                    omniDash = GetMovementBool(player, "omniDashUnlocked", false),
                    airDashes = GetMovementField(player, "maxAirDashes") is int d ? d : 0, airJumps = GetMovementField(player, "maxAirJumps") is int j ? j : 0 };
            }

            public void Bought(string token)
            {
                switch (token)
                {
                    case "buywj": wallJump = true; break;
                    case "buyds": dash = true; airDashes++; break;
                    case "buydj": doubleJump = true; airJumps++; break;
                    case "buyblockswap5": blockSwap = true; break;
                    case "buyomni": dash = true; omniDash = true; airDashes++; break;
                }
            }

            public override string ToString()
            {
                var parts = new List<string>();
                if (wallJump) parts.Add("wall jump");
                if (dash) parts.Add(airDashes + " dash" + (airDashes == 1 ? "" : "es") + (omniDash ? " (omnidirectional)" : ""));
                if (doubleJump) parts.Add(airJumps + " double jump" + (airJumps == 1 ? "" : "s"));
                if (blockSwap) parts.Add("block swap");
                return parts.Count == 0 ? "none" : string.Join(", ", parts);
            }
        }

        sealed class FullTasOfflineFile
        {
            public List<string> goals;
            public List<long> ticks, held, inputTicks;
            public List<SolverTick> inputs;
            public List<FullTasIdle> idles;
        }
        /// <summary>A buy goal's wait for its Cash, standing at its box (enginesim FullTasIdles.cs): [physics tick, held frames] when
        /// the player was first at the box and when the Cash was there. Not a goal: goal is the buy's number (1-based).</summary>
        sealed class FullTasIdle
        {
            public int goal;
            public long[] arrive, afford;
        }
        sealed class FullTasOffline
        {
            /// <summary>Per goal reached, the ticks its stretch stands still (idle-input runs of 25 or more): a wait, not the stretch's.</summary>
            public int[] waits;
            public List<SolverTick> inputs;
            public int reached;
            public List<FullTasSplit> splits;
            public float time;
            /// <summary>The route cut at its Quick Restarts, [goals before, goals by its Quick Restart, inputs, first input] per segment.</summary>
            public List<int[]> segments;
            /// <summary>Its idles at the goals it reaches.</summary>
            public List<FullTasIdle> idles;
        }
        readonly CachedFiles<FullTasOfflineFile> _fullTasOffline = new CachedFiles<FullTasOfflineFile>(StringComparer.OrdinalIgnoreCase);

        sealed class FullTasClosest
        {
            public List<string> goals;
            public int reached;
            public string next;
            public float? distance;
            public List<SolverTick> inputs;
        }
        readonly CachedFiles<FullTasClosest> _fullTasClosest = new CachedFiles<FullTasClosest>(StringComparer.OrdinalIgnoreCase);

        Rect _fullTasPlayerBounds;

        readonly Dictionary<string, upgradeBox> _bonusRewardBoxes = new Dictionary<string, upgradeBox>(StringComparer.OrdinalIgnoreCase);
        // Bonuses with no reward box near their end, for the scenes loaded then (BonusRewardBox).
        readonly HashSet<string> _bonusRewardMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _bonusRewardMissScenes;

        const int VmanSetupJumpTicks = 45;
        static readonly AccessTools.FieldRef<upgradeBox, double> BoxEffectMult = AccessTools.FieldRefAccess<upgradeBox, double>("effectMult");

        static readonly System.Reflection.MethodInfo[] FloatingOriginMoves = new[] { "MoveRootTransforms", "MoveParticles", "MoveTrailRenderers", "MoveLineRenderers" }
            .Select(n => AccessTools.Method(typeof(FloatingOrigin), n)).ToArray();

        static readonly Regex FullTasSeedRoute = new Regex(@"^([A-Za-z0-9]+)_");
        static readonly Regex FullTasSeedTransition = new Regex(@"^(bonus\d+|\d+|tree\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);

        readonly Dictionary<string, List<float[]>> _fullTasAvoids = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        JObject _fullTasAvoidsOf;
        static readonly List<float[]> NoAvoids = new List<float[]>();

        static readonly Regex CompactScalarArrays = new Regex(@"\[((?:\s*(?:-?[\d.eE+-]+|""[^""\[\]{},]*"")\s*,)*\s*(?:-?[\d.eE+-]+|""[^""\[\]{},]*"")\s*)\]");
        static readonly Regex CompactBoxLists = new Regex(@"\[((?:\s*\[[^\[\]{}]*\]\s*,)*\s*\[[^\[\]{}]*\]\s*)\]");
        static readonly Regex InnerArrays = new Regex(@"\[[^\[\]]*\]");
    }
}
