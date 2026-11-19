namespace Super;

/// <summary>
/// Hands the frames the renderer draws to the stats panel. The renderer is measured only while the panel shows, as the
/// measuring costs a little, and a profile measures for itself, so the panel leaves the renderer alone during one.
/// </summary>
internal sealed class RenderStatsFeed(SceneRenderer scene)
{
    /// <summary>The most frames a screenshot draws for the stats panel to measure.</summary>
    public const int MostScreenshotFrames = 600;

    /// <summary>Whether a profile runs, which sets the renderer's measuring itself.</summary>
    public bool Profiling { get; set; }

    /// <summary>Before a frame: whether it is measured for the panel, with the renderer's measuring set to match.</summary>
    public bool Begin(GameStats stats)
    {
        bool measured = stats.Shown && !Profiling;
        if (!Profiling)
        {
            scene.Renderer.Settings.MeasureGpu = measured;
        }

        return measured;
    }

    /// <summary>Hands the frame the renderer just drew to the stats panel.</summary>
    public void Add(GameStats stats, int width, int height)
    {
        var rendered = scene.Renderer.Stats;
        var triangles = rendered.DrawnTriangles;
        var tiles = rendered.ShadowTiles;
        var memory = rendered.GpuMemoryBytes;
        var counts = new RenderStats(
            0.0, 0.0, null, rendered.DrawCalls, triangles.Camera, triangles.Shadows, tiles.Drawn, tiles.Kept,
            memory.Used, memory.Reserved, width, height);
        stats.AddRender(rendered.GpuTimes, rendered.WaitMilliseconds, counts);
    }
}
