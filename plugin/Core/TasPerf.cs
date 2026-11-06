using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        struct PerfFrame { public float dt, clock, wait, update, pump, gui, skin; public int gc; public bool ticked; public long allocUpdate, allocGui, allocRest; }
        static readonly string[] PerfSectionNames = { "ghosts", "trainer", "goals", "origin", "techs", "spikes", "checkpoints", "graze", "panels", "dropped", "redraw", "blit", "pad", "live" };
        const int PerfSectionCount = 14;
        readonly float[] _perfSections = new float[PerfFrames * PerfSectionCount];
        readonly float[] _perfSectionsNow = new float[PerfSectionCount];
        readonly long[] _perfSectionAlloc = new long[PerfSectionCount];
        double _perfSectionMark;
        long _perfHeap;
        double _perfFixed;
        const int PerfFrames = 4096;
        readonly PerfFrame[] _perf = new PerfFrame[PerfFrames];
        int _perfAt, _perfCount, _perfGc;
        PerfFrame _perfNow;
        readonly Stopwatch _perfClock = Stopwatch.StartNew();
        double _perfMark, _perfGuiStart;

        static double PerfMs(Stopwatch clock) { return clock.Elapsed.TotalMilliseconds; }

        void PerfFrameStart()
        {
            int gc = GC.CollectionCount(0);
            _perfNow.gc = gc - _perfGc;
            _perfGc = gc;
            double now = PerfMs(_perfClock);
            _perfNow.dt = (float)(now - _perfMark);
            _perfNow.clock = _playbackPaced ? _clockFrame * 1000f : 0f;
            _perfNow.allocRest = PerfAlloc();
            _perfNow.ticked = Time.fixedTimeAsDouble != _perfFixed;
            _perfFixed = Time.fixedTimeAsDouble;
            _perf[_perfAt] = _perfNow;
            Array.Copy(_perfSectionsNow, 0, _perfSections, _perfAt * PerfSectionNames.Length, PerfSectionNames.Length);
            Array.Clear(_perfSectionsNow, 0, PerfSectionNames.Length);
            _perfAt = (_perfAt + 1) % PerfFrames;
            _perfCount = Math.Min(_perfCount + 1, PerfFrames);
            _perfNow = default(PerfFrame);
            _perfMark = now;
        }

        void PerfUpdateEnd()
        {
            _perfNow.update = (float)(PerfMs(_perfClock) - _perfMark) - _perfNow.wait;
            _perfNow.allocUpdate = PerfAlloc();
        }

        void PerfGuiStart() { _perfNow.allocRest += PerfAlloc(); _perfGuiStart = _perfSectionMark = PerfMs(_perfClock); }
        void PerfGuiEnd() { _perfNow.gui += (float)(PerfMs(_perfClock) - _perfGuiStart); _perfNow.allocGui += PerfAlloc(); }

        void PerfSection(int i)
        {
            double now = PerfMs(_perfClock);
            _perfSectionsNow[i] += (float)(now - _perfSectionMark);
            long grew = PerfAlloc();
            _perfSectionAlloc[i] += grew;
            _perfNow.allocGui += grew;
            _perfSectionMark = now;
        }

        long PerfAlloc()
        {
            long heap = GC.GetTotalMemory(false), grew = Math.Max(0, heap - _perfHeap);
            _perfHeap = heap;
            return grew;
        }

        object BuildPerfReport()
        {
            var frames = new List<PerfFrame>(_perfCount);
            for (int i = _perfCount; i > 0; i--) frames.Add(_perf[(_perfAt - i + PerfFrames) % PerfFrames]);
            if (frames.Count == 0) return new { frames = 0 };
            float seconds = frames.Sum(f => f.dt) / 1000f;
            Func<float, int> over = ms => frames.Count(f => f.dt > ms);
            Func<Func<PerfFrame, float>, object> stat = get =>
            {
                var sorted = frames.Select(get).OrderBy(v => v).ToList();
                return new { mean = Math.Round(sorted.Average(), 3), p50 = Math.Round(sorted[sorted.Count / 2], 3),
                    p99 = Math.Round(sorted[(int)(sorted.Count * 0.99)], 3), max = Math.Round(sorted[sorted.Count - 1], 3) };
            };
            return new
            {
                frames = frames.Count, seconds = Math.Round(seconds, 2), speed = Math.Round(frames.Sum(f => f.clock) / 1000f / seconds, 4), fps = Math.Round(frames.Count / seconds, 1),
                paced = _playbackPaced, targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount,
                screen = Screen.width + "x" + Screen.height, fullScreenMode = Screen.fullScreenMode.ToString(),
                refresh = Screen.currentResolution.refreshRateRatio.value, gcFrames = frames.Count(f => f.gc > 0),
                over4ms = over(4f), over5ms = over(5f), over10ms = over(10f), over20ms = over(20f),
                dt = stat(f => f.dt), wait = stat(f => f.wait), update = stat(f => f.update), pump = stat(f => f.pump), gui = stat(f => f.gui), skin = stat(f => f.skin),
                rest = stat(f => f.dt - f.wait - f.update - f.gui),
                allocUpdate = stat(f => f.allocUpdate), allocGui = stat(f => f.allocGui), allocRest = stat(f => f.allocRest),
                allocPerSecond = Math.Round(frames.Sum(f => (double)(f.allocUpdate + f.allocGui + f.allocRest)) / seconds),
                tickFrames = frames.Count(f => f.ticked),
                tickFrame = new { dt = stat2(frames.Where(f => f.ticked), f => f.dt), update = stat2(frames.Where(f => f.ticked), f => f.update),
                    gui = stat2(frames.Where(f => f.ticked), f => f.gui), rest = stat2(frames.Where(f => f.ticked), f => f.dt - f.wait - f.update - f.gui) },
                otherFrame = new { dt = stat2(frames.Where(f => !f.ticked), f => f.dt), update = stat2(frames.Where(f => !f.ticked), f => f.update),
                    gui = stat2(frames.Where(f => !f.ticked), f => f.gui), rest = stat2(frames.Where(f => !f.ticked), f => f.dt - f.wait - f.update - f.gui) },
                sections = PerfSectionNames.Select((name, i) => new { name, ms = PerfSectionStat(i), allocTotal = _perfSectionAlloc[i] }).ToList(),
                slowest = frames.OrderByDescending(f => f.dt).Take(20).Select(f => new
                {
                    dt = Math.Round(f.dt, 2), clock = Math.Round(f.clock, 2), wait = Math.Round(f.wait, 2), update = Math.Round(f.update, 2), pump = Math.Round(f.pump, 2),
                    gui = Math.Round(f.gui, 2), rest = Math.Round(f.dt - f.wait - f.update - f.gui, 2), f.gc, f.allocUpdate, f.allocGui, f.allocRest
                }).ToList()
            };
        }

        static object stat2(IEnumerable<PerfFrame> frames, Func<PerfFrame, float> get)
        {
            var sorted = frames.Select(get).OrderBy(v => v).ToList();
            if (sorted.Count == 0) return null;
            return new { n = sorted.Count, mean = Math.Round(sorted.Average(), 3), p50 = Math.Round(sorted[sorted.Count / 2], 3),
                p90 = Math.Round(sorted[(int)(sorted.Count * 0.9)], 3), max = Math.Round(sorted[sorted.Count - 1], 3) };
        }

        object PerfSectionStat(int section)
        {
            var sorted = Enumerable.Range(0, _perfCount).Select(k => _perfSections[(_perfAt - 1 - k + PerfFrames) % PerfFrames * PerfSectionNames.Length + section]).OrderBy(v => v).ToList();
            return new { mean = Math.Round(sorted.Average(), 3), p99 = Math.Round(sorted[(int)(sorted.Count * 0.99)], 3), max = Math.Round(sorted[sorted.Count - 1], 3) };
        }
    }
}
