using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IGTAPTasMod;
using UnityEngine;

namespace IGTAP.EngineSim
{
    /// <summary>
    /// The economy without the player: the economy planner's Cash model (docs/superpowers/specs/2026-10-03-economy-planner-design.md).
    /// A physics tick's economy half is the pieces below, which <see cref="Run"/>'s tick plays between the player's: the due
    /// Invokes and Seconds waits (<see cref="EconomyTickStart"/>), globalStats' NP (Economy.FixedStep), the course, Vman clock,
    /// refill orb and box cooldown FixedUpdates (<see cref="EconomyFixedUpdates"/>), the buy-max steps and FixedUpdate waits
    /// after the physics step (<see cref="EconomyAfterStep"/>), each rendered frame's Start pass, clone and atom steps, atom grid
    /// and frame and end-of-frame resumes (<see cref="InterpolatedFrames"/> without the player). The drive plays the same pieces in the same order with the player's half left out: no
    /// input, no Movement FixedUpdate or Update, no physics step, no FloatingOrigin rebase. What the physics step's callbacks
    /// do for the economy it does as they do it, at the step's place in the tick: a clonesLoD trigger's enter or exit where
    /// the player arrives (its boxes standing there, the first tick's step), a box's OnTriggerStay2D, a course's stopTracking
    /// economy.
    /// A drive starts from <see cref="RestoreForDrive"/>, and the run's engine counts go on through it (its physics ticks: the
    /// frame grid's phase and the Full-TAS reward refresh, MacroEngine.RefreshRewards). The run's goal tracker sits the drive
    /// out (<see cref="Driving"/>): a buy neither reaches nor strays from a goal, a short touch isn't logged, a reward box
    /// doesn't finish the run. The planner owns its schedule. An unmodeled economy event still stops the engine, and the
    /// drive throws. The physics world's contacts stay where the snapshot's player left them: a drive's state is for more
    /// drives, not for a Run.
    /// </summary>
    public sealed partial class Simulation
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly MethodInfo BoxStay = typeof(upgradeBox).GetMethod("OnTriggerStay2D", Instance);
        static readonly MethodInfo LodEnter = typeof(clonesLoD).GetMethod("OnTriggerEnter2D", Instance);
        static readonly MethodInfo LodExit = typeof(clonesLoD).GetMethod("OnTriggerExit2D", Instance);

        /// <summary>Scheduler.Tick is the drive's last tick (as in a Run's afterTick, where Capture counts from), not the next one (as
        /// after a Restore).</summary>
        bool _driven;
        /// <summary>Each course's clones' LoD triggers, on or off.</summary>
        (clonesScript clones, Collider2D[] lods)[] _lods;
        static readonly FieldInfo OverrideLod = typeof(clonesLoD).GetField("overrideLOD", Instance);

        /// <summary>A drive's tick is playing: the goal tracker (Bought, Shop.Stay's short touch, RewardHit) sits it out.</summary>
        internal bool Driving;

        /// <summary>
        /// The snapshot's state, its engine counts going on (Run resumes them itself): a drive's start. A snapshot before the
        /// run's first input (a trace's start) counts every physics tick from here as the run would from its first input tick
        /// (MacroEngine.RebaseInputs, as Advance's states do).
        /// </summary>
        public void RestoreForDrive(SimSnapshot s)
        {
            Restore(s);
            Engine.ResumeOwned(s.Engine, new List<MacroStep>(), 0);
            if (Engine.RunTicks == 0) Engine.RebaseInputs();
        }

        /// <summary><paramref name="ticks"/> physics ticks with the player standing at <paramref name="at"/> (the scene frame
        /// now; it arrives in the first tick's physics step, <see cref="Arrive"/>).</summary>
        public void EconomyIdle(int ticks, Vector2 at)
        {
            for (int i = 0; i < ticks; i++) EconomyTick(i == 0 ? () => Arrive(at) : null);
        }

        /// <summary>
        /// One physics tick in which the player at <paramref name="at"/> touches the box (an id or hierarchy path): its
        /// OnTriggerStay2D, as the physics step calls it (the cooldown, the affordability and the box's state as the hook checks
        /// them; a buy-max box's routine started, the next ticks' drive going on with it), where one of its triggers overlaps the
        /// player's boxes standing there (<see cref="Reaches"/>: a tree box still dropping onto its spot after IE_Grow turns it
        /// on, 2000 u above, doesn't). False when it didn't buy.
        /// </summary>
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

