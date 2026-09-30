using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IGTAPTasMod;
using UnityEngine;
using G = globalStats.globalUpgradeSet;
using L = localUpgrades.localUpgradeSet;

namespace IGTAP.EngineSim.Cli
{
    // zip-plan (docs/fulltas.md, "Zip planner"): the buy order from a run's state in Area 2 to the zip movers
    // (buytree16, 625,000,000 NP), planned on a model of the game's economy (the decompiled formulas, below), not the engine:
    // the engine's economy doesn't run Area 2 (colour clones, clone dust, atoms and the tree end a run as unmodeled).
    static class ZipPlan
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const int W = (int)globalStats.Currencies.Cash, GP = (int)globalStats.Currencies.GreenPower, NP = (int)globalStats.Currencies.AtomicPower,
            CD = (int)globalStats.Currencies.CloneDust, Currencies = 7;
        static readonly int GlobalCount = Enum.GetValues(typeof(G)).Cast<int>().Max() + 1;
        static readonly int LocalCount = Enum.GetValues(typeof(L)).Cast<int>().Max() + 1;
        const int MaxTier = 16;
        const double Never = double.PositiveInfinity;
        static readonly string[] CurrencyNames = { "W", "GP", "NP", "#", "CD", "RP", "BP" };

        static object Get(object o, string name) { return o.GetType().GetField(name, Fields)?.GetValue(o); }
        static T Get<T>(object o, string name) { return Get(o, name) is T v ? v : default; }

        // ---------------------------------------------------------------- the world: what never changes during a plan

        sealed class Course
        {
            public int Index, Number, Area, Zone;
            public string Name;
            public double BaseReward, BaseGp, BaseCd, Tier, RewardTier;
            public double StartX, StartY;
            public double Game, Farm;   // record (the clones' path) and farm lap (quick restart to finish), seconds
            public string LapSource;
            public int[] Boxes = Array.Empty<int>();
        }

        sealed class Box
        {
            public int Index;
            public string Token, Path, Name;
            public double X, Y;
            public int Zone;
            public int Course = -1;      // the course whose tier its cost carries (upgradeBox.course)
            public int Owner = -1;       // the course whose localUpgrades holds it (resets with it)
            public bool Local;
            public L LocalKind;
            public G GlobalKind;
            public int Currency;
            public double Base, Scale, Pow, Add, Em, TierMod;
            public int Cap;
            public bool NeverBuyMax;
            public int Tree = -1, Segment = -1;   // the tree segment it sits in
            public int GrowsTree = -1;            // upgradeTree: the tree its purchase grows
            public int[] Grows = Array.Empty<int>();
            public int Target = -1;               // courseToExemptReference: the course it exempts or makes buy-max
            public bool Atom;
            public int[] Resets = Array.Empty<int>(); // coursesToReset (atom boxes)
        }

        sealed class Tree
        {
            public int Index;
            public double[] LandSeconds, DropSeconds;
            public int[] Auto;       // autoTriggerNext's segment, or -1
            public int[] SegmentBox; // the box on each segment, or -1
        }

        sealed class Data
        {
            public Course[] Courses;
            public Box[] Boxes;
            public Tree[] Trees;
            public int Zip = -1, AtomBox = -1;
            public int[] Path = Array.Empty<int>();   // the boxes from the tree's root to the zip box, in order
            public double TravelX = 9.2, TravelUp = 6, TravelDown = 20, TravelOverhead = 10;
            public Dictionary<string, double> Legs = new Dictionary<string, double>();
            public double GameSpeed = 1, VmanMult = 1;
            public HashSet<int> LapCourses = new HashSet<int>();
            public double WaitLimit = 4 * 3600;
            // Zone 2 is a drop from zone 1; the way back up (Zone2toZone1PathOpen) opens with the OmniDash box, past the zip movers.
            public bool WayBack;
            // The clones' frame (clonesScript.Update runs in each of playback's 280 fps frames, in 1x, turbo and the engine)
            // and the frames an off-screen spawn takes (timeSinceLastClone to 1 s).
            public double Frame = 1.0 / MacroEngine.PlaybackFrameRate;
            public int OffscreenFrames = FramesTo(1f, 1f / MacroEngine.PlaybackFrameRate);
            public List<Vector2[]>[] LoD;
        }

        // ---------------------------------------------------------------- the state

        sealed class State
        {
            public double T, X, Y;
            public int Zone;
            public double[] C = new double[Currencies];
            public double[] G;
            public double[][] L;
            public int[] Clones;
            public double[] Path, Best, Boost;
            public bool[] Exempt;
            public int[] Times;
            public double[] Cost, Ready;   // Ready: when the box can be touched (a growing tree segment's box), Never if not yet grown
            public double[] Touched;       // the last purchase's time (upgradeBox.boxCooldown: 0.2 s to the next)
            public bool[] Active, BuyMax;
            public double[][] SegmentGrown;
            public int[] Atoms = new int[MaxTier + 2];
            public int Lists, Waiting, CraftTier = -1;
            public double Counter, NextArrive, CraftEnd, Eff, White, NpTick, NpBoost = 1, LeftZone2 = -1;
            public int Onscreen = -1;
            public bool Done;

            public State Clone()
            {
                var s = (State)MemberwiseClone();
                s.C = (double[])C.Clone();
                s.G = (double[])G.Clone();
                s.L = L.Select(a => (double[])a.Clone()).ToArray();
                s.Clones = (int[])Clones.Clone();
                s.Path = (double[])Path.Clone(); s.Best = (double[])Best.Clone(); s.Boost = (double[])Boost.Clone();
                s.Exempt = (bool[])Exempt.Clone();
                s.Times = (int[])Times.Clone(); s.Cost = (double[])Cost.Clone(); s.Ready = (double[])Ready.Clone(); s.Touched = (double[])Touched.Clone();
                s.Active = (bool[])Active.Clone(); s.BuyMax = (bool[])BuyMax.Clone();
                s.SegmentGrown = SegmentGrown.Select(a => (double[])a.Clone()).ToArray();
                s.Atoms = (int[])Atoms.Clone();
                return s;
            }
        }

        // ---------------------------------------------------------------- formulas (decompiled game code, cited)

        static double Gl(State s, G g) { return s.G[(int)g]; }
        static double Lo(State s, int c, L l) { return s.L[c][(int)l]; }

        // courseScript.UpdateReward
        static double Reward(Data d, State s, int c)
        {
            Course k = d.Courses[c];
            double r = Math.Ceiling(k.BaseReward * (1.0 + Gl(s, G.increasedWatts) * 0.5 + Gl(s, G.tripleThreatIncrease) * 0.2)
                * (1.0 + Gl(s, G.cashPerLoop) * 0.4) * (1.0 + Lo(s, c, L.cashPerLoop) * 1.0) * (1.0 + Lo(s, c, L.moreWatts))
                * (1.0 + Gl(s, G.moreWatts) * 1.0) * d.GameSpeed * d.VmanMult * Math.Pow(10.0, k.RewardTier));
            return Math.Ceiling(Math.Pow(r, 1.0 + Gl(s, G.wattPower) * 0.01));
        }

        static double GpReward(Data d, State s, int c)
        {
            return Math.Ceiling(d.Courses[c].BaseGp * (1.0 + Lo(s, c, L.GreenCloneRewardBase))
                * (1.0 + Gl(s, G.increasedGreenPower) * 0.5 + Gl(s, G.tripleThreatIncrease) * 0.2) * (1.0 + Gl(s, G.moreGP) * 0.5)
                * d.GameSpeed * d.VmanMult);
        }

        static double CdReward(Data d, State s, int c)
        {
            return Math.Ceiling(d.Courses[c].BaseCd) * d.GameSpeed * (1.0 + Gl(s, G.IncreasedCloneDust) * 1.0) * d.VmanMult;
        }

        static double CloneMult(State s, int c) { return 0.1 + Gl(s, G.cloneMult) * 0.2 + Lo(s, c, L.cloneMult) * 0.1; }
        static double BoostBonus(State s) { return 2.0 + 0.5 * Gl(s, G.compBoostStrength); }
        static double BoostSeconds(State s) { return 30.0 + 10.0 * Gl(s, G.compBoostTime); }

        // globalStats.RecalcCloneDustMult
        static double CdMult(State s)
        {
            double m = (s.C[CD] == 0.0 ? 0.0 : Math.Log10(s.C[CD])) + Gl(s, G.additionalCDdigits);
            return Math.Pow(m, 1.7) * 1.05 + 1.0;
        }

        // clonesScript.SpawnClone's speed and size rolls: Random.Range(0, 101) < chance / (1 + k) and < 50 + 5 max, per tier.
        static double[] Roll(double chance, double max)
        {
            int tiers = 1 + (int)max;
            var p = new double[tiers + 1];
            double reach = 1.0;
            for (int k = 0; k < tiers; k++)
            {
                double x = Math.Min(chance / (1.0 + k), 50.0 + max * 5.0);
                double pass = x <= 0 ? 0 : Math.Min(101.0, Math.Ceiling(x)) / 101.0;
                p[k] = reach * (1 - pass);
                reach *= pass;
            }
            p[tiers] = reach;
            return p;
        }

        // Frames until clonesScript.Update's timeSinceLastClone (+= deltaTime a frame, from 0 at a spawn) reaches `due`, in the
        // game's float arithmetic.
        static int FramesTo(float due, float frame)
        {
            float t = 0f;
            int n = 0;
            while (!(t >= due) && n < 10000000) { t += frame; n++; }
            return n;
        }

