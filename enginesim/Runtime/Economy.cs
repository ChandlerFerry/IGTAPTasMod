using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using UnityEngine;
using G = globalStats.globalUpgradeSet;

namespace IGTAP.EngineSim
{
    public sealed class Economy
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo BaseRewardField = typeof(courseScript).GetField("baseReward", Fields);
        static readonly FieldInfo BaseGpRewardField = typeof(courseScript).GetField("baseGpReward", Fields);
        static readonly FieldInfo BaseRpRewardField = typeof(courseScript).GetField("baseRpReward", Fields);
        static readonly FieldInfo BaseCdRewardField = typeof(courseScript).GetField("baseCdReward", Fields);
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
        static readonly FieldInfo BoxDoorToOpenField = typeof(upgradeBox).GetField("doorToOpen", Fields);
        static readonly FieldInfo CourseToExemptField = typeof(upgradeBox).GetField("courseToExemptReference", Fields);
        static readonly FieldInfo RpAtomBoxField = typeof(OvergrowthLoadScript).GetField("rpAtomBox", Fields);
        static readonly FieldInfo A1CoursesField = typeof(OvergrowthLoadScript).GetField("courses", Fields);
        static readonly FieldInfo BoxesToEnableField = typeof(OvergrowthLoadScript).GetField("boxesToEnable", Fields);
        static readonly FieldInfo TrunksField = typeof(Zone2to1TransitionController).GetField("trunks", Fields);
        static readonly FieldInfo BoxAtomColliderField = typeof(upgradeBox).GetField("atomColliderScript", Fields);
        /// <summary>
        /// The earliest the Omni Dash buy's Area 2 → 1 transition can open at 1x, in seconds after the buy
        /// (Zone2to1TransitionController.cs:46-81): the first trunk grows 0.6 a frame-second from the buy's own physics step
        /// (0.02 s, then the buy frame's whole deltaTime, at most Unity's 1/3 s maximumDeltaTime), then WaitForSeconds(3),
        /// then a fade a single long frame can finish, then updateState(open: true) turns closedObject off.
        /// </summary>
        const float TransitionOpensAfter = 1f / 0.6f - 0.02f - 1f / 3f + 3f;
        static readonly int LocalCount = Enum.GetValues(typeof(localUpgrades.localUpgradeSet)).Cast<int>().Max() + 1;
        static readonly int GlobalCount = Enum.GetValues(typeof(globalStats.globalUpgradeSet)).Cast<int>().Max() + 1;

        internal struct Clone
        {
            public float Pointer;
            public int Fastness, Bigness;
            public bool Red, Green;   // cloneIsRed, cloneIsGreen (cloneIsBlue is never set on this build: clonesScript.cs:399)
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
            public double[] Income;
            public CloneValues[] Values;
            public Clone[][] Active;
            public double[][] Locals;
            public int LocalsHash;
            public double[] Global;
            public bool TripBroken;
            public float TransitionOpensIn;
            public double CdMult;
            public int[] Atoms;
            public bool[] Crafting;
            public float[] CraftProgress;
            public int WaitingAtoms, HandledTier;
            public double TierUpCounter;
            public bool IsCrafting;
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
        // Seconds of run time until the Omni Dash buy's Area 2 → 1 transition can open at 1x (0: none under way).
        float _transitionOpensIn;
        // globalStats.CloneDustGpCdMult: green power and clone dust payouts are multiplied by it (RecalcCdMult).
        double _cdMult;

        // Atoms (AtomColliderSystem): the collider's own state, each tier's sprites (contents) as a count; the counts the game
        // keeps on globalStats (effectiveAtomCount, grossAtomCount, AtomCountInTermsOfWhiteAtoms) stay there, touched.
        readonly AtomColliderSystem _atomCollider;
        readonly upgradeBox _newAtomBox;
        readonly float _craftStep, _craftStepPerTier, _craftStart, _craftStartPerTier, _craftEnd;
        readonly int _atomEffectiveness, _tierColors;
        int[] _atoms = Array.Empty<int>();              // contents[tier].Count; the length is contents.Count
        bool[] _crafting = Array.Empty<bool>();         // craftingInProgress
        float[] _craftProgress = Array.Empty<float>();  // craftingProgress
        int _waitingAtoms, _handledTier;                // waitingAtoms, currentlyHandledTier
        double _tierUpCounter;                          // atomTierUpCounter
        bool _isCrafting;

        internal readonly double[] Income = new double[7];   // per globalStats.Currencies, what the economy paid since the start
        public double Earned => Income[(int)globalStats.Currencies.Cash];
        internal void Add(globalStats.Currencies c, double v) { Income[(int)c] += v; }

        internal Economy(Simulation sim, JsonElement economy)
        {
            _sim = sim;
            World world = sim.World;
            if (economy.TryGetProperty("globalUpgrades", out JsonElement global))
                SetGlobal((Dictionary<globalStats.globalUpgradeSet, double>)world.Read(global, typeof(Dictionary<globalStats.globalUpgradeSet, double>)));
            else SetGlobal(globalStats.globalUpgradeDict);
            WriteStatic();
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
            RecalcCdMult();

            _atomCollider = world.All<AtomColliderSystem>().FirstOrDefault();
            if ((object)_atomCollider != null)
            {
                T A<T>(string name) => (T)typeof(AtomColliderSystem).GetField(name, Fields).GetValue(_atomCollider);
                _craftStep = A<float>("craftingProgressPerStep");
                _craftStepPerTier = A<float>("craftingProgressPerStepPerTier");
                _craftStart = A<float>("craftingStartPoint");
                _craftStartPerTier = A<float>("startPointPerTier");
                _craftEnd = A<float>("craftingEndValue");
                _atomEffectiveness = A<int>("atomTierEffectiveness");
                _tierColors = A<Array>("tierColors")?.Length ?? 0;
                _newAtomBox = A<upgradeBox>("newAtomUpgradeBox");
                var contents = A<System.Collections.IList>("contents");
                List<bool> crafting = A<List<bool>>("craftingInProgress");
                List<float> progress = A<List<float>>("craftingProgress");
                int tiers = contents?.Count ?? 0;
                GrowTiers(tiers);
                for (int i = 0; i < tiers; i++)
                {
                    _atoms[i] = ((System.Collections.ICollection)contents[i]).Count;
                    _crafting[i] = crafting[i];
                    _craftProgress[i] = progress[i];
                }
                _waitingAtoms = A<int>("waitingAtoms");
                _handledTier = A<int>("currentlyHandledTier");
                _isCrafting = A<bool>("isCrafting");
                _tierUpCounter = _atomCollider.atomTierUpCounter;
                // The grid as 1x had it at the start (the plugin's TurboRunner.AtomGridDueIn): its next call counted in ticks as
                // the start's other pending Invokes are (Simulation.LoadStart), every 0.4 s after. A start without it has none.
                if (economy.TryGetProperty("atomGridDueIn", out JsonElement due))
                    sim.Scheduler.Queue.Add(new Scheduler.Pending { Due = Mathf.FloorToInt(World.ReadFloat(due) / Time.fixedDeltaTime),
                        Target = _atomCollider, Method = "callAddNewAtom", Repeat = 0.4f });
            }
        }

