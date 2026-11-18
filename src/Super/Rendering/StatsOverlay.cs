using System.Globalization;
using Rendition;
using Super.Ui;

namespace Super;

/// <summary>
/// The engine's stats panel at the top right: the frame rate, where the frame's time goes on the CPU and the GPU, the
/// slowest GPU passes, the draw calls and triangles, and the memory. F3 shows and hides it. See "Showing how the game
/// runs" in docs/interfaces.md.
/// </summary>
internal static class StatsOverlay
{
    private const double Megabyte = 1024.0 * 1024.0;

    private static readonly Color Fill = Color.Hex("#000000", 0.72f);
    private static readonly Color Ink = Color.Hex("#E8E8E8");
    private static readonly Color Label = Color.Hex("#B4B4B4");

    /// <summary>One line of the panel: a label on the left and its value on the right.</summary>
    internal readonly record struct Row(string Label, string Value);

    /// <summary>
    /// The panel's lines for the stats. The renderer's lines appear once it was measured, and the GPU's times once it
    /// could time its passes. Every number is written the same way in every language.
    /// </summary>
    public static IReadOnlyList<Row> Rows(in FrameStats stats, string gpuName)
    {
        var rows = new List<Row>
        {
            new("Frame", Format($"{stats.FramesPerSecond:0} fps, {stats.FrameMilliseconds:0.0} ms")),
        };

        var render = stats.Render;
        if (render.Measured)
        {
            rows.Add(new Row("CPU", Format($"{stats.BusyMilliseconds:0.0} ms")));
            AddRenderer(rows, render);
        }

        rows.Add(new Row("Heap", Format($"{stats.HeapBytes / Megabyte:0.0} MB, {stats.AllocatedBytesPerFrame:N0} B a frame")));
        rows.Add(new Row("Collections", Format($"{stats.Collections:N0}")));
        if (render.Measured)
        {
            rows.Add(new Row(Format($"{render.Width}x{render.Height}"), gpuName));
        }

        return rows;
    }

    private static void AddRenderer(List<Row> rows, in RenderStats render)
    {
        if (render.GpuMilliseconds > 0.0)
        {
            rows.Add(new Row("GPU", Format($"{render.GpuMilliseconds:0.0} ms")));
            foreach (var pass in render.SlowestPasses!)
            {
                rows.Add(new Row("  " + pass.Name, Format($"{pass.Milliseconds:0.00} ms")));
            }
        }

        rows.Add(new Row("Draw calls", Format($"{render.DrawCalls:N0}")));
        rows.Add(new Row("Triangles", Triangles(render.CameraTriangles)));
        rows.Add(new Row("  Shadows", Triangles(render.ShadowTriangles)));
        if (render.ShadowTilesDrawn + render.ShadowTilesKept > 0)
        {
            rows.Add(new Row("Shadow tiles", Format($"{render.ShadowTilesDrawn} drawn, {render.ShadowTilesKept} kept")));
        }

        rows.Add(new Row("GPU memory", Format(
            $"{render.GpuMemoryUsedBytes / Megabyte:N0} of {render.GpuMemoryReservedBytes / Megabyte:N0} MB")));
    }

    /// <summary>The panel, which the pointer passes through.</summary>
    public static UiView Panel(in FrameStats stats, string gpuName)
    {
        var lines = new VStack(HorizontalAlignment.Leading, Spacing: 2f);
        foreach (var row in Rows(stats, gpuName))
        {
            lines.Add(new HStack(Spacing: 16f)
            {
                new Text(row.Label).Foreground(Label),
                new Spacer(),
                new Text(row.Value).Foreground(Ink).MonospacedDigit(),
            });
        }

        return lines
            .FontSize(14f)
            .Padding(12f, 8f)
            .Background(Fill)
            .CornerRadius(4f)
            .FixedSize()
            .Padding(12f)
            .Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopTrailing)
            .IgnoresPointer();
    }

    /// <summary>"5.66 M" for millions, "12.4 k" for thousands, and the plain count below that.</summary>
    internal static string Triangles(long count) => count switch
    {
        >= 1_000_000 => Format($"{count / 1_000_000.0:0.00} M"),
        >= 10_000 => Format($"{count / 1_000.0:0.0} k"),
        _ => Format($"{count:N0}"),
    };

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