        // Expected payout per second of one course's clones in one currency, frame by frame as clonesScript.Update steps them
        // (one per rendered frame). Off screen (SpawnClone's else branch) a clone spawns
        // once timeSinceLastClone reaches 1 s and pays ceil(value (2j+1) / the previous spawn's interval); on screen one spawns
        // once it reaches the rolled interval, if the pool has one free (the oldest clone leaves on the frame after its spawn
        // check), and pays ceil(value (2j+1)) at its path's end.
        static double ClonePay(Data d, double value, double path, int count, double[] fast, double[] big, bool onscreen)
        {
            if (value <= 0) return 0;
            float frame = (float)d.Frame;
            float baseInterval = (float)path / count;
            if (onscreen)
            {
                double pay = 0, frames = 0;
                for (int j = 0; j < big.Length; j++) pay += big[j] * Math.Ceiling(value * (2 * j + 1));
                for (int i = 0; i < fast.Length; i++)
                    for (int j = 0; j < big.Length; j++)
                    {
                        if (fast[i] * big[j] == 0) continue;
                        float due = (float)(baseInterval / (1.0 + i * (double)0.35f));
                        due = (float)(due / Math.Pow(0.75, j));
                        frames += fast[i] * big[j] * Math.Max(1.0, Math.Ceiling(due / frame - 1e-6));
                    }
                // A full pool: `count` spawns take no longer than the path, so each spawn waits a frame for the oldest to leave.
                double pathFrames = Math.Round(path / 0.02) * 0.02 / frame;
                if (count * frames <= pathFrames + 1e-9) frames += 1;
                return pay / (frames * frame);
            }
            double sum = 0;
            for (int j = 0; j < big.Length; j++)
            {
                if (big[j] == 0) continue;
                for (int i2 = 0; i2 < fast.Length; i2++)
                {
                    if (fast[i2] == 0) continue;
                    for (int j2 = 0; j2 < big.Length; j2++)
                    {
                        if (big[j2] == 0) continue;
                        float due = (float)(baseInterval / (1.0 + i2 * (double)0.35f));
                        due = (float)(due / Math.Pow(0.75, j2));
                        sum += big[j] * fast[i2] * big[j2] * Math.Ceiling(value * (2 * j + 1) / due);
                    }
                }
            }
            return sum / (d.OffscreenFrames * frame);
        }

        struct Rates { public double W, GP, CD; }

