using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace IGTAP.EngineSim.Cli
{
    internal sealed class Teleports
    {
        internal sealed class Pad
        {
            public int Id;
            public double X, Y, W, H;
            public int? Up, Down;
            public string Place => "tp:" + Id.ToString(CultureInfo.InvariantCulture);
            public string At => FormattableString.Invariant($"at:{X:0},{Y:0},{W / 2:0},{H / 2:0}");
            public bool Inside(double x, double y) => Math.Abs(x - X) <= W / 2 && Math.Abs(y - Y) <= H / 2;
        }

        // The teleporter poll needs the stick under 0.2 at rest since entering, then over 0.7 pushed.
        internal const int RestTicks = 2, PushTicks = 3;
        internal const int HopTicks = RestTicks + PushTicks;

        static readonly AccessTools.FieldRef<TeleporterScript, TeleporterScript> UpOf = AccessTools.FieldRefAccess<TeleporterScript, TeleporterScript>("upTeleporter");
        static readonly AccessTools.FieldRef<TeleporterScript, TeleporterScript> DownOf = AccessTools.FieldRefAccess<TeleporterScript, TeleporterScript>("downTeleporter");

        internal readonly Dictionary<int, Pad> Pads = new Dictionary<int, Pad>();

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, Teleports> Read =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, Teleports>();

        internal static Teleports For(Simulation sim) => Read.GetValue(sim, Of);

        static Teleports Of(Simulation sim)
        {
            var t = new Teleports();
            foreach (TeleporterScript script in sim.World.All<TeleporterScript>())
            {
                if (script == null || !(script.GetComponent<BoxCollider2D>() is BoxCollider2D box)) continue;
                Vector3 p = script.transform.position, scale = script.transform.lossyScale;
                WorldPos c = sim.ToWorld(new ScenePos(p.x + box.offset.x * scale.x, p.y + box.offset.y * scale.y));
                t.Pads[script.ID] = new Pad
                {
                    Id = script.ID, X = c.x, Y = c.y, W = Math.Abs(box.size.x * scale.x), H = Math.Abs(box.size.y * scale.y),
                    Up = UpOf(script) is TeleporterScript up && up != null ? up.ID : null,
                    Down = DownOf(script) is TeleporterScript down && down != null ? down.ID : null,
                };
            }
            return t;
        }

        internal List<(int from, int to, bool up)> Hops(Simulation sim)
        {
            var hops = new List<(int, int, bool)>();
            if (!globalStats.globalUpgradeDict.TryGetValue(globalStats.globalUpgradeSet.unlockTeleporters, out double on) || on == 0.0) return hops;
            var visited = new HashSet<int>(Singleton<globalStats>.Instance?.visitedTeleporters ?? new List<int>());
            foreach (Pad p in Pads.Values)
            {
                if (p.Up is int u && visited.Contains(u) && Pads.ContainsKey(u)) hops.Add((p.Id, u, true));
                if (p.Down is int d && visited.Contains(d) && Pads.ContainsKey(d)) hops.Add((p.Id, d, false));
            }
            return hops;
        }

        internal static List<InputTick> HopInputs(IEnumerable<bool> ups)
        {
            var inputs = new List<InputTick>();
            foreach (bool up in ups)
            {
                inputs.AddRange(Enumerable.Repeat(new InputTick(), RestTicks));
                inputs.AddRange(Enumerable.Repeat(new InputTick { y = up ? 1f : -1f }, PushTicks));
            }
            return inputs;
        }
    }
}
