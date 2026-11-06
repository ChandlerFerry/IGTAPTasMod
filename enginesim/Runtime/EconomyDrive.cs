using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    public sealed partial class Simulation
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly MethodInfo BoxStay = typeof(upgradeBox).GetMethod("OnTriggerStay2D", Instance);
        static readonly MethodInfo LodEnter = typeof(clonesLoD).GetMethod("OnTriggerEnter2D", Instance);
        static readonly MethodInfo LodExit = typeof(clonesLoD).GetMethod("OnTriggerExit2D", Instance);

        bool _driven;
        (clonesScript clones, Collider2D[] lods)[] _lods;
        static readonly FieldInfo OverrideLod = typeof(clonesLoD).GetField("overrideLOD", Instance);

        internal bool Driving;

        public void RestoreForDrive(SimSnapshot s)
        {
            Restore(s);
            Engine.ResumeOwned(s.Engine, new List<MacroStep>(), 0);
            if (Engine.RunTicks == 0) Engine.RebaseInputs();
        }

        public void EconomyIdle(int ticks, Vector2 at)
        {
            for (int i = 0; i < ticks; i++) EconomyTick(i == 0 ? () => Arrive(at) : null);
        }

        public bool EconomyBuy(string boxId, Vector2 at)
        {
            upgradeBox box = Shop.Box(boxId) ?? throw new ArgumentException("No upgrade box " + boxId + " in this world export.");
            int used = box.TimesUsed;
            EconomyTick(() =>
            {
                Arrive(at);
                if (!Reaches(box, at)) return;
                Touch(box);
                BoxStay.Invoke(box, new object[] { _playerColliders[0] });
            });
            return box.TimesUsed != used;
        }

        internal Vector2? TouchSpot(upgradeBox box)
        {
            foreach (Collider2D c in box.GetComponents<Collider2D>())
            {
                if (!c.isTrigger || c is not BoxCollider2D trigger) continue;
                Vector3 scale = box.transform.lossyScale, p = box.transform.position;
                float bottom = p.y + (trigger.offset.y - trigger.size.y / 2f) * scale.y;
                BoxCollider2D body = _playerColliders[0];
                Vector3 own = Player.transform.lossyScale;
                float top = (body.offset.y + body.size.y / 2f) * own.y;
                return new Vector2(p.x + trigger.offset.x * scale.x - body.offset.x * own.x, bottom + 4f - top);
            }
            return null;
        }

        internal List<(WorldPos a, WorldPos b, string box)> StrayTriggers(ICollection<string> named)
        {
            var strays = new List<(WorldPos, WorldPos, string)>();
            BoxCollider2D body = _playerColliders[0];
            Vector3 own = Player.transform.lossyScale;
            float ox = body.offset.x * own.x, oy = body.offset.y * own.y;
            float hx = Math.Abs(body.size.x * own.x) / 2f, hy = Math.Abs(body.size.y * own.y) / 2f;
            foreach (upgradeBox box in Shop.PaidFor())
            {
                string id = Shop.Id(box);
                if (named.Contains(id)) continue;
                Vector3 scale = box.transform.lossyScale, p = box.transform.position;
                foreach (Collider2D c in box.GetComponents<Collider2D>())
                {
                    if (!c.isTrigger || c is not BoxCollider2D trigger) continue;
                    float cx = p.x + trigger.offset.x * scale.x - ox, cy = p.y + trigger.offset.y * scale.y - oy;
                    float ex = Math.Abs(trigger.size.x * scale.x) / 2f + hx, ey = Math.Abs(trigger.size.y * scale.y) / 2f + hy;
                    strays.Add((ToWorld(new ScenePos(cx - ex, cy - ey)), ToWorld(new ScenePos(cx + ex, cy + ey)), id));
                }
            }
            return strays;
        }

        internal double NpCapAfterPending()
        {
            globalStats stats = Singleton<globalStats>.Instance;
            double cap = stats?.NPrewardCap ?? 0;
            int left = Shop.Box(AtomBoxId) is upgradeBox atom ? Shop.BuyMaxLeft(atom) : 0;
            return Shop.Economy?.NpAfterAtoms(left) is var (_, after, _) ? Math.Max(cap, after) : cap;
        }

        static readonly string AtomBoxId = FullTasGoal.BoxId("AtomUpgrades/AtomUpgrade", 0);

        internal bool Reaches(upgradeBox box, Vector2 at)
        {
            if (!box.gameObject.activeInHierarchy) return false;
            Physics.SyncTransforms();
            foreach (Collider2D c in box.GetComponents<Collider2D>())
                if (c.m_IsTrigger && c.m_Enabled && Physics.PlayerOverlaps(c, at)) return true;
            return false;
        }

        public void EconomyFinish(int course, float time)
        {
            courseScript c = Course(course);
            EconomyTick(() => Shop.CourseFinished(c, time));
        }

        // Runs before the tick's other FixedUpdates: woken Starts (RULES.md exception 12), due Invokes, then Seconds waits.
        void EconomyTickStart()
        {
            Scheduler.Frame++;
            Shop.Economy?.StartPass();
            Scheduler.FireDue();
            Scheduler.FrameDeltaTime = Frames.Step(MacroEngine.FrameGrid.NextTicks(Engine.GridTicks), 0);
            Scheduler.Resume(Scheduler.Wait.Seconds);
        }

        void EconomyFixedUpdates()
        {
            for (int i = 0; i < Courses.Length; i++)
                if (Courses[i].isActiveAndEnabled) _courseFixedUpdates[i]();
            VmanClock();
            StepOrbs();
            Shop.TickCooldowns(World.FixedDeltaTime);
        }

        // Build 25630570: IE_BuyMax yields WaitForFixedUpdate, one level per physics step, the touch's step included.
        void EconomyAfterStep()
        {
            Shop.FixedStep();
            Scheduler.Resume(Scheduler.Wait.FixedUpdate);
        }

        void EconomyTick(Action physicsStep)
        {
            if (!Engine.IsRunning) throw new InvalidOperationException("The economy drive has no run: start it from RestoreForDrive (" + Engine.Status + ")");
            if (_driven) Scheduler.Tick++;
            _driven = true;
            _freezeFor = -1f;
            _pauseFrames = _pauseAfter = 0;
            Driving = true;
            try
            {
                EconomyTickStart();
                Shop.Economy?.FixedStep();
                Engine.CountPhysicsTick();
                EconomyFixedUpdates();
                physicsStep?.Invoke();
                EconomyAfterStep();
                InterpolatedFrames(player: false);
            }
            finally { Driving = false; }
            if (!Engine.IsRunning) throw new InvalidOperationException("The economy drive stopped: " + Engine.Status);
        }

        void Arrive(Vector2 at)
        {
            Economy economy = Shop.Economy;
            if (economy == null) return;
            _lods ??= World.All<clonesLoD>().Where(l => l.GetComponent<Collider2D>() != null)
                .GroupBy(l => l.GetComponentInParent<clonesScript>()).Where(g => (object)g.Key != null)
                .Select(g => (g.Key, g.Select(l => l.GetComponent<Collider2D>()).ToArray())).ToArray();
            foreach (var (clones, lods) in _lods)
            {
                bool? onScreen = economy.OnScreenOf(clones);
                if (onScreen == null) continue;
                Collider2D on = lods.FirstOrDefault(c => c.gameObject.activeInHierarchy && Physics.PlayerOverlaps(c, at));
                if ((on != null) == onScreen.Value) continue;
                Collider2D trigger = on ?? lods.FirstOrDefault(c => c.gameObject.activeInHierarchy);
                if (trigger == null) continue;
                clonesLoD lod = trigger.GetComponent<clonesLoD>();
                if ((bool)OverrideLod.GetValue(lod))
                    throw new InvalidOperationException("Unmodeled: a clonesLoD with overrideLOD (" + lod.name + ") in the economy drive");
                Touch(lod);
                (on != null ? LodEnter : LodExit).Invoke(lod, new object[] { _playerColliders[0] });
            }
        }

        internal bool InsideLod(clonesScript clones, Vector2 at)
        {
            return World.All<clonesLoD>().Any(l => l.gameObject.activeInHierarchy && ReferenceEquals(l.GetComponentInParent<clonesScript>(), clones)
                && l.GetComponent<Collider2D>() is Collider2D c && Physics.PlayerOverlaps(c, at));
        }
    }
}
