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
        // Earliest the Omni Dash buy's Area 2->1 transition opens at 1x (Zone2to1TransitionController.cs:46-81).
        const float TransitionOpensAfter = 1f / 0.6f - 0.02f - 1f / 3f + 3f;
        static readonly int LocalCount = Enum.GetValues(typeof(localUpgrades.localUpgradeSet)).Cast<int>().Max() + 1;
        static readonly int GlobalCount = Enum.GetValues(typeof(globalStats.globalUpgradeSet)).Cast<int>().Max() + 1;

        internal struct Clone
        {
            public float Pointer;
            public int Fastness, Bigness;
            public bool Red, Green;
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
            public bool? OpenA2Door;
            public int GridDue;
            public Behaviour[] Woken, Starting;
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
        float _transitionOpensIn;
        double _cdMult;

        readonly AtomColliderSystem _atomCollider;
        readonly upgradeBox _newAtomBox;
        readonly float _craftStep, _craftStepPerTier, _craftStart, _craftStartPerTier, _craftEnd;
        readonly int _atomEffectiveness, _tierColors;
        int[] _atoms = Array.Empty<int>();
        bool[] _crafting = Array.Empty<bool>();
        float[] _craftProgress = Array.Empty<float>();
        int _waitingAtoms, _handledTier;
        double _tierUpCounter;
        bool _isCrafting;
        bool? _openA2Door;
        int _gridDue = -1;
        bool _gridNow;
        readonly HashSet<Behaviour> _neverStarted;
        Behaviour[] _woken = Array.Empty<Behaviour>();
        readonly List<Behaviour> _starting = new List<Behaviour>();
        static readonly FieldInfo AnimIsBroken = typeof(RandomAnimSpeedComponent).GetField("isBroken", Fields);
        static readonly FieldInfo BoxTree = typeof(upgradeBox).GetField("upgradeTree", Fields);

        internal readonly double[] Income = new double[7];
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

            // Update order is instance id, lowest first, as turbo steps them: it decides which course gets the fast/big rolls.
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
            // CloneDustGpCdMult stays as the start holds it; the game recomputes it only after a clone dust payout or readout.
            if ((object)own != null) _cdMult = own.CloneDustGpCdMult; else RecalcCdMult();

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
                if (economy.TryGetProperty("atomCollider", out JsonElement start))
                {
                    int[] counts = start.GetProperty("atoms").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    _atoms = Array.Empty<int>();
                    _crafting = Array.Empty<bool>();
                    _craftProgress = Array.Empty<float>();
                    GrowTiers(counts.Length);
                    Array.Copy(counts, _atoms, counts.Length);
                    bool[] startCrafting = start.GetProperty("crafting").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
                    float[] startProgress = start.GetProperty("progress").EnumerateArray().Select(World.ReadFloat).ToArray();
                    Array.Copy(startCrafting, _crafting, Math.Min(startCrafting.Length, _crafting.Length));
                    Array.Copy(startProgress, _craftProgress, Math.Min(startProgress.Length, _craftProgress.Length));
                    _waitingAtoms = start.GetProperty("waiting").GetInt32();
                    _handledTier = start.GetProperty("handledTier").GetInt32();
                    _isCrafting = start.GetProperty("isCrafting").GetBoolean();
                    _tierUpCounter = start.GetProperty("tierUpCounter").GetDouble();
                }
                if (economy.TryGetProperty("atomGridDueIn", out JsonElement due))
                    _gridDue = Math.Max(0, Mathf.FloorToInt(World.ReadFloat(due) / Time.fixedDeltaTime) - 1);
            }
            if (economy.TryGetProperty("openA2Door", out JsonElement door)) _openA2Door = door.GetBoolean();
            globalStats live = Singleton<globalStats>.Instance;
            if (economy.TryGetProperty("atomStats", out JsonElement statsJson) && (object)live != null)
            {
                _sim.Touch(live);
                double[] v = statsJson.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                live.effectiveAtomCount = v[0];
                live.grossAtomCount = v[1];
                live.AtomCountInTermsOfWhiteAtoms = v[2];
                live.NPrewardPerTick = v[3];
                live.NPrewardCap = v[4];
                live.EffectOfNpCompBoost = v[5];
                if (v.Length > 7) { live.CloneDustGpCdMult = v[6]; live.rpCap = v[7]; _cdMult = v[6]; }
            }
            if (economy.TryGetProperty("neverStarted", out JsonElement never))
            {
                _neverStarted = new HashSet<Behaviour>();
                foreach (JsonElement id in never.EnumerateArray())
                    if (world.Objects.TryGetValue(id.GetInt32(), out UnityEngine.Object o) && o is Behaviour b) _neverStarted.Add(b);
                _starting.AddRange(_neverStarted.Where(b => b.isActiveAndEnabled));
            }
            else _neverStarted = new HashSet<Behaviour>(world.All<LightFlicker>().Cast<Behaviour>().Concat(world.All<RandomAnimSpeedComponent>())
                .Concat(world.All<JiggleDropScript>()).Where(b => !b.isActiveAndEnabled));
        }

        void RecalcCdMult()
        {
            double cd = _sim.Shop.Holds(globalStats.Currencies.CloneDust);
            double m = cd == 0.0 ? 0.0 : Math.Log10(cd);
            m += Global(globalStats.globalUpgradeSet.additionalCDdigits);
            _cdMult = Math.Pow(m, 1.7) * 1.05 + 1.0;
        }

        static readonly G[] StaticReads = { G.zipMoversUnlocked, G.unlockJiggleDrops, G.unlockTeleporters };

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

        internal double LocalOf(localUpgrades script, localUpgrades.localUpgradeSet upgrade)
        {
            int i = LocalIndex(script);
            return i < 0 ? 0.0 : _locals[i][(int)upgrade];
        }

        internal double GlobalOf(globalStats.globalUpgradeSet upgrade) { return Global(upgrade); }

        internal int[] AtomCounts => (int[])_atoms.Clone();
        internal int WaitingAtoms => _waitingAtoms;
        internal bool NpCapped
        {
            get
            {
                if (Global(globalStats.globalUpgradeSet.NpCapIsSoftcap) != 0.0 || _waitingAtoms > 0 || _isCrafting) return false;
                for (int t = 0; t < _atoms.Length; t++)
                    if (_atoms[t] >= 3 + t) return false;
                return true;
            }
        }
        internal (double rate, double cap, double bonus)? NpAfterAtoms(int extra) => NpAfterAtoms(extra, null, 0, false);

        internal (double rate, double cap, double bonus)? NpAfterAtoms(int extra, G? with, double by, bool cleared)
        {
            double Level(G g) => Global(g) + (with == g ? by : 0);
            Dictionary<G, double> effect = globalStats.globalUpgradeEffectDict;
            if ((object)_atomCollider == null || effect == null || !effect.TryGetValue(G.atomLevelChance, out double chance)
                || !effect.TryGetValue(G.increasedNuclearPower, out double nuclear) || !effect.TryGetValue(G.tripleThreatIncrease, out double triple)) return null;
            double boxes = 1.0 + Level(G.increasedNuclearPower) * (nuclear / 100.0) + Level(G.tripleThreatIncrease) * (triple / 100.0);
            var counts = cleared ? new List<int>() : new List<int>(_atoms);
            double counter = cleared ? 0 : _tierUpCounter, bonus = 0;
            double Rate()
            {
                double effective = 0, white = 0;
                for (int t = 0; t < counts.Count; t++)
                {
                    effective += counts[t] * (double)Mathf.Pow(_atomEffectiveness + 10 * t, t) * (double)0.001f;
                    white += counts[t] * Factorial(2 + t) / 2.0;
                }
                return effective * (1.0 + white / 60.0) * boxes;
            }
            void Craft()
            {
                for (int t = 0; t < counts.Count; t++)
                    while (counts[t] >= 3 + t)
                    {
                        counts[t] -= 3 + t;
                        bool first = counts.Count <= t + 1;
                        if (first) counts.Add(0);
                        counts[t + 1]++;
                        if (first) bonus += Rate() * 1000.0;
                    }
            }
            Craft();
            for (int i = 0; i < (cleared ? 0 : _waitingAtoms) + extra; i++)
            {
                int tier = 0;
                counter += Level(G.atomLevelChance) * chance;
                while (counter >= 100.0) { counter -= 100.0; tier++; }
                while (counts.Count <= tier) counts.Add(0);
                counts[tier]++;
                Craft();
            }
            double rate = Rate();
            return (rate, Global(G.NpCapIsSoftcap) != 0.0 ? double.PositiveInfinity : rate * 50.0 * 120.0 * _vmanMult, bonus);
        }

        internal static double EffectMult(upgradeBox box) => (double)BoxEffectMultField.GetValue(box);

        internal int GridCalls;
        internal double CdMult => _cdMult;

        internal IEnumerable<object[]> CloneStates()
        {
            foreach (Clones c in _clones)
                if (c.V.Count > 0) yield return new object[] { c.Course?.courseNumber ?? -1, c.V.Count, c.V.PathLength, c.V.OnScreen, c.Active.Count, c.V.SinceLast };
        }

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

        internal bool? OnScreenOf(clonesScript script)
        {
            return (object)script != null && _byScript.TryGetValue(script, out Clones c) ? c.V.OnScreen : null;
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
                    // Build 25706930: a timer at two intervals or more goes to 0, else it keeps the overshoot (clonesScript.cs:212-219).
                    c.V.SinceLast = c.V.SinceLast >= c.V.NextDue * 2f ? 0f : (float)(c.V.SinceLast - (double)c.V.NextDue);
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
            if (!ColourCheck(c, out bool red, out bool green)) return false;
            double size = j * 2 + 1;
            if (red && !PayRed(c.Course.rpReward * size * boost / divisor)) return false;
            if (ExpectedGreens) Add(globalStats.Currencies.GreenPower, Math.Ceiling(c.Course.gpReward * size * _cdMult * boost / divisor) * GreenShare());
            else if (green) Add(globalStats.Currencies.GreenPower, Math.Ceiling(c.Course.gpReward * size * _cdMult * boost / divisor));
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

        double Pay(Clones c, int bigness)
        {
            double boost = Boost(c.Course);
            double pay = c.Course.reward * (double)(bigness * 2 + 1);
            double mult = 0.1 + Global(globalStats.globalUpgradeSet.cloneMult) * 0.2 + Local(c.Local, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
            pay *= mult;
            return pay * boost;
        }

        static double Boost(courseScript course) { return course.compBoostTimeLeft > 0.0 ? course.compBoostBonus : 1.0; }

        internal string DescribeClones() => string.Join(" ", _clones.Where(c => c.V.Count > 0).Select(c =>
            $"{c.Course?.courseNumber}:{c.V.Count}/{c.V.PathLength:0.##}s {(c.V.OnScreen ? "on" : "off")} gp {c.Course?.gpReward:G3} counter {c.V.Green:0.#} div {c.V.OffscreenDivisor:0.##}{(c.Script.isActiveAndEnabled ? "" : " inactive")}"));

        internal double[] CloneRates(float dt)
        {
            var rates = new double[7];
            double green = Global(globalStats.globalUpgradeSet.greenCloneClance) / 100.0;
            foreach (Clones c in _clones)
            {
                if (c.V.Count <= 0 || c.V.PathLength <= 0.1f || (object)c.Course == null || c.Local < 0 || !c.Script.isActiveAndEnabled) continue;
                double spawns = c.V.Count / (double)c.V.PathLength * dt, boost = Boost(c.Course);
                double mult = 0.1 + Global(globalStats.globalUpgradeSet.cloneMult) * 0.2 + Local(c.Local, localUpgrades.localUpgradeSet.cloneMult) * 0.1;
                rates[(int)globalStats.Currencies.Cash] += spawns * c.Course.reward * mult * boost;
                rates[(int)globalStats.Currencies.GreenPower] += spawns * green * c.Course.gpReward * _cdMult * boost;
            }
            return rates;
        }

        bool PayRed(double value)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("red power without globalStats (the game throws)");
            Add(globalStats.Currencies.RedPower, Math.Ceiling(value));
            double held = _sim.Shop.Holds(globalStats.Currencies.RedPower);
            // shortcut: exact on the cap only while the held sum and cap are integers below 2^53; use a Shop setter otherwise.
            if (held > stats.rpCap) Income[(int)globalStats.Currencies.RedPower] -= held - stats.rpCap;
            return true;
        }

        internal bool ExpectedGreens;

        double GreenShare() => Math.Max(0.0, Global(globalStats.globalUpgradeSet.greenCloneClance)) / 100.0;

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

        bool DestroyClone(Clones c, int index)
        {
            Clone clone = c.Active[index];
            if ((object)c.Course == null || c.Local < 0) return Unmodeled("clones without a course");
            double boost = Boost(c.Course), size = clone.Bigness * 2 + 1;
            Add(globalStats.Currencies.Cash, Math.Ceiling(Pay(c, clone.Bigness)));
            if (clone.Red && !PayRed(c.Course.rpReward * size * boost)) return false;
            if (ExpectedGreens) Add(globalStats.Currencies.GreenPower, Math.Ceiling(c.Course.gpReward * size * _cdMult * boost) * GreenShare());
            else if (clone.Green) Add(globalStats.Currencies.GreenPower, Math.Ceiling(c.Course.gpReward * size * _cdMult * boost));
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
                        UpdateCloneInterval(c);
                        if (c.V.Pooled + c.Active.Count < c.V.Count && !c.V.ArrayFull) c.V.Pooled++;
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
            G upgrade = box.globalUpgrade;
            if (upgrade == G.spawnNewAtom)
            {
                if (!OwnCollider(box)) return false;
                _waitingAtoms++;
                return true;
            }
            switch (upgrade)
            {
                case G.startVmanSpeedrun: return Unmodeled("a Vman speedrun's start (VmanScript.SetupVmanSpeedrun, upgradeBox.cs:710)");
                case G.openGate: return Unmodeled("the gate box (gateAnim's \"open\" trigger and deactivate, upgradeBox.cs:731-732: the gate's collider)");
                case G.area2DoorOpen:
                    if (BoxDoorToOpenField.GetValue(box) is not PlatformMover door || (object)door == null)
                        return Unmodeled("an Area 2 door box without its doorToOpen mover (the game throws)");
                    _sim.Touch(door);
                    door.ForceNextPhase();
                    return true;
                case G.unlockTeleporters:
                    _global[(int)G.unlockTeleporters] += 1.0;
                    WriteStatic();
                    if (BoxObjectReferenceField.GetValue(box) is GameObject teleporter && (object)teleporter != null)
                        teleporter.GetComponent<TeleporterScript>()?.updateVisuals();
                    return true;
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
                default:
                    _global[(int)upgrade] += 1.0 * effectMult;
                    return true;
            }
        }

        bool OwnCollider(upgradeBox box)
        {
            var collider = BoxAtomColliderField.GetValue(box) as AtomColliderSystem;
            if ((object)collider == null) return Unmodeled("an atom box without its atomColliderScript (the game throws)");
            return ReferenceEquals(collider, _atomCollider) || Unmodeled("an atom box whose collider isn't the world's AtomColliderSystem");
        }

        internal void GridStart(AtomColliderSystem collider)
        {
            if (!ReferenceEquals(collider, _atomCollider)) { Unmodeled("an AtomColliderSystem that isn't the world's woke"); return; }
            _gridNow = true;
            _gridDue = _sim.Scheduler.Tick + Math.Max(1, Mathf.FloorToInt(0.4f / Time.fixedDeltaTime));
        }

        internal void GridNow()
        {
            if (!_gridNow) return;
            _gridNow = false;
            AtomTick(_atomCollider);
        }

        internal void GridFrame()
        {
            if (_gridDue < 0 || _sim.Scheduler.Tick < _gridDue || (object)_atomCollider == null) return;
            _gridDue += Math.Max(1, Mathf.FloorToInt(0.4f / Time.fixedDeltaTime));
            AtomTick(_atomCollider);
        }

        internal void AtomTick(AtomColliderSystem collider)
        {
            if (!ReferenceEquals(collider, _atomCollider)) { Unmodeled("callAddNewAtom on a collider that isn't the world's AtomColliderSystem"); return; }
            GridCalls++;
            if (_waitingAtoms <= 0) return;
            Add(globalStats.Currencies.AtomicPower, 1.0);
            if (!AtomTier(out int tier) || !AddAtom(tier)) return;
            _waitingAtoms--;
        }

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

        bool AddAtom(int tier)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            if ((object)stats == null) return Unmodeled("an atom without globalStats (the game throws)");
            if (tier >= _tierColors) return Unmodeled("a tier " + tier + " atom (AtomColliderSystem.tierColors has " + _tierColors + ": the game throws)");
            GrowTiers(tier + 1);
            _atoms[tier]++;
            _sim.Touch(stats);
            stats.grossAtomCount += 1 + tier * (tier - 1);
            // Mono JIT keeps Pow(...)*0.001f in double precision into the double field; a float32 product drifts from turbo.
            stats.effectiveAtomCount += (double)Mathf.Pow(_atomEffectiveness + 10 * tier, tier) * (double)0.001f;
            stats.AtomCountInTermsOfWhiteAtoms += Factorial(2 + tier) / 2.0;
            return RecalcNp();
        }

        void GrowTiers(int count)
        {
            int n = _atoms.Length;
            if (count <= n) return;
            Array.Resize(ref _atoms, count);
            Array.Resize(ref _crafting, count);
            Array.Resize(ref _craftProgress, count);
            for (int i = n; i < count; i++) _craftProgress[i] = 1f;
        }

        static double Factorial(int f) { return f == 0 ? 1.0 : (double)f * Factorial(f - 1); }

        internal void AtomFrame(float dt)
        {
            if ((object)_atomCollider == null || !_atomCollider.isActiveAndEnabled || _atoms.Length <= 0) return;
            if (!HandleTier(_handledTier, dt)) return;
            _handledTier++;
            if (_handledTier >= _atoms.Length) _handledTier = 0;
        }

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

        void StartCrafting(int tier)
        {
            _isCrafting = true;
            _crafting[tier] = true;
            _craftProgress[tier] = _craftStart + _craftStartPerTier * (float)tier;
        }

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
                stats.effectiveAtomCount -= (double)Mathf.Pow(_atomEffectiveness + 10 * tier, tier) * (double)0.001f;
                stats.grossAtomCount -= 1 + tier * (tier - 1);
                stats.AtomCountInTermsOfWhiteAtoms -= Factorial(2 + tier) / 2.0;
            }
            stats.effectiveAtomCount -= (double)Mathf.Pow((float)_atomEffectiveness + 10f * (float)tier, tier) * (double)0.001f;
            stats.AtomCountInTermsOfWhiteAtoms -= Factorial(2 + tier) / 2.0;
            stats.effectiveAtomCount += (double)Mathf.Pow(_atomEffectiveness + 10 * (tier + 1), tier + 1) * (double)0.001f;
            stats.AtomCountInTermsOfWhiteAtoms += Factorial(2 + tier + 1) / 2.0;
            stats.grossAtomCount += 1 + tier * (tier - 1);
            _atoms[tier + 1]++;
            _atoms[tier] -= 3 + tier;
            if (!RecalcNp()) return false;
            if (first) Add(globalStats.Currencies.AtomicPower, stats.NPrewardPerTick * 1000.0);
            return true;
        }

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

        internal void FixedStep()
        {
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

        internal bool AtomPrestige(upgradeBox box, globalStats.Currencies currency)
        {
            if (BoxDoorToOpenField.GetValue(box) is PlatformMover door && (object)door != null && box.TimesUsed == 0 && _openA2Door != true)
            {
                if (_openA2Door == null)
                    return Unmodeled("the atom box's first buy opens its door (doorToOpen.ForceNextPhase) unless PlayerPrefs hold "
                        + "\"OpenA2Door\", and this start doesn't say whether they do (economy.openA2Door)");
                // Course 6-15 starts have the door open (RULES.md 21); without OpenA2Door the first atom buy shuts it (upgradeBox.cs:644).
                _sim.Touch(door);
                door.ForceNextPhase();
            }
            _openA2Door = true;
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

        // SetActive(true) runs woken OnEnables at once and some draw UnityEngine.Random (RULES.md exception 12).
        internal static void Activate(GameObject g, bool fullTas)
        {
            foreach (upgradeBox b in g.GetComponentsInChildren<upgradeBox>(false))
                if (b.enabled) { UnityEngine.Random.Draw(); UnityEngine.Random.Draw(); }
            if (fullTas) return;
            foreach (ObjectBobber b in g.GetComponentsInChildren<ObjectBobber>(false))
                if (b.enabled) UnityEngine.Random.Draw();
        }

        internal void Woken(GameObject g)
        {
            foreach (Behaviour b in g.GetComponentsInChildren<LightFlicker>(false).Cast<Behaviour>()
                .Concat(g.GetComponentsInChildren<RandomAnimSpeedComponent>(false)).Concat(g.GetComponentsInChildren<JiggleDropScript>(false))
                .Concat(g.GetComponentsInChildren<AtomColliderSystem>(false)).Concat(g.GetComponentsInChildren<upgradeBox>(false)))
                if (b.enabled && _neverStarted.Contains(b) && Array.IndexOf(_woken, b) < 0 && !_starting.Contains(b)) _starting.Add(b);
        }

        // Start pass runs before a frame's Updates; woken behaviours' Start draws RNG here (RULES.md exception 12).
        internal void StartPass()
        {
            if (_starting.Count == 0) return;
            List<Behaviour> started = null;
            for (int i = 0; i < _starting.Count; i++)
            {
                Behaviour b = _starting[i];
                if (!b.isActiveAndEnabled) continue;
                if (b is upgradeBox box)
                {
                    if (IGTAPTasMod.WokenStarts.Unmodeled(box) is string why) { if (_sim.Engine.IsRunning) _sim.Engine.Abort(why); return; }
                    _sim.Touch(box);
                    if (BoxTree.GetValue(box) is TreeController tree && (object)tree != null) _sim.Touch(tree);
                    IGTAPTasMod.WokenStarts.Start.Invoke(box, null);
                }
                if (b is AtomColliderSystem) RecalcNp();
                int draws = b is RandomAnimSpeedComponent anim ? ((bool)AnimIsBroken.GetValue(anim) ? 3 : 2) : b is LightFlicker || b is JiggleDropScript ? 1 : 0;
                for (int d = 0; d < draws; d++) UnityEngine.Random.Draw();
                (started ??= new List<Behaviour>()).Add(b);
                _starting.RemoveAt(i--);
            }
            if (started != null) _woken = _woken.Concat(started).ToArray();
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
                TierUpCounter = _tierUpCounter, IsCrafting = _isCrafting, OpenA2Door = _openA2Door, Woken = _woken,
                GridDue = _gridDue < 0 ? -1 : _gridDue - _sim.Scheduler.Tick - 1,
                Starting = _starting.Count == 0 ? Array.Empty<Behaviour>() : _starting.ToArray() };
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
            _openA2Door = s.OpenA2Door;
            _gridDue = s.GridDue;
            _woken = s.Woken;
            _starting.Clear();
            _starting.AddRange(s.Starting);
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
            h.Add(_openA2Door); h.Add(_woken.Length); h.Add(_starting.Count); h.Add(_gridDue < 0 ? -1 : _gridDue - _sim.Scheduler.Tick);
        }

        int HashLocals()
        {
            var h = new HashCode();
            foreach (double[] local in _locals) foreach (double v in local) h.Add(v);
            return h.ToHashCode();
        }
    }
}