        // clonesLoD: a course's clones run on screen while the player is inside its activeCloneArea (and while it laps it).
        static bool OnScreen(Data d, State s, int c)
        {
            if (s.Onscreen == c) return true;
            foreach (Vector2[] poly in d.LoD[c])
            {
                bool inside = false;
                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                    if ((poly[i].y > s.Y) != (poly[j].y > s.Y)
                        && s.X < (poly[j].x - poly[i].x) * (s.Y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) inside = !inside;
                if (inside) return true;
            }
            return false;
        }

        static Rates Income(Data d, State s)
        {
            var r = new Rates();
            double cdMult = CdMult(s), green = Math.Min(1.0, Math.Max(0.0, Gl(s, G.greenCloneClance)) / 100.0);
            for (int c = 0; c < d.Courses.Length; c++)
            {
                if (s.Clones[c] <= 0 || !(s.Path[c] > 0.1)) continue;
                double[] fast = Roll(Lo(s, c, L.fastCloneChance) * 5.0 + Gl(s, G.fastCloneChance) * 8.0, Gl(s, G.maxCloneFastness));
                double[] big = Roll(Lo(s, c, L.bigCloneChance) * 5.0 + Gl(s, G.bigCloneChance) * 8.0, Gl(s, G.maxCloneBigness));
                double boost = s.Boost[c] > 0 ? BoostBonus(s) : 1.0;
                bool on = OnScreen(d, s, c);
                r.W += ClonePay(d, Reward(d, s, c) * CloneMult(s, c) * boost, s.Path[c], s.Clones[c], fast, big, on);
                if (green > 0) r.GP += green * ClonePay(d, GpReward(d, s, c) * cdMult * boost, s.Path[c], s.Clones[c], fast, big, on);
                if (Lo(s, c, L.enableCloneDustGeneration) >= 1.0) r.CD += ClonePay(d, CdReward(d, s, c) * boost * cdMult, s.Path[c], s.Clones[c], fast, big, on);
            }
            return r;
        }

        // globalStats.RecalcNpRewardPerTick
        static void RecalcNp(State s)
        {
            s.NpTick = s.Eff * (1.0 + s.White / 60.0) * (1.0 + Gl(s, G.increasedNuclearPower) * 0.25 + Gl(s, G.tripleThreatIncrease) * 0.2);
        }

        static double NpCap(Data d, State s) { return s.NpTick * 6000.0 * d.VmanMult; }

        // NP per second from globalStats.DoConstantCurrency at the NP held now
        static double NpRate(Data d, State s)
        {
            double tick = s.NpTick * (1.0 + Gl(s, G.NpGenerationSpeed)) * d.GameSpeed;
            if (Gl(s, G.compBoostDoublesNP) != 0.0) tick *= s.NpBoost;
            double cap = NpCap(d, s);
            if (s.C[NP] < cap) return tick * 50.0;
            if (Gl(s, G.NpCapIsSoftcap) != 0.0 && s.C[NP] > 0) return tick * 50.0 * Math.Pow(cap / s.C[NP], 3.0);
            return 0;
        }

        // AtomColliderSystem.AddNewAtom's effectiveAtomCount and AtomCountInTermsOfWhiteAtoms per atom of a tier
        static double AtomEff(int tier) { return (float)((float)Math.Pow(100 + 10 * tier, tier) * 0.001f); }
        static double AtomWhite(int tier) { double f = 1; for (int i = 2; i <= 2 + tier; i++) f *= i; return f / 2.0; }
        // HandleTierLogic's crafting: progress from craftingStartPoint + startPointPerTier * tier grows by
        // max(craftingProgressPerStep + craftingProgressPerStepPerTier * tier, 5) per second (exponentially) to craftingEndValue.
        static double CraftSeconds(int tier) { return Math.Log(45.0 / (0.2 + 0.1 * tier)) / Math.Max(8.0 - 0.8 * tier, 5.0); }

        static int NextAtomTier(State s)
        {
            s.Counter += Gl(s, G.atomLevelChance) * 25.0;
            int t = 0;
            while (s.Counter >= 100.0) { s.Counter -= 100.0; t++; }
            return Math.Min(t, MaxTier);
        }

        static void AddAtom(State s, int tier)
        {
            while (s.Lists <= tier) s.Lists++;
            s.Atoms[tier]++;
            s.Eff += AtomEff(tier);
            s.White += AtomWhite(tier);
            RecalcNp(s);
        }

        static void TryCraft(State s)
        {
            if (s.CraftTier >= 0) return;
            for (int t = 0; t < MaxTier; t++)
                if (s.Atoms[t] >= 3 + t) { s.CraftTier = t; s.CraftEnd = s.T + CraftSeconds(t); return; }
        }

        static void FinishCraft(State s)
        {
            int t = s.CraftTier;
            s.CraftTier = -1;
            bool first = s.Lists <= t + 1;
            if (first) s.Lists++;
            s.Atoms[t] -= 3 + t;
            s.Atoms[t + 1]++;
            s.Eff += AtomEff(t + 1) - (3 + t) * AtomEff(t);
            RecalcNp(s);
            if (first) s.C[NP] += s.NpTick * 1000.0;
        }

        // ---------------------------------------------------------------- time

        static double NextEvent(Data d, State s)
        {
            double next = Never;
            if (s.Zone == 2)
            {
                if (s.Waiting > 0) next = Math.Min(next, s.NextArrive);
                if (s.CraftTier >= 0) next = Math.Min(next, s.CraftEnd);
            }
            for (int c = 0; c < s.Boost.Length; c++) if (s.Boost[c] > 0) next = Math.Min(next, s.T + s.Boost[c]);
            return next;
        }

        /// <summary>Runs the economy to time `until`: clone income, NP, atoms arriving and crafting, comp boosts running out.</summary>
        static void Advance(Data d, State s, double until)
        {
            while (s.T < until - 1e-9)
            {
                double next = Math.Min(until, NextEvent(d, s));
                Rates r = Income(d, s);
                // Clone dust feeds its own multiplier (and green power's): step so it grows by at most 5% at a time.
                if (r.CD > 0) next = Math.Min(next, s.T + Math.Max(0.05, 0.05 * Math.Max(s.C[CD], 1.0) / r.CD));
                double np = NpRate(d, s), cap = NpCap(d, s);
                if (np > 0 && s.C[NP] < cap) next = Math.Min(next, s.T + Math.Max(0.02, (cap - s.C[NP]) / np));
                double h = next - s.T;
                s.C[W] += r.W * h;
                s.C[GP] += r.GP * h;
                s.C[CD] += r.CD * h;
                if (np > 0)
                {
                    bool below = s.C[NP] < cap;
                    s.C[NP] += np * h;
                    if (below && s.C[NP] > cap) s.C[NP] = cap + np / 50.0;
                }
                for (int c = 0; c < s.Boost.Length; c++)
                {
                    if (!(s.Boost[c] > 0)) continue;
                    s.Boost[c] -= h;
                    if (s.Boost[c] <= 1e-9) { s.Boost[c] = 0; s.NpBoost = Math.Max(1.0, s.NpBoost / 2.0); }
                }
                s.T = next;
                if (s.Zone != 2) continue;
                while (s.Waiting > 0 && s.NextArrive <= s.T + 1e-9)
                {
                    // AtomColliderSystem.callAddNewAtom, every 0.4 s
                    s.C[NP] += 1.0;
                    AddAtom(s, NextAtomTier(s));
                    s.Waiting--;
                    s.NextArrive += 0.4;
                    TryCraft(s);
                }
                if (s.CraftTier >= 0 && s.CraftEnd <= s.T + 1e-9) { FinishCraft(s); TryCraft(s); }
            }
        }

        static double TravelSeconds(Data d, double x0, double y0, double x1, double y1, string key = null)
        {
            if (key != null && d.Legs.TryGetValue(key, out double leg)) return leg;
            double dx = Math.Abs(x1 - x0), dy = y1 - y0;
            if (dx < 1 && Math.Abs(dy) < 1) return 0;
            double ticks = d.TravelOverhead + dx / d.TravelX + (dy > 0 ? dy / d.TravelUp : -dy / d.TravelDown);
            return ticks * 0.02;
        }

        // ---------------------------------------------------------------- purchases (upgradeBox)

        static bool Available(State s, int b) { return s.Active[b] && s.Ready[b] <= s.T + 1e-9; }

        static void ScaleCost(Box b, State s)
        {
            double c = s.Cost[b.Index] + b.Add;
            c = Math.Pow(c, b.Pow);
            s.Cost[b.Index] = b.Local ? Math.Ceiling(Math.Ceiling(c * b.Scale)) : Math.Round(c * b.Scale);
        }

        static void Grow(Data d, State s, int tree, int segment, double at)
        {
            Tree t = d.Trees[tree];
            if (segment < 0 || segment >= t.LandSeconds.Length || s.SegmentGrown[tree][segment] < Never) return;
            double land = at + t.LandSeconds[segment];
            s.SegmentGrown[tree][segment] = land;
            if (t.SegmentBox[segment] >= 0) s.Ready[t.SegmentBox[segment]] = land + t.DropSeconds[segment];
            if (t.Auto[segment] >= 0) Grow(d, s, tree, t.Auto[segment], land);
        }

        // upgradeBox.doNewAtomPrestige + localUpgrades.OnPrestige(isAtomPrestige) + clonesScript.onPrestige(false)
        static void AtomPrestige(Data d, State s, Box atom)
        {
            foreach (int c in atom.Resets)
            {
                if (s.Exempt[c]) continue;
                Array.Clear(s.L[c], 0, s.L[c].Length);
                s.Clones[c] = 0;
                foreach (int b in d.Courses[c].Boxes)
                {
                    Box x = d.Boxes[b];
                    if (!x.Local) continue;
                    s.Times[b] = 0;
                    s.Cost[b] = x.Base * (x.Course >= 0 ? Math.Pow(10.0, d.Courses[x.Course].Tier + x.TierMod) : 1.0);
                    s.Active[b] = true;
                }
            }
            s.C[W] = 0.0;
        }

        // AtomColliderSystem.ClearAtoms (the atom level box)
        static void ClearAtoms(Data d, State s)
        {
            Array.Clear(s.Atoms, 0, s.Atoms.Length);
            s.Lists = 0; s.Counter = 0; s.Waiting = 0; s.CraftTier = -1; s.Eff = 0; s.White = 0;
            RecalcNp(s);
            if (d.AtomBox >= 0)
            {
                Box a = d.Boxes[d.AtomBox];
                s.Times[a.Index] = 1;
                s.Cost[a.Index] = a.Base;
                s.Active[a.Index] = true;
            }
        }

        // upgradeBox.DoBuyUpgrade for the modelled upgrades
        static void DoBuy(Data d, State s, Box b)
        {
            s.Times[b.Index]++;
            s.C[b.Currency] -= s.Cost[b.Index];
            if (b.Local)
            {
                int c = b.Owner;
                s.L[c][(int)b.LocalKind] += b.Em;
                if (b.LocalKind == L.cloneCount) s.Clones[c] += (int)Math.Ceiling(b.Em);
                ScaleCost(b, s);
            }
            else
            {
                switch (b.GlobalKind)
                {
                    case G.spawnNewAtom:
                        // AtomColliderSystem.OnEnable: InvokeRepeating("callAddNewAtom", 0, 0.4), on the 0.4 s grid
                        if (s.Waiting == 0 && s.NextArrive < s.T) s.NextArrive = Math.Ceiling(s.T / 0.4 - 1e-9) * 0.4;
                        s.Waiting++;
                        break;
                    case G.atomLevelChance:
                        s.G[(int)b.GlobalKind] += b.Em;
                        ClearAtoms(d, s);
                        break;
                    case G.area2DoorOpen:
                        break;
                    case G.exemptCourseFromAtomPrestige:
                        if (b.Target >= 0) s.Exempt[b.Target] = true;
                        break;
                    case G.makeCourseBuyMax:
                        if (b.Target >= 0)
                            foreach (int x in d.Courses[b.Target].Boxes)
                                if (d.Boxes[x].Local) s.BuyMax[x] = true;
                        break;
                    default:
                        s.G[(int)b.GlobalKind] += b.Em;
                        break;
                }
                if (b.GlobalKind == G.increasedNuclearPower || b.GlobalKind == G.tripleThreatIncrease) RecalcNp(s);
                double c = s.Cost[b.Index] + b.Add;
                c = Math.Pow(c, b.Pow);
                s.Cost[b.Index] = Math.Round(c * b.Scale);
            }
            if (s.Times[b.Index] >= b.Cap) s.Active[b.Index] = false;
            if (b.Index == d.Zip) s.Done = true;
        }

        /// <summary>One touch of a box (upgradeBox.buyUpgrade): the atom box's prestige, then one purchase or, buy-max, all it can pay for.</summary>
        static int Touch(Data d, State s, int bi)
        {
            Box b = d.Boxes[bi];
            if (!Available(s, bi) || !(s.C[b.Currency] >= s.Cost[bi])) return 0;
            if (b.Atom && (b.Currency != GP || Gl(s, G.GPAtomsDontResetCourses) == 0.0)) AtomPrestige(d, s, b);
            int n = 0;
            if (s.BuyMax[bi] && !b.NeverBuyMax)
            {
                // upgradeBox.GetNumPlayerCouldBuy counts on unrounded costs; IE_BuyMax then buys that many while it can pay.
                double cash = s.C[b.Currency], cost = s.Cost[bi];
                int count = 0;
                while (cash >= cost && s.Times[bi] + count < b.Cap) { count++; cash -= cost; cost = Math.Pow(cost + b.Add, b.Pow) * b.Scale; }
                for (int i = 0; i < count && s.Active[bi] && s.C[b.Currency] >= s.Cost[bi]; i++) { DoBuy(d, s, b); n++; }
            }
            else { DoBuy(d, s, b); n = 1; }
            if (b.GrowsTree >= 0) foreach (int g in b.Grows) Grow(d, s, b.GrowsTree, g, s.T);
            return n;
        }

        // ---------------------------------------------------------------- the plan

        enum Kind : byte { Buy, BuyAll, Lap, Atoms }

        readonly struct Step
        {
            public readonly Kind Kind;
            public readonly int Arg;
            public Step(Kind kind, int arg) { Kind = kind; Arg = arg; }
            public override string ToString() { return Kind + " " + Arg; }
        }

        sealed class Record
        {
            public double Start, End;
            public string What, Goal;
            public string Detail;
        }

        sealed class Result
        {
            public double Time = Never;
            public bool Feasible;
            public double Progress;
            public List<Record> Records;
            public State End;
        }

        sealed class Executor
        {
            readonly Data _d;
            readonly State _s;
            readonly List<Record> _log;
            public string Failed;

            public Executor(Data d, State start, bool record)
            {
                _d = d;
                _s = start.Clone();
                _log = record ? new List<Record>() : null;
            }

            public State S { get { return _s; } }
            public List<Record> Records { get { return _log; } }

            void Note(double start, string what, string goal, string detail = null)
            {
                _log?.Add(new Record { Start = start, End = _s.T, What = what, Goal = goal, Detail = detail ?? Snapshot() });
            }

            string Snapshot()
            {
                Rates r = Income(_d, _s);
                return $"W {_s.C[W]:G3} ({r.W:G3}/s)  GP {_s.C[GP]:G3} ({r.GP:G3}/s)  NP {_s.C[NP]:G4} ({NpRate(_d, _s):G3}/s, cap {NpCap(_d, _s):G3})"
                    + (_s.C[CD] > 0 ? $"  CD {_s.C[CD]:G3} ({r.CD:G3}/s)" : "") + $"  atoms {AtomText(_s)}";
            }

            void MoveTo(double x, double y, int zone, string key)
            {
                double t = TravelSeconds(_d, _s.X, _s.Y, x, y, key);
                if (t > 0) Advance(_d, _s, _s.T + t);
                _s.X = x; _s.Y = y;
                if (zone == _s.Zone) return;
                // The atom collider (zone 2) is off while zone 1 is loaded: its arrivals and crafting wait.
                if (zone != 2) _s.LeftZone2 = _s.T;
                else if (_s.LeftZone2 >= 0)
                {
                    double paused = _s.T - _s.LeftZone2;
                    _s.NextArrive += paused;
                    _s.CraftEnd += paused;
                    _s.LeftZone2 = -1;
                }
                _s.Zone = zone;
            }

            string Token(int b) { return _d.Boxes[b].Token; }

            bool Reachable(int zone) { return _d.WayBack || _s.Zone != 2 || zone == 2; }

            public bool Lap(int c)
            {
                Course k = _d.Courses[c];
                double t0 = _s.T;
                if (!Reachable(k.Zone)) return true;
                bool here = Math.Abs(_s.X - k.StartX) < 1 && Math.Abs(_s.Y - k.StartY) < 1;
                MoveTo(k.StartX, k.StartY, k.Zone, "start:" + k.Number);
                double lap = here ? k.Farm : k.Game;
                _s.Onscreen = c;
                Advance(_d, _s, _s.T + lap);
                _s.Onscreen = -1;
                // courseScript.stopTracking
                double boost = _s.Boost[c] > 0 ? BoostBonus(_s) : 1.0;
                _s.C[W] += Math.Ceiling(Reward(_d, _s, c) * boost);
                if (Lo(_s, c, L.enableCloneDustGeneration) >= 1.0) _s.C[CD] += Math.Ceiling(CdReward(_d, _s, c) * 10.0 * boost * CdMult(_s));
                if (Gl(_s, G.compBoostUnlocked) != 0.0)
                {
                    if (_s.Boost[c] <= 0) _s.NpBoost *= 2.0;
                    _s.Boost[c] = BoostSeconds(_s);
                }
                if (k.Game < _s.Best[c]) { _s.Best[c] = k.Game; _s.Path[c] = k.Game; }
                if (_log != null) Note(t0, "lap Course " + k.Number, "start:" + k.Number + " finish:" + k.Number);
                return true;
            }

            /// <summary>
            /// Waits until the run holds `need` of a currency: laps the course that earns it fastest (its reward, and its clones'
            /// comp boost), or stands still when no lap helps (NP, or GP before the comp boost). False past the wait limit.
            /// </summary>
            public bool Earn(int currency, double need, double atX, double atY)
            {
                double limit = _s.T + _d.WaitLimit;
                while (_s.C[currency] < need)
                {
                    if (_s.T > limit) { Failed = $"waited {_d.WaitLimit:0} s for {need:G4} {CurrencyNames[currency]} (holds {_s.C[currency]:G4})"; return false; }
                    double rate = RateOf(currency);
                    double idle = rate > 0 ? (need - _s.C[currency]) / rate : Never;
                    int best = -1;
                    double bestValue = 0, bestTime = 0;
                    if (currency != NP)
                        foreach (int c in _d.LapCourses)
                        {
                            Course k = _d.Courses[c];
                            if (!Reachable(k.Zone)) continue;
                            bool here = Math.Abs(_s.X - k.StartX) < 1 && Math.Abs(_s.Y - k.StartY) < 1;
                            double time = (here ? k.Farm : k.Game + TravelSeconds(_d, _s.X, _s.Y, k.StartX, k.StartY, "start:" + k.Number))
                                + TravelSeconds(_d, k.StartX, k.StartY, atX, atY);
                            double value = LapValue(c, currency, Math.Min(idle, 600));
                            if (value <= 0) continue;
                            // A lap has to beat standing: its own earnings plus what the clones make meanwhile.
                            if (value / time > bestValue / Math.Max(bestTime, 1e-9) || best < 0) { best = c; bestValue = value; bestTime = time; }
                        }
                    if (best >= 0 && idle > bestTime && bestValue > 0)
                    {
                        Lap(best);
                        continue;
                    }
                    if (idle == Never)
                    {
                        double next = NextEvent(_d, _s);
                        if (next == Never) { Failed = $"no income of {CurrencyNames[currency]} to reach {need:G4}"; return false; }
                        Advance(_d, _s, next);
                        continue;
                    }
                    Advance(_d, _s, Math.Min(_s.T + Math.Max(idle, 0.02), Math.Min(limit + 1, NextEventOrFar())));
                }
                return true;
            }

            double NextEventOrFar() { double n = NextEvent(_d, _s); return n == Never ? Never : Math.Max(n, _s.T + 0.02); }

            double RateOf(int currency)
            {
                if (currency == NP) return NpRate(_d, _s);
                Rates r = Income(_d, _s);
                return currency == W ? r.W : currency == GP ? r.GP : currency == CD ? r.CD : 0;
            }

            // What a lap of course c earns of a currency: its finish reward and, with the comp boost, its clones' extra income for
            // as long as the wait lasts (up to the boost's length).
            double LapValue(int c, int currency, double horizon)
            {
                double direct = 0;
                double boost = _s.Boost[c] > 0 ? BoostBonus(_s) : 1.0;
                if (currency == W) direct = Math.Ceiling(Reward(_d, _s, c) * boost);
                else if (currency == CD && Lo(_s, c, L.enableCloneDustGeneration) >= 1.0) direct = Math.Ceiling(CdReward(_d, _s, c) * 10.0 * boost * CdMult(_s));
                double extra = 0;
                if (Gl(_s, G.compBoostUnlocked) != 0.0 && _s.Clones[c] > 0 && _s.Path[c] > 0.1)
                {
                    State probe = _s.Clone();
                    probe.Boost[c] = 0;
                    Rates off = Income(_d, probe);
                    probe.Boost[c] = 1;
                    Rates on = Income(_d, probe);
                    double gain = currency == W ? on.W - off.W : currency == GP ? on.GP - off.GP : currency == CD ? on.CD - off.CD : 0;
                    double left = Math.Max(0, _s.Boost[c]);
                    extra = gain * Math.Max(0, Math.Min(BoostSeconds(_s), horizon) - left);
                }
                return direct + extra;
            }

            /// <summary>Goes to a box and buys it once (or, buy-max, all it can pay for); `all`: then every further level it can pay for.</summary>
            public bool Buy(int bi, bool all)
            {
                Box b = _d.Boxes[bi];
                if (!_s.Active[bi] || _s.Ready[bi] == Never || !Reachable(b.Zone)) return true;   // capped, not grown or out of reach: skipped
                if (_s.Ready[bi] > _s.T)
                {
                    MoveTo(b.X, b.Y, b.Zone, Token(bi));
                    if (_s.Ready[bi] > _s.T) Advance(_d, _s, _s.Ready[bi]);
                }
                if (_s.C[b.Currency] < _s.Cost[bi] && !Earn(b.Currency, _s.Cost[bi], b.X, b.Y)) return false;
                double start = _s.T;
                MoveTo(b.X, b.Y, b.Zone, Token(bi));
                if (_s.C[b.Currency] < _s.Cost[bi] && !Earn(b.Currency, _s.Cost[bi], b.X, b.Y)) return false;
                if (_s.X != b.X || _s.Y != b.Y) MoveTo(b.X, b.Y, b.Zone, Token(bi));
                if (_s.T < _s.Touched[bi] + 0.2) Advance(_d, _s, _s.Touched[bi] + 0.2);
                if (_s.C[b.Currency] < _s.Cost[bi] && !Earn(b.Currency, _s.Cost[bi], b.X, b.Y)) return false;
                int n = Touch(_d, _s, bi);
                _s.Touched[bi] = _s.T;
                if (_log != null) Note(start, "buy " + Describe(bi) + (n > 1 ? " x" + n : ""), b.Token == null ? null : "buy:" + b.Token);
                while (all && !(_s.BuyMax[bi] && !b.NeverBuyMax) && _s.Active[bi] && _s.C[b.Currency] >= _s.Cost[bi])
                {
                    // upgradeBox.OnTriggerStay2D: one purchase per 0.2 s box cooldown while standing in it
                    double t1 = _s.T;
                    Advance(_d, _s, _s.T + 0.2);
                    if (!(_s.C[b.Currency] >= _s.Cost[bi])) break;
                    Touch(_d, _s, bi);
                    _s.Touched[bi] = _s.T;
                    if (_log != null) Note(t1, "buy " + Describe(bi), b.Token == null ? null : "buy:" + b.Token);
                }
                return true;
            }

            string Describe(int bi)
            {
                Box b = _d.Boxes[bi];
                string what = b.Local ? b.LocalKind.ToString() : b.GlobalKind.ToString();
                return (b.Token ?? b.Path) + " (" + what + (b.Owner >= 0 ? ", Course " + _d.Courses[b.Owner].Number : "") + ", now " + _s.Times[bi] + "/" + b.Cap + ")";
            }

            /// <summary>Saves GP for `count` atoms (the atom box buys all it can pay for in one touch) and touches it.</summary>
            public bool Atoms(int count)
            {
                if (_d.AtomBox < 0) return true;
                Box a = _d.Boxes[_d.AtomBox];
                if (!Available(_s, a.Index)) return true;
                double need = AtomCost(_s, a, count);
                if (!Earn(a.Currency, need, a.X, a.Y)) return false;
                return Buy(a.Index, false);
            }

            public bool Run(IEnumerable<Step> plan)
            {
                foreach (Step p in plan)
                {
                    if (_s.Done) return true;
                    bool ok = p.Kind switch
                    {
                        Kind.Buy => Buy(p.Arg, false),
                        Kind.BuyAll => Buy(p.Arg, true),
                        Kind.Lap => Lap(p.Arg),
                        Kind.Atoms => Atoms(p.Arg),
                        _ => true,
                    };
                    if (!ok) return false;
                }
                return Finish();
            }

            /// <summary>
            /// The autopilot: the rest of the way from wherever the plan left off. Each round it buys the next box on the tree's way to
            /// the zip box once it can pay for it; with no atoms, the atom box's first (free) atom; when the NP cap can't reach the zip
            /// box's price, either the GP investment (or a course's first clone and lap) that brings the atoms it needs soonest, or
            /// those atoms (saved for in GP, bought in one touch). The plan's own steps come first; the search improves on this.
            /// </summary>
            bool Finish()
            {
                if (_d.Zip < 0) { Failed = "no zip box"; return false; }
                double limit = _s.T + _d.WaitLimit;
                for (int round = 0; !_s.Done; round++)
                {
                    if (round > 4000 || _s.T > limit) { Failed = "the autopilot ran out of rounds or time"; return false; }
                    int at = Array.FindIndex(_d.Path, b => _s.Times[b] == 0 && _s.Active[b]);
                    if (at < 0) { Failed = "the tree's way to the zip box is bought but the zip box isn't"; return false; }
                    int next = _d.Path[at];
                    Box nb = _d.Boxes[next];
                    if (_s.Ready[next] == Never) { Failed = "the tree's way to the zip box is cut at " + nb.Token; return false; }
                    double target = _s.Cost[next];
                    // The next box on the way, once the run holds its price or will (NP within the cap of the atoms held and coming).
                    if (nb.Currency != NP || _s.C[NP] >= target || NpReaches(target))
                    {
                        if (!Buy(next, false)) return false;
                        continue;
                    }
                    Box atom = _d.AtomBox >= 0 ? _d.Boxes[_d.AtomBox] : null;
                    if (atom == null) { Failed = "no atom box: NP can't reach " + nb.Token; return false; }
                    if (_s.Eff == 0 && _s.Waiting == 0 && Available(_s, atom.Index) && _s.Cost[atom.Index] <= _s.C[atom.Currency])
                    {
                        if (!Atoms(1)) return false;
                        continue;
                    }
                    // Atoms for the next box's price under the NP cap: the GP investment that pays for them soonest, or them.
                    int k = AtomsForCap(_d, _s, target);
                    if (k < 0) { Failed = "no atom count reaches the NP cap " + nb.Token + " needs"; return false; }
                    (int box, int course, int levels)? invest = k == 0 ? null : BestInvestment(k, target);
                    if (invest == null)
                    {
                        if (!Atoms(Math.Max(1, k))) return false;
                        continue;
                    }
                    var (ib, ic, levels) = invest.Value;
                    for (int i = 0; i < levels && ib >= 0; i++) if (!Buy(ib, false)) return false;
                    if (ic >= 0 && !Lap(ic)) return false;
                }
                return true;
            }

            // Whether NP reaches `target` without more atoms: the NP cap of the atoms held and on their way, once merged.
            bool NpReaches(double target)
            {
                if (_s.C[NP] + _s.Waiting >= target) return true;
                State s = _s.Clone();
                SettleAtoms(s);
                return NpCap(_d, s) >= target && s.NpTick > 0;
            }

            // The W a lap earns per second, at the best course to lap (for waits on W).
            double LapRate(int currency)
            {
                double best = 0;
                foreach (int c in _d.LapCourses)
                {
                    Course k = _d.Courses[c];
                    if (!Reachable(k.Zone)) continue;
                    double pay = currency == W ? Reward(_d, _s, c)
                        : currency == CD && Lo(_s, c, L.enableCloneDustGeneration) >= 1.0 ? CdReward(_d, _s, c) * 10.0 * CdMult(_s) : 0;
                    best = Math.Max(best, pay / k.Farm);
                }
                return best;
            }

            /// <summary>
            /// When the atoms the zip box needs cost more GP than the run holds: the purchase (or a course's first clone and first lap)
            /// after which they're paid for soonest, counting the wait for its own price; null when saving is sooner.
            /// </summary>
            (int box, int course, int levels)? BestInvestment(int k, double target)
            {
                Box atom = _d.Boxes[_d.AtomBox];
                Rates now = Income(_d, _s);
                double[] rate = new double[Currencies];
                rate[W] = now.W + LapRate(W);
                rate[GP] = now.GP;
                rate[CD] = now.CD + LapRate(CD);
                rate[NP] = NpRate(_d, _s);
                double costGp = AtomCost(_s, atom, k);
                double baseline = _s.C[GP] >= costGp ? 0 : now.GP > 0 ? (costGp - _s.C[GP]) / now.GP : Never;
                if (baseline == 0) return null;
                double bestTime = baseline * 0.999;
                (int, int, int)? best = null;
                // Several levels at once: off-screen payouts are ceil'd per spawn, so one more clone or reward level often pays
                // nothing until a few more come with it. False when even the wait for these levels is too long.
                bool Try(int box, int course, int levels)
                {
                    double wait = 0, x = _s.X, y = _s.Y;
                    if (box >= 0)
                    {
                        Box b = _d.Boxes[box];
                        if (_s.Times[box] + levels > b.Cap) return false;
                        double total = 0, cost = _s.Cost[box];
                        for (int i = 0; i < levels; i++)
                        {
                            total += cost;
                            cost = b.Local ? Math.Ceiling(Math.Ceiling(Math.Pow(cost + b.Add, b.Pow) * b.Scale)) : Math.Round(Math.Pow(cost + b.Add, b.Pow) * b.Scale);
                        }
                        double need = total - _s.C[b.Currency];
                        if (need > 0) wait = rate[b.Currency] > 0 ? need / rate[b.Currency] : Never;
                        if (wait == Never) return false;
                        wait += TravelSeconds(_d, x, y, b.X, b.Y) + 0.2 * (levels - 1);
                        x = b.X; y = b.Y;
                    }
                    if (course >= 0)
                    {
                        Course c = _d.Courses[course];
                        wait += TravelSeconds(_d, x, y, c.StartX, c.StartY) + c.Game;
                    }
                    if (wait >= bestTime) return false;
                    State s = _s.Clone();
                    // The wait pays for the box (its currency) and brings GP; clone dust piling up meanwhile would lift the GP
                    // multiplier whatever is bought, so it isn't counted for the box.
                    s.C[W] += rate[W] * wait;
                    s.C[GP] += rate[GP] * wait;
                    s.T += wait;
                    if (box >= 0)
                        for (int i = 0; i < levels; i++)
                        {
                            Box b = _d.Boxes[box];
                            s.C[b.Currency] = Math.Max(s.C[b.Currency], s.Cost[box]);
                            if (Touch(_d, s, box) == 0) return false;
                        }
                    if (course >= 0)
                    {
                        // The lap after it: the course's path, and its clone dust reward once clone dust is on.
                        if (s.Path[course] <= 0.1) { s.Path[course] = _d.Courses[course].Game; s.Best[course] = s.Path[course]; }
                        if (Lo(s, course, L.enableCloneDustGeneration) >= 1.0) s.C[CD] += Math.Ceiling(CdReward(_d, s, course) * 10.0 * CdMult(s));
                    }
                    Box bx = box >= 0 ? _d.Boxes[box] : null;
                    int k2 = bx != null && !bx.Local && (bx.GlobalKind == G.increasedNuclearPower || bx.GlobalKind == G.tripleThreatIncrease || bx.GlobalKind == G.atomLevelChance)
                        ? AtomsForCap(_d, s, target) : k;
                    if (k2 < 0) return true;
                    double gp = Income(_d, s).GP, gpCost = AtomCost(s, atom, k2);
                    double t = wait + (s.C[GP] >= gpCost ? 0 : gp > 0 ? (gpCost - s.C[GP]) / gp : Never);
                    if (t < bestTime) { bestTime = t; best = (box, course, levels); }
                    return true;
                }
                foreach (Box b in _d.Boxes)
                {
                    // The way's boxes are bought by the round above; their further levels (green clone chance, NP) are investments.
                    if (b.Atom || b.Index == _d.Zip || Array.IndexOf(_d.Path, b.Index) >= 0 && _s.Times[b.Index] == 0 || !Available(_s, b.Index) || !Reachable(b.Zone)) continue;
                    int course = -1;
                    if (b.Local && (b.LocalKind == L.cloneCount && _s.Path[b.Owner] <= 0.1 || b.LocalKind == L.enableCloneDustGeneration))
                    {
                        // A course's first clone runs once it has a path; clone dust (it multiplies green power,
                        // globalStats.RecalcCloneDustMult) comes from a lap: both are tried with the lap after them.
                        if (!_d.LapCourses.Contains(b.Owner)) continue;
                        course = b.Owner;
                    }
                    bool multi = !(_s.BuyMax[b.Index] && !b.NeverBuyMax) && b.Cap > 1;
                    for (int levels = 1; levels <= (multi ? 32 : 1); levels *= 2)
                        if (!Try(b.Index, course, levels)) break;
                }
                foreach (Course c in _d.Courses)
                    if (_s.Clones[c.Index] > 0 && _s.Path[c.Index] <= 0.1 && _d.LapCourses.Contains(c.Index)) Try(-1, c.Index, 1);
                return best;
            }
        }

        static string AtomText(State s)
        {
            var parts = new List<string>();
            for (int t = 0; t <= MaxTier; t++) if (s.Atoms[t] > 0) parts.Add(s.Atoms[t] + "xT" + t);
            if (s.Waiting > 0) parts.Add(s.Waiting + " waiting");
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        static double AtomCost(State s, Box a, int count)
        {
            double cost = s.Cost[a.Index], total = 0;
            for (int i = 0; i < count; i++)
            {
                total += cost;
                cost = Math.Round(Math.Pow(cost + a.Add, a.Pow) * a.Scale);
            }
            return total;
        }

        /// <summary>The fewest atoms more (bought now, all merged) that lift the NP cap to `need`; -1 if none up to 4000 does.</summary>
        static int AtomsForCap(Data d, State s0, double need)
        {
            State s = s0.Clone();
            // atoms still waiting or crafting land first
            SettleAtoms(s);
            for (int k = 0; k <= 4000; k++)
            {
                if (NpCap(d, s) * 1.0 >= need) return k;
                AddAtom(s, NextAtomTier(s));
                SettleAtoms(s);
            }
            return -1;
        }

        static void SettleAtoms(State s)
        {
            while (s.Waiting > 0) { AddAtom(s, NextAtomTier(s)); s.Waiting--; }
            s.CraftTier = -1;
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int t = 0; t < MaxTier; t++)
                    if (s.Atoms[t] >= 3 + t) { s.CraftTier = t; double np = s.C[NP]; FinishCraft(s); s.C[NP] = np; merged = true; break; }
            }
        }

