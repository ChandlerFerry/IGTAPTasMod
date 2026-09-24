using System;
using System.Collections.Generic;

namespace IGTAPTasMod
{
    [Flags]
    public enum TimelineSnapMode
    {
        None = 0,
        FixedTick = 1,
        NeighborEdges = 2,
        StateEvents = 4
    }

    public enum TimelineEndCondition
    {
        FixedTicks,
        GroundContact,
        WallContact,
        WallCoyote,
        JumpApex,
        Falling,
        VerticalVelocity,
        HorizontalVelocity,
        PlayerX,
        PlayerY,
        PositionRegion,
        DashReady,
        JumpReady,
        CourseStart,
        CourseFinish
    }

    public enum TimelineTimeoutPolicy
    {
        AbortAttempt,
        Continue,
        RetryAction,
        RecoveryBundle
    }

    /// <summary>Editable, serializable timeline metadata around a runtime MacroStep.</summary>
    [Serializable]
    public sealed class TimelineAction
    {
        public string id = Guid.NewGuid().ToString("N");
        public MacroStep step = new MacroStep();
        public int startTick;
        public int durationTicks = 1;
        public bool enabled = true;
        public TimelineEndCondition endCondition = TimelineEndCondition.FixedTicks;
        public int timeoutTicks = 60;
        public TimelineTimeoutPolicy timeoutPolicy = TimelineTimeoutPolicy.AbortAttempt;
        public float threshold;
        public float regionMinX;
        public float regionMinY;
        public float regionMaxX;
        public float regionMaxY;
        public string recoveryBundle = "";

        public int EndTick { get { return startTick + durationTicks; } }

        public TimelineAction Copy()
        {
            return new TimelineAction {
                id = id, step = step == null ? new MacroStep() : step.Copy(), startTick = startTick,
                durationTicks = durationTicks, enabled = enabled, endCondition = endCondition,
                timeoutTicks = timeoutTicks, timeoutPolicy = timeoutPolicy, threshold = threshold,
                regionMinX = regionMinX, regionMinY = regionMinY, regionMaxX = regionMaxX,
                regionMaxY = regionMaxY, recoveryBundle = recoveryBundle
            };
        }
    }

    [Serializable]
    public sealed class TimelineDocument
    {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion = CurrentSchemaVersion;
        public List<TimelineAction> actions = new List<TimelineAction>();

        public TimelineDocument Copy()
        {
            TimelineDocument copy = new TimelineDocument();
            foreach (TimelineAction action in actions)
                copy.actions.Add(action.Copy());
            return copy;
        }

