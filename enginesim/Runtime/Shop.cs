using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class Shop
    {
        const int CurrencyCount = 7;
        const int Cash = (int)globalStats.Currencies.Cash;
        internal static readonly AccessTools.FieldRef<upgradeBox, float> Cooldown = AccessTools.FieldRefAccess<upgradeBox, float>("boxCooldown");
        internal static readonly AccessTools.FieldRef<upgradeBox, globalStats.Currencies> Currency = AccessTools.FieldRefAccess<upgradeBox, globalStats.Currencies>("upgradeCurrency");
        static readonly AccessTools.FieldRef<courseScript, bool> ClearedThisVman = AccessTools.FieldRefAccess<courseScript, bool>("clearedThisVman");

        readonly Simulation _sim;
        readonly upgradeBox[] _boxes;
        readonly Dictionary<upgradeBox, string> _paths = new Dictionary<upgradeBox, string>();
        readonly Dictionary<upgradeBox, string> _ids = new Dictionary<upgradeBox, string>();
        readonly double[] _start = new double[CurrencyCount];
        double[] _spent = new double[CurrencyCount];
        double[] _awarded = new double[CurrencyCount];
        internal Economy Economy { get; private set; }
        readonly List<BuyMaxRoutine> _buyMax = new List<BuyMaxRoutine>();
        bool _continuing;

        internal struct BuyMaxRoutine
        {
            public upgradeBox Box;
            public int Currency, Left;
            public Collider2D Collision;
        }

        internal sealed class State
        {
            public double[] Spent, Awarded;
            public Economy.State Economy;
            public BuyMaxRoutine[] BuyMax;
        }

        internal Shop(Simulation sim, World world)
        {
            _sim = sim;
            upgradeBox[] boxes = world.All<upgradeBox>();
            _boxes = boxes;
            foreach (upgradeBox b in boxes)
            {
                _paths[b] = HierarchyPath(b.transform);
                _ids[b] = FullTasGoal.BoxId(_paths[b], world.SiblingIndex[b.transform]);
            }
            if (globalStats.currencyLookup != null)
                foreach (KeyValuePair<globalStats.Currencies, double> c in globalStats.currencyLookup)
                    if ((int)c.Key >= 0 && (int)c.Key < CurrencyCount) _start[(int)c.Key] = c.Value;
            Hooks.ClonesOnScreen = (clones, onScreen) => Economy?.OnScreen((clonesScript)clones, onScreen);
            Hooks.CourseReward = course => Economy?.UpdateReward((courseScript)course);
            Hooks.AtomTick = collider => Economy?.AtomTick((AtomColliderSystem)collider);
        }

        internal void LoadStart(JsonElement economy, Action<object, JsonElement> applyFields)
        {
            foreach (JsonElement c in economy.GetProperty("currencies").EnumerateArray())
            {
                int currency = c[0].GetInt32();
                if (currency >= 0 && currency < CurrencyCount) _start[currency] = c[1].GetDouble();
            }
            foreach (JsonElement b in economy.GetProperty("boxes").EnumerateArray())
                if (_sim.World.Objects.TryGetValue(b.GetProperty("id").GetInt32(), out UnityEngine.Object o) && o is upgradeBox box)
                    applyFields(box, b.GetProperty("fields"));
            Array.Clear(_spent, 0, _spent.Length);
            Array.Clear(_awarded, 0, _awarded.Length);
            _buyMax.Clear();
            Economy = null; // the new economy's constructor reads Holds
            Economy = economy.TryGetProperty("clones", out _) ? new Economy(_sim, economy) : null;
        }

        internal void TickCooldowns(float dt)
        {
            foreach (upgradeBox box in _boxes)
            {
                if (!box.isActiveAndEnabled) continue;
                float cooldown = Cooldown(box);
                if (!(cooldown >= 0f)) continue;
                _sim.Touch(box);
                Cooldown(box) = cooldown - dt;
            }
        }

        double Available(int currency)
        {
            return _start[currency] - _spent[currency] + _awarded[currency] + (Economy?.Income[currency] ?? 0.0);
        }

        public double CashNow => Available(Cash);

        internal bool Named(string[] boxes, upgradeBox b) => Array.IndexOf(boxes, _ids[b]) >= 0 || Array.IndexOf(boxes, _paths[b]) >= 0;

        internal string Id(upgradeBox box) => _ids[box];

        internal IEnumerable<upgradeBox> PaidFor()
        {
            foreach (upgradeBox b in _boxes)
                if (Affordable(new[] { _ids[b] }) == true) yield return b;
        }

        internal double Holds(globalStats.Currencies currency) { return Available((int)currency); }

        internal bool? Affordable(string[] boxes, int levels = 1)
        {
            double[] need = Need(boxes, levels);
            if (need == null) return null;
            for (int c = 0; c < CurrencyCount; c++)
                if (need[c] > 0.0 && Available(c) < need[c]) return false;
            return true;
        }

        internal double[] Need(string[] boxes, int levels = 1, bool shown = true)
        {
            var need = new double[CurrencyCount];
            foreach (upgradeBox b in _boxes)
            {
                if (!Named(boxes, b)) continue;
                if (shown && (!b.isActive || !b.gameObject.activeInHierarchy) || b.TimesUsed + levels > b.Cap) return null;
                int currency = (int)Currency(b);
                if (currency < 0 || currency >= CurrencyCount) continue;
                double cost = b.upgradeCost;
                for (int i = 0; i < levels; i++)
                {
                    need[currency] += cost;
                    cost = NextCost(cost, b);
                }
            }
            return need;
        }

        static readonly AccessTools.FieldRef<TreeController, TreeSegmentActivationComponent[]> TreeSegments = AccessTools.FieldRefAccess<TreeController, TreeSegmentActivationComponent[]>("TreeSegments");
        static readonly AccessTools.FieldRef<TreeSegmentActivationComponent, bool> AutoTriggerNext = AccessTools.FieldRefAccess<TreeSegmentActivationComponent, bool>("autoTriggerNext");
        static readonly AccessTools.FieldRef<TreeSegmentActivationComponent, int> NextAuto = AccessTools.FieldRefAccess<TreeSegmentActivationComponent, int>("NextSegmentToAutoTriggerIndex");

        internal List<string> TreeWay(string box)
        {
            var way = new List<string>();
            upgradeBox target = Box(box);
            foreach (TreeController tree in _sim.World.All<TreeController>())
            {
                TreeSegmentActivationComponent[] segments = TreeSegments(tree) ?? Array.Empty<TreeSegmentActivationComponent>();
                int segment = Array.FindIndex(segments, s => s != null && s.box != null && s.box == target?.gameObject);
                var seen = new HashSet<int>();
                while (segment >= 0 && seen.Add(segment))
                {
                    upgradeBox grower = _boxes.FirstOrDefault(b => UpgradeTree(b) == tree && SegmentsToTrigger(b)?.Contains(segment) == true);
                    if (grower != null)
                    {
                        way.Insert(0, _ids[grower]);
                        segment = Array.FindIndex(segments, s => s != null && s.box != null && s.box == grower.gameObject);
                    }
                    else segment = Array.FindIndex(segments, s => s != null && AutoTriggerNext(s) && NextAuto(s) == segment);
                }
                if (way.Count > 0) break;
            }
            return way;
        }

        internal double Gained(globalStats.Currencies currency) { return Available((int)currency) + _spent[(int)currency]; }

        internal bool Buying(string[] boxes)
        {
            foreach (BuyMaxRoutine r in _buyMax)
                if (Named(boxes, r.Box)) return true;
            return false;
        }

        internal string Describe(string[] boxes)
        {
            var parts = new List<string>();
            foreach (upgradeBox b in _boxes)
            {
                if (!Named(boxes, b)) continue;
                int currency = (int)Currency(b);
                double held = currency >= 0 && currency < CurrencyCount ? Available(currency) : double.NaN;
                parts.Add(_paths[b] + ": " + b.TimesUsed + "/" + b.Cap + (b.isActive ? "" : ", inactive") + (b.gameObject.activeInHierarchy ? "" : ", hidden")
                    + ", costs " + b.upgradeCost.ToString("G4") + " " + (globalStats.Currencies)currency + ", holds " + held.ToString("G4"));
            }
            return string.Join("; ", parts);
        }

        internal void StepClones(float dt)
        {
            Economy?.Step(dt);
            Economy?.AtomFrame(dt);
        }

        internal double FromBase(string box, int levels)
        {
            upgradeBox b = Box(box);
            if (b == null) return double.PositiveInfinity;
            double cost = b.baseUpgradeCost, sum = 0;
            for (int i = 0; i < levels; i++) { sum += cost; cost = NextCost(cost, b); }
            return sum;
        }

        internal int BuyMaxLeft(upgradeBox box) { int left = 0; foreach (BuyMaxRoutine r in _buyMax) if (r.Box == box) left += r.Left; return left; }

        internal int BuyMaxLeft(string[] boxes) { int left = 0; foreach (BuyMaxRoutine r in _buyMax) if (Named(boxes, r.Box)) left += r.Left; return left; }

        internal void FixedStep()
        {
            _continuing = true;
            for (int i = 0; i < _buyMax.Count; i++)
            {
                BuyMaxRoutine r = _buyMax[i];
                if (r.Box.gameObject.activeInHierarchy && BuyMaxStep(ref r)) _buyMax[i] = r;
                else _buyMax.RemoveAt(i--);
            }
            _continuing = false;
        }

        internal void CourseFinished(courseScript course, float time)
        {
            if (VmanScript.isCurrentlyVman) VmanCourseCompleted(course);
            else Economy?.CourseFinished(course, time);
        }

        void VmanCourseCompleted(courseScript course)
        {
            if (ClearedThisVman(course)) return;
            int currency = (int)course.completionCurrency;
            if (course.completionReward != 0.0 && currency >= 0 && currency < CurrencyCount) _awarded[currency] += course.completionReward;
            ClearedThisVman(course) = true;
        }

        internal void Stay(object boxObject, Collider2D collision)
        {
            var box = (upgradeBox)boxObject;
            if (!(Cooldown(box) <= 0f)) return;
            Cooldown(box) = 0.2f;
            if (_sim.RewardHit(box, collision)) return;
            int currency = (int)Currency(box);
            if (!(Available(currency) >= box.upgradeCost && box.isActive))
            {
                if (_sim.Goals != null && !_sim.Driving && _sim.Engine.IsRunning)
                    _sim.Goals.TouchedShort(_paths[box], _ids[box], _sim.InputBase + _sim.Engine.RunTicks, Available(currency), box.upgradeCost,
                        ((globalStats.Currencies)currency).ToString(), box.isActive);
                return;
            }
            if (box.upgrade == localUpgrades.localUpgradeSet.GLOBAL && box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && !AtomPrestige(box, currency)) return;
            if (!(box.buyMax && !box.neverBuyMax))
            {
                Buy(box, currency, collision);
                UnityEngine.Random.Draw();
            }
            else
            {
                var routine = new BuyMaxRoutine { Box = box, Currency = currency, Left = CouldBuy(box, currency), Collision = collision };
                if (BuyMaxStep(ref routine)) _buyMax.Add(routine);
            }
            GrowTree(box);
            UnityEngine.Random.Draw();
            UnityEngine.Random.Draw();
        }

        // Mirrors buyUpgrade's atom block (upgradeBox.cs:641-654).
        bool AtomPrestige(upgradeBox box, int currency)
        {
            if (Economy != null) return Economy.AtomPrestige(box, (globalStats.Currencies)currency);
            _sim.Engine.Abort("Unmodeled purchase: " + _paths[box] + " (an atom box's course reset needs the native economy: this trace's start state has no clones)");
            return false;
        }

        static readonly AccessTools.FieldRef<upgradeBox, TreeController> UpgradeTree = AccessTools.FieldRefAccess<upgradeBox, TreeController>("upgradeTree");
        static readonly AccessTools.FieldRef<upgradeBox, int[]> SegmentsToTrigger = AccessTools.FieldRefAccess<upgradeBox, int[]>("SegmentsToTrigger");

        void GrowTree(upgradeBox box)
        {
            TreeController tree = UpgradeTree(box);
            if (tree == null) return;
            int[] segments = SegmentsToTrigger(box);
            if (segments == null)
            {
                if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled purchase: " + _paths[box] + " (a tree box without SegmentsToTrigger: the game throws)");
                return;
            }
            _sim.Touch(tree);
            tree.growSegments(segments);
        }

        bool BuyMaxStep(ref BuyMaxRoutine r)
        {
            if (r.Left <= 0 || !(Available(r.Currency) >= r.Box.upgradeCost)) return false;
            r.Left--;
            _sim.Touch(r.Box);
            Buy(r.Box, r.Currency, r.Collision);
            return r.Left > 0;
        }

        int CouldBuy(upgradeBox box, int currency)
        {
            double cash = Available(currency), cost = box.upgradeCost;
            int n = 0;
            while (cash >= cost && box.TimesUsed + n < box.Cap)
            {
                n++;
                cash -= cost;
                cost += box.upgradeAddFactor;
                cost = Math.Pow(cost, box.upgradePowerScaleFactor);
                cost *= box.upgradeScaleFactor;
            }
            return n;
        }

        void Buy(upgradeBox box, int currency, Collider2D collision)
        {
            string path = _paths[box];
            localUpgrades.localUpgradeSet kind = box.upgrade;
            Movement player = kind == localUpgrades.localUpgradeSet.Movement ? collision.GetComponent<Movement>() : null;
            bool global = kind == localUpgrades.localUpgradeSet.GLOBAL;
            upgradeBox.movementUpgrades move = box.movementUpgrade;
            bool swapBlocks = kind == localUpgrades.localUpgradeSet.Movement && move == upgradeBox.movementUpgrades.swapBlocksOnce;
            bool modeled;
            string why;
            if (global || kind == localUpgrades.localUpgradeSet.prestige)
            {
                modeled = Economy != null && (kind != localUpgrades.localUpgradeSet.prestige || !VmanScript.isCurrentlyVman);
                why = Economy == null ? " (a global upgrade needs the native economy: this trace's start state has no clones)"
                    : " (a Vman prestige: the game returns before ScaleBoxCost and spends nothing)";
            }
            else if (kind == localUpgrades.localUpgradeSet.Movement)
            {
                // The Omni Dash box's first buy resets Courses 1-5 (Economy.OmniDashFirstBuy); later buys only toggle.
                bool omniFirst = move == upgradeBox.movementUpgrades.OmniDash && box.TimesUsed == 0;
                modeled = player != null && (swapBlocks || move == upgradeBox.movementUpgrades.dash || move == upgradeBox.movementUpgrades.wallJump
                    || move == upgradeBox.movementUpgrades.doubleJump || move == upgradeBox.movementUpgrades.unlockBlockSwap
                    || move == upgradeBox.movementUpgrades.freeVmanOmnidash || move == upgradeBox.movementUpgrades.OmniDash && (!omniFirst || Economy != null));
                why = player == null ? " (no player at the box)"
                    : omniFirst && Economy == null ? " (the Omni Dash box's first buy needs the native economy: this trace's start state has no clones)"
                    : " (movement upgrade " + move + ")";
            }
            else
            {
                modeled = true;
                why = null;
            }
            if (!modeled)
            {
                _sim.Engine.Abort("Unmodeled purchase: " + path + why);
                return;
            }
            box.TimesUsed++;
            if (!VmanScript.isCurrentlyVman) _spent[currency] += box.upgradeCost;
            if (swapBlocks)
            {
                box.isActive = false;
                _sim.Bought(path, _ids[box], _continuing);
                if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled purchase: block swap (collider changes)");
                return;
            }
            if (Economy != null && kind != localUpgrades.localUpgradeSet.Movement && !(global ? Economy.BoughtGlobal(box) : Economy.Bought(box, kind))) return;
            bool unlocksChange = false;
            if (kind == localUpgrades.localUpgradeSet.Movement)
            {
                unlocksChange = move == upgradeBox.movementUpgrades.dash || move == upgradeBox.movementUpgrades.doubleJump
                    || move == upgradeBox.movementUpgrades.freeVmanOmnidash || move == upgradeBox.movementUpgrades.unlockBlockSwap
                    || move == upgradeBox.movementUpgrades.OmniDash
                    || move == upgradeBox.movementUpgrades.wallJump && !player.wallJumpUnlocked;
                switch (box.movementUpgrade)
                {
                    case upgradeBox.movementUpgrades.dash:
                        player.dashUnlocked = true;
                        player.maxAirDashes++;
                        if (UpgradeTree(box) != null) TreeBoxTriggered(dash: true);
                        break;
                    case upgradeBox.movementUpgrades.wallJump: player.wallJumpUnlocked = true; break;
                    case upgradeBox.movementUpgrades.doubleJump:
                        player.doubleJumpUnlocked = true;
                        player.maxAirJumps++;
                        if (UpgradeTree(box) != null) TreeBoxTriggered(dash: false);
                        break;
                    case upgradeBox.movementUpgrades.unlockBlockSwap: player.blockSwapUnlocked = true; break;
                    case upgradeBox.movementUpgrades.freeVmanOmnidash: player.dashUnlocked = true; player.omniDashUnlocked = true; player.maxAirDashes++; break;
                    case upgradeBox.movementUpgrades.OmniDash:
                        if (box.TimesUsed == 1 && !Economy.OmniDashFirstBuy(box)) return;
                        if (!OmniDashToggle(box, player)) return;
                        break;
                }
                if (move != upgradeBox.movementUpgrades.OmniDash) box.isActive = false;
            }
            else if (global)
            {
                box.upgradeCost += box.upgradeAddFactor;
                box.upgradeCost = Math.Pow(box.upgradeCost, box.upgradePowerScaleFactor);
                box.upgradeCost = Math.Round(box.upgradeCost * box.upgradeScaleFactor);
            }
            else ScaleBoxCost(box);
            if (box.TimesUsed >= box.Cap) box.isActive = false;
            _sim.Bought(path, _ids[box], _continuing);
            if (unlocksChange && _sim.Goals == null && _sim.Engine.IsRunning)
                _sim.Engine.Abort("Category unlocks changed: bought " + move + " at " + path);
        }

        // A tree's dash/double-jump box counts itself (upgradeBox.cs:788-802); Omni Dash toggle derives counts from them.
        void TreeBoxTriggered(bool dash)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null)
            {
                if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled purchase: a tree movement box without globalStats (the game throws)");
                return;
            }
            _sim.Touch(stats);
            if (dash) stats.DashBoxesTriggered++;
            else stats.DjBoxesTriggered++;
        }

        // Omni Dash toggle (upgradeBox.cs:841-860): odd buy on with tree's double jumps, even off with one more; dashes +1.
        bool OmniDashToggle(upgradeBox box, Movement player)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null)
            {
                if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled purchase: the Omni Dash box without globalStats (the game throws)");
                return false;
            }
            bool on = box.TimesUsed % 2 != 0;
            player.omniDashUnlocked = on;
            player.maxAirJumps = stats.DjBoxesTriggered + (on ? 0 : 1);
            player.airJumpsLeft = player.maxAirJumps;
            player.maxAirDashes = stats.DashBoxesTriggered + 1;
            box.upgradeCost = 0.0;
            box.baseUpgradeCost = 0.0;
            ScaleBoxCost(box);
            return true;
        }

        internal static void ScaleBoxCost(upgradeBox box) { box.upgradeCost = NextCost(box.upgradeCost, box); }

        // Replicates upgradeBox's cost raise (upgradeBox.cs:941-944): add, power, ceiling of scaled cost, ceiling.
        static double NextCost(double cost, upgradeBox box)
        {
            cost += box.upgradeAddFactor;
            cost = Math.Pow(cost, box.upgradePowerScaleFactor);
            cost = Math.Ceiling(cost * box.upgradeScaleFactor);
            return Math.Ceiling(cost);
        }

        internal void ZeroCash() { _spent[Cash] = _start[Cash] + _awarded[Cash]; }

        internal bool HasBox(string box) { return _ids.ContainsValue(box) || _paths.ContainsValue(box); }

        internal upgradeBox Box(string box)
        {
            foreach (upgradeBox b in _boxes)
                if (_ids[b] == box || _paths[b] == box) return b;
            return null;
        }

        internal (float x, float y)? GlobalCashAt(string box)
        {
            upgradeBox b = Box(box);
            if (b == null || b.upgrade != localUpgrades.localUpgradeSet.GLOBAL || b.globalUpgrade != globalStats.globalUpgradeSet.cashPerLoop) return null;
            Vector3 p = b.transform.position;
            return (p.x, p.y);
        }

        internal upgradeBox.movementUpgrades? MovementUpgrade(string box)
        {
            upgradeBox b = Box(box);
            return b != null && b.upgrade == localUpgrades.localUpgradeSet.Movement ? b.movementUpgrade : null;
        }

        internal Caps AfterBuys(Caps caps, IEnumerable<string> boxes)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            int treeJumps = (object)stats == null ? 0 : stats.DjBoxesTriggered, treeDashes = (object)stats == null ? 0 : stats.DashBoxesTriggered;
            var omniUses = new Dictionary<upgradeBox, int>();
            foreach (string token in boxes)
            {
                upgradeBox b = Box(token);
                if (b == null || b.upgrade != localUpgrades.localUpgradeSet.Movement) continue;
                switch (b.movementUpgrade)
                {
                    case upgradeBox.movementUpgrades.wallJump: caps.WallJump = true; break;
                    case upgradeBox.movementUpgrades.dash:
                        caps.Dashes++;
                        if (UpgradeTree(b) != null) treeDashes++;
                        break;
                    case upgradeBox.movementUpgrades.doubleJump:
                        caps.AirJumps++;
                        if (UpgradeTree(b) != null) treeJumps++;
                        break;
                    case upgradeBox.movementUpgrades.freeVmanOmnidash: caps.Dashes++; caps.Omni = true; break;
                    case upgradeBox.movementUpgrades.unlockBlockSwap: caps.BlockSwap = true; break;
                    case upgradeBox.movementUpgrades.OmniDash:
                    {
                        int uses = (omniUses.TryGetValue(b, out int used) ? used : b.TimesUsed) + 1;
                        omniUses[b] = uses;
                        if (uses == 1) caps.Orbs = true;
                        caps.Omni = uses % 2 != 0;
                        if (caps.AirJumps > 0) caps.AirJumps = Math.Max(1, treeJumps + (caps.Omni ? 0 : 1));
                        if (caps.Dashes > 0) caps.Dashes = Math.Max(1, treeDashes + 1);
                        break;
                    }
                }
            }
            return caps;
        }

        internal string NearestBox(float x, float y, float range)
        {
            string best = null;
            float bestDistance = range * range;
            foreach (KeyValuePair<upgradeBox, string> b in _ids)
            {
                Vector3 p = b.Key.transform.position;
                float dx = p.x - x, dy = p.y - y, d = dx * dx + dy * dy;
                if (d < bestDistance) { best = b.Value; bestDistance = d; }
            }
            return best;
        }

        internal State Capture() { return new State { Spent = (double[])_spent.Clone(), Awarded = (double[])_awarded.Clone(), Economy = Economy?.Capture(), BuyMax = _buyMax.ToArray() }; }

        internal void Restore(State s)
        {
            Array.Copy(s.Spent, _spent, CurrencyCount);
            Array.Copy(s.Awarded, _awarded, CurrencyCount);
            if (Economy != null && s.Economy != null) Economy.Restore(s.Economy);
            _buyMax.Clear();
            _buyMax.AddRange(s.BuyMax);
        }

        internal void AddTo(ref HashCode h)
        {
            foreach (double s in _spent) h.Add(s);
            foreach (double a in _awarded) h.Add(a);
            Economy?.AddTo(ref h);
            foreach (BuyMaxRoutine r in _buyMax)
            {
                h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Box));
                h.Add(r.Left);
            }
        }

        static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
