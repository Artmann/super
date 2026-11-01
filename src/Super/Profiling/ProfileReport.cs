using System.Globalization;
using System.Text;

namespace Super.Profiling;

/// <summary>One measured frame, in milliseconds.</summary>
internal readonly record struct ProfiledFrame(
    int Ticks, double Update, double Draw, double Render, double Waiting, long AllocatedBytes)
{
    public double Total => Update + Draw + Render;
}

/// <summary>What one model adds to a frame.</summary>
internal readonly record struct ModelCost(string Name, int Copies, long TrianglesEach)
{
    public long Triangles => Copies * TrianglesEach;
}

/// <summary>Everything a profile run measured.</summary>
internal sealed record ProfileRun(
    string Title,
    int Seed,
    long Tick,
    int Width,
    int Height,
    IReadOnlyList<ProfiledFrame> Frames,
    IReadOnlyDictionary<string, double> GpuMilliseconds,
    IReadOnlyList<string[]> Stacks,
    IReadOnlyList<ModelCost>? Models = null,
    (long Camera, long Shadows) DrawnTriangles = default);

/// <summary>Writes a profile as plain text, short enough to read in one go, by a person or an agent.</summary>
internal static class ProfileReport
{
    private const int FunctionsShown = 20;
    private const int StacksShown = 8;
    private const int StackDepthShown = 14;

    public static string Write(ProfileRun run)
    {
        var text = new StringBuilder();
        WriteSummary(text, run);
        WriteGpu(text, run);
        WriteModels(text, run.Models ?? [], run.DrawnTriangles);
        WriteFunctions(text, run.Stacks);
        WriteStacks(text, run.Stacks);
        return text.ToString();
    }

    /// <summary>Counts how many samples each function was running in, and was anywhere on the stack for.</summary>
    internal static List<(string Name, int Self, int Total)> Functions(IReadOnlyList<string[]> stacks)
    {
        var self = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();
        foreach (var stack in stacks)
        {
            self[stack[^1]] = self.GetValueOrDefault(stack[^1]) + 1;
            foreach (string name in stack.Distinct())
            {
                total[name] = total.GetValueOrDefault(name) + 1;
            }
        }

        return total
            .Select(pair => (pair.Key, self.GetValueOrDefault(pair.Key), pair.Value))
            .OrderByDescending(function => function.Item2)
            .ThenByDescending(function => function.Value)
            .ThenBy(function => function.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteSummary(StringBuilder text, ProfileRun run)
    {
        var frames = run.Frames;
        var sorted = frames.Select(frame => frame.Total).Order().ToArray();
        double total = sorted.Average();
        double waiting = frames.Average(frame => frame.Waiting);
        Formatted(text, $"{run.Title}: seed {run.Seed}, from tick {run.Tick:N0}, {run.Width}x{run.Height}, {frames.Count} frames");
        Line(text);
        Formatted(text, $"Frame: {total:0.0} ms on average ({1000.0 / total:0} fps), fastest {sorted[0]:0.0}, slowest {sorted[^1]:0.0}");
        Formatted(text, $"  Update    {frames.Average(frame => frame.Update),7:0.00} ms  ({frames.Sum(frame => frame.Ticks)} ticks in all)");
        Formatted(text, $"  Draw      {frames.Average(frame => frame.Draw),7:0.00} ms  (the game's Draw, and the interface)");
        Formatted(text, $"  Render    {frames.Average(frame => frame.Render),7:0.00} ms  (of which {waiting:0.00} ms waiting for the GPU and the display)");
        Formatted(text, $"  Allocated {frames.Average(frame => frame.AllocatedBytes),7:N0} bytes per frame");
        Line(text);
        WriteLimit(text, total - waiting, run.GpuMilliseconds.Values.Sum());
        Line(text);
    }

    /// <summary>
    /// Compares time spent working. Waiting says little, because a frame also waits for the
    /// display, even with nothing left to do.
    /// </summary>
    private static void WriteLimit(StringBuilder text, double cpu, double gpu)
    {
        if (gpu == 0.0)
        {
            Formatted(text, $"CPU busy {cpu:0.0} ms per frame. Without GPU times, there's no telling which side is the limit.");
            return;
        }

        string limit = gpu > cpu ? "GPU" : "CPU";
        Formatted(text, $"Limited by the {limit}: CPU busy {cpu:0.0} ms, GPU busy {gpu:0.0} ms per frame, enough for about {1000.0 / Math.Max(cpu, gpu):0} fps.");
    }

    private static void WriteGpu(StringBuilder text, ProfileRun run)
    {
        if (run.GpuMilliseconds.Count == 0)
        {
            Line(text, "GPU: this GPU can't time its passes.");
            Line(text);
            return;
        }

        double total = run.GpuMilliseconds.Values.Sum();
        Formatted(text, $"GPU: {total:0.00} ms per frame");
        foreach (var (name, milliseconds) in run.GpuMilliseconds.OrderByDescending(pass => pass.Value))
        {
            Formatted(text, $"  {name,-18} {milliseconds,7:0.00} ms  {milliseconds / total,4:0%}");
        }

        Line(text);
    }

    private static void WriteModels(StringBuilder text, IReadOnlyList<ModelCost> models, (long Camera, long Shadows) drawn)
    {
        if (models.Count == 0)
        {
            return;
        }

        long total = models.Sum(model => model.Triangles);
        Formatted(text, $"Scene: {models.Sum(model => model.Copies):N0} models, {total:N0} triangles at full detail");
        Formatted(text, $"Drawn after culling and detail: {drawn.Camera:N0} triangles for the camera, {drawn.Shadows:N0} for shadows");
        foreach (var model in models.OrderByDescending(model => model.Triangles))
        {
            double share = (double)model.Triangles / total;
            Formatted(text, $"  {share,4:0%}  {model.Copies,6:N0} x {model.TrianglesEach,10:N0}  {model.Name}");
        }

        Line(text);
    }

    private static void WriteFunctions(StringBuilder text, IReadOnlyList<string[]> stacks)
    {
        if (stacks.Count == 0)
        {
            Line(text, "CPU: no samples were collected.");
            return;
        }

        Formatted(text, $"CPU: {stacks.Count:N0} samples of the game's thread. Waiting counts as time, so waits for the GPU and the display show up, mostly as Renderer.Render.");
        Line(text, "Self is time in the function itself, and total includes what it calls.");
        Line(text);
        Line(text, "   Self   Total  Function");
        foreach (var (name, self, total) in Functions(stacks).Take(FunctionsShown))
        {
            Formatted(text, $"{Share(self, stacks.Count)}  {Share(total, stacks.Count)}  {name}");
        }

        Line(text);
    }

    private static void WriteStacks(StringBuilder text, IReadOnlyList<string[]> stacks)
    {
        var groups = stacks
            .GroupBy(stack => string.Join('\n', stack))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(StacksShown);
        foreach (var group in groups)
        {
            Formatted(text, $"{Share(group.Count(), stacks.Count).Trim()} of samples:");
            string[] stack = group.First();
            foreach (string name in stack.TakeLast(StackDepthShown))
            {
                Formatted(text, $"  {name}");
            }

            Line(text);
        }
    }

    private static string Share(int part, int whole) =>
        ((double)part / whole).ToString("0.0%", CultureInfo.InvariantCulture).PadLeft(6);

    // Not an overload of Line: a $"" string would pick the string one and use the machine's culture.
    private static void Formatted(StringBuilder text, FormattableString line) =>
        text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Line(StringBuilder text, string line = "") => text.Append(line).Append('\n');
}
