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
            /// <summary>Other runs whose offline bests seed this run's solve: from each of their goals on, their inputs are tried where this run reaches that goal.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<string> seeds;
            public List<string> goals = new List<string>();
            [JsonIgnore] public bool Vman { get { return start == VmanStart; } }
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
        // which made a 437-goal trace 3.8 GB, past what the offline engine can read).
        const int FullTasTraceFormat = 2;

        public sealed class FullTasSplit
        {
            public string label;
            public long ticks;
        }

        sealed class FullTasVerdict
        {
            public bool ok = true, finished, saved;
            public float time;
            public long runTicks;
            public string reason;
            public List<FullTasSplit> splits;
        }

        sealed class FullTasPlay
        {
            public FullTasRunDef run;
            public MacroDefinition route;
            public FullTasTracker tracker;
            public int expected;
            public bool waitingForNewGame = true, started;
            /// <summary>Input ticks still to run in turbo before 1x (a tech demo's fastForward: it starts where the tech begins).</summary>
            public int fastForward;
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
        DateTime _fullTasWritten;
        float _fullTasCheckAt;
        Dictionary<string, List<FullTasSplit>> _fullTasSplits = new Dictionary<string, List<FullTasSplit>>();

        FullTasTracker _fullTas;
        FullTasUnlocks _fullTasUnlocks;
        bool _fullTasStartWasTracking;
        int _fullTasFinishCourse = -1;
        FullTasRunDef _solverFullTas;
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
        /// <summary>The solve's last "segments" event (--resets): kept apart, as the event list drops its oldest.</summary>
        JObject _fullTasSegments;
        string _fullTasSolveError, _fullTasCommand;
        const int MaxFullTasEvents = 400, MaxFullTasLog = 300;

        static readonly Regex FullTasToken = new Regex(@"^(start|finish|buy|reward|at):(.+)$");
        static readonly Regex FullTasRouteName = new Regex(@"^fulltas/([A-Za-z0-9%-]+)_NONE ", RegexOptions.IgnoreCase);
        static readonly Regex OfflineFullTasBestFile = new Regex(@"^fulltas-([A-Za-z0-9%-]+)-([\d.]+)\.json$");
        static readonly HashSet<string> FullTasBreakerTokens = new HashSet<string> { "buyprestige5" };
        const int MaxBuyGoalBoxes = 8;

        string FullTasPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas.json"); } }
        string FullTasSplitsPath { get { return Path.Combine(Paths.ConfigPath, "IGTAPTasMod", "fulltas-splits.json"); } }
        string FullTasTracePath(string run) { return Path.Combine(EngineSimDir, "fulltas-" + run + ".json"); }
        static string FullTasCategory(string run) { return "fulltas/" + run + "_" + CapabilityLabels["none"]; }

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
            public DateTime written;
            public List<string> goals;
            public List<long> ticks, inputTicks;
            public List<SolverTick> inputs;
        }
        sealed class FullTasOffline
        {
            public List<SolverTick> inputs;
            public int reached;
            public List<FullTasSplit> splits;
            public float time;
            /// <summary>The route cut at its Quick Restarts, as the solver's --resets sees it: [goals before, goals by its Quick Restart, inputs, first input] per segment.</summary>
            public List<int[]> segments;
            /// <summary>Faster ways the solver found for a segment whose Quick Restart matched but whose whole run failed (fulltas/&lt;run&gt;.faster.json), while that file is of this best.</summary>
            public List<JObject> faster;
        }
        readonly Dictionary<string, (DateTime written, JObject file)> _fullTasFaster = new Dictionary<string, (DateTime, JObject)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FullTasOfflineFile> _fullTasOffline = new Dictionary<string, FullTasOfflineFile>(StringComparer.OrdinalIgnoreCase);

        sealed class FullTasClosest
        {
            public DateTime written;
            public List<string> goals;
            public int reached;
            public string next;
            public float? distance;
            public List<SolverTick> inputs;
        }
        readonly Dictionary<string, FullTasClosest> _fullTasClosest = new Dictionary<string, FullTasClosest>(StringComparer.OrdinalIgnoreCase);

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
        static readonly Regex FullTasSeedTransition = new Regex(@"^(bonus\d+|\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);

        readonly Dictionary<string, List<float[]>> _fullTasAvoids = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
        DateTime _fullTasAvoidsRead;
        float _fullTasAvoidsChecked = float.NegativeInfinity;
        static readonly List<float[]> NoAvoids = new List<float[]>();

        static readonly Regex CompactScalarArrays = new Regex(@"\[((?:\s*(?:-?[\d.eE+-]+|""[^""\[\]{},]*"")\s*,)*\s*(?:-?[\d.eE+-]+|""[^""\[\]{},]*"")\s*)\]");
        static readonly Regex CompactBoxLists = new Regex(@"\[((?:\s*\[[^\[\]{}]*\]\s*,)*\s*\[[^\[\]{}]*\]\s*)\]");
        static readonly Regex InnerArrays = new Regex(@"\[[^\[\]]*\]");
    }
}
