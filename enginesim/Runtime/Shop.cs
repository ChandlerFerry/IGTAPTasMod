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
    // earned, minus what it spent (a trip breaker zeroes it). Earned: with the start's clone state, the native Economy
    // (course rewards, clones, local upgrades); else nothing. Purchases whose effects aren't modeled end the run: offline
    // can't say what follows them. Modeled: movement dash, wall jump, double jump, swapBlocksOnce and unlockBlockSwap
    // (those two as the purchase's own bookkeeping and buy goal, then the run ends: the block swaps that follow aren't
    // modeled), every local upgrade (the Economy: prestige's BoostOldCourses and Course 5's trip breaker included,
    // activateNextBreakerLights) and the global cashPerLoop, maxCloneFastness, maxCloneBigness and unlockPrestige with
    // the native economy. Unmodeled: the other movement and global boxes and a prestige buy while Vman is active.
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
        // Every box; TurboRunner.Run counts down the ones active and enabled at each tick (the trip breaker toggles some).
        readonly upgradeBox[] _boxes;
        readonly Dictionary<upgradeBox, string> _paths = new Dictionary<upgradeBox, string>();
        // Full-TAS box ids (FullTasGoal.BoxId): hierarchy paths aren't unique.
        readonly Dictionary<upgradeBox, string> _ids = new Dictionary<upgradeBox, string>();
        readonly double[] _start = new double[CurrencyCount];
        double[] _spent = new double[CurrencyCount];
        // The native economy: null unless the start state carries the clones' state (LoadStart).
        internal Economy Economy { get; private set; }
        // The buy-max boxes' IE_BuyMax coroutines waiting for the frame's end, in start order.
        readonly List<BuyMaxRoutine> _buyMax = new List<BuyMaxRoutine>();

        // upgradeBox.buyUpgrade's IE_BuyMax: the purchases GetNumPlayerCouldBuy counted when it started, not made yet.
        internal struct BuyMaxRoutine
        {
            public upgradeBox Box;
            public int Currency, Left;
            public Collider2D Collision;
        }

        // What a run changed (SimSnapshot): spent per currency, and the native economy's state.
        internal sealed class State
        {
            public double[] Spent;
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
            _buyMax.Clear();
            Economy = economy.TryGetProperty("clones", out _) ? new Economy(_sim, economy) : null;
        }

        // upgradeBox.FixedUpdate, before the tick's physics (TurboRunner.Run's order).
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

        // TurboRunner.EndOfFrame, after StepClones: every waiting IE_BuyMax takes its next iteration (one frame per
        // tick). Unity stops a coroutine whose object went inactive.
        internal void EndOfFrame()
        {
            for (int i = 0; i < _buyMax.Count; i++)
            {
                BuyMaxRoutine r = _buyMax[i];
                if (r.Box.gameObject.activeInHierarchy && BuyMaxStep(ref r)) _buyMax[i] = r;
                else _buyMax.RemoveAt(i--);
            }
        }

        // courseScript.stopTracking(player, true) of a course that was timing, at course timer `time`.
        internal void CourseFinished(courseScript course, float time) { Economy?.CourseFinished(course, time); }

        // upgradeBox.OnTriggerStay2D (the physics world already touched the box).
        internal void Stay(object boxObject, Collider2D collision)
        {
            var box = (upgradeBox)boxObject;
            if (!(GetCooldown(box) <= 0f)) return;
            SetCooldown(box, 0.2f);
            int currency = (int)GetCurrency(box);
            if (!(Available(currency) >= box.upgradeCost && box.isActive)) return;
            // The game's buy draws (upgradeBox.buyUpgrade and the trigger's BoxHit sound), modeled or not: the tick's
            // Random state is the game's either way. The purchase first (DoBuyUpgrade, or IE_BuyMax's first iteration
            // inside StartCoroutine: a trip breaker draws in it), then a non-buy-max buy's Random.Range(4, 7) for the
            // buy's particles, a tree's first grow's growth sound (two draws), and BoxHit's clip and pitch (its
            // Audio.PlayAudio(BoxHit, 1f, 0.08f)).
            if (!(box.buyMax && !box.neverBuyMax))
            {
                Buy(box, currency, collision);
                UnityEngine.Random.Draw();
            }
            else
            {
                // IE_BuyMax: GetNumPlayerCouldBuy's count, its first iteration inside StartCoroutine, the rest at frames' ends.
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

        // upgradeBox.buyUpgrade's upgradeTree.growSegments(SegmentsToTrigger): a segment not grown yet grows (its
        // parts' colliders and the box it opens are not modeled) and the tree plays its growth sound once per 0.3 s
        // audio cooldown (an Invoke on the tree). Only the draws and the cooldown are kept here: the sound is the
        // only part of a grow a run's state can see.
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
                    // Audio.PlayAudio(treeGrowing, 1f, 0.05f): a clip draw and a pitch draw.
                    UnityEngine.Random.Draw();
                    UnityEngine.Random.Draw();
                    AudioCooldownField.SetValue(tree, true);
                    tree.Invoke("EndAudioCooldown", 0.3f);
                }
                tree.GrownSegments.Add(segment);
            }
        }

        // One IE_BuyMax loop iteration: stops when the count is used up or the box is no longer affordable (it doesn't
        // check isActive or the cap); else DoBuyUpgrade. Whether a later iteration is left.
        bool BuyMaxStep(ref BuyMaxRoutine r)
        {
            if (r.Left <= 0 || !(Available(r.Currency) >= r.Box.upgradeCost)) return false;
            r.Left--;
            _sim.Touch(r.Box);
            Buy(r.Box, r.Currency, r.Collision);
            return r.Left > 0;
        }

        // upgradeBox.GetNumPlayerCouldBuy's count, with this run's Cash.
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

        // upgradeBox.DoBuyUpgrade, as far as a run can observe it.
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
                // The global dictionary and prestige's BoostOldCourses live in the native economy.
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
                    || move == upgradeBox.movementUpgrades.doubleJump || move == upgradeBox.movementUpgrades.unlockBlockSwap);
                why = player == null ? " (no player at the box)" : " (movement upgrade " + move + ")";
            }
            else
            {
                // Every local upgrade, activateNextBreakerLights (a trip breaker's former movement box) included.
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
                // swapBlocksOnce: Movement.swapBlocks toggles the coloured blocks' colliders and boxesToActivate opens
                // the way on; none of that is modeled, so the purchase is its bookkeeping and its buy goal. It works
                // as a run's last goal (the buy goal completes the run); anything past it ends here.
                box.isActive = false;
                _sim.Bought(path, _ids[box]);
                if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled purchase: block swap (collider changes)");
                return;
            }
            if (Economy != null && kind != localUpgrades.localUpgradeSet.Movement && !(global ? Economy.BoughtGlobal(box) : Economy.Bought(box, kind))) return;
            if (kind == localUpgrades.localUpgradeSet.Movement)
            {
                switch (box.movementUpgrade)
                {
                    case upgradeBox.movementUpgrades.dash: player.dashUnlocked = true; player.maxAirDashes++; break;
                    case upgradeBox.movementUpgrades.wallJump: player.wallJumpUnlocked = true; break;
                    case upgradeBox.movementUpgrades.doubleJump: player.doubleJumpUnlocked = true; player.maxAirJumps++; break;
                    case upgradeBox.movementUpgrades.unlockBlockSwap: player.blockSwapUnlocked = true; break;
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
            else ScaleBoxCost(box);
            if (box.TimesUsed >= box.Cap) box.isActive = false;
            // TasPlugin.AfterBuyUpgrade: a Full-TAS buy goal.
            _sim.Bought(path, _ids[box]);
            // unlockBlockSwap (Course 5's SwapBlocksOnDash, after a trip breaker): from now on every dash (and a
            // respawn) swaps the coloured blocks through Movement.blockSwapper, collider toggles the game's own code would
            // make outside the snapshots' reach, so the purchase is the run's last modeled step: bookkeeping and its buy goal.
            if (kind == localUpgrades.localUpgradeSet.Movement && move == upgradeBox.movementUpgrades.unlockBlockSwap && _sim.Engine.IsRunning)
                _sim.Engine.Abort("Unmodeled purchase: block swap on dash");
        }

        // upgradeBox.ScaleBoxCost.
        internal static void ScaleBoxCost(upgradeBox box)
        {
            box.upgradeCost += box.upgradeAddFactor;
            box.upgradeCost = Math.Pow(box.upgradeCost, box.upgradePowerScaleFactor);
            box.upgradeCost = Math.Ceiling(box.upgradeCost * box.upgradeScaleFactor);
            box.upgradeCost = Math.Ceiling(box.upgradeCost);
        }

        // tripBreakerScript.TripBreaker's Cash = 0 (Economy zeroes what the run earned).
        internal void ZeroCash() { _spent[Cash] = _start[Cash]; }

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

        internal State Capture() { return new State { Spent = (double[])_spent.Clone(), Economy = Economy?.Capture(), BuyMax = _buyMax.ToArray() }; }

        internal void Restore(State s)
        {
            Array.Copy(s.Spent, _spent, CurrencyCount);
            if (Economy != null && s.Economy != null) Economy.Restore(s.Economy);
            _buyMax.Clear();
            _buyMax.AddRange(s.BuyMax);
        }

        internal void AddTo(ref HashCode h)
        {
            foreach (double s in _spent) h.Add(s);
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