        /// <summary>Where the player stands to touch the box once it's where its transform is now (a tree box not grown yet: its
        /// spot): just under its first trigger (a buy box's thin trigger under its body, hit from below), the player's boxes 4 u
        /// into it; null for a box with no box trigger.</summary>
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

        /// <summary>One of the box's own trigger colliders, on and where it is now, overlaps the player's boxes, its body standing at
        /// <paramref name="at"/> (PhysicsWorld.PlayerOverlaps, as the clonesLoD triggers are tested).</summary>
        internal bool Reaches(upgradeBox box, Vector2 at)
        {
            if (!box.gameObject.activeInHierarchy) return false;
            // The transforms scripts moved since the last step (IE_Grow's drop), as the physics step syncs them first.
            Physics.SyncTransforms();
            foreach (Collider2D c in box.GetComponents<Collider2D>())
                if (c.m_IsTrigger && c.m_Enabled && Physics.PlayerOverlaps(c, at)) return true;
            return false;
        }

        /// <summary>One physics tick in which the course numbered <paramref name="course"/> finishes in <paramref name="time"/>
        /// (its course timer): stopTracking's economy, as its end gate's trigger calls it (Shop.CourseFinished: the reward, clone
        /// dust, comp boost and a record's clone path; a Vman course's completion reward). A quick restart has no economy side.</summary>
        public void EconomyFinish(int course, float time)
        {
            courseScript c = Course(course);
            EconomyTick(() => Shop.CourseFinished(c, time));
        }

        /// <summary>Before the tick's other FixedUpdates, in the frame that will cross the tick: the Start of what the last frame's end
        /// woke (IE_Grow's box: Unity runs it before the frame's FixedUpdates, README ### Rules, exception 12), the due Invokes,
        /// then the Seconds waits.</summary>
        void EconomyTickStart()
        {
            // The tick's physics step is in its first rendered frame (InterpolatedFrames counts the others).
            Scheduler.Frame++;
            Shop.Economy?.StartPass();
            Scheduler.FireDue();
            Scheduler.FrameDeltaTime = Frames.Step(MacroEngine.FrameGrid.NextTicks(Engine.GridTicks), 0);
            Scheduler.Resume(Scheduler.Wait.Seconds);
        }

        /// <summary>After Movement.FixedUpdate: the active courses' FixedUpdates, the Vman clock, the used refill orbs and the boxes'
        /// cooldowns.</summary>
        void EconomyFixedUpdates()
        {
            for (int i = 0; i < Courses.Length; i++)
                if (Courses[i].isActiveAndEnabled) _courseFixedUpdates[i]();
            VmanClock();
            StepOrbs();
            Shop.TickCooldowns(World.FixedDeltaTime);
        }

        /// <summary>
        /// After the physics step: a buy-max box's IE_BuyMax yields WaitForFixedUpdate (build 25630570; WaitForEndOfFrame before),
        /// one level per physics step, the touch's step included (its coroutine starts in the step's trigger callback); then the
        /// other FixedUpdate waits.
        /// </summary>
        void EconomyAfterStep()
        {
            Shop.FixedStep();
            Scheduler.Resume(Scheduler.Wait.FixedUpdate);
        }

        /// <summary>The drive's tick: Run's tick without the player, <paramref name="physicsStep"/> the step's callbacks.</summary>
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

        /// <summary>
        /// The player arrives at <paramref name="at"/>, in a physics step: each course's clones go on screen where its boxes
        /// standing there overlap one of their clonesLoD triggers and off where they overlap none (<see cref="InsideLod"/>), by
        /// the trigger's own OnTriggerEnter2D (its course's fullUpdate included) or OnTriggerExit2D, where that changes them,
        /// before the step's other callbacks (a box's touch). No scene's clonesLoD overrides its LoD (clonesLoD.Start keeps
        /// those on screen until an exit): one that does is refused.
        /// </summary>
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

        /// <summary>Whether the player's boxes, its body standing at <paramref name="at"/>, overlap one of the clones' clonesLoD
        /// triggers that are on (PhysicsWorld.PlayerOverlaps).</summary>
        internal bool InsideLod(clonesScript clones, Vector2 at)
        {
            return World.All<clonesLoD>().Any(l => l.gameObject.activeInHierarchy && ReferenceEquals(l.GetComponentInParent<clonesScript>(), clones)
                && l.GetComponent<Collider2D>() is Collider2D c && Physics.PlayerOverlaps(c, at));
        }
    }
}
