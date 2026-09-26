using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        // "Breaker lights" (studio toggle): Area 1 lit as before the breaker tripped (tripBreakerScript's globalLight) plus
        // every breaker area light. Visual only: it sets the lights' GameObjects, never activeLights/fixedAreas (the save
        // and the economy read those), and turning it off puts back what the game's own state shows.
        static readonly AccessTools.FieldRef<tripBreakerScript, bool[]> TrippedLights = AccessTools.FieldRefAccess<tripBreakerScript, bool[]>("activeLights");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject> ForcedAllLights = AccessTools.FieldRefAccess<tripBreakerScript, GameObject>("allLights");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject> ForcedGlobalLight = AccessTools.FieldRefAccess<tripBreakerScript, GameObject>("globalLight");
        static readonly AccessTools.FieldRef<tripBreakerScript, GameObject[]> ForcedAreaLights = AccessTools.FieldRefAccess<tripBreakerScript, GameObject[]>("areaLights");

        readonly List<tripBreakerScript> _lightBreakers = new List<tripBreakerScript>();
        // Before the lights were forced, per breaker: allLights' and globalLight's states, and whether it had tripped.
        readonly Dictionary<tripBreakerScript, (bool all, bool global, bool tripped)> _lightsBefore = new Dictionary<tripBreakerScript, (bool, bool, bool)>();
        float _nextBreakerSearch;

        void BreakerLightsFrame()
        {
            if (_settings.forceBreakerLights) ForceBreakerLights();
            else if (_lightsBefore.Count > 0) ReleaseBreakerLights();
        }

        void ForceBreakerLights()
        {
            // A zone or scene load replaces the breaker: look again (at most once a second while none is loaded).
            if ((_lightBreakers.Count == 0 || _lightBreakers.Exists(b => b == null)) && Time.unscaledTime >= _nextBreakerSearch)
            {
                _nextBreakerSearch = Time.unscaledTime + 1f;
                _lightBreakers.Clear();
                _lightBreakers.AddRange(FindObjectsByType<tripBreakerScript>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                _lightsBefore.Clear();
            }
            foreach (tripBreakerScript breaker in _lightBreakers)
            {
                if (breaker == null) continue;
                GameObject all = ForcedAllLights(breaker), global = ForcedGlobalLight(breaker);
                if (!_lightsBefore.ContainsKey(breaker))
                    _lightsBefore[breaker] = (all != null && all.activeSelf, global != null && global.activeSelf, Tripped(breaker));
                if (global != null && !global.activeSelf) global.SetActive(true);
                if (all != null && !all.activeSelf) all.SetActive(true);
                foreach (GameObject area in ForcedAreaLights(breaker) ?? new GameObject[0])
                    if (area != null && !area.activeSelf) area.SetActive(true);
            }
        }

        static bool Tripped(tripBreakerScript breaker)
        {
            bool[] lit = TrippedLights(breaker);
            return lit != null && lit.Length > 0 && lit[0];
        }

        void ReleaseBreakerLights()
        {
            foreach (var entry in _lightsBefore)
            {
                tripBreakerScript breaker = entry.Key;
                if (breaker == null) continue;
                bool[] lit = TrippedLights(breaker) ?? new bool[0];
                GameObject[] areas = ForcedAreaLights(breaker) ?? new GameObject[0];
                for (int i = 0; i < areas.Length; i++)
                    if (areas[i] != null) areas[i].SetActive(i < lit.Length && lit[i]);
                GameObject all = ForcedAllLights(breaker), global = ForcedGlobalLight(breaker);
                if (all != null) all.SetActive(entry.Value.all || System.Array.IndexOf(lit, true) >= 0);
                // TripBreaker turns the global light off: a trip while forced leaves it off.
                if (global != null) global.SetActive(entry.Value.global && (entry.Value.tripped || !Tripped(breaker)));
            }
            _lightsBefore.Clear();
            _lightBreakers.Clear();
        }
    }
}
