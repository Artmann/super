namespace Super.Materials.Tests;

/// <summary>Checks that every node can share.</summary>
internal static class NodeChecks
{
    /// <summary>The seam between the last and first column (and row) must be no rougher than the rest of the image.</summary>
    public static void Tiles(Func<Image> build, int size = 64)
    {
        var p = GraphRunner.Evaluate(build, size);

        double seamX = 0, insideX = 0, seamY = 0, insideY = 0;

        for (var y = 0; y < size; y++)
        {
            seamX += Math.Abs(p[y * size + size - 1] - p[y * size]);

            for (var x = 0; x < size - 1; x++)
            {
                insideX += Math.Abs(p[y * size + x + 1] - p[y * size + x]);
            }
        }

        for (var x = 0; x < size; x++)
        {
            seamY += Math.Abs(p[(size - 1) * size + x] - p[x]);

            for (var y = 0; y < size - 1; y++)
            {
                insideY += Math.Abs(p[(y + 1) * size + x] - p[y * size + x]);
            }
        }

        var meanSeamX = seamX / size;
        var meanInsideX = insideX / (size * (size - 1));
        var meanSeamY = seamY / size;
        var meanInsideY = insideY / (size * (size - 1));

        Assert.True(meanSeamX <= 3 * meanInsideX + 1e-4, $"Horizontal seam {meanSeamX} is rougher than the inside {meanInsideX}.");
        Assert.True(meanSeamY <= 3 * meanInsideY + 1e-4, $"Vertical seam {meanSeamY} is rougher than the inside {meanInsideY}.");
    }

    /// <summary>The large image, shrunk with a box filter, must look like the small one.</summary>
    public static void SameAtSizes(Func<Image> build, int small = 64, int large = 128, float tolerance = 0.03f)
    {
        var a = GraphRunner.Evaluate(build, small);
        var b = GraphRunner.Evaluate(build, large);
        var factor = large / small;
        double total = 0;

        for (var y = 0; y < small; y++)
        {
            for (var x = 0; x < small; x++)
            {
                double sum = 0;

                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++)
                    {
                        sum += b[(y * factor + dy) * large + x * factor + dx];
                    }
                }

                total += Math.Abs(a[y * small + x] - sum / (factor * factor));
            }
        }

        var mean = total / (small * small);

        Assert.True(mean < tolerance, $"The sizes differ by {mean} on average, over {tolerance}.");
    }
}
