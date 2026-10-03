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
    /// and end-of-frame resumes (<see cref="InterpolatedFrames"/> without the player) and the frame waits
    /// (<see cref="EconomyTickEnd"/>). The drive plays the same pieces in the same order with the player's half left out: no
    /// input, no Movement FixedUpdate or Update, no physics step, no FloatingOrigin rebase. What the physics step's callbacks
    /// do for the economy it does as they do it, at the step's place in the tick: a clonesLoD trigger's enter or exit for the
    /// place the player stands at, a box's OnTriggerStay2D, a course's stopTracking economy.
    /// A drive starts from <see cref="RestoreForDrive"/>, and the run's engine counts go on through it (its physics ticks: the
    /// frame grid's phase and the Full-TAS reward refresh, MacroEngine.RefreshRewards). What the run would refuse the drive
    /// refuses: an unmodeled economy event, or a purchase the run stops on (a strict run's stray buy), throws. The physics
    /// world's contacts stay where the snapshot's player left them: a drive's state is for more drives, not for a Run.
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
        /// <summary>Each course's clones' LoD triggers (on in the hierarchy at load).</summary>
        (clonesScript clones, Collider2D[] lods)[] _lods;

        /// <summary>The snapshot's state, its engine counts going on (Run resumes them itself): a drive's start.</summary>
        public void RestoreForDrive(SimSnapshot s)
        {
            Restore(s);
            Engine.ResumeOwned(s.Engine, new List<MacroStep>(), 0);
        }

        /// <summary><paramref name="ticks"/> physics ticks with the player standing at <paramref name="at"/> (the scene frame
        /// now), its clones on screen where <paramref name="at"/> is inside their clonesLoD trigger.</summary>
        public void EconomyIdle(int ticks, Vector2 at)
        {
            Arrive(at);
            for (int i = 0; i < ticks; i++) EconomyTick(null);
        }

        /// <summary>
        /// One physics tick in which the player at <paramref name="at"/> touches the box (an id or hierarchy path): its
        /// OnTriggerStay2D, as the physics step calls it (the cooldown, the affordability and the box's state as the hook checks
        /// them; a buy-max box's routine started, the next ticks' drive going on with it). False when it didn't buy.
        /// </summary>
        public bool EconomyBuy(string boxId, Vector2 at)
        {
            upgradeBox box = Shop.Box(boxId) ?? throw new ArgumentException("No upgrade box " + boxId + " in this world export.");
            Arrive(at);
            int used = box.TimesUsed;
            EconomyTick(() =>
            {
                Touch(box);
                BoxStay.Invoke(box, new object[] { _playerColliders[0] });
            });
            return box.TimesUsed != used;
        }

        /// <summary>One physics tick in which the course numbered <paramref name="course"/> finishes in <paramref name="time"/>
        /// (its course timer): stopTracking's economy, as its end gate's trigger calls it (Shop.CourseFinished: the reward, clone
        /// dust, comp boost and a record's clone path; a Vman course's completion reward). A quick restart has no economy side.</summary>
        public void EconomyFinish(int course, float time)
        {
            courseScript c = Course(course);
            EconomyTick(() => Shop.CourseFinished(c, time));
        }

        /// <summary>Before the tick's other FixedUpdates: the due Invokes, then the Seconds waits, in the frame that will cross the tick.</summary>
        void EconomyTickStart()
        {
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

        /// <summary>After the tick's rendered frames: the frame waits, on the crossing frame's deltaTime.</summary>
        void EconomyTickEnd()
        {
            Scheduler.FrameDeltaTime = Frames.Step(Engine.GridTicks, 0);
            Scheduler.Resume(Scheduler.Wait.Frame);
        }

        /// <summary>The drive's tick: Run's tick without the player, <paramref name="physicsStep"/> the step's callbacks.</summary>
        void EconomyTick(Action physicsStep)
        {
            if (!Engine.IsRunning) throw new InvalidOperationException("The economy drive has no run: start it from RestoreForDrive (" + Engine.Status + ")");
            if (_driven) Scheduler.Tick++;
            _driven = true;
            _freezeFor = -1f;
            _pauseFrames = _pauseAfter = 0;
            EconomyTickStart();
            Shop.Economy?.FixedStep();
            Engine.CountPhysicsTick();
            EconomyFixedUpdates();
            physicsStep?.Invoke();
            EconomyAfterStep();
            InterpolatedFrames(player: false);
            EconomyTickEnd();
            if (!Engine.IsRunning) throw new InvalidOperationException("The economy drive stopped: " + Engine.Status);
        }

        /// <summary>
        /// The player now stands at <paramref name="at"/>: each course's clones go on screen if it is inside one of their clonesLoD
        /// triggers and off if it isn't (the trigger's own OnTriggerEnter2D, its course's fullUpdate included, or
        /// OnTriggerExit2D), where that changes them. The test is the place, not the player's boxes.
        /// </summary>
        void Arrive(Vector2 at)
        {
            Economy economy = Shop.Economy;
            if (economy == null) return;
            _lods ??= World.All<clonesLoD>().Where(l => l.gameObject.activeInHierarchy && l.GetComponent<Collider2D>() != null)
                .GroupBy(l => l.GetComponentInParent<clonesScript>()).Where(g => (object)g.Key != null)
                .Select(g => (g.Key, g.Select(l => l.GetComponent<Collider2D>()).ToArray())).ToArray();
            foreach (var (clones, lods) in _lods)
            {
                bool? onScreen = economy.OnScreenOf(clones);
                if (onScreen == null) continue;
                Collider2D inside = lods.FirstOrDefault(c => c.gameObject.activeInHierarchy && Physics.OverlapPoint(c, at));
                if ((inside != null) == onScreen.Value) continue;
                clonesLoD lod = (inside ?? lods[0]).GetComponent<clonesLoD>();
                Touch(lod);
                (inside != null ? LodEnter : LodExit).Invoke(lod, new object[] { _playerColliders[0] });
            }
        }
    }
}