        static Result Evaluate(Data d, State start, List<Step> plan, bool record = false)
        {
            var e = new Executor(d, start, record);
            bool ok = e.Run(plan);
            var r = new Result { End = e.S, Records = null };
            if (ok && e.S.Done) { r.Feasible = true; r.Time = e.S.T; }
            else
            {
                // Not reached: ranked by how far it got (tree boxes bought, then NP, then GP income), never above a finished plan.
                State s = e.S;
                int tree = d.Path.Count(b => s.Times[b] > 0);
                double gp = Income(d, s).GP;
                r.Progress = tree * 1e6 + Math.Log10(1 + s.C[NP]) * 1e4 + Math.Log10(1 + gp) * 1e2;
                r.Time = 1e12 - r.Progress;
            }
            if (record)
            {
                r.Records = e.Records;
                if (!r.Feasible) r.Records.Add(new Record { Start = e.S.T, End = e.S.T, What = "stuck: " + (e.Failed ?? "?"), Detail = "" });
            }
            return r;
        }

        // ---------------------------------------------------------------- search

        sealed class Search
        {
            readonly Data _d;
            readonly State _start;
            readonly Step[] _vocabulary;
            readonly int _threads;
            public List<Step> Best;
            public double BestTime = Never;
            long _evaluations;

