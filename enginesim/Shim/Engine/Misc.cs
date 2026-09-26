using System;
using System.Collections.Generic;
using IGTAP.EngineSim;

namespace UnityEngine
{
    public static class Time
    {
        public static float fixedDeltaTime { get; set; } = 0.0199999921f;
        public static float deltaTime { get { return fixedDeltaTime; } }
        public static float fixedTime { get; set; }
        public static float time { get; set; }
        public static float unscaledDeltaTime { get { return fixedDeltaTime; } }
        public static float timeScale { get; set; } = 1f;
        public static int frameCount { get; set; }
        public static float realtimeSinceStartup { get { return 0f; } }
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void Log(object message, Object context) { }
        public static void LogWarning(object message) { }
        public static void LogWarning(object message, Object context) { }
        public static void LogError(object message) { }
        public static void LogError(object message, Object context) { }
        public static void LogException(Exception exception) { }
        public static void DrawRay(Vector3 start, Vector3 dir) { }
        public static void DrawRay(Vector3 start, Vector3 dir, Color color) { }
        public static void DrawRay(Vector3 start, Vector3 dir, Color color, float duration) { }
        public static void DrawLine(Vector3 start, Vector3 end) { }
        public static void DrawLine(Vector3 start, Vector3 end, Color color) { }
        public static void DrawLine(Vector3 start, Vector3 end, Color color, float duration) { }
    }

    public static class Random
    {
        public struct State
        {
            public int s0, s1, s2, s3;
        }

        [ThreadStatic] static uint _s0, _s1, _s2, _s3;

        public static void InitState(int seed)
        {
            unchecked
            {
                _s0 = (uint)seed;
                _s1 = 1812433253u * _s0 + 1u;
                _s2 = 1812433253u * _s1 + 1u;
                _s3 = 1812433253u * _s2 + 1u;
            }
        }

        public static State state
        {
            get { return new State { s0 = (int)_s0, s1 = (int)_s1, s2 = (int)_s2, s3 = (int)_s3 }; }
            set { _s0 = (uint)value.s0; _s1 = (uint)value.s1; _s2 = (uint)value.s2; _s3 = (uint)value.s3; }
        }

        public static uint Draw()
        {
            unchecked
            {
                uint t = _s0 ^ (_s0 << 11);
                _s0 = _s1; _s1 = _s2; _s2 = _s3;
                return _s3 = (_s3 ^ (_s3 >> 19)) ^ (t ^ (t >> 8));
            }
        }

        public static float value { get { return (Draw() & 0x7FFFFFu) / 8388607f; } }