        public TimelineAction Find(string id)
        {
            return actions.Find(a => a.id == id);
        }

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
                    step = step.Copy(), startTick = tick, durationTicks = duration,
                    timeoutTicks = Math.Max(1, (int)Math.Round(step.timeoutMs / fixedDeltaMs))
                });
                tick += duration;
            }
            return document;
        }

        /// <summary>
        /// Flattens the visual timeline for the current sequential scheduler. Disabled actions are
        /// omitted and positive gaps become neutral Wait steps. Overlaps execute in visual order.
        /// </summary>
        public List<MacroStep> ToSequentialSteps(float fixedDeltaMs)
        {
            if (fixedDeltaMs <= 0f) throw new ArgumentOutOfRangeException(nameof(fixedDeltaMs));
            List<TimelineAction> ordered = new List<TimelineAction>(actions);
            ordered.Sort(CompareActions);
            List<MacroStep> result = new List<MacroStep>();
            int cursor = 0;
            foreach (TimelineAction action in ordered)
            {
                if (!action.enabled) continue;
                if (action.startTick > cursor)
                {
                    int gapTicks = action.startTick - cursor;
                    result.Add(new MacroStep {
                        label = "Timeline gap", type = MacroStepType.Wait, direction = 0,
                        durationTicks = gapTicks, durationMs = TicksToMilliseconds(gapTicks, fixedDeltaMs),
                        endCondition = ActionEndCondition.FixedTicks
                    });
                }
                MacroStep step = action.step.Copy();
                step.durationTicks = action.durationTicks;
                step.durationMs = TicksToMilliseconds(action.durationTicks, fixedDeltaMs);
                step.endCondition = ActionEndCondition.FixedTicks;
                step.timeoutTicks = action.timeoutTicks;
                step.timeoutMs = TicksToMilliseconds(action.timeoutTicks, fixedDeltaMs);
                result.Add(step);
                cursor = Math.Max(cursor, action.EndTick);
            }
            return result;
        }

        private static int TicksToMilliseconds(int ticks, float fixedDeltaMs)
        {
            return Math.Max(0, (int)Math.Round(ticks * fixedDeltaMs));
        }

        internal static int CompareActions(TimelineAction left, TimelineAction right)
        {
            int start = left.startTick.CompareTo(right.startTick);
            return start != 0 ? start : string.CompareOrdinal(left.id, right.id);
        }
    }

    public interface ITimelineEditCommand
    {
        string Name { get; }
        void Undo(TimelineEditorState editor);
        void Redo(TimelineEditorState editor);
    }

    internal sealed class TimelineSnapshotCommand : ITimelineEditCommand
    {
        private readonly TimelineDocument _before;
        private readonly TimelineDocument _after;
        public string Name { get; private set; }

        public TimelineSnapshotCommand(string name, TimelineDocument before, TimelineDocument after)
        {
            Name = name;
            _before = before;
            _after = after;
        }

        public void Undo(TimelineEditorState editor) { editor.Restore(_before); }
        public void Redo(TimelineEditorState editor) { editor.Restore(_after); }
    }

    /// <summary>
    /// UI-neutral timeline controller. IMGUI may call BeginInteraction/Preview*/CommitInteraction
    /// during a drag so an entire gesture creates one undo entry.
    /// </summary>
    public sealed class TimelineEditorState
    {
        private const int MaxHistory = 200;
        private readonly Stack<ITimelineEditCommand> _undo = new Stack<ITimelineEditCommand>();
        private readonly Stack<ITimelineEditCommand> _redo = new Stack<ITimelineEditCommand>();
        private TimelineDocument _interactionBefore;
        private string _interactionName;

        public TimelineDocument Document { get; private set; }
        public string SelectedActionId { get; set; }
        public int PlayheadTick { get; private set; }
        public int CurrentTick { get; private set; }
        public float PixelsPerTick { get; private set; } = 8f;
        public float PanTick { get; private set; }
        public TimelineSnapMode SnapMode { get; set; } = TimelineSnapMode.FixedTick | TimelineSnapMode.NeighborEdges | TimelineSnapMode.StateEvents;
        public int SnapIntervalTicks { get; set; } = 1;
        public int SnapToleranceTicks { get; set; } = 2;
        public List<int> StateEventTicks { get; } = new List<int>();
        public bool CanUndo { get { return _undo.Count != 0; } }
        public bool CanRedo { get { return _redo.Count != 0; } }
        public string UndoName { get { return CanUndo ? _undo.Peek().Name : ""; } }
        public string RedoName { get { return CanRedo ? _redo.Peek().Name : ""; } }
        public bool IsInteracting { get { return _interactionBefore != null; } }
        public event Action Changed;

        public TimelineEditorState(TimelineDocument document)
        {
            Document = document == null ? new TimelineDocument() : document.Copy();
            Normalize();
        }

        public void Move(string id, int proposedStartTick)
        {
            Edit("Move action", delegate { MoveCore(id, proposedStartTick); });
        }

        public void Resize(string id, int proposedStartTick, int proposedEndTick)
        {
            Edit("Resize action", delegate { ResizeCore(id, proposedStartTick, proposedEndTick); });
        }

        public void Reorder(string id, int targetIndex)
        {
            Edit("Reorder action", delegate {
                TimelineAction action = Require(id);
                Document.actions.Remove(action);
                targetIndex = Math.Max(0, Math.Min(targetIndex, Document.actions.Count));
                Document.actions.Insert(targetIndex, action);
                int tick = 0;
                foreach (TimelineAction item in Document.actions)
                {
                    item.startTick = tick;
                    tick += item.durationTicks;
                }
            });
        }

        public string Duplicate(string id)
        {
            string duplicateId = Guid.NewGuid().ToString("N");
            Edit("Duplicate action", delegate {
                TimelineAction source = Require(id);
                TimelineAction copy = source.Copy();
                copy.id = duplicateId;
                copy.step.label += " copy";
                copy.startTick = Snap(source.EndTick, id);
                Document.actions.Add(copy);
                SelectedActionId = copy.id;
            });
            return duplicateId;
        }

        public string Split(string id, int proposedTick)
        {
            if (Require(id).durationTicks < 2)
                throw new InvalidOperationException("An action must be at least two ticks long to split.");
            string rightId = Guid.NewGuid().ToString("N");
            Edit("Split action", delegate {
                TimelineAction left = Require(id);
                int splitTick = Snap(proposedTick, id);
                splitTick = Math.Max(left.startTick + 1, Math.Min(splitTick, left.EndTick - 1));
                TimelineAction right = left.Copy();
                right.id = rightId;
                right.step.label += " (part 2)";
                right.startTick = splitTick;
                right.durationTicks = left.EndTick - splitTick;
                left.durationTicks = splitTick - left.startTick;
                Document.actions.Add(right);
                SelectedActionId = right.id;
            });
            return rightId;
        }

        public string Merge(string firstId, string secondId)
        {
            TimelineAction first = Require(firstId);
            TimelineAction second = Require(secondId);
            if (!CanMerge(first, second)) throw new InvalidOperationException("Only compatible actions can be merged.");
            string keptId = first.startTick <= second.startTick ? first.id : second.id;
            Edit("Merge actions", delegate {
                TimelineAction a = Require(firstId);
                TimelineAction b = Require(secondId);
                TimelineAction kept = a.id == keptId ? a : b;
                int start = Math.Min(a.startTick, b.startTick);
                int end = Math.Max(a.EndTick, b.EndTick);
                kept.startTick = start;
                kept.durationTicks = Math.Max(1, end - start);
                kept.step.label = a.step.label;
                Document.actions.Remove(kept == a ? b : a);
                SelectedActionId = kept.id;
            });
            return keptId;
        }

        public void SetEnabled(string id, bool enabled)
        {
            Edit(enabled ? "Enable action" : "Disable action", delegate { Require(id).enabled = enabled; });
        }

        public void Delete(string id)
        {
            Edit("Delete action", delegate {
                Document.actions.Remove(Require(id));
                if (SelectedActionId == id) SelectedActionId = null;
            });
        }

        public void EditInspector(string id, Action<TimelineAction> edit)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            Edit("Edit action condition", delegate { edit(Require(id)); });
        }

        public void BeginInteraction(string name)
        {
            if (IsInteracting) throw new InvalidOperationException("A timeline interaction is already active.");
            _interactionBefore = Document.Copy();
            _interactionName = string.IsNullOrEmpty(name) ? "Edit timeline" : name;
        }

        public void PreviewMove(string id, int proposedStartTick)
        {
            RequireInteraction();
            MoveCore(id, proposedStartTick);
            Normalize();
            RaiseChanged();
        }

        public void PreviewResize(string id, int proposedStartTick, int proposedEndTick)
        {
            RequireInteraction();
            ResizeCore(id, proposedStartTick, proposedEndTick);
            Normalize();
            RaiseChanged();
        }

        public void CommitInteraction()
        {
            RequireInteraction();
            TimelineDocument before = _interactionBefore;
            string name = _interactionName;
            _interactionBefore = null;
            _interactionName = null;
            Push(new TimelineSnapshotCommand(name, before, Document.Copy()));
        }

        public void CancelInteraction()
        {
            RequireInteraction();
            TimelineDocument before = _interactionBefore;
            _interactionBefore = null;
            _interactionName = null;
            Restore(before);
        }

        public void Undo()
        {
            if (!CanUndo || IsInteracting) return;
            ITimelineEditCommand command = _undo.Pop();
            command.Undo(this);
            _redo.Push(command);
        }

        public void Redo()
        {
            if (!CanRedo || IsInteracting) return;
            ITimelineEditCommand command = _redo.Pop();
            command.Redo(this);
            _undo.Push(command);
        }

        public int Snap(int proposedTick, string movingActionId = null)
        {
            int result = Math.Max(0, proposedTick);
            if ((SnapMode & TimelineSnapMode.FixedTick) != 0)
            {
                int interval = Math.Max(1, SnapIntervalTicks);
                result = (int)Math.Round(result / (double)interval) * interval;
            }
            int best = result;
            int distance = Math.Max(0, SnapToleranceTicks) + 1;
            if ((SnapMode & TimelineSnapMode.NeighborEdges) != 0)
            {
                foreach (TimelineAction action in Document.actions)
                {
                    if (action.id == movingActionId) continue;
                    ConsiderSnap(action.startTick, result, ref best, ref distance);
                    ConsiderSnap(action.EndTick, result, ref best, ref distance);
                }
            }
            if ((SnapMode & TimelineSnapMode.StateEvents) != 0)
                foreach (int tick in StateEventTicks) ConsiderSnap(tick, result, ref best, ref distance);
            return Math.Max(0, best);
        }

        public void SetZoom(float pixelsPerTick, float anchorPixel)
        {
            float oldTick = TickAtPixel(anchorPixel);
            PixelsPerTick = Math.Max(0.25f, Math.Min(64f, pixelsPerTick));
            PanTick = Math.Max(0f, oldTick - anchorPixel / PixelsPerTick);
            RaiseChanged();
        }

        public void ZoomBy(float factor, float anchorPixel)
        {
            if (factor <= 0f) return;
            SetZoom(PixelsPerTick * factor, anchorPixel);
        }

        public void PanByPixels(float pixels)
        {
            PanTick = Math.Max(0f, PanTick - pixels / PixelsPerTick);
            RaiseChanged();
        }

        public float PixelAtTick(float tick) { return (tick - PanTick) * PixelsPerTick; }
        public float TickAtPixel(float pixel) { return PanTick + pixel / PixelsPerTick; }

        public void SetPlayhead(int tick)
        {
            PlayheadTick = Math.Max(0, tick);
            RaiseChanged();
        }

        public void UpdateCurrentTick(int tick)
        {
            CurrentTick = Math.Max(0, tick);
            RaiseChanged();
        }

        public TimelineAction ActionAtTick(int tick)
        {
            foreach (TimelineAction action in Document.actions)
                if (action.enabled && tick >= action.startTick && tick < action.EndTick) return action;
            return null;
        }

        public static bool CanMerge(TimelineAction first, TimelineAction second)
        {
            if (first == null || second == null || first.step == null || second.step == null) return false;
            return first.enabled == second.enabled && first.endCondition == second.endCondition
                && first.step.type == second.step.type && first.step.direction == second.step.direction
                && string.Equals(first.step.bundleName, second.step.bundleName, StringComparison.Ordinal);
        }

        internal void Restore(TimelineDocument snapshot)
        {
            Document = snapshot.Copy();
            Normalize();
            if (SelectedActionId != null && Document.Find(SelectedActionId) == null) SelectedActionId = null;
            RaiseChanged();
        }

        private void Edit(string name, Action mutation)
        {
            if (IsInteracting) throw new InvalidOperationException("Finish the active timeline interaction first.");
            TimelineDocument before = Document.Copy();
            mutation();
            Normalize();
            Push(new TimelineSnapshotCommand(name, before, Document.Copy()));
            RaiseChanged();
        }

        private void Push(ITimelineEditCommand command)
        {
            _undo.Push(command);
            _redo.Clear();
            if (_undo.Count <= MaxHistory) return;
            ITimelineEditCommand[] commands = _undo.ToArray();
            _undo.Clear();
            for (int i = Math.Min(MaxHistory - 1, commands.Length - 1); i >= 0; i--) _undo.Push(commands[i]);
        }

        private void MoveCore(string id, int proposedStartTick)
        {
            Require(id).startTick = Snap(proposedStartTick, id);
        }

        private void ResizeCore(string id, int proposedStartTick, int proposedEndTick)
        {
            TimelineAction action = Require(id);
            int start = Snap(proposedStartTick, id);
            int end = Snap(proposedEndTick, id);
            if (end <= start) end = start + 1;
            action.startTick = start;
            action.durationTicks = end - start;
        }

        private TimelineAction Require(string id)
        {
            TimelineAction action = Document.Find(id);
            if (action == null) throw new ArgumentException("Timeline action was not found: " + id, nameof(id));
            return action;
        }

        private void RequireInteraction()
        {
            if (!IsInteracting) throw new InvalidOperationException("BeginInteraction must be called first.");
        }

        private void Normalize()
        {
            if (Document.actions == null) Document.actions = new List<TimelineAction>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TimelineAction action in Document.actions)
            {
                if (action.step == null) action.step = new MacroStep();
                if (string.IsNullOrEmpty(action.id) || !ids.Add(action.id))
                {
                    action.id = Guid.NewGuid().ToString("N");
                    ids.Add(action.id);
                }
                action.startTick = Math.Max(0, action.startTick);
                action.durationTicks = Math.Max(1, action.durationTicks);
                action.timeoutTicks = Math.Max(1, action.timeoutTicks);
            }
            Document.actions.Sort(TimelineDocument.CompareActions);
        }

        private static void ConsiderSnap(int candidate, int proposed, ref int best, ref int distance)
        {
            int candidateDistance = Math.Abs(candidate - proposed);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                best = candidate;
            }
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }
    }
}
