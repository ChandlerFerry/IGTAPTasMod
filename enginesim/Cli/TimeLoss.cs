using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace IGTAP.EngineSim.Cli
{
    static class TimeLoss
    {
        const string Usage = "usage: timeloss split <trace.json> <route.json> --ref <route.json | captured trace.json> [--within U] [--json]"
            + " | timeloss search <trace.json> <route.json> [--segment k | --window a-b] [--depth 1|2|3] [--radius R] [--beam B] [--threads T]"
            + " | timeloss floor <trace.json> <route.json> [--json]"
            + " | timeloss report <trace.json> <route.json> [--ref <route.json | captured trace.json>] [--threads T] [--depth D] [--json]";

        internal static int Run(string[] args)
        {
            string[] rest = args.Skip(1).ToArray();
            switch (args.FirstOrDefault())
            {
                case "split": return TimeLossSplit.Run(rest);
                case "search": return TimeLossSearch.Run(rest);
                case "floor": return TimeLossFloor.Run(rest);
                case "report": return Report(rest);
                default: throw new ArgumentException(Usage);
            }
        }

        sealed class Joined
        {
            public TimeLossFloor.Row Floor;
            public TimeLossSplit.Row Split;
            public TimeLossSearch.Row Search;
        }

        static int Report(string[] args)
        {
            if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            string trace = args[0], route = args[1], reference = Solve.Option(args, "--ref", (string)null);
            int threads = Solve.Option(args, "--threads", Environment.ProcessorCount), depth = Solve.Option(args, "--depth", 2);
            TimeLossFloor.Result floor = TimeLossFloor.Measure(trace, route);
            Dictionary<int, TimeLossSplit.Row> split = reference == null ? new Dictionary<int, TimeLossSplit.Row>()
                : TimeLossSplit.Rows(trace, route, reference).ToDictionary(x => x.From);
            List<TimeLossSearch.Row> search = TimeLossSearch.Rows(trace, route, threads, depth);
            List<Joined> rows = floor.Rows.Select(f => new Joined
            {
                Floor = f, Split = split.GetValueOrDefault(f.From),
                Search = search.Where(x => x.From >= f.From && x.From < f.To).OrderByDescending(x => x.Gain).ThenByDescending(x => x.SegmentGain).FirstOrDefault(),
            }).ToList();
            string refName = reference == null ? null : Path.GetFileName(reference);
            Console.WriteLine(args.Contains("--json") ? JsonSerializer.Serialize(Json(floor, refName, rows)) : Table(floor, refName, rows));
            return 0;
        }

        static string Signed(long? v) => v == null ? "-" : v.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

        static string Table(TimeLossFloor.Result floor, string reference, List<Joined> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(TimeLossFloor.Header(floor));
            sb.AppendLine((reference == null ? "no reference (--ref)" : $"split against {reference}: ref = the segment's ticks less the reference's")
                + "; gain = ticks the best change in the segment saves at the finish (at next: at the next anchor); slack = ticks - floor;"
                + " timed: the course timer's share");
            sb.AppendLine(" seg  inputs     anchor -> next      ticks  timed    ref  timed ref   gain  at next  floor  slack  timed slack  best change");
            foreach (Joined j in rows)
            {
                TimeLossFloor.Row f = j.Floor;
                sb.AppendLine($" {f.Index,3}  {f.From,4}-{f.To,-4}  {f.Kind,-7} -> {f.Next,-7}  {f.Ticks,5}  {f.Timed,5}  {Signed(j.Split?.Delta),5}  {Signed(j.Split?.TimedDelta),9}"
                    + $"  {(j.Search == null ? "-" : j.Search.Gain.ToString(CultureInfo.InvariantCulture)),5}  {(j.Search == null ? "-" : j.Search.SegmentGain.ToString(CultureInfo.InvariantCulture)),7}"
                    + $"  {f.Floor,5}  {f.Slack,5}  {f.TimedSlack,11}  {(j.Search == null || j.Search.Gain <= 0 ? "" : j.Search.Change)}");
            }
            Joined lost = rows.Where(j => j.Split?.TimedDelta > 0).OrderByDescending(j => j.Split.TimedDelta).FirstOrDefault();
            Joined gain = rows.Where(j => j.Search?.Gain > 0).OrderByDescending(j => j.Search.Gain).FirstOrDefault();
            Joined slack = TimeLossFloor.Ranked(floor).Select(f => rows[f.Index]).FirstOrDefault();
            if (reference != null)
                sb.AppendLine($"split: timed {Signed(rows.Sum(j => j.Split?.TimedDelta ?? 0))} ticks against the reference; "
                    + (lost == null ? "no segment loses timed ticks" : $"most lost in segment {lost.Floor.Index} ({Signed(lost.Split.TimedDelta)})"));
            sb.AppendLine("search: " + (gain == null ? "no change gains at the finish" : $"best gain {gain.Search.Gain} in segment {gain.Floor.Index}: {gain.Search.Change}"));
            if (slack != null)
                sb.AppendLine($"floor: most slack in segment {slack.Floor.Index} (" + (slack.Floor.Timed > 0
                    ? $"{slack.Floor.TimedSlack} of its {slack.Floor.Timed} timed ticks)" : $"{slack.Floor.Slack} of {slack.Floor.Ticks} ticks, untimed)"));
            return sb.ToString().TrimEnd();
        }

        static object Json(TimeLossFloor.Result floor, string reference, List<Joined> rows) => new
        {
            route = floor.Route, reference, finished = floor.Finished, time = floor.Time, ticks = floor.Ticks,
            speeds = floor.Speeds.ToDictionary(v => v.Key, v => Math.Round(v.Value.speed, 2)),
            segments = rows.Select(j => new
            {
                floor = TimeLossFloor.Json(j.Floor),
                split = j.Split == null ? null : new { refTicks = j.Split.RefTicks, delta = j.Split.Delta, timedDelta = j.Split.TimedDelta },
                search = j.Search == null ? null : new { gain = j.Search.Gain, change = j.Search.Change, segmentGain = j.Search.SegmentGain, segmentChange = j.Search.SegmentChange },
            }),
        };
    }
}
