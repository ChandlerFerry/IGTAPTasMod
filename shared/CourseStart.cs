using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace IGTAPTasMod
{
    /// <summary>
    /// A category start's history, fixed. The quick restart (<c>Movement.respawn(true)</c>) puts the player back and resets the
    /// run's state, but not a set of Movement history fields, the RNG or the clone timers, so a start captured after a run
    /// carried that run's leftovers and one captured right after a launch had the launch values (docs/fidelity-log.md,
    /// "Course start captured with its zone switched off, and the quick restart's history"). Every category start sets
    /// them to one set of values, a game just launched has (the field initializers, <c>clonesScript.Start</c>'s timer): the
    /// plugin when it restores the quick restart and when it captures the start state, and the offline engine when it loads
    /// a course trace's start for a search, so a trace captured before this searches from the start the game now judges.
    /// Shared source: the plugin and enginesim/Runtime (linked) compile it against their own game assembly.
    /// </summary>
    internal static class CourseStart
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Movement fields a quick restart keeps from the run before, at a launched game's values. <c>framesSinceLastDash</c>
        /// only has to be past every window it opens (the dash boost's 0-6 frames, the wall bounce's 8.4, a dash's 10).
        /// <c>preDashMomentum</c> and <c>finalWallClingTimeLeft</c> are written before any read; fixed so starts compare equal.
        /// </summary>
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

        /// <summary>Standing, the game writes these every tick (touchedSolidGround/touchedMossyGround); in the air they are
        /// the last landing's: an airborne start takes a launched game's (no hyper yet) and its own height.</summary>
        static readonly FieldInfo LastHyperY = typeof(Movement).GetField("lastHyperY", Fields),
            HeightOfLastJumpStart = typeof(Movement).GetField("heightOfLastJumpStart", Fields),
            OnGround = typeof(Movement).GetField("onGround", Fields);
        const float NoHyperY = 100000000f;

        /// <summary>The RNG state (xorshift128 words s0-s3) every category start draws from.</summary>
        static readonly int[] RandomState = { -959912050, 848757813, -1712370445, -552349168 };
        static readonly FieldInfo[] RandomWords = typeof(Random.State).GetFields(Fields);

        /// <summary>An RNG state's words s0-s3 (private in the game's Unity).</summary>
        internal static int[] Words(Random.State state)
        {
            return System.Array.ConvertAll(RandomWords, f => (int)f.GetValue(state));
        }

        /// <summary><c>clonesScript.timeSinceLastClone</c> as <c>clonesScript.Start</c> leaves it.</summary>
        const float CloneTimer = 10000f;
        static readonly FieldInfo CloneTimeSinceLast = typeof(clonesScript).GetField("timeSinceLastClone", Fields);

        static KeyValuePair<FieldInfo, object> Field(string name, object value)
        {
            return new KeyValuePair<FieldInfo, object>(typeof(Movement).GetField(name, Fields), value);
        }

        /// <summary>Sets the player's history fields, the RNG and every clone timer to the category start's values.</summary>
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

        /// <summary>
        /// Courses 6-15 and bonus 11 run as if the atom room's door were open (RULES.md 21): the free first atom (the
        /// <c>AtomUpgrade</c> box, <c>spawnNewAtom</c>) bought, without its purchase or its atom prestige. Course 9's end is
        /// reachable only through that door. <paramref name="course"/> is courses.json's id: 11-15 are Courses 1-5 overgrown,
        /// 111 bonus 11 (secret 14, its start the checkpoint by the door).
        /// </summary>
        public static bool AssumesAtomDoor(int course) { return course >= 6 && course <= 15 || course == 111 || IsTreeTransition(course); }

        /// <summary>A tree transition (courses.json id 1000 + 10 × level + course): from bonus11's checkpoint by the atom room.</summary>
        public static bool IsTreeTransition(int course) { return course >= 1000 && course < 2000; }

        /// <summary>
        /// The atom room's door opened as a game load opens it once the first atom was bought (PlayerPrefs <c>OpenA2Door</c>
        /// or the box's <c>TimesUsed</c>): <c>upgradeBox.Start</c> of the <c>spawnNewAtom</c> box calls its
        /// <c>doorToOpen.JumpToNextState()</c>, which takes the door from its first stop to the next (index 1, the stop's
        /// local position, 380 up). A door already there is left alone. Whether the door moved.
        /// </summary>
        public static bool OpenAtomDoor(IEnumerable<upgradeBox> boxes) { return OpenDoors(boxes, IsAtomBox); }

        /// <summary>The Area 2 door (TreePart7's box, <c>area2DoorOpen</c>) open, as the bought box's Start leaves it
        /// (upgradeBox.cs:260): the tree transitions from level 8.</summary>
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

        /// <summary>The box whose first purchase opens the atom room's door: <c>spawnNewAtom</c> with a <c>doorToOpen</c>
        /// (<c>AtomUpgrades/AtomUpgrade</c>; Course 6's Bp/Rp atom boxes have none).</summary>
        public static bool IsAtomBox(upgradeBox box)
        {
            return box != null && box.globalUpgrade == globalStats.globalUpgradeSet.spawnNewAtom && DoorToOpen.GetValue(box) is PlatformMover door && door != null;
        }
    }
}
