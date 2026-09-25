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
    // unlocks a movement box gives, the local upgrades (Economy), and Full-TAS buy goals. The rest a purchase does (the
    // box's UI, particles) is left out. Each box's buy cooldown counts down by the tick like upgradeBox.FixedUpdate in
    // TurboRunner.Run.
    //
    // Cash is per simulation (the game keeps it in a static every worker thread shares): the start's, plus what the run
    // earned, minus what it spent. Earned: with the start's clone state, the native Economy (course rewards, clones,
    // local upgrades); else nothing. Purchases whose effects aren't modeled (buy-max and prestige boxes, global boxes
    // other than the global reward one with the native economy; movement boxes other than dash, wall jump and double
    // jump) end the run: offline can't say what follows them.
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
        // Full-TAS box ids (FullTasGoal.BoxId): hierarchy paths aren't unique.
        readonly Dictionary<upgradeBox, string> _ids = new Dictionary<upgradeBox, string>();
        readonly double[] _start = new double[CurrencyCount];
        double[] _spent = new double[CurrencyCount];
        // The native economy: null unless the start state carries the clones' state (LoadStart).
        internal Economy Economy { get; private set; }

        // What a run changed (SimSnapshot): spent per currency, and the native economy's state.
        internal sealed class State
        {
            public double[] Spent;
            public Economy.State Economy;
        }

        internal Shop(Simulation sim, World world)
        {
            _sim = sim;
            upgradeBox[] boxes = world.All<upgradeBox>();
            _ticking = boxes.Where(b => b.gameObject.activeInHierarchy && b.enabled).ToArray();
            foreach (upgradeBox b in boxes)
            {
                _paths[b] = HierarchyPath(b.transform);
                _ids[b] = FullTasGoal.BoxId(_paths[b], world.SiblingIndex[b.transform]);
            }
            // The world export's Cash; a start state with its economy (LoadStart) replaces it.
            if (globalStats.currencyLookup != null)
                foreach (KeyValuePair<globalStats.Currencies, double> c in globalStats.currencyLookup)
                    if ((int)c.Key >= 0 && (int)c.Key < CurrencyCount) _start[(int)c.Key] = c.Value;
            // clonesScript.isOnScreen, and courseScript.UpdateReward / fullUpdate (hooks.txt): the economy's, when native.
            Hooks.ClonesOnScreen = (clones, onScreen) => Economy?.OnScreen((clonesScript)clones, onScreen);
            Hooks.CourseReward = course => Economy?.UpdateReward((courseScript)course);
        }

        // The start state's "economy" (TasPlugin's TurboRunner.Economy): every currency and every box's own state; with
        // "clones" (the clones' state, the upgrade dictionaries), the native Economy.
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
            Economy = economy.TryGetProperty("clones", out _) ? new Economy(_sim, economy) : null;
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
            return _start[currency] - _spent[currency] + (currency == Cash ? Economy?.Earned ?? 0.0 : 0.0);
        }

        // Cash after the run's latest tick, clone payouts included (a trace tick's "cash").
        public double CashNow
        {
            get { return _start[Cash] - _spent[Cash] + (Economy?.Earned ?? 0.0); }
        }

        // TurboRunner.StepClones for every course's clones, after Movement.Update.
        internal void StepClones() { Economy?.Step(_sim.World.FixedDeltaTime); }

        // courseScript.stopTracking(player, true) of a course that was timing, at course timer `time`.
        internal void CourseFinished(courseScript course, float time) { Economy?.CourseFinished(course, time); }

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
            bool global = kind == localUpgrades.localUpgradeSet.GLOBAL;
            bool modeled = !(box.buyMax && !box.neverBuyMax)
                && (!global || Economy != null && box.globalUpgrade == globalStats.globalUpgradeSet.cashPerLoop)
                && kind != localUpgrades.localUpgradeSet.prestige && kind != localUpgrades.localUpgradeSet.activateNextBreakerLights
                && (kind != localUpgrades.localUpgradeSet.Movement || player != null && (box.movementUpgrade == upgradeBox.movementUpgrades.dash
                    || box.movementUpgrade == upgradeBox.movementUpgrades.wallJump || box.movementUpgrade == upgradeBox.movementUpgrades.doubleJump));
            if (!modeled)
            {
                _sim.Engine.Abort("Unmodeled purchase: " + path + (!global || box.buyMax && !box.neverBuyMax ? ""
                    : Economy == null ? " (a global upgrade needs the native economy: this trace's start state has no clones)"
                    : box.globalUpgrade != globalStats.globalUpgradeSet.cashPerLoop ? " (global upgrade " + box.globalUpgrade + ")" : ""));
                return;
            }
            box.TimesUsed++;
            if (!VmanScript.isCurrentlyVman) _spent[currency] += box.upgradeCost;
            if (Economy != null && kind != localUpgrades.localUpgradeSet.Movement && !(global ? Economy.BoughtGlobal(box) : Economy.Bought(box, kind))) return;
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
            else if (global)
            {
                // DoBuyUpgrade's global branch rounds.
                box.upgradeCost += box.upgradeAddFactor;
                box.upgradeCost = Math.Pow(box.upgradeCost, box.upgradePowerScaleFactor);
                box.upgradeCost = Math.Round(box.upgradeCost * box.upgradeScaleFactor);
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
            // TasPlugin.AfterBuyUpgrade: a Full-TAS buy goal.
            _sim.Bought(path, _ids[box]);
        }

        // Whether some box has this id or hierarchy path (a Full-TAS goal's box).
        internal bool HasBox(string box) { return _ids.ContainsValue(box) || _paths.ContainsValue(box); }

        // The id of the upgrade box nearest the world point (x, y), closer than `range` (TasPlugin.FullTasRewardBox's rule); null: none.
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

        internal State Capture() { return new State { Spent = (double[])_spent.Clone(), Economy = Economy?.Capture() }; }

        internal void Restore(State s)
        {
            Array.Copy(s.Spent, _spent, CurrencyCount);
            if (Economy != null && s.Economy != null) Economy.Restore(s.Economy);
        }

        internal void AddTo(ref HashCode h)
        {
            foreach (double s in _spent) h.Add(s);
            Economy?.AddTo(ref h);
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