            public Search(Data d, State start, int threads)
            {
                _d = d;
                _start = start;
                _threads = threads;
                var v = new List<Step>();
                foreach (Box b in d.Boxes)
                {
                    if (b.Atom || b.Index == d.Zip) continue;
                    v.Add(new Step(Kind.Buy, b.Index));
                    if (b.Cap > 1) v.Add(new Step(Kind.BuyAll, b.Index));
                }
                foreach (int c in d.LapCourses) v.Add(new Step(Kind.Lap, c));
                if (d.AtomBox >= 0) foreach (int k in new[] { 1, 2, 3, 5, 8, 12, 18, 25, 35, 50, 70, 100, 140, 200, 280, 400 }) v.Add(new Step(Kind.Atoms, k));
                _vocabulary = v.ToArray();
            }

            double Score(List<Step> plan) { Interlocked.Increment(ref _evaluations); return Evaluate(_d, _start, plan).Time; }

            public long Evaluations { get { return Interlocked.Read(ref _evaluations); } }

            /// <summary>Builds a plan one step at a time: each round appends the step whose plan (with the finish) ends soonest.</summary>
            public List<Step> Greedy(Action<string> log, double seconds)
            {
                var plan = new List<Step>();
                double current = Score(plan);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    var scores = new double[_vocabulary.Length];
                    Parallel.For(0, _vocabulary.Length, new ParallelOptions { MaxDegreeOfParallelism = _threads }, i =>
                    {
                        var p = new List<Step>(plan) { _vocabulary[i] };
                        scores[i] = Score(p);
                    });
                    int pick = Array.IndexOf(scores, scores.Min());
                    if (!(scores[pick] < current - 1e-6)) break;
                    plan.Add(_vocabulary[pick]);
                    current = scores[pick];
                    log?.Invoke($"  greedy {plan.Count,3}: {Name(_d, _vocabulary[pick]),-40} -> {Show(current)}");
                }
                Offer(plan, current);
                return plan;
            }

            void Offer(List<Step> plan, double time)
            {
                lock (this) if (time < BestTime) { BestTime = time; Best = new List<Step>(plan); }
            }

            /// <summary>Drops every step whose removal doesn't make the plan slower (no-ops, capped boxes, detours that don't pay).</summary>
            public void Prune()
            {
                for (int i = Best.Count - 1; i >= 0; i--)
                {
                    var p = new List<Step>(Best);
                    p.RemoveAt(i);
                    double t = Score(p);
                    if (t <= BestTime + 1e-6) { Best = p; BestTime = Math.Min(BestTime, t); }
                }
            }

            public void Seed(List<Step> plan) { Offer(plan, Score(plan)); }

