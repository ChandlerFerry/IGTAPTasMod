using System;
using System.Collections.Generic;

namespace IGTAP.EngineSim.Cli
{
    // The Full-TAS search's shared contracts (docs/fulltas.md, "Auto"): stage 1's goal-state library and exact stitching
    // (FullTasAutoLib.cs), stage 2's learned cost-to-go, stage 3's pull scheduler, stage 4's macro edges. Each stage implements
    // its interface in its own file; FullTasAuto.cs wires them into --auto. Physics ticks everywhere; goal k is 1-based (goal 0:
    // the run's start).

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
        public float Speed => (float)Math.Sqrt((VX + MX) * (VX + MX) + (VY + MY) * (VY + MY));
    }

    /// <summary>
    /// An exact state some simulated run passed through. Two states with the same <see cref="Key"/> (and goal) are bit-identical
    /// in everything that moves the player: every Movement field (the three threshold timers compared as the game reads them),
    /// transform, body, colliders, interpolation ends, the player's invokes, the input engine's pending press state, the goal
    /// tracker. A join is only ever made between states with equal keys, so a stitched route plays as one continuous run.
    /// </summary>
    sealed class LibState
    {
        public int Id;
        /// <summary>Goals reached when the state was filed (the goal touched, for <see cref="LibPoint.Goal"/>).</summary>
        public int Goal;
        public LibPoint Kind;
        public ulong Key;
        /// <summary>Physics ticks from the run's start on the run that filed it.</summary>
        public long Tick;
        /// <summary>The exact snapshot handle: the filing run and how many of its inputs had been played (replaying them from the
        /// start, or a stored goal state on the same inputs, gives the state back bit for bit).</summary>
        public int Run, Input;
        public LibFeatures Features;
    }

    /// <summary>Exact state to exact state, with its inputs (the filing run's inputs [From.Input, To.Input)).</summary>
    sealed class Segment
    {
        public int From, To;
        public InputRuns Inputs;
        public long Ticks;
        public int Run;
    }

    interface IStateLibrary
    {
        /// <summary>Files a run replayed from the start (every goal touch and chain point on it); returns its run id.</summary>
        int Add(IReadOnlyList<InputTick> inputs, string how);
        LibState State(int id);
        /// <summary>Known ways on from a state: every segment leaving it or any state with the same key and goal.</summary>
        IReadOnlyList<Segment> Continuations(int stateId);
        /// <summary>The fastest known way from the start to the last goal through exact joins only: its segments in order, the
        /// predicted end tick (long.MaxValue when none reaches the end).</summary>
        (IReadOnlyList<Segment> path, long end) ShortestPath();
        /// <summary>States filed at goal touches of this goal (1-based).</summary>
        IReadOnlyList<LibState> StatesAt(int goal);
        /// <summary>The known end tick from a state (the fastest way on from it), long.MaxValue when none reaches the end.</summary>
        long ToEnd(int stateId);
        int Runs { get; }
    }

    /// <summary>Stage 2: estimated physics ticks from an arrival at a goal to the run's end.</summary>
    interface ICostToGo
    {
        double Ticks(int goal, in LibFeatures features);
        /// <summary>Refits from every library state with a known way to the end (after each new segment or best).</summary>
        void Fit(IStateLibrary library);
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

    /// <summary>Stage 3: successive halving across every goal region.</summary>
    interface IPullScheduler
    {
        /// <summary>The next pull (null: nothing left worth pulling).</summary>
        Pull Next();
        /// <summary>What the pull gave: ticks the whole best gained (the library's shortest path) and the CPU seconds it used.</summary>
        void Report(Pull pull, long ticksGained, double cpuSeconds);
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

    /// <summary>Stage 4: primitives mined from the good routes, handed to windows as seeds or one-step edges.</summary>
    interface IMacroSource
    {
        /// <summary>Re-mines from the library's good routes (after a new best).</summary>
        void Mine(IStateLibrary library);
        /// <summary>Macros playable from this state (its anchor kind and unlocks match).</summary>
        IReadOnlyList<Macro> For(LibState state, string caps);
    }
}
