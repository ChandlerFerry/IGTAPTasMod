using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed class Economy
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo BaseRewardField = typeof(courseScript).GetField("baseReward", Fields);
        static readonly FieldInfo BestPathTimeField = typeof(courseScript).GetField("bestPathTime", Fields);
        static readonly FieldInfo CourseClonesField = typeof(courseScript).GetField("clones", Fields);
        static readonly FieldInfo ClonesCourseField = typeof(clonesScript).GetField("course", Fields);
        static readonly FieldInfo ClonesUpgradesField = typeof(clonesScript).GetField("upgrades", Fields);
        static readonly FieldInfo BoxCourseField = typeof(upgradeBox).GetField("course", Fields);
        static readonly FieldInfo BoxClonesField = typeof(upgradeBox).GetField("clones", Fields);
        static readonly FieldInfo BoxEffectMultField = typeof(upgradeBox).GetField("effectMult", Fields);
        static readonly FieldInfo BoxOldCoursesField = typeof(upgradeBox).GetField("oldCourses", Fields);
        static readonly FieldInfo BoxTripBreakerField = typeof(upgradeBox).GetField("tripBreaker", Fields);
        static readonly FieldInfo BoxCoursesToResetField = typeof(upgradeBox).GetField("coursesToReset", Fields);
        static readonly FieldInfo BoxObjectReferenceField = typeof(upgradeBox).GetField("ObjectReference", Fields);
        static readonly FieldInfo BoxTripBreakerScriptField = typeof(upgradeBox).GetField("tripBreakerScript", Fields);
        static readonly FieldInfo BoxTierModField = typeof(upgradeBox).GetField("TierMod", Fields);
        static readonly FieldInfo BoxLocalUpgradeScriptField = typeof(upgradeBox).GetField("localUpgradeScript", Fields);
        static readonly FieldInfo BoxStatuePrestigeTextField = typeof(upgradeBox).GetField("statuePrestigeText", Fields);
        static readonly FieldInfo ChildBoxesField = typeof(localUpgrades).GetField("ChildBoxes", Fields);
        static readonly FieldInfo MovementBoxField = typeof(localUpgrades).GetField("MovementBox", Fields);
        static readonly FieldInfo BreakerLayerTierModField = typeof(courseScript).GetField("BreakerLayerTierMod", Fields);
        static readonly FieldInfo BreakerLayerRewardTierModField = typeof(courseScript).GetField("BreakerLayerRewardTierMod", Fields);
        static readonly FieldInfo BoxToDeactivateField = typeof(tripBreakerScript).GetField("boxToDeactivate", Fields);
        static readonly FieldInfo BoxToActivateField = typeof(tripBreakerScript).GetField("boxToActivate", Fields);
        static readonly FieldInfo AreaLightsField = typeof(tripBreakerScript).GetField("areaLights", Fields);
        static readonly FieldInfo ActiveLightsField = typeof(tripBreakerScript).GetField("activeLights", Fields);
        static readonly FieldInfo OrangeBlocksField = typeof(colouredBlockSwapper).GetField("orange", Fields);
        static readonly FieldInfo BlueBlocksField = typeof(colouredBlockSwapper).GetField("blue", Fields);
        static readonly int LocalCount = Enum.GetValues(typeof(localUpgrades.localUpgradeSet)).Cast<int>().Max() + 1;
        static readonly int GlobalCount = Enum.GetValues(typeof(globalStats.globalUpgradeSet)).Cast<int>().Max() + 1;

        internal struct Clone
        {
            public float Pointer;
            public int Fastness, Bigness;
            public bool Coloured;
        }

        internal struct CloneValues
        {
            public int Count, Pooled, PathSamples;
            public bool ArrayFull, OnScreen;
            public float BaseInterval, SinceLast, NextDue, OffscreenDivisor, PathLength;
            public double Red, Green, Blue;
        }

        sealed class Clones
        {
            public clonesScript Script;
            public courseScript Course;
            public int Local;
            public CloneValues V;
            public readonly List<Clone> Active = new List<Clone>();
        }

        internal sealed class State
        {
            public double Earned;
            public CloneValues[] Values;
            public Clone[][] Active;
            public double[][] Locals;
            public int LocalsHash;
            public double[] Global;
            public bool TripBroken;
        }

        readonly Simulation _sim;
        readonly Clones[] _clones;
        readonly Dictionary<clonesScript, Clones> _byScript = new Dictionary<clonesScript, Clones>();
        readonly Clones[] _stepped;
        readonly Dictionary<localUpgrades, int> _localIndex = new Dictionary<localUpgrades, int>();
        readonly double[][] _locals;
        int _localsHash;
        double[] _global = new double[GlobalCount];
        readonly double _gameSpeed, _vmanMult;
        bool _tripBroken;

        public double Earned { get; private set; }

        internal Economy(Simulation sim, JsonElement economy)
        {
            _sim = sim;
            World world = sim.World;
            if (economy.TryGetProperty("globalUpgrades", out JsonElement global))
                SetGlobal((Dictionary<globalStats.globalUpgradeSet, double>)world.Read(global, typeof(Dictionary<globalStats.globalUpgradeSet, double>)));
            else SetGlobal(globalStats.globalUpgradeDict);
            globalStats stats = Singleton<globalStats>.Instance;
            _gameSpeed = economy.TryGetProperty("gameSpeed", out JsonElement speed) ? speed.GetDouble() : (object)stats == null ? 1.0 : stats.GameSpeed;
            _vmanMult = economy.TryGetProperty("vmanMult", out JsonElement vman) ? vman.GetDouble() : (object)stats == null ? 1.0 : stats.vmanMult;
            globalStats own = world.All<globalStats>().FirstOrDefault();
            _tripBroken = (object)own != null && own.currentA1State == globalStats.area1states.tripBreaker;

            localUpgrades[] locals = world.All<localUpgrades>();
            _locals = new double[locals.Length][];
            var exported = new Dictionary<int, JsonElement>();
            if (economy.TryGetProperty("locals", out JsonElement localsJson))
                foreach (JsonElement l in localsJson.EnumerateArray()) exported[l.GetProperty("id").GetInt32()] = l.GetProperty("upgrades");
            for (int i = 0; i < locals.Length; i++)
            {
                _localIndex[locals[i]] = i;
                _locals[i] = new double[LocalCount];
                Dictionary<localUpgrades.localUpgradeSet, double> dict = exported.TryGetValue(locals[i].GetInstanceID(), out JsonElement upgrades)
                    ? (Dictionary<localUpgrades.localUpgradeSet, double>)world.Read(upgrades, typeof(Dictionary<localUpgrades.localUpgradeSet, double>))
                    : locals[i].localUpgradeDict;
                if (dict != null)
                    foreach (KeyValuePair<localUpgrades.localUpgradeSet, double> e in dict)
                        if ((int)e.Key >= 0 && (int)e.Key < LocalCount) _locals[i][(int)e.Key] = e.Value;
            }
            _localsHash = HashLocals();

            // 1x's Update order (instance id, lowest first), as turbo steps them (TurboRunner.Run): the order decides which course
            // gets which fast/big rolls when two spawn in one frame.
            clonesScript[] scripts = world.All<clonesScript>().OrderBy(s => s.GetInstanceID()).ToArray();
            var clonesJson = new Dictionary<int, JsonElement>();
            foreach (JsonElement c in economy.GetProperty("clones").EnumerateArray()) clonesJson[c.GetProperty("id").GetInt32()] = c;
            _clones = new Clones[scripts.Length];
            for (int i = 0; i < scripts.Length; i++)
            {
                clonesScript script = scripts[i];
                var clones = new Clones { Script = script, Course = ClonesCourseField.GetValue(script) as courseScript,
                    Local = LocalIndex(ClonesUpgradesField.GetValue(script) as localUpgrades) };
                if (clonesJson.TryGetValue(script.GetInstanceID(), out JsonElement c)) LoadClones(clones, c);
                else LoadClones(clones, script);
                _clones[i] = clones;
                _byScript[script] = clones;
            }
            _stepped = _clones.Where(c => c.Script.gameObject.activeInHierarchy).ToArray();
        }

        void SetGlobal(Dictionary<globalStats.globalUpgradeSet, double> dict)
        {
            if (dict == null) return;
            foreach (KeyValuePair<globalStats.globalUpgradeSet, double> e in dict)
                if ((int)e.Key >= 0 && (int)e.Key < GlobalCount) _global[(int)e.Key] = e.Value;
        }

        /// <summary>MacroEngine.RefreshRewards: an active course with local upgrades updates its reward (TasPlugin's the same set).</summary>
        internal void RefreshReward(courseScript course)
        {
            if (course.gameObject.activeInHierarchy && LocalIndex(course.localUpgradesScript) >= 0) UpdateReward(course);
        }

        int LocalIndex(localUpgrades script)
        {
            return (object)script != null && _localIndex.TryGetValue(script, out int i) ? i : -1;
        }

        void LoadClones(Clones clones, JsonElement c)
        {
            var values = new Dictionary<string, JsonElement>();
            foreach (JsonElement entry in c.GetProperty("fields").EnumerateArray()) values[entry[1].GetString()] = entry[2];
            int path = c.GetProperty("path").GetInt32();
            LoadClones(clones, name =>
            {
                FieldInfo f = typeof(clonesScript).GetField(name, Fields);
                return values.TryGetValue(name, out JsonElement v) ? _sim.World.Read(v, f.FieldType) : f.GetValue(clones.Script);
            }, path);
        }

        void LoadClones(Clones clones, clonesScript script)
        {
            LoadClones(clones, name => typeof(clonesScript).GetField(name, Fields).GetValue(script), script.clonePath?.Length ?? 0);
        }

        static void LoadClones(Clones clones, Func<string, object> field, int pathSamples)
        {
            var pointers = field("clonePathPointers") as List<float> ?? new List<float>();
            var fastness = field("cloneFastness") as List<int> ?? new List<int>();
            var bigness = field("cloneBigness") as List<int> ?? new List<int>();
            var red = field("cloneIsRed") as List<bool> ?? new List<bool>();
            var green = field("cloneIsGreen") as List<bool> ?? new List<bool>();
            var blue = field("cloneIsBlue") as List<bool> ?? new List<bool>();
            var colour = field("ColourCloneCounter") as double[] ?? new[] { 50.0, 50.0, 50.0 };
            clones.V = new CloneValues
            {
                Count = (int)field("cloneCount"),
                Pooled = (field("inactiveClones") as System.Collections.ICollection)?.Count ?? 0,
                PathSamples = pathSamples,
                ArrayFull = (bool)field("cloneArrayFull"),
                OnScreen = (bool)field("onScreen"),
                BaseInterval = (float)field("BaseCloneInterval"),
                SinceLast = (float)field("timeSinceLastClone"),
                NextDue = (float)field("nextCloneDue"),
                OffscreenDivisor = (float)field("nextOffscreenCloneDivisor"),
                PathLength = (float)field("pathLength"),
                Red = colour[0], Green = colour[1], Blue = colour[2],
            };
            clones.Active.Clear();
            for (int i = 0; i < pointers.Count; i++)
                clones.Active.Add(new Clone { Pointer = pointers[i], Fastness = i < fastness.Count ? fastness[i] : 0, Bigness = i < bigness.Count ? bigness[i] : 0,
                    Coloured = i < red.Count && red[i] || i < green.Count && green[i] || i < blue.Count && blue[i] });
        }

        bool Unmodeled(string what)
        {
            if (_sim.Engine.IsRunning) _sim.Engine.Abort("Unmodeled economy: " + what);
            return false;
        }

        double Local(int local, localUpgrades.localUpgradeSet upgrade) { return _locals[local][(int)upgrade]; }

        double Global(globalStats.globalUpgradeSet upgrade) { return _global[(int)upgrade]; }

        // Reads for zip-plan's start state: a course's upgrades, the global upgrades and a course's clone count and path length as
        // the run holds them now (the objects keep their loaded values).
        internal double LocalOf(localUpgrades script, localUpgrades.localUpgradeSet upgrade)
        {
            int i = LocalIndex(script);
            return i < 0 ? 0.0 : _locals[i][(int)upgrade];
        }

        internal double GlobalOf(globalStats.globalUpgradeSet upgrade) { return Global(upgrade); }

        internal (int count, float pathLength)? ClonesOf(clonesScript script)
        {
            return (object)script != null && _byScript.TryGetValue(script, out Clones c) ? (c.V.Count, c.V.PathLength) : null;
        }

        bool Effect(Dictionary<globalStats.globalUpgradeSet, double> dict, globalStats.globalUpgradeSet upgrade, out double value)
        {
            if (dict != null && dict.TryGetValue(upgrade, out value)) return true;
            value = 0.0;
            return Unmodeled("no effect value for " + upgrade);
        }

        internal void Step(float dt)
        {
            foreach (Clones c in _stepped)
            {
                if (!c.Script.isActiveAndEnabled) continue;
                c.V.SinceLast += dt;
                if (c.V.SinceLast >= c.V.NextDue && (double)c.V.PathLength > 0.1 && !SpawnClone(c)) return;
                if (!c.V.OnScreen) continue;
                List<int> done = null;
                for (int i = 0; i < c.Active.Count; i++)
                {
                    Clone clone = c.Active[i];
                    if (Mathf.FloorToInt(clone.Pointer) >= c.V.PathSamples) { (done ?? (done = new List<int>())).Add(i); continue; }
                    clone.Pointer = (float)(clone.Pointer + (double)PathIncrement(clone) * (50.0 * dt));
                    c.Active[i] = clone;
                }
                if (done != null)
                    for (int k = done.Count - 1; k >= 0; k--)
                        if (!DestroyClone(c, done[k])) return;
            }
        }

        static float PathIncrement(Clone clone)
        {
            return (float)((1.0 + clone.Fastness * (double)0.35f) * Mathf.Pow(0.75f, clone.Bigness));
        }

        static int RollCount(double chance, double cap)
        {
            int i = 0;
            while ((double)i < 1.0 + cap)
            {
                double roll = UnityEngine.Random.Range(0, 101);
                if (!(roll < chance * (1.0 / (1.0 + i))) || !(roll < 50.0 + cap * 5.0)) break;
                i++;
            }
            return i;
        }

        /// <summary>A diagnostic (--check --ticks-out): each clone spawn as it is rolled, (course, instance id, on screen, divisor
        /// before, fastness i, bigness j, payout or 0 on screen), to line up with a 1x probe's clone rows.</summary>
        public Action<int, int, bool, float, int, int, double> Spawned;

        bool SpawnClone(Clones c)
        {
            float divisorBefore = c.V.OffscreenDivisor;
            if (c.Local < 0) return Unmodeled("clones without local upgrades");
            double chance = Local(c.Local, localUpgrades.localUpgradeSet.fastCloneChance) * 5.0
                + Global(globalStats.globalUpgradeSet.fastCloneChance) * 8.0;
            int i = RollCount(chance, Global(globalStats.globalUpgradeSet.maxCloneFastness));
            chance = Local(c.Local, localUpgrades.localUpgradeSet.bigCloneChance) * 5.0
                + Global(globalStats.globalUpgradeSet.bigCloneChance) * 8.0;
            int j = RollCount(chance, Global(globalStats.globalUpgradeSet.maxCloneBigness));
            c.V.NextDue = (float)(c.V.BaseInterval / (1.0 + i * (double)0.35f));
            c.V.NextDue = (float)((double)c.V.NextDue / Mathf.Pow(0.75f, j));
            if (c.V.OnScreen)
            {
                Spawned?.Invoke(c.Course?.courseNumber ?? -1, c.Script.GetInstanceID(), true, divisorBefore, i, j, 0.0);
                if (c.V.Pooled > 0 && c.Active.Count < c.V.Count)
                {
                    c.V.OffscreenDivisor = 1f;
                    c.V.Pooled--;
                    c.Active.Add(new Clone { Pointer = 0f, Fastness = i, Bigness = j });
                    c.V.SinceLast = 0f;
                    if (!ColourCheck(c)) return false;
                }
                return true;
            }
            if (c.V.Count <= 0) return true;
            if ((object)c.Course == null) return Unmodeled("clones without a course");
            double pay = Math.Ceiling(Pay(c, j) / (double)c.V.OffscreenDivisor);
            Earned += pay;
            Spawned?.Invoke(c.Course.courseNumber, c.Script.GetInstanceID(), false, divisorBefore, i, j, pay);
            if (!ColourCheck(c)) return false;
            if (Local(c.Local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0) return Unmodeled("clone dust");
            c.V.SinceLast = 0f;
            c.V.OffscreenDivisor = c.V.NextDue;
            c.V.NextDue = 1f;
            return true;
        }

        /// <summary>A clone's payout before the offscreen divisor: the course reward × (2 bigness + 1) × the clone multiplier × the
        /// competition boost, in the game's order.</summary>
        double Pay(Clones c, int bigness)
        {
            double boost = 1.0;
            if (c.Course.compBoostTimeLeft > 0.0) boost = c.Course.compBoostBonus;
            double pay = c.Course.reward * (double)(bigness * 2 + 1);
            double mult = 0.1 + Global(globalStats.globalUpgradeSet.cloneMult) * 0.2 + Local(c.Local, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
            pay *= mult;
            return pay * boost;
        }

        bool ColourCheck(Clones c)
        {
            c.V.Green += Global(globalStats.globalUpgradeSet.greenCloneClance);
            if (c.V.Green > 100.0 || c.V.Red > 100.0) return Unmodeled("colour clone");
            return true;
        }

        bool DestroyClone(Clones c, int index)
        {
            Clone clone = c.Active[index];
            if (clone.Coloured) return Unmodeled("colour clone");
            if ((object)c.Course == null || c.Local < 0) return Unmodeled("clones without a course");
            Earned += Math.Ceiling(Pay(c, clone.Bigness));
            if (Local(c.Local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0) return Unmodeled("clone dust");
            c.V.Pooled++;
            c.Active.RemoveAt(index);
            return true;
        }

        static void UpdateCloneInterval(Clones c)
        {
            c.V.BaseInterval = c.V.PathLength / (float)c.V.Count;
            c.V.NextDue = c.V.BaseInterval;
        }

        internal void OnScreen(clonesScript script, bool onScreen)
        {
            if (!_byScript.TryGetValue(script, out Clones c) || onScreen == c.V.OnScreen) return;
            if ((object)c.Course == null) { Unmodeled("clones without a course"); return; }
            if (!UpdateReward(c.Course)) return;
            c.V.OnScreen = onScreen;
        }

        internal bool UpdateReward(courseScript course)
        {
            int local = LocalIndex(course.localUpgradesScript);
            if (local < 0) return Unmodeled("course " + course.courseNumber + " without local upgrades");
            Dictionary<globalStats.globalUpgradeSet, double> effect = globalStats.globalUpgradeEffectDict, baseEffect = globalStats.globalUpgradeBaseEffectDict;
            if (!Effect(effect, globalStats.globalUpgradeSet.increasedWatts, out double increasedWatts)
                || !Effect(effect, globalStats.globalUpgradeSet.tripleThreatIncrease, out double tripleThreat)
                || !Effect(effect, globalStats.globalUpgradeSet.cashPerLoop, out double cashPerLoop)
                || !Effect(effect, globalStats.globalUpgradeSet.moreWatts, out double moreWatts)
                || !Effect(effect, globalStats.globalUpgradeSet.wattPower, out double wattPower)
                || !Effect(baseEffect, globalStats.globalUpgradeSet.compBoostStrength, out double strengthBase)
                || !Effect(effect, globalStats.globalUpgradeSet.compBoostStrength, out double strength))
                return false;
            double reward = Math.Ceiling((double)(int)BaseRewardField.GetValue(course)
                * (1.0 + Global(globalStats.globalUpgradeSet.increasedWatts) * (increasedWatts / 100.0) + Global(globalStats.globalUpgradeSet.tripleThreatIncrease) * (tripleThreat / 100.0))
                * (1.0 + Global(globalStats.globalUpgradeSet.cashPerLoop) * (cashPerLoop / 100.0))
                * (1.0 + Local(local, localUpgrades.localUpgradeSet.cashPerLoop) * (moreWatts / 100.0))
                * (1.0 + Local(local, localUpgrades.localUpgradeSet.moreWatts))
                * (1.0 + Global(globalStats.globalUpgradeSet.moreWatts) * (moreWatts / 100.0))
                * _gameSpeed * _vmanMult * Math.Pow(10.0, course.rewardTier));
            course.reward = Math.Ceiling(Math.Pow(reward, 1.0 + Global(globalStats.globalUpgradeSet.wattPower) * wattPower));
            course.compBoostBonus = strengthBase + strength * Global(globalStats.globalUpgradeSet.compBoostStrength);
            return true;
        }

        bool TryActivateCompBoost(courseScript course)
        {
            if (Global(globalStats.globalUpgradeSet.compBoostUnlocked) == 0.0) return true;
            Dictionary<globalStats.globalUpgradeSet, double> effect = globalStats.globalUpgradeEffectDict, baseEffect = globalStats.globalUpgradeBaseEffectDict;
            if (!Effect(baseEffect, globalStats.globalUpgradeSet.compBoostTime, out double timeBase)
                || !Effect(effect, globalStats.globalUpgradeSet.compBoostTime, out double time)
                || !Effect(baseEffect, globalStats.globalUpgradeSet.compBoostStrength, out double strengthBase)
                || !Effect(effect, globalStats.globalUpgradeSet.compBoostStrength, out double strength))
                return false;
            course.compBoostTimeLeft = timeBase + time * Global(globalStats.globalUpgradeSet.compBoostTime);
            course.compBoostTimeLeft += (double)0f;
            course.compBoostBonus = strengthBase + strength * Global(globalStats.globalUpgradeSet.compBoostStrength);
            return true;
        }

        internal void CourseFinished(courseScript course, float time)
        {
            double reward = course.reward;
            if (course.compBoostTimeLeft > 0.0) reward *= course.compBoostBonus;
            Earned += Math.Ceiling(reward);
            int local = LocalIndex(course.localUpgradesScript);
            if (local < 0) { Unmodeled("course " + course.courseNumber + " without local upgrades"); return; }
            if (Local(local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0) { Unmodeled("clone dust"); return; }
            if (!TryActivateCompBoost(course)) return;
            var script = CourseClonesField.GetValue(course) as clonesScript;
            if ((object)script == null || !_byScript.TryGetValue(script, out Clones c)) { Unmodeled("course " + course.courseNumber + " without clones"); return; }
            if (time < (float)BestPathTimeField.GetValue(course))
            {
                int samples = PathSamples(time);
                if (samples < 0) { Unmodeled("course timer " + time + " is not a sum of physics ticks"); return; }
                c.V.PathLength = time;
                c.V.BaseInterval = c.V.PathLength / (float)c.V.Count;
                c.V.PathSamples = samples;
                for (int i = c.Active.Count - 1; i >= 0; i--)
                    if (!DestroyClone(c, i)) return;
                if (samples < 10) { Unmodeled("a best path shorter than 10 samples"); return; }
                BestPathTimeField.SetValue(course, time);
            }
            UpdateCloneInterval(c);
        }

        static int PathSamples(float time)
        {
            float t = 0f;
            int n = 0;
            while (t < time) { t += 0.02f; n++; }
            return t == time ? n : -1;
        }

        internal bool Bought(upgradeBox box, localUpgrades.localUpgradeSet upgrade)
        {
            int local = LocalIndex(box.GetComponentInParent<localUpgrades>());
            if (local < 0) return Unmodeled("box without local upgrades");
            double effectMult = (double)BoxEffectMultField.GetValue(box);
            _locals[local] = (double[])_locals[local].Clone();
            _locals[local][(int)upgrade] += 1.0 * effectMult;
            _localsHash = HashLocals();
            var course = BoxCourseField.GetValue(box) as courseScript;
            switch (upgrade)
            {
                case localUpgrades.localUpgradeSet.cashPerLoop:
                    if ((object)course == null) return Unmodeled("box without a course");
                    return UpdateReward(course);
                case localUpgrades.localUpgradeSet.cloneCount:
                {
                    var script = BoxClonesField.GetValue(box) as clonesScript;
                    if ((object)script == null || !_byScript.TryGetValue(script, out Clones c)) return Unmodeled("clone box without clones");
                    for (int j = 0; (double)j < effectMult; j++)
                    {
                        c.V.Count++;
                        if (c.V.ArrayFull) continue;
                        c.V.Pooled++;
                        UpdateCloneInterval(c);
                    }
                    if ((object)course == null) return Unmodeled("box without a course");
                    return UpdateReward(course);
                }
                case localUpgrades.localUpgradeSet.prestige:
                    return BoostOldCourses(box);
                case localUpgrades.localUpgradeSet.activateNextBreakerLights:
                {
                    var breaker = BoxTripBreakerScriptField.GetValue(box) as tripBreakerScript;
                    if ((object)breaker == null || !_tripBroken) return true;
                    if ((object)course == null) return Unmodeled("breaker lights box without a course (the game throws)");
                    int n = course.courseNumber;
                    if (n < 1 || n > breaker.fixedAreas.Length) return Unmodeled("breaker lights for course " + n + " (the game throws)");
                    return breaker.fixedAreas[n - 1] || FixedArea(breaker, n);
                }
            }
            return true;
        }

        bool FixedArea(tripBreakerScript breaker, int courseNumber)
        {
            var areas = AreaLightsField.GetValue(breaker) as GameObject[];
            if (areas == null || courseNumber > areas.Length) return true;
            GameObject area = areas[courseNumber - 1], next = courseNumber < areas.Length ? areas[courseNumber] : null;
            if ((object)area == null || (object)next == null) return Unmodeled("breaker lights for course " + courseNumber + " (the game throws)");
            _sim.Touch(breaker);
            if (area.transform.childCount > 0) breaker.fixedAreas[courseNumber - 1] = true;
            ((bool[])ActiveLightsField.GetValue(breaker))[courseNumber] = true;
            return true;
        }

        internal bool BoughtGlobal(upgradeBox box)
        {
            globalStats.globalUpgradeSet upgrade = box.globalUpgrade;
            if (upgrade != globalStats.globalUpgradeSet.cashPerLoop && upgrade != globalStats.globalUpgradeSet.maxCloneFastness
                && upgrade != globalStats.globalUpgradeSet.maxCloneBigness && upgrade != globalStats.globalUpgradeSet.unlockPrestige)
                return Unmodeled("global upgrade " + upgrade);
            _global = (double[])_global.Clone();
            double effectMult = (double)BoxEffectMultField.GetValue(box);
            if (upgrade == globalStats.globalUpgradeSet.unlockPrestige)
            {
                _global[(int)globalStats.globalUpgradeSet.cloneMult] += 1.0 * effectMult;
                var text = BoxStatuePrestigeTextField.GetValue(box) as GameObject;
                if ((object)text == null) return Unmodeled("unlock prestige box without its statue text (the game throws)");
                Activate(text);
                return BoostOldCourses(box);
            }
            _global[(int)upgrade] += 1.0 * effectMult;
            if (upgrade != globalStats.globalUpgradeSet.cashPerLoop) return true;
            var course = BoxCourseField.GetValue(box) as courseScript;
            return (object)course == null || UpdateReward(course);
        }

        bool BoostOldCourses(upgradeBox box)
        {
            var course = BoxCourseField.GetValue(box) as courseScript;
            if ((object)course == null) return Unmodeled("prestige box without a course");
            courseScript[] oldCourses = BoxOldCoursesField.GetValue(box) as courseScript[];
            if (oldCourses == null) return Unmodeled("prestige box without old courses");
            upgradeBox[] boxes = _sim.World.All<upgradeBox>();
            foreach (courseScript old in oldCourses)
            {
                var root = (object)old.localUpgradesScript == null ? null : old.localUpgradesScript.transform;
                if ((object)root == null) return Unmodeled("an old course without local upgrades");
                old.rewardTier = course.tier + old.RewardPrestigeMod;
                if (!UpdateReward(old)) return false;
                foreach (upgradeBox boosted in boxes)
                {
                    if (!boosted.gameObject.activeInHierarchy || !Under(boosted.transform, root)) continue;
                    if (boosted.upgrade == localUpgrades.localUpgradeSet.GLOBAL || boosted.upgrade == localUpgrades.localUpgradeSet.Movement
                        || boosted.upgrade == localUpgrades.localUpgradeSet.prestige) continue;
                    _sim.Touch(boosted);
                    boosted.baseCapMult += 0.5;
                    boosted.Cap = (int)Math.Round(boosted.baseCap * boosted.baseCapMult);
                    boosted.buyMax = true;
                    if (!boosted.isActive) boosted.isActive = true;
                }
            }
            _sim.Touch(box);
            box.isActive = false;
            return !(bool)BoxTripBreakerField.GetValue(box) || TripBreakerPrestige(box, course);
        }

        bool TripBreakerPrestige(upgradeBox box, courseScript course)
        {
            var reset = BoxCoursesToResetField.GetValue(box) as courseScript[];
            var reference = BoxObjectReferenceField.GetValue(box) as GameObject;
            var breaker = BoxTripBreakerScriptField.GetValue(box) as tripBreakerScript;
            upgradeBox referenceBox = (object)reference == null ? null : reference.GetComponent<upgradeBox>();
            if (reset == null || (object)referenceBox == null || (object)breaker == null)
                return Unmodeled("a trip breaker prestige without coursesToReset, ObjectReference box or tripBreakerScript (the game throws)");
            foreach (courseScript reset1 in reset)
            {
                var local = reset1.GetComponentInChildren<localUpgrades>();
                if ((object)local == null) return Unmodeled("course " + reset1.courseNumber + " without local upgrades (the game throws)");
                bool four = reset1.courseNumber == 4;
                bool ok = reset1.courseNumber == 5
                    ? LocalPrestige(local, true, course.tier, course.rewardTier, false)
                    : LocalPrestige(local, four, course.tier + reset1.tier, course.tier + reset1.tier, !four);
                if (!ok) return false;
                referenceBox.enabled = false;
            }
            return TripBreaker(breaker);
        }

        bool LocalPrestige(localUpgrades script, bool resetCloneTime, double newTier, double newRewardTier, bool breakerPrestige)
        {
            int local = LocalIndex(script);
            if (local < 0) return Unmodeled("local upgrades the economy doesn't know");
            _locals[local] = new double[LocalCount];
            _localsHash = HashLocals();
            Transform parent = script.transform.parent;
            var clones = parent.GetComponentInChildren<clonesScript>();
            var course = parent.GetComponentInChildren<courseScript>();
            if ((object)clones == null || (object)course == null || !_byScript.TryGetValue(clones, out Clones c))
                return Unmodeled("a course reset without its clones or course (the game throws)");
            for (int i = c.Active.Count - 1; i >= 0; i--)
                if (!DestroyClone(c, i)) return false;
            c.V.Count = 0;
            if (resetCloneTime)
            {
                c.Active.Clear();
                c.V.PathLength = 1E+16f;
            }
            UpdateCloneInterval(c);
            if (breakerPrestige)
            {
                double tierMod = (double)BreakerLayerTierModField.GetValue(course);
                course.tier = newTier + tierMod;
                course.rewardTier = newRewardTier + tierMod + (double)BreakerLayerRewardTierModField.GetValue(course);
            }
            else
            {
                course.tier = newTier - 1.0 + course.RewardPrestigeMod;
                course.rewardTier = newRewardTier - 1.0 + course.RewardPrestigeMod;
            }
            if (resetCloneTime) BestPathTimeField.SetValue(course, 1E+11f);
            if (!UpdateReward(course)) return false;
            if (ChildBoxesField.GetValue(script) is not List<upgradeBox> children) return Unmodeled("local upgrades without child boxes");
            foreach (upgradeBox child in children)
                if (!BoxPrestige(child)) return false;
            if (!breakerPrestige) return true;
            var movement = MovementBoxField.GetValue(script) as upgradeBox;
            if ((object)movement == null) return Unmodeled("a breaker prestige without a movement box (the game throws)");
            _sim.Touch(movement);
            movement.isActive = true;
            movement.TimesUsed = 0;
            float mult = movement.movementUpgrade == upgradeBox.movementUpgrades.wallJump ? 10f : 1f;
            movement.upgrade = localUpgrades.localUpgradeSet.activateNextBreakerLights;
            movement.modifiedLocalUpgrade = localUpgrades.localUpgradeSet.activateNextBreakerLights;
            if (!BoxPrestige(movement)) return false;
            movement.upgradeCost *= mult;
            return true;
        }

        bool BoxPrestige(upgradeBox box)
        {
            if ((object)box == null) return Unmodeled("a null child box (the game throws)");
            if (box.upgrade == localUpgrades.localUpgradeSet.prestige || box.upgrade == localUpgrades.localUpgradeSet.GLOBAL
                || box.upgrade == localUpgrades.localUpgradeSet.Movement)
                return true;
            var own = BoxLocalUpgradeScriptField.GetValue(box) as localUpgrades;
            if (box.ExemptFromPrestige && (object)own != null)
            {
                int local = LocalIndex(own);
                if (local < 0) return Unmodeled("local upgrades the economy doesn't know");
                _locals[local] = (double[])_locals[local].Clone();
                _locals[local][(int)box.upgrade] += (double)box.TimesUsed * (double)BoxEffectMultField.GetValue(box);
                _localsHash = HashLocals();
                return true;
            }
            _sim.Touch(box);
            box.TimesUsed = 0;
            box.upgradeCost = box.baseUpgradeCost;
            var course = BoxCourseField.GetValue(box) as courseScript;
            if ((object)course != null) box.upgradeCost *= Math.Pow(10.0, course.tier + (double)BoxTierModField.GetValue(box));
            if (!box.isActive) box.isActive = true;
            return true;
        }

        bool TripBreaker(tripBreakerScript breaker)
        {
            colouredBlockSwapper swapper = _sim.World.All<colouredBlockSwapper>().FirstOrDefault();
            var off = BoxToDeactivateField.GetValue(breaker) as upgradeBox;
            var on = BoxToActivateField.GetValue(breaker) as upgradeBox;
            if ((object)swapper == null || (object)off == null || (object)on == null)
                return Unmodeled("a trip breaker without its block swapper or boxes (the game throws)");
            _sim.Touch(swapper);
            swapper.isBlueActive = false;
            foreach (var (blocks, enabled) in new[] { (OrangeBlocksField, true), (BlueBlocksField, false) })
                foreach (GameObject o in blocks.GetValue(swapper) as GameObject[] ?? Array.Empty<GameObject>())
                {
                    var collider = (object)o == null ? null : o.GetComponent<UnityEngine.Tilemaps.TilemapCollider2D>();
                    if ((object)collider == null) return Unmodeled("coloured blocks without a tilemap collider (the game throws)");
                    collider.enabled = enabled;
                }
            _sim.Touch(off);
            off.visible = false;
            off.gameObject.SetActive(false);
            off.isActive = false;
            _sim.Touch(on);
            on.visible = true;
            Activate(on.gameObject);
            on.isActive = true;
            on.upgradeCost = on.baseUpgradeCost;
            var onCourse = BoxCourseField.GetValue(on) as courseScript;
            if ((object)onCourse != null) on.upgradeCost *= Math.Pow(10.0, onCourse.tier + (double)BoxTierModField.GetValue(on));
            for (int i = 0; i < on.TimesUsed; i++) Shop.ScaleBoxCost(on);
            UnityEngine.Random.Draw();
            _tripBroken = true;
            if (!VmanScript.isCurrentlyVman)
            {
                Earned = 0.0;
                _sim.Shop.ZeroCash();
            }
            _sim.Touch(breaker);
            ((bool[])ActiveLightsField.GetValue(breaker))[0] = true;
            return true;
        }

        static bool Under(Transform t, Transform root)
        {
            for (Transform p = t; p != null; p = p.parent) if (p == root) return true;
            return false;
        }

        /// <summary>
        /// SetActive(true) as the game runs it: the OnEnable of every behaviour it wakes runs at once, and two of them draw
        /// UnityEngine.Random: upgradeBox.OnEnable twice (the multibuy text's repeat rate, customBoxUpdate's first delay),
        /// ObjectBobber.OnEnable once, except in a Full-TAS run, where 1x and turbo give a bobber's draw back (README exception
        /// 11: a buy-max box's "buy N" text turns on from its multibuy timer at 1x only). Start (LightFlicker,
        /// RandomAnimSpeedComponent, JiggleDropScript) waits for the next frame's Start pass, which a turbo run never reaches,
        /// so its draws are not made here.
        /// </summary>
        void Activate(GameObject g)
        {
            bool was = g.activeInHierarchy;
            g.SetActive(true);
            if (was || !g.activeInHierarchy) return;
            foreach (upgradeBox b in g.GetComponentsInChildren<upgradeBox>(false))
                if (b.enabled) { UnityEngine.Random.Draw(); UnityEngine.Random.Draw(); }
            if (_sim.Goals != null) return;
            foreach (ObjectBobber b in g.GetComponentsInChildren<ObjectBobber>(false))
                if (b.enabled) UnityEngine.Random.Draw();
        }

        internal State Capture()
        {
            var s = new State { Earned = Earned, Values = new CloneValues[_clones.Length], Active = new Clone[_clones.Length][],
                Locals = (double[][])_locals.Clone(), LocalsHash = _localsHash, Global = _global, TripBroken = _tripBroken };
            for (int i = 0; i < _clones.Length; i++)
            {
                s.Values[i] = _clones[i].V;
                s.Active[i] = _clones[i].Active.Count == 0 ? Array.Empty<Clone>() : _clones[i].Active.ToArray();
            }
            return s;
        }

        internal void Restore(State s)
        {
            Earned = s.Earned;
            for (int i = 0; i < _clones.Length; i++)
            {
                _clones[i].V = s.Values[i];
                _clones[i].Active.Clear();
                _clones[i].Active.AddRange(s.Active[i]);
            }
            Array.Copy(s.Locals, _locals, _locals.Length);
            _localsHash = s.LocalsHash;
            _global = s.Global;
            _tripBroken = s.TripBroken;
        }

        internal void AddTo(ref HashCode h)
        {
            h.Add(Earned);
            foreach (Clones c in _clones)
            {
                CloneValues v = c.V;
                h.Add(v.Count); h.Add(v.Pooled); h.Add(v.PathSamples); h.Add(v.ArrayFull); h.Add(v.OnScreen);
                h.Add(v.BaseInterval); h.Add(v.SinceLast); h.Add(v.NextDue); h.Add(v.OffscreenDivisor); h.Add(v.PathLength);
                h.Add(v.Red); h.Add(v.Green); h.Add(v.Blue);
                foreach (Clone clone in c.Active) { h.Add(clone.Pointer); h.Add(clone.Fastness); h.Add(clone.Bigness); h.Add(clone.Coloured); }
                h.Add(-1);
            }
            h.Add(_localsHash);
            foreach (double g in _global) h.Add(g);
            h.Add(_tripBroken);
        }

        int HashLocals()
        {
            var h = new HashCode();
            foreach (double[] local in _locals) foreach (double v in local) h.Add(v);
            return h.ToHashCode();
        }
    }
}
