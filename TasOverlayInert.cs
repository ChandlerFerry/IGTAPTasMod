using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        /// <summary>
        /// A route's inputs that change nothing in the game (the engine's <c>effects</c>, enginesim/Cli/Effects.cs): the overlay
        /// leaves them out while the route plays. Presses, dashes and releases by their number in the run (MacroEngine.JumpPresses,
        /// DashPresses, JumpReleases from the run's start), a dash's turn by its dash, the stick's x and y by input tick.
        /// </summary>
        sealed class InertInputs
        {
            public HashSet<int> Jumps, Dashes, Turns, Releases;
            public bool[] X, Y;

            public static bool At(bool[] ticks, long input) { return input >= 0 && input < ticks.Length && ticks[input]; }
        }

        readonly Dictionary<string, InertInputs> _inertLoaded = new Dictionary<string, InertInputs>();

        string InertPath(string key) { return Path.Combine(EngineSimDir, "effects-" + key + ".json"); }

        /// <summary>The route's inputs hashed: its effects file's name.</summary>
        static string InertKey(List<MacroStep> steps)
        {
            ulong h = 14695981039346656037UL;
            void Add(long v) { for (int b = 0; b < 8; b++) h = (h ^ (byte)(v >> (8 * b))) * 1099511628211UL; }
            void Float(float? v) { Add(v.HasValue ? BitConverter.ToInt32(BitConverter.GetBytes(v.Value), 0) : long.MinValue); }
            foreach (MacroStep s in steps)
            {
                Add((long)s.type); Add(s.durationTicks); Float(s.rawMoveX); Float(s.rawMoveY);
                Add((s.rawJumpHeld ? 1 : 0) | (s.rawJumpPressed ? 2 : 0) | (s.rawJumpReleased ? 4 : 0) | (s.rawDashPressed ? 8 : 0) | (s.rawQuickRestart ? 16 : 0));
                Add(s.dashJumpFrame); Add(s.springDashFrame); Float(s.lockMoveX); Float(s.lockMoveY); Float(s.dashTurnX); Float(s.dashTurnY);
                Add(s.pauseFrames); Add(s.pauseTick); Add(s.pauseFrame);
            }
            return h.ToString("x16");
        }

        /// <summary>The route's inert inputs, null when the engine hasn't found them for these inputs.</summary>
        InertInputs LoadInertInputs(MacroDefinition macro)
        {
            if (macro == null || macro.steps == null || macro.steps.Any(s => s.type != MacroStepType.RawInput || !s.enabled)) return null;
            string key = InertKey(macro.steps);
            if (_inertLoaded.TryGetValue(key, out InertInputs hit)) return hit;
            string path = InertPath(key);
            if (!File.Exists(path)) return null;
            try
            {
                JObject json = JObject.Parse(File.ReadAllText(path));
                HashSet<int> Set(string name) { return new HashSet<int>(json[name].ToObject<int[]>()); }
                bool[] Ticks(string name)
                {
                    var ticks = new bool[(int)json["inputs"]];
                    foreach (int[] range in json[name].ToObject<int[][]>())
                        for (int t = range[0]; t <= range[1] && t < ticks.Length; t++) ticks[t] = true;
                    return ticks;
                }
                var inert = new InertInputs { Jumps = Set("jump"), Dashes = Set("dash"), Turns = Set("turn"), Releases = Set("release"), X = Ticks("x"), Y = Ticks("y") };
                _inertLoaded[key] = inert;
                return inert;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is NullReferenceException || ex is ArgumentException)
            {
                Log.LogWarning("Inert inputs " + path + " unreadable: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The studio's "Find inert inputs": the engine's <c>effects</c> on a route, replayed from its start trace (a course's, a
        /// Full-TAS run's under its goals), to finish a route before recording it. Kept per route inputs while the trace stands.
        /// </summary>
        object EngineSimEffects(WebStudioRequest request, JObject body)
        {
            MacroDefinition route = _library.macros.Find(m => m.id == Text(body, "routeId"));
            if (route == null || !string.IsNullOrEmpty(route.savestateId) || !route.steps.All(s => s.type == MacroStepType.RawInput && s.enabled))
            {
                request.StatusCode = 404;
                return new { ok = false, error = "Inert inputs need a raw-input route with no savestate." };
            }
            string trace, input;
            if (FullTasRouteOf(route.name, out string runName, out _, out _))
            {
                FullTasRunDef run = _fullTasRuns.Find(r => string.Equals(r.name, runName, StringComparison.OrdinalIgnoreCase));
                trace = run == null ? null : FullTasTracePath(run.name);
                if (run == null || !FullTasTraceCurrent(run, trace, false))
                {
                    request.StatusCode = 404;
                    return new { ok = false, error = "Inert inputs need a current trace of " + runName + " (Capture start in the Full-TAS tab)." };
                }
                input = JsonConvert.SerializeObject(new { name = route.name, goals = FullTasLabels(run), ticks = ToTicks(route.steps) });
            }
            else
            {
                Match category = MatchRoutePrefix(route.name);
                CourseDef course = CourseOfRoute(route.name);
                trace = !category.Success || course == null ? null : CourseTrace(course, CapsOf(category.Groups["label"].Value).Id);
                if (trace == null)
                {
                    request.StatusCode = 404;
                    return new { ok = false, error = "Inert inputs need a course or Full-TAS route with a start trace (run Improve once)." };
                }
                input = JsonConvert.SerializeObject(new { name = route.name, ticks = ToTicks(route.steps) });
            }
            if (string.IsNullOrEmpty(EngineSimExe) || !File.Exists(EngineSimExe))
            {
                request.StatusCode = 409;
                return new { ok = false, error = "The offline engine isn't built." };
            }
            string key = InertKey(route.steps), output = InertPath(key), inputPath = Path.Combine(EngineSimDir, "effects-" + key + "-in.json");
            if (MapInFlight(output)) return RunEngineSimMap(request, null, output);
            if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(trace))
            {
                request.AsyncHandling = true;
                request.Response = File.ReadAllText(output);
                request.Completed.Set();
                return null;
            }
            _inertLoaded.Remove(key);
            File.Delete(output);
            File.WriteAllText(inputPath, input);
            return RunEngineSimMap(request, "effects " + Quote(trace) + " " + Quote(inputPath) + " --out " + Quote(output), output);
        }
    }
}
