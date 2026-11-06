using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace IGTAPTasMod
{
    public sealed partial class TasPlugin
    {
        sealed class FullTasQueue
        {
            public FullTasRunDef run;
            public List<string> files;
            public int index;
            public FullTasPlay play;
            public float nextAt;
            public readonly List<FullTasQueueResult> results = new List<FullTasQueueResult>();
        }

        sealed class FullTasQueueResult
        {
            public string file;
            public bool finished;
            public float? time;
            public int reached;
            public string reason;
        }

        FullTasQueue _fullTasQueue;
        const float FullTasQueueSettleSeconds = 2f;

        object StartFullTasQueue(JObject body)
        {
            FullTasRunDef run = FindFullTasRun(Text(body, "name"));
            if (!(body["files"] is JArray list) || list.Count == 0) throw new InvalidOperationException("files: the candidate files to play, from fulltas/candidates/" + run.name + "/.");
            List<string> files = list.Select(f => (string)f).ToList();
            string dir = FullTasCandidatesDir(run.name);
            foreach (string file in files)
                if (string.IsNullOrEmpty(file) || file != Path.GetFileName(file) || !File.Exists(Path.Combine(dir, file)))
                    throw new InvalidOperationException("No candidate " + file + " in " + dir + ".");
            if (_fullTasQueue != null && _fullTasQueue.index < _fullTasQueue.files.Count) throw new InvalidOperationException("A queue is playing: stop it first.");
            var queue = new FullTasQueue { run = run, files = files };
            PlayFullTasCandidate(queue);
            _fullTasQueue = queue;
            FullTasLog("Playing " + files.Count + " " + run.name + " candidates back to back at 1x");
            return new { ok = true, of = files.Count };
        }

        void PlayFullTasCandidate(FullTasQueue queue)
        {
            string file = queue.files[queue.index];
            JObject o = JObject.Parse(File.ReadAllText(Path.Combine(FullTasCandidatesDir(queue.run.name), file)));
            string[] labels = ResolveFullTasGoals(queue.run).Select(g => g.label).ToArray();
            if (!(o["goals"] is JArray goals) || !goals.Select(g => (string)g).SequenceEqual(labels))
                throw new InvalidOperationException("Candidate " + file + " is of other goals than " + queue.run.name + "'s.");
            var route = new MacroDefinition { name = "fulltas/" + queue.run.name + " candidate " + Path.GetFileNameWithoutExtension(file),
                steps = ToSteps(o["inputs"].ToObject<List<SolverTick>>()) };
            StartFullTasPlayback(queue.run, route, labels.Length);
            queue.play = _fullTasPlay;
        }

        void FullTasQueuePlayed(FullTasPlay play, bool finished, float time, int reached, string reason)
        {
            FullTasQueue queue = _fullTasQueue;
            if (queue == null || queue.play != play) return;
            Played(queue, finished, finished ? time : (float?)null, reached, finished ? null : reason);
        }

        void Played(FullTasQueue queue, bool finished, float? time, int reached, string reason)
        {
            queue.results.Add(new FullTasQueueResult { file = queue.files[queue.index], finished = finished, time = time, reached = reached, reason = reason });
            queue.play = null;
            queue.index++;
            queue.nextAt = Time.unscaledTime + FullTasQueueSettleSeconds;
            if (queue.index < queue.files.Count) return;
            SetFullTasResult("Played " + queue.results.Count + " " + queue.run.name + " candidates back to back at 1x: " + queue.results.Count(r => r.finished) + " finished");
        }

        void PumpFullTasQueue()
        {
            FullTasQueue queue = _fullTasQueue;
            if (queue == null || queue.index >= queue.files.Count) return;
            if (queue.play != null)
            {
                if (_fullTasPlay != queue.play) Played(queue, false, null, 0, "did not start");
                return;
            }
            if (Time.unscaledTime < queue.nextAt || PlaybackActive || _recorder.IsRecording || _fullTasPending != null || _newGamePhase != 0 || _solverRunning) return;
            try { PlayFullTasCandidate(queue); }
            catch (Exception ex) { Played(queue, false, null, 0, ex.Message); }
        }

        void StopFullTasQueue()
        {
            FullTasQueue queue = _fullTasQueue;
            if (queue == null || queue.index >= queue.files.Count) return;
            int keep = queue.index + (queue.play != null ? 1 : 0);
            queue.files.RemoveRange(keep, queue.files.Count - keep);
            FullTasLog("Stopped the " + queue.run.name + " candidate queue after " + keep + " of its candidates");
        }

        object FullTasQueueWebState()
        {
            FullTasQueue queue = _fullTasQueue;
            return queue == null ? null : new { name = queue.run.name, index = queue.index, of = queue.files.Count,
                current = queue.play == null ? null : queue.files[queue.index], results = queue.results };
        }
    }
}
