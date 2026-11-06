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
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string savestate;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float[] rebaseAt;
            [JsonIgnore] public Vector2? RebaseAt { get { return rebaseAt == null || rebaseAt.Length < 2 ? (Vector2?)null : new Vector2(rebaseAt[0], rebaseAt[1]); } }
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JToken tree;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<string> seeds;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string buys;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public FullTasHumanWeights human;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string section;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<FullTasAvoidBlock> avoid;
            public List<FullTasRunGoal> goals = new List<FullTasRunGoal>();
            [JsonIgnore] public List<string> Labels { get { return goals.Select(g => g.goal).ToList(); } }
            [JsonIgnore] public List<string> Ids { get { return goals.Select(g => g.id).ToList(); } }
            [JsonIgnore] public string Section { get { return section ?? (savestate != null ? "test" : "wip"); } }
            [JsonIgnore] public bool LooseBuys { get { return buys == "loose"; } }
            [JsonIgnore] public bool Vman { get { return start == VmanStart; } }
            [JsonIgnore] public FullTasHumanWeights Human { get { return human ?? new FullTasHumanWeights(); } }
        }

        public sealed class FullTasRunGoal
        {
            public string id;
            public string goal;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<FullTasAvoidBlock> avoid;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<FullTasAvoidBlock> avoidAfter;
        }

        public sealed class FullTasAvoidBlock
        {
            public List<float[]> boxes = new List<float[]>();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string from;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<string> offFor;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string note;
        }

        public sealed class FullTasHumanWeights
        {
            public float grace = 2f, framePerfect = 5f, backToBack = 20f;

            public bool Same(FullTasHumanWeights o) { return grace == o.grace && framePerfect == o.framePerfect && backToBack == o.backToBack; }

            public bool PricesDifficulty() { return framePerfect != 0f || backToBack != 0f; }
        }

        const string VmanStart = "vman";

        const int FullTasSeed = 20260925;

        // Bump when the new-game start state changes: a trace from another setup must be recaptured (FullTasTraceCurrent).
        const int FullTasStartSetup = 4;

        // Bump when the trace export changes: an older trace must be recaptured before a solve (FullTasTraceCurrent).
        const int FullTasTraceFormat = 3;

        public sealed class FullTasSplit
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string id;
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
            public float? score;
        }

        const string MajorClass = "major";
        static readonly string[] FullTasClasses = { MajorClass, "nmg", "human-major", "human-nmg" };
        static bool HumanClass(string cls) { return cls.StartsWith("human-", StringComparison.Ordinal); }
        static bool NoMajorClass(string cls) { return cls == "nmg" || cls == "human-nmg"; }

        static string FullTasClassOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return MajorClass;
            return FullTasClasses.FirstOrDefault(c => c == key)
                ?? throw new InvalidOperationException("class: " + string.Join(", ", FullTasClasses) + ", not \"" + key + "\".");
        }

        public sealed class FullTasSpan
        {
            public string tech, category;
            public int tick;
            public long physics;
        }

        public sealed class FullTasClassRecord
        {
            [JsonProperty("class")] public string cls;
            public float score, live;
            public int major, nmg, graceTicks;
            public List<FullTasSpan> spans;
            public FullTasDifficulty difficulty;
        }

        [JsonConverter(typeof(FullTasDifficultyConverter))]
        public sealed class FullTasDifficulty
        {
            public int inputs, framePerfect, backToBack;
        }

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
            public int fastForward;
            public bool fastForwardFrames;
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
        long _fullTasStat = NoFullTasStat;
        const long NoFullTasStat = long.MinValue;
        long _fullTasStatOf;
        int _fullTasStatBusy;
        Dictionary<string, List<FullTasSplit>> _fullTasSplits = new Dictionary<string, List<FullTasSplit>>();
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
        JObject _fullTasOutlook;
        readonly List<JObject> _fullTasPulls = new List<JObject>();
        string _fullTasSolveError, _fullTasCommand;
        const int MaxFullTasEvents = 400, MaxFullTasLog = 300;
        int _fullTasEventsVersion, _fullTasLogVersion;
        string _fullTasEventsJson, _fullTasLogJson;
        int _fullTasEventsJsonAt = -1, _fullTasLogJsonAt = -1;
        static readonly HashSet<string> FullTasDiskOnlyEvents = new HashSet<string> { "model", "pull", "check", "chaining", "goals", "goalEdit" };
        static readonly HashSet<string> FullTasKeptEvents = new HashSet<string> { "session", "autoStart", "record", "gate" };

        Process _fullTasDifficultyProcess;
        readonly ConcurrentQueue<string> _fullTasDifficultyOutput = new ConcurrentQueue<string>();
        string _fullTasDifficultyRun;
        int _fullTasDifficultyDone;
        int? _fullTasDifficultyOf;

        static readonly Regex FullTasToken = new Regex(@"^(start|record|farm|buy|reward|at):(.+)$");
        static readonly Regex FullTasRouteName = new Regex(@"^fulltas/([A-Za-z0-9%-]+)_NONE ", RegexOptions.IgnoreCase);
        static readonly Regex FullTasClassRoute = new Regex(@"^fulltas/(?<run>[A-Za-z0-9%-]+)_NONE (?:(?<class>nmg|human-major|human-nmg) )?(?<time>[\d.]+)$", RegexOptions.IgnoreCase);
        static readonly Regex OfflineFullTasBestFile = new Regex(@"^fulltas-([A-Za-z0-9%-]+)-([\d.]+)\.json$");
        static readonly HashSet<string> FullTasBreakerTokens = new HashSet<string> { "buyprestige5" };
        const int MaxBuyGoalBoxes = 8;

        static readonly string FullTasRunsDir = typeof(TasPlugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "IgtapRepo")?.Value is string repo
            ? Path.Combine(repo, "data", "runs") : Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "runs");
        static string FullTasRunFile(string run) { return Path.Combine(FullTasRunsDir, run + ".json"); }
        string FullTasSplitsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-splits.json"); } }
        string FullTasClassesPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-classes.json"); } }
        string FullTasCandidatesDir(string run) { return Path.Combine(EngineSimDir, "fulltas", "candidates", run); }
        string FullTasTracePath(string run) { return Path.Combine(EngineSimDir, "fulltas-" + run + ".json"); }
        static string FullTasCategory(string run) { return "fulltas/" + run + "_" + default(Caps).Label; }
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
            public List<FullTasSavedGoal> goals;
            public List<SolverTick> inputs;
        }
        sealed class FullTasSavedGoal
        {
            public string id, label;
            public long input = -1, physics = -1, held = -1;
            public FullTasIdle idle;
        }
        sealed class FullTasIdle
        {
            public string id, label;
            public long[] arrive, afford;
        }
        sealed class FullTasOffline
        {
            public int[] waits;
            public List<SolverTick> inputs;
            public int reached;
            public List<FullTasSplit> splits;
            public float time;
            public List<int[]> segments;
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
        readonly HashSet<string> _bonusRewardMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _bonusRewardMissScenes;

        const int VmanSetupJumpTicks = 45;
        static readonly AccessTools.FieldRef<upgradeBox, double> BoxEffectMult = AccessTools.FieldRefAccess<upgradeBox, double>("effectMult");

        static readonly System.Reflection.MethodInfo[] FloatingOriginMoves = new[] { "MoveRootTransforms", "MoveParticles", "MoveTrailRenderers", "MoveLineRenderers" }
            .Select(n => AccessTools.Method(typeof(FloatingOrigin), n)).ToArray();

        static readonly Regex FullTasSeedRoute = new Regex(@"^([A-Za-z0-9]+)_");
        static readonly Regex FullTasSeedTransition = new Regex(@"^(bonus\d+|\d+|tree\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);

        readonly Dictionary<string, List<float[]>> _fullTasAvoids = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        static readonly List<float[]> NoAvoids = new List<float[]>();

        static readonly Regex CompactScalarArrays = new Regex(@"\[((?:\s*(?:-?[\d.eE+-]+|""[^""\[\]{},]*"")\s*,)*\s*(?:-?[\d.eE+-]+|""[^""\[\]{},]*"")\s*)\]");
        static readonly Regex CompactBoxLists = new Regex(@"\[((?:\s*\[[^\[\]{}]*\]\s*,)*\s*\[[^\[\]{}]*\]\s*)\]");
        static readonly Regex InnerArrays = new Regex(@"\[[^\[\]]*\]");
    }
}
