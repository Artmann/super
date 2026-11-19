using System.Diagnostics;
using System.Numerics;
using Rendition;
using Xunit.Abstractions;

namespace Super.Tests;

/// <summary>Timing runs alone, so other tests don't slow these frames down.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}

/// <summary>A world of static pieces costs about what a game paid for keeping them in view.Cached itself.</summary>
[Collection(TimingCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class StaticBenchmarkTests(ITestOutputHelper output)
{
    private const int Side = 224; // 224 x 224 is just over 50,000 pieces.
    private const int Frames = 200;
    private const int WarmUp = 20;

    [Fact]
    public void Fifty_thousand_static_pieces_describe_no_slower_than_Cached()
    {
        var world = new World();
        for (int x = 0; x < Side; x++)
        {
            for (int z = 0; z < Side; z++)
            {
                world.Spawn(Transform.At(new Vector3(x, 0f, z)), new Model(ModelRef.Cube), new Static());
            }
        }

        var worldView = new View();
        var cachedView = new View();
        var (worldMedian, cachedMedian) = Medians(() =>
        {
            worldView.Begin(0f);
            WorldFrame.Describe(worldView, world);
        }, () =>
        {
            cachedView.Begin(0f);
            cachedView.Cached(0, 1, 0, static (_, view) =>
            {
                for (int x = 0; x < Side; x++)
                {
                    for (int z = 0; z < Side; z++)
                    {
                        view.Model(x * Side + z, ModelRef.Cube, Matrix4x4.CreateTranslation(x, 0f, z), new ModelOptions { Static = true });
                    }
                }
            });
        });

        output.WriteLine($"World: {worldMedian:F3} ms a frame, view.Cached: {cachedMedian:F3} ms a frame, for {Side * Side} pieces.");
        Assert.Equal(Side * Side, worldView.Drawn.Length);
        Assert.True(worldMedian <= 1.5 * cachedMedian, $"The world took {worldMedian:F3} ms against view.Cached's {cachedMedian:F3} ms.");

        long before = GC.GetAllocatedBytesForCurrentThread();
        worldView.Begin(0f);
        WorldFrame.Describe(worldView, world);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Times the two frames turn about, so a busy machine, such as other test projects running alongside, slows both
    /// alike instead of whichever happened to be measured while it was busy.
    /// </summary>
    private static (double First, double Second) Medians(Action first, Action second)
    {
        for (int i = 0; i < WarmUp; i++)
        {
            first();
            second();
        }

        var firstTimes = new double[Frames];
        var secondTimes = new double[Frames];
        for (int i = 0; i < Frames; i++)
        {
            firstTimes[i] = Time(first);
            secondTimes[i] = Time(second);
        }

        Array.Sort(firstTimes);
        Array.Sort(secondTimes);
        return (firstTimes[Frames / 2], secondTimes[Frames / 2]);
    }

    private static double Time(Action frame)
    {
        long start = Stopwatch.GetTimestamp();
        frame();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
