using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        internal static readonly AccessTools.FieldRef<tripBreakerScript, bool[]> BreakerActiveLights = AccessTools.FieldRefAccess<tripBreakerScript, bool[]>("activeLights");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject> BreakerAllLights = AccessTools.FieldRefAccess<tripBreakerScript, GameObject>("allLights");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject> BreakerGlobalLight = AccessTools.FieldRefAccess<tripBreakerScript, GameObject>("globalLight");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject[]> BreakerAreaLights = AccessTools.FieldRefAccess<tripBreakerScript, GameObject[]>("areaLights");

        readonly List<tripBreakerScript> _lightBreakers = new List<tripBreakerScript>();
        float _nextBreakerSearch;
        int _breakerSearchGeneration = -1;

        void BreakerLightsFrame()
        {
            if (_settings.forceBreakerLights) ForceBreakerLights();
            else if (_lightBreakers.Count > 0) ReleaseBreakerLights();
        }

        void ForceBreakerLights()
        {
            if ((_lightBreakers.Count == 0 || _lightBreakers.Exists(b => b == null)) && Time.unscaledTime >= _nextBreakerSearch)
            {
                _nextBreakerSearch = Time.unscaledTime + 1f;
                int generation = SceneGeneration;
                if (generation != _breakerSearchGeneration || _lightBreakers.Count > 0)
                {
                    _breakerSearchGeneration = generation;
                    _lightBreakers.Clear();
                    _lightBreakers.AddRange(FindObjectsByType<tripBreakerScript>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                }
            }
            foreach (tripBreakerScript breaker in _lightBreakers)
            {
                if (breaker == null) continue;
                GameObject all = BreakerAllLights(breaker), global = BreakerGlobalLight(breaker);
                if (global != null && !global.activeSelf) global.SetActive(true);
                if (all != null && !all.activeSelf) all.SetActive(true);
                GameObject[] areas = BreakerAreaLights(breaker);
                if (areas == null) continue;
                foreach (GameObject area in areas)
                    if (area != null && !area.activeSelf) area.SetActive(true);
            }
        }

        void ReleaseBreakerLights()
        {
            foreach (tripBreakerScript breaker in _lightBreakers)
            {
                if (breaker == null) continue;
                foreach (var (light, on) in GameBreakerLights(breaker))
                    if (light.activeSelf != on) light.SetActive(on);
            }
            _lightBreakers.Clear();
            _breakerSearchGeneration = -1;
        }

        internal static List<(GameObject light, bool on)> GameBreakerLights(tripBreakerScript breaker)
        {
            globalStats stats = Singleton<globalStats>.Instance;
            bool overgrown = stats != null && stats.currentA1State == globalStats.area1states.Overgrown;
            bool tripped = stats != null && (stats.currentA1State == globalStats.area1states.tripBreaker
                || stats.mainMenuState == globalStats.area1states.tripBreaker);
            var lights = new List<(GameObject, bool)>();
            GameObject global = BreakerGlobalLight(breaker), all = BreakerAllLights(breaker);
            if (global != null) lights.Add((global, !overgrown && !tripped));
            if (all != null) lights.Add((all, !overgrown));
            bool[] lit = BreakerActiveLights(breaker) ?? new bool[0];
            GameObject[] areas = BreakerAreaLights(breaker) ?? new GameObject[0];
            for (int i = 0; i < areas.Length; i++)
                if (areas[i] != null) lights.Add((areas[i], i < lit.Length && lit[i]));
            return lights;
        }
    }
}