        public static int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive == minInclusive) return minInclusive;
            long range = (long)maxExclusive - minInclusive, draw = Draw();
            return maxExclusive < minInclusive ? (int)(minInclusive - draw % range) : (int)(minInclusive + draw % range);
        }

        public static float Range(float minInclusive, float maxInclusive)
        {
            float unit = value;
            return minInclusive * unit + maxInclusive * (1f - unit);
        }
    }

    public struct LayerMask
    {
        int m_Mask;
        public int value { get { return m_Mask; } set { m_Mask = value; } }
        public static implicit operator int(LayerMask mask) { return mask.m_Mask; }
        public static implicit operator LayerMask(int intVal) { LayerMask result = default; result.m_Mask = intVal; return result; }
        public static string LayerToName(int layer) { return layer >= 0 && layer < 32 ? Engine.LayerNames[layer] ?? "" : ""; }
        public static int NameToLayer(string layerName) { return Array.IndexOf(Engine.LayerNames, layerName); }

        public static int GetMask(params string[] layerNames)
        {
            if (layerNames == null) throw new ArgumentNullException("layerNames");
            int num = 0;
            foreach (string layerName in layerNames)
            {
                int num2 = NameToLayer(layerName);
                if (num2 != -1) num |= 1 << num2;
            }
            return num;
        }
    }

    public sealed class Animator : Behaviour
    {
        readonly Dictionary<string, int> _ints = new Dictionary<string, int>();
        readonly Dictionary<string, float> _floats = new Dictionary<string, float>();
        readonly Dictionary<string, bool> _bools = new Dictionary<string, bool>();
        public float speed { get; set; } = 1f;
        public void SetInteger(string name, int value) { _ints[name] = value; }
        public int GetInteger(string name) { return _ints.TryGetValue(name, out int v) ? v : 0; }
        public void SetFloat(string name, float value) { _floats[name] = value; }
        public float GetFloat(string name) { return _floats.TryGetValue(name, out float v) ? v : 0f; }
        public void SetBool(string name, bool value) { _bools[name] = value; }
        public bool GetBool(string name) { return _bools.TryGetValue(name, out bool v) && v; }
        public void SetTrigger(string name) { }
        public void ResetTrigger(string name) { }
        public void Play(string stateName) { }
        public void ForceStateNormalizedTime(float normalizedTime) { }
        public void InitIntegers(Dictionary<string, int> values) { foreach (var kv in values) _ints[kv.Key] = kv.Value; }
    }

    public sealed class AudioClip : Object { public float length { get; set; } }
    public sealed class AudioSource : Behaviour
    {
        public AudioClip clip { get; set; }
        public bool loop { get; set; }
        public float pitch { get; set; } = 1f;
        public float time { get; set; }
        public float volume { get; set; } = 1f;
        public float spatialBlend { get; set; }
        public bool isPlaying { get; set; }
        public Audio.AudioMixerGroup outputAudioMixerGroup { get; set; }
        public void Play() { }
        public void Stop() { }
        public void PlayOneShot(AudioClip clip) { }
        public void PlayOneShot(AudioClip clip, float volumeScale) { }
    }

    public sealed class ParticleSystem : Component
    {
        internal float m_RateOverTime;
        public bool isPlaying { get; set; }
        public float startSpeed { get; set; }
        public void Emit(int count) { }
        public void Play() { isPlaying = true; }
        public void Stop() { isPlaying = false; }
        public void Clear() { }
        public EmissionModule emission { get { return new EmissionModule(this); } }
        public MainModule main { get { return new MainModule(this); } }
        public InheritVelocityModule inheritVelocity { get { return new InheritVelocityModule(this); } }

        public struct EmissionModule
        {
            readonly ParticleSystem _system;
            internal EmissionModule(ParticleSystem system) { _system = system; }
            public MinMaxCurve rateOverTime { get { return _system.m_RateOverTime; } set { _system.m_RateOverTime = value.constant; } }
            public bool enabled { get; set; }
        }

        public struct MainModule
        {
            readonly ParticleSystem _system;
            internal MainModule(ParticleSystem system) { _system = system; }
            public MinMaxGradient startColor { get { return default; } set { } }
            public float startSizeMultiplier { get { return 1f; } set { } }
        }

        public struct InheritVelocityModule
        {
            readonly ParticleSystem _system;
            internal InheritVelocityModule(ParticleSystem system) { _system = system; }
            public bool enabled { get { return false; } set { } }
        }

        public struct MinMaxCurve
        {
            public float constant { get; set; }
            public MinMaxCurve(float constant) { this.constant = constant; }
            public static implicit operator MinMaxCurve(float constant) { return new MinMaxCurve(constant); }
        }

        public struct MinMaxGradient
        {
            public Color color { get; set; }
            public static implicit operator MinMaxGradient(Color color) { return new MinMaxGradient { color = color }; }
        }

        public struct Particle { public Vector3 position { get; set; } }
    }

    public class Renderer : Component
    {
        public bool enabled { get; set; } = true;
        public Material material { get; set; } = new Material();
        public Material sharedMaterial { get; set; }
        public int sortingOrder { get; set; }
        public bool isVisible { get { return false; } }
        public void SetPropertyBlock(MaterialPropertyBlock properties) { }
    }

    public sealed class SpriteRenderer : Renderer
    {
        public Color color { get; set; } = Color.white;
        public Sprite sprite { get; set; }
        public bool flipX { get; set; }
    }

    public sealed class Sprite : Object { }
    public sealed class Material : Object { public void SetFloat(string name, float value) { } public void SetColor(string name, Color value) { } }
    public sealed class MaterialPropertyBlock { public void SetFloat(string name, float value) { } public void SetColor(string name, Color value) { } }
    public sealed class Canvas : Behaviour { public static void ForceUpdateCanvases() { } }
    public sealed class Camera : Behaviour { public static Camera main { get; set; } public float orthographicSize { get; set; } }

    public static class Application
    {
        public static string persistentDataPath { get { return ""; } }
        public static string streamingAssetsPath { get { return ""; } }
        public static bool isPlaying { get { return true; } }
    }
}

