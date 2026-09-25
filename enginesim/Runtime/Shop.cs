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
    // The upgrade boxes offline. hooks.txt makes upgradeBox.OnTriggerStay2D call Stay: the game's buy rule (cooldown up,
    // affordable, active) and what a purchase changes that a run observes: Cash, the box's cost, uses and cap, the
    // unlocks a movement box gives, and a purchase segment's finish. The rest a purchase does (the box's UI, particles,
    // the upgrade dictionaries that only feed rewards and clone rolls) is left out. Each box's buy cooldown counts down
    // by the tick like upgradeBox.FixedUpdate in TurboRunner.Run.
    //
    // Cash is per simulation (the game keeps it in a static every worker thread shares): the start's, plus what the
    // clones paid by then (the trace's income, recorded in turbo standing still), minus what this run spent. Purchases
    // whose effects aren't modeled (global, prestige and buy-max boxes; movement boxes other than dash, wall jump and
    // double jump) end the run: offline can't say what follows them.
    public sealed class Shop
    {
        const int CurrencyCount = 7;
        const int Cash = (int)globalStats.Currencies.Cash;
        static readonly FieldInfo CooldownField = typeof(upgradeBox).GetField("boxCooldown", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Func<upgradeBox, float> GetCooldown = Getter<float>(CooldownField);
        static readonly Action<upgradeBox, float> SetCooldown = Setter<float>(CooldownField);
        static readonly Func<upgradeBox, globalStats.Currencies> GetCurrency =
            Getter<globalStats.Currencies>(typeof(upgradeBox).GetField("upgradeCurrency", BindingFlags.Instance | BindingFlags.NonPublic));

        readonly Simulation _sim;
        // The boxes TurboRunner.Run counts down: on active objects, enabled.
        readonly upgradeBox[] _ticking;
        readonly Dictionary<upgradeBox, string> _paths = new Dictionary<upgradeBox, string>();
        readonly double[] _start = new double[CurrencyCount];
        double[] _spent = new double[CurrencyCount];
        // Cash the clones paid through physics tick k + 1 of the run (TasPlugin.PurchaseIncome).
        double[] _income = Array.Empty<double>();
        // A purchase segment's boxes (hierarchy paths, MacroEngine.RecordPurchase); null for any other target.
        string[] _targets;

        internal Shop(Simulation sim, World world)
        {
            _sim = sim;
            upgradeBox[] boxes = world.All<upgradeBox>();
            _ticking = boxes.Where(b => b.gameObject.activeInHierarchy && b.enabled).ToArray();
            foreach (upgradeBox b in boxes) _paths[b] = HierarchyPath(b.transform);
            // The world export's Cash; a start state with its economy (LoadStart) replaces it.
            if (globalStats.currencyLookup != null)
                foreach (KeyValuePair<globalStats.Currencies, double> c in globalStats.currencyLookup)
                    if ((int)c.Key >= 0 && (int)c.Key < CurrencyCount) _start[(int)c.Key] = c.Value;
        }

        public bool IsPurchase { get { return _targets != null; } }

        // The trace's "purchase": {boxes, income}.
        internal void SetTarget(JsonElement purchase)
        {
            if (!purchase.TryGetProperty("boxes", out JsonElement boxes))
                throw new InvalidOperationException("This purchase trace predates the offline buy rule: capture it again.");
            _targets = boxes.EnumerateArray().Select(b => b.GetString()).ToArray();
            foreach (string path in _targets)
                if (!_paths.ContainsValue(path)) throw new InvalidOperationException("Upgrade box " + path + " is not in this world export.");
            _income = purchase.GetProperty("income").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        }

        // The start state's "economy" (TasPlugin's TurboRunner.Economy): every currency and every box's own state.
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
        }

        // upgradeBox.FixedUpdate, before the tick's physics (TurboRunner.Run's order).
        internal void TickCooldowns(float dt)
        {
            foreach (upgradeBox box in _ticking)
            {
                float cooldown = GetCooldown(box);
                if (!(cooldown >= 0f)) continue;
                _sim.Touch(box);
                SetCooldown(box, cooldown - dt);
            }
        }

        // What the player can spend now: this tick's physics runs after the clones paid in the ticks before it.
        double Available(int currency)
        {
            return _start[currency] - _spent[currency] + (currency == Cash ? Paid(_sim.Engine.PhysicsTicks - 1) : 0.0);
        }

        // Cash after the run's latest tick, clone payouts included (a trace tick's "cash").
        public double CashNow { get { return _start[Cash] - _spent[Cash] + Paid(_sim.Engine.PhysicsTicks); } }

        // What the clones paid through physics tick `ticks` of the run.
        double Paid(long ticks)
        {
            return ticks <= 0 || _income.Length == 0 ? 0.0 : _income[(int)Math.Min(ticks, _income.Length) - 1];
        }

        // upgradeBox.OnTriggerStay2D (the physics world already touched the box).
        internal void Stay(object boxObject, Collider2D collision)
        {
            var box = (upgradeBox)boxObject;
            if (!(GetCooldown(box) <= 0f)) return;
            SetCooldown(box, 0.2f);
            int currency = (int)GetCurrency(box);
            if (Available(currency) >= box.upgradeCost && box.isActive) Buy(box, currency, collision);
        }

        // upgradeBox.buyUpgrade and DoBuyUpgrade, as far as a run can observe them.
        void Buy(upgradeBox box, int currency, Collider2D collision)
        {
            string path = _paths[box];
            localUpgrades.localUpgradeSet kind = box.upgrade;
            Movement player = kind == localUpgrades.localUpgradeSet.Movement ? collision.GetComponent<Movement>() : null;
            bool modeled = !(box.buyMax && !box.neverBuyMax) && kind != localUpgrades.localUpgradeSet.GLOBAL
                && kind != localUpgrades.localUpgradeSet.prestige && kind != localUpgrades.localUpgradeSet.activateNextBreakerLights
                && (kind != localUpgrades.localUpgradeSet.Movement || player != null && (box.movementUpgrade == upgradeBox.movementUpgrades.dash
                    || box.movementUpgrade == upgradeBox.movementUpgrades.wallJump || box.movementUpgrade == upgradeBox.movementUpgrades.doubleJump));
            if (!modeled)
            {
                _sim.Engine.Abort("Unmodeled purchase: " + path);
                return;
            }
            box.TimesUsed++;
            if (!VmanScript.isCurrentlyVman) _spent[currency] += box.upgradeCost;
            if (kind == localUpgrades.localUpgradeSet.Movement)
            {
                switch (box.movementUpgrade)
                {
                    case upgradeBox.movementUpgrades.dash: player.dashUnlocked = true; player.maxAirDashes++; break;
                    case upgradeBox.movementUpgrades.wallJump: player.wallJumpUnlocked = true; break;
                    case upgradeBox.movementUpgrades.doubleJump: player.doubleJumpUnlocked = true; player.maxAirJumps++; break;
                }
                box.isActive = false;
            }
            else
            {
                // ScaleBoxCost.
                box.upgradeCost += box.upgradeAddFactor;
                box.upgradeCost = Math.Pow(box.upgradeCost, box.upgradePowerScaleFactor);
                box.upgradeCost = Math.Ceiling(box.upgradeCost * box.upgradeScaleFactor);
                box.upgradeCost = Math.Ceiling(box.upgradeCost);
            }
            if (box.TimesUsed >= box.Cap) box.isActive = false;
            // TasPlugin.AfterBuyUpgrade.
            if (_targets != null && _sim.Engine.RecordPurchase(_targets, path))
                _sim.Engine.CompleteFromGame(MacroEngine.PurchaseFinishReason, _sim.Engine.PhysicsTicks * _sim.World.FixedDeltaTime);
        }

        internal double[] Capture() { return (double[])_spent.Clone(); }

        internal void Restore(double[] spent) { Array.Copy(spent, _spent, CurrencyCount); }

        internal void AddTo(ref HashCode h) { foreach (double s in _spent) h.Add(s); }

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
