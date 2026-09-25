using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    // The game's quick restart, the upgrade boxes' identity, the route-name rules and playback bookkeeping. These lived
    // in Chains.cs while chains existed; a route or a Full-TAS run uses them just the same.
    public sealed partial class TasPlugin
    {
        static readonly MethodInfo RespawnMethod = AccessTools.Method(typeof(Movement), "respawn");

        // What the game does on a QuickReset press (Movement.Update): respawn(true), nothing else.
        void QuickRestart(Movement movement)
        {
            RespawnMethod.Invoke(movement, new object[] { true });
        }

        // An upgrade box's identity: its hierarchy path in the Overworld scene ("Courses/course 1/localUpgrades/Wall
        // jump"); paths aren't unique, so FullTasGoal.BoxId adds the sibling index.
        static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // Run names are letters, digits and "%" ("ANY%"): route names ("fulltas/ANY%_NO_WJ 812.40") and offline-best
        // files carry them.
        static readonly Regex RunName = new Regex(@"^[A-Za-z0-9%]+$");

        // A route name's transition token ("1to2", "2tobonus1"): "<from>to<to>", where each side is a course id or a
        // bonus name (standalone transitions are named with it: AddTransitionFromRecording).
        static readonly Regex TransitionToken = new Regex(@"^(newgame|bonus\d+|\d+)to(bonus\d+|\d+)$", RegexOptions.IgnoreCase);

        // Playback in progress: a route or a Full-TAS run (one that is about to start included).
        bool PlaybackActive { get { return _engine.IsRunning || _fullTasPlay != null; } }

        // Stops a route or Full-TAS playback.
        void StopPlayback(string reason)
        {
            _pendingReplay = null;
            _pendingReplayStartDelayTicks = 0;
            DropPendingFullTasPlayback(reason);
            if (_engine.IsRunning) _engine.Abort(reason);
        }

        // upgradeBox.DoBuyUpgrade postfix: a Full-TAS run's purchase is a goal event (and its unlocks count from here),
        // never the end of the run by itself (only its last goal does).
        private static void AfterBuyUpgrade(upgradeBox __instance)
        {
            if (Instance == null || !Instance._engine.IsRunning || __instance == null || Instance._fullTas == null) return;
            Instance.FullTasBought(__instance);
        }
    }
}
