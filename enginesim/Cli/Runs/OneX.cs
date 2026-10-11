using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using IGTAPTasMod;

namespace IGTAP.EngineSim.Cli.Runs
{
    // A 1x log (the plugin's probe of a run, docs/fulltas.md "1x confirmation") against the engine's rows of the same inputs, by
    // physics tick. The probe's tick row is taken inside its tick, after Movement.FixedUpdate: its input and Velocity are the
    // engine's row of that tick; its body position, held frames and Cash before the tick's NP payout (econPre) are what the tick
    // before left, the engine's row of the tick before (taken after that tick's rendered frames, a dash freeze's held ones
    // included). Both write a row every physics tick; one either side lacks isn't compared. Two differences are where the probe
    // reads, not the engine: Movement.OnCollisionStay2D zeroes Velocity.y on a floor or ceiling after FixedUpdate, after the
    // probe's row (engine vy 0); and a step-up in a rendered frame moves the transform, which the body follows only at the next
    // physics step, after the probe's row (a body off on one tick only). FalseEnding%-280's 1x reference has both on every
    // landing; neither parts.
    static class OneX
    {
        internal struct Tick
        {
            public long Physics, Input, Held;
            public double X, Y, Vx, Vy, Cash;
        }

        internal sealed class Result
        {
            public int Compared;
            public long EngineLast, GameLast;
            public long Physics = -1, Input;
            public string Field;
            public double Engine, Game;
            public bool Parted => Field != null;
        }

        public static Result Align(IReadOnlyList<Tick> engine, IReadOnlyList<Tick> game, double pos = 0.01, double vel = 0.01, double cashRel = 1e-9)
        {
            var at = new Dictionary<long, Tick>();
            foreach (Tick g in game) at[g.Physics] = g;
            var r = new Result { EngineLast = engine.Count == 0 ? 0 : engine[^1].Physics, GameLast = game.Count == 0 ? 0 : game[^1].Physics };
            var engineAt = new Dictionary<long, Tick>();
            foreach (Tick e in engine) engineAt[e.Physics] = e;
            bool Body(Tick e, Tick g) => Math.Abs(e.X - g.X) <= pos && Math.Abs(e.Y - g.Y) <= pos;
            foreach (Tick e in engine)
            {
                if (!at.TryGetValue(e.Physics, out Tick now) || !at.TryGetValue(e.Physics + 1, out Tick next)) continue;
                bool body = Body(e, next) || engineAt.TryGetValue(e.Physics + 1, out Tick e1) && at.TryGetValue(e.Physics + 2, out Tick next1) && Body(e1, next1);
                var fields = new (string name, double engine, double game, bool same)[]
                {
                    ("input", e.Input, now.Input, e.Input == now.Input),
                    ("held", e.Held, next.Held, next.Held < 0 || e.Held == next.Held),
                    ("x", e.X, next.X, body || Math.Abs(e.X - next.X) <= pos), ("y", e.Y, next.Y, body || Math.Abs(e.Y - next.Y) <= pos),
                    ("vx", e.Vx, now.Vx, Math.Abs(e.Vx - now.Vx) <= vel), ("vy", e.Vy, now.Vy, e.Vy == 0 || Math.Abs(e.Vy - now.Vy) <= vel),
                    ("cash", e.Cash, next.Cash, e.Cash == next.Cash || Math.Abs(e.Cash - next.Cash) <= cashRel * Math.Max(Math.Abs(e.Cash), Math.Abs(next.Cash))),
                };
                foreach (var f in fields)
                {
                    if (f.same) continue;
                    (r.Physics, r.Input, r.Field, r.Engine, r.Game) = (e.Physics, e.Input, f.name, f.engine, f.game);
                    return r;
                }
                r.Compared++;
            }
            return r;
        }