namespace UnityEngine.Audio
{
    public sealed class AudioMixerGroup : UnityEngine.Object { }
}

namespace UnityEngine.Rendering.Universal
{
    public sealed class Light2D : UnityEngine.Behaviour { public float pointLightOuterRadius { get; set; } public float intensity { get; set; } }
}

namespace UnityEngine.Tilemaps
{
    public sealed class Tilemap : UnityEngine.Component { public UnityEngine.Color color { get; set; } }
    public sealed class TilemapRenderer : UnityEngine.Renderer { }
    public sealed class TilemapCollider2D : UnityEngine.Collider2D { }
}

namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.Behaviour { public UnityEngine.Color color { get; set; } }
    public class Image : Graphic { }
    public class Text : Graphic { public string text { get; set; } }
}

namespace TMPro
{
    public class TMP_Text : UnityEngine.UI.Graphic { public string text { get; set; } = ""; }
    public class TextMeshProUGUI : TMP_Text { }
    public class TextMeshPro : TMP_Text { }
}

namespace UnityEngine.Localization
{
    public class LocalizedString { public string GetLocalizedString() { return ""; } }
    public struct LocaleIdentifier { public string Code { get { return "en"; } } }
    public class Locale : UnityEngine.Object { public LocaleIdentifier Identifier { get { return default; } } }
}

namespace UnityEngine.Localization.Settings
{
    public static class LocalizationSettings { public static Locale SelectedLocale { get { return new Locale(); } } }
}

namespace UnityEngine.Localization.Components
{
    public class LocalizeStringEvent : UnityEngine.MonoBehaviour { }
}

namespace UnityEngine.InputSystem
{
    public class InputAction
    {
        public TValue ReadValue<TValue>() where TValue : struct
        {
            if (typeof(TValue) != typeof(Vector2) || IGTAP.EngineSim.Engine.Stick == null) return default;
            Vector2 stick = IGTAP.EngineSim.Engine.Stick();
            return System.Runtime.CompilerServices.Unsafe.As<Vector2, TValue>(ref stick);
        }
        public bool WasPressedThisFrame() { return false; }
        public bool WasReleasedThisFrame() { return false; }
        public bool IsPressed() { return false; }
        public bool triggered { get { return false; } }
    }

    public class PlayerInput : UnityEngine.Behaviour { }
}

namespace Steamworks
{
    public static class SteamUserStats
    {
        public static bool GetAchievement(string pchName, out bool pbAchieved) { pbAchieved = false; return false; }
        public static bool SetAchievement(string pchName) { return false; }
        public static bool GetStat(string pchName, out int pData) { pData = 0; return false; }
        public static bool SetStat(string pchName, int nData) { return false; }
        public static bool StoreStats() { return false; }
    }

    public delegate void SteamAPIWarningMessageHook_t(int nSeverity, System.Text.StringBuilder pchDebugText);
}

namespace Unity.Mathematics
{
    public static class math
    {
        public static double ceil(double x) { return Math.Ceiling(x); }
        public static double floor(double x) { return Math.Floor(x); }
        public static double round(double x) { return Math.Round(x); }
        public static double log10(double x) { return Math.Log10(x); }
        public static double pow(double x, double y) { return Math.Pow(x, y); }
        public static double max(double x, double y) { return x > y ? x : y; }
        public static double min(double x, double y) { return x < y ? x : y; }
        public static float ceil(float x) { return MathF.Ceiling(x); }
        public static float floor(float x) { return MathF.Floor(x); }
    }
}
