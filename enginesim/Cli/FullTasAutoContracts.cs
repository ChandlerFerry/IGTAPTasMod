using System;
using System.Collections.Generic;

namespace IGTAP.EngineSim.Cli
{
    // The Full-TAS search's shared types (docs/fulltas.md, "Auto"): stage 1's goal-state library and exact stitching
    // (FullTasAutoLib.cs), stage 2's learned cost-to-go, stage 3's pull scheduler, stage 4's macro edges, each in its own file;
    // FullTasAuto.cs wires them into --auto. Physics ticks everywhere; goal k is 1-based (goal 0: the run's start).

    /// <summary>Where a library state was filed on its run.</summary>
    [Flags]
    enum LibPoint
    {
        None = 0,
        /// <summary>The run's start (tick 0).</summary>
        Start = 1,
        /// <summary>A goal touch: the tick the tracker counted goal <see cref="LibState.Goal"/>.</summary>
        Goal = 2,
        /// <summary>The tick after a quick restart.</summary>
        Reset = 4,
        /// <summary>onGround false -> true.</summary>
        Land = 8,
        /// <summary>OnWall false -> true.</summary>
        Wall = 16,
        /// <summary>dashActive true -> false.</summary>
        DashEnd = 32,
    }

    /// <summary>What a state looks like, for the cost-to-go's neighbours and the Pareto caps (world frame, units per second).</summary>
    struct LibFeatures
    {
        public float X, Y;
        /// <summary>Movement.Velocity and Movement.momentum.</summary>
        public float VX, VY, MX, MY;
        public int DashesLeft, AirJumpsLeft;
        public bool FacingRight, OnGround;
        /// <summary>The run's purchases and kept boxes (FullTasTracker.Hash with the unlocks): states differ in it only across buys.</summary>
        public int BuysHash;
        /// <summary>Shop.CashNow: FalseEnding's buys wait on it.</summary>
        public double Cash;
        /// <summary>The clones' Cash a physics tick (FullTas.CloneIncome): a break's faster path shows here, not in Cash or the buys.</summary>
        public double Income;
        public float Speed => (float)Math.Sqrt((VX + MX) * (VX + MX) + (VY + MY) * (VY + MY));
    }

    /// <summary>
    /// An exact state some simulated run passed through. Two states with the same <see cref="Key"/> (and goal) are identical in the
    /// whole game state but the clock: Simulation.Fingerprint (every Movement field, the three threshold timers compared as the
    /// game reads them; transform, body, colliders, interpolation ends; every course's fields; the economy: Cash, box levels,
    /// clones and their timers, buy-max routines; the RNG; the goal tracker's progress and kept boxes; pending invokes and
    /// coroutines; every touched script, toggled object and layer; the rebases) plus the input engine's pending press state. A
    /// join is only ever made between states with equal keys, so a stitched route plays as one continuous run and its
    /// continuation replays exactly.
    /// </summary>
    sealed class LibState
    {
        public int Id;
        /// <summary>Goals reached when the state was filed (the goal touched, for <see cref="LibPoint.Goal"/>).</summary>
        public int Goal;
        public LibPoint Kind;
        /// <summary>The join key: the full-state digest above.</summary>
        public ulong Key;
        /// <summary>The player-only key (movement, engine, tracker; no world): what joins would be if the world were left out
        /// (measured, never joined on).</summary>
        public ulong PlayerKey;
        /// <summary>Physics ticks from the run's start on the run that filed it.</summary>
        public long Tick;
        /// <summary>The exact snapshot handle: the filing run and how many of its inputs had been played (replaying them from the
        /// start, or a stored goal state on the same inputs, gives the state back bit for bit).</summary>
        public int Run, Input;
        public LibFeatures Features;
    }

    /// <summary>Exact state to exact state, with its inputs (the filing run's inputs [From.Input, To.Input)).</summary>
    sealed class LibSegment
    {
        public int From, To;
        public InputRuns Inputs;
        public long Ticks;
        public int Run;
    }

    /// <summary>One pull the scheduler hands out: a lever over goals [First, Limit], its CPU slice (CPU seconds) and threads.</summary>
    sealed class Pull
    {
        public int Id;
        public string Lever;
        public int First, Limit;
        public double CpuSeconds;
        public int Threads;
    }

    /// <summary>A recurring input pattern played as one step, from a state that touches the anchor contact.</summary>
    sealed class Macro
    {
        public string Name;
        /// <summary>The contact a state must be at to play it (Land, Wall, DashEnd, Reset, Goal).</summary>
        public LibPoint Anchor;
        public List<InputTick> Inputs;
        /// <summary>Unlock set it was mined under (PlayerCaps id); null: any.</summary>
        public string Caps;
        /// <summary>How often the good routes played it and the ticks it covered there (for ranking).</summary>
        public int Uses;
        public long Ticks;
    }

}
