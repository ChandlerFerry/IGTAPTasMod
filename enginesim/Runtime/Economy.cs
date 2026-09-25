using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace IGTAP.EngineSim
{
    // The Cash a run earns offline, as the game pays it in turbo: a course's reward at the player's finish
    // (courseScript.stopTracking), the clones (clonesScript.Update as TurboRunner.StepClones steps it: SpawnClone,
    // DestroyClone, updateClonePath, updateCloneInterval, isOnScreen), and what the local upgrades a box buys change
    // (upgradeBox.DoBuyUpgrade: the course's reward, its clone count). Active when the start state carries the clones'
    // state (the plugin's start economy "clones"); Shop adds what it earns to the Cash the run can spend.
    //
    // The clones, the local and global upgrade dictionaries and the Cash earned are kept here, per simulation (the
    // game's are statics or clone objects offline can't instantiate). A course's reward, best time and comp boost are its
    // own fields (captured with the course). Of the global boxes only the global reward one (cashPerLoop) is modeled;
    // the others end the run. What the game decides with UnityEngine.Random (a clone's speed or size roll when its chance
    // isn't 0, colour clones) and the other currencies' branches (clone dust, colour clones, a Vman course completion)
    // end the run as unmodeled.
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
        static readonly int LocalCount = Enum.GetValues(typeof(localUpgrades.localUpgradeSet)).Cast<int>().Max() + 1;
        static readonly int GlobalCount = Enum.GetValues(typeof(globalStats.globalUpgradeSet)).Cast<int>().Max() + 1;

        // One active clone: its path pointer and rolls (clonePathPointers, cloneFastness, cloneBigness; any colour flag).
        internal struct Clone
        {
            public float Pointer;
            public int Fastness, Bigness;
            public bool Coloured;
        }

        // A clonesScript's own state.
        internal struct CloneValues
        {
            public int Count, Pooled, PathSamples;
            public bool ArrayFull, OnScreen;
            public float BaseInterval, SinceLast, NextDue, OffscreenDivisor, PathLength;
            // ColourCloneCounter: red, green, blue.
            public double Red, Green, Blue;
        }

        sealed class Clones
        {
            public clonesScript Script;
            public courseScript Course;
            // Index into _locals of the script's localUpgrades (clonesScript.upgrades); -1: none.
            public int Local;
            public CloneValues V;
            public readonly List<Clone> Active = new List<Clone>();
        }

        // Economy state after a tick (SimSnapshot).
        internal sealed class State
        {
            public double Earned;
            public CloneValues[] Values;
            public Clone[][] Active;
            public double[][] Locals;
            public int LocalsHash;
            public double[] Global;
        }

        readonly Simulation _sim;
        readonly Clones[] _clones;
        readonly Dictionary<clonesScript, Clones> _byScript = new Dictionary<clonesScript, Clones>();
        // TurboRunner.Run steps the clones on active objects when the run starts, in this order.
        readonly Clones[] _stepped;
        readonly Dictionary<localUpgrades, int> _localIndex = new Dictionary<localUpgrades, int>();
        // Each localUpgrades' localUpgradeDict, by localUpgradeSet. Only a purchase changes one, replacing its array
        // (snapshots share the arrays).
        readonly double[][] _locals;
        int _localsHash;
        // globalStats.globalUpgradeDict, by globalUpgradeSet. Only a purchase changes it, replacing the array.
        double[] _global = new double[GlobalCount];
        readonly double _gameSpeed, _vmanMult;

        // Cash earned since the start (course rewards and clone payouts).
        public double Earned { get; private set; }

        // The start state's "economy" (TasPlugin's EngineSimExport.WriteSnapshot): globalUpgrades, locals, clones,
        // gameSpeed, vmanMult. Anything missing is read from the world export.
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

            clonesScript[] scripts = world.All<clonesScript>();
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

        int LocalIndex(localUpgrades script)
        {
            return (object)script != null && _localIndex.TryGetValue(script, out int i) ? i : -1;
        }

        // An exported clonesScript: {id, path (clonePath's length), fields: [[type, name, value], ...]}.
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

        // A clonesScript without an exported state: the world export's.
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

        // globalStats.globalUpgradeEffectDict / globalUpgradeBaseEffectDict (constants); a missing key throws in the game.
        bool Effect(Dictionary<globalStats.globalUpgradeSet, double> dict, globalStats.globalUpgradeSet upgrade, out double value)
        {
            if (dict != null && dict.TryGetValue(upgrade, out value)) return true;
            value = 0.0;
            return Unmodeled("no effect value for " + upgrade);
        }

        // ---- clonesScript.Update as TurboRunner.StepClones runs it, once per physics tick after Movement.Update.
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
                    // Mono: float intermediates in double, rounded on store.
                    clone.Pointer = (float)(clone.Pointer + (double)PathIncrement(clone) * (50.0 * dt));
                    c.Active[i] = clone;
                }
                if (done != null)
                    for (int k = done.Count - 1; k >= 0; k--)
                        if (!DestroyClone(c, done[k])) return;
            }
        }

        // clonesScript.determinePathIncrement.
        static float PathIncrement(Clone clone)
        {
            return (float)((1.0 + clone.Fastness * (double)0.35f) * Mathf.Pow(0.75f, clone.Bigness));
        }

        // clonesScript.SpawnClone(1.0).
        bool SpawnClone(Clones c)
        {
            if (c.Local < 0) return Unmodeled("clones without local upgrades");
            // The speed and size rolls: with no chance, the first roll stops each loop at 0 whatever it is.
            if (Local(c.Local, localUpgrades.localUpgradeSet.fastCloneChance) * 5.0 + Global(globalStats.globalUpgradeSet.fastCloneChance) * 8.0 != 0.0)
                return Unmodeled("clone speed roll");
            if (Local(c.Local, localUpgrades.localUpgradeSet.bigCloneChance) * 5.0 + Global(globalStats.globalUpgradeSet.bigCloneChance) * 8.0 != 0.0)
                return Unmodeled("clone size roll");
            const int i = 0, j = 0;
            c.V.NextDue = (float)(c.V.BaseInterval / (1.0 + i * (double)0.35f));
            c.V.NextDue /= Mathf.Pow(0.75f, j);
            if (c.V.OnScreen)
            {
                if (c.V.Pooled > 0 && c.Active.Count < c.V.Count)
                {
                    c.V.OffscreenDivisor = 1f;
                    c.V.Pooled--;
                    c.Active.Add(new Clone { Pointer = 0f, Fastness = i, Bigness = j });
                    c.V.SinceLast = 0f;
                    // AssignCloneColour.
                    if (!ColourCheck(c)) return false;
                }
                return true;
            }
            if (c.V.Count <= 0) return true;
            if ((object)c.Course == null) return Unmodeled("clones without a course");
            double boost = 1.0;
            if (c.Course.compBoostTimeLeft > 0.0) boost = c.Course.compBoostBonus;
            double pay = c.Course.reward * (double)(j * 2 + 1);
            double mult = 0.1 + Global(globalStats.globalUpgradeSet.cloneMult) * 0.2 + Local(c.Local, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
            pay *= mult;
            pay *= boost;
            pay *= 1.0;
            pay /= (double)c.V.OffscreenDivisor;
            Earned += Math.Ceiling(pay);
            if (!ColourCheck(c)) return false;
            if (Local(c.Local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0) return Unmodeled("clone dust");
            c.V.SinceLast = 0f;
            c.V.OffscreenDivisor = c.V.NextDue;
            c.V.NextDue = 1f;
            return true;
        }

        // clonesScript.checkIfColourClone, while it finds no colour clone (one rolls UnityEngine.Random).
        bool ColourCheck(Clones c)
        {
            c.V.Green += Global(globalStats.globalUpgradeSet.greenCloneClance);
            if (c.V.Green > 100.0 || c.V.Red > 100.0) return Unmodeled("colour clone");
            return true;
        }

        // clonesScript.DestroyClone: the clone pays and goes back to the pool.
        bool DestroyClone(Clones c, int index)
        {
            Clone clone = c.Active[index];
            if (clone.Coloured) return Unmodeled("colour clone");
            if ((object)c.Course == null || c.Local < 0) return Unmodeled("clones without a course");
            double boost = 1.0;
            if (c.Course.compBoostTimeLeft > 0.0) boost = c.Course.compBoostBonus;
            double pay = c.Course.reward * (double)(clone.Bigness * 2 + 1);
            double mult = 0.1 + Global(globalStats.globalUpgradeSet.cloneMult) * 0.2 + Local(c.Local, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
            pay *= mult;
            pay *= boost;
            Earned += Math.Ceiling(pay);
            if (Local(c.Local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0) return Unmodeled("clone dust");
            c.V.Pooled++;
            c.Active.RemoveAt(index);
            return true;
        }

        // clonesScript.updateCloneInterval.
        static void UpdateCloneInterval(Clones c)
        {
            c.V.BaseInterval = c.V.PathLength / (float)c.V.Count;
            c.V.NextDue = c.V.BaseInterval;
        }

        // clonesScript.isOnScreen (the clone area's clonesLoD trigger): a change updates the course (fullUpdate).
        internal void OnScreen(clonesScript script, bool onScreen)
        {
            if (!_byScript.TryGetValue(script, out Clones c) || onScreen == c.V.OnScreen) return;
            if ((object)c.Course == null) { Unmodeled("clones without a course"); return; }
            if (!UpdateReward(c.Course)) return;
            c.V.OnScreen = onScreen;
        }

        // courseScript.UpdateReward (and fullUpdate: the rest is UI): the reward and the comp boost bonus.
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

        // courseScript.TryActivateCompBoost (the NP boost it doubles pays no Cash).
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

        // courseScript.stopTracking(player, savePositionData: true) after tracking stopped: `time` is the course timer
        // (currentPathTime) it stopped at.
        internal void CourseFinished(courseScript course, float time)
        {
            if (VmanScript.isCurrentlyVman) { Unmodeled("Vman course completion"); return; }
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
                // clonesScript.updateClonePath: the new best's recording, one sample per timed courseScript.FixedUpdate.
                int samples = PathSamples(time);
                if (samples < 0) { Unmodeled("course timer " + time + " is not a sum of physics ticks"); return; }
                c.V.PathLength = time;
                c.V.BaseInterval = c.V.PathLength / (float)c.V.Count;
                c.V.PathSamples = samples;
                for (int i = c.Active.Count - 1; i >= 0; i--)
                    if (!DestroyClone(c, i)) return;
                // cloneEndVelocity reads the path's 10th sample from the end: a shorter path throws out of stopTracking.
                if (samples < 10) { Unmodeled("a best path shorter than 10 samples"); return; }
                BestPathTimeField.SetValue(course, time);
            }
            UpdateCloneInterval(c);
        }

        // How many 0.02 steps courseScript.FixedUpdate added to reach the course timer `time` (-1: none).
        static int PathSamples(float time)
        {
            float t = 0f;
            int n = 0;
            while (t < time) { t += 0.02f; n++; }
            return t == time ? n : -1;
        }

        // upgradeBox.DoBuyUpgrade's local upgrade (after TimesUsed and the cost, before ScaleBoxCost).
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
                    // clonesScript.spawnNewCloneVisual.
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
            }
            return true;
        }

        // upgradeBox.DoBuyUpgrade's global cashPerLoop (every bonus reward box and course 2's GLOBALcashPerLoop): the global
        // dictionary, then the box's own course's reward. Other courses update theirs on their 5 s UpdateReward repeat, a
        // real-time InvokeRepeating that never fires inside a turbo run (one frame) and isn't run here either: their clones
        // and finishes pay the old reward until something else updates it (a clone area change, a purchase there).
        internal bool BoughtGlobal(upgradeBox box)
        {
            if (box.globalUpgrade != globalStats.globalUpgradeSet.cashPerLoop) return Unmodeled("global upgrade " + box.globalUpgrade);
            _global = (double[])_global.Clone();
            _global[(int)globalStats.globalUpgradeSet.cashPerLoop] += 1.0 * (double)BoxEffectMultField.GetValue(box);
            var course = BoxCourseField.GetValue(box) as courseScript;
            return (object)course == null || UpdateReward(course);
        }

        internal State Capture()
        {
            var s = new State { Earned = Earned, Values = new CloneValues[_clones.Length], Active = new Clone[_clones.Length][],
                Locals = (double[][])_locals.Clone(), LocalsHash = _localsHash, Global = _global };
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
            h.Add(_global[(int)globalStats.globalUpgradeSet.cashPerLoop]);  // the only global a run changes
        }

        int HashLocals()
        {
            var h = new HashCode();
            foreach (double[] local in _locals) foreach (double v in local) h.Add(v);
            return h.ToHashCode();
        }
    }
}
