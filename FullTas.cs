using System;

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
        // zip-plan names its purchases with these tokens.
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

        public bool EnteredFrom(float rx0, float rx1, float ry0, float ry1, float px0, float px1, float py0, float py1)
        {
            return from == 0
                || (from & FromLeft) != 0 && px1 <= rx0 || (from & FromRight) != 0 && px0 >= rx1
                || (from & FromBottom) != 0 && py1 <= ry0 || (from & FromTop) != 0 && py0 >= ry1;
        }
    }

    internal sealed class FullTasTracker
    {
        public readonly FullTasGoal[] Goals;
        public int Next;
        public int Limit;
        int _bought;
        public readonly long[] InputTicks, PhysicsTicks;

        public FullTasTracker(FullTasGoal[] goals)
        {
            Goals = goals;
            Limit = goals.Length;
            InputTicks = new long[goals.Length];
            PhysicsTicks = new long[goals.Length];
            Reset();
        }

        public void Reset()
        {
            Next = 0;
            _bought = 0;
            for (int i = 0; i < Goals.Length; i++) InputTicks[i] = PhysicsTicks[i] = -1;
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

        /// <summary>A tracker for other goals with this one's checkpoint guard (the loop builder's extra laps).</summary>
        public FullTasTracker For(FullTasGoal[] goals)
        {
            var t = new FullTasTracker(goals);
            t._noCheckpoint = _noCheckpoint;
            t._startRespawnX = _startRespawnX;
            t._startRespawnY = _startRespawnY;
            return t;
        }

        /// <summary>Why a quick restart now would leave the game's checkpoints (null when it respawns at a real one).</summary>
        public string RestartRefused(float respawnX, float respawnY, bool courseReset)
        {
            if (!_noCheckpoint || courseReset || respawnX != _startRespawnX || respawnY != _startRespawnY) return null;
            return "Quick restart with no checkpoint touched since the savestate start";
        }

        public FullTasGoal Current { get { return Next < Goals.Length && Next < Limit ? Goals[Next] : null; } }

        public bool CourseStarted(int course, long input, long physics)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.Start && g.course == course && Reach(input, physics);
        }

        public bool CourseFinished(int course, long input, long physics)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.Finish && g.course == course && Reach(input, physics);
        }

        public bool Touched(long input, long physics)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.At && Reach(input, physics);
        }

        public void QuickRestarted()
        {
            if (Next == 0 || Next > Limit || Goals[Next - 1].kind != FullTasGoal.At) return;
            Next--;
            InputTicks[Next] = PhysicsTicks[Next] = -1;
            _bought = 0;
        }

        public bool Bought(string path, string id, long input, long physics)
        {
            FullTasGoal g = Current;
            if (g == null || g.kind != FullTasGoal.Buy && g.kind != FullTasGoal.Reward || g.boxes == null) return false;
            for (int i = 0; i < g.boxes.Length; i++)
                if ((_bought & (1 << i)) == 0 && (g.boxes[i] == id || g.boxes[i] == path)) { _bought |= 1 << i; break; }
            return _bought == (1 << g.boxes.Length) - 1 && Reach(input, physics);
        }

        bool Reach(long input, long physics)
        {
            InputTicks[Next] = input;
            PhysicsTicks[Next] = physics;
            Next++;
            _bought = 0;
            return true;
        }

        public sealed class State
        {
            public int Next, Bought;
            public long[] InputTicks, PhysicsTicks;
        }

        public State Capture()
        {
            return new State { Next = Next, Bought = _bought, InputTicks = (long[])InputTicks.Clone(), PhysicsTicks = (long[])PhysicsTicks.Clone() };
        }

        public void Restore(State s)
        {
            Next = s.Next;
            _bought = s.Bought;
            // The engine farm planner's goal list grows as it plans: a snapshot taken with fewer goals leaves the rest unreached.
            int n = Math.Min(s.InputTicks.Length, InputTicks.Length);
            Array.Copy(s.InputTicks, InputTicks, n);
            Array.Copy(s.PhysicsTicks, PhysicsTicks, n);
            for (int i = n; i < InputTicks.Length; i++) InputTicks[i] = PhysicsTicks[i] = -1;
        }

        public int Hash() { return Next * 397 ^ _bought; }
    }
}
