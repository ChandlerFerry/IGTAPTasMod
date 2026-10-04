using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    internal sealed class FullTasGoal
    {
        public const string Start = "start", Finish = "finish", Buy = "buy", Reward = "reward", At = "at";
        public string label;
        public string kind;
        public int course;
        public string[] boxes;
        public float x, y, w, h;
        public int from;
        public const int FromLeft = 1, FromRight = 2, FromBottom = 4, FromTop = 8;
        public static readonly string[] SideNames = { "left", "right", "bottom", "top" };
        public const float RewardBoxRange = 400f;

        // Every box a Full-TAS goal can buy (buy:<token>), by hierarchy path and sibling index (BoxId). Shared with enginesim, whose
        // economy planner names its purchases with these tokens.
        public static readonly (string token, string path, int sibling)[] Boxes = {
            ("buyreward", "Courses/course 1/localUpgrades/CashPerLoop", 0),
            ("buyclone", "Courses/course 1/localUpgrades/Clone", 1),
            ("buywj", "Courses/course 1/localUpgrades/Wall jump", 2),
            ("buyomni", "Courses/course 1/localUpgrades/FreeOmnidash", 5),
            ("buyreward2", "Courses/course 2/localUpgrades/CashPerLoop", 0),
            ("buyclone2", "Courses/course 2/localUpgrades/Clone", 1),
            ("buyclonemult2", "Courses/course 2/localUpgrades/CloneMult", 2),
            ("buyglobalreward2", "Courses/course 2/localUpgrades/GLOBALcashPerLoop", 3),
            ("buyds", "Courses/course 2/localUpgrades/DashUnlock", 4),
            ("buyreward3", "Courses/course 3/localUpgrades/CashPerLoop", 0),
            ("buyclone3", "Courses/course 3/localUpgrades/CloneCount", 1),
            ("buyclonemult3", "Courses/course 3/localUpgrades/CloneMult", 2),
            ("buyfast3", "Courses/course 3/localUpgrades/FastCloneChance", 3),
            ("buyglobalreward3", "Courses/course 3/localUpgrades/GLOBALcash", 4),
            ("buydj", "Courses/course 3/localUpgrades/Double jump", 5),
            ("buyfree3", "Courses/course 3/localUpgrades/GLOBALWatt", 6),
            ("buyclonespeed", "Courses/course 3/localUpgrades/GLOBALclonespeedmax", 9),
            ("buystatue", "Courses/course 3/localUpgrades/enablePrestige", 10),
            ("buyreward4", "Courses/course 4/localUpgrades/CashPerloop", 0),
            ("buyclone4", "Courses/course 4/localUpgrades/Clone", 1),
            ("buyclonemult4", "Courses/course 4/localUpgrades/CloneMult", 2),
            ("buybig4", "Courses/course 4/localUpgrades/BigCloneChance", 3),
            ("buyfast4", "Courses/course 4/localUpgrades/FastCloneChance", 4),
            ("buymaxsize4", "Courses/course 4/localUpgrades/MaxCloneSize", 5),
            ("buyglobalreward4", "Courses/course 4/localUpgrades/MaxCloneSize", 6),
            ("buyswap", "Courses/course 4/localUpgrades/SwapBlocksOnce/endDemo", 7),
            ("buyfree4a", "Courses/course 4/localUpgrades/GLOBALfastCloneChance", 8),
            ("buyfree4b", "Courses/course 4/localUpgrades/GLOBALcashperloop", 9),
            ("buyfree4c", "Courses/course 4/localUpgrades/GLOBALcash", 10),
            ("buyprestige4", "Courses/course 4/localUpgrades/PrestigeUpgradeBox4", 11),
            ("buyreward5", "Courses/course 5/localUpgrades/cashPerLoop", 0),
            ("buyclone5", "Courses/course 5/localUpgrades/Clones", 1),
            ("buyclonemult5", "Courses/course 5/localUpgrades/Clone mult", 2),
            ("buyglobalreward5", "Courses/course 5/localUpgrades/global cash ", 3),
            ("buybig5", "Courses/course 5/localUpgrades/Big clone chance", 4),
            ("buyfast5", "Courses/course 5/localUpgrades/Fast clone chance", 5),
            ("buyprestige5", "Courses/course 5/localUpgrades/prestige upgrade box", 6),
            ("buyblockswap5", "Courses/course 5/localUpgrades/SwapBlocksOnDash", 7),
            ("buyfree5", "Courses/course 5/localUpgrades/GLOBALCash", 8),
            // Area 2: Courses 6-10, the atom collider's box and the upgrade tree (zone 2/TreeArea/The Tree, buytree<N> is TreePart<N>'s
            // box: buytree16 activates the zip movers).
            ("buyreward6", "Courses/Course 6/localUpgrades/Base reward", 0),
            ("buygreen6", "Courses/Course 6/localUpgrades/Green reward (1)", 1),
            ("buyclone6", "Courses/Course 6/localUpgrades/Clones", 2),
            ("buyfree6a", "Courses/Course 6/localUpgrades/GlobalCash", 3),
            ("buyfree6b", "Courses/Course 6/localUpgrades/GlobalCash (1)", 4),
            ("buyfree6c", "Courses/Course 6/localUpgrades/upgradeBox", 5),
            ("buyfree6d", "Courses/Course 6/localUpgrades/upgradeBox (1)", 6),
            ("buyfree6e", "Courses/Course 6/localUpgrades/GlobalCash (2)", 9),
            ("buyreward7", "Courses/course 7/localUpgrades/Base reward", 0),
            ("buyclone7", "Courses/course 7/localUpgrades/Clones (1)", 1),
            ("buyclonemult7", "Courses/course 7/localUpgrades/CloneMult", 2),
            ("buygreen7", "Courses/course 7/localUpgrades/GPreward", 4),
            ("buyclonecd8", "Courses/course 8/localUpgrades/Clones", 0),
            ("buyclonegp8", "Courses/course 8/localUpgrades/Clones (1)", 1),
            ("buycd8", "Courses/course 8/localUpgrades/UnlockRubble", 2),
            ("buycddigits8", "Courses/course 8/localUpgrades/CloneDustPower", 3),
            ("buyreward9", "Courses/course 9/localUpgrades/Base reward", 0),
            ("buyclone9", "Courses/course 9/localUpgrades/Clones (1)", 1),
            ("buygreen9", "Courses/course 9/localUpgrades/GP", 2),
            ("buycd9", "Courses/course 9/localUpgrades/Clone Dust", 3),
            ("buyatomlevel9", "Courses/course 9/localUpgrades/EnableTeleporters (1)", 5),
            ("buyclonemult9", "Courses/course 9/localUpgrades/CloneMult", 6),
            ("buyreward10", "Courses/course 10/localUpgrades/Cash", 0),
            ("buyfree10a", "Courses/course 10/localUpgrades/Cash (1)", 1),
            ("buyfree10b", "Courses/course 10/localUpgrades/Cash (2)", 2),
            ("buyclone10", "Courses/course 10/localUpgrades/Clones", 3),
            ("buyclonemult10", "Courses/course 10/localUpgrades/CloneMult", 4),
            ("buywatts10", "Courses/course 10/localUpgrades/CashPerLoop", 5),
            ("buynp10", "Courses/course 10/localUpgrades/IncreasedNP", 6),
            ("buyatomlevel10", "Courses/course 10/localUpgrades/Atoms", 7),
            ("buygreen10", "Courses/course 10/localUpgrades/GPreward", 8),
            ("buyatom", "AtomUpgrades/AtomUpgrade", 0),
            ("buyplant", "zone 2/TreeArea/The Tree/PlantTreeBox", 0),
            ("buytree0", "zone 2/TreeArea/The Tree/TreePart0/GreenCloneChanceBox", 0),
            ("buytree1", "zone 2/TreeArea/The Tree/TreePart1/IncreasedWattsBox", 0),
            ("buytree2", "zone 2/TreeArea/The Tree/TreePart2/IncreasedGPbox", 0),
            ("buytree3", "zone 2/TreeArea/The Tree/TreePart3/GlobalFastCloneBox", 0),
            ("buytree4", "zone 2/TreeArea/The Tree/TreePart4/GlobalBigCloneBox", 0),
            ("buytree5", "zone 2/TreeArea/The Tree/TreePart5/GlobalNPincreaseBox", 0),
            ("buytree6", "zone 2/TreeArea/The Tree/TreePart6/TripleThreatBox", 0),
            ("buytree7", "zone 2/TreeArea/The Tree/TreePart7/Area 2 door open", 0),
            ("buytree8", "zone 2/TreeArea/The Tree/TreePart8/Max Clone Size", 0),
            ("buytree9", "zone 2/TreeArea/The Tree/TreePart9/Max Clone Speed", 0),
            ("buytree10", "zone 2/TreeArea/The Tree/TreePart10/ExemptC6FromPrestige", 0),
            ("buytree11", "zone 2/TreeArea/The Tree/TreePart11/exempt c7 from prestige", 0),
            ("buytree12", "zone 2/TreeArea/The Tree/TreePart12/UnlockCompBoost", 0),
            ("buytree13", "zone 2/TreeArea/The Tree/TreePart13/CompBoostStrength", 0),
            ("buytree14", "zone 2/TreeArea/The Tree/TreePart14/CompBoostDuration", 0),
            ("buytree16", "zone 2/TreeArea/The Tree/TreePart16/ZipMoversUnlock", 2),
            ("buytree17", "zone 2/TreeArea/The Tree/TreePart17/Cdmult", 1),
            ("buytree18", "zone 2/TreeArea/The Tree/TreePart18/Cdmult", 0),
            ("buytree19", "zone 2/TreeArea/The Tree/TreePart19/CD,W,GP", 0),
            ("buytree20", "zone 2/TreeArea/The Tree/TreePart20/MoreOrbs", 3),
            ("buytree21", "zone 2/TreeArea/The Tree/TreePart21/5xIncWatts", 2),
            ("buytree22", "zone 2/TreeArea/The Tree/TreePart22/exempt c8 from prestige", 0),
            ("buytree24", "zone 2/TreeArea/The Tree/TreePart24/5xGP", 2),
            ("buytree25", "zone 2/TreeArea/The Tree/TreePart25/BuyMaxC6", 0),
            ("buytree26", "zone 2/TreeArea/The Tree/TreePart26/BuyMaxBox", 0),
            ("buytree27", "zone 2/TreeArea/The Tree/TreePart27/MoreOrbs", 2),
            ("buyplant2", "Zone 1/The Tree 2/PlantTreeBox", 0) };

        public static string BoxId(string path, int sibling)
        {
            return path + "#" + sibling.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The economy boxes: a course's repeatable income boxes (reward, clones, clone multiplier, global reward, fast, big and
        /// max-size clone chances, green reward). `fulltas --auto`'s goals part may drop, add, regroup or reorder these buy goals and
        /// the laps (`start:N`, `finish:N`) around them; every other goal is the run's skeleton.
        /// </summary>
        public static readonly HashSet<string> EconomyBoxes = new HashSet<string> {
            "buyreward", "buyclone", "buyreward2", "buyclone2", "buyclonemult2", "buyglobalreward2",
            "buyreward3", "buyclone3", "buyclonemult3", "buyfast3", "buyglobalreward3",
            "buyreward4", "buyclone4", "buyclonemult4", "buybig4", "buyfast4", "buymaxsize4", "buyglobalreward4",
            "buyreward5", "buyclone5", "buyclonemult5", "buyglobalreward5", "buybig5", "buyfast5",
            "buyreward6", "buygreen6", "buyclone6", "buyreward7", "buyclone7", "buyclonemult7", "buygreen7",
            "buyreward9", "buyclone9", "buygreen9", "buyclonemult9", "buyreward10", "buyclone10", "buyclonemult10", "buygreen10" };

        /// <summary>A goal label the goals part may edit: a lap's gate or finish, or a buy of economy boxes only.</summary>
        public static bool IsEconomy(string label)
        {
            if (label.StartsWith("start:", StringComparison.Ordinal) || label.StartsWith("finish:", StringComparison.Ordinal)) return true;
            if (!label.StartsWith("buy:", StringComparison.Ordinal)) return false;
            foreach (string token in label.Substring(4).Split('+'))
                if (!EconomyBoxes.Contains(token)) return false;
            return true;
        }

        /// <summary>
        /// Why a goal list may not replace another (null: it may): the run's end goal, its area and reward goals and every buy of
        /// an unlock (anything but economy boxes) stay, in their order; only the economy goals between them change (docs/fulltas.md,
        /// "Parts": goals). The engine's goals part proposes only such edits, and the game adopts a judged route's goals only then.
        /// </summary>
        public static string EditRefused(IList<string> from, IList<string> to)
        {
            if (to == null || to.Count == 0) return "the new goal list is empty";
            if (from.Count == 0) return "the run has no goals";
            if (from[from.Count - 1] != to[to.Count - 1]) return "the end goal changes (" + from[from.Count - 1] + " -> " + to[to.Count - 1] + ")";
            var a = new List<string>();
            var b = new List<string>();
            foreach (string l in from) if (!IsEconomy(l)) a.Add(l);
            foreach (string l in to) if (!IsEconomy(l)) b.Add(l);
            for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
                if (i >= a.Count || i >= b.Count || a[i] != b[i])
                    return "the skeleton (area, reward and unlock goals) changes at its goal " + (i + 1) + ": "
                        + (i < a.Count ? a[i] : "none") + " -> " + (i < b.Count ? b[i] : "none");
            return null;
        }

        public bool EnteredFrom(float rx0, float rx1, float ry0, float ry1, float px0, float px1, float py0, float py1)
        {
            return from == 0
                || (from & FromLeft) != 0 && px1 <= rx0 || (from & FromRight) != 0 && px0 >= rx1
                || (from & FromBottom) != 0 && py1 <= ry0 || (from & FromTop) != 0 && py0 >= ry1;
        }

        /// <summary>A move longer than this in one physics tick is a respawn, not a move (<see cref="CrossesInto"/>).</summary>
        public const float TeleportStep = 400f;

        /// <summary>
        /// A one-way avoid box's cut (forced-paths.json's block "from"; the engine's OfflineSolver.Box, the gate's direction records):
        /// the move from (ax, ay) to (px, py) crosses one of the box's <paramref name="sides"/> edges inwards, a fast move through a
        /// thin box included. A respawn's jump crosses nothing.
        /// </summary>
        public static bool CrossesInto(int sides, float x0, float y0, float x1, float y1, float ax, float ay, float px, float py)
        {
            if ((px - ax) * (px - ax) + (py - ay) * (py - ay) > TeleportStep * TeleportStep) return false;
            return (sides & FromLeft) != 0 && Crosses(ax, px, x0, ay, py, y0, y1)
                || (sides & FromRight) != 0 && Crosses(-ax, -px, -x1, ay, py, y0, y1)
                || (sides & FromBottom) != 0 && Crosses(ay, py, y0, ax, px, x0, x1)
                || (sides & FromTop) != 0 && Crosses(-ay, -py, -y1, ax, px, x0, x1);
        }

        /// <summary>A move from u0 to u1 crosses the edge at u = e going up, at a v (interpolated) within [v0, v1].</summary>
        static bool Crosses(float u0, float u1, float e, float va, float vb, float v0, float v1)
        {
            if (!(u0 < e && u1 >= e)) return false;
            float v = va + (vb - va) * (e - u0) / (u1 - u0);
            return v >= v0 && v <= v1;
        }
    }

    internal sealed class FullTasTracker
    {
        public readonly FullTasGoal[] Goals;
        public int Next;
        public int Limit;
        int _bought;
        // Loose buys (fulltas.json "buys": "loose"; null when strict): every box a buy or reward goal names, the last goal that
        // names it, and the boxes bought before their goal came up (bit per box), which count toward it once it's current.
        string[] _named;
        int[] _lastNaming;
        ulong _banked;
        // Per goal reached: the input tick, the physics tick (its game frames) and the frames the run had stood still in by
        // then (dash freezes, pauses: MacroEngine.HeldFrames), the last two its live time (MacroEngine.LiveSeconds).
        public readonly long[] InputTicks, PhysicsTicks, HeldFrames;

        public FullTasTracker(FullTasGoal[] goals)
        {
            Goals = goals;
            Limit = goals.Length;
            InputTicks = new long[goals.Length];
            PhysicsTicks = new long[goals.Length];
            HeldFrames = new long[goals.Length];
            Reset();
        }

        public void Reset()
        {
            Next = 0;
            _bought = 0;
            _banked = 0;
            _shortGoal = -1;
            for (int i = 0; i < Goals.Length; i++) InputTicks[i] = PhysicsTicks[i] = HeldFrames[i] = -1;
        }

        public bool Done { get { return Next >= Limit; } }

        // A savestate start holds no checkpoint: its respawnPoint is the new game's, a place the run never reached. Until a real
        // checkpoint (checkpointScript) moves it or a start gate sets a course reset point, a quick restart there would teleport,
        // so it ends the run. Set for savestate starts only (NoCheckpointAt); a new game's respawnPoint is the game's own.
        bool _noCheckpoint;
        float _startRespawnX, _startRespawnY;

        public void NoCheckpointAt(float respawnX, float respawnY)
        {
            _noCheckpoint = true;
            _startRespawnX = respawnX;
            _startRespawnY = respawnY;
        }

        /// <summary>
        /// Loose buys: a purchase that isn't the current goal's is kept when a later goal names its box, and a buy goal is reached
        /// as soon as it's current and every box it names is owned (bought for it, or kept from before). Strict (the default):
        /// only a purchase of the current goal's boxes counts.
        /// </summary>
        public void LooseBuys()
        {
            var named = new List<string>();
            var last = new List<int>();
            for (int g = 0; g < Goals.Length; g++)
            {
                if (Goals[g].boxes == null) continue;
                foreach (string box in Goals[g].boxes)
                {
                    int i = named.IndexOf(box);
                    if (i < 0) { named.Add(box); last.Add(g); }
                    else last[i] = g;
                }
            }
            if (named.Count > 64) throw new InvalidOperationException("Loose buys track at most 64 boxes; this run's goals name " + named.Count + ".");
            _named = named.ToArray();
            _lastNaming = last.ToArray();
        }

        public bool Loose { get { return _named != null; } }

        /// <summary>A tracker for other goals with this one's checkpoint guard and buy rule (the loop builder's extra laps).</summary>
        public FullTasTracker For(FullTasGoal[] goals)
        {
            var t = new FullTasTracker(goals);
            t._noCheckpoint = _noCheckpoint;
            t._startRespawnX = _startRespawnX;
            t._startRespawnY = _startRespawnY;
            if (Loose) t.LooseBuys();
            return t;
        }

        /// <summary>Why a quick restart now would leave the game's checkpoints (null when it respawns at a real one).</summary>
        public string RestartRefused(float respawnX, float respawnY, bool courseReset)
        {
            if (!_noCheckpoint || courseReset || respawnX != _startRespawnX || respawnY != _startRespawnY) return null;
            return "Quick restart with no checkpoint touched since the savestate start";
        }

        public FullTasGoal Current { get { return Next < Goals.Length && Next < Limit ? Goals[Next] : null; } }

        public bool CourseStarted(int course, long input, long physics, long held)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.Start && g.course == course && Reach(input, physics, held);
        }

        public bool CourseFinished(int course, long input, long physics, long held)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.Finish && g.course == course && Reach(input, physics, held);
        }

        public bool Touched(long input, long physics, long held)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.At && Reach(input, physics, held);
        }

        public void QuickRestarted()
        {
            if (Next == 0 || Next > Limit || Goals[Next - 1].kind != FullTasGoal.At) return;
            Next--;
            InputTicks[Next] = PhysicsTicks[Next] = HeldFrames[Next] = -1;
            _bought = 0;
        }

        public bool Bought(string path, string id, long input, long physics, long held)
        {
            FullTasGoal g = Current;
            if (g != null && (g.kind == FullTasGoal.Buy || g.kind == FullTasGoal.Reward) && g.boxes != null)
                for (int i = 0; i < g.boxes.Length; i++)
                    if ((_bought & (1 << i)) == 0 && (g.boxes[i] == id || g.boxes[i] == path))
                    {
                        _bought |= 1 << i;
                        return Owned(g) && Reach(input, physics, held);
                    }
            if (!Loose) return false;
            int b = Array.IndexOf(_named, id);
            if (b < 0) b = Array.IndexOf(_named, path);
            if (b >= 0 && _lastNaming[b] > Next) _banked |= 1UL << b;
            return false;
        }

        /// <summary>Whether every box goal g names is bought for it or kept (loose buys).</summary>
        bool Owned(FullTasGoal g)
        {
            for (int i = 0; i < g.boxes.Length; i++)
                if ((_bought & (1 << i)) == 0 && (!Loose || (_banked & 1UL << Array.IndexOf(_named, g.boxes[i])) == 0)) return false;
            return true;
        }

        bool Reach(long input, long physics, long held)
        {
            FullTasGoal g = Goals[Next];
            if (Loose && g.boxes != null)
                for (int i = 0; i < g.boxes.Length; i++)
                    if ((_bought & (1 << i)) == 0) _banked &= ~(1UL << Array.IndexOf(_named, g.boxes[i]));
            InputTicks[Next] = input;
            PhysicsTicks[Next] = physics;
            HeldFrames[Next] = held;
            Next++;
            _bought = 0;
            // Loose buys: a buy goal whose boxes were all bought before it came up is reached on the same tick.
            FullTasGoal then = Current;
            if (Loose && then != null && (then.kind == FullTasGoal.Buy || then.kind == FullTasGoal.Reward) && then.boxes != null && Owned(then))
                Reach(input, physics, held);
            return true;
        }

        public sealed class State
        {
            public int Next, Bought;
            public ulong Banked;
            public long[] InputTicks, PhysicsTicks, HeldFrames;
        }

        public State Capture()
        {
            return new State { Next = Next, Bought = _bought, Banked = _banked, InputTicks = (long[])InputTicks.Clone(), PhysicsTicks = (long[])PhysicsTicks.Clone(),
                HeldFrames = (long[])HeldFrames.Clone() };
        }

        // The current buy goal's box touched without the game buying it (upgradeBox.OnTriggerStay2D past its cooldown, with less
        // held than upgradeCost or the box inactive): the goal, the touches, and the active one with the most held. For the end
        // reason only (Why): not in State, and a restore clears it (snapshots sit at goal boundaries).
        int _shortGoal = -1, _shortTouches;
        long _shortInput;
        double _shortHeld, _shortCost;
        string _shortBox, _shortCurrency;

        /// <summary>The game refused a purchase at a touch of this box: less held than its cost, or the box inactive.</summary>
        public void TouchedShort(string path, string id, long input, double held, double cost, string currency, bool active)
        {
            FullTasGoal g = Current;
            if (g?.boxes == null || Array.IndexOf(g.boxes, id) < 0 && Array.IndexOf(g.boxes, path) < 0) return;
            if (_shortGoal != Next) { _shortGoal = Next; _shortTouches = 0; _shortBox = null; }
            _shortTouches++;
            if (!active || _shortBox != null && held <= _shortHeld) return;
            (_shortBox, _shortInput, _shortHeld, _shortCost, _shortCurrency) = (path, input, held, cost, currency);
        }

        /// <summary>Between an end reason and its <see cref="Why"/>: <see cref="Unexplained"/> cuts there.</summary>
        public const string WhyMark = "; why: ";
        const string StrayTail = ", which isn't the next goal's box";

        /// <summary>
        /// Why a run of these goals ended where it did (null: nothing to add). A strict run's stray buy names the goal it expected
        /// and, from <paramref name="boxes"/> (null: unknown), what that goal's boxes cost against what the run held. A death or
        /// stop at a buy goal says whether its box was touched, the most held at a touch against its cost, and the shortfall.
        /// </summary>
        public string Why(string reason, Func<string[], string> boxes)
        {
            FullTasGoal g = Current;
            if (reason == null || g == null) return null;
            string goal = "goal " + (Next + 1) + " " + g.label;
            bool buy = (g.kind == FullTasGoal.Buy || g.kind == FullTasGoal.Reward) && g.boxes != null;
            if (reason.Contains(StrayTail))
                return "the next goal is " + goal + (buy && boxes != null ? " (" + boxes(g.boxes) + ")" : "");
            if (!buy) return null;
            string touched;
            if (_shortGoal != Next) touched = "its box not touched since " + (Next == 0 ? "the start" : "goal " + Next + " (input " + InputTicks[Next - 1] + ")");
            else if (_shortBox == null) touched = "its box touched " + _shortTouches + "x, inactive (capped, or not unlocked yet)";
            else touched = Num(_shortCost - _shortHeld) + " " + _shortCurrency + " short at input " + _shortInput + ": held " + Num(_shortHeld) + " of its " + Num(_shortCost)
                + (g.boxes.Length > 1 ? " (" + _shortBox + ")" : "") + (_shortTouches == 1 ? ", its one touch" : ", the most of " + _shortTouches + " touches");
            return (reason.StartsWith("Player death", StringComparison.Ordinal) ? "died after missing " : "stopped at ") + goal + " (" + touched + ")";
        }

        static string Num(double v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The end reason with its <see cref="Why"/> after <see cref="WhyMark"/>.</summary>
        public string Explain(string reason, Func<string[], string> boxes = null)
        {
            string why = Why(reason, boxes);
            return why == null ? reason : reason + WhyMark + why;
        }

        /// <summary>An end reason without its <see cref="Why"/> (to compare ends).</summary>
        public static string Unexplained(string reason)
        {
            int at = reason?.IndexOf(WhyMark, StringComparison.Ordinal) ?? -1;
            return at < 0 ? reason : reason.Substring(0, at);
        }

        public void Restore(State s)
        {
            _shortGoal = -1;
            Next = s.Next;
            _bought = s.Bought;
            _banked = s.Banked;
            // The engine farm planner's goal list grows as it plans: a snapshot taken with fewer goals leaves the rest unreached.
            int n = Math.Min(s.InputTicks.Length, InputTicks.Length);
            Array.Copy(s.InputTicks, InputTicks, n);
            Array.Copy(s.PhysicsTicks, PhysicsTicks, n);
            Array.Copy(s.HeldFrames, HeldFrames, n);
            for (int i = n; i < InputTicks.Length; i++) InputTicks[i] = PhysicsTicks[i] = HeldFrames[i] = -1;
        }

        public int Hash() { return Next * 397 ^ _bought ^ (int)(_banked * 0x9E3779B97F4A7C15UL >> 32); }
    }
}