            /// <summary>Iterated local search from the best plan: delete, swap, move, insert and resize steps; keeps any improvement.</summary>
            public void Polish(double seconds, int seed, Action<string> log)
            {
                var clock = Stopwatch.StartNew();
                Parallel.For(0, _threads, new ParallelOptions { MaxDegreeOfParallelism = _threads }, w =>
                {
                    var rng = new System.Random(seed + w * 7919);
                    List<Step> plan;
                    double score;
                    lock (this) { plan = new List<Step>(Best); score = BestTime; }
                    int stale = 0;
                    while (clock.Elapsed.TotalSeconds < seconds)
                    {
                        List<Step> next = Mutate(plan, rng);
                        if (next == null) continue;
                        double t = Score(next);
                        if (t < score - 1e-6)
                        {
                            plan = next; score = t; stale = 0;
                            bool better;
                            lock (this) better = t < BestTime;
                            if (better) { Offer(plan, t); log?.Invoke($"  polish {clock.Elapsed.TotalSeconds,6:0.0}s  {Show(t)}  ({plan.Count} steps, worker {w})"); }
                        }
                        else if (++stale > 400)
                        {
                            // Restart from the best so far, perturbed.
                            lock (this) { plan = new List<Step>(Best); score = BestTime; }
                            for (int k = 0; k < 3; k++) plan = Mutate(plan, rng) ?? plan;
                            score = Score(plan);
                            stale = 0;
                        }
                    }
                });
            }