        /// <summary>The probe log's tick rows: x, y the body (bodyX, bodyY), Cash its cashPre or econPre's, held -1 where it has none.</summary>
        public static List<Tick> GameRows(JsonElement log)
        {
            var rows = new List<Tick>();
            foreach (JsonElement r in log.GetProperty("rows").EnumerateArray())
            {
                if (r.GetProperty("kind").GetString() != "tick") continue;
                double cash = r.TryGetProperty("cashPre", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble()
                    : r.TryGetProperty("econPre", out JsonElement pre) && pre.ValueKind == JsonValueKind.Object ? pre.GetProperty("cash").GetDouble()
                    : r.GetProperty("cash").GetDouble();
                rows.Add(new Tick
                {
                    Physics = r.GetProperty("physics").GetInt64(), Input = r.GetProperty("input").GetInt64(),
                    Held = r.TryGetProperty("held", out JsonElement h) ? h.GetInt64() : -1,
                    X = r.GetProperty("bodyX").GetDouble(), Y = r.GetProperty("bodyY").GetDouble(),
                    Vx = r.GetProperty("vx").GetDouble(), Vy = r.GetProperty("vy").GetDouble(), Cash = cash,
                });
            }
            return rows;
        }

        /// <summary>`fulltas --check --ticks-out` (or `replay --ticks-out`) lines: the tick rows, not the clone and draw rows.</summary>
        public static List<Tick> EngineRows(string file)
        {
            var rows = new List<Tick>();
            foreach (string line in File.ReadLines(file))
            {
                if (line.Length == 0) continue;
                using JsonDocument d = JsonDocument.Parse(line);
                JsonElement r = d.RootElement;
                if (r.TryGetProperty("kind", out _)) continue;
                rows.Add(new Tick
                {
                    Physics = r.GetProperty("physics").GetInt64(), Input = r.GetProperty("input").GetInt64(), Held = r.GetProperty("held").GetInt64(),
                    X = r.GetProperty("x").GetDouble(), Y = r.GetProperty("y").GetDouble(), Vx = r.GetProperty("vx").GetDouble(), Vy = r.GetProperty("vy").GetDouble(),
                    Cash = r.TryGetProperty("econ", out JsonElement econ) ? econ.GetProperty("cash").GetDouble() : r.GetProperty("cash").GetDouble(),
                });
            }
            return rows;
        }

        /// <summary>The engine's rows of a 1x log's inputs, replayed from the run's trace. Loose buys: the run's goals may have
        /// changed since the log, and a stray buy that ended it at 1x ends the rows compared there anyway.</summary>
        public static List<Tick> Replay(string trace, List<InputTick> inputs)
        {
            Simulation sim = Solve.Creator(trace, Solve.ReadHeader(trace))();
            var rows = new List<Tick>();
            sim.Run(inputs, null, null, 1000000, _ =>
            {
                WorldPos p = sim.PlayerWorld;
                rows.Add(new Tick
                {
                    Physics = sim.Engine.PhysicsTicks, Input = sim.Engine.RunTicks, Held = sim.Engine.HeldFrames, X = p.x, Y = p.y,
                    Vx = sim.Player.Velocity.x, Vy = sim.Player.Velocity.y, Cash = sim.Shop.Holds(globalStats.Currencies.Cash),
                });
            });
            return rows;
        }

        public static string Line(Result r) => JsonSerializer.Serialize(new
        {
            compared = r.Compared, engineLast = r.EngineLast, gameLast = r.GameLast,
            parted = r.Parted ? new { physics = r.Physics, input = r.Input, field = r.Field, engine = r.Engine, game = r.Game } : null,
        });

        public static string Describe(Result r) => r.Parted
            ? $"parts from 1x at physics tick {r.Physics} (input {r.Input}): {r.Field} engine {r.Engine.ToString("R", CultureInfo.InvariantCulture)}, 1x {r.Game.ToString("R", CultureInfo.InvariantCulture)} ({r.Compared} ticks matched before)"
            : $"matches 1x on the {r.Compared} ticks both have (engine to tick {r.EngineLast}, 1x to {r.GameLast})";

        /// <summary>align-1x &lt;probe log&gt; (&lt;ticks-out lines&gt; | --trace T): the first tick and field where the engine parts from 1x.</summary>
        public static int Command(string[] args)
        {
            string trace = Solve.Option<string>(args, "--trace", null);
            string[] files = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] != "--trace")).ToArray();
            if (files.Length < 1 || trace == null && files.Length < 2) { Console.Error.WriteLine("align-1x <probe.json> (<ticks-out.jsonl> | --trace <trace.json>)"); return 2; }
            using JsonDocument log = JsonDocument.Parse(File.ReadAllText(files[0]));
            List<Tick> engine = trace != null ? Replay(trace, Solve.ReadInputs(log.RootElement)) : EngineRows(files[1]);
            Result r = Align(engine, GameRows(log.RootElement));
            Console.WriteLine(Describe(r));
            Console.WriteLine(Line(r));
            return r.Parted ? 1 : 0;
        }
    }
}
