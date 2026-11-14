using System.Numerics;
using Super.Materials;

namespace Courtyard.Tests;

/// <summary>The cobweb card: fine threads in the image's top-left corner, the rest cut out.</summary>
[Trait("Speed", "Slow")]
public sealed class CobwebTests
{
    private const int Size = 1024;

    private static readonly Lazy<MaterialImages> Cooked = new(() => GraphRunner.Run(new Cobweb(), Size));

    [Fact]
    public void The_web_is_mostly_clear_with_threads_through_it()
    {
        var web = Cooked.Value;
        int solid = web.Opacity.Count(value => value > 0.5f);

        // Threads a millimetre or two thick, a few metres of them in all, cover a few percent at most.
        Assert.InRange(solid, Size * Size / 200, Size * Size / 10);
    }

    [Fact]
    public void Nothing_hangs_beyond_the_frame_thread()
    {
        var web = Cooked.Value;

        // The frame thread runs from (0.9, 0.015) to (0.015, 0.86), so the whole bottom-right half of the card,
        // where a triangular card has no surface, is clear.
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if ((x + 0.5f) / Size + (y + 0.5f) / Size > 1f)
                {
                    Assert.True(web.Opacity[y * Size + x] < 0.01f, $"({x}, {y}) is past the frame but not clear.");
                }
            }
        }
    }

    [Fact]
    public void An_anchor_thread_runs_along_the_top()
    {
        var web = Cooked.Value;

        // Along the top, 1.5% in, from the corner to where the frame thread meets it, the anchor thread is solid on
        // its own row, and a few pixels below it, between the spokes, it's clear more often than not.
        int row = (int)(Cobweb.Inset * Size);
        int on = Enumerable.Range(Size / 10, Size * 7 / 10).Count(x => web.Opacity[row * Size + x] > 0.5f);
        int below = Enumerable.Range(Size / 10, Size * 7 / 10).Count(x => web.Opacity[(row + 6) * Size + x] > 0.5f);

        Assert.True(on > Size * 7 / 10 * 0.95f, $"Only {on} pixels of the anchor thread are solid.");
        Assert.True(below < on / 4, $"{below} pixels are solid just under the anchor thread.");
    }

    [Fact]
    public void The_threads_are_fine()
    {
        var web = Cooked.Value;

        // Across a row through the hub, every thread it crosses is a narrow run: a thread is under 2 mm thick, about
        // 4 pixels here, and a slanting one a little more. There are many of them: spokes, spiral and strays.
        int row = (int)(Cobweb.Hub.Y * Size) + 40;
        var runs = new List<int>();
        int run = 0;
        for (int x = 0; x < Size; x++)
        {
            if (web.Opacity[row * Size + x] > 0.5f)
            {
                run++;
            }
            else if (run > 0)
            {
                runs.Add(run);
                run = 0;
            }
        }

        Assert.True(runs.Count >= 10, $"The row crosses only {runs.Count} threads.");
        runs.Sort();
        Assert.True(runs[runs.Count / 2] <= 5, $"The middle thread is {runs[runs.Count / 2]} pixels wide.");
    }
}
