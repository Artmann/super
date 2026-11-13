namespace Rendition;

/// <summary>
/// Lays rectangles out on a page with a skyline: the page's top edge so far, as a row of
/// flat steps. Each rectangle goes where it sits lowest, then leftmost. Lightmap charts
/// and pages are packed with it.
/// </summary>
public static class SkylinePacker
{
    /// <summary>
    /// Finds a place for every rectangle on one page, or reports that they don't all fit.
    /// Rectangles are placed tallest first (ties by width, wider first, then by index, so
    /// the result is the same every run), each at the lowest then leftmost skyline spot it
    /// fits. <paramref name="gutter"/> texels are kept between any two rectangles and none
    /// at the page's edges. <paramref name="positions"/> gets each rectangle's top left
    /// corner, in the order of <paramref name="rectangles"/>, and is undefined when this
    /// returns false.
    /// </summary>
    public static bool TryPack(
        ReadOnlySpan<(int Width, int Height)> rectangles,
        int pageWidth,
        int pageHeight,
        int gutter,
        Span<(int X, int Y)> positions)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            throw new ArgumentException($"The page must have an area, not {pageWidth}x{pageHeight}.");
        }

        if (gutter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gutter), gutter, "The gutter can't be negative.");
        }

        if (positions.Length < rectangles.Length)
        {
            throw new ArgumentException(
                $"There is room for {positions.Length} positions but {rectangles.Length} rectangles.",
                nameof(positions));
        }

        // Every rectangle takes its gutter along its right and bottom sides, and the page
        // gets the same extra, so rectangles at the page's right and bottom edges have
        // none to spare.
        var count = rectangles.Length;
        var widths = new int[count];
        var heights = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (rectangles[i].Width <= 0 || rectangles[i].Height <= 0)
            {
                throw new ArgumentException(
                    $"Rectangle {i} must have an area, not {rectangles[i].Width}x{rectangles[i].Height}.",
                    nameof(rectangles));
            }

            widths[i] = rectangles[i].Width + gutter;
            heights[i] = rectangles[i].Height + gutter;
        }

        var width = pageWidth + gutter;
        var height = pageHeight + gutter;

        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            if (heights[a] != heights[b])
            {
                return heights[b].CompareTo(heights[a]);
            }

            if (widths[a] != widths[b])
            {
                return widths[b].CompareTo(widths[a]);
            }

            return a.CompareTo(b);
        });

        var skyline = new List<Step>(count + 2) { new(0, 0, width) };
        var next = new List<Step>(count + 2);

        foreach (var index in order)
        {
            var w = widths[index];
            var h = heights[index];

            var bestX = 0;
            var bestY = int.MaxValue;
            for (var start = 0; start < skyline.Count; start++)
            {
                var x = skyline[start].X;
                if (x + w > width)
                {
                    break;
                }

                // It rests on the highest step it spans.
                var y = 0;
                var covered = 0;
                for (var step = start; covered < w; step++)
                {
                    y = Math.Max(y, skyline[step].Y);
                    covered += skyline[step].Width;
                }

                if (y + h <= height && y < bestY)
                {
                    bestX = x;
                    bestY = y;
                }
            }

            if (bestY == int.MaxValue)
            {
                return false;
            }

            positions[index] = (bestX, bestY);
            Raise(skyline, next, bestX, w, bestY + h);
            (skyline, next) = (next, skyline);
        }

        return true;
    }

    /// <summary>
    /// Writes into <paramref name="result"/> the skyline with a new step from
    /// <paramref name="x"/> across <paramref name="width"/> at <paramref name="y"/>,
    /// trimming the steps it covers and joining neighbours at the same height.
    /// </summary>
    private static void Raise(List<Step> skyline, List<Step> result, int x, int width, int y)
    {
        result.Clear();

        var right = x + width;
        var placed = false;

        foreach (var step in skyline)
        {
            var stepRight = step.X + step.Width;
            if (stepRight <= x)
            {
                Add(result, step);
                continue;
            }

            if (step.X >= right)
            {
                if (!placed)
                {
                    Add(result, new Step(x, y, width));
                    placed = true;
                }

                Add(result, step);
                continue;
            }

            // It overlaps the new step, so keep whatever sticks out on either side.
            if (step.X < x)
            {
                Add(result, new Step(step.X, step.Y, x - step.X));
            }

            if (!placed)
            {
                Add(result, new Step(x, y, width));
                placed = true;
            }

            if (stepRight > right)
            {
                Add(result, new Step(right, step.Y, stepRight - right));
            }
        }

        if (!placed)
        {
            Add(result, new Step(x, y, width));
        }
    }

    private static void Add(List<Step> steps, Step step)
    {
        if (steps.Count > 0 && steps[^1].Y == step.Y)
        {
            var last = steps[^1];
            steps[^1] = last with { Width = last.Width + step.Width };
            return;
        }

        steps.Add(step);
    }

    private readonly record struct Step(int X, int Y, int Width);
}
