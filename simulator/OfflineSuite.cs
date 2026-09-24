using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

static class OfflineSuite
{
    sealed record Shape(string Name, bool Trigger, float CenterX, float CenterY,
        float SizeX, float SizeY, float[] Points, int[] PathStarts);
    sealed record Options(string Model, string Routes, string Route, string Output, int Radius,
        long MaxEvaluations, int Beam, int Top, string? Recording, string? Result, bool AllowScreening);
    sealed class ValidationData
    {
        public int samples { get; init; }
        public double rmsePixels { get; init; }
        public double maxErrorPixels { get; init; }
        public double groundAgreement { get; init; }
        public string fidelity { get; init; } = "unmeasured";
        [JsonIgnore] public List<(float x, float y, float vx, float vy, bool ground)> Truth { get; init; } = [];
    }
    sealed record Candidate(int[] Durations, float[] RawMoveX, bool[] NoJump, int HopStep, int HopTick, int HopHold, SimResult Result,
        double Clearance, List<(float x, float y, float vx, float vy, bool ground)>? Trace = null)
    {
        public double HazardClearance { get; init; } = double.NaN;
        // Furthest baseline-trace index reached before dying; lets a route-shape
        // change that does not finish yet still steer its timing repair.
        public int Progress { get; init; }
        string? signature;
        public string Signature => signature ??= Sign(Durations, RawMoveX, NoJump, HopStep, HopTick, HopHold);
        public static string Sign(int[] durations, float[] rawMoveX, bool[] noJump, int hopStep, int hopTick, int hopHold)
            => string.Join(',', durations) + "|x="
            + string.Join(',', rawMoveX.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture)))
            + "|nj=" + string.Concat(noJump.Select(n => n ? '1' : '0'))
            + $"|{hopStep}|{hopTick}|{hopHold}";
        public int ChangeCost(int[] baseline, float[] baselineX) => Durations.Select((d, i) => Math.Abs(d - baseline[i])).Sum()
            + RawMoveX.Select((x, i) => (int)Math.Round(Math.Abs(x - baselineX[i]) * 20)).Sum()
            + NoJump.Count(n => n) * 4
            + (HopStep >= 0 ? 2 + HopHold : 0);
        public string ShapeChange(float[] baselineX, bool includeHop = true)
        {
            var parts = new List<string>();
            int[] dropped = Enumerable.Range(0, NoJump.Length).Where(i => NoJump[i]).ToArray();
            if (dropped.Length > 0) parts.Add("no jump at step " + string.Join('/', dropped.Select(i => i + 1)));
            int[] turned = Enumerable.Range(0, RawMoveX.Length).Where(i => MathF.Abs(RawMoveX[i] - baselineX[i]) > .5f).ToArray();
            if (turned.Length > 0) parts.Add("redirect step " + string.Join('/', turned.Select(i =>
                (i + 1) + "→" + RawMoveX[i].ToString("0.##", CultureInfo.InvariantCulture))));
            if (includeHop && HopStep >= 0) parts.Add($"hop step {HopStep + 1}, tick {HopTick}, hold {HopHold}");
            return parts.Count == 0 ? "—" : string.Join("; ", parts);
        }
    }

    public static int Run(string[] args)
    {
        try
        {
            Options options = Parse(args);
            Directory.CreateDirectory(options.Output);
            var environment = Loader.Environment(options.Model);
            List<Shape> geometry = LoadGeometry(options.Model);
            Gate[] hazardBounds = LoadHazardBounds(geometry, environment.viewport);
            float initialX = environment.initialX, initialY = environment.initialY;
            // An export can capture the controller between restart and the first
            // replay physics sample. Use the measured tick-zero pose for a
            // result-backed search so timing edits share the native start state.
            if (!string.IsNullOrWhiteSpace(options.Result) && File.Exists(options.Result))
            {
                using JsonDocument resultDocument = JsonDocument.Parse(File.ReadAllText(options.Result));
                string trace = resultDocument.RootElement.GetProperty("earlyTrace").GetString() ?? "";
                var measured = ParseAttemptTrace(trace);
                if (measured.Count > 0) (initialX, initialY) = (measured[0].x, measured[0].y);
            }
            else if (!string.IsNullOrWhiteSpace(options.Recording) && File.Exists(options.Recording))
            {
                Loader.Recording(options.Recording, out var recordedTrace);
                if (recordedTrace.Count > 0) (initialX, initialY) = (recordedTrace[0].x, recordedTrace[0].y);
            }
            var simulator = new FastSimulator(environment.surfaces, environment.ceilings, environment.walls,
                environment.start, environment.finish, environment.physics, initialX, initialY);
            List<Loader.RouteStep> steps = Loader.Route(options.Routes, options.Route);
            int[] baseline = steps.Select(s => s.Duration).ToArray();
            float[] baselineX = steps.Select(s => s.X).ToArray();
            var validation = Validate(options.Recording, options.Result, simulator, steps);
            if (validation != null && validation.fidelity == "screening-only" && !options.AllowScreening)
                throw new InvalidDataException($"Offline model validation failed for {environment.course}: "
                    + $"RMSE {validation.rmsePixels:0.###} px, max error {validation.maxErrorPixels:0.###} px. "
                    + "Search aborted before candidate evaluation.");

            int[] variables = Enumerable.Range(0, steps.Count)
                .Where(i => steps[i].Duration > 1).ToArray();
            int[][] values = variables.Select(i => Enumerable.Range(
                Math.Max(1, baseline[i] - options.Radius), baseline[i] + options.Radius - Math.Max(1, baseline[i] - options.Radius) + 1)
                .ToArray()).ToArray();
            long cartesian = Product(values.Select(v => (long)v.Length), long.MaxValue);
            bool exhaustive = cartesian <= options.MaxEvaluations;
            var evaluated = new ConcurrentDictionary<string, Candidate>();
            long simulations = 0;
            Candidate? budgetFallback = null;
            bool[] baselineNoJump = new bool[steps.Count];

            // The simulator does not kill on spikes, so every candidate is gated here.
            var reference = new List<(float x, float y, float vx, float vy, bool ground)>();
            simulator.Run(BuildFrames(steps, baseline, -1, -1, 0, baselineX), reference);
            double baselineHazard = reference.Count == 0 ? double.PositiveInfinity : reference.Min(p => PointHazardClearance(p, hazardBounds));
            // ponytail: fixed 2px margin (or the seed's own, if tighter); make it a flag if native spike deaths show up.
            double hazardFloor = Math.Min(2.0, baselineHazard);

            Candidate Evaluate(int[] durations, int hopStep = -1, int hopTick = -1, int hopHold = 0, float[]? rawMoveX = null, bool[]? noJump = null)
            {
                rawMoveX ??= baselineX;
                noJump ??= baselineNoJump;
                string signature = Candidate.Sign(durations, rawMoveX, noJump, hopStep, hopTick, hopHold);
                if (evaluated.TryGetValue(signature, out Candidate? existing)) return existing;
                if (Volatile.Read(ref simulations) >= options.MaxEvaluations && budgetFallback != null)
                    return budgetFallback;
                return evaluated.GetOrAdd(signature, _ =>
                {
                    Interlocked.Increment(ref simulations);
                    var trace = new List<(float x, float y, float vx, float vy, bool ground)>();
                    SimResult result = simulator.Run(BuildFrames(steps, durations, hopStep, hopTick, hopHold, rawMoveX, noJump), trace);
                    int alive = trace.Count;
                    double hazard = double.PositiveInfinity;
                    for (int i = 0; i < trace.Count; i++)
                    {
                        double clearance = PointHazardClearance(trace[i], hazardBounds);
                        if (clearance < hazard) hazard = clearance;
                        if (clearance < hazardFloor) { alive = i; break; }
                    }
                    if (alive < trace.Count) result = result with { Finished = false };
                    return new Candidate((int[])durations.Clone(), (float[])rawMoveX.Clone(), (bool[])noJump.Clone(),
                        hopStep, hopTick, hopHold, result, double.NaN)
                    { HazardClearance = hazard, Progress = result.Finished ? reference.Count : Progress(trace, alive, reference) };
                });
            }

            Candidate baselineCandidate = Evaluate(baseline);
            budgetFallback = baselineCandidate;
            int requiredLandingMask = baselineCandidate.Result.LandingMask;
            int requiredWallJumps = baselineCandidate.Result.WallJumpCount;
            // Full Rank over millions of candidates is the hot spot; pre-cut with cheap
            // integer keys to a generous pool, then apply the complete ordering.
            List<Candidate> Leaders(int count) => Rank(evaluated.Values
                    .OrderBy(c => c.Result.Finished ? 0 : 1)
                    .ThenBy(c => c.Result.Finished ? c.Result.CourseTicks : 0)
                    .ThenByDescending(c => c.Progress)
                    .Take(count * 4 + 64), baseline, baselineX, requiredLandingMask, requiredWallJumps)
                .Take(count).ToList();
            if (exhaustive)
            {
                var combinations = EnumerateCartesian(baseline, variables, values).ToArray();
                Parallel.ForEach(combinations, durations => Evaluate(durations));
            }
            else
            {
                // Complete one-factor suite: every legal value of every timing block.
                Parallel.ForEach(variables.Select((step, p) => (step, p)), pair =>
                {
                    foreach (int value in values[pair.p])
                    {
                        if (Volatile.Read(ref simulations) >= options.MaxEvaluations) return;
                        int[] durations = (int[])baseline.Clone();
                        durations[pair.step] = value;
                        Evaluate(durations);
                    }
                });

                // Alternate route-shape moves (drop a jump, or send a block another way,
                // then re-time the blocks right after it) with timing polish, restarting
                // from each new best so shape changes stack. Stops when a pass finds
                // nothing faster or the budget runs out.
                long shapeBudget = 0;
                Candidate RepairShape(Candidate seed, float[] xs, bool[] noJump, int changed)
                {
                    int[] window = Enumerable.Range(Math.Max(0, changed - 1), 7).Where(i => i < steps.Count).ToArray();
                    List<Candidate> local = [Evaluate(seed.Durations, seed.HopStep, seed.HopTick, seed.HopHold, xs, noJump)];
                    for (int round = 0; round < 3 && Volatile.Read(ref simulations) < shapeBudget; round++)
                    {
                        var next = new List<Candidate>(local);
                        foreach (Candidate parent in local)
                            foreach (int step in window)
                                for (int value = Math.Max(1, parent.Durations[step] - options.Radius); value <= parent.Durations[step] + options.Radius; value++)
                                {
                                    if (value == parent.Durations[step]) continue;
                                    int[] durations = (int[])parent.Durations.Clone();
                                    durations[step] = value;
                                    next.Add(Evaluate(durations, parent.HopStep, parent.HopTick, parent.HopHold, xs, noJump));
                                }
                        local = Rank(next.DistinctBy(c => c.Signature), baseline, baselineX, requiredLandingMask, requiredWallJumps).Take(4).ToList();
                        if (local[0].Result.Finished) break;
                    }
                    return local[0];
                }
                int BestTicks() => evaluated.Values.Where(c => c.Result.Finished).Select(c => c.Result.CourseTicks).DefaultIfEmpty(int.MaxValue).Min();
                // ponytail: beam capped at 64; a 1000-wide beam spent the whole budget on one round of near-duplicates.
                int beamWidth = Math.Min(options.Beam, 64);
                int previousBest = BestTicks();
                for (int pass = 1; simulations < options.MaxEvaluations; pass++)
                {
                    shapeBudget = Math.Min(options.MaxEvaluations, simulations + (options.MaxEvaluations - simulations) / 3);
                    var shapeMoves = new List<(Candidate seed, float[] xs, bool[] noJump, int step)>();
                    foreach (Candidate seed in Leaders(3).Where(c => c.Result.Finished).ToArray())
                        for (int step = 0; step < steps.Count; step++)
                        {
                            if (steps[step].Press && !seed.NoJump[step])
                                shapeMoves.Add((seed, seed.RawMoveX, DropJump(steps, seed.NoJump, step), step));
                            if (seed.Durations[step] < 2) continue;
                            foreach (float x in new[] { -1f, 0f, 1f })
                                if (MathF.Abs(x - seed.RawMoveX[step]) > .5f)
                                {
                                    float[] xs = (float[])seed.RawMoveX.Clone();
                                    xs[step] = x;
                                    shapeMoves.Add((seed, xs, seed.NoJump, step));
                                }
                        }
                    Parallel.ForEach(shapeMoves, move =>
                    {
                        if (Volatile.Read(ref simulations) >= shapeBudget) return;
                        RepairShape(move.seed, move.xs, move.noJump, move.step);
                    });

                    // Deterministic beam search explores interacting timing changes without random blind spots.
                    // Values are relative to each seed so shape-changed seeds re-time around their own timing.
                    List<Candidate> beam = Leaders(beamWidth);
                    for (int round = 0; round < variables.Length && simulations < options.MaxEvaluations; round++)
                    {
                        Parallel.ForEach(beam, seed =>
                        {
                            foreach (int step in variables)
                                for (int value = Math.Max(1, seed.Durations[step] - options.Radius); value <= seed.Durations[step] + options.Radius; value++)
                                {
                                    if (Volatile.Read(ref simulations) >= options.MaxEvaluations) return;
                                    int[] durations = (int[])seed.Durations.Clone();
                                    durations[step] = value;
                                    Evaluate(durations, seed.HopStep, seed.HopTick, seed.HopHold, seed.RawMoveX, seed.NoJump);
                                }
                        });
                        List<Candidate> next = Leaders(beamWidth);
                        if (next.Select(c => c.Signature).SequenceEqual(beam.Select(c => c.Signature))) break;
                        beam = next;
                    }
                    int best = BestTicks();
                    Console.WriteLine($"pass {pass}: shape moves={shapeMoves.Count} best={best} simulations={simulations:N0}");
                    if (best >= previousBest) break;
                    previousBest = best;
                }
            }

            // Search controller magnitude in the offline engine. Preserve each
            // block's intended direction, but include fine values around an
            // existing analog input and useful partial values for digital blocks.
            int[] analogSteps = Enumerable.Range(0, steps.Count).Where(i => MathF.Abs(baselineX[i]) > .001f).ToArray();
            foreach (int analogStep in analogSteps)
            {
                if (simulations >= options.MaxEvaluations) break;
                float sign = MathF.Sign(baselineX[analogStep]);
                float baseMagnitude = MathF.Abs(baselineX[analogStep]);
                float minimumMagnitude = baseMagnitude < .999f ? MathF.Max(.2f, baseMagnitude - .5f) : .5f;
                float increment = baseMagnitude < .999f ? .01f : .05f;
                for (float magnitude = minimumMagnitude; magnitude <= 1.0001f; magnitude += increment)
                {
                    if (simulations >= options.MaxEvaluations) break;
                    float[] xs = (float[])baselineX.Clone();
                    xs[analogStep] = MathF.Round(sign * magnitude, 2);
                    Evaluate(baseline, -1, -1, 0, xs);
                }
            }

            // Coordinate beam rounds allow analog and duration improvements to
            // compose without an in-game Cartesian search.
            List<Candidate> analogBeam = Leaders(options.Beam);
            for (int round = 0; round < 2 && simulations < options.MaxEvaluations; round++)
            {
                foreach (Candidate seed in analogBeam.Take(Math.Min(100, analogBeam.Count)).ToArray())
                {
                    if (simulations >= options.MaxEvaluations) break;
                    foreach (int analogStep in analogSteps)
                    {
                        if (simulations >= options.MaxEvaluations) break;
                        foreach (float delta in new[] { -.05f, -.02f, -.01f, .01f, .02f, .05f })
                        {
                            if (simulations >= options.MaxEvaluations) break;
                            float sign = MathF.Sign(baselineX[analogStep]);
                            float[] xs = (float[])seed.RawMoveX.Clone();
                            xs[analogStep] = Math.Clamp(MathF.Round(xs[analogStep] + sign * delta, 2), -1f, 1f);
                            if (MathF.Sign(xs[analogStep]) != sign || MathF.Abs(xs[analogStep]) < .2f) continue;
                            Evaluate(seed.Durations, seed.HopStep, seed.HopTick, seed.HopHold, xs, seed.NoJump);
                        }
                    }
                }
                analogBeam = Leaders(options.Beam);
            }

            // Test every jump insertion/release pair in every long plain-input block on strong timing candidates.
            Candidate[] timingSeeds = Leaders(Math.Min(100, options.Beam)).ToArray();
            foreach (Candidate seed in timingSeeds)
            {
                if (simulations >= options.MaxEvaluations) break;
                for (int step = 0; step < steps.Count; step++)
                {
                    if (simulations >= options.MaxEvaluations) break;
                    Loader.RouteStep source = steps[step];
                    if (seed.Durations[step] < 4 || !seed.NoJump[step] && (source.Press || source.Held || source.Release)) continue;
                    for (int tick = 0; tick < seed.Durations[step]; tick++)
                    {
                        if (simulations >= options.MaxEvaluations) break;
                        for (int hold = 0; hold <= Math.Min(12, seed.Durations[step] - tick); hold++)
                        {
                            Evaluate(seed.Durations, step, tick, hold, seed.RawMoveX, seed.NoJump);
                            if (simulations >= options.MaxEvaluations) break;
                        }
                    }
                }
            }

            Candidate[] ranked = Rank(evaluated.Values, baseline, baselineX, requiredLandingMask, requiredWallJumps).ToArray();
            var shortlist = new List<Candidate>();
            // Keep the fastest candidate of each distinct route change, but reserve room for
            // low-edit anchors. Interpolation phase makes edge-of-platform routes fragile, and
            // tiny edits are the best offline proxy until Unity performs the authoritative trials.
            // A route whose outcome flips when the start moves a pixel is exploiting simulator
            // error, not the game. Keep only leaders that survive every shifted start.
            bool Robust(Candidate candidate)
            {
                InputFrame[] frames = BuildFrames(steps, candidate.Durations, candidate.HopStep, candidate.HopTick,
                    candidate.HopHold, candidate.RawMoveX, candidate.NoJump);
                return new[] { -1f, -.5f, .5f, 1f }.All(dx =>
                {
                    var shifted = new FastSimulator(environment.surfaces, environment.ceilings, environment.walls,
                        environment.start, environment.finish, environment.physics, initialX + dx, initialY);
                    var trace = new List<(float x, float y, float vx, float vy, bool ground)>();
                    SimResult result = shifted.Run(frames, trace);
                    return result.Finished && Math.Abs(result.CourseTicks - candidate.Result.CourseTicks) <= 1
                        && trace.All(point => PointHazardClearance(point, hazardBounds) >= hazardFloor);
                });
            }
            int fragile = 0;
            foreach (Candidate candidate in ranked.Where(c => c.Result.Finished)
                // Hop variants of one route shape are one idea; the shortlist keeps the best of each.
                .DistinctBy(c => c.ShapeChange(baselineX, includeHop: false))
                .Where(c => Robust(c) || ++fragile < 0)
                .Take(Math.Max(Math.Min(3, options.Top), options.Top / 2)))
                if (!shortlist.Any(item => item.Signature == candidate.Signature)) shortlist.Add(candidate);
            if (options.Top > 1 && !shortlist.Any(item => item.Signature == baselineCandidate.Signature))
                shortlist.Add(baselineCandidate);
            foreach (Candidate candidate in ranked.OrderBy(item => item.ChangeCost(baseline, baselineX))
                .ThenBy(item => item.Result.Finished ? item.Result.CourseTicks : int.MaxValue))
            {
                if (!shortlist.Any(item => item.Signature == candidate.Signature)) shortlist.Add(candidate);
                if (shortlist.Count >= options.Top) break;
            }
            Candidate[] top = shortlist.Take(options.Top).Select(candidate =>
            {
                var trace = new List<(float x, float y, float vx, float vy, bool ground)>();
                SimResult result = simulator.Run(BuildFrames(steps, candidate.Durations, candidate.HopStep, candidate.HopTick, candidate.HopHold, candidate.RawMoveX, candidate.NoJump), trace);
                return candidate with { Result = result, Clearance = Clearance(trace, environment.surfaces),
                    HazardClearance = HazardClearance(trace, hazardBounds), Trace = trace };
            }).ToArray();

            WriteArtifacts(options, steps, baseline, baselineX, baselineCandidate, top, environment, geometry, validation,
                exhaustive, cartesian, simulations, evaluated.Count);
            Console.WriteLine($"fragile leaders rejected={fragile}; seed robust={Robust(baselineCandidate)}");
            Console.WriteLine($"mode={(exhaustive ? "exhaustive" : "bounded-deterministic")} theoretical={cartesian:N0} simulations={simulations:N0} unique={evaluated.Count:N0}");
            Console.WriteLine($"baseline={(baselineCandidate.Result.Finished ? baselineCandidate.Result.CourseTicks : -1)} ticks; best={(top.FirstOrDefault()?.Result.CourseTicks ?? -1)} ticks");
            Console.WriteLine("report=" + Path.GetFullPath(Path.Combine(options.Output, "report.html")));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    static Options Parse(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException(
            "Usage: IGTAP.FastSim suite <model.json> <routes.json> <route-name> [--out dir] [--radius 8] [--max 2000000] [--beam 1000] [--top 20] [--recording file] [--result file] [--allow-screening]");
        string output = Path.Combine(Environment.CurrentDirectory, "offline-report");
        int radius = 8, beam = 1000, top = 20; long max = 2_000_000; string? recording = null, result = null;
        bool allowScreening = false;
        for (int i = 3; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
            switch (args[i])
            {
                case "--out": output = Value(); break;
                case "--radius": radius = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--max": max = long.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--beam": beam = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--top": top = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--recording": recording = Value(); break;
                case "--result": result = Value(); break;
                case "--allow-screening": allowScreening = true; break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (radius < 0 || beam < 1 || top < 1 || max < 1) throw new ArgumentException("Search limits must be positive.");
        return new Options(args[0], args[1], args[2], output, radius, max, beam, top, recording, result, allowScreening);
    }

    static ValidationData? Validate(string? recording, string? resultPath, FastSimulator simulator, List<Loader.RouteStep> steps)
    {
        List<InputFrame> inputs;
        List<(float x, float y, float vx, float vy, bool ground)> truth;
        int nativeFinish = -1;
        if (!string.IsNullOrWhiteSpace(resultPath) && File.Exists(resultPath))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultPath));
            if (document.RootElement.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True)
                nativeFinish = document.RootElement.GetProperty("completionTicks").GetInt32();
            string trace = document.RootElement.TryGetProperty("earlyTrace", out JsonElement traceElement)
                ? traceElement.GetString() ?? "" : "";
            truth = ParseAttemptTrace(trace);
            int[] durations = steps.Select(step => step.Duration).ToArray();
            inputs = BuildFrames(steps, durations, -1, -1, 0).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(recording) && File.Exists(recording))
            inputs = Loader.Recording(recording, out truth);
        else return null;
        var predicted = new List<(float x, float y, float vx, float vy, bool ground)>();
        SimResult replay = simulator.Run(inputs, predicted, continueAfterFinish: true);
        int count = Math.Min(truth.Count, predicted.Count); double squared = 0, max = 0; int ground = 0;
        for (int i = 0; i < count; i++)
        {
            double dx = truth[i].x - predicted[i].x, dy = truth[i].y - predicted[i].y;
            double error = Math.Sqrt(dx * dx + dy * dy); squared += error * error; max = Math.Max(max, error);
            if (truth[i].ground == predicted[i].ground) ground++;
        }
        return new ValidationData { samples = count, rmsePixels = count == 0 ? 0 : Math.Sqrt(squared / count), maxErrorPixels = max,
            groundAgreement = count == 0 ? 0 : (double)ground / count,
            // A few-pixel landing-settle wobble is tolerable when the replay still
            // reproduces the native finish tick exactly.
            fidelity = max <= 1 ? "pixel-certified"
                : max <= 5 || nativeFinish > 0 && replay.FinishTick == nativeFinish && Math.Sqrt(squared / Math.Max(1, count)) <= 5 ? "close"
                : "screening-only", Truth = truth };
    }

    internal static List<(float x, float y, float vx, float vy, bool ground)> ParseAttemptTrace(string trace)
    {
        var raw = new List<(float x, float y, float vx, float vy)>();
        foreach (string sample in trace.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = sample.Split(',');
            if (values.Length < 7) continue;
            raw.Add((float.Parse(values[3], CultureInfo.InvariantCulture), float.Parse(values[4], CultureInfo.InvariantCulture),
                float.Parse(values[5], CultureInfo.InvariantCulture), float.Parse(values[6], CultureInfo.InvariantCulture)));
        }
        var result = new List<(float x, float y, float vx, float vy, bool ground)>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            bool ground = MathF.Abs(raw[i].vy + 25f) < .1f && (i == 0 || MathF.Abs(raw[i].y - raw[i - 1].y) < 1f);
            result.Add((raw[i].x, raw[i].y, raw[i].vx, raw[i].vy, ground));
        }
        return result;
    }

    static IEnumerable<int[]> EnumerateCartesian(int[] baseline, int[] variables, int[][] values)
    {
        int[] indices = new int[variables.Length];
        while (true)
        {
            int[] item = (int[])baseline.Clone();
            for (int i = 0; i < variables.Length; i++) item[variables[i]] = values[i][indices[i]];
            yield return item;
            int position = indices.Length - 1;
            while (position >= 0 && ++indices[position] == values[position].Length) { indices[position] = 0; position--; }
            if (position < 0) yield break;
        }
    }

    static long Product(IEnumerable<long> values, long cap)
    {
        long result = 1;
        foreach (long value in values) { if (result > cap / value) return cap; result *= value; }
        return result;
    }

    static IOrderedEnumerable<Candidate> Rank(IEnumerable<Candidate> candidates, int[] baseline, float[] baselineX,
        int requiredLandingMask, int requiredWallJumps) => candidates
        .OrderBy(c => c.Result.Finished ? 0 : 1)
        .ThenBy(c => c.Result.Finished ? c.Result.CourseTicks : int.MaxValue)
        .ThenByDescending(c => c.Progress)
        .ThenBy(c => (c.Result.LandingMask & requiredLandingMask) == requiredLandingMask ? 0 : 1)
        .ThenBy(c => c.Result.WallJumpCount == requiredWallJumps ? 0 : 1)
        .ThenBy(c => c.Result.Finished ? c.ChangeCost(baseline, baselineX) : int.MaxValue)
        .ThenByDescending(c => c.Result.LandingMask)
        .ThenByDescending(c => c.Result.PeakY)
        .ThenBy(c => c.Signature, StringComparer.Ordinal);

    // Strips a whole jump: its press step plus the held/release steps that belong to it.
    static bool[] DropJump(List<Loader.RouteStep> steps, bool[] noJump, int press)
    {
        bool[] result = (bool[])noJump.Clone();
        result[press] = true;
        for (int i = press + 1; i < steps.Count && !steps[i].Press && (steps[i].Held || steps[i].Release); i++)
        {
            result[i] = true;
            if (steps[i].Release) break;
        }
        return result;
    }

    // ponytail: greedy forward match within 100 ticks; a detour longer than that reads as no progress.
    static int Progress(List<(float x, float y, float vx, float vy, bool ground)> trace, int alive,
        List<(float x, float y, float vx, float vy, bool ground)> reference)
    {
        int reached = 0;
        for (int i = 0; i < alive; i++)
            for (int k = Math.Min(reference.Count - 1, reached + 100); k > reached; k--)
            {
                float dx = trace[i].x - reference[k].x, dy = trace[i].y - reference[k].y;
                if (dx * dx + dy * dy < 48f * 48f) { reached = k; break; }
            }
        return reached;
    }

    static InputFrame[] BuildFrames(List<Loader.RouteStep> steps, int[] durations, int hopStep, int hopTick, int hopHold,
        float[]? rawMoveX = null, bool[]? noJump = null)
    {
        var frames = new List<InputFrame>(durations.Sum() + 80);
        for (int step = 0; step < steps.Count; step++)
            for (int tick = 0; tick < durations[step]; tick++)
            {
                bool jump = noJump == null || !noJump[step];
                bool press = jump && tick == 0 && steps[step].Press;
                bool release = jump && tick == 0 && steps[step].Release;
                bool held = jump && steps[step].Held;
                if (step == hopStep && tick == hopTick) press = true;
                if (step == hopStep && tick >= hopTick && tick < hopTick + hopHold) held = true;
                if (step == hopStep && tick == hopTick + hopHold) release = true;
                frames.Add(new InputFrame(rawMoveX == null ? steps[step].X : rawMoveX[step], press, held, release,
                    tick == 0 && steps[step].DashPress));
            }
        return frames.ToArray();
    }

    static double Clearance(List<(float x, float y, float vx, float vy, bool ground)> trace, Surface[] surfaces)
    {
        double minimum = double.PositiveInfinity;
        foreach (var point in trace)
            foreach (Surface surface in surfaces)
            {
                double dx = point.x < surface.X1 ? surface.X1 - point.x : point.x > surface.X2 ? point.x - surface.X2 : 0;
                double dy = Math.Abs((point.y - 32) - surface.Y);
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > .01) minimum = Math.Min(minimum, distance);
            }
        return double.IsFinite(minimum) ? minimum : 0;
    }

    static Gate[] LoadHazardBounds(List<Shape> geometry, Gate viewport)
    {
        var result = new List<Gate>();
        foreach (Shape shape in geometry.Where(shape => shape.Trigger && shape.Name.Contains("Spike", StringComparison.OrdinalIgnoreCase)
            || shape.Name.Contains("BallKiller", StringComparison.OrdinalIgnoreCase)))
        {
            if (shape.Points.Length >= 6 && shape.PathStarts.Length > 0)
            {
                int[] ends = shape.PathStarts.Append(shape.Points.Length / 2).ToArray();
                for (int path = 0; path < shape.PathStarts.Length; path++)
                {
                    var indices = Enumerable.Range(shape.PathStarts[path], ends[path + 1] - shape.PathStarts[path]).ToArray();
                    var gate = new Gate(indices.Min(i => shape.Points[i * 2]), indices.Max(i => shape.Points[i * 2]),
                        indices.Min(i => shape.Points[i * 2 + 1]), indices.Max(i => shape.Points[i * 2 + 1]));
                    if (gate.MaxX >= viewport.MinX && gate.MinX <= viewport.MaxX
                        && gate.MaxY >= viewport.MinY && gate.MinY <= viewport.MaxY) result.Add(gate);
                }
            }
            else
            {
                var gate = new Gate(shape.CenterX - shape.SizeX / 2, shape.CenterX + shape.SizeX / 2,
                    shape.CenterY - shape.SizeY / 2, shape.CenterY + shape.SizeY / 2);
                if (gate.MaxX >= viewport.MinX && gate.MinX <= viewport.MaxX
                    && gate.MaxY >= viewport.MinY && gate.MinY <= viewport.MaxY) result.Add(gate);
            }
        }
        return result.ToArray();
    }

    static double HazardClearance(List<(float x, float y, float vx, float vy, bool ground)> trace, Gate[] hazards)
    {
        double minimum = trace.Count == 0 ? double.PositiveInfinity : trace.Min(point => PointHazardClearance(point, hazards));
        return double.IsFinite(minimum) ? minimum : -1;
    }

    // First trace index whose body box comes within floorPx of a spike AABB, or -1.
    internal static int FirstHazardTick(string modelPath, Gate viewport,
        List<(float x, float y, float vx, float vy, bool ground)> trace, double floorPx)
    {
        Gate[] hazards = LoadHazardBounds(LoadGeometry(modelPath), viewport);
        return trace.FindIndex(point => PointHazardClearance(point, hazards) < floorPx);
    }

    static double PointHazardClearance((float x, float y, float vx, float vy, bool ground) point, Gate[] hazards)
    {
        double minimum = double.PositiveInfinity;
        float lowY = point.y - (point.ground ? 34f : 32f);
        float highY = point.y + (point.ground ? 38f : 22f);
        foreach (Gate hazard in hazards)
        {
            double dx = Math.Max(hazard.MinX - (point.x + 20.5f), point.x - 20.5f - hazard.MaxX);
            double dy = Math.Max(hazard.MinY - highY, lowY - hazard.MaxY);
            double clearance = dx <= 0 && dy <= 0 ? Math.Max(dx, dy)
                : Math.Sqrt(Math.Max(0, dx) * Math.Max(0, dx) + Math.Max(0, dy) * Math.Max(0, dy));
            minimum = Math.Min(minimum, clearance);
        }
        return minimum;
    }

    static List<Shape> LoadGeometry(string modelPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(modelPath));
        var result = new List<Shape>();
        foreach (JsonElement item in document.RootElement.GetProperty("colliders").EnumerateArray())
        {
            float[] points = item.GetProperty("points").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            int[] starts = item.GetProperty("pathStarts").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            result.Add(new Shape(item.GetProperty("name").GetString() ?? "",
                item.GetProperty("trigger").GetBoolean(),
                item.GetProperty("centerX").GetSingle(), item.GetProperty("centerY").GetSingle(),
                item.GetProperty("sizeX").GetSingle(), item.GetProperty("sizeY").GetSingle(), points, starts));
        }
        return result;
    }

    static string RouteId(string routesPath, string routeName)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(routesPath));
        foreach (JsonElement macro in document.RootElement.GetProperty("macros").EnumerateArray())
            if (string.Equals(macro.GetProperty("name").GetString(), routeName, StringComparison.Ordinal))
                return macro.GetProperty("id").GetString() ?? "";
        return "";
    }

    static void WriteArtifacts(Options options, List<Loader.RouteStep> steps, int[] baseline, float[] baselineX, Candidate baselineCandidate,
        Candidate[] top, SimulationEnvironment environment, List<Shape> geometry, ValidationData? validation,
        bool exhaustive, long theoretical, long simulations, int unique)
    {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var summary = new
        {
            schemaVersion = 1, createdUtc = DateTime.UtcNow, route = options.Route,
            coverage = new { mode = exhaustive ? "exhaustive" : "bounded-deterministic", radius = options.Radius,
                theoreticalCombinations = theoretical, simulations, uniqueCandidates = unique, targetEvaluationBudget = options.MaxEvaluations },
            validation, baseline = ResultObject(baselineCandidate, baseline, baselineX, baselineCandidate.Result.LandingMask, baselineCandidate.Result.WallJumpCount),
            candidates = top.Select((candidate, index) => new { rank = index + 1, candidate = ResultObject(candidate, baseline, baselineX, baselineCandidate.Result.LandingMask, baselineCandidate.Result.WallJumpCount) })
        };
        File.WriteAllText(Path.Combine(options.Output, "summary.json"), JsonSerializer.Serialize(summary, jsonOptions));
        for (int i = 0; i < top.Length; i++)
        {
            Candidate candidate = top[i];
            var import = new { routeId = RouteId(options.Routes, options.Route), name = $"{options.Route} [offline #{i + 1}]",
                durations = candidate.Durations, rawMoveX = candidate.RawMoveX, noJump = candidate.NoJump,
                hopStep = candidate.HopStep, hopTick = candidate.HopTick, hopHold = candidate.HopHold };
            File.WriteAllText(Path.Combine(options.Output, $"candidate-{i + 1:00}.json"), JsonSerializer.Serialize(import, jsonOptions));
        }
        File.WriteAllText(Path.Combine(options.Output, "report.html"), Html(options, steps, baseline, baselineX, baselineCandidate, top, environment, geometry, validation, exhaustive, theoretical, simulations));
    }

    static object ResultObject(Candidate candidate, int[] baseline, float[] baselineX, int requiredLandingMask, int requiredWallJumps) => new
    {
        finished = candidate.Result.Finished, courseTicks = candidate.Result.Finished ? candidate.Result.CourseTicks : -1,
        timeSeconds = candidate.Result.Finished ? candidate.Result.CourseTicks * .02 : -1,
        durations = candidate.Durations, rawMoveX = candidate.RawMoveX,
        noJump = candidate.NoJump, shapeChange = candidate.ShapeChange(baselineX),
        hopStep = candidate.HopStep, hopTick = candidate.HopTick, hopHold = candidate.HopHold,
        changeCost = candidate.ChangeCost(baseline, baselineX), peakY = candidate.Result.PeakY,
        landingMask = candidate.Result.LandingMask, wallJumpCount = candidate.Result.WallJumpCount,
        topologyValid = candidate.Result.Finished
            && (candidate.Result.LandingMask & requiredLandingMask) == requiredLandingMask
            && candidate.Result.WallJumpCount == requiredWallJumps,
        minimumGeometryDistancePixels = double.IsFinite(candidate.Clearance) ? candidate.Clearance : -1,
        minimumHazardAabbClearancePixels = double.IsFinite(candidate.HazardClearance) ? candidate.HazardClearance : -1
    };

    static string Html(Options options, List<Loader.RouteStep> steps, int[] baseline, float[] baselineX, Candidate baselineCandidate,
        Candidate[] top, SimulationEnvironment env, List<Shape> geometry, ValidationData? validation, bool exhaustive, long theoretical, long simulations)
    {
        IEnumerable<(float x, float y)> points = top.SelectMany(c => c.Trace ?? []).Select(p => (p.x, p.y))
            .Concat(validation?.Truth.Select(p => (p.x, p.y)) ?? []);
        float minX = Math.Min(env.start.MinX, points.Any() ? points.Min(p => p.x) : env.start.MinX) - 100;
        float maxX = Math.Max(env.finish.MaxX, points.Any() ? points.Max(p => p.x) : env.finish.MaxX) + 100;
        float minY = Math.Min(env.start.MinY, points.Any() ? points.Min(p => p.y) : env.start.MinY) - 100;
        float maxY = Math.Max(env.finish.MaxY, points.Any() ? points.Max(p => p.y) : env.finish.MaxY) + 100;
        string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string Tx(float x) => F(x - minX); string Ty(float y) => F(maxY - y);
        var svg = new StringBuilder();
        foreach (Shape shape in geometry.Where(s => s.CenterX + s.SizeX / 2 >= minX && s.CenterX - s.SizeX / 2 <= maxX
            && s.CenterY + s.SizeY / 2 >= minY && s.CenterY - s.SizeY / 2 <= maxY))
        {
            string css = shape.Trigger ? "tile trigger" : "tile";
            if (shape.Points.Length >= 4 && shape.PathStarts.Length > 0)
            {
                int[] ends = shape.PathStarts.Append(shape.Points.Length / 2).ToArray();
                for (int path = 0; path < shape.PathStarts.Length; path++)
                {
                    string polygon = string.Join(' ', Enumerable.Range(shape.PathStarts[path], ends[path + 1] - shape.PathStarts[path])
                        .Select(i => Tx(shape.Points[i * 2]) + "," + Ty(shape.Points[i * 2 + 1])));
                    svg.Append($"<polygon class='{css}' points='{polygon}'><title>{WebUtility.HtmlEncode(shape.Name)}</title></polygon>");
                }
            }
            else
                svg.Append($"<rect class='{css}' x='{Tx(shape.CenterX-shape.SizeX/2)}' y='{Ty(shape.CenterY+shape.SizeY/2)}' width='{F(shape.SizeX)}' height='{F(shape.SizeY)}'><title>{WebUtility.HtmlEncode(shape.Name)}</title></rect>");
        }
        void GateRect(Gate g, string css) => svg.Append($"<rect class='{css}' x='{Tx(g.MinX)}' y='{Ty(g.MaxY)}' width='{F(g.MaxX-g.MinX)}' height='{F(g.MaxY-g.MinY)}'/>");
        GateRect(env.start, "start"); GateRect(env.finish, "finish");
        if (validation?.Truth.Count > 0)
        {
            string authoritative = string.Join(' ', validation.Truth.Select((p, n) => (n == 0 ? "M" : "L") + Tx(p.x) + "," + Ty(p.y)));
            svg.Append($"<path class='authoritative' d='{authoritative}'/>");
        }
        string[] colors = ["#56d6ff", "#ffcc66", "#ff6b9d", "#9cff7a", "#c69cff", "#ff936b"];
        for (int i = top.Length - 1; i >= 0; i--)
        {
            Candidate c = top[i]; string color = colors[i % colors.Length];
            string path = string.Join(' ', (c.Trace ?? []).Select((p, n) => (n == 0 ? "M" : "L") + Tx(p.x) + "," + Ty(p.y)));
            svg.Append($"<path class='trace' style='stroke:{color}' d='{path}'/><text x='{Tx(c.Trace?.Last().x ?? 0)}' y='{Ty(c.Trace?.Last().y ?? 0)}' fill='{color}'>#{i+1}</text>");
            if (i == 0 && c.Trace != null)
                foreach (var p in c.Trace.Select((p, n) => (p, n)).Where(v => v.n % 5 == 0))
                    svg.Append($"<rect class='player' x='{F(p.p.x-minX-20.5f)}' y='{F(maxY-(p.p.y+36))}' width='41' height='64'/>");
        }
        string rows = string.Join("", top.Select((c, i) => $"<tr><td>#{i+1}</td><td>{(c.Result.Finished ? c.Result.CourseTicks + " / " + (c.Result.CourseTicks*.02).ToString("0.000")+"s" : "miss")}</td><td>{c.ChangeCost(baseline, baselineX)}</td><td>{WebUtility.HtmlEncode(c.ShapeChange(baselineX))}</td><td>{c.Clearance:0.###}</td><td><code>{string.Join(',', c.Durations)}</code></td></tr>"));
        string fidelity = validation == null ? "No recording supplied; fidelity is unmeasured." : WebUtility.HtmlEncode(JsonSerializer.Serialize(validation));
        return $$"""
<!doctype html><html><head><meta charset="utf-8"><title>IGTAP offline optimizer</title><style>
body{background:#07111f;color:#dcecff;font:14px system-ui;margin:0;padding:24px}h1,h2{margin:.3em 0}.card{background:#0d1d31;border:1px solid #243b56;border-radius:12px;padding:18px;margin:14px 0}.warn{color:#ffcc66}svg{width:100%;height:auto;background:#071727;border-radius:8px}.tile{fill:#263c50;stroke:#7894ad;stroke-width:1}.tile.trigger{fill:#cb86ff0c;stroke:#cb86ff55;stroke-dasharray:5 3}.start{fill:#32d58355;stroke:#32d583}.finish{fill:#ff6b9d55;stroke:#ff6b9d}.trace{fill:none;stroke-width:2}.authoritative{fill:none;stroke:#fff;stroke-width:2;stroke-dasharray:7 4}.player{fill:#56d6ff18;stroke:#56d6ff66;stroke-width:.7}table{border-collapse:collapse;width:100%}td,th{padding:8px;border-bottom:1px solid #243b56;text-align:left}code{font-size:11px;color:#a9d9ff}.muted{color:#8da4ba}
</style></head><body><h1>{{WebUtility.HtmlEncode(options.Route)}} — offline search</h1>
<div class="card"><b>Coverage:</b> {{(exhaustive ? "complete Cartesian enumeration" : "bounded deterministic suite")}} · {{simulations:N0}} simulations · {{theoretical:N0}} timing combinations. <span class="warn">{{fidelity}}</span></div>
<div class="card"><h2>Map and candidate paths</h2><p class="muted">All exported colliders in the route viewport; colored simulated paths; dashed white authoritative in-game path; rank #1 body box every 5 physics frames. Start is green, finish pink, triggers are dashed. One SVG unit is one game pixel.</p><svg viewBox="0 0 {{F(maxX-minX)}} {{F(maxY-minY)}}">{{svg}}</svg></div>
<div class="card"><h2>Shortlist</h2><table><thead><tr><th>Rank</th><th>Frames / time</th><th>Edit cost</th><th>Route change</th><th>Nearest geometry px</th><th>Durations</th></tr></thead><tbody>{{rows}}</tbody></table></div>
<div class="card"><h2>Confirmation contract</h2><p>Candidate JSON files are import payloads for <code>/api/simulation/import-candidate</code>. Offline results are screening predictions until recording validation reaches pixel-certified fidelity; final time and reliability always come from repeated in-game trials.</p></div></body></html>
""";
    }

    public static int SelfTest()
    {
        try
        {
            if (Product([5, 5, 5], 1000) != 125) throw new Exception("Cartesian product failed.");
            int[] baseline = [2, 3]; int[] vars = [0, 1]; int[][] values = [[1, 2], [3, 4, 5]];
            if (EnumerateCartesian(baseline, vars, values).Count() != 6) throw new Exception("Cartesian enumeration failed.");
            var steps = new List<Loader.RouteStep> { new("test", 4, 1, false, false, false) };
            InputFrame[] frames = BuildFrames(steps, [4], 0, 1, 2);
            if (!frames[1].Press || !frames[1].Held || !frames[2].Held || !frames[3].Release) throw new Exception("Jump insertion failed.");
            var dashSteps = new List<Loader.RouteStep> { new("dash", 2, 1, false, false, false, true) };
            InputFrame[] dashFrames = BuildFrames(dashSteps, [2], -1, -1, 0);
            if (!dashFrames[0].DashPress || dashFrames[1].DashPress) throw new Exception("Dash edge expansion failed.");
            var jumpSteps = new List<Loader.RouteStep> { new("run", 2, 1, false, false, false), new("jump", 3, 1, true, true, false),
                new("release", 1, 1, false, false, true), new("run", 2, 1, false, false, false) };
            bool[] dropped = DropJump(jumpSteps, new bool[4], 1);
            if (!dropped.SequenceEqual([false, true, true, false])) throw new Exception("Jump removal did not strip the whole jump.");
            if (BuildFrames(jumpSteps, [2, 3, 1, 2], -1, -1, 0, null, dropped).Any(f => f.Press || f.Held || f.Release))
                throw new Exception("Removed jump still emits jump input.");
            SimResult Finish(int ticks, int wallJumps) => new(true, 0, ticks, 0, 0, 0, 0, wallJumps);
            var seedShape = new Candidate([1], [1f], [false], -1, -1, 0, Finish(900, 14), 0);
            var newShape = new Candidate([1], [0f], [true], -1, -1, 0, Finish(880, 13), 0);
            if (Rank([seedShape, newShape], [1], [1f], 0, 14).First() != newShape)
                throw new Exception("A faster route with a different shape must outrank the seed shape.");
            var emptyGate = new Gate(-10000, -9999, -10000, -9999);
            var lockedSimulator = new FastSimulator([], [], [], emptyGate, emptyGate);
            SimState lockedState = lockedSimulator.CreateState();
            lockedSimulator.Advance(lockedState, dashFrames[0]);
            if (lockedState.DashActive) throw new Exception("Locked dash was activated.");
            var unlockedPhysics = PhysicsConfig.DemoDefaults with { DashUnlocked = true, MaxAirDashes = 1, InitialAirDashes = 1 };
            var unlockedSimulator = new FastSimulator([], [], [], emptyGate, emptyGate, unlockedPhysics);
            SimState unlockedState = unlockedSimulator.CreateState();
            unlockedSimulator.Advance(unlockedState, dashFrames[0]);
            if (!unlockedState.DashActive || unlockedState.AirDashesLeft != 0 || unlockedState.VX != unlockedPhysics.DashSpeed)
                throw new Exception("Unlocked dash did not consume inventory and apply speed.");
            Console.WriteLine("All offline optimizer self-tests passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
    }
}
