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
        CheckPage(pageWidth, pageHeight, gutter);
        if (positions.Length < rectangles.Length)
        {
            throw new ArgumentException(
                $"There is room for {positions.Length} positions but {rectangles.Length} rectangles.",
                nameof(positions));
        }

        // Every rectangle takes its gutter along its right and bottom sides, and the page
        // gets the same extra, so rectangles at the page's right and bottom edges have
        // none to spare.
        var (widths, heights) = Padded(rectangles, gutter);
        var skyline = new Skyline(pageWidth + gutter, pageHeight + gutter, rectangles.Length);
        foreach (var index in TallestFirst(widths, heights))
        {
            var w = widths[index];
            var h = heights[index];
            if (!skyline.TryFind(w, h, out var x, out var y))
            {
                return false;
            }

            positions[index] = (x, y);
            skyline.Raise(x, w, y + h);
        }

        return true;
    }

    private static void CheckPage(int pageWidth, int pageHeight, int gutter)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            throw new ArgumentException($"The page must have an area, not {pageWidth}x{pageHeight}.");
        }

        if (gutter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gutter), gutter, "The gutter can't be negative.");
        }
    }

    // Each rectangle's width and height with the gutter added.
    private static (int[] Widths, int[] Heights) Padded(ReadOnlySpan<(int Width, int Height)> rectangles, int gutter)
    {
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

        return (widths, heights);
    }

    // The rectangles' indices, tallest first, then widest, then by index.
    private static int[] TallestFirst(int[] widths, int[] heights)
    {
        var order = new int[widths.Length];
        for (var i = 0; i < order.Length; i++)
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
        return order;
    }

    private readonly record struct Step(int X, int Y, int Width);

    /// <summary>The page's top edge so far, as steps from left to right, on a page of the given size.</summary>
    private sealed class Skyline(int width, int height, int rectangles)
    {
        private List<Step> _steps = new(rectangles + 2) { new(0, 0, width) };
        private List<Step> _next = new(rectangles + 2);

        /// <summary>The lowest, then leftmost, place a rectangle of <paramref name="w"/> by <paramref name="h"/> fits.</summary>
        public bool TryFind(int w, int h, out int bestX, out int bestY)
        {
            bestX = 0;
            bestY = int.MaxValue;
            for (var start = 0; start < _steps.Count; start++)
            {
                var x = _steps[start].X;
                if (x + w > width)
                {
                    break;
                }

                var y = RestingHeight(start, w);
                if (y + h <= height && y < bestY)
                {
                    bestX = x;
                    bestY = y;
                }
            }

            return bestY != int.MaxValue;
        }

        // A rectangle w wide from step start rests on the highest step it spans.
        private int RestingHeight(int start, int w)
        {
            var y = 0;
            var covered = 0;
            for (var step = start; covered < w; step++)
            {
                y = Math.Max(y, _steps[step].Y);
                covered += _steps[step].Width;
            }

            return y;
        }

        /// <summary>
        /// Puts a new step from <paramref name="x"/> across <paramref name="w"/> at <paramref name="y"/>, trimming the
        /// steps it covers and joining neighbours at the same height.
        /// </summary>
        public void Raise(int x, int w, int y)
        {
            _next.Clear();

            // The steps wholly left of the new one, then whatever of the next sticks out on its left.
            var i = 0;
            for (; i < _steps.Count && _steps[i].X + _steps[i].Width <= x; i++)
            {
                Add(_steps[i]);
            }

            if (i < _steps.Count && _steps[i].X < x)
            {
                Add(new Step(_steps[i].X, _steps[i].Y, x - _steps[i].X));
            }

            Add(new Step(x, y, w));

            // Steps wholly under the new one go; one sticking out on its right keeps that part, and the rest stay.
            var right = x + w;
            for (; i < _steps.Count; i++)
            {
                var step = _steps[i];
                var stepRight = step.X + step.Width;
                if (step.X >= right)
                {
                    Add(step);
                }
                else if (stepRight > right)
                {
                    Add(new Step(right, step.Y, stepRight - right));
                }
            }

            (_steps, _next) = (_next, _steps);
        }

        private void Add(Step step)
        {
            if (_next.Count > 0 && _next[^1].Y == step.Y)
            {
                var last = _next[^1];
                _next[^1] = last with { Width = last.Width + step.Width };
                return;
            }

            _next.Add(step);
        }
    }
}
