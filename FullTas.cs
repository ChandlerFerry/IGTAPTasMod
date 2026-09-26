using System;

namespace IGTAPTasMod
{
    internal sealed class FullTasGoal
    {
        public const string Start = "start", Finish = "finish", Buy = "buy", Reward = "reward", At = "at";
        public string label;
        public string kind;
        public int course;
        public string[] boxes;
        public float x, y, w, h;
        public int from;
        public const int FromLeft = 1, FromRight = 2, FromBottom = 4, FromTop = 8;
        public static readonly string[] SideNames = { "left", "right", "bottom", "top" };
        public const float RewardBoxRange = 400f;

        public static string BoxId(string path, int sibling)
        {
            return path + "#" + sibling.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool EnteredFrom(float rx0, float rx1, float ry0, float ry1, float px0, float px1, float py0, float py1)
        {
            return from == 0
                || (from & FromLeft) != 0 && px1 <= rx0 || (from & FromRight) != 0 && px0 >= rx1
                || (from & FromBottom) != 0 && py1 <= ry0 || (from & FromTop) != 0 && py0 >= ry1;
        }
    }

    internal sealed class FullTasTracker
    {
        public readonly FullTasGoal[] Goals;
        public int Next;
        public int Limit;
        int _bought;
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

        public bool Touched(long input, long physics)
        {
            FullTasGoal g = Current;
            return g != null && g.kind == FullTasGoal.At && Reach(input, physics);
        }

        public void QuickRestarted()
        {
            if (Next == 0 || Next > Limit || Goals[Next - 1].kind != FullTasGoal.At) return;
            Next--;
            InputTicks[Next] = PhysicsTicks[Next] = -1;
            _bought = 0;
        }

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

        public int Hash() { return Next * 397 ^ _bought; }
    }
}