        /// <summary>globalStats.RecalcCloneDustMult (globalStats.cs:1126-1140) on the clone dust the run holds: the game runs it
        /// after every clone dust payout (and RubbleDisplayScript's readout), so at a start it is the held clone dust's.</summary>
        void RecalcCdMult()
        {
            double cd = _sim.Shop.Holds(globalStats.Currencies.CloneDust);
            double m = cd == 0.0 ? 0.0 : Math.Log10(cd);   // math.log10: Unity.Mathematics, double
            m += Global(globalStats.globalUpgradeSet.additionalCDdigits);
            _cdMult = Math.Pow(m, 1.7) * 1.05 + 1.0;
        }

        /// <summary>The global upgrades native code the engine steps reads from globalStats' static dictionary:
        /// PlatformMover.IE_AdvanceToNextState (PlatformMover.cs:134) and JiggleDropScript.FixedUpdate (JiggleDropScript.cs:88).</summary>
        static readonly G[] StaticReads = { G.zipMoversUnlocked, G.unlockJiggleDrops };

        /// <summary>The run's values of <see cref="StaticReads"/> into the static dictionary (a buy, a restore, the start): _global
        /// stays the run's own.</summary>
        void WriteStatic()
        {
            Dictionary<G, double> dict = globalStats.globalUpgradeDict;
            if (dict == null) return;
            foreach (G g in StaticReads) dict[g] = _global[(int)g];
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
                    Red = i < red.Count && red[i], Green = i < green.Count && green[i] });
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

        /// <summary>The atom collider's atoms per tier (contents[i].Count) and its waiting atoms (--ticks-out).</summary>
        internal int[] AtomCounts => (int[])_atoms.Clone();
        internal int WaitingAtoms => _waitingAtoms;
        /// <summary>The grid's callAddNewAtom calls so far (--ticks-out, a diagnostic: not in snapshots).</summary>
        internal int GridCalls;
        /// <summary>globalStats.CloneDustGpCdMult as the run has it (--ticks-out).</summary>
        internal double CdMult => _cdMult;

        /// <summary>Each course's clones now (--ticks-out): course, count, path length, on screen, in flight, spawn timer.</summary>
        internal IEnumerable<object[]> CloneStates()
        {
            foreach (Clones c in _clones)
                if (c.V.Count > 0) yield return new object[] { c.Course?.courseNumber ?? -1, c.V.Count, c.V.PathLength, c.V.OnScreen, c.Active.Count, c.V.SinceLast };
        }

