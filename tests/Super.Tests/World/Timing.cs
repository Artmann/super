using System.Diagnostics;

namespace Super.Tests;

/// <summary>Times two pieces of work against each other, for the benchmark tests in <see cref="TimingCollection"/>.</summary>
internal static class Timing
{
    /// <summary>
    /// The median milliseconds of each, timed turn about, so a busy machine, such as other test projects running
    /// alongside, slows both alike instead of whichever happened to be measured while it was busy.
    /// </summary>
    public static (double First, double Second) Medians(Action first, Action second, int frames = 200, int warmUp = 20)
    {
        for (int i = 0; i < warmUp; i++)
        {
            first();
            second();
        }

        var firstTimes = new double[frames];
        var secondTimes = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            firstTimes[i] = Time(first);
            secondTimes[i] = Time(second);
        }

        Array.Sort(firstTimes);
        Array.Sort(secondTimes);
        return (firstTimes[frames / 2], secondTimes[frames / 2]);
    }

    private static double Time(Action frame)
    {
        long start = Stopwatch.GetTimestamp();
        frame();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
