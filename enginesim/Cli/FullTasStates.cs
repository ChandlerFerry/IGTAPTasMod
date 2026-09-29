using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IGTAP.EngineSim.Cli
{
    /// <summary>
    /// A Full-TAS run's goal-boundary states on disk (archive\fulltas-&lt;run&gt;\states): when the run first reaches goal k,
    /// or a better arrival does, its state is kept there, keyed by the hash of the run's inputs up to it. A solve or window
    /// starts from the stored state instead of replaying the run up to there, and an archive cell replays its prefix from
    /// the later of its worker's own state and these (docs/fulltas.md "Offline search"). They go with the archive: the
    /// run's start.key drops both together, and a route edit hashes differently, so a stale state is never picked up.
    /// </summary>
    sealed class FullTasStates
    {
        /// <summary>The snapshot format's version; a state file of another engine build is ignored.</summary>
        public readonly SnapshotIndex Index = new SnapshotIndex();
        readonly string _dir;
        public int Saved;

        public FullTasStates(string archiveDir, bool fresh)
        {
            _dir = Path.Combine(archiveDir, "states");
            if (fresh || !Directory.Exists(_dir)) return;
            foreach (string file in Directory.GetFiles(_dir, "*.bin"))
                try
                {
                    using FileStream stream = File.OpenRead(file);
                    using var reader = new BinaryReader(stream);
                    if (reader.ReadInt32() != SnapshotStore.Format) continue;
                    Index.Add(new SnapshotIndex.Entry
                    {
                        Goal = reader.ReadInt32(),
                        Tick = reader.ReadInt32(),
                        Hash = reader.ReadUInt64(),
                        Data = reader.ReadBytes((int)(stream.Length - stream.Position)),
                    });
                }
                catch (Exception) { }
        }

        /// <summary>Records the state at the run's input tick unless this arrival is stored already.</summary>
        public void Record(SnapshotStore store, ulong hash, int tick, int goal, SimSnapshot state)
        {
            if (Index.TryGet(hash, out _)) return;
            byte[] data;
            // A state with a part of the world this can't write just isn't kept: the run replays to it as before.
            try { data = store.Save(state); }
            catch (NotSupportedException) { return; }
            Index.Add(new SnapshotIndex.Entry { Hash = hash, Tick = tick, Goal = goal, Data = data });
            Directory.CreateDirectory(_dir);
            string file = Path.Combine(_dir, goal + "-" + hash.ToString("x16") + ".bin");
            // Solver workers can record the same state at once: each writes its own temp file, the last move wins.
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream stream = File.Create(temp))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(SnapshotStore.Format);
                writer.Write(goal);
                writer.Write(tick);
                writer.Write(hash);
                writer.Write(data);
            }
            Solve.Replace(temp, file);
            Saved++;
            // Per goal the newest arrivals win: the older ones were a route that no longer is. Another worker (or solve) can be
            // deleting or replacing the same file: Windows then refuses with access denied, and that file is gone or new anyway.
            foreach (string old in Directory.GetFiles(_dir, goal + "-*.bin").OrderByDescending(File.GetLastWriteTimeUtc).Skip(3))
                try { File.Delete(old); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>
        /// One replay of the inputs' first <paramref name="count"/> (as <see cref="Simulation.Advance"/> runs them),
        /// recording the state at every goal boundary it reaches and returning the one at the end. The states go in keyed
        /// by the run's own inputs up to them (the caller's list is the run's head).
        /// </summary>
        public SimSnapshot ReplayAndRecord(Simulation sim, List<InputTick> inputs, int count, SnapshotStore store)
        {
            if (count == 0) return sim.Start;
            var run = inputs.GetRange(0, count).Concat(new[] { inputs[count - 1] }).ToList();
            SimSnapshot at = null;
            ulong hash = SnapshotStore.HashSeed;
            int lastGoal = 0, folded = 0;
            sim.Run(run, null, tick =>
            {
                for (int i = folded; i < Math.Min(tick, count); i++) hash = SnapshotStore.Feed(hash, inputs[i]);
                folded = Math.Max(folded, Math.Min(tick, count));
                if (tick < count)
                {
                    if (sim.Progress > lastGoal)
                    {
                        lastGoal = sim.Progress;
                        Record(store, hash, tick, lastGoal - 1, sim.Capture(tick));
                    }
                    return true;
                }
                sim.Engine.RebaseInputs();
                at = sim.Capture(0);
                return false;
            }, 1000000);
            if (at != null) Record(store, hash, count, lastGoal, at);
            return at;
        }

        /// <summary>The stored state for the run's input hash, patched to sit at <paramref name="tick"/> of the list it resumes; null when there is none or this world can't restore it (the run replays to it as before).</summary>
        public SimSnapshot Load(SnapshotStore store, ulong hash, int tick)
        {
            if (!Index.TryGet(hash, out SnapshotIndex.Entry entry)) return null;
            try { return store.StateOf(entry, tick); }
            catch (NotSupportedException ex)
            {
                Console.WriteLine($"goal state (goal {entry.Goal}) can't be restored here: {ex.Message}; replaying to it");
                return null;
            }
        }
    }
}
