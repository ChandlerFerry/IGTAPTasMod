using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    sealed class RunContext
    {
        public string Trace, Name, Dir;
        public JsonElement Header;
        public Func<Simulation> Make;
        public FullTasGoal[] HeaderGoals;
        public string[] HeaderIds;
        public Dictionary<string, FullTasGoal> Known;
        public bool Loose;
        public RunClass Class;
        // A course's own timing (its game timer, a farm loop's steady lap), lower is better; null: live time.
        public Func<List<InputTick>, long> Score;

        public static RunContext Open(string trace, RunClass cls = RunClass.Major)
        {
            trace = Path.GetFullPath(trace);
            JsonElement header = Solve.ReadHeader(trace);
            if (!header.TryGetProperty("fulltas", out JsonElement fulltas)) throw new InvalidOperationException("Not a Full-TAS trace (no \"fulltas\" header): " + trace);
            JsonElement[] entries = fulltas.GetProperty("goals").EnumerateArray().ToArray();
            string[] ids = entries.Select(g => g.TryGetProperty("id", out JsonElement id) ? id.GetString() : null).ToArray();
            string invalid = FullTasGoalIds.Invalid(ids, ids.Length);
            if (invalid != null) throw new InvalidOperationException("The trace's goals have no valid ids (" + invalid + "): capture it again");
            Func<Simulation> make = Solve.Creator(trace, header);
            bool human = Glitches.Human(cls);
            Func<Simulation> strict = () => { Simulation sim = make(); sim.StrictBuys = true; sim.Engine.HumanClass = human; return sim; };
            // The goals as the engine resolves them (a reward goal's box found near its rect): read on a thread of its own, since
            // a Simulation owns its thread's engine hooks.
            (FullTasGoal[] goals, bool loose) = Solve.OnThread(() => { Simulation sim = strict(); return (sim.Goals.Goals, sim.Goals.Loose); });
            var known = new Dictionary<string, FullTasGoal>();
            foreach (FullTasGoal g in goals) known.TryAdd(g.label, g);
            return new RunContext
            {
                Trace = trace, Name = fulltas.GetProperty("name").GetString(), Dir = Path.GetDirectoryName(trace), Header = header,
                Make = strict, HeaderGoals = goals, HeaderIds = ids, Known = known, Loose = loose, Class = cls,
            };
        }

        public FullTasGoal[] GoalsOf(RunPlan plan) => plan.Labels.Select(l => RunGoals.Goal(l, Known)).ToArray();

        // A class's own files beside the run's (fulltas/<run>.<class>.json), as the plugin keeps class bests.
        public string Key => Class == RunClass.Major ? Name : Name + "." + Glitches.Key(Class);

        public string RunFile => Path.Combine(Dir, "fulltas", Key + ".json");
    }
}
