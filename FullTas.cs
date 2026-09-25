using System;

namespace IGTAPTasMod
{
    // Full-TAS: the whole game from a fresh new game as one continuous input stream (quick restarts are inputs, where the
    // route chooses), scored in physics ticks from the new game's first settled tick to its last goal. Goals are game
    // events that must happen in order. They split the run for display and for enginesim's search windows; nothing
    // restarts, teleports or re-times between them. Shared with enginesim (compiled into both; C# 7.3).
    internal sealed class FullTasGoal
    {
        public const string Start = "start", Finish = "finish", Buy = "buy", Reward = "reward";
        // The config token: "start:1" (course 1's start gate starts its timer), "finish:1" (its end gate stops it, the
        // player's own finish), "buy:buyreward+buyclone" (the game bought each of these boxes, any order), "reward:bonus1"
        // (the game bought the bonus's reward box, the upgrade box nearest the bonus's end rect: the bonus's unlock).
        public string label;
        public string kind;
        // start / finish: the course number.
        public int course;
        // buy / reward: the boxes (a box twice must be bought twice). Each is a box id (BoxId: exactly that box) or a plain
        // hierarchy path (any box at that path; two boxes can share one, e.g. course 2's GLOBALcashPerLoop and bonus 1's).
        public string[] boxes;
        // reward: the bonus's end rect (centre and size, world coordinates), where its reward box was looked for.
        public float x, y, w, h;
        // How far from the bonus's end rect's centre its reward box may be (world units; the boxes are about 157 above).
        public const float RewardBoxRange = 400f;

        // A box's id: its hierarchy path and its sibling index (Transform.GetSiblingIndex; enginesim: the world export's).
        public static string BoxId(string path, int sibling)
        {
            return path + "#" + sibling.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // Which goals a run has reached so far, and when. Events that don't match the next goal change nothing (buying some
    // other box, finishing another course): goals are reached strictly in order.
    internal sealed class FullTasTracker
    {
        public readonly FullTasGoal[] Goals;
        // The first goal not reached yet.
        public int Next;
        // The run is done once Next reaches this (every goal, unless enginesim searches a window of them).
        public int Limit;
        // Bits: which boxes of the next goal (a buy) the game bought so far.
        int _bought;
        // When each goal was reached: inputs consumed (MacroEngine.RunTicks) and physics ticks from the run start. -1: not yet.
        public readonly long[] InputTicks, PhysicsTicks;

        public FullTasTracker(FullTasGoal[] goals)
        {
            Goals = goals;
            Limit = goals.Length;
            InputTicks = new long[goals.Length];
            PhysicsTicks = new long[goals.Length];
            Reset();
        }

        public void Reset()
        {
            Next = 0;
            _bought = 0;
            for (int i = 0; i < Goals.Length; i++) InputTicks[i] = PhysicsTicks[i] = -1;
        }

        public bool Done { get { return Next >= Limit; } }
        public FullTasGoal Current { get { return Next < Goals.Length && Next < Limit ? Goals[Next] : null; } }

        // Each returns true when the event reached the next goal.
        public bool CourseStarted(int course, long input, long physics)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.Start && g.course == course && Reach(input, physics);
        }

        public bool CourseFinished(int course, long input, long physics)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.Finish && g.course == course && Reach(input, physics);
        }

        // The game bought the box at hierarchy path `path` with id `id` (BoxId): a buy or reward goal's box.
        public bool Bought(string path, string id, long input, long physics)
        {
            FullTasGoal g = Current;
            if (g == null || g.kind != FullTasGoal.Buy && g.kind != FullTasGoal.Reward || g.boxes == null) return false;
            for (int i = 0; i < g.boxes.Length; i++)
                if ((_bought & (1 << i)) == 0 && (g.boxes[i] == id || g.boxes[i] == path)) { _bought |= 1 << i; break; }
            return _bought == (1 << g.boxes.Length) - 1 && Reach(input, physics);
        }

        bool Reach(long input, long physics)
        {
            InputTicks[Next] = input;
            PhysicsTicks[Next] = physics;
            Next++;
            _bought = 0;
            return true;
        }

        public sealed class State
        {
            public int Next, Bought;
            public long[] InputTicks, PhysicsTicks;
        }

        public State Capture()
        {
            return new State { Next = Next, Bought = _bought, InputTicks = (long[])InputTicks.Clone(), PhysicsTicks = (long[])PhysicsTicks.Clone() };
        }

        public void Restore(State s)
        {
            Next = s.Next;
            _bought = s.Bought;
            Array.Copy(s.InputTicks, InputTicks, InputTicks.Length);
            Array.Copy(s.PhysicsTicks, PhysicsTicks, PhysicsTicks.Length);
        }

        // What the rest of a run depends on (enginesim's state fingerprint): the goal it is at and that goal's boxes.
        public int Hash() { return Next * 397 ^ _bought; }
    }
}
