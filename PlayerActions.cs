using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        static readonly MethodInfo RespawnMethod = AccessTools.Method(typeof(Movement), "respawn");

        void QuickRestart(Movement movement)
        {
            string refused = _fullTas != null && _engine.IsRunning
                ? _fullTas.RestartRefused(movement.respawnPoint.x, movement.respawnPoint.y, movement.courseResetPoint != Vector2.zero) : null;
            if (refused != null)
            {
                _engine.Abort(refused + " at action " + (_engine.StepIndex + 1));
                return;
            }
            RespawnMethod.Invoke(movement, new object[] { true });
            if (_fullTas != null && _engine.IsRunning) _fullTas.QuickRestarted();
        }

        static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        static readonly Regex RunName = new Regex(@"^[A-Za-z0-9%-]+$");

        bool PlaybackActive { get { return _engine.IsRunning || _fullTasPlay != null || _tas != null; } }

        void StopPlayback(string reason)
        {
            if (_tas != null) { TasClose(reason); return; }
            _pendingReplay = null;
            _pendingReplayStartDelayTicks = 0;
            DropPendingFullTasPlayback(reason);
            if (_engine.IsRunning) _engine.Abort(reason);
        }

        private static void AfterBuyUpgrade(upgradeBox __instance)
        {
            if (Instance == null || !Instance._engine.IsRunning || __instance == null || Instance._fullTas == null) return;
            Instance.FullTasBought(__instance);
        }
    }
}