            List<Step> Mutate(List<Step> plan, System.Random rng)
            {
                var p = new List<Step>(plan);
                int n = p.Count;
                switch (rng.Next(7))
                {
                    case 0:
                        if (n == 0) return null;
                        p.RemoveAt(rng.Next(n));
                        break;
                    case 1:
                        if (n < 2) return null;
                        { int i = rng.Next(n - 1); (p[i], p[i + 1]) = (p[i + 1], p[i]); }
                        break;
                    case 2:
                        if (n < 2) return null;
                        { int i = rng.Next(n); Step s = p[i]; p.RemoveAt(i); p.Insert(rng.Next(p.Count + 1), s); }
                        break;
                    case 3:
                        p.Insert(rng.Next(n + 1), _vocabulary[rng.Next(_vocabulary.Length)]);
                        break;
                    case 4:
                        // Two steps together (a box and the lap that makes it pay, say): neither may help alone.
                        {
                            int at = rng.Next(n + 1);
                            p.Insert(at, _vocabulary[rng.Next(_vocabulary.Length)]);
                            p.Insert(at + 1, _vocabulary[rng.Next(_vocabulary.Length)]);
                        }
                        break;
                    case 5:
                        {
                            int[] atoms = Enumerable.Range(0, n).Where(i => p[i].Kind == Kind.Atoms).ToArray();
                            if (atoms.Length == 0) return null;
                            int i = atoms[rng.Next(atoms.Length)];
                            int k = Math.Max(1, (int)Math.Round(p[i].Arg * (rng.NextDouble() < 0.5 ? 0.7 : 1.4)) + (rng.Next(3) - 1));
                            p[i] = new Step(Kind.Atoms, k);
                        }
                        break;
                    default:
                        {
                            int[] buys = Enumerable.Range(0, n).Where(i => p[i].Kind == Kind.Buy || p[i].Kind == Kind.BuyAll).ToArray();
                            if (buys.Length == 0) return null;
                            int i = buys[rng.Next(buys.Length)];
                            p[i] = new Step(p[i].Kind == Kind.Buy && _d.Boxes[p[i].Arg].Cap > 1 ? Kind.BuyAll : Kind.Buy, p[i].Arg);
                        }
                        break;
                }
                return p;
            }
        }

        static string Show(double t) { return t >= 1e11 ? "not reached" : $"{t:0.0} s ({t / 60:0.0} min)"; }

        // Plan steps as text (--plan and the printed plan): buy:<token>, all:<token>, lap:<course>, atoms:<count>.
        static string PlanToken(Data d, Step s)
        {
            return s.Kind switch
            {
                Kind.Lap => "lap:" + d.Courses[s.Arg].Number,
                Kind.Atoms => "atoms:" + s.Arg,
                Kind.BuyAll => "all:" + d.Boxes[s.Arg].Token,
                _ => "buy:" + d.Boxes[s.Arg].Token,
            };
        }

        static List<Step> ParsePlan(Data d, string text)
        {
            var plan = new List<Step>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2) throw new ArgumentException("plan step " + part + ": buy:<token>, all:<token>, lap:<course> or atoms:<count>");
                int box = Array.FindIndex(d.Boxes, b => b.Token == kv[1]);
                switch (kv[0])
                {
                    case "buy":
                    case "all":
                        if (box < 0) throw new ArgumentException("plan step " + part + ": no modelled box " + kv[1]);
                        plan.Add(new Step(kv[0] == "all" ? Kind.BuyAll : Kind.Buy, box));
                        break;
                    case "lap":
                        int course = Array.FindIndex(d.Courses, c => c.Number.ToString(CultureInfo.InvariantCulture) == kv[1]);
                        if (course < 0) throw new ArgumentException("plan step " + part + ": no course " + kv[1]);
                        plan.Add(new Step(Kind.Lap, course));
                        break;
                    case "atoms":
                        plan.Add(new Step(Kind.Atoms, int.Parse(kv[1], CultureInfo.InvariantCulture)));
                        break;
                    default:
                        throw new ArgumentException("plan step " + part + ": buy:<token>, all:<token>, lap:<course> or atoms:<count>");
                }
            }
            return plan;
        }

        static string Name(Data d, Step s)
        {
            return s.Kind switch
            {
                Kind.Lap => "lap Course " + d.Courses[s.Arg].Number,
                Kind.Atoms => "atoms x" + s.Arg,
                Kind.BuyAll => "buy all " + (d.Boxes[s.Arg].Token ?? d.Boxes[s.Arg].Path),
                _ => "buy " + (d.Boxes[s.Arg].Token ?? d.Boxes[s.Arg].Path),
            };
        }

        // ---------------------------------------------------------------- loading

        static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        static bool Present(Transform t)
        {
            // A box is in the world when it and its parents are on, except the zone roots (loaded as the player moves) and the tree
            // segments (grown by purchases, followed by the state).
            for (Transform p = t; p != null; p = p.parent)
            {
                if (p.GetComponent<TreeSegmentActivationComponent>() != null) return true;
                if (p.parent == null && (p.name == "Zone 1" || p.name == "zone 2" || p.name == "Zone 3")) return true;
                if (!p.gameObject.activeSelf) return false;
            }
            return true;
        }

        static readonly HashSet<L> LocalKinds = new HashSet<L> { L.cloneCount, L.cashPerLoop, L.fastCloneChance, L.bigCloneChance, L.cloneMult,
            L.GreenCloneRewardBase, L.enableCloneDustGeneration, L.moreWatts };
        static readonly HashSet<G> GlobalKinds = new HashSet<G> { G.cashPerLoop, G.fastCloneChance, G.maxCloneFastness, G.bigCloneChance,
            G.maxCloneBigness, G.cloneMult, G.spawnNewAtom, G.atomLevelChance, G.greenCloneClance, G.treeGrowth, G.increasedWatts,
            G.increasedGreenPower, G.increasedNuclearPower, G.area2DoorOpen, G.zipMoversUnlocked, G.exemptCourseFromAtomPrestige,
            G.tripleThreatIncrease, G.additionalCDdigits, G.compBoostUnlocked, G.compBoostTime, G.compBoostStrength, G.IncreasedCloneDust,
            G.makeCourseBuyMax, G.moreWatts, G.moreGP, G.NpCapIsSoftcap, G.compBoostDoublesNP, G.GPAtomsDontResetCourses,
            G.NpGenerationSpeed, G.wattPower, G.moreRefreshOrbs, G.unlockJiggleDrops };

        static readonly Regex RouteName = new Regex(@"^(\d+)_(" + Caps.LabelPattern + @") (farm )?(\d+(?:\.\d+)?)( stale)?$");

        /// <summary>
        /// Lap times per course from the saved routes (routes.json in the config folder): the record (the game timer, the clones'
        /// path) and the farm route (quick restart to finish), fastest in any category up to `caps`, non-stale first.
        /// </summary>
        static Dictionary<int, (double game, double farm, string source)> SavedLaps(string routesFile, string[] caps)
        {
            var result = new Dictionary<int, (double, double, string)>();
            if (!File.Exists(routesFile)) return result;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(routesFile));
            var found = new Dictionary<(int, bool), (double time, bool stale, string name)>();
            foreach (JsonElement m in doc.RootElement.GetProperty("macros").EnumerateArray())
            {
                string name = m.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                Match r = name == null ? null : RouteName.Match(name);
                if (r == null || !r.Success || !Categories.IsCategory(r.Groups[2].Value) || !caps.Contains(r.Groups[2].Value)) continue;
                int course = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                bool farm = r.Groups[3].Success, stale = r.Groups[5].Success;
                double time = double.Parse(r.Groups[4].Value, CultureInfo.InvariantCulture);
                var key = (course, farm);
                if (!found.TryGetValue(key, out var old) || (old.stale && !stale) || (old.stale == stale && time < old.time)) found[key] = (time, stale, name);
            }
            foreach (int course in found.Keys.Select(k => k.Item1).Distinct())
            {
                bool hasGame = found.TryGetValue((course, false), out var g), hasFarm = found.TryGetValue((course, true), out var f);
                double game = hasGame ? g.time : f.time - 0.4, farm = hasFarm ? f.time : g.time + 0.4;
                result[course] = (game, farm, string.Join(", ", new[] { hasGame ? g.name : null, hasFarm ? f.name : null }.Where(x => x != null)));
            }
            return result;
        }

        static (Data, State) Load(Simulation sim, JsonElement config, string routesFile, TextWriter log)
        {
            World world = sim.World;
            var d = new Data();
            Economy economy = sim.Shop.Economy;
            string[] caps = (config.TryGetProperty("categories", out JsonElement cats) ? cats.EnumerateArray().Select(c => c.GetString()) :
                Categories.All.Where(d => d.Scope == CategoryScope.Range).Select(d => d.Label)).ToArray();
            if (config.TryGetProperty("travel", out JsonElement travel))
            {
                if (travel.TryGetProperty("x", out JsonElement v)) d.TravelX = v.GetDouble();
                if (travel.TryGetProperty("up", out v)) d.TravelUp = v.GetDouble();
                if (travel.TryGetProperty("down", out v)) d.TravelDown = v.GetDouble();
                if (travel.TryGetProperty("overheadTicks", out v)) d.TravelOverhead = v.GetDouble();
            }
            if (config.TryGetProperty("legs", out JsonElement legs))
                foreach (JsonProperty p in legs.EnumerateObject()) d.Legs[p.Name] = p.Value.GetDouble();
            if (config.TryGetProperty("waitLimitSeconds", out JsonElement wl)) d.WaitLimit = wl.GetDouble();
            if (config.TryGetProperty("wayBackToZone1", out JsonElement wb)) d.WayBack = wb.GetBoolean();
            if (config.TryGetProperty("cloneFrameSeconds", out JsonElement cf)) d.Frame = cf.GetDouble();
            d.OffscreenFrames = FramesTo(1f, (float)d.Frame);
            globalStats stats = world.All<globalStats>().FirstOrDefault();
            if ((object)stats != null) { d.GameSpeed = stats.GameSpeed == 0 ? 1 : stats.GameSpeed; d.VmanMult = stats.vmanMult == 0 ? 1 : stats.vmanMult; d.WayBack |= stats.Zone2toZone1PathOpen; }

            // Courses
            Dictionary<int, (double game, double farm, string source)> saved = SavedLaps(routesFile, caps);
            JsonElement estimates = config.TryGetProperty("laps", out JsonElement lapsJson) ? lapsJson : default;
            courseScript[] scripts = world.All<courseScript>().Where(c => c.courseNumber >= 1 && c.courseNumber <= 10).OrderBy(c => c.courseNumber).ToArray();
            var courses = new List<Course>();
            startGate[] gates = world.All<startGate>();
            foreach (courseScript cs in scripts)
            {
                var k = new Course { Index = courses.Count, Number = cs.courseNumber, Area = cs.area, Name = cs.name,
                    BaseReward = Get<int>(cs, "baseReward"), BaseGp = Get<double>(cs, "baseGpReward"), BaseCd = Get<double>(cs, "baseCdReward"),
                    Tier = cs.tier, RewardTier = cs.rewardTier };
                k.Zone = k.Area == 2 ? 2 : 1;
                startGate gate = gates.FirstOrDefault(g => { for (Transform p = g.transform; p != null; p = p.parent) if (p.GetComponent<courseScript>() == cs) return true; return false; });
                Transform at = gate == null ? cs.transform : (Get(gate, "resetPoint") as GameObject)?.transform ?? gate.transform;
                k.StartX = at.position.x; k.StartY = at.position.y;
                string key = k.Number.ToString(CultureInfo.InvariantCulture);
                if (estimates.ValueKind == JsonValueKind.Object && estimates.TryGetProperty(key, out JsonElement e) && e.TryGetProperty("force", out JsonElement force) && force.GetBoolean())
                { k.Game = e.GetProperty("game").GetDouble(); k.Farm = e.GetProperty("farm").GetDouble(); k.LapSource = "zip-plan.json (forced)"; }
                else if (saved.TryGetValue(k.Number, out var lap)) { k.Game = lap.game; k.Farm = lap.farm; k.LapSource = lap.source; }
                else if (estimates.ValueKind == JsonValueKind.Object && estimates.TryGetProperty(key, out e))
                { k.Game = e.GetProperty("game").GetDouble(); k.Farm = e.GetProperty("farm").GetDouble(); k.LapSource = "ESTIMATE (zip-plan.json)"; }
                else { k.Game = 20; k.Farm = 20.4; k.LapSource = "ESTIMATE (default 20 s)"; }
                courses.Add(k);
            }
            d.Courses = courses.ToArray();
            int CourseIndex(courseScript cs) { return (object)cs == null ? -1 : Array.FindIndex(d.Courses, k => k.Number == cs.courseNumber); }
            int[] lapCourses = config.TryGetProperty("lapCourses", out JsonElement lc) ? lc.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Enumerable.Range(1, 10).ToArray();
            foreach (Course k in d.Courses) if (lapCourses.Contains(k.Number)) d.LapCourses.Add(k.Index);
            d.LoD = d.Courses.Select(_ => new List<Vector2[]>()).ToArray();
            foreach (ColliderData cd in world.Colliders)
            {
                if ((object)cd.Collider == null || cd.Collider.GetComponent<clonesLoD>() == null) continue;
                clonesScript clones = cd.Collider.GetComponentInParent<clonesScript>();
                int c = (object)clones == null ? -1 : CourseIndex(Get<courseScript>(clones, "course"));
                if (c < 0) continue;
                foreach (ShapeData shape in cd.Shapes) if (shape.Vertices != null && shape.Vertices.Length >= 3) d.LoD[c].Add(shape.Vertices);
            }

            // Trees
            TreeController[] trees = world.All<TreeController>();
            var treeList = new List<Tree>();
            var segmentOf = new Dictionary<GameObject, (int tree, int segment)>();
            foreach (TreeController tc in trees)
            {
                var segs = Get<TreeSegmentActivationComponent[]>(tc, "TreeSegments") ?? Array.Empty<TreeSegmentActivationComponent>();
                var t = new Tree { Index = treeList.Count, LandSeconds = new double[segs.Length], DropSeconds = new double[segs.Length],
                    Auto = new int[segs.Length], SegmentBox = Enumerable.Repeat(-1, segs.Length).ToArray() };
                for (int i = 0; i < segs.Length; i++)
                {
                    int trunks = (Get<GameObject[]>(segs[i], "trunks") ?? Array.Empty<GameObject>()).Length;
                    // TreeSegmentActivationComponent.IE_Grow: each trunk 1/0.6 s, the land 1/3.5 s, then the box drops 2000 units at
                    // Lerp(3 per second) to within 5 units: ln(400)/3 s.
                    t.LandSeconds[i] = trunks / 0.6 + 1 / 3.5;
                    t.DropSeconds[i] = Math.Log(2000.0 / 5.0) / 3.0;
                    t.Auto[i] = Get<bool>(segs[i], "autoTriggerNext") ? Get<int>(segs[i], "NextSegmentToAutoTriggerIndex") : -1;
                    if (segs[i].box != null) segmentOf[segs[i].box] = (t.Index, i);
                }
                treeList.Add(t);
            }
            d.Trees = treeList.ToArray();

            // Boxes: every box with a Full-TAS token whose purchase the model follows
            var tokens = FullTasGoal.Boxes.ToDictionary(b => FullTasGoal.BoxId(b.path, b.sibling), b => b.token);
            var boxes = new List<Box>();
            var boxOf = new Dictionary<upgradeBox, int>();
            var skipped = new List<string>();
            foreach (upgradeBox ub in world.All<upgradeBox>())
            {
                string path = HierarchyPath(ub.transform);
                tokens.TryGetValue(FullTasGoal.BoxId(path, world.SiblingIndex.TryGetValue(ub.transform, out int sib) ? sib : 0), out string token);
                if (token == null) continue;
                int currency = (int)Get<globalStats.Currencies>(ub, "upgradeCurrency");
                bool local = ub.upgrade != L.GLOBAL;
                if (ub.upgrade == L.Movement || ub.upgrade == L.prestige || (local ? !LocalKinds.Contains(ub.upgrade) : !GlobalKinds.Contains(ub.globalUpgrade))
                    || !(currency == W || currency == GP || currency == NP || currency == CD))
                { skipped.Add(token); continue; }
                var b = new Box { Index = boxes.Count, Token = token, Path = path, Name = ub.name, X = ub.transform.position.x, Y = ub.transform.position.y,
                    Local = local, LocalKind = ub.upgrade, GlobalKind = ub.globalUpgrade, Currency = currency, Base = ub.baseUpgradeCost,
                    Scale = ub.upgradeScaleFactor, Pow = ub.upgradePowerScaleFactor, Add = ub.upgradeAddFactor, Em = Get<double>(ub, "effectMult"),
                    TierMod = Get<double>(ub, "TierMod"), Cap = ub.Cap, NeverBuyMax = ub.neverBuyMax,
                    Atom = !local && ub.globalUpgrade == G.spawnNewAtom };
                b.Course = CourseIndex(Get<courseScript>(ub, "course"));
                localUpgrades owner = ub.GetComponentInParent<localUpgrades>();
                if ((object)owner != null) b.Owner = CourseIndex(owner.GetComponentInParent<courseScript>());
                if (local && b.Owner < 0) { skipped.Add(token + " (no course)"); continue; }
                b.Target = CourseIndex(Get<courseScript>(ub, "courseToExemptReference"));
                b.Resets = (Get<courseScript[]>(ub, "coursesToReset") ?? Array.Empty<courseScript>()).Select(CourseIndex).Where(i => i >= 0).ToArray();
                if (segmentOf.TryGetValue(ub.gameObject, out var seg)) { b.Tree = seg.tree; b.Segment = seg.segment; d.Trees[seg.tree].SegmentBox[seg.segment] = b.Index; }
                TreeController grows = Get<TreeController>(ub, "upgradeTree");
                if ((object)grows != null) { b.GrowsTree = Array.IndexOf(trees, grows); b.Grows = Get<int[]>(ub, "SegmentsToTrigger") ?? Array.Empty<int>(); }
                b.Zone = b.Owner >= 0 ? d.Courses[b.Owner].Zone : path.StartsWith("Zone 1/", StringComparison.Ordinal) ? 1 : 2;
                if (!local && ub.globalUpgrade == G.zipMoversUnlocked) d.Zip = b.Index;
                if (b.Atom && currency == GP) d.AtomBox = b.Index;
                boxes.Add(b);
                boxOf[ub] = b.Index;
            }
            d.Boxes = boxes.ToArray();
            foreach (Course k in d.Courses) k.Boxes = d.Boxes.Where(b => b.Owner == k.Index).Select(b => b.Index).ToArray();
            if (skipped.Count > 0) log.WriteLine("not modelled (not an economy box the plan needs): " + string.Join(", ", skipped));

            // The zip box's way down its tree: segment by segment, the box (or auto-grow) that grows each one.
            if (d.Zip >= 0 && d.Boxes[d.Zip].Tree >= 0)
            {
                Box zip = d.Boxes[d.Zip];
                Tree t = d.Trees[zip.Tree];
                var path = new List<int> { zip.Index };
                int segment = zip.Segment;
                var seen = new HashSet<int>();
                while (segment >= 0 && seen.Add(segment))
                {
                    int grower = d.Boxes.FirstOrDefault(x => x.GrowsTree == zip.Tree && x.Grows.Contains(segment))?.Index ?? -1;
                    if (grower >= 0) { path.Insert(0, grower); segment = d.Boxes[grower].Segment; continue; }
                    int auto = Array.FindIndex(t.Auto, a => a == segment);
                    segment = auto;
                }
                d.Path = path.ToArray();
            }

            // State
            var s = new State { G = new double[GlobalCount], L = new double[d.Courses.Length][], Clones = new int[d.Courses.Length],
                Path = new double[d.Courses.Length], Best = new double[d.Courses.Length], Boost = new double[d.Courses.Length],
                Exempt = new bool[d.Courses.Length], Times = new int[d.Boxes.Length], Cost = new double[d.Boxes.Length],
                Ready = new double[d.Boxes.Length], Active = new bool[d.Boxes.Length], BuyMax = new bool[d.Boxes.Length],
                Touched = Enumerable.Repeat(-1e9, d.Boxes.Length).ToArray(),
                SegmentGrown = d.Trees.Select(t => Enumerable.Repeat(Never, t.LandSeconds.Length).ToArray()).ToArray() };
            for (int c = 0; c < Currencies; c++)
                s.C[c] = economy != null ? sim.Shop.Holds((globalStats.Currencies)c) : globalStats.currencyLookup != null && globalStats.currencyLookup.TryGetValue((globalStats.Currencies)c, out double v) ? v : 0;
            for (int g = 0; g < GlobalCount; g++)
                s.G[g] = economy != null ? economy.GlobalOf((G)g) : globalStats.globalUpgradeDict != null && globalStats.globalUpgradeDict.TryGetValue((G)g, out double v) ? v : 0;
            foreach (Course k in d.Courses)
            {
                courseScript cs = scripts[k.Index];
                s.L[k.Index] = new double[LocalCount];
                for (int l = 0; l < LocalCount; l++)
                    s.L[k.Index][l] = economy != null ? economy.LocalOf(cs.localUpgradesScript, (L)l) : cs.localUpgradesScript?.localUpgradeDict?.TryGetValue((L)l, out double v) == true ? v : 0;
                clonesScript clones = Get<clonesScript>(cs, "clones");
                var cv = economy?.ClonesOf(clones);
                s.Clones[k.Index] = cv?.count ?? clones?.cloneCount ?? 0;
                s.Path[k.Index] = cv?.pathLength ?? clones?.pathLength ?? 0;
                s.Best[k.Index] = Get<float>(cs, "bestPathTime");
                s.Boost[k.Index] = Math.Max(0, cs.compBoostTimeLeft);
                s.Exempt[k.Index] = cs.exemptFromAtomPrestige;
            }
            foreach (upgradeBox ub in boxOf.Keys)
            {
                int i = boxOf[ub];
                Box b = d.Boxes[i];
                s.Times[i] = ub.TimesUsed;
                s.Cost[i] = ub.upgradeCost;
                s.Active[i] = ub.isActive && ub.TimesUsed < ub.Cap;
                s.BuyMax[i] = ub.buyMax;
                s.Ready[i] = b.Tree >= 0 ? Never : ub.visible && Present(ub.transform) ? 0 : Never;
            }
            for (int ti = 0; ti < trees.Length; ti++)
            {
                List<int> grown = Get<List<int>>(trees[ti], "GrownSegments") ?? new List<int>();
                foreach (int g in grown)
                    if (g >= 0 && g < d.Trees[ti].LandSeconds.Length)
                    {
                        s.SegmentGrown[ti][g] = 0;
                        if (d.Trees[ti].SegmentBox[g] >= 0) s.Ready[d.Trees[ti].SegmentBox[g]] = 0;
                    }
            }
            // A bought tree box's segments grew (upgradeBox.Start regrows them).
            foreach (Box b in d.Boxes) if (b.GrowsTree >= 0 && s.Times[b.Index] > 0) foreach (int g in b.Grows) Grow(d, s, b.GrowsTree, g, -1e6);
            AtomColliderSystem collider = world.All<AtomColliderSystem>().FirstOrDefault();
            if ((object)collider != null)
            {
                var contents = Get<List<List<GameObject>>>(collider, "contents");
                if (contents != null)
                    for (int t = 0; t < contents.Count && t <= MaxTier; t++) { s.Lists = t + 1; for (int i = 0; i < contents[t].Count; i++) AddAtom(s, t); }
                s.Waiting = Get<int>(collider, "waitingAtoms");
                s.Counter = collider.atomTierUpCounter;
            }
            RecalcNp(s);
            if ((object)stats != null) s.NpBoost = Math.Max(1.0, stats.EffectOfNpCompBoost);
            Vector3 player = sim.Player.transform.position;
            s.X = player.x; s.Y = player.y;
            ZoneLoader zones = world.All<ZoneLoader>().FirstOrDefault();
            s.Zone = (object)zones != null && zones.activeZone == 2 ? 2 : 1;
            s.NextArrive = 0;
            return (d, s);
        }

        // ---------------------------------------------------------------- command

        public static int Run(string[] args)
        {
            if (args.Length == 0 || args[0].StartsWith("--"))
            {
                Console.Error.WriteLine("usage: zip-plan <trace.json> [--route <route.json> [--ticks N]] [--config zip-plan.json] [--plan steps] [--minutes M] [--threads N] [--out plan.json] [--seed N]");
                return 2;
            }
            string trace = args[0];
            JsonElement header = Solve.ReadHeader(trace);
            Simulation sim = Solve.Creator(trace, header)();
            string route = Solve.Option(args, "--route", "");
            if (route.Length > 0)
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(route));
                List<InputTick> inputs = Solve.ReadInputs(doc.RootElement);
                int ticks = Solve.Option(args, "--ticks", inputs.Count);
                sim.Advance(inputs.Take(ticks).ToList());
                Console.WriteLine($"start: {route} replayed to input {Math.Min(ticks, inputs.Count)}");
            }
            string configFile = Solve.Option(args, "--config", Path.Combine(AppContext.BaseDirectory, "zip-plan.json"));
            using JsonDocument configDoc = JsonDocument.Parse(File.Exists(configFile) ? File.ReadAllText(configFile) : "{}");
            string routesFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(trace)), "..", "routes.json");
            var (d, start) = Load(sim, configDoc.RootElement, routesFile, Console.Out);
            if (d.Zip < 0) { Console.Error.WriteLine("the world has no zip mover box (buytree16)"); return 1; }

            Console.WriteLine($"Zip movers: {d.Boxes[d.Zip].Token}, {start.Cost[d.Zip]:N0} {CurrencyNames[d.Boxes[d.Zip].Currency]}; the tree's way there: "
                + string.Join(" -> ", d.Path.Select(b => d.Boxes[b].Token)));
            Console.WriteLine("Laps (the clones' path is the record; a lap from the course's restart takes the farm time):");
            foreach (Course k in d.Courses)
                Console.WriteLine($"  Course {k.Number,2}: record {k.Game,6:0.00} s, farm {k.Farm,6:0.00} s  [{k.LapSource}]{(d.LapCourses.Contains(k.Index) ? "" : "  (not lapped)")}");
            Console.WriteLine($"Start: W {start.C[W]:G4}, GP {start.C[GP]:G4}, NP {start.C[NP]:G4}, CD {start.C[CD]:G4}; clones "
                + string.Join(", ", d.Courses.Where(k => start.Clones[k.Index] > 0).Select(k => $"C{k.Number} {start.Clones[k.Index]} (path {start.Path[k.Index]:0.##} s)")));

            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount);
            double minutes = Solve.Option(args, "--minutes", 5.0);
            var search = new Search(d, start, threads);
            var clock = Stopwatch.StartNew();
            string given = Solve.Option(args, "--plan", "");
            if (given.Length > 0)
            {
                List<Step> plan = ParsePlan(d, given);
                search.Seed(plan);
                Console.WriteLine($"Given plan ({plan.Count} steps): {Show(search.BestTime)}");
            }
            else
            {
                Console.WriteLine("Greedy build:");
                search.Greedy(Console.WriteLine, minutes * 60 * 0.5);
                Console.WriteLine($"Greedy: {Show(search.BestTime)} after {search.Evaluations} plans, {clock.Elapsed.TotalSeconds:0} s");
            }
            double left = minutes * 60 - clock.Elapsed.TotalSeconds;
            if (left > 1)
            {
                Console.WriteLine("Polish:");
                search.Polish(left, Solve.Option(args, "--seed", 1), Console.WriteLine);
            }
            search.Prune();
            Result best = Evaluate(d, start, search.Best, record: true);
            Console.WriteLine("\nPlan steps (--plan): " + string.Join(",", search.Best.Select(p => PlanToken(d, p))));
            Console.WriteLine($"\nBest plan: {Show(best.Time)} from the start state ({search.Evaluations} plans evaluated)\n");
            var goals = new List<string>();
            for (int i = 0; i < best.Records.Count; i++)
            {
                Record r = best.Records[i];
                if (r.Goal != null) goals.AddRange(r.Goal.Split(' '));
                // Repeated purchases of one box (standing in it) print as one line.
                int j = i;
                while (r.Goal != null && r.Goal.StartsWith("buy:", StringComparison.Ordinal) && j + 1 < best.Records.Count && best.Records[j + 1].Goal == r.Goal)
                {
                    j++;
                    goals.Add(best.Records[j].Goal);
                }
                Record last = best.Records[j];
                Console.WriteLine($"{r.Start,8:0.0}-{last.End,8:0.0}s  {last.What}{(j > i ? $"  ({j - i + 1} touches)" : "")}");
                if (!string.IsNullOrEmpty(last.Detail)) Console.WriteLine($"{"",20}{last.Detail}");
                i = j;
            }
            Console.WriteLine($"\n{goals.Count} Full-TAS goals:\n" + string.Join(" ", goals.Select(g => "\"" + g + "\"")));
            string outFile = Solve.Option(args, "--out", "");
            if (outFile.Length > 0)
            {
                var json = new
                {
                    trace = Path.GetFileName(trace), seconds = best.Feasible ? Math.Round(best.Time, 2) : (double?)null,
                    laps = d.Courses.Select(k => new { course = k.Number, record = k.Game, farm = k.Farm, source = k.LapSource }),
                    steps = best.Records.Select(r => new { start = Math.Round(r.Start, 2), end = Math.Round(r.End, 2), what = r.What, goal = r.Goal, state = r.Detail }),
                    goals,
                };
                File.WriteAllText(outFile, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("wrote " + outFile);
            }
            return best.Feasible ? 0 : 1;
        }
    }
}
