using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    [Serializable]
    public sealed class TimelineAction
    {
        public string id = Guid.NewGuid().ToString("N");
        public MacroStep step = new MacroStep();
        public int startTick;
        public int durationTicks = 1;
        public bool enabled = true;

        public int EndTick { get { return startTick + durationTicks; } }
    }

    [Serializable]
    public sealed class TimelineDocument
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public List<TimelineAction> actions = new List<TimelineAction>();

        public static TimelineDocument FromSequentialSteps(IList<MacroStep> steps, float fixedDeltaMs)
        {
            if (fixedDeltaMs <= 0f) throw new ArgumentOutOfRangeException(nameof(fixedDeltaMs));
            TimelineDocument document = new TimelineDocument();
            int tick = 0;
            if (steps == null) return document;
            foreach (MacroStep step in steps)
            {
                int duration = step.endCondition == ActionEndCondition.FixedTicks
                    ? Math.Max(1, step.durationTicks)
                    : Math.Max(1, (int)Math.Round(step.durationMs / fixedDeltaMs));
                document.actions.Add(new TimelineAction {
                    step = step.Copy(), startTick = tick, durationTicks = duration
                });
                tick += duration;
            }
            return document;
        }
    }
}
