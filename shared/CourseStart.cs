using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace IGTAPTasMod
{
    // A quick restart keeps Movement history, RNG and clone timers; every category start sets these to fixed values.
    internal static class CourseStart
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Movement fields a quick restart keeps, at launched-game values; framesSinceLastDash need only exceed every window.
        static readonly KeyValuePair<FieldInfo, object>[] History =
        {
            Field("facingRight", true),
            Field("wallJumpFacing", new Vector2(1f, 0f)),
            Field("dashDirection", new Vector2(0f, 0f)),
            Field("wallCoyoteDir", 0f),
            Field("framesSinceLastDash", 1000f),
            Field("MostRecentUpwardsMoveWasNormalJump", false),
            Field("timeSinceLastAfterimage", 0f),
            Field("emitWallSlideParticlesThisFrame", false),
            Field("preDashMomentum", new Vector2(0f, 0f)),
            Field("finalWallClingTimeLeft", 0f),
        };

        // Standing, the game rewrites these each tick; airborne they are the last landing's: a start takes launched-game values.
        static readonly FieldInfo LastHyperY = typeof(Movement).GetField("lastHyperY", Fields),
            HeightOfLastJumpStart = typeof(Movement).GetField("heightOfLastJumpStart", Fields),
            OnGround = typeof(Movement).GetField("onGround", Fields);
        const float NoHyperY = 100000000f;

        static readonly int[] RandomState = { -959912050, 848757813, -1712370445, -552349168 };
        static readonly FieldInfo[] RandomWords = typeof(Random.State).GetFields(Fields);

        internal static int[] Words(Random.State state)
        {
            return System.Array.ConvertAll(RandomWords, f => (int)f.GetValue(state));
        }

        const float CloneTimer = 10000f;
        static readonly FieldInfo CloneTimeSinceLast = typeof(clonesScript).GetField("timeSinceLastClone", Fields);

        static KeyValuePair<FieldInfo, object> Field(string name, object value)
        {
            return new KeyValuePair<FieldInfo, object>(typeof(Movement).GetField(name, Fields), value);
        }

        public static void Apply(Movement player, IEnumerable<clonesScript> clones)
        {
            foreach (KeyValuePair<FieldInfo, object> f in History) f.Key.SetValue(player, f.Value);
            if (!(bool)OnGround.GetValue(player))
            {
                LastHyperY.SetValue(player, NoHyperY);
                HeightOfLastJumpStart.SetValue(player, player.transform.position.y);
            }
            object state = new Random.State();
            for (int i = 0; i < RandomWords.Length; i++) RandomWords[i].SetValue(state, RandomState[i]);
            Random.state = (Random.State)state;
            foreach (clonesScript c in clones) if (c != null) CloneTimeSinceLast.SetValue(c, CloneTimer);
        }

        static readonly FieldInfo DoorToOpen = typeof(upgradeBox).GetField("doorToOpen", Fields),
            MoverIndex = typeof(PlatformMover).GetField("indexInPositions", Fields),
            MoverPositions = typeof(PlatformMover).GetField("Positions", Fields);

        // Courses 6-15 and bonus 11 start with the atom room's door open, without buying the atom (RULES.md 21).
        public static bool AssumesAtomDoor(int course) { return course >= 6 && course <= 15 || course == 111 || IsTreeTransition(course); }

        // Tree transition ids are 1000 + 10 x level + course.
        public static bool IsTreeTransition(int course) { return course >= 1000 && course < 2000; }

        // Opens the door as a load does (doorToOpen.JumpToNextState: stop index 1, 380 up); a door already there stays.
        public static bool OpenAtomDoor(IEnumerable<upgradeBox> boxes) { return OpenDoors(boxes, IsAtomBox); }

        public static bool OpenArea2Door(IEnumerable<upgradeBox> boxes)
        {
            return OpenDoors(boxes, b => b != null && b.globalUpgrade == globalStats.globalUpgradeSet.area2DoorOpen
                && DoorToOpen.GetValue(b) is PlatformMover door && door != null);
        }

        static bool OpenDoors(IEnumerable<upgradeBox> boxes, System.Func<upgradeBox, bool> which)
        {
            bool moved = false;
            foreach (upgradeBox box in boxes)
            {
                if (!which(box) || !(DoorToOpen.GetValue(box) is PlatformMover door)
                    || !(MoverPositions.GetValue(door) is System.Array stops) || stops.Length < 2 || (int)MoverIndex.GetValue(door) != 0) continue;
                object open = stops.GetValue(1);
                MoverIndex.SetValue(door, 1);
                door.transform.localPosition = (Vector2)open.GetType().GetField("position").GetValue(open);
                moved = true;
            }
            if (moved) Physics2D.SyncTransforms();
            return moved;
        }

        public static bool IsAtomBox(upgradeBox box)
        {
            return box != null && box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && DoorToOpen.GetValue(box) is PlatformMover door && door != null;
        }
    }
}
