using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IGTAPTasMod
{
    internal static class TasMarkup
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const int MaxTicks = 100000;

        public static string Format(string name, List<MacroStep> steps, List<RouteMarker> markers)
        {
            var text = new StringBuilder();
            text.Append("# IGTAP TAS: ").Append(name).Append('\n');
            text.Append("# <input ticks>,<inputs>: R L (R0.55: part tilt) U D stick, J jump held (presses where it starts), H held\n");
            text.Append("# without that press, P press, C release, X dash (X+3 jump on its 3rd frame, X(0.35:1) freeze stick),\n");
            text.Append("# S3 dash on a spring's 3rd tick, K(x:y) stick through a lock, Z12@0.3 pause, Q quick restart. @ marks a tick.\n");
            var marks = (markers ?? new List<RouteMarker>()).OrderBy(m => m.tick).ToList();
            int tick = 0, next = 0;
            bool held = false;
            foreach (MacroStep step in steps)
            {
                int length = Math.Max(1, step.durationTicks);
                int done = 0;
                while (done < length)
                {
                    while (next < marks.Count && marks[next].tick < tick + done) next++;
                    while (next < marks.Count && marks[next].tick == tick + done) text.Append("@ ").Append(marks[next++].note).Append('\n');
                    int part = length - done;
                    if (next < marks.Count && marks[next].tick < tick + length) part = marks[next].tick - tick - done;
                    text.Append(Line(step, part, done == 0, held)).Append('\n');
                    held = step.rawJumpHeld;
                    done += part;
                }
                tick += length;
            }
            while (next < marks.Count) text.Append("@ ").Append(marks[next++].note).Append('\n');
            return text.ToString();
        }

        static string Line(MacroStep s, int ticks, bool first, bool wasHeld)
        {
            var parts = new List<string>();
            float x = s.rawMoveX;
            if (x != 0f) parts.Add((x > 0f ? "R" : "L") + (Math.Abs(x) == 1f ? "" : Num(Math.Abs(x))));
            if (s.rawMoveY != 0f) parts.Add(s.rawMoveY > 0f ? "U" : "D");
            bool press = first && s.rawJumpPressed;
            if (s.rawJumpHeld)
            {
                bool rising = !wasHeld;
                parts.Add(rising && !press ? "H" : "J");
                if (press && !rising) parts.Add("P");
            }
            else if (press) parts.Add("P");
            if (first && s.rawJumpReleased) parts.Add("C");
            if (first && s.rawDashPressed)
            {
                string dash = "X";
                if (s.dashJumpFrame > 0) dash += "+" + s.dashJumpFrame;
                if (s.dashTurnX.HasValue || s.dashTurnY.HasValue) dash += "(" + Num(s.dashTurnX) + ":" + Num(s.dashTurnY) + ")";
                parts.Add(dash);
            }
            if (first && s.springDashFrame > 0) parts.Add("S" + s.springDashFrame);
            if (first && (s.lockMoveX.HasValue || s.lockMoveY.HasValue)) parts.Add("K(" + Num(s.lockMoveX) + ":" + Num(s.lockMoveY) + ")");
            if (first && s.pauseFrames > 0)
                parts.Add("Z" + s.pauseFrames + (s.pauseTick != 0 || s.pauseFrame != 0 ? "@" + s.pauseTick + "." + s.pauseFrame : ""));
            if (first && s.rawQuickRestart) parts.Add("Q");
            return ticks.ToString(Inv).PadLeft(4) + (parts.Count > 0 ? "," + string.Join(",", parts) : "");
        }

        static string Num(float? v) { return v.HasValue ? v.Value.ToString("0.###", Inv) : ""; }

        static readonly Regex Stick = new Regex(@"^([RL])([0-9.]*)$"), Dash = new Regex(@"^X(?:\+(\d+))?(?:\(([-0-9.]*):([-0-9.]*)\))?$"),
            Lock = new Regex(@"^K\(([-0-9.]*):([-0-9.]*)\)$"), Pause = new Regex(@"^Z(\d+)(?:@(\d+)\.(\d+))?$"), Spring = new Regex(@"^S(\d+)$");

        public static List<MacroStep> Parse(string text, out List<RouteMarker> markers)
        {
            var steps = new List<MacroStep>();
            markers = new List<RouteMarker>();
            int tick = 0, number = 0;
            bool held = false;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                number++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("@", StringComparison.Ordinal)) { markers.Add(new RouteMarker { tick = tick, note = line.Substring(1).Trim() }); continue; }
                string[] fields = line.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
                if (fields.Length == 0 || !int.TryParse(fields[0], NumberStyles.None, Inv, out int ticks) || ticks < 1)
                    throw new FormatException("Line " + number + ": starts with '" + (fields.Length > 0 ? fields[0] : line) + "', not a tick count.");
                if (ticks > MaxTicks - tick) throw new FormatException("Line " + number + ": past " + MaxTicks + " input ticks in all.");
                var step = new MacroStep { type = MacroStepType.RawInput, label = "TAS markup", durationTicks = ticks };
                bool hold = false, noPress = false, press = false;
                foreach (string field in fields.Skip(1))
                {
                    string token = field.ToUpperInvariant();
                    Match m;
                    if ((m = Stick.Match(token)).Success)
                        step.rawMoveX = (m.Groups[1].Value == "R" ? 1f : -1f) * (m.Groups[2].Value.Length > 0 ? Float(m.Groups[2].Value, number) : 1f);
                    else if (token == "U") step.rawMoveY = 1f;
                    else if (token == "D") step.rawMoveY = -1f;
                    else if (token == "J") hold = true;
                    else if (token == "H") { hold = true; noPress = true; }
                    else if (token == "P") press = true;
                    else if (token == "C") step.rawJumpReleased = true;
                    else if (token == "Q") step.rawQuickRestart = true;
                    else if ((m = Dash.Match(token)).Success)
                    {
                        step.rawDashPressed = true;
                        if (m.Groups[1].Success) step.dashJumpFrame = int.Parse(m.Groups[1].Value, Inv);
                        if (m.Groups[2].Success) { step.dashTurnX = Opt(m.Groups[2].Value, number); step.dashTurnY = Opt(m.Groups[3].Value, number); }
                    }
                    else if ((m = Spring.Match(token)).Success) step.springDashFrame = int.Parse(m.Groups[1].Value, Inv);
                    else if ((m = Lock.Match(token)).Success) { step.lockMoveX = Opt(m.Groups[1].Value, number); step.lockMoveY = Opt(m.Groups[2].Value, number); }
                    else if ((m = Pause.Match(token)).Success)
                    {
                        step.pauseFrames = int.Parse(m.Groups[1].Value, Inv);
                        if (m.Groups[2].Success) { step.pauseTick = int.Parse(m.Groups[2].Value, Inv); step.pauseFrame = int.Parse(m.Groups[3].Value, Inv); }
                    }
                    else throw new FormatException("Line " + number + ": unknown input '" + field + "'.");
                }
                step.rawJumpHeld = hold;
                step.rawJumpPressed = press || hold && !held && !noPress;
                step.direction = step.rawMoveX > 0f ? 1 : step.rawMoveX < 0f ? -1 : 0;
                steps.Add(step);
                held = hold;
                tick += ticks;
            }
            return steps;
        }

        static float Float(string value, int line)
        {
            if (float.TryParse(value, NumberStyles.Float, Inv, out float v)) return v;
            throw new FormatException("Line " + line + ": '" + value + "' is not a number.");
        }

        static float? Opt(string value, int line) { return value.Length == 0 ? (float?)null : Float(value, line); }
    }
}
