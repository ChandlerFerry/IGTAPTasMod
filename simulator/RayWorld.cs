using System.Text.Json;

// Physics2D.Raycast over the exported collider geometry, per Unity layer.
// Movement's probes (scratch_movement.txt FixedUpdate) are defined against this.
sealed class RayWorld
{
    public const int GroundMask = 1 << 7, BlockingMask = 1 << 6;
    readonly record struct Segment(float AX, float AY, float BX, float BY, int Layer);
    readonly List<Segment> segments = [];
    // Uniform grid over segment bounding boxes; Movement's rays are at most 25 px long.
    const float Cell = 64f;
    readonly Dictionary<long, List<int>> grid = [];
    static long Key(int cx, int cy) => (long)cx << 32 | (uint)cy;

    // ponytail: process-wide ray world for the loaded map; every tool loads one model per process.
    public static RayWorld? Current { get; private set; }
    static string? currentPath;
    public static void Use(string modelPath)
    {
        if (currentPath != modelPath) { Current = Load(modelPath); currentPath = modelPath; }
    }

    public static RayWorld Load(string modelPath)
    {
        var world = new RayWorld();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(modelPath));
        foreach (JsonElement c in document.RootElement.GetProperty("colliders").EnumerateArray())
        {
            if (!c.TryGetProperty("layer", out JsonElement layerElement) || !c.TryGetProperty("points", out _)) continue;
            int layer = layerElement.GetInt32();
            if ((1 << layer & (GroundMask | BlockingMask)) == 0
                || c.TryGetProperty("enabled", out JsonElement enabled) && !enabled.GetBoolean()) continue;
            string type = c.GetProperty("colliderType").GetString() ?? "";
            float[] points = c.GetProperty("points").EnumerateArray().Select(p => p.GetSingle()).ToArray();
            int[] starts = c.GetProperty("pathStarts").EnumerateArray().Select(p => p.GetInt32()).ToArray();
            if (points.Length >= 6 && starts.Length > 0)
            {
                int[] ends = starts.Append(points.Length / 2).ToArray();
                for (int path = 0; path < starts.Length; path++)
                {
                    int n = ends[path + 1] - starts[path];
                    if (n < 3) continue;
                    world.Add(Enumerable.Range(starts[path], n).Select(i => points[i * 2]).ToArray(),
                        Enumerable.Range(starts[path], n).Select(i => points[i * 2 + 1]).ToArray(), layer);
                }
            }
            else if (type == "BoxCollider2D")
            {
                float cx = c.GetProperty("centerX").GetSingle(), cy = c.GetProperty("centerY").GetSingle();
                float hx = c.GetProperty("sizeX").GetSingle() / 2, hy = c.GetProperty("sizeY").GetSingle() / 2;
                float rad = c.GetProperty("rotation").GetSingle() * MathF.PI / 180f, cos = MathF.Cos(rad), sin = MathF.Sin(rad);
                float[] lx = [-hx, hx, hx, -hx], ly = [-hy, -hy, hy, hy];
                world.Add(lx.Select((x, i) => cx + x * cos - ly[i] * sin).ToArray(),
                    lx.Select((x, i) => cy + x * sin + ly[i] * cos).ToArray(), layer);
            }
        }
        return world;
    }

    void Add(float[] xs, float[] ys, int layer)
    {
        for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
        {
            int index = segments.Count;
            segments.Add(new Segment(xs[j], ys[j], xs[i], ys[i], layer));
            for (int cx = (int)MathF.Floor(Math.Min(xs[i], xs[j]) / Cell); cx <= (int)MathF.Floor(Math.Max(xs[i], xs[j]) / Cell); cx++)
                for (int cy = (int)MathF.Floor(Math.Min(ys[i], ys[j]) / Cell); cy <= (int)MathF.Floor(Math.Max(ys[i], ys[j]) / Cell); cy++)
                {
                    if (!grid.TryGetValue(Key(cx, cy), out List<int>? cell)) grid[Key(cx, cy)] = cell = [];
                    cell.Add(index);
                }
        }
    }

    // Distance to the first hit, or null. Non-positive length never hits.
    public float? Cast(float ox, float oy, float dx, float dy, float length, int mask)
    {
        if (length <= 0f) return null;
        float ex = ox + dx * length, ey = oy + dy * length;
        float lo = Math.Min(ox, ex), hi = Math.Max(ox, ex), bo = Math.Min(oy, ey), to = Math.Max(oy, ey);
        // Every collider behaves as its outline: a ray starting inside still only
        // hits where it crosses an edge (oracle: r2 from inside a spike hits its far edge).
        float? best = null;
        for (int cx = (int)MathF.Floor(lo / Cell); cx <= (int)MathF.Floor(hi / Cell); cx++)
            for (int cy = (int)MathF.Floor(bo / Cell); cy <= (int)MathF.Floor(to / Cell); cy++)
                if (grid.TryGetValue(Key(cx, cy), out List<int>? cell))
                    foreach (int index in cell)
                    {
                        Segment g = segments[index];
                        if ((1 << g.Layer & mask) == 0) continue;
                        float? t = Intersect(ox, oy, dx, dy, g.AX, g.AY, g.BX, g.BY);
                        if (t is float d && d <= length && (best == null || d < best)) best = d;
                    }
        return best;
    }

    static float? Intersect(float ox, float oy, float dx, float dy, float ax, float ay, float bx, float by)
    {
        float sx = bx - ax, sy = by - ay;
        float denominator = dx * sy - dy * sx;
        if (MathF.Abs(denominator) < 1e-9f) return null;
        float t = ((ax - ox) * sy - (ay - oy) * sx) / denominator;
        float u = ((ax - ox) * dy - (ay - oy) * dx) / denominator;
        return t >= 0f && u >= 0f && u <= 1f ? t : null;
    }

    // Movement's ground test (scratch_movement.txt 957): (r5 && !r2 && !r4) || (r1 && !r2)
    // || (r3 && !r4 && vy < 200). Returns the supporting ray's hit distance, or null.
    public float? Ground(float x, float y, bool facingRight, float vy)
    {
        float f = facingRight ? 1f : -1f, oy = y - 18f;
        float? Down(float ox, int mask) => Cast(x + ox * f, oy, 0, -1, 22, mask);
        float? r2 = Down(-9, BlockingMask);
        float? r4 = Down(21, BlockingMask);
        if (r2 == null && r4 == null && Down(6, GroundMask) is float r5) return r5;
        if (r2 == null && Down(-15, GroundMask) is float r1) return r1;
        if (r4 == null && vy < 200f && Down(25, GroundMask) is float r3) return r3;
        return null;
    }

    // Movement's step-up nudge (scratch_movement.txt 954): (r6 || r7) && !r8 && !r9. The
    // negative-length r8/r9 never hit (0 of 76,910 oracle ticks), so r6 || r7 decides.
    public bool StepUp(float x, float y, bool facingRight)
    {
        float f = facingRight ? 1f : -1f;
        return Cast(x - 15f * f, y - 18f, 0, -1, 5, GroundMask) != null || Cast(x + 25f * f, y - 18f, 0, -1, 5, GroundMask) != null;
    }

    // Movement.FixedUpdate probe set r1..r13 at a transform pose (scratch_movement.txt 905-953).
    public float?[] Probes(float x, float y, bool facingRight, float extra = 0f)
    {
        float f = facingRight ? 1f : -1f;
        (float, float) V(float ox, float oy) => (x + ox * f, y + oy);
        var v = V(-15, -18); var v2 = V(-9, -18); var v3 = V(25, -18); var v4 = V(21, -18);
        var v5 = V(6, -18); var v6 = V(15, 19); var v7 = V(15, -18);
        float? Down((float x, float y) o, float len, int mask) => Cast(o.x, o.y, 0, -1, len, mask);
        float? Side((float x, float y) o, int mask) => Cast(o.x, o.y, f, 0, 12f + extra, mask);
        return [Down(v, 22, GroundMask), Down(v2, 22, BlockingMask), Down(v3, 22, GroundMask), Down(v4, 22, BlockingMask),
            Down(v5, 22, GroundMask), Down(v, 5, GroundMask), Down(v3, 5, GroundMask), Down(v, -25, GroundMask), Down(v3, -25, GroundMask),
            Side(v7, GroundMask), Side(v6, GroundMask), Side(v7, BlockingMask), Side(v6, BlockingMask)];
    }

    // ray-validate <model.json> <diagnostics.json|dir>... : compare r1..r13 against oracle-recorded probes.
    public static int Validate(string[] args)
    {
        RayWorld world = Load(args[0]);
            var files = args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a, "*.diagnostics.json") : [a]).ToArray();
            int ticks = 0; int[] wrongHit = new int[13], wrongDistance = new int[13];
            var examples = new List<string>();
            foreach (string file in files)
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (JsonElement t in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
                {
                    ticks++;
                    float?[] mine = world.Probes(t.GetProperty("x").GetSingle(), t.GetProperty("y").GetSingle(), t.GetProperty("facingRight").GetBoolean());
                    for (int r = 0; r < 13; r++)
                    {
                        JsonElement native = t.GetProperty("r" + (r + 1));
                        float? truth = native.ValueKind == JsonValueKind.Object ? native.GetProperty("distance").GetSingle() : null;
                        if (truth.HasValue != mine[r].HasValue)
                        {
                            wrongHit[r]++;
                            if (examples.Count < 12) examples.Add($"{Path.GetFileName(file)[..24]} tick {t.GetProperty("tick").GetInt32()} r{r + 1}: game={truth?.ToString("0.###") ?? "miss"} mine={mine[r]?.ToString("0.###") ?? "miss"} pose=({t.GetProperty("x").GetSingle()},{t.GetProperty("y").GetSingle()}) fR={t.GetProperty("facingRight").GetBoolean()}");
                        }
                        else if (truth.HasValue && MathF.Abs(truth.Value - mine[r]!.Value) > .05f) wrongDistance[r]++;
                    }
                }
            }
        Console.WriteLine($"ticks={ticks} wrongHit=[{string.Join(',', wrongHit)}] wrongDistance=[{string.Join(',', wrongDistance)}]");
        foreach (string e in examples) Console.WriteLine("  " + e);
        return 0;
    }
}