        /// <summary>Whether every stepped course with clones has both roll chances at 0: SpawnClone's fast and big rolls then always
        /// fail and draw exactly two values.</summary>
        internal bool RollsFixed()
        {
            foreach (Clones c in _stepped)
            {
                if (c.V.Count <= 0 || c.Local < 0) continue;
                if (Local(c.Local, localUpgrades.localUpgradeSet.fastCloneChance) * 5.0 + Global(globalStats.globalUpgradeSet.fastCloneChance) * 8.0 > 0.0
                    || Local(c.Local, localUpgrades.localUpgradeSet.bigCloneChance) * 5.0 + Global(globalStats.globalUpgradeSet.bigCloneChance) * 8.0 > 0.0)
                    return false;
            }
            return true;
        }

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
            if (_transitionOpensIn > 0f && (_transitionOpensIn -= dt) <= 0f)
            {
                Unmodeled("the Omni Dash buy's Area 2 → 1 transition (Zone2to1TransitionController.doStateVisuals) can have opened at 1x by now: "
                    + "its closedObject's \"Transition ground\" collider goes off on 1x frames turbo never plays within a run");
                return;
            }
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
                    // Build 25640326: a timer still at Start's 10000 (>= 4000) goes to 0, any other keeps the overshoot.
                    c.V.SinceLast = c.V.SinceLast >= 4000f ? 0f : (float)(c.V.SinceLast - (double)c.V.NextDue);
                    // AssignCloneColour (clonesScript.cs:462-495): the colours go on the clone and pay at its path's end.
                    if (!ColourCheck(c, out bool isRed, out bool isGreen)) return false;
                    c.Active.Add(new Clone { Pointer = 0f, Fastness = i, Bigness = j, Red = isRed, Green = isGreen });
                }
                return true;
            }
            if (c.V.Count <= 0) return true;
            if ((object)c.Course == null) return Unmodeled("clones without a course");
            double boost = Boost(c.Course), divisor = c.V.OffscreenDivisor;
            double pay = Math.Ceiling(Pay(c, j) / divisor);
            Add(globalStats.Currencies.Cash, pay);
            Spawned?.Invoke(c.Course.courseNumber, c.Script.GetInstanceID(), false, divisorBefore, i, j, pay);
            // clonesScript.cs:243-283, SpawnClone's mult 1 (only a Vman course passes another, VmanScript.cs:124): each pay
            // in the game's order of operations; array[2] (blue) is never set (clonesScript.cs:399).
            if (!ColourCheck(c, out bool red, out bool green)) return false;
            double size = j * 2 + 1;
            if (red && !PayRed(c.Course.rpReward * size * boost / divisor)) return false;
            if (green) Add(globalStats.Currencies.GreenPower, Math.Ceiling(c.Course.gpReward * size * _cdMult * boost / divisor));
            if (Local(c.Local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0)
            {
                Add(globalStats.Currencies.CloneDust, Math.Ceiling(c.Course.CdReward * size * boost * _cdMult / divisor));
                RecalcCdMult();
            }
            c.V.SinceLast = 0f;
            c.V.OffscreenDivisor = c.V.NextDue;
            c.V.NextDue = 1f;
            return true;
        }

        /// <summary>A clone's payout before the offscreen divisor: the course reward × (2 bigness + 1) × the clone multiplier × the
        /// competition boost, in the game's order.</summary>
        double Pay(Clones c, int bigness)
        {
            double boost = Boost(c.Course);
            double pay = c.Course.reward * (double)(bigness * 2 + 1);
            double mult = 0.1 + Global(globalStats.globalUpgradeSet.cloneMult) * 0.2 + Local(c.Local, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
            pay *= mult;
            return pay * boost;
        }

        static double Boost(courseScript course) { return course.compBoostTimeLeft > 0.0 ? course.compBoostBonus : 1.0; }

        /// <summary>A red clone's red power (clonesScript.cs:250-254, 330-334): capped at globalStats.rpCap as the world holds it
        /// (UpdateRpCap runs at Movement.Start and at an RP cap box's buy: <see cref="UpdateRpCap"/>).</summary>
        bool PayRed(double value)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("red power without globalStats (the game throws)");
            Add(globalStats.Currencies.RedPower, Math.Ceiling(value));
            double held = _sim.Shop.Holds(globalStats.Currencies.RedPower);
            // ponytail: lands exactly on the cap while the held sum and the cap are integers below 2^53; a Shop setter if not.
            if (held > stats.rpCap) Income[(int)globalStats.Currencies.RedPower] -= held - stats.rpCap;
            return true;
        }

        /// <summary>clonesScript.checkIfColourClone (clonesScript.cs:379-401): the green counter grows each spawn, a pass over 100
        /// makes the clone green and grows the red counter (off Area 2), a red pass over 100 makes it red; each pass draws
        /// Random.Range(-15, 15).</summary>
        bool ColourCheck(Clones c, out bool red, out bool green)
        {
            red = green = false;
            c.V.Green += Global(globalStats.globalUpgradeSet.greenCloneClance);
            if (c.V.Green > 100.0)
            {
                if ((object)c.Course == null) return Unmodeled("a green clone without a course (the game throws)");
                c.V.Green -= 100.0;
                c.V.Green += UnityEngine.Random.Range(-15, 15);
                green = true;
                if (c.Course.area != 2) c.V.Red += Global(globalStats.globalUpgradeSet.redCloneChance);
            }
            if (c.V.Red > 100.0)
            {
                c.V.Red -= 100.0;
                c.V.Red += UnityEngine.Random.Range(-15, 15);
                red = true;
            }
            return true;
        }

        /// <summary>clonesScript.DestroyClone (clonesScript.cs:289-375): its pays at the comp boost, with no divisor; cloneIsBlue's
        /// is never set on this build (clonesScript.cs:399).</summary>
        bool DestroyClone(Clones c, int index)
        {
            Clone clone = c.Active[index];
            if ((object)c.Course == null || c.Local < 0) return Unmodeled("clones without a course");
            double boost = Boost(c.Course), size = clone.Bigness * 2 + 1;
            Add(globalStats.Currencies.Cash, Math.Ceiling(Pay(c, clone.Bigness)));
            if (clone.Red && !PayRed(c.Course.rpReward * size * boost)) return false;
            if (clone.Green) Add(globalStats.Currencies.GreenPower, Math.Ceiling(c.Course.gpReward * size * _cdMult * boost));
            if (Local(c.Local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0)
            {
                Add(globalStats.Currencies.CloneDust, Math.Ceiling(c.Course.CdReward * size * boost * _cdMult));
                RecalcCdMult();
            }
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
                || !Effect(effect, globalStats.globalUpgradeSet.compBoostStrength, out double strength)
                || !Effect(effect, globalStats.globalUpgradeSet.increasedGreenPower, out double increasedGreenPower)
                || !Effect(effect, globalStats.globalUpgradeSet.moreGP, out double moreGP)
                || !Effect(effect, globalStats.globalUpgradeSet.increasedRP, out double increasedRP)
                || !Effect(effect, globalStats.globalUpgradeSet.moreRP, out double moreRP)
                || !Effect(effect, globalStats.globalUpgradeSet.IncreasedCloneDust, out double increasedCloneDust))
                return false;
            double reward = Math.Ceiling((double)(int)BaseRewardField.GetValue(course)
                * (1.0 + Global(globalStats.globalUpgradeSet.increasedWatts) * (increasedWatts / 100.0) + Global(globalStats.globalUpgradeSet.tripleThreatIncrease) * (tripleThreat / 100.0))
                * (1.0 + Global(globalStats.globalUpgradeSet.cashPerLoop) * (cashPerLoop / 100.0))
                * (1.0 + Local(local, localUpgrades.localUpgradeSet.cashPerLoop) * (moreWatts / 100.0))
                * (1.0 + Local(local, localUpgrades.localUpgradeSet.moreWatts))
                * (1.0 + Global(globalStats.globalUpgradeSet.moreWatts) * (moreWatts / 100.0))
                * _gameSpeed * _vmanMult * Math.Pow(10.0, course.rewardTier));
            course.reward = Math.Ceiling(Math.Pow(reward, 1.0 + Global(globalStats.globalUpgradeSet.wattPower) * wattPower));
            // courseScript.cs:497-499
            course.gpReward = Math.Ceiling((double)BaseGpRewardField.GetValue(course)
                * (1.0 + Local(local, localUpgrades.localUpgradeSet.GreenCloneRewardBase))
                * (1.0 + Global(globalStats.globalUpgradeSet.increasedGreenPower) * (increasedGreenPower / 100.0) + Global(globalStats.globalUpgradeSet.tripleThreatIncrease) * (tripleThreat / 100.0))
                * (1.0 + Global(globalStats.globalUpgradeSet.moreGP) * (moreGP / 100.0))
                * _gameSpeed * _vmanMult);
            course.rpReward = Math.Ceiling((double)BaseRpRewardField.GetValue(course)
                * (1.0 + Local(local, localUpgrades.localUpgradeSet.baseRP))
                * (1.0 + Global(globalStats.globalUpgradeSet.increasedRP) * (increasedRP / 100.0))
                * (1.0 + Global(globalStats.globalUpgradeSet.moreRP) * (moreRP / 100.0))
                * _gameSpeed);
            course.CdReward = Math.Ceiling((double)BaseCdRewardField.GetValue(course)) * _gameSpeed
                * (1.0 + Global(globalStats.globalUpgradeSet.IncreasedCloneDust) * (increasedCloneDust / 100.0)) * _vmanMult;
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
            // courseScript.cs:446-449: a boost that isn't running doubles NP's effect; the course's FixedUpdate halves it (:307-311, native).
            if (course.compBoostTimeLeft <= 0.0)
            {
                globalStats stats = Singleton<globalStats>.Instance;
                if ((object)stats == null) return Unmodeled("the comp boost without globalStats (the game throws)");
                _sim.Touch(stats);
                stats.EffectOfNpCompBoost *= 2.0;
            }
            course.compBoostTimeLeft = timeBase + time * Global(globalStats.globalUpgradeSet.compBoostTime);
            course.compBoostTimeLeft += (double)0f;
            course.compBoostBonus = strengthBase + strength * Global(globalStats.globalUpgradeSet.compBoostStrength);
            return true;
        }

        internal void CourseFinished(courseScript course, float time)
        {
            double reward = course.reward;
            if (course.compBoostTimeLeft > 0.0) reward *= course.compBoostBonus;
            Add(globalStats.Currencies.Cash, Math.Ceiling(reward));
            int local = LocalIndex(course.localUpgradesScript);
            if (local < 0) { Unmodeled("course " + course.courseNumber + " without local upgrades"); return; }
            if (Local(local, localUpgrades.localUpgradeSet.enableCloneDustGeneration) >= 1.0)
            {
                // courseScript.cs:396-407
                double cd = course.CdReward * 10.0;
                if (course.compBoostTimeLeft > 0.0) cd *= course.compBoostBonus;
                cd *= _cdMult;
                Add(globalStats.Currencies.CloneDust, Math.Ceiling(cd));
                RecalcCdMult();
            }
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

        /// <summary>
        /// DoBuyUpgrade's GLOBAL branch (upgradeBox.cs:706-779). A reward input (watts, GP, RP, clone dust, triple threat) is
        /// read by each course on its next UpdateReward, not at the buy: its 5 s InvokeRepeating (courseScript.cs:268; in a
        /// Full-TAS run MacroEngine.RefreshRewards), its clones going on or off screen (clonesScript.cs:529, clonesLoD.cs:21),
        /// its comp boost's end (courseScript.cs:312) and a prestige (courseScript.cs:563), which the engine runs where the game
        /// does. Only cashPerLoop updates its own course at once (upgradeBox.cs:745-751).
        /// </summary>
        internal bool BoughtGlobal(upgradeBox box)
        {
            G upgrade = box.globalUpgrade;
            // DoBuyUpgrade's spawnNewAtom (upgradeBox.cs:752-754): BuyNewAtom queues an atom (AtomColliderSystem.cs:174-177)
            // that the collider's 0.4 s grid lands (AtomTick). The touch's course reset ran first (AtomPrestige).
            if (upgrade == G.spawnNewAtom)
            {
                if (!OwnCollider(box)) return false;
                _waitingAtoms++;
                return AtomsLand();
            }
            switch (upgrade)
            {
                case G.startVmanSpeedrun: return Unmodeled("a Vman speedrun's start (VmanScript.SetupVmanSpeedrun, upgradeBox.cs:710)");
                case G.openGate: return Unmodeled("the gate box (gateAnim's \"open\" trigger and deactivate, upgradeBox.cs:731-732: the gate's collider)");
                case G.area2DoorOpen: return Unmodeled("the Area 2 door box (doorToOpen.ForceNextPhase, upgradeBox.cs:756: the door's collider)");
                case G.unlockTeleporters: return Unmodeled("teleporters (TeleporterScript.updateVisuals, upgradeBox.cs:767-769: TeleporterScript reads the static dictionary)");
            }
            _global = (double[])_global.Clone();
            double effectMult = (double)BoxEffectMultField.GetValue(box);
            if (upgrade == G.RPcap || upgrade == G.MoreRpCap)
            {
                _global[(int)upgrade] += 1.0 * effectMult;
                return UpdateRpCap();
            }
            if (upgrade == G.blueCloneChance && Global(G.blueCloneChance) == 0.0 || upgrade == G.redCloneChance && Global(G.redCloneChance) == 0.0)
            {
                // upgradeBox.cs:717-721: the next box is marked visible (read by upgradeBox.Start at the next load, not now).
                GameObject reference = BoxObjectReferenceField.GetValue(box) as GameObject;
                upgradeBox next = (object)reference == null ? null : reference.GetComponent<upgradeBox>();
                if ((object)next == null) return Unmodeled("a first colour clone chance box without its ObjectReference box (the game throws)");
                _sim.Touch(next);
                next.visible = true;
                _global[(int)upgrade] += 1.0 * effectMult;
                return true;
            }
            switch (upgrade)
            {
                case G.atomLevelChance:
                    _global[(int)upgrade] += 1.0 * effectMult;
                    return OwnCollider(box) && ClearAtoms();
                case G.increasedNuclearPower:
                case G.tripleThreatIncrease:
                    _global[(int)upgrade] += 1.0 * effectMult;
                    return RecalcNp();
                case G.unlockPrestige:
                {
                    _global[(int)G.cloneMult] += 1.0 * effectMult;
                    var text = BoxStatuePrestigeTextField.GetValue(box) as GameObject;
                    if ((object)text == null) return Unmodeled("unlock prestige box without its statue text (the game throws)");
                    text.SetActive(true);
                    return BoostOldCourses(box);
                }
                case G.cashPerLoop:
                {
                    _global[(int)upgrade] += 1.0 * effectMult;
                    var course = BoxCourseField.GetValue(box) as courseScript;
                    return (object)course == null || UpdateReward(course);
                }
                case G.exemptCourseFromAtomPrestige:
                {
                    if (CourseToExemptField.GetValue(box) is not courseScript course) return Unmodeled("an exempt box without its course (the game throws)");
                    _sim.Touch(course);
                    course.exemptFromAtomPrestige = true;
                    return true;
                }
                case G.makeCourseBuyMax:
                {
                    // courseScript.EnableBuyMax (courseScript.cs:714-725): the course's active local boxes buy max.
                    if (CourseToExemptField.GetValue(box) is not courseScript course || (object)course.localUpgradesScript == null)
                        return Unmodeled("a buy max box without its course or its local upgrades (the game throws)");
                    foreach (upgradeBox b in course.localUpgradesScript.transform.GetComponentsInChildren<upgradeBox>())
                    {
                        if (b.upgrade == localUpgrades.localUpgradeSet.GLOBAL || b.upgrade == localUpgrades.localUpgradeSet.Movement
                            || b.upgrade == localUpgrades.localUpgradeSet.prestige) continue;
                        _sim.Touch(b);
                        b.buyMax = true;
                    }
                    return true;
                }
                case G.zipMoversUnlocked:
                case G.unlockJiggleDrops:
                    _global[(int)upgrade] += 1.0 * effectMult;
                    WriteStatic();
                    return true;
                default:   // IncreasedCloneDust (its own case, the same), treeGrowth (nothing reads it: the box's tree grows,
                           // Shop.GrowTree) and every other global
                    _global[(int)upgrade] += 1.0 * effectMult;
                    return true;
            }
        }

        // Atoms (AtomColliderSystem)

        /// <summary>The box's atomColliderScript is the world's collider, whose state this economy keeps (the game throws on none).</summary>
        bool OwnCollider(upgradeBox box)
        {
            var collider = BoxAtomColliderField.GetValue(box) as AtomColliderSystem;
            if ((object)collider == null) return Unmodeled("an atom box without its atomColliderScript (the game throws)");
            return ReferenceEquals(collider, _atomCollider) || Unmodeled("an atom box whose collider isn't the world's AtomColliderSystem");
        }

        /// <summary>
        /// Waiting atoms land only on the collider's 0.4 s grid, OnEnable's InvokeRepeating("callAddNewAtom", 0, 0.4)
        /// (AtomColliderSystem.cs:111-115), which 1x starts when ZoneLoader._LoadZone(2) turns zone 2 on. The engine runs no
        /// OnEnable and loads no zone within a run (cuts.txt: zoneChanger): the grid runs only if the start had it (the trace's
        /// atomGridDueIn), and otherwise a waiting atom ends the run.
        /// </summary>
        bool AtomsLand()
        {
            if (_waitingAtoms <= 0) return true;
            foreach (Scheduler.Pending p in _sim.Scheduler.Queue)
                if (ReferenceEquals(p.Target, _atomCollider) && p.Method == "callAddNewAtom") return true;
            return Unmodeled("a waiting atom with AtomColliderSystem's 0.4 s grid not running: it starts when zone 2 loads, which the engine "
                + "doesn't do within a run, and this trace's start had no grid (atomGridDueIn)");
        }

        /// <summary>AtomColliderSystem.callAddNewAtom (AtomColliderSystem.cs:117-126), the 0.4 s grid's call (hooks.txt replaces
        /// it): a waiting atom lands, +1 AtomicPower, at the tier determineAtomTier rolls.</summary>
        internal void AtomTick(AtomColliderSystem collider)
        {
            if (!ReferenceEquals(collider, _atomCollider)) { Unmodeled("callAddNewAtom on a collider that isn't the world's AtomColliderSystem"); return; }
            GridCalls++;
            if (_waitingAtoms <= 0) return;
            Add(globalStats.Currencies.AtomicPower, 1.0);
            if (!AtomTier(out int tier) || !AddAtom(tier)) return;
            _waitingAtoms--;
        }

        /// <summary>determineAtomTier (AtomColliderSystem.cs:128-138): the counter grows by the atom level chance; each 100 is a tier up.</summary>
        bool AtomTier(out int tier)
        {
            tier = 0;
            if (!Effect(globalStats.globalUpgradeEffectDict, G.atomLevelChance, out double chance)) return false;
            _tierUpCounter += Global(G.atomLevelChance) * chance;
            while (_tierUpCounter >= 100.0)
            {
                _tierUpCounter -= 100.0;
                tier++;
            }
            return true;
        }

        /// <summary>AddNewAtom's counts (AtomColliderSystem.cs:179-211); its sprite and npDisplay are presentation, and its
        /// tierColors[Tier] throws past the array.</summary>
        bool AddAtom(int tier)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("an atom without globalStats (the game throws)");
            if (tier >= _tierColors) return Unmodeled("a tier " + tier + " atom (AtomColliderSystem.tierColors has " + _tierColors + ": the game throws)");
            GrowTiers(tier + 1);
            _atoms[tier]++;
            _sim.Touch(stats);
            stats.grossAtomCount += 1 + tier * (tier - 1);
            stats.effectiveAtomCount += Mathf.Pow(_atomEffectiveness + 10 * tier, tier) * 0.001f;
            stats.AtomCountInTermsOfWhiteAtoms += Factorial(2 + tier) / 2.0;
            return RecalcNp();
        }

        /// <summary>The tiers AddNewAtom and FinishCrafting add (AtomColliderSystem.cs:189-195, 292-299): not crafting, progress 1.</summary>
        void GrowTiers(int count)
        {
            int n = _atoms.Length;
            if (count <= n) return;
            Array.Resize(ref _atoms, count);
            Array.Resize(ref _crafting, count);
            Array.Resize(ref _craftProgress, count);
            for (int i = n; i < count; i++) _craftProgress[i] = 1f;
        }

        /// <summary>Utilities.Factorial (Utilities.cs:22-29).</summary>
        static double Factorial(int f) { return f == 0 ? 1.0 : (double)f * Factorial(f - 1); }

        /// <summary>
        /// AtomColliderSystem.Update (AtomColliderSystem.cs:213-232) in a rendered frame on its Time.deltaTime (0 in a dash freeze's
        /// and a pause's held frames), while the collider is on: one tier's crafting a frame, round robin. The sprites' movement
        /// is presentation.
        /// </summary>
        internal void AtomFrame(float dt)
        {
            if ((object)_atomCollider == null || !_atomCollider.isActiveAndEnabled || _atoms.Length <= 0) return;
            if (!HandleTier(_handledTier, dt)) return;
            _handledTier++;
            if (_handledTier >= _atoms.Length) _handledTier = 0;
        }

        /// <summary>HandleTierLogic's crafting (AtomColliderSystem.cs:234-247): a crafting tier's progress grows, finishing past
        /// craftingEndValue; an idle tier with 3 + tier atoms starts while no tier crafts.</summary>
        bool HandleTier(int tier, float dt)
        {
            if (_crafting[tier])
            {
                _craftProgress[tier] *= 1f + Mathf.Max(_craftStep + _craftStepPerTier * (float)tier, 5f) * (dt * (float)_atoms.Length);
                if (_craftProgress[tier] > _craftEnd) return FinishCrafting(tier);
            }
            else if (_atoms[tier] >= 3 + tier && !_isCrafting) StartCrafting(tier);
            return true;
        }

        /// <summary>startCrafting (AtomColliderSystem.cs:278-283).</summary>
        void StartCrafting(int tier)
        {
            _isCrafting = true;
            _crafting[tier] = true;
            _craftProgress[tier] = _craftStart + _craftStartPerTier * (float)tier;
        }

        /// <summary>
        /// FinishCrafting (AtomColliderSystem.cs:285-332), its counts as the game has them: 2 + tier atoms of the tier off
        /// (effective, gross, white each), the third's effective and white once more but not its gross, the next tier's
        /// effective and white on, and gross += the tier's 1 + tier (tier - 1), not the next tier's. 3 + tier atoms leave the
        /// tier, one joins the next; a tier made for the first time pays 1000 ticks of NP at the new rate. Its tierColors[tier + 1]
        /// throws past the array.
        /// </summary>
        bool FinishCrafting(int tier)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("crafting without globalStats (the game throws)");
            if (tier + 1 >= _tierColors) return Unmodeled("crafting a tier " + (tier + 1) + " atom (AtomColliderSystem.tierColors has " + _tierColors + ": the game throws)");
            bool first = false;
            _isCrafting = false;
            _crafting[tier] = false;
            _craftProgress[tier] = 1f;
            if (_atoms.Length <= tier + 1)
            {
                first = true;
                GrowTiers(tier + 2);
            }
            _sim.Touch(stats);
            for (int i = 0; i < 2 + tier; i++)
            {
                stats.effectiveAtomCount -= Mathf.Pow(_atomEffectiveness + 10 * tier, tier) * 0.001f;
                stats.grossAtomCount -= 1 + tier * (tier - 1);
                stats.AtomCountInTermsOfWhiteAtoms -= Factorial(2 + tier) / 2.0;
            }
            stats.effectiveAtomCount -= Mathf.Pow((float)_atomEffectiveness + 10f * (float)tier, tier) * 0.001f;
            stats.AtomCountInTermsOfWhiteAtoms -= Factorial(2 + tier) / 2.0;
            stats.effectiveAtomCount += Mathf.Pow(_atomEffectiveness + 10 * (tier + 1), tier + 1) * 0.001f;
            stats.AtomCountInTermsOfWhiteAtoms += Factorial(2 + tier + 1) / 2.0;
            stats.grossAtomCount += 1 + tier * (tier - 1);
            _atoms[tier + 1]++;
            _atoms[tier] -= 3 + tier;
            if (!RecalcNp()) return false;
            if (first) Add(globalStats.Currencies.AtomicPower, stats.NPrewardPerTick * 1000.0);
            return true;
        }

        /// <summary>
        /// AtomColliderSystem.ClearAtoms (AtomColliderSystem.cs:140-172), the atom level box's: every atom, crafting and waiting
        /// atom gone, the counts 0 and NP recalculated, then the atom box's onPrestige(force: true) and one more use. onPrestige
        /// (upgradeBox.cs:966-994) is <see cref="BoxPrestige"/> forced: the game's own would write an exempt box's native
        /// localUpgradeDict, which the economy doesn't read (it keeps its own), and draw texts and sprites.
        /// </summary>
        bool ClearAtoms()
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null || (object)_newAtomBox == null) return Unmodeled("ClearAtoms without globalStats or its newAtomUpgradeBox (the game throws)");
            _atoms = Array.Empty<int>();
            _crafting = Array.Empty<bool>();
            _craftProgress = Array.Empty<float>();
            _tierUpCounter = 0.0;
            _sim.Touch(stats);
            stats.effectiveAtomCount = 0.0;
            stats.grossAtomCount = 0.0;
            stats.AtomCountInTermsOfWhiteAtoms = 0.0;
            if (!RecalcNp()) return false;
            _isCrafting = false;
            _handledTier = 0;
            _waitingAtoms = 0;
            if (!BoxPrestige(_newAtomBox, force: true)) return false;
            _sim.Touch(_newAtomBox);
            _newAtomBox.TimesUsed++;
            return true;
        }

        /// <summary>globalStats.UpdateRpCap (globalStats.cs:1142-1147) into the world's globalStats, which PayRed caps at.</summary>
        bool UpdateRpCap()
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("the RP cap without globalStats (the game throws)");
            Dictionary<G, double> effect = globalStats.globalUpgradeEffectDict, baseEffect = globalStats.globalUpgradeBaseEffectDict;
            if (!Effect(baseEffect, G.RPcap, out double capBase) || !Effect(effect, G.RPcap, out double cap) || !Effect(effect, G.MoreRpCap, out double more))
                return false;
            _sim.Touch(stats);
            stats.rpCap = capBase + Global(G.RPcap) * cap;
            stats.rpCap *= Global(G.MoreRpCap) * more + 1.0;
            stats.rpCap *= _vmanMult;
            return true;
        }

        /// <summary>globalStats.RecalcNpRewardPerTick (globalStats.cs:1158-1163) on the world's globalStats: NP a tick from the
        /// atoms (effectiveAtomCount, AtomCountInTermsOfWhiteAtoms) and its cap.</summary>
        internal bool RecalcNp()
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("NP without globalStats (the game throws)");
            Dictionary<G, double> effect = globalStats.globalUpgradeEffectDict;
            if (!Effect(effect, G.increasedNuclearPower, out double nuclear) || !Effect(effect, G.tripleThreatIncrease, out double triple)) return false;
            _sim.Touch(stats);
            stats.NPrewardPerTick = stats.effectiveAtomCount * (1.0 + stats.AtomCountInTermsOfWhiteAtoms / 60.0)
                * (1.0 + Global(G.increasedNuclearPower) * (nuclear / 100.0) + Global(G.tripleThreatIncrease) * (triple / 100.0));
            stats.NPrewardCap = stats.NPrewardPerTick * 50.0 * 120.0;
            stats.NPrewardCap *= _vmanMult;
            return true;
        }

        /// <summary>
        /// globalStats.FixedUpdate's DoConstantCurrency (globalStats.cs:1100-1124), once a physics step: NP a tick below the cap,
        /// past it nothing or, with NpCapIsSoftcap, the rate times (cap / NP)³; doubled per running comp boost with
        /// compBoostDoublesNP. lastAppliedNpRewardPerTick is only the readout's. No globalStats, no FixedUpdate: nothing.
        /// Simulation runs it before every other FixedUpdate: globalStats inherits [DefaultExecutionOrder(-50)] from Singleton&lt;T&gt;
        /// (Singleton.cs:3), so it pays before courseScript.FixedUpdate halves EffectOfNpCompBoost (courseScript.cs:307).
        /// </summary>
        internal void FixedStep()
        {
            if (!AtomsLand()) return;
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return;
            double np = _sim.Shop.Holds(globalStats.Currencies.AtomicPower), add;
            if (np < stats.NPrewardCap) add = stats.NPrewardPerTick * 1.0 * (1.0 + Global(G.NpGenerationSpeed)) * _gameSpeed;
            else if (Global(G.NpCapIsSoftcap) != 0.0)
                add = stats.NPrewardPerTick * 1.0 * (1.0 + Global(G.NpGenerationSpeed)) * _gameSpeed * Math.Pow(stats.NPrewardCap / np, 3.0);
            else add = 0.0;
            if (Global(G.compBoostDoublesNP) != 0.0) add *= stats.EffectOfNpCompBoost;
            Add(globalStats.Currencies.AtomicPower, add);
        }

        /// <summary>
        /// upgradeBox.buyUpgrade's atom block (upgradeBox.cs:641-654), once a touch before its buys: the first buy of a box
        /// with a door opens it unless the machine's PlayerPrefs already hold "OpenA2Door", then, unless it is a Green Power
        /// box with GPAtomsDontResetCourses, doNewAtomPrestige (upgradeBox.cs:688-696): every coursesToReset course not
        /// exempt from atom prestige resets its upgrades, clones and boxes at its own tiers, and Cash goes to 0.
        /// </summary>
        internal bool AtomPrestige(upgradeBox box, globalStats.Currencies currency)
        {
            if (box.TimesUsed == 0 && (object)(BoxDoorToOpenField.GetValue(box) as PlatformMover) != null)
                return Unmodeled("the atom box's first buy opens its door (doorToOpen.ForceNextPhase) unless PlayerPrefs hold "
                    + "\"OpenA2Door\", which the engine can't read");
            if (currency == globalStats.Currencies.GreenPower && Global(globalStats.globalUpgradeSet.GPAtomsDontResetCourses) != 0.0) return true;
            if (BoxCoursesToResetField.GetValue(box) is not courseScript[] reset) return Unmodeled("an atom box without coursesToReset (the game throws)");
            foreach (courseScript course in reset)
            {
                if ((object)course == null || (object)course.localUpgradesScript == null)
                    return Unmodeled("an atom reset of a course without local upgrades (the game throws)");
                if (!LocalPrestige(course.localUpgradesScript, false, course.tier, course.rewardTier, false, true, false)) return false;
            }
            Income[(int)globalStats.Currencies.Cash] = 0.0;
            _sim.Shop.ZeroCash();
            return true;
        }

        /// <summary>
        /// The Omni Dash box's first buy (upgradeBox.cs:833-840): Area 1 overgrown, one more refresh orb, the way back from
        /// Area 2 open, the transition's growth started and Courses 1-5 set to their overgrown tiers. Its on/off toggle is
        /// the shop's (Shop.Buy).
        /// </summary>
        internal bool OmniDashFirstBuy(upgradeBox box)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            var transition = BoxObjectReferenceField.GetValue(box) as GameObject;
            Zone2to1TransitionController controller = (object)transition == null ? null : transition.GetComponent<Zone2to1TransitionController>();
            OvergrowthLoadScript overgrowth = Singleton<OvergrowthLoadScript>.Instance;
            if ((object)stats == null || (object)controller == null || (object)overgrowth == null)
                return Unmodeled("the Omni Dash box's first buy without globalStats, its Zone2to1TransitionController or OvergrowthLoadScript (the game throws)");
            _sim.Touch(stats);
            stats.currentA1State = globalStats.area1states.Overgrown;
            _tripBroken = false;
            _global = (double[])_global.Clone();
            _global[(int)globalStats.globalUpgradeSet.moreRefreshOrbs] += 1.0;
            stats.Zone2toZone1PathOpen = true;
            return GrowTransition(controller) && SetA1CourseTiers(overgrowth);
        }

        /// <summary>
        /// Zone2to1TransitionController.doStateVisuals' coroutine to its first yield (Zone2to1TransitionController.cs:40-64),
        /// all a turbo run plays of it: the growth sound's clip draw (Audio.PlayAudio, no pitch variance), every trunk off,
        /// the first back on (its scale and shader are presentation: trunks have no colliders). Its later frames open the way
        /// at 1x (closedObject's ground collider off); a run still going by the earliest of them ends (<see cref="Step"/>).
        /// </summary>
        bool GrowTransition(Zone2to1TransitionController controller)
        {
            var trunks = TrunksField.GetValue(controller) as GameObject[];
            if (trunks == null || trunks.Length == 0 || trunks.Any(t => (object)t == null))
                return Unmodeled("the Area 2 → 1 transition without its trunks (the game throws)");
            UnityEngine.Random.Draw();
            foreach (GameObject trunk in trunks) trunk.SetActive(false);
            trunks[0].SetActive(true);
            _transitionOpensIn = TransitionOpensAfter;
            return true;
        }

        /// <summary>
        /// OvergrowthLoadScript.SetA1CourseTiers (OvergrowthLoadScript.cs:29-62): the RP atom box shown; on each of its
        /// courses (i = 0..4, its array's order) every active box that scales its cost linearly with more than one level
        /// gets 2 (1 + 0.2 i) times its base cap and buy-max, its breaker-light, prestige, movement and unlock-prestige
        /// boxes are hidden, and the course resets to tier 9.45 + 3.6 i, reward tier 6.6 + i, its clone time reset
        /// (localUpgrades.OnPrestige without the A1 prestige mod); then boxesToEnable shown.
        /// </summary>
        bool SetA1CourseTiers(OvergrowthLoadScript overgrowth)
        {
            var rpAtomBox = RpAtomBoxField.GetValue(overgrowth) as upgradeBox;
            var courses = A1CoursesField.GetValue(overgrowth) as courseScript[];
            var enable = BoxesToEnableField.GetValue(overgrowth) as upgradeBox[];
            if ((object)rpAtomBox == null || courses == null || enable == null)
                return Unmodeled("SetA1CourseTiers without its rpAtomBox, courses or boxesToEnable (the game throws)");
            Show(rpAtomBox);
            for (int i = 0; i < courses.Length; i++)
            {
                courseScript course = courses[i];
                if ((object)course == null || (object)course.localUpgradesScript == null)
                    return Unmodeled("SetA1CourseTiers on a course without local upgrades (the game throws)");
                foreach (upgradeBox b in course.transform.GetComponentsInChildren<upgradeBox>())
                {
                    if (b.upgradePowerScaleFactor == 1.0 && b.Cap > 1)
                    {
                        _sim.Touch(b);
                        b.baseCapMult = 2.0 * (1.0 + 0.2 * (double)i);
                        b.Cap = (int)Math.Round((double)b.baseCap * b.baseCapMult);
                        b.buyMax = true;
                    }
                    if (b.upgrade == localUpgrades.localUpgradeSet.activateNextBreakerLights || b.upgrade == localUpgrades.localUpgradeSet.prestige
                        || b.upgrade == localUpgrades.localUpgradeSet.Movement
                        || b.upgrade == localUpgrades.localUpgradeSet.GLOBAL && b.globalUpgrade == globalStats.globalUpgradeSet.unlockPrestige)
                    {
                        _sim.Touch(b);
                        b.visible = false;
                        b.gameObject.SetActive(false);
                    }
                }
                if (!LocalPrestige(course.localUpgradesScript, true, 9.45 + 3.6 * (double)i, 6.6 + (double)i, false, false, false)) return false;
            }
            foreach (upgradeBox b in enable)
            {
                if ((object)b == null) return Unmodeled("SetA1CourseTiers with a null box to enable (the game throws)");
                Show(b);
            }
            return true;
        }

        void Show(upgradeBox box)
        {
            _sim.Touch(box);
            box.visible = true;
            box.gameObject.SetActive(true);
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
                    ? LocalPrestige(local, true, course.tier, course.rewardTier, false, false, true)
                    : LocalPrestige(local, four, course.tier + reset1.tier, course.tier + reset1.tier, !four, false, true);
                if (!ok) return false;
                referenceBox.enabled = false;
            }
            return TripBreaker(breaker);
        }

        /// <summary>localUpgrades.OnPrestige (localUpgrades.cs:409-444) with clonesScript.onPrestige and courseScript.onPrestige
        /// (courseScript.cs:542-564): an atom prestige leaves a course exempt from it as it is.</summary>
        bool LocalPrestige(localUpgrades script, bool resetCloneTime, double newTier, double newRewardTier, bool breakerPrestige,
            bool atomPrestige, bool useA1PrestigeMod)
        {
            if (atomPrestige)
            {
                courseScript owner = script.GetComponentInParent<courseScript>();
                if ((object)owner == null) return Unmodeled("an atom reset of local upgrades without a course (the game throws)");
                if (owner.exemptFromAtomPrestige) return true;
            }
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
            _sim.Touch(course);
            if (breakerPrestige)
            {
                double tierMod = (double)BreakerLayerTierModField.GetValue(course);
                course.tier = newTier + tierMod;
                course.rewardTier = newRewardTier + tierMod + (double)BreakerLayerRewardTierModField.GetValue(course);
            }
            else if (useA1PrestigeMod)
            {
                course.tier = newTier - 1.0 + course.RewardPrestigeMod;
                course.rewardTier = newRewardTier - 1.0 + course.RewardPrestigeMod;
            }
            else
            {
                course.tier = newTier;
                course.rewardTier = newRewardTier;
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

        /// <summary>upgradeBox.onPrestige (upgradeBox.cs:966-994) without its texts and sprites.</summary>
        bool BoxPrestige(upgradeBox box, bool force = false)
        {
            if ((object)box == null) return Unmodeled("a null child box (the game throws)");
            if (!force && (box.upgrade == localUpgrades.localUpgradeSet.prestige || box.upgrade == localUpgrades.localUpgradeSet.GLOBAL
                || box.upgrade == localUpgrades.localUpgradeSet.Movement))
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
            on.gameObject.SetActive(true);
            on.isActive = true;
            on.upgradeCost = on.baseUpgradeCost;
            var onCourse = BoxCourseField.GetValue(on) as courseScript;
            if ((object)onCourse != null) on.upgradeCost *= Math.Pow(10.0, onCourse.tier + (double)BoxTierModField.GetValue(on));
            for (int i = 0; i < on.TimesUsed; i++) Shop.ScaleBoxCost(on);
            UnityEngine.Random.Draw();
            _tripBroken = true;
            if (!VmanScript.isCurrentlyVman)
            {
                Income[(int)globalStats.Currencies.Cash] = 0.0;
                _sim.Shop.ZeroCash();
            }
            _sim.Touch(breaker);
            ((bool[])ActiveLightsField.GetValue(breaker))[0] = true;
            return true;
        }

        /// <summary>
        /// SetActive(true) as the game runs it (Simulation's Activated hook, on every SetActive that turns an object on, not a
        /// restore's): the OnEnable of every behaviour it wakes runs at once, and two of them draw UnityEngine.Random:
        /// upgradeBox.OnEnable twice (the multibuy text's repeat rate, customBoxUpdate's first delay), ObjectBobber.OnEnable
        /// once, except in a Full-TAS run, where 1x and turbo give a bobber's draw back (README exception 11: a buy-max box's
        /// "buy N" text turns on from its multibuy timer at 1x only). Start (LightFlicker, RandomAnimSpeedComponent,
        /// JiggleDropScript, upgradeBox) runs before the behaviour's first Update: at 1x in the same rendered frame for an object
        /// woken in a physics step, in the next for one woken at a frame's end. A turbo run never reaches it, so its draws are
        /// not made here.
        /// </summary>
        internal static void Activate(GameObject g, bool fullTas)
        {
            foreach (upgradeBox b in g.GetComponentsInChildren<upgradeBox>(false))
                if (b.enabled) { UnityEngine.Random.Draw(); UnityEngine.Random.Draw(); }
            if (fullTas) return;
            foreach (ObjectBobber b in g.GetComponentsInChildren<ObjectBobber>(false))
                if (b.enabled) UnityEngine.Random.Draw();
        }

        static bool Under(Transform t, Transform root)
        {
            for (Transform p = t; p != null; p = p.parent) if (p == root) return true;
            return false;
        }

        internal State Capture()
        {
            var s = new State { Income = (double[])Income.Clone(), Values = new CloneValues[_clones.Length], Active = new Clone[_clones.Length][],
                Locals = (double[][])_locals.Clone(), LocalsHash = _localsHash, Global = _global, TripBroken = _tripBroken,
                TransitionOpensIn = _transitionOpensIn, CdMult = _cdMult, Atoms = (int[])_atoms.Clone(), Crafting = (bool[])_crafting.Clone(),
                CraftProgress = (float[])_craftProgress.Clone(), WaitingAtoms = _waitingAtoms, HandledTier = _handledTier,
                TierUpCounter = _tierUpCounter, IsCrafting = _isCrafting };
            for (int i = 0; i < _clones.Length; i++)
            {
                s.Values[i] = _clones[i].V;
                s.Active[i] = _clones[i].Active.Count == 0 ? Array.Empty<Clone>() : _clones[i].Active.ToArray();
            }
            return s;
        }

        internal void Restore(State s)
        {
            Array.Copy(s.Income, Income, Income.Length);
            for (int i = 0; i < _clones.Length; i++)
            {
                _clones[i].V = s.Values[i];
                _clones[i].Active.Clear();
                _clones[i].Active.AddRange(s.Active[i]);
            }
            Array.Copy(s.Locals, _locals, _locals.Length);
            _localsHash = s.LocalsHash;
            _global = s.Global;
            WriteStatic();
            _tripBroken = s.TripBroken;
            _transitionOpensIn = s.TransitionOpensIn;
            _cdMult = s.CdMult;
            _atoms = (int[])s.Atoms.Clone();
            _crafting = (bool[])s.Crafting.Clone();
            _craftProgress = (float[])s.CraftProgress.Clone();
            _waitingAtoms = s.WaitingAtoms;
            _handledTier = s.HandledTier;
            _tierUpCounter = s.TierUpCounter;
            _isCrafting = s.IsCrafting;
        }

        internal void AddTo(ref HashCode h)
        {
            foreach (double v in Income) h.Add(v);
            foreach (Clones c in _clones)
            {
                CloneValues v = c.V;
                h.Add(v.Count); h.Add(v.Pooled); h.Add(v.PathSamples); h.Add(v.ArrayFull); h.Add(v.OnScreen);
                h.Add(v.BaseInterval); h.Add(v.SinceLast); h.Add(v.NextDue); h.Add(v.OffscreenDivisor); h.Add(v.PathLength);
                h.Add(v.Red); h.Add(v.Green); h.Add(v.Blue);
                foreach (Clone clone in c.Active) { h.Add(clone.Pointer); h.Add(clone.Fastness); h.Add(clone.Bigness); h.Add(clone.Red); h.Add(clone.Green); }
                h.Add(-1);
            }
            h.Add(_localsHash);
            foreach (double g in _global) h.Add(g);
            h.Add(_tripBroken);
            h.Add(_transitionOpensIn);
            h.Add(_cdMult);
            for (int i = 0; i < _atoms.Length; i++) { h.Add(_atoms[i]); h.Add(_crafting[i]); h.Add(_craftProgress[i]); }
            h.Add(_waitingAtoms); h.Add(_handledTier); h.Add(_tierUpCounter); h.Add(_isCrafting);
        }

        int HashLocals()
        {
            var h = new HashCode();
            foreach (double[] local in _locals) foreach (double v in local) h.Add(v);
            return h.ToHashCode();
        }
    }
}
