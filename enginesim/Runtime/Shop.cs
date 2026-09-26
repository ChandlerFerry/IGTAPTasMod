using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class Shop
    {
        const int CurrencyCount = 7;
        const int Cash = (int)globalStats.Currencies.Cash;
        static readonly FieldInfo CooldownField = typeof(upgradeBox).GetField("boxCooldown", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Func<upgradeBox, float> GetCooldown = Getter<float>(CooldownField);
        static readonly Action<upgradeBox, float> SetCooldown = Setter<float>(CooldownField);
        static readonly Func<upgradeBox, globalStats.Currencies> GetCurrency =
            Getter<globalStats.Currencies>(typeof(upgradeBox).GetField("upgradeCurrency", BindingFlags.Instance | BindingFlags.NonPublic));

        static readonly FieldInfo ClearedThisVmanField = typeof(courseScript).GetField("clearedThisVman", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly Simulation _sim;
        readonly upgradeBox[] _boxes;
        readonly Dictionary<upgradeBox, string> _paths = new Dictionary<upgradeBox, string>();
        readonly Dictionary<upgradeBox, string> _ids = new Dictionary<upgradeBox, string>();
        readonly double[] _start = new double[CurrencyCount];
        double[] _spent = new double[CurrencyCount];
        double[] _awarded = new double[CurrencyCount];
        internal Economy Economy { get; private set; }
        readonly List<BuyMaxRoutine> _buyMax = new List<BuyMaxRoutine>();

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
            Economy = economy.TryGetProperty("clones", out _) ? new Economy(_sim, economy) : null;
        }

        internal void TickCooldowns(float dt)
        {
            foreach (upgradeBox box in _boxes)
            {
                if (!box.isActiveAndEnabled) continue;
                float cooldown = GetCooldown(box);
                if (!(cooldown >= 0f)) continue;
                _sim.Touch(box);
                SetCooldown(box, cooldown - dt);
            }
        }

        double Available(int currency)
        {
            return _start[currency] - _spent[currency] + _awarded[currency] + (currency == Cash ? Economy?.Earned ?? 0.0 : 0.0);
        }

        public double CashNow
        {
            get { return _start[Cash] - _spent[Cash] + _awarded[Cash] + (Economy?.Earned ?? 0.0); }
        }

        internal void StepClones() { Economy?.Step(_sim.World.FixedDeltaTime); }

        internal void EndOfFrame()
        {
            for (int i = 0; i < _buyMax.Count; i++)
            {
                BuyMaxRoutine r = _buyMax[i];
                if (r.Box.gameObject.activeInHierarchy && BuyMaxStep(ref r)) _buyMax[i] = r;
                else _buyMax.RemoveAt(i--);
            }
        }

        internal void CourseFinished(courseScript course, float time)
        {
            if (VmanScript.isCurrentlyVman) VmanCourseCompleted(course);
            else Economy?.CourseFinished(course, time);
        }

        void VmanCourseCompleted(courseScript course)
        {
            if ((bool)ClearedThisVmanField.GetValue(course)) return;
            int currency = (int)course.completionCurrency;
            if (course.completionReward != 0.0 && currency >= 0 && currency < CurrencyCount) _awarded[currency] += course.completionReward;
            ClearedThisVmanField.SetValue(course, true);
        }

        internal void Stay(object boxObject, Collider2D collision)
        {
            var box = (upgradeBox)boxObject;
            if (!(GetCooldown(box) <= 0f)) return;
            SetCooldown(box, 0.2f);
            int currency = (int)GetCurrency(box);
            if (!(Available(currency) >= box.upgradeCost && box.isActive)) return;
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

        static readonly FieldInfo UpgradeTreeField = typeof(upgradeBox).GetField("upgradeTree", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo SegmentsToTriggerField = typeof(upgradeBox).GetField("SegmentsToTrigger", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo TreeSegmentsField = typeof(TreeController).GetField("TreeSegments", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo AudioCooldownField = typeof(TreeController).GetField("audioCooldown", BindingFlags.Instance | BindingFlags.NonPublic);

        void GrowTree(upgradeBox box)
        {
            var tree = UpgradeTreeField.GetValue(box) as TreeController;
            int[] segments = SegmentsToTriggerField.GetValue(box) as int[];
            if (tree == null || segments == null || segments.Length == 0 || tree.GrownSegments.Contains(segments[0])) return;
            Array lengths = TreeSegmentsField.GetValue(tree) as Array;
            _sim.Touch(tree);
            foreach (int segment in segments)
            {
                if (lengths == null || segment >= lengths.Length) continue;
                if (!(bool)AudioCooldownField.GetValue(tree))
                {
                    UnityEngine.Random.Draw();
                    UnityEngine.Random.Draw();
                    AudioCooldownField.SetValue(tree, true);
                    tree.Invoke("EndAudioCooldown", 0.3f);
                }
                tree.GrownSegments.Add(segment);
            }
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
                bool known = kind == localUpgrades.localUpgradeSet.prestige
                    || box.globalUpgrade == globalStats.globalUpgradeSet.cashPerLoop
                    || box.globalUpgrade == globalStats.globalUpgradeSet.maxCloneFastness
                    || box.globalUpgrade == globalStats.globalUpgradeSet.maxCloneBigness
                    || box.globalUpgrade == globalStats.globalUpgradeSet.unlockPrestige;
                modeled = known && Economy != null && (kind != localUpgrades.localUpgradeSet.prestige || !VmanScript.isCurrentlyVman);
                why = !known ? " (global upgrade " + box.globalUpgrade + ")"
                    : Economy == null ? " (a global upgrade needs the native economy: this trace's start state has no clones)"
                    : " (a Vman prestige: the game returns before ScaleBoxCost and spends nothing)";
            }
            else if (kind == localUpgrades.localUpgradeSet.Movement)
            {
                modeled = player != null && (swapBlocks || move == upgradeBox.movementUpgrades.dash || move == upgradeBox.movementUpgrades.wallJump
                    || move == upgradeBox.movementUpgrades.doubleJump || move == upgradeBox.movementUpgrades.unlockBlockSwap
                    || move == upgradeBox.movementUpgrades.freeVmanOmnidash);
                why = player == null ? " (no player at the box)" : " (movement upgrade " + move + ")";
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
                _sim.Bought(path, _ids[box]);
                if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled purchase: block swap (collider changes)");
                return;
            }
            if (Economy != null && kind != localUpgrades.localUpgradeSet.Movement && !(global ? Economy.BoughtGlobal(box) : Economy.Bought(box, kind))) return;
            bool unlocksChange = false;
            if (kind == localUpgrades.localUpgradeSet.Movement)
            {
                unlocksChange = move == upgradeBox.movementUpgrades.dash || move == upgradeBox.movementUpgrades.doubleJump
                    || move == upgradeBox.movementUpgrades.freeVmanOmnidash || move == upgradeBox.movementUpgrades.wallJump && !player.wallJumpUnlocked;
                switch (box.movementUpgrade)
                {
                    case upgradeBox.movementUpgrades.dash: player.dashUnlocked = true; player.maxAirDashes++; break;
                    case upgradeBox.movementUpgrades.wallJump: player.wallJumpUnlocked = true; break;
                    case upgradeBox.movementUpgrades.doubleJump: player.doubleJumpUnlocked = true; player.maxAirJumps++; break;
                    case upgradeBox.movementUpgrades.unlockBlockSwap: player.blockSwapUnlocked = true; break;
                    case upgradeBox.movementUpgrades.freeVmanOmnidash: player.dashUnlocked = true; player.omniDashUnlocked = true; player.maxAirDashes++; break;
                }
                box.isActive = false;
            }
            else if (global)
            {
                box.upgradeCost += box.upgradeAddFactor;
                box.upgradeCost = Math.Pow(box.upgradeCost, box.upgradePowerScaleFactor);
                box.upgradeCost = Math.Round(box.upgradeCost * box.upgradeScaleFactor);
            }
            else ScaleBoxCost(box);
            if (box.TimesUsed >= box.Cap) box.isActive = false;
            _sim.Bought(path, _ids[box]);
            if (kind == localUpgrades.localUpgradeSet.Movement && move == upgradeBox.movementUpgrades.unlockBlockSwap && _sim.Engine.IsRunning)
                _sim.Engine.Abort("Unmodeled purchase: block swap on dash");
            else if (unlocksChange && _sim.Goals == null && _sim.Engine.IsRunning)
                _sim.Engine.Abort("Category unlocks changed: bought " + move + " at " + path);
        }

        internal static void ScaleBoxCost(upgradeBox box)
        {
            box.upgradeCost += box.upgradeAddFactor;
            box.upgradeCost = Math.Pow(box.upgradeCost, box.upgradePowerScaleFactor);
            box.upgradeCost = Math.Ceiling(box.upgradeCost * box.upgradeScaleFactor);
            box.upgradeCost = Math.Ceiling(box.upgradeCost);
        }

        internal void ZeroCash() { _spent[Cash] = _start[Cash] + _awarded[Cash]; }

        internal bool HasBox(string box) { return _ids.ContainsValue(box) || _paths.ContainsValue(box); }

        internal upgradeBox.movementUpgrades? MovementUpgrade(string box)
        {
            foreach (upgradeBox b in _boxes)
                if (_ids[b] == box || _paths[b] == box)
                    return b.upgrade == localUpgrades.localUpgradeSet.Movement ? b.movementUpgrade : null;
            return null;
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

        static Func<upgradeBox, T> Getter<T>(FieldInfo field)
        {
            ParameterExpression box = Expression.Parameter(typeof(upgradeBox));
            return Expression.Lambda<Func<upgradeBox, T>>(Expression.Field(box, field), box).Compile();
        }

        static Action<upgradeBox, T> Setter<T>(FieldInfo field)
        {
            ParameterExpression box = Expression.Parameter(typeof(upgradeBox)), value = Expression.Parameter(typeof(T));
            return Expression.Lambda<Action<upgradeBox, T>>(Expression.Assign(Expression.Field(box, field), value), box, value).Compile();
        }
    }
}
